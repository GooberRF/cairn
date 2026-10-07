using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

/// <summary>Which repairs <see cref="ClipEdit.Normalize(RfaClip, NormalizeOptions?)"/> makes; all on by default.</summary>
public sealed record NormalizeOptions
{
    /// <summary>Re-quantise rotation keys whose stored length (over 16383) is off 1 by more than 0.002 — only those.</summary>
    public bool UnitQuaternions { get; init; } = true;

    /// <summary>
    /// Negate the stored components (exact integer negation, no re-quantising) of a rotation key in
    /// the other hemisphere from the key before it. The engine's slerp takes the short arc either way,
    /// so the motion is the same, except where an interpolated w is exactly 0: the engine replaces it
    /// with +1, which is not sign-symmetric, so such samples move by two int16 steps (0.014 degrees).
    /// It makes tracks easy to read and edit. 355 of the 1009 stock clips have such keys.
    /// </summary>
    public bool SignContinuity { get; init; } = true;

    /// <summary>Sort each track by time (stable) and drop duplicate times, keeping the last key stored at that time.</summary>
    public bool IncreasingTimes { get; init; } = true;

    /// <summary>
    /// Drop keys outside [start, end], inserting a sampled boundary key where keys lay beyond it so
    /// the motion inside the range is unchanged (as <see cref="ClipEdit.Trim"/> does per track). Skipped
    /// when end is before start.
    /// </summary>
    public bool ClampToRange { get; init; } = true;

    /// <summary>Set every rotation key's pad word to 0 (0 in every stock file).</summary>
    public bool ZeroPad { get; init; } = true;

    /// <summary>
    /// A position key's control point that is exactly (0, 0, 0) while the key's position is not is
    /// replaced by its linear auto control point (a third of the way to the neighbour; the key itself at
    /// the track ends) — a sign of an exporter that did not write control points.
    /// </summary>
    public bool FixControlPoints { get; init; } = true;

    /// <summary>Give every bone at least 1 rotation key and 2 position keys (<see cref="ClipEdit.EnsureMinimumKeys"/> without a skeleton).</summary>
    public bool MinimumKeys { get; init; } = true;
}

/// <summary>What <see cref="ClipEdit.Normalize(RfaClip, NormalizeOptions?, out NormalizeReport)"/> changed, counted per repair.</summary>
/// <param name="ReorderedTracks">Tracks re-sorted by time.</param>
/// <param name="DuplicateKeysDropped">Keys dropped because a later key had the same time.</param>
/// <param name="KeysOutsideRangeDropped">Keys dropped outside [start, end].</param>
/// <param name="BoundaryKeysInserted">Keys inserted at start or end by the range clamp.</param>
/// <param name="QuaternionsRequantized">Rotation keys re-quantised to unit length.</param>
/// <param name="SignsFlipped">Rotation keys negated for sign continuity.</param>
/// <param name="PadsZeroed">Rotation keys whose pad word was cleared.</param>
/// <param name="ControlPointsFixed">Control points replaced by linear auto control points.</param>
/// <param name="MinimumKeysAdded">Keys added so every bone has 1 rotation and 2 position keys.</param>
public sealed record NormalizeReport(
    int ReorderedTracks, int DuplicateKeysDropped, int KeysOutsideRangeDropped, int BoundaryKeysInserted,
    int QuaternionsRequantized, int SignsFlipped, int PadsZeroed, int ControlPointsFixed, int MinimumKeysAdded)
{
    /// <summary>True when nothing was changed.</summary>
    public bool IsEmpty => ReorderedTracks + DuplicateKeysDropped + KeysOutsideRangeDropped + BoundaryKeysInserted
        + QuaternionsRequantized + SignsFlipped + PadsZeroed + ControlPointsFixed + MinimumKeysAdded == 0;

    /// <summary>A one-line plain-language summary ("nothing to fix" when empty).</summary>
    public override string ToString()
    {
        if (IsEmpty) return "nothing to fix";
        var parts = new List<string>();
        void Add(int n, string what)
        {
            if (n > 0) parts.Add($"{n} {what}");
        }
        Add(ReorderedTracks, "tracks re-sorted");
        Add(DuplicateKeysDropped, "duplicate keys dropped");
        Add(KeysOutsideRangeDropped, "keys outside the range dropped");
        Add(BoundaryKeysInserted, "boundary keys inserted");
        Add(QuaternionsRequantized, "rotations re-normalised");
        Add(SignsFlipped, "rotation signs flipped");
        Add(PadsZeroed, "pad words cleared");
        Add(ControlPointsFixed, "control points fixed");
        Add(MinimumKeysAdded, "missing keys added");
        return string.Join(", ", parts);
    }
}

public static partial class ClipEdit
{
    /// <summary>
    /// Repairs structural problems (see <see cref="NormalizeOptions"/>) and leaves everything else
    /// bit-identical: a clip without problems comes back unchanged (the same instance). Repairs run in
    /// this order: increasing times, clamp to range, unit quaternions, sign continuity, zero pad,
    /// control points, minimum keys.
    /// </summary>
    public static RfaClip Normalize(RfaClip clip, NormalizeOptions? options = null) => Normalize(clip, options, out _);

    /// <summary>As <see cref="Normalize(RfaClip, NormalizeOptions?)"/>, also counting what changed.</summary>
    public static RfaClip Normalize(RfaClip clip, NormalizeOptions? options, out NormalizeReport report)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var o = options ?? new NormalizeOptions();
        int reordered = 0, duplicates = 0, outside = 0, inserted = 0, requantized = 0, signs = 0, pads = 0, controls = 0;
        bool clamp = o.ClampToRange && clip.EndTime >= clip.StartTime;
        var bones = clip.Bones.ToBuilder();
        bool any = false;
        for (int b = 0; b < bones.Count; b++)
        {
            var track = bones[b];
            var rot = track.RotationKeys;
            var pos = track.PositionKeys;

            if (o.IncreasingTimes)
            {
                rot = NormalizeEditing.Sort(rot, k => k.Time, ref reordered, ref duplicates);
                pos = NormalizeEditing.Sort(pos, k => k.Time, ref reordered, ref duplicates);
            }
            if (clamp)
            {
                rot = NormalizeEditing.Clamp(rot, k => k.Time, clip.StartTime, clip.EndTime, ClipEditTracks.CropRotation, ref outside, ref inserted);
                pos = NormalizeEditing.Clamp(pos, k => k.Time, clip.StartTime, clip.EndTime, ClipEditTracks.CropPosition, ref outside, ref inserted);
            }
            if (o.UnitQuaternions || o.SignContinuity || o.ZeroPad)
            {
                var r = rot.ToBuilder();
                for (int i = 0; i < r.Count; i++)
                {
                    var k = r[i];
                    if (o.UnitQuaternions && MathF.Abs(k.FileQuaternion.Length() - 1f) > 0.002f)
                    {
                        RfaRotKey? reference = i > 0 ? r[i - 1] : r.Count > 1 ? r[1] : null;
                        k = QuantizeRotation(k.Time, KeyRotation(k), reference, k.EaseIn, k.EaseOut) with { Pad = k.Pad };
                        requantized++;
                    }
                    if (o.SignContinuity && i > 0 && ClipEditTracks.StoredDot(k, r[i - 1]) < 0)
                    {
                        k = ClipEditTracks.Negated(k);
                        signs++;
                    }
                    if (o.ZeroPad && k.Pad != 0)
                    {
                        k = k with { Pad = 0 };
                        pads++;
                    }
                    r[i] = k;
                }
                if (!r.SequenceEqual(rot)) rot = r.MoveToImmutable();
            }
            if (o.FixControlPoints)
            {
                var p = pos.ToBuilder();
                for (int i = 0; i < p.Count; i++)
                {
                    var k = p[i];
                    if (k.Position == Vector3.Zero || (k.InControl != Vector3.Zero && k.OutControl != Vector3.Zero)) continue;
                    var (cin, cout) = ClipEditTracks.LinearControls(pos, i);
                    if (k.InControl == Vector3.Zero)
                    {
                        k = k with { InControl = cin };
                        controls++;
                    }
                    if (k.OutControl == Vector3.Zero)
                    {
                        k = k with { OutControl = cout };
                        controls++;
                    }
                    p[i] = k;
                }
                if (!p.SequenceEqual(pos)) pos = p.MoveToImmutable();
            }
            if (rot != track.RotationKeys || pos != track.PositionKeys)
            {
                bones[b] = track with { RotationKeys = rot, PositionKeys = pos };
                any = true;
            }
        }
        var result = any ? clip with { Bones = bones.MoveToImmutable() } : clip;
        int added = 0;
        if (o.MinimumKeys)
        {
            int before = result.Bones.Sum(t => t.RotationKeys.Length + t.PositionKeys.Length);
            result = EnsureMinimumKeys(result);
            added = result.Bones.Sum(t => t.RotationKeys.Length + t.PositionKeys.Length) - before;
        }
        report = new NormalizeReport(reordered, duplicates, outside, inserted, requantized, signs, pads, controls, added);
        return result;
    }

    /// <summary>
    /// Gives every bone the keys the engine needs: a bone with no rotation key gets one at start
    /// holding the skeleton's rest local rotation (identity without a skeleton or for a bone beyond
    /// it); a bone with fewer than two position keys gets constant keys at start and end (where it has
    /// none) holding its single existing position, or else the rest local position (the origin without
    /// a skeleton). Existing keys are untouched, except that a single existing position key's control
    /// points (unused until now) are set to its position so the hold is exactly constant. In a clip
    /// whose end is not after its start, the second
    /// position key goes one frame after start (outside the range: fix the range too). Returns the same
    /// instance when nothing is missing.
    /// </summary>
    public static RfaClip EnsureMinimumKeys(RfaClip clip, Skeleton? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var bones = clip.Bones.ToBuilder();
        bool any = false;
        int start = clip.StartTime;
        int end = clip.EndTime > start ? clip.EndTime : start + RfaClip.TicksPerFrame;
        for (int b = 0; b < bones.Count; b++)
        {
            var t = bones[b];
            bool hasRest = skeleton is not null && b < skeleton.Count;
            if (t.RotationKeys.Length < 2)
            {
                // Two keys, never one: with a single rotation key the engine's find_animation_rotation reads
                // key[1] past the track for any time at or before that key (phase 7b, RFA015).
                t = t with { RotationKeys = HoldRotation(t.RotationKeys, start, end,
                    hasRest ? skeleton!.RestLocal[b].Rotation : Quaternion.Identity) };
            }
            if (t.PositionKeys.Length < 2)
            {
                var keys = t.PositionKeys;
                var p = keys.Length == 1 ? keys[0].Position : hasRest ? skeleton!.RestLocal[b].Position : Vector3.Zero;
                // A single key's control points were never used; the hold segments now use them.
                if (keys.Length == 1) keys = [RfaPosKey.Constant(keys[0].Time, p)];
                if (!keys.Any(k => k.Time == start)) keys = keys.Insert(ClipEditTracks.LowerBound(keys, start), RfaPosKey.Constant(start, p));
                if (!keys.Any(k => k.Time == end)) keys = keys.Insert(ClipEditTracks.LowerBound(keys, end), RfaPosKey.Constant(end, p));
                if (keys.Length < 2) keys = keys.Add(RfaPosKey.Constant(keys[^1].Time + RfaClip.TicksPerFrame, p));
                t = t with { PositionKeys = keys };
            }
            if (!ReferenceEquals(t, bones[b]))
            {
                bones[b] = t;
                any = true;
            }
        }
        return any ? clip with { Bones = bones.MoveToImmutable() } : clip;
    }

    /// <summary>
    /// A rotation track of 0 or 1 keys made into a constant hold of at least two keys: the single key (raw
    /// int16 values and eases kept) is copied to <paramref name="start"/> and <paramref name="end"/> where it
    /// has none; with no key, <paramref name="rest"/> is quantised at both.
    /// </summary>
    internal static ImmutableArray<RfaRotKey> HoldRotation(ImmutableArray<RfaRotKey> keys, int start, int end, Quaternion rest)
    {
        if (keys.Length >= 2) return keys;
        if (end <= start) end = start + RfaClip.TicksPerFrame;
        var key = keys.Length == 1 ? keys[0] : QuantizeRotation(start, rest, null);
        var list = new List<RfaRotKey>();
        if (keys.Length == 1) list.Add(key);
        if (!list.Any(k => k.Time == start)) list.Add(key with { Time = start });
        if (!list.Any(k => k.Time == end)) list.Add(key with { Time = end });
        // A single key after the end (outside the range): one more frame after it.
        if (list.Count < 2) list.Add(key with { Time = key.Time + RfaClip.TicksPerFrame });
        return [.. list.OrderBy(k => k.Time)];
    }

    /// <summary>
    /// Every rotation track with exactly one key becomes a constant hold of two or more (the quick fix of
    /// RFA015). Tracks with no keys are left alone (that is RFA004, and a skeleton's rest is needed to fill it).
    /// Returns the same instance when no track has exactly one key.
    /// </summary>
    public static RfaClip FixSingleRotationKeys(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!clip.Bones.Any(b => b.RotationKeys.Length == 1)) return clip;
        var bones = clip.Bones.ToBuilder();
        for (int b = 0; b < bones.Count; b++)
        {
            if (bones[b].RotationKeys.Length == 1)
                bones[b] = bones[b] with { RotationKeys = HoldRotation(bones[b].RotationKeys, clip.StartTime, clip.EndTime, Quaternion.Identity) };
        }
        return clip with { Bones = bones.MoveToImmutable() };
    }
}

file static class NormalizeEditing
{
    public static ImmutableArray<T> Sort<T>(ImmutableArray<T> keys, Func<T, int> time, ref int reordered, ref int duplicates)
    {
        bool strictly = true;
        for (int i = 1; i < keys.Length && strictly; i++) strictly = time(keys[i]) > time(keys[i - 1]);
        if (strictly) return keys;
        var order = Enumerable.Range(0, keys.Length).OrderBy(i => time(keys[i])).ThenBy(i => i).ToList();
        bool moved = order.Where((v, i) => v != i).Any();
        var result = new List<T>();
        for (int j = 0; j < order.Count; j++)
        {
            // Of equal times keep the last stored key.
            if (j + 1 < order.Count && time(keys[order[j + 1]]) == time(keys[order[j]]))
            {
                duplicates++;
                continue;
            }
            result.Add(keys[order[j]]);
        }
        if (moved) reordered++;
        return [.. result];
    }

    public static ImmutableArray<T> Clamp<T>(
        ImmutableArray<T> keys, Func<T, int> time, int start, int end, Func<ImmutableArray<T>, int, int, ImmutableArray<T>> crop,
        ref int outside, ref int inserted)
    {
        int beyond = keys.Count(k => time(k) < start || time(k) > end);
        if (beyond == 0) return keys;
        var cropped = crop(keys, start, end);
        outside += beyond;
        inserted += cropped.Length - (keys.Length - beyond);
        return cropped;
    }
}
