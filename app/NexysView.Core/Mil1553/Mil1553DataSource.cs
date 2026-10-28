using NexysView.Core.Data;

namespace NexysView.Core.Mil1553;

/// <summary>Opens a 1553 bus-monitor capture as a DataFile: one "1553" group with per-record columns (time, rt, sa, tr, wc, bus,
/// error, activity, count, resp) and the 32 data words as channels (NaN where the record is shorter), plus — when an ICD hub is
/// attached — one group per ICD message with engineering-unit word channels on that message's own time base.</summary>
public static class Mil1553DataSource
{
    public const string Format = "MIL1553";

    sealed class Col : IValueSource
    {
        readonly Mil1553File _f; readonly Func<Mil1553File, int, double> _get; readonly int[]? _idx;
        public Col(Mil1553File f, Func<Mil1553File, int, double> get, int[]? idx = null) { _f = f; _get = get; _idx = idx; }
        public long Length => _idx?.Length ?? _f.Count;
        public int Read(long first, Span<double> dst)
        {
            long n = Math.Min(dst.Length, Length - first); if (n <= 0) return 0;
            for (int k = 0; k < n; k++) { int i = _idx != null ? _idx[first + k] : (int)(first + k); dst[k] = _get(_f, i); }
            return (int)n;
        }
    }

    public static DataFile Open(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = new Mil1553File(path);
        var props = new Dictionary<string, object> { ["records"] = f.Count, ["record_bytes"] = Mil1553File.RecordSize, ["metadata_bytes"] = f.Metadata.Length };
        if (f.Count > 0) { props["t0_us"] = f.TimeUs[0]; props["t1_us"] = f.TimeUs[f.Count - 1]; }
        var df = new DataFile(path, Format, props, f);
        _files.Add(df, f);
        var g = new ChannelGroup("1553", new Dictionary<string, object> { ["bus"] = "MIL-STD-1553B" });
        df.Groups.Add(g);
        double t0 = f.Count > 0 ? f.TimeUs[0] : 0;
        var time = Ch(df, g, "time", new Col(f, (x, i) => (x.TimeUs[i] - t0) * 1e-6), "s", "time since first record");
        time.XKind = XKind.Index;
        void add(string name, Func<Mil1553File, int, double> get, string unit, string desc, bool step = true)
        {
            var c = Ch(df, g, name, new Col(f, get), unit, desc); c.XKind = XKind.Explicit; c.XChannel = time; c.XUnit = "s"; c.DataType = step ? "U16" : "DBL";
        }
        add("rt", (x, i) => (x.Cwd1[i] >> 11) & 0x1f, "", "remote terminal address (cwd1)");
        add("sa", (x, i) => (x.Cwd1[i] >> 5) & 0x1f, "", "subaddress / mode (cwd1)");
        add("tr", (x, i) => (x.Cwd1[i] >> 10) & 1, "", "transmit/receive bit (1 = RT transmits)");
        add("wc", (x, i) => x.Cwd1[i] & 0x1f, "", "word count field (0 = 32)");
        add("bus", (x, i) => x.Activity[i] & 1, "", "0 = bus A, 1 = bus B");
        add("error", (x, i) => x.Error[i] & ~1, "", "error flags without HIT");
        add("activity", (x, i) => x.Activity[i], "", "activity flags");
        add("count", (x, i) => x.DataCount[i], "", "data words in the record");
        add("resp1", (x, i) => x.Get(i).RespTime1, "0.1us", "response time 1");
        add("swd1", (x, i) => x.Swd1[i], "", "status word 1");
        for (int w = 0; w < 32; w++)
        {
            int ww = w;
            add($"w{w + 1:00}", (x, i) => { int v = x.Word(i, ww); return v < 0 ? double.NaN : v; }, "", $"data word {w + 1} (raw)");
        }
        df.OpenTime = sw.Elapsed;
        return df;
    }

    /// <summary>Add engineering-unit groups from an ICD hub: for each message a group "ICD:name" with its own time channel and one
    /// channel per word (scale*raw+offset, bits extracted from the data words). Returns the number of channels added.</summary>
    public static int AttachIcd(DataFile df, IcdHub hub)
    {
        var file = FileOf(df); if (file == null) return 0;
        df.Groups.RemoveAll(g => g.Name.StartsWith("ICD:", StringComparison.Ordinal));
        int added = 0;
        foreach (var m in hub.Messages)
        {
            var idx = file.Filter(m.Rt, m.Sa, m.Tr);
            if (idx.Length == 0) continue;
            var g = new ChannelGroup("ICD:" + m.Name, new Dictionary<string, object> { ["rt"] = m.Rt, ["sa"] = m.Sa, ["tr"] = m.Tr, ["wc"] = m.Wc, ["records"] = idx.Length });
            df.Groups.Add(g);
            double t0 = file.Count > 0 ? file.TimeUs[0] : 0;
            var time = Ch(df, g, "time", new Col(file, (x, i) => (x.TimeUs[i] - t0) * 1e-6, idx), "s", "message time"); time.XKind = XKind.Index;
            foreach (var w in m.Words)
            {
                var ww = w;
                var c = Ch(df, g, w.Name, new Col(file, (x, i) => ww.Decode(x, i), idx), w.Unit, $"word {w.Index} bits {w.BitStart}..{w.BitStart + w.BitLen - 1} {w.Type} scale {w.Scale} offset {w.Offset}");
                c.XKind = XKind.Explicit; c.XChannel = time; c.XUnit = "s"; c.DataType = "DBL"; added++;
            }
        }
        return added;
    }

    static Channel Ch(DataFile df, ChannelGroup g, string name, IValueSource src, string unit, string desc)
    {
        var c = new Channel(df.Name, g.Name, name, src, new Dictionary<string, object>()) { Unit = unit, Description = desc };
        g.Channels.Add(c); return c;
    }

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataFile, Mil1553File> _files = new();
    /// <summary>The underlying capture of a DataFile opened by this source (null otherwise).</summary>
    public static Mil1553File? FileOf(DataFile df) => _files.TryGetValue(df, out var f) ? f : null;
}
