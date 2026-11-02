using PAIC.Controls;
using PAIC.ThemeEngine;

namespace PAIC.ProjectSystem.Model;

public enum TargetArchitecture { X64, X86, Arm64 }
public enum OutputFormat { Exe, Msi, Msix, Zip }
public enum AssetCategory { Other, Application, Image, Icon, License, Font, Document }

public enum InstallerPageType
{
    Welcome, LicenseAgreement, InstallationDirectory, ComponentSelection, Prerequisites,
    UserConfiguration, InstallationProgress, InstallationSummary, Completion, Error, Uninstallation, Custom
}

/// <summary>Root of a .paproj file. All paths inside are relative to the project directory.</summary>
public sealed class PaicProject
{
    public int SchemaVersion { get; set; } = ProjectSchema.CurrentVersion;
    public ProjectMetadata Metadata { get; set; } = new();
    public ApplicationInfo Application { get; set; } = new();
    public TargetArchitecture Architecture { get; set; } = TargetArchitecture.X64;
    public InstallConfiguration Installation { get; set; } = new();
    /// <summary>Installer themes embedded in the project and the active one. Absent in older files (treated as empty).</summary>
    public ThemeSettings Theme { get; set; } = new();
    public List<InstallerPageDefinition> Pages { get; set; } = new();
    /// <summary>Custom control definitions used by this project (embedded so the project is self-contained).</summary>
    public List<CustomControlDefinition> CustomControls { get; set; } = new();
    public List<AssetReference> Assets { get; set; } = new();
    public List<ComponentDefinition> Components { get; set; } = new();
    public List<PrerequisiteDefinition> Prerequisites { get; set; } = new();
    public List<InstallOperationDefinition> Operations { get; set; } = new();
    public BuildConfiguration Build { get; set; } = new();
    public SecurityConfiguration Security { get; set; } = new();
}

public sealed class ProjectMetadata
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string GeneratorVersion { get; set; } = "";
}

public sealed class ApplicationInfo
{
    public string ProductName { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string Website { get; set; } = "";
    /// <summary>Four-part numeric version, e.g. 1.2.3 or 1.2.3.4.</summary>
    public string Version { get; set; } = "1.0.0";
    /// <summary>Id of an entry in <see cref="PaicProject.Assets"/>.</summary>
    public string IconAssetId { get; set; } = "";
}

public sealed class InstallConfiguration
{
    public string DefaultInstallDirectory { get; set; } = "";
    public bool PerUser { get; set; }
    /// <summary>Installer window size in device-independent pixels (designer canvas size).</summary>
    public double WindowWidth { get; set; } = 720;
    public double WindowHeight { get; set; } = 480;
}

public sealed class InstallerPageDefinition
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public InstallerPageType Type { get; set; } = InstallerPageType.Custom;
    /// <summary>Elements on the page (z-order = list order, last on top).</summary>
    public List<DesignElement> Elements { get; set; } = new();
    public PageBackground Background { get; set; } = new();
    public bool AllowBack { get; set; } = true;
    public bool AllowCancel { get; set; } = true;
    /// <summary>Optional navigation overrides; empty means "the next/previous visible page in order".</summary>
    public string NextPageId { get; set; } = "";
    public string BackPageId { get; set; } = "";
    /// <summary>When set and false at run time, the page is skipped.</summary>
    public ConditionDefinition? VisibleWhen { get; set; }
}

public sealed class AssetReference
{
    public string Id { get; set; } = "";
    /// <summary>Project-relative path using '/' separators, always under assets/.</summary>
    public string Path { get; set; } = "";
    public AssetCategory Category { get; set; } = AssetCategory.Other;
    /// <summary>Optional lowercase SHA-256; when present it is verified by validation.</summary>
    public string Sha256 { get; set; } = "";
}

public sealed class ComponentDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Required { get; set; }
    public bool DefaultSelected { get; set; } = true;
}

public enum DetectionKind { None, RegistryValue, FileVersion, DotnetRuntime, WindowsVersion }
public enum PrerequisiteArchitecture { Any, X64, X86, Arm64 }

public sealed class PrerequisiteDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string MinVersion { get; set; } = "";
    public string MaxVersion { get; set; } = "";
    /// <summary>https URL of the prerequisite installer. When set, <see cref="Sha256"/> is required so the download is verified.</summary>
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    /// <summary>How the installed version is detected. RegistryValue: target = "HKLM\\key", value = value name. FileVersion: target = file path.
    /// DotnetRuntime: value = shared runtime name (e.g. Microsoft.WindowsDesktop.App). WindowsVersion: no target.</summary>
    public DetectionKind Detection { get; set; } = DetectionKind.None;
    public string DetectionTarget { get; set; } = "";
    public string DetectionValue { get; set; } = "";
    /// <summary>The prerequisite only applies when the machine architecture matches; Any = always.</summary>
    public PrerequisiteArchitecture Architecture { get; set; } = PrerequisiteArchitecture.Any;
    /// <summary>Silent-install switches passed to the downloaded installer.</summary>
    public string InstallArguments { get; set; } = "";
    /// <summary>Ids of prerequisites that must be satisfied first.</summary>
    public List<string> DependsOn { get; set; } = new();
    /// <summary>A missing optional prerequisite is a warning instead of an error.</summary>
    public bool Optional { get; set; }
}

public sealed class InstallOperationDefinition
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string ComponentId { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
    /// <summary>When set, the operation only runs if the condition holds for the installer variables.</summary>
    public ConditionDefinition? Condition { get; set; }
}

public sealed class BuildConfiguration
{
    public string OutputDirectory { get; set; } = "build";
    public List<OutputFormat> Formats { get; set; } = new() { OutputFormat.Zip };
}

/// <summary>Signing settings. Private keys are never stored; only a certificate thumbprint reference.</summary>
public sealed class SecurityConfiguration
{
    public bool SigningEnabled { get; set; }
    public string CertificateThumbprint { get; set; } = "";
    public string TimestampUrl { get; set; } = "";
}

/// <summary>Operation kinds known to the project format. The installer engine (a later phase) defines their semantics.</summary>
public static class OperationKinds
{
    public const string CopyFiles = "copyFiles";
    public const string CreateDirectory = "createDirectory";
    public const string CreateShortcut = "createShortcut";
    public const string WriteRegistry = "writeRegistry";
    public const string SetEnvironmentVariable = "setEnvironmentVariable";
    public const string RunCommand = "runCommand";
    public const string InstallService = "installService";
    public const string SetPermissions = "setPermissions";
    public const string DeleteFiles = "deleteFiles";

    public static IReadOnlyList<string> All { get; } = new[]
    {
        CopyFiles, DeleteFiles, CreateDirectory, CreateShortcut, WriteRegistry, SetEnvironmentVariable, RunCommand, InstallService, SetPermissions
    };
}
