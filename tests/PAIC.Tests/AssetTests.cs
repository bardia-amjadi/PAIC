using Microsoft.Extensions.Logging.Abstractions;
using PAIC.AssetManager;
using PAIC.Core.IO;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Recent;
using Xunit;

namespace PAIC.Tests;

public class AssetServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TempDir _sources = new();
    private readonly ProjectService _projects;
    private readonly AssetService _assets;

    public AssetServiceTests()
    {
        _projects = new ProjectService(new RecentProjectsStore(Path.Combine(_dir.Path, "recent.json")), NullLogger<ProjectService>.Instance);
        _projects.CreateAsync(_dir.Path, "Demo").GetAwaiter().GetResult();
        _assets = new AssetService(_projects, NullLogger<AssetService>.Instance);
    }

    public void Dispose() { _dir.Dispose(); _sources.Dispose(); }

    private string Source(string name, string content = "hello")
    {
        var path = Path.Combine(_sources.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string ProjectDir => Path.Combine(_dir.Path, "Demo");

    [Fact]
    public async Task Import_CopiesCategorisesHashesAndMarksDirty()
    {
        var result = await _assets.ImportAsync(new[] { Source("logo.png", "png-data"), Source("LICENSE.txt"), Source("app.exe") });
        Assert.Equal(3, result.Imported.Count);
        Assert.True(_projects.IsDirty);

        var logo = result.Imported.Single(a => a.Path.EndsWith("logo.png"));
        Assert.Equal("assets/images/logo.png", logo.Path);
        Assert.Equal(AssetCategory.Image, logo.Category);
        Assert.Equal(FileHashing.ComputeSha256(Path.Combine(ProjectDir, "assets", "images", "logo.png")), logo.Sha256);
        Assert.Equal(AssetCategory.License, result.Imported.Single(a => a.Path.EndsWith("LICENSE.txt")).Category);
        Assert.Equal(AssetCategory.Application, result.Imported.Single(a => a.Path.EndsWith("app.exe")).Category);
    }

    [Fact]
    public async Task Import_SameNameGetsUniqueName_AndDuplicatesAreReportedOrSkipped()
    {
        var first = Source("a.png", "same");
        await _assets.ImportAsync(new[] { first });

        var again = await _assets.ImportAsync(new[] { first });
        Assert.Equal("assets/images/a (2).png", Assert.Single(again.Imported).Path);
        Assert.Single(again.DuplicateWarnings);

        var skipped = await _assets.ImportAsync(new[] { first }, policy: DuplicatePolicy.Skip);
        Assert.Empty(skipped.Imported);
        Assert.Single(skipped.SkippedDuplicates);

        Assert.Single(_assets.FindDuplicateGroups(), g => g.Count == 2);
    }

    [Fact]
    public async Task Import_MissingSource_IsReportedAsFailure()
    {
        var result = await _assets.ImportAsync(new[] { Path.Combine(_sources.Path, "nope.png") });
        Assert.Empty(result.Imported);
        Assert.Single(result.Failed);
    }

    [Fact]
    public async Task Rename_And_Move_UpdateRegistry_AndKeepProjectValid()
    {
        await _assets.ImportAsync(new[] { Source("logo.png") });
        _assets.Rename("assets/images/logo.png", "brand.png");
        Assert.Equal("assets/images/brand.png", _projects.Current!.Assets.Single().Path);

        _assets.CreateFolder("assets/branding");
        _assets.Move("assets/images/brand.png", "assets/branding");
        Assert.Equal("assets/branding/brand.png", _projects.Current.Assets.Single().Path);
        Assert.True(File.Exists(Path.Combine(ProjectDir, "assets", "branding", "brand.png")));

        _assets.Rename("assets/branding", "marks");
        Assert.Equal("assets/marks/brand.png", _projects.Current.Assets.Single().Path);

        var report = await _projects.ValidateAsync();
        Assert.False(report.HasErrors, string.Join("; ", report.Issues));
    }

    [Fact]
    public async Task Rename_RejectsBadNamesAndCollisions()
    {
        await _assets.ImportAsync(new[] { Source("a.png", "1"), Source("b.png", "2") });
        Assert.Throws<ProjectException>(() => _assets.Rename("assets/images/a.png", "b.png"));
        Assert.Throws<ProjectException>(() => _assets.Rename("assets/images/a.png", "../x.png"));
        Assert.Throws<ProjectException>(() => _assets.Rename("assets", "other"));
    }

    [Fact]
    public async Task PathsOutsideAssets_AreRejected()
    {
        await _assets.ImportAsync(new[] { Source("a.png") });
        Assert.Throws<ProjectException>(() => _assets.CreateFolder("../escape"));
        Assert.Throws<ProjectException>(() => _assets.Delete("Demo.paproj"));
        Assert.Throws<ProjectException>(() => _assets.Move("assets/images/a.png", "assets/../.."));
        Assert.Throws<ProjectException>(() => _assets.ToFullPath("C:\\Windows\\win.ini"));
    }

    [Fact]
    public async Task Move_FolderIntoItself_IsRejected()
    {
        _assets.CreateFolder("assets/one");
        _assets.CreateFolder("assets/one/two");
        Assert.Throws<ProjectException>(() => _assets.Move("assets/one", "assets/one/two"));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Copy_File_AndFolder_RegisterNewAssets()
    {
        await _assets.ImportAsync(new[] { Source("a.png", "x") });
        await _assets.CopyAsync("assets/images/a.png", "assets/images");
        Assert.Equal(2, _projects.Current!.Assets.Count);
        Assert.Equal(2, _projects.Current.Assets.Select(a => a.Id).Distinct().Count());

        await _assets.CopyAsync("assets/images", "assets");
        Assert.Equal(4, _projects.Current.Assets.Count);
        Assert.True(Directory.Exists(Path.Combine(ProjectDir, "assets", "images (2)")));
        await Assert.ThrowsAsync<ProjectException>(() => _assets.CopyAsync("assets/images", "assets/images"));
    }

    [Fact]
    public async Task Delete_BlockedWhenReferenced_UnlessForced()
    {
        var result = await _assets.ImportAsync(new[] { Source("icon.ico", "ico") });
        var icon = result.Imported.Single();
        _projects.Current!.Application.IconAssetId = icon.Id;
        _projects.Current.Theme.Themes[0].BackgroundImageAssetId = icon.Id;

        var uses = Assert.Throws<AssetInUseException>(() => _assets.Delete(icon.Path)).Uses;
        Assert.Equal(2, uses.Count);
        Assert.True(File.Exists(Path.Combine(ProjectDir, "assets", "icons", "icon.ico")));

        _assets.Delete(icon.Path, removeReferences: true);
        Assert.Empty(_projects.Current.Assets);
        Assert.Equal("", _projects.Current.Application.IconAssetId);
        Assert.Equal("", _projects.Current.Theme.Themes[0].BackgroundImageAssetId);
        Assert.False(File.Exists(Path.Combine(ProjectDir, "assets", "icons", "icon.ico")));
    }

    [Fact]
    public async Task Delete_Folder_RemovesContainedAssets()
    {
        await _assets.ImportAsync(new[] { Source("a.png", "1"), Source("b.png", "2") });
        _assets.Delete("assets/images");
        Assert.Empty(_projects.Current!.Assets);
        Assert.False(Directory.Exists(Path.Combine(ProjectDir, "assets", "images")));
    }

    [Fact]
    public async Task Sync_RegistersUntracked_ReportsAndRemovesMissing()
    {
        var untracked = Path.Combine(ProjectDir, "assets", "loose.txt");
        File.WriteAllText(untracked, "loose");
        var tree = _assets.GetTree();
        Assert.True(tree.Children.Single(c => c.Name == "loose.txt").IsUntracked);

        var sync = await _assets.SyncAsync();
        Assert.Equal(1, sync.Registered);
        Assert.Equal("assets/loose.txt", _projects.Current!.Assets.Single().Path);

        File.Delete(untracked);
        var missing = await _assets.SyncAsync();
        Assert.Equal(new[] { "assets/loose.txt" }, missing.MissingFiles);
        Assert.Single(_projects.Current.Assets);

        var removed = await _assets.SyncAsync(removeMissing: true);
        Assert.Equal(1, removed.RemovedMissing);
        Assert.Empty(_projects.Current.Assets);
    }

    [Fact]
    public async Task GetTree_ListsFoldersBeforeFiles()
    {
        await _assets.ImportAsync(new[] { Source("a.png") });
        File.WriteAllText(Path.Combine(ProjectDir, "assets", "z.txt"), "z");
        var tree = _assets.GetTree();
        Assert.Equal(new[] { "images", "z.txt" }, tree.Children.Select(c => c.Name));
        Assert.Equal("a.png", tree.Children[0].Children.Single().Name);
    }
}
