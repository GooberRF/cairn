using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The mesh structure tree and the selected node's editor and facts. Code-behind turns a number box's
/// stepping gesture into one coalesced undo step, commits a name box on Enter (Esc restores it), runs
/// the tree's Del (remove the selected sphere or prop point) and Alt+Up / Alt+Down (move a bone), and
/// puts the keyboard in the name box for Rename Selected (F2).
/// </summary>
public partial class MeshStructureView : UserControl
{
    private MeshStructureViewModel? _model;

    public MeshStructureView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Detach();
            _model = DataContext as MeshStructureViewModel;
            Attach();
        };
        Unloaded += (_, _) => Detach();
        Loaded += (_, _) =>
        {
            Detach();
            Attach();
        };
    }

    private void Attach()
    {
        if (_model is null) return;
        _model.RenameRequested += OnRenameRequested;
        _model.PropertyChanged += OnModelPropertyChanged;
    }

    private void Detach()
    {
        if (_model is null) return;
        _model.RenameRequested -= OnRenameRequested;
        _model.PropertyChanged -= OnModelPropertyChanged;
    }

    /// <summary>
    /// A node selected from outside the tree (a bone picked in the viewport, the Problems panel, an added
    /// sphere, a diagnostic run) scrolls into view. The tree virtualises, so the node's container may not
    /// exist yet: it is realised level by level down the node's ancestors.
    /// </summary>
    private void OnModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MeshStructureViewModel.Selected)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_model?.Selected is not { } node || !IsLoaded) return;
            Tree.UpdateLayout();
            if (RealizeContainer(node) is { } item)
            {
                if (!item.IsSelected) item.IsSelected = true;
                item.BringIntoView();
            }
        }));
    }

    /// <summary>Generates the containers from the root down to <paramref name="node"/> (expanding its ancestors).</summary>
    private TreeViewItem? RealizeContainer(MeshNodeViewModel node)
    {
        var path = new List<MeshNodeViewModel>();
        for (var n = node; n is not null; n = n.Parent) path.Insert(0, n);
        ItemsControl parent = Tree;
        TreeViewItem? item = null;
        foreach (var step in path)
        {
            item = ContainerFor(parent, step);
            if (item is null) return null;
            if (!ReferenceEquals(step, node) && !item.IsExpanded)
            {
                item.IsExpanded = true;
                item.UpdateLayout();
            }
            parent = item;
        }
        return item;
    }

    private static TreeViewItem? ContainerFor(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem found) return found;
        int index = parent.Items.IndexOf(item);
        if (index < 0) return null;
        parent.ApplyTemplate();
        if (FindDescendant<ItemsPresenter>(parent) is { } presenter)
        {
            presenter.ApplyTemplate();
            if (VisualTreeHelper.GetChildrenCount(presenter) > 0 && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingStackPanel panel)
                panel.BringIndexIntoViewPublic(index);
        }
        parent.UpdateLayout();
        return parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private void OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MeshStructureViewModel model) model.Selected = e.NewValue as MeshNodeViewModel;
    }

    private void OnInteractionStarted(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MeshNumberField field }) field.BeginInteraction();
    }

    private void OnInteractionEnded(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MeshNumberField field }) field.EndInteraction();
    }

    /// <summary>Enter commits a name box, Esc puts the stored text back (number boxes handle their own keys).</summary>
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is not TextBox box) return;
        var binding = BindingOperations.GetBindingExpression(box, TextBox.TextProperty);
        if (binding is null) return;
        if (e.Key == Key.Enter)
        {
            binding.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            binding.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Del removes the selected sphere or prop point; Alt+Up / Alt+Down move the selected bone.</summary>
    private void OnTreeKeyDown(object sender, KeyEventArgs e)
    {
        if (_model is null) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var tools = _model.Tools;
        System.Windows.Input.ICommand? command = (key, Keyboard.Modifiers) switch
        {
            (Key.Delete, ModifierKeys.None) => tools.RemoveSelectedCommand,
            (Key.Up, ModifierKeys.Alt) => tools.MoveBoneUpCommand,
            (Key.Down, ModifierKeys.Alt) => tools.MoveBoneDownCommand,
            _ => null,
        };
        if (command is null) return;
        if (command.CanExecute(null)) command.Execute(null);
        e.Handled = true;
        // The tree is rebuilt by the edit: give the keyboard back to the node now selected.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FocusSelectedNode));
    }

    private void FocusSelectedNode()
    {
        if (_model?.Selected is not { } node) return;
        if (FindContainer(Tree, node) is { } item) item.Focus();
    }

    private static TreeViewItem? FindContainer(ItemsControl parent, MeshNodeViewModel node)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem direct) return direct;
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem child && child.IsExpanded && FindContainer(child, node) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Rename Selected (F2): the editor's name box takes the keyboard with its text selected.</summary>
    private void OnRenameRequested(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            EditorHost.UpdateLayout();
            if (FindRenameBox(EditorHost) is { } box)
            {
                box.BringIntoView();
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }
        }));

    /// <summary>The text box inside the element tagged "RenameTarget" in the editor.</summary>
    private static TextBox? FindRenameBox(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: "RenameTarget" } target) return FirstTextBox(target);
            if (FindRenameBox(child) is { } found) return found;
        }
        return null;
    }

    private static TextBox? FirstTextBox(DependencyObject root)
    {
        if (root is TextBox box) return box;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            if (FirstTextBox(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
