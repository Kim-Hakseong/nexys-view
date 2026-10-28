using System.IO;
using NexysView.Core.Data;
using NexysView.Core.Pyramid;

namespace NexysView.App.Services;

/// <summary>Undo/redo of UI actions (each action carries its inverse).</summary>
public sealed class UndoStack
{
    /// <summary>History depth. Each entry's closures keep the removed chart (PlotHost + its bitmap) or added curves alive, so an
    /// unbounded stack grew by 1.3 GB over 200 open/drop/close cycles (stability --stress).</summary>
    public const int MaxDepth = 50;
    readonly LinkedList<(string name, Action undo, Action redo)> _undo = new(); readonly Stack<(string name, Action undo, Action redo)> _redo = new();
    public event Action? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoName => _undo.Count > 0 ? _undo.Last!.Value.name : null;

    public void Push(string name, Action undo, Action redo) { _undo.AddLast((name, undo, redo)); while (_undo.Count > MaxDepth) _undo.RemoveFirst(); _redo.Clear(); Changed?.Invoke(); }
    public void Do(string name, Action redo, Action undo) { redo(); Push(name, undo, redo); }
    public void Undo() { if (_undo.Count == 0) return; var a = _undo.Last!.Value; _undo.RemoveLast(); a.undo(); _redo.Push(a); Changed?.Invoke(); }
    public void Redo() { if (_redo.Count == 0) return; var a = _redo.Pop(); a.redo(); _undo.AddLast(a); Changed?.Invoke(); }
    /// <summary>Drop all history (a closed file's channels must not be resurrected by undo).</summary>
    public void Clear() { _undo.Clear(); _redo.Clear(); Changed?.Invoke(); }
}

/// <summary>Open files, pyramid service, selection. Single instance per window.</summary>
public sealed class AppState
{
    public List<DataFile> Files { get; } = new();
    public PyramidService Pyramids { get; } = new();
    public UndoStack Undo { get; } = new();
    public event Action? FilesChanged;
    /// <summary>Raised before a file is disposed: charts, tables and analysis inputs must drop its channels (stability bug 1: AV in a chart reading a closed map).</summary>
    public event Action<DataFile>? FileClosing;
    public event Action<string>? Status;
    public bool DeveloperMode { get; set; }

    public Channel? FindChannel(string id) => Files.SelectMany(f => f.AllChannels).FirstOrDefault(c => c.Id == id);
    public DataFile? FileOf(Channel ch) => Files.FirstOrDefault(f => f.Name == ch.FileName);

    public DataFile Open(string path)
    {
        var existing = Files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var _ = Diag.Span("open " + Path.GetFileName(path));
        DataFile df = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".tdms" => TdmsDataSource.Open(path),
            ".tdm" => Core.Tdm.TdmFile.OpenDataFile(path),
            ".dxd" => Core.Dxd.DxdDataSource.Open(path),
            ".csv" or ".txt" or ".xyz" => Import.CsvDataSource.Open(path),
            ".mon" => Core.Mil1553.Mil1553DataSource.Open(path),
            _ => throw new NotSupportedException("Unsupported file type: " + Path.GetExtension(path)),
        };
        Files.Add(df);
        Status?.Invoke($"Opened {df.Name}: {df.Groups.Count} groups, {df.ChannelCount} channels in {sw.Elapsed.TotalMilliseconds:F0} ms");
        FilesChanged?.Invoke();
        return df;
    }

    public void Close(DataFile f)
    {
        Diag.Trace("close " + f.Name);
        FileClosing?.Invoke(f);
        Files.Remove(f);
        Undo.Clear();
        foreach (var ch in f.AllChannels) Pyramids.Cancel(ch);   // builds in flight stop before the map is unmapped
        f.Dispose(); FilesChanged?.Invoke();
    }

    /// <summary>Open a data file through a DataPlugin: .vbs script (in-process VBScript host) or .py (the Python marshaller worker).</summary>
    public DataFile OpenWithPlugin(string dataPath, string pluginPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        DataFile df = Path.GetExtension(pluginPath).ToLowerInvariant() switch
        {
            ".vbs" => Plugins.VbsDataPlugin.Open(dataPath, pluginPath),
            ".py" => Import.PluginDataSource.OpenWithPython(dataPath, pluginPath),
            ".dll" => Import.PluginDataSource.Open(dataPath, pluginPath),
            _ => throw new NotSupportedException("Plugin must be .vbs, .py or .dll"),
        };
        Files.Add(df);
        Status?.Invoke($"Opened {df.Name} via {Path.GetFileName(pluginPath)}: {df.ChannelCount} channels in {sw.Elapsed.TotalMilliseconds:F0} ms");
        FilesChanged?.Invoke();
        return df;
    }

    public Task<MinMaxPyramid> EnsurePyramid(Channel ch) => Pyramids.Ensure(ch, FileOf(ch)?.Path ?? ch.FileName);

    public AppState() { Pyramids.Log = Diag.Trace; }

    public void SetStatus(string s) => Status?.Invoke(s);
}
