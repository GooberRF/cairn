using System.Diagnostics;
using System.Numerics;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Inspectors;
using Cairn.Vfx.Ui.VertexEditing;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Guards for review findings: selection and hidden state follow their objects, the Remove material message,
/// vertex-mode shortcuts only while the mode is active, and the cost of a drag tick on a large effect.</summary>
public static class VfxReviewFixSelfTests
{
    private static VfxFile Rich()
    {
        var f = VfxBuilder.NewFile();
        f = VfxEdit.AddSection(f, VfxBuilder.ImageMaterial("rv_a.tga"), null);
        f = VfxEdit.AddSection(f, VfxPrimitives.Box("Box"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Dummy("Host", 4), null);
        f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("Part", 0, 4, "Host"), null);
        f = VfxEdit.AddSection(f, VfxBuilder.Light("Lamp", 4), null);
        return f;
    }

    private static VfxDocument Doc(SelfTestContext ctx, VfxFile f) => new(ctx.Shell, new VfxKind { Shell = ctx.Shell }, f, "review.vfx", null);

    private static string? Name(VfxSection? s) => s is null ? null : VfxEdit.NameOf(s);

    [SelfTest("VFX review: selection and hidden objects follow their objects through move, undo and delete")]
    public static void SelectionFollowsObjects(SelfTestContext ctx)
    {
        using var d = Doc(ctx, Rich());
        int lamp = VfxEdit.FindByName(d.Current, "Lamp"), box = VfxEdit.FindByName(d.Current, "Box");
        d.Selection.Select(lamp);
        ctx.Check(VfxEditing.Apply(d, "Move up", f => VfxEdit.MoveSection(f, lamp, lamp - 1)), "move up applied");
        ctx.Check(Name(d.SelectedSection) == "Lamp", "after move up the selection is still the lamp: " + Name(d.SelectedSection));
        d.Undo();
        ctx.Check(Name(d.SelectedSection) == "Lamp", "after undo the selection is still the lamp: " + Name(d.SelectedSection));
        d.Redo();
        ctx.Check(Name(d.SelectedSection) == "Lamp", "after redo the selection is still the lamp: " + Name(d.SelectedSection));

        lamp = VfxEdit.FindByName(d.Current, "Lamp");
        d.SetHidden(lamp, true);
        ctx.Check(VfxEditing.Apply(d, "Delete", f => VfxEdit.RemoveSection(f, box)), "delete applied");
        int now = VfxEdit.FindByName(d.Current, "Lamp");
        ctx.Check(d.HiddenSections.Count == 1 && d.HiddenSections.Contains(now), $"the lamp stays hidden at its new index {now}: [{string.Join(",", d.HiddenSections)}]");
        d.Undo();
        ctx.Check(d.HiddenSections.Count == 1 && d.HiddenSections.Contains(lamp), "after undo the lamp is hidden again at its old index");

        // deleting the selected object selects its next sibling, else the previous one, else nothing
        box = VfxEdit.FindByName(d.Current, "Box");
        var siblings = Enumerable.Range(0, d.Current.Sections.Length).Where(i => VfxEdit.ParentOf(d.Current.Sections[i]) == VfxEdit.ParentOf(d.Current.Sections[box])).ToList();
        int at = siblings.IndexOf(box);
        string? expect = at + 1 < siblings.Count ? Name(d.Current.Sections[siblings[at + 1]]) : at > 0 ? Name(d.Current.Sections[siblings[at - 1]]) : null;
        d.Selection.Select(box);
        ctx.Check(Commands.VfxObjectCommands.Delete(d), "delete command applied");
        ctx.Check(Name(d.SelectedSection) == expect, $"after delete the selection moves to the sibling '{expect}': '{Name(d.SelectedSection)}'");
        if (expect is not null)
        {
            ctx.Check(Commands.VfxObjectCommands.Delete(d), "second delete applied");
            ctx.Check(d.SelectedSection is null || Name(d.SelectedSection) != expect, "the second delete moves on again (or to nothing)");
            d.Undo();
        }
        d.Undo();
        lamp = VfxEdit.FindByName(d.Current, "Lamp");
        ctx.Check(lamp >= 0 && VfxEdit.FindByName(d.Current, "Box") >= 0, "undo restores the deleted objects");

        // an edit replaces the section in place: selection and hidden state stay on it
        d.Selection.Select(lamp);
        ctx.Check(VfxEditing.Apply(d, "Rename", f => VfxEdit.Rename(f, lamp, "Lamp2")), "rename applied");
        ctx.Check(Name(d.SelectedSection) == "Lamp2" && d.HiddenSections.Contains(lamp), "an in-place edit keeps selection and hidden state");
    }

    [SelfTest("VFX review: Remove material shows the real reason, and the in-use hint only when the material is in use")]
    public static void RemoveMaterialMessage(SelfTestContext ctx)
    {
        var f = Rich();
        int mat = VfxEdit.MaterialSectionIndex(f, 0);
        using (var older = Doc(ctx, f with { Version = 0x3000E }))
        {
            VfxMaterialTab.RemoveMaterial(older, mat, null);
            ctx.Check(older.StatusMessage == VfxEditing.OlderVersionReason, "older file: the older-version reason stays: " + older.StatusMessage);
        }
        using var d = Doc(ctx, f);
        VfxMaterialTab.RemoveMaterial(d, mat, null);
        ctx.Check(d.StatusMessage.Contains("On remove, move users to", StringComparison.Ordinal), "in use: the replacement hint is shown: " + d.StatusMessage);
    }

    [SelfTest("VFX review: vertex-mode shortcuts apply only while vertex mode is active, not merely enabled")]
    public static void VertexShortcutsNeedActiveMode(SelfTestContext ctx)
    {
        var module = new VfxModule();
        var prop = typeof(VfxModule).GetProperty("VertexShortcuts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (!ctx.Check(prop?.GetValue(module) is IReadOnlyList<ShortcutInfo>, "vertex shortcuts reachable")) return;
        var all = (IReadOnlyList<ShortcutInfo>)prop!.GetValue(module)!;
        var selectAll = all.FirstOrDefault(s => s.Key == System.Windows.Input.Key.A && s.Modifiers == System.Windows.Input.ModifierKeys.Control);
        if (!ctx.Check(selectAll?.AppliesTo is not null, "Ctrl+A vertex shortcut found")) return;
        using var d = Doc(ctx, Rich());
        var mode = VfxVertexMode.Of(d);
        d.Selection.Select(VfxEdit.FindByName(d.Current, "Host"));
        mode.Enabled = true;
        ctx.Check(!mode.IsActive && !selectAll!.AppliesTo!(d), "enabled on a dummy (inactive): Ctrl+A is not claimed");
        d.Selection.Select(VfxEdit.FindByName(d.Current, "Box"));
        ctx.Check(mode.IsActive && selectAll!.AppliesTo!(d), "enabled on a mesh (active): Ctrl+A is claimed");
        mode.Enabled = false;
    }

    [SelfTest("VFX review: drag ticks on a large effect (200 boxes, a 20,000-vertex sphere, particles) stay cheap")]
    public static void DragTickCost(SelfTestContext ctx)
    {
        var f = Rich();
        f = VfxEdit.AddSection(f, VfxPrimitives.Sphere("Big", 1, 200, 100), null);
        for (int i = 0; i < 200; i++) f = VfxEdit.AddSection(f, VfxPrimitives.Box("B" + i), null);
        for (int i = 0; i < 20; i++) f = VfxEdit.AddSection(f, VfxBuilder.ParticleSystem("P" + i, 0, 4, "Host"), null);
        using var d = Doc(ctx, f);
        int big = VfxEdit.FindByName(d.Current, "Big");
        var positions = d.Current.Sections[big] is VfxMesh { Frames: [{ Positions: { } p0 }, ..] } ? VfxPositionCodec.Decode(p0) : [];
        if (!ctx.Check(positions.Length >= 19000,$"large mesh built ({positions.Length} vertices)")) return;
        if (!ctx.Check(VfxEditing.Begin(d, "Drag"), "drag started")) return;
        var sw = Stopwatch.StartNew();
        const int ticks = 20;
        for (int t = 1; t <= ticks; t++)
        {
            var moved = positions.Select(p => p + new Vector3(0.01f * t, 0, 0)).ToArray();
            VfxEditing.Update(d, x => VfxEdit.SetPositions(x, big, 0, moved));
        }
        sw.Stop();
        VfxEditing.Commit(d);
        double perTick = sw.Elapsed.TotalMilliseconds / ticks;
        // breakdown: the edit itself (quantise + bounds) and the sampler rebuild, without the document
        var file = d.Current;
        sw.Restart();
        for (int t = 1; t <= ticks; t++) VfxEdit.SetPositions(file, big, 0, positions.Select(p => p + new Vector3(0.01f * t, 0, 0)).ToArray());
        double edit = sw.Elapsed.TotalMilliseconds / ticks;
        sw.Restart();
        for (int t = 1; t <= ticks; t++) _ = new VfxSampler(file);
        double sampler = sw.Elapsed.TotalMilliseconds / ticks;
        // finding 14: what a recovery snapshot or a watcher check (MatchesSaved) costs on the UI thread for this effect
        sw.Restart();
        int size = 0;
        for (int t = 0; t < 5; t++) size = VfxWriter.Write(file).Length;
        ctx.Check(size > 0, $"serialize for recovery / watcher check: {sw.Elapsed.TotalMilliseconds / 5:0.0} ms for {size / 1024} KiB");
        ctx.Check(perTick < 250, $"a drag tick stays under 250 ms: {perTick:0.0} ms per tick over {ticks} ticks, {d.Current.Sections.Length} sections (edit alone {edit:0.0} ms, new sampler {sampler:0.0} ms)");
    }
}
