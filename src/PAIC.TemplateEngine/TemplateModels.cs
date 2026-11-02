using PAIC.ProjectSystem.Model;

namespace PAIC.TemplateEngine;

public sealed class TemplateInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Custom";
}

/// <summary>An editable project blueprint plus the asset files it needs.</summary>
public sealed class TemplateEntry
{
    public TemplateInfo Info { get; init; } = new();
    public PaicProject Project { get; init; } = new();
    /// <summary>Project-relative path (under assets/) to file content.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; init; } = new Dictionary<string, byte[]>();
    public bool IsBuiltIn { get; init; }
}
