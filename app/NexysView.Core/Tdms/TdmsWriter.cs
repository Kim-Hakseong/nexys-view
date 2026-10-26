using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace NexysView.Core.Tdms;

/// <summary>
/// TDMS writer (port of tools/tdms_py.TdmsWriter): one or more segments, new object list per segment, contiguous
/// little-endian raw data, version 4713. Property values: string, double, float, int, long, bool, TdmsTimestamp.
/// </summary>
public sealed class TdmsWriter : IDisposable
{
    public const uint Version = 4713;
    readonly FileStream _fs;
    public TdmsWriter(string path) { _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20); }

    public sealed record ChannelData(string Group, string Name, TdsType Type, Array Values, IReadOnlyDictionary<string, object>? Props = null);

    static byte[] Str(string s) { var b = Encoding.UTF8.GetBytes(s); var r = new byte[4 + b.Length]; BinaryPrimitives.WriteInt32LittleEndian(r, b.Length); b.CopyTo(r, 4); return r; }

    static void Prop(BinaryWriter w, string name, object v)
    {
        w.Write(Str(name));
        switch (v)
        {
            case string s: w.Write((uint)TdsType.String); w.Write(Str(s)); break;
            case double d: w.Write((uint)TdsType.Double); w.Write(d); break;
            case float f: w.Write((uint)TdsType.Single); w.Write(f); break;
            case int i: w.Write((uint)TdsType.I32); w.Write(i); break;
            case long l: w.Write((uint)TdsType.I64); w.Write(l); break;
            case uint ui: w.Write((uint)TdsType.U32); w.Write(ui); break;
            case ulong ul: w.Write((uint)TdsType.U64); w.Write(ul); break;
            case short sh: w.Write((uint)TdsType.I16); w.Write(sh); break;
            case ushort ush: w.Write((uint)TdsType.U16); w.Write(ush); break;
            case byte by: w.Write((uint)TdsType.U8); w.Write(by); break;
            case sbyte sb: w.Write((uint)TdsType.I8); w.Write(sb); break;
            case bool b: w.Write((uint)TdsType.Boolean); w.Write((byte)(b ? 1 : 0)); break;
            case TdmsTimestamp ts: w.Write((uint)TdsType.Timestamp); w.Write(ts.Fraction); w.Write(ts.Seconds); break;
            case DateTime dt: { var t = TdmsTimestamp.FromDateTime(dt); w.Write((uint)TdsType.Timestamp); w.Write(t.Fraction); w.Write(t.Seconds); break; }
            default: w.Write((uint)TdsType.String); w.Write(Str(v.ToString() ?? "")); break;
        }
    }

    static (byte[] bytes, long count) Encode(TdsType t, Array values)
    {
        if (t == TdsType.String)
        {
            var ss = (string[])values; var blobs = ss.Select(s => Encoding.UTF8.GetBytes(s)).ToArray();
            var ms = new MemoryStream(); int acc = 0;
            foreach (var b in blobs) { acc += b.Length; var e = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(e, acc); ms.Write(e); }
            foreach (var b in blobs) ms.Write(b);
            return (ms.ToArray(), ss.Length);
        }
        if (t == TdsType.Timestamp)
        {
            var ts = (TdmsTimestamp[])values; var r = new byte[16 * ts.Length];
            for (int i = 0; i < ts.Length; i++) { BinaryPrimitives.WriteUInt64LittleEndian(r.AsSpan(16 * i), ts[i].Fraction); BinaryPrimitives.WriteInt64LittleEndian(r.AsSpan(16 * i + 8), ts[i].Seconds); }
            return (r, ts.Length);
        }
        if (t == TdsType.Boolean && values is bool[] bs) return (bs.Select(b => (byte)(b ? 1 : 0)).ToArray(), bs.Length);
        int sz = TdsTypes.Size(t);
        var bytes = new byte[values.Length * sz];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return (bytes, values.Length);
    }

    /// <summary>Write one segment with a complete (new) object list.</summary>
    public void WriteSegment(IReadOnlyDictionary<string, object>? fileProps, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? groupProps, IReadOnlyList<ChannelData> channels)
    {
        var meta = new MemoryStream(); var w = new BinaryWriter(meta);
        var groups = new List<string>();
        foreach (var g in groupProps?.Keys ?? Enumerable.Empty<string>()) if (!groups.Contains(g)) groups.Add(g);
        foreach (var c in channels) if (!groups.Contains(c.Group)) groups.Add(c.Group);
        var raws = new List<byte[]>();
        w.Write(1 + groups.Count + channels.Count);
        w.Write(Str("/")); w.Write(0xFFFFFFFFu); w.Write(fileProps?.Count ?? 0);
        foreach (var kv in fileProps ?? new Dictionary<string, object>()) Prop(w, kv.Key, kv.Value);
        foreach (var g in groups)
        {
            w.Write(Str(TdmsPath.Make(g))); w.Write(0xFFFFFFFFu);
            var gp = groupProps != null && groupProps.TryGetValue(g, out var p) ? p : null;
            w.Write(gp?.Count ?? 0);
            foreach (var kv in gp ?? new Dictionary<string, object>()) Prop(w, kv.Key, kv.Value);
        }
        foreach (var c in channels)
        {
            var (bytes, count) = Encode(c.Type, c.Values);
            raws.Add(bytes);
            w.Write(Str(TdmsPath.Make(c.Group, c.Name)));
            if (c.Type == TdsType.String) { w.Write(28u); w.Write((uint)c.Type); w.Write(1u); w.Write((ulong)count); w.Write((ulong)bytes.Length); }
            else { w.Write(20u); w.Write((uint)c.Type); w.Write(1u); w.Write((ulong)count); }
            w.Write(c.Props?.Count ?? 0);
            foreach (var kv in c.Props ?? new Dictionary<string, object>()) Prop(w, kv.Key, kv.Value);
        }
        w.Flush();
        long rawLen = raws.Sum(r => (long)r.Length);
        var toc = Toc.MetaData | Toc.NewObjList | (rawLen > 0 ? Toc.RawData : 0);
        var lead = new byte[28];
        "TDSm"u8.CopyTo(lead);
        BinaryPrimitives.WriteUInt32LittleEndian(lead.AsSpan(4), (uint)toc);
        BinaryPrimitives.WriteUInt32LittleEndian(lead.AsSpan(8), Version);
        BinaryPrimitives.WriteUInt64LittleEndian(lead.AsSpan(12), (ulong)(meta.Length + rawLen));
        BinaryPrimitives.WriteUInt64LittleEndian(lead.AsSpan(20), (ulong)meta.Length);
        _fs.Write(lead); meta.Position = 0; meta.CopyTo(_fs);
        foreach (var r in raws) _fs.Write(r);
    }

    /// <summary>Convenience: write double channels in chunks (one segment per chunk) to bound memory.</summary>
    public void WriteDoubleChannelsChunked(IReadOnlyDictionary<string, object>? fileProps, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object>>? groupProps,
        IReadOnlyList<(string group, string name, long length, Func<long, double[], int> read, IReadOnlyDictionary<string, object>? props)> channels, int chunk = 1 << 20)
    {
        long maxLen = channels.Count == 0 ? 0 : channels.Max(c => c.length);
        for (long pos = 0; pos < maxLen || pos == 0; pos += chunk)
        {
            var list = new List<ChannelData>();
            foreach (var c in channels)
            {
                long n = Math.Max(0, Math.Min(chunk, c.length - pos));
                var buf = new double[n];
                if (n > 0) c.read(pos, buf);
                if (n > 0 || pos == 0) list.Add(new ChannelData(c.group, c.name, TdsType.Double, buf, pos == 0 ? c.props : null));
            }
            WriteSegment(pos == 0 ? fileProps : null, pos == 0 ? groupProps : null, list);
            if (maxLen == 0) break;
        }
    }

    public void Dispose() => _fs.Dispose();
}
