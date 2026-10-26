using System.IO;
using System.Windows;
using NexysView.Core.Analysis;
using NexysView.Render;
using SkiaSharp;

namespace NexysView.App.Services;

/// <summary>REPORT: a block list (title / text / chart snapshot / value table) rendered to an A4 page with Skia (light paper
/// tokens), previewed as PNG in the page and exported as PNG (2×) or PDF.</summary>
public sealed class ReportService
{
    public sealed class Block { public string Kind = "Title"; public string Text = ""; public int ChartId; public string Name = ""; }
    public List<Block> Blocks { get; } = new();
    public const int PageW = 794, PageH = 1123;   // A4 @ 96 dpi
    readonly Engine _e;
    readonly Dictionary<string, Stats> _statsCache = new();
    static SKTypeface? _ui, _mono;
    public int Version { get; private set; }

    public ReportService(Engine e) { _e = e; }

    public void Add(string kind, int chartId = 0, string text = "")
    {
        var b = new Block { Kind = kind, ChartId = chartId, Text = text };
        if (kind == "Title" && text == "") b.Text = "Report";
        if (kind == "Text" && text == "") b.Text = "Text";
        if (kind is "Chart" or "Table") { var c = _e.Get(chartId == 0 ? _e.ActiveChart : chartId); if (c != null) { b.ChartId = c.Id; b.Name = string.IsNullOrEmpty(c.Model.Title) ? $"Chart {_e.Charts.IndexOf(c) + 1}" : c.Model.Title; } }
        Blocks.Add(b); Version++; _e.Touch();
    }
    public void Remove(int idx) { if (idx >= 0 && idx < Blocks.Count) { Blocks.RemoveAt(idx); Version++; _e.Touch(); } }
    public void Move(int idx, int dir) { int j = idx + dir; if (idx < 0 || idx >= Blocks.Count || j < 0 || j >= Blocks.Count) return; (Blocks[idx], Blocks[j]) = (Blocks[j], Blocks[idx]); Version++; _e.Touch(); }
    public void SetText(int idx, string text) { if (idx >= 0 && idx < Blocks.Count) { Blocks[idx].Text = text; Version++; _e.Touch(); } }
    public void Clear() { Blocks.Clear(); Version++; _e.Touch(); }
    public void Touch() => Version++;

    public object Dto() => new
    {
        version = Version,
        blocks = Blocks.Select((b, i) => new { idx = i, kind = b.Kind, text = b.Text, chartId = b.ChartId, name = b.Name, summary = b.Kind switch { "Chart" => $"Chart: {(_e.Get(b.ChartId)?.Model.Curves.Count ?? 0)} curves", "Table" => $"Table: {(_e.Get(b.ChartId)?.Model.Curves.Count ?? 0)} rows", _ => b.Text } }).ToList(),
        charts = _e.Charts.Select((c, i) => new { id = c.Id, name = string.IsNullOrEmpty(c.Model.Title) ? $"Chart {i + 1}" : c.Model.Title }).ToList(),
    };

    static SKTypeface? LoadFont(string file)
    {
        try { using var s = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/fonts/" + file))!.Stream; return SKTypeface.FromStream(s); } catch { return SKTypeface.Default; }
    }

    static readonly RenderTheme Paper = RenderTheme.FromHex("#FFFFFF", "#FAFAF8", "#E4E4E0", "#1A1A1A", "#6E6E6A", "#A8A8A3", "#E8601C", "#ECECE9");
    static readonly SKColor[] Pal = new[] { "#1F77B4", "#E8601C", "#2CA02C", "#9467BD", "#17BECF", "#D62728", "#8C564B", "#BCBD22" }.Select(SKColor.Parse).ToArray();

    void DrawPage(SKCanvas c, float scale, SKPixmap? px)
    {
        _ui ??= LoadFont("Pretendard-Regular.ttf"); _mono ??= LoadFont("JetBrainsMono-Regular.ttf");
        var th = Paper;
        c.Scale(scale);
        using var bg = new SKPaint { Color = th.Panel }; c.DrawRect(0, 0, PageW, PageH, bg);
        using var text = new SKPaint { Color = th.Text, IsAntialias = true };
        using var muted = new SKPaint { Color = th.TextMuted, IsAntialias = true };
        using var line = new SKPaint { Color = th.Border, StrokeWidth = 1 };
        float y = 56, x = 56, w = PageW - 112;
        var r = new PlotRenderer(th) { UiTypeface = _ui, MonoTypeface = _mono, FontSize = 10, Palette = i => Pal[i % Pal.Length] };
        foreach (var b in Blocks)
        {
            var model = b.Kind is "Chart" or "Table" ? _e.Get(b.ChartId)?.Model : null;
            switch (b.Kind)
            {
                case "Title":
                    using (var f = new SKFont(_ui, 16)) { c.DrawText(b.Text, x, y + 16, f, text); }
                    using (var f = new SKFont(_ui, 10)) { c.DrawText(DateTime.Now.ToString("yyyy-MM-dd HH:mm"), x, y + 32, f, muted); }
                    c.DrawLine(x, y + 40, x + w, y + 40, line); y += 56; break;
                case "Text":
                    using (var f = new SKFont(_ui, 11))
                    {
                        foreach (var ln in Wrap(b.Text, f, w)) { c.DrawText(ln, x, y + 12, f, text); y += 16; }
                        y += 8;
                    }
                    break;
                case "Chart" when model != null:
                    {
                        float h = 260;
                        using (var f = new SKFont(_ui, 10)) c.DrawText(string.IsNullOrEmpty(model.Title) ? b.Name : model.Title, x, y + 10, f, muted);
                        c.Save(); c.Translate(x, y + 16);
                        r.TargetPixmap = null;
                        r.Draw(c, new SKRect(0, 0, w, h), model);
                        c.Restore();
                        c.DrawRect(x, y + 16, w, h, new SKPaint { Color = th.Border, Style = SKPaintStyle.Stroke, StrokeWidth = 1 });
                        y += h + 28; break;
                    }
                case "Table" when model != null:
                    {
                        using var f = new SKFont(_ui, 10); using var fm = new SKFont(_mono, 10);
                        string[] cols = { "Channel", "Unit", "Samples", "Min", "Max", "Mean", "RMS" };
                        float[] cw = { 0.30f, 0.08f, 0.14f, 0.12f, 0.12f, 0.12f, 0.12f };
                        float cx = x;
                        for (int i = 0; i < cols.Length; i++) { c.DrawText(cols[i].ToUpperInvariant(), cx, y + 10, f, muted); cx += cw[i] * w; }
                        y += 16; c.DrawLine(x, y, x + w, y, line); y += 4;
                        foreach (var cv in model.Curves)
                        {
                            if (!_statsCache.TryGetValue(cv.Channel.Id, out var s)) { s = Dsp.StatisticsOf(cv.Channel.Source); _statsCache[cv.Channel.Id] = s; }
                            string[] row = { cv.Channel.DisplayName, cv.Channel.Unit, cv.Channel.Length.ToString("N0"), Ticks.FormatValue(s.Min, 5), Ticks.FormatValue(s.Max, 5), Ticks.FormatValue(s.Mean, 5), Ticks.FormatValue(s.Rms, 5) };
                            cx = x;
                            for (int i = 0; i < row.Length; i++) { c.DrawText(row[i], cx, y + 11, i == 0 ? f : fm, text); cx += cw[i] * w; }
                            y += 16;
                        }
                        y += 12; break;
                    }
            }
            if (y > PageH - 60) break;
        }
        using (var f = new SKFont(_ui, 9)) c.DrawText("Nexys VIEW", x, PageH - 24, f, muted);
    }

    static IEnumerable<string> Wrap(string t, SKFont f, float w)
    {
        foreach (var para in t.Split('\n'))
        {
            var line = ""; foreach (var word in para.Split(' '))
            {
                var cand = line.Length == 0 ? word : line + " " + word;
                if (f.MeasureText(cand) > w && line.Length > 0) { yield return line; line = word; } else line = cand;
            }
            yield return line;
        }
    }

    /// <summary>Page preview as PNG bytes at <paramref name="widthPx"/> pixels wide.</summary>
    public byte[] PreviewPng(int widthPx)
    {
        widthPx = Math.Clamp(widthPx, 200, PageW * 2);
        float scale = widthPx / (float)PageW;
        using var bmp = new SKBitmap(new SKImageInfo(widthPx, (int)(PageH * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var c = new SKCanvas(bmp);
        DrawPage(c, scale, bmp.PeekPixels());
        using var img = SKImage.FromBitmap(bmp); using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    public void ExportTo(string path, bool pdf)
    {
        if (pdf)
        {
            using var doc = SKDocument.CreatePdf(path);
            using var c = doc.BeginPage(595, 842);
            DrawPage(c, 595f / PageW, null);
            doc.EndPage(); doc.Close();
        }
        else
        {
            using var bmp = new SKBitmap(new SKImageInfo(PageW * 2, PageH * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var c = new SKCanvas(bmp);
            DrawPage(c, 2f, bmp.PeekPixels());
            using var img = SKImage.FromBitmap(bmp); using var data = img.Encode(SKEncodedImageFormat.Png, 95);
            using var fs = File.Create(path); data.SaveTo(fs);
        }
    }
}
