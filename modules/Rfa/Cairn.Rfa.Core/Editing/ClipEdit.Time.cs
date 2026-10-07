using System.Collections.Immutable;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

public static partial class ClipEdit
{
    /// <summary>
    /// Crops the clip to [<paramref name="from"/>, <paramref name="to"/>] ticks: on every track a key
    /// is inserted at a boundary that has keys beyond it (by sampling; position curves split exactly
    /// by de Casteljau, rotation eases refit as in <see cref="KeyPoseAtTime"/>), keys outside the range
    /// are dropped, start/end become the range and the ramps are clamped to the new duration. The
    /// motion inside the range is unchanged; keys inside it stay bit-identical except the neighbours
    /// of an inserted boundary key (one control point or ease byte each).
    /// </summary>
    /// <remarks>
    /// A position track that had two or more keys keeps at least two (a constant key is added at the
    /// far boundary if only one key would remain). Morph data: version 8 keyframe times are absolute
    /// and the engine clamps outside them, so they are kept as they are; a version 7 morph spreads its
    /// keyframes over [start, end], so it is resampled at the new range (same keyframe count) to keep
    /// playing the same inside it.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="to"/> is not after <paramref name="from"/>.</exception>
    public static RfaClip Trim(RfaClip clip, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (to <= from)
            throw new ArgumentException($"The trim range must be at least one tick long; got {from} to {to}. Pass the earlier time first.", nameof(to));
        var bones = clip.Bones.ToBuilder();
        for (int i = 0; i < bones.Count; i++)
        {
            var t = bones[i];
            bones[i] = t with
            {
                RotationKeys = ClipEditTracks.CropRotation(t.RotationKeys, from, to),
                PositionKeys = ClipEditTracks.CropPosition(t.PositionKeys, from, to),
            };
        }
        int duration = to - from;
        var morph = clip.Morph;
        if (clip.Version < 8 && (from != clip.StartTime || to != clip.EndTime))
            morph = ClipEditMorph.ResampleV7(clip.Morph, clip.StartTime, clip.EndTime, from, to);
        int rampIn = Math.Clamp(clip.RampIn, 0, duration), rampOut = Math.Clamp(clip.RampOut, 0, duration);
        // Ramps that fitted the clip must still fit together in the shorter one (else the action never
        // reaches full weight): both shrink in proportion. Ramps that never fitted are only clamped.
        if (rampIn + rampOut > duration && clip.RampIn + clip.RampOut <= clip.EndTime - clip.StartTime)
        {
            double scale = duration / (double)(rampIn + rampOut);
            rampIn = (int)Math.Floor(rampIn * scale);
            rampOut = (int)Math.Floor(rampOut * scale);
        }
        return clip with
        {
            Bones = bones.MoveToImmutable(),
            StartTime = from,
            EndTime = to,
            RampIn = rampIn,
            RampOut = rampOut,
            Morph = morph,
        };
    }

    /// <summary>
    /// Moves the whole clip in time by <paramref name="deltaTicks"/>: every key time, start and end,
    /// and version 8 morph keyframe times (version 7 morph timing is relative to start/end). Values
    /// and ramps are untouched; shifting back by the negated delta restores the clip exactly.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A time would not fit in 32 bits.</exception>
    public static RfaClip Shift(RfaClip clip, int deltaTicks)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (deltaTicks == 0) return clip;
        return TimeEditing.MapTimes(clip, t => ClipEditTracks.ToTime((double)t + deltaTicks, nameof(deltaTicks)), clip.RampIn, clip.RampOut, resolve: false);
    }

    /// <summary>
    /// Scales the clip's duration by <paramref name="factor"/> about <paramref name="pivot"/> (the
    /// start time when null): every time becomes <c>pivot + round((t - pivot) * factor)</c>
    /// (midpoints away from zero), including start, end and version 8 morph keyframe times; ramps
    /// become <c>round(ramp * factor)</c>. Values are untouched (Bezier control points are absolute, so
    /// curves keep their shape; eases are relative). Keys that round onto the same tick collide: the
    /// later key wins. Without collisions, retiming by 2 and then by 0.5 restores every time.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The factor is not a positive finite number (use <see cref="Reverse"/> to reverse).</exception>
    public static RfaClip Retime(RfaClip clip, double factor, int? pivot = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (!double.IsFinite(factor) || factor <= 0)
            throw new ArgumentOutOfRangeException(nameof(factor), $"The retime factor must be a positive number; got {factor}. Use Reverse to play a clip backwards.");
        if (factor == 1) return clip;
        double p = pivot ?? clip.StartTime;
        int Map(int t) => ClipEditTracks.ToTime(p + (t - p) * factor, nameof(factor));
        int Ramp(int r) => ClipEditTracks.ToTime(r * factor, nameof(factor));
        return TimeEditing.MapTimes(clip, Map, Ramp(clip.RampIn), Ramp(clip.RampOut), resolve: true);
    }

    /// <summary>
    /// Plays the clip backwards: every time <c>t</c> becomes <c>start + end - t</c> and every track's
    /// key order is reversed. Each rotation key swaps its ease-in and ease-out and each position key
    /// swaps its in and out control points, so every segment is the exact mirror of the original
    /// (the engine's ease curve is symmetric under this swap). Reversing twice restores the clip bit
    /// for bit.
    /// </summary>
    /// <remarks>
    /// Version 8 morph: keyframe times are mirrored and the keyframes reordered so the times still
    /// increase. Version 7 morph: the keyframes are reversed in order; because v7 keyframe k sits at
    /// <c>start + k (end - start) / count</c> (k = 0 .. count-1, the last one held to the end), the
    /// reversed morph runs one keyframe step (<c>(end - start) / count</c> ticks) early compared with an
    /// exact time reversal. Convert to version 8 first when that matters. Ramps are kept.
    /// </remarks>
    public static RfaClip Reverse(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        long sum = (long)clip.StartTime + clip.EndTime;
        int Map(int t) => ClipEditTracks.ToTime(sum - t, nameof(clip));
        var bones = clip.Bones.ToBuilder();
        for (int i = 0; i < bones.Count; i++)
        {
            var t = bones[i];
            var rot = t.RotationKeys.Reverse().Select(k => k with { Time = Map(k.Time), EaseIn = k.EaseOut, EaseOut = k.EaseIn });
            var pos = t.PositionKeys.Reverse().Select(k => k with { Time = Map(k.Time), InControl = k.OutControl, OutControl = k.InControl });
            bones[i] = t with { RotationKeys = [.. rot], PositionKeys = [.. pos] };
        }
        return clip with { Bones = bones.MoveToImmutable(), Morph = ClipEditMorph.Reverse(clip.Morph, Map) };
    }

    /// <summary>
    /// Sets start and end to the earliest and latest key time over every rotation and position key
    /// of every bone (morph times are not considered; ramps are kept). A clip with no keys at all is
    /// returned unchanged.
    /// </summary>
    public static RfaClip RecomputeRange(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int min = int.MaxValue, max = int.MinValue;
        foreach (var t in clip.Bones)
        {
            foreach (var k in t.RotationKeys) (min, max) = (Math.Min(min, k.Time), Math.Max(max, k.Time));
            foreach (var k in t.PositionKeys) (min, max) = (Math.Min(min, k.Time), Math.Max(max, k.Time));
        }
        if (min > max || (min == clip.StartTime && max == clip.EndTime)) return clip;
        // Version 7 morph keyframes are spread over [start, end]: keep them playing at the same times.
        var morph = clip.Version < 8 && !clip.Morph.IsEmpty && max > min && clip.EndTime > clip.StartTime
            ? ClipEditMorph.ResampleV7(clip.Morph, clip.StartTime, clip.EndTime, min, max)
            : clip.Morph;
        return clip with { StartTime = min, EndTime = max, Morph = morph };
    }

}

file static class TimeEditing
{
    // Applies a monotonic time map to every key, start/end and v8 morph times. With resolve, tracks go
    // through the collision rule (later key wins, survivors sorted); without, keys keep their stored order.
    public static RfaClip MapTimes(RfaClip clip, Func<int, int> map, int rampIn, int rampOut, bool resolve)
    {
        var bones = clip.Bones.ToBuilder();
        for (int i = 0; i < bones.Count; i++)
        {
            var t = bones[i];
            if (!resolve)
            {
                bones[i] = t with
                {
                    RotationKeys = [.. t.RotationKeys.Select(k => k with { Time = map(k.Time) })],
                    PositionKeys = [.. t.PositionKeys.Select(k => k with { Time = map(k.Time) })],
                };
                continue;
            }
            var rot = ClipEditTracks.Rearrange(
                t.RotationKeys, Enumerable.Repeat(true, t.RotationKeys.Length).ToArray(), map, k => k.Time, (k, nt) => k with { Time = nt }, out _, out _);
            var pos = ClipEditTracks.Rearrange(
                t.PositionKeys, Enumerable.Repeat(true, t.PositionKeys.Length).ToArray(), map, k => k.Time, (k, nt) => k with { Time = nt }, out _, out _);
            bones[i] = t with { RotationKeys = rot, PositionKeys = pos };
        }
        var morph = clip.Morph;
        if (clip.Version >= 8 && !morph.KeyframeTimes.IsDefaultOrEmpty)
            morph = morph with { KeyframeTimes = [.. morph.KeyframeTimes.Select(map)] };
        return clip with
        {
            Bones = bones.MoveToImmutable(),
            StartTime = map(clip.StartTime),
            EndTime = map(clip.EndTime),
            RampIn = rampIn,
            RampOut = rampOut,
            Morph = morph,
        };
    }
}
