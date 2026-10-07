using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Cairn.Vfx.Ui.Dialogs;

/// <summary>
/// A small themed options dialog built in code: labelled rows, a read-only summary that the owner
/// refreshes on every change (<see cref="Changed"/>), OK / Cancel. Keyboard: Tab order follows the
/// rows, labels carry access keys, Enter = OK, Esc = Cancel.
/// </summary>
public sealed class VfxForm : Window
{
    private readonly Grid _rows = new();
    private readonly TextBox _summary;
    private readonly Button _ok;

    public event EventHandler? Changed;

    public VfxForm(string title, Window? owner, string okText = "_OK")
    {
        Title = title;
        Owner = owner;
        Width = 480; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        MinWidth = 400;
        _rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        _rows.ColumnDefinitions.Add(new ColumnDefinition());
        _summary = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, MinHeight = 70, MaxHeight = 220, Margin = new Thickness(0, 10, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, ToolTip = "What will be created",
        };
        AutomationProperties.SetName(_summary, "Summary");
        _ok = new Button { Content = new AccessText { Text = okText }, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        _ok.Click += (_, _) => DialogResult = true;
        AutomationProperties.SetName(_ok, okText.Replace("_", ""));
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        AutomationProperties.SetName(cancel, "Cancel");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Children = { _ok, cancel } };
        var root = new StackPanel { Margin = new Thickness(14), Children = { _rows, _summary, buttons } };
        Content = root;
        Loaded += (_, _) => { Refresh(); MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First)); };
    }

    public string Summary { get => _summary.Text; set => _summary.Text = value; }
    public bool CanAccept { get => _ok.IsEnabled; set => _ok.IsEnabled = value; }

    /// <summary>Raises <see cref="Changed"/> (summary refresh).</summary>
    public void Refresh() => Changed?.Invoke(this, EventArgs.Empty);

    public T Row<T>(string label, T control, string tooltip) where T : FrameworkElement
    {
        int row = _rows.RowDefinitions.Count;
        _rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // Label/value rows without colons, as in the inspectors.
        // tall controls (lists, previews) get a top-aligned label so it sits beside their first line
        bool tall = control is ListBox || control.Height > 40 || control.MinHeight > 40;
        var text = new Label { Content = label.TrimEnd(':', ' '), Target = control, Padding = new Thickness(0, tall ? 7 : 4, 6, 4), VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center };
        if (control is ListBox) ResizeMode = ResizeMode.CanResize; // long lists: let the user widen the dialog
        text.SetResourceReference(ForegroundProperty, "App.Text");
        control.Margin = new Thickness(0, 3, 0, 3);
        control.ToolTip = tooltip;
        AutomationProperties.SetName(control, label.Replace("_", "").TrimEnd(':'));
        Grid.SetRow(text, row); Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        _rows.Children.Add(text); _rows.Children.Add(control);
        return control;
    }

    public TextBox Text(string label, string value, string tooltip)
    {
        var box = Row(label, new TextBox { Text = value }, tooltip);
        box.TextChanged += (_, _) => Refresh();
        return box;
    }

    public ComboBox Combo(string label, IEnumerable<object> items, int selected, string tooltip)
    {
        var combo = Row(label, new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected }, tooltip);
        combo.SelectionChanged += (_, _) => Refresh();
        return combo;
    }

    public CheckBox Check(string label, bool value, string tooltip)
    {
        var box = Row(label, new CheckBox { IsChecked = value, VerticalAlignment = VerticalAlignment.Center }, tooltip);
        box.Click += (_, _) => Refresh();
        return box;
    }

    public static float Number(TextBox box, float fallback) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v > 0 ? v : fallback;
    public static float NumberOrZero(TextBox box) =>
        float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : 0;
}
