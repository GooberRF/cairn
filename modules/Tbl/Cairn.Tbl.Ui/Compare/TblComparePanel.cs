using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Tbl.Compare;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Cairn.Tbl.Ui.Documents;
using TblDocument = Cairn.Tbl.Model.TblDocument;
using Cairn.Tbl.Ui.Navigation;
using Cairn.Tbl.Ui.References;

namespace Cairn.Tbl.Ui.Compare;

/// <summary>One row of the comparison: an entry change, a field change under it, or (with "Only changes" off) an unchanged entry.</summary>
public sealed class TblCompareRow
{
    internal TblCompareRow(string glyph, string kind, string section, string entry, string field, string stock, string modded, TextSpan? moddedSpan, TextSpan? stockSpan, bool isField)
    {
        Glyph = glyph; Kind = kind; Section = section; Entry = entry; Field = field; Stock = stock; Modded = modded;
        ModdedSpan = moddedSpan; StockSpan = stockSpan; IsField = isField;
    }

    public string Glyph { get; }
    /// <summary>"added", "removed", "changed", "same".</summary>
    public string Kind { get; }
    public string Section { get; }
    public string Entry { get; }
    public string Field { get; }
    public string Stock { get; }
    public string Modded { get; }
    public TextSpan? ModdedSpan { get; }
    public TextSpan? StockSpan { get; }
    public bool IsField { get; }
    public string EntryText => IsField ? "    " + Entry : Entry;
    public override string ToString() => $"{Kind} {Entry} {Field}".Trim();
}

/// <summary>
/// The bottom "Compare" panel: the stock table of the same name from the game data against this document, listing added,
/// removed and changed entries with per-field changes. A click jumps to the place in the document (a removed entry shows
/// its stock text beside the list). Live while shown: every new parse is compared again.
/// </summary>
public sealed class TblComparePanel : DockPanel
{
    // Stock tables by file name: the game's packfiles do not change while Cairn runs (a new game folder clears it).
    private static readonly ConcurrentDictionary<string, (string Text, AssetLocation Location)?> StockCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _cacheGameDirectory;

    private readonly Func<string?> _gameDirectory;
    private readonly TextBlock _header = new() { Margin = new Thickness(8, 4, 8, 4), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _onlyChanges = new() { Content = "Only changes", IsChecked = true, Margin = new Thickness(8, 4, 4, 4), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _refresh = new() { Content = "Compare again", Margin = new Thickness(4, 2, 8, 2), Padding = new Thickness(8, 1, 8, 1) };
    // A GridList, not a plain ListView: the Fluent theme's ListView style drops a GridView's header and columns.
    private readonly Cairn.Ui.Controls.GridList _list = new();
    private readonly TblSnippetView _stockView = new();
    private readonly TextBlock _stockHeader = new() { Margin = new Thickness(6, 4, 6, 2), TextWrapping = TextWrapping.Wrap };
    private readonly DockPanel _stockPanel = new();
    private readonly ColumnDefinition _stockColumn = new() { Width = new GridLength(0) };
    private CancellationTokenSource? _cts;
    private TblModel? _model;
    private string? _fileName;
    private (string Text, AssetLocation Location, TblDocument Parsed, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan> Classes)? _stock;

    /// <param name="gameDirectory">The game folder now (settings), read when a comparison starts.</param>
    public TblComparePanel(Func<string?> gameDirectory)
    {
        _gameDirectory = gameDirectory ?? throw new ArgumentNullException(nameof(gameDirectory));
        AutomationProperties.SetName(this, "Compare with stock");

        var bar = new DockPanel();
        bar.SetResourceReference(BackgroundProperty, "App.ChromeBackground");
        _onlyChanges.ToolTip = "List only added, removed and changed entries; untick to list every entry";
        AutomationProperties.SetName(_onlyChanges, "Only changes");
        _onlyChanges.Click += (_, _) => Render();
        _refresh.ToolTip = "Look for the stock table again and compare";
        AutomationProperties.SetName(_refresh, "Compare again");
        _refresh.Click += (_, _) => { if (_fileName is { } f && _model is { } m) { _stock = null; StockCache.TryRemove(f, out _); Run(f, m); } };
        SetDock(_refresh, Dock.Right);
        SetDock(_onlyChanges, Dock.Right);
        bar.Children.Add(_refresh);
        bar.Children.Add(_onlyChanges);
        bar.Children.Add(_header);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        _list.Column("", 28, nameof(TblCompareRow.Glyph));
        _list.Column("Change", 70, nameof(TblCompareRow.Kind));
        _list.Column("Section", 140, nameof(TblCompareRow.Section));
        _list.Column("Entry", 170, nameof(TblCompareRow.EntryText));
        _list.Column("Field", 150, nameof(TblCompareRow.Field));
        _list.Column("Stock", 200, nameof(TblCompareRow.Stock));
        _list.Column("This table", 200, nameof(TblCompareRow.Modded));
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        AutomationProperties.SetName(_list, "Changes");
        _list.ToolTip = "Click a change to go to it (a removed entry shows its stock text)";
        _list.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is ListViewItem { DataContext: TblCompareRow row }) Activate(row);
        };
        _list.KeyDown += (_, e) => { if (e.Key == Key.Enter && _list.SelectedItem is TblCompareRow row) { Activate(row); e.Handled = true; } };

        _stockHeader.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_stockHeader, Dock.Top);
        _stockPanel.Children.Add(_stockHeader);
        _stockPanel.Children.Add(_stockView);
        AutomationProperties.SetName(_stockView, "Stock text");

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(_stockColumn);
        Grid.SetColumn(_list, 0);
        Grid.SetColumn(_stockPanel, 1);
        body.Children.Add(_list);
        body.Children.Add(_stockPanel);
        Children.Add(body);
        _header.Text = "Use Table > Compare with stock to compare this table with the game's own copy.";
    }

    /// <summary>True once a comparison was asked for (later parses compare again).</summary>
    public bool IsActive => _fileName is not null;
    /// <summary>The last comparison, or null (no stock table, not run yet).</summary>
    public TblComparison? Comparison { get; private set; }
    /// <summary>The rows shown.</summary>
    public IReadOnlyList<TblCompareRow> Rows => _list.Items.Cast<TblCompareRow>().ToList();
    /// <summary>The list (self-tests check its columns).</summary>
    internal ListView List => _list;
    /// <summary>The header line.</summary>
    public string Header => _header.Text;
    /// <summary>"Only changes".</summary>
    public bool OnlyChanges { get => _onlyChanges.IsChecked == true; set { _onlyChanges.IsChecked = value; Render(); } }
    /// <summary>The stock text shown beside the list (removed entries), or empty.</summary>
    public string StockText => _stockColumn.Width.Value > 0 ? _stockView.Text : string.Empty;
    /// <summary>Completes when the current comparison is shown (tests).</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>A change in this document was clicked: jump to (start, length).</summary>
    public event Action<int, int>? NavigateRequested;

    /// <summary>Compares <paramref name="model"/> with the stock table named <paramref name="fileName"/> and keeps comparing on <see cref="Update"/>.</summary>
    public void Run(string fileName, TblModel model)
    {
        _fileName = Path.GetFileName(fileName);
        _model = model;
        Compare();
    }

    /// <summary>The document was parsed again: compare again when active.</summary>
    public void Update(TblModel model)
    {
        _model = model;
        if (IsActive) Compare();
    }

    private void Compare()
    {
        if (_fileName is not { } fileName || _model is not { } model) return;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        string? gameDir = _gameDirectory();
        var stock = _stock;
        if (stock is null) _header.Text = $"Looking for the stock {fileName} in the game data...";
        Pending = CompareAsync(fileName, model, gameDir, stock, cts.Token);
    }

    private async Task CompareAsync(string fileName, TblModel model, string? gameDir, (string, AssetLocation, TblDocument, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan>)? stock, CancellationToken ct)
    {
        (TblComparison? Result, (string, AssetLocation, TblDocument, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan>)? Stock) outcome;
        try
        {
            outcome = await Task.Run(() =>
            {
                if (stock is null)
                {
                    if (!string.Equals(_cacheGameDirectory, gameDir, StringComparison.OrdinalIgnoreCase)) { StockCache.Clear(); _cacheGameDirectory = gameDir; }
                    var found = StockCache.GetOrAdd(fileName, f => TblCompare.FindStock(gameDir, f));
                    if (found is not { } f2) return ((TblComparison?)null, ((string, AssetLocation, TblDocument, System.Collections.Immutable.ImmutableArray<TblClassifiedSpan>)?)null);
                    var parsed = TblDocument.Parse(f2.Text, TblSchemaSet.Default.Find(fileName));
                    stock = (f2.Text, f2.Location, parsed, TblClassifier.Classify(parsed));
                }
                ct.ThrowIfCancellationRequested();
                return (TblCompare.Compare(model.Parsed, stock.Value.Item3), stock);
            }, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested) return;
        _stock = outcome.Stock;
        Comparison = outcome.Result;
        if (outcome.Stock is null)
        {
            _header.Text = string.IsNullOrWhiteSpace(gameDir)
                ? $"No game folder is set, so there is no stock {fileName} to compare with (Settings > Game data)."
                : $"There is no stock table named {fileName} in the game data: this looks like a custom table.";
            _list.ItemsSource = null;
            HideStock();
            return;
        }
        Render();
    }

    private void Render()
    {
        if (Comparison is not { } c || _stock is not { } stock || _model is not { } model) return;
        var rows = new List<TblCompareRow>();
        var changedNames = new HashSet<(string, string)>();
        foreach (var e in c.Changes)
        {
            changedNames.Add((e.Section, e.Name));
            string kind = e.Kind switch { TblChangeKind.Added => "added", TblChangeKind.Removed => "removed", _ => "changed" };
            string glyph = e.Kind switch { TblChangeKind.Added => "+", TblChangeKind.Removed => "−", _ => "~" };
            rows.Add(new TblCompareRow(glyph, kind, Section(e.Section), e.Name, e.Kind == TblChangeKind.Changed ? $"{e.Fields.Length} field{(e.Fields.Length == 1 ? "" : "s")}" : "", "", "", e.ModdedSpan, e.StockSpan, false));
            if (e.Kind != TblChangeKind.Changed) continue;
            foreach (var f in e.Fields)
            {
                string fk = f.Kind switch { TblChangeKind.Added => "added", TblChangeKind.Removed => "removed", _ => "changed" };
                string fg = f.Kind switch { TblChangeKind.Added => "+", TblChangeKind.Removed => "−", _ => "~" };
                rows.Add(new TblCompareRow(fg, fk, Section(e.Section), e.Name, f.Path, f.StockValue ?? "", f.ModdedValue ?? "", f.ModdedSpan ?? e.ModdedSpan, f.StockSpan ?? e.StockSpan, true));
            }
        }
        if (!OnlyChanges)
        {
            // Unchanged entries in document order, changes kept where they are.
            var all = new List<TblCompareRow>();
            foreach (var section in model.Parsed.Sections)
                foreach (var entry in section.Entries)
                    if (!changedNames.Contains((section.Name, entry.Name)))
                        all.Add(new TblCompareRow("", "same", Section(section.Name), entry.Name, "", "", "", entry.NameSpan.Length > 0 ? entry.NameSpan : entry.Span, null, false));
            rows = all.Concat(rows).OrderBy(r => r.ModdedSpan?.Start ?? int.MaxValue).ToList();
        }
        _list.ItemsSource = rows;
        _header.Text = c.IsIdentical
            ? $"Identical to the stock {stock.Location.ResolvedName} in {stock.Location.DisplayLocation} (entries and values)."
            : $"Against the stock {stock.Location.ResolvedName} in {stock.Location.DisplayLocation}: {c.Added:N0} added, {c.Removed:N0} removed, {c.Changed:N0} changed {(c.Changed == 1 ? "entry" : "entries")}.";
    }

    private static string Section(string name) => name.Length == 0 ? "" : "#" + name;

    /// <summary>Jumps to a change (or shows the stock text of a removed one), as a click would.</summary>
    public void Activate(TblCompareRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (row.ModdedSpan is { } span && row.Kind != "removed")
        {
            if (row.StockSpan is { } s && row.IsField) ShowStock(s, $"Stock: {row.Entry}, {row.Field}"); else HideStock();
            NavigateRequested?.Invoke(span.Start, span.Length);
        }
        else if (row.StockSpan is { } stockSpan) ShowStock(stockSpan, $"Removed from this table; the stock {row.Entry}:");
    }

    private void ShowStock(TextSpan span, string title)
    {
        if (_stock is not { } stock) return;
        // The whole stock entry for a removed entry; the stock line(s) of a field.
        var entry = stock.Parsed.EntryAt(span.Start);
        int end = entry is not null && entry.Span.Contains(span.Start) ? entry.Span.End : span.End;
        var from = entry is not null && entry.Span.Start <= span.Start ? entry.Span : span;
        var (text, offset) = TblSourceText.Snippet(stock.Text, from, 60, end);
        _stockHeader.Text = title;
        _stockView.Show(text, stock.Classes, offset);
        _stockColumn.Width = new GridLength(1, GridUnitType.Star);
    }

    private void HideStock() => _stockColumn.Width = new GridLength(0);

    /// <summary>Stops a pending comparison.</summary>
    public void Cancel() => _cts?.Cancel();
}
