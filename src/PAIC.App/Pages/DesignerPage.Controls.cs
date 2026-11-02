using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;
using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem.Model;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAIC.App.Pages;

public sealed partial class DesignerPage
{
    private void RefreshControlList()
    {
        CustomControls.Clear();
        if (Project is null) return;
        foreach (var c in Project.CustomControls) CustomControls.Add(new ControlItem { Definition = c });
    }

    private CustomControlDefinition? SelectedControl() => (ControlList.SelectedItem as ControlItem)?.Definition;

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "control" : slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    private string UniqueControlId(string baseId)
    {
        var id = baseId;
        for (var i = 2; Project!.CustomControls.Any(c => c.Id == id); i++) id = $"{baseId}-{i}";
        return id;
    }

    private void AddControlToProject(CustomControlDefinition def)
    {
        Project!.CustomControls.RemoveAll(c => c.Id == def.Id);
        Project.CustomControls.Add(def);
        _projects.MarkDirty();
        RefreshControlList();
        RefreshToolbox();
        ControlList.SelectedItem = CustomControls.FirstOrDefault(c => c.Definition.Id == def.Id);
    }

    // ---------------------------------------------------------------- create / edit / remove

    private async void CreateControl_Click(object sender, RoutedEventArgs e)
    {
        var doc = Doc;
        if (doc is null || Project is null || _editingControl is not null) return;
        var roots = doc.SelectionRoots();
        if (roots.Count == 0) { await UiDialogs.MessageAsync(XamlRoot, "Create control", "Select the elements that should become the control first."); return; }
        if (roots.Any(r => !ReferenceEquals(doc.ParentOf(r.Id), doc.ParentOf(roots[0].Id)))) { await UiDialogs.MessageAsync(XamlRoot, "Create control", "The selected elements must share the same parent."); return; }
        if (roots.SelectMany(r => new[] { r }.Concat(r.Descendants())).Any(x => ControlTypes.IsCustom(x.Type))) { await UiDialogs.MessageAsync(XamlRoot, "Create control", "Custom controls cannot contain other custom controls."); return; }

        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Create control", "Control name", "My control", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            var bounds = roots.Select(r => new PAIC.Controls.Rect(r.X, r.Y, r.Width, r.Height)).Aggregate((a, b) => a.Union(b));
            var def = new CustomControlDefinition { Id = UniqueControlId(Slug(name)), Name = name.Trim(), Width = bounds.Width, Height = bounds.Height };
            foreach (var r in roots)
            {
                var copy = r.Clone();
                copy.X -= bounds.X; copy.Y -= bounds.Y;
                def.Template.Add(copy);
            }
            ParameterizeVariables(def);
            AddControlToProject(def);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not create the control", ex); }
    }

    /// <summary>Turns each distinct variable name in the template into a parameter, so every instance can bind its own variable.</summary>
    private static void ParameterizeVariables(CustomControlDefinition def)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ParamFor(string variable)
        {
            if (map.TryGetValue(variable, out var p)) return p;
            var name = map.Count == 0 ? "Variable" : "Variable" + (map.Count + 1);
            map[variable] = name;
            def.Parameters.Add(new CustomParameter { Name = name, Label = $"Variable (was {variable})", Type = ParameterType.Text, Default = variable });
            return name;
        }
        string Token(string variable) => "{param:" + ParamFor(variable) + "}";

        foreach (var e in ElementIds.Flatten(def.Template))
        {
            foreach (var key in e.Properties.Keys.ToList())
                if (ControlRegistry.TryGet(e.Type, out var d) && d.Find(key)?.Kind == PropertyKind.Variable && !string.IsNullOrWhiteSpace(e.Properties[key]))
                    e.Properties[key] = Token(e.Properties[key]);
        }
        foreach (var e in ElementIds.Flatten(def.Template))
        {
            if (e.Action is { Kind: ElementActionKind.BrowseFolder or ElementActionKind.SetVariable } a && map.ContainsKey(a.Variable)) a.Variable = Token(a.Variable);
            foreach (var c in new[] { e.VisibleWhen, e.EnabledWhen })
                if (c is not null && map.ContainsKey(c.Variable)) c.Variable = Token(c.Variable);
        }
    }

    private void EditControl_Click(object sender, RoutedEventArgs e)
    {
        var def = SelectedControl();
        if (def is null || Project is null) return;
        _editingControl = def;
        _controlDoc = new DesignerDocument(def.Template, def.Width, def.Height, null);
        _controlDoc.Changed += OnDocChanged;
        _controlDoc.SelectionChanged += OnSelectionChanged;
        RefreshToolbox();
        FullRefresh();
    }

    private async void StopEditingControl()
    {
        var def = _editingControl;
        _editingControl = null; _controlDoc = null;
        RefreshToolbox();
        RefreshControlList();
        SelectPage(_page);
        if (def is null) return;
        var report = CustomControlValidator.Validate(def);
        if (report.HasErrors)
            await UiDialogs.MessageAsync(XamlRoot, "Control has problems",
                "The control was kept, but it has validation errors that must be fixed before building:" + Environment.NewLine +
                string.Join(Environment.NewLine, report.Issues.Where(i => i.Severity == PAIC.Core.Validation.ValidationSeverity.Error).Select(i => i.Message)));
    }

    private async void RemoveControl_Click(object sender, RoutedEventArgs e)
    {
        var def = SelectedControl();
        if (def is null || Project is null) return;
        var type = ControlTypes.CustomPrefix + def.Id;
        var count = Project.Pages.Sum(p => ElementIds.Flatten(p.Elements).Count(x => x.Type == type));
        if (count > 0)
        {
            await UiDialogs.MessageAsync(XamlRoot, "Remove control", $"'{def.Name}' is used by {count} element(s). Delete those elements first.");
            return;
        }
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Remove control", $"Remove '{def.Name}' from the project? A copy in the library is not affected.", "Remove")) return;
        Project.CustomControls.Remove(def);
        _projects.MarkDirty();
        RefreshControlList(); RefreshToolbox();
    }

    // ---------------------------------------------------------------- library, import, export

    private async void ControlToLibrary_Click(object sender, RoutedEventArgs e)
    {
        var def = SelectedControl();
        if (def is null) return;
        try
        {
            _controlLibrary.Save(def.Clone());
            await UiDialogs.MessageAsync(XamlRoot, "Library", $"'{def.Name}' was saved to your control library and can be added to other projects.");
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not save the control to the library", ex); }
    }

    private async void ControlFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _editingControl is not null) return;
        try
        {
            var library = _controlLibrary.Load();
            if (library.Count == 0) { await UiDialogs.MessageAsync(XamlRoot, "Library", "Your control library is empty. Save a control to the library first."); return; }
            var index = await UiDialogs.ChooseAsync(XamlRoot, "Add from library", "Control", library.Select(c => $"{c.Name} (v{c.Version})").ToList(), "Add");
            if (index < 0) return;
            await AddLibraryControlAsync(library[index]);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not add the control", ex); }
    }

    private async Task AddLibraryControlAsync(CustomControlDefinition def)
    {
        if (Project!.CustomControls.Any(c => c.Id == def.Id) &&
            !await UiDialogs.ConfirmAsync(XamlRoot, "Replace control", $"The project already has a control '{def.Id}'. Replace it with this version? Existing instances keep their parameter values.", "Replace")) return;
        AddControlToProject(def.Clone());
    }

    private async void ImportControl_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _editingControl is not null) return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(CustomControlSerializer.FileExtension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var def = _controlLibrary.Import(file.Path);
            await AddLibraryControlAsync(def);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not import the control", ex); }
    }

    private async void ExportControl_Click(object sender, RoutedEventArgs e)
    {
        var def = SelectedControl();
        if (def is null) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = def.Id };
            picker.FileTypeChoices.Add("PAIC control", new List<string> { CustomControlSerializer.FileExtension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            _controlLibrary.Export(def, file.Path);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not export the control", ex); }
    }

    // ---------------------------------------------------------------- control inspector (no selection while editing a control)

    private void BuildControlInspector()
    {
        var def = _editingControl!;
        Section("Custom control");
        Note("Edit the template on the canvas. Reference parameters as {param:Name} in property values.");
        AddText("Name", def.Name, v => { def.Name = v; _projects.MarkDirty(); RefreshControlList(); UpdateStateBar(); });
        AddText("Description", def.Description, v => { def.Description = v; _projects.MarkDirty(); }, multiline: true);
        AddText("Version", def.Version, v => { def.Version = v; _projects.MarkDirty(); RefreshControlList(); });
        AddNumber("Width", def.Width, 10, 2000, v => { def.Width = v; if (_controlDoc is not null) _controlDoc.Width = v; _projects.MarkDirty(); RenderSurface(); });
        AddNumber("Height", def.Height, 10, 2000, v => { def.Height = v; if (_controlDoc is not null) _controlDoc.Height = v; _projects.MarkDirty(); RenderSurface(); });

        Section("Parameters");
        if (def.Parameters.Count == 0) Note("No parameters yet.");
        foreach (var p in def.Parameters.ToList())
        {
            var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(new TextBlock { Text = $"{{param:{p.Name}}}  \u2022  {p.Type}  \u2022  default \"{p.Default}\"", TextWrapping = TextWrapping.Wrap });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var edit = new Button { Content = "Edit" };
            edit.Click += async (_, _) => await EditParameterAsync(def, p);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { def.Parameters.Remove(p); _projects.MarkDirty(); BuildInspector(); };
            buttons.Children.Add(edit); buttons.Children.Add(remove);
            row.Children.Add(buttons);
            Add(row);
        }
        var add = new Button { Content = "Add parameter..." };
        add.Click += async (_, _) => await EditParameterAsync(def, null);
        Add(add);

        Section("Validation");
        var report = CustomControlValidator.Validate(def);
        Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            Text = report.Issues.Count == 0 ? "No problems found." : string.Join(Environment.NewLine, report.Issues.Select(i => $"{i.Severity}: {i.Message}"))
        });
        var done = new Button { Content = "Done editing", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Margin = new Thickness(0, 8, 0, 0) };
        done.Click += (_, _) => StopEditingControl();
        Add(done);
    }

    private async Task EditParameterAsync(CustomControlDefinition def, CustomParameter? existing)
    {
        try
        {
            var name = await UiDialogs.PromptTextAsync(XamlRoot, existing is null ? "Add parameter" : "Edit parameter", "Name (letters and digits)", existing?.Name ?? "", "Next");
            if (string.IsNullOrWhiteSpace(name)) return;
            var label = await UiDialogs.PromptTextAsync(XamlRoot, "Parameter", "Label shown in the inspector", existing?.Label ?? name, "Next");
            if (label is null) return;
            var types = Enum.GetNames<ParameterType>();
            var typeIndex = await UiDialogs.ChooseAsync(XamlRoot, "Parameter", "Type", types, "Next");
            if (typeIndex < 0) return;
            var type = Enum.Parse<ParameterType>(types[typeIndex]);

            var choices = new List<string>();
            string? def0;
            if (type == ParameterType.Choice)
            {
                var text = await UiDialogs.PromptTextAsync(XamlRoot, "Parameter", "Choices (comma separated)", existing is null ? "" : string.Join(", ", existing.Choices), "Next");
                if (text is null) return;
                choices = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (choices.Count == 0) { await UiDialogs.MessageAsync(XamlRoot, "Parameter", "Enter at least one choice."); return; }
                var i = await UiDialogs.ChooseAsync(XamlRoot, "Parameter", "Default choice", choices, "Done");
                if (i < 0) return;
                def0 = choices[i];
            }
            else if (type == ParameterType.Bool)
            {
                var i = await UiDialogs.ChooseAsync(XamlRoot, "Parameter", "Default value", new[] { "false", "true" }, "Done");
                if (i < 0) return;
                def0 = i == 1 ? "true" : "false";
            }
            else
            {
                def0 = await UiDialogs.PromptTextAsync(XamlRoot, "Parameter", type == ParameterType.Number ? "Default value (number)" : "Default value", existing?.Default ?? (type == ParameterType.Number ? "0" : ""), "Done");
                if (def0 is null) return;
            }

            var param = existing ?? new CustomParameter();
            param.Name = name.Trim(); param.Label = label.Trim(); param.Type = type; param.Default = def0.Trim(); param.Choices = choices;
            if (existing is null) def.Parameters.Add(param);
            _projects.MarkDirty();
            BuildInspector();
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not edit the parameter", ex); }
    }
}
