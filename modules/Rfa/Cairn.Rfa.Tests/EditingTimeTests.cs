using System.Numerics;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

public class EditingTimeTests
{
    // ── Trim ────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(Start + 320, Start + 3200)]  // both boundaries in constant-speed parts
    [InlineData(Start + 640, Start + 4000)]  // boundaries on existing keys
    [InlineData(Start + 2100, Start + 2300)] // both inside one segment
    [InlineData(Start, End)]                 // no-op
    public void TrimKeepsTheMotionInsideTheRange(int from, int to)
    {
        var clip = Make();
        var trimmed = ClipEdit.Trim(clip, from, to);
        Assert.Equal(from, trimmed.StartTime);
        Assert.Equal(to, trimmed.EndTime);
        Assert.InRange(trimmed.RampIn, 0, to - from);
        Assert.InRange(trimmed.RampOut, 0, to - from);
        AssertStructural(trimmed);
        AssertSameMotion(clip, trimmed, from, to, step: 4);
        // Keys strictly inside the range that do not neighbour a boundary are untouched.
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var inner = clip.Bones[b].PositionKeys.Where(k => k.Time > from && k.Time < to).ToArray();
            foreach (var k in inner.Skip(1).SkipLast(1)) Assert.Contains(k, trimmed.Bones[b].PositionKeys);
        }
        if (from == Start && to == End) AssertIdentical(clip, trimmed);
    }

    [Fact]
    public void TrimClampsRampsAndRejectsAnEmptyRange()
    {
        var clip = Make() with { RampIn = 4000, RampOut = 100 };
        var trimmed = ClipEdit.Trim(clip, Start + 1000, Start + 2000);
        // Each ramp is clamped to the new duration, then both shrink together so they still fit (phase 7b):
        // 1000 + 100 in a 1000-tick clip becomes 909 + 90.
        Assert.Equal(909, trimmed.RampIn);
        Assert.Equal(90, trimmed.RampOut);
        Assert.True(trimmed.RampIn + trimmed.RampOut <= trimmed.EndTime - trimmed.StartTime);
        // Ramps that never fitted the original clip are only clamped.
        var overlong = ClipEdit.Trim(clip with { RampIn = 9000, RampOut = 9000 }, Start + 1000, Start + 2000);
        Assert.Equal((1000, 1000), (overlong.RampIn, overlong.RampOut));
        var ex = Assert.Throws<ArgumentException>(() => ClipEdit.Trim(clip, 500, 500));
        Assert.Contains("at least one tick", ex.Message);
    }

    [Fact]
    public void TrimKeepsTwoPositionKeysWhenOnlyOneWouldRemain()
    {
        var clip = Make();
        var trimmed = ClipEdit.Trim(clip, PosTimes[^1], PosTimes[^1] + 160);
        AssertStructural(trimmed with { EndTime = trimmed.EndTime });
        Assert.All(trimmed.Bones, t => Assert.Equal(2, t.PositionKeys.Length));
    }

    [Fact]
    public void TrimResamplesAVersion7MorphSoItPlaysTheSame()
    {
        var clip = Make(version: 7) with { Morph = MakeV7Morph(8) };
        var trimmed = ClipEdit.Trim(clip, Start + 1000, Start + 3000);
        Assert.Equal(8, trimmed.Morph.KeyframeCount);
        for (int t = Start + 1000; t < Start + 3000; t += 50)
        {
            var a = ClipEdit.SampleMorph(clip, t);
            var b = ClipEdit.SampleMorph(trimmed, t);
            for (int v = 0; v < a.Length; v++) Assert.InRange(Vector3.Distance(a[v], b[v]), 0f, 0.02f); // a resampling of a piecewise-linear morph
        }
        // Version 8 morph times are absolute and stay as they are.
        var v8 = ClipEdit.ConvertVersion(clip, 8);
        Assert.Equal(v8.Morph.KeyframeTimes.ToArray(), ClipEdit.Trim(v8, Start + 1000, Start + 3000).Morph.KeyframeTimes.ToArray());
    }

    // ── Shift / retime / reverse ────────────────────────────────────────────────────────────────

    [Fact]
    public void ShiftThereAndBackRestoresTheClip()
    {
        var clip = ClipEdit.ConvertVersion(Make(version: 7) with { Morph = MakeV7Morph() }, 8);
        var shifted = ClipEdit.Shift(clip, 1234);
        Assert.Equal(clip.StartTime + 1234, shifted.StartTime);
        Assert.Equal(clip.EndTime + 1234, shifted.EndTime);
        Assert.Equal(clip.Morph.KeyframeTimes.Select(t => t + 1234).ToArray(), shifted.Morph.KeyframeTimes.ToArray());
        for (int b = 0; b < clip.BoneCount; b++)
            AssertKeys(clip.Bones[b].RotationKeys.Select(k => k with { Time = k.Time + 1234 }), shifted.Bones[b].RotationKeys);
        AssertSameMotion(clip, shifted, Start, End, map: t => t + 1234);
        AssertIdentical(clip, ClipEdit.Shift(shifted, -1234));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.Shift(clip, int.MaxValue));
    }

    [Fact]
    public void RetimeScalesEveryTimeAndInvertsWithoutCollisions()
    {
        var clip = ClipEdit.ConvertVersion(Make(version: 7) with { Morph = MakeV7Morph() }, 8);
        var slow = ClipEdit.Retime(clip, 2);
        Assert.Equal(Start, slow.StartTime);
        Assert.Equal(Start + 2 * (End - Start), slow.EndTime);
        Assert.Equal(640, slow.RampIn);
        Assert.Equal(960, slow.RampOut);
        AssertStructural(slow);
        AssertSameMotion(clip, slow, Start, End, map: t => Start + 2 * (t - Start), rotDegrees: 0.05f, pos: 1e-6f);
        AssertIdentical(clip, ClipEdit.Retime(slow, 0.5));

        var pivoted = ClipEdit.Retime(clip, 1.5, pivot: End);
        Assert.Equal(End, pivoted.EndTime);
        Assert.Equal(End - 3 * (End - Start) / 2, pivoted.StartTime);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.Retime(clip, -1));
    }

    [Fact]
    public void RetimeCollisionsKeepTheLaterKey()
    {
        var clip = Make();
        var squashed = ClipEdit.Retime(clip, 1.0 / 2000);
        AssertStructural(squashed);
        foreach (var t in squashed.Bones)
        {
            Assert.Equal(t.RotationKeys.Select(k => k.Time).Distinct().Count(), t.RotationKeys.Length);
        }
        Assert.Equal(clip.Bones[0].RotationKeys[^1] with { Time = Start + 3 }, squashed.Bones[0].RotationKeys[^1]);
    }

    [Fact]
    public void ReverseTwiceIsTheIdentityAndPlaysBackwards()
    {
        var clip = ClipEdit.ConvertVersion(Make(version: 7) with { Morph = MakeV7Morph() }, 8);
        var reversed = ClipEdit.Reverse(clip);
        AssertStructural(reversed);
        // Mirrored eases and swapped control points make every segment the exact mirror.
        AssertSameMotion(clip, reversed, Start, End, step: 4, rotDegrees: 0.05f, pos: 1e-5f, map: t => Start + End - t);
        Assert.Equal(clip.Morph.KeyframeTimes.Reverse().Select(t => Start + End - t).ToArray(), reversed.Morph.KeyframeTimes.ToArray());
        Assert.Equal(clip.Morph.GetPosition(0, 1), reversed.Morph.GetPosition(reversed.Morph.KeyframeCount - 1, 1));
        AssertIdentical(clip, ClipEdit.Reverse(reversed));

        var v7 = Make(version: 7) with { Morph = MakeV7Morph() };
        AssertIdentical(v7, ClipEdit.Reverse(ClipEdit.Reverse(v7)));
        Assert.Equal(v7.Morph.GetPosition(0, 2), ClipEdit.Reverse(v7).Morph.GetPosition(v7.Morph.KeyframeCount - 1, 2));
    }

    [Fact]
    public void RecomputeRangeFitsTheKeys()
    {
        var clip = Make() with { StartTime = 0, EndTime = 99999 };
        var fitted = ClipEdit.RecomputeRange(clip);
        Assert.Equal(Start, fitted.StartTime);
        Assert.Equal(End, fitted.EndTime);
        Assert.Same(fitted, ClipEdit.RecomputeRange(fitted));
        var empty = new RfaClip { StartTime = 5, EndTime = 50, Bones = [new RfaBoneTrack(1, [], [])] };
        Assert.Same(empty, ClipEdit.RecomputeRange(empty));
    }
}
