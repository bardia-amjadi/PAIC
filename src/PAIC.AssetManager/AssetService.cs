using System.Text;
using Microsoft.Extensions.Logging;
using PAIC.Core.IO;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;

namespace PAIC.AssetManager;

/// <summary>File-explorer style operations on the open project's assets/ folder, keeping the project's asset list in sync.</summary>
public interface IAssetService
{
    AssetNode GetTree();
    Task<ImportResult> ImportAsync(IEnumerable<string> sourceFiles, string? targetFolder = null,
        DuplicatePolicy policy = DuplicatePolicy.ImportAndReport, CancellationToken ct = default);
    void CreateFolder(string relativePath);
    void Rename(string relativePath, string newName);
    void Move(string relativePath, string destinationFolder);
    Task CopyAsync(string relativePath, string destinationFolder, CancellationToken ct = default);
    /// <summary>Deletes a file or folder. Throws <see cref="AssetInUseException"/> if it is referenced, unless removeReferences is true.</summary>
    void Delete(string relativePath, bool removeReferences = false);
    IReadOnlyList<string> FindUses(string relativePath);
    IReadOnlyList<IReadOnlyList<AssetReference>> FindDuplicateGroups();
    Task<SyncResult> SyncAsync(bool removeMissing = false, CancellationToken ct = default);
    string ToFullPath(string relativePath);
}

public sealed class AssetService : IAssetService
{
    private readonly IProjectService _projects;
    private readonly ILogger<AssetService> _logger;
    private const string Root = ProjectLayout.AssetsFolderName;

    public AssetService(IProjectService projects, ILogger<AssetService> logger)
    {
        _projects = projects;
        _logger = logger;
    }

    // ---------- context & path helpers ----------

    private (PaicProject project, string dir) Ctx()
    {
        var p = _projects.Current ?? throw new ProjectException("No project is open.");
        return (p, Path.GetDirectoryName(_projects.FilePath!)!);
    }

    /// <summary>Normalises to a project-relative path that is "assets" or inside it.</summary>
    private static string Normalize(string relative)
    {
        var rel = PathSafety.ToProjectPath(relative ?? "").Trim('/');
        if (!PathSafety.IsSafeRelativePath(rel, out var reason)) throw new ProjectException($"Invalid asset path '{relative}': {reason}");
        if (!string.Equals(rel, Root, StringComparison.OrdinalIgnoreCase) &&
            !rel.StartsWith(Root + "/", StringComparison.OrdinalIgnoreCase))
            throw new ProjectException($"'{relative}' is not inside the {Root} folder.");
        return rel;
    }

    private static string Full(string dir, string rel) => PathSafety.ResolveUnder(dir, rel);

    public string ToFullPath(string relativePath)
    {
        var (_, dir) = Ctx();
        return Full(dir, Normalize(relativePath));
    }

    private static string ValidateName(string name)
    {
        var n = name?.Trim() ?? "";
        if (n.Length == 0 || n is "." or ".." || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.EndsWith('.'))
            throw new ProjectException($"'{name}' is not a valid file or folder name.");
        return n;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name) sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
        return sb.ToString().TrimEnd('.', ' ');
    }

    private static string UniqueRelative(string dir, string folder, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (var i = 2; File.Exists(Full(dir, folder + "/" + candidate)) || Directory.Exists(Full(dir, folder + "/" + candidate)); i++)
            candidate = $"{stem} ({i}){ext}";
        return folder + "/" + candidate;
    }

    private static string UniqueId(PaicProject p, string rel)
    {
        var slug = new StringBuilder();
        foreach (var ch in rel[(Root.Length + 1)..].ToLowerInvariant())
            slug.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        var baseId = slug.ToString().Trim('-');
        if (baseId.Length == 0) baseId = "asset";
        var id = baseId;
        for (var i = 2; p.Assets.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)); i++) id = $"{baseId}-{i}";
        return id;
    }

    private static AssetReference? Find(PaicProject p, string rel) =>
        p.Assets.FirstOrDefault(a => string.Equals(PathSafety.ToProjectPath(a.Path), rel, StringComparison.OrdinalIgnoreCase));

    private static List<AssetReference> AssetsUnder(PaicProject p, string rel) =>
        p.Assets.Where(a =>
        {
            var path = PathSafety.ToProjectPath(a.Path);
            return string.Equals(path, rel, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(rel + "/", StringComparison.OrdinalIgnoreCase);
        }).ToList();

    private static bool IsInside(string parentRel, string candidateRel) =>
        string.Equals(parentRel, candidateRel, StringComparison.OrdinalIgnoreCase) ||
        candidateRel.StartsWith(parentRel + "/", StringComparison.OrdinalIgnoreCase);

    // ---------- tree ----------

    public AssetNode GetTree()
    {
        var (p, dir) = Ctx();
        var rootPath = Full(dir, Root);
        Directory.CreateDirectory(rootPath);
        return Build(p, dir, rootPath, Root, Root);
    }

    private static AssetNode Build(PaicProject p, string dir, string fullPath, string rel, string name)
    {
        var node = new AssetNode { Name = name, RelativePath = rel, IsFolder = true };
        foreach (var sub in Directory.EnumerateDirectories(fullPath).OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase))
        {
            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
            var subName = Path.GetFileName(sub);
            node.Children.Add(Build(p, dir, sub, rel + "/" + subName, subName));
        }
        foreach (var file in Directory.EnumerateFiles(fullPath).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var fileName = Path.GetFileName(file);
            var fileRel = rel + "/" + fileName;
            node.Children.Add(new AssetNode
            {
                Name = fileName, RelativePath = fileRel, IsFolder = false,
                Asset = Find(p, fileRel), Size = new FileInfo(file).Length
            });
        }
        return node;
    }

    // ---------- import ----------

    public async Task<ImportResult> ImportAsync(IEnumerable<string> sourceFiles, string? targetFolder = null,
        DuplicatePolicy policy = DuplicatePolicy.ImportAndReport, CancellationToken ct = default)
    {
        var (p, dir) = Ctx();
        var folderOverride = targetFolder is null ? null : Normalize(targetFolder);
        var imported = new List<AssetReference>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var failed = new List<string>();

        foreach (var source in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!File.Exists(source)) { failed.Add($"{source}: file not found"); continue; }
                var name = Sanitize(Path.GetFileName(source));
                if (name.Length == 0) { failed.Add($"{source}: invalid file name"); continue; }

                var category = AssetCategorizer.Categorize(name);
                var folder = folderOverride ?? $"{Root}/{AssetCategorizer.DefaultFolder(category)}";
                Directory.CreateDirectory(Full(dir, folder));

                var hash = await FileHashing.ComputeSha256Async(source, ct);
                var duplicate = p.Assets.FirstOrDefault(a => string.Equals(a.Sha256, hash, StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null && policy == DuplicatePolicy.Skip)
                {
                    skipped.Add($"{name} is identical to {duplicate.Path}");
                    continue;
                }

                var rel = UniqueRelative(dir, folder, name);
                File.Copy(source, Full(dir, rel), overwrite: false);
                var asset = new AssetReference { Id = UniqueId(p, rel), Path = rel, Category = category, Sha256 = hash };
                p.Assets.Add(asset);
                imported.Add(asset);
                if (duplicate is not null) warnings.Add($"{rel} has the same content as {duplicate.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{source}: {ex.Message}");
            }
        }

        if (imported.Count > 0) _projects.MarkDirty();
        _logger.LogInformation("Imported {Count} asset(s), skipped {Skipped}, failed {Failed}", imported.Count, skipped.Count, failed.Count);
        return new ImportResult(imported, skipped, warnings, failed);
    }

    // ---------- folder / rename / move / copy / delete ----------

    public void CreateFolder(string relativePath)
    {
        var (_, dir) = Ctx();
        var rel = Normalize(relativePath);
        var full = Full(dir, rel);
        if (Directory.Exists(full) || File.Exists(full)) throw new ProjectException($"'{rel}' already exists.");
        Directory.CreateDirectory(full);
        _logger.LogInformation("Created asset folder {Path}", rel);
    }

    public void Rename(string relativePath, string newName)
    {
        var (p, dir) = Ctx();
        var rel = Normalize(relativePath);
        if (string.Equals(rel, Root, StringComparison.OrdinalIgnoreCase)) throw new ProjectException("The assets folder cannot be renamed.");
        var name = ValidateName(newName);
        var parent = rel[..rel.LastIndexOf('/')];
        var newRel = parent + "/" + name;
        Relocate(p, dir, rel, newRel);
        _logger.LogInformation("Renamed {From} to {To}", rel, newRel);
    }

    public void Move(string relativePath, string destinationFolder)
    {
        var (p, dir) = Ctx();
        var rel = Normalize(relativePath);
        if (string.Equals(rel, Root, StringComparison.OrdinalIgnoreCase)) throw new ProjectException("The assets folder cannot be moved.");
        var dest = Normalize(destinationFolder);
        Directory.CreateDirectory(Full(dir, dest));
        var newRel = dest + "/" + rel[(rel.LastIndexOf('/') + 1)..];
        if (IsInside(rel, dest) && Directory.Exists(Full(dir, rel))) throw new ProjectException("A folder cannot be moved into itself.");
        Relocate(p, dir, rel, newRel);
        _logger.LogInformation("Moved {From} to {To}", rel, newRel);
    }

    private void Relocate(PaicProject p, string dir, string rel, string newRel)
    {
        var from = Full(dir, rel);
        var to = Full(dir, newRel);
        if (string.Equals(rel, newRel, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(to) || Directory.Exists(to)) throw new ProjectException($"'{newRel}' already exists.");

        var affected = AssetsUnder(p, rel);
        try
        {
            if (Directory.Exists(from)) Directory.Move(from, to);
            else if (File.Exists(from)) File.Move(from, to);
            else throw new ProjectException($"'{rel}' does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not move '{rel}': {ex.Message}", ex);
        }
        foreach (var a in affected)
        {
            var path = PathSafety.ToProjectPath(a.Path);
            a.Path = newRel + path[rel.Length..];
        }
        _projects.MarkDirty();
    }

    public async Task CopyAsync(string relativePath, string destinationFolder, CancellationToken ct = default)
    {
        var (p, dir) = Ctx();
        var rel = Normalize(relativePath);
        var dest = Normalize(destinationFolder);
        var source = Full(dir, rel);
        Directory.CreateDirectory(Full(dir, dest));

        try
        {
            if (File.Exists(source))
            {
                await CopyFileAsync(p, dir, rel, UniqueRelative(dir, dest, rel[(rel.LastIndexOf('/') + 1)..]), ct);
            }
            else if (Directory.Exists(source))
            {
                if (IsInside(rel, dest)) throw new ProjectException("A folder cannot be copied into itself.");
                var newFolder = UniqueRelative(dir, dest, rel[(rel.LastIndexOf('/') + 1)..]);
                Directory.CreateDirectory(Full(dir, newFolder));
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
                    var sub = PathSafety.ToProjectPath(Path.GetRelativePath(source, file));
                    var target = newFolder + "/" + sub;
                    Directory.CreateDirectory(Path.GetDirectoryName(Full(dir, target))!);
                    await CopyFileAsync(p, dir, rel + "/" + sub, target, ct);
                }
            }
            else throw new ProjectException($"'{rel}' does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not copy '{rel}': {ex.Message}", ex);
        }
        _projects.MarkDirty();
        _logger.LogInformation("Copied {Path} to {Destination}", rel, dest);
    }

    private static Task CopyFileAsync(PaicProject p, string dir, string fromRel, string toRel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        File.Copy(Full(dir, fromRel), Full(dir, toRel), overwrite: false);
        var original = Find(p, fromRel);
        if (original is not null)
            p.Assets.Add(new AssetReference { Id = UniqueId(p, toRel), Path = toRel, Category = original.Category, Sha256 = original.Sha256 });
        return Task.CompletedTask;
    }

    public void Delete(string relativePath, bool removeReferences = false)
    {
        var (p, dir) = Ctx();
        var rel = Normalize(relativePath);
        if (string.Equals(rel, Root, StringComparison.OrdinalIgnoreCase)) throw new ProjectException("The assets folder cannot be deleted.");
        var full = Full(dir, rel);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new ProjectException($"'{rel}' does not exist.");

        var affected = AssetsUnder(p, rel);
        var uses = UsesOf(p, affected);
        if (uses.Count > 0 && !removeReferences) throw new AssetInUseException(uses);
        ClearReferences(p, affected);

        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, recursive: true); else File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectException($"Could not delete '{rel}': {ex.Message}", ex);
        }
        foreach (var a in affected) p.Assets.Remove(a);
        _projects.MarkDirty();
        _logger.LogInformation("Deleted {Path} ({Count} registered asset(s))", rel, affected.Count);
    }

    // ---------- references ----------

    public IReadOnlyList<string> FindUses(string relativePath)
    {
        var (p, _) = Ctx();
        return UsesOf(p, AssetsUnder(p, Normalize(relativePath)));
    }

    private static List<string> UsesOf(PaicProject p, List<AssetReference> assets)
    {
        var ids = assets.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uses = new List<string>();
        if (ids.Contains(p.Application.IconAssetId ?? "")) uses.Add("Application icon");
        foreach (var t in p.Theme.Themes.Where(t => ids.Contains(t.BackgroundImageAssetId ?? "")))
            uses.Add($"Background image of theme '{t.Name}'");
        return uses;
    }

    private static void ClearReferences(PaicProject p, List<AssetReference> assets)
    {
        var ids = assets.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ids.Contains(p.Application.IconAssetId ?? "")) p.Application.IconAssetId = "";
        foreach (var t in p.Theme.Themes.Where(t => ids.Contains(t.BackgroundImageAssetId ?? ""))) t.BackgroundImageAssetId = "";
    }

    private static bool ExistsSafe(string dir, string path)
    {
        try { return File.Exists(Full(dir, Normalize(path))); }
        catch (Exception ex) when (ex is ProjectException or InvalidOperationException) { return false; }
    }

    // ---------- duplicates & sync ----------

    public IReadOnlyList<IReadOnlyList<AssetReference>> FindDuplicateGroups()
    {
        var (p, _) = Ctx();
        return p.Assets.Where(a => !string.IsNullOrWhiteSpace(a.Sha256))
            .GroupBy(a => a.Sha256.ToLowerInvariant())
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<AssetReference>)g.ToList())
            .ToList();
    }

    public async Task<SyncResult> SyncAsync(bool removeMissing = false, CancellationToken ct = default)
    {
        var (p, dir) = Ctx();
        var rootFull = Full(dir, Root);
        Directory.CreateDirectory(rootFull);
        var registered = 0;

        foreach (var file in Directory.EnumerateFiles(rootFull, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) continue;
            var rel = Root + "/" + PathSafety.ToProjectPath(Path.GetRelativePath(rootFull, file));
            if (Find(p, rel) is not null) continue;
            p.Assets.Add(new AssetReference
            {
                Id = UniqueId(p, rel), Path = rel, Category = AssetCategorizer.Categorize(file),
                Sha256 = await FileHashing.ComputeSha256Async(file, ct)
            });
            registered++;
        }

        var missing = p.Assets.Where(a => !ExistsSafe(dir, a.Path)).ToList();
        var removed = 0;
        if (removeMissing)
        {
            ClearReferences(p, missing);
            foreach (var a in missing) { p.Assets.Remove(a); removed++; }
        }
        if (registered > 0 || removed > 0) _projects.MarkDirty();
        _logger.LogInformation("Asset sync: {Registered} registered, {Missing} missing, {Removed} removed", registered, missing.Count, removed);
        return new SyncResult(registered, missing.Select(a => a.Path).ToList(), removed);
    }
}
