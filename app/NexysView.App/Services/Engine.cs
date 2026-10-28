using System.IO;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>Nexys VIEW's engine: the shared chart/file engine (<see cref="EngineBase"/>, nexys-core) plus the VIEW-only services
/// (analysis, report, .tdv layouts, CSV/TDMS range export).</summary>
public sealed class Engine : EngineBase
{
    public AnalysisService Analysis { get; }
    public ReportService Report { get; }
    public Engine() { Analysis = new AnalysisService(this); Report = new ReportService(this); }
    /// <summary>1.0.0-rc.2: a bus capture (.mon) was given — the page shows the "open in NexysBus" notice instead of loading it.</summary>
    public string? BusNotice { get; set; }

    protected override bool TryOpenSpecial(string path)
    {
        if (path.EndsWith(".mon", StringComparison.OrdinalIgnoreCase)) { BusNotice = path; SetStatus($"{Path.GetFileName(path)}: bus captures open in NexysBus (ANALYZE)"); Touch(); return true; }
        if (!path.EndsWith(".tdv", StringComparison.OrdinalIgnoreCase)) return false;
        try { int n = Import.TdvImport.Apply(this, path); SetStatus($"Layout {Path.GetFileName(path)}: {n} areas"); Mode = "VIEW"; }
        catch (Exception ex) { SetStatus("Layout import failed: " + ex.Message); }
        return true;
    }
    protected override void OnFileRemoved(DataFile f) => Analysis.OnFileClosed(f);
    protected override int ExportChannels(string path, bool tdms, List<Channel> chans, long first, long last, Curve? cv0) => tdms ? Export.TdmsExport.SaveRange(path, chans, first, last) : Export.CsvExport.Write(path, chans, first, last, cv0);
    protected override void ExtendSnapshot(Dictionary<string, object?> s) { s["analysis"] = Analysis.Dto(); s["report"] = Report.Dto(); s["busNotice"] = BusNotice; }
}
