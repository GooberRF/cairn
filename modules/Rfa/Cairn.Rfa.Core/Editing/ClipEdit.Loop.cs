using System.Numerics;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>Which end of the clip <see cref="ClipEdit.MakeLoopable"/> changes.</summary>
public enum LoopMode
{
    /// <summary>The last <c>blendTicks</c> cross-fade into the start pose; the final key equals the first.</summary>
    BlendEndToStart,
    /// <summary>The first <c>blendTicks</c> cross-fade out of the end pose; the first key equals the last.</summary>
    BlendStartToEnd,
}

/// <summary>Root-motion options for <see cref="ClipEdit.MakeLoopable"/>.</summary>
/// <param name="RootBone">The root bone (see <see cref="ClipEdit.FindRootBone(Animation.Skeleton)"/>); -1 = none.</param>
/// <param name="KeepRootTranslation">
/// Translation axes of the root whose motion is kept: those components of the root's position keys
/// are left exactly as they are (a walk keeps travelling), only its other components and every
/// rotation are blended. <see cref="RootMotionAxes.Yaw"/> is ignored here.
/// </param>
public sealed record LoopOptions(int RootBone = -1, RootMotionAxes KeepRootTranslation = RootMotionAxes.None);

public static partial class ClipEdit
{
    /// <summary>
    /// Makes the clip loop seamlessly by cross-fading one end into the other's pose, for every bone,
    /// rotations and positions.
    /// <para>
    /// <see cref="LoopMode.BlendEndToStart"/>: the target is each track's FIRST key. Keys in the window
    /// <c>(end - blendTicks, end]</c> are blended towards it with weight
    /// <c>smoothstep((t - (end - blendTicks)) / blendTicks)</c> (rotations by slerp, positions and their
    /// control points by lerp, so the tangents fade out). A key is inserted by sampling at
    /// <c>end - blendTicks</c> (the untouched anchor) and at <c>end</c> when missing. The key at
    /// <c>end</c> then equals the first key exactly: a rotation key copies the first key's raw int16
    /// components (exactly negated when needed for sign continuity) and a position key copies the
    /// first key's point and control points.
    /// </para>
    /// <para>
    /// <see cref="LoopMode.BlendStartToEnd"/> mirrors this in time: the target is each track's LAST key,
    /// the window is <c>[start, start + blendTicks)</c> and the key at <c>start</c> equals the last key.
    /// </para>
    /// Keys outside the window are untouched; a <paramref name="blendTicks"/> of 0 only sets the end key.
    /// </summary>
    /// <param name="clip">The clip.</param>
    /// <param name="blendTicks">Length of the cross-fade window, 0..duration.</param>
    /// <param name="mode">Which end changes.</param>
    /// <param name="options">Root-motion options; null = blend everything.</param>
    public static RfaClip MakeLoopable(RfaClip clip, int blendTicks, LoopMode mode = LoopMode.BlendEndToStart, LoopOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Duration <= 0)
            throw new ArgumentException("The clip has no length (end equals start), so there is nothing to loop.", nameof(clip));
        if (blendTicks < 0 || blendTicks > clip.Duration)
            throw new ArgumentOutOfRangeException(nameof(blendTicks), $"The blend window must be between 0 and the clip's length ({clip.Duration} ticks).");
        options ??= new LoopOptions();
        if (options.RootBone >= 0) CheckBone(clip, options.RootBone);

        bool endToStart = mode == LoopMode.BlendEndToStart;
        int seam = endToStart ? clip.EndTime : clip.StartTime;
        int anchor = endToStart ? clip.EndTime - blendTicks : clip.StartTime + blendTicks;

        float Weight(int time)
        {
            if (time == seam) return 1f;
            if (blendTicks == 0) return 0f;
            float x = endToStart ? (time - anchor) / (float)blendTicks : (anchor - time) / (float)blendTicks;
            return x <= 0f ? 0f : PoseEditMath.SmoothStep(x);
        }

        var bones = clip.Bones.ToBuilder();
        for (int b = 0; b < bones.Count; b++)
        {
            var track = bones[b];

            var rot = new RotTrackWork(track.RotationKeys);
            if (rot.Count > 0 && RotationNeedsLoop(rot, endToStart, seam, Weight))
            {
                var target = endToStart ? rot.Keys[0] : rot.Keys[^1];
                var targetActive = KeyRotation(target);
                if (blendTicks > 0) rot.Insert(anchor);
                int seamIndex = rot.Insert(seam);
                for (int i = 0; i < rot.Count; i++)
                {
                    if (i == seamIndex) continue;
                    float w = Weight(rot.Keys[i].Time);
                    if (w <= 0f) continue;
                    rot.SetRotation(i, Quat.Slerp(KeyRotation(rot.Keys[i]), targetActive, w));
                }
                var old = rot.Keys[seamIndex];
                var copy = new RfaRotKey(seam, target.X, target.Y, target.Z, target.W, old.EaseIn, old.EaseOut, target.Pad);
                int neighbour = endToStart ? seamIndex - 1 : seamIndex + 1;
                if (neighbour >= 0 && neighbour < rot.Count) copy = AlignSign(copy, rot.Keys[neighbour]);
                rot.Set(seamIndex, copy);
            }

            var pos = new PosTrackWork(track.PositionKeys);
            var keep = b == options.RootBone ? options.KeepRootTranslation : RootMotionAxes.None;
            if (pos.Count > 0 && PositionNeedsLoop(pos, endToStart, seam, keep, Weight))
            {
                var target = endToStart ? pos.Keys[0] : pos.Keys[^1];
                if (blendTicks > 0) pos.Insert(anchor);
                int seamIndex = pos.Insert(seam);
                for (int i = 0; i < pos.Count; i++)
                {
                    var k = pos.Keys[i];
                    if (i == seamIndex)
                    {
                        pos.Keys[i] = new RfaPosKey(seam, Keep(target.Position, k.Position, keep),
                            Keep(target.InControl, k.InControl, keep), Keep(target.OutControl, k.OutControl, keep));
                        continue;
                    }
                    float w = Weight(k.Time);
                    if (w <= 0f) continue;
                    pos.Keys[i] = new RfaPosKey(k.Time,
                        Keep(Vector3.Lerp(k.Position, target.Position, w), k.Position, keep),
                        Keep(Vector3.Lerp(k.InControl, target.Position, w), k.InControl, keep),
                        Keep(Vector3.Lerp(k.OutControl, target.Position, w), k.OutControl, keep));
                }
            }

            bones[b] = track with { RotationKeys = rot.ToImmutable(), PositionKeys = pos.ToImmutable() };
        }
        return clip with { Bones = bones.MoveToImmutable() };

        // Components on kept axes come from the original key.
        static Vector3 Keep(Vector3 blended, Vector3 original, RootMotionAxes keep) => new(
            (keep & RootMotionAxes.X) != 0 ? original.X : blended.X,
            (keep & RootMotionAxes.Y) != 0 ? original.Y : blended.Y,
            (keep & RootMotionAxes.Z) != 0 ? original.Z : blended.Z);
    }

    // A track that already holds its target through the window (a constant track, or one whose end
    // already equals its start) is left alone, so looping adds no keys to it.
    private static bool RotationNeedsLoop(RotTrackWork rot, bool endToStart, int seam, Func<int, float> weight)
    {
        var target = endToStart ? rot.Keys[0] : rot.Keys[^1];
        foreach (var k in rot.Keys)
        {
            if (weight(k.Time) > 0f && !SameRotation(k, target)) return true;
        }
        return rot.IndexAt(seam) < 0 && Quat.AngleDegrees(rot.Sample(seam), KeyRotation(target)) > 1e-3f;

        static bool SameRotation(RfaRotKey a, RfaRotKey b) =>
            (a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W)
            || (a.X == -b.X && a.Y == -b.Y && a.Z == -b.Z && a.W == -b.W);
    }

    private static bool PositionNeedsLoop(PosTrackWork pos, bool endToStart, int seam, RootMotionAxes keep, Func<int, float> weight)
    {
        var target = endToStart ? pos.Keys[0] : pos.Keys[^1];
        foreach (var k in pos.Keys)
        {
            if (weight(k.Time) <= 0f) continue;
            if (Differs(k.Position, target.Position) || Differs(k.InControl, target.Position) || Differs(k.OutControl, target.Position)) return true;
        }
        return pos.IndexAt(seam) < 0 && Differs(pos.Sample(seam), target.Position);

        bool Differs(Vector3 a, Vector3 b) =>
            ((keep & RootMotionAxes.X) == 0 && a.X != b.X)
            || ((keep & RootMotionAxes.Y) == 0 && a.Y != b.Y)
            || ((keep & RootMotionAxes.Z) == 0 && a.Z != b.Z);
    }
}
