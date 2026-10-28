using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace NexysView.Core.Dxd;

/// <summary>Channel described by the DataFileSetup XML (port of poc/dxd_reader/dxd_reader.py Channel).</summary>
public sealed class DxdChannel
{
    public string IndexStr = "", Name = "", Unit = "";
    public int Pos;
    public XElement Elem = null!;
    public XElement? Msg;
    public bool IsCanMsg, IsCanSignal, IsAsync;
    public int ArraySize = 1, DataType;
    public double Scale = 1, Offset;
    public int? IbOffset;
    public int StartBit, BitLen = 8;
    public List<string> ArrayNames = new();

    public int ElemSize => DxdTypes.Size(DataType);
    public override string ToString() => $"{Pos} {IndexStr} {Name} [{Unit}]";
}

/// <summary>Samples of one channel. Values are raw (unscaled) and row-major for array channels (Width items per sample).</summary>
public sealed class DxdSamples
{
    public double[] Values = Array.Empty<double>();
    public double[] Times = Array.Empty<double>();
    public int Width = 1;
    public int Count => Times.Length;
}

public static class DxdTypes
{
    // DWDataType codes (ref/data-reader DWDataReaderLibTypes.h)
    public static int Size(int dt) => dt switch { 0 or 1 => 1, 2 or 3 => 2, 4 or 5 or 8 => 4, 6 or 7 => 8, 15 => 8, _ => throw new NotSupportedException($"data type {dt}") };

    public static double Read(ReadOnlySpan<byte> b, int dt) => dt switch
    {
        0 => b[0],
        1 => (sbyte)b[0],
        2 => BinaryPrimitives.ReadInt16LittleEndian(b),
        3 => BinaryPrimitives.ReadUInt16LittleEndian(b),
        4 => BinaryPrimitives.ReadInt32LittleEndian(b),
        5 => BinaryPrimitives.ReadSingleLittleEndian(b),
        6 => BinaryPrimitives.ReadInt64LittleEndian(b),
        7 => BinaryPrimitives.ReadDoubleLittleEndian(b),
        8 => BinaryPrimitives.ReadUInt32LittleEndian(b),
        _ => throw new NotSupportedException($"data type {dt}"),
    };
}

/// <summary>Independent .dxd reader (no DXD DLL). Port of poc/dxd_reader/dxd_reader.py; layout docs/18.</summary>
public sealed class DxdReader
{
    public MultiStream Ms { get; }
    public XElement Setup { get; }
    public double SampleRate { get; }
    public int BlockSize { get; }
    public double StartStoreTime { get; }
    public int? IbRecord { get; }
    public List<DxdChannel> Channels { get; } = new();
    public long SyncCount { get; }
    readonly Dictionary<string, DxdChannel> _msgByIndex = new();

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string? F(XElement e, string tag)
    {
        var c = e.Element(tag);
        if (c == null) return null;
        var t = c.Value;
        return t == "" ? null : t;
    }
    static double Fd(XElement e, string tag, double def) => F(e, tag) is { } t ? double.Parse(t, Inv) : def;
    static int Fi(XElement e, string tag, int def) => F(e, tag) is { } t ? int.Parse(t, Inv) : def;

    public DxdReader(string path) : this(new MultiStream(path)) { }

    public DxdReader(MultiStream ms)
    {
        Ms = ms;
        var name = ms.Has("SETUP__") ? "SETUP__" : "SETUP";
        Setup = XDocument.Load(new MemoryStream(ms.Stream(name))).Root!;
        var ds = Setup.Element("System")!.Element("DXDSetup")!;
        var dv = ds.Element("Devices")!;
        SampleRate = double.Parse(dv.Element("SampleRate")!.Value, Inv);
        BlockSize = int.Parse(dv.Element("BlockSize")!.Value, Inv);
        StartStoreTime = double.Parse(dv.Element("StartStoreTime")!.Value, Inv);
        var dib = Level(dv.Element("OnlineInfo"), "0")?.Element("Offset");
        IbRecord = dib != null ? int.Parse(dib.Value, Inv) : null;

        var byIndex = new Dictionary<string, XElement>();
        foreach (var e in Setup.DescendantsAndSelf())
        {
            var ix = e.Element("Index");
            if (ix != null && ix.Value != "" && !ix.HasElements)
                byIndex.TryAdd(ix.Value.Trim(), e);
        }
        int pos = 0;
        foreach (var c in ds.Element("StoredChannels")!.Elements("Channel"))
        {
            var idx = c.Attribute("Index")!.Value;
            if (byIndex.TryGetValue(idx, out var el))
                Channels.Add(MakeChannel(idx, pos, el, null));
            else
            {
                var parts = idx.Split(';');   // CAN;port;msgid;signal
                byIndex.TryGetValue(string.Join(';', parts.Take(3)), out var msg);
                var sig = msg != null && parts.Length == 4
                    ? msg.Elements("OutputChannel").FirstOrDefault(o => (string?)o.Attribute("Index") == parts[3]) : null;
                if (sig == null) throw new InvalidDataException($"stored channel {idx} not found in setup");
                Channels.Add(MakeChannel(idx, pos, sig, msg));
            }
            pos++;
        }
        foreach (var ch in Channels.Where(c => c.IsCanMsg)) _msgByIndex[ch.IndexStr] = ch;
        SyncCount = ComputeSyncCount();
    }

    static XElement? Level(XElement? oi, string lv) =>
        oi?.Elements("IBStream").FirstOrDefault(x => (string?)x.Attribute("Level") == lv);

    static DxdChannel MakeChannel(string idx, int pos, XElement elem, XElement? parentMsg)
    {
        var ch = new DxdChannel { IndexStr = idx, Pos = pos, Elem = elem, Msg = parentMsg };
        ch.Name = F(elem, "Name") ?? "";
        ch.Unit = F(elem, "Unit") ?? "";
        ch.IsCanMsg = elem.Name.LocalName == "Message";
        ch.IsCanSignal = parentMsg != null;
        ch.IsAsync = ch.IsCanMsg || ch.IsCanSignal || (F(elem, "Async") ?? "False") == "True";
        var si = elem.Element("ArrayInfo")?.Element("Axis")?.Element("StringValues");
        if (si != null && si.Value != "")
        {
            var sp = si.Value.Split(';');
            ch.ArraySize = int.Parse(sp[0], Inv);
            ch.ArrayNames = sp.Skip(1).ToList();
        }
        var dt = F(elem, "DataType");
        ch.DataType = dt != null ? int.Parse(dt, Inv) : ((F(elem, "BitsLog") ?? "16") == "16" ? 2 : 4);
        if (idx.StartsWith("AI;"))
        {
            int bits = Fi(elem, "BitsLog", 16);
            double rmin = Fd(elem, "RangeMin", 0), rmax = Fd(elem, "RangeMax", 0);
            ch.Scale = (rmax - rmin) / Math.Pow(2, bits);
            ch.Offset = (rmax + rmin) / 2;
        }
        else
        {
            ch.Scale = Fd(elem, "Scale", 1.0);
            ch.Offset = Fd(elem, "Offset", 0.0);
        }
        var oi = Level(elem.Element("OnlineInfo"), "0")?.Element("Offset");
        ch.IbOffset = oi != null ? int.Parse(oi.Value, Inv) : null;
        if (ch.IsCanSignal)
        {
            ch.StartBit = Fi(elem, "StartBit", 0);
            ch.BitLen = Fi(elem, "BitLength", 8);
        }
        return ch;
    }

    // the stop event holds (block, sample offset) of the end of storing (docs/18 §2.5)
    long ComputeSyncCount()
    {
        var ev = Ms.Has("EVENTS") ? Ms.Stream("EVENTS") : Array.Empty<byte>();
        var parts = Split(ev, EventSep);
        if (parts.Count >= 3)
        {
            var p = parts[2];
            int blk = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(4)), ofs = BinaryPrimitives.ReadInt32LittleEndian(p.AsSpan(8));
            return (long)blk * BlockSize + ofs;
        }
        int bb = SyncBlockBytes();
        return bb == 0 ? 0 : Ms.Stream("DBDATA").Length / bb * (long)BlockSize;
    }

    public static readonly byte[] EventSep = { 0x86, (byte)'E', (byte)'v', (byte)'e', (byte)'n', (byte)'t', (byte)'S' };

    static List<byte[]> Split(byte[] data, byte[] sep)
    {
        var res = new List<byte[]>();
        int start = 0;
        for (int i = 0; i + sep.Length <= data.Length;)
        {
            if (data.AsSpan(i, sep.Length).SequenceEqual(sep)) { res.Add(data[start..i]); i += sep.Length; start = i; }
            else i++;
        }
        res.Add(data[start..]);
        return res;
    }

    IEnumerable<DxdChannel> SyncChannels => Channels.Where(c => !c.IsAsync);
    int SyncBlockBytes() => SyncChannels.Sum(c => c.ElemSize * c.ArraySize) * BlockSize;

    public DxdSamples ReadSync(DxdChannel ch)
    {
        var db = Ms.Stream("DBDATA");
        int bb = SyncBlockBytes(), before = 0;
        foreach (var c in SyncChannels) { if (c == ch) break; before += c.ElemSize * c.ArraySize * BlockSize; }
        int size = ch.ElemSize;
        long nb = (SyncCount + BlockSize - 1) / BlockSize;
        var vals = new double[SyncCount];
        long w = 0;
        for (long b = 0; b < nb && w < SyncCount; b++)
        {
            long o = b * bb + before;
            for (int i = 0; i < BlockSize && w < SyncCount; i++)
                vals[w++] = DxdTypes.Read(db.AsSpan((int)(o + i * size)), ch.DataType);
        }
        var ts = new double[vals.Length];
        for (int i = 0; i < ts.Length; i++) ts[i] = i / SampleRate;
        return new DxdSamples { Values = vals, Times = ts };
    }

    DxdSamples Clip(List<double> vals, List<double> ts, int width)
    {
        // samples stamped after the stop position are not part of the recording (official reader drops them)
        double dur = SyncCount / SampleRate;
        int n = ts.Count(t => t <= dur + 1e-9);
        return new DxdSamples { Values = vals.Take(n * width).ToArray(), Times = ts.Take(n).ToArray(), Width = width };
    }

    /// <summary>[(block, n, cumulative, position)] from IBDATA0 for an async scalar channel or CAN message.</summary>
    public List<(int blk, int n, int cum, long pos)> AsyncIndex(DxdChannel ch)
    {
        var ib = Ms.Stream("IBDATA0");
        int rec = IbRecord ?? throw new InvalidDataException("no IB record size");
        int rel = ch.IsCanMsg ? ch.IbOffset!.Value : ch.IbOffset!.Value + 16;
        var res = new List<(int, int, int, long)>();
        for (int b = 1; (long)(b - 1) * rec + rel + 24 <= ib.Length; b++)
        {
            var s = ib.AsSpan((b - 1) * rec + rel);
            int n = BinaryPrimitives.ReadInt32LittleEndian(s), blk = BinaryPrimitives.ReadInt32LittleEndian(s[4..]);
            int cum = BinaryPrimitives.ReadInt32LittleEndian(s[8..]);
            long p = BinaryPrimitives.ReadInt64LittleEndian(s[16..]);
            if (blk != b) break;
            res.Add((blk, n, cum, p));
        }
        return res;
    }

    public DxdChannel MessageOf(DxdChannel sig) => _msgByIndex[F(sig.Msg!, "Index")!];

    public DxdSamples ReadAsync(DxdChannel ch, bool clip = true)
    {
        bool can = ch.IsCanMsg || ch.IsCanSignal;
        int size = can ? 8 : ch.ElemSize;
        var data = Ms.Stream("DBASDAT0");
        var vals = new List<double>();
        var ts = new List<double>();
        double blockT = BlockSize / SampleRate;
        var src = ch.IsCanSignal ? MessageOf(ch) : ch;
        ulong mask = ch.BitLen >= 64 ? ulong.MaxValue : (1UL << ch.BitLen) - 1;
        foreach (var (blk, n, _, p) in AsyncIndex(src))
        {
            for (int j = 0; j < n; j++)
            {
                var s = data.AsSpan((int)(p + (long)j * size));
                if (ch.IsCanSignal) vals.Add((BinaryPrimitives.ReadUInt64LittleEndian(s) >> ch.StartBit) & mask);
                else if (ch.IsCanMsg) vals.Add(BinaryPrimitives.ReadUInt64LittleEndian(s));
                else vals.Add(DxdTypes.Read(s, ch.DataType));
            }
            for (int j = 0; j < n; j++)
                ts.Add((blk - 1) * blockT + BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)(p + (long)n * size + 4L * j))));
        }
        if (clip) return Clip(vals, ts, 1);
        return new DxdSamples { Values = vals.ToArray(), Times = ts.ToArray() };
    }

    /// <summary>Array async channel k-th in DBAD streams (DBAD%04d / IB01%04d).</summary>
    public DxdSamples ReadArrayAsync(DxdChannel ch, int k)
    {
        var data = Ms.Stream($"DBAD{k:D4}");
        var idx = Ms.Stream($"IB01{k:D4}");
        int es = ch.ElemSize, size = es * ch.ArraySize;
        var dv = Setup.Element("System")!.Element("DXDSetup")!.Element("Devices")!.Element("OnlineInfo");
        int l1 = int.Parse(Level(dv, "1")!.Element("Offset")!.Value, Inv);
        int perL1 = 20;   // ratio seen in Example drive.dxd (UNVERIFIED as a constant)
        if (Ms.Has("IBDATA1") && Ms.Entries["IBDATA1"].Size > 0)
            perL1 = (Ms.Stream("IBDATA0").Length / IbRecord!.Value) / (Ms.Stream("IBDATA1").Length / l1);
        double tL1 = perL1 * BlockSize / SampleRate;
        var vals = new List<double>();
        var ts = new List<double>();
        for (int i = 0; i < idx.Length / 48; i++)
        {
            var e = idx.AsSpan(i * 48);
            long n = BinaryPrimitives.ReadInt64LittleEndian(e[24..]);
            long p = BinaryPrimitives.ReadInt64LittleEndian(e[40..]);
            for (long j = 0; j < n; j++)
                for (int a = 0; a < ch.ArraySize; a++)
                    vals.Add(DxdTypes.Read(data.AsSpan((int)(p + j * size + a * es)), ch.DataType));
            for (long j = 0; j < n; j++)
                ts.Add(i * tL1 + BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan((int)(p + n * size + 4 * j))));
        }
        return Clip(vals, ts, ch.ArraySize);
    }

    public DxdSamples Read(DxdChannel ch)
    {
        if (!ch.IsAsync) return ReadSync(ch);
        if (ch.ArraySize > 1)
        {
            int k = Channels.Where(c => c.IsAsync && c.ArraySize > 1).ToList().IndexOf(ch);
            return ReadArrayAsync(ch, k);
        }
        return ReadAsync(ch);
    }

    public double[] Scaled(DxdChannel ch, DxdSamples raw)
    {
        var r = new double[raw.Values.Length];
        for (int i = 0; i < r.Length; i++) r[i] = raw.Values[i] * ch.Scale + ch.Offset;
        return r;
    }

    public double Duration => SyncCount / SampleRate;
}
