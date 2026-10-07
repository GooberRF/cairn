using System.ComponentModel;
using System.Numerics;

namespace Cairn.Viewport;

/// <summary>
/// What the viewport's <see cref="GizmoLayer"/>, tool bar, badge and Q / E / W / Esc keys drive: one per
/// document (RFA: posing a clip, or a mesh's bone binds, collision spheres and prop points). The layer only asks where the gizmo sits and
/// which kind it is, and turns mouse input into <see cref="BeginDrag"/> / <c>Update…</c> /
/// <see cref="CommitDrag"/> / <see cref="CancelDrag"/>; each target makes one coalesced undo step per drag.
/// </summary>
public interface IGizmoTarget : INotifyPropertyChanged
{
    /// <summary>Raised when what the gizmo layer draws may have changed (tool, space, selection, drag).</summary>
    event EventHandler? Changed;

    /// <summary>The active tool (shared by every document and persisted).</summary>
    PoseTool Tool { get; set; }

    /// <summary>The gizmo to draw now, or <see cref="PoseGizmo.None"/> (the badge says why).</summary>
    PoseGizmo Gizmo { get; }

    /// <summary>The item (bone) the gizmo sits on, or -1 (another item's pick inside the trackball wins the click).</summary>
    int ActiveBone { get; }

    /// <summary>True during a drag.</summary>
    bool IsDragging { get; }

    /// <summary>The handle being dragged (<see cref="GizmoHandle"/>), or <see cref="GizmoHandle.None"/>.</summary>
    int DragHandle { get; }

    /// <summary>The numeric readout while dragging, else null.</summary>
    string? Readout { get; }

    /// <summary>What a drag of the move gizmo's handles does: <see cref="PoseDragKind.Move"/> or <see cref="PoseDragKind.Ik"/>.</summary>
    PoseDragKind MoveDragKind { get; }

    /// <summary>True when the move gizmo also has a radius handle (a collision sphere's outline).</summary>
    bool HasRadiusHandle { get; }

    /// <summary>The radius the radius handle shows, in metres (model space).</summary>
    float GizmoRadius { get; }

    /// <summary>
    /// Where the gizmo sits: its model-space centre and the model-space rotation of its axes in the current
    /// space, from whatever the viewport shows now (the target knows its own scene state).
    /// </summary>
    bool TryGetPlacement(out Vector3 centre, out Quaternion frame);

    /// <summary>Starts a drag; false (with the reason in the status bar) when the gizmo does not apply.</summary>
    bool BeginDrag(PoseDragKind kind, int handle = GizmoHandle.None);

    /// <summary>Starts a drag of the radius handle; false when there is none.</summary>
    bool BeginRadiusDrag();

    /// <summary>Rotation about the space's axis <paramref name="axis"/> by <paramref name="radians"/>.</summary>
    void UpdateAxisRotation(int axis, double radians);

    /// <summary>A model-space rotation (screen ring, trackball); <paramref name="shownDegrees"/> is the readout angle.</summary>
    void UpdateWorldRotation(Quaternion world, int handle = GizmoHandle.Free, double? shownDegrees = null);

    /// <summary>A translation of <paramref name="metres"/> along the space's axis <paramref name="axis"/>.</summary>
    void UpdateAxisTranslation(int axis, double metres);

    /// <summary>A model-space translation (screen-plane drag).</summary>
    void UpdateWorldTranslation(Vector3 world);

    /// <summary>The radius handle's new radius, in metres.</summary>
    void UpdateRadius(double radius);

    /// <summary>Ends the drag as one undo step.</summary>
    void CommitDrag();

    /// <summary>Abandons the drag, restoring the pre-drag snapshot exactly (Esc).</summary>
    void CancelDrag();
}
