using System.Text.RegularExpressions;
using PAIC.Controls;
using PAIC.Core.IO;
using PAIC.Core.Validation;
using PAIC.ProjectSystem.Model;
using PAIC.ProjectSystem.Operations;
using PAIC.ThemeEngine;

namespace PAIC.ProjectSystem.Validation;

/// <summary>What project rules evaluate. ProjectDirectory is null for projects that were never saved.</summary>
public sealed record ProjectValidationContext(PaicProject Project, string? ProjectDirectory);

public static class ProjectValidator
{
    public static Validator<ProjectValidationContext> CreateDefault() => new(new IValidationRule<ProjectValidationContext>[]
    {
        new MetadataRule(), new UniqueIdRule(), new AssetRule(), new ReferenceRule(), new OperationRule(), new PrerequisiteRule(), new ThemeRule(), new DesignRule(), new SecurityRule()
    });

    internal static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(\.\d+)?$", RegexOptions.Compiled);

    internal static bool IsValidVersion(string? v) =>
        !string.IsNullOrWhiteSpace(v) && VersionPattern.IsMatch(v) &&
        v.Split('.').All(p => int.TryParse(p, out var n) && n <= 65535);
}

internal sealed class MetadataRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "metadata";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var p = c.Project;
        if (!ProjectNaming.IsValidName(p.Metadata.Name, out var why))
            yield return new("PAIC1001", ValidationSeverity.Error, $"Project name is invalid: {why}", "metadata.name");
        if (string.IsNullOrWhiteSpace(p.Application.ProductName))
            yield return new("PAIC1002", ValidationSeverity.Error, "Product name is required.", "application.productName");
        if (!ProjectValidator.IsValidVersion(p.Application.Version))
            yield return new("PAIC1003", ValidationSeverity.Error,
                "Version must be numeric, like 1.2.3 or 1.2.3.4 (each part 0-65535).", "application.version");
        if (string.IsNullOrWhiteSpace(p.Application.Publisher))
            yield return new("PAIC1004", ValidationSeverity.Warning, "Publisher is empty; installers and signing normally need one.", "application.publisher");
        if (!string.IsNullOrWhiteSpace(p.Application.Website) &&
            !(Uri.TryCreate(p.Application.Website, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
            yield return new("PAIC1005", ValidationSeverity.Warning, "Website should be an http(s) URL.", "application.website");
        if (string.IsNullOrWhiteSpace(p.Installation.DefaultInstallDirectory))
            yield return new("PAIC1006", ValidationSeverity.Error, "Default install directory is required.", "installation.defaultInstallDirectory");
        var portableOnly = p.Build.Formats.Count > 0 && p.Build.Formats.All(f => f == OutputFormat.Zip);
        if (p.Pages.Count == 0 && !portableOnly)
            yield return new("PAIC1007", ValidationSeverity.Warning, "The installer has no pages.", "pages");
    }
}

internal sealed class UniqueIdRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "unique-ids";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var p = c.Project;
        foreach (var i in Check("pages", p.Pages.Select(x => x.Id))) yield return i;
        foreach (var i in Check("assets", p.Assets.Select(x => x.Id))) yield return i;
        foreach (var i in Check("components", p.Components.Select(x => x.Id))) yield return i;
        foreach (var i in Check("prerequisites", p.Prerequisites.Select(x => x.Id))) yield return i;
        foreach (var i in Check("operations", p.Operations.Select(x => x.Id))) yield return i;
        foreach (var i in Check("themes", p.Theme.Themes.Select(x => x.Id))) yield return i;
        foreach (var i in Check("customControls", p.CustomControls.Select(x => x.Id))) yield return i;
    }

    private static IEnumerable<ValidationIssue> Check(string area, IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (string.IsNullOrWhiteSpace(id))
                yield return new("PAIC1101", ValidationSeverity.Error, "An entry has an empty id.", area);
            else if (!seen.Add(id))
                yield return new("PAIC1102", ValidationSeverity.Error, $"Duplicate id '{id}'.", area);
        }
    }
}

internal sealed class AssetRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "assets";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        foreach (var a in c.Project.Assets)
        {
            var loc = $"assets[{a.Id}]";
            if (!PathSafety.IsSafeRelativePath(a.Path, out var reason))
            {
                yield return new("PAIC1201", ValidationSeverity.Error, $"Asset path is not allowed: {reason}", loc);
                continue;
            }
            var normalized = PathSafety.ToProjectPath(a.Path);
            if (!normalized.StartsWith(ProjectLayout.AssetsFolderName + "/", StringComparison.OrdinalIgnoreCase))
            {
                yield return new("PAIC1202", ValidationSeverity.Error, $"Asset must be inside the '{ProjectLayout.AssetsFolderName}' folder.", loc);
                continue;
            }
            if (c.ProjectDirectory is null) continue;

            var full = PathSafety.ResolveUnder(c.ProjectDirectory, normalized);
            if (!File.Exists(full))
            {
                yield return new("PAIC1203", ValidationSeverity.Error, $"Asset file is missing: {normalized}", loc);
                continue;
            }
            if (!string.IsNullOrWhiteSpace(a.Sha256) &&
                !string.Equals(FileHashing.ComputeSha256(full), a.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                yield return new("PAIC1204", ValidationSeverity.Error, $"Asset checksum does not match: {normalized}", loc);
        }

        var icon = c.Project.Application.IconAssetId;
        if (!string.IsNullOrWhiteSpace(icon) && !c.Project.Assets.Any(a => string.Equals(a.Id, icon, StringComparison.OrdinalIgnoreCase)))
            yield return new("PAIC1205", ValidationSeverity.Error, $"Icon asset '{icon}' is not in the asset list.", "application.iconAssetId");
    }
}

internal sealed class ReferenceRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "references";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var componentIds = c.Project.Components.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var op in c.Project.Operations)
        {
            if (string.IsNullOrWhiteSpace(op.Kind))
                yield return new("PAIC1301", ValidationSeverity.Error, "Operation has no kind.", $"operations[{op.Id}]");
            else if (!OperationKinds.All.Contains(op.Kind))
                yield return new("PAIC1308", ValidationSeverity.Warning, $"Operation kind '{op.Kind}' is not a known kind.", $"operations[{op.Id}]");
            if (!string.IsNullOrWhiteSpace(op.ComponentId) && !componentIds.Contains(op.ComponentId))
                yield return new("PAIC1302", ValidationSeverity.Error, $"Operation references unknown component '{op.ComponentId}'.", $"operations[{op.Id}]");
        }

        foreach (var pre in c.Project.Prerequisites)
        {
            var loc = $"prerequisites[{pre.Id}]";
            if (string.IsNullOrWhiteSpace(pre.Name))
                yield return new("PAIC1303", ValidationSeverity.Error, "Prerequisite has no name.", loc);
            if (!string.IsNullOrWhiteSpace(pre.MinVersion) && !Version.TryParse(pre.MinVersion, out _))
                yield return new("PAIC1304", ValidationSeverity.Error, "Minimum version is not a valid version.", loc);
            if (!string.IsNullOrWhiteSpace(pre.MaxVersion) && !Version.TryParse(pre.MaxVersion, out _))
                yield return new("PAIC1305", ValidationSeverity.Error, "Maximum version is not a valid version.", loc);
            if (Version.TryParse(pre.MinVersion, out var min) && Version.TryParse(pre.MaxVersion, out var max) && min > max)
                yield return new("PAIC1306", ValidationSeverity.Error, "Minimum version is greater than maximum version.", loc);
            if (!string.IsNullOrWhiteSpace(pre.DownloadUrl) &&
                !(Uri.TryCreate(pre.DownloadUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps))
                yield return new("PAIC1307", ValidationSeverity.Error, "Download URL must be an https URL.", loc);
        }
    }
}

/// <summary>Checks operation parameters against the operation catalog (PAIC1310-1315).</summary>
internal sealed class OperationRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "operation-parameters";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        foreach (var op in c.Project.Operations)
        {
            var loc = $"operations[{op.Id}]";
            if (op.Condition is { } cond && string.IsNullOrWhiteSpace(cond.Variable))
                yield return new("PAIC1315", ValidationSeverity.Error, "The operation's condition has no variable.", loc);
            if (!OperationCatalog.TryGet(op.Kind, out var d)) continue;

            foreach (var p in d.Parameters)
            {
                op.Parameters.TryGetValue(p.Name, out var value);
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (p.Required) yield return new("PAIC1310", ValidationSeverity.Error, $"'{p.Label}' is required for {d.DisplayName.ToLowerInvariant()}.", loc);
                    continue;
                }
                switch (p.Kind)
                {
                    case OperationParameterKind.Bool when !bool.TryParse(value, out _):
                        yield return new("PAIC1311", ValidationSeverity.Error, $"'{p.Label}' must be true or false.", loc); break;
                    case OperationParameterKind.Number when !double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _):
                        yield return new("PAIC1311", ValidationSeverity.Error, $"'{p.Label}' must be a number.", loc); break;
                    case OperationParameterKind.Choice when p.Choices is not null && !p.Choices.Contains(value):
                        yield return new("PAIC1311", ValidationSeverity.Error, $"'{p.Label}' must be one of: {string.Join(", ", p.Choices)}.", loc); break;
                }
            }
            foreach (var key in op.Parameters.Keys.Where(k => d.Find(k) is null))
                yield return new("PAIC1312", ValidationSeverity.Warning, $"Unknown parameter '{key}' for {d.DisplayName.ToLowerInvariant()}.", loc);

            if (op.Kind == OperationKinds.CopyFiles && op.Parameters.TryGetValue("source", out var source) && !string.IsNullOrWhiteSpace(source))
            {
                if (!PathSafety.IsSafeRelativePath(source, out var why))
                    yield return new("PAIC1313", ValidationSeverity.Error, $"Source '{source}' is not allowed: {why}", loc);
                else if (!(op.Parameters.TryGetValue("optional", out var opt) && bool.TryParse(opt, out var isOptional) && isOptional)
                         && !c.Project.Assets.Any(a => Glob.IsMatch(source, a.Path)))
                    yield return new("PAIC1314", ValidationSeverity.Warning, $"No project asset matches '{source}'; the installer would have nothing to copy.", loc);
            }
        }
    }
}

/// <summary>Prerequisite detection, ordering and download integrity (PAIC1309, PAIC1317-1319).</summary>
internal sealed class PrerequisiteRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "prerequisites";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var ids = c.Project.Prerequisites.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pre in c.Project.Prerequisites)
        {
            var loc = $"prerequisites[{pre.Id}]";
            if (!string.IsNullOrWhiteSpace(pre.DownloadUrl) && string.IsNullOrWhiteSpace(pre.Sha256))
                yield return new("PAIC1309", ValidationSeverity.Error, "A download URL requires a SHA-256 checksum so the download can be verified.", loc);
            if (!string.IsNullOrWhiteSpace(pre.Sha256) && !(pre.Sha256.Length == 64 && pre.Sha256.All(Uri.IsHexDigit)))
                yield return new("PAIC1309", ValidationSeverity.Error, "The SHA-256 checksum must be 64 hexadecimal characters.", loc);

            var missingTarget = pre.Detection switch
            {
                DetectionKind.RegistryValue or DetectionKind.FileVersion => string.IsNullOrWhiteSpace(pre.DetectionTarget),
                DetectionKind.DotnetRuntime => string.IsNullOrWhiteSpace(pre.DetectionValue),
                _ => false
            };
            if (pre.Detection == DetectionKind.None)
                yield return new("PAIC1319", ValidationSeverity.Error, "Choose how the installed version of this prerequisite is detected.", loc);
            else if (missingTarget)
                yield return new("PAIC1319", ValidationSeverity.Error, "The detection method needs its target (key, file or runtime name).", loc);

            foreach (var dep in pre.DependsOn)
            {
                if (string.Equals(dep, pre.Id, StringComparison.OrdinalIgnoreCase))
                    yield return new("PAIC1318", ValidationSeverity.Error, "A prerequisite cannot depend on itself.", loc);
                else if (!ids.Contains(dep))
                    yield return new("PAIC1317", ValidationSeverity.Error, $"Depends on unknown prerequisite '{dep}'.", loc);
            }
        }

        // cycles
        var byId = c.Project.Prerequisites.GroupBy(p => p.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = visiting, 2 = done
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cycles = new List<string>();
        void Visit(string id)
        {
            if (state.TryGetValue(id, out var s)) { if (s == 1 && reported.Add(id)) cycles.Add(id); return; }
            state[id] = 1;
            if (byId.TryGetValue(id, out var p))
                foreach (var dep in p.DependsOn.Where(d => !string.Equals(d, id, StringComparison.OrdinalIgnoreCase) && byId.ContainsKey(d))) Visit(dep);
            state[id] = 2;
        }
        foreach (var id in byId.Keys) Visit(id);
        foreach (var id in cycles)
            yield return new("PAIC1318", ValidationSeverity.Error, $"Prerequisite '{id}' is part of a dependency cycle.", $"prerequisites[{id}]");
    }
}

internal sealed class ThemeRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "themes";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var settings = c.Project.Theme;
        if (settings.Themes.Count == 0)
        {
            yield return new("PAIC1501", ValidationSeverity.Warning, "The project has no installer theme. Add one in Theme Studio.", "theme");
            yield break;
        }
        if (!settings.Themes.Any(t => string.Equals(t.Id, settings.ActiveThemeId, StringComparison.Ordinal)))
            yield return new("PAIC1502", ValidationSeverity.Error, $"Active theme '{settings.ActiveThemeId}' is not part of the project.", "theme.activeThemeId");

        var validator = ThemeValidator.CreateDefault();
        foreach (var theme in settings.Themes)
        {
            foreach (var issue in validator.Validate(theme).Issues)
                yield return issue with { Location = $"themes[{theme.Id}].{issue.Location}" };
            if (!string.IsNullOrWhiteSpace(theme.BackgroundImageAssetId) &&
                !c.Project.Assets.Any(a => string.Equals(a.Id, theme.BackgroundImageAssetId, StringComparison.OrdinalIgnoreCase)))
                yield return new("PAIC1503", ValidationSeverity.Error,
                    $"Theme '{theme.Name}' uses background image asset '{theme.BackgroundImageAssetId}' which is not in the asset list.", $"themes[{theme.Id}].backgroundImageAssetId");
        }
    }
}

/// <summary>Pages, elements, navigation and custom controls (PAIC1601-1626, PAIC17xx).</summary>
internal sealed class DesignRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "design";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var p = c.Project;
        var width = p.Installation.WindowWidth; var height = p.Installation.WindowHeight;
        if (!(width is >= 320 and <= 3840) || !(height is >= 240 and <= 2160))
            yield return new("PAIC1626", ValidationSeverity.Error, "Installer window size must be between 320x240 and 3840x2160.", "installation.window");

        var assetIds = p.Assets.Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pageIds = p.Pages.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var controls = new Dictionary<string, CustomControlDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var cc in p.CustomControls) controls[cc.Id] = cc;

        var known = new HashSet<string>(Layouts.BuiltInVariables.Names, StringComparer.OrdinalIgnoreCase);
        foreach (var page in p.Pages)
            foreach (var e in ElementIds.Flatten(page.Elements))
            {
                var v = e.GetString("variable");
                if (!string.IsNullOrWhiteSpace(v)) known.Add(v);
                if (e.Action is { Kind: ElementActionKind.BrowseFolder or ElementActionKind.SetVariable } a && !string.IsNullOrWhiteSpace(a.Variable)) known.Add(a.Variable);
            }

        foreach (var cc in p.CustomControls)
            foreach (var issue in CustomControlValidator.Validate(cc).Issues)
                yield return issue with { Location = $"customControls[{cc.Id}].{issue.Location}" };

        foreach (var page in p.Pages)
        {
            var loc = $"pages[{page.Id}]";
            var ctx = new ElementValidationContext
            {
                AssetIds = assetIds, Controls = controls, PageIds = pageIds, KnownVariables = known,
                Width = width, Height = height, LocationPrefix = loc
            };
            foreach (var issue in ElementValidator.Validate(page.Elements, ctx)) yield return issue;

            foreach (var (target, what) in new[] { (page.NextPageId, "next"), (page.BackPageId, "back") })
                if (!string.IsNullOrWhiteSpace(target) && !pageIds.Contains(target))
                    yield return new("PAIC1620", ValidationSeverity.Error, $"Page '{page.Id}' has {what} page '{target}' which does not exist.", loc);

            var bg = page.Background;
            if (bg.Kind == PageBackgroundKind.Token && !PAIC.ThemeEngine.ThemeTokens.Required.Contains(bg.Value))
                yield return new("PAIC1621", ValidationSeverity.Error, $"Page background token '{bg.Value}' is not a theme colour token.", loc);
            if (bg.Kind == PageBackgroundKind.Color && !PAIC.ThemeEngine.ThemeColor.TryParse(bg.Value, out _, out _, out _, out _))
                yield return new("PAIC1621", ValidationSeverity.Error, $"Page background colour '{bg.Value}' is not a valid #RRGGBB colour.", loc);
            if (bg.Kind == PageBackgroundKind.Image && !assetIds.Contains(bg.Value))
                yield return new("PAIC1621", ValidationSeverity.Error, $"Page background image '{bg.Value}' is not in the project's assets.", loc);

            if (page.VisibleWhen is { } cond && !known.Contains(cond.Variable ?? ""))
                yield return new("PAIC1622", ValidationSeverity.Warning, $"Page '{page.Id}' is conditional on unknown variable '{cond.Variable}'.", loc);

            if (page.Type == InstallerPageType.InstallationProgress && !ElementIds.Flatten(page.Elements).Any(e => e.Type == ControlTypes.Progress))
                yield return new("PAIC1623", ValidationSeverity.Warning, $"Installation progress page '{page.Id}' has no progress bar.", loc);
        }

        foreach (var unreachable in Unreachable(p))
            yield return new("PAIC1624", ValidationSeverity.Warning, $"Page '{unreachable}' cannot be reached by navigation.", $"pages[{unreachable}]");
    }

    private static IEnumerable<string> Unreachable(PaicProject p)
    {
        if (p.Pages.Count < 2) yield break;
        var byId = p.Pages.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<int>();
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            var i = queue.Dequeue();
            var page = p.Pages[i];
            if (!reached.Add(page.Id)) continue;
            void Go(string? id) { if (id is not null && byId.TryGetValue(id, out var t)) queue.Enqueue(p.Pages.IndexOf(t)); }
            if (!string.IsNullOrWhiteSpace(page.NextPageId)) Go(page.NextPageId);
            else
                for (var j = i + 1; j < p.Pages.Count; j++)
                {
                    queue.Enqueue(j);
                    if (p.Pages[j].VisibleWhen is null) break; // later pages are only reachable past conditional pages
                }
            foreach (var e in ElementIds.Flatten(page.Elements))
                if (e.Action is { Kind: ElementActionKind.GoToPage } a) Go(a.TargetPageId);
        }
        foreach (var page in p.Pages) if (!reached.Contains(page.Id)) yield return page.Id;
    }
}

internal sealed class SecurityRule : IValidationRule<ProjectValidationContext>
{
    public string Id => "security";

    public IEnumerable<ValidationIssue> Evaluate(ProjectValidationContext c)
    {
        var s = c.Project.Security;
        if (s.SigningEnabled)
        {
            var thumb = s.CertificateThumbprint.Replace(" ", "");
            if (!(thumb.Length is 40 or 64) || !thumb.All(Uri.IsHexDigit))
                yield return new("PAIC1401", ValidationSeverity.Error,
                    "Signing is enabled but the certificate thumbprint is not a valid SHA-1/SHA-256 hex thumbprint.", "security.certificateThumbprint");
        }
        if (!string.IsNullOrWhiteSpace(s.TimestampUrl) &&
            !(Uri.TryCreate(s.TimestampUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
            yield return new("PAIC1402", ValidationSeverity.Error, "Timestamp URL must be an http(s) URL.", "security.timestampUrl");
    }
}
