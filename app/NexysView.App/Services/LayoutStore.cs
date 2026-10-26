using System.IO;
using System.Text.Json;
using System.Windows;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>Auto-saved layout: window, theme, plot theme, open files, charts (curves by channel id, x mapping, alignment, time mode,
/// lanes, cursors), navigator tree mode per file. Automation runs never write it.</summary>
public static class LayoutStore
{
    public sealed class CurveDto { public string Channel { get; set; } = ""; public string? X { get; set; } public bool XMono { get; set; } public double XScale { get; set; } = 1; public double XOffset { get; set; } public bool Step { get; set; } public int Color { get; set; } public bool Visible { get; set; } = true; public bool RightAxis { get; set; } }
    public sealed class PlotDto
    {
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
        public Dictionary<string, bool> TreeHierarchy { get; set; } = new();
        public string Mode { get; set; } = "VIEW";
    }

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
                Files = e.State.Files.Where(f => File.Exists(f.Path)).Select(f => f.Path).ToList(), Developer = e.Developer, Mode = e.Mode,
                TreeHierarchy = new Dictionary<string, bool>(e.Hier),
            };
            foreach (var p in e.Charts)
            {
                var m = p.Model;
                var pd = new PlotDto
                {
                    Title = m.Title, XMin = m.XMin, XMax = m.XMax, Grid = m.ShowGrid, Legend = m.ShowLegend, CursorA = m.CursorA.Enabled, CursorAX = m.CursorA.X, CursorB = m.CursorB.Enabled, CursorBX = m.CursorB.X,
                    XTimeMode = m.XTimeMode, XTimeOrigin = m.XTimeOrigin, Lanes = m.DigitalLanes, AlignMode = m.AlignMode, AlignOffsetMs = m.AlignOffsetMs,
                };
                foreach (var c in m.Curves) pd.Curves.Add(new CurveDto { Channel = c.Channel.Id, X = c.XChannel?.Id, XMono = c.XMonotonic, XScale = c.XScale, XOffset = c.XOffset, Step = c.Step, Color = c.ColorIndex, Visible = c.Visible, RightAxis = c.Axis.Right });
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
            if (!double.IsNaN(d.X) && d.W > 300) { w.Left = d.X; w.Top = d.Y; w.Width = d.W; w.Height = d.H; }
            if (d.Maximized) w.WindowState = WindowState.Maximized;
            if (SelfTest.Enabled) return;   // automation runs start from an empty workspace
            foreach (var kv in d.TreeHierarchy) e.Hier[kv.Key] = kv.Value;
            foreach (var f in d.Files) if (File.Exists(f)) e.OpenFile(f);
            foreach (var pd in d.Plots)
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
            e.Developer = d.Developer; e.Mode = d.Mode is "NAVIGATOR" or "VIEW" or "ANALYSIS" or "REPORT" ? d.Mode : "VIEW";
            e.State.Undo.Clear();
        }
        catch { }
        finally { _loading = false; }
    }
}
