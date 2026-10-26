using System.Runtime.InteropServices;
using NexysView.Core.Data;

namespace NexysView.Core.Pyramid;

/// <summary>
/// Min/max LOD pyramid. Level 0 = bins of BaseBin samples (16); level k = bins of BaseBin·2^k. Float32 storage:
/// ~N bytes per channel in total (10 MB for 10M samples). Zooms below ~2·BaseBin samples/pixel read raw values.
/// </summary>
public sealed class MinMaxPyramid
{
    public const int BaseBin = 16;
    public const int CacheVersion = 1;

    public long Length { get; }
    public int Levels => _min.Length;
    public double GlobalMin { get; private set; } = double.NaN;
    public double GlobalMax { get; private set; } = double.NaN;
    public long BuiltSamples { get; private set; }   // progress (level-0 bins filled so far × BaseBin)
    public bool Complete => BuiltSamples >= Length;
    public TimeSpan BuildTime { get; internal set; }
    public bool FromCache { get; internal set; }

    readonly float[][] _min, _max;

    public MinMaxPyramid(long length)
    {
        Length = length;
        var mins = new List<float[]>(); var maxs = new List<float[]>();
        long bins = (length + BaseBin - 1) / BaseBin;
        while (true)
        {
            mins.Add(new float[bins]); maxs.Add(new float[bins]);
            if (bins <= 1) break;
            bins = (bins + 1) / 2;
        }
        _min = mins.ToArray(); _max = maxs.ToArray();
    }

    public long BinSize(int level) => (long)BaseBin << level;

    /// <summary>Build from a value source, streaming blocks of <paramref name="block"/> samples. Calls progress after each block.</summary>
    public void Build(IValueSource src, Action<long>? progress = null, int block = 1 << 20, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        block -= block % BaseBin;
        var buf = new double[Math.Min(block, (int)Math.Min(Length, int.MaxValue))];
        long pos = 0;
        double gmin = double.PositiveInfinity, gmax = double.NegativeInfinity;
        var l0min = _min[0]; var l0max = _max[0];
        using var seq = src.OpenSequential();
        while (pos < Length)
        {
            ct.ThrowIfCancellationRequested();
            int n = seq.Read(pos, buf.AsSpan(0, (int)Math.Min(buf.Length, Length - pos)));
            if (n <= 0) break;
            long bin = pos / BaseBin;
            for (int i = 0; i < n; i += BaseBin, bin++)
            {
                int e = Math.Min(i + BaseBin, n);
                float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
                for (int j = i; j < e; j++)
                {
                    double v = buf[j];
                    if (v < mn) mn = (float)v;
                    if (v > mx) mx = (float)v;
                }
                if (mn == float.PositiveInfinity) { mn = float.NaN; mx = float.NaN; }
                else { if (mn < gmin) gmin = mn; if (mx > gmax) gmax = mx; }
                l0min[bin] = mn; l0max[bin] = mx;
            }
            pos += n;
            BuiltSamples = pos;
            progress?.Invoke(pos);
        }
        GlobalMin = gmin == double.PositiveInfinity ? double.NaN : gmin;
        GlobalMax = gmax == double.NegativeInfinity ? double.NaN : gmax;
        BuildUpperLevels();
        BuiltSamples = Length;
        BuildTime = sw.Elapsed;
    }

    void BuildUpperLevels()
    {
        for (int l = 1; l < _min.Length; l++)
        {
            var pm = _min[l - 1]; var px = _max[l - 1]; var cm = _min[l]; var cx = _max[l];
            for (int i = 0; i < cm.Length; i++)
            {
                int a = 2 * i, b = 2 * i + 1;
                float mn = pm[a], mx = px[a];
                if (b < pm.Length)
                {
                    float mn2 = pm[b], mx2 = px[b];
                    if (float.IsNaN(mn)) { mn = mn2; mx = mx2; }
                    else if (!float.IsNaN(mn2)) { if (mn2 < mn) mn = mn2; if (mx2 > mx) mx = mx2; }
                }
                cm[i] = mn; cx[i] = mx;
            }
        }
    }

    /// <summary>
    /// Envelope for sample range [s0, s1) split into <paramref name="columns"/> pixel columns.
    /// Returns per-column (min,max); NaN where no valid sample. Uses the coarsest level with ≥2 bins/column; raw below 2·BaseBin spp.
    /// </summary>
    public void Envelope(IValueSource src, double s0, double s1, int columns, Span<float> outMin, Span<float> outMax, out int levelUsed)
    {
        double spp = (s1 - s0) / Math.Max(1, columns);
        if (spp < 2 * BaseBin)
        {
            levelUsed = -1;
            EnvelopeRaw(src, s0, s1, columns, outMin, outMax);
            return;
        }
        int level = 0;
        while (level + 1 < _min.Length && BinSize(level + 1) * 2 <= spp) level++;
        levelUsed = level;
        var mn = _min[level]; var mx = _max[level]; long bs = BinSize(level);
        for (int c = 0; c < columns; c++)
        {
            double a = s0 + spp * c, b = a + spp;
            long ba = Math.Max(0, (long)Math.Floor(a / bs)), bb = Math.Min(mn.Length - 1, (long)Math.Floor((b - 1e-9) / bs));
            float cmn = float.PositiveInfinity, cmx = float.NegativeInfinity;
            for (long i = ba; i <= bb; i++)
            {
                float v = mn[i];
                if (float.IsNaN(v)) continue;
                if (v < cmn) cmn = v;
                float w = mx[i];
                if (w > cmx) cmx = w;
            }
            if (cmn == float.PositiveInfinity) { outMin[c] = float.NaN; outMax[c] = float.NaN; }
            else { outMin[c] = cmn; outMax[c] = cmx; }
        }
    }

    [ThreadStatic] static double[]? _rawBuf;

    static void EnvelopeRaw(IValueSource src, double s0, double s1, int columns, Span<float> outMin, Span<float> outMax)
    {
        long first = Math.Max(0, (long)Math.Floor(s0));
        long last = Math.Min(src.Length, (long)Math.Ceiling(s1) + 1);
        int n = (int)Math.Max(0, last - first);
        if (_rawBuf == null || _rawBuf.Length < n) _rawBuf = new double[Math.Max(n, 4096)];
        var buf = _rawBuf;
        int got = n > 0 ? src.Read(first, buf.AsSpan(0, n)) : 0;
        double spp = (s1 - s0) / Math.Max(1, columns);
        for (int c = 0; c < columns; c++)
        {
            double a = s0 + spp * c, b = a + spp;
            long ia = Math.Max(first, (long)Math.Floor(a)), ib = Math.Min(first + got - 1, (long)Math.Floor(b));
            if (spp < 1) { ib = ia; }   // sub-sample zoom: one sample per column (interpolation done by renderer)
            float cmn = float.PositiveInfinity, cmx = float.NegativeInfinity;
            for (long i = ia; i <= ib; i++)
            {
                double v = buf[i - first];
                if (double.IsNaN(v)) continue;
                if (v < cmn) cmn = (float)v;
                if (v > cmx) cmx = (float)v;
            }
            if (cmn == float.PositiveInfinity) { outMin[c] = float.NaN; outMax[c] = float.NaN; }
            else { outMin[c] = cmn; outMax[c] = cmx; }
        }
    }

    // ------------------------------------------------------------------ disk cache
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        using var w = new BinaryWriter(fs);
        w.Write(0x3143564Eu); // "NVC1"
        w.Write(CacheVersion); w.Write(Length); w.Write(BaseBin); w.Write(_min.Length);
        w.Write(GlobalMin); w.Write(GlobalMax);
        for (int l = 0; l < _min.Length; l++)
        {
            w.Write(_min[l].Length);
            fs.Write(MemoryMarshal.AsBytes(_min[l].AsSpan()));
            fs.Write(MemoryMarshal.AsBytes(_max[l].AsSpan()));
        }
    }

    public static MinMaxPyramid? Load(string path, long expectedLength)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs);
            if (r.ReadUInt32() != 0x3143564Eu || r.ReadInt32() != CacheVersion) return null;
            long len = r.ReadInt64();
            if (len != expectedLength || r.ReadInt32() != BaseBin) return null;
            int levels = r.ReadInt32();
            var p = new MinMaxPyramid(len);
            if (p._min.Length != levels) return null;
            p.GlobalMin = r.ReadDouble(); p.GlobalMax = r.ReadDouble();
            for (int l = 0; l < levels; l++)
            {
                int n = r.ReadInt32();
                if (n != p._min[l].Length) return null;
                fs.ReadExactly(MemoryMarshal.AsBytes(p._min[l].AsSpan()));
                fs.ReadExactly(MemoryMarshal.AsBytes(p._max[l].AsSpan()));
            }
            p.BuiltSamples = len; p.FromCache = true;
            return p;
        }
        catch { return null; }
    }

    public long MemoryBytes => _min.Sum(a => (long)a.Length) * 8;
}
