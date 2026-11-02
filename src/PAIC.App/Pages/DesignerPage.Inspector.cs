using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PAIC.App.Services;
using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;
using Windows.System;

namespace PAIC.App.Pages;

public sealed partial class DesignerPage
{
    private NumberBox? _xBox, _yBox, _wBox, _hBox;
    private string? _boundsId;
    private bool _updatingBounds;

    private const string None = "(none)";
    private const string Auto = "(automatic)";

    // ---------------------------------------------------------------- inspector plumbing

    private void Edit(Action action)
    {
        _suppressInspector++;
        try { action(); }
        catch (Exception ex) { _ = _errors.ReportAsync("The change could not be applied", ex); }
        finally { _suppressInspector--; }
    }

    private void Add(UIElement e) => InspectorPanel.Children.Add(e);

    private void Section(string title) =>
        Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) });

    private void Note(string text) =>
        Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });

    private TextBox AddText(string label, string value, Action<string> commit, bool multiline = false, string? placeholder = null)
    {
        var tb = new TextBox { Header = label, Text = value, PlaceholderText = placeholder ?? "", AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 80 : 0 };
        var committed = value;
        void Commit()
        {
            if (tb.Text == committed) return;
            committed = tb.Text;
            commit(tb.Text);
        }
        tb.LostFocus += (_, _) => Commit();
        if (!multiline) tb.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Commit(); e.Handled = true; } };
        Add(tb);
        return tb;
    }

    private NumberBox AddNumber(string label, double value, double min, double max, Action<double> commit)
    {
        var nb = new NumberBox { Header = label, Value = value, Minimum = min, Maximum = max, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, SmallChange = 1, LargeChange = 10 };
        nb.ValueChanged += (_, a) => { if (!_updatingBounds && !double.IsNaN(a.NewValue) && Math.Abs(a.NewValue - a.OldValue) > 1e-9) commit(a.NewValue); };
        Add(nb);
        return nb;
    }

    private CheckBox AddBool(string label, bool value, Action<bool> commit)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.Click += (_, _) => commit(cb.IsChecked == true);
        Add(cb);
        return cb;
    }

    private ComboBox AddChoice(string label, IReadOnlyList<string> choices, string current, Action<string> commit)
    {
        var items = choices.ToList();
        if (!items.Contains(current)) items.Insert(0, current);
        var cb = new ComboBox { Header = label, ItemsSource = items, SelectedItem = current, HorizontalAlignment = HorizontalAlignment.Stretch };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != current) { current = s; commit(s); } };
        Add(cb);
        return cb;
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- inspector

    private void BuildInspector()
    {
        InspectorPanel.Children.Clear();
        _xBox = _yBox = _wBox = _hBox = null; _boundsId = null;
        var doc = Doc;
        if (doc is null || Project is null) return;
        var roots = doc.SelectionRoots();
        if (roots.Count == 1) BuildElementInspector(roots[0]);
        else if (roots.Count > 1)
        {
            Section($"{roots.Count} elements selected");
            Note("Use the Align menu to align, distribute and match sizes. Select a single element to edit its properties.");
        }
        else if (_editingControl is not null) BuildControlInspector();
        else BuildPageInspector();
    }

    private void RefreshBounds()
    {
        var doc = Doc;
        if (doc is null || _boundsId is null || _xBox is null) return;
        var el = doc.Find(_boundsId);
        if (el is null) return;
        _updatingBounds = true;
        _xBox.Value = Math.Round(el.X, 2); _yBox!.Value = Math.Round(el.Y, 2);
        _wBox!.Value = Math.Round(el.Width, 2); _hBox!.Value = Math.Round(el.Height, 2);
        _updatingBounds = false;
    }

    private string TypeDisplay(string type)
    {
        if (ControlRegistry.TryGet(type, out var d)) return d.DisplayName;
        if (ControlTypes.IsCustom(type))
            return Project?.CustomControls.FirstOrDefault(c => c.Id == ControlTypes.CustomId(type))?.Name is { } n ? $"{n} (custom control)" : "Custom control";
        return type;
    }

    private IReadOnlyList<string> AssetIds(params AssetCategory[] categories) =>
        new[] { None }.Concat(Project!.Assets.Where(a => categories.Length == 0 || categories.Contains(a.Category)).Select(a => a.Id)).ToList();

    private void BuildElementInspector(DesignElement el)
    {
        var doc = Doc!;
        var id = el.Id;
        Section(TypeDisplay(el.Type));
        Note($"Id: {id}");
        AddText("Name", el.Name, v => Edit(() => doc.SetName(id, v)));

        Section("Layout");
        var parent = doc.ParentOf(id);
        var stacked = parent is not null && LayoutEngine.IsStack(parent);
        if (stacked) Note("Position is managed by the parent container's stack layout.");

        void Bounds() { if (_xBox is null || _updatingBounds) return; Edit(() => doc.SetBounds(id, _xBox.Value, _yBox!.Value, _wBox!.Value, _hBox!.Value)); }
        _xBox = AddNumber("X", Math.Round(el.X, 2), -5000, 5000, _ => Bounds());
        _yBox = AddNumber("Y", Math.Round(el.Y, 2), -5000, 5000, _ => Bounds());
        _wBox = AddNumber("Width", Math.Round(el.Width, 2), 1, 5000, _ => Bounds());
        _hBox = AddNumber("Height", Math.Round(el.Height, 2), 1, 5000, _ => Bounds());
        _boundsId = id;
        _xBox.IsEnabled = _yBox.IsEnabled = !stacked && !el.Locked;
        _wBox.IsEnabled = _hBox.IsEnabled = !el.Locked;

        AddBool("Visible", el.Visible, v => Edit(() => doc.SetVisible(id, v)));
        AddBool("Locked", el.Locked, v => Edit(() => doc.SetLocked(id, v)));

        if (ControlRegistry.TryGet(el.Type, out var descriptor))
        {
            foreach (var group in descriptor.Properties.GroupBy(p => p.Category))
            {
                Section(group.Key);
                foreach (var p in group) AddProperty(el, p);
            }
            if (descriptor.SupportsAction) BuildActionEditor(el);
        }
        else if (ControlTypes.IsCustom(el.Type) && Project!.CustomControls.FirstOrDefault(c => c.Id == ControlTypes.CustomId(el.Type)) is { } def)
        {
            Section("Parameters");
            if (def.Parameters.Count == 0) Note("This control has no parameters.");
            foreach (var param in def.Parameters) AddParameterEditor(el, param);
        }

        Section("Conditions");
        AddCondition("Show only when...", el.VisibleWhen, c => Edit(() => doc.SetVisibleWhen(id, c)));
        if (el.Type is ControlTypes.Button or ControlTypes.TextInput or ControlTypes.PasswordInput or ControlTypes.Checkbox or ControlTypes.Radio or ControlTypes.Dropdown or ControlTypes.List)
            AddCondition("Enable only when...", el.EnabledWhen, c => Edit(() => doc.SetEnabledWhen(id, c)));
    }

    private void AddProperty(DesignElement el, PropertyDescriptor p)
    {
        var doc = Doc!;
        var id = el.Id;
        var label = p.Required ? p.Label + " *" : p.Label;
        var value = el.GetString(p.Name);
        void Set(string v) => Edit(() => doc.SetProperty(id, p.Name, v));

        // When editing a custom control template every value may be a {param:Name} placeholder, so use plain text boxes.
        if (_editingControl is not null && p.Kind is not (PropertyKind.Text or PropertyKind.MultiLine or PropertyKind.Variable))
        {
            AddText(label, value, Set, placeholder: "value or {param:Name}");
            return;
        }

        switch (p.Kind)
        {
            case PropertyKind.Text or PropertyKind.Variable: AddText(label, value, Set); break;
            case PropertyKind.MultiLine: AddText(label, value, Set, multiline: true); break;
            case PropertyKind.Number:
                AddNumber(label, el.GetNumber(p.Name, 0), p.Min, p.Max, v => Set(Fmt(v))); break;
            case PropertyKind.Bool: AddBool(label, el.GetBool(p.Name), v => Set(v ? "true" : "false")); break;
            case PropertyKind.Choice or PropertyKind.ColorToken or PropertyKind.Glyph:
                AddChoice(label, p.Choices ?? Array.Empty<string>(), value, Set); break;
            case PropertyKind.ImageAsset:
                AddChoice(label, AssetIds(AssetCategory.Image, AssetCategory.Icon), string.IsNullOrEmpty(value) ? None : value, v => Set(v == None ? "" : v)); break;
            case PropertyKind.TextAsset:
                AddChoice(label, AssetIds(AssetCategory.Document, AssetCategory.License, AssetCategory.Other), string.IsNullOrEmpty(value) ? None : value, v => Set(v == None ? "" : v)); break;
        }
    }

    private void AddParameterEditor(DesignElement el, CustomParameter param)
    {
        var doc = Doc!;
        var id = el.Id;
        var current = el.Properties.TryGetValue(param.Name, out var v) ? v : param.Default;
        var label = string.IsNullOrWhiteSpace(param.Label) ? param.Name : param.Label;
        void Set(string value) => Edit(() => doc.SetProperty(id, param.Name, value));
        switch (param.Type)
        {
            case ParameterType.Bool: AddBool(label, ConditionEvaluator.IsTruthy(current), b => Set(b ? "true" : "false")); break;
            case ParameterType.Number:
                AddNumber(label, double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0, -100000, 100000, d => Set(Fmt(d))); break;
            case ParameterType.Choice: AddChoice(label, param.Choices, current, Set); break;
            default: AddText(label, current, Set); break;
        }
    }

    private void AddCondition(string title, ConditionDefinition? current, Action<ConditionDefinition?> setter)
    {
        var cond = current?.Clone() ?? new ConditionDefinition();
        var detail = new StackPanel { Spacing = 6, Visibility = current is null ? Visibility.Collapsed : Visibility.Visible };
        var toggle = new CheckBox { Content = title, IsChecked = current is not null };
        toggle.Click += (_, _) =>
        {
            detail.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            setter(toggle.IsChecked == true ? cond.Clone() : null);
        };
        Add(toggle);

        var variable = new TextBox { Header = "Variable", Text = cond.Variable };
        variable.LostFocus += (_, _) => { if (variable.Text != cond.Variable) { cond.Variable = variable.Text.Trim(); setter(cond.Clone()); } };
        var op = new ComboBox { Header = "Operator", ItemsSource = Enum.GetNames<ConditionOperator>(), SelectedItem = cond.Operator.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        op.SelectionChanged += (_, _) =>
        {
            if (op.SelectedItem is string s && Enum.TryParse<ConditionOperator>(s, out var parsed) && parsed != cond.Operator) { cond.Operator = parsed; setter(cond.Clone()); }
        };
        var value = new TextBox { Header = "Value", Text = cond.Value };
        value.LostFocus += (_, _) => { if (value.Text != cond.Value) { cond.Value = value.Text; setter(cond.Clone()); } };
        detail.Children.Add(variable); detail.Children.Add(op); detail.Children.Add(value);
        Add(detail);
    }

    private void BuildActionEditor(DesignElement el)
    {
        var doc = Doc!;
        var id = el.Id;
        var action = el.Action?.Clone() ?? new ElementAction();
        Section("Action (on click)");
        var kind = new ComboBox { Header = "Action", ItemsSource = Enum.GetNames<ElementActionKind>(), SelectedItem = action.Kind.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedItem is string s && Enum.TryParse<ElementActionKind>(s, out var parsed) && parsed != action.Kind)
            {
                action.Kind = parsed;
                Edit(() => doc.SetAction(id, action));
                BuildInspector();
            }
        };
        Add(kind);

        if (action.Kind == ElementActionKind.GoToPage)
            AddChoice("Target page", Project!.Pages.Select(p => p.Id).ToList(), action.TargetPageId, v => { action.TargetPageId = v; Edit(() => doc.SetAction(id, action)); });
        if (action.Kind is ElementActionKind.BrowseFolder or ElementActionKind.SetVariable)
            AddText("Variable", action.Variable, v => { action.Variable = v.Trim(); Edit(() => doc.SetAction(id, action)); });
        if (action.Kind == ElementActionKind.SetVariable)
            AddText("Value", action.Value, v => { action.Value = v; Edit(() => doc.SetAction(id, action)); });
    }

    // ---------------------------------------------------------------- page inspector

    private void BuildPageInspector()
    {
        var page = _page;
        var project = Project;
        if (page is null || project is null) return;

        Section("Page");
        Note("Page settings are saved with the project and are not part of the undo history.");
        AddText("Title", page.Title, v => { page.Title = v; PageChanged(); RefreshPages(page.Id); });
        Note($"Type: {page.Type}   \u2022   Id: {page.Id}");

        Section("Background");
        var kinds = Enum.GetNames<PageBackgroundKind>();
        AddChoice("Kind", kinds, page.Background.Kind.ToString(), v =>
        {
            page.Background = new PageBackground { Kind = Enum.Parse<PageBackgroundKind>(v) };
            PageChanged(); RenderSurface(); BuildInspector();
        });
        switch (page.Background.Kind)
        {
            case PageBackgroundKind.Token:
                AddChoice("Colour token", ThemeTokens.Required.ToList(), string.IsNullOrEmpty(page.Background.Value) ? ThemeTokens.Background : page.Background.Value,
                    v => { page.Background.Value = v; PageChanged(); RenderSurface(); });
                break;
            case PageBackgroundKind.Color:
                AddText("Colour (#RRGGBB)", page.Background.Value, v => { page.Background.Value = v.Trim(); PageChanged(); RenderSurface(); });
                break;
            case PageBackgroundKind.Image:
                AddChoice("Image asset", AssetIds(AssetCategory.Image), string.IsNullOrEmpty(page.Background.Value) ? None : page.Background.Value,
                    v => { page.Background.Value = v == None ? "" : v; PageChanged(); RenderSurface(); });
                break;
        }

        Section("Navigation");
        AddBool("Allow Back", page.AllowBack, v => { page.AllowBack = v; PageChanged(); });
        AddBool("Allow Cancel", page.AllowCancel, v => { page.AllowCancel = v; PageChanged(); });
        var ids = new[] { Auto }.Concat(project.Pages.Where(p => p.Id != page.Id).Select(p => p.Id)).ToList();
        AddChoice("Next page", ids, string.IsNullOrEmpty(page.NextPageId) ? Auto : page.NextPageId, v => { page.NextPageId = v == Auto ? "" : v; PageChanged(); });
        AddChoice("Back page", ids, string.IsNullOrEmpty(page.BackPageId) ? Auto : page.BackPageId, v => { page.BackPageId = v == Auto ? "" : v; PageChanged(); });
        AddCondition("Show page only when...", page.VisibleWhen, c => { page.VisibleWhen = c; PageChanged(); });

        Section("Installer window (all pages)");
        void Size() { if (_updatingBounds) return; PageChanged(); foreach (var d in _docs.Values) { d.Width = project.Installation.WindowWidth; d.Height = project.Installation.WindowHeight; } RenderSurface(); }
        AddNumber("Width", project.Installation.WindowWidth, 320, 3840, v => { project.Installation.WindowWidth = v; Size(); });
        AddNumber("Height", project.Installation.WindowHeight, 240, 2160, v => { project.Installation.WindowHeight = v; Size(); });
    }

    private void PageChanged() => _projects.MarkDirty();

    // ---------------------------------------------------------------- page list commands

    private int CurrentPageIndex() => Project is null || _page is null ? -1 : Project.Pages.IndexOf(_page);

    private async void AddPage_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _editingControl is not null) return;
        try
        {
            var names = Enum.GetNames<InstallerPageType>();
            var index = await UiDialogs.ChooseAsync(XamlRoot, "Add page", "Page type", names, "Add");
            if (index < 0) return;
            var type = Enum.Parse<InstallerPageType>(names[index]);
            string? title = null;
            if (type == InstallerPageType.Custom)
            {
                title = await UiDialogs.PromptTextAsync(XamlRoot, "Add page", "Page title", "New page", "Add");
                if (string.IsNullOrWhiteSpace(title)) return;
            }
            var page = PageService.AddPage(Project, type, title, CurrentPageIndex() is var i && i >= 0 ? i : null);
            _projects.MarkDirty();
            RefreshPages(page.Id);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not add the page", ex); }
    }

    private void DuplicatePage_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _page is null || _editingControl is not null) return;
        Guard(() => { var copy = PageService.DuplicatePage(Project, _page.Id); _projects.MarkDirty(); RefreshPages(copy.Id); });
    }

    private async void RemovePage_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _page is null || _editingControl is not null) return;
        try
        {
            var uses = PageService.FindReferences(Project, _page.Id);
            var message = uses.Count == 0
                ? $"Delete the page '{_page.Title}'?"
                : $"Delete the page '{_page.Title}'? These navigation references will be reset:{Environment.NewLine}{string.Join(Environment.NewLine, uses)}";
            if (!await UiDialogs.ConfirmAsync(XamlRoot, "Delete page", message, "Delete")) return;
            var index = Math.Max(0, CurrentPageIndex() - 1);
            _docs.Remove(_page.Id);
            PageService.RemovePage(Project, _page.Id, clearReferences: true);
            _projects.MarkDirty();
            RefreshPages(Project.Pages.Count > 0 ? Project.Pages[Math.Min(index, Project.Pages.Count - 1)].Id : null);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not delete the page", ex); }
    }

    private void PageUp_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void PageDown_Click(object sender, RoutedEventArgs e) => MovePage(1);

    private void MovePage(int delta)
    {
        if (Project is null || _page is null || _editingControl is not null) return;
        Guard(() =>
        {
            PageService.MovePage(Project, _page.Id, CurrentPageIndex() + delta);
            _projects.MarkDirty();
            RefreshPages(_page.Id);
        });
    }

    private async void ResetLayout_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null || _page is null || _editingControl is not null) return;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Reset layout", "Replace all elements on this page with the default layout for its type?", "Reset")) return;
        Guard(() => { PageService.ResetLayout(Project, _page.Id); _docs.Remove(_page.Id); _projects.MarkDirty(); RefreshPages(_page.Id); });
    }
}
