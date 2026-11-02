using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using PAIC.App.Services;
using PAIC.AssetManager;
using PAIC.Controls;
using PAIC.TemplateEngine;
using PAIC.ThemeEngine;
using PAIC.Diagnostics;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Recent;

namespace PAIC.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services?.GetService<ILogger<App>>()?.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Services = ConfigureServices();
        Services.GetRequiredService<ILogger<App>>().LogInformation("PAIC starting");
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    private static IServiceProvider ConfigureServices()
    {
        var sink = new InMemoryLogSink();
        var services = new ServiceCollection();
        services.AddSingleton(sink);
        services.AddLogging(b => b.AddPaicLogging(AppPaths.Logs, sink));
        services.AddSingleton<IRecentProjectsStore>(sp =>
            new RecentProjectsStore(AppPaths.RecentProjects, sp.GetRequiredService<ILogger<RecentProjectsStore>>()));
        services.AddSingleton<IProjectService, ProjectService>();
        services.AddSingleton<IAssetService, AssetService>();
        services.AddSingleton<IThemeLibrary>(new ThemeLibrary(AppPaths.Themes));
        services.AddSingleton<IControlLibrary>(new ControlLibrary(AppPaths.Controls));
        services.AddSingleton<ITemplateService>(new TemplateService(AppPaths.Templates));
        services.AddSingleton(new AppSettingsStore(AppPaths.Settings));
        services.AddSingleton<IErrorReporter, ErrorReporter>();
        return services.BuildServiceProvider();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Services?.GetService<ILogger<App>>()?.LogCritical(e.Exception, "Unhandled UI exception");
        if (Services?.GetService<IErrorReporter>() is { } reporter)
            MainWindow?.DispatcherQueue.TryEnqueue(async () => await reporter.ReportAsync("Unexpected error", e.Exception));
    }
}
