using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace NexysView.App.Services;

/// <summary>
/// Stability diagnostics (stability task [1]):
/// <list type="bullet">
/// <item><see cref="Trace"/> — timestamped start/end lines for file open, channel load, pyramid build, tab switch, analysis run → work/hang/trace.log</item>
/// <item><see cref="StartWatchdog"/> — a background thread pings the UI dispatcher every 100 ms; when a ping is not answered within
/// 300 ms it launches <c>NexysView.exe --stacks &lt;pid&gt; &lt;file&gt;</c>, which attaches with ClrMD and writes every managed thread's stack
/// to work/hang/&lt;time&gt;.txt (a process cannot walk its own foreign threads in .NET Core, hence the helper process).</item>
/// </list>
/// </summary>
public static class Diag
{
    public static string HangDir { get; } = FindHangDir();
    static readonly object _lock = new();
    static readonly Stopwatch _clock = Stopwatch.StartNew();
    static string? _traceFile;

    static string FindHangDir()
    {
        try
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) d = d.Parent;
            string root = d != null ? Path.Combine(d.FullName, "work", "hang") : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexysView", "hang");
            Directory.CreateDirectory(root);
            return root;
        }
        catch { return Path.GetTempPath(); }
    }

    /// <summary>Append one line: wall time, ms since start, thread id, message.</summary>
    public static void Trace(string msg)
    {
        try
        {
            lock (_lock)
            {
                _traceFile ??= Path.Combine(HangDir, "trace.log");
                File.AppendAllText(_traceFile, $"{DateTime.Now:HH:mm:ss.fff} +{_clock.Elapsed.TotalMilliseconds,9:F1} T{Environment.CurrentManagedThreadId,-3} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }

    /// <summary>Trace "name start" now and "name end (ms)" when the returned scope is disposed.</summary>
    public static IDisposable Span(string name) => new Scope(name);

    sealed class Scope : IDisposable
    {
        readonly string _n; readonly Stopwatch _sw = Stopwatch.StartNew();
        public Scope(string n) { _n = n; Trace(_n + " start"); }
        public void Dispose() => Trace($"{_n} end ({_sw.Elapsed.TotalMilliseconds:F1} ms)");
    }

    // ------------------------------------------------------------------ watchdog
    static Thread? _wd;
    public static int StallThresholdMs { get; set; } = 300;
    public static int StallCount { get; private set; }
    public static double MaxStallMs { get; private set; }
    public static void ResetStats() { StallCount = 0; MaxStallMs = 0; }
    public static event Action<double>? Stalled;

    public static void StartWatchdog(Dispatcher ui)
    {
        if (_wd != null) return;
        long lastPong = _clock.ElapsedMilliseconds;
        long lastPing = lastPong;
        bool pending = false;
        DateTime lastDump = DateTime.MinValue;
        _wd = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(50);
                if (ui.HasShutdownStarted) return;
                long now = _clock.ElapsedMilliseconds;
                if (!pending)
                {
                    pending = true; lastPing = now;
                    ui.BeginInvoke(DispatcherPriority.Normal, () => { lastPong = _clock.ElapsedMilliseconds; pending = false; });
                }
                else if (now - lastPing > StallThresholdMs)
                {
                    double stall = now - lastPing;
                    if ((DateTime.Now - lastDump).TotalSeconds > 5)
                    {
                        lastDump = DateTime.Now;
                        StallCount++;
                        string file = Path.Combine(HangDir, DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
                        Trace($"WATCHDOG ui stalled {stall:F0} ms -> {Path.GetFileName(file)}");
                        DumpStacks(file, stall);
                        Stalled?.Invoke(stall);
                    }
                    // keep measuring the same stall until the pong arrives
                    while (pending && !ui.HasShutdownStarted) { Thread.Sleep(50); }
                    double total = _clock.ElapsedMilliseconds - lastPing;
                    MaxStallMs = Math.Max(MaxStallMs, total);
                    Trace($"WATCHDOG ui resumed after {total:F0} ms");
                }
            }
        }) { IsBackground = true, Name = "ui-watchdog" };
        _wd.Start();
    }

    static void DumpStacks(string file, double stallMs)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "NexysView.exe";
            var psi = new ProcessStartInfo(exe, $"--stacks {Environment.ProcessId} \"{file}\" {stallMs:F0}") { UseShellExecute = false, CreateNoWindow = true };
            var p = Process.Start(psi);
            if (p != null) _ = Task.Delay(30_000).ContinueWith(_ => { try { if (!p.HasExited) { p.Kill(); Trace("WATCHDOG helper killed after 30 s"); } } catch { } finally { p.Dispose(); } });
        }
        catch (Exception ex) { Trace("WATCHDOG helper failed: " + ex.Message); }
    }

    /// <summary>Run the heap helper synchronously (stress modes): top managed types by size → file.</summary>
    public static void DumpHeap(string file)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "NexysView.exe";
            using var p = Process.Start(new ProcessStartInfo(exe, $"--heap {Environment.ProcessId} \"{file}\"") { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(120_000);
        }
        catch (Exception ex) { Trace("heap helper failed: " + ex.Message); }
    }

    /// <summary>Helper-process entry (<c>--heap pid file</c>): managed heap summary, top 40 types by total size.</summary>
    public static int WriteHeap(string[] args)
    {
        int pid = int.Parse(args[1]); string file = args[2];
        var sb = new StringBuilder();
        try
        {
            using var dt = Microsoft.Diagnostics.Runtime.DataTarget.AttachToProcess(pid, suspend: true);
            using var rt = dt.ClrVersions[0].CreateRuntime();
            var heap = rt.Heap;
            var byType = new Dictionary<string, (long count, long bytes)>();
            long total = 0;
            foreach (var obj in heap.EnumerateObjects())
            {
                if (!obj.IsValid || obj.Type == null) continue;
                string n = obj.Type.Name ?? "?"; long sz = (long)obj.Size; total += sz;
                byType[n] = byType.TryGetValue(n, out var e) ? (e.count + 1, e.bytes + sz) : (1, sz);
            }
            sb.AppendLine($"# heap pid={pid} total={total / 1048576.0:F1} MB objects={byType.Values.Sum(v => v.count):N0} at {DateTime.Now:HH:mm:ss}");
            sb.AppendLine("bytes	count	type");
            foreach (var kv in byType.OrderByDescending(k => k.Value.bytes).Take(40)) sb.AppendLine($"{kv.Value.bytes:N0}	{kv.Value.count:N0}	{kv.Key}");
        }
        catch (Exception ex) { sb.AppendLine("heap walk failed: " + ex); }
        File.WriteAllText(file, sb.ToString());
        return 0;
    }

    /// <summary>Helper-process entry (<c>--stacks pid file [stallMs]</c>): attach with ClrMD and write all managed thread stacks.</summary>
    public static int WriteStacks(string[] args)
    {
        int pid = int.Parse(args[1]); string file = args[2];
        var sb = new StringBuilder();
        sb.AppendLine($"# NexysView UI stall {(args.Length > 3 ? args[3] + " ms" : "")} pid={pid} at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        // hard limit: whatever ClrMD does (DAC load, a target that exits mid-walk), the helper is gone after 20 s
        var guard = new Thread(() => { Thread.Sleep(20_000); lock (sb) { sb.AppendLine("# helper timeout after 20 s"); try { File.WriteAllText(file, sb.ToString()); } catch { } } Environment.Exit(2); }) { IsBackground = true };
        guard.Start();
        try
        {
            // suspend:false — a suspending attach loads the DAC while all target threads are frozen (seconds), which would itself
            // look like a UI stall; non-suspending walks may see slightly inconsistent frames but do not perturb the app
            var swAttach = System.Diagnostics.Stopwatch.StartNew();
            using var dt = Microsoft.Diagnostics.Runtime.DataTarget.AttachToProcess(pid, suspend: false);
            sb.AppendLine($"# attach {swAttach.Elapsed.TotalMilliseconds:F0} ms (non-suspending)");
            foreach (var cv in dt.ClrVersions)
            {
                using var rt = cv.CreateRuntime();
                foreach (var th in rt.Threads)
                {
                    if (!th.IsAlive) continue;
                    var frames = th.EnumerateStackTrace().ToList();
                    lock (sb)
                    {
                        sb.AppendLine($"## thread {th.ManagedThreadId} osid={th.OSThreadId:X} {(th.IsFinalizer ? "finalizer " : "")}{(th.IsGc ? "gc " : "")}frames={frames.Count}");
                        foreach (var f in frames) sb.AppendLine("    " + (f.Method != null ? f.Method.Type?.Name + "." + f.Method.Name : f.FrameName ?? f.Kind.ToString()));
                    }
                }
            }
        }
        catch (Exception ex) { lock (sb) sb.AppendLine("attach failed: " + ex); }
        lock (sb) File.WriteAllText(file, sb.ToString());
        return 0;
    }
}
