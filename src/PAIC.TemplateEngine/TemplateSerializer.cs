using System.Text.Json;
using System.Text.Json.Nodes;
using PAIC.Core;
using PAIC.Core.IO;
using PAIC.Core.Validation;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Validation;
using PAIC.ThemeEngine;

namespace PAIC.TemplateEngine;

public sealed class TemplateException : UserFacingException
{
    public TemplateException(string message) : base(message) { }
    public TemplateException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// .paictemplate format: { format, schemaVersion, info, project (a full .paproj document), assets: [{ path, data (base64) }] }.
/// </summary>
public static class TemplateSerializer
{
    public const string FileExtension = ".paictemplate";
    public const string Format = "paic-template";
    public const int SchemaVersion = 1;
    public const long MaxTotalAssetBytes = 50L * 1024 * 1024;

    private static readonly JsonSerializerOptions InfoOptions = new(JsonSerializerDefaults.Web);

    public static string Serialize(TemplateEntry template)
    {
        var assets = new JsonArray();
        foreach (var (path, data) in template.Files)
            assets.Add(new JsonObject { ["path"] = path, ["data"] = Convert.ToBase64String(data) });

        var root = new JsonObject
        {
            ["format"] = Format,
            ["schemaVersion"] = SchemaVersion,
            ["info"] = JsonSerializer.SerializeToNode(template.Info, InfoOptions),
            ["project"] = JsonNode.Parse(ProjectSerializer.Default.Serialize(template.Project)),
            ["assets"] = assets
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parses a template and validates its info, project, and every embedded file path. Throws <see cref="TemplateException"/>.</summary>
    public static TemplateEntry Deserialize(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new TemplateException("The template file is not valid JSON.", ex); }

        if (node is not JsonObject root || root["format"]?.GetValue<string>() != Format)
            throw new TemplateException("This is not a PAIC template file.");
        int version;
        try { version = root["schemaVersion"]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new TemplateException("The template has an invalid schemaVersion.", ex); }
        if (version != SchemaVersion)
            throw new TemplateException($"Unsupported template schema version {version}; this version of PAIC supports {SchemaVersion}.");

        TemplateInfo info;
        PaicProject project;
        try
        {
            info = root["info"].Deserialize<TemplateInfo>(InfoOptions) ?? throw new TemplateException("The template has no info section.");
            project = ProjectSerializer.Default.Deserialize((root["project"] ?? throw new TemplateException("The template has no project section.")).ToJsonString());
        }
        catch (JsonException ex) { throw new TemplateException($"The template is malformed: {ex.Message}", ex); }
        catch (ProjectException ex) { throw new TemplateException($"The template's project is invalid: {ex.Message}", ex); }

        ValidateInfo(info);

        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var item in root["assets"] as JsonArray ?? new JsonArray())
        {
            var path = item?["path"]?.GetValue<string>() ?? "";
            if (!PathSafety.IsSafeRelativePath(path, out var reason))
                throw new TemplateException($"Template file '{path}' is not allowed: {reason}");
            var normalized = PathSafety.ToProjectPath(path);
            if (!normalized.StartsWith(ProjectLayout.AssetsFolderName + "/", StringComparison.OrdinalIgnoreCase))
                throw new TemplateException($"Template file '{path}' must be inside the assets folder.");
            byte[] data;
            try { data = Convert.FromBase64String(item?["data"]?.GetValue<string>() ?? ""); }
            catch (FormatException ex) { throw new TemplateException($"Template file '{path}' has invalid content.", ex); }
            total += data.Length;
            if (total > MaxTotalAssetBytes) throw new TemplateException("The template's files are larger than 50 MB.");
            if (!files.TryAdd(normalized, data)) throw new TemplateException($"Template file '{path}' appears twice.");
        }

        foreach (var asset in project.Assets)
        {
            var path = PathSafety.ToProjectPath(asset.Path);
            if (!files.TryGetValue(path, out var data))
                throw new TemplateException($"The template lists asset '{asset.Path}' but does not contain its file.");
            if (!string.IsNullOrWhiteSpace(asset.Sha256) &&
                !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new TemplateException($"The file for asset '{asset.Path}' does not match its checksum.");
        }
        var listed = project.Assets.Select(a => PathSafety.ToProjectPath(a.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = files.Keys.FirstOrDefault(f => !listed.Contains(f));
        if (extra is not null) throw new TemplateException($"The template contains a file that is not a listed asset: {extra}");

        var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(project, null));
        if (report.HasErrors)
            throw new TemplateException("The template's project is invalid: " +
                string.Join(" ", report.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3).Select(i => i.Message)));

        return new TemplateEntry { Info = info, Project = project, Files = files, IsBuiltIn = false };
    }

    internal static void ValidateInfo(TemplateInfo info)
    {
        if (!ThemeValidator.IdPattern.IsMatch(info.Id ?? "") || info.Id!.Length > 60)
            throw new TemplateException("Template id must use lowercase letters, digits and hyphens.");
        if (string.IsNullOrWhiteSpace(info.Name) || info.Name.Length > 80)
            throw new TemplateException("Template name is required (max 80 characters).");
    }
}
