using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NexysView.App;

/// <summary>--mf4: MF4/MDF gates for 1.1 — open (host path shared by dialog and file drop), NAVIGATOR groups → channels, group drop
/// through the navigator row, second-group drop keeps its own master as x, pan/zoom frames, value→text channel as a step curve with
/// the text in the cursor table and PROPERTIES, CAN frame as raw channels, and the 1 GB corpus (work/mf4/big) when present:
/// first screen and pan frames. Captures go to --out; gates to work/bench/gate_mf4.txt.</summary>
public static partial class SelfTest
{
    static async Task Mf4Async(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); var sw = Stopwatch.StartNew();
        const string Settle = "await nx.test.settle(); return true;";
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.Touch();
        string samples = Path.Combine(root, "work", "mf4", "samples");
        if (!Directory.Exists(samples)) samples = Path.Combine(root, "app", "NexysView.Tests", "data", "mdf");
        // ---- 1) open: multirate.mf4 = 3 channel groups (1000 / 100 / 7 Hz), each with its own master
        sw.Restart(); var df = e.OpenFile(Path.Combine(samples, "multirate.mf4"))!; await JS(w, Settle);
        G("open_multirate_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=500");
        GB("file_format_mdf", df.Format == "MDF" && df.Properties.TryGetValue("mdf_version", out var mv) && mv?.ToString() == "4.10", $"format={df.Format} version={df.Properties.GetValueOrDefault("mdf_version")}");
        GB("navigator_groups_channels", df.Groups.Count == 3 && df.Groups.All(g => g.Channels.Count == 3), $"groups={df.Groups.Count} [{string.Join(",", df.Groups.Select(g => g.Name + ":" + g.Channels.Count))}]");
        await Shot(w, "mf4_01_open");
        int grpRows = (await JS(w, "return document.querySelectorAll('.row-grp').length;")).GetInt32();
        GB("navigator_group_rows", grpRows >= 3, $"group rows={grpRows}");
        // ---- 2) group drop through the NAVIGATOR row (real dragstart → drop): x = the group's master, 2 curves
        var types = Arr2(await JS(w, "return await nx.test.dragRowToView('.row-grp');"));
        await JS(w, "try { await nx.test.dragEnd(); } catch {} return true;");   // the navigator's drag preview card ends with the drag
        int chart = await ActiveChart(w); var m = e.Get(chart)!.Model;
        var masters = df.Groups.Select(g => g.Channels.First(c => !c.IsText && c.XChannel == null)).ToList();
        GB("group_drop_chart", m.Curves.Count == 2 && types.Contains("application/x-nexys-group"), $"curves={m.Curves.Count} types=[{string.Join(",", types)}]");
        GB("group_drop_x_master", m.XChannelOf != null && masters.Any(x => x.Id == m.XChannelOf.Id) && m.Curves.All(c => c.Channel.Group == m.XChannelOf.Group), $"x={m.XChannelOf?.Name} group={m.XChannelOf?.Group}");
        await Task.WhenAll(m.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(chart); await JS(w, Settle);
        // ---- 3) group switch: the slowest group dropped as a second chart keeps its own master (7 Hz, 7 samples/s) as x
        var slow = df.Groups.OrderBy(g => g.Channels.Max(c => c.Length)).First(); var slowMaster = slow.Channels.First(c => !c.IsText && c.XChannel == null);
        var c2 = e.DropAll(slow.Channels.Where(c => c != slowMaster && !c.IsText).Select(c => c.Id)); await Task.WhenAll(c2.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(c2.Id); await JS(w, Settle);
        var lastBuf = new double[1]; slowMaster.Source.Read(slowMaster.Length - 1, lastBuf); double lastX = lastBuf[0]; var (fx0, fx1) = c2.Model.FullX();
        GB("group_switch_own_master", c2.Model.Curves.Count == 2 && c2.Model.Curves.All(c => c.Channel.XChannel?.Id == slowMaster.Id && c.Channel.Length == slowMaster.Length) && Math.Abs(fx1 - lastX) < 1e-9 && slowMaster.Length != m.XChannelOf!.Length, $"curve x=[{string.Join(",", c2.Model.Curves.Select(c => c.Channel.XChannel?.Name + "@" + c.Channel.Group))}] full x {fx0:F3}..{fx1:F3} (master last {lastX:F3}) n={slowMaster.Length} vs first chart n={m.XChannelOf?.Length}");
        await Shot(w, "mf4_02_multigroup");
        // ---- 4) pan / zoom frames on the 1 kHz chart
        ReportFrames("pan_20", Arr(await JS(w, $"return await nx.test.pan({chart}, 20);")));
        ReportFrames("zoom_20", Arr(await JS(w, $"return await nx.test.zoomSteps({chart}, 20);")));
        ReportFrames("wheel_10", Arr(await JS(w, $"return await nx.test.wheel({chart}, 10);")));
        // ---- 5) value → text: conversions.mf4 val_to_text (OFF/ON/ERROR cycle) + range_to_text (LOW/MID/HIGH): step curves, texts at cursor A
        foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
        var cf = e.OpenFile(Path.Combine(samples, "conversions.mf4"))!; await JS(w, Settle);
        var vt = cf.AllChannels.First(c => c.Name == "val_to_text"); var rt = cf.AllChannels.First(c => c.Name == "range_to_text");
        GB("text_channel_props", vt.Properties.ContainsKey("text_labels") && vt.Properties["conversion"]?.ToString()?.StartsWith("value → text") == true && !vt.IsText, $"conversion={vt.Properties["conversion"]} labels={vt.Properties["text_labels"]}");
        await JS(w, $"await nx.test.drop({Chans(vt, rt)}); return true;");
        int tc = await ActiveChart(w); var tm = e.Get(tc)!.Model; await Task.WhenAll(tm.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(tc); await JS(w, Settle);
        GB("text_channels_step", tm.Curves.Count == 2 && tm.Curves.All(c => c.Step), $"steps={tm.Curves.Count(c => c.Step)}/{tm.Curves.Count}");
        double xOn = vt.XAt(1), xMid = rt.XAt(12);   // raw 1 → ON ; raw 12 → MID
        e.SetCursor(tc, "a", xOn); e.SetCursor(tc, "b", xMid); await JS(w, Settle);
        string table = (await JS(w, $"return nx.test.plot({tc}).el.querySelector('.cur-table').textContent;")).ToString();
        GB("cursor_table_text_labels", table.Contains("ON") && table.Contains("LOW") && table.Contains("MID"), table[..Math.Min(160, table.Length)]);
        // a second file's groups start collapsed: the search makes the row visible, then a real row click selects it
        await JS(w, $"await nx.test.search('val_to_text'); await nx.test.selectChannel({J(vt.Id)}); return true;");
        string big = (await JS(w, "const n = document.querySelector('.prop-big .num'); return n ? n.textContent : '';")).ToString();
        GB("properties_value_text", big.Trim() == "ON", $"properties value='{big.Trim()}' (expected ON)");
        string props = (await JS(w, "return document.querySelector('.props') ? document.querySelector('.props').textContent : '';")).ToString();
        GB("properties_no_internal_keys", !props.Contains("__labels") && props.Contains("conversion"), $"has __labels={props.Contains("__labels")} has conversion={props.Contains("conversion")}");
        await Shot(w, "mf4_03_text_tooltip");
        await JS(w, "await nx.test.search(''); return true;");
        // ---- 6) CAN frames as raw channels
        var cl = e.OpenFile(Path.Combine(samples, "can_log.mf4"))!;
        var names = cl.AllChannels.Select(c => c.Name).ToList();
        GB("can_raw_channels", names.Contains("CAN_DataFrame.ID") && names.Contains("CAN_DataFrame.DLC") && names.Contains("CAN_DataFrame.DataBytes[0]") && names.Contains("CAN_DataFrame.DataBytes[7]") && cl.Groups[0].Properties.ContainsKey("bus_event"), string.Join(",", names.Take(12)));
        // ---- 7) 1 GB corpus: first screen (open → 10 curves ready and painted) and pan frames
        foreach (var name in new[] { "big_1g_deflate.mf4", "big_1g_uncompressed.mf4" })
        {
            string p = Path.Combine(root, "work", "mf4", "big", name); if (!File.Exists(p)) continue;
            try { if (Directory.Exists(p + ".nvcache")) Directory.Delete(p + ".nvcache", true); } catch { }   // cold pyramids: the first screen is the first ever
            foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
            string tag = name.Contains("deflate") ? "deflate" : "uncompressed";
            sw.Restart(); var bf = e.OpenFile(p)!; await JS(w, Settle); G($"big_{tag}_open_ms", sw.Elapsed.TotalMilliseconds, "ms", name.Contains("deflate") ? "<=1000" : "<=500");
            sw.Restart();
            var ov = e.DropAll(bf.AllChannels.Where(c => !c.IsText && c.XChannel != null).Take(10).Select(c => c.Id)); await Task.WhenAll(ov.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel)));
            e.TouchChart(ov.Id); await JS(w, Settle);
            G($"big_{tag}_first_screen_10ch_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=2000");
            // zoom into the middle first (20 steps ≈ ×16), then pan 20 × 5 % of the zoomed span: the view stays inside the data
            ReportFrames($"big_{tag}_zoom_20", Arr(await JS(w, $"return await nx.test.zoomSteps({ov.Id}, 20);")));
            ReportFrames($"big_{tag}_pan_20", Arr(await JS(w, $"return await nx.test.pan({ov.Id}, 20);")));
            G($"big_{tag}_memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
            if (name.Contains("deflate")) await Shot(w, "mf4_04_big_pan");
            e.CloseFile(bf.Name);
        }
        // ---- 8) 2,000 channels: open + navigator render
        string wide = Path.Combine(root, "work", "mf4", "big", "wide_2000ch.mf4");
        if (File.Exists(wide))
        {
            sw.Restart(); var wf = e.OpenFile(wide)!; await JS(w, Settle); G("wide_2000ch_open_and_navigator_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=300");
            GB("wide_2000ch_count", wf.AllChannels.Count() == 2020 && wf.Groups.Count == 20, $"channels={wf.AllChannels.Count()} groups={wf.Groups.Count}");
            e.CloseFile(wf.Name);
        }
        try { File.WriteAllLines(Path.Combine(root, "work", "bench", "gate_mf4.txt"), Gate); } catch { }
    }
}
