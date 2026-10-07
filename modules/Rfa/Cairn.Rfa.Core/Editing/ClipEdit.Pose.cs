using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>The frame an offset's rotation axes and translation are expressed in.</summary>
public enum OffsetSpace
{
    /// <summary>The bone's own axes: <c>L' = L * R</c>; a translation moves along the bone's own axes.</summary>
    Local,
    /// <summary>The parent's axes: <c>L' = R * L</c>; a translation moves along the parent's axes.</summary>
    Parent,
    /// <summary>
    /// Model space: <c>L' = Pw^-1 * R * Pw * L</c> with <c>Pw</c> the parent's world rotation at the
    /// key's time (needs <see cref="BoneOffset.Skeleton"/>); a translation moves along model axes.
    /// </summary>
    Model,
}

/// <summary>
/// A rotation and/or translation added to every key of a bone (the "layer" edit of DESIGN.md
/// section 6), optionally limited to a time range with a smooth falloff.
/// </summary>
/// <param name="Rotation">Active rotation to add; the identity adds none.</param>
/// <param name="Translation">Translation to add to the position keys (metres); zero adds none.</param>
/// <param name="Space">The frame <paramref name="Rotation"/> and <paramref name="Translation"/> are in.</param>
public sealed record BoneOffset(Quaternion Rotation, Vector3 Translation = default, OffsetSpace Space = OffsetSpace.Local)
{
    /// <summary>Start of the full-weight range in ticks; null = from the beginning (no ramp in).</summary>
    public int? From { get; init; }

    /// <summary>End of the full-weight range in ticks; null = to the end (no ramp out).</summary>
    public int? To { get; init; }

    /// <summary>
    /// Ticks over which the weight ramps (smoothstep) from 0 at <c>From - FalloffTicks</c> to 1 at
    /// <c>From</c>, and from 1 at <c>To</c> back to 0 at <c>To + FalloffTicks</c>. Ignored on an open end.
    /// </summary>
    public int FalloffTicks { get; init; }

    /// <summary>The mesh's skeleton (bone parents); required for <see cref="OffsetSpace.Model"/>.</summary>
    public Skeleton? Skeleton { get; init; }

    /// <summary>A rotation-only offset.</summary>
    public static BoneOffset Rotate(Quaternion rotation, OffsetSpace space = OffsetSpace.Local) => new(rotation, Vector3.Zero, space);

    /// <summary>A translation-only offset.</summary>
    public static BoneOffset Move(Vector3 translation, OffsetSpace space = OffsetSpace.Parent) => new(Quaternion.Identity, translation, space);

    /// <summary>
    /// The offset's weight at <paramref name="time"/>: 1 inside [From, To], smoothstep ramps across the
    /// falloff on each closed end, 0 beyond.
    /// </summary>
    public float WeightAt(float time)
    {
        float w = 1f;
        int fo = Math.Max(0, FalloffTicks);
        if (From is int f)
        {
            if (time < f) w = fo > 0 ? PoseEditMath.SmoothStep((time - (f - fo)) / fo) : 0f;
        }
        if (To is int t)
        {
            if (time > t) w = Math.Min(w, fo > 0 ? PoseEditMath.SmoothStep(((t + fo) - time) / fo) : 0f);
        }
        return w;
    }
}

public static partial class ClipEdit
{
    /// <summary>
    /// Adds <paramref name="offset"/> to one bone: every rotation key in the affected range is rotated
    /// (by the offset's rotation scaled by the weight at that key, <c>slerp(identity, R, w)</c>) and
    /// every position key is moved (translation times weight; control points move with the key).
    /// <para>
    /// With a range, keys are first inserted (by sampling, so the curve is unchanged) at
    /// <c>From - FalloffTicks</c>, <c>From</c>, <c>To</c> and <c>To + FalloffTicks</c> where those
    /// times lie inside [start, end] and hold no key: the outer pair anchors the untouched part
    /// exactly, the inner pair carries the full weight. Between two keys the offset follows the
    /// track's own interpolation, so the smoothstep falloff is exact at keys and approximated in
    /// between (add keys inside the falloff for a finer ramp). Inserted position keys split the Bezier
    /// exactly, which moves the neighbours' facing control points. Only the kinds the offset touches
    /// get keys inserted (rotation keys for a rotation, position keys for a translation).
    /// </para>
    /// <para>
    /// Model space samples the parent chain of the INPUT clip at each key's time (exact at keys).
    /// Local translation is rotated by the bone's own sampled rotation at the key's time.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Model space without a matching skeleton, or a bad range.</exception>
    public static RfaClip OffsetBone(RfaClip clip, int bone, BoneOffset offset)
    {
        ArgumentNullException.ThrowIfNull(offset);
        CheckBone(clip, bone);
        CheckOffset(clip, offset);
        return OffsetBoneCore(clip, clip, bone, offset);
    }

    /// <summary>
    /// <see cref="OffsetBone"/> on each of <paramref name="bones"/>. Every bone gets the offset as if it
    /// were alone, with Model-space parent chains sampled from the input clip, so the order does not
    /// matter; a child of another offset bone also inherits that parent's offset, as in any rig.
    /// </summary>
    public static RfaClip OffsetBones(RfaClip clip, IEnumerable<int> bones, BoneOffset offset)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bones);
        ArgumentNullException.ThrowIfNull(offset);
        CheckOffset(clip, offset);
        var result = clip;
        foreach (int b in PoseEditMath.BoneList(clip, bones)) result = OffsetBoneCore(clip, result, b, offset);
        return result;
    }

    private static void CheckOffset(RfaClip clip, BoneOffset offset)
    {
        if (offset.FalloffTicks < 0)
            throw new ArgumentException("The falloff cannot be negative; use 0 for a hard edge.", nameof(offset));
        if (offset.From is int f && offset.To is int t && f > t)
            throw new ArgumentException($"The offset range starts at {f} after it ends at {t}; swap From and To.", nameof(offset));
        if (offset.Space == OffsetSpace.Model)
        {
            if (offset.Skeleton is null)
                throw new ArgumentException("A model-space offset needs the bone parents: set BoneOffset.Skeleton to the clip's mesh skeleton.", nameof(offset));
            PoseEditMath.CheckSkeleton(clip, offset.Skeleton, nameof(offset));
        }
    }

    private static RfaClip OffsetBoneCore(RfaClip reference, RfaClip clip, int bone, BoneOffset offset)
    {
        var track = clip.Bones[bone];
        bool rotate = Quat.AngleDegrees(Quaternion.Identity, offset.Rotation) > 0f;
        bool move = offset.Translation != Vector3.Zero;
        if (!rotate && !move) return clip;

        var boundaries = new List<int>();
        int fo = offset.FalloffTicks;
        if (offset.From is int from)
        {
            if (fo > 0) boundaries.Add(from - fo);
            boundaries.Add(from);
        }
        if (offset.To is int to)
        {
            boundaries.Add(to);
            if (fo > 0) boundaries.Add(to + fo);
        }
        boundaries.RemoveAll(t => t < clip.StartTime || t > clip.EndTime);

        var refTrack = reference.Bones[bone];
        var rotKeys = track.RotationKeys;
        var posKeys = track.PositionKeys;

        if (rotate)
        {
            var work = new RotTrackWork(track.RotationKeys);
            if (work.Count == 0) work.Insert(clip.StartTime);
            foreach (int t in boundaries) work.Insert(t);
            var r = Quat.Normalize(offset.Rotation);
            for (int i = 0; i < work.Count; i++)
            {
                int time = work.Keys[i].Time;
                float w = offset.WeightAt(time);
                if (w <= 0f) continue;
                var local = KeyRotation(work.Keys[i]);
                var rw = PoseEditMath.Partial(r, w);
                Quaternion result = offset.Space switch
                {
                    OffsetSpace.Local => Quat.Mul(local, rw),
                    OffsetSpace.Parent => Quat.Mul(rw, local),
                    _ => ModelSpaceRotate(reference, offset.Skeleton!, bone, time, rw, local),
                };
                work.SetRotation(i, result);
            }
            rotKeys = work.ToImmutable();
        }

        if (move)
        {
            var work = new PosTrackWork(track.PositionKeys);
            if (work.Count == 0)
            {
                work.Insert(clip.StartTime);
                if (clip.EndTime > clip.StartTime) work.Insert(clip.EndTime);
            }
            foreach (int t in boundaries) work.Insert(t);
            for (int i = 0; i < work.Count; i++)
            {
                int time = work.Keys[i].Time;
                float w = offset.WeightAt(time);
                if (w <= 0f) continue;
                var tw = offset.Translation * w;
                var delta = offset.Space switch
                {
                    OffsetSpace.Local => Quat.Rotate(ClipSampler.SampleRotation(refTrack.RotationKeys.AsSpan(), time), tw),
                    OffsetSpace.Parent => tw,
                    _ => Quat.Rotate(Quat.Conj(PoseEditMath.ParentWorldRotation(reference, offset.Skeleton!, bone, time)), tw),
                };
                work.Translate(i, delta);
            }
            posKeys = work.ToImmutable();
        }

        return WithTrack(clip, bone, track with { RotationKeys = rotKeys, PositionKeys = posKeys });
    }

    private static Quaternion ModelSpaceRotate(RfaClip reference, Skeleton skeleton, int bone, int time, Quaternion rw, Quaternion local)
    {
        var pw = PoseEditMath.ParentWorldRotation(reference, skeleton, bone, time);
        return Quat.Mul(Quat.Conj(pw), Quat.Mul(rw, Quat.Mul(pw, local)));
    }
}
