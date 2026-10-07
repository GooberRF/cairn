using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>
/// A mutable working copy of one key list plus a "changed" flag per key, used by the pose, loop,
/// mirror, root and skeleton edits to insert keys without disturbing the curve and to keep the
/// untouched keys bit-identical. Internal: the public surface is <see cref="ClipEdit"/>.
/// </summary>
internal sealed class RotTrackWork
{
    public RotTrackWork(ImmutableArray<RfaRotKey> keys)
    {
        Keys = keys.IsDefault ? [] : [.. keys];
        Changed = [.. new bool[Keys.Count]];
    }

    public List<RfaRotKey> Keys { get; }

    public List<bool> Changed { get; }

    public int Count => Keys.Count;

    /// <summary>The index of the key at exactly <paramref name="time"/>, or -1.</summary>
    public int IndexAt(int time)
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            if (Keys[i].Time == time) return i;
        }
        return -1;
    }

    /// <summary>The track's active rotation at <paramref name="time"/> as the engine samples it.</summary>
    public Quaternion Sample(float time) => ClipSampler.SampleRotation(CollectionsMarshal.AsSpan(Keys), time);

    /// <summary>
    /// Ensures a key exists at <paramref name="time"/>, inserting one with the sampled value (eases 0)
    /// when there is none. Before the first or after the last key the neighbour's raw components are
    /// copied exactly; an empty track gets an identity key. Returns the key's index.
    /// </summary>
    public int Insert(int time)
    {
        int at = IndexAt(time);
        if (at >= 0) return at;
        int i = 0;
        while (i < Keys.Count && Keys[i].Time < time) i++;
        RfaRotKey key;
        if (Keys.Count == 0)
        {
            key = ClipEdit.QuantizeRotation(time, Quaternion.Identity, null);
        }
        else if (i == 0 || i == Keys.Count)
        {
            var n = Keys[i == 0 ? 0 : Keys.Count - 1];
            key = new RfaRotKey(time, n.X, n.Y, n.Z, n.W, 0, 0, n.Pad);
        }
        else
        {
            key = ClipEdit.QuantizeRotation(time, Sample(time), Keys[i - 1]);
        }
        Keys.Insert(i, key);
        Changed.Insert(i, true);
        return i;
    }

    /// <summary>Sets key <paramref name="index"/> and flags it changed (unless it is bit-identical).</summary>
    public void Set(int index, RfaRotKey key)
    {
        if (Keys[index] == key) return;
        Keys[index] = key;
        Changed[index] = true;
    }

    /// <summary>
    /// Quantises an active rotation into key <paramref name="index"/>, keeping its time, eases and pad,
    /// sign-continuous with the key before it.
    /// </summary>
    public void SetRotation(int index, Quaternion active)
    {
        var old = Keys[index];
        var key = ClipEdit.QuantizeRotation(old.Time, active, index > 0 ? Keys[index - 1] : null, old.EaseIn, old.EaseOut)
            with { Pad = old.Pad };
        Set(index, key);
    }

    /// <summary>
    /// The finished key list. Sign continuity is restored where an edit could have broken it: a key
    /// is negated (exact integer negation, the same rotation) when it is in the other hemisphere from
    /// its predecessor and either it changed or its predecessor did. A changed first key with an
    /// untouched successor is aligned with that successor. Discontinuities between two untouched keys
    /// are left exactly as they were.
    /// </summary>
    public ImmutableArray<RfaRotKey> ToImmutable()
    {
        int n = Keys.Count;
        if (n >= 2 && Changed[0] && !Changed[1]) Keys[0] = ClipEdit.AlignSign(Keys[0], Keys[1]);
        bool prevTouched = n > 0 && Changed[0];
        for (int i = 1; i < n; i++)
        {
            bool touched = Changed[i];
            if (touched || prevTouched)
            {
                var aligned = ClipEdit.AlignSign(Keys[i], Keys[i - 1]);
                if (aligned != Keys[i])
                {
                    Keys[i] = aligned;
                    touched = true;
                }
            }
            prevTouched = touched;
        }
        return [.. Keys];
    }
}

/// <summary>The position-key counterpart of <see cref="RotTrackWork"/>.</summary>
internal sealed class PosTrackWork
{
    public PosTrackWork(ImmutableArray<RfaPosKey> keys) => Keys = keys.IsDefault ? [] : [.. keys];

    public List<RfaPosKey> Keys { get; }

    public int Count => Keys.Count;

    /// <summary>The index of the key at exactly <paramref name="time"/>, or -1.</summary>
    public int IndexAt(int time)
    {
        for (int i = 0; i < Keys.Count; i++)
        {
            if (Keys[i].Time == time) return i;
        }
        return -1;
    }

    /// <summary>The track's position at <paramref name="time"/> as the engine samples it.</summary>
    public Vector3 Sample(float time) => ClipSampler.SamplePosition(CollectionsMarshal.AsSpan(Keys), time);

    /// <summary>
    /// Ensures a key exists at <paramref name="time"/> without changing the curve. Inside a segment
    /// the Bezier is split exactly (de Casteljau at the segment's linear parameter), which moves the
    /// two neighbours' facing control points. Before the first / after the last key a constant key is
    /// added and the neighbour's facing control point is set to its position so the hold stays flat
    /// (that control point was unused before). An empty track gets a key at the origin.
    /// </summary>
    public int Insert(int time)
    {
        int at = IndexAt(time);
        if (at >= 0) return at;
        int i = 0;
        while (i < Keys.Count && Keys[i].Time < time) i++;
        if (Keys.Count == 0)
        {
            Keys.Add(RfaPosKey.Constant(time, Vector3.Zero));
            return 0;
        }
        if (i == 0)
        {
            var first = Keys[0];
            Keys[0] = first with { InControl = first.Position };
            Keys.Insert(0, RfaPosKey.Constant(time, first.Position));
            return 0;
        }
        if (i == Keys.Count)
        {
            var last = Keys[^1];
            Keys[^1] = last with { OutControl = last.Position };
            Keys.Add(RfaPosKey.Constant(time, last.Position));
            return Keys.Count - 1;
        }

        var k0 = Keys[i - 1];
        var k1 = Keys[i];
        float t = k1.Time > k0.Time ? (time - k0.Time) / (float)(k1.Time - k0.Time) : 0f;
        var p01 = Vector3.Lerp(k0.Position, k0.OutControl, t);
        var p12 = Vector3.Lerp(k0.OutControl, k1.InControl, t);
        var p23 = Vector3.Lerp(k1.InControl, k1.Position, t);
        var p012 = Vector3.Lerp(p01, p12, t);
        var p123 = Vector3.Lerp(p12, p23, t);
        var p = Vector3.Lerp(p012, p123, t);
        Keys[i - 1] = k0 with { OutControl = p01 };
        Keys[i] = k1 with { InControl = p23 };
        Keys.Insert(i, new RfaPosKey(time, p, p012, p123));
        return i;
    }

    /// <summary>Moves key <paramref name="index"/> and both its control points by <paramref name="delta"/>.</summary>
    public void Translate(int index, Vector3 delta)
    {
        if (delta == Vector3.Zero) return;
        var k = Keys[index];
        Keys[index] = new RfaPosKey(k.Time, k.Position + delta, k.InControl + delta, k.OutControl + delta);
    }

    public ImmutableArray<RfaPosKey> ToImmutable() => [.. Keys];
}

/// <summary>Small shared helpers for the edits in this folder.</summary>
internal static class PoseEditMath
{
    /// <summary><c>x * x * (3 - 2x)</c> on [0, 1], clamped.</summary>
    public static float SmoothStep(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>A fraction <paramref name="w"/> of rotation <paramref name="r"/> (slerp from the identity).</summary>
    public static Quaternion Partial(Quaternion r, float w)
    {
        if (w >= 1f) return Quat.Normalize(r);
        if (w <= 0f) return Quaternion.Identity;
        return Quat.Slerp(Quaternion.Identity, r, w);
    }

    /// <summary>Each bone's model-space rotation (rotation-only forward kinematics) at <paramref name="time"/>.</summary>
    public static void WorldRotations(RfaClip clip, Skeleton skeleton, float time, Span<Quaternion> world)
    {
        var parents = skeleton.EffectiveParents;
        foreach (int i in skeleton.EvaluationOrder)
        {
            var local = i < clip.BoneCount
                ? ClipSampler.SampleRotation(clip.Bones[i].RotationKeys.AsSpan(), time)
                : skeleton.RestLocal[i].Rotation;
            int p = parents[i];
            world[i] = p < 0 ? local : Quat.Normalize(Quat.Mul(world[p], local));
        }
    }

    /// <summary>The model-space rotation of <paramref name="bone"/>'s parent at <paramref name="time"/> (identity for a root).</summary>
    public static Quaternion ParentWorldRotation(RfaClip clip, Skeleton skeleton, int bone, float time)
    {
        var result = Quaternion.Identity;
        int p = skeleton.EffectiveParents[bone];
        int guard = 0;
        while (p >= 0 && guard++ <= skeleton.Count)
        {
            var local = p < clip.BoneCount
                ? ClipSampler.SampleRotation(clip.Bones[p].RotationKeys.AsSpan(), time)
                : skeleton.RestLocal[p].Rotation;
            result = Quat.Mul(local, result);
            p = skeleton.EffectiveParents[p];
        }
        return Quat.Normalize(result);
    }

    /// <summary>Throws when a skeleton's bone count differs from a clip's (bones match by index).</summary>
    public static void CheckSkeleton(RfaClip clip, Skeleton skeleton, string paramName)
    {
        if (skeleton.Count != clip.BoneCount)
            throw new ArgumentException(
                $"The skeleton has {skeleton.Count} bones but the clip has {clip.BoneCount}; bones are matched by index, so use the mesh this clip was made for.",
                paramName);
    }

    /// <summary>The distinct, valid bone indices of <paramref name="bones"/>, or every bone when null.</summary>
    public static int[] BoneList(RfaClip clip, IEnumerable<int>? bones)
    {
        if (bones is null) return [.. Enumerable.Range(0, clip.BoneCount)];
        var list = new List<int>();
        foreach (int b in bones.Distinct())
        {
            ClipEdit.CheckBone(clip, b);
            list.Add(b);
        }
        list.Sort();
        return [.. list];
    }
}
