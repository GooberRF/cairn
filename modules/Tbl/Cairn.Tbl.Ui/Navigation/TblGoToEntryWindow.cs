using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Tbl.Model;

namespace Cairn.Tbl.Ui.Navigation;

/// <summary>A quick pick of a table's entries: type to filter, Enter (or double-click) jumps to the chosen one.</summary>
public sealed class TblGoToEntryWindow : Window
{
    private sealed record Item(string Text, int Start, int Length)
    {
        public override string ToString() => Text;
    }

    private readonly List<Item> _all;
    private readonly TextBox _filter = new() { Margin = new Thickness(8, 8, 8, 4) };
    private readonly ListBox _list = new() { Margin = new Thickness(8, 0, 8, 8) };

    /// <param name="table">The parsed table.</param>
    public TblGoToEntryWindow(TblDocument table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Title = "Go to Entry";
        Width = 420;
        Height = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "App.WindowBackground");
        SetResourceReference(ForegroundProperty, "App.Text");
        AutomationProperties.SetName(this, "Go to entry");

        _all = table.Sections.SelectMany(s => s.Entries.Select(e => new Item(
            (e.Name.Length > 0 ? e.Name : "(unnamed)") + (s.HasHeader ? "    #" + s.Name : ""),
            e.NameSpan.Length > 0 ? e.NameSpan.Start : e.Span.Start, e.NameSpan.Length))).ToList();

        _filter.ToolTip = "Type part of an entry name";
        AutomationProperties.SetName(_filter, "Entry name");
        _filter.TextChanged += (_, _) => Refill();
        _filter.PreviewKeyDown += OnFilterKey;
        AutomationProperties.SetName(_list, "Entries");
        _list.MouseDoubleClick += (_, _) => Accept();
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Accept(); e.Handled = true; } };
        var root = new DockPanel();
        DockPanel.SetDock(_filter, Dock.Top);
        root.Children.Add(_filter);
        root.Children.Add(_list);
        Content = root;
        Refill();
        Loaded += (_, _) => _filter.Focus();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; } };
    }

    /// <summary>The chosen entry's (start, length), or null.</summary>
    public (int Start, int Length)? Result { get; private set; }

    private void Refill()
    {
        string f = _filter.Text.Trim();
        _list.ItemsSource = f.Length == 0 ? _all : _all.Where(i => i.Text.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void OnFilterKey(object sender, KeyEventArgs e)
    {
        int n = _list.Items.Count;
        if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
        else if (e.Key == Key.Down && n > 0) { _list.SelectedIndex = Math.Min(n - 1, _list.SelectedIndex + 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up && n > 0) { _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); _list.ScrollIntoView(_list.SelectedItem); e.Handled = true; }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not Item item) return;
        Result = (item.Start, item.Length);
        DialogResult = true;
    }
}
