using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Viewport;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The Library panel. Code-behind wires what XAML cannot: tree selection into the view-model,
/// double-click and Enter to open, Esc to clear the filter, and dragging an entry onto a viewport.
/// </summary>
public partial class LibraryView : UserControl
{
    private Point _dragStart;
    private object? _dragItem;

    public LibraryView()
    {
        InitializeComponent();
        FilterBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && Model is { } model)
            {
                model.Filter = string.Empty;
                e.Handled = true;
            }
        };
    }

    private LibraryViewModel? Model => DataContext as LibraryViewModel;

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (Model is { } model) model.SelectedNode = e.NewValue as LibraryNode;
    }

    private static bool CtrlHeld => Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

    /// <summary>Double-click previews on the document in front (or opens; Ctrl: always a new tab).</summary>
    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Model is not { } model || Find<TreeViewItem>(e.OriginalSource as DependencyObject) is not { DataContext: LibraryNode node }) return;
        if (node.Item is null) return;
        model.Activate(node.Item, newTab: CtrlHeld);
        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { SelectedNode.Item: { } item } model)
        {
            model.Activate(item, newTab: CtrlHeld);
            e.Handled = true;
        }
    }

    private void OnClipDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (Model is not { } model || Find<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: LibraryClipRow row }) return;
        model.Activate(row.Clip, newTab: CtrlHeld);
        e.Handled = true;
    }

    private void OnClipKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Model is { SelectedClipRow: { } row } model)
        {
            model.Activate(row.Clip, newTab: CtrlHeld);
            e.Handled = true;
        }
    }

    /// <summary>Middle-click opens the entry under the pointer in a new tab.</summary>
    private void OnMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || Model is not { } model) return;
        var source = e.OriginalSource as DependencyObject;
        object? item = Find<TreeViewItem>(source)?.DataContext is LibraryNode { Item: { } nodeItem } ? nodeItem
            : Find<ListBoxItem>(source)?.DataContext is LibraryClipRow row ? row.Clip
            : null;
        if (item is null) return;
        model.Activate(item, newTab: true);
        e.Handled = true;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        var source = e.OriginalSource as DependencyObject;
        _dragItem = Find<TreeViewItem>(source)?.DataContext is LibraryNode { Item: { } item } ? item
            : Find<ListBoxItem>(source)?.DataContext is LibraryClipRow row ? row.Clip
            : null;
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        var data = new DataObject(ViewportControl.LibraryDragFormat, item);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Link);
    }

    private static T? Find<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return source as T;
    }
}
