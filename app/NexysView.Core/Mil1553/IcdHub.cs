using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexysView.Core.Mil1553;

/// <summary>Small interface-control-document hub: messages (rt/sa/tr/wc/period/bus) and their words (bit field, type, scale, offset, unit).
/// JSON schema version 1 (docs/13-icd-hub.md of the analysis project); CSV/XLSX import takes one row per word.</summary>
public sealed class IcdHub
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("messages")] public List<IcdMessage> Messages { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static IcdHub FromJson(string json) => JsonSerializer.Deserialize<IcdHub>(json, Json) ?? new IcdHub();
    public static IcdHub Load(string path) => FromJson(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ToJson());

    public static readonly string[] Columns = { "message", "rt", "sa", "tr", "wc", "period_ms", "bus", "word", "bit_start", "bit_len", "type", "scale", "offset", "unit", "name" };

    /// <summary>CSV (one row per word; header row with the column names above, any order, case-insensitive).</summary>
    public static IcdHub FromCsv(string path)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0) return new IcdHub();
        var hdr = Split(lines[0]).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int col(string n) => hdr.IndexOf(n);
        var hub = new IcdHub(); var byName = new Dictionary<string, IcdMessage>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = Split(line);
            string g(string n, string d = "") { int i = col(n); return i >= 0 && i < f.Count ? f[i].Trim() : d; }
            double num(string n, double d = 0) => double.TryParse(g(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            string mn = g("message"); if (mn.Length == 0) continue;
            if (!byName.TryGetValue(mn, out var m))
            {
                m = new IcdMessage { Name = mn, Rt = (int)num("rt"), Sa = (int)num("sa"), Tr = (int)num("tr"), Wc = (int)num("wc"), PeriodMs = num("period_ms"), Bus = g("bus", "A").ToUpperInvariant() == "B" ? "B" : "A" };
                byName[mn] = m; hub.Messages.Add(m);
            }
            m.Words.Add(new IcdWord { Index = (int)num("word", 1), BitStart = (int)num("bit_start", 1), BitLen = (int)num("bit_len", 16), Type = g("type", "BNR"), Scale = num("scale", 1), Offset = num("offset"), Unit = g("unit"), Name = g("name", mn + "_W" + g("word")) });
        }
        return hub;
    }

    static List<string> Split(string line)
    {
        var o = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (char c in line)
        {
            if (c == '"') q = !q;
            else if (c == ',' && !q) { o.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        o.Add(cur.ToString()); return o;
    }

    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder(string.Join(",", Columns) + "\n");
        foreach (var m in Messages) foreach (var w in m.Words)
            sb.Append(string.Join(",", new[] { Q(m.Name), m.Rt.ToString(), m.Sa.ToString(), m.Tr.ToString(), m.Wc.ToString(), m.PeriodMs.ToString(CultureInfo.InvariantCulture), m.Bus, w.Index.ToString(), w.BitStart.ToString(), w.BitLen.ToString(), w.Type, w.Scale.ToString(CultureInfo.InvariantCulture), w.Offset.ToString(CultureInfo.InvariantCulture), Q(w.Unit), Q(w.Name) })).Append('\n');
        return sb.ToString();
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>The 10-message / 40-word dummy ICD used by the self-tests (no customer data).</summary>
    public static IcdHub Dummy()
    {
        var specs = new (string n, int rt, int sa, int tr, int wc, double per, string bus)[] { ("NAV_POS", 1, 1, 0, 8, 20, "A"), ("NAV_VEL", 1, 2, 0, 6, 20, "A"), ("NAV_ATT", 1, 3, 1, 8, 10, "A"), ("ENG_STAT", 2, 4, 1, 4, 50, "A"), ("ENG_CMD", 2, 5, 0, 4, 50, "B"), ("FUEL_QTY", 3, 6, 1, 6, 100, "A"), ("HYD_PRESS", 4, 7, 1, 4, 100, "A"), ("AIR_DATA", 5, 8, 1, 8, 20, "A"), ("RADALT", 6, 9, 1, 2, 40, "B"), ("TIME_SYNC", 7, 10, 0, 4, 1000, "A") };
        string[] units = { "deg", "deg", "m", "m/s", "m/s", "m/s", "rpm", "degC", "kPa", "%", "kg", "kg", "kPa", "kts", "ft", "degC", "ft", "s", "ms", "" };
        double[] scales = { 0.5, 0.01, 0.001, 2.0 }, offs = { 0, -100, 0, 10 };
        var hub = new IcdHub(); int k = 0;
        foreach (var s in specs)
        {
            var m = new IcdMessage { Name = s.n, Rt = s.rt, Sa = s.sa, Tr = s.tr, Wc = s.wc, PeriodMs = s.per, Bus = s.bus };
            for (int w = 0; w < 4; w++)
            {
                int widx = Math.Min(w * 2 + 1, s.wc); bool disc = k % 10 == 9;
                m.Words.Add(new IcdWord { Index = widx, BitStart = 1, BitLen = disc ? 1 : (w == 0 && s.wc >= 2 ? 32 : 16), Type = disc ? "Discrete" : "BNR", Scale = disc ? 1 : scales[w], Offset = disc ? 0 : offs[w], Unit = disc ? "" : units[k % units.Length], Name = $"{s.n}_W{widx}" });
                k++;
            }
            hub.Messages.Add(m);
        }
        return hub;
    }
}

public sealed class IcdMessage
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("rt")] public int Rt { get; set; }
    [JsonPropertyName("sa")] public int Sa { get; set; }
    [JsonPropertyName("tr")] public int Tr { get; set; }
    [JsonPropertyName("wc")] public int Wc { get; set; }
    [JsonPropertyName("period_ms")] public double PeriodMs { get; set; }
    [JsonPropertyName("bus")] public string Bus { get; set; } = "A";
    [JsonPropertyName("words")] public List<IcdWord> Words { get; set; } = new();
    [JsonIgnore] public ushort Cwd1 => (ushort)(((Rt & 0x1f) << 11) | ((Tr & 1) << 10) | ((Sa & 0x1f) << 5) | (Wc & 0x1f));
}

public sealed class IcdWord
{
    [JsonPropertyName("index")] public int Index { get; set; } = 1;          // 1-32
    [JsonPropertyName("bit_start")] public int BitStart { get; set; } = 1;   // 1-16, LSB = 1
    [JsonPropertyName("bit_len")] public int BitLen { get; set; } = 16;      // may span words (32, 48)
    [JsonPropertyName("type")] public string Type { get; set; } = "BNR";     // BNR | BCD | Discrete | SIMSCALAR | ...
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("offset")] public double Offset { get; set; }
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Raw field value from a record's data words (little-endian word order; BitStart 1 = LSB of word Index).</summary>
    public long Raw(Mil1553File f, int i)
    {
        Span<ushort> d = stackalloc ushort[40]; int n = f.Data(i, d);
        int w0 = Index - 1; if (w0 < 0 || w0 >= n) return long.MinValue;
        ulong v = 0; int got = 0;
        for (int k = 0; k < 4 && w0 + k < n && got < 64; k++) { v |= (ulong)d[w0 + k] << (16 * k); got += 16; }
        int shift = BitStart - 1; int len = Math.Min(BitLen, 64 - shift);
        ulong raw = (v >> shift) & (len >= 64 ? ulong.MaxValue : ((1UL << len) - 1));
        if (Type is "BNR" or "SIMSCALAR" && len > 1 && (raw & (1UL << (len - 1))) != 0) return (long)raw - (1L << len);   // two's complement
        return (long)raw;
    }
    public double Decode(Mil1553File f, int i) { long r = Raw(f, i); return r == long.MinValue ? double.NaN : r * Scale + Offset; }
}
