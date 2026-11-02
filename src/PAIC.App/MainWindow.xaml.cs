using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Pages;
using PAIC.App.Services;
using PAIC.ProjectSystem;

namespace PAIC.App;

public sealed partial class MainWindow : Window
{
    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Closing += AppWindow_Closing;
        _projects.Changed += (_, _) => DispatcherQueue.TryEnqueue(UpdateStatus);
        ApplyTheme(App.Services.GetRequiredService<AppSettingsStore>().Settings.Theme);
        Nav.SelectedItem = Nav.MenuItems[0];
        UpdateStatus();
    }

    public void ApplyTheme(ThemePreference theme) =>
        Root.RequestedTheme = theme switch
        {
            ThemePreference.Light => ElementTheme.Light,
            ThemePreference.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

    public void Navigate(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null) Nav.SelectedItem = item;
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { ContentFrame.Navigate(typeof(SettingsPage)); return; }
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch
        {
            "project" => typeof(ProjectPage),
            "designer" => typeof(DesignerPage),
            "installation" => typeof(InstallationPage),
            "assets" => typeof(AssetsPage),
            "themes" => typeof(ThemesPage),
            "templates" => typeof(TemplatesPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => typeof(HomePage)
        };
        ContentFrame.Navigate(page);
    }

    private void UpdateStatus()
    {
        if (_projects.Current is null)
        {
            StatusText.Text = "No project open";
            Title = "Pixel Advanced Installer Creator";
            return;
        }
        var dirty = _projects.IsDirty ? " \u2022 unsaved changes" : "";
        StatusText.Text = $"{_projects.FilePath}{dirty}";
        Title = $"{_projects.Current.Metadata.Name}{(_projects.IsDirty ? "*" : "")} - Pixel Advanced Installer Creator";
    }

    /// <summary>Returns true when it is safe to replace or close the current project (saving if the user asks).</summary>
    public async Task<bool> ConfirmCloseProjectAsync()
    {
        if (!_projects.IsDirty) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = "Unsaved changes",
            Content = "Save changes to the current project first?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return false;
        if (result == ContentDialogResult.Primary)
        {
            try { await _projects.SaveAsync(); }
            catch (Exception ex) { await _errors.ReportAsync("Could not save the project", ex); return false; }
        }
        return true;
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose || !_projects.IsDirty) return;
        args.Cancel = true;
        if (await ConfirmCloseProjectAsync())
        {
            _forceClose = true;
            Close();
        }
    }
}
