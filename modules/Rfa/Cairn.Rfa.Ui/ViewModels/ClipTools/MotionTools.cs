using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>Clip › Make Loopable: <see cref="ClipEdit.MakeLoopable"/>.</summary>
public sealed class LoopToolViewModel : ClipToolViewModel
{
    private LoopMode _mode = LoopMode.BlendEndToStart;
    private double _blend;
    private bool _keepX;
    private bool _keepY;
    private bool _keepZ;

    public LoopToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        int frames = Math.Max(1, Math.Min(8, Original.Duration / RfaClip.TicksPerFrame / 4));
        _blend = ToUnit(Math.Min(Original.Duration, frames * RfaClip.TicksPerFrame));
        if (document.FittingSkeleton is { } skeleton) RootBone = ClipEdit.FindRootBone(skeleton);
        else RootBone = -1;
        // A walk or run keeps travelling: keep the root's forward travel by default when it has any.
        if (RootBone >= 0)
        {
            var travel = ClipToolSummary.Travel(Original, RootBone);
            _keepX = MathF.Abs(travel.X) > 0.05f;
            _keepZ = MathF.Abs(travel.Z) > 0.05f;
        }
        Start();
    }

    public override string ToolId => "loop";

    public override string Title => "Make Loopable";

    public override string Heading => "Make the clip loop seamlessly";

    public override string Description =>
        "Cross-fades one end of the clip into the other end's pose over the blend window, for every bone, so the last frame "
        + "matches the first. Keys outside the window are untouched.";

    public bool IsEndToStart
    {
        get => _mode == LoopMode.BlendEndToStart;
        set { if (value) Mode = LoopMode.BlendEndToStart; }
    }

    public bool IsStartToEnd
    {
        get => _mode == LoopMode.BlendStartToEnd;
        set { if (value) Mode = LoopMode.BlendStartToEnd; }
    }

    /// <summary>Which end changes.</summary>
    public LoopMode Mode
    {
        get => _mode;
        set
        {
            if (SetParameter(ref _mode, value)) RaiseAll(nameof(IsEndToStart), nameof(IsStartToEnd));
        }
    }

    /// <summary>The cross-fade window in the display unit.</summary>
    public double BlendWindow
    {
        get => _blend;
        set => SetParameter(ref _blend, value);
    }

    /// <summary>The clip's duration in the display unit (the window's maximum).</summary>
    public double MaxBlend => ToUnit(Math.Max(0, Original.Duration));

    /// <summary>The root bone (from the preview mesh), or -1 without a fitting mesh.</summary>
    public int RootBone { get; }

    public bool HasRoot => RootBone >= 0;

    /// <summary>"Root bone: root (travel (0.00, 0.00, 1.52) m)", or why root options are off.</summary>
    public string RootText => HasRoot
        ? $"Root bone: {BoneName(RootBone)} · travels {ClipToolSummary.Vector(ClipToolSummary.Travel(Original, RootBone))} from start to end"
        : "Root options need a preview mesh with as many bones as the clip (to know which bone is the root).";

    public bool KeepX
    {
        get => _keepX;
        set => SetParameter(ref _keepX, value);
    }

    public bool KeepY
    {
        get => _keepY;
        set => SetParameter(ref _keepY, value);
    }

    public bool KeepZ
    {
        get => _keepZ;
        set => SetParameter(ref _keepZ, value);
    }

    /// <summary>The root axes whose travel is kept.</summary>
    public RootMotionAxes KeepAxes =>
        (_keepX ? RootMotionAxes.X : 0) | (_keepY ? RootMotionAxes.Y : 0) | (_keepZ ? RootMotionAxes.Z : 0);

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        if (clip.Duration <= 0) throw new ArgumentException("The clip has no duration to loop (end is not after start).");
        int blend = ToTicks(_blend);
        if (blend < 0 || blend > clip.Duration) throw new ArgumentException($"The blend window must be between 0 and the clip's length ({Time(clip.Duration)}).");
        var mode = _mode;
        var axes = KeepAxes;
        int root = RootBone;
        var options = root >= 0 ? new LoopOptions(root, axes) : null;
        string label = $"Make loopable (blend {Num(blend)} {UnitSuffix})";
        string window = Time(blend);
        string rootName = root >= 0 ? BoneName(root) : "";
        return Work(_ =>
        {
            var result = ClipEdit.MakeLoopable(clip, blend, mode, options);
            int skip = axes != RootMotionAxes.None ? root : -1;
            var before = ClipToolSummary.SeamExcept(clip, skip);
            var after = ClipToolSummary.SeamExcept(result, skip);
            var lines = new List<string>
            {
                mode == LoopMode.BlendEndToStart
                    ? $"The last {window} blend into the first pose; the final key equals the first."
                    : $"The first {window} blend out of the last pose; the first key equals the last.",
                string.Format(CultureInfo.CurrentCulture, "Seam (largest jump from the last pose to the first): {0:0.##}°, {1} → {2:0.##}°, {3}",
                    before.Degrees, ClipToolSummary.Metres(before.Metres), after.Degrees, ClipToolSummary.Metres(after.Metres)),
                KeyDiff.Of(clip, result).KeysLine(),
            };
            if (axes != RootMotionAxes.None) lines.Add($"The root ({rootName}) keeps its travel along {axes}.");
            return new ClipToolResult(result, label, lines);
        });
    }
}

/// <summary>Clip › Resample (Bake): <see cref="ClipEdit.Resample"/>.</summary>
public sealed class ResampleToolViewModel : ClipToolViewModel
{
    private double _fps = 30;
    private bool _rotations = true;
    private bool _positions = true;

    public ResampleToolViewModel(ClipDocumentViewModel document) : base(document) => Start();

    public override string ToolId => "resample";

    public override string Title => "Resample (Bake)";

    public override string Heading => "Bake the clip to evenly spaced keys";

    public override string Description =>
        "Replaces the keys with samples of the current motion every step from start to end (end always included). "
        + "Eases are baked in; position curves are reproduced exactly where they can be.";

    /// <summary>Keys per second.</summary>
    public double Fps
    {
        get => _fps;
        set
        {
            if (SetParameter(ref _fps, value)) Raise(nameof(StepText));
        }
    }

    public bool Rotations
    {
        get => _rotations;
        set => SetParameter(ref _rotations, value);
    }

    public bool Positions
    {
        get => _positions;
        set => SetParameter(ref _positions, value);
    }

    private int StepTicks => Math.Max(1, (int)Math.Round(RfaClip.TicksPerSecond / Math.Max(0.01, _fps)));

    /// <summary>"A key every 160 ticks (1 f)".</summary>
    public string StepText => $"A key every {StepTicks} ticks ({Time(StepTicks)})";

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        if (_fps <= 0) throw new ArgumentException("The rate must be more than zero.");
        if (!_rotations && !_positions) return NothingWork("Choose rotations, positions or both.");
        var clip = Original;
        int step = StepTicks;
        bool rot = _rotations, pos = _positions;
        string label = string.Format(CultureInfo.CurrentCulture, "Resample at {0:0.##} fps", _fps);
        string stepText = StepText;
        return Work(ct =>
        {
            var result = ClipEdit.Resample(clip, step, rot, pos);
            ct.ThrowIfCancellationRequested();
            var (deg, m) = ClipEdit.MeasureError(clip, result, 16);
            return new ClipToolResult(result, label,
            [
                stepText + (rot && pos ? ", rotations and positions" : rot ? ", rotations only" : ", positions only"),
                KeyDiff.Of(clip, result).KeysLine(),
                ClipToolSummary.ErrorLine("Measured largest difference from the current motion", deg, m),
            ]);
        });
    }
}

/// <summary>Clip › Reduce Keys: <see cref="ClipEdit.ReduceKeys"/>.</summary>
public sealed class ReduceToolViewModel : ClipToolViewModel
{
    private double _rotationTolerance = 0.1;
    private double _positionTolerance = 0.0005;
    private bool _rotations = true;
    private bool _positions = true;

    public ReduceToolViewModel(ClipDocumentViewModel document) : base(document) => Start();

    public override string ToolId => "reduce";

    public override string Title => "Reduce Keys";

    public override string Heading => "Remove keys the motion does not need";

    public override string Description =>
        "Drops keys while the motion stays within the tolerances of the current clip (checked 300 times a second). "
        + "Kept keys are unchanged; the error shown is measured on the result.";

    public override bool SupportsBoneScope => true;

    /// <summary>Largest allowed rotation error, degrees.</summary>
    public double RotationTolerance
    {
        get => _rotationTolerance;
        set => SetParameter(ref _rotationTolerance, value);
    }

    /// <summary>Largest allowed position error, metres.</summary>
    public double PositionTolerance
    {
        get => _positionTolerance;
        set => SetParameter(ref _positionTolerance, value);
    }

    public bool Rotations
    {
        get => _rotations;
        set => SetParameter(ref _rotations, value);
    }

    public bool Positions
    {
        get => _positions;
        set => SetParameter(ref _positions, value);
    }

    /// <summary>The measured result of the last preview (null before one).</summary>
    public ReduceResult? Reduction { get; private set; }

    protected override void OnResult(ClipToolResult result)
    {
        Reduction = result.Extra as ReduceResult;
        Raise(nameof(Reduction));
    }

    /// <summary>The options the current parameters give (also what the self-test calls the Core with).</summary>
    public ReduceOptions Options => new()
    {
        RotationToleranceDegrees = (float)_rotationTolerance,
        PositionTolerance = (float)_positionTolerance,
        Rotations = _rotations,
        Positions = _positions,
        Bones = ScopeBones,
    };

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        if (_rotationTolerance < 0 || _positionTolerance < 0) throw new ArgumentException("Tolerances cannot be negative.");
        if (!_rotations && !_positions) return NothingWork("Choose rotations, positions or both.");
        if (ScopeBones is { Count: 0 }) throw new ArgumentException("No bones in scope: select bones (or keys) first, or reduce every bone.");
        var clip = Original;
        var options = Options;
        string label = _rotations
            ? string.Format(CultureInfo.CurrentCulture, "Reduce keys ({0:0.###}°)", _rotationTolerance)
            : string.Format(CultureInfo.CurrentCulture, "Reduce keys ({0})", ClipToolSummary.Metres((float)_positionTolerance));
        string scope = ScopeText;
        return Work(_ =>
        {
            var r = ClipEdit.ReduceKeys(clip, options);
            int removed = r.KeysBefore - r.KeysAfter;
            double percent = r.KeysBefore > 0 ? 100.0 * removed / r.KeysBefore : 0;
            return new ClipToolResult(r.Clip, label,
            [
                string.Format(CultureInfo.CurrentCulture, "Keys: {0:N0} → {1:N0} ({2:N0} removed, {3:0.#} % fewer)", r.KeysBefore, r.KeysAfter, removed, percent),
                ClipToolSummary.ErrorLine("Measured largest error", r.MaxRotationErrorDegrees, r.MaxPositionError),
                $"Scope: {scope}" + (options.Rotations && options.Positions ? ", rotations and positions" : options.Rotations ? ", rotations only" : ", positions only"),
            ], r);
        });
    }
}

/// <summary>A bone's partner in the mirror dialog's pair table.</summary>
/// <param name="Bone">The partner's index, or -1 for "centre" (mirrored in place).</param>
/// <param name="Label">The partner's name.</param>
public sealed record PartnerOption(int Bone, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the mirror dialog's pair table: a bone and its partner (editable).</summary>
public sealed class MirrorPairRow : ObservableObject
{
    private readonly MirrorToolViewModel _owner;
    private PartnerOption _partner;
    private bool _syncing;

    internal MirrorPairRow(MirrorToolViewModel owner, int bone, string name, IReadOnlyList<PartnerOption> options, PartnerOption partner)
    {
        _owner = owner;
        Bone = bone;
        Name = name;
        Options = options;
        _partner = partner;
    }

    public int Bone { get; }

    public string Name { get; }

    /// <summary>"Bone 3: arm-l-upper" for screen readers.</summary>
    public string AutomationName => $"Partner of {Name}";

    public IReadOnlyList<PartnerOption> Options { get; }

    /// <summary>The partner (setting it re-pairs both bones).</summary>
    public PartnerOption Partner
    {
        get => _partner;
        set
        {
            if (value is null || !Set(ref _partner, value) || _syncing) return;
            _owner.SetPartner(Bone, value.Bone);
        }
    }

    /// <summary>True when the bone has a partner other than itself.</summary>
    public bool IsPaired => _partner.Bone >= 0;

    internal void Sync(PartnerOption partner)
    {
        _syncing = true;
        try
        {
            Partner = partner;
            Raise(nameof(IsPaired));
        }
        finally { _syncing = false; }
    }
}

/// <summary>Clip › Mirror Left/Right: <see cref="ClipEdit.MirrorClip"/> with an editable pair table.</summary>
public sealed class MirrorToolViewModel : ClipToolViewModel
{
    private readonly IReadOnlyList<string>? _names;
    private readonly Skeleton? _skeleton;
    private readonly List<PartnerOption> _options;
    private BonePairMap _pairs;
    private MirrorAxis _axis = MirrorAxis.X;
    private bool _useSkeleton;

    public MirrorToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        _names = document.ClipboardBoneNames;
        _skeleton = document.FittingSkeleton;
        _useSkeleton = _skeleton is not null;
        int count = Original.BoneCount;
        _pairs = _names is { } names && names.Count == count ? BonePairs.Detect(names) : BonePairMap.Identity(count);
        _options = [new PartnerOption(-1, "— centre (mirrored in place)")];
        for (int i = 0; i < count; i++) _options.Add(new PartnerOption(i, BoneName(i)));
        for (int i = 0; i < count; i++) Rows.Add(new MirrorPairRow(this, i, BoneName(i), _options, OptionFor(i)));
        DetectCommand = new RelayCommand(() => SetPairs(BonePairs.Detect(_names!)), () => _names is not null);
        UnpairAllCommand = new RelayCommand(() => SetPairs(BonePairMap.Identity(count)));
        Start();
    }

    public override string ToolId => "mirror";

    public override string Title => "Mirror";

    public override string Heading => "Mirror the clip left/right";

    public override string Description =>
        "Every bone takes its partner's animation, reflected across the mirror plane; centre bones are reflected in place. "
        + "Bone lengths stay the rig's own. Pairs come from the bone names — change any partner below.";

    /// <summary>The pair table.</summary>
    public ObservableCollection<MirrorPairRow> Rows { get; } = [];

    /// <summary>The current pair map.</summary>
    public BonePairMap Pairs => _pairs;

    public bool HasNames => _names is not null;

    /// <summary>Why the table starts unpaired, when it does.</summary>
    public string? NamesNote => _names is null
        ? $"The clip's bones have no names here (no preview mesh with {Original.BoneCount} bones is loaded), so no pairs could be found. Pair bones by hand, or pick a fitting preview mesh first."
        : null;

    public IReadOnlyList<Choice<MirrorAxis>> Axes { get; } =
    [
        new(MirrorAxis.X, "Left ↔ right (flip X)"),
        new(MirrorAxis.Y, "Up ↔ down (flip Y)"),
        new(MirrorAxis.Z, "Front ↔ back (flip Z)"),
    ];

    /// <summary>The axis choice.</summary>
    public Choice<MirrorAxis> SelectedAxis
    {
        get => Axes.First(a => a.Value == _axis);
        set
        {
            if (value is not null && SetParameter(ref _axis, value.Value, nameof(Axis))) Raise(nameof(SelectedAxis));
        }
    }

    /// <summary>The axis flipped.</summary>
    public MirrorAxis Axis
    {
        get => _axis;
        set
        {
            if (SetParameter(ref _axis, value)) Raise(nameof(SelectedAxis));
        }
    }

    public bool HasSkeleton => _skeleton is not null;

    /// <summary>Mirror each bone's model-space change from the preview mesh's rest pose (recommended).</summary>
    public bool UseSkeleton
    {
        get => _useSkeleton;
        set => SetParameter(ref _useSkeleton, value && _skeleton is not null);
    }

    /// <summary>"Uses ult2_guard.v3c's rest pose", or why not.</summary>
    public string SkeletonText => _skeleton is not null
        ? $"Mirror each bone's change from {Document.Scene.MeshName}'s rest pose (right for rigs whose left and right bone frames are not exact reflections)"
        : "No preview mesh with as many bones as the clip: local rotations are reflected directly.";

    public RelayCommand DetectCommand { get; }

    public RelayCommand UnpairAllCommand { get; }

    /// <summary>The options the current parameters give.</summary>
    public MirrorOptions Options => new(_axis, _useSkeleton ? _skeleton : null);

    private PartnerOption OptionFor(int bone)
    {
        int partner = _pairs.PartnerOf(bone);
        return partner == bone ? _options[0] : _options[partner + 1];
    }

    internal void SetPartner(int bone, int partner)
    {
        SetPairs(partner < 0 || partner == bone ? _pairs.Without(bone) : _pairs.With(bone, partner));
    }

    /// <summary>Replaces the whole map (tests, Detect, Unpair all).</summary>
    public void SetPairs(BonePairMap pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        _pairs = pairs;
        foreach (var row in Rows) row.Sync(OptionFor(row.Bone));
        Raise(nameof(Pairs));
        OnParametersChanged();
    }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        var pairs = _pairs;
        var options = Options;
        string label = _axis switch
        {
            MirrorAxis.Y => "Mirror up/down",
            MirrorAxis.Z => "Mirror front/back",
            _ => "Mirror left/right",
        };
        int pairCount = Enumerable.Range(0, pairs.Count).Count(i => pairs.PartnerOf(i) > i);
        int centre = Enumerable.Range(0, pairs.Count).Count(i => pairs.PartnerOf(i) == i);
        string rest = options.Skeleton is not null
            ? $"Each bone's model-space change from {Document.Scene.MeshName}'s rest pose is mirrored."
            : "Local rotations and positions are reflected directly (exact sign flips).";
        return Work(_ =>
        {
            var result = ClipEdit.MirrorClip(clip, pairs, options);
            return new ClipToolResult(result, label,
            [
                $"{ClipToolSummary.Count(pairCount, "pair")} of bones swap animations; {ClipToolSummary.Count(centre, "centre bone")} mirrored in place.",
                rest,
                KeyDiff.Of(clip, result).KeysLine(),
            ]);
        });
    }
}
