using System.Globalization;

namespace PAIC.Controls;

/// <summary>Identifiers of the built-in control types. Custom controls use <see cref="CustomPrefix"/> + the control id.</summary>
public static class ControlTypes
{
    public const string Text = "text";
    public const string RichText = "richtext";
    public const string Image = "image";
    public const string Button = "button";
    public const string TextInput = "textinput";
    public const string PasswordInput = "passwordinput";
    public const string Checkbox = "checkbox";
    public const string Radio = "radio";
    public const string Dropdown = "dropdown";
    public const string List = "list";
    public const string Progress = "progress";
    public const string Card = "card";
    public const string Stepper = "stepper";
    public const string Accordion = "accordion";
    public const string Separator = "separator";
    public const string Icon = "icon";
    public const string Container = "container";
    public const string Group = "group";
    public const string CustomPrefix = "custom:";

    public static bool IsCustom(string type) => type.StartsWith(CustomPrefix, StringComparison.Ordinal);
    public static string CustomId(string type) => type[CustomPrefix.Length..];
}

public enum ElementActionKind { None, Next, Back, Cancel, Finish, GoToPage, BrowseFolder, SetVariable }

/// <summary>What a button does when clicked.</summary>
public sealed class ElementAction
{
    public ElementActionKind Kind { get; set; } = ElementActionKind.None;
    /// <summary>For GoToPage.</summary>
    public string TargetPageId { get; set; } = "";
    /// <summary>For BrowseFolder and SetVariable.</summary>
    public string Variable { get; set; } = "";
    /// <summary>For SetVariable.</summary>
    public string Value { get; set; } = "";

    public ElementAction Clone() => new() { Kind = Kind, TargetPageId = TargetPageId, Variable = Variable, Value = Value };
}

public enum ConditionOperator { IsTrue, IsFalse, Equals, NotEquals, Contains, IsEmpty, IsNotEmpty }

/// <summary>A simple condition on an installer variable, used for page/element visibility and enabling.</summary>
public sealed class ConditionDefinition
{
    public string Variable { get; set; } = "";
    public ConditionOperator Operator { get; set; } = ConditionOperator.IsTrue;
    public string Value { get; set; } = "";

    public ConditionDefinition Clone() => new() { Variable = Variable, Operator = Operator, Value = Value };
}

/// <summary>One element on an installer page (or inside a container / custom control template).</summary>
public sealed class DesignElement
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = ControlTypes.Text;
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 100;
    public double Height { get; set; } = 32;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new();
    public ElementAction? Action { get; set; }
    public ConditionDefinition? VisibleWhen { get; set; }
    public ConditionDefinition? EnabledWhen { get; set; }
    public List<DesignElement> Children { get; set; } = new();

    public DesignElement Clone() => new()
    {
        Id = Id, Type = Type, Name = Name, X = X, Y = Y, Width = Width, Height = Height, Visible = Visible, Locked = Locked,
        Properties = new Dictionary<string, string>(Properties),
        Action = Action?.Clone(), VisibleWhen = VisibleWhen?.Clone(), EnabledWhen = EnabledWhen?.Clone(),
        Children = Children.Select(c => c.Clone()).ToList()
    };

    /// <summary>Property value, falling back to the control descriptor's default.</summary>
    public string GetString(string name)
    {
        if (Properties.TryGetValue(name, out var v)) return v;
        return ControlRegistry.TryGet(Type, out var d) ? d.Find(name)?.Default ?? "" : "";
    }

    public double GetNumber(string name, double fallback = 0) =>
        double.TryParse(GetString(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    public bool GetBool(string name, bool fallback = false) =>
        bool.TryParse(GetString(name), out var b) ? b : fallback;

    public void Set(string name, string value) => Properties[name] = value;
    public void Set(string name, double value) => Properties[name] = value.ToString("0.###", CultureInfo.InvariantCulture);
    public void Set(string name, bool value) => Properties[name] = value ? "true" : "false";

    public IEnumerable<DesignElement> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            foreach (var d in c.Descendants()) yield return d;
        }
    }
}

public enum PageBackgroundKind { Default, Token, Color, Image }

public sealed class PageBackground
{
    public PageBackgroundKind Kind { get; set; } = PageBackgroundKind.Default;
    /// <summary>Token name (Token), #RRGGBB (Color) or asset id (Image).</summary>
    public string Value { get; set; } = "";

    public PageBackground Clone() => new() { Kind = Kind, Value = Value };
}
