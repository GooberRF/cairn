using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Retarget;

/// <summary>Where the target root's translation comes from.</summary>
/// <remarks>
/// Phase 7a retired <c>ScaleByLegLength</c> (it scaled the root's raw model-space keys, which is wrong
/// because the stock rigs' roots sit at different heights relative to their pelvis and ground, and it
/// left the foot targets unscaled). <see cref="HipHeight"/> replaces it; saved profiles that name it are
/// read as <see cref="HipHeight"/> (<see cref="RetargetPresets.MigrateOptionsJson"/>).
/// </remarks>
public enum RootMode
{
    /// <summary>
    /// The reference's rule: place the target root so the target PELVIS lands where the source
    /// pelvis was (a seated rider stays on the seat). Needs a pelvis that is a child of the root on
    /// both rigs.
    /// </summary>
    AnchorPelvis,

    /// <summary>
    /// For standing and locomotion: the target's hip joints keep the source's horizontal position
    /// (scaled by the leg-length ratio with <see cref="RetargetOptions.ScaleStride"/>) and stand above
    /// the TARGET's ground as high as the source's stand above the source's ground, the leg part scaled
    /// by the leg-length ratio. Each rig's ground (the lowest foot point) and ankle height come from
    /// its reference (stand) clip. Leg IK then puts the feet on the target's ground where the source's
    /// feet touch its own (<see cref="GroundMapping"/>). Needs a leg IK chain on both rigs.
    /// </summary>
    HipHeight,

    /// <summary>Copy the source root's position keys as they are.</summary>
    CopySource,

    /// <summary>The root holds the target's own root offset (from the bone-length source) for the whole clip.</summary>
    KeepInPlace,
}

/// <summary>A ready-made set of retarget options for one kind of clip (<see cref="RetargetPresets"/>).</summary>
public enum RetargetPreset
{
    /// <summary>
    /// Seated / fixed controls: the reference method exactly (anchor the pelvis, IK hands and feet onto
    /// the source's positions). For riders and turret gunners whose hands and feet hold controls fixed
    /// in seat space.
    /// </summary>
    Seated,

    /// <summary>
    /// Standing / locomotion: hip-height root, IK on the legs only (feet on the ground where the
    /// source's touch it), arms by rotation transfer. For stands, walks, runs, crouches, jumps, deaths.
    /// </summary>
    Locomotion,

    /// <summary>Rotation only: no IK at all, hip-height root. Every joint angle exactly as the source's.</summary>
    RotationOnly,

    /// <summary>The options match none of the presets.</summary>
    Custom,
}

/// <summary>Where the target's bone offsets (position keys of non-root bones) come from.</summary>
public enum BoneLengthSource
{
    /// <summary>The target reference (stand) clip's first position key per bone: the AnimType's own proportions. The reference's choice.</summary>
    ReferenceClip,

    /// <summary>The target mesh's bind pose offsets.</summary>
    TargetBind,

    /// <summary>The source clip's own position keys (the source's proportions) for mapped bones.</summary>
    Source,
}

/// <summary>The still pose of target bones that have no source bone.</summary>
public enum ExtraBonePose
{
    /// <summary>The first rotation key of the bone in the target reference (stand) clip. The reference's choice.</summary>
    ReferenceClip,

    /// <summary>The bone's bind-pose rotation relative to its parent.</summary>
    Bind,
}

/// <summary>
/// Retarget settings. <see cref="Default"/> is the reference's behaviour (the
/// <see cref="RetargetPreset.Seated"/> preset); <see cref="RetargetPresets.Options"/> gives the others.
/// </summary>
public sealed record RetargetOptions
{
    /// <summary>The reference's settings: rest alignment, IK on every chain, pelvis anchor, reference-clip lengths and extra poses, keys kept.</summary>
    public static RetargetOptions Default { get; } = new();

    /// <summary>The pure frame-offset transfer (no rest alignment, no IK), as the reference's self-tests use.</summary>
    public static RetargetOptions PureFrameOffset { get; } = new() { RestAlignment = false, Ik = false };

    /// <summary>
    /// Swing each target bone's rest so its direction to its primary child matches the source rest
    /// before the frame offsets are taken (stock T-poses disagree by up to 16 degrees per limb).
    /// </summary>
    public bool RestAlignment { get; init; } = true;

    /// <summary>Run two-bone IK on the target profile's chains after the transfer (see <see cref="IkArms"/> and <see cref="IkLegs"/>).</summary>
    public bool Ik { get; init; } = true;

    /// <summary>With <see cref="Ik"/>: run it on the arm chains (every chain that is not a leg).</summary>
    public bool IkArms { get; init; } = true;

    /// <summary>With <see cref="Ik"/>: run it on the leg chains (<see cref="RigProfile.IsLegChain"/>).</summary>
    public bool IkLegs { get; init; } = true;

    /// <summary>IK chains to leave out, by their upper bone's canonical name (e.g. <c>upperleg-l</c>).</summary>
    public ImmutableArray<string> DisabledIkChains { get; init; } = [];

    /// <summary>How the target root's translation is made.</summary>
    public RootMode RootMode { get; init; } = RootMode.AnchorPelvis;

    /// <summary>
    /// With <see cref="RootMode.HipHeight"/>: scale horizontal motion (root travel, hip and foot
    /// positions) by the leg-length ratio too, so strides stay in proportion to the legs. Off (the
    /// default) keeps the source's horizontal motion, so the feet keep pace with the entity's movement,
    /// which the game drives per class, not per clip (the stock runs of all four rigs move their
    /// planted feet at the same 3.7-4.1 m/s whatever the leg length).
    /// </summary>
    public bool ScaleStride { get; init; }

    /// <summary>
    /// For two-handed holds: IK the off (left) hand onto the point the source's left hand held
    /// relative to its right hand, wherever the target's right hand ends up, while the source hands
    /// are within <see cref="OffHandGripDistance"/> of each other (fading out over 10 cm beyond).
    /// Applies whether or not the arms are otherwise IK'd.
    /// </summary>
    public bool OffHandFollowsMainHand { get; init; }

    /// <summary>Metres: the source hands' distance under which <see cref="OffHandFollowsMainHand"/> holds the off hand fully.</summary>
    public double OffHandGripDistance { get; init; } = 0.45;

    /// <summary>Where the target's bone offsets come from.</summary>
    public BoneLengthSource BoneLengths { get; init; } = BoneLengthSource.ReferenceClip;

    /// <summary>The still pose of target bones without a source.</summary>
    public ExtraBonePose ExtraBonePose { get; init; } = ExtraBonePose.ReferenceClip;

    /// <summary>
    /// Null keeps the source's key times wherever the algebra allows (eases too). A tick count
    /// resamples every rotation track at start, start + step, ..., end instead (eases 0).
    /// </summary>
    public int? ResampleStep { get; init; }

    /// <summary>
    /// How rotation keys are rounded to int16. <see cref="KeyQuantization.WithinUnit"/> (the default)
    /// never stores a key longer than 1, so the game interpolates every segment;
    /// <see cref="KeyQuantization.Reference"/> rounds half to even exactly as retarget.py and reproduces
    /// its outputs byte for byte (some of their slow segments then snap in game).
    /// </summary>
    public KeyQuantization Quantization { get; init; } = KeyQuantization.WithinUnit;

    /// <summary><see cref="Default"/> with the reference's rounding: reproduces retarget.py's files byte for byte.</summary>
    public static RetargetOptions Reference { get; } = new() { Quantization = KeyQuantization.Reference };
}

/// <summary>How the retargeter rounds rotation keys (see <see cref="RetargetOptions.Quantization"/>).</summary>
public enum KeyQuantization
{
    /// <summary>Round to nearest, then shorten any key longer than 1 by single units.</summary>
    WithinUnit,
    /// <summary>Round half to even, as retarget.py (Python's <c>round</c>).</summary>
    Reference,
}

/// <summary>One side of a retarget: a skeleton (from a T-pose rest mesh), its profile, and for a target its reference clip.</summary>
public sealed record RetargetRig
{
    /// <param name="skeleton">The skeleton of the rig's REST mesh (a T-pose; see <see cref="RigProfile.RestMesh"/>).</param>
    /// <param name="profile">The rig's profile.</param>
    public RetargetRig(Skeleton skeleton, RigProfile profile)
    {
        Skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <summary>The skeleton of the rest mesh.</summary>
    public Skeleton Skeleton { get; init; }

    /// <summary>The rig's profile.</summary>
    public RigProfile Profile { get; init; }

    /// <summary>
    /// The rig's reference (stand) clip: supplies bone lengths and the pose of extra bones when the
    /// rig is the TARGET, and on either side the rig's ground (lowest foot point) and standing ankle
    /// height for <see cref="RootMode.HipHeight"/>. Must have the skeleton's bone count.
    /// </summary>
    public RfaClip? ReferenceClip { get; init; }

    /// <summary>The reference clip's file name, for report lines (defaults to the profile's).</summary>
    public string? ReferenceClipName { get; init; }

    /// <summary>
    /// The rest mesh's bind rotations exactly as stored (see <see cref="FromMesh"/>). When present
    /// they are normalised in double precision, exactly as the reference does; otherwise the
    /// skeleton's single-precision rest rotations are re-normalised in double precision, which
    /// differs by at most ~2e-8 per component (on the nine stock goldens this changes no byte).
    /// </summary>
    public ImmutableArray<Quaternion> BindRotations { get; init; } = [];

    /// <summary>The rig of a rest mesh: its skeleton, its stored bind rotations, and a profile (the matching built-in, else a generic one, when null).</summary>
    public static RetargetRig FromMesh(V3dFile restMesh, RigProfile? profile = null, RfaClip? referenceClip = null, string? referenceClipName = null)
    {
        ArgumentNullException.ThrowIfNull(restMesh);
        var skeleton = Skeleton.FromFile(restMesh);
        return new RetargetRig(skeleton, profile ?? RigProfiles.For(skeleton))
        {
            ReferenceClip = referenceClip,
            ReferenceClipName = referenceClipName,
            BindRotations = [.. restMesh.Bones.Select(b => b.Rotation)],
        };
    }
}

/// <summary>Everything one retarget needs.</summary>
/// <param name="SourceClip">The clip to retarget; it must be made for <paramref name="Source"/>'s skeleton.</param>
/// <param name="Source">The source rig.</param>
/// <param name="Target">The target rig (with its reference clip).</param>
public sealed record RetargetRequest(RfaClip SourceClip, RetargetRig Source, RetargetRig Target)
{
    /// <summary>The bone map; null maps automatically with <see cref="BoneMapper"/>.</summary>
    public BoneMap? BoneMap { get; init; }

    /// <summary>Settings.</summary>
    public RetargetOptions Options { get; init; } = RetargetOptions.Default;
}

/// <summary>One limb end IK was asked to hold, and how well the finished clip holds it.</summary>
/// <param name="Chain">The chain's upper bone (canonical), e.g. <c>upperleg-l</c>.</param>
/// <param name="EndBone">The held joint (canonical), e.g. <c>foot-l</c>.</param>
/// <param name="TargetIndex">Its target bone index.</param>
/// <param name="IsLeg">True for a leg chain (<see cref="RigProfile.IsLegChain"/>).</param>
/// <param name="HeldTo">What it is held to, in plain words ("the source's position", "the ground contact", "the right hand").</param>
/// <param name="Samples">Times measured.</param>
/// <param name="StretchedSamples">Times the wanted point was out of the limb's reach (the limb fully stretched, or folded shut).</param>
/// <param name="MaxErrorCm">Largest distance between the joint and the wanted point, cm.</param>
/// <param name="MeanErrorCm">Mean of that distance, cm.</param>
/// <param name="MaxOverreachCm">How far beyond the limb's reach the wanted point went at worst, cm (0 when always reachable).</param>
public sealed record PinnedContact(
    string Chain, string EndBone, int TargetIndex, bool IsLeg, string HeldTo,
    int Samples, int StretchedSamples, double MaxErrorCm, double MeanErrorCm, double MaxOverreachCm)
{
    /// <summary>True when the limb could not reach at some time (the error is then the shortfall, not a transfer error).</summary>
    public bool Stretched => StretchedSamples > 0;

    /// <summary>"leg fully stretched" / "arm fully stretched", or null when always reachable.</summary>
    public string? StretchNote => Stretched ? $"{(IsLeg ? "leg" : "arm")} fully stretched at {StretchedSamples} of {Samples} samples" : null;
}

/// <summary>
/// How <see cref="RootMode.HipHeight"/> maps the source's lower body onto the target's ground. Heights
/// are model-space y in metres. A point at height y above the source becomes
/// <c>TargetGround + TargetAnkleHeight + (y - SourceGround - SourceAnkleHeight) * LegRatio</c> for the
/// hips; a foot's lowest point (ankle or toe) at <c>SourceGround + h</c> goes to <c>TargetGround + h * LegRatio</c>;
/// horizontal positions are multiplied by <see cref="HorizontalScale"/>.
/// </summary>
/// <param name="SourceGround">The source's ground: the lowest foot point (ankle or toe joint) over its reference clip.</param>
/// <param name="TargetGround">The target's ground, likewise from its reference clip.</param>
/// <param name="SourceAnkleHeight">The source's ankle height above its ground when standing.</param>
/// <param name="TargetAnkleHeight">The target's ankle height above its ground when standing.</param>
/// <param name="LegRatio">
/// The target's mean leg (thigh + shin) over the source's, lowered (by at most 8 %) where a leg held on
/// the ground could not otherwise reach its foot.
/// </param>
/// <param name="HorizontalScale">1, or <see cref="LegRatio"/> with <see cref="RetargetOptions.ScaleStride"/>.</param>
/// <param name="SourceFrom">Where the source's ground came from (a clip name, or "the source clip itself").</param>
/// <param name="TargetFrom">Where the target's ground came from.</param>
public sealed record GroundMapping(
    double SourceGround, double TargetGround, double SourceAnkleHeight, double TargetAnkleHeight,
    double LegRatio, double HorizontalScale, string SourceFrom, string TargetFrom);

/// <summary>The outcome of <see cref="Retargeter.Retarget"/>.</summary>
public sealed record RetargetResult
{
    /// <summary>True when <see cref="Clip"/> holds the retargeted clip.</summary>
    public bool Success { get; init; }

    /// <summary>When the retarget could not run: what is wrong and how to fix it, in plain words.</summary>
    public string? Error { get; init; }

    /// <summary>The retargeted clip (target bone order), or null on failure.</summary>
    public RfaClip? Clip { get; init; }

    /// <summary>The bone map used (the automatic one when the request had none); null when the inputs were unusable before mapping.</summary>
    public BoneMap? BoneMap { get; init; }

    /// <summary>The reference-style report: static bones, reparented chains, root placement, IK.</summary>
    public ImmutableArray<string> ReportLines { get; init; } = [];

    /// <summary>Things the user should know (morph data dropped, IK chains skipped, ...).</summary>
    public ImmutableArray<string> Warnings { get; init; } = [];

    /// <summary>
    /// What IK was asked to hold (one entry per chain that ran), measured on the finished clip with the
    /// engine's sampler every 160 ticks: should be ~0 cm unless the limb could not reach.
    /// </summary>
    public ImmutableArray<PinnedContact> Contacts { get; init; } = [];

    /// <summary>The ground mapping <see cref="RootMode.HipHeight"/> used, or null for the other root modes.</summary>
    public GroundMapping? Ground { get; init; }

    internal static RetargetResult Fail(string error, BoneMap? map = null, IEnumerable<string>? warnings = null) =>
        new() { Success = false, Error = error, BoneMap = map, Warnings = warnings is null ? [] : [.. warnings] };
}
