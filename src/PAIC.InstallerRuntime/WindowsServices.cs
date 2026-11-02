using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using PAIC.InstallerEngine;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerRuntime;

public sealed class WindowsRegistryService : IRegistryService
{
    private static RegistryKey Base(string root) => root.ToUpperInvariant() switch
    {
        "HKCU" => Registry.CurrentUser,
        "HKLM" => Registry.LocalMachine,
        _ => throw new EngineException($"Unsupported registry root '{root}'.")
    };

    public RegistryValue? GetValue(string root, string key, string name)
    {
        using var k = Base(root).OpenSubKey(key);
        if (k is null) return null;
        var value = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value is null) return null;
        return k.GetValueKind(name) switch
        {
            RegistryValueKind.ExpandString => new RegistryValue("expandString", (string)value),
            RegistryValueKind.DWord => new RegistryValue("dword", unchecked((uint)(int)value).ToString()),
            RegistryValueKind.QWord => new RegistryValue("qword", unchecked((ulong)(long)value).ToString()),
            RegistryValueKind.MultiString => new RegistryValue("multiString", string.Join('\n', (string[])value)),
            _ => new RegistryValue("string", value.ToString() ?? "")
        };
    }

    public void SetValue(string root, string key, string name, RegistryValue value)
    {
        using var k = Base(root).CreateSubKey(key, writable: true) ?? throw new EngineException($"Could not open registry key {root}\\{key}.");
        switch (value.Type)
        {
            case "dword": k.SetValue(name, unchecked((int)uint.Parse(value.Data)), RegistryValueKind.DWord); break;
            case "qword": k.SetValue(name, unchecked((long)ulong.Parse(value.Data)), RegistryValueKind.QWord); break;
            case "expandString": k.SetValue(name, value.Data, RegistryValueKind.ExpandString); break;
            case "multiString": k.SetValue(name, value.Data.Split('\n'), RegistryValueKind.MultiString); break;
            default: k.SetValue(name, value.Data, RegistryValueKind.String); break;
        }
    }

    public bool DeleteValue(string root, string key, string name)
    {
        using var k = Base(root).OpenSubKey(key, writable: true);
        if (k is null || k.GetValue(name) is null) return false;
        k.DeleteValue(name, throwOnMissingValue: false);
        return true;
    }

    public bool KeyExists(string root, string key)
    {
        using var k = Base(root).OpenSubKey(key);
        return k is not null;
    }

    public bool CreateKey(string root, string key)
    {
        if (KeyExists(root, key)) return false;
        Base(root).CreateSubKey(key)?.Dispose();
        return true;
    }

    public bool DeleteKeyIfEmpty(string root, string key)
    {
        using (var k = Base(root).OpenSubKey(key))
        {
            if (k is null || k.ValueCount > 0 || k.SubKeyCount > 0) return false;
        }
        Base(root).DeleteSubKey(key, throwOnMissingSubKey: false);
        return true;
    }
}

public sealed class WindowsEnvironmentService : IEnvironmentService
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);

    private static RegistryKey? Open(string scope, bool writable) =>
        scope == "machine" ? Registry.LocalMachine.OpenSubKey(MachineKey, writable) : Registry.CurrentUser.OpenSubKey("Environment", writable);

    public string? Get(string scope, string name)
    {
        using var k = Open(scope, writable: false);
        return k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    public void Set(string scope, string name, string? value)
    {
        using var k = Open(scope, writable: true) ?? throw new EngineException($"Could not open the {scope} environment.");
        if (value is null) k.DeleteValue(name, throwOnMissingValue: false);
        else
        {
            var wasExpandable = k.GetValue(name) is not null && k.GetValueKind(name) == RegistryValueKind.ExpandString;
            k.SetValue(name, value, wasExpandable || value.Contains('%') ? RegistryValueKind.ExpandString : RegistryValueKind.String);
        }
        // tell running programs (Explorer, new consoles) that the environment changed
        SendMessageTimeout(new IntPtr(0xffff), 0x1A, UIntPtr.Zero, "Environment", 0x2, 5000, out _);
    }
}

public sealed class WindowsShortcutService : IShortcutService
{
    public void Create(ShortcutSpec spec)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new EngineException("Windows Script Host is not available, so shortcuts cannot be created.");
        dynamic? shell = Activator.CreateInstance(type);
        dynamic? link = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spec.Path)!);
            link = shell!.CreateShortcut(spec.Path);
            link.TargetPath = spec.Target;
            link.Arguments = spec.Arguments;
            if (!string.IsNullOrEmpty(spec.WorkingDirectory)) link.WorkingDirectory = spec.WorkingDirectory;
            if (!string.IsNullOrEmpty(spec.IconPath)) link.IconLocation = spec.IconPath + ",0";
            if (!string.IsNullOrEmpty(spec.Description)) link.Description = spec.Description;
            link.Save();
        }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }
}

public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(spec.FileName, spec.Arguments)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = spec.WorkingDirectory ?? ""
        };
        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        void Append(string? line) { if (line is null) return; lock (output) { if (output.Length < 1_000_000) output.AppendLine(line); } }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        try { process.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new EngineException($"Could not start '{spec.FileName}': {ex.Message}", ex);
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(spec.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            process.WaitForExit(); // flush the redirected streams
            return new ProcessResult(process.ExitCode, output.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, output.ToString(), true);
        }
    }
}

public sealed class WindowsServiceManager : IServiceManager
{
    private (int Exit, string Output) Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new EngineException("Could not start sc.exe.");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private static string Account(string account) => account.Trim() switch
    {
        "" or "LocalSystem" => "LocalSystem",
        "LocalService" or "NT AUTHORITY\\LocalService" => "NT AUTHORITY\\LocalService",
        "NetworkService" or "NT AUTHORITY\\NetworkService" => "NT AUTHORITY\\NetworkService",
        _ => throw new EngineException($"The service account '{account}' is not supported. Use LocalSystem, LocalService or NetworkService.")
    };

    public ServiceInfo? Get(string name)
    {
        var (exit, output) = Sc("query", name);
        if (exit == 1060) return null;
        if (exit != 0) throw new EngineException($"Could not query service '{name}': {output.Trim()}");
        return new ServiceInfo(name, output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase));
    }

    private static string BinPath(ServiceSpec s) => string.IsNullOrWhiteSpace(s.Arguments) ? $"\"{s.BinaryPath}\"" : $"\"{s.BinaryPath}\" {s.Arguments}";
    private static string StartMode(string type) => type switch { "manual" => "demand", "disabled" => "disabled", _ => "auto" };

    public void Create(ServiceSpec s)
    {
        var (exit, output) = Sc("create", s.Name, "binPath=", BinPath(s), "start=", StartMode(s.StartType), "obj=", Account(s.Account), "DisplayName=", string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName);
        if (exit != 0) throw new EngineException($"Could not create service '{s.Name}': {output.Trim()}");
        if (!string.IsNullOrWhiteSpace(s.Description)) Sc("description", s.Name, s.Description);
    }

    public void Reconfigure(ServiceSpec s)
    {
        var (exit, output) = Sc("config", s.Name, "binPath=", BinPath(s), "start=", StartMode(s.StartType), "obj=", Account(s.Account), "DisplayName=", string.IsNullOrWhiteSpace(s.DisplayName) ? s.Name : s.DisplayName);
        if (exit != 0) throw new EngineException($"Could not configure service '{s.Name}': {output.Trim()}");
        if (!string.IsNullOrWhiteSpace(s.Description)) Sc("description", s.Name, s.Description);
    }

    public void Start(string name)
    {
        var (exit, output) = Sc("start", name);
        if (exit != 0 && exit != 1056) throw new EngineException($"Could not start service '{name}': {output.Trim()}");
    }

    public void Stop(string name)
    {
        var (exit, output) = Sc("stop", name);
        if (exit != 0 && exit != 1062) throw new EngineException($"Could not stop service '{name}': {output.Trim()}");
        for (var i = 0; i < 50 && Get(name)?.Running == true; i++) Thread.Sleep(200); // wait until it has actually stopped
    }

    public void Delete(string name)
    {
        var (exit, output) = Sc("delete", name);
        if (exit != 0 && exit != 1060) throw new EngineException($"Could not delete service '{name}': {output.Trim()}");
    }
}

public sealed class WindowsPermissionService : IPermissionService
{
    private static FileSystemRights Rights(string rights) => rights switch
    {
        "fullControl" => FileSystemRights.FullControl,
        "modify" => FileSystemRights.Modify,
        _ => FileSystemRights.ReadAndExecute
    };

    private static FileSystemAccessRule Rule(string path, string principal, string rights)
    {
        var inherit = Directory.Exists(path) ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None;
        return new FileSystemAccessRule(new NTAccount(principal), Rights(rights), inherit, PropagationFlags.None, AccessControlType.Allow);
    }

    public bool AddRule(string path, string principal, string rights)
    {
        var wanted = Rule(path, principal, rights);
        if (Directory.Exists(path))
        {
            var info = new DirectoryInfo(path);
            var security = info.GetAccessControl();
            if (Exists(security, wanted)) return false;
            security.AddAccessRule(wanted);
            info.SetAccessControl(security);
        }
        else
        {
            var info = new FileInfo(path);
            var security = info.GetAccessControl();
            if (Exists(security, wanted)) return false;
            security.AddAccessRule(wanted);
            info.SetAccessControl(security);
        }
        return true;
    }

    private static bool Exists(FileSystemSecurity security, FileSystemAccessRule wanted)
    {
        var sid = wanted.IdentityReference.Translate(typeof(SecurityIdentifier));
        return security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Any(r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & wanted.FileSystemRights) == wanted.FileSystemRights);
    }

    public void RemoveRule(string path, string principal, string rights)
    {
        var rule = Rule(path, principal, rights);
        if (Directory.Exists(path)) { var i = new DirectoryInfo(path); var s = i.GetAccessControl(); s.RemoveAccessRuleSpecific(rule); i.SetAccessControl(s); }
        else { var i = new FileInfo(path); var s = i.GetAccessControl(); s.RemoveAccessRuleSpecific(rule); i.SetAccessControl(s); }
    }
}

public sealed class HttpDownloader : IDownloader
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(30) };

    public async Task DownloadAsync(Uri url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new EngineException("Only https downloads are allowed.");
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long done = 0; int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total is > 0) progress?.Report(100d * done / total.Value);
        }
    }
}

public sealed class WindowsDetectionService : IDetectionService
{
    public PrerequisiteArchitecture Architecture => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.Arm64 => PrerequisiteArchitecture.Arm64,
        System.Runtime.InteropServices.Architecture.X86 => PrerequisiteArchitecture.X86,
        _ => PrerequisiteArchitecture.X64
    };

    public Version? GetVersion(DetectionKind kind, string target, string value)
    {
        try
        {
            return kind switch
            {
                DetectionKind.RegistryValue => FromRegistry(target, value),
                DetectionKind.FileVersion => FromFile(Environment.ExpandEnvironmentVariables(target)),
                DetectionKind.DotnetRuntime => FromDotnet(value),
                DetectionKind.WindowsVersion => Environment.OSVersion.Version,
                _ => null
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static Version? FromRegistry(string target, string valueName)
    {
        var slash = target.IndexOf('\\');
        if (slash <= 0) return null;
        var hive = target[..slash].ToUpperInvariant() switch { "HKLM" => RegistryHive.LocalMachine, "HKCU" => RegistryHive.CurrentUser, _ => (RegistryHive?)null };
        if (hive is null) return null;
        var key = target[(slash + 1)..];
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive.Value, view);
            using var k = baseKey.OpenSubKey(key);
            if (k?.GetValue(valueName) is { } v && VersionParser.Parse(v.ToString()) is { } version) return version;
        }
        return null;
    }

    private static Version? FromFile(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    private static Version? FromDotnet(string runtimeName)
    {
        if (string.IsNullOrWhiteSpace(runtimeName) || runtimeName.IndexOfAny(new[] { '\\', '/' }) >= 0) return null;
        Version? best = null;
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var dir = Path.Combine(Environment.GetFolderPath(folder), "dotnet", "shared", runtimeName);
            if (!Directory.Exists(dir)) continue;
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (VersionParser.Parse(Path.GetFileName(sub)) is { } v && (best is null || v > best)) best = v;
        }
        return best;
    }
}

public sealed class WindowsPaths : ISystemPaths
{
    public string ExpandEnvironment(string text) => Environment.ExpandEnvironmentVariables(text);

    public string StartMenuPrograms(bool allUsers) => Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs);
    public string Desktop(bool allUsers) => Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory);
    public string Startup(bool allUsers) => Environment.GetFolderPath(allUsers ? Environment.SpecialFolder.CommonStartup : Environment.SpecialFolder.Startup);

    public string LogDirectory(bool perUser, string productId) =>
        Path.Combine(Environment.GetFolderPath(perUser ? Environment.SpecialFolder.LocalApplicationData : Environment.SpecialFolder.CommonApplicationData),
            "PAIC", "InstallerLogs", productId.Trim('{', '}'));

    private static bool Under(Environment.SpecialFolder folder, string path)
    {
        var dir = Environment.GetFolderPath(folder);
        if (string.IsNullOrEmpty(dir)) return false;
        var d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
        return (Path.GetFullPath(path).TrimEnd('\\') + "\\").StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    public bool IsProtected(string fullPath) => Under(Environment.SpecialFolder.Windows, fullPath) || Under(Environment.SpecialFolder.System, fullPath);

    public bool RequiresElevation(string fullPath) =>
        Under(Environment.SpecialFolder.ProgramFiles, fullPath) || Under(Environment.SpecialFolder.ProgramFilesX86, fullPath) || IsProtected(fullPath);
}

public sealed class WindowsInstallLock : IInstallLock
{
    private sealed class Handle : IDisposable
    {
        private Mutex? _mutex;
        public Handle(Mutex mutex) => _mutex = mutex;
        public void Dispose() { try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { } _mutex?.Dispose(); _mutex = null; }
    }

    public IDisposable? TryAcquire(string productId)
    {
        var id = productId.Trim('{', '}');
        Mutex mutex;
        try { mutex = new Mutex(false, @"Global\PAIC-Setup-" + id); }
        catch (UnauthorizedAccessException) { mutex = new Mutex(false, @"Local\PAIC-Setup-" + id); }
        try
        {
            if (mutex.WaitOne(0)) return new Handle(mutex);
        }
        catch (AbandonedMutexException) { return new Handle(mutex); } // a crashed setup left it; we now own it
        mutex.Dispose();
        return null;
    }
}

public static class WindowsSystem
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts a hidden command that deletes files that were in use (such as the running uninstaller) once this process has exited.</summary>
    public static void ScheduleDeletion(IReadOnlyList<string> files, string installDirectory)
    {
        var sb = new StringBuilder("/c ping 127.0.0.1 -n 4 >nul");
        foreach (var f in files) sb.Append($" & del /f /q \"{f}\"");
        var stateDir = Path.Combine(installDirectory, ".paic");
        sb.Append($" & rmdir \"{stateDir}\" & rmdir \"{installDirectory}\"");
        Process.Start(new ProcessStartInfo("cmd.exe", sb.ToString()) { UseShellExecute = false, CreateNoWindow = true });
    }

    public static SystemServices Create() => new()
    {
        Registry = new WindowsRegistryService(), Environment = new WindowsEnvironmentService(), Shortcuts = new WindowsShortcutService(),
        Services = new WindowsServiceManager(), Permissions = new WindowsPermissionService(), Processes = new WindowsProcessRunner(),
        Downloader = new HttpDownloader(), Detection = new WindowsDetectionService(), Paths = new WindowsPaths(),
        Uninstaller = new PAIC.InstallerEngine.Virtual.CopyExecutableUninstaller(ScheduleDeletion), Locks = new WindowsInstallLock(), IsElevated = IsElevated
    };
}
