using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PAIC.App.Dialogs;
using PAIC.App.Services;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.TemplateEngine;
using PAIC.ThemeEngine;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAIC.App.Pages;

/// <summary>A row in the template list.</summary>
public sealed class TemplateItem
{
    public TemplateEntry Template { get; init; } = new();
    public string Name => Template.Info.Name;
    public string Subtitle => $"{Template.Info.Category} \u2022 {(Template.IsBuiltIn ? "Built-in" : "Custom")}";
}

public sealed partial class TemplatesPage : Page
{
    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly ITemplateService _templates = App.Services.GetRequiredService<ITemplateService>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();

    public ObservableCollection<TemplateItem> Items { get; } = new();
    private TemplateItem? Selected => TemplateList.SelectedItem as TemplateItem;

    public TemplatesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh(null);
    }

    private void Refresh(string? selectId)
    {
        var keep = selectId ?? Selected?.Template.Info.Id;
        Items.Clear();
        try { foreach (var t in _templates.List()) Items.Add(new TemplateItem { Template = t }); }
        catch (Exception ex) { _ = _errors.ReportAsync("Could not load templates", ex); }
        TemplateList.SelectedItem = Items.FirstOrDefault(i => i.Template.Info.Id == keep) ?? Items.FirstOrDefault();
        ShowSelected();
        SaveProjectButton.IsEnabled = _projects.Current is not null;
    }

    private void TemplateList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelected();

    private void ShowSelected()
    {
        SwatchPanel.Children.Clear();
        if (Selected is not { } item)
        {
            Details.Visibility = Visibility.Collapsed;
            return;
        }
        Details.Visibility = Visibility.Visible;
        var t = item.Template;
        var p = t.Project;
        NameText.Text = t.Info.Name;
        DescriptionText.Text = t.Info.Description;
        DeleteButton.IsEnabled = !t.IsBuiltIn;

        var theme = p.Theme.Themes.FirstOrDefault(x => x.Id == p.Theme.ActiveThemeId) ?? p.Theme.Themes.FirstOrDefault();
        if (theme is not null)
            foreach (var token in new[] { ThemeTokens.Background, ThemeTokens.Surface, ThemeTokens.Accent, ThemeTokens.TextPrimary, ThemeTokens.Success, ThemeTokens.Warning, ThemeTokens.Error })
                SwatchPanel.Children.Add(new Border
                {
                    Width = 36, Height = 36, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Colors.Gray),
                    Background = UiColor.Brush(theme.Colors.GetValueOrDefault(token), Colors.Gray)
                });

        string List(IEnumerable<string> xs, string none) { var l = xs.ToList(); return l.Count == 0 ? none : string.Join(", ", l); }
        ContentsText.Text = string.Join(Environment.NewLine, new[]
        {
            $"Theme: {theme?.Name ?? "none"}",
            $"Pages ({p.Pages.Count}): {List(p.Pages.Select(x => x.Title), "none (portable package)")}",
            $"Components: {List(p.Components.Select(x => x.Name + (x.Required ? " (required)" : "")), "none")}",
            $"Prerequisites: {List(p.Prerequisites.Select(x => x.Name), "none")}",
            $"Operations: {List(p.Operations.Select(x => x.Id), "none")}",
            $"Output formats: {List(p.Build.Formats.Select(x => x.ToString().ToUpperInvariant()), "none")}",
            $"Install scope: {(p.Installation.PerUser ? "per-user" : "per-machine")}",
            $"Bundled files: {t.Files.Count}"
        });
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        if (!await App.MainWindow!.ConfirmCloseProjectAsync()) return;
        var dialog = new NewProjectDialog { XamlRoot = XamlRoot, Title = $"New project from '{item.Name}'" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var (project, files) = _templates.Prepare(item.Template, dialog.ProjectName);
            await _projects.CreateAsync(dialog.ParentDirectory, dialog.ProjectName, project, files);
            App.MainWindow!.Navigate("project");
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not create the project", ex); }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(TemplateSerializer.FileExtension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var template = _templates.Import(file.Path);
            Refresh(template.Info.Id);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not import the template", ex); }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } item) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = item.Template.Info.Id };
            picker.FileTypeChoices.Add("PAIC template", new List<string> { TemplateSerializer.FileExtension });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            _templates.Export(item.Template, file.Path);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not export the template", ex); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { Template.IsBuiltIn: false } item) return;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Delete template", $"Delete '{item.Name}'?", "Delete")) return;
        try { _templates.Delete(item.Template.Info.Id); Refresh(null); }
        catch (Exception ex) { await _errors.ReportAsync("Could not delete the template", ex); }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        var project = _projects.Current;
        if (project is null || _projects.FilePath is null) return;
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Save as template", "Template name", project.Metadata.Name + " template", "Next");
        if (string.IsNullOrWhiteSpace(name)) return;
        var description = await UiDialogs.PromptTextAsync(XamlRoot, "Save as template", "Description (optional)", "", "Save") ?? null;
        if (description is null) return;
        try
        {
            var template = _templates.SaveFromProject(project, Path.GetDirectoryName(_projects.FilePath)!, name, description, "Custom");
            Refresh(template.Info.Id);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not save the template", ex); }
    }
}
