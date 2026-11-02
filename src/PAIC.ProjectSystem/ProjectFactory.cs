using PAIC.ProjectSystem.Layouts;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;

namespace PAIC.ProjectSystem;

public static class ProjectFactory
{
    public static PaicProject CreateNew(string name)
    {
        var project = new PaicProject();
        project.Metadata.Name = name;
        project.Metadata.GeneratorVersion = typeof(ProjectFactory).Assembly.GetName().Version?.ToString() ?? "";
        project.Application.ProductName = name;
        project.Installation.DefaultInstallDirectory = @"%ProgramFiles%\" + name;
        project.Theme.Themes.Add(BuiltInThemes.FluentLight());
        project.Theme.ActiveThemeId = BuiltInThemes.FluentLightId;
        project.Pages.AddRange(new[]
        {
            Page("welcome", "Welcome", InstallerPageType.Welcome),
            Page("license", "License Agreement", InstallerPageType.LicenseAgreement),
            Page("directory", "Installation Directory", InstallerPageType.InstallationDirectory),
            Page("progress", "Installing", InstallerPageType.InstallationProgress),
            Page("completion", "Completed", InstallerPageType.Completion),
        });
        return project;
    }

    private static InstallerPageDefinition Page(string id, string title, InstallerPageType type) =>
        new() { Id = id, Title = title, Type = type, Elements = PageLayouts.Create(type, 720, 480) };
}
