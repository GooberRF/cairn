using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class EditingPoseSkeletonTests
{
    private static readonly Skeleton Rig = PoseFixtures.Skeleton();

    // ── Bone lengths ─────────────────────────────────────────────────────────

    [Fact]
    public void BoneLengthsFromAReferenceClipReplaceNonRootPositionKeys()
    {
        var clip = PoseFixtures.Clip(Rig);
        // Give bone 6 an extra position key so the existing times are kept.
        clip = ClipEdit.WithPositionKeys(clip, 6, [.. clip.Bones[6].PositionKeys.Take(1), RfaPosKey.Constant(640, new Vector3(1, 2, 3)), .. clip.Bones[6].PositionKeys.Skip(1)]);
        var reference = clip;
        var p4 = new Vector3(0.3f, 0.01f, -0.02f);
        var p6 = new Vector3(0.25f, 0f, 0.01f);
        reference = ClipEdit.WithPositionKeys(reference, 4, [RfaPosKey.Constant(160, p4)]);
        reference = ClipEdit.WithPositionKeys(reference, 6, [RfaPosKey.Constant(320, p6), RfaPosKey.Constant(900, Vector3.One)]);
        reference = ClipEdit.WithPositionKeys(reference, 0, [RfaPosKey.Constant(160, new Vector3(9, 9, 9))]);

        var result = ClipEdit.SetBoneLengthsFromClip(clip, reference, Rig.Parents);
        PoseFixtures.AssertStructure(result);
        Assert.Equal([RfaPosKey.Constant(160, p4), RfaPosKey.Constant(1120, p4)], result.Bones[4].PositionKeys.ToArray());
        Assert.Equal([160, 640, 1120], result.Bones[6].PositionKeys.Select(k => k.Time).ToArray());
        Assert.All(result.Bones[6].PositionKeys, k => Assert.Equal(RfaPosKey.Constant(k.Time, p6), k));
        Assert.Same(clip.Bones[0], result.Bones[0]);                      // the root is never touched
        Assert.Same(clip.Bones[1], result.Bones[1]);                      // unchanged lengths stay as they were
        Assert.True(clip.Bones[4].RotationKeys.SequenceEqual(result.Bones[4].RotationKeys));

        var only6 = ClipEdit.SetBoneLengthsFromClip(clip, reference, Rig.Parents, [6, 0]);
        Assert.Same(clip.Bones[4], only6.Bones[4]);
        Assert.Same(clip.Bones[0], only6.Bones[0]);
        Assert.NotSame(clip.Bones[6], only6.Bones[6]);

        Assert.Throws<ArgumentException>(() => ClipEdit.SetBoneLengthsFromClip(clip, clip with { Bones = clip.Bones.RemoveAt(0) }, Rig.Parents));
    }

    [Fact]
    public void BoneLengthsFromTheBindUseTheRestLocals()
    {
        var clip = PoseFixtures.Clip(Rig);
        clip = ClipEdit.OffsetBones(clip, [4, 5, 6], BoneOffset.Move(new Vector3(0.05f, 0, 0)));
        var result = ClipEdit.SetBoneLengthsFromBind(clip, Rig);
        PoseFixtures.AssertStructure(result);
        for (int b = 1; b < Rig.Count; b++)
            Assert.All(result.Bones[b].PositionKeys, k => Assert.Equal(Rig.RestLocal[b].Position, k.Position));
        Assert.Same(clip.Bones[0], result.Bones[0]);
    }

    // ── Conform ──────────────────────────────────────────────────────────────

    private static Skeleton Reordered(out int[] order)
    {
        // Same rig in another bone order, without the head and with a new tail under the pelvis.
        order = [9, 1, 7, 0, 5, 2, 8, 6, 4];   // old index of each new bone (head = 3 dropped)
        var map = order;
        var bones = new List<V3dBone>();
        var oldToNew = new int[Rig.Count];
        Array.Fill(oldToNew, -1);
        for (int n = 0; n < map.Length; n++) oldToNew[map[n]] = n;
        for (int n = 0; n < map.Length; n++)
        {
            int oldParent = Rig.Parents[map[n]];
            bones.Add(PoseFixtures.Bone(PoseFixtures.Names[map[n]], oldParent < 0 ? -1 : oldToNew[oldParent], Rig.RestWorld[map[n]]));
        }
        var tailWorld = Rig.RestWorld[1].Compose(new Rigid(Quat.FromAxisAngle(Vector3.UnitX, 2.5f), new Vector3(0, -0.05f, -0.15f)));
        bones.Add(PoseFixtures.Bone("TAIL", oldToNew[1], tailWorld));
        return Skeleton.FromBones(bones);
    }

    [Fact]
    public void ConformReordersDropsAndAddsTracksByName()
    {
        var clip = PoseFixtures.Clip(Rig);
        var target = Reordered(out var order);
        var result = ClipEdit.ConformToSkeleton(clip, PoseFixtures.Names, target);
        PoseFixtures.AssertStructure(result.Clip);
        Assert.Equal(target.Count, result.Clip.BoneCount);
        Assert.Equal([.. order, -1], result.SourceOfTarget.ToArray());
        Assert.Equal(["head"], result.DroppedBones.ToArray());
        Assert.Equal(["TAIL"], result.AddedBones.ToArray());
        for (int n = 0; n < order.Length; n++) Assert.Same(clip.Bones[order[n]], result.Clip.Bones[n]);

        var tail = result.Clip.Bones[^1];
        Assert.Equal(clip.Bones[1].Weight, tail.Weight);   // nearest mapped ancestor: the pelvis
        // A held rest rotation at start and end (never a lone key: RFA015).
        Assert.Equal([PoseFixtures.Start, PoseFixtures.End], tail.RotationKeys.Select(k => k.Time).ToArray());
        Assert.All(tail.RotationKeys, k => Assert.True(PoseFixtures.Angle(target.RestLocal[^1].Rotation, ClipEdit.KeyRotation(k)) < 0.02f));
        Assert.Equal([PoseFixtures.Start, PoseFixtures.End], tail.PositionKeys.Select(k => k.Time).ToArray());
        Assert.All(tail.PositionKeys, k => Assert.Equal(RfaPosKey.Constant(k.Time, target.RestLocal[^1].Position), k));
        Assert.Equal(clip.StartTime, result.Clip.StartTime);
        Assert.Equal(clip.RampIn, result.Clip.RampIn);

        // And back: every bone both lists share comes home bit-identically; the head returns at rest.
        var back = ClipEdit.ConformToSkeleton(result.Clip, target.Names, Rig);
        Assert.Equal(["TAIL"], back.DroppedBones.ToArray());
        Assert.Equal(["head"], back.AddedBones.ToArray());
        for (int b = 0; b < Rig.Count; b++)
        {
            if (b != 3) Assert.True(PoseFixtures.SameTrack(clip.Bones[b], back.Clip.Bones[b]), $"bone {b}");
        }
    }

    [Fact]
    public void ConformThereAndBackIsTheIdentityForAReordering()
    {
        var clip = PoseFixtures.Clip(Rig);
        int[] order = [3, 0, 9, 8, 1, 2, 7, 6, 5, 4];
        var oldToNew = new int[order.Length];
        for (int n = 0; n < order.Length; n++) oldToNew[order[n]] = n;
        var target = Skeleton.FromBones([.. order.Select(o => PoseFixtures.Bone(
            "ult2-bdbn-" + PoseFixtures.Names[o].ToUpperInvariant(), Rig.Parents[o] < 0 ? -1 : oldToNew[Rig.Parents[o]], Rig.RestWorld[o]))]);
        var there = ClipEdit.ConformToSkeleton(clip, PoseFixtures.Names, target);
        Assert.Empty(there.DroppedBones);
        Assert.Empty(there.AddedBones);
        Assert.Equal(order, there.SourceOfTarget.ToArray());
        var back = ClipEdit.ConformToSkeleton(there.Clip, target.Names, Rig);
        Assert.Equal(clip, back.Clip with { Bones = clip.Bones });
        for (int b = 0; b < clip.BoneCount; b++) Assert.Same(clip.Bones[b], back.Clip.Bones[b]);

        // Canonical matching can be switched off: then the prefixed names find nothing.
        var strict = ClipEdit.ConformToSkeleton(clip, PoseFixtures.Names, target, new ConformOptions(UseCanonicalNames: false));
        Assert.Equal(clip.BoneCount, strict.AddedBones.Length);
        Assert.Throws<ArgumentException>(() => ClipEdit.ConformToSkeleton(clip, ["a"], target));
    }

    [Fact]
    public void StockClipConformsFromRigAToTheFemaleRigAndBack()
    {
        string? a = TestPaths.CorpusFile("ult2_guard.v3c");
        string? b = TestPaths.CorpusFile("nurse1.v3c");
        string? c = TestPaths.CorpusFile("ult2_stand.rfa");
        if (a is null || b is null || c is null) return;
        var rigA = Skeleton.FromFile(V3dReader.ReadFile(a));
        var rigB = Skeleton.FromFile(V3dReader.ReadFile(b));
        var clip = RfaReader.ReadFile(c);
        var there = ClipEdit.ConformToSkeleton(clip, rigA.Names, rigB);
        Assert.Empty(there.DroppedBones);
        Assert.Empty(there.AddedBones);
        Assert.Equal(clip.Bones[1], there.Clip.Bones[3]);   // fingers-l2 moves from index 1 to 3
        var back = ClipEdit.ConformToSkeleton(there.Clip, rigB.Names, rigA);
        for (int i = 0; i < clip.BoneCount; i++) Assert.Same(clip.Bones[i], back.Clip.Bones[i]);
    }
}
