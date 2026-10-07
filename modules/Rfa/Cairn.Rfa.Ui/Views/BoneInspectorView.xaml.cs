using System.Windows.Controls;

namespace Cairn.Rfa.Ui.Views;

/// <summary>The Bone inspector; code-behind coalesces a weight spinner run into one undo step.</summary>
public partial class BoneInspectorView : UserControl
{
    public BoneInspectorView() => InitializeComponent();

    private void OnInteractionStarted(object sender, EventArgs e) => KeyInspectorView.Begin(sender);

    private void OnInteractionEnded(object sender, EventArgs e) => KeyInspectorView.End(sender);
}
