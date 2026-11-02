using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PAIC.Core.IO;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Recent;
using PAIC.ProjectSystem.Validation;
using Xunit;

namespace PAIC.Tests;

public class SerializerTests
{
    [Fact]
    public void RoundTrip_PreservesData()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Architecture = TargetArchitecture.Arm64;
        p.Build.Formats = new() { OutputFormat.Msix, OutputFormat.Zip };

        var json = ProjectSerializer.Default.Serialize(p);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"arm64\"", json);

        var back = ProjectSerializer.Default.Deserialize(json);
        Assert.Equal("Demo", back.Metadata.Name);
        Assert.Equal(TargetArchitecture.Arm64, back.Architecture);
        Assert.Equal(p.Pages.Count, back.Pages.Count);
        Assert.Equal(new[] { OutputFormat.Msix, OutputFormat.Zip }, back.Build.Formats);
    }

    [Fact]
    public void NewerSchema_IsRejected()
    {
        var json = "{\"schemaVersion\": 99}";
        var ex = Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Default.Deserialize(json));
        Assert.Contains("newer", ex.Message);
    }

    [Fact]
    public void InvalidJson_And_MissingVersion_AreRejected()
    {
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Default.Deserialize("not json"));
        Assert.Throws<ProjectFormatException>(() => ProjectSerializer.Default.Deserialize("{}"));
    }

    private sealed class RenameMigration : ISchemaMigration
    {
        public int FromVersion => 1;
        public void Apply(JsonObject root) => root["metadata"]!["name"] = "migrated";
    }

    [Fact]
    public void OlderSchema_IsMigrated()
    {
        var v1 = ProjectSerializer.Default.Serialize(ProjectFactory.CreateNew("Old"));
        var serializer = new ProjectSerializer(new ProjectMigrator(2, new ISchemaMigration[] { new RenameMigration() }));
        var p = serializer.Deserialize(v1);
        Assert.Equal("migrated", p.Metadata.Name);
        Assert.Equal(2, p.SchemaVersion);
    }

    [Fact]
    public void MissingMigration_IsRejected()
    {
        var v1 = ProjectSerializer.Default.Serialize(ProjectFactory.CreateNew("Old"));
        var serializer = new ProjectSerializer(new ProjectMigrator(2));
        Assert.Throws<ProjectFormatException>(() => serializer.Deserialize(v1));
    }
}

public class ValidationTests
{
    private static ProjectValidationContext Ctx(PaicProject p, string? dir = null) => new(p, dir);

    [Fact]
    public void FreshProject_HasNoErrors()
    {
        var report = ProjectValidator.CreateDefault().Validate(Ctx(ProjectFactory.CreateNew("Demo")));
        Assert.False(report.HasErrors);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.2.70000")]
    public void BadVersion_IsError(string version)
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Application.Version = version;
        Assert.Contains(ProjectValidator.CreateDefault().Validate(Ctx(p)).Issues, i => i.Code == "PAIC1003");
    }

    [Fact]
    public void DuplicateIds_AreErrors()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Components.Add(new ComponentDefinition { Id = "core", Name = "Core" });
        p.Components.Add(new ComponentDefinition { Id = "CORE", Name = "Again" });
        Assert.Contains(ProjectValidator.CreateDefault().Validate(Ctx(p)).Issues, i => i.Code == "PAIC1102");
    }

    [Fact]
    public void AssetRules_CatchTraversalMissingAndHashMismatch()
    {
        using var t = new TempDir();
        Directory.CreateDirectory(Path.Combine(t.Path, "assets"));
        File.WriteAllText(Path.Combine(t.Path, "assets", "ok.txt"), "abc");

        var p = ProjectFactory.CreateNew("Demo");
        p.Assets.Add(new AssetReference { Id = "trav", Path = "../x.txt" });
        p.Assets.Add(new AssetReference { Id = "outside", Path = "other/x.txt" });
        p.Assets.Add(new AssetReference { Id = "missing", Path = "assets/missing.txt" });
        p.Assets.Add(new AssetReference { Id = "bad", Path = "assets/ok.txt", Sha256 = new string('0', 64) });
        p.Assets.Add(new AssetReference { Id = "good", Path = "assets/ok.txt",
            Sha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" });

        var codes = ProjectValidator.CreateDefault().Validate(Ctx(p, t.Path)).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC1201", codes);
        Assert.Contains("PAIC1202", codes);
        Assert.Contains("PAIC1203", codes);
        Assert.Single(codes, "PAIC1204");
    }

    [Fact]
    public void Prerequisites_AreChecked()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Prerequisites.Add(new PrerequisiteDefinition
        { Id = "rt", Name = "Runtime", MinVersion = "5.0", MaxVersion = "4.0", DownloadUrl = "http://insecure" });
        var codes = ProjectValidator.CreateDefault().Validate(Ctx(p)).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC1306", codes);
        Assert.Contains("PAIC1307", codes);
    }

    [Fact]
    public void Signing_RequiresValidThumbprint()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Security.SigningEnabled = true;
        p.Security.CertificateThumbprint = "nothex";
        Assert.Contains(ProjectValidator.CreateDefault().Validate(Ctx(p)).Issues, i => i.Code == "PAIC1401");
        p.Security.CertificateThumbprint = new string('A', 40);
        Assert.DoesNotContain(ProjectValidator.CreateDefault().Validate(Ctx(p)).Issues, i => i.Code == "PAIC1401");
    }
}

public class ProjectServiceTests
{
    private static ProjectService NewService(TempDir t) =>
        new(new RecentProjectsStore(Path.Combine(t.Path, "recent.json")), NullLogger<ProjectService>.Instance);

    [Fact]
    public async Task Create_Save_Reopen()
    {
        using var t = new TempDir();
        var svc = NewService(t);
        await svc.CreateAsync(t.Path, "Demo");
        var file = Path.Combine(t.Path, "Demo", "Demo.paproj");
        Assert.True(File.Exists(file));
        Assert.True(Directory.Exists(Path.Combine(t.Path, "Demo", "assets")));

        svc.Current!.Application.Publisher = "Acme";
        svc.MarkDirty();
        Assert.True(svc.IsDirty);
        await svc.SaveAsync();
        Assert.False(svc.IsDirty);

        var other = NewService(t);
        var reopened = await other.OpenAsync(file);
        Assert.Equal("Acme", reopened.Application.Publisher);
        Assert.False(File.Exists(file + ".tmp"));
    }

    [Fact]
    public async Task Create_RejectsInvalidNamesAndExistingProjects()
    {
        using var t = new TempDir();
        var svc = NewService(t);
        await Assert.ThrowsAsync<ProjectException>(() => svc.CreateAsync(t.Path, "bad/name"));
        await Assert.ThrowsAsync<ProjectException>(() => svc.CreateAsync(t.Path, "CON"));
        await svc.CreateAsync(t.Path, "Demo");
        await Assert.ThrowsAsync<ProjectException>(() => svc.CreateAsync(t.Path, "Demo"));
    }

    [Fact]
    public async Task Open_RejectsWrongExtensionAndCorruptFile()
    {
        using var t = new TempDir();
        var svc = NewService(t);
        var txt = Path.Combine(t.Path, "a.txt");
        await File.WriteAllTextAsync(txt, "x");
        await Assert.ThrowsAsync<ProjectException>(() => svc.OpenAsync(txt));

        var bad = Path.Combine(t.Path, "bad.paproj");
        await File.WriteAllTextAsync(bad, "{ not json");
        await Assert.ThrowsAsync<ProjectFormatException>(() => svc.OpenAsync(bad));
    }

    [Fact]
    public async Task SaveAs_CopiesAssets_AndProjectStaysPortable()
    {
        using var a = new TempDir();
        using var b = new TempDir();
        var svc = NewService(a);
        await svc.CreateAsync(a.Path, "Demo");
        var assetFile = Path.Combine(a.Path, "Demo", "assets", "logo.txt");
        await File.WriteAllTextAsync(assetFile, "abc");
        svc.Current!.Assets.Add(new AssetReference
        {
            Id = "logo", Path = "assets/logo.txt", Sha256 = FileHashing.ComputeSha256(assetFile)
        });
        svc.MarkDirty();

        var newFile = Path.Combine(b.Path, "Moved", "Moved.paproj");
        await svc.SaveAsAsync(newFile);

        Assert.Equal(newFile, svc.FilePath);
        Assert.True(File.Exists(Path.Combine(b.Path, "Moved", "assets", "logo.txt")));
        var report = await svc.ValidateAsync();
        Assert.False(report.HasErrors, string.Join("; ", report.Issues));
    }

    [Fact]
    public async Task SaveAs_IntoOwnAssetsFolder_IsRejected()
    {
        using var t = new TempDir();
        var svc = NewService(t);
        await svc.CreateAsync(t.Path, "Demo");
        var inside = Path.Combine(t.Path, "Demo", "assets", "nested", "x.paproj");
        await Assert.ThrowsAsync<ProjectException>(() => svc.SaveAsAsync(inside));
    }

    [Fact]
    public async Task Validate_DetectsMissingAsset_AfterFileDeleted()
    {
        using var t = new TempDir();
        var svc = NewService(t);
        await svc.CreateAsync(t.Path, "Demo");
        svc.Current!.Assets.Add(new AssetReference { Id = "x", Path = "assets/x.bin" });
        var report = await svc.ValidateAsync();
        Assert.Contains(report.Issues, i => i.Code == "PAIC1203");
    }
}

public class RecentProjectsTests
{
    [Fact]
    public void Add_OrdersNewestFirst_DedupesAndDropsMissingFiles()
    {
        using var t = new TempDir();
        var store = new RecentProjectsStore(Path.Combine(t.Path, "recent.json"));
        var f1 = Path.Combine(t.Path, "a.paproj"); File.WriteAllText(f1, "{}");
        var f2 = Path.Combine(t.Path, "b.paproj"); File.WriteAllText(f2, "{}");
        store.Add(f1, "A");
        Thread.Sleep(5);
        store.Add(f2, "B");
        Thread.Sleep(5);
        store.Add(f1, "A");
        Assert.Equal(new[] { "A", "B" }, store.Load().Select(r => r.Name));

        File.Delete(f2);
        Assert.Equal(new[] { "A" }, store.Load().Select(r => r.Name));
        store.Remove(f1);
        Assert.Empty(store.Load());
    }

    [Fact]
    public void CorruptStoreFile_IsTreatedAsEmpty()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "recent.json");
        File.WriteAllText(file, "garbage");
        Assert.Empty(new RecentProjectsStore(file).Load());
    }
}
