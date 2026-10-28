using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using NexysView.Core.Data;
using NexysView.Core.Mil1553;

namespace NexysView.App.Services;

/// <summary>1.0-rc: bus capture view (MIL-STD-1553 and ARINC 429 records of the capture container: list, filters, decode, statistics,
/// CSV exports) and the ICD module (hub import with column mapping, edit/validate, exports: simulator project / message-database XML /
/// custom-device XML / automation script). Owned by the Engine; the bridge routes "bus.*" and "icd.*" commands here.</summary>
public sealed class Mil1553Service
{
    readonly Engine _e;
    public Mil1553Service(Engine e) { _e = e; }

    public IcdHub? Hub { get; private set; }
    public string HubPath { get; private set; } = "";
    public bool IcdApplied { get; private set; } = true;
    public string? File { get; private set; }
    int[]? _filtered; int _rt = -1, _sa = -1, _tr = -1, _bus = -1; bool _errOnly; string _text = "";
    int _label = -1, _sdi = -1, _ssm = -1; bool _parityOnly;
    public int Selected { get; private set; } = -1;
    public double LastFilterMs { get; private set; }
    public string LastExport { get; private set; } = "";
    public sealed class FilterPreset { public string Name { get; set; } = ""; public int Rt { get; set; } = -1; public int Sa { get; set; } = -1; public int Tr { get; set; } = -1; public int Bus { get; set; } = -1; public bool ErrorsOnly { get; set; } public string Text { get; set; } = ""; }
    public List<FilterPreset> Presets { get; } = new();

    DataFile? Df => _e.State.Files.FirstOrDefault(f => (f.Format == Mil1553DataSource.Format || f.Format == Mil1553DataSource.Format429) && (File == null || f.Name == File)) ?? _e.State.Files.FirstOrDefault(f => f.Format == Mil1553DataSource.Format || f.Format == Mil1553DataSource.Format429);
    Mil1553File? Cap => Df is { } d ? Mil1553DataSource.FileOf(d) : null;
    Arinc429File? Cap429 => Df is { } d ? Mil1553DataSource.FileOf429(d) : null;
    public string Protocol => Df?.Format == Mil1553DataSource.Format429 ? "A429" : Df != null ? "1553" : "";

    // ------------------------------------------------------------------ state for the page
    public object Dto()
    {
        var df = Df; var cap = Cap; var c429 = Cap429;
        int count = cap?.Count ?? c429?.Count ?? 0;
        return new
        {
            file = df?.Name, path = df?.Path, protocol = Protocol, count, filtered = _filtered?.Length ?? count, selected = Selected,
            filter = new { rt = _rt, sa = _sa, tr = _tr, bus = _bus, errorsOnly = _errOnly, text = _text, label = _label, sdi = _sdi, ssm = _ssm, parityOnly = _parityOnly }, filterMs = LastFilterMs,
            presets = Presets.Select(p => new { p.Name, p.Rt, p.Sa, p.Tr, p.Bus, p.ErrorsOnly, p.Text }).ToList(), sort = new { col = _sortCol, dir = _sortDir },
            icd = Hub == null ? null : new { path = HubPath, applied = IcdApplied, messages = Hub.Messages.Count, words = Hub.Messages.Sum(m => m.Words.Count), matched = cap == null ? 0 : Hub.Messages.Count(m => MatchesAny(cap, m)), source = Hub.Source },
            files = _e.State.Files.Where(f => f.Format == Mil1553DataSource.Format || f.Format == Mil1553DataSource.Format429).Select(f => new { f.Name, protocol = f.Format == Mil1553DataSource.Format429 ? "A429" : "1553" }).ToList(),
            templateDir = _e.IcdTemplateDir, templateOk = TemplateOk(_e.IcdTemplateDir), lastExport = LastExport,
            t0us = cap != null && cap.Count > 0 ? cap.TimeUs[0] : c429 != null && c429.Count > 0 ? c429.TimeUs[0] : 0UL,
        };
    }
    static bool TemplateOk(string d) => d.Length > 0 && Directory.Exists(d) && Directory.EnumerateFiles(d, "*.CDV").Any() && Directory.EnumerateFiles(d, "*.CPJ").Any();
    static bool MatchesAny(Mil1553File cap, IcdMessage m) { ushort c = m.Cwd1; for (int i = 0; i < cap.Count; i++) if ((cap.Cwd1[i] & 0xFFE0) == (c & 0xFFE0)) return true; return false; }

    public void SelectFile(string? name) { File = name; _filtered = null; Selected = -1; _e.Touch(); }

    public void SetFilter(int rt, int sa, int tr, int bus, bool errorsOnly, string text = "", int label = -1, int sdi = -1, int ssm = -1, bool parityOnly = false)
    {
        _rt = rt; _sa = sa; _tr = tr; _bus = bus; _errOnly = errorsOnly; _text = text ?? ""; _label = label; _sdi = sdi; _ssm = ssm; _parityOnly = parityOnly;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (Cap is { } cap)
        {
            bool any = rt < 0 && sa < 0 && tr < 0 && bus < 0 && !errorsOnly && _text.Length == 0;
            _filtered = any ? null : cap.Filter(rt, sa, tr, errorsOnly, bus);
            if (_text.Length > 0) _filtered = TextFilter(cap, _filtered ?? Enumerable.Range(0, cap.Count).ToArray(), _text);
        }
        else if (Cap429 is { } c429) _filtered = label < 0 && sdi < 0 && ssm < 0 && !parityOnly ? null : c429.Filter(label, sdi, ssm, parityOnly);
        else _filtered = null;
        LastFilterMs = sw.Elapsed.TotalMilliseconds; _sorted = null; if (_sortCol.Length > 0) SetSort(_sortCol, _sortDir); else _e.Touch();
    }

    /// <summary>Text filter over the decoded row text (hex data words, ICD message name, RT/SA numbers); case-insensitive substring.</summary>
    int[] TextFilter(Mil1553File cap, int[] idx, string text)
    {
        var l = new List<int>(); Span<ushort> d = stackalloc ushort[40]; var sb = new StringBuilder(); string q = text.Trim().ToUpperInvariant();
        foreach (int i in idx)
        {
            var rec = cap.Get(i); int n = cap.Data(i, d); sb.Clear();
            sb.Append("RT").Append(rec.Rt).Append(" SA").Append(rec.Sa).Append(' ').Append(rec.Tr == 1 ? "RT->BC" : "BC->RT").Append(' ').Append(MsgOf(rec)?.Name ?? "").Append(' ');
            for (int k = 0; k < n; k++) sb.Append(d[k].ToString("X4")).Append(' ');
            if (sb.ToString().ToUpperInvariant().Contains(q)) l.Add(i);
        }
        return l.ToArray();
    }

    string _sortCol = ""; int _sortDir = 1; int[]? _sorted;
    public (string col, int dir) Sort => (_sortCol, _sortDir);
    /// <summary>Sort the (filtered) list by a column; empty column = capture order. Returns the row count.</summary>
    public int SetSort(string col, int dir)
    {
        _sortCol = col ?? ""; _sortDir = dir >= 0 ? 1 : -1; _sorted = null;
        if (_sortCol.Length > 0)
        {
            int n = _filtered?.Length ?? Cap?.Count ?? Cap429?.Count ?? 0; var idx = new int[n]; for (int r = 0; r < n; r++) idx[r] = _filtered != null ? _filtered[r] : r;
            Func<int, long>? key = null;
            if (Cap is { } cap) key = _sortCol switch { "rt" => i => (cap.Cwd1[i] >> 11) & 0x1f, "sa" => i => (cap.Cwd1[i] >> 5) & 0x1f, "tr" => i => (cap.Cwd1[i] >> 10) & 1, "wc" => i => cap.Cwd1[i] & 0x1f, "bus" => i => cap.Activity[i] & 1, "err" => i => cap.Error[i] & ~1, "act" => i => cap.Activity[i], "n" => i => cap.DataCount[i], "cwd1" => i => cap.Cwd1[i], "swd1" => i => cap.Swd1[i], _ => null };
            else if (Cap429 is { } c) key = _sortCol switch { "label" => i => c.Get(i).Label, "sdi" => i => c.Get(i).Sdi, "data" => i => c.Get(i).Data, "ssm" => i => c.Get(i).Ssm, "parity" => i => c.Get(i).ParityOk ? 1 : 0, "word" => i => c.Word[i], _ => null };
            if (key != null) { var keys = new long[n]; for (int r = 0; r < n; r++) keys[r] = key(idx[r]); Array.Sort(keys, idx); if (_sortDir < 0) Array.Reverse(idx); _sorted = idx; }
            else if (_sortDir < 0) { Array.Reverse(idx); _sorted = idx; }
        }
        _e.Touch(); return RowCount;
    }
    int Map(int row) { var v = _sorted ?? _filtered; return v != null ? (row >= 0 && row < v.Length ? v[row] : -1) : row; }
    public int RowCount => (_sorted ?? _filtered)?.Length ?? Cap?.Count ?? Cap429?.Count ?? 0;
    public int RowOf(int i) { var v = _sorted ?? _filtered; if (v == null) return i; return Array.IndexOf(v, i); }

    public object? Rows(int first, int count)
    {
        count = Math.Clamp(count, 1, 5000); int total = RowCount; first = Math.Clamp(first, 0, Math.Max(0, total - 1));
        if (Cap is { } cap)
        {
            double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0; var rows = new List<object>(); Span<ushort> d = stackalloc ushort[40]; ulong prev = 0;
            for (int r = first; r < Math.Min(total, first + count); r++)
            {
                int i = Map(r); if (i < 0) break; var rec = cap.Get(i); int n = cap.Data(i, d);
                var sb = new StringBuilder(n * 5); for (int k = 0; k < n; k++) { if (k > 0) sb.Append(' '); sb.Append(d[k].ToString("X4")); }
                ulong tp = r > first ? prev : (r > 0 && Map(r - 1) is int pi && pi >= 0 ? cap.TimeUs[pi] : cap.TimeUs[i]); prev = cap.TimeUs[i];
                rows.Add(new { i, t = (cap.TimeUs[i] - t0) * 1e-6, tus = cap.TimeUs[i], dt = (double)(cap.TimeUs[i] - tp) * 1e-6, bus = rec.BusB ? "B" : "A", rt = rec.Rt, sa = rec.Sa, tr = rec.Tr, wc = rec.Wc == 0 && n > 0 ? 32 : rec.Wc, cwd1 = rec.Cwd1, swd1 = rec.Swd1, err = rec.Error, act = rec.Activity, n, data = sb.ToString(), icd = Hub == null || !IcdApplied ? null : MsgOf(rec)?.Name, e = rec.HasError });
            }
            return new { first, total, rows };
        }
        if (Cap429 is { } c)
        {
            double t0 = c.Count > 0 ? c.TimeUs[0] : 0; var rows = new List<object>(); ulong prev = 0;
            for (int r = first; r < Math.Min(total, first + count); r++)
            {
                int i = Map(r); if (i < 0) break; var rec = c.Get(i);
                ulong tp = r > first ? prev : (r > 0 && Map(r - 1) is int pi && pi >= 0 ? c.TimeUs[pi] : c.TimeUs[i]); prev = c.TimeUs[i];
                rows.Add(new { i, t = (c.TimeUs[i] - t0) * 1e-6, tus = c.TimeUs[i], dt = (double)(c.TimeUs[i] - tp) * 1e-6, label = rec.LabelOctal, sdi = rec.Sdi, data = rec.Data, dataHex = rec.Data.ToString("X5"), ssm = rec.Ssm, ssmName = Arinc429Record.SsmName(rec.Ssm), parity = rec.ParityOk ? "ok" : "ERR", word = rec.Word.ToString("X8"), act = rec.Activity, e = !rec.ParityOk });
            }
            return new { first, total, rows };
        }
        return null;
    }

    IcdMessage? MsgOf(Mil1553Record rec) => Hub?.Messages.FirstOrDefault(m => m.Rt == rec.Rt && m.Sa == rec.Sa && m.Tr == rec.Tr);

    public object? Decode(int i)
    {
        if (Cap is { } cap)
        {
            if (i < 0 || i >= cap.Count) return null;
            Selected = i; var rec = cap.Get(i); double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0;
            Span<ushort> d = stackalloc ushort[40]; int n = cap.Data(i, d);
            var raw = new List<object>(n); for (int k = 0; k < n; k++) raw.Add(new { k = k + 1, hex = d[k].ToString("X4"), bin = Convert.ToString(d[k], 2).PadLeft(16, '0'), dec = (int)d[k] });
            var msg = IcdApplied ? MsgOf(rec) : null; var words = new List<object>();
            if (msg != null) foreach (var w in msg.Words) { long r = w.Raw(cap, i); words.Add(new { word = w.Index, bits = $"{w.BitStart}..{w.BitStart + w.BitLen - 1}", name = w.Name, raw = r == long.MinValue ? "" : (r & 0xFFFFFFFF).ToString("X" + (w.BitLen > 16 ? 8 : 4)), bin = r == long.MinValue ? "" : Convert.ToString(r & ((1L << Math.Min(w.BitLen, 62)) - 1), 2).PadLeft(Math.Min(w.BitLen, 62), '0'), eu = r == long.MinValue ? (double?)null : r * w.Scale + w.Offset, unit = w.Unit, type = w.Type, channel = $"{Df!.Name}|ICD:{msg.Name}|{w.Name}" }); }
            var errs = new List<string>(); foreach (var (bit, nm) in ErrBits) if ((rec.Error & bit) != 0) errs.Add(nm);
            var acts = new List<string>(); foreach (var (bit, nm) in ActBits) if ((rec.Activity & bit) != 0) acts.Add(nm);
            _e.Touch();
            return new { protocol = "1553", i, row = RowOf(i), t = (cap.TimeUs[i] - t0) * 1e-6, tUs = cap.TimeUs[i], bus = rec.BusB ? "B" : "A", rt = rec.Rt, sa = rec.Sa, tr = rec.Tr, wc = rec.Wc == 0 && n > 0 ? 32 : rec.Wc, modeCode = rec.IsModeCode,
                cwd1 = rec.Cwd1.ToString("X4"), cwd2 = rec.Cwd2.ToString("X4"), swd1 = rec.Swd1.ToString("X4"), swd2 = rec.Swd2.ToString("X4"), err = rec.Error.ToString("X4"), errs, act = rec.Activity.ToString("X4"), acts, resp1 = rec.RespTime1, resp2 = rec.RespTime2, n, raw, icd = msg?.Name, words, swdFlags = SwdFlags(rec.Swd1) };
        }
        if (Cap429 is { } c)
        {
            if (i < 0 || i >= c.Count) return null;
            Selected = i; var rec = c.Get(i); double t0 = c.Count > 0 ? c.TimeUs[0] : 0; _e.Touch();
            return new { protocol = "A429", i, row = RowOf(i), t = (c.TimeUs[i] - t0) * 1e-6, tUs = c.TimeUs[i], word = rec.Word.ToString("X8"), bin = Convert.ToString(rec.Word, 2).PadLeft(32, '0'), label = rec.LabelOctal, labelDec = rec.Label, sdi = rec.Sdi, data = rec.Data, dataHex = rec.Data.ToString("X5"), dataBin = Convert.ToString(rec.Data, 2).PadLeft(19, '0'), ssm = rec.Ssm, ssmName = Arinc429Record.SsmName(rec.Ssm), parity = rec.Parity, parityOk = rec.ParityOk, act = rec.Activity.ToString("X4"), decgap = rec.DecGap,
                bnr = (rec.Data & 0x40000) != 0 ? rec.Data - 0x80000 : rec.Data, bcd = BcdOf(rec.Data) };
        }
        return null;
    }
    static string BcdOf(int data) { var sb = new StringBuilder(); for (int s = 15; s >= 0; s -= 4) sb.Append(((data >> s) & 0xF).ToString("X")); return (data >> 19 & 0) + sb.ToString(); }

    static readonly (ushort, string)[] ErrBits = { (0x0001, "HIT"), (0x0002, "SYSTEM"), (0x0004, "E04"), (0x0008, "E08"), (0x0010, "E10"), (0x0020, "E20"), (0x0040, "E40"), (0x0080, "E80"), (0x0100, "E100"), (0x0200, "E200"), (0x0400, "E400"), (0x0800, "E800"), (0x1000, "E1000"), (0x2000, "E2000"), (0x4000, "E4000"), (0x8000, "E8000") };
    static readonly (ushort, string)[] ActBits = { (0x0800, "XMTCWD1"), (0x0400, "XMTCWD2"), (0x0200, "XMTSWD1"), (0x0100, "XMTSWD2"), (0x0080, "RCVCWD1"), (0x0040, "RCVCWD2"), (0x0020, "RCVSWD1"), (0x0010, "RCVSWD2"), (0x0008, "XMTDWD"), (0x0004, "RCVDWD"), (0x0001, "BUS B") };
    static List<string> SwdFlags(ushort s) { var l = new List<string>(); if ((s & 0x0400) != 0) l.Add("MSG ERROR"); if ((s & 0x0200) != 0) l.Add("INSTR"); if ((s & 0x0100) != 0) l.Add("SRQ"); if ((s & 0x0010) != 0) l.Add("BCAST RCVD"); if ((s & 0x0008) != 0) l.Add("BUSY"); if ((s & 0x0004) != 0) l.Add("SUBSYS"); if ((s & 0x0002) != 0) l.Add("DBC ACCEPT"); if ((s & 0x0001) != 0) l.Add("TERMINAL"); return l; }

    // ------------------------------------------------------------------ statistics
    public object? Stats()
    {
        if (Cap is { } cap)
        {
            if (cap.Count == 0) return new { protocol = "1553", count = 0 };
            var perRt = new int[32]; var perRtErr = new int[32]; int errors = 0, busB = 0, modeCodes = 0; long bits = 0; var sa = new Dictionary<int, int>();
            for (int i = 0; i < cap.Count; i++)
            {
                int rt = (cap.Cwd1[i] >> 11) & 0x1f; perRt[rt]++; int s = (cap.Cwd1[i] >> 5) & 0x1f; int key = (rt << 6) | (s << 1) | ((cap.Cwd1[i] >> 10) & 1); sa[key] = sa.GetValueOrDefault(key) + 1;
                bool err = (cap.Error[i] & ~1) != 0; if (err) { errors++; perRtErr[rt]++; } if ((cap.Activity[i] & 1) != 0) busB++; if (s == 0 || s == 31) modeCodes++;
                bits += (2 + cap.DataCount[i]) * 20L;   // command + status + data words (20 bit times each)
            }
            double dur = (cap.TimeUs[cap.Count - 1] - cap.TimeUs[0]) * 1e-6;
            return new { protocol = "1553", count = cap.Count, durationS = dur, rateHz = dur > 0 ? cap.Count / dur : 0, errors, busB, busA = cap.Count - busB, modeCodes,
                busLoadPct = dur > 0 ? Math.Min(100, bits / (1e6 * dur) * 100) : 0,
                perRt = Enumerable.Range(0, 32).Where(r => perRt[r] > 0).Select(r => new { rt = r, count = perRt[r], errors = perRtErr[r] }).ToList(),
                topMessages = sa.OrderByDescending(kv => kv.Value).Take(12).Select(kv => new { rt = kv.Key >> 6, sa = (kv.Key >> 1) & 0x1f, tr = kv.Key & 1, count = kv.Value, icd = Hub?.Messages.FirstOrDefault(m => m.Rt == (kv.Key >> 6) && m.Sa == ((kv.Key >> 1) & 0x1f) && m.Tr == (kv.Key & 1))?.Name }).ToList() };
        }
        if (Cap429 is { } c)
        {
            if (c.Count == 0) return new { protocol = "A429", count = 0 };
            var perLabel = new Dictionary<int, int>(); int parityErr = 0;
            for (int i = 0; i < c.Count; i++) { var r = c.Get(i); perLabel[r.Label] = perLabel.GetValueOrDefault(r.Label) + 1; if (!r.ParityOk) parityErr++; }
            double dur = (c.TimeUs[c.Count - 1] - c.TimeUs[0]) * 1e-6;
            return new { protocol = "A429", count = c.Count, durationS = dur, rateHz = dur > 0 ? c.Count / dur : 0, parityErrors = parityErr, busLoadPct = dur > 0 ? Math.Min(100, c.Count * 32.0 / (12500 * dur) * 100) : 0,
                perLabel = perLabel.OrderByDescending(kv => kv.Value).Select(kv => new { label = Convert.ToString(kv.Key, 8).PadLeft(3, '0'), count = kv.Value }).ToList() };
        }
        return null;
    }

    // ------------------------------------------------------------------ CSV exports
    public int ExportRowsCsv(string path, int first, int last)
    {
        int total = RowCount; first = Math.Clamp(first, 0, Math.Max(0, total - 1)); last = Math.Clamp(last, first, total - 1); int n = 0;
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        if (Cap is { } cap)
        {
            w.WriteLine("index,time_s,time_us,bus,rt,sa,tr,wc,cwd1,cwd2,swd1,swd2,error,activity,resp1,count,data_hex,icd");
            Span<ushort> d = stackalloc ushort[40]; double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0;
            for (int r = first; r <= last; r++) { int i = Map(r); if (i < 0) break; var rec = cap.Get(i); int k = cap.Data(i, d); var hex = string.Join(" ", d[..k].ToArray().Select(x => x.ToString("X4")));
                w.WriteLine(string.Join(",", i, ((cap.TimeUs[i] - t0) * 1e-6).ToString("F6", CultureInfo.InvariantCulture), cap.TimeUs[i], rec.BusB ? "B" : "A", rec.Rt, rec.Sa, rec.Tr, rec.Wc, rec.Cwd1.ToString("X4"), rec.Cwd2.ToString("X4"), rec.Swd1.ToString("X4"), rec.Swd2.ToString("X4"), rec.Error.ToString("X4"), rec.Activity.ToString("X4"), rec.RespTime1, k, hex, MsgOf(rec)?.Name ?? "")); n++; }
        }
        else if (Cap429 is { } c)
        {
            w.WriteLine("index,time_s,time_us,word_hex,label_oct,sdi,data,ssm,parity_ok"); double t0 = c.Count > 0 ? c.TimeUs[0] : 0;
            for (int r = first; r <= last; r++) { int i = Map(r); if (i < 0) break; var rec = c.Get(i); w.WriteLine(string.Join(",", i, ((c.TimeUs[i] - t0) * 1e-6).ToString("F6", CultureInfo.InvariantCulture), c.TimeUs[i], rec.Word.ToString("X8"), rec.LabelOctal, rec.Sdi, rec.Data, rec.Ssm, rec.ParityOk ? 1 : 0)); n++; }
        }
        _e.SetStatus($"Exported {n} rows to {Path.GetFileName(path)}"); return n;
    }

    /// <summary>Decoded CSV: one line per record × ICD word (time, message, word name, raw, EU, unit) for the current (filtered) rows.</summary>
    public int ExportDecodedCsv(string path, int first, int last)
    {
        if (Cap429 != null) return ExportRowsCsv(path, first, last);   // 429: the row CSV already carries the decoded fields
        if (Cap is not { } cap) throw new InvalidOperationException("No bus capture selected");
        int total = RowCount; first = Math.Clamp(first, 0, Math.Max(0, total - 1)); last = Math.Clamp(last, first, total - 1); int n = 0; double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0;
        using var w = new StreamWriter(path, false, new UTF8Encoding(true)); w.WriteLine("index,time_s,message,rt,sa,tr,word,bits,name,raw,eu,unit");
        if (Hub == null || !IcdApplied)
        {   // no ICD: one line per data word, raw = hex, eu = unsigned decimal
            Span<ushort> d = stackalloc ushort[40];
            for (int r = first; r <= last; r++) { int i = Map(r); if (i < 0) break; var rec = cap.Get(i); int k = cap.Data(i, d); for (int j = 0; j < k; j++) { w.WriteLine(string.Join(",", i, ((cap.TimeUs[i] - t0) * 1e-6).ToString("F6", CultureInfo.InvariantCulture), "", rec.Rt, rec.Sa, rec.Tr, j + 1, "1-16", $"W{j + 1}", d[j].ToString("X4"), d[j], "")); n++; } }
            _e.SetStatus($"Exported {n} raw words to {Path.GetFileName(path)} (no ICD applied)"); return n;
        }
        for (int r = first; r <= last; r++)
        {
            int i = Map(r); if (i < 0) break; var rec = cap.Get(i); var m = MsgOf(rec); if (m == null) continue;
            foreach (var wd in m.Words) { long raw = wd.Raw(cap, i); w.WriteLine(string.Join(",", i, ((cap.TimeUs[i] - t0) * 1e-6).ToString("F6", CultureInfo.InvariantCulture), Csv(m.Name), m.Rt, m.Sa, m.Tr, wd.Index, $"{wd.BitStart}-{wd.BitStart + wd.BitLen - 1}", Csv(wd.Name), raw == long.MinValue ? "" : raw.ToString(), raw == long.MinValue ? "" : (raw * wd.Scale + wd.Offset).ToString("R", CultureInfo.InvariantCulture), Csv(wd.Unit))); n++; }
        }
        _e.SetStatus($"Exported {n} decoded values to {Path.GetFileName(path)}"); return n;
    }
    static string Csv(string s) => s.Contains(',') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    // ------------------------------------------------------------------ ICD module
    void Attach()
    {
        foreach (var f in _e.State.Files.Where(f => f.Format == Mil1553DataSource.Format)) { if (Hub != null && IcdApplied) Mil1553DataSource.AttachIcd(f, Hub); else Mil1553DataSource.DetachIcd(f); }
        _e.FilesChangedExternally();
    }
    public void SetHub(IcdHub hub, string path) { Hub = hub; HubPath = path; Attach(); _e.SetStatus($"ICD {Path.GetFileName(path)}: {hub.Messages.Count} messages, {hub.Messages.Sum(m => m.Words.Count)} words"); }
    public void ApplyIcd(bool on) { IcdApplied = on; Attach(); }
    public object LoadIcd(string path)
    {
        IcdHub hub = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? IcdHub.Load(path) : path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ? IcdXml.FromDbXml(System.IO.File.ReadAllText(path)) : IcdHub.FromRows(ReadRows(path), null);
        hub.Source ??= Path.GetFileName(path);
        SetHub(hub, path);
        return new { messages = hub.Messages.Count, words = hub.Messages.Sum(m => m.Words.Count), errors = IcdHub.Validate(hub) };
    }
    public void LoadDummyIcd() { SetHub(IcdHub.Dummy(), "(built-in dummy)"); }
    public void NewHub() { SetHub(new IcdHub { Source = "new" }, "(unsaved)"); }
    public string SaveHub(string path) { if (Hub == null) throw new InvalidOperationException("No ICD"); Hub.Save(path); HubPath = path; _e.Touch(); return path; }

    static List<string[]> ReadRows(string path) => path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? XlsxReader.ReadFirstSheet(path) : IcdHub.ReadCsvRows(path);

    /// <summary>Column-mapping preview of a CSV/XLSX: header, auto-detected mapping, first rows.</summary>
    public object Preview(string path)
    {
        var rows = ReadRows(path); if (rows.Count == 0) return new { columns = Array.Empty<string>(), mapping = new Dictionary<string, int>(), rows = Array.Empty<string[]>(), total = 0 };
        var hdr = rows[0]; var map = IcdHub.AutoMap(hdr);
        return new { columns = hdr, mapping = map, rows = rows.Skip(1).Take(12).ToList(), total = rows.Count - 1, required = IcdHub.Columns };
    }
    public object ImportMapped(string path, Dictionary<string, int> mapping)
    {
        var hub = IcdHub.FromRows(ReadRows(path), mapping); hub.Source = Path.GetFileName(path); SetHub(hub, path);
        return new { messages = hub.Messages.Count, words = hub.Messages.Sum(m => m.Words.Count), errors = IcdHub.Validate(hub) };
    }

    public object HubDto() => Hub == null ? new { messages = Array.Empty<object>(), errors = Array.Empty<string>() } : new
    {
        path = HubPath, source = Hub.Source, applied = IcdApplied,
        messages = Hub.Messages.Select((m, k) => new { k, m.Name, m.Rt, m.Sa, m.Tr, m.Wc, m.PeriodMs, m.Bus, words = m.Words.Select((w, j) => new { j, w.Index, w.BitStart, w.BitLen, w.Type, w.Scale, w.Offset, w.Unit, w.Name }).ToList() }).ToList(),
        errors = IcdHub.Validate(Hub),
    };

    public object Edit(JsonElement r)
    {
        Hub ??= new IcdHub { Source = "new" }; string op = r.GetProperty("op").GetString() ?? "";
        int mi = r.TryGetProperty("m", out var mv) && mv.ValueKind == JsonValueKind.Number ? mv.GetInt32() : -1;
        switch (op)
        {
            case "addMessage": Hub.Messages.Add(new IcdMessage { Name = $"MSG_{Hub.Messages.Count + 1}", Rt = 1, Sa = 1, Tr = 0, Wc = 8, PeriodMs = 100, Bus = "A" }); break;
            case "deleteMessage": if (mi >= 0 && mi < Hub.Messages.Count) Hub.Messages.RemoveAt(mi); break;
            case "setMessage": { var m = Hub.Messages[mi]; var f = r.GetProperty("field").GetString(); var v = r.GetProperty("value");
                switch (f) { case "name": m.Name = v.GetString() ?? m.Name; break; case "rt": m.Rt = Math.Clamp(v.GetInt32(), 0, 31); break; case "sa": m.Sa = Math.Clamp(v.GetInt32(), 0, 31); break; case "tr": m.Tr = v.GetInt32() == 1 ? 1 : 0; break; case "wc": m.Wc = Math.Clamp(v.GetInt32(), 1, 32); break; case "period_ms": m.PeriodMs = Math.Max(0, v.GetDouble()); break; case "bus": m.Bus = v.GetString() == "B" ? "B" : "A"; break; } break; }
            case "addWord": { var m = Hub.Messages[mi]; m.Words.Add(new IcdWord { Index = Math.Min(m.Wc, m.Words.Count + 1), BitStart = 1, BitLen = 16, Type = "BNR", Scale = 1, Offset = 0, Unit = "", Name = $"{m.Name}_W{m.Words.Count + 1}" }); break; }
            case "deleteWord": { var m = Hub.Messages[mi]; int wi = r.GetProperty("w").GetInt32(); if (wi >= 0 && wi < m.Words.Count) m.Words.RemoveAt(wi); break; }
            case "setWord": { var m = Hub.Messages[mi]; var w = m.Words[r.GetProperty("w").GetInt32()]; var f = r.GetProperty("field").GetString(); var v = r.GetProperty("value");
                switch (f) { case "index": w.Index = Math.Clamp(v.GetInt32(), 1, 32); break; case "bit_start": w.BitStart = Math.Clamp(v.GetInt32(), 1, 16); break; case "bit_len": w.BitLen = Math.Clamp(v.GetInt32(), 1, 512); break; case "type": w.Type = v.GetString() ?? w.Type; break; case "scale": w.Scale = v.GetDouble(); break; case "offset": w.Offset = v.GetDouble(); break; case "unit": w.Unit = v.GetString() ?? ""; break; case "name": w.Name = v.GetString() ?? w.Name; break; } break; }
        }
        if (IcdApplied) Attach(); else _e.Touch();
        return HubDto();
    }

    /// <summary>What an export would produce (messages, fields, formats) without writing anything.</summary>
    public object ExportPreview()
    {
        if (Hub == null) return new { ok = false };
        var errs = IcdHub.Validate(Hub);
        return new { ok = !errs.Any(e => e.StartsWith("error")), errors = errs, messages = Hub.Messages.Count, words = Hub.Messages.Sum(m => m.Words.Count), templateOk = TemplateOk(_e.IcdTemplateDir), simulatorDetected = BusSimProject.SimulatorDetected(),
            items = Hub.Messages.Select(m => new { m.Name, cwd = m.Cwd1.ToString("X4"), m.Rt, m.Sa, dir = m.Tr == 1 ? "RT->BC" : "BC->RT", m.Wc, fields = m.Words.Select(w => $"{w.Name} w{w.Index} b{w.BitStart}+{w.BitLen} {w.Type} ×{w.Scale}{(w.Offset != 0 ? "+" + w.Offset : "")} {w.Unit}").ToList() }).ToList() };
    }

    /// <summary>Export the hub: formats ⊆ {project, xml, device, api}. Returns per-format results incl. schema validation.</summary>
    public object Export(string outDir, string? templateDir, bool withRt, string[]? formats = null)
    {
        if (Hub == null) throw new InvalidOperationException("No ICD loaded");
        formats ??= new[] { "project", "xml", "device", "api" };
        Directory.CreateDirectory(outDir); string proj = new DirectoryInfo(outDir).Name;
        string? tdir = templateDir ?? (_e.IcdTemplateDir.Length > 0 ? _e.IcdTemplateDir : null); if (tdir != null && !TemplateOk(tdir)) tdir = null;
        var res = new Dictionary<string, object>();
        res["hub"] = new { json = Hub.SaveTo(Path.Combine(outDir, "icd_hub.json")), csv = WriteText(Path.Combine(outDir, "icd_hub.csv"), Hub.ToCsv()) };
        if (formats.Contains("project"))
        {
            var r = BusSimProject.Export(Hub, outDir, tdir, withRt);
            res["project"] = new { ok = r.FileWritten, r.Cdv, r.Cpj, template = tdir, note = r.FileWritten ? "" : "template folder not set — project files skipped" };
        }
        if (formats.Contains("xml")) { string xml = IcdXml.ToDbXml(Hub); string p = WriteText(Path.Combine(outDir, proj + "_messages.xml"), xml); var v = IcdXml.Validate(xml, IcdXml.DbSchema); var back = IcdXml.FromDbXml(xml); res["xml"] = new { ok = v.Count == 0 && IcdHub.SameShape(Hub, back), path = p, schemaErrors = v, roundTrip = IcdHub.SameShape(Hub, back) }; }
        if (formats.Contains("device")) { string xml = IcdXml.ToDeviceXml(Hub, proj); string p = WriteText(Path.Combine(outDir, proj + "_device.xml"), xml); var v = IcdXml.Validate(xml, IcdXml.DeviceSchema); res["device"] = new { ok = v.Count == 0, path = p, schemaErrors = v }; }
        if (formats.Contains("api")) { string p = WriteText(Path.Combine(outDir, proj + "_automation.py"), BusSimProject.ApiScript(Hub)); res["api"] = new { ok = true, path = p, simulatorDetected = BusSimProject.SimulatorDetected() }; }
        LastExport = outDir; _e.SetStatus($"ICD export → {outDir} ({string.Join(", ", res.Keys)})"); _e.Touch();
        return res;
    }
    static string WriteText(string path, string text) { System.IO.File.WriteAllText(path, text); return path; }
}
