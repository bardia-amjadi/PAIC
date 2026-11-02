using System.Globalization;
using Microsoft.Extensions.Logging;
using PAIC.Controls;
using PAIC.Core.IO;
using PAIC.Core.Validation;
using PAIC.InstallerEngine.Handlers;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Validation;

namespace PAIC.InstallerEngine;

/// <summary>
/// Runs installations as transactions: every change is journalled with the information needed to undo it, so a failure, a cancellation
/// or a crash rolls everything back, and the same records drive uninstall, repair and modify.
/// </summary>
public sealed class InstallEngine
{
    public const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private readonly SystemServices _system;
    private readonly ILogger _logger;
    private readonly OperationHandlerRegistry _handlers;
    private readonly PrerequisiteManager _prerequisites;

    public InstallEngine(SystemServices system, ILogger logger, OperationHandlerRegistry? handlers = null)
    {
        _system = system;
        _logger = logger;
        _handlers = handlers ?? new OperationHandlerRegistry();
        _prerequisites = new PrerequisiteManager(system, logger);
    }

    public static string ProductKey(PaicProject project) => "{" + project.Metadata.Id.ToString().ToUpperInvariant() + "}";

    // ================================================================= detection

    /// <summary>Finds an existing installation of the project's product through its Add/Remove Programs entry.</summary>
    public InstallationInfo? FindInstallation(PaicProject project)
    {
        var key = $@"{UninstallRegistryPath}\{ProductKey(project)}";
        foreach (var root in new[] { "HKCU", "HKLM" })
        {
            var location = _system.Registry.GetValue(root, key, "InstallLocation");
            if (location is null || string.IsNullOrWhiteSpace(location.Data)) continue;
            InstallState? state = null;
            try { state = new StateStore(StateStore.StateDirFor(location.Data)).Load(); } catch (EngineException) { }
            return new InstallationInfo
            {
                InstallDir = location.Data, PerUser = root == "HKCU",
                Version = _system.Registry.GetValue(root, key, "DisplayVersion")?.Data ?? state?.Version ?? "", State = state
            };
        }
        return null;
    }

    public InstallState? LoadState(string installDir) => new StateStore(StateStore.StateDirFor(Path.GetFullPath(installDir))).Load();

    // ================================================================= preparation

    private sealed class Prepared
    {
        public required PaicProject Project { get; init; }
        public required InstallMode Mode { get; init; }
        public required string InstallDir { get; init; }
        public required string StateDir { get; init; }
        public required bool PerUser { get; init; }
        public required PathExpander Expander { get; init; }
        public required Dictionary<string, string> Variables { get; init; }
        public required List<string> Components { get; init; }
        public required List<InstallOperationDefinition> Operations { get; init; }
        public InstallationInfo? ExistingInfo { get; init; }
        public InstallState? Existing { get; init; }
        public bool IsUpgrade { get; init; }
        public bool AlreadyInstalled { get; init; }
        public List<string> AddedComponents { get; init; } = new();
        public List<string> RemovedComponents { get; init; } = new();
    }

    private Prepared Prepare(InstallRequest req)
    {
        var project = req.Project;
        var perUser = project.Installation.PerUser;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in req.Variables) vars[k] = v;
        vars["ProductName"] = project.Application.ProductName;
        vars["Version"] = project.Application.Version;
        vars["Publisher"] = project.Application.Publisher;
        vars["ProductId"] = ProductKey(project);

        var existingInfo = FindInstallation(project);
        var raw = req.InstallDir ?? existingInfo?.InstallDir ?? project.Installation.DefaultInstallDirectory;
        var installDir = Path.GetFullPath(PathExpander.ToLocalPath(new PathExpander(vars, _system.Paths.ExpandEnvironment).Expand(raw)));
        if (existingInfo is not null && req.InstallDir is not null &&
            !string.Equals(Path.GetFullPath(existingInfo.InstallDir).TrimEnd('\\', '/'), installDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new EngineException($"{project.Application.ProductName} is already installed in '{existingInfo.InstallDir}'. Uninstall it first or omit the install folder.");
        if (existingInfo is null)
        {
            // an installation whose registry entry is gone can still be found by its state file
            var state = new StateStore(StateStore.StateDirFor(installDir)).Load();
            if (state is not null && state.ProductId == ProductKey(project))
                existingInfo = new InstallationInfo { InstallDir = installDir, Version = state.Version, PerUser = state.PerUser, State = state };
        }
        vars["InstallDir"] = installDir;
        var existing = existingInfo?.State;
        if (installDir.Length == 0 || Path.GetPathRoot(installDir)?.TrimEnd('\\', '/').Equals(installDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) == true)
            throw new EngineException("The install folder must not be the root of a drive.");
        if (_system.Paths.IsProtected(installDir)) throw new EngineException($"'{installDir}' is inside a protected system folder.");

        if (req.Mode is InstallMode.Repair or InstallMode.Modify && existing is null)
            throw new EngineException($"{project.Application.ProductName} is not installed, so it cannot be {(req.Mode == InstallMode.Repair ? "repaired" : "modified")}.");

        var isUpgrade = false;
        var already = false;
        if (req.Mode == InstallMode.Install && existing is not null)
        {
            var oldV = VersionParser.Parse(existing.Version); var newV = VersionParser.Parse(project.Application.Version);
            var cmp = oldV is not null && newV is not null ? newV.CompareTo(oldV) : string.CompareOrdinal(project.Application.Version, existing.Version);
            if (cmp == 0) already = true;
            else if (cmp < 0 && !req.AllowDowngrade) throw new EngineException($"Version {existing.Version} is already installed; installing the older version {project.Application.Version} requires allowing a downgrade.");
            else isUpgrade = cmp > 0;
        }

        // components
        var known = project.Components.Select(c => c.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var requested = req.Components;
        if (requested is null && req.Mode != InstallMode.Install && existing is not null) requested = existing.Components;
        var selected = new List<string>();
        foreach (var comp in project.Components)
        {
            var wanted = comp.Required || (requested is null ? comp.DefaultSelected : requested.Contains(comp.Id, StringComparer.OrdinalIgnoreCase));
            if (wanted) selected.Add(comp.Id);
        }
        if (requested is not null)
            foreach (var id in requested.Where(r => !known.Contains(r)))
                throw new EngineException($"Unknown component '{id}'.");

        var oldComponents = existing?.Components ?? new List<string>();
        var added = req.Mode == InstallMode.Modify ? selected.Where(s => !oldComponents.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList() : new List<string>();
        var removed = req.Mode == InstallMode.Modify ? oldComponents.Where(o => !selected.Contains(o, StringComparer.OrdinalIgnoreCase)).ToList() : new List<string>();

        bool IncludeComponent(InstallOperationDefinition op)
        {
            var id = op.ComponentId ?? "";
            if (req.Mode == InstallMode.Modify) return id.Length > 0 && added.Contains(id, StringComparer.OrdinalIgnoreCase);
            return id.Length == 0 || project.Components.Count == 0 || selected.Contains(id, StringComparer.OrdinalIgnoreCase);
        }
        var ops = project.Operations.Where(o => IncludeComponent(o) && ConditionEvaluator.Evaluate(o.Condition, vars)).ToList();

        return new Prepared
        {
            Project = project, Mode = req.Mode, InstallDir = installDir, StateDir = StateStore.StateDirFor(installDir), PerUser = perUser,
            Expander = new PathExpander(vars, _system.Paths.ExpandEnvironment), Variables = vars, Components = selected, Operations = ops,
            ExistingInfo = existingInfo, Existing = existing, IsUpgrade = isUpgrade, AlreadyInstalled = already, AddedComponents = added, RemovedComponents = removed
        };
    }

    private OperationContext MakeContext(Prepared p, InstallRequest req, Action<double>? sub = null) => new()
    {
        Request = req, System = _system, Logger = _logger, Expander = p.Expander, InstallDir = p.InstallDir, StateDir = p.StateDir,
        BackupDir = new StateStore(p.StateDir).BackupDir, PerUser = p.PerUser, IsRepair = p.Mode == InstallMode.Repair,
        ExistingItems = p.Existing?.Items ?? new List<UndoRecord>(), SubProgress = sub
    };

    private List<string> ElevationReasons(Prepared p, OperationContext ctx)
    {
        var reasons = new List<string>();
        if (!p.PerUser) reasons.Add("Installing for all users needs administrator rights.");
        if (_system.Paths.RequiresElevation(p.InstallDir)) reasons.Add($"Writing to '{p.InstallDir}' needs administrator rights.");
        foreach (var op in p.Operations)
            if (_handlers.TryGet(op.Kind, out var h))
                reasons.AddRange(h.ElevationReasons(ctx, op));
        return reasons.Distinct().ToList();
    }

    // ================================================================= plan (no changes)

    /// <summary>Describes what an install would do without changing anything.</summary>
    public async Task<InstallPlan> PlanAsync(InstallRequest req, CancellationToken ct = default)
    {
        var plan = new InstallPlan { Mode = req.Mode };
        Prepared p;
        try { p = Prepare(req); }
        catch (EngineException ex) { plan.Issues.Add(new ValidationIssue("PAIC3100", ValidationSeverity.Error, ex.Message)); return plan; }

        plan.InstallDir = p.InstallDir; plan.ExistingVersion = p.Existing?.Version; plan.IsUpgrade = p.IsUpgrade;
        plan.Components.AddRange(p.Components);
        if (p.AlreadyInstalled) plan.Issues.Add(new ValidationIssue("PAIC3105", ValidationSeverity.Info, $"Version {p.Existing!.Version} is already installed; nothing to do (use repair to restore files)."));

        foreach (var issue in ProjectValidator.CreateDefault().Validate(new ProjectValidationContext(req.Project, null)).Issues)
            if (issue.Severity == ValidationSeverity.Error && issue.Location is not null && (issue.Location.StartsWith("operations[") || issue.Location.StartsWith("prerequisites[")))
                plan.Issues.Add(issue);

        var ctx = MakeContext(p, req);
        foreach (var op in p.Operations)
        {
            if (!_handlers.TryGet(op.Kind, out var handler))
            {
                plan.Issues.Add(new ValidationIssue("PAIC3100", ValidationSeverity.Error, $"There is no handler for operation kind '{op.Kind}'.", $"operations[{op.Id}]"));
                continue;
            }
            string description;
            try { description = handler.Describe(ctx, op); } catch (EngineException ex) { description = ex.Message; }
            plan.Steps.Add(new PlannedStep(op.Id, op.Kind, op.ComponentId ?? "", description));
            plan.Issues.AddRange(handler.Preflight(ctx, op));
        }
        plan.ElevationReasons.AddRange(ElevationReasons(p, ctx));

        if (p.Mode != InstallMode.Modify && req.Project.Prerequisites.Count > 0)
        {
            try
            {
                plan.Prerequisites = await _prerequisites.EvaluateAsync(req.Project.Prerequisites, ct);
                foreach (var s in plan.Prerequisites.Where(s => !s.IsSatisfied))
                {
                    var blocking = !s.Definition.Optional && (!req.InstallPrerequisites || string.IsNullOrWhiteSpace(s.Definition.DownloadUrl) || s.State == PrerequisiteState.VersionTooHigh);
                    plan.Issues.Add(new ValidationIssue("PAIC3110", blocking ? ValidationSeverity.Error : ValidationSeverity.Warning,
                        s.Message + (blocking ? "" : " It will be installed."), $"prerequisites[{s.Definition.Id}]"));
                }
            }
            catch (EngineException ex) { plan.Issues.Add(new ValidationIssue("PAIC3111", ValidationSeverity.Error, ex.Message)); }
        }
        return plan;
    }

    // ================================================================= install / upgrade / repair / modify

    public async Task<InstallResult> InstallAsync(InstallRequest req, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new InstallResult { Operation = req.Mode.ToString().ToLowerInvariant() };
        Prepared p;
        try { p = Prepare(req); }
        catch (EngineException ex) { result.Error = ex.Message; return result; }

        result.InstallDir = p.InstallDir;
        result.Version = req.Project.Application.Version;
        if (p.IsUpgrade) result.Operation = "upgrade";
        if (p.AlreadyInstalled)
        {
            result.Success = true; result.AlreadyInstalled = true;
            return result;
        }

        var ctx = MakeContext(p, req);
        var reasons = ElevationReasons(p, ctx);
        if (reasons.Count > 0 && !_system.IsElevated())
        {
            result.ElevationRequired = true;
            result.Error = "Administrator rights are required. " + reasons[0];
            return result;
        }

        using var guard = _system.Locks.TryAcquire(ProductKey(req.Project));
        if (guard is null) { result.Error = "Another setup for this product is already running."; return result; }

        var recovery = await RecoverAsync(p.InstallDir, CancellationToken.None);
        result.Warnings.AddRange(recovery.Warnings);

        if (p.Mode != InstallMode.Modify && req.Project.Prerequisites.Count > 0)
        {
            try
            {
                var statuses = req.InstallPrerequisites
                    ? await _prerequisites.InstallMissingAsync(req.Project.Prerequisites, progress, ct)
                    : await _prerequisites.EvaluateAsync(req.Project.Prerequisites, ct);
                result.Prerequisites = statuses;
                var failed = statuses.Where(s => !s.IsSatisfied).ToList();
                foreach (var s in failed.Where(s => s.Definition.Optional)) result.Warnings.Add(s.Message);
                var blocking = failed.Where(s => !s.Definition.Optional).ToList();
                if (blocking.Count > 0)
                {
                    result.PrerequisitesFailed = true;
                    result.Error = "Prerequisites are not satisfied: " + string.Join(" ", blocking.Select(s => s.Message));
                    WriteHistory(p, "prerequisites", "failed", result.Error);
                    return result;
                }
            }
            catch (OperationCanceledException) { result.Cancelled = true; result.Error = "The installation was cancelled."; return result; }
            catch (EngineException ex) { result.Error = ex.Message; return result; }
        }

        return await RunTransactionAsync(p, req, result, progress, ct);
    }

    private async Task<InstallResult> RunTransactionAsync(Prepared p, InstallRequest req, InstallResult result, IProgress<InstallProgress>? progress, CancellationToken ct)
    {
        var store = new StateStore(p.StateDir);
        var journalPath = TransactionJournal.PathFor(p.StateDir);
        var records = new List<UndoRecord>();
        var engineOp = new InstallOperationDefinition { Id = "engine" };
        var journal = new TransactionJournal(journalPath);
        var started = DateTimeOffset.UtcNow;

        try
        {
            progress?.Report(new InstallProgress("Preparing", 0, "Preparing installation..."));
            var dirRecords = new List<UndoRecord>();
            CreateDirectoryHandler.EnsureDirectory(p.InstallDir, engineOp, dirRecords);
            CreateDirectoryHandler.EnsureDirectory(p.StateDir, engineOp, dirRecords);
            if (Directory.Exists(store.BackupDir)) Directory.Delete(store.BackupDir, recursive: true);
            journal.Begin(new JournalHeader(Guid.NewGuid().ToString("N"), p.Mode.ToString(), req.Project.Application.Version, started, p.InstallDir, p.Existing?.Version));
            foreach (var r in dirRecords) { journal.Append(r); records.Add(r); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Error = $"Could not prepare the install folder: {ex.Message}";
            await RollbackAsync(p, records, result);
            return result;
        }

        try
        {
            var total = p.Operations.Count + 2;
            for (var i = 0; i < p.Operations.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var op = p.Operations[i];
                var handler = _handlers.Get(op.Kind);
                var index = i;
                var ctx = MakeContext(p, req, sub => progress?.Report(new InstallProgress("Installing", 100d * (index + sub) / total, handler.Describe(MakeContext(p, req), op))));
                string description; try { description = handler.Describe(ctx, op); } catch (EngineException) { description = op.Kind; }
                progress?.Report(new InstallProgress("Installing", 100d * i / total, description));
                _logger.LogInformation("Operation {Id} ({Kind})", op.Id, op.Kind);
                foreach (var r in await handler.ExecuteAsync(ctx, op, ct)) { journal.Append(r); records.Add(r); }
            }

            ct.ThrowIfCancellationRequested();
            progress?.Report(new InstallProgress("Finalizing", 100d * p.Operations.Count / total, "Registering the installation..."));
            var finalContext = MakeContext(p, req);
            var uninstaller = await ProvisionUninstallerAsync(p, journal, records, ct);
            RegisterWithWindows(p, req, finalContext, uninstaller, records, journal);

            // ---- build the new state
            var now = DateTimeOffset.UtcNow;
            var state = new InstallState
            {
                ProductId = ProductKey(req.Project), ProductName = req.Project.Application.ProductName, Publisher = req.Project.Application.Publisher,
                Version = req.Project.Application.Version, InstallDir = p.InstallDir, PerUser = p.PerUser,
                InstalledUtc = p.Existing?.InstalledUtc ?? now, UpdatedUtc = now, Components = p.Components,
                UninstallerPath = uninstaller ?? ""
            };
            var staleItems = Merge(p, records, state);

            journal.Commit(state);
            store.Save(state);
            CleanupTransaction(store, journalPath);
            progress?.Report(new InstallProgress("Done", 100, "Installation complete."));

            // ---- best-effort removal of things the previous version installed that this one no longer does
            if (staleItems.Count > 0)
            {
                var undo = new UndoContext { System = _system, Logger = _logger, Purpose = UndoPurpose.Uninstall };
                foreach (var item in Enumerable.Reverse(staleItems))
                    await UndoItemAsync(undo, item, CancellationToken.None);
                result.Warnings.AddRange(undo.Warnings);
            }

            result.Success = true;
            WriteHistory(p, result.Operation, "success", $"Version {result.Version}");
            _logger.LogInformation("{Operation} of {Product} {Version} completed", result.Operation, req.Project.Application.ProductName, result.Version);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Error = "The installation was cancelled.";
            await RollbackAsync(p, records, result);
            WriteHistory(p, result.Operation, "cancelled", "Rolled back");
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogError(ex, "Installation failed");
            result.Error = ex is EngineException or IOException or UnauthorizedAccessException ? ex.Message : $"Unexpected error: {ex.Message}";
            await RollbackAsync(p, records, result);
            WriteHistory(p, result.Operation, "failed", result.Error);
            return result;
        }
    }

    private async Task<string?> ProvisionUninstallerAsync(Prepared p, TransactionJournal journal, List<UndoRecord> records, CancellationToken ct)
    {
        var existedBefore = p.Existing is { UninstallerPath.Length: > 0 } && File.Exists(p.Existing.UninstallerPath);
        var path = await _system.Uninstaller.ProvisionAsync(p.StateDir, ct);
        if (path is null) return null;
        var rec = new UndoRecord { Kind = "copyFiles", OperationId = "engine", Key = path };
        rec.Data["path"] = path; rec.Data["sha256"] = await FileHashing.ComputeSha256Async(path, ct);
        rec.Data["adopted"] = existedBefore ? "true" : "false"; rec.Data["userData"] = "false"; rec.Data["internal"] = "true";
        journal.Append(rec); records.Add(rec);
        return path;
    }

    private void RegisterWithWindows(Prepared p, InstallRequest req, OperationContext ctx, string? uninstaller, List<UndoRecord> records, TransactionJournal journal)
    {
        var root = p.PerUser ? "HKCU" : "HKLM";
        var key = $@"{UninstallRegistryPath}\{ProductKey(req.Project)}";
        long sizeBytes = records.Where(r => r.Kind == "copyFiles").Sum(r => long.TryParse(r.Get("size"), out var s) ? s : 0);
        var app = req.Project.Application;
        var values = new List<(string Name, RegistryValue Value)>
        {
            ("DisplayName", new("string", app.ProductName)), ("DisplayVersion", new("string", app.Version)), ("Publisher", new("string", app.Publisher)),
            ("InstallLocation", new("string", p.InstallDir)), ("InstallDate", new("string", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture))),
            ("EstimatedSize", new("dword", Math.Max(1, sizeBytes / 1024).ToString(CultureInfo.InvariantCulture))),
            ("NoModify", new("dword", "1")), ("NoRepair", new("dword", "1"))
        };
        if (!string.IsNullOrWhiteSpace(app.Website)) values.Add(("URLInfoAbout", new("string", app.Website)));
        if (uninstaller is not null)
        {
            values.Add(("UninstallString", new("string", $"\"{uninstaller}\" uninstall")));
            values.Add(("QuietUninstallString", new("string", $"\"{uninstaller}\" uninstall --silent")));
        }
        foreach (var (name, value) in values)
        {
            var rec = WriteRegistryHandler.SetWithUndo(ctx, "engine", "", root, key, name, value);
            journal.Append(rec); records.Add(rec);
        }
    }

    /// <summary>Combines the old and new records into the state's item list; returns old items that must be removed afterwards.</summary>
    private static List<UndoRecord> Merge(Prepared p, List<UndoRecord> journalRecords, InstallState state)
    {
        var fresh = journalRecords.Select(r => { var c = new UndoRecord { Id = r.Id, Kind = r.Kind, OperationId = r.OperationId, ComponentId = r.ComponentId, Key = r.Key, Data = new Dictionary<string, string>(r.Data) }; c.Data.Remove("backup"); return c; }).ToList();
        var freshKeys = fresh.Select(r => r.Identity).ToHashSet();
        var old = p.Existing?.Items ?? new List<UndoRecord>();
        var stale = new List<UndoRecord>();
        var kept = new List<UndoRecord>();

        if (p.Mode == InstallMode.Modify)
        {
            foreach (var item in old)
            {
                if (freshKeys.Contains(item.Identity)) continue;
                if (item.ComponentId.Length > 0 && p.RemovedComponents.Contains(item.ComponentId, StringComparer.OrdinalIgnoreCase)) stale.Add(item);
                else kept.Add(item);
            }
        }
        else
        {
            foreach (var item in old)
            {
                if (freshKeys.Contains(item.Identity)) continue;
                if (item.Kind == "createDirectory") kept.Add(item);   // folders stay tracked so uninstall can remove them when empty
                else stale.Add(item);
            }
        }
        state.Items = kept.Concat(fresh).ToList();
        return stale;
    }

    // ================================================================= rollback & recovery

    private async Task RollbackAsync(Prepared p, IReadOnlyList<UndoRecord> records, InstallResult result)
    {
        var failures = await RollbackRecordsAsync(p.StateDir, records);
        result.RolledBack = true;
        result.RollbackFailures.AddRange(failures);
        if (failures.Count > 0) result.Warnings.Add("Some changes could not be undone; see the log.");
    }

    private async Task<List<string>> RollbackRecordsAsync(string stateDir, IReadOnlyList<UndoRecord> records)
    {
        var failures = new List<string>();
        var undo = new UndoContext { System = _system, Logger = _logger, Purpose = UndoPurpose.Rollback };
        _logger.LogWarning("Rolling back {Count} change(s)", records.Count);
        for (var i = records.Count - 1; i >= 0; i--)
        {
            try { await UndoItemAsync(undo, records[i], CancellationToken.None, throwOnError: true); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var message = $"{records[i].Kind} {records[i].Key}: {ex.Message}";
                failures.Add(message);
                _logger.LogError(ex, "Rollback step failed: {Step}", message);
            }
        }
        failures.AddRange(undo.Warnings);

        var store = new StateStore(stateDir);
        var journalPath = TransactionJournal.PathFor(stateDir);
        try
        {
            if (File.Exists(journalPath)) new TransactionJournal(journalPath).MarkRolledBack();
            if (failures.Count == 0) CleanupTransaction(store, journalPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _logger.LogWarning(ex, "Could not clean up the transaction files"); }

        // folders created by the transaction can only be removed once the journal is gone
        var dirUndo = new UndoContext { System = _system, Logger = _logger, Purpose = UndoPurpose.Rollback };
        foreach (var r in records.Where(r => r.Kind == "createDirectory").Reverse()) await UndoItemAsync(dirUndo, r, CancellationToken.None);
        return failures;
    }

    private static void CleanupTransaction(StateStore store, string journalPath)
    {
        if (File.Exists(journalPath)) File.Delete(journalPath);
        if (Directory.Exists(store.BackupDir)) Directory.Delete(store.BackupDir, recursive: true);
    }

    private async Task UndoItemAsync(UndoContext undo, UndoRecord record, CancellationToken ct, bool throwOnError = false)
    {
        if (!_handlers.TryGet(record.Kind, out var handler))
        {
            undo.Warn($"No handler can undo '{record.Kind}' ({record.Key}).");
            return;
        }
        try { await handler.UndoAsync(undo, record, ct); }
        catch (Exception ex) when (!throwOnError && ex is not OutOfMemoryException)
        {
            undo.Warn($"Could not undo {record.Kind} '{record.Key}': {ex.Message}");
        }
    }

    /// <summary>Finishes or undoes a transaction that was interrupted (crash, power loss, killed process).</summary>
    public async Task<RecoveryResult> RecoverAsync(string installDir, CancellationToken ct = default)
    {
        var result = new RecoveryResult();
        var stateDir = StateStore.StateDirFor(Path.GetFullPath(installDir));
        var journalPath = TransactionJournal.PathFor(stateDir);
        JournalContents? journal;
        try { journal = TransactionJournal.Read(journalPath); }
        catch (IOException ex) { result.Warnings.Add($"Could not read the transaction journal: {ex.Message}"); return result; }
        if (journal is null) return result;

        result.Found = true;
        var store = new StateStore(stateDir);
        try
        {
            if (journal.Committed)
            {
                store.Save(journal.CommittedState!);
                CleanupTransaction(store, journalPath);
                result.Finalized = true;
                _logger.LogInformation("Finalized an interrupted installation (it had already committed).");
            }
            else if (journal.RolledBack)
            {
                if (File.Exists(journalPath)) File.Delete(journalPath);
            }
            else
            {
                _logger.LogWarning("Found an interrupted installation; rolling it back.");
                var failures = await RollbackRecordsAsync(stateDir, journal.Records);
                result.RolledBack = true;
                result.Warnings.AddRange(failures);
                var entry = new HistoryEntry { Action = "recovery", Version = journal.Header?.Version ?? "", Result = failures.Count == 0 ? "rolled back" : "rolled back with problems", InstallDir = installDir };
                HistoryLog.Append(_system.Paths.LogDirectory(false, "recovery"), entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Warnings.Add($"Recovery could not finish: {ex.Message}");
        }
        return result;
    }

    // ================================================================= uninstall

    public async Task<InstallResult> UninstallAsync(UninstallRequest req, IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new InstallResult { Operation = "uninstall" };
        var installDir = Path.GetFullPath(req.InstallDir);
        result.InstallDir = installDir;
        var store = new StateStore(StateStore.StateDirFor(installDir));

        var recovery = await RecoverAsync(installDir, CancellationToken.None);
        result.Warnings.AddRange(recovery.Warnings);

        InstallState? state;
        try { state = store.Load(); } catch (EngineException ex) { result.Error = ex.Message; return result; }
        if (state is null) { result.Error = $"No installation was found in '{installDir}'."; return result; }
        result.Version = state.Version;

        if (!state.PerUser && !_system.IsElevated())
        {
            result.ElevationRequired = true;
            result.Error = "Administrator rights are required to uninstall for all users.";
            return result;
        }
        using var guard = _system.Locks.TryAcquire(state.ProductId);
        if (guard is null) { result.Error = "Another setup for this product is already running."; return result; }

        var undo = new UndoContext { System = _system, Logger = _logger, Purpose = UndoPurpose.Uninstall, RemoveUserData = req.RemoveUserData, CurrentExecutable = req.CurrentExecutable };
        var items = state.Items.ToList();
        try
        {
            for (var i = items.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new InstallProgress("Uninstalling", 100d * (items.Count - 1 - i) / Math.Max(1, items.Count), $"Removing {items[i].Kind}..."));
                await UndoItemAsync(undo, items[i], ct);
            }
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true; result.Error = "The uninstall was cancelled; the product may be partly removed. Run uninstall again to finish.";
            result.Warnings.AddRange(undo.Warnings);
            WriteHistory(state, "uninstall", "cancelled", "Partly removed");
            return result;
        }

        // bookkeeping goes last, then folders that are now empty
        if (File.Exists(store.StatePath)) File.Delete(store.StatePath);
        var journalPath = TransactionJournal.PathFor(store.StateDir);
        if (File.Exists(journalPath)) File.Delete(journalPath);
        if (Directory.Exists(store.BackupDir)) Directory.Delete(store.BackupDir, recursive: true);
        var dirUndo = new UndoContext { System = _system, Logger = _logger, Purpose = UndoPurpose.Uninstall };
        foreach (var r in items.Where(r => r.Kind == "createDirectory").AsEnumerable().Reverse()) await UndoItemAsync(dirUndo, r, CancellationToken.None);

        result.DeferredDeletes.AddRange(undo.DeferredDeletes);
        if (result.DeferredDeletes.Count > 0) _system.Uninstaller.ScheduleDeletion(result.DeferredDeletes, installDir);
        result.Warnings.AddRange(undo.Warnings);
        result.Warnings.AddRange(dirUndo.Warnings);
        result.Success = true;
        progress?.Report(new InstallProgress("Done", 100, "Uninstall complete."));
        WriteHistory(state, "uninstall", "success", $"Version {state.Version}");
        _logger.LogInformation("Uninstalled {Product} {Version}", state.ProductName, state.Version);
        return result;
    }

    // ================================================================= verification

    /// <summary>Checks that installed files and shortcuts still exist and match what was installed.</summary>
    public async Task<VerifyReport> VerifyAsync(string installDir, CancellationToken ct = default)
    {
        var state = LoadState(installDir) ?? throw new EngineException($"No installation was found in '{installDir}'.");
        var report = new VerifyReport();
        foreach (var item in state.Items)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Kind == "copyFiles")
            {
                report.Checked++;
                var path = item.Get("path");
                if (!File.Exists(path)) { report.Missing.Add(path); continue; }
                var expected = item.Get("sha256");
                if (expected.Length > 0 && !(await FileHashing.ComputeSha256Async(path, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase)) report.Modified.Add(path);
            }
            else if (item.Kind == "createShortcut")
            {
                report.Checked++;
                if (!File.Exists(item.Get("path"))) report.Missing.Add(item.Get("path"));
            }
        }
        return report;
    }

    // ================================================================= history

    private void WriteHistory(Prepared p, string action, string outcome, string message) =>
        HistoryLog.Append(_system.Paths.LogDirectory(p.PerUser, ProductKey(p.Project)),
            new HistoryEntry { Action = action, Version = p.Project.Application.Version, Result = outcome, Message = message, InstallDir = p.InstallDir });

    private void WriteHistory(InstallState s, string action, string outcome, string message) =>
        HistoryLog.Append(_system.Paths.LogDirectory(s.PerUser, s.ProductId),
            new HistoryEntry { Action = action, Version = s.Version, Result = outcome, Message = message, InstallDir = s.InstallDir });
}
