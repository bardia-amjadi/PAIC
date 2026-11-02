using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PAIC.Diagnostics;
using PAIC.InstallerEngine;
using PAIC.InstallerEngine.Cli;
using PAIC.InstallerEngine.Virtual;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerRuntime;

/// <summary>
/// Runs installer commands from the command line: composes the real (or sandboxed) machine, loads the payload, runs the engine
/// and turns the result into console output and an exit code.
/// </summary>
public static class InstallerHost
{
    private sealed class Session
    {
        public required InstallerCommand Command { get; init; }
        public required SystemServices System { get; init; }
        public required TextWriter Out { get; init; }
        public required TextReader? In { get; init; }
        public PaicProject? Project { get; init; }
        public string? ProjectDirectory { get; init; }
    }

    public static string ProductVersion =>
        typeof(InstallerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextReader? input, CancellationToken ct)
    {
        var cmd = InstallerCommandLine.Parse(args);
        if (cmd.Error is not null)
        {
            await output.WriteLineAsync(cmd.Error);
            await output.WriteLineAsync(InstallerCommandLine.Usage);
            return ExitCodes.InvalidArguments;
        }
        if (cmd.Kind == InstallerCommandKind.Help)
        {
            await output.WriteLineAsync(InstallerCommandLine.Usage);
            return ExitCodes.Success;
        }

        PaicProject? project = null;
        string? projectDir = null;
        try
        {
            if (cmd.ProjectPath is not null)
            {
                var full = Path.GetFullPath(cmd.ProjectPath);
                project = ProjectSerializer.Default.Deserialize(await File.ReadAllTextAsync(full, ct));
                projectDir = Path.GetDirectoryName(full);
            }
        }
        catch (Exception ex) when (ex is ProjectException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Could not load the project: {ex.Message}");
            return ExitCodes.Failure;
        }

        var system = CreateSystem(cmd);
        var session = new Session { Command = cmd, System = system, Out = output, In = input, Project = project, ProjectDirectory = projectDir };

        var logDirectory = cmd.LogDirectory ?? system.Paths.LogDirectory(project?.Installation.PerUser ?? true, project is null ? "installer" : InstallEngine.ProductKey(project));
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Information).AddProvider(new FileLoggerProvider(logDirectory)));
        var logger = factory.CreateLogger("PAIC.Installer");
        var engine = new InstallEngine(system, logger);

        try
        {
            return cmd.Kind switch
            {
                InstallerCommandKind.Version => await VersionAsync(session, engine),
                InstallerCommandKind.Status => await StatusAsync(session, engine, ct),
                InstallerCommandKind.Plan => await PlanAsync(session, engine, ct),
                InstallerCommandKind.Prerequisites => await PrerequisitesAsync(session, engine, logger, ct),
                InstallerCommandKind.Uninstall => await UninstallAsync(session, engine, args, ct),
                _ => await InstallLikeAsync(session, engine, args, ct)
            };
        }
        catch (OperationCanceledException)
        {
            await output.WriteLineAsync("Cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (PAIC.Core.UserFacingException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return ExitCodes.Failure;
        }
    }

    private static SystemServices CreateSystem(InstallerCommand cmd)
    {
        if (cmd.Sandbox is null) return WindowsSystem.Create();
        var root = Path.GetFullPath(cmd.Sandbox);
        Directory.CreateDirectory(root);
        var virtualSystem = new VirtualSystem(root, Path.Combine(root, "virtual-system.json"));
        var v = virtualSystem.Services;
        return new SystemServices
        {
            Registry = v.Registry, Environment = v.Environment, Shortcuts = v.Shortcuts, Services = v.Services, Permissions = v.Permissions,
            Processes = v.Processes, Downloader = v.Downloader, Detection = v.Detection, Paths = v.Paths,
            Uninstaller = new CopyExecutableUninstaller(), Locks = v.Locks, IsElevated = () => true
        };
    }

    private static IReadOnlyCollection<string>? Components(Session s) =>
        s.Command.AllComponents ? s.Project?.Components.Select(c => c.Id).ToList() : s.Command.Components;

    private static string? ResolveInstallDir(Session s, InstallEngine engine)
    {
        if (s.Command.InstallDir is not null) return Path.GetFullPath(s.System.Paths.ExpandEnvironment(s.Command.InstallDir));
        if (s.Project is not null && engine.FindInstallation(s.Project) is { } found) return found.InstallDir;
        // an uninstaller lives in <install folder>\.paic
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (exeDir is not null && Path.GetFileName(exeDir) == ".paic") return Path.GetDirectoryName(exeDir);
        return null;
    }

    // ------------------------------------------------------------------ commands

    private static async Task<int> VersionAsync(Session s, InstallEngine engine)
    {
        var installDir = ResolveInstallDir(s, engine);
        var state = installDir is null ? null : engine.LoadState(installDir);
        var data = new
        {
            installer = ProductVersion,
            product = s.Project?.Application.ProductName ?? state?.ProductName,
            packageVersion = s.Project?.Application.Version,
            installedVersion = state?.Version,
            installDir = state?.InstallDir
        };
        if (s.Command.Json) await s.Out.WriteLineAsync(JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            await s.Out.WriteLineAsync($"PAIC installer {data.installer}");
            if (data.product is not null) await s.Out.WriteLineAsync($"Product: {data.product}");
            if (data.packageVersion is not null) await s.Out.WriteLineAsync($"Package version: {data.packageVersion}");
            await s.Out.WriteLineAsync(data.installedVersion is null ? "Installed: no" : $"Installed version: {data.installedVersion} ({data.installDir})");
        }
        return ExitCodes.Success;
    }

    private static async Task<int> StatusAsync(Session s, InstallEngine engine, CancellationToken ct)
    {
        var installDir = ResolveInstallDir(s, engine);
        InstallState? state = null;
        if (installDir is not null)
        {
            var recovery = Directory.Exists(installDir) ? await engine.RecoverAsync(installDir, ct) : null;
            if (recovery is { Found: true }) await s.Out.WriteLineAsync("An interrupted setup was found and has been " + (recovery.RolledBack ? "rolled back." : "finalized."));
            state = engine.LoadState(installDir);
        }
        if (state is null)
        {
            if (s.Command.Json) await s.Out.WriteLineAsync(JsonSerializer.Serialize(new { installed = false }));
            else await s.Out.WriteLineAsync("Not installed.");
            return ExitCodes.NotInstalled;
        }
        var report = await engine.VerifyAsync(installDir!, ct);
        if (s.Command.Json)
            await s.Out.WriteLineAsync(JsonSerializer.Serialize(new { installed = true, version = state.Version, installDir = state.InstallDir, components = state.Components, healthy = report.IsHealthy, filesChecked = report.Checked, missing = report.Missing, modified = report.Modified }, new JsonSerializerOptions { WriteIndented = true }));
        else
        {
            await s.Out.WriteLineAsync($"{state.ProductName} {state.Version} is installed in {state.InstallDir}");
            if (state.Components.Count > 0) await s.Out.WriteLineAsync("Components: " + string.Join(", ", state.Components));
            await s.Out.WriteLineAsync($"Checked {report.Checked} item(s): {(report.IsHealthy ? "all present and unchanged." : $"{report.Missing.Count} missing, {report.Modified.Count} modified.")}");
            foreach (var m in report.Missing) await s.Out.WriteLineAsync("  missing:  " + m);
            foreach (var m in report.Modified) await s.Out.WriteLineAsync("  modified: " + m);
        }
        return report.IsHealthy ? ExitCodes.Success : ExitCodes.Failure;
    }

    private static InstallRequest? BuildRequest(Session s, InstallEngine engine, InstallMode mode)
    {
        if (s.Project is null || s.ProjectDirectory is null) return null;
        return new InstallRequest
        {
            Project = s.Project, Payload = new DirectoryPayloadProvider(s.Project, s.ProjectDirectory), InstallDir = s.Command.InstallDir is null ? null : ResolveInstallDir(s, engine),
            Components = Components(s), Variables = s.Command.Variables, Mode = mode,
            InstallPrerequisites = !s.Command.NoPrerequisites, AllowDowngrade = s.Command.AllowDowngrade
        };
    }

    private static InstallMode ModeFor(InstallerCommandKind kind) => kind switch
    {
        InstallerCommandKind.Repair => InstallMode.Repair, InstallerCommandKind.Modify => InstallMode.Modify, _ => InstallMode.Install
    };

    private static async Task<int> PlanAsync(Session s, InstallEngine engine, CancellationToken ct)
    {
        var request = BuildRequest(s, engine, InstallMode.Install);
        if (request is null) { await s.Out.WriteLineAsync("The plan command needs --project <file.paproj>."); return ExitCodes.InvalidArguments; }
        return await PrintPlanAsync(s, await engine.PlanAsync(request, ct));
    }

    private static async Task<int> PrintPlanAsync(Session s, InstallPlan plan)
    {
        await s.Out.WriteLineAsync($"Install folder: {plan.InstallDir}");
        if (plan.ExistingVersion is not null) await s.Out.WriteLineAsync($"Existing version: {plan.ExistingVersion}{(plan.IsUpgrade ? " (upgrade)" : "")}");
        if (plan.Components.Count > 0) await s.Out.WriteLineAsync("Components: " + string.Join(", ", plan.Components));
        foreach (var p in plan.Prerequisites) await s.Out.WriteLineAsync($"Prerequisite: {p.Message}");
        var n = 1;
        foreach (var step in plan.Steps) await s.Out.WriteLineAsync($"{n++,3}. {step.Description}");
        foreach (var issue in plan.Issues) await s.Out.WriteLineAsync($"{issue.Severity}: {issue.Message}");
        if (plan.RequiresElevation) await s.Out.WriteLineAsync("Needs administrator rights: " + string.Join(" ", plan.ElevationReasons));
        return plan.HasErrors ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static async Task<int> PrerequisitesAsync(Session s, InstallEngine engine, ILogger logger, CancellationToken ct)
    {
        if (s.Project is null) { await s.Out.WriteLineAsync("The prerequisites command needs --project <file.paproj>."); return ExitCodes.InvalidArguments; }
        var statuses = await new PrerequisiteManager(s.System, logger).EvaluateAsync(s.Project.Prerequisites, ct);
        foreach (var st in statuses) await s.Out.WriteLineAsync($"{(st.IsSatisfied ? "ok     " : "MISSING")} {st.Message}");
        return statuses.Any(x => !x.IsSatisfied && !x.Definition.Optional) ? ExitCodes.PrerequisitesFailed : ExitCodes.Success;
    }

    private static async Task<bool> ConfirmAsync(Session s, string question)
    {
        if (s.Command.Silent || s.In is null) return true;
        await s.Out.WriteAsync(question + " [y/N] ");
        var answer = await s.In.ReadLineAsync();
        return answer is not null && answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    private static IProgress<InstallProgress>? ProgressWriter(Session s) =>
        s.Command.Silent ? null : new Progress<InstallProgress>(p => s.Out.WriteLine($"[{p.Percent,3:0}%] {p.Message}"));

    private static async Task<int> InstallLikeAsync(Session s, InstallEngine engine, IReadOnlyList<string> args, CancellationToken ct)
    {
        var mode = ModeFor(s.Command.Kind);
        var request = BuildRequest(s, engine, mode);
        if (request is null)
        {
            await s.Out.WriteLineAsync($"The {s.Command.Kind.ToString().ToLowerInvariant()} command needs --project <file.paproj> (a packaged installer supplies its own payload).");
            return ExitCodes.InvalidArguments;
        }

        if (mode == InstallMode.Install && s.Project!.Pages.Any(p => p.Type == InstallerPageType.LicenseAgreement) && !s.Command.AcceptLicense)
        {
            if (s.Command.Silent) { await s.Out.WriteLineAsync("This product has a license agreement. Pass --accept-license to accept it for a silent install."); return ExitCodes.InvalidArguments; }
            if (!await ConfirmAsync(s, "Do you accept the license agreement?")) { await s.Out.WriteLineAsync("The license was not accepted."); return ExitCodes.Cancelled; }
        }

        var plan = await engine.PlanAsync(request, ct);
        if (s.Command.DryRun) return await PrintPlanAsync(s, plan);
        if (plan.HasErrors)
        {
            await PrintPlanAsync(s, plan);
            return ExitCodes.Failure;
        }
        if (!s.Command.Silent)
        {
            await PrintPlanAsync(s, plan);
            if (!await ConfirmAsync(s, $"{mode} {s.Project!.Application.ProductName} now?")) return ExitCodes.Cancelled;
        }

        var result = await engine.InstallAsync(request, ProgressWriter(s), ct);
        if (result.ElevationRequired && s.Command.Elevate && !s.Command.Silent) return Relaunch(args);
        return await ReportAsync(s, result);
    }

    private static async Task<int> UninstallAsync(Session s, InstallEngine engine, IReadOnlyList<string> args, CancellationToken ct)
    {
        var installDir = ResolveInstallDir(s, engine);
        if (installDir is null)
        {
            await s.Out.WriteLineAsync("Cannot tell what to uninstall. Pass --install-dir <folder> (or --project <file.paproj>).");
            return ExitCodes.InvalidArguments;
        }
        var state = engine.LoadState(installDir);
        if (state is null) { await s.Out.WriteLineAsync($"No installation was found in '{installDir}'."); return ExitCodes.NotInstalled; }
        if (!await ConfirmAsync(s, $"Remove {state.ProductName} {state.Version} from {installDir}?")) return ExitCodes.Cancelled;

        var result = await engine.UninstallAsync(new UninstallRequest { InstallDir = installDir, RemoveUserData = s.Command.RemoveUserData, CurrentExecutable = Environment.ProcessPath }, ProgressWriter(s), ct);
        if (result.ElevationRequired && s.Command.Elevate && !s.Command.Silent) return Relaunch(args);
        return await ReportAsync(s, result);
    }

    private static async Task<int> ReportAsync(Session s, InstallResult r)
    {
        foreach (var w in r.Warnings) await s.Out.WriteLineAsync("Warning: " + w);
        foreach (var f in r.RollbackFailures) await s.Out.WriteLineAsync("Rollback problem: " + f);
        if (r.Success)
        {
            await s.Out.WriteLineAsync(r.AlreadyInstalled ? $"Version {r.Version} is already installed. Use repair to restore files." : $"Done: {r.Operation} {(r.Version is null ? "" : r.Version)} ({r.InstallDir}).");
            return ExitCodes.Success;
        }
        await s.Out.WriteLineAsync((r.Cancelled ? "Cancelled: " : "Failed: ") + r.Error + (r.RolledBack ? " All changes were rolled back." : ""));
        if (r.Cancelled) return ExitCodes.Cancelled;
        if (r.ElevationRequired) return ExitCodes.ElevationRequired;
        if (r.PrerequisitesFailed) return ExitCodes.PrerequisitesFailed;
        return ExitCodes.Failure;
    }

    /// <summary>Starts this program again with administrator rights and waits for it.</summary>
    private static int Relaunch(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath ?? "", string.Join(' ', args.Where(a => a != "--elevate").Select(Quote))) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode ?? ExitCodes.Failure;
        }
        catch (System.ComponentModel.Win32Exception) { return ExitCodes.ElevationRequired; } // user declined the prompt
    }

    private static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";
}
