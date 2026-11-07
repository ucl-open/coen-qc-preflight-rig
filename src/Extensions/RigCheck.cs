using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Bonsai;
using YamlDotNet.RepresentationModel;

// PROTOTYPE (7 Nov 2026): if the one-workflow design holds on a rig, RigCheckDevices belongs beside
// lib-open-rigs' Shared\RigDevices.cs, whose rule it copies, and CheckDevice in a lab package, both
// with tests. C# 5: no interpolation, no ?. and no property initialisers.

/// <summary>One entry of rig.yml the check covers: a device (kind: instance: settings) or a single section.</summary>
public class RigCheckDevice
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
}

/// <summary>One raw measurement, a row of the check's CSV: Device,Measure,Value,Detail.</summary>
public class RigCheckRow
{
    /// <summary>The device's name.</summary>
    public string Device { get; set; }
    /// <summary>What was measured: messages, frames, seconds, error.</summary>
    public string Measure { get; set; }
    /// <summary>The number measured.</summary>
    public string Value { get; set; }
    /// <summary>An error's message, with no commas or line breaks.</summary>
    public string Detail { get; set; }
}

/// <summary>The rig.yml entries of the kinds named, in file order; nothing when the rig declares none of them.</summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Source)]
[Description("The rig.yml entries of the kinds named, in file order; nothing when the rig declares none of them.")]
public class RigCheckDevices
{
    string configRoot = @"C:\Coen-Lab-Local\RigConfigs";

    /// <summary>The folder holding &lt;MACHINE&gt;\rig.yml.</summary>
    [Description("The folder holding <MACHINE>\\rig.yml.")]
    public string ConfigRoot { get { return configRoot; } set { configRoot = value; } }

    /// <summary>The rig.yml keys to list, separated by commas, such as camera.</summary>
    [Description("The rig.yml keys to list, separated by commas, such as camera.")]
    public string Kinds { get; set; }

    /// <summary>Reads rig.yml when subscribed; a missing or unreadable rig.yml is an error: the check cannot run.</summary>
    public IObservable<RigCheckDevice> Process()
    {
        return Observable.Defer(() => Read().ToObservable());
    }

    List<RigCheckDevice> Read()
    {
        var path = Path.Combine(Environment.ExpandEnvironmentVariables(ConfigRoot), Environment.MachineName, "rig.yml");
        if (!File.Exists(path)) throw new FileNotFoundException("The rig check needs a rig.yml, and there is none at " + path, path);
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = yaml.Documents.Count > 0 ? yaml.Documents[0].RootNode as YamlMappingNode : null;
        if (root == null) throw new InvalidOperationException(path + " is empty or not a set of settings");
        var kinds = new HashSet<string>((Kinds ?? string.Empty).Split(',').Select(k => k.Trim()).Where(k => k.Length > 0));
        var devices = new List<RigCheckDevice>();
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

    static RigCheckDevice Device(string kind, string instance, string name, YamlMappingNode settings)
    {
        var device = new RigCheckDevice();
        device.Kind = kind;
        device.Instance = instance;
        device.Name = name;
        device.PortName = Setting(settings, "portName");
        device.SerialNumber = Setting(settings, "serialNumber");
        device.MeshMapPath = Setting(settings, "meshMapPath");
        device.GammaLutPath = Setting(settings, "gammaLutPath");
        return device;
    }

    static string Setting(YamlMappingNode settings, string key)
    {
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
/// Counts what a device sends in a window after a warm-up, as rows: the count and the window's seconds;
/// or one error row with the message when the device fails, so one dead device never ends the check.
/// </summary>
[Combinator]
[WorkflowElementCategory(ElementCategory.Combinator)]
[Description("Counts what a device sends in a window after a warm-up; an error becomes an error row.")]
public class CheckDevice
{
    static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    string measure = "messages";
    double warmup = 1;
    double seconds = 5;

    /// <summary>The device's name, the rows' Device.</summary>
    public string Device { get; set; }

    /// <summary>What is counted: messages, frames.</summary>
    public string Measure { get { return measure; } set { measure = value; } }

    /// <summary>Seconds after subscribing before counting starts.</summary>
    public double Warmup { get { return warmup; } set { warmup = value; } }

    /// <summary>Seconds counted.</summary>
    public double Seconds { get { return seconds; } set { seconds = value; } }

    /// <summary>The device's rows.</summary>
    public IObservable<RigCheckRow> Process<TSource>(IObservable<TSource> source)
    {
        // Read at subscription: a PropertyMapping sets Device after the workflow is built.
        return Observable.Defer(() => Measured(source, Device, Measure, Warmup, Seconds));
    }

    static IObservable<RigCheckRow> Measured<TSource>(IObservable<TSource> source, string device, string what, double warmup, double seconds)
    {
        var start = TimeSpan.FromSeconds(warmup);
        var end = TimeSpan.FromSeconds(warmup + seconds);
        var window = seconds.ToString(CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        // Ends on the source's own thread, at its first item past the window: ended from a timer, the
        // projector's render chain was disposed off the GL thread and BonVision's RenderTexture
        // crashed the process deleting its framebuffer (AccessViolationException, 7 Nov 2026). The
        // timer only ends a source that goes quiet.
        return source
            .Select(item => clock.Elapsed)
            .TakeWhile(elapsed => elapsed < end)
            .TakeUntil(Observable.Timer(end + Quiet))
            .Where(elapsed => elapsed >= start)
            .Count()
            .SelectMany(n => new[] { Row(device, what, n.ToString(CultureInfo.InvariantCulture), string.Empty), Row(device, "seconds", window, string.Empty) })
            .Catch<RigCheckRow, Exception>(error => Observable.Return(Row(device, "error", string.Empty, Clean(error))))
            // Each row on stderr as it is made: CsvWriter reaches disk only when the workflow ends.
            .Do(row => Console.Error.WriteLine("rig check: " + row.Device + " " + row.Measure + " " + row.Value + " " + row.Detail));
    }

    static RigCheckRow Row(string device, string measure, string value, string detail)
    {
        var row = new RigCheckRow();
        row.Device = device;
        row.Measure = measure;
        row.Value = value;
        row.Detail = detail;
        return row;
    }

    static string Clean(Exception error)
    {
        var inner = error;
        while (inner.InnerException != null && (inner is AggregateException || inner is System.Reflection.TargetInvocationException)) inner = inner.InnerException;
        return inner.Message.Replace(',', ';').Replace('\r', ' ').Replace('\n', ' ').Replace("\"", "'").Trim();
    }
}
