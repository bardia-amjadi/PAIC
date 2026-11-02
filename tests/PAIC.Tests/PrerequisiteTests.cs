using System.Security.Cryptography;
using System.Text;
using PAIC.InstallerEngine;
using PAIC.ProjectSystem.Model;
using Xunit;

namespace PAIC.Tests;

public class PrerequisiteTests
{
    private const string Url = "https://example.test/dotnet-runtime.exe";
    private static readonly byte[] Installer = Encoding.UTF8.GetBytes("pretend installer");
    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static PrerequisiteDefinition Runtime(string sha = "") => new()
    {
        Id = "dotnet", Name = ".NET Desktop Runtime", MinVersion = "8.0", Detection = DetectionKind.DotnetRuntime,
        DetectionValue = "Microsoft.WindowsDesktop.App", DownloadUrl = Url, Sha256 = sha.Length > 0 ? sha : Hash(Installer), InstallArguments = "/quiet /norestart"
    };

    private static void Arm(EngineFixture f, int exitCode = 0, Version? installs = null)
    {
        f.Vm.Downloads[Url] = Installer;
        f.Vm.ProcessHandler = spec =>
        {
            if (spec.FileName.EndsWith("dotnet-runtime.exe", StringComparison.OrdinalIgnoreCase) && installs is not null)
                f.Vm.SetDetected(DetectionKind.DotnetRuntime, "", "Microsoft.WindowsDesktop.App", installs);
            return new ProcessResult(exitCode, "", false);
        };
    }

    [Fact]
    public async Task MissingPrerequisite_IsDownloaded_Verified_Installed_AndDetectedAgain()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        Arm(f, installs: new Version(8, 0, 5));
        var result = await f.Install();

        Assert.True(result.Success, result.Error);
        Assert.Single(f.Vm.DownloadCalls);
        var call = Assert.Single(f.Vm.ProcessCalls);
        Assert.Equal("/quiet /norestart", call.Arguments);
        Assert.All(result.Prerequisites, s => Assert.True(s.IsSatisfied));
        Assert.True(File.Exists(f.Path1("app.exe")));
    }

    [Fact]
    public async Task SatisfiedPrerequisite_IsNotDownloaded()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        f.Vm.SetDetected(DetectionKind.DotnetRuntime, "", "Microsoft.WindowsDesktop.App", new Version(8, 0, 1));
        Assert.True((await f.Install()).Success);
        Assert.Empty(f.Vm.DownloadCalls);
    }

    [Fact]
    public async Task DownloadThatFailsTheChecksum_IsNeverRun_AndNothingIsInstalled()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime(sha: new string('a', 64))));
        Arm(f, installs: new Version(8, 0, 5));
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.True(result.PrerequisitesFailed);
        Assert.Contains("integrity", result.Error);
        Assert.Empty(f.Vm.ProcessCalls);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task AutomaticInstallCanBeDisabled()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        Arm(f, installs: new Version(8, 0, 5));
        var result = await f.Engine.InstallAsync(f.Request(installPrerequisites: false));
        Assert.False(result.Success);
        Assert.True(result.PrerequisitesFailed);
        Assert.Empty(f.Vm.DownloadCalls);
    }

    [Fact]
    public async Task InstallerThatDoesNotFixTheProblem_FailsTheSetup()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        Arm(f, installs: null);
        var result = await f.Install();
        Assert.True(result.PrerequisitesFailed);
        Assert.Contains("still not detected", result.Error);
    }

    [Fact]
    public async Task FailingPrerequisiteInstaller_IsReported()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        Arm(f, exitCode: 1603, installs: new Version(8, 0, 5));
        var result = await f.Install();
        Assert.Contains("1603", result.Error);
    }

    [Fact]
    public async Task RebootExitCode_IsAcceptedWithANote()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        Arm(f, exitCode: 3010, installs: new Version(8, 0, 5));
        var result = await f.Install();
        Assert.True(result.Success, result.Error);
        Assert.Contains("restart", result.Prerequisites.Single().Message);
    }

    [Fact]
    public async Task VersionAboveTheMaximum_CannotBeFixed()
    {
        using var f = new EngineFixture(p =>
        {
            var def = Runtime(); def.MaxVersion = "8.9";
            p.Prerequisites.Add(def);
        });
        Arm(f, installs: new Version(8, 0, 5));
        f.Vm.SetDetected(DetectionKind.DotnetRuntime, "", "Microsoft.WindowsDesktop.App", new Version(9, 0, 0));
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Empty(f.Vm.DownloadCalls);
        Assert.Equal(PrerequisiteState.VersionTooHigh, result.Prerequisites.Single().State);
    }

    [Fact]
    public async Task OlderVersion_IsUpdated()
    {
        using var f = new EngineFixture(p => p.Prerequisites.Add(Runtime()));
        f.Vm.SetDetected(DetectionKind.DotnetRuntime, "", "Microsoft.WindowsDesktop.App", new Version(6, 0, 0));
        Arm(f, installs: new Version(8, 0, 5));
        Assert.True((await f.Install()).Success);
        Assert.Single(f.Vm.DownloadCalls);
    }

    [Fact]
    public async Task OptionalPrerequisite_OnlyWarns()
    {
        using var f = new EngineFixture(p =>
        {
            var def = Runtime(); def.Optional = true; def.DownloadUrl = ""; def.Sha256 = "";
            p.Prerequisites.Add(def);
        });
        var result = await f.Install();
        Assert.True(result.Success, result.Error);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public async Task PrerequisiteForAnotherArchitecture_IsNotApplicable()
    {
        using var f = new EngineFixture(p => { var def = Runtime(); def.Architecture = PrerequisiteArchitecture.Arm64; p.Prerequisites.Add(def); });
        var result = await f.Install();
        Assert.True(result.Success, result.Error);
        Assert.Equal(PrerequisiteState.NotApplicable, result.Prerequisites.Single().State);
    }

    [Fact]
    public async Task Prerequisites_AreInstalledInDependencyOrder_AndDependentsWaitForThem()
    {
        var a = new PrerequisiteDefinition { Id = "a", Name = "A", Detection = DetectionKind.RegistryValue, DetectionTarget = @"HKLM\SOFTWARE\A", DetectionValue = "Version", MinVersion = "1.0" };
        var b = new PrerequisiteDefinition { Id = "b", Name = "B", Detection = DetectionKind.FileVersion, DetectionTarget = @"C:\b.dll", DependsOn = { "a" } };
        Assert.Equal(new[] { "a", "b" }, PrerequisiteManager.Order(new[] { b, a }).Select(p => p.Id));

        using var f = new EngineFixture(p => { p.Prerequisites.Add(b); p.Prerequisites.Add(a); });
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.Equal(new[] { "a", "b" }, plan.Prerequisites.Select(s => s.Definition.Id));
        Assert.Equal(PrerequisiteState.DependencyMissing, plan.Prerequisites[1].State);
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3110" && i.Severity == PAIC.Core.Validation.ValidationSeverity.Error);
    }

    [Fact]
    public void DependencyCycles_AndUnknownDependencies_Throw()
    {
        var a = new PrerequisiteDefinition { Id = "a", DependsOn = { "b" } };
        var b = new PrerequisiteDefinition { Id = "b", DependsOn = { "a" } };
        Assert.Throws<EngineException>(() => PrerequisiteManager.Order(new[] { a, b }));
        Assert.Throws<EngineException>(() => PrerequisiteManager.Order(new[] { new PrerequisiteDefinition { Id = "x", DependsOn = { "ghost" } } }));
    }

    [Theory]
    [InlineData("8.0.1", 8, 0, 1)]
    [InlineData("v8.0", 8, 0, 0)]
    [InlineData("8", 8, 0, 0)]
    [InlineData("8.0.1-preview.2", 8, 0, 1)]
    [InlineData("10.0.19045.1", 10, 0, 19045)]
    public void VersionParser_AcceptsCommonFormats(string text, int major, int minor, int build)
    {
        var v = VersionParser.Parse(text)!;
        Assert.Equal((major, minor), (v.Major, v.Minor));
        if (build > 0) Assert.Equal(build, v.Build);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void VersionParser_RejectsText(string? text) => Assert.Null(VersionParser.Parse(text));
}
