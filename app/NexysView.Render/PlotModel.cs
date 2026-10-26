using NexysView.Core.Data;

namespace NexysView.Render;

public sealed class YAxis
{
    public string Id { get; }
    public string Unit { get; set; } = "";
    public double Min { get; set; } = -1;
    public double Max { get; set; } = 1;
    public bool Auto { get; set; } = true;
    public bool Right { get; set; }
    public YAxis(string id) => Id = id;
}

public sealed class Curve
{
    public Channel Channel { get; }
    /// <summary>X source: scatter XY when <see cref="XMonotonic"/> is false, time/x mapping (same length, monotonic) when true.</summary>
    public Channel? XChannel { get; set; }
    public bool XMonotonic { get; set; }
    /// <summary>Seconds per x-channel unit (µs → 1e-6); 1 for plain x.</summary>
    public double XScale { get; set; } = 1;
    /// <summary>Alignment offset added to x (v3 B4), in x units after scaling.</summary>
    public double XOffset { get; set; }
    /// <summary>Step rendering (v3 B2): integer/boolean/few-valued channels; auto-detected on drop, user switchable.</summary>
    public bool Step { get; set; }
    public int ColorIndex { get; set; }
    public bool Visible { get; set; } = true;
    public YAxis Axis { get; set; }
    public float Width { get; set; } = 1f;
    public Curve(Channel ch, YAxis axis, int colorIndex) { Channel = ch; Axis = axis; ColorIndex = colorIndex; }
    public string Label => Channel.DisplayName;
    /// <summary>x in plot units of sample i.</summary>
    public double XAt(long i)
    {
        if (XMonotonic && XChannel != null) { Span<double> one = stackalloc double[1]; return (XChannel.Source.Read(i, one) == 1 ? one[0] : double.NaN) * XScale + XOffset; }
        return Channel.XAt(i) + XOffset;
    }
}

public sealed class PlotCursor
{
    public bool Enabled;
    public double X;        // in x units
}

/// <summary>A 2D time/XY chart: curves, axes, view window, cursors. Pure model (no rendering state).</summary>
public sealed class PlotModel
{
    public string Title { get; set; } = "";
    public List<Curve> Curves { get; } = new();
    public List<YAxis> YAxes { get; } = new();
    public double XMin { get; set; }
    public double XMax { get; set; } = 1;
    public string XUnit { get; set; } = "s";
    public bool IsXY => Curves.Count > 0 && Curves[0].XChannel != null && !Curves[0].XMonotonic;
    public PlotCursor CursorA { get; } = new();
    public PlotCursor CursorB { get; } = new();
    public bool ShowLegend { get; set; } = true;
    public bool ShowGrid { get; set; } = true;
    /// <summary>Legend pill state; null = auto (collapsed when more than 3 curves).</summary>
    public bool? LegendCollapsed { get; set; }
    public bool IsLegendCollapsed => LegendCollapsed ?? Curves.Count > 3;
    public int NextColor;

    // ---- v3: time x axis, digital lanes, alignment
    /// <summary>"none" (numeric), "absolute" (1904-epoch 1904 epoch → clock ticks), "relative" (mm:ss.fff from <see cref="XTimeOrigin"/>).</summary>
    public string XTimeMode { get; set; } = "none";
    public double XTimeOrigin { get; set; }
    /// <summary>Stack step curves as thin 0/1 lanes under the plot.</summary>
    public bool DigitalLanes { get; set; }
    /// <summary>Comparison alignment (v3 B4): "none" | "start" | "zero" | "manual".</summary>
    public string AlignMode { get; set; } = "none";
    public double AlignOffsetMs { get; set; }
    /// <summary>Curves from more than one file → legend/readouts show the file name.</summary>
    public bool LabelWithFile => Curves.Select(c => c.Channel.FileName).Distinct().Count() > 1;
    public string LabelOf(Curve c) => LabelWithFile ? System.IO.Path.GetFileNameWithoutExtension(c.Channel.FileName) + " › " + c.Channel.Name : c.Label;
    public Channel? XChannelOf => Curves.FirstOrDefault(c => c.XMonotonic)?.XChannel;

    public YAxis AxisFor(string unit)
    {
        var a = YAxes.FirstOrDefault(y => y.Unit == unit);
        if (a != null) return a;
        a = new YAxis("y" + YAxes.Count) { Unit = unit, Right = YAxes.Count % 2 == 1 };
        YAxes.Add(a);
        return a;
    }

    public Curve AddCurve(Channel ch, Channel? x = null)
    {
        var c = new Curve(ch, AxisFor(ch.Unit), NextColor++) { XChannel = x };
        Curves.Add(c);
        if (Curves.Count == 1) { XUnit = x != null ? x.Unit : ch.XUnit; }
        // a chart that already maps x to a time channel maps the new curve too when the lengths agree
        var xc = XChannelOf;
        if (x == null && xc != null && xc.Length == ch.Length) { c.XChannel = xc; c.XMonotonic = true; c.XScale = Curves[0].XScale; }
        return c;
    }

    /// <summary>Use <paramref name="x"/> as the x axis of every curve with the same sample count (v3 B1).
    /// Returns the number of curves that could not be mapped (length mismatch). Time units switch the axis to clock/stopwatch ticks.</summary>
    public int SetXChannel(Channel x)
    {
        double scale = XIndex.TimeScale(x.Unit); if (scale <= 0) scale = 1;
        int miss = 0;
        foreach (var c in Curves)
        {
            if (c.Channel == x) continue;
            if (c.Channel.Length != x.Length) { miss++; continue; }
            c.XChannel = x; c.XMonotonic = true; c.XScale = scale;
        }
        Curves.RemoveAll(c => c.Channel == x);
        bool time = XIndex.TimeScale(x.Unit) > 0;
        XUnit = time ? "s" : x.Unit;
        var (first, _) = XIndex.Range(x);
        if (time) { XTimeOrigin = first * scale; XTimeMode = XIndex.LooksAbsolute(first * scale) ? "absolute" : "relative"; }
        else XTimeMode = "none";
        ZoomFull();
        return miss;
    }

    /// <summary>Back to implicit x (sample index / wf_increment) for all curves.</summary>
    public void ClearXChannel()
    {
        foreach (var c in Curves) { if (c.XMonotonic) { c.XChannel = null; c.XMonotonic = false; c.XScale = 1; } }
        XTimeMode = "none"; if (Curves.Count > 0) XUnit = Curves[0].Channel.XUnit;
        ZoomFull();
    }

    /// <summary>Full-range x window over all curves.</summary>
    public (double min, double max) FullX()
    {
        if (Curves.Count == 0) return (0, 1);
        double mn = double.PositiveInfinity, mx = double.NegativeInfinity;
        foreach (var c in Curves)
        {
            if (c.XChannel != null)
            {
                if (c.XMonotonic) { var (a, b) = XIndex.Range(c.XChannel); mn = Math.Min(mn, a * c.XScale + c.XOffset); mx = Math.Max(mx, b * c.XScale + c.XOffset); continue; }
                var p = c.XChannel.Pyramid; if (p != null && !double.IsNaN(p.GlobalMin)) { mn = Math.Min(mn, p.GlobalMin); mx = Math.Max(mx, p.GlobalMax); } continue;
            }
            mn = Math.Min(mn, c.Channel.XMin + c.XOffset); mx = Math.Max(mx, c.Channel.XMax + c.XOffset);
        }
        if (mn == double.PositiveInfinity) return (0, 1);
        if (mx <= mn) mx = mn + 1;
        return (mn, mx);
    }

    public void ZoomFull() { (XMin, XMax) = FullX(); foreach (var a in YAxes) a.Auto = true; }
}
