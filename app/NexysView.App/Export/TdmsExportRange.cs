using NexysView.Core.Data;
using NexysView.Core.Tdms;

namespace NexysView.App.Export;

public static partial class TdmsExport
{
    /// <summary>0.9.3: write a sample range [first, last] of the given channels to one TDMS (group "<file>/<group>" when several files),
    /// chunked like <see cref="SaveAll"/>; implicit x channels keep their rate with the start offset moved to the first exported sample.</summary>
    public static int SaveRange(string path, IReadOnlyList<Channel> chans, long first, long last)
    {
        bool multi = chans.Select(c => c.FileName).Distinct().Count() > 1;
        var list = new List<(string group, string name, long length, Func<long, double[], int> read, IReadOnlyDictionary<string, object>? props)>();
        var groups = new Dictionary<string, IReadOnlyDictionary<string, object>>();
        foreach (var ch in chans)
        {
            if (ch.IsText) continue;
            string gname = multi ? $"{System.IO.Path.GetFileNameWithoutExtension(ch.FileName)}/{ch.Group}" : ch.Group;
            groups.TryAdd(gname, new Dictionary<string, object>());
            var props = new Dictionary<string, object>(Clean(ch.Properties));
            if (!string.IsNullOrEmpty(ch.Unit)) props["unit_string"] = ch.Unit;
            if (!string.IsNullOrEmpty(ch.Description)) props["description"] = ch.Description;
            long len = Math.Max(0, Math.Min(last, ch.Length - 1) - first + 1);
            if (ch.XKind == XKind.Implicit) { props["wf_start_offset"] = ch.XStart + first * ch.XIncrement; props["wf_increment"] = ch.XIncrement; props["wf_samples"] = (int)Math.Min(int.MaxValue, len); if (!string.IsNullOrEmpty(ch.XUnit)) props["wf_xunit_string"] = ch.XUnit; }
            props["nexys_export_first_sample"] = first;
            var src = ch.Source; long f0 = first;
            list.Add((gname, ch.Name, len, (i, buf) => src.Read(f0 + i, buf), props));
        }
        var fprops = new Dictionary<string, object> { ["name"] = System.IO.Path.GetFileNameWithoutExtension(path), ["author"] = "Nexys VIEW", ["datetime"] = DateTime.UtcNow, ["description"] = $"samples {first}..{last}" };
        using var w = new TdmsWriter(path);
        w.WriteDoubleChannelsChunked(fprops, groups, list);
        return list.Count;
    }
}
