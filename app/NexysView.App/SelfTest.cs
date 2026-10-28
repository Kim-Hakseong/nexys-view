using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using NexysView.App.Services;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App;

/// <summary>Command-line automation on the web UI: --shot, --selftest, --v3, --stress, --stress-analysis. Steps run as real DOM
/// events inside the page (window.nx.test.*) and are awaited through the bridge; captures are WebView2 previews.</summary>
public static partial class SelfTest
{
    public static string[] Args = Array.Empty<string>();
    public static bool Enabled => Args.Length > 0 && Args[0] is "--shot" or "--selftest" or "--stress" or "--stress-analysis" or "--v3" or "--v093" or "--v094" or "--v0100" or "--v100rc";
    static readonly List<string> Log = new();
    public static string OutDir = "";
    static readonly List<string> Gate = new();

    public static void L(string s) { lock (Log) Log.Add($"{DateTime.Now:HH:mm:ss.fff} {s}"); Console.WriteLine(s); }
    static void G(string k, double v, string unit, string? gate = null, string? wpf = null) { var s = $"{k}\t{v:F3} {unit}" + (gate != null ? $"\tgate {gate}" : "") + (wpf != null ? $"\twpf {wpf}" : ""); Gate.Add(s); L(s); }
    static void GB(string k, bool ok, string detail) { var s = $"{k}\t{(ok ? "PASS" : "FAIL")}\t{detail}"; Gate.Add(s); L(s); }
    static string Arg(string name, string dflt = "") { int i = Array.IndexOf(Args, name); return i >= 0 && i + 1 < Args.Length ? Args[i + 1] : dflt; }
    static IEnumerable<string> ArgAll(string name) { for (int i = 0; i < Args.Length - 1; i++) if (Args[i] == name) yield return Args[i + 1]; }

    public static async Task RunAsync(MainWindow w)
    {
        try
        {
            OutDir = Arg("--out", Path.Combine(Directory.GetCurrentDirectory(), "selftest"));
            Directory.CreateDirectory(OutDir);
            await Idle(w, 300);
            // page = 1440×900 CSS px (window chrome excluded) so captures match the mockup viewport
            w.Width += 1440 - w.Web.ActualWidth; w.Height += 900 - w.Web.ActualHeight;
            await Idle(w, 300);
            L($"page size {w.Web.ActualWidth}x{w.Web.ActualHeight} dpr {VisualTreeHelper.GetDpi(w).DpiScaleX}");
            if (Args[0] == "--shot") await ShotAsync(w);
            else if (Args[0] == "--stress") await StressAsync(w);
            else if (Args[0] == "--stress-analysis") await StressAnalysisAsync(w);
            else if (Args[0] == "--v3") await V3Async(w);
            else if (Args[0] == "--v093") await V093Async(w);
            else if (Args[0] == "--v094") await V094Async(w);
            else if (Args[0] == "--v0100") await V0100Async(w);
            else if (Args[0] == "--v100rc") await V100Async(w);
            else await FullAsync(w);
        }
        catch (Exception ex) { L("FATAL " + ex); }
        finally
        {
            File.WriteAllLines(Path.Combine(OutDir, "selftest.log"), Log);
            if (Gate.Count > 0) File.WriteAllLines(Path.Combine(OutDir, "gates.txt"), Gate);
            await Task.Delay(200);
            Application.Current.Shutdown(0);
        }
    }

    static async Task Idle(MainWindow w, int ms = 50) { await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle); await Task.Delay(ms); }

    /// <summary>Run a page-side test step (async JS body with `return`) and get its JSON result.</summary>
    static async Task<JsonElement> JS(MainWindow w, string body, int timeoutMs = 60000)
    {
        var r = await w.Bridge!.RunTest(body, timeoutMs);
        return r;
    }

    static async Task Shot(MainWindow w, string name)
    {
        await JS(w, "await nx.test.settle(); return true;");
        await Idle(w, 150);
        await w.CaptureAsync(Path.Combine(OutDir, name + ".png"));
        L($"shot {name}.png");
    }

    static string TdmsRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
        return d?.FullName ?? Directory.GetCurrentDirectory();
    }

    static string J(string s) => JsonSerializer.Serialize(s);

    static async Task ShotAsync(MainWindow w)
    {
        var e = w.Engine;
        e.Theme = Arg("--theme", "light"); e.Plot = Arg("--plot", "dark");
        foreach (var f in ArgAll("--open")) e.OpenFile(f);
        if (Arg("--scene") is "timelog" or "timelog") { await TimeLogScene(w); await Shot(w, Arg("--name", "shot")); if (Arg("--probe") == "1") await Probe(w, Arg("--name", "shot")); return; }
        int n = int.Parse(Arg("--curves", "3"));
        var chans = e.State.Files.SelectMany(f => f.AllChannels).Where(c => !c.IsText).Take(n).ToList();
        if (chans.Count > 0)
        {
            var chart = e.AddChart();
            foreach (var c in chans) e.AddCurve(chart.Id, c.Id, record: false);
            await Task.WhenAll(chans.Select(c => e.State.EnsurePyramid(c)));
            chart.Model.ZoomFull();
            double zf = double.Parse(Arg("--zoom", "1"), System.Globalization.CultureInfo.InvariantCulture);
            if (zf < 1) { var (a, b) = chart.Model.FullX(); double mid = (a + b) / 2, half = (b - a) * zf / 2; chart.Model.XMin = mid - half; chart.Model.XMax = mid + half; }
            if (Arg("--cursors", "0") == "1") { var (a, b) = chart.Model.FullX(); chart.Model.CursorA.Enabled = true; chart.Model.CursorA.X = a + (b - a) * 0.3; chart.Model.CursorB.Enabled = true; chart.Model.CursorB.X = a + (b - a) * 0.6; }
            e.Selected = chans[0].Id;
            e.TouchChart(chart.Id);
        }
        if (Arg("--select") != "") { var sc = e.State.Files.SelectMany(f => f.AllChannels).FirstOrDefault(c => c.Name == Arg("--select")); if (sc != null) e.Selected = sc.Id; }
        if (Arg("--usex", "0") == "1" && e.Charts.Count > 0 && e.SuggestedX(e.Charts[0]) is { } sx) { e.SetXChannel(e.Charts[0].Id, sx.Id); await JS(w, "await nx.test.settle(); return true;"); }
        if (Arg("--detail", "0") == "1" && e.Charts.Count > 0)
        {
            var ov = e.Charts[0]; var (fa, fb) = ov.Model.FullX(); double fw = fb - fa;
            e.SetCursorMode(ov.Id, "band"); e.SetBand(ov.Id, true, fa + fw * double.Parse(Arg("--band0", "0.42"), System.Globalization.CultureInfo.InvariantCulture), fa + fw * double.Parse(Arg("--band1", "0.58"), System.Globalization.CultureInfo.InvariantCulture), true);
            e.LinkDetail(ov.Id, e.Selected); e.ActiveChart = ov.Id; await Idle(w, 300); await JS(w, "await nx.test.settle(); return true;");
        }
        if (Arg("--table", "0") == "1" && e.Charts.Count > 0) { var t = e.AddTable(); foreach (var c in e.Charts[0].Model.Curves.Take(3)) e.TableAdd(t.Id, c.Channel.Id); var fx = e.Charts[0].Model.FullX(); e.SetCursor(e.Charts[0].Id, "a", fx.min + (fx.max - fx.min) * 0.3); }
        if (Arg("--analysis") != "" && chans.Count > 0)
        {
            var inCh = Arg("--select") != "" ? e.State.Files.SelectMany(f => f.AllChannels).FirstOrDefault(c => c.Name == Arg("--select")) ?? chans[0] : chans[0];
            e.Analysis.SetInputs(new[] { inCh.Id }); foreach (var kv in ArgAll("--param")) { var p = kv.Split('='); if (p.Length == 2) e.Analysis.SetParam(p[0], p[1]); }
            await e.Analysis.RunAsync(Arg("--analysis"));
        }
        if (Arg("--report", "0") == "1" && e.Charts.Count > 0)
        {
            int cid = e.Charts[0].Id;
            e.Report.Add("Title", 0, Arg("--report-title", "Sample report"));
            e.Report.Add("Text", 0, Arg("--report-text", "Sine 1 Hz / Sine 5 Hz / Ramp from the built-in sample, cursors A-B at 30 % and 70 % of the record."));
            e.Report.Add("Chart", cid);
            e.Report.Add("Table", cid);
            if (Arg("--report-out") != "") { e.Report.ExportTo(Arg("--report-out"), false); L("report png " + Arg("--report-out")); }
        }
        e.Mode = Arg("--mode", "VIEW"); e.Touch();
        await Idle(w, 400);
        if (Arg("--tour") != "") L("tour " + await JS(w, $"return await nx.test.tour({Arg("--tour")});"));
        if (Arg("--help") != "") L("help " + await JS(w, $"return await nx.test.help('{Arg("--help")}');"));
        if (Arg("--tour") != "" || Arg("--help") != "") await Idle(w, 500);
        await Shot(w, Arg("--name", "shot"));
        L("buffers " + await w.Bridge!.Eval("JSON.stringify(Array.from(nx.bridge.buffers.keys()))"));
        L("frames " + await w.Bridge!.Eval("JSON.stringify(nx.test.frameStats(nx.state.activeChart))"));
    }

    /// <summary>The scene of design/mockup/view.html: Log 05_28, chart "Target Position · DEG · Sys_Man.ACT_Interval_Value · Sys_Man.IGV"
    /// + lanes command/Ready/Fault on System Time (relative), cursors A = 8.4 s, B = 19.7 s, Target Position selected.</summary>
    static async Task TimeLogScene(MainWindow w)
    {
        var e = w.Engine; string uf = Path.Combine(TdmsRoot(), "work", "userfiles");
        var logs = Directory.GetFiles(uf, "*.tdms").OrderBy(f => f).ToList();
        var df = e.OpenFile(logs[1])!;
        string mode = Arg("--mode", "VIEW");
        foreach (var f in logs.Where(f => f != logs[1])) e.OpenFile(f);
        e.SelectedFile = df.Name;
        Channel C(string n) => df.AllChannels.First(c => c.Name == n);
        var chart = e.AddChart(record: false);
        foreach (var n in new[] { "Target Position", "DEG", "Sys_Man.ACT_Interval_Value", "Sys_Man.IGV", "command", "Ready", "Fault" }) e.AddCurve(chart.Id, C(n).Id, record: false);
        await Task.WhenAll(chart.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel)));
        await e.State.EnsurePyramid(C("System Time"));
        chart.Model.SetXChannel(C("System Time"));
        foreach (var cv in chart.Model.Curves) cv.Step = cv.Channel.Name is "command" or "Ready" or "Fault";
        chart.Model.DigitalLanes = true;
        chart.Model.Curves.First(c => c.Channel.Name == "Sys_Man.ACT_Interval_Value").Axis = chart.Model.YAxes.FirstOrDefault(a => a.Right) ?? new YAxis("yr") { Unit = "", Right = true };
        if (!chart.Model.YAxes.Contains(chart.Model.Curves.First(c => c.Channel.Name == "Sys_Man.ACT_Interval_Value").Axis)) chart.Model.YAxes.Add(chart.Model.Curves.First(c => c.Channel.Name == "Sys_Man.ACT_Interval_Value").Axis);
        chart.Model.Curves.First(c => c.Channel.Name == "Sys_Man.IGV").Axis = chart.Model.Curves.First(c => c.Channel.Name == "Sys_Man.ACT_Interval_Value").Axis;
        chart.Model.ZoomFull();
        double o = chart.Model.XTimeOrigin;
        chart.Model.CursorA.Enabled = true; chart.Model.CursorA.X = o + 8.4; chart.Model.CursorB.Enabled = true; chart.Model.CursorB.X = o + 19.7;
        e.Selected = C("Target Position").Id; e.ActiveChart = chart.Id;
        if (mode == "ANALYSIS")
        {
            e.Analysis.SetInputs(new[] { C("Sys_Man.ACT_Interval_Value").Id }); e.Analysis.SetParam("iir_fc", "2.0"); e.Analysis.SetParam("iir_q", "0.7071");
            await e.Analysis.RunAsync("IIR low-pass");
            e.Analysis.SetInputs(new[] { C("Sys_Man.ACT_Interval_Value").Id });
        }
        e.Mode = mode; e.Touch(); e.TouchChart(chart.Id);
        await Idle(w, 300);
        await JS(w, "await nx.test.settle(); return true;");
    }

    /// <summary>Structural probe of the page (public/probe.js) → &lt;name&gt;_probe.json, compared with the mockup's by tools/pixel_diff.py.</summary>
    static async Task Probe(MainWindow w, string name)
    {
        string js = await w.Bridge!.Eval("JSON.stringify(window.nxProbe())");
        string json = JsonSerializer.Deserialize<string>(js) ?? "{}";
        File.WriteAllText(Path.Combine(OutDir, name + "_probe.json"), json);
        L($"probe {name}_probe.json {json.Length} chars");
    }

    static partial void FullImpl(MainWindow w, ref Task task);
    static Task FullAsync(MainWindow w) { Task t = Task.CompletedTask; FullImpl(w, ref t); return t; }
    static partial void V3Impl(MainWindow w, ref Task task);
    static Task V3Async(MainWindow w) { Task t = Task.CompletedTask; V3Impl(w, ref t); return t; }
    static partial void V094Impl(MainWindow w, ref Task task);
    static Task V0100Async(MainWindow w) { Task t = Task.CompletedTask; V0100Impl(w, ref t); return t; }
    static partial void V0100Impl(MainWindow w, ref Task task);
    static Task V100Async(MainWindow w) { Task t = Task.CompletedTask; V100Impl(w, ref t); return t; }
    static partial void V100Impl(MainWindow w, ref Task task);
    static Task V094Async(MainWindow w) { Task t = Task.CompletedTask; V094Impl(w, ref t); return t; }
    static partial void V093Impl(MainWindow w, ref Task task);
    static Task V093Async(MainWindow w) { Task t = Task.CompletedTask; V093Impl(w, ref t); return t; }
    static partial void StressImpl(MainWindow w, ref Task task);
    static Task StressAsync(MainWindow w) { Task t = Task.CompletedTask; StressImpl(w, ref t); return t; }
    static partial void StressAnalysisImpl(MainWindow w, ref Task task);
    static Task StressAnalysisAsync(MainWindow w) { Task t = Task.CompletedTask; StressAnalysisImpl(w, ref t); return t; }
}
