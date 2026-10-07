using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Xunit.Abstractions;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

/// <summary>
/// ClipEdit operations on the stock corpus (read in place; each test passes trivially without it),
/// plus the range clamp on a synthetic clip.
/// </summary>
public class EditingCorpusTests
{
    [Fact]
    public void ClampToRangeKeepsTheMotionInsideTheRange()
    {
        var clip = Make() with { StartTime = Start + 300, EndTime = End - 700 };
        var fixedClip = ClipEdit.Normalize(clip, null, out var report);
        AssertStructural(fixedClip);
        Assert.Equal(2 * 2 * clip.BoneCount, report.BoundaryKeysInserted);
        AssertSameMotion(clip, fixedClip, clip.StartTime, clip.EndTime, step: 4, rotDegrees: 0.015f * 23.5f);
    }
}

/// <summary>Normalize over the whole stock corpus (its own class so xunit runs it beside the others).</summary>
public class EditingCorpusNormalizeTests(ITestOutputHelper output)
{
    [Fact]
    public void NormalizeChangesStockClipsOnlyWhereItReportsAProblem()
    {
        int clips = 0, changed = 0, signOnly = 0;
        var totals = new int[9];
        var notable = new List<string>();
        foreach (var (name, clip, bytes) in StockClips())
        {
            clips++;
            var result = ClipEdit.Normalize(clip, null, out var report);
            if (report.IsEmpty)
            {
                Assert.Same(clip, result);
                continue;
            }
            changed++;
            int[] r =
            [
                report.ReorderedTracks, report.DuplicateKeysDropped, report.KeysOutsideRangeDropped, report.BoundaryKeysInserted,
                report.QuaternionsRequantized, report.SignsFlipped, report.PadsZeroed, report.ControlPointsFixed, report.MinimumKeysAdded,
            ];
            for (int i = 0; i < r.Length; i++) totals[i] += r[i];
            if (r.Where((n, i) => i != 5 && n > 0).Any()) notable.Add($"{name}: {report}");
            else signOnly++;
            Assert.NotEqual(bytes, RfaWriter.Write(result));
            AssertStructural(result);
            // None of the repairs a stock clip needs changes its motion.
            // Sign flips only move samples whose interpolated w is exactly 0 (the engine's w = 0 -> 1
            // rule is not sign-symmetric): two int16 steps. Constant Bezier holds round by ~1e-7 m.
            AssertSameMotion(clip, result, clip.StartTime, clip.EndTime, step: 80, rotDegrees: 0.015f, pos: 1e-6f);
            Assert.Same(result, ClipEdit.Normalize(result));
        }
        if (clips == 0) return;
        output.WriteLine($"{clips} stock clips: {clips - changed} unchanged, {changed} changed ({signOnly} by sign continuity only)");
        output.WriteLine($"totals: reordered {totals[0]}, duplicates {totals[1]}, outside {totals[2]}, boundary {totals[3]}, requantised {totals[4]}, " +
            $"signs {totals[5]}, pads {totals[6]}, control points {totals[7]}, minimum keys {totals[8]}");
        foreach (string n in notable) output.WriteLine(n);
        Assert.Equal(1009, clips);
    }
}

/// <summary>Version conversion and inverse pairs over the whole stock corpus (a class of their own for parallelism).</summary>
public class EditingCorpusInverseTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryStockClipConvertsBetweenVersionsKeepingHeaderAndBones()
    {
        var stats = new Dictionary<int, List<RfaClip>> { [7] = [], [8] = [] };
        foreach (var (name, clip, _) in StockClips())
        {
            stats[clip.Version].Add(clip);
            int other = clip.Version == 7 ? 8 : 7;
            var converted = RfaReader.Read(RfaWriter.Write(ClipEdit.ConvertVersion(clip, other)), name);
            Assert.Equal(other, converted.Version);
            AssertIdentical(clip with { Morph = RfaMorph.Empty }, converted with { Version = clip.Version, Morph = RfaMorph.Empty });
            if (clip.Morph.VertexCount > 0) Assert.Equal(clip.Morph.KeyframeCount, converted.Morph.KeyframeCount);
        }
        foreach (var (version, list) in stats)
        {
            if (list.Count == 0) return;
            output.WriteLine(
                $"v{version}: {list.Count} clips; morph {list.Count(c => !c.Morph.IsEmpty)}; " +
                $"start times {{{string.Join(", ", list.Select(c => c.StartTime).Distinct().Order())}}}; " +
                $"bone counts {{{string.Join(", ", list.Select(c => c.BoneCount).Distinct().Order())}}}; " +
                $"weights {{{string.Join(", ", list.SelectMany(c => c.Bones).Select(b => b.Weight).Distinct().Order())}}}; " +
                $"pos/rot reduction {{{string.Join(", ", list.Select(c => $"{c.PosReduction}/{c.RotReduction}").Distinct().Order())}}}; " +
                $"total rotation identity {list.Count(c => c.TotalRotation == System.Numerics.Quaternion.Identity)}; " +
                $"total translation zero {list.Count(c => c.TotalTranslation == System.Numerics.Vector3.Zero)}; " +
                $"ramped {list.Count(c => c.RampIn != 0 || c.RampOut != 0)}; " +
                $"eased keys {list.Sum(c => c.Bones.Sum(b => b.RotationKeys.Count(k => k.EaseIn != 0 || k.EaseOut != 0)))}; " +
                $"non-zero pads {list.Sum(c => c.Bones.Sum(b => b.RotationKeys.Count(k => k.Pad != 0)))}; " +
                $"e.g. total rotation {string.Join(" ", list.Select(c => c.TotalRotation).Where(q => q != System.Numerics.Quaternion.Identity).Distinct().Take(3))}, " +
                $"total translation {string.Join(" ", list.Select(c => c.TotalTranslation).Where(v => v != System.Numerics.Vector3.Zero).Distinct().Take(3))}");
        }
    }

    [Fact]
    public void InversePairsRestoreStockClipsExactly()
    {
        int i = 0, checkedClips = 0;
        foreach (var (name, clip, bytes) in StockClips())
        {
            i++;
            checkedClips++;
            Assert.Equal(bytes, RfaWriter.Write(ClipEdit.Reverse(ClipEdit.Reverse(clip))));
            Assert.Equal(bytes, RfaWriter.Write(ClipEdit.Shift(ClipEdit.Shift(clip, 4321), -4321)));
            Assert.Equal(bytes, RfaWriter.Write(ClipEdit.Retime(ClipEdit.Retime(clip, 2), 0.5)));
            if (i % 10 == 1)
            {
                var reversed = ClipEdit.Reverse(clip);
                AssertSameMotion(clip, reversed, clip.StartTime, clip.EndTime, step: 160, rotDegrees: 0.05f, pos: 1e-5f, map: t => clip.StartTime + clip.EndTime - t);
            }
            Assert.False(string.IsNullOrEmpty(name));
        }
        output.WriteLine($"{checkedClips} stock clips checked");
    }
}

/// <summary>Trim, key, resample and reduce on a few stock clips (a class of their own for parallelism).</summary>
public class EditingCorpusMotionTests(ITestOutputHelper output)
{
    [Fact]
    public void EditsOnStockClipsKeepTheirMotion()
    {
        string[] names = ["ult2_stand.rfa", "NURS_talk.rfa", "mrc2_fire_Rcharge.rfa", "miner_talk.rfa", "CS6_PARK_Shot01_06.rfa"];
        foreach (string name in names)
        {
            if (Stock(name) is not { } clip) return;
            int start = clip.StartTime, end = clip.EndTime, mid = (start + end) / 2;
            var tidy = ClipEdit.EnsureMinimumKeys(clip);

            // Trim to the middle half.
            int from = start + (end - start) / 4 + 7, to = end - (end - start) / 4 - 3;
            var trimmed = ClipEdit.Trim(tidy, from, to);
            AssertSameMotion(clip, trimmed, from, to, step: 16, rotDegrees: 0.2f, pos: 1e-4f);

            // Key the pose of every bone mid-clip.
            var keyed = ClipEdit.KeyPoseAtTime(clip, Enumerable.Range(0, clip.BoneCount), mid + 13);
            AssertSameMotion(clip, keyed, start, end, step: 16, rotDegrees: 0.2f, pos: 1e-4f);

            // Bake to 30 fps, then reduce with an error bound verified independently.
            var baked = ClipEdit.Resample(clip, RfaClip.TicksPerFrame);
            var result = ClipEdit.ReduceKeys(baked, new ReduceOptions { RotationToleranceDegrees = 0.25f, PositionTolerance = 0.001f, CheckStepTicks = 32 });
            var (rot, pos) = MaxDifference(baked, result.Clip, start, end, 32);
            Assert.InRange(rot, 0f, 0.25f + 1e-4f);
            Assert.InRange(pos, 0f, 0.001f + 1e-7f);
            RfaReader.Read(RfaWriter.Write(result.Clip), name);
            output.WriteLine($"{name}: reduce {result.KeysBefore} -> {result.KeysAfter} keys, max error {result.MaxRotationErrorDegrees:0.000} deg / {result.MaxPositionError * 1000:0.000} mm");

            // Reduce the original directly too (its keys are the exporter's own).
            var direct = ClipEdit.ReduceKeys(clip, new ReduceOptions());
            (rot, pos) = MaxDifference(clip, direct.Clip, start, end, 16);
            Assert.InRange(rot, 0f, 0.1f + 1e-4f);
            Assert.InRange(pos, 0f, 0.0005f + 1e-7f);
            output.WriteLine($"{name}: reduce original {direct.KeysBefore} -> {direct.KeysAfter} keys");
        }
    }
}
