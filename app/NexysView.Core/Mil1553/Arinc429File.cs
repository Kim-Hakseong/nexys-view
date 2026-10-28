using System.Buffers.Binary;

namespace NexysView.Core.Mil1553;

/// <summary>ARINC 429 record of the same capture container (30-byte records: type, count, timestamp µs, activity, decgap, 32-bit word,
/// timestamp high, 10 bytes padding). Label = bit-reversed low byte (octal), SDI = bits 9-10, data = bits 11-29, SSM = bits 30-31, parity = bit 32.</summary>
public readonly record struct Arinc429Record(ulong TimeUs, ushort Activity, ushort DecGap, uint Word, int Index)
{
    public int Label { get { int v = (int)(Word & 0xff); int r = 0; for (int i = 0; i < 8; i++) r = (r << 1) | ((v >> i) & 1); return r; } }
    public string LabelOctal => Convert.ToString(Label, 8).PadLeft(3, '0');
    public int Sdi => (int)((Word >> 8) & 3);
    public int Data => (int)((Word >> 10) & 0x7FFFF);
    public int Ssm => (int)((Word >> 29) & 3);
    public int Parity => (int)(Word >> 31);
    public bool ParityOk { get { uint v = Word; v ^= v >> 16; v ^= v >> 8; v ^= v >> 4; v ^= v >> 2; v ^= v >> 1; return (v & 1) == 1; } }   // odd parity
    public static string SsmName(int ssm) => ssm switch { 0 => "FW/NO", 1 => "NCD/+", 2 => "TEST/-", _ => "NO/FW" };
}

public sealed class Arinc429File
{
    public const int RecordSize = 30;
    public string Path { get; }
    public int Count { get; }
    public byte[] Metadata { get; }
    public readonly ulong[] TimeUs; public readonly uint[] Word; public readonly ushort[] Activity; public readonly ushort[] DecGap;

    public Arinc429File(string path)
    {
        Path = path;
        var b = File.ReadAllBytes(path);
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(b.Length - 4));
        long rawLen = b.Length - 4 - off - 4;
        if (off < 512 || rawLen < 0 || rawLen % RecordSize != 0) throw new InvalidDataException("not an ARINC 429 capture");
        Count = (int)(rawLen / RecordSize); Metadata = b[..(int)off];
        TimeUs = new ulong[Count]; Word = new uint[Count]; Activity = new ushort[Count]; DecGap = new ushort[Count];
        for (int i = 0; i < Count; i++)
        {
            var r = b.AsSpan((int)off + 4 + i * RecordSize, RecordSize);
            uint ts = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(4)); uint th = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(16));
            TimeUs[i] = ((ulong)th << 32) | ts; Activity[i] = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(8)); DecGap[i] = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(10)); Word[i] = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(12));
        }
    }

    public Arinc429Record Get(int i) => new(TimeUs[i], Activity[i], DecGap[i], Word[i], i);

    public static bool Looks429(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (fs.Length < 512 + 8) return false;
        Span<byte> t = stackalloc byte[4]; fs.Seek(fs.Length - 4, SeekOrigin.Begin); fs.ReadExactly(t);
        uint off = BinaryPrimitives.ReadUInt32LittleEndian(t); if (off + 8 > fs.Length) return false;
        long rawLen = fs.Length - 4 - off - 4;
        fs.Seek(off + 4, SeekOrigin.Begin); Span<byte> h = stackalloc byte[2]; if (rawLen >= 2) fs.ReadExactly(h); else return false;
        ushort type = BinaryPrimitives.ReadUInt16LittleEndian(h);
        return (type & 0xff) == 1 && rawLen % RecordSize == 0;
    }

    public int[] Filter(int label = -1, int sdi = -1, int ssm = -1, bool parityErrorsOnly = false)
    {
        var l = new List<int>();
        for (int i = 0; i < Count; i++)
        {
            var r = Get(i);
            if (label >= 0 && r.Label != label) continue; if (sdi >= 0 && r.Sdi != sdi) continue; if (ssm >= 0 && r.Ssm != ssm) continue; if (parityErrorsOnly && r.ParityOk) continue;
            l.Add(i);
        }
        return l.ToArray();
    }

    /// <summary>Write a capture (same container shape as the 1553 writer).</summary>
    public static void Write(string path, IReadOnlyList<(ulong timeUs, ushort act, uint word)> recs, byte[]? metadata = null)
    {
        metadata ??= Mil1553File.MinimalMetadata();
        using var fs = new FileStream(path, FileMode.Create);
        fs.Write(metadata); Span<byte> u4 = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(u4, (uint)metadata.Length); fs.Write(u4);
        var rec = new byte[RecordSize];
        foreach (var r in recs)
        {
            Array.Clear(rec); BinaryPrimitives.WriteUInt16LittleEndian(rec, 1); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(2), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(4), (uint)r.timeUs); BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(8), r.act); BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(12), r.word); BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(16), (uint)(r.timeUs >> 32));
            fs.Write(rec);
        }
        fs.Write(u4);
    }

    public static uint MakeWord(int labelOctal, int sdi, int data, int ssm)
    {
        int lab = Convert.ToInt32(labelOctal.ToString(), 8); int rev = 0; for (int i = 0; i < 8; i++) rev = (rev << 1) | ((lab >> i) & 1);
        uint w = (uint)rev | ((uint)(sdi & 3) << 8) | ((uint)(data & 0x7FFFF) << 10) | ((uint)(ssm & 3) << 29);
        uint v = w; v ^= v >> 16; v ^= v >> 8; v ^= v >> 4; v ^= v >> 2; v ^= v >> 1; if ((v & 1) == 0) w |= 0x80000000u;   // odd parity
        return w;
    }

    public static void WriteVirtual(string path, int n, int seed = 1)
    {
        var rnd = new Random(seed); int[] labels = { 203, 204, 205, 210, 211, 212, 310, 311, 312, 313 };
        var list = new List<(ulong, ushort, uint)>(n); ulong t = 1000;
        for (int k = 0; k < n; k++) { int m = k % labels.Length; list.Add((t, (ushort)0x0780, MakeWord(labels[m], k % 4, (k * 37 + m * 1000) & 0x7FFFF, k % 50 == 49 ? 1 : 0))); t += (ulong)(15000 + rnd.Next(-100, 100)); }
        Write(path, list);
    }
}
