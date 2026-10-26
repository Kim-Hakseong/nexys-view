using System.Diagnostics;
using NexysView.Core.Data;
using NexysView.Render;

namespace NexysView.App.Services;

/// <summary>
/// Produces one chart frame for the web renderer: per visible curve either the pixel-column min/max envelope (pyramid query)
/// or the raw samples of the window (≤ 6 samples per pixel), digital lanes as 0/1 columns, y-axis auto ranges + ticks, x ticks,
/// cursor read-outs. Numbers go into a Float32 buffer (shared memory with the page), everything else is a small JSON header.
/// The logic mirrors Render/PlotRenderer (same sample windows, same auto-scale padding, same tick steps) so the two renderers agree.
/// </summary>
public sealed class FrameServer
{
    float[] _mn = new float[4096], _mx = new float[4096];
    public const int MaxColumns = 8192;
    public const double RawSpp = 6.0;

    public sealed class Result
    {
        public object Header = new();
        public int Floats;
        public double Ms;
    }

    static int TickTarget(double px, double per) => Math.Clamp((int)(px / per), 3, 7);

    /// <summary>x label in the model's time mode (numeric / absolute clock / relative stopwatch).</summary>
    public string FormatX(PlotModel m, double x, double step = 0)
    {
        if (step <= 0) step = Math.Max(1e-3, (m.XMax - m.XMin) / 1000);
        return m.XTimeMode switch
        {
            "absolute" => TimeAxis.FormatAbsolute(x, step),
            "relative" => TimeAxis.FormatRelative(x - m.XTimeOrigin, step < 60 ? Math.Min(step, 0.001) : step),
            _ => step >= (m.XMax - m.XMin) / 1000 ? Ticks.Format(x, step) : Ticks.FormatValue(x, 6),
        };
    }

    static (double s0, double s1) SampleWindow(Curve c, PlotModel m)
    {
        if (c.XMonotonic && c.XChannel != null)
            return (XIndex.IndexOf(c.XChannel, (m.XMin - c.XOffset) / c.XScale), XIndex.IndexOf(c.XChannel, (m.XMax - c.XOffset) / c.XScale));
        return (c.Channel.IndexOf(m.XMin - c.XOffset), c.Channel.IndexOf(m.XMax - c.XOffset));
    }

    static bool InLane(PlotModel m, Curve c) => m.DigitalLanes && c.Step && !(c.XChannel != null && !c.XMonotonic);
    static bool IsXY(Curve c) => c.XChannel != null && !c.XMonotonic;

    /// <summary>Compute a frame. <paramref name="W"/> = plot width in device pixels (columns), <paramref name="plotH"/> = plot height
    /// in CSS pixels (tick count), <paramref name="buf"/> receives the Float32 payload.</summary>
    public Result Compute(Chart chart, int W, double plotH, double dpr, float[] buf) => Compute(chart, W, plotH, dpr, buf, null);

    /// <summary>Band statistics over [x0, x1]: exact min/max (pyramid) + mean/RMS from at most 4096 strided samples (approx flag when strided).</summary>
    (double min, double max, double mean, double rms, long n, bool approx)? BandStats(Curve c, PlotModel m, double x0, double x1)
    {
        var ch = c.Channel; var p = ch.Pyramid; if (p == null || ch.Length == 0) return null;
        double ia, ib;
        if (c.XMonotonic && c.XChannel != null) { ia = XIndex.IndexOf(c.XChannel, (x0 - c.XOffset) / c.XScale); ib = XIndex.IndexOf(c.XChannel, (x1 - c.XOffset) / c.XScale); }
        else { ia = ch.IndexOf(x0 - c.XOffset); ib = ch.IndexOf(x1 - c.XOffset); }
        long a = Math.Max(0, (long)Math.Ceiling(Math.Min(ia, ib))), b = Math.Min(ch.Length - 1, (long)Math.Floor(Math.Max(ia, ib)));
        if (b < a) return null;
        long n = b - a + 1; long stride = Math.Max(1, n / 2048); int take = (int)((n + stride - 1) / stride);   // ≤ 2048 strided reads: bounded cost and bounded mapped-page residency
        double sum = 0, sq = 0; long cnt = 0; double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        if (stride == 1)
        {
            var vb = System.Buffers.ArrayPool<double>.Shared.Rent(take); int got = ch.Source.Read(a, vb.AsSpan(0, take));
            for (int i = 0; i < got; i++) { double v = vb[i]; if (double.IsNaN(v)) continue; sum += v; sq += v * v; cnt++; if (v < mn) mn = v; if (v > mx) mx = v; }
            System.Buffers.ArrayPool<double>.Shared.Return(vb);
        }
        else
        {
            Span<double> one = stackalloc double[1];
            for (long i = a; i <= b; i += stride) { if (ch.Source.Read(i, one) != 1) continue; double v = one[0]; if (double.IsNaN(v)) continue; sum += v; sq += v * v; cnt++; }
            Span<float> emn = stackalloc float[1], emx = stackalloc float[1];
            p.Envelope(ch.Source, a, b + 1, 1, emn, emx, out _);
            if (!float.IsNaN(emn[0])) { mn = emn[0]; mx = emx[0]; }
        }
        if (cnt == 0) return null;
        return (mn, mx, sum / cnt, Math.Sqrt(sq / cnt), n, stride > 1);
    }

    static IEnumerable<double> LogTicks(double min, double max)
    {
        if (!(min > 0) || !(max > min)) yield break;
        int d0 = (int)Math.Floor(Math.Log10(min)), d1 = (int)Math.Ceiling(Math.Log10(max));
        bool fine = d1 - d0 <= 2;
        for (int d = d0; d <= d1; d++)
            foreach (var mant in fine ? new[] { 1.0, 2.0, 5.0 } : new[] { 1.0 })
            { double v = mant * Math.Pow(10, d); if (v >= min * 0.999 && v <= max * 1.001) yield return v; }
    }

    public Result Compute(Chart chart, int W, double plotH, double dpr, float[] buf, (double x0, double x1)? bandRef)
    {
        var sw = Stopwatch.StartNew();
        var m = chart.Model;
        W = Math.Clamp(W, 1, MaxColumns);
        if (_mn.Length < W) { _mn = new float[W]; _mx = new float[W]; }
        int pos = 0; int levelMin = int.MaxValue, levelMax = int.MinValue; long samples = 0; int columns = W;
        // ---- y auto-scale (PlotRenderer.AutoScaleY)
        foreach (var a in m.YAxes)
        {
            if (!a.Auto) continue;
            double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
            foreach (var c in m.Curves)
            {
                if (!c.Visible || c.Axis != a || InLane(m, c)) continue;
                var p = c.Channel.Pyramid; if (p == null) continue;
                if (IsXY(c)) { if (!double.IsNaN(p.GlobalMin)) { mn = Math.Min(mn, p.GlobalMin); mx = Math.Max(mx, p.GlobalMax); } continue; }
                var (s0, s1) = SampleWindow(c, m);
                int cols = Math.Min(W, 512);
                p.Envelope(c.Channel.Source, s0, s1, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out _);
                for (int i = 0; i < cols; i++) { if (float.IsNaN(_mn[i])) continue; if (a.Log && _mx[i] <= 0) continue; float lo = a.Log && _mn[i] <= 0 ? _mx[i] : _mn[i]; if (lo < mn) mn = lo; if (_mx[i] > mx) mx = _mx[i]; }
            }
            if (mn == double.PositiveInfinity) { mn = a.Log ? 0.1 : -1; mx = a.Log ? 10 : 1; }
            if (a.Log)
            {
                if (mn <= 0) mn = mx > 0 ? mx * 1e-3 : 0.1; if (mx <= mn) mx = mn * 10;
                double lpad = (Math.Log10(mx) - Math.Log10(mn)) * 0.05; a.Min = Math.Pow(10, Math.Log10(mn) - lpad); a.Max = Math.Pow(10, Math.Log10(mx) + lpad);
                continue;
            }
            if (mx - mn < 1e-12) { mn -= 0.5; mx += 0.5; }
            double pad = (mx - mn) * 0.05;
            a.Min = mn - pad; a.Max = mx + pad;
        }
        // ---- axes + ticks
        var axes = m.YAxes.Count == 0 ? new List<YAxis> { new("y0") } : m.YAxes;
        int yt = TickTarget(plotH, 46);
        var axesDto = axes.Select((a, i) => new
        {
            id = a.Id, right = a.Right, unit = a.Unit, min = a.Min, max = a.Max, log = a.Log && a.Min > 0,
            color = axes.Count > 1 ? m.Curves.FirstOrDefault(c => c.Axis == a)?.ColorIndex : (int?)null,
            ticks = a.Log && a.Min > 0 ? LogTicks(a.Min, a.Max).Select(v => new { v, label = Ticks.FormatValue(v, 3) }).ToList() : Ticks.Generate(a.Min, a.Max, yt).Select(v => new { v, label = Ticks.Format(v, Ticks.NiceStep(a.Max - a.Min, yt)) }).ToList(),
        }).ToList();
        int xt = TickTarget(W / Math.Max(1, dpr), 110);
        bool timeAxis = m.XTimeMode != "none";
        double xstep = timeAxis ? TimeAxis.NiceStep(m.XMax - m.XMin, xt) : Ticks.NiceStep(m.XMax - m.XMin, xt);
        double origin = m.XTimeMode == "relative" ? m.XTimeOrigin : 0;   // relative axis: ticks on the mm:ss grid of the origin, not on absolute values
        var xticks = (timeAxis ? TimeTicks(m.XMin - origin, m.XMax - origin, xstep).Select(v => v + origin) : Ticks.Generate(m.XMin, m.XMax, xt)).Select(v => new { v, label = FormatX(m, v, xstep) }).ToList();
        string xunit = m.XTimeMode == "absolute" ? TimeAxis.DateOf(m.XMin) : m.XTimeMode == "relative" ? "rel " + m.XUnit : m.XUnit;
        // ---- curves
        var curves = new List<object>();
        double xr = m.XMax - m.XMin;
        for (int ci = 0; ci < m.Curves.Count; ci++)
        {
            var c = m.Curves[ci];
            if (!c.Visible) continue;
            var ch = c.Channel; var p = ch.Pyramid;
            samples += ch.Length;
            if (InLane(m, c))
            {
                if (p == null) continue;
                var (s0, s1) = SampleWindow(c, m);
                double spp = (s1 - s0) / W;
                int c0 = (int)Math.Max(0, Math.Floor((0 - s0) / spp)), c1 = (int)Math.Min(W, Math.Ceiling((ch.Length - s0) / spp));
                int cols = c1 - c0;
                if (cols <= 0 || pos + W > buf.Length) continue;
                p.Envelope(ch.Source, s0 + c0 * spp, s0 + c1 * spp, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out _);
                double lo = double.IsNaN(p.GlobalMin) ? 0 : p.GlobalMin, hi = double.IsNaN(p.GlobalMax) ? 1 : p.GlobalMax, mid = lo + (hi - lo) / 2;
                int off = pos;
                for (int i = 0; i < W; i++) buf[pos + i] = float.NaN;
                for (int i = 0; i < cols; i++) buf[pos + c0 + i] = float.IsNaN(_mx[i]) ? float.NaN : (hi > lo ? _mx[i] > mid : _mx[i] != 0) ? 1f : 0f;
                pos += W;
                curves.Add(new { idx = ci, mode = "lane", off, n = W, color = c.ColorIndex, label = m.LabelOf(c) });
                continue;
            }
            if (IsXY(c))
            {
                // scatter: decimated raw pairs (x channel, y channel)
                long n = Math.Min(ch.Length, c.XChannel!.Length); if (n <= 0) continue;
                int take = (int)Math.Min(n, 20000); long stride = Math.Max(1, n / take);
                if (pos + 2 * take > buf.Length) continue;
                var bx = new double[1]; var by = new double[1]; int off = pos; int k = 0;
                for (long i = 0; i < n && k < take; i += stride) { c.XChannel.Source.Read(i, bx); ch.Source.Read(i, by); buf[pos + k] = (float)(bx[0] * c.XScale + c.XOffset); buf[pos + take + k] = (float)by[0]; k++; }
                pos += 2 * take;
                curves.Add(new { idx = ci, mode = "xy", off, n = k, color = c.ColorIndex, axis = c.Axis.Id, label = m.LabelOf(c) });
                continue;
            }
            if (p == null) continue;
            {
                var (s0, s1) = SampleWindow(c, m);
                if (s1 <= 0 || s0 >= ch.Length) continue;
                double spp = (s1 - s0) / W;
                if (spp < RawSpp)
                {
                    long first = Math.Max(0, (long)Math.Floor(s0)), last = Math.Min(ch.Length - 1, (long)Math.Ceiling(s1));
                    int n = (int)(last - first + 1);
                    if (n <= 0 || pos + 2 * n > buf.Length) continue;
                    var vb = System.Buffers.ArrayPool<double>.Shared.Rent(n);
                    int got = ch.Source.Read(first, vb.AsSpan(0, n));
                    double[]? xb = null;
                    if (c.XMonotonic && c.XChannel != null) { xb = System.Buffers.ArrayPool<double>.Shared.Rent(n); c.XChannel.Source.Read(first, xb.AsSpan(0, n)); }
                    int off = pos;
                    for (int i = 0; i < got; i++)
                    {
                        double x = xb != null ? xb[i] * c.XScale + c.XOffset : ch.XAt(first + i) + c.XOffset;
                        buf[pos + i] = (float)((x - m.XMin) / xr);   // normalized 0..1 across the plot width
                        buf[pos + got + i] = (float)vb[i];
                    }
                    System.Buffers.ArrayPool<double>.Shared.Return(vb);
                    if (xb != null) System.Buffers.ArrayPool<double>.Shared.Return(xb);
                    pos += 2 * got;
                    levelMin = Math.Min(levelMin, -1); levelMax = Math.Max(levelMax, -1);
                    curves.Add(new { idx = ci, mode = "raw", off, n = got, color = c.ColorIndex, axis = c.Axis.Id, step = c.Step, label = m.LabelOf(c), width = c.Width, points = c.Points });
                }
                else
                {
                    int c0 = (int)Math.Max(0, Math.Floor((0 - s0) / spp)), c1 = (int)Math.Min(W, Math.Ceiling((ch.Length - s0) / spp));
                    int cols = c1 - c0;
                    if (cols <= 0 || pos + 2 * W > buf.Length) continue;
                    p.Envelope(ch.Source, s0 + c0 * spp, s0 + c1 * spp, cols, _mn.AsSpan(0, cols), _mx.AsSpan(0, cols), out int level);
                    levelMin = Math.Min(levelMin, level); levelMax = Math.Max(levelMax, level);
                    int off = pos;
                    for (int i = 0; i < W; i++) { buf[pos + i] = float.NaN; buf[pos + W + i] = float.NaN; }
                    for (int i = 0; i < cols; i++) { buf[pos + c0 + i] = _mn[i]; buf[pos + W + c0 + i] = _mx[i]; }
                    pos += 2 * W;
                    curves.Add(new { idx = ci, mode = "env", off, n = W, color = c.ColorIndex, axis = c.Axis.Id, label = m.LabelOf(c), width = c.Width, points = c.Points });
                }
            }
        }
        // ---- cursors + read-outs (PlotRenderer.DrawCursors)
        object? cursors = null;
        if (m.CursorA.Enabled || m.CursorB.Enabled)
        {
            var ro = new List<object>();
            foreach (var c in m.Curves)
            {
                if (!c.Visible || IsXY(c) || InLane(m, c)) continue;
                double? va = m.CursorA.Enabled ? PlotRenderer.ValueAt(c, m.CursorA.X) : null;
                double? vb = m.CursorB.Enabled ? PlotRenderer.ValueAt(c, m.CursorB.X) : null;
                (double min, double max, double mean)? seg = va != null && vb != null ? PlotRenderer.SegmentStats(c, Math.Min(m.CursorA.X, m.CursorB.X), Math.Max(m.CursorA.X, m.CursorB.X)) : null;
                ro.Add(new { idx = m.Curves.IndexOf(c), label = m.LabelOf(c), unit = c.Channel.Unit, color = c.ColorIndex, a = va, b = vb, delta = va != null && vb != null ? vb - va : null, seg = seg == null ? null : new { seg.Value.min, seg.Value.max, seg.Value.mean } });
            }
            double? dx = m.CursorA.Enabled && m.CursorB.Enabled ? m.CursorB.X - m.CursorA.X : null;
            double stepX = Math.Max(1e-3, (m.XMax - m.XMin) / 1000);
            cursors = new
            {
                a = m.CursorA.Enabled ? new { x = m.CursorA.X, label = FormatX(m, m.CursorA.X, timeAxis ? 0.001 : 0) } : null,
                b = m.CursorB.Enabled ? new { x = m.CursorB.X, label = FormatX(m, m.CursorB.X, timeAxis ? 0.001 : 0) } : null,
                dx, dxLabel = dx == null ? "" : (timeAxis ? dx.Value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " s" : Ticks.FormatValue(dx.Value) + (string.IsNullOrEmpty(m.XUnit) ? "" : " " + m.XUnit)),
                readouts = ro,
            };
        }
        // ---- band cursor read-outs (0.9.3)
        object? band = null;
        if (m.BandEnabled)
        {
            List<object>? ro = m.BandLive ? null : new List<object>();
            foreach (var c in m.Curves)
            {
                if (ro == null) break;
                if (!c.Visible || IsXY(c) || InLane(m, c)) continue;
                var st = BandStats(c, m, m.BandX0, m.BandX1);
                ro.Add(new { idx = m.Curves.IndexOf(c), label = m.LabelOf(c), unit = c.Channel.Unit, color = c.ColorIndex, min = st?.min, max = st?.max, mean = st?.mean, rms = st?.rms, pp = st == null ? (double?)null : st.Value.max - st.Value.min, n = st?.n, approx = st?.approx ?? false });
            }
            double len = m.BandX1 - m.BandX0;
            band = new
            {
                x0 = m.BandX0, x1 = m.BandX1, label0 = FormatX(m, m.BandX0, timeAxis ? 0.001 : 0), label1 = FormatX(m, m.BandX1, timeAxis ? 0.001 : 0),
                lenLabel = timeAxis ? len.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture) + " s" : Ticks.FormatValue(len) + (string.IsNullOrEmpty(m.XUnit) ? "" : " " + m.XUnit),
                readouts = ro,
            };
        }
        var r = new Result { Floats = pos, Ms = sw.Elapsed.TotalMilliseconds };
        r.Header = new
        {
            chart = chart.Id, xmin = m.XMin, xmax = m.XMax, xunit, xTimeMode = m.XTimeMode, columns, axes = axesDto, xticks, curves, cursors, band,
            bandRef = bandRef == null ? null : new { x0 = bandRef.Value.x0, x1 = bandRef.Value.x1 },
            cursorMode = m.CursorMode,
            lanes = m.DigitalLanes,
            stats = new { ms = r.Ms, samples, columns, level = levelMin == int.MaxValue ? "-" : levelMin == levelMax ? levelMin.ToString() : $"{levelMin}..{levelMax}", floats = pos },
        };
        return r;
    }

    static IEnumerable<double> TimeTicks(double min, double max, double step)
    {
        if (!(step > 0) || !double.IsFinite(step)) yield break;
        double start = Math.Ceiling(min / step) * step;
        int n = (int)Math.Min(Ticks.MaxTicks, Math.Max(0, Math.Floor((max - start) / step + 1e-9) + 1));
        for (int i = 0; i < n; i++) yield return start + i * step;
    }
}
