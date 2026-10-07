using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>
/// The viewport's gizmo glue: connects the document's <see cref="IGizmoTarget"/> (a clip document's
/// <see cref="PoseEditController"/>, a mesh document's <see cref="MeshEditController"/>) to the gizmo layer,
/// the toolbar group and the badge, and routes gizmo drags, Q / E / W and Esc. Gizmo handles win over
/// picking and orbiting when hit. In mesh tabs a click also picks collision spheres and prop points.
/// </summary>
public partial class ViewportControl
{
    /// <summary>The pose-editing controller of the document shown (null for mesh documents).</summary>
    public PoseEditController? PoseEditing { get; private set; }

    /// <summary>The mesh gizmo controller of the document shown (null for clip documents).</summary>
    public MeshEditController? MeshEditing { get; private set; }

    /// <summary>The gizmo target of the document shown, or null (a dialog's preview).</summary>
    public IGizmoTarget? GizmoTarget => (IGizmoTarget?)PoseEditing ?? MeshEditing;

    private void InitPoseEditing()
    {
        Gizmos.BonePicker = p => Overlay.Pick(p);
        // Bone labels move out of the gizmo's circle (phase 6).
        Overlay.GizmoFootprint = () => Gizmos.Footprint;
        // A drag whose capture is taken away (Alt+Tab, a dialog) keeps what it did, as one undo step.
        Root.LostMouseCapture += (_, _) =>
        {
            if (_drag != DragMode.Gizmo) return;
            _drag = DragMode.None;
            Gizmos.EndDrag();
        };
    }

    private void AttachPoseEditing(DocumentViewModel? document)
    {
        if (Gizmos.IsDragging) Gizmos.EndDrag();
        if (GizmoTarget is { } old) old.Changed -= OnPoseEditingChanged;
        PoseEditing = document is ClipDocumentViewModel clip ? PoseEditController.For(clip) : null;
        MeshEditing = document is MeshDocumentViewModel mesh ? MeshEditController.For(mesh) : null;
        var target = GizmoTarget;
        if (target is not null) target.Changed += OnPoseEditingChanged;
        Gizmos.IsBoneSelected = document is null ? null : bone => document.Scene.Selection.Contains(bone);
        if (target is PoseEditController pc) pc.PoseSource = () => document?.Scene.Pose;
        else if (target is MeshEditController mc) mc.PoseSource = () => document?.Scene.Pose;
        Gizmos.Camera = document?.Scene.Camera;
        Gizmos.Controller = target;
        PoseBar.DataContext = target;
        PoseBadge.DataContext = target;
        var visibility = target is null ? Visibility.Collapsed : Visibility.Visible;
        PoseBar.Visibility = visibility;
        PoseBarSeparator.Visibility = visibility;
        PoseEditing?.SyncGhost();
        _lastPicks = [];
    }

    /// <summary>The gizmo appeared, moved or went: the overlay re-places the labels it would cover.</summary>
    private void OnPoseEditingChanged(object? sender, EventArgs e) => MarkOverlay();

    /// <summary>Starts a gizmo drag when a handle is under the mouse.</summary>
    private bool TryBeginGizmoDrag(Point p)
    {
        if (GizmoTarget is null) return false;
        // Ctrl+click on a joint inside the trackball toggles the bone in the selection, as everywhere else.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && Gizmos.HitTest(p) == GizmoHandle.Free && Overlay.Pick(p) >= 0) return false;
        if (!Gizmos.TryBeginDrag(p)) return false;
        _drag = DragMode.Gizmo;
        Root.CaptureMouse();
        ClearPickHover();
        return true;
    }

    /// <summary>Hover over the gizmo; true when a handle is under the mouse (pick hover is then cleared).</summary>
    private bool UpdateGizmoHover(Point p)
    {
        if (GizmoTarget is null) return false;
        bool changed = Gizmos.UpdateHover(p);
        if (Gizmos.HoverHandle == GizmoHandle.None)
        {
            if (changed) Root.Cursor = HasPickHover ? Cursors.Hand : null;
            return false;
        }
        ClearPickHover();
        Root.Cursor = Gizmos.HoverHandle == GizmoHandle.Radius ? Cursors.SizeNWSE : Cursors.SizeAll;
        return true;
    }

    private bool HasPickHover => Overlay.HoverBone >= 0 || Overlay.HoverSphere >= 0 || Overlay.HoverProp >= 0;

    private void ClearPickHover()
    {
        if (!HasPickHover) return;
        Overlay.HoverBone = Overlay.HoverSphere = Overlay.HoverProp = -1;
        MarkOverlay();
    }

    /// <summary>Hover highlight of what a click would pick in a mesh tab (a joint, a collision sphere or a prop point).</summary>
    private void UpdateMeshPickHover(Point p)
    {
        var hit = Overlay.PickBest(p, items: !Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        int bone = hit is { Kind: PickKind.Bone } b ? b.Index : -1;
        int sphere = hit is { Kind: PickKind.Sphere } s ? s.Index : -1;
        int prop = hit is { Kind: PickKind.Prop } q ? q.Index : -1;
        if (bone == Overlay.HoverBone && sphere == Overlay.HoverSphere && prop == Overlay.HoverProp) return;
        Overlay.HoverBone = bone;
        Overlay.HoverSphere = sphere;
        Overlay.HoverProp = prop;
        Root.Cursor = hit is null ? null : Cursors.Hand;
        MarkOverlay();
    }

    // ── Picking in mesh tabs ─────────────────────────────────────────────────

    private List<PickHit> _lastPicks = [];
    private Point _lastPickPoint;
    private int _lastPickIndex = -1;

    /// <summary>
    /// A click in a mesh tab: picks the best joint, collision sphere or prop point under <paramref name="p"/>;
    /// clicking again on the same spot (the same things under it) picks the next one, so overlapping items can
    /// all be reached. A bone is selected as before (Ctrl+click toggles bones only); a sphere or prop point is
    /// selected through the document (its Structure node and editor); empty space lets go of either.
    /// </summary>
    internal void ClickInMesh(Point p, ModifierKeys modifiers)
    {
        if (_scene is null) return;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var hits = Overlay.PickAll(p, items: !ctrl);
        int choice = -1;
        if (hits.Count > 0)
        {
            bool again = (p - _lastPickPoint).Length <= 4 && _lastPickIndex >= 0 && hits.Count == _lastPicks.Count
                && hits.Select(h => (h.Kind, h.Index)).OrderBy(h => h).SequenceEqual(_lastPicks.Select(h => (h.Kind, h.Index)).OrderBy(h => h));
            int previous = again ? hits.FindIndex(h => h.Kind == _lastPicks[_lastPickIndex].Kind && h.Index == _lastPicks[_lastPickIndex].Index) : -1;
            choice = previous >= 0 ? (previous + 1) % hits.Count : 0;
        }
        _lastPicks = hits;
        _lastPickPoint = p;
        _lastPickIndex = choice;
        var pick = choice >= 0 ? hits[choice] : (PickHit?)null;
        if (ctrl)
        {
            _scene.Selection.Toggle(pick?.Index ?? -1);
            if (pick is { } b && _scene.Selection.Contains(b.Index)) _scene.NotifyBonePicked(b.Index);
            return;
        }
        switch (pick)
        {
            case { Kind: PickKind.Bone } bone:
                _scene.Selection.Select(bone.Index);
                _scene.NotifyBonePicked(bone.Index);
                break;
            case { Kind: PickKind.Sphere } sphere:
                _scene.NotifyItemPicked(new ScenePick(SceneItemKind.Sphere, sphere.Index));
                break;
            case { Kind: PickKind.Prop } prop:
                _scene.NotifyItemPicked(new ScenePick(SceneItemKind.Prop, prop.Index));
                break;
            default:
                _scene.Selection.Select(-1);
                _scene.NotifyItemPicked(new ScenePick(SceneItemKind.None, -1));
                break;
        }
    }

    private void EndGizmoDrag()
    {
        _drag = DragMode.None;
        Gizmos.EndDrag();
        if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
    }

    /// <summary>Q / E / W and Esc for pose editing; true when handled.</summary>
    private bool HandlePoseKey(Key key)
    {
        if (key == Key.Escape && (Gizmos.IsDragging || GizmoTarget?.IsDragging == true))
        {
            _drag = DragMode.None;
            Gizmos.CancelDrag();
            GizmoTarget?.CancelDrag();
            if (Root.IsMouseCaptured) Root.ReleaseMouseCapture();
            return true;
        }
        if (GizmoTarget is not { } pose || Keyboard.Modifiers != ModifierKeys.None) return false;
        switch (key)
        {
            case Key.Q:
                pose.Tool = PoseTool.Select;
                return true;
            case Key.E:
                pose.Tool = PoseTool.Rotate;
                return true;
            case Key.W:
                pose.Tool = PoseTool.Move;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The display menu's "Ghost of the saved clip" item.</summary>
    private MenuItem GhostMenuItem(ViewportDisplaySettings display)
    {
        var item = new MenuItem
        {
            Header = "_Ghost of the saved clip",
            IsCheckable = true,
            IsChecked = display.GhostSavedClip,
            IsEnabled = PoseEditing is not null,
            ToolTip = "Draws the clip as last saved (or reloaded) as a dimmed skeleton under the edited pose.\n"
                + "It shows only while the clip has unsaved changes: an unedited clip's ghost would sit exactly under the live skeleton.",
        };
        item.Click += (_, _) => display.GhostSavedClip = !display.GhostSavedClip;
        return item;
    }
}
