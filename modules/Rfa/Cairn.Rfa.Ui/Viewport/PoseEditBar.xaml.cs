using System.Windows;
using System.Windows.Controls;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>
/// The viewport toolbar's gizmo group: Select / Rotate / Move (Q / E / W) and the space switch for every
/// document; in clip tabs auto-key with its layer-range options flyout and the IK toggle; in mesh tabs the
/// "children follow" toggle of bind editing. Its DataContext is the document's <see cref="IGizmoTarget"/>
/// (a <see cref="PoseEditController"/> or a <see cref="MeshEditController"/>); each kind's own group gets
/// that controller as its DataContext (null for the other kind, so its bindings stay quiet) and shows only then.
/// </summary>
public partial class PoseEditBar : UserControl
{
    public PoseEditBar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateGroups();
        UpdateGroups();
    }

    private void UpdateGroups()
    {
        var clip = DataContext as PoseEditController;
        var mesh = DataContext as MeshEditController;
        ClipGroup.DataContext = clip;
        ClipGroup.Visibility = clip is null ? Visibility.Collapsed : Visibility.Visible;
        MeshGroup.DataContext = mesh;
        MeshGroup.Visibility = mesh is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOptionsClick(object sender, RoutedEventArgs e) => OptionsPopup.IsOpen = !OptionsPopup.IsOpen;

    private void OnFromPlayheadClick(object sender, RoutedEventArgs e) => (DataContext as PoseEditController)?.RangeFromPlayhead();

    private void OnToPlayheadClick(object sender, RoutedEventArgs e) => (DataContext as PoseEditController)?.RangeToPlayhead();
}
