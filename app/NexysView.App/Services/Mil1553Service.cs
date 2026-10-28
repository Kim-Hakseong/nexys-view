using System.IO;
using NexysView.Core.Data;
using NexysView.Core.Mil1553;

namespace NexysView.App.Services;

/// <summary>0.10.0: MIL-STD-1553 capture view (message list, filter, decode) and the ICD export module (feature flag).
/// Owned by the Engine; the bridge routes the "m1553.*" / "icd.*" commands here.</summary>
public sealed class Mil1553Service
{
    readonly Engine _e;
    public Mil1553Service(Engine e) { _e = e; }

    public IcdHub? Hub { get; private set; }
    public string HubPath { get; private set; } = "";
    public string? File { get; private set; }          // selected capture (file name)
    int[]? _filtered; int _rt = -1, _sa = -1, _tr = -1, _bus = -1; bool _errOnly;
    public int Selected { get; private set; } = -1;   // record index (unfiltered)
    public double LastFilterMs { get; private set; }
    public string LastExport { get; private set; } = "";

    DataFile? Df => _e.State.Files.FirstOrDefault(f => f.Format == Mil1553DataSource.Format && (File == null || f.Name == File)) ?? _e.State.Files.FirstOrDefault(f => f.Format == Mil1553DataSource.Format);
    Mil1553File? Cap => Df is { } d ? Mil1553DataSource.FileOf(d) : null;

    public object Dto()
    {
        var df = Df; var cap = Cap;
        return new
        {
            file = df?.Name, path = df?.Path, count = cap?.Count ?? 0, filtered = _filtered?.Length ?? cap?.Count ?? 0, selected = Selected,
            filter = new { rt = _rt, sa = _sa, tr = _tr, bus = _bus, errorsOnly = _errOnly }, filterMs = LastFilterMs,
            icd = Hub == null ? null : new { path = HubPath, messages = Hub.Messages.Count, words = Hub.Messages.Sum(m => m.Words.Count), matched = cap == null ? 0 : Hub.Messages.Count(m => MatchesAny(cap, m)) },
            files = _e.State.Files.Where(f => f.Format == Mil1553DataSource.Format).Select(f => f.Name).ToList(),
            templateDir = _e.IcdTemplateDir, templateOk = _e.IcdTemplateDir.Length > 0 && Directory.Exists(_e.IcdTemplateDir) && Directory.EnumerateFiles(_e.IcdTemplateDir, "*.CDV").Any(), lastExport = LastExport,
        };
    }

    static bool MatchesAny(Mil1553File cap, IcdMessage m) { ushort c = m.Cwd1; ushort mask = 0xFFE0; for (int i = 0; i < cap.Count; i++) if ((cap.Cwd1[i] & mask) == (c & mask)) return true; return false; }

    public void SelectFile(string? name) { File = name; _filtered = null; Selected = -1; _e.Touch(); }

    public void SetFilter(int rt, int sa, int tr, int bus, bool errorsOnly)
    {
        _rt = rt; _sa = sa; _tr = tr; _bus = bus; _errOnly = errorsOnly;
        var cap = Cap; var sw = System.Diagnostics.Stopwatch.StartNew();
        _filtered = cap == null || (rt < 0 && sa < 0 && tr < 0 && bus < 0 && !errorsOnly) ? null : cap.Filter(rt, sa, tr, errorsOnly, bus);
        LastFilterMs = sw.Elapsed.TotalMilliseconds; _e.Touch();
    }

    int Map(int row) => _filtered != null ? (row >= 0 && row < _filtered.Length ? _filtered[row] : -1) : row;
    public int RowCount => _filtered?.Length ?? Cap?.Count ?? 0;

    /// <summary>Rows [first, first+count) of the (filtered) list.</summary>
    public object? Rows(int first, int count)
    {
        var cap = Cap; if (cap == null) return null;
        count = Math.Clamp(count, 1, 5000); int total = RowCount; first = Math.Clamp(first, 0, Math.Max(0, total - 1));
        double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0;
        var rows = new List<object>(Math.Min(count, total - first));
        Span<ushort> d = stackalloc ushort[40];
        for (int r = first; r < Math.Min(total, first + count); r++)
        {
            int i = Map(r); if (i < 0) break;
            var rec = cap.Get(i); int n = cap.Data(i, d);
            var sb = new System.Text.StringBuilder(n * 5); for (int k = 0; k < n; k++) { if (k > 0) sb.Append(' '); sb.Append(d[k].ToString("X4")); }
            rows.Add(new { i, t = (cap.TimeUs[i] - t0) * 1e-6, bus = rec.BusB ? "B" : "A", rt = rec.Rt, sa = rec.Sa, tr = rec.Tr, wc = rec.Wc == 0 && n > 0 ? 32 : rec.Wc, cwd1 = rec.Cwd1, swd1 = rec.Swd1, err = rec.Error, act = rec.Activity, n, data = sb.ToString(), icd = Hub == null ? null : MsgOf(rec)?.Name });
        }
        return new { first, total, rows };
    }

    IcdMessage? MsgOf(Mil1553Record rec) => Hub?.Messages.FirstOrDefault(m => m.Rt == rec.Rt && m.Sa == rec.Sa && m.Tr == rec.Tr);

    /// <summary>Decode one record: command/status/error/activity text + words (raw hex; ICD name/EU/unit when the hub has the message).</summary>
    public object? Decode(int i)
    {
        var cap = Cap; if (cap == null || i < 0 || i >= cap.Count) return null;
        Selected = i; var rec = cap.Get(i); double t0 = cap.Count > 0 ? cap.TimeUs[0] : 0;
        Span<ushort> d = stackalloc ushort[40]; int n = cap.Data(i, d);
        var raw = new List<string>(n); for (int k = 0; k < n; k++) raw.Add(d[k].ToString("X4"));
        var msg = MsgOf(rec);
        var words = new List<object>();
        if (msg != null) foreach (var w in msg.Words) { long r = w.Raw(cap, i); words.Add(new { word = w.Index, bits = $"{w.BitStart}..{w.BitStart + w.BitLen - 1}", name = w.Name, raw = r == long.MinValue ? "" : (r & 0xFFFFFFFF).ToString("X" + (w.BitLen > 16 ? 8 : 4)), eu = r == long.MinValue ? (double?)null : r * w.Scale + w.Offset, unit = w.Unit, type = w.Type, channel = $"{Df!.Name}|ICD:{msg.Name}|{w.Name}" }); }
        var errs = new List<string>(); ushort e = rec.Error;
        foreach (var (bit, nm) in ErrBits) if ((e & bit) != 0) errs.Add(nm);
        var acts = new List<string>(); foreach (var (bit, nm) in ActBits) if ((rec.Activity & bit) != 0) acts.Add(nm);
        _e.Touch();
        return new
        {
            i, t = (cap.TimeUs[i] - t0) * 1e-6, tUs = cap.TimeUs[i], bus = rec.BusB ? "B" : "A", rt = rec.Rt, sa = rec.Sa, tr = rec.Tr, wc = rec.Wc == 0 && n > 0 ? 32 : rec.Wc,
            cwd1 = rec.Cwd1.ToString("X4"), cwd2 = rec.Cwd2.ToString("X4"), swd1 = rec.Swd1.ToString("X4"), swd2 = rec.Swd2.ToString("X4"), err = e.ToString("X4"), errs, act = rec.Activity.ToString("X4"), acts,
            resp1 = rec.RespTime1, resp2 = rec.RespTime2, n, raw, icd = msg?.Name, words,
            swdFlags = SwdFlags(rec.Swd1),
        };
    }

    static readonly (ushort, string)[] ErrBits = { (0x0001, "HIT"), (0x0002, "SYSTEM"), (0x0004, "ERR04"), (0x0008, "ERR08"), (0x0010, "ERR10"), (0x0020, "ERR20"), (0x0040, "ERR40"), (0x0080, "ERR80"), (0x0100, "ERR100"), (0x0200, "ERR200"), (0x0400, "ERR400"), (0x0800, "ERR800"), (0x1000, "ERR1000"), (0x2000, "ERR2000"), (0x4000, "ERR4000"), (0x8000, "ERR8000") };
    static readonly (ushort, string)[] ActBits = { (0x0800, "XMTCWD1"), (0x0400, "XMTCWD2"), (0x0200, "XMTSWD1"), (0x0100, "XMTSWD2"), (0x0080, "RCVCWD1"), (0x0040, "RCVCWD2"), (0x0020, "RCVSWD1"), (0x0010, "RCVSWD2"), (0x0008, "XMTDWD"), (0x0004, "RCVDWD"), (0x0001, "BUS B") };
    static List<string> SwdFlags(ushort s)
    {
        var l = new List<string>(); if ((s & 0x0400) != 0) l.Add("MSG ERROR"); if ((s & 0x0200) != 0) l.Add("INSTR"); if ((s & 0x0100) != 0) l.Add("SRQ"); if ((s & 0x0010) != 0) l.Add("BCAST RCVD"); if ((s & 0x0008) != 0) l.Add("BUSY"); if ((s & 0x0004) != 0) l.Add("SUBSYS"); if ((s & 0x0002) != 0) l.Add("DBC ACCEPT"); if ((s & 0x0001) != 0) l.Add("TERMINAL"); return l;
    }

    // ---------------------------------------------------------------- ICD module
    public bool Enabled { get => _e.IcdExport; }

    public object LoadIcd(string path)
    {
        var hub = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? IcdHub.Load(path) : path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? throw new NotSupportedException("XLSX: save the sheet as CSV (or JSON) first") : IcdHub.FromCsv(path);
        Hub = hub; HubPath = path;
        int added = 0; foreach (var f in _e.State.Files.Where(f => f.Format == Mil1553DataSource.Format)) added += Mil1553DataSource.AttachIcd(f, hub);
        _e.FilesChangedExternally();
        _e.SetStatus($"ICD {Path.GetFileName(path)}: {hub.Messages.Count} messages, {hub.Messages.Sum(m => m.Words.Count)} words, {added} EU channels");
        return new { messages = hub.Messages.Count, words = hub.Messages.Sum(m => m.Words.Count), channels = added };
    }

    public void LoadDummyIcd() { Hub = IcdHub.Dummy(); HubPath = "(built-in dummy)"; foreach (var f in _e.State.Files.Where(f => f.Format == Mil1553DataSource.Format)) Mil1553DataSource.AttachIcd(f, Hub); _e.FilesChangedExternally(); }

    public object Export(string outDir, string? templateDir, bool withRt)
    {
        if (Hub == null) throw new InvalidOperationException("No ICD loaded");
        string? tdir = templateDir ?? (_e.IcdTemplateDir.Length > 0 ? _e.IcdTemplateDir : null);
        if (tdir != null && !(Directory.Exists(tdir) && Directory.EnumerateFiles(tdir, "*.CDV").Any())) tdir = null;
        var r = BusSimProject.Export(Hub, outDir, tdir, withRt);
        LastExport = outDir;
        _e.SetStatus(r.FileWritten ? $"Exported project to {outDir}: {r.Messages} messages, {r.Words} words" : $"No template project found — wrote hub JSON/CSV and automation script to {outDir}");
        _e.Touch();
        return new { r.Cdv, r.Cpj, r.Script, r.HubJson, r.HubCsv, r.FileWritten, r.Messages, r.Words };
    }
}
