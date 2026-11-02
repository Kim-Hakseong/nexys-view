using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.Core.Data;

namespace NexysView.App;

/// <summary>--csv: CSV import gates for 1.1 — auto-detected open (no dialog) with implicit time x, group drop + pan/zoom, comma
/// decimal / tab + units / timestamps / text columns, the import dialog for a low-confidence file (reasons, live preview on a
/// setting change, OPEN), the IMPORT… reopen from the file card, and the perf corpus (work/csv/big when present): first open
/// (parse), cached reopen, first screen, pan frames, wide file. Captures go to --out; gates to work/bench/gate_csv.txt.</summary>
public static partial class SelfTest
{
    static async Task CsvAsync(MainWindow w)
    {
        var e = w.Engine; string root = TdmsRoot(); var sw = Stopwatch.StartNew();
        const string Settle = "await nx.test.settle(); return true;";
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.Touch();
        string samples = Path.Combine(root, "app", "NexysView.Tests", "data", "csv");
        Core.Csv.CsvDataSource.CacheRootOverride = Path.Combine(Path.GetTempPath(), "nexys_csv_selftest");   // the samples folder stays clean
        try { if (Directory.Exists(Core.Csv.CsvDataSource.CacheRootOverride)) Directory.Delete(Core.Csv.CsvDataSource.CacheRootOverride, true); } catch { }
        e.State.Pyramids.CacheRootOverride = Core.Csv.CsvDataSource.CacheRootOverride;
        // ---- 1) auto-detected open: comma, names, 1 kHz time column → implicit x, no dialog
        sw.Restart(); var df = await e.OpenFileAsync(Path.Combine(samples, "basic_comma.csv")); await JS(w, Settle);
        G("open_basic_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=500");
        GB("auto_open_no_dialog", df != null && e.CsvReview == null && df.Format == "CSV" && df.ChannelCount == 4, $"file={df?.Name} channels={df?.ChannelCount} review={(e.CsvReview != null)}");
        var sine = df?.AllChannels.FirstOrDefault(c => c.Name == "Sine");
        GB("time_column_implicit_x", sine != null && sine.XKind == XKind.Implicit && Math.Abs(sine.XIncrement - 0.001) < 1e-12 && sine.XUnit == "s", $"xkind={sine?.XKind} inc={sine?.XIncrement} unit={sine?.XUnit}");
        GB("file_props_format", df != null && df.Properties.GetValueOrDefault("delimiter")?.ToString() == "comma" && df.Properties.GetValueOrDefault("rows")?.ToString() == "1000", $"delimiter={df?.Properties.GetValueOrDefault("delimiter")} rows={df?.Properties.GetValueOrDefault("rows")}");
        await Shot(w, "csv_01_open");
        // ---- 2) group drop → chart with the 3 data curves on the time x; pan / zoom frames
        var types = Arr2(await JS(w, "return await nx.test.dragRowToView('.row-grp');"));
        await JS(w, "try { await nx.test.dragEnd(); } catch {} return true;");
        int chart = await ActiveChart(w); var m = e.Get(chart)!.Model;
        await Task.WhenAll(m.Curves.Select(c => e.State.EnsurePyramid(c.Channel))); e.TouchChart(chart); await JS(w, Settle);
        GB("group_drop_chart", m.Curves.Count == 3 && types.Contains("application/x-nexys-group") && m.Curves.All(c => c.Channel.XKind == XKind.Implicit), $"curves={m.Curves.Count} [{string.Join(",", m.Curves.Select(c => c.Channel.Name))}]");
        GB("square_is_step", m.Curves.Any(c => c.Channel.Name == "Square" && c.Step), string.Join(",", m.Curves.Select(c => c.Channel.Name + ":" + c.Step)));
        ReportFrames("pan_20", Arr(await JS(w, $"return await nx.test.pan({chart}, 20);")));
        ReportFrames("zoom_20", Arr(await JS(w, $"return await nx.test.zoomSteps({chart}, 20);")));
        await Shot(w, "csv_02_chart");
        // ---- 3) comma decimal + semicolon, tab + unit row + text column, ISO timestamps
        var sd = await e.OpenFileAsync(Path.Combine(samples, "semicolon_decimal_comma.csv"));
        var sp = sd?.AllChannels.FirstOrDefault(c => c.Name == "Spannung"); var one = new double[1]; sp?.Source.Read(0, one);
        GB("semicolon_comma_decimal", sd != null && sd.Properties.GetValueOrDefault("delimiter")?.ToString() == "semicolon" && sd.Properties.GetValueOrDefault("decimal")?.ToString() == "," && Math.Abs(one[0] - 3.0) < 1e-9, $"delimiter={sd?.Properties.GetValueOrDefault("delimiter")} decimal={sd?.Properties.GetValueOrDefault("decimal")} Spannung[0]={one[0]}");
        var td = await e.OpenFileAsync(Path.Combine(samples, "tab_units_row.csv"));
        var volt = td?.AllChannels.FirstOrDefault(c => c.Name == "Voltage"); var state = td?.AllChannels.FirstOrDefault(c => c.Name == "State");
        GB("tab_units_text_column", td != null && volt?.Unit == "V" && volt.XUnit == "ms" && state?.IsText == true && (state.Properties["text_values"] as string[])?.Length == 300, $"unit={volt?.Unit} xunit={volt?.XUnit} state text={state?.IsText}");
        var id = await e.OpenFileAsync(Path.Combine(samples, "iso_datetime.csv"));
        var temp = id?.AllChannels.FirstOrDefault(c => c.Name == "Temp");
        GB("iso_datetime_start_and_x", id != null && id.Properties.ContainsKey("start_time") && temp?.XKind == XKind.Implicit && Math.Abs(temp.XIncrement - 0.1) < 1e-9, $"start={id?.Properties.GetValueOrDefault("start_time")} xkind={temp?.XKind} inc={temp?.XIncrement}");
        await JS(w, Settle);
        int cards = (await JS(w, "await nx.test.mode('NAVIGATOR'); return document.querySelectorAll('.ds-card').length;")).GetInt32();
        int importBtns = (await JS(w, "return document.querySelectorAll('.ds-card [data-import]').length;")).GetInt32();
        GB("file_cards_import_button", cards == 4 && importBtns == 4, $"cards={cards} import buttons={importBtns}");
        foreach (var f in e.State.Files.ToList()) e.CloseFile(f.Name);
        await JS(w, "await nx.test.mode('VIEW'); return true;");
        // ---- 4) import dialog: 30 % non-numeric tokens → reasons, live preview on a setting change, OPEN
        string review = Path.Combine(samples, "review_bad_tokens.csv");
        var r0 = e.OpenFile(review); await JS(w, Settle);
        var dlg = await JS(w, "return nx.test.csvDialog();");
        GB("dialog_shown_with_reasons", r0 == null && e.CsvReview != null && dlg.ValueKind == JsonValueKind.Object && dlg.GetProperty("reasons").GetArrayLength() >= 1 && dlg.GetProperty("columns").GetInt32() == 3 && dlg.GetProperty("rows").GetInt32() == 20, dlg.ValueKind == JsonValueKind.Object ? $"columns={dlg.GetProperty("columns")} rows={dlg.GetProperty("rows")} bad={dlg.GetProperty("bad")} reasons={dlg.GetProperty("reasons")}" : "no dialog");
        int bad0 = dlg.ValueKind == JsonValueKind.Object ? dlg.GetProperty("bad").GetInt32() : -1;
        await Shot(w, "csv_03_dialog");
        var d2 = await JS(w, "return await nx.test.csvSet('decimal', ',');");
        int bad1 = d2.ValueKind == JsonValueKind.Object ? d2.GetProperty("bad").GetInt32() : -1;
        // decimal ',' with the ',' delimiter: "1.234" no longer parses → the numeric columns turn into text columns (no bad-cell marks)
        string notes1 = d2.ValueKind == JsonValueKind.Object ? d2.GetProperty("notes").GetString() ?? "" : "";
        GB("dialog_live_preview", bad1 != bad0 && d2.GetProperty("decimal").GetString() == "," && notes1.Contains("텍스트 2"), $"bad cells {bad0} → {bad1} after decimal ','; notes: {notes1[..Math.Min(80, notes1.Length)]}");
        var d3 = await JS(w, "return await nx.test.csvSet('decimal', '.');");
        GB("dialog_setting_back", d3.ValueKind == JsonValueKind.Object && d3.GetProperty("bad").GetInt32() == bad0, $"bad={d3.GetProperty("bad")}");
        GB("dialog_no_sidecar_in_automation", d3.ValueKind == JsonValueKind.Object && !d3.GetProperty("remember").GetBoolean() && !File.Exists(review + ".nvimport.json"), $"remember box={d3.GetProperty("remember")}");
        await JS(w, "await nx.test.modalButton('ok'); return true;"); await e.CsvIdle(); await JS(w, Settle);
        var rf = e.State.Files.FirstOrDefault(f => f.Name == "review_bad_tokens.csv"); var val = rf?.AllChannels.FirstOrDefault(c => c.Name == "value");
        GB("dialog_open_result", rf != null && e.CsvReview == null && rf.ChannelCount == 3 && val != null && val.Properties.GetValueOrDefault("non_numeric_tokens")?.ToString() == "60" && val.XKind == XKind.Implicit, $"file={rf?.Name} channels={rf?.ChannelCount} bad={val?.Properties.GetValueOrDefault("non_numeric_tokens")} xkind={val?.XKind}");
        // ---- 5) IMPORT… on the file card: dialog for the open file, time column → none, REOPEN
        await JS(w, "await nx.test.mode('NAVIGATOR'); return true;");
        await JS(w, "return await nx.test.clickSel('.ds-card [data-import]');");
        var d4 = await JS(w, "return nx.test.csvDialog();");
        GB("reopen_dialog", d4.ValueKind == JsonValueKind.Object && d4.GetProperty("title").GetString() == "IMPORT SETTINGS" && d4.GetProperty("timeColumn").GetInt32() == 0, d4.ValueKind == JsonValueKind.Object ? $"title={d4.GetProperty("title")} time={d4.GetProperty("timeColumn")}" : "no dialog");
        await JS(w, "return await nx.test.csvSet('timeColumn', '-1');");
        await JS(w, "await nx.test.modalButton('ok'); return true;"); await e.CsvIdle(); await JS(w, Settle);
        rf = e.State.Files.FirstOrDefault(f => f.Name == "review_bad_tokens.csv"); val = rf?.AllChannels.FirstOrDefault(c => c.Name == "value");
        GB("reopen_with_new_settings", rf != null && val != null && val.XKind == XKind.Index && e.State.Files.Count == 1, $"xkind={val?.XKind} files={e.State.Files.Count}");
        GB("no_review_left", e.CsvReview == null && !File.Exists(review + ".nvimport.json"), "");
        e.CloseFile("review_bad_tokens.csv"); await JS(w, "await nx.test.mode('VIEW'); return true;");
        // ---- 6) perf corpus
        string big = Path.Combine(root, "work", "csv", "big");
        foreach (var (name, tag, gateFirst) in new[] { ("mid_100m.csv", "100m", "<=1000"), ("big_1g.csv", "1g", "<=5000") })
        {
            string p = Path.Combine(big, name); if (!File.Exists(p)) continue;
            string cacheDir = Core.Pyramid.PyramidService.CacheDirFor(p, Core.Csv.CsvDataSource.CacheRootOverride); try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
            sw.Restart(); var bf = await e.OpenFileAsync(p); await JS(w, Settle); G($"big_{tag}_first_open_ms", sw.Elapsed.TotalMilliseconds, "ms", gateFirst);
            GB($"big_{tag}_parsed", bf != null && bf.Properties.GetValueOrDefault("source")?.ToString()?.StartsWith("parsed") == true, $"{bf?.Properties.GetValueOrDefault("source")} rows={bf?.Properties.GetValueOrDefault("rows")}");
            if (bf == null) continue;
            sw.Restart();
            var ov = e.DropAll(bf.AllChannels.Where(c => !c.IsText && c.XKind != XKind.Index).Take(10).Select(c => c.Id)); await Task.WhenAll(ov.Model.Curves.Select(c => e.State.EnsurePyramid(c.Channel)));
            e.TouchChart(ov.Id); await JS(w, Settle);
            G($"big_{tag}_first_screen_10ch_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=2000");
            ReportFrames($"big_{tag}_zoom_20", Arr(await JS(w, $"return await nx.test.zoomSteps({ov.Id}, 20);")));
            ReportFrames($"big_{tag}_pan_20", Arr(await JS(w, $"return await nx.test.pan({ov.Id}, 20);")));
            G($"big_{tag}_memory_ws_mb", Process.GetCurrentProcess().WorkingSet64 / 1048576.0, "MB");
            if (tag == "1g") await Shot(w, "csv_04_big_pan");
            e.CloseFile(bf.Name); await JS(w, Settle);
            // the cache is written in the background after the parse: wait for it (bounded), then the reopen must come from it
            string cp = Core.Csv.CsvCache.PathFor(p, e.CsvFormatOf(p) ?? Core.Csv.CsvSniffer.Sniff(p).Format, Core.Csv.CsvDataSource.CacheRootOverride);
            for (int i = 0; i < 300 && !File.Exists(cp); i++) await Task.Delay(100);
            await Task.Delay(200);
            foreach (var c in e.Charts.ToList()) e.RemoveChart(c.Id, false);
            sw.Restart(); var cf = await e.OpenFileAsync(p); await JS(w, Settle); G($"big_{tag}_cached_open_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=300");
            GB($"big_{tag}_from_cache", cf != null && cf.Properties.GetValueOrDefault("source")?.ToString() == "cache", $"{cf?.Properties.GetValueOrDefault("source")}");
            if (cf != null) e.CloseFile(cf.Name);
        }
        string wide = Path.Combine(big, "wide_2000col.csv");
        if (File.Exists(wide))
        {
            string cacheDir = Core.Pyramid.PyramidService.CacheDirFor(wide, Core.Csv.CsvDataSource.CacheRootOverride); try { if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); } catch { }
            sw.Restart(); var wf = await e.OpenFileAsync(wide); await JS(w, Settle); G("wide_2000col_open_and_navigator_ms", sw.Elapsed.TotalMilliseconds, "ms", "<=1000");
            GB("wide_2000col_count", wf != null && wf.ChannelCount == 2000 && wf.AllChannels.First().Length == 5000, $"channels={wf?.ChannelCount} rows={wf?.AllChannels.First().Length}");
            if (wf != null) e.CloseFile(wf.Name);
        }
        try { File.WriteAllLines(Path.Combine(root, "work", "bench", "gate_csv.txt"), Gate); } catch { }
    }
}
