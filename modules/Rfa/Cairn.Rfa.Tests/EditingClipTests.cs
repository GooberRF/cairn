using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

/// <summary>Header, weights, normalise, minimum keys, version conversion and morph stripping.</summary>
public class EditingClipTests
{
    // ── Header ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetHeaderChangesOnlyTheGivenFields()
    {
        var clip = Make();
        var edited = ClipEdit.SetHeader(clip, new ClipHeaderChange { EndTime = 9000, RampIn = 0, TotalTranslation = Vector3.UnitY });
        Assert.Equal(9000, edited.EndTime);
        Assert.Equal(0, edited.RampIn);
        Assert.Equal(Vector3.UnitY, edited.TotalTranslation);
        Assert.Equal(clip.StartTime, edited.StartTime);
        Assert.Equal(clip.RampOut, edited.RampOut);
        Assert.Equal(clip.TotalRotation, edited.TotalRotation);
        Assert.Equal(clip.Bones, edited.Bones);
        Assert.Contains("before the start", Assert.Throws<ArgumentException>(() => ClipEdit.SetHeader(clip, new ClipHeaderChange { EndTime = 0 })).Message);
        Assert.Throws<ArgumentException>(() => ClipEdit.SetHeader(clip, new ClipHeaderChange { RampOut = -1 }));
        Assert.Throws<ArgumentException>(() => ClipEdit.SetHeader(clip, new ClipHeaderChange { PosReduction = float.NaN }));
    }

    // ── Weights ─────────────────────────────────────────────────────────────────────────────────

    // Rig A's bone list (names and parents as in research/anim_retarget/rigs.md).
    private static readonly string[] RigNames =
    [
        "ult2-bdbn-fingers-l", "ult2-bdbn-fingers-l2", "ult2-bdbn-fingers-r", "ult2-bdbn-fingers-r2", "ult2-bdbn-foot-l",
        "ult2-bdbn-foot-r", "ult2-bdbn-hand-l", "ult2-bdbn-hand-r", "ult2-bdbn-head", "ult2-bdbn-lowerarm-l",
        "ult2-bdbn-lowerarm-r", "ult2-bdbn-lowerleg-l", "ult2-bdbn-lowerleg-r", "ult2-bdbn-pelvis", "ult2-bdbn-root",
        "ult2-bdbn-spine03", "ult2-bdbn-spine04", "ult2-bdbn-thumb-l", "ult2-bdbn-thumb-r", "ult2-bdbn-toes-l",
        "ult2-bdbn-toes-r", "ult2-bdbn-upperarm-l", "ult2-bdbn-upperarm-r", "ult2-bdbn-upperleg-l", "ult2-bdbn-upperleg-r",
    ];

    private static readonly int[] RigParents = [6, 0, 7, 2, 11, 12, 9, 10, 16, 21, 22, 23, 24, 14, -1, 13, 15, 6, 7, 4, 5, 16, 16, 13, 13];

    [Fact]
    public void WeightPresetsSplitRigAIntoUpperAndLowerBody()
    {
        var upper = WeightPreset.UpperBody(RigParents, RigNames);
        var lower = WeightPreset.LowerBody(RigParents, RigNames);
        Assert.Equal([0, 1, 2, 3, 6, 7, 8, 9, 10, 15, 16, 17, 18, 21, 22], upper.ToArray());
        Assert.Equal([4, 5, 11, 12, 13, 14, 19, 20, 23, 24], lower.ToArray());
        Assert.Equal(25, upper.Length + lower.Length);

        // Merc layout: spine03 parented to root; pelvis is then not an ancestor but still lower body.
        var merc = RigParents.ToArray();
        merc[15] = 14;
        Assert.Contains(13, WeightPreset.LowerBody(merc, RigNames));
        Assert.DoesNotContain(13, WeightPreset.UpperBody(merc, RigNames));

        // Fallback by names when nothing is called spine.
        var names = new[] { "root", "hips", "thigh_l", "chest", "neck", "head", "arm_l", "tail" };
        var parents = new[] { -1, 0, 1, 1, 3, 4, 3, 1 };
        Assert.Equal([3, 4, 5, 6], WeightPreset.UpperBody(parents, names).ToArray());
        Assert.Equal([0, 1, 2, 7], WeightPreset.LowerBody(parents, names).ToArray());
        names[3] = "torso_upper";
        names[1] = "pelvis";
        Assert.Equal([3, 4, 5, 6], WeightPreset.UpperBody(parents, names).ToArray());
        Assert.Throws<ArgumentException>(() => WeightPreset.UpperBody([0], names));
    }

    [Fact]
    public void WeightPresetsOnAStockSkeleton()
    {
        if (TestPaths.CorpusFile("ult2_guard.v3c") is not { } path) return;
        var skeleton = Skeleton.FromFile(V3dReader.ReadFile(path));
        var upper = WeightPreset.UpperBody(skeleton).Select(i => skeleton.Names[i]).ToList();
        var lower = WeightPreset.LowerBody(skeleton).Select(i => skeleton.Names[i]).ToList();
        Assert.Contains(upper, n => n.EndsWith("head", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lower, n => n.EndsWith("foot-l", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(skeleton.Count, upper.Count + lower.Count);
    }

    [Fact]
    public void SetBoneWeightsAndSubtree()
    {
        var clip = Make(25);
        var edited = ClipEdit.SetBoneWeights(clip, [1, 3, 3], 10);
        Assert.Equal(10f, edited.Bones[1].Weight);
        Assert.Equal(10f, edited.Bones[3].Weight);
        Assert.Equal(clip.Bones[1].RotationKeys, edited.Bones[1].RotationKeys);
        Assert.Same(clip.Bones[2], edited.Bones[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.SetBoneWeights(clip, [99], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.SetBoneWeights(clip, [1], -1));

        // spine04's subtree: head, both arms, hands, fingers, thumbs.
        var arms = ClipEdit.SetBoneWeightsForSubtree(clip, RigParents, 16, 7, includeRoot: false);
        var set = Enumerable.Range(0, 25).Where(i => arms.Bones[i].Weight == 7f).ToArray();
        Assert.Equal([0, 1, 2, 3, 6, 7, 8, 9, 10, 17, 18, 21, 22], set);
        Assert.Equal(clip.Bones[16].Weight, arms.Bones[16].Weight);
    }

    // ── Normalise ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeLeavesACleanClipAlone()
    {
        var clip = Make();
        var result = ClipEdit.Normalize(clip, null, out var report);
        Assert.Same(clip, result);
        Assert.True(report.IsEmpty);
        Assert.Equal("nothing to fix", report.ToString());
    }

    [Fact]
    public void NormalizeRepairsEveryKindOfProblem()
    {
        var clip = Make();
        var t0 = clip.Bones[0];
        var rot = t0.RotationKeys.ToList();
        // Unsorted, with a duplicate time (the later stored key must win).
        (rot[1], rot[2]) = (rot[2], rot[1]);
        var dup = rot[3] with { EaseIn = 77 };
        rot.Insert(4, dup);
        // Non-unit quaternion, sign flip, pad.
        var k5 = rot[5];
        rot[5] = k5 with { X = (short)(k5.X * 0.9), Y = (short)(k5.Y * 0.9), Z = (short)(k5.Z * 0.9), W = (short)(k5.W * 0.9) };
        rot[0] = rot[0] with { Pad = 3 };
        var flipped = rot[^1];
        rot[^1] = flipped with { X = (short)-flipped.X, Y = (short)-flipped.Y, Z = (short)-flipped.Z, W = (short)-flipped.W };
        // A key past the end, and a position key with zero control points.
        rot.Add(ClipEdit.QuantizeRotation(End + 500, Quaternion.Identity, rot[^1]));
        var pos = t0.PositionKeys.ToList();
        pos[2] = pos[2] with { InControl = Vector3.Zero, OutControl = Vector3.Zero };
        var broken = clip with
        {
            Bones = clip.Bones
                .SetItem(0, t0 with { RotationKeys = [.. rot], PositionKeys = [.. pos] })
                .SetItem(1, clip.Bones[1] with { RotationKeys = [], PositionKeys = [clip.Bones[1].PositionKeys[2]] }),
        };

        var fixedClip = ClipEdit.Normalize(broken, null, out var report);
        AssertStructural(fixedClip);
        Assert.Equal(1, report.ReorderedTracks);
        Assert.Equal(1, report.DuplicateKeysDropped);
        Assert.Equal(1, report.KeysOutsideRangeDropped);
        Assert.Equal(0, report.BoundaryKeysInserted); // there already is a key at End
        Assert.Equal(1, report.QuaternionsRequantized);
        Assert.True(report.SignsFlipped >= 1);
        Assert.Equal(1, report.PadsZeroed);
        Assert.Equal(2, report.ControlPointsFixed);
        // Bone 1: two held rotation keys (start and end, never a lone one) and one more position key.
        Assert.Equal(4, report.MinimumKeysAdded);
        Assert.False(report.IsEmpty);
        Assert.Contains("tracks re-sorted", report.ToString());

        var r = fixedClip.Bones[0].RotationKeys;
        Assert.Contains(r, k => k.Time == dup.Time && k.EaseIn == 77);
        Assert.Equal(End, r[^1].Time);
        var p = fixedClip.Bones[0].PositionKeys[2];
        Assert.Equal(p.Position + (fixedClip.Bones[0].PositionKeys[1].Position - p.Position) / 3f, p.InControl);
        Assert.Equal(Quaternion.Identity, ClipEdit.KeyRotation(fixedClip.Bones[1].RotationKeys[0]));
        Assert.Equal([Start, PosTimes[2], End], fixedClip.Bones[1].PositionKeys.Select(k => k.Time).ToArray());
        Assert.All(fixedClip.Bones[1].PositionKeys, k => Assert.Equal(clip.Bones[1].PositionKeys[2].Position, k.Position));
        // Untouched bones are untouched.
        Assert.Same(broken.Bones[2], fixedClip.Bones[2]);
    }

    [Fact]
    public void NormalizeOptionsSwitchRepairsOff()
    {
        var clip = Make();
        var k = clip.Bones[0].RotationKeys[1];
        var broken = ClipEdit.WithRotationKeys(clip, 0, clip.Bones[0].RotationKeys.SetItem(1, k with { Pad = 9 }));
        Assert.Same(broken, ClipEdit.Normalize(broken, new NormalizeOptions { ZeroPad = false }));
        Assert.Equal(0, ClipEdit.Normalize(broken).Bones[0].RotationKeys[1].Pad);
    }

    [Fact]
    public void EnsureMinimumKeysUsesTheSkeletonRestPose()
    {
        var rest = Quat.FromEulerDegrees(new Vector3(0, 90, 0));
        var bones = new[]
        {
            new V3dBone(FixedString.FromText("root", V3dBone.NameSize), Quaternion.Identity, Vector3.Zero, -1),
            new V3dBone(FixedString.FromText("child", V3dBone.NameSize), rest, new Vector3(0, -1, 0), 0),
        };
        var skeleton = Skeleton.FromBones(bones);
        var clip = new RfaClip { StartTime = Start, EndTime = End, Bones = [new RfaBoneTrack(1, [], []), new RfaBoneTrack(1, [], [])] };
        var fixedClip = ClipEdit.EnsureMinimumKeys(clip, skeleton);
        AssertStructural(fixedClip);
        Assert.InRange(Angle(skeleton.RestLocal[1].Rotation, ClipEdit.KeyRotation(fixedClip.Bones[1].RotationKeys[0])), 0f, 0.02f);
        Assert.Equal(skeleton.RestLocal[1].Position, fixedClip.Bones[1].PositionKeys[0].Position);
        Assert.Same(fixedClip, ClipEdit.EnsureMinimumKeys(fixedClip, skeleton));
    }

    // ── Version / morph ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Version7To8To7AgreesWithinOneQuantisationStep()
    {
        var clip = Make(version: 7) with { Morph = MakeV7Morph(6) };
        var v8 = ClipEdit.ConvertVersion(clip, 8);
        Assert.Equal(8, v8.Version);
        Assert.Equal([Start, Start + 853, Start + 1707, Start + 2560, Start + 3413, Start + 4267], v8.Morph.KeyframeTimes.ToArray());
        RfaReader.Read(RfaWriter.Write(v8), "v8");
        Assert.Equal(clip.Bones, v8.Bones);
        AssertMorphClose(clip, v8);

        var back = ClipEdit.ConvertVersion(v8, 7);
        RfaReader.Read(RfaWriter.Write(back), "v7");
        AssertMorphClose(clip, back);
        Assert.Same(clip, ClipEdit.ConvertVersion(clip, 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.ConvertVersion(clip, 6));
    }

    [Fact]
    public void Version8To7To8IsStable()
    {
        var v8 = ClipEdit.ConvertVersion(Make(version: 7) with { Morph = MakeV7Morph(6) }, 8);
        var again = ClipEdit.ConvertVersion(ClipEdit.ConvertVersion(v8, 7), 8);
        Assert.Equal(v8.Morph.KeyframeTimes.ToArray(), again.Morph.KeyframeTimes.ToArray());
        AssertMorphClose(v8, again);
    }

    [Fact]
    public void AVersion8MorphWithTimesButNoVerticesBecomesEmpty()
    {
        var clip = Make() with { Morph = new RfaMorph([], 61, [.. new int[61]], null, [], []) };
        var v7 = ClipEdit.ConvertVersion(clip, 7);
        Assert.True(v7.Morph.IsEmpty);
        RfaWriter.Validate(v7);
        Assert.Equal(61, ClipEdit.ConvertVersion(clip with { Version = 7, Morph = new RfaMorph([], 61, [], null, [], []) }, 8).Morph.KeyframeTimes.Length);
    }

    [Fact]
    public void StripMorphRemovesOnlyTheMorph()
    {
        var clip = Make(version: 7) with { Morph = MakeV7Morph() };
        var stripped = ClipEdit.StripMorph(clip);
        Assert.True(stripped.Morph.IsEmpty);
        Assert.Equal(clip.Bones, stripped.Bones);
        Assert.Same(stripped, ClipEdit.StripMorph(stripped));
    }

    [Fact]
    public void StockMorphClipsSurviveVersionRoundTrips()
    {
        foreach (string name in new[] { "NURS_talk.rfa", "NURS_talk_short.rfa" })
        {
            if (Stock(name) is not { } clip) return;
            Assert.Equal(7, clip.Version);
            var v8 = ClipEdit.ConvertVersion(clip, 8);
            RfaReader.Read(RfaWriter.Write(v8), name);
            var back = ClipEdit.ConvertVersion(v8, 7);
            Assert.Equal(clip.Morph.KeyframeCount, back.Morph.KeyframeCount);
            // Within one quantisation step of the bounds (plus the sub-tick rounding of the v8 times).
            var box = v8.Morph.Bounds!.Value;
            var step = (box.Max - box.Min) / 255f;
            for (int k = 0; k < clip.Morph.KeyframeCount; k++)
            {
                for (int v = 0; v < clip.Morph.VertexCount; v++)
                {
                    var d = Vector3.Abs(clip.Morph.GetPosition(k, v) - back.Morph.GetPosition(k, v));
                    Assert.True(d.X <= step.X * 1.01f + 1e-6f && d.Y <= step.Y * 1.01f + 1e-6f && d.Z <= step.Z * 1.01f + 1e-6f, $"{name} keyframe {k} vertex {v}: {d} vs step {step}");
                }
            }
            // And plays the same in between.
            for (int t = clip.StartTime; t <= clip.EndTime; t += 37)
            {
                var a = ClipEdit.SampleMorph(clip, t);
                var b = ClipEdit.SampleMorph(v8, t);
                for (int v = 0; v < a.Length; v++) Assert.InRange(Vector3.Distance(a[v], b[v]), 0f, step.Length() * 1.01f);
            }
        }
        foreach (string name in new[] { "miner_talk.rfa", "ult2_talk_short.rfa", "admin_fem_talk_short.rfa", "CS6_PARK_Shot01_06.rfa", "mnrm_onbed_talk.rfa" })
        {
            if (Stock(name) is not { } clip) return;
            Assert.Equal(8, clip.Version);
            var v7 = ClipEdit.ConvertVersion(clip, 7);
            RfaReader.Read(RfaWriter.Write(v7), name);
            var again = ClipEdit.ConvertVersion(v7, 8);
            RfaReader.Read(RfaWriter.Write(again), name);
            Assert.Equal(clip.Morph.KeyframeCount, again.Morph.KeyframeCount);
            // The v7 morph plays the v8 one at its own (even) keyframe times.
            for (int k = 0; k < v7.Morph.KeyframeCount; k++)
            {
                double t = clip.StartTime + k * (double)(clip.EndTime - clip.StartTime) / clip.Morph.KeyframeCount;
                var expected = ClipEdit.SampleMorph(clip, t);
                for (int v = 0; v < expected.Length; v++) Assert.InRange(Vector3.Distance(expected[v], v7.Morph.GetPosition(k, v)), 0f, 1e-5f);
            }
        }
        if (Stock("mnrm_onbed_loop.rfa") is { } loop)
        {
            Assert.Equal(61, loop.Morph.KeyframeCount);
            Assert.True(ClipEdit.ConvertVersion(loop, 7).Morph.IsEmpty);
        }
    }

    private static void AssertMorphClose(RfaClip expected, RfaClip actual)
    {
        var box = (actual.Version == 8 ? actual : expected).Morph.Bounds ?? ClipEdit.ConvertVersion(expected, 8).Morph.Bounds!.Value;
        float tolerance = (box.Max - box.Min).Length() / 255f * 1.01f;
        for (int t = expected.StartTime; t <= expected.EndTime; t += 41)
        {
            var a = ClipEdit.SampleMorph(expected, t);
            var b = ClipEdit.SampleMorph(actual, t);
            Assert.Equal(a.Length, b.Length);
            for (int v = 0; v < a.Length; v++) Assert.InRange(Vector3.Distance(a[v], b[v]), 0f, tolerance);
        }
    }
}
