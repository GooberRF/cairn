using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>
/// Track-level building blocks shared by the <see cref="ClipEdit"/> operations: inserting a key that
/// reproduces the sampled motion (Bezier split by de Casteljau, ease refit for rotations), cropping a
/// track to a range, and re-timing keys with the collision rule. Kept in their own class so the
/// partial <see cref="ClipEdit"/> files written in parallel cannot clash on private names.
/// </summary>
internal static class ClipEditTracks
{
    // ── Selection helpers ──────────────────────────────────────────────────────────────────────

    /// <summary>Throws a plain-language <see cref="ArgumentException"/> when the selection names a key the clip lacks.</summary>
    public static void CheckSelection(RfaClip clip, KeySelection selection)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(selection);
        foreach (var k in selection.Keys)
        {
            if (!k.IsValidIn(clip))
                throw new ArgumentException(
                    $"The selection refers to {k.Kind.ToString().ToLowerInvariant()} key {k.Index} of bone {k.Bone}, which this clip does not have. " +
                    "Rebuild the selection for this clip (KeySelection.Validate drops stale keys).", nameof(selection));
        }
    }

    /// <summary>One flag per key of a track: true when the key is selected.</summary>
    public static bool[] Flags(KeySelection selection, int bone, KeyKind kind, int count)
    {
        var flags = new bool[count];
        foreach (int i in selection.IndicesOf(bone, kind))
        {
            if ((uint)i < (uint)count) flags[i] = true;
        }
        return flags;
    }

    /// <summary>Index of the first key whose time is at or after <paramref name="time"/> (the key count when none is).</summary>
    public static int LowerBound(ImmutableArray<RfaRotKey> keys, int time)
    {
        int i = 0;
        while (i < keys.Length && keys[i].Time < time) i++;
        return i;
    }

    /// <inheritdoc cref="LowerBound(ImmutableArray{RfaRotKey}, int)"/>
    public static int LowerBound(ImmutableArray<RfaPosKey> keys, int time)
    {
        int i = 0;
        while (i < keys.Length && keys[i].Time < time) i++;
        return i;
    }

    /// <summary>Converts a computed time to a stored one, with a plain-language error when it does not fit.</summary>
    public static int ToTime(double t, string what)
    {
        double r = Math.Round(t, MidpointRounding.AwayFromZero);
        if (!(r >= int.MinValue && r <= int.MaxValue))
            throw new ArgumentOutOfRangeException(what, $"The edit would move a time to {t:0}, outside what a key time can store (a 32-bit tick count). Use a smaller offset or factor.");
        return (int)r;
    }

    // ── Bezier ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// de Casteljau split of the cubic <c>(p0, p1, p2, p3)</c> at <paramref name="u"/>: the left half
    /// is <c>(p0, l1, l2, mid)</c>, the right half <c>(mid, r1, r2, p3)</c>, each reproducing its part
    /// of the original curve exactly (reparametrised linearly).
    /// </summary>
    public static void SplitBezier(
        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float u,
        out Vector3 l1, out Vector3 l2, out Vector3 mid, out Vector3 r1, out Vector3 r2)
    {
        var a = Vector3.Lerp(p0, p1, u);
        var b = Vector3.Lerp(p1, p2, u);
        var c = Vector3.Lerp(p2, p3, u);
        var d = Vector3.Lerp(a, b, u);
        var e = Vector3.Lerp(b, c, u);
        l1 = a;
        l2 = d;
        mid = Vector3.Lerp(d, e, u);
        r1 = e;
        r2 = c;
    }

    /// <summary>
    /// The exact control points of the original position curve between <paramref name="ta"/> and
    /// <paramref name="tb"/>, when that stretch lies inside one segment (or in a clamped constant
    /// region); null when an original key lies strictly inside it (the stretch is not one cubic).
    /// </summary>
    public static (Vector3 Out, Vector3 In)? SubCurve(ImmutableArray<RfaPosKey> keys, int ta, int tb)
    {
        int n = keys.Length;
        if (n == 0) return (Vector3.Zero, Vector3.Zero);
        if (n == 1 || tb <= keys[0].Time || ta >= keys[n - 1].Time)
        {
            var p = ClipSampler.SamplePosition(keys.AsSpan(), ta);
            return (p, p);
        }
        for (int i = 1; i < n; i++)
        {
            var k0 = keys[i - 1];
            var k1 = keys[i];
            if (k0.Time <= ta && tb <= k1.Time && k1.Time > k0.Time)
            {
                float span = k1.Time - k0.Time;
                float ua = (ta - k0.Time) / span, ub = (tb - k0.Time) / span;
                // Left part [0, ub], then its right part from ua / ub.
                SplitBezier(k0.Position, k0.OutControl, k1.InControl, k1.Position, ub, out var l1, out var l2, out var m, out _, out _);
                if (ua <= 0f) return (l1, l2);
                SplitBezier(k0.Position, l1, l2, m, ua / ub, out _, out _, out _, out var r1, out var r2);
                return (r1, r2);
            }
            if (k0.Time > ta) break;
        }
        return null;
    }

    // ── Sampled key insertion ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The track with a rotation key at <paramref name="time"/> equal to the sampled rotation, so the
    /// motion is unchanged (within the int16 store). An existing key at that time is left alone.
    /// Before the first key / after the last, the new key is a raw copy of that end key (exact; eases
    /// 0). Inside a segment, the new key is the sampled value, sign-continuous with the earlier key,
    /// and the four ease bytes of the two halves are refit to reproduce the original eased timing:
    /// exact (up to the byte step) when the split falls in the segment's constant-speed part, best fit
    /// over the ease bytes otherwise (error up to 4.3% of the segment's rotation, see
    /// <see cref="ClipEdit.KeyPoseAtTime"/>); with no ease on the segment all eases stay 0 and the
    /// neighbours are untouched.
    /// </summary>
    public static ImmutableArray<RfaRotKey> InsertSampledRotation(ImmutableArray<RfaRotKey> keys, int time, out int index)
    {
        int n = keys.Length;
        if (n == 0)
        {
            index = 0;
            return [ClipEdit.QuantizeRotation(time, Quaternion.Identity, null)];
        }
        int i = LowerBound(keys, time);
        index = i;
        if (i < n && keys[i].Time == time) return keys;
        if (i == 0) return keys.Insert(0, keys[0] with { Time = time, EaseIn = 0, EaseOut = 0, Pad = 0 });
        if (i == n) return keys.Add(keys[n - 1] with { Time = time, EaseIn = 0, EaseOut = 0, Pad = 0 });

        var k0 = keys[i - 1];
        var k1 = keys[i];
        var active = ClipSampler.SampleRotation(keys.AsSpan(), time);
        float u = (time - k0.Time) / (float)(k1.Time - k0.Time);
        var (aL, bL, aR, bR) = FitSplitEases(u, k0.EaseOut, k1.EaseIn);
        var key = ClipEdit.QuantizeRotation(time, active, k0, bL, aR);
        var b = keys.ToBuilder();
        if (k0.EaseOut != aL) b[i - 1] = k0 with { EaseOut = aL };
        if (k1.EaseIn != bR) b[i] = k1 with { EaseIn = bR };
        b.Insert(i, key);
        return b.ToImmutable();
    }

    /// <summary>
    /// The track with a position key at <paramref name="time"/> on the sampled curve. An existing key
    /// at that time is left alone. Inside a segment the Bezier is split exactly by de Casteljau (the
    /// earlier key's out control and the later key's in control change; both halves reproduce the
    /// original curve). Before the first / after the last key the new key is constant and the old end
    /// key's outer control point is set to its position, so the new hold segment is constant.
    /// </summary>
    public static ImmutableArray<RfaPosKey> InsertSampledPosition(ImmutableArray<RfaPosKey> keys, int time, out int index)
    {
        int n = keys.Length;
        if (n == 0)
        {
            index = 0;
            return [RfaPosKey.Constant(time, Vector3.Zero)];
        }
        int i = LowerBound(keys, time);
        index = i;
        if (i < n && keys[i].Time == time) return keys;
        var b = keys.ToBuilder();
        if (i == 0)
        {
            var first = keys[0];
            if (first.InControl != first.Position) b[0] = first with { InControl = first.Position };
            b.Insert(0, RfaPosKey.Constant(time, first.Position));
            return b.ToImmutable();
        }
        if (i == n)
        {
            var last = keys[n - 1];
            if (last.OutControl != last.Position) b[n - 1] = last with { OutControl = last.Position };
            b.Add(RfaPosKey.Constant(time, last.Position));
            return b.ToImmutable();
        }
        var k0 = keys[i - 1];
        var k1 = keys[i];
        float u = (time - k0.Time) / (float)(k1.Time - k0.Time);
        SplitBezier(k0.Position, k0.OutControl, k1.InControl, k1.Position, u, out var l1, out var l2, out var mid, out var r1, out var r2);
        b[i - 1] = k0 with { OutControl = l1 };
        b[i] = k1 with { InControl = r2 };
        b.Insert(i, new RfaPosKey(time, mid, l2, r1));
        return b.ToImmutable();
    }

    // ── Crop ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The rotation keys inside [<paramref name="from"/>, <paramref name="to"/>], with sampled keys
    /// inserted at a boundary when keys lie beyond it (so the motion inside is unchanged). An inserted
    /// boundary key's outer ease is 0. Untouched keys inside the range stay bit-identical except the
    /// eases a split refits.
    /// </summary>
    public static ImmutableArray<RfaRotKey> CropRotation(ImmutableArray<RfaRotKey> keys, int from, int to)
    {
        if (keys.IsDefaultOrEmpty) return keys;
        bool before = keys.Any(k => k.Time < from), after = keys.Any(k => k.Time > to);
        if (!before && !after) return keys;
        if (before && !keys.Any(k => k.Time == from))
        {
            keys = InsertSampledRotation(keys, from, out int i);
            keys = keys.SetItem(i, keys[i] with { EaseIn = 0 });
        }
        if (after && !keys.Any(k => k.Time == to))
        {
            keys = InsertSampledRotation(keys, to, out int i);
            keys = keys.SetItem(i, keys[i] with { EaseOut = 0 });
        }
        return keys.Where(k => k.Time >= from && k.Time <= to).ToImmutableArray();
    }

    /// <summary>
    /// The position keys inside [<paramref name="from"/>, <paramref name="to"/>], with exact
    /// (de Casteljau) boundary keys inserted where keys lie beyond a boundary. An inserted boundary
    /// key's outer control point equals its position. A track that had at least two keys keeps at
    /// least two (a constant key is added at the other boundary when needed and the range has length).
    /// </summary>
    public static ImmutableArray<RfaPosKey> CropPosition(ImmutableArray<RfaPosKey> keys, int from, int to)
    {
        if (keys.IsDefaultOrEmpty) return keys;
        int originalCount = keys.Length;
        bool before = keys.Any(k => k.Time < from), after = keys.Any(k => k.Time > to);
        if (!before && !after) return keys;
        if (before && !keys.Any(k => k.Time == from))
        {
            keys = InsertSampledPosition(keys, from, out int i);
            keys = keys.SetItem(i, keys[i] with { InControl = keys[i].Position });
        }
        if (after && !keys.Any(k => k.Time == to))
        {
            keys = InsertSampledPosition(keys, to, out int i);
            keys = keys.SetItem(i, keys[i] with { OutControl = keys[i].Position });
        }
        var kept = keys.Where(k => k.Time >= from && k.Time <= to).ToImmutableArray();
        if (kept.Length == 1 && originalCount >= 2 && to > from)
        {
            var only = kept[0] with { InControl = kept[0].Position, OutControl = kept[0].Position };
            kept = only.Time < to
                ? [only, RfaPosKey.Constant(to, only.Position)]
                : [RfaPosKey.Constant(from, only.Position), only];
        }
        return kept;
    }

    // ── Re-timing with the collision rule ───────────────────────────────────────────────────────

    /// <summary>
    /// Re-times the selected keys of one track and resolves collisions (the rule documented on
    /// <see cref="ClipEdit.MoveKeys(RfaClip, KeySelection, int)"/>):
    /// <list type="number">
    /// <item>selected keys take <paramref name="newTime"/>; unselected keys keep their time;</item>
    /// <item>two selected keys on the same new time: the one later in the original track wins;</item>
    /// <item>an unselected key is removed (merged into the moved block) when a selected key lands on
    /// its time or when keeping it would change its order relative to any selected key, so moved keys
    /// never pass over unselected ones;</item>
    /// <item>the survivors are ordered by time.</item>
    /// </list>
    /// </summary>
    /// <param name="keys">The track.</param>
    /// <param name="selected">One flag per key.</param>
    /// <param name="newTime">New time of a selected key from its original time.</param>
    /// <param name="getTime">Reads a key's time.</param>
    /// <param name="transform">Builds the moved key from the original and its new time.</param>
    /// <param name="resultSelected">Indices in the result of the surviving selected keys.</param>
    /// <param name="moved">One flag per result key: true for keys that were re-timed.</param>
    public static ImmutableArray<TKey> Rearrange<TKey>(
        ImmutableArray<TKey> keys, bool[] selected, Func<int, int> newTime, Func<TKey, int> getTime,
        Func<TKey, int, TKey> transform, out List<int> resultSelected, out bool[] moved)
    {
        int n = keys.Length;
        var times = new int[n];
        for (int i = 0; i < n; i++) times[i] = selected[i] ? newTime(getTime(keys[i])) : getTime(keys[i]);

        // Selected-selected collisions: the later original key wins.
        var winner = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            if (selected[i]) winner[times[i]] = i;
        }

        // Prefix max / suffix min of the selected keys' new times, by original index.
        var maxBefore = new long[n];
        var minAfter = new long[n];
        long run = long.MinValue;
        for (int i = 0; i < n; i++)
        {
            maxBefore[i] = run;
            if (selected[i]) run = Math.Max(run, times[i]);
        }
        run = long.MaxValue;
        for (int i = n - 1; i >= 0; i--)
        {
            minAfter[i] = run;
            if (selected[i]) run = Math.Min(run, times[i]);
        }

        var survivors = new List<int>(n);
        for (int i = 0; i < n; i++)
        {
            if (selected[i])
            {
                if (winner[times[i]] == i) survivors.Add(i);
            }
            else if (maxBefore[i] < times[i] && minAfter[i] > times[i] && !winner.ContainsKey(times[i]))
            {
                survivors.Add(i);
            }
        }
        survivors.Sort((a, b) => times[a] != times[b] ? times[a].CompareTo(times[b]) : a.CompareTo(b));

        var result = ImmutableArray.CreateBuilder<TKey>(survivors.Count);
        resultSelected = [];
        moved = new bool[survivors.Count];
        for (int r = 0; r < survivors.Count; r++)
        {
            int i = survivors[r];
            if (selected[i])
            {
                result.Add(transform(keys[i], times[i]));
                resultSelected.Add(r);
                moved[r] = true;
            }
            else
            {
                result.Add(keys[i]);
            }
        }
        return result.MoveToImmutable();
    }

    // ── Ease refit ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Ease bytes for the two halves of an eased rotation segment split at linear parameter
    /// <paramref name="u"/>: (earlier key's ease-out, new key's ease-in) for the left half and (new
    /// key's ease-out, later key's ease-in) for the right half, each chosen so the half's eased
    /// parameter matches the original's (renormalised to the half). Slerp along one great arc is
    /// uniform, so matching the parameter matches the rotation. No ease gives all zeros.
    /// </summary>
    public static (sbyte LeftOut, sbyte LeftIn, sbyte RightOut, sbyte RightIn) FitSplitEases(float u, sbyte easeOut, sbyte easeIn)
    {
        if (easeOut == 0 && easeIn == 0) return (0, 0, 0, 0);
        float a = easeOut / RfaClip.EaseScale, b = easeIn / RfaClip.EaseScale;
        float s = ClipSampler.Ease(u, a, b);
        (sbyte, sbyte) left = (0, 0), right = (0, 0);
        if (s > 1e-6f) left = FitEase(v => ClipSampler.Ease(v * u, a, b) / s);
        if (s < 1f - 1e-6f) right = FitEase(v => (ClipSampler.Ease(u + v * (1f - u), a, b) - s) / (1f - s));
        return (left.Item1, left.Item2, right.Item1, right.Item2);
    }

    /// <summary>
    /// The ease bytes (0..127 each) whose ease curve is closest to <paramref name="target"/> on (0, 1)
    /// in the max norm: a coarse grid search, then a fine search around the best coarse pair.
    /// </summary>
    public static (sbyte EaseOut, sbyte EaseIn) FitEase(Func<float, float> target)
    {
        const int samples = 31;
        var v = new float[samples];
        var g = new float[samples];
        for (int j = 0; j < samples; j++)
        {
            v[j] = (j + 1) / (float)(samples + 1);
            g[j] = target(v[j]);
        }

        int bestA = 0, bestB = 0;
        float best = Error(0, 0);
        for (int a = 0; a <= 128; a += 4)
        {
            for (int b = 0; b <= 128; b += 4)
            {
                int ca = Math.Min(a, 127), cb = Math.Min(b, 127);
                float e = Error(ca, cb);
                if (e < best) (best, bestA, bestB) = (e, ca, cb);
            }
        }
        int a0 = bestA, b0 = bestB;
        for (int a = Math.Max(0, a0 - 4); a <= Math.Min(127, a0 + 4); a++)
        {
            for (int b = Math.Max(0, b0 - 4); b <= Math.Min(127, b0 + 4); b++)
            {
                float e = Error(a, b);
                if (e < best) (best, bestA, bestB) = (e, a, b);
            }
        }
        return ((sbyte)bestA, (sbyte)bestB);

        float Error(int ea, int eb)
        {
            float fa = ea / RfaClip.EaseScale, fb = eb / RfaClip.EaseScale, max = 0f;
            for (int j = 0; j < samples; j++) max = MathF.Max(max, MathF.Abs(ClipSampler.Ease(v[j], fa, fb) - g[j]));
            return max;
        }
    }

    // ── Rotation key utilities ──────────────────────────────────────────────────────────────────

    /// <summary>The integer dot product of two stored quaternions (sign is exact).</summary>
    public static long StoredDot(RfaRotKey a, RfaRotKey b) =>
        (long)a.X * b.X + (long)a.Y * b.Y + (long)a.Z * b.Z + (long)a.W * b.W;

    /// <summary>The key with its stored components negated exactly (short.MinValue becomes short.MaxValue).</summary>
    public static RfaRotKey Negated(RfaRotKey k) => k with { X = Neg(k.X), Y = Neg(k.Y), Z = Neg(k.Z), W = Neg(k.W) };

    private static short Neg(short c) => c == short.MinValue ? short.MaxValue : (short)-c;

    /// <summary>A rotation key for <paramref name="active"/> at index <paramref name="i"/> of the track, sign-aligned with the key before it (or after it when it is first).</summary>
    public static RfaRotKey QuantizeAt(IReadOnlyList<RfaRotKey> track, int i, int time, Quaternion active, sbyte easeIn, sbyte easeOut)
    {
        RfaRotKey? reference = i > 0 ? track[i - 1] : track.Count > 1 ? track[1] : null;
        return ClipEdit.QuantizeRotation(time, active, reference, easeIn, easeOut);
    }

    /// <summary>Linear auto control points of key <paramref name="i"/>: a third of the way to each neighbour (the key itself at the ends).</summary>
    public static (Vector3 In, Vector3 Out) LinearControls(IReadOnlyList<RfaPosKey> keys, int i)
    {
        var p = keys[i].Position;
        var cin = i > 0 ? p + (keys[i - 1].Position - p) / 3f : p;
        var cout = i < keys.Count - 1 ? p + (keys[i + 1].Position - p) / 3f : p;
        return (cin, cout);
    }

    /// <summary>
    /// The angle between two rotations in degrees, ignoring sign, accurate for tiny angles:
    /// <c>4 asin(|a - b| / 2)</c> on the normalised, hemisphere-aligned quaternions, in double.
    /// (<see cref="Quat.AngleDegrees"/> takes the acos of a float dot product, which cannot resolve
    /// angles below about 0.05 degrees: identical rotations can report 0.07.)
    /// </summary>
    public static float AngleDegrees(Quaternion a, Quaternion b)
    {
        a = Quat.Normalize(a);
        b = Quat.Align(Quat.Normalize(b), a);
        double dx = (double)a.X - b.X, dy = (double)a.Y - b.Y, dz = (double)a.Z - b.Z, dw = (double)a.W - b.W;
        double chord = Math.Sqrt(dx * dx + dy * dy + dz * dz + dw * dw);
        return (float)(4.0 * Math.Asin(Math.Min(1.0, chord / 2.0)) * (180.0 / Math.PI));
    }
}
