using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using NexysView.App.Import;
using NexysView.Core.Data;

namespace NexysView.App.Plugins;

/// <summary>
/// VBS DataPlugin host — the subset of the DataPlugin object model (the DataPlugin typelib, DataPlugin.chm)
/// used by the tutorial examples: Root(Properties, ChannelGroups), ChannelGroup(Channels.AddImplicitChannel /
/// AddDirectAccessChannel), File(Formatter, GetNextStringValue, SkipLine, SkipValue, Position, Size, GetStringBlock,
/// GetBinaryBlock), StringBlock/BinaryBlock(Channels.Add, Position, ByteOrder). Values are materialized after ReadStore.
/// </summary>
public static class VbsDataPlugin
{
    public const int eU8 = 5, eI16 = 2, eI32 = 3, eI64 = 4, eR32 = 9, eR64 = 10, eString = 32, eTime = 68, eLittleEndian = 0, eBigEndian = 1;
    const string Prelude = "Const eU8 = 5: Const eI16 = 2: Const eI32 = 3: Const eI64 = 4: Const eR32 = 9: Const eR64 = 10: Const eString = 32: Const eTime = 68: Const eLittleEndian = 0: Const eBigEndian = 1\n" +
                           "Sub RaiseError(msg): Err.Raise 1001, \"DataPlugin\", msg: End Sub\n";

    public static DataFile Open(string dataPath, string scriptPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var root = new RootObj();
        var file = new FileObj(dataPath);
        using var host = new VbsScriptHost();
        host.AddObject("Root", root);
        host.AddObject("File", file);
        // "Option Explicit" must stay first; host constants/helpers are appended (VBScript hoists Const and Sub).
        var code = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(scriptPath), @"(?im)^\s*Option\s+Explicit\s*$", "");
        host.Run("Option Explicit\n" + code + "\n" + Prelude);
        host.Invoke("ReadStore", file);
        var df = new DataFile(dataPath, "Plugin:VBS", root.PropsDict(), null);
        foreach (var g in root.Groups.Items)
        {
            var grp = new ChannelGroup(g.Name, g.PropsDict()); df.Groups.Add(grp);
            foreach (var c in g.Channels.Items)
            {
                var (vals, text) = c.Materialize(file);
                var props = c.PropsDict();
                if (text != null) props["text_values"] = text;
                var ch = new Channel(df.Name, g.Name, c.Name, new ArraySource(vals), props) { Unit = c.Unit, DataType = text != null ? "String" : c.TypeName, IsText = text != null };
                if (c.Implicit != null) { }
                grp.Channels.Add(ch);
            }
        }
        df.OpenTime = sw.Elapsed;
        return df;
    }

    // ------------------------------------------------------------------ object model (COM-visible, late-bound from VBScript)
    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class PropertiesObj
    {
        internal readonly List<(string name, object value)> List = new();
        public void Add(string name, object value) => List.Add((name, value ?? ""));
        public int Count => List.Count;
        public bool Exists(string name) => List.Any(p => string.Equals(p.name, name, StringComparison.OrdinalIgnoreCase));
        public object Item(object key)
        {
            if (key is int i) return List[i - 1].value;
            var p = List.FirstOrDefault(p => string.Equals(p.name, key.ToString(), StringComparison.OrdinalIgnoreCase));
            return p.value ?? "";
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class RootObj
    {
        public string Name { get; set; } = ""; public string Title { get; set; } = ""; public string Author { get; set; } = ""; public string Description { get; set; } = "";
        public PropertiesObj Properties { get; } = new();
        public ChannelGroupsObj ChannelGroups { get; } = new();
        internal ChannelGroupsObj Groups => ChannelGroups;
        internal Dictionary<string, object> PropsDict() { var d = new Dictionary<string, object>(); foreach (var (n, v) in Properties.List) d[n] = v; if (Name.Length > 0) d["name"] = Name; if (Title.Length > 0) d["title"] = Title; if (Author.Length > 0) d["author"] = Author; return d; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ChannelGroupsObj
    {
        internal readonly List<ChannelGroupObj> Items = new();
        public ChannelGroupObj Add(string name) { var g = new ChannelGroupObj { Name = name }; Items.Add(g); return g; }
        public int Count => Items.Count;
        public ChannelGroupObj Item(int i) => Items[i - 1];
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ChannelGroupObj
    {
        public string Name { get; set; } = ""; public string Description { get; set; } = "";
        public PropertiesObj Properties { get; } = new();
        public ChannelsObj Channels { get; } = new();
        internal Dictionary<string, object> PropsDict() { var d = new Dictionary<string, object>(); foreach (var (n, v) in Properties.List) d[n] = v; if (Description.Length > 0) d["description"] = Description; return d; }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ChannelsObj : System.Collections.IEnumerable
    {
        internal readonly List<ChannelObj> Items = new();
        public System.Collections.IEnumerator GetEnumerator() => Items.ToList().GetEnumerator();
        public ChannelObj AddImplicitChannel(string name, double start, double increment, double size, int type)
        { var c = new ChannelObj { Name = name, Type = type, Implicit = new[] { start, increment }, Size = (long)size }; Items.Add(c); return c; }
        public ChannelObj AddDirectAccessChannel(object blockChannel)
        {
            var c = ChannelObj.Resolve(blockChannel) ?? throw new ArgumentException("not a block channel");
            Items.Add(c); return c;
        }
        public ChannelObj Add(string name, int type) { var c = new ChannelObj { Name = name, Type = type }; Items.Add(c); return c; }
        public int Count => Items.Count;
        public ChannelObj Item(int i) => Items[i - 1];
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ChannelObj
    {
        // Objects that round-trip through the script engine may come back as a plain COM proxy instead of the managed
        // instance (observed with VBScript on .NET 8); every channel carries a key so it can be resolved by identity.
        static readonly Dictionary<string, ChannelObj> Registry = new();
        public static string? LastResolveType;
        public string Key { get; } = Guid.NewGuid().ToString("N");
        public ChannelObj() { lock (Registry) Registry[Key] = this; }
        internal static ChannelObj? Resolve(object o)
        {
            LastResolveType = o?.GetType().FullName;
            if (o is ChannelObj c) return c;
            if (o == null) return null;
            try
            {
                var key = o.GetType().InvokeMember("Key", System.Reflection.BindingFlags.GetProperty, null, o, null) as string;
                if (key != null) lock (Registry) return Registry.TryGetValue(key, out var r) ? r : null;
            }
            catch { }
            return null;
        }
        public string Name { get; set; } = ""; public string Unit { get; set; } = ""; public string Description { get; set; } = "";
        public int Type { get; set; } = eR64;
        public PropertiesObj Properties { get; } = new();
        internal double[]? Implicit; internal long Size;
        internal BlockBase? Block; internal int Column;
        internal List<double> Vals = new(); internal List<string>? Text;
        /// <summary>VBS: Channel.Values(i) = v (1-based) / x = Channel.Values(i) — in-memory channel values (indexed property).</summary>
        [System.Runtime.CompilerServices.IndexerName("Values")]
        public object this[int i]
        {
            get => i >= 1 && i <= Vals.Count ? Vals[i - 1] : double.NaN;
            set { while (Vals.Count < i) Vals.Add(double.NaN); Vals[i - 1] = value is double d ? d : value == null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        }
        internal string TypeName => Type switch { eI32 => "I32", eI16 => "I16", eI64 => "I64", eU8 => "U8", eR32 => "SGL", eR64 => "DBL", eTime => "Time", eString => "String", _ => "DBL" };
        internal Dictionary<string, object> PropsDict() { var d = new Dictionary<string, object>(); foreach (var (n, v) in Properties.List) d[n] = v; if (Description.Length > 0) d["description"] = Description; return d; }
        internal (double[] vals, string[]? text) Materialize(FileObj f)
        {
            if (Implicit != null) { var v = new double[Size]; for (long i = 0; i < Size; i++) v[i] = Implicit[0] + i * Implicit[1]; return (v, null); }
            Block?.Materialize(f);
            if (Type == eString) return (Text?.Select(_ => double.NaN).ToArray() ?? Array.Empty<double>(), Text?.ToArray() ?? Array.Empty<string>());
            return (Vals.ToArray(), null);
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ValuesObj
    {
        readonly ChannelObj _c; internal ValuesObj(ChannelObj c) => _c = c;
        [System.Runtime.InteropServices.DispId(0)]
        public object this[int i]
        {
            get => i >= 1 && i <= _c.Vals.Count ? _c.Vals[i - 1] : double.NaN;
            set { while (_c.Vals.Count < i) _c.Vals.Add(double.NaN); _c.Vals[i - 1] = value is double d ? d : value == null ? double.NaN : Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        }
        public int Count => _c.Vals.Count;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class FileInfoObj
    {
        public string FileName { get; init; } = ""; public string Path { get; init; } = ""; public string Extension { get; init; } = "";
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class FormatterObj
    {
        public string DecimalPoint { get => DecimalSeparator; set => DecimalSeparator = value; }
        public string LineFeeds { get; set; } = "\r\n";
        public bool IgnoreEmptyLines { get; set; }
        public string TrimCharacters { get; set; } = "";
        public string Delimiters { get; set; } = " ";
        public string ThousandSeparator { get; set; } = "";
        public string DecimalSeparator { get; set; } = ".";
        public string TimeFormat { get; set; } = "";
        public string CommentCharacter { get; set; } = "";
    }

    public abstract class BlockBase
    {
        internal readonly List<ChannelObj> Chans = new();
        internal bool Done;
        internal abstract void Materialize(FileObj f);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class BlockChannelsObj
    {
        readonly BlockBase _b; internal BlockChannelsObj(BlockBase b) => _b = b;
        public ChannelObj Add(string name, int type) { var c = new ChannelObj { Name = name, Type = type, Block = _b, Column = _b.Chans.Count }; _b.Chans.Add(c); return c; }
        public int Count => _b.Chans.Count;
        public ChannelObj Item(int i) => _b.Chans[i - 1];
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class StringBlockObj : BlockBase
    {
        readonly FileObj _f;
        public StringBlockObj(FileObj f) { _f = f; Channels = new BlockChannelsObj(this); Position = f.Position; }
        public BlockChannelsObj Channels { get; }
        public double Position { get; set; }
        public double Size => _f.Size;
        internal override void Materialize(FileObj f)
        {
            if (Done) return; Done = true;
            long pos = (long)Position;
            var delims = f.Formatter.Delimiters.ToCharArray();
            foreach (var c in Chans) { c.Vals.Clear(); if (c.Type == eString) c.Text = new(); }
            while (pos < f.Size)
            {
                string line = f.ReadLineAt(ref pos);
                if (f.Formatter.IgnoreEmptyLines && line.Trim().Length == 0) continue;
                var cells = line.Split(delims);
                for (int k = 0; k < Chans.Count; k++)
                {
                    var c = Chans[k];
                    string cell = k < cells.Length ? f.Trim(cells[k]) : "";
                    if (c.Type == eString) { c.Text!.Add(cell); c.Vals.Add(double.NaN); }
                    else c.Vals.Add(f.ParseValue(cell, c.Type));
                }
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class BinaryBlockObj : BlockBase
    {
        readonly FileObj _f;
        public BinaryBlockObj(FileObj f) { _f = f; Channels = new BlockChannelsObj(this); Position = f.Position; }
        public BlockChannelsObj Channels { get; }
        public double Position { get; set; }
        public int ByteOrder { get; set; } = eLittleEndian;
        public int BlockWidth { get; set; }   // 0 = interleaved record of all channels
        public double Size => _f.Size;
        static int SizeOf(int t) => t switch { eU8 => 1, eI16 => 2, eI32 => 4, eI64 => 8, eR32 => 4, eR64 => 8, eTime => 8, _ => 8 };
        internal override void Materialize(FileObj f)
        {
            if (Done) return; Done = true;
            var data = f.Bytes;
            int rec = Chans.Sum(c => SizeOf(c.Type));
            if (rec == 0) return;
            long pos = (long)Position;
            bool be = ByteOrder == eBigEndian;
            foreach (var c in Chans) c.Vals.Clear();
            while (pos + rec <= data.Length)
            {
                int off = 0;
                foreach (var c in Chans)
                {
                    var s = new ReadOnlySpan<byte>(data, (int)(pos + off), SizeOf(c.Type));
                    c.Vals.Add(c.Type switch
                    {
                        eU8 => s[0],
                        eI16 => be ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
                        eI32 => be ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
                        eI64 => be ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s),
                        eR32 => be ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                        _ => be ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
                    });
                    off += SizeOf(c.Type);
                }
                pos += rec;
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class FileObj
    {
        internal readonly byte[] Bytes; readonly Encoding _enc;
        public FileObj(string path)
        {
            Bytes = File.ReadAllBytes(path); _enc = Bytes.Length >= 2 && Bytes[0] == 0xFF && Bytes[1] == 0xFE ? Encoding.Unicode : Encoding.UTF8; if (_enc == Encoding.Unicode) Position = 2;
            Info = new FileInfoObj { FileName = System.IO.Path.GetFileNameWithoutExtension(path), Path = System.IO.Path.GetDirectoryName(path) ?? "", Extension = System.IO.Path.GetExtension(path).TrimStart('.') };
        }
        public FormatterObj Formatter { get; } = new();
        public FileInfoObj Info { get; }
        public double Position { get; set; }
        public double Size => Bytes.Length;
        public string Name => "";
        public StringBlockObj GetStringBlock() => new(this);
        public BinaryBlockObj GetBinaryBlock() => new(this);

        internal string Trim(string s) => Formatter.TrimCharacters.Length > 0 ? s.Trim(Formatter.TrimCharacters.ToCharArray()) : s;
        int CharBytes => _enc == Encoding.Unicode ? 2 : 1;

        internal string ReadLineAt(ref long pos)
        {
            var lf = _enc.GetBytes(Formatter.LineFeeds.Length > 0 ? Formatter.LineFeeds : "\n");
            long start = pos, end = -1;
            for (long i = pos; i + lf.Length <= Bytes.Length; i += CharBytes)
            {
                bool m = true; for (int k = 0; k < lf.Length; k++) if (Bytes[i + k] != lf[k]) { m = false; break; }
                if (m) { end = i; break; }
            }
            if (end < 0) { pos = Bytes.Length; return _enc.GetString(Bytes, (int)start, (int)(Bytes.Length - start)); }
            pos = end + lf.Length;
            return _enc.GetString(Bytes, (int)start, (int)(end - start));
        }

        // current line buffer for GetNextStringValue / SkipValue
        string? _line; int _lineIdx; long _lineStart;
        void EnsureLine()
        {
            if (_line != null) return;
            long p = (long)Position;
            while (p < Bytes.Length)
            {
                _lineStart = p;
                var l = ReadLineAt(ref p);
                if (Formatter.IgnoreEmptyLines && l.Trim().Length == 0) { Position = p; continue; }
                _line = l; _lineIdx = 0; Position = p; return;
            }
            _line = ""; _lineIdx = 0; Position = Bytes.Length;
        }

        string? NextToken()
        {
            EnsureLine();
            var delims = Formatter.Delimiters;
            if (_line == null || _lineIdx > _line.Length) return null;
            if (_lineIdx == _line.Length && _line.Length > 0 && _lineIdx > 0) return null;
            int i = _lineIdx;
            // tokens are separated by any delimiter char; the last token runs to end of line
            int j = i; while (j < _line.Length && delims.IndexOf(_line[j]) < 0) j++;
            string tok = _line.Substring(i, j - i);
            _lineIdx = j < _line.Length ? j + 1 : _line.Length + 1;
            return Trim(tok);
        }

        public object GetNextStringValue(int type)
        {
            var tok = NextToken();
            if (tok == null) return DBNull.Value;   // VBS IsEmpty(...) → Empty is closest; DBNull reads as Null; use Empty via null below
            if (type == eString) return tok;
            double v = ParseValue(tok, type);
            return type is eI32 or eI16 or eI64 or eU8 ? (object)(int)v : v;
        }

        public void SkipValue() => NextToken();
        public void SkipLine() { if (_line == null) EnsureLine(); _line = null; }
        public string GetNextLine() { EnsureLine(); var l = _line ?? ""; _line = null; return l; }

        internal double ParseValue(string s, int type)
        {
            if (s.Length == 0) return double.NaN;
            if (type == eTime)
            {
                string fmt = Formatter.TimeFormat.Replace("DD", "dd").Replace("YYYY", "yyyy").Replace("hh", "HH").Replace("fff", "fff");
                if (DateTime.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                    return (dt - DateTime.UnixEpoch).TotalSeconds;
                return double.NaN;
            }
            if (Formatter.ThousandSeparator.Length > 0) s = s.Replace(Formatter.ThousandSeparator, "");
            if (Formatter.DecimalSeparator != "." && Formatter.DecimalSeparator.Length > 0) s = s.Replace(Formatter.DecimalSeparator, ".");
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }
    }
}
