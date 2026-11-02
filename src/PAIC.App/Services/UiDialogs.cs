using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PAIC.App.Services;

/// <summary>Small helpers for common modal dialogs.</summary>
public static class UiDialogs
{
    public static async Task<string?> PromptTextAsync(XamlRoot root, string title, string label, string initial = "", string primary = "OK")
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 380 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = box, PrimaryButtonText = primary,
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>Returns the chosen index, or -1 when cancelled.</summary>
    public static async Task<int> ChooseAsync(XamlRoot root, string title, string label, IReadOnlyList<string> options, string primary = "OK")
    {
        var combo = new ComboBox { Header = label, ItemsSource = options, SelectedIndex = options.Count > 0 ? 0 : -1, MinWidth = 380 };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = combo, PrimaryButtonText = primary,
            CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? combo.SelectedIndex : -1;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary = "OK")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public static async Task MessageAsync(XamlRoot root, string title, string message) =>
        await new ContentDialog
        {
            XamlRoot = root, Title = title, Content = new ScrollViewer
            {
                MaxHeight = 400, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }
            },
            CloseButtonText = "OK"
        }.ShowAsync();
}

/// <summary>Converts theme hex colours to WinUI colours and brushes.</summary>
public static class UiColor
{
    public static Windows.UI.Color Parse(string? hex, Windows.UI.Color fallback) =>
        PAIC.ThemeEngine.ThemeColor.TryParse(hex, out var a, out var r, out var g, out var b)
            ? Windows.UI.Color.FromArgb(a, r, g, b) : fallback;

    public static SolidColorBrush Brush(string? hex, Windows.UI.Color fallback) => new(Parse(hex, fallback));
}
