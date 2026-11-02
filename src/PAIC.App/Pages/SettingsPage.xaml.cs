using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;

namespace PAIC.App.Pages;

public sealed partial class SettingsPage : Page
{
    private readonly AppSettingsStore _store = App.Services.GetRequiredService<AppSettingsStore>();
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        ThemeRadio.SelectedIndex = (int)_store.Settings.Theme;
        VersionText.Text = $"Version {typeof(SettingsPage).Assembly.GetName().Version}";
        LogPathText.Text = $"Logs: {AppPaths.Logs}";
        _loading = false;
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ThemeRadio.SelectedIndex < 0) return;
        _store.Settings.Theme = (ThemePreference)ThemeRadio.SelectedIndex;
        _store.Save();
        App.MainWindow?.ApplyTheme(_store.Settings.Theme);
    }
}
