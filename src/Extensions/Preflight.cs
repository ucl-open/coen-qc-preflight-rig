using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;

// What only the preflight does, beside the lab's pieces (the rig list) and its check (OpenRigs.Devices PreflightChecks):
// never a device opened here (Pip, 16 Nov 2026): the speakers' test sounds counted; headless Bonsai ended after a
// stop. C# 5: no interpolation, no ?. and no property initialisers. Bonsai measures: nothing here judges a device; coen_qc_preflight_rig.judge does.

static class Preflight
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
}

/// <summary>
/// The evidence that the speakers were played to (Pip, 15 Nov 2026: evidence plus person): each sound the mixer is
/// sent (SpeakerMixer's MixerStimulusSounds, past its CheckMixerStarted, on its way to AppendBuffer) counted for every
/// rig.yml speaker whose channel carries sound in it, one line on stderr each. Gives the counts at once with every
/// speaker at 0, then after each sound; into PreflightChecks' SpeakerSounds.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Counts the sounds sent to the mixer for each rig.yml speaker, by the channels that carry sound; one stderr line each. Inputs: the mixer's sounds, RigConfigs.")]
public class CountSpeakerSounds
{
    /// <summary>The counts, at once with every speaker at 0, then after each sound with sound on a speaker's channel.</summary>
    public IObservable<SpeakerSounds> Process(IObservable<OpenCV.Net.Mat> sounds, IObservable<LabRig> rig)
    {
        return Observable.Create<SpeakerSounds>(observer =>
        {
            var gate = new object();
            string[] names = null;
            int[] channels = null, counts = null;
            var listed = rig.Take(1).Subscribe(r =>
            {
                var speakers = r.SpeakerArray == null || r.SpeakerArray.Speakers == null ? new Dictionary<string, Speaker>() : r.SpeakerArray.Speakers;
                SpeakerSounds first;
                lock (gate)
                {
                    names = speakers.Keys.ToArray();
                    channels = speakers.Values.Select(s => s == null ? -1 : s.Channel).ToArray();
                    counts = new int[names.Length];
                    first = Snapshot(names, counts);
                }
                observer.OnNext(first);
            }, observer.OnError);
            var heard = sounds.Subscribe(sound =>
            {
                SpeakerSounds now;
                lock (gate)
                {
                    if (names == null || sound == null) return;
                    var any = false;
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (channels[i] < 0 || channels[i] >= sound.Rows) continue;
                        using (var row = sound.GetRow(channels[i]))
                        {
                            if (OpenCV.Net.CV.CountNonZero(row) == 0) continue;
                        }
                        counts[i]++;
                        any = true;
                        Console.Error.WriteLine("preflight: SpeakerArray " + names[i] + " test sound " + counts[i].ToString(Preflight.Invariant) + " sent to the mixer");
                    }
                    if (!any) return;
                    now = Snapshot(names, counts);
                }
                observer.OnNext(now);
            }, observer.OnError);
            return new CompositeDisposable(listed, heard);
        });
    }

    static SpeakerSounds Snapshot(string[] names, int[] counts)
    {
        return new SpeakerSounds { Names = (string[])names.Clone(), Counts = (int[])counts.Clone() };
    }
}

/// <summary>
/// Ends headless Bonsai when it is still running GraceSeconds after the controller's stop, or after the workflow began
/// to end for any other reason. Headless Bonsai returns from Main as soon as its runner has the stop's error, and the
/// process then lasts while any foreground thread does: here the live view's two publishers, which end only once
/// their subscriptions are disposed, last of all. A disposal held up anywhere before them kept Bonsai running until
/// the agent killed it 30 s after End Testing's stop (GREEN-DOME-2, 15 Nov 2026). In the editor it does nothing.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Source)]
[Description("Ends headless Bonsai if it is still running GraceSeconds after the controller's stop (or after the workflow began to end); nothing in the editor.")]
public class EndAfterStop
{
    double graceSeconds = 2;

    /// <summary>Seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the agent's kill.</summary>
    [Description("Seconds the workflow's own teardown is given after the stop; 0 or less leaves it to the agent's kill.")]
    public double GraceSeconds { get { return graceSeconds; } set { graceSeconds = value; } }

    /// <summary>Never gives anything; watches for the stop from a background thread.</summary>
    public IObservable<string> Process()
    {
        return Observable.Create<string>(observer =>
        {
            var grace = GraceSeconds;
            if (grace <= 0 || !Environment.GetCommandLineArgs().Contains("--no-editor")) return Disposable.Empty;
            var ending = new ManualResetEventSlim(false);
            var watch = new Thread(() => Watch(ending, TimeSpan.FromSeconds(grace)));
            watch.IsBackground = true;
            watch.Name = "Preflight EndAfterStop";
            watch.Start();
            // Disposed means the workflow is ending: the watch goes on, since a teardown held up is what it is for.
            return Disposable.Create(ending.Set);
        });
    }

    static void Watch(ManualResetEventSlim ending, TimeSpan grace)
    {
        string why = null;
        OpenRigs.Control.ControlState control = null;
        while (why == null)
        {
            var ended = ending.Wait(100);
            // Kept once seen: the control server forgets its state as it is disposed.
            control = OpenRigs.Control.ControlState.Current ?? control;
            var stop = control == null ? null : control.StopReason;
            if (stop != null) why = "the stop (" + stop + ")";
            else if (ended) why = "the workflow began to end";
        }
        Thread.Sleep(grace);
        Console.Error.WriteLine("preflight: Bonsai was still running " + grace.TotalSeconds.ToString(Preflight.Invariant) + " s after " + why +
                                ", its teardown held up, so EndAfterStop ends it");
        var kill = new Thread(() =>
        {
            Thread.Sleep(3000);
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        });
        kill.IsBackground = true;
        kill.Start();
        Environment.Exit(0);
    }
}
