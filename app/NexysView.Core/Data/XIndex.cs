namespace NexysView.Core.Data;

/// <summary>
/// X-channel mapping helpers (v3 B1): a monotonic channel (time) used as the x axis of other curves of the same length.
/// Index lookup is a binary search on the x channel's values (24 single-sample reads on 10M samples), so mapping costs
/// nothing per frame beyond the two window bounds.
/// </summary>
public static class XIndex
{
    public const double Epoch1904ToUnix = -2082844800.0;   // seconds from 1904-01-01 (1904-epoch/TDMS) to 1970-01-01

    /// <summary>Seconds per unit for a time-like unit string; 0 when the unit is not a time unit.</summary>
    public static double TimeScale(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "s" or "sec" or "secs" or "second" or "seconds" => 1,
        "ms" or "msec" or "millisecond" or "milliseconds" => 1e-3,
        "us" or "usec" or "µs" or "microsecond" or "microseconds" => 1e-6,
        "ns" => 1e-9,
        "min" or "minute" or "minutes" => 60,
        "h" or "hr" or "hour" or "hours" => 3600,
        _ => 0,
    };

    static double Read(Channel c, long i)
    {
        Span<double> one = stackalloc double[1];
        return c.Source.Read(i, one) == 1 ? one[0] : double.NaN;
    }

    /// <summary>First and last value of a channel (direct reads, no pyramid needed).</summary>
    public static (double first, double last) Range(Channel c) => c.Length == 0 ? (0, 0) : (Read(c, 0), Read(c, c.Length - 1));

    /// <summary>Non-decreasing check on up to <paramref name="samples"/> evenly spaced reads (plus first/last).</summary>
    public static bool IsMonotonic(Channel c, int samples = 512)
    {
        if (c.IsText || c.Length < 2) return false;
        long n = c.Length; long step = Math.Max(1, n / samples);
        double prev = Read(c, 0);
        if (double.IsNaN(prev)) return false;
        for (long i = step; i < n; i += step) { double v = Read(c, i); if (double.IsNaN(v) || v < prev) return false; prev = v; }
        double last = Read(c, n - 1);
        return !double.IsNaN(last) && last >= prev;
    }

    /// <summary>Time-axis candidate: time unit, numeric, monotonic.</summary>
    public static bool IsTimeCandidate(Channel c) => TimeScale(c.Unit) > 0 && IsMonotonic(c);

    /// <summary>Best x candidate of a file for curves of a given length: time unit + monotonic + same length;
    /// "System Time"-like names first, then seconds over µs.</summary>
    public static Channel? SuggestX(IEnumerable<Channel> channels, long length)
    {
        return channels.Where(c => c.Length == length && !c.IsText && TimeScale(c.Unit) > 0 && IsMonotonic(c))
            .OrderByDescending(c => c.Name.Contains("System Time", StringComparison.OrdinalIgnoreCase) && !c.Name.Contains("Micro", StringComparison.OrdinalIgnoreCase) ? 3
                               : c.Name.Contains("Time", StringComparison.OrdinalIgnoreCase) ? 2 : 1)
            .ThenByDescending(c => TimeScale(c.Unit))
            .FirstOrDefault();
    }

    /// <summary>Fractional sample index of x within a monotonic channel (binary search). Extrapolates linearly outside.</summary>
    public static double IndexOf(Channel xc, double x)
    {
        long n = xc.Length;
        if (n == 0) return 0;
        if (n == 1) return 0;
        long lo = 0, hi = n - 1;
        double xlo = Read(xc, lo), xhi = Read(xc, hi);
        if (double.IsNaN(xlo) || double.IsNaN(xhi) || xhi <= xlo) return x <= xlo ? 0 : n - 1;
        double avg = (xhi - xlo) / (hi - lo);
        if (x <= xlo) return lo + (x - xlo) / avg;
        if (x >= xhi) return hi + (x - xhi) / avg;
        while (hi - lo > 1)
        {
            long mid = (lo + hi) >> 1; double xm = Read(xc, mid);
            if (double.IsNaN(xm)) { hi = mid; continue; }
            if (xm <= x) { lo = mid; xlo = xm; } else { hi = mid; xhi = xm; }
        }
        return lo + (xhi > xlo ? (x - xlo) / (xhi - xlo) : 0);
    }

    /// <summary>True when the values look like an absolute 1904-epoch timestamp (seconds since 1904, i.e. > 1e9).</summary>
    public static bool LooksAbsolute(double first) => first > 1e9;

    /// <summary>Step-like channel (v3 B2): integer/boolean data type, or at most 8 distinct values in the first 100k samples.</summary>
    public static bool IsStepLike(Channel c)
    {
        if (c.IsText || c.Length == 0) return false;
        string t = c.DataType.ToUpperInvariant();
        if (t is "I8" or "U8" or "I16" or "U16" or "I32" or "U32" or "I64" or "U64" or "BOOLEAN" or "BOOL") return true;
        int n = (int)Math.Min(c.Length, 100_000);
        var buf = new double[n]; c.Source.Read(0, buf);
        var set = new HashSet<double>();
        foreach (var v in buf) { if (double.IsNaN(v)) continue; set.Add(v); if (set.Count > 8) return false; }
        return set.Count > 0;
    }
}
