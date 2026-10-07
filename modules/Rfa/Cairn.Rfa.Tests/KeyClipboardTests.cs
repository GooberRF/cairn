using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class KeyClipboardTests
{
    private static readonly Skeleton Rig = PoseFixtures.Skeleton();

    private static void AssertSameContent(KeyClipboard expected, KeyClipboard actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.SourceTime, actual.SourceTime);
        Assert.Equal(expected.Bones.Length, actual.Bones.Length);
        for (int i = 0; i < expected.Bones.Length; i++)
        {
            var a = expected.Bones[i];
            var b = actual.Bones[i];
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Index, b.Index);
            Assert.Equal(a.RotationKeys.ToArray(), b.RotationKeys.ToArray());
            Assert.Equal(a.PositionKeys.ToArray(), b.PositionKeys.ToArray());
        }
    }

    [Fact]
    public void CopyKeepsRawKeysAtRelativeTimesAndSurvivesJson()
    {
        var clip = PoseFixtures.Clip(Rig);
        var selection = KeySelection.InTimeRange(clip, 480, 1120, [0, 4, 6]);
        var copied = KeyClipboard.Copy(clip, selection, PoseFixtures.Names);
        Assert.Equal(480, copied.SourceTime);
        Assert.Equal(KeyClipboardKind.Keys, copied.Kind);
        Assert.Equal(["root", "arm-l-upper", "arm-l-lower"], copied.Bones.Select(b => b.Name ?? "").ToArray());
        var arm = copied.Bones[1];
        Assert.Equal([0, 320, 640], arm.RotationKeys.Select(k => k.Time).ToArray());
        Assert.Equal(clip.Bones[4].RotationKeys[1] with { Time = 0 }, arm.RotationKeys[0]);
        Assert.Equal([640], arm.PositionKeys.Select(k => k.Time).ToArray());
        Assert.Equal(selection.Count, copied.KeyCount);

        string json = copied.ToJson();
        Assert.Contains("\"format\":\"rfaworkbench-keys\"", json);
        Assert.Contains("\"version\":1", json);
        Assert.Contains("\"rotationKeys\"", json);
        AssertSameContent(copied, KeyClipboard.FromJson(json));

        var pose = KeyClipboard.CopyPose(clip, 700, null);
        AssertSameContent(pose, KeyClipboard.FromJson(pose.ToJson()));
        Assert.True(KeyClipboard.Copy(clip, KeySelection.Empty, null).IsEmpty);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"format\":\"something-else\",\"version\":1,\"kind\":\"keys\",\"bones\":[]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":2,\"kind\":\"keys\",\"bones\":[]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":1,\"kind\":\"curves\",\"bones\":[]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":1,\"kind\":\"keys\",\"bones\":[{\"index\":-1}]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":1,\"kind\":\"keys\",\"bones\":[{\"index\":0,\"rotationKeys\":[{\"time\":5,\"w\":16383},{\"time\":5,\"w\":16383}]}]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":1,\"kind\":\"keys\",\"bones\":[{\"index\":0,\"positionKeys\":[{\"time\":0,\"position\":[1,2],\"inControl\":[0,0,0],\"outControl\":[0,0,0]}]}]}")]
    [InlineData("{\"format\":\"rfaworkbench-keys\",\"version\":1,\"kind\":\"keys\",\"bones\":[{\"index\":0,\"rotationKeys\":[{\"time\":0,\"x\":99999}]}]}")]
    public void BadClipboardTextIsRejectedPlainly(string json)
    {
        var ex = Assert.Throws<FormatException>(() => KeyClipboard.FromJson(json));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void PasteMatchesBonesByNameOnAReorderedClip()
    {
        var source = PoseFixtures.Clip(Rig);
        var copied = KeyClipboard.Copy(source, KeySelection.Bones(source, [4, 8]), PoseFixtures.Names);

        // Target: another clip whose bones are in a different order and carry the exporter prefix.
        int[] order = [8, 4, 0, 1, 2, 3, 5, 6, 7, 9];
        var other = PoseFixtures.Clip(Rig, seed: 99);
        var target = other with { Bones = [.. order.Select(o => other.Bones[o])] };
        string[] targetNames = [.. order.Select(o => "ult2-bdbn-" + PoseFixtures.Names[o])];

        var result = copied.Paste(target, 2000, targetNames);
        PoseFixtures.AssertStructure(result.Clip);
        Assert.Empty(result.UnmatchedBones);
        Assert.Equal(160 + 2000 - 160 + 960, result.Clip.EndTime);   // keys up to 2960: the range grew
        Assert.Equal(target.StartTime, result.Clip.StartTime);

        // arm-l-upper (source 4) landed on target bone 1, leg-l-upper (source 8) on target bone 0.
        foreach (var (src, dst) in new[] { (4, 1), (8, 0) })
        {
            var pasted = result.Clip.Bones[dst].RotationKeys.Where(k => k.Time >= 2000).ToArray();
            Assert.Equal(source.Bones[src].RotationKeys.Select(k => k.Time - 160 + 2000), pasted.Select(k => k.Time));
            for (int i = 0; i < pasted.Length; i++)
                Assert.True(PoseFixtures.Angle(ClipEdit.KeyRotation(source.Bones[src].RotationKeys[i]), ClipEdit.KeyRotation(pasted[i])) < 1e-3f);
            // The target's own keys before the paste are untouched.
            Assert.True(target.Bones[dst].RotationKeys.SequenceEqual(result.Clip.Bones[dst].RotationKeys.Where(k => k.Time < 2000)));
        }
        for (int b = 2; b < target.BoneCount; b++) Assert.Same(target.Bones[b], result.Clip.Bones[b]);

        // The returned selection addresses exactly the pasted keys.
        Assert.Equal(copied.KeyCount, result.Pasted.Count);
        Assert.All(result.Pasted.Keys, k => Assert.True(k.TimeIn(result.Clip) >= 2000));
    }

    [Fact]
    public void PastedKeysReplaceKeysAtTheSameTimeAndAreReSignedExactly()
    {
        var clip = PoseFixtures.Clip(Rig);
        // A copied key stored in the opposite hemisphere from its new neighbour.
        var key = ClipEdit.QuantizeRotation(0, Quat.Mul(ClipEdit.KeyRotation(clip.Bones[2].RotationKeys[1]), Quat.FromAxisAngle(Vector3.UnitY, 0.1f)), null);
        var flipped = key with { X = (short)-key.X, Y = (short)-key.Y, Z = (short)-key.Z, W = (short)-key.W, EaseIn = 5 };
        if (Quat.Dot(flipped.FileQuaternion, clip.Bones[2].RotationKeys[0].FileQuaternion) >= 0f) flipped = key with { EaseIn = 5 };
        var board = new KeyClipboard { Bones = [new KeyClipboardBone("spine", 2, [flipped], [])] };

        var result = board.Paste(clip, 480, PoseFixtures.Names);
        PoseFixtures.AssertStructure(result.Clip);
        var keys = result.Clip.Bones[2].RotationKeys;
        Assert.Equal(clip.Bones[2].RotationKeys.Length, keys.Length);       // replaced, not added
        var pasted = keys[1];
        Assert.Equal(480, pasted.Time);
        Assert.Equal(5, pasted.EaseIn);
        Assert.Equal(-flipped.X, pasted.X);                                  // exact integer negation
        Assert.Equal(-flipped.W, pasted.W);
        Assert.Equal(clip.Bones[2].RotationKeys[0], keys[0]);
        Assert.Equal(clip.Bones[2].RotationKeys[2], keys[2]);
    }

    [Fact]
    public void RangePolicyExtendsOrClips()
    {
        var clip = PoseFixtures.Clip(Rig);
        var board = KeyClipboard.Copy(clip, KeySelection.Bones(clip, [1]), PoseFixtures.Names);
        var extended = board.Paste(clip, 800, PoseFixtures.Names);
        Assert.Equal(800 + 960, extended.Clip.EndTime);
        var clipped = board.Paste(clip, 800, PoseFixtures.Names, new PasteOptions(Range: PasteRangePolicy.Clip));
        PoseFixtures.AssertStructure(clipped.Clip);
        Assert.Equal(clip.EndTime, clipped.Clip.EndTime);
        Assert.Equal([160, 480, 800, 1120], clipped.Clip.Bones[1].RotationKeys.Select(k => k.Time).ToArray());
        Assert.All(clipped.Pasted.Keys, k => Assert.InRange(k.TimeIn(clipped.Clip), 800, 1120));
        var early = board.Paste(clip, 0, PoseFixtures.Names);
        Assert.Equal(0, early.Clip.StartTime);
    }

    [Fact]
    public void BonesMatchByIndexWithoutNamesAndUnmatchedOnesAreReported()
    {
        var clip = PoseFixtures.Clip(Rig);
        var board = KeyClipboard.Copy(clip, KeySelection.Bones(clip, [3, 4]), PoseFixtures.Names);
        var byIndex = board.Paste(clip, 160, null);
        Assert.Empty(byIndex.UnmatchedBones);
        Assert.True(byIndex.Clip.Bones[4].RotationKeys.SequenceEqual(clip.Bones[4].RotationKeys));

        string[] renamed = [.. PoseFixtures.Names];
        renamed[3] = "skull";
        var byName = board.Paste(clip, 160, renamed);
        Assert.Equal(["head"], byName.UnmatchedBones.ToArray());
        var forced = board.Paste(clip, 160, renamed, new PasteOptions(MatchByIndex: true));
        Assert.Empty(forced.UnmatchedBones);

        var small = clip with { Bones = clip.Bones.RemoveRange(4, 6) };
        Assert.Equal(["arm-l-upper"], board.Paste(small, 160, null).UnmatchedBones.ToArray());
    }

    [Fact]
    public void AWholePosePastesOntoAnotherClip()
    {
        var source = PoseFixtures.Clip(Rig);
        var target = PoseFixtures.Clip(Rig, seed: 3);
        var pose = KeyClipboard.CopyPose(source, 640, PoseFixtures.Names);
        Assert.Equal(KeyClipboardKind.Pose, pose.Kind);
        Assert.Equal(source.BoneCount, pose.Bones.Length);
        Assert.Equal(source.Bones[3].RotationKeys[1] with { Time = 0, EaseIn = 0, EaseOut = 0 }, pose.Bones[3].RotationKeys[0]); // the head has a key at 640

        var result = pose.Paste(target, 960, PoseFixtures.Names);
        PoseFixtures.AssertStructure(result.Clip);
        for (int b = 0; b < source.BoneCount; b++)
        {
            Assert.True(PoseFixtures.Angle(ClipEdit.SampleRotation(source, b, 640), ClipEdit.SampleRotation(result.Clip, b, 960)) < 0.02f);
            Assert.True(Vector3.Distance(ClipEdit.SamplePosition(source, b, 640), ClipEdit.SamplePosition(result.Clip, b, 960)) < 1e-6f);
        }
        var subset = KeyClipboard.CopyPose(source, 640, null, [2, 5]);
        Assert.Equal([2, 5], subset.Bones.Select(b => b.Index).ToArray());
    }

    [Fact]
    public void MirroredPasteOfAPoseMatchesTheMirroredClip()
    {
        var clip = PoseFixtures.Clip(Rig);
        var pairs = BonePairs.Detect(PoseFixtures.Names);
        foreach (var options in new[] { new MirrorOptions(), new MirrorOptions(MirrorAxis.X, Rig) })
        {
            var mirroredClip = ClipEdit.MirrorClip(clip, pairs, options);
            foreach (int t in new[] { 480, 700 })
            {
                var pose = KeyClipboard.CopyPose(clip, t, PoseFixtures.Names);
                var result = pose.PasteMirrored(clip, t, PoseFixtures.Names, pairs, options);
                PoseFixtures.AssertStructure(result.Clip);
                for (int b = 0; b < clip.BoneCount; b++)
                {
                    float angle = PoseFixtures.Angle(ClipEdit.SampleRotation(mirroredClip, b, t), ClipEdit.SampleRotation(result.Clip, b, t));
                    Assert.True(angle < 0.2f, $"bone {b} at {t}: {angle} degrees");
                    Assert.True(Vector3.Distance(ClipEdit.SamplePosition(mirroredClip, b, t), ClipEdit.SamplePosition(result.Clip, b, t)) < 1e-4f);
                }
            }
        }

        // Mirroring the clipboard itself moves keys to the partner bones (and twice is the identity).
        var keys = KeyClipboard.Copy(clip, KeySelection.Bones(clip, [4]), PoseFixtures.Names);
        var once = keys.Mirror(pairs, null, PoseFixtures.Names);
        Assert.Equal(5, once.Bones.Single().Index);
        Assert.Equal("arm-r-upper", once.Bones.Single().Name);
        AssertSameContent(keys, once.Mirror(pairs, null, PoseFixtures.Names));
    }

    [Fact]
    public void StockKeysPasteAcrossRigsByName()
    {
        string? a = TestPaths.CorpusFile("ult2_guard.v3c");
        string? b = TestPaths.CorpusFile("nurse1.v3c");
        string? c = TestPaths.CorpusFile("ult2_stand.rfa");
        if (a is null || b is null || c is null) return;
        var namesA = V3dProbe.ProbeFile(a).BoneNames;
        var namesB = V3dProbe.ProbeFile(b).BoneNames;
        var clip = RfaReader.ReadFile(c);
        // fingers-l2 is bone 1 in rig A and bone 3 in the female rig.
        var board = KeyClipboard.FromJson(KeyClipboard.Copy(clip, KeySelection.Bones(clip, [1]), namesA).ToJson());
        var target = clip with { Bones = [.. clip.Bones.Select(t => t with { RotationKeys = [t.RotationKeys[0]] })] };
        var result = board.Paste(target, clip.StartTime, namesB);
        Assert.Empty(result.UnmatchedBones);
        Assert.True(result.Clip.Bones[3].RotationKeys.SequenceEqual(clip.Bones[1].RotationKeys));
    }
}
