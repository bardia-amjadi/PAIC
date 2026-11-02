namespace PAIC.ThemeEngine;

public enum ThemeMode { Light, Dark }
public enum ThemeMaterial { Solid, Mica, Acrylic }

/// <summary>Names of the centralized colour design tokens. Controls reference tokens, never raw colours.</summary>
public static class ThemeTokens
{
    public const string Background = "background";
    public const string Surface = "surface";
    public const string SurfaceAlt = "surfaceAlt";
    public const string Border = "border";
    public const string TextPrimary = "textPrimary";
    public const string TextSecondary = "textSecondary";
    public const string Accent = "accent";
    public const string AccentText = "accentText";
    public const string Success = "success";
    public const string Warning = "warning";
    public const string Error = "error";

    /// <summary>Every theme must define all of these.</summary>
    public static IReadOnlyList<string> Required { get; } = new[]
    {
        Background, Surface, SurfaceAlt, Border, TextPrimary, TextSecondary, Accent, AccentText, Success, Warning, Error
    };
}

public sealed class TypographyTokens
{
    public string FontFamily { get; set; } = "Segoe UI Variable, Segoe UI";
    public double CaptionSize { get; set; } = 12;
    public double BodySize { get; set; } = 14;
    public double SubtitleSize { get; set; } = 20;
    public double TitleSize { get; set; } = 28;
}

public sealed class ShapeTokens
{
    public double CornerRadius { get; set; } = 8;
    public double ControlHeight { get; set; } = 32;
    public double Spacing { get; set; } = 8;
}

/// <summary>A reusable installer theme: colours, typography, shape and background configuration.</summary>
public sealed class ThemeDefinition
{
    /// <summary>Lowercase letters, digits and hyphens, e.g. "fluent-light".</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ThemeMode Mode { get; set; } = ThemeMode.Light;
    public Dictionary<string, string> Colors { get; set; } = new();
    public TypographyTokens Typography { get; set; } = new();
    public ShapeTokens Shape { get; set; } = new();
    public ThemeMaterial Material { get; set; } = ThemeMaterial.Solid;
    /// <summary>Optional id of a project asset used as page background image.</summary>
    public string BackgroundImageAssetId { get; set; } = "";

    public ThemeDefinition Clone() => new()
    {
        Id = Id,
        Name = Name,
        Mode = Mode,
        Colors = new Dictionary<string, string>(Colors),
        Typography = new TypographyTokens
        {
            FontFamily = Typography.FontFamily, CaptionSize = Typography.CaptionSize, BodySize = Typography.BodySize,
            SubtitleSize = Typography.SubtitleSize, TitleSize = Typography.TitleSize
        },
        Shape = new ShapeTokens { CornerRadius = Shape.CornerRadius, ControlHeight = Shape.ControlHeight, Spacing = Shape.Spacing },
        Material = Material,
        BackgroundImageAssetId = BackgroundImageAssetId
    };
}

/// <summary>Theme data stored inside a project, so a project carries the themes it uses.</summary>
public sealed class ThemeSettings
{
    public string ActiveThemeId { get; set; } = "";
    public List<ThemeDefinition> Themes { get; set; } = new();
}

public sealed class ThemeException : PAIC.Core.UserFacingException
{
    public ThemeException(string message) : base(message) { }
    public ThemeException(string message, Exception inner) : base(message, inner) { }
}
