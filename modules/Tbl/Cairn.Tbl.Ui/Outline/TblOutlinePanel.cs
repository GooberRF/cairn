using System.Collections.Immutable;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Ui.Documents;
using TblDocument = Cairn.Tbl.Model.TblDocument;

namespace Cairn.Tbl.Ui.Outline;

/// <summary>One row of the outline: a section (with its entry count, collapsible) or an entry (with its problem marker).</summary>
public sealed class TblOutlineRow
{
    internal TblOutlineRow(string label, bool isSection, int sectionIndex, int entryIndex, int start, int length)
    {
        Label = label;
        IsSection = isSection;
        SectionIndex = sectionIndex;
        EntryIndex = entryIndex;
        Start = start;
        Length = length;
    }

    /// <summary>Section header or entry name.</summary>
    public string Label { get; }
    /// <summary>True for a section row.</summary>
    public bool IsSection { get; }
    /// <summary>The section's index in the table.</summary>
    public int SectionIndex { get; }
    /// <summary>The entry's index in its section, -1 for a section row.</summary>
    public int EntryIndex { get; }
    /// <summary>Where a click jumps (the entry's name, the section's header).</summary>
    public int Start { get; }
    /// <summary>Length of the selection made by a jump.</summary>
    public int Length { get; }
    /// <summary>Entries of a section (all, or those matching the filter while filtering).</summary>
    public int Count { get; internal set; }
    /// <summary>Errors inside the entry (or section).</summary>
    public int Errors { get; internal set; }
    /// <summary>Warnings inside the entry (or section).</summary>
    public int Warnings { get; internal set; }
    /// <summary>A collapsed section hides its entries.</summary>
    public bool IsExpanded { get; internal set; } = true;

    // Bound by the row template.
    public string CountText => IsSection ? Count.ToString("N0", CultureInfo.CurrentCulture) : string.Empty;
    public string Chevron => IsSection ? (IsExpanded ? "▾" : "▸") : string.Empty;
    public Thickness Indent => IsSection ? new Thickness(0) : new Thickness(16, 0, 0, 0);
    public Visibility ErrorVisibility => Errors > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WarningVisibility => Errors == 0 && Warnings > 0 ? Visibility.Visible : Visibility.Collapsed;
    public FontWeight Weight => IsSection ? FontWeights.SemiBold : FontWeights.Normal;
    public string ToolTipText => (IsSection ? $"{Label}: {Count:N0} {(Count == 1 ? "entry" : "entries")}" : Label)
        + (Errors > 0 ? $"; {Errors} error{(Errors == 1 ? "" : "s")}" : "")
        + (Warnings > 0 ? $"; {Warnings} warning{(Warnings == 1 ? "" : "s")}" : "");

    public override string ToString() => Label;
}

/// <summary>
/// The left-pane outline of a table: sections and their entries with counts and problem markers, a filter box, the
/// entry under the caret highlighted, and a click (or Enter) jumps there. Rows are virtualised.
/// </summary>
public sealed class TblOutlinePanel : DockPanel
{
    private const string RowTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <DockPanel Margin="{Binding Indent}" ToolTip="{Binding ToolTipText}" Background="Transparent">
            <TextBlock DockPanel.Dock="Left" Text="{Binding Chevron}" Width="14" Foreground="{DynamicResource App.SecondaryText}" />
            <TextBlock DockPanel.Dock="Right" Text="{Binding CountText}" Margin="6,0,2,0" Foreground="{DynamicResource App.SecondaryText}" />
            <TextBlock DockPanel.Dock="Right" Text="&#x2716;" Margin="4,0,0,0" FontSize="10" VerticalAlignment="Center" Visibility="{Binding ErrorVisibility}" Foreground="{DynamicResource Severity.Error}" />
            <TextBlock DockPanel.Dock="Right" Text="&#x25B2;" Margin="4,0,0,0" FontSize="10" VerticalAlignment="Center" Visibility="{Binding WarningVisibility}" Foreground="{DynamicResource Severity.Warning}" />
            <TextBlock Text="{Binding Label}" FontWeight="{Binding Weight}" TextTrimming="CharacterEllipsis" />
          </DockPanel>
        </DataTemplate>
        """;

    private static DataTemplate? _template;
    private readonly TextBox _filter = new() { Margin = new Thickness(0, 0, 0, 4) };
    private readonly TextBlock _summary = new() { Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _list = new();
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);
    private List<(TblOutlineRow Section, List<TblOutlineRow> Entries)> _tree = [];
    private TblModel? _model;
    private int _caret = -1;
    private bool _syncing;

    public TblOutlinePanel()
    {
        Margin = new Thickness(6, 6, 6, 6);
        AutomationProperties.SetName(this, "Outline");

        _filter.ToolTip = "Show only entries whose name contains this text";
        AutomationProperties.SetName(_filter, "Filter entries");
        // A grey hint inside the empty box says what it is for.
        var hint = new TextBlock { Text = "Filter entries", Margin = new Thickness(6, 3, 0, 0), IsHitTestVisible = false, FontStyle = FontStyles.Italic };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        _filter.TextChanged += (_, _) => { hint.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Rebuild(); };
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _filter.Text.Length > 0) { _filter.Text = string.Empty; e.Handled = true; }
            else if (e.Key == Key.Down && _list.Items.Count > 0) { _list.SelectedIndex = Math.Max(0, _list.SelectedIndex); FocusSelected(); e.Handled = true; }
        };
        var filterBox = new Grid();
        filterBox.Children.Add(_filter);
        filterBox.Children.Add(hint);
        SetDock(filterBox, Dock.Top);
        Children.Add(filterBox);

        _summary.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        SetDock(_summary, Dock.Top);
        Children.Add(_summary);

        _template ??= (DataTemplate)XamlReader.Parse(RowTemplate);
        _list.ItemTemplate = _template;
        _list.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _list.BorderThickness = new Thickness(0);
        _list.SetResourceReference(BackgroundProperty, "App.PaneBackground");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        AutomationProperties.SetName(_list, "Sections and entries");
        _list.ToolTip = null;
        _list.PreviewMouseLeftButtonUp += OnRowClicked;
        _list.KeyDown += OnListKey;
        Children.Add(_list);
        _summary.Text = "No table.";
    }

    /// <summary>The rows shown now (sections and the entries of expanded sections, filtered).</summary>
    public IReadOnlyList<TblOutlineRow> Rows => _list.Items.Cast<TblOutlineRow>().ToList();
    /// <summary>The highlighted row (the entry under the caret), or null.</summary>
    public TblOutlineRow? Current => _list.SelectedItem as TblOutlineRow;
    /// <summary>Entries in the outline (all sections, unfiltered).</summary>
    public int EntryCount => _tree.Sum(t => t.Entries.Count);
    /// <summary>The filter text.</summary>
    public string Filter { get => _filter.Text; set => _filter.Text = value ?? string.Empty; }

    /// <summary>A row was clicked or Enter pressed: jump to (start, length).</summary>
    public event Action<int, int>? NavigateRequested;

    /// <summary>Shows <paramref name="model"/> (call after every parse).</summary>
    public void Update(TblModel? model)
    {
        _model = model;
        _tree = model is null ? [] : Build(model.Parsed, model.Diagnostics);
        Rebuild();
    }

    /// <summary>Highlights the entry (or section) that contains <paramref name="offset"/>.</summary>
    public void SetCaret(int offset)
    {
        _caret = offset;
        Highlight();
    }

    /// <summary>Jumps to <paramref name="row"/> as a click would.</summary>
    public void Activate(TblOutlineRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        NavigateRequested?.Invoke(row.Start, row.Length);
    }

    /// <summary>Collapses or expands a section row.</summary>
    public void Toggle(TblOutlineRow section)
    {
        if (!section.IsSection) return;
        if (!_collapsed.Remove(section.Label)) _collapsed.Add(section.Label);
        Rebuild();
    }

    private static List<(TblOutlineRow, List<TblOutlineRow>)> Build(TblDocument doc, ImmutableArray<TblDiagnostic> diagnostics)
    {
        var tree = new List<(TblOutlineRow, List<TblOutlineRow>)>();
        var problems = diagnostics.IsDefault ? [] : diagnostics.Where(d => d.Severity != TblSeverity.Information).ToList();
        foreach (var section in doc.Sections)
        {
            string label = section.HasHeader ? "#" + section.Name : "(no section)";
            int hs = section.HeaderSpan?.Start ?? section.Span.Start, hl = section.HeaderSpan?.Length ?? 0;
            var sectionRow = new TblOutlineRow(label, true, section.Index, -1, hs, hl);
            var entries = new List<TblOutlineRow>(section.Entries.Length);
            foreach (var entry in section.Entries)
            {
                string name = entry.Name.Length > 0 ? entry.Name : "(unnamed)";
                bool hasName = entry.NameSpan.Length > 0;
                var row = new TblOutlineRow(name, false, section.Index, entry.Index, hasName ? entry.NameSpan.Start : entry.Span.Start, hasName ? entry.NameSpan.Length : 0);
                foreach (var d in problems)
                {
                    if (d.Span.Start < entry.Span.Start) continue;
                    if (d.Span.Start >= entry.Span.End && !(entry.Span.IsEmpty && d.Span.Start == entry.Span.Start)) continue;
                    if (d.Severity == TblSeverity.Error) row.Errors++; else row.Warnings++;
                }
                entries.Add(row);
            }
            foreach (var d in problems)
                if (section.Span.ContainsInclusive(d.Span.Start) || d.Span.Start == section.Span.Start)
                {
                    if (d.Severity == TblSeverity.Error) sectionRow.Errors++; else sectionRow.Warnings++;
                }
            sectionRow.Count = entries.Count;
            tree.Add((sectionRow, entries));
        }
        return tree;
    }

    private void Rebuild()
    {
        string filter = _filter.Text.Trim();
        var rows = new List<TblOutlineRow>();
        int shown = 0;
        foreach (var (section, entries) in _tree)
        {
            var matching = filter.Length == 0 ? entries : entries.Where(e => e.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filter.Length > 0 && matching.Count == 0 && !section.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            section.Count = matching.Count;
            section.IsExpanded = filter.Length > 0 || !_collapsed.Contains(section.Label);
            rows.Add(section);
            if (section.IsExpanded) rows.AddRange(matching);
            shown += matching.Count;
        }
        _syncing = true;
        try { _list.ItemsSource = rows; }
        finally { _syncing = false; }
        int total = EntryCount;
        _summary.Text = _model is null ? "No table."
            : filter.Length > 0 ? $"{shown:N0} of {total:N0} entries match."
            : $"{_tree.Count:N0} {(_tree.Count == 1 ? "section" : "sections")}, {total:N0} {(total == 1 ? "entry" : "entries")}."
              + (_model.ErrorCount + _model.WarningCount > 0 ? $" {_model.ErrorCount:N0} errors, {_model.WarningCount:N0} warnings." : "");
        Highlight();
    }

    private void Highlight()
    {
        if (_model is null || _caret < 0) return;
        var doc = _model.Parsed;
        TblOutlineRow? target = null;
        if (doc.EntryAt(_caret) is { } entry)
            target = _list.Items.Cast<TblOutlineRow>().FirstOrDefault(r => !r.IsSection && r.SectionIndex == entry.Section.Index && r.EntryIndex == entry.Index);
        if (target is null && doc.SectionAt(_caret) is { } section)
            target = _list.Items.Cast<TblOutlineRow>().FirstOrDefault(r => r.IsSection && r.SectionIndex == section.Index);
        if (ReferenceEquals(target, _list.SelectedItem)) return;
        _syncing = true;
        try
        {
            _list.SelectedItem = target;
            if (target is not null) _list.ScrollIntoView(target);
        }
        finally { _syncing = false; }
    }

    private void OnRowClicked(object sender, MouseButtonEventArgs e)
    {
        if (_syncing) return;
        if (ItemsControl.ContainerFromElement(_list, (DependencyObject)e.OriginalSource) is not ListBoxItem { DataContext: TblOutlineRow row } item) return;
        // A click on a section's chevron folds it; anywhere else jumps.
        if (row.IsSection && e.GetPosition(item).X < 18) { Toggle(row); return; }
        Activate(row);
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (_list.SelectedItem is not TblOutlineRow row) return;
        if (e.Key == Key.Enter) { Activate(row); e.Handled = true; }
        else if (row.IsSection && (e.Key == Key.Left && row.IsExpanded || e.Key == Key.Right && !row.IsExpanded)) { Toggle(row); e.Handled = true; }
    }

    private void FocusSelected()
    {
        if (_list.ItemContainerGenerator.ContainerFromIndex(_list.SelectedIndex) is ListBoxItem item) item.Focus();
        else _list.Focus();
    }
}
