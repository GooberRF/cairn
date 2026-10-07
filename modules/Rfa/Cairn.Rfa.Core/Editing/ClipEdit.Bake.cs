using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

/// <summary>Options for <see cref="ClipEdit.ReduceKeys"/>.</summary>
public sealed record ReduceOptions
{
    /// <summary>Largest allowed rotation error per bone, in degrees (local rotation).</summary>
    public float RotationToleranceDegrees { get; init; } = 0.1f;

    /// <summary>Largest allowed position error per bone, in metres (local position).</summary>
    public float PositionTolerance { get; init; } = 0.0005f;

    /// <summary>Error is checked at <c>start + j * CheckStepTicks</c> (and at end); 16 ticks = 300 checks a second.</summary>
    public int CheckStepTicks { get; init; } = 16;

    /// <summary>Reduce rotation keys.</summary>
    public bool Rotations { get; init; } = true;

    /// <summary>Reduce position keys.</summary>
    public bool Positions { get; init; } = true;

    /// <summary>Only these bones (null: every bone).</summary>
    public IReadOnlyCollection<int>? Bones { get; init; }
}

/// <summary>The outcome of <see cref="ClipEdit.ReduceKeys"/>.</summary>
/// <param name="Clip">The reduced clip.</param>
/// <param name="MaxRotationErrorDegrees">The largest rotation difference from the original, measured by sampling both at the check step over [start, end].</param>
/// <param name="MaxPositionError">The largest position difference in metres, measured the same way.</param>
/// <param name="KeysBefore">Rotation plus position keys before.</param>
/// <param name="KeysAfter">Rotation plus position keys after.</param>
public sealed record ReduceResult(RfaClip Clip, float MaxRotationErrorDegrees, float MaxPositionError, int KeysBefore, int KeysAfter);

public static partial class ClipEdit
{
    /// <summary>
    /// Bakes the clip to keys every <paramref name="stepTicks"/> from start to end (end always
    /// included, even off the step), replacing the keys of the chosen kinds with sampled values.
    /// </summary>
    /// <remarks>
    /// Rotation keys are the sampled rotations (sign-continuous, eases 0). Position keys are the
    /// sampled positions; between two new keys whose stretch lies inside one original segment (or a
    /// clamped constant end) the control points are that stretch's exact sub-curve (de Casteljau), so
    /// the curve is reproduced exactly; a stretch spanning an original key gets linear control points
    /// (a third of the way to the neighbour). Tracks with no keys of a kind stay empty (there is no
    /// motion to bake). A clip whose end is not after its start bakes to one key at start.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stepTicks"/> is not positive.</exception>
    public static RfaClip Resample(RfaClip clip, int stepTicks, bool rotations = true, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (stepTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(stepTicks), $"The resample step must be at least 1 tick; got {stepTicks} (160 ticks is one frame at 30 fps).");
        var times = BakeEditing.Grid(clip.StartTime, clip.EndTime, stepTicks);
        var bones = clip.Bones.ToBuilder();
        for (int i = 0; i < bones.Count; i++)
        {
            var t = bones[i];
            if (rotations && t.RotationKeys.Length > 0)
            {
                var src = t.RotationKeys;
                var keys = new List<RfaRotKey>(times.Length);
                foreach (int time in times)
                {
                    var q = ClipSampler.SampleRotation(src.AsSpan(), time);
                    keys.Add(QuantizeRotation(time, q, keys.Count > 0 ? keys[^1] : null));
                }
                t = t with { RotationKeys = [.. keys] };
            }
            if (positions && t.PositionKeys.Length > 0)
            {
                var src = t.PositionKeys;
                var pts = times.Select(time => ClipSampler.SamplePosition(src.AsSpan(), time)).ToArray();
                var keys = new RfaPosKey[times.Length];
                for (int k = 0; k < times.Length; k++) keys[k] = RfaPosKey.Constant(times[k], pts[k]);
                for (int k = 0; k + 1 < times.Length; k++)
                {
                    var (cout, cin) = ClipEditTracks.SubCurve(src, times[k], times[k + 1])
                        ?? (pts[k] + (pts[k + 1] - pts[k]) / 3f, pts[k + 1] + (pts[k] - pts[k + 1]) / 3f);
                    keys[k] = keys[k] with { OutControl = cout };
                    keys[k + 1] = keys[k + 1] with { InControl = cin };
                }
                t = t with { PositionKeys = [.. keys] };
            }
            bones[i] = t;
        }
        return clip with { Bones = bones.MoveToImmutable() };
    }

    /// <summary>
    /// Removes keys while the sampled motion stays within the tolerances of the original, checked at
    /// every <see cref="ReduceOptions.CheckStepTicks"/> from start to end. The reported errors are
    /// measured independently afterwards by sampling the result against the original at the same
    /// check times.
    /// </summary>
    /// <remarks>
    /// Per track, Douglas-Peucker on the key list: keep the first and last key; if the segment between
    /// two kept keys misses a check time by more than the tolerance, also keep the inner key with the
    /// largest error at its own time (or, when every inner key is within tolerance, the one nearest the
    /// worst check time) and recurse on both halves. The result is a subset of the original keys.
    /// Kept rotation keys are bit-identical (their eases now shape the merged segment). Kept position
    /// keys are bit-identical except the control points facing a merged segment: those two (the
    /// earlier key's out, the later key's in) are refit by least squares to the original curve over
    /// the merged stretch. A track keeps at least its first and last key, so position tracks keep two
    /// keys if they had two. Untouched kinds and bones are returned as they are.
    /// </remarks>
    public static ReduceResult ReduceKeys(RfaClip clip, ReduceOptions options)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(options);
        if (options.CheckStepTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), $"The check step must be at least 1 tick; got {options.CheckStepTicks}.");
        if (!(options.RotationToleranceDegrees >= 0f) || !(options.PositionTolerance >= 0f))
            throw new ArgumentOutOfRangeException(nameof(options), "Tolerances must be 0 or more.");

        int step = options.CheckStepTicks;
        var bones = clip.Bones.ToBuilder();
        var only = options.Bones is null ? null : new HashSet<int>(options.Bones);
        for (int i = 0; i < bones.Count; i++)
        {
            if (only is not null && !only.Contains(i)) continue;
            var t = bones[i];
            if (options.Rotations && t.RotationKeys.Length > 2)
                t = t with { RotationKeys = BakeEditing.ReduceRotation(t.RotationKeys, clip.StartTime, clip.EndTime, step, options.RotationToleranceDegrees) };
            if (options.Positions && t.PositionKeys.Length > 2)
                t = t with { PositionKeys = BakeEditing.ReducePosition(t.PositionKeys, clip.StartTime, clip.EndTime, step, options.PositionTolerance) };
            bones[i] = t;
        }
        var result = clip with { Bones = bones.MoveToImmutable() };
        var (rotError, posError) = MeasureError(clip, result, step);
        return new ReduceResult(result, rotError, posError, BakeEditing.KeyCount(clip), BakeEditing.KeyCount(result));
    }

    /// <summary>
    /// The largest per-bone local rotation difference (degrees) and position difference (metres)
    /// between two clips with the same bone count, sampled at <c>start + j * stepTicks</c> and at end
    /// of <paramref name="original"/>'s range.
    /// </summary>
    public static (float RotationDegrees, float Position) MeasureError(RfaClip original, RfaClip edited, int stepTicks)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(edited);
        if (stepTicks <= 0) throw new ArgumentOutOfRangeException(nameof(stepTicks), "The step must be at least 1 tick.");
        if (original.BoneCount != edited.BoneCount)
            throw new ArgumentException($"The clips have {original.BoneCount} and {edited.BoneCount} bones; compare clips with the same bones.", nameof(edited));
        float rot = 0f, pos = 0f;
        var times = BakeEditing.Grid(original.StartTime, original.EndTime, stepTicks);
        for (int b = 0; b < original.BoneCount; b++)
        {
            var o = original.Bones[b];
            var e = edited.Bones[b];
            foreach (int time in times)
            {
                rot = MathF.Max(rot, ClipEditTracks.AngleDegrees(
                    ClipSampler.SampleRotation(o.RotationKeys.AsSpan(), time), ClipSampler.SampleRotation(e.RotationKeys.AsSpan(), time)));
                pos = MathF.Max(pos, Vector3.Distance(
                    ClipSampler.SamplePosition(o.PositionKeys.AsSpan(), time), ClipSampler.SamplePosition(e.PositionKeys.AsSpan(), time)));
            }
        }
        return (rot, pos);
    }
}

file static class BakeEditing
{
    // start, start + step, ..., and end (once) when it is after start.
    public static int[] Grid(int start, int end, int step)
    {
        var list = new List<int>();
        for (long t = start; t < end; t += step) list.Add((int)t);
        if (list.Count == 0 || end > start) list.Add(end);
        return [.. list.Distinct()];
    }

    public static int KeyCount(RfaClip clip) => clip.Bones.Sum(b => b.RotationKeys.Length + b.PositionKeys.Length);

    // Check times strictly inside (ta, tb): the global grid start + j * step, plus end.
    private static IEnumerable<int> Checks(int start, int end, int step, int ta, int tb)
    {
        long j = (long)Math.Floor((ta - (double)start) / step);
        for (long t = start + j * step; t < tb; t += step)
        {
            if (t > ta) yield return (int)t;
        }
        if (end > ta && end < tb && ((long)end - start) % step != 0) yield return end;
    }

    public static ImmutableArray<RfaRotKey> ReduceRotation(ImmutableArray<RfaRotKey> keys, int start, int end, int step, float tolerance)
    {
        int n = keys.Length;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var cache = new Dictionary<int, Quaternion>();
        var stack = new Stack<(int A, int C)>();
        stack.Push((0, n - 1));
        Span<RfaRotKey> pair = stackalloc RfaRotKey[2];
        while (stack.Count > 0)
        {
            var (a, c) = stack.Pop();
            if (c - a < 2) continue;
            pair[0] = keys[a];
            pair[1] = keys[c];
            float worst = 0f;
            int worstTime = 0;
            foreach (int time in Checks(start, end, step, keys[a].Time, keys[c].Time))
            {
                float e = ClipEditTracks.AngleDegrees(Original(time), ClipSampler.SampleRotation(pair, time));
                if (e > worst) (worst, worstTime) = (e, time);
            }
            if (worst <= tolerance) continue;
            int m = -1;
            float best = -1f;
            for (int k = a + 1; k < c; k++)
            {
                float e = ClipEditTracks.AngleDegrees(ClipEdit.KeyRotation(keys[k]), ClipSampler.SampleRotation(pair, keys[k].Time));
                if (e > best) (best, m) = (e, k);
            }
            if (best <= tolerance) m = Nearest(keys.Select(k => k.Time).ToArray(), a, c, worstTime);
            keep[m] = true;
            stack.Push((a, m));
            stack.Push((m, c));
        }
        return [.. keys.Where((_, i) => keep[i])];

        Quaternion Original(int time)
        {
            if (!cache.TryGetValue(time, out var q)) cache[time] = q = ClipSampler.SampleRotation(keys.AsSpan(), time);
            return q;
        }
    }

    public static ImmutableArray<RfaPosKey> ReducePosition(ImmutableArray<RfaPosKey> keys, int start, int end, int step, float tolerance)
    {
        int n = keys.Length;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        // Fitted control points per merged segment, keyed by its first kept key.
        var fits = new Dictionary<int, (int C, Vector3 Out, Vector3 In)>();
        var cache = new Dictionary<int, Vector3>();
        var stack = new Stack<(int A, int C)>();
        stack.Push((0, n - 1));
        Span<RfaPosKey> pair = stackalloc RfaPosKey[2];
        while (stack.Count > 0)
        {
            var (a, c) = stack.Pop();
            if (c - a < 2) continue;
            var (cout, cin) = Fit(keys, a, c, start, end, step);
            pair[0] = keys[a] with { OutControl = cout };
            pair[1] = keys[c] with { InControl = cin };
            float worst = 0f;
            int worstTime = 0;
            foreach (int time in Checks(start, end, step, keys[a].Time, keys[c].Time))
            {
                float e = Vector3.Distance(Original(time), ClipSampler.SamplePosition(pair, time));
                if (e > worst) (worst, worstTime) = (e, time);
            }
            if (worst <= tolerance)
            {
                fits[a] = (c, cout, cin);
                continue;
            }
            int m = -1;
            float best = -1f;
            for (int k = a + 1; k < c; k++)
            {
                float e = Vector3.Distance(keys[k].Position, ClipSampler.SamplePosition(pair, keys[k].Time));
                if (e > best) (best, m) = (e, k);
            }
            if (best <= tolerance) m = Nearest(keys.Select(k => k.Time).ToArray(), a, c, worstTime);
            keep[m] = true;
            stack.Push((a, m));
            stack.Push((m, c));
        }

        var result = keys.ToBuilder();
        foreach (var (a, (c, cout, cin)) in fits)
        {
            result[a] = result[a] with { OutControl = cout };
            result[c] = result[c] with { InControl = cin };
        }
        return [.. result.Where((_, i) => keep[i])];

        Vector3 Original(int time)
        {
            if (!cache.TryGetValue(time, out var p)) cache[time] = p = ClipSampler.SamplePosition(keys.AsSpan(), time);
            return p;
        }
    }

    // The inner key nearest a time (ties: the earlier).
    private static int Nearest(int[] times, int a, int c, int time)
    {
        int m = a + 1;
        for (int k = a + 2; k < c; k++)
        {
            if (Math.Abs((long)times[k] - time) < Math.Abs((long)times[m] - time)) m = k;
        }
        return m;
    }

    // Least-squares control points for one cubic from keys[a] to keys[c] (endpoints fixed, linear time
    // parameter) against the original curve, sampled at the check times, the inner keys and 16 even times.
    private static (Vector3 Out, Vector3 In) Fit(ImmutableArray<RfaPosKey> keys, int a, int c, int start, int end, int step)
    {
        var p0 = keys[a].Position;
        var p3 = keys[c].Position;
        int ta = keys[a].Time, tc = keys[c].Time;
        float span = tc - ta;
        var times = new List<float>();
        foreach (int t in Checks(start, end, step, ta, tc)) times.Add(t);
        for (int k = a + 1; k < c; k++) times.Add(keys[k].Time);
        for (int j = 1; j <= 16; j++) times.Add(ta + span * j / 17f);

        double s11 = 0, s12 = 0, s22 = 0;
        Vector3 r1 = Vector3.Zero, r2 = Vector3.Zero;
        foreach (float t in times)
        {
            float u = (t - ta) / span, v = 1f - u;
            float b0 = v * v * v, b1 = 3f * u * v * v, b2 = 3f * u * u * v, b3 = u * u * u;
            var q = ClipSampler.SamplePosition(keys.AsSpan(), t) - b0 * p0 - b3 * p3;
            s11 += b1 * b1;
            s12 += b1 * b2;
            s22 += b2 * b2;
            r1 += b1 * q;
            r2 += b2 * q;
        }
        double det = s11 * s22 - s12 * s12;
        if (Math.Abs(det) < 1e-12) return (p0 + (p3 - p0) / 3f, p3 + (p0 - p3) / 3f);
        float i11 = (float)(s22 / det), i12 = (float)(-s12 / det), i22 = (float)(s11 / det);
        return (i11 * r1 + i12 * r2, i12 * r1 + i22 * r2);
    }
}
