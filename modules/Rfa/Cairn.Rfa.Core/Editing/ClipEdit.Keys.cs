using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

public static partial class ClipEdit
{
    // ── Insert / replace ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inserts a stored rotation key into one bone's track at its time, replacing a key already at
    /// that time. The key's components, eases and pad are kept, except that it is negated exactly
    /// (same rotation) when it is in the other hemisphere from the key before it (or, when it becomes
    /// the first key, the key after it). Other keys are untouched (a following key is never re-signed).
    /// </summary>
    public static RfaClip InsertRotationKey(RfaClip clip, int bone, RfaRotKey key) =>
        InsertRotationKey(clip, bone, key, out _);

    /// <summary>As <see cref="InsertRotationKey(RfaClip, int, RfaRotKey)"/>; <paramref name="index"/> is the key's index in the new track.</summary>
    public static RfaClip InsertRotationKey(RfaClip clip, int bone, RfaRotKey key, out int index)
    {
        CheckBone(clip, bone);
        var keys = clip.Bones[bone].RotationKeys;
        int i = ClipEditTracks.LowerBound(keys, key.Time);
        bool replace = i < keys.Length && keys[i].Time == key.Time;
        var b = keys.ToBuilder();
        if (replace) b.RemoveAt(i);
        RfaRotKey? reference = i > 0 ? b[i - 1] : i < b.Count ? b[i] : null;
        if (reference is { } r && ClipEditTracks.StoredDot(key, r) < 0) key = ClipEditTracks.Negated(key);
        b.Insert(i, key);
        index = i;
        return WithRotationKeys(clip, bone, b.ToImmutable());
    }

    /// <summary>
    /// Inserts a rotation key for the ACTIVE-convention rotation <paramref name="active"/> at
    /// <paramref name="time"/> ticks (quantised once, sign-continuous with its neighbour, pad 0),
    /// replacing a key already at that time.
    /// </summary>
    public static RfaClip InsertRotationKey(RfaClip clip, int bone, int time, Quaternion active, sbyte easeIn = 0, sbyte easeOut = 0)
    {
        CheckBone(clip, bone);
        var keys = clip.Bones[bone].RotationKeys;
        int i = ClipEditTracks.LowerBound(keys, time);
        var list = keys.ToList();
        if (i < list.Count && list[i].Time == time) list.RemoveAt(i);
        list.Insert(i, default);
        list[i] = ClipEditTracks.QuantizeAt(list, i, time, active, easeIn, easeOut);
        return WithRotationKeys(clip, bone, [.. list]);
    }

    /// <summary>Inserts a position key exactly as given at its time, replacing a key already at that time.</summary>
    public static RfaClip InsertPositionKey(RfaClip clip, int bone, RfaPosKey key) =>
        InsertPositionKey(clip, bone, key, out _);

    /// <summary>As <see cref="InsertPositionKey(RfaClip, int, RfaPosKey)"/>; <paramref name="index"/> is the key's index in the new track.</summary>
    public static RfaClip InsertPositionKey(RfaClip clip, int bone, RfaPosKey key, out int index)
    {
        CheckBone(clip, bone);
        var keys = clip.Bones[bone].PositionKeys;
        int i = ClipEditTracks.LowerBound(keys, key.Time);
        var b = keys.ToBuilder();
        if (i < keys.Length && keys[i].Time == key.Time) b.RemoveAt(i);
        b.Insert(i, key);
        index = i;
        return WithPositionKeys(clip, bone, b.ToImmutable());
    }

    /// <summary>
    /// Replaces rotation key <paramref name="index"/> of a bone with <paramref name="key"/>. When the
    /// time is unchanged the key is stored exactly as given in place; otherwise this is a delete
    /// followed by <see cref="InsertRotationKey(RfaClip, int, RfaRotKey)"/> (so it moves to its time,
    /// replacing any key there, and is sign-aligned with its new neighbour).
    /// </summary>
    public static RfaClip ReplaceRotationKey(RfaClip clip, int bone, int index, RfaRotKey key)
    {
        CheckBone(clip, bone);
        var keys = clip.Bones[bone].RotationKeys;
        KeyEditing.CheckIndex(keys.Length, index, bone, "rotation");
        if (keys[index].Time == key.Time) return WithRotationKeys(clip, bone, keys.SetItem(index, key));
        return InsertRotationKey(WithRotationKeys(clip, bone, keys.RemoveAt(index)), bone, key);
    }

    /// <summary>
    /// Replaces position key <paramref name="index"/> of a bone with <paramref name="key"/>: in place
    /// when the time is unchanged, otherwise a delete followed by
    /// <see cref="InsertPositionKey(RfaClip, int, RfaPosKey)"/>.
    /// </summary>
    public static RfaClip ReplacePositionKey(RfaClip clip, int bone, int index, RfaPosKey key)
    {
        CheckBone(clip, bone);
        var keys = clip.Bones[bone].PositionKeys;
        KeyEditing.CheckIndex(keys.Length, index, bone, "position");
        if (keys[index].Time == key.Time) return WithPositionKeys(clip, bone, keys.SetItem(index, key));
        return InsertPositionKey(WithPositionKeys(clip, bone, keys.RemoveAt(index)), bone, key);
    }

    // ── Delete ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes the selected keys. By default a deletion may leave a bone with no rotation key or
    /// fewer than two position keys (the linter flags that: no position keys collapses the bone onto
    /// its parent). With <paramref name="keepMinimum"/> true, just enough selected keys are kept to
    /// leave 2 rotation keys and 2 position keys (or all the track had): first the earliest selected
    /// key, then the latest, then the others in order.
    /// </summary>
    public static RfaClip DeleteKeys(RfaClip clip, KeySelection selection, bool keepMinimum = false)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        if (selection.IsEmpty) return clip;
        var bones = clip.Bones.ToBuilder();
        foreach (int bone in selection.SelectedBones)
        {
            var track = bones[bone];
            // Two rotation keys, not one: a lone rotation key makes the engine read past the track (RFA015).
            var rot = Keep(track.RotationKeys.Length, selection.IndicesOf(bone, KeyKind.Rotation).ToList(), keepMinimum ? 2 : 0);
            var pos = Keep(track.PositionKeys.Length, selection.IndicesOf(bone, KeyKind.Position).ToList(), keepMinimum ? 2 : 0);
            bones[bone] = track with
            {
                RotationKeys = [.. track.RotationKeys.Where((_, i) => rot[i])],
                PositionKeys = [.. track.PositionKeys.Where((_, i) => pos[i])],
            };
        }
        return clip with { Bones = bones.MoveToImmutable() };

        static bool[] Keep(int count, List<int> selected, int minimum)
        {
            var keep = Enumerable.Repeat(true, count).ToArray();
            foreach (int i in selected) keep[i] = false;
            int remaining = count - selected.Count;
            if (remaining < minimum && selected.Count > 0)
            {
                var order = new List<int> { selected[0] };
                if (selected.Count > 1) order.Add(selected[^1]);
                order.AddRange(selected.Skip(1).Take(selected.Count - 2));
                foreach (int i in order)
                {
                    if (remaining >= minimum) break;
                    keep[i] = true;
                    remaining++;
                }
            }
            return keep;
        }
    }

    // ── Move / scale ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Moves the selected keys by <paramref name="deltaTicks"/>. Values are untouched (moved rotation
    /// keys are only negated exactly when needed for sign continuity with their new predecessor).
    /// </summary>
    /// <remarks>
    /// Collision rule, per track (shared by <see cref="ScaleKeys(RfaClip, KeySelection, int, double)"/>
    /// and <see cref="SetKeyTime"/>):
    /// <list type="number">
    /// <item>Moved keys keep their order relative to each other (for a negative scale, reversed).</item>
    /// <item>Two moved keys landing on the same tick: the one later in the original track wins.</item>
    /// <item>A moved key landing exactly on an unselected key's time replaces it.</item>
    /// <item>Moved keys never pass over unselected keys: an unselected key that a moved key would
    /// jump over (its order relative to any selected key would change) is merged into the moved block,
    /// i.e. removed. Use <see cref="ClampMoveDelta"/> first for a drag that stops at the neighbours
    /// instead.</item>
    /// </list>
    /// Keys are not clamped to [start, end]; keys moved outside are a lint finding
    /// (<see cref="RecomputeRange"/> or <see cref="Normalize(RfaClip, NormalizeOptions?)"/> fix it).
    /// </remarks>
    public static RfaClip MoveKeys(RfaClip clip, KeySelection selection, int deltaTicks) =>
        MoveKeys(clip, selection, deltaTicks, out _);

    /// <summary>As <see cref="MoveKeys(RfaClip, KeySelection, int)"/>; <paramref name="moved"/> addresses the moved keys in the result.</summary>
    public static RfaClip MoveKeys(RfaClip clip, KeySelection selection, int deltaTicks, out KeySelection moved)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        if (deltaTicks == 0 || selection.IsEmpty)
        {
            moved = selection;
            return clip;
        }
        return KeyEditing.RetimeSelection(clip, selection, t => ClipEditTracks.ToTime((double)t + deltaTicks, nameof(deltaTicks)), false, out moved);
    }

    /// <summary>
    /// Scales the selected keys' times about <paramref name="pivotTime"/>:
    /// <c>t' = pivot + round((t - pivot) * factor)</c> (midpoints away from zero). A negative factor
    /// reverses the selected block in time; each reversed key then swaps its ease-in and ease-out and
    /// its in and out control points, so the segments between selected keys play mirrored. Collisions
    /// follow the rule of <see cref="MoveKeys(RfaClip, KeySelection, int)"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The factor is 0 or not finite.</exception>
    public static RfaClip ScaleKeys(RfaClip clip, KeySelection selection, int pivotTime, double factor) =>
        ScaleKeys(clip, selection, pivotTime, factor, out _);

    /// <summary>As <see cref="ScaleKeys(RfaClip, KeySelection, int, double)"/>; <paramref name="scaled"/> addresses the scaled keys in the result.</summary>
    public static RfaClip ScaleKeys(RfaClip clip, KeySelection selection, int pivotTime, double factor, out KeySelection scaled)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        if (!double.IsFinite(factor) || factor == 0)
            throw new ArgumentOutOfRangeException(nameof(factor), $"The scale factor must be a non-zero finite number; got {factor}. Use a negative factor to reverse.");
        if (factor == 1 || selection.IsEmpty)
        {
            scaled = selection;
            return clip;
        }
        return KeyEditing.RetimeSelection(
            clip, selection, t => ClipEditTracks.ToTime(pivotTime + ((double)t - pivotTime) * factor, nameof(factor)), factor < 0, out scaled);
    }

    /// <summary>Moves one key to <paramref name="time"/> ticks, with the collision rule of <see cref="MoveKeys(RfaClip, KeySelection, int)"/>.</summary>
    public static RfaClip SetKeyTime(RfaClip clip, KeyRef key, int time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!key.IsValidIn(clip))
            throw new ArgumentException($"The clip has no {key.Kind.ToString().ToLowerInvariant()} key {key.Index} on bone {key.Bone}.", nameof(key));
        return MoveKeys(clip, KeySelection.Of(key), time - key.TimeIn(clip));
    }

    /// <summary>
    /// <paramref name="deltaTicks"/> limited so that moving the selection by it never reaches or
    /// passes an unselected key of the same track (the whole selection moves together, so the most
    /// restrictive track decides). Returns 0 when the tracks are not in strictly increasing order.
    /// </summary>
    public static int ClampMoveDelta(RfaClip clip, KeySelection selection, int deltaTicks)
    {
        ClipEditTracks.CheckSelection(clip, selection);
        long lo = long.MinValue, hi = long.MaxValue;
        foreach (int bone in selection.SelectedBones)
        {
            var track = clip.Bones[bone];
            Limit(track.RotationKeys.Select(k => k.Time).ToArray(), ClipEditTracks.Flags(selection, bone, KeyKind.Rotation, track.RotationKeys.Length));
            Limit(track.PositionKeys.Select(k => k.Time).ToArray(), ClipEditTracks.Flags(selection, bone, KeyKind.Position, track.PositionKeys.Length));
        }
        if (lo > 0 || hi < 0) return 0;
        return (int)Math.Clamp(deltaTicks, lo, hi);

        void Limit(int[] times, bool[] selected)
        {
            for (int i = 0; i < times.Length; i++)
            {
                if (!selected[i]) continue;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (selected[j]) continue;
                    lo = Math.Max(lo, (long)times[j] + 1 - times[i]);
                    break;
                }
                for (int j = i + 1; j < times.Length; j++)
                {
                    if (selected[j]) continue;
                    hi = Math.Min(hi, (long)times[j] - 1 - times[i]);
                    break;
                }
            }
        }
    }

}

file static class KeyEditing
{
    public static RfaClip RetimeSelection(RfaClip clip, KeySelection selection, Func<int, int> newTime, bool mirror, out KeySelection result)
    {
        var bones = clip.Bones.ToBuilder();
        var refs = new List<KeyRef>();
        foreach (int bone in selection.SelectedBones)
        {
            var track = bones[bone];
            var rotFlags = ClipEditTracks.Flags(selection, bone, KeyKind.Rotation, track.RotationKeys.Length);
            var posFlags = ClipEditTracks.Flags(selection, bone, KeyKind.Position, track.PositionKeys.Length);
            var rot = track.RotationKeys;
            var pos = track.PositionKeys;
            if (rotFlags.Contains(true))
            {
                rot = ClipEditTracks.Rearrange(
                    rot, rotFlags, newTime, k => k.Time,
                    (k, t) => mirror ? k with { Time = t, EaseIn = k.EaseOut, EaseOut = k.EaseIn } : k with { Time = t },
                    out var sel, out var moved);
                rot = ClipEdit.AlignChangedSigns(rot, moved);
                refs.AddRange(sel.Select(i => new KeyRef(bone, KeyKind.Rotation, i)));
            }
            if (posFlags.Contains(true))
            {
                pos = ClipEditTracks.Rearrange(
                    pos, posFlags, newTime, k => k.Time,
                    (k, t) => mirror ? k with { Time = t, InControl = k.OutControl, OutControl = k.InControl } : k with { Time = t },
                    out var sel, out _);
                refs.AddRange(sel.Select(i => new KeyRef(bone, KeyKind.Position, i)));
            }
            bones[bone] = track with { RotationKeys = rot, PositionKeys = pos };
        }
        result = KeySelection.Of(refs);
        return clip with { Bones = bones.MoveToImmutable() };
    }

    public static void CheckIndex(int count, int index, int bone, string kind)
    {
        if ((uint)index >= (uint)count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Bone {bone} has {count} {kind} keys; there is no key {index}.");
    }
}
