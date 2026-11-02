using NexysView.Core.Data;

namespace NexysView.App.Import;

/// <summary>In-memory value source (DataPlugin results, analysis outputs, tests). CSV files are read by the core
/// <see cref="NexysView.Core.Csv.CsvDataSource"/> since 1.1.</summary>
public sealed class ArraySource : IValueSource
{
    public double[] Data;
    public ArraySource(double[] d) => Data = d;
    public long Length => Data.Length;
    public int Read(long first, Span<double> dst)
    {
        int n = (int)Math.Max(0, Math.Min(dst.Length, Data.Length - first));
        if (n > 0) Data.AsSpan((int)first, n).CopyTo(dst);
        return n;
    }
}
