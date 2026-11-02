using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.App.Services;
using PAIC.Diagnostics;

namespace PAIC.App.Pages;

public sealed partial class DiagnosticsPage : Page
{
    private readonly InMemoryLogSink _sink = App.Services.GetRequiredService<InMemoryLogSink>();
    public ObservableCollection<LogEntry> Entries { get; } = new();

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Entries.Clear();
            foreach (var e in _sink.Snapshot()) Entries.Add(e);
            _sink.EntryAdded += OnEntry;
            ScrollToEnd();
        };
        Unloaded += (_, _) => _sink.EntryAdded -= OnEntry;
    }

    private void OnEntry(LogEntry entry) => DispatcherQueue.TryEnqueue(() =>
    {
        Entries.Add(entry);
        ScrollToEnd();
    });

    private void ScrollToEnd()
    {
        if (Entries.Count > 0) LogList.ScrollIntoView(Entries[^1]);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) { _sink.Clear(); Entries.Clear(); }

    private async void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.Logs);
        await Windows.System.Launcher.LaunchFolderPathAsync(AppPaths.Logs);
    }
}
