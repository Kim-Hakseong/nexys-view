using NexysView.Core.Data;
using NexysView.Core.Tdms;

namespace NexysView.App.Export;

public static partial class TdmsExport
{
    /// <summary>Write every channel of every open file to one TDMS (groups "<file>/<group>"), chunked (1M samples/segment).</summary>
    static partial void SaveAllImpl(Services.AppState state, string path, ref int count)
    {
        var chans = new List<(string group, string name, long length, Func<long, double[], int> read, IReadOnlyDictionary<string, object>? props)>();
        var groups = new Dictionary<string, IReadOnlyDictionary<string, object>>();
        foreach (var f in state.Files)
            foreach (var g in f.Groups)
            {
                string gname = state.Files.Count > 1 ? $"{System.IO.Path.GetFileNameWithoutExtension(f.Name)}/{g.Name}" : g.Name;
                groups[gname] = Clean(g.Properties);
                foreach (var ch in g.Channels)
                {
                    if (ch.IsText) continue;
                    var props = new Dictionary<string, object>(Clean(ch.Properties));
                    if (!string.IsNullOrEmpty(ch.Unit)) props["unit_string"] = ch.Unit;
                    if (!string.IsNullOrEmpty(ch.Description)) props["description"] = ch.Description;
                    if (ch.XKind == XKind.Implicit) { props["wf_start_offset"] = ch.XStart; props["wf_increment"] = ch.XIncrement; props["wf_samples"] = (int)Math.Min(int.MaxValue, ch.Length); props["wf_xunit_string"] = ch.XUnit; }
                    var src = ch.Source;
                    chans.Add((gname, ch.Name, ch.Length, (first, buf) => src.Read(first, buf), props));
                }
            }
        var fprops = new Dictionary<string, object> { ["name"] = System.IO.Path.GetFileNameWithoutExtension(path), ["author"] = "Nexys VIEW", ["datetime"] = DateTime.UtcNow };
        using var w = new TdmsWriter(path);
        w.WriteDoubleChannelsChunked(fprops, groups, chans);
        count = chans.Count;
    }

    static IReadOnlyDictionary<string, object> Clean(IReadOnlyDictionary<string, object> p)
    {
        var d = new Dictionary<string, object>();
        foreach (var kv in p) if (kv.Value is string or double or float or int or long or bool or TdmsTimestamp or DateTime) d[kv.Key] = kv.Value;
        return d;
    }
}
