using SkiaSharp;

namespace NexysView.Render;

/// <summary>
/// Colors handed to the renderer by the application from the design tokens (the renderer never defines UI colors).
/// Chrome tokens (Panel … Grid) come from the theme; plot tokens (PlotBg … PlotText) from the plot token set
/// (DESIGN-v2.md §1.3), which is independent of the chrome theme.
/// </summary>
public sealed record RenderTheme(
    SKColor Panel, SKColor PanelAlt, SKColor Border, SKColor Text, SKColor TextMuted, SKColor TextFaint, SKColor Accent, SKColor Grid,
    SKColor PlotBg, SKColor PlotGrid, SKColor PlotAxisText, SKColor PlotFrame, SKColor PlotCursor, SKColor PlotText)
{
    /// <summary>Chrome-only construction (tests, report page): plot tokens default to a light plot.</summary>
    public static RenderTheme FromHex(string panel, string panelAlt, string border, string text, string textMuted, string textFaint, string accent, string grid)
        => new(SKColor.Parse(panel), SKColor.Parse(panelAlt), SKColor.Parse(border), SKColor.Parse(text), SKColor.Parse(textMuted), SKColor.Parse(textFaint), SKColor.Parse(accent), SKColor.Parse(grid),
               SKColor.Parse(panel), SKColor.Parse(grid), SKColor.Parse(textMuted), SKColor.Parse(border), SKColor.Parse(accent), SKColor.Parse(text));
}

/// <summary>Trace palette (DESIGN-v2.md §1.3): 8 colors cycling, lightness variants from the 9th. The application sets it from
/// the plot tokens (dark plot: high-saturation set; light plot: v1 set).</summary>
public static class CurvePalette
{
    static readonly object _lock = new();
    static SKColor[] _base = new[] { "#FFD400", "#00D1FF", "#4CFF4C", "#FF5CE1", "#FF8A00", "#8FA8FF", "#FF5A5A", "#B6FF00" }.Select(SKColor.Parse).ToArray();

    public static void Set(IEnumerable<SKColor> colors) { var a = colors.ToArray(); if (a.Length > 0) lock (_lock) _base = a; }

    public static SKColor Get(int index)
    {
        var b = _base;
        var c = b[index % b.Length];
        int round = index / b.Length;
        if (round == 0) return c;
        c.ToHsl(out var h, out var s, out var l);
        float nl = round % 2 == 1 ? Math.Min(85, l + 18) : Math.Max(25, l - 18);
        return SKColor.FromHsl(h, s, nl);
    }

    public static string HexOf(int index) => "#" + Get(index).ToString()[3..];
}
