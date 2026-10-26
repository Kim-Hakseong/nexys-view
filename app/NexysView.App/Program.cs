namespace NexysView.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--stacks") return Services.Diag.WriteStacks(args);   // watchdog helper process
        if (args.Length >= 3 && args[0] == "--heap") return Services.Diag.WriteHeap(args);       // stress-mode heap summary helper
        SelfTest.Args = args;
        Services.AppId.Apply();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
