using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace NexysView.Core.Dxd;

/// <summary>One recorded channel handed to the writer. Values are row-major (Width items per sample).</summary>
public sealed class RecChannel
{
    public string Name = "", Unit = "";
    public int DataType = 5;              // 5 float32, 7 float64
    public bool IsArray;
    public int Width = 1;
    public List<double> Times = new();
    public List<double> Values = new();
    public double Min, Max = 1;
    public List<string> ArrayNames = new() { "Latitude", "Longitude", "Altitude", "Yaw" };
    public string Index = "";
}

/// <summary>Writer for the MultiStream container. Port of poc/dxd_writer/mswriter.py.</summary>
public static class MultiStreamWriter
{
    public const int HeaderSize = 0x200;
    public const int IndexCap = 2016;

    public record StreamSpec(string Name, byte[] Data, int Capacity, byte Flag);

    static byte[] PStr(string s)
    {
        var b = Encoding.Latin1.GetBytes(s);
        var r = new byte[4 + b.Length];
        BinaryPrimitives.WriteInt32LittleEndian(r, b.Length);
        b.CopyTo(r, 4);
        return r;
    }

    static void Entry(Span<byte> d, long self, string name, long first, long last, int used, int lastIdx, int cap, byte flag)
    {
        BinaryPrimitives.WriteInt64LittleEndian(d, self);
        var nb = Encoding.Latin1.GetBytes(name);
        d.Slice(8, 8).Clear();
        nb.AsSpan(0, Math.Min(8, nb.Length)).CopyTo(d[8..]);
        BinaryPrimitives.WriteInt64LittleEndian(d[16..], first);
        BinaryPrimitives.WriteInt64LittleEndian(d[24..], last);
        BinaryPrimitives.WriteInt32LittleEndian(d[32..], used);
        BinaryPrimitives.WriteInt32LittleEndian(d[36..], lastIdx);
        BinaryPrimitives.WriteInt32LittleEndian(d[40..], cap);
        d[44] = flag;
    }

    public static byte[] Build(IReadOnlyList<StreamSpec> streams,
        string gen0 = "DXD_Data_7.x____dynamicVER2022.4 (RELEASE-221028) (64-bit)", string gen1 = "6.6.8 b08")
    {
        int n = streams.Count;
        int dirSize = 4 + 45 * n;
        int indexPages = Math.Max(1, (dirSize + IndexCap - 1) / IndexCap);
        long pos = HeaderSize + (long)indexPages * (IndexCap + 32);
        var layout = new List<long[]>();
        foreach (var s in streams)
        {
            int np = s.Data.Length > 0 ? (s.Data.Length + s.Capacity - 1) / s.Capacity : 0;
            var pages = new long[np];
            for (int k = 0; k < np; k++) pages[k] = pos + (long)k * (s.Capacity + 32);
            pos += (long)np * (s.Capacity + 32);
            layout.Add(pages);
        }
        var outb = new byte[pos];
        var h = MultiStream.Magic.Concat(PStr(gen0)).Concat(PStr(gen1)).ToArray();
        h.CopyTo(outb, 0);
        var d = new byte[dirSize];
        BinaryPrimitives.WriteInt32LittleEndian(d, n);
        for (int i = 0; i < n; i++)
        {
            var s = streams[i]; var pages = layout[i];
            if (pages.Length > 0)
                Entry(d.AsSpan(4 + 45 * i), 4 + 45 * i, s.Name, pages[0], pages[^1], s.Data.Length - (pages.Length - 1) * s.Capacity, pages.Length - 1, s.Capacity, s.Flag);
            else
                Entry(d.AsSpan(4 + 45 * i), 4 + 45 * i, s.Name, -1, -1, 0, -1, s.Capacity, s.Flag);
        }
        var ipages = new long[indexPages];
        for (int k = 0; k < indexPages; k++) ipages[k] = HeaderSize + (long)k * (IndexCap + 32);
        Entry(outb.AsSpan(0x7E), 0x7E, "___INDEX", ipages[0], ipages[^1], d.Length - (indexPages - 1) * IndexCap, indexPages - 1, IndexCap, 0);

        void PutPages(long[] pages, byte[] data, int cap, int sid)
        {
            for (int k = 0; k < pages.Length; k++)
            {
                var p = outb.AsSpan((int)pages[k]);
                "PAG1"u8.CopyTo(p);
                BinaryPrimitives.WriteInt32LittleEndian(p[4..], k);                  // page number in stream
                BinaryPrimitives.WriteInt64LittleEndian(p[8..], k > 0 ? pages[k - 1] : -1);
                BinaryPrimitives.WriteInt64LittleEndian(p[16..], k + 1 < pages.Length ? pages[k + 1] : -1);
                BinaryPrimitives.WriteInt32LittleEndian(p[24..], sid);
                BinaryPrimitives.WriteInt32LittleEndian(p[28..], 0);
                int len = Math.Min(cap, data.Length - k * cap);
                data.AsSpan(k * cap, len).CopyTo(p[32..]);
            }
        }
        PutPages(ipages, d, IndexCap, -1);
        for (int i = 0; i < n; i++) PutPages(layout[i], streams[i].Data, streams[i].Capacity, i);
        return outb;
    }
}

/// <summary>.dxd writer for async channels (scalar + array). Port of poc/dxd_writer/write_vehsim.py build(); layout docs/20 §1.</summary>
public static class DxdWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Python repr() of a float (shortest round-trip, "x.0" for integral values).</summary>
    public static string PyRepr(double v)
    {
        if (double.IsNaN(v)) return "nan";
        if (double.IsInfinity(v)) return v > 0 ? "inf" : "-inf";
        double a = Math.Abs(v);
        if (a != 0 && (a < 1e-4 || a >= 1e16))
        {
            var r = v.ToString("R", Inv);   // shortest round-trip digits
            if (!r.Contains('E')) r = v.ToString("0.################E+0", Inv);
            var parts = r.Split('E');
            var mant = parts[0];
            int ex = int.Parse(parts[1], Inv);
            return $"{mant}e{(ex < 0 ? "-" : "+")}{Math.Abs(ex):00}";
        }
        var t = v.ToString("R", Inv);
        if (t.Contains('E'))
            t = decimal.Parse(t, NumberStyles.Float, Inv).ToString(Inv);
        if (!t.Contains('.')) t += ".0";
        return t;
    }

    static string PyG(double v) => v.ToString("G6", Inv);
    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    static string OnlineInfo(int l0, int l1)
    {
        var sb = new StringBuilder("<OnlineInfo>");
        for (int lv = 0; lv < 6; lv++) sb.Append($"<IBStream Level=\"{lv}\"><Offset>{(lv == 0 ? l0 : l1)}</Offset></IBStream>");
        return sb.Append("</OnlineInfo>").ToString();
    }

    static void PackVal(List<byte> b, double v, int dt)
    {
        Span<byte> t = stackalloc byte[8];
        if (dt == 7) { BinaryPrimitives.WriteDoubleLittleEndian(t, v); b.AddRange(t[..8].ToArray()); }
        else { BinaryPrimitives.WriteSingleLittleEndian(t, (float)v); b.AddRange(t[..4].ToArray()); }
    }
    static void PackF32(List<byte> b, double v)
    {
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(t, (float)v);
        b.AddRange(t.ToArray());
    }

    public sealed record Result(int Blocks, int Samples, int Mid, int Dir, Dictionary<string, int> Streams);

    public static Result Write(string path, IReadOnlyList<RecChannel> chans, double sampleRate = 100.0, int block = 50, string devtype = "Plugin")
    {
        var bytes = Build(chans, out var res, sampleRate, block, devtype);
        File.WriteAllBytes(path, bytes);
        return res;
    }

    public static byte[] Build(IReadOnlyList<RecChannel> chans, out Result result, double sampleRate = 100.0, int block = 50, string devtype = "Plugin")
    {
        var scal = chans.Where(c => !c.IsArray).ToList();
        var arrs = chans.Where(c => c.IsArray).ToList();
        double tEnd = chans.Where(c => c.Times.Count > 0).Max(c => c.Times.Max()) + 1 / sampleRate;
        int nsamp = (int)Math.Round(tEnd * sampleRate);
        int nblk = (nsamp + block - 1) / block;
        int mid = nblk, dirv = nsamp - nblk * block;
        double bt = block / sampleRate;

        // DBASDAT0 + IBDATA0 (level 0: one record per block, 40 B per async scalar channel)
        int rec = 40 * scal.Count;
        var db = new List<byte>();
        var ib = new byte[rec * nblk];
        var cum = new int[scal.Count];
        for (int b = 1; b <= nblk; b++)
        {
            for (int k = 0; k < scal.Count; k++)
            {
                var c = scal[k];
                var idx = new List<int>();
                for (int i = 0; i < c.Times.Count; i++)
                {
                    double t = c.Times[i];
                    if ((b - 1) * bt - 1e-9 <= t && t < b * bt - 1e-9) idx.Add(i);
                }
                int pos = db.Count;
                foreach (var i in idx) PackVal(db, c.Values[i], c.DataType);
                foreach (var i in idx) PackF32(db, c.Times[i] - (b - 1) * bt);
                cum[k] += idx.Count;
                var r = ib.AsSpan((b - 1) * rec + 40 * k);
                if (idx.Count > 0)
                {
                    double mn = double.MaxValue, mx = double.MinValue, sum = 0;
                    foreach (var i in idx) { var v = c.Values[i]; mn = Math.Min(mn, v); mx = Math.Max(mx, v); sum += v; }
                    BinaryPrimitives.WriteSingleLittleEndian(r, (float)mn);
                    BinaryPrimitives.WriteSingleLittleEndian(r[4..], (float)mx);
                    BinaryPrimitives.WriteSingleLittleEndian(r[8..], (float)(sum / idx.Count));
                }
                BinaryPrimitives.WriteInt32LittleEndian(r[16..], idx.Count);
                BinaryPrimitives.WriteInt32LittleEndian(r[20..], b);
                BinaryPrimitives.WriteInt32LittleEndian(r[24..], cum[k]);
                BinaryPrimitives.WriteInt32LittleEndian(r[28..], 0);
                BinaryPrimitives.WriteInt64LittleEndian(r[32..], pos);
            }
        }
        // array channels: DBADxxxx + IB01xxxx, one chunk per level-1 record (all data fits in one: < 10 s)
        var extra = new List<MultiStreamWriter.StreamSpec>();
        for (int a = 0; a < arrs.Count; a++)
        {
            var c = arrs[a];
            int n = c.Times.Count, width = c.Width;
            var d = new List<byte>();
            for (int i = 0; i < n * width; i++) PackVal(d, c.Values[i], c.DataType);
            foreach (var t in c.Times) PackF32(d, t);
            var ix = new byte[48];
            for (int j = 0; j < 3; j++) BinaryPrimitives.WriteDoubleLittleEndian(ix.AsSpan(8 * j), j < width && n > 0 ? c.Values[j] : 0.0);
            BinaryPrimitives.WriteInt64LittleEndian(ix.AsSpan(24), n);
            BinaryPrimitives.WriteInt64LittleEndian(ix.AsSpan(32), n);
            BinaryPrimitives.WriteInt64LittleEndian(ix.AsSpan(40), 0);
            extra.Add(new($"DBAD{a:D4}", d.ToArray(), 992, 1));
            extra.Add(new($"IB01{a:D4}", ix, 5088, 1));
        }
        // setup XML
        var slots = new StringBuilder();
        for (int k = 0; k < scal.Count; k++)
        {
            var c = scal[k];
            slots.Append($"<Slot Index=\"{k}\"><OutputChannel><Name>{Esc(c.Name)}</Name><Unit>{Esc(c.Unit)}</Unit>"
                + $"<UserScaleMin>{PyRepr(c.Min)}</UserScaleMin><UserScaleMax>{PyRepr(c.Max)}</UserScaleMax><Used>True</Used>"
                + $"<Index>{devtype};0;{k}</Index><DataType>{c.DataType}</DataType><RangeMax>{PyRepr(c.Max)}</RangeMax>"
                + $"<RangeMin>{PyRepr(c.Min)}</RangeMin><Async>True</Async><ExpectedAsyncRate>100</ExpectedAsyncRate>"
                + $"{OnlineInfo(40 * k, 32 * k)}</OutputChannel></Slot>");
            c.Index = $"{devtype};0;{k}";
        }
        string Offline(int a) =>
            $"<OfflineInfo><DBStreamName>DBAD{a:D4}</DBStreamName><PosInDBStream>0</PosInDBStream><StartPos>0</StartPos>"
            + $"<EndPos>{mid}</EndPos><ExactStartPos_Mid>0</ExactStartPos_Mid><ExactEndPos_Mid>{mid}</ExactEndPos_Mid>"
            + $"<ExactEndPos_Dir>{dirv}</ExactEndPos_Dir><IBStream Level=\"0\" /><IBStream Level=\"1\"><IBStreamName>IB01{a:D4}"
            + "</IBStreamName><PosInIBStream>0</PosInIBStream></IBStream></OfflineInfo>";
        var math = new StringBuilder();
        for (int a = 0; a < arrs.Count; a++)
        {
            var c = arrs[a];
            var names = string.Join(';', c.ArrayNames.Take(c.Width));
            var arr = $"<ArrayInfo><Axis Index=\"0\"><Size>{c.Width}</Size><Name>Value</Name><AxisType>0</AxisType>"
                    + $"<StringValues>{c.Width};{names}</StringValues></Axis><StoreIB>True</StoreIB></ArrayInfo>";
            math.Append($"<Math Type=\"navigation\" Index=\"{a}\" ClassIndex=\"0\"><OutputChannel Descr=\"{c.Name}\" Index=\"0\">"
                + $"<Name>{c.Name}</Name><Unit /><Used>True</Used><Index>Math;{a};0;{c.Name}</Index>"
                + $"<DataType>{c.DataType}</DataType><Async>True</Async><ExpectedAsyncRate>100</ExpectedAsyncRate>"
                + $"<StoredFirstAsyncIBLevel>1</StoredFirstAsyncIBLevel>{arr}{Offline(a)}</OutputChannel></Math>");
            c.Index = $"Math;{a};0;{c.Name}";
        }
        var stored = new StringBuilder();
        foreach (var c in chans)   // keep the host's channel order
        {
            var body = $"<AsyncSamples>{c.Times.Count}</AsyncSamples>";
            if (c.IsArray) body += $"<Name>{c.Name}</Name>" + Offline(arrs.IndexOf(c));
            stored.Append($"<Channel Index=\"{c.Index}\">{body}</Channel>");
        }
        var devOi = new StringBuilder();
        for (int lv = 0; lv < 6; lv++) devOi.Append($"<IBStream Level=\"{lv}\"><Offset>{(lv == 0 ? rec : 32 * scal.Count)}</Offset></IBStream>");
        var xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<DataFileSetup><System Name=\"Local\"><SysInfo><Software>"
            + "<VersionCreated>6</VersionCreated><VersionModifed>6.6.8 b08</VersionModifed></Software></SysInfo>"
            + "<DXDSetup><Devices><StartStoreTime>46000.0</StartStoreTime>"
            + $"<SampleRate>{PyG(sampleRate)}</SampleRate><BlockSize>{block}</BlockSize><UserBlockTime>{PyG(bt)}</UserBlockTime>"
            + $"<StartZoomTime>0</StartZoomTime><EndZoomTime>{PyG(nsamp / sampleRate)}</EndZoomTime>"
            + $"<Device Type=\"{devtype}\">{slots}</Device>"
            + $"<OnlineInfo><DBOffset>0</DBOffset>{devOi}</OnlineInfo>"
            + $"</Devices><StoredChannels>{stored}</StoredChannels><Math>{math}</Math>"
            + "</DXDSetup></System></DataFileSetup>");
        // events: start + stop (Mid, Dir), field pattern of the example (docs/18 §2.5)
        var ev = new List<byte>();
        void I32(int v) { var t = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(t, v); ev.AddRange(t); }
        void I64(long v) { var t = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(t, v); ev.AddRange(t); }
        var es = DxdReader.EventSep; var ee = (byte[])es.Clone(); ee[0] = 0x87;
        I32(2); I32(1); ev.AddRange(es); I32(3); I64(0); I64(0); I32(2); I64(0); ev.AddRange(ee);
        I32(2); ev.AddRange(es); I32(3); I32(mid); I32(dirv); I64(mid - 1); I32(2); I32(mid - 1); I32(10); ev.AddRange(ee);
        I32(-1);
        var info = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<Info>\r\n\t<SampleRate>{PyG(sampleRate)}</SampleRate>\r\n"
            + "\t<StartStoreTime>46000.0</StartStoreTime>\r\n</Info>\r\n");
        var streams = new List<MultiStreamWriter.StreamSpec>
        {
            new("EVENTS", ev.ToArray(), 4064, 1), new("DBDATA", Array.Empty<byte>(), 4064, 1), new("DBASDAT0", db.ToArray(), 32736, 1),
            new("IBDATA0", ib, 32736, 1), new("SETUP__", xml, 32736, 0), new("INFO__", info, 992, 0),
        };
        streams.AddRange(extra);
        result = new Result(nblk, nsamp, mid, dirv, streams.ToDictionary(s => s.Name, s => s.Data.Length));
        return MultiStreamWriter.Build(streams);
    }
}
