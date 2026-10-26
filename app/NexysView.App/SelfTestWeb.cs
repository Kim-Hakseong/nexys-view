using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.App.Services;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App;

/// <summary>
/// --selftest (P9 gates on the web UI), --v3 (time-axis log feature gates), --stress / --stress-analysis (stability gates).
/// Interaction goes through real DOM events on the page (window.nx.test.*: synthetic DragEvent/PointerEvent/WheelEvent),
/// frame times are measured on the page (request → painted, bridge round-trip included), captures are WebView2 previews.
/// </summary>
public static partial class SelfTest
{
    static readonly Random Rng = new(12345);
    static string Chans(params Channel[] cs) => JsonSerializer.Serialize(cs.Select(c => new { id = c.Id, name = c.Name }));
    static double[] Arr(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetDouble()).ToArray() : Array.Empty<double>();
    static void ReportFrames(string name, double[] frames, string gate = "<=16")
    {
        if (frames.Length == 0) { L(name + ": no frames"); return; }
        var s = frames.OrderBy(x => x).ToArray();
        G(name + "_median_ms", s[s.Length / 2], "ms", gate); G(name + "_p95_ms", s[(int)(s.Length * 0.95)], "ms"); G(name + "_max_ms", s[^1], "ms");
    }
    static async Task<int> ActiveChart(MainWindow w) { await Idle(w, 20); return w.Engine.ActiveChart; }

    // ================================================================== --selftest
    static partial void FullImpl(MainWindow w, ref Task task) => task = Full(w);

    static async Task Full(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); string bench = Path.Combine(root, "work", "bench");
        e.Theme = "light"; e.Plot = "dark"; e.Touch();
        G("memory_ws_start_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB", null, "181");
        G("memory_webview_start_mb", await WebViewMemoryMbAsync(), "MB");
        var sw = Stopwatch.StartNew();
        foreach (var f in new[] { "bench1_10ch_10M.tdms", "bench2_1GB_16ch_8M.tdms", "bench3_10000ch.tdms" })
        {
            sw.Restart(); e.OpenFile(Path.Combine(bench, f)); G("open_" + f, sw.Elapsed.TotalMilliseconds, "ms", f.Contains("1GB") ? "<=100" : f.Contains("10000") ? "<=2000" : null, f.Contains("1GB") ? "15" : f.Contains("10000") ? "1821" : "119");
        }
        await JS(w, "await nx.test.settle(); return true;");
        sw.Restart(); await JS(w, "await nx.test.settle(); return true;"); G("navigator_render_10000ch_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=500");
        await Shot(w, "01_navigator_light_tree");
        await JS(w, "await nx.test.mode('NAVIGATOR'); return true;"); await Shot(w, "02_navigator_mode");
        await JS(w, "await nx.test.mode('VIEW'); return true;");
        // 2) drop Ch00 of bench1 onto the drop zone (real DragEvent) → first chart
        var f1 = e.State.Files[0]; var ch0 = f1.AllChannels.First();
        sw.Restart();
        await JS(w, $"await nx.test.drop({Chans(ch0)}); return true;");
        await e.State.EnsurePyramid(ch0);
        await JS(w, "await nx.test.settle(); return true;");
        G("drop_first_curve_10M_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=500", "1060 (wpf 260)");
        int chart = await ActiveChart(w);
        GB("first_chart_created", chart > 0 && e.Get(chart)?.Model.Curves.Count == 1, $"chart={chart}");
        var ch1 = f1.AllChannels.Skip(1).First();
        string hint = (await JS(w, $"return await nx.test.drop({Chans(ch1)}, {{chart: {chart}, preview: true}});")).ToString();
        await Shot(w, "03_view_dragging_preview");
        GB("drop_preview_hint", hint.Contains(ch1.Name), hint);
        await JS(w, $"const b = nx.test.plot({chart}).el.querySelector('.plot-box'); b.dispatchEvent(new DragEvent('dragleave', {{bubbles:true}})); return true;");
        // 3) nine more channels onto the chart
        sw.Restart();
        foreach (var c in f1.AllChannels.Skip(1).Take(9)) await JS(w, $"await nx.test.drop({Chans(c)}, {{chart: {chart}}}); return true;");
        await Task.WhenAll(f1.AllChannels.Take(10).Select(c => e.State.EnsurePyramid(c)));
        await JS(w, "await nx.test.settle(); return true;");
        G("ten_curves_ready_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=3000", "15490 (wpf 490)");
        e.Get(chart)!.Model.ZoomFull(); e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;");
        await Shot(w, "04_view_light_10curves");
        // 4) frames through the real page path (wheel events, pans, zoom steps) — bridge round-trip included
        ReportFrames("zoom_30_steps", Arr(await JS(w, $"return await nx.test.zoomSteps({chart}, 30);")));
        ReportFrames("pan_30_steps", Arr(await JS(w, $"return await nx.test.pan({chart}, 30);")));
        ReportFrames("wheel_10_events", Arr(await JS(w, $"return await nx.test.wheel({chart}, 10);")));
        G("drag_pan_ms", (await JS(w, $"return await nx.test.dragPan({chart}, 120);")).GetDouble(), "ms", "<=16");
        e.Get(chart)!.Model.ZoomFull(); e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;");
        // 5) cursors via clicks
        await JS(w, $"await nx.test.click({chart}, 0.3); await nx.test.click({chart}, 0.6, true); return true;");
        var m = e.Get(chart)!.Model;
        GB("cursors_set_by_click", m.CursorA.Enabled && m.CursorB.Enabled && m.CursorB.X > m.CursorA.X, $"A={m.CursorA.X:F3} B={m.CursorB.X:F3}");
        await Shot(w, "05_view_cursors");
        var va = PlotRenderer.ValueAt(ch0, m.CursorA.X); L($"cursorA value {va}");
        // 6) ANALYSIS formula channel
        await JS(w, "await nx.test.mode('ANALYSIS'); return true;");
        e.Analysis.SetInputs(new[] { ch0.Id }); e.Analysis.SetParam("formula", "Ch(\"Results/y\") = 2 * Ch(\"Bench/Ch00\") + 1");
        sw.Restart(); bool ok = await e.Analysis.RunAsync("Formula channel"); G("formula_10M_ms", sw.Elapsed.TotalMilliseconds, "ms");
        var y = e.State.Files.FirstOrDefault(f => f.Name == "Analysis")?.AllChannels.FirstOrDefault(c => c.Name == "y");
        if (y != null) { var a = new double[3]; y.Source.Read(0, a); var src = new double[3]; ch0.Source.Read(0, src); GB("formula_check", ok && a[0] == 2 * src[0] + 1, $"{a[0]} == {2 * src[0] + 1}"); }
        else GB("formula_check", false, string.Join(" | ", e.Analysis.Notes));
        e.Analysis.SetInputs(new[] { ch0.Id }); await e.Analysis.RunAsync("Statistics");
        await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "06_analysis");
        await JS(w, "await nx.test.mode('VIEW'); return true;");
        // 7) TDMS save + reread
        string outTdms = Path.Combine(OutDir, "selftest_export.tdms");
        var smallDf = e.OpenFile(Path.Combine(root, "ref", "examples", "data", "Example.tdms"))!;
        var keep = e.State.Files.Where(f => f != smallDf).ToList();
        foreach (var f in keep) e.State.Files.Remove(f);
        sw.Restart(); int n = Export.TdmsExport.SaveAll(e.State, outTdms); G("tdms_save_ms", sw.Elapsed.TotalMilliseconds, "ms");
        e.State.Files.InsertRange(0, keep);
        using (var re = new Core.Tdms.TdmsFile(outTdms))
        {
            long eq = 0, tot = 0;
            foreach (var ch in smallDf.AllChannels.Where(c => !c.IsText))
            {
                var o = re.Find(Core.Tdms.TdmsPath.Make(ch.Group, ch.Name)); if (o == null) { L("export missing " + ch.Name); continue; }
                var a = new double[ch.Length]; ch.Source.Read(0, a); var b = re.ReadDoubles(o);
                tot += a.Length; for (int i = 0; i < Math.Min(a.Length, b.Length); i++) if (BitConverter.DoubleToInt64Bits(a[i]) == BitConverter.DoubleToInt64Bits(b[i]) || (double.IsNaN(a[i]) && double.IsNaN(b[i]))) eq++;
            }
            G("tdms_roundtrip_equal_pct", 100.0 * eq / Math.Max(1, tot), "%", "=100");
        }
        // 8) REPORT
        e.Report.Clear(); e.Report.Add("Title", 0, "Self-test report"); e.Report.Add("Chart", chart); e.Report.Add("Table", chart); e.Report.Add("Text", 0, "Generated by --selftest. 10 curves × 10M samples from bench1.");
        await JS(w, "await nx.test.mode('REPORT'); await nx.test.reportPage(); return true;");
        await Shot(w, "07_report");
        sw.Restart(); e.Report.ExportTo(Path.Combine(OutDir, "report.png"), false); G("report_png_ms", sw.Elapsed.TotalMilliseconds, "ms");
        sw.Restart(); e.Report.ExportTo(Path.Combine(OutDir, "report.pdf"), true); G("report_pdf_ms", sw.Elapsed.TotalMilliseconds, "ms");
        // 9) dark theme shots
        e.Theme = "dark"; e.Touch(); await JS(w, "await nx.test.settle(); return true;");
        await Shot(w, "08_report_dark");
        await JS(w, "await nx.test.mode('VIEW'); return true;"); await Shot(w, "09_view_dark");
        await JS(w, "await nx.test.mode('ANALYSIS'); return true;"); await Shot(w, "10_analysis_dark");
        await JS(w, "await nx.test.mode('NAVIGATOR'); return true;"); await Shot(w, "11_navigator_dark");
        e.Theme = "light"; e.Touch(); await JS(w, "await nx.test.mode('VIEW'); return true;");
        // 10) undo/redo sanity
        int before = m.Curves.Count; e.Undo(); int after = m.Curves.Count; e.Redo();
        L($"undo/redo curves {before} -> {after} -> {m.Curves.Count}");
        G("frames_shared_buffer", w.Bridge!.SharedBuffersWorking ? 1 : 0, "", "=1"); G("frames_inline", w.Bridge.InlineFrames, "");
        e.CloseFile("Analysis");   // the 10M formula result (80 MB) is not part of the memory gate (the WPF test evaluated 1M in memory only)
        G("memory_ws_peak_mb", Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0, "MB");
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); await Task.Delay(500);
        G("memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB", "<=500", "485 (wpf)");
        G("memory_private_mb", Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, "MB");
        G("memory_webview_mb", await WebViewMemoryMbAsync(), "MB");
        foreach (var f in e.State.Files.ToList()) e.CloseFile(f.Name);
        foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); await Task.Delay(500);
        G("memory_ws_after_close_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
        L("SELFTEST DONE");
    }

    static Task<int> WebViewProcessCountAsync() => Task.Run(() => { int n = 0; foreach (var p in Process.GetProcessesByName("msedgewebview2")) { try { if (IsChildOfUs(p)) n++; } catch { } p.Dispose(); } return n; });
    /// <summary>Working set of the WebView2 (msedgewebview2) processes spawned for this app's user-data folder.</summary>
    /// <summary>WMI per process is slow (seconds): always awaited off the UI thread (a synchronous wait stalled the UI 6 s in --stress).</summary>
    static Task<double> WebViewMemoryMbAsync() => Task.Run(WebViewMemoryMbCore);
    static double WebViewMemoryMbCore()
    {
        try
        {
            double mb = 0;
            var started = Process.GetCurrentProcess().StartTime;
            foreach (var p in Process.GetProcessesByName("msedgewebview2")) { try { if (p.StartTime >= started && IsChildOfUs(p)) mb += p.WorkingSet64 / 1048576.0; } catch { } p.Dispose(); }
            return mb;
        }
        catch { return 0; }
    }
    static bool IsChildOfUs(Process p)
    {
        // WebView2 processes carry --webview-exe-name=NexysView.exe on their command line
        try
        {
            using var q = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {p.Id}");
            foreach (System.Management.ManagementObject o in q.Get()) { string c = o["CommandLine"]?.ToString() ?? ""; return c.Contains("webview-exe-name=NexysView.exe") && c.Contains("WebView2-auto"); }
        }
        catch { }
        return false;
    }

    // ================================================================== --v3
    static partial void V3Impl(MainWindow w, ref Task task) => task = V3(w);

    static async Task V3(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); string uf = Path.Combine(root, "work", "userfiles");
        var logs = Directory.GetFiles(uf, "*.tdms").OrderBy(f => f).ToList();
        e.Theme = "light"; e.Plot = "light"; e.Touch();
        var sw = Stopwatch.StartNew();
        var df = e.OpenFile(logs[1])!;
        await JS(w, "await nx.test.settle(); return true;");
        bool hier = e.IsHier(df); int sysMan = df.AllChannels.Count(c => c.Name.StartsWith("Sys_Man.", StringComparison.Ordinal));
        GB("tree_hierarchical_default", hier && sysMan >= 3, $"hier={hier} Sys_Man.* channels={sysMan}");
        var act = df.AllChannels.First(c => c.Name == "Sys_Man.ACT_Apply");
        await JS(w, $"await nx.test.selectChannel({J(act.Id)}); return true;");
        GB("tree_row_click_selects", e.Selected == act.Id, $"selected={e.Selected}");
        await Shot(w, "v3_01_navigator_tree_hier");
        var matches = Arr2(await JS(w, "return await nx.test.search('ACT');"));
        GB("search_ACT_matches_ge5", matches.Length >= 5, $"matches={matches.Length}");
        await Shot(w, "v3_02_navigator_search");
        await JS(w, "await nx.test.clickSel('.nav-btns [data-b=drop]'); return true;");
        int chartAll = await ActiveChart(w);
        GB("drop_all_curves", chartAll > 0 && e.Get(chartAll)?.Model.Curves.Count == matches.Length, $"curves={e.Get(chartAll)?.Model.Curves.Count}");
        await Task.WhenAll(e.Get(chartAll)!.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(chartAll); await JS(w, "await nx.test.settle(); return true;");
        await Shot(w, "v3_03_view_dropall");
        await JS(w, "await nx.test.search(''); return true;"); e.RemoveChart(chartAll, false); await JS(w, "await nx.test.settle(); return true;");
        // ---- B1 x-axis drop of System Time
        var sysTime = df.AllChannels.First(c => c.Name == "System Time"); var absTime = df.AllChannels.First(c => c.Name == "Absolute Time");
        var deg = df.AllChannels.First(c => c.Name == "DEG"); var fault = df.AllChannels.First(c => c.Name == "Fault"); var cmd = df.AllChannels.First(c => c.Name == "command");
        await JS(w, $"await nx.test.drop({Chans(deg, fault)}); return true;");
        int chart = await ActiveChart(w); var ch = e.Get(chart)!; var m = ch.Model;
        await Task.WhenAll(new[] { deg, fault, sysTime, absTime }.Select(c => e.State.EnsurePyramid(c))); e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;");
        GB("x_suggested_system_time", e.SuggestedX(ch) == sysTime, $"suggested={e.SuggestedX(ch)?.Name}");
        string hint = (await JS(w, $"return await nx.test.drop({Chans(sysTime)}, {{chart: {chart}, zone: 'x', preview: true}});")).ToString();
        GB("x_drop_zone_hint", hint.Contains("as X axis"), hint);
        await Shot(w, "v3_04_view_xdrop_preview");
        await JS(w, $"await nx.test.drop({Chans(sysTime)}, {{chart: {chart}, zone: 'x'}}); return true;");
        GB("x_mapped_relative", m.XChannelOf == sysTime && m.XTimeMode == "relative" && m.Curves.All(c => c.XMonotonic), $"x={m.XChannelOf?.Name} mode={m.XTimeMode}");
        var (xa, xb) = m.FullX(); var (t0, t1) = XIndex.Range(sysTime);
        GB("x_range_is_system_time", Math.Abs(xa - t0) < 1e-9 && Math.Abs(xb - t1) < 1e-9, $"full x {xa:F3}..{xb:F3} vs {t0:F3}..{t1:F3}");
        var fs = new FrameServer(); string tick = fs.FormatX(m, xa + 10, 1);
        GB("x_tick_relative_format", tick.Contains(':'), $"tick(x0+10 s)={tick}");
        var one = new double[1]; sysTime.Source.Read(1234, one); double xs = one[0]; deg.Source.Read(1234, one); double expect = one[0];
        e.SetCursor(chart, "a", xs); e.SetCursor(chart, "b", xs + 5); await JS(w, "await nx.test.settle(); return true;");
        double? got = PlotRenderer.ValueAt(m.Curves[0], xs);
        GB("cursor_value_raw_sample", got != null && Math.Abs(got.Value - expect) < 1e-12, $"x={xs} value={got} expected={expect}");
        string table = (await JS(w, $"return nx.test.plot({chart}).el.querySelector('.cur-table').textContent;")).ToString();
        GB("cursor_table_shows_readouts", table.Contains("DEG") && table.Contains("Fault"), table[..Math.Min(120, table.Length)]);
        await Shot(w, "v3_05_view_xmapped_relative_cursors");
        e.SetXChannel(chart, absTime.Id); await JS(w, "await nx.test.settle(); return true;");
        string abs = fs.FormatX(m, m.XMin + 5, 1);
        GB("x_absolute_clock_format", m.XTimeMode == "absolute" && abs.Count(c => c == ':') == 2, $"mode={m.XTimeMode} tick={abs} date={TimeAxis.DateOf(m.XTimeOrigin)}");
        await Shot(w, "v3_06_view_xmapped_absolute");
        e.Undo(); await JS(w, "await nx.test.settle(); return true;");
        GB("x_undo_restores", m.XChannelOf == sysTime && m.XTimeMode == "relative", $"after undo x={m.XChannelOf?.Name} mode={m.XTimeMode}");
        e.ClearCursors(chart);
        // ---- B2 step + lanes
        var ready = df.AllChannels.First(c => c.Name == "Ready"); var tpos = df.AllChannels.First(c => c.Name == "Target Position");
        await JS(w, $"await nx.test.drop({Chans(cmd, ready, tpos)}, {{chart: {chart}}}); return true;");
        await Task.WhenAll(new[] { cmd, ready, tpos }.Select(c => e.State.EnsurePyramid(c)));
        var cv1 = m.Curves.First(c => c.Channel == cmd); var cv2 = m.Curves.First(c => c.Channel == ready); var cv3 = m.Curves.First(c => c.Channel == tpos);
        GB("step_auto_detect", cv1.Step && cv2.Step && cv3.Step && !m.Curves[0].Step, $"command={cv1.Step} Ready={cv2.Step} TargetPosition={cv3.Step} DEG={m.Curves[0].Step}");
        GB("step_curves_mapped_to_x", cv1.XMonotonic && cv1.XChannel == sysTime, $"new curve x={cv1.XChannel?.Name}");
        m.ZoomFull(); e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "v3_07_view_step_curves");
        await JS(w, "await nx.test.clickSel('[data-a=lanes]'); return true;");
        int lanes = m.DigitalLanes ? m.Curves.Count(c => c.Step) : 0;
        GB("digital_lanes_3", lanes == 3, $"lanes={lanes} on={m.DigitalLanes}");
        await Shot(w, "v3_08_view_digital_lanes");
        // ---- B4 compare
        foreach (var f in logs.Where(f => f != logs[1])) e.OpenFile(f);
        await JS(w, "await nx.test.settle(); return true;");
        var files = e.State.Files.Where(f => logs.Contains(f.Path)).OrderBy(f => f.Path).ToList();
        var common = Compare.CommonChannels(files);
        GB("compare_common_channels", common.Count >= 10 && common.Contains("Fault"), $"common={common.Count}");
        var starts = files.Select(Compare.FileStart).ToArray();
        foreach (var (mode, off) in new[] { ("start", 0.0), ("zero", 0.0), ("manual", 500.0) })
        {
            foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
            int n = e.CreateComparison(files, new[] { "Fault", "DEG" }, mode, off);
            var hc = e.Charts[0];
            await Task.WhenAll(hc.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel)));
            hc.Model.ZoomFull(); e.Charts[1].Model.ZoomFull(); e.TouchChart(-1); await JS(w, "await nx.test.settle(); return true;");
            var x0s = hc.Model.Curves.Select(c => c.XAt(0)).ToArray();
            bool ok = n == 2 && hc.Model.Curves.Count == 3 && hc.Model.Curves.All(c => c.XMonotonic) && mode switch
            {
                "zero" => x0s.All(x => Math.Abs(x) < 1e-6),
                "manual" => Math.Abs(x0s[1] - 0.5) < 1e-6 && Math.Abs(x0s[2] - 1.0) < 1e-6,
                _ => Math.Abs(x0s[0]) < 1e-6 && Math.Abs(x0s[1] - (starts[1] - starts[0])) < 1e-3 && Math.Abs(x0s[2] - (starts[2] - starts[0])) < 1e-3,
            };
            GB("compare_align_" + mode, ok, $"charts={n} curves={hc.Model.Curves.Count} first-x=[{string.Join(", ", x0s.Select(o => o.ToString("F3")))}]");
            await Shot(w, "v3_09_compare_" + mode);
        }
        await JS(w, "await nx.test.mode('NAVIGATOR'); return true;"); await Shot(w, "v3_10_navigator_mode");
        await JS(w, "await nx.test.mode('ANALYSIS'); return true;"); e.Analysis.SetInputs(new[] { deg.Id }); await e.Analysis.RunAsync("Statistics"); await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "v3_11_analysis");
        e.Report.Clear(); e.Report.Add("Title", 0, "Log comparison"); e.Report.Add("Chart", e.Charts[0].Id); e.Report.Add("Table", e.Charts[0].Id);
        await JS(w, "await nx.test.mode('REPORT'); await nx.test.reportPage(); return true;"); await Shot(w, "v3_12_report");
        e.Theme = "dark"; e.Plot = "dark"; e.Touch(); await JS(w, "await nx.test.mode('VIEW'); return true;"); await Shot(w, "v3_13_view_dark");
        e.Theme = "light"; e.Plot = "light"; e.Touch();
        // ---- perf: X-mapped 10M curve, in-memory monotonic x, through the page (bridge round-trip included)
        foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
        string bench1 = Path.Combine(root, "work", "bench", "bench1_10ch_10M.tdms");
        if (File.Exists(bench1))
        {
            var bf = e.OpenFile(bench1)!; var bc = bf.AllChannels.First();
            var xarr = new double[bc.Length]; double t = 0; var rng = new Random(7);
            for (int i = 0; i < xarr.Length; i++) { t += 1e-4 * (0.5 + rng.NextDouble()); xarr[i] = t; }
            var xch = new Channel("memory", "X", "t", new Import.ArraySource(xarr), new Dictionary<string, object>()) { Unit = "s", DataType = "DBL" };
            var hp = e.AddChart(); e.AddCurve(hp.Id, bc.Id, record: false);
            await e.State.EnsurePyramid(bc);
            sw.Restart(); int miss = hp.Model.SetXChannel(xch); G("xmap_10M_set_ms", sw.Elapsed.TotalMilliseconds, "ms");
            e.TouchChart(hp.Id); await JS(w, "await nx.test.settle(); return true;");
            GB("xmap_10M_mapped", miss == 0 && hp.Model.XChannelOf == xch, $"miss={miss}");
            ReportFrames("xmap_zoom_30", Arr(await JS(w, $"return await nx.test.zoomSteps({hp.Id}, 30);")));
            ReportFrames("xmap_pan_30", Arr(await JS(w, $"return await nx.test.pan({hp.Id}, 30);")));
            var (x0, x1) = hp.Model.FullX(); double span = x1 - x0;
            await JS(w, $"nx.test.plot({hp.Id}).setViewLocal({(x0 + span * 0.5).ToString(System.Globalization.CultureInfo.InvariantCulture)}, {(x0 + span * 0.5 + 0.15).ToString(System.Globalization.CultureInfo.InvariantCulture)}, true); await nx.test.settle(); return true;");
            ReportFrames("xmap_raw_pan_20", Arr(await JS(w, $"return await nx.test.pan({hp.Id}, 20, 0.0133);")));
            await Shot(w, "v3_14_xmap_10M_raw");
            G("memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB", "<=500");
        }
        G("frames_shared_buffer", w.Bridge!.SharedBuffersWorking ? 1 : 0, "", "=1");
        try { File.WriteAllLines(Path.Combine(root, "work", "bench", "gate_v3_web.txt"), Gate); } catch { }
        L($"V3 DONE  fail={Gate.Count(s => s.Contains("\tFAIL"))}");
    }
    static string[] Arr2(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();

    // ================================================================== --stress
    static partial void StressImpl(MainWindow w, ref Task task) => task = Stress(w);
    static long WsMb() => Process.GetCurrentProcess().WorkingSet64 >> 20;
    static async Task Pause(MainWindow w) { await Idle(w, 50 + Rng.Next(251)); }

    static List<string> StressFiles()
    {
        var files = ArgAll("--open").ToList();
        if (files.Count == 0)
        {
            string root = TdmsRoot();
            files.AddRange(Directory.GetFiles(Path.Combine(root, "work", "userfiles"), "*.tdms").OrderBy(f => f));
            files.Add(Path.Combine(root, "ref", "examples", "data", "Example.tdms"));
        }
        return files;
    }

    static async Task Stress(MainWindow w)
    {
        var e = w.Engine; int iters = int.Parse(Arg("--iter", "200")); var files = StressFiles();
        Diag.StallThresholdMs = 300;
        var sw = Stopwatch.StartNew();
        G("startup_max_ui_stall_ms", Diag.MaxStallMs, "ms");
        foreach (var f in files) e.OpenFile(f);
        await JS(w, "await nx.test.settle(); return true;"); await Idle(w, 1500); Diag.ResetStats();
        GC.Collect(); long ws0 = WsMb(); int exceptions = 0, drops = 0, closes = 0, windowsOpened = 0; int wvProc0 = await WebViewProcessCountAsync(); var modes = e.ShowLegacyTabs ? new[] { "NAVIGATOR", "VIEW", "ANALYSIS", "REPORT" } : new[] { "NAVIGATOR", "VIEW" };
        for (int it = 0; it < iters; it++)
        {
            try
            {
                foreach (var md in modes) { await JS(w, $"await nx.test.mode('{md}'); return true;"); await Pause(w); }
                var chans = e.State.Files.SelectMany(f => f.AllChannels).Where(c => !c.IsText).ToList();
                var pick = Enumerable.Range(0, 5).Select(_ => chans[Rng.Next(chans.Count)]).ToArray();
                await JS(w, $"await nx.test.drop({Chans(pick[0])}); return true;"); drops++;
                int chart = await ActiveChart(w);
                foreach (var c in pick.Skip(1)) { await JS(w, $"await nx.test.drop({Chans(c)}, {{chart: {chart}}}); return true;"); drops++; await Pause(w); }
                await JS(w, $"await nx.test.wheel({chart}, 3); await nx.test.click({chart}, 0.4); return true;");
                if (it % 4 == 3)
                {
                    // 0.9.4: detach the chart to a window, pan it there, close the window (chart returns)
                    await JS(w, $"await nx.test.menu({chart}, 'detach'); return true;");
                    var cw = await WaitWindow(w, 1, 20000);
                    if (cw != null) { windowsOpened++; try { await cw.Bridge!.RunTest($"await nx.test.settle(); await nx.test.pan({chart}, 2); return true;", 20000); } catch (Exception ex) { L($"iter {it}: window pan {ex.Message}"); } w.CloseChartWindow(cw.Id); await Pause(w); }
                    else L($"iter {it}: chart window did not open");
                }
                await JS(w, $"await nx.test.clickSel('[data-chart=\"{chart}\"] [data-act=close]'); return true;"); closes++;
                if (it % 10 == 9)
                {
                    var f = e.State.Files[Rng.Next(e.State.Files.Count)]; string p = f.Path;
                    e.CloseFile(f.Name); await Pause(w); e.OpenFile(p); await JS(w, "await nx.test.settle(); return true;");
                }
            }
            catch (Exception ex) { exceptions++; L($"iter {it}: {ex.GetType().Name}: {ex.Message}"); }
            if (it % 25 == 24) L($"iter {it + 1}: stalls={Diag.StallCount} maxStall={Diag.MaxStallMs:F0} ms ws={WsMb()} MB charts={e.Charts.Count} files={e.State.Files.Count}");
        }
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); await Task.Delay(300);
        G("stress_iterations", iters, ""); G("stress_drops", drops, ""); G("stress_chart_closes", closes, "");
        G("stress_exceptions", exceptions, "", "=0"); G("stress_ui_stalls_300ms", Diag.StallCount, "", "=0"); G("stress_max_ui_stall_ms", Diag.MaxStallMs, "ms", "<=200");
        G("stress_mem_growth_mb", WsMb() - ws0, "MB", "<=100"); G("stress_webview_mb", await WebViewMemoryMbAsync(), "MB"); G("stress_total_s", sw.Elapsed.TotalSeconds, "s");
        G("stress_windows_opened", windowsOpened, "", "=50"); G("stress_windows_left", w.Windows.Count, "", "=0"); G("stress_webview_processes_delta", await WebViewProcessCountAsync() - wvProc0, "", "<=1");
        await Shot(w, "stress_end");
    }

    // ================================================================== --stress-analysis
    static partial void StressAnalysisImpl(MainWindow w, ref Task task) => task = StressAnalysis(w);

    static async Task StressAnalysis(MainWindow w)
    {
        var e = w.Engine; var files = StressFiles();
        Diag.StallThresholdMs = 300; G("startup_max_ui_stall_ms", Diag.MaxStallMs, "ms");
        foreach (var f in files) e.OpenFile(f);
        await JS(w, "await nx.test.mode('ANALYSIS'); return true;"); await Idle(w, 1500); Diag.ResetStats();
        var chans = e.State.Files.SelectMany(f => f.AllChannels).Where(c => !c.IsText && c.Length >= 16).ToList();
        var fns = AnalysisService.Functions.Where(f => f != "Formula channel").ToList();
        var plan = new List<string>(); for (int r = 0; r < 50; r++) plan.AddRange(fns); for (int r = 0; r < 50 * fns.Count; r++) plan.Add(fns[Rng.Next(fns.Count)]);
        GC.Collect(); long ws0 = WsMb(); int okRuns = 0, errors = 0, timeouts = 0; var sw = Stopwatch.StartNew();
        for (int i = 0; i < plan.Count; i++)
        {
            string fn = plan[i]; var ch = chans[Rng.Next(chans.Count)];
            e.Analysis.SetInputs(new[] { ch.Id });
            if (fn.StartsWith("IIR")) e.Analysis.SetParam("iir_fc", (ch.XKind == XKind.Implicit ? 0.1 / ch.XIncrement * Rng.NextDouble() : 50).ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
            var task = e.Analysis.RunAsync(fn);
            var done = await Task.WhenAny(task, Task.Delay(10_000));
            if (done != task) { timeouts++; L($"run {i} {fn} on {ch.DisplayName}: TIMEOUT 10 s"); e.Analysis.Cancel(); await task; }
            else if (task.Result) okRuns++; else errors++;
            if (i % 20 == 0) await JS(w, "await nx.test.settle(); return true;");
            if (i % 100 == 99) L($"run {i + 1}/{plan.Count}: ok={okRuns} err={errors} timeouts={timeouts} stalls={Diag.StallCount} ws={WsMb()} MB");
        }
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); await Task.Delay(300);
        G("analysis_runs", plan.Count, ""); G("analysis_ok", okRuns, ""); G("analysis_errors_expected", errors, ""); G("analysis_timeouts", timeouts, "", "=0");
        G("analysis_ui_stalls_300ms", Diag.StallCount, "", "=0"); G("analysis_max_ui_stall_ms", Diag.MaxStallMs, "ms", "<=200");
        G("analysis_mem_growth_mb", WsMb() - ws0, "MB", "<=100"); G("analysis_total_s", sw.Elapsed.TotalSeconds, "s");
        await Shot(w, "stress_analysis_end");
    }
}
