using System.Text;

namespace NexysView.Core.Tdms;

/// <summary>TDMS object path helpers: "/" , "/'Group'", "/'Group'/'Chan''x'" (quote doubling).</summary>
public static class TdmsPath
{
    public static string Make(params string[] parts)
    {
        if (parts.Length == 0) return "/";
        var sb = new StringBuilder();
        foreach (var p in parts) sb.Append("/'").Append(p.Replace("'", "''")).Append('\'');
        return sb.ToString();
    }

    public static string[] Split(string path)
    {
        var res = new List<string>();
        int i = 0;
        while (i < path.Length)
        {
            if (path[i] == '/') { i++; continue; }
            if (path[i] != '\'') throw new FormatException("bad TDMS path: " + path);
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= path.Length) throw new FormatException("bad TDMS path: " + path);
                if (path[i] == '\'')
                {
                    if (i + 1 < path.Length && path[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                    i++;
                    break;
                }
                sb.Append(path[i++]);
            }
            res.Add(sb.ToString());
        }
        return res.ToArray();
    }
}
