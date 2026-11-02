using System.Globalization;
using PAIC.Core.Validation;
using PAIC.ThemeEngine;

namespace PAIC.Controls;

public sealed class ElementValidationContext
{
    public IReadOnlySet<string> AssetIds { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, CustomControlDefinition> Controls { get; init; } = new Dictionary<string, CustomControlDefinition>();
    /// <summary>When null, page references are not checked.</summary>
    public IReadOnlySet<string>? PageIds { get; init; }
    /// <summary>When null, condition variables are not checked against known names.</summary>
    public IReadOnlySet<string>? KnownVariables { get; init; }
    public double Width { get; init; } = 720;
    public double Height { get; init; } = 480;
    public bool AllowPlaceholders { get; init; }
    public string LocationPrefix { get; init; } = "";
}

/// <summary>Validates elements against their control's property contract (codes PAIC1601-1610).</summary>
public static class ElementValidator
{
    private const double MaxSize = 5000;

    public static IEnumerable<ValidationIssue> Validate(IReadOnlyList<DesignElement> roots, ElementValidationContext ctx)
    {
        var issues = new List<ValidationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots) Check(root, null, ctx, seen, issues);

        // variables bound by more than one control
        var byVar = ElementIds.Flatten(roots).Where(e => !string.IsNullOrWhiteSpace(e.GetString("variable")) && ControlRegistry.IsBuiltIn(e.Type))
            .GroupBy(e => e.GetString("variable"), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1);
        foreach (var g in byVar)
            issues.Add(new("PAIC1610", ValidationSeverity.Warning, $"Variable '{g.Key}' is bound by several controls: {string.Join(", ", g.Select(e => e.Id))}.", Loc(ctx, g.First().Id)));
        return issues;
    }

    private static string Loc(ElementValidationContext c, string id) =>
        string.IsNullOrEmpty(c.LocationPrefix) ? $"elements[{id}]" : $"{c.LocationPrefix}.elements[{id}]";

    private static void Check(DesignElement e, DesignElement? parent, ElementValidationContext ctx, HashSet<string> seen, List<ValidationIssue> issues)
    {
        var loc = Loc(ctx, e.Id);
        void Add(string code, ValidationSeverity s, string msg) => issues.Add(new ValidationIssue(code, s, msg, loc));

        if (string.IsNullOrWhiteSpace(e.Id)) Add("PAIC1601", ValidationSeverity.Error, "An element has an empty id.");
        else if (!seen.Add(e.Id)) Add("PAIC1601", ValidationSeverity.Error, $"Duplicate element id '{e.Id}'.");

        if (double.IsNaN(e.Width) || double.IsNaN(e.Height) || e.Width <= 0 || e.Height <= 0 || e.Width > MaxSize || e.Height > MaxSize)
            Add("PAIC1603", ValidationSeverity.Error, $"Element '{e.Id}' has an invalid size ({e.Width} x {e.Height}).");

        var known = ControlRegistry.TryGet(e.Type, out var descriptor);
        CustomControlDefinition? custom = null;
        if (!known)
        {
            if (!ControlTypes.IsCustom(e.Type) || !ctx.Controls.TryGetValue(ControlTypes.CustomId(e.Type), out custom))
                Add("PAIC1602", ValidationSeverity.Error, $"Element '{e.Id}' uses unknown control type '{e.Type}'.");
        }

        if (descriptor is not null) CheckProperties(e, descriptor, ctx, Add);
        if (custom is not null)
            foreach (var key in e.Properties.Keys.Where(k => custom.Parameters.All(p => p.Name != k)))
                Add("PAIC1605", ValidationSeverity.Warning, $"Element '{e.Id}' sets unknown parameter '{key}'.");

        // bounds
        var parentW = parent?.Width ?? ctx.Width; var parentH = parent?.Height ?? ctx.Height;
        if (parent is null || !LayoutEngine.IsStack(parent))
            if (e.X < 0 || e.Y < 0 || e.X + e.Width > parentW + 0.5 || e.Y + e.Height > parentH + 0.5)
                Add("PAIC1607", ValidationSeverity.Warning, $"Element '{e.Id}' extends outside its {(parent is null ? "page" : "container")}.");

        if (e.Action is { Kind: not ElementActionKind.None } action)
        {
            if (e.Type != ControlTypes.Button) Add("PAIC1608", ValidationSeverity.Warning, $"Element '{e.Id}' has an action but only buttons run actions.");
            if (action.Kind == ElementActionKind.GoToPage && ctx.PageIds is not null && !ctx.PageIds.Contains(action.TargetPageId))
                Add("PAIC1608", ValidationSeverity.Error, $"Button '{e.Id}' navigates to page '{action.TargetPageId}' which does not exist.");
            if (action.Kind is ElementActionKind.BrowseFolder or ElementActionKind.SetVariable && !ControlRegistry.VariablePattern.IsMatch(action.Variable ?? "") && !Skip(ctx, action.Variable))
                Add("PAIC1608", ValidationSeverity.Error, $"Button '{e.Id}' needs a valid variable name for its action.");
        }

        foreach (var (cond, what) in new[] { (e.VisibleWhen, "visibility"), (e.EnabledWhen, "enabled") })
        {
            if (cond is null) continue;
            if (string.IsNullOrWhiteSpace(cond.Variable) && !Skip(ctx, cond.Variable))
                Add("PAIC1609", ValidationSeverity.Error, $"The {what} condition of '{e.Id}' has no variable.");
            else if (ctx.KnownVariables is not null && !Skip(ctx, cond.Variable) && !ctx.KnownVariables.Contains(cond.Variable))
                Add("PAIC1609", ValidationSeverity.Warning, $"The {what} condition of '{e.Id}' uses unknown variable '{cond.Variable}'.");
        }

        foreach (var child in e.Children) Check(child, e, ctx, seen, issues);
    }

    private static bool Skip(ElementValidationContext ctx, string? value) =>
        ctx.AllowPlaceholders && value is not null && value.Contains("{param:", StringComparison.Ordinal);

    private static void CheckProperties(DesignElement e, ControlDescriptor d, ElementValidationContext ctx, Action<string, ValidationSeverity, string> add)
    {
        foreach (var key in e.Properties.Keys.Where(k => d.Find(k) is null))
            add("PAIC1605", ValidationSeverity.Warning, $"Element '{e.Id}' has unknown property '{key}'.");

        foreach (var p in d.Properties)
        {
            var value = e.GetString(p.Name);
            if (Skip(ctx, value)) continue;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (p.Required) add("PAIC1604", ValidationSeverity.Error, $"'{p.Label}' is required for element '{e.Id}'.");
                continue;
            }
            switch (p.Kind)
            {
                case PropertyKind.Number:
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < p.Min || n > p.Max)
                        add("PAIC1604", ValidationSeverity.Error, $"'{p.Label}' of '{e.Id}' must be a number between {p.Min} and {p.Max}.");
                    break;
                case PropertyKind.Bool:
                    if (!bool.TryParse(value, out _)) add("PAIC1604", ValidationSeverity.Error, $"'{p.Label}' of '{e.Id}' must be true or false.");
                    break;
                case PropertyKind.Choice or PropertyKind.ColorToken or PropertyKind.Glyph:
                    if (p.Choices is not null && !p.Choices.Contains(value))
                        add("PAIC1604", ValidationSeverity.Error, $"'{p.Label}' of '{e.Id}' must be one of: {string.Join(", ", p.Choices)}.");
                    break;
                case PropertyKind.Variable:
                    if (!ControlRegistry.VariablePattern.IsMatch(value))
                        add("PAIC1604", ValidationSeverity.Error, $"Variable '{value}' of '{e.Id}' must start with a letter and contain only letters, digits and underscores.");
                    break;
                case PropertyKind.ImageAsset or PropertyKind.TextAsset:
                    if (!ctx.AssetIds.Contains(value))
                        add("PAIC1606", ValidationSeverity.Error, $"Element '{e.Id}' uses asset '{value}' which is not in the project.");
                    break;
            }
        }
    }
}
