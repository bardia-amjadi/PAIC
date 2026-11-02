using PAIC.Core;
using PAIC.ProjectSystem.Model;

namespace PAIC.AssetManager;

/// <summary>A folder or file in the project's assets tree.</summary>
public sealed class AssetNode
{
    public string Name { get; init; } = "";
    /// <summary>Project-relative path with '/' separators, e.g. assets/images/logo.png.</summary>
    public string RelativePath { get; init; } = "";
    public bool IsFolder { get; init; }
    public AssetReference? Asset { get; init; }
    public long Size { get; init; }
    public List<AssetNode> Children { get; } = new();

    /// <summary>Files present on disk but not listed in the project.</summary>
    public bool IsUntracked => !IsFolder && Asset is null;
    public string Display => IsUntracked ? Name + " (untracked)" : Name;
}

public enum DuplicatePolicy { ImportAndReport, Skip }

public sealed record ImportResult(
    IReadOnlyList<AssetReference> Imported,
    IReadOnlyList<string> SkippedDuplicates,
    IReadOnlyList<string> DuplicateWarnings,
    IReadOnlyList<string> Failed);

public sealed record SyncResult(int Registered, IReadOnlyList<string> MissingFiles, int RemovedMissing);

/// <summary>Raised when deleting an asset that the project still uses.</summary>
public sealed class AssetInUseException : UserFacingException
{
    public IReadOnlyList<string> Uses { get; }
    public AssetInUseException(IReadOnlyList<string> uses)
        : base("These assets are still in use: " + string.Join("; ", uses)) => Uses = uses;
}

public static class AssetCategorizer
{
    private static readonly Dictionary<string, AssetCategory> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = AssetCategory.Image, [".jpg"] = AssetCategory.Image, [".jpeg"] = AssetCategory.Image,
        [".bmp"] = AssetCategory.Image, [".gif"] = AssetCategory.Image, [".webp"] = AssetCategory.Image, [".svg"] = AssetCategory.Image,
        [".ico"] = AssetCategory.Icon,
        [".ttf"] = AssetCategory.Font, [".otf"] = AssetCategory.Font, [".woff"] = AssetCategory.Font, [".woff2"] = AssetCategory.Font,
        [".pdf"] = AssetCategory.Document, [".md"] = AssetCategory.Document, [".rtf"] = AssetCategory.Document,
        [".txt"] = AssetCategory.Document, [".html"] = AssetCategory.Document, [".htm"] = AssetCategory.Document,
        [".exe"] = AssetCategory.Application, [".dll"] = AssetCategory.Application, [".msi"] = AssetCategory.Application,
        [".msix"] = AssetCategory.Application, [".appx"] = AssetCategory.Application,
    };

    public static AssetCategory Categorize(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.StartsWith("license", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("eula", StringComparison.OrdinalIgnoreCase)) return AssetCategory.License;
        return Map.TryGetValue(Path.GetExtension(fileName), out var c) ? c : AssetCategory.Other;
    }

    public static string DefaultFolder(AssetCategory category) => category switch
    {
        AssetCategory.Image => "images",
        AssetCategory.Icon => "icons",
        AssetCategory.Font => "fonts",
        AssetCategory.Document => "documents",
        AssetCategory.License => "licenses",
        AssetCategory.Application => "application",
        _ => "other"
    };
}
