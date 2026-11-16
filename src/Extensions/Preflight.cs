using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Bonsai;
using Bonsai.Harp;
using OpenRigs.Core;
using OpenRigs.Devices;
using OpenRigs.Devices.Rigs;

// What only the preflight does, beside the lab's pieces (the rig list) and its check (OpenRigs.Devices PreflightChecks):
// a person's Pulse, Reset, Move and Home, written into the pieces' own command subjects, never a device opened here
// (Pip, 16 Nov 2026); the speakers' test sounds counted; headless Bonsai ended after a stop. C# 5: no interpolation,
// no ?. and no property initialisers. Bonsai measures: nothing here judges a device; coen_qc_preflight_rig.judge does.

static class Preflight
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string[] Words(string text)
    {
        return (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    }

    // Harp.Behavior's DigitalOutputs bits: DOPort0-2 are 1, 2, 4; DO0-3 are 0x400 to 0x2000.
    public static ushort Mask(string line)
    {
        int n;
        var upper = line.ToUpperInvariant();
        if (upper.StartsWith("DOPORT") && int.TryParse(upper.Substring(6), out n) && n >= 0 && n <= 2) return (ushort)(1 << n);
        if (upper.StartsWith("DO") && int.TryParse(upper.Substring(2), out n) && n >= 0 && n <= 3) return (ushort)(0x400 << n);
        return 0;
    }
}

/// <summary>
/// A person's Pulse and Reset for one Behavior board, as Harp writes into the board piece's Commands subject. Pulse
/// (Board Line [ms], such as BehaviorBoard-Main DO1 or BehaviorBoard-Main DO1 10000, rig-control's Flush): every line,
/// DO0 to DO3 and DOPort0 to DOPort2, timed by the board (its PulseDO or PulseDOPort width, the line added to the
/// pulsed lines, OutputSet), never cleared from here, so a pulse ends however the workflow stops. Its width: given,
/// 1 to 60000 ms, else the line's pulseWidthMs in rig.yml, else 50 ms. Never on a line wiring gives a CameraTrigger or
/// the ZapitTrigger (the pieces drive those). Reset (empty, or the board's name; rig-control's End Testing, before its
/// stop): each DO and DOPort line not a camera trigger cleared, each fixed line's PulseDO width and the pulsed lines as
/// rig.yml has them.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("A person's Pulse and Reset for this Behavior board (its named entry, the first input), as writes for its Commands subject.")]
public class BoardCommands
{
    const int OutputSet = 34;
    const int OutputClear = 35;
    const int OutputPulseEnable = 45;
    const int PulseDOPort0 = 46;
    const int PulseDO0 = 56;
    // DOPort0-2 and DO0-3: every line a Pulse may raise.
    const ushort OutputLines = 0x3C07;

    /// <summary>The writes for each Pulse and Reset naming this board.</summary>
    public IObservable<HarpMessage> Process(IObservable<NamedDevice<BehaviorBoard>> board, IObservable<string> pulses, IObservable<string> resets)
    {
        return board.Take(1).SelectMany(entry =>
        {
            var lines = new Lines(entry);
            ushort pulsedHere = 0;
            var pulsed = pulses.SelectMany(text => lines.Pulse(text, ref pulsedHere).ToObservable(ImmediateScheduler.Instance));
            return pulsed.Merge(resets.SelectMany(text => lines.Reset(text).ToObservable(ImmediateScheduler.Instance)));
        });
    }

    class Lines
    {
        readonly string name;
        readonly ushort cameras, zapit, pulsed;
        readonly Dictionary<string, int> widths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public Lines(NamedDevice<BehaviorBoard> entry)
        {
            name = entry.Name;
            cameras = (ushort)WiredLines.Find(entry.Settings, "CameraTrigger");
            zapit = (ushort)WiredLines.Find(entry.Settings, "ZapitTrigger");
            var controller = entry.Settings.PulseController;
            pulsed = controller == null ? (ushort)0 : (ushort)((ushort)controller.OutputPulseEnable & ~zapit);
            var outputs = controller == null ? null : controller.Outputs;
            if (outputs == null) return;
            var each = new[] { Tuple.Create("DO1", (object)outputs.DO1), Tuple.Create("DO2", (object)outputs.DO2), Tuple.Create("DO3", (object)outputs.DO3) };
            foreach (var line in each)
            {
                var fixedPulse = line.Item2 as FixedPulse;
                if (fixedPulse != null && fixedPulse.PulseWidthMs > 0) widths[line.Item1] = fixedPulse.PulseWidthMs;
            }
        }

        // The pulse's writes: its width, then the line among the pulsed ones, then the line set. Nothing, said on
        // stderr, for a command it cannot take; nothing for another board's.
        public List<HarpMessage> Pulse(string text, ref ushort pulsedHere)
        {
            var writes = new List<HarpMessage>();
            var parts = Preflight.Words(text);
            if (parts.Length == 0 || parts[0] != name) return writes;
            double ms;
            var mask = parts.Length >= 2 ? Preflight.Mask(parts[1]) : (ushort)0;
            if (parts.Length < 2 || parts.Length > 3 || mask == 0)
                return Ignored(writes, text, "give the board, a line (DO0 to DO3 or DOPort0 to DOPort2) and, optionally, a width in ms");
            if ((mask & (cameras | zapit)) != 0)
                return Ignored(writes, text, parts[1] + " is wired to a " + ((mask & cameras) != 0 ? "CameraTrigger" : "ZapitTrigger") + ", which its piece drives");
            if (parts.Length == 3)
            {
                if (!double.TryParse(parts[2], NumberStyles.Float, Preflight.Invariant, out ms) || ms < 1 || ms > 60000)
                    return Ignored(writes, text, "the width is 1 to 60000 ms");
            }
            else
            {
                int width;
                ms = widths.TryGetValue(parts[1], out width) ? width : 50;
            }
            // The board times every pulse, so a stop, an error or EndAfterStop never leaves a line high (a valve open).
            pulsedHere |= mask;
            writes.Add(HarpMessage.FromUInt16(PulseRegister(mask), MessageType.Write, Width(ms)));
            writes.Add(HarpMessage.FromUInt16(OutputPulseEnable, MessageType.Write, (ushort)(pulsed | pulsedHere)));
            writes.Add(HarpMessage.FromUInt16(OutputSet, MessageType.Write, mask));
            return writes;
        }

        public List<HarpMessage> Reset(string text)
        {
            var writes = new List<HarpMessage>();
            var named = (text ?? string.Empty).Trim();
            if (named.Length > 0 && named != name) return writes;
            writes.Add(HarpMessage.FromUInt16(OutputClear, MessageType.Write, (ushort)(OutputLines & ~cameras)));
            foreach (var pair in widths) writes.Add(HarpMessage.FromUInt16(PulseRegister(Preflight.Mask(pair.Key)), MessageType.Write, Width(pair.Value)));
            writes.Add(HarpMessage.FromUInt16(OutputPulseEnable, MessageType.Write, pulsed));
            Console.Error.WriteLine("preflight: " + name + " reset: DO and DOPort lines closed, pulse widths and pulsed lines as rig.yml has them");
            return writes;
        }

        static List<HarpMessage> Ignored(List<HarpMessage> writes, string text, string why)
        {
            Console.Error.WriteLine("preflight: Pulse '" + text + "' ignored: " + why);
            return writes;
        }

        // PulseDOPort0-2 (46-48) for DOPort0-2, PulseDO0-3 (56-59) for DO0-3.
        static int PulseRegister(ushort mask)
        {
            var first = mask >= 0x400 ? 0x400 : 1;
            var n = 0;
            while ((first << n) != mask) n++;
            return (mask >= 0x400 ? PulseDO0 : PulseDOPort0) + n;
        }

        static ushort Width(double ms)
        {
            return (ushort)Math.Max(1, Math.Min(65535, Math.Round(ms)));
        }
    }
}

/// <summary>
/// A person's Move and Home for one lick spout stage driver (StepperDriver-A or -B), as Harp writes into the driver's
/// Commands subject; only for a motor its rig.yml wiring names (Home: its motor and its input). Move (Driver Motor
/// steps, such as StepperDriver-A Motor1 -150, or Driver Motor to position, such as StepperDriver-A Motor1 to 5900).
/// Home (Driver Motor): OpenRigs.Devices HomeLickSpoutMotor, as the stage homes a motor, on the driver's own data; not
/// homed within 60 s, said on stderr and the motor stopped.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("A person's Move and Home for one lick spout stage driver, as writes for its Commands subject. Inputs: RigConfigs, the driver's Data, Move, Home.")]
public class StepperCommands
{
    const int Motor0MoveRelative = 81;
    const int Motor0MoveAbsolute = 86;

    /// <summary>The driver's name, StepperDriver-A or StepperDriver-B.</summary>
    [Description("The driver's name, StepperDriver-A or StepperDriver-B.")]
    public string Driver { get; set; }

    /// <summary>The writes for each Move and Home naming this driver.</summary>
    public IObservable<HarpMessage> Process(IObservable<LabRig> rig, IObservable<HarpMessage> data, IObservable<string> moves, IObservable<string> homes)
    {
        var name = Driver;
        return rig.Take(1).SelectMany(r =>
        {
            var entry = RigDevice.Find(r.StepperDriver, name);
            var wiring = entry.Wiring;
            int motors = 0, inputs = 0;
            if (wiring != null)
            {
                var m = new[] { wiring.Motor0, wiring.Motor1, wiring.Motor2, wiring.Motor3 };
                var i = new[] { wiring.Input0, wiring.Input1, wiring.Input2, wiring.Input3 };
                for (var n = 0; n < 4; n++)
                {
                    if (m[n] != null) motors |= 1 << n;
                    if (i[n] != null) inputs |= 1 << n;
                }
            }
            var driver = (LickSpoutDriver)Enum.Parse(typeof(LickSpoutDriver), name.Substring("StepperDriver-".Length));
            return Moves(name, motors, moves).Merge(Homes(name, driver, motors & inputs, data, homes));
        });
    }

    static bool Motor(string word, int wired, out int n)
    {
        var motor = word.ToLowerInvariant();
        n = -1;
        return motor.StartsWith("motor") && int.TryParse(motor.Substring(5), out n) && n >= 0 && n <= 3 && (wired & (1 << n)) != 0;
    }

    static IObservable<HarpMessage> Moves(string name, int wired, IObservable<string> moves)
    {
        return moves.SelectMany(text =>
        {
            var parts = Preflight.Words(text);
            if (parts.Length < 3 || parts[0] != name) return Observable.Empty<HarpMessage>();
            var absolute = parts.Length == 4 && parts[2] == "to";
            int n, steps;
            if ((parts.Length != 3 && !absolute) || !Motor(parts[1], wired, out n)
                || !int.TryParse(parts[parts.Length - 1], NumberStyles.Integer, Preflight.Invariant, out steps))
            {
                Console.Error.WriteLine("preflight: Move '" + text + "' ignored: give the driver, a motor its wiring names, and whole steps (or 'to' and a position)");
                return Observable.Empty<HarpMessage>();
            }
            return Observable.Return(HarpMessage.FromInt32((absolute ? Motor0MoveAbsolute : Motor0MoveRelative) + n, MessageType.Write, steps));
        });
    }

    static IObservable<HarpMessage> Homes(string name, LickSpoutDriver driver, int wired, IObservable<HarpMessage> data, IObservable<string> homes)
    {
        return homes.SelectMany(text =>
        {
            var parts = Preflight.Words(text);
            if (parts.Length != 2 || parts[0] != name) return Observable.Empty<HarpMessage>();
            int n;
            if (!Motor(parts[1], wired, out n))
            {
                Console.Error.WriteLine("preflight: Home '" + text + "' ignored: give a motor whose motor and input the driver's wiring names");
                return Observable.Empty<HarpMessage>();
            }
            return HomeLickSpoutMotor.Home(data, driver, n, TimeSpan.FromSeconds(60), DefaultScheduler.Instance)
                .Do(_ => { }, () => Console.Error.WriteLine("preflight: " + name + " Motor" + n + " homed"))
                .Catch<HarpMessage, TimeoutException>(error =>
                {
                    Console.Error.WriteLine("preflight: " + name + " Motor" + n + " not homed: " + error.Message + " It was stopped.");
                    return Observable.Return(Harp.StepperDriver.StopMotors.FromPayload(MessageType.Write, (Harp.StepperDriver.StepperMotors)(1 << n)));
                });
        });
    }
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
