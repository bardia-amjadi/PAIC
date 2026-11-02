using System.Globalization;
using Microsoft.Extensions.Logging;
using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine.Handlers;

public enum UndoPurpose { Rollback, Uninstall }

public static class OperationParameterExtensions
{
    public static string Str(this InstallOperationDefinition op, string name, string fallback = "") =>
        op.Parameters.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : fallback;

    public static bool Flag(this InstallOperationDefinition op, string name, bool fallback = false) =>
        op.Parameters.TryGetValue(name, out var v) && bool.TryParse(v, out var b) ? b : fallback;

    public static double Num(this InstallOperationDefinition op, string name, double fallback = 0) =>
        op.Parameters.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;
}

/// <summary>Everything a handler needs while running an operation.</summary>
public sealed class OperationContext
{
    public required InstallRequest Request { get; init; }
    public required SystemServices System { get; init; }
    public required ILogger Logger { get; init; }
    public required PathExpander Expander { get; init; }
    public required string InstallDir { get; init; }
    public required string StateDir { get; init; }
    public required string BackupDir { get; init; }
    public required bool PerUser { get; init; }
    public required bool IsRepair { get; init; }
    /// <summary>Records of the existing installation (empty for a fresh install); lets handlers recognise things they own.</summary>
    public IReadOnlyList<UndoRecord> ExistingItems { get; init; } = Array.Empty<UndoRecord>();
    public Action<double>? SubProgress { get; init; }

    public string Expand(string? text) => Expander.Expand(text);

    public UndoRecord? FindExisting(string kind, string key) =>
        ExistingItems.FirstOrDefault(r => r.Kind == kind && string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    public static bool IsInside(string directory, string path)
    {
        var d = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Expands and validates a path: never inside a protected system folder, and inside the install folder unless allowed.</summary>
    public string ResolvePath(string? raw, bool allowOutside)
    {
        var expanded = PathExpander.ToLocalPath(Expand(raw));
        if (string.IsNullOrWhiteSpace(expanded)) throw new EngineException("A path is empty.");
        if (!Path.IsPathRooted(expanded)) throw new EngineException($"'{expanded}' is not an absolute path.");
        var full = Path.GetFullPath(expanded);
        if (System.Paths.IsProtected(full)) throw new EngineException($"'{full}' is inside a protected system folder.");
        if (!allowOutside && !IsInside(InstallDir, full))
            throw new EngineException($"'{full}' is outside the install folder. Enable 'allow outside' on the operation if this is intended.");
        return full;
    }

    public string NewBackupPath()
    {
        Directory.CreateDirectory(BackupDir);
        return Path.Combine(BackupDir, Guid.NewGuid().ToString("N") + ".bak");
    }
}

public sealed class UndoContext
{
    public required SystemServices System { get; init; }
    public required ILogger Logger { get; init; }
    public required UndoPurpose Purpose { get; init; }
    public bool RemoveUserData { get; init; }
    public string? CurrentExecutable { get; init; }
    public List<string> DeferredDeletes { get; } = new();
    public List<string> Warnings { get; } = new();

    public void Warn(string message) { Warnings.Add(message); Logger.LogWarning("{Message}", message); }
}

/// <summary>Implements one operation kind: executing it, undoing it, and checking it before anything changes.</summary>
public abstract class OperationHandler
{
    public abstract string Kind { get; }

    /// <summary>Runs the operation and returns a record for every change that can be undone.</summary>
    public abstract Task<IReadOnlyList<UndoRecord>> ExecuteAsync(OperationContext c, InstallOperationDefinition op, CancellationToken ct);

    /// <summary>Reverses one record. Throws when the change cannot be reversed.</summary>
    public abstract Task UndoAsync(UndoContext c, UndoRecord record, CancellationToken ct);

    /// <summary>Checks the operation without changing anything.</summary>
    public virtual IEnumerable<ValidationIssue> Preflight(OperationContext c, InstallOperationDefinition op) => Enumerable.Empty<ValidationIssue>();

    public virtual string Describe(OperationContext c, InstallOperationDefinition op) => Kind;

    /// <summary>Reasons this operation needs administrator rights (empty when it does not).</summary>
    public virtual IEnumerable<string> ElevationReasons(OperationContext c, InstallOperationDefinition op) => Enumerable.Empty<string>();

    protected static UndoRecord NewRecord(InstallOperationDefinition op, string kind, string key, params (string Name, string Value)[] data)
    {
        var r = new UndoRecord { Kind = kind, OperationId = op.Id, ComponentId = op.ComponentId ?? "", Key = key };
        foreach (var (n, v) in data) r.Data[n] = v;
        return r;
    }

    protected static ValidationIssue Error(string code, InstallOperationDefinition op, string message) =>
        new(code, ValidationSeverity.Error, message, $"operations[{op.Id}]");

    protected static ValidationIssue Warning(string code, InstallOperationDefinition op, string message) =>
        new(code, ValidationSeverity.Warning, message, $"operations[{op.Id}]");

    /// <summary>Preflight helper: reports unknown variables and unsafe paths for a path-like parameter.</summary>
    protected IEnumerable<ValidationIssue> CheckPath(OperationContext c, InstallOperationDefinition op, string parameter, bool allowOutside)
    {
        var raw = op.Str(parameter);
        if (raw.Length == 0) yield break;
        string? problem = null;
        try { c.ResolvePath(raw, allowOutside); }
        catch (EngineException ex) { problem = ex.Message; }
        if (problem is not null) yield return Error("PAIC3101", op, problem);
    }
}

public sealed class OperationHandlerRegistry
{
    private readonly Dictionary<string, OperationHandler> _handlers = new(StringComparer.Ordinal);

    public OperationHandlerRegistry(IEnumerable<OperationHandler>? handlers = null)
    {
        foreach (var h in handlers ?? DefaultHandlers()) Register(h);
    }

    public static IEnumerable<OperationHandler> DefaultHandlers() => new OperationHandler[]
    {
        new CopyFilesHandler(), new DeleteFilesHandler(), new CreateDirectoryHandler(), new CreateShortcutHandler(),
        new WriteRegistryHandler(), new SetEnvironmentVariableHandler(), new RunCommandHandler(), new InstallServiceHandler(), new SetPermissionsHandler()
    };

    public void Register(OperationHandler handler) => _handlers[handler.Kind] = handler;
    public bool TryGet(string kind, out OperationHandler handler) => _handlers.TryGetValue(kind ?? "", out handler!);
    public OperationHandler Get(string kind) =>
        _handlers.TryGetValue(kind ?? "", out var h) ? h : throw new EngineException($"There is no handler for operation kind '{kind}'.");
}
