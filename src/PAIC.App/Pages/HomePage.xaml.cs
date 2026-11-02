using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Dialogs;
using PAIC.App.Services;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Recent;

namespace PAIC.App.Pages;

public sealed partial class HomePage : Page
{
    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IRecentProjectsStore _recent = App.Services.GetRequiredService<IRecentProjectsStore>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();

    public ObservableCollection<RecentProject> Recent { get; } = new();

    public HomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadRecent();
    }

    private void LoadRecent()
    {
        Recent.Clear();
        foreach (var r in _recent.Load()) Recent.Add(r);
        NoRecentText.Visibility = Recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await App.MainWindow!.ConfirmCloseProjectAsync()) return;
        var dialog = new NewProjectDialog { XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            await _projects.CreateAsync(dialog.ParentDirectory, dialog.ProjectName);
            App.MainWindow!.Navigate("project");
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not create the project", ex); }
    }

    private void BrowseTemplates_Click(object sender, RoutedEventArgs e) => App.MainWindow!.Navigate("templates");

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await App.MainWindow!.ConfirmCloseProjectAsync()) return;
        var path = await Pickers.PickProjectFileAsync();
        if (path is not null) await OpenAsync(path);
    }

    private async void RecentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RecentProject r) return;
        if (!await App.MainWindow!.ConfirmCloseProjectAsync()) return;
        await OpenAsync(r.Path);
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is string path) { _recent.Remove(path); LoadRecent(); }
    }

    private async Task OpenAsync(string path)
    {
        try
        {
            await _projects.OpenAsync(path);
            App.MainWindow!.Navigate("project");
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not open the project", ex); }
    }
}
