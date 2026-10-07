using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Cairn.Previews;

/// <summary>One line of a text preview.</summary>
public sealed record TextLine(int Number, string Text);

/// <summary>Decoded text for <see cref="TextPreview"/>: the encoding found and the lines.</summary>
public sealed class TextData
{
    /// <summary>Lines shown before the rest arrive, so big files appear at once.</summary>
    public const int FirstBatch = 2_000;
    /// <summary>Longest line shown in full; longer lines are cut (the details say how long they are).</summary>
    private const int MaxLineChars = 4_000;

    public required string EncodingName { get; init; }
    public required List<TextLine> Lines { get; init; }

    /// <summary>True when <paramref name="head"/> looks like text: no NULs and mostly printable (UTF-16 with a BOM counts).</summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0) return false;
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return true;
        int control = 0;
        foreach (byte b in head)
        {
            if (b == 0) return false;
            if (b < 32 && b is not (9 or 10 or 13 or 12)) control++;
        }
        return control * 50 < head.Length;
    }

    /// <summary>Detects the encoding and splits the text into lines (pool thread).</summary>
    public static TextData Decode(byte[] bytes, CancellationToken ct)
    {
        string name;
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            name = "UTF-8 (with BOM)";
            text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            name = "UTF-16 LE";
            text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            name = "UTF-16 BE";
            text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        else if (bytes.All(b => b < 0x80))
        {
            name = "ASCII";
            text = Encoding.ASCII.GetString(bytes);
        }
        else
        {
            try
            {
                text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                name = "UTF-8";
            }
            catch (DecoderFallbackException)
            {
                text = Encoding.Latin1.GetString(bytes);
                name = "Latin-1 (Windows-1252)";
            }
        }
        ct.ThrowIfCancellationRequested();

        var lines = new List<TextLine>();
        int start = 0, number = 1;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\r') continue;
            int length = i - start;
            string line = length > MaxLineChars ? text.Substring(start, MaxLineChars) + " ..." : text.Substring(start, length);
            lines.Add(new TextLine(number++, line.Replace('\t', ' ')));
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
            if ((number & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
        }
        // A final line break does not start another line.
        if (lines.Count > 1 && lines[^1].Text.Length == 0 && text.Length > 0 && text[^1] is '\n' or '\r') lines.RemoveAt(lines.Count - 1);
        return new TextData { EncodingName = name, Lines = lines };
    }
}

/// <summary>A collection that can take many items with one notification (WPF lists do not accept range adds).</summary>
internal sealed class BulkCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>
/// Read-only text preview: monospaced lines with numbers in a virtualised list, the detected encoding, and
/// find (Ctrl+F, F3 / Shift+F3). The first lines show at once and the rest follow.
/// </summary>
public sealed class TextPreview : UserControl, IDisposable
{
    private readonly BulkCollection<TextLine> _lines = [];
    private readonly ListBox _list;
    private readonly Border _findBar;
    private readonly TextBox _findBox;
    private readonly TextBlock _findStatus;
    private readonly TextBlock _info;
    private readonly DataTemplate _wrapTemplate, _noWrapTemplate;
    private bool _disposed;

    /// <summary>Whether long lines wrap; shared by every text preview of the session (on by default).</summary>
    private static bool s_wrap = true;

    public TextPreview(TextData data, string? note = null)
    {
        int digits = Math.Max(3, data.Lines.Count.ToString(CultureInfo.InvariantCulture).Length);
        _wrapTemplate = BuildTemplate(digits, wrap: true);
        _noWrapTemplate = BuildTemplate(digits, wrap: false);
        _list = new ListBox
        {
            ItemsSource = _lines,
            SelectionMode = SelectionMode.Extended,
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(_list, "Text");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.SetResourceReference(BackgroundProperty, "Editor.Background");
        _list.SetResourceReference(ForegroundProperty, "Editor.Foreground");
        _list.SetResourceReference(FontFamilyProperty, "MonoFont");
        _list.FontSize = 12;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(0)));
        // Stretch so a wrapped line gets the list's width; without wrapping the list scrolls sideways instead.
        itemStyle.Setters.Add(new Setter(HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        _list.ItemContainerStyle = itemStyle;
        ApplyWrap();

        _findBox = new TextBox { Width = 180, Margin = new Thickness(0, 0, 4, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Text to find (Enter: next, Shift+Enter: previous, Esc: close)" };
        AutomationProperties.SetName(_findBox, "Find");
        _findBox.KeyDown += OnFindKey;
        _findBox.TextChanged += (_, _) => _findStatus!.Text = "";
        _findStatus = PreviewUi.Secondary("");
        _findBar = PreviewUi.Toolbar(
            PreviewUi.Secondary("Find"),
            _findBox,
            PreviewUi.Button("Next", "Find the next match (F3)", (_, _) => Find(forward: true)),
            PreviewUi.Button("Previous", "Find the previous match (Shift+F3)", (_, _) => Find(forward: false)),
            _findStatus,
            PreviewUi.Button("Close", "Close the find bar (Esc)", (_, _) => CloseFind()));
        _findBar.Visibility = Visibility.Collapsed;

        _info = PreviewUi.Secondary(Describe(data, note));
        var wrap = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Wrap",
            IsChecked = s_wrap,
            ToolTip = "Wrap long lines to the width of the pane (off: scroll sideways)",
            Margin = new Thickness(0, 0, 4, 0),
            Padding = new Thickness(7, 0, 7, 0),
            Height = 24,
        };
        wrap.SetResourceReference(StyleProperty, "ToolToggle");
        AutomationProperties.SetName(wrap, "Wrap long lines");
        wrap.Click += (_, _) => { s_wrap = wrap.IsChecked == true; ApplyWrap(); };
        var top = PreviewUi.Toolbar(
            PreviewUi.Button("Find", "Find in this text (Ctrl+F)", (_, _) => OpenFind()),
            PreviewUi.Button("Copy", "Copy the selected lines, or all text when none are selected (Ctrl+C)", (_, _) => Copy()),
            wrap,
            _info);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(_findBar, Dock.Top);
        dock.Children.Add(top);
        dock.Children.Add(_findBar);
        dock.Children.Add(_list);
        Content = dock;
        PreviewKeyDown += OnKey;

        _lines.AddRange(data.Lines.Take(TextData.FirstBatch));
        if (data.Lines.Count > TextData.FirstBatch)
        {
            // The rest after the first lines have rendered.
            Dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) _lines.AddRange(data.Lines.Skip(TextData.FirstBatch));
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    public int LineCount => _lines.Count;
    public string FindStatus => _findStatus.Text;
    /// <summary>True while long lines wrap.</summary>
    public bool IsWrapping => s_wrap;

    private void ApplyWrap()
    {
        _list.ItemTemplate = s_wrap ? _wrapTemplate : _noWrapTemplate;
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, s_wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }

    private static string Describe(TextData data, string? note) =>
        string.Format(CultureInfo.CurrentCulture, "{0}, {1:N0} line{2}", data.EncodingName, data.Lines.Count, data.Lines.Count == 1 ? "" : "s")
        + (note is null ? "" : "; " + note);

    private static DataTemplate BuildTemplate(int digits, bool wrap)
    {
        // Number docked left; the text fills the rest, so with wrapping on it breaks at the pane's width and
        // continues under itself, not under the line number.
        var row = new FrameworkElementFactory(typeof(DockPanel));
        row.SetValue(DockPanel.LastChildFillProperty, true);
        var number = new FrameworkElementFactory(typeof(TextBlock));
        number.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextLine.Number)));
        number.SetValue(DockPanel.DockProperty, Dock.Left);
        number.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
        number.SetValue(VerticalAlignmentProperty, VerticalAlignment.Top);
        number.SetValue(WidthProperty, digits * 7.5 + 6);
        number.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        number.SetResourceReference(TextBlock.ForegroundProperty, "Editor.LineNumber");
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(TextLine.Text)));
        text.SetValue(TextBlock.TextWrappingProperty, wrap ? TextWrapping.Wrap : TextWrapping.NoWrap);
        row.AppendChild(number);
        row.AppendChild(text);
        return new DataTemplate { VisualTree = row };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { OpenFind(); e.Handled = true; }
        else if (e.Key == Key.F3) { Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && !_findBox.IsKeyboardFocusWithin) { Copy(); e.Handled = true; }
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Find(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0); e.Handled = true; }
        else if (e.Key == Key.Escape) { CloseFind(); e.Handled = true; }
    }

    public void OpenFind()
    {
        _findBar.Visibility = Visibility.Visible;
        _findBox.Focus();
        _findBox.SelectAll();
    }

    private void CloseFind()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _list.Focus();
    }

    /// <summary>Selects the next (or previous) line containing the find text, wrapping around; returns its index or -1.</summary>
    public int Find(bool forward, string? text = null)
    {
        if (text is not null) _findBox.Text = text;
        string needle = _findBox.Text;
        if (needle.Length == 0) { OpenFind(); return -1; }
        int count = _lines.Count;
        int start = _list.SelectedIndex;
        int total = 0;
        foreach (var line in _lines) if (line.Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) total++;
        for (int step = 1; step <= count; step++)
        {
            int i = ((forward ? start + step : start - step) % count + count) % count;
            if (start < 0 && !forward && step == 1) i = count - 1;
            if (!_lines[i].Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            _list.SelectedIndex = i;
            _list.ScrollIntoView(_lines[i]);
            int ordinal = 0;
            for (int j = 0; j <= i; j++) if (_lines[j].Text.Contains(needle, StringComparison.OrdinalIgnoreCase)) ordinal++;
            _findStatus.Text = string.Format(CultureInfo.CurrentCulture, "line {0:N0}: match {1:N0} of {2:N0}", i + 1, ordinal, total);
            return i;
        }
        _findStatus.Text = "Not found";
        return -1;
    }

    private void Copy()
    {
        var lines = _list.SelectedItems.Count > 0 ? _list.SelectedItems.Cast<TextLine>().OrderBy(l => l.Number) : _lines.AsEnumerable();
        try { Clipboard.SetText(string.Join(Environment.NewLine, lines.Select(l => l.Text))); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy: nothing copied */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lines.Clear();
    }
}
