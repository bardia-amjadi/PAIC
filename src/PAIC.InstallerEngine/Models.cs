using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;

namespace PAIC.InstallerEngine;

/// <summary>One reversible change made by an operation. The same record drives rollback and uninstall.</summary>
public sealed class UndoRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string OperationId { get; set; } = "";
    public string ComponentId { get; set; } = "";
    /// <summary>Identity of the thing changed (a path, a registry value, a variable name). Used to match records across versions.</summary>
    public string Key { get; set; } = "";
    public Dictionary<string, string> Data { get; set; } = new();

    public string Identity => Kind + "|" + Key.ToLowerInvariant();
    public string Get(string name) => Data.TryGetValue(name, out var v) ? v : "";
    public bool Flag(string name) => Data.TryGetValue(name, out var v) && v == "true";
}

/// <summary>What is installed, persisted in the install folder's .paic directory.</summary>
public sealed class InstallState
{
    public int SchemaVersion { get; set; } = 1;
    public string ProductId { get; set; } = "";
    public string ProductName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public bool PerUser { get; set; }
    public DateTimeOffset InstalledUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    public List<string> Components { get; set; } = new();
    public List<UndoRecord> Items { get; set; } = new();
    public string UninstallerPath { get; set; } = "";
}

public enum InstallMode { Install, Repair, Modify }

public sealed class InstallRequest
{
    public required PaicProject Project { get; init; }
    public required IPayloadProvider Payload { get; init; }
    /// <summary>Null = the existing installation's folder, or the project's default.</summary>
    public string? InstallDir { get; init; }
    /// <summary>Null = the default selection (required and default-selected components). Required components are always included.</summary>
    public IReadOnlyCollection<string>? Components { get; init; }
    /// <summary>User-supplied installer variables (for example answers from wizard pages).</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();
    public InstallMode Mode { get; init; } = InstallMode.Install;
    /// <summary>When false, missing prerequisites fail the install instead of being installed.</summary>
    public bool InstallPrerequisites { get; init; } = true;
    public bool AllowDowngrade { get; init; }
}

public sealed class UninstallRequest
{
    public required string InstallDir { get; init; }
    /// <summary>Explicit consent to delete files marked as user data (and files modified since installation).</summary>
    public bool RemoveUserData { get; init; }
    /// <summary>The running executable; it cannot delete itself, so it is reported in <see cref="InstallResult.DeferredDeletes"/>.</summary>
    public string? CurrentExecutable { get; init; }
    /// <summary>Components to remove (modify). Null = remove the whole product.</summary>
    public IReadOnlyCollection<string>? OnlyComponents { get; init; }
}

public sealed record InstallProgress(string Phase, double Percent, string Message);

public sealed class InstallResult
{
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool RolledBack { get; set; }
    public bool AlreadyInstalled { get; set; }
    public bool PrerequisitesFailed { get; set; }
    public bool ElevationRequired { get; set; }
    public string Operation { get; set; } = "";
    public string? Error { get; set; }
    public string? InstallDir { get; set; }
    public string? Version { get; set; }
    public List<string> Warnings { get; } = new();
    public List<string> RollbackFailures { get; } = new();
    public List<string> DeferredDeletes { get; } = new();
    public IReadOnlyList<PrerequisiteStatus> Prerequisites { get; set; } = Array.Empty<PrerequisiteStatus>();
}

public sealed record PlannedStep(string OperationId, string Kind, string ComponentId, string Description);

public sealed class InstallPlan
{
    public InstallMode Mode { get; set; }
    public string InstallDir { get; set; } = "";
    public string? ExistingVersion { get; set; }
    public bool IsUpgrade { get; set; }
    public List<string> Components { get; } = new();
    public List<PlannedStep> Steps { get; } = new();
    public List<ValidationIssue> Issues { get; } = new();
    public List<string> ElevationReasons { get; } = new();
    public IReadOnlyList<PrerequisiteStatus> Prerequisites { get; set; } = Array.Empty<PrerequisiteStatus>();
    public bool RequiresElevation => ElevationReasons.Count > 0;
    public bool HasErrors => Issues.Any(i => i.Severity == ValidationSeverity.Error);
}

public sealed class VerifyReport
{
    public int Checked { get; set; }
    public List<string> Missing { get; } = new();
    public List<string> Modified { get; } = new();
    public bool IsHealthy => Missing.Count == 0 && Modified.Count == 0;
}

public sealed class RecoveryResult
{
    public bool Found { get; set; }
    public bool RolledBack { get; set; }
    public bool Finalized { get; set; }
    public List<string> Warnings { get; } = new();
}

public sealed class InstallationInfo
{
    public string InstallDir { get; set; } = "";
    public string Version { get; set; } = "";
    public bool PerUser { get; set; }
    public InstallState? State { get; set; }
}

public sealed class HistoryEntry
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Action { get; set; } = "";
    public string Version { get; set; } = "";
    public string Result { get; set; } = "";
    public string Message { get; set; } = "";
    public string InstallDir { get; set; } = "";
    public string User { get; set; } = Environment.UserName;
}
