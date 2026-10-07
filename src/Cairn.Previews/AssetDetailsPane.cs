using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Previews;

/// <summary>One shown row of the details pane.</summary>
/// <param name="Section">The group heading.</param>
/// <param name="Label">What the value is.</param>
/// <param name="Value">The value.</param>
/// <param name="Flagged">Highlighted (missing file, warning).</param>
public sealed record AssetDetailRow(string Section, string Label, string Value, bool Flagged = false);

/// <summary>Everything the details pane shows for one file or selection.</summary>
/// <param name="Title">The heading (normally the file name).</param>
/// <param name="Rows">The rows, grouped by <see cref="AssetDetailRow.Section"/> in first-seen order.</param>
/// <param name="Warnings">Shown in a banner above the rows.</param>
public sealed record AssetDetails(string Title, IReadOnlyList<AssetDetailRow> Rows, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The basic rows of a file looked up by name (requested name, resolved name, where it was found, size, type),
    /// followed by <paramref name="extra"/> (the caller's facts); for a missing file, where it was looked for and a warning.
    /// </summary>
    public static AssetDetails ForLookup(AssetLookupResult lookup, IEnumerable<AssetDetailRow>? extra = null)
    {
        const string s = "File";
        var rows = new List<AssetDetailRow> { new(s, "Requested", lookup.RequestedName) };
        var warnings = new List<string>();
        if (lookup.Location is { } l)
        {
            if (l.IsSupersede) rows.Add(new(s, "Resolved as", l.ResolvedName));
            rows.Add(new(s, "Found in", l.DisplayLocation));
            if (l.ArchivePath is { } archive) rows.Add(new(s, "Packfile", archive));
            else if (l.FilePath is { } path) rows.Add(new(s, "Path", path));
            long size = lookup.Size;
            if (size >= 0) rows.Add(new(s, "Size", string.Format(CultureInfo.CurrentCulture, "{0} ({1:N0} bytes)", PreviewUi.Size(size), size)));
            string ext = Path.GetExtension(l.ResolvedName);
            rows.Add(new(s, "Type", ext.Length == 0 ? "(no extension)" : ext.ToLowerInvariant()));
        }
        else
        {
            rows.Add(new(s, "Found in", "not found", Flagged: true));
            rows.Add(new(s, "Looked in", lookup.Searched.Count == 0 ? "nowhere (no game folder or search folders are set)" : string.Join(Environment.NewLine, lookup.Searched)));
            warnings.Add($"'{lookup.RequestedName}' was not found.");
        }
        if (extra is not null) rows.AddRange(extra);
        return new AssetDetails(lookup.Location?.ResolvedName ?? lookup.RequestedName, rows, warnings);
    }
}

/// <summary>
/// Details of a file or selection as grouped rows. The rows come from the caller (a facts callback run off the UI
/// thread), so each module keeps its own facts. Values are selectable; "Copy all" copies everything as text. Long
/// groups (a level's references) start collapsed, with flagged rows counted in the heading.
/// </summary>
public class AssetDetailsPane : UserControl, IDisposable
{
    /// <summary>Groups with more rows than this start collapsed.</summary>
    private const int CollapseAbove = 12;
    /// <summary>Most rows built per group (the rest are in "Copy all").</summary>
    private const int MaxRowsPerGroup = 400;

    private readonly ScrollViewer _scroll;
    private readonly StackPanel _body = new() { Margin = new Thickness(10, 6, 10, 10) };
    private CancellationTokenSource? _cts;
    private int _generation;
    private bool _disposed;
    private string _copyText = "";

    public AssetDetailsPane(IShellContext? shell)
    {
        Shell = shell;
        Grid.SetIsSharedSizeScope(_body, true);
        _scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
        Content = _scroll;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        AutomationProperties.SetName(this, "Details");
        ShowMessage("Select a file to see its details.");
    }

    /// <summary>The shell (game data for facts), or null.</summary>
    protected IShellContext? Shell { get; }

    /// <summary>True once disposed.</summary>
    protected bool IsDisposed => _disposed;

    /// <summary>The name the background work is reported under (<see cref="BusyTracker"/>).</summary>
    public string BusyLabel { get; set; } = "asset details";

    /// <summary>The rows shown now (for tests and "Copy all").</summary>
    public IReadOnlyList<AssetDetailRow> Rows { get; private set; } = [];

    /// <summary>The warnings shown now.</summary>
    public IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>The load in progress, or a completed task.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>Everything shown, as text.</summary>
    public string CopyText => _copyText;

    /// <summary>Builds the details on a pool thread and shows them; a newer call wins (callers debounce).</summary>
    /// <param name="build">The facts callback (pool thread; throw <see cref="OperationCanceledException"/> when cancelled).</param>
    public void Show(Func<CancellationToken, AssetDetails> build)
    {
        if (_disposed) return;
        var ct = Begin();
        Pending = LoadAsync(build, _generation, ct);
    }

    /// <summary>Shows <paramref name="details"/> now (cancels a load).</summary>
    public void ShowDetails(AssetDetails details)
    {
        if (_disposed) return;
        Begin();
        Render(details);
    }

    /// <summary>Shows only a hint (cancels a load).</summary>
    public void ShowMessage(string text)
    {
        if (_disposed) return;
        Begin();
        Rows = [];
        Warnings = [];
        _copyText = "";
        _body.Children.Clear();
        var hint = PreviewUi.Text(text, "HintText");
        hint.Margin = new Thickness(0, 12, 0, 0);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        _body.Children.Add(hint);
    }

    private CancellationToken Begin()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _generation++;
        Pending = Task.CompletedTask;
        return cts.Token;
    }

    private async Task LoadAsync(Func<CancellationToken, AssetDetails> build, int generation, CancellationToken ct)
    {
        AssetDetails details;
        try
        {
            using (BusyTracker.Begin(BusyLabel))
                details = await Task.Run(() => build(ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_disposed || generation != _generation) return;
        Render(details);
    }

    private void Render(AssetDetails details)
    {
        var rows = details.Rows;
        var warnings = details.Warnings;
        Rows = rows;
        Warnings = warnings;
        _copyText = BuildCopyText(details.Title, rows, warnings);
        _body.Children.Clear();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var copy = PreviewUi.Button("Copy all", "Copy every detail as text", (_, _) => CopyAll());
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);
        var name = ValueBox(details.Title);
        name.FontSize = 14;
        name.FontWeight = FontWeights.SemiBold;
        AutomationProperties.SetName(name, "Name");
        head.Children.Add(name);
        _body.Children.Add(head);

        if (warnings.Count > 0)
        {
            var list = new StackPanel();
            foreach (var w in warnings.Distinct().Take(20))
            {
                var t = ValueBox("⚠ " + w);
                t.SetResourceReference(ForegroundProperty, "Banner.Foreground");
                list.Children.Add(t);
            }
            var banner = new Border { Child = list, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 0, 8) };
            banner.SetResourceReference(Border.BackgroundProperty, "Banner.NoticeBackground");
            banner.SetResourceReference(Border.BorderBrushProperty, "Banner.NoticeBorder");
            AutomationProperties.SetName(banner, "Warnings");
            _body.Children.Add(banner);
        }

        foreach (var group in rows.GroupBy(r => r.Section))
        {
            var groupRows = group.ToList();
            int flagged = groupRows.Count(r => r.Flagged);
            _body.Children.Add(BuildGroup(group.Key, groupRows, flagged, collapsed: groupRows.Count > CollapseAbove));
        }
    }

    private static FrameworkElement BuildGroup(string section, List<AssetDetailRow> rows, int flagged, bool collapsed)
    {
        var grid = new Grid { Margin = new Thickness(18, 0, 0, 6) };
        // One label width across the groups (the body is the shared size scope).
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto), MaxWidth = 220, SharedSizeGroup = "FactLabel" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var row in rows.Take(MaxRowsPerGroup))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = PreviewUi.Text(row.Label, "FactLabel");
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.VerticalAlignment = VerticalAlignment.Top;
            label.ToolTip = row.Label;
            var value = ValueBox(row.Value);
            AutomationProperties.SetName(value, row.Label);
            if (row.Flagged)
            {
                value.SetResourceReference(ForegroundProperty, "Severity.Warning");
                label.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
            }
            Grid.SetRow(label, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
            r++;
        }
        if (rows.Count > MaxRowsPerGroup)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var more = PreviewUi.Text(string.Format(CultureInfo.CurrentCulture, "and {0:N0} more (Copy all includes them)", rows.Count - MaxRowsPerGroup), "HintText");
            Grid.SetRow(more, r);
            Grid.SetColumnSpan(more, 2);
            grid.Children.Add(more);
        }

        var toggle = new ToggleButton { IsChecked = !collapsed, VerticalAlignment = VerticalAlignment.Center };
        toggle.SetResourceReference(StyleProperty, "TreeExpanderToggle");
        var header = PreviewUi.Text(section.ToUpper(CultureInfo.CurrentCulture), "PaneHeaderText");
        header.Margin = new Thickness(2, 0, 0, 0);
        var headPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 2), Background = System.Windows.Media.Brushes.Transparent, Focusable = true, Cursor = Cursors.Hand };
        headPanel.SetResourceReference(FocusVisualStyleProperty, "FocusRing");
        headPanel.Children.Add(toggle);
        headPanel.Children.Add(header);
        if (rows.Count > CollapseAbove || !(toggle.IsChecked ?? false))
            headPanel.Children.Add(PreviewUi.Secondary(string.Format(CultureInfo.CurrentCulture, "{0:N0}", rows.Count)));
        if (flagged > 0)
        {
            var marker = PreviewUi.Secondary(string.Format(CultureInfo.CurrentCulture, "{0:N0} flagged", flagged));
            marker.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
            marker.Margin = new Thickness(8, 0, 0, 0);
            headPanel.Children.Add(marker);
        }
        AutomationProperties.SetName(headPanel, section);
        headPanel.ToolTip = "Show or hide " + section;
        grid.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        toggle.Checked += (_, _) => grid.Visibility = Visibility.Visible;
        toggle.Unchecked += (_, _) => grid.Visibility = Visibility.Collapsed;
        headPanel.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not ToggleButton) toggle.IsChecked = !(toggle.IsChecked ?? false); };
        headPanel.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space) { toggle.IsChecked = !(toggle.IsChecked ?? false); e.Handled = true; }
        };
        var panel = new StackPanel();
        panel.Children.Add(headPanel);
        panel.Children.Add(grid);
        return panel;
    }

    private static TextBox ValueBox(string text)
    {
        var box = new TextBox { Text = text };
        box.SetResourceReference(StyleProperty, "FactValue");
        return box;
    }

    private static string BuildCopyText(string title, IReadOnlyList<AssetDetailRow> rows, IReadOnlyList<string> warnings)
    {
        var sb = new StringBuilder();
        sb.AppendLine(title);
        foreach (var w in warnings.Distinct()) sb.Append("Warning: ").AppendLine(w);
        foreach (var group in rows.GroupBy(r => r.Section))
        {
            sb.AppendLine().AppendLine(group.Key);
            foreach (var row in group) sb.Append("  ").Append(row.Label).Append(": ").AppendLine(row.Value.Replace(Environment.NewLine, Environment.NewLine + "    "));
        }
        return sb.ToString();
    }

    private void CopyAll()
    {
        try { Clipboard.SetText(_copyText); }
        catch (System.Runtime.InteropServices.ExternalException) { /* clipboard busy */ }
    }

    /// <summary>Cancels a load and releases the rows.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        _generation++;
        _body.Children.Clear();
        GC.SuppressFinalize(this);
    }
}
