using System.Numerics;
using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Timeline;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Keys inspector tab, gizmo toggles and facing-quad scale self-tests.</summary>
public static class VfxKeysSelfTests
{
    private const int T = VfxTimelineEdits.TicksPerFrame;

    /// <summary>A keyframed box whose translation and rotation channels have exactly two keys, at frame 0 and frame 4.</summary>
    private static (VfxDocument Doc, int Keyed) TwoKeys(SelfTestContext ctx)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("keys_test.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Keyed"), null);
        int i = Enumerable.Range(0, f.Sections.Length).First(n => VfxSections.NameOf(f.Sections[n]) == "Keyed");
        f = VfxEdit.SetFrameCount(f, i, 5);
        f = VfxEdit.ToKeyframes(f, i, reduce: false);
        foreach (var ch in new[] { VfxKeyChannel.Translation, VfxKeyChannel.Rotation })
            f = VfxTimelineEdits.DeleteKeys(f, VfxTimelineEdits.KeyTimes((VfxMesh)f.Sections[i], ch).Where(t => t != 0 && t != 4 * T).Select(t => (i, ch, t)).ToList());
        var doc = new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "keys_test.vfx", null);
        return (doc, i);
    }

    private static void Set(VfxKeysTab tab, string label, double v) => ((Cairn.Ui.Controls.NumericBox)tab.EditorFor(label)!).Value = v;

    [SelfTest("VFX keys tab: values, flat/auto tangents, ease, multi-key edits, time, navigation (one undo step each)")]
    public static void KeysTab(SelfTestContext ctx)
    {
        var (doc, i) = TwoKeys(ctx);
        var tab = new VfxKeysTab(doc);
        var k0 = new VfxKeyRef(i, VfxKeyChannel.Translation, 0); var k1 = k0 with { Time = 4 * T };
        Vector3 Pos(float frame) { VfxTimelineEdits.TryGetPlacement(doc.Current, i, frame, out var p, out _); return p; }
        Quaternion Rot(float frame) { VfxTimelineEdits.TryGetPlacement(doc.Current, i, frame, out _, out var q); return q; }

        ctx.Check(VfxTimelineEdits.KeyTimes((VfxMesh)doc.Current.Sections[i], VfxKeyChannel.Translation).Count() == 2, "setup: two translation keys");
        doc.Selection.SelectKeys([k0]);
        Set(tab, "Value X", 0); Set(tab, "Value Y", 0); Set(tab, "Value Z", 0);
        doc.Selection.SelectKeys([k1]);
        var before = doc.Current;
        Set(tab, "Value X", 10);
        ctx.Check(doc.UndoLabel == "Edit value x" && VfxKeysTab.Vec(doc.Current, k1)!.Value.X == 10, $"Value X typed = one step ({doc.UndoLabel})");
        doc.Selection.SelectKeys([k0, k1]);
        Set(tab, "Value Y", 0); Set(tab, "Value Z", 0);
        ctx.Check(((System.Windows.Controls.Control)tab.EditorFor("Value X")!) is Cairn.Ui.Controls.NumericBox { IsIndeterminate: true }, "two keys with different X: indeterminate");
        var pre = doc.Current;
        Set(tab, "Value Y", 2);
        ctx.Check(VfxKeysTab.Vec(doc.Current, k0)!.Value.Y == 2 && VfxKeysTab.Vec(doc.Current, k1)!.Value.Y == 2, "multi-key Y edit sets both keys");
        doc.Undo();
        ctx.Check(VfxWriter.Write(doc.Current).AsSpan().SequenceEqual(VfxWriter.Write(pre)), "multi-key edit undoes in one step");

        // Flat: control points = value. Cubic Bezier v0 + (v1-v0)(3u^2-2u^3): straight path, smoothstep timing.
        doc.Current.GetType(); tab.Refresh();
        ctx.Check(VfxEditing_Apply(doc, "Flat tangents", tab.Flat), "Flat applied");
        var mid = Pos(2); var quarter = Pos(1);
        ctx.Check(MathF.Abs(mid.X - 5) < 1e-3f && MathF.Abs(quarter.X - 10 * (3 * 0.0625f - 2 * 0.015625f)) < 1e-3f && quarter.Y == 0 && quarter.Z == 0,
            $"flat tangents: on the straight line, x(u=.5)=5 ({mid.X}), x(u=.25)=1.5625 ({quarter.X})");
        ctx.Check(VfxKeysTab.Vec(doc.Current, k0)!.OutTangent == VfxKeysTab.Vec(doc.Current, k0)!.Value, "flat: out tangent = value");
        // Absolute out tangent of key 0 at 1/3 and in tangent of key 1 at 2/3 -> linear timing.
        doc.Selection.SelectKeys([k0]); Set(tab, "Out X", 10 / 3.0);
        doc.Selection.SelectKeys([k1]); Set(tab, "In X", 20 / 3.0);
        ctx.Check(MathF.Abs(Pos(1).X - 2.5f) < 1e-3f, $"absolute tangents at thirds give linear timing ({Pos(1).X})");
        tab.Relative = true; tab.Refresh();
        ctx.Check(Math.Abs(((Cairn.Ui.Controls.NumericBox)tab.EditorFor("In X")!).Value - (20 / 3.0 - 10)) < 1e-3,"relative display = control point - value");
        Set(tab, "In X", 0);
        ctx.Check(VfxKeysTab.Vec(doc.Current, k1)!.InTangent.X == 10, "relative edit stores value + offset");
        tab.Relative = false; tab.Refresh();
        Set(tab, "In X", 7);
        ctx.Check(VfxEditing_Apply(doc, "Auto tangents", tab.Auto) && VfxKeysTab.Vec(doc.Current, k1)!.InTangent == VfxKeysTab.Vec(doc.Current, k1)!.Value, "Auto on an end key = flat (Core helper)");

        // Rotation: key 1 yaw 90, ease out of key 0 = 0.5 -> fraction at u=.5 is 1/3 (k=1/1.5, k(2u-a)).
        var r0 = new VfxKeyRef(i, VfxKeyChannel.Rotation, 0); var r1 = r0 with { Time = 4 * T };
        doc.Selection.SelectKeys([r0]); Set(tab, "Yaw (Y)", 0); Set(tab, "Pitch (X)", 0); Set(tab, "Roll (Z)", 0);
        doc.Selection.SelectKeys([r1]); Set(tab, "Yaw (Y)", 90); Set(tab, "Pitch (X)", 0); Set(tab, "Roll (Z)", 0);
        float Angle(float frame) => 2 * MathF.Acos(MathF.Min(1, MathF.Abs(Rot(frame).W))) * 180 / MathF.PI;
        ctx.Check(MathF.Abs(Angle(2) - 45) < 0.05f, $"no ease: 45 deg at u=.5 ({Angle(2):0.00})");
        doc.Selection.SelectKeys([r0]); Set(tab, "Ease out", 0.5);
        float expect = 90 * VfxKeyframeMath.Ease(0.5f, 0.5f, 0);
        ctx.Check(MathF.Abs(Angle(2) - 30) < 0.05f && MathF.Abs(expect - 30) < 1e-3f, $"ease out 0.5: 30 deg at u=.5 ({Angle(2):0.00})");
        Set(tab, "Tension", 0.7);
        ctx.Check(MathF.Abs(Angle(2) - 30) < 0.05f && VfxKeysTab.Rot(doc.Current, r0)!.Tension == 0.7f, "tension is stored but does not change the motion");

        // Time, navigation, delete.
        doc.Selection.SelectKeys([k1]); Set(tab, "Time (frames)", 3);
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        ctx.Check(VfxTimelineEdits.KeyTimes((VfxMesh)doc.Current.Sections[i], VfxKeyChannel.Translation).Contains(3 * T) && doc.Selection.Keys.Single().Time == 3 * T,
            "time edit moves the key and the selection follows");
        tab.Step(-1);
        ctx.Check(doc.Selection.Keys.Single().Time == 0 && MathF.Abs(doc.TimelineFrame) < 1e-4f, "Previous key selects key 0 and seeks the playhead");
        tab.Step(+1);
        ctx.Check(doc.Selection.Keys.Single().Time == 3 * T && MathF.Abs(doc.TimelineFrame - 3) < 1e-4f, "Next key selects key 1 and seeks to frame 3");
        ctx.Check(!VfxWriter.Write(before).AsSpan().SequenceEqual(VfxWriter.Write(doc.Current)), "edits changed the file");
    }

    [SelfTest("VFX pivot mode: pivot move/turn keep sampled vertex positions at 10 frames")]
    public static void PivotMode(SelfTestContext ctx)
    {
        var (doc, i) = TwoKeys(ctx);
        var f = doc.Current;
        f = VfxEdit.SetKey(f, i, VfxKeyChannel.Translation, new VfxVectorKey(4 * T, new(3, 1, -2), new(2, 0, -1), new(3, 1, -2)));
        var rk = ((VfxMesh)f.Sections[i]).Keys!.Rotation;
        foreach (var r in rk) f = VfxEdit.SetRotationKey(f, i, r with { Value = Quaternion.CreateFromYawPitchRoll(0.4f, 0.2f, 0) });
        foreach (var s in ((VfxMesh)f.Sections[i]).Keys!.Scale) f = VfxEdit.SetKey(f, i, VfxKeyChannel.Scale, s with { Value = new(1.5f, 1.5f, 1.5f), InTangent = new(1.5f, 1.5f, 1.5f), OutTangent = new(1.5f, 1.5f, 1.5f) });
        float[] frames = Enumerable.Range(0, 10).Select(n => n * 0.45f).ToArray();
        Vector3[][] Sample(VfxFile file) { var smp = new VfxSampler(file); return frames.Select(fr => VertexEditing.VfxVertexEdits.EffectPositions(smp, i, fr)).ToArray(); }
        float Err(Vector3[][] a, Vector3[][] b) => a.Zip(b).SelectMany(p => p.First.Zip(p.Second, (x, y) => Vector3.Distance(x, y))).DefaultIfEmpty(0).Max();
        var before = Sample(f);
        ctx.Check(VfxPivotEdits.MoveBlocked(f, i) is null, "equal rotation/scale keys: pivot move allowed");
        var moved = VfxPivotEdits.ApplyGizmo(f, i, 1, new Vector3(0.7f, -0.3f, 1.1f), Quaternion.Identity);
        ctx.Check(Err(before, Sample(moved)) < 1e-4f && ((VfxMesh)moved.Sections[i]).Pivot != ((VfxMesh)f.Sections[i]).Pivot, $"pivot move keeps positions (max {Err(before, Sample(moved)):g3})");
        // Now rotation keys that differ: move blocked, turn still exact.
        var g = VfxEdit.SetRotationKey(f, i, ((VfxMesh)f.Sections[i]).Keys!.Rotation[^1] with { Value = Quaternion.CreateFromYawPitchRoll(1.6f, -0.3f, 0.5f) });
        var b2 = Sample(g);
        ctx.Check(VfxPivotEdits.MoveBlocked(g, i) is not null, $"differing rotation keys block pivot move ({VfxPivotEdits.MoveBlocked(g, i)})");
        var turned = VfxPivotEdits.ApplyGizmo(g, i, 2, Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new(1, 2, 0.5f)), 0.8f));
        ctx.Check(Err(b2, Sample(turned)) < 1e-4f, $"pivot turn keeps positions with differing rotation keys (max {Err(b2, Sample(turned)):g3})");
        var ns = VfxEdit.SetKey(g, i, VfxKeyChannel.Scale, ((VfxMesh)g.Sections[i]).Keys!.Scale[0] with { Value = new(1, 2, 1) });
        ctx.Check(VfxPivotEdits.TurnBlocked(ns, i) is not null, "non-uniform scale blocks pivot turn");
        ctx.Check(VfxPivotEdits.MoveBlocked(VfxBuilder.NewFile(), 0) is not null, "non-keyframed: blocked with a reason");
    }

    private static bool VfxEditing_Apply(VfxDocument doc, string label, Func<VfxFile, VfxFile> edit)
        => Commands.VfxEditing.Apply(doc, label, edit) && doc.UndoLabel == label;

    [SelfTest("VFX gizmo toggles persist; insert/delete keys at the playhead; facing-quad scale")]
    public static void TogglesAndFacingScale(SelfTestContext ctx)
    {
        bool auto0 = VfxGizmoPrefs.AutoKey, local0 = VfxGizmoPrefs.LocalSpace;
        VfxGizmoPrefs.AutoKey = !auto0; VfxGizmoPrefs.LocalSpace = !local0;
        ctx.Check(ctx.Shell.Settings.Get("vfx.autoKey", auto0) == !auto0 && ctx.Shell.Settings.Get("vfx.localAxes", local0) == !local0, "toggles written to settings");
        var (doc, i) = TwoKeys(ctx);
        var g = VfxModule.TimelineOf(doc).Gizmo;
        ctx.Check(g.AutoKey == !auto0 && g.LocalSpace == !local0, "gizmo reads the shared toggles");
        ctx.Check(VfxGizmoPrefs.StatusText.StartsWith("Auto-key " + (!auto0 ? "on" : "off")), $"status text ({VfxGizmoPrefs.StatusText})");
        VfxGizmoPrefs.AutoKey = auto0; VfxGizmoPrefs.LocalSpace = local0;

        doc.Selection.Select(i);
        doc.SeekFrame(2);
        VfxModule.TimelineOf(doc).InsertKeys(VfxKeyChannel.Scale);
        var m = (VfxMesh)doc.Current.Sections[i];
        ctx.Check(VfxTimelineEdits.KeyTimes(m, VfxKeyChannel.Scale).Contains(2 * T) && !VfxTimelineEdits.KeyTimes(m, VfxKeyChannel.Translation).Contains(2 * T), "per-track insert keys scale only");
        VfxModule.TimelineOf(doc).InsertKeys(null);
        VfxModule.TimelineOf(doc).DeleteKeysAtPlayhead();
        m = (VfxMesh)doc.Current.Sections[i];
        ctx.Check(doc.UndoLabel == "Delete keys at playhead" && !VfxTimelineEdits.KeyTimes(m, VfxKeyChannel.Translation).Contains(2 * T) && !VfxTimelineEdits.KeyTimes(m, VfxKeyChannel.Scale).Contains(2 * T), "delete keys at playhead, one step");

        // Facing quad: gizmo X -> width, Y -> height.
        var f = VfxEdit.AddSection(VfxEdit.AddSection(VfxBuilder.NewFile(), VfxBuilder.ImageMaterial("q.tga"), null), VfxPrimitives.FacingQuad("Quad", 2, 0), null);
        int q = Enumerable.Range(0, f.Sections.Length).First(n => VfxSections.NameOf(f.Sections[n]) == "Quad");
        Vector2 Size(VfxFile file) { var mm = (VfxMesh)file.Sections[q]; return mm.Frames.FirstOrDefault()?.FacingSize ?? mm.LegacyFacingSize ?? default; }
        var s0 = Size(f);
        var f2 = VfxTimelineEdits.ApplyScale(f, q, 0, new Vector3(2, 1, 1), Vector3.Zero, true, false);
        var f3 = VfxTimelineEdits.ApplyScale(f, q, 0, new Vector3(1, 0.5f, 1), Vector3.Zero, true, false);
        ctx.Check(s0.X > 0 && MathF.Abs(Size(f2).X - 2 * s0.X) < 1e-4f && Size(f2).Y == s0.Y, $"facing quad X x2 doubles width ({s0} -> {Size(f2)})");
        ctx.Check(MathF.Abs(Size(f3).Y - 0.5f * s0.Y) < 1e-4f && Size(f3).X == s0.X, $"facing quad Y x0.5 halves height ({Size(f3)})");
    }
}
