using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
namespace Cairn.Previews;

/// <summary>A hex dump of the first bytes of a binary entry, with an ASCII column.</summary>
public sealed class HexPreview : UserControl
{
    /// <summary>How much of an entry the hex view shows.</summary>
    public const int MaxBytes = 4096;

    public HexPreview(byte[] head, long size, string? note = null)
    {
        var box = new TextBox
        {
            Text = Dump(head),
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Padding = new Thickness(6, 4, 6, 4),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(box, "Hex view");
        box.SetResourceReference(FontFamilyProperty, "MonoFont");
        box.SetResourceReference(BackgroundProperty, "Editor.Background");
        box.SetResourceReference(ForegroundProperty, "Editor.Foreground");
        string what = head.Length < size
            ? string.Format(CultureInfo.CurrentCulture, "First {0:N0} of {1:N0} bytes", head.Length, size)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", size);
        var bar = PreviewUi.Toolbar(PreviewUi.Secondary(note is null ? what : note + " " + what));
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(box);
        Content = dock;
    }

    /// <summary>Offset, 16 hex bytes and their printable characters per line.</summary>
    public static string Dump(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 5);
        for (int line = 0; line < bytes.Length; line += 16)
        {
            sb.Append(line.ToString("X8", CultureInfo.InvariantCulture)).Append("  ");
            for (int i = 0; i < 16; i++)
            {
                if (line + i < bytes.Length) sb.Append(bytes[line + i].ToString("X2", CultureInfo.InvariantCulture)).Append(' ');
                else sb.Append("   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            for (int i = 0; i < 16 && line + i < bytes.Length; i++)
            {
                byte b = bytes[line + i];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }
            if (line + 16 < bytes.Length) sb.AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>What the preview shows for several selected entries: how many, how big, which types.</summary>
public sealed class SelectionSummary : UserControl
{
    public SelectionSummary(IReadOnlyList<(string Name, long Size)> files)
    {
        var items = files.Select(f => (f.Name, f.Size, Extension: Path.GetExtension(f.Name))).ToList();
        long total = items.Sum(i => i.Size);
        var stack = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var head = PreviewUi.Text(string.Format(CultureInfo.CurrentCulture, "{0:N0} files selected", items.Count));
        head.FontSize = 18;
        head.FontWeight = FontWeights.SemiBold;
        stack.Children.Add(head);
        var size = PreviewUi.Secondary(string.Format(CultureInfo.CurrentCulture, "{0} in total ({1:N0} bytes)", PreviewUi.Size(total), total));
        size.Margin = new Thickness(0, 2, 0, 10);
        stack.Children.Add(size);

        var groups = items.GroupBy(i => i.Extension.Length == 0 ? "(no extension)" : i.Extension.ToLowerInvariant())
            .Select(g => (Type: g.Key, Count: g.Count(), Bytes: g.Sum(i => i.Size)))
            .OrderByDescending(g => g.Bytes).ToList();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.HorizontalAlignment = HorizontalAlignment.Left;
        int row = 0;
        foreach (var g in groups.Take(24))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var type = PreviewUi.Text(g.Type);
            type.Margin = new Thickness(0, 2, 12, 2);
            var count = PreviewUi.Secondary(string.Format(CultureInfo.CurrentCulture, "{0:N0}", g.Count));
            count.TextAlignment = TextAlignment.Right;
            count.Margin = new Thickness(0, 2, 12, 2);
            var bar = new Border { Height = 8, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center };
            bar.SetResourceReference(Border.BackgroundProperty, "App.Accent");
            double fraction = total > 0 ? (double)g.Bytes / total : 0;
            bar.Width = Math.Max(2, fraction * 160);
            var bytes = PreviewUi.Secondary(PreviewUi.Size(g.Bytes));
            Grid.SetRow(type, row); Grid.SetRow(count, row); Grid.SetRow(bar, row); Grid.SetRow(bytes, row);
            Grid.SetColumn(count, 1); Grid.SetColumn(bar, 2); Grid.SetColumn(bytes, 3);
            grid.Children.Add(type); grid.Children.Add(count); grid.Children.Add(bar); grid.Children.Add(bytes);
            row++;
        }
        stack.Children.Add(grid);
        if (groups.Count > 24) stack.Children.Add(PreviewUi.Secondary(string.Format(CultureInfo.CurrentCulture, "and {0:N0} more types", groups.Count - 24)));
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        AutomationProperties.SetName(this, "Selection summary");
    }
}

/// <summary>Hosts another module's read-only preview (meshes, clips, effects, animated textures) with "Open in Cairn".</summary>
public sealed class ModulePreviewHost : UserControl, IDisposable
{
    private FrameworkElement? _inner;

    // moduleName: the module that made the preview, or null for a plain (text, image, hex) preview
    public ModulePreviewHost(FrameworkElement inner, string name, string? moduleName, Action? openInCairn)
    {
        _inner = inner;
        var tools = new List<UIElement>();
        if (openInCairn is not null) tools.Add(PreviewUi.Button("Open in Cairn", "Open " + name + " in a Cairn tab", (_, _) => openInCairn()));
        if (moduleName is not null) tools.Add(PreviewUi.Secondary("Read-only preview from the " + moduleName + " module"));
        var bar = PreviewUi.Toolbar([.. tools]);
        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(inner);
        Content = dock;
    }

    public FrameworkElement? Inner => _inner;

    public void Dispose()
    {
        if (_inner is IDisposable d)
        {
            try { d.Dispose(); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* a provider's own teardown problem */ }
        }
        if (Content is DockPanel dock && _inner is not null) dock.Children.Remove(_inner);
        _inner = null;
    }
}
