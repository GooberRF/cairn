using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Every ClipEdit operation's result passes the linter's structural rules (no errors, and none of the
/// warnings an edit could introduce), and every Edit quick fix removes the problem it is offered for.
/// </summary>
public class EditingLintInvariantTests
{
    private static readonly string[] EditWarnings =
        [ClipRules.ZeroControlPoints, ClipRules.NonUnitQuaternion, ClipRules.SegmentSnaps, ClipRules.NegativeRamp];

    private static void AssertClean(string op, RfaClip clip)
    {
        var bad = ClipLinter.AnalyzeStructure(clip)
            .Where(d => d.Severity == DiagnosticSeverity.Error || EditWarnings.Contains(d.Code))
            .ToList();
        Assert.True(bad.Count == 0, $"{op}: {string.Join(" | ", bad)}");
    }

    public static IEnumerable<(string Name, Func<RfaClip, RfaClip> Op)> Operations(Skeleton? skeleton)
    {
        var turn = Quat.FromAxisAngle(Vector3.UnitZ, 0.4f);
        yield return ("SetHeader", c => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = 320, RampOut = 160 }));
        yield return ("SetBoneWeights", c => ClipEdit.SetBoneWeights(c, [0, 1], 5f));
        yield return ("InsertRotationKey", c => ClipEdit.InsertRotationKey(c, 1, Start + 333, turn));
        yield return ("DeleteKeys", c => ClipEdit.DeleteKeys(c, KeySelection.InTimeRange(c, Start + 1, Start + 2000), keepMinimum: true));
        yield return ("MoveKeys", c => ClipEdit.MoveKeys(c, KeySelection.InTimeRange(c, Start + 1, Start + 2000, [1]), 37));
        yield return ("ScaleKeys", c => ClipEdit.ScaleKeys(c, KeySelection.InTimeRange(c, Start + 1, End - 1), Start + 1000, 0.5));
        yield return ("SetRotationEuler", c => ClipEdit.SetRotationEuler(c, KeySelection.Bones(c, [2], positions: false), new Vector3(10, 20, 30)));
        yield return ("SetEases", c => ClipEdit.SetEases(c, KeySelection.All(c, positions: false), 40, 60));
        yield return ("SetPosition", c => ClipEdit.SetPosition(c, KeySelection.Bones(c, [1], rotations: false), new Vector3(0, 0.3f, 0)));
        yield return ("AutoControlPoints", c => ClipEdit.AutoControlPoints(c, KeySelection.All(c, rotations: false), ControlPointMode.Smooth));
        yield return ("KeyPoseAtTime", c => ClipEdit.KeyPoseAtTime(c, Enumerable.Range(0, c.BoneCount), (c.StartTime + c.EndTime) / 2));
        yield return ("Trim", c => ClipEdit.Trim(c, c.StartTime + 160, c.EndTime - 160));
        yield return ("Shift", c => ClipEdit.Shift(c, 480));
        yield return ("Retime", c => ClipEdit.Retime(c, 1.5));
        yield return ("Reverse", ClipEdit.Reverse);
        yield return ("Resample", c => ClipEdit.Resample(c, 320));
        yield return ("ReduceKeys", c => ClipEdit.ReduceKeys(c, new ReduceOptions()).Clip);
        yield return ("Normalize", c => ClipEdit.Normalize(c));
        yield return ("ConvertVersion", c => ClipEdit.ConvertVersion(c, c.Version == 8 ? 7 : 8));
        yield return ("StripMorph", ClipEdit.StripMorph);
        yield return ("RecomputeRange", ClipEdit.RecomputeRange);
        yield return ("OffsetBone local", c => ClipEdit.OffsetBone(c, 1, BoneOffset.Rotate(turn)));
        yield return ("OffsetBone ranged", c => ClipEdit.OffsetBone(c, 1, BoneOffset.Rotate(turn) with { From = c.StartTime + 800, To = c.StartTime + 1600, FalloffTicks = 320 }));
        yield return ("MakeLoopable", c => ClipEdit.MakeLoopable(c, 640));
        yield return ("MirrorClip", c => ClipEdit.MirrorClip(c, BonePairMap.Identity(c.BoneCount)));
        yield return ("ScaleRootMotion", c => ClipEdit.ScaleRootMotion(c, 0, new Vector3(0.5f)));
        yield return ("RemoveRootMotion", c => ClipEdit.RemoveRootMotion(c, 0, RootMotionAxes.Horizontal));
        if (skeleton is not null)
        {
            yield return ("OffsetBone model", c => ClipEdit.OffsetBone(c, 2, BoneOffset.Rotate(turn, OffsetSpace.Model) with { Skeleton = skeleton }));
            yield return ("SetBoneLengthsFromBind", c => ClipEdit.SetBoneLengthsFromBind(c, skeleton));
            yield return ("Mirror with skeleton", c => ClipEdit.MirrorClip(c, BonePairs.Detect(skeleton.Names), new MirrorOptions(MirrorAxis.X, skeleton)));
        }
    }

    [Fact]
    public void EveryOperationOnASyntheticClipPassesTheStructuralLint()
    {
        var clip = Make();
        AssertClean("input", clip);
        foreach (var (name, op) in Operations(null)) AssertClean(name, op(clip));
        var v7 = Make(version: 7) with { Morph = MakeV7Morph() };
        foreach (var (name, op) in Operations(null)) AssertClean(name + " (v7 morph)", op(v7));
    }

    [Fact]
    public void EveryOperationOnStockClipsPassesTheStructuralLint()
    {
        string? meshPath = TestPaths.CorpusFile("ult2_guard.v3c");
        if (meshPath is null) return;
        var skeleton = Skeleton.FromFile(Formats.V3d.V3dReader.ReadFile(meshPath));
        foreach (string name in new[] { "ult2_stand.rfa", "park_jeep_driver.rfa", "ult3_idle_radarlook.rfa" })
        {
            var clip = Stock(name);
            if (clip is null) continue;
            var ops = clip.BoneCount == skeleton.Count ? Operations(skeleton) : Operations(null);
            foreach (var (op, f) in ops) AssertClean($"{name} {op}", f(clip));
        }
    }

    // ── Quick fixes ──────────────────────────────────────────────────────────

    private static void FixRemoves(string code, RfaClip broken, ClipLintContext? context = null)
    {
        var d = ClipLinter.Analyze(broken, context).First(x => x.Code == code);
        var fix = d.QuickFixes.FirstOrDefault(f => f.Kind == QuickFixKind.Edit);
        Assert.NotNull(fix);
        var repaired = fix!.Apply(broken);
        Assert.DoesNotContain(ClipLinter.Analyze(repaired, context), x => x.Code == code);
        Assert.DoesNotContain(ClipLinter.Analyze(repaired, context), x => x.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void EveryEditQuickFixRemovesItsProblem()
    {
        var good = Make();
        var keys = good.Bones[1].RotationKeys;
        var pos = good.Bones[1].PositionKeys;

        FixRemoves(ClipRules.UnsupportedVersion, good with { Version = 9 });
        FixRemoves(ClipRules.NoPositionKeys, ClipEdit.WithPositionKeys(good, 1, []));
        FixRemoves(ClipRules.NoRotationKeys, ClipEdit.WithRotationKeys(good, 1, []));
        FixRemoves(ClipRules.KeyTimesNotIncreasing, ClipEdit.WithRotationKeys(good, 1, [keys[1], keys[0], .. keys.Skip(2)]));
        FixRemoves(ClipRules.KeyOutsideRange, good with { EndTime = End - 100 });
        FixRemoves(ClipRules.EndBeforeStart, good with { StartTime = End + 10 });
        FixRemoves(ClipRules.NotWritable, good with { Morph = new RfaMorph([1], 2, [160], null, [], []) });
        FixRemoves(ClipRules.NonFinite, ClipEdit.WithTrack(good, 0, good.Bones[0] with { Weight = float.NaN }));
        FixRemoves(ClipRules.ZeroControlPoints, ClipEdit.WithPositionKeys(good, 1, pos.SetItem(1, pos[1] with { InControl = Vector3.Zero })));
        FixRemoves(ClipRules.NonUnitQuaternion, ClipEdit.WithRotationKeys(good, 1, keys.SetItem(1, keys[1] with { W = (short)(keys[1].W * 0.98f) })));
        FixRemoves(ClipRules.RampsLongerThanClip, good with { RampIn = 4000, RampOut = 4000 });
        FixRemoves(ClipRules.NegativeRamp, good with { RampIn = -1 });
        FixRemoves(ClipRules.WeightAboveTen, ClipEdit.WithTrack(good, 0, good.Bones[0] with { Weight = 20f }));
        FixRemoves(ClipRules.ZeroLength, good with { EndTime = Start });
        var flipped = keys[1] with { X = (short)-keys[1].X, Y = (short)-keys[1].Y, Z = (short)-keys[1].Z, W = (short)-keys[1].W };
        FixRemoves(ClipRules.SignDiscontinuity, ClipEdit.WithRotationKeys(good, 1, keys.SetItem(1, flipped)));
        FixRemoves(ClipRules.NonZeroPad, ClipEdit.WithRotationKeys(good, 1, keys.SetItem(1, keys[1] with { Pad = 9 })));
        var a = new RfaRotKey(Start, 0, 0, 0, 16384);
        var b = new RfaRotKey(Start + 640, 43, 0, 0, 16384);
        FixRemoves(ClipRules.SegmentSnaps, ClipEdit.WithRotationKeys(good, 1, [a, b, .. keys.Skip(2)]));

        // Proportions: the fix copies the reference's bone lengths.
        var parents = Enumerable.Range(-1, good.BoneCount).ToArray();
        var skeleton = Skeleton.FromBones([.. Enumerable.Range(0, good.BoneCount).Select(i =>
            new Formats.V3d.V3dBone(Cairn.Formats.FixedString.FromText($"b{i}", 24), Quaternion.Identity, Vector3.Zero, parents[i]))]);
        var stretched = ClipEdit.WithPositionKeys(good, 2, [.. good.Bones[2].PositionKeys.Select(k => RfaPosKey.Constant(k.Time, k.Position * 2f + Vector3.UnitY))]);
        FixRemoves(ClipRules.ProportionsDiffer, stretched, new ClipLintContext { Skeleton = skeleton, ReferenceClip = good });
    }
}
