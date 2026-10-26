using NexysView.Core.Data;

namespace NexysView.App.Services;

/// <summary>File comparison helpers (v3 B4): absolute file start, alignment offsets, common channel names.</summary>
public static class Compare
{
    /// <summary>Absolute start of a file in seconds (1904 epoch): first value of an "Absolute Time" channel, else a timestamp
    /// file property (wf_start_time / datetime / Created), else NaN.</summary>
    public static double FileStart(DataFile f)
    {
        var abs = f.AllChannels.FirstOrDefault(c => !c.IsText && c.Name.Contains("Absolute", StringComparison.OrdinalIgnoreCase) && XIndex.TimeScale(c.Unit) > 0 && c.Length > 0);
        if (abs != null) { var (a, _) = XIndex.Range(abs); if (double.IsFinite(a)) return a * XIndex.TimeScale(abs.Unit); }
        foreach (var kv in f.Properties) if (kv.Value is Core.Tdms.TdmsTimestamp ts) return ts.ToUnixSeconds() - XIndex.Epoch1904ToUnix;
        foreach (var ch in f.AllChannels) foreach (var kv in ch.Properties) if (kv.Key == "wf_start_time" && kv.Value is Core.Tdms.TdmsTimestamp ts2) return ts2.ToUnixSeconds() - XIndex.Epoch1904ToUnix;
        return double.NaN;
    }

    /// <summary>Per-file x offsets (seconds) for a comparison: start = absolute start difference to the first file,
    /// zero = every file from 0 s, manual = file index × offsetMs. <paramref name="x0"/> = first x value of each file (seconds, NaN when none).</summary>
    public static double[] AlignOffsets(string mode, double offsetMs, double[] x0, double[] absStart)
    {
        var off = new double[x0.Length];
        for (int i = 0; i < x0.Length; i++)
        {
            double first = double.IsNaN(x0[i]) ? 0 : x0[i];
            off[i] = mode switch
            {
                "zero" => -first,
                "manual" => -first + i * offsetMs / 1000.0,
                _ => -first + (double.IsNaN(absStart[i]) || double.IsNaN(absStart[0]) ? 0 : absStart[i] - absStart[0]),
            };
        }
        return off;
    }

    /// <summary>Channel names present in every file (numeric channels only), in first-file order.</summary>
    public static List<string> CommonChannels(IReadOnlyList<DataFile> files)
    {
        if (files.Count == 0) return new();
        var sets = files.Select(f => f.AllChannels.Where(c => !c.IsText).Select(c => c.Name).ToHashSet()).ToList();
        return files[0].AllChannels.Where(c => !c.IsText).Select(c => c.Name).Distinct().Where(n => sets.All(s => s.Contains(n))).ToList();
    }

    /// <summary>Time channel of a file (SuggestX over the file's own channels) and its duration in seconds.</summary>
    public static (Channel? time, double duration, double rate) TimeInfo(DataFile f)
    {
        var first = f.AllChannels.FirstOrDefault(c => !c.IsText);
        if (first == null) return (null, double.NaN, double.NaN);
        var t = XIndex.SuggestX(f.AllChannels, first.Length);
        if (t != null)
        {
            var (a, b) = XIndex.Range(t); double s = XIndex.TimeScale(t.Unit);
            double dur = (b - a) * s;
            return (t, dur, t.Length > 1 && dur > 0 ? (t.Length - 1) / dur : double.NaN);
        }
        var imp = f.AllChannels.FirstOrDefault(c => c.XKind == XKind.Implicit && c.XIncrement > 0);
        if (imp != null) return (null, imp.XIncrement * Math.Max(0, imp.Length - 1), 1 / imp.XIncrement);
        return (null, double.NaN, double.NaN);
    }
}
