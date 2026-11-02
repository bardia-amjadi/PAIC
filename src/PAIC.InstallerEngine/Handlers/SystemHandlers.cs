using System.Globalization;
using Microsoft.Extensions.Logging;
using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine.Handlers;

public sealed class CreateShortcutHandler : OperationHandler
{
    public override string Kind => OperationKinds.CreateShortcut;

    private static string FolderFor(OperationContext c, InstallOperationDefinition op)
    {
        var allUsers = !c.PerUser;
        var folder = op.Str("location", "startMenu") switch
        {
            "desktop" => c.System.Paths.Desktop(allUsers),
            "startup" => c.System.Paths.Startup(allUsers),
            _ => c.System.Paths.StartMenuPrograms(allUsers)
        };
        var sub = c.Expand(op.Str("folder"));
        if (sub.Length > 0)
        {
            if (sub.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || sub is "." or "..") throw new EngineException($"'{sub}' is not a valid shortcut folder name.");
            if (op.Str("location", "startMenu") == "startMenu") folder = Path.Combine(folder, sub);
        }
        return folder;
    }

    private static string PathFor(OperationContext c, InstallOperationDefinition op)
    {
        var name = c.Expand(op.Str("name"));
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new EngineException($"'{name}' is not a valid shortcut name.");
        return Path.Combine(FolderFor(c, op), name + ".lnk");
    }

    public override string Describe(OperationContext c, InstallOperationDefinition op)
    {
        try { return $"Create shortcut {PathFor(c, op)}"; } catch (EngineException) { return "Create shortcut " + op.Str("name"); }
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        string? problem = null;
        try { PathFor(c, op); c.Expand(op.Str("target")); } catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
    }

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        if (!c.PerUser && op.Str("location", "startMenu") is "startMenu" or "desktop" or "startup")
            yield return "Shortcuts for all users need administrator rights.";
    }

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var records = new List<UndoRecord>();
        var path = PathFor(c, op);
        CreateDirectoryHandler.EnsureDirectory(Path.GetDirectoryName(path)!, op, records);
        string? backup = null;
        if (File.Exists(path)) { backup = c.NewBackupPath(); File.Move(path, backup); }

        var target = c.Expand(op.Str("target"));
        var workingDirectory = op.Str("workingDirectory").Length > 0 ? c.Expand(op.Str("workingDirectory")) : Path.GetDirectoryName(target) ?? "";
        try
        {
            c.System.Shortcuts.Create(new ShortcutSpec(path, target, c.Expand(op.Str("arguments")), workingDirectory, c.Expand(op.Str("icon")), c.Expand(op.Str("description"))));
        }
        catch
        {
            if (backup is not null && !File.Exists(path)) File.Move(backup, path);
            throw;
        }
        var rec = NewRecord(op, Kind, path, ("path", path));
        if (backup is not null) rec.Data["backup"] = backup;
        records.Add(rec);
        return Task.FromResult<IReadOnlyList<UndoRecord>>(records);
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var path = record.Get("path");
        if (File.Exists(path)) File.Delete(path);
        var backup = record.Get("backup");
        if (c.Purpose == UndoPurpose.Rollback && backup.Length > 0 && File.Exists(backup)) File.Move(backup, path);
        return Task.CompletedTask;
    }
}

public sealed class WriteRegistryHandler : OperationHandler
{
    public override string Kind => OperationKinds.WriteRegistry;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Write registry {op.Str("root", "HKCU")}\\{op.Str("key")}\\{op.Str("name")}";

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        if (op.Str("root", "HKCU").Equals("HKLM", StringComparison.OrdinalIgnoreCase)) yield return "Writing to HKEY_LOCAL_MACHINE needs administrator rights.";
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        string? problem = null;
        try { Parse(c, op); } catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
    }

    private static (string Root, string Key, string Name, RegistryValue Value) Parse(OperationContext c, InstallOperationDefinition op)
    {
        var root = op.Str("root", "HKCU").ToUpperInvariant();
        var key = c.Expand(op.Str("key")).Trim('\\');
        if (key.Length == 0) throw new EngineException("The registry key is empty.");
        var type = op.Str("type", "string");
        var data = c.Expand(op.Str("value"));
        if (type == "dword" && !uint.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new EngineException($"'{data}' is not a valid DWORD value.");
        if (type == "qword" && !ulong.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) throw new EngineException($"'{data}' is not a valid QWORD value.");
        return (root, key, c.Expand(op.Str("name")), new RegistryValue(type, data));
    }

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var (root, key, name, value) = Parse(c, op);
        var rec = SetWithUndo(c, op.Id, op.ComponentId ?? "", root, key, name, value);
        return Task.FromResult<IReadOnlyList<UndoRecord>>(new[] { rec });
    }

    /// <summary>Writes a value and returns the record that undoes it. Used by the engine for Add/Remove Programs registration too.</summary>
    public static UndoRecord SetWithUndo(OperationContext c, string operationId, string componentId, string root, string key, string name, RegistryValue value)
    {
        var reg = c.System.Registry;
        var recordKey = $"{root}\\{key}\\{name}";
        var previous = reg.GetValue(root, key, name);
        var existing = c.FindExisting("writeRegistry", recordKey);
        var created = reg.CreateKey(root, key);
        reg.SetValue(root, key, name, value);
        var rec = new UndoRecord { Kind = "writeRegistry", OperationId = operationId, ComponentId = componentId, Key = recordKey };
        rec.Data["root"] = root; rec.Data["key"] = key; rec.Data["name"] = name; rec.Data["type"] = value.Type; rec.Data["data"] = value.Data;
        rec.Data["hadPrevious"] = previous is null ? "false" : "true";
        rec.Data["previousType"] = previous?.Type ?? ""; rec.Data["previousData"] = previous?.Data ?? "";
        rec.Data["createdKey"] = created || existing?.Flag("createdKey") == true ? "true" : "false";
        return rec;
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var reg = c.System.Registry;
        string root = record.Get("root"), key = record.Get("key"), name = record.Get("name");
        var current = reg.GetValue(root, key, name);
        if (c.Purpose == UndoPurpose.Rollback)
        {
            if (record.Flag("hadPrevious")) reg.SetValue(root, key, name, new RegistryValue(record.Get("previousType"), record.Get("previousData")));
            else reg.DeleteValue(root, key, name);
        }
        else if (current is not null)
        {
            if (current.Data == record.Get("data")) reg.DeleteValue(root, key, name);
            else c.Warn($"Kept registry value {root}\\{key}\\{name}: it was changed after installation.");
        }
        if (record.Flag("createdKey")) reg.DeleteKeyIfEmpty(root, key);
        return Task.CompletedTask;
    }
}

public sealed class SetEnvironmentVariableHandler : OperationHandler
{
    public override string Kind => OperationKinds.SetEnvironmentVariable;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"{op.Str("action", "set")} {op.Str("scope", "user")} variable {op.Str("name")}";

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        if (op.Str("scope", "user") == "machine") yield return "Changing machine environment variables needs administrator rights.";
    }

    private static string Normalize(string entry) => entry.Trim().TrimEnd('\\', '/').ToLowerInvariant();
    private static List<string> Split(string? value) => (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var scope = op.Str("scope", "user");
        var name = c.Expand(op.Str("name"));
        var value = c.Expand(op.Str("value"));
        var action = op.Str("action", "set");
        if (name.Length == 0 || name.Contains('=')) throw new EngineException($"'{name}' is not a valid environment variable name.");

        var current = c.System.Environment.Get(scope, name);
        string? updated = current;
        var applied = false;
        if (action == "set")
        {
            if (current != value) { updated = value; applied = true; }
        }
        else if (!Split(current).Any(e => Normalize(e) == Normalize(value)))
        {
            var list = Split(current);
            if (action == "prepend") list.Insert(0, value); else list.Add(value);
            updated = string.Join(';', list);
            applied = true;
        }
        if (applied) c.System.Environment.Set(scope, name, updated);

        var key = action == "set" ? $"{scope}|{name}" : $"{scope}|{name}|{Normalize(value)}";
        var existing = c.FindExisting(Kind, key);
        var rec = NewRecord(op, Kind, key,
            ("scope", scope), ("name", name), ("action", action), ("value", value),
            ("hadPrevious", current is null ? "false" : "true"), ("previous", current ?? ""), ("updated", updated ?? ""),
            ("applied", applied ? "true" : "false"), ("owned", applied || existing?.Flag("owned") == true ? "true" : "false"));
        return Task.FromResult<IReadOnlyList<UndoRecord>>(new[] { rec });
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var rollback = c.Purpose == UndoPurpose.Rollback;
        if (!(rollback ? record.Flag("applied") : record.Flag("owned"))) return Task.CompletedTask;

        string scope = record.Get("scope"), name = record.Get("name"), value = record.Get("value"), action = record.Get("action");
        var current = c.System.Environment.Get(scope, name);
        if (current is null) return Task.CompletedTask;

        if (action == "set")
        {
            if (rollback) c.System.Environment.Set(scope, name, record.Flag("hadPrevious") ? record.Get("previous") : null);
            else if (current == value) c.System.Environment.Set(scope, name, null);
            else c.Warn($"Kept environment variable {name}: it was changed after installation.");
            return Task.CompletedTask;
        }

        var remaining = Split(current).Where(e => Normalize(e) != Normalize(value)).ToList();
        if (remaining.Count == 0 && !record.Flag("hadPrevious")) c.System.Environment.Set(scope, name, null);
        else c.System.Environment.Set(scope, name, string.Join(';', remaining));
        return Task.CompletedTask;
    }
}

public sealed class RunCommandHandler : OperationHandler
{
    public override string Kind => OperationKinds.RunCommand;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Run {op.Str("command")} {op.Str("arguments")}".Trim();

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        string? problem = null;
        try { c.Expand(op.Str("command")); c.Expand(op.Str("arguments")); c.Expand(op.Str("uninstallCommand")); }
        catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
        if (op.Str("uninstallCommand").Length == 0 && op.Str("runOn", "install") == "install")
            yield return Warning("PAIC3104", op, "This command has no uninstall command, so it cannot be undone on rollback or uninstall.");
    }

    private static HashSet<int> SuccessCodes(string text)
    {
        var codes = new HashSet<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)) codes.Add(code);
        if (codes.Count == 0) codes.Add(0);
        return codes;
    }

    private static async Task RunAsync(SystemServices system, ILogger logger, string command, string args, string? workingDirectory, double timeoutSeconds, string successCodes, CancellationToken ct)
    {
        logger.LogInformation("Running {Command} {Arguments}", command, args);
        var result = await system.Processes.RunAsync(new ProcessSpec(command, args, string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory, TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))), ct);
        if (result.TimedOut) throw new EngineException($"'{command}' did not finish within {timeoutSeconds:0} seconds.");
        if (!SuccessCodes(successCodes).Contains(result.ExitCode)) throw new EngineException($"'{command}' failed with exit code {result.ExitCode}.");
    }

    public override async Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var runOn = op.Str("runOn", "install");
        var command = c.Expand(op.Str("command"));
        var args = c.Expand(op.Str("arguments"));
        var wd = op.Str("workingDirectory").Length > 0 ? c.Expand(op.Str("workingDirectory")) : "";
        var timeout = op.Num("timeoutSeconds", 300);
        var codes = op.Str("successCodes", "0");
        var ran = false;
        if (runOn == "install" && (!c.IsRepair || op.Flag("onRepair")))
        {
            await RunAsync(c.System, c.Logger, command, args, wd, timeout, codes, ct);
            ran = true;
        }
        var rec = NewRecord(op, Kind, op.Id,
            ("command", command), ("arguments", args), ("workingDirectory", wd), ("runOn", runOn),
            ("uninstallCommand", c.Expand(op.Str("uninstallCommand"))), ("uninstallArguments", c.Expand(op.Str("uninstallArguments"))),
            ("timeoutSeconds", timeout.ToString(CultureInfo.InvariantCulture)), ("successCodes", codes), ("ran", ran ? "true" : "false"));
        return new[] { rec };
    }

    public override async Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var timeout = double.TryParse(record.Get("timeoutSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : 300;
        var uninstall = record.Get("uninstallCommand");
        if (c.Purpose == UndoPurpose.Rollback)
        {
            if (!record.Flag("ran")) return;
            if (uninstall.Length == 0) { c.Warn($"Command '{record.Get("command")}' ran but has no uninstall command; its effects could not be undone."); return; }
            await RunAsync(c.System, c.Logger, uninstall, record.Get("uninstallArguments"), record.Get("workingDirectory"), timeout, "0", ct);
            return;
        }
        if (record.Get("runOn") == "uninstall")
            await RunAsync(c.System, c.Logger, record.Get("command"), record.Get("arguments"), record.Get("workingDirectory"), timeout, record.Get("successCodes"), ct);
        else if (uninstall.Length > 0)
            await RunAsync(c.System, c.Logger, uninstall, record.Get("uninstallArguments"), record.Get("workingDirectory"), timeout, "0", ct);
    }
}

public sealed class InstallServiceHandler : OperationHandler
{
    public override string Kind => OperationKinds.InstallService;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Install service {op.Str("name")}";

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        yield return "Installing a Windows service needs administrator rights.";
    }

    private static ServiceSpec Spec(OperationContext c, InstallOperationDefinition op)
    {
        var name = c.Expand(op.Str("name"));
        if (name.Length == 0 || name.IndexOfAny(new[] { '\\', '/' }) >= 0) throw new EngineException($"'{name}' is not a valid service name.");
        return new ServiceSpec(name, c.Expand(op.Str("displayName", name)), c.ResolvePath(op.Str("binaryPath"), allowOutside: true), c.Expand(op.Str("arguments")),
            op.Str("startType", "auto"), c.Expand(op.Str("account", "LocalSystem")), c.Expand(op.Str("description")));
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op)
    {
        string? problem = null;
        try { Spec(c, op); } catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
    }

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var spec = Spec(c, op);
        var existing = c.System.Services.Get(spec.Name);
        var owned = c.FindExisting(Kind, spec.Name);
        bool created;
        if (existing is null) { c.System.Services.Create(spec); created = true; }
        else if (owned is not null)
        {
            c.System.Services.Stop(spec.Name);
            c.System.Services.Reconfigure(spec);
            created = owned.Flag("created");
        }
        else throw new EngineException($"A service named '{spec.Name}' already exists and was not installed by this product.");

        var started = false;
        if (op.Flag("start", true)) { c.System.Services.Start(spec.Name); started = true; }
        return Task.FromResult<IReadOnlyList<UndoRecord>>(new[] { NewRecord(op, Kind, spec.Name, ("name", spec.Name), ("created", created ? "true" : "false"), ("started", started ? "true" : "false")) });
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var name = record.Get("name");
        if (c.System.Services.Get(name) is null) return Task.CompletedTask;
        try { c.System.Services.Stop(name); } catch (EngineException ex) { c.Warn($"Could not stop service '{name}': {ex.Message}"); }
        if (record.Flag("created")) c.System.Services.Delete(name);
        return Task.CompletedTask;
    }
}

public sealed class SetPermissionsHandler : OperationHandler
{
    public override string Kind => OperationKinds.SetPermissions;

    public override string Describe(OperationContext c, InstallOperationDefinition op) => $"Grant {op.Str("principal")} {op.Str("rights", "read")} on {op.Str("path")}";

    public override IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op)
    {
        yield return "Changing permissions needs administrator rights.";
    }

    public override IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op) => CheckPath(c, op, "path", op.Flag("allowOutside"));

    public override Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct)
    {
        var path = c.ResolvePath(op.Str("path"), op.Flag("allowOutside"));
        var principal = c.Expand(op.Str("principal"));
        var rights = op.Str("rights", "read");
        var applied = c.System.Permissions.AddRule(path, principal, rights);
        var key = $"{path}|{principal}|{rights}";
        var existing = c.FindExisting(Kind, key);
        return Task.FromResult<IReadOnlyList<UndoRecord>>(new[]
        {
            NewRecord(op, Kind, key, ("path", path), ("principal", principal), ("rights", rights),
                ("applied", applied ? "true" : "false"), ("owned", applied || existing?.Flag("owned") == true ? "true" : "false"))
        });
    }

    public override Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct)
    {
        var rollback = c.Purpose == UndoPurpose.Rollback;
        if (!(rollback ? record.Flag("applied") : record.Flag("owned"))) return Task.CompletedTask;
        var path = record.Get("path");
        if (File.Exists(path) || Directory.Exists(path)) c.System.Permissions.RemoveRule(path, record.Get("principal"), record.Get("rights"));
        return Task.CompletedTask;
    }
}
