using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-test and screenshot switches of viewport pose editing (phase 5, deliverable 3). The test drives
/// <see cref="PoseEditController"/> directly (not the mouse): auto-key rotations in each space, layer
/// edits against <see cref="ClipEdit.OffsetBone"/>, coalescing and cancel, moving the root, the IK drag
/// and the saved-clip ghost. It undoes everything it did.
/// </summary>
internal static class PoseEditingSelfTests
{
    private const double Tolerance = 0.05;

    [SelfTest("pose", Order = 500)]
    public static async Task Pose(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("selftest pose: needs a clip document");
            return;
        }
        if (doc.FittingSkeleton is not { } skeleton)
        {
            ctx.Log("selftest pose: needs a preview mesh that fits the clip; skipped");
            return;
        }
        var ctl = PoseEditController.For(doc);
        var original = doc.Current;
        int undoBase = doc.History.UndoLabels.Count;
        var saved = (ctl.Tool, ctl.Space, ctl.AutoKey, ctl.IkEnabled, ctl.RangeEnabled, Ghost: doc.Display.GhostSavedClip,
            Selection: doc.Selection.Bones.ToArray(), Time: doc.Playback.Time);
        try
        {
            Run(ctx, doc, ctl, skeleton);
        }
        finally
        {
            if (ctl.IsDragging) ctl.CancelDrag();
            while (doc.History.UndoLabels.Count > undoBase && doc.CanUndo) doc.Undo();
            ctl.Tool = saved.Tool;
            ctl.Space = saved.Space;
            ctl.AutoKey = saved.AutoKey;
            ctl.IkEnabled = saved.IkEnabled;
            ctl.RangeEnabled = saved.RangeEnabled;
            doc.Display.GhostSavedClip = saved.Ghost;
            doc.Selection.Set(saved.Selection);
            doc.Playback.Seek(saved.Time);
        }
        ctx.Check(ReferenceEquals(doc.Current, original) && doc.History.UndoLabels.Count == undoBase,
            "pose: the document is left as found (every step undone)");
        await ctx.YieldAsync();
    }

    private static void Run(SelfTestContext ctx, ClipDocumentViewModel doc, PoseEditController ctl, Skeleton skeleton)
    {
        var profile = RigProfiles.For(skeleton);
        int hand = profile.IndexOf(skeleton.Names, "hand-l");
        if (hand < 0) hand = Enumerable.Range(0, skeleton.Count).OrderByDescending(i => Depth(skeleton, i)).First();
        int root = Enumerable.Range(0, skeleton.Count).First(i => skeleton.EffectiveParents[i] < 0);
        string handName = doc.BoneDisplayName(hand);
        var clip0 = doc.Current;
        // A whole tick between frames (so the auto-key usually has to split a segment).
        int t = clip0.StartTime + (int)Math.Round((clip0.EndTime - clip0.StartTime) * 0.37) + 37;
        t = Math.Clamp(t, clip0.StartTime, clip0.EndTime);
        doc.Playback.Seek(t);
        ctx.Log($"selftest pose: bone {handName} ({hand}), root {doc.BoneDisplayName(root)}, playhead tick {t} (playhead {doc.Playback.Time}), rig {profile.Name}");
        ctx.Check(ctl.PlayheadTick == t, $"pose: the playhead is tick {ctl.PlayheadTick}");

        doc.Selection.Select(hand);
        ctl.AutoKey = true;
        ctl.Tool = PoseTool.Rotate;
        ctx.Check(ctl.Gizmo == PoseGizmo.Rotate && ctl.Hint is null && ctl.ModeText.StartsWith("Auto-key", StringComparison.Ordinal),
            $"pose: rotate gizmo on {handName}, badge '{ctl.ModeText}'");

        // ── Auto-key rotate in each space ───────────────────────────────────
        double radians = 25 * Math.PI / 180;
        var r = Quat.FromAxisAngle(Vector3.UnitY, (float)radians);
        foreach (var space in new[] { OffsetSpace.Local, OffsetSpace.Parent, OffsetSpace.Model })
        {
            ctl.Space = space;
            var before = doc.Current;
            var pose0 = new Pose(skeleton);
            pose0.Sample(before, t);
            var l0 = ClipSampler.SampleRotation(before.Bones[hand].RotationKeys.AsSpan(), t);
            int parent = skeleton.EffectiveParents[hand];
            var pw = parent >= 0 ? pose0.World[parent].Rotation : Quaternion.Identity;
            int labels = doc.History.UndoLabels.Count;
            bool began = ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Y);
            ctl.UpdateAxisRotation(GizmoHandle.Y, radians * 0.3);
            ctl.UpdateAxisRotation(GizmoHandle.Y, radians * 0.7);
            ctl.UpdateAxisRotation(GizmoHandle.Y, radians);
            string? readout = ctl.Readout;
            ctl.CommitDrag();
            var after = doc.Current;
            ctx.Check(began && !ReferenceEquals(after, before), $"pose {space}: the drag edits the clip (readout '{readout}')");
            ctx.Check(doc.History.UndoLabels.Count == labels + 1 && doc.UndoLabel == "Rotate " + handName,
                $"pose {space}: a three-step drag is one undo step labelled '{doc.UndoLabel}'");
            var track = after.Bones[hand].RotationKeys;
            ctx.Check(track.Any(k => k.Time == t), $"pose {space}: a rotation key now sits at tick {t}");

            // Expected, written out here independently of the controller.
            Quaternion expectedLocal = space switch
            {
                OffsetSpace.Local => Quat.Mul(l0, r),
                OffsetSpace.Parent => Quat.Mul(r, l0),
                _ => Quat.Mul(Quat.Conj(pw), Quat.Mul(r, Quat.Mul(pw, l0))),
            };
            Quaternion expectedWorld = space switch
            {
                OffsetSpace.Local => Quat.Mul(pose0.World[hand].Rotation, r),
                OffsetSpace.Parent => Quat.Mul(pw, Quat.Mul(r, l0)),
                _ => Quat.Mul(r, pose0.World[hand].Rotation),
            };
            var sampled = ClipSampler.SampleRotation(track.AsSpan(), t);
            double localError = Quat.AngleDegrees(sampled, expectedLocal);
            var pose1 = new Pose(skeleton);
            pose1.Sample(after, t);
            double worldError = Quat.AngleDegrees(pose1.World[hand].Rotation, expectedWorld);
            ctx.Check(localError < Tolerance && worldError < Tolerance,
                $"pose {space}: sampled local rotation at the playhead is the expected composition ({localError:0.0000}° local, {worldError:0.0000}° model)");

            // Every other key of the bone keeps its stored components; other bones are untouched.
            var old = before.Bones[hand].RotationKeys;
            bool keysKept = old.Where(k => k.Time != t).All(k => track.Any(n => n.Time == k.Time && n.X == k.X && n.Y == k.Y && n.Z == k.Z && n.W == k.W));
            bool othersSame = Enumerable.Range(0, after.BoneCount).Where(i => i != hand).All(i => ReferenceEquals(after.Bones[i], before.Bones[i]));
            double maxElsewhere = old.Select(k => k.Time).Where(time => time != t).Distinct()
                .Select(time => (double)Quat.AngleDegrees(ClipSampler.SampleRotation(old.AsSpan(), time), ClipSampler.SampleRotation(track.AsSpan(), time)))
                .DefaultIfEmpty(0).Max();
            // 0.17°: the engine returns the later key when two keys are under 0.16° apart, so a sample at a key next to
            // a nearly-equal one can move by that much when its neighbour is replaced; the stored keys themselves are checked exactly.
            ctx.Check(keysKept && othersSame && maxElsewhere < 0.17,
                $"pose {space}: motion at the other key times is unchanged (keys bit-identical: {keysKept}, other bones untouched: {othersSame}, max {maxElsewhere:0.0000}°)");
            doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, before), $"pose {space}: undo restores the clip");
        }

        // ── Drag cost ───────────────────────────────────────────────────────
        {
            ctl.Space = OffsetSpace.Local;
            // Median per-frame cost (one warm-up frame not counted): a timing, judged only when grossly off.
            const int frames = 20;
            double DragMedian()
            {
                ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
                ctl.UpdateAxisRotation(GizmoHandle.X, 0.005);
                var each = new List<double>(frames);
                for (int i = 1; i <= frames; i++)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    ctl.UpdateAxisRotation(GizmoHandle.X, i * 0.01);
                    each.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
                }
                ctl.CancelDrag();
                return TimelineSelfTests.Median(each);
            }
            double autoKeyMs = DragMedian();
            ctl.AutoKey = false;
            double layerMs = DragMedian();
            ctl.AutoKey = true;
            ctx.Log(string.Format(CultureInfo.InvariantCulture,
                "timing: pose drag frame (edit + document refresh), median {0:0.00} ms auto-key, {1:0.00} ms layer edit on {2} bones", autoKeyMs, layerMs, doc.Current.BoneCount));
            ctx.Check(autoKeyMs < 100 && layerMs < 100, $"pose: a drag frame is not grossly slow (median {autoKeyMs:0.00} / {layerMs:0.00} ms, limit 100 ms)");
        }

        // ── Cancel restores the exact snapshot ──────────────────────────────
        {
            var before = doc.Current;
            int labels = doc.History.UndoLabels.Count;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Z);
            ctl.UpdateAxisRotation(GizmoHandle.Z, 0.5);
            bool changed = !ReferenceEquals(doc.Current, before);
            ctl.CancelDrag();
            ctx.Check(changed && ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == labels,
                "pose: Esc (cancel) during a drag restores the exact pre-drag snapshot and adds no step");
        }

        // ── Multi-selection ─────────────────────────────────────────────────
        int lower = skeleton.EffectiveParents[hand];
        if (lower >= 0)
        {
            doc.Selection.Set([lower, hand]);
            var before = doc.Current;
            ctl.Space = OffsetSpace.Local;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
            ctl.UpdateAxisRotation(GizmoHandle.X, 0.2);
            ctl.CommitDrag();
            var after = doc.Current;
            bool both = new[] { lower, hand }.All(b =>
                after.Bones[b].RotationKeys.Any(k => k.Time == t)
                && Quat.AngleDegrees(ClipSampler.SampleRotation(after.Bones[b].RotationKeys.AsSpan(), t),
                    Quat.Mul(ClipSampler.SampleRotation(before.Bones[b].RotationKeys.AsSpan(), t), Quat.FromAxisAngle(Vector3.UnitX, 0.2f))) < Tolerance);
            ctx.Check(both && doc.UndoLabel == "Rotate 2 bones", $"pose: rotating two selected bones keys both with the same local delta ('{doc.UndoLabel}')");
            doc.Undo();
            doc.Selection.Select(hand);
        }

        // ── Layer edit equals ClipEdit.OffsetBone ───────────────────────────
        ctl.AutoKey = false;
        ctx.Check(ctl.IsLayerMode && ctl.ModeText.StartsWith("Layer edit", StringComparison.Ordinal), $"pose: the badge says layer edit ('{ctl.ModeText}')");
        foreach (var space in new[] { OffsetSpace.Local, OffsetSpace.Model })
        {
            ctl.Space = space;
            var before = doc.Current;
            double a = 20 * Math.PI / 180;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
            ctl.UpdateAxisRotation(GizmoHandle.X, a * 0.5);
            ctl.UpdateAxisRotation(GizmoHandle.X, a);
            ctl.CommitDrag();
            var expected = ClipEdit.OffsetBone(before, hand, new BoneOffset(Quat.FromAxisAngle(Vector3.UnitX, (float)a), Vector3.Zero, space) { Skeleton = skeleton });
            bool same = RfaWriter.Write(doc.Current).AsSpan().SequenceEqual(RfaWriter.Write(expected));
            ctx.Check(same && doc.UndoLabel == "Layer rotate " + handName,
                $"pose layer {space}: the drag equals ClipEdit.OffsetBone on the pre-drag clip byte for byte ('{doc.UndoLabel}')");
            doc.Undo();
        }
        {
            ctl.Space = OffsetSpace.Parent;
            ctl.RangeEnabled = true;
            ctl.RangeFromTicks = t - 320;
            ctl.RangeToTicks = t + 320;
            ctl.FalloffTicks = 480;
            var before = doc.Current;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Z);
            ctl.UpdateAxisRotation(GizmoHandle.Z, 0.3);
            ctl.CommitDrag();
            var offset = new BoneOffset(Quat.FromAxisAngle(Vector3.UnitZ, 0.3f), Vector3.Zero, OffsetSpace.Parent)
            {
                Skeleton = skeleton, From = t - 320, To = t + 320, FalloffTicks = 480,
            };
            bool same = RfaWriter.Write(doc.Current).AsSpan().SequenceEqual(RfaWriter.Write(ClipEdit.OffsetBone(before, hand, offset)));
            ctx.Check(same && ctl.ModeText.Contains("falloff", StringComparison.Ordinal),
                $"pose layer range: limited to a range with falloff it equals OffsetBone with From/To/FalloffTicks ('{ctl.ModeText}')");
            doc.Undo();
            ctl.RangeEnabled = false;
        }
        ctl.AutoKey = true;

        // ── Move ────────────────────────────────────────────────────────────
        ctl.Tool = PoseTool.Move;
        ctl.IkEnabled = false;
        ctx.Check(ctl.Gizmo == PoseGizmo.None && ctl.Hint is { Length: > 0 } && !ctl.BeginDrag(PoseDragKind.Move, GizmoHandle.X),
            $"pose: {handName} has no move gizmo and says why ('{ctl.Hint}')");
        doc.Selection.Select(root);
        string rootName = doc.BoneDisplayName(root);
        foreach (var space in new[] { OffsetSpace.Model, OffsetSpace.Local })
        {
            ctl.Space = space;
            var before = doc.Current;
            var pose0 = new Pose(skeleton);
            pose0.Sample(before, t);
            var p0 = ClipSampler.SamplePosition(before.Bones[root].PositionKeys.AsSpan(), t);
            bool began = ctl.BeginDrag(PoseDragKind.Move, GizmoHandle.X);
            ctl.UpdateAxisTranslation(GizmoHandle.X, 0.05);
            ctl.UpdateAxisTranslation(GizmoHandle.X, 0.1);
            ctl.CommitDrag();
            var after = doc.Current;
            var expected = p0 + (space == OffsetSpace.Local ? Quat.Rotate(pose0.World[root].Rotation, new Vector3(0.1f, 0, 0)) : new Vector3(0.1f, 0, 0));
            var p1 = ClipSampler.SamplePosition(after.Bones[root].PositionKeys.AsSpan(), t);
            ctx.Check(began && ctl.CanMove(root) && after.Bones[root].PositionKeys.Any(k => k.Time == t) && Vector3.Distance(p1, expected) < 1e-4f
                && doc.UndoLabel == "Move " + rootName,
                $"pose move {space}: moving the root keys its position at the playhead (error {Vector3.Distance(p1, expected) * 1000:0.000} mm, '{doc.UndoLabel}')");
            doc.Undo();
        }

        // ── IK drag ─────────────────────────────────────────────────────────
        doc.Selection.Select(hand);
        ctl.IkEnabled = true;
        ctl.Space = OffsetSpace.Model;
        if (ctl.ChainFor(hand) is { } chain)
        {
            ctx.Check(ctl.IsIkMove && ctl.Gizmo == PoseGizmo.Move && ctl.ModeText.StartsWith("IK", StringComparison.Ordinal), $"pose IK: the move tool drives the chain ('{ctl.ModeText}')");
            var before = doc.Current;
            var pose0 = new Pose(skeleton);
            pose0.Sample(before, t);
            // A reachable target: a fifth of the way towards the shoulder, then 4 cm forward and 3 cm out.
            var shoulder = pose0.World[chain.Upper].Position;
            var offset = (shoulder - pose0.World[hand].Position) * 0.2f + new Vector3(0.03f, 0f, 0.04f);
            var target = pose0.World[hand].Position + offset;
            bool began = ctl.BeginDrag(PoseDragKind.Ik, GizmoHandle.Screen);
            ctl.UpdateWorldTranslation(offset * 0.5f);
            // Out of reach first: the readout says so and the limb stops short on the shoulder-target line.
            ctl.UpdateWorldTranslation((pose0.World[hand].Position - shoulder) * 2f);
            bool reportsReach = ctl.LastIkSolution?.Clamped == true && ctl.Readout?.Contains("out of reach", StringComparison.Ordinal) == true;
            ctl.UpdateWorldTranslation(offset);
            bool clamped = ctl.LastIkSolution?.Clamped ?? true;
            ctl.CommitDrag();
            ctx.Check(reportsReach, "pose IK: a target out of reach is reported in the readout");
            var after = doc.Current;
            var pose1 = new Pose(skeleton);
            pose1.Sample(after, t);
            float error = Vector3.Distance(pose1.World[hand].Position, target);
            double turn = Quat.AngleDegrees(pose1.World[hand].Rotation, pose0.World[hand].Rotation);
            bool keyed = new[] { chain.Upper, chain.Lower, chain.End }.All(b => after.Bones[b].RotationKeys.Any(k => k.Time == t));
            float shoulderMoved = Vector3.Distance(pose1.World[chain.Upper].Position, pose0.World[chain.Upper].Position);
            ctx.Check(began && !clamped && error < 0.001f && keyed && shoulderMoved < 1e-5f,
                $"pose IK: the hand reaches the target ({error * 1000:0.000} mm), the shoulder stays, three bones keyed: {keyed}");
            ctx.Check(turn < Tolerance && doc.UndoLabel == "IK drag " + handName,
                $"pose IK: the hand keeps its model-space rotation ({turn:0.0000}°), '{doc.UndoLabel}'");
            doc.Undo();
        }
        else
        {
            ctx.Log($"selftest pose: rig {profile.Name} defines no IK chain ending at {handName}; IK checks skipped");
        }

        // ── Ghost of the saved clip ─────────────────────────────────────────
        {
            ctl.Tool = PoseTool.Rotate;
            doc.Display.GhostSavedClip = false;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Y);
            ctl.UpdateAxisRotation(GizmoHandle.Y, 0.4);
            ctl.CommitDrag();
            bool hiddenOff = !doc.Scene.HasGhost("saved");
            doc.Display.GhostSavedClip = true;
            bool shown = doc.Scene.HasGhost("saved") && ReferenceEquals(doc.Scene.Ghosts.First(g => g.Id == "saved").Clip, doc.SavedSnapshot);
            doc.Display.GhostSavedClip = false;
            bool hidden = !doc.Scene.HasGhost("saved");
            doc.Display.GhostSavedClip = true;
            doc.Undo();
            bool hiddenWhenClean = !doc.Scene.HasGhost("saved");
            doc.Display.GhostSavedClip = false;
            ctx.Check(hiddenOff && shown && hidden && hiddenWhenClean,
                $"pose ghost: the saved clip's ghost follows the toggle (off {hiddenOff}, on {shown}, off again {hidden}) and hides when the clip equals the saved one ({hiddenWhenClean})");
        }
    }

    /// <summary>
    /// The mouse path end to end: hit testing of the drawn handles and the drag maths of the gizmo layer,
    /// fed with screen points projected from the gizmo's own geometry.
    /// </summary>
    [SelfTest("pose-gizmo", Order = 501)]
    public static async Task GizmoMousePath(SelfTestContext ctx)
    {
        if (ctx.Clip is not { } doc || doc.FittingSkeleton is not { } skeleton)
        {
            ctx.Log("selftest pose-gizmo: needs a clip document with a fitting preview mesh; skipped");
            return;
        }
        var viewport = Viewport.ViewportControl.Visible().FirstOrDefault(v => ReferenceEquals(v.DataContext, doc));
        if (viewport is null)
        {
            ctx.Log("selftest pose-gizmo: no visible viewport; skipped");
            return;
        }
        var ctl = PoseEditController.For(doc);
        var gizmos = viewport.Gizmos;
        var original = doc.Current;
        int undoBase = doc.History.UndoLabels.Count;
        var saved = (ctl.Tool, ctl.Space, ctl.AutoKey, ctl.IkEnabled, Selection: doc.Selection.Bones.ToArray());
        try
        {
            var profile = RigProfiles.For(skeleton);
            int hand = profile.IndexOf(skeleton.Names, "hand-l");
            if (hand < 0) hand = skeleton.Count - 1;
            int root = Enumerable.Range(0, skeleton.Count).First(i => skeleton.EffectiveParents[i] < 0);
            int t = ctl.PlayheadTick;
            doc.Selection.Select(hand);
            ctl.AutoKey = true;
            ctl.Space = OffsetSpace.Local;
            ctl.Tool = PoseTool.Rotate;
            // An oblique view, so every ring is open enough to drag exactly whatever the window layout.
            doc.Scene.Camera.SetView(35, 20);
            await ctx.YieldAsync();
            Viewport.ViewportControl.FlushAll();

            ctx.Check(gizmos.CentreScreen is not null, "pose-gizmo: the rotate gizmo is drawn on the selected bone");
            if (gizmos.CentreScreen is not { } centre) return;
            // Inside the rings is the free trackball (except right on another joint, which wins so it can be picked).
            var inside = Enumerable.Range(0, 16).Select(k => new System.Windows.Vector(
                Math.Cos(k * Math.PI / 8) * GizmoLayerRadius * 0.55, Math.Sin(k * Math.PI / 8) * GizmoLayerRadius * 0.55)).ToArray();
            ctx.Check(inside.Any(v => gizmos.HitTest(centre + v) == GizmoHandle.Free), "pose-gizmo: the inside of the rings is the free trackball");
            ctx.Check(gizmos.HitTest(centre + new System.Windows.Vector(GizmoLayerRadius * 1.22, 0)) == GizmoHandle.Screen, "pose-gizmo: the outer ring is the screen ring");
            ctx.Check(gizmos.HitTest(centre + new System.Windows.Vector(GizmoLayerRadius * 3, 0)) == GizmoHandle.None, "pose-gizmo: outside the gizmo nothing is hit (bone picking and orbit go through)");

            // Each axis ring: grab it where it is hit, drag 30° round it, expect a 30° local rotation about that axis.
            for (int axis = 0; axis < 3; axis++)
            {
                double start = double.NaN;
                // A ring seen nearly edge-on is dragged along its screen tangent (approximate by design), so only
                // rings whose projected ellipse is open enough (minor/major > 0.35, ~20° from edge-on) are measured.
                var radii = Enumerable.Range(0, 36).Select(k => gizmos.RingPoint(axis, k * Math.PI / 18)).OfType<System.Windows.Point>()
                    .Select(p => (p - centre).Length).ToList();
                if (radii.Count < 36 || radii.Min() < 0.35 * radii.Max()) start = double.PositiveInfinity;
                for (int step = 0; step < 72 && double.IsNaN(start); step++)
                {
                    double a = step * Math.PI / 36;
                    if (gizmos.RingPoint(axis, a) is { } p && gizmos.HitTest(p) == axis
                        && gizmos.RingPoint(axis, a + Math.PI / 6) is { } q && (q - p).Length > 8) start = a;
                }
                if (double.IsNaN(start) || double.IsInfinity(start))
                {
                    ctx.Log($"selftest pose-gizmo: ring {GizmoHandle.Name(axis)} is edge-on from this view; skipped");
                    continue;
                }
                var before = doc.Current;
                var l0 = ClipSampler.SampleRotation(before.Bones[hand].RotationKeys.AsSpan(), t);
                var points = Enumerable.Range(0, 7).Select(k => gizmos.RingPoint(axis, start + k * Math.PI / 36)!.Value).ToArray();
                bool began = gizmos.TryBeginDrag(points[0]);
                foreach (var point in points.Skip(1)) gizmos.Drag(point, System.Windows.Input.ModifierKeys.None);
                string? readout = ctl.Readout;
                gizmos.EndDrag();
                var expected = Quat.Mul(l0, Quat.FromAxisAngle(axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ, (float)(Math.PI / 6)));
                double error = Quat.AngleDegrees(ClipSampler.SampleRotation(doc.Current.Bones[hand].RotationKeys.AsSpan(), t), expected);
                ctx.Check(began && error < 0.5 && doc.UndoLabel == "Rotate " + doc.BoneDisplayName(hand),
                    $"pose-gizmo: dragging the {GizmoHandle.Name(axis)} ring 30° round turns the bone +30° about local {GizmoHandle.Name(axis)} (error {error:0.000}°, readout '{readout}')");
                if (!ReferenceEquals(doc.Current, before)) doc.Undo();
            }

            // The screen ring (a quarter turn) and the trackball (one ring radius = one radian) turn the bone in model space.
            // The trackball's grab point avoids other joints (a joint under the mouse wins over the trackball).
            var ball = new System.Windows.Vector(-GizmoLayerRadius * 0.45, -GizmoLayerRadius * 0.5);
            foreach (var candidate in new[] { ball, new(-GizmoLayerRadius * 0.45, GizmoLayerRadius * 0.5), new(-GizmoLayerRadius * 0.45, 0) })
            {
                if (gizmos.HitTest(centre + candidate) != GizmoHandle.Free) continue;
                ball = candidate;
                break;
            }
            foreach (var (name, from, path, degrees) in new[]
            {
                ("screen ring", new System.Windows.Vector(GizmoLayerRadius * 1.22, 0),
                    Enumerable.Range(1, 9).Select(k => new System.Windows.Vector(Math.Cos(k * Math.PI / 18), -Math.Sin(k * Math.PI / 18)) * (GizmoLayerRadius * 1.22)).ToArray(), 90.0),
                ("trackball", ball,
                    Enumerable.Range(1, 8).Select(k => ball + new System.Windows.Vector(k * GizmoLayerRadius / 8, 0)).ToArray(), 180 / Math.PI),
            })
            {
                var before = doc.Current;
                var pose0 = new Pose(skeleton);
                pose0.Sample(before, t);
                bool began = gizmos.TryBeginDrag(centre + from);
                foreach (var step in path) gizmos.Drag(centre + step, System.Windows.Input.ModifierKeys.None);
                string? readout = ctl.Readout;
                gizmos.EndDrag();
                var pose1 = new Pose(skeleton);
                pose1.Sample(doc.Current, t);
                double turned = Quat.AngleDegrees(pose1.World[hand].Rotation, pose0.World[hand].Rotation);
                ctx.Check(began && Math.Abs(turned - degrees) < 1.0,
                    $"pose-gizmo: the {name} drag turns the bone {turned:0.00}° in model space (expected {degrees:0.00}°, readout '{readout}')");
                if (!ReferenceEquals(doc.Current, before)) doc.Undo();
            }

            // Ctrl snaps the ring angle to 5°.
            {
                double start = double.NaN;
                for (int step = 0; step < 72 && double.IsNaN(start); step++)
                {
                    double a = step * Math.PI / 36;
                    if (gizmos.RingPoint(GizmoHandle.Y, a) is { } p && gizmos.HitTest(p) == GizmoHandle.Y) start = a;
                }
                if (!double.IsNaN(start))
                {
                    var before = doc.Current;
                    var to = gizmos.RingPoint(GizmoHandle.Y, start + 0.21)!.Value;
                    gizmos.TryBeginDrag(gizmos.RingPoint(GizmoHandle.Y, start)!.Value);
                    gizmos.Drag(to, System.Windows.Input.ModifierKeys.Control);
                    string? readout = ctl.Readout;
                    gizmos.EndDrag();
                    ctx.Check(readout is not null && (readout.EndsWith(" 10.0°", StringComparison.Ordinal) || readout.EndsWith(" 15.0°", StringComparison.Ordinal)),
                        $"pose-gizmo: Ctrl snaps a 12° drag to a multiple of 5° ('{readout}')");
                    if (!ReferenceEquals(doc.Current, before)) doc.Undo();
                }
            }

            // Move arrow on the root: drag 10 cm along an axis that faces the screen.
            doc.Selection.Select(root);
            ctl.Tool = PoseTool.Move;
            ctl.Space = OffsetSpace.Model;
            await ctx.YieldAsync();
            Viewport.ViewportControl.FlushAll();
            double along = gizmos.ArrowMetres * 0.6;
            int moveAxis = -1;
            for (int axis = 0; axis < 3 && moveAxis < 0; axis++)
            {
                if (gizmos.AxisPoint(axis, along) is { } p && gizmos.HitTest(p) == axis) moveAxis = axis;
            }
            ctx.Check(moveAxis >= 0, "pose-gizmo: a move arrow on the root is hit where it is drawn");
            if (moveAxis >= 0)
            {
                var before = doc.Current;
                var p0 = ClipSampler.SamplePosition(before.Bones[root].PositionKeys.AsSpan(), t);
                // The gizmo follows the live joint, so the screen points are taken before the drag moves it.
                var grab = gizmos.AxisPoint(moveAxis, along)!.Value;
                var half = gizmos.AxisPoint(moveAxis, along + 0.05)!.Value;
                var full = gizmos.AxisPoint(moveAxis, along + 0.1)!.Value;
                gizmos.TryBeginDrag(grab);
                gizmos.Drag(half, System.Windows.Input.ModifierKeys.None);
                gizmos.Drag(full, System.Windows.Input.ModifierKeys.None);
                string? readout = ctl.Readout;
                gizmos.EndDrag();
                var axisVector = moveAxis == 0 ? Vector3.UnitX : moveAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                var p1 = ClipSampler.SamplePosition(doc.Current.Bones[root].PositionKeys.AsSpan(), t);
                float error = Vector3.Distance(p1, p0 + axisVector * 0.1f);
                ctx.Check(error < 0.002f, $"pose-gizmo: dragging the {GizmoHandle.Name(moveAxis)} arrow 10 cm moves the root 10 cm (error {error * 1000:0.00} mm, readout '{readout}')");
                if (!ReferenceEquals(doc.Current, before)) doc.Undo();
            }
        }
        finally
        {
            if (gizmos.IsDragging) gizmos.CancelDrag();
            while (doc.History.UndoLabels.Count > undoBase && doc.CanUndo) doc.Undo();
            ctl.Tool = saved.Tool;
            ctl.Space = saved.Space;
            ctl.AutoKey = saved.AutoKey;
            ctl.IkEnabled = saved.IkEnabled;
            doc.Selection.Set(saved.Selection);
        }
        ctx.Check(ReferenceEquals(doc.Current, original), "pose-gizmo: the document is left as found");
    }

    private const double GizmoLayerRadius = Cairn.Viewport.GizmoLayer.RingRadius;

    private static int Depth(Skeleton skeleton, int bone)
    {
        int depth = 0;
        for (int p = skeleton.EffectiveParents[bone]; p >= 0 && depth <= skeleton.Count; p = skeleton.EffectiveParents[p]) depth++;
        return depth;
    }

    /// <summary>
    /// Screenshot switches: <c>--tool rotate|move|select</c>, <c>--space local|parent|model</c>,
    /// <c>--autokey on|off</c>, <c>--ghost on|off</c>, <c>--ik on|off</c>, <c>--layer-range on</c> and
    /// <c>--drag-preview N</c> (degrees for the rotate gizmo about <c>--drag-axis x|y|z</c>,
    /// centimetres for the move gizmo), which leaves the drag as a live, uncommitted edit for the capture.
    /// </summary>
    [ScreenshotStep(50)]
    public static async Task PoseScreenshot(ScreenshotContext ctx)
    {
        // A mesh tab's gizmo switches (the same names) are read by MeshGizmoSelfTests.MeshGizmoScreenshot.
        if (ctx.Model.ActiveDocument is MeshDocumentViewModel) return;
        string? tool = ctx.Extra("tool"), space = ctx.Extra("space"), autokey = ctx.Extra("autokey"), ghost = ctx.Extra("ghost"),
            ik = ctx.Extra("ik"), drag = ctx.Extra("drag-preview"), axisName = ctx.Extra("drag-axis"), range = ctx.Extra("layer-range");
        if (tool is null && space is null && autokey is null && ghost is null && ik is null && drag is null && range is null) return;
        if (ctx.Clip is not { } doc)
        {
            ctx.Log("pose: the pose switches need a clip document");
            return;
        }
        var ctl = PoseEditController.For(doc);
        if (tool is not null) ctl.Tool = tool.ToLowerInvariant() switch { "rotate" => PoseTool.Rotate, "move" => PoseTool.Move, _ => PoseTool.Select };
        if (space is not null) ctl.Space = space.ToLowerInvariant() switch { "parent" => OffsetSpace.Parent, "model" => OffsetSpace.Model, _ => OffsetSpace.Local };
        if (autokey is not null) ctl.AutoKey = On(autokey);
        if (ik is not null) ctl.IkEnabled = On(ik);
        if (range is not null) ctl.RangeEnabled = On(range);
        if (ghost is not null) doc.Display.GhostSavedClip = On(ghost);
        if (drag is not null && double.TryParse(drag, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
        {
            int axis = (axisName ?? "x").ToLowerInvariant() switch { "y" => GizmoHandle.Y, "z" => GizmoHandle.Z, _ => GizmoHandle.X };
            if (ctl.Gizmo == PoseGizmo.Rotate && ctl.BeginDrag(PoseDragKind.Rotate, axis))
            {
                ctl.UpdateAxisRotation(axis, amount * Math.PI / 180);
            }
            else if (ctl.Gizmo == PoseGizmo.Move && ctl.BeginDrag(ctl.IsIkMove ? PoseDragKind.Ik : PoseDragKind.Move, axis))
            {
                ctl.UpdateAxisTranslation(axis, amount / 100);
            }
            else
            {
                ctx.Log($"pose: --drag-preview needs a gizmo ({ctl.Hint ?? "none"})");
            }
        }
        ctl.Refresh();
        if (doc.Scene.Pose is { } live && ctl.ActiveBone >= 0)
        {
            var parents = doc.Scene.Skeleton.EffectiveParents;
            var chainText = string.Join(" ", Enumerable.Range(0, live.Count).Where(i => i == ctl.ActiveBone || parents[i] == ctl.ActiveBone)
                .Select(i => $"{doc.BoneDisplayName(i)}={live.World[i].Position}"));
            ctx.Log($"pose: live joints {chainText}");
        }
        ctx.Log($"pose: tool {ctl.Tool}, space {ctl.Space}, auto-key {ctl.AutoKey}, IK {ctl.IkEnabled}, gizmo {ctl.Gizmo}, badge '{ctl.ModeText}', hint '{ctl.Hint ?? ""}', readout '{ctl.Readout ?? ""}', ghost {doc.Scene.HasGhost("saved")}");
        await ctx.SettleAsync();

        static bool On(string value) => value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
