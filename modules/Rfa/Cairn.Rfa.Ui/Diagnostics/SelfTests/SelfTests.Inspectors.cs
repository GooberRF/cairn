using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the Bone and Key inspectors (multi-selection: mixed values, one undo step per edit,
/// coalesced spinner runs; Euler ⇄ quaternion; time, eases, position, control points) and of the
/// layered "play as action over state" preview against <see cref="ClipBlender"/>.
/// </summary>
internal static class InspectorSelfTests
{
    [SelfTest("inspectors", Order = 20)]
    public static Task Inspectors(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest inspectors: needs a clip document");
            return Task.CompletedTask;
        }
        var original = doc.Current;
        int undoBefore = doc.History.UndoLabels.Count;
        var keys = doc.KeyInspector;

        // Three rotation keys on three bones, with their eases made different first.
        var bones = Enumerable.Range(0, original.BoneCount).Where(b => original.Bones[b].RotationKeys.Length >= 3).Take(3).ToList();
        var three = KeySelection.Of(bones.Select(b => new KeyRef(b, KeyKind.Rotation, 1)));
        var withEases = ClipEdit.SetEases(original, KeySelection.Of(three.Keys[0]), 10, null);
        doc.Apply("Prepare eases", _ => withEases);
        doc.SetKeySelection(three);
        ctx.Check(keys.HasRotationKeys && keys.EaseIn.IsMixed, "different eases show as mixed (indeterminate)");
        int steps = doc.History.UndoLabels.Count;
        keys.EaseIn.Value = 50;
        var c1 = doc.Current;
        bool allEased = three.Keys.All(k => c1.Bones[k.Bone].RotationKeys[k.Index].EaseIn == 64);
        ctx.Check(allEased && doc.History.UndoLabels.Count == steps + 1 && doc.UndoLabel == "Set ease in of 3 keys" && !keys.EaseIn.IsMixed,
            $"one edit sets the ease of every selected key in one undo step ('{doc.UndoLabel}')");

        // Euler on one key: yaw +10° lands, the quaternion boxes follow.
        var single = KeySelection.Of(three.Keys[0]);
        doc.SetKeySelection(single);
        var k0 = single.Keys[0];
        var before = ClipEdit.KeyRotation(doc.Current.Bones[k0.Bone].RotationKeys[k0.Index]);
        var e = Quat.ToEulerDegrees(before);
        keys.Euler[1].Value = e.Y + 10;
        var after = ClipEdit.KeyRotation(doc.Current.Bones[k0.Bone].RotationKeys[k0.Index]);
        var expected = Quat.FromEulerDegrees(e with { Y = e.Y + 10 });
        float err = Quat.AngleDegrees(after, expected);
        ctx.Check(err < 0.05f, $"an Euler edit sets the key's rotation (off by {err:0.000}°, '{doc.UndoLabel}')");
        var q = after;
        bool quatFollows = Math.Abs(keys.Quaternion[0].Value - q.X) < 1e-3 && Math.Abs(keys.Quaternion[3].Value - q.W) < 1e-3
            || Math.Abs(keys.Quaternion[0].Value + q.X) < 1e-3 && Math.Abs(keys.Quaternion[3].Value + q.W) < 1e-3;
        ctx.Check(quatFollows, "the quaternion boxes follow an Euler edit");
        double w = keys.Quaternion[3].Value;
        keys.Quaternion[3].Value = Math.Clamp(w - 0.05, -1, 1);
        var e2 = Quat.ToEulerDegrees(ClipEdit.KeyRotation(doc.Current.Bones[k0.Bone].RotationKeys[k0.Index]));
        ctx.Check(Math.Abs(keys.Euler[0].Value - e2.X) < 0.02 && Math.Abs(keys.Euler[1].Value - e2.Y) < 0.02, "the Euler boxes follow a quaternion edit");

        // A spinner run is one undo step.
        steps = doc.History.UndoLabels.Count;
        keys.Euler[0].BeginInteraction();
        keys.Euler[0].Value = keys.Euler[0].Value + 1;
        keys.Euler[0].Value = keys.Euler[0].Value + 1;
        keys.Euler[0].Value = keys.Euler[0].Value + 1;
        keys.Euler[0].EndInteraction();
        ctx.Check(doc.History.UndoLabels.Count == steps + 1, $"a spinner run on a key value is one undo step ({doc.History.UndoLabels.Count - steps} added)");

        // Time: moves the key and the selection follows it.
        int t0 = doc.Current.Bones[k0.Bone].RotationKeys[k0.Index].Time;
        var track = doc.Current.Bones[k0.Bone].RotationKeys;
        int room = track.Length > k0.Index + 1 ? track[k0.Index + 1].Time - t0 : RfaClip.TicksPerFrame * 4;
        int shift = Math.Max(1, Math.Min(room - 1, RfaClip.TicksPerFrame));
        keys.Time.Value = TimeFormat.ToUnit(t0 + shift, ctx.Model.TimeUnit);
        var moved = doc.KeySelection;
        ctx.Check(moved.Count == 1 && moved.Keys[0].TimeIn(doc.Current) == t0 + shift, $"a time edit moves the key and keeps it selected ('{doc.UndoLabel}')");

        // Position keys: a value edit moves the control points with it; Auto (linear) equals Core.
        int posBone = Enumerable.Range(0, original.BoneCount).OrderByDescending(b => original.Bones[b].PositionKeys.Length).First();
        if (doc.Current.Bones[posBone].PositionKeys.Length >= 3)
        {
            var pk = new KeyRef(posBone, KeyKind.Position, 1);
            doc.SetKeySelection(KeySelection.Of(pk));
            var p0 = doc.Current.Bones[posBone].PositionKeys[1];
            keys.Position[1].Value = p0.Position.Y + 0.25;
            var p1 = doc.Current.Bones[posBone].PositionKeys[1];
            ctx.Check(Math.Abs(p1.Position.Y - (p0.Position.Y + 0.25f)) < 1e-4 && Math.Abs(p1.InControl.Y - (p0.InControl.Y + 0.25f)) < 1e-4,
                "a position edit moves the key and its control points together");
            var beforeAuto = doc.Current;
            keys.AutoLinearCommand.Execute(null);
            ctx.Check(TimelineSelfTests.SameKeys(doc.Current, ClipEdit.AutoControlPoints(beforeAuto, KeySelection.Of(pk), ControlPointMode.Linear)),
                $"Auto (linear) equals ClipEdit.AutoControlPoints ('{doc.UndoLabel}')");
            ctx.Check(keys.ModelPositionText != "—" || doc.FittingSkeleton is null, $"the joint's model-space position is shown ({keys.ModelPositionText})");
        }

        // Bone inspector: mixed weights, one edit for all, apply to children.
        var bi = doc.BoneInspector;
        doc.Apply("Prepare weights", c => ClipEdit.SetBoneWeights(c, [bones[0]], 3));
        doc.Selection.Set([bones[0], bones[1]]);
        ctx.Check(bi.HasSelection && bi.Weight.IsMixed, "bones with different weights show a mixed weight");
        bi.Weight.Value = 7;
        var cw = doc.Current;
        ctx.Check(cw.Bones[bones[0]].Weight == 7 && cw.Bones[bones[1]].Weight == 7 && !bi.Weight.IsMixed,
            $"one weight edit sets every selected bone in one step ('{doc.UndoLabel}')");
        if (doc.FittingSkeleton is { } skeleton)
        {
            int parent = Enumerable.Range(0, skeleton.Count).First(b => skeleton.EffectiveParents.Any(p => p == b));
            doc.Selection.Select(parent);
            bi.IncludeChildren = true;
            bi.Weight.Value = 2;
            var sub = WeightPreset.Subtree(skeleton.EffectiveParents, parent, includeRoot: true);
            ctx.Check(sub.All(b => doc.Current.Bones[b].Weight == 2), $"'apply to children' sets the whole subtree ({sub.Length} bones, '{doc.UndoLabel}')");
            bi.IncludeChildren = false;
        }

        doc.SetKeySelection(KeySelection.Empty);
        doc.Selection.Clear();
        while (doc.History.UndoLabels.Count > undoBefore && doc.CanUndo) doc.Undo();
        ctx.Check(ReferenceEquals(doc.Current, original), "the inspector test leaves the clip as it found it");
        return Task.CompletedTask;
    }

    [SelfTest("layered", Order = 30)]
    public static async Task Layered(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest layered: needs a clip document");
            return;
        }
        var layered = doc.Layered;
        var original = doc.Current;
        if (layered.Options.Count == 0)
        {
            ctx.Log("selftest layered: no compatible state clip in the library; skipped");
            return;
        }
        var pick = layered.Options.FirstOrDefault(o => o.Clip.Name.Contains("stand", StringComparison.OrdinalIgnoreCase)) ?? layered.Options[0];
        bool loaded = await layered.UseStateAsync(pick.Clip.Name);
        ctx.Check(loaded && layered.IsActive && layered.BadgeText.Length > 0 && doc.Scene.PoseOverride is not null,
            $"picking a state turns the layered preview on with a badge ('{layered.BadgeText}')");
        ctx.Check(layered.Options.TakeWhile(o => o.IsTableState).Count() == layered.Options.Count(o => o.IsTableState), "table states are listed first");
        var state = layered.State!;
        if (doc.Scene.Pose is { } pose && doc.Scene.Skeleton.Count > 0)
        {
            float t = original.StartTime + Math.Max(1, original.RampIn / 2);
            doc.Playback.Seek(t);
            var locals = new Rigid[doc.Scene.Skeleton.Count];
            ClipBlender.Blend(state, LayeredPreviewViewModel.StateTime(state, original, doc.Playback.Time), original, doc.Playback.Time, locals, doc.Scene.Skeleton.RestLocal.AsSpan());
            float worst = 0;
            for (int i = 0; i < locals.Length; i++) worst = Math.Max(worst, Quat.AngleDegrees(locals[i].Rotation, pose.Local[i].Rotation));
            ctx.Check(worst < 0.001f, $"the viewport pose is ClipBlender's blend of the action over the state ({worst:0.0000}° off)");
            float stateTime = LayeredPreviewViewModel.StateTime(state, original, original.StartTime + (state.EndTime - state.StartTime) + 10);
            ctx.Check(Math.Abs(stateTime - (state.StartTime + 10)) < 0.5f || state.EndTime == state.StartTime, "the state loops in step with the action");
            layered.IsActive = false;
            var plain = new Pose(doc.Scene.Skeleton);
            plain.Sample(original, doc.Playback.Time);
            worst = 0;
            for (int i = 0; i < plain.Local.Length; i++) worst = Math.Max(worst, Quat.AngleDegrees(plain.Local[i].Rotation, pose.Local[i].Rotation));
            ctx.Check(!layered.IsActive && layered.BadgeText.Length == 0 && doc.Scene.PoseOverride is null && worst < 0.001f,
                "turning it off goes back to the plain clip and hides the badge");
        }
        ctx.Check(ReferenceEquals(doc.Current, original) && !doc.IsDirty, "the layered preview never touches the document");
    }
}
