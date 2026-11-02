using PAIC.Controls;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Layouts;
using PAIC.ProjectSystem.Model;

namespace PAIC.Designer;

/// <summary>Page-level operations: add, remove, reorder, duplicate, reset layout, navigation references.</summary>
public static class PageService
{
    private static string Slug(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "page" : slug;
    }

    public static string UniquePageId(PaicProject project, string baseId)
    {
        var id = Slug(baseId);
        var used = project.Pages.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = id;
        for (var i = 2; used.Contains(candidate); i++) candidate = $"{id}-{i}";
        return candidate;
    }

    public static string DefaultTitle(InstallerPageType type) => type switch
    {
        InstallerPageType.Welcome => "Welcome",
        InstallerPageType.LicenseAgreement => "License Agreement",
        InstallerPageType.InstallationDirectory => "Installation Directory",
        InstallerPageType.ComponentSelection => "Components",
        InstallerPageType.Prerequisites => "Prerequisites",
        InstallerPageType.UserConfiguration => "Configuration",
        InstallerPageType.InstallationProgress => "Installing",
        InstallerPageType.InstallationSummary => "Summary",
        InstallerPageType.Completion => "Completed",
        InstallerPageType.Error => "Error",
        InstallerPageType.Uninstallation => "Uninstall",
        _ => "New page"
    };

    /// <summary>Adds a page with the default layout for its type after <paramref name="afterIndex"/> (or at the end).</summary>
    public static InstallerPageDefinition AddPage(PaicProject project, InstallerPageType type, string? title = null, int? afterIndex = null)
    {
        title = string.IsNullOrWhiteSpace(title) ? DefaultTitle(type) : title.Trim();
        var page = new InstallerPageDefinition
        {
            Id = UniquePageId(project, type == InstallerPageType.Custom ? title : type.ToString()),
            Title = title,
            Type = type,
            Elements = PageLayouts.Create(type, project.Installation.WindowWidth, project.Installation.WindowHeight)
        };
        var index = afterIndex is { } a ? Math.Clamp(a + 1, 0, project.Pages.Count) : project.Pages.Count;
        project.Pages.Insert(index, page);
        return page;
    }

    public static InstallerPageDefinition DuplicatePage(PaicProject project, string pageId)
    {
        var src = Require(project, pageId);
        var copy = new InstallerPageDefinition
        {
            Id = UniquePageId(project, src.Id + "-copy"),
            Title = src.Title + " (copy)",
            Type = src.Type,
            Elements = src.Elements.Select(e => e.Clone()).ToList(),
            Background = src.Background.Clone(),
            AllowBack = src.AllowBack, AllowCancel = src.AllowCancel,
            NextPageId = "", BackPageId = "",
            VisibleWhen = src.VisibleWhen?.Clone()
        };
        project.Pages.Insert(project.Pages.IndexOf(src) + 1, copy);
        return copy;
    }

    /// <summary>Everything that navigates to the page: other pages' overrides and buttons that go to it.</summary>
    public static IReadOnlyList<string> FindReferences(PaicProject project, string pageId)
    {
        var uses = new List<string>();
        foreach (var p in project.Pages.Where(p => !Same(p.Id, pageId)))
        {
            if (Same(p.NextPageId, pageId)) uses.Add($"Page '{p.Title}' next page");
            if (Same(p.BackPageId, pageId)) uses.Add($"Page '{p.Title}' back page");
        }
        foreach (var p in project.Pages)
            foreach (var e in ElementIds.Flatten(p.Elements))
                if (e.Action is { Kind: ElementActionKind.GoToPage } a && Same(a.TargetPageId, pageId))
                    uses.Add($"Button '{e.Name}' on page '{p.Title}'");
        return uses;
    }

    /// <summary>Removes a page. References block removal unless <paramref name="clearReferences"/> is set, which resets them.</summary>
    public static void RemovePage(PaicProject project, string pageId, bool clearReferences = false)
    {
        var page = Require(project, pageId);
        var uses = FindReferences(project, pageId);
        if (uses.Count > 0 && !clearReferences)
            throw new ProjectException("The page is still referenced: " + string.Join("; ", uses));

        foreach (var p in project.Pages)
        {
            if (Same(p.NextPageId, pageId)) p.NextPageId = "";
            if (Same(p.BackPageId, pageId)) p.BackPageId = "";
            foreach (var e in ElementIds.Flatten(p.Elements))
                if (e.Action is { Kind: ElementActionKind.GoToPage } a && Same(a.TargetPageId, pageId)) e.Action = new ElementAction();
        }
        project.Pages.Remove(page);
    }

    public static void MovePage(PaicProject project, string pageId, int newIndex)
    {
        var page = Require(project, pageId);
        project.Pages.Remove(page);
        project.Pages.Insert(Math.Clamp(newIndex, 0, project.Pages.Count), page);
    }

    /// <summary>Replaces the page's elements with the default layout for its type.</summary>
    public static void ResetLayout(PaicProject project, string pageId)
    {
        var page = Require(project, pageId);
        page.Elements.Clear();
        page.Elements.AddRange(PageLayouts.Create(page.Type, project.Installation.WindowWidth, project.Installation.WindowHeight));
    }

    public static InstallerPageDefinition Require(PaicProject project, string pageId) =>
        project.Pages.FirstOrDefault(p => Same(p.Id, pageId)) ?? throw new ProjectException($"No page '{pageId}'.");

    private static bool Same(string? a, string? b) => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
