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
    public string StatusText { get; private set; } = "";
    public string Led { get; private set; } = "idle";
    public string AlignMode { get; set; } = "start";
    public double AlignOffsetMs { get; set; } = 500;
    public string Mode { get; set; } = "VIEW";
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
    public Chart AddChart(bool record = true, int at = -1)
    {
        var ch = new Chart(_nextId++);
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
            double ox0 = fromX0.Value, ox1 = fromX1.Value;
            State.Undo.Push("View", () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); }, () => { m.XMin = x0; m.XMax = x1; TouchChart(chartId); });
        }
        ChartChanged?.Invoke(chartId);
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
            double ox0 = fromX0.Value, ox1 = fromX1.Value;
            State.Undo.Push("View", () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); }, () => { m.XMin = x0; m.XMax = x1; TouchChart(chartId); });
        }
    }

    public void ZoomFull(int chartId)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        double ox0 = m.XMin, ox1 = m.XMax;
        var (a, b) = m.FullX();
        State.Undo.Do("Zoom full", () => { m.XMin = a; m.XMax = b; foreach (var ax in m.YAxes) ax.Auto = true; TouchChart(chartId); }, () => { m.XMin = ox0; m.XMax = ox1; TouchChart(chartId); });
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

    public void SetCurve(int chartId, int idx, bool? visible = null, bool? step = null, int? color = null, bool? right = null)
    {
        var chart = Get(chartId); if (chart == null) return; var m = chart.Model;
        if (idx < 0 || idx >= m.Curves.Count) return;
        var c = m.Curves[idx];
        bool v0 = c.Visible, s0 = c.Step, r0 = c.Axis.Right; int c0 = c.ColorIndex; var ax0 = c.Axis;
        void Apply(bool v, bool s, int col, bool r, YAxis? ax)
        {
            c.Visible = v; c.Step = s; c.ColorIndex = col;
            if (ax != null) c.Axis = ax; else if (r != c.Axis.Right) c.Axis = m.YAxes.FirstOrDefault(a => a.Right == r) ?? NewAxis(m, c.Channel.Unit, r);
            m.YAxes.RemoveAll(a => !m.Curves.Any(cc => cc.Axis == a));
            TouchChart(chartId);
        }
        State.Undo.Do("Curve", () => Apply(visible ?? v0, step ?? s0, color ?? c0, right ?? r0, null), () => Apply(v0, s0, c0, r0, m.YAxes.Contains(ax0) ? ax0 : null));
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
    public sealed class Table { public int Id; public List<Channel> Channels = new(); }
    public List<Table> Tables { get; } = new();
    int _nextTable = 1;
    public Table AddTable() { var t = new Table { Id = _nextTable++ }; Tables.Add(t); Changed?.Invoke(); return t; }
    public void RemoveTable(int id) { Tables.RemoveAll(t => t.Id == id); Changed?.Invoke(); }
    public void TableAdd(int id, string channelId) { var t = Tables.FirstOrDefault(x => x.Id == id); var c = Find(channelId); if (t != null && c != null && !t.Channels.Contains(c)) { t.Channels.Add(c); Changed?.Invoke(); } }
    public object? TableRows(int id, long first, int count)
    {
        var t = Tables.FirstOrDefault(x => x.Id == id); if (t == null) return null;
        count = Math.Clamp(count, 1, 1000); long maxLen = t.Channels.Count == 0 ? 0 : t.Channels.Max(c => c.Length);
        first = Math.Clamp(first, 0, Math.Max(0, maxLen - 1));
        var cols = new List<object>();
        foreach (var c in t.Channels)
        {
            var buf = new double[count]; int got = c.Length > first ? c.Source.Read(first, buf.AsSpan(0, (int)Math.Min(count, c.Length - first))) : 0;
            cols.Add(new { id = c.Id, name = c.Name, unit = c.Unit, values = buf.Take(got).Select(v => double.IsNaN(v) ? (double?)null : v).ToList(), x = Enumerable.Range(0, got).Select(i => c.XAt(first + i)).ToList() });
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
            theme = Theme, plot = Plot, mode = Mode, developer = Developer,
            app = new { version = AppInfo.Version, versionText = AppInfo.VersionText, beta = AppInfo.IsBeta, repo = AppInfo.Repo, issues = AppInfo.Issues },
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
            tables = Tables.Select(t => new { id = t.Id, channels = t.Channels.Select(c => new { id = c.Id, name = c.Name, unit = c.Unit, length = c.Length }).ToList(), rows = t.Channels.Count == 0 ? 0 : t.Channels.Max(c => c.Length) }).ToList(),
            cache = new { ready = State.Files.SelectMany(f => f.AllChannels).Count(c => c.Pyramid != null), total = State.Files.Sum(f => f.ChannelCount), mb = State.Files.SelectMany(f => f.AllChannels).Sum(c => c.Pyramid?.MemoryBytes ?? 0) / 1048576.0 },
        };
    }

    object FileDto(DataFile f)
    {
        if (_fileDto.TryGetValue(f, out var cached)) return cached;
        long size = 0; try { if (File.Exists(f.Path)) size = new FileInfo(f.Path).Length; } catch { }
        var (t, dur, rate) = Compare.TimeInfo(f);
        double start = Compare.FileStart(f);
        string writer = f.Properties.TryGetValue("name", out var nm) ? nm?.ToString() ?? "" : f.Properties.TryGetValue("author", out var au) ? au?.ToString() ?? "" : "";
        bool veri = f.AllChannels.Any(c => c.Name == "System Time") && f.AllChannels.Any(c => c.Name == "Absolute Time");
        var dto = new
        {
            name = f.Name, path = f.Path, dir = Path.GetDirectoryName(f.Path) ?? "", format = f.Format.ToUpperInvariant(), writer = veri ? "VeriStand" : writer,
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
            cursorA = new { on = m.CursorA.Enabled, x = m.CursorA.X, label = m.CursorA.Enabled ? fr.FormatX(m, m.CursorA.X) : "" },
            cursorB = new { on = m.CursorB.Enabled, x = m.CursorB.X, label = m.CursorB.Enabled ? fr.FormatX(m, m.CursorB.X) : "" },
            xChannel = xc == null ? null : new { id = xc.Id, name = xc.Name, unit = xc.Unit },
            suggestedX = sug == null ? null : new { id = sug.Id, name = sug.Name, unit = sug.Unit },
            curves = m.Curves.Select((c, i) => new
            {
                idx = i, channelId = c.Channel.Id, label = m.LabelOf(c), name = c.Channel.Name, unit = c.Channel.Unit, color = c.ColorIndex, hex = Hex(c.ColorIndex),
                visible = c.Visible, step = c.Step, lane = m.DigitalLanes && c.Step && !(c.XChannel != null && !c.XMonotonic), right = c.Axis.Right, axisUnit = c.Axis.Unit,
                xChannel = c.XChannel?.Name, xMono = c.XMonotonic, xOffset = c.XOffset, length = c.Channel.Length, file = c.Channel.FileName, ready = c.Channel.Pyramid != null,
                dataType = c.Channel.DataType,
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
