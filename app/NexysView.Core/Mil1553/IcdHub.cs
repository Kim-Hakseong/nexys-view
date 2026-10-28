using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexysView.Core.Mil1553;

/// <summary>Interface-control-document hub (schema 1.0, icd-hub.schema.json): messages (rt/sa/tr/wc/period/bus) and their words
/// (bit field, type, scale, offset, unit). CSV/XLSX import takes one row per word with a column mapping (auto-detected by header name).</summary>
public sealed class IcdHub
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("messages")] public List<IcdMessage> Messages { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static IcdHub FromJson(string json)
    {
        var hub = JsonSerializer.Deserialize<IcdHub>(json, Json) ?? new IcdHub();
        if (hub.Version != 1) throw new InvalidDataException($"unsupported hub version {hub.Version} (expected 1)");
        return hub;
    }
    public static IcdHub Load(string path) => FromJson(File.ReadAllText(path));
    public void Save(string path) => File.WriteAllText(path, ToJson());
    public string SaveTo(string path) { Save(path); return path; }

    public static readonly string[] Columns = { "message", "rt", "sa", "tr", "wc", "period_ms", "bus", "word", "bit_start", "bit_len", "type", "scale", "offset", "unit", "name" };
    static readonly Dictionary<string, string[]> Aliases = new()
    {
        ["message"] = new[] { "message", "msg", "message_name", "msgname", "메시지" }, ["rt"] = new[] { "rt", "rt_addr", "terminal", "ta", "remote_terminal" }, ["sa"] = new[] { "sa", "subaddress", "sub_address", "sub" },
        ["tr"] = new[] { "tr", "t/r", "dir", "direction", "transmit" }, ["wc"] = new[] { "wc", "word_count", "wordcount", "words_count", "count" }, ["period_ms"] = new[] { "period_ms", "period", "rate_ms", "interval" },
        ["bus"] = new[] { "bus", "channel" }, ["word"] = new[] { "word", "word_index", "index", "wordno", "word_no" }, ["bit_start"] = new[] { "bit_start", "start_bit", "startbit", "lsb", "bit" },
        ["bit_len"] = new[] { "bit_len", "bits", "width", "length", "bit_length", "nbits" }, ["type"] = new[] { "type", "encoding", "interpreter", "format" }, ["scale"] = new[] { "scale", "resolution", "lsb_value", "factor" },
        ["offset"] = new[] { "offset", "bias" }, ["unit"] = new[] { "unit", "units", "eu" }, ["name"] = new[] { "name", "field", "field_name", "signal", "parameter" },
    };

    /// <summary>Header → column index for every hub column that could be recognised (missing columns are absent from the map).</summary>
    public static Dictionary<string, int> AutoMap(string[] header)
    {
        var map = new Dictionary<string, int>(); var norm = header.Select(h => h.Trim().ToLowerInvariant().Replace(' ', '_')).ToArray();
        foreach (var col in Columns)
        {
            int idx = -1;
            foreach (var a in Aliases[col]) { idx = Array.IndexOf(norm, a); if (idx >= 0) break; }
            if (idx >= 0 && !map.ContainsValue(idx)) map[col] = idx;
        }
        return map;
    }

    public static List<string[]> ReadCsvRows(string path)
    {
        var rows = new List<string[]>();
        foreach (var line in File.ReadAllLines(path)) { if (string.IsNullOrWhiteSpace(line)) continue; rows.Add(Split(line).ToArray()); }
        return rows;
    }
    static List<string> Split(string line)
    {
        var o = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (char c in line) { if (c == '"') q = !q; else if (c == ',' && !q) { o.Add(cur.ToString()); cur.Clear(); } else cur.Append(c); }
        o.Add(cur.ToString()); return o;
    }

    /// <summary>Build a hub from rows (first row = header) and a mapping (null → auto). One row per word; rows with the same message name are merged.</summary>
    public static IcdHub FromRows(List<string[]> rows, Dictionary<string, int>? mapping)
    {
        var hub = new IcdHub(); if (rows.Count < 2) return hub;
        var map = mapping ?? AutoMap(rows[0]); if (!map.ContainsKey("message")) throw new InvalidDataException("no 'message' column (map one in the import dialog)");
        var byName = new Dictionary<string, IcdMessage>(StringComparer.Ordinal);
        foreach (var f in rows.Skip(1))
        {
            string g(string col, string d = "") => map.TryGetValue(col, out var i) && i >= 0 && i < f.Length ? f[i].Trim() : d;
            double num(string col, double d = 0) => double.TryParse(g(col), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
            string mn = g("message"); if (mn.Length == 0) continue;
            if (!byName.TryGetValue(mn, out var m))
            {
                string trs = g("tr", "0").ToUpperInvariant();
                m = new IcdMessage { Name = mn, Rt = (int)num("rt"), Sa = (int)num("sa"), Tr = trs is "1" or "TX" or "T" or "RT->BC" or "TRANSMIT" ? 1 : 0, Wc = (int)num("wc", 32), PeriodMs = num("period_ms"), Bus = g("bus", "A").ToUpperInvariant().StartsWith("B") ? "B" : "A" };
                byName[mn] = m; hub.Messages.Add(m);
            }
            if (!map.ContainsKey("word") && !map.ContainsKey("name")) continue;
            string t = g("type", "BNR"); if (t.Length == 0) t = "BNR";
            m.Words.Add(new IcdWord { Index = (int)num("word", 1), BitStart = (int)num("bit_start", 1), BitLen = (int)num("bit_len", 16), Type = t, Scale = map.ContainsKey("scale") && g("scale").Length > 0 ? num("scale", 1) : 1, Offset = num("offset"), Unit = g("unit"), Name = g("name", mn + "_W" + g("word", "1")) });
        }
        return hub;
    }
    public static IcdHub FromCsv(string path) => FromRows(ReadCsvRows(path), null);

    public string ToCsv()
    {
        var sb = new System.Text.StringBuilder(string.Join(",", Columns) + "\n");
        foreach (var m in Messages) foreach (var w in m.Words)
            sb.Append(string.Join(",", new[] { Q(m.Name), m.Rt.ToString(), m.Sa.ToString(), m.Tr.ToString(), m.Wc.ToString(), m.PeriodMs.ToString(CultureInfo.InvariantCulture), m.Bus, w.Index.ToString(), w.BitStart.ToString(), w.BitLen.ToString(), w.Type, w.Scale.ToString(CultureInfo.InvariantCulture), w.Offset.ToString(CultureInfo.InvariantCulture), Q(w.Unit), Q(w.Name) })).Append('\n');
        return sb.ToString();
        static string Q(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    /// <summary>Structural validation (schema 1.0 rules + bit-overlap warnings). "error: …" entries block an export, "warning: …" do not.</summary>
    public static List<string> Validate(IcdHub hub)
    {
        var out_ = new List<string>(); var keys = new HashSet<(int, int, int)>();
        for (int k = 0; k < hub.Messages.Count; k++)
        {
            var m = hub.Messages[k]; string p = $"message {k + 1} '{m.Name}'";
            if (string.IsNullOrWhiteSpace(m.Name)) out_.Add($"error: {p}: empty name");
            if (m.Rt < 0 || m.Rt > 31) out_.Add($"error: {p}: RT {m.Rt} out of 0..31");
            if (m.Sa < 0 || m.Sa > 31) out_.Add($"error: {p}: SA {m.Sa} out of 0..31");
            if (m.Wc < 1 || m.Wc > 32) out_.Add($"error: {p}: WC {m.Wc} out of 1..32");
            if (m.Tr != 0 && m.Tr != 1) out_.Add($"error: {p}: T/R must be 0 or 1");
            if (m.Bus != "A" && m.Bus != "B") out_.Add($"error: {p}: bus must be A or B");
            if (!keys.Add((m.Rt, m.Sa, m.Tr))) out_.Add($"warning: {p}: duplicate RT/SA/T-R ({m.Rt}/{m.Sa}/{m.Tr})");
            var used = new bool[32 * 16 + 1];
            for (int j = 0; j < m.Words.Count; j++)
            {
                var w = m.Words[j]; string q = $"{p} word {j + 1} '{w.Name}'";
                if (w.Index < 1 || w.Index > 32) out_.Add($"error: {q}: word index {w.Index} out of 1..32");
                else if (w.Index > m.Wc) out_.Add($"warning: {q}: word {w.Index} beyond WC {m.Wc}");
                if (w.BitStart < 1 || w.BitStart > 16) out_.Add($"error: {q}: bit_start {w.BitStart} out of 1..16");
                if (w.BitLen < 1) out_.Add($"error: {q}: bit_len must be ≥ 1");
                if (w.Index >= 1 && w.Index <= 32 && w.BitStart >= 1)
                {
                    int b0 = (w.Index - 1) * 16 + (w.BitStart - 1);
                    for (int b = b0; b < Math.Min(b0 + w.BitLen, used.Length - 1); b++) { if (used[b]) { out_.Add($"error: {q}: bits overlap another word (word {b / 16 + 1} bit {b % 16 + 1})"); break; } used[b] = true; }
                }
            }
        }
        return out_;
    }

    /// <summary>Same messages/words (rt, sa, tr, wc, word bit fields, type, scale, offset) regardless of names' case or order.</summary>
    public static bool SameShape(IcdHub a, IcdHub b)
    {
        static string K(IcdHub h) => string.Join("|", h.Messages.Select(m => $"{m.Rt}/{m.Sa}/{m.Tr}/{m.Wc}:" + string.Join(",", m.Words.Select(w => $"{w.Index}.{w.BitStart}.{w.BitLen}.{w.Type.ToUpperInvariant()}.{w.Scale:R}.{w.Offset:R}").OrderBy(x => x, StringComparer.Ordinal))).OrderBy(x => x, StringComparer.Ordinal));
        return K(a) == K(b);
    }

    /// <summary>The 10-message / 40-word dummy ICD used by the self-tests (no customer data).</summary>
    public static IcdHub Dummy()
    {
        var specs = new (string n, int rt, int sa, int tr, int wc, double per, string bus)[] { ("NAV_POS", 1, 1, 0, 8, 20, "A"), ("NAV_VEL", 1, 2, 0, 6, 20, "A"), ("NAV_ATT", 1, 3, 1, 8, 10, "A"), ("ENG_STAT", 2, 4, 1, 4, 50, "A"), ("ENG_CMD", 2, 5, 0, 4, 50, "B"), ("FUEL_QTY", 3, 6, 1, 6, 100, "A"), ("HYD_PRESS", 4, 7, 1, 4, 100, "A"), ("AIR_DATA", 5, 8, 1, 8, 20, "A"), ("RADALT", 6, 9, 1, 2, 40, "B"), ("TIME_SYNC", 7, 10, 0, 4, 1000, "A") };
        string[] units = { "deg", "deg", "m", "m/s", "m/s", "m/s", "rpm", "degC", "kPa", "%", "kg", "kg", "kPa", "kts", "ft", "degC", "ft", "s", "ms", "" };
        double[] scales = { 0.5, 0.01, 0.001, 2.0 }, offs = { 0, -100, 0, 10 };
        var hub = new IcdHub { Source = "dummy" }; int k = 0;
        foreach (var s in specs)
        {
            var m = new IcdMessage { Name = s.n, Rt = s.rt, Sa = s.sa, Tr = s.tr, Wc = s.wc, PeriodMs = s.per, Bus = s.bus };
            // bit allocator: words never overlap whatever the WC (32-bit first word only when WC ≥ 8, 8-bit fields when WC ≤ 3)
            var used = new bool[s.wc * 16];
            int Alloc(int len) { int step = len >= 16 ? 16 : len; for (int b = 0; b + len <= used.Length; b += step) { bool free = true; for (int x = b; x < b + len; x++) if (used[x]) { free = false; break; } if (free) { for (int x = b; x < b + len; x++) used[x] = true; return b; } } return -1; }
            for (int w = 0; w < 4; w++)
            {
                bool disc = k % 10 == 9; int want = disc ? 1 : w == 0 && s.wc >= 8 ? 32 : s.wc >= 4 ? 16 : 8; int len = want, at = -1;
                foreach (int l in new[] { want, 8, 4, 1 }) { at = Alloc(l); if (at >= 0) { len = l; break; } }
                if (at < 0) break;
                int widx = at / 16 + 1, bs = at % 16 + 1;
                m.Words.Add(new IcdWord { Index = widx, BitStart = bs, BitLen = len, Type = disc ? "Discrete" : "BNR", Scale = disc ? 1 : scales[w], Offset = disc ? 0 : offs[w], Unit = disc ? "" : units[k % units.Length], Name = $"{s.n}_W{widx}{(bs > 1 ? "B" + bs : "")}" });
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
    [JsonPropertyName("index")] public int Index { get; set; } = 1;
    [JsonPropertyName("bit_start")] public int BitStart { get; set; } = 1;
    [JsonPropertyName("bit_len")] public int BitLen { get; set; } = 16;
    [JsonPropertyName("type")] public string Type { get; set; } = "BNR";
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
        if (Type is "BNR" or "SIMSCALAR" && len > 1 && (raw & (1UL << (len - 1))) != 0) return (long)raw - (1L << len);
        return (long)raw;
    }
    public double Decode(Mil1553File f, int i) { long r = Raw(f, i); return r == long.MinValue ? double.NaN : r * Scale + Offset; }
}
