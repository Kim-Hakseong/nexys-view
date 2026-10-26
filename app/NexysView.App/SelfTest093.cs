using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NexysView.App.Services;
using NexysView.Core.Data;

namespace NexysView.App;

/// <summary>--v093: gates for the 0.9.3 VIEW features, driven by real DOM events on the page — group drop → chart, right-drag zoom
/// (box / x-only / y-only), Backspace zoom history, band cursor (create, move, handles, keyboard), overview/detail sync both ways,
/// x-axis link, value table (cursor row, Ctrl+C copy), CSV / TDMS range export, band-drag frame time on 10 curves × 10M.</summary>
public static partial class SelfTest
{
    static partial void V093Impl(MainWindow w, ref Task task) => task = V093(w);
    static string Inv(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    static async Task V093(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); var sw = Stopwatch.StartNew();
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.Touch();
        await JS(w, "await nx.test.settle(); return true;");
        string tabs = (await JS(w, "return Array.from(document.querySelectorAll('.modes a')).map(a => a.textContent).join(',');")).ToString();
        GB("tabs_navigator_view_only", tabs == "NAVIGATOR,VIEW", tabs);
        // ---- 1) group drop: a log file's "Channel Group 1" node → one chart, y axis per unit, steps as lanes, x = System Time
        string uf = Path.Combine(root, "work", "userfiles"); var logs = Directory.GetFiles(uf, "*.tdms").OrderBy(f => f).ToList();
        var df = e.OpenFile(logs[1])!; await JS(w, "await nx.test.settle(); return true;");
        var types = Arr2(await JS(w, "return await nx.test.dragRowToView('.row-grp');"));
        int chart = await ActiveChart(w); var ch = e.Get(chart); var m = ch?.Model;
        int expect = df.Groups[0].Channels.Count(c => !c.IsText) - 1;   // time channel becomes x
        GB("group_drop_chart", m != null && m.Curves.Count == expect && types.Contains("application/x-nexys-group"), $"curves={m?.Curves.Count} expected={expect} types=[{string.Join(",", types)}]");
        GB("group_drop_x_time", m != null && m.XChannelOf?.Name == "System Time" && m.XTimeMode == "relative", $"x={m?.XChannelOf?.Name} mode={m?.XTimeMode}");
        GB("group_drop_axes_by_unit", m != null && m.YAxes.Count == m.Curves.Select(c => c.Axis).Distinct().Count() && m.YAxes.Count <= 3, $"axes={m?.YAxes.Count} units=[{string.Join(",", m?.YAxes.Select(a => a.Unit) ?? Array.Empty<string>())}]");
        GB("group_drop_lanes", m != null && m.DigitalLanes && m.Curves.Any(c => c.Step), $"lanes={m?.DigitalLanes} steps={m?.Curves.Count(c => c.Step)}");
        await Task.WhenAll(m!.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;");
        await Shot(w, "v093_01_group_drop");
        // per-channel rule
        e.DropMode = "each"; int before = e.Charts.Count;
        await JS(w, "await nx.test.clickSel('.nav-search [data-b=flat]'); await nx.test.dragRowToView('.row-grp'); return true;");
        GB("group_drop_per_channel", e.Charts.Count - before == expect, $"charts +{e.Charts.Count - before} expected {expect}");
        G("memory_ws_20_charts_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
        foreach (var c in e.Charts.Where(c => c.Id != chart).ToList()) e.RemoveChart(c.Id, false);
        await JS(w, "await nx.test.settle(); return true;"); GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); await Task.Delay(200);
        G("memory_ws_after_20_removed_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
        e.DropMode = "unit"; e.ActiveChart = chart; e.TouchChart(chart); await JS(w, "await nx.test.settle(); return true;");
        // ---- 2) right-drag zoom box, x-only, y-only; Backspace history
        double x0 = m.XMin, x1 = m.XMax, span = x1 - x0;
        await JS(w, $"return await nx.test.rightZoom({chart}, 0.25, 0.2, 0.75, 0.8);");
        bool boxOk = Math.Abs(m.XMin - (x0 + span * 0.25)) < span * 0.01 && Math.Abs(m.XMax - (x0 + span * 0.75)) < span * 0.01 && m.YAxes.All(a => !a.Auto);
        GB("right_drag_zoom_box", boxOk, $"x {m.XMin:F3}..{m.XMax:F3} expected {x0 + span * 0.25:F3}..{x0 + span * 0.75:F3} yManual={m.YAxes.All(a => !a.Auto)}");
        await Shot(w, "v093_02_right_zoom");
        double zx0 = m.XMin, zx1 = m.XMax; var ymin = m.YAxes[0].Min;
        await JS(w, $"return await nx.test.rightZoom({chart}, 0.2, 0.1, 0.6, 0.9, {{shift: true}});");
        GB("right_drag_zoom_x_only", Math.Abs(m.XMin - (zx0 + (zx1 - zx0) * 0.2)) < (zx1 - zx0) * 0.01 && m.YAxes[0].Min == ymin, $"x {m.XMin:F3} y-min unchanged={m.YAxes[0].Min == ymin}");
        double cx0 = m.XMin, cx1 = m.XMax; double ymax0 = m.YAxes[0].Max;
        await JS(w, $"return await nx.test.rightZoom({chart}, 0.1, 0.3, 0.9, 0.7, {{ctrl: true}});");
        GB("right_drag_zoom_y_only", m.XMin == cx0 && m.XMax == cx1 && m.YAxes[0].Max < ymax0, $"x unchanged={m.XMin == cx0 && m.XMax == cx1} y-max {ymax0:F3} -> {m.YAxes[0].Max:F3}");
        int hist = m.ZoomHistory.Count;
        await JS(w, "await nx.test.key('Backspace'); return true;");
        GB("backspace_zoom_back", m.ZoomHistory.Count == hist - 1 && m.YAxes[0].Max == ymax0 && m.XMin == cx0, $"history {hist} -> {m.ZoomHistory.Count} y-max restored={m.YAxes[0].Max == ymax0}");
        await JS(w, "await nx.test.key('Backspace'); await nx.test.key('Backspace'); return true;");
        GB("backspace_to_full", Math.Abs(m.XMin - x0) < 1e-9 && Math.Abs(m.XMax - x1) < 1e-9 && m.YAxes.All(a => a.Auto), $"x {m.XMin:F3}..{m.XMax:F3} auto={m.YAxes.All(a => a.Auto)}");
        // ---- 3) band cursor: Ctrl+B creates one, drag inside moves it, handle drag changes the width, arrows nudge
        await JS(w, "await nx.test.key('b', {ctrlKey: true}); return true;");
        GB("band_mode_ctrl_b", m.CursorMode == "band" && m.BandEnabled, $"mode={m.CursorMode} band={m.BandEnabled} {m.BandX0:F3}..{m.BandX1:F3}");
        double b0 = m.BandX0, b1 = m.BandX1, bw = b1 - b0;
        double f0 = (b0 - m.XMin) / (m.XMax - m.XMin), f1 = (b1 - m.XMin) / (m.XMax - m.XMin), fm = (f0 + f1) / 2;
        await JS(w, $"return await nx.test.leftDrag({chart}, {Inv(fm)}, {Inv(fm + 0.1)});");
        GB("band_drag_moves", Math.Abs((m.BandX0 - b0) - span * 0.1) < span * 0.005 && Math.Abs((m.BandX1 - m.BandX0) - bw) < 1e-9, $"moved {m.BandX0 - b0:F3} expected {span * 0.1:F3} width {m.BandX1 - m.BandX0:F3} vs {bw:F3}");
        double e1 = (m.BandX1 - m.XMin) / (m.XMax - m.XMin);
        await JS(w, $"return await nx.test.leftDrag({chart}, {Inv(e1)}, {Inv(e1 + 0.05)});");
        GB("band_handle_resizes", Math.Abs((m.BandX1 - m.BandX0) - (bw + span * 0.05)) < span * 0.005, $"width {m.BandX1 - m.BandX0:F3} expected {bw + span * 0.05:F3}");
        double nb0 = m.BandX0; double pxX = (await JS(w, $"return nx.test.plot({chart}).xPerPx;")).GetDouble();
        await JS(w, "await nx.test.key('ArrowRight', {shiftKey: true}); return true;");
        GB("band_arrow_nudge_10px", Math.Abs((m.BandX0 - nb0) - 10 * pxX) < pxX * 0.01, $"moved {(m.BandX0 - nb0) / pxX:F2} px");
        string strip = (await JS(w, $"return nx.test.plot({chart}).el.querySelector('.cur-table').textContent;")).ToString();
        GB("band_readout_strip", strip.Contains("BAND") && strip.Contains("RMS") && strip.Contains("P-P"), strip[..Math.Min(100, strip.Length)]);
        await Shot(w, "v093_03_band");
        // ---- 4) overview / detail
        await JS(w, $"await nx.test.menu({chart}, 'detail'); return true;");
        var det = e.Charts.FirstOrDefault(c => c.DetailOf == chart); var dm = det?.Model;
        GB("detail_chart_created", dm != null && dm.Curves.Count == 1 && dm.XMin == m.BandX0 && dm.XMax == m.BandX1, $"detail={det?.Id} curves={dm?.Curves.Count} x={dm?.XMin:F3}..{dm?.XMax:F3} band={m.BandX0:F3}..{m.BandX1:F3}");
        await JS(w, "await nx.test.settle(); return true;");
        await JS(w, $"return await nx.test.bandDrag({chart}, 8, 5, {det!.Id});");
        var cv = dm!.Curves[0]; double i0 = XIndex.IndexOf(cv.XChannel!, (dm.XMin - cv.XOffset) / cv.XScale), j0 = XIndex.IndexOf(cv.XChannel!, (m.BandX0 - cv.XOffset) / cv.XScale);
        GB("detail_follows_band", dm.XMin == m.BandX0 && dm.XMax == m.BandX1 && Math.Abs(i0 - j0) < 1e-9, $"detail {dm.XMin:F4}..{dm.XMax:F4} band {m.BandX0:F4}..{m.BandX1:F4} Δsamples={Math.Abs(i0 - j0):F3}");
        await JS(w, $"return await nx.test.zoomSteps({det.Id}, 3);");
        GB("band_follows_detail_zoom", Math.Abs(m.BandX0 - dm.XMin) < 1e-9 && Math.Abs(m.BandX1 - dm.XMax) < 1e-9, $"band {m.BandX0:F4}..{m.BandX1:F4} detail {dm.XMin:F4}..{dm.XMax:F4}");
        var other = m.Curves.First(c => c.Channel != cv.Channel).Channel;
        await JS(w, $"await nx.test.drop({Chans(other)}, {{chart: {det.Id}}}); return true;");
        GB("detail_drop_replaces_channel", dm.Curves.Count == 1 && dm.Curves[0].Channel == other, $"detail curve={dm.Curves[0].Channel.Name}");
        await Shot(w, "v093_04_detail");
        // ---- 5) x-axis link: a second chart on the same x follows the overview's pan
        var deg = df.AllChannels.First(c => c.Name == "DEG");
        await JS(w, $"await nx.test.drop({Chans(deg)}); return true;"); int c2 = await ActiveChart(w); var m2 = e.Get(c2)!.Model;
        await JS(w, $"await nx.test.drop({Chans(df.AllChannels.First(c => c.Name == "System Time"))}, {{chart: {c2}, zone: 'x'}}); return true;");
        GB("link_key_shared", e.LinkKey(e.Get(c2)!) == e.LinkKey(ch!) && e.LinkKey(ch!) != null, $"key={e.LinkKey(ch!)}");
        await JS(w, $"return await nx.test.pan({chart}, 3, 0.05);");
        GB("link_pan_follows", Math.Abs(m2.XMin - m.XMin) < 1e-9 && Math.Abs(m2.XMax - m.XMax) < 1e-9, $"chart2 {m2.XMin:F3}..{m2.XMax:F3} overview {m.XMin:F3}..{m.XMax:F3}");
        e.SetLinkX(c2, false); await JS(w, $"return await nx.test.pan({chart}, 2, 0.05);");
        GB("link_off_independent", Math.Abs(m2.XMin - m.XMin) > 1e-9, $"chart2 {m2.XMin:F3} overview {m.XMin:F3}");
        e.SetLinkX(c2, true); await JS(w, "await nx.test.settle(); return true;");
        string bref = (await JS(w, $"return JSON.stringify(nx.test.plot({c2}).lastHdr?.bandRef ?? null);")).ToString();
        GB("band_ref_on_linked_chart", bref.Contains("x0"), bref);
        e.RemoveChart(c2, false);
        // ---- 6) value table: cursor row + Ctrl+C copy
        e.SetCursorMode(chart, "free"); await JS(w, "await nx.test.settle(); return true;");
        int tid = (await JS(w, $"return await nx.call('table.fromChart', {{chart: {chart}}});")).GetInt32();
        await JS(w, "await nx.test.settle(); return true;");
        var t = e.Tables.First(x => x.Id == tid); var tch = t.Channels[0]; var tcv = m.Curves.First(c => c.Channel == tch);
        double xs = tcv.XAt(777);
        e.SetCursor(chart, "a", xs); await JS(w, "await nx.test.settle(); await new Promise(r => setTimeout(r, 200)); return true;");
        string info = (await JS(w, $"return JSON.stringify(nx.test.tableInfo({tid}));")).ToString();
        GB("table_cursor_row_777", info.Contains("\"cursorRow\":777") && info.Contains("\"curVisible\":true"), info);
        string tsv = (await JS(w, $"return await nx.test.tableCopy({tid}, 770, 1, 772, 3);")).ToString();
        var lines = tsv.Split('\n'); var vals = new double[3]; tch.Source.Read(770, vals); var v2 = new double[3]; t.Channels[1].Source.Read(770, v2);
        var r1 = lines.Length > 3 ? lines[1].Split('\t') : Array.Empty<string>(); var r3 = lines.Length > 3 ? lines[3].Split('\t') : Array.Empty<string>();
        bool tsvOk = lines.Length == 4 && r1.Length == 3 && double.Parse(r1[1], CultureInfo.InvariantCulture) == vals[0] && double.Parse(r3[2], CultureInfo.InvariantCulture) == v2[2] && Math.Abs(double.Parse(r1[0], CultureInfo.InvariantCulture) - tcv.XAt(770)) < 1e-9;
        GB("table_copy_tsv_values", tsvOk, tsv.Replace("\n", " | ")[..Math.Min(160, tsv.Length)]);
        await Shot(w, "v093_05_table");
        e.RemoveTable(tid);
        // ---- 7) CSV / TDMS export of the band range
        e.SetCursorMode(chart, "band"); await JS(w, "await nx.test.settle(); return true;");
        string csv = Path.Combine(OutDir, "band.csv"), tdms = Path.Combine(OutDir, "band.tdms");
        var ids = JsonSerializer.Serialize(m.Curves.Select(c => c.Channel.Id).ToList());
        await JS(w, $"return await nx.call('export.rangeTo', {{path: {J(csv)}, kind: 'csv', channels: {ids}, chart: {chart}, x0: {Inv(m.BandX0)}, x1: {Inv(m.BandX1)}}});");
        var csvLines = File.ReadAllLines(csv); var c0 = m.Curves[0];
        long firstIdx = (long)Math.Ceiling(XIndex.IndexOf(c0.XChannel!, (m.BandX0 - c0.XOffset) / c0.XScale)), lastIdx = (long)Math.Floor(XIndex.IndexOf(c0.XChannel!, (m.BandX1 - c0.XOffset) / c0.XScale));
        var one = new double[1]; c0.Channel.Source.Read(firstIdx, one); var cells = csvLines[1].Split(',');
        bool csvOk = csvLines.Length == lastIdx - firstIdx + 2 && long.Parse(cells[0]) == firstIdx && double.Parse(cells[2], CultureInfo.InvariantCulture) == one[0] && Math.Abs(double.Parse(cells[1], CultureInfo.InvariantCulture) - c0.XAt(firstIdx)) < 1e-9;
        GB("csv_band_export_values", csvOk, $"rows={csvLines.Length - 1} expected={lastIdx - firstIdx + 1} first={cells[0]}/{firstIdx} v={cells[2]}/{one[0]}");
        await JS(w, $"return await nx.call('export.rangeTo', {{path: {J(tdms)}, kind: 'tdms', channels: {ids}, chart: {chart}, x0: {Inv(m.BandX0)}, x1: {Inv(m.BandX1)}}});");
        using (var re = new Core.Tdms.TdmsFile(tdms))
        {
            var o = re.Find(Core.Tdms.TdmsPath.Make(c0.Channel.Group, c0.Channel.Name)); var back = o == null ? Array.Empty<double>() : re.ReadDoubles(o);
            GB("tdms_band_export_values", back.Length == lastIdx - firstIdx + 1 && back.Length > 0 && back[0] == one[0], $"samples={back.Length} expected={lastIdx - firstIdx + 1}");
        }
        // ---- 8) PNG of a chart to file
        string png = Path.Combine(OutDir, "chart.png");
        await JS(w, $"return await nx.call('png.saveTo', {{path: {J(png)}, dataUrl: nx.test.plot({chart}).pngDataUrl()}});");
        GB("chart_png_saved", File.Exists(png) && new FileInfo(png).Length > 5000, $"{(File.Exists(png) ? new FileInfo(png).Length : 0)} bytes");
        // ---- 9) perf: 10 curves × 10M overview + detail, band drag frames
        foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
        string bench1 = Path.Combine(root, "work", "bench", "bench1_10ch_10M.tdms");
        if (File.Exists(bench1))
        {
            var bf = e.OpenFile(bench1)!; await JS(w, "await nx.test.settle(); return true;");
            sw.Restart();
            var ov = e.DropAll(bf.AllChannels.Take(10).Select(c => c.Id)); await Task.WhenAll(ov.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel)));
            e.TouchChart(ov.Id); await JS(w, "await nx.test.settle(); return true;");
            G("overview_10x10M_ready_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=3000");
            G("memory_ws_after_10x10M_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
            await JS(w, $"await nx.test.menu({ov.Id}, 'detail'); return true;");
            var d10 = e.Charts.First(c => c.DetailOf == ov.Id); await JS(w, "await nx.test.settle(); return true;");
            ReportFrames("band_drag_live_detail_frames", Arr(await JS(w, $"return await nx.test.bandDragLive({ov.Id}, 6, 30, {d10.Id});")));
            ReportFrames("band_drag_live_overview_frames", Arr(await JS(w, $"return await nx.test.bandDragLive({ov.Id}, -6, 20);")));
            ReportFrames("band_nudge_commit_detail_frames", Arr(await JS(w, $"return await nx.test.bandDrag({ov.Id}, 6, 10, {d10.Id});")), "<=60");
            ReportFrames("overview_pan_30", Arr(await JS(w, $"return await nx.test.pan({ov.Id}, 30);")));
            ReportFrames("detail_zoom_30", Arr(await JS(w, $"return await nx.test.zoomSteps({d10.Id}, 30);")));
            GB("detail_sync_after_zoom", Math.Abs(ov.Model.BandX0 - d10.Model.XMin) < 1e-9 && Math.Abs(ov.Model.BandX1 - d10.Model.XMax) < 1e-9, $"band {ov.Model.BandX0:F3}..{ov.Model.BandX1:F3}");
            await Shot(w, "v093_06_overview_detail_10M");
            e.Theme = "dark"; e.Touch(); await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "v093_07_dark"); e.Theme = "light"; e.Touch();
            e.State.Undo.Clear(); GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true); GC.WaitForPendingFinalizers(); await Task.Delay(300);
            G("memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB", "<=700");
            G("memory_private_mb", Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0, "MB");
        }
        G("frames_shared_buffer", w.Bridge!.SharedBuffersWorking ? 1 : 0, "", "=1");
        try { File.WriteAllLines(Path.Combine(root, "work", "bench", "gate_v093.txt"), Gate); } catch { }
        L($"V093 DONE  fail={Gate.Count(s => s.Contains("\tFAIL"))}");
    }
}
