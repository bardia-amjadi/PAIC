using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PAIC.App.Designer;
using PAIC.App.Services;
using PAIC.AssetManager;
using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;
using Rect = Windows.Foundation.Rect;
using Windows.Foundation;
using Windows.System;

namespace PAIC.App.Pages;

public sealed class PageItem
{
    public InstallerPageDefinition Page { get; init; } = new();
    public int Index { get; init; }
    public string Title => $"{Index + 1}. {Page.Title}";
    public string Subtitle => $"{Page.Type} \u2022 {Page.Elements.Count} element(s)";
}

public sealed class ToolItem
{
    public string Type { get; init; } = "";
    public string Title { get; init; } = "";
    public string Category { get; init; } = "";
}

public sealed class ControlItem
{
    public CustomControlDefinition Definition { get; init; } = new();
    public string Title => $"{Definition.Name}  (v{Definition.Version})";
}

public sealed class LayerNode
{
    public DesignElement Element { get; init; } = new();
    public string Title =>
        $"{(string.IsNullOrWhiteSpace(Element.Name) ? Element.Id : Element.Name)}  \u2022 {Element.Type}{(Element.Visible ? "" : "  (hidden)")}{(Element.Locked ? "  (locked)" : "")}";
    public List<LayerNode> Children { get; } = new();
}

public sealed partial class DesignerPage : Page
{
    private enum Drag { None, Move, Resize, Marquee }

    private readonly IProjectService _projects = App.Services.GetRequiredService<IProjectService>();
    private readonly IAssetService _assets = App.Services.GetRequiredService<IAssetService>();
    private readonly IControlLibrary _controlLibrary = App.Services.GetRequiredService<IControlLibrary>();
    private readonly IErrorReporter _errors = App.Services.GetRequiredService<IErrorReporter>();

    private readonly Dictionary<string, DesignerDocument> _docs = new();
    private DesignerDocument? _doc;
    private InstallerPageDefinition? _page;
    private CustomControlDefinition? _editingControl;
    private DesignerDocument? _controlDoc;
    private PaicProject? _shownProject;

    private double _zoom = 1;
    private bool _snap = true;
    private int _suppressInspector;
    private bool _syncingLayers;
    private bool _loadingPages;

    private Drag _drag = Drag.None;
    private Point _dragStart;
    private ResizeHandle _handle;
    private PAIC.Controls.Rect _moveBounds;
    private Rect? _marquee;
    private List<double> _vGuides = new(), _hGuides = new();

    public ObservableCollection<PageItem> Pages { get; } = new();
    public ObservableCollection<ToolItem> Tools { get; } = new();
    public ObservableCollection<ControlItem> CustomControls { get; } = new();

    private PaicProject? Project => _projects.Current;
    private DesignerDocument? Doc => _editingControl is not null ? _controlDoc : _doc;

    public DesignerPage()
    {
        InitializeComponent();
        ZoomBox.ItemsSource = new[] { "50%", "75%", "100%", "125%", "150%", "200%" };
        ZoomBox.SelectedIndex = 2;
        InitPreview();
        Loaded += (_, _) => { _projects.Changed += OnProjectChanged; LoadProject(); };
        Unloaded += (_, _) => _projects.Changed -= OnProjectChanged;
    }

    // ---------------------------------------------------------------- project / pages

    private void OnProjectChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(() => { if (!ReferenceEquals(_shownProject, Project)) { _docs.Clear(); _editingControl = null; LoadProject(); } });

    private void LoadProject()
    {
        _shownProject = Project;
        var open = Project is not null;
        NoProjectBar.IsOpen = !open;
        Toolbar.Visibility = Workspace.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (!open) { _doc = null; _page = null; return; }
        RefreshPages(_page?.Id);
        RefreshToolbox();
        RefreshControlList();
    }

    private void RefreshPages(string? selectId)
    {
        var project = Project!;
        _loadingPages = true;
        Pages.Clear();
        for (var i = 0; i < project.Pages.Count; i++) Pages.Add(new PageItem { Page = project.Pages[i], Index = i });
        PageList.SelectedItem = Pages.FirstOrDefault(p => p.Page.Id == selectId) ?? Pages.FirstOrDefault();
        _loadingPages = false;
        SelectPage((PageList.SelectedItem as PageItem)?.Page);
    }

    private void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPages) return;
        if (_editingControl is not null) { PageList.SelectedItem = Pages.FirstOrDefault(p => p.Page == _page); return; }
        SelectPage((PageList.SelectedItem as PageItem)?.Page);
    }

    private void SelectPage(InstallerPageDefinition? page)
    {
        _page = page;
        if (page is null || Project is null) { _doc = null; ClearSurface(); BuildInspector(); RefreshLayers(); return; }
        if (!_docs.TryGetValue(page.Id, out var doc) || !ReferenceEquals(doc.Roots, page.Elements))
        {
            doc = new DesignerDocument(page.Elements, Project.Installation.WindowWidth, Project.Installation.WindowHeight, id => Project.CustomControls.FirstOrDefault(c => c.Id == id));
            doc.Changed += OnDocChanged;
            doc.SelectionChanged += OnSelectionChanged;
            _docs[page.Id] = doc;
        }
        doc.Width = Project.Installation.WindowWidth; doc.Height = Project.Installation.WindowHeight;
        _doc = doc;
        FullRefresh();
    }

    private void FullRefresh()
    {
        RenderSurface();
        BuildInspector();
        RefreshLayers();
        UpdateStateBar();
        UpdateUndoRedo();
    }

    private void UpdateStateBar()
    {
        if (_editingControl is not null)
        {
            StateBar.IsOpen = true; StateBar.Severity = InfoBarSeverity.Warning;
            StateBar.Title = $"Editing control '{_editingControl.Name}'";
            StateBar.Message = "Use {param:Name} in property values to bind them to the control's parameters.";
            StateButton.Content = "Done editing";
        }
        else if (_page is { Elements.Count: 0 } && _page.Type != InstallerPageType.Custom)
        {
            StateBar.IsOpen = true; StateBar.Severity = InfoBarSeverity.Informational;
            StateBar.Title = "This page has no layout yet";
            StateBar.Message = "Generate the default layout for this page type, or drag controls from the toolbox.";
            StateButton.Content = "Generate default layout";
        }
        else StateBar.IsOpen = false;
    }

    private void StateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_editingControl is not null) { StopEditingControl(); return; }
        if (_page is null || Project is null) return;
        Guard(() =>
        {
            PageService.ResetLayout(Project, _page.Id);
            _docs.Remove(_page.Id);
            _projects.MarkDirty();
            RefreshPages(_page.Id);
        });
    }

    // ---------------------------------------------------------------- document events

    private void OnDocChanged()
    {
        _projects.MarkDirty();
        RenderSurface();
        RefreshLayers();
        UpdateUndoRedo();
        if (_suppressInspector > 0) return;
        if (Doc?.InteractionActive == true) RefreshBounds(); else BuildInspector();
        if (!_loadingPages && _editingControl is null) UpdatePageSubtitle();
    }

    private void OnSelectionChanged()
    {
        DrawOverlay();
        SyncLayerSelection();
        if (_suppressInspector == 0) BuildInspector();
    }

    private void UpdatePageSubtitle()
    {
        if (_page is null) return;
        var item = Pages.FirstOrDefault(p => p.Page == _page);
        if (item is null) return;
        var i = Pages.IndexOf(item);
        _loadingPages = true;
        Pages[i] = new PageItem { Page = item.Page, Index = item.Index };
        PageList.SelectedItem = Pages[i];
        _loadingPages = false;
    }

    private void UpdateUndoRedo()
    {
        UndoButton.IsEnabled = Doc?.CanUndo == true;
        RedoButton.IsEnabled = Doc?.CanRedo == true;
        UndoButton.Label = Doc?.UndoLabel is { } u ? $"Undo {u}" : "Undo";
        RedoButton.Label = Doc?.RedoLabel is { } r ? $"Redo {r}" : "Redo";
    }

    private async void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { await _errors.ReportAsync("The designer could not complete the action", ex); }
    }

    // ---------------------------------------------------------------- rendering

    private ThemeDefinition ActiveTheme()
    {
        var t = Project?.Theme;
        return t?.Themes.FirstOrDefault(x => x.Id == t.ActiveThemeId) ?? t?.Themes.FirstOrDefault() ?? BuiltInThemes.FluentLight();
    }

    private string? AssetFullPath(string id)
    {
        var asset = Project?.Assets.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;
        try { var path = _assets.ToFullPath(asset.Path); return File.Exists(path) ? path : null; }
        catch (Exception ex) when (ex is ProjectException or InvalidOperationException) { return null; }
    }

    private RenderContext MakeContext(bool design, Dictionary<string, string>? variables = null, PreviewSession? session = null, ThemeDefinition? theme = null, double progress = 40) => new()
    {
        Project = Project!,
        Theme = theme ?? ActiveTheme(),
        Variables = variables ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        DesignMode = design,
        Session = session,
        InstallProgress = progress,
        AssetPath = AssetFullPath,
        Control = id => Project?.CustomControls.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
    };

    private void ClearSurface() { RenderHost.Children.Clear(); Overlay.Children.Clear(); }

    private void RenderSurface()
    {
        var doc = Doc;
        if (doc is null || Project is null) { ClearSurface(); return; }
        // Design mode shows variable tokens as written; defaults for inputs come from the property values.
        var ctx = MakeContext(design: true);
        var background = _editingControl is not null ? new PageBackground() : _page?.Background ?? new PageBackground();
        var canvas = ElementRenderer.BuildPage(doc.Roots, doc.Width, doc.Height, background, ctx);
        RenderHost.Children.Clear();
        RenderHost.Children.Add(canvas);
        Surface.Width = doc.Width; Surface.Height = doc.Height;
        Overlay.Width = doc.Width; Overlay.Height = doc.Height;
        ApplyZoom();
        DrawOverlay();
    }

    private void ApplyZoom()
    {
        Surface.RenderTransform = new ScaleTransform { ScaleX = _zoom, ScaleY = _zoom };
        ZoomHost.Width = (Doc?.Width ?? 0) * _zoom;
        ZoomHost.Height = (Doc?.Height ?? 0) * _zoom;
    }

    private void Zoom_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ZoomBox.SelectedItem is string s && double.TryParse(s.TrimEnd('%'), out var pct))
        {
            _zoom = pct / 100;
            ApplyZoom();
            DrawOverlay();
        }
    }

    private void Snap_Click(object sender, RoutedEventArgs e) => _snap = SnapToggle.IsChecked == true;

    private static Windows.UI.Color Accent => Windows.UI.Color.FromArgb(255, 0, 120, 212);

    private void DrawOverlay()
    {
        Overlay.Children.Clear();
        var doc = Doc;
        if (doc is null) return;
        var stroke = new SolidColorBrush(Accent);
        var th = 1.5 / _zoom;
        var selected = doc.SelectionRoots();

        foreach (var e in selected)
        {
            if (LayoutEngine.GetAbsoluteRect(doc.Roots, e.Id) is not { } r) continue;
            var rect = new Rectangle { Width = r.Width, Height = r.Height, Stroke = stroke, StrokeThickness = th };
            if (e.Locked) rect.StrokeDashArray = new DoubleCollection { 3, 2 };
            Canvas.SetLeft(rect, r.X); Canvas.SetTop(rect, r.Y);
            Overlay.Children.Add(rect);
        }

        if (selected.Count == 1 && !selected[0].Locked && LayoutEngine.GetAbsoluteRect(doc.Roots, selected[0].Id) is { } sel)
        {
            var size = 8 / _zoom;
            foreach (var (_, pt) in HandlePoints(sel))
            {
                var h = new Rectangle { Width = size, Height = size, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Stroke = stroke, StrokeThickness = th };
                Canvas.SetLeft(h, pt.X - size / 2); Canvas.SetTop(h, pt.Y - size / 2);
                Overlay.Children.Add(h);
            }
        }

        var guide = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 40, 120));
        foreach (var x in _vGuides) Overlay.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = doc.Height, Stroke = guide, StrokeThickness = 1 / _zoom });
        foreach (var y in _hGuides) Overlay.Children.Add(new Line { Y1 = y, Y2 = y, X1 = 0, X2 = doc.Width, Stroke = guide, StrokeThickness = 1 / _zoom });

        if (_marquee is { } m)
        {
            var mr = new Rectangle { Width = m.Width, Height = m.Height, Stroke = stroke, StrokeThickness = 1 / _zoom, Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 212)) };
            Canvas.SetLeft(mr, m.X); Canvas.SetTop(mr, m.Y);
            Overlay.Children.Add(mr);
        }
    }

    private static IEnumerable<(ResizeHandle, Point)> HandlePoints(PAIC.Controls.Rect r)
    {
        yield return (ResizeHandle.TopLeft, new Point(r.X, r.Y));
        yield return (ResizeHandle.Top, new Point(r.X + r.Width / 2, r.Y));
        yield return (ResizeHandle.TopRight, new Point(r.Right, r.Y));
        yield return (ResizeHandle.Right, new Point(r.Right, r.Y + r.Height / 2));
        yield return (ResizeHandle.BottomRight, new Point(r.Right, r.Bottom));
        yield return (ResizeHandle.Bottom, new Point(r.X + r.Width / 2, r.Bottom));
        yield return (ResizeHandle.BottomLeft, new Point(r.X, r.Bottom));
        yield return (ResizeHandle.Left, new Point(r.X, r.Y + r.Height / 2));
    }

    // ---------------------------------------------------------------- pointer interaction

    private ResizeHandle? HandleAt(Point p)
    {
        var doc = Doc;
        if (doc is null) return null;
        var roots = doc.SelectionRoots();
        if (roots.Count != 1 || roots[0].Locked || LayoutEngine.GetAbsoluteRect(doc.Roots, roots[0].Id) is not { } r) return null;
        var tol = 7 / _zoom;
        foreach (var (h, pt) in HandlePoints(r))
            if (Math.Abs(pt.X - p.X) <= tol && Math.Abs(pt.Y - p.Y) <= tol) return h;
        return null;
    }

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var doc = Doc;
        if (doc is null) return;
        var pt = e.GetCurrentPoint(Surface);
        if (!pt.Properties.IsLeftButtonPressed) return;
        Surface.Focus(FocusState.Programmatic);
        var p = pt.Position;
        _dragStart = p;
        var additive = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift) || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        var enterGroups = e.KeyModifiers.HasFlag(VirtualKeyModifiers.Menu);

        if (HandleAt(p) is { } handle)
        {
            _handle = handle; _drag = Drag.Resize;
            doc.BeginInteraction();
            Surface.CapturePointer(e.Pointer);
            e.Handled = true;
            return;
        }

        var hit = LayoutEngine.HitTest(doc.Roots, p.X, p.Y, enterGroups);
        if (hit is not null)
        {
            var wasSelected = doc.SelectedIds.Contains(hit.Id);
            if (additive) doc.Select(hit.Id, additive: true);
            else if (!wasSelected) doc.Select(hit.Id);

            if (doc.SelectedIds.Count > 0)
            {
                var bounds = doc.SelectionRoots().Select(s => LayoutEngine.GetAbsoluteRect(doc.Roots, s.Id)).Where(r => r is not null).Select(r => r!.Value).ToList();
                if (bounds.Count > 0)
                {
                    _moveBounds = bounds.Aggregate((a, b) => a.Union(b));
                    _drag = Drag.Move;
                    doc.BeginInteraction();
                    Surface.CapturePointer(e.Pointer);
                }
            }
        }
        else
        {
            if (!additive) doc.ClearSelection();
            _drag = Drag.Marquee;
            _marquee = new Rect(p.X, p.Y, 0, 0);
            Surface.CapturePointer(e.Pointer);
        }
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var doc = Doc;
        if (doc is null) return;
        var p = e.GetCurrentPoint(Surface).Position;

        if (_drag == Drag.None) return;

        var dx = p.X - _dragStart.X; var dy = p.Y - _dragStart.Y;
        switch (_drag)
        {
            case Drag.Move:
            {
                if (Math.Abs(dx) < 2 && Math.Abs(dy) < 2 && _vGuides.Count == 0) return;
                var proposed = new PAIC.Controls.Rect(_moveBounds.X + dx, _moveBounds.Y + dy, _moveBounds.Width, _moveBounds.Height);
                var selectedIds = doc.SelectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var others = doc.Roots.Where(r => r.Visible && !selectedIds.Contains(r.Id)).Select(r => LayoutEngine.RootRect(r)).ToList();
                var snap = _snap ? SnapEngine.Snap(proposed, others, 8, 6 / _zoom, doc.Width, doc.Height) : new SnapResult(0, 0, Array.Empty<double>(), Array.Empty<double>());
                _vGuides = snap.VerticalGuides.ToList(); _hGuides = snap.HorizontalGuides.ToList();
                doc.UpdateMove(Math.Round(dx + snap.Dx), Math.Round(dy + snap.Dy));
                break;
            }
            case Drag.Resize:
                doc.UpdateResize(_handle, Math.Round(dx), Math.Round(dy), e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
                break;
            case Drag.Marquee:
                _marquee = new Rect(Math.Min(_dragStart.X, p.X), Math.Min(_dragStart.Y, p.Y), Math.Abs(dx), Math.Abs(dy));
                DrawOverlay();
                break;
        }
    }

    private void Surface_PointerReleased(object sender, PointerRoutedEventArgs e) => EndDrag(e, commit: true);
    private void Surface_PointerCanceled(object sender, PointerRoutedEventArgs e) => EndDrag(e, commit: false);

    private void EndDrag(PointerRoutedEventArgs e, bool commit)
    {
        var doc = Doc;
        var p = e.GetCurrentPoint(Surface).Position;
        Surface.ReleasePointerCapture(e.Pointer);
        var kind = _drag;
        _drag = Drag.None;
        _vGuides.Clear(); _hGuides.Clear();
        if (doc is null) return;

        try
        {
            switch (kind)
            {
                case Drag.Move:
                    if (commit) doc.CommitInteraction("Move", (p.X, p.Y)); else doc.CancelInteraction();
                    break;
                case Drag.Resize:
                    if (commit) doc.CommitInteraction("Resize"); else doc.CancelInteraction();
                    break;
                case Drag.Marquee when _marquee is { } m && commit:
                    var ids = new List<string>();
                    foreach (var el in doc.Roots.Where(r => r.Visible))
                    {
                        var r = LayoutEngine.RootRect(el);
                        if (r.X < m.X + m.Width && r.Right > m.X && r.Y < m.Y + m.Height && r.Bottom > m.Y) ids.Add(el.Id);
                    }
                    doc.SelectMany(ids);
                    break;
            }
        }
        finally { _marquee = null; DrawOverlay(); BuildInspector(); UpdateUndoRedo(); }
    }

    private void Surface_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var doc = Doc;
        if (doc is null) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var step = shift ? 10 : 1;
        var handled = true;
        Guard(() =>
        {
            switch (e.Key)
            {
                case VirtualKey.Delete: doc.DeleteSelection(); break;
                case VirtualKey.Left: doc.Nudge(-step, 0); break;
                case VirtualKey.Right: doc.Nudge(step, 0); break;
                case VirtualKey.Up: doc.Nudge(0, -step); break;
                case VirtualKey.Down: doc.Nudge(0, step); break;
                case VirtualKey.Escape: if (doc.InteractionActive) doc.CancelInteraction(); else doc.ClearSelection(); break;
                case VirtualKey.Z when ctrl: doc.Undo(); break;
                case VirtualKey.Y when ctrl: doc.Redo(); break;
                case VirtualKey.C when ctrl: doc.Copy(); break;
                case VirtualKey.X when ctrl: doc.Cut(); break;
                case VirtualKey.V when ctrl: doc.Paste(); break;
                case VirtualKey.D when ctrl: doc.Duplicate(); break;
                case VirtualKey.A when ctrl: doc.SelectAll(); break;
                case VirtualKey.G when ctrl && shift: doc.Ungroup(); break;
                case VirtualKey.G when ctrl: doc.Group(); break;
                default: handled = false; break;
            }
        });
        e.Handled = handled;
    }

    // ---------------------------------------------------------------- toolbox & drop

    private void RefreshToolbox()
    {
        Tools.Clear();
        foreach (var d in ControlRegistry.All.Where(d => d.Type != ControlTypes.Group))
            Tools.Add(new ToolItem { Type = d.Type, Title = d.DisplayName, Category = d.Category });
        if (_editingControl is null && Project is not null)
            foreach (var c in Project.CustomControls)
                Tools.Add(new ToolItem { Type = ControlTypes.CustomPrefix + c.Id, Title = c.Name, Category = "Custom control" });
    }

    private void ToolList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is ToolItem t)
        {
            e.Data.SetText(t.Type);
            e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
        }
    }

    private void ToolList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ToolList.SelectedItem is ToolItem t && Doc is { } doc) Guard(() => doc.AddDefault(t.Type));
    }

    private void Surface_DragOver(object sender, DragEventArgs e)
    {
        if (Doc is not null && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
    }

    private async void Surface_Drop(object sender, DragEventArgs e)
    {
        var doc = Doc;
        if (doc is null || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
        var type = await e.DataView.GetTextAsync();
        var known = ControlRegistry.IsBuiltIn(type) || (ControlTypes.IsCustom(type) && Project?.CustomControls.Any(c => c.Id == ControlTypes.CustomId(type)) == true);
        if (!known) return;
        var p = e.GetPosition(Surface);
        Guard(() => doc.AddAt(type, p.X, p.Y));
    }

    // ---------------------------------------------------------------- toolbar commands

    private void Undo_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Undo());
    private void Redo_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Redo());
    private void Cut_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Cut());
    private void Copy_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Copy());
    private void Paste_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Paste());
    private void Duplicate_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Duplicate());
    private void Delete_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.DeleteSelection());
    private void Group_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Group());
    private void Ungroup_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Ungroup());

    private void AlignLeft_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.Left));
    private void AlignCenterH_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.CenterHorizontal));
    private void AlignRight_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.Right));
    private void AlignTop_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.Top));
    private void AlignCenterV_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.CenterVertical));
    private void AlignBottom_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Align(AlignKind.Bottom));
    private void DistributeH_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Distribute(DistributeAxis.Horizontal));
    private void DistributeV_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.Distribute(DistributeAxis.Vertical));
    private void MatchWidth_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MatchSize(true, false));
    private void MatchHeight_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MatchSize(false, true));
    private void ToFront_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MoveLayer(LayerMove.ToFront));
    private void ToBack_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MoveLayer(LayerMove.ToBack));
    private void Forward_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MoveLayer(LayerMove.Forward));
    private void Backward_Click(object sender, RoutedEventArgs e) => Guard(() => Doc?.MoveLayer(LayerMove.Backward));

    private void SpacingH_Click(object sender, RoutedEventArgs e) => AskSpacing(DistributeAxis.Horizontal);
    private void SpacingV_Click(object sender, RoutedEventArgs e) => AskSpacing(DistributeAxis.Vertical);

    private async void AskSpacing(DistributeAxis axis)
    {
        var text = await UiDialogs.PromptTextAsync(XamlRoot, "Set spacing", "Gap between elements (pixels)", "8", "Apply");
        if (text is null) return;
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gap) || gap < 0 || gap > 500)
        {
            await UiDialogs.MessageAsync(XamlRoot, "Set spacing", "Enter a number between 0 and 500.");
            return;
        }
        Guard(() => Doc?.SetSpacing(axis, gap));
    }

    private async void Validate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = await _projects.ValidateAsync();
            var relevant = report.Issues.Where(i => i.Location is not null && (i.Location.StartsWith("pages[") || i.Location.StartsWith("customControls[") || i.Location.StartsWith("installation"))).ToList();
            await UiDialogs.MessageAsync(XamlRoot, "Design validation",
                relevant.Count == 0 ? "No design problems found." : string.Join(Environment.NewLine, relevant.Select(i => $"{i.Severity} {i.Code}: {i.Message}")));
        }
        catch (Exception ex) { await _errors.ReportAsync("Validation failed", ex); }
    }

    // ---------------------------------------------------------------- layers

    private List<LayerNode> BuildNodes(IEnumerable<DesignElement> list)
    {
        // top-most element first
        var nodes = new List<LayerNode>();
        foreach (var e in list.Reverse())
        {
            var n = new LayerNode { Element = e };
            n.Children.AddRange(BuildNodes(e.Children));
            nodes.Add(n);
        }
        return nodes;
    }

    private void RefreshLayers()
    {
        _syncingLayers = true;
        LayerTree.ItemsSource = Doc is null ? null : BuildNodes(Doc.Roots);
        _syncingLayers = false;
        SyncLayerSelection();
    }

    private LayerNode? FindNode(IEnumerable<LayerNode>? nodes, string id)
    {
        if (nodes is null) return null;
        foreach (var n in nodes)
        {
            if (string.Equals(n.Element.Id, id, StringComparison.OrdinalIgnoreCase)) return n;
            if (FindNode(n.Children, id) is { } inner) return inner;
        }
        return null;
    }

    private void SyncLayerSelection()
    {
        if (Doc is null) return;
        _syncingLayers = true;
        try
        {
            var first = Doc.SelectedIds.FirstOrDefault();
            LayerTree.SelectedItem = first is null ? null : FindNode(LayerTree.ItemsSource as IEnumerable<LayerNode>, first);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { }
        finally { _syncingLayers = false; }
    }

    private void LayerTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args)
    {
        if (_syncingLayers || Doc is null) return;
        if (sender.SelectedItem is LayerNode n) Doc.Select(n.Element.Id);
    }

    private void ToggleVisible_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        foreach (var el in Doc?.SelectionRoots().ToList() ?? new List<DesignElement>()) Doc!.SetVisible(el.Id, !el.Visible);
    });

    private void ToggleLock_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        foreach (var el in Doc?.SelectionRoots().ToList() ?? new List<DesignElement>()) Doc!.SetLocked(el.Id, !el.Locked);
    });
}
