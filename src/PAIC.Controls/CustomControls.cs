using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PAIC.Core;
using PAIC.Core.Validation;
using PAIC.ThemeEngine;

namespace PAIC.Controls;

public enum ParameterType { Text, Bool, Number, Choice }

/// <summary>A configurable property of a custom control. Template elements refer to it as {param:Name}.</summary>
public sealed class CustomParameter
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public ParameterType Type { get; set; } = ParameterType.Text;
    public string Default { get; set; } = "";
    public List<string> Choices { get; set; } = new();

    public CustomParameter Clone() => new() { Name = Name, Label = Label, Type = Type, Default = Default, Choices = new List<string>(Choices) };
}

/// <summary>
/// A reusable control composed of built-in elements. Contracts:
/// rendering = the template expands to a group of built-in elements; property = the parameter list;
/// validation = <see cref="CustomControlValidator"/>.
/// </summary>
public sealed class CustomControlDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "1.0";
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 100;
    public List<CustomParameter> Parameters { get; set; } = new();
    public List<DesignElement> Template { get; set; } = new();

    public CustomControlDefinition Clone() => new()
    {
        Id = Id, Name = Name, Description = Description, Version = Version, Width = Width, Height = Height,
        Parameters = Parameters.Select(p => p.Clone()).ToList(), Template = Template.Select(t => t.Clone()).ToList()
    };
}

public sealed class CustomControlException : UserFacingException
{
    public CustomControlException(string message) : base(message) { }
    public CustomControlException(string message, Exception inner) : base(message, inner) { }
}

public static class CustomControlExpander
{
    public static readonly Regex Placeholder = new(@"\{param:([A-Za-z][A-Za-z0-9]*)\}", RegexOptions.Compiled);

    /// <summary>Expands an instance into a group of built-in elements with parameter values substituted. Child ids are prefixed with the instance id.</summary>
    public static DesignElement Expand(DesignElement instance, CustomControlDefinition definition)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in definition.Parameters)
            values[p.Name] = instance.Properties.TryGetValue(p.Name, out var v) ? v : p.Default;

        string Sub(string s) => Placeholder.Replace(s, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        DesignElement Copy(DesignElement src)
        {
            var c = src.Clone();
            Apply(c);
            return c;
        }
        void Apply(DesignElement e)
        {
            e.Id = instance.Id + "." + e.Id;
            foreach (var key in e.Properties.Keys.ToList()) e.Properties[key] = Sub(e.Properties[key]);
            if (e.Action is { } a) { a.TargetPageId = Sub(a.TargetPageId); a.Variable = Sub(a.Variable); a.Value = Sub(a.Value); }
            foreach (var cond in new[] { e.VisibleWhen, e.EnabledWhen })
                if (cond is not null) { cond.Variable = Sub(cond.Variable); cond.Value = Sub(cond.Value); }
            foreach (var child in e.Children) Apply(child);
        }

        return new DesignElement
        {
            Id = instance.Id, Type = ControlTypes.Group, Name = instance.Name,
            X = instance.X, Y = instance.Y, Width = instance.Width, Height = instance.Height,
            Visible = instance.Visible, Locked = instance.Locked, VisibleWhen = instance.VisibleWhen?.Clone(),
            Children = definition.Template.Select(Copy).ToList()
        };
    }

    public static IEnumerable<string> PlaceholdersIn(DesignElement element)
    {
        foreach (var e in new[] { element }.Concat(element.Descendants()))
        {
            var texts = e.Properties.Values.AsEnumerable();
            if (e.Action is { } a) texts = texts.Concat(new[] { a.TargetPageId, a.Variable, a.Value });
            foreach (var c in new[] { e.VisibleWhen, e.EnabledWhen }.Where(c => c is not null))
                texts = texts.Concat(new[] { c!.Variable, c.Value });
            foreach (var t in texts)
                foreach (Match m in Placeholder.Matches(t ?? "")) yield return m.Groups[1].Value;
        }
    }
}

public static class CustomControlValidator
{
    private static readonly Regex IdPattern = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex ParamName = new("^[A-Za-z][A-Za-z0-9]*$", RegexOptions.Compiled);

    public static ValidationReport Validate(CustomControlDefinition d)
    {
        var report = new ValidationReport();
        void Add(string code, ValidationSeverity sev, string msg, string? loc = null) => report.Add(new ValidationIssue(code, sev, msg, loc));

        if (string.IsNullOrEmpty(d.Id) || d.Id.Length > 60 || !IdPattern.IsMatch(d.Id)) Add("PAIC1701", ValidationSeverity.Error, "Control id must use lowercase letters, digits and hyphens.", "id");
        if (string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 60) Add("PAIC1702", ValidationSeverity.Error, "Control name is required (max 60 characters).", "name");
        if (!(d.Width is >= 10 and <= 2000) || !(d.Height is >= 10 and <= 2000)) Add("PAIC1703", ValidationSeverity.Error, "Control width and height must be between 10 and 2000.", "size");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in d.Parameters)
        {
            var loc = $"parameters[{p.Name}]";
            if (!ParamName.IsMatch(p.Name ?? "")) Add("PAIC1704", ValidationSeverity.Error, $"Parameter name '{p.Name}' must start with a letter and contain only letters and digits.", loc);
            else if (!names.Add(p.Name)) Add("PAIC1704", ValidationSeverity.Error, $"Parameter '{p.Name}' is declared twice.", loc);
            switch (p.Type)
            {
                case ParameterType.Choice:
                    if (p.Choices.Count == 0) Add("PAIC1708", ValidationSeverity.Error, $"Parameter '{p.Name}' needs at least one choice.", loc);
                    else if (!p.Choices.Contains(p.Default)) Add("PAIC1708", ValidationSeverity.Error, $"Default of '{p.Name}' must be one of its choices.", loc);
                    break;
                case ParameterType.Bool when !bool.TryParse(p.Default, out _):
                    Add("PAIC1708", ValidationSeverity.Error, $"Default of '{p.Name}' must be true or false.", loc); break;
                case ParameterType.Number when !double.TryParse(p.Default, NumberStyles.Float, CultureInfo.InvariantCulture, out _):
                    Add("PAIC1708", ValidationSeverity.Error, $"Default of '{p.Name}' must be a number.", loc); break;
            }
        }

        if (d.Template.Count == 0) Add("PAIC1709", ValidationSeverity.Warning, "The control template is empty.", "template");
        foreach (var e in ElementIds.Flatten(d.Template))
            if (ControlTypes.IsCustom(e.Type))
                Add("PAIC1706", ValidationSeverity.Error, $"Template element '{e.Id}' is a custom control; custom controls cannot contain other custom controls.", $"template[{e.Id}]");

        foreach (var root in d.Template)
            foreach (var used in CustomControlExpander.PlaceholdersIn(root).Distinct())
                if (!names.Contains(used))
                    Add("PAIC1705", ValidationSeverity.Error, $"Placeholder {{param:{used}}} is not a declared parameter.", $"template[{root.Id}]");

        var ctx = new ElementValidationContext { Width = d.Width, Height = d.Height, AllowPlaceholders = true, LocationPrefix = "template" };
        foreach (var issue in ElementValidator.Validate(d.Template, ctx)) report.Add(issue);
        return report;
    }
}

/// <summary>.paiccontrol files: { format: "paic-control", schemaVersion: 1, control: {...} }.</summary>
public static class CustomControlSerializer
{
    public const string FileExtension = ".paiccontrol";
    public const string Format = "paic-control";
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(CustomControlDefinition control) =>
        new JsonObject { ["format"] = Format, ["schemaVersion"] = SchemaVersion, ["control"] = JsonSerializer.SerializeToNode(control, Options) }
            .ToJsonString(Options);

    public static CustomControlDefinition Deserialize(string json)
    {
        if (json.Length > 2_000_000) throw new CustomControlException("The control file is too large.");
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new CustomControlException("The control file is not valid JSON.", ex); }
        if (node is not JsonObject root || root["format"]?.GetValue<string>() != Format)
            throw new CustomControlException("This is not a PAIC control file.");
        int version;
        try { version = root["schemaVersion"]?.GetValue<int>() ?? 0; }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException) { throw new CustomControlException("Invalid schemaVersion.", ex); }
        if (version != SchemaVersion) throw new CustomControlException($"Unsupported control schema version {version}.");

        CustomControlDefinition def;
        try { def = root["control"].Deserialize<CustomControlDefinition>(Options) ?? throw new CustomControlException("The file contains no control."); }
        catch (JsonException ex) { throw new CustomControlException($"The control file is malformed: {ex.Message}", ex); }

        var report = CustomControlValidator.Validate(def);
        if (report.HasErrors)
            throw new CustomControlException("The control is invalid: " + string.Join(" ", report.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(3).Select(i => i.Message)));
        return def;
    }
}

/// <summary>Per-user library of custom controls, reusable across projects.</summary>
public interface IControlLibrary
{
    IReadOnlyList<CustomControlDefinition> Load();
    void Save(CustomControlDefinition control);
    void Delete(string id);
    CustomControlDefinition Import(string file);
    void Export(CustomControlDefinition control, string file);
}

public sealed class ControlLibrary : IControlLibrary
{
    private readonly string _directory;
    public ControlLibrary(string directory) => _directory = directory;

    public IReadOnlyList<CustomControlDefinition> Load()
    {
        var list = new List<CustomControlDefinition>();
        if (!Directory.Exists(_directory)) return list;
        foreach (var file in Directory.EnumerateFiles(_directory, "*" + CustomControlSerializer.FileExtension))
        {
            try { list.Add(CustomControlSerializer.Deserialize(File.ReadAllText(file))); }
            catch (Exception ex) when (ex is CustomControlException or IOException or UnauthorizedAccessException) { }
        }
        return list.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(CustomControlDefinition control)
    {
        var report = CustomControlValidator.Validate(control);
        if (report.HasErrors) throw new CustomControlException("The control has validation errors and cannot be saved to the library.");
        try
        {
            Directory.CreateDirectory(_directory);
            WriteAtomic(PathFor(control.Id), CustomControlSerializer.Serialize(control));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CustomControlException($"Could not save the control: {ex.Message}", ex); }
    }

    public void Delete(string id)
    {
        try { var p = PathFor(id); if (File.Exists(p)) File.Delete(p); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CustomControlException($"Could not delete the control: {ex.Message}", ex); }
    }

    public CustomControlDefinition Import(string file)
    {
        string json;
        try { json = File.ReadAllText(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CustomControlException($"Could not read the control file: {ex.Message}", ex); }
        var def = CustomControlSerializer.Deserialize(json);
        Save(def);
        return def;
    }

    public void Export(CustomControlDefinition control, string file)
    {
        try { WriteAtomic(file, CustomControlSerializer.Serialize(control)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new CustomControlException($"Could not export the control: {ex.Message}", ex); }
    }

    private string PathFor(string id)
    {
        if (!ThemeValidator.IdPattern.IsMatch(id)) throw new CustomControlException("Invalid control id.");
        return Path.Combine(_directory, id + CustomControlSerializer.FileExtension);
    }

    private static void WriteAtomic(string file, string content)
    {
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
    }
}
