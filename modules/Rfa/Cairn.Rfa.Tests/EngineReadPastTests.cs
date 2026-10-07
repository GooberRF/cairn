using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Phase 7b: shapes the engine misreads although the file is well formed. A rotation track of exactly one
/// key makes <c>Skeleton::find_animation_rotation</c> read <c>key[1]</c> past the track for any time at or
/// before the key (RFA015); morph data with no keyframes, or a version 7 morph on a clip with no length,
/// cannot be timed (RFA016). The edits that used to create lone rotation keys now hold two.
/// </summary>
public class EngineReadPastTests
{
    private static RfaClip WithLoneRotationKey()
    {
        var clip = Make();
        var track = clip.Bones[1];
        return clip with { Bones = clip.Bones.SetItem(1, track with { RotationKeys = [track.RotationKeys[1]] }) };
    }

    [Fact]
    public void ALoneRotationKeyIsAnErrorWhoseFixHoldsItAtStartAndEnd()
    {
        var clip = WithLoneRotationKey();
        var d = Assert.Single(ClipLinter.Analyze(clip, null), x => x.Code == ClipRules.SingleRotationKey);
        Assert.Equal(DiagnosticSeverity.Error, d.Severity);
        Assert.Equal(1, d.Location.Bone);
        var fix = Assert.Single(d.QuickFixes);
        var fixedClip = fix.ClipEdit!(clip);
        var keys = fixedClip.Bones[1].RotationKeys;
        int[] expected = [.. new[] { clip.StartTime, clip.Bones[1].RotationKeys[0].Time, clip.EndTime }.Distinct().Order()];
        Assert.Equal(expected, keys.Select(k => k.Time).ToArray());
        // The stored values are the lone key's, bit for bit.
        Assert.All(keys, k => Assert.Equal(clip.Bones[1].RotationKeys[0] with { Time = k.Time }, k));
        Assert.DoesNotContain(ClipLinter.Analyze(fixedClip, null), x => x.Code == ClipRules.SingleRotationKey);
        Assert.Same(fixedClip, ClipEdit.FixSingleRotationKeys(fixedClip));
    }

    [Fact]
    public void EditsThatEmptyATrackLeaveTwoRotationKeys()
    {
        var clip = Make();
        var bare = clip with { Bones = clip.Bones.SetItem(2, clip.Bones[2] with { RotationKeys = [] }) };
        var filled = ClipEdit.EnsureMinimumKeys(bare);
        Assert.Equal([clip.StartTime, clip.EndTime], filled.Bones[2].RotationKeys.Select(k => k.Time).ToArray());
        Assert.Equal(2, ClipEdit.DeleteKeys(clip, KeySelection.Bones(clip, [0]), keepMinimum: true).Bones[0].RotationKeys.Length);
        Assert.DoesNotContain(ClipLinter.Analyze(filled, null), x => x.Code is ClipRules.SingleRotationKey or ClipRules.NoRotationKeys);
    }

    private static RfaClip V7MorphClip() => Make() with { Version = 7, Morph = MakeV7Morph() };

    [Fact]
    public void MorphDataTheEngineCannotTimeIsAnError()
    {
        var morph = V7MorphClip();
        Assert.DoesNotContain(ClipLinter.Analyze(morph, null), x => x.Code == ClipRules.MorphUnplayable);
        var noLength = morph with { EndTime = morph.StartTime };
        Assert.Contains(ClipLinter.Analyze(noLength, null), x => x.Code == ClipRules.MorphUnplayable);
        var noKeyframes = morph with { Morph = morph.Morph with { KeyframeCount = 0, Positions = [] } };
        var d = Assert.Single(ClipLinter.Analyze(noKeyframes, null), x => x.Code == ClipRules.MorphUnplayable);
        Assert.True(d.QuickFixes.Single().ClipEdit!(noKeyframes).Morph.IsEmpty);
    }

    [Fact]
    public void RecomputeRangeKeepsAVersion7MorphOnItsTimes()
    {
        var clip = V7MorphClip();
        // Keys reaching past the end: the range grows, and the v7 keyframes (spread over [start, end]) are
        // resampled so each plays at the same time as before.
        var track = clip.Bones[0];
        int later = clip.EndTime + 1600;
        var wide = clip with { Bones = clip.Bones.SetItem(0, track with { RotationKeys = track.RotationKeys.Add(track.RotationKeys[^1] with { Time = later }) }) };
        var recomputed = ClipEdit.RecomputeRange(wide);
        Assert.Equal(later, recomputed.EndTime);
        Assert.NotEqual(wide.Morph.Positions, recomputed.Morph.Positions);
        Assert.Equal(wide.Morph.KeyframeCount, recomputed.Morph.KeyframeCount);
    }
}
