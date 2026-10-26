using System.Windows;
using NexysView.App.Services;

namespace NexysView.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Diag.Trace($"app start args=[{string.Join(" ", e.Args)}] pid={Environment.ProcessId}");
        DispatcherUnhandledException += (_, ex) => { Diag.Trace("UNHANDLED " + ex.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Diag.Trace("UNHANDLED(domain) " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Diag.Trace("UNOBSERVED " + ex.Exception); ex.SetObserved(); };
        var win = new MainWindow();
        MainWindow = win;
        Diag.StartWatchdog(win.Dispatcher);
        if (SelfTest.Enabled)
        {
            win.WhenReady.ContinueWith(_ => win.Dispatcher.BeginInvoke(async () => await SelfTest.RunAsync(win)));
            // files given as plain args are opened by MainWindow; the selftest drives the rest
        }
        else
        {
            win.WhenReady.ContinueWith(_ => win.Dispatcher.BeginInvoke(() => { foreach (var a in e.Args) if (System.IO.File.Exists(a)) win.OpenFile(a); }));
        }
        win.Show();
    }
}
