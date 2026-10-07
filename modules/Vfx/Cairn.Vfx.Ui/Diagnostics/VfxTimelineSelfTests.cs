using System.Numerics;
using System.Windows;
using System.Windows.Input;
using Cairn.Ui.Diagnostics;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;
using Cairn.Vfx.Ui.Viewport;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Timeline, key editing and gizmo self-tests.</summary>
public static class VfxTimelineSelfTests
{
    private static (VfxDocument Doc, int Static, int Keyed, int Dummy) Synthetic(SelfTestContext ctx)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("tl_test.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Static"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Keyed"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Dummy", 10, "Scene Root"), null);
        int Find(VfxFile file, string n) => Enumerable.Range(0, file.Sections.Length).First(i => VfxSections.NameOf(file.Sections[i]) == n);
        int keyed = Find(f, "Keyed");
        f = VfxEdit.SetFrameCount(f, keyed, 16);
        f = VfxEdit.ToKeyframes(f, keyed, reduce: false);
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "timeline_test.vfx", null);
        return (doc, Find(f, "Static"), keyed, Find(f, "Dummy"));
    }

    private static bool Same(VfxFile a, VfxFile b) => VfxWriter.Write(a).AsSpan().SequenceEqual(VfxWriter.Write(b));

    [SelfTest("VFX timeline: key insert/move/delete/paste and range drags are single undo steps")]
    public static void KeyEdits(SelfTestContext ctx)
    {
        var (doc, stat, keyed, _) = Synthetic(ctx);
        var start = doc.Current;
        void Step(string label, Func<VfxFile, VfxFile> edit, Func<VfxFile, bool> check)
        {
            bool changed = doc.Apply(label, edit);
            ctx.Check(changed && check(doc.Current), $"{label}: changed as expected");
            ctx.Check(doc.UndoLabel == label, $"{label}: one undo step ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{label}: undo restores byte-identical output");
        }
        int n = VfxTimelineEdits.KeyTimes((VfxMesh)start.Sections[keyed], VfxKeyChannel.Translation).Length;
        ctx.Check(n == 16, $"keyframed test mesh has 16 translation keys ({n})");
        Step("Insert key", f => VfxTimelineEdits.InsertKey(VfxTimelineEdits.DeleteKeys(f, [(keyed, VfxKeyChannel.Translation, 5 * 320)]), keyed, VfxKeyChannel.Translation, 5 * 320 + 160),
            f => VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[keyed], VfxKeyChannel.Translation).Contains(5 * 320 + 160));
        Step("Move keys", f => VfxTimelineEdits.MoveKeys(f, [(keyed, VfxKeyChannel.Translation, 15 * 320)], 320),
            f => VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[keyed], VfxKeyChannel.Translation).Contains(16 * 320));
        Step("Delete keys", f => VfxTimelineEdits.DeleteKeys(f, [(keyed, VfxKeyChannel.Translation, 3 * 320), (keyed, VfxKeyChannel.Rotation, 3 * 320)]),
            f => VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[keyed], VfxKeyChannel.Translation).Length == 15);
        var clip = VfxTimelineEdits.CopyKeys(start, [(keyed, VfxKeyChannel.Translation, 0)]).ToList();
        Step("Paste keys", f => VfxTimelineEdits.PasteKeys(f, clip, 20 * 320),
            f => VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[keyed], VfxKeyChannel.Translation).Contains(20 * 320));
        Step("Move start time", f => VfxTimelineEdits.MoveRange(f, stat, 4), f => VfxTimelineEdits.Range(f.Sections[stat])!.Value.Start == 4);
        Step("Change frame count", f => VfxTimelineEdits.ResizeRange(f, keyed, 20), f => ((VfxMesh)f.Sections[keyed]).Frames.Length == 20);
        // static -> keyframed keeps the sampled positions
        var conv = VfxEdit.ToKeyframes(VfxTimelineEdits.ApplyGizmo(start, stat, 0, new Vector3(1, 2, 3), Quaternion.Identity, true, true), stat);
        ctx.Check(VfxTimelineEdits.TryGetPlacement(conv, stat, 0, out var p, out _) && Vector3.Distance(p, new Vector3(1, 2, 3)) < 1e-4f, $"static -> keyframed keeps the position ({p})");
    }

    [SelfTest("VFX timeline: gizmo drags per animation kind with auto-key on and off")]
    public static void GizmoDrags(SelfTestContext ctx)
    {
        var (doc, stat, keyed, dummy) = Synthetic(ctx);
        var gizmo = new VfxGizmoTarget(doc) { Tool = PoseTool.Move };
        var d = new Vector3(0.5f, 0, -1);
        foreach (var (section, autoKey) in new[] { (keyed, true), (keyed, false), (stat, true), (dummy, true) })
        {
            doc.SeekFrame(6);
            doc.Selection.Select(section);
            gizmo.AutoKey = autoKey;
            VfxTimelineEdits.TryGetPlacement(doc.Current, section, 6, out var before, out _);
            var otherBefore = section == keyed && VfxTimelineEdits.TryGetPlacement(doc.Current, section, 12, out var o, out _) ? o : default;
            ctx.Check(gizmo.BeginDrag(PoseDragKind.Move, GizmoHandle.Screen), "drag starts");
            gizmo.UpdateWorldTranslation(d * 0.5f);
            gizmo.UpdateWorldTranslation(d);
            gizmo.CommitDrag();
            VfxTimelineEdits.TryGetPlacement(doc.Current, section, 6, out var after, out _);
            string what = $"{VfxSections.NameOf(doc.Current.Sections[section])} auto-key {(autoKey ? "on" : "off")}";
            ctx.Check(Vector3.Distance(after, before + d) < 1e-3f, $"{what}: moved by the drag at the playhead ({before} -> {after})");
            if (section == keyed)
            {
                VfxTimelineEdits.TryGetPlacement(doc.Current, section, 12, out var otherAfter, out _);
                bool offsetAll = Vector3.Distance(otherAfter, otherBefore + d) < 1e-3f;
                ctx.Check(autoKey ? !offsetAll : offsetAll, $"{what}: frame 12 {(autoKey ? "keeps its key" : "moves too")}");
            }
            ctx.Check(doc.UndoLabel == "Move object", $"{what}: one undo step");
            doc.Undo();
        }
        // rotate + Escape-style cancel leaves the file unchanged
        var start = doc.Current; doc.Selection.Select(keyed); gizmo.Tool = PoseTool.Rotate;
        gizmo.BeginDrag(PoseDragKind.Rotate, GizmoHandle.Y); gizmo.UpdateAxisRotation(1, 0.7); gizmo.CancelDrag();
        ctx.Check(ReferenceEquals(doc.Current, start) || Same(doc.Current, start), "cancelled rotate drag leaves the effect unchanged");
    }

    [SelfTest("VFX timeline: gizmo moves per-frame, morph, particle, light and spacewarp objects at the playhead")]
    public static void GizmoKinds(SelfTestContext ctx)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("tl_test.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("PerFrame"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Morph"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("Emitter", 0, 10), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Light("Light", 10), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Spacewarp("Warp", 0, 10), null);
        int Find(string n) => Enumerable.Range(0, f.Sections.Length).First(i => VfxSections.NameOf(f.Sections[i]) == n);
        int pf = Find("PerFrame"), morph = Find("Morph");
        f = VfxEdit.ToKeyframes(VfxEdit.SetFrameCount(f, pf, 10), pf, reduce: false); f = VfxEdit.ToPerFrameTransforms(f, pf);
        f = VfxEdit.ToMorph(VfxEdit.SetFrameCount(f, morph, 10), morph);
        ctx.Check(VfxTimelineEdits.KindOf(f.Sections[pf]) == VfxAnimKind.PerFrame && VfxTimelineEdits.KindOf(f.Sections[morph]) == VfxAnimKind.Morph, "test meshes are per-frame and morph");
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "gizmo_kinds.vfx", null);
        var gizmo = new VfxGizmoTarget(doc) { Tool = PoseTool.Move };
        var d = new Vector3(0.25f, 1, 0);
        foreach (var name in new[] { "PerFrame", "Morph", "Emitter", "Light", "Warp" })
        {
            int i = Find(name); doc.SeekFrame(4); doc.Selection.Select(i);
            var start = doc.Current;
            ctx.Check(gizmo.TryGetPlacement(out var before, out _), $"{name}: gizmo has a placement");
            ctx.Check(gizmo.BeginDrag(PoseDragKind.Move, GizmoHandle.Screen), $"{name}: drag starts");
            gizmo.UpdateWorldTranslation(d); gizmo.CommitDrag();
            gizmo.TryGetPlacement(out var after, out _);
            ctx.Check(Vector3.Distance(after, before + d) < 1e-3f, $"{name}: handle follows the drag at the playhead ({before} -> {after})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{name}: undo restores byte-identical output");
        }
    }

    [SelfTest("VFX timeline: surface hit-testing (key click, marquee, snapped/free key drag, range bar drags, Escape)")]
    public static void SurfaceHitTests(SelfTestContext ctx)
    {
        var (doc, stat, keyed, _) = Synthetic(ctx);
        var tl = new VfxTimelineSurface(doc); tl.ExpandAll();
        var start = doc.Current;
        VfxKeyRef T(int frame, VfxKeyChannel ch = VfxKeyChannel.Translation) => new(keyed, ch, frame * 320);
        void Click(Point p, ModifierKeys m = ModifierKeys.None) { tl.PointerDown(p, m); tl.PointerUp(); }
        Point P(VfxKeyRef k) => tl.KeyPoint(k) is { } p ? new Point(p.X, p.Y) : default;
        double px = tl.PixelsPerFrame;
        Click(P(T(3)));
        ctx.Check(doc.Selection.Keys.Count == 1 && doc.Selection.ContainsKey(T(3)), $"click selects one key ({doc.Selection.Keys.Count})");
        // marquee from an empty spot right of the keys (frame 20, T row) to frame 12.5 on the R row: frames 13..15 on T and R
        var a = P(T(15)); var b = P(T(15, VfxKeyChannel.Rotation));
        tl.PointerDown(new Point(a.X + 5 * px, a.Y - 4), ModifierKeys.None); tl.PointerMove(new Point(a.X - 2.5 * px, b.Y + 4), ModifierKeys.None); tl.PointerUp();
        ctx.Check(doc.Selection.Keys.Count == 6, $"marquee selects frames 13-15 on two rows ({doc.Selection.Keys.Count} keys)");
        foreach (var (alt, expect) in new[] { (false, 16 * 320), (true, 15 * 320 + 448) })
        {
            doc.Selection.ClearKeys();
            var p = P(T(15)); tl.PointerDown(p, ModifierKeys.None); tl.PointerMove(new Point(p.X + 1.4 * px, p.Y), alt ? ModifierKeys.Alt : ModifierKeys.None); tl.PointerUp();
            var times = VfxTimelineEdits.KeyTimes((VfxMesh)doc.Current.Sections[keyed], VfxKeyChannel.Translation);
            ctx.Check(times.Contains(expect) && !times.Contains(15 * 320), $"key drag {(alt ? "with Alt is free" : "snaps to 320 ticks")} ({string.Join(",", times.TakeLast(2))})");
            ctx.Check(doc.UndoLabel == "Move keys", "key drag is one undo step"); doc.Undo();
            ctx.Check(Same(doc.Current, start), "undo restores the keys");
        }
        var q = P(T(10)); tl.PointerDown(q, ModifierKeys.None); tl.PointerMove(new Point(q.X + 3 * px, q.Y), ModifierKeys.None);
        ctx.Check(tl.CancelDrag() && Same(doc.Current, start), "Escape cancels a key drag");
        tl.PointerUp();
        // range bar: drag the right edge of the 16-frame keyed mesh by +4 frames, then move the static mesh start by +3
        var edge = tl.ObjectRowPoint(keyed, VfxTimelineEdits.Range(start.Sections[keyed])!.Value.Length - 0.1)!.Value;
        tl.PointerDown(edge, ModifierKeys.None); tl.PointerMove(new Point(edge.X + 4 * px, edge.Y), ModifierKeys.None); tl.PointerUp();
        ctx.Check(((VfxMesh)doc.Current.Sections[keyed]).Frames.Length == 20 && doc.UndoLabel == "Change frame count", $"range edge drag changes the frame count ({((VfxMesh)doc.Current.Sections[keyed]).Frames.Length})");
        doc.Undo();
        var len = VfxTimelineEdits.Range(start.Sections[stat])!.Value.Length;
        var mid = tl.ObjectRowPoint(stat, len * 0.25)!.Value;
        tl.PointerDown(mid, ModifierKeys.None); tl.PointerMove(new Point(mid.X + 3 * px, mid.Y), ModifierKeys.None); tl.PointerUp();
        ctx.Check(VfxTimelineEdits.Range(doc.Current.Sections[stat])!.Value.Start == 3 && doc.UndoLabel == "Move start time", $"range drag moves the start ({VfxTimelineEdits.Range(doc.Current.Sections[stat])!.Value.Start})");
        doc.Undo();
        ctx.Check(Same(doc.Current, start), "range edits undo to byte-identical output");
    }

    private static int MeshOrdinal(VfxFile f, int section) => f.Sections.Take(section).Count(s => s is VfxMesh);

    /// <summary>Sampled world-space bounding-box size of a mesh at an effect frame (zero when not visible).</summary>
    private static Vector3 SampledSize(VfxFile f, int section, float frame)
    {
        var s = new VfxMeshSample();
        if (!new VfxSampler(f).SampleMesh(MeshOrdinal(f, section), frame, s) || s.Positions.Length == 0) return Vector3.Zero;
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        foreach (var p in s.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); }
        return hi - lo;
    }

    [SelfTest("VFX timeline: scale gizmo per animation kind gives the expected sampled size, one undo step each")]
    public static void ScaleDrags(SelfTestContext ctx)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("tl_test.tga"), null);
        foreach (var n in new[] { "Static", "Keyed", "PerFrame", "Morph" }) f = VfxEdit.AddSection(f, VfxPrimitives.Box(n), null);
        int Find(string n) => Enumerable.Range(0, f.Sections.Length).First(i => VfxSections.NameOf(f.Sections[i]) == n);
        int keyed = Find("Keyed"), pf = Find("PerFrame"), morph = Find("Morph"), stat = Find("Static");
        f = VfxEdit.SetFrameCount(f, stat, 10, VfxFrameFill.Hold); // visible at frame 4
        f = VfxEdit.ToKeyframes(VfxEdit.SetFrameCount(f, keyed, 16), keyed, reduce: false);
        f = VfxEdit.ToKeyframes(VfxEdit.SetFrameCount(f, pf, 10), pf, reduce: false); f = VfxEdit.ToPerFrameTransforms(f, pf);
        f = VfxEdit.ToMorph(VfxEdit.SetFrameCount(f, morph, 10), morph);
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "scale_kinds.vfx", null);
        var gizmo = new VfxGizmoTarget(doc) { Tool = PoseTool.Scale };
        foreach (var (name, handle, factor, autoKey) in new[] { ("Static", 0, 2.0, true), ("Keyed", GizmoHandle.Screen, 1.5, true), ("Keyed", 1, 2.0, false), ("PerFrame", 2, 0.5, true), ("Morph", 0, 2.0, true),
                     ("Static", GizmoHandle.PlaneXY, 2.0, true), ("Keyed", GizmoHandle.PlaneZX, 1.5, true), ("Morph", GizmoHandle.PlaneYZ, 0.5, true) })
        {
            int i = Find(name); doc.SeekFrame(4); doc.Selection.Select(i);
            gizmo.AutoKey = autoKey;
            var start = doc.Current;
            var before = SampledSize(start, i, 4);
            ctx.Check(before.X > 0.1f, $"{name}: visible at frame 4 ({before})");
            ctx.Check(gizmo.Gizmo == PoseGizmo.Scale, $"{name}: scale gizmo shown");
            ctx.Check(gizmo.BeginDrag(PoseDragKind.Scale, handle), $"{name}: drag starts");
            gizmo.UpdateScale(handle, 1.2); gizmo.UpdateScale(handle, factor); gizmo.CommitDrag();
            var expected = before * GizmoHandle.ScaleVector(handle, (float)factor);
            var after = SampledSize(doc.Current, i, 4);
            string what = $"{name} {GizmoHandle.Name(handle)} x{factor} auto-key {(autoKey ? "on" : "off")}";
            ctx.Check(Vector3.Distance(after, expected) < 1e-3f, $"{what}: sampled size {before} -> {after} (expected {expected})");
            if (name == "Keyed" && !autoKey)
            {
                // auto-key off scales the whole track: another frame scales the same way
                var far0 = SampledSize(start, i, 14); var far1 = SampledSize(doc.Current, i, 14);
                ctx.Check(Vector3.Distance(far1, far0 * new Vector3(1, (float)factor, 1)) < 1e-3f, $"{what}: frame 14 scales too ({far0} -> {far1})");
            }
            ctx.Check(doc.UndoLabel == "Scale object", $"{what}: one undo step ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{what}: undo restores byte-identical output");
        }
    }

    [SelfTest("VFX gizmo: plane squares are hit where drawn and move/scale in exactly their two axes (mouse path)")]
    public static void PlaneHandles(SelfTestContext ctx)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("tl_test.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Static"), null);
        int stat = Enumerable.Range(0, f.Sections.Length).First(i => VfxSections.NameOf(f.Sections[i]) == "Static");
        f = VfxEdit.SetFrameCount(f, stat, 10, VfxFrameFill.Hold);
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "plane_handles.vfx", null);
        doc.SeekFrame(4);
        doc.Selection.Select(stat);
        var gizmo = new VfxGizmoTarget(doc) { Tool = PoseTool.Move };
        gizmo.TryGetPlacement(out var centre, out _);
        // An oblique view so all three planes face the camera enough to have a square.
        var camera = new OrbitCamera { Target = centre, Yaw = 35, Pitch = 30, Distance = 6 };
        var layer = new GizmoLayer { Camera = camera, Controller = gizmo };
        layer.Measure(new Size(900, 650));
        layer.Arrange(new Rect(0, 0, 900, 650));
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };

        for (int n = 0; n < 3; n++)
        {
            int handle = GizmoHandle.PlaneOf(n);
            string what = $"move {GizmoHandle.Name(handle)}";
            if (layer.PlanePoint(n) is not { } grab) { ctx.Check(false, $"{what}: square drawn from an oblique view"); continue; }
            ctx.Check(layer.HitTest(grab) == handle, $"{what}: hit where drawn ({GizmoHandle.Name(layer.HitTest(grab))})");
            var start = doc.Current;
            ctx.Check(layer.TryBeginDrag(grab), $"{what}: drag starts");
            layer.Drag(grab + new System.Windows.Vector(20, -12), ModifierKeys.None);
            layer.Drag(grab + new System.Windows.Vector(45, -30), ModifierKeys.None);
            layer.EndDrag();
            gizmo.TryGetPlacement(out var after, out _);
            var delta = after - centre;
            ctx.Check(delta.Length() > 1e-3f, $"{what}: object moved ({delta})");
            ctx.Check(MathF.Abs(Vector3.Dot(delta, axes[n])) < 1e-4f, $"{what}: no movement along {GizmoHandle.Name(n)} ({delta})");
            ctx.Check(doc.UndoLabel == "Move object", $"{what}: one undo step ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{what}: undo restores byte-identical output");
        }

        gizmo.Tool = PoseTool.Scale;
        layer.InvalidateVisual();
        for (int n = 0; n < 3; n++)
        {
            int handle = GizmoHandle.PlaneOf(n);
            string what = $"scale {GizmoHandle.Name(handle)}";
            if (layer.PlanePoint(n) is not { } grab || layer.CentreScreen is not { } c) { ctx.Check(false, $"{what}: square drawn"); continue; }
            ctx.Check(layer.HitTest(grab) == handle, $"{what}: hit where drawn ({GizmoHandle.Name(layer.HitTest(grab))})");
            var start = doc.Current;
            var before = SampledSize(start, stat, 4);
            ctx.Check(layer.TryBeginDrag(grab), $"{what}: drag starts");
            layer.Drag(c + (grab - c) * 1.5, ModifierKeys.None); // outwards: grows
            layer.EndDrag();
            var after = SampledSize(doc.Current, stat, 4);
            static float Comp(Vector3 v, int a) => a switch { 0 => v.X, 1 => v.Y, _ => v.Z };
            bool grewInPlane = Enumerable.Range(0, 3).Where(a => a != n).All(a => Comp(after, a) > Comp(before, a) * 1.05f);
            bool keptNormal = MathF.Abs(Comp(after, n) - Comp(before, n)) < 1e-3f;
            ctx.Check(grewInPlane && keptNormal, $"{what}: grows in its two axes only ({before} -> {after})");
            ctx.Check(doc.UndoLabel == "Scale object", $"{what}: one undo step ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(Same(doc.Current, start), $"{what}: undo restores byte-identical output");
        }
        layer.Controller = null;
    }

    [SelfTest("VFX timeline: each mesh's range bar equals the sampler's visibility window")]
    public static void RangeBarsMatchSampler(SelfTestContext ctx)
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) { ctx.Log("corpus not found; skipped"); return; }
        int odd = 0, files = 0;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var f = ((VfxDocument)new VfxKind { Shell = ctx.Shell }.Open(path)).Current;
            bool explosion = Path.GetFileName(path).Equals("Explosion_Sub.vfx", StringComparison.OrdinalIgnoreCase);
            var sampler = new VfxSampler(f);
            var sample = new VfxMeshSample();
            bool fileOdd = false; int bad = 0, meshes = 0;
            for (int i = 0; i < f.Sections.Length; i++)
            {
                if (f.Sections[i] is not VfxMesh m || VfxTimelineEdits.Range(m) is not { } r || m.Frames.Length == 0) continue;
                meshes++;
                fileOdd |= r.Start > 0.01 || Math.Abs(VfxTimelineEdits.Fps(m) - 15) > 0.01;
                int mi = MeshOrdinal(f, i);
                double end = r.Start + r.Length;
                bool ok = sampler.SampleMesh(mi, (float)(r.Start + 1e-3), sample) && sampler.SampleMesh(mi, (float)(end - 1e-3), sample)
                    && (r.Start < 2e-3 || !sampler.SampleMesh(mi, (float)(r.Start - 2e-3), sample)) && !sampler.SampleMesh(mi, (float)(end + 2e-3), sample);
                if (!ok) { bad++; ctx.Log($"{Path.GetFileName(path)} {VfxSections.NameOf(m)}: bar {r.Start:0.###}..{end:0.###} differs from the sampler"); }
            }
            if (!explosion && !(fileOdd && odd++ < 3)) continue;
            files++;
            ctx.Check(bad == 0, $"{Path.GetFileName(path)}: {meshes} mesh range bars equal the sampler's window ({bad} differ)");
        }
        ctx.Check(files >= 2, $"checked Explosion_Sub and effects with non-15 fps / non-zero start ({files} files)");
    }
}
