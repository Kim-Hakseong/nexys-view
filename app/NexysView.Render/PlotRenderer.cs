using System.Diagnostics;
using NexysView.Core.Data;
using NexysView.Core.Pyramid;
using SkiaSharp;

namespace NexysView.Render;

/// <summary>Per-frame statistics for the developer status bar.</summary>
public sealed class FrameStats
{
    public double TotalMs, QueryMs, DrawMs;
    public int Columns, Curves, LevelMin = int.MaxValue, LevelMax = int.MinValue;
    public override string ToString() => $"{TotalMs:F1} ms (query {QueryMs:F1}, draw {DrawMs:F1}) {Curves} curves × {Columns} cols L{(LevelMin == int.MaxValue ? "-" : LevelMin.ToString())}..{(LevelMax == int.MinValue ? "-" : LevelMax.ToString())}";
}

/// <summary>
/// Skia renderer for PlotModel: pixel-column min/max envelope per curve (raw polyline when zoomed in), grid, axes,
/// legend, cursors. One Draw() = one frame; all data access is through the pyramid (no allocation per column).
/// Design-pass: envelope fill alpha 0.22 with opaque 1px min/max edge lines, 1.25px AA lines when zoomed in,
/// 16px inner padding, 3–7 nice ticks, legend as a collapsible pill (top-right), cursor pill labels,
/// cursor read-out exposed as text (CursorBand) for the chart header instead of being drawn over the plot.
/// </summary>
public sealed class PlotRenderer
{
    public RenderTheme Theme { get; set; }
    public SKTypeface? UiTypeface { get; set; }
    public SKTypeface? MonoTypeface { get; set; }
    public float FontSize { get; set; } = 11f;
    public float AxisWidth { get; set; } = 48f;
    public float Padding { get; set; } = 16f;
    /// <summary>Envelope fill alpha (0.22 · 255); the min/max edge pixels of every column stay opaque.</summary>
    public byte FillAlpha { get; set; } = 56;
    public FrameStats LastFrame { get; } = new();
    public bool DeveloperMode { get; set; }
    /// <summary>Cursor read-out (values at A/B, Δ, segment stats) for the chart header; empty without cursors.</summary>
    public string CursorBand { get; private set; } = "";
    /// <summary>Structured cursor read-outs for the chart-header readout strip (one per visible curve).</summary>
    public sealed record Readout(string Name, string Unit, SKColor Color, double? A, double? B, double? Delta, (double min, double max, double mean)? Segment);
    public List<Readout> Readouts { get; } = new();
    public double? DeltaX { get; private set; }
    /// <summary>Trace color source (defaults to the application palette; the report page passes its own).</summary>
    public Func<int, SKColor> Palette { get; set; } = CurvePalette.Get;
    /// <summary>v3: dot grid instead of lines (light plot), rounded plot corners (card), lane height for digital lanes.</summary>
    public bool DotGrid { get; set; }
    public float PlotRadius { get; set; }
    public const float LaneH = 16f;
    public float CursorLineAlpha { get; set; } = 1f;

    float[] _mn = new float[4096], _mx = new float[4096];
    float[] _xs = new float[4096], _ys = new float[4096];
    readonly SKPaint _line = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    readonly SKPaint _poly = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.25f, StrokeJoin = SKStrokeJoin.Bevel, StrokeCap = SKStrokeCap.Butt };
    readonly SKPaint _dots = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3.5f, StrokeCap = SKStrokeCap.Round };
    readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    readonly SKPaint _aaFill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    readonly SKPaint _aaLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    readonly SKPaint _text = new() { IsAntialias = true };
    readonly SKFont _font = new();
    readonly SKFont _mono = new();

    public PlotRenderer(RenderTheme theme) { Theme = theme; }

    /// <summary>Pixels of the raster target of the current Draw (SKSurface.PeekPixels / SKBitmap.PeekPixels). Enables the direct column fill.</summary>
    public SKPixmap? TargetPixmap { get; set; }

    sealed class EnvelopeJob { public SKColor Color; public int X0, Cols; public int[] Y0 = new int[4096], Y1 = new int[4096]; public byte[] Op = new byte[4096]; }
    readonly List<EnvelopeJob> _jobs = new();
    readonly List<EnvelopeJob> _pool = new();

    EnvelopeJob RentJob(int cols)
    {
        EnvelopeJob j;
        if (_pool.Count > _jobs.Count) j = _pool[_jobs.Count]; else { j = new EnvelopeJob(); _pool.Add(j); }
        if (j.Y0.Length < cols) { j.Y0 = new int[cols]; j.Y1 = new int[cols]; j.Op = new byte[cols]; }
        _jobs.Add(j);
        return j;
    }

    /// <summary>Direct column fill: columns split into bands processed in parallel; curves drawn in order within a band (deterministic).
    /// A column whose own min/max span is thin (a line, Op=1) is written opaque including its bridge to the previous column;
    /// a wide (noisy) span gets opaque edge pixels and an alpha-blended interior (FillAlpha) so overlapping curves stay visible.</summary>
    unsafe void RasterFill(SKPixmap px, SKRect plot)
    {
        int W = px.Width, H = px.Height, rowBytes = px.RowBytes;
        byte* basePtr = (byte*)px.GetPixels();
        if (basePtr == null || px.ColorType != SKColorType.Bgra8888) return;
        int left = Math.Max(0, (int)plot.Left), right = Math.Min(W, (int)plot.Right);
        int bands = Math.Clamp(Environment.ProcessorCount, 1, 16);
        int bandW = Math.Max(32, (right - left + bands - 1) / bands);
        var jobs = _jobs.ToArray();
        var argb = new uint[jobs.Length];
        for (int k = 0; k < jobs.Length; k++) { var c = jobs[k].Color; argb[k] = 0xFF000000u | (uint)c.Red << 16 | (uint)c.Green << 8 | c.Blue; }
        uint fa = FillAlpha, ia = 255u - fa;
        int top = Math.Max(0, (int)plot.Top), bottom = Math.Min(H - 1, (int)plot.Bottom - 1);
        // pin the job arrays for the whole fill: raw pointers into movable managed arrays would dangle after a GC compaction
        var pins = new System.Runtime.InteropServices.GCHandle[jobs.Length * 3];
        var py0 = new IntPtr[jobs.Length]; var py1 = new IntPtr[jobs.Length]; var pop = new IntPtr[jobs.Length];
        for (int k = 0; k < jobs.Length; k++)
        {
            pins[3 * k] = System.Runtime.InteropServices.GCHandle.Alloc(jobs[k].Y0, System.Runtime.InteropServices.GCHandleType.Pinned); py0[k] = pins[3 * k].AddrOfPinnedObject();
            pins[3 * k + 1] = System.Runtime.InteropServices.GCHandle.Alloc(jobs[k].Y1, System.Runtime.InteropServices.GCHandleType.Pinned); py1[k] = pins[3 * k + 1].AddrOfPinnedObject();
            pins[3 * k + 2] = System.Runtime.InteropServices.GCHandle.Alloc(jobs[k].Op, System.Runtime.InteropServices.GCHandleType.Pinned); pop[k] = pins[3 * k + 2].AddrOfPinnedObject();
        }
        try
        {
        Parallel.For(0, (right - left + bandW - 1) / bandW, new ParallelOptions { MaxDegreeOfParallelism = bands }, b =>
        {
            int xa = left + b * bandW, xb = Math.Min(right, xa + bandW);
            // row-major within the band: sequential memory per row, curves in order (later curves on top)
            for (int y = top; y <= bottom; y++)
            {
                uint* row = (uint*)(basePtr + (long)y * rowBytes);
                for (int k = 0; k < jobs.Length; k++)
                {
                    var j = jobs[k];
                    uint src = argb[k], srb = (src & 0x00FF00FFu) * fa, sg = (src & 0x0000FF00u) * fa;
                    int ca = Math.Max(xa, j.X0), cb = Math.Min(xb, j.X0 + j.Cols);
                    int* y0 = (int*)py0[k]; int* y1 = (int*)py1[k]; byte* op = (byte*)pop[k];
                    int i = ca - j.X0;
                    for (int x = ca; x < cb; x++, i++)
                    {
                        int a = y0[i], bb = y1[i];
                        if (y < a || y > bb) continue;
                        if (op[i] != 0 || y == a || y == bb) { row[x] = src; continue; }
                        uint d = row[x];
                        uint rb = (((d & 0x00FF00FFu) * ia + srb) >> 8) & 0x00FF00FFu;
                        uint g = (((d & 0x0000FF00u) * ia + sg) >> 8) & 0x0000FF00u;
                        row[x] = 0xFF000000u | rb | g;
                    }
                }
            }
        });
        }
        finally { foreach (var h in pins) if (h.IsAllocated) h.Free(); }
    }

    public sealed record Layout(SKRect Plot, SKRect Full, float LeftAxes, float RightAxes);

    public Layout ComputeLayout(PlotModel m, SKRect full)
    {
        int left = m.YAxes.Count(a => !a.Right), right = m.YAxes.Count(a => a.Right);
        if (m.YAxes.Count == 0) left = 1;
        float l = Padding + left * AxisWidth, r = Padding + right * AxisWidth;
        float lanes = LanesOf(m) * LaneH + (LanesOf(m) > 0 ? 4 : 0);
        var plot = new SKRect(full.Left + l, full.Top + Padding, full.Right - r, full.Bottom - Padding - 18 - lanes);
        return new Layout(plot, full, l, r);
    }

    static int LanesOf(PlotModel m) => m.DigitalLanes ? m.Curves.Count(c => c.Visible && c.Step && !(c.XChannel != null && !c.XMonotonic)) : 0;
    static bool InLane(PlotModel m, Curve c) => m.DigitalLanes && c.Step && !(c.XChannel != null && !c.XMonotonic);

    public void Draw(SKCanvas canvas, SKRect full, PlotModel m)
    {
        var sw = Stopwatch.StartNew();
        LastFrame.QueryMs = LastFrame.DrawMs = 0; LastFrame.LevelMin = int.MaxValue; LastFrame.LevelMax = int.MinValue; LastFrame.Curves = 0;
        _font.Typeface = UiTypeface; _font.Size = FontSize; _mono.Typeface = MonoTypeface ?? UiTypeface; _mono.Size = FontSize;
        var lay = ComputeLayout(m, full);
        var plot = lay.Plot;
        int W = Math.Max(1, (int)plot.Width);
        LastFrame.Columns = W;
        if (_mn.Length < W) { _mn = new float[W]; _mx = new float[W]; }
        if (_xs.Length < W * 2 + 8) { _xs = new float[W * 2 + 8]; _ys = new float[W * 2 + 8]; }

        _fill.Color = Theme.Panel; canvas.DrawRect(full, _fill);
        _aaFill.Color = Theme.PlotBg; _fill.Color = Theme.PlotBg;   // plot area uses the plot token set, chrome stays panel
        if (PlotRadius > 0) canvas.DrawRoundRect(plot, PlotRadius, PlotRadius, _aaFill); else canvas.DrawRect(plot, _fill);
        AutoScaleY(m, W);
        DrawGridAndAxes(canvas, m, lay);

        canvas.Save();
        canvas.ClipRect(plot);
        _jobs.Clear();
        foreach (var c in m.Curves)
        {
            if (!c.Visible || InLane(m, c)) continue;
            LastFrame.Curves++;
            if (c.XChannel != null && !c.XMonotonic) DrawXY(canvas, c, m, plot); else DrawEnvelope(canvas, c, m, plot, W);
        }
        if (_jobs.Count > 0 && TargetPixmap != null)
        {
            canvas.Flush();
            var d = Stopwatch.StartNew();
            RasterFill(TargetPixmap, plot);
            LastFrame.DrawMs += d.Elapsed.TotalMilliseconds;
        }
        canvas.Restore();
        if (PlotRadius > 0)
        {
            // the direct raster fill ignores the clip: paint the panel back over the rounded corners
            canvas.Save(); canvas.ClipRoundRect(new SKRoundRect(plot, PlotRadius), SKClipOperation.Difference, true);
            _fill.Color = Theme.Panel; canvas.DrawRect(new SKRect(plot.Left - 1, plot.Top - 1, plot.Right + 1, plot.Bottom + 1), _fill); canvas.Restore();
            _aaLine.Color = Theme.PlotFrame; _aaLine.StrokeWidth = 1; canvas.DrawRoundRect(new SKRect(plot.Left + 0.5f, plot.Top + 0.5f, plot.Right - 0.5f, plot.Bottom - 0.5f), PlotRadius, PlotRadius, _aaLine);
        }
        DrawLanes(canvas, m, lay, W);
        DrawCursors(canvas, m, lay);
        if (m.ShowLegend && m.Curves.Count > 0) DrawLegend(canvas, m, plot);
        LastFrame.TotalMs = sw.Elapsed.TotalMilliseconds;
    }

    // ------------------------------------------------------------------ scaling
    void AutoScaleY(PlotModel m, int W)
    {
        foreach (var a in m.YAxes)
        {
            if (!a.Auto) continue;
            double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
            foreach (var c in m.Curves)
            {
                if (!c.Visible || c.Axis != a || InLane(m, c)) continue;
                var p = c.Channel.Pyramid;
                if (p == null) continue;
                if (c.XChannel != null && !c.XMonotonic) { if (!double.IsNaN(p.GlobalMin)) { mn = Math.Min(mn, p.GlobalMin); mx = Math.Max(mx, p.GlobalMax); } continue; }
                var (s0, s1) = SampleWindow(c, m);
                int cols = Math.Min(W, 512);
                p.Envelope(c.Channel.Source, s0, s1, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out _);
                for (int i = 0; i < cols; i++) { if (float.IsNaN(_mn[i])) continue; if (_mn[i] < mn) mn = _mn[i]; if (_mx[i] > mx) mx = _mx[i]; }
            }
            if (mn == double.PositiveInfinity) { mn = -1; mx = 1; }
            if (mx - mn < 1e-12) { mn -= 0.5; mx += 0.5; }
            double pad = (mx - mn) * 0.05;
            a.Min = mn - pad; a.Max = mx + pad;
        }
    }

    /// <summary>Sample window of a curve for the current x window: implicit x via the channel, mapped x via a binary search on the
    /// x channel (v3 B1). Columns inside the window assume uniform sampling (exact for constant-rate loggers).</summary>
    static (double s0, double s1) SampleWindow(Curve c, PlotModel m)
    {
        if (c.XMonotonic && c.XChannel != null)
            return (XIndex.IndexOf(c.XChannel, (m.XMin - c.XOffset) / c.XScale), XIndex.IndexOf(c.XChannel, (m.XMax - c.XOffset) / c.XScale));
        return (c.Channel.IndexOf(m.XMin - c.XOffset), c.Channel.IndexOf(m.XMax - c.XOffset));
    }

    // ------------------------------------------------------------------ curves
    void DrawEnvelope(SKCanvas canvas, Curve c, PlotModel m, SKRect plot, int W)
    {
        var ch = c.Channel; var p = ch.Pyramid;
        if (p == null) return;
        var (s0, s1) = SampleWindow(c, m);
        if (s1 <= 0 || s0 >= ch.Length) return;
        var q = Stopwatch.StartNew();
        // clip the sample window to the channel; map columns accordingly
        double spp = (s1 - s0) / W;
        int c0 = (int)Math.Max(0, Math.Floor((0 - s0) / spp)), c1 = (int)Math.Min(W, Math.Ceiling((ch.Length - s0) / spp));
        int cols = c1 - c0;
        if (cols <= 0) return;
        double q0 = s0 + c0 * spp, q1 = s0 + c1 * spp;
        p.Envelope(ch.Source, q0, q1, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out int level);
        LastFrame.QueryMs += q.Elapsed.TotalMilliseconds;
        LastFrame.LevelMin = Math.Min(LastFrame.LevelMin, level); LastFrame.LevelMax = Math.Max(LastFrame.LevelMax, level);
        var d = Stopwatch.StartNew();
        var color = Palette(c.ColorIndex);
        double ymin = c.Axis.Min, ymax = c.Axis.Max;
        float H = plot.Height, top = plot.Top;
        float scale = (float)(H / (ymax - ymin));
        bool rawMode = spp < 6.0;   // ≤6 samples/px: polyline through the samples (envelope columns look beaded on short logs)
        if (rawMode)
        {
            // fewer samples than columns: anti-aliased 1.25px polyline through actual samples
            long first = Math.Max(0, (long)Math.Floor(s0)), last = Math.Min(ch.Length - 1, (long)Math.Ceiling(s1));
            int n = (int)(last - first + 1);
            if (n > _xs.Length) { _xs = new float[n]; _ys = new float[n]; }
            var buf = System.Buffers.ArrayPool<double>.Shared.Rent(n);
            int got = ch.Source.Read(first, buf.AsSpan(0, n));
            double[]? xb = null;
            if (c.XMonotonic && c.XChannel != null) { xb = System.Buffers.ArrayPool<double>.Shared.Rent(n); c.XChannel.Source.Read(first, xb.AsSpan(0, n)); }
            int k = 0;
            double xr = m.XMax - m.XMin;
            for (int i = 0; i < got; i++)
            {
                double v = buf[i];
                if (double.IsNaN(v)) continue;
                float x = xb != null ? plot.Left + (float)((xb[i] * c.XScale + c.XOffset - m.XMin) / xr * plot.Width) : plot.Left + (float)((first + i - s0) / spp);
                _xs[k] = x; _ys[k] = top + (float)((ymax - v) * scale); k++;
            }
            System.Buffers.ArrayPool<double>.Shared.Return(buf);
            if (xb != null) System.Buffers.ArrayPool<double>.Shared.Return(xb);
            using var path = new SKPath();
            if (k > 0)
            {
                path.MoveTo(_xs[0], _ys[0]);
                if (c.Step) for (int i = 1; i < k; i++) { path.LineTo(_xs[i], _ys[i - 1]); path.LineTo(_xs[i], _ys[i]); }   // sample-and-hold
                else for (int i = 1; i < k; i++) path.LineTo(_xs[i], _ys[i]);
            }
            if (c.Width > 1.25f) { _poly.Color = color; _poly.StrokeWidth = c.Width; canvas.DrawPath(path, _poly); }
            else
            {
                // ~1.25 px AA look at hairline cost: an AA hairline plus a half-pixel-offset hairline at 45% alpha
                // (a real 1.25 px stroke is a filled outline path: 10× slower on 1000-point curves, P3 gate)
                _poly.Color = color; _poly.StrokeWidth = 1; canvas.DrawPath(path, _poly);
                canvas.Save(); canvas.Translate(0, 0.6f); _poly.Color = color.WithAlpha(115); canvas.DrawPath(path, _poly); canvas.Restore();
            }
            if (spp < 0.25)
            {
                // sample markers in one call (round-capped points)
                var pts = new SKPoint[k];
                for (int i = 0; i < k; i++) pts[i] = new SKPoint(_xs[i], _ys[i]);
                _dots.Color = color; canvas.DrawPoints(SKPointMode.Points, pts, _dots);
            }
        }
        else
        {
            // column spans [y0,y1] in pixels; bridged to the previous column so thin/sloped signals stay continuous
            var job = RentJob(cols);
            job.Color = color; job.X0 = (int)plot.Left + c0; job.Cols = cols;
            float prevMin = float.NaN, prevMax = float.NaN;
            int yTop = (int)plot.Top, yBot = (int)plot.Bottom - 1;
            for (int i = 0; i < cols; i++)
            {
                float mn = _mn[i], mx = _mx[i];
                if (float.IsNaN(mn)) { job.Y0[i] = 1; job.Y1[i] = 0; prevMin = float.NaN; continue; }
                float y0 = top + (float)(ymax - mx) * scale, y1 = top + (float)(ymax - mn) * scale;
                job.Op[i] = y1 - y0 <= 8f ? (byte)1 : (byte)0;   // own span ≤ 8 px = a line: opaque incl. bridge; wider = noise band (alpha interior)
                if (!float.IsNaN(prevMin))
                {
                    if (mn > prevMax) y1 = top + (float)(ymax - prevMax) * scale;
                    else if (mx < prevMin) y0 = top + (float)(ymax - prevMin) * scale;
                }
                int a = Math.Max(yTop, (int)MathF.Floor(y0)), b = Math.Min(yBot, (int)MathF.Floor(y1));
                if (b < a) b = a;
                job.Y0[i] = a; job.Y1[i] = b;
                prevMin = mn; prevMax = mx;
            }
            if (TargetPixmap == null)
            {
                // no direct pixel access: fall back to Skia rects (translucent span + opaque edge pixels)
                for (int i = 0; i < cols; i++)
                {
                    if (job.Y1[i] < job.Y0[i]) continue;
                    _fill.Color = job.Op[i] != 0 ? color : color.WithAlpha(FillAlpha); canvas.DrawRect(job.X0 + i, job.Y0[i], 1, job.Y1[i] - job.Y0[i] + 1, _fill);
                    _fill.Color = color; canvas.DrawRect(job.X0 + i, job.Y0[i], 1, 1, _fill); canvas.DrawRect(job.X0 + i, job.Y1[i], 1, 1, _fill);
                }
                _jobs.RemoveAt(_jobs.Count - 1);
            }
        }
        LastFrame.DrawMs += d.Elapsed.TotalMilliseconds;
    }

    void DrawXY(SKCanvas canvas, Curve c, PlotModel m, SKRect plot)
    {
        var xc = c.XChannel!; var yc = c.Channel;
        long n = Math.Min(xc.Length, yc.Length);
        if (n <= 0) return;
        int maxPts = 200_000;
        long step = Math.Max(1, n / maxPts);
        var bx = System.Buffers.ArrayPool<double>.Shared.Rent((int)Math.Min(n, 1 << 20));
        var by = System.Buffers.ArrayPool<double>.Shared.Rent((int)Math.Min(n, 1 << 20));
        double xr = m.XMax - m.XMin, yr = c.Axis.Max - c.Axis.Min;
        using var path = new SKPath();
        bool first = true;
        long pos = 0;
        while (pos < n)
        {
            int blk = (int)Math.Min(bx.Length, n - pos);
            xc.Source.Read(pos, bx.AsSpan(0, blk)); yc.Source.Read(pos, by.AsSpan(0, blk));
            for (int i = 0; i < blk; i += (int)step)
            {
                if (double.IsNaN(bx[i]) || double.IsNaN(by[i])) { first = true; continue; }
                float px = plot.Left + (float)((bx[i] - m.XMin) / xr * plot.Width);
                float py = plot.Top + (float)((c.Axis.Max - by[i]) / yr * plot.Height);
                if (first) { path.MoveTo(px, py); first = false; } else path.LineTo(px, py);
            }
            pos += blk;
        }
        System.Buffers.ArrayPool<double>.Shared.Return(bx); System.Buffers.ArrayPool<double>.Shared.Return(by);
        var xyColor = Palette(c.ColorIndex);
        _poly.Color = xyColor; _poly.StrokeWidth = Math.Max(1f, c.Width); canvas.DrawPath(path, _poly);
        if (c.Width <= 1.25f) { canvas.Save(); canvas.Translate(0, 0.6f); _poly.Color = xyColor.WithAlpha(115); canvas.DrawPath(path, _poly); canvas.Restore(); }
    }

    // ------------------------------------------------------------------ chrome
    static int TickTarget(float px, float per) => Math.Clamp((int)(px / per), 3, 7);

    void DrawGridAndAxes(SKCanvas canvas, PlotModel m, Layout lay)
    {
        var plot = lay.Plot;
        _line.Color = Theme.PlotGrid; _line.StrokeWidth = 1;
        int xt = TickTarget(plot.Width, 110);
        bool timeAxis = m.XTimeMode != "none";
        double xstep = timeAxis ? TimeAxis.NiceStep(m.XMax - m.XMin, xt) : Ticks.NiceStep(m.XMax - m.XMin, xt);
        float lanes = LanesOf(m) * LaneH + (LanesOf(m) > 0 ? 4 : 0);
        float tickY = plot.Bottom + lanes + 15;
        var gridX = new List<float>(); var gridY = new List<float>();
        float lastTickRight = plot.Left;
        foreach (var v in timeAxis ? TimeTicks(m.XMin, m.XMax, xstep) : Ticks.Generate(m.XMin, m.XMax, xt))
        {
            float x = plot.Left + (float)((v - m.XMin) / (m.XMax - m.XMin) * plot.Width);
            if (m.ShowGrid) { if (DotGrid) gridX.Add(x); else { _line.Color = Theme.PlotGrid; canvas.DrawLine(x, plot.Top, x, plot.Bottom, _line); } }
            var s = FormatX(m, v, xstep);
            float w = _mono.MeasureText(s);
            _text.Color = Theme.TextMuted; canvas.DrawText(s, x - w / 2, tickY, _mono, _text);
            lastTickRight = Math.Max(lastTickRight, x + w / 2);
        }
        // x unit title: sans, muted; right after the last tick label when it fits, else on the line below (design-review-1 #2)
        string xunit = m.XTimeMode == "absolute" ? TimeAxis.DateOf(m.XMin) : m.XTimeMode == "relative" ? "rel " + m.XUnit : m.XUnit;
        if (!string.IsNullOrEmpty(xunit))
        {
            float uw = _font.MeasureText(xunit);
            float ux = Math.Max(lastTickRight + 10, plot.Right - uw);
            _text.Color = Theme.TextMuted;   // outside the plot rect: chrome text
            if (ux + uw <= lay.Full.Right - 4) canvas.DrawText(xunit, ux, tickY, _font, _text);
            else canvas.DrawText(xunit, plot.Right - uw, tickY + 13, _font, _text);
        }
        int li = 0, ri = 0;
        var axes = m.YAxes.Count == 0 ? new List<YAxis> { new("y0") } : m.YAxes;
        int yt = TickTarget(plot.Height, 46);
        foreach (var a in axes)
        {
            bool primary = ReferenceEquals(a, axes[0]);
            float ax = a.Right ? plot.Right + ri++ * AxisWidth : plot.Left - li++ * AxisWidth;
            double ystep = Ticks.NiceStep(a.Max - a.Min, yt);
            var axisColor = axes.Count > 1 ? Palette(m.Curves.FirstOrDefault(c => c.Axis == a)?.ColorIndex ?? 0) : Theme.TextMuted;
            foreach (var v in Ticks.Generate(a.Min, a.Max, yt))
            {
                float y = plot.Top + (float)((a.Max - v) / (a.Max - a.Min) * plot.Height);
                if (primary && m.ShowGrid) { if (DotGrid) gridY.Add(y); else { _line.Color = Theme.PlotGrid; canvas.DrawLine(plot.Left, y, plot.Right, y, _line); } }
                var s = Ticks.Format(v, ystep);
                float w = _mono.MeasureText(s);
                _text.Color = axisColor;
                if (a.Right) canvas.DrawText(s, ax + 6, y + 4, _mono, _text); else canvas.DrawText(s, ax - 6 - w, y + 4, _mono, _text);
            }
            if (!string.IsNullOrEmpty(a.Unit))
            {
                _text.Color = Theme.TextMuted;
                float ux = a.Right ? ax + 6 : ax - 6 - _font.MeasureText(a.Unit);
                canvas.DrawText(a.Unit, ux, plot.Top - 5, _font, _text);
            }
            _line.Color = Theme.PlotFrame;
            canvas.DrawLine(ax, plot.Top, ax, plot.Bottom, _line);
        }
        if (DotGrid && m.ShowGrid && gridX.Count > 1 && gridY.Count > 1)
        {
            // dot grid (DESIGN-v3 light plot): dots at every 1/5 of the tick spacing in both directions
            float dx = (gridX[1] - gridX[0]) / 5f, dy = (gridY[1] - gridY[0]) / 5f;
            var pts = new List<SKPoint>();
            for (float x = gridX[0] - 5 * dx; x <= plot.Right; x += dx) { if (x < plot.Left) continue; for (float y = gridY[0] - 5 * dy; y <= plot.Bottom; y += dy) { if (y < plot.Top) continue; pts.Add(new SKPoint(x, y)); } }
            _dots.Color = Theme.PlotGrid; _dots.StrokeWidth = 1.6f; canvas.DrawPoints(SKPointMode.Points, pts.ToArray(), _dots);
        }
        if (PlotRadius <= 0)
        {
            _line.Color = Theme.PlotFrame;
            canvas.DrawRect(new SKRect(plot.Left + 0.5f, plot.Top + 0.5f, plot.Right - 0.5f, plot.Bottom - 0.5f), new SKPaint { Color = Theme.PlotFrame, Style = SKPaintStyle.Stroke, StrokeWidth = 1 });
        }
    }

    static IEnumerable<double> TimeTicks(double min, double max, double step)
    {
        double start = Math.Ceiling(min / step) * step;
        int n = (int)Math.Min(Ticks.MaxTicks, Math.Floor((max - start) / step + 1e-9) + 1);
        for (int i = 0; i < n; i++) yield return start + i * step;
    }

    /// <summary>x label in the model's time mode (numeric / absolute clock / relative stopwatch).</summary>
    public string FormatX(PlotModel m, double x, double step = 0)
    {
        if (step <= 0) step = Math.Max(1e-3, (m.XMax - m.XMin) / 1000);
        return m.XTimeMode switch
        {
            "absolute" => TimeAxis.FormatAbsolute(x, step),
            "relative" => TimeAxis.FormatRelative(x - m.XTimeOrigin, step),
            _ => step >= (m.XMax - m.XMin) / 1000 ? Ticks.Format(x, step) : Ticks.FormatValue(x, 6),
        };
    }

    /// <summary>Digital lanes (v3 B2): each step curve as a 16px lane under the plot; filled where the column max is above mid-range.</summary>
    void DrawLanes(SKCanvas canvas, PlotModel m, Layout lay, int W)
    {
        int lanes = LanesOf(m); if (lanes == 0) return;
        var plot = lay.Plot;
        float y = plot.Bottom + 4; int li = 0;
        foreach (var c in m.Curves)
        {
            if (!c.Visible || !InLane(m, c)) continue;
            var rect = new SKRect(plot.Left, y + li * LaneH, plot.Right, y + li * LaneH + LaneH - 2);
            _fill.Color = Theme.PlotBg; canvas.DrawRect(rect, _fill);
            var p = c.Channel.Pyramid;
            if (p != null)
            {
                var (s0, s1) = SampleWindow(c, m);
                double spp = (s1 - s0) / W;
                int c0 = (int)Math.Max(0, Math.Floor((0 - s0) / spp)), c1 = (int)Math.Min(W, Math.Ceiling((c.Channel.Length - s0) / spp));
                int cols = c1 - c0;
                if (cols > 0)
                {
                    p.Envelope(c.Channel.Source, s0 + c0 * spp, s0 + c1 * spp, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out _);
                    double lo = double.IsNaN(p.GlobalMin) ? 0 : p.GlobalMin, hi = double.IsNaN(p.GlobalMax) ? 1 : p.GlobalMax, mid = lo + (hi - lo) / 2;
                    _fill.Color = Palette(c.ColorIndex).WithAlpha(200);
                    int runStart = -1;
                    for (int i = 0; i <= cols; i++)
                    {
                        bool on = i < cols && !float.IsNaN(_mx[i]) && (hi > lo ? _mx[i] > mid : _mx[i] != 0);
                        if (on && runStart < 0) runStart = i;
                        if (!on && runStart >= 0) { canvas.DrawRect(plot.Left + c0 + runStart, rect.Top + 2, i - runStart, rect.Height - 4, _fill); runStart = -1; }
                    }
                }
            }
            string lbl = m.LabelOf(c); float lw = _font.MeasureText(lbl);
            _fill.Color = Theme.PlotBg.WithAlpha(230); canvas.DrawRect(rect.Left + 2, rect.Top + 1, lw + 6, rect.Height - 2, _fill);
            _text.Color = Theme.PlotAxisText; canvas.DrawText(lbl, rect.Left + 5, rect.Bottom - 3, _font, _text);
            _line.Color = Theme.PlotFrame; canvas.DrawLine(rect.Left, rect.Bottom + 0.5f, rect.Right, rect.Bottom + 0.5f, _line);
            li++;
        }
    }

    // ------------------------------------------------------------------ legend (collapsible pill, top-right)
    const float LegendH = 22, LegendItemH = 18, LegendPad = 8;

    string LegendTitle(PlotModel m) => m.Curves.Count == 1 ? m.Curves[0].Label : $"{m.Curves.Count} curves";

    float LegendWidth(PlotModel m)
    {
        _legendModel = m;
        int dots = Math.Min(m.Curves.Count, 8);
        float w = 8 + dots * 9 + 6 + _font.MeasureText(LegendTitle(m)) + 8 + 10 + 8;
        if (!m.IsLegendCollapsed) foreach (var c in m.Curves) w = Math.Max(w, 18 + _font.MeasureText(ItemLabel(c)) + 10);
        return w;
    }

    string ItemLabel(Curve c) => ItemLabel(_legendModel!, c);
    static string ItemLabel(PlotModel m, Curve c) => m.LabelOf(c) + (string.IsNullOrEmpty(c.Channel.Unit) ? "" : " [" + c.Channel.Unit + "]");
    PlotModel? _legendModel;

    SKRect HeaderRect(PlotModel m, SKRect plot)
    {
        float w = LegendWidth(m);
        return new SKRect(plot.Right - LegendPad - w, plot.Top + LegendPad, plot.Right - LegendPad, plot.Top + LegendPad + LegendH);
    }

    public SKRect LegendHeaderRect(PlotModel m, SKRect full)
    {
        _font.Typeface = UiTypeface; _font.Size = FontSize;
        return HeaderRect(m, ComputeLayout(m, full).Plot);
    }

    public SKRect LegendItemRect(PlotModel m, SKRect full, int index)
    {
        if (m.IsLegendCollapsed) return SKRect.Empty;
        var h = LegendHeaderRect(m, full);
        float top = h.Bottom + 2 + index * LegendItemH;
        return new SKRect(h.Left, top, h.Right, top + LegendItemH);
    }

    void DrawLegend(SKCanvas canvas, PlotModel m, SKRect plot)
    {
        // instrument-style strip legend (DESIGN-v2 §3): 3px color bar + name [unit] rows on a plot-bg box, top-right;
        // collapsed = one summary row ("N curves" + color bars)
        var hr = HeaderRect(m, plot);
        bool collapsed = m.IsLegendCollapsed;
        var box = collapsed ? hr : new SKRect(hr.Left, hr.Top, hr.Right, hr.Bottom + 2 + m.Curves.Count * LegendItemH + 4);
        _fill.Color = Theme.PlotBg; canvas.DrawRect(box, _fill);
        _line.Color = Theme.PlotFrame; canvas.DrawRect(new SKRect(box.Left + 0.5f, box.Top + 0.5f, box.Right - 0.5f, box.Bottom - 0.5f), new SKPaint { Color = Theme.PlotFrame, Style = SKPaintStyle.Stroke, StrokeWidth = 1 });
        float x = hr.Left + 8, cy = hr.MidY;
        int dots = Math.Min(m.Curves.Count, 8);
        for (int i = 0; i < dots; i++) { var c = m.Curves[i]; _fill.Color = c.Visible ? Palette(c.ColorIndex) : Theme.TextFaint; canvas.DrawRect(x, cy - 4, 6, 8, _fill); x += 9; }
        x += 6;
        _text.Color = Theme.PlotText; canvas.DrawText(LegendTitle(m), x, cy + 4, _font, _text);
        float chx = hr.Right - 16, chy = cy;
        _aaLine.Color = Theme.PlotAxisText; _aaLine.StrokeWidth = 1.5f;
        using (var chev = new SKPath())
        {
            if (collapsed) { chev.MoveTo(chx, chy - 2); chev.LineTo(chx + 4, chy + 2); chev.LineTo(chx + 8, chy - 2); }
            else { chev.MoveTo(chx, chy + 2); chev.LineTo(chx + 4, chy - 2); chev.LineTo(chx + 8, chy + 2); }
            canvas.DrawPath(chev, _aaLine);
        }
        if (collapsed) return;
        float y = hr.Bottom + 2;
        foreach (var c in m.Curves)
        {
            var col = Palette(c.ColorIndex);
            _fill.Color = c.Visible ? col : Theme.TextFaint;
            canvas.DrawRect(hr.Left + 8, y + 3, 3, LegendItemH - 6, _fill);
            _text.Color = c.Visible ? Theme.PlotText : Theme.TextFaint; canvas.DrawText(ItemLabel(c), hr.Left + 18, y + 13, _font, _text);
            y += LegendItemH;
        }
    }

    // ------------------------------------------------------------------ cursors
    void DrawCursors(SKCanvas canvas, PlotModel m, Layout lay)
    {
        var plot = lay.Plot;
        Readouts.Clear(); DeltaX = null;
        if (!m.CursorA.Enabled && !m.CursorB.Enabled) { CursorBand = ""; return; }
        _line.Color = Theme.PlotCursor; _line.StrokeWidth = 1;
        double stepX = Math.Max(1e-3, (m.XMax - m.XMin) / 1000);
        void One(PlotCursor cur, string name)
        {
            if (!cur.Enabled) return;
            float x = plot.Left + (float)((cur.X - m.XMin) / (m.XMax - m.XMin) * plot.Width);
            if (x < plot.Left || x > plot.Right) return;
            canvas.DrawLine(x, plot.Top, x, plot.Bottom, _line);
            // small pill label at the top of the line
            float w = _font.MeasureText(name) + 8;
            var r = new SKRect(x + 2, plot.Top + 2, x + 2 + w, plot.Top + 2 + 14);
            _fill.Color = Theme.PlotCursor; canvas.DrawRect(r, _fill);
            _text.Color = Theme.PlotBg; canvas.DrawText(name, r.Left + 4, r.Bottom - 3, _font, _text);
        }
        One(m.CursorA, "A"); One(m.CursorB, "B");
        // read-out text for the chart header (values at cursors, Δ, segment stats)
        var parts = new List<string>();
        foreach (var c in m.Curves)
        {
            if (!c.Visible || (c.XChannel != null && !c.XMonotonic)) continue;
            double? va = m.CursorA.Enabled ? ValueAt(c, m.CursorA.X) : null;
            double? vb = m.CursorB.Enabled ? ValueAt(c, m.CursorB.X) : null;
            (double min, double max, double mean)? segR = va != null && vb != null ? SegmentStats(c, Math.Min(m.CursorA.X, m.CursorB.X), Math.Max(m.CursorA.X, m.CursorB.X)) : null;
            Readouts.Add(new Readout(m.LabelOf(c), c.Channel.Unit, Palette(c.ColorIndex), va, vb, va != null && vb != null ? vb - va : null, segR));
            string s = m.LabelOf(c) + ":";
            if (va != null) s += " A=" + Ticks.FormatValue(va.Value);
            if (vb != null) s += " B=" + Ticks.FormatValue(vb.Value);
            if (va != null && vb != null)
            {
                s += " Δ=" + Ticks.FormatValue(vb.Value - va.Value);
                var seg = SegmentStats(c.Channel, Math.Min(m.CursorA.X, m.CursorB.X), Math.Max(m.CursorA.X, m.CursorB.X));
                if (seg != null) s += $" ({Ticks.FormatValue(seg.Value.min, 4)}…{Ticks.FormatValue(seg.Value.max, 4)}, x̄ {Ticks.FormatValue(seg.Value.mean, 4)})";
            }
            parts.Add(s);
        }
        if (m.CursorA.Enabled && m.CursorB.Enabled) { DeltaX = m.CursorB.X - m.CursorA.X; parts.Insert(0, "Δx=" + (m.XTimeMode != "none" ? TimeAxis.FormatRelative(DeltaX.Value, stepX) : Ticks.FormatValue(DeltaX.Value)) + (string.IsNullOrEmpty(m.XUnit) || m.XTimeMode != "none" ? "" : " " + m.XUnit)); }
        CursorBand = string.Join("  ·  ", parts);
    }

    static readonly Dictionary<(string, double, double), (double, double, double)?> _segCache = new();

    /// <summary>Curve-aware segment stats: x positions map through the curve's x channel when one is set (v3 B1).</summary>
    public static (double min, double max, double mean)? SegmentStats(Curve c, double xa, double xb)
    {
        if (!c.XMonotonic || c.XChannel == null) return SegmentStats(c.Channel, xa - c.XOffset, xb - c.XOffset);
        double ia = XIndex.IndexOf(c.XChannel, (xa - c.XOffset) / c.XScale), ib = XIndex.IndexOf(c.XChannel, (xb - c.XOffset) / c.XScale);
        return SegmentStatsByIndex(c.Channel, ia, ib, (c.Channel.Id + "|" + c.XChannel.Id, xa, xb));
    }

    /// <summary>Exact min/max/mean of the samples between two x positions (sequential scan, cached per channel+range;
    /// pyramid min/max only beyond 20M samples).</summary>
    public static (double min, double max, double mean)? SegmentStats(Channel ch, double xa, double xb)
        => SegmentStatsByIndex(ch, ch.IndexOf(xa), ch.IndexOf(xb), (ch.Id, xa, xb));

    static (double min, double max, double mean)? SegmentStatsByIndex(Channel ch, double ia, double ib, (string, double, double) key)
    {
        lock (_segCache) { if (_segCache.TryGetValue(key, out var hit)) return hit; }
        long a = Math.Max(0, (long)Math.Ceiling(ia)), b = Math.Min(ch.Length - 1, (long)Math.Floor(ib));
        (double, double, double)? res = null;
        if (b >= a)
        {
            long n = b - a + 1;
            if (n > 20_000_000)
            {
                var p = ch.Pyramid;
                if (p != null) { Span<float> mn = stackalloc float[1], mx = stackalloc float[1]; p.Envelope(ch.Source, a, b + 1, 1, mn, mx, out _); res = (mn[0], mx[0], double.NaN); }
            }
            else
            {
                var buf = System.Buffers.ArrayPool<double>.Shared.Rent((int)Math.Min(n, 1 << 18));
                double smin = double.PositiveInfinity, smax = double.NegativeInfinity, sum = 0; long cnt = 0;
                using (var seq = ch.Source.OpenSequential())
                    for (long pos = a; pos <= b;)
                    {
                        int take = (int)Math.Min(buf.Length, b - pos + 1);
                        int got = seq.Read(pos, buf.AsSpan(0, take)); if (got <= 0) break;
                        for (int i = 0; i < got; i++) { double v = buf[i]; if (double.IsNaN(v)) continue; cnt++; sum += v; if (v < smin) smin = v; if (v > smax) smax = v; }
                        pos += got;
                    }
                System.Buffers.ArrayPool<double>.Shared.Return(buf);
                if (cnt > 0) res = (smin, smax, sum / cnt);
            }
        }
        lock (_segCache) { if (_segCache.Count > 256) _segCache.Clear(); _segCache[key] = res; }
        return res;
    }

    /// <summary>Value of a curve at x: through the x channel when mapped, else implicit x (v3 B1 gate: equals the raw sample).</summary>
    public static double? ValueAt(Curve c, double x)
    {
        if (c.XMonotonic && c.XChannel != null)
        {
            long k = (long)Math.Round(XIndex.IndexOf(c.XChannel, (x - c.XOffset) / c.XScale));
            if (k < 0 || k >= c.Channel.Length) return null;
            Span<double> one = stackalloc double[1];
            return c.Channel.Source.Read(k, one) == 1 ? one[0] : null;
        }
        return ValueAt(c.Channel, x - c.XOffset);
    }

    public static double? ValueAt(Channel ch, double x)
    {
        double i = ch.IndexOf(x);
        long k = (long)Math.Round(i);
        if (k < 0 || k >= ch.Length) return null;
        Span<double> one = stackalloc double[1];
        return ch.Source.Read(k, one) == 1 ? one[0] : null;
    }

    public (double xMin, double xMax) PixelToX(PlotModel m, SKRect full, float px0, float px1)
    {
        var plot = ComputeLayout(m, full).Plot;
        double a = m.XMin + (px0 - plot.Left) / plot.Width * (m.XMax - m.XMin);
        double b = m.XMin + (px1 - plot.Left) / plot.Width * (m.XMax - m.XMin);
        return (Math.Min(a, b), Math.Max(a, b));
    }
}
