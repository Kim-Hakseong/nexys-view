using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NexysView.Core.Data;

namespace NexysView.App.Import;

/// <summary>
/// Loads a file through an the vendor DataPlugin (public C++ SDK ABI: USP_TDM_MARSHALLER → IRoot) in a separate worker
/// process (NexysView.PluginHost.exe, from tools/dp_minihost). Known hosts: the Python marshaller (+ .py script) and any
/// SDK-built plugin DLL. Invalid-value flags (0 = invalid) become NaN.
/// </summary>
public static class PluginDataSource
{
    public static string HostExe => Path.Combine(AppContext.BaseDirectory, "NexysView.PluginHost.exe");
    public static string PythonMarshaller => @"C:\Program Files\the vendor\Shared\UsiCore\Bin\PythonMarshaller\PythonMarshaller.dll";

    public static DataFile Open(string dataFile, string pluginDll, string? paramXml = null, int timeoutMs = 120000)
    {
        if (!File.Exists(HostExe)) throw new FileNotFoundException("PluginHost worker not found", HostExe);
        string tmp = Path.Combine(Path.GetTempPath(), "nexysview_plugin_" + Guid.NewGuid().ToString("N") + ".json");
        var psi = new ProcessStartInfo(HostExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        psi.ArgumentList.Add("read"); psi.ArgumentList.Add(pluginDll); psi.ArgumentList.Add(dataFile); psi.ArgumentList.Add(tmp);
        if (paramXml != null) psi.ArgumentList.Add(paramXml);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException("DataPlugin worker timed out"); }
        if (p.ExitCode != 0 || !File.Exists(tmp)) throw new InvalidDataException($"DataPlugin worker failed (rc={p.ExitCode}): {err.Trim()}");
        try { return Parse(dataFile, File.ReadAllText(tmp), Path.GetFileNameWithoutExtension(pluginDll)); }
        finally { try { File.Delete(tmp); } catch { } }
    }

    public static DataFile OpenWithPython(string dataFile, string scriptPath) => Open(dataFile, PythonMarshaller, $"<script>{scriptPath}</script>");

    static DataFile Parse(string path, string json, string format)
    {
        var doc = JsonDocument.Parse(json).RootElement;
        var fprops = new Dictionary<string, object>();
        foreach (var k in new[] { "name", "description", "title", "author" }) if (doc.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String) fprops[k] = v.GetString()!;
        var df = new DataFile(path, "Plugin:" + format, fprops, null);
        foreach (var g in doc.GetProperty("groups").EnumerateArray())
        {
            var gp = Props(g);
            var grp = new ChannelGroup(g.GetProperty("name").GetString() ?? "Group", gp);
            df.Groups.Add(grp);
            foreach (var c in g.GetProperty("channels").EnumerateArray())
            {
                var cp = Props(c);
                var vals = c.GetProperty("values").EnumerateArray().ToList();
                int vt = c.GetProperty("vt").GetInt32();
                bool text = vt == 8;
                var arr = new double[vals.Count]; var txt = text ? new string[vals.Count] : null;
                for (int i = 0; i < vals.Count; i++)
                {
                    var e = vals[i];
                    if (text) { txt![i] = e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString(); arr[i] = double.NaN; }
                    else arr[i] = e.ValueKind == JsonValueKind.Number ? e.GetDouble() : e.ValueKind == JsonValueKind.True ? 1 : e.ValueKind == JsonValueKind.False ? 0 : double.NaN;
                }
                if (c.TryGetProperty("flags", out var fl) && fl.ValueKind == JsonValueKind.Array)
                {
                    int i = 0; foreach (var f in fl.EnumerateArray()) { if (i < arr.Length && f.ValueKind == JsonValueKind.Number && ((int)f.GetDouble() & 1) == 0) { arr[i] = double.NaN; if (txt != null) txt[i] = ""; } i++; }
                }
                var ch = new Channel(df.Name, grp.Name, c.GetProperty("name").GetString() ?? "ch", new ArraySource(arr), cp)
                {
                    Unit = c.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : "",
                    Description = c.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "",
                    DataType = text ? "String" : "DBL", IsText = text,
                };
                if (txt != null) cp["text_values"] = txt;
                grp.Channels.Add(ch);
            }
        }
        return df;
    }

    static Dictionary<string, object> Props(JsonElement e)
    {
        var d = new Dictionary<string, object>();
        if (!e.TryGetProperty("props", out var p) || p.ValueKind != JsonValueKind.Object) return d;
        foreach (var kv in p.EnumerateObject())
        {
            d[kv.Name] = kv.Value.ValueKind switch
            {
                JsonValueKind.Number => kv.Value.GetDouble(),
                JsonValueKind.String => kv.Value.GetString()!,
                JsonValueKind.True => true, JsonValueKind.False => false,
                JsonValueKind.Object when kv.Value.TryGetProperty("date", out var dt) => DateTime.FromOADate(dt.GetDouble()),
                _ => kv.Value.ToString(),
            };
        }
        return d;
    }
}
