using System.Numerics;
using Cairn.Rfa.Editing;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

public class EditingBakeTests
{
    [Fact]
    public void ResampleKeysEveryStepIncludingTheEnd()
    {
        var clip = Make() with { EndTime = End + 50 };
        var baked = ClipEdit.Resample(clip, 160);
        var expected = Enumerable.Range(0, 33).Select(i => Start + 160 * i).Append(End + 50).ToArray();
        foreach (var t in baked.Bones)
        {
            Assert.Equal(expected, t.RotationKeys.Select(k => k.Time).ToArray());
            Assert.Equal(expected, t.PositionKeys.Select(k => k.Time).ToArray());
            Assert.All(t.RotationKeys, k => Assert.Equal((0, 0), ((int)k.EaseIn, (int)k.EaseOut)));
        }
        AssertStructural(baked);
        // Values equal the samples at every key time.
        for (int b = 0; b < clip.BoneCount; b++)
        {
            foreach (var k in baked.Bones[b].RotationKeys)
                Assert.InRange(Angle(ClipEdit.SampleRotation(clip, b, k.Time), ClipEdit.KeyRotation(k)), 0f, 0.02f);
        }
        // The original position keys lie on the 160 grid, so every stretch is one cubic: exact curves.
        var (_, pos) = MaxDifference(clip, baked, Start, End + 50, 1);
        Assert.InRange(pos, 0f, 2e-6f);
    }

    [Fact]
    public void ResampleOffGridUsesLinearControlsWhereAStretchSpansAKey()
    {
        var clip = Make();
        var baked = ClipEdit.Resample(clip, 700, rotations: false);
        Assert.Equal(clip.Bones[0].RotationKeys.ToArray(), baked.Bones[0].RotationKeys.ToArray());
        AssertStructural(baked);
        // Keys hold the sampled positions.
        foreach (var k in baked.Bones[1].PositionKeys)
            Assert.InRange(Vector3.Distance(ClipEdit.SamplePosition(clip, 1, k.Time), k.Position), 0f, 1e-6f);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.Resample(clip, 0));
    }

    [Theory]
    [InlineData(0.1f, 0.0005f, 16)]
    [InlineData(0.5f, 0.002f, 32)]
    [InlineData(0f, 0f, 16)]
    public void ReduceKeysStaysWithinTheToleranceMeasuredIndependently(float rotTol, float posTol, int step)
    {
        // A densely baked clip has plenty of redundant keys.
        var dense = ClipEdit.Resample(Make(), 40);
        var result = ClipEdit.ReduceKeys(dense, new ReduceOptions { RotationToleranceDegrees = rotTol, PositionTolerance = posTol, CheckStepTicks = step });
        var reduced = result.Clip;
        AssertStructural(reduced);
        Assert.Equal(dense.Bones.Sum(t => t.RotationKeys.Length + t.PositionKeys.Length), result.KeysBefore);
        Assert.Equal(reduced.Bones.Sum(t => t.RotationKeys.Length + t.PositionKeys.Length), result.KeysAfter);
        if (rotTol > 0) Assert.True(result.KeysAfter < result.KeysBefore / 2, $"{result.KeysAfter} of {result.KeysBefore} kept");

        // Independent measurement at the same check step (and the end).
        var (rot, pos) = MaxDifference(dense, reduced, dense.StartTime, dense.EndTime, step);
        var (rotEnd, posEnd) = MaxDifference(dense, reduced, dense.EndTime, dense.EndTime, 1);
        rot = MathF.Max(rot, rotEnd);
        pos = MathF.Max(pos, posEnd);
        Assert.InRange(rot, 0f, rotTol + 1e-4f);
        Assert.InRange(pos, 0f, posTol + 1e-7f);
        Assert.InRange(MathF.Abs(rot - result.MaxRotationErrorDegrees), 0f, 1e-4f);
        Assert.InRange(MathF.Abs(pos - result.MaxPositionError), 0f, 1e-7f);

        for (int b = 0; b < dense.BoneCount; b++)
        {
            var o = dense.Bones[b];
            var r = reduced.Bones[b];
            // Rotation keys are a bit-identical subset; first and last always kept.
            Assert.All(r.RotationKeys, k => Assert.Contains(k, o.RotationKeys));
            Assert.Equal(o.RotationKeys[0], r.RotationKeys[0]);
            Assert.Equal(o.RotationKeys[^1], r.RotationKeys[^1]);
            // Position keys keep their time and position (only facing control points may be refit).
            Assert.All(r.PositionKeys, k => Assert.Contains(o.PositionKeys, x => x.Time == k.Time && x.Position == k.Position));
            Assert.Equal(o.PositionKeys[0].Position, r.PositionKeys[0].Position);
            Assert.Equal(o.PositionKeys[^1].Time, r.PositionKeys[^1].Time);
        }
    }

    [Fact]
    public void ReduceKeysOnlyTouchesWhatItIsAskedTo()
    {
        var dense = ClipEdit.Resample(Make(), 80);
        var result = ClipEdit.ReduceKeys(dense, new ReduceOptions { Positions = false, Bones = [1] });
        for (int b = 0; b < dense.BoneCount; b++)
        {
            Assert.Equal(dense.Bones[b].PositionKeys.ToArray(), result.Clip.Bones[b].PositionKeys.ToArray());
            if (b != 1) Assert.Same(dense.Bones[b], result.Clip.Bones[b]);
        }
        Assert.True(result.Clip.Bones[1].RotationKeys.Length < dense.Bones[1].RotationKeys.Length);
    }

    [Fact]
    public void MeasureErrorIsZeroForTheSameClip()
    {
        var clip = Make();
        Assert.Equal((0f, 0f), ClipEdit.MeasureError(clip, clip, 16));
        Assert.Throws<ArgumentException>(() => ClipEdit.MeasureError(clip, Make(3), 16));
    }
}
