using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Cairn.Rfa.Formats.V3d;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Dialogs;

/// <summary>Builders for the creation / import / export dialogs. Each returns the (unshown) form and a reader for its result.</summary>
public static class VfxCreationDialogs
{
    private static readonly string[] Targets = ["New effect", "Add objects to the current effect"];

    public static VfxForm Primitive(VfxFile file, Window? owner, out Func<VfxPrimitiveRequest> result)
    {
        var form = new VfxForm("Add primitive", owner, "_Add");
        var kind = form.Combo("_Type:", VfxCreation.PrimitiveLabels, 1, "Shape of the new mesh");
        var name = form.Text("_Name:", "", "Object name (made unique; empty = type name)");
        var a = form.Text("Size _A (m)", "1", "Width, size, outer radius or radius, in metres, depending on the type");
        var b = form.Text("Size _B (m)", "1", "Depth, rod length, inner radius (0 makes a disc) or top radius (0 makes a cone), in metres");
        var h = form.Text("_Height (m)", "1", "Height of cylinders, cones and boxes, in metres");
        var seg = form.Text("_Segments", "16", "Segments around discs, cylinders and spheres");
        var materials = new List<object> { "New image material", "New colour material" };
        for (int m = 0; m < VfxCreation.MaterialCount(file); m++)
        {
            var mat = (VfxMaterial)file.Sections[VfxEdit.MaterialSectionIndex(file, m)];
            materials.Add($"Material {m + 1}: {mat.Texture0?.Name ?? "(colour)"}");
        }
        var material = form.Combo("_Material:", materials, 0, "Use an existing material or create one");
        var texture = form.Text("_Texture:", "cairn_placeholder.tga", "Texture file name (.tga or .vbm) resolved through the game folders");
        var additive = form.Check("A_dditive", true, "Add colours to the background (glows, flashes)");
        var full = form.Check("_Fullbright", true, "Self-illumination 1: ignore scene lighting");
        var frames = form.Text("_Frames:", VfxCreation.EffectFrames(file).ToString(), "Frame count (15 per second); defaults to the effect length");
        result = () => new VfxPrimitiveRequest((VfxPrimitiveKind)Math.Max(0, kind.SelectedIndex), name.Text,
            VfxForm.Number(a, 1), VfxForm.NumberOrZero(b), VfxForm.Number(h, 1), (int)VfxForm.Number(seg, 16),
            material.SelectedIndex >= 2 ? material.SelectedIndex - 2 : -1,
            material.SelectedIndex == 0 ? texture.Text : null, additive.IsChecked == true, full.IsChecked == true, (int)VfxForm.Number(frames, 1));
        var read = result;
        form.Changed += (_, _) =>
        {
            texture.IsEnabled = material.SelectedIndex == 0;
            var r = read();
            var (f, i) = VfxCreation.AddPrimitive(file, r);
            form.Summary = $"{VfxCreation.PrimitiveLabels[(int)r.Kind]} \"{VfxSections.NameOf(f.Sections[i])}\": {r.Frames} {(r.Frames == 1 ? "frame" : "frames")}, placed at the origin;" +
                (r.ExistingMaterial >= 0 ? $"uses material {r.ExistingMaterial}." : $"new {(r.Texture is null ? "colour" : "image")} material {(r.Additive ? "(additive)" : "")}.");
        };
        return form;
    }

    private static string N(int n, string word) => $"{n} {word}{(n == 1 ? "" : word.EndsWith("sh") ? "es" : "s")}";

    public static VfxForm GltfImport(string path, VfxFile imported, IReadOnlyList<string> messages, bool canAdd, Window? owner, out Func<bool> addToCurrent)
    {
        var form = new VfxForm("Import glTF as effect", owner, "_Import");
        var target = form.Combo("_Import into:", Targets.Take(canAdd ? 2 : 1), canAdd ? 1 : 0, "Open as a new effect, or copy the objects into the active effect (one undo step)");
        addToCurrent = () => target.SelectedIndex == 1;
        int objects = imported.Sections.Count(VfxTransplant.IsObject);
        form.Summary = $"{System.IO.Path.GetFileName(path)}: {N(objects, "object")}, {N(VfxCreation.MaterialCount(imported), "material")}, {N(imported.EndFrame + 1, "frame")}.\n" +
            (messages.Count == 0 ? "Everything in the file was read; nothing was skipped or approximated." : string.Join("\n", messages));
        return form;
    }

    public static VfxForm FromV3d(V3dFile mesh, bool canAdd, Window? owner, out Func<(VfxFromV3dOptions Options, bool AddToCurrent)> result)
    {
        var form = new VfxForm("Geometry from mesh", owner, "_Import");
        var subs = mesh.Submeshes.ToList();
        var names = new List<object> { $"All submeshes ({subs.Count})" };
        names.AddRange(subs.Select((s, i) => (object)$"{i}: {s.Name}"));
        var sub = form.Combo("_Submesh:", names, 0, "Which submesh to convert");
        var lod = form.Text("_Level of detail:", "0", "Level of detail (0 = most detailed; past the last uses the last)");
        var additive = form.Check("A_dditive materials", false, "New image materials (one per mesh material) blend additively");
        var target = form.Combo("_Import into:", Targets.Take(canAdd ? 2 : 1), canAdd ? 1 : 0, "New effect, or add to the active effect (one undo step)");
        result = () => (new VfxFromV3dOptions { SubmeshIndex = sub.SelectedIndex > 0 ? sub.SelectedIndex - 1 : null, LodIndex = (int)VfxForm.NumberOrZero(lod), Additive = additive.IsChecked == true },
            target.SelectedIndex == 1);
        var read = result;
        form.Changed += (_, _) =>
        {
            try
            {
                var f = VfxFromV3d.CreateFile(mesh, read().Options);
                form.Summary = $"Creates {N(f.Sections.Count(s => s is VfxMesh), "mesh")} and {N(VfxCreation.MaterialCount(f), "material")} (a material identical to one already in the effect is reused).";
                form.CanAccept = true;
            }
            catch (Exception ex) { form.Summary = "Cannot convert: " + ex.Message; form.CanAccept = false; }
        };
        return form;
    }

    public static VfxForm Transplant(VfxFile source, string sourceName, Window? owner, out Func<(int[] Sections, int Offset)> result)
    {
        var form = new VfxForm("Objects from another effect", owner, "_Add");
        var list = new ListBox { Height = 200 };
        var checks = new List<(CheckBox Box, int Index)>();
        for (int i = 0; i < source.Sections.Length; i++)
        {
            if (!VfxTransplant.IsObject(source.Sections[i])) continue;
            var box = new CheckBox { Content = $"{VfxSections.NameOf(source.Sections[i])}  ({source.Sections[i].GetType().Name.Replace("Vfx", "")})", IsChecked = true };
            AutomationProperties.SetName(box, VfxSections.NameOf(source.Sections[i]));
            box.Click += (_, _) => form.Refresh();
            checks.Add((box, i)); list.Items.Add(box);
        }
        form.Row("O_bjects:", list, $"Objects of {sourceName}; their materials come along (identical ones are reused)");
        var offset = form.Text("Time _offset:", "0", "Frames (15 per second) to delay the copied objects by; negative drops leading frames");
        result = () => ([.. checks.Where(c => c.Box.IsChecked == true).Select(c => c.Index)], (int)VfxForm.NumberOrZero(offset));
        var read = result;
        form.Changed += (_, _) =>
        {
            var (sections, off) = read();
            form.CanAccept = sections.Length > 0;
            form.Summary = $"{sections.Length} of {N(checks.Count, "object")} from {sourceName}" +
                (off == 0 ? ", at their original times." : off > 0 ? $", starting {N(off, "frame")} later." : $", starting {N(-off, "frame")} earlier (frames before 0 are dropped).");
        };
        return form;
    }

    public static VfxForm GltfExport(string name, Window? owner, out Func<bool> glb)
    {
        var form = new VfxForm("Export glTF", owner, "_Export...");
        var box = form.Check("_Binary (.glb)", false, "One .glb file instead of .gltf + .bin");
        glb = () => box.IsChecked == true;
        form.Changed += (_, _) => form.Summary = box.IsChecked == true
            ? $"Writes {name}.glb (current state, including unsaved edits)."
            : $"Writes {name}.gltf + {name}.bin (current state, including unsaved edits). This is the form REDUX reads.";
        return form;
    }
}
