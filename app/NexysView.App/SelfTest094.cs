using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NexysView.App.Services;

namespace NexysView.App;

/// <summary>--v094: gates for 0.9.4 — toolbar modes, no accent card borders, multi-select drag preview position, detach → chart
/// window → band sync → close returns the chart, grip drag-out, pages (add/select/rename/reorder/remove/auto split), 1/2 column grid,
/// cross-window pan latency with 10 curves × 10M in each window, memory per window.</summary>
public static partial class SelfTest
{
    static partial void V094Impl(MainWindow w, ref Task task) => task = V094(w);

    static async Task<ChartWindow?> WaitWindow(MainWindow w, int count, int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (w.Windows.Count >= count) { var cw = w.Windows.Values.Last(); try { await cw.WhenReady; } catch { } await Idle(w, 150); return cw; } await Task.Delay(50); }
        return null;
    }
    static async Task<JsonElement> JSW(ChartWindow cw, string body) => await cw.Bridge!.RunTest(body);

    static async Task V094(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); var sw = Stopwatch.StartNew();
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.Touch();
        string sample = SampleData.Ensure() ?? ""; var df = e.OpenFile(sample)!; await JS(w, "await nx.test.settle(); return true;");
        // ---- 1) toolbar
        string modes = (await JS(w, "return Array.from(document.querySelectorAll('.tools-modes .tool.mode')).map(b => b.textContent.trim() + (b.classList.contains('on') ? '*' : '')).join(',');")).ToString();
        bool dev = (await JS(w, "return !!document.querySelector('[data-a=dev]');")).GetBoolean();
        GB("toolbar_modes_first_group", modes == "NAVIGATOR,VIEW*" && !dev, $"modes={modes} devButton={dev}");
        string under = (await JS(w, "const b = document.querySelector('.tool.mode.on'); const cs = getComputedStyle(b); return cs.boxShadow + '|' + cs.fontWeight + '|' + cs.backgroundColor;")).ToString();
        GB("toolbar_active_style", under.Contains("inset") && under.Contains("600"), under);
        await Shot(w, "v094_01_toolbar_light");
        e.Theme = "dark"; e.Touch(); await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "v094_02_toolbar_dark"); e.Theme = "light"; e.Touch();
        // ---- 2) drag preview + the fixed row positions
        var s1 = df.AllChannels.First(c => c.Name == "Sine 1 Hz"); var s5 = df.AllChannels.First(c => c.Name == "Sine 5 Hz");
        await JS(w, $"nx.test.pick([{J(s1.Id)}, {J(s5.Id)}]); return true;");
        string tops = (await JS(w, $"return JSON.stringify(nx.test.rowTops([{J(s1.Id)}, {J(s5.Id)}]));")).ToString();
        GB("picked_rows_keep_absolute_position", tops.Contains("\"abs\":true") && !tops.Contains("\"abs\":false") && tops.IndexOf("top") != tops.LastIndexOf("top") && !tops.Contains("\"top\":0,\"abs\":true,") , tops);
        string pv0 = (await JS(w, $"return JSON.stringify(await nx.test.dragStartRow({J(s1.Id)}));")).ToString();
        GB("drag_preview_shown", pv0.Contains("2 channels") && pv0.Contains("Sine 1 Hz"), pv0[..Math.Min(140, pv0.Length)]);
        var p1 = JsonDocument.Parse((await JS(w, "return JSON.stringify(await nx.test.dragMove(300, 400));")).ToString()).RootElement;
        GB("drag_preview_follows_pointer", p1.ValueKind == JsonValueKind.Object && Math.Abs(p1.GetProperty("x").GetDouble() - 314) < 1 && Math.Abs(p1.GetProperty("y").GetDouble() - 414) < 1, p1.ToString());
        await Shot(w, "v094_05_drag_preview");
        var p2 = JsonDocument.Parse((await JS(w, "return JSON.stringify(await nx.test.dragMove(window.innerWidth - 4, window.innerHeight - 4));")).ToString()).RootElement;
        GB("drag_preview_stays_in_window", p2.ValueKind == JsonValueKind.Object && p2.GetProperty("right").GetDouble() <= p2.GetProperty("vw").GetDouble() + 0.5 && p2.GetProperty("bottom").GetDouble() <= p2.GetProperty("vh").GetDouble() + 0.5, p2.ToString());
        bool gone = (await JS(w, "return await nx.test.dragEnd();")).GetBoolean();
        GB("drag_preview_removed_on_end", gone, $"removed={gone}");
        // ---- 3) accent borders: chart with band + detail, help open
        await JS(w, $"await nx.test.drop({Chans(s1, s5)}); return true;");
        int chart = await ActiveChart(w); var m = e.Get(chart)!.Model;
        e.SetXChannel(chart, df.AllChannels.First(c => c.Name == "Time").Id); e.SetCursorMode(chart, "band"); e.LinkDetail(chart, s1.Id);
        await JS(w, "await nx.test.settle(); return true;");
        var det = e.Charts.First(c => c.DetailOf == chart);
        await JS(w, "await nx.test.help('band'); return true;");
        var scan = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.accentScan());")).ToString()).RootElement;
        int hits = scan.GetProperty("hits").GetArrayLength();
        GB("accent_borders_zero", hits == 0, $"hits={hits} {scan.GetProperty("hits")}");
        await JS(w, "document.querySelector('.help-bg')?.remove(); return true;");
        string chip = (await JS(w, $"return nx.test.plot({det.Id}).el.querySelector('.chart-h .chip')?.textContent ?? '';")).ToString();
        GB("detail_link_chip", chip.StartsWith("linked ·"), chip);
        await Shot(w, "v094_03_detail_chip");
        // ---- 4) detach the detail chart → window 2, band sync across windows, close → back
        await JS(w, $"await nx.test.menu({det.Id}, 'detach'); return true;");
        var cw = await WaitWindow(w, 1);
        GB("detach_opens_window", cw != null && det.WindowId == cw.Id && e.Windows.ContainsKey(cw.Id), $"windows={w.Windows.Count} chart.window={det.WindowId}");
        if (cw != null)
        {
            bool mainHides = (await JS(w, $"return !nx.test.plot({det.Id});")).GetBoolean();
            bool winShows = (await JSW(cw, $"await nx.test.settle(); return !!nx.test.plot({det.Id});")).GetBoolean();
            GB("detached_chart_moves_between_pages", mainHides && winShows, $"mainHides={mainHides} windowShows={winShows}");
            int f0 = JsonDocument.Parse((await JSW(cw, $"return JSON.stringify(nx.test.frameClock({det.Id}));")).ToString()).RootElement.GetProperty("frames").GetInt32();
            await JS(w, $"return await nx.test.bandDrag({chart}, 8, 4);");
            await Task.Delay(300); await JSW(cw, "await nx.test.settle(); return true;");
            var fc = JsonDocument.Parse((await JSW(cw, $"return JSON.stringify(nx.test.frameClock({det.Id}));")).ToString()).RootElement;
            bool synced = Math.Abs(det.Model.XMin - m.BandX0) < 1e-9 && Math.Abs(fc.GetProperty("xmin").GetDouble() - m.BandX0) < 1e-6 && fc.GetProperty("frames").GetInt32() > f0;
            GB("band_sync_across_windows", synced, $"band {m.BandX0:F4} detail {det.Model.XMin:F4} window-page xmin {fc.GetProperty("xmin").GetDouble():F4} frames {f0}->{fc.GetProperty("frames").GetInt32()}");
            await cw.CaptureAsync(Path.Combine(OutDir, "v094_04_window.png")); L("shot v094_04_window.png");
            await Shot(w, "v094_04_main_with_window");
            w.CloseChartWindow(cw.Id); await Idle(w, 300); await JS(w, "await nx.test.settle(); return true;");
            bool back = det.WindowId == 0 && w.Windows.Count == 0 && (await JS(w, $"return !!nx.test.plot({det.Id});")).GetBoolean();
            GB("close_window_returns_chart", back, $"windows={w.Windows.Count} chart.window={det.WindowId}");
        }
        // grip dragged out of the window → window
        await JS(w, $"return await nx.test.gripDragOut({det.Id});");
        var cw2 = await WaitWindow(w, 1);
        GB("grip_drag_out_opens_window", cw2 != null && det.WindowId == cw2.Id, $"windows={w.Windows.Count}");
        if (cw2 != null) { w.CloseChartWindow(cw2.Id); await Idle(w, 300); }
        // ---- 5) pages
        int p2id = (await JS(w, "return await nx.call('page.add');")).GetInt32(); await JS(w, "await nx.test.settle(); return true;");
        GB("page_add_selects", e.Pages.Count == 2 && e.ActivePage == p2id, $"pages={e.Pages.Count} active={e.ActivePage}");
        var ramp = df.AllChannels.First(c => c.Name == "Ramp");
        await JS(w, $"await nx.test.drop({Chans(ramp)}); return true;"); int c2 = await ActiveChart(w);
        int shown = (await JS(w, "return nx.test.state().charts.filter(c => c.pageId === nx.test.state().activePage && !c.windowId).length;")).GetInt32();
        GB("chart_belongs_to_active_page", e.Get(c2)!.PageId == p2id && shown == 1, $"chart.page={e.Get(c2)!.PageId} shown={shown}");
        await JS(w, "await nx.test.pageTabAction(1, 'click'); return true;");
        int shown1 = (await JS(w, "return document.querySelectorAll('.charts .chart').length;")).GetInt32();
        GB("page_tab_switches", e.ActivePage == 1 && shown1 == 2, $"active={e.ActivePage} charts shown={shown1}");
        await JS(w, $"await nx.test.pageTabAction({p2id}, 'click'); await nx.test.pageTabAction({p2id}, 'dblclick'); await nx.test.renameInput('Zoom'); return true;");
        GB("page_rename", e.Pages.First(p => p.Id == p2id).Name == "Zoom", $"name={e.Pages.First(p => p.Id == p2id).Name}");
        await JS(w, $"await nx.call('page.moveTo', {{page: {p2id}, index: 0}}); await nx.test.settle(); return true;");
        GB("page_reorder", e.Pages[0].Id == p2id, $"order=[{string.Join(",", e.Pages.Select(p => p.Name))}]");
        await JS(w, $"await nx.call('page.columns', {{page: {p2id}, n: 2}}); await nx.test.settle(); return true;");
        int cols = (await JS(w, "return nx.test.gridCols();")).GetInt32();
        GB("grid_two_columns", cols == 2 && e.Pages.First(p => p.Id == p2id).Columns == 2, $"cols={cols}");
        await JS(w, $"await nx.test.drop({Chans(df.AllChannels.First(c => c.Name == "Temperature"))}); return true;");
        await Shot(w, "v094_06_pages_2col");
        await JS(w, $"await nx.test.pageTabAction({p2id}, 'remove'); return true;");
        bool modal = (await JS(w, "return !!document.querySelector('.modal [data-b=ok]');")).GetBoolean();
        await JS(w, "await nx.test.modalButton('ok'); return true;");
        GB("page_remove_confirms_and_moves_charts", modal && e.Pages.Count == 1 && e.Charts.All(c => c.PageId == 1), $"modal={modal} pages={e.Pages.Count}");
        e.SetAutoPage(true, 2); int pagesBefore = e.Pages.Count;
        foreach (var n in new[] { "Square", "Counter", "State" }) await JS(w, $"await nx.test.drop({Chans(df.AllChannels.First(c => c.Name == n))}); return true;");
        GB("auto_page_split", e.Pages.Count > pagesBefore && e.Charts.GroupBy(c => c.PageId).All(g => g.Count() <= 2 || g.Key == 1), $"pages {pagesBefore}->{e.Pages.Count} per page=[{string.Join(",", e.Pages.Select(p => e.Charts.Count(c => c.PageId == p.Id)))}]");
        e.SetAutoPage(true, 4);
        // ---- 6) perf: 10 curves × 10M in the main window and in window 2 (x-linked), pan in main → frame in window 2
        foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
        foreach (var p in e.Pages.Skip(1).ToList()) e.RemovePage(p.Id);
        string bench1 = Path.Combine(root, "work", "bench", "bench1_10ch_10M.tdms");
        if (File.Exists(bench1))
        {
            var bf = e.OpenFile(bench1)!; await JS(w, "await nx.test.settle(); return true;");
            var a = e.DropAll(bf.AllChannels.Take(10).Select(c => c.Id)); var b = e.DropAll(bf.AllChannels.Take(10).Select(c => c.Id));
            await Task.WhenAll(a.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(-1); await JS(w, "await nx.test.settle(); return true;");
            GC.Collect(); double ws0 = Process.GetCurrentProcess().WorkingSet64 / 1048576.0, wv0 = await WebViewMemoryMbAsync();
            await JS(w, $"await nx.test.menu({b.Id}, 'detach'); return true;");
            var cwp = await WaitWindow(w, 1);
            if (cwp != null)
            {
                await JSW(cwp, "await nx.test.settle(); return true;"); await Task.Delay(500);
                GC.Collect(); G("memory_ws_per_window_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0 - ws0, "MB"); G("memory_webview_per_window_mb", await WebViewMemoryMbAsync() - wv0, "MB");
                var sent = Arr(await JS(w, $"return await nx.test.panClock({a.Id}, 30);"));
                await Task.Delay(300);
                var paints = Arr(await JSW(cwp, $"return nx.test.plot({b.Id}).paintEpochs;"));
                var lat = new List<double>();
                foreach (var t in sent) { var p = paints.Where(x => x >= t).DefaultIfEmpty(double.NaN).Min(); if (!double.IsNaN(p)) lat.Add(p - t); }
                ReportFrames("cross_window_pan_latency", lat.ToArray());
                GB("cross_window_pan_synced", Math.Abs(a.Model.XMin - b.Model.XMin) < 1e-9 && lat.Count >= 25, $"a {a.Model.XMin:F3} b {b.Model.XMin:F3} measured={lat.Count}/30");
                await cwp.CaptureAsync(Path.Combine(OutDir, "v094_07_window_10M.png")); await Shot(w, "v094_07_main_10M");
                w.CloseChartWindow(cwp.Id); await Idle(w, 300);
            }
            else GB("perf_window_opened", false, "window 2 did not open");
            G("memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB", "<=700");
        }
        G("windows_left_open", w.Windows.Count, "", "=0");
        G("frames_shared_buffer", w.Bridge!.SharedBuffersWorking ? 1 : 0, "", "=1");
        try { File.WriteAllLines(Path.Combine(root, "work", "bench", "gate_v094.txt"), Gate); } catch { }
        L($"V094 DONE  fail={Gate.Count(s => s.Contains("\tFAIL"))}");
    }
}
