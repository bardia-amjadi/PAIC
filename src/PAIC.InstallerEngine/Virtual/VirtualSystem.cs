using System.Text.Json;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine.Virtual;

/// <summary>
/// A complete in-memory stand-in for the machine: registry, environment, services, permissions, processes, downloads and well-known folders.
/// Files are real but live under <see cref="Root"/>. Used by tests, dry runs and sandbox test installs, so nothing outside the sandbox is touched.
/// Optionally persists its state to a JSON file so several command-line runs share one sandbox.
/// </summary>
public sealed class VirtualSystem
{
    private sealed class MachineState
    {
        public Dictionary<string, Dictionary<string, RegistryValue>> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Keys { get; set; } = new();
        public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VirtualService> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Permissions { get; set; } = new();
    }

    public sealed class VirtualService
    {
        public ServiceSpec Spec { get; set; } = new("", "", "", "", "", "", "");
        public bool Running { get; set; }
    }

    private readonly MachineState _state = new();
    private readonly string? _persistPath;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public VirtualSystem(string root, string? persistPath = null)
    {
        Root = Path.GetFullPath(root);
        _persistPath = persistPath;
        if (persistPath is not null && File.Exists(persistPath))
        {
            try { _state = JsonSerializer.Deserialize<MachineState>(File.ReadAllText(persistPath), Options) ?? new MachineState(); }
            catch (JsonException) { _state = new MachineState(); }
        }
        Paths = new VirtualPaths(this);
        Services = BuildServices();
    }

    public string Root { get; }
    public bool Elevated { get; set; } = true;
    /// <summary>When true, Program Files and Windows paths report that they need administrator rights (to test elevation handling).</summary>
    public bool SimulateAdminFolders { get; set; }
    public VirtualPaths Paths { get; }
    public SystemServices Services { get; }

    public List<ShortcutSpec> Shortcuts { get; } = new();
    public List<ProcessSpec> ProcessCalls { get; } = new();
    public Func<ProcessSpec, ProcessResult> ProcessHandler { get; set; } = _ => new ProcessResult(0, "", false);
    public Dictionary<string, byte[]> Downloads { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DownloadCalls { get; } = new();
    public Dictionary<string, Version> DetectedVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public PrerequisiteArchitecture MachineArchitecture { get; set; } = PrerequisiteArchitecture.X64;
    public List<string> ScheduledDeletions { get; } = new();
    public HashSet<string> HeldLocks { get; } = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, VirtualService> ServiceList => _state.Services;
    public IReadOnlyList<string> PermissionRules => _state.Permissions;
    public bool Fail { get; set; }

    public static string DetectionKey(DetectionKind kind, string target, string value) => $"{kind}|{target}|{value}";
    public void SetDetected(DetectionKind kind, string target, string value, Version version) => DetectedVersions[DetectionKey(kind, target, value)] = version;

    private void Save()
    {
        if (_persistPath is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_persistPath)!);
        File.WriteAllText(_persistPath, JsonSerializer.Serialize(_state, Options));
    }

    private static string K(string root, string key) => root.ToUpperInvariant() + "\\" + key.Trim('\\');

    private SystemServices BuildServices() => new()
    {
        Registry = new VReg(this), Environment = new VEnv(this), Shortcuts = new VShortcuts(this), Services = new VSvc(this),
        Permissions = new VPerm(this), Processes = new VProc(this), Downloader = new VDownload(this), Detection = new VDetect(this),
        Paths = Paths, Uninstaller = new VUninstaller(this), Locks = new VLock(this), IsElevated = () => Elevated
    };

    // ------------------------------------------------------------ registry
    private sealed class VReg : IRegistryService
    {
        private readonly VirtualSystem _s; public VReg(VirtualSystem s) => _s = s;
        public RegistryValue? GetValue(string root, string key, string name) =>
            _s._state.Values.TryGetValue(K(root, key), out var v) && v.TryGetValue(name ?? "", out var val) ? val : null;
        public void SetValue(string root, string key, string name, RegistryValue value)
        {
            var k = K(root, key);
            if (!_s._state.Values.TryGetValue(k, out var v)) _s._state.Values[k] = v = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
            if (!_s._state.Keys.Contains(k, StringComparer.OrdinalIgnoreCase)) _s._state.Keys.Add(k);
            v[name ?? ""] = value;
            _s.Save();
        }
        public bool DeleteValue(string root, string key, string name)
        {
            var removed = _s._state.Values.TryGetValue(K(root, key), out var v) && v.Remove(name ?? "");
            if (removed) _s.Save();
            return removed;
        }
        public bool KeyExists(string root, string key) => _s._state.Keys.Contains(K(root, key), StringComparer.OrdinalIgnoreCase);
        public bool CreateKey(string root, string key)
        {
            var k = K(root, key);
            if (KeyExists(root, key)) return false;
            _s._state.Keys.Add(k); _s.Save();
            return true;
        }
        public bool DeleteKeyIfEmpty(string root, string key)
        {
            var k = K(root, key);
            if (_s._state.Values.TryGetValue(k, out var v) && v.Count > 0) return false;
            if (_s._state.Keys.Any(o => o.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))) return false;
            var removed = _s._state.Keys.RemoveAll(o => o.Equals(k, StringComparison.OrdinalIgnoreCase)) > 0;
            _s._state.Values.Remove(k);
            if (removed) _s.Save();
            return removed;
        }
    }

    // ------------------------------------------------------------ environment
    private sealed class VEnv : IEnvironmentService
    {
        private readonly VirtualSystem _s; public VEnv(VirtualSystem s) => _s = s;
        public string? Get(string scope, string name) => _s._state.Environment.TryGetValue(scope + "|" + name, out var v) ? v : null;
        public void Set(string scope, string name, string? value)
        {
            if (value is null) _s._state.Environment.Remove(scope + "|" + name); else _s._state.Environment[scope + "|" + name] = value;
            _s.Save();
        }
    }

    private sealed class VShortcuts : IShortcutService
    {
        private readonly VirtualSystem _s; public VShortcuts(VirtualSystem s) => _s = s;
        public void Create(ShortcutSpec spec)
        {
            if (_s.Fail) throw new EngineException("Simulated shortcut failure.");
            Directory.CreateDirectory(Path.GetDirectoryName(spec.Path)!);
            File.WriteAllText(spec.Path, $"[virtual shortcut] {spec.Target} {spec.Arguments}");
            _s.Shortcuts.Add(spec);
        }
    }

    private sealed class VSvc : IServiceManager
    {
        private readonly VirtualSystem _s; public VSvc(VirtualSystem s) => _s = s;
        public ServiceInfo? Get(string name) => _s._state.Services.TryGetValue(name, out var v) ? new ServiceInfo(name, v.Running) : null;
        public void Create(ServiceSpec spec) { _s._state.Services[spec.Name] = new VirtualService { Spec = spec }; _s.Save(); }
        public void Reconfigure(ServiceSpec spec) { if (_s._state.Services.TryGetValue(spec.Name, out var v)) { v.Spec = spec; _s.Save(); } }
        public void Start(string name) { if (_s._state.Services.TryGetValue(name, out var v)) { v.Running = true; _s.Save(); } }
        public void Stop(string name) { if (_s._state.Services.TryGetValue(name, out var v)) { v.Running = false; _s.Save(); } }
        public void Delete(string name) { _s._state.Services.Remove(name); _s.Save(); }
    }

    private sealed class VPerm : IPermissionService
    {
        private readonly VirtualSystem _s; public VPerm(VirtualSystem s) => _s = s;
        private static string Rule(string p, string who, string r) => $"{p}|{who}|{r}";
        public bool AddRule(string path, string principal, string rights)
        {
            var rule = Rule(path, principal, rights);
            if (_s._state.Permissions.Contains(rule, StringComparer.OrdinalIgnoreCase)) return false;
            _s._state.Permissions.Add(rule); _s.Save();
            return true;
        }
        public void RemoveRule(string path, string principal, string rights)
        {
            _s._state.Permissions.RemoveAll(r => r.Equals(Rule(path, principal, rights), StringComparison.OrdinalIgnoreCase)); _s.Save();
        }
    }

    private sealed class VProc : IProcessRunner
    {
        private readonly VirtualSystem _s; public VProc(VirtualSystem s) => _s = s;
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _s.ProcessCalls.Add(spec);
            return Task.FromResult(_s.ProcessHandler(spec));
        }
    }

    private sealed class VDownload : IDownloader
    {
        private readonly VirtualSystem _s; public VDownload(VirtualSystem s) => _s = s;
        public Task DownloadAsync(Uri url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            _s.DownloadCalls.Add(url.ToString());
            if (!_s.Downloads.TryGetValue(url.ToString(), out var data)) throw new EngineException($"No virtual download for {url}.");
            File.WriteAllBytes(destinationPath, data);
            progress?.Report(100);
            return Task.CompletedTask;
        }
    }

    private sealed class VDetect : IDetectionService
    {
        private readonly VirtualSystem _s; public VDetect(VirtualSystem s) => _s = s;
        public PrerequisiteArchitecture Architecture => _s.MachineArchitecture;
        public Version? GetVersion(DetectionKind kind, string target, string value) =>
            _s.DetectedVersions.TryGetValue(DetectionKey(kind, target ?? "", value ?? ""), out var v) ? v : null;
    }

    private sealed class VUninstaller : IUninstallerProvisioner
    {
        private readonly VirtualSystem _s; public VUninstaller(VirtualSystem s) => _s = s;
        public Task<string?> ProvisionAsync(string stateDirectory, CancellationToken ct)
        {
            var path = Path.Combine(stateDirectory, "uninstall.exe");
            File.WriteAllText(path, "virtual uninstaller");
            return Task.FromResult<string?>(path);
        }
        public void ScheduleDeletion(IReadOnlyList<string> files, string installDirectory) => _s.ScheduledDeletions.AddRange(files);
    }

    private sealed class VLock : IInstallLock
    {
        private readonly VirtualSystem _s; public VLock(VirtualSystem s) => _s = s;
        private sealed class Handle : IDisposable
        {
            private readonly VirtualSystem _s; private readonly string _id;
            public Handle(VirtualSystem s, string id) { _s = s; _id = id; }
            public void Dispose() => _s.HeldLocks.Remove(_id);
        }
        public IDisposable? TryAcquire(string productId) => _s.HeldLocks.Add(productId) ? new Handle(_s, productId) : null;
    }
}

/// <summary>Well-known folders inside the virtual machine root.</summary>
public sealed class VirtualPaths : ISystemPaths
{
    private readonly VirtualSystem _system;
    private readonly Dictionary<string, string> _folders;

    public VirtualPaths(VirtualSystem system)
    {
        _system = system;
        var r = system.Root;
        _folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ProgramFiles"] = Path.Combine(r, "ProgramFiles"), ["ProgramFiles(x86)"] = Path.Combine(r, "ProgramFilesX86"),
            ["ProgramData"] = Path.Combine(r, "ProgramData"), ["LocalAppData"] = Path.Combine(r, "Users", "test", "AppData", "Local"),
            ["AppData"] = Path.Combine(r, "Users", "test", "AppData", "Roaming"), ["UserProfile"] = Path.Combine(r, "Users", "test"),
            ["Temp"] = Path.Combine(r, "Temp"), ["SystemRoot"] = Path.Combine(r, "Windows"), ["WinDir"] = Path.Combine(r, "Windows")
        };
    }

    public string Folder(string name) => _folders[name];

    public string ExpandEnvironment(string text)
    {
        foreach (var (name, value) in _folders)
            text = System.Text.RegularExpressions.Regex.Replace(text, "%" + System.Text.RegularExpressions.Regex.Escape(name) + "%", value.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return text;
    }

    public string StartMenuPrograms(bool allUsers) => allUsers ? Path.Combine(_folders["ProgramData"], "Microsoft", "Windows", "Start Menu", "Programs") : Path.Combine(_folders["AppData"], "Microsoft", "Windows", "Start Menu", "Programs");
    public string Desktop(bool allUsers) => allUsers ? Path.Combine(_system.Root, "Users", "Public", "Desktop") : Path.Combine(_folders["UserProfile"], "Desktop");
    public string Startup(bool allUsers) => Path.Combine(StartMenuPrograms(allUsers), "Startup");
    public string LogDirectory(bool perUser, string productId) =>
        Path.Combine(perUser ? _folders["LocalAppData"] : _folders["ProgramData"], "PAIC", "InstallerLogs", productId.Trim('{', '}'));
    public bool IsProtected(string fullPath) => Under(_folders["SystemRoot"], fullPath);
    public bool RequiresElevation(string fullPath) =>
        _system.SimulateAdminFolders && (Under(_folders["ProgramFiles"], fullPath) || Under(_folders["ProgramFiles(x86)"], fullPath) || Under(_folders["SystemRoot"], fullPath));

    private static bool Under(string dir, string path) =>
        Path.GetFullPath(path).TrimEnd('\\', '/').StartsWith(Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(path).TrimEnd('\\', '/').Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
}

/// <summary>Copies the running executable next to the state file so it can serve as the uninstaller (works for single-file publishes).</summary>
public sealed class CopyExecutableUninstaller : IUninstallerProvisioner
{
    private readonly Action<IReadOnlyList<string>, string>? _scheduler;

    public CopyExecutableUninstaller(Action<IReadOnlyList<string>, string>? scheduler = null) => _scheduler = scheduler;

    public Task<string?> ProvisionAsync(string stateDirectory, CancellationToken ct)
    {
        var source = Environment.ProcessPath;
        if (string.IsNullOrEmpty(source) || !File.Exists(source) || Path.GetFileNameWithoutExtension(source).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>(null);
        var target = Path.Combine(stateDirectory, "uninstall.exe");
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: true);
        return Task.FromResult<string?>(target);
    }

    public void ScheduleDeletion(IReadOnlyList<string> files, string installDirectory) => _scheduler?.Invoke(files, installDirectory);
}
