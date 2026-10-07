using System.Numerics;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>One clip playing on a character: the clip, its current time, whether it is a state, and its mix.</summary>
/// <param name="Clip">The clip.</param>
/// <param name="Time">Its current time in ticks (the clip's own timeline, start..end).</param>
/// <param name="IsState">
/// True for a STATE (looping base: weights used as-is, then scaled down by the primary action); false
/// for an ACTION (weights ramp in and out and are zero outside the clip).
/// </param>
/// <param name="Mix">
/// The instance's blend percentage for this clip (the <c>percent</c> argument of
/// <c>CharacterInstance::set_state / play_action</c>; 1 for a clip playing normally). A layer with a mix
/// of exactly 0 contributes nothing.
/// </param>
public readonly record struct ClipLayer(RfaClip Clip, float Time, bool IsState, float Mix = 1f);

/// <summary>
/// Layers clips exactly as the engine's pose build does (<c>transform_skeleton_iterative</c>,
/// 0x0051B500, with <c>Skeleton::find_animation_weight</c> 0x00539E10 and
/// <c>super_combine_matrix_new</c> 0x0051B110, all read from RF.exe's disassembly):
/// <list type="number">
/// <item>The PRIMARY action (the first action played, or the one the caller names) gives a state
/// factor per bone: <c>(10 - w) / 10</c>, w being that action's ramped weight on the bone (0 when it
/// has no mix). Bone weights therefore run 0..10: an action bone of weight 10 completely replaces the
/// states on that bone, weight 5 shares it half and half.</item>
/// <item>Each layer with a non-zero mix contributes with weight <c>BoneWeight * Mix</c>, times the state
/// factor for a state. Contributions of 0 or less are skipped.</item>
/// <item>The weights are normalised to sum to 1. Positions are the weighted sum. Rotations are a running
/// slerp: starting from the first contributor, each next one is slerped in with
/// <c>t = w_i / (w_0 + ... + w_i)</c> (<see cref="EngineSlerp"/>).</item>
/// <item>A bone no layer contributes to gets the identity rotation at the parent's origin in the engine;
/// callers may pass a fallback instead (the rest locals, to show a missing clip without collapsing the
/// skeleton).</item>
/// </list>
/// The engine's times are integers and a state's time is <c>start + floor(duration * phase)</c> from a
/// phase shared by every state; callers supply times directly here.
/// </summary>
public static class ClipBlender
{
    /// <summary>A bone whose stored weight is below this (0x00589CB8) is ignored by the clip.</summary>
    public const float WeightEpsilon = 1e-5f;

    /// <summary>The weight at which an action fully replaces the states on a bone (0x0058957C).</summary>
    public const float FullWeight = 10f;

    /// <summary>
    /// The action envelope at <paramref name="time"/> as the engine computes it, with
    /// <c>dt = time - start</c> and <c>d = end - start</c>: 0 when <c>dt &lt; 0</c>; <c>dt / ramp_in</c>
    /// when <c>dt &lt; ramp_in</c> (ramp-in wins where the ramps overlap, and is tested before the end);
    /// 0 when <c>dt &gt; d</c>; <c>(d - dt) / ramp_out</c> when <c>dt &gt; d - ramp_out</c>; otherwise 1.
    /// </summary>
    public static float RampFactor(RfaClip clip, float time)
    {
        ArgumentNullException.ThrowIfNull(clip);
        float dt = time - clip.StartTime;
        float duration = clip.EndTime - clip.StartTime;
        if (dt < 0f) return 0f;
        if (dt < clip.RampIn) return dt / clip.RampIn;
        if (dt > duration) return 0f;
        if (dt > duration - clip.RampOut) return (duration - dt) / clip.RampOut;
        return 1f;
    }

    /// <summary>
    /// A bone's weight in a clip (<c>Skeleton::find_animation_weight</c>): 0 for a bone the clip does
    /// not have or whose stored weight is below <see cref="WeightEpsilon"/>; the stored weight for a
    /// state; for an action the stored weight times <see cref="RampFactor"/>, never more than the stored
    /// weight.
    /// </summary>
    public static float BoneWeight(RfaClip clip, int bone, float time, bool isState)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if ((uint)bone >= (uint)clip.BoneCount) return 0f;
        float w = clip.Bones[bone].Weight;
        if (!(w >= WeightEpsilon)) return 0f;
        if (isState) return w;
        float r = RampFactor(clip, time) * w;
        return r <= w ? r : w;
    }

    /// <summary>
    /// The factor the primary action applies to every state's weight on <paramref name="bone"/>:
    /// <c>(10 - w) / 10</c> with w the action's <see cref="BoneWeight"/>, or 1 without an action (or
    /// when its mix is 0). It can go negative for weights above 10, which removes the states.
    /// </summary>
    public static float StateFactor(ClipLayer? primaryAction, int bone)
    {
        if (primaryAction is not { } a || a.Clip is null || a.Mix == 0f) return 1f;
        return (FullWeight - BoneWeight(a.Clip, bone, a.Time, isState: false)) * 0.1f;
    }

    /// <summary>
    /// The engine's float quaternion slerp (<c>slerp</c>, 0x00519DA0): t wrapped into [0, 1]; the second
    /// quaternion negated when <c>|a + b|^2 &lt;= |a - b|^2</c>; when <c>1 - dot &lt;= 1e-6</c> the result is
    /// the (possibly negated) second quaternion; otherwise the standard slerp weights. No
    /// normalisation. Works the same on file- or active-convention quaternions.
    /// </summary>
    public static Quaternion EngineSlerp(Quaternion a, Quaternion b, float t)
    {
        if (t < 0f)
        {
            do t += 1f; while (t < 0f);
        }
        if (t > 1f)
        {
            do t -= 1f; while (t > 1f);
        }
        var d = a - b;
        var s = a + b;
        if (Quat.Dot(s, s) <= Quat.Dot(d, d)) b = Quat.Negate(b);
        float dot = Quat.Dot(a, b);
        if (float.IsNaN(dot)) return a;
        double s0, s1;
        if (dot + 1f <= ClipSampler.NearParallel)
        {
            // Unreachable after the flip; ported for completeness.
            s0 = Math.Sin((1.0 - t) * (Math.PI / 2));
            s1 = Math.Sin(t * (Math.PI / 2));
            return new Quaternion(
                (float)(a.X * s0 - b.Y * s1), (float)(a.Y * s0 + b.X * s1),
                (float)(a.Z * s0 - b.W * s1), (float)(a.W * s0 + b.Z * s1));
        }
        if (1f - dot <= ClipSampler.NearParallel)
        {
            return b;
        }
        double omega = Math.Acos(dot);
        double sin = Math.Sin(omega);
        s0 = Math.Sin((1.0 - t) * omega) / sin;
        s1 = Math.Sin(t * omega) / sin;
        return new Quaternion(
            (float)(a.X * s0 + b.X * s1), (float)(a.Y * s0 + b.Y * s1),
            (float)(a.Z * s0 + b.Z * s1), (float)(a.W * s0 + b.W * s1));
    }

    /// <summary>
    /// Blends <paramref name="layers"/> into <paramref name="locals"/> as the engine does (see the type
    /// summary). <paramref name="primaryAction"/> is the index in <paramref name="layers"/> of the action
    /// whose weights scale the states; -1 (default) picks the first action layer with a non-zero mix,
    /// which is the engine's choice until a stronger action takes over at its reference bone. A bone no
    /// layer drives takes <paramref name="fallback"/>'s entry, or the identity (what the engine does).
    /// Allocation-free for up to 16 layers (the engine's limit).
    /// </summary>
    public static void Blend(
        ReadOnlySpan<ClipLayer> layers, Span<Rigid> locals, ReadOnlySpan<Rigid> fallback = default, int primaryAction = -1)
    {
        if (primaryAction < 0)
        {
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].Clip is not null && !layers[i].IsState && layers[i].Mix != 0f)
                {
                    primaryAction = i;
                    break;
                }
            }
        }
        ClipLayer? primary = primaryAction >= 0 && primaryAction < layers.Length && !layers[primaryAction].IsState
            ? layers[primaryAction] : null;

        for (int bone = 0; bone < locals.Length; bone++)
        {
            float stateFactor = StateFactor(primary, bone);
            float total = 0f;
            var rot = Quaternion.Identity;
            var pos = Vector3.Zero;
            int count = 0;
            foreach (var layer in layers)
            {
                if (layer.Clip is null || layer.Mix == 0f) continue;
                float w = BoneWeight(layer.Clip, bone, layer.Time, layer.IsState) * layer.Mix;
                if (layer.IsState) w *= stateFactor;
                if (!(w > 0f)) continue;
                var local = ClipSampler.SampleBone(layer.Clip.Bones[bone], layer.Time);
                total += w;
                // The running slerp: t = w_i / (sum so far). The positions are re-weighted at the end.
                rot = count == 0 ? local.Rotation : EngineSlerp(rot, local.Rotation, w / total);
                pos += local.Position * w;
                count++;
            }
            locals[bone] = count > 0
                ? new Rigid(Quat.Normalize(rot), pos / total)
                : bone < fallback.Length ? fallback[bone] : Rigid.Identity;
        }
    }

    /// <summary>Convenience: a state with an optional action over it (the action is the primary). Allocation-free.</summary>
    public static void Blend(
        RfaClip state, float stateTime, RfaClip? action, float actionTime, Span<Rigid> locals, ReadOnlySpan<Rigid> fallback = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        var pair = new LayerPair();
        pair[0] = new ClipLayer(state, stateTime, true);
        int count = 1;
        if (action is not null) pair[count++] = new ClipLayer(action, actionTime, false);
        Blend(((ReadOnlySpan<ClipLayer>)pair)[..count], locals, fallback);
    }

    [System.Runtime.CompilerServices.InlineArray(2)]
    private struct LayerPair
    {
        private ClipLayer _element;
    }
}
