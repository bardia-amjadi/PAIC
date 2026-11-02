using System.Text.RegularExpressions;
using PAIC.ThemeEngine;

namespace PAIC.Controls;

public enum PropertyKind { Text, MultiLine, Number, Bool, Choice, ColorToken, ImageAsset, TextAsset, Variable, Glyph }

public sealed record PropertyDescriptor(string Name, string Label, PropertyKind Kind, string Default = "",
    IReadOnlyList<string>? Choices = null, double Min = 0, double Max = 0, bool Required = false, string Category = "Content");

public sealed class ControlDescriptor
{
    public string Type { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public double DefaultWidth { get; init; }
    public double DefaultHeight { get; init; }
    public bool IsContainer { get; init; }
    public bool SupportsAction { get; init; }
    public IReadOnlyList<PropertyDescriptor> Properties { get; init; } = Array.Empty<PropertyDescriptor>();

    public PropertyDescriptor? Find(string name) => Properties.FirstOrDefault(p => p.Name == name);
}

/// <summary>The property contract of every built-in control: what it can be configured with and how values are validated.</summary>
public static class ControlRegistry
{
    public static readonly Regex VariablePattern = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    public static IReadOnlyList<string> Glyphs { get; } =
        new[] { "info", "warning", "error", "success", "folder", "download", "settings", "shield", "app", "user" };

    private static readonly string[] Tokens = ThemeTokens.Required.Where(t => t.StartsWith("text") || t is "accent" or "accentText" or "success" or "warning" or "error").ToArray();
    private static readonly string[] TextStyles = { "caption", "body", "subtitle", "title" };

    private static PropertyDescriptor Text(string n, string l, string d = "", bool req = false, string cat = "Content") => new(n, l, PropertyKind.Text, d, Required: req, Category: cat);
    private static PropertyDescriptor Multi(string n, string l, string d = "", string cat = "Content") => new(n, l, PropertyKind.MultiLine, d, Category: cat);
    private static PropertyDescriptor Choice(string n, string l, string d, string[] c, string cat = "Appearance") => new(n, l, PropertyKind.Choice, d, c, Category: cat);
    private static PropertyDescriptor Bool(string n, string l, bool d, string cat = "Behavior") => new(n, l, PropertyKind.Bool, d ? "true" : "false", Category: cat);
    private static PropertyDescriptor Num(string n, string l, string d, double min, double max, string cat = "Appearance") => new(n, l, PropertyKind.Number, d, Min: min, Max: max, Category: cat);
    private static PropertyDescriptor Token(string n, string l, string d) => new(n, l, PropertyKind.ColorToken, d, Tokens, Category: "Appearance");
    private static PropertyDescriptor Var(string n = "variable", string l = "Variable") => new(n, l, PropertyKind.Variable, "", Required: true, Category: "Binding");

    private static ControlDescriptor D(string type, string name, string cat, double w, double h, bool container, bool action, params PropertyDescriptor[] props) =>
        new() { Type = type, DisplayName = name, Category = cat, DefaultWidth = w, DefaultHeight = h, IsContainer = container, SupportsAction = action, Properties = props };

    private static readonly ControlDescriptor[] Descriptors =
    {
        D(ControlTypes.Text, "Text", "Content", 300, 24, false, false,
            Multi("text", "Text", "Text"), Choice("style", "Style", "body", TextStyles), Token("color", "Colour", "textPrimary"),
            Choice("align", "Alignment", "left", new[] { "left", "center", "right" }), Bool("wrap", "Wrap text", true, "Appearance")),
        D(ControlTypes.RichText, "Rich Text", "Content", 400, 200, false, false,
            Multi("text", "Text (**bold**, *italic*)", "Rich **text**"), new("sourceAsset", "Text from asset", PropertyKind.TextAsset, "", Category: "Content"),
            Token("color", "Colour", "textPrimary")),
        D(ControlTypes.Image, "Image", "Content", 120, 120, false, false,
            new("asset", "Image asset", PropertyKind.ImageAsset, "", Required: true), Choice("stretch", "Stretch", "uniform", new[] { "uniform", "fill", "none", "uniformToFill" }),
            Text("altText", "Alt text")),
        D(ControlTypes.Button, "Button", "Input", 100, 32, false, true,
            Text("text", "Text", "Button"), Choice("style", "Style", "standard", new[] { "accent", "standard", "subtle" })),
        D(ControlTypes.TextInput, "Text Input", "Input", 300, 56, false, false,
            Text("label", "Label", "Label"), Text("placeholder", "Placeholder"), Var(), Text("defaultValue", "Default value", "", false, "Binding"), Bool("required", "Required", false)),
        D(ControlTypes.PasswordInput, "Password Input", "Input", 300, 56, false, false,
            Text("label", "Label", "Password"), Var(), Bool("required", "Required", false)),
        D(ControlTypes.Checkbox, "Checkbox", "Input", 300, 32, false, false,
            Text("label", "Label", "Checkbox"), Var(), Bool("defaultChecked", "Checked by default", false)),
        D(ControlTypes.Radio, "Radio Buttons", "Input", 300, 100, false, false,
            Text("label", "Group label", "Choose one"), Multi("options", "Options (one per line)", "Option 1\nOption 2"), Var(), Text("defaultValue", "Default option", "", false, "Binding")),
        D(ControlTypes.Dropdown, "Dropdown", "Input", 300, 56, false, false,
            Text("label", "Label", "Choose"), Multi("options", "Options (one per line)", "Option 1\nOption 2"), Var(), Text("defaultValue", "Default option", "", false, "Binding")),
        D(ControlTypes.List, "List", "Input", 300, 160, false, false,
            Choice("source", "Source", "items", new[] { "items", "components", "prerequisites" }, "Content"),
            Multi("items", "Items (one per line)", "Item 1\nItem 2\nItem 3"), new("variable", "Selected item variable", PropertyKind.Variable, "", Category: "Binding")),
        D(ControlTypes.Progress, "Progress Bar", "Feedback", 400, 16, false, false,
            Choice("source", "Source", "value", new[] { "value", "install" }, "Content"), Num("value", "Value (0-100)", "40", 0, 100, "Content"), Bool("indeterminate", "Indeterminate", false, "Appearance")),
        D(ControlTypes.Card, "Card", "Layout", 300, 160, true, false, Text("title", "Title", "Card")),
        D(ControlTypes.Stepper, "Stepper", "Feedback", 600, 40, false, false,
            Multi("steps", "Steps (one per line)", "Welcome\nInstall\nFinish"), Num("current", "Current step", "1", 1, 50, "Content")),
        D(ControlTypes.Accordion, "Accordion", "Layout", 400, 160, true, false,
            Text("header", "Header", "Section"), Bool("expanded", "Expanded", true)),
        D(ControlTypes.Separator, "Separator", "Layout", 400, 8, false, false,
            Choice("orientation", "Orientation", "horizontal", new[] { "horizontal", "vertical" })),
        D(ControlTypes.Icon, "Icon", "Content", 40, 40, false, false,
            new("glyph", "Glyph", PropertyKind.Glyph, "info", Glyphs, Category: "Content"), Num("size", "Size", "32", 8, 128), Token("color", "Colour", "accent")),
        D(ControlTypes.Container, "Container", "Layout", 300, 200, true, false,
            Choice("layout", "Layout", "absolute", new[] { "absolute", "vertical", "horizontal" }, "Layout"),
            Num("spacing", "Spacing", "8", 0, 64, "Layout"), Num("padding", "Padding", "0", 0, 64, "Layout"), Bool("stretch", "Stretch children", true, "Layout")),
        D(ControlTypes.Group, "Group", "Layout", 100, 100, true, false),
    };

    private static readonly Dictionary<string, ControlDescriptor> Map = Descriptors.ToDictionary(d => d.Type);

    public static IReadOnlyList<ControlDescriptor> All => Descriptors;
    public static bool TryGet(string type, out ControlDescriptor descriptor) => Map.TryGetValue(type, out descriptor!);
    public static ControlDescriptor Get(string type) => Map.TryGetValue(type, out var d) ? d : throw new ArgumentException($"Unknown control type '{type}'.");
    public static bool IsBuiltIn(string type) => Map.ContainsKey(type);
}
