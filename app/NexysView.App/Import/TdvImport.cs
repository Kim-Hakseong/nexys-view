using System.Globalization;
using System.IO;
using System.Xml.Linq;
using NexysView.Core.Data;

namespace NexysView.App.Import;

/// <summary>
/// the reference tool VIEW layout (.tdv, model DataModels/기준 도구/View/2_6, docs/findings.md) → charts/tables in the VIEW canvas.
/// SHEET → AREAS → SPLITTER chain → AREA{DisplayTyp 0 = 2D chart, 1 = channel table}; AREA.DisplayObjectShared →
/// SHAREDDATA.SharedChannelList → SHAREDCHANNELDATA{KanalExtName "[g]/Name", Kanalindex, CurveData → SHAREDCURVEDATA.ReferenceChannel (x)}.
/// Channels are resolved against the open files by "[group index]/name" or name.
/// </summary>
public static class TdvImport
{
    static readonly XNamespace Usi = "http://www.ni.com/Schemas/USI/1_0";

    public sealed record AreaSpec(int DisplayType, string Name, List<(string y, string? x, int colorIndex)> Curves, double? X1, double? X2);

    public static List<AreaSpec> Parse(string path)
    {
        var root = XDocument.Load(path).Root!;
        var data = root.Element(Usi + "data") ?? throw new InvalidDataException("no usi:data");
        var byId = data.Elements().Where(e => e.Attribute("id") != null).ToDictionary(e => e.Attribute("id")!.Value, e => e);
        List<string> Ids(string? v) => v == null ? new() : System.Text.RegularExpressions.Regex.Matches(v, "id\\(\"([^\"]+)\"\\)").Select(m => m.Groups[1].Value).ToList();
        XElement? Ref(XElement? e, string f) { var ids = Ids(e?.Element(f)?.Value); return ids.Count > 0 && byId.TryGetValue(ids[0], out var r) ? r : null; }
        var res = new List<AreaSpec>();
        foreach (var sheet in data.Elements("SHEET"))
        {
            var areas = Ref(sheet, "Areas");
            var sp = Ref(areas, "FirstSplitter");
            var areaList = new List<XElement>();
            int guard = 0;
            while (sp != null && guard++ < 64)
            {
                var a = Ref(sp, "AreaObject"); if (a != null) areaList.Add(a);
                sp = Ref(sp, "NextSplitter");
            }
            if (areaList.Count == 0) areaList.AddRange(data.Elements("AREA"));
            foreach (var a in areaList)
            {
                int dt = int.TryParse(a.Element("DisplayTyp")?.Value, out var t) ? t : 0;
                var shared = Ref(a, "DisplayObjectShared");
                var disp = Ref(a, "DisplayObject");
                var curves = new List<(string, string?, int)>();
                var chanEls = Ids(shared?.Element("SharedChannelList")?.Value).Select(id => byId.TryGetValue(id, out var e) ? e : null).Where(e => e != null).ToList();
                var order = (disp?.Element("CurveList")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => int.TryParse(s, out var i) ? i : -1).Where(i => i >= 0).ToList();
                if (order.Count == 0) order = Enumerable.Range(0, chanEls.Count).ToList();
                foreach (var idx in order)
                {
                    if (idx >= chanEls.Count) continue;
                    var ce = chanEls[idx]!;
                    string y = ce.Element("KanalExtName")?.Value ?? ""; if (y.Length == 0) continue;
                    var cd = Ref(ce, "CurveData");
                    var xr = Ref(cd, "ReferenceChannel");
                    string? x = xr?.Element("KanalExtName")?.Value; if (string.IsNullOrEmpty(x)) x = null;
                    int color = int.TryParse(cd?.Element("CurveColorByColorRef")?.Value, out var cr) ? cr : -1;
                    curves.Add((y, x, color));
                }
                double? x1 = double.TryParse(disp?.Element("DisplayX1")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dx1) ? dx1 : null;
                double? x2 = double.TryParse(disp?.Element("DisplayX2")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var dx2) ? dx2 : null;
                res.Add(new AreaSpec(dt, a.Element("name")?.Value ?? "", curves, x1, x2));
            }
        }
        return res;
    }

    public static Channel? Resolve(Host.AppState state, string ext)
    {
        // "[2]/Speed" → group index (1-based, across open files) + channel name; "Group/Name"; or bare name
        var m = System.Text.RegularExpressions.Regex.Match(ext, @"^\[(\d+)\]/(.+)$");
        var groups = state.Files.SelectMany(f => f.Groups).ToList();
        if (m.Success)
        {
            int gi = int.Parse(m.Groups[1].Value) - 1; string name = m.Groups[2].Value;
            if (gi >= 0 && gi < groups.Count) { var c = groups[gi].Channels.FirstOrDefault(c => c.Name == name); if (c != null) return c; }
            return state.Files.SelectMany(f => f.AllChannels).FirstOrDefault(c => c.Name == name);
        }
        return state.Files.SelectMany(f => f.AllChannels).FirstOrDefault(c => c.DisplayName == ext || c.Name == ext);
    }

    /// <summary>Create charts/tables for every area; returns the number of areas applied.</summary>
    public static int Apply(Services.Engine e, string path)
    {
        var specs = Parse(path);
        int n = 0;
        foreach (var s in specs)
        {
            if (s.DisplayType == 1)
            {
                var t = e.AddTable();
                foreach (var (y, _, _) in s.Curves) { var c = Resolve(e.State, y); if (c != null) e.TableAdd(t.Id, c.Id); }
                n++; continue;
            }
            if (s.DisplayType != 0) continue;
            var chart = e.AddChart(record: false);
            chart.Model.Title = s.Name;
            bool any = false;
            foreach (var (y, x, _) in s.Curves)
            {
                var cy = Resolve(e.State, y); if (cy == null) continue;
                var cx = x != null ? Resolve(e.State, x) : null;
                if (cx != null && cx.Length == cy.Length) e.AddCurve(chart.Id, cy.Id, "xy", cx.Id, record: false); else e.AddCurve(chart.Id, cy.Id, record: false);
                any = true;
            }
            if (any && s.X1 != null && s.X2 != null && s.X2 > s.X1 && chart.Model.Curves[0].XChannel == null)
            {
                // DisplayX1/X2 are 1-based sample indices in the VIEW layout file
                var c0 = chart.Model.Curves[0].Channel;
                chart.Model.XMin = c0.XAt((long)s.X1.Value - 1); chart.Model.XMax = c0.XAt((long)s.X2.Value - 1);
            }
            e.ActiveChart = chart.Id; n++;
        }
        return n;
    }
}
