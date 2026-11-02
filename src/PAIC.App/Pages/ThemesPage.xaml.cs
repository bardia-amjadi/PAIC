using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PAIC.App.Services;
using PAIC.Core.Validation;
using PAIC.ProjectSystem;
using PAIC.ThemeEngine;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAIC.App.Pages;

public enum ThemeSource { Project, Library, BuiltIn }

/// <summary>A row in the theme list.</summary>
public sealed class ThemeItem
{
    public ThemeDefinition Theme { get; init; } = new();
    public ThemeSource Source { get; init; }
    public bool IsActive { get; init; }
    public string Title => Theme.Name + (IsActive ? "  (active)" : "");
    public string Subtitle => $"{Source switch { ThemeSource.Project => "In project", ThemeSource.Library => "Library", _ => "Built-in" }} \u2022 {Theme.Mode}";
}

public sealed partial class ThemesPage : Page
{
    private static readonly Windows.UI.Color Fallback = Colors.Gray;

    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IThemeLibrary _library = App.Services.GetRequiredService<IThemeLibrary>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();
    private readonly Validator<ThemeDefinition> _validator = ThemeValidator.CreateDefault();
    private readonly Dictionary<string, TextBox> _colorBoxes = new();
    private readonly Dictionary<string, Border> _swatches = new();
    private bool _loading;

    public ObservableCollection<ThemeItem> Items { get; } = new();
    private ThemeItem? Selected => ThemeList.SelectedItem as ThemeItem;

    public ThemesPage()
    {
        InitializeComponent();
        ModeBox.ItemsSource = Enum.GetValues<ThemeMode>();
        MaterialBox.ItemsSource = Enum.GetValues<ThemeMaterial>();
        BuildColorRows();
        Loaded += (_, _) => { _projects.Changed += OnProjectChanged; Refresh(null); };
        Unloaded += (_, _) => _projects.Changed -= OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (_projects.Current is null) Refresh(null); });

    // ---------- list ----------

    private void Refresh(string? selectId, ThemeSource? selectSource = null)
    {
        var keepId = selectId ?? Selected?.Theme.Id;
        var keepSource = selectSource ?? Selected?.Source;
        Items.Clear();
        var project = _projects.Current;
        if (project is not null)
            foreach (var t in project.Theme.Themes)
                Items.Add(new ThemeItem { Theme = t, Source = ThemeSource.Project, IsActive = t.Id == project.Theme.ActiveThemeId });
        foreach (var t in _library.LoadUserThemes()) Items.Add(new ThemeItem { Theme = t, Source = ThemeSource.Library });
        foreach (var t in _library.BuiltIn) Items.Add(new ThemeItem { Theme = t, Source = ThemeSource.BuiltIn });

        var match = Items.FirstOrDefault(i => i.Theme.Id == keepId && (keepSource is null || i.Source == keepSource)) ?? Items.FirstOrDefault();
        ThemeList.SelectedItem = match;
        ShowSelected();
    }

    private void ThemeList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        var item = Selected;
        var hasProject = _projects.Current is not null;
        var inProject = item?.Source == ThemeSource.Project;

        AddButton.IsEnabled = hasProject && item is { Source: not ThemeSource.Project };
        ActiveButton.IsEnabled = inProject && item is { IsActive: false };
        DuplicateButton.IsEnabled = hasProject && item is not null;
        RemoveButton.IsEnabled = inProject && item is { IsActive: false };
        LibraryButton.IsEnabled = item is { Source: ThemeSource.Project };
        DeleteLibraryButton.IsEnabled = item is { Source: ThemeSource.Library };
        ExportButton.IsEnabled = item is not null;

        if (item is null)
        {
            EditorPanel.Visibility = PreviewFrame.Visibility = PreviewHeader.Visibility = Visibility.Collapsed;
            StateBar.Message = "No themes available."; StateBar.IsOpen = true;
            return;
        }

        StateBar.IsOpen = true;
        StateBar.Message = inProject
            ? "Editing a theme stored in this project. Changes mark the project as modified."
            : hasProject
                ? "This theme is read-only here. Use 'Add to project' to get an editable copy."
                : "Open a project to edit themes. You can still preview, import and export themes.";

        _loading = true;
        var t = item.Theme;
        NameBox.Text = t.Name;
        IdText.Text = $"Id: {t.Id}";
        ModeBox.SelectedItem = t.Mode;
        MaterialBox.SelectedItem = t.Material;
        FillBackgroundImages(t);
        foreach (var token in ThemeTokens.Required)
            _colorBoxes[token].Text = t.Colors.TryGetValue(token, out var hex) ? hex : "";
        FontFamilyBox.Text = t.Typography.FontFamily;
        CaptionBox.Value = t.Typography.CaptionSize; BodyBox.Value = t.Typography.BodySize;
        SubtitleBox.Value = t.Typography.SubtitleSize; TitleBox.Value = t.Typography.TitleSize;
        RadiusBox.Value = t.Shape.CornerRadius; HeightBox.Value = t.Shape.ControlHeight; SpacingBox.Value = t.Shape.Spacing;
        EditorPanel.IsHitTestVisible = inProject;
        EditorPanel.Opacity = inProject ? 1 : 0.6;
        EditorPanel.Visibility = PreviewFrame.Visibility = PreviewHeader.Visibility = Visibility.Visible;
        _loading = false;

        Revalidate(t);
        ApplyPreview(t);
    }

    private void FillBackgroundImages(ThemeDefinition t)
    {
        var ids = new List<string> { "(none)" };
        if (_projects.Current is not null)
            ids.AddRange(_projects.Current.Assets.Where(a => a.Category == ProjectSystem.Model.AssetCategory.Image).Select(a => a.Id));
        if (!string.IsNullOrEmpty(t.BackgroundImageAssetId) && !ids.Contains(t.BackgroundImageAssetId)) ids.Add(t.BackgroundImageAssetId);
        BackgroundImageBox.ItemsSource = ids;
        BackgroundImageBox.SelectedItem = string.IsNullOrEmpty(t.BackgroundImageAssetId) ? "(none)" : t.BackgroundImageAssetId;
    }

    // ---------- editor construction ----------

    private void BuildColorRows()
    {
        foreach (var token in ThemeTokens.Required)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock { Text = token, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { PlaceholderText = "#RRGGBB", Tag = token };
            box.TextChanged += ColorBox_Changed;
            var swatch = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Gray)
            };
            Grid.SetColumn(box, 1); Grid.SetColumn(swatch, 2);
            row.Children.Add(label); row.Children.Add(box); row.Children.Add(swatch);
            ColorsPanel.Children.Add(row);
            _colorBoxes[token] = box;
            _swatches[token] = swatch;
        }
    }

    // ---------- editing ----------

    private void Edit(Action<ThemeDefinition> change)
    {
        if (_loading || Selected is not { Source: ThemeSource.Project } item) return;
        change(item.Theme);
        _projects.MarkDirty();
        Revalidate(item.Theme);
        ApplyPreview(item.Theme);
    }

    private void Name_Changed(object sender, TextChangedEventArgs e) => Edit(t => t.Name = NameBox.Text);
    private void Mode_Changed(object sender, SelectionChangedEventArgs e) { if (ModeBox.SelectedItem is ThemeMode m) Edit(t => t.Mode = m); }
    private void Material_Changed(object sender, SelectionChangedEventArgs e) { if (MaterialBox.SelectedItem is ThemeMaterial m) Edit(t => t.Material = m); }
    private void FontFamily_Changed(object sender, TextChangedEventArgs e) => Edit(t => t.Typography.FontFamily = FontFamilyBox.Text);

    private void BackgroundImage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (BackgroundImageBox.SelectedItem is not string id) return;
        Edit(t => t.BackgroundImageAssetId = id == "(none)" ? "" : id);
    }

    private void ColorBox_Changed(object sender, TextChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var token = (string)box.Tag;
        _swatches[token].Background = UiColor.Brush(box.Text, Fallback);
        Edit(t => t.Colors[token] = box.Text.Trim());
    }

    private void Number_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) return;
        Edit(t =>
        {
            t.Typography.CaptionSize = CaptionBox.Value; t.Typography.BodySize = BodyBox.Value;
            t.Typography.SubtitleSize = SubtitleBox.Value; t.Typography.TitleSize = TitleBox.Value;
            t.Shape.CornerRadius = RadiusBox.Value; t.Shape.ControlHeight = HeightBox.Value; t.Shape.Spacing = SpacingBox.Value;
        });
    }

    private void Revalidate(ThemeDefinition t)
    {
        foreach (var token in ThemeTokens.Required)
            _swatches[token].Background = UiColor.Brush(t.Colors.TryGetValue(token, out var hex) ? hex : null, Fallback);
        var report = _validator.Validate(t);
        ValidationText.Text = report.Issues.Count == 0
            ? "No problems found."
            : string.Join(Environment.NewLine, report.Issues.Select(i => $"{i.Severity}: {i.Message}"));
    }

    private void ApplyPreview(ThemeDefinition t)
    {
        string C(string token) => t.Colors.TryGetValue(token, out var v) ? v : "";
        SolidColorBrush B(string token) => UiColor.Brush(C(token), Fallback);

        PreviewFrame.Background = B(ThemeTokens.Background);
        PreviewFrame.BorderBrush = B(ThemeTokens.Border);
        PreviewFrame.CornerRadius = new CornerRadius(Math.Clamp(t.Shape.CornerRadius, 0, 32));

        var family = string.IsNullOrWhiteSpace(t.Typography.FontFamily) ? "Segoe UI" : t.Typography.FontFamily;
        foreach (var tb in new[] { PvTitle, PvBody, PvCardText, PvSuccess, PvWarning, PvError, PvButtonText })
            tb.FontFamily = new FontFamily(family);

        PvTitle.FontSize = Math.Clamp(t.Typography.TitleSize, 8, 96); PvTitle.FontWeight = FontWeights.SemiBold;
        PvTitle.Foreground = B(ThemeTokens.TextPrimary);
        PvBody.FontSize = Math.Clamp(t.Typography.BodySize, 8, 96); PvBody.Foreground = B(ThemeTokens.TextSecondary);
        PvCardText.FontSize = Math.Clamp(t.Typography.BodySize, 8, 96); PvCardText.Foreground = B(ThemeTokens.TextPrimary);
        PvSuccess.FontSize = PvWarning.FontSize = PvError.FontSize = Math.Clamp(t.Typography.CaptionSize, 8, 96);
        PvSuccess.Foreground = B(ThemeTokens.Success); PvWarning.Foreground = B(ThemeTokens.Warning); PvError.Foreground = B(ThemeTokens.Error);

        var radius = new CornerRadius(Math.Clamp(t.Shape.CornerRadius, 0, 32));
        PvCard.Background = B(ThemeTokens.Surface); PvCard.BorderBrush = B(ThemeTokens.Border); PvCard.CornerRadius = radius;
        PvProgress.Foreground = B(ThemeTokens.Accent);
        PvButton.Background = B(ThemeTokens.Accent); PvButton.CornerRadius = radius;
        PvButton.Height = Math.Clamp(t.Shape.ControlHeight, 20, 64);
        PvButtonText.Foreground = B(ThemeTokens.AccentText); PvButtonText.FontSize = Math.Clamp(t.Typography.BodySize, 8, 96);
        ((StackPanel)PvTitle.Parent).Spacing = Math.Clamp(t.Shape.Spacing, 0, 48) + 2;
    }

    // ---------- actions ----------

    private static string UniqueId(IEnumerable<string> existing, string baseId)
    {
        var set = existing.ToHashSet();
        var id = baseId;
        for (var i = 2; set.Contains(id); i++) id = $"{baseId}-{i}";
        return id;
    }

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = System.Text.RegularExpressions.Regex.Replace(new string(chars), "-{2,}", "-").Trim('-');
        return slug.Length == 0 ? "theme" : slug.Length > 50 ? slug[..50].Trim('-') : slug;
    }

    private ThemeDefinition AddCopyToProject(ThemeDefinition source, string? newName = null)
    {
        var project = _projects.Current!;
        var copy = source.Clone();
        if (newName is not null) copy.Name = newName;
        copy.Id = UniqueId(project.Theme.Themes.Select(t => t.Id), Slug(copy.Name));
        if (!string.IsNullOrEmpty(copy.BackgroundImageAssetId) && !project.Assets.Any(a => a.Id == copy.BackgroundImageAssetId))
            copy.BackgroundImageAssetId = "";
        project.Theme.Themes.Add(copy);
        if (string.IsNullOrEmpty(project.Theme.ActiveThemeId)) project.Theme.ActiveThemeId = copy.Id;
        _projects.MarkDirty();
        return copy;
    }

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _projects.Current is null) return;
        var existing = _projects.Current.Theme.Themes.FirstOrDefault(t => t.Id == item.Theme.Id);
        var copy = AddCopyToProject(item.Theme, existing is null ? null : item.Theme.Name + " copy");
        Refresh(copy.Id, ThemeSource.Project);
    }

    private async void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item || _projects.Current is null) return;
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Duplicate theme", "Name for the copy", item.Theme.Name + " copy", "Duplicate");
        if (string.IsNullOrWhiteSpace(name)) return;
        var copy = AddCopyToProject(item.Theme, name.Trim());
        Refresh(copy.Id, ThemeSource.Project);
    }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Source: ThemeSource.Project } item || _projects.Current is null) return;
        _projects.Current.Theme.ActiveThemeId = item.Theme.Id;
        _projects.MarkDirty();
        Refresh(item.Theme.Id, ThemeSource.Project);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Source: ThemeSource.Project } item || _projects.Current is null) return;
        _projects.Current.Theme.Themes.Remove(item.Theme);
        _projects.MarkDirty();
        Refresh(null);
    }

    private async void SaveToLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        try
        {
            var toSave = item.Theme.Clone();
            toSave.BackgroundImageAssetId = ""; // asset ids only make sense inside their project
            _library.Save(toSave);
            Refresh(toSave.Id, ThemeSource.Library);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not save the theme to the library", ex); }
    }

    private async void DeleteFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Source: ThemeSource.Library } item) return;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Delete theme", $"Delete '{item.Theme.Name}' from the library? Projects that already contain it keep their copy.", "Delete")) return;
        try { _library.Delete(item.Theme.Id); Refresh(null); }
        catch (Exception ex) { await _errors.ReportAsync("Could not delete the theme", ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(ThemeSerializer.FileExtension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var theme = _library.Import(file.Path);
            Refresh(theme.Id, ThemeSource.Library);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not import the theme", ex); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = item.Theme.Id };
            picker.FileTypeChoices.Add("PAIC theme", new List<string> { ThemeSerializer.FileExtension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            _library.Export(item.Theme, file.Path);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not export the theme", ex); }
    }
}
