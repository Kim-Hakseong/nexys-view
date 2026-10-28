using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using NexysView.App.Services;
using NexysView.Core.Mil1553;

namespace NexysView.App;

/// <summary>--v0100: gates for 0.10.0 — MIL-STD-1553 capture (virtual 1M-record file): open time, mode switch, list scroll frame times,
/// select + decode, filter time, ICD module toggle (settings) + ICD attach + EU decode + word trend, project export (template or
/// fallback), 50× open/close leak, accent-border scan, regression of the TDMS self-test path (sample file still opens and plots).</summary>
public static partial class SelfTest
{
    static partial void V0100Impl(MainWindow w, ref Task task) => task = V0100(w);

    static async Task V0100(MainWindow w)
    {
        var e = w.Engine; var sw = Stopwatch.StartNew();
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.IcdExport = false; e.Touch();
        string dir = Path.Combine(OutDir, "data"); Directory.CreateDirectory(dir);
        string mon1m = Path.Combine(dir, "virtual_1553_1M.mon"), mon10k = Path.Combine(dir, "virtual_1553_10k.mon");
        if (!File.Exists(mon1m)) { var t = Stopwatch.StartNew(); Mil1553File.WriteVirtual(mon1m, 1_000_000, 1); L($"wrote 1M virtual capture in {t.ElapsedMilliseconds} ms ({new FileInfo(mon1m).Length} bytes)"); }
        if (!File.Exists(mon10k)) Mil1553File.WriteVirtual(mon10k, 10_000, 2);
        // ---- 0) regression: the built-in TDMS sample still opens and plots
        string sample = SampleData.Ensure() ?? ""; var dfs = e.OpenFile(sample)!; await JS(w, "await nx.test.settle(); return true;");
        var sine = dfs.AllChannels.First(c => c.Name == "Sine 1 Hz");
        await JS(w, $"await nx.test.drop([{{id:{J(sine.Id)},name:'s'}}]); return true;");
        GB("regression_tdms_chart", e.Charts.Any(c => c.Model.Curves.Count > 0), $"charts={e.Charts.Count}");
        // ---- 1) open 1M records
        var t0 = Stopwatch.StartNew(); var df = e.OpenFile(mon1m)!; double openMs = t0.Elapsed.TotalMilliseconds;
        var cap = Mil1553DataSource.FileOf(df)!;
        G("m1553_open_1M_ms", openMs, "ms", "<=1000"); G("m1553_records", cap.Count, "", "=1000000");
        GB("m1553_format_chip", df.Format == "MIL1553" && df.Groups[0].Channels.Count == 43, $"format={df.Format} channels={df.Groups[0].Channels.Count} (time + 10 columns + 32 words)");
        // ---- 1b) bit-level truth: a capture written by the Python reference (poc/mil1553_capture.py) re-encodes byte-identically
        string truth = Arg("--truth");
        if (truth.Length > 0 && File.Exists(truth))
        {
            using var pf = new Mil1553File(truth, mapped: false);
            var recs = new List<(ulong, ushort, ushort, ushort, ushort, ushort, ushort, ushort[], ushort, ushort)>(pf.Count);
            var dd = new ushort[40];
            for (int i = 0; i < pf.Count; i++) { var rr = pf.Get(i); int nn = pf.Data(i, dd); recs.Add((rr.TimeUs, rr.Activity, rr.Error, rr.Cwd1, rr.Cwd2, rr.Swd1, rr.Swd2, dd.Take(nn).ToArray(), rr.RespTime1, rr.RespTime2)); }
            string rewrite = Path.Combine(dir, "truth_rewrite.mon"); Mil1553File.Write(rewrite, recs, pf.Metadata);
            bool same = File.ReadAllBytes(truth).AsSpan().SequenceEqual(File.ReadAllBytes(rewrite));
            var r0 = pf.Get(0); int n0 = pf.Data(0, dd);
            GB("m1553_python_truth_bitwise", same && pf.Count == 1000 && r0.Cwd1 == 0x0824 && r0.TimeUs == 133143986176UL && n0 == 4 && dd[1] == 13 && r0.RespTime1 == 59, $"records={pf.Count} rewrite identical={same} rec0 cwd1=0x{r0.Cwd1:X4} t={r0.TimeUs} n={n0} d1={dd[1]} resp1={r0.RespTime1}");
        }
        await JS(w, "await nx.test.settle(); return true;");
        string modes = (await JS(w, "return Array.from(document.querySelectorAll('.tools-modes .tool.mode')).map(b => b.textContent.trim()).join(',');")).ToString();
        GB("m1553_mode_button", modes.Contains("MIL1553"), modes);
        await JS(w, "await nx.test.mode('MIL1553'); return true;");
        await Idle(w, 300);
        int total = (await JS(w, "return nx.test.m1553().total();")).GetInt32();
        GB("m1553_list_total", total == 1_000_000, $"total={total}");
        int vis = (await JS(w, "return await nx.test.m1553().scrollTo(500000);")).GetInt32();
        GB("m1553_list_virtualized", vis > 5 && vis < 80, $"visible rows={vis}");
        await Shot(w, "v0100_01_list_light");
        // ---- 2) scroll frame times
        var steps = JsonSerializer.Deserialize<double[][]>((await JS(w, "return JSON.stringify(await nx.test.m1553().scrollSteps(30));")).GetString()!)!;
        G("m1553_scroll_paint_avg_ms", steps.Average(s => s[0]), "ms", "<=8"); G("m1553_scroll_paint_max_ms", steps.Max(s => s[0]), "ms", "<=30");
        G("m1553_scroll_frame_avg_ms", steps.Average(s => s[1]), "ms", "<=34");
        // ---- 3) select + decode (no ICD)
        var visIds = JsonSerializer.Deserialize<int[]>((await JS(w, "return JSON.stringify(nx.test.m1553().visible());")).GetString()!)!;
        int firstRow = (await JS(w, "const r = document.querySelector('.m1553-scroll .trow[data-i]'); return r ? +r.dataset.row : -1;")).GetInt32();
        int kv = (await JS(w, $"return await nx.test.m1553().selectRow({firstRow});")).GetInt32();
        GB("m1553_decode_panel", kv >= 7, $"kv fields={kv} row={firstRow} i={visIds.FirstOrDefault()}");
        // ---- 4) filter
        var fr = await JS(w, "return JSON.stringify(await nx.test.m1553().filter({rt: 3}));");
        using (var jd = JsonDocument.Parse(fr.GetString()!)) { var o = jd.RootElement; G("m1553_filter_rt3_ms", o.GetProperty("ms").GetDouble(), "ms", "<=100"); G("m1553_filter_rt3_engine_ms", o.GetProperty("engineMs").GetDouble(), "ms"); G("m1553_filter_rt3_rows", o.GetProperty("n").GetInt32(), "", "=100000"); }
        var fe = await JS(w, "return JSON.stringify(await nx.test.m1553().filter({rt: -1, errorsOnly: true}));");
        using (var jd = JsonDocument.Parse(fe.GetString()!)) { var o = jd.RootElement; int n = o.GetProperty("n").GetInt32(); G("m1553_filter_errors_ms", o.GetProperty("ms").GetDouble(), "ms", "<=100"); GB("m1553_filter_errors_rows", n > 8000 && n < 12000, $"rows={n} (~1% of 1M)"); }
        await JS(w, "return JSON.stringify(await nx.test.m1553().filter({rt: -1, errorsOnly: false}));");
        // ---- 5) settings toggle → ICD module tools
        bool toggled = (await JS(w, "return await nx.test.settings(true);")).GetBoolean();
        GB("icd_module_toggle", toggled && e.IcdExport, $"features.icdExport={e.IcdExport}");
        await Shot(w, "v0100_02_settings_module_on");
        // ---- 6) ICD attach (built-in dummy) + EU decode + trend
        string hubCsv = Path.Combine(dir, "dummy_icd.csv"); File.WriteAllText(hubCsv, IcdHub.Dummy().ToCsv());
        var ir = e.Mil1553.LoadIcd(hubCsv); await JS(w, "await nx.test.settle(); return true;");
        int euGroups = df.Groups.Count(g => g.Name.StartsWith("ICD:"));
        GB("icd_attach_groups", euGroups == 10, $"EU groups={euGroups} result={JsonSerializer.Serialize(ir)}");
        int firstRow2 = (await JS(w, "await nx.test.m1553().scrollTo(0); const r = document.querySelector('.m1553-scroll .trow[data-i]'); return r ? +r.dataset.row : -1;")).GetInt32();
        await JS(w, $"return await nx.test.m1553().selectRow({firstRow2});");
        string words = (await JS(w, "return JSON.stringify(nx.test.m1553().words());")).GetString()!;
        GB("icd_decode_words", words.Contains("\"eu\"") && JsonDocument.Parse(words).RootElement.GetArrayLength() == 4, words.Length > 300 ? words[..300] : words);
        // verify an EU value against the generator: NAV_POS_W1 = 32-bit BNR of words 1-2 × 0.5 of record 0 (data[j] = (k*7 + j*13) & 0xffff)
        var d = new ushort[40]; cap.Data(0, d);
        long raw = (long)(d[0] | ((uint)d[1] << 16)); if ((raw & 0x80000000L) != 0) raw -= 1L << 32;
        double euExpected = raw * 0.5; double euGot = IcdHub.Dummy().Messages[0].Words[0].Decode(cap, 0);
        GB("icd_eu_value", Math.Abs(euExpected - euGot) < 1e-9, $"expected={euExpected} got={euGot}");
        var tj = await JS(w, "return await nx.test.m1553().trendFirst();");
        int chart = tj.ValueKind == JsonValueKind.Number ? tj.GetInt32() : -1;
        await Idle(w, 400);
        var tc = e.Charts.FirstOrDefault(c => c.Id == chart);
        GB("icd_trend_chart", tc != null && tc.PageId == 0 && tc.Model.Curves.Count == 1, $"chart={chart} page={tc?.PageId} curves={tc?.Model.Curves.Count}");
        await Shot(w, "v0100_03_decode_trend_light");
        e.Theme = "dark"; e.Touch(); await JS(w, "await nx.test.settle(); return true;"); await Shot(w, "v0100_04_decode_trend_dark"); e.Theme = "light"; e.Touch(); await JS(w, "await nx.test.settle(); return true;");
        // ---- 7) export (template if installed, otherwise fallback files)
        string outDir = Path.Combine(OutDir, "export", "IcdProject"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        object? ex = null; string exErr = ""; string tdir = Arg("--template");   // template project folder (user-supplied; not part of the repository)
        bool tmpl = tdir.Length > 0 && Directory.Exists(tdir);
        try { ex = e.Mil1553.Export(outDir, tmpl ? tdir : null, true); } catch (Exception exc) { exErr = exc.ToString(); L("export failed: " + exErr); }
        string exs = ex != null ? JsonSerializer.Serialize(ex) : "ERROR " + exErr.Split('\n')[0];
        GB("icd_export_files", File.Exists(Path.Combine(outDir, "icd_hub.json")) && Directory.EnumerateFiles(outDir, "*_automation.py").Any() && (!tmpl || Directory.EnumerateFiles(outDir, "*.CDV").Any()), $"template={tmpl} {exs}");
        if (tmpl && Directory.EnumerateFiles(outDir, "*.CDV").Any())
        {
            var cf = CompoundFile.Read(File.ReadAllBytes(Directory.EnumerateFiles(outDir, "*.CDV").First()));
            int msgs = cf.Storages.Count(kv => new Guid(kv.Value).ToString() == "1553014a-7f30-4c82-ac9b-043151722f6b"), fields = cf.Storages.Count(kv => new Guid(kv.Value).ToString() == "15530653-9400-488c-af27-8e2092c854f3");
            GB("icd_export_cdv_nodes", msgs == 10 && fields == 80, $"bcmsg={msgs} fields={fields} (10 BC + 10 SA × 4 words)");
        }
        // ---- 8) accent borders in the 1553 mode
        var scan = await JS(w, "return JSON.stringify(nx.test.accentScan());");
        using (var jd = JsonDocument.Parse(scan.GetString()!)) { int hits = jd.RootElement.GetProperty("hits").GetArrayLength(); G("accent_borders_zero", hits, "", "=0"); }
        // ---- 9) 50× open/close leak (10k file)
        double ws0 = WsMb(); var tl = Stopwatch.StartNew(); int exceptions = 0;
        for (int i = 0; i < 50; i++) { try { var f = e.OpenFile(mon10k)!; e.CloseFile(f.Name); } catch (Exception exc) { exceptions++; L("open/close " + exc.Message); } }
        await JS(w, "await nx.test.settle(); return true;");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(w, 200);
        G("m1553_openclose_50_s", tl.Elapsed.TotalSeconds, "s"); G("m1553_openclose_exceptions", exceptions, "", "=0"); G("m1553_openclose_mem_growth_mb", WsMb() - ws0, "MB", "<=100");
        G("m1553_openclose_files_left", e.State.Files.Count(f => f.Format == "MIL1553" && f.Path == mon10k), "", "=0");
        // ---- 10) back to VIEW: the trend chart (page 0) must not appear in the page grid
        await JS(w, "await nx.test.mode('VIEW'); return true;");
        int gridCharts = (await JS(w, "return document.querySelectorAll('.charts .chart').length;")).GetInt32();
        GB("m1553_chart_hidden_in_view", !e.Charts.Where(c => c.PageId == 0).Any(c => gridCharts > e.Charts.Count(x => x.PageId == e.ActivePage)), $"grid={gridCharts} page0={e.Charts.Count(c => c.PageId == 0)}");
        G("v0100_total_s", sw.Elapsed.TotalSeconds, "s");
    }
}
