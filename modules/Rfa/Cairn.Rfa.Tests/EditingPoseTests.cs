using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

/// <summary>
/// A small synthetic humanoid for the pose-editing tests: a symmetric rest pose whose right-side bone
/// frames are the left ones reflected AND given a half turn (as real exporters do), so mirroring has
/// to go through the model-space rest delta, plus a deterministic clip with eased keys and root motion.
/// </summary>
internal static class PoseFixtures
{
    public static readonly string[] Names =
        ["root", "pelvis", "spine", "head", "arm-l-upper", "arm-r-upper", "arm-l-lower", "arm-r-lower", "leg-l-upper", "leg-r-upper"];

    public static readonly int[] Parents = [-1, 0, 1, 2, 2, 2, 4, 5, 1, 1];

    public const int Start = 160;
    public const int End = 1120;

    public static Quaternion ReflectX(Quaternion q) => new(q.X, -q.Y, -q.Z, q.W);

    public static Vector3 ReflectX(Vector3 v) => new(-v.X, v.Y, v.Z);

    public static V3dBone Bone(string name, int parent, Rigid world) =>
        new(FixedString.FromText(name, 24), world.Rotation, world.Inverse().Position, parent);

    public static Skeleton Skeleton()
    {
        var world = new Rigid[10];
        world[0] = new Rigid(Quat.FromAxisAngle(Vector3.UnitX, 0.05f), new Vector3(0, 1.0f, 0));
        world[1] = new Rigid(Quat.FromAxisAngle(Vector3.UnitX, -0.1f), new Vector3(0, 1.02f, 0.01f));
        world[2] = new Rigid(Quat.FromAxisAngle(Vector3.UnitX, 0.2f), new Vector3(0, 1.3f, 0));
        world[3] = new Rigid(Quat.FromAxisAngle(Vector3.UnitX, -0.15f), new Vector3(0, 1.6f, 0.02f));
        var halfZ = Quat.FromAxisAngle(Vector3.UnitZ, MathF.PI);
        var halfY = Quat.FromAxisAngle(Vector3.UnitY, MathF.PI);
        var armL = new Rigid(Quat.FromEulerDegrees(new Vector3(10, 80, -20)), new Vector3(-0.2f, 1.5f, 0));
        var lowerL = new Rigid(Quat.FromEulerDegrees(new Vector3(-5, 70, 15)), new Vector3(-0.45f, 1.45f, 0.03f));
        var legL = new Rigid(Quat.FromEulerDegrees(new Vector3(170, 5, 3)), new Vector3(-0.1f, 0.95f, 0));
        world[4] = armL;
        world[5] = new Rigid(Quat.Mul(ReflectX(armL.Rotation), halfZ), ReflectX(armL.Position));
        world[6] = lowerL;
        world[7] = new Rigid(Quat.Mul(ReflectX(lowerL.Rotation), halfZ), ReflectX(lowerL.Position));
        world[8] = legL;
        world[9] = new Rigid(Quat.Mul(ReflectX(legL.Rotation), halfY), ReflectX(legL.Position));
        return Animation.Skeleton.FromBones([.. Enumerable.Range(0, Names.Length).Select(i => Bone(Names[i], Parents[i], world[i]))]);
    }

    /// <summary>Keys at 160/480/800/1120 (the head at 160/640/1120), eased middle keys, moving root.</summary>
    public static RfaClip Clip(Skeleton? skeleton = null, int seed = 7)
    {
        skeleton ??= Skeleton();
        var rng = new Random(seed);
        var bones = new List<RfaBoneTrack>();
        for (int b = 0; b < skeleton.Count; b++)
        {
            int[] times = b == 3 ? [160, 640, 1120] : [160, 480, 800, 1120];
            var rot = new List<RfaRotKey>();
            for (int i = 0; i < times.Length; i++)
            {
                var axis = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f));
                var delta = Quat.FromAxisAngle(axis, (rng.NextSingle() - 0.5f) * 1.4f);
                sbyte easeIn = (sbyte)(i == 2 ? 20 : 0), easeOut = (sbyte)(i == 1 ? 30 : 0);
                rot.Add(ClipEdit.QuantizeRotation(times[i], Quat.Mul(skeleton.RestLocal[b].Rotation, delta), rot.Count > 0 ? rot[^1] : null, easeIn, easeOut));
            }
            ImmutableArray<RfaPosKey> pos = b == 0
                ?
                [
                    new RfaPosKey(160, new Vector3(0, 1, 0), new Vector3(0, 1, 0), new Vector3(0.02f, 1.02f, 0.15f)),
                    new RfaPosKey(640, new Vector3(0.1f, 1.05f, 0.5f), new Vector3(0.08f, 1.04f, 0.35f), new Vector3(0.12f, 1.06f, 0.65f)),
                    new RfaPosKey(1120, new Vector3(0.2f, 1f, 1f), new Vector3(0.18f, 1f, 0.85f), new Vector3(0.2f, 1f, 1f)),
                ]
                : [RfaPosKey.Constant(160, skeleton.RestLocal[b].Position), RfaPosKey.Constant(1120, skeleton.RestLocal[b].Position)];
            float weight = b switch { 4 => 8f, 5 => 2f, _ => 2f + b % 3 };
            bones.Add(new RfaBoneTrack(weight, [.. rot], pos));
        }
        return new RfaClip { StartTime = Start, EndTime = End, RampIn = 160, RampOut = 160, Bones = [.. bones] };
    }

    /// <summary>
    /// The structural rules every edit result must pass: at least one rotation and two position keys
    /// per bone, strictly increasing times inside [start, end], unit quaternions within 0.002 and sign
    /// continuity.
    /// </summary>
    public static void AssertStructure(RfaClip clip)
    {
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var track = clip.Bones[b];
            Assert.True(track.RotationKeys.Length >= 1, $"bone {b} has no rotation key");
            if (clip.Duration > 0) Assert.True(track.PositionKeys.Length >= 2, $"bone {b} has fewer than two position keys");
            for (int i = 0; i < track.RotationKeys.Length; i++)
            {
                var k = track.RotationKeys[i];
                Assert.InRange(k.Time, clip.StartTime, clip.EndTime);
                Assert.InRange(k.FileQuaternion.Length(), 0.998f, 1.002f);
                if (i > 0)
                {
                    Assert.True(k.Time > track.RotationKeys[i - 1].Time, $"bone {b} rotation key {i} is not after the one before");
                    Assert.True(Quat.Dot(k.FileQuaternion, track.RotationKeys[i - 1].FileQuaternion) >= 0f, $"bone {b} rotation key {i} flips sign");
                }
            }
            for (int i = 0; i < track.PositionKeys.Length; i++)
            {
                Assert.InRange(track.PositionKeys[i].Time, clip.StartTime, clip.EndTime);
                if (i > 0) Assert.True(track.PositionKeys[i].Time > track.PositionKeys[i - 1].Time, $"bone {b} position key {i} is not after the one before");
            }
        }
    }

    /// <summary>Both clips sample to the same locals (every 40 ticks over the union of their ranges).</summary>
    public static void AssertSamplesEqual(RfaClip expected, RfaClip actual, float degrees = 0.2f, float metres = 1e-4f, IEnumerable<int>? bones = null)
    {
        Assert.Equal(expected.BoneCount, actual.BoneCount);
        int from = Math.Min(expected.StartTime, actual.StartTime), to = Math.Max(expected.EndTime, actual.EndTime);
        foreach (int b in bones ?? Enumerable.Range(0, expected.BoneCount))
        {
            for (int t = from; t <= to; t += 40)
            {
                float angle = Quat.AngleDegrees(ClipEdit.SampleRotation(expected, b, t), ClipEdit.SampleRotation(actual, b, t));
                Assert.True(angle <= degrees, $"bone {b} at {t}: rotations differ by {angle} degrees");
                float d = Vector3.Distance(ClipEdit.SamplePosition(expected, b, t), ClipEdit.SamplePosition(actual, b, t));
                Assert.True(d <= metres, $"bone {b} at {t}: positions differ by {d} m");
            }
        }
    }

    /// <summary>True when two tracks are bit-identical (weight, every key field).</summary>
    public static bool SameTrack(RfaBoneTrack a, RfaBoneTrack b) =>
        a.Weight.Equals(b.Weight) && a.RotationKeys.SequenceEqual(b.RotationKeys) && a.PositionKeys.SequenceEqual(b.PositionKeys);

    public static float Angle(Quaternion a, Quaternion b) => Quat.AngleDegrees(a, b);
}

public class EditingPoseTests
{
    private static readonly Skeleton Rig = PoseFixtures.Skeleton();

    [Fact]
    public void LocalOffsetRotatesEveryKeyAboutTheBonesOwnAxes()
    {
        var clip = PoseFixtures.Clip(Rig);
        var r = Quat.FromAxisAngle(Vector3.UnitY, 0.6f);
        var result = ClipEdit.OffsetBone(clip, 4, BoneOffset.Rotate(r));
        PoseFixtures.AssertStructure(result);

        var before = clip.Bones[4].RotationKeys;
        var after = result.Bones[4].RotationKeys;
        Assert.Equal(before.Length, after.Length);
        for (int i = 0; i < before.Length; i++)
        {
            Assert.Equal(before[i].Time, after[i].Time);
            Assert.Equal(before[i].EaseIn, after[i].EaseIn);
            Assert.Equal(before[i].EaseOut, after[i].EaseOut);
            Assert.True(PoseFixtures.Angle(Quat.Mul(ClipEdit.KeyRotation(before[i]), r), ClipEdit.KeyRotation(after[i])) < 0.02f);
        }
        Assert.True(clip.Bones[4].PositionKeys.SequenceEqual(result.Bones[4].PositionKeys));
        for (int b = 0; b < clip.BoneCount; b++)
        {
            if (b != 4) Assert.Same(clip.Bones[b], result.Bones[b]);
        }
    }

    [Fact]
    public void ParentOffsetRotatesAboutTheParentsAxes()
    {
        var clip = PoseFixtures.Clip(Rig);
        var r = Quat.FromAxisAngle(Vector3.UnitZ, -0.4f);
        var result = ClipEdit.OffsetBone(clip, 6, BoneOffset.Rotate(r, OffsetSpace.Parent));
        PoseFixtures.AssertStructure(result);
        for (int i = 0; i < clip.Bones[6].RotationKeys.Length; i++)
        {
            var expected = Quat.Mul(r, ClipEdit.KeyRotation(clip.Bones[6].RotationKeys[i]));
            Assert.True(PoseFixtures.Angle(expected, ClipEdit.KeyRotation(result.Bones[6].RotationKeys[i])) < 0.02f);
        }
    }

    [Fact]
    public void ModelSpaceOffsetTurnsTheBoneInModelSpaceAndItsInverseUndoesIt()
    {
        var clip = PoseFixtures.Clip(Rig);
        var r = Quat.FromAxisAngle(new Vector3(1, 1, 0), 0.7f);
        var move = new Vector3(0.02f, -0.03f, 0.05f);
        var result = ClipEdit.OffsetBone(clip, 6, new BoneOffset(r, move, OffsetSpace.Model) { Skeleton = Rig });
        PoseFixtures.AssertStructure(result);

        // At the bone's key times its world rotation is exactly the offset applied in model space.
        var before = new Pose(Rig);
        var after = new Pose(Rig);
        foreach (var key in clip.Bones[6].RotationKeys)
        {
            before.Sample(clip, key.Time);
            after.Sample(result, key.Time);
            Assert.True(PoseFixtures.Angle(Quat.Mul(r, before.World[6].Rotation), after.World[6].Rotation) < 0.05f);
        }

        var back = ClipEdit.OffsetBone(result, 6, new BoneOffset(Quat.Conj(r), -move, OffsetSpace.Model) { Skeleton = Rig });
        PoseFixtures.AssertSamplesEqual(clip, back);
    }

    [Fact]
    public void ModelSpaceOffsetOfAWholeChainCanBeUndone()
    {
        var clip = PoseFixtures.Clip(Rig);
        var offset = BoneOffset.Rotate(Quat.FromAxisAngle(Vector3.UnitX, 0.5f), OffsetSpace.Model) with { Skeleton = Rig };
        int[] chain = [4, 6];
        var result = ClipEdit.OffsetBones(clip, chain, offset);
        PoseFixtures.AssertStructure(result);
        var back = ClipEdit.OffsetBones(result, chain, offset with { Rotation = Quat.Conj(offset.Rotation) });
        PoseFixtures.AssertSamplesEqual(clip, back);
    }

    [Fact]
    public void RangedOffsetFallsOffSmoothlyAndLeavesTheRestExactlyAsItWas()
    {
        var clip = PoseFixtures.Clip(Rig);
        var r = Quat.FromAxisAngle(Vector3.UnitX, 0.8f);
        var offset = BoneOffset.Rotate(r, OffsetSpace.Parent) with { From = 480, To = 800, FalloffTicks = 160 };
        Assert.Equal(0f, offset.WeightAt(300));
        Assert.Equal(0f, offset.WeightAt(320));
        Assert.Equal(0.5f, offset.WeightAt(400), 5);
        Assert.Equal(1f, offset.WeightAt(480));
        Assert.Equal(1f, offset.WeightAt(800));
        Assert.Equal(0.5f, offset.WeightAt(880), 5);
        Assert.Equal(0f, offset.WeightAt(960));

        var result = ClipEdit.OffsetBone(clip, 2, offset);
        PoseFixtures.AssertStructure(result);
        var keys = result.Bones[2].RotationKeys;
        Assert.Equal([160, 320, 480, 800, 960, 1120], keys.Select(k => k.Time).ToArray());
        Assert.Equal(clip.Bones[2].RotationKeys[0], keys[0]);   // outside: untouched bits
        Assert.Equal(clip.Bones[2].RotationKeys[3], keys[5]);
        for (int i = 1; i <= 2; i++)
        {
            var expected = Quat.Mul(r, ClipEdit.KeyRotation(clip.Bones[2].RotationKeys[i]));
            Assert.True(PoseFixtures.Angle(expected, ClipEdit.KeyRotation(keys[i + 1])) < 0.02f);
        }

        // The anchors confine the edit: before From - falloff and after To + falloff nothing moves.
        for (int t = 160; t <= 320; t += 20)
            Assert.True(PoseFixtures.Angle(ClipEdit.SampleRotation(clip, 2, t), ClipEdit.SampleRotation(result, 2, t)) < 0.05f, $"at {t}");
        for (int t = 960; t <= 1120; t += 20)
            Assert.True(PoseFixtures.Angle(ClipEdit.SampleRotation(clip, 2, t), ClipEdit.SampleRotation(result, 2, t)) < 0.05f, $"at {t}");
        // Half way through the falloff the bone is visibly (but not fully) turned.
        float mid = PoseFixtures.Angle(ClipEdit.SampleRotation(clip, 2, 400), ClipEdit.SampleRotation(result, 2, 400));
        Assert.InRange(mid, 5f, 45f);
        Assert.True(clip.Bones[2].PositionKeys.SequenceEqual(result.Bones[2].PositionKeys));
    }

    [Fact]
    public void TranslationOffsetMovesPositionKeysWithTheirControlPoints()
    {
        var clip = PoseFixtures.Clip(Rig);
        var move = new Vector3(0.1f, 0.2f, -0.05f);
        var result = ClipEdit.OffsetBone(clip, 0, BoneOffset.Move(move));
        PoseFixtures.AssertStructure(result);
        for (int i = 0; i < clip.Bones[0].PositionKeys.Length; i++)
        {
            var a = clip.Bones[0].PositionKeys[i];
            var b = result.Bones[0].PositionKeys[i];
            Assert.Equal(a.Time, b.Time);
            Assert.True(Vector3.Distance(a.Position + move, b.Position) < 1e-6f);
            Assert.True(Vector3.Distance(a.InControl + move, b.InControl) < 1e-6f);
            Assert.True(Vector3.Distance(a.OutControl + move, b.OutControl) < 1e-6f);
        }
        Assert.True(clip.Bones[0].RotationKeys.SequenceEqual(result.Bones[0].RotationKeys));
    }

    [Fact]
    public void RangedTranslationSplitsThePositionCurveExactly()
    {
        var clip = PoseFixtures.Clip(Rig);
        var offset = BoneOffset.Move(new Vector3(0, 0.3f, 0)) with { From = 800, To = 1120, FalloffTicks = 160 };
        var result = ClipEdit.OffsetBone(clip, 0, offset);
        PoseFixtures.AssertStructure(result);
        Assert.Equal([160, 640, 800, 1120], result.Bones[0].PositionKeys.Select(k => k.Time).ToArray());
        for (int t = 160; t <= 640; t += 20)
            Assert.True(Vector3.Distance(ClipEdit.SamplePosition(clip, 0, t), ClipEdit.SamplePosition(result, 0, t)) < 1e-5f, $"at {t}");
        Assert.True(Vector3.Distance(ClipEdit.SamplePosition(clip, 0, 800) + new Vector3(0, 0.3f, 0), ClipEdit.SamplePosition(result, 0, 800)) < 1e-5f);
    }

    [Fact]
    public void LocalTranslationFollowsTheBonesRotation()
    {
        var clip = PoseFixtures.Clip(Rig);
        var result = ClipEdit.OffsetBone(clip, 4, BoneOffset.Move(Vector3.UnitX * 0.1f, OffsetSpace.Local));
        foreach (var (a, b) in clip.Bones[4].PositionKeys.Zip(result.Bones[4].PositionKeys))
        {
            var expected = a.Position + Quat.Rotate(ClipEdit.SampleRotation(clip, 4, a.Time), Vector3.UnitX * 0.1f);
            Assert.True(Vector3.Distance(expected, b.Position) < 1e-5f);
        }
    }

    [Fact]
    public void OffsetArgumentsAreChecked()
    {
        var clip = PoseFixtures.Clip(Rig);
        var model = BoneOffset.Rotate(Quat.FromAxisAngle(Vector3.UnitX, 0.1f), OffsetSpace.Model);
        Assert.Contains("Skeleton", Assert.Throws<ArgumentException>(() => ClipEdit.OffsetBone(clip, 1, model)).Message);
        Assert.Throws<ArgumentException>(() => ClipEdit.OffsetBone(clip, 1, model with { Skeleton = Animation.Skeleton.Empty }));
        Assert.Throws<ArgumentException>(() => ClipEdit.OffsetBone(clip, 1, BoneOffset.Rotate(Quaternion.Identity) with { From = 900, To = 200 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.OffsetBone(clip, 99, BoneOffset.Rotate(Quaternion.Identity)));
        Assert.Same(clip, ClipEdit.OffsetBone(clip, 1, BoneOffset.Rotate(Quaternion.Identity)));
    }
}
