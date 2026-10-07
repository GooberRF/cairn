using System.ComponentModel;
using System.Numerics;
using System.Windows.Input;
using Cairn.Ui.Mvvm;
using Cairn.Viewport;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;

namespace Cairn.Vfx.Ui.Viewport;

/// <summary>
/// Gizmo controller for the primary selected object of an effect: move/rotate at the playhead. What a drag edits is
/// decided by <see cref="VfxTimelineEdits.ApplyGizmo"/> (animation kind, <see cref="AutoKey"/>, Shift at drag start =
/// all frames). One drag = one undo step (BeginEdit/UpdateEdit/CommitEdit); Escape cancels (GizmoLayer).
/// </summary>
public sealed class VfxGizmoTarget : ObservableObject, IGizmoTarget, IGizmoScaleTarget
{
    private readonly VfxDocument _doc;
    private PoseTool _tool = PoseTool.Select;
    private VfxFile? _base;
    private int _section = -1, _handle = GizmoHandle.None;
    private float _frame;
    private bool _allFrames;
    private Quaternion _frameRot = Quaternion.Identity;
    private Vector3 _scaleCentre;
    private string? _readout;

    public VfxGizmoTarget(VfxDocument doc)
    {
        _doc = doc;
        doc.Selection.Changed += (_, _) => Notify();
        doc.FrameChanged += (_, _) => { if (!IsDragging) Notify(); };
        doc.SceneChanged += (_, _) => { if (!IsDragging) Notify(); };
    }

    public event EventHandler? Changed;
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Keyframed meshes: set/insert keys at the playhead (on) or offset whole channels (off).</summary>
    /// <remarks>Shared by every effect: reads/writes <see cref="VfxGizmoPrefs"/> (persisted).</remarks>
    public bool AutoKey { get => VfxGizmoPrefs.AutoKey; set => VfxGizmoPrefs.AutoKey = value; }
    /// <summary>Gizmo axes in the object's orientation (true) or world axes (shared <see cref="VfxGizmoPrefs"/>).</summary>
    public bool LocalSpace { get => VfxGizmoPrefs.LocalSpace; set { if (VfxGizmoPrefs.LocalSpace == value) return; VfxGizmoPrefs.LocalSpace = value; Notify(); } }

    public PoseTool Tool { get => _tool; set { if (Set(ref _tool, value)) Notify(); } }

    public PoseGizmo Gizmo => _doc.IsOlderVersion || _doc.Selection.Primary < 0 ? PoseGizmo.None
        : _tool switch { PoseTool.Move => PoseGizmo.Move, PoseTool.Rotate when CanRotate => PoseGizmo.Rotate, PoseTool.Scale when CanScale => PoseGizmo.Scale, _ => PoseGizmo.None };

    private bool CanRotate => _doc.Selection.Primary is var p and >= 0 && p < _doc.Current.Sections.Length
        && _doc.Current.Sections[p] is not VfxLight && !(_doc.Current.Sections[p] is VfxMesh { IsMorph: true, Keys: null });

    public int ActiveBone => _doc.Selection.Primary;
    public bool IsDragging => _base is not null;
    public int DragHandle => _handle;
    public string? Readout => _readout;
    /// <summary>Why the current drag step was refused (pivot mode on a mesh whose pivot cannot change exactly), or null.</summary>
    public string? BlockedReason { get; private set; }
    public PoseDragKind MoveDragKind => PoseDragKind.Move;
    public bool HasRadiusHandle => false;
    public float GizmoRadius => 0;

    public bool TryGetPlacement(out Vector3 centre, out Quaternion frame)
    {
        bool ok = VfxTimelineEdits.TryGetPlacement(_doc.Current, _doc.Selection.Primary, _doc.TimelineFrame, out centre, out var rot);
        frame = LocalSpace ? rot : Quaternion.Identity;
        return ok;
    }

    public bool BeginDrag(PoseDragKind kind, int handle = GizmoHandle.None)
    {
        if (_doc.IsOlderVersion || _doc.Selection.Primary < 0 || IsDragging) return false;
        _section = _doc.Selection.Primary;
        _frame = _doc.TimelineFrame;
        _allFrames = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        TryGetPlacement(out _scaleCentre, out _frameRot);
        _handle = handle;
        _base = _doc.Current;
        _doc.BeginEdit(kind switch { PoseDragKind.Rotate => "Rotate object", PoseDragKind.Scale => "Scale object", _ => "Move object" });
        Notify();
        return true;
    }

    public bool BeginRadiusDrag() => false;

    private void Update(Vector3 move, Quaternion turn, string readout)
    {
        if (_base is not { } b) return;
        bool pivot = VfxGizmoPrefs.PivotMode && b.Sections[_section] is VfxMesh { Keys: not null };
        // a pivot change that cannot be compensated exactly is refused: the drag shows why and leaves the effect alone
        BlockedReason = !pivot ? null : (move != Vector3.Zero ? VfxPivotEdits.MoveBlocked(b, _section) : null) ?? (!turn.IsIdentity ? VfxPivotEdits.TurnBlocked(b, _section) : null);
        _readout = BlockedReason ?? readout;
        if (BlockedReason is not null) { _doc.UpdateEdit(_ => b); Notify(); return; }
        _doc.UpdateEdit(_ => pivot ? VfxPivotEdits.ApplyGizmo(b, _section, _frame, move, turn) : VfxTimelineEdits.ApplyGizmo(b, _section, _frame, move, turn, AutoKey, _allFrames));
        Notify();
    }

    public void UpdateAxisRotation(int axis, double radians)
    {
        var a = Vector3.Transform(axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }, _frameRot);
        Update(Vector3.Zero, Quaternion.CreateFromAxisAngle(a, (float)radians), $"{radians * 180 / Math.PI:0.0}°");
    }

    public void UpdateWorldRotation(Quaternion world, int handle = GizmoHandle.Free, double? shownDegrees = null)
        => Update(Vector3.Zero, world, shownDegrees is { } d ? $"{d:0.0}°" : "free");

    public void UpdateAxisTranslation(int axis, double metres)
    {
        var a = Vector3.Transform(axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }, _frameRot);
        Update(a * (float)metres, Quaternion.Identity, $"{metres:0.000} m");
    }

    public void UpdateWorldTranslation(Vector3 world) => Update(world, Quaternion.Identity, $"{world.X:0.00}, {world.Y:0.00}, {world.Z:0.00}");

    public void UpdateRadius(double radius) { }

    /// <summary>True when the primary object has something to scale (meshes: keyed, static/per-frame, morph, facing).</summary>
    public bool CanScale => _doc.Selection.Primary is var p and >= 0 && p < _doc.Current.Sections.Length && _doc.Current.Sections[p] is VfxMesh;

    /// <summary>Scale drag: factor along the gizmo axis <paramref name="handle"/> or uniform (<see cref="GizmoHandle.Screen"/>).</summary>
    public void UpdateScale(int handle, double factor)
    {
        if (_base is not { } b) return;
        float k = (float)factor;
        var scale = GizmoHandle.ScaleVector(handle, k);
        _readout = handle == GizmoHandle.Screen ? $"×{factor:0.00}" : $"{GizmoHandle.Name(handle)} ×{factor:0.00}";
        var centre = _scaleCentre;
        BlockedReason = VfxGizmoPrefs.PivotMode && b.Sections[_section] is VfxMesh { Keys: not null } ? "Pivot mode has no scale: turn pivot mode off to scale the object" : null;
        if (BlockedReason is not null) { _readout = BlockedReason; _doc.UpdateEdit(_ => b); Notify(); return; }
        _doc.UpdateEdit(_ => VfxTimelineEdits.ApplyScale(b, _section, _frame, scale, centre, AutoKey, _allFrames));
        Notify();
    }

    public void CommitDrag() { if (_base is null) return; _base = null; _readout = null; _handle = GizmoHandle.None; _doc.CommitEdit(); Notify(); }

    public void CancelDrag() { if (_base is null) return; _base = null; _readout = null; _handle = GizmoHandle.None; _doc.CancelEdit(); Notify(); }
}
