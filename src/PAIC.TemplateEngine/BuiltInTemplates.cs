using PAIC.ProjectSystem.Layouts;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;

namespace PAIC.TemplateEngine;

/// <summary>
/// The built-in template library. Each template is a real <see cref="PaicProject"/> that is copied when a project is created.
/// "{ProductName}" is replaced by the new project's name; "{InstallDir}" is left for the installer engine to resolve.
/// Operation parameter names are provisional until the installer engine phase fixes them.
/// </summary>
public static class BuiltInTemplates
{
    public static IReadOnlyList<TemplateEntry> All() => new[]
    {
        Minimal(), Fluent(), Dark(), Light(), DeveloperTool(), DesktopApplication(), MultiComponent(), Runtime(), Enterprise(), Portable()
    };

    private static TemplateEntry Make(string id, string name, string category, string description, ThemeDefinition theme,
        (string Id, string Title, InstallerPageType Type)[] pages, Action<PaicProject>? configure = null)
    {
        var p = new PaicProject();
        p.Metadata.Name = name;
        p.Application.ProductName = "{ProductName}";
        p.Installation.DefaultInstallDirectory = @"%ProgramFiles%\{ProductName}";
        p.Theme.Themes.Add(theme);
        p.Theme.ActiveThemeId = theme.Id;
        foreach (var (pid, title, type) in pages)
            p.Pages.Add(new InstallerPageDefinition { Id = pid, Title = title, Type = type, Elements = PageLayouts.Create(type, p.Installation.WindowWidth, p.Installation.WindowHeight) });
        p.Build.Formats = new() { OutputFormat.Exe, OutputFormat.Zip };
        configure?.Invoke(p);
        return new TemplateEntry
        {
            Info = new TemplateInfo { Id = id, Name = name, Category = category, Description = description },
            Project = p,
            IsBuiltIn = true
        };
    }

    private static (string, string, InstallerPageType)[] Standard() => new[]
    {
        ("welcome", "Welcome", InstallerPageType.Welcome),
        ("license", "License Agreement", InstallerPageType.LicenseAgreement),
        ("directory", "Installation Directory", InstallerPageType.InstallationDirectory),
        ("progress", "Installing", InstallerPageType.InstallationProgress),
        ("completion", "Completed", InstallerPageType.Completion),
    };

    private static InstallOperationDefinition Copy(string id, string component, string source, string destination = "{InstallDir}") =>
        Op(id, OperationKinds.CopyFiles, component, ("source", source), ("destination", destination));

    private static InstallOperationDefinition Op(string id, string kind, string component, params (string, string)[] parameters)
    {
        var op = new InstallOperationDefinition { Id = id, Kind = kind, ComponentId = component };
        foreach (var (k, v) in parameters) op.Parameters[k] = v;
        return op;
    }

    private static TemplateEntry Minimal() => Make("minimal", "Minimal Installer", "General",
        "Three pages: welcome, progress and completion. For small tools that need no choices.",
        BuiltInThemes.FluentLight(), new[]
        {
            ("welcome", "Welcome", InstallerPageType.Welcome),
            ("progress", "Installing", InstallerPageType.InstallationProgress),
            ("completion", "Completed", InstallerPageType.Completion),
        });

    private static TemplateEntry Fluent() => Make("fluent", "Fluent Installer", "General",
        "Standard wizard with license, install directory and progress pages in the Fluent Light theme.",
        BuiltInThemes.FluentLight(), Standard(), p => p.Build.Formats = new() { OutputFormat.Exe, OutputFormat.Msi, OutputFormat.Zip });

    private static TemplateEntry Dark() => Make("dark", "Dark Installer", "General",
        "The standard wizard using the Fluent Dark theme.", BuiltInThemes.FluentDark(), Standard());

    private static TemplateEntry Light() => Make("light", "Light Installer", "General",
        "The standard wizard using a plain light theme with the Solid material.",
        BuiltInThemes.FluentLight(), Standard(), p =>
        {
            var t = p.Theme.Themes[0];
            t.Id = "light-solid"; t.Name = "Light Solid"; t.Material = ThemeMaterial.Solid;
            t.Colors[ThemeTokens.Background] = "#FFFFFF";
            p.Theme.ActiveThemeId = t.Id;
        });

    private static TemplateEntry DeveloperTool() => Make("developer-tool", "Developer Tool Installer", "Developer",
        "Command-line tool with optional documentation and shell integration, and a PATH update.",
        BuiltInThemes.Midnight(), new[]
        {
            ("welcome", "Welcome", InstallerPageType.Welcome),
            ("directory", "Installation Directory", InstallerPageType.InstallationDirectory),
            ("components", "Components", InstallerPageType.ComponentSelection),
            ("progress", "Installing", InstallerPageType.InstallationProgress),
            ("completion", "Completed", InstallerPageType.Completion),
        }, p =>
        {
            p.Installation.PerUser = true;
            p.Installation.DefaultInstallDirectory = @"%LocalAppData%\Programs\{ProductName}";
            p.Components.Add(new ComponentDefinition { Id = "cli", Name = "Command-line tool", Required = true });
            p.Components.Add(new ComponentDefinition { Id = "docs", Name = "Documentation" });
            p.Components.Add(new ComponentDefinition { Id = "shell", Name = "Shell integration", DefaultSelected = false });
            p.Operations.Add(Copy("copy-cli", "cli", "assets/app/**", @"{InstallDir}\bin"));
            p.Operations.Add(Copy("copy-docs", "docs", "assets/docs/**", @"{InstallDir}\docs"));
            p.Operations.Add(Op("add-to-path", OperationKinds.SetEnvironmentVariable, "cli",
                ("name", "PATH"), ("value", @"{InstallDir}\bin"), ("action", "append"), ("scope", "user")));
            p.Build.Formats = new() { OutputFormat.Exe, OutputFormat.Zip };
        });

    private static TemplateEntry DesktopApplication() => Make("desktop-application", "Desktop Application Installer", "Applications",
        "Per-machine desktop app with Start menu and desktop shortcuts.",
        BuiltInThemes.FluentLight(), Standard(), p =>
        {
            p.Components.Add(new ComponentDefinition { Id = "app", Name = "Application", Required = true });
            p.Operations.Add(Copy("copy-app", "app", "assets/app/**"));
            p.Operations.Add(Op("start-menu-shortcut", OperationKinds.CreateShortcut, "app",
                ("location", "startMenu"), ("target", @"{InstallDir}\{ProductName}.exe"), ("name", "{ProductName}")));
            p.Operations.Add(Op("desktop-shortcut", OperationKinds.CreateShortcut, "app",
                ("location", "desktop"), ("target", @"{InstallDir}\{ProductName}.exe"), ("name", "{ProductName}")));
            p.Build.Formats = new() { OutputFormat.Exe, OutputFormat.Msi, OutputFormat.Msix };
        });

    private static TemplateEntry MultiComponent() => Make("multi-component", "Multi-component Installer", "Applications",
        "Component selection page with a required core and optional plug-ins, documentation and samples.",
        BuiltInThemes.FluentLight(), new[]
        {
            ("welcome", "Welcome", InstallerPageType.Welcome),
            ("license", "License Agreement", InstallerPageType.LicenseAgreement),
            ("components", "Components", InstallerPageType.ComponentSelection),
            ("directory", "Installation Directory", InstallerPageType.InstallationDirectory),
            ("summary", "Summary", InstallerPageType.InstallationSummary),
            ("progress", "Installing", InstallerPageType.InstallationProgress),
            ("completion", "Completed", InstallerPageType.Completion),
        }, p =>
        {
            p.Components.Add(new ComponentDefinition { Id = "core", Name = "Core application", Required = true });
            p.Components.Add(new ComponentDefinition { Id = "plugins", Name = "Plug-ins" });
            p.Components.Add(new ComponentDefinition { Id = "docs", Name = "Documentation" });
            p.Components.Add(new ComponentDefinition { Id = "samples", Name = "Samples", DefaultSelected = false });
            p.Operations.Add(Copy("copy-core", "core", "assets/app/**"));
            p.Operations.Add(Copy("copy-plugins", "plugins", "assets/plugins/**", @"{InstallDir}\plugins"));
            p.Operations.Add(Copy("copy-docs", "docs", "assets/docs/**", @"{InstallDir}\docs"));
            p.Operations.Add(Copy("copy-samples", "samples", "assets/samples/**", @"{InstallDir}\samples"));
        });

    private static TemplateEntry Runtime() => Make("runtime", "Runtime Installer", "Developer",
        "Installs a shared runtime machine-wide and publishes its location in an environment variable.",
        BuiltInThemes.FluentDark(), new[]
        {
            ("welcome", "Welcome", InstallerPageType.Welcome),
            ("license", "License Agreement", InstallerPageType.LicenseAgreement),
            ("progress", "Installing", InstallerPageType.InstallationProgress),
            ("completion", "Completed", InstallerPageType.Completion),
        }, p =>
        {
            p.Installation.DefaultInstallDirectory = @"%ProgramFiles%\{ProductName}\Runtime";
            p.Components.Add(new ComponentDefinition { Id = "runtime", Name = "Runtime", Required = true });
            p.Operations.Add(Copy("copy-runtime", "runtime", "assets/runtime/**"));
            p.Operations.Add(Op("runtime-home", OperationKinds.SetEnvironmentVariable, "runtime",
                ("name", "RUNTIME_HOME"), ("value", "{InstallDir}"), ("action", "set"), ("scope", "machine")));
            p.Build.Formats = new() { OutputFormat.Exe, OutputFormat.Msi };
        });

    private static TemplateEntry Enterprise() => Make("enterprise", "Enterprise Installer", "Enterprise",
        "Full page set with prerequisites, summary and error pages, a registry entry and MSI output for managed deployment.",
        BuiltInThemes.FluentLight(), new[]
        {
            ("welcome", "Welcome", InstallerPageType.Welcome),
            ("license", "License Agreement", InstallerPageType.LicenseAgreement),
            ("prerequisites", "Prerequisites", InstallerPageType.Prerequisites),
            ("directory", "Installation Directory", InstallerPageType.InstallationDirectory),
            ("summary", "Summary", InstallerPageType.InstallationSummary),
            ("progress", "Installing", InstallerPageType.InstallationProgress),
            ("completion", "Completed", InstallerPageType.Completion),
            ("error", "Installation failed", InstallerPageType.Error),
            ("uninstall", "Uninstall", InstallerPageType.Uninstallation),
        }, p =>
        {
            p.Components.Add(new ComponentDefinition { Id = "app", Name = "Application", Required = true });
            p.Prerequisites.Add(new PrerequisiteDefinition
            {
                Id = "dotnet-desktop", Name = ".NET Desktop Runtime", MinVersion = "8.0",
                Detection = DetectionKind.DotnetRuntime, DetectionValue = "Microsoft.WindowsDesktop.App"
            });
            p.Operations.Add(Copy("copy-app", "app", "assets/app/**"));
            p.Operations.Add(Op("install-path-registry", OperationKinds.WriteRegistry, "app",
                ("root", "HKLM"), ("key", @"SOFTWARE\{ProductName}"), ("name", "InstallPath"), ("value", "{InstallDir}")));
            p.Build.Formats = new() { OutputFormat.Msi, OutputFormat.Exe };
        });

    private static TemplateEntry Portable() => Make("portable", "Portable Application Package", "Portable",
        "ZIP package with a launch configuration and no installer pages.",
        BuiltInThemes.FluentLight(), Array.Empty<(string, string, InstallerPageType)>(), p =>
        {
            p.Installation.PerUser = true;
            p.Installation.DefaultInstallDirectory = "{ProductName}";
            p.Components.Add(new ComponentDefinition { Id = "app", Name = "Application", Required = true });
            p.Build.Formats = new() { OutputFormat.Zip };
        });
}
