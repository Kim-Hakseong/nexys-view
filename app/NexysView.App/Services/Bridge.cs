using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NexysView.Core.Data;

namespace NexysView.App.Services;

/// <summary>
/// WebView2 ⇄ Engine bridge. The page posts JSON commands (<c>{id, cmd, ...args}</c>) and receives <c>{ev:'reply'|'state'|'frame'|
/// 'invalidate'|'test', …}</c>. Chart frames go through one shared memory buffer per chart (Float32), posted once with
/// PostSharedBufferToScript; each frame rewrites it and sends a small JSON header. Host-only commands (file dialogs, exports) are
/// delegated to the window.
/// </summary>
public sealed class Bridge
{
    readonly CoreWebView2 _wv; readonly CoreWebView2Environment _env; readonly Engine _e; readonly Dispatcher _ui;
    readonly Func<string, JsonElement, Task<object?>> _host;
    readonly FrameServer _fs = new();
    readonly object _fsLock = new();
    public const int BufferFloats = 2 * 1024 * 1024;   // 8 MB per chart (8192 columns × 2 × 64 curves fits; raw windows ≤ 6 samples/px)
    readonly Dictionary<int, (CoreWebView2SharedBuffer buf, float[] staging)> _buffers = new();
    readonly HashSet<int> _ack = new();   // charts whose shared buffer the page has received; others get the payload inline (base64)
    public bool SharedBuffersWorking => _ack.Count > 0;
    public int InlineFrames { get; private set; }
    sealed record FrameReq(int Chart, int W, double H, double Dpr, double? XMin, double? XMax, bool Commit, double? FromX0, double? FromX1, int Seq);
    readonly Dictionary<int, FrameReq> _pending = new();
    readonly HashSet<int> _inFlight = new();
    readonly HashSet<int> _invalid = new();
    bool _snapshotScheduled, _invalidateScheduled;
    readonly Dictionary<int, TaskCompletionSource<JsonElement>> _tests = new();
    int _testId;
    public bool PageReady { get; private set; }
    public event Action? Ready;
    public double LastFrameMs { get; private set; }
    public int FramesServed { get; private set; }
    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    public Bridge(CoreWebView2 wv, CoreWebView2Environment env, Engine e, Dispatcher ui, Func<string, JsonElement, Task<object?>> host)
    {
        _wv = wv; _env = env; _e = e; _ui = ui; _host = host;
        _wv.WebMessageReceived += OnMessage;
        _e.Changed += () => { ScheduleSnapshot(); PruneBuffers(); };
        _e.ChartChanged += id => { lock (_invalid) _invalid.Add(id); ScheduleInvalidate(); };
        _e.UndoChanged += () => _ui.BeginInvoke(DispatcherPriority.Background, () => { if (PageReady) Post(new { ev = "undo", can = _e.State.Undo.CanUndo, name = _e.State.Undo.UndoName, canRedo = _e.State.Undo.CanRedo }); });
        _e.StatusChanged += () => _ui.BeginInvoke(DispatcherPriority.Background, () => { if (PageReady) Post(new { ev = "status", text = _e.StatusText, led = _e.Led }); });
    }

    // ------------------------------------------------------------------ outbound
    public void Post(object msg) { try { _wv.PostWebMessageAsJson(JsonSerializer.Serialize(msg, JsonOpts)); } catch (Exception ex) { Diag.Trace("post failed " + ex.Message); } }

    void ScheduleSnapshot()
    {
        if (_snapshotScheduled) return; _snapshotScheduled = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _snapshotScheduled = false; if (!PageReady) return; try { bool files = _sentFilesVersion != _e.FilesVersion; _sentFilesVersion = _e.FilesVersion; Post(new { ev = "state", state = _e.Snapshot(files) }); } catch (Exception ex) { Diag.Trace("snapshot failed " + ex); } });
    }

    void ScheduleInvalidate()
    {
        if (_invalidateScheduled) return; _invalidateScheduled = true;
        _ui.BeginInvoke(DispatcherPriority.Render, () =>
        {
            _invalidateScheduled = false; if (!PageReady) return;
            int[] ids; lock (_invalid) { ids = _invalid.ToArray(); _invalid.Clear(); }
            Post(new { ev = "invalidate", charts = ids });
        });
    }

    int _sentFilesVersion = -1;
    public void PushSnapshot() { if (PageReady) { _sentFilesVersion = _e.FilesVersion; Post(new { ev = "state", state = _e.Snapshot(true) }); } }

    // ------------------------------------------------------------------ inbound
    async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs a)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(a.WebMessageAsJson); } catch { return; }
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        string cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() ?? "" : "";
        int id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt32() : -1;
        try
        {
            switch (cmd)
            {
                case "ready": PageReady = true; Ready?.Invoke(); PushSnapshot(); return;
                case "frame": HandleFrame(root); return;
                case "files":
                    foreach (var o in a.AdditionalObjects) if (o is CoreWebView2File f && !string.IsNullOrEmpty(f.Path)) _e.OpenFile(f.Path);
                    Reply(id, true, null); return;
                case "test": { int tid = root.GetProperty("tid").GetInt32(); lock (_tests) { if (_tests.Remove(tid, out var tcs)) tcs.TrySetResult(root.TryGetProperty("result", out var r) ? r.Clone() : default); } return; }
                case "log": Diag.Trace("web: " + (root.TryGetProperty("text", out var t) ? t.GetString() : "")); return;
                case "bufferOk": _ack.Add(I(root, "chart")); return;
            }
            object? result = Dispatch(cmd, root);
            if (result is Task<object?> task) result = await task;
            Reply(id, true, result);
        }
        catch (Exception ex) { Diag.Trace($"cmd {cmd} failed: {ex}"); Reply(id, false, ex.Message); }
    }

    void Reply(int id, bool ok, object? result) { if (id >= 0) Post(new { ev = "reply", id, ok, result }); }

    static string S(JsonElement r, string k, string d = "") => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? d : d;
    static int I(JsonElement r, string k, int d = 0) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
    static double D(JsonElement r, string k, double d = 0) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : d;
    static double? DN(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static bool? BN(JsonElement r, string k) => r.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;
    static bool B(JsonElement r, string k, bool d = false) => BN(r, k) ?? d;
    static int? IN(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    static List<string> SL(JsonElement r, string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new();

    object? Dispatch(string cmd, JsonElement r)
    {
        switch (cmd)
        {
            case "state": _sentFilesVersion = _e.FilesVersion; return _e.Snapshot(true);
            case "open": foreach (var p in SL(r, "paths")) _e.OpenFile(p); return null;
            case "openWithPlugin": foreach (var p in SL(r, "paths")) _e.OpenWithPlugin(p, S(r, "plugin")); return null;
            case "closeFile": _e.CloseFile(S(r, "name")); return null;
            case "setMode": _e.Mode = S(r, "mode", "VIEW"); _e.Touch(); return null;
            case "setTheme": _e.Theme = S(r, "theme", "light"); _e.Touch(); return null;
            case "setPlot": _e.Plot = S(r, "plot", "dark"); _e.Touch(); _e.TouchChart(-1); return null;
            case "setDeveloper": _e.Developer = B(r, "on"); _e.Touch(); return null;
            case "tourSeen": _e.TourSeen = true; _e.Touch(); return null;
            case "select": _e.Selected = S(r, "channel") is { Length: > 0 } s ? s : null; _e.Touch(); return null;
            case "selectFile": _e.SelectedFile = S(r, "name") is { Length: > 0 } fn ? fn : null; _e.Touch(); return null;
            case "selectFiles": _e.SelectedFiles.Clear(); _e.SelectedFiles.AddRange(SL(r, "names")); _e.Touch(); return null;
            case "setHier": _e.Hier[S(r, "file")] = B(r, "on"); _e.Touch(); return null;
            case "addChart": { var ch = _e.AddChart(at: I(r, "at", -1)); return ch.Id; }
            case "removeChart": _e.RemoveChart(I(r, "chart")); return null;
            case "setActive": _e.ActiveChart = I(r, "chart"); _e.Touch(); return null;
            case "swapCharts": _e.Swap(I(r, "a"), I(r, "b")); return null;
            case "addCurves":
                {
                    int chart = I(r, "chart"); string zone = S(r, "zone", "plot"); var ids = SL(r, "channels");
                    if (chart <= 0) chart = _e.AddChart().Id;
                    if (zone == "xy" && ids.Count == 2) { _e.AddCurve(chart, ids[1], "xy", ids[0]); return chart; }
                    foreach (var cid in ids) _e.AddCurve(chart, cid, zone);
                    return chart;
                }
            case "removeCurve": _e.RemoveCurve(I(r, "chart"), I(r, "idx")); return null;
            case "moveCurve": _e.MoveCurve(I(r, "from"), I(r, "idx"), I(r, "to")); return null;
            case "setView": _e.SetView(I(r, "chart"), D(r, "xmin"), D(r, "xmax"), B(r, "commit", true), DN(r, "fromX0"), DN(r, "fromX1")); return null;
            case "zoomFull": _e.ZoomFull(I(r, "chart")); return null;
            case "setCursor": _e.SetCursor(I(r, "chart"), S(r, "which", "a"), DN(r, "x")); return null;
            case "clearCursors": _e.ClearCursors(I(r, "chart")); return null;
            case "setCurve": _e.SetCurve(I(r, "chart"), I(r, "idx"), BN(r, "visible"), BN(r, "step"), IN(r, "color"), BN(r, "right")); return null;
            case "setChart": _e.SetChart(I(r, "chart"), r.TryGetProperty("title", out var tt) ? tt.GetString() : null, BN(r, "grid"), BN(r, "legend"), BN(r, "lanes"), r.TryGetProperty("timeMode", out var tm) ? tm.GetString() : null); return null;
            case "setX": return _e.SetXChannel(I(r, "chart"), S(r, "channel"));
            case "clearX": _e.ClearX(I(r, "chart")); return null;
            case "undo": _e.Undo(); return null;
            case "redo": _e.Redo(); return null;
            case "dropAll": { var ch = _e.DropAll(SL(r, "channels")); return ch.Id; }
            case "setAlign": _e.AlignMode = S(r, "mode", "start"); _e.AlignOffsetMs = D(r, "offsetMs", _e.AlignOffsetMs); _e.Touch(); return null;
            case "compare":
                {
                    var names = SL(r, "files"); if (names.Count == 0) names = _e.SelectedFiles.Count >= 2 ? _e.SelectedFiles.ToList() : _e.State.Files.Select(f => f.Name).ToList();
                    var files = _e.FilesByName(names);
                    var chans = SL(r, "channels");
                    if (chans.Count == 0) chans = Compare.CommonChannels(files).Where(n => XIndex.TimeScale(files[0].AllChannels.FirstOrDefault(c => c.Name == n)?.Unit ?? "") <= 0).Take(4).ToList();
                    return _e.CreateComparison(files, chans, S(r, "mode", _e.AlignMode), D(r, "offsetMs", _e.AlignOffsetMs));
                }
            case "table.add": { var t = _e.AddTable(); foreach (var cid in SL(r, "channels")) _e.TableAdd(t.Id, cid); return t.Id; }
            case "table.addChannels": foreach (var cid in SL(r, "channels")) _e.TableAdd(I(r, "table"), cid); return null;
            case "table.remove": _e.RemoveTable(I(r, "table")); return null;
            case "table.rows": return _e.TableRows(I(r, "table"), (long)D(r, "first"), I(r, "count", 100));
            // analysis
            case "analysis.function": _e.Analysis.SelectFunction(S(r, "name")); return null;
            case "analysis.param": _e.Analysis.SetParam(S(r, "key"), S(r, "value")); return null;
            case "analysis.inputs": _e.Analysis.SetInputs(SL(r, "channels")); return null;
            case "analysis.run": return _e.Analysis.RunAsync(r.TryGetProperty("function", out var fnEl) ? fnEl.GetString() : null).ContinueWith(t => (object?)t.Result, TaskScheduler.FromCurrentSynchronizationContext());
            case "analysis.cancel": _e.Analysis.Cancel(); return null;
            case "analysis.show": return _e.Analysis.ShowInView();
            // report
            case "report.add": _e.Report.Add(S(r, "kind", "Title"), I(r, "chart"), S(r, "text")); return null;
            case "report.remove": _e.Report.Remove(I(r, "idx")); return null;
            case "report.move": _e.Report.Move(I(r, "idx"), I(r, "dir", 1)); return null;
            case "report.text": _e.Report.SetText(I(r, "idx"), S(r, "text")); return null;
            case "report.clear": _e.Report.Clear(); return null;
            case "report.page": { int wpx = I(r, "width", 794); return Task.Run(() => (object?)("data:image/png;base64," + Convert.ToBase64String(_e.Report.PreviewPng(wpx)))); }
            case "report.export": { string path = S(r, "path"); bool pdf = B(r, "pdf"); return Task.Run(() => { _e.Report.ExportTo(path, pdf); _e.SetStatus("Exported " + path); return (object?)null; }); }
            case "props": return _e.ChannelProps(S(r, "channel"));
            case "sparkline": { var ch = _e.Find(S(r, "channel")); return ch == null ? null : _e.Sparkline(ch); }
            case "layout.save": return null;
            default: return _host(cmd, r);
        }
    }

    // ------------------------------------------------------------------ frames
    void EnsureBuffer(int chart)
    {
        if (_buffers.ContainsKey(chart)) return;
        var buf = _env.CreateSharedBuffer((ulong)BufferFloats * 4);
        _buffers[chart] = (buf, new float[BufferFloats]);
        _wv.PostSharedBufferToScript(buf, CoreWebView2SharedBufferAccess.ReadOnly, JsonSerializer.Serialize(new { chart, floats = BufferFloats }));
    }

    /// <summary>Dispose shared buffers of charts that no longer exist (the page releases its ArrayBuffer on 'releaseBuffer').</summary>
    void PruneBuffers()
    {
        if (_buffers.Count == 0) return;
        var gone = _buffers.Keys.Where(id => id != 0 && _e.Get(id) == null).ToList();
        foreach (var id in gone)
        {
            var (buf, _) = _buffers[id]; _buffers.Remove(id); _ack.Remove(id); _pending.Remove(id);
            Post(new { ev = "releaseBuffer", chart = id });
            try { buf.Dispose(); } catch { }
        }
    }

    void HandleFrame(JsonElement r)
    {
        int chart = I(r, "chart");
        var req = new FrameReq(chart, I(r, "w", 800), D(r, "h", 300), D(r, "dpr", 1), DN(r, "xmin"), DN(r, "xmax"), B(r, "commit"), DN(r, "fromX0"), DN(r, "fromX1"), I(r, "seq"));
        EnsureBuffer(chart);
        var ch = _e.Get(chart); if (ch == null) { Post(new { ev = "frame", chart, seq = req.Seq, gone = true }); return; }
        if (req.XMin != null && req.XMax != null) _e.SetViewQuiet(chart, req.XMin.Value, req.XMax.Value, req.Commit, req.FromX0, req.FromX1);
        if (_inFlight.Contains(chart)) { _pending[chart] = req; return; }
        StartFrame(req);
    }

    void StartFrame(FrameReq req)
    {
        var ch = _e.Get(req.Chart); if (ch == null) return;
        var (buf, staging) = _buffers[req.Chart];
        _inFlight.Add(req.Chart);
        Task.Run(() =>
        {
            FrameServer.Result res;
            lock (_fsLock) res = _fs.Compute(ch, req.W, req.H, req.Dpr, staging);
            return res;
        }).ContinueWith(t =>
        {
            _inFlight.Remove(req.Chart);
            if (t.IsFaulted) { Diag.Trace("frame failed " + t.Exception?.InnerException?.Message); Post(new { ev = "frame", chart = req.Chart, seq = req.Seq, error = t.Exception?.InnerException?.Message }); }
            else
            {
                var res = t.Result;
                try { Marshal.Copy(staging, 0, buf.Buffer, Math.Max(1, res.Floats)); } catch (Exception ex) { Diag.Trace("buffer copy failed " + ex.Message); }
                LastFrameMs = res.Ms; FramesServed++;
                if (_ack.Contains(req.Chart)) Post(new { ev = "frame", chart = req.Chart, seq = req.Seq, header = res.Header });
                else
                {
                    // page has not (yet) received the shared buffer: ship the floats inline
                    InlineFrames++;
                    var bytes = new byte[Math.Max(1, res.Floats) * 4]; Buffer.BlockCopy(staging, 0, bytes, 0, bytes.Length);
                    Post(new { ev = "frame", chart = req.Chart, seq = req.Seq, header = res.Header, data = Convert.ToBase64String(bytes) });
                }
            }
            if (_pending.Remove(req.Chart, out var next)) StartFrame(next);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    // ------------------------------------------------------------------ automation
    /// <summary>Run a JS snippet inside <c>nx.test.run(tid, async () => { … })</c> on the page and await its result (JSON).</summary>
    public Task<JsonElement> RunTest(string body, int timeoutMs = 60000)
    {
        int tid = Interlocked.Increment(ref _testId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_tests) _tests[tid] = tcs;
        string js = $"window.nx.test.run({tid}, async () => {{ {body} }})";
        _ = _wv.ExecuteScriptAsync(js);
        var timeout = Task.Delay(timeoutMs).ContinueWith(_ => { lock (_tests) _tests.Remove(tid); tcs.TrySetException(new TimeoutException("test step timed out: " + body[..Math.Min(80, body.Length)])); });
        return tcs.Task;
    }

    public Task<string> Eval(string js) => _wv.ExecuteScriptAsync(js);

    /// <summary>Diagnostics: post a tiny shared buffer tagged chart=99 (the page logs its arrival).</summary>
    public void PostTestBuffer()
    {
        try { var b = _env.CreateSharedBuffer(64); _wv.PostSharedBufferToScript(b, CoreWebView2SharedBufferAccess.ReadOnly, "{\"chart\":99}"); Diag.Trace("test buffer posted"); }
        catch (Exception ex) { Diag.Trace("test buffer failed: " + ex); }
    }
}
