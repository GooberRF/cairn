using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Cairn.Ui.Controls;
using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Inspectors;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Inspector, outliner-command and material self-tests: every edit through the real editors.</summary>
public static class VfxEditingSelfTests
{
    private sealed class Rig(SelfTestContext ctx, VfxDocument doc) : IDisposable
    {
        public readonly VfxDocument Doc = doc;
        public readonly VfxInspectors Ins = new(doc);
        public int Find(string name) => Enumerable.Range(0, Doc.Current.Sections.Length).First(i => VfxSections.NameOf(Doc.Current.Sections[i]) == name);
        public T Ed<T>(VfxInspectorPage p, string label) where T : FrameworkElement
        {
            var e = p.EditorFor(label) as T;
            ctx.Check(e is not null, $"editor '{label}' exists");
            return e!;
        }
        public void Num(VfxInspectorPage p, string label, double v) => Ed<NumericBox>(p, label).Value = v;
        public void Toggle(VfxInspectorPage p, string label)
        {
            var c = Ed<CheckBox>(p, label);
            c.IsChecked = c.IsChecked != true;
            c.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }
        public void Pick(VfxInspectorPage p, string label, string item) => Ed<ComboBox>(p, label).SelectedItem = item;
        public void Type(VfxInspectorPage p, string label, string text)
        {
            var t = Ed<TextBox>(p, label);
            t.Text = text;
            t.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, t, null) { RoutedEvent = UIElement.LostKeyboardFocusEvent });
        }
        public void Press(VfxInspectorPage p, string label) => Ed<Button>(p, label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

        /// <summary>One edit: changes the snapshot as expected, is ONE undo step with the label, undo is byte-identical, redo re-applies.</summary>
        public void Step(string what, string? label, Action act, Func<VfxFile, bool> check)
        {
            var before = VfxWriter.Write(Doc.Current);
            var sel = Doc.Selection.Sections.ToList();
            act();
            var after = VfxWriter.Write(Doc.Current);
            ctx.Check(!after.AsSpan().SequenceEqual(before) && check(Doc.Current), $"{what}: edits the snapshot");
            if (label is not null) ctx.Check(Doc.UndoLabel == label, $"{what}: undo label '{label}' ({Doc.UndoLabel})");
            Doc.Undo();
            ctx.Check(VfxWriter.Write(Doc.Current).AsSpan().SequenceEqual(before), $"{what}: one undo restores byte-identical output");
            Doc.Redo();
            ctx.Check(VfxWriter.Write(Doc.Current).AsSpan().SequenceEqual(after), $"{what}: redo re-applies");
            Doc.Undo();
            bool first = true;
            foreach (var i in sel.Where(i => i < Doc.Current.Sections.Length)) { Doc.Selection.Select(i, add: !first); first = false; }
        }
        public void Dispose() => Doc.Dispose();
    }

    private static Rig Make(SelfTestContext ctx, int version = VfxVersion.Current)
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("ed_a.tga"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("ed_b.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Box"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Host", 4), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Spacewarp("Warp", 0, 4), null);
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("PartA", 0, 4, "Host") with { Warps = ["Warp"] }, null);
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("PartB", 0, 4) with { ParticleCount = 7 }, null);
        f = VfxEdit.AddSection(f, VfxBuilder.Light("Lamp", 4), null);
        return new Rig(ctx, new VfxDocument(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f with { Version = version }, "editing_test.vfx", null));
    }

    [SelfTest("VFX inspectors: every field family is one undo step, byte-identical undo, redo")]
    public static void FieldFamilies(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        var d = r.Doc; var o = r.Ins.Object;
        d.SeekFrame(0);
        int Fi(int n) => VfxEditing.FrameIndex(d, n);
        bool L(string s) { ctx.Log(s); return true; }
        d.Selection.Select(r.Find("Box"));
        r.Step("mesh frame rate", "Edit frame rate", () => r.Num(o, "Frame rate", 30), f => ((VfxMesh)f.Sections[r.Find("Box")]).Fps == 30);
        r.Step("mesh flag", "Set fullbright", () => r.Toggle(o, "Fullbright"), f => (((VfxMesh)f.Sections[r.Find("Box")]).Flags & VfxMeshFlags.Fullbright) != 0);
        r.Step("mesh start time", "Edit start time", () => r.Num(o, "Start time", 0.4), f => ((VfxMesh)f.Sections[r.Find("Box")]).StartTime is > 0.39f and < 0.41f);
        r.Step("mesh parent", "Edit parent", () => { var c = r.Ed<ComboBox>(o, "Parent"); c.Text = "Host"; c.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, c, null) { RoutedEvent = UIElement.LostKeyboardFocusEvent }); },
            f => VfxSections.ParentOf(f.Sections[r.Find("Box")]) == "Host");

        d.Selection.Select(r.Find("PartA"));
        r.Step("particle count", "Edit particle count", () => r.Num(o, "Particle count", 33), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]).ParticleCount == 33);
        r.Step("particle lifetime", "Edit lifetime", () => r.Num(o, "Lifetime", 6400), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]).Lifetime == 6400);
        r.Step("emitter type", "Edit emitter", () => r.Pick(o, "Emitter", "1: Sphere shell (radial)"), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]).EmitterType == 1);
        foreach (var (bit, name, _) in VfxObjectTab.ParticleFlagRows)
        {
            // The default system already has some flags set (roll, shrink, fade): toggling flips only this bit.
            uint had = ((VfxParticleSystem)d.Current.Sections[r.Find("PartA")]).Flags ?? 0;
            r.Step($"particle flag 0x{bit:X}", ((had & bit) != 0 ? "Clear " : "Set ") + name.ToLowerInvariant(), () => r.Toggle(o, name), f => (((VfxParticleSystem)f.Sections[r.Find("PartA")]).Flags ?? 0) == (had ^ bit));
        }
        r.Step("particle width at playhead", "Edit width", () => r.Num(o, "Width", 2.5), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]) is var ps && L($"widths {string.Join(",", ps.Frames.Select(x => x.Width))} at {Fi(ps.Frames.Length)}") && ps.Frames[Fi(ps.Frames.Length)].Width is 2.5f);
        r.Step("particle frame count", "Edit frame count", () => r.Num(o, "Frame count", 9), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]).Frames.Length == 9);
        r.Step("spacewarp check", "Clear warp", () => r.Toggle(o, "Warp"), f => ((VfxParticleSystem)f.Sections[r.Find("PartA")]).Warps.IsEmpty);

        d.Selection.Select(r.Find("Host"));
        r.Step("dummy position", "Edit position x", () => r.Num(o, "Position X", 3), f => ((VfxDummy)f.Sections[r.Find("Host")]) is var dm && (dm.Frames.Length > 0 ? dm.Frames[Fi(dm.Frames.Length)].Position.X : dm.Position.X) is 3f);
        d.Selection.Select(r.Find("Lamp"));
        r.Step("light colour", "Edit red", () => r.Num(o, "Red", 0.25), f => ((VfxLight)f.Sections[r.Find("Lamp")]) is var l && (l.Frames.Length > 0 ? l.Frames[Fi(l.Frames.Length)].Color.X : l.Initial.Color.X) is 0.25f);
        r.Step("light on", "Clear on", () => r.Toggle(o, "On"), f => ((VfxLight)f.Sections[r.Find("Lamp")]) is var l && (l.Frames.Length > 0 ? l.Frames[Fi(l.Frames.Length)].IsOn : l.Initial.IsOn) == 0);
        d.Selection.Select(r.Find("Warp"));
        r.Step("spacewarp strength", "Edit strength", () => r.Num(o, "Strength", 4), f => ((VfxSpacewarp)f.Sections[r.Find("Warp")]) is var w && w.Frames[Fi(w.Frames.Length)].Strength is 4f);
        ctx.Log($"playhead frame {d.TimelineFrame}");

        var m = r.Ins.Material;
        d.Selection.Select(r.Find("PartA"));
        r.Step("material fps", "Edit fps", () => r.Num(m, "Fps", 30), f => f.Sections.OfType<VfxMaterial>().First().Fps == 30);
        r.Step("material additive", "Clear additive", () => r.Toggle(m, "Additive"), f => f.Sections.OfType<VfxMaterial>().First().Additive is 0);
        r.Step("texture playback", "Edit tex 1 playback", () => r.Pick(m, "Tex 1 playback", VfxMaterialTab.TexModes[1]), f => f.Sections.OfType<VfxMaterial>().First().Texture0?.AnimType == 1);
        r.Step("opacity at playhead", "Edit at playhead", () => r.Num(m, "At playhead", 0.5), f => f.Sections.OfType<VfxMaterial>().First().Opacity?[0] is 0.5f);
        r.Step("effect end frame", "Edit end frame", () => r.Num(r.Ins.EffectTab, "End frame", 77), f => f.EndFrame == 77);
    }

    [SelfTest("VFX inspectors: multi-selection shows indeterminate and edits every selected object in one step")]
    public static void MultiSelection(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        int a = r.Find("PartA"), b = r.Find("PartB");
        r.Doc.Selection.Select(a); r.Doc.Selection.Select(b, add: true);
        var box = r.Ed<NumericBox>(r.Ins.Object, "Particle count");
        ctx.Check(box.IsIndeterminate, "differing particle counts show indeterminate");
        r.Step("multi particle count", "Edit particle count", () => r.Num(r.Ins.Object, "Particle count", 12),
            f => ((VfxParticleSystem)f.Sections[a]).ParticleCount == 12 && ((VfxParticleSystem)f.Sections[b]).ParticleCount == 12);
        r.Doc.Selection.Select(a); r.Doc.Selection.Select(b, add: true);
        var flag = r.Ed<CheckBox>(r.Ins.Object, "Gravity");
        ctx.Check(flag.IsChecked == false, "equal flags show a definite state");
    }

    [SelfTest("VFX editing: rename keeps references; duplicate, delete, reparent, reorder are single steps")]
    public static void ObjectCommands(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        var d = r.Doc;
        d.Selection.Select(r.Find("Host"));
        r.Step("rename dummy", "Edit name", () => r.Type(r.Ins.Object, "Name", "Carrier"),
            f => f.Sections.Any(s => VfxSections.NameOf(s) == "Carrier") && f.Sections.OfType<VfxParticleSystem>().FirstOrDefault(p => p.Name == "PartA") is { } pa && VfxSections.ParentOf(pa) == "Carrier");
        r.Step("rename spacewarp", "Rename", () => VfxObjectCommands.Rename(d, r.Find("Warp"), "Gust"),
            f => f.Sections.OfType<VfxParticleSystem>().FirstOrDefault(p => p.Name == "PartA") is { } pa && pa.Warps.SequenceEqual(["Gust"]));
        int n = d.Current.Sections.Length;
        d.Selection.Select(r.Find("Box"));
        r.Step("duplicate", "Duplicate object", () => VfxObjectCommands.Duplicate(d), f => f.Sections.Length == n + 1);
        d.Selection.Select(r.Find("Lamp"));
        r.Step("delete", "Delete", () => VfxObjectCommands.Delete(d), f => f.Sections.Length == n - 1 && !f.Sections.OfType<VfxLight>().Any());
        d.Selection.Select(r.Find("Host"));
        r.Step("delete parent re-homes children", "Delete", () => VfxObjectCommands.Delete(d), f => VfxSections.ParentOf(f.Sections.OfType<VfxParticleSystem>().First(p => p.Name == "PartA")) == "Scene Root");
        r.Step("reparent", "Parent to Host", () => VfxObjectCommands.Reparent(d, [r.Find("PartB")], "Host"), f => VfxSections.ParentOf(f.Sections.OfType<VfxParticleSystem>().First(p => p.Name == "PartB")) == "Host");
        d.Selection.Select(r.Find("Box"));
        int box = r.Find("Box");
        r.Step("move down", null, () => VfxObjectCommands.Move(d, 1), f => VfxSections.NameOf(f.Sections[box + 1]) == "Box");
    }

    [SelfTest("VFX inspectors: pivot fields of a keyframed mesh; a committed value is one step, unchanged fields add none")]
    public static void PivotFields(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        var d = r.Doc; var o = r.Ins.Object;
        ctx.Check(VfxEditing.Apply(d, "Convert to keyframes", f => VfxEdit.ToKeyframes(f, r.Find("Box"))), "box converted to keyframes");
        d.Selection.Select(r.Find("Box"));
        VfxTransform Pv(VfxFile f) => ((VfxMesh)f.Sections[r.Find("Box")]).Pivot!;
        r.Step("pivot translation", "Edit pivot y", () => r.Num(o, "Pivot Y", 1.5), f => Pv(f).Translation.Y is 1.5f);
        r.Step("pivot rotation", "Edit pivot rotation y", () => r.Num(o, "Pivot rotation Y", 90),
            f => Math.Abs(VfxObjectTab.ToEuler(Pv(f).Rotation).Y - 90) < 0.01 && Math.Abs(Pv(f).Rotation.Length() - 1) < 1e-4);
        r.Step("pivot scale", "Edit pivot scale z", () => r.Num(o, "Pivot scale Z", 2), f => Pv(f).Scale.Z is 2f);
        var e = new System.Numerics.Vector3(20, -35, 50);
        var back = VfxObjectTab.ToEuler(VfxObjectTab.FromEuler(e));
        ctx.Check((back - e).Length() < 0.01, $"Euler round trip ({back})");
        var bytes = VfxWriter.Write(d.Current); var undo = d.UndoLabel;
        foreach (var label in new[] { "Pivot X", "Pivot Y", "Pivot Z", "Pivot rotation X", "Pivot rotation Y", "Pivot rotation Z", "Pivot scale X", "Pivot scale Y", "Pivot scale Z" })
        {
            var box = r.Ed<NumericBox>(o, label);
            box.Focus(); box.Value = box.Value;
        }
        ctx.Check(VfxWriter.Write(d.Current).AsSpan().SequenceEqual(bytes) && d.UndoLabel == undo, "tabbing through the pivot fields unchanged adds no history entry");
    }

    [SelfTest("VFX materials: curve strip click, vertical drag and range paint are one undo step each; fit to effect length")]
    public static void CurveStrip(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        var d = r.Doc; var m = r.Ins.Material;
        int mat = Enumerable.Range(0, d.Current.Sections.Length).First(i => d.Current.Sections[i] is VfxMaterial);
        ctx.Check(VfxEditing.Apply(d, "Resize", f => VfxEdit.ResizeTrack(f, mat, VfxMaterialTrack.Opacity, 10)), "opacity track resized to 10 samples");
        d.Selection.Select(mat);
        m.Refresh();
        ctx.Check(m.Strips.TryGetValue("Opacity", out var s), "opacity strip exists");
        if (s is null) return;
        float At(VfxFile f, int i) => ((VfxMaterial)f.Sections[mat]).Opacity!.Value[i];
        Point P(int i, double v) => new(s.PointOf(i, 0).X, s.PointOf(0, (float)v).Y);
        ctx.Check(s.SampleAt(P(7, 0).X) == 7 && Math.Abs(s.ValueAt(P(0, 0.3).Y) - 0.3) < 1e-6, "hit-test: x -> sample, y -> value");
        r.Step("curve click", "Edit opacity curve", () => { s.Press(P(3, 0.8)); s.Release(); }, f => Math.Abs(At(f, 3) - 0.8) < 1e-4 && At(f, 2) != At(f, 3));
        r.Step("curve vertical drag", "Edit opacity curve", () => { s.Press(P(4, 0.1)); s.Drag(P(4, 0.5)); s.Drag(P(4, 0.9)); s.Release(); }, f => Math.Abs(At(f, 4) - 0.9) < 1e-4);
        r.Step("curve range paint", "Edit opacity curve", () => { s.Press(P(2, 0.25)); s.Drag(P(4, 0.25)); s.Drag(P(6, 0.25)); s.Release(); },
            f => Enumerable.Range(2, 5).All(i => Math.Abs(At(f, i) - 0.25) < 1e-4) && Math.Abs(At(f, 7) - 0.25) > 1e-3);
        var before = VfxWriter.Write(d.Current);
        s.Press(P(5, 0.6)); s.Cancel();
        ctx.Check(VfxWriter.Write(d.Current).AsSpan().SequenceEqual(before), "cancelled gesture leaves the snapshot unchanged");
        ctx.Log($"fit: end frame {d.EndFrame}, material fps {((VfxMaterial)d.Current.Sections[mat]).Fps}, samples {((VfxMaterial)d.Current.Sections[mat]).Opacity?.Length}");
        r.Step("fit to effect length", "Resize opacity track", () => r.Press(m, "Fit to effect length"),
            f => ((VfxMaterial)f.Sections[mat]).Opacity!.Value.Length ==VfxMaterialTab.TrackIndex((VfxMaterial)f.Sections[mat], d.EndFrame, int.MaxValue) + 1);
    }

    [ScreenshotDialog("vfx-texture-picker")]
    public static Window TexturePickerShot(ScreenshotContext ctx) =>
        VfxTexturePicker.Build(ctx.Shell.MainWindow, () => ctx.Shell.Assets.Resolver, "UwaterEXP_Volume.tga", out _);

    [SelfTest("VFX materials: removing a material in use refuses, or reassigns its users")]
    public static void MaterialRemove(SelfTestContext ctx)
    {
        using var r = Make(ctx);
        var d = r.Doc; var m = r.Ins.Material;
        d.Selection.Select(Enumerable.Range(0, d.Current.Sections.Length).First(i => d.Current.Sections[i] is VfxMaterial));
        var before = VfxWriter.Write(d.Current);
        var undo = d.UndoLabel;
        r.Press(m, "Remove");
        ctx.Check(VfxWriter.Write(d.Current).AsSpan().SequenceEqual(before) && d.UndoLabel == undo, "remove of a material in use is refused without an undo step");
        r.Step("remove with reassign", "Remove material", () => { m.EditorFor("Remove"); m.ReassignBox!.SelectedItem = VfxMaterialTab.Labels(d.Current)[1]; r.Press(m, "Remove"); },
            f => f.Sections.OfType<VfxMaterial>().Count() == 1 && f.Sections.OfType<VfxParticleSystem>().All(p => p.MaterialIndex == 0));
    }

    [SelfTest("VFX editing: an older-version file refuses edits with a reason until converted")]
    public static void OlderVersion(SelfTestContext ctx)
    {
        using var r = Make(ctx, 0x40000);
        var d = r.Doc;
        d.Selection.Select(r.Find("PartA"));
        var box = r.Ed<NumericBox>(r.Ins.Object, "Particle count");
        ctx.Check(!box.IsEnabled && (box.ToolTip as string) == VfxEditing.OlderVersionReason, "editor disabled with the convert reason");
        ctx.Check(!VfxEditing.Apply(d, "Edit", f => VfxEdit.Rename(f, r.Find("PartA"), "X")), "Apply refuses on the older version");
        d.ConvertCommand.Execute(null);
        ctx.Check(!d.IsOlderVersion, "converted");
        box = r.Ed<NumericBox>(r.Ins.Object, "Particle count");
        ctx.Check(box.IsEnabled, "editor enabled after convert");
        r.Step("edit after convert", "Edit particle count", () => r.Num(r.Ins.Object, "Particle count", 21), f => f.Sections.OfType<VfxParticleSystem>().First().ParticleCount == 21);
    }
}
