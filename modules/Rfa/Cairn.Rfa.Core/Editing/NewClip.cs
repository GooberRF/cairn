using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Editing;

/// <summary>How a new clip is meant to play.</summary>
public enum NewClipKind
{
    /// <summary>A state: loops under everything else; the engine ignores its ramps, so they are 0.</summary>
    State,

    /// <summary>An action: plays once over the state, fading in and out over its ramps.</summary>
    Action,
}

/// <summary>Options for <see cref="NewClip.Create(Skeleton, NewClipOptions)"/>. Times are in ticks.</summary>
public sealed record NewClipOptions
{
    /// <summary>
    /// The ramp in and ramp out a new action gets: 480 ticks (0.1 s, 3 frames) each. That is the median
    /// ramp in and the median ramp out of the 516 stock clips the tables play as actions, and also their
    /// most common pair (151 of them use exactly 480 / 480; the next is 800 / 800).
    /// </summary>
    public const int DefaultActionRamp = 480;

    /// <summary>A new clip's default length: one second (30 frames).</summary>
    public const int DefaultLength = RfaClip.TicksPerSecond;

    /// <summary>Clip length (end minus start), at least one tick.</summary>
    public int Length { get; init; } = DefaultLength;

    /// <summary>Start time; stock clips start at frame 1 (tick 160).</summary>
    public int StartTime { get; init; } = RfaClip.TicksPerFrame;

    /// <summary>File version, 7 or 8.</summary>
    public int Version { get; init; } = 8;

    /// <summary>Every bone's weight, 0 to 10.</summary>
    public float Weight { get; init; } = 10f;

    /// <summary>Ramp in, ticks (actions; see <see cref="ForKind"/>).</summary>
    public int RampIn { get; init; }

    /// <summary>Ramp out, ticks.</summary>
    public int RampOut { get; init; }

    /// <summary>
    /// The clip whose pose at <see cref="PoseTime"/> every bone holds (usually the character's stand clip,
    /// which carries the bone lengths every other clip of that character carries); null holds the mesh's
    /// bind (rest) pose. Bones are matched by index, so it must have the skeleton's bone count.
    /// </summary>
    public RfaClip? PoseClip { get; init; }

    /// <summary>The time in <see cref="PoseClip"/>'s own timeline to take the pose from (sampled as the engine does).</summary>
    public float PoseTime { get; init; } = RfaClip.TicksPerFrame;

    /// <summary>The defaults for a kind: a state has no ramps, an action <see cref="DefaultActionRamp"/> each.</summary>
    public static NewClipOptions ForKind(NewClipKind kind) => kind == NewClipKind.Action
        ? new NewClipOptions { RampIn = DefaultActionRamp, RampOut = DefaultActionRamp }
        : new NewClipOptions();
}

/// <summary>Why <see cref="NewClip.SuggestPoseClip"/> picked a clip.</summary>
public enum NewClipPoseReason
{
    /// <summary>The tables give a class using the mesh this clip as its "stand" state.</summary>
    TableStand,

    /// <summary>The built-in rig profile matching the skeleton names it as the rig's stand clip.</summary>
    RigProfile,
}

/// <summary>A suggested starting-pose clip for a new clip.</summary>
/// <param name="ClipName">The clip's file name ("ult2_stand.rfa").</param>
/// <param name="Reason">Where the suggestion came from.</param>
/// <param name="ClassName">The class whose stand state it is (<see cref="NewClipPoseReason.TableStand"/>), else the rig profile's name.</param>
public sealed record NewClipPoseSuggestion(string ClipName, NewClipPoseReason Reason, string ClassName);

/// <summary>
/// Builds a clip from nothing: a correct starting pose held on every bone of a mesh, ready to be posed.
/// Every bone gets two rotation keys and two position keys (at the start and the end) holding the pose:
/// the engine needs position keys on every bone (RFA003) and reads past a lone rotation key (RFA015).
/// Rotations are quantised never longer than 1 (<see cref="RfaRotKey.QuantizeWithinUnit"/>), both keys of
/// a track identical (so sign-continuous), eases 0; position tracks are constant with both control points
/// equal to the position (they are absolute Bezier points: zeros would swing the bone through its
/// parent's joint, RFA020). No morph data; the header's start, end, ramps and version as asked; one track
/// per mesh bone in the mesh's order.
/// </summary>
public static class NewClip
{
    /// <summary>Builds a new clip for <paramref name="skeleton"/> (see the class remarks).</summary>
    /// <exception cref="ArgumentException">The skeleton has no bones or more than the engine poses, or an option is out of range.</exception>
    public static RfaClip Create(Skeleton skeleton, NewClipOptions options)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(options);
        Validate(skeleton, options);

        int start = options.StartTime;
        int end = start + options.Length;
        var tracks = ImmutableArray.CreateBuilder<RfaBoneTrack>(skeleton.Count);
        for (int b = 0; b < skeleton.Count; b++)
        {
            var (rotation, position) = StartingPose(skeleton, options, b);
            var first = ShortenToUnit(ClipEdit.QuantizeRotation(start, rotation, null));
            tracks.Add(new RfaBoneTrack(
                options.Weight,
                [first, first with { Time = end }],
                [RfaPosKey.Constant(start, position), RfaPosKey.Constant(end, position)]));
        }

        return new RfaClip
        {
            Version = options.Version,
            StartTime = start,
            EndTime = end,
            RampIn = options.RampIn,
            RampOut = options.RampOut,
            Bones = tracks.MoveToImmutable(),
            Morph = RfaMorph.Empty,
        };
    }

    /// <summary>Builds a new clip for a character mesh's skeleton.</summary>
    /// <exception cref="ArgumentException">The mesh has no bones (a static mesh), or an option is out of range.</exception>
    public static RfaClip Create(V3dFile mesh, NewClipOptions options)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return Create(Skeleton.FromFile(mesh), options);
    }

    /// <summary>
    /// The pose bone <paramref name="bone"/> starts in, as a local rotation (active convention) and a local
    /// position: the pose clip sampled at its time, or the bind pose. A pose-clip track with no rotation keys
    /// (or no position keys) takes that part from the bind pose instead of the engine's identity (or origin).
    /// </summary>
    public static (Quaternion Rotation, Vector3 Position) StartingPose(Skeleton skeleton, NewClipOptions options, int bone)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(options);
        if ((uint)bone >= (uint)skeleton.Count) throw new ArgumentOutOfRangeException(nameof(bone));
        var rest = skeleton.RestLocal[bone];
        if (options.PoseClip is not { } clip || bone >= clip.BoneCount) return (rest.Rotation, rest.Position);
        var track = clip.Bones[bone];
        var rotation = track.RotationKeys.IsDefaultOrEmpty ? rest.Rotation : ClipSampler.SampleRotation(track.RotationKeys.AsSpan(), options.PoseTime);
        var position = track.PositionKeys.IsDefaultOrEmpty ? rest.Position : ClipSampler.SamplePosition(track.PositionKeys.AsSpan(), options.PoseTime);
        return (rotation, position);
    }

    /// <summary>Why <paramref name="options"/> cannot make a clip for <paramref name="skeleton"/>, in plain words, or null when they can.</summary>
    public static string? Problem(Skeleton skeleton, NewClipOptions options)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(options);
        if (skeleton.Count == 0) return "The mesh has no bones: clips only play on character meshes (.v3c) with a skeleton.";
        if (skeleton.Count > RfaClip.MaxEngineBones)
            return $"The mesh has {skeleton.Count} bones; the engine poses at most {RfaClip.MaxEngineBones}, so no clip can play on it.";
        if (options.Length < 1) return "The clip needs a length of at least one tick.";
        if (options.StartTime < 0) return "The start time cannot be negative.";
        if ((long)options.StartTime + options.Length > int.MaxValue) return "The clip ends too late to be stored.";
        if (options.Version is not (7 or 8)) return "The version must be 7 or 8.";
        if (!float.IsFinite(options.Weight) || options.Weight < 0f || options.Weight > 10f) return "The bone weight must be between 0 and 10.";
        if (options.RampIn < 0 || options.RampOut < 0) return "Ramps cannot be negative.";
        if ((long)options.RampIn + options.RampOut > options.Length)
            return "The ramps together are longer than the clip: shorten them or lengthen the clip.";
        if (options.PoseClip is { } clip)
        {
            if (clip.BoneCount != skeleton.Count)
                return $"The reference clip has {clip.BoneCount} bones but the mesh has {skeleton.Count}; clips address bones by index, so pick a clip made for this mesh.";
            if (!float.IsFinite(options.PoseTime)) return "The reference time is not a number.";
        }
        return null;
    }

    /// <summary>
    /// <paramref name="key"/> with its largest component stepped one unit toward zero until the stored
    /// quaternion is no longer than 1. <see cref="RfaRotKey.WithinUnit"/> only chooses between floor and
    /// ceiling, so a component that is already a whole number (w exactly 16383 next to a tiny x, as when
    /// a stock key stored slightly long is sampled and normalised in float) can leave it no shorter
    /// choice; this finishes the job. The turn it adds is far below 0.01 degrees.
    /// </summary>
    private static RfaRotKey ShortenToUnit(RfaRotKey key)
    {
        const long limit = 16383L * 16383L;
        Span<int> c = [key.X, key.Y, key.Z, key.W];
        while ((long)c[0] * c[0] + (long)c[1] * c[1] + (long)c[2] * c[2] + (long)c[3] * c[3] > limit)
        {
            int largest = 0;
            for (int i = 1; i < 4; i++)
            {
                if (Math.Abs(c[i]) > Math.Abs(c[largest])) largest = i;
            }
            c[largest] -= Math.Sign(c[largest]);
        }
        return key with { X = (short)c[0], Y = (short)c[1], Z = (short)c[2], W = (short)c[3] };
    }

    private static void Validate(Skeleton skeleton, NewClipOptions options)
    {
        if (Problem(skeleton, options) is { } problem) throw new ArgumentException(problem, nameof(options));
    }

    /// <summary>
    /// The clip a new clip for <paramref name="meshName"/> should start from: the "stand" state the tables
    /// give a class using the mesh (outside weapon blocks), else the stand clip of the built-in rig profile
    /// matching <paramref name="skeleton"/>; only a clip <paramref name="isAvailable"/> accepts (readable,
    /// with the skeleton's bone count). Null when neither exists: start from the bind pose. A clip is not
    /// guessed from its name, because a clip with the same bone count may belong to another character.
    /// </summary>
    /// <param name="meshName">The mesh's file name ("ult2_guard.v3c").</param>
    /// <param name="skeleton">The mesh's skeleton.</param>
    /// <param name="usage">The table usage index, if loaded.</param>
    /// <param name="isAvailable">Whether a clip file name can be used (found, readable, right bone count).</param>
    public static NewClipPoseSuggestion? SuggestPoseClip(string meshName, Skeleton skeleton, ClipUsageIndex? usage, Func<string, bool> isAvailable)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(isAvailable);
        if (skeleton.Count == 0) return null;
        if (usage is not null && !string.IsNullOrWhiteSpace(meshName))
        {
            foreach (var list in usage.ClipListsForMesh(meshName))
            {
                foreach (var u in list.Clips)
                {
                    if (u.Kind != ClipUsageKind.State || u.WeaponBlock is not null) continue;
                    if (!string.Equals(u.SlotName, "stand", StringComparison.OrdinalIgnoreCase)) continue;
                    if (isAvailable(u.DiskName)) return new NewClipPoseSuggestion(u.DiskName, NewClipPoseReason.TableStand, list.ClassName);
                }
            }
        }
        if (RigProfiles.FindFor(skeleton) is { ReferenceClip: { Length: > 0 } stand } profile && isAvailable(stand))
            return new NewClipPoseSuggestion(stand, NewClipPoseReason.RigProfile, profile.Name);
        return null;
    }
}
