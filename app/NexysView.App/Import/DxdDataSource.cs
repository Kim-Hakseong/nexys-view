#if DXD
using System.IO;
using MiniMeas.Core.Dxd;
using NexysView.Core.Data;

namespace NexysView.App.Import;

/// <summary>DXD .dxd via MiniMeas.Core (read-only reuse). Channels are decoded on open (files are small);
/// equidistant time → implicit x, otherwise an explicit time channel.</summary>
public static class DxdDataSource
{
    public static DataFile Open(string path)
    {
        var r = new DxdReader(path);
        var df = new DataFile(path, "DXD", new Dictionary<string, object>(), null);
        var g = new ChannelGroup(Path.GetFileNameWithoutExtension(path), new Dictionary<string, object>());
        df.Groups.Add(g);
        foreach (var c in r.Channels)
        {
            DxdSamples s;
            double[] vals;
            try { s = r.Read(c); vals = r.Scaled(c, s); }
            catch { continue; }
            var props = new Dictionary<string, object> { ["dxd_index"] = c.IndexStr ?? "", ["dxd_pos"] = c.Pos, ["dxd_async"] = c.IsAsync };
            var ch = new Channel(df.Name, g.Name, c.Name, new ArraySource(vals), props) { Unit = c.Unit ?? "", DataType = "DBL", XUnit = "s" };
            var t = s.Times;
            if (t.Length >= 2)
            {
                double inc = (t[^1] - t[0]) / (t.Length - 1); bool eq = inc > 0;
                for (int i = 1; i < t.Length && eq; i++) if (Math.Abs(t[i] - (t[0] + i * inc)) > Math.Abs(inc) * 1e-3) eq = false;
                if (eq) { ch.XKind = XKind.Implicit; ch.XStart = t[0]; ch.XIncrement = inc; }
                else
                {
                    var tc = new Channel(df.Name, g.Name, c.Name + ".time", new ArraySource(t), new Dictionary<string, object>()) { Unit = "s", DataType = "DBL" };
                    ch.XKind = XKind.Explicit; ch.XChannel = tc;
                }
            }
            g.Channels.Add(ch);
        }
        return df;
    }
}
#endif
