using PAIC.ProjectSystem.Model;

namespace PAIC.ProjectSystem.Operations;

public enum OperationParameterKind { Text, Path, Bool, Number, Choice }

public sealed record OperationParameter(string Name, string Label, OperationParameterKind Kind, bool Required = false, string Default = "",
    IReadOnlyList<string>? Choices = null, string Help = "");

/// <summary>Contract of one installation operation kind: its parameters and platform notes.</summary>
public sealed class OperationDescriptor
{
    public string Kind { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>True when the engine can undo the operation (rollback and uninstall).</summary>
    public bool Reversible { get; init; } = true;
    /// <summary>False when the operation cannot be expressed inside an MSIX package's sandbox.</summary>
    public bool MsixCompatible { get; init; }
    public IReadOnlyList<OperationParameter> Parameters { get; init; } = Array.Empty<OperationParameter>();
    public OperationParameter? Find(string name) => Parameters.FirstOrDefault(p => p.Name == name);
}

/// <summary>The built-in operation kinds and their parameter contracts. Shared by project validation, the editor and the installer engine.</summary>
public static class OperationCatalog
{
    private static OperationParameter T(string n, string l, bool req = false, string d = "", string help = "") => new(n, l, OperationParameterKind.Text, req, d, Help: help);
    private static OperationParameter P(string n, string l, bool req = false, string d = "", string help = "") => new(n, l, OperationParameterKind.Path, req, d, Help: help);
    private static OperationParameter B(string n, string l, bool d = false, string help = "") => new(n, l, OperationParameterKind.Bool, false, d ? "true" : "false", Help: help);
    private static OperationParameter N(string n, string l, string d, string help = "") => new(n, l, OperationParameterKind.Number, false, d, Help: help);
    private static OperationParameter C(string n, string l, string d, bool req, params string[] choices) => new(n, l, OperationParameterKind.Choice, req, d, choices);

    private static readonly OperationDescriptor[] Descriptors =
    {
        new()
        {
            Kind = OperationKinds.CopyFiles, DisplayName = "Copy files", MsixCompatible = true,
            Description = "Copies payload files (project assets) to a folder. Files are verified by SHA-256 and backed up when replaced.",
            Parameters = new[]
            {
                P("source", "Source (project path or glob)", true, "assets/app/**", "e.g. assets/app/** or assets/app/*.dll"),
                P("destination", "Destination folder", true, "{InstallDir}"),
                C("overwrite", "When the file exists", "always", false, "always", "ifDifferent", "never"),
                B("userData", "Treat as user data (kept on uninstall unless removal is requested)"),
                B("optional", "No error when nothing matches"),
                B("allowOutside", "Allow a destination outside the install folder")
            }
        },
        new()
        {
            Kind = OperationKinds.DeleteFiles, DisplayName = "Delete files",
            Description = "Deletes existing files inside the install folder (for example leftovers of an older version). Originals are backed up for rollback.",
            Parameters = new[] { P("path", "File path (wildcards allowed in the file name)", true, "{InstallDir}\\old\\*.dll"), B("recursive", "Include sub-folders") }
        },
        new()
        {
            Kind = OperationKinds.CreateDirectory, DisplayName = "Create folder",
            Description = "Creates a folder. Folders created by the installer are removed on uninstall when empty.",
            Parameters = new[] { P("path", "Folder", true, "{InstallDir}\\data"), B("allowOutside", "Allow a folder outside the install folder") }
        },
        new()
        {
            Kind = OperationKinds.CreateShortcut, DisplayName = "Create shortcut", MsixCompatible = true,
            Description = "Creates a .lnk shortcut in the Start menu, on the desktop or in Startup.",
            Parameters = new[]
            {
                C("location", "Location", "startMenu", true, "startMenu", "desktop", "startup"),
                T("name", "Shortcut name", true),
                P("target", "Target", true, "{InstallDir}\\{ProductName}.exe"),
                T("arguments", "Arguments"),
                P("workingDirectory", "Working directory", false, "{InstallDir}"),
                P("icon", "Icon file"),
                T("description", "Description"),
                T("folder", "Start menu sub-folder")
            }
        },
        new()
        {
            Kind = OperationKinds.WriteRegistry, DisplayName = "Write registry value",
            Description = "Writes a registry value. The previous value is restored on rollback; the value is removed on uninstall.",
            Parameters = new[]
            {
                C("root", "Root", "HKCU", true, "HKCU", "HKLM"),
                T("key", "Key (without root)", true, "SOFTWARE\\Contoso\\App"),
                T("name", "Value name (empty = default)"),
                T("value", "Value", true),
                C("type", "Type", "string", false, "string", "expandString", "dword", "qword", "multiString")
            }
        },
        new()
        {
            Kind = OperationKinds.SetEnvironmentVariable, DisplayName = "Set environment variable",
            Description = "Sets, appends to or prepends to an environment variable (for example PATH). Only the entry the installer added is removed again.",
            Parameters = new[]
            {
                T("name", "Name", true, "PATH"),
                T("value", "Value", true),
                C("action", "Action", "set", false, "set", "append", "prepend"),
                C("scope", "Scope", "user", false, "user", "machine")
            }
        },
        new()
        {
            Kind = OperationKinds.RunCommand, DisplayName = "Run command", Reversible = false,
            Description = "Runs a program. It can only be undone through the optional uninstall command, which is a compensating action.",
            Parameters = new[]
            {
                P("command", "Command", true),
                T("arguments", "Arguments"),
                P("workingDirectory", "Working directory", false, "{InstallDir}"),
                C("runOn", "Run on", "install", false, "install", "uninstall"),
                B("onRepair", "Also run during repair"),
                P("uninstallCommand", "Command to run on uninstall / rollback"),
                T("uninstallArguments", "Uninstall arguments"),
                T("successCodes", "Success exit codes (comma separated)", false, "0"),
                N("timeoutSeconds", "Timeout (seconds)", "300")
            }
        },
        new()
        {
            Kind = OperationKinds.InstallService, DisplayName = "Install Windows service",
            Description = "Registers a Windows service (requires elevation). The service is stopped and removed on uninstall.",
            Parameters = new[]
            {
                T("name", "Service name", true), T("displayName", "Display name"), P("binaryPath", "Executable", true, "{InstallDir}\\service.exe"),
                T("arguments", "Arguments"), C("startType", "Start type", "auto", false, "auto", "manual", "disabled"),
                T("account", "Account", false, "LocalSystem"), T("description", "Description"), B("start", "Start after install", true)
            }
        },
        new()
        {
            Kind = OperationKinds.SetPermissions, DisplayName = "Set permissions",
            Description = "Grants a user or group access to a file or folder. The added rule is removed on uninstall.",
            Parameters = new[]
            {
                P("path", "Path", true, "{InstallDir}\\data"), T("principal", "User or group", true, "BUILTIN\\Users"),
                C("rights", "Rights", "read", false, "read", "modify", "fullControl"), B("allowOutside", "Allow a path outside the install folder")
            }
        }
    };

    private static readonly Dictionary<string, OperationDescriptor> Map = Descriptors.ToDictionary(d => d.Kind);

    public static IReadOnlyList<OperationDescriptor> All => Descriptors;
    public static bool TryGet(string kind, out OperationDescriptor descriptor) => Map.TryGetValue(kind ?? "", out descriptor!);
}
