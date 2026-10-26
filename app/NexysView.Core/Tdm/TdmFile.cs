using System.Buffers.Binary;
using System.Globalization;
using System.Xml.Linq;
using NexysView.Core.Data;

namespace NexysView.Core.Tdm;

/// <summary>
/// the vendor TDM reader (.tdm XML header + .tdx binary blocks), model http://www.ni.com/DataModels/USI/TDM/1_0 (docs/findings.md).
/// tdm_root → tdm_channelgroup → tdm_channel → localcolumn → {double|short|…|string}_sequence → &lt;values external="incN"/&gt;
/// → usi:include/file/block(byteOffset, length, valueType). implicit_linear columns expand (offset, increment) over
/// submatrix.number_of_rows. Values are read lazily from the .tdx (one shared FileStream per file).
/// </summary>
public sealed class TdmFile : IDisposable
{
    public string Path { get; }
    public string TdxPath { get; }
    public Dictionary<string, object> RootProps { get; } = new();
    public List<(string name, Dictionary<string, object> props, List<TdmChannel> channels)> Groups { get; } = new();
    readonly FileStream? _tdx;
    readonly object _lock = new();
    readonly Dictionary<string, (long off, long len, string type)> _blocks = new();
    static readonly XNamespace Usi = "http://www.ni.com/Schemas/USI/1_0";

    public sealed class TdmChannel
    {
        public string Name = "", Description = "", Unit = "", DataType = "";
        public Dictionary<string, object> Props = new();
        public long Length;
        public bool IsText;
        public string[]? Text;
        public double[]? Implicit;          // [offset, increment] for implicit_linear
        public double[]? RawLinear;         // [offset, factor] for raw_linear: value = offset + factor * raw
        public (long off, long len, string type)? Block;
        public IValueSource Source = null!;
    }

    public TdmFile(string path)
    {
        Path = path;
        var doc = XDocument.Load(path);
        var root = doc.Root!;
        var inc = root.Element(Usi + "include");
        string tdx = "";
        if (inc != null)
        {
            foreach (var f in inc.Elements("file"))
            {
                tdx = f.Attribute("url")?.Value ?? "";
                foreach (var b in f.Elements("block"))
                    _blocks[b.Attribute("id")!.Value] = (long.Parse(b.Attribute("byteOffset")!.Value), long.Parse(b.Attribute("length")!.Value), b.Attribute("valueType")?.Value ?? "eFloat64Usi");
            }
        }
        TdxPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, tdx);
        if (!File.Exists(TdxPath)) { var alt = System.IO.Path.ChangeExtension(path, ".tdx"); if (File.Exists(alt)) TdxPath = alt; }
        if (File.Exists(TdxPath)) _tdx = new FileStream(TdxPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        var data = root.Element(Usi + "data")!;
        var byId = data.Elements().Where(e => e.Attribute("id") != null).ToDictionary(e => e.Attribute("id")!.Value, e => e);
        XElement? Ref(XElement? e, string field) { var v = e?.Element(field)?.Value; if (v == null) return null; var ids = Ids(v); return ids.Count > 0 && byId.TryGetValue(ids[0], out var r) ? r : null; }
        var tr = data.Element("tdm_root");
        if (tr != null) { foreach (var k in new[] { "name", "description", "title", "author", "datetime" }) { var v = tr.Element(k)?.Value; if (v != null) RootProps[k] = v; } Attrs(tr, RootProps); }
        var groupsOrder = tr != null ? Ids(tr.Element("channelgroups")?.Value ?? "") : data.Elements("tdm_channelgroup").Select(g => g.Attribute("id")!.Value).ToList();
        foreach (var gid in groupsOrder)
        {
            if (!byId.TryGetValue(gid, out var g)) continue;
            var gp = new Dictionary<string, object>();
            foreach (var k in new[] { "description" }) { var v = g.Element(k)?.Value; if (v != null) gp[k] = v; }
            Attrs(g, gp);
            var list = new List<TdmChannel>();
            foreach (var cid in Ids(g.Element("channels")?.Value ?? ""))
            {
                if (!byId.TryGetValue(cid, out var c)) continue;
                var ch = new TdmChannel { Name = c.Element("name")?.Value ?? "", Description = c.Element("description")?.Value ?? "", Unit = c.Element("unit_string")?.Value ?? "", DataType = c.Element("datatype")?.Value ?? "" };
                foreach (var k in new[] { "minimum", "maximum" }) { var v = c.Element(k)?.Value; if (v != null && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) ch.Props[k] = d; }
                Attrs(c, ch.Props);
                var lc = Ref(c, "local_columns");
                if (lc != null)
                {
                    var seq = Ref(lc, "values");
                    string repr = lc.Element("sequence_representation")?.Value ?? "explicit";
                    if (repr == "raw_linear")
                    {
                        var gp2 = (lc.Element("generation_parameters")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                        if (gp2.Length >= 2) ch.RawLinear = gp2;
                    }
                    var sm = Ref(lc, "submatrix");
                    long rows = sm != null && long.TryParse(sm.Element("number_of_rows")?.Value, out var nr) ? nr : -1;
                    if (seq != null)
                    {
                        var vals = seq.Element("values");
                        string ext = vals?.Attribute("external")?.Value ?? "";
                        if (seq.Name.LocalName == "string_sequence")
                        {
                            ch.IsText = true; ch.Text = vals?.Elements("s").Select(s => s.Value).ToArray() ?? Array.Empty<string>(); ch.Length = ch.Text.Length;
                        }
                        else if (ext.Length > 0 && _blocks.TryGetValue(ext, out var blk))
                        {
                            if (repr == "implicit_linear" && blk.len >= 2 && rows > 0)
                            {
                                var two = ReadBlock(blk, 0, 2); ch.Implicit = two; ch.Length = rows;
                            }
                            else { ch.Block = blk; ch.Length = blk.len; }
                        }
                        else if (vals != null)
                        {
                            // inline numeric values
                            var inline = vals.Elements().Select(v => double.TryParse(v.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToArray();
                            if (repr == "implicit_linear" && inline.Length >= 2 && rows > 0) { ch.Implicit = inline; ch.Length = rows; }
                            else { ch.Block = null; ch.Length = inline.Length; ch.Implicit = null; ch.Props["__inline"] = inline; }
                        }
                    }
                }
                ch.Source = new Src(this, ch);
                list.Add(ch);
            }
            Groups.Add((g.Element("name")?.Value ?? "", gp, list));
        }
    }

    static List<string> Ids(string xpointer) => System.Text.RegularExpressions.Regex.Matches(xpointer, "id\\(\"([^\"]+)\"\\)").Select(m => m.Groups[1].Value).ToList();

    static void Attrs(XElement e, Dictionary<string, object> into)
    {
        var ia = e.Element("instance_attributes"); if (ia == null) return;
        foreach (var a in ia.Elements())
        {
            string name = a.Attribute("name")?.Value ?? ""; if (name.Length == 0) continue;
            var v = a.Elements().FirstOrDefault()?.Value ?? a.Value;
            into[name] = a.Name.LocalName switch
            {
                "double_attribute" => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : v,
                "long_attribute" => long.TryParse(v, out var l) ? l : v,
                _ => v,
            };
        }
    }

    static int Size(string t) => t switch { "eFloat64Usi" => 8, "eFloat32Usi" => 4, "eInt32Usi" or "eUInt32Usi" => 4, "eInt16Usi" or "eUInt16Usi" => 2, "eInt8Usi" or "eUInt8Usi" => 1, "eInt64Usi" or "eUInt64Usi" => 8, "eTimeUsi" => 8, _ => 8 };

    double[] ReadBlock((long off, long len, string type) b, long first, int count)
    {
        var d = new double[count]; ReadBlock(b, first, d); return d;
    }

    int ReadBlock((long off, long len, string type) b, long first, Span<double> dst)
    {
        if (_tdx == null) { dst.Fill(double.NaN); return 0; }
        int sz = Size(b.type);
        int n = (int)Math.Max(0, Math.Min(dst.Length, b.len - first));
        if (n == 0) return 0;
        var buf = new byte[n * sz];
        lock (_lock) { _tdx.Position = b.off + first * sz; _tdx.ReadExactly(buf); }
        for (int i = 0; i < n; i++)
        {
            var s = buf.AsSpan(i * sz);
            dst[i] = b.type switch
            {
                "eFloat64Usi" or "eTimeUsi" => BinaryPrimitives.ReadDoubleLittleEndian(s),
                "eFloat32Usi" => BinaryPrimitives.ReadSingleLittleEndian(s),
                "eInt32Usi" => BinaryPrimitives.ReadInt32LittleEndian(s),
                "eUInt32Usi" => BinaryPrimitives.ReadUInt32LittleEndian(s),
                "eInt16Usi" => BinaryPrimitives.ReadInt16LittleEndian(s),
                "eUInt16Usi" => BinaryPrimitives.ReadUInt16LittleEndian(s),
                "eInt8Usi" => (sbyte)s[0], "eUInt8Usi" => s[0],
                "eInt64Usi" => BinaryPrimitives.ReadInt64LittleEndian(s),
                "eUInt64Usi" => BinaryPrimitives.ReadUInt64LittleEndian(s),
                _ => BinaryPrimitives.ReadDoubleLittleEndian(s),
            };
        }
        return n;
    }

    sealed class Src : IValueSource
    {
        readonly TdmFile _f; readonly TdmChannel _c;
        public Src(TdmFile f, TdmChannel c) { _f = f; _c = c; }
        public long Length => _c.Length;
        public int Read(long first, Span<double> dst)
        {
            int n = (int)Math.Max(0, Math.Min(dst.Length, _c.Length - first));
            if (n == 0) return 0;
            if (_c.IsText) { dst.Slice(0, n).Fill(double.NaN); return n; }
            if (_c.Implicit != null) { for (int i = 0; i < n; i++) dst[i] = _c.Implicit[0] + (first + i) * _c.Implicit[1]; return n; }
            if (_c.Block != null)
            {
                int got = _f.ReadBlock(_c.Block.Value, first, dst.Slice(0, n));
                if (_c.RawLinear != null) { double off = _c.RawLinear[0], fac = _c.RawLinear[1]; for (int i = 0; i < got; i++) dst[i] = off + fac * dst[i]; }
                return got;
            }
            if (_c.Props.TryGetValue("__inline", out var o) && o is double[] inl) { inl.AsSpan((int)first, n).CopyTo(dst); return n; }
            dst.Slice(0, n).Fill(double.NaN); return n;
        }
    }

    public void Dispose() => _tdx?.Dispose();

    /// <summary>Open as a DataFile (lazy values, implicit x from wf_* attributes when present).</summary>
    public static DataFile OpenDataFile(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var f = new TdmFile(path);
        var df = new DataFile(path, "TDM", f.RootProps, f);
        foreach (var (gname, gp, chans) in f.Groups)
        {
            var g = new ChannelGroup(gname, gp); df.Groups.Add(g);
            foreach (var c in chans)
            {
                var props = new Dictionary<string, object>(c.Props); props.Remove("__inline");
                if (c.Text != null) props["text_values"] = c.Text;
                var ch = new Channel(df.Name, gname, c.Name, c.Source, props) { Unit = c.Unit, Description = c.Description, DataType = c.IsText ? "String" : c.DataType, IsText = c.IsText };
                if (props.TryGetValue("wf_increment", out var inc) && inc is double dinc && dinc > 0)
                {
                    ch.XKind = XKind.Implicit; ch.XIncrement = dinc; ch.XStart = props.TryGetValue("wf_start_offset", out var so) && so is double dso ? dso : 0;
                    ch.XUnit = props.TryGetValue("wf_xunit_string", out var xu) && xu is string sxu ? sxu : "s";
                }
                g.Channels.Add(ch);
            }
        }
        df.OpenTime = sw.Elapsed;
        return df;
    }
}
