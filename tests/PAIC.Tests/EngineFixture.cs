using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PAIC.Controls;
using PAIC.InstallerEngine;
using PAIC.InstallerEngine.Virtual;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;

namespace PAIC.Tests;

/// <summary>A project, a virtual machine and an engine wired together for installer tests.</summary>
public sealed class EngineFixture : IDisposable
{
    public TempDir Dir { get; } = new();
    public VirtualSystem Vm { get; }
    public PaicProject Project { get; }
    public InstallEngine Engine { get; }
    public Dictionary<string, byte[]> Files { get; } = new()
    {
        ["assets/app/app.exe"] = Encoding.UTF8.GetBytes("app v1"),
        ["assets/app/lib/core.dll"] = Encoding.UTF8.GetBytes("core v1"),
        ["assets/app/old.txt"] = Encoding.UTF8.GetBytes("old"),
        ["assets/docs/readme.txt"] = Encoding.UTF8.GetBytes("docs"),
    };

    public EngineFixture(Action<PaicProject>? configure = null)
    {
        Vm = new VirtualSystem(Path.Combine(Dir.Path, "machine"));
        Project = ProjectFactory.CreateNew("Demo");
        Project.Application.Publisher = "Acme";
        Project.Installation.DefaultInstallDirectory = @"%ProgramFiles%\Demo";
        Project.Components.Add(new ComponentDefinition { Id = "app", Name = "App", Required = true });
        Project.Components.Add(new ComponentDefinition { Id = "docs", Name = "Docs", DefaultSelected = false });
        Project.Operations.Add(Copy("copy-app", "app", "assets/app/**", "{InstallDir}"));
        Project.Operations.Add(Copy("copy-docs", "docs", "assets/docs/**", @"{InstallDir}\docs"));
        Project.Operations.Add(Op("shortcut", OperationKinds.CreateShortcut, "app", ("location", "startMenu"), ("name", "Demo"), ("target", @"{InstallDir}\app.exe")));
        configure?.Invoke(Project);
        Engine = new InstallEngine(Vm.Services, NullLogger.Instance);
    }

    public string InstallDir => Path.Combine(Vm.Paths.Folder("ProgramFiles"), "Demo");
    public string StateDir => Path.Combine(InstallDir, ".paic");
    public string ProductKey => InstallEngine.ProductKey(Project);
    public string ArpKey => $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductKey}";
    public string ShortcutPath => Path.Combine(Vm.Paths.StartMenuPrograms(allUsers: true), "Demo.lnk");
    public string Path1(string relative) => Path.Combine(InstallDir, relative.Replace('/', Path.DirectorySeparatorChar));

    public static InstallOperationDefinition Op(string id, string kind, string component, params (string Name, string Value)[] parameters)
    {
        var op = new InstallOperationDefinition { Id = id, Kind = kind, ComponentId = component };
        foreach (var (n, v) in parameters) op.Parameters[n] = v;
        return op;
    }

    public static InstallOperationDefinition Copy(string id, string component, string source, string destination, params (string, string)[] extra) =>
        Op(id, OperationKinds.CopyFiles, component, new[] { ("source", source), ("destination", destination) }.Concat(extra).ToArray());

    public IPayloadProvider Payload => new MemoryPayloadProvider(Files);

    public InstallRequest Request(InstallMode mode = InstallMode.Install, IReadOnlyCollection<string>? components = null, Dictionary<string, string>? variables = null,
        bool installPrerequisites = true, bool allowDowngrade = false, IPayloadProvider? payload = null, string? installDir = null) => new()
    {
        Project = Project, Payload = payload ?? Payload, Mode = mode, Components = components, Variables = variables ?? new Dictionary<string, string>(),
        InstallPrerequisites = installPrerequisites, AllowDowngrade = allowDowngrade, InstallDir = installDir
    };

    public Task<InstallResult> Install(InstallMode mode = InstallMode.Install, IReadOnlyCollection<string>? components = null, CancellationToken ct = default) =>
        Engine.InstallAsync(Request(mode, components), null, ct);

    public Task<InstallResult> Uninstall(bool removeUserData = false, string? currentExecutable = null) =>
        Engine.UninstallAsync(new UninstallRequest { InstallDir = InstallDir, RemoveUserData = removeUserData, CurrentExecutable = currentExecutable });

    public static InstallOperationDefinition FailingCommand() => Op("fail", OperationKinds.RunCommand, "", ("command", "boom.exe"));

    public void MakeCommandsFail() => Vm.ProcessHandler = _ => new ProcessResult(1, "failed", false);

    public void Dispose() => Dir.Dispose();
}

/// <summary>Returns different bytes than it advertises, like a damaged or tampered package.</summary>
public sealed class TamperedPayload : IPayloadProvider
{
    private readonly IPayloadProvider _inner;
    public TamperedPayload(IPayloadProvider inner) => _inner = inner;
    public IReadOnlyList<PayloadFile> Files => _inner.Files;
    public Stream Open(string path) => new MemoryStream(Encoding.UTF8.GetBytes("tampered content"));
}
