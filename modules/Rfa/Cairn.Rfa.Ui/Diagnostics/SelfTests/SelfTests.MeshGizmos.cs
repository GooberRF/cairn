using System.Globalization;
using System.Numerics;
using System.Windows;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.ViewModels.MeshEditing;
using Cairn.Rfa.Ui.ViewModels.PoseEditing;
using Cairn.Rfa.Ui.Viewport;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Vector = System.Windows.Vector;

namespace Cairn.Rfa.Ui.Diagnostics.SelfTests;

/// <summary>
/// Self-tests of the mesh tabs' viewport gizmos (<see cref="MeshEditController"/>), driven the way the pose
/// self-tests drive the clip controller: bone bind moves and turns in each space with and without children
/// follow (each equal to the corresponding <c>MeshEdit.SetBoneBind</c> call, one labelled undo step, cancel
/// restoring the exact snapshot), the bind pose shown while a bind is edited, several selected bones,
/// collision sphere moves (attached and unattached, in the bind pose and in a clip's pose: the model-space
/// centre ends where it was dragged) and radius drags, prop point moves and turns with every LOD's copy kept
/// equal, picking spheres and prop points in the viewport (and cycling through overlapping things), the mouse
/// path of the arrows and the radius grip, the shared tool and space, and no gizmo on a read-only .v3m. Uses
/// the repository's sample figure, the stock ult2_guard.v3c and a .v3m when the research corpus is present;
/// opens and closes its own tabs and leaves nothing changed.
/// </summary>
internal static class MeshGizmoSelfTests
{
    private const float Millimetre = 0.001f;

    [SelfTest("meshgizmo", Order = 660)]
    public static async Task MeshGizmos(SelfTestContext ctx)
    {
        var model = ctx.Model;
        var startActive = model.ActiveDocument;
        var clipCtl = ctx.Clip is { } clipDoc ? PoseEditController.For(clipDoc) : null;
        var opened = new List<DocumentViewModel>();
        var display = model.Display;
        var saved = (model.PoseSettings.Tool, model.PoseSettings.Space, model.BindOptions.ChildrenFollow,
            display.ShowSpheres, display.ShowProps, display.BindPose, display.ShowSkeleton);
        try
        {
            display.BindPose = false;
            display.ShowSkeleton = true;
            string? root = LocalPaths.RepositoryRoot;
            string? sample = root is null ? null : Path.Combine(root, "samples", "rfa", "sample_figure.v3c");
            if (sample is not null && File.Exists(sample) && await Open(ctx, sample, opened) is { } figure)
            {
                string walkPath = Path.Combine(root!, "samples", "rfa", "sample_figure_walk.rfa");
                var walk = File.Exists(walkPath) ? RfaReader.Read(await File.ReadAllBytesAsync(walkPath), "sample_figure_walk.rfa") : null;
                await RunAll(ctx, figure, walk, clipCtl, rotateFirst: true);
            }
            else
            {
                ctx.Log("selftest meshgizmo: samples/sample_figure.v3c not found; the sample checks are skipped");
            }

            string? guard = LocalPaths.CorpusFile("ult2_guard.v3c");
            if (guard is not null && await Open(ctx, guard, opened) is { } stock)
            {
                string? walkPath = LocalPaths.CorpusFile("ult2_walk.rfa");
                var walk = walkPath is null ? null : RfaReader.Read(await File.ReadAllBytesAsync(walkPath), "ult2_walk.rfa");
                if (walk is not null && walk.BoneCount != stock.Current.Bones.Length) walk = null;
                await RunAll(ctx, stock, walk, null, rotateFirst: false);
            }
            else
            {
                ctx.Log("selftest meshgizmo: the research corpus (ult2_guard.v3c) is absent; the stock character checks are skipped");
            }

            if (LocalPaths.Corpus is { } corpus && Directory.EnumerateFiles(corpus, "*.v3m").FirstOrDefault() is { } v3m
                && await Open(ctx, v3m, opened) is { } staticDoc)
            {
                ReadOnlyChecks(ctx, staticDoc);
            }
            else
            {
                ctx.Log("selftest meshgizmo: no .v3m in the research corpus; the read-only check is skipped");
            }
        }
        finally
        {
            foreach (var d in opened)
            {
                if (d is MeshDocumentViewModel m && MeshEditController.For(m) is { IsDragging: true } c) c.CancelDrag();
                model.CloseDiscarding(d);
            }
            model.PoseSettings.Tool = saved.Tool;
            model.PoseSettings.Space = saved.Space;
            model.BindOptions.ChildrenFollow = saved.ChildrenFollow;
            display.ShowSpheres = saved.ShowSpheres;
            display.ShowProps = saved.ShowProps;
            display.BindPose = saved.BindPose;
            display.ShowSkeleton = saved.ShowSkeleton;
            if (startActive is not null && model.Documents.Contains(startActive)) model.ActiveDocument = startActive;
            await ctx.SettleAsync();
        }
    }

    private static async Task<MeshDocumentViewModel?> Open(SelfTestContext ctx, string path, List<DocumentViewModel> opened)
    {
        bool wasOpen = ctx.Model.Documents.Any(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (ctx.Model.OpenFile(path) is not MeshDocumentViewModel doc)
        {
            ctx.Check(false, $"meshgizmo: {Path.GetFileName(path)} opens as a mesh document");
            return null;
        }
        if (!wasOpen) opened.Add(doc);
        ctx.Model.ActiveDocument = doc;
        await ctx.SettleAsync();
        // A known state: no preview clip (a library pick may still be loading; this supersedes it).
        doc.UsePreviewClipForTest(null);
        await ctx.YieldAsync();
        return doc;
    }

    private static async Task RunAll(SelfTestContext ctx, MeshDocumentViewModel doc, RfaClip? walk, PoseEditController? clipCtl, bool rotateFirst)
    {
        var ctl = MeshEditController.For(doc);
        var original = doc.Current;
        int undoBase = doc.History.UndoLabels.Count;
        string tag = doc.DisplayName;
        ctx.Log($"selftest meshgizmo: {tag}, {original.Bones.Length} bones, {original.CollisionSpheres.Count()} spheres, {MeshEdit.PropPointCount(original)} prop points");
        try
        {
            // The sample figure's binds are all unrotated, so Local, Parent and Model would agree: turn two of them first.
            if (rotateFirst && original.Bones.Length > 5)
            {
                doc.Apply("Test: turn binds", m =>
                {
                    m = MeshEdit.SetBoneBind(m, 1, new Rigid(Quat.FromAxisAngle(Vector3.UnitY, 0.4f), MeshEdit.GetBoneBind(m, 1, BindSpace.World).Position), BindSpace.World, childrenFollow: true);
                    var arm = MeshEdit.GetBoneBind(m, 3, BindSpace.World);
                    m = MeshEdit.SetBoneBind(m, 3, new Rigid(Quat.Mul(Quat.FromAxisAngle(Vector3.UnitZ, 0.5f), arm.Rotation), arm.Position), BindSpace.World, childrenFollow: true);
                    var local = MeshEdit.GetBoneBind(m, 4, BindSpace.Local);
                    return MeshEdit.SetBoneBind(m, 4, new Rigid(Quat.Mul(local.Rotation, Quat.FromAxisAngle(Vector3.UnitX, 0.35f)), local.Position), BindSpace.Local, childrenFollow: true);
                });
            }
            SharedSettingsChecks(ctx, doc, ctl, clipCtl);
            BoneChecks(ctx, doc, ctl, walk);
            SphereChecks(ctx, doc, ctl, walk);
            PropChecks(ctx, doc, ctl, walk);
            await PickChecks(ctx, doc, ctl);
            await MousePathChecks(ctx, doc, ctl);
        }
        finally
        {
            if (ctl.IsDragging) ctl.CancelDrag();
            doc.UsePreviewClipForTest(null);
            while (doc.History.UndoLabels.Count > undoBase && doc.CanUndo) doc.Undo();
            ctl.Tool = PoseTool.Select;
        }
        ctx.Check(ReferenceEquals(doc.Current, original) && doc.History.UndoLabels.Count == undoBase && ReferenceEquals(doc.Scene.Mesh, original),
            $"meshgizmo {tag}: the mesh is left as found (every step undone)");
    }

    private static bool Same(V3dFile a, V3dFile b) => V3dWriter.Write(a).AsSpan().SequenceEqual(V3dWriter.Write(b));

    private static string Fmt(float metres) => (metres * 1000).ToString("0.000", CultureInfo.InvariantCulture) + " mm";

    // ── Shared tool and space ────────────────────────────────────────────────

    private static void SharedSettingsChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl, PoseEditController? clipCtl)
    {
        ctl.Tool = PoseTool.Move;
        ctl.Space = OffsetSpace.Parent;
        bool shared = ctx.Model.PoseSettings.Tool == PoseTool.Move && ctx.Model.PoseSettings.Space == OffsetSpace.Parent
            && (clipCtl is null || (clipCtl.Tool == PoseTool.Move && clipCtl.Space == OffsetSpace.Parent));
        ctx.Check(shared, $"meshgizmo {doc.DisplayName}: the tool and space are the clip tabs' (shared and persisted){(clipCtl is null ? " (no clip tab to compare)" : "")}");
        ctl.Tool = PoseTool.Select;
        ctx.Check(!ctl.BadgeVisible && ctl.Gizmo == PoseGizmo.None, "meshgizmo: the Select tool shows no gizmo and no badge");
    }

    // ── Bone binds ───────────────────────────────────────────────────────────

    private static void BoneChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl, RfaClip? walk)
    {
        var mesh = doc.Current;
        var skeleton0 = Skeleton.FromFile(mesh);
        int n = mesh.Bones.Length;
        int k = Enumerable.Range(0, n).FirstOrDefault(b => skeleton0.EffectiveParents[b] >= 0 && skeleton0.EffectiveParents[skeleton0.EffectiveParents[b]] >= 0 && skeleton0.EffectiveParents.Contains(b), -1);
        if (k < 0)
        {
            ctx.Log("selftest meshgizmo: no bone with a grandparent and a child; bone checks skipped");
            return;
        }
        int parent = skeleton0.EffectiveParents[k];
        int child = Enumerable.Range(0, n).First(b => skeleton0.EffectiveParents[b] == k);
        string name = mesh.Bones[k].Name.Text;
        doc.Selection.Select(k);
        ctl.Tool = PoseTool.Move;
        ctx.Check(ctl.Target == MeshGizmoTarget.Bone && ctl.TargetIndex == k && ctl.Gizmo == PoseGizmo.Move && ctl.BadgeKind == PoseBadgeKind.Mesh
            && ctl.ModeText.StartsWith("Bind pose", StringComparison.Ordinal),
            $"meshgizmo bone: selecting {name} with the move tool shows its bind gizmo ('{ctl.ModeText}')");

        foreach (var follow in new[] { false, true })
        {
            ctx.Model.BindOptions.ChildrenFollow = follow;
            foreach (var space in new[] { OffsetSpace.Local, OffsetSpace.Parent, OffsetSpace.Model })
            {
                ctl.Space = space;
                foreach (var rotate in new[] { false, true })
                {
                    ctl.Tool = rotate ? PoseTool.Rotate : PoseTool.Move;
                    var before = doc.Current;
                    var sk = Skeleton.FromFile(before);
                    var w = sk.RestWorld[k];
                    var l = sk.RestLocal[k];
                    var p = sk.RestWorld[parent];
                    var childWorld = sk.RestWorld[child];
                    var frame = space switch { OffsetSpace.Local => w.Rotation, OffsetSpace.Parent => p.Rotation, _ => Quaternion.Identity };
                    int labels = doc.History.UndoLabels.Count;
                    var roots = doc.Structure.Roots.FirstOrDefault();
                    bool began = ctl.BeginDrag(rotate ? PoseDragKind.Rotate : PoseDragKind.Move, rotate ? GizmoHandle.Y : GizmoHandle.X);
                    V3dFile expected;
                    Vector3 delta = default;
                    Quaternion r = Quaternion.Identity;
                    if (rotate)
                    {
                        ctl.UpdateAxisRotation(GizmoHandle.Y, 0.15);
                        ctl.UpdateAxisRotation(GizmoHandle.Y, 0.35);
                        r = Quat.FromAxisAngle(Vector3.UnitY, (float)0.35);
                        expected = space switch
                        {
                            OffsetSpace.Model => MeshEdit.SetBoneBind(before, k, new Rigid(Quat.Mul(r, w.Rotation), w.Position), BindSpace.World, follow),
                            OffsetSpace.Local => MeshEdit.SetBoneBind(before, k, new Rigid(Quat.Mul(l.Rotation, r), l.Position), BindSpace.Local, follow),
                            _ => MeshEdit.SetBoneBind(before, k, new Rigid(Quat.Mul(r, l.Rotation), l.Position), BindSpace.Local, follow),
                        };
                    }
                    else
                    {
                        ctl.UpdateAxisTranslation(GizmoHandle.X, 0.02);
                        ctl.UpdateAxisTranslation(GizmoHandle.X, 0.05);
                        delta = Quat.Rotate(frame, Vector3.UnitX * (float)0.05);
                        expected = space == OffsetSpace.Model
                            ? MeshEdit.SetBoneBind(before, k, new Rigid(w.Rotation, w.Position + delta), BindSpace.World, follow)
                            : MeshEdit.SetBoneBind(before, k, new Rigid(l.Rotation, l.Position + Quat.Rotate(Quat.Conj(p.Rotation), delta)), BindSpace.Local, follow);
                    }
                    string? readout = ctl.Readout;
                    // Live: the viewport has the edited bind, the tree has not been rebuilt, the editor's numbers follow.
                    bool live = ReferenceEquals(doc.Scene.Mesh, doc.Current) && ReferenceEquals(doc.Structure.Roots.FirstOrDefault(), roots)
                        && doc.Structure.Editor is BoneEditor be && Math.Abs(be.Position[0].Value - MeshEdit.GetBoneBind(doc.Current, k, be.Space).Position.X) < 1e-4;
                    ctl.CommitDrag();
                    var after = doc.Current;
                    string what = $"meshgizmo bone {(rotate ? "rotate" : "move")} {space}{(follow ? " with children" : "")}";
                    string label = $"{(rotate ? "Rotate" : "Move")} bone {name} bind{(follow ? " with its children" : "")}";
                    ctx.Check(began && Same(after, expected), $"{what}: equals MeshEdit.SetBoneBind on the pre-drag mesh byte for byte (readout '{readout}')");
                    ctx.Check(doc.History.UndoLabels.Count == labels + 1 && doc.UndoLabel == label, $"{what}: one undo step '{doc.UndoLabel}'");
                    ctx.Check(live && !ReferenceEquals(doc.Structure.Roots.FirstOrDefault(), roots), $"{what}: shown live during the drag (editor numbers follow, tree rebuilt only at the end)");
                    var sk1 = Skeleton.FromFile(after);
                    var w1 = sk1.RestWorld[k];
                    var c1 = sk1.RestWorld[child];
                    if (rotate)
                    {
                        var turn = Quat.Mul(frame, Quat.Mul(r, Quat.Conj(frame)));
                        double err = Quat.AngleDegrees(Quat.Mul(turn, w.Rotation), w1.Rotation);
                        var childExpected = follow ? w.Position + Quat.Rotate(turn, childWorld.Position - w.Position) : childWorld.Position;
                        ctx.Check(err < 0.01 && Vector3.Distance(w1.Position, w.Position) < 1e-5f && Vector3.Distance(c1.Position, childExpected) < 1e-4f,
                            $"{what}: the bone turned 20° about the gizmo's Y axis on its joint ({err:0.0000}°), its child {(follow ? "turned with it" : "stayed")} ({Fmt(Vector3.Distance(c1.Position, childExpected))})");
                    }
                    else
                    {
                        float err = Vector3.Distance(w1.Position, w.Position + delta);
                        float childErr = Vector3.Distance(c1.Position, childWorld.Position + (follow ? delta : Vector3.Zero));
                        ctx.Check(err < 1e-5f && childErr < 1e-5f,
                            $"{what}: the joint moved 5 cm along the gizmo's X axis ({Fmt(err)}), its child {(follow ? "moved with it" : "stayed")} ({Fmt(childErr)})");
                    }
                    doc.Undo();
                    ctx.Check(ReferenceEquals(doc.Current, before) && ReferenceEquals(doc.Scene.Mesh, before), $"{what}: undo restores the exact mesh");

                    // Esc: the exact snapshot back, no step.
                    ctl.BeginDrag(rotate ? PoseDragKind.Rotate : PoseDragKind.Move, GizmoHandle.Z);
                    if (rotate) ctl.UpdateAxisRotation(GizmoHandle.Z, 0.4);
                    else ctl.UpdateAxisTranslation(GizmoHandle.Z, 0.08);
                    bool changed = !ReferenceEquals(doc.Current, before);
                    ctl.CancelDrag();
                    ctx.Check(changed && ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == labels && ReferenceEquals(doc.Scene.Mesh, before) && !doc.IsLiveEditing,
                        $"{what}: Esc restores the exact pre-drag mesh (viewport included) and adds no step");
                }
            }
        }

        // Drag cost: median per mouse move (edit + live refresh), judged only when grossly off.
        {
            ctx.Model.BindOptions.ChildrenFollow = true;
            ctl.Space = OffsetSpace.Local;
            ctl.Tool = PoseTool.Move;
            ctl.BeginDrag(PoseDragKind.Move, GizmoHandle.X);
            ctl.UpdateAxisTranslation(GizmoHandle.X, 0.001);
            var each = new List<double>(20);
            for (int step = 1; step <= 20; step++)
            {
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                ctl.UpdateAxisTranslation(GizmoHandle.X, step * 0.002);
                each.Add((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            }
            ctl.CancelDrag();
            double median = TimelineSelfTests.Median(each);
            ctx.Log(string.Format(CultureInfo.InvariantCulture, "timing: mesh bind drag frame (edit + live refresh), median {0:0.00} ms on {1} bones", median, n));
            ctx.Check(median < 100, $"meshgizmo bone: a bind drag frame is not grossly slow (median {median:0.00} ms, limit 100 ms)");
        }

        // The screen ring and the free trackball give a model-space turn (expressed in the gizmo's frame).
        {
            ctx.Model.BindOptions.ChildrenFollow = false;
            ctl.Space = OffsetSpace.Local;
            ctl.Tool = PoseTool.Rotate;
            var before = doc.Current;
            var w = Skeleton.FromFile(before).RestWorld[k];
            var world = Quat.FromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 0)), 0.3f);
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Free);
            ctl.UpdateWorldRotation(world, GizmoHandle.Free);
            ctl.CommitDrag();
            double err = Quat.AngleDegrees(Quat.Mul(world, w.Rotation), Skeleton.FromFile(doc.Current).RestWorld[k].Rotation);
            ctx.Check(err < 0.01, $"meshgizmo bone: a free (trackball) turn turns the bind by that model-space rotation ({err:0.0000}°)");
            doc.Undo();
        }

        // The bind pose shows while a bone's bind is edited; Select brings the clip back.
        if (walk is not null)
        {
            doc.UsePreviewClipForTest(walk);
            doc.Playback.Seek(walk.StartTime + (walk.EndTime - walk.StartTime) / 3);
            ctl.Tool = PoseTool.Select;
            bool animated = !doc.Scene.ShowingBindPose;
            ctl.Tool = PoseTool.Move;
            bool forced = doc.Scene.ShowingBindPose && doc.Scene.ForceBindPose && !doc.Display.BindPose
                && ctl.Hint?.Contains("bind pose", StringComparison.Ordinal) == true;
            var restJoint = Skeleton.FromFile(doc.Current).RestWorld[k].Position;
            bool gizmoOnRest = ctl.TryGetPlacement(doc.Scene.Pose, out var centre, out _) && Vector3.Distance(centre, restJoint) < 1e-5f
                && doc.Scene.Pose is { } pose && Vector3.Distance(pose.World[k].Position, restJoint) < 1e-4f;
            ctl.Tool = PoseTool.Select;
            bool back = !doc.Scene.ShowingBindPose && !doc.Scene.ForceBindPose;
            ctx.Check(animated && forced && gizmoOnRest && back,
                $"meshgizmo bone: with a preview clip playing, a bind gizmo shows the bind pose and says so ('{(forced ? "bind pose shown" : "not shown")}'), the gizmo on the rest joint; Select brings the clip back");
            doc.UsePreviewClipForTest(null);
        }

        // Several selected bones.
        {
            ctl.Tool = PoseTool.Rotate;
            ctl.Space = OffsetSpace.Local;
            ctx.Model.BindOptions.ChildrenFollow = false;
            doc.Selection.Set([parent, k]);
            var before = doc.Current;
            var sk = Skeleton.FromFile(before);
            var r = Quat.FromAxisAngle(Vector3.UnitX, (float)0.3);
            Rigid Target(int b) => new(Quat.Mul(sk.RestWorld[sk.EffectiveParents[b]].Rotation, Quat.Mul(sk.RestLocal[b].Rotation, r)), sk.RestWorld[b].Position);
            var expected = MeshEdit.SetBoneBind(MeshEdit.SetBoneBind(before, parent, Target(parent), BindSpace.World, false), k, Target(k), BindSpace.World, false);
            bool both = ctl.EditBones.SequenceEqual([parent, k]) && !ctl.IsReducedToActive && ctl.ModeText.Contains("2 bones", StringComparison.Ordinal);
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
            ctl.UpdateAxisRotation(GizmoHandle.X, 0.3);
            ctl.CommitDrag();
            var sk1 = Skeleton.FromFile(doc.Current);
            double err = Math.Max(Quat.AngleDegrees(Quat.Mul(sk.RestWorld[parent].Rotation, r), sk1.RestWorld[parent].Rotation),
                Quat.AngleDegrees(Quat.Mul(sk.RestWorld[k].Rotation, r), sk1.RestWorld[k].Rotation));
            ctx.Check(both && Same(doc.Current, expected) && err < 0.01 && doc.UndoLabel == "Rotate 2 bone binds",
                $"meshgizmo bones: two selected bones turn together, each about its own axes ({err:0.0000}°, '{doc.UndoLabel}')");
            doc.Undo();

            ctx.Model.BindOptions.ChildrenFollow = true;
            var l = sk.RestLocal[k];
            var single = MeshEdit.SetBoneBind(before, k, new Rigid(Quat.Mul(l.Rotation, r), l.Position), BindSpace.Local, true);
            bool reduced = ctl.IsReducedToActive && ctl.EditBones.SequenceEqual([k]) && ctl.Hint?.Contains("only", StringComparison.Ordinal) == true;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
            ctl.UpdateAxisRotation(GizmoHandle.X, 0.3);
            ctl.CommitDrag();
            ctx.Check(reduced && Same(doc.Current, single) && doc.UndoLabel == $"Rotate bone {name} bind with its children",
                $"meshgizmo bones: with children follow and a bone selected below another, only the active bone is edited and the badge says so ('{ctl.Hint}')");
            doc.Undo();
            ctx.Model.BindOptions.ChildrenFollow = false;
            doc.Selection.Select(k);
        }
        ctl.Tool = PoseTool.Select;
    }

    // ── Collision spheres ────────────────────────────────────────────────────

    private static void SphereChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl, RfaClip? walk)
    {
        var mesh = doc.Current;
        int i = Enumerable.Range(0, mesh.CollisionSpheres.Count()).FirstOrDefault(s => mesh.CollisionSpheres.ElementAt(s).BoneIndex is >= 0 and var b && b < mesh.Bones.Length, -1);
        if (i < 0)
        {
            ctx.Log("selftest meshgizmo: no collision sphere on a bone; sphere checks skipped");
            return;
        }
        string name = mesh.CollisionSpheres.ElementAt(i).Name.Text;
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, i));
        ctl.Tool = PoseTool.Rotate;
        ctx.Check(ctl.Target == MeshGizmoTarget.Sphere && ctl.Gizmo == PoseGizmo.None && ctl.Hint?.Contains("no orientation", StringComparison.Ordinal) == true
            && !ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X) && doc.Scene.HighlightSphere == i,
            $"meshgizmo sphere: the Rotate tool has no gizmo on a sphere and the badge says why ('{ctl.Hint}')");
        ctl.Tool = PoseTool.Move;
        ctx.Check(ctl.Gizmo == PoseGizmo.Move && ctl.HasRadiusHandle && !doc.Scene.ForceBindPose, $"meshgizmo sphere: the Move tool shows the move gizmo and the radius handle ('{ctl.ModeText}')");

        void Move(string when, OffsetSpace space, Action<MeshEditController> drag, Func<Quaternion, Vector3> deltaFor)
        {
            ctl.Space = space;
            var before = doc.Current;
            var s = before.CollisionSpheres.ElementAt(i);
            var pose = doc.Scene.Pose;
            var bone = s.BoneIndex >= 0 && pose is not null && s.BoneIndex < pose.World.Length ? pose.World[s.BoneIndex] : Rigid.Identity;
            var centre0 = bone.TransformPoint(s.Position);
            var frame = space == OffsetSpace.Model ? Quaternion.Identity : bone.Rotation;
            var delta = deltaFor(frame);
            int labels = doc.History.UndoLabels.Count;
            bool began = ctl.BeginDrag(PoseDragKind.Move, GizmoHandle.X);
            drag(ctl);
            bool liveEditor = doc.Structure.Editor is SphereEditor se && Math.Abs(se.Position[0].Value - doc.Current.CollisionSpheres.ElementAt(i).Position.X) < 1e-5;
            ctl.CommitDrag();
            var after = doc.Current.CollisionSpheres.ElementAt(i);
            var expected = MeshEdit.SetCollisionSphere(before, i, s.Name.Text, s.BoneIndex, s.Position + Quat.Rotate(Quat.Conj(bone.Rotation), delta), s.Radius);
            float err = Vector3.Distance(bone.TransformPoint(after.Position), centre0 + delta);
            ctx.Check(began && Same(doc.Current, expected) && err < 1e-5f && liveEditor && doc.History.UndoLabels.Count == labels + 1 && doc.UndoLabel == $"Move collision sphere '{name}'",
                $"meshgizmo sphere {when} ({space}): the model-space centre ends where it was dragged ({Fmt(err)}), equal to MeshEdit.SetCollisionSphere, one step '{doc.UndoLabel}'");
            doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, before), $"meshgizmo sphere {when}: undo restores the exact mesh");
        }

        Move("attached, bind pose", OffsetSpace.Model, c => c.UpdateAxisTranslation(GizmoHandle.X, 0.1), f => Quat.Rotate(f, Vector3.UnitX * (float)0.1));
        Move("attached, bind pose", OffsetSpace.Local, c => c.UpdateAxisTranslation(GizmoHandle.X, 0.07), f => Quat.Rotate(f, Vector3.UnitX * (float)0.07));
        if (walk is not null)
        {
            doc.UsePreviewClipForTest(walk);
            doc.Playback.Seek(walk.StartTime + (walk.EndTime - walk.StartTime) * 2 / 3);
            bool animated = !doc.Scene.ShowingBindPose && ctl.Hint?.Contains("preview clip's pose", StringComparison.Ordinal) == true;
            ctx.Check(animated, $"meshgizmo sphere: shown in the preview clip's pose, the badge says it is stored relative to its bone ('{ctl.Hint}')");
            Move("attached, animated pose", OffsetSpace.Local, c => c.UpdateAxisTranslation(GizmoHandle.X, 0.06), f => Quat.Rotate(f, Vector3.UnitX * (float)0.06));
            var screen = new Vector3(0.03f, -0.04f, 0.05f);
            Move("attached, animated pose, screen square", OffsetSpace.Model, c => c.UpdateWorldTranslation(screen), _ => screen);
            doc.UsePreviewClipForTest(null);
        }

        // Unattached: stored in model space.
        {
            var s = doc.Current.CollisionSpheres.ElementAt(i);
            doc.Apply("Test: detach sphere", m => MeshEdit.SetCollisionSphere(m, i, s.Name.Text, -1, s.Position, s.Radius));
            ctx.Check(ctl.ModeText.Contains("the model", StringComparison.Ordinal), $"meshgizmo sphere: an unattached sphere is stored relative to the model ('{ctl.ModeText}')");
            Move("unattached", OffsetSpace.Parent, c => c.UpdateAxisTranslation(GizmoHandle.X, 0.04), f => Quat.Rotate(f, Vector3.UnitX * (float)0.04));
            if (walk is not null)
            {
                doc.UsePreviewClipForTest(walk);
                Move("unattached, animated pose", OffsetSpace.Model, c => c.UpdateAxisTranslation(GizmoHandle.X, 0.04), f => Quat.Rotate(f, Vector3.UnitX * (float)0.04));
                doc.UsePreviewClipForTest(null);
            }
            doc.Undo();
        }

        // Radius.
        {
            var before = doc.Current;
            var s = before.CollisionSpheres.ElementAt(i);
            int labels = doc.History.UndoLabels.Count;
            bool began = ctl.BeginRadiusDrag();
            ctl.UpdateRadius(s.Radius + 0.02);
            ctl.UpdateRadius(s.Radius + 0.05);
            string? readout = ctl.Readout;
            bool liveEditor = doc.Structure.Editor is SphereEditor se && Math.Abs(se.Radius.Value - (s.Radius + 0.05)) < 1e-4 && Math.Abs(ctl.GizmoRadius - (s.Radius + 0.05)) < 1e-5;
            ctl.CommitDrag();
            var expected = MeshEdit.SetCollisionSphere(before, i, s.Name.Text, s.BoneIndex, s.Position, (float)(s.Radius + 0.05));
            ctx.Check(began && Same(doc.Current, expected) && liveEditor && doc.History.UndoLabels.Count == labels + 1 && doc.UndoLabel == $"Resize collision sphere '{name}'"
                && readout?.StartsWith("Radius ", StringComparison.Ordinal) == true,
                $"meshgizmo sphere radius: the radius drag sets the radius (readout '{readout}'), one step '{doc.UndoLabel}'");
            doc.Undo();
            ctl.BeginRadiusDrag();
            ctl.UpdateRadius(-1);
            float smallest = doc.Current.CollisionSpheres.ElementAt(i).Radius;
            ctl.CancelDrag();
            ctx.Check(Math.Abs(smallest - MeshEditController.MinimumRadius) < 1e-7 && ReferenceEquals(doc.Current, before) && doc.History.UndoLabels.Count == labels,
                $"meshgizmo sphere radius: the radius never goes below {MeshEditController.MinimumRadius} m, and Esc restores the exact mesh");
        }
        ctl.Tool = PoseTool.Select;
    }

    // ── Prop points ──────────────────────────────────────────────────────────

    private static void PropChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl, RfaClip? walk)
    {
        var mesh = doc.Current;
        int count = MeshEdit.PropPointCount(mesh);
        if (count == 0)
        {
            ctx.Log("selftest meshgizmo: no prop points; prop checks skipped");
            return;
        }
        int p = Enumerable.Range(0, count).FirstOrDefault(i => PropPointEditor.Find(mesh, i) is { ParentIndex: >= 0 }, 0);
        string name = PropPointEditor.Prop(mesh, p).Name.Text;
        int lods = mesh.Submeshes.Sum(s => s.Lods.Length);
        doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, p));

        bool Consistent(V3dFile m)
        {
            var reference = PropPointEditor.Prop(m, p);
            return m.Submeshes.SelectMany(s => s.Lods).Where(l => !l.PropPoints.IsDefault && l.PropPoints.Length > p).All(l => l.PropPoints[p] == reference);
        }

        void Drag(string when, PoseTool tool, OffsetSpace space)
        {
            ctl.Tool = tool;
            ctl.Space = space;
            bool rotate = tool == PoseTool.Rotate;
            var before = doc.Current;
            var prop = PropPointEditor.Prop(before, p);
            var pose = doc.Scene.Pose;
            var bone = FrameEdit.ParentFrame(pose is null ? default : pose.World, prop.ParentIndex);
            var orientation = Quat.Conj(Quat.Normalize(prop.Rotation));
            var modelRotation = Quat.Mul(bone.Rotation, orientation);
            var frame = space switch { OffsetSpace.Local => Quat.Normalize(modelRotation), OffsetSpace.Parent => bone.Rotation, _ => Quaternion.Identity };
            int labels = doc.History.UndoLabels.Count;
            bool began = ctl.BeginDrag(rotate ? PoseDragKind.Rotate : PoseDragKind.Move, GizmoHandle.Z);
            V3dFile expected;
            if (rotate)
            {
                ctl.UpdateAxisRotation(GizmoHandle.Z, 0.5);
                var r = Quat.FromAxisAngle(Vector3.UnitZ, (float)0.5);
                var next = space switch
                {
                    OffsetSpace.Local => Quat.Mul(orientation, r),
                    OffsetSpace.Parent => Quat.Mul(r, orientation),
                    _ => Quat.Mul(Quat.Conj(bone.Rotation), Quat.Mul(r, Quat.Mul(bone.Rotation, orientation))),
                };
                expected = MeshEdit.SetPropPoint(before, p, prop.Name.Text, prop.ParentIndex, Quat.Align(Quat.Conj(Quat.Normalize(next)), prop.Rotation), prop.Position);
            }
            else
            {
                ctl.UpdateAxisTranslation(GizmoHandle.Z, 0.06);
                var delta = Quat.Rotate(frame, Vector3.UnitZ * (float)0.06);
                expected = MeshEdit.SetPropPoint(before, p, prop.Name.Text, prop.ParentIndex, prop.Rotation, prop.Position + Quat.Rotate(Quat.Conj(bone.Rotation), delta));
            }
            ctl.CommitDrag();
            var after = PropPointEditor.Prop(doc.Current, p);
            string label = $"{(rotate ? "Rotate" : "Move")} prop point '{name}'";
            bool placed;
            string detail;
            if (rotate)
            {
                var turn = Quat.Mul(frame, Quat.Mul(Quat.FromAxisAngle(Vector3.UnitZ, 0.5f), Quat.Conj(frame)));
                double err = Quat.AngleDegrees(Quat.Mul(turn, modelRotation), Quat.Mul(bone.Rotation, Quat.Conj(Quat.Normalize(after.Rotation))));
                placed = err < 0.01 && after.Position == prop.Position && Quat.Dot(after.Rotation, prop.Rotation) > 0;
                detail = $"{err:0.0000}° from the expected model orientation, stored in the old hemisphere";
            }
            else
            {
                var delta = Quat.Rotate(frame, Vector3.UnitZ * 0.06f);
                float err = Vector3.Distance(bone.TransformPoint(after.Position), bone.TransformPoint(prop.Position) + delta);
                placed = err < 1e-5f && after.Rotation == prop.Rotation;
                detail = $"{Fmt(err)} from where it was dragged";
            }
            ctx.Check(began && Same(doc.Current, expected) && placed && Consistent(doc.Current) && doc.History.UndoLabels.Count == labels + 1 && doc.UndoLabel == label,
                $"meshgizmo prop {(rotate ? "rotate" : "move")} {space} {when}: equals MeshEdit.SetPropPoint ({detail}), every one of the {lods} LODs' copies equal, one step '{doc.UndoLabel}'");
            doc.Undo();
            ctx.Check(ReferenceEquals(doc.Current, before), $"meshgizmo prop {when}: undo restores the exact mesh");
        }

        foreach (var space in new[] { OffsetSpace.Local, OffsetSpace.Parent, OffsetSpace.Model })
        {
            Drag("(bind pose)", PoseTool.Move, space);
            Drag("(bind pose)", PoseTool.Rotate, space);
        }
        if (walk is not null)
        {
            doc.UsePreviewClipForTest(walk);
            doc.Playback.Seek(walk.StartTime + (walk.EndTime - walk.StartTime) / 2);
            Drag("(animated pose)", PoseTool.Move, OffsetSpace.Model);
            Drag("(animated pose)", PoseTool.Rotate, OffsetSpace.Model);
            doc.UsePreviewClipForTest(null);
        }
        {
            ctl.Tool = PoseTool.Rotate;
            var before = doc.Current;
            ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
            ctl.UpdateAxisRotation(GizmoHandle.X, 1.0);
            bool changed = !ReferenceEquals(doc.Current, before);
            ctl.CancelDrag();
            ctx.Check(changed && ReferenceEquals(doc.Current, before), "meshgizmo prop: Esc restores the exact pre-drag mesh");
        }
        ctl.Tool = PoseTool.Select;
    }

    // ── Picking ──────────────────────────────────────────────────────────────

    private static ViewportControl? ViewportOf(DocumentViewModel doc) =>
        ViewportControl.Visible().FirstOrDefault(v => ReferenceEquals(v.DataContext, doc));

    private static async Task PickChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl)
    {
        var viewport = ViewportOf(doc);
        if (viewport is null)
        {
            ctx.Log("selftest meshgizmo: no visible viewport; picking checks skipped");
            return;
        }
        ctl.Tool = PoseTool.Select;
        doc.Display.ShowSpheres = true;
        doc.Display.ShowProps = true;
        doc.Scene.Camera.SetView(25, 12);
        doc.Scene.RequestFrame(false);
        await ctx.YieldAsync();
        ViewportControl.FlushAll();
        var overlay = viewport.Overlay;
        var size = new Size(viewport.Overlay.ActualWidth, viewport.Overlay.ActualHeight);
        string tag = doc.DisplayName;

        // A prop point: its diamond.
        int props = doc.Scene.PropPoints.Count;
        for (int p = 0; p < props; p++)
        {
            if (!overlay.TryGetItem(PickKind.Prop, p, out var at, out _)) continue;
            var best = overlay.PickBest(at, items: true);
            if (best is not { Kind: PickKind.Prop } || best.Value.Index != p) continue;
            viewport.ClickInMesh(at, System.Windows.Input.ModifierKeys.None);
            ctx.Check(doc.Structure.Selected?.Node is { Kind: MeshNodeKind.PropPoint } n && n.Index == p && doc.Structure.Editor is PropPointEditor
                && doc.Scene.HighlightProp == p && doc.SelectedInspectorTab?.Id == "structure" && doc.Selection.Count == 0,
                $"meshgizmo pick {tag}: clicking prop point '{doc.Scene.PropPoints[p].Name.Text}' in the viewport selects its Structure node and editor");
            break;
        }

        // A collision sphere: a point on its outline that nothing sharper covers.
        int spheres = doc.Current.CollisionSpheres.Count();
        bool sphereDone = false;
        for (int s = 0; s < spheres && !sphereDone; s++)
        {
            if (!overlay.TryGetItem(PickKind.Sphere, s, out var c, out double r) || r < 6) continue;
            for (int a = 0; a < 16 && !sphereDone; a++)
            {
                var at = c + new Vector(Math.Cos(a * Math.PI / 8) * r, Math.Sin(a * Math.PI / 8) * r);
                if (at.X < 4 || at.Y < 60 || at.X > size.Width - 4 || at.Y > size.Height - 4) continue;
                var hits = overlay.PickAll(at, items: true);
                if (hits.Count == 0 || hits[0].Kind != PickKind.Sphere || hits[0].Index != s) continue;
                sphereDone = true;
                doc.Selection.Select(0);
                viewport.ClickInMesh(at, System.Windows.Input.ModifierKeys.None);
                ctx.Check(doc.Structure.Selected?.Node is { Kind: MeshNodeKind.CollisionSphere } n && n.Index == s && doc.Structure.Editor is SphereEditor
                    && doc.Scene.HighlightSphere == s && doc.Selection.Count == 0,
                    $"meshgizmo pick {tag}: clicking collision sphere '{doc.Current.CollisionSpheres.ElementAt(s).Name.Text}' on its outline selects its node and editor (the bone selection goes)");
                // Empty space lets it go.
                var empty = new Point(size.Width - 3, size.Height - 3);
                if (overlay.PickAll(empty, items: true).Count == 0)
                {
                    viewport.ClickInMesh(empty, System.Windows.Input.ModifierKeys.None);
                    ctx.Check(doc.Structure.Selected is null && doc.Scene.HighlightSphere < 0, $"meshgizmo pick {tag}: a click on empty space lets the sphere go");
                }
            }
        }
        if (spheres > 0 && !sphereDone) ctx.Log($"selftest meshgizmo {tag}: no sphere outline point free of other hits from this view; sphere pick skipped");

        // Overlapping things: clicking the same spot again picks the next one.
        overlay.PickBest(new Point(-100, -100), items: false); // brings the joints' screen points up to date
        bool cycled = false;
        for (int b = 0; b < doc.Scene.Skeleton.Count && !cycled; b++)
        {
            if (!overlay.TryGetJoint(b, out var joint)) continue;
            var hits = overlay.PickAll(joint, items: true);
            if (hits.Count < 2) continue;
            cycled = true;
            viewport.ClickInMesh(joint, System.Windows.Input.ModifierKeys.None);
            var first = Picked(doc);
            viewport.ClickInMesh(joint, System.Windows.Input.ModifierKeys.None);
            var second = Picked(doc);
            for (int i = 2; i < hits.Count; i++) viewport.ClickInMesh(joint, System.Windows.Input.ModifierKeys.None);
            viewport.ClickInMesh(joint, System.Windows.Input.ModifierKeys.None);
            var wrapped = Picked(doc);
            ctx.Check(first == (hits[0].Kind, hits[0].Index) && second == (hits[1].Kind, hits[1].Index) && wrapped == first,
                $"meshgizmo pick {tag}: clicking the same spot again cycles through the {hits.Count} things under it ({first} then {second}, back to the first)");
        }
        if (!cycled) ctx.Log($"selftest meshgizmo {tag}: no spot with overlapping things from this view; cycling skipped");

        // Tree selection highlights in the viewport.
        if (spheres > 1)
        {
            doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, 1));
            ctx.Check(doc.Scene.HighlightSphere == 1, $"meshgizmo pick {tag}: selecting a sphere in the tree highlights it in the viewport");
        }
        doc.Structure.Selected = null;
        doc.Selection.Clear();
    }

    private static (PickKind, int) Picked(MeshDocumentViewModel doc) => doc.Structure.Selected?.Node switch
    {
        { Kind: MeshNodeKind.CollisionSphere } s => (PickKind.Sphere, s.Index),
        { Kind: MeshNodeKind.PropPoint } p => (PickKind.Prop, p.Index),
        _ => (PickKind.Bone, doc.Selection.Active),
    };

    // ── The mouse path ───────────────────────────────────────────────────────

    private static async Task MousePathChecks(SelfTestContext ctx, MeshDocumentViewModel doc, MeshEditController ctl)
    {
        var viewport = ViewportOf(doc);
        if (viewport is null) return;
        var gizmos = viewport.Gizmos;
        string tag = doc.DisplayName;
        doc.Scene.Camera.SetView(35, 20);

        // A bind move arrow: 5 cm along a model axis that faces the screen.
        var skeleton = Skeleton.FromFile(doc.Current);
        int bone = Enumerable.Range(0, skeleton.Count).FirstOrDefault(b => skeleton.EffectiveParents[b] >= 0, -1);
        if (bone >= 0)
        {
            doc.Selection.Select(bone);
            ctl.Tool = PoseTool.Move;
            ctl.Space = OffsetSpace.Model;
            await ctx.YieldAsync();
            ViewportControl.FlushAll();
            double along = gizmos.ArrowMetres * 0.6;
            int axis = Enumerable.Range(0, 3).FirstOrDefault(a => gizmos.AxisPoint(a, along) is { } p && gizmos.HitTest(p) == a, -1);
            ctx.Check(axis >= 0, $"meshgizmo mouse {tag}: a move arrow on the bone's bind is hit where it is drawn");
            if (axis >= 0)
            {
                var before = doc.Current;
                var w0 = skeleton.RestWorld[bone].Position;
                var grab = gizmos.AxisPoint(axis, along)!.Value;
                var half = gizmos.AxisPoint(axis, along + 0.025)!.Value;
                var full = gizmos.AxisPoint(axis, along + 0.05)!.Value;
                gizmos.TryBeginDrag(grab);
                gizmos.Drag(half, System.Windows.Input.ModifierKeys.None);
                gizmos.Drag(full, System.Windows.Input.ModifierKeys.None);
                string? readout = ctl.Readout;
                gizmos.EndDrag();
                var axisVector = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                float err = Vector3.Distance(Skeleton.FromFile(doc.Current).RestWorld[bone].Position, w0 + axisVector * 0.05f);
                ctx.Check(err < 0.002f && doc.UndoLabel?.StartsWith("Move bone ", StringComparison.Ordinal) == true,
                    $"meshgizmo mouse {tag}: dragging the {GizmoHandle.Name(axis)} arrow 5 cm moves the bind joint 5 cm ({Fmt(err)}, readout '{readout}')");
                if (!ReferenceEquals(doc.Current, before)) doc.Undo();
            }
        }

        // The radius grip: dragging 5 cm further out grows the radius by 5 cm.
        int sphere = doc.Current.CollisionSpheres.Any() ? 0 : -1;
        if (sphere >= 0)
        {
            doc.Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, sphere));
            ctl.Tool = PoseTool.Move;
            await ctx.YieldAsync();
            ViewportControl.FlushAll();
            var grip = gizmos.RadiusGrip;
            ctx.Check(grip is { } g && gizmos.HitTest(g) == GizmoHandle.Radius, $"meshgizmo mouse {tag}: the radius grip is drawn on the selected sphere and hit there");
            if (grip is { } at)
            {
                var before = doc.Current;
                float r0 = ctl.GizmoRadius;
                var p1 = gizmos.RadiusPoint(r0 + 0.1);
                var p2 = gizmos.RadiusPoint(r0 + 0.15);
                bool began = gizmos.TryBeginDrag(at);
                if (p1 is { } a) gizmos.Drag(a, System.Windows.Input.ModifierKeys.None);
                float r1 = ctl.GizmoRadius;
                if (p2 is { } b) gizmos.Drag(b, System.Windows.Input.ModifierKeys.None);
                float r2 = ctl.GizmoRadius;
                string? readout = ctl.Readout;
                gizmos.EndDrag();
                ctx.Check(began && Math.Abs(r2 - r1 - 0.05f) < 0.002f && doc.UndoLabel?.StartsWith("Resize collision sphere", StringComparison.Ordinal) == true,
                    $"meshgizmo mouse {tag}: dragging the radius grip 5 cm further out grows the radius 5 cm ({(r2 - r1) * 100:0.00} cm, readout '{readout}')");
                if (!ReferenceEquals(doc.Current, before)) doc.Undo();
                // Ctrl snaps the radius to 1 cm.
                gizmos.TryBeginDrag(gizmos.RadiusGrip ?? at);
                if (gizmos.RadiusPoint(r0 + 0.123) is { } c) gizmos.Drag(c, System.Windows.Input.ModifierKeys.Control);
                float snapped = ctl.GizmoRadius;
                gizmos.CancelDrag();
                ctx.Check(Math.Abs(snapped * 100 - Math.Round(snapped * 100)) < 1e-3 && ReferenceEquals(doc.Current, before),
                    $"meshgizmo mouse {tag}: Ctrl snaps the radius to whole centimetres ({snapped:0.0000} m), Esc restores it");
            }
        }
        ctl.Tool = PoseTool.Select;
        doc.Structure.Selected = null;
        doc.Selection.Clear();
    }

    // ── Read-only ────────────────────────────────────────────────────────────

    private static void ReadOnlyChecks(SelfTestContext ctx, MeshDocumentViewModel doc)
    {
        var ctl = MeshEditController.For(doc);
        var before = doc.Current;
        ctl.Tool = PoseTool.Move;
        string? hint = ctl.Hint;
        bool none = doc.IsReadOnly && ctl.Gizmo == PoseGizmo.None && ctl.BadgeVisible && hint?.Contains("read-only", StringComparison.Ordinal) == true
            && !ctl.BeginDrag(PoseDragKind.Move, GizmoHandle.X) && !ctl.BeginRadiusDrag();
        ctl.Tool = PoseTool.Rotate;
        none &= ctl.Gizmo == PoseGizmo.None && !ctl.BeginDrag(PoseDragKind.Rotate, GizmoHandle.X);
        ctl.Tool = PoseTool.Select;
        ctx.Check(none && ReferenceEquals(doc.Current, before) && !doc.CanUndo,
            $"meshgizmo: a read-only {doc.DisplayName} shows no gizmo, and the badge says why ('{hint}')");
    }

    // ── Screenshots ──────────────────────────────────────────────────────────

    /// <summary>
    /// Screenshot switches for a mesh tab (the clip tabs' names): <c>--tool rotate|move|select</c>,
    /// <c>--space local|parent|model</c>, <c>--follow on|off</c> (children follow), <c>--preview-file
    /// &lt;clip.rfa&gt;</c> (a preview clip read from a file, at <c>--time</c>), <c>--drag-preview N</c> (degrees
    /// for the rotate gizmo about <c>--drag-axis x|y|z</c>, centimetres for the move gizmo) and
    /// <c>--radius-preview N</c> (centimetres added to the selected sphere's radius), which leave the drag as a
    /// live, uncommitted edit for the capture. Runs after <c>--mesh-node</c> has selected the target.
    /// </summary>
    [ScreenshotStep(660)]
    public static async Task MeshGizmoScreenshot(ScreenshotContext ctx)
    {
        if (ctx.Model.ActiveDocument is not MeshDocumentViewModel doc) return;
        string? tool = ctx.Extra("tool"), space = ctx.Extra("space"), follow = ctx.Extra("follow"), drag = ctx.Extra("drag-preview"),
            axisName = ctx.Extra("drag-axis"), radius = ctx.Extra("radius-preview"), clipFile = ctx.Extra("preview-file");
        if (tool is null && space is null && follow is null && drag is null && radius is null && clipFile is null) return;
        if (clipFile is not null)
        {
            try
            {
                string path = Path.GetFullPath(clipFile);
                doc.UsePreviewClipForTest(RfaReader.Read(await File.ReadAllBytesAsync(path), Path.GetFileName(path)));
                if (ctx.Options.Time is { } t) doc.Playback.Seek(t);
            }
            catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
            {
                ctx.Log($"mesh gizmo: --preview-file could not be read: {ex.Message}");
            }
        }
        var ctl = MeshEditController.For(doc);
        if (follow is not null) ctx.Model.BindOptions.ChildrenFollow = On(follow);
        if (space is not null) ctl.Space = space.ToLowerInvariant() switch { "parent" => OffsetSpace.Parent, "model" => OffsetSpace.Model, _ => OffsetSpace.Local };
        if (tool is not null) ctl.Tool = tool.ToLowerInvariant() switch { "rotate" => PoseTool.Rotate, "move" => PoseTool.Move, _ => PoseTool.Select };
        if (drag is not null && double.TryParse(drag, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount))
        {
            int axis = (axisName ?? "x").ToLowerInvariant() switch { "y" => GizmoHandle.Y, "z" => GizmoHandle.Z, _ => GizmoHandle.X };
            if (ctl.Gizmo == PoseGizmo.Rotate && ctl.BeginDrag(PoseDragKind.Rotate, axis)) ctl.UpdateAxisRotation(axis, amount * Math.PI / 180);
            else if (ctl.Gizmo == PoseGizmo.Move && ctl.BeginDrag(PoseDragKind.Move, axis)) ctl.UpdateAxisTranslation(axis, amount / 100);
            else ctx.Log($"mesh gizmo: --drag-preview needs a gizmo ({ctl.Hint ?? "none"})");
        }
        else if (radius is not null && double.TryParse(radius, NumberStyles.Float, CultureInfo.InvariantCulture, out double cm))
        {
            float r0 = ctl.GizmoRadius;
            if (ctl.BeginRadiusDrag()) ctl.UpdateRadius(r0 + cm / 100);
            else ctx.Log($"mesh gizmo: --radius-preview needs a selected sphere and the move tool ({ctl.Hint ?? "none"})");
        }
        ctx.Log($"mesh gizmo: tool {ctl.Tool}, space {ctl.Space}, target {ctl.Target} {ctl.TargetIndex}, gizmo {ctl.Gizmo}, radius handle {ctl.HasRadiusHandle}, follow {ctl.ChildrenFollow}, badge '{ctl.ModeText}', hint '{ctl.Hint ?? ""}', readout '{ctl.Readout ?? ""}', bind pose shown {doc.Scene.ShowingBindPose}");
        await ctx.SettleAsync();

        static bool On(string value) => value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
