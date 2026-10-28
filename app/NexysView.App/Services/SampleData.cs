using System.IO;
using NexysView.Core.Tdms;

namespace NexysView.App.Services;

/// <summary>Built-in sample: a small synthetic TDMS (20 s @ 1 kHz, 8 channels) written once under %LOCALAPPDATA%\NexysView\sample\
/// and opened at start when no file was given on the command line, so a first start has something to look at (beta feedback, 0.9.2).
/// Under --fresh / automation the file goes to %TEMP% so user data is never touched. Regenerated when <see cref="Revision"/> changes.</summary>
public static class SampleData
{
    public const int Revision = 1;
    public const string FileName = "Sample.tdms";

    public static string Dir => AppPaths.SampleDir;

    /// <summary>Path of the sample file, writing it if missing or outdated; null if it could not be written.</summary>
    public static string? Ensure()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, FileName), stamp = Path.Combine(Dir, "Sample.rev");
            if (File.Exists(path) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == Revision.ToString()) return path;
            Write(path);
            File.WriteAllText(stamp, Revision.ToString());
            return path;
        }
        catch (Exception ex) { Diag.Trace("sample: " + ex.Message); return null; }
    }

    static void Write(string path)
    {
        const int n = 20000; const double fs = 1000.0;   // 20 s @ 1 kHz
        var rnd = new Random(20261026);
        var time = new double[n]; var sine1 = new double[n]; var sine5 = new double[n]; var ramp = new double[n]; var temp = new double[n];
        var square = new int[n]; var counter = new int[n]; var state = new byte[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / fs; time[i] = t;
            sine1[i] = Math.Round(5.0 * Math.Sin(2 * Math.PI * 1.0 * t), 4);
            sine5[i] = Math.Round(2.0 * Math.Sin(2 * Math.PI * 5.0 * t) + 0.3 * (rnd.NextDouble() - 0.5), 4);
            ramp[i] = Math.Round(100.0 * (t % 5.0) / 5.0, 3);
            temp[i] = Math.Round(20.0 + 5.0 * (1 - Math.Exp(-t / 8.0)) + 0.05 * (rnd.NextDouble() - 0.5), 3);
            square[i] = ((int)(t / 1.0)) % 2;                 // 0/1, 1 s high / 1 s low
            counter[i] = (int)t;                              // +1 every second
            state[i] = (byte)(t < 2 ? 0 : t < 15 ? 1 : t < 18 ? 2 : 3);   // 0 idle, 1 run, 2 hold, 3 done
        }
        Dictionary<string, object> P(string unit, string desc) => new() { ["unit_string"] = unit, ["description"] = desc };
        var channels = new List<TdmsWriter.ChannelData>
        {
            new("Sample", "Time", TdsType.Double, time, P("s", "time base, 1 kHz")),
            new("Sample", "Sine 1 Hz", TdsType.Double, sine1, P("V", "5 V amplitude")),
            new("Sample", "Sine 5 Hz", TdsType.Double, sine5, P("V", "2 V amplitude + noise")),
            new("Sample", "Ramp", TdsType.Double, ramp, P("mm", "0-100 mm sawtooth, 5 s period")),
            new("Sample", "Temperature", TdsType.Double, temp, P("degC", "first-order rise 20 -> 25")),
            new("Sample", "Square", TdsType.I32, square, P("", "0/1, 2 s period")),
            new("Sample", "Counter", TdsType.I32, counter, P("", "+1 per second")),
            new("Sample", "State", TdsType.U8, state, P("", "0 idle, 1 run, 2 hold, 3 done")),
        };
        var fileProps = new Dictionary<string, object> { ["name"] = "Sample", ["description"] = "Synthetic sample written by Nexys VIEW (20 s @ 1 kHz). Drop your own .tdms / .tdm / .csv files anywhere in the window." };
        var groupProps = new Dictionary<string, IReadOnlyDictionary<string, object>> { ["Sample"] = new Dictionary<string, object> { ["description"] = "synthetic signals" } };
        using var w = new TdmsWriter(path);
        w.WriteSegment(fileProps, groupProps, channels);
    }
}
