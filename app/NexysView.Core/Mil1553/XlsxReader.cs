using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;

namespace NexysView.Core.Mil1553;

/// <summary>Minimal .xlsx reader (first worksheet, shared strings, inline strings, numbers) — enough for ICD sheets. No styles/dates.</summary>
public static class XlsxReader
{
    static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Rows of the first sheet as strings (empty string for blank cells); numbers are formatted with the invariant culture.</summary>
    public static List<string[]> ReadFirstSheet(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        var ss = zip.GetEntry("xl/sharedStrings.xml");
        if (ss != null)
        {
            using var s = ss.Open(); var doc = XDocument.Load(s);
            foreach (var si in doc.Root!.Elements(M + "si")) shared.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));
        }
        string sheetPath = "xl/worksheets/sheet1.xml";
        var wb = zip.GetEntry("xl/workbook.xml"); var rels = zip.GetEntry("xl/_rels/workbook.xml.rels");
        if (wb != null && rels != null)
        {
            using var ws = wb.Open(); using var rs = rels.Open();
            var first = XDocument.Load(ws).Root!.Element(M + "sheets")?.Elements(M + "sheet").FirstOrDefault();
            string? rid = first?.Attribute(R + "id")?.Value;
            var rel = XDocument.Load(rs).Root!.Elements(P + "Relationship").FirstOrDefault(x => x.Attribute("Id")?.Value == rid);
            if (rel?.Attribute("Target")?.Value is { } tgt) sheetPath = tgt.StartsWith('/') ? tgt[1..] : "xl/" + tgt;
        }
        var sheet = zip.GetEntry(sheetPath) ?? throw new InvalidDataException("worksheet not found: " + sheetPath);
        using var st = sheet.Open(); var sd = XDocument.Load(st);
        var rows = new List<string[]>();
        foreach (var row in sd.Root!.Element(M + "sheetData")!.Elements(M + "row"))
        {
            var cells = new Dictionary<int, string>(); int maxCol = -1;
            foreach (var c in row.Elements(M + "c"))
            {
                int col = ColIndex(c.Attribute("r")?.Value ?? ""); string t = c.Attribute("t")?.Value ?? "";
                string v = c.Element(M + "v")?.Value ?? "";
                string val = t switch
                {
                    "s" => int.TryParse(v, out var k) && k < shared.Count ? shared[k] : "",
                    "inlineStr" => string.Concat(c.Element(M + "is")?.Descendants(M + "t").Select(x => x.Value) ?? Array.Empty<string>()),
                    "b" => v == "1" ? "1" : "0",
                    _ => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d.ToString("R", CultureInfo.InvariantCulture) : v,
                };
                cells[col] = val; if (col > maxCol) maxCol = col;
            }
            var arr = new string[maxCol + 1]; Array.Fill(arr, "");
            foreach (var kv in cells) if (kv.Key >= 0) arr[kv.Key] = kv.Value;
            rows.Add(arr);
        }
        return rows;
    }

    static int ColIndex(string cellRef)
    {
        int col = 0; foreach (char ch in cellRef) { if (ch >= 'A' && ch <= 'Z') col = col * 26 + (ch - 'A' + 1); else if (ch >= 'a' && ch <= 'z') col = col * 26 + (ch - 'a' + 1); else break; }
        return col - 1;
    }
}
