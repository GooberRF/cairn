using System.Windows.Controls;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>
/// The viewport badge that says which pose-editing mode a drag uses ("Auto-key — keys hand-l at the
/// playhead", "Layer edit — offsets every key of hand-l", "IK — …") or why no gizmo is shown.
/// </summary>
public partial class PoseEditBadge : UserControl
{
    public PoseEditBadge()
    {
        InitializeComponent();
    }
}
