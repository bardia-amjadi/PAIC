using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;

namespace PAIC.App.Pages;

/// <summary>Display row for one validation issue.</summary>
public sealed record IssueRow(string Heading, string Detail);

public sealed partial class ProjectPage : Page
{
    private PaicProject? _shown;
    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();
    private bool _loading;

    public ProjectPage()
    {
        InitializeComponent();
        ArchitectureBox.ItemsSource = Enum.GetValues<TargetArchitecture>();
        Loaded += (_, _) => { _projects.Changed += OnProjectChanged; Load(); };
        Unloaded += (_, _) => _projects.Changed -= OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (!ReferenceEquals(_shown, _projects.Current)) Load(); });

    private void Load()
    {
        var p = _projects.Current;
        _shown = p;
        NoProjectBar.IsOpen = p is null;
        Editor.Visibility = p is null ? Visibility.Collapsed : Visibility.Visible;
        if (p is null) return;

        _loading = true;
        NameBox.Text = p.Metadata.Name;
        ProductBox.Text = p.Application.ProductName;
        PublisherBox.Text = p.Application.Publisher;
        VersionBox.Text = p.Application.Version;
        WebsiteBox.Text = p.Application.Website;
        DescriptionBox.Text = p.Application.Description;
        ArchitectureBox.SelectedItem = p.Architecture;
        InstallDirBox.Text = p.Installation.DefaultInstallDirectory;
        PerUserBox.IsChecked = p.Installation.PerUser;
        _loading = false;
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => Apply();
    private void Architecture_Changed(object sender, SelectionChangedEventArgs e) => Apply();
    private void PerUser_Changed(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        var p = _projects.Current;
        if (_loading || p is null) return;
        p.Metadata.Name = NameBox.Text;
        p.Application.ProductName = ProductBox.Text;
        p.Application.Publisher = PublisherBox.Text;
        p.Application.Version = VersionBox.Text;
        p.Application.Website = WebsiteBox.Text;
        p.Application.Description = DescriptionBox.Text;
        if (ArchitectureBox.SelectedItem is TargetArchitecture a) p.Architecture = a;
        p.Installation.DefaultInstallDirectory = InstallDirBox.Text;
        p.Installation.PerUser = PerUserBox.IsChecked == true;
        _projects.MarkDirty();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try { await _projects.SaveAsync(); }
        catch (Exception ex) { await _errors.ReportAsync("Could not save the project", ex); }
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await Pickers.PickSaveProjectAsync(_projects.Current?.Metadata.Name ?? "project");
            if (path is not null) await _projects.SaveAsAsync(path);
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not save the project", ex); }
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await _projects.ValidateAsync();
            IssueList.ItemsSource = report.Issues
                .Select(i => new IssueRow($"{i.Severity}  {i.Code}  {i.Message}", i.Location ?? ""))
                .ToList();
            ValidationSummary.Text = report.Issues.Count == 0
                ? "Validation passed: no issues found."
                : $"Validation: {report.ErrorCount} error(s), {report.WarningCount} warning(s)";
        }
        catch (Exception ex) { await _errors.ReportAsync("Validation failed", ex); }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (await App.MainWindow!.ConfirmCloseProjectAsync())
        {
            _projects.Close();
            IssueList.ItemsSource = null;
            ValidationSummary.Text = "";
        }
    }
}
