using System.Buffers.Binary;
using System.Text;

namespace NexysView.Core.Mil1553;

/// <summary>Minimal OLE2 compound file (structured storage) reader/writer: storages with CLSID + streams, v3 (512-byte sectors).</summary>
public sealed class CompoundFile
{
    public byte[] RootClsid = new byte[16];
    public Dictionary<string, byte[]> Storages = new(StringComparer.Ordinal);   // path "A/B" → clsid (16 bytes)
    public Dictionary<string, byte[]> Streams = new(StringComparer.Ordinal);    // path "A/B/stream" → bytes
    const uint ENDOFCHAIN = 0xFFFFFFFE, FREESECT = 0xFFFFFFFF, FATSECT = 0xFFFFFFFD, NOSTREAM = 0xFFFFFFFF;
    static readonly byte[] Sig = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public static CompoundFile Read(byte[] b)
    {
        if (!b.AsSpan(0, 8).SequenceEqual(Sig)) throw new InvalidDataException("not a compound file");
        int ss = 1 << BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x1E)); int ms = 1 << BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(0x20));
        uint nFat = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x2C)), firstDir = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x30)), cutoff = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x38));
        uint firstMini = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x3C)), nMini = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x40)), firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x44)), nDifat = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x48));
        var difat = new List<uint>(); for (int i = 0; i < 109; i++) difat.Add(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(0x4C + 4 * i)));
        uint s = firstDifat;
        for (uint k = 0; k < nDifat && s != ENDOFCHAIN && s != FREESECT; k++) { var d = Sector(b, s, ss); int n = ss / 4 - 1; for (int i = 0; i < n; i++) difat.Add(BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4 * i))); s = BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4 * n)); }
        var fat = new List<uint>();
        foreach (var fs in difat.Take((int)nFat)) { if (fs == FREESECT || fs == ENDOFCHAIN) continue; var d = Sector(b, fs, ss); for (int i = 0; i < ss / 4; i++) fat.Add(BinaryPrimitives.ReadUInt32LittleEndian(d.Slice(4 * i))); }
        byte[] Chain(uint start, long size = -1)
        {
            var ms2 = new MemoryStream(); uint c = start; var seen = new HashSet<uint>();
            while (c != ENDOFCHAIN && c != FREESECT && c < fat.Count && seen.Add(c)) { ms2.Write(Sector(b, c, ss)); c = fat[(int)c]; }
            var r = ms2.ToArray(); return size >= 0 && size < r.Length ? r[..(int)size] : r;
        }
        var dir = Chain(firstDir);
        int ne = dir.Length / 128;
        var names = new string[ne]; var types = new byte[ne]; var left = new uint[ne]; var right = new uint[ne]; var child = new uint[ne]; var clsid = new byte[ne][]; var start = new uint[ne]; var size = new long[ne];
        for (int i = 0; i < ne; i++)
        {
            var e = dir.AsSpan(i * 128, 128); int nl = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(0x40));
            names[i] = nl >= 2 ? Encoding.Unicode.GetString(e.Slice(0, nl - 2)) : ""; types[i] = e[0x42];
            left[i] = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x44)); right[i] = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x48)); child[i] = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x4C));
            clsid[i] = e.Slice(0x50, 16).ToArray(); start[i] = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x74)); size[i] = BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(0x78));
        }
        var cf = new CompoundFile { RootClsid = clsid[0] };
        var mini = start[0] != ENDOFCHAIN && start[0] != FREESECT ? Chain(start[0], size[0]) : Array.Empty<byte>();
        var minifat = new List<uint>(); if (nMini > 0 && firstMini != ENDOFCHAIN && firstMini != FREESECT) { var mf = Chain(firstMini); for (int i = 0; i < mf.Length / 4; i++) minifat.Add(BinaryPrimitives.ReadUInt32LittleEndian(mf.AsSpan(4 * i))); }
        byte[] MiniChain(uint st, long sz) { var o = new MemoryStream(); uint c = st; var seen = new HashSet<uint>(); while (c != ENDOFCHAIN && c != FREESECT && c < minifat.Count && seen.Add(c)) { o.Write(mini, (int)(c * ms), Math.Min(ms, mini.Length - (int)(c * ms))); c = minifat[(int)c]; } var r = o.ToArray(); return sz < r.Length ? r[..(int)sz] : r; }
        var visited = new HashSet<uint>();
        void Walk(uint sid, string prefix)
        {
            if (sid == NOSTREAM || sid >= ne || !visited.Add(sid)) return;
            string path = prefix.Length == 0 ? names[sid] : prefix + "/" + names[sid];
            Walk(left[sid], prefix); Walk(right[sid], prefix);
            if (types[sid] == 1) { cf.Storages[path] = clsid[sid]; Walk(child[sid], path); }
            else if (types[sid] == 2) cf.Streams[path] = size[sid] == 0 ? Array.Empty<byte>() : size[sid] < cutoff ? MiniChain(start[sid], size[sid]) : Chain(start[sid], size[sid]);
        }
        Walk(child[0], "");
        return cf;
    }
    static Span<byte> Sector(byte[] b, uint s, int ss) => b.AsSpan((int)((s + 1) * ss), ss);

    /// <summary>Serialize (v3, 512-byte sectors, mini stream for streams &lt; 4096 bytes, balanced sibling trees).</summary>
    public byte[] Write()
    {
        const int ss = 512;
        var nodes = new SortedDictionary<string, (byte type, byte[] clsid, byte[] data)>(StringComparer.Ordinal);
        foreach (var kv in Streams) { nodes[kv.Key] = (2, new byte[16], kv.Value); AddParents(nodes, kv.Key); }
        foreach (var kv in Storages) { nodes[kv.Key] = (1, kv.Value, Array.Empty<byte>()); AddParents(nodes, kv.Key); }
        // mini stream
        var mini = new MemoryStream(); var minifat = new List<uint>(); var miniStart = new Dictionary<string, uint>();
        var sectors = new List<byte[]>(); var regStart = new Dictionary<string, uint>(); var chains = new List<(uint, int)>();
        uint Alloc(byte[] data) { uint st = (uint)sectors.Count; for (int o = 0; o < data.Length; o += ss) { var sct = new byte[ss]; Array.Copy(data, o, sct, 0, Math.Min(ss, data.Length - o)); sectors.Add(sct); } int cnt = (data.Length + ss - 1) / ss; chains.Add((st, cnt)); return st; }
        foreach (var kv in nodes)
        {
            if (kv.Value.type != 2 || kv.Value.data.Length == 0) continue;
            if (kv.Value.data.Length < 4096)
            {
                uint st = (uint)(mini.Length / 64); miniStart[kv.Key] = st; mini.Write(kv.Value.data); int pad = (int)((64 - mini.Length % 64) % 64); if (pad > 0) mini.Write(new byte[pad]);
                int cnt = (kv.Value.data.Length + 63) / 64; for (int i = 0; i < cnt; i++) minifat.Add(i < cnt - 1 ? st + (uint)i + 1 : ENDOFCHAIN);
            }
            else regStart[kv.Key] = Alloc(kv.Value.data);
        }
        uint minifatStart = ENDOFCHAIN; int minifatCnt = 0; uint miniStreamStart = ENDOFCHAIN;
        if (minifat.Count > 0) { var mfb = new byte[minifat.Count * 4]; for (int i = 0; i < minifat.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(mfb.AsSpan(4 * i), minifat[i]); minifatStart = Alloc(mfb); minifatCnt = (mfb.Length + ss - 1) / ss; }
        if (mini.Length > 0) miniStreamStart = Alloc(mini.ToArray());
        // directory entries
        var entries = new List<(string name, byte type, byte[] clsid, uint start, long size, uint left, uint right, uint child)>();
        entries.Add(("Root Entry", 5, RootClsid, miniStreamStart, mini.Length, NOSTREAM, NOSTREAM, NOSTREAM));
        uint Build(string parent)
        {
            var kids = nodes.Keys.Where(k => Parent(k) == parent).OrderBy(k => Leaf(k).Length).ThenBy(k => Leaf(k).ToUpperInvariant(), StringComparer.Ordinal).ToList();
            var sids = new List<uint>();
            foreach (var k in kids)
            {
                var n = nodes[k]; uint sid = (uint)entries.Count;
                entries.Add((Leaf(k), n.type, n.clsid, n.type == 2 ? (n.data.Length == 0 ? ENDOFCHAIN : n.data.Length < 4096 ? miniStart[k] : regStart[k]) : ENDOFCHAIN, n.type == 2 ? n.data.Length : 0, NOSTREAM, NOSTREAM, NOSTREAM));
                sids.Add(sid);
            }
            for (int i = 0; i < kids.Count; i++) if (nodes[kids[i]].type == 1) { uint c = Build(kids[i]); var e = entries[(int)sids[i]]; entries[(int)sids[i]] = (e.name, e.type, e.clsid, e.start, e.size, e.left, e.right, c); }
            uint Bst(int lo, int hi) { if (lo >= hi) return NOSTREAM; int mid = (lo + hi) / 2; uint sid = sids[mid]; var e = entries[(int)sid]; uint l = Bst(lo, mid), r = Bst(mid + 1, hi); entries[(int)sid] = (e.name, e.type, e.clsid, e.start, e.size, l, r, e.child); return sid; }
            return Bst(0, kids.Count);
        }
        uint rootChild = Build(""); var re = entries[0]; entries[0] = (re.name, re.type, re.clsid, re.start, re.size, re.left, re.right, rootChild);
        var dir = new byte[((entries.Count * 128 + ss - 1) / ss) * ss];
        for (int i = 0; i < dir.Length / 128; i++)
        {
            var e = dir.AsSpan(i * 128, 128);
            if (i < entries.Count)
            {
                var en = entries[i]; var nm = Encoding.Unicode.GetBytes(en.name + "\0"); nm.CopyTo(e); BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(0x40), (ushort)nm.Length); e[0x42] = en.type; e[0x43] = 1;
                BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x44), en.left); BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x48), en.right); BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x4C), en.child);
                en.clsid.AsSpan(0, 16).CopyTo(e.Slice(0x50)); BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x74), en.start); BinaryPrimitives.WriteUInt64LittleEndian(e.Slice(0x78), (ulong)en.size);
            }
            else { BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x44), NOSTREAM); BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x48), NOSTREAM); BinaryPrimitives.WriteUInt32LittleEndian(e.Slice(0x4C), NOSTREAM); }
        }
        uint dirStart = Alloc(dir);
        int nData = sectors.Count, per = ss / 4, nFat = 0;
        while (true) { int n2 = (nData + nFat + per - 1) / per; if (n2 == nFat) break; nFat = n2; }
        var fat = new uint[nFat * per]; Array.Fill(fat, FREESECT);
        foreach (var (st, cnt) in chains) for (int i = 0; i < cnt; i++) fat[st + i] = i < cnt - 1 ? st + (uint)i + 1 : ENDOFCHAIN;
        for (int i = 0; i < nFat; i++) fat[nData + i] = FATSECT;
        if (nFat > 109) throw new NotSupportedException("file too large for this writer");
        var fb = new byte[fat.Length * 4]; for (int i = 0; i < fat.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(fb.AsSpan(4 * i), fat[i]);
        for (int o = 0; o < fb.Length; o += ss) { var sct = new byte[ss]; Array.Copy(fb, o, sct, 0, Math.Min(ss, fb.Length - o)); sectors.Add(sct); }
        var h = new byte[ss]; Sig.CopyTo(h, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x18), 0x3E); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1A), 3); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1C), 0xFFFE); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x1E), 9); BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x2C), (uint)nFat); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x30), dirStart); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x38), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x3C), minifatStart); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x40), (uint)minifatCnt); BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x44), ENDOFCHAIN);
        for (int i = 0; i < 109; i++) BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(0x4C + 4 * i), i < nFat ? (uint)(nData + i) : FREESECT);
        var o2 = new MemoryStream(); o2.Write(h); foreach (var sct in sectors) o2.Write(sct); return o2.ToArray();
    }
    static void AddParents(SortedDictionary<string, (byte, byte[], byte[])> nodes, string path) { for (int i = path.IndexOf('/'); i > 0; i = path.IndexOf('/', i + 1)) { var p = path[..i]; if (!nodes.ContainsKey(p)) nodes[p] = (1, new byte[16], Array.Empty<byte>()); } }
    static string Parent(string p) { int i = p.LastIndexOf('/'); return i < 0 ? "" : p[..i]; }
    static string Leaf(string p) { int i = p.LastIndexOf('/'); return i < 0 ? p : p[(i + 1)..]; }
}

/// <summary>
/// Writes a bus-simulator project folder from an ICD hub by cloning a user-supplied template project (its device file holds the
/// MIL-STD-1553 tree as compound-file storages) and rebuilding the BC message / field nodes. Byte layouts come from sample diffs
/// (analysis docs/10-project-format.md): message cwd1/cwd2 at 0x36/0x38, data words at 0x40, name/description strings at 0x9F;
/// field Contents = u32 6, u16 startBit, u16 startWord, u16 width, string desc, string name; scalar interpreter = clsid + u32 ver + u16 type
/// + min/max/resolution doubles at 0x16/0x1E/0x26 + u16 bits at 0x2E + name at 0x4A. When no template is available the caller falls
/// back to the automation script (<see cref="ApiScript"/>) and the hub JSON/CSV.
/// </summary>
public static class BusSimProject
{
    static readonly byte[] BnrTemplate = Hex("ba075315239c584fa687b8d4c70f7ba2" + "06000000" + "0100" + "000000000000e0c0" + "00000000c0ffdf40" + "000000000000f03f" + "1000" + "0200" + new string('0', 48) + "0e000000" + Hx("Field1\0") + new string('0', 64));
    // 176-byte discrete interpreter stream as found in a template field (4 labelled states); name string at 0x1a
    static readonly byte[] DiscreteTemplate = Hex("51035315d4cbc948b71019a51b1e871c040000000200000000000e0000004600690065006c006400330000000000000000000000cdcd010008000000000000000a0000005a00650072006f000000010000000c00000043006c006500610072000000020000000c000000520065007300650074000000030000000c00000045006a00650063007400000004000000000000000500000000000000060000000000000007000000000000000000cdcd0000");
    static readonly byte[] FieldClsid = new Guid("15530653-9400-488c-af27-8e2092c854f3").ToByteArray();
    static string Hx(string s) => Convert.ToHexString(Encoding.Unicode.GetBytes(s)).ToLowerInvariant();
    static byte[] Hex(string h) => Convert.FromHexString(h);
    static byte[] Str(string s) { if (s.Length == 0) return new byte[4]; var e = Encoding.Unicode.GetBytes(s + "\0"); var o = new byte[4 + e.Length]; BinaryPrimitives.WriteInt32LittleEndian(o, e.Length); e.CopyTo(o, 4); return o; }
    static (string s, int end) RdStr(byte[] b, int o) { int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)); return (Encoding.Unicode.GetString(b, o + 4, Math.Max(0, n)).TrimEnd('\0'), o + 4 + n); }
    static string G(byte[] clsid) => new Guid(clsid).ToString();
    const string MsgClsid = "1553014a-7f30-4c82-ac9b-043151722f6b", MsgsClsid = "155300d8-6611-4345-9975-593949c92644", RtClsid = "15530644-1fd3-40b7-bda9-130e3864f6e1", SaClsid = "1553064e-1e7c-400a-ae6d-390ed05210dc";
    static readonly HashSet<string> ScalarClsids = new(StringComparer.OrdinalIgnoreCase) { "155307ba-9c23-4f58-a687-b8d4c70f7ba2", "155307c0-d62c-4edc-97ac-70e015efcf51", "15530586-63db-49e1-8161-20550eb310bd", "1553097f-412e-43bc-af92-4e815725c43e", "155307d5-6ee3-4e0c-bf22-59514a5f386b", "155307d9-5641-4898-be2f-22ac9a55d0c5" };


    public sealed class Result { public string? Cdv; public string? Cpj; public string Script = ""; public string HubJson = ""; public string HubCsv = ""; public int Messages; public int Words; public bool FileWritten; }

    /// <summary>True when the bus simulator's COM shell is registered on this PC: the ProgID is taken from the BUS_SIM_PROGID environment
    /// variable (no vendor identifier is built into the app) and looked up under HKEY_CLASSES_ROOT.</summary>
    public static bool SimulatorDetected()
    {
        string? progId = Environment.GetEnvironmentVariable("BUS_SIM_PROGID");
        if (string.IsNullOrWhiteSpace(progId) || !OperatingSystem.IsWindows()) return false;
        try { using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(progId); return k != null; } catch { return false; }
    }

    /// <summary>Export: always writes hub JSON/CSV + the automation script; writes the project files when a template folder is given.</summary>
    public static Result Export(IcdHub hub, string outDir, string? templateDir, bool withRt)
    {
        Directory.CreateDirectory(outDir);
        var r = new Result { Messages = hub.Messages.Count, Words = hub.Messages.Sum(m => m.Words.Count) };
        string proj = new DirectoryInfo(outDir).Name;
        r.HubJson = Path.Combine(outDir, "icd_hub.json"); hub.Save(r.HubJson);
        r.HubCsv = Path.Combine(outDir, "icd_hub.csv"); File.WriteAllText(r.HubCsv, hub.ToCsv());
        r.Script = Path.Combine(outDir, proj + "_automation.py"); File.WriteAllText(r.Script, ApiScript(hub));
        if (templateDir == null) return r;
        string? cdv = Directory.EnumerateFiles(templateDir, "*.CDV").FirstOrDefault(), cpj = Directory.EnumerateFiles(templateDir, "*.CPJ").FirstOrDefault();
        if (cdv == null || cpj == null) return r;
        var cf = CompoundFile.Read(File.ReadAllBytes(cdv));
        string? coll = cf.Storages.FirstOrDefault(kv => G(kv.Value) == MsgsClsid).Key;
        if (coll == null) return r;
        var msgs = Children(cf, coll).Where(k => G(cf.Storages[k]) == MsgClsid).ToList();
        if (msgs.Count == 0) return r;
        var tmplMsg = cf.Streams[msgs[0] + "/Contents"]; var msgClsid = cf.Storages[msgs[0]];
        var tmplScalar = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase); byte[]? tmplDiscrete = null;
        foreach (var kv in cf.Streams) if (kv.Key.EndsWith("/INTERPRETER", StringComparison.Ordinal) && kv.Value.Length >= 16) { string c = G(kv.Value[..16]); if (ScalarClsids.Contains(c)) tmplScalar.TryAdd(c, kv.Value); else if (c == "15530351-cbd4-48c9-b710-19a51b1e871c") tmplDiscrete ??= kv.Value; }
        RemoveSubtree(cf, coll + "/CHILDREN"); cf.Storages[coll + "/CHILDREN"] = new byte[16];
        var aliases = new List<(string, string)>(); int n = 0; string cdvBase = Path.GetFileNameWithoutExtension(cdv);
        foreach (var m in hub.Messages)
        {
            string node = $"{coll}/CHILDREN/CHILD {n++}"; cf.Storages[node] = msgClsid;
            string name = "BCMsg_" + Guid.NewGuid().ToString().ToUpperInvariant() + "_";
            cf.Streams[node + "/Contents"] = PatchMsg(tmplMsg, m.Cwd1, 0, name, m.Name);
            WordNodes(cf, node, m.Words, tmplScalar, tmplDiscrete);
            string mon = $"!{cdvBase}!Core 0!MIL-STD-1553 Chan!BC!BC Msgs!{name}"; aliases.Add((m.Name, mon));
            foreach (var w in m.Words) aliases.Add((w.Name, $"{mon}!Field {w.Index:00}-{w.BitStart:00}"));
        }
        if (withRt)
        {
            string? rt0 = cf.Storages.Keys.Where(k => G(cf.Storages[k]) == RtClsid).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            string? sa0 = cf.Storages.Keys.Where(k => G(cf.Storages[k]) == SaClsid).OrderBy(k => k.Length).ThenBy(k => k, StringComparer.Ordinal).FirstOrDefault();
            if (rt0 != null && sa0 != null && cf.Streams.ContainsKey(rt0 + "/Contents") && cf.Streams.ContainsKey(sa0 + "/Contents"))
            {
                string rts = rt0[..rt0.LastIndexOf("/CHILDREN/", StringComparison.Ordinal)]; var rtT = cf.Streams[rt0 + "/Contents"]; var saT = cf.Streams[sa0 + "/Contents"]; var rtC = cf.Storages[rt0]; var saC = cf.Storages[sa0];
                RemoveSubtree(cf, rts + "/CHILDREN"); cf.Storages[rts + "/CHILDREN"] = new byte[16];
                int i = 0;
                foreach (var grp in hub.Messages.GroupBy(m => m.Rt).OrderBy(g => g.Key))
                {
                    string rn = $"{rts}/CHILDREN/CHILD {i++}"; cf.Storages[rn] = rtC;
                    var rb = (byte[])rtT.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(rb.AsSpan(6), (ushort)grp.Key); var (_, e1) = RdStr(rb, 0x8c);
                    cf.Streams[rn + "/Contents"] = rb[..0x8c].Concat(Str($"RT{grp.Key:00}")).Concat(rb[e1..]).ToArray();
                    cf.Storages[rn + "/CHILDREN"] = new byte[16]; int j = 0;
                    foreach (var m in grp)
                    {
                        string sn = $"{rn}/CHILDREN/CHILD {j++}"; cf.Storages[sn] = saC;
                        var sb = (byte[])saT.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(6), (ushort)m.Sa); BinaryPrimitives.WriteUInt16LittleEndian(sb.AsSpan(8), (ushort)(m.Tr == 1 ? 0 : 1)); var (_, e2) = RdStr(sb, 0x98);
                        cf.Streams[sn + "/Contents"] = sb[..0x98].Concat(Str($"SA{m.Sa:00} ({(m.Tr == 1 ? "TX" : "RX")})")).Concat(sb[e2..]).ToArray();
                        WordNodes(cf, sn, m.Words, tmplScalar, tmplDiscrete);
                    }
                }
            }
        }
        r.Cdv = Path.Combine(outDir, Path.GetFileName(cdv)); File.WriteAllBytes(r.Cdv, cf.Write());
        r.Cpj = Path.Combine(outDir, proj + ".CPJ"); File.Copy(cpj, r.Cpj, true);
        foreach (var v in Directory.EnumerateFiles(templateDir, "*.CVW")) File.Copy(v, Path.Combine(outDir, Path.GetFileName(v)), true);
        var seen = new HashSet<string>(); var al = new StringBuilder();
        foreach (var (a, mon) in aliases) { string a2 = new string(a.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray()); if (seen.Add(a2)) al.Append($"shell.AliasCreateFromMoniker('{a2}', r'{mon}')\n"); }
        File.WriteAllText(Path.Combine(outDir, "aliases.py"), al.ToString());
        r.FileWritten = true;
        return r;
    }

    static List<string> Children(CompoundFile cf, string parent) => cf.Storages.Keys.Where(k => k.StartsWith(parent + "/CHILDREN/", StringComparison.Ordinal) && k.LastIndexOf('/') == parent.Length + 9).OrderBy(k => int.TryParse(k[(k.LastIndexOf(' ') + 1)..], out var v) ? v : 0).ToList();
    static void RemoveSubtree(CompoundFile cf, string prefix) { foreach (var k in cf.Storages.Keys.Where(k => k == prefix || k.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList()) cf.Storages.Remove(k); foreach (var k in cf.Streams.Keys.Where(k => k.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList()) cf.Streams.Remove(k); }
    static byte[] PatchMsg(byte[] tmpl, ushort cwd1, ushort cwd2, string name, string desc)
    {
        var b = (byte[])tmpl.Clone(); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x36), cwd1); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x38), cwd2); Array.Clear(b, 0x40, 64);
        var (_, o1) = RdStr(b, 0x9f); var (_, o2) = RdStr(b, o1);
        return b[..0x9f].Concat(Str(name)).Concat(Str(desc)).Concat(b[o2..]).ToArray();
    }
    static void WordNodes(CompoundFile cf, string parent, List<IcdWord> words, Dictionary<string, byte[]> tmplScalar, byte[]? tmplDiscrete)
    {
        RemoveSubtree(cf, parent + "/CHILDREN"); cf.Storages[parent + "/CHILDREN"] = new byte[16];
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i]; string node = $"{parent}/CHILDREN/CHILD {i}"; cf.Storages[node] = FieldClsid;
            var c = new byte[10]; BinaryPrimitives.WriteUInt32LittleEndian(c, 6); BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(4), (ushort)w.BitStart); BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(6), (ushort)w.Index); BinaryPrimitives.WriteUInt16LittleEndian(c.AsSpan(8), (ushort)w.BitLen);
            cf.Streams[node + "/Contents"] = c.Concat(Str("Field ???")).Concat(Str($"Field {w.Index:00}-{w.BitStart:00}")).Concat(Hex("00040000000000")).ToArray();
            string nm = w.Name.Length > 31 ? w.Name[..31] : w.Name;
            if (w.Type.StartsWith("Discrete", StringComparison.OrdinalIgnoreCase))
            {
                var t = tmplDiscrete ?? DiscreteTemplate; var (_, e) = RdStr(t, 0x1a); cf.Streams[node + "/INTERPRETER"] = t[..0x1a].Concat(Str(nm)).Concat(t[e..]).ToArray();
            }
            else
            {
                byte[]? t = null;
                if (w.Type.Equals("BNR", StringComparison.OrdinalIgnoreCase)) t = tmplScalar.GetValueOrDefault("155307ba-9c23-4f58-a687-b8d4c70f7ba2") ?? BnrTemplate;
                t ??= tmplScalar.Values.FirstOrDefault() ?? BnrTemplate;
                var b = (byte[])t.Clone(); int bits = w.BitLen; double sc = w.Scale == 0 ? 1 : w.Scale;
                double mn = bits > 1 ? -Math.Pow(2, bits - 1) * sc : 0, mx = bits > 1 ? (Math.Pow(2, bits - 1) - 1) * sc : sc;
                BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(0x16), mn); BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(0x1e), mx); BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(0x26), sc); BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0x2e), (ushort)bits);
                var (_, e) = RdStr(b, 0x4a); cf.Streams[node + "/INTERPRETER"] = b[..0x4a].Concat(Str(nm)).Concat(b[e..]).ToArray();
            }
        }
    }

    /// <summary>Automation script (COM object model) that recreates the hub's messages and fields inside a running simulator session.</summary>
    public static string ApiScript(IcdHub hub)
    {
        var sb = new StringBuilder();
        sb.Append("\"\"\"Generated by Nexys VIEW ICD export. Run inside the bus simulator's script console (global `shell`) with the target\n");
        sb.Append("project open and stopped, or standalone with pywin32: set the environment variable BUS_SIM_PROGID to the simulator shell's COM ProgID.\"\"\"\n");
        sb.Append("try:\n    shell\nexcept NameError:\n    import os, win32com.client\n    shell = win32com.client.GetActiveObject(os.environ['BUS_SIM_PROGID'])\n\n");
        sb.Append("bc = shell.Cards(1).Chan1553s(1).BC\nFIELD_TYPE = {'BCD': 0, 'BNR': 1, 'ANG': 2, 'ISO': 3, 'Discrete': 4}\n\n");
        bool rate = false;
        foreach (var m in hub.Messages)
        {
            sb.Append($"msg = bc.AddBCMsg({m.Rt}, {m.Sa}, {m.Wc % 32}, {(m.Tr == 1 ? "True" : "False")}, {Py(m.Name)}, False, 0, 0)\n");
            if (m.Bus == "B") sb.Append("msg.BusBOpt = True\n");
            if (m.PeriodMs > 0) { sb.Append($"msg.TransmitRate = {Math.Max(1, (int)Math.Round(1000.0 / m.PeriodMs))}  # Hz (rate-based scheduling)\n"); rate = true; }
            sb.Append("msg.Enable = True\n");
            foreach (var w in m.Words)
            {
                string t = w.Type.StartsWith("Discrete", StringComparison.OrdinalIgnoreCase) ? "Discrete" : w.Type.Equals("BCD", StringComparison.OrdinalIgnoreCase) ? "BCD" : "BNR";
                sb.Append($"msg.Field1553s.AddField(FIELD_TYPE['{t}'], {w.BitStart}, {w.Index}, {w.BitLen}, 0, {Py(w.Name)})  # scale={w.Scale.ToString(System.Globalization.CultureInfo.InvariantCulture)} offset={w.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)} unit={Py(w.Unit)}\n");
            }
            sb.Append('\n');
        }
        if (rate) sb.Append("bc.RateBased = True\n");
        sb.Append("shell.Save(False)\n");
        return sb.ToString();
        static string Py(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }
}
