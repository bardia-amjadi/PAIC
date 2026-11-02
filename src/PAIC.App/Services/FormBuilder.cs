using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PAIC.Controls;
using Windows.System;

namespace PAIC.App.Services;

/// <summary>Builds simple property forms (labelled editors that commit on leaving the field) into a panel.</summary>
public sealed class FormBuilder
{
    private readonly StackPanel _panel;

    public FormBuilder(StackPanel panel) { _panel = panel; _panel.Children.Clear(); }

    public void Section(string title) =>
        _panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 10, 0, 0) });

    public void Note(string text) =>
        _panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });

    public void Add(UIElement element) => _panel.Children.Add(element);

    public TextBox Text(string label, string value, Action<string> commit, bool multiline = false, bool readOnly = false, string? placeholder = null)
    {
        var tb = new TextBox
        {
            Header = label, Text = value, IsReadOnly = readOnly, PlaceholderText = placeholder ?? "",
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = multiline ? 80 : 0
        };
        var committed = value;
        void Commit() { if (tb.Text == committed || readOnly) return; committed = tb.Text; commit(tb.Text); }
        tb.LostFocus += (_, _) => Commit();
        if (!multiline) tb.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { Commit(); e.Handled = true; } };
        _panel.Children.Add(tb);
        return tb;
    }

    public CheckBox Bool(string label, bool value, Action<bool> commit)
    {
        var cb = new CheckBox { Content = label, IsChecked = value };
        cb.Click += (_, _) => commit(cb.IsChecked == true);
        _panel.Children.Add(cb);
        return cb;
    }

    public ComboBox Choice(string label, IReadOnlyList<string> choices, string current, Action<string> commit)
    {
        var items = choices.ToList();
        if (!items.Contains(current)) items.Insert(0, current);
        var cb = new ComboBox { Header = label, ItemsSource = items, SelectedItem = current, HorizontalAlignment = HorizontalAlignment.Stretch };
        cb.SelectionChanged += (_, _) => { if (cb.SelectedItem is string s && s != current) { current = s; commit(s); } };
        _panel.Children.Add(cb);
        return cb;
    }

    /// <summary>An optional condition on an installer variable.</summary>
    public void Condition(string title, ConditionDefinition? current, Action<ConditionDefinition?> setter)
    {
        var cond = current?.Clone() ?? new ConditionDefinition();
        var detail = new StackPanel { Spacing = 6, Visibility = current is null ? Visibility.Collapsed : Visibility.Visible };
        var toggle = new CheckBox { Content = title, IsChecked = current is not null };
        toggle.Click += (_, _) =>
        {
            detail.Visibility = toggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            setter(toggle.IsChecked == true ? cond.Clone() : null);
        };
        _panel.Children.Add(toggle);

        var variable = new TextBox { Header = "Variable", Text = cond.Variable };
        variable.LostFocus += (_, _) => { if (variable.Text != cond.Variable) { cond.Variable = variable.Text.Trim(); setter(cond.Clone()); } };
        var op = new ComboBox { Header = "Operator", ItemsSource = Enum.GetNames<ConditionOperator>(), SelectedItem = cond.Operator.ToString(), HorizontalAlignment = HorizontalAlignment.Stretch };
        op.SelectionChanged += (_, _) => { if (op.SelectedItem is string s && Enum.TryParse<ConditionOperator>(s, out var parsed) && parsed != cond.Operator) { cond.Operator = parsed; setter(cond.Clone()); } };
        var value = new TextBox { Header = "Value", Text = cond.Value };
        value.LostFocus += (_, _) => { if (value.Text != cond.Value) { cond.Value = value.Text; setter(cond.Clone()); } };
        detail.Children.Add(variable); detail.Children.Add(op); detail.Children.Add(value);
        _panel.Children.Add(detail);
    }
}
