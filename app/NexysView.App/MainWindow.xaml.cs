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

    public MainWindow()
    {
        InitializeComponent();
        Title = $"{_brand.displayName} {AppInfo.VersionText}";
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand/AppIcon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }
        SourceInitialized += (_, _) => AppId.ApplyWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, _brand.displayName);
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => { Engine.Analysis.Cancel(); LayoutStore.Save(this); };
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
            string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexysView", SelfTest.Enabled ? "WebView2-auto" : "WebView2");
            var opts = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection" };
            var env = await CoreWebView2Environment.CreateAsync(FixedRuntimeFolder, userData, opts);
            RuntimeVersion = env.BrowserVersionString;
            Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml("#ECEAE5");
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            if (!Directory.Exists(WwwRoot)) { Boot.Text = "wwwroot missing — run `npm run build` in app/web and rebuild"; Diag.Trace("wwwroot missing: " + WwwRoot); }
            core.SetVirtualHostNameToFolderMapping("app.nexys", WwwRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.ProcessFailed += (_, e) => Diag.Trace($"webview process failed: {e.ProcessFailedKind} {e.Reason}");
            Bridge = new Bridge(core, env, Engine, Dispatcher, HostCommand);
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
    async Task<object?> HostCommand(string cmd, JsonElement args)
    {
        switch (cmd)
        {
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
            case "openUrl":
                {
                    string url = args.GetProperty("url").GetString() ?? "";
                    if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                    return null;
                }
            case "quit": Close(); return null;
        }
        throw new NotSupportedException("unknown command " + cmd);
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

/// <summary>Version strings for the title bar, the page header and the About dialog (from the assembly's InformationalVersion).</summary>
public static class AppInfo
{
    public static string Version { get; } = (typeof(AppInfo).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.9.0-beta";
    public static bool IsBeta => Version.Contains("beta", StringComparison.OrdinalIgnoreCase);
    /// <summary>"0.9.0 beta"</summary>
    public static string VersionText => Version.Replace("-beta", " beta");
    public const string Repo = "https://github.com/Kim-Hakseong/nexys-view";
    public const string Issues = Repo + "/issues";
}

public sealed class Branding
{
    public string displayName { get; set; } = "Nexys VIEW";
    public static Branding Load()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, "branding.json");
            if (File.Exists(p)) return JsonSerializer.Deserialize<Branding>(File.ReadAllText(p)) ?? new Branding();
        }
        catch { }
        return new Branding();
    }
}
