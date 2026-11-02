using System.Security.Cryptography;
using System.Text;
using PAIC.Controls;
using PAIC.Core.IO;
using PAIC.InstallerEngine;
using PAIC.InstallerEngine.Virtual;
using PAIC.ProjectSystem.Model;
using Xunit;
using static PAIC.Tests.EngineFixture;

namespace PAIC.Tests;

public class InstallTests
{
    [Fact]
    public async Task Install_CopiesFiles_CreatesShortcut_RegistersAndWritesState()
    {
        using var f = new EngineFixture();
        var result = await f.Install();

        Assert.True(result.Success, result.Error);
        Assert.Equal("app v1", File.ReadAllText(f.Path1("app.exe")));
        Assert.Equal("core v1", File.ReadAllText(f.Path1("lib/core.dll")));
        Assert.False(File.Exists(f.Path1("docs/readme.txt")), "optional component must not be installed by default");
        Assert.True(File.Exists(f.ShortcutPath));

        var state = f.Engine.LoadState(f.InstallDir)!;
        Assert.Equal("1.0.0", state.Version);
        Assert.Equal(new[] { "app" }, state.Components);
        Assert.Contains(state.Items, i => i.Kind == "copyFiles" && i.Get("path") == f.Path1("app.exe"));
        Assert.Equal("1.0.0", f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "DisplayVersion")!.Data);
        Assert.Equal(f.InstallDir, f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "InstallLocation")!.Data);
        Assert.Contains("uninstall", f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "UninstallString")!.Data);
        Assert.True(File.Exists(Path.Combine(f.StateDir, "uninstall.exe")));

        // transaction bookkeeping is gone after a successful commit
        Assert.False(File.Exists(Path.Combine(f.StateDir, "journal.jsonl")));
        Assert.False(Directory.Exists(Path.Combine(f.StateDir, "backup")));
        Assert.NotEmpty(HistoryLog.Read(f.Vm.Paths.LogDirectory(false, f.ProductKey)));
    }

    [Fact]
    public async Task Components_DefaultToRequiredAndDefaultSelected_AndAcceptSelection()
    {
        using var f = new EngineFixture();
        var result = await f.Install(components: new[] { "docs" });
        Assert.True(result.Success, result.Error);
        Assert.Equal("docs", File.ReadAllText(f.Path1("docs/readme.txt")));
        Assert.Equal(new[] { "app", "docs" }, f.Engine.LoadState(f.InstallDir)!.Components);
    }

    [Fact]
    public async Task UnknownComponent_IsRejected_WithoutChangingAnything()
    {
        using var f = new EngineFixture();
        var result = await f.Install(components: new[] { "nope" });
        Assert.False(result.Success);
        Assert.Contains("Unknown component", result.Error);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task InstallingTheSameVersionAgain_DoesNothing()
    {
        using var f = new EngineFixture();
        await f.Install();
        var again = await f.Install();
        Assert.True(again.Success);
        Assert.True(again.AlreadyInstalled);
    }

    [Fact]
    public async Task Failure_RollsBackEverything_IncludingCreatedFolders()
    {
        using var f = new EngineFixture(p => p.Operations.Add(FailingCommand()));
        f.MakeCommandsFail();
        var result = await f.Install();

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Contains("exit code 1", result.Error);
        Assert.False(Directory.Exists(f.InstallDir));
        Assert.False(File.Exists(f.ShortcutPath));
        Assert.Null(f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "DisplayName"));
        Assert.Null(f.Engine.FindInstallation(f.Project));
    }

    [Fact]
    public async Task Cancellation_RollsBack()
    {
        using var cts = new CancellationTokenSource();
        using var f = new EngineFixture(p =>
        {
            p.Operations.Insert(1, Op("trigger", OperationKinds.RunCommand, "", ("command", "trigger.exe")));
        });
        f.Vm.ProcessHandler = _ => { cts.Cancel(); return new ProcessResult(0, "", false); };

        var result = await f.Install(ct: cts.Token);
        Assert.True(result.Cancelled);
        Assert.True(result.RolledBack);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task Upgrade_ReplacesFiles_RemovesFilesThatAreNoLongerShipped()
    {
        using var f = new EngineFixture();
        await f.Install();
        Assert.True(File.Exists(f.Path1("old.txt")));

        f.Project.Application.Version = "1.1.0";
        f.Files["assets/app/app.exe"] = Encoding.UTF8.GetBytes("app v2");
        f.Files.Remove("assets/app/old.txt");
        var result = await f.Install();

        Assert.True(result.Success, result.Error);
        Assert.Equal("upgrade", result.Operation);
        Assert.Equal("app v2", File.ReadAllText(f.Path1("app.exe")));
        Assert.Equal("core v1", File.ReadAllText(f.Path1("lib/core.dll")));
        Assert.False(File.Exists(f.Path1("old.txt")));
        Assert.Equal("1.1.0", f.Engine.LoadState(f.InstallDir)!.Version);
        Assert.Equal("1.1.0", f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "DisplayVersion")!.Data);

        var uninstall = await f.Uninstall();
        Assert.True(uninstall.Success, uninstall.Error);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task FailedUpgrade_RestoresThePreviousVersion()
    {
        using var f = new EngineFixture();
        await f.Install();

        f.Project.Application.Version = "1.1.0";
        f.Files["assets/app/app.exe"] = Encoding.UTF8.GetBytes("app v2");
        f.Project.Operations.Add(FailingCommand());
        f.MakeCommandsFail();
        var result = await f.Install();

        Assert.False(result.Success);
        Assert.True(result.RolledBack);
        Assert.Equal("app v1", File.ReadAllText(f.Path1("app.exe")));
        Assert.True(File.Exists(f.Path1("old.txt")));
        Assert.Equal("1.0.0", f.Engine.LoadState(f.InstallDir)!.Version);
        Assert.Equal("1.0.0", f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "DisplayVersion")!.Data);
    }

    [Fact]
    public async Task Downgrade_IsRefusedUnlessAllowed()
    {
        using var f = new EngineFixture(p => p.Application.Version = "2.0.0");
        await f.Install();
        f.Project.Application.Version = "1.0.0";
        var refused = await f.Install();
        Assert.False(refused.Success);
        Assert.Contains("older version", refused.Error);

        var allowed = await f.Engine.InstallAsync(f.Request(allowDowngrade: true));
        Assert.True(allowed.Success, allowed.Error);
        Assert.Equal("1.0.0", f.Engine.LoadState(f.InstallDir)!.Version);
    }

    [Fact]
    public async Task Repair_RestoresMissingAndModifiedFiles()
    {
        using var f = new EngineFixture();
        await f.Install();
        File.Delete(f.Path1("app.exe"));
        File.WriteAllText(f.Path1("lib/core.dll"), "tampered");
        Assert.False((await f.Engine.VerifyAsync(f.InstallDir)).IsHealthy);

        var result = await f.Install(InstallMode.Repair);
        Assert.True(result.Success, result.Error);
        Assert.Equal("app v1", File.ReadAllText(f.Path1("app.exe")));
        Assert.Equal("core v1", File.ReadAllText(f.Path1("lib/core.dll")));
        Assert.True((await f.Engine.VerifyAsync(f.InstallDir)).IsHealthy);
    }

    [Fact]
    public async Task RepairOrModify_WhenNotInstalled_Fail()
    {
        using var f = new EngineFixture();
        var repair = await f.Install(InstallMode.Repair);
        Assert.False(repair.Success);
        Assert.Contains("not installed", repair.Error);
        Assert.False((await f.Install(InstallMode.Modify)).Success);
    }

    [Fact]
    public async Task Modify_AddsAndRemovesComponents()
    {
        using var f = new EngineFixture();
        await f.Install();

        var add = await f.Install(InstallMode.Modify, new[] { "docs" });
        Assert.True(add.Success, add.Error);
        Assert.Equal("docs", File.ReadAllText(f.Path1("docs/readme.txt")));
        Assert.Equal(new[] { "app", "docs" }, f.Engine.LoadState(f.InstallDir)!.Components);
        Assert.True(File.Exists(f.Path1("app.exe")), "existing components stay untouched");

        var remove = await f.Install(InstallMode.Modify, Array.Empty<string>());
        Assert.True(remove.Success, remove.Error);
        Assert.False(File.Exists(f.Path1("docs/readme.txt")));
        Assert.False(Directory.Exists(f.Path1("docs")));
        Assert.True(File.Exists(f.Path1("app.exe")));
        Assert.Equal(new[] { "app" }, f.Engine.LoadState(f.InstallDir)!.Components);
    }

    [Fact]
    public async Task OperationConditions_DecideWhetherAnOperationRuns()
    {
        using var f = new EngineFixture(p =>
        {
            p.Operations.RemoveAll(o => o.Id == "shortcut");
            var op = Op("shortcut", OperationKinds.CreateShortcut, "app", ("location", "startMenu"), ("name", "Demo"), ("target", @"{InstallDir}\app.exe"));
            op.Condition = new ConditionDefinition { Variable = "WantShortcut", Operator = ConditionOperator.IsTrue };
            p.Operations.Add(op);
        });
        Assert.True((await f.Install()).Success);
        Assert.False(File.Exists(f.ShortcutPath));
        await f.Uninstall();

        var withVar = await f.Engine.InstallAsync(f.Request(variables: new Dictionary<string, string> { ["WantShortcut"] = "true" }));
        Assert.True(withVar.Success, withVar.Error);
        Assert.True(File.Exists(f.ShortcutPath));
    }

    [Fact]
    public async Task OnlyOneSetupPerProductMayRun()
    {
        using var f = new EngineFixture();
        f.Vm.HeldLocks.Add(f.ProductKey);
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Contains("already running", result.Error);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task Elevation_IsRequiredForPerMachineInstalls_AndNothingChangesWithoutIt()
    {
        using var f = new EngineFixture();
        f.Vm.Elevated = false;
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.True(result.ElevationRequired);
        Assert.False(Directory.Exists(f.InstallDir));

        var perUser = new EngineFixture(p => { p.Installation.PerUser = true; p.Installation.DefaultInstallDirectory = @"%LocalAppData%\Demo"; });
        perUser.Vm.Elevated = false;
        var ok = await perUser.Install();
        Assert.True(ok.Success, ok.Error);
        Assert.Equal("1.0.0", perUser.Vm.Services.Registry.GetValue("HKCU", perUser.ArpKey, "DisplayVersion")!.Data);
        perUser.Dispose();
    }
}

public class UninstallTests
{
    [Fact]
    public async Task Uninstall_RemovesEverythingTheInstallerAdded_AndNothingElse()
    {
        using var f = new EngineFixture();
        await f.Install();
        var mine = f.Path1("notes.txt");
        File.WriteAllText(mine, "user file");

        var result = await f.Uninstall();
        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(f.Path1("app.exe")));
        Assert.False(Directory.Exists(f.Path1("lib")));
        Assert.False(File.Exists(f.ShortcutPath));
        Assert.False(Directory.Exists(f.StateDir));
        Assert.Equal("user file", File.ReadAllText(mine));
        Assert.True(Directory.Exists(f.InstallDir), "a folder holding user files is never removed");
        Assert.Null(f.Vm.Services.Registry.GetValue("HKLM", f.ArpKey, "DisplayName"));
        Assert.False(f.Vm.Services.Registry.KeyExists("HKLM", f.ArpKey));
        Assert.Contains(HistoryLog.Read(f.Vm.Paths.LogDirectory(false, f.ProductKey)), h => h.Action == "uninstall" && h.Result == "success");
    }

    [Fact]
    public async Task Uninstall_RemovesTheInstallFolderWhenItIsEmpty()
    {
        using var f = new EngineFixture();
        await f.Install();
        Assert.True((await f.Uninstall()).Success);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task UserData_IsKeptUnlessRemovalIsRequested()
    {
        Action<PaicProject> add = p => p.Operations.Add(Copy("copy-data", "app", "assets/data/**", @"{InstallDir}\data", ("userData", "true")));

        using var keep = new EngineFixture(add);
        keep.Files["assets/data/settings.ini"] = Encoding.UTF8.GetBytes("settings");
        await keep.Install();
        var kept = await keep.Uninstall();
        Assert.True(kept.Success);
        Assert.Equal("settings", File.ReadAllText(keep.Path1("data/settings.ini")));
        Assert.Contains(kept.Warnings, w => w.Contains("user data"));

        using var remove = new EngineFixture(add);
        remove.Files["assets/data/settings.ini"] = Encoding.UTF8.GetBytes("settings");
        await remove.Install();
        Assert.True((await remove.Uninstall(removeUserData: true)).Success);
        Assert.False(Directory.Exists(remove.InstallDir));
    }

    [Fact]
    public async Task FilesModifiedAfterInstall_AreKeptUnlessRemovalIsRequested()
    {
        using var f = new EngineFixture();
        await f.Install();
        File.WriteAllText(f.Path1("lib/core.dll"), "edited by the user");

        var result = await f.Uninstall();
        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("modified"));
        Assert.Equal("edited by the user", File.ReadAllText(f.Path1("lib/core.dll")));
        Assert.False(File.Exists(f.Path1("app.exe")));
    }

    [Fact]
    public async Task RunningUninstaller_IsDeferred_NotDeleted()
    {
        using var f = new EngineFixture();
        await f.Install();
        var exe = Path.Combine(f.StateDir, "uninstall.exe");
        var result = await f.Uninstall(currentExecutable: exe);
        Assert.True(result.Success);
        Assert.Contains(exe, result.DeferredDeletes);
        Assert.Contains(exe, f.Vm.ScheduledDeletions);
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task Uninstall_WithoutInstallation_Fails()
    {
        using var f = new EngineFixture();
        var result = await f.Uninstall();
        Assert.False(result.Success);
        Assert.Contains("No installation", result.Error);
    }

    [Fact]
    public async Task Uninstall_NeedsElevation_ForPerMachineInstalls()
    {
        using var f = new EngineFixture();
        await f.Install();
        f.Vm.Elevated = false;
        var result = await f.Uninstall();
        Assert.True(result.ElevationRequired);
        Assert.True(File.Exists(f.Path1("app.exe")));
    }
}

public class SystemOperationTests
{
    private static EngineFixture WithSystemOperations() => new(p =>
    {
        p.Operations.Add(Op("data-dir", OperationKinds.CreateDirectory, "app", ("path", @"{InstallDir}\data")));
        p.Operations.Add(Op("reg", OperationKinds.WriteRegistry, "app", ("root", "HKCU"), ("key", @"Software\Demo"), ("name", "Path"), ("value", "{InstallDir}")));
        p.Operations.Add(Op("path", OperationKinds.SetEnvironmentVariable, "app", ("name", "PATH"), ("value", @"{InstallDir}\bin"), ("action", "append"), ("scope", "user")));
        p.Operations.Add(Op("home", OperationKinds.SetEnvironmentVariable, "app", ("name", "DEMO_HOME"), ("value", "{InstallDir}"), ("scope", "user")));
        p.Operations.Add(Op("svc", OperationKinds.InstallService, "app", ("name", "DemoSvc"), ("binaryPath", @"{InstallDir}\svc.exe")));
        p.Operations.Add(Op("perm", OperationKinds.SetPermissions, "app", ("path", @"{InstallDir}\data"), ("principal", @"BUILTIN\Users"), ("rights", "modify")));
    });

    [Fact]
    public async Task Registry_Environment_Service_Permissions_AreAppliedAndUndone()
    {
        using var f = WithSystemOperations();
        f.Vm.Services.Environment.Set("user", "PATH", @"C:\Existing");
        var result = await f.Install();
        Assert.True(result.Success, result.Error);

        var reg = f.Vm.Services.Registry;
        Assert.Equal(f.InstallDir, reg.GetValue("HKCU", @"Software\Demo", "Path")!.Data);
        Assert.Equal(@"C:\Existing;" + f.InstallDir + @"\bin", f.Vm.Services.Environment.Get("user", "PATH"));
        Assert.Equal(f.InstallDir, f.Vm.Services.Environment.Get("user", "DEMO_HOME"));
        Assert.True(f.Vm.Services.Services.Get("DemoSvc")!.Running);
        Assert.Single(f.Vm.PermissionRules);

        Assert.True((await f.Uninstall()).Success);
        Assert.Null(reg.GetValue("HKCU", @"Software\Demo", "Path"));
        Assert.False(reg.KeyExists("HKCU", @"Software\Demo"));
        Assert.Equal(@"C:\Existing", f.Vm.Services.Environment.Get("user", "PATH"));
        Assert.Null(f.Vm.Services.Environment.Get("user", "DEMO_HOME"));
        Assert.Null(f.Vm.Services.Services.Get("DemoSvc"));
        Assert.Empty(f.Vm.PermissionRules);
    }

    [Fact]
    public async Task Rollback_RestoresPreviousRegistryAndEnvironmentValues()
    {
        using var f = new EngineFixture(p =>
        {
            p.Operations.Add(Op("reg", OperationKinds.WriteRegistry, "app", ("root", "HKCU"), ("key", @"Software\Demo"), ("name", "Path"), ("value", "new")));
            p.Operations.Add(Op("home", OperationKinds.SetEnvironmentVariable, "app", ("name", "DEMO_HOME"), ("value", "new"), ("scope", "user")));
            p.Operations.Add(FailingCommand());
        });
        f.Vm.Services.Registry.SetValue("HKCU", @"Software\Demo", "Path", new RegistryValue("string", "old"));
        f.Vm.Services.Environment.Set("user", "DEMO_HOME", "old");
        f.MakeCommandsFail();

        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Equal("old", f.Vm.Services.Registry.GetValue("HKCU", @"Software\Demo", "Path")!.Data);
        Assert.Equal("old", f.Vm.Services.Environment.Get("user", "DEMO_HOME"));
    }

    [Fact]
    public async Task EnvironmentEntry_AlreadyPresent_IsNotRemovedByUninstall()
    {
        using var f = WithSystemOperations();
        f.Vm.Services.Environment.Set("user", "PATH", @"C:\Existing;" + f.InstallDir + @"\bin");
        Assert.True((await f.Install()).Success);
        Assert.True((await f.Uninstall()).Success);
        Assert.Equal(@"C:\Existing;" + f.InstallDir + @"\bin", f.Vm.Services.Environment.Get("user", "PATH"));
    }

    [Fact]
    public async Task ExistingForeignService_BlocksTheInstall_AndIsLeftAlone()
    {
        using var f = WithSystemOperations();
        f.Vm.Services.Services.Create(new ServiceSpec("DemoSvc", "x", @"C:\other.exe", "", "auto", "LocalSystem", ""));
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Contains("already exists", result.Error);
        Assert.NotNull(f.Vm.Services.Services.Get("DemoSvc"));
    }

    [Fact]
    public async Task RunCommand_UninstallCommandRunsOnRollbackAndUninstall()
    {
        using var f = new EngineFixture(p =>
            p.Operations.Add(Op("setup", OperationKinds.RunCommand, "app", ("command", "setup.exe"), ("arguments", "/i {InstallDir}"), ("uninstallCommand", "setup.exe"), ("uninstallArguments", "/u"))));
        Assert.True((await f.Install()).Success);
        Assert.Contains(f.Vm.ProcessCalls, c => c.FileName == "setup.exe" && c.Arguments == "/i " + f.InstallDir);
        f.Vm.ProcessCalls.Clear();
        Assert.True((await f.Uninstall()).Success);
        Assert.Contains(f.Vm.ProcessCalls, c => c.FileName == "setup.exe" && c.Arguments == "/u");
    }

    [Fact]
    public async Task RunCommand_ForUninstallOnly_DoesNotRunDuringInstall()
    {
        using var f = new EngineFixture(p => p.Operations.Add(Op("cleanup", OperationKinds.RunCommand, "app", ("command", "cleanup.exe"), ("runOn", "uninstall"))));
        await f.Install();
        Assert.DoesNotContain(f.Vm.ProcessCalls, c => c.FileName == "cleanup.exe");
        await f.Uninstall();
        Assert.Contains(f.Vm.ProcessCalls, c => c.FileName == "cleanup.exe");
    }

    [Fact]
    public async Task RunCommand_AcceptedExitCodes_AreConfigurable()
    {
        using var f = new EngineFixture(p => p.Operations.Add(Op("setup", OperationKinds.RunCommand, "app", ("command", "setup.exe"), ("successCodes", "0, 3010"))));
        f.Vm.ProcessHandler = _ => new ProcessResult(3010, "", false);
        Assert.True((await f.Install()).Success);
        using var g = new EngineFixture(p => p.Operations.Add(Op("setup", OperationKinds.RunCommand, "app", ("command", "setup.exe"))));
        g.Vm.ProcessHandler = _ => new ProcessResult(3010, "", false);
        Assert.False((await g.Install()).Success);
    }
}

public class SafetyTests
{
    [Fact]
    public async Task Destinations_OutsideTheInstallFolder_NeedExplicitPermission()
    {
        using var f = new EngineFixture();
        f.Project.Operations.Add(Copy("outside", "app", "assets/app/**", Path.Combine(f.Vm.Paths.Folder("ProgramData"), "Other")));
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3101" && i.Message.Contains("outside the install folder"));
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.False(Directory.Exists(f.InstallDir));

        f.Project.Operations.RemoveAll(o => o.Id == "outside");
        f.Project.Operations.Add(Copy("outside", "app", "assets/app/**", Path.Combine(f.Vm.Paths.Folder("ProgramData"), "Other"), ("allowOutside", "true")));
        Assert.True((await f.Install()).Success);
        Assert.True(File.Exists(Path.Combine(f.Vm.Paths.Folder("ProgramData"), "Other", "app.exe")));
        await f.Uninstall();
        Assert.False(File.Exists(Path.Combine(f.Vm.Paths.Folder("ProgramData"), "Other", "app.exe")));
    }

    [Fact]
    public async Task ProtectedFolders_AreNeverWrittenTo()
    {
        using var f = new EngineFixture();
        f.Project.Operations.Add(Copy("system", "app", "assets/app/**", Path.Combine(f.Vm.Paths.Folder("SystemRoot"), "x"), ("allowOutside", "true")));
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Contains("protected", result.Error);
        Assert.False(Directory.Exists(Path.Combine(f.Vm.Paths.Folder("SystemRoot"), "x")));
    }

    [Fact]
    public async Task DeleteFiles_IsConfinedToTheInstallFolder()
    {
        using var f = new EngineFixture();
        f.Project.Operations.Add(Op("del", OperationKinds.DeleteFiles, "app", ("path", Path.Combine(f.Vm.Paths.Folder("ProgramData"), "Other", "*.txt"))));
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3101");
    }

    [Fact]
    public async Task DeleteFiles_BacksUpAndRestoresOnRollback_ButNeverTouchesInstallerData()
    {
        using var f = new EngineFixture(p =>
        {
            p.Operations.Add(Op("del", OperationKinds.DeleteFiles, "app", ("path", @"{InstallDir}\*.tmp")));
            p.Operations.Add(FailingCommand());
        });
        Directory.CreateDirectory(f.InstallDir);
        File.WriteAllText(f.Path1("junk.tmp"), "junk");
        f.MakeCommandsFail();
        var result = await f.Install();
        Assert.False(result.Success);
        Assert.Equal("junk", File.ReadAllText(f.Path1("junk.tmp")));
    }

    [Fact]
    public async Task UnknownVariables_AreReportedBeforeAnythingChanges()
    {
        using var f = new EngineFixture(p => p.Operations.Add(Copy("bad", "app", "assets/app/**", @"{InstallDir}\{Nope}")));
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3101" && i.Message.Contains("Nope"));
    }

    [Fact]
    public async Task TamperedPayload_FailsIntegrityCheck_AndRollsBack()
    {
        using var f = new EngineFixture();
        var result = await f.Engine.InstallAsync(f.Request(payload: new TamperedPayload(f.Payload)));
        Assert.False(result.Success);
        Assert.Contains("Integrity check failed", result.Error);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task ExistingFiles_AreBackedUp_RestoredOnRollback_AndKeptWhenOverwriteIsNever()
    {
        using var failing = new EngineFixture(p => p.Operations.Add(FailingCommand()));
        Directory.CreateDirectory(failing.InstallDir);
        File.WriteAllText(failing.Path1("app.exe"), "mine");
        failing.MakeCommandsFail();
        Assert.False((await failing.Install()).Success);
        Assert.Equal("mine", File.ReadAllText(failing.Path1("app.exe")));
        Assert.True(Directory.Exists(failing.InstallDir), "a pre-existing folder is not removed");

        using var never = new EngineFixture(p => { p.Operations.RemoveAll(o => o.Id == "copy-app"); p.Operations.Insert(0, Copy("copy-app", "app", "assets/app/**", "{InstallDir}", ("overwrite", "never"))); });
        Directory.CreateDirectory(never.InstallDir);
        File.WriteAllText(never.Path1("app.exe"), "mine");
        Assert.True((await never.Install()).Success);
        Assert.Equal("mine", File.ReadAllText(never.Path1("app.exe")));
        await never.Uninstall();
        Assert.Equal("mine", File.ReadAllText(never.Path1("app.exe")));
    }

    [Fact]
    public async Task OptionalCopy_WithNoMatches_IsSkipped_ButRequiredCopyFails()
    {
        using var optional = new EngineFixture(p => p.Operations.Add(Copy("extra", "app", "assets/extra/**", "{InstallDir}", ("optional", "true"))));
        Assert.True((await optional.Install()).Success);

        using var required = new EngineFixture(p => p.Operations.Add(Copy("extra", "app", "assets/extra/**", "{InstallDir}")));
        var result = await required.Install();
        Assert.False(result.Success);
        Assert.Contains("No payload file matches", result.Error);
    }
}

public class RecoveryTests
{
    [Fact]
    public async Task InterruptedTransaction_IsRolledBack()
    {
        using var f = new EngineFixture();
        Directory.CreateDirectory(f.StateDir);
        File.WriteAllText(f.Path1("partial.txt"), "half installed");
        var journal = new TransactionJournal(TransactionJournal.PathFor(f.StateDir));
        journal.Begin(new JournalHeader("tx", "Install", "1.0.0", DateTimeOffset.UtcNow, f.InstallDir, null));
        journal.Append(new UndoRecord { Kind = "createDirectory", Key = f.InstallDir, Data = { ["path"] = f.InstallDir } });
        journal.Append(new UndoRecord { Kind = "createDirectory", Key = f.StateDir, Data = { ["path"] = f.StateDir } });
        journal.Append(new UndoRecord { Kind = "copyFiles", Key = f.Path1("partial.txt"), Data = { ["path"] = f.Path1("partial.txt"), ["adopted"] = "false" } });

        var result = await f.Engine.RecoverAsync(f.InstallDir);
        Assert.True(result.Found);
        Assert.True(result.RolledBack);
        Assert.False(Directory.Exists(f.InstallDir));
    }

    [Fact]
    public async Task CommittedJournal_IsFinalized()
    {
        using var f = new EngineFixture();
        Directory.CreateDirectory(f.StateDir);
        var journal = new TransactionJournal(TransactionJournal.PathFor(f.StateDir));
        journal.Begin(new JournalHeader("tx", "Install", "1.0.0", DateTimeOffset.UtcNow, f.InstallDir, null));
        journal.Commit(new InstallState { ProductId = f.ProductKey, ProductName = "Demo", Version = "1.0.0", InstallDir = f.InstallDir });

        var result = await f.Engine.RecoverAsync(f.InstallDir);
        Assert.True(result.Finalized);
        Assert.Equal("1.0.0", f.Engine.LoadState(f.InstallDir)!.Version);
        Assert.False(File.Exists(TransactionJournal.PathFor(f.StateDir)));
    }

    [Fact]
    public void Journal_IgnoresATruncatedLastLine()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "journal.jsonl");
        var journal = new TransactionJournal(path);
        journal.Begin(new JournalHeader("tx", "Install", "1.0.0", DateTimeOffset.UtcNow, dir.Path, null));
        journal.Append(new UndoRecord { Kind = "copyFiles", Key = "a" });
        File.AppendAllText(path, "{\"type\":\"undo\",\"record\":{\"Kind\":\"cop");
        var contents = TransactionJournal.Read(path)!;
        Assert.Single(contents.Records);
        Assert.True(contents.IsOpen);
    }

    [Fact]
    public async Task InstallRecoversAnInterruptedTransactionFirst()
    {
        using var f = new EngineFixture();
        Directory.CreateDirectory(f.StateDir);
        File.WriteAllText(f.Path1("partial.txt"), "leftover");
        var journal = new TransactionJournal(TransactionJournal.PathFor(f.StateDir));
        journal.Begin(new JournalHeader("tx", "Install", "1.0.0", DateTimeOffset.UtcNow, f.InstallDir, null));
        journal.Append(new UndoRecord { Kind = "copyFiles", Key = f.Path1("partial.txt"), Data = { ["path"] = f.Path1("partial.txt"), ["adopted"] = "false" } });

        var result = await f.Install();
        Assert.True(result.Success, result.Error);
        Assert.False(File.Exists(f.Path1("partial.txt")));
        Assert.True(File.Exists(f.Path1("app.exe")));
    }
}

public class VerifyTests
{
    [Fact]
    public async Task Verify_ReportsMissingAndModifiedFiles()
    {
        using var f = new EngineFixture();
        await f.Install();
        Assert.True((await f.Engine.VerifyAsync(f.InstallDir)).IsHealthy);
        File.Delete(f.Path1("app.exe"));
        File.WriteAllText(f.Path1("lib/core.dll"), "changed");
        var report = await f.Engine.VerifyAsync(f.InstallDir);
        Assert.Contains(f.Path1("app.exe"), report.Missing);
        Assert.Contains(f.Path1("lib/core.dll"), report.Modified);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task Verify_WithoutInstallation_Throws()
    {
        using var f = new EngineFixture();
        await Assert.ThrowsAsync<EngineException>(() => f.Engine.VerifyAsync(f.InstallDir));
    }
}

public class PlanTests
{
    [Fact]
    public async Task Plan_ListsStepsComponentsAndElevation_WithoutChangingAnything()
    {
        using var f = new EngineFixture();
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.False(plan.HasErrors, string.Join("; ", plan.Issues));
        Assert.Equal(f.InstallDir, plan.InstallDir);
        Assert.Equal(new[] { "copy-app", "shortcut" }, plan.Steps.Select(s => s.OperationId));
        Assert.StartsWith("Copy 3 file(s)", plan.Steps[0].Description);
        Assert.True(plan.RequiresElevation);
        Assert.False(Directory.Exists(f.InstallDir));
        Assert.False(f.Vm.Services.Registry.KeyExists("HKLM", f.ArpKey));
    }

    [Fact]
    public async Task Plan_ReportsAlreadyInstalled_AndUpgrade()
    {
        using var f = new EngineFixture();
        await f.Install();
        Assert.Contains((await f.Engine.PlanAsync(f.Request())).Issues, i => i.Code == "PAIC3105");
        f.Project.Application.Version = "2.0.0";
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.True(plan.IsUpgrade);
        Assert.Equal("1.0.0", plan.ExistingVersion);
    }

    [Fact]
    public async Task Plan_ReportsMissingPayloadAndUnknownKinds()
    {
        using var f = new EngineFixture(p =>
        {
            p.Operations.Add(Copy("none", "app", "assets/none/**", "{InstallDir}"));
            p.Operations.Add(Op("weird", "teleport", "app"));
        });
        var plan = await f.Engine.PlanAsync(f.Request());
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3103");
        Assert.Contains(plan.Issues, i => i.Code == "PAIC3100");
    }

    [Fact]
    public async Task Plan_WarnsAboutCommandsThatCannotBeUndone()
    {
        using var f = new EngineFixture(p => p.Operations.Add(Op("run", OperationKinds.RunCommand, "app", ("command", "x.exe"))));
        Assert.Contains((await f.Engine.PlanAsync(f.Request())).Issues, i => i.Code == "PAIC3104");
    }

    [Fact]
    public async Task InstallDirectory_CannotBeADriveRootOrProtected()
    {
        using var f = new EngineFixture();
        var root = await f.Engine.InstallAsync(f.Request(installDir: Path.GetPathRoot(f.Dir.Path)));
        Assert.False(root.Success);
        var windows = await f.Engine.InstallAsync(f.Request(installDir: Path.Combine(f.Vm.Paths.Folder("SystemRoot"), "Demo")));
        Assert.False(windows.Success);
        Assert.Contains("protected", windows.Error);
    }
}

public class InstallLocationTests
{
    [Fact]
    public async Task CustomInstallFolder_IsUsed_AndExistingInstallBlocksAnotherLocation()
    {
        using var f = new EngineFixture();
        var custom = Path.Combine(f.Dir.Path, "custom");
        var result = await f.Engine.InstallAsync(f.Request(installDir: custom));
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(Path.Combine(custom, "app.exe")));
        Assert.Equal(custom, f.Engine.FindInstallation(f.Project)!.InstallDir);

        var other = await f.Engine.InstallAsync(f.Request(installDir: Path.Combine(f.Dir.Path, "elsewhere")));
        Assert.False(other.Success);
        Assert.Contains("already installed", other.Error);
    }
}
