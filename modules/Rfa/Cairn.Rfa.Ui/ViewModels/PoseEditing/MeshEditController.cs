using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels.PoseEditing;

/// <summary>What a mesh tab's gizmo edits.</summary>
public enum MeshGizmoTarget
{
    /// <summary>Nothing it can move is selected.</summary>
    None,
    /// <summary>The selected bone's bind (rest) pose.</summary>
    Bone,
    /// <summary>The selected collision sphere (centre and radius).</summary>
    Sphere,
    /// <summary>The selected prop point (position and orientation).</summary>
    Prop,
}

/// <summary>
/// The viewport gizmos of one mesh document (.v3c): Move and Rotate on the selected bone's bind pose, Move
/// and a radius handle on a collision sphere, Move and Rotate on a prop point. The tool and space are the
/// clip tabs' (<see cref="PoseEditSettings"/>, shared and persisted); "children follow" is the bone editor's
/// (<see cref="BindEditOptions"/>). Every drag is one coalesced undo step through the document's
/// <c>BeginEdit</c> / <c>UpdateEdit</c> / <c>CommitEdit</c> / <c>CancelEdit</c>, each update recomputed from the
/// pre-drag snapshot with the same <c>MeshEdit</c> call the Structure tab's editor makes, and shown live
/// (<see cref="MeshDocumentViewModel.BeginLiveEdit"/>): the viewport and the editor pane's numbers follow the
/// mouse; the tree and the lint are brought up to date when the drag ends.
/// </summary>
/// <remarks>
/// <para>
/// Bones: the gizmo sits on the bone's rest joint, and while a bone is the target with the Move or Rotate
/// tool the scene shows the bind pose (<see cref="SceneViewModel.ForceBindPose"/>; the badge says so when a
/// preview clip is hidden by it). With L the rest local, P the parent's rest world (identity for a root)
/// and W = P ∘ L: Model space sets <c>SetBoneBind(World, (R·W.rot, W.pos + d))</c>; Local and Parent set
/// <c>SetBoneBind(Local, (FrameEdit.RotateLocal(space, L.rot, P.rot, R), L.pos + conj(P.rot)·d))</c>, with
/// the "children follow" option. Several selected bones are edited together (each turned in its own space,
/// all moved by the same model-space delta; each set as a World target computed from the pre-drag skeleton,
/// so the order does not matter), unless children follow is on and one selected bone hangs below another:
/// then only the active bone is edited, and the badge says so.
/// </para>
/// <para>
/// Spheres and prop points: placed in the pose the viewport shows (the bind pose or the preview clip's), so
/// the stored, bone-relative value is converted with the bone's frame in that pose (B):
/// <c>local + conj(B.rot)·d</c>, which puts the model-space point exactly where it was dragged. A prop point
/// turns as <c>FrameEdit.RotateLocal</c> of its orientation (the conjugate of the stored quaternion), written
/// back in the stored hemisphere; <c>MeshEdit.SetPropPoint</c> writes every LOD's copy.
/// </para>
/// </remarks>
public sealed class MeshEditController : ObservableObject, IGizmoTarget
{
    private static readonly ConditionalWeakTable<MeshDocumentViewModel, MeshEditController> Controllers = new();

    /// <summary>The smallest radius the radius handle sets, in metres.</summary>
    public const double MinimumRadius = 0.001;

    private readonly PoseEditSettings _settings;
    private readonly BindEditOptions _bind;
    private PoseTool _tool;
    private OffsetSpace _space;
    private PoseGizmo _gizmo;
    private string _modeText = string.Empty;
    private string? _hint;
    private string? _readout;
    private PoseBadgeKind _badgeKind;
    private MeshGizmoTarget _target;
    private int _targetIndex = -1;
    private bool _reduced;
    private DragState? _drag;
    private V3dFile? _sphereMesh;
    private V3dCollisionSphere[] _spheres = [];

    private MeshEditController(MeshDocumentViewModel document)
    {
        Document = document;
        _settings = document.Shell.PoseSettings;
        _bind = document.Shell.BindOptions;
        _tool = _settings.Tool;
        _space = _settings.Space;
        // Shared switches, followed weakly (the shell's settings never keep a closed document alive).
        PropertyChangedEventManager.AddHandler(_settings, OnSettingsChanged, string.Empty);
        PropertyChangedEventManager.AddHandler(_bind, OnBindOptionsChanged, string.Empty);
        document.Selection.Changed += (_, _) => Refresh();
        document.Structure.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MeshStructureViewModel.Selected)) Refresh();
        };
        document.Scene.MeshChanged += (_, _) => Refresh();
        document.Scene.OverlayChanged += (_, _) => Refresh();
        // The pose moves every frame while a clip plays: the gizmo follows, the badge text does not change.
        document.Scene.PoseChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        Refresh();
    }

    /// <summary>The controller of a mesh document (one per document, created on first use).</summary>
    public static MeshEditController For(MeshDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Controllers.GetValue(document, d => new MeshEditController(d));
    }

    /// <summary>The document edited.</summary>
    public MeshDocumentViewModel Document { get; }

    /// <summary>Raised when what the gizmo layer draws may have changed.</summary>
    public event EventHandler? Changed;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Tool = _settings.Tool;
        Space = _settings.Space;
    }

    private void OnBindOptionsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Raise(nameof(ChildrenFollow));
        Refresh();
    }

    // ── Switches ─────────────────────────────────────────────────────────────

    /// <summary>The active tool (shared with every tab and persisted).</summary>
    public PoseTool Tool
    {
        get => _tool;
        set
        {
            if (!Set(ref _tool, value)) return;
            _settings.Tool = value;
            RaiseAll(nameof(IsSelectTool), nameof(IsRotateTool), nameof(IsMoveTool));
            Refresh();
        }
    }

    public bool IsSelectTool { get => _tool == PoseTool.Select; set { if (value) Tool = PoseTool.Select; } }

    public bool IsRotateTool { get => _tool == PoseTool.Rotate; set { if (value) Tool = PoseTool.Rotate; } }

    public bool IsMoveTool { get => _tool == PoseTool.Move; set { if (value) Tool = PoseTool.Move; } }

    /// <summary>The frame gizmo axes are in (shared with every tab and persisted).</summary>
    public OffsetSpace Space
    {
        get => _space;
        set
        {
            if (!Set(ref _space, value)) return;
            _settings.Space = value;
            Refresh();
        }
    }

    /// <summary>The space switch's choices, worded for a mesh.</summary>
    public IReadOnlyList<PoseSpaceChoice> SpaceChoices { get; } =
    [
        new(OffsetSpace.Local, "Local", "Local: the selected item's own axes (a bone's or a prop point's; a collision sphere has none and uses its bone's)"),
        new(OffsetSpace.Parent, "Parent", "Parent: the axes of the frame it is stored in (a bone's parent; a sphere's or prop point's bone)"),
        new(OffsetSpace.Model, "Model", "Model: the character's axes (X right, Y up, Z forward)"),
    ];

    /// <summary>The tool bar's Select button tooltip.</summary>
    public string SelectToolTip => "Select (Q): click joints, collision spheres and prop points to select them; no gizmo";

    /// <summary>The tool bar's Rotate button tooltip.</summary>
    public string RotateToolTip => "Rotate (E): rings turn the selected bone's bind (rest) pose, or the selected prop point, about the X / Y / Z axes of the chosen space; the outer ring turns about the view, the inside turns freely. Ctrl snaps to 5°.";

    /// <summary>The tool bar's Move button tooltip.</summary>
    public string MoveToolTip => "Move (W): arrows move the selected bone's bind (rest) pose, collision sphere or prop point along the chosen space's axes; the square moves it in the view plane; a sphere's outline handle changes its radius. Ctrl snaps to 1 cm.";

    /// <summary>The space box's tooltip.</summary>
    public string SpaceToolTip => "The axes the gizmo uses: Local (the selected item's own), Parent (the frame it is stored in) or Model (the character's)";

    /// <summary>
    /// Children follow (the bone editor's option, shared): a bone's descendants move and turn with its bind;
    /// off, they stay where they are in the model.
    /// </summary>
    public bool ChildrenFollow
    {
        get => _bind.ChildrenFollow;
        set => _bind.ChildrenFollow = value;
    }

    // ── State shown by the viewport ──────────────────────────────────────────

    /// <summary>The gizmo to draw now.</summary>
    public PoseGizmo Gizmo => _gizmo;

    /// <summary>The badge's first line (what a drag will do), empty when no gizmo applies.</summary>
    public string ModeText => _modeText;

    /// <summary>Why there is no gizmo, or a note on what the viewport shows, or null.</summary>
    public string? Hint => _hint;

    /// <summary>True when the badge shows (the Move or Rotate tool is active).</summary>
    public bool BadgeVisible => _tool != PoseTool.Select;

    /// <summary>Which mode the badge shows.</summary>
    public PoseBadgeKind BadgeKind => _badgeKind;

    /// <summary>The numeric readout while dragging ("X +0.100 m", "Y 30.0°", "Radius 0.250 m"), else null.</summary>
    public string? Readout => _readout;

    /// <summary>True during a drag.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>The handle being dragged, or <see cref="GizmoHandle.None"/>.</summary>
    public int DragHandle => _drag?.Handle ?? GizmoHandle.None;

    /// <summary>The undo label of the drag in progress, or null.</summary>
    public string? DragLabelText => _drag?.Label;

    /// <summary>What the gizmo edits.</summary>
    public MeshGizmoTarget Target => _target;

    /// <summary>The bone, sphere or prop point index the gizmo edits, or -1.</summary>
    public int TargetIndex => _targetIndex;

    /// <summary>The bone the gizmo sits on (a bone target), or -1.</summary>
    public int ActiveBone => _target == MeshGizmoTarget.Bone ? _targetIndex : -1;

    /// <summary>True when several bones are selected but only the active one is edited (children follow on, nested selection).</summary>
    public bool IsReducedToActive => _reduced;

    PoseDragKind IGizmoTarget.MoveDragKind => PoseDragKind.Move;

    /// <summary>True when the move gizmo also shows a collision sphere's radius handle.</summary>
    public bool HasRadiusHandle => _gizmo == PoseGizmo.Move && _target == MeshGizmoTarget.Sphere;

    /// <summary>The selected sphere's radius (live during a drag), in metres.</summary>
    public float GizmoRadius => _target == MeshGizmoTarget.Sphere && SphereAt(_targetIndex) is { } s ? s.Radius : 0;

    /// <summary>
    /// The bones a bind drag edits: the selection (the active bone last), or only the active bone when
    /// children follow is on and one selected bone hangs below another.
    /// </summary>
    public IReadOnlyList<int> EditBones => _target == MeshGizmoTarget.Bone ? BonesFor(_targetIndex, out _) : [];

    private IReadOnlyList<int> BonesFor(int active, out bool reduced)
    {
        reduced = false;
        var skeleton = Document.Scene.Skeleton;
        int count = Math.Min(skeleton.Count, (Document.Scene.Mesh ?? Document.Current).Bones.Length);
        var bones = Document.Selection.Bones.Where(b => b >= 0 && b < count && b != active).ToList();
        bones.Add(active);
        if (bones.Count > 1 && ChildrenFollow && AnyNested(bones, skeleton.EffectiveParents))
        {
            reduced = true;
            return [active];
        }
        return bones;
    }

    private static bool AnyNested(List<int> bones, System.Collections.Immutable.ImmutableArray<int> parents)
    {
        foreach (int bone in bones)
        {
            for (int p = parents[bone], guard = 0; p >= 0 && guard <= parents.Length; p = parents[p], guard++)
            {
                if (bones.Contains(p)) return true;
            }
        }
        return false;
    }

    private V3dCollisionSphere[] Spheres(V3dFile? mesh)
    {
        if (mesh is null) return [];
        if (!ReferenceEquals(mesh, _sphereMesh))
        {
            _sphereMesh = mesh;
            _spheres = [.. mesh.CollisionSpheres];
        }
        return _spheres;
    }

    private V3dCollisionSphere? SphereAt(int index)
    {
        var spheres = Spheres(Document.Scene.Mesh ?? Document.Current);
        return index >= 0 && index < spheres.Length ? spheres[index] : null;
    }

    private V3dPropPoint? PropAt(int index)
    {
        var props = Document.Scene.PropPoints;
        return index >= 0 && index < props.Count ? props[index] : null;
    }

    /// <summary>Where the gizmo sits (see <see cref="IGizmoTarget.TryGetPlacement"/>).</summary>
    public Func<Pose?>? PoseSource { get; set; }

    bool IGizmoTarget.TryGetPlacement(out Vector3 centre, out Quaternion frame) => TryGetPlacement(PoseSource?.Invoke(), out centre, out frame);

    /// <summary>Where the gizmo sits for <paramref name="pose"/>.</summary>
    public bool TryGetPlacement(Pose? pose, out Vector3 centre, out Quaternion frame)
    {
        centre = default;
        frame = Quaternion.Identity;
        ReadOnlySpan<Rigid> world = pose is null ? default : pose.World;
        switch (_target)
        {
            case MeshGizmoTarget.Bone:
            {
                var skeleton = Document.Scene.Skeleton;
                int bone = _targetIndex;
                if (bone < 0 || bone >= skeleton.Count) return false;
                var rest = skeleton.RestWorld[bone];
                int parent = skeleton.EffectiveParents[bone];
                centre = rest.Position;
                frame = _space switch
                {
                    OffsetSpace.Local => rest.Rotation,
                    OffsetSpace.Parent => parent >= 0 ? skeleton.RestWorld[parent].Rotation : Quaternion.Identity,
                    _ => Quaternion.Identity,
                };
                return true;
            }
            case MeshGizmoTarget.Sphere when SphereAt(_targetIndex) is { } sphere:
            {
                var bone = FrameEdit.ParentFrame(world, sphere.BoneIndex);
                centre = bone.TransformPoint(sphere.Position);
                frame = _space == OffsetSpace.Model ? Quaternion.Identity : bone.Rotation;
                return true;
            }
            case MeshGizmoTarget.Prop when PropAt(_targetIndex) is { } prop:
            {
                var bone = FrameEdit.ParentFrame(world, prop.ParentIndex);
                centre = bone.TransformPoint(prop.Position);
                frame = FrameEdit.SpaceAxes(_space, bone.Rotation, FrameEdit.PropOrientation(prop.Rotation));
                return true;
            }
            default:
                return false;
        }
    }

    // ── Drags ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a Rotate or Move drag on the target (labelled "Move bone hand-l bind", "Rotate prop point
    /// 'muzzle_1'", "Move collision sphere 'head'"…); false, with the reason in the status bar, when the gizmo
    /// does not apply.
    /// </summary>
    public bool BeginDrag(PoseDragKind kind, int handle = GizmoHandle.None)
    {
        if (_drag is not null) CancelDrag();
        Refresh();
        bool ok = kind switch
        {
            PoseDragKind.Rotate => _gizmo == PoseGizmo.Rotate,
            PoseDragKind.Move => _gizmo == PoseGizmo.Move,
            _ => false,
        };
        if (!ok)
        {
            if (_hint is { } why) Document.ShowStatus(why);
            return false;
        }
        return Start(kind == PoseDragKind.Rotate ? DragKind.Rotate : DragKind.Move, handle);
    }

    /// <summary>Starts a drag of the selected sphere's radius handle ("Resize collision sphere 'head'").</summary>
    public bool BeginRadiusDrag()
    {
        if (_drag is not null) CancelDrag();
        Refresh();
        if (!HasRadiusHandle)
        {
            if (_hint is { } why) Document.ShowStatus(why);
            return false;
        }
        return Start(DragKind.Radius, GizmoHandle.Radius);
    }

    private bool Start(DragKind kind, int handle)
    {
        var doc = Document;
        doc.Playback.Pause();
        var mesh = doc.Current;
        var state = new DragState(kind, handle, _target, _targetIndex, _space);
        ReadOnlySpan<Rigid> world = doc.Scene.Pose is { } pose ? pose.World : default;
        try
        {
            switch (_target)
            {
                case MeshGizmoTarget.Bone:
                {
                    var skeleton = Skeleton.FromFile(mesh);
                    var bones = BonesFor(_targetIndex, out _);
                    state.Bones = [.. bones];
                    state.World = new Rigid[bones.Count];
                    state.Local = new Rigid[bones.Count];
                    state.ParentRotation = new Quaternion[bones.Count];
                    for (int i = 0; i < bones.Count; i++)
                    {
                        int b = bones[i];
                        int p = skeleton.EffectiveParents[b];
                        state.World[i] = skeleton.RestWorld[b];
                        state.Local[i] = skeleton.RestLocal[b];
                        state.ParentRotation[i] = p >= 0 ? skeleton.RestWorld[p].Rotation : Quaternion.Identity;
                    }
                    int last = bones.Count - 1;
                    state.Frame = _space switch
                    {
                        OffsetSpace.Local => state.World[last].Rotation,
                        OffsetSpace.Parent => state.ParentRotation[last],
                        _ => Quaternion.Identity,
                    };
                    state.Follow = ChildrenFollow;
                    state.Name = mesh.Bones[_targetIndex].Name.Text;
                    break;
                }
                case MeshGizmoTarget.Sphere:
                {
                    var sphere = SphereEditor.Sphere(mesh, _targetIndex);
                    state.Parent = FrameEdit.ParentFrame(world, sphere.BoneIndex);
                    state.Position = sphere.Position;
                    state.Radius = sphere.Radius;
                    state.Bone = sphere.BoneIndex;
                    state.Name = sphere.Name.Text;
                    state.Frame = _space == OffsetSpace.Model ? Quaternion.Identity : state.Parent.Rotation;
                    break;
                }
                case MeshGizmoTarget.Prop:
                {
                    var prop = PropPointEditor.Prop(mesh, _targetIndex);
                    state.Parent = FrameEdit.ParentFrame(world, prop.ParentIndex);
                    state.Position = prop.Position;
                    state.Stored = prop.Rotation;
                    state.Bone = prop.ParentIndex;
                    state.Name = prop.Name.Text;
                    state.Frame = FrameEdit.SpaceAxes(_space, state.Parent.Rotation, FrameEdit.PropOrientation(prop.Rotation));
                    break;
                }
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            doc.ShowStatus("Gizmo: " + ex.Message);
            return false;
        }
        state.Label = Label(state);
        doc.BeginEdit(state.Label);
        if (!doc.IsEditing) return false;
        doc.BeginLiveEdit();
        _drag = state;
        SetReadout(null);
        Raise(nameof(IsDragging));
        Raise(nameof(DragHandle));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string Label(DragState d) => d.Target switch
    {
        MeshGizmoTarget.Bone => d.Bones.Length > 1
            ? $"{Verb(d)} {d.Bones.Length.ToString(CultureInfo.CurrentCulture)} bone binds{(d.Follow ? " with their children" : string.Empty)}"
            : $"{Verb(d)} bone {d.Name} bind{(d.Follow ? " with its children" : string.Empty)}",
        MeshGizmoTarget.Sphere => d.Kind == DragKind.Radius ? $"Resize collision sphere '{d.Name}'" : $"Move collision sphere '{d.Name}'",
        _ => $"{Verb(d)} prop point '{d.Name}'",
    };

    private static string Verb(DragState d) => d.Kind == DragKind.Rotate ? "Rotate" : "Move";

    private static Vector3 Axis(int handle) => handle switch
    {
        GizmoHandle.X => Vector3.UnitX,
        GizmoHandle.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>Rotation about the space's axis <paramref name="axis"/> by <paramref name="radians"/>.</summary>
    public void UpdateAxisRotation(int axis, double radians)
    {
        if (_drag is not { Kind: DragKind.Rotate } d) return;
        Apply(d, Quat.FromAxisAngle(Axis(axis), (float)radians), Vector3.Zero,
            string.Format(CultureInfo.CurrentCulture, "{0} {1:0.0}°", GizmoHandle.Name(axis), radians * 180 / Math.PI));
    }

    /// <summary>A model-space rotation (screen ring, trackball), expressed in the gizmo's space frame.</summary>
    public void UpdateWorldRotation(Quaternion world, int handle = GizmoHandle.Free, double? shownDegrees = null)
    {
        if (_drag is not { Kind: DragKind.Rotate } d) return;
        world = Quat.Normalize(world);
        var delta = Quat.Mul(Quat.Conj(d.Frame), Quat.Mul(world, d.Frame));
        double degrees = shownDegrees ?? Quat.AngleDegrees(Quaternion.Identity, world);
        Apply(d, delta, Vector3.Zero, string.Format(CultureInfo.CurrentCulture, "{0} {1:0.0}°", GizmoHandle.Name(handle), degrees));
    }

    /// <summary>A translation of <paramref name="metres"/> along the space's axis <paramref name="axis"/>.</summary>
    public void UpdateAxisTranslation(int axis, double metres)
    {
        if (_drag is not { Kind: DragKind.Move } d) return;
        Apply(d, Quaternion.Identity, Quat.Rotate(d.Frame, Axis(axis) * (float)metres),
            string.Format(CultureInfo.CurrentCulture, "{0} {1:+0.000;-0.000;0.000} m", GizmoHandle.Name(axis), metres));
    }

    /// <summary>A model-space translation (screen-plane drag).</summary>
    public void UpdateWorldTranslation(Vector3 world)
    {
        if (_drag is not { Kind: DragKind.Move } d) return;
        Apply(d, Quaternion.Identity, world, string.Format(CultureInfo.CurrentCulture, "{0:0.000} m", world.Length()));
    }

    /// <summary>The radius handle's new radius (at least <see cref="MinimumRadius"/>).</summary>
    public void UpdateRadius(double radius)
    {
        if (_drag is not { Kind: DragKind.Radius } d || !EnsureEditing()) return;
        float r = (float)Math.Max(MinimumRadius, double.IsFinite(radius) ? radius : d.Radius);
        Document.UpdateEdit(m => MeshEdit.SetCollisionSphere(m, d.Index, d.Name, d.Bone, d.Position, r));
        SetReadout(string.Format(CultureInfo.CurrentCulture, "Radius {0:0.000} m", r));
    }

    private void Apply(DragState d, Quaternion delta, Vector3 move, string readout)
    {
        if (!EnsureEditing()) return;
        Document.UpdateEdit(m => d.Target switch
        {
            MeshGizmoTarget.Bone => Bind(m, d, delta, move),
            MeshGizmoTarget.Sphere => MeshEdit.SetCollisionSphere(m, d.Index, d.Name, d.Bone, FrameEdit.TranslateLocal(d.Position, d.Parent.Rotation, move), d.Radius),
            MeshGizmoTarget.Prop => Prop(m, d, delta, move),
            _ => m,
        });
        SetReadout(readout);
    }

    /// <summary>The bind edit of a drag, on the pre-drag mesh (see the class remarks).</summary>
    private static V3dFile Bind(V3dFile mesh, DragState d, Quaternion delta, Vector3 move)
    {
        bool rotate = d.Kind == DragKind.Rotate;
        if (d.Bones.Length == 1)
        {
            int bone = d.Bones[0];
            var w = d.World[0];
            var l = d.Local[0];
            if (d.Space == OffsetSpace.Model)
            {
                var target = rotate ? new Rigid(Quat.Mul(delta, w.Rotation), w.Position) : new Rigid(w.Rotation, w.Position + move);
                return MeshEdit.SetBoneBind(mesh, bone, target, BindSpace.World, d.Follow);
            }
            var local = rotate
                ? new Rigid(FrameEdit.RotateLocal(d.Space, l.Rotation, d.ParentRotation[0], delta), l.Position)
                : new Rigid(l.Rotation, FrameEdit.TranslateLocal(l.Position, d.ParentRotation[0], move));
            return MeshEdit.SetBoneBind(mesh, bone, local, BindSpace.Local, d.Follow);
        }
        for (int i = 0; i < d.Bones.Length; i++)
        {
            var w = d.World[i];
            Rigid target;
            if (!rotate) target = new Rigid(w.Rotation, w.Position + move);
            else if (d.Space == OffsetSpace.Model) target = new Rigid(Quat.Mul(delta, w.Rotation), w.Position);
            else target = new Rigid(Quat.Mul(d.ParentRotation[i], FrameEdit.RotateLocal(d.Space, d.Local[i].Rotation, d.ParentRotation[i], delta)), w.Position);
            mesh = MeshEdit.SetBoneBind(mesh, d.Bones[i], target, BindSpace.World, d.Follow);
        }
        return mesh;
    }

    private static V3dFile Prop(V3dFile mesh, DragState d, Quaternion delta, Vector3 move)
    {
        var stored = d.Kind == DragKind.Rotate
            ? FrameEdit.PropStored(FrameEdit.RotateLocal(d.Space, FrameEdit.PropOrientation(d.Stored), d.Parent.Rotation, delta), d.Stored)
            : d.Stored;
        var position = d.Kind == DragKind.Move ? FrameEdit.TranslateLocal(d.Position, d.Parent.Rotation, move) : d.Position;
        return MeshEdit.SetPropPoint(mesh, d.Index, d.Name, d.Bone, stored, position);
    }

    /// <summary>Ends the drag as one undo step; the tree and the lint catch up.</summary>
    public void CommitDrag()
    {
        if (_drag is null) return;
        _drag = null;
        Document.EndLiveEdit(commit: true);
        EndDrag();
    }

    /// <summary>Abandons the drag, restoring the pre-drag snapshot exactly (Esc).</summary>
    public void CancelDrag()
    {
        if (_drag is null) return;
        _drag = null;
        Document.EndLiveEdit(commit: false);
        EndDrag();
    }

    private void EndDrag()
    {
        SetReadout(null);
        Raise(nameof(IsDragging));
        Raise(nameof(DragHandle));
        Refresh();
    }

    // The edit can be committed from elsewhere (another Apply, undo) mid-drag: then the drag is over.
    private bool EnsureEditing()
    {
        if (Document.IsEditing) return true;
        _drag = null;
        Document.EndLiveEdit(commit: true);
        EndDrag();
        return false;
    }

    private void SetReadout(string? text)
    {
        if (_readout == text) return;
        _readout = text;
        Raise(nameof(Readout));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── State refresh ────────────────────────────────────────────────────────

    private (MeshGizmoTarget Target, int Index) ResolveTarget()
    {
        var doc = Document;
        var mesh = doc.Scene.Mesh ?? doc.Current;
        var node = doc.Structure.Selected?.Node;
        if (node is { Kind: MeshNodeKind.CollisionSphere } s && s.Index >= 0 && s.Index < Spheres(mesh).Length) return (MeshGizmoTarget.Sphere, s.Index);
        if (node is { Kind: MeshNodeKind.PropPoint } p && p.Index >= 0 && p.Index < doc.Scene.PropPoints.Count) return (MeshGizmoTarget.Prop, p.Index);
        int bone = doc.Selection.Active;
        if (bone >= 0 && bone < mesh.Bones.Length && bone < doc.Scene.Skeleton.Count) return (MeshGizmoTarget.Bone, bone);
        return (MeshGizmoTarget.None, -1);
    }

    private string BoneName(int bone)
    {
        var bones = (Document.Scene.Mesh ?? Document.Current).Bones;
        return bone >= 0 && bone < bones.Length ? bones[bone].Name.Text : bone.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>"bone spine", or "the model" for an unattached item (or one naming a missing bone).</summary>
    private string Owner(int bone) =>
        bone >= 0 && bone < (Document.Scene.Mesh ?? Document.Current).Bones.Length ? "bone " + BoneName(bone) : "the model";

    /// <summary>A note for an attached sphere or prop point shown in a clip's pose, else null.</summary>
    private string? PoseNote(int bone) =>
        !Document.Scene.ShowingBindPose && bone >= 0 && bone < Document.Scene.Skeleton.Count
            ? $"Placed in the preview clip's pose at this frame; the file stores it relative to {BoneName(bone)}, so it follows the bone in every pose."
            : null;

    /// <summary>Recomputes the target, the gizmo, the badge text and whether the scene shows the bind pose.</summary>
    public void Refresh()
    {
        var (target, index) = ResolveTarget();
        var gizmo = PoseGizmo.None;
        string? hint = null;
        string mode = string.Empty;
        var kind = PoseBadgeKind.None;
        bool reduced = false;
        bool bindPose = false;
        if (_tool != PoseTool.Select)
        {
            bool rotate = _tool == PoseTool.Rotate;
            var scene = Document.Scene;
            if (Document.IsReadOnly)
            {
                hint = "Static meshes (.v3m) are read-only: nothing here can be moved, rotated or resized.";
            }
            else
            {
                switch (target)
                {
                    case MeshGizmoTarget.Bone:
                    {
                        bindPose = true;
                        gizmo = rotate ? PoseGizmo.Rotate : PoseGizmo.Move;
                        kind = PoseBadgeKind.Mesh;
                        var bones = BonesFor(index, out reduced);
                        string verb = rotate ? "turns" : "moves";
                        string children = ChildrenFollow ? "follow" : "stay where they are";
                        mode = bones.Count > 1
                            ? $"Bind pose — {verb} the rest pose of {bones.Count.ToString(CultureInfo.CurrentCulture)} bones; their children {children}"
                            : $"Bind pose — {verb} {BoneName(index)}'s rest pose; its children {children}";
                        if (reduced)
                            hint = $"Children follow is on and a selected bone hangs below another: only {BoneName(index)}, the bone selected last, is edited.";
                        else if (scene.Clip is not null && !scene.Display.BindPose)
                            hint = "The viewport shows the bind pose while a bone's bind is edited; Select (Q) brings the preview clip back.";
                        break;
                    }
                    case MeshGizmoTarget.Sphere when SphereAt(index) is { } sphere:
                        if (rotate)
                        {
                            hint = $"Collision spheres have no orientation: press W to move '{sphere.Name.Text}', and drag its outline to change the radius.";
                        }
                        else
                        {
                            gizmo = PoseGizmo.Move;
                            kind = PoseBadgeKind.Mesh;
                            mode = $"Moves collision sphere '{sphere.Name.Text}' (stored relative to {Owner(sphere.BoneIndex)}); drag its outline to change the radius";
                            hint = PoseNote(sphere.BoneIndex);
                        }
                        break;
                    case MeshGizmoTarget.Prop when PropAt(index) is { } prop:
                        gizmo = rotate ? PoseGizmo.Rotate : PoseGizmo.Move;
                        kind = PoseBadgeKind.Mesh;
                        mode = $"{(rotate ? "Turns" : "Moves")} prop point '{prop.Name.Text}' (stored relative to {Owner(prop.ParentIndex)}; every LOD's copy follows)";
                        hint = PoseNote(prop.ParentIndex);
                        break;
                    default:
                        hint = rotate
                            ? "Click a joint to turn that bone's bind pose, or a prop point to turn it (prop points show with the toolbar's prop point toggle)."
                            : "Click a joint to move that bone's bind pose, or a collision sphere or prop point to move it (they show with the toolbar's sphere and prop point toggles).";
                        break;
                }
            }
        }

        bool changed = gizmo != _gizmo || hint != _hint || mode != _modeText || kind != _badgeKind || target != _target || index != _targetIndex || reduced != _reduced;
        _gizmo = gizmo;
        _hint = hint;
        _modeText = mode;
        _badgeKind = kind;
        _target = target;
        _targetIndex = index;
        _reduced = reduced;
        if (changed)
        {
            RaiseAll(nameof(Gizmo), nameof(Hint), nameof(ModeText), nameof(BadgeKind), nameof(BadgeVisible), nameof(Target), nameof(TargetIndex),
                nameof(ActiveBone), nameof(HasRadiusHandle), nameof(IsReducedToActive));
        }
        // The bind is edited where it is drawn: the bind pose shows while a bone is the gizmo's target (the clip pauses).
        var sceneToSet = Document.Scene;
        if (bindPose && !sceneToSet.ForceBindPose && Document.Playback.IsPlaying) Document.Playback.Pause();
        sceneToSet.ForceBindPose = bindPose;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private enum DragKind
    {
        Rotate,
        Move,
        Radius,
    }

    private sealed class DragState(DragKind kind, int handle, MeshGizmoTarget target, int index, OffsetSpace space)
    {
        public DragKind Kind { get; } = kind;
        public int Handle { get; } = handle;
        public MeshGizmoTarget Target { get; } = target;
        public int Index { get; } = index;
        public OffsetSpace Space { get; } = space;
        public Quaternion Frame { get; set; } = Quaternion.Identity;
        public string Label { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        // Bones: pre-drag rest world, rest local and parent rest rotation of each edited bone (active last).
        public int[] Bones { get; set; } = [];
        public Rigid[] World { get; set; } = [];
        public Rigid[] Local { get; set; } = [];
        public Quaternion[] ParentRotation { get; set; } = [];
        public bool Follow { get; set; }
        // Spheres and prop points: the bone's frame in the pose shown, and the stored values.
        public Rigid Parent { get; set; } = Rigid.Identity;
        public Vector3 Position { get; set; }
        public Quaternion Stored { get; set; } = Quaternion.Identity;
        public float Radius { get; set; }
        public int Bone { get; set; } = -1;
    }
}
