using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace PAIC.ThemeEngine;

/// <summary>Reads and writes .paictheme files: { "format": "paic-theme", "schemaVersion": 1, "theme": {...} }.</summary>
public static class ThemeSerializer
{
    public const string FileExtension = ".paictheme";
    public const string Format = "paic-theme";
    public const int SchemaVersion = 1;
    private const long MaxFileBytes = 1_000_000;

    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ThemeDefinition theme)
    {
        var root = new JsonObject
        {
            ["format"] = Format,
            ["schemaVersion"] = SchemaVersion,
            ["theme"] = JsonSerializer.SerializeToNode(theme, Options)
        };
        return root.ToJsonString(Options);
    }

    /// <summary>Parses and validates a theme document. Validation errors are thrown; warnings are accepted.</summary>
    public static ThemeDefinition Deserialize(string json)
    {
        if (json.Length > MaxFileBytes) throw new ThemeException("The theme file is too large.");
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new ThemeException("The theme file is not valid JSON.", ex); }

        if (node is not JsonObject root || root["format"]?.GetValue<string>() != Format)
            throw new ThemeException("This is not a PAIC theme file.");
        int version;
        try { version = root["schemaVersion"]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new ThemeException("The theme file has an invalid schemaVersion.", ex); }
        if (version != SchemaVersion)
            throw new ThemeException($"Unsupported theme schema version {version}; this version of PAIC supports {SchemaVersion}.");

        ThemeDefinition theme;
        try { theme = root["theme"].Deserialize<ThemeDefinition>(Options) ?? throw new ThemeException("The theme file contains no theme."); }
        catch (JsonException ex) { throw new ThemeException($"The theme file is malformed: {ex.Message}", ex); }

        var report = ThemeValidator.CreateDefault().Validate(theme);
        if (report.HasErrors)
            throw new ThemeException("The theme is invalid: " + string.Join(" ", report.Issues.Where(i => i.Severity == Core.Validation.ValidationSeverity.Error).Take(3).Select(i => i.Message)));
        return theme;
    }
}
