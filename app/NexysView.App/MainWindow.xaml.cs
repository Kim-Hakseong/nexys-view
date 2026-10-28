using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using NexysView.Core.Data;
using NexysView.App.Services;

namespace NexysView.App;

/// <summary>One WPF window hosting the WebView2 page (app/web → wwwroot). Dialogs, exports and the window itself stay native;
/// everything else is the page talking to <see cref="Engine"/> through <see cref="Bridge"/>.</summary>
public partial class MainWindow : Window
{
    public Engine Engine { get; } = new();
    public Bridge? Bridge { get; private set; }
    readonly Branding _brand = Branding.Load();
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WhenReady => _ready.Task;
    public static string WwwRoot => Path.Combine(AppContext.BaseDirectory, "wwwroot");
    public string? FixedRuntimeFolder { get; private set; }
    public string RuntimeVersion { get; private set; } = "";
    public CoreWebView2Environment? Env { get; private set; }
    public string DisplayName => _brand.displayName;
    /// <summary>0.9.4: detached chart windows by id.</summary>
    public Dictionary<int, ChartWindow> Windows { get; } = new();
    public Dictionary<int, Rect> SavedWindowRects { get; set; } = new();
    int _nextWindow = 2;
    public List<LayoutStore.WindowDto> WindowRects()
    {
        var list = SavedWindowRects.Select(kv => new LayoutStore.WindowDto { Id = kv.Key, X = kv.Value.X, Y = kv.Value.Y, W = kv.Value.Width, H = kv.Value.Height }).ToList();
        foreach (var w in Windows.Values) { var r = w.Rect; list.RemoveAll(x => x.Id == w.Id); list.Add(new LayoutStore.WindowDto { Id = w.Id, X = r.X, Y = r.Y, W = r.Width, H = r.Height }); }
        return list;
    }

    /// <summary>Open a chart window and move the chart (and its detail) into it. Position: caller's screen point, else the saved rect of that id, else cascaded.</summary>
    public async Task<int> DetachChartAsync(int chartId, double? sx, double? sy)
    {
        if (Engine.Get(chartId) == null || chartId == 0) return 0;
        int id = _nextWindow++;
        Rect? rect = SavedWindowRects.TryGetValue(id, out var saved) ? saved : null;
        if (sx != null && sy != null) rect = new Rect(sx.Value - 200, sy.Value - 24, rect?.Width ?? 960, rect?.Height ?? 600);
        var win = new ChartWindow(this, id, rect) { Owner = null };
        Windows[id] = win; win.Show();
        Engine.DetachChart(chartId, id, win.Title);
        try { await win.WhenReady; } catch { }
        return id;
    }

    public void OnChartWindowClosing(ChartWindow w)
    {
        SavedWindowRects[w.Id] = w.Rect;
        Windows.Remove(w.Id);
        int n = Engine.CloseWindow(w.Id);
        Engine.SetStatus(n > 0 ? $"Window {w.Id} closed — {n} chart(s) back in the main window" : $"Window {w.Id} closed");
    }

    /// <summary>Close a chart window from the engine side (last chart returned, tests).</summary>
    public void CloseChartWindow(int id) { if (Windows.TryGetValue(id, out var w)) { w.Returning = true; SavedWindowRects[id] = w.Rect; Windows.Remove(id); Engine.CloseWindow(id); w.Close(); } }

    public static void ConfigureCore(CoreWebView2 core) => WebShell.ConfigureCore(core);
    public MainWindow()
    {
        InitializeComponent();
        Title = $"{_brand.displayName} {AppInfo.VersionText}";
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand/AppIcon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }
        SourceInitialized += (_, _) => AppId.ApplyWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, _brand.displayName);
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => { Engine.Analysis.Cancel(); LayoutStore.Save(this); foreach (var w in Windows.Values.ToList()) { w.Returning = true; try { w.Close(); } catch { } } };
        Engine.Changed += () => { if (Bridge?.PageReady == true) Dispatcher.BeginInvoke(() => LayoutStore.Save(this), System.Windows.Threading.DispatcherPriority.ApplicationIdle); };
    }

    async Task InitAsync()
    {
        try
        {
            Boot.Text = "";
            string? rt = null;
            try { rt = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch (Exception ex) { Diag.Trace("webview2 runtime probe: " + ex.Message); }
            Diag.Trace("webview2 runtime " + (rt ?? "MISSING"));
            if (rt == null)
            {
                string fixedDir = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");   // optional Fixed Version runtime folder next to the exe
                if (Directory.Exists(fixedDir)) { Diag.Trace("using fixed runtime " + fixedDir); FixedRuntimeFolder = fixedDir; }
                else throw new InvalidOperationException("Microsoft Edge WebView2 Runtime is not installed");
            }
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexysView", SelfTest.Enabled ? "WebView2-auto" : Program.Fresh ? "WebView2-fresh" : "WebView2");
            var opts = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection" };
            var env = await CoreWebView2Environment.CreateAsync(FixedRuntimeFolder, userData, opts);
            Env = env;
            RuntimeVersion = env.BrowserVersionString;
            Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml("#ECEAE5");
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            if (!Directory.Exists(WwwRoot)) { Boot.Text = "wwwroot missing — run `npm run build` in app/web and rebuild"; Diag.Trace("wwwroot missing: " + WwwRoot); }
            ConfigureCore(core);
            Bridge = new Bridge(core, env, Engine, Dispatcher, HostCommand, AppDispatch);
            Bridge.Ready += () => { Diag.Trace("page ready"); Dispatcher.BeginInvoke(() => { LayoutStore.Load(this); Bridge.PushSnapshot(); _ready.TrySetResult(); }); };
            core.Navigate("https://app.nexys/index.html");
            Diag.Trace("webview2 " + env.BrowserVersionString + " navigating");
        }
        catch (Exception ex)
        {
            Diag.Trace("webview init failed " + ex);
            Boot.Text = "WebView2 runtime not available: " + ex.Message + "\nInstall the Evergreen WebView2 Runtime (https://developer.microsoft.com/microsoft-edge/webview2/) or place a Fixed Version runtime in the WebView2Runtime folder next to NexysView.exe, then start again.";
            _ready.TrySetException(ex);
        }
    }

    /// <summary>Host-only commands (need WPF dialogs or files).</summary>
    public async Task<object?> HostCommand(string cmd, JsonElement args)
    {
        switch (cmd)
        {
            // 0.9.4 chart windows
            case "detachChart":
                {
                    int chart = args.GetProperty("chart").GetInt32();
                    double? sx = args.TryGetProperty("x", out var ax) && ax.ValueKind == JsonValueKind.Number ? ax.GetDouble() : null, sy = args.TryGetProperty("y", out var ay) && ay.ValueKind == JsonValueKind.Number ? ay.GetDouble() : null;
                    return await DetachChartAsync(chart, sx, sy);
                }
            case "window.close": { int id = args.GetProperty("id").GetInt32(); CloseChartWindow(id); return null; }
            case "window.returnAll":
                {
                    int id = args.GetProperty("id").GetInt32();
                    foreach (var c in Engine.Charts.Where(c => c.WindowId == id).ToList()) Engine.ReturnChart(c.Id);
                    CloseChartWindow(id); return null;
                }
            case "window.list": return Windows.Values.Select(w => new { id = w.Id, title = w.Title, x = w.Rect.X, y = w.Rect.Y, w = w.Rect.Width, h = w.Rect.Height }).ToList();
            case "openDialog":
                {
                    var dlg = new OpenFileDialog { Filter = "Measurement data|*.tdms;*.tdm;*.dxd;*.csv;*.txt;*.xyz;*.tdv|TDMS|*.tdms|TDM|*.tdm|DXD|*.dxd|CSV|*.csv;*.txt|VIEW layout|*.tdv|All|*.*", Multiselect = true };
                    if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) Engine.OpenFile(f);
                    return null;
                }
            case "openPluginDialog":
                {
                    var p = new OpenFileDialog { Title = "DataPlugin script or DLL", Filter = "DataPlugin|*.vbs;*.py;*.dll|VBS DataPlugin|*.vbs|Python DataPlugin|*.py|SDK plugin DLL|*.dll" };
                    if (p.ShowDialog(this) != true) return null;
                    var d = new OpenFileDialog { Title = "Data file to open with " + Path.GetFileName(p.FileName), Filter = "All files|*.*", Multiselect = true };
                    if (d.ShowDialog(this) != true) return null;
                    foreach (var f in d.FileNames) Engine.OpenWithPlugin(f, p.FileName);
                    return null;
                }
            case "saveDialog":
                {
                    var dlg = new SaveFileDialog { Filter = "TDMS|*.tdms", FileName = "export.tdms" };
                    if (dlg.ShowDialog(this) != true) return null;
                    try { int n = Export.TdmsExport.SaveAll(Engine.State, dlg.FileName); Engine.SetStatus($"Saved {n} channels to {dlg.FileName}"); }
                    catch (Exception ex) { Engine.SetStatus("Save failed: " + ex.Message); }
                    return null;
                }
            case "save": { int n = Export.TdmsExport.SaveAll(Engine.State, args.GetProperty("path").GetString()!); Engine.SetStatus($"Saved {n} channels"); return n; }
            case "exportDialog":
                {
                    bool pdf = args.TryGetProperty("pdf", out var p) && p.GetBoolean();
                    var dlg = new SaveFileDialog { Filter = pdf ? "PDF|*.pdf" : "PNG|*.png", FileName = "report" + (pdf ? ".pdf" : ".png") };
                    if (dlg.ShowDialog(this) != true) return null;
                    try { string path = dlg.FileName; await Task.Run(() => Engine.Report.ExportTo(path, pdf)); Engine.SetStatus("Exported " + path); } catch (Exception ex) { Engine.SetStatus("Export failed: " + ex.Message); }
                    return null;
                }
            case "capture": { string path = args.GetProperty("path").GetString()!; await CaptureAsync(path); return path; }
            // 0.9.3: band / full range → CSV or TDMS of the given channels (dialog), chart PNG to file or clipboard, text to clipboard
            case "export.range":
                {
                    bool tdms = args.TryGetProperty("kind", out var k) && k.GetString() == "tdms";
                    var dlg = new SaveFileDialog { Filter = tdms ? "TDMS|*.tdms" : "CSV|*.csv", FileName = tdms ? "export.tdms" : "export.csv" };
                    if (dlg.ShowDialog(this) != true) return null;
                    var ids = args.TryGetProperty("channels", out var chs) && chs.ValueKind == JsonValueKind.Array ? chs.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                    int chart = args.TryGetProperty("chart", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                    double? x0 = args.TryGetProperty("x0", out var a0) && a0.ValueKind == JsonValueKind.Number ? a0.GetDouble() : null, x1 = args.TryGetProperty("x1", out var a1) && a1.ValueKind == JsonValueKind.Number ? a1.GetDouble() : null;
                    string path = dlg.FileName;
                    try { return await Task.Run(() => Engine.ExportRange(path, tdms, ids, chart, x0, x1)); } catch (Exception ex) { Engine.SetStatus("Export failed: " + ex.Message); return null; }
                }
            case "openInBus":   // 1.0.0-rc.2: hand a bus capture to NexysBus (next to the exe, the user's install folder, Program Files), else the release page
                {
                    string path = args.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                    var candidates = new[] { Path.Combine(AppContext.BaseDirectory, "NexysBus.exe"), Path.Combine(AppContext.BaseDirectory, "..", "NexysBus", "NexysBus.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "NexysBus", "NexysBus.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NexysBus", "NexysBus.exe") };
                    var exe = candidates.FirstOrDefault(File.Exists);
                    if (exe != null) { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, $"\"{path}\"") { UseShellExecute = true }); Engine.SetStatus("Opened in NexysBus: " + Path.GetFileName(path)); return exe; }
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Kim-Hakseong/nexys-bus/releases") { UseShellExecute = true });
                    Engine.SetStatus("NexysBus is not installed — opening its release page"); return null;
                }
            case "quit": Close(); return null;
        }
        var (handled, result) = await HostCommands.TryAsync(this, Engine, cmd, args);
        if (handled) return result;
        throw new NotSupportedException("unknown command " + cmd);
    }

    /// <summary>VIEW-only page commands (analysis, report) — tried by the bridge before the host commands.</summary>
    public bool AppDispatch(string cmd, JsonElement r, out object? result)
    {
        var e = Engine; result = null;
        switch (cmd)
        {
            case "busNotice.clear": e.BusNotice = null; return true;
            case "analysis.function": e.Analysis.SelectFunction(Bridge.S(r, "name")); return true;
            case "analysis.param": e.Analysis.SetParam(Bridge.S(r, "key"), Bridge.S(r, "value")); return true;
            case "analysis.inputs": e.Analysis.SetInputs(Bridge.SL(r, "channels")); return true;
            case "analysis.run": result = e.Analysis.RunAsync(r.TryGetProperty("function", out var fnEl) ? fnEl.GetString() : null).ContinueWith(t => (object?)t.Result, TaskScheduler.FromCurrentSynchronizationContext()); return true;
            case "analysis.cancel": e.Analysis.Cancel(); return true;
            case "analysis.show": result = e.Analysis.ShowInView(); return true;
            case "report.add": e.Report.Add(Bridge.S(r, "kind", "Title"), Bridge.I(r, "chart"), Bridge.S(r, "text")); return true;
            case "report.remove": e.Report.Remove(Bridge.I(r, "idx")); return true;
            case "report.move": e.Report.Move(Bridge.I(r, "idx"), Bridge.I(r, "dir", 1)); return true;
            case "report.text": e.Report.SetText(Bridge.I(r, "idx"), Bridge.S(r, "text")); return true;
            case "report.clear": e.Report.Clear(); return true;
            case "report.page": { int wpx = Bridge.I(r, "width", 794); result = Task.Run(() => (object?)("data:image/png;base64," + Convert.ToBase64String(e.Report.PreviewPng(wpx)))); return true; }
            case "report.export": { string path = Bridge.S(r, "path"); bool pdf = Bridge.B(r, "pdf"); result = Task.Run(() => { e.Report.ExportTo(path, pdf); e.SetStatus("Exported " + path); return (object?)null; }); return true; }
        }
        return false;
    }

    /// <summary>Screenshot of the page (what the user sees) as PNG.</summary>
    public async Task CaptureAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }

    public DataFile? OpenFile(string path) => Engine.OpenFile(path);
}

