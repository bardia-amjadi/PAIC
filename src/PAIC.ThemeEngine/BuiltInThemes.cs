namespace PAIC.ThemeEngine;

public static class BuiltInThemes
{
    public const string FluentLightId = "fluent-light";
    public const string FluentDarkId = "fluent-dark";
    public const string MidnightId = "midnight";

    public static ThemeDefinition FluentLight() => Make(FluentLightId, "Fluent Light", ThemeMode.Light, ThemeMaterial.Mica,
        "#F3F3F3", "#FFFFFF", "#F9F9F9", "#E5E5E5", "#1A1A1A", "#5C5C5C", "#0F6CBD", "#FFFFFF", "#107C10", "#9D5D00", "#C42B1C");

    public static ThemeDefinition FluentDark() => Make(FluentDarkId, "Fluent Dark", ThemeMode.Dark, ThemeMaterial.Mica,
        "#202020", "#2B2B2B", "#323232", "#3D3D3D", "#FFFFFF", "#C5C5C5", "#60CDFF", "#000000", "#6CCB5F", "#FCE100", "#FF99A4");

    public static ThemeDefinition Midnight() => Make(MidnightId, "Midnight Blue", ThemeMode.Dark, ThemeMaterial.Solid,
        "#0B1220", "#131C2E", "#1A2540", "#2A3A5C", "#E8EEF9", "#A9B8D4", "#4C8DFF", "#08101F", "#4CD08A", "#F5B544", "#FF7B7B");

    public static IReadOnlyList<ThemeDefinition> All() => new[] { FluentLight(), FluentDark(), Midnight() };

    public static bool IsBuiltInId(string id) => All().Any(t => t.Id == id);

    private static ThemeDefinition Make(string id, string name, ThemeMode mode, ThemeMaterial material,
        string background, string surface, string surfaceAlt, string border, string text, string textSecondary,
        string accent, string accentText, string success, string warning, string error) => new()
    {
        Id = id, Name = name, Mode = mode, Material = material,
        Colors = new Dictionary<string, string>
        {
            [ThemeTokens.Background] = background, [ThemeTokens.Surface] = surface, [ThemeTokens.SurfaceAlt] = surfaceAlt,
            [ThemeTokens.Border] = border, [ThemeTokens.TextPrimary] = text, [ThemeTokens.TextSecondary] = textSecondary,
            [ThemeTokens.Accent] = accent, [ThemeTokens.AccentText] = accentText,
            [ThemeTokens.Success] = success, [ThemeTokens.Warning] = warning, [ThemeTokens.Error] = error
        }
    };
}
