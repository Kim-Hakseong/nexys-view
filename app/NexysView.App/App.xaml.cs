using System.Windows;
using NexysView.App.Services;

namespace NexysView.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppPaths.AppName = "NexysView"; AppPaths.Ephemeral = () => Program.Fresh || SelfTest.Enabled; AppPaths.Automation = () => SelfTest.Enabled; AppInfo.Name = "Nexys VIEW";
        foreach (var ext in new[] { ".csv", ".txt", ".xyz" }) AppState.ExtraOpeners[ext] = Import.CsvDataSource.Open;
        AppState.PluginOpener = (data, plugin) => System.IO.Path.GetExtension(plugin).ToLowerInvariant() switch
        {
            ".vbs" => Plugins.VbsDataPlugin.Open(data, plugin),
            ".py" => Import.PluginDataSource.OpenWithPython(data, plugin),
            ".dll" => Import.PluginDataSource.Open(data, plugin),
            _ => throw new NotSupportedException("Plugin must be .vbs, .py or .dll"),
        };
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
            win.WhenReady.ContinueWith(_ => win.Dispatcher.BeginInvoke(() =>
            {
                foreach (var a in e.Args) if (!a.StartsWith("--") && System.IO.File.Exists(a)) win.OpenFile(a);
                if (win.Engine.State.Files.Count == 0) { var sample = SampleData.Ensure(); if (sample != null) win.OpenFile(sample); }   // first start: built-in sample
            }));
        }
        win.Show();
    }
}
