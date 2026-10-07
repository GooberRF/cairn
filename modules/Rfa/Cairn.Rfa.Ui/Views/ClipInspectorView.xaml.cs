using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Rfa.Ui.Controls;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The Clip inspector. Code-behind does two things XAML cannot: turns a number box's stepping gesture
/// into one coalesced undo step, and focuses the row the Problems panel asks for.
/// </summary>
public partial class ClipInspectorView : UserControl
{
    private ClipInspectorViewModel? _model;

    public ClipInspectorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.FocusFieldRequested -= OnFocusFieldRequested;
            _model = DataContext as ClipInspectorViewModel;
            if (_model is not null) _model.FocusFieldRequested += OnFocusFieldRequested;
        };
    }

    private void OnInteractionStarted(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NumberRowViewModel row }) row.BeginInteraction();
    }

    private void OnInteractionEnded(object sender, EventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NumberRowViewModel row }) row.EndInteraction();
    }

    private void OnFocusFieldRequested(object? sender, string fieldId)
    {
        // The expander may just have opened; let layout run before looking for the box.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            FrameworkElement? target = Find(this, fieldId);
            if (target is null) return;
            target.BringIntoView();
            if (target is NumericBox box) box.Focus();
            target.Focus();
            if (target is NumericBox numeric) numeric.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static FrameworkElement? Find(DependencyObject root, string fieldId)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Tag as string == fieldId && fe is NumericBox or ComboBox) return fe;
            if (Find(child, fieldId) is { } hit) return hit;
        }
        return null;
    }
}
