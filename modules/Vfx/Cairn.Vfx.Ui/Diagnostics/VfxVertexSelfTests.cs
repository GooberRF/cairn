using System.Numerics;
using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;
using Cairn.Vfx.Ui.VertexEditing;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Vertex mode self-tests.</summary>
public static class VfxVertexSelfTests
{
    private static (VfxFile File, int Static, int Keyed, int Morph) Synthetic()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("vx_test.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Static"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Keyed"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Morph"), null);
        int Find(VfxFile file, string n) => Enumerable.Range(0, file.Sections.Length).First(i => VfxSections.NameOf(file.Sections[i]) == n);
        int keyed = Find(f, "Keyed"), morph = Find(f, "Morph");
        f = VfxEdit.SetFrameCount(f, keyed, 16);
        f = VfxEdit.ToKeyframes(f, keyed, reduce: false);
        f = VfxEdit.SetPivot(f, keyed, new VfxTransform(new Vector3(0.2f, 0, -0.1f), Quaternion.CreateFromYawPitchRoll(0.3f, 0, 0), new Vector3(1.5f)));
        f = VfxTimelineEdits.ApplyGizmo(f, keyed, 5, new Vector3(1, 2, 0.5f), Quaternion.CreateFromYawPitchRoll(0.4f, 0.7f, 0), autoKey: true, allFrames: false);
        f = VfxEdit.ToMorph(f, morph);
        f = VfxEdit.AddMorphFrame(f, morph, 1);
        f = VfxEdit.AddMorphFrame(f, morph, 2);
        return (f, Find(f, "Static"), keyed, morph);
    }

    private static bool Same(VfxFile a, VfxFile b) => VfxWriter.Write(a).AsSpan().SequenceEqual(VfxWriter.Write(b));
    private static bool Near(Vector3 a, Vector3 b, float tol = 3e-3f) => (a - b).Length() <= tol;

    [SelfTest("VFX vertex mode: pick (front-most within radius) and marquee on synthetic screen points")]
    public static void Picking(SelfTestContext ctx)
    {
        List<(double X, double Y, double Depth, bool Visible)> pts = [(10, 10, 5, true), (12, 11, 2, true), (100, 100, 1, true), (50, 50, 0.5, false)];
        ctx.Check(VfxVertexEdits.Pick(pts, 11, 10) == 1, "click between two close vertices picks the front-most");
        ctx.Check(VfxVertexEdits.Pick(pts, 70, 70) == -1, "click far from every vertex picks nothing");
        ctx.Check(VfxVertexEdits.Pick(pts, 50, 50) == -1, "vertices behind the camera are not picked");
        ctx.Check(VfxVertexEdits.InRect(pts, 0, 0, 60, 60).SequenceEqual([0, 1]), "marquee selects the visible vertices inside the rectangle");
    }

    [SelfTest("VFX vertex mode: moves land where the gizmo was dragged (static, keyed + pivot, morph this/all frames)")]
    public static void Moves(SelfTestContext ctx)
    {
        var (f, stat, keyed, morph) = Synthetic();
        var d = new Vector3(0.3f, -0.2f, 0.5f);
        foreach (var (sec, name, fr) in new[] { (stat, "static", 0f), (keyed, "keyframed+pivot", 7f), (morph, "morph", 0f) })
        {
            var s = new VfxSampler(f);
            float frame = fr;
            if (sec == morph) frame =VfxVertexEdits.EffectFrameOf(s, VfxVertexEdits.Ordinal(f, sec), 1);
            var p = VfxVertexEdits.EffectPositions(s, sec, frame);
            var g = VfxVertexEdits.Move(s, sec, [0, 3], d, frame, allFrames: false);
            var q = VfxVertexEdits.EffectPositions(new VfxSampler(g), sec, frame);
            ctx.Check(p.Length > 0 && Near(q[0], p[0] + d) && Near(q[3], p[3] + d) && Near(q[1], p[1]), $"{name}: vertex ends at old + drag delta in effect space ({p.ElementAtOrDefault(0)} -> {q.ElementAtOrDefault(0)})");
            if (sec == morph)
            {
                var m0 = (VfxMesh)g.Sections[sec];
                ctx.Check(Near(m0.DecodePositions(0)![0], ((VfxMesh)f.Sections[sec]).DecodePositions(0)![0]), "morph this-frame: other stored frames unchanged");
                var all = VfxVertexEdits.Move(s, sec, [0], d, frame, allFrames: true);
                var ma = (VfxMesh)all.Sections[sec]; var mo = (VfxMesh)f.Sections[sec];
                ctx.Check(Enumerable.Range(0, ma.Frames.Length).All(i => Near(ma.DecodePositions(i)![0], mo.DecodePositions(i)![0] + d)), "morph all-frames: same delta on every stored frame");
            }
        }
        var rs = new VfxSampler(f);
        var c0 = VfxVertexEdits.Centroid(VfxVertexEdits.EffectPositions(rs, keyed, 7), [0, 1, 2]);
        var rot = VfxVertexEdits.Transform(rs, keyed, [0, 1, 2], Matrix4x4.CreateRotationZ(0.5f), 7, allFrames: true);
        var c1 = VfxVertexEdits.Centroid(VfxVertexEdits.EffectPositions(new VfxSampler(rot), keyed, 7), [0, 1, 2]);
        ctx.Check(Near(c0, c1), $"rotate about the centroid keeps the effect-space centroid ({c0} / {c1})");
    }

    [SelfTest("VFX vertex mode: edits are one undo step; delete/merge/morph/UV scroll keep the file valid")]
    public static void Operations(SelfTestContext ctx)
    {
        var (f, stat, _, morph) = Synthetic();
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "vertex_test.vfx", null);
        var start = doc.Current;
        doc.Selection.Select(stat);
        var vm = VfxVertexMode.Of(doc);
        vm.Enabled = true;
        ctx.Check(vm.IsActive, $"vertex mode active on a static box ({vm.Unavailable})");
        vm.Select([0, 1]);
        void Step(string label, Func<bool> run, Func<VfxFile, bool> check)
        {
            bool ok = run();
            ctx.Check(ok && check(doc.Current) && doc.UndoLabel == label, $"{label}: applied as one undo step ({doc.UndoLabel})");
            var bytes = VfxWriter.Write(doc.Current);
            var back = VfxReader.Read(bytes, "vertex_test.vfx");
            var errors = VfxLinter.Lint(back).Where(x => x.Severity.ToString() == "Error").Select(x => x.Code).ToList();
            ctx.Check(errors.Count == 0, $"{label}: writes, re-reads and lints without errors ({string.Join(",", errors)})");
            if (back.Sections[doc.Selection.Primary] is VfxMesh m)
                ctx.Check(m.Faces.All(fc => m.FaceVertices[fc.FaceVertex0].VertexIndex == fc.V0 && m.FaceVertices[fc.FaceVertex1].VertexIndex == fc.V1 && m.FaceVertices[fc.FaceVertex2].VertexIndex == fc.V2),
                    $"{label}: face-vertex records consistent");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{label}: undo restores byte-identical output");
            vm.Select([0, 1]);
        }
        int nv = ((VfxMesh)f.Sections[stat]).NumVertices, nf = ((VfxMesh)f.Sections[stat]).Faces.Length;
        Step("Move vertices", () => vm.MoveBy(new Vector3(0, 0.25f, 0)), x => !Same(x, start));
        Step("Delete vertices", vm.Delete, x => ((VfxMesh)x.Sections[stat]).NumVertices <= nv - 2 && ((VfxMesh)x.Sections[stat]).Faces.Length < nf);
        Step("Merge vertices", vm.Merge, x => ((VfxMesh)x.Sections[stat]).NumVertices == nv - 1);
        Step("Make morph mesh", vm.MakeMorph, x => ((VfxMesh)x.Sections[stat]).IsMorph);
        doc.Selection.Select(morph);
        vm.Select([0]);
        Step("Generate scrolling UVs", () => vm.ScrollUvs(new Vector2(0.5f, 0)), x =>
        {
            var s = new VfxSampler(x); int o = VfxVertexEdits.Ordinal(x, morph);
            var a = new VfxMeshSample(); var b = new VfxMeshSample();
            return s.SampleMesh(o, 0, a) && s.SampleMesh(o, 2, b) && a.Uvs.Length > 0 && !a.Uvs.SequenceEqual(b.Uvs);
        });
        int frames = ((VfxMesh)f.Sections[morph]).Frames.Length;
        Step("Add morph frame", vm.AddMorphFrame, x => ((VfxMesh)x.Sections[morph]).Frames.Length == frames + 1);
        Step("Remove morph frame", vm.RemoveMorphFrame, x => ((VfxMesh)x.Sections[morph]).Frames.Length == frames - 1);
        vm.Enabled = false;
    }

    [SelfTest("VFX vertex mode: face/UV ops on selected faces, centroid fields, scale gizmo, Tab via the viewport")]
    public static void FaceOps(SelfTestContext ctx)
    {
        var (f, stat, _, _) = Synthetic();
        var m = (VfxMesh)f.Sections[stat];
        var flipped = VfxVertexEdits.FlipFaces(f, stat, [0]);
        var fm = (VfxMesh)flipped.Sections[stat];
        ctx.Check(fm.Faces[0].V1 == m.Faces[0].V2 && fm.Faces[1] == m.Faces[1], "flip winding: only the selected face changes");
        ctx.Check(Same(VfxVertexEdits.FlipFaces(flipped, stat, [0]), f), "flip winding twice: byte-identical");
        if (m.Frames[0].Uvs is { } uv0 && uv0.Length == m.Faces.Length * 3)
        {
            var moved = ((VfxMesh)VfxVertexEdits.TransformFaceUvs(f, stat, [1], Matrix3x2.CreateTranslation(0.5f, 0), 0, true).Sections[stat]).Frames[0].Uvs!.Value;
            ctx.Check(moved[0] == uv0[0] && moved[3] == uv0[3] + new Vector2(0.5f, 0), "UV offset: only the selected face's corners move");
        }
        else ctx.Check(true, "UV offset: synthetic box has no per-corner UVs (skipped)");

        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "vertex_face.vfx", null);
        var start = doc.Current;
        doc.Selection.Select(stat);
        var vm = VfxVertexMode.Of(doc);
        vm.Enabled = true;
        var face0 = new[] { m.Faces[0].V0, m.Faces[0].V1, m.Faces[0].V2 };
        void Step(string label, Func<bool> run)
        {
            vm.Select(face0);
            bool ok = run();
            var back = VfxReader.Read(VfxWriter.Write(doc.Current), "vertex_face.vfx");
            var errors = VfxLinter.Lint(back).Where(x => x.Severity.ToString() == "Error").Select(x => x.Code).ToList();
            ctx.Check(ok && doc.UndoLabel == label && errors.Count == 0, $"{label}: one undo step, lint clean ({doc.UndoLabel}; {string.Join(",", errors)})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{label}: undo byte-identical");
        }
        Step("Flip winding", vm.FlipFaces);
        if (m.Frames[0].Uvs is not null)
        {
            Step("Offset UVs", () => vm.OffsetUvs(new Vector2(0.25f, 0)));
            Step("Scale UVs", () => vm.ScaleUvs(new Vector2(2, 2)));
            Step("Rotate UVs", () => vm.RotateUvs(90));
        }
        Step("Edit effect x", () => Commands.VfxEditing.Apply(doc, "Edit effect x", x => vm.MoveCentroidTo(x, 0, 1.5, false)));
        vm.Select(face0);
        Commands.VfxEditing.Apply(doc, "Edit mesh y", x => vm.MoveCentroidTo(x, 1, -0.75, true));
        ctx.Check(MathF.Abs(vm.Centroid(true).Y + 0.75f) < 1e-3f, $"mesh-space centroid field moves the selection ({vm.Centroid(true).Y})");
        doc.Undo();
        var c0 = vm.Centroid(false);
        var g = vm.Gizmo; g.Tool = Cairn.Viewport.PoseTool.Scale;
        ctx.Check(g.Gizmo == Cairn.Viewport.PoseGizmo.Scale && g.BeginDrag(Cairn.Viewport.PoseDragKind.Scale), "scale gizmo available in vertex mode (R)");
        g.UpdateScale(Cairn.Viewport.GizmoHandle.Screen, 2); g.CommitDrag();
        ctx.Check(doc.UndoLabel == "Scale vertices" && Vector3.Distance(c0, vm.Centroid(false)) < 1e-3f, "scale about the centroid: one step, centroid kept");
        doc.Undo();
        vm.Enabled = false;

        // Real input path: Tab raised as a routed key event on the viewport of the open effect toggles the mode.
        if (ctx.Shell.ActiveDocument is VfxDocument live && live.View is VfxDocumentView view)
        {
            var lm = VfxVertexMode.Of(live);
            bool was = lm.Enabled;
            var src = System.Windows.PresentationSource.FromVisual(view.Viewport);
            if (src is null) { ctx.Check(true, "viewport not presented (no window): Tab routing skipped"); return; }
            view.Viewport.Focus();
            var e = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, src, 0, System.Windows.Input.Key.Tab) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            view.Viewport.RaiseEvent(e);
            if (!e.Handled) { e.RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent; view.Viewport.RaiseEvent(e); }
            ctx.Check(lm.Enabled != was, $"Tab on the viewport toggles vertex mode (handled={e.Handled}, enabled {was} -> {lm.Enabled})");
            if (lm.Enabled != was) VfxModule.ToggleVertex(live);
        }
    }
}
