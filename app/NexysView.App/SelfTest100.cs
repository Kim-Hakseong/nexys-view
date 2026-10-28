using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.App.Services;
using NexysView.Core.Mil1553;

namespace NexysView.App;

/// <summary>--v100rc: 1.0 release-candidate gates on the BUS mode + ICD module + migrated .dxd reader (all through the real web UI):
/// empty state → built-in 1553 sample → sort / keyboard / time units / filters / presets / text → stats → CSV exports → ICD import
/// (CSV mapping, optional XLSX) → edit + validation → export (project / message XML / device XML / api) with XSD + round trips →
/// ARINC 429 sample (open, decode, filter, stats) → .dxd open → settings / help / about dialogs → accent scan, memory, timing.
/// Captures are numbered v100_01 … v100_14 for docs/screens.</summary>
public static partial class SelfTest
{
    static partial void V100Impl(MainWindow w, ref Task task) => task = V100(w);

    static async Task V100(MainWindow w)
    {
        var e = w.Engine; var sw = Stopwatch.StartNew();
        e.Theme = "light"; e.Plot = "dark"; e.ShowLegacyTabs = false; e.IcdExport = false; e.BusTime = "rel"; e.Mil1553.Presets.Clear(); e.Touch();
        string dir = Path.Combine(OutDir, "data"); Directory.CreateDirectory(dir);
        G("memory_ws_start_mb", WsMb(), "MB");
        G("startup_process_to_ui_ms", (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds - 600, "ms", "<=3000");   // process start → page ready (minus the 2×300 ms self-test settle waits)
        // ---- 1) empty state (no bus file open): BUS mode shows the empty card with OPEN / 1553 SAMPLE / 429 SAMPLE
        await JS(w, "await nx.test.mode('MIL1553'); return true;"); await Idle(w, 200);
        bool empty = (await JS(w, "return nx.test.m1553().empty();")).GetBoolean();
        int emptyBtns = (await JS(w, "return document.querySelectorAll('.bus-empty [data-a]').length;")).GetInt32();
        GB("bus_empty_state", empty && emptyBtns == 3, $"empty={empty} buttons={emptyBtns}");
        await Shot(w, "v100_01_empty");
        // ---- 2) built-in 1553 sample (20,000 records) through the empty-state button
        var t0 = Stopwatch.StartNew();
        await JS(w, "await nx.test.clickSel('.bus-empty [data-a=sample1553]'); return true;"); await Idle(w, 300);
        int total = (await JS(w, "return nx.test.m1553().total();")).GetInt32(); string proto = (await JS(w, "return nx.test.m1553().protocol();")).GetString() ?? "";
        G("bus_sample_open_ms", t0.Elapsed.TotalMilliseconds, "ms", "<=1500");
        GB("bus_sample_1553", total == 20_000 && proto == "1553" && !(await JS(w, "return nx.test.m1553().empty();")).GetBoolean(), $"total={total} protocol={proto}");
        string modes = (await JS(w, "return Array.from(document.querySelectorAll('.tools-modes .tool.mode')).map(b => b.textContent.trim()).join(',');")).ToString();
        GB("bus_mode_button_label", modes.Contains("BUS") && !modes.Contains("MIL1553"), modes);
        await Shot(w, "v100_02_list");
        // ---- 3) sort by RT descending (header click path = m1553.sort) → first 50 rows monotone; back to capture order
        int visAfterSort = (await JS(w, "return await nx.test.m1553().sort('rt', -1);")).GetInt32();
        var rowsJson = JsonSerializer.Serialize(e.Mil1553.Rows(0, 50)); int[] rts;
        using (var jd = JsonDocument.Parse(rowsJson)) rts = jd.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("rt").GetInt32()).ToArray();
        bool mono = rts.Length == 50 && rts.Zip(rts.Skip(1)).All(p => p.First >= p.Second);
        string hdr = (await JS(w, "return document.querySelector('.m1553-scroll .thead span.on')?.textContent ?? '';")).GetString() ?? "";
        GB("bus_sort_rt_desc", mono && hdr.StartsWith("RT") && visAfterSort > 5, $"first rts={string.Join(',', rts.Take(8))} header='{hdr}'");
        await JS(w, "return await nx.test.m1553().sort('', 1);");
        // ---- 4) keyboard navigation on the list (ArrowDown ×3, PageDown, End, Home)
        await JS(w, "await nx.test.m1553().scrollTo(0); const r = document.querySelector('.m1553-scroll .trow[data-i]'); await nx.test.m1553().selectRow(+r.dataset.row); return true;");
        int r1 = (await JS(w, "await nx.test.m1553().key('ArrowDown'); await nx.test.m1553().key('ArrowDown'); return await nx.test.m1553().key('ArrowDown');")).GetInt32();
        int r2 = (await JS(w, "return await nx.test.m1553().key('PageDown');")).GetInt32();
        int r3 = (await JS(w, "return await nx.test.m1553().key('End');")).GetInt32();
        int r4 = (await JS(w, "return await nx.test.m1553().key('Home');")).GetInt32();
        GB("bus_keyboard_nav", r1 == 3 && r2 > r1 && r3 == total - 1 && r4 == 0, $"down3={r1} pgdn={r2} end={r3} home={r4}");
        // ---- 5) time units: REL s / ABS µs / Δt ms (toolbar pills → m1553.busTime → header + cells)
        await JS(w, "await nx.test.m1553().clickFilter('[data-time=abs]'); await nx.test.settle(); return true;"); await Idle(w, 150);
        string hAbs = (await JS(w, "return document.querySelector('.m1553-scroll .thead').textContent;")).GetString() ?? "";
        string cAbs = (await JS(w, "return document.querySelector('.m1553-scroll .trow[data-i] span:nth-child(2)')?.textContent ?? '';")).GetString() ?? "";
        await JS(w, "await nx.test.m1553().clickFilter('[data-time=delta]'); await nx.test.settle(); return true;"); await Idle(w, 150);
        string hDel = (await JS(w, "return document.querySelector('.m1553-scroll .thead').textContent;")).GetString() ?? "";
        await JS(w, "await nx.test.m1553().clickFilter('[data-time=rel]'); await nx.test.settle(); return true;"); await Idle(w, 150);
        string hRel = (await JS(w, "return document.querySelector('.m1553-scroll .thead').textContent;")).GetString() ?? "";
        GB("bus_time_units", hAbs.Contains("TIME (µs)") && hDel.Contains("Δt (ms)") && hRel.Contains("TIME (s)") && e.BusTime == "rel" && cAbs.Replace(",", "").All(char.IsDigit) && cAbs.Length > 6, $"abs='{cAbs}' heads: {hAbs[..Math.Min(40, hAbs.Length)]} | {hDel[..Math.Min(30, hDel.Length)]}");
        // ---- 6) filters: RT input, T/R pill, bus pill, ERRORS ONLY, TEXT; presets save / apply / delete
        int nRt3 = (await JS(w, "return await nx.test.m1553().setInput('rt', '3');")).GetInt32();
        int nTr = (await JS(w, "await nx.test.m1553().clickFilter('[data-tr=\"1\"]'); return nx.test.m1553().total();")).GetInt32();
        int nBusB = (await JS(w, "await nx.test.m1553().clickFilter('[data-bus=\"1\"]'); return nx.test.m1553().total();")).GetInt32();
        int nErr = (await JS(w, "await nx.test.m1553().clickFilter('[data-a=err]'); return nx.test.m1553().total();")).GetInt32();
        GB("bus_filter_combo", nRt3 > 0 && nRt3 < total && nTr <= nRt3 && nBusB <= nTr && nErr <= nBusB, $"rt3={nRt3} +rt→bc={nTr} +busB={nBusB} +errors={nErr} of {total}");
        await Shot(w, "v100_03_filters");
        var fr = await JS(w, "return JSON.stringify(await nx.test.m1553().filter({rt: -1, sa: -1, tr: -1, bus: -1, errorsOnly: false, text: ''}));");
        int nAll; using (var jd = JsonDocument.Parse(fr.GetString()!)) { nAll = jd.RootElement.GetProperty("n").GetInt32(); G("bus_filter_reset_ms", jd.RootElement.GetProperty("ms").GetDouble(), "ms", "<=100"); }
        int nText = (await JS(w, "return await nx.test.m1553().setInput('text', 'RT3');")).GetInt32();
        int nHex = (await JS(w, "return await nx.test.m1553().setInput('text', '0001');")).GetInt32();
        GB("bus_filter_text", nAll == total && nText == nRt3 && nHex > 0 && nHex < total, $"reset={nAll} 'RT3'={nText} (rt filter gave {nRt3}) hex '0001'={nHex}");
        await JS(w, "return await nx.test.m1553().setInput('text', '');");
        await JS(w, "await nx.call('m1553.preset.save', {name: 'RT3 errors', rt: 3, sa: -1, tr: -1, bus: -1, errorsOnly: true, text: ''}); await nx.test.settle(); return true;"); await Idle(w, 150);
        int presetOpts = (await JS(w, "return document.querySelectorAll('.m1553-filters [data-f=preset] option').length;")).GetInt32();
        int nPreset = (await JS(w, "const s = document.querySelector('.m1553-filters [data-f=preset]'); s.value = 'RT3 errors'; s.dispatchEvent(new Event('change', {bubbles: true})); await new Promise(r => setTimeout(r, 250)); return nx.test.m1553().total();")).GetInt32();
        await JS(w, "await nx.test.m1553().clickFilter('[data-a=presetDel]'); await nx.test.settle(); return true;"); await Idle(w, 150);
        int presetOpts2 = (await JS(w, "return document.querySelectorAll('.m1553-filters [data-f=preset] option').length;")).GetInt32();
        GB("bus_presets", presetOpts == 2 && nPreset > 0 && nPreset < nRt3 && presetOpts2 == 1 && e.Mil1553.Presets.Count == 0, $"options={presetOpts}→{presetOpts2} applied rows={nPreset}");
        await JS(w, "return JSON.stringify(await nx.test.m1553().filter({rt: -1, errorsOnly: false}));");
        // ---- 7) select + decode (raw hex/bin/dec) and statistics card
        int firstRow = (await JS(w, "await nx.test.m1553().scrollTo(0); const r = document.querySelector('.m1553-scroll .trow[data-i]'); return +r.dataset.row;")).GetInt32();
        int kv = (await JS(w, $"return await nx.test.m1553().selectRow({firstRow});")).GetInt32();
        int rawRows = (await JS(w, "return document.querySelectorAll('.m1553-words.raw tbody tr').length;")).GetInt32();
        GB("bus_decode_raw", kv >= 7 && rawRows >= 1, $"kv={kv} raw word rows={rawRows}");
        int bars = (await JS(w, "return await nx.test.m1553().stats();")).GetInt32(); await Idle(w, 200);
        var st = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.Stats())).RootElement;
        GB("bus_stats", bars > 0 && st.GetProperty("count").GetInt32() == total && st.GetProperty("perRt").GetArrayLength() > 0 && st.GetProperty("busLoadPct").GetDouble() > 0, $"bars={bars} count={st.GetProperty("count")} rts={st.GetProperty("perRt").GetArrayLength()} load={st.GetProperty("busLoadPct").GetDouble():F1}% errors={st.GetProperty("errors")}");
        await Shot(w, "v100_04_decode_stats");
        await JS(w, "await nx.test.m1553().clickFilter('[data-a=stats]'); return true;");
        // ---- 8) CSV exports (rows / decoded with EU) through the service path the dialog uses
        string csvRows = Path.Combine(dir, "rows.csv"), csvDec = Path.Combine(dir, "decoded.csv");
        int nr = e.Mil1553.ExportRowsCsv(csvRows, 0, 999); int nd = e.Mil1553.ExportDecodedCsv(csvDec, 0, 99);
        int lr = File.ReadAllLines(csvRows).Length, ld = File.ReadAllLines(csvDec).Length;
        GB("bus_csv_exports", nr == 1000 && lr == 1001 && nd >= 100 && ld == nd + 1, $"rows={nr}/{lr} lines, decoded (no ICD → raw words)={nd}/{ld} lines");
        // ---- 9) ICD module: settings toggle → import CSV with column mapping → edit → validate
        bool toggled = (await JS(w, "return await nx.test.settings(true);")).GetBoolean();
        GB("icd_module_toggle", toggled && e.IcdExport, $"features.icdExport={e.IcdExport}");
        string hubCsv = Path.Combine(dir, "dummy_icd.csv"); File.WriteAllText(hubCsv, IcdHub.Dummy().ToCsv());
        await JS(w, "await nx.test.icd().open('import'); return true;"); await Idle(w, 150);
        await JS(w, $"await nx.test.icd().pick({J(hubCsv)}); return true;"); await Idle(w, 200);
        var map = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.icd().mapping());")).GetString()!).RootElement;
        int mapped = map.EnumerateObject().Count(p => p.Value.GetInt32() >= 0);
        await Shot(w, "v100_05_icd_import");
        var ir = JsonDocument.Parse((await JS(w, "return JSON.stringify(await nx.test.icd().importGo());")).GetString()!).RootElement;
        int msgs = (await JS(w, "return nx.test.icd().messages();")).GetInt32();
        GB("icd_import_csv_mapping", mapped == 15 && msgs == 10 && ir.GetProperty("words").GetInt32() == 40 && ir.GetProperty("errors").GetArrayLength() == 0, $"auto-mapped columns={mapped}/15 messages={msgs} words={ir.GetProperty("words")} errors={ir.GetProperty("errors").GetArrayLength()}");
        string xlsx = Arg("--xlsx");
        if (xlsx.Length > 0 && File.Exists(xlsx))
        {
            var xr = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.LoadIcd(xlsx))).RootElement; var hx = e.Mil1553.Hub!; var hc = IcdHub.FromRows(IcdHub.ReadCsvRows(hubCsv), null);
            GB("icd_import_xlsx", xr.GetProperty("messages").GetInt32() == 10 && IcdHub.SameShape(hx, hc), $"xlsx messages={xr.GetProperty("messages")} words={xr.GetProperty("words")} same shape as csv={IcdHub.SameShape(hx, hc)}");
            e.Mil1553.LoadIcd(hubCsv);
        }
        else L("icd_import_xlsx skipped (pass --xlsx <file>)");
        // edit: add a message (duplicate RT/SA → warning), widen a word (bit overlap → error), then delete it again
        await JS(w, "await nx.test.icd().open('edit'); return true;"); await Idle(w, 150);
        // the edited message is message 11 (index 10); the table shows the selected message's words, so select it after adding
        var h1 = JsonDocument.Parse((await JS(w, "return JSON.stringify(await nx.test.icd().edit({op: 'addMessage'}));")).GetString()!).RootElement;
        await JS(w, "await nx.test.icd().edit({op: 'setMessage', m: 10, field: 'rt', value: 1}); await nx.test.icd().edit({op: 'setMessage', m: 10, field: 'sa', value: 1}); await nx.test.icd().edit({op: 'setMessage', m: 10, field: 'tr', value: 0}); return true;");
        var errs1 = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.icd().errors());")).GetString()!).RootElement.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var hub0 = e.Mil1553.Hub!; var dup = hub0.Messages.Take(10).Any(m => m.Rt == 1 && m.Sa == 1 && m.Tr == 0);
        await JS(w, "await nx.test.icd().edit({op: 'addWord', m: 10}); await nx.test.icd().edit({op: 'addWord', m: 10}); await nx.test.icd().edit({op: 'setWord', m: 10, w: 0, field: 'bit_len', value: 32}); return true;");
        var errs2 = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.icd().errors());")).GetString()!).RootElement.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        int cellRows = (await JS(w, "await nx.test.clickSel('.icd-tbl.edit tr[data-m=\"10\"] td'); return document.querySelectorAll('.icd-tbl.edit tr[data-w]').length;")).GetInt32();
        string errsText = string.Join(" | ", errs2.Take(3));
        await Shot(w, "v100_06_icd_edit");
        var h2 = JsonDocument.Parse((await JS(w, "return JSON.stringify(await nx.test.icd().edit({op: 'deleteMessage', m: 10}));")).GetString()!).RootElement;
        var errs3 = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.icd().errors());")).GetString()!).RootElement.GetArrayLength();
        GB("icd_edit_validate", h1.GetProperty("messages").GetArrayLength() == 11 && (!dup || errs1.Any(x => x.Contains("duplicate"))) && errs2.Any(x => x.StartsWith("error") && x.Contains("overlap")) && cellRows == 2 && h2.GetProperty("messages").GetArrayLength() == 10 && errs3 == 0,
            $"add→{h1.GetProperty("messages").GetArrayLength()} dupWarn={errs1.Count(x => x.Contains("duplicate"))} overlapErr={errs2.Count(x => x.Contains("overlap"))} wordRows={cellRows} delete→{h2.GetProperty("messages").GetArrayLength()} errors after={errs3} [{errsText}]");
        // cell edit through the DOM input (change event → icd.edit setWord)
        string n0 = hub0.Messages[0].Words[0].Name;
        await JS(w, "await nx.test.clickSel('.icd-tbl.edit tr[data-m=\"0\"] td'); await nx.test.icd().setCell('.icd-tbl.edit tr[data-w=\"0\"] [data-f=name]', 'RENAMED_W1'); return true;");
        GB("icd_edit_cell", e.Mil1553.Hub!.Messages[0].Words[0].Name == "RENAMED_W1", $"'{n0}' → '{e.Mil1553.Hub!.Messages[0].Words[0].Name}'");
        await JS(w, $"await nx.test.icd().setCell('.icd-tbl.edit tr[data-w=\"0\"] [data-f=name]', {J(n0)}); return true;");
        // a hub with an out-of-range RT (only reachable from a file) must block export
        string badCsv = Path.Combine(dir, "bad_icd.csv"); File.WriteAllText(badCsv, IcdHub.Dummy().ToCsv().Replace("NAV_POS,1,1,0,", "NAV_POS,40,1,0,"));
        var bad = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.LoadIcd(badCsv))).RootElement; var badPrev = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.ExportPreview())).RootElement;
        GB("icd_validate_blocks_export", bad.GetProperty("errors").EnumerateArray().Any(x => (x.GetString() ?? "").StartsWith("error") && (x.GetString() ?? "").Contains("RT 40")) && !badPrev.GetProperty("ok").GetBoolean(), $"errors={string.Join(" | ", bad.GetProperty("errors").EnumerateArray().Select(x => x.GetString()).Take(2))} preview ok={badPrev.GetProperty("ok")}");
        e.Mil1553.LoadIcd(hubCsv); await JS(w, "await nx.test.icd().open('export'); return true;"); await Idle(w, 200);
        await Shot(w, "v100_07_icd_export");
        // ---- 10) export: project (template if given) + message XML + device XML + api; XSD validation + XML round trip + hub JSON round trip + CDV node count
        string outDir = Path.Combine(OutDir, "export", "DummyIcd"); if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        string tdir = Arg("--template"); bool tmpl = tdir.Length > 0 && Directory.Exists(tdir);
        var ex = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.Export(outDir, tmpl ? tdir : null, true, new[] { "project", "xml", "device", "api" }))).RootElement;
        bool xmlOk = ex.GetProperty("xml").GetProperty("ok").GetBoolean(), devOk = ex.GetProperty("device").GetProperty("ok").GetBoolean(), rt = ex.GetProperty("xml").GetProperty("roundTrip").GetBoolean();
        int xsdErrs = ex.GetProperty("xml").GetProperty("schemaErrors").GetArrayLength() + ex.GetProperty("device").GetProperty("schemaErrors").GetArrayLength();
        GB("icd_export_xml_xsd_roundtrip", xmlOk && devOk && rt && xsdErrs == 0 && File.Exists(Path.Combine(outDir, "DummyIcd_messages.xml")) && File.Exists(Path.Combine(outDir, "DummyIcd_device.xml")), $"xml ok={xmlOk} device ok={devOk} roundTrip={rt} schema errors={xsdErrs}");
        var hubBack = IcdHub.Load(Path.Combine(outDir, "icd_hub.json")); var xmlBack = IcdXml.FromDbXml(File.ReadAllText(Path.Combine(outDir, "DummyIcd_messages.xml")));
        GB("icd_export_file_roundtrip", IcdHub.SameShape(e.Mil1553.Hub!, hubBack) && IcdHub.SameShape(e.Mil1553.Hub!, xmlBack) && hubBack.Messages[0].Words[0].Scale == 0.5, $"hub json same={IcdHub.SameShape(e.Mil1553.Hub!, hubBack)} xml file same={IcdHub.SameShape(e.Mil1553.Hub!, xmlBack)}");
        var prj = ex.GetProperty("project"); bool prjOk = prj.GetProperty("ok").GetBoolean();
        GB("icd_export_project", (tmpl && prjOk && Directory.EnumerateFiles(outDir, "*.CDV").Any()) || (!tmpl && !prjOk && prj.GetProperty("note").GetString()!.Contains("template")), $"template={tmpl} ok={prjOk} note={prj.GetProperty("note")}");
        if (tmpl && prjOk)
        {
            var cf = CompoundFile.Read(File.ReadAllBytes(Directory.EnumerateFiles(outDir, "*.CDV").First()));
            int bm = cf.Storages.Count(kv => new Guid(kv.Value).ToString() == "1553014a-7f30-4c82-ac9b-043151722f6b"), fl = cf.Storages.Count(kv => new Guid(kv.Value).ToString() == "15530653-9400-488c-af27-8e2092c854f3");
            GB("icd_export_cdv_nodes", bm == 10 && (fl == 40 || fl == 80), $"bcmsg={bm} fields={fl} (10 BC × 4 words{(fl == 80 ? " + 10 simulated SA × 4 words" : "; template has no RT/SA node → BC only")})");
        }
        GB("icd_export_api_script", File.Exists(Path.Combine(outDir, "DummyIcd_automation.py")) && !File.ReadAllText(Path.Combine(outDir, "DummyIcd_automation.py")).Contains("ProgID(\"") , $"simulatorDetected={ex.GetProperty("api").GetProperty("simulatorDetected")}");
        await JS(w, "nx.test.icd().close(); return true;");
        // ICD applied to the list: ICD column + EU words in decode + trend chart
        await JS(w, $"await nx.test.settle(); await nx.test.m1553().scrollTo(0); const r = document.querySelector('.m1553-scroll .trow[data-i]'); return await nx.test.m1553().selectRow(+r.dataset.row);");
        string words = (await JS(w, "return JSON.stringify(nx.test.m1553().words());")).GetString()!;
        int icdCells = (await JS(w, "return Array.from(document.querySelectorAll('.m1553-scroll .trow[data-i]')).filter(r => r.textContent.includes('NAV_') || r.textContent.includes('_W')).length;")).GetInt32();
        var tj = await JS(w, "return await nx.test.m1553().trendFirst();"); await Idle(w, 400);
        int chart = tj.ValueKind == JsonValueKind.Number ? tj.GetInt32() : -1; var tc = e.Charts.FirstOrDefault(c => c.Id == chart);
        GB("icd_applied_decode_trend", words.Contains("\"eu\"") && JsonDocument.Parse(words).RootElement.GetArrayLength() == 4 && tc != null && tc.Model.Curves.Count == 1, $"eu words={JsonDocument.Parse(words).RootElement.GetArrayLength()} icd-named rows visible={icdCells} chart={chart} curves={tc?.Model.Curves.Count}");
        await Shot(w, "v100_08_icd_trend");
        // ---- 11) ARINC 429 sample: open via the empty-state path (bridge), decode, filter by label / parity, stats
        string p429 = SampleBus.Ensure429()!; var df429 = e.OpenFile(p429)!; e.Mil1553.SelectFile(df429.Name); await JS(w, "await nx.test.settle(); return true;"); await Idle(w, 250);
        int t429 = (await JS(w, "return nx.test.m1553().total();")).GetInt32(); string pr429 = (await JS(w, "return nx.test.m1553().protocol();")).GetString() ?? "";
        string cols429 = (await JS(w, "return nx.test.m1553().columns().join(',');")).GetString() ?? "";
        GB("a429_open", t429 == 2000 && pr429 == "A429" && df429.Format == "A429" && cols429.Contains("LABEL") && cols429.Contains("SSM"), $"total={t429} protocol={pr429} format={df429.Format} cols={cols429}");
        int kv429 = (await JS(w, "await nx.test.m1553().scrollTo(0); const r = document.querySelector('.m1553-scroll .trow[data-i]'); return await nx.test.m1553().selectRow(+r.dataset.row);")).GetInt32();
        var d429 = JsonDocument.Parse((await JS(w, "return JSON.stringify(nx.test.m1553().decode());")).GetString()!).RootElement;
        var rec0 = Mil1553DataSource.FileOf429(df429)!.Get(0);
        GB("a429_decode", kv429 >= 8 && d429.GetProperty("label").GetString() == rec0.LabelOctal && d429.GetProperty("sdi").GetInt32() == rec0.Sdi && d429.GetProperty("parityOk").GetBoolean() == rec0.ParityOk, $"kv={kv429} label={d429.GetProperty("label")} sdi={d429.GetProperty("sdi")} ssm={d429.GetProperty("ssmName")} parity={rec0.ParityOk}");
        int nLabel = (await JS(w, $"return await nx.test.m1553().setInput('label', {J(rec0.LabelOctal)});")).GetInt32();
        int nPar = (await JS(w, "await nx.test.m1553().clickFilter('[data-a=parity]'); return nx.test.m1553().total();")).GetInt32();
        await JS(w, "return JSON.stringify(await nx.test.m1553().filter({label: -1, parityOnly: false, sdi: -1, ssm: -1}));");
        var s429 = JsonDocument.Parse(JsonSerializer.Serialize(e.Mil1553.Stats())).RootElement;
        int bars429 = (await JS(w, "return await nx.test.m1553().stats();")).GetInt32(); await Idle(w, 150);
        GB("a429_filter_stats", nLabel > 0 && nLabel < 2000 && nPar <= nLabel && s429.GetProperty("count").GetInt32() == 2000 && s429.GetProperty("perLabel").GetArrayLength() > 0 && bars429 > 0, $"label {rec0.LabelOctal}={nLabel} +parity errors={nPar} stats count={s429.GetProperty("count")} labels={s429.GetProperty("perLabel").GetArrayLength()} parityErrors={s429.GetProperty("parityErrors")} bars={bars429}");
        await Shot(w, "v100_09_a429");
        await JS(w, "await nx.test.m1553().clickFilter('[data-a=stats]'); return true;");
        // ---- 12) .dxd reader migrated into Core: the test fixture opens and matches the recorded truth (channel count)
        string dxd = Path.Combine(TdmsRoot(), "app", "NexysView.Tests", "data", "dxd", "sample_vehsim.dxd");
        if (File.Exists(dxd))
        {
            var dfx = e.OpenFile(dxd)!; await JS(w, "await nx.test.settle(); return true;");
            int truthCh; using (var jd = JsonDocument.Parse(File.ReadAllText(dxd + ".truth.json"))) truthCh = jd.RootElement.GetProperty("channels").GetArrayLength();
            GB("dxd_open_core", dfx.Format == "DXD" && dfx.AllChannels.Count() == truthCh && dfx.AllChannels.All(c => c.Length > 0), $"format={dfx.Format} channels={dfx.AllChannels.Count()} truth={truthCh}");
            e.CloseFile(dfx.Name);
        }
        else L("dxd_open_core skipped (fixture missing: " + dxd + ")");
        // ---- 13) dialogs: settings (sections, pills), help (bus / icd sections), about (version only, no third-party names)
        await JS(w, "await nx.test.clickSel('[data-a=settings]'); return true;"); await Idle(w, 150);
        int secs = (await JS(w, "return document.querySelectorAll('.modal.settings .sec').length;")).GetInt32();
        int pills = (await JS(w, "return document.querySelectorAll('.modal.settings button[data-bt]').length;")).GetInt32();
        string tmplIn = (await JS(w, "return document.querySelector('.modal.settings [data-f=icdTemplateDir]')?.value ?? null;")).ToString();
        await Shot(w, "v100_10_settings");
        await JS(w, "document.querySelector('.modal-bg')?.remove(); return true;");
        GB("settings_dialog", secs == 4 && pills == 3 && tmplIn != "null", $"sections={secs} time pills={pills} template input={tmplIn != "null"}");
        bool helpBus = (await JS(w, "await nx.test.help('bus'); return document.querySelector('.help-pane').textContent.includes('MIL-STD-1553') && !!document.querySelector('.help-nav [data-s=icd]');")).GetBoolean();
        await Shot(w, "v100_11_help_bus");
        bool helpIcd = (await JS(w, "await nx.test.help('icd'); return document.querySelector('.help-pane').textContent.includes('IMPORT') && document.querySelector('.help-pane').textContent.includes('EXPORT');")).GetBoolean();
        int helpSections = (await JS(w, "return document.querySelectorAll('.help-nav [data-s]').length;")).GetInt32();
        string helpText = (await JS(w, "return document.querySelector('.modal-bg')?.textContent ?? '';")).GetString() ?? "";
        await JS(w, "document.querySelector('.modal-bg')?.remove(); return true;");
        GB("help_sections", helpBus && helpIcd && helpSections == 9, $"bus={helpBus} icd={helpIcd} sections={helpSections} (legacy off → 11 − 2)");
        await JS(w, "await nx.test.clickSel('[data-a=about]'); return true;"); await Idle(w, 150);
        string about = (await JS(w, "return document.querySelector('.modal.about')?.textContent ?? '';")).GetString() ?? "";
        await Shot(w, "v100_12_about");
        await JS(w, "document.querySelector('.modal-bg')?.remove(); return true;");
        // names that must not appear in the about / help text are supplied by the private run script (--forbid a,b,c), never in source
        var forbidden = Arg("--forbid").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hits = forbidden.Where(f => about.Contains(f, StringComparison.OrdinalIgnoreCase) || helpText.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        GB("about_help_third_party_names", about.Contains(AppInfo.VersionText) && hits.Count == 0, $"about='{about.Replace('\n', ' ')[..Math.Min(80, about.Length)]}' hits={string.Join(",", hits)}");
        // ---- 14) dark theme capture + accent scan + memory
        e.Theme = "dark"; e.Touch(); await JS(w, "await nx.test.settle(); return true;"); await Idle(w, 200);
        await JS(w, "await nx.test.mode('MIL1553'); return true;"); await Shot(w, "v100_13_dark");
        var scan = await JS(w, "return JSON.stringify(nx.test.accentScan());");
        using (var jd = JsonDocument.Parse(scan.GetString()!)) { int ah = jd.RootElement.GetProperty("hits").GetArrayLength(); G("accent_borders_zero", ah, "", "=0"); if (ah > 0) L("accent hits: " + jd.RootElement.GetProperty("hits")); }
        e.Theme = "light"; e.Touch(); await JS(w, "await nx.test.settle(); return true;");
        await JS(w, "await nx.test.mode('VIEW'); return true;"); await Shot(w, "v100_14_view_after");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Idle(w, 200);
        G("memory_ws_end_mb", WsMb(), "MB", "<=600");
        G("v100rc_total_s", sw.Elapsed.TotalSeconds, "s");
    }
}
