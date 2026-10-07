using System.Collections;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui.List;

/// <summary>
/// The packfile's file list: a virtualised, recycling <see cref="ListView"/> with sortable Name / Type / Size / State /
/// Info columns (Info stretches to the right edge), extended selection, inline rename (F2), the entry context menu, drag-out to Explorer (entries are
/// extracted to a staging folder first) and drop-in of files and folders (handled by the document view).
/// </summary>
public sealed class VppFileListView : Grid
{
    private sealed class EntryListView : ListView
    {
        public void SelectAllOf(IEnumerable items) => SetSelectedItems(items);
    }

    private readonly VppDocument _doc;
    private readonly EntryListView _list = new();
    private readonly TextBlock _empty = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Dictionary<GridViewColumn, (VppListSort Sort, string Title)> _columns = [];
    private readonly ContextMenu _menu = new();
    private readonly GridViewColumn _infoColumn;
    private ScrollViewer? _scroller;
    private bool _stretching;
    private bool _syncing;
    private Point _dragStart;
    private bool _dragCandidate;

    public VppFileListView(VppDocument doc)
    {
        _doc = doc;
        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderThickness = new Thickness(0);
        _list.SetResourceReference(Control.BackgroundProperty, "App.PaneBackground");
        _list.SetResourceReference(Control.ForegroundProperty, "App.Text");
        _list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "Vpp.ListViewItem");
        AutomationProperties.SetName(_list, "Packfile entries");
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetIsContainerVirtualizable(_list, true);
        ScrollViewer.SetCanContentScroll(_list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Auto);

        var grid = new GridView { AllowsColumnReorder = false };
        grid.ColumnHeaderContainerStyle = Application.Current?.TryFindResource("Vpp.ColumnHeader") as Style;
        // narrow marker column: a warning glyph on entries with warnings or errors (tooltip = the problems)
        grid.Columns.Add(new GridViewColumn { Header = string.Empty, Width = 26, CellTemplate = Application.Current?.TryFindResource("Vpp.ProblemCell") as DataTemplate });
        AddColumn(grid, "Name", VppListSort.Name, 300, "Vpp.NameCell");
        AddColumn(grid, "Type", VppListSort.Type, 170, "Vpp.TypeCell");
        AddColumn(grid, "Size", VppListSort.Size, 90, "Vpp.SizeCell");
        AddColumn(grid, "State", VppListSort.State, 110, "Vpp.StateCell");
        // Info last, stretched over whatever width the other columns leave (see StretchInfo); none with --vpp-info off
        _infoColumn = new GridViewColumn { Header = "Info", Width = 240, CellTemplate = Application.Current?.TryFindResource("Vpp.InfoCell") as DataTemplate };
        if (doc.List.InfoCache is not null)
        {
            _columns[_infoColumn] = (VppListSort.Info, "Info");
            grid.Columns.Add(_infoColumn);
        }
        foreach (var column in grid.Columns.Where(c => c != _infoColumn))
            ((System.ComponentModel.INotifyPropertyChanged)column).PropertyChanged += (_, e) => { if (e.PropertyName is nameof(GridViewColumn.Width) or nameof(GridViewColumn.ActualWidth)) StretchInfo(); };
        _list.View = grid;
        _list.SizeChanged += (_, _) => StretchInfo();
        _list.Loaded += (_, _) =>
        {
            if (_scroller is null && FindScroller(_list) is { } scroller)
            {
                _scroller = scroller;
                scroller.ScrollChanged += (_, e) => { if (e.ViewportWidthChange != 0) StretchInfo(); };
            }
            StretchInfo();
        };
        _list.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));

        _empty.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        Children.Add(_list);
        Children.Add(_empty);

        _list.SelectionChanged += (_, _) => PushSelection();
        _list.MouseDoubleClick += (_, e) => { if (RowAt(e.OriginalSource) is not null) _doc.Commands.Fire(_doc.Commands.OpenSelectedAsync); };
        _list.PreviewKeyDown += OnPreviewKeyDown;
        _list.AddHandler(UIElement.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnEditorLostFocus));
        _list.PreviewMouseLeftButtonDown += OnPreviewMouseDown;
        _list.PreviewMouseMove += OnPreviewMouseMove;
        BuildContextMenu();
        _list.ContextMenu = _menu;
        _list.ContextMenuOpening += (_, _) => UpdateContextMenu();

        doc.List.VisibleChanged += (_, _) => Reload();
        doc.SelectNamesHandler = SelectNames;
        doc.BeginRenameHandler = BeginRename;
        Reload();
    }

    /// <summary>The list control (keyboard focus target, self-tests).</summary>
    public ListView List => _list;

    /// <summary>The Info column (last; it takes the width the other columns leave).</summary>
    internal GridViewColumn InfoColumn => _infoColumn;

    private void AddColumn(GridView grid, string title, VppListSort sort, double width, string templateKey)
    {
        var column = new GridViewColumn { Header = title, Width = width, CellTemplate = Application.Current?.TryFindResource(templateKey) as DataTemplate };
        _columns[column] = (sort, title);
        grid.Columns.Add(column);
    }

    /// <summary>
    /// Diagnostic: sets the named columns' widths (title, case-insensitive; "Info" ignored, it takes what is left), for
    /// captures at a window size where the default widths would push the Info column out of view. Not remembered.
    /// </summary>
    internal void SetColumnWidths(IReadOnlyDictionary<string, double> widths)
    {
        foreach (var (column, (_, title)) in _columns)
            if (column != _infoColumn && widths.FirstOrDefault(w => string.Equals(w.Key, title, StringComparison.OrdinalIgnoreCase)) is { Key: not null } match)
                column.Width = Math.Max(24, match.Value);
        StretchInfo();
    }

    private const double InfoMinWidth = 160;

    /// <summary>Widens or narrows the Info column to end at the list's right edge (never below <see cref="InfoMinWidth"/>; then the list scrolls sideways).</summary>
    private void StretchInfo()
    {
        if (_stretching || _list.View is not GridView grid || !grid.Columns.Contains(_infoColumn)) return;
        double viewport = _scroller is { ViewportWidth: > 0 } s ? s.ViewportWidth : _list.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        if (viewport <= 0) return;
        double others = 0;
        foreach (var column in grid.Columns)
            if (column != _infoColumn) others += double.IsNaN(column.Width) ? column.ActualWidth : column.Width;
        // a few pixels for the row border, so the rows never need a horizontal scroll bar just for the Info column
        double width = Math.Max(InfoMinWidth, Math.Floor(viewport - others - 6));
        if (Math.Abs(_infoColumn.Width - width) < 1) return;
        _stretching = true;
        try { _infoColumn.Width = width; }
        finally { _stretching = false; }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    // ---- rows and selection ----------------------------------------------------------------------------------

    private void Reload()
    {
        _syncing = true;
        try
        {
            var visible = _doc.List.Visible;
            _list.ItemsSource = visible;
            var names = _doc.SelectedItems.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Count > 0) _list.SelectAllOf(visible.Where(r => names.Contains(r.Name)).ToList());
        }
        finally { _syncing = false; }
        _empty.Text = _doc.List.AllRows.Count == 0 ? "This packfile is empty.\nAdd files with the toolbar or the Packfile menu, or drop files and folders here."
            : _doc.List.Visible.Count == 0 ? "No entries match the filter." : string.Empty;
        _empty.Visibility = _empty.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var (column, (sort, title)) in _columns)
            column.Header = _doc.List.Sort == sort ? title + (_doc.List.Descending ? "  ▼" : "  ▲") : title;
    }

    private void PushSelection()
    {
        if (_syncing) return;
        // list order, not click order, so the preview and commands see a stable sequence
        var selected = new HashSet<object>(_list.SelectedItems.Cast<object>());
        _doc.SetSelection([.. _doc.List.Visible.Where(selected.Contains).Select(r => r.Item)]);
    }

    private void SelectNames(IReadOnlyCollection<string> names)
    {
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = _doc.List.Visible.Where(r => set.Contains(r.Name)).ToList();
        _syncing = true;
        try { _list.SelectAllOf(rows); }
        finally { _syncing = false; }
        PushSelection();
        if (rows.Count > 0)
        {
            _list.ScrollIntoView(rows[0]);
            if (_list.IsKeyboardFocusWithin) (_list.ItemContainerGenerator.ContainerFromItem(rows[0]) as ListViewItem)?.Focus();
        }
    }

    /// <summary>Selects every row shown (Ctrl+A).</summary>
    public void SelectAllShown()
    {
        _list.SelectAll();
        PushSelection();
    }

    private void OnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } column } && _columns.TryGetValue(column, out var info))
        {
            _doc.List.SortBy(info.Sort);
            e.Handled = true;
        }
    }

    private static VppEntryRow? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListViewItem item) return item.DataContext as VppEntryRow;
        return null;
    }

    // ---- inline rename ---------------------------------------------------------------------------------------

    private void BeginRename(VppItem item)
    {
        var row = _doc.List.Visible.FirstOrDefault(r => string.Equals(r.Name, item.Name, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        foreach (var other in _doc.List.Visible.Where(r => r.IsEditing)) other.IsEditing = false;
        _list.ScrollIntoView(row);
        row.EditText = row.Name;
        row.IsEditing = true;
        Dispatcher.InvokeAsync(() =>
        {
            if (_list.ItemContainerGenerator.ContainerFromItem(row) is ListViewItem container && FindEditor(container) is { } editor)
            {
                editor.Focus();
                editor.Select(0, Path.GetFileNameWithoutExtension(row.Name).Length);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBox? FindEditor(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Name: "Editor" } tb) return tb;
            if (FindEditor(child) is { } found) return found;
        }
        return null;
    }

    private void EndRename(VppEntryRow row, bool commit)
    {
        if (!row.IsEditing) return;
        row.IsEditing = false;
        if (commit) _doc.Commands.Rename(row.Name, row.EditText);
        _list.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox { DataContext: VppEntryRow { IsEditing: true } row })
        {
            if (e.Key == Key.Enter) { EndRename(row, commit: true); e.Handled = true; }
            else if (e.Key == Key.Escape) { EndRename(row, commit: false); e.Handled = true; }
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.OriginalSource is TextBox { DataContext: VppEntryRow { IsEditing: true } row }) EndRename(row, commit: true);
    }

    // ---- context menu ----------------------------------------------------------------------------------------

    private MenuItem? _open, _openWith, _openInCairn, _extractTo, _extractHere, _copyName, _rename, _renameToFit, _replace, _remove, _selectType;

    private void BuildContextMenu()
    {
        var c = _doc.Commands;
        MenuItem Item(string header, string? gesture, string tip, Action action)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, ToolTip = tip };
            item.Click += (_, _) => action();
            _menu.Items.Add(item);
            return item;
        }
        _open = Item("_Open", "Enter", "Open in the program Windows uses for this type (from a work copy)", () => c.Fire(c.OpenSelectedAsync));
        _openWith = Item("Open _with...", null, "Choose the program to open it with", () => c.Fire(c.OpenWithAsync));
        _openInCairn = Item("Open in _Cairn", null, "Open it in a Cairn tab; saving that tab offers to update the packfile", () => c.Fire(c.OpenInCairnAsync));
        _menu.Items.Add(new Separator());
        _extractTo = Item("_Extract to...", "Ctrl+E", "Write the selected entries to a folder", () => c.Fire(c.ExtractSelectedToAsync));
        _extractHere = Item("Extract _here", null, "Write the selected entries next to the packfile", () => c.Fire(c.ExtractHereAsync));
        _copyName = Item("Copy _name", null, "Copy the selected entries' names as text", c.CopyNames);
        _menu.Items.Add(new Separator());
        _rename = Item("Re_name", "F2", "Rename the entry", c.BeginRename);
        _renameToFit = Item("Rename to _fit...", null, "Shorten the selected names the game cannot use (longer than 31 characters) to 31 characters, as one undo step",
            () => c.RenameToFit());
        _replace = Item("Re_place...", null, "Replace the entry's data with a file", c.ReplaceSelected);
        // DDS converter (Conversion/): enabled when the selection holds a TGA/PNG/JPG image.
        var toDds = Item("Con_vert to DDS...", "Ctrl+Shift+D", "Convert the selected TGA/PNG/JPG images to DDS for Alpine Faction (other entries are skipped)",
            () => c.Fire(() => _doc.Module.ConvertToDdsAsync(_doc)));
        _menu.Opened += (_, _) => toDds.IsEnabled = !_doc.IsBusy && _doc.SelectedItems.Any(i => Conversion.DdsConversion.IsConvertible(i.Name));
        _remove = Item("_Remove", "Del", "Remove the selected entries (undoable)", c.RemoveSelected);
        _menu.Items.Add(new Separator());
        _selectType = Item("Select all of this _type", null, "Select every entry with the same extension", c.SelectAllOfType);
    }

    private void UpdateContextMenu()
    {
        var c = _doc.Commands;
        foreach (var item in new[] { _open, _extractTo, _copyName, _remove, _selectType }) item!.IsEnabled = c.HasSelection;
        foreach (var item in new[] { _openWith, _rename, _replace }) item!.IsEnabled = c.HasSingleSelection;
        _openInCairn!.IsEnabled = c.CanOpenInCairn;
        _renameToFit!.IsEnabled = c.CanRenameToFit;
        _renameToFit.Visibility = c.CanRenameToFit ? Visibility.Visible : Visibility.Collapsed;
        _extractHere!.IsEnabled = c.HasSelection && _doc.Folder is not null;
        _extractHere.ToolTip = _doc.Folder is { } f ? "Write the selected entries to " + f : "Save the packfile first: it has no folder yet";
    }

    // ---- drag out --------------------------------------------------------------------------------------------

    /// <summary>True while entries are being dragged out (the document view ignores its own drop).</summary>
    public bool IsDraggingOut { get; private set; }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_list);
        var row = RowAt(e.OriginalSource);
        _dragCandidate = row is { IsEditing: false } && _list.SelectedItems.Contains(row);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed || IsDraggingOut || _doc.IsBusy) return;
        var delta = e.GetPosition(_list) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragCandidate = false;
        DragOut();
    }

    private void DragOut()
    {
        IsDraggingOut = true;
        try
        {
            // The files must exist before Explorer asks for them: extract to a staging folder first (progress bar
            // for large selections; the window stays live), then hand Explorer the paths.
            var stage = _doc.Commands.StageSelectionAsync();
            VppDocument.WaitPumping(stage, Dispatcher);
            if (stage.Status != System.Threading.Tasks.TaskStatus.RanToCompletion || stage.Result is not { Count: > 0 } files) return;
            if (Mouse.LeftButton != MouseButtonState.Pressed) { _doc.ShowStatus("Drag cancelled: the button was released while the files were being extracted"); return; }
            var data = new DataObject(DataFormats.FileDrop, files.ToArray());
            DragDrop.DoDragDrop(_list, data, DragDropEffects.Copy);
        }
        finally { IsDraggingOut = false; }
    }
}
