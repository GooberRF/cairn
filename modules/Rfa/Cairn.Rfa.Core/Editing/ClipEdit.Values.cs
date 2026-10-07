using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>How <see cref="ClipEdit.AutoControlPoints"/> places Bezier control points.</summary>
public enum ControlPointMode
{
    /// <summary>
    /// A third of the way to each neighbouring key, so every segment whose two keys are both set is a
    /// straight line travelled at uniform speed. The first key's in control and the last key's out
    /// control are the key itself.
    /// </summary>
    Linear,

    /// <summary>
    /// Catmull-Rom style: a velocity per key from its neighbours, scaled by each segment's duration,
    /// so the curve is C1-continuous in time across the key. See <see cref="ClipEdit.AutoControlPoints"/>.
    /// </summary>
    Smooth,
}

public static partial class ClipEdit
{
    /// <summary>
    /// Sets every selected rotation key to the ACTIVE-convention rotation <paramref name="active"/>
    /// (re-quantised once, sign-continuous with the key before it — or after it for a first key — pad
    /// 0). Time and eases are kept; position keys in the selection are ignored.
    /// </summary>
    public static RfaClip SetRotation(RfaClip clip, KeySelection selection, Quaternion active)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        return ValueEditing.EditRotations(clip, selection, (list, i) =>
            ClipEditTracks.QuantizeAt(list, i, list[i].Time, active, list[i].EaseIn, list[i].EaseOut));
    }

    /// <summary>
    /// <see cref="SetRotation"/> from Euler angles in degrees, (pitch, yaw, roll) about X, Y, Z in the
    /// convention of <see cref="Quat.FromEulerDegrees"/>.
    /// </summary>
    public static RfaClip SetRotationEuler(RfaClip clip, KeySelection selection, Vector3 eulerDegrees) =>
        SetRotation(clip, selection, Quat.FromEulerDegrees(eulerDegrees));

    /// <summary>
    /// Sets the ease bytes of the selected rotation keys (-128..127 over 127; null keeps a value).
    /// Ease-in shapes the segment ending at the key, ease-out the one starting at it. The quaternion
    /// components are not touched.
    /// </summary>
    public static RfaClip SetEases(RfaClip clip, KeySelection selection, sbyte? easeIn, sbyte? easeOut)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        return ValueEditing.EditRotations(clip, selection, (list, i) =>
            list[i] with { EaseIn = easeIn ?? list[i].EaseIn, EaseOut = easeOut ?? list[i].EaseOut });
    }

    /// <summary>
    /// Sets the selected position keys to <paramref name="position"/> (metres, parent frame). With
    /// <paramref name="moveControlPoints"/> the key's control points move by the same delta (the
    /// curve's shape around the key is kept); otherwise they stay where they are. Rotation keys in the
    /// selection are ignored.
    /// </summary>
    public static RfaClip SetPosition(RfaClip clip, KeySelection selection, Vector3 position, bool moveControlPoints = true)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        return ValueEditing.EditPositions(clip, selection, (list, i) =>
        {
            var k = list[i];
            var d = position - k.Position;
            return moveControlPoints
                ? k with { Position = position, InControl = k.InControl + d, OutControl = k.OutControl + d }
                : k with { Position = position };
        });
    }

    /// <summary>Sets the in and/or out control point (absolute, metres; null keeps it) of the selected position keys.</summary>
    public static RfaClip SetControlPoints(RfaClip clip, KeySelection selection, Vector3? inControl, Vector3? outControl)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        return ValueEditing.EditPositions(clip, selection, (list, i) =>
            list[i] with { InControl = inControl ?? list[i].InControl, OutControl = outControl ?? list[i].OutControl });
    }

    /// <summary>
    /// Recomputes both control points of the selected position keys from the key positions (which
    /// are not changed). Neighbours are read from the track as it was before the edit.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ControlPointMode.Linear"/>: <c>in = P[i] + (P[i-1] - P[i]) / 3</c>,
    /// <c>out = P[i] + (P[i+1] - P[i]) / 3</c>; the first key's in and the last key's out are P[i].</para>
    /// <para><see cref="ControlPointMode.Smooth"/>: velocity (metres per tick)
    /// <c>v[i] = (P[i+1] - P[i-1]) / (t[i+1] - t[i-1])</c> for an inner key, one-sided
    /// <c>(P[1] - P[0]) / (t[1] - t[0])</c> and <c>(P[n-1] - P[n-2]) / (t[n-1] - t[n-2])</c> at the
    /// ends; then <c>out = P[i] + v[i] (t[i+1] - t[i]) / 3</c> and <c>in = P[i] - v[i] (t[i] - t[i-1]) / 3</c>.
    /// A Bezier leaves its first point with velocity <c>3 (out - P) / duration</c>, so both segments
    /// meet with velocity v[i]: C1 in time. The outer control of an end key is the key itself, a single
    /// key gets in = out = P, and coinciding times give a zero velocity.</para>
    /// </remarks>
    public static RfaClip AutoControlPoints(RfaClip clip, KeySelection selection, ControlPointMode mode)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        return ValueEditing.EditPositions(clip, selection, (list, i) =>
        {
            var k = list[i];
            if (mode == ControlPointMode.Linear)
            {
                var (cin, cout) = ClipEditTracks.LinearControls(list, i);
                return k with { InControl = cin, OutControl = cout };
            }
            int n = list.Count;
            if (n < 2) return k with { InControl = k.Position, OutControl = k.Position };
            int a = Math.Max(0, i - 1), b = Math.Min(n - 1, i + 1);
            int dt = list[b].Time - list[a].Time;
            var v = dt > 0 ? (list[b].Position - list[a].Position) / dt : Vector3.Zero;
            var inC = i > 0 ? k.Position - v * ((list[i].Time - list[i - 1].Time) / 3f) : k.Position;
            var outC = i < n - 1 ? k.Position + v * ((list[i + 1].Time - list[i].Time) / 3f) : k.Position;
            return k with { InControl = inC, OutControl = outC };
        });
    }

    /// <summary>
    /// Keys the current pose: on each listed bone, inserts a rotation and/or position key at
    /// <paramref name="time"/> equal to the sampled value, so the motion is unchanged. A bone that
    /// already has a key at that time keeps it.
    /// </summary>
    /// <remarks>
    /// Positions: inside a segment the Bezier is split exactly (de Casteljau), changing the earlier
    /// key's out control and the later key's in control so both halves reproduce the original curve;
    /// before the first or after the last key a constant key is added and the old end key's outer
    /// control is set to its position. Rotations: the new key is the sampled rotation (re-quantised);
    /// on a segment without ease nothing else changes and the motion is reproduced up to the int16
    /// store. If the segment is eased, the ease-out of the earlier key, both eases of the new key and
    /// the ease-in of the later key are refit so each half keeps the original eased timing: exact up to
    /// the ease byte step when the time falls in the segment's constant-speed part. Inside an ease's
    /// acceleration or deceleration part one half is a piece of a parabola that no pair of engine ease
    /// bytes can represent, so that half gets the best fit (max-norm search over the bytes): the timing
    /// error is about 1.2% of the segment's rotation for eases around 30/40 (0.27 degrees on a
    /// 23-degree segment) and at most 4.3% (full ease, split early in the acceleration). Adding helper
    /// keys would make it exact but would leave stale keys next to the one the user is about to pose,
    /// so none are added. An unexpectedly keyless track gets a key holding the value it already
    /// samples to (identity / origin).
    /// </remarks>
    public static RfaClip KeyPoseAtTime(RfaClip clip, IEnumerable<int> bones, int time, bool rotations = true, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bones);
        var b = clip.Bones.ToBuilder();
        foreach (int bone in bones.Distinct())
        {
            CheckBone(clip, bone);
            var track = b[bone];
            if (rotations) track = track with { RotationKeys = ClipEditTracks.InsertSampledRotation(track.RotationKeys, time, out _) };
            if (positions) track = track with { PositionKeys = ClipEditTracks.InsertSampledPosition(track.PositionKeys, time, out _) };
            b[bone] = track;
        }
        return clip with { Bones = b.MoveToImmutable() };
    }
}

file static class ValueEditing
{
    // Applies an edit to each selected rotation key in track order; the edit sees the keys already
    // edited (so sign continuity follows the new predecessor).
    public static RfaClip EditRotations(RfaClip clip, KeySelection selection, Func<List<RfaRotKey>, int, RfaRotKey> edit)
    {
        var bones = clip.Bones.ToBuilder();
        foreach (int bone in selection.SelectedBones)
        {
            var indices = selection.IndicesOf(bone, KeyKind.Rotation).ToList();
            if (indices.Count == 0) continue;
            var list = bones[bone].RotationKeys.ToList();
            foreach (int i in indices) list[i] = edit(list, i);
            bones[bone] = bones[bone] with { RotationKeys = [.. list] };
        }
        return clip with { Bones = bones.MoveToImmutable() };
    }

    // Applies an edit to each selected position key; the edit reads the track as it was before.
    public static RfaClip EditPositions(RfaClip clip, KeySelection selection, Func<IReadOnlyList<RfaPosKey>, int, RfaPosKey> edit)
    {
        var bones = clip.Bones.ToBuilder();
        foreach (int bone in selection.SelectedBones)
        {
            var indices = selection.IndicesOf(bone, KeyKind.Position).ToList();
            if (indices.Count == 0) continue;
            var original = bones[bone].PositionKeys;
            var b = original.ToBuilder();
            foreach (int i in indices) b[i] = edit(original, i);
            bones[bone] = bones[bone] with { PositionKeys = b.MoveToImmutable() };
        }
        return clip with { Bones = bones.MoveToImmutable() };
    }
}
