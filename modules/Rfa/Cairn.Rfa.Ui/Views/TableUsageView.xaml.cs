using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>The Table usage tab: double-click or Enter runs a row's action, Ctrl+C copies its table line.</summary>
public partial class TableUsageView : UserControl
{
    public TableUsageView() => InitializeComponent();

    private TableUsageViewModel? Model => DataContext as TableUsageViewModel;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // The innermost item under the pointer (the event bubbles through every ancestor item).
        if (FindItem(e.OriginalSource as DependencyObject)?.DataContext is not TableUsageNode node || !node.HasPrimaryAction) return;
        Model?.Activate(node);
        e.Handled = true;
    }

    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (Model is not { } model || Tree.SelectedItem is not TableUsageNode node) return;
        if (e.Key == Key.Enter && node.HasPrimaryAction)
        {
            model.Activate(node);
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && model.CopyCommand.CanExecute(node))
        {
            model.CopyCommand.Execute(node);
            e.Handled = true;
        }
    }

    private static TreeViewItem? FindItem(DependencyObject? source)
    {
        while (source is not null and not TreeViewItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        return source as TreeViewItem;
    }
}
