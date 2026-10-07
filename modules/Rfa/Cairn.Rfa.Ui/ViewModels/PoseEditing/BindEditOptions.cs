using Cairn.Ui.Mvvm;

namespace Cairn.Rfa.Ui.ViewModels.PoseEditing;

/// <summary>
/// Bind-pose editing options shared by the bone editor (Structure tab) and the mesh tabs' gizmos, kept for
/// the session (<see cref="RfaWorkspace.BindOptions"/>): "children follow" — a moved or rotated bone's
/// descendants move rigidly with it (their rest poses relative to it stay); off, they stay where they are
/// in the model.
/// </summary>
public sealed class BindEditOptions : ObservableObject
{
    private bool _childrenFollow;

    /// <summary>A bone's descendants move with it when its bind changes.</summary>
    public bool ChildrenFollow
    {
        get => _childrenFollow;
        set => Set(ref _childrenFollow, value);
    }
}
