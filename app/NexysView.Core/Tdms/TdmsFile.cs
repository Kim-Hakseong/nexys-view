using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace NexysView.Core.Tdms;

/// <summary>A raw-data chunk of one channel inside one segment.</summary>
public readonly record struct TdmsChunk(long Offset, long Count, TdsType Type, bool BigEndian, int RowStride, long StringBytes);

public sealed class TdmsObject
{
    public string Path { get; }
    public string[] Parts { get; }
    public Dictionary<string, object> Properties { get; } = new(StringComparer.Ordinal);
    public TdsType DataType { get; internal set; } = TdsType.Void;
    public List<TdmsChunk> Chunks { get; } = new();
    public long Count { get; internal set; }
    public bool IsGroup => Parts.Length == 1;
    public bool IsChannel => Parts.Length == 2;
    public string Name => Parts.Length == 0 ? "" : Parts[^1];
    public string Group => Parts.Length == 0 ? "" : Parts[0];
    internal TdmsObject(string path) { Path = path; Parts = TdmsPath.Split(path); }
    public override string ToString() => $"{Path} {TdsTypes.Name(DataType)} n={Count}";
}

public sealed class TdmsSegment
{
    public long Position;
    public Toc Toc;
    public uint Version;
    public long NextSegmentOffset;
    public long RawDataOffset;
    public bool Incomplete;
    public int Chunks;
    public long ChunkSize;
}

/// <summary>
/// Independent TDMS reader (port of tools/tdms_py.py): memory-mapped, lazy values. Handles segment ToC flags,
/// object-list carry-over, raw-index reuse, interleaved/contiguous layout, strings, timestamps, big-endian,
/// DAQmx raw (reported, not decoded) and an incomplete last segment (ignored, like the reference TDMS library).
/// </summary>
public sealed class TdmsFile : IDisposable
{
    const uint NoData = 0xFFFFFFFF, SameAsPrev = 0;
    const uint DaqmxFmt = 0x00001269, DaqmxDig = 0x0000126A;

    public string FileName { get; }
    public long Length { get; }
    public IReadOnlyList<TdmsSegment> Segments => _segments;
    public List<string> Warnings { get; } = new();
    public IReadOnlyList<TdmsObject> Objects => _order;
    public TdmsObject Root => _objects.TryGetValue("/", out var r) ? r : (_objects["/"] = AddObj("/"));

    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _acc;
    readonly unsafe byte* _base;
    readonly Dictionary<string, TdmsObject> _objects = new(StringComparer.Ordinal);
    readonly List<TdmsObject> _order = new();
    readonly List<TdmsSegment> _segments = new();
    bool _disposed;

    public unsafe TdmsFile(string path)
    {
        FileName = path;
        Length = new FileInfo(path).Length;
        _mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _acc.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + _acc.PointerOffset;
        ReadStructure();
    }

    public TdmsObject? Find(string path) => _objects.TryGetValue(path, out var o) ? o : null;
    public IEnumerable<TdmsObject> Groups() => _order.Where(o => o.IsGroup);
    public IEnumerable<TdmsObject> Channels(string? group = null) => _order.Where(o => o.IsChannel && (group == null || o.Group == group));

    TdmsObject AddObj(string path)
    {
        var o = new TdmsObject(path);
        _objects[path] = o;
        _order.Add(o);
        return o;
    }

    unsafe ReadOnlySpan<byte> Span(long off, long len) => new(_base + off, checked((int)len));

    // ------------------------------------------------------------------ structure
    sealed class RawIndex { public TdsType Type; public long Count; public long Bytes; public bool Daqmx; public long Width; }

    void ReadStructure()
    {
        long pos = 0;
        var prevList = new List<(TdmsObject obj, RawIndex? ix)>();
        var prevIndex = new Dictionary<string, RawIndex>(StringComparer.Ordinal);
        while (pos + 28 <= Length)
        {
            var lead = Span(pos, 28);
            if (!(lead[0] == (byte)'T' && lead[1] == (byte)'D' && lead[2] == (byte)'S' && lead[3] == (byte)'m'))
                throw new InvalidDataException($"bad TDMS segment tag at {pos}");
            var toc = (Toc)BinaryPrimitives.ReadUInt32LittleEndian(lead[4..]);
            bool be = (toc & Toc.BigEndian) != 0;
            uint ver = be ? BinaryPrimitives.ReadUInt32BigEndian(lead[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(lead[8..]);
            ulong next = be ? BinaryPrimitives.ReadUInt64BigEndian(lead[12..]) : BinaryPrimitives.ReadUInt64LittleEndian(lead[12..]);
            ulong rawOff = be ? BinaryPrimitives.ReadUInt64BigEndian(lead[20..]) : BinaryPrimitives.ReadUInt64LittleEndian(lead[20..]);
            var seg = new TdmsSegment { Position = pos, Toc = toc, Version = ver, NextSegmentOffset = (long)next, RawDataOffset = (long)rawOff };
            seg.Incomplete = next == ulong.MaxValue;
            // length-field validation (stability task [4]): a next-offset that does not move forward, or that points past the
            // file, or a metadata length beyond the segment is treated like an incomplete last segment (progress guaranteed)
            if (!seg.Incomplete && (next < rawOff || (ulong)pos + 28 + next > (ulong)Length || (ulong)pos + 28 + rawOff > (ulong)Length))
            {
                Warnings.Add($"segment {_segments.Count} at {pos}: invalid length fields (next={next}, meta={rawOff}); rest of file ignored");
                seg.Incomplete = true; seg.NextSegmentOffset = -1;
                if ((ulong)pos + 28 + rawOff > (ulong)Length) { _segments.Add(seg); break; }
            }
            long dataStart = pos + 28 + (long)rawOff;
            long segEnd = seg.Incomplete ? Length : Math.Min(pos + 28 + (long)next, Length);
            if ((toc & Toc.MetaData) != 0)
            {
                var cur = (toc & Toc.NewObjList) != 0 ? new List<(TdmsObject, RawIndex?)>() : new List<(TdmsObject, RawIndex?)>(prevList);
                ParseMeta(pos + 28, (long)rawOff, be, cur, prevIndex);
                prevList = cur;
            }
            // the reference TDMS library ignores the raw data of a segment whose next-offset is -1 (measured, work/tdms/crafted_le.tdms)
            if ((toc & Toc.RawData) != 0 && !seg.Incomplete)
                Layout(seg, prevList, dataStart, segEnd, be, toc);
            _segments.Add(seg);
            if (seg.Incomplete) break;
            pos = pos + 28 + (long)next;
        }
        foreach (var o in _order) if (o.DataType == TdsType.DaqmxRaw) Warnings.Add($"{o.Path}: DAQmx raw channel not decoded");
    }

    void ParseMeta(long off, long len, bool be, List<(TdmsObject obj, RawIndex? ix)> cur, Dictionary<string, RawIndex> prevIndex)
    {
        var b = Span(off, len);
        int p = 0;
        uint n = U32(b, ref p, be);
        for (uint k = 0; k < n; k++)
        {
            string path = Str(b, ref p, be);
            var obj = _objects.TryGetValue(path, out var o) ? o : AddObj(path);
            uint ri = U32(b, ref p, be);
            RawIndex? ix = null;
            if (ri == NoData) { }
            else if (ri == SameAsPrev) { prevIndex.TryGetValue(path, out ix); }
            else if (ri == DaqmxFmt || ri == DaqmxDig)
            {
                var dt = U32(b, ref p, be); U32(b, ref p, be); long cnt = (long)U64(b, ref p, be);
                uint nsc = U32(b, ref p, be); p += (int)nsc * 17;
                uint nw = U32(b, ref p, be); long width = 0;
                for (uint w = 0; w < nw; w++) width += U32(b, ref p, be);
                ix = new RawIndex { Type = TdsType.DaqmxRaw, Count = cnt, Daqmx = true, Width = width };
            }
            else
            {
                int start = p;
                if (ri < 16 || start + (long)ri - 4 > b.Length) throw new InvalidDataException($"object '{path}': raw index length {ri} out of range");
                var dt = (TdsType)U32(b, ref p, be); U32(b, ref p, be); long cnt = (long)U64(b, ref p, be);
                if (cnt < 0 || (dt != TdsType.String && TdsTypes.Size(dt) <= 0 && dt != TdsType.Void)) throw new InvalidDataException($"object '{path}': unsupported data type 0x{(uint)dt:X}");
                ix = new RawIndex { Type = dt, Count = cnt };
                if (dt == TdsType.String) ix.Bytes = (long)U64(b, ref p, be);
                p = start + (int)ri - 4;   // index length includes its own 4-byte field
            }
            if (ix != null) { prevIndex[path] = ix; obj.DataType = ix.Type; }
            int found = cur.FindIndex(e => ReferenceEquals(e.obj, obj));
            if (found >= 0) cur[found] = (obj, ix);
            else if (ix != null) cur.Add((obj, ix));
            uint np = U32(b, ref p, be);
            for (uint q = 0; q < np; q++)
            {
                string name = Str(b, ref p, be);
                var dt = (TdsType)U32(b, ref p, be);
                obj.Properties[name] = ReadValue(b, ref p, dt, be);
            }
        }
        cur.RemoveAll(e => e.ix == null);
    }

    void Layout(TdmsSegment seg, List<(TdmsObject obj, RawIndex? ix)> lst, long start, long end, bool be, Toc toc)
    {
        var active = lst.Where(e => e.ix != null && e.ix.Count > 0).ToList();
        if (active.Count == 0) return;
        var sizes = active.Select(e => e.ix!.Type == TdsType.String ? e.ix.Bytes : e.ix.Daqmx ? e.ix.Width * e.ix.Count : (long)TdsTypes.Size(e.ix.Type) * e.ix.Count).ToArray();
        long chunk = sizes.Sum();
        long total = end - start;
        long nchunks = chunk > 0 ? total / chunk : 0;
        long rem = total - nchunks * chunk;
        bool inter = (toc & Toc.Interleaved) != 0;
        seg.Chunks = (int)nchunks; seg.ChunkSize = chunk;
        for (long c = 0; c < nchunks + (rem > 0 ? 1 : 0); c++)
        {
            long bas = start + c * chunk;
            bool last = c == nchunks;
            if (inter)
            {
                int row = active.Sum(e => TdsTypes.Size(e.ix!.Type));
                long nrows = last ? rem / row : active[0].ix!.Count;
                int col = 0;
                foreach (var (obj, ix) in active)
                {
                    obj.Chunks.Add(new TdmsChunk(bas + col, nrows, ix!.Type, be, row, 0));
                    obj.Count += nrows;
                    col += TdsTypes.Size(ix.Type);
                }
                continue;
            }
            long off = bas;
            for (int i = 0; i < active.Count; i++)
            {
                var (obj, ix) = active[i];
                long cnt;
                if (last)
                {
                    long avail = start + total - off;
                    if (avail <= 0 || ix!.Type == TdsType.String || ix.Daqmx) break;
                    cnt = Math.Min(ix.Count, avail / TdsTypes.Size(ix.Type));
                }
                else cnt = ix!.Count;
                obj.Chunks.Add(ix.Daqmx
                    ? new TdmsChunk(off, cnt, TdsType.DaqmxRaw, be, (int)ix.Width, 0)
                    : new TdmsChunk(off, cnt, ix.Type, be, 0, ix.Type == TdsType.String ? ix.Bytes : 0));
                obj.Count += cnt;
                off += sizes[i];
            }
        }
    }

    // ------------------------------------------------------------------ values
    /// <summary>Read numeric values as double (bool→0/1, timestamps→unix seconds). Range [first, first+count).</summary>
    public double[] ReadDoubles(TdmsObject ch, long first = 0, long count = -1)
    {
        if (count < 0) count = ch.Count - first;
        var res = new double[Math.Max(0, count)];
        ReadDoubles(ch, first, res.AsSpan());
        return res;
    }

    public bool IsDisposed => _disposed;

    public unsafe int ReadDoubles(TdmsObject ch, long first, Span<double> dst)
    {
        if (_disposed) { dst.Fill(double.NaN); return 0; }   // file closed while a chart still references the channel
        long skip = first; int written = 0;
        foreach (var c in ch.Chunks)
        {
            if (written >= dst.Length) break;
            if (skip >= c.Count) { skip -= c.Count; continue; }
            long take = Math.Min(c.Count - skip, dst.Length - written);
            int sz = TdsTypes.Size(c.Type);
            if (c.Type == TdsType.String || c.Type == TdsType.DaqmxRaw)
            {
                for (long i = 0; i < take; i++) dst[written++] = double.NaN;
                skip = 0; continue;
            }
            int stride = c.RowStride > 0 ? c.RowStride : sz;
            byte* p = _base + c.Offset + skip * stride;
            var d = dst.Slice(written, (int)take);
            Decode(p, stride, c.Type, c.BigEndian, d);
            written += (int)take; skip = 0;
        }
        return written;
    }

    /// <summary>
    /// Sequential (non-mmap) read for full scans such as pyramid building: uses a FileStream with SequentialScan so the
    /// file pages are not charged to the process working set. Same semantics as ReadDoubles.
    /// </summary>
    public unsafe int ReadDoublesSequential(TdmsObject ch, long first, Span<double> dst, ref FileStream? fs)
    {
        if (_disposed) { dst.Fill(double.NaN); return 0; }
        fs ??= new FileStream(FileName, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        long skip = first; int written = 0;
        byte[]? tmp = null;
        foreach (var c in ch.Chunks)
        {
            if (written >= dst.Length) break;
            if (skip >= c.Count) { skip -= c.Count; continue; }
            long take = Math.Min(c.Count - skip, dst.Length - written);
            int sz = TdsTypes.Size(c.Type);
            if (c.Type == TdsType.String || c.Type == TdsType.DaqmxRaw)
            {
                for (long i = 0; i < take; i++) dst[written++] = double.NaN;
                skip = 0; continue;
            }
            int stride = c.RowStride > 0 ? c.RowStride : sz;
            fs.Position = c.Offset + skip * stride;
            var d = dst.Slice(written, (int)take);
            if (stride == 8 && !c.BigEndian && c.Type is TdsType.Double or TdsType.DoubleUnit)
            {
                fs.ReadExactly(MemoryMarshal.AsBytes(d));
            }
            else
            {
                long nb = take * stride;
                if (tmp == null || tmp.Length < nb) tmp = new byte[Math.Max(nb, 1 << 16)];
                fs.ReadExactly(tmp.AsSpan(0, (int)nb));
                fixed (byte* p = tmp) Decode(p, stride, c.Type, c.BigEndian, d);
            }
            written += (int)take; skip = 0;
        }
        return written;
    }

    static unsafe void Decode(byte* p, int stride, TdsType t, bool be, Span<double> d)
    {
        int n = d.Length;
        if (!be && stride == 8 && t is TdsType.Double or TdsType.DoubleUnit) { new ReadOnlySpan<double>(p, n).CopyTo(d); return; }
        for (int i = 0; i < n; i++, p += stride)
        {
            var s = new ReadOnlySpan<byte>(p, 16);
            d[i] = t switch
            {
                TdsType.I8 => (sbyte)p[0],
                TdsType.U8 => p[0],
                TdsType.Boolean => p[0] != 0 ? 1 : 0,
                TdsType.I16 => be ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
                TdsType.U16 => be ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                TdsType.I32 => be ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
                TdsType.U32 => be ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                TdsType.I64 => be ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
                TdsType.U64 => be ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s),
                TdsType.Single or TdsType.SingleUnit => be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                TdsType.Double or TdsType.DoubleUnit => be ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
                TdsType.Timestamp => ReadTimestamp(s, be).ToUnixSeconds(),
                TdsType.ComplexSingle => be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                TdsType.ComplexDouble => be ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
                _ => double.NaN,
            };
        }
    }

    /// <summary>Exact typed read (bit-exact comparison use). Returns boxed array of the native type.</summary>
    public unsafe Array ReadRaw(TdmsObject ch)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TdmsFile));
        var t = ch.DataType;
        if (t == TdsType.String) return ReadStrings(ch);
        if (t == TdsType.Timestamp)
        {
            var r = new TdmsTimestamp[ch.Count]; long k = 0;
            foreach (var c in ch.Chunks) { int stride = c.RowStride > 0 ? c.RowStride : 16; byte* p = _base + c.Offset; for (long i = 0; i < c.Count; i++, p += stride) r[k++] = ReadTimestamp(new ReadOnlySpan<byte>(p, 16), c.BigEndian); }
            return r;
        }
        if (t == TdsType.DaqmxRaw) return new byte[0];
        int sz = TdsTypes.Size(t);
        var bytes = new byte[ch.Count * sz]; long o = 0;
        foreach (var c in ch.Chunks)
        {
            int stride = c.RowStride > 0 ? c.RowStride : sz;
            byte* p = _base + c.Offset;
            if (stride == sz && !c.BigEndian) { new ReadOnlySpan<byte>(p, (int)(c.Count * sz)).CopyTo(bytes.AsSpan((int)o)); o += c.Count * sz; continue; }
            for (long i = 0; i < c.Count; i++, p += stride)
            {
                var s = new ReadOnlySpan<byte>(p, sz);
                if (c.BigEndian) for (int j = 0; j < sz; j++) bytes[o + j] = s[sz - 1 - j];
                else s.CopyTo(bytes.AsSpan((int)o));
                o += sz;
            }
        }
        Array arr = t switch
        {
            TdsType.I8 => new sbyte[ch.Count], TdsType.U8 or TdsType.Boolean => new byte[ch.Count],
            TdsType.I16 => new short[ch.Count], TdsType.U16 => new ushort[ch.Count],
            TdsType.I32 => new int[ch.Count], TdsType.U32 => new uint[ch.Count],
            TdsType.I64 => new long[ch.Count], TdsType.U64 => new ulong[ch.Count],
            TdsType.Single or TdsType.SingleUnit => new float[ch.Count],
            TdsType.Double or TdsType.DoubleUnit => new double[ch.Count],
            _ => new byte[bytes.Length],
        };
        Buffer.BlockCopy(bytes, 0, arr, 0, bytes.Length);
        return arr;
    }

    public unsafe string[] ReadStrings(TdmsObject ch)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TdmsFile));
        var res = new string[ch.Count]; long k = 0;
        foreach (var c in ch.Chunks)
        {
            if (c.Type != TdsType.String) { for (long i = 0; i < c.Count; i++) res[k++] = ""; continue; }
            byte* p = _base + c.Offset;
            var ends = new ReadOnlySpan<byte>(p, (int)(4 * c.Count));
            byte* blob = p + 4 * c.Count;
            int s = 0;
            for (long i = 0; i < c.Count; i++)
            {
                int e = c.BigEndian ? BinaryPrimitives.ReadInt32BigEndian(ends.Slice((int)(4 * i))) : BinaryPrimitives.ReadInt32LittleEndian(ends.Slice((int)(4 * i)));
                res[k++] = Encoding.UTF8.GetString(blob + s, e - s);
                s = e;
            }
        }
        return res;
    }

    static TdmsTimestamp ReadTimestamp(ReadOnlySpan<byte> s, bool be)
    {
        if (be) return new TdmsTimestamp(BinaryPrimitives.ReadInt64BigEndian(s), BinaryPrimitives.ReadUInt64BigEndian(s[8..]));
        return new TdmsTimestamp(BinaryPrimitives.ReadInt64LittleEndian(s[8..]), BinaryPrimitives.ReadUInt64LittleEndian(s));
    }

    static object ReadValue(ReadOnlySpan<byte> b, ref int p, TdsType dt, bool be)
    {
        switch (dt)
        {
            case TdsType.String: return Str(b, ref p, be);
            case TdsType.Timestamp: { var v = ReadTimestamp(b.Slice(p, 16), be); p += 16; return v; }
            case TdsType.Boolean: return b[p++] != 0;
            case TdsType.I8: return (sbyte)b[p++];
            case TdsType.U8: return b[p++];
            case TdsType.I16: { var v = be ? BinaryPrimitives.ReadInt16BigEndian(b[p..]) : BinaryPrimitives.ReadInt16LittleEndian(b[p..]); p += 2; return v; }
            case TdsType.U16: { var v = be ? BinaryPrimitives.ReadUInt16BigEndian(b[p..]) : BinaryPrimitives.ReadUInt16LittleEndian(b[p..]); p += 2; return v; }
            case TdsType.I32: { var v = be ? BinaryPrimitives.ReadInt32BigEndian(b[p..]) : BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4; return v; }
            case TdsType.U32: { var v = be ? BinaryPrimitives.ReadUInt32BigEndian(b[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); p += 4; return v; }
            case TdsType.I64: { var v = be ? BinaryPrimitives.ReadInt64BigEndian(b[p..]) : BinaryPrimitives.ReadInt64LittleEndian(b[p..]); p += 8; return v; }
            case TdsType.U64: { var v = be ? BinaryPrimitives.ReadUInt64BigEndian(b[p..]) : BinaryPrimitives.ReadUInt64LittleEndian(b[p..]); p += 8; return v; }
            case TdsType.Single: case TdsType.SingleUnit: { var v = be ? BinaryPrimitives.ReadSingleBigEndian(b[p..]) : BinaryPrimitives.ReadSingleLittleEndian(b[p..]); p += 4; return v; }
            case TdsType.Double: case TdsType.DoubleUnit: { var v = be ? BinaryPrimitives.ReadDoubleBigEndian(b[p..]) : BinaryPrimitives.ReadDoubleLittleEndian(b[p..]); p += 8; return v; }
            case TdsType.ComplexSingle: { p += 8; return double.NaN; }
            case TdsType.ComplexDouble: { p += 16; return double.NaN; }
            default: throw new InvalidDataException($"unsupported property type 0x{(uint)dt:X}");
        }
    }

    static uint U32(ReadOnlySpan<byte> b, ref int p, bool be) { var v = be ? BinaryPrimitives.ReadUInt32BigEndian(b[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); p += 4; return v; }
    static ulong U64(ReadOnlySpan<byte> b, ref int p, bool be) { var v = be ? BinaryPrimitives.ReadUInt64BigEndian(b[p..]) : BinaryPrimitives.ReadUInt64LittleEndian(b[p..]); p += 8; return v; }
    static string Str(ReadOnlySpan<byte> b, ref int p, bool be) { int n = (int)U32(b, ref p, be); var s = Encoding.UTF8.GetString(b.Slice(p, n)); p += n; return s; }

    public unsafe void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _acc.SafeMemoryMappedViewHandle.ReleasePointer();
        _acc.Dispose();
        _mmf.Dispose();
    }
}
