using System.Globalization;

namespace PAIC.ThemeEngine;

public static class ThemeColor
{
    /// <summary>Parses #RRGGBB or #AARRGGBB.</summary>
    public static bool TryParse(string? hex, out byte a, out byte r, out byte g, out byte b)
    {
        a = 255; r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex) || hex[0] != '#') return false;
        var digits = hex.AsSpan(1);
        if (digits.Length != 6 && digits.Length != 8) return false;
        foreach (var ch in digits) if (!Uri.IsHexDigit(ch)) return false;
        var offset = 0;
        if (digits.Length == 8) { a = byte.Parse(digits[..2], NumberStyles.HexNumber); offset = 2; }
        r = byte.Parse(digits.Slice(offset, 2), NumberStyles.HexNumber);
        g = byte.Parse(digits.Slice(offset + 2, 2), NumberStyles.HexNumber);
        b = byte.Parse(digits.Slice(offset + 4, 2), NumberStyles.HexNumber);
        return true;
    }

    public static double RelativeLuminance(byte r, byte g, byte b)
    {
        static double Channel(byte c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
    }

    /// <summary>WCAG contrast ratio (1..21) or null when either colour is invalid. Alpha is ignored.</summary>
    public static double? ContrastRatio(string? foreground, string? background)
    {
        if (!TryParse(foreground, out _, out var fr, out var fg, out var fb) ||
            !TryParse(background, out _, out var br, out var bg, out var bb)) return null;
        var l1 = RelativeLuminance(fr, fg, fb);
        var l2 = RelativeLuminance(br, bg, bb);
        var (hi, lo) = l1 >= l2 ? (l1, l2) : (l2, l1);
        return (hi + 0.05) / (lo + 0.05);
    }
}
