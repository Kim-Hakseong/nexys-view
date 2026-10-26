using System.Numerics;

namespace NexysView.Core.Analysis;

public sealed record Stats(long Count, double Min, double Max, double Mean, double Rms, double Std, double Sum);

/// <summary>Basic DSP for ANALYSIS: statistics, FFT (3 windows), IIR/FIR filters, derivative/integral, resampling.</summary>
public static class Dsp
{
    public const int MaxFirTaps = 8191;
    public const double MaxQ = 1000;

    /// <summary>Parameter validation before any filter runs (stability task [3]): fs &gt; 0, 0 &lt; fc &lt; fs/2, signal longer than the
    /// filter, bounded length. Throws ArgumentException with a message meant for the UI.</summary>
    public static void ValidateFilter(double fs, double fc, long n, int taps = 1, double q = 1)
    {
        if (!double.IsFinite(fs) || fs <= 0) throw new ArgumentException("Sample rate must be > 0 — the channel has no x increment (wf_increment); resample or set one first.");
        if (!double.IsFinite(fc) || fc <= 0) throw new ArgumentException("Cutoff frequency must be > 0 Hz.");
        if (fc >= fs / 2) throw new ArgumentException($"Cutoff {fc:G6} Hz must be below Nyquist fs/2 = {fs / 2:G6} Hz.");
        if (n < 2) throw new ArgumentException($"Signal needs at least 2 samples (has {n}).");
        if (taps < 1 || taps > MaxFirTaps) throw new ArgumentException($"Filter length {taps} out of range 1…{MaxFirTaps}.");
        if (taps > n) throw new ArgumentException($"Filter length {taps} exceeds the signal length {n}.");
        if (!double.IsFinite(q) || q <= 0 || q > MaxQ) throw new ArgumentException($"Q must be in (0, {MaxQ}].");
    }

    static void CheckFinite(double[] c, string what)
    {
        foreach (var v in c) if (!double.IsFinite(v)) throw new ArithmeticException($"{what} coefficients are not finite — filter aborted.");
    }

    static void Tick(CancellationToken ct, int i) { if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested(); }
    public static Stats Statistics(ReadOnlySpan<double> x)
    {
        long n = 0; double mn = double.PositiveInfinity, mx = double.NegativeInfinity, sum = 0, sq = 0;
        foreach (var v in x) { if (double.IsNaN(v)) continue; n++; sum += v; sq += v * v; if (v < mn) mn = v; if (v > mx) mx = v; }
        if (n == 0) return new Stats(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        double mean = sum / n;
        // two-pass variance for accuracy
        double ss = 0; foreach (var v in x) { if (double.IsNaN(v)) continue; double d = v - mean; ss += d * d; }
        double std = n > 1 ? Math.Sqrt(ss / (n - 1)) : 0;
        return new Stats(n, mn, mx, mean, Math.Sqrt(sq / n), std, sum);
    }

    /// <summary>Streaming statistics over a whole channel (sequential reader, 1M-sample blocks, no large allocations).</summary>
    public static Stats StatisticsOf(Data.IValueSource src, int block = 1 << 20)
    {
        long n = 0; double mn = double.PositiveInfinity, mx = double.NegativeInfinity, sum = 0, sq = 0;
        var buf = new double[(int)Math.Min(block, Math.Max(1, src.Length))];
        using (var seq = src.OpenSequential())
        {
            long pos = 0;
            while (pos < src.Length)
            {
                int got = seq.Read(pos, buf.AsSpan(0, (int)Math.Min(buf.Length, src.Length - pos)));
                if (got <= 0) break;
                for (int i = 0; i < got; i++) { double v = buf[i]; if (double.IsNaN(v)) continue; n++; sum += v; sq += v * v; if (v < mn) mn = v; if (v > mx) mx = v; }
                pos += got;
            }
        }
        if (n == 0) return new Stats(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, 0);
        double mean = sum / n;
        double var_ = Math.Max(0, (sq - n * mean * mean) / Math.Max(1, n - 1));
        return new Stats(n, mn, mx, mean, Math.Sqrt(sq / n), Math.Sqrt(var_), sum);
    }

    public enum Window { Rectangular, Hann, Hamming, Blackman }

    public static double[] WindowCoefficients(Window w, int n)
    {
        var c = new double[n];
        for (int i = 0; i < n; i++)
        {
            double a = 2 * Math.PI * i / (n - 1);
            c[i] = w switch
            {
                Window.Hann => 0.5 - 0.5 * Math.Cos(a),
                Window.Hamming => 0.54 - 0.46 * Math.Cos(a),
                Window.Blackman => 0.42 - 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2 * a),
                _ => 1.0,
            };
        }
        return c;
    }

    /// <summary>In-place iterative radix-2 FFT (n must be a power of two).</summary>
    public static void Fft(Complex[] a)
    {
        int n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            var wl = new Complex(Math.Cos(ang), Math.Sin(ang));
            for (int i = 0; i < n; i += len)
            {
                Complex w = Complex.One;
                for (int j = 0; j < len / 2; j++)
                {
                    var u = a[i + j]; var v = a[i + j + len / 2] * w;
                    a[i + j] = u + v; a[i + j + len / 2] = u - v;
                    w *= wl;
                }
            }
        }
    }

    /// <summary>Amplitude spectrum (peak amplitude, single-sided, window-gain corrected). Returns (frequencies, amplitudes).</summary>
    public static (double[] f, double[] amp) AmplitudeSpectrum(ReadOnlySpan<double> x, double sampleRate, Window window, int? fftSize = null)
    {
        if (x.Length < 2) throw new ArgumentException($"FFT needs at least 2 samples (has {x.Length}).");
        if (fftSize is < 2 or > (1 << 26)) throw new ArgumentException("FFT size must be between 2 and 67,108,864.");
        if (!double.IsFinite(sampleRate) || sampleRate <= 0) throw new ArgumentException("Sample rate must be > 0 for a frequency axis — the channel has no x increment.");
        int n = fftSize ?? 1 << (int)Math.Floor(Math.Log2(Math.Max(2, x.Length)));
        n = Math.Min(n, 1 << (int)Math.Floor(Math.Log2(Math.Max(2, x.Length))));
        var w = WindowCoefficients(window, n);
        double gain = w.Sum() / n;
        var a = new Complex[n];
        for (int i = 0; i < n; i++) a[i] = new Complex(double.IsNaN(x[i]) ? 0 : x[i] * w[i], 0);
        Fft(a);
        int half = n / 2 + 1;
        var f = new double[half]; var amp = new double[half];
        for (int k = 0; k < half; k++)
        {
            f[k] = k * sampleRate / n;
            double m = a[k].Magnitude / n / gain;
            amp[k] = (k == 0 || k == n / 2) ? m : 2 * m;
        }
        return (f, amp);
    }

    /// <summary>Biquad coefficients (RBJ cookbook) for low/high-pass Butterworth-like 2nd order sections.</summary>
    public static (double[] b, double[] a) Biquad(string type, double fc, double fs, double q = 0.7071067811865476)
    {
        ValidateFilter(fs, fc, 2, 1, q);
        double w0 = 2 * Math.PI * fc / fs, cw = Math.Cos(w0), sw = Math.Sin(w0), alpha = sw / (2 * q);
        double b0, b1, b2, a0, a1, a2;
        if (type == "highpass") { b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2; }
        else { b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2; }
        a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
        var res = (b: new[] { b0 / a0, b1 / a0, b2 / a0 }, a: new[] { 1, a1 / a0, a2 / a0 });
        CheckFinite(res.b, "IIR"); CheckFinite(res.a, "IIR");
        return res;
    }

    /// <summary>Direct-form II transposed IIR filter (scipy.signal.lfilter semantics, zero initial state).</summary>
    public static double[] Lfilter(double[] b, double[] a, ReadOnlySpan<double> x, CancellationToken ct = default)
    {
        CheckFinite(b, "IIR"); CheckFinite(a, "IIR");
        if (a.Length == 0 || a[0] == 0 || !double.IsFinite(a[0])) throw new ArgumentException("IIR denominator a[0] must be non-zero.");
        int n = x.Length; var y = new double[n];
        int order = Math.Max(a.Length, b.Length);
        var bb = new double[order]; var aa = new double[order];
        Array.Copy(b, bb, b.Length); Array.Copy(a, aa, a.Length);
        var z = new double[order];
        for (int i = 0; i < n; i++)
        {
            Tick(ct, i);
            double xi = x[i];
            double yi = bb[0] * xi + z[0];
            for (int k = 1; k < order; k++) z[k - 1] = bb[k] * xi + (k < order ? z[k] : 0) - aa[k] * yi;
            z[order - 1] = 0;
            y[i] = yi;
        }
        return y;
    }

    /// <summary>Windowed-sinc FIR low-pass (Hamming), odd length; applied with zero-phase delay compensation disabled (causal, like np.convolve 'same' shifted).</summary>
    public static double[] FirLowpass(double fc, double fs, int taps)
    {
        ValidateFilter(fs, fc, long.MaxValue, Math.Max(1, taps));
        if (taps % 2 == 0) taps++;
        var h = new double[taps]; int m = taps / 2; double wc = 2 * fc / fs;
        for (int i = 0; i < taps; i++)
        {
            int k = i - m;
            double s = k == 0 ? wc : Math.Sin(Math.PI * wc * k) / (Math.PI * k);
            h[i] = s * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (taps - 1)));
        }
        double sum = h.Sum(); for (int i = 0; i < taps; i++) h[i] /= sum;
        CheckFinite(h, "FIR");
        return h;
    }

    /// <summary>FIR convolution, 'same' length, centered (np.convolve(x, h, 'same')).</summary>
    public static double[] Convolve(ReadOnlySpan<double> x, double[] h, CancellationToken ct = default)
    {
        CheckFinite(h, "FIR");
        int n = x.Length, m = h.Length; var y = new double[n];
        int off = m / 2;
        for (int i = 0; i < n; i++)
        {
            Tick(ct, i);
            double s = 0;
            for (int k = 0; k < m; k++) { int j = i + off - k; if (j >= 0 && j < n) s += x[j] * h[k]; }
            y[i] = s;
        }
        return y;
    }

    /// <summary>np.gradient with uniform spacing dx (central differences, one-sided at the ends).</summary>
    public static double[] Derivative(ReadOnlySpan<double> x, double dx)
    {
        if (!double.IsFinite(dx) || dx <= 0) throw new ArgumentException("x increment must be > 0 for a derivative — the channel has no x increment.");
        int n = x.Length; var y = new double[n];
        if (n < 2) return y;
        y[0] = (x[1] - x[0]) / dx; y[n - 1] = (x[n - 1] - x[n - 2]) / dx;
        for (int i = 1; i < n - 1; i++) y[i] = (x[i + 1] - x[i - 1]) / (2 * dx);
        return y;
    }

    /// <summary>scipy.integrate.cumulative_trapezoid with initial=0.</summary>
    public static double[] Integral(ReadOnlySpan<double> x, double dx)
    {
        if (!double.IsFinite(dx) || dx <= 0) throw new ArgumentException("x increment must be > 0 for an integral — the channel has no x increment.");
        int n = x.Length; var y = new double[n];
        for (int i = 1; i < n; i++) y[i] = y[i - 1] + (x[i] + x[i - 1]) * dx / 2;
        return y;
    }

    /// <summary>Linear resampling to a new sample count (np.interp on the time axis).</summary>
    public static double[] ResampleLinear(ReadOnlySpan<double> x, int newCount)
    {
        if (newCount < 1 || newCount > 100_000_000) throw new ArgumentException($"New sample count {newCount} out of range 1…100,000,000.");
        int n = x.Length; var y = new double[newCount];
        if (n == 0) return y;
        if (n == 1) { Array.Fill(y, x[0]); return y; }
        for (int i = 0; i < newCount; i++)
        {
            double t = newCount == 1 ? 0 : (double)i * (n - 1) / (newCount - 1);
            int k = (int)Math.Floor(t); if (k >= n - 1) { y[i] = x[n - 1]; continue; }
            double f = t - k; y[i] = x[k] + (x[k + 1] - x[k]) * f;
        }
        return y;
    }
}
