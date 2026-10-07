using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>Clip › Offset Bone: <see cref="ClipEdit.OffsetBones"/> on the selected bones.</summary>
public sealed class OffsetToolViewModel : ClipToolViewModel
{
    private readonly Skeleton? _skeleton;
    private double _pitch, _yaw, _roll;
    private double _moveX, _moveY, _moveZ;
    private OffsetSpace _space = OffsetSpace.Local;
    private bool _useRange;
    private double _from, _to, _falloff;

    public OffsetToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        _skeleton = document.FittingSkeleton;
        var span = document.KeySelection.TimeSpan(Original);
        _useRange = span is { } s && s.Max > s.Min;
        (int a, int b) = _useRange ? span!.Value : (Original.StartTime, Original.EndTime);
        _from = ToUnit(a);
        _to = ToUnit(b);
        _falloff = ToUnit(4 * RfaClip.TicksPerFrame);
        Start();
    }

    public override string ToolId => "offset";

    public override string Title => "Offset Bone";

    public override string Heading => "Offset the selected bones";

    public override string Description =>
        "Adds a rotation and/or a translation to every key of the selected bones — the whole clip, or a time range that fades "
        + "in and out over the falloff. Children follow, as in any rig.";

    /// <summary>The bones offset (the viewport selection when the dialog opened).</summary>
    public string BonesText => SelectedBones.Count == 0
        ? "No bones are selected. Close this dialog, select bones in the viewport (click a joint, Ctrl+click for more), then open it again."
        : "Bones: " + ClipToolSummary.Names([.. SelectedBones.Select(BoneName)]);

    public double Pitch { get => _pitch; set => SetParameter(ref _pitch, value); }

    public double Yaw { get => _yaw; set => SetParameter(ref _yaw, value); }

    public double Roll { get => _roll; set => SetParameter(ref _roll, value); }

    public double MoveX { get => _moveX; set => SetParameter(ref _moveX, value); }

    public double MoveY { get => _moveY; set => SetParameter(ref _moveY, value); }

    public double MoveZ { get => _moveZ; set => SetParameter(ref _moveZ, value); }

    public IReadOnlyList<Choice<OffsetSpace>> Spaces { get; } =
    [
        new(OffsetSpace.Local, "Local (the bone's own axes)"),
        new(OffsetSpace.Parent, "Parent (the parent bone's axes)"),
        new(OffsetSpace.Model, "Model (the character's axes)"),
    ];

    public Choice<OffsetSpace> SelectedSpace
    {
        get => Spaces.First(s => s.Value == _space);
        set
        {
            if (value is not null && SetParameter(ref _space, value.Value, nameof(Space))) Raise(nameof(SelectedSpace));
        }
    }

    public OffsetSpace Space
    {
        get => _space;
        set
        {
            if (SetParameter(ref _space, value)) Raise(nameof(SelectedSpace));
        }
    }

    public bool HasSkeleton => _skeleton is not null;

    public bool IsWholeClip
    {
        get => !_useRange;
        set => UseRange = !value;
    }

    public bool UseRange
    {
        get => _useRange;
        set
        {
            if (SetParameter(ref _useRange, value)) Raise(nameof(IsWholeClip));
        }
    }

    public double From { get => _from; set => SetParameter(ref _from, value); }

    public double To { get => _to; set => SetParameter(ref _to, value); }

    public double Falloff { get => _falloff; set => SetParameter(ref _falloff, value); }

    /// <summary>The offset the current parameters give (also what the self-test calls the Core with).</summary>
    public BoneOffset Offset => new(
        Quat.FromEulerDegrees(new Vector3((float)_pitch, (float)_yaw, (float)_roll)),
        new Vector3((float)_moveX, (float)_moveY, (float)_moveZ),
        _space)
    {
        From = _useRange ? ToTicks(_from) : null,
        To = _useRange ? ToTicks(_to) : null,
        FalloffTicks = _useRange ? Math.Max(0, ToTicks(_falloff)) : 0,
        Skeleton = _skeleton,
    };

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        if (SelectedBones.Count == 0) throw new ArgumentException("Select the bones to offset in the viewport before opening this tool.");
        if (_pitch == 0 && _yaw == 0 && _roll == 0 && _moveX == 0 && _moveY == 0 && _moveZ == 0)
            return NothingWork("Enter a rotation and/or a translation to add.");
        if (_space == OffsetSpace.Model && _skeleton is null)
            throw new ArgumentException("Model space needs a preview mesh with as many bones as the clip.");
        var clip = Original;
        var bones = SelectedBones;
        var offset = Offset;
        string label = bones.Count == 1 ? $"Offset {BoneName(bones[0])}" : $"Offset {bones.Count} bones";
        string where = _useRange
            ? $"From {Time(offset.From!.Value)} to {Time(offset.To!.Value)}, fading over {Time(offset.FalloffTicks)} on each side."
            : "Over the whole clip.";
        string names = BonesText;
        return Work(ct =>
        {
            var result = ClipEdit.OffsetBones(clip, bones, offset);
            ct.ThrowIfCancellationRequested();
            var (deg, m) = ClipEdit.MeasureError(clip, result, 16);
            return new ClipToolResult(result, label,
            [
                names,
                where,
                KeyDiff.Of(clip, result).KeysLine(),
                ClipToolSummary.ErrorLine("Largest change of any bone", deg, m),
            ]);
        });
    }
}

/// <summary>Clip › Remove / Scale Root Motion: <see cref="ClipEdit.RemoveRootMotion"/> and <see cref="ClipEdit.ScaleRootMotion"/>.</summary>
public sealed class RootMotionToolViewModel : ClipToolViewModel
{
    private Choice<int> _root;
    private bool _scale;
    private bool _removeX = true, _removeY, _removeZ = true, _removeYaw;
    private double _scaleX = 1, _scaleY = 1, _scaleZ = 1;

    public RootMotionToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        RootChoices = [.. Enumerable.Range(0, Original.BoneCount).Select(i => new Choice<int>(i, BoneName(i)))];
        int root = document.FittingSkeleton is { } skeleton ? ClipEdit.FindRootBone(skeleton) : 0;
        _root = RootChoices.Count > 0 ? RootChoices[Math.Clamp(root, 0, RootChoices.Count - 1)] : new Choice<int>(-1, "(no bones)");
        RootNote = document.FittingSkeleton is null
            ? "No preview mesh with as many bones as the clip: bone 0 is assumed to be the root — check it."
            : $"Found from {document.Scene.MeshName}'s hierarchy.";
        Start();
    }

    public override string ToolId => "root";

    public override string Title => "Root Motion";

    public override string Heading => "Remove or scale the root's motion";

    public override string Description =>
        "Remove makes the clip play in place on the chosen axes (the root keeps its first key's value there) or removes its turning; "
        + "Scale stretches or shrinks how far the root travels.";

    public IReadOnlyList<Choice<int>> RootChoices { get; }

    public Choice<int> SelectedRoot
    {
        get => _root;
        set
        {
            if (value is not null && SetParameter(ref _root, value)) Raise(nameof(TravelText));
        }
    }

    public string RootNote { get; }

    /// <summary>"Travels (0.00, 0.00, 1.52) m from start to end".</summary>
    public string TravelText => $"The root travels {ClipToolSummary.Vector(ClipToolSummary.Travel(Original, _root.Value))} from start to end (X sideways, Y up, Z forward).";

    public bool IsRemove
    {
        get => !_scale;
        set => IsScale = !value;
    }

    public bool IsScale
    {
        get => _scale;
        set
        {
            if (SetParameter(ref _scale, value)) Raise(nameof(IsRemove));
        }
    }

    public bool RemoveX { get => _removeX; set => SetParameter(ref _removeX, value); }

    public bool RemoveY { get => _removeY; set => SetParameter(ref _removeY, value); }

    public bool RemoveZ { get => _removeZ; set => SetParameter(ref _removeZ, value); }

    public bool RemoveYaw { get => _removeYaw; set => SetParameter(ref _removeYaw, value); }

    public double ScaleX { get => _scaleX; set => SetParameter(ref _scaleX, value); }

    public double ScaleY { get => _scaleY; set => SetParameter(ref _scaleY, value); }

    public double ScaleZ { get => _scaleZ; set => SetParameter(ref _scaleZ, value); }

    /// <summary>The axes Remove takes away.</summary>
    public RootMotionAxes Axes =>
        (_removeX ? RootMotionAxes.X : 0) | (_removeY ? RootMotionAxes.Y : 0) | (_removeZ ? RootMotionAxes.Z : 0) | (_removeYaw ? RootMotionAxes.Yaw : 0);

    /// <summary>The scale vector Scale applies.</summary>
    public Vector3 ScaleVector => new((float)_scaleX, (float)_scaleY, (float)_scaleZ);

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        int root = _root.Value;
        var clip = Original;
        if ((uint)root >= (uint)clip.BoneCount) throw new ArgumentException("Choose the root bone.");
        string rootName = BoneName(root);
        if (!_scale)
        {
            var axes = Axes;
            if (axes == RootMotionAxes.None) return NothingWork("Choose the axes to remove.");
            var names = new List<string>();
            if (_removeX) names.Add("X");
            if (_removeY) names.Add("Y");
            if (_removeZ) names.Add("Z");
            if (_removeYaw) names.Add("turning");
            string label = $"Remove root motion ({string.Join(", ", names)})";
            return Work(_ =>
            {
                var result = ClipEdit.RemoveRootMotion(clip, root, axes);
                return new ClipToolResult(result, label,
                [
                    $"Root: {rootName}",
                    $"Travel from start to end: {ClipToolSummary.Vector(ClipToolSummary.Travel(clip, root))} → {ClipToolSummary.Vector(ClipToolSummary.Travel(result, root))}",
                    KeyDiff.Of(clip, result).KeysLine(),
                ]);
            });
        }
        var scale = ScaleVector;
        if (!float.IsFinite(scale.X) || !float.IsFinite(scale.Y) || !float.IsFinite(scale.Z)) throw new ArgumentException("The scale must be a number on every axis.");
        if (scale == Vector3.One) return NothingWork("Enter a scale other than 1 on at least one axis.");
        string scaleLabel = string.Format(CultureInfo.CurrentCulture, "Scale root motion ({0:0.##}, {1:0.##}, {2:0.##})", scale.X, scale.Y, scale.Z);
        return Work(_ =>
        {
            var result = ClipEdit.ScaleRootMotion(clip, root, scale);
            return new ClipToolResult(result, scaleLabel,
            [
                $"Root: {rootName}",
                $"Travel from start to end: {ClipToolSummary.Vector(ClipToolSummary.Travel(clip, root))} → {ClipToolSummary.Vector(ClipToolSummary.Travel(result, root))}",
                KeyDiff.Of(clip, result).KeysLine(),
            ]);
        });
    }
}

/// <summary>Clip › Set Bone Lengths: from a reference clip (<see cref="ClipEdit.SetBoneLengthsFromClip"/>) or the bind pose.</summary>
public sealed class BoneLengthsToolViewModel : ClipToolViewModel
{
    private readonly Skeleton? _skeleton;
    private bool _fromBind;

    public BoneLengthsToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        _skeleton = document.FittingSkeleton;
        var snapshot = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        var compatible = snapshot.CompatibleClips(Original.BoneCount);
        var preferred = document.Scene.MeshName is { } mesh ? snapshot.DefaultPreviewClip(mesh, usage) : null;
        var ordered = compatible
            .OrderBy(c => ReferenceEquals(c, preferred) ? 0 : c.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new LibraryClipChoice(c, ReferenceEquals(c, preferred) ? "tables: stand" : $"{c.BoneCount} bones"));
        References = new FilteredList<LibraryClipChoice>(ordered, c => c.Clip.Name);
        References.Selected = References.All.FirstOrDefault();
        References.SelectionChanged += (_, _) => OnParametersChanged();
        _fromBind = References.IsEmpty && _skeleton is not null;
        Start();
    }

    public override string ToolId => "lengths";

    public override string Title => "Set Bone Lengths";

    public override string Heading => "Set bone lengths";

    public override string Description =>
        "Makes every chosen bone's position keys constant at a reference length: the reference clip's (usually the character's "
        + "stand clip) or the preview mesh's bind pose. Root bones keep their motion.";

    public override bool SupportsBoneScope => true;

    /// <summary>Reference clips: compatible clips, the stand clip first.</summary>
    public FilteredList<LibraryClipChoice> References { get; }

    public bool IsFromClip
    {
        get => !_fromBind;
        set => IsFromBind = !value;
    }

    public bool IsFromBind
    {
        get => _fromBind;
        set
        {
            if (SetParameter(ref _fromBind, value)) Raise(nameof(IsFromClip));
        }
    }

    public bool HasSkeleton => _skeleton is not null;

    /// <summary>"The bind pose of ult2_guard.v3c", or why it is not available.</summary>
    public string BindText => _skeleton is not null
        ? $"The bind (rest) pose of {Document.Scene.MeshName}"
        : "The bind pose needs a preview mesh with as many bones as the clip.";

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var skeleton = _skeleton ?? throw new ArgumentException(
            $"Setting bone lengths needs to know which bones are roots: pick a preview mesh with {Original.BoneCount} bones first.");
        if (ScopeBones is { Count: 0 }) throw new ArgumentException("No bones in scope: select bones (or keys) first, or use every bone.");
        var clip = Original;
        var bones = ScopeBones;
        string scope = ScopeText;
        string[] boneNames = [.. Enumerable.Range(0, clip.BoneCount).Select(BoneName)];
        if (_fromBind)
        {
            string label = "Set bone lengths from the bind pose";
            string mesh = Document.Scene.MeshName ?? "the preview mesh";
            return Work(_ =>
            {
                var result = ClipEdit.SetBoneLengthsFromBind(clip, skeleton, bones);
                return new ClipToolResult(result, label, Summary(clip, result, boneNames, $"Reference: {mesh}'s bind pose", scope));
            });
        }
        if (References.Selected is not { } choice) return NothingWork("Pick the reference clip (usually the character's stand clip).");
        var library = choice.Clip;
        var assets = Shell.Assets;
        string fromClip = $"Set bone lengths from {library.Name}";
        return async ct =>
        {
            var reference = await assets.LoadClipAsync(library, ct).ConfigureAwait(false);
            if (reference.BoneCount != clip.BoneCount)
                throw new ArgumentException($"{library.Name} has {reference.BoneCount} bones but the clip has {clip.BoneCount}; bones match by index.");
            var result = ClipEdit.SetBoneLengthsFromClip(clip, reference, skeleton.Parents, bones);
            return new ClipToolResult(result, fromClip, Summary(clip, result, boneNames, $"Reference: {library.Name}", scope), reference);
        };
    }

    private static IReadOnlyList<string> Summary(RfaClip before, RfaClip after, string[] names, string reference, string scope)
    {
        int changed = 0, worst = -1;
        float worstDelta = 0;
        for (int i = 0; i < Math.Min(before.BoneCount, after.BoneCount); i++)
        {
            if (ReferenceEquals(before.Bones[i], after.Bones[i])) continue;
            var a = before.Bones[i].PositionKeys;
            var b = after.Bones[i].PositionKeys;
            if (a.Length == 0 || b.Length == 0) continue;
            float delta = MathF.Abs(a[0].Position.Length() - b[0].Position.Length());
            changed++;
            if (delta > worstDelta)
            {
                worstDelta = delta;
                worst = i;
            }
        }
        var lines = new List<string>
        {
            reference + $" · scope: {scope} (roots are never changed)",
            $"{ClipToolSummary.Count(changed, "bone")} get constant position keys"
                + (worst >= 0 ? $"; largest length change {ClipToolSummary.Metres(worstDelta)} ({names[worst]})" : "."),
            KeyDiff.Of(before, after).KeysLine(),
        };
        return lines;
    }
}

/// <summary>Clip › Conform to Skeleton: <see cref="ClipEdit.ConformToSkeleton"/> for a library mesh.</summary>
public sealed class ConformToolViewModel : ClipToolViewModel
{
    private readonly IReadOnlyList<string>? _names;

    /// <param name="document">The clip document.</param>
    /// <param name="meshName">A mesh to pick at once (a Problems quick fix names it), or null to let the user pick.</param>
    public ConformToolViewModel(ClipDocumentViewModel document, string? meshName = null) : base(document)
    {
        _names = document.ClipboardBoneNames;
        var snapshot = Shell.Assets.Snapshot;
        var meshes = snapshot.Meshes.Where(m => m.HasSkeleton)
            .OrderBy(m => m.BoneCount == Original.BoneCount ? 1 : 0)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new LibraryMeshChoice(m, m.BoneCount == Original.BoneCount ? $"{m.BoneCount} bones · same count" : $"{m.BoneCount} bones"));
        Meshes = new FilteredList<LibraryMeshChoice>(meshes, m => m.Mesh.Name);
        if (!string.IsNullOrWhiteSpace(meshName) && snapshot.FindMesh(meshName) is { } wanted)
            Meshes.Selected = Meshes.All.FirstOrDefault(m => string.Equals(m.Mesh.Name, wanted.Name, StringComparison.OrdinalIgnoreCase));
        Meshes.SelectionChanged += (_, _) => OnParametersChanged();
        Start();
    }

    public override string ToolId => "conform";

    public override string Title => "Conform to Skeleton";

    public override string Heading => "Conform the clip to another skeleton";

    public override string Description =>
        "Re-lays the clip out for another mesh's bone list, matching bones by name: matched tracks move to the new index unchanged, "
        + "bones the mesh lacks are dropped, and its bones the clip lacks get a rest-pose track.";

    /// <summary>Meshes with a skeleton, other bone counts first.</summary>
    public FilteredList<LibraryMeshChoice> Meshes { get; }

    public bool HasNames => _names is not null;

    /// <summary>Where the clip's bone names come from, or why there are none.</summary>
    public string NamesText => _names is not null
        ? $"The clip's bone names come from {Document.Scene.MeshName} (a clip stores none)."
        : $"A clip stores no bone names; they come from its preview mesh, and no preview mesh with {Original.BoneCount} bones is loaded. "
          + "Pick a preview mesh the clip was made for first, then conform.";

    /// <summary>The last result's details.</summary>
    public ConformResult? Conform { get; private set; }

    protected override void OnResult(ClipToolResult result)
    {
        Conform = result.Extra as ConformResult;
        Raise(nameof(Conform));
    }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var names = _names ?? throw new ArgumentException(
            $"The clip's bones have no names: no preview mesh with {Original.BoneCount} bones is loaded. Pick the mesh the clip was made for as the preview mesh first.");
        if (Meshes.Selected is not { } choice) return NothingWork("Pick the mesh whose skeleton the clip should fit.");
        var clip = Original;
        var library = choice.Mesh;
        var assets = Shell.Assets;
        string label = $"Conform to {library.Name}";
        string preview = Document.Scene.MeshName ?? "the preview mesh";
        return async ct =>
        {
            var file = await assets.LoadMeshAsync(library, ct).ConfigureAwait(false);
            var target = Skeleton.FromFile(file);
            if (target.Count == 0) throw new ArgumentException($"{library.Name} has no skeleton.");
            var r = ClipEdit.ConformToSkeleton(clip, names, target);
            // Morph data addresses the vertices of the mesh the clip was made for: on another mesh it would
            // move arbitrary vertices (or write past a smaller mesh's buffer), so it does not come along.
            bool sameMesh = string.Equals(library.Name, preview, StringComparison.OrdinalIgnoreCase);
            bool droppedMorph = !sameMesh && !r.Clip.Morph.IsEmpty;
            if (droppedMorph) r = r with { Clip = ClipEdit.StripMorph(r.Clip) };
            int matched = r.SourceOfTarget.Count(s => s >= 0);
            int moved = Enumerable.Range(0, r.SourceOfTarget.Length).Count(i => r.SourceOfTarget[i] >= 0 && r.SourceOfTarget[i] != i);
            var lines = new List<string>
            {
                $"Bones: {clip.BoneCount} → {r.Clip.BoneCount} ({matched} matched by name, {moved} move to a new index)",
                $"Added with a rest pose ({r.AddedBones.Length}): {ClipToolSummary.Names(r.AddedBones)}",
                $"Dropped ({r.DroppedBones.Length}): {ClipToolSummary.Names(r.DroppedBones)}",
            };
            if (droppedMorph)
                lines.Add($"The morph (vertex) animation is dropped: it moves vertices of {preview}, not of {library.Name}.");
            if (r.Clip.BoneCount != clip.BoneCount)
                lines.Add($"The preview still uses {preview}; pick {library.Name} as the preview mesh after OK to see the result on its skeleton.");
            return new ClipToolResult(r.Clip, label, lines, r);
        };
    }
}

/// <summary>Which bones <see cref="WeightsToolViewModel"/> sets.</summary>
public enum WeightTarget
{
    All,
    UpperBody,
    LowerBody,
    Selected,
    Subtree,
}

/// <summary>Clip › Set Weights: <see cref="ClipEdit.SetBoneWeights"/> on a preset bone set.</summary>
public sealed class WeightsToolViewModel : ClipToolViewModel
{
    private readonly Skeleton? _skeleton;
    private WeightTarget _target;
    private double _weight = 10;

    public WeightsToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        _skeleton = document.FittingSkeleton;
        _target = SelectedBones.Count > 0 ? WeightTarget.Selected : WeightTarget.All;
        SetWeightCommand = new RelayCommand(p =>
        {
            if (p is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)) Weight = w;
        });
        Start();
    }

    public override string ToolId => "weights";

    public override string Title => "Set Weights";

    public override string Heading => "Set bone weights";

    public override string Description =>
        "A bone's weight decides how strongly this clip drives it when it plays as an action over a state: 10 replaces the state, "
        + "5 shares the bone half and half, 0 leaves it to the state. Keys are untouched.";

    public bool HasSkeleton => _skeleton is not null;

    public string SkeletonNote => _skeleton is not null
        ? $"Body parts come from {Document.Scene.MeshName}'s bone names and hierarchy."
        : "Upper body, lower body and children need a preview mesh with as many bones as the clip.";

    public WeightTarget Target
    {
        get => _target;
        set
        {
            if (SetParameter(ref _target, value))
                RaiseAll(nameof(IsAll), nameof(IsUpper), nameof(IsLower), nameof(IsSelected), nameof(IsSubtree));
        }
    }

    public bool IsAll { get => _target == WeightTarget.All; set { if (value) Target = WeightTarget.All; } }

    public bool IsUpper { get => _target == WeightTarget.UpperBody; set { if (value) Target = WeightTarget.UpperBody; } }

    public bool IsLower { get => _target == WeightTarget.LowerBody; set { if (value) Target = WeightTarget.LowerBody; } }

    public bool IsSelected { get => _target == WeightTarget.Selected; set { if (value) Target = WeightTarget.Selected; } }

    public bool IsSubtree { get => _target == WeightTarget.Subtree; set { if (value) Target = WeightTarget.Subtree; } }

    /// <summary>The weight to set (stock values 2, 4, 5, 10).</summary>
    public double Weight
    {
        get => _weight;
        set => SetParameter(ref _weight, value);
    }

    /// <summary>Sets <see cref="Weight"/> from a preset button's parameter ("10").</summary>
    public RelayCommand SetWeightCommand { get; }

    /// <summary>The bones of the current target (empty when the target cannot be resolved).</summary>
    public IReadOnlyList<int> TargetBones()
    {
        int count = Original.BoneCount;
        return _target switch
        {
            WeightTarget.All => WeightPreset.All(count),
            WeightTarget.UpperBody when _skeleton is not null => WeightPreset.UpperBody(_skeleton),
            WeightTarget.LowerBody when _skeleton is not null => WeightPreset.LowerBody(_skeleton),
            WeightTarget.Selected => SelectedBones,
            WeightTarget.Subtree when _skeleton is not null => [.. SelectedBones.SelectMany(b => WeightPreset.Subtree(_skeleton.Parents, b, true)).Distinct().Order()],
            _ => [],
        };
    }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        if (!double.IsFinite(_weight) || _weight < 0) throw new ArgumentException("The weight must be zero or more.");
        var bones = TargetBones().Where(b => b < Original.BoneCount).ToList();
        if (bones.Count == 0)
        {
            throw new ArgumentException(_target switch
            {
                WeightTarget.Selected or WeightTarget.Subtree when SelectedBones.Count == 0 => "No bones are selected: select bones in the viewport first, or pick another set.",
                WeightTarget.UpperBody or WeightTarget.LowerBody or WeightTarget.Subtree when _skeleton is null => "This set needs a preview mesh with as many bones as the clip.",
                _ => "No bone of the clip is in this set.",
            });
        }
        var clip = Original;
        float weight = (float)_weight;
        string target = _target switch
        {
            WeightTarget.All => "all bones",
            WeightTarget.UpperBody => "upper body",
            WeightTarget.LowerBody => "lower body",
            WeightTarget.Selected => bones.Count == 1 ? BoneName(bones[0]) : "selected bones",
            _ => "selected bones and children",
        };
        string label = string.Format(CultureInfo.CurrentCulture, "Set weights ({0} → {1:0.##})", target, weight);
        string names = ClipToolSummary.Names([.. bones.Select(BoneName)], 8);
        return Work(_ =>
        {
            var result = ClipEdit.SetBoneWeights(clip, bones, weight);
            var was = bones.GroupBy(b => clip.Bones[b].Weight).OrderByDescending(g => g.Count())
                .Select(g => string.Format(CultureInfo.CurrentCulture, "{0:0.##} on {1}", g.Key, ClipToolSummary.Count(g.Count(), "bone")));
            int changed = bones.Count(b => clip.Bones[b].Weight != weight);
            return new ClipToolResult(result, label,
            [
                string.Format(CultureInfo.CurrentCulture, "{0} get weight {1:0.##} ({2} of them change)", ClipToolSummary.Count(bones.Count, "bone"), weight, changed),
                "Before: " + string.Join(", ", was),
                "Bones: " + names,
            ]);
        });
    }
}
