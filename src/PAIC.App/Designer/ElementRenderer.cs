using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PAIC.App.Services;
using PAIC.Controls;
using PAIC.Designer;
using PAIC.ProjectSystem.Model;
using PAIC.ThemeEngine;

namespace PAIC.App.Designer;

/// <summary>Everything the renderer needs to draw a page. In preview mode the session drives visibility and enabling.</summary>
public sealed class RenderContext
{
    public required PaicProject Project { get; init; }
    public required ThemeDefinition Theme { get; init; }
    public required Dictionary<string, string> Variables { get; init; }
    public bool DesignMode { get; init; }
    public PreviewSession? Session { get; init; }
    public double InstallProgress { get; init; }
    public Func<string, string?> AssetPath { get; init; } = _ => null;
    public Func<string, CustomControlDefinition?> Control { get; init; } = _ => null;
    public Action<DesignElement>? Clicked { get; init; }
    public Action<string, string>? VariableChanged { get; init; }
    /// <summary>Re-evaluates visibility, enabled state and templated text without rebuilding controls (keeps focus while typing).</summary>
    public List<Action> Updaters { get; } = new();

    public void RunUpdaters() { foreach (var u in Updaters) u(); }
}

/// <summary>Draws installer pages from the element model using the active theme's design tokens.</summary>
public static class ElementRenderer
{
    private static readonly Windows.UI.Color Fallback = Colors.Gray;

    private static readonly Dictionary<string, string> GlyphMap = new()
    {
        ["info"] = "\uE946", ["warning"] = "\uE7BA", ["error"] = "\uEA39", ["success"] = "\uE73E", ["folder"] = "\uE8B7",
        ["download"] = "\uE896", ["settings"] = "\uE713", ["shield"] = "\uEA18", ["app"] = "\uECAA", ["user"] = "\uE77B"
    };

    // ---------------------------------------------------------------- tokens

    public static SolidColorBrush Brush(RenderContext c, string token) =>
        UiColor.Brush(c.Theme.Colors.TryGetValue(token, out var hex) ? hex : null, Fallback);

    private static double Size(RenderContext c, string style) => style switch
    {
        "caption" => c.Theme.Typography.CaptionSize,
        "subtitle" => c.Theme.Typography.SubtitleSize,
        "title" => c.Theme.Typography.TitleSize,
        _ => c.Theme.Typography.BodySize
    };

    private static FontFamily Font(RenderContext c) =>
        new(string.IsNullOrWhiteSpace(c.Theme.Typography.FontFamily) ? "Segoe UI" : c.Theme.Typography.FontFamily);

    private static CornerRadius Radius(RenderContext c) => new(Math.Clamp(c.Theme.Shape.CornerRadius, 0, 32));

    private static string Resolve(RenderContext c, string? text) => TextTemplating.Resolve(text, c.Variables);

    // ---------------------------------------------------------------- page

    /// <summary>Builds the page surface (background plus elements) as a Canvas of the given size.</summary>
    public static Canvas BuildPage(IReadOnlyList<DesignElement> elements, double width, double height, PageBackground background, RenderContext c)
    {
        var canvas = new Canvas { Width = width, Height = height, Background = PageBrush(background, c), Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, height) } };
        if (background.Kind == PageBackgroundKind.Image && c.AssetPath(background.Value) is { } path)
        {
            canvas.Children.Add(new Image { Width = width, Height = height, Stretch = Stretch.UniformToFill, Source = LoadImage(path), IsHitTestVisible = false });
        }
        foreach (var e in elements) AddElement(canvas, e, new PAIC.Controls.Rect(e.X, e.Y, e.Width, e.Height), c);
        c.RunUpdaters();
        return canvas;
    }

    private static Brush PageBrush(PageBackground bg, RenderContext c) => bg.Kind switch
    {
        PageBackgroundKind.Token => Brush(c, bg.Value),
        PageBackgroundKind.Color => UiColor.Brush(bg.Value, Fallback),
        _ => Brush(c, ThemeTokens.Background)
    };

    private static void AddElement(Canvas parent, DesignElement e, PAIC.Controls.Rect rect, RenderContext c)
    {
        var source = e;
        if (ControlTypes.IsCustom(e.Type))
        {
            var def = c.Control(ControlTypes.CustomId(e.Type));
            if (def is null) source = Placeholder(e, $"Missing control '{ControlTypes.CustomId(e.Type)}'");
            else source = CustomControlExpander.Expand(e, def);
        }

        var fe = Render(source, rect, c);
        fe.Width = Math.Max(1, rect.Width); fe.Height = Math.Max(1, rect.Height);
        Canvas.SetLeft(fe, rect.X); Canvas.SetTop(fe, rect.Y);
        if (c.DesignMode)
        {
            fe.IsHitTestVisible = false;
            if (!e.Visible) fe.Opacity = 0.35;
        }
        else
        {
            var element = e;
            c.Updaters.Add(() =>
            {
                var visible = c.Session?.IsElementVisible(element) ?? element.Visible;
                fe.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (fe is Control ctl && c.Session is not null) ctl.IsEnabled = c.Session.IsElementEnabled(element);
            });
        }
        parent.Children.Add(fe);
    }

    private static DesignElement Placeholder(DesignElement e, string message) => new()
    {
        Id = e.Id, Type = ControlTypes.Text, X = e.X, Y = e.Y, Width = e.Width, Height = e.Height,
        Properties = { ["text"] = message, ["color"] = "error", ["style"] = "caption" }
    };

    // ---------------------------------------------------------------- element rendering

    private static FrameworkElement Render(DesignElement e, PAIC.Controls.Rect rect, RenderContext c) => e.Type switch
    {
        ControlTypes.Text => RenderText(e, c),
        ControlTypes.RichText => RenderRichText(e, c),
        ControlTypes.Image => RenderImage(e, c),
        ControlTypes.Button => RenderButton(e, c),
        ControlTypes.TextInput => RenderTextInput(e, c),
        ControlTypes.PasswordInput => RenderPassword(e, c),
        ControlTypes.Checkbox => RenderCheckbox(e, c),
        ControlTypes.Radio => RenderRadio(e, c),
        ControlTypes.Dropdown => RenderDropdown(e, c),
        ControlTypes.List => RenderList(e, c),
        ControlTypes.Progress => RenderProgress(e, c),
        ControlTypes.Card => RenderCard(e, rect, c),
        ControlTypes.Stepper => RenderStepper(e, c),
        ControlTypes.Accordion => RenderAccordion(e, rect, c),
        ControlTypes.Separator => RenderSeparator(e, c),
        ControlTypes.Icon => RenderIcon(e, c),
        ControlTypes.Container or ControlTypes.Group => RenderContainer(e, rect, c),
        _ => new TextBlock { Text = $"Unknown control '{e.Type}'", Foreground = Brush(c, ThemeTokens.Error) }
    };

    private static TextBlock RenderText(DesignElement e, RenderContext c)
    {
        var style = e.GetString("style");
        var tb = new TextBlock
        {
            FontFamily = Font(c), FontSize = Size(c, style), Foreground = Brush(c, e.GetString("color")),
            TextWrapping = e.GetBool("wrap", true) ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = e.GetString("align") switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
            FontWeight = style is "title" or "subtitle" ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = e.GetBool("wrap", true) ? TextTrimming.None : TextTrimming.CharacterEllipsis
        };
        var raw = e.GetString("text");
        tb.Text = Resolve(c, raw);
        if (!c.DesignMode) c.Updaters.Add(() => tb.Text = Resolve(c, raw));
        return tb;
    }

    private static FrameworkElement RenderRichText(DesignElement e, RenderContext c)
    {
        var raw = e.GetString("text");
        var asset = e.GetString("sourceAsset");
        if (!string.IsNullOrWhiteSpace(asset) && c.AssetPath(asset) is { } path)
        {
            try { using var r = new StreamReader(path); var buf = new char[100_000]; raw = new string(buf, 0, r.Read(buf, 0, buf.Length)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { raw = "Could not read " + asset; }
        }
        var tb = new TextBlock { FontFamily = Font(c), FontSize = Size(c, "body"), Foreground = Brush(c, e.GetString("color")), TextWrapping = TextWrapping.Wrap };
        void Fill() { tb.Inlines.Clear(); foreach (var inline in Markup(Resolve(c, raw), c)) tb.Inlines.Add(inline); }
        Fill();
        if (!c.DesignMode) c.Updaters.Add(Fill);
        return new Border
        {
            Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border), BorderThickness = new Thickness(1),
            CornerRadius = Radius(c), Padding = new Thickness(10),
            Child = new ScrollViewer { Content = tb, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };
    }

    /// <summary>Tiny markup: **bold**, *italic*, newlines.</summary>
    private static IEnumerable<Inline> Markup(string text, RenderContext c)
    {
        var bold = false; var italic = false;
        var sb = new System.Text.StringBuilder();
        var result = new List<Inline>();
        void Flush()
        {
            if (sb.Length == 0) return;
            result.Add(new Run { Text = sb.ToString(), FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal });
            sb.Clear();
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*') { Flush(); bold = !bold; i++; }
            else if (text[i] == '*') { Flush(); italic = !italic; }
            else if (text[i] == '\n') { Flush(); result.Add(new LineBreak()); }
            else if (text[i] != '\r') sb.Append(text[i]);
        }
        Flush();
        return result;
    }

    private static ImageSource? LoadImage(string path)
    {
        try
        {
            return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? new SvgImageSource(new Uri(path)) : new BitmapImage(new Uri(path));
        }
        catch (Exception ex) when (ex is UriFormatException or ArgumentException) { return null; }
    }

    private static FrameworkElement RenderImage(DesignElement e, RenderContext c)
    {
        var path = c.AssetPath(e.GetString("asset"));
        var source = path is null ? null : LoadImage(path);
        if (source is null)
            return new Border
            {
                BorderBrush = Brush(c, ThemeTokens.Border), BorderThickness = new Thickness(1), CornerRadius = Radius(c), Background = Brush(c, ThemeTokens.SurfaceAlt),
                Child = new TextBlock { Text = string.IsNullOrWhiteSpace(e.GetString("asset")) ? "No image" : "Missing image", Foreground = Brush(c, ThemeTokens.TextSecondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
        var stretch = e.GetString("stretch") switch { "fill" => Stretch.Fill, "none" => Stretch.None, "uniformToFill" => Stretch.UniformToFill, _ => Stretch.Uniform };
        return new Image { Source = source, Stretch = stretch };
    }

    private static FrameworkElement RenderButton(DesignElement e, RenderContext c)
    {
        var style = e.GetString("style");
        var btn = new Button
        {
            Content = Resolve(c, e.GetString("text")), FontFamily = Font(c), FontSize = Size(c, "body"), CornerRadius = Radius(c),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(8, 0, 8, 0)
        };
        switch (style)
        {
            case "accent": btn.Background = Brush(c, ThemeTokens.Accent); btn.Foreground = Brush(c, ThemeTokens.AccentText); btn.BorderThickness = new Thickness(0); break;
            case "subtle": btn.Background = new SolidColorBrush(Colors.Transparent); btn.Foreground = Brush(c, ThemeTokens.TextPrimary); btn.BorderThickness = new Thickness(0); break;
            default: btn.Background = Brush(c, ThemeTokens.SurfaceAlt); btn.Foreground = Brush(c, ThemeTokens.TextPrimary); btn.BorderBrush = Brush(c, ThemeTokens.Border); break;
        }
        var raw = e.GetString("text");
        btn.Click += (_, _) => c.Clicked?.Invoke(e);
        if (!c.DesignMode) c.Updaters.Add(() => btn.Content = Resolve(c, raw));
        return btn;
    }

    private static StackPanel Labelled(DesignElement e, RenderContext c, FrameworkElement input)
    {
        var panel = new StackPanel { Spacing = 4 };
        var label = e.GetString("label");
        if (!string.IsNullOrEmpty(label))
            panel.Children.Add(new TextBlock { Text = Resolve(c, label), FontFamily = Font(c), FontSize = Size(c, "caption"), Foreground = Brush(c, ThemeTokens.TextSecondary) });
        panel.Children.Add(input);
        return panel;
    }

    private static FrameworkElement RenderTextInput(DesignElement e, RenderContext c)
    {
        var variable = e.GetString("variable");
        var tb = new TextBox
        {
            PlaceholderText = e.GetString("placeholder"), FontFamily = Font(c), FontSize = Size(c, "body"), CornerRadius = Radius(c),
            Foreground = Brush(c, ThemeTokens.TextPrimary), Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border),
            Text = c.Variables.TryGetValue(variable, out var v) ? v : e.GetString("defaultValue")
        };
        if (!c.DesignMode)
        {
            tb.TextChanged += (_, _) => { c.VariableChanged?.Invoke(variable, tb.Text); };
            c.Updaters.Add(() => { if (c.Variables.TryGetValue(variable, out var cur) && cur != tb.Text && !tb.FocusState.Equals(FocusState.Keyboard)) tb.Text = cur; });
        }
        return Labelled(e, c, tb);
    }

    private static FrameworkElement RenderPassword(DesignElement e, RenderContext c)
    {
        var variable = e.GetString("variable");
        var pb = new PasswordBox { FontFamily = Font(c), FontSize = Size(c, "body"), CornerRadius = Radius(c), Foreground = Brush(c, ThemeTokens.TextPrimary), Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border) };
        if (!c.DesignMode) pb.PasswordChanged += (_, _) => c.VariableChanged?.Invoke(variable, pb.Password);
        return Labelled(e, c, pb);
    }

    private static FrameworkElement RenderCheckbox(DesignElement e, RenderContext c)
    {
        var variable = e.GetString("variable");
        var cb = new CheckBox
        {
            Content = Resolve(c, e.GetString("label")), FontFamily = Font(c), FontSize = Size(c, "body"), Foreground = Brush(c, ThemeTokens.TextPrimary),
            IsChecked = c.Variables.TryGetValue(variable, out var v) ? ConditionEvaluator.IsTruthy(v) : e.GetBool("defaultChecked")
        };
        if (!c.DesignMode)
        {
            void Set() => c.VariableChanged?.Invoke(variable, cb.IsChecked == true ? "true" : "false");
            cb.Checked += (_, _) => Set(); cb.Unchecked += (_, _) => Set();
        }
        return cb;
    }

    private static string[] Options(DesignElement e) =>
        e.GetString("options").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FrameworkElement RenderRadio(DesignElement e, RenderContext c)
    {
        var variable = e.GetString("variable");
        var options = Options(e);
        var rb = new RadioButtons { Header = Resolve(c, e.GetString("label")), FontFamily = Font(c), Foreground = Brush(c, ThemeTokens.TextPrimary), ItemsSource = options };
        var current = c.Variables.TryGetValue(variable, out var v) && !string.IsNullOrEmpty(v) ? v : e.GetString("defaultValue");
        rb.SelectedIndex = Array.IndexOf(options, current);
        if (!c.DesignMode) rb.SelectionChanged += (_, _) => { if (rb.SelectedItem is string s) c.VariableChanged?.Invoke(variable, s); };
        return rb;
    }

    private static FrameworkElement RenderDropdown(DesignElement e, RenderContext c)
    {
        var variable = e.GetString("variable");
        var options = Options(e);
        var cb = new ComboBox { ItemsSource = options, FontFamily = Font(c), CornerRadius = Radius(c), HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = Brush(c, ThemeTokens.TextPrimary), Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border) };
        var current = c.Variables.TryGetValue(variable, out var v) && !string.IsNullOrEmpty(v) ? v : e.GetString("defaultValue");
        cb.SelectedIndex = Array.IndexOf(options, current);
        if (cb.SelectedIndex < 0 && options.Length > 0) cb.SelectedIndex = 0;
        if (!c.DesignMode) cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s) c.VariableChanged?.Invoke(variable, s); };
        return Labelled(e, c, cb);
    }

    private static FrameworkElement RenderList(DesignElement e, RenderContext c)
    {
        var source = e.GetString("source");
        var panel = new StackPanel { Spacing = 4 };
        switch (source)
        {
            case "components":
                foreach (var comp in c.Project.Components)
                    panel.Children.Add(new CheckBox { Content = comp.Name + (comp.Required ? " (required)" : ""), IsChecked = comp.Required || comp.DefaultSelected, IsEnabled = !comp.Required, FontFamily = Font(c), Foreground = Brush(c, ThemeTokens.TextPrimary) });
                if (c.Project.Components.Count == 0) panel.Children.Add(Muted("No components are defined in the project.", c));
                break;
            case "prerequisites":
                foreach (var pre in c.Project.Prerequisites)
                    panel.Children.Add(new TextBlock { Text = $"{pre.Name}  \u2014  pending", FontFamily = Font(c), Foreground = Brush(c, ThemeTokens.TextPrimary) });
                if (c.Project.Prerequisites.Count == 0) panel.Children.Add(Muted("No prerequisites are defined in the project.", c));
                break;
            default:
                var variable = e.GetString("variable");
                var lv = new ListView { ItemsSource = e.GetString("items").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), SelectionMode = ListViewSelectionMode.Single, FontFamily = Font(c) };
                if (!c.DesignMode && !string.IsNullOrWhiteSpace(variable))
                    lv.SelectionChanged += (_, _) => { if (lv.SelectedItem is string s) c.VariableChanged?.Invoke(variable, s); };
                return Framed(lv, c);
        }
        return Framed(new ScrollViewer { Content = panel }, c);
    }

    private static TextBlock Muted(string text, RenderContext c) =>
        new() { Text = text, FontFamily = Font(c), FontSize = Size(c, "caption"), Foreground = Brush(c, ThemeTokens.TextSecondary) };

    private static Border Framed(UIElement child, RenderContext c) => new()
    {
        Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border), BorderThickness = new Thickness(1),
        CornerRadius = Radius(c), Padding = new Thickness(8), Child = child
    };

    private static FrameworkElement RenderProgress(DesignElement e, RenderContext c)
    {
        var pb = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Foreground = Brush(c, ThemeTokens.Accent), Background = Brush(c, ThemeTokens.Border),
            IsIndeterminate = e.GetBool("indeterminate"), VerticalAlignment = VerticalAlignment.Center
        };
        pb.Value = e.GetString("source") == "install" ? c.InstallProgress : Math.Clamp(e.GetNumber("value", 0), 0, 100);
        return pb;
    }

    private static FrameworkElement RenderCard(DesignElement e, PAIC.Controls.Rect rect, RenderContext c)
    {
        var grid = new Grid();
        var border = new Border { Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border), BorderThickness = new Thickness(1), CornerRadius = Radius(c) };
        grid.Children.Add(border);
        var title = Resolve(c, e.GetString("title"));
        if (!string.IsNullOrWhiteSpace(title))
            grid.Children.Add(new TextBlock { Text = title, FontFamily = Font(c), FontSize = Size(c, "body"), FontWeight = FontWeights.SemiBold, Foreground = Brush(c, ThemeTokens.TextPrimary), Margin = new Thickness(12, 6, 12, 0), VerticalAlignment = VerticalAlignment.Top });
        grid.Children.Add(ChildCanvas(e, c));
        return grid;
    }

    private static FrameworkElement RenderAccordion(DesignElement e, PAIC.Controls.Rect rect, RenderContext c)
    {
        var expanded = e.GetBool("expanded", true);
        var grid = new Grid();
        grid.Children.Add(new Border { Background = Brush(c, ThemeTokens.Surface), BorderBrush = Brush(c, ThemeTokens.Border), BorderThickness = new Thickness(1), CornerRadius = Radius(c) });
        grid.Children.Add(new TextBlock
        {
            Text = (expanded ? "\u25BE  " : "\u25B8  ") + Resolve(c, e.GetString("header")), FontFamily = Font(c), FontSize = Size(c, "body"), FontWeight = FontWeights.SemiBold,
            Foreground = Brush(c, ThemeTokens.TextPrimary), Margin = new Thickness(12, 8, 12, 0), VerticalAlignment = VerticalAlignment.Top
        });
        if (expanded) grid.Children.Add(ChildCanvas(e, c));
        return grid;
    }

    private static FrameworkElement RenderStepper(DesignElement e, RenderContext c)
    {
        var steps = e.GetString("steps").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = (int)Math.Clamp(e.GetNumber("current", 1), 1, Math.Max(1, steps.Length));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < steps.Length; i++)
        {
            var done = i + 1 <= current;
            row.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = done ? Brush(c, ThemeTokens.Accent) : Brush(c, ThemeTokens.Border),
                Child = new TextBlock { Text = (i + 1).ToString(), FontFamily = Font(c), FontSize = Size(c, "caption"), Foreground = done ? Brush(c, ThemeTokens.AccentText) : Brush(c, ThemeTokens.TextSecondary), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
            row.Children.Add(new TextBlock { Text = steps[i], FontFamily = Font(c), FontSize = Size(c, "body"), Foreground = Brush(c, i + 1 == current ? ThemeTokens.TextPrimary : ThemeTokens.TextSecondary), FontWeight = i + 1 == current ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center });
            if (i < steps.Length - 1) row.Children.Add(new Rectangle { Width = 28, Height = 2, Fill = Brush(c, ThemeTokens.Border), VerticalAlignment = VerticalAlignment.Center });
        }
        return row;
    }

    private static FrameworkElement RenderSeparator(DesignElement e, RenderContext c)
    {
        var vertical = e.GetString("orientation") == "vertical";
        return new Rectangle
        {
            Fill = Brush(c, ThemeTokens.Border),
            Height = vertical ? double.NaN : 1, Width = vertical ? 1 : double.NaN,
            HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center
        };
    }

    private static FrameworkElement RenderIcon(DesignElement e, RenderContext c) => new TextBlock
    {
        Text = GlyphMap.TryGetValue(e.GetString("glyph"), out var g) ? g : "\uE946",
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = Math.Clamp(e.GetNumber("size", 32), 8, 128),
        Foreground = Brush(c, e.GetString("color")), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };

    private static FrameworkElement RenderContainer(DesignElement e, PAIC.Controls.Rect rect, RenderContext c)
    {
        var grid = new Grid();
        if (c.DesignMode)
            grid.Children.Add(new Border { BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2) });
        grid.Children.Add(ChildCanvas(e, c));
        return grid;
    }

    /// <summary>Canvas holding the element's children, offset by the container's content origin.</summary>
    private static Canvas ChildCanvas(DesignElement e, RenderContext c)
    {
        var canvas = new Canvas();
        var (ox, oy) = LayoutEngine.ContentOffset(e);
        canvas.Margin = new Thickness(ox, oy, 0, 0);
        var rects = LayoutEngine.ArrangeChildren(e);
        for (var i = 0; i < e.Children.Count; i++) AddElement(canvas, e.Children[i], rects[i], c);
        return canvas;
    }
}
