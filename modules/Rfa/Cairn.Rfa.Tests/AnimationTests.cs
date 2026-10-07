using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class AnimationTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-4f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static V3dBone Bone(string name, int parent, Rigid world)
    {
        // Store the inverse bind as the file does: rotation = world rotation as-is, position =
        // translation of the inverse (model -> bone).
        var inverse = world.Inverse();
        return new V3dBone(FixedString.FromText(name, 24), world.Rotation, inverse.Position, parent);
    }

    private static RfaBoneTrack Track(float weight, Quaternion active, Vector3 position) =>
        new(weight, [RfaRotKey.Quantize(0, Quat.Conj(active))], [RfaPosKey.Constant(0, position)]);

    // ── Skeleton ─────────────────────────────────────────────────────────────

    [Fact]
    public void SkeletonDecodesTheInverseBindIntoRestWorldsAndLocals()
    {
        var rootWorld = new Rigid(Quat.FromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(0, 1, 0));
        var childWorld = rootWorld.Compose(new Rigid(Quat.FromAxisAngle(Vector3.UnitX, 0.3f), new Vector3(0, 0.5f, 0.1f)));
        // The child is stored first and points forward at its parent.
        var skeleton = Skeleton.FromBones([Bone("child", 1, childWorld), Bone("root", -1, rootWorld)]);

        Near(childWorld.Position, skeleton.RestWorld[0].Position);
        Near(rootWorld.Position, skeleton.RestWorld[1].Position);
        Near(new Vector3(0, 0.5f, 0.1f), skeleton.RestLocal[0].Position);
        Assert.Equal(0f, Quat.AngleDegrees(Quat.FromAxisAngle(Vector3.UnitX, 0.3f), skeleton.RestLocal[0].Rotation), 2);
        Assert.Equal([1, 0], skeleton.EvaluationOrder.ToArray());
        Assert.Equal(1, skeleton.IndexOf("ROOT"));

        // Inverse bind times rest world is the identity.
        var id = skeleton.RestWorld[0].Compose(skeleton.InverseBind[0]);
        Near(Vector3.Zero, id.Position);
        Assert.Equal(0f, Quat.AngleDegrees(Quaternion.Identity, id.Rotation), 2);
    }

    // ── Forward kinematics ───────────────────────────────────────────────────

    [Fact]
    public void ForwardKinematicsHandlesParentsAfterChildren()
    {
        // 0 -> parent 2, 1 -> parent 0, 2 root: index order is child, grandchild, root.
        int[] parents = [2, 0, -1];
        var locals = new[]
        {
            new Rigid(Quat.FromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new Vector3(0, 1, 0)),
            new Rigid(Quaternion.Identity, new Vector3(1, 0, 0)),
            new Rigid(Quaternion.Identity, new Vector3(0, 0, 5)),
        };
        var world = new Rigid[3];
        ForwardKinematics.Solve(parents, locals, world);
        Near(new Vector3(0, 0, 5), world[2].Position);
        Near(new Vector3(0, 1, 5), world[0].Position);
        // Bone 0's 90 degree turn about Z moves its child: (1,0,0) becomes (0,1,0).
        Near(new Vector3(0, 2, 5), world[1].Position);
    }

    [Fact]
    public void InvalidParentsAndCyclesBecomeRoots()
    {
        int[] parents = [1, 0, 7, 2, -5];
        var effective = ForwardKinematics.EffectiveParents(parents);
        Assert.Equal(-1, effective[2]);        // out of range
        Assert.Equal(-1, effective[4]);        // negative
        Assert.Contains(-1, new[] { effective[0], effective[1] }); // the 0 <-> 1 cycle is broken
        Assert.Equal(2, effective[3]);
        var order = ForwardKinematics.EvaluationOrder(effective);
        Assert.Equal(5, order.Distinct().Count());
        var world = new Rigid[5];
        ForwardKinematics.Solve(parents, Enumerable.Repeat(new Rigid(Quaternion.Identity, Vector3.UnitX), 5).ToArray(), world);
        Assert.All(world, w => Assert.True(float.IsFinite(w.Position.X)));
    }

    [Fact]
    public void FkOfTheRestLocalsGivesTheRestWorlds()
    {
        var mesh = V3dFormatTests.SampleCharacter();
        var skeleton = Skeleton.FromFile(mesh);
        var pose = new Pose(skeleton);
        for (int i = 0; i < skeleton.Count; i++) Near(skeleton.RestWorld[i].Position, pose.World[i].Position);
    }

    // ── Skinning ─────────────────────────────────────────────────────────────

    [Fact]
    public void SkinningIsTheIdentityInTheBindPoseAndFollowsAMovedBone()
    {
        var mesh = V3dFormatTests.SampleCharacter();
        var skeleton = Skeleton.FromFile(mesh);
        var batch = mesh.Submeshes.Single().Lods[0].Batches[0];
        var skin = new Matrix4x4[skeleton.Count];
        var outPos = new Vector3[batch.VertexCount];
        var outNrm = new Vector3[batch.VertexCount];

        var pose = new Pose(skeleton);
        Skinning.ComputeSkinMatrices(skeleton, pose.World, skin);
        Skinning.Skin(batch.Positions.AsSpan(), batch.Normals.AsSpan(), batch.BoneLinks.AsSpan(), skin, outPos, outNrm);
        for (int v = 0; v < batch.VertexCount; v++)
        {
            Near(batch.Positions[v], outPos[v]);
            Near(batch.Normals[v], outNrm[v]);
        }

        // Move the root (bone 2) up by 1: every vertex rides along, fully or blended.
        pose.Local[2] = pose.Local[2] with { Position = pose.Local[2].Position + Vector3.UnitY };
        pose.SolveWorld();
        Skinning.ComputeSkinMatrices(skeleton, pose.World, skin);
        Skinning.Skin(batch.Positions.AsSpan(), batch.Normals.AsSpan(), batch.BoneLinks.AsSpan(), skin, outPos, outNrm);
        for (int v = 0; v < batch.VertexCount; v++) Near(batch.Positions[v] + Vector3.UnitY, outPos[v]);
    }

    [Fact]
    public void SkinningBlendsWeightsAndIgnoresUnusedSlots()
    {
        var a = Matrix4x4.CreateTranslation(2, 0, 0);
        var b = Matrix4x4.CreateTranslation(0, 4, 0);
        Matrix4x4[] skin = [a, b];
        Vector3[] positions = [Vector3.Zero, Vector3.One, Vector3.UnitZ];
        V3dBoneLink[] links =
        [
            new(128, 127, 0, 0, 0, 1, 0xFF, 0xFF),
            new(0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF),   // nothing usable: unchanged
            new(255, 10, 0, 0, 0, 9, 0xFF, 0xFF),      // bone 9 does not exist: ignored
        ];
        var output = new Vector3[3];
        Skinning.Skin(positions, [], links, skin, output, []);
        Near(new Vector3(2 * 128 / 255f, 4 * 127 / 255f, 0), output[0]);
        Near(Vector3.One, output[1]);
        Near(new Vector3(2, 0, 1), output[2]);
    }

    // ── Blender ──────────────────────────────────────────────────────────────

    private static RfaClip Clip(int start, int end, int rampIn, int rampOut, params RfaBoneTrack[] bones) =>
        new() { StartTime = start, EndTime = end, RampIn = rampIn, RampOut = rampOut, Bones = [.. bones] };

    [Fact]
    public void RampsShapeAnActionsWeightAndStatesIgnoreThem()
    {
        var clip = Clip(160, 1760, 320, 640, Track(10, Quaternion.Identity, Vector3.Zero));
        Assert.Equal(0f, ClipBlender.RampFactor(clip, 100));
        Assert.Equal(0f, ClipBlender.RampFactor(clip, 160));
        Assert.Equal(0.5f, ClipBlender.RampFactor(clip, 320), 5);
        Assert.Equal(1f, ClipBlender.RampFactor(clip, 480));
        Assert.Equal(1f, ClipBlender.RampFactor(clip, 1120));
        Assert.Equal(0.5f, ClipBlender.RampFactor(clip, 1440), 5);
        Assert.Equal(0f, ClipBlender.RampFactor(clip, 1760));
        Assert.Equal(0f, ClipBlender.RampFactor(clip, 2000));

        Assert.Equal(5f, ClipBlender.BoneWeight(clip, 0, 320, isState: false), 4);
        Assert.Equal(10f, ClipBlender.BoneWeight(clip, 0, 320, isState: true));
        Assert.Equal(10f, ClipBlender.BoneWeight(clip, 0, 5000, isState: true));
        Assert.Equal(0f, ClipBlender.BoneWeight(clip, 3, 320, isState: true));

        // No ramps: full weight across the clip, nothing outside it.
        var flat = clip with { RampIn = 0, RampOut = 0 };
        Assert.Equal(1f, ClipBlender.RampFactor(flat, 160));
        Assert.Equal(1f, ClipBlender.RampFactor(flat, 1760));
        Assert.Equal(0f, ClipBlender.RampFactor(flat, 1761));
    }

    [Fact]
    public void PerBoneWeightsDecideWhichClipWinsABone()
    {
        var up = new Vector3(0, 1, 0);
        var side = new Vector3(1, 0, 0);
        var turn = Quat.FromAxisAngle(Vector3.UnitY, 1f);
        // A seated state owns the legs (bone 0), an upper-body action owns the arm (bone 1).
        var state = Clip(160, 960, 0, 0, Track(10, Quaternion.Identity, up), Track(2, Quaternion.Identity, up));
        var action = Clip(160, 960, 0, 0, Track(0, turn, side), Track(8, turn, side));
        var locals = new Rigid[2];
        ClipBlender.Blend(state, 500, action, 500, locals);

        Near(up, locals[0].Position);                       // the action's weight 0 is ignored
        Assert.Equal(0f, Quat.AngleDegrees(Quaternion.Identity, locals[0].Rotation), 2);
        // Bone 1: the action (8) scales the state by (10 - 8) / 10, so 2 * 0.2 = 0.4 against 8.
        float a = 8f / 8.4f;
        Near(up * (1f - a) + side * a, locals[1].Position);
        Assert.Equal(57.2958f * (1f - a), Quat.AngleDegrees(turn, locals[1].Rotation), 1);
    }

    [Fact]
    public void AnActionOfWeightTenReplacesTheStateOnThatBone()
    {
        var state = Clip(160, 960, 0, 0, Track(10, Quaternion.Identity, Vector3.UnitY));
        var action = Clip(160, 960, 0, 0, Track(10, Quat.FromAxisAngle(Vector3.UnitX, 1f), Vector3.UnitX));
        var locals = new Rigid[1];
        ClipBlender.Blend(state, 500, action, 500, locals);
        Near(Vector3.UnitX, locals[0].Position);
        Assert.Equal(0f, Quat.AngleDegrees(Quat.FromAxisAngle(Vector3.UnitX, 1f), locals[0].Rotation), 2);
        Assert.Equal(0f, ClipBlender.StateFactor(new ClipLayer(action, 500, false), 0), 6);
        Assert.Equal(1f, ClipBlender.StateFactor(null, 0));
        Assert.Equal(1f, ClipBlender.StateFactor(new ClipLayer(action, 500, false, Mix: 0f), 0));
    }

    [Fact]
    public void AnActionFadesInOverItsRamp()
    {
        var state = Clip(160, 9600, 0, 0, Track(5, Quaternion.Identity, Vector3.Zero));
        var action = Clip(160, 1760, 800, 0, Track(5, Quaternion.Identity, Vector3.UnitX));
        var locals = new Rigid[1];
        ClipBlender.Blend(state, 400, action, 160, locals);
        Near(Vector3.Zero, locals[0].Position);
        // Ramp half way: the action weighs 2.5 and scales the state's 5 by 0.75.
        ClipBlender.Blend(state, 400, action, 560, locals);
        Near(Vector3.UnitX * (2.5f / (2.5f + 3.75f)), locals[0].Position);
        // Fully in: 5 against 5 * 0.5.
        ClipBlender.Blend(state, 400, action, 1200, locals);
        Near(Vector3.UnitX * (5f / 7.5f), locals[0].Position);
        ClipBlender.Blend(state, 400, action, 5000, locals);   // past the end: gone
        Near(Vector3.Zero, locals[0].Position);
    }

    [Fact]
    public void RampInWinsWhereTheRampsOverlapAndWeightsBelowTheEpsilonAreIgnored()
    {
        // Duration 320 with 320-tick ramps both ways: the engine tests ramp-in first.
        var clip = Clip(160, 480, 320, 320, Track(10, Quaternion.Identity, Vector3.Zero), Track(5e-6f, Quaternion.Identity, Vector3.Zero));
        Assert.Equal(0.75f, ClipBlender.RampFactor(clip, 400), 5);
        Assert.Equal(7.5f, ClipBlender.BoneWeight(clip, 0, 400, isState: false), 4);
        Assert.Equal(0f, ClipBlender.BoneWeight(clip, 1, 400, isState: true));
        Assert.Equal(1e-5f, ClipBlender.BoneWeight(clip with { Bones = [Track(1e-5f, Quaternion.Identity, Vector3.Zero)] }, 0, 400, true));
    }

    [Fact]
    public void ThreeLayersBlendByARunningSlerp()
    {
        var q0 = Quaternion.Identity;
        var q1 = Quat.FromAxisAngle(Vector3.UnitY, 0.6f);
        var q2 = Quat.FromAxisAngle(Vector3.UnitY, 1.2f);
        var a = Clip(160, 960, 0, 0, Track(4, q0, Vector3.Zero));
        var b = Clip(160, 960, 0, 0, Track(4, q1, Vector3.UnitX));
        var c = Clip(160, 960, 0, 0, Track(2, q2, Vector3.UnitZ));
        var locals = new Rigid[1];
        ReadOnlySpan<ClipLayer> layers = [new(a, 300, true), new(b, 300, true), new(c, 300, true)];
        ClipBlender.Blend(layers, locals);
        // Weights 4, 4, 2 (no action): slerp(q0, q1, 4/8) = 0.3 rad, then slerp(that, q2, 2/10).
        Assert.Equal(MathF.Abs(0.3f + (1.2f - 0.3f) * 0.2f) * 57.29578f, Quat.AngleDegrees(q0, locals[0].Rotation), 2);
        Near(new Vector3(0.4f, 0, 0.2f), locals[0].Position);
    }

    [Fact]
    public void EngineSlerpReturnsTheSecondQuaternionWhenNearlyParallel()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitX, 0.001f);      // 0.057 degrees apart
        var b = Quaternion.Identity;
        Assert.Equal(b, ClipBlender.EngineSlerp(a, b, 0.25f));
        var c = Quat.FromAxisAngle(Vector3.UnitX, 0.5f);
        Assert.Equal(0.125f * 57.29578f, Quat.AngleDegrees(b, ClipBlender.EngineSlerp(b, c, 0.25f)), 2);
    }

    [Fact]
    public void BonesNoClipDrivesFallBack()
    {
        var clip = Clip(160, 960, 0, 0, Track(0, Quaternion.Identity, Vector3.UnitX));
        var locals = new Rigid[2];
        var fallback = new[] { new Rigid(Quaternion.Identity, Vector3.UnitZ), new Rigid(Quaternion.Identity, Vector3.UnitY) };
        ReadOnlySpan<ClipLayer> layers = [new ClipLayer(clip, 500, true)];
        ClipBlender.Blend(layers, locals, fallback);
        Near(Vector3.UnitZ, locals[0].Position);
        Near(Vector3.UnitY, locals[1].Position);
    }

    // ── Skeleton matching ────────────────────────────────────────────────────

    [Fact]
    public void ClipFitComparesBoneCounts()
    {
        Assert.Equal(ClipFit.Match, SkeletonMatcher.Check(27, 27));
        Assert.Equal(ClipFit.ClipHasFewerBones, SkeletonMatcher.Check(27, 24));
        Assert.Equal(ClipFit.ClipHasMoreBones, SkeletonMatcher.Check(24, 27));
        var skeleton = Skeleton.FromFile(V3dFormatTests.SampleCharacter());
        Assert.True(SkeletonMatcher.Fits(skeleton, RfaFormatTests.SampleClip()));
    }

    [Fact]
    public void FamiliesGroupIdenticalBoneLists()
    {
        var families = SkeletonMatcher.GroupFamilies(
        [
            new("a.v3c", ["pelvis", "spine"], [-1, 0]),
            new("b.v3c", ["PELVIS", "ult2-bdbn-Spine"], [-1, 0]), // same list: case and exporter prefix ignored
            new("c.v3c", ["spine", "pelvis"], [1, -1]),     // same names, different order
            new("d.v3m", [], []),                            // static meshes are left out
            new("e.v3c", ["pelvis", "spine"], [-1, -1]),     // different parents
        ]);
        Assert.Equal(3, families.Count);
        Assert.Equal(["a.v3c", "b.v3c"], families[0].Members.ToArray());
        Assert.Equal(["c.v3c"], families[1].Members.ToArray());
        Assert.Equal(["e.v3c"], families[2].Members.ToArray());
        Assert.Equal(2, families[0].BoneCount);
    }

    // ── Corpus checks ────────────────────────────────────────────────────────

    [Fact]
    public void Ult2GuardRestLocalsMatchUlt2StandPositionKeys()
    {
        // v3c_skeleton.md: the non-root rest local positions equal the stand clip's position keys to 3
        // decimals, which is what proves the inverse-bind convention. Measured (here and with
        // rfanim.py's bind_local alike): 23 of the 24 non-root bones agree to under 1e-6 m; spine03
        // is 0.0144 m off, so the research note's "every" is one bone too strong (DESIGN.md 9).
        string? meshPath = TestPaths.CorpusFile("ult2_guard.v3c");
        string? clipPath = TestPaths.CorpusFile("ult2_stand.rfa");
        if (meshPath is null || clipPath is null) return;
        var skeleton = Skeleton.FromFile(V3dReader.ReadFile(meshPath));
        var clip = RfaReader.ReadFile(clipPath);
        Assert.Equal(skeleton.Count, clip.BoneCount);
        var mismatched = new List<string>();
        int matched = 0;
        for (int i = 0; i < skeleton.Count; i++)
        {
            if (skeleton.EffectiveParents[i] < 0) continue;
            var rest = skeleton.RestLocal[i].Position;
            Assert.NotEmpty(clip.Bones[i].PositionKeys);
            bool ok = clip.Bones[i].PositionKeys.All(k =>
                MathF.Round(rest.X, 3) == MathF.Round(k.Position.X, 3)
                && MathF.Round(rest.Y, 3) == MathF.Round(k.Position.Y, 3)
                && MathF.Round(rest.Z, 3) == MathF.Round(k.Position.Z, 3));
            if (ok) matched++;
            else mismatched.Add(skeleton.Names[i]);
        }
        Assert.Equal(23, matched);
        Assert.Equal(["ult2-bdbn-spine03"], mismatched);
        int spine = skeleton.IndexOf("ult2-bdbn-spine03");
        Assert.InRange(Vector3.Distance(skeleton.RestLocal[spine].Position, clip.Bones[spine].PositionKeys[0].Position), 0.01f, 0.02f);
    }

    [Fact]
    public void StockFamiliesMatchTheResearchRigs()
    {
        if (TestPaths.Corpus is null) return;
        var members = Directory.EnumerateFiles(TestPaths.Corpus, "*.v3c")
            .Select(p => (Path: p, Probe: V3dProbe.ProbeFile(p)))
            .Select(x => new SkeletonFamilyMember(Path.GetFileNameWithoutExtension(x.Path), x.Probe.BoneNames, x.Probe.BoneParents));
        var families = SkeletonMatcher.GroupFamilies(members);
        var rigA = families.Single(f => f.Members.Contains("ult2_guard", StringComparer.OrdinalIgnoreCase));
        foreach (string mesh in new[] { "miner", "riot_guard", "parker_sci", "multi_guard2" })
            Assert.Contains(mesh, rigA.Members, StringComparer.OrdinalIgnoreCase);
        var rigB = families.Single(f => f.Members.Contains("nurse1", StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain("ult2_guard", rigB.Members, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("masako", rigB.Members, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryStockClipSamplesToFinitePosesOnAMatchingMesh()
    {
        string? meshPath = TestPaths.CorpusFile("ult2_guard.v3c");
        if (meshPath is null) return;
        var skeleton = Skeleton.FromFile(V3dReader.ReadFile(meshPath));
        var pose = new Pose(skeleton);
        int sampled = 0;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus!, "*.rfa"))
        {
            var probe = RfaProbe.ProbeFile(path);
            if (probe.BoneCount != skeleton.Count) continue;
            var clip = RfaReader.ReadFile(path);
            for (int t = clip.StartTime; t <= clip.EndTime; t += Math.Max(1, clip.Duration / 7))
            {
                pose.Sample(clip, t);
                Assert.All(pose.World, w => Assert.True(float.IsFinite(w.Position.X) && float.IsFinite(w.Rotation.W)));
            }
            sampled++;
        }
        Assert.True(sampled > 50);
    }
}
