using System.Globalization;

namespace NexysView.Render;

public static class Ticks
{
    /// <summary>"Nice" tick step for a range at roughly the requested count.</summary>
    public static double NiceStep(double range, int target)
    {
        if (range <= 0 || double.IsNaN(range) || double.IsInfinity(range)) return 1;
        double raw = range / Math.Max(1, target);
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double r = raw / mag;
        double nice = r < 1.5 ? 1 : r < 3 ? 2 : r < 7 ? 5 : 10;
        return nice * mag;
    }

    public const int MaxTicks = 1000;

    /// <summary>Tick positions; computed as start + i·step with a hard cap so a step below the double resolution of the
    /// axis values (e.g. x ≈ 3.8e9 s zoomed to µs) cannot loop forever (stability task [2]).</summary>
    public static IEnumerable<double> Generate(double min, double max, int target)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max) || max <= min) yield break;
        double step = NiceStep(max - min, target);
        if (!(step > 0) || !double.IsFinite(step)) yield break;
        double start = Math.Ceiling(min / step) * step;
        double count = Math.Floor((max - start) / step + 1e-9) + 1;
        if (!(count > 0)) yield break;
        int n = (int)Math.Min(MaxTicks, count);
        for (int i = 0; i < n; i++)
        {
            double v = start + i * step;
            if (v > max + step * 1e-9) yield break;
            yield return Math.Abs(v) < step * 1e-9 ? 0 : v;
        }
    }

    public static string Format(double v, double step)
    {
        if (double.IsNaN(v)) return "NaN";
        double a = Math.Abs(v);
        if (a >= 1e6 || (a > 0 && a < 1e-4)) return v.ToString("0.###E0", CultureInfo.InvariantCulture);
        int dec = step >= 1 ? 0 : (int)Math.Min(9, Math.Ceiling(-Math.Log10(step)));
        return v.ToString("F" + dec, CultureInfo.InvariantCulture);
    }

    public static string FormatValue(double v, int sig = 6)
    {
        if (double.IsNaN(v)) return "—";
        double a = Math.Abs(v);
        if (a == 0) return "0";
        if (a >= 1e9 || a < 1e-4) return v.ToString("0.####E0", CultureInfo.InvariantCulture);
        int dec = Math.Max(0, sig - 1 - (int)Math.Floor(Math.Log10(a)));
        return v.ToString("F" + Math.Min(dec, 9), CultureInfo.InvariantCulture);
    }
}
