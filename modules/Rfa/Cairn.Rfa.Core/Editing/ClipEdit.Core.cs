using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>
/// Pure clip edits: every operation takes an <see cref="RfaClip"/> and returns a new one, leaving the
/// input untouched. The contract every operation keeps (DESIGN.md section 4):
/// <list type="bullet">
/// <item>keys the operation does not change come back bit-identical (raw int16 components, ease bytes,
/// pad, float positions and control points);</item>
/// <item>a rotation key that does change is re-quantised once (<c>round(c * 16383)</c> of the FILE
/// quaternion) and made sign-continuous with the key before it (<see cref="QuantizeRotation"/>);</item>
/// <item>quaternions passed in and out are in the ACTIVE convention of <see cref="Quat"/>; the
/// conjugation to the file convention happens here, never in the caller.</item>
/// </list>
/// This file holds the shared helpers; the operations live in the other <c>ClipEdit.*.cs</c> files.
/// </summary>
public static partial class ClipEdit
{
    /// <summary>A key's rotation in the active convention (normalised).</summary>
    public static Quaternion KeyRotation(RfaRotKey key) => ClipSampler.KeyRotation(key);

    /// <summary>
    /// Quantises an ACTIVE-convention rotation into a stored key: the file quaternion is
    /// <c>conj(normalize(active))</c>, negated if that puts it in the other hemisphere from
    /// <paramref name="previous"/> (sign continuity), then rounded to int16 by
    /// <see cref="RfaRotKey.QuantizeWithinUnit"/> (never longer than 1, so the engine's slerp does not
    /// snap the segment). The pad word is 0.
    /// </summary>
    /// <param name="time">Key time in ticks.</param>
    /// <param name="active">The rotation (active convention; normalised here).</param>
    /// <param name="previous">The key before this one in the track, if any.</param>
    /// <param name="easeIn">Ease-in byte.</param>
    /// <param name="easeOut">Ease-out byte.</param>
    public static RfaRotKey QuantizeRotation(int time, Quaternion active, RfaRotKey? previous, sbyte easeIn = 0, sbyte easeOut = 0)
    {
        var file = Quat.Conj(Quat.Normalize(active));
        if (previous is { } p && Quat.Dot(file, p.FileQuaternion) < 0f) file = Quat.Negate(file);
        return RfaRotKey.QuantizeWithinUnit(time, file, easeIn, easeOut);
    }

    /// <summary>
    /// <paramref name="key"/> with its stored components negated (the same rotation) when it is in the
    /// other hemisphere from <paramref name="previous"/>; otherwise the key itself, untouched. Use it
    /// only on keys an edit has already changed: untouched keys are never re-signed.
    /// </summary>
    public static RfaRotKey AlignSign(RfaRotKey key, RfaRotKey previous)
    {
        if (Quat.Dot(key.FileQuaternion, previous.FileQuaternion) >= 0f) return key;
        return key with { X = Neg(key.X), Y = Neg(key.Y), Z = Neg(key.Z), W = Neg(key.W) };

        static short Neg(short c) => c == short.MinValue ? short.MaxValue : (short)-c;
    }

    /// <summary>
    /// Re-signs the keys flagged in <paramref name="changed"/> so each is sign-continuous with the key
    /// before it. Unflagged keys are returned exactly as they were.
    /// </summary>
    public static ImmutableArray<RfaRotKey> AlignChangedSigns(ImmutableArray<RfaRotKey> keys, IReadOnlyList<bool> changed)
    {
        ArgumentNullException.ThrowIfNull(changed);
        if (keys.IsDefaultOrEmpty) return keys;
        var b = keys.ToBuilder();
        for (int i = 1; i < b.Count; i++)
        {
            if (i < changed.Count && changed[i]) b[i] = AlignSign(b[i], b[i - 1]);
        }
        return b.MoveToImmutable();
    }

    /// <summary>The clip with one bone's track replaced.</summary>
    public static RfaClip WithTrack(RfaClip clip, int bone, RfaBoneTrack track)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        CheckBone(clip, bone);
        return clip with { Bones = clip.Bones.SetItem(bone, track) };
    }

    /// <summary>The clip with one bone's rotation keys replaced (weight and position keys kept).</summary>
    public static RfaClip WithRotationKeys(RfaClip clip, int bone, ImmutableArray<RfaRotKey> keys)
    {
        CheckBone(clip, bone);
        return WithTrack(clip, bone, clip.Bones[bone] with { RotationKeys = keys });
    }

    /// <summary>The clip with one bone's position keys replaced (weight and rotation keys kept).</summary>
    public static RfaClip WithPositionKeys(RfaClip clip, int bone, ImmutableArray<RfaPosKey> keys)
    {
        CheckBone(clip, bone);
        return WithTrack(clip, bone, clip.Bones[bone] with { PositionKeys = keys });
    }

    /// <summary>A bone's active-convention local rotation at <paramref name="time"/>, as the engine samples it.</summary>
    public static Quaternion SampleRotation(RfaClip clip, int bone, float time)
    {
        CheckBone(clip, bone);
        return ClipSampler.SampleRotation(clip.Bones[bone].RotationKeys.AsSpan(), time);
    }

    /// <summary>A bone's local position at <paramref name="time"/>, as the engine samples it.</summary>
    public static Vector3 SamplePosition(RfaClip clip, int bone, float time)
    {
        CheckBone(clip, bone);
        return ClipSampler.SamplePosition(clip.Bones[bone].PositionKeys.AsSpan(), time);
    }

    /// <summary>Throws a plain-language <see cref="ArgumentOutOfRangeException"/> for a bone index the clip lacks.</summary>
    public static void CheckBone(RfaClip clip, int bone)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if ((uint)bone >= (uint)clip.BoneCount)
            throw new ArgumentOutOfRangeException(nameof(bone), $"The clip has {clip.BoneCount} bones; there is no bone {bone}.");
    }
}
