using System.Numerics;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;
using static Cairn.Rfa.Tests.EditingTestClips;

namespace Cairn.Rfa.Tests;

public class EditingKeyTests
{
    private static KeyRef Rot(int bone, int index) => new(bone, KeyKind.Rotation, index);

    private static KeyRef Pos(int bone, int index) => new(bone, KeyKind.Position, index);

    private static void AssertOtherBonesUntouched(RfaClip before, RfaClip after, params int[] edited)
    {
        for (int b = 0; b < before.BoneCount; b++)
        {
            if (edited.Contains(b)) continue;
            Assert.Same(before.Bones[b], after.Bones[b]);
        }
    }

    // ── Insert / replace / delete ───────────────────────────────────────────────────────────────

    [Fact]
    public void InsertRotationKeyQuantisesOnceAndLeavesTheOtherKeysAlone()
    {
        var clip = Make();
        var q = Quat.FromEulerDegrees(new Vector3(10, 20, 30));
        var edited = ClipEdit.InsertRotationKey(clip, 1, Start + 1000, q, 12, 34);
        var keys = edited.Bones[1].RotationKeys;
        Assert.Equal(clip.Bones[1].RotationKeys.Length + 1, keys.Length);
        var k = keys.Single(x => x.Time == Start + 1000);
        Assert.Equal((sbyte)12, k.EaseIn);
        Assert.Equal((sbyte)34, k.EaseOut);
        Assert.InRange(Quat.AngleDegrees(q, ClipEdit.SampleRotation(edited, 1, Start + 1000)), 0f, 0.02f);
        Assert.Equal(clip.Bones[1].RotationKeys.Where(x => x.Time != k.Time), keys.Where(x => x.Time != k.Time));
        AssertKeys(clip.Bones[1].PositionKeys, edited.Bones[1].PositionKeys);
        AssertOtherBonesUntouched(clip, edited, 1);
        AssertStructural(edited);

        // At an existing time it replaces.
        var replaced = ClipEdit.InsertRotationKey(clip, 1, RotTimes[2], q);
        Assert.Equal(clip.Bones[1].RotationKeys.Length, replaced.Bones[1].RotationKeys.Length);
        Assert.InRange(Quat.AngleDegrees(q, ClipEdit.KeyRotation(replaced.Bones[1].RotationKeys[2])), 0f, 0.02f);
        AssertStructural(replaced);
    }

    [Fact]
    public void InsertingARawKeyAlignsItsSignExactly()
    {
        var clip = Make();
        var prev = clip.Bones[0].RotationKeys[1];
        var raw = new RfaRotKey(Start + 800, (short)-prev.X, (short)-prev.Y, (short)-prev.Z, (short)-prev.W, 5, 6, 0);
        var edited = ClipEdit.InsertRotationKey(clip, 0, raw, out int index);
        Assert.Equal(2, index);
        var k = edited.Bones[0].RotationKeys[index];
        Assert.Equal(prev with { Time = raw.Time, EaseIn = 5, EaseOut = 6 }, k);
        AssertStructural(edited);
    }

    [Fact]
    public void InsertAndReplacePositionKeys()
    {
        var clip = Make();
        var key = new RfaPosKey(Start + 160, new Vector3(1, 2, 3), new Vector3(1, 2, 2), new Vector3(1, 2, 4));
        var edited = ClipEdit.InsertPositionKey(clip, 2, key, out int index);
        Assert.Equal(1, index);
        Assert.Equal(key, edited.Bones[2].PositionKeys[1]);
        Assert.Equal(clip.Bones[2].PositionKeys.Length + 1, edited.Bones[2].PositionKeys.Length);

        var same = ClipEdit.InsertPositionKey(clip, 2, key with { Time = PosTimes[1] });
        Assert.Equal(clip.Bones[2].PositionKeys.Length, same.Bones[2].PositionKeys.Length);
        Assert.Equal(key.Position, same.Bones[2].PositionKeys[1].Position);

        var moved = ClipEdit.ReplacePositionKey(clip, 2, 1, key);
        Assert.Equal(clip.Bones[2].PositionKeys.Length, moved.Bones[2].PositionKeys.Length);
        Assert.Equal(key, moved.Bones[2].PositionKeys[1]);
        Assert.DoesNotContain(moved.Bones[2].PositionKeys, k => k.Time == PosTimes[1]);
        AssertStructural(moved);

        var inPlace = ClipEdit.ReplaceRotationKey(clip, 2, 3, clip.Bones[2].RotationKeys[3] with { EaseIn = 99 });
        Assert.Equal((sbyte)99, inPlace.Bones[2].RotationKeys[3].EaseIn);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.ReplaceRotationKey(clip, 2, 99, default));
        Assert.Contains("no key 99", ex.Message);
    }

    [Fact]
    public void DeleteKeysRemovesExactlyTheSelection()
    {
        var clip = Make();
        var sel = KeySelection.Of(Rot(0, 1), Rot(0, 3), Pos(1, 2));
        var edited = ClipEdit.DeleteKeys(clip, sel);
        AssertKeys(clip.Bones[0].RotationKeys.RemoveAt(3).RemoveAt(1), edited.Bones[0].RotationKeys);
        AssertKeys(clip.Bones[1].PositionKeys.RemoveAt(2), edited.Bones[1].PositionKeys);
        AssertKeys(clip.Bones[0].PositionKeys, edited.Bones[0].PositionKeys);
        AssertOtherBonesUntouched(clip, edited, 0, 1);
        AssertStructural(edited);
    }

    [Fact]
    public void DeleteKeysCanKeepTheMinimum()
    {
        var clip = Make();
        var all = KeySelection.Bones(clip, [0]);
        var bare = ClipEdit.DeleteKeys(clip, all);
        Assert.Empty(bare.Bones[0].RotationKeys);
        Assert.Empty(bare.Bones[0].PositionKeys);

        var kept = ClipEdit.DeleteKeys(clip, all, keepMinimum: true);
        // Two rotation keys, not one (a lone rotation key makes the engine read past the track, RFA015).
        AssertKeys([clip.Bones[0].RotationKeys[0], clip.Bones[0].RotationKeys[^1]], kept.Bones[0].RotationKeys);
        AssertKeys([clip.Bones[0].PositionKeys[0], clip.Bones[0].PositionKeys[^1]], kept.Bones[0].PositionKeys);
        AssertStructural(kept);
    }

    [Fact]
    public void AStaleSelectionIsRejectedInPlainLanguage()
    {
        var clip = Make();
        var ex = Assert.Throws<ArgumentException>(() => ClipEdit.DeleteKeys(clip, KeySelection.Of(Rot(0, 50))));
        Assert.Contains("does not have", ex.Message);
    }

    // ── Move / scale / set time ─────────────────────────────────────────────────────────────────

    [Fact]
    public void MoveKeysShiftsTimesAndKeepsValues()
    {
        var clip = Make();
        var sel = KeySelection.Of(Rot(1, 2), Rot(1, 3), Pos(1, 2));
        var edited = ClipEdit.MoveKeys(clip, sel, 100, out var moved);
        var before = clip.Bones[1].RotationKeys;
        var after = edited.Bones[1].RotationKeys;
        Assert.Equal(before.Length, after.Length);
        Assert.Equal(before[2] with { Time = before[2].Time + 100 }, after[2]);
        Assert.Equal(before[3] with { Time = before[3].Time + 100 }, after[3]);
        Assert.Equal(before[1], after[1]);
        Assert.Equal(clip.Bones[1].PositionKeys[2] with { Time = PosTimes[2] + 100 }, edited.Bones[1].PositionKeys[2]);
        AssertSelection(sel, moved);
        AssertOtherBonesUntouched(clip, edited, 1);
        AssertStructural(edited);

        // Moving back restores the clip.
        AssertIdentical(clip, ClipEdit.MoveKeys(edited, moved, -100));
    }

    [Fact]
    public void AMovedKeyLandingOnAnUnselectedKeyReplacesIt()
    {
        var clip = Make();
        var edited = ClipEdit.MoveKeys(clip, KeySelection.Of(Rot(0, 2)), RotTimes[3] - RotTimes[2], out var moved);
        var keys = edited.Bones[0].RotationKeys;
        Assert.Equal(clip.Bones[0].RotationKeys.Length - 1, keys.Length);
        Assert.Equal(clip.Bones[0].RotationKeys[2] with { Time = RotTimes[3] }, keys[2]);
        AssertSelection(KeySelection.Of(Rot(0, 2)), moved);
        AssertStructural(edited);
    }

    [Fact]
    public void MovedKeysNeverPassOverUnselectedKeys()
    {
        var clip = Make();
        // Key 1 jumps past keys 2 and 3 to just before key 4: the passed keys merge into the move.
        var edited = ClipEdit.MoveKeys(clip, KeySelection.Of(Rot(0, 1)), RotTimes[4] - 10 - RotTimes[1], out var moved);
        var before = clip.Bones[0].RotationKeys;
        var keys = edited.Bones[0].RotationKeys;
        AssertKeys([before[0], before[1] with { Time = RotTimes[4] - 10 }, before[4], before[5]], keys);
        AssertSelection(KeySelection.Of(Rot(0, 1)), moved);
        AssertStructural(edited);

        // A clamped delta stops one tick short of the neighbour and removes nothing.
        int clamped = ClipEdit.ClampMoveDelta(clip, KeySelection.Of(Rot(0, 1)), 5000);
        Assert.Equal(RotTimes[2] - 1 - RotTimes[1], clamped);
        Assert.Equal(-(RotTimes[1] - RotTimes[0] - 1), ClipEdit.ClampMoveDelta(clip, KeySelection.Of(Rot(0, 1)), -5000));
        Assert.Equal(25, ClipEdit.ClampMoveDelta(clip, KeySelection.Of(Rot(0, 1)), 25));
        Assert.Equal(before.Length, ClipEdit.MoveKeys(clip, KeySelection.Of(Rot(0, 1)), clamped).Bones[0].RotationKeys.Length);
    }

    [Fact]
    public void ScaleKeysAboutAPivotAndReverseWithANegativeFactor()
    {
        var clip = Make();
        var sel = KeySelection.InTimeRange(clip, RotTimes[1], RotTimes[3], [0], positions: false);
        Assert.Equal(3, sel.Count);
        int pivot = RotTimes[1];
        var scaled = ClipEdit.ScaleKeys(clip, sel, pivot, 0.5);
        var keys = scaled.Bones[0].RotationKeys;
        Assert.Equal(pivot + (RotTimes[2] - pivot) / 2, keys[2].Time);
        Assert.Equal(pivot + (RotTimes[3] - pivot) / 2, keys[3].Time);
        Assert.Equal(clip.Bones[0].RotationKeys[4], keys[4]);
        AssertStructural(scaled);

        // Reverse the block about its centre: times mirror, eases swap, order is kept by time.
        int centre = (RotTimes[1] + RotTimes[3]) / 2;
        var reversed = ClipEdit.ScaleKeys(clip, sel, centre, -1, out var result);
        var r = reversed.Bones[0].RotationKeys;
        var o = clip.Bones[0].RotationKeys;
        Assert.Equal(o.Length, r.Length);
        Assert.Equal(RotTimes[1], r[1].Time);
        Assert.Equal(o[3].X, r[1].X);
        Assert.Equal(o[3].EaseOut, r[1].EaseIn);
        Assert.Equal(o[3].EaseIn, r[1].EaseOut);
        Assert.Equal(o[1].X, r[3].X);
        Assert.Equal(3, result.Count);
        AssertStructural(reversed);
        // Reversing again restores the block exactly.
        AssertIdentical(clip, ClipEdit.ScaleKeys(reversed, result, centre, -1));
    }

    [Fact]
    public void ScaleCollisionsKeepTheLaterKey()
    {
        var clip = Make();
        var sel = KeySelection.Bones(clip, [2], positions: false);
        var squashed = ClipEdit.ScaleKeys(clip, sel, Start, 1e-6);
        var keys = squashed.Bones[2].RotationKeys;
        Assert.Single(keys);
        Assert.Equal(clip.Bones[2].RotationKeys[^1] with { Time = Start }, keys[0]);
        Assert.Throws<ArgumentOutOfRangeException>(() => ClipEdit.ScaleKeys(clip, sel, Start, 0));
    }

    [Fact]
    public void SetKeyTimeMovesOneKey()
    {
        var clip = Make();
        var edited = ClipEdit.SetKeyTime(clip, Pos(3, 1), PosTimes[1] + 37);
        Assert.Equal(PosTimes[1] + 37, edited.Bones[3].PositionKeys[1].Time);
        Assert.Equal(clip.Bones[3].PositionKeys[1].Position, edited.Bones[3].PositionKeys[1].Position);
        AssertStructural(edited);
    }

    // ── Values ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SetRotationAndEulerWriteTheSameKeys()
    {
        var clip = Make();
        var sel = KeySelection.Of(Rot(1, 0), Rot(1, 4), Pos(1, 1));
        var euler = new Vector3(-30, 45, 10);
        var a = ClipEdit.SetRotationEuler(clip, sel, euler);
        var b = ClipEdit.SetRotation(clip, sel, Quat.FromEulerDegrees(euler));
        AssertIdentical(a, b);
        var keys = a.Bones[1].RotationKeys;
        foreach (int i in new[] { 0, 4 })
        {
            Assert.InRange(Quat.AngleDegrees(Quat.FromEulerDegrees(euler), ClipEdit.KeyRotation(keys[i])), 0f, 0.02f);
            Assert.Equal(clip.Bones[1].RotationKeys[i].EaseIn, keys[i].EaseIn);
            Assert.Equal(clip.Bones[1].RotationKeys[i].EaseOut, keys[i].EaseOut);
            Assert.Equal(clip.Bones[1].RotationKeys[i].Time, keys[i].Time);
        }
        foreach (int i in new[] { 1, 2, 3, 5 }) Assert.Equal(clip.Bones[1].RotationKeys[i], keys[i]);
        AssertKeys(clip.Bones[1].PositionKeys, a.Bones[1].PositionKeys);
        var back = Quat.ToEulerDegrees(ClipEdit.KeyRotation(keys[0]));
        Assert.InRange(Vector3.Distance(euler, back), 0f, 0.05f);
    }

    [Fact]
    public void SetRotationKeepsSignContinuityEvenForTheNegatedRotation()
    {
        var clip = Make();
        var current = ClipEdit.KeyRotation(clip.Bones[0].RotationKeys[2]);
        var edited = ClipEdit.SetRotation(clip, KeySelection.Of(Rot(0, 2)), Quat.Negate(current));
        AssertStructural(edited);
    }

    [Fact]
    public void SetEasesTouchesOnlyTheEaseBytes()
    {
        var clip = Make();
        var edited = ClipEdit.SetEases(clip, KeySelection.Of(Rot(2, 1), Rot(2, 2)), 50, null);
        var o = clip.Bones[2].RotationKeys;
        var e = edited.Bones[2].RotationKeys;
        Assert.Equal(o[1] with { EaseIn = 50 }, e[1]);
        Assert.Equal(o[2] with { EaseIn = 50 }, e[2]);
        Assert.Equal(o[3], e[3]);
    }

    [Fact]
    public void SetPositionMovesTheControlPointsByTheSameDeltaWhenAsked()
    {
        var clip = Make();
        var target = new Vector3(0.5f, 0.6f, 0.7f);
        var sel = KeySelection.Of(Pos(0, 2));
        var o = clip.Bones[0].PositionKeys[2];
        var moved = ClipEdit.SetPosition(clip, sel, target).Bones[0].PositionKeys[2];
        Assert.Equal(target, moved.Position);
        Assert.InRange(Vector3.Distance(o.InControl - o.Position, moved.InControl - moved.Position), 0f, 1e-6f);
        Assert.InRange(Vector3.Distance(o.OutControl - o.Position, moved.OutControl - moved.Position), 0f, 1e-6f);
        var fixedControls = ClipEdit.SetPosition(clip, sel, target, moveControlPoints: false).Bones[0].PositionKeys[2];
        Assert.Equal(o with { Position = target }, fixedControls);

        var controls = ClipEdit.SetControlPoints(clip, sel, Vector3.One, null).Bones[0].PositionKeys[2];
        Assert.Equal(o with { InControl = Vector3.One }, controls);
    }

    [Fact]
    public void LinearAutoControlPointsMakeStraightUniformSegments()
    {
        var clip = Make();
        var edited = ClipEdit.AutoControlPoints(clip, KeySelection.Bones(clip, [1], rotations: false), ControlPointMode.Linear);
        var keys = edited.Bones[1].PositionKeys;
        Assert.Equal(keys[0].Position, keys[0].InControl);
        Assert.Equal(keys[^1].Position, keys[^1].OutControl);
        for (int i = 0; i + 1 < keys.Length; i++)
        {
            foreach (float f in new[] { 0.1f, 0.25f, 0.5f, 0.8f })
            {
                float t = keys[i].Time + f * (keys[i + 1].Time - keys[i].Time);
                var expected = Vector3.Lerp(keys[i].Position, keys[i + 1].Position, f);
                Assert.InRange(Vector3.Distance(expected, ClipEdit.SamplePosition(edited, 1, t)), 0f, 1e-5f);
            }
        }
        AssertKeys(clip.Bones[1].RotationKeys, edited.Bones[1].RotationKeys);
    }

    [Fact]
    public void SmoothAutoControlPointsAreC1InTime()
    {
        var clip = Make();
        var edited = ClipEdit.AutoControlPoints(clip, KeySelection.Bones(clip, [1], rotations: false), ControlPointMode.Smooth);
        var keys = edited.Bones[1].PositionKeys;
        for (int i = 1; i + 1 < keys.Length; i++)
        {
            float t = keys[i].Time;
            const float h = 0.5f;
            var left = (ClipEdit.SamplePosition(edited, 1, t) - ClipEdit.SamplePosition(edited, 1, t - h)) / h;
            var right = (ClipEdit.SamplePosition(edited, 1, t + h) - ClipEdit.SamplePosition(edited, 1, t)) / h;
            var expected = (keys[i + 1].Position - keys[i - 1].Position) / (keys[i + 1].Time - keys[i - 1].Time);
            Assert.InRange(Vector3.Distance(left, right), 0f, 2e-6f);
            Assert.InRange(Vector3.Distance(expected, right), 0f, 2e-6f);
        }
        // A hold (three equal positions) stays constant.
        var flat = ClipEdit.SetPosition(clip, KeySelection.Of(Pos(2, 0), Pos(2, 1), Pos(2, 2)), Vector3.One);
        flat = ClipEdit.AutoControlPoints(flat, KeySelection.Of(Pos(2, 1)), ControlPointMode.Smooth);
        Assert.Equal(RfaPosKey.Constant(PosTimes[1], Vector3.One), flat.Bones[2].PositionKeys[1]);
    }

    // ── Key the pose ────────────────────────────────────────────────────────────────────────────

    // Rotation segments are about 23 degrees with eases 30/40. A split in the constant-speed part of
    // a segment (or on a segment without ease) is exact up to the int16 store; a split inside an
    // ease's acceleration or deceleration part cannot be reproduced exactly by any pair of engine ease
    // bytes, and the refit stays within 1.5% of the segment's rotation there (documented on
    // ClipEdit.KeyPoseAtTime).
    [Theory]
    [InlineData(Start + 100, 0.015f * 23.5f)]  // inside the ease-out acceleration of key 0
    [InlineData(Start + 320, 0.2f)]            // constant-speed part of an eased segment
    [InlineData(Start + 600, 0.015f * 23.5f)]  // inside the ease-in deceleration of key 1
    [InlineData(Start + 1601, 0.2f)]           // just after a key
    [InlineData(Start + 3000, 0.2f)]           // constant-speed part / position segment 2
    [InlineData(Start + 3840, 0.2f)]           // an existing rotation key time
    public void KeyPoseAtTimeLeavesTheMotionUnchanged(int time, float rotationTolerance)
    {
        var clip = Make();
        var edited = ClipEdit.KeyPoseAtTime(clip, [0, 1, 2, 3], time);
        foreach (var t in edited.Bones)
        {
            Assert.Contains(t.RotationKeys, k => k.Time == time);
            Assert.Contains(t.PositionKeys, k => k.Time == time);
        }
        AssertStructural(edited);
        AssertSameMotion(clip, edited, Start, End, step: 4, rotDegrees: rotationTolerance);
    }

    [Fact]
    public void KeyPoseOnUnEasedSegmentsIsExactAndTouchesNoNeighbour()
    {
        var clip = ClipEdit.SetEases(Make(), KeySelection.All(Make(), positions: false), 0, 0);
        foreach (int time in new[] { Start + 1, Start + 100, Start + 600, Start + 2000, End - 1 })
        {
            var edited = ClipEdit.KeyPoseAtTime(clip, [0, 1, 2, 3], time);
            AssertSameMotion(clip, edited, Start, End, step: 4, rotDegrees: 0.04f, pos: 2e-6f);
            for (int b = 0; b < clip.BoneCount; b++)
                AssertKeys(clip.Bones[b].RotationKeys, edited.Bones[b].RotationKeys.Where(k => k.Time != time));
        }
    }

    [Fact]
    public void KeyPoseOutsideTheKeysHoldsTheEndValues()
    {
        var clip = Make() with { StartTime = 0, EndTime = End + 800 };
        var edited = ClipEdit.KeyPoseAtTime(clip, [1], End + 400);
        edited = ClipEdit.KeyPoseAtTime(edited, [1], 80);
        var rot = edited.Bones[1].RotationKeys;
        Assert.Equal(clip.Bones[1].RotationKeys[0] with { Time = 80, EaseIn = 0, EaseOut = 0 }, rot[0]);
        Assert.Equal(clip.Bones[1].RotationKeys[^1] with { Time = End + 400, EaseIn = 0, EaseOut = 0 }, rot[^1]);
        AssertSameMotion(clip, edited, 0, End + 800, step: 8);
        AssertOtherBonesUntouched(clip, edited, 1);
    }

    [Fact]
    public void KeyPoseSplitsPositionCurvesExactly()
    {
        var clip = Make();
        var edited = ClipEdit.KeyPoseAtTime(clip, [3], Start + 1700, rotations: false);
        AssertKeys(clip.Bones[3].RotationKeys, edited.Bones[3].RotationKeys);
        var (_, pos) = MaxDifference(clip, edited, Start, End, 1);
        Assert.InRange(pos, 0f, 2e-6f);
    }
}
