using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Linting;

/// <summary>
/// The quick fixes the clip rules offer. Each is a pure edit from <see cref="ClipEdit"/> applied to
/// whatever clip the UI passes in (normally the one the diagnostic was computed for).
/// </summary>
internal static class ClipFixes
{
    private static readonly NormalizeOptions None = new()
    {
        UnitQuaternions = false, SignContinuity = false, IncreasingTimes = false, ClampToRange = false,
        ZeroPad = false, FixControlPoints = false, MinimumKeys = false,
    };

    public static QuickFix SetVersion(RfaClip clip, int version) =>
        new($"Convert to version {version}", QuickFixKind.Edit, c => c.Version is 7 or 8 ? ClipEdit.ConvertVersion(c, version) : c with { Version = version });

    public static QuickFix RecomputeRange(RfaClip clip) =>
        new("Set start and end to the first and last key", QuickFixKind.Edit, ClipEdit.RecomputeRange);

    public static QuickFix ClampToRange(RfaClip clip) =>
        new("Drop the keys outside the range", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { ClampToRange = true }));

    public static QuickFix SetRamps(RfaClip clip, int rampIn, int rampOut) =>
        new($"Set the ramps to {rampIn} in, {rampOut} out", QuickFixKind.Edit,
            c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = rampIn, RampOut = rampOut }));

    public static QuickFix SetWeight(RfaClip clip, int bone, float weight) =>
        new($"Set the weight to {weight:0.##}", QuickFixKind.Edit, c => ClipEdit.SetBoneWeights(c, [bone], weight));

    public static QuickFix EnsureMinimumKeys(RfaClip clip, Skeleton? skeleton) =>
        new(skeleton is null ? "Add the missing keys" : "Add the missing keys from the rest pose", QuickFixKind.Edit,
            c => ClipEdit.EnsureMinimumKeys(c, skeleton is not null && skeleton.Count == c.BoneCount ? skeleton : null));

    public static QuickFix FixSingleRotationKeys(RfaClip clip) =>
        new("Hold the rotation with a key at the start and the end", QuickFixKind.Edit, ClipEdit.FixSingleRotationKeys);

    public static QuickFix SortKeys(RfaClip clip) =>
        new("Sort the keys and drop duplicate times", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { IncreasingTimes = true }));

    public static QuickFix UnitQuaternions(RfaClip clip) =>
        new("Normalise the quaternions", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { UnitQuaternions = true }));

    public static QuickFix SignContinuity(RfaClip clip) =>
        new("Make the keys sign-continuous", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { SignContinuity = true }));

    public static QuickFix ZeroPad(RfaClip clip) =>
        new("Clear the pad words", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { ZeroPad = true }));

    public static QuickFix ControlPoints(RfaClip clip) =>
        new("Set the control points to Auto (Linear)", QuickFixKind.Edit, c => ClipEdit.Normalize(c, None with { FixControlPoints = true }));

    public static QuickFix StripMorph(RfaClip clip) =>
        new("Strip the morph (vertex) animation", QuickFixKind.Edit, ClipEdit.StripMorph);

    /// <summary>Re-quantises every rotation key of the bone that is stored longer than 1 (exact sign and eases kept).</summary>
    public static QuickFix RequantizeOverlong(RfaClip clip, int bone) =>
        new("Re-quantise the keys within unit length", QuickFixKind.Edit, c =>
        {
            if ((uint)bone >= (uint)c.BoneCount) return c;
            var keys = c.Bones[bone].RotationKeys.ToBuilder();
            bool changed = false;
            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if ((long)k.X * k.X + (long)k.Y * k.Y + (long)k.Z * k.Z + (long)k.W * k.W <= 16383L * 16383L) continue;
                var q = Cairn.Formats.Maths.Quat.Normalize(k.FileQuaternion);
                keys[i] = RfaRotKey.QuantizeWithinUnit(k.Time, q, k.EaseIn, k.EaseOut) with { Pad = k.Pad };
                changed = true;
            }
            return changed ? c with { Bones = c.Bones.SetItem(bone, c.Bones[bone] with { RotationKeys = keys.MoveToImmutable() }) } : c;
        });

    public static QuickFix BoneLengthsFrom(RfaClip clip, RfaClip reference, IReadOnlyList<int> parents) =>
        new("Set the bone lengths from the reference clip", QuickFixKind.Edit,
            c => ClipEdit.SetBoneLengthsFromClip(c, reference, parents));
}
