using System.Buffers.Binary;
using System.Text;

namespace NexysView.Core.Dxd;

/// <summary>Reader for the MultiStream container (MULTI_STREAM_FILE_VER0200) used by .dxd files.
/// Port of poc/dxd_reader/multistream.py; layout in docs/18 §1.</summary>
public sealed class MultiStream
{
    public static readonly byte[] Magic = Encoding.ASCII.GetBytes("MULTI_STREAM_FILE_VER0200");
    public const int PageHeader = 32;     // 'PAG1', i32 page number in stream, i64 prev, i64 next, i32 stream index, i32 0
    public const int EntrySize = 45;      // i64 self, char[8] name, i64 first, i64 last, i32 used, i32 last index, i32 capacity, u8 flag
    public const int IndexEntryOfs = 0x7E;

    public sealed class Entry
    {
        public long SelfOfs, First, Last;
        public string Name = "";
        public int LastUsed, LastIndex, Capacity;
        public byte Flag;

        public static Entry Parse(ReadOnlySpan<byte> raw, int ofs) => new()
        {
            SelfOfs = BinaryPrimitives.ReadInt64LittleEndian(raw[ofs..]),
            Name = Encoding.Latin1.GetString(raw.Slice(ofs + 8, 8)).TrimEnd('\0'),
            First = BinaryPrimitives.ReadInt64LittleEndian(raw[(ofs + 16)..]),
            Last = BinaryPrimitives.ReadInt64LittleEndian(raw[(ofs + 24)..]),
            LastUsed = BinaryPrimitives.ReadInt32LittleEndian(raw[(ofs + 32)..]),
            LastIndex = BinaryPrimitives.ReadInt32LittleEndian(raw[(ofs + 36)..]),
            Capacity = BinaryPrimitives.ReadInt32LittleEndian(raw[(ofs + 40)..]),
            Flag = raw[ofs + 44],
        };

        public long Size => First < 0 ? 0 : (long)LastIndex * Capacity + LastUsed;
        public override string ToString() => $"<{Name} pages={(First >= 0 ? LastIndex + 1 : 0)} size={Size} cap={Capacity} flag={Flag}>";
    }

    public byte[] Raw { get; }
    public Dictionary<string, Entry> Entries { get; } = new();
    public List<string> EntryOrder { get; } = new();
    private readonly Dictionary<string, byte[]> _cache = new();

    public MultiStream(string path) : this(File.ReadAllBytes(path)) { }

    public MultiStream(byte[] raw)
    {
        Raw = raw;
        if (raw.Length < Magic.Length || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException("not a MULTI_STREAM_FILE_VER0200 file");
        var index = Entry.Parse(raw, IndexEntryOfs);
        var data = ReadEntry(index);
        int n = BinaryPrimitives.ReadInt32LittleEndian(data);
        for (int i = 0; i < n; i++)
        {
            var e = Entry.Parse(data, 4 + EntrySize * i);
            if (!Entries.ContainsKey(e.Name)) EntryOrder.Add(e.Name);
            Entries[e.Name] = e;
        }
    }

    public List<long> Pages(Entry e)
    {
        var outp = new List<long>();
        long ofs = e.First;
        while (ofs >= 0)
        {
            if (Raw[ofs] != 'P' || Raw[ofs + 1] != 'A' || Raw[ofs + 2] != 'G' || Raw[ofs + 3] != '1')
                throw new InvalidDataException($"bad page at 0x{ofs:x}");
            outp.Add(ofs);
            ofs = BinaryPrimitives.ReadInt64LittleEndian(Raw.AsSpan((int)ofs + 16));
        }
        return outp;
    }

    public byte[] ReadEntry(Entry e)
    {
        if (e.First < 0) return Array.Empty<byte>();
        var pages = Pages(e);
        if (pages.Count != e.LastIndex + 1 || pages[^1] != e.Last)
            throw new InvalidDataException($"{e.Name}: page chain mismatch");
        var buf = new byte[e.Size];
        int w = 0;
        for (int i = 0; i < pages.Count; i++)
        {
            int n = i < pages.Count - 1 ? e.Capacity : e.LastUsed;
            Buffer.BlockCopy(Raw, (int)pages[i] + PageHeader, buf, w, n);
            w += n;
        }
        return buf;
    }

    public bool Has(string name) => Entries.ContainsKey(name);

    public byte[] Stream(string name)
    {
        if (!_cache.TryGetValue(name, out var b))
            _cache[name] = b = ReadEntry(Entries[name]);
        return b;
    }
}
