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
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
    /// <summary>A behaviour board's pulseController outputPulseEnable, such as DO1, DO2, DO3; empty elsewhere.</summary>
    public string OutputPulseEnable { get; set; }
    /// <summary>A behaviour board's cameraTriggerController trigger0Frequency in Hz; 0 when trigger 0 is not enabled, and elsewhere.</summary>
    public int Trigger0Frequency { get; set; }
    /// <summary>A behaviour board with a runningWheel in rig.yml: its encoder on Port 2 is enabled.</summary>
    public bool RunningWheel { get; set; }
    /// <summary>A camera's exposureTime in microseconds, else 19000.</summary>
    public double ExposureTime { get; set; }
    /// <summary>A camera's gain, else 0.</summary>
    public double Gain { get; set; }
    /// <summary>A camera's binning, else 1.</summary>
    public int Binning { get; set; }
    /// <summary>A camera's pixelFormat, else Mono8.</summary>
    public string PixelFormat { get; set; }
    /// <summary>A stepper driver's motors its rig.yml wiring names, as Harp.StepperDriver's StepperMotors bits (Motor0 = 1); 0 elsewhere.</summary>
    public int WiredMotors { get; set; }
    /// <summary>A stepper driver's inputs its rig.yml wiring names, as DigitalInputs bits (Input0 = 1); 0 elsewhere.</summary>
    public int WiredInputs { get; set; }
    /// <summary>A stepper driver's stepInterval in microseconds; 0 when rig.yml gives none.</summary>
    public int StepInterval { get; set; }
    /// <summary>A stepper driver's microstepResolution, such as Microstep8; empty when rig.yml gives none.</summary>
    public string MicrostepResolution { get; set; }
    /// <summary>A stepper driver's maximumRunCurrent in A; 0 when rig.yml gives none.</summary>
    public double MaximumRunCurrent { get; set; }
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
        device.OutputPulseEnable = Setting(Child(settings, "pulseController"), "outputPulseEnable");
        var trigger = Child(settings, "cameraTriggerController");
        if (Setting(trigger, "trigger0Enabled").ToLowerInvariant() == "true")
            device.Trigger0Frequency = (int)Number(trigger, "trigger0Frequency", 50);
        device.RunningWheel = Child(settings, "runningWheel") != null;
        device.ExposureTime = Number(settings, "exposureTime", 19000);
        device.Gain = Number(settings, "gain", 0);
        device.Binning = (int)Number(settings, "binning", 1);
        device.PixelFormat = Setting(settings, "pixelFormat");
        if (device.PixelFormat.Length == 0) device.PixelFormat = "Mono8";
        device.StepInterval = (int)Number(settings, "stepInterval", 0);
        device.MicrostepResolution = Setting(settings, "microstepResolution");
        device.MaximumRunCurrent = Number(settings, "maximumRunCurrent", 0);
        var wiring = Child(settings, "wiring");
        if (wiring != null)
        {
            // The lab's HarpStepperDriver wiring: Motor0-3 and Input0-3, each naming what it drives or reads.
            foreach (var line in wiring.Children)
            {
                var key = (Scalar(line.Key) ?? string.Empty).ToLowerInvariant();
                int n;
                if (!(line.Value is YamlMappingNode) || key.Length < 6 || !int.TryParse(key.Substring(key.Length - 1), out n) || n > 3) continue;
                if (key.StartsWith("motor")) device.WiredMotors |= 1 << n;
                else if (key.StartsWith("input")) device.WiredInputs |= 1 << n;
            }
        }
        return device;
    }

    static double Number(YamlMappingNode settings, string key, double otherwise)
    {
        var text = Setting(settings, key);
        if (text.Length == 0) return otherwise;
        double value;
        if (!double.TryParse(text, NumberStyles.Float, Preflight.Invariant, out value))
            throw new InvalidOperationException("rig.yml's " + key + " is '" + text + "', not a number");
        return value;
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
/// A behaviour board's writes. When the board opens, with trigger 0 enabled in rig.yml: camera trigger 0
/// at its frequency, then started, so the cameras get frames. The board stops it itself when Device
/// closes it at Stop, as the firmware stops both camera outputs on going to Standby. OutputPulseEnable
/// is cleared as it opens. A Reset command (rig-control's End Testing, before its stop) closes every DO
/// line and puts PulseDO and OutputPulseEnable back as rig.yml has them. Then for each Pulse command (Device Line, such as BehaviorBoard-Main DO1): a
/// pulse of the line's pulseWidthMs in rig.yml, else 50 ms, or of the width given third
/// (BehaviorBoard-Main DO1 10000, rig-control's Flush), timed by the board on DO0 to DO3 (so it ends
/// even if the workflow stops) and from here on DOPort0 to DOPort2. Other devices' commands, and any
/// board that is not a behaviour board, give nothing.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Transform)]
[Description("Harp writes to this board: a behaviour board's camera trigger 0 and each Pulse; a stepper driver's setup from rig.yml and each Move.")]
public class BoardWrites
{
    const int OutputSet = 34;
    const int OutputClear = 35;
    // Harp.Behavior's OutputPulseEnable (U16 DigitalOutputs) and PulseDO0 to PulseDO3 (U16 ms, 56 to 59).
    const int PulseEnableRegister = 45;
    const int PulseDO0 = 56;
    // Harp.Behavior's device.yml: StartCameras (U8, CameraOutput0 = 1) and Camera0Frequency (U16, Hz).
    const int StartCameras = 78;
    const int Camera0Frequency = 93;
    // The running wheel, as OpenRigs.Devices' BehaviorBoard.RunningWheel writes it: EnableEncoders
    // (U8, EncoderPort2 = 4), EncoderMode (U8, Position = 0), EncoderReset (U8, EncoderPort2 = 4).
    const int EnableEncoders = 82;
    const int EncoderMode = 83;
    const int EncoderReset = 108;
    const byte EncoderPort2 = 4;

    /// <summary>The board's name.</summary>
    public string Device { get; set; }
    /// <summary>The board's rig.yml kind; only behaviorBoard is written to.</summary>
    public string Kind { get; set; }
    /// <summary>The board's pulse widths, such as DO1=50,DO2=45.</summary>
    public string PulseWidths { get; set; }
    /// <summary>The lines rig.yml's pulseController pulses, such as DO1, DO2, DO3: put back by Reset.</summary>
    public string OutputPulseEnable { get; set; }
    /// <summary>Camera trigger 0's frequency in Hz; 0 leaves the trigger alone.</summary>
    public int Trigger0Frequency { get; set; }
    /// <summary>rig.yml has a runningWheel for this board: its encoder is enabled, in Position mode, and reset.</summary>
    public bool RunningWheel { get; set; }
    /// <summary>A stepper driver's motors its wiring names (StepperMotors bits): enabled, and the only ones Move drives.</summary>
    public int WiredMotors { get; set; }
    /// <summary>A stepper driver's inputs its wiring names (DigitalInputs bits): enabled, Input n stopping Motor n on falling where Motor n is wired.</summary>
    public int WiredInputs { get; set; }
    /// <summary>A stepper driver's stepInterval in microseconds; 0 leaves it.</summary>
    public int StepInterval { get; set; }
    /// <summary>A stepper driver's microstepResolution, such as Microstep8; empty leaves it.</summary>
    public string MicrostepResolution { get; set; }
    /// <summary>A stepper driver's maximumRunCurrent in A; 0 leaves it.</summary>
    public double MaximumRunCurrent { get; set; }

    // Harp.StepperDriver 0.4.0's registers: EnableDriver and EnableDigitalInputs (U8 bits), per motor
    // MicrostepResolution (U8), MaximumRunCurrent (float), StepInterval (U16) and MoveRelative (S32),
    // per input OpMode (U8, StopMotor<n>OnFalling = 32 + n), InterlockEnabled (U8, Open = 1) and
    // AccumulatedStepsSamplingRate (U8, Rate10Hz = 1), as av-dome writes them.
    const int EnableDriver = 32;
    const int EnableDigitalInputs = 36;
    const int Motor0MicrostepResolution = 42;
    const int Motor0MaximumRunCurrent = 46;
    const int Motor0StepInterval = 54;
    const int Input0OpMode = 68;
    const int InterlockEnabled = 72;
    const int AccumulatedStepsSamplingRate = 73;
    const int Motor0MoveRelative = 81;
    const int DigitalInputState = 78;
    const int Motor0AccumulatedSteps = 91;
    const int StopMotors = 110;

    /// <summary>The board's writes when it opens, then those for each Pulse and Reset command (a behaviour board), or Move and Home command (a stepper driver), to it.</summary>
    public IObservable<HarpMessage> Process(IObservable<string> pulses, IObservable<string> moves, IObservable<string> homes, IObservable<string> resets)
    {
        return Observable.Defer(() =>
        {
            if (Kind == "stepperDriver") return Stepper().ToObservable().Concat(Moves(moves).Merge(Homes(homes)));
            if (Kind == "behaviorBoard") return Board(pulses).Merge(Resets(resets));
            return Board(pulses);  // another board: open until its commands end, writing nothing
        });
    }

    // Reset (Pip, 11 Nov 2026: "End test should reset values and close"), which rig-control sends before
    // End Testing's stop: every DO line closed now (a Flush too; DO0 left to the camera trigger when it
    // runs), each line's PulseDO back to rig.yml's pulseWidthMs and OutputPulseEnable back to rig.yml's
    // outputPulseEnable, so the board is as an experiment expects it. Its text: empty for every board,
    // or this board's name.
    IObservable<HarpMessage> Resets(IObservable<string> resets)
    {
        var name = Device;
        return resets.SelectMany(text =>
        {
            var named = (text ?? string.Empty).Trim();
            if (named.Length > 0 && named != name) return Observable.Empty<HarpMessage>();
            var writes = new List<HarpMessage>();
            writes.Add(HarpMessage.FromUInt16(OutputClear, MessageType.Write, (ushort)(Trigger0Frequency > 0 ? 0x3800 : 0x3C00)));
            foreach (var pair in Widths())
            {
                var mask = Mask(pair.Key);
                if (mask < 0x400) continue;
                var n = 0;
                while ((0x400 << n) != mask) n++;
                writes.Add(HarpMessage.FromUInt16(PulseDO0 + n, MessageType.Write, Width(pair.Value)));
            }
            ushort enable = 0;
            foreach (var line in (OutputPulseEnable ?? string.Empty).Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var mask = Mask(line.Trim());
                if (mask == 0) Console.Error.WriteLine("preflight: " + name + " Reset: rig.yml's outputPulseEnable names '" + line + "', not a line");
                enable |= mask;
            }
            writes.Add(HarpMessage.FromUInt16(PulseEnableRegister, MessageType.Write, enable));
            Console.Error.WriteLine("preflight: " + name + " reset: DO lines closed, pulse widths and OutputPulseEnable as rig.yml");
            return writes.ToObservable();
        });
    }

    Dictionary<string, double> Widths()
    {
        var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in (PulseWidths ?? string.Empty).Split(','))
        {
            var cut = pair.IndexOf('=');
            double ms;
            if (cut > 0 && double.TryParse(pair.Substring(cut + 1), NumberStyles.Float, Preflight.Invariant, out ms)) widths[pair.Substring(0, cut).Trim()] = ms;
        }
        return widths;
    }

    static ushort Width(double ms)
    {
        return (ushort)Math.Max(1, Math.Min(65535, Math.Round(ms)));
    }

    IObservable<HarpMessage> Board(IObservable<string> pulses)
    {
        return Observable.Defer(() =>
        {
            var board = Kind == "behaviorBoard";
            var start = new List<HarpMessage>();
            if (board && RunningWheel)
            {
                start.Add(HarpMessage.FromByte(EnableEncoders, MessageType.Write, EncoderPort2));
                start.Add(HarpMessage.FromByte(EncoderMode, MessageType.Write, 0));
                start.Add(HarpMessage.FromByte(EncoderReset, MessageType.Write, EncoderPort2));
            }
            // No line pulses until this run writes its width (an experiment leaves DO1 to DO3 enabled).
            if (board) start.Add(HarpMessage.FromUInt16(PulseEnableRegister, MessageType.Write, 0));
            if (board && Trigger0Frequency > 0)
            {
                start.Add(HarpMessage.FromUInt16(Camera0Frequency, MessageType.Write, (ushort)Trigger0Frequency));
                start.Add(HarpMessage.FromByte(StartCameras, MessageType.Write, 1));
            }
            return start.ToObservable().Concat(Pulses(pulses, board));
        });
    }

    // A stepper driver's setup from rig.yml (Pip, 11 Nov 2026: "rig.yml, one source"): only the motors and
    // inputs its wiring names, its settings where rig.yml gives them, and positions sent 10 times a second.
    List<HarpMessage> Stepper()
    {
        var start = new List<HarpMessage>();
        start.Add(HarpMessage.FromByte(InterlockEnabled, MessageType.Write, 1));
        start.Add(HarpMessage.FromByte(EnableDriver, MessageType.Write, (byte)WiredMotors));
        byte resolution = 0;
        var resolutions = new[] { "Microstep8", "Microstep16", "Microstep32", "Microstep64" };
        var r = Array.IndexOf(resolutions, MicrostepResolution ?? string.Empty);
        if (r >= 0) resolution = (byte)r;
        else if (!string.IsNullOrEmpty(MicrostepResolution))
            throw new InvalidOperationException(Device + ": rig.yml's microstepResolution is '" + MicrostepResolution + "', not Microstep8, 16, 32 or 64");
        for (var n = 0; n < 4; n++)
        {
            if ((WiredMotors & (1 << n)) == 0) continue;
            if (r >= 0) start.Add(HarpMessage.FromByte(Motor0MicrostepResolution + n, MessageType.Write, resolution));
            if (MaximumRunCurrent > 0) start.Add(HarpMessage.FromSingle(Motor0MaximumRunCurrent + n, MessageType.Write, (float)MaximumRunCurrent));
            if (StepInterval > 0) start.Add(HarpMessage.FromUInt16(Motor0StepInterval + n, MessageType.Write, (ushort)StepInterval));
        }
        start.Add(HarpMessage.FromByte(AccumulatedStepsSamplingRate, MessageType.Write, 1));
        start.Add(HarpMessage.FromByte(EnableDigitalInputs, MessageType.Write, (byte)WiredInputs));
        for (var n = 0; n < 4; n++)
        {
            if ((WiredInputs & (1 << n)) != 0 && (WiredMotors & (1 << n)) != 0)
                start.Add(HarpMessage.FromByte(Input0OpMode + n, MessageType.Write, (byte)(32 + n)));
        }
        return start;
    }

    // Move <Device> <Motor> <steps>, such as StepperDriver-A Motor1 -150: that many steps from where the motor is,
    // for a motor the wiring names; any other is ignored, said on stderr.
    IObservable<HarpMessage> Moves(IObservable<string> moves)
    {
        var name = Device;
        var wired = WiredMotors;
        return moves.SelectMany(text =>
        {
            var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 || parts[0] != name) return Observable.Empty<HarpMessage>();
            int n, steps;
            var motor = parts[1].ToLowerInvariant();
            if (!motor.StartsWith("motor") || !int.TryParse(motor.Substring(5), out n) || n < 0 || n > 3 || (wired & (1 << n)) == 0
                || !int.TryParse(parts[2], NumberStyles.Integer, Preflight.Invariant, out steps))
            {
                Console.Error.WriteLine("preflight: Move '" + text + "' ignored: give the driver, a motor its wiring names, and whole steps");
                return Observable.Empty<HarpMessage>();
            }
            return Observable.Return(HarpMessage.FromInt32(Motor0MoveRelative + n, MessageType.Write, steps));
        });
    }

    // The driver's digital inputs as it last said them (DigitalInputState bits, Input0 = 1); -1 before it has.
    class Inputs
    {
        public int Bits = -1;
    }

    // Home <Device> <Motor>, such as StepperDriver-A Motor1: as av-dome zeroes a motor (ZEROingMotors.bonsai). Off its
    // home switch first if on it (1000 steps at a time), then towards zero until the switch stops it
    // (StopMotor<n>OnFalling, set at open), then its position set to 0. Only a motor whose input the wiring names too.
    IObservable<HarpMessage> Homes(IObservable<string> homes)
    {
        var name = Device;
        var wired = WiredMotors & WiredInputs;
        return Observable.Create<HarpMessage>(observer =>
        {
            var inputs = new Inputs();
            var watch = DeviceHub<HarpMessage>.Items
                .Where(item => item.Item1 == name && item.Item2.Address == DigitalInputState && item.Item2.MessageType != MessageType.Write)
                .Subscribe(item => Volatile.Write(ref inputs.Bits, item.Item2.GetPayloadByte()));
            var runs = homes.SelectMany(text =>
            {
                var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || parts[0] != name) return Observable.Empty<HarpMessage>();
                int n;
                var motor = parts[1].ToLowerInvariant();
                if (!motor.StartsWith("motor") || !int.TryParse(motor.Substring(5), out n) || n < 0 || n > 3 || (wired & (1 << n)) == 0)
                {
                    Console.Error.WriteLine("preflight: Home '" + text + "' ignored: give the driver and a motor whose motor and input its wiring names");
                    return Observable.Empty<HarpMessage>();
                }
                return Home(name, n, inputs);
            }).Subscribe(observer);
            return new CompositeDisposable(watch, runs);
        });
    }

    static IObservable<HarpMessage> Home(string device, int n, Inputs inputs)
    {
        return Observable.Create<HarpMessage>(async (observer, token) =>
        {
            var bit = 1 << n;
            Func<int> bits = () => Volatile.Read(ref inputs.Bits);
            var clock = Stopwatch.StartNew();
            // Asked, as av-dome asks for them at start: the first state may have come before this listened.
            if (bits() < 0) observer.OnNext(HarpMessage.FromByte(DigitalInputState, MessageType.Read, 0));
            while (bits() < 0 && clock.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(50, token);
            if (bits() < 0)
            {
                Console.Error.WriteLine("preflight: " + device + " Motor" + n + " not homed: the driver has not said its inputs");
                return;
            }
            for (var i = 0; i < 10 && (bits() & bit) == 0; i++)
            {
                observer.OnNext(HarpMessage.FromInt32(Motor0MoveRelative + n, MessageType.Write, 1000));
                await Task.Delay(1000, token);
            }
            if ((bits() & bit) == 0)
            {
                Console.Error.WriteLine("preflight: " + device + " Motor" + n + " not homed: still on its home switch after 10000 steps off it");
                return;
            }
            observer.OnNext(HarpMessage.FromInt32(Motor0MoveRelative + n, MessageType.Write, -100000));
            clock.Restart();
            while ((bits() & bit) != 0)
            {
                if (clock.Elapsed > TimeSpan.FromSeconds(60))
                {
                    observer.OnNext(HarpMessage.FromByte(StopMotors, MessageType.Write, (byte)bit));
                    Console.Error.WriteLine("preflight: " + device + " Motor" + n + " not homed: its home switch was not reached in 60 s, so it was stopped");
                    return;
                }
                await Task.Delay(20, token);
            }
            await Task.Delay(200, token);
            observer.OnNext(HarpMessage.FromInt32(Motor0AccumulatedSteps + n, MessageType.Write, 0));
            Console.Error.WriteLine("preflight: " + device + " Motor" + n + " homed");
        });
    }

    // A DO line's pulse timed by the board itself (audit, 11 Nov 2026): its PulseDO<n> width, the line in
    // OutputPulseEnable, then OutputSet, as OpenRigs.Devices PulseController's calibrated pulse fires one.
    // Experiments leave OutputPulseEnable set with a reward's width, so a line set and cleared from the PC
    // gave that width instead (Flush, one drop); and a pulse timed here closes even if the workflow stops
    // mid-pulse, where a clear sent from the PC never came.
    IObservable<HarpMessage> Pulses(IObservable<string> commands, bool board)
    {
        var name = Device;
        var widths = Widths();
        ushort enabled = 0;  // the DO lines this run has pulsed, each with its width written
        return commands.SelectMany(text =>
        {
            var parts = (text ?? string.Empty).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (!board || parts.Length == 0 || parts[0] != name) return Observable.Empty<HarpMessage>();
            if (parts.Length < 2 || parts.Length > 3)
            {
                Console.Error.WriteLine("preflight: Pulse '" + text + "' ignored: give the board, the line and, optionally, a width in ms");
                return Observable.Empty<HarpMessage>();
            }
            var mask = Mask(parts[1]);
            if (mask == 0)
            {
                Console.Error.WriteLine("preflight: Pulse '" + text + "' ignored: the line is DO0 to DO3 or DOPort0 to DOPort2");
                return Observable.Empty<HarpMessage>();
            }
            double ms;
            if (parts.Length == 3)
            {
                // A width given with the line (Pip, 11 Nov 2026: Flush, a valve open 10 s): 1 ms to 60 s.
                if (!double.TryParse(parts[2], NumberStyles.Float, Preflight.Invariant, out ms) || ms < 1 || ms > 60000)
                {
                    Console.Error.WriteLine("preflight: Pulse '" + text + "' ignored: the width is 1 to 60000 ms");
                    return Observable.Empty<HarpMessage>();
                }
            }
            else if (!widths.TryGetValue(parts[1], out ms)) ms = 50;
            if (mask >= 0x400)
            {
                var n = 0;
                while ((0x400 << n) != mask) n++;
                enabled |= mask;
                return Observable.Return(HarpMessage.FromUInt16(PulseDO0 + n, MessageType.Write, Width(ms)))
                    .Concat(Observable.Return(HarpMessage.FromUInt16(PulseEnableRegister, MessageType.Write, enabled)))
                    .Concat(Observable.Return(HarpMessage.FromUInt16(OutputSet, MessageType.Write, mask)));
            }
            // A DOPort line has no pulse register: set, then cleared from here.
            return Observable.Return(HarpMessage.FromUInt16(OutputSet, MessageType.Write, mask))
                .Concat(Observable.Timer(TimeSpan.FromMilliseconds(ms)).Select(_ => HarpMessage.FromUInt16(OutputClear, MessageType.Write, mask)));
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

/// <summary>
/// A Harp board's port opened again when Windows refuses it. Bonsai.Harp's Device opens the port,
/// reads the board's identity, closes it and reopens it at once; on a busy rig the reopen can find
/// the port not yet released and fail with Access to the port is denied (BLUE-OPTO-1, 11 Nov 2026:
/// a different board each run). Waits, then subscribes to the Device again, up to Attempts in all.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Subscribes to a Harp Device again after Access to the port is denied, up to Attempts in all.")]
public class RetryPortOpen
{
    int attempts = 4;
    double delaySeconds = 0.5;

    /// <summary>The device's name, for the line on stderr.</summary>
    public string Device { get; set; }
    /// <summary>Tries in all, the first included.</summary>
    public int Attempts { get { return attempts; } set { attempts = value; } }
    /// <summary>Seconds between tries.</summary>
    public double DelaySeconds { get { return delaySeconds; } set { delaySeconds = value; } }

    public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
    {
        return Observable.Defer(() => Attempt(source, Device, 1, Attempts, TimeSpan.FromSeconds(DelaySeconds)));
    }

    static IObservable<TSource> Attempt<TSource>(IObservable<TSource> source, string device, int attempt, int attempts, TimeSpan delay)
    {
        return source.Catch<TSource, Exception>(e =>
        {
            var inner = e;
            while (inner.InnerException != null && (inner is AggregateException || inner is System.Reflection.TargetInvocationException)) inner = inner.InnerException;
            if (!(inner is UnauthorizedAccessException) || attempt >= attempts) return Observable.Throw<TSource>(e);
            Console.Error.WriteLine("preflight: " + device + " port refused on try " + attempt + " of " + attempts + "; trying again");
            return Observable.Timer(delay).SelectMany(_ => Attempt(source, device, attempt + 1, attempts, delay));
        });
    }
}

// Live view (11 Nov 2026). StreamDevices streams <Device>.Data subjects it finds when the workflow
// is built; this workflow opens its devices inside SelectManys, once per rig.yml entry. So each
// branch hands its device's data, by name, to one hub per data type (ShareDeviceData), and a
// top-level source per name the lab's workflows use (HarpDeviceData, CameraDeviceData) takes its
// own back out, into a <Device>.Data subject that StreamDevices finds. A name this rig.yml lacks is
// a source that never emits, which StreamDevices does not look at; a rig.yml device with no source
// here is in its skipped list.
static class DeviceHub<T>
{
    public static readonly ISubject<Tuple<string, T>, Tuple<string, T>> Items = Subject.Synchronize(new Subject<Tuple<string, T>>());
}

/// <summary>Passes the device's data on unchanged, and hands each item, with the device's name, to the live view's hub.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Passes the device's data on unchanged and hands it, by the device's name, to HarpDeviceData or CameraDeviceData for the live view.")]
public class ShareDeviceData
{
    /// <summary>The device's name.</summary>
    public string Device { get; set; }

    public IObservable<TSource> Process<TSource>(IObservable<TSource> source)
    {
        return Observable.Defer(() =>
        {
            var name = Device;
            return source.Do(item => DeviceHub<TSource>.Items.OnNext(Tuple.Create(name, item)));
        });
    }
}

/// <summary>A Harp board's messages, from its branch's ShareDeviceData; nothing when this rig has no board of this name.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Source)]
[Description("A Harp board's messages by its name, from its branch's ShareDeviceData; nothing when this rig has no board of this name.")]
public class HarpDeviceData
{
    /// <summary>The board's name, such as BehaviorBoard-Main.</summary>
    public string Device { get; set; }

    public IObservable<HarpMessage> Process()
    {
        return Observable.Defer(() =>
        {
            var name = Device;
            return DeviceHub<HarpMessage>.Items.Where(item => item.Item1 == name).Select(item => item.Item2);
        });
    }
}

/// <summary>A camera's frames, from its branch's ShareDeviceData; nothing when this rig has no camera of this name.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Source)]
[Description("A camera's frames by its name, from its branch's ShareDeviceData; nothing when this rig has no camera of this name.")]
public class CameraDeviceData
{
    /// <summary>The camera's name, such as Camera-Face.</summary>
    public string Device { get; set; }

    public IObservable<Bonsai.Spinnaker.SpinnakerDataFrame> Process()
    {
        return Observable.Defer(() =>
        {
            var name = Device;
            return DeviceHub<Bonsai.Spinnaker.SpinnakerDataFrame>.Items.Where(item => item.Item1 == name).Select(item => item.Item2);
        });
    }
}
