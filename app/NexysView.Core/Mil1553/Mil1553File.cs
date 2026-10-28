using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace NexysView.Core.Mil1553;

/// <summary>One MIL-STD-1553 bus-monitor record (decoded view over the 120-byte on-disk record).</summary>
public readonly record struct Mil1553Record(ulong TimeUs, ushort Activity, ushort Error, ushort Cwd1, ushort Cwd2, ushort Swd1, ushort Swd2,
    int DataCount, ushort RespTime1, ushort RespTime2, int Index)
{
    public int Rt => (Cwd1 >> 11) & 0x1f;
    public int Tr => (Cwd1 >> 10) & 1;
    public int Sa => (Cwd1 >> 5) & 0x1f;
    public int Wc => Cwd1 & 0x1f;
    public bool BusB => (Activity & 0x0001) != 0;
    /// <summary>Any error bit other than HIT (0x0001, "always set after the message is sent — not an error").</summary>
    public bool HasError => (Error & ~0x0001) != 0;
    public bool IsModeCode => Sa == 0 || Sa == 31;
}

/// <summary>
/// Reader/writer for the bus-monitor capture container used by the 1553 analysis tool family: an OLE compound file (metadata)
/// followed by fixed 120-byte records, with the record-area offset stored as a trailing little-endian uint32 (and repeated as the
/// first uint32 of the record area). Only the record area is interpreted here; the compound part is kept as opaque bytes.
/// Record layout (uint16 words): [0]=type (0x0103 = 1553 v1), [1]=word count, [2..3]=time tag µs lo/hi, [4]=activity, [5]=error,
/// [6]=cwd1, [7]=cwd2, [8]=swd1, [9]=swd2, [10]=data count (≤40), [11..]=data words, then time tag high lo/hi, resp time 1, resp time 2.
/// </summary>
public sealed class Mil1553File : IDisposable
{
    public const int RecordSize = 120;
    public const ushort Type1553 = 0x0103;
    public string Path { get; }
    public int Count { get; }
    public long RecordOffset { get; }
    public byte[] Metadata { get; }
    readonly MemoryMappedFile? _map; readonly MemoryMappedViewAccessor? _view; readonly byte[]? _mem;
    // compact per-record columns for fast filtering / plotting
    public readonly ulong[] TimeUs; public readonly ushort[] Cwd1; public readonly ushort[] Activity; public readonly ushort[] Error;
    public readonly byte[] DataCount; public readonly ushort[] Swd1;

    public Mil1553File(string path, bool mapped = true)
    {
        Path = path;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = fs.Length;
        if (size < 8) throw new InvalidDataException("file too small");
        Span<byte> tail = stackalloc byte[4];
        fs.Seek(size - 4, SeekOrigin.Begin); fs.ReadExactly(tail);
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(tail);
        if (off < 512 || off + 4 > size - 4) throw new InvalidDataException("bad record offset");
        long rawLen = size - 4 - off - 4;
        if (rawLen % RecordSize != 0) throw new InvalidDataException("record area is not a multiple of 120 bytes");
        Count = (int)(rawLen / RecordSize);
        RecordOffset = off + 4;
        Metadata = new byte[off]; fs.Seek(0, SeekOrigin.Begin); fs.ReadExactly(Metadata);
        fs.Close();
        if (mapped && rawLen > 0)
        {
            _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = _map.CreateViewAccessor(RecordOffset, rawLen, MemoryMappedFileAccess.Read);
        }
        else
        {
            _mem = new byte[rawLen];
            using var f2 = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            f2.Seek(RecordOffset, SeekOrigin.Begin); f2.ReadExactly(_mem);
        }
        TimeUs = new ulong[Count]; Cwd1 = new ushort[Count]; Activity = new ushort[Count]; Error = new ushort[Count]; DataCount = new byte[Count]; Swd1 = new ushort[Count];
        Span<byte> rec = stackalloc byte[RecordSize];
        for (int i = 0; i < Count; i++)
        {
            ReadRaw(i, rec);
            int n = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(20));
            if (n > 40) n = 40;
            uint tlo = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(4)); uint thi = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(6));
            int o = (11 + n) * 2;
            ulong th = o + 4 <= RecordSize ? (uint)(BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(o)) | (BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(o + 2)) << 16)) : 0u;
            TimeUs[i] = (th << 32) | (thi << 16) | tlo;
            Activity[i] = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(8)); Error[i] = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(10));
            Cwd1[i] = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(12)); Swd1[i] = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(16)); DataCount[i] = (byte)n;
        }
    }

    void ReadRaw(int i, Span<byte> dst)
    {
        if (_mem != null) _mem.AsSpan(i * RecordSize, RecordSize).CopyTo(dst);
        else
        {
            var tmp = new byte[RecordSize]; _view!.ReadArray((long)i * RecordSize, tmp, 0, RecordSize); tmp.CopyTo(dst);
        }
    }

    public Mil1553Record Get(int i)
    {
        Span<byte> rec = stackalloc byte[RecordSize]; ReadRaw(i, rec);
        int n = DataCount[i]; int o = (11 + n) * 2;
        return new Mil1553Record(TimeUs[i], Activity[i], Error[i], Cwd1[i], BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(14)), Swd1[i], BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(18)),
            n, o + 8 <= RecordSize ? BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(o + 4)) : (ushort)0, o + 8 <= RecordSize ? BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(o + 6)) : (ushort)0, i);
    }

    /// <summary>Data words of record i (DataCount values).</summary>
    public int Data(int i, Span<ushort> dst)
    {
        Span<byte> rec = stackalloc byte[RecordSize]; ReadRaw(i, rec);
        int n = Math.Min(DataCount[i], dst.Length);
        for (int k = 0; k < n; k++) dst[k] = BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(22 + 2 * k));
        return n;
    }

    /// <summary>Data word w (0-based) of record i, or -1 when the record has fewer words.</summary>
    public int Word(int i, int w)
    {
        if (w >= DataCount[i]) return -1;
        Span<byte> rec = stackalloc byte[RecordSize]; ReadRaw(i, rec);
        return BinaryPrimitives.ReadUInt16LittleEndian(rec.Slice(22 + 2 * w));
    }

    /// <summary>Record indices matching the filter (rt/sa = -1 for any). Linear scan over the compact columns.</summary>
    public int[] Filter(int rt = -1, int sa = -1, int tr = -1, bool errorsOnly = false, int bus = -1)
    {
        var list = new List<int>(errorsOnly ? 1024 : Count);
        for (int i = 0; i < Count; i++)
        {
            ushort c = Cwd1[i];
            if (rt >= 0 && ((c >> 11) & 0x1f) != rt) continue;
            if (sa >= 0 && ((c >> 5) & 0x1f) != sa) continue;
            if (tr >= 0 && ((c >> 10) & 1) != tr) continue;
            if (errorsOnly && (Error[i] & ~0x0001) == 0) continue;
            if (bus >= 0 && (Activity[i] & 1) != bus) continue;
            list.Add(i);
        }
        return list.ToArray();
    }

    public void Dispose() { _view?.Dispose(); _map?.Dispose(); }

    // ------------------------------------------------------------------ writer (virtual samples, round trips)
    /// <summary>Write a capture: metadata (opaque compound bytes; a minimal stub when null) + records + trailing offset.</summary>
    public static void Write(string path, IReadOnlyList<(ulong timeUs, ushort act, ushort err, ushort cwd1, ushort cwd2, ushort swd1, ushort swd2, ushort[] data, ushort rt1, ushort rt2)> recs, byte[]? metadata = null)
    {
        metadata ??= MinimalMetadata();
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        fs.Write(metadata);
        Span<byte> u4 = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(u4, (uint)metadata.Length); fs.Write(u4);
        var rec = new byte[RecordSize];
        foreach (var r in recs)
        {
            Array.Clear(rec);
            int n = Math.Min(40, r.data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(0), Type1553);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), (ushort)(9 + n + 4));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(4), (ushort)(r.timeUs & 0xffff)); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(6), (ushort)((r.timeUs >> 16) & 0xffff));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8), r.act); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(10), r.err);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(12), r.cwd1); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(14), r.cwd2);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(16), r.swd1); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(18), r.swd2);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(20), (ushort)n);
            for (int k = 0; k < n; k++) BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(22 + 2 * k), r.data[k]);
            int o = (11 + n) * 2; uint th = (uint)(r.timeUs >> 32);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(o), (ushort)(th & 0xffff)); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(o + 2), (ushort)(th >> 16));
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(o + 4), r.rt1); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(o + 6), r.rt2);
            fs.Write(rec);
        }
        fs.Write(u4);
    }

    /// <summary>A minimal valid OLE compound file (header + FAT + directory with an empty root) so the container shape is preserved.</summary>
    public static byte[] MinimalMetadata()
    {
        var b = new byte[512 * 3];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x18), 0x3E); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x1C), 0xFFFE); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x1E), 9); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x2C), 1);            // 1 FAT sector
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x30), 1);            // first directory sector = 1
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x3C), 0xFFFFFFFE); BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x44), 0xFFFFFFFE);
        for (int i = 0; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0x4C + 4 * i), i == 0 ? 0u : 0xFFFFFFFF);
        // FAT at sector 0: [0]=FATSECT, [1]=ENDOFCHAIN (dir), rest free
        var fat = b.AsSpan(512, 512); fat.Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(fat, 0xFFFFFFFD); BinaryPrimitives.WriteUInt32LittleEndian(fat.Slice(4), 0xFFFFFFFE);
        // directory at sector 1: root entry
        var dir = b.AsSpan(1024, 512);
        var name = System.Text.Encoding.Unicode.GetBytes("Root Entry\0"); name.CopyTo(dir);
        BinaryPrimitives.WriteUInt16LittleEndian(dir.Slice(0x40), (ushort)name.Length); dir[0x42] = 5; dir[0x43] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(0x44), 0xFFFFFFFF); BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(0x48), 0xFFFFFFFF); BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(0x4C), 0xFFFFFFFF);
        BinaryPrimitives.WriteUInt32LittleEndian(dir.Slice(0x74), 0xFFFFFFFE);
        for (int e = 1; e < 4; e++) { var d = dir.Slice(128 * e, 128); BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(0x44), 0xFFFFFFFF); BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(0x48), 0xFFFFFFFF); BinaryPrimitives.WriteUInt32LittleEndian(d.Slice(0x4C), 0xFFFFFFFF); }
        return b;
    }

    /// <summary>Deterministic synthetic capture: 10 messages cycling (RT 1..10, SA 1..10, WC 4..32), bus B every 7th, ~1% errors.</summary>
    public static void WriteVirtual(string path, int n, int seed = 1, int periodUs = 2000)
    {
        var rnd = new Random(seed);
        // the same 10 messages as IcdHub.Dummy() so the dummy ICD decodes every record
        var msgs = new (int rt, int sa, bool tr, int wc)[] { (1, 1, false, 8), (1, 2, false, 6), (1, 3, true, 8), (2, 4, true, 4), (2, 5, false, 4), (3, 6, true, 6), (4, 7, true, 4), (5, 8, true, 8), (6, 9, true, 2), (7, 10, false, 4) };
        var list = new List<(ulong, ushort, ushort, ushort, ushort, ushort, ushort, ushort[], ushort, ushort)>(n);
        ulong t = 0x1fUL << 32;
        for (int k = 0; k < n; k++)
        {
            int m = k % 10; var (rt, sa, tr, wc) = msgs[m];
            ushort cwd1 = (ushort)((rt << 11) | ((tr ? 1 : 0) << 10) | (sa << 5) | (wc & 0x1f));
            ushort swd = (ushort)(rt << 11);
            var data = new ushort[wc]; for (int j = 0; j < wc; j++) data[j] = (ushort)((k * 7 + j * 13) & 0xffff);
            ushort act = (ushort)(0x0800 | 0x0020 | (tr ? 0x0004 : 0x0008)); if (k % 7 == 6) act |= 1;
            ushort err = 1;
            if (rnd.NextDouble() < 0.01) { err |= 0x0120; swd |= 0x0400; }
            list.Add((t, act, err, cwd1, 0, swd, 0, data, (ushort)rnd.Next(40, 120), 0));
            t += (ulong)(periodUs + rnd.Next(-50, 51));
        }
        Write(path, list);
    }
}
