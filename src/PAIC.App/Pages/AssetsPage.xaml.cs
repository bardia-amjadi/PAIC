using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using PAIC.App.Services;
using PAIC.AssetManager;
using PAIC.ProjectSystem;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace PAIC.App.Pages;

public sealed partial class AssetsPage : Page
{
    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md", ".json", ".xml", ".html", ".htm", ".css", ".js", ".csv", ".log", ".ini", ".cfg", ".yml", ".yaml" };

    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IAssetService _assets = App.Services.GetRequiredService<IAssetService>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();
    private AssetNode? _root;
    private AssetNode? _selected;
    private ProjectChangeToken? _shown;

    private sealed record ProjectChangeToken(object Project);

    public AssetsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => { _projects.Changed += OnProjectChanged; Reload(); };
        Unloaded += (_, _) => _projects.Changed -= OnProjectChanged;
    }

    private void OnProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_projects.Current is null || !ReferenceEquals(_shown?.Project, _projects.Current)) Reload();
        });

    private void Reload()
    {
        var open = _projects.Current is not null;
        NoProjectBar.IsOpen = !open;
        Toolbar.Visibility = Body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _shown = open ? new ProjectChangeToken(_projects.Current!) : null;
        _selected = null;
        ShowDetails(null);
        if (!open) { Tree.ItemsSource = null; return; }
        try
        {
            _root = _assets.GetTree();
            Tree.ItemsSource = _root.Children;
        }
        catch (Exception ex) { _ = _errors.ReportAsync("Could not read the assets folder", ex); }
    }

    private string SelectedFolder() =>
        _selected is null ? "assets" : _selected.IsFolder ? _selected.RelativePath : _selected.RelativePath[.._selected.RelativePath.LastIndexOf('/')];

    private async Task Run(string failureTitle, Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { await _errors.ReportAsync(failureTitle, ex); }
        Reload();
    }

    private List<string> AllFolders()
    {
        var folders = new List<string>();
        void Walk(AssetNode n) { if (n.IsFolder) { folders.Add(n.RelativePath); foreach (var c in n.Children) Walk(c); } }
        if (_root is not null) Walk(_root);
        return folders;
    }

    // ---------- toolbar ----------

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) await ImportPathsAsync(files.Select(f => f.Path).ToList());
    }

    private async Task ImportPathsAsync(List<string> paths) => await Run("Import failed", async () =>
    {
        var folder = _selected is null ? null : SelectedFolder();
        var result = await _assets.ImportAsync(paths, folder == "assets" ? null : folder);
        var lines = new List<string> { $"Imported {result.Imported.Count} file(s)." };
        lines.AddRange(result.DuplicateWarnings.Select(w => "Duplicate: " + w));
        lines.AddRange(result.Failed.Select(f => "Failed: " + f));
        if (result.DuplicateWarnings.Count > 0 || result.Failed.Count > 0)
            await UiDialogs.MessageAsync(XamlRoot, "Import finished", string.Join(Environment.NewLine, lines));
    });

    private async void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "New folder", "Folder name", "", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        await Run("Could not create the folder", () => { _assets.CreateFolder(SelectedFolder() + "/" + name.Trim()); return Task.CompletedTask; });
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(allowRoot: false)) return;
        var name = await UiDialogs.PromptTextAsync(XamlRoot, "Rename", "New name", _selected!.Name, "Rename");
        if (string.IsNullOrWhiteSpace(name) || name == _selected.Name) return;
        await Run("Could not rename", () => { _assets.Rename(_selected.RelativePath, name); return Task.CompletedTask; });
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(allowRoot: false)) return;
        var folders = AllFolders();
        var index = await UiDialogs.ChooseAsync(XamlRoot, "Copy to", "Destination folder", folders, "Copy");
        if (index < 0) return;
        await Run("Could not copy", () => _assets.CopyAsync(_selected!.RelativePath, folders[index]));
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(allowRoot: false)) return;
        var folders = AllFolders().Where(f => f != _selected!.RelativePath && !f.StartsWith(_selected.RelativePath + "/", StringComparison.OrdinalIgnoreCase)).ToList();
        var index = await UiDialogs.ChooseAsync(XamlRoot, "Move to", "Destination folder", folders, "Move");
        if (index < 0) return;
        await Run("Could not move", () => { _assets.Move(_selected!.RelativePath, folders[index]); return Task.CompletedTask; });
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireSelection(allowRoot: false)) return;
        var target = _selected!;
        if (!await UiDialogs.ConfirmAsync(XamlRoot, "Delete", $"Delete '{target.Name}'{(target.IsFolder ? " and everything in it" : "")}? This cannot be undone.", "Delete")) return;
        try
        {
            _assets.Delete(target.RelativePath);
        }
        catch (AssetInUseException inUse)
        {
            if (await UiDialogs.ConfirmAsync(XamlRoot, "Asset in use",
                    string.Join(Environment.NewLine, inUse.Uses) + Environment.NewLine + Environment.NewLine + "Delete anyway and clear these references?", "Delete anyway"))
                await Run("Could not delete", () => { _assets.Delete(target.RelativePath, removeReferences: true); return Task.CompletedTask; });
            return;
        }
        catch (Exception ex) { await _errors.ReportAsync("Could not delete", ex); }
        Reload();
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await Run("Sync failed", async () =>
    {
        var first = await _assets.SyncAsync(removeMissing: false);
        var message = $"Registered {first.Registered} untracked file(s).";
        if (first.MissingFiles.Count > 0)
        {
            message += $"{Environment.NewLine}{first.MissingFiles.Count} listed asset(s) are missing on disk:{Environment.NewLine}" + string.Join(Environment.NewLine, first.MissingFiles);
            if (await UiDialogs.ConfirmAsync(XamlRoot, "Sync with disk", message + Environment.NewLine + Environment.NewLine + "Remove the missing entries from the project?", "Remove entries"))
                await _assets.SyncAsync(removeMissing: true);
        }
        else await UiDialogs.MessageAsync(XamlRoot, "Sync with disk", message);
    });

    private async void Duplicates_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var groups = _assets.FindDuplicateGroups();
            var text = groups.Count == 0
                ? "No duplicate files found."
                : string.Join(Environment.NewLine + Environment.NewLine, groups.Select(g => "Identical content:" + Environment.NewLine + string.Join(Environment.NewLine, g.Select(a => "  " + a.Path))));
            await UiDialogs.MessageAsync(XamlRoot, "Duplicate detection", text);
        }
        catch (Exception ex) { await _errors.ReportAsync("Duplicate detection failed", ex); }
    }

    private bool RequireSelection(bool allowRoot)
    {
        if (_selected is null || (!allowRoot && _selected.RelativePath == "assets"))
        {
            _ = UiDialogs.MessageAsync(XamlRoot, "Select an item", "Select a file or folder in the tree first.");
            return false;
        }
        return true;
    }

    // ---------- drag & drop ----------

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (_projects.Current is not null && e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (_projects.Current is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
        if (paths.Count > 0) await ImportPathsAsync(paths);
    }

    // ---------- selection & preview ----------

    private void Tree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        _selected = sender.SelectedItem as AssetNode;
        ShowDetails(_selected);
    }

    private void ShowDetails(AssetNode? node)
    {
        PreviewImage.Visibility = PreviewText.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        if (node is null) { DetailTitle.Text = "Select an item"; DetailText.Text = ""; return; }

        DetailTitle.Text = node.Name;
        if (node.IsFolder)
        {
            DetailText.Text = $"Folder: {node.RelativePath}{Environment.NewLine}{node.Children.Count} item(s)";
            return;
        }

        var lines = new List<string> { $"Path: {node.RelativePath}", $"Size: {node.Size:N0} bytes" };
        if (node.Asset is { } a)
        {
            lines.Add($"Id: {a.Id}");
            lines.Add($"Category: {a.Category}");
            lines.Add($"SHA-256: {a.Sha256}");
            var uses = _assets.FindUses(node.RelativePath);
            if (uses.Count > 0) lines.Add("Used by: " + string.Join("; ", uses));
        }
        else lines.Add("Not listed in the project. Use 'Sync with disk' to register it.");
        DetailText.Text = string.Join(Environment.NewLine, lines);

        try
        {
            var full = _assets.ToFullPath(node.RelativePath);
            var ext = Path.GetExtension(full);
            if (ImageExt.Contains(ext))
            {
                PreviewImage.Source = new BitmapImage(new Uri(full));
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                PreviewImage.Source = new SvgImageSource(new Uri(full));
                PreviewImage.Visibility = Visibility.Visible;
            }
            else if (TextExt.Contains(ext) && node.Size <= 2_000_000)
            {
                using var reader = new StreamReader(full);
                var buffer = new char[20_000];
                var read = reader.Read(buffer, 0, buffer.Length);
                PreviewText.Text = new string(buffer, 0, read) + (reader.EndOfStream ? "" : Environment.NewLine + "...");
                PreviewText.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException)
        {
            DetailText.Text += Environment.NewLine + "Preview unavailable: " + ex.Message;
        }
    }
}
