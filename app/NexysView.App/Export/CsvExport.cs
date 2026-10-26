using System.Globalization;
using System.IO;
using System.Text;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App.Export;

/// <summary>0.9.3: CSV of a sample range of several channels — columns index, x (of the chart's x mapping of the first channel,
/// else the first channel's own x), then one column per channel ("name [unit]"); invariant culture, round-trip doubles.</summary>
public static class CsvExport
{
    public static int Write(string path, IReadOnlyList<Channel> chans, long first, long last, Curve? xCurve)
    {
        const int block = 65536;
        using var fs = new StreamWriter(path, false, new UTF8Encoding(false), 1 << 20);
        var sb = new StringBuilder();
        sb.Append("index,x");
        string xUnit = xCurve?.XChannel?.Unit ?? chans[0].XUnit; if (!string.IsNullOrEmpty(xUnit)) sb.Append(" [").Append(xUnit).Append(']');
        foreach (var c in chans) { sb.Append(',').Append(c.Name.Replace(',', ';')); if (!string.IsNullOrEmpty(c.Unit)) sb.Append(" [").Append(c.Unit).Append(']'); }
        fs.WriteLine(sb.ToString());
        var bufs = chans.Select(_ => new double[block]).ToArray();
        var xb = new double[block];
        for (long pos = first; pos <= last; pos += block)
        {
            int n = (int)Math.Min(block, last - pos + 1);
            for (int k = 0; k < chans.Count; k++) { int got = chans[k].Length > pos ? chans[k].Source.Read(pos, bufs[k].AsSpan(0, (int)Math.Min(n, chans[k].Length - pos))) : 0; for (int i = got; i < n; i++) bufs[k][i] = double.NaN; }
            if (xCurve != null && xCurve.XMonotonic && xCurve.XChannel != null) { int got = xCurve.XChannel.Source.Read(pos, xb.AsSpan(0, n)); for (int i = 0; i < n; i++) xb[i] = i < got ? xb[i] * xCurve.XScale + xCurve.XOffset : double.NaN; }
            else for (int i = 0; i < n; i++) xb[i] = chans[0].XAt(pos + i) + (xCurve?.XOffset ?? 0);
            for (int i = 0; i < n; i++)
            {
                sb.Clear(); sb.Append(pos + i).Append(',').Append(F(xb[i]));
                for (int k = 0; k < chans.Count; k++) sb.Append(',').Append(F(bufs[k][i]));
                fs.WriteLine(sb.ToString());
            }
        }
        return chans.Count;
    }

    static string F(double v) => double.IsNaN(v) ? "" : v.ToString("R", CultureInfo.InvariantCulture);
}
