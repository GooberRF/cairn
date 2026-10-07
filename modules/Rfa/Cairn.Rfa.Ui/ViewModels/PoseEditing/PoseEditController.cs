using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.ViewModels.PoseEditing;


/// <summary>A two-bone IK chain resolved to bone indices of the preview skeleton.</summary>
/// <param name="Upper">Shoulder / hip bone (its joint stays put).</param>
/// <param name="Lower">Elbow / knee bone.</param>
/// <param name="End">Hand / foot bone (dragged; keeps its model-space rotation).</param>
/// <param name="PoleBias">The chain's bend bias (model-space metres).</param>
/// <param name="PoleFade">Metres over which the bias fades in.</param>
public sealed record IkChainBones(int Upper, int Lower, int End, Vector3 PoleBias, float PoleFade);

/// <summary>A choice of the space switch.</summary>
public sealed record PoseSpaceChoice(OffsetSpace Value, string Label, string ToolTip)
{
    public override string ToString() => Label;
}

/// <summary>
/// Pose editing for one clip document (DESIGN.md section 6, "Pose editing"): the tool and space
/// switches, auto-key vs layer edit, the IK toggle and the layer range, which gizmo applies to the
/// selected bone and why not, and the drags themselves. The viewport only turns mouse input into
/// <see cref="BeginDrag"/> / <c>Update…</c> / <see cref="CommitDrag"/> / <see cref="CancelDrag"/>;
/// everything here is testable without a mouse (the <c>pose</c> self-test drives it).
/// </summary>
/// <remarks>
/// <para>
/// Every drag is one coalesced undo step (<see cref="DocumentViewModel{T}.BeginEdit"/>): each update is
/// recomputed from the pre-drag snapshot. A rotation is applied as an <see cref="OffsetSpace"/> delta
/// <c>R</c> to each bone's local rotation <c>L</c>: Local <c>L R</c>, Parent <c>R L</c>, Model
/// <c>Pw^-1 R Pw L</c> (<c>Pw</c> = the parent's model rotation at the playhead, pre-drag), exactly as
/// <see cref="ClipEdit.OffsetBone"/> does. Ring drags give <c>R</c> about the space's own axis; screen
/// and free drags give a model-space rotation that is expressed in the active bone's space frame.
/// </para>
/// <para>
/// Auto-key on: <see cref="ClipEdit.KeyPoseAtTime"/> splits the track at the playhead once (so the
/// motion elsewhere is unchanged), then each update sets that one key (<see cref="ClipEdit.SetRotation"/>
/// / <see cref="ClipEdit.SetPosition"/>). Auto-key off: <see cref="ClipEdit.OffsetBone"/> /
/// <see cref="ClipEdit.OffsetBones"/> on the pre-drag clip (every key of the bone), optionally limited
/// to a range with falloff. IK drags always key at the playhead.
/// </para>
/// </remarks>
public sealed class PoseEditController : ObservableObject, IGizmoTarget
{
    private static readonly ConditionalWeakTable<ClipDocumentViewModel, PoseEditController> Controllers = new();

    /// <summary>Angle snap step with Ctrl held, in degrees.</summary>
    public const double AngleSnapDegrees = 5;

    /// <summary>Distance snap step with Ctrl held, in metres.</summary>
    public const double DistanceSnapMetres = 0.01;

    private PoseTool _tool = PoseTool.Select;
    private OffsetSpace _space = OffsetSpace.Local;
    private bool _ikEnabled = true;
    private bool _rangeEnabled;
    private int _rangeFrom;
    private int _rangeTo;
    private int _falloff;
    private bool _rangeInitialised;
    private PoseGizmo _gizmo;
    private string _modeText = string.Empty;
    private string? _hint;
    private string? _readout;
    private PoseBadgeKind _badgeKind;
    private bool _lastAutoKey;
    private DragState? _drag;
    private RfaClip? _ghostClip;
    private Skeleton? _chainSkeleton;
    private IReadOnlyList<IkChainBones> _chains = [];

    private PoseEditController(ClipDocumentViewModel document)
    {
        Document = document;
        // Tool, space and IK are app-wide and persisted: start from the shared values and follow them
        // (weakly, so the shell's settings never keep a closed document alive).
        _settings = document.Shell.PoseSettings;
        _tool = _settings.Tool;
        _space = _settings.Space;
        _ikEnabled = _settings.IkEnabled;
        System.ComponentModel.PropertyChangedEventManager.AddHandler(_settings, OnPoseSettingsChanged, string.Empty);
        document.Scene.PoseChanged += (_, _) => OnSceneChanged();
        document.Scene.OverlayChanged += (_, _) => OnSceneChanged();
        document.PreviewSkeletonChanged += (_, _) => OnSceneChanged();
        Refresh();
    }

    /// <summary>The controller of a clip document (one per document, created on first use).</summary>
    public static PoseEditController For(ClipDocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Controllers.GetValue(document, d => new PoseEditController(d));
    }

    /// <summary>The document edited.</summary>
    public ClipDocumentViewModel Document { get; }

    private readonly PoseEditSettings _settings;

    /// <summary>Another document (or the settings) changed a shared switch: take it over.</summary>
    private void OnPoseSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Tool = _settings.Tool;
        Space = _settings.Space;
        IkEnabled = _settings.IkEnabled;
    }

    /// <summary>Raised when what the gizmo layer draws may have changed (tool, space, selection, drag).</summary>
    public event EventHandler? Changed;

    // ── Switches ─────────────────────────────────────────────────────────────

    /// <summary>The active tool.</summary>
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

    /// <summary>The frame gizmo axes and offsets are in.</summary>
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

    /// <summary>The space switch's choices.</summary>
    public IReadOnlyList<PoseSpaceChoice> SpaceChoices { get; } =
    [
        new(OffsetSpace.Local, "Local", "Local: the bone's own axes (a rotation turns the bone about itself)"),
        new(OffsetSpace.Parent, "Parent", "Parent: the parent bone's axes"),
        new(OffsetSpace.Model, "Model", "Model: the character's axes (X right, Y up, Z forward)"),
    ];

    /// <summary>The tool bar's Select button tooltip.</summary>
    public string SelectToolTip => "Select (Q): click joints to select bones; no gizmo";

    /// <summary>The tool bar's Rotate button tooltip.</summary>
    public string RotateToolTip => "Rotate (E): rings turn the selected bone(s) about the X / Y / Z axes of the chosen space; the outer ring turns about the view, the inside turns freely. Ctrl snaps to 5°.";

    /// <summary>The tool bar's Move button tooltip.</summary>
    public string MoveToolTip => "Move (W): arrows move the root, or a bone whose position is animated, along the chosen space's axes; the square moves in the view plane. Ctrl snaps to 1 cm. With IK on, drags a hand or foot.";

    /// <summary>The space box's tooltip.</summary>
    public string SpaceToolTip => "The axes the gizmo and the layer edit use: Local (the bone's own), Parent (its parent's) or Model (the character's)";

    /// <summary>
    /// Auto-key (persisted, default on): a manipulation keys the selected bone(s) at the playhead.
    /// Off: a layer edit that offsets every key of the bone.
    /// </summary>
    public bool AutoKey
    {
        get => Document.Display.AutoKey;
        set => Document.Display.AutoKey = value;
    }

    /// <summary>Two-bone IK drag with the move tool on a limb's end (when the rig defines the chain).</summary>
    public bool IkEnabled
    {
        get => _ikEnabled;
        set
        {
            if (!Set(ref _ikEnabled, value)) return;
            _settings.IkEnabled = value;
            Refresh();
        }
    }

    // ── Layer range (per document) ───────────────────────────────────────────

    /// <summary>Limits a layer edit to [<see cref="RangeFromTicks"/>, <see cref="RangeToTicks"/>] with falloff.</summary>
    public bool RangeEnabled
    {
        get => _rangeEnabled;
        set
        {
            if (value && !_rangeInitialised) InitialiseRange();
            if (!Set(ref _rangeEnabled, value)) return;
            Refresh();
        }
    }

    /// <summary>Range start in ticks.</summary>
    public int RangeFromTicks
    {
        get => _rangeFrom;
        set
        {
            if (!Set(ref _rangeFrom, value)) return;
            _rangeInitialised = true;
            Raise(nameof(RangeFrom));
            Refresh();
        }
    }

    /// <summary>Range end in ticks.</summary>
    public int RangeToTicks
    {
        get => _rangeTo;
        set
        {
            if (!Set(ref _rangeTo, value)) return;
            _rangeInitialised = true;
            Raise(nameof(RangeTo));
            Refresh();
        }
    }

    /// <summary>Falloff in ticks (smoothstep on each side of the range).</summary>
    public int FalloffTicks
    {
        get => _falloff;
        set
        {
            if (!Set(ref _falloff, Math.Max(0, value))) return;
            Raise(nameof(Falloff));
            Refresh();
        }
    }

    /// <summary>Range start in the user's time unit (the options flyout).</summary>
    public double RangeFrom { get => TimeFormat.ToUnit(_rangeFrom, Unit); set => RangeFromTicks = TimeFormat.FromUnit(value, Unit); }

    /// <summary>Range end in the user's time unit.</summary>
    public double RangeTo { get => TimeFormat.ToUnit(_rangeTo, Unit); set => RangeToTicks = TimeFormat.FromUnit(value, Unit); }

    /// <summary>Falloff in the user's time unit.</summary>
    public double Falloff { get => TimeFormat.ToUnit(_falloff, Unit); set => FalloffTicks = TimeFormat.FromUnit(value, Unit); }

    /// <summary>The time unit's suffix ("f", "s", "ticks").</summary>
    public string UnitSuffix => TimeFormat.Suffix(Unit);

    /// <summary>Decimals for the time boxes.</summary>
    public int UnitDecimals => TimeFormat.Decimals(Unit);

    /// <summary>Puts the range start at the playhead.</summary>
    public void RangeFromPlayhead() => RangeFromTicks = PlayheadTick;

    /// <summary>Puts the range end at the playhead.</summary>
    public void RangeToPlayhead() => RangeToTicks = PlayheadTick;

    private TimeUnit Unit => Document.Shell.TimeUnit;

    private void InitialiseRange()
    {
        _rangeInitialised = true;
        var clip = Document.Current;
        int t = PlayheadTick;
        _rangeFrom = Math.Max(clip.StartTime, t - 5 * RfaClip.TicksPerFrame);
        _rangeTo = Math.Min(clip.EndTime, t + 5 * RfaClip.TicksPerFrame);
        _falloff = 3 * RfaClip.TicksPerFrame;
        RaiseAll(nameof(RangeFrom), nameof(RangeTo), nameof(Falloff), nameof(RangeFromTicks), nameof(RangeToTicks), nameof(FalloffTicks));
    }

    // ── State shown by the viewport ──────────────────────────────────────────

    /// <summary>The gizmo to draw on <see cref="ActiveBone"/> now.</summary>
    public PoseGizmo Gizmo => _gizmo;

    /// <summary>The badge's first line ("Auto-key — keys hand-l at 12 f"), empty when no gizmo applies.</summary>
    public string ModeText => _modeText;

    /// <summary>Why there is no gizmo (or a note), or null.</summary>
    public string? Hint => _hint;

    /// <summary>True when the badge shows (a pose tool is active).</summary>
    public bool BadgeVisible => _tool != PoseTool.Select;

    /// <summary>Which mode the badge shows.</summary>
    public PoseBadgeKind BadgeKind => _badgeKind;

    /// <summary>True while a layer edit (auto-key off) is what a drag would do.</summary>
    public bool IsLayerMode => !AutoKey && !IsIkMove;

    /// <summary>The numeric readout while dragging ("X 30.0°", "0.125 m"), else null.</summary>
    public string? Readout => _readout;

    /// <summary>True during a drag.</summary>
    public bool IsDragging => _drag is not null;

    /// <summary>The handle being dragged (<see cref="GizmoHandle"/>), or <see cref="GizmoHandle.None"/>.</summary>
    public int DragHandle => _drag?.Handle ?? GizmoHandle.None;

    /// <summary>The preview skeleton when it fits the clip, else null.</summary>
    public Skeleton? Skeleton => Document.FittingSkeleton;

    /// <summary>The bone the gizmo sits on (the active selected bone), or -1.</summary>
    public int ActiveBone
    {
        get
        {
            int bone = Document.Selection.Active;
            return Skeleton is { } s && bone >= 0 && bone < s.Count && bone < Document.Current.BoneCount ? bone : -1;
        }
    }

    /// <summary>The bones a rotation edits: the selection (valid indices), the active bone last.</summary>
    public IReadOnlyList<int> EditBones
    {
        get
        {
            int count = Math.Min(Document.Current.BoneCount, Skeleton?.Count ?? 0);
            return [.. Document.Selection.Bones.Where(b => b >= 0 && b < count)];
        }
    }

    /// <summary>The playhead as a whole tick inside the clip's range (where auto-key writes).</summary>
    public int PlayheadTick
    {
        get
        {
            var clip = Document.Current;
            int t = (int)Math.Round(Document.Playback.Time);
            return clip.EndTime >= clip.StartTime ? Math.Clamp(t, clip.StartTime, clip.EndTime) : t;
        }
    }

    /// <summary>True when the move tool drives the active bone's IK chain.</summary>
    public bool IsIkMove => _tool == PoseTool.Move && _ikEnabled && ChainFor(ActiveBone) is not null;

    /// <summary>
    /// True when the bone has a move gizmo: the root (no parent), or a bone whose position is animated
    /// (more than one distinct position key).
    /// </summary>
    public bool CanMove(int bone)
    {
        var clip = Document.Current;
        if (bone < 0 || bone >= clip.BoneCount) return false;
        var skeleton = Skeleton;
        if (skeleton is not null && bone < skeleton.Count && skeleton.EffectiveParents[bone] < 0) return true;
        var keys = clip.Bones[bone].PositionKeys;
        for (int i = 1; i < keys.Length; i++)
        {
            if (keys[i].Position != keys[0].Position) return true;
        }
        return false;
    }

    /// <summary>The rig's two-bone IK chain whose END is <paramref name="bone"/>, or null.</summary>
    public IkChainBones? ChainFor(int bone)
    {
        if (bone < 0) return null;
        foreach (var chain in Chains())
        {
            if (chain.End == bone) return chain;
        }
        return null;
    }

    private IReadOnlyList<IkChainBones> Chains()
    {
        var skeleton = Skeleton;
        if (skeleton is null) return [];
        if (ReferenceEquals(skeleton, _chainSkeleton)) return _chains;
        _chainSkeleton = skeleton;
        var list = new List<IkChainBones>();
        try
        {
            var profile = RigProfiles.For(skeleton);
            var names = skeleton.Names;
            var parents = skeleton.EffectiveParents;
            foreach (var chain in profile.IkChains)
            {
                int u = profile.IndexOf(names, chain.Upper), l = profile.IndexOf(names, chain.Lower), e = profile.IndexOf(names, chain.End);
                if (u < 0 || l < 0 || e < 0 || parents[e] != l || parents[l] != u) continue;
                list.Add(new IkChainBones(u, l, e, chain.PoleBias, (float)profile.IkPoleFade));
            }
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException) { }
        _chains = list;
        return _chains;
    }

    /// <summary>
    /// The model-space rotation of the axes the gizmo shows for <paramref name="bone"/> in the current
    /// space: the bone's own (Local), its parent's (Parent) or the identity (Model).
    /// </summary>
    public Quaternion SpaceFrame(Pose pose, int bone) => SpaceFrame(pose, bone, _space, Skeleton ?? pose.Skeleton);

    private static Quaternion SpaceFrame(Pose pose, int bone, OffsetSpace space, Skeleton skeleton)
    {
        if (bone < 0 || bone >= pose.World.Length) return Quaternion.Identity;
        switch (space)
        {
            case OffsetSpace.Local:
                return pose.World[bone].Rotation;
            case OffsetSpace.Parent:
                int p = skeleton.EffectiveParents[bone];
                return p >= 0 && p < pose.World.Length ? pose.World[p].Rotation : Quaternion.Identity;
            default:
                return Quaternion.Identity;
        }
    }

    /// <summary>
    /// A bone's local rotation after the space delta <paramref name="delta"/> (the composition
    /// <see cref="ClipEdit.OffsetBone"/> uses): Local <c>L R</c>, Parent <c>R L</c>, Model <c>Pw^-1 R Pw L</c>.
    /// </summary>
    public static Quaternion ComposeRotation(OffsetSpace space, Quaternion local, Quaternion parentWorld, Quaternion delta) =>
        FrameEdit.RotateLocal(space, local, parentWorld, delta);

    // ── IGizmoTarget (the gizmo layer's view of this controller) ─────────────

    /// <summary>The move gizmo drags the IK chain (<see cref="IsIkMove"/>) or moves the bone.</summary>
    PoseDragKind IGizmoTarget.MoveDragKind => IsIkMove ? PoseDragKind.Ik : PoseDragKind.Move;

    /// <summary>A clip's bones have no radius handle.</summary>
    bool IGizmoTarget.HasRadiusHandle => false;

    float IGizmoTarget.GizmoRadius => 0;

    /// <summary>Supplies the pose shown now (set by the viewport); see <see cref="IGizmoTarget.TryGetPlacement"/>.</summary>
    public Func<Pose?>? PoseSource { get; set; }

    bool IGizmoTarget.TryGetPlacement(out Vector3 centre, out Quaternion frame) => TryGetPlacement(PoseSource?.Invoke(), out centre, out frame);

    /// <summary>The active bone's joint in <paramref name="pose"/>, with its <see cref="SpaceFrame(Pose, int)"/>.</summary>
    public bool TryGetPlacement(Pose? pose, out Vector3 centre, out Quaternion frame)
    {
        centre = default;
        frame = Quaternion.Identity;
        int bone = ActiveBone;
        if (pose is null || bone < 0 || bone >= pose.World.Length) return false;
        centre = pose.World[bone].Position;
        frame = SpaceFrame(pose, bone);
        return true;
    }

    bool IGizmoTarget.BeginRadiusDrag() => false;

    void IGizmoTarget.UpdateRadius(double radius) { }

    private static Vector3 Axis(int handle) => handle switch
    {
        GizmoHandle.X => Vector3.UnitX,
        GizmoHandle.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    // ── Drags ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Starts a drag of <paramref name="kind"/> on the active bone (all selected bones for a rotation):
    /// opens a coalesced edit labelled "Rotate hand-l", "Layer rotate 3 bones", "Move root", "IK drag hand-l"…
    /// Returns false (and says why in the status bar) when the gizmo does not apply.
    /// </summary>
    public bool BeginDrag(PoseDragKind kind, int handle = GizmoHandle.None)
    {
        if (_drag is not null) CancelDrag();
        Refresh();
        var skeleton = Skeleton;
        int active = ActiveBone;
        bool ok = kind switch
        {
            PoseDragKind.Rotate => _gizmo == PoseGizmo.Rotate,
            PoseDragKind.Move => _gizmo == PoseGizmo.Move && !IsIkMove,
            _ => _gizmo == PoseGizmo.Move && IsIkMove,
        };
        if (!ok || skeleton is null || active < 0)
        {
            if (_hint is { } why) Document.ShowStatus(why);
            return false;
        }
        Document.Playback.Pause();
        var clip = Document.Current;
        int time = PlayheadTick;
        var pose = new Pose(skeleton);
        pose.Sample(clip, time);
        var chain = kind == PoseDragKind.Ik ? ChainFor(active) : null;
        int[] bones = kind switch
        {
            PoseDragKind.Rotate => [.. EditBones],
            PoseDragKind.Move => [active],
            _ => [chain!.Upper, chain.Lower, chain.End],
        };
        if (bones.Length == 0) return false;
        bool layer = kind != PoseDragKind.Ik && !AutoKey;
        var state = new DragState(kind, handle, time, clip, bones, layer, _space)
        {
            Frame = SpaceFrame(pose, active, _space, skeleton),
            Chain = chain,
            Skeleton = skeleton,
        };
        try
        {
            if (!layer)
            {
                state.Keyed = ClipEdit.KeyPoseAtTime(clip, bones, time, rotations: kind != PoseDragKind.Move, positions: kind == PoseDragKind.Move);
                for (int i = 0; i < bones.Length; i++)
                {
                    var track = state.Keyed.Bones[bones[i]];
                    state.KeyIndex[i] = kind == PoseDragKind.Move ? LastAt(track.PositionKeys, time) : LastAt(track.RotationKeys, time);
                    if (state.KeyIndex[i] < 0) throw new InvalidOperationException("The key at the playhead could not be made.");
                    if (kind == PoseDragKind.Move) state.BasePosition = track.PositionKeys[state.KeyIndex[i]].Position;
                    else state.BaseLocal[i] = ClipEdit.KeyRotation(track.RotationKeys[state.KeyIndex[i]]);
                }
            }
            for (int i = 0; i < bones.Length; i++) state.ParentWorld[i] = ParentWorld(pose, skeleton, bones[i]);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Document.ShowStatus("Pose edit: " + ex.Message);
            return false;
        }
        if (chain is not null)
        {
            state.Shoulder = pose.World[chain.Upper].Position;
            state.Elbow = pose.World[chain.Lower].Position;
            state.End = pose.World[chain.End].Position;
            state.UpperLength = Vector3.Distance(state.Shoulder, state.Elbow);
            state.LowerLength = Vector3.Distance(state.Elbow, state.End);
            state.UpperWorld = pose.World[chain.Upper].Rotation;
            state.LowerWorld = pose.World[chain.Lower].Rotation;
            state.EndWorld = pose.World[chain.End].Rotation;
            if (state.UpperLength < 1e-4f || state.LowerLength < 1e-4f)
            {
                Document.ShowStatus("IK drag: the limb has a zero-length segment at this time.");
                return false;
            }
        }
        state.Label = DragLabel(kind, layer, bones, active);
        Document.BeginEdit(state.Label);
        if (!Document.IsEditing) return false;
        _drag = state;
        SetReadout(null);
        Raise(nameof(IsDragging));
        Raise(nameof(DragHandle));
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>The undo label of the drag in progress, or null.</summary>
    public string? DragLabelText => _drag?.Label;

    private string DragLabel(PoseDragKind kind, bool layer, int[] bones, int active)
    {
        string who = kind == PoseDragKind.Rotate && bones.Length > 1
            ? bones.Length.ToString(CultureInfo.CurrentCulture) + " bones"
            : Document.BoneDisplayName(active);
        return kind switch
        {
            PoseDragKind.Ik => "IK drag " + who,
            PoseDragKind.Move => (layer ? "Layer move " : "Move ") + who,
            _ => (layer ? "Layer rotate " : "Rotate ") + who,
        };
    }

    /// <summary>Rotation about the space's axis <paramref name="axis"/> (X/Y/Z handle) by <paramref name="radians"/>.</summary>
    public void UpdateAxisRotation(int axis, double radians)
    {
        if (_drag is not { Kind: PoseDragKind.Rotate } d) return;
        ApplyRotation(d, Quat.FromAxisAngle(Axis(axis), (float)radians),
            string.Format(CultureInfo.CurrentCulture, "{0} {1:0.0}°", GizmoHandle.Name(axis), radians * 180 / Math.PI));
    }

    /// <summary>
    /// A model-space rotation (screen ring, trackball), expressed in the active bone's space frame.
    /// <paramref name="shownDegrees"/> is the readout angle (signed for the screen ring); null shows the
    /// rotation's size.
    /// </summary>
    public void UpdateWorldRotation(Quaternion world, int handle = GizmoHandle.Free, double? shownDegrees = null)
    {
        if (_drag is not { Kind: PoseDragKind.Rotate } d) return;
        world = Quat.Normalize(world);
        var delta = Quat.Mul(Quat.Conj(d.Frame), Quat.Mul(world, d.Frame));
        double degrees = shownDegrees ?? Quat.AngleDegrees(Quaternion.Identity, world);
        ApplyRotation(d, delta, string.Format(CultureInfo.CurrentCulture, "{0} {1:0.0}°", GizmoHandle.Name(handle), degrees));
    }

    private void ApplyRotation(DragState d, Quaternion delta, string readout)
    {
        if (!EnsureEditing()) return;
        Document.UpdateEdit(_ => d.Layer ? LayerRotate(d, delta) : KeyRotations(d, delta));
        SetReadout(readout);
    }

    private RfaClip LayerRotate(DragState d, Quaternion delta)
    {
        var offset = new BoneOffset(delta, Vector3.Zero, d.Space) { Skeleton = d.Skeleton };
        offset = WithRange(offset);
        return d.Bones.Length == 1 ? ClipEdit.OffsetBone(d.Base, d.Bones[0], offset) : ClipEdit.OffsetBones(d.Base, d.Bones, offset);
    }

    private RfaClip KeyRotations(DragState d, Quaternion delta)
    {
        var clip = d.Keyed!;
        for (int i = 0; i < d.Bones.Length; i++)
        {
            var next = ComposeRotation(d.Space, d.BaseLocal[i], d.ParentWorld[i], delta);
            clip = ClipEdit.SetRotation(clip, KeySelection.Of(new KeyRef(d.Bones[i], KeyKind.Rotation, d.KeyIndex[i])), next);
        }
        return clip;
    }

    /// <summary>The <see cref="BoneOffset"/> a layer edit uses (the range and falloff when enabled).</summary>
    public BoneOffset WithRange(BoneOffset offset)
    {
        ArgumentNullException.ThrowIfNull(offset);
        if (!_rangeEnabled) return offset;
        return offset with { From = Math.Min(_rangeFrom, _rangeTo), To = Math.Max(_rangeFrom, _rangeTo), FalloffTicks = _falloff };
    }

    /// <summary>A translation of <paramref name="metres"/> along the space's axis <paramref name="axis"/>.</summary>
    public void UpdateAxisTranslation(int axis, double metres)
    {
        if (_drag is not { Kind: PoseDragKind.Move or PoseDragKind.Ik } d) return;
        var spaceDelta = Axis(axis) * (float)metres;
        ApplyTranslation(d, Quat.Rotate(d.Frame, spaceDelta),
            string.Format(CultureInfo.CurrentCulture, "{0} {1:+0.000;-0.000;0.000} m", GizmoHandle.Name(axis), metres));
    }

    /// <summary>A model-space translation (screen-plane drag).</summary>
    public void UpdateWorldTranslation(Vector3 world)
    {
        if (_drag is not { Kind: PoseDragKind.Move or PoseDragKind.Ik } d) return;
        ApplyTranslation(d, world, string.Format(CultureInfo.CurrentCulture, "{0:0.000} m", world.Length()));
    }

    private void ApplyTranslation(DragState d, Vector3 world, string readout)
    {
        if (!EnsureEditing()) return;
        if (d.Kind == PoseDragKind.Ik)
        {
            UpdateIk(d, d.End + world);
            return;
        }
        int bone = d.Bones[0];
        if (d.Layer)
        {
            // The same delta in the space's own frame, applied to every key (OffsetBone converts per key).
            var spaceDelta = Quat.Rotate(Quat.Conj(d.Frame), world);
            var offset = WithRange(new BoneOffset(Quaternion.Identity, spaceDelta, d.Space) { Skeleton = d.Skeleton });
            Document.UpdateEdit(_ => ClipEdit.OffsetBone(d.Base, bone, offset));
        }
        else
        {
            // At the playhead every space gives the same parent-frame delta.
            var parentDelta = Quat.Rotate(Quat.Conj(d.ParentWorld[0]), world);
            var key = new KeyRef(bone, KeyKind.Position, d.KeyIndex[0]);
            Document.UpdateEdit(_ => ClipEdit.SetPosition(d.Keyed!, KeySelection.Of(key), d.BasePosition + parentDelta, moveControlPoints: true));
        }
        SetReadout(readout);
    }

    /// <summary>Drags the IK chain's end to <paramref name="target"/> (model space).</summary>
    public void UpdateIkTarget(Vector3 target)
    {
        if (_drag is not { Kind: PoseDragKind.Ik } d || !EnsureEditing()) return;
        UpdateIk(d, target);
    }

    private void UpdateIk(DragState d, Vector3 target)
    {
        var chain = d.Chain!;
        var solution = TwoBoneIk.Solve(d.Shoulder, d.Elbow, target, d.UpperLength, d.LowerLength, chain.PoleBias, chain.PoleFade);
        var (upper, lower, end) = IkLocals(d, solution);
        d.LastSolution = solution;
        Document.UpdateEdit(_ =>
        {
            var clip = d.Keyed!;
            clip = ClipEdit.SetRotation(clip, KeySelection.Of(new KeyRef(chain.Upper, KeyKind.Rotation, d.KeyIndex[0])), upper);
            clip = ClipEdit.SetRotation(clip, KeySelection.Of(new KeyRef(chain.Lower, KeyKind.Rotation, d.KeyIndex[1])), lower);
            clip = ClipEdit.SetRotation(clip, KeySelection.Of(new KeyRef(chain.End, KeyKind.Rotation, d.KeyIndex[2])), end);
            return clip;
        });
        SetReadout(string.Format(CultureInfo.CurrentCulture, "IK {0:0.000} m{1}", Vector3.Distance(target, d.End),
            solution.Clamped ? " (out of reach)" : string.Empty));
    }

    /// <summary>
    /// The three local rotations that put the chain on <paramref name="solution"/>: the upper bone swings
    /// its elbow onto the solved elbow (shortest arc), the lower bone then swings the end onto the solved
    /// end, and the end bone keeps its pre-drag model-space rotation.
    /// </summary>
    private static (Quaternion Upper, Quaternion Lower, Quaternion End) IkLocals(DragState d, TwoBoneIkSolution solution)
    {
        var q1 = Quat.FromTo(d.Elbow - d.Shoulder, solution.Elbow - d.Shoulder);
        var end1 = d.Shoulder + Quat.Rotate(q1, d.End - d.Shoulder);
        var q2 = Quat.FromTo(end1 - solution.Elbow, solution.End - solution.Elbow);
        var upperWorld = Quat.Normalize(Quat.Mul(q1, d.UpperWorld));
        var lowerWorld = Quat.Normalize(Quat.Mul(q2, Quat.Mul(q1, d.LowerWorld)));
        var upper = Quat.Mul(Quat.Conj(d.ParentWorld[0]), upperWorld);
        var lower = Quat.Mul(Quat.Conj(upperWorld), lowerWorld);
        var end = Quat.Mul(Quat.Conj(lowerWorld), d.EndWorld);
        return (upper, lower, end);
    }

    /// <summary>The last IK solution of the drag in progress (self-test), or null.</summary>
    public TwoBoneIkSolution? LastIkSolution => _drag?.LastSolution;

    /// <summary>Ends the drag as one undo step.</summary>
    public void CommitDrag()
    {
        if (_drag is null) return;
        _drag = null;
        if (Document.IsEditing) Document.CommitEdit();
        EndDrag();
    }

    /// <summary>Abandons the drag, restoring the pre-drag snapshot exactly (Esc).</summary>
    public void CancelDrag()
    {
        if (_drag is null) return;
        _drag = null;
        if (Document.IsEditing) Document.CancelEdit();
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

    private static Quaternion ParentWorld(Pose pose, Skeleton skeleton, int bone)
    {
        int p = skeleton.EffectiveParents[bone];
        return p >= 0 && p < pose.World.Length ? pose.World[p].Rotation : Quaternion.Identity;
    }

    private static int LastAt(System.Collections.Immutable.ImmutableArray<RfaRotKey> keys, int time)
    {
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            if (keys[i].Time == time) return i;
        }
        return -1;
    }

    private static int LastAt(System.Collections.Immutable.ImmutableArray<RfaPosKey> keys, int time)
    {
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            if (keys[i].Time == time) return i;
        }
        return -1;
    }

    // ── Ghost of the saved clip and state refresh ────────────────────────────

    private void OnSceneChanged()
    {
        SyncGhost();
        Refresh();
    }

    /// <summary>
    /// Shows <c>SavedSnapshot</c> as the "saved" ghost while the display toggle is on and the clip has
    /// unsaved changes (an unedited clip's ghost would sit exactly under the live skeleton).
    /// </summary>
    public void SyncGhost()
    {
        var doc = Document;
        bool want = doc.Display.GhostSavedClip && doc.Scene.Skeleton.Count > 0 && !ReferenceEquals(doc.Current, doc.SavedSnapshot);
        var clip = want ? doc.SavedSnapshot : null;
        if (ReferenceEquals(clip, _ghostClip)) return;
        _ghostClip = clip;
        doc.Scene.SetGhost("saved", clip, "Saved clip", "Viewport.Ghost");
    }

    /// <summary>Recomputes the gizmo, the badge text and the hint.</summary>
    public void Refresh()
    {
        var gizmo = PoseGizmo.None;
        string? hint = null;
        string mode = string.Empty;
        var kind = PoseBadgeKind.None;
        if (_tool != PoseTool.Select)
        {
            var doc = Document;
            int active = ActiveBone;
            string verb = _tool == PoseTool.Rotate ? "rotate" : "move";
            if (Skeleton is null)
            {
                hint = doc.Scene.Skeleton.Count == 0
                    ? "Pose editing needs a preview mesh: pick one in the mesh picker."
                    : $"Pose editing needs a preview mesh with {doc.Current.BoneCount} bones; {doc.Scene.MeshName} has {doc.Scene.Skeleton.Count}.";
            }
            else if (doc.Display.BindPose)
            {
                hint = "The bind pose is showing: turn it off to pose the clip.";
            }
            else if (doc.Scene.PreviewClip is not null)
            {
                hint = "A clip tool's preview is showing; close the tool to pose the clip.";
            }
            else if (active < 0)
            {
                hint = $"Click a joint to select the bone to {verb}.";
            }
            else if (_tool == PoseTool.Rotate)
            {
                gizmo = PoseGizmo.Rotate;
            }
            else
            {
                var chain = _ikEnabled ? ChainFor(active) : null;
                if (chain is not null)
                {
                    if (AutoKey) gizmo = PoseGizmo.Move;
                    else hint = "The IK drag keys the limb at the playhead: turn auto-key on (or IK off).";
                }
                else if (CanMove(active))
                {
                    gizmo = PoseGizmo.Move;
                }
                else
                {
                    string name = doc.BoneDisplayName(active);
                    hint = $"{name} cannot be moved: only the root and bones whose position is animated have a move gizmo."
                        + (ChainFor(active) is not null ? " Turn IK on to drag the limb by it." : " Rotate it instead (E).");
                }
            }

            if (gizmo != PoseGizmo.None)
            {
                string at = TimeFormat.Format(PlayheadTick, Unit);
                var bones = EditBones;
                string who = _tool == PoseTool.Rotate && bones.Count > 1
                    ? bones.Count.ToString(CultureInfo.CurrentCulture) + " bones"
                    : doc.BoneDisplayName(active);
                if (_tool == PoseTool.Move && _ikEnabled && ChainFor(active) is { } c)
                {
                    kind = PoseBadgeKind.Ik;
                    mode = $"IK — drags {doc.BoneDisplayName(c.End)} with {doc.BoneDisplayName(c.Upper)} and {doc.BoneDisplayName(c.Lower)}; keys at {at}";
                }
                else if (AutoKey)
                {
                    kind = PoseBadgeKind.AutoKey;
                    mode = $"Auto-key — keys {who} at the playhead ({at})";
                }
                else
                {
                    kind = PoseBadgeKind.Layer;
                    mode = $"Layer edit — offsets every key of {who}";
                    if (_rangeEnabled)
                    {
                        mode += $" from {TimeFormat.Format(Math.Min(_rangeFrom, _rangeTo), Unit)} to {TimeFormat.Format(Math.Max(_rangeFrom, _rangeTo), Unit)}";
                        if (_falloff > 0) mode += $", falloff {TimeFormat.Format(_falloff, Unit)}";
                    }
                }
            }
        }

        bool autoKey = AutoKey;
        bool changed = gizmo != _gizmo || hint != _hint || mode != _modeText || kind != _badgeKind || autoKey != _lastAutoKey;
        _lastAutoKey = autoKey;
        _gizmo = gizmo;
        _hint = hint;
        _modeText = mode;
        _badgeKind = kind;
        if (changed)
        {
            RaiseAll(nameof(Gizmo), nameof(Hint), nameof(ModeText), nameof(BadgeKind), nameof(BadgeVisible), nameof(IsLayerMode),
                nameof(AutoKey), nameof(UnitSuffix), nameof(UnitDecimals), nameof(RangeFrom), nameof(RangeTo), nameof(Falloff));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class DragState(PoseDragKind kind, int handle, int time, RfaClip baseClip, int[] bones, bool layer, OffsetSpace space)
    {
        public PoseDragKind Kind { get; } = kind;
        public int Handle { get; } = handle;
        public int Time { get; } = time;
        public RfaClip Base { get; } = baseClip;
        public int[] Bones { get; } = bones;
        public bool Layer { get; } = layer;
        public OffsetSpace Space { get; } = space;
        public RfaClip? Keyed { get; set; }
        public int[] KeyIndex { get; } = new int[bones.Length];
        public Quaternion[] BaseLocal { get; } = new Quaternion[bones.Length];
        public Quaternion[] ParentWorld { get; } = new Quaternion[bones.Length];
        public Vector3 BasePosition { get; set; }
        public Quaternion Frame { get; set; } = Quaternion.Identity;
        public Skeleton? Skeleton { get; set; }
        public string Label { get; set; } = string.Empty;
        public IkChainBones? Chain { get; set; }
        public Vector3 Shoulder { get; set; }
        public Vector3 Elbow { get; set; }
        public Vector3 End { get; set; }
        public float UpperLength { get; set; }
        public float LowerLength { get; set; }
        public Quaternion UpperWorld { get; set; }
        public Quaternion LowerWorld { get; set; }
        public Quaternion EndWorld { get; set; }
        public TwoBoneIkSolution? LastSolution { get; set; }
    }
}

/// <summary>Which mode the viewport badge shows.</summary>
public enum PoseBadgeKind
{
    None,
    AutoKey,
    Layer,
    Ik,
    /// <summary>A mesh tab's gizmo (bone bind, collision sphere, prop point).</summary>
    Mesh,
}
