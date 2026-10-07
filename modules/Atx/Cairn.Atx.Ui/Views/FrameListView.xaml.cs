using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The frames list. The code here is the interaction the design specifies and XAML cannot express:
/// drag reorder with an insertion indicator, Explorer drops landing at the position under the
/// cursor, the inline rename/time editors, and the keyboard shortcuts.
/// </summary>
public partial class FrameListView : UserControl
{
    private const string InternalDragFormat = "Cairn.Atx.Ui.Frames";

    private Point _pressPoint;
    private bool _dragCandidate;
    private bool _applyingSelection;

    public FrameListView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        List.SelectionChanged += OnListSelectionChanged;
        List.PreviewMouseLeftButtonDown += OnListMouseDown;
        List.PreviewMouseMove += OnListMouseMove;
        List.PreviewMouseLeftButtonUp += (_, _) => OnListMouseUp();
        List.MouseDoubleClick += OnDoubleClick;
        List.PreviewKeyDown += OnListKeyDown;
        List.DragOver += OnDragOver;
        List.DragLeave += (_, _) => HideIndicator();
        List.Drop += OnDrop;
        List.GotKeyboardFocus += (_, _) => SetFocused(true);
        List.LostKeyboardFocus += (_, _) => SetFocused(false);
        // Row realisation is hooked here rather than on Loaded: the list generates its first screen
        // of containers during the first layout pass, which happens before this view's own Loaded
        // runs, and those rows would then never be reported — leaving the visible thumbnails blank.
        List.ItemContainerGenerator.StatusChanged += OnContainersChanged;
        Loaded += (_, _) => { ReportRealisedRows(); PushSelectionToList(); };
    }

    private void OnContainersChanged(object? sender, EventArgs e)
    {
        if (List.ItemContainerGenerator.Status
            == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            ReportRealisedRows();
        }
    }

    /// <summary>
    /// Tells the view-model which rows the list has actually built a container for, so only those
    /// ask for a thumbnail — a 500-frame document should not start 500 image decodes the moment it
    /// opens. The list recycles containers, so this runs again every time scrolling generates more.
    /// </summary>
    private void ReportRealisedRows()
    {
        var generator = List.ItemContainerGenerator;
        var model = Model;
        for (int i = 0; i < List.Items.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not FrameworkElement container) continue;
            model?.OnRowRealised(i);
            WatchRowEditors(container);
        }
    }

    private FrameListViewModel? Model => DataContext as FrameListViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is FrameListViewModel old)
        {
            old.ScrollIntoViewRequested -= OnScrollIntoView;
            old.SelectionRequested -= OnSelectionRequested;
        }
        if (e.NewValue is FrameListViewModel now)
        {
            now.ScrollIntoViewRequested += OnScrollIntoView;
            now.SelectionRequested += OnSelectionRequested;
            PushSelectionToList();
        }
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The list box owns the selection, so every change it makes is handed to the view-model here.
    /// It used to be the other way round — a two-way <c>IsSelected</c> binding on the container —
    /// and under recycling virtualisation that only ever reached the rows on screen: Ctrl+A then a
    /// click on one row left the view-model holding hundreds, and Delete removed them.
    /// </summary>
    private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // A selection the view-model asked for is already recorded there; echoing it back would
        // fan the same change out twice, and for the editor-driven case would scroll the source
        // the user is typing in.
        if (_applyingSelection || !ReferenceEquals(e.OriginalSource, List)) return;
        Model?.OnListSelectionChanged(List.SelectedItems.Cast<object?>());
    }

    private void OnSelectionRequested(object? sender, IReadOnlyList<FrameRowViewModel> rows) =>
        ApplyToList(rows);

    /// <summary>Makes the list box's selection match the view-model's, e.g. when a tab is built.</summary>
    private void PushSelectionToList()
    {
        if (Model is not { } model) return;
        ApplyToList([.. model.Rows.Where(r => r.IsSelected)]);
    }

    private void ApplyToList(IReadOnlyList<FrameRowViewModel> rows)
    {
        _applyingSelection = true;
        try { List.SetSelection(rows); }
        finally { _applyingSelection = false; }
    }

    private void OnScrollIntoView(object? sender, int index)
    {
        if (Model is null || index < 0 || index >= Model.Rows.Count) return;
        List.ScrollIntoView(Model.Rows[index]);
    }

    private void SetFocused(bool focused)
    {
        if (Model is not null) Model.IsFocused = focused;
    }

    // ── Inline editors ────────────────────────────────────────────────────────

    /// <summary>
    /// Focuses whichever inline editor has just appeared. Rows are virtualised and recycled, so the
    /// view watches for the text box becoming visible rather than holding a reference to it.
    ///
    /// Loaded alone is not enough. A row's text box goes through Loaded while it is still hidden,
    /// so pressing F2 later only changes its visibility — no Loaded, no focus, and the caret stays
    /// wherever it was. Watching visibility as well catches every appearance, first or hundredth.
    /// The handler is attached here rather than on this view's own Loaded, because the list builds
    /// its first screen of rows before that runs and those boxes would never be seen at all.
    /// </summary>
    private void WatchRowEditors(FrameworkElement container)
    {
        if (container.IsLoaded) { HookEditors(container); return; }
        container.Loaded -= OnRowContainerLoaded;
        container.Loaded += OnRowContainerLoaded;
    }

    private void OnRowContainerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject container) HookEditors(container);
    }

    private void HookEditors(DependencyObject container)
    {
        foreach (var box in InlineEditors(container))
        {
            // Containers are recycled, so the same text box comes back; remove before adding.
            box.IsVisibleChanged -= OnEditorVisibleChanged;
            box.IsVisibleChanged += OnEditorVisibleChanged;
            if (box.IsVisible) FocusEditor(box);
        }
    }

    private void OnEditorVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && e.NewValue is true) FocusEditor(box);
    }

    /// <summary>The inline rename and time boxes inside one row, marked in XAML with Tag="edit".</summary>
    private static IEnumerable<TextBox> InlineEditors(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { Tag: "edit" } box) yield return box;
            else
            {
                foreach (var found in InlineEditors(child)) yield return found;
            }
        }
    }

    private static void FocusEditor(TextBox box)
    {
        box.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!box.IsVisible) return;
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FrameRowViewModel row }) return;
        if (e.Key == Key.Enter) { row.CommitEdit(); e.Handled = true; List.Focus(); }
        else if (e.Key == Key.Escape) { row.CancelEdit(); e.Handled = true; List.Focus(); }
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FrameRowViewModel row } && row.IsEditing) row.CommitEdit();
    }

    // ── Mouse ─────────────────────────────────────────────────────────────────

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(List);
        var container = FindRow(e.OriginalSource as DependencyObject);
        _dragCandidate = container is not null && e.OriginalSource is not TextBox;
        _pendingSingleSelect = null;

        // A ListBox in Extended mode collapses a multi-selection to the clicked row on mouse DOWN,
        // so by the time the drag starts only one frame is selected and "moves all selected" never
        // happens. Hold the collapse back until mouse-up, when we know no drag began.
        if (container is { IsSelected: true } row
            && Keyboard.Modifiers is not (ModifierKeys.Control or ModifierKeys.Shift)
            && Model is { } model && model.SelectedIndices.Count > 1)
        {
            _pendingSingleSelect = row.DataContext as FrameRowViewModel;
            e.Handled = true;
            // Handling the press stops the ListBox taking focus for itself, and the list needs it
            // for its keyboard shortcuts and for the caret-sync suppression.
            if (!List.IsKeyboardFocusWithin) List.Focus();
        }
    }

    private void OnListMouseUp()
    {
        _dragCandidate = false;
        if (_pendingSingleSelect is { } row)
        {
            _pendingSingleSelect = null;
            Model?.SelectOnly(row.Index);
        }
    }

    private FrameRowViewModel? _pendingSingleSelect;

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || e.LeftButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(List);
        if (Math.Abs(now.X - _pressPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(now.Y - _pressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragCandidate = false;
        // A drag started, so the held-back collapse must not happen on mouse-up.
        _pendingSingleSelect = null;
        if (Model is not { CanEditStructure: true } model || !model.HasSelection) return;

        var data = new DataObject(InternalDragFormat, model.SelectedIndices.ToArray());
        try { DragDrop.DoDragDrop(List, data, DragDropEffects.Move); }
        finally { HideIndicator(); }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Model is not { CanEditStructure: true }) return;
        var container = FindRow(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not FrameRowViewModel row) return;

        // Double-clicking the time column edits the time; anywhere else renames the file.
        double xInRow = e.GetPosition(container).X;
        bool onTime = xInRow > container.ActualWidth - 130 && xInRow < container.ActualWidth - 30;
        if (onTime) row.BeginEditTime(); else row.BeginEditName();
        e.Handled = true;
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model) return;
        if (e.OriginalSource is TextBox) return;

        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        switch (e.Key)
        {
            case Key.System when e.SystemKey == Key.Up && alt:
                model.MoveUpCommand.Execute(null); e.Handled = true; break;
            case Key.System when e.SystemKey == Key.Down && alt:
                model.MoveDownCommand.Execute(null); e.Handled = true; break;
            case Key.Delete:
                model.RemoveCommand.Execute(null); e.Handled = true; break;
            case Key.F2:
                model.RenameCommand.Execute(null); e.Handled = true; break;
            case Key.Insert:
                model.AddFramesCommand.Execute(null); e.Handled = true; break;
            case Key.D when ctrl:
                model.DuplicateCommand.Execute(null); e.Handled = true; break;
            case Key.C when ctrl:
                model.CopyCommand.Execute(null); e.Handled = true; break;
            case Key.X when ctrl:
                model.CutCommand.Execute(null); e.Handled = true; break;
            case Key.V when ctrl:
                model.PasteCommand.Execute(null); e.Handled = true; break;
            case Key.A when ctrl:
                model.SelectAllCommand.Execute(null); e.Handled = true; break;
        }
    }

    // ── Drag and drop ─────────────────────────────────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        int index = InsertionIndex(e.GetPosition(List));
        bool internalMove = e.Data.GetDataPresent(InternalDragFormat);
        bool files = HasImageFiles(e.Data);

        if (Model is not { CanEditStructure: true } || (!internalMove && !files))
        {
            e.Effects = DragDropEffects.None;
            HideIndicator();
            e.Handled = true;
            return;
        }

        e.Effects = internalMove ? DragDropEffects.Move : DragDropEffects.Copy;
        ShowIndicator(index);
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HideIndicator();
        if (Model is not { CanEditStructure: true } model) return;
        int index = InsertionIndex(e.GetPosition(List));

        if (e.Data.GetDataPresent(InternalDragFormat))
        {
            model.MoveSelectionTo(index);
            e.Handled = true;
            return;
        }

        var images = ImageFiles(e.Data);
        if (images.Count == 0) return;
        DocumentOf()?.AddImageFiles(images, index);
        e.Handled = true;
    }

    private DocumentViewModel? DocumentOf()
    {
        var parent = this as DependencyObject;
        while (parent is not null)
        {
            if (parent is FrameworkElement { DataContext: DocumentViewModel document }) return document;
            parent = VisualTreeHelper.GetParent(parent);
        }
        return null;
    }

    private static bool HasImageFiles(IDataObject data) => ImageFiles(data).Count > 0;

    /// <summary>The dropped paths whose extension the app can actually read as a texture.</summary>
    private static IReadOnlyList<string> ImageFiles(IDataObject data)
    {
        if (data.GetData(DataFormats.FileDrop) is not string[] paths) return [];
        return
        [
            .. paths.Where(p => AtxSchema.ReadableExtensions.Any(
                e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))),
        ];
    }

    /// <summary>
    /// The realised container for <paramref name="index"/>, or the nearest realised one below it
    /// with its own index. Returns null only when nothing at all is realised.
    /// </summary>
    private (ListBoxItem Container, int Index)? Nearest(int index)
    {
        for (int i = index; i >= 0; i--)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem c) return (c, i);
        }
        return null;
    }

    /// <summary>Where a drop at <paramref name="point"/> would insert, in frame indices.</summary>
    private int InsertionIndex(Point point)
    {
        if (Model is not { } model || model.Rows.Count == 0) return 0;
        // The list is virtualised, so rows outside the viewport have no container. Falling back to
        // the end of the whole list would send a drop below the last visible row to the end of the
        // file; the answer wanted is "after the last row we could actually measure".
        int lastRealized = -1;
        for (int i = 0; i < model.Rows.Count; i++)
        {
            if (List.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container) continue;
            lastRealized = i;
            var topLeft = container.TranslatePoint(new Point(0, 0), List);
            double middle = topLeft.Y + container.ActualHeight / 2;
            if (point.Y < middle) return i;
        }
        return lastRealized >= 0 ? lastRealized + 1 : model.Rows.Count;
    }

    private void ShowIndicator(int index)
    {
        double y;
        if (Model is { Rows.Count: > 0 } model
            && Nearest(Math.Min(index, model.Rows.Count - 1)) is (ListBoxItem container, int shown))
        {
            var topLeft = container.TranslatePoint(new Point(0, 0), List);
            y = index > shown ? topLeft.Y + container.ActualHeight : topLeft.Y;
        }
        else
        {
            y = 0;
        }

        InsertLine.Width = Math.Max(0, List.ActualWidth - 8);
        Canvas.SetLeft(InsertLine, 4);
        Canvas.SetTop(InsertLine, Math.Clamp(y - 1, 0, Math.Max(0, List.ActualHeight - 2)));
        InsertLine.Visibility = Visibility.Visible;
    }

    private void HideIndicator() => InsertLine.Visibility = Visibility.Collapsed;

    private static ListBoxItem? FindRow(DependencyObject? source)
    {
        while (source is not null and not ListBoxItem)
        {
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return source as ListBoxItem;
    }

    /// <summary>Wires the inline editors' keyboard and focus behaviour as rows are realised.</summary>
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        List.AddHandler(TextBox.KeyDownEvent, new KeyEventHandler(OnEditorKeyDown), true);
        List.AddHandler(TextBox.LostFocusEvent, new RoutedEventHandler(OnEditorLostFocus), true);
    }
}
