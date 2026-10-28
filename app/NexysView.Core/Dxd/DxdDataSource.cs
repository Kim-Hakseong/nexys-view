using NexysView.Core.Data;

namespace NexysView.Core.Dxd;

/// <summary>.dxd (DXD logger format) as a DataFile. Channels are decoded on open (files are small); equidistant time → implicit x,
/// otherwise an explicit time channel. The reader (DxdReader/MultiStream) lives in this project since 0.10.0 (moved from the sibling
/// logger project; bit-equality against its output is kept by NexysView.Tests DxdTests).</summary>
public static class DxdDataSource
{
    public const string Format = "DXD";

    sealed class ArrSrc : IValueSource
    {
        readonly double[] _v; public ArrSrc(double[] v) { _v = v; }
        public long Length => _v.Length;
        public int Read(long first, Span<double> dst) { int n = (int)Math.Max(0, Math.Min(dst.Length, _v.Length - first)); _v.AsSpan((int)first, n).CopyTo(dst); return n; }
    }

    public static DataFile Open(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = new DxdReader(path);
        var props = new Dictionary<string, object> { ["sample_rate"] = r.SampleRate, ["block_size"] = r.BlockSize, ["start_store_time"] = r.StartStoreTime, ["sync_count"] = r.SyncCount };
        var df = new DataFile(path, Format, props, null);
        var g = new ChannelGroup(Path.GetFileNameWithoutExtension(path), new Dictionary<string, object>());
        df.Groups.Add(g);
        foreach (var c in r.Channels)
        {
            DxdSamples s; double[] vals;
            try { s = r.Read(c); vals = r.Scaled(c, s); }
            catch (Exception ex) { df.Warnings.Add($"{c.Name}: {ex.Message}"); continue; }
            var cp = new Dictionary<string, object> { ["dxd_index"] = c.IndexStr ?? "", ["dxd_pos"] = c.Pos, ["dxd_async"] = c.IsAsync };
            var ch = new Channel(df.Name, g.Name, c.Name, new ArrSrc(vals), cp) { Unit = c.Unit ?? "", DataType = "DBL", XUnit = "s" };
            var t = s.Times;
            if (t.Length >= 2)
            {
                double inc = (t[^1] - t[0]) / (t.Length - 1); bool eq = inc > 0;
                for (int i = 1; i < t.Length && eq; i++) if (Math.Abs(t[i] - (t[0] + i * inc)) > Math.Abs(inc) * 1e-3) eq = false;
                if (eq) { ch.XKind = XKind.Implicit; ch.XStart = t[0]; ch.XIncrement = inc; }
                else
                {
                    var tc = new Channel(df.Name, g.Name, c.Name + ".time", new ArrSrc(t), new Dictionary<string, object>()) { Unit = "s", DataType = "DBL" };
                    ch.XKind = XKind.Explicit; ch.XChannel = tc;
                }
            }
            g.Channels.Add(ch);
        }
        df.OpenTime = sw.Elapsed;
        return df;
    }
}
