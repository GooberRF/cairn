using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Cairn.Rfa.Ui.ViewModels;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The Key inspector. Code-behind turns a number box's stepping gesture or a slider drag into one
/// coalesced undo step (the field's Begin/EndInteraction).
/// </summary>
public partial class KeyInspectorView : UserControl
{
    public KeyInspectorView() => InitializeComponent();

    internal static void Begin(object sender)
    {
        if (sender is FrameworkElement { DataContext: SelectionFieldViewModel field }) field.BeginInteraction();
    }

    internal static void End(object sender)
    {
        if (sender is FrameworkElement { DataContext: SelectionFieldViewModel field }) field.EndInteraction();
    }

    private void OnInteractionStarted(object sender, EventArgs e) => Begin(sender);

    private void OnInteractionEnded(object sender, EventArgs e) => End(sender);

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e) => Begin(sender);

    private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e) => End(sender);
}
