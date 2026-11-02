using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PAIC.ProjectSystem.Model;

namespace PAIC.ProjectSystem;

/// <summary>A single step that upgrades the raw JSON of schema version <see cref="FromVersion"/> to FromVersion + 1.</summary>
public interface ISchemaMigration
{
    int FromVersion { get; }
    void Apply(JsonObject root);
}

/// <summary>Upgrades older project documents to the current schema; refuses documents from newer versions.</summary>
public sealed class ProjectMigrator
{
    private readonly int _currentVersion;
    private readonly IReadOnlyList<ISchemaMigration> _migrations;

    public ProjectMigrator(int currentVersion, IEnumerable<ISchemaMigration>? migrations = null)
    {
        _currentVersion = currentVersion;
        _migrations = (migrations ?? Enumerable.Empty<ISchemaMigration>()).ToList();
    }

    public static ProjectMigrator Default { get; } = new(ProjectSchema.CurrentVersion);

    /// <returns>True when at least one migration ran.</returns>
    public bool Migrate(JsonObject root)
    {
        int version;
        try { version = root["schemaVersion"]?.GetValue<int>() ?? throw new ProjectFormatException("The project file has no schemaVersion."); }
        catch (InvalidOperationException ex) { throw new ProjectFormatException("The project file has an invalid schemaVersion.", ex); }
        catch (FormatException ex) { throw new ProjectFormatException("The project file has an invalid schemaVersion.", ex); }

        if (version < 1) throw new ProjectFormatException($"Unsupported schema version {version}.");
        if (version > _currentVersion)
            throw new ProjectFormatException(
                $"This project was created by a newer version of PAIC (schema {version}); this version supports up to schema {_currentVersion}.");

        var migrated = false;
        while (version < _currentVersion)
        {
            var step = _migrations.FirstOrDefault(m => m.FromVersion == version)
                       ?? throw new ProjectFormatException($"No migration available from schema {version}.");
            step.Apply(root);
            version++;
            root["schemaVersion"] = version;
            migrated = true;
        }
        return migrated;
    }
}

/// <summary>Reads and writes .paproj JSON (camelCase, string enums, indented).</summary>
public sealed class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ProjectMigrator _migrator;

    public ProjectSerializer(ProjectMigrator? migrator = null) => _migrator = migrator ?? ProjectMigrator.Default;

    public static ProjectSerializer Default { get; } = new();

    public string Serialize(PaicProject project) => JsonSerializer.Serialize(project, Options);

    public PaicProject Deserialize(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new ProjectFormatException("The project file is not valid JSON.", ex); }

        if (node is not JsonObject root) throw new ProjectFormatException("The project file does not contain a project object.");
        _migrator.Migrate(root);

        try
        {
            return root.Deserialize<PaicProject>(Options)
                   ?? throw new ProjectFormatException("The project file is empty.");
        }
        catch (JsonException ex) { throw new ProjectFormatException($"The project file is malformed: {ex.Message}", ex); }
    }
}
