using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Tbl.Index;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;

namespace Cairn.Tbl.Ui.Navigation;

/// <summary>One usage row: where a name is used. <see cref="Entry"/> and <see cref="Snippet"/> fill in after the table was read.</summary>
public sealed class TblUsageRow(TblReference reference) : INotifyPropertyChanged
{
    private string _entry = string.Empty;
    private string _snippet = string.Empty;

    /// <summary>The use.</summary>
    public TblReference Reference { get; } = reference;
    public string Table => Reference.Source.FileName;
    public string Location => Reference.Source.Kind == TblSourceKind.OpenDocument ? "open" : Reference.Source.DisplayLocation;
    public int Line => Reference.Line;
    public string Field => Reference.Field;
    /// <summary>The entry the use is in.</summary>
    public string Entry { get => _entry; internal set { _entry = value; Raise(); } }
    /// <summary>The line's text.</summary>
    public string Snippet { get => _snippet; internal set { _snippet = value; Raise(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The bottom "Usages" panel: every use of a name across the indexed tables (table, entry, line, snippet); a click opens
/// that table and jumps to the use.
/// </summary>
public sealed class TblUsagesPanel : DockPanel
{
    private readonly TextBlock _header = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap };
    // A GridList, not a plain ListView: the Fluent theme's ListView style drops a GridView's header and columns.
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private CancellationTokenSource? _cts;

    public TblUsagesPanel()
    {
        AutomationProperties.SetName(this, "Usages");
        _header.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_header, Dock.Top);
        Children.Add(_header);
        _list.Column("Table", 130, nameof(TblUsageRow.Table));
        _list.Column("Entry", 170, nameof(TblUsageRow.Entry));
        _list.Column("Line", 56, nameof(TblUsageRow.Line));
        _list.Column("Field", 130, nameof(TblUsageRow.Field));
        _list.Column("Text", 420, nameof(TblUsageRow.Snippet));
        _list.Column("Location", 140, nameof(TblUsageRow.Location));
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        AutomationProperties.SetName(_list, "Usages list");
        _list.ToolTip = "Click a usage to open its table at that line";
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem { DataContext: TblUsageRow row }) Activate(row);
        };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && _list.SelectedItem is TblUsageRow row) { Activate(row); e.Handled = true; } };
        Children.Add(_list);
        _header.Text = "Place the caret on a name and use Find usages (Shift+F12).";
    }

    /// <summary>The rows shown.</summary>
    public IReadOnlyList<TblUsageRow> Rows { get; private set; } = [];
    /// <summary>The list (self-tests check its columns).</summary>
    internal ListView List => _list;
    /// <summary>The header line.</summary>
    public string Header => _header.Text;
    /// <summary>Completes when entries and snippets are filled in (tests).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>A usage was clicked.</summary>
    public event EventHandler<TblReference>? UsageActivated;

    /// <summary>Shows a plain message instead of results.</summary>
    public void ShowMessage(string message)
    {
        _cts?.Cancel();
        Rows = [];
        _list.ItemsSource = null;
        _header.Text = message;
    }

    /// <summary>Shows the uses of <paramref name="what"/>; entry names and line texts are read off the UI thread.</summary>
    /// <param name="what">"ammo '12mm'".</param>
    /// <param name="references">The uses.</param>
    /// <param name="openText">The current text of an open table by source key.</param>
    public void Show(string what, ImmutableArray<TblReference> references, Func<string, string?>? openText)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var rows = references.IsDefault ? [] : references
            .OrderBy(r => r.Source.Kind == TblSourceKind.OpenDocument ? 0 : 1)
            .ThenBy(r => r.Source.FileName, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Line)
            .Select(r => new TblUsageRow(r)).ToList();
        Rows = rows;
        _list.ItemsSource = rows;
        int tables = rows.Select(r => r.Reference.Source.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        _header.Text = rows.Count == 0 ? $"No usages of {what} in the indexed tables."
            : $"{rows.Count:N0} {(rows.Count == 1 ? "usage" : "usages")} of {what} in {tables:N0} {(tables == 1 ? "table" : "tables")}. Click one to open it.";
        Pending = FillAsync(rows, openText, cts.Token);
    }

    private async Task FillAsync(List<TblUsageRow> rows, Func<string, string?>? openText, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        // Texts of open documents are read here, on the UI thread; files and packfile entries on the pool.
        var open = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in rows.Select(r => r.Reference.Source.Key).Distinct(StringComparer.OrdinalIgnoreCase)) open[key] = openText?.Invoke(key);
        List<(TblUsageRow Row, string Entry, string Snippet)> filled;
        try
        {
            filled = await Task.Run(() =>
            {
                var result = new List<(TblUsageRow, string, string)>();
                foreach (var group in rows.GroupBy(r => r.Reference.Source.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    var source = group.First().Reference.Source;
                    string? text = open.GetValueOrDefault(source.Key) ?? TblSourceText.Read(source);
                    if (text is null) continue;
                    var doc = TblDocument.Parse(text, TblSchemaSet.Default.Find(source.FileName));
                    foreach (var row in group)
                    {
                        int at = Math.Clamp(row.Reference.Span.Start, 0, text.Length);
                        string entry = doc.EntryAt(at)?.Name ?? (doc.SectionAt(at) is { HasHeader: true } s ? "#" + s.Name : "");
                        int ls = at, le = at;
                        while (ls > 0 && text[ls - 1] != '\n' && text[ls - 1] != '\r') ls--;
                        while (le < text.Length && text[le] != '\n' && text[le] != '\r') le++;
                        result.Add((row, entry, text[ls..le].Trim()));
                    }
                }
                return result;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        foreach (var (row, entry, snippet) in filled) { row.Entry = entry; row.Snippet = snippet; }
    }

    private void Activate(TblUsageRow row) => UsageActivated?.Invoke(this, row.Reference);

    /// <summary>Stops a pending fill.</summary>
    public void Cancel() => _cts?.Cancel();
}
