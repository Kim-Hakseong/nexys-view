using System.IO;
using System.Text.Json;
using System.Windows;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>Auto-saved layout: window, theme, plot theme, open files, charts (curves by channel id, x mapping, alignment, time mode,
/// lanes, cursors), navigator tree mode per file. Automation runs never write it.</summary>
public static class LayoutStore
{
    public sealed class CurveDto { public string Channel { get; set; } = ""; public string? X { get; set; } public bool XMono { get; set; } public double XScale { get; set; } = 1; public double XOffset { get; set; } public bool Step { get; set; } public double Width { get; set; } = 1; public bool Points { get; set; } public string AxisId { get; set; } = ""; public int Color { get; set; } public bool Visible { get; set; } = true; public bool RightAxis { get; set; } }
    public sealed class PageDto { public int Id { get; set; } public string Name { get; set; } = ""; public int Columns { get; set; } = 1; }
    public sealed class WindowDto { public int Id { get; set; } public double X { get; set; } public double Y { get; set; } public double W { get; set; } public double H { get; set; } }
    public sealed class AxisDto { public string Id { get; set; } = ""; public string Unit { get; set; } = ""; public bool Right { get; set; } public bool Auto { get; set; } = true; public double Min { get; set; } public double Max { get; set; } public bool Log { get; set; } }
    public sealed class PlotDto
    {
        // 0.9.3
        public string CursorMode { get; set; } = "free"; public bool Band { get; set; } public double BandX0 { get; set; } public double BandX1 { get; set; } public bool LinkX { get; set; } = true; public int DetailOfIndex { get; set; } = -1;
        public List<AxisDto> Axes { get; set; } = new();
        public string Title { get; set; } = ""; public double XMin { get; set; } public double XMax { get; set; } public bool Grid { get; set; } = true; public bool Legend { get; set; } = true;
        public List<CurveDto> Curves { get; set; } = new(); public bool CursorA { get; set; } public double CursorAX { get; set; } public bool CursorB { get; set; } public double CursorBX { get; set; }
        public string XTimeMode { get; set; } = "none"; public double XTimeOrigin { get; set; } public bool Lanes { get; set; } public string AlignMode { get; set; } = "none"; public double AlignOffsetMs { get; set; }
    }
    public sealed class Dto
    {
        public string Version { get; set; } = "";
        public string Theme { get; set; } = "light";
        public string Plot { get; set; } = "dark";
        public double W { get; set; } = 1440;
        public double H { get; set; } = 900;
        public double X { get; set; } = double.NaN;
        public double Y { get; set; } = double.NaN;
        public bool Maximized { get; set; }
        public List<string> Files { get; set; } = new();
        public List<PlotDto> Plots { get; set; } = new();
        public bool Developer { get; set; }
        public bool TourSeen { get; set; }
        /// <summary>0.9.3: show the ANALYSIS / REPORT tabs again (hidden by default).</summary>
        public bool ShowLegacyTabs { get; set; }
        public string DropMode { get; set; } = "unit";
        // 0.9.4
        public List<PageDto> Pages { get; set; } = new();
        public int ActivePage { get; set; } = 1;
        public bool AutoPage { get; set; } = true;
        public int AutoPageN { get; set; } = 4;
        /// <summary>Rectangles of chart windows by window id (reused when a window with that id is opened again; off-screen rects fall back to the main screen).</summary>
        public List<WindowDto> Windows { get; set; } = new();
        public Dictionary<string, bool> TreeHierarchy { get; set; } = new();
        public string Mode { get; set; } = "VIEW";
        /// <summary>0.10.0: features.icdExport — ICD import / bus-simulator project export module (off by default).</summary>
        public bool IcdExport { get; set; }
        public string IcdTemplateDir { get; set; } = "";
        /// <summary>1.0: saved bus-filter combinations and the list's time display (rel|abs|delta).</summary>
        public List<Mil1553Service.FilterPreset> BusFilters { get; set; } = new();
        public string BusTime { get; set; } = "rel";
    }

    public static readonly bool RestoreSession = false;   // not const: keeps the restore code compiled without CS0162
    public static string PathFor() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexysView", "layout.json");
    static bool _loading;

    public static void Save(MainWindow w)
    {
        if (_loading || SelfTest.Enabled || Program.Fresh) return;   // automation / --fresh must not clobber the user's layout
        try
        {
            var e = w.Engine;
            var d = new Dto
            {
                Version = AppInfo.Version, Theme = e.Theme, Plot = e.Plot, W = w.Width, H = w.Height, X = w.Left, Y = w.Top, Maximized = w.WindowState == WindowState.Maximized,
                Files = e.State.Files.Where(f => File.Exists(f.Path)).Select(f => f.Path).ToList(), Developer = e.Developer, TourSeen = e.TourSeen, ShowLegacyTabs = e.ShowLegacyTabs, DropMode = e.DropMode, Mode = e.Mode, IcdExport = e.IcdExport, IcdTemplateDir = e.IcdTemplateDir, BusFilters = e.Mil1553.Presets.ToList(), BusTime = e.BusTime,
                Pages = e.Pages.Select(p => new PageDto { Id = p.Id, Name = p.Name, Columns = p.Columns }).ToList(), ActivePage = e.ActivePage, AutoPage = e.AutoPage, AutoPageN = e.AutoPageN,
                Windows = w.WindowRects(),
                TreeHierarchy = new Dictionary<string, bool>(e.Hier),
            };
            foreach (var p in e.Charts)
            {
                var m = p.Model;
                var pd = new PlotDto
                {
                    Title = m.Title, XMin = m.XMin, XMax = m.XMax, Grid = m.ShowGrid, Legend = m.ShowLegend, CursorA = m.CursorA.Enabled, CursorAX = m.CursorA.X, CursorB = m.CursorB.Enabled, CursorBX = m.CursorB.X,
                    XTimeMode = m.XTimeMode, XTimeOrigin = m.XTimeOrigin, Lanes = m.DigitalLanes, AlignMode = m.AlignMode, AlignOffsetMs = m.AlignOffsetMs,
                    CursorMode = m.CursorMode, Band = m.BandEnabled, BandX0 = m.BandX0, BandX1 = m.BandX1, LinkX = m.LinkX, DetailOfIndex = p.DetailOf is int dov ? e.Charts.FindIndex(c => c.Id == dov) : -1,
                    Axes = m.YAxes.Select(a => new AxisDto { Id = a.Id, Unit = a.Unit, Right = a.Right, Auto = a.Auto, Min = a.Min, Max = a.Max, Log = a.Log }).ToList(),
                };
                foreach (var c in m.Curves) pd.Curves.Add(new CurveDto { Channel = c.Channel.Id, X = c.XChannel?.Id, XMono = c.XMonotonic, XScale = c.XScale, XOffset = c.XOffset, Step = c.Step, Width = c.Width, Points = c.Points, AxisId = c.Axis.Id, Color = c.ColorIndex, Visible = c.Visible, RightAxis = c.Axis.Right });
                d.Plots.Add(pd);
            }
            var path = PathFor();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Load(MainWindow w)
    {
        var path = PathFor();
        if (!File.Exists(path) || Program.Fresh) return;   // first start (or --fresh): built-in defaults = light chrome, dark plot
        _loading = true;
        try
        {
            var d = JsonSerializer.Deserialize<Dto>(File.ReadAllText(path));
            if (d == null) return;
            var e = w.Engine;
            e.Theme = d.Theme is "light" or "dark" ? d.Theme : Engine.DefaultTheme; e.Plot = d.Plot is "light" or "dark" ? d.Plot : Engine.DefaultPlot;
            if (SelfTest.Enabled) return;   // automation runs start from an empty workspace with the default window size
            if (!double.IsNaN(d.X) && d.W > 300 && ChartWindow.OnAScreen(new Rect(d.X, d.Y, d.W, d.H))) { w.Left = d.X; w.Top = d.Y; w.Width = d.W; w.Height = d.H; }
            if (d.Maximized) w.WindowState = WindowState.Maximized;
            if (d.Pages.Count > 0) { e.Pages.Clear(); foreach (var p in d.Pages) e.Pages.Add(new Engine.Page { Id = p.Id, Name = p.Name, Columns = p.Columns == 2 ? 2 : 1 }); e.ActivePage = e.Pages.Any(p => p.Id == d.ActivePage) ? d.ActivePage : e.Pages[0].Id; e.ResetPageCounter(); }
            e.AutoPage = d.AutoPage; e.AutoPageN = Math.Clamp(d.AutoPageN, 1, 32);
            w.SavedWindowRects = d.Windows.ToDictionary(x => x.Id, x => new Rect(x.X, x.Y, x.W, x.H));
            foreach (var kv in d.TreeHierarchy) e.Hier[kv.Key] = kv.Value;
            // 0.9.2 (beta feedback): the previous session's files and charts are NOT reopened — a start shows the built-in sample
            // (SampleData) or the files given on the command line. Files/Plots are still saved for a future "restore session" option.
            if (RestoreSession) foreach (var f in d.Files) if (File.Exists(f)) e.OpenFile(f);
            if (RestoreSession) foreach (var pd in d.Plots)
            {
                var chart = e.AddChart(record: false);
                var m = chart.Model;
                foreach (var c in pd.Curves)
                {
                    var ch = e.State.FindChannel(c.Channel); if (ch == null) continue;
                    var x = c.X != null ? e.State.FindChannel(c.X) : null;
                    var cv = e.AddCurve(chart.Id, ch.Id, c.XMono || x == null ? "plot" : "xy", c.XMono ? null : x?.Id, record: false);
                    if (cv == null) continue;
                    if (c.XMono && x != null) { cv.XChannel = x; cv.XMonotonic = true; cv.XScale = c.XScale; }
                    cv.XOffset = c.XOffset; cv.Step = c.Step; cv.ColorIndex = c.Color; cv.Visible = c.Visible;
                    if (c.RightAxis) { cv.Axis = m.YAxes.FirstOrDefault(a => a.Right) ?? new YAxis("yr") { Unit = ch.Unit, Right = true }; if (!m.YAxes.Contains(cv.Axis)) m.YAxes.Add(cv.Axis); }
                }
                m.Title = pd.Title; m.ShowGrid = pd.Grid; m.ShowLegend = pd.Legend;
                m.XTimeMode = pd.XTimeMode; m.XTimeOrigin = pd.XTimeOrigin; m.DigitalLanes = pd.Lanes; m.AlignMode = pd.AlignMode; m.AlignOffsetMs = pd.AlignOffsetMs;
                if (m.Curves.Any(c => c.XMonotonic)) m.XUnit = Core.Data.XIndex.TimeScale(m.XChannelOf?.Unit ?? "") > 0 ? "s" : m.XChannelOf?.Unit ?? m.XUnit;
                if (pd.XMax > pd.XMin) { m.XMin = pd.XMin; m.XMax = pd.XMax; }
                m.CursorA.Enabled = pd.CursorA; m.CursorA.X = pd.CursorAX; m.CursorB.Enabled = pd.CursorB; m.CursorB.X = pd.CursorBX;
            }
            e.Developer = d.Developer; e.TourSeen = d.TourSeen; e.ShowLegacyTabs = d.ShowLegacyTabs; e.DropMode = d.DropMode == "each" ? "each" : "unit";
            if (!e.ShowLegacyTabs && d.Mode is "ANALYSIS" or "REPORT") d.Mode = "VIEW";
            e.IcdExport = d.IcdExport; e.IcdTemplateDir = d.IcdTemplateDir ?? ""; e.Mil1553.Presets.Clear(); e.Mil1553.Presets.AddRange(d.BusFilters ?? new()); e.BusTime = d.BusTime is "abs" or "delta" ? d.BusTime : "rel";
            e.Mode = d.Mode is "NAVIGATOR" or "VIEW" or "ANALYSIS" or "REPORT" or "MIL1553" ? d.Mode : "VIEW";
            e.State.Undo.Clear();
        }
        catch { }
        finally { _loading = false; }
    }
}
