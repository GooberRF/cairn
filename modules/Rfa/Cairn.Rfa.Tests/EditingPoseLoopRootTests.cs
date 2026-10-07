using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class EditingPoseLoopRootTests
{
    private static readonly Skeleton Rig = PoseFixtures.Skeleton();

    private static bool SameComponents(RfaRotKey a, RfaRotKey b) =>
        (a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.W == b.W) || (a.X == -b.X && a.Y == -b.Y && a.Z == -b.Z && a.W == -b.W);

    // ── Make loopable ────────────────────────────────────────────────────────

    [Fact]
    public void BlendEndToStartMakesTheLastPoseTheFirst()
    {
        var clip = PoseFixtures.Clip(Rig);
        var result = ClipEdit.MakeLoopable(clip, 320);
        PoseFixtures.AssertStructure(result);
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var before = clip.Bones[b];
            var after = result.Bones[b];
            Assert.True(SameComponents(after.RotationKeys[0], after.RotationKeys[^1]), $"bone {b}: last rotation key is not the first");
            Assert.Equal(PoseFixtures.End, after.RotationKeys[^1].Time);
            Assert.Equal(before.PositionKeys[0].Position, after.PositionKeys[^1].Position);
            // Keys before the window keep their bits.
            foreach (var k in before.RotationKeys.Where(k => k.Time <= PoseFixtures.End - 320)) Assert.Contains(k, after.RotationKeys);
            // (An inserted anchor splits the Bezier, so a neighbour's facing control point may move; the points stay.)
            foreach (var k in before.PositionKeys.Where(k => k.Time < PoseFixtures.End - 320))
                Assert.Contains(after.PositionKeys, a => a.Time == k.Time && a.Position == k.Position && a.InControl == k.InControl);
            // The sampled end pose equals the start pose.
            Assert.True(PoseFixtures.Angle(ClipEdit.SampleRotation(result, b, PoseFixtures.Start), ClipEdit.SampleRotation(result, b, PoseFixtures.End)) < 0.01f);
            Assert.Equal(ClipEdit.SamplePosition(result, b, PoseFixtures.Start), ClipEdit.SamplePosition(result, b, PoseFixtures.End));
        }
        // Before the window nothing changes. (The head's 640-1120 segment is eased, and splitting an eased
        // segment at the anchor reshapes it slightly, so compare up to its last untouched key.)
        PoseFixtures.AssertSamplesEqual(clip with { EndTime = 640 }, result with { EndTime = 640 }, 0.05f, 1e-5f);
        PoseFixtures.AssertSamplesEqual(clip with { EndTime = 800 }, result with { EndTime = 800 }, 0.05f, 1e-5f, [0, 1, 2, 4, 5, 6, 7, 8, 9]);
    }

    [Fact]
    public void BlendStartToEndMakesTheFirstPoseTheLast()
    {
        var clip = PoseFixtures.Clip(Rig);
        var result = ClipEdit.MakeLoopable(clip, 160, LoopMode.BlendStartToEnd);
        PoseFixtures.AssertStructure(result);
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var after = result.Bones[b];
            Assert.True(SameComponents(after.RotationKeys[0], clip.Bones[b].RotationKeys[^1]));
            Assert.Equal(clip.Bones[b].RotationKeys[^1], after.RotationKeys[^1]);
            Assert.Equal(clip.Bones[b].PositionKeys[^1].Position, after.PositionKeys[0].Position);
        }
        PoseFixtures.AssertSamplesEqual(clip with { StartTime = 320 }, result with { StartTime = 320 }, 0.05f, 1e-5f);
    }

    [Fact]
    public void LoopingCanKeepTheRootsTravel()
    {
        var clip = PoseFixtures.Clip(Rig);
        var result = ClipEdit.MakeLoopable(clip, 320, LoopMode.BlendEndToStart, new LoopOptions(0, RootMotionAxes.Horizontal));
        PoseFixtures.AssertStructure(result);
        var end = result.Bones[0].PositionKeys[^1];
        Assert.Equal(clip.Bones[0].PositionKeys[^1].Position.X, end.Position.X);
        Assert.Equal(clip.Bones[0].PositionKeys[^1].Position.Z, end.Position.Z);
        Assert.Equal(clip.Bones[0].PositionKeys[0].Position.Y, end.Position.Y);
        // Without the option the root returns to its start.
        var full = ClipEdit.MakeLoopable(clip, 320);
        Assert.Equal(clip.Bones[0].PositionKeys[0].Position, full.Bones[0].PositionKeys[^1].Position);
    }

    [Fact]
    public void ConstantTracksGetNoExtraKeysAndZeroBlendOnlySetsTheEnd()
    {
        var clip = PoseFixtures.Clip(Rig);
        clip = ClipEdit.WithTrack(clip, 2, clip.Bones[2] with { RotationKeys = [clip.Bones[2].RotationKeys[0]] });
        var result = ClipEdit.MakeLoopable(clip, 320);
        Assert.Equal(clip.Bones[2].RotationKeys[0], Assert.Single(result.Bones[2].RotationKeys));
        Assert.True(clip.Bones[2].PositionKeys.SequenceEqual(result.Bones[2].PositionKeys));

        var snap = ClipEdit.MakeLoopable(clip, 0);
        PoseFixtures.AssertStructure(snap);
        Assert.Equal(clip.Bones[4].RotationKeys.Length, snap.Bones[4].RotationKeys.Length);
        Assert.True(SameComponents(snap.Bones[4].RotationKeys[0], snap.Bones[4].RotationKeys[^1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.MakeLoopable(clip, clip.Duration + 1));
    }

    // ── Root motion ──────────────────────────────────────────────────────────

    [Fact]
    public void RemovingHorizontalRootMotionPlaysInPlace()
    {
        var clip = PoseFixtures.Clip(Rig);
        var result = ClipEdit.RemoveRootMotion(clip, 0, RootMotionAxes.Horizontal);
        PoseFixtures.AssertStructure(result);
        var first = clip.Bones[0].PositionKeys[0].Position;
        for (int t = PoseFixtures.Start; t <= PoseFixtures.End; t += 40)
        {
            var before = ClipEdit.SamplePosition(clip, 0, t);
            var after = ClipEdit.SamplePosition(result, 0, t);
            Assert.Equal(first.X, after.X, 6);
            Assert.Equal(first.Z, after.Z, 6);
            Assert.Equal(before.Y, after.Y, 6);
        }
        Assert.True(clip.Bones[0].RotationKeys.SequenceEqual(result.Bones[0].RotationKeys));
        for (int b = 1; b < clip.BoneCount; b++) Assert.Same(clip.Bones[b], result.Bones[b]);
    }

    [Fact]
    public void RemovingYawKeepsTheFirstHeadingAndTheTilt()
    {
        var clip = PoseFixtures.Clip(Rig);
        // Give the root a turning walk: a growing heading on top of a fixed tilt.
        var tilt = Quat.FromAxisAngle(Vector3.UnitX, 0.2f);
        var keys = new List<RfaRotKey>();
        foreach (int t in new[] { 160, 480, 800, 1120 })
        {
            var q = Quat.Mul(Quat.FromAxisAngle(Vector3.UnitY, (t - 160) / 960f * 2.5f), tilt);
            keys.Add(ClipEdit.QuantizeRotation(t, q, keys.Count > 0 ? keys[^1] : null));
        }
        clip = ClipEdit.WithRotationKeys(clip, 0, [.. keys]);
        var result = ClipEdit.RemoveRootMotion(clip, 0, RootMotionAxes.Yaw);
        PoseFixtures.AssertStructure(result);
        Assert.Equal(clip.Bones[0].RotationKeys[0], result.Bones[0].RotationKeys[0]);
        foreach (var k in result.Bones[0].RotationKeys)
            Assert.True(PoseFixtures.Angle(tilt, ClipEdit.KeyRotation(k)) < 0.05f, $"at {k.Time}");
        Assert.True(clip.Bones[0].PositionKeys.SequenceEqual(result.Bones[0].PositionKeys));
    }

    [Fact]
    public void ScalingRootMotionScalesTheTravelAndTheControlPoints()
    {
        var clip = PoseFixtures.Clip(Rig);
        var scale = new Vector3(2f, 1f, 0.5f);
        var result = ClipEdit.ScaleRootMotion(clip, 0, scale);
        PoseFixtures.AssertStructure(result);
        var first = clip.Bones[0].PositionKeys[0].Position;
        foreach (var (a, b) in clip.Bones[0].PositionKeys.Zip(result.Bones[0].PositionKeys))
        {
            Assert.True(Vector3.Distance(first + (a.Position - first) * scale, b.Position) < 1e-6f);
            Assert.True(Vector3.Distance(first + (a.InControl - first) * scale, b.InControl) < 1e-6f);
            Assert.True(Vector3.Distance(first + (a.OutControl - first) * scale, b.OutControl) < 1e-6f);
        }
        Assert.Equal(clip.Bones[0].PositionKeys[0].Position, result.Bones[0].PositionKeys[0].Position);
        Assert.Same(clip, ClipEdit.ScaleRootMotion(clip, 0, Vector3.One));
        var back = ClipEdit.ScaleRootMotion(result, 0, new Vector3(0.5f, 1f, 2f));
        PoseFixtures.AssertSamplesEqual(clip, back, 0.01f, 1e-5f);
    }

    [Fact]
    public void FindRootBonePrefersTheRootByNameThenByDescendants()
    {
        Assert.Equal(0, ClipEdit.FindRootBone(Rig));
        Assert.Equal(-1, ClipEdit.FindRootBone(Animation.Skeleton.Empty));
        Assert.Equal(2, ClipEdit.FindRootBone([2, 2, -1]));
        // Two parentless bones: the one called root wins, else the bigger tree.
        Assert.Equal(2, ClipEdit.FindRootBone([-1, 0, -1, 2], ["prop", "x", "ult2-bdbn-root", "y"]));
        Assert.Equal(3, ClipEdit.FindRootBone([-1, 3, 3, -1]));
    }
}
