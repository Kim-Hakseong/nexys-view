using System.Text.RegularExpressions;

namespace NexysView.Core.Data;

/// <summary>Channel-name hierarchy and wildcard search (v3 B3): "Sys_Man.ACT_Apply" → Sys_Man ▸ ACT_Apply; separators '.', '/', '\'.</summary>
public static class NameTree
{
    static readonly char[] Seps = { '.', '/', '\\' };

    public static string[] Parts(string name)
    {
        var p = name.Split(Seps, StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? new[] { name } : p;
    }

    /// <summary>Search matcher: plain text = case-insensitive substring; '*' and '?' are wildcards (anchored to the whole name).</summary>
    public static Regex Matcher(string query)
    {
        string q = query.Trim();
        if (q.Length == 0) return new Regex("", RegexOptions.IgnoreCase);
        if (q.IndexOf('*') < 0 && q.IndexOf('?') < 0) return new Regex(Regex.Escape(q), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        string re = "^" + Regex.Escape(q).Replace("\\*", ".*").Replace("\\?", ".") + "$";
        return new Regex(re, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool Matches(Regex m, string name) => m.IsMatch(name);
}
