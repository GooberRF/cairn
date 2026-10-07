using System.Numerics;
using System.Text.Json;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class EditingPoseMirrorTests
{
    private static readonly Skeleton Rig = PoseFixtures.Skeleton();

    private static readonly BonePairMap RigPairs = BonePairs.Detect(PoseFixtures.Names);

    // ── Pair detection ───────────────────────────────────────────────────────

    [Fact]
    public void DetectorHandlesTheCommonSideSpellings()
    {
        string[] names =
        [
            "ult2-bdbn-hand-l", "ult2-bdbn-hand-r",           // 0 1  -l / -r suffix
            "ult2-bdbn-fingers-l2", "ult2-bdbn-fingers-r2",   // 2 3  -l2 / -r2
            "tech- arm-l-upper", "tech- arm-R-upper",         // 4 5  infix, case-insensitive
            "tech- foot-l-toes", "tech- foot-r-toes",         // 6 7
            "Thigh_L", "Thigh_R",                             // 8 9  _l / _r
            "LeftShoulder", "RightShoulder",                  // 10 11 left / right (camel case)
            "Bip01 L Calf", "Bip01 R Calf",                   // 12 13 L / R word tokens
            "hand-bdbn-thumb-la", "hand-bdbn-thumb-ra",       // 14 15 side plus segment letter
            "bdbn-r-hand", "bdbn-hand-l",                     // 16 17 side token in different places
            "ult2-bdbn-root", "ult2-bdbn-pelvis", "tech- 1spine", "ult2-bdbn-head", // 18-21 centre
            "lights", "rod", "solo-l",                        // 22-24 not sides / no partner
        ];
        var map = BonePairs.Detect(names);
        for (int i = 0; i < 18; i += 2) Assert.Equal(i + 1, map.PartnerOf(i));
        for (int i = 18; i < names.Length; i++) Assert.Equal(i, map.PartnerOf(i));
    }

    [Fact]
    public void AmbiguousCandidatesStayUnpaired()
    {
        var map = BonePairs.Detect(["arm-l", "arm-l", "arm-r", "leg-l", "leg-r"]);
        Assert.Equal([0, 1, 2, 4, 3], map.Partner.ToArray());
    }

    [Theory]
    [InlineData("ult2_guard.v3c", 10)]
    [InlineData("nurse1.v3c", 10)]
    [InlineData("merc_grunt.v3c", 11)]
    [InlineData("tech01.v3c", 11)]
    public void StockHumanRigsPairEveryLeftWithItsRight(string mesh, int pairs)
    {
        string? path = TestPaths.CorpusFile(mesh);
        if (path is null) return;
        var names = V3dProbe.ProbeFile(path).BoneNames;
        var map = BonePairs.Detect(names);
        map.Validate(names.Length);
        int found = 0;
        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i].ToLowerInvariant();
            bool left = name.EndsWith("-l") || name.EndsWith("-l2") || name.Contains("-l-");
            bool right = name.EndsWith("-r") || name.EndsWith("-r2") || name.Contains("-r-");
            if (left)
            {
                found++;
                string partner = names[map.PartnerOf(i)].ToLowerInvariant();
                int at = name.EndsWith("-l2") ? name.Length - 3 : name.EndsWith("-l") ? name.Length - 2 : name.IndexOf("-l-", StringComparison.Ordinal);
                Assert.Equal(name[..at] + "-r" + name[(at + 2)..], partner);
            }
            else if (!right)
            {
                Assert.Equal(i, map.PartnerOf(i));
            }
        }
        Assert.Equal(pairs, found);
        foreach (string centre in new[] { "root", "pelvis", "spine03", "head", "1spine", "1head" })
        {
            int i = names.ToList().FindIndex(n => SkeletonMatcher.CanonicalBoneName(n).Replace("tech- ", "") == centre);
            if (i >= 0) Assert.False(map.IsPaired(i), $"{mesh}: {names[i]} should be a centre bone");
        }
    }

    [Theory]
    [InlineData("bat1.v3c", "wing-l01", "wing-r01")]
    [InlineData("auto_turret.v3c", "barrel-l", "barrel-r")]
    [InlineData("Capek.v3c", "cape-lt", "cape-rt")]
    [InlineData("Big_Snake.v3c", "mand-bot-l", "mand-bot-r")]
    public void NonHumanRigsPairTheirSides(string mesh, string left, string right)
    {
        string? path = TestPaths.CorpusFile(mesh);
        if (path is null) return;
        var names = V3dProbe.ProbeFile(path).BoneNames.ToList();
        var map = BonePairs.Detect(names);
        map.Validate(names.Count);
        int l = names.FindIndex(n => n.EndsWith(left, StringComparison.OrdinalIgnoreCase));
        int r = names.FindIndex(n => n.EndsWith(right, StringComparison.OrdinalIgnoreCase));
        Assert.True(l >= 0 && r >= 0);
        Assert.Equal(r, map.PartnerOf(l));
        int root = names.FindIndex(n => n.EndsWith("root", StringComparison.OrdinalIgnoreCase));
        if (root >= 0) Assert.False(map.IsPaired(root));
    }

    [Fact]
    public void PairMapsEditAndSerialise()
    {
        var map = RigPairs;
        Assert.Equal(5, map.PartnerOf(4));
        Assert.Equal(9, map.PartnerOf(8));
        Assert.False(map.IsPaired(3));

        var edited = map.With(4, 7);       // arm-l-upper <-> arm-r-lower; their old partners are freed
        Assert.Equal(7, edited.PartnerOf(4));
        Assert.Equal(4, edited.PartnerOf(7));
        Assert.Equal(5, edited.PartnerOf(5));
        Assert.Equal(6, edited.PartnerOf(6));
        edited.Validate(map.Count);
        Assert.False(edited.Without(4).IsPaired(7));

        var json = map.ToJson();
        Assert.Contains("\"Partner\"", json);
        Assert.Equal(map, BonePairMap.FromJson(json));
        Assert.Equal(map, JsonSerializer.Deserialize<BonePairMap>(JsonSerializer.Serialize(map)));
        Assert.Throws<FormatException>(() => BonePairMap.FromJson("{\"Partner\":[1,1]}"));
        Assert.Throws<FormatException>(() => BonePairMap.FromJson("{\"Partner\":[5]}"));
        Assert.Throws<FormatException>(() => BonePairMap.FromJson("not json"));
        Assert.Throws<ArgumentException>(() => map.Validate(3));
    }

    // ── Mirror ───────────────────────────────────────────────────────────────

    [Fact]
    public void MirrorWithoutASkeletonReflectsLocalsAndSwapsTracks()
    {
        var clip = PoseFixtures.Clip(Rig);
        var mirrored = ClipEdit.MirrorClip(clip, RigPairs);
        PoseFixtures.AssertStructure(mirrored);
        // plane YZ: q(x, y, z, w) -> (x, -y, -z, w), exact on the stored integers, times and eases kept.
        var src = clip.Bones[5].RotationKeys;
        var dst = mirrored.Bones[4].RotationKeys;
        Assert.Equal(src.Length, dst.Length);
        for (int i = 0; i < src.Length; i++) Assert.Equal(src[i] with { Y = (short)-src[i].Y, Z = (short)-src[i].Z }, dst[i]);
        Assert.Equal(clip.Bones[5].Weight, mirrored.Bones[4].Weight);
        Assert.Equal(clip.Bones[4].Weight, mirrored.Bones[5].Weight);
        // Root travel mirrors across the plane.
        var root = clip.Bones[0].PositionKeys[1];
        Assert.Equal(PoseFixtures.ReflectX(root.Position), mirrored.Bones[0].PositionKeys[1].Position);
        Assert.Equal(PoseFixtures.ReflectX(root.OutControl), mirrored.Bones[0].PositionKeys[1].OutControl);

        var twice = ClipEdit.MirrorClip(mirrored, RigPairs);
        for (int b = 0; b < clip.BoneCount; b++) Assert.True(clip.Bones[b].RotationKeys.SequenceEqual(twice.Bones[b].RotationKeys));
        PoseFixtures.AssertSamplesEqual(clip, twice, 0.01f, 1e-5f);
    }

    [Fact]
    public void MirrorKeepsEachBonesOwnLength()
    {
        var clip = PoseFixtures.Clip(Rig);
        // Make the left upper arm 20% longer than the right one.
        var longer = clip.Bones[6].PositionKeys.Select(k => RfaPosKey.Constant(k.Time, k.Position * 1.2f)).ToArray();
        clip = ClipEdit.WithPositionKeys(clip, 6, [.. longer]);
        foreach (var options in new[] { new MirrorOptions(), new MirrorOptions(MirrorAxis.X, Rig) })
        {
            var mirrored = ClipEdit.MirrorClip(clip, RigPairs, options);
            Assert.Equal(clip.Bones[6].PositionKeys[0].Position.Length(), mirrored.Bones[6].PositionKeys[0].Position.Length(), 5);
            Assert.Equal(clip.Bones[7].PositionKeys[0].Position.Length(), mirrored.Bones[7].PositionKeys[0].Position.Length(), 5);
            PoseFixtures.AssertSamplesEqual(clip, ClipEdit.MirrorClip(mirrored, RigPairs, options), 0.2f, 1e-4f);
        }
    }

    [Fact]
    public void MirrorWithASkeletonMirrorsTheModelSpaceDeltaFromRest()
    {
        var clip = PoseFixtures.Clip(Rig);
        var mirrored = ClipEdit.MirrorClip(clip, RigPairs, new MirrorOptions(MirrorAxis.X, Rig));
        PoseFixtures.AssertStructure(mirrored);
        // The partner's key times and eases are kept.
        Assert.Equal(clip.Bones[7].RotationKeys.Select(k => (k.Time, k.EaseIn, k.EaseOut)), mirrored.Bones[6].RotationKeys.Select(k => (k.Time, k.EaseIn, k.EaseOut)));

        var before = new Pose(Rig);
        var after = new Pose(Rig);
        for (int t = PoseFixtures.Start; t <= PoseFixtures.End; t += 80)
        {
            before.Sample(clip, t);
            after.Sample(mirrored, t);
            for (int i = 0; i < Rig.Count; i++)
            {
                int p = RigPairs.PartnerOf(i);
                var delta = Quat.Mul(before.World[p].Rotation, Quat.Conj(Rig.RestWorld[p].Rotation));
                var expected = Quat.Mul(PoseFixtures.ReflectX(delta), Rig.RestWorld[i].Rotation);
                float angle = PoseFixtures.Angle(expected, after.World[i].Rotation);
                Assert.True(angle < 0.2f, $"bone {i} at {t}: {angle} degrees off the mirrored delta");
            }
        }
        PoseFixtures.AssertSamplesEqual(clip, ClipEdit.MirrorClip(mirrored, RigPairs, new MirrorOptions(MirrorAxis.X, Rig)));
    }

    [Fact]
    public void ASymmetricPoseStaysSymmetric()
    {
        var options = new MirrorOptions(MirrorAxis.X, Rig);
        var clip = PoseFixtures.Clip(Rig);
        // Centre bones at rest, right side = the mirrored left side: a symmetric clip.
        var rest = clip;
        foreach (int c in new[] { 1, 2, 3 })
            rest = ClipEdit.WithRotationKeys(rest, c, [ClipEdit.QuantizeRotation(PoseFixtures.Start, Rig.RestLocal[c].Rotation, null)]);
        rest = ClipEdit.WithTrack(rest, 0, rest.Bones[0] with
        {
            RotationKeys = [ClipEdit.QuantizeRotation(PoseFixtures.Start, Rig.RestLocal[0].Rotation, null)],
            PositionKeys = [RfaPosKey.Constant(PoseFixtures.Start, Rig.RestLocal[0].Position), RfaPosKey.Constant(PoseFixtures.End, Rig.RestLocal[0].Position)],
        });
        var mirroredRest = ClipEdit.MirrorClip(rest, RigPairs, options);
        var symmetric = rest;
        foreach (int r in new[] { 5, 7, 9 }) symmetric = ClipEdit.WithTrack(symmetric, r, mirroredRest.Bones[r]);

        PoseFixtures.AssertSamplesEqual(symmetric, ClipEdit.MirrorClip(symmetric, RigPairs, options));
    }

    [Fact]
    public void MirrorAcrossOtherPlanesIsItsOwnInverse()
    {
        var clip = PoseFixtures.Clip(Rig);
        foreach (var axis in new[] { MirrorAxis.Y, MirrorAxis.Z })
        {
            var options = new MirrorOptions(axis);
            var once = ClipEdit.MirrorClip(clip, BonePairMap.Identity(clip.BoneCount), options);
            PoseFixtures.AssertStructure(once);
            var p = clip.Bones[0].PositionKeys[1].Position;
            var q = once.Bones[0].PositionKeys[1].Position;
            Assert.Equal(axis == MirrorAxis.Y ? -p.Y : -p.Z, axis == MirrorAxis.Y ? q.Y : q.Z);
            PoseFixtures.AssertSamplesEqual(clip, ClipEdit.MirrorClip(once, BonePairMap.Identity(clip.BoneCount), options), 0.01f, 1e-6f);
        }
    }

    [Fact]
    public void MirrorChecksItsInputs()
    {
        var clip = PoseFixtures.Clip(Rig);
        Assert.Throws<ArgumentException>(() => ClipEdit.MirrorClip(clip, BonePairMap.Identity(3)));
        Assert.Throws<ArgumentException>(() => ClipEdit.MirrorClip(clip, RigPairs, new MirrorOptions(MirrorAxis.X, Animation.Skeleton.Empty)));
    }

    [Fact]
    public void StockClipMirrorsTwiceBackToItself()
    {
        string? meshPath = TestPaths.CorpusFile("ult2_guard.v3c");
        string? clipPath = TestPaths.CorpusFile("ult2_stand.rfa");
        if (meshPath is null || clipPath is null) return;
        var skeleton = Skeleton.FromFile(V3dReader.ReadFile(meshPath));
        var clip = RfaReader.ReadFile(clipPath);
        var pairs = BonePairs.Detect(skeleton.Names);
        foreach (var options in new[] { new MirrorOptions(), new MirrorOptions(MirrorAxis.X, skeleton) })
        {
            var once = ClipEdit.MirrorClip(clip, pairs, options);
            PoseFixtures.AssertStructure(once);
            PoseFixtures.AssertSamplesEqual(clip, ClipEdit.MirrorClip(once, pairs, options));
        }
    }
}
