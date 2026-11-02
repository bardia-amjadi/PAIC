using System.Text.RegularExpressions;
using PAIC.Core.Validation;

namespace PAIC.ThemeEngine;

public static class ThemeValidator
{
    public static Validator<ThemeDefinition> CreateDefault() => new(new IValidationRule<ThemeDefinition>[]
    {
        new IdentityRule(), new ColorRule(), new ContrastRule(), new TypographyShapeRule()
    });

    public static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    private sealed class IdentityRule : IValidationRule<ThemeDefinition>
    {
        public string Id => "theme-identity";
        public IEnumerable<ValidationIssue> Evaluate(ThemeDefinition t)
        {
            if (string.IsNullOrEmpty(t.Id) || t.Id.Length > 60 || !IdPattern.IsMatch(t.Id))
                yield return new("PAIC2001", ValidationSeverity.Error, "Theme id must use lowercase letters, digits and hyphens (max 60 characters).", "id");
            if (string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 60)
                yield return new("PAIC2002", ValidationSeverity.Error, "Theme name is required (max 60 characters).", "name");
        }
    }

    private sealed class ColorRule : IValidationRule<ThemeDefinition>
    {
        public string Id => "theme-colors";
        public IEnumerable<ValidationIssue> Evaluate(ThemeDefinition t)
        {
            foreach (var token in ThemeTokens.Required)
            {
                if (!t.Colors.TryGetValue(token, out var value))
                    yield return new("PAIC2003", ValidationSeverity.Error, $"Colour token '{token}' is missing.", $"colors.{token}");
                else if (!ThemeColor.TryParse(value, out _, out _, out _, out _))
                    yield return new("PAIC2004", ValidationSeverity.Error, $"Colour '{token}' must be #RRGGBB or #AARRGGBB (found '{value}').", $"colors.{token}");
            }
            foreach (var extra in t.Colors.Keys.Except(ThemeTokens.Required))
                yield return new("PAIC2005", ValidationSeverity.Warning, $"Unknown colour token '{extra}' is ignored.", $"colors.{extra}");
        }
    }

    private sealed class ContrastRule : IValidationRule<ThemeDefinition>
    {
        public string Id => "theme-contrast";
        public IEnumerable<ValidationIssue> Evaluate(ThemeDefinition t)
        {
            foreach (var i in Check(t, ThemeTokens.TextPrimary, ThemeTokens.Background, errorBelow: 3.0)) yield return i;
            foreach (var i in Check(t, ThemeTokens.TextPrimary, ThemeTokens.Surface, errorBelow: 3.0)) yield return i;
            foreach (var i in Check(t, ThemeTokens.AccentText, ThemeTokens.Accent, errorBelow: 3.0)) yield return i;
            foreach (var i in Check(t, ThemeTokens.TextSecondary, ThemeTokens.Background, errorBelow: 0)) yield return i;
        }

        private static IEnumerable<ValidationIssue> Check(ThemeDefinition t, string fg, string bg, double errorBelow)
        {
            t.Colors.TryGetValue(fg, out var f);
            t.Colors.TryGetValue(bg, out var b);
            var ratio = ThemeColor.ContrastRatio(f, b);
            if (ratio is null) yield break; // missing/invalid colours are reported by ColorRule
            if (ratio < errorBelow)
                yield return new("PAIC2010", ValidationSeverity.Error, $"'{fg}' on '{bg}' has contrast {ratio:0.0}:1; at least {errorBelow:0.0}:1 is required.", $"colors.{fg}");
            else if (ratio < 4.5)
                yield return new("PAIC2011", ValidationSeverity.Warning, $"'{fg}' on '{bg}' has contrast {ratio:0.0}:1; 4.5:1 is recommended for readable text.", $"colors.{fg}");
        }
    }

    private sealed class TypographyShapeRule : IValidationRule<ThemeDefinition>
    {
        public string Id => "theme-typography-shape";
        public IEnumerable<ValidationIssue> Evaluate(ThemeDefinition t)
        {
            var ty = t.Typography; var sh = t.Shape;
            if (string.IsNullOrWhiteSpace(ty.FontFamily))
                yield return new("PAIC2020", ValidationSeverity.Error, "Font family is required.", "typography.fontFamily");
            foreach (var (name, v) in new[] { ("captionSize", ty.CaptionSize), ("bodySize", ty.BodySize), ("subtitleSize", ty.SubtitleSize), ("titleSize", ty.TitleSize) })
                if (double.IsNaN(v) || v < 8 || v > 96)
                    yield return new("PAIC2021", ValidationSeverity.Error, $"Font size '{name}' must be between 8 and 96.", $"typography.{name}");
            if (!(ty.CaptionSize <= ty.BodySize && ty.BodySize <= ty.SubtitleSize && ty.SubtitleSize <= ty.TitleSize))
                yield return new("PAIC2022", ValidationSeverity.Warning, "Font sizes should increase from caption to title.", "typography");
            if (double.IsNaN(sh.CornerRadius) || sh.CornerRadius < 0 || sh.CornerRadius > 32)
                yield return new("PAIC2030", ValidationSeverity.Error, "Corner radius must be between 0 and 32.", "shape.cornerRadius");
            if (double.IsNaN(sh.ControlHeight) || sh.ControlHeight < 20 || sh.ControlHeight > 64)
                yield return new("PAIC2031", ValidationSeverity.Error, "Control height must be between 20 and 64.", "shape.controlHeight");
            if (double.IsNaN(sh.Spacing) || sh.Spacing < 0 || sh.Spacing > 48)
                yield return new("PAIC2032", ValidationSeverity.Error, "Spacing must be between 0 and 48.", "shape.spacing");
        }
    }
}
