using PAIC.Core.IO;
using PAIC.InstallerEngine.Cli;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Validation;
using Xunit;
using static PAIC.Tests.EngineFixture;

namespace PAIC.Tests;

public class GlobTests
{
    [Theory]
    [InlineData("assets/app/**", "assets/app/a.dll", true)]
    [InlineData("assets/app/**", "assets/app/lib/b.dll", true)]
    [InlineData("assets/app/**", "assets/other/a.dll", false)]
    [InlineData("assets/app/*", "assets/app/a.dll", true)]
    [InlineData("assets/app/*", "assets/app/lib/b.dll", false)]
    [InlineData("assets/app/*.dll", "assets/app/A.DLL", true)]
    [InlineData("assets/app/*.dll", "assets/app/a.exe", false)]
    [InlineData("assets/**/readme.txt", "assets/readme.txt", true)]
    [InlineData("assets/**/readme.txt", "assets/a/b/readme.txt", true)]
    [InlineData("assets/app/?.dll", "assets/app/a.dll", true)]
    [InlineData("assets/app/?.dll", "assets/app/ab.dll", false)]
    [InlineData("assets\\app\\x.exe", "assets/app/x.exe", true)]
    [InlineData("assets/app/x.exe", "assets/app/x.exe.bak", false)]
    public void IsMatch(string pattern, string path, bool expected) => Assert.Equal(expected, Glob.IsMatch(pattern, path));

    [Theory]
    [InlineData("assets/app/**", "assets/app")]
    [InlineData("assets/app/*.dll", "assets/app")]
    [InlineData("assets/app/x.exe", "assets/app")]
    [InlineData("assets/**/a.dll", "assets")]
    [InlineData("**", "")]
    [InlineData("x.exe", "")]
    public void BaseDirectory(string pattern, string expected) => Assert.Equal(expected, Glob.BaseDirectory(pattern));
}

public class CommandLineTests
{
    private static InstallerCommand Parse(params string[] args) => InstallerCommandLine.Parse(args);

    [Fact]
    public void NoArguments_AndHelp_ShowUsage()
    {
        Assert.Equal(InstallerCommandKind.Help, Parse().Kind);
        Assert.Equal(InstallerCommandKind.Help, Parse("--help").Kind);
        Assert.Equal(InstallerCommandKind.Help, Parse("install", "-h").Kind);
    }

    [Fact]
    public void Commands_AreRecognised()
    {
        Assert.Equal(InstallerCommandKind.Install, Parse("install").Kind);
        Assert.Equal(InstallerCommandKind.Uninstall, Parse("UNINSTALL").Kind);
        Assert.Equal(InstallerCommandKind.Repair, Parse("repair").Kind);
        Assert.Equal(InstallerCommandKind.Modify, Parse("modify").Kind);
        Assert.Equal(InstallerCommandKind.Version, Parse("version").Kind);
        Assert.Equal(InstallerCommandKind.Status, Parse("status").Kind);
        Assert.Equal(InstallerCommandKind.Plan, Parse("plan").Kind);
        Assert.Equal(InstallerCommandKind.Prerequisites, Parse("prerequisites").Kind);
    }

    [Fact]
    public void Options_AcceptSpaceAndEqualsForms()
    {
        var c = Parse("install", "--silent", "--install-dir", @"C:\Apps\Demo", "--components=docs,samples", "--project", "p.paproj",
            "--set", "Name=Contoso", "--set=Mode=Fast=1", "--accept-license", "--no-prerequisites", "--sandbox", "box", "--log", "logs", "--json", "--elevate", "--dry-run", "--allow-downgrade");
        Assert.Null(c.Error);
        Assert.True(c.Silent && c.AcceptLicense && c.NoPrerequisites && c.Json && c.Elevate && c.DryRun && c.AllowDowngrade);
        Assert.Equal(@"C:\Apps\Demo", c.InstallDir);
        Assert.Equal(new[] { "docs", "samples" }, c.Components);
        Assert.Equal("p.paproj", c.ProjectPath);
        Assert.Equal("box", c.Sandbox);
        Assert.Equal("logs", c.LogDirectory);
        Assert.Equal("Contoso", c.Variables["Name"]);
        Assert.Equal("Fast=1", c.Variables["Mode"]);
    }

    [Theory]
    [InlineData("-s")]
    [InlineData("/s")]
    [InlineData("/quiet")]
    [InlineData("--quiet")]
    public void SilentAliases(string flag) => Assert.True(Parse("install", flag).Silent);

    [Fact]
    public void ComponentsAll_AndUninstallOptions()
    {
        Assert.True(Parse("install", "-c", "all").AllComponents);
        Assert.True(Parse("uninstall", "--remove-user-data").RemoveUserData);
    }

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("install", "--nope")]
    [InlineData("install", "--install-dir")]
    [InlineData("install", "--set", "novalue")]
    public void BadArguments_ReportAnError(params string[] args) => Assert.NotNull(Parse(args).Error);

    [Fact]
    public void ExitCodes_AreDistinct()
    {
        var codes = new[] { ExitCodes.Success, ExitCodes.Failure, ExitCodes.InvalidArguments, ExitCodes.Cancelled, ExitCodes.PrerequisitesFailed, ExitCodes.NotInstalled, ExitCodes.ElevationRequired };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.Equal(740, ExitCodes.ElevationRequired);
    }
}

public class OperationValidationTests
{
    private static List<PAIC.Core.Validation.ValidationIssue> Issues(PaicProject p) =>
        ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(p, null)).Issues.ToList();

    private static PaicProject WithOp(InstallOperationDefinition op)
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Operations.Add(op);
        return p;
    }

    [Fact]
    public void MissingRequiredParameters_AreErrors()
    {
        var issues = Issues(WithOp(Op("c", OperationKinds.CopyFiles, "")));
        Assert.Equal(2, issues.Count(i => i.Code == "PAIC1310"));
        Assert.Contains(Issues(WithOp(Op("s", OperationKinds.CreateShortcut, "", ("location", "startMenu")))), i => i.Code == "PAIC1310");
    }

    [Fact]
    public void InvalidValues_AreErrors_AndUnknownParametersWarn()
    {
        var issues = Issues(WithOp(Op("r", OperationKinds.WriteRegistry, "", ("root", "HKCR"), ("key", "x"), ("value", "1"), ("type", "dword"), ("bogus", "1"))));
        Assert.Contains(issues, i => i.Code == "PAIC1311" && i.Message.Contains("one of"));
        Assert.Contains(issues, i => i.Code == "PAIC1312" && i.Severity == PAIC.Core.Validation.ValidationSeverity.Warning);

        var run = Issues(WithOp(Op("x", OperationKinds.RunCommand, "", ("command", "a.exe"), ("timeoutSeconds", "soon"), ("onRepair", "maybe"))));
        Assert.Equal(2, run.Count(i => i.Code == "PAIC1311"));
    }

    [Fact]
    public void CopySource_MustBeASafeProjectPath_AndShouldMatchAssets()
    {
        Assert.Contains(Issues(WithOp(Copy("c", "", "../secrets/**", "{InstallDir}"))), i => i.Code == "PAIC1313");
        Assert.Contains(Issues(WithOp(Copy("c", "", @"C:\data\**", "{InstallDir}"))), i => i.Code == "PAIC1313");
        Assert.Contains(Issues(WithOp(Copy("c", "", "assets/app/**", "{InstallDir}"))), i => i.Code == "PAIC1314");

        var p = WithOp(Copy("c", "", "assets/app/**", "{InstallDir}"));
        p.Assets.Add(new AssetReference { Id = "a", Path = "assets/app/a.exe" });
        Assert.DoesNotContain(Issues(p), i => i.Code is "PAIC1313" or "PAIC1314");
        Assert.DoesNotContain(Issues(WithOp(Copy("c", "", "assets/none/**", "{InstallDir}", ("optional", "true")))), i => i.Code == "PAIC1314");
    }

    [Fact]
    public void OperationCondition_NeedsAVariable()
    {
        var op = Copy("c", "", "assets/a", "{InstallDir}");
        op.Condition = new PAIC.Controls.ConditionDefinition { Variable = "" };
        Assert.Contains(Issues(WithOp(op)), i => i.Code == "PAIC1315");
    }

    [Fact]
    public void Catalog_CoversEveryBuiltInKind_WithRequiredParametersAndSaneDefaults()
    {
        foreach (var kind in OperationKinds.All)
        {
            Assert.True(PAIC.ProjectSystem.Operations.OperationCatalog.TryGet(kind, out var d), kind);
            Assert.NotEmpty(d.Parameters);
            Assert.Equal(d.Parameters.Count, d.Parameters.Select(p => p.Name).Distinct().Count());
            foreach (var p in d.Parameters.Where(p => p.Kind == PAIC.ProjectSystem.Operations.OperationParameterKind.Choice))
                Assert.Contains(p.Default, p.Choices!);
        }
        Assert.True(PAIC.ProjectSystem.Operations.OperationCatalog.TryGet(OperationKinds.CopyFiles, out var copy) && copy.MsixCompatible);
        Assert.True(PAIC.ProjectSystem.Operations.OperationCatalog.TryGet(OperationKinds.InstallService, out var svc) && !svc.MsixCompatible);
    }

    [Fact]
    public void PrerequisiteRules()
    {
        var p = ProjectFactory.CreateNew("Demo");
        p.Prerequisites.Add(new PrerequisiteDefinition { Id = "a", Name = "A", DownloadUrl = "https://x.test/a.exe", Detection = DetectionKind.FileVersion, DetectionTarget = @"C:\a.dll" });
        p.Prerequisites.Add(new PrerequisiteDefinition { Id = "b", Name = "B", Sha256 = "xyz", Detection = DetectionKind.None, DependsOn = { "b", "ghost" } });
        p.Prerequisites.Add(new PrerequisiteDefinition { Id = "c", Name = "C", Detection = DetectionKind.RegistryValue, DependsOn = { "d" } });
        p.Prerequisites.Add(new PrerequisiteDefinition { Id = "d", Name = "D", Detection = DetectionKind.DotnetRuntime, DetectionValue = "X", DependsOn = { "c" } });
        var issues = Issues(p);
        Assert.Equal(2, issues.Count(i => i.Code == "PAIC1309"));          // url without checksum, malformed checksum
        Assert.Equal(2, issues.Count(i => i.Code == "PAIC1319"));          // no detection, registry without target
        Assert.Contains(issues, i => i.Code == "PAIC1317");                // unknown dependency
        Assert.True(issues.Count(i => i.Code == "PAIC1318") >= 2);         // self dependency and a cycle
    }

    [Fact]
    public void BuiltInTemplates_UseValidOperationsAndPrerequisites()
    {
        foreach (var t in PAIC.TemplateEngine.BuiltInTemplates.All())
        {
            var issues = Issues(t.Project);
            Assert.DoesNotContain(issues, i => i.Severity == PAIC.Core.Validation.ValidationSeverity.Error);
            Assert.DoesNotContain(issues, i => i.Code is "PAIC1310" or "PAIC1311");
        }
    }
}
