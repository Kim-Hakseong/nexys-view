using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace NexysView.Core.Mil1553;

/// <summary>XML exports of an ICD hub:
/// (1) the bus simulator's message-database import format (Definition/LRUs/LRU/SA/Field with StartWord/StartBit/Bits/Min/Max/Resolution/… —
///     the field interpreter is written as the "interpreter" attribute, offset/units/decimals as the documented subnodes), and
/// (2) a real-time test-system custom-device definition (device → sections per message → channels per word with scale/offset/unit).
/// Both come with an XSD used by <see cref="Validate"/> (the schemas are this project's own, written from the public format documentation).</summary>
public static class IcdXml
{
    public const string DbSchema = """
<?xml version="1.0" encoding="utf-8"?>
<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
  <xs:element name="Definition">
    <xs:complexType><xs:sequence>
      <xs:element name="LRUs"><xs:complexType><xs:sequence>
        <xs:element name="LRU" maxOccurs="unbounded"><xs:complexType><xs:sequence>
          <xs:element name="SA" maxOccurs="unbounded"><xs:complexType><xs:sequence>
            <xs:element name="Field" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:all>
              <xs:element name="StartWord"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="32"/></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="StartBit"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="16"/></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="Bits"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="1"/><xs:maxInclusive value="512"/></xs:restriction></xs:simpleType></xs:element>
              <xs:element name="MCBit" type="xs:integer"/>
              <xs:element name="Min" type="xs:double" minOccurs="0"/><xs:element name="Max" type="xs:double" minOccurs="0"/><xs:element name="Resolution" type="xs:double" minOccurs="0"/>
              <xs:element name="Offset" type="xs:double" minOccurs="0"/><xs:element name="Decimals" type="xs:integer" minOccurs="0"/><xs:element name="Rounding" type="xs:integer" minOccurs="0"/><xs:element name="Unsigned" type="xs:integer" minOccurs="0"/>
              <xs:element name="OperatingLimitMin" type="xs:double" minOccurs="0"/><xs:element name="OperatingLimitMax" type="xs:double" minOccurs="0"/>
              <xs:element name="CompRate" type="xs:integer" minOccurs="0"/><xs:element name="XmitRate" type="xs:integer" minOccurs="0"/>
              <xs:element name="Discretes" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="Discrete" maxOccurs="unbounded"><xs:complexType><xs:simpleContent><xs:extension base="xs:string"><xs:attribute name="id" type="xs:integer" use="required"/></xs:extension></xs:simpleContent></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element>
            </xs:all>
            <xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="units" type="xs:string"/><xs:attribute name="comment" type="xs:string"/><xs:attribute name="interpreter" type="xs:string" use="required"/><xs:attribute name="short" type="xs:string"/>
            </xs:complexType></xs:element>
          </xs:sequence>
          <xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="id" use="required"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="0"/><xs:maxInclusive value="31"/></xs:restriction></xs:simpleType></xs:attribute>
          <xs:attribute name="mode" use="required"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Tx"/><xs:enumeration value="Rx"/></xs:restriction></xs:simpleType></xs:attribute>
          <xs:attribute name="wc" type="xs:integer"/><xs:attribute name="period_ms" type="xs:double"/><xs:attribute name="bus" type="xs:string"/>
          </xs:complexType></xs:element>
          </xs:sequence>
          <xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="rt" use="required"><xs:simpleType><xs:restriction base="xs:integer"><xs:minInclusive value="0"/><xs:maxInclusive value="31"/></xs:restriction></xs:simpleType></xs:attribute>
        </xs:complexType></xs:element>
      </xs:sequence></xs:complexType></xs:element>
    </xs:sequence><xs:attribute name="version" type="xs:string"/><xs:attribute name="source" type="xs:string"/></xs:complexType>
  </xs:element>
</xs:schema>
""";

    public const string DeviceSchema = """
<?xml version="1.0" encoding="utf-8"?>
<xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" elementFormDefault="qualified">
  <xs:element name="CustomDevice">
    <xs:complexType><xs:sequence>
      <xs:element name="Name" type="xs:string"/><xs:element name="GUID" type="xs:string"/><xs:element name="Version" type="xs:string"/>
      <xs:element name="Type"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Asynchronous"/><xs:enumeration value="Inline"/><xs:enumeration value="InlineTiming"/></xs:restriction></xs:simpleType></xs:element>
      <xs:element name="Description" type="xs:string" minOccurs="0"/>
      <xs:element name="Parameters" minOccurs="0"><xs:complexType><xs:sequence><xs:element name="Parameter" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="value" type="xs:string" use="required"/><xs:attribute name="type" type="xs:string"/></xs:complexType></xs:element></xs:sequence></xs:complexType></xs:element>
      <xs:element name="Sections"><xs:complexType><xs:sequence>
        <xs:element name="Section" maxOccurs="unbounded"><xs:complexType><xs:sequence>
          <xs:element name="Parameter" minOccurs="0" maxOccurs="unbounded"><xs:complexType><xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="value" type="xs:string" use="required"/><xs:attribute name="type" type="xs:string"/></xs:complexType></xs:element>
          <xs:element name="Channel" minOccurs="0" maxOccurs="unbounded"><xs:complexType>
            <xs:attribute name="name" type="xs:string" use="required"/>
            <xs:attribute name="direction" use="required"><xs:simpleType><xs:restriction base="xs:string"><xs:enumeration value="Input"/><xs:enumeration value="Output"/></xs:restriction></xs:simpleType></xs:attribute>
            <xs:attribute name="unit" type="xs:string"/><xs:attribute name="default" type="xs:double"/><xs:attribute name="word" type="xs:integer"/><xs:attribute name="bitStart" type="xs:integer"/><xs:attribute name="bitLen" type="xs:integer"/>
            <xs:attribute name="scale" type="xs:double"/><xs:attribute name="offset" type="xs:double"/><xs:attribute name="encoding" type="xs:string"/>
          </xs:complexType></xs:element>
        </xs:sequence><xs:attribute name="name" type="xs:string" use="required"/><xs:attribute name="rt" type="xs:integer"/><xs:attribute name="sa" type="xs:integer"/><xs:attribute name="direction" type="xs:string"/><xs:attribute name="wordCount" type="xs:integer"/><xs:attribute name="period_ms" type="xs:double"/><xs:attribute name="bus" type="xs:string"/></xs:complexType></xs:element>
      </xs:sequence></xs:complexType></xs:element>
    </xs:sequence><xs:attribute name="schemaVersion" type="xs:string"/></xs:complexType>
  </xs:element>
</xs:schema>
""";

    static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Message-database XML: one LRU per RT, one SA entry per message, one Field per word (min/max from bits × scale).</summary>
    public static string ToDbXml(IcdHub hub, string source = "Nexys VIEW")
    {
        var lrus = new XElement("LRUs");
        foreach (var grp in hub.Messages.GroupBy(m => m.Rt).OrderBy(g => g.Key))
        {
            var lru = new XElement("LRU", new XAttribute("name", $"RT{grp.Key:00}"), new XAttribute("rt", grp.Key));
            foreach (var m in grp)
            {
                var sa = new XElement("SA", new XAttribute("name", m.Name), new XAttribute("id", m.Sa), new XAttribute("mode", m.Tr == 1 ? "Tx" : "Rx"), new XAttribute("wc", m.Wc), new XAttribute("period_ms", D(m.PeriodMs)), new XAttribute("bus", m.Bus));
                foreach (var w in m.Words)
                {
                    bool disc = w.Type.StartsWith("Discrete", StringComparison.OrdinalIgnoreCase);
                    string interp = disc ? "1553Discrete" : w.Type.Equals("BCD", StringComparison.OrdinalIgnoreCase) ? "1553BCD" : w.Type.Equals("BNR", StringComparison.OrdinalIgnoreCase) ? "1553BNR" : w.Type;
                    double sc = w.Scale == 0 ? 1 : w.Scale; int bits = w.BitLen;
                    double mn = bits > 1 ? -Math.Pow(2, bits - 1) * sc + w.Offset : w.Offset, mx = bits > 1 ? (Math.Pow(2, bits - 1) - 1) * sc + w.Offset : sc + w.Offset;
                    var f = new XElement("Field", new XAttribute("name", w.Name), new XAttribute("units", w.Unit), new XAttribute("comment", ""), new XAttribute("interpreter", interp),
                        new XElement("StartWord", w.Index), new XElement("StartBit", w.BitStart), new XElement("Bits", w.BitLen), new XElement("MCBit", 0));
                    if (disc) f.Add(new XElement("Discretes", Enumerable.Range(0, Math.Min(1 << Math.Min(bits, 4), 16)).Select(i => new XElement("Discrete", new XAttribute("id", i), $"State {i}"))));
                    else f.Add(new XElement("Min", D(mn)), new XElement("Max", D(mx)), new XElement("Resolution", D(sc)), new XElement("Offset", D(w.Offset)), new XElement("Decimals", 3), new XElement("Rounding", 0), new XElement("Unsigned", bits == 1 ? 1 : 0),
                        new XElement("OperatingLimitMin", D(mn)), new XElement("OperatingLimitMax", D(mx)), new XElement("CompRate", m.PeriodMs > 0 ? (int)Math.Round(1000 / m.PeriodMs) : 0), new XElement("XmitRate", m.PeriodMs > 0 ? (int)Math.Round(1000 / m.PeriodMs) : 0));
                    sa.Add(f);
                }
                lru.Add(sa);
            }
            lrus.Add(lru);
        }
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("Definition", new XAttribute("version", "1.0"), new XAttribute("source", source), lrus));
        return doc.Declaration + "\n" + doc.ToString();
    }

    /// <summary>Custom-device definition: a section per message (RT/SA/direction/word count/period) with a channel per word; EU channels
    /// carry scale/offset/unit so the device engine can apply them.</summary>
    public static string ToDeviceXml(IcdHub hub, string deviceName = "MIL-STD-1553 ICD device")
    {
        var sections = new XElement("Sections");
        foreach (var m in hub.Messages)
        {
            var sec = new XElement("Section", new XAttribute("name", m.Name), new XAttribute("rt", m.Rt), new XAttribute("sa", m.Sa), new XAttribute("direction", m.Tr == 1 ? "RT->BC" : "BC->RT"), new XAttribute("wordCount", m.Wc), new XAttribute("period_ms", D(m.PeriodMs)), new XAttribute("bus", m.Bus),
                new XElement("Parameter", new XAttribute("name", "CommandWord"), new XAttribute("value", m.Cwd1.ToString("X4")), new XAttribute("type", "hex16")));
            foreach (var w in m.Words)
                sec.Add(new XElement("Channel", new XAttribute("name", w.Name), new XAttribute("direction", m.Tr == 1 ? "Input" : "Output"), new XAttribute("unit", w.Unit), new XAttribute("default", "0"),
                    new XAttribute("word", w.Index), new XAttribute("bitStart", w.BitStart), new XAttribute("bitLen", w.BitLen), new XAttribute("scale", D(w.Scale)), new XAttribute("offset", D(w.Offset)), new XAttribute("encoding", w.Type)));
            sections.Add(sec);
        }
        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), new XElement("CustomDevice", new XAttribute("schemaVersion", "1.0"),
            new XElement("Name", deviceName), new XElement("GUID", GuidFor(hub).ToString("B").ToUpperInvariant()), new XElement("Version", "1.0.0"), new XElement("Type", "Asynchronous"),
            new XElement("Description", $"{hub.Messages.Count} messages, {hub.Messages.Sum(m => m.Words.Count)} words — generated from an ICD hub"),
            new XElement("Parameters", new XElement("Parameter", new XAttribute("name", "Bus"), new XAttribute("value", "MIL-STD-1553B")), new XElement("Parameter", new XAttribute("name", "Messages"), new XAttribute("value", hub.Messages.Count), new XAttribute("type", "int"))),
            sections));
        return doc.Declaration + "\n" + doc.ToString();
    }

    static Guid GuidFor(IcdHub hub)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        return new Guid(md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(string.Join(";", hub.Messages.Select(m => $"{m.Rt}/{m.Sa}/{m.Tr}/{m.Wc}")))));
    }

    /// <summary>Validate an XML text against one of the schemas; returns the list of errors (empty = valid).</summary>
    public static List<string> Validate(string xml, string xsd)
    {
        var errors = new List<string>();
        var set = new XmlSchemaSet(); set.Add(null, XmlReader.Create(new StringReader(xsd)));
        var doc = XDocument.Parse(xml);
        doc.Validate(set, (o, e) => errors.Add(e.Message));
        return errors;
    }

    /// <summary>Read a message-database XML back into a hub (round trip of <see cref="ToDbXml"/>).</summary>
    public static IcdHub FromDbXml(string xml)
    {
        var doc = XDocument.Parse(xml); var hub = new IcdHub();
        foreach (var lru in doc.Root!.Element("LRUs")!.Elements("LRU"))
        {
            int rt = (int?)lru.Attribute("rt") ?? 0;
            foreach (var sa in lru.Elements("SA"))
            {
                var m = new IcdMessage { Name = (string?)sa.Attribute("name") ?? "", Rt = rt, Sa = (int?)sa.Attribute("id") ?? 0, Tr = (string?)sa.Attribute("mode") == "Tx" ? 1 : 0, Wc = (int?)sa.Attribute("wc") ?? 0, PeriodMs = (double?)sa.Attribute("period_ms") ?? 0, Bus = (string?)sa.Attribute("bus") ?? "A" };
                foreach (var f in sa.Elements("Field"))
                {
                    string interp = (string?)f.Attribute("interpreter") ?? "1553BNR";
                    m.Words.Add(new IcdWord { Name = (string?)f.Attribute("name") ?? "", Unit = (string?)f.Attribute("units") ?? "", Index = (int?)f.Element("StartWord") ?? 1, BitStart = (int?)f.Element("StartBit") ?? 1, BitLen = (int?)f.Element("Bits") ?? 16,
                        Type = interp == "1553Discrete" ? "Discrete" : interp == "1553BCD" ? "BCD" : interp == "1553BNR" ? "BNR" : interp, Scale = (double?)f.Element("Resolution") ?? 1, Offset = (double?)f.Element("Offset") ?? 0 });
                }
                hub.Messages.Add(m);
            }
        }
        return hub;
    }
}
