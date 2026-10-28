using System.Globalization;
using System.IO;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>One chart of the VIEW (or the ANALYSIS preview when Id == 0): the render model lives here, the web UI only draws it.</summary>
public sealed class Chart
{
    public int Id { get; }
    public PlotModel Model { get; } = new();
    /// <summary>0.9.3: id of the overview chart this chart is the detail of (its x window = the overview's band).</summary>
    public int? DetailOf { get; set; }
    /// <summary>0.9.4: page of the main window this chart belongs to; window 0 = main, N = a detached chart window.</summary>
    public int PageId { get; set; } = 1;
    public int WindowId { get; set; }
    public Chart(int id) { Id = id; }
}

/// <summary>
/// Application model behind the web UI: open files, charts (PlotModel per chart), selection, undo, compare, theme. Every command
/// of the bridge ends here; every change raises <see cref="Changed"/> (UI snapshot) and/or <see cref="ChartChanged"/> (new frame).
/// Events may fire on pool threads (pyramid progress); the bridge marshals them to the UI thread.
/// </summary>
public sealed class Engine
{
    public AppState State { get; } = new();
    public List<Chart> Charts { get; } = new();
    public Chart Preview { get; } = new(0);
    int _nextId = 1;
    public int ActiveChart { get; set; }
    public string? Selected { get; set; }          // selected channel id (properties panel)
    public string? SelectedFile { get; set; }      // selected file name (navigator mode properties)
    public List<string> SelectedFiles { get; } = new();
    public Dictionary<string, bool> Hier { get; } = new(StringComparer.OrdinalIgnoreCase);
    public const string DefaultTheme = "light", DefaultPlot = "dark";   // first start (no layout.json): light chrome, dark plot
    public string Theme { get; set; } = DefaultTheme;
    public string Plot { get; set; } = DefaultPlot;
    public bool Developer { get; set; }
    /// <summary>Feature tour finished or skipped once (layout.json); false on a first start → the page auto-starts the tour.</summary>
    public bool TourSeen { get; set; }
    // ---- 0.9.4: pages of the main window, detached chart windows
    public sealed class Page { public int Id; public string Name = ""; public int Columns = 1; }
    public List<Page> Pages { get; } = new() { new Page { Id = 1, Name = "Page 1" } };
    int _nextPage = 2;
    public int ActivePage { get; set; } = 1;
    public bool AutoPage { get; set; } = true;
    public int AutoPageN { get; set; } = 4;
    /// <summary>Open chart windows (id → title); charts carry <see cref="Chart.WindowId"/>. Managed by the host.</summary>
    public Dictionary<int, string> Windows { get; } = new();
    /// <summary>0.9.3: ANALYSIS / REPORT tabs are hidden unless layout.json says showLegacyTabs=true (engine and commands stay).</summary>
    public bool ShowLegacyTabs { get; set; }
    /// <summary>0.9.3: file/group drop rule — "unit" (one chart, y axis per unit, steps as lanes) or "each" (a chart per channel).</summary>
    public string DropMode { get; set; } = "unit";
    public string StatusText { get; private set; } = "";
    public string Led { get; private set; } = "idle";
    public string AlignMode { get; set; } = "start";
    public double AlignOffsetMs { get; set; } = 500;
    public string Mode { get; set; } = "VIEW";
    /// <summary>0.10.0: features.icdExport (layout.json) — shows the ICD import / project export tools; the 1553 view itself is always on.</summary>
    public bool IcdExport { get; set; }
    /// <summary>Folder of a bus-simulator template project (one .CPJ + the 1553 device .CDV) cloned by the ICD export; empty = file export off.</summary>
    public string IcdTemplateDir { get; set; } = "";
    public Mil1553Service Mil1553 { get; }
    /// <summary>Channel groups were added/removed outside AppState.Open (ICD attach): resend the file tree.</summary>
    public void FilesChangedExternally() { _fileDto.Clear(); FilesVersion++; Changed?.Invoke(); }
    public AnalysisService Analysis { get; }
    public ReportService Report { get; }
    public event Action? Changed;
    public event Action<int>? ChartChanged;   // chart id, -1 = all
    public event Action? UndoChanged;         // light: undo/redo availability only
    public event Action? StatusChanged;       // light: status text + LED only
    /// <summary>Bumped whenever the file list / channel readiness changes; the bridge resends the file tree only then.</summary>
    public int FilesVersion { get; private set; }
    long _lastProgressTick;
    readonly Dictionary<string, bool> _stepCache = new(), _timeCache = new();
    readonly Dictionary<DataFile, object> _fileDto = new();

    public Engine()
    {
        Analysis = new AnalysisService(this);
        Mil1553 = new Mil1553Service(this);
        Report = new ReportService(this);
        State.Status += s => { StatusText = s; StatusChanged?.Invoke(); };
        State.FilesChanged += () => { Led = State.Files.Count > 0 ? "ready" : "idle"; _fileDto.Clear(); FilesVersion++; Changed?.Invoke(); };
        State.FileClosing += RemoveFile;
        State.Undo.Changed += () => UndoChanged?.Invoke();
        State.Pyramids.Progress += (ch, n) =>
        {
            if (n >= ch.Length) return;
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastProgressTick) < 120) return;   // throttle: a 10M build reports hundreds of times
            Interlocked.Exchange(ref _lastProgressTick, now);
            Led = "busy"; StatusText = $"Loading {ch.Name}: {100.0 * n / ch.Length:F0}%"; StatusChanged?.Invoke(); ChartChanged?.Invoke(-1);
        };
        State.Pyramids.Completed += ch => { StatusText = $"{ch.Name}: {ch.Length:N0} samples ready ({ch.Pyramid?.BuildTime.TotalMilliseconds:F0} ms{(ch.Pyramid?.FromCache == true ? ", cache" : "")})"; Led = "ready"; _fileDto.Clear(); FilesVersion++; Changed?.Invoke(); ChartChanged?.Invoke(-1); };
    }

    public void SetStatus(string s) => State.SetStatus(s);
    public void SetLed(string led) { Led = led; StatusChanged?.Invoke(); }
    public void Touch() => Changed?.Invoke();
    public void TouchChart(int id) { Changed?.Invoke(); ChartChanged?.Invoke(id); }

    // ------------------------------------------------------------------ files
    public DataFile? OpenFile(string path)
    {
        if (path.EndsWith(".tdv", StringComparison.OrdinalIgnoreCase))
        {
            try { int n = Import.TdvImport.Apply(this, path); SetStatus($"Layout {Path.GetFileName(path)}: {n} areas"); Mode = "VIEW"; }
            catch (Exception ex) { SetStatus("Layout import failed: " + ex.Message); }
            return null;
        }
        try
        {
            var df = State.Open(path);
            if (df.Warnings.Count > 0) SetStatus($"Opened {df.Name} with {df.Warnings.Count} warning(s): {df.Warnings[0]}");
            SelectedFile ??= df.Name;
            return df;
        }
        catch (Exception ex) { Diag.Trace("open failed " + ex); SetStatus("Open failed: " + ex.Message); SetLed("error"); return null; }
    }

    public DataFile? OpenWithPlugin(string dataPath, string pluginPath)
    {
        try { return State.OpenWithPlugin(dataPath, pluginPath); }
        catch (Exception ex) { SetStatus("DataPlugin failed: " + ex.Message); return null; }
    }

    public void CloseFile(string name)
    {
        var f = State.Files.FirstOrDefault(x => x.Name == name);
        if (f != null) State.Close(f);
    }

    void RemoveFile(DataFile f)
    {
        foreach (var ch in AllCharts())
        {
            var m = ch.Model;
            int n = m.Curves.RemoveAll(c => c.Channel.FileName == f.Name || c.XChannel?.FileName == f.Name);
            if (n > 0) { m.YAxes.RemoveAll(a => !m.Curves.Any(c => c.Axis == a)); if (m.Curves.Count > 0) m.ZoomFull(); }
        }
        if (Selected != null && State.FindChannel(Selected)?.FileName == f.Name) Selected = null;
        if (SelectedFile == f.Name) SelectedFile = State.Files.FirstOrDefault(x => x != f)?.Name;
        SelectedFiles.Remove(f.Name);
        Analysis.OnFileClosed(f);
        ChartChanged?.Invoke(-1);
    }

    public Channel? Find(string? id) => id == null ? null : State.FindChannel(id);
    IEnumerable<Chart> AllCharts() { foreach (var c in Charts) yield return c; yield return Preview; }
    public Chart? Get(int id) => id == 0 ? Preview : Charts.FirstOrDefault(c => c.Id == id);

    // ------------------------------------------------------------------ charts
    public Chart AddChart(bool record = true, int at = -1, int? pageId = null)
    {
        var ch = new Chart(_nextId++);
        if (pageId == null && AutoPage && Pages.Any(p => p.Id == ActivePage) && Charts.Count(c => c.PageId == ActivePage && c.WindowId == 0) >= Math.Max(1, AutoPageN))
        {
            var np = AddPage(null, false); ActivePage = np.Id; SetStatus($"Page {Pages.Count} created (auto split after {AutoPageN} charts)");
        }
        ch.PageId = pageId ?? ActivePage;
        if (at < 0 || at > Charts.Count) Charts.Add(ch); else Charts.Insert(at, ch);
        ActiveChart = ch.Id;
        if (record) State.Undo.Push("New chart", () => RemoveChart(ch.Id, false), () => { if (!Charts.Contains(ch)) { Charts.Add(ch); Changed?.Invoke(); } });
        Changed?.Invoke();
        return ch;
    }

    public void RemoveChart(int id, bool record = true)
    {
        var ch = Charts.FirstOrDefault(c => c.Id == id); if (ch == null) return;
        int idx = Charts.IndexOf(ch);
        Charts.Remove(ch);
        if (record) State.Undo.Push("Remove chart", () => { Charts.Insert(Math.Min(idx, Charts.Count), ch); Changed?.Invoke(); }, () => RemoveChart(id, false));
        if (ActiveChart == id) ActiveChart = Charts.FirstOrDefault()?.Id ?? 0;
        Changed?.Invoke();
    }

    public void Swap(int a, int b)
    {
        int ia = Charts.FindIndex(c => c.Id == a), ib = Charts.FindIndex(c => c.Id == b);
        if (ia < 0 || ib < 0 || ia == ib) return;
        State.Undo.Do("Swap charts", () => { (Charts[ia], Charts[ib]) = (Charts[ib], Charts[ia]); Changed?.Invoke(); }, () => { (Charts[ia], Charts[ib]) = (Charts[ib], Charts[ia]); Changed?.Invoke(); });
    }

    /// <summary>Add a channel to a chart. zone: plot | left | right | x (use as x axis) | xy (scatter with xId as x).</summary>
    public Curve? AddCurve(int chartId, string channelId, string zone = "plot", string? xId = null, bool record = true)
    {
        var chart = Get(chartId); var ch = Find(channelId);
        if (chart == null || ch == null || ch.IsText) return null;
        var m = chart.Model;
        if (zone == "x") { SetXChannel(chartId, channelId); return null; }
        if (chart.DetailOf != null && zone != "xy") { SetDetailChannel(chartId, channelId); return m.Curves.FirstOrDefault(); }
        var x = zone == "xy" ? Find(xId) : null;
        var c = m.AddCurve(ch, x);
        if (x == null && IsStep(ch)) c.Step = true;
        if (zone == "right") c.Axis = m.YAxes.FirstOrDefault(a => a.Right) ?? NewAxis(m, ch.Unit, true);
        else if (zone == "left") c.Axis = m.YAxes.FirstOrDefault(a => !a.Right) ?? NewAxis(m, ch.Unit, false);
        if (m.Curves.Count == 1) m.ZoomFull();
        _ = State.EnsurePyramid(ch).ContinueWith(_ => { if (m.Curves.Count == 1) m.ZoomFull(); ChartChanged?.Invoke(chartId); Changed?.Invoke(); });
        if (x != null) _ = State.EnsurePyramid(x);
        if (record) State.Undo.Push($"Add {ch.Name}", () => RemoveCurve(chartId, m.Curves.IndexOf(c), false), () => { if (!m.Curves.Contains(c)) m.Curves.Add(c); TouchChart(chartId); });
        if (chartId > 0) ActiveChart = chartId;
        if (m.Curves.Count == 1 && SuggestedX(chart) is { } sug) SetStatus($"Time channel '{sug.Name}' found — use it as the x axis from the chart header or the properties panel");
        TouchChart(chartId);
        return c;
    }

    static YAxis NewAxis(PlotModel m, string unit, bool right) { var a = new YAxis("y" + m.YAxes.Count) { Unit = unit, Right = right }; m.YAxes.Add(a); return a; }

    public void RemoveCurve(int chartId, int idx, bool record = true)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (idx < 0 || idx >= m.Curves.Count) return;
        var c = m.Curves[idx]; m.Curves.RemoveAt(idx);
        if (record) State.Undo.Push($"Remove {c.Channel.Name}", () => { m.Curves.Insert(Math.Min(idx, m.Curves.Count), c); TouchChart(chartId); }, () => RemoveCurve(chartId, m.Curves.IndexOf(c), false));
        TouchChart(chartId);
    }

    public void MoveCurve(int fromChart, int idx, int toChart)
    {
        var a = Get(fromChart); var b = Get(toChart); if (a == null || b == null || a == b) return;
        if (idx < 0 || idx >= a.Model.Curves.Count) return;
        var c = a.Model.Curves[idx];
        State.Undo.Do($"Move {c.Channel.Name}",
            () => { a.Model.Curves.Remove(c); b.Model.Curves.Add(c); c.Axis = b.Model.AxisFor(c.Channel.Unit); if (b.Model.Curves.Count == 1) b.Model.ZoomFull(); TouchChart(fromChart); TouchChart(toChart); },
            () => { b.Model.Curves.Remove(c); a.Model.Curves.Add(c); c.Axis = a.Model.AxisFor(c.Channel.Unit); TouchChart(fromChart); TouchChart(toChart); });
    }

    public void SetView(int chartId, double x0, double x1, bool commit, double? fromX0 = null, double? fromX1 = null)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (!(x1 - x0 > 1e-12) || !double.IsFinite(x0) || !double.IsFinite(x1)) return;
        m.XMin = x0; m.XMax = x1;
        if (commit && fromX0 != null && fromX1 != null && (fromX0 != x0 || fromX1 != x1))
        {
            double ox0 = fromX0.Value, ox1 = fromX1.Value; m.PushZoom(ox0, ox1);
            State.Undo.Push("View", () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); AfterView(chartId); }, () => { m.XMin = x0; m.XMax = x1; TouchChart(chartId); AfterView(chartId); });
        }
        ChartChanged?.Invoke(chartId);
        AfterView(chartId);
        if (commit) Changed?.Invoke();
    }

    /// <summary>View update from a frame request: no invalidate round-trip; the undo entry is pushed on commit only.</summary>
    public void SetViewQuiet(int chartId, double x0, double x1, bool commit, double? fromX0, double? fromX1)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (!(x1 - x0 > 1e-12) || !double.IsFinite(x0) || !double.IsFinite(x1)) return;
        m.XMin = x0; m.XMax = x1;
        if (commit && fromX0 != null && fromX1 != null && (fromX0 != x0 || fromX1 != x1))
        {
            double ox0 = fromX0.Value, ox1 = fromX1.Value; m.PushZoom(ox0, ox1);
            State.Undo.Push("View", () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); AfterView(chartId); }, () => { m.XMin = x0; m.XMax = x1; TouchChart(chartId); AfterView(chartId); });
        }
        AfterView(chartId);
    }

    // ------------------------------------------------------------------ 0.9.3: x-axis link, overview/detail, band cursor, zoom box/back
    /// <summary>After the x window of a chart changed: a detail chart moves its overview's band, x-linked charts follow.</summary>
    public void AfterView(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (chart.DetailOf is int ov && Get(ov) is { } o)
        {
            var om = o.Model;
            if (!om.BandEnabled || om.BandX0 != m.XMin || om.BandX1 != m.XMax) { om.BandEnabled = true; om.BandX0 = m.XMin; om.BandX1 = m.XMax; ChartChanged?.Invoke(ov); }
        }
        PropagateLink(chart);
    }

    /// <summary>Charts with the same key share pan/zoom: the mapped x channel, else the file + x unit of the first curve.</summary>
    public string? LinkKey(Chart c)
    {
        var m = c.Model;
        if (!m.LinkX || m.Curves.Count == 0 || m.IsXY || c.DetailOf != null || c.Id == 0) return null;
        var xc = m.XChannelOf; if (xc != null) return "x:" + xc.Id;
        return "f:" + m.Curves[0].Channel.FileName + "|" + m.XUnit;
    }

    void PropagateLink(Chart src)
    {
        var key = LinkKey(src); if (key == null) return;
        foreach (var c in Charts)
        {
            if (c == src || LinkKey(c) != key) continue; var m = c.Model;
            if (m.XMin == src.Model.XMin && m.XMax == src.Model.XMax) continue;
            m.XMin = src.Model.XMin; m.XMax = src.Model.XMax; ChartChanged?.Invoke(c.Id);
        }
    }

    /// <summary>Band of an overview that this chart should show as faint reference lines (same link group).</summary>
    public (double x0, double x1)? BandRefFor(Chart c)
    {
        if (c.Model.BandEnabled) return null;
        var key = LinkKey(c); if (key == null) return null;
        foreach (var o in Charts) if (o != c && o.Model.BandEnabled && LinkKey(o) == key) return (o.Model.BandX0, o.Model.BandX1);
        return null;
    }

    public void SetLinkX(int chartId, bool on)
    {
        var chart = Get(chartId); if (chart == null) return;
        chart.Model.LinkX = on; if (on) PropagateLink(chart);
        Changed?.Invoke();
    }

    /// <summary>Band cursor of an overview: live (record=false, no snapshot) during a drag, committed (undoable) at the end.</summary>
    public void SetBand(int chartId, bool enabled, double x0, double x1, bool record)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (x1 < x0) (x0, x1) = (x1, x0);
        bool e0 = m.BandEnabled; double b0 = m.BandX0, b1 = m.BandX1;
        void Apply(bool en, double a, double b) { m.BandEnabled = en; m.BandX0 = a; m.BandX1 = b; ChartChanged?.Invoke(chartId); SyncDetail(chart); foreach (var c in Charts) if (c != chart && BandRefFor(c) != null) ChartChanged?.Invoke(c.Id); }
        m.BandLive = !record;
        if (record) { if (e0 == enabled && b0 == x0 && b1 == x1) { ChartChanged?.Invoke(chartId); return; } State.Undo.Do("Band", () => Apply(enabled, x0, x1), () => Apply(e0, b0, b1)); Changed?.Invoke(); }
        else Apply(enabled, x0, x1);
    }

    void SyncDetail(Chart overview)
    {
        var om = overview.Model; if (!om.BandEnabled) return;
        foreach (var d in Charts.Where(c => c.DetailOf == overview.Id))
        {
            var m = d.Model; if (m.XMin == om.BandX0 && m.XMax == om.BandX1) continue;
            m.XMin = om.BandX0; m.XMax = om.BandX1; ChartChanged?.Invoke(d.Id);
        }
    }

    public void SetCursorMode(int chartId, string mode)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        m.CursorMode = mode == "band" ? "band" : "free";
        if (m.CursorMode == "band" && !m.BandEnabled) { double w = m.XMax - m.XMin; m.BandEnabled = true; m.BandX0 = m.XMin + w * 0.4; m.BandX1 = m.XMin + w * 0.6; SyncDetail(chart); }
        TouchChart(chartId);
    }

    static void CopyX(Curve from, Curve to, PlotModel om, PlotModel dm)
    {
        to.XChannel = from.XChannel; to.XMonotonic = from.XMonotonic; to.XScale = from.XScale; to.XOffset = from.XOffset;
        dm.XUnit = om.XUnit; dm.XTimeMode = om.XTimeMode; dm.XTimeOrigin = om.XTimeOrigin;
    }

    /// <summary>Create the detail chart of an overview: one curve (the selected channel of the overview, else its first), x window = band.</summary>
    public Chart? LinkDetail(int chartId, string? channelId = null)
    {
        var ov = Get(chartId); if (ov == null || ov.Id == 0 || ov.Model.Curves.Count == 0) return null; var m = ov.Model;
        var src = m.Curves.FirstOrDefault(c => c.Channel.Id == (channelId ?? Selected)) ?? m.Curves[0];
        if (m.CursorMode != "band" || !m.BandEnabled) { m.CursorMode = "band"; if (!m.BandEnabled) { double w = m.XMax - m.XMin; m.BandEnabled = true; m.BandX0 = m.XMin + w * 0.4; m.BandX1 = m.XMin + w * 0.6; } }
        var existing = Charts.FirstOrDefault(c => c.DetailOf == ov.Id);
        if (existing != null) { SetDetailChannel(existing.Id, src.Channel.Id); return existing; }
        var d = AddChart(record: true, at: Charts.IndexOf(ov) + 1, pageId: ov.PageId); d.DetailOf = ov.Id; d.WindowId = ov.WindowId;
        var dm = d.Model; dm.Title = "Detail · " + src.Channel.Name; dm.LinkX = false;
        var cv = dm.AddCurve(src.Channel, null); CopyX(src, cv, m, dm); cv.Step = src.Step; cv.ColorIndex = src.ColorIndex; cv.Width = src.Width;
        dm.XMin = m.BandX0; dm.XMax = m.BandX1;
        _ = State.EnsurePyramid(src.Channel).ContinueWith(_ => ChartChanged?.Invoke(d.Id));
        ActiveChart = ov.Id; TouchChart(ov.Id); TouchChart(d.Id);
        SetStatus($"Detail chart linked to '{(string.IsNullOrEmpty(m.Title) ? "Chart " + (Charts.IndexOf(ov) + 1) : m.Title)}' — drag the band or drop another channel on the detail chart");
        return d;
    }

    /// <summary>Replace the single curve of a detail chart (legend click on the overview, properties, or a drop on the detail chart).</summary>
    public void SetDetailChannel(int detailId, string channelId)
    {
        var d = Get(detailId); var ch = Find(channelId); if (d == null || ch == null || ch.IsText) return; var dm = d.Model;
        var ov = d.DetailOf is int o ? Get(o) : null; var src = ov?.Model.Curves.FirstOrDefault(c => c.Channel == ch);
        var old = dm.Curves.ToList(); var oldAxes = dm.YAxes.ToList(); double x0 = dm.XMin, x1 = dm.XMax; string t0 = dm.Title;
        void Apply()
        {
            dm.Curves.Clear(); dm.YAxes.Clear();
            var cv = dm.AddCurve(ch, null);
            if (src != null && ov != null) { CopyX(src, cv, ov.Model, dm); cv.Step = src.Step; cv.ColorIndex = src.ColorIndex; cv.Width = src.Width; } else cv.Step = IsStep(ch);
            dm.Title = "Detail · " + ch.Name; dm.XMin = x0; dm.XMax = x1;
            _ = State.EnsurePyramid(ch).ContinueWith(_ => ChartChanged?.Invoke(detailId));
            TouchChart(detailId);
        }
        State.Undo.Do("Detail " + ch.Name, Apply, () => { dm.Curves.Clear(); dm.YAxes.Clear(); dm.Curves.AddRange(old); dm.YAxes.AddRange(oldAxes); dm.Title = t0; dm.XMin = x0; dm.XMax = x1; TouchChart(detailId); });
    }

    /// <summary>Right-drag zoom: x window and/or y ranges (fractions from the top of the plot, 0..1); undoable, on the zoom history.</summary>
    public void ZoomBox(int chartId, double? x0, double? x1, double? fy0, double? fy1)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        m.PushZoom(m.XMin, m.XMax);
        double ox0 = m.XMin, ox1 = m.XMax; var ax0 = m.YAxes.Select(a => (a, a.Auto, a.Min, a.Max)).ToList();
        void Apply()
        {
            if (x0 != null && x1 != null && x1.Value - x0.Value > 1e-12) { m.XMin = x0.Value; m.XMax = x1.Value; }
            if (fy0 != null && fy1 != null && fy1.Value - fy0.Value > 1e-6)
                foreach (var a in m.YAxes)
                {
                    double top = Math.Clamp(fy0.Value, 0, 1), bot = Math.Clamp(fy1.Value, 0, 1), mn, mx;
                    if (a.Log && a.Min > 0 && a.Max > 0) { double lmin = Math.Log10(a.Min), lmax = Math.Log10(a.Max); mn = Math.Pow(10, lmax - (lmax - lmin) * bot); mx = Math.Pow(10, lmax - (lmax - lmin) * top); }
                    else { mn = a.Max - (a.Max - a.Min) * bot; mx = a.Max - (a.Max - a.Min) * top; }
                    if (mx - mn > 0) { a.Auto = false; a.Min = mn; a.Max = mx; }
                }
            TouchChart(chartId); AfterView(chartId);
        }
        State.Undo.Do("Zoom", Apply, () => { m.XMin = ox0; m.XMax = ox1; foreach (var (a, au, mn, mx) in ax0) { a.Auto = au; a.Min = mn; a.Max = mx; } TouchChart(chartId); AfterView(chartId); });
    }

    public bool ZoomBack(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return false;
        if (!chart.Model.ZoomBack()) { SetStatus("No earlier zoom to go back to"); return false; }
        TouchChart(chartId); AfterView(chartId); return true;
    }

    public void SetAxis(int chartId, string axisId, bool? auto, double? min, double? max, bool? log, bool? right)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model; var a = m.YAxes.FirstOrDefault(x => x.Id == axisId); if (a == null) return;
        var s0 = (a.Auto, a.Min, a.Max, a.Log, a.Right);
        bool au = auto ?? (min != null || max != null ? false : s0.Auto); double mn = min ?? s0.Min, mx = max ?? s0.Max;
        if (mx <= mn) { mx = mn + 1; }
        void Apply(bool a1, double a2, double a3, bool a4, bool a5) { a.Auto = a1; a.Min = a2; a.Max = a3; a.Log = a4; a.Right = a5; TouchChart(chartId); }
        State.Undo.Do("Axis", () => Apply(au, mn, mx, log ?? s0.Log, right ?? s0.Right), () => Apply(s0.Auto, s0.Min, s0.Max, s0.Log, s0.Right));
    }

    /// <summary>Drop of a file / group / name node: one chart with a y axis per unit (steps as lanes) or a chart per channel;
    /// the file's time channel becomes the x axis when every curve has its length. Large drops are not confirmed, only reported.</summary>
    public int DropGroup(IEnumerable<string> ids, string? mode = null)
    {
        mode ??= DropMode;
        var chans = ids.Select(Find).Where(c => c != null && !c!.IsText).Select(c => c!).Distinct().Take(256).ToList();
        if (chans.Count == 0) return 0;
        var file = State.FileOf(chans[0]);
        var timeCh = file != null ? XIndex.SuggestX(file.AllChannels, chans[0].Length) : null;
        if (timeCh != null && chans.Count > 1) chans.Remove(timeCh);
        var charts = new List<Chart>();
        if (mode == "each") foreach (var ch in chans) { var c = AddChart(record: charts.Count == 0); AddCurve(c.Id, ch.Id, record: false); charts.Add(c); }
        else
        {
            var c = AddChart(); foreach (var ch in chans) AddCurve(c.Id, ch.Id, record: false);
            if (c.Model.Curves.Any(cv => cv.Step) && c.Model.Curves.Any(cv => !cv.Step)) c.Model.DigitalLanes = true;
            charts.Add(c);
        }
        if (timeCh != null) foreach (var c in charts) if (c.Model.Curves.All(cv => cv.Channel.Length == timeCh.Length)) { c.Model.SetXChannel(timeCh); _ = State.EnsurePyramid(timeCh); }
        Mode = "VIEW"; ActiveChart = charts[0].Id;
        SetStatus($"{chans.Count} channel(s) → {charts.Count} chart(s)" + (timeCh != null ? $" · x = {timeCh.Name}" : "") + (chans.Count > 50 ? $" · large drop ({chans.Count} channels)" : ""));
        Changed?.Invoke(); ChartChanged?.Invoke(-1);
        return charts.Count;
    }

    /// <summary>Row of a value table at the active chart's cursor A (or band start): sample index of the table's first channel.</summary>
    public long? CursorRowOf(Table t)
    {
        if (t.Channels.Count == 0) return null; var chart = Get(ActiveChart); if (chart == null) return null; var m = chart.Model;
        double? x = m.CursorA.Enabled ? m.CursorA.X : m.BandEnabled ? m.BandX0 : null; if (x == null) return null;
        var ch = t.Channels[0]; var cv = m.Curves.FirstOrDefault(c => c.Channel == ch);
        double i = cv != null && cv.XMonotonic && cv.XChannel != null ? XIndex.IndexOf(cv.XChannel, (x.Value - cv.XOffset) / cv.XScale) : ch.IndexOf(x.Value - (cv?.XOffset ?? 0));
        if (!double.IsFinite(i)) return null;
        return Math.Clamp((long)Math.Round(i), 0, Math.Max(0, ch.Length - 1));
    }

    /// <summary>CSV / TDMS of the given channels over an x range of a chart (null = whole channels).</summary>
    public int ExportRange(string path, bool tdms, IEnumerable<string> ids, int chartId, double? x0, double? x1)
    {
        var chans = ids.Select(Find).Where(c => c != null && !c!.IsText).Select(c => c!).ToList();
        if (chans.Count == 0) throw new InvalidOperationException("no channels to export");
        var chart = Get(chartId); var cv0 = chart?.Model.Curves.FirstOrDefault(c => c.Channel == chans[0]);
        long first = 0, last = chans.Max(c => c.Length) - 1;
        if (x0 != null && x1 != null)
        {
            double Idx(double x) => cv0 != null && cv0.XMonotonic && cv0.XChannel != null ? XIndex.IndexOf(cv0.XChannel, (x - cv0.XOffset) / cv0.XScale) : chans[0].IndexOf(x - (cv0?.XOffset ?? 0));
            double ia = Idx(Math.Min(x0.Value, x1.Value)), ib = Idx(Math.Max(x0.Value, x1.Value));
            first = Math.Max(0, (long)Math.Ceiling(ia)); last = Math.Min(last, (long)Math.Floor(ib));
        }
        if (last < first) throw new InvalidOperationException("the range holds no sample");
        int n = tdms ? Export.TdmsExport.SaveRange(path, chans, first, last) : Export.CsvExport.Write(path, chans, first, last, cv0);
        SetStatus($"Exported {n} channel(s), samples {first:N0}–{last:N0} → {path}");
        return n;
    }

    public void ZoomFull(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        double ox0 = m.XMin, ox1 = m.XMax;
        var (a, b) = m.FullX(); m.PushZoom(ox0, ox1);
        State.Undo.Do("Zoom full", () => { m.XMin = a; m.XMax = b; foreach (var ax in m.YAxes) ax.Auto = true; TouchChart(chartId); AfterView(chartId); }, () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); AfterView(chartId); });
    }

    public void SetCursor(int chartId, string which, double? x)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        var cur = which == "b" ? m.CursorB : m.CursorA;
        bool wasOn = cur.Enabled; double was = cur.X;
        State.Undo.Do("Cursor", () => { cur.Enabled = x != null; if (x != null) cur.X = x.Value; TouchChart(chartId); }, () => { cur.Enabled = wasOn; cur.X = was; TouchChart(chartId); });
    }

    public void ClearCursors(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (!m.CursorA.Enabled && !m.CursorB.Enabled) return;
        bool a = m.CursorA.Enabled, b = m.CursorB.Enabled;
        State.Undo.Do("Clear cursors", () => { m.CursorA.Enabled = m.CursorB.Enabled = false; TouchChart(chartId); }, () => { m.CursorA.Enabled = a; m.CursorB.Enabled = b; TouchChart(chartId); });
    }

    public void SetCurve(int chartId, int idx, bool? visible = null, bool? step = null, int? color = null, bool? right = null, double? width = null, bool? points = null, string? axis = null)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (idx < 0 || idx >= m.Curves.Count) return;
        var c = m.Curves[idx];
        bool v0 = c.Visible, s0 = c.Step, r0 = c.Axis.Right, p0 = c.Points; int c0 = c.ColorIndex; var ax0 = c.Axis; float w0 = c.Width;
        void Apply(bool v, bool s, int col, bool r, YAxis? ax, float wd, bool pt, string? axMode)
        {
            c.Visible = v; c.Step = s; c.ColorIndex = col; c.Width = wd; c.Points = pt;
            if (ax != null) c.Axis = ax;
            else if (axMode == "own") { var na = new YAxis($"y{m.YAxes.Count}_{idx}") { Unit = c.Channel.Unit, Right = r }; m.YAxes.Add(na); c.Axis = na; }
            else if (axMode == "shared") c.Axis = m.YAxes.FirstOrDefault(a => a.Unit == c.Channel.Unit && a.Right == r && m.Curves.Any(cc => cc != c && cc.Axis == a)) ?? c.Axis;
            else if (r != c.Axis.Right) c.Axis = m.YAxes.FirstOrDefault(a => a.Right == r) ?? NewAxis(m, c.Channel.Unit, r);
            m.YAxes.RemoveAll(a => !m.Curves.Any(cc => cc.Axis == a));
            TouchChart(chartId);
        }
        State.Undo.Do("Curve", () => Apply(visible ?? v0, step ?? s0, color ?? c0, right ?? r0, null, (float)(width ?? w0), points ?? p0, axis), () => Apply(v0, s0, c0, r0, m.YAxes.Contains(ax0) ? ax0 : null, w0, p0, null));
    }

    public void SetChart(int chartId, string? title = null, bool? grid = null, bool? legend = null, bool? lanes = null, string? timeMode = null)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        string t0 = m.Title; bool g0 = m.ShowGrid, l0 = m.ShowLegend, d0 = m.DigitalLanes; string tm0 = m.XTimeMode;
        void Apply(string t, bool g, bool l, bool d, string tm) { m.Title = t; m.ShowGrid = g; m.ShowLegend = l; m.DigitalLanes = d; m.XTimeMode = tm; TouchChart(chartId); }
        State.Undo.Do("Chart", () => Apply(title ?? t0, grid ?? g0, legend ?? l0, lanes ?? d0, timeMode ?? tm0), () => Apply(t0, g0, l0, d0, tm0));
    }

    /// <summary>Map every curve of the chart to <paramref name="channelId"/> (undoable). Returns the number of curves that did not fit.</summary>
    public int SetXChannel(int chartId, string channelId)
    {
        var chart = Get(chartId); var x = Find(channelId); if (chart == null || x == null) return -1;
        var m = chart.Model;
        if (!XIndex.IsMonotonic(x)) { SetStatus($"{x.Name} is not monotonic — cannot be an X axis"); return -1; }
        if (m.Curves.Count == 0) { SetStatus("Add a curve first, then drop the time channel on the x axis"); return -1; }
        var snapshot = m.Curves.Select(c => (c, c.XChannel, c.XMonotonic, c.XScale)).ToList();
        string mode = m.XTimeMode; double origin = m.XTimeOrigin; string unit = m.XUnit; double ox0 = m.XMin, ox1 = m.XMax;
        int miss = 0;
        State.Undo.Do($"X = {x.Name}",
            () => { miss = m.SetXChannel(x); _ = State.EnsurePyramid(x); TouchChart(chartId); },
            () => { foreach (var (c, xc, mono, sc) in snapshot) { c.XChannel = xc; c.XMonotonic = mono; c.XScale = sc; if (!m.Curves.Contains(c)) m.Curves.Add(c); } m.XTimeMode = mode; m.XTimeOrigin = origin; m.XUnit = unit; m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); });
        SetStatus(miss == 0 ? $"X axis: {x.Name} [{x.Unit}] · {(m.XTimeMode == "absolute" ? "absolute time" : m.XTimeMode == "relative" ? "relative time" : "numeric")}" : $"X axis: {x.Name} — {miss} curve(s) skipped (sample count differs)");
        return miss;
    }

    public void ClearX(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        var snapshot = m.Curves.Select(c => (c, c.XChannel, c.XMonotonic, c.XScale)).ToList();
        string mode = m.XTimeMode; double origin = m.XTimeOrigin; string unit = m.XUnit;
        State.Undo.Do("Clear X", () => { m.ClearXChannel(); TouchChart(chartId); },
            () => { foreach (var (c, xc, mono, sc) in snapshot) { c.XChannel = xc; c.XMonotonic = mono; c.XScale = sc; } m.XTimeMode = mode; m.XTimeOrigin = origin; m.XUnit = unit; m.ZoomFull(); TouchChart(chartId); });
    }

    public Channel? SuggestedX(Chart chart)
    {
        var m = chart.Model;
        if (m.Curves.Count == 0 || m.XChannelOf != null || m.IsXY) return null;
        var first = m.Curves[0].Channel;
        var file = State.FileOf(first);
        return file == null ? null : XIndex.SuggestX(file.AllChannels, first.Length);
    }

    public void Undo() => State.Undo.Undo();
    public void Redo() => State.Undo.Redo();

    // ------------------------------------------------------------------ navigator features
    public bool IsHier(DataFile f) => Hier.TryGetValue(f.Name, out var h) ? h : f.AllChannels.Count(c => c.Name.IndexOfAny(new[] { '.', '/' }) >= 0) >= 3;

    public bool IsStep(Channel c)
    {
        if (_stepCache.TryGetValue(c.Id, out var s)) return s;
        var f = State.FileOf(c);
        s = (f == null || f.ChannelCount <= 256) && XIndex.IsStepLike(c);
        _stepCache[c.Id] = s; return s;
    }

    public bool IsTimeCandidate(Channel c)
    {
        if (_timeCache.TryGetValue(c.Id, out var s)) return s;
        s = XIndex.TimeScale(c.Unit) > 0 && XIndex.IsMonotonic(c);
        _timeCache[c.Id] = s; return s;
    }

    public Chart DropAll(IEnumerable<string> ids)
    {
        var chart = AddChart();
        foreach (var id in ids.Take(64)) AddCurve(chart.Id, id, record: false);
        Mode = "VIEW";
        return chart;
    }

    /// <summary>v3 B4: one chart per channel name, one curve per file (color = file index), each curve on its own file's time channel.</summary>
    public int CreateComparison(IReadOnlyList<DataFile> files, IEnumerable<string> channelNames, string mode, double offsetMs)
    {
        var xs = files.Select(f => XIndex.SuggestX(f.AllChannels, f.AllChannels.FirstOrDefault(c => !c.IsText)?.Length ?? -1)).ToArray();
        var x0 = xs.Select(x => x == null ? double.NaN : XIndex.Range(x).first * XIndex.TimeScale(x.Unit)).ToArray();
        var abs = files.Select(Compare.FileStart).ToArray();
        var off = Compare.AlignOffsets(mode, offsetMs, x0, abs);
        int made = 0;
        foreach (var name in channelNames)
        {
            var chart = AddChart(record: made == 0);
            var m = chart.Model;
            for (int i = 0; i < files.Count; i++)
            {
                var ch = files[i].AllChannels.FirstOrDefault(c => c.Name == name && !c.IsText);
                if (ch == null) continue;
                var x = xs[i] != null && xs[i]!.Length == ch.Length ? xs[i] : null;
                var cv = m.AddCurve(ch);
                cv.ColorIndex = i; cv.Step = IsStep(ch);
                if (x != null) { cv.XChannel = x; cv.XMonotonic = true; cv.XScale = XIndex.TimeScale(x.Unit); _ = State.EnsurePyramid(x); }
                cv.XOffset = off[i];
                _ = State.EnsurePyramid(ch).ContinueWith(_ => { ChartChanged?.Invoke(chart.Id); });
            }
            m.Title = name; m.XUnit = "s"; m.XTimeMode = "relative"; m.XTimeOrigin = 0; m.AlignMode = mode; m.AlignOffsetMs = offsetMs;
            m.ZoomFull(); made++;
        }
        Mode = "VIEW";
        SetStatus($"Compare: {made} chart(s), {files.Count} files, alignment {mode}");
        Changed?.Invoke(); ChartChanged?.Invoke(-1);
        return made;
    }

    public List<DataFile> FilesByName(IEnumerable<string> names) => names.Select(n => State.Files.FirstOrDefault(f => f.Name == n)).Where(f => f != null).Select(f => f!).ToList();

    // ------------------------------------------------------------------ value tables
    public sealed class Table { public int Id; public List<Channel> Channels = new(); public int PageId = 1; }
    public List<Table> Tables { get; } = new();
    int _nextTable = 1;
    public Table AddTable() { var t = new Table { Id = _nextTable++, PageId = ActivePage }; Tables.Add(t); Changed?.Invoke(); return t; }

    // ------------------------------------------------------------------ 0.9.4: pages
    public Page AddPage(string? name = null, bool select = true)
    {
        var p = new Page { Id = _nextPage++, Name = name ?? $"Page {Pages.Count + 1}", Columns = Pages.LastOrDefault()?.Columns ?? 1 };
        Pages.Add(p); if (select) ActivePage = p.Id; Changed?.Invoke(); return p;
    }
    public void ResetPageCounter() { _nextPage = Pages.Count == 0 ? 2 : Pages.Max(p => p.Id) + 1; }
    public void RenamePage(int id, string name) { var p = Pages.FirstOrDefault(x => x.Id == id); if (p == null || string.IsNullOrWhiteSpace(name)) return; p.Name = name.Trim(); Changed?.Invoke(); }
    public void MovePageTo(int id, int index) { var p = Pages.FirstOrDefault(x => x.Id == id); if (p == null) return; Pages.Remove(p); Pages.Insert(Math.Clamp(index, 0, Pages.Count), p); Changed?.Invoke(); }
    public void MovePage(int id, int dir) { int i = Pages.FindIndex(x => x.Id == id), j = i + dir; if (i < 0 || j < 0 || j >= Pages.Count) return; (Pages[i], Pages[j]) = (Pages[j], Pages[i]); Changed?.Invoke(); }
    /// <summary>Remove a page; its charts and tables move to the first remaining page (nothing is deleted).</summary>
    public void RemovePage(int id)
    {
        if (Pages.Count <= 1) { SetStatus("The last page cannot be removed"); return; }
        var p = Pages.FirstOrDefault(x => x.Id == id); if (p == null) return;
        Pages.Remove(p); var target = Pages[0];
        int moved = 0; foreach (var c in Charts.Where(c => c.PageId == id)) { c.PageId = target.Id; moved++; } foreach (var t in Tables.Where(t => t.PageId == id)) t.PageId = target.Id;
        if (ActivePage == id) ActivePage = target.Id;
        if (moved > 0) SetStatus($"{moved} chart(s) moved to {target.Name}");
        Changed?.Invoke();
    }
    public void SelectPage(int id) { if (Pages.Any(p => p.Id == id)) { ActivePage = id; var first = Charts.FirstOrDefault(c => c.PageId == id && c.WindowId == 0); if (first != null) ActiveChart = first.Id; Changed?.Invoke(); } }
    public void SetPageColumns(int id, int n) { var p = Pages.FirstOrDefault(x => x.Id == id); if (p == null) return; p.Columns = n == 2 ? 2 : 1; Changed?.Invoke(); }
    public void SetAutoPage(bool on, int n) { AutoPage = on; AutoPageN = Math.Clamp(n, 1, 32); Changed?.Invoke(); }
    public void MoveChartToPage(int chartId, int pageId) { var c = Get(chartId); if (c == null || c.Id == 0 || !Pages.Any(p => p.Id == pageId)) return; c.PageId = pageId; Changed?.Invoke(); }

    // ------------------------------------------------------------------ 0.9.4: chart windows (the host creates/closes the WPF windows)
    public void DetachChart(int chartId, int windowId, string title)
    {
        var c = Get(chartId); if (c == null || c.Id == 0) return;
        Windows[windowId] = title; c.WindowId = windowId;
        foreach (var d in Charts.Where(x => x.DetailOf == chartId)) d.WindowId = windowId;   // a detail follows its overview
        Changed?.Invoke(); ChartChanged?.Invoke(chartId);
    }
    public void ReturnChart(int chartId) { var c = Get(chartId); if (c == null) return; c.WindowId = 0; foreach (var d in Charts.Where(x => x.DetailOf == chartId)) d.WindowId = 0; Changed?.Invoke(); ChartChanged?.Invoke(chartId); }
    /// <summary>Window closed by the user: every chart in it returns to the main window (nothing is deleted).</summary>
    public int CloseWindow(int windowId)
    {
        int n = 0; foreach (var c in Charts.Where(c => c.WindowId == windowId)) { c.WindowId = 0; n++; }
        Windows.Remove(windowId); Changed?.Invoke(); ChartChanged?.Invoke(-1); return n;
    }
    public void RemoveTable(int id) { Tables.RemoveAll(t => t.Id == id); Changed?.Invoke(); }
    public void TableAdd(int id, string channelId) { var t = Tables.FirstOrDefault(x => x.Id == id); var c = Find(channelId); if (t != null && c != null && !t.Channels.Contains(c)) { t.Channels.Add(c); Changed?.Invoke(); } }
    public object? TableRows(int id, long first, int count)
    {
        var t = Tables.FirstOrDefault(x => x.Id == id); if (t == null) return null;
        count = Math.Clamp(count, 1, 200_000); long maxLen = t.Channels.Count == 0 ? 0 : t.Channels.Max(c => c.Length);
        first = Math.Clamp(first, 0, Math.Max(0, maxLen - 1));
        var cols = new List<object>(); var chart = Get(ActiveChart);
        foreach (var c in t.Channels)
        {
            var buf = new double[count]; int got = c.Length > first ? c.Source.Read(first, buf.AsSpan(0, (int)Math.Min(count, c.Length - first))) : 0;
            var cv = chart?.Model.Curves.FirstOrDefault(x => x.Channel == c);
            cols.Add(new { id = c.Id, name = c.Name, unit = c.Unit, values = buf.Take(got).Select(v => double.IsNaN(v) ? (double?)null : v).ToList(), x = Enumerable.Range(0, got).Select(i => cv != null ? cv.XAt(first + i) : c.XAt(first + i)).ToList() });
        }
        return new { first, count, total = maxLen, columns = cols };
    }

    // ------------------------------------------------------------------ snapshots for the web UI
    public static string Hex(int colorIndex) => CurvePalette.HexOf(colorIndex);

    public object Snapshot(bool withFiles = true)
    {
        var files = withFiles ? State.Files.Select(FileDto).ToList() : null;
        var charts = Charts.Select(ChartDto).ToList();
        var sparkIds = new HashSet<string>(Charts.SelectMany(c => c.Model.Curves.Select(cv => cv.Channel.Id)));
        if (Selected != null) sparkIds.Add(Selected);
        var sparks = new Dictionary<string, float[]>();
        foreach (var id in sparkIds) { var ch = Find(id); if (ch?.Pyramid != null) sparks[id] = Sparkline(ch); }
        return new
        {
            theme = Theme, plot = Plot, mode = Mode, developer = Developer, legacyTabs = ShowLegacyTabs, dropMode = DropMode,
            features = new { icdExport = IcdExport }, mil1553 = Mil1553.Dto(),
            pages = Pages.Select(p => new { id = p.Id, name = p.Name, columns = p.Columns, charts = Charts.Count(c => c.PageId == p.Id && c.WindowId == 0) }).ToList(), activePage = ActivePage, autoPage = new { on = AutoPage, n = AutoPageN },
            windows = Windows.Select(kv => new { id = kv.Key, title = kv.Value, charts = Charts.Count(c => c.WindowId == kv.Key) }).ToList(),
            app = new { version = AppInfo.Version, versionText = AppInfo.VersionText, beta = AppInfo.IsBeta, repo = AppInfo.Repo, issues = AppInfo.Issues, tourSeen = TourSeen, automation = SelfTest.Enabled },
            status = new { text = StatusText, led = Led },
            undo = new { can = State.Undo.CanUndo, name = State.Undo.UndoName, canRedo = State.Undo.CanRedo },
            files, filesVersion = FilesVersion, charts, sparks,
            activeChart = ActiveChart, selected = Selected, selectedFile = SelectedFile, selectedFiles = SelectedFiles,
            align = new { mode = AlignMode, offsetMs = AlignOffsetMs },
            common = Mode == "NAVIGATOR" || SelectedFiles.Count >= 2 ? (SelectedFiles.Count >= 2 ? Compare.CommonChannels(FilesByName(SelectedFiles)) : State.Files.Count >= 2 ? Compare.CommonChannels(State.Files.Where(f => f.Name != "Analysis").ToList()) : new List<string>()) : new List<string>(),
            props = Selected != null ? ChannelProps(Selected) : null,
            fileProps = SelectedFile != null ? FileProps(SelectedFile) : null,
            analysis = Analysis.Dto(),
            report = Report.Dto(),
            preview = Preview.Model.Curves.Count > 0 ? ChartDto(Preview) : null,
            tables = Tables.Select(t => new { id = t.Id, pageId = t.PageId, channels = t.Channels.Select(c => new { id = c.Id, name = c.Name, unit = c.Unit, length = c.Length }).ToList(), rows = t.Channels.Count == 0 ? 0 : t.Channels.Max(c => c.Length), cursorRow = CursorRowOf(t) }).ToList(),
            cache = new { ready = State.Files.SelectMany(f => f.AllChannels).Count(c => c.Pyramid != null), total = State.Files.Sum(f => f.ChannelCount), mb = State.Files.SelectMany(f => f.AllChannels).Sum(c => c.Pyramid?.MemoryBytes ?? 0) / 1048576.0 },
        };
    }

    object FileDto(DataFile f)
    {
        if (_fileDto.TryGetValue(f, out var cached)) return cached;
        long size = 0; try { if (File.Exists(f.Path)) size = new FileInfo(f.Path).Length; } catch { }
        var (t, dur, rate) = Compare.TimeInfo(f);
        double start = Compare.FileStart(f);
        var dto = new
        {
            name = f.Name, path = f.Path, dir = Path.GetDirectoryName(f.Path) ?? "", format = f.Format.ToUpperInvariant(), writer = "",   // 0.9.2: no writer/product label, the format chip is enough
            channelCount = f.ChannelCount, samples = f.AllChannels.Sum(c => c.Length), sizeBytes = size, openMs = f.OpenTime.TotalMilliseconds,
            start = double.IsFinite(start) ? start : (double?)null, startText = double.IsFinite(start) ? TimeAxis.DateOf(start).Replace(" UTC", "") + " " + TimeAxis.FormatAbsolute(start, 0.001) : "",
            duration = double.IsFinite(dur) ? dur : (double?)null, rate = double.IsFinite(rate) ? rate : (double?)null,
            timeChannel = t?.Id, hier = IsHier(f), segments = f.Properties.TryGetValue("segments", out var sg) ? sg?.ToString() : null,
            warnings = f.Warnings.Count,
            groups = f.Groups.Select(g => new
            {
                name = g.Name,
                channels = g.Channels.Select(c => new { id = c.Id, name = c.Name, unit = c.Unit, length = c.Length, isText = c.IsText, step = !c.IsText && IsStep(c), time = !c.IsText && f.ChannelCount <= 512 && IsTimeCandidate(c), ready = c.Pyramid != null }).ToList(),
            }).ToList(),
            props = f.Properties.Take(60).Select(kv => new { k = kv.Key, v = FormatProp(kv.Value) }).ToList(),
        };
        _fileDto[f] = dto;
        return dto;
    }

    object ChartDto(Chart chart)
    {
        var m = chart.Model;
        var xc = m.XChannelOf; var sug = chart.Id > 0 ? SuggestedX(chart) : null;
        var fr = new FrameServer();   // formatting only
        return new
        {
            id = chart.Id, title = m.Title, xmin = m.XMin, xmax = m.XMax, xUnit = m.XUnit, xTimeMode = m.XTimeMode, xTimeOrigin = m.XTimeOrigin,
            lanes = m.DigitalLanes, laneCount = m.DigitalLanes ? m.Curves.Count(c => c.Visible && c.Step && !(c.XChannel != null && !c.XMonotonic)) : 0,
            curveCount = m.Curves.Count(c => !(m.DigitalLanes && c.Step && !(c.XChannel != null && !c.XMonotonic))),
            alignMode = m.AlignMode, alignOffsetMs = m.AlignOffsetMs, grid = m.ShowGrid, legend = m.ShowLegend, isXY = m.IsXY,
            cursorMode = m.CursorMode, band = new { on = m.BandEnabled, x0 = m.BandX0, x1 = m.BandX1, label0 = m.BandEnabled ? fr.FormatX(m, m.BandX0, m.XTimeMode != "none" ? 0.001 : 0) : "", label1 = m.BandEnabled ? fr.FormatX(m, m.BandX1, m.XTimeMode != "none" ? 0.001 : 0) : "" },
            linkX = m.LinkX, linkKey = LinkKey(chart), detailOf = chart.DetailOf is int dov && Get(dov) != null ? dov : (int?)null, hasDetail = Charts.Any(c => c.DetailOf == chart.Id), zoomHistory = m.ZoomHistory.Count,
            detailOfName = chart.DetailOf is int dn && Get(dn) is { } och ? (string.IsNullOrEmpty(och.Model.Title) ? "Chart " + (Charts.IndexOf(och) + 1) : och.Model.Title) : null,
            pageId = chart.PageId, windowId = chart.WindowId,
            axes = m.YAxes.Select(a => new { id = a.Id, unit = a.Unit, right = a.Right, auto = a.Auto, min = a.Min, max = a.Max, log = a.Log, curves = m.Curves.Count(c => c.Axis == a) }).ToList(),
            cursorA = new { on = m.CursorA.Enabled, x = m.CursorA.X, label = m.CursorA.Enabled ? fr.FormatX(m, m.CursorA.X) : "" },
            cursorB = new { on = m.CursorB.Enabled, x = m.CursorB.X, label = m.CursorB.Enabled ? fr.FormatX(m, m.CursorB.X) : "" },
            xChannel = xc == null ? null : new { id = xc.Id, name = xc.Name, unit = xc.Unit },
            suggestedX = sug == null ? null : new { id = sug.Id, name = sug.Name, unit = sug.Unit },
            curves = m.Curves.Select((c, i) => new
            {
                idx = i, channelId = c.Channel.Id, label = m.LabelOf(c), name = c.Channel.Name, unit = c.Channel.Unit, color = c.ColorIndex, hex = Hex(c.ColorIndex),
                visible = c.Visible, step = c.Step, lane = m.DigitalLanes && c.Step && !(c.XChannel != null && !c.XMonotonic), right = c.Axis.Right, axisUnit = c.Axis.Unit,
                xChannel = c.XChannel?.Name, xMono = c.XMonotonic, xOffset = c.XOffset, length = c.Channel.Length, file = c.Channel.FileName, ready = c.Channel.Pyramid != null,
                dataType = c.Channel.DataType, width = c.Width, points = c.Points, axisId = c.Axis.Id,
            }).ToList(),
        };
    }

    /// <summary>Properties of a channel (right panel): identity, range, value at cursor A of the active chart, time info, raw properties.</summary>
    public object? ChannelProps(string id)
    {
        var ch = Find(id); if (ch == null) return null;
        var f = State.FileOf(ch);
        var p = ch.Pyramid;
        var chart = Get(ActiveChart);
        var cv = chart?.Model.Curves.FirstOrDefault(c => c.Channel == ch);
        double? cur = null, curB = null; string curLabel = "LAST SAMPLE", curWhen = "";
        (double, double)? band = null;
        var fr = new FrameServer();
        if (chart != null && cv != null && chart.Model.CursorA.Enabled)
        {
            cur = PlotRenderer.ValueAt(cv, chart.Model.CursorA.X); curLabel = "AT CURSOR A"; curWhen = fr.FormatX(chart.Model, chart.Model.CursorA.X);
            if (chart.Model.CursorB.Enabled)
            {
                curB = PlotRenderer.ValueAt(cv, chart.Model.CursorB.X);
                var seg = PlotRenderer.SegmentStats(cv, Math.Min(chart.Model.CursorA.X, chart.Model.CursorB.X), Math.Max(chart.Model.CursorA.X, chart.Model.CursorB.X));
                if (seg != null) band = (seg.Value.min, seg.Value.max);
            }
        }
        if (cur == null && ch.Length > 0 && !ch.IsText) { var one = new double[1]; if (ch.Source.Read(ch.Length - 1, one) == 1) cur = one[0]; }
        bool timeCand = !ch.IsText && IsTimeCandidate(ch);
        double abs = double.NaN; if (timeCand) { var (a, _) = XIndex.Range(ch); abs = a * XIndex.TimeScale(ch.Unit); }
        object? wfStart = null;
        if (ch.Properties.TryGetValue("wf_start_time", out var ws) && ws is Core.Tdms.TdmsTimestamp ts) wfStart = ts.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        double fileStart = f != null ? Compare.FileStart(f) : double.NaN;
        if (wfStart == null && double.IsFinite(fileStart)) wfStart = TimeAxis.DateOf(fileStart).Replace(" UTC", "") + " " + TimeAxis.FormatAbsolute(fileStart, 1);
        if (!double.IsFinite(abs) && double.IsFinite(fileStart)) abs = fileStart;
        var xc = chart?.Model.XChannelOf;
        return new
        {
            id = ch.Id, name = ch.Name, group = ch.Group, file = ch.FileName, unit = ch.Unit, dataType = ch.DataType, length = ch.Length, isText = ch.IsText,
            xKind = ch.XKind.ToString(), xStart = ch.XStart, xIncrement = ch.XIncrement, xUnit = ch.XUnit, rate = ch.XKind == XKind.Implicit && ch.XIncrement > 0 ? 1 / ch.XIncrement : (double?)null,
            render = cv != null ? (cv.Step ? (chart!.Model.DigitalLanes ? "lane" : "step") : "line") : (IsStep(ch) ? "step" : "line"),
            timeCandidate = timeCand, absolute = double.IsFinite(abs) ? abs : (double?)null, absoluteText = double.IsFinite(abs) && XIndex.LooksAbsolute(abs) ? TimeAxis.DateOf(abs) + " " + TimeAxis.FormatAbsolute(abs, 0.001) : null,
            min = p != null && !double.IsNaN(p.GlobalMin) ? p.GlobalMin : (double?)null, max = p != null && !double.IsNaN(p.GlobalMax) ? p.GlobalMax : (double?)null, ready = p != null,
            value = cur, valueB = curB, valueLabel = curLabel, valueWhen = curWhen, band = band == null ? null : new[] { band.Value.Item1, band.Value.Item2 },
            xChannel = cv?.XChannel?.Name ?? xc?.Name, display = chart != null ? chart.Model.XTimeMode switch { "absolute" => "absolute HH:mm:ss", "relative" => "relative mm:ss", _ => "numeric" } : "",
            wfStart, inChart = cv != null ? chart!.Id : (int?)null, curveIdx = cv != null ? chart!.Model.Curves.IndexOf(cv) : (int?)null,
            props = ch.Properties.Where(kv => kv.Key is not ("unit_string" or "description" or "text_values")).Take(60).Select(kv => new { k = kv.Key, v = FormatProp(kv.Value) }).ToList(),
            description = ch.Description,
        };
    }

    public object? FileProps(string name)
    {
        var f = State.Files.FirstOrDefault(x => x.Name == name); if (f == null) return null;
        return FileDto(f);
    }

    static string FormatProp(object? v) => v switch
    {
        double d => Ticks.FormatValue(d, 10),
        float fl => Ticks.FormatValue(fl, 7),
        Core.Tdms.TdmsTimestamp ts => ts.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        null => "",
        _ => v.ToString() ?? "",
    };

    static readonly float[] _smn = new float[14], _smx = new float[14];
    public float[] Sparkline(Channel ch)
    {
        var p = ch.Pyramid; var r = new float[14];
        if (p == null) { Array.Fill(r, float.NaN); return r; }
        lock (_smn)
        {
            p.Envelope(ch.Source, 0, ch.Length, 14, _smn, _smx, out _);
            for (int i = 0; i < 14; i++) r[i] = float.IsNaN(_smn[i]) ? float.NaN : (_smn[i] + _smx[i]) / 2;
        }
        return r;
    }
}
