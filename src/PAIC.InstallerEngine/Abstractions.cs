using PAIC.Core;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine;

/// <summary>Error whose message is meant for the person running the installer.</summary>
public sealed class EngineException : UserFacingException
{
    public EngineException(string message) : base(message) { }
    public EngineException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>A file of the installer payload. Path is project-relative with '/' separators (assets/...).</summary>
public sealed record PayloadFile(string Path, long Size, string Sha256);

/// <summary>Where the files to install come from: the project folder during development, a package later.</summary>
public interface IPayloadProvider
{
    IReadOnlyList<PayloadFile> Files { get; }
    Stream Open(string path);
}

public sealed record RegistryValue(string Type, string Data);

/// <summary>Registry access. Roots are "HKCU" and "HKLM". multiString data uses '\n' between strings.</summary>
public interface IRegistryService
{
    RegistryValue? GetValue(string root, string key, string name);
    void SetValue(string root, string key, string name, RegistryValue value);
    bool DeleteValue(string root, string key, string name);
    bool KeyExists(string root, string key);
    /// <returns>True when the key did not exist before.</returns>
    bool CreateKey(string root, string key);
    bool DeleteKeyIfEmpty(string root, string key);
}

public interface IEnvironmentService
{
    /// <summary>Scope is "user" or "machine". Null when the variable does not exist.</summary>
    string? Get(string scope, string name);
    /// <summary>A null value deletes the variable.</summary>
    void Set(string scope, string name, string? value);
}

public sealed record ShortcutSpec(string Path, string Target, string Arguments, string WorkingDirectory, string IconPath, string Description);

public interface IShortcutService
{
    void Create(ShortcutSpec spec);
}

public sealed record ServiceSpec(string Name, string DisplayName, string BinaryPath, string Arguments, string StartType, string Account, string Description);
public sealed record ServiceInfo(string Name, bool Running);

public interface IServiceManager
{
    ServiceInfo? Get(string name);
    void Create(ServiceSpec spec);
    void Reconfigure(ServiceSpec spec);
    void Start(string name);
    void Stop(string name);
    void Delete(string name);
}

public interface IPermissionService
{
    /// <returns>True when a rule was added (false when an equivalent rule already existed).</returns>
    bool AddRule(string path, string principal, string rights);
    void RemoveRule(string path, string principal, string rights);
}

public sealed record ProcessSpec(string FileName, string Arguments, string? WorkingDirectory, TimeSpan Timeout);
public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

public interface IDownloader
{
    Task DownloadAsync(Uri url, string destinationPath, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>Reads installed versions for prerequisite detection.</summary>
public interface IDetectionService
{
    Version? GetVersion(DetectionKind kind, string target, string value);
    PrerequisiteArchitecture Architecture { get; }
}

public interface ISystemPaths
{
    /// <summary>Expands %VARIABLE% references.</summary>
    string ExpandEnvironment(string text);
    string StartMenuPrograms(bool allUsers);
    string Desktop(bool allUsers);
    string Startup(bool allUsers);
    /// <summary>Folder for install logs and history that survives uninstall.</summary>
    string LogDirectory(bool perUser, string productId);
    /// <summary>Folders that installers must never write to (for example the Windows folder).</summary>
    bool IsProtected(string fullPath);
    /// <summary>True when writing there needs administrator rights (Program Files, Windows, ...).</summary>
    bool RequiresElevation(string fullPath);
}

public interface IUninstallerProvisioner
{
    /// <summary>Places the uninstaller in the state folder and returns its path, or null when none is available.</summary>
    Task<string?> ProvisionAsync(string stateDirectory, CancellationToken ct);
    /// <summary>Removes files that could not be deleted because they are in use (for example the running uninstaller).</summary>
    void ScheduleDeletion(IReadOnlyList<string> files, string installDirectory);
}

public interface IInstallLock
{
    /// <summary>Returns a handle that releases the lock when disposed, or null when another setup is running.</summary>
    IDisposable? TryAcquire(string productId);
}

/// <summary>Everything the engine needs from the machine. Windows implementations live in PAIC.InstallerRuntime; tests use virtual ones.</summary>
public sealed class SystemServices
{
    public required IRegistryService Registry { get; init; }
    public required IEnvironmentService Environment { get; init; }
    public required IShortcutService Shortcuts { get; init; }
    public required IServiceManager Services { get; init; }
    public required IPermissionService Permissions { get; init; }
    public required IProcessRunner Processes { get; init; }
    public required IDownloader Downloader { get; init; }
    public required IDetectionService Detection { get; init; }
    public required ISystemPaths Paths { get; init; }
    public required IUninstallerProvisioner Uninstaller { get; init; }
    public required IInstallLock Locks { get; init; }
    public required Func<bool> IsElevated { get; init; }
}
