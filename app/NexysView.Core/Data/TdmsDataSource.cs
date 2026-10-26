using NexysView.Core.Tdms;

namespace NexysView.Core.Data;

/// <summary>Opens a .tdms as a DataFile: metadata immediately, values lazily through the memory map.</summary>
public static class TdmsDataSource
{
    sealed class Src : IValueSource
    {
        readonly TdmsFile _f; readonly TdmsObject _o;
        public Src(TdmsFile f, TdmsObject o) { _f = f; _o = o; }
        public long Length => _o.Count;
        public int Read(long first, Span<double> dst) => _f.ReadDoubles(_o, first, dst);
        public ISequentialReader OpenSequential() => new Seq(_f, _o);
    }

    sealed class Seq : ISequentialReader
    {
        readonly TdmsFile _f; readonly TdmsObject _o; FileStream? _fs;
        public Seq(TdmsFile f, TdmsObject o) { _f = f; _o = o; }
        public int Read(long first, Span<double> dst) => _f.ReadDoublesSequential(_o, first, dst, ref _fs);
        public void Dispose() => _fs?.Dispose();
    }

    public static DataFile Open(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = new TdmsFile(path);
        var fprops = new Dictionary<string, object>(f.Root.Properties) { ["segments"] = f.Segments.Count };
        var df = new DataFile(path, "TDMS", fprops, f);
        var groups = new Dictionary<string, ChannelGroup>(StringComparer.Ordinal);
        foreach (var o in f.Objects)
        {
            if (o.IsGroup) { if (!groups.ContainsKey(o.Name)) { var g = new ChannelGroup(o.Name, o.Properties); groups[o.Name] = g; df.Groups.Add(g); } }
        }
        foreach (var o in f.Objects)
        {
            if (!o.IsChannel) continue;
            if (!groups.TryGetValue(o.Group, out var g)) { g = new ChannelGroup(o.Group, new Dictionary<string, object>()); groups[o.Group] = g; df.Groups.Add(g); }
            var ch = new Channel(df.Name, o.Group, o.Name, new Src(f, o), o.Properties)
            {
                DataType = TdsTypes.Name(o.DataType),
                IsText = o.DataType == TdsType.String,
                Unit = Str(o.Properties, "unit_string"),
                Description = Str(o.Properties, "description"),
            };
            if (o.Properties.TryGetValue("wf_increment", out var inc) && inc is double dinc && dinc > 0)
            {
                ch.XKind = XKind.Implicit;
                ch.XIncrement = dinc;
                ch.XStart = o.Properties.TryGetValue("wf_start_offset", out var so) && so is double dso ? dso : 0;
                ch.XUnit = Str(o.Properties, "wf_xunit_string", "s");
            }
            if (o.DataType == TdsType.DaqmxRaw) df.Warnings.Add($"{o.Group}/{o.Name}: DAQmx raw data is not decoded (values read as NaN)");
            else if (o.DataType is TdsType.ComplexSingle or TdsType.ComplexDouble) df.Warnings.Add($"{o.Group}/{o.Name}: complex data shown as real part");
            g.Channels.Add(ch);
        }
        foreach (var w in f.Warnings) df.Warnings.Add(w);
        df.OpenTime = sw.Elapsed;
        return df;
    }

    static string Str(IReadOnlyDictionary<string, object> p, string k, string dflt = "") => p.TryGetValue(k, out var v) && v is string s ? s : dflt;
}
