using System.Globalization;

namespace NexysView.Render;

/// <summary>Time-unit tick steps and labels for x axes mapped to a time channel (v3 B1).</summary>
public static class TimeAxis
{
    static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    static readonly double[] Steps =
    {
        0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800, 604800
    };

    /// <summary>Nice step in seconds: 1/2/5 ms…s, then 10/15/30 s, 1/2/5/10/15/30 min, 1/2/3/6/12 h, 1/2/7 d.</summary>
    public static double NiceStep(double rangeSeconds, int target)
    {
        if (!(rangeSeconds > 0) || double.IsInfinity(rangeSeconds)) return 1;
        double raw = rangeSeconds / Math.Max(1, target);
        foreach (var s in Steps) if (s >= raw) return s;
        return Ticks.NiceStep(rangeSeconds, target);   // beyond a week: decimal days
    }

    public static DateTime ToDateTime(double seconds1904) => Epoch1904.AddSeconds(seconds1904);

    /// <summary>Absolute label: "HH:mm:ss" (+".fff" for sub-second steps); the date is given separately (<see cref="DateOf"/>).</summary>
    public static string FormatAbsolute(double seconds1904, double step)
    {
        if (!double.IsFinite(seconds1904) || Math.Abs(seconds1904) > 1e11) return Ticks.Format(seconds1904, step);
        var dt = ToDateTime(seconds1904);
        if (step >= 86400) return dt.ToString("MM-dd", CultureInfo.InvariantCulture);
        if (step >= 1) return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        int dec = (int)Math.Min(3, Math.Ceiling(-Math.Log10(step)));
        return dt.ToString("HH:mm:ss." + new string('f', dec), CultureInfo.InvariantCulture);
    }

    public static string DateOf(double seconds1904) => double.IsFinite(seconds1904) && Math.Abs(seconds1904) < 1e11 ? ToDateTime(seconds1904).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " UTC" : "";

    /// <summary>Relative label from an origin: "mm:ss.fff" (below an hour) or "HH:mm:ss(.fff)".</summary>
    public static string FormatRelative(double seconds, double step)
    {
        if (!double.IsFinite(seconds)) return "—";
        bool neg = seconds < 0; seconds = Math.Abs(seconds);
        int dec = step >= 1 ? 0 : (int)Math.Min(3, Math.Ceiling(-Math.Log10(step)));
        long whole = (long)Math.Floor(seconds);
        double frac = seconds - whole;
        long h = whole / 3600, mi = whole % 3600 / 60, s = whole % 60;
        string core = h > 0 ? $"{h:00}:{mi:00}:{s:00}" : $"{mi:00}:{s:00}";
        if (dec > 0) core += frac.ToString("." + new string('0', dec), CultureInfo.InvariantCulture).Substring(1).Insert(0, ".");
        return (neg ? "-" : "") + core;
    }
}
