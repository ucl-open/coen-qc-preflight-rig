using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Bonsai;
using Bonsai.Harp;
using YamlDotNet.RepresentationModel;

// PROTOTYPE (7 Nov 2026): if the one-workflow design holds on a rig, PreflightDevices belongs beside
// lib-open-rigs' Shared\RigDevices.cs, whose rule it copies, and the rest in a lab package, both
// with tests. C# 5: no interpolation, no ?. and no property initialisers. Bonsai measures: nothing
// here judges a device; coen_qc_preflight_rig.judge does.

/// <summary>One entry of rig.yml the check covers: a device (kind: instance: settings) or a single section.</summary>
public class PreflightDevice
{
    /// <summary>The rig.yml key, such as camera.</summary>
    public string Kind { get; set; }
    /// <summary>The entry's key, such as body; empty for a single section.</summary>
    public string Instance { get; set; }
    /// <summary>Camera-Body, as lib-open-rigs names it; SpeakerArray for a single section.</summary>
    public string Name { get; set; }
    /// <summary>The entry's portName, empty when it has none.</summary>
    public string PortName { get; set; }
    /// <summary>The entry's serialNumber, empty when it has none.</summary>
    public string SerialNumber { get; set; }
    /// <summary>projectionCalibration's meshMapPath, empty elsewhere.</summary>
    public string MeshMapPath { get; set; }
    /// <summary>projectionCalibration's gammaLutPath, empty elsewhere.</summary>
    public string GammaLutPath { get; set; }
    /// <summary>A behaviour board's pulseController outputs' pulseWidthMs, such as DO1=50,DO2=45; empty elsewhere.</summary>
    public string PulseWidths { get; set; }
}

/// <summary>One raw measurement, a row of the check's CSV: Device,Measure,Value,Detail.</summary>
public class PreflightRow
{
    /// <summary>The device's name.</summary>
    public string Device { get; set; }
    /// <summary>What was measured: messages, frames, seconds, person, error.</summary>
    public string Measure { get; set; }
    /// <summary>The number measured.</summary>
    public string Value { get; set; }
    /// <summary>An error's message, with no commas, semicolons or line breaks.</summary>
    public string Detail { get; set; }
}

/// <summary>What an open device sent in one interval of about a second, or why it failed.</summary>
public class DeviceReport
{
    /// <summary>The device's name.</summary>
    public string Device { get; set; }
    /// <summary>What is counted: messages, frames.</summary>
    public string Measure { get; set; }
    /// <summary>Items sent in the interval.</summary>
    public long Count { get; set; }
    /// <summary>The interval's length, by a stopwatch: intervals follow one another with no gap.</summary>
    public double Seconds { get; set; }
    /// <summary>Whether the device has opened: sent something, failed, or been open OpenSeconds without failing.</summary>
    public bool Settled { get; set; }
    /// <summary>Why the device failed; null while it works.</summary>
    public string Error { get; set; }
}

/// <summary>Where the check is after Go: a row to write, the step and prompt metrics, and whether it is done.</summary>
public class CheckState
{
    /// <summary>A raw row, or null.</summary>
    public PreflightRow Row { get; set; }
    /// <summary>The step metric: n/total Device while a device is counted, else empty.</summary>
    public string Step { get; set; }
    /// <summary>The prompt metric: Device: question while a person's answer is awaited, else empty.</summary>
    public string Prompt { get; set; }
    /// <summary>True once, on the last state: every row has been given.</summary>
    public bool Done { get; set; }
}

static class Preflight
{
    public static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static HashSet<string> Kinds(string kinds)
    {
        return new HashSet<string>((kinds ?? string.Empty).Split(',').Select(k => k.Trim()).Where(k => k.Length > 0));
    }

    // An error's message for a CSV field and the devices metric: no commas, semicolons, quotes or line breaks.
    public static string Clean(Exception error)
    {
        var inner = error;
        while (inner.InnerException != null && (inner is AggregateException || inner is System.Reflection.TargetInvocationException)) inner = inner.InnerException;
        return Regex.Replace(inner.Message.Replace("\"", "'"), "[,;\\s]+", " ").Trim();
    }

    // A Confirm's text, <Device> yes or <Device> no; false when it is neither.
    public static bool ParseAnswer(string text, out string device, out bool yes)
    {
        device = null;
        yes = false;
        var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        var word = parts[1].ToLowerInvariant();
        if (word != "yes" && word != "no") return false;
        device = parts[0];
        yes = word == "yes";
        return true;
    }
}

/// <summary>The rig.yml entries of the kinds named, in file order; nothing when the rig declares none of them.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Source)]
[Description("The rig.yml entries of the kinds named, in file order; nothing when the rig declares none of them.")]
public class PreflightDevices
{
    string configRoot = @"C:\Coen-Lab-Local\RigConfigs";

    /// <summary>The folder holding &lt;MACHINE&gt;\rig.yml.</summary>
    [Description("The folder holding <MACHINE>\\rig.yml.")]
    public string ConfigRoot { get { return configRoot; } set { configRoot = value; } }

    /// <summary>The rig.yml keys to list, separated by commas, such as camera.</summary>
    [Description("The rig.yml keys to list, separated by commas, such as camera.")]
    public string Kinds { get; set; }

    /// <summary>Reads rig.yml when subscribed; a missing or unreadable rig.yml is an error: the check cannot run.</summary>
    public IObservable<PreflightDevice> Process()
    {
        return Observable.Defer(() => Read().ToObservable());
    }

    List<PreflightDevice> Read()
    {
        var path = Path.Combine(Environment.ExpandEnvironmentVariables(ConfigRoot), Environment.MachineName, "rig.yml");
        if (!File.Exists(path)) throw new FileNotFoundException("The preflight needs a rig.yml, and there is none at " + path, path);
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
        if (root == null) throw new InvalidOperationException(path + " is empty or not a set of settings");
        var kinds = Preflight.Kinds(Kinds);
        var devices = new List<PreflightDevice>();
        foreach (var kind in root.Children)
        {
            var kindName = Scalar(kind.Key);
            var entries = kind.Value as YamlMappingNode;
            if (kindName == null || entries == null || !kinds.Contains(kindName)) continue;
            // As lib-open-rigs' RigDevices: a key whose every value is a mapping is a group of devices.
            if (entries.Children.Count > 0 && entries.Children.Values.All(v => v is YamlMappingNode))
            {
                foreach (var entry in entries.Children)
                {
                    var instance = Scalar(entry.Key) ?? string.Empty;
                    devices.Add(Device(kindName, instance, Pascal(kindName) + "-" + Pascal(instance), (YamlMappingNode)entry.Value));
                }
            }
            else devices.Add(Device(kindName, string.Empty, Pascal(kindName), entries));
        }
        return devices;
    }

    static PreflightDevice Device(string kind, string instance, string name, YamlMappingNode settings)
    {
        var device = new PreflightDevice();
        device.Kind = kind;
        device.Instance = instance;
        device.Name = name;
        device.PortName = Setting(settings, "portName");
        device.SerialNumber = Setting(settings, "serialNumber");
        device.MeshMapPath = Setting(settings, "meshMapPath");
        device.GammaLutPath = Setting(settings, "gammaLutPath");
        var widths = new List<string>();
        var outputs = Child(Child(settings, "pulseController"), "outputs");
        if (outputs != null)
        {
            foreach (var line in outputs.Children)
            {
                var width = Setting(line.Value as YamlMappingNode, "pulseWidthMs");
                if (width.Length > 0) widths.Add(Scalar(line.Key) + "=" + width);
            }
        }
        device.PulseWidths = string.Join(",", widths);
        return device;
    }

    static YamlMappingNode Child(YamlMappingNode settings, string key)
    {
        if (settings == null) return null;
        foreach (var pair in settings.Children)
        {
            if (Scalar(pair.Key) == key) return pair.Value as YamlMappingNode;
        }
        return null;
    }

    static string Setting(YamlMappingNode settings, string key)
    {
        if (settings == null) return string.Empty;
        foreach (var pair in settings.Children)
        {
            if (Scalar(pair.Key) == key)
            {
                // An unset value is null, ~ or empty, as lib-open-rigs' RigDevices.Value reads it.
                var value = Scalar(pair.Value);
                return string.IsNullOrWhiteSpace(value) || value == "null" || value == "~" ? string.Empty : value.Trim();
            }
        }
        return string.Empty;
    }

    static string Scalar(YamlNode node)
    {
        var scalar = node as YamlScalarNode;
        return scalar != null ? scalar.Value : null;
    }

    static string Pascal(string name)
    {
        return name.Length == 0 ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);
    }
}

/// <summary>
/// Keeps a device's stream subscribed (the device open) and reports, about once a second, how many
/// items it sent in that interval; a failure becomes one report with the error, so one dead device
/// never ends the workflow.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Keeps a device open and reports what it sent each second; a failure becomes one error report.")]
public class DeviceMonitor
{
    string measure = "messages";
    double openSeconds = 5;

    /// <summary>The device's name.</summary>
    public string Device { get; set; }

    /// <summary>What is counted: messages, frames.</summary>
    public string Measure { get { return measure; } set { measure = value; } }

    /// <summary>Seconds after which a device that has sent nothing and not failed counts as open (a camera waiting for its trigger).</summary>
    public double OpenSeconds { get { return openSeconds; } set { openSeconds = value; } }

    /// <summary>The device's reports.</summary>
    public IObservable<DeviceReport> Process<TSource>(IObservable<TSource> source)
    {
        // Read at subscription: a PropertyMapping sets Device after the workflow is built.
        return Observable.Defer(() => Monitor(source, Device, Measure, TimeSpan.FromSeconds(OpenSeconds)));
    }

    static IObservable<DeviceReport> Monitor<TSource>(IObservable<TSource> source, string device, string measure, TimeSpan open)
    {
        return Observable.Create<DeviceReport>(observer =>
        {
            var clock = Stopwatch.StartNew();
            var last = TimeSpan.Zero;
            long count = 0;
            var seen = 0;
            string error = null;
            // Every report, an error's too, goes out on the tick: never before every subscriber of
            // DeviceReports is subscribed, which a port that does not exist could otherwise beat.
            var ticks = Observable.Interval(TimeSpan.FromSeconds(1)).Subscribe(_ =>
            {
                var report = new DeviceReport { Device = device, Measure = measure };
                var failed = Volatile.Read(ref error);
                if (failed != null)
                {
                    report.Error = failed;
                    report.Settled = true;
                    Console.Error.WriteLine("preflight: " + device + " error " + failed);
                    observer.OnNext(report);
                    observer.OnCompleted();
                    return;
                }
                var now = clock.Elapsed;
                report.Count = Interlocked.Exchange(ref count, 0);
                report.Seconds = (now - last).TotalSeconds;
                report.Settled = Volatile.Read(ref seen) == 1 || now >= open;
                last = now;
                observer.OnNext(report);
            });
            IDisposable data = Disposable.Empty;
            try
            {
                data = source.Subscribe(
                    item => { Interlocked.Increment(ref count); Volatile.Write(ref seen, 1); },
                    e => Volatile.Write(ref error, Preflight.Clean(e)),
                    () => { });
            }
            catch (Exception e) { Volatile.Write(ref error, Preflight.Clean(e)); }
            return new CompositeDisposable(data, ticks);
        });
    }
}

/// <summary>
/// The devices metric, once a second once every device has opened or failed: each device in list
/// order as Device measure rate (per second over the last second) or Device error detail, joined by ;.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Each device's rate over the last second, or its error, once a second once every device has opened or failed.")]
public class DeviceRates
{
    string notOpened = "speakerArray";

    /// <summary>Kinds with nothing to open, only a person's answer: listed as Device person 0.</summary>
    public string NotOpened { get { return notOpened; } set { notOpened = value; } }

    /// <summary>The devices metric's text.</summary>
    public IObservable<string> Process(IObservable<PreflightDevice[]> devices, IObservable<DeviceReport> reports)
    {
        var skip = Preflight.Kinds(NotOpened);
        return Observable.Create<string>(observer =>
        {
            var gate = new object();
            PreflightDevice[] list = null;
            var latest = new Dictionary<string, DeviceReport>();
            var settled = new HashSet<string>();
            var listed = devices.Take(1).Subscribe(l => { lock (gate) list = l; }, observer.OnError);
            var heard = reports.Subscribe(r =>
            {
                lock (gate)
                {
                    latest[r.Device] = r;
                    if (r.Settled) settled.Add(r.Device);
                }
            }, observer.OnError);
            var ticks = Observable.Interval(TimeSpan.FromSeconds(1)).Subscribe(_ =>
            {
                string text;
                lock (gate)
                {
                    if (list == null || !list.All(d => skip.Contains(d.Kind) || settled.Contains(d.Name))) return;
                    text = string.Join(";", list.Select(d => Entry(d, skip.Contains(d.Kind) ? null : latest[d.Name])));
                }
                observer.OnNext(text);
            });
            return new CompositeDisposable(listed, heard, ticks);
        });
    }

    static string Entry(PreflightDevice device, DeviceReport report)
    {
        if (report == null) return device.Name + " person 0";
        if (report.Error != null) return device.Name + " error " + report.Error;
        var rate = report.Seconds > 0 ? report.Count / report.Seconds : 0;
        return device.Name + " " + report.Measure + " " + rate.ToString("0.#", Preflight.Invariant);
    }
}

/// <summary>
/// The check, from Go: each open device counted in turn for Seconds from its reports (step metric),
/// then each device a person must confirm asked in turn (prompt metric) until its Confirm comes.
/// Gives the raw rows, then a Done state, then completes.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("From Go: counts each open device in turn, then asks the person about each device that needs it; gives the raw rows.")]
public class RunCheck
{
    string notOpened = "speakerArray";
    string questions = "speakerArray=Did each speaker play the test sound?;projectionCalibration=Does the test pattern fill the dome?";
    double seconds = 5;

    /// <summary>Kinds with nothing to open, only a person's answer.</summary>
    public string NotOpened { get { return notOpened; } set { notOpened = value; } }

    /// <summary>What a person is asked, by kind: kind=question, separated by ;.</summary>
    public string Questions { get { return questions; } set { questions = value; } }

    /// <summary>Seconds each device is counted.</summary>
    public double Seconds { get { return seconds; } set { seconds = value; } }

    static readonly object Went = new object();

    /// <summary>The check's states.</summary>
    public IObservable<CheckState> Process<TGo>(IObservable<PreflightDevice[]> devices, IObservable<DeviceReport> reports, IObservable<TGo> go, IObservable<string> confirms)
    {
        var skip = Preflight.Kinds(NotOpened);
        var asked = new Dictionary<string, string>();
        foreach (var pair in (Questions ?? string.Empty).Split(';'))
        {
            var cut = pair.IndexOf('=');
            if (cut > 0) asked[pair.Substring(0, cut).Trim()] = pair.Substring(cut + 1).Trim();
        }
        var window = Seconds;
        return Observable.Create<CheckState>(observer =>
        {
            var inbox = new BlockingCollection<object>();
            var cancel = new CancellationTokenSource();
            Action<Exception> fail = e => inbox.Add(e);
            var subscriptions = new CompositeDisposable(
                devices.Take(1).Subscribe(l => inbox.Add(l), fail),
                reports.Subscribe(r => inbox.Add(r), fail),
                go.Take(1).Subscribe(_ => inbox.Add(Went), fail),
                confirms.Subscribe(c => inbox.Add(c ?? string.Empty), fail));
            var thread = new Thread(() =>
            {
                try { new Run(inbox, cancel.Token, observer, skip, asked, window).Check(); }
                catch (OperationCanceledException) { }
            });
            thread.IsBackground = true;
            thread.Name = "Preflight check";
            thread.Start();
            return new CompositeDisposable(subscriptions, Disposable.Create(cancel.Cancel));
        });
    }

    class Run
    {
        readonly BlockingCollection<object> inbox;
        readonly CancellationToken token;
        readonly IObserver<CheckState> observer;
        readonly HashSet<string> skip;
        readonly Dictionary<string, string> asked;
        readonly double window;
        readonly Dictionary<string, string> errors = new Dictionary<string, string>();
        readonly Dictionary<string, bool> answers = new Dictionary<string, bool>();
        PreflightDevice[] list;
        bool went;

        public Run(BlockingCollection<object> inbox, CancellationToken token, IObserver<CheckState> observer,
                   HashSet<string> skip, Dictionary<string, string> asked, double window)
        {
            this.inbox = inbox;
            this.token = token;
            this.observer = observer;
            this.skip = skip;
            this.asked = asked;
            this.window = window;
        }

        public void Check()
        {
            Send(null, string.Empty, string.Empty, false);
            while (list == null || !went) Next(Timeout.InfiniteTimeSpan);
            var counted = list.Where(d => !skip.Contains(d.Kind)).ToList();
            for (var i = 0; i < counted.Count; i++)
            {
                var device = counted[i];
                Send(null, (i + 1).ToString(Preflight.Invariant) + "/" + counted.Count.ToString(Preflight.Invariant) + " " + device.Name, string.Empty, false);
                foreach (var row in Count(device.Name)) Send(row, null, null, false);
            }
            foreach (var device in list.Where(d => asked.ContainsKey(d.Kind)))
            {
                while (!answers.ContainsKey(device.Name))
                {
                    Send(null, string.Empty, device.Name + ": " + asked[device.Kind], false);
                    Next(Timeout.InfiniteTimeSpan);
                }
                Send(Row(device.Name, "person", answers[device.Name] ? "1" : "0", string.Empty), string.Empty, string.Empty, false);
            }
            Send(null, string.Empty, string.Empty, true);
            observer.OnCompleted();
        }

        // The device's rows: its count over whole reports adding up to the window, and their seconds.
        IEnumerable<PreflightRow> Count(string device)
        {
            string error;
            if (errors.TryGetValue(device, out error)) return new[] { Row(device, "error", string.Empty, error) };
            long count = 0;
            double seconds = 0;
            string measure = null;
            var deadline = Stopwatch.StartNew();
            var limit = TimeSpan.FromSeconds(window + 5);
            while (seconds < window)
            {
                var left = limit - deadline.Elapsed;
                var report = left > TimeSpan.Zero ? Next(left) as DeviceReport : null;
                if (left <= TimeSpan.Zero) break;
                if (report == null || report.Device != device) continue;
                if (report.Error != null) return new[] { Row(device, "error", string.Empty, report.Error) };
                count += report.Count;
                seconds += report.Seconds;
                measure = report.Measure;
            }
            if (measure == null)
                return new[] { Row(device, "error", string.Empty, "it sent no report in " + limit.TotalSeconds.ToString(Preflight.Invariant) + " s") };
            return new[]
            {
                Row(device, measure, count.ToString(Preflight.Invariant), string.Empty),
                Row(device, "seconds", seconds.ToString("0.###", Preflight.Invariant), string.Empty),
            };
        }

        // The next item from the inbox, after noting what it says; null after the timeout.
        object Next(TimeSpan timeout)
        {
            object item;
            if (!inbox.TryTake(out item, timeout.Equals(Timeout.InfiniteTimeSpan) ? Timeout.Infinite : (int)Math.Ceiling(timeout.TotalMilliseconds), token)) return null;
            var error = item as Exception;
            if (error != null) { observer.OnError(error); throw new OperationCanceledException(); }
            if (item is PreflightDevice[]) list = (PreflightDevice[])item;
            else if (item == Went) went = true;
            var report = item as DeviceReport;
            if (report != null && report.Error != null) errors[report.Device] = report.Error;
            var text = item as string;
            if (text != null && went)
            {
                // The first answer after Go for each device is the one kept.
                string device;
                bool yes;
                if (!Preflight.ParseAnswer(text, out device, out yes)) Console.Error.WriteLine("preflight: Confirm '" + text + "' ignored: give <Device> yes or <Device> no");
                else if (!answers.ContainsKey(device)) answers[device] = yes;
            }
            return item;
        }

        void Send(PreflightRow row, string step, string prompt, bool done)
        {
            if (row != null) Console.Error.WriteLine("preflight: " + row.Device + " " + row.Measure + " " + row.Value + " " + row.Detail);
            observer.OnNext(new CheckState { Row = row, Step = step, Prompt = prompt, Done = done });
        }
    }

    static PreflightRow Row(string device, string measure, string value, string detail)
    {
        return new PreflightRow { Device = device, Measure = measure, Value = value, Detail = detail };
    }
}

/// <summary>The rows of the check's states, for CsvWriter.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("The rows of the check's states.")]
public class RowsOf
{
    /// <summary>Each state's row, where it has one.</summary>
    public IObservable<PreflightRow> Process(IObservable<CheckState> source)
    {
        return source.Where(s => s.Row != null).Select(s => s.Row);
    }
}

/// <summary>The first Confirm for the device after Go, the person's answer.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("The first Confirm for the device after Go.")]
public class Answered
{
    /// <summary>The device's name.</summary>
    public string Device { get; set; }

    /// <summary>The Confirm's text, once.</summary>
    public IObservable<string> Process<TGo>(IObservable<string> confirms, IObservable<TGo> go)
    {
        return Observable.Defer(() =>
        {
            var name = Device;
            return confirms.SkipUntil(go).Where(text =>
            {
                string device;
                bool yes;
                return Preflight.ParseAnswer(text, out device, out yes) && device == name;
            }).Take(1);
        });
    }
}

/// <summary>
/// Passes the source through until the signal, then completes at the source's next item, on the
/// source's own thread: a BonVision render chain ended from another thread is disposed off the GL
/// thread, and RenderTexture deleting its framebuffer there crashed the process (7 Nov 2026).
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Passes the source through until the signal, then completes at the source's next item, on its own thread.")]
public class CloseWhen
{
    /// <summary>The source until the signal.</summary>
    public IObservable<TSource> Process<TSource, TSignal>(IObservable<TSource> source, IObservable<TSignal> signal)
    {
        return Observable.Create<TSource>(observer =>
        {
            var closing = 0;
            var signalled = signal.Take(1).Subscribe(_ => Volatile.Write(ref closing, 1));
            var passed = source.TakeWhile(_ => Volatile.Read(ref closing) == 0).Subscribe(observer);
            return new CompositeDisposable(signalled, passed);
        });
    }
}

/// <summary>
/// A behaviour board's pulse from the Pulse command (Device Line, such as BehaviorBoard-Main DO1): the
/// line set, then cleared after its pulseWidthMs in rig.yml, else 50 ms. Other devices' commands, and
/// any board that is not a behaviour board, give nothing.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Harp writes for a Pulse command to this behaviour board: the line set, then cleared after its pulse width.")]
public class BoardPulses
{
    const int OutputSet = 34;
    const int OutputClear = 35;

    /// <summary>The board's name.</summary>
    public string Device { get; set; }
    /// <summary>The board's rig.yml kind; only behaviorBoard pulses.</summary>
    public string Kind { get; set; }
    /// <summary>The board's pulse widths, such as DO1=50,DO2=45.</summary>
    public string PulseWidths { get; set; }

    /// <summary>The writes for each Pulse command to this board.</summary>
    public IObservable<HarpMessage> Process(IObservable<string> commands)
    {
        return Observable.Defer(() =>
        {
            var name = Device;
            var board = Kind == "behaviorBoard";
            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (PulseWidths ?? string.Empty).Split(','))
            {
                var cut = pair.IndexOf('=');
                double ms;
                if (cut > 0 && double.TryParse(pair.Substring(cut + 1), NumberStyles.Float, Preflight.Invariant, out ms)) widths[pair.Substring(0, cut).Trim()] = ms;
            }
            return commands.SelectMany(text =>
            {
                var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (!board || parts.Length != 2 || parts[0] != name) return Observable.Empty<HarpMessage>();
                var mask = Mask(parts[1]);
                if (mask == 0)
                {
                    Console.Error.WriteLine("preflight: Pulse '" + text + "' ignored: the line is DO0 to DO3 or DOPort0 to DOPort2");
                    return Observable.Empty<HarpMessage>();
                }
                double ms;
                if (!widths.TryGetValue(parts[1], out ms)) ms = 50;
                return Observable.Return(HarpMessage.FromUInt16(OutputSet, MessageType.Write, mask))
                    .Concat(Observable.Timer(TimeSpan.FromMilliseconds(ms)).Select(_ => HarpMessage.FromUInt16(OutputClear, MessageType.Write, mask)));
            });
        });
    }

    // Harp.Behavior's DigitalOutputs bits: DOPort0-2 are 1, 2, 4; DO0-3 are 0x400 to 0x2000.
    static ushort Mask(string line)
    {
        int n;
        var upper = line.ToUpperInvariant();
        if (upper.StartsWith("DOPORT") && int.TryParse(upper.Substring(6), out n) && n >= 0 && n <= 2) return (ushort)(1 << n);
        if (upper.StartsWith("DO") && int.TryParse(upper.Substring(2), out n) && n >= 0 && n <= 3) return (ushort)(0x400 << n);
        return 0;
    }
}
