using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using NexysView.Core.Data;

namespace NexysView.Core.Pyramid;

/// <summary>Builds pyramids in the background (one task per channel, bounded parallelism) with a disk cache next to the file.</summary>
public sealed class PyramidService
{
    readonly ConcurrentDictionary<string, Task<MinMaxPyramid>> _tasks = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    public Action<string>? Log { get; set; }
    readonly SemaphoreSlim _gate;
    public event Action<Channel, long>? Progress;     // channel, samples built so far
    public event Action<Channel>? Completed;
    public bool UseDiskCache { get; set; } = true;
    public string? CacheRootOverride { get; set; }

    public PyramidService(int parallelism = 0)
    {
        _gate = new SemaphoreSlim(parallelism > 0 ? parallelism : Math.Max(2, Environment.ProcessorCount - 1));
    }

    public static string CacheDirFor(string dataFilePath, string? overrideRoot)
    {
        if (overrideRoot != null) return Path.Combine(overrideRoot, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(dataFilePath.ToLowerInvariant())))[..16]);
        return dataFilePath + ".nvcache";
    }

    public string CachePath(Channel ch, string dataFilePath)
    {
        var fi = new FileInfo(dataFilePath);
        string key = $"{ch.Group}|{ch.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}";
        string h = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..20];
        return Path.Combine(CacheDirFor(dataFilePath, CacheRootOverride), h + ".nvp");
    }

    /// <summary>Get or start building. Returns immediately; the task completes when the pyramid is ready.</summary>
    public Task<MinMaxPyramid> Ensure(Channel ch, string dataFilePath, CancellationToken outer = default)
    {
        if (ch.Pyramid != null && ch.Pyramid.Complete) return Task.FromResult(ch.Pyramid);
        // duplicate requests for the same channel share one task (stability task [4])
        return _tasks.GetOrAdd(ch.Id, _ => Task.Run(async () =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _cts[ch.Id] = cts;
            var ct = cts.Token;
            Log?.Invoke($"pyramid {ch.DisplayName} queued (n={ch.Length:N0})");
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            var sw0 = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                string? cache = null;
                if (UseDiskCache)
                {
                    try { cache = CachePath(ch, dataFilePath); } catch { cache = null; }
                    if (cache != null && File.Exists(cache))
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var p = MinMaxPyramid.Load(cache, ch.Length);
                        if (p != null) { p.BuildTime = sw.Elapsed; ch.Pyramid = p; Completed?.Invoke(ch); return p; }
                    }
                }
                var py = new MinMaxPyramid(ch.Length);
                ch.Pyramid = py;   // partial pyramid is visible immediately (progressive display)
                py.Build(ch.Source, n => Progress?.Invoke(ch, n), ct: ct);
                if (UseDiskCache && cache != null)
                {
                    try { py.Save(cache); }
                    catch { /* read-only location: keep in memory only */ }
                }
                Completed?.Invoke(ch);
                Log?.Invoke($"pyramid {ch.DisplayName} built ({sw0.Elapsed.TotalMilliseconds:F0} ms)");
                return py;
            }
            catch (OperationCanceledException) { Log?.Invoke($"pyramid {ch.DisplayName} cancelled"); throw; }
            finally { _gate.Release(); _tasks.TryRemove(ch.Id, out Task<MinMaxPyramid>? _); _cts.TryRemove(ch.Id, out CancellationTokenSource? _); }
        }, outer));
    }

    /// <summary>Cancel a build in flight (file closed).</summary>
    public void Cancel(Channel ch) { if (_cts.TryRemove(ch.Id, out var c)) { try { c.Cancel(); } catch { } } }
}
