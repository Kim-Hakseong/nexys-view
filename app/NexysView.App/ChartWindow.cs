using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexysView.App.Services;

namespace NexysView.App;

/// <summary>0.9.4: a secondary WPF window hosting the same page (index.html?win=N) for charts detached from the main window.
/// It shares the engine and the session: its own <see cref="Bridge"/> subscribes to the same engine events, so x-axis links, bands,
/// overview/detail and cursors stay in sync across windows. Closing the window returns its charts to the main window.</summary>
public sealed class ChartWindow : Window
{
    public int Id { get; }
    public WebView2 Web { get; } = new();
    public Bridge? Bridge { get; private set; }
    readonly MainWindow _owner;
    readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task WhenReady => _ready.Task;
    public bool Returning;   // charts are being returned by the host (close without a second return)

    public ChartWindow(MainWindow owner, int id, Rect? rect)
    {
        _owner = owner; Id = id;
        Title = $"{owner.DisplayName} — Window {id}";
        Width = 960; Height = 600; MinWidth = 480; MinHeight = 320;
        Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#ECEAE5")!;
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/brand/AppIcon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); } catch { }
        if (rect is { } r && OnAScreen(r)) { Left = r.X; Top = r.Y; Width = Math.Max(MinWidth, r.Width); Height = Math.Max(MinHeight, r.Height); WindowStartupLocation = WindowStartupLocation.Manual; }
        else { WindowStartupLocation = WindowStartupLocation.Manual; Left = owner.Left + 60 + 24 * (id % 5); Top = owner.Top + 60 + 24 * (id % 5); if (!OnAScreen(new Rect(Left, Top, Width, Height))) { Left = 80; Top = 80; } }
        Content = Web;
        Loaded += async (_, _) => await InitAsync();
        Closing += (_, _) => { if (!Returning) { Returning = true; owner.OnChartWindowClosing(this); } };
        Closed += (_, _) => { var sw = System.Diagnostics.Stopwatch.StartNew(); try { Bridge?.Dispose(); } catch { } try { Web.Dispose(); } catch { } Diag.Trace($"chart window {Id} closed ({sw.ElapsedMilliseconds} ms)"); };
    }

    /// <summary>Saved window rectangles are only honoured when at least part of the window is on a connected screen; else the main screen.</summary>
    public static bool OnAScreen(Rect r)
    {
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var head = new Rect(r.X, r.Y, Math.Max(100, Math.Min(r.Width, 300)), 40);
        return vs.IntersectsWith(head) && r.Width >= 200 && r.Height >= 120;
    }

    async Task InitAsync()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var env = _owner.Env ?? throw new InvalidOperationException("WebView2 environment not ready");
            Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml("#ECEAE5");
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;
            MainWindow.ConfigureCore(core);
            Bridge = new Bridge(core, env, _owner.Engine, Dispatcher, _owner.HostCommand, _owner.AppDispatch);
            Bridge.Ready += () => Dispatcher.BeginInvoke(() => { Bridge.PushSnapshot(); _ready.TrySetResult(); });
            core.Navigate($"https://app.nexys/index.html?win={Id}");
            Diag.Trace($"chart window {Id} core ready ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception ex) { Diag.Trace("chart window init failed " + ex); _ready.TrySetException(ex); }
    }

    public Rect Rect => WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

    public async Task CaptureAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        await Web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
    }
}
