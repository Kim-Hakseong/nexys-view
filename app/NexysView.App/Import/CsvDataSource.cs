using System.Globalization;
using System.IO;
using NexysView.Core.Data;

namespace NexysView.App.Import;

/// <summary>In-memory value source.</summary>
public sealed class ArraySource : IValueSource
{
    public double[] Data;
    public ArraySource(double[] d) => Data = d;
    public long Length => Data.Length;
    public int Read(long first, Span<double> dst)
    {
        int n = (int)Math.Max(0, Math.Min(dst.Length, Data.Length - first));
        if (n > 0) Data.AsSpan((int)first, n).CopyTo(dst);
        return n;
    }
}

/// <summary>CSV / TSV import: auto-detect delimiter (tab, ';', ','), decimal point, header row; text columns kept as text channels.</summary>
public static class CsvDataSource
{
    public static DataFile Open(string path)
    {
        var lines = File.ReadAllLines(path);
        int start = 0;
        while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
        if (start >= lines.Length) throw new InvalidDataException("empty file");
        char delim = Detect(lines[start]);
        var header = lines[start].Split(delim);
        bool hasHeader = header.Any(h => !double.TryParse(h.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) && h.Trim().Length > 0);
        var names = hasHeader ? header.Select((h, i) => string.IsNullOrWhiteSpace(h) ? "Col" + (i + 1) : h.Trim()).ToArray() : header.Select((_, i) => "Col" + (i + 1)).ToArray();
        int cols = names.Length;
        var num = new List<double>[cols]; var txt = new List<string>[cols]; var isNum = new bool[cols];
        for (int c = 0; c < cols; c++) { num[c] = new(); txt[c] = new(); isNum[c] = true; }
        bool commaDecimal = delim != ',' && lines.Skip(start + 1).Take(50).Any(l => l.Contains(',') && !l.Contains('.'));
        for (int i = start + (hasHeader ? 1 : 0); i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var f = lines[i].Split(delim);
            for (int c = 0; c < cols; c++)
            {
                string s = c < f.Length ? f[c].Trim() : "";
                txt[c].Add(s);
                if (s.Length == 0) { num[c].Add(double.NaN); continue; }
                if (commaDecimal) s = s.Replace(',', '.');
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) num[c].Add(v);
                else { num[c].Add(double.NaN); isNum[c] = false; }
            }
        }
        var df = new DataFile(path, "CSV", new Dictionary<string, object>(), null);
        var g = new ChannelGroup(Path.GetFileNameWithoutExtension(path), new Dictionary<string, object>());
        df.Groups.Add(g);
        for (int c = 0; c < cols; c++)
        {
            var props = new Dictionary<string, object>();
            var ch = new Channel(df.Name, g.Name, names[c], new ArraySource(num[c].ToArray()), props) { DataType = isNum[c] ? "DBL" : "String", IsText = !isNum[c] };
            if (!isNum[c]) props["text_values"] = txt[c].ToArray();
            g.Channels.Add(ch);
        }
        // first numeric column named like time becomes the implicit x of the others when equidistant
        var t = g.Channels.FirstOrDefault(ch => !ch.IsText && ch.Name.Contains("time", StringComparison.OrdinalIgnoreCase));
        if (t != null && t.Length > 2)
        {
            var d = ((ArraySource)t.Source).Data;
            double inc = d[1] - d[0]; bool eq = true;
            for (int i = 2; i < Math.Min(d.Length, 10000); i++) if (Math.Abs((d[i] - d[i - 1]) - inc) > Math.Abs(inc) * 1e-6) { eq = false; break; }
            if (eq && inc > 0) foreach (var ch in g.Channels) if (ch != t && !ch.IsText) { ch.XKind = XKind.Implicit; ch.XStart = d[0]; ch.XIncrement = inc; ch.XUnit = "s"; }
        }
        return df;
    }

    static char Detect(string line)
    {
        int tab = line.Count(c => c == '\t'), semi = line.Count(c => c == ';'), comma = line.Count(c => c == ',');
        if (tab >= semi && tab >= comma && tab > 0) return '\t';
        if (semi >= comma && semi > 0) return ';';
        return ',';
    }
}
