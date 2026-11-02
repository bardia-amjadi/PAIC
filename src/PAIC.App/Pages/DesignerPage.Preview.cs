using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PAIC.App.Designer;
using PAIC.App.Services;
using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;

namespace PAIC.App.Pages;

public sealed partial class DesignerPage
{
    private static readonly (string Label, double Width, double Height)[] PreviewSizes =
    {
        ("Project window size", 0, 0), ("640 x 420", 640, 420), ("720 x 480", 720, 480), ("800 x 560", 800, 560), ("960 x 640", 960, 640), ("1024 x 720", 1024, 720)
    };

    private static readonly int[] DpiScales = { 100, 125, 150, 175, 200 };

    private PreviewSession? _session;
    private RenderContext? _pvCtx;
    private bool _pvLoading;
    private readonly Dictionary<string, TextBox> _pvVariableBoxes = new(StringComparer.OrdinalIgnoreCase);

    private void InitPreview()
    {
        PvSizeBox.ItemsSource = PreviewSizes.Select(s => s.Label).ToList();
        PvSizeBox.SelectedIndex = 0;
        PvDpiBox.ItemsSource = DpiScales.Select(s => s + "%").ToList();
        PvDpiBox.SelectedIndex = 0;
    }

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = PreviewToggle.IsChecked == true;
        PreviewPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on) { _session = null; _pvCtx = null; PvHost.Children.Clear(); return; }
        if (Project is null) return;
        if (_editingControl is not null) { StopEditingControl(); }
        _pvLoading = true;
        PvThemeBox.ItemsSource = Project.Theme.Themes.Count > 0 ? Project.Theme.Themes.Select(t => t.Name).ToList() : new List<string> { BuiltInThemes.FluentLight().Name + " (default)" };
        PvThemeBox.SelectedIndex = Math.Max(0, Project.Theme.Themes.FindIndex(t => t.Id == Project.Theme.ActiveThemeId));
        PvPageBox.ItemsSource = Project.Pages.Select(p => $"{p.Title}  ({p.Id})").ToList();
        _pvLoading = false;
        _session = new PreviewSession(Project, _page?.Id);
        RenderPreview();
    }

    private void PreviewOption_Changed(object sender, SelectionChangedEventArgs e) { if (!_pvLoading) RenderPreview(); }
    private void PvProgress_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) { if (!_pvLoading && _session is not null) RenderPreview(); }

    private void PvPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_pvLoading || _session is null || Project is null || PvPageBox.SelectedIndex < 0) return;
        _session.GoTo(Project.Pages[PvPageBox.SelectedIndex].Id);
        RenderPreview();
    }

    private void PvRestart_Click(object sender, RoutedEventArgs e)
    {
        if (Project is null) return;
        _session = new PreviewSession(Project, Project.Pages.FirstOrDefault()?.Id);
        RenderPreview();
    }

    private ThemeDefinition PreviewTheme()
    {
        var themes = Project?.Theme.Themes;
        if (themes is { Count: > 0 } && PvThemeBox.SelectedIndex >= 0 && PvThemeBox.SelectedIndex < themes.Count) return themes[PvThemeBox.SelectedIndex];
        return ActiveTheme();
    }

    private void RenderPreview()
    {
        if (_session is null || Project is null) return;
        var page = _session.CurrentPage;
        PvHost.Children.Clear();
        _pvVariableBoxes.Clear();
        PvVariables.Children.Clear();

        var size = PreviewSizes[Math.Max(0, PvSizeBox.SelectedIndex)];
        var width = size.Width > 0 ? size.Width : Project.Installation.WindowWidth;
        var height = size.Height > 0 ? size.Height : Project.Installation.WindowHeight;
        var scale = DpiScales[Math.Max(0, PvDpiBox.SelectedIndex)] / 100.0;

        if (page is null)
        {
            PvHost.Children.Add(new TextBlock { Text = "The project has no pages to preview." });
            PvStatus.Text = "";
            return;
        }

        RenderContext? ctx = null;
        ctx = new RenderContext
        {
            Project = Project,
            Theme = PreviewTheme(),
            Variables = _session.Variables,
            DesignMode = false,
            Session = _session,
            InstallProgress = PvProgress.Value,
            AssetPath = AssetFullPath,
            Control = id => Project.CustomControls.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)),
            Clicked = OnPreviewClicked,
            VariableChanged = (name, value) =>
            {
                _session.Variables[name] = value;
                if (_pvVariableBoxes.TryGetValue(name, out var box) && box.Text != value && !box.FocusState.Equals(FocusState.Keyboard)) box.Text = value;
                ctx!.RunUpdaters();
            }
        };
        _pvCtx = ctx;

        var canvas = ElementRenderer.BuildPage(page.Elements, width, height, page.Background, ctx);
        var frame = new Border
        {
            Child = canvas, Width = width, Height = height, BorderThickness = new Thickness(1),
            BorderBrush = ElementRenderer.Brush(ctx, ThemeTokens.Border), CornerRadius = new CornerRadius(8)
        };
        var holder = new Grid { Width = width * scale, Height = height * scale };
        frame.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        holder.Children.Add(frame);
        PvHost.Children.Add(holder);

        var index = Project.Pages.IndexOf(page);
        _pvLoading = true;
        PvPageBox.SelectedIndex = index;
        _pvLoading = false;

        var state = _session.State switch
        {
            PreviewState.Cancelled => "  \u2022  The installer was cancelled (preview). Press Restart to start again.",
            PreviewState.Finished => "  \u2022  The installer finished (preview). Press Restart to start again.",
            _ => ""
        };
        PvStatus.Text = $"Preview only: nothing is installed and no files are changed.  Page {index + 1} of {Project.Pages.Count}: {page.Title}.{state}";

        foreach (var kv in _session.Variables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock { Text = kv.Key, VerticalAlignment = VerticalAlignment.Center };
            var box = new TextBox { Text = kv.Value };
            var name = kv.Key;
            box.TextChanged += (_, _) => { if (_session is not null && _session.Variables.GetValueOrDefault(name) != box.Text) { _session.Variables[name] = box.Text; _pvCtx?.RunUpdaters(); } };
            Grid.SetColumn(box, 1);
            row.Children.Add(label); row.Children.Add(box);
            PvVariables.Children.Add(row);
            _pvVariableBoxes[name] = box;
        }
    }

    private async void OnPreviewClicked(DesignElement element)
    {
        if (_session is null || element.Action is null) return;
        try
        {
            var result = _session.Apply(element.Action);
            if (result == PreviewActionResult.NeedsFolderBrowse && !string.IsNullOrWhiteSpace(element.Action.Variable))
            {
                var folder = await Pickers.PickFolderAsync();
                if (folder is not null) _session.Variables[element.Action.Variable] = folder;
            }
        }
        catch (Exception ex) { await _errors.ReportAsync("Preview action failed", ex); }
        RenderPreview();
    }
}
