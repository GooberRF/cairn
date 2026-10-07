using System.Windows.Controls;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views;

/// <summary>
/// The Texture settings panel. The only code here wires the frame-time spinner's gesture events to
/// the document's undo grouping, so a wheel-spin or a held arrow key is a single Ctrl+Z.
/// </summary>
public partial class HeaderView : UserControl
{
    public HeaderView()
    {
        InitializeComponent();
        FrameTimeBox.InteractionStarted += (_, _) => (DataContext as HeaderViewModel)?.BeginStepping();
        FrameTimeBox.InteractionEnded += (_, _) => (DataContext as HeaderViewModel)?.EndStepping();
    }
}
