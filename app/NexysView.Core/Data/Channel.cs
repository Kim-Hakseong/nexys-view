namespace NexysView.Core.Data;

/// <summary>How X is derived for a channel.</summary>
public enum XKind { Index, Implicit, Explicit }

/// <summary>Source of raw values: reads a contiguous slice into a caller buffer (lazy, mmap-backed).</summary>
public interface IValueSource
{
    long Length { get; }
    /// <summary>Fill dst with values [first, first+dst.Length). Returns number written.</summary>
    int Read(long first, Span<double> dst);
    /// <summary>Opens a sequential scan (full-file passes); the returned reader must be disposed.</summary>
    ISequentialReader OpenSequential() => new DefaultSeq(this);

    sealed class DefaultSeq : ISequentialReader
    {
        readonly IValueSource _s; public DefaultSeq(IValueSource s) => _s = s;
        public int Read(long first, Span<double> dst) => _s.Read(first, dst);
        public void Dispose() { }
    }
}

public interface ISequentialReader : IDisposable
{
    int Read(long first, Span<double> dst);
}

/// <summary>A channel in the data store: metadata + lazy values + optional implicit time base.</summary>
public sealed class Channel
{
    public string Id { get; }              // "<file>|<group>|<name>" unique key
    public string FileName { get; }
    public string Group { get; }
    public string Name { get; }
    public string Unit { get; set; } = "";
    public string Description { get; set; } = "";
    public string DataType { get; set; } = "";
    public long Length => Source.Length;
    public IValueSource Source { get; }
    public IReadOnlyDictionary<string, object> Properties { get; }
    public XKind XKind { get; set; } = XKind.Index;
    public double XStart { get; set; }      // Implicit: x = XStart + i * XIncrement
    public double XIncrement { get; set; } = 1;
    public string XUnit { get; set; } = "";
    public Channel? XChannel { get; set; }  // Explicit: x values from another channel
    public bool IsText { get; set; }
    public bool IsLoaded => Pyramid != null;
    public Pyramid.MinMaxPyramid? Pyramid { get; set; }
    public string DisplayName => $"{Group}/{Name}";

    public Channel(string fileName, string group, string name, IValueSource src, IReadOnlyDictionary<string, object> props)
    {
        FileName = fileName; Group = group; Name = name; Source = src; Properties = props;
        Id = $"{fileName}|{group}|{name}";
    }

    public double XAt(long i)
    {
        if (XKind == XKind.Implicit) return XStart + i * XIncrement;
        if (XKind == XKind.Explicit && XChannel != null && i >= 0 && i < XChannel.Length)
        {
            Span<double> one = stackalloc double[1];
            return XChannel.Source.Read(i, one) == 1 ? one[0] : i;
        }
        return i;
    }

    /// <summary>Fractional sample index for x. Explicit (monotonic) x uses binary search on the x channel.</summary>
    public double IndexOf(double x)
    {
        if (XKind == XKind.Implicit) return (x - XStart) / XIncrement;
        if (XKind == XKind.Explicit && XChannel != null && XChannel.Length > 1)
        {
            long lo = 0, hi = XChannel.Length - 1;
            double xlo = XAt(lo), xhi = XAt(hi);
            if (x <= xlo) return lo + (x - xlo) / Math.Max(1e-300, (xhi - xlo) / (hi - lo));
            if (x >= xhi) return hi + (x - xhi) / Math.Max(1e-300, (xhi - xlo) / (hi - lo));
            while (hi - lo > 1)
            {
                long mid = (lo + hi) / 2; double xm = XAt(mid);
                if (xm <= x) { lo = mid; xlo = xm; } else { hi = mid; xhi = xm; }
            }
            return lo + (xhi > xlo ? (x - xlo) / (xhi - xlo) : 0);
        }
        return x;
    }
    public double XMin => XAt(0);
    public double XMax => Length > 0 ? XAt(Length - 1) : XAt(0);
    public override string ToString() => DisplayName;
}

public sealed class ChannelGroup
{
    public string Name { get; }
    public List<Channel> Channels { get; } = new();
    public IReadOnlyDictionary<string, object> Properties { get; }
    public ChannelGroup(string name, IReadOnlyDictionary<string, object> props) { Name = name; Properties = props; }
}

public sealed class DataFile : IDisposable
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Format { get; }
    public List<ChannelGroup> Groups { get; } = new();
    public IReadOnlyDictionary<string, object> Properties { get; }
    public TimeSpan OpenTime { get; set; }
    /// <summary>Reader warnings (unsupported channel types, truncated segments …) shown in the status bar.</summary>
    public List<string> Warnings { get; } = new();
    readonly IDisposable? _owner;
    public DataFile(string path, string format, IReadOnlyDictionary<string, object> props, IDisposable? owner) { Path = path; Format = format; Properties = props; _owner = owner; }
    public IEnumerable<Channel> AllChannels => Groups.SelectMany(g => g.Channels);
    public int ChannelCount => Groups.Sum(g => g.Channels.Count);
    public void Dispose() => _owner?.Dispose();
}
