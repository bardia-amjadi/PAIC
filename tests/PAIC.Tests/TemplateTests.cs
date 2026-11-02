using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PAIC.Core.IO;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Recent;
using PAIC.ProjectSystem.Validation;
using PAIC.TemplateEngine;
using Xunit;

namespace PAIC.Tests;

public class ProjectThemeValidationTests
{
    [Fact]
    public void NewProject_EmbedsActiveTheme()
    {
        var p = ProjectFactory.CreateNew("Demo");
        Assert.Equal("fluent-light", p.Theme.ActiveThemeId);
        Assert.Single(p.Theme.Themes);
    }

    [Fact]
    public void Phase1ProjectFile_WithoutTheme_StillOpens_WithWarning()
    {
        var p = ProjectFactory.CreateNew("Old");
        var json = JsonNode.Parse(ProjectSerializer.Default.Serialize(p))!.AsObject();
        json.Remove("theme");
        var back = ProjectSerializer.Default.Deserialize(json.ToJsonString());
        Assert.Empty(back.Theme.Themes);
        var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(back, null));
        Assert.Contains(report.Issues, i => i.Code == "PAIC1501");
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void UnknownActiveTheme_AndMissingBackgroundAsset_AreErrors()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Theme.ActiveThemeId = "nope";
        p.Theme.Themes[0].BackgroundImageAssetId = "ghost";
        var codes = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues.Select(i => i.Code).ToList();
        Assert.Contains("PAIC1502", codes);
        Assert.Contains("PAIC1503", codes);
    }

    [Fact]
    public void InvalidEmbeddedTheme_IsReported_WithLocation()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Theme.Themes[0].Colors["accent"] = "nope";
        var issue = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues.First(i => i.Code == "PAIC2004");
        Assert.Contains("themes[fluent-light]", issue.Location);
    }

    [Fact]
    public void UnknownOperationKind_IsWarning()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Operations.Add(new InstallOperationDefinition { Id = "x", Kind = "teleport" });
        var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null));
        Assert.Contains(report.Issues, i => i.Code == "PAIC1308");
        Assert.False(report.HasErrors);
    }
}

public class TemplateTests
{
    private static TemplateService Service(TempDir t) => new(Path.Combine(t.Path, "templates"));

    [Fact]
    public void BuiltIns_AreTenAndUnique()
    {
        var all = BuiltInTemplates.All();
        Assert.Equal(10, all.Count);
        Assert.Equal(10, all.Select(t => t.Info.Id).Distinct().Count());
        Assert.All(all, t => Assert.True(t.IsBuiltIn));
    }

    [Fact]
    public void BuiltIns_AreValidProjects_AndSurviveExportImport()
    {
        foreach (var template in BuiltInTemplates.All())
        {
            var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(template.Project, null));
            Assert.False(report.HasErrors, $"{template.Info.Id}: {string.Join("; ", report.Issues)}");

            var round = TemplateSerializer.Deserialize(TemplateSerializer.Serialize(template));
            Assert.Equal(template.Info.Id, round.Info.Id);
            Assert.Equal(template.Project.Pages.Count, round.Project.Pages.Count);
            Assert.Equal(template.Project.Build.Formats, round.Project.Build.Formats);
        }
    }

    [Fact]
    public void Prepare_PersonalisesProject_AndDoesNotMutateTemplate()
    {
        var template = BuiltInTemplates.All().Single(t => t.Info.Id == "desktop-application");
        var (project, files) = Service(new TempDir()).Prepare(template, "Contoso Notes");

        Assert.Equal("Contoso Notes", project.Metadata.Name);
        Assert.Equal("Contoso Notes", project.Application.ProductName);
        Assert.Equal(@"%ProgramFiles%\Contoso Notes", project.Installation.DefaultInstallDirectory);
        Assert.Contains(project.Operations, o => o.Parameters.GetValueOrDefault("target") == @"{InstallDir}\Contoso Notes.exe");
        Assert.NotEqual(template.Project.Metadata.Id, project.Metadata.Id);
        Assert.Equal("{ProductName}", template.Project.Application.ProductName);
        Assert.Empty(files);

        var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(project, null));
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void Prepare_RejectsInvalidProjectName() =>
        Assert.Throws<ProjectException>(() => Service(new TempDir()).Prepare(BuiltInTemplates.All()[0], "bad/name"));

    [Fact]
    public void PortableTemplate_HasNoPagesButNoPageWarning()
    {
        var portable = BuiltInTemplates.All().Single(t => t.Info.Id == "portable");
        Assert.Empty(portable.Project.Pages);
        var report = ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(portable.Project, null));
        Assert.DoesNotContain(report.Issues, i => i.Code == "PAIC1007");
    }

    [Fact]
    public async Task CreateProjectFromTemplate_WritesProjectAndAssets()
    {
        using var dir = new TempDir();
        var projects = new ProjectService(new RecentProjectsStore(Path.Combine(dir.Path, "r.json")), NullLogger<ProjectService>.Instance);
        var svc = Service(dir);

        // a custom template that carries one asset file
        var template = new TemplateEntry
        {
            Info = new TemplateInfo { Id = "with-asset", Name = "With asset" },
            Project = ProjectFactory.CreateNew("{ProductName}"),
            Files = new Dictionary<string, byte[]> { ["assets/images/logo.png"] = new byte[] { 1, 2, 3 } }
        };
        template.Project.Assets.Add(new AssetReference
        {
            Id = "logo", Path = "assets/images/logo.png", Category = AssetCategory.Image,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(new byte[] { 1, 2, 3 })).ToLowerInvariant()
        });

        var (project, files) = svc.Prepare(template, "Made");
        await projects.CreateAsync(dir.Path, "Made", project, files);

        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Combine(dir.Path, "Made", "assets", "images", "logo.png")));
        var report = await projects.ValidateAsync();
        Assert.False(report.HasErrors, string.Join("; ", report.Issues));
    }

    [Fact]
    public async Task CreateAsync_RejectsFilesOutsideAssets()
    {
        using var dir = new TempDir();
        var projects = new ProjectService(new RecentProjectsStore(Path.Combine(dir.Path, "r.json")), NullLogger<ProjectService>.Instance);
        var files = new Dictionary<string, byte[]> { ["../evil.txt"] = new byte[] { 1 } };
        await Assert.ThrowsAsync<ProjectException>(() => projects.CreateAsync(dir.Path, "Bad", null, files));
        Assert.False(File.Exists(Path.Combine(dir.Path, "evil.txt")));
    }

    [Fact]
    public async Task SaveFromProject_ListExportImportDelete()
    {
        using var dir = new TempDir();
        using var other = new TempDir();
        var projects = new ProjectService(new RecentProjectsStore(Path.Combine(dir.Path, "r.json")), NullLogger<ProjectService>.Instance);
        await projects.CreateAsync(dir.Path, "Source");
        var assetPath = Path.Combine(dir.Path, "Source", "assets", "note.txt");
        await File.WriteAllTextAsync(assetPath, "note");
        projects.Current!.Assets.Add(new AssetReference
        { Id = "note", Path = "assets/note.txt", Category = AssetCategory.Document, Sha256 = FileHashing.ComputeSha256(assetPath) });
        projects.Current.Application.Publisher = "Acme";

        var svc = Service(dir);
        var saved = svc.SaveFromProject(projects.Current, Path.Combine(dir.Path, "Source"), "My Template", "desc", "Custom");
        Assert.Equal("my-template", saved.Info.Id);
        Assert.Equal("{ProductName}", saved.Project.Application.ProductName);
        Assert.Equal(@"%ProgramFiles%\{ProductName}", saved.Project.Installation.DefaultInstallDirectory);
        Assert.Contains(svc.List(), t => t.Info.Id == "my-template" && !t.IsBuiltIn && t.Files.Count == 1);
        Assert.Throws<TemplateException>(() => svc.SaveFromProject(projects.Current, Path.Combine(dir.Path, "Source"), "My Template", "", ""));
        Assert.Throws<TemplateException>(() => svc.SaveFromProject(projects.Current, Path.Combine(dir.Path, "Source"), "Minimal", "", ""));

        var exported = Path.Combine(other.Path, "t" + TemplateSerializer.FileExtension);
        svc.Export(saved, exported);
        var imported = Service(other).Import(exported);
        Assert.Equal("my-template", imported.Info.Id);
        Assert.Equal("Acme", imported.Project.Application.Publisher);
        Assert.Equal("note"u8.ToArray(), imported.Files["assets/note.txt"]);

        svc.Delete("my-template");
        Assert.DoesNotContain(svc.List(), t => t.Info.Id == "my-template");
        Assert.Throws<TemplateException>(() => svc.Delete("minimal"));
    }

    [Fact]
    public void Import_RejectsTraversalMissingFilesAndBadChecksums()
    {
        var baseTemplate = new TemplateEntry
        {
            Info = new TemplateInfo { Id = "x", Name = "X" },
            Project = ProjectFactory.CreateNew("{ProductName}"),
            Files = new Dictionary<string, byte[]> { ["assets/a.txt"] = new byte[] { 1 } }
        };
        baseTemplate.Project.Assets.Add(new AssetReference { Id = "a", Path = "assets/a.txt" });
        var good = TemplateSerializer.Serialize(baseTemplate);
        Assert.NotNull(TemplateSerializer.Deserialize(good));

        // traversal in an embedded file
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize(good.Replace("\"assets/a.txt\"", "\"../evil.txt\"")));

        // asset listed without file
        var missing = new TemplateEntry { Info = baseTemplate.Info, Project = baseTemplate.Project, Files = new Dictionary<string, byte[]>() };
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize(TemplateSerializer.Serialize(missing)));

        // checksum mismatch
        baseTemplate.Project.Assets[0].Sha256 = new string('0', 64);
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize(TemplateSerializer.Serialize(baseTemplate)));

        // wrong format / invalid json / bad id
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize("{}"));
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize("nope"));
        Assert.Throws<TemplateException>(() => TemplateSerializer.Deserialize(good.Replace("\"id\": \"x\"", "\"id\": \"Bad Id\"")));
    }

    [Fact]
    public void Import_OfBuiltInId_IsRenamed()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "m.paictemplate");
        File.WriteAllText(file, TemplateSerializer.Serialize(BuiltInTemplates.All()[0]));
        var imported = Service(dir).Import(file);
        Assert.Equal("minimal-imported", imported.Info.Id);
        Assert.False(imported.IsBuiltIn);
    }
}
