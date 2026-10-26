namespace NexysView.App;

public static class Program
{
    /// <summary>--fresh: behave like a first start (ignore and do not write layout.json, separate WebView2 profile).</summary>
    public static bool Fresh { get; private set; }
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--stacks") return Services.Diag.WriteStacks(args);   // watchdog helper process
        if (args.Length >= 3 && args[0] == "--heap") return Services.Diag.WriteHeap(args);       // stress-mode heap summary helper
        SelfTest.Args = args;
        Fresh = args.Contains("--fresh");
        Services.AppId.Apply();
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
