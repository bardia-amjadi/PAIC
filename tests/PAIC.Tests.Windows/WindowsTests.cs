using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PAIC.InstallerEngine;
using PAIC.InstallerRuntime;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using Xunit;

namespace PAIC.Tests.Windows;

/// <summary>Integration tests of the real Windows implementations. They only touch HKCU\Software\PAIC.Tests, temp folders and user variables with a unique name.</summary>
public class WindowsImplementationTests
{
    private static string TempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "paic-win-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Registry_RoundTripsAllValueKinds_AndDeletesEmptyKeys()
    {
        var reg = new WindowsRegistryService();
        var key = @"Software\PAIC.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.True(reg.CreateKey("HKCU", key));
            Assert.False(reg.CreateKey("HKCU", key));
            reg.SetValue("HKCU", key, "s", new RegistryValue("string", "text"));
            reg.SetValue("HKCU", key, "e", new RegistryValue("expandString", "%TEMP%\\x"));
            reg.SetValue("HKCU", key, "d", new RegistryValue("dword", "4294967295"));
            reg.SetValue("HKCU", key, "q", new RegistryValue("qword", "18446744073709551615"));
            reg.SetValue("HKCU", key, "m", new RegistryValue("multiString", "a\nb"));

            Assert.Equal(new RegistryValue("string", "text"), reg.GetValue("HKCU", key, "s"));
            Assert.Equal("%TEMP%\\x", reg.GetValue("HKCU", key, "e")!.Data);
            Assert.Equal("expandString", reg.GetValue("HKCU", key, "e")!.Type);
            Assert.Equal("4294967295", reg.GetValue("HKCU", key, "d")!.Data);
            Assert.Equal("18446744073709551615", reg.GetValue("HKCU", key, "q")!.Data);
            Assert.Equal("a\nb", reg.GetValue("HKCU", key, "m")!.Data);
            Assert.Null(reg.GetValue("HKCU", key, "missing"));

            Assert.False(reg.DeleteKeyIfEmpty("HKCU", key));
            foreach (var n in new[] { "s", "e", "d", "q", "m" }) Assert.True(reg.DeleteValue("HKCU", key, n));
            Assert.False(reg.DeleteValue("HKCU", key, "s"));
            Assert.True(reg.DeleteKeyIfEmpty("HKCU", key));
            Assert.False(reg.KeyExists("HKCU", key));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\PAIC.Tests", throwOnMissingSubKey: false); }
    }

    [Fact]
    public void Registry_RejectsUnknownRoots()
    {
        Assert.Throws<EngineException>(() => new WindowsRegistryService().GetValue("HKCR", "x", "y"));
    }

    [Fact]
    public void UserEnvironmentVariables_RoundTrip()
    {
        var env = new WindowsEnvironmentService();
        var name = "PAIC_TEST_" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.Null(env.Get("user", name));
            env.Set("user", name, @"C:\a;C:\b");
            Assert.Equal(@"C:\a;C:\b", env.Get("user", name));
            env.Set("user", name, null);
            Assert.Null(env.Get("user", name));
        }
        finally { env.Set("user", name, null); }
    }

    [Fact]
    public async Task ProcessRunner_ReportsExitCodes_Output_Timeouts_AndCancellation()
    {
        var runner = new WindowsProcessRunner();
        var ok = await runner.RunAsync(new ProcessSpec("cmd.exe", "/c echo hello & exit 3", null, TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(3, ok.ExitCode);
        Assert.Contains("hello", ok.Output);
        Assert.False(ok.TimedOut);

        var slow = await runner.RunAsync(new ProcessSpec("cmd.exe", "/c ping 127.0.0.1 -n 20 >nul", null, TimeSpan.FromMilliseconds(500)), CancellationToken.None);
        Assert.True(slow.TimedOut);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(new ProcessSpec("cmd.exe", "/c ping 127.0.0.1 -n 20 >nul", null, TimeSpan.FromSeconds(30)), cts.Token));

        await Assert.ThrowsAsync<EngineException>(() => runner.RunAsync(new ProcessSpec(@"C:\definitely\missing.exe", "", null, TimeSpan.FromSeconds(5)), CancellationToken.None));
    }

    [Fact]
    public void Shortcuts_AreCreatedAsLnkFiles()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "sub", "Demo.lnk");
            new WindowsShortcutService().Create(new ShortcutSpec(path, @"C:\Windows\System32\notepad.exe", "/a", dir, "", "Demo shortcut"));
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path).Length > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Permissions_AddOnce_AndRemove()
    {
        var dir = TempDir();
        try
        {
            var perms = new WindowsPermissionService();
            Assert.True(perms.AddRule(dir, @"BUILTIN\Users", "modify"));
            Assert.False(perms.AddRule(dir, @"BUILTIN\Users", "modify"));
            perms.RemoveRule(dir, @"BUILTIN\Users", "modify");
            Assert.True(perms.AddRule(dir, @"BUILTIN\Users", "modify"));
            perms.RemoveRule(dir, @"BUILTIN\Users", "modify");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Detection_FindsWindowsRegistryAndFileVersions()
    {
        var d = new WindowsDetectionService();
        Assert.NotNull(d.GetVersion(DetectionKind.WindowsVersion, "", ""));
        Assert.NotNull(d.GetVersion(DetectionKind.RegistryValue, @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber"));
        Assert.NotNull(d.GetVersion(DetectionKind.FileVersion, @"%SystemRoot%\System32\kernel32.dll", ""));
        Assert.Null(d.GetVersion(DetectionKind.FileVersion, @"C:\no\such\file.dll", ""));
        Assert.Null(d.GetVersion(DetectionKind.RegistryValue, @"HKLM\SOFTWARE\PAIC.Tests.Missing", "x"));
        Assert.Null(d.GetVersion(DetectionKind.DotnetRuntime, "..\\evil", ""));
    }

    [Fact]
    public void SystemPaths_KnowProtectedAndElevatedFolders()
    {
        var p = new WindowsPaths();
        Assert.True(p.IsProtected(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
        Assert.True(p.IsProtected(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers")));
        Assert.True(p.RequiresElevation(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Demo")));
        Assert.False(p.RequiresElevation(Path.Combine(Path.GetTempPath(), "Demo")));
        Assert.Contains("InstallerLogs", p.LogDirectory(true, "{ABC}"));
    }

    [Fact]
    public void InstallLock_IsExclusivePerProduct()
    {
        var locks = new WindowsInstallLock();
        var id = "{" + Guid.NewGuid() + "}";
        var first = locks.TryAcquire(id);
        Assert.NotNull(first);
        // a mutex is re-entrant for its owning thread, so check from another thread
        IDisposable? second = null;
        var t = new Thread(() => second = locks.TryAcquire(id));
        t.Start(); t.Join();
        Assert.Null(second);
        first!.Dispose();
        IDisposable? third = null;
        var t2 = new Thread(() => { third = locks.TryAcquire(id); third?.Dispose(); });
        t2.Start(); t2.Join();
        Assert.NotNull(third);
    }

    [Fact]
    public void ServiceManager_ReportsMissingServices()
    {
        Assert.Null(new WindowsServiceManager().Get("PAIC_DoesNotExist_" + Guid.NewGuid().ToString("N")));
    }

    [Fact]
    public async Task Downloader_RefusesNonHttps()
    {
        await Assert.ThrowsAsync<EngineException>(() => new HttpDownloader().DownloadAsync(new Uri("http://example.test/x.exe"), Path.GetTempFileName(), null, CancellationToken.None));
    }
}

/// <summary>Drives the same code path as the paic-installer command line, against a sandbox so nothing outside the temp folder is changed.</summary>
public class SandboxCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paic-e2e-" + Guid.NewGuid().ToString("N"));
    private readonly string _projectFile;
    private readonly string _sandbox;

    public SandboxCommandLineTests()
    {
        var projectDir = Path.Combine(_root, "project");
        _sandbox = Path.Combine(_root, "sandbox");
        Directory.CreateDirectory(Path.Combine(projectDir, "assets", "app"));
        var content = Encoding.UTF8.GetBytes("hello from the payload");
        File.WriteAllBytes(Path.Combine(projectDir, "assets", "app", "hello.txt"), content);

        var project = ProjectFactory.CreateNew("E2E Demo");
        project.Installation.PerUser = true;
        project.Installation.DefaultInstallDirectory = @"%LocalAppData%\E2E Demo";
        project.Assets.Add(new AssetReference { Id = "hello", Path = "assets/app/hello.txt", Sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant() });
        project.Operations.Add(new InstallOperationDefinition
        {
            Id = "copy", Kind = OperationKinds.CopyFiles, Parameters = { ["source"] = "assets/app/**", ["destination"] = "{InstallDir}" }
        });
        _projectFile = Path.Combine(projectDir, "E2E.paproj");
        File.WriteAllText(_projectFile, ProjectSerializer.Default.Serialize(project));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private string Installed => Path.Combine(_sandbox, "Users", "test", "AppData", "Local", "E2E Demo", "hello.txt");

    private async Task<(int Code, string Output)> Run(params string[] args)
    {
        var writer = new StringWriter();
        var code = await InstallerHost.RunAsync(args, writer, null, CancellationToken.None);
        return (code, writer.ToString());
    }

    [Fact]
    public async Task FullLifecycle_Plan_Install_Status_Repair_Uninstall()
    {
        var common = new[] { "--project", _projectFile, "--sandbox", _sandbox };

        var plan = await Run(new[] { "plan" }.Concat(common).ToArray());
        Assert.Equal(0, plan.Code);
        Assert.Contains("Copy 1 file(s)", plan.Output);
        Assert.False(File.Exists(Installed));

        var install = await Run(new[] { "install", "--silent", "--accept-license" }.Concat(common).ToArray());
        Assert.True(install.Code == 0, install.Output);
        Assert.Equal("hello from the payload", File.ReadAllText(Installed));

        var again = await Run(new[] { "install", "--silent", "--accept-license" }.Concat(common).ToArray());
        Assert.Equal(0, again.Code);
        Assert.Contains("already installed", again.Output);

        var status = await Run(new[] { "status", "--json" }.Concat(common).ToArray());
        Assert.Equal(0, status.Code);
        Assert.Contains("\"installed\": true", status.Output);
        Assert.Contains("\"healthy\": true", status.Output);

        File.Delete(Installed);
        Assert.Equal(1, (await Run(new[] { "status" }.Concat(common).ToArray())).Code);
        var repair = await Run(new[] { "repair", "--silent" }.Concat(common).ToArray());
        Assert.True(repair.Code == 0, repair.Output);
        Assert.True(File.Exists(Installed));

        var version = await Run(new[] { "version", "--json" }.Concat(common).ToArray());
        Assert.Contains("\"installedVersion\": \"1.0.0\"", version.Output);

        var uninstall = await Run(new[] { "uninstall", "--silent" }.Concat(common).ToArray());
        Assert.True(uninstall.Code == 0, uninstall.Output);
        Assert.False(File.Exists(Installed));
        Assert.Equal(5, (await Run(new[] { "status" }.Concat(common).ToArray())).Code);
    }

    [Fact]
    public async Task SilentInstall_NeedsExplicitLicenseAcceptance()
    {
        var result = await Run("install", "--silent", "--project", _projectFile, "--sandbox", _sandbox);
        Assert.Equal(2, result.Code);
        Assert.Contains("--accept-license", result.Output);
        Assert.False(File.Exists(Installed));
    }

    [Fact]
    public async Task DryRun_ChangesNothing()
    {
        var result = await Run("install", "--dry-run", "--accept-license", "--project", _projectFile, "--sandbox", _sandbox);
        Assert.Equal(0, result.Code);
        Assert.False(File.Exists(Installed));
    }

    [Fact]
    public async Task BadInput_ReturnsUsageErrors()
    {
        Assert.Equal(2, (await Run("frobnicate")).Code);
        Assert.Equal(2, (await Run("install", "--silent")).Code);   // no payload given
        Assert.Equal(0, (await Run("--help")).Code);
        Assert.Equal(1, (await Run("plan", "--project", Path.Combine(_root, "missing.paproj"))).Code);
    }

    [Fact]
    public async Task Uninstall_WithoutInstallation_ReportsNotInstalled()
    {
        var result = await Run("uninstall", "--silent", "--install-dir", Path.Combine(_root, "nothing"), "--sandbox", _sandbox);
        Assert.Equal(5, result.Code);
    }
}
