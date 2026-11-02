using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.ProjectSystem.Model;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAIC.App.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PAIC");
    public static string Logs => Path.Combine(Root, "logs");
    public static string RecentProjects => Path.Combine(Root, "recent-projects.json");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Themes => Path.Combine(Root, "themes");
    public static string Templates => Path.Combine(Root, "templates");
    public static string Controls => Path.Combine(Root, "controls");
}

public enum ThemePreference { System, Light, Dark }

public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    public AppSettings Settings { get; private set; } = new();

    public AppSettingsStore(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path))
                Settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { Settings = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public interface IErrorReporter
{
    Task ReportAsync(string title, Exception exception);
}

/// <summary>Central place that logs an exception and shows a user-facing dialog.</summary>
public sealed class ErrorReporter : IErrorReporter
{
    private readonly ILogger<ErrorReporter> _logger;
    public ErrorReporter(ILogger<ErrorReporter> logger) => _logger = logger;

    public async Task ReportAsync(string title, Exception exception)
    {
        _logger.LogError(exception, "{Title}", title);
        var root = (App.MainWindow?.Content as FrameworkElement)?.XamlRoot;
        if (root is null) return;
        var message = exception is PAIC.Core.UserFacingException
            ? exception.Message
            : $"An unexpected error occurred: {exception.Message}\n\nDetails were written to the log (Diagnostics page).";
        await new ContentDialog { XamlRoot = root, Title = title, Content = message, CloseButtonText = "OK" }.ShowAsync();
    }
}

public static class Pickers
{
    private static nint Hwnd => WindowNative.GetWindowHandle(App.MainWindow);

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSingleFolderAsync())?.Path;
    }

    public static async Task<string?> PickProjectFileAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(ProjectLayout.FileExtension);
        InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> PickSaveProjectAsync(string suggestedName)
    {
        var picker = new FileSavePicker { SuggestedFileName = suggestedName };
        picker.FileTypeChoices.Add("PAIC project", new List<string> { ProjectLayout.FileExtension });
        InitializeWithWindow.Initialize(picker, Hwnd);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
