using System.Windows.Controls;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The frame inspector. The only code here joins the frame-time spinner's gesture to the document's
/// undo grouping, so bulk-editing a selection by wheel is one undo step.
/// </summary>
public partial class FrameInspectorView : UserControl
{
    public FrameInspectorView()
    {
        InitializeComponent();
        TimeBox.InteractionStarted += (_, _) => Document?.BeginInteraction();
        TimeBox.InteractionEnded += (_, _) => Document?.EndInteraction();
    }

    private DocumentViewModel? Document =>
        (DataContext as FrameInspectorViewModel) is not null ? FindDocument() : null;

    private DocumentViewModel? FindDocument()
    {
        var element = System.Windows.Media.VisualTreeHelper.GetParent(this);
        while (element is not null)
        {
            if (element is System.Windows.FrameworkElement { DataContext: DocumentViewModel document })
                return document;
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
