using System.IO;
using NexysView.Core.Mil1553;

namespace NexysView.App.Services;

/// <summary>Built-in bus capture samples (virtual data, written once next to Sample.tdms): a 20,000-record MIL-STD-1553 capture
/// (10 messages, ~1 % errors, bus B every 7th) and a 2,000-word ARINC 429 capture (10 labels). Opened from the empty-state screen.</summary>
public static class SampleBus
{
    public const int Revision = 1;
    public static string? Ensure1553() => Ensure("Sample1553.mon", p => Mil1553File.WriteVirtual(p, 20_000, 7));
    public static string? Ensure429() => Ensure("Sample429.mon", p => Arinc429File.WriteVirtual(p, 2_000, 3));

    static string? Ensure(string name, Action<string> write)
    {
        try
        {
            Directory.CreateDirectory(SampleData.Dir);
            string path = Path.Combine(SampleData.Dir, name), stamp = path + ".rev";
            if (File.Exists(path) && File.Exists(stamp) && File.ReadAllText(stamp).Trim() == Revision.ToString()) return path;
            write(path); File.WriteAllText(stamp, Revision.ToString());
            return path;
        }
        catch (Exception ex) { Diag.Trace("sample bus: " + ex.Message); return null; }
    }
}
