using System.Numerics;
using System.Runtime.CompilerServices;
using Cairn.Ui.Mvvm;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.VertexEditing;

/// <summary>
/// Vertex mode state of one effect document: on/off, the mesh section whose vertices are selected,
/// the selected vertex indices and the edit scope (this frame / all frames). The selection lives here rather than in
/// <see cref="VfxSelection"/> (kept separately); it is cleared when the primary selection or the mesh topology changes.
/// </summary>
public sealed class VfxVertexMode : ObservableObject
{
    private static readonly ConditionalWeakTable<VfxDocument, VfxVertexMode> Modes = new();
    public static VfxVertexMode Of(VfxDocument doc) => Modes.GetValue(doc, d => new VfxVertexMode(d));

    private readonly VfxDocument _doc;
    private readonly HashSet<int> _selected = [];
    private int _section = -1, _topology;
    private bool _enabled, _allFrames;

    private VfxVertexMode(VfxDocument doc)
    {
        _doc = doc;
        Gizmo = new VfxVertexGizmoTarget(doc, this);
        doc.Selection.Changed += (_, _) => { if (doc.Selection.Primary != _section) Retarget(); };
        doc.SceneChanged += (_, _) => { if (Topology() != _topology) Retarget(); else Notify(); };
    }

    public VfxVertexGizmoTarget Gizmo { get; }
    public event EventHandler? Changed;
    private void Notify() { Changed?.Invoke(this, EventArgs.Empty); Raise(nameof(StatusText)); }

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Retarget(); if (value) SnapPlayhead(); } } }
    /// <summary>All frames (true): the same delta on every stored geometry frame. This frame (false): the nearest stored frame.</summary>
    public bool AllFrames { get => _allFrames; set { if (Set(ref _allFrames, value)) { if (!value) SnapPlayhead(); Notify(); } } }
    public int Section => _section;
    public IReadOnlySet<int> Selected => _selected;
    public VfxMesh? Mesh => (uint)_section < (uint)_doc.Current.Sections.Length ? _doc.Current.Sections[_section] as VfxMesh : null;
    /// <summary>Why vertex editing is unavailable right now (null = available).</summary>
    public string? Unavailable => _doc.IsOlderVersion ? VfxEditing.DisabledReason(_doc) : VfxVertexEdits.Unavailable(_doc.Current, _doc.Selection.Primary);
    public bool IsActive => _enabled && Unavailable is null && Mesh is not null;

    public string StatusText => !_enabled ? "" : Unavailable is { } why ? $"Vertex mode: {why}"
        : $"Vertex mode: {_selected.Count} of {Mesh!.NumVertices} vertices, {ScopeText}";

    private int Topology() => Mesh is { } m ? HashCode.Combine(m.NumVertices, m.Faces.Length, _section) : -1;

    private void Retarget()
    {
        _section = _doc.Selection.Primary;
        _selected.Clear();
        _topology = Topology();
        Notify();
    }

    /// <summary>This-frame scope on a morph mesh: moves the playhead onto the stored frame being edited.</summary>
    public void SnapPlayhead()
    {
        if (!IsActive || _allFrames || Mesh is not { IsMorph: true }) return;
        int ord = VfxVertexEdits.Ordinal(_doc.Current, _section);
        int k = VfxVertexEdits.EditFrame(_doc.Sampler, _section, _doc.TimelineFrame);
        float f = VfxVertexEdits.EffectFrameOf(_doc.Sampler, ord, k);
        if (MathF.Abs(f - _doc.TimelineFrame) > 1e-3f) _doc.SeekFrame(f);
    }

    public void Select(IEnumerable<int> verts, bool add = false, bool toggle = false)
    {
        if (!add && !toggle) _selected.Clear();
        foreach (int v in verts) if (!(toggle && _selected.Remove(v))) _selected.Add(v);
        Notify();
    }

    public void SelectAll() { if (Mesh is { } m) Select(Enumerable.Range(0, m.NumVertices)); }
    public void Invert() { if (Mesh is { } m) Select(Enumerable.Range(0, m.NumVertices).Where(v => !_selected.Contains(v)).ToList()); }
    public void SelectConnected() { if (Mesh is { } m) Select(VfxVertexEdits.Connected(m, _selected).ToList()); }
    public void Grow() { if (Mesh is { } m) Select(VfxVertexEdits.Grow(m, _selected).ToList()); }
    public void Shrink() { if (Mesh is { } m) Select(VfxVertexEdits.Shrink(m, _selected).ToList()); }

    /// <summary>Effect-space positions of the target mesh at the playhead.</summary>
    public Vector3[] Positions() => IsActive ? VfxVertexEdits.EffectPositions(_doc.Sampler, _section, _doc.TimelineFrame) : [];

    /// <summary>One undoable edit on the target mesh (no-op with a reason when unavailable).</summary>
    public bool Apply(string label, Func<VfxSampler, int, VfxFile> edit)
    {
        if (!IsActive || !VfxEditing.CanEdit(_doc)) return false;
        var s = _doc.Sampler; int sec = _section;
        return VfxEditing.Apply(_doc, label, _ => edit(s, sec));
    }

    // ---- operations (Vertices tab / Effect menu / shortcuts) ----
    public bool MoveBy(Vector3 effectDelta) => Apply("Move vertices", (s, i) => VfxVertexEdits.Move(s, i, _selected.ToList(), effectDelta, _doc.TimelineFrame, _allFrames));
    public bool Delete() { var sel = _selected.ToHashSet(); return sel.Count > 0 && Apply("Delete vertices", (s, i) => VfxVertexEdits.Delete(s.File, i, sel)); }
    public bool Merge() { var sel = _selected.ToList(); return sel.Count > 1 && Apply("Merge vertices", (s, i) => VfxVertexEdits.Merge(s.File, i, sel)); }
    public bool SetFaceMaterial(int slot) => Faces() is { Length: > 0 } f && Apply("Assign material slot", (s, i) => Vfx.Editing.VfxEdit.SetFaceMaterial(s.File, i, slot, f));
    public bool SetFaceSmoothing(int group) => Faces() is { Length: > 0 } f && Apply("Assign smoothing group", (s, i) => Vfx.Editing.VfxEdit.SetFaceSmoothing(s.File, i, group, f));
    public bool MakeMorph() => Mesh is { IsMorph: false } && Apply("Make morph mesh", (s, i) => Vfx.Editing.VfxEdit.ToMorph(s.File, i));
    public bool AddMorphFrame() => Mesh is { IsMorph: true } m && Apply("Add morph frame",
        (s, i) => Vfx.Editing.VfxEdit.AddMorphFrame(s.File, i, Math.Clamp(VfxVertexEdits.EditFrame(s, i, _doc.TimelineFrame) + 1, 1, m.Frames.Length)));
    public bool RemoveMorphFrame() => Mesh is { IsMorph: true, Frames.Length: > 1 } && Apply("Remove morph frame",
        (s, i) => Vfx.Editing.VfxEdit.RemoveMorphFrame(s.File, i, VfxVertexEdits.EditFrame(s, i, _doc.TimelineFrame)));
    /// <summary>Scrolling UVs: per-frame offsets from a speed in UV units per second (sets the per-frame-UV data).</summary>
    public bool ScrollUvs(Vector2 perSecond) => Mesh is { } m && Apply("Generate scrolling UVs",
        (s, i) => Vfx.Editing.VfxEdit.GenerateUvScroll(s.File, i, perSecond / Math.Max(1, m.Fps ?? 15)));
    public bool FlipFaces() => Faces() is { Length: > 0 } f && Apply("Flip winding", (s, i) => VfxVertexEdits.FlipFaces(s.File, i, f));
    /// <summary>UV transform of the fully selected faces about their UV centre (this stored frame or all frames).</summary>
    public bool TransformUvs(string label, Func<Vector2, Matrix3x2> make) => Faces() is { Length: > 0 } f && Mesh is { } m && Apply(label, (s, i) =>
    {
        int k = VfxVertexEdits.UvFrame(m, VfxVertexEdits.EditFrame(s, i, _doc.TimelineFrame));
        return VfxVertexEdits.TransformFaceUvs(s.File, i, f, make(VfxVertexEdits.UvCentre(m, f, k)), k, _allFrames);
    });
    public bool OffsetUvs(Vector2 d) => TransformUvs("Offset UVs", _ => Matrix3x2.CreateTranslation(d));
    public bool ScaleUvs(Vector2 k) => TransformUvs("Scale UVs", c => Matrix3x2.CreateScale(k, c));
    public bool RotateUvs(float degrees) => TransformUvs("Rotate UVs", c => Matrix3x2.CreateRotation(degrees * MathF.PI / 180, c));

    /// <summary>Gizmo move snap in effect units (0 = off); used by <see cref="VfxVertexGizmoTarget"/> drags.</summary>
    public float Snap { get => _snap; set { if (Set(ref _snap, Math.Max(0, value))) Notify(); } }
    private float _snap;

    /// <summary>Selection centroid in effect space (at the playhead) or mesh space (stored edit frame).</summary>
    public Vector3 Centroid(bool meshSpace)
    {
        if (!IsActive || _selected.Count == 0) return default;
        if (!meshSpace) return VfxVertexEdits.Centroid(Positions(), _selected);
        var p = Mesh!.DecodePositions(VfxVertexEdits.EditFrame(_doc.Sampler, _section, _doc.TimelineFrame));
        return p is null ? default : VfxVertexEdits.Centroid(p, _selected);
    }

    /// <summary>Pure: moves the selection so one centroid component becomes <paramref name="value"/> (axis 0..2, effect or mesh space).</summary>
    public VfxFile MoveCentroidTo(VfxFile f, int axis, double value, bool meshSpace)
    {
        var s = new VfxSampler(f); var sel = _selected.ToList();
        var p = meshSpace ? ((VfxMesh)f.Sections[_section]).DecodePositions(VfxVertexEdits.EditFrame(s, _section, _doc.TimelineFrame)) : VfxVertexEdits.EffectPositions(s, _section, _doc.TimelineFrame);
        if (p is null || sel.Count == 0) return f;
        var c = VfxVertexEdits.Centroid(p, sel);
        var d = axis switch { 0 => new Vector3((float)value - c.X, 0, 0), 1 => new Vector3(0, (float)value - c.Y, 0), _ => new Vector3(0, 0, (float)value - c.Z) };
        if (d == Vector3.Zero) return f;
        if (meshSpace) d = Vector3.TransformNormal(d, VfxVertexEdits.LocalToEffect(s, _section, _doc.TimelineFrame));
        return VfxVertexEdits.Move(s, _section, sel, d, _doc.TimelineFrame, _allFrames);
    }

    public string ScopeText => _allFrames ? "all frames" : $"this frame (stored frame {VfxVertexEdits.EditFrame(_doc.Sampler, _section, _doc.TimelineFrame)})";
    public int[] SelectedFaceIndices => Faces();
    private int[] Faces() => Mesh is { } m ? VfxVertexEdits.SelectedFaces(m, _selected) : [];
}

/// <summary>Gizmo controller for vertex mode: move/rotate the selected vertices about their effect-space centroid.</summary>
public sealed class VfxVertexGizmoTarget : ObservableObject, IGizmoTarget, IGizmoScaleTarget
{
    private readonly VfxDocument _doc;
    private readonly VfxVertexMode _mode;
    private VfxSampler? _sampler;
    private int[] _verts = [];
    private float _frame;
    private int _handle = GizmoHandle.None;
    private Quaternion _frameRot = Quaternion.Identity;

    internal VfxVertexGizmoTarget(VfxDocument doc, VfxVertexMode mode)
    {
        _doc = doc; _mode = mode;
        mode.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        doc.FrameChanged += (_, _) => { if (!IsDragging) Changed?.Invoke(this, EventArgs.Empty); };
    }

    public event EventHandler? Changed;
    public PoseTool Tool { get; set; } = PoseTool.Move;
    public bool LocalSpace { get; set; }
    public PoseGizmo Gizmo => !_mode.IsActive || _mode.Selected.Count == 0 ? PoseGizmo.None
        : Tool switch { PoseTool.Move => PoseGizmo.Move, PoseTool.Rotate => PoseGizmo.Rotate, PoseTool.Scale => PoseGizmo.Scale, _ => PoseGizmo.None };
    public int ActiveBone => _mode.Section;
    public bool IsDragging => _sampler is not null;
    public int DragHandle => _handle;
    public string? Readout { get; private set; }
    public PoseDragKind MoveDragKind => PoseDragKind.Move;
    public bool HasRadiusHandle => false;
    public float GizmoRadius => 0;

    public bool TryGetPlacement(out Vector3 centre, out Quaternion frame)
    {
        frame = Quaternion.Identity;
        var p = _mode.Positions();
        centre = VfxVertexEdits.Centroid(p, _mode.Selected);
        if (p.Length == 0 || _mode.Selected.Count == 0) return false;
        if (LocalSpace && Matrix4x4.Decompose(VfxVertexEdits.LocalToEffect(_doc.Sampler, _mode.Section, _doc.TimelineFrame), out _, out var r, out _)) frame = r;
        return true;
    }

    public bool BeginDrag(PoseDragKind kind, int handle = GizmoHandle.None)
    {
        if (IsDragging || !_mode.IsActive || _mode.Selected.Count == 0 || !VfxEditing.CanEdit(_doc)) return false;
        _mode.SnapPlayhead();
        TryGetPlacement(out _, out _frameRot);
        _sampler = _doc.Sampler; _verts = _mode.Selected.ToArray(); _frame = _doc.TimelineFrame; _handle = handle;
        _doc.BeginEdit(kind switch { PoseDragKind.Rotate => "Rotate vertices", PoseDragKind.Scale => "Scale vertices", _ => "Move vertices" });
        return true;
    }

    private void Update(Func<VfxSampler, VfxFile> edit, string readout)
    {
        if (_sampler is not { } s) return;
        Readout = readout;
        _doc.UpdateEdit(_ => edit(s));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Vector3 Axis(int axis) => Vector3.Transform(axis switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ }, _frameRot);
    public void UpdateAxisTranslation(int axis, double metres) => UpdateWorldTranslation(Axis(axis) * (float)metres);
    public void UpdateWorldTranslation(Vector3 world)
    {
        if (_mode.Snap > 0) { float g = _mode.Snap; world = new Vector3(MathF.Round(world.X / g) * g, MathF.Round(world.Y / g) * g, MathF.Round(world.Z / g) * g); }
        Update(s => VfxVertexEdits.Move(s, _mode.Section, _verts, world, _frame, _mode.AllFrames), $"{world.X:0.000}, {world.Y:0.000}, {world.Z:0.000}");
    }
    /// <summary>Scale about the selection centroid (R): per axis of the placement frame, or uniform (<see cref="GizmoHandle.Screen"/>).</summary>
    public void UpdateScale(int handle, double factor)
    {
        float k = (float)Math.Max(0.01, factor);
        var sv = GizmoHandle.ScaleVector(handle, k);
        var lin = Matrix4x4.CreateFromQuaternion(Quaternion.Conjugate(_frameRot)) * Matrix4x4.CreateScale(sv) * Matrix4x4.CreateFromQuaternion(_frameRot);
        Update(s => VfxVertexEdits.Transform(s, _mode.Section, _verts, lin, _frame, _mode.AllFrames), $"x{k:0.###}");
    }
    public void UpdateAxisRotation(int axis, double radians) =>
        UpdateWorldRotation(Quaternion.CreateFromAxisAngle(Axis(axis), (float)radians), GizmoHandle.None, radians * 180 / Math.PI);
    public void UpdateWorldRotation(Quaternion world, int handle = GizmoHandle.Free, double? shownDegrees = null) =>
        Update(s => VfxVertexEdits.Transform(s, _mode.Section, _verts, Matrix4x4.CreateFromQuaternion(world), _frame, _mode.AllFrames),
            shownDegrees is { } d ? $"{d:0.0} deg" : "free");
    public bool BeginRadiusDrag() => false;
    public void UpdateRadius(double radius) { }
    public void CommitDrag() { if (_sampler is null) return; _sampler = null; Readout = null; _handle = GizmoHandle.None; _doc.CommitEdit(); Changed?.Invoke(this, EventArgs.Empty); }
    public void CancelDrag() { if (_sampler is null) return; _sampler = null; Readout = null; _handle = GizmoHandle.None; _doc.CancelEdit(); Changed?.Invoke(this, EventArgs.Empty); }
}
