using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>Material tab: the selected material (or the material of the selected mesh's first slot / particle system).</summary>
public sealed class VfxMaterialTab(VfxDocument doc) : VfxInspectorPage(doc)
{
    protected override bool UsesFrame => true;

    /// <summary>"n: texture" for every material, in material order.</summary>
    public static IReadOnlyList<string> Labels(VfxFile f) => [.. f.Sections.OfType<VfxMaterial>().Select((m, i) => Format(i, m))];
    public static string Label(VfxFile f, int ordinal) => f.Sections.OfType<VfxMaterial>().ElementAtOrDefault(ordinal) is { } m ? Format(ordinal, m) : "";
    private static string Format(int i, VfxMaterial m) => $"{i}: {(m.Type == 2 ? "colour" : m.Texture0?.Name ?? "(none)")}";

    /// <summary>The material ordinal this tab edits, or -1.</summary>
    public int Ordinal => Doc.SelectedSection switch
    {
        VfxMaterial m => File.Sections.OfType<VfxMaterial>().ToList().IndexOf(m),
        VfxMesh { MaterialIndices: { Length: > 0 } mi } => Doc.Selection.Material >= 0 && Doc.Selection.Material < mi.Length ? mi[Doc.Selection.Material] : mi[0],
        VfxParticleSystem p => p.MaterialIndex ?? -1,
        _ => -1,
    };

    /// <summary>
    /// Removes the material at section <paramref name="index"/>, moving its users to material <paramref name="to"/>.
    /// The replacement hint replaces the refusal message only when the edit ran and the material is in use; any other
    /// reason (older version, read-only, bad replacement) stays on the status bar as posted.
    /// </summary>
    internal static void RemoveMaterial(VfxDocument doc, int index, int? to)
    {
        bool inUse = false;
        VfxFile Remove(VfxFile f)
        {
            try { return VfxEdit.RemoveSection(f, index, to); }
            catch (InvalidOperationException) when (to is null) { inUse = true; throw; }
        }
        if (!VfxEditing.Apply(doc, "Remove material", Remove) && inUse)
            doc.ShowStatus("The material is in use: choose a replacement in \"On remove, move users to\" and press Remove again.");
    }

    private int Index => Ordinal < 0 ? -1 : VfxEdit.MaterialSectionIndex(File, Ordinal);
    private VfxMaterial? Mat => Index >= 0 && Index < File.Sections.Length ? File.Sections[Index] as VfxMaterial : null;
    private IEnumerable<T> One<T>(Func<VfxMaterial, T> get) => Mat is { } m ? [get(m)] : [];
    private Func<VfxFile, V, VfxFile> Upd<V>(Func<VfxMaterial, V, VfxMaterial> u) => (f, v) => VfxEdit.UpdateMaterial(f, VfxEdit.MaterialSectionIndex(f, Ordinal), m => u(m, v));

    protected override string StructureKey() => $"{Ordinal}|{Mat?.Type}|{File.Sections.Length}";

    protected override void Build()
    {
        Header("Materials");
        Buttons(("Add", "Append a new colour material (set its type and texture below)", () => { VfxEditing.Apply(Doc, "Add material", f => VfxEdit.AddSection(f, VfxBuilder.ColorMaterial())); }),
            ("Duplicate", "Copy this material", () => { if (Mat is { } m) VfxEditing.Apply(Doc, "Duplicate material", f => VfxEdit.AddSection(f, m)); }),
            ("Select users", "Select the objects that use this material", SelectUsers));
        if (Mat is null) { Fact("Hint", () => "Select a material in the object list, or a mesh or particle system that uses one."); return; }
        Fact("Material", () => Label(File, Ordinal), "vfx.material.type");
        // a plain combo, not a Choice editor: picking a target must not commit an edit nor be reset by the value refresh
        var reassign = new System.Windows.Controls.ComboBox { ItemsSource = Labels(File), ToolTip = "When this material is in use, its users switch to the material chosen here; then press Remove" };
        System.Windows.Automation.AutomationProperties.SetName(reassign, "On remove, move users to");
        ReassignBox = reassign;
        Row("On remove, move users to", reassign, (string)reassign.ToolTip);
        Buttons(("Remove", "Remove this material (asks for a replacement when it is in use)", () =>
        {
            int target = Labels(File).ToList().IndexOf(reassign.SelectedItem as string ?? "");
            int? to = target >= 0 && target != Ordinal ? target - (target > Ordinal ? 1 : 0) : null;
            RemoveMaterial(Doc, Index, to);
        }));
        Header("Appearance");
        Choice("Type", Tip("vfx.material.type", "Image, mix of two textures, or solid colour"), () => ["Image", "Two-texture mix", "Colour only"], () => One(m => m.Type switch { 1 => "Two-texture mix", 2 => "Colour only", _ => "Image" }),
            (f, v) => VfxEdit.SetMaterialType(f, VfxEdit.MaterialSectionIndex(f, Ordinal), v switch { "Two-texture mix" => 1, "Colour only" => 2, _ => 0 }));
        Check("Additive", Tip("vfx.material.additive", "Adds light to the scene instead of blending"), () => One(m => m.Additive is 1), (f, on) => VfxEdit.SetAdditive(f, Index, on));
        for (int slot = 0; slot < (Mat.Type == 1 ? 2 : 1); slot++)
        {
            int s = slot;
            VfxTexture? Tex(VfxMaterial m) => s == 0 ? m.Texture0 : m.Texture1;
            VfxMaterial With(VfxMaterial m, VfxTexture t) => s == 0 ? m with { Texture0 = t } : m with { Texture1 = t };
            var texBox = Text($"Texture {s + 1}", Tip("vfx.material.texture", "Texture file (.tga or animated .vbm) from the game archives or a search folder. \"$original_map\" names are placeholders the game replaces with the host's texture"), () => One(m => Tex(m)?.Name ?? ""), (f, v) => VfxEdit.SetTexture(f, Index, v, s));
            AddPickButton(texBox, s);
            Number($"Tex {s + 1} start frame", "First frame of an animated texture", () => One(m => (double)(Tex(m)?.StartFrame ?? 0)), Upd<double>((m, v) => Tex(m) is { } t ? With(m, t with { StartFrame = (int)v }) : m), 0, 1, 0);
            Number($"Tex {s + 1} rate", "Playback rate of an animated texture (1 = its own fps)", () => One(m => (double)(Tex(m)?.PlaybackRate ?? 1)), Upd<double>((m, v) => Tex(m) is { } t ? With(m, t with { PlaybackRate = F(v) }) : m), 2, 0.1, 0);
            Choice($"Tex {s + 1} playback", Tip("vfx.material.texture", "anim_type, as the engine plays it (TexMap__get_tex_handle):\n0 = repeats (frame index wraps); the format docs call it \"loop\".\n1 = holds the last frame (index clamps); the format docs call it \"ping-pong\", but the engine never plays backwards.\n2 = repeats (wraps), same as 0; the format docs call it \"once\". Every stock texture uses 2."),
                () => TexModes, () => One(m => TexModes[Math.Clamp(Tex(m)?.AnimType ?? 2, 0, 2)]),
                Upd<string>((m, v) => Tex(m) is { } t ? With(m, t with { AnimType = Math.Max(0, Array.IndexOf(TexModes, v)) }) : m));
        }
        Header("Colour and lighting");
        // one row, three boxes (red, green, blue); each box keeps its own name for screen readers and undo
        var rgb = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var (name, comp) in new[] { ("Red", 0), ("Green", 1), ("Blue", 2) })
        {
            var box = Number("Colour " + name, Tip("vfx.material.solid_color", $"Solid colour, {name.ToLowerInvariant()} component (0-255)"), () => One(m => (double)Comp(m.SolidColor, comp)), Upd<double>((m, v) => m with { SolidColor = WithComp(m.SolidColor, comp, (int)v) }), 0, 1, 0, 255, addRow: false);
            box.Margin = new System.Windows.Thickness(comp == 0 ? 0 : 2, 0, 0, 0);
            rgb.Children.Add(box);
        }
        Row("Colour (R, G, B)", rgb, "Solid colour used by \"Colour only\" materials, 0-255 per component");
        foreach (var (name, comp) in new[] { ("Specular", 0), ("Gloss", 1), ("Reflection", 2) })
            Number(name, "Specular / gloss / reflection amount", () => One(m => (double)(m.SpecularGlossReflection is { } v ? v[comp] : 0)), Upd<double>((m, v) => { var s = m.SpecularGlossReflection ?? default; s[comp] = F(v); return m with { SpecularGlossReflection = s }; }), 3, 0.05);
        Header("Tracks");
        Number("Fps", Tip("vfx.material.fps", "Frame rate of the material tracks (opacity, self-illumination, mix), in frames per second"), () => One(m => (double)(m.Fps ?? 15)), Upd<double>((m, v) => m with { Fps = (int)v }), 0, 1, 1, 120);
        Track(VfxMaterialTrack.Opacity, "Opacity", "vfx.material.opacity", m => m.Opacity);
        Track(VfxMaterialTrack.SelfIllumination, "Self-illumination", "vfx.material.self_illumination", m => m.SelfIllumination);
        if (Mat.Type == 1) Track(VfxMaterialTrack.Mix, "Mix", null, m => m.Mix);
    }

    /// <summary>The "On remove, move users to" target list (self-tests pick from it).</summary>
    internal System.Windows.Controls.ComboBox? ReassignBox { get; private set; }

    internal static readonly string[] TexModes = ["0: Repeat (wraps)", "1: Hold last frame (clamps)", "2: Repeat (wraps; stock)"];

    /// <summary>Track sample shown at the playhead: the engine reads index floor(frame / 15 * material fps), last sample past the end.</summary>
    internal static int TrackIndex(VfxMaterial m, float frame, int count) =>
        Math.Clamp((int)Math.Floor(Math.Max(0, frame) / 15.0 * (m.Fps is > 0 ? m.Fps.Value : 15)), 0, Math.Max(0, count - 1));

    private static int Comp(VfxColorI? c, int i) => c is not { } x ? 0 : i switch { 0 => x.R, 1 => x.G, _ => x.B };
    private static VfxColorI WithComp(VfxColorI? c, int i, int v) => (c ?? new VfxColorI(128, 128, 128)) switch { var x => i switch { 0 => x with { R = v }, 1 => x with { G = v }, _ => x with { B = v } } };

    private void Track(VfxMaterialTrack track, string name, string? docsId, Func<VfxMaterial, IReadOnlyList<float>?> get)
    {
        Header(name + " track");
        int Sample(VfxMaterial m) => TrackIndex(m, (float)Doc.TimelineFrame, get(m)?.Count ?? 1);
        Fact("Samples", () => One(m => get(m) is { Count: > 0 } t ? $"{t.Count} (min {t.Min():0.##}, max {t.Max():0.##})" : "none").FirstOrDefault() ?? "", docsId);
        var strip = new VfxCurveStrip(Doc, () => Index, track, get, name) { Margin = new System.Windows.Thickness(0, 2, 0, 4) };
        Strips[name] = strip;
        Body.Children.Add(strip);
        Watch(strip.InvalidateVisual);
        Number("At playhead", Tip(docsId, "Track value at the current frame (0-1)"), () => One(m => get(m) is { Count: > 0 } t ? (double)t[Sample(m)] : 0),
            (f, v) => VfxEdit.SetTrackSample(f, Index, track, Sample(Mat!), F(v)), 3, 0.05, 0, 1);
        Buttons(("Constant", "Every sample takes the value at the playhead", () => Fill(track, get, (t, at) => [(0, t[at])])),
            ("Linear ramp", "Straight line between the first and last samples", () => Fill(track, get, (t, _) => [(0, t[0]), (t.Count - 1, t[^1])])),
            ("Fit to effect length", "Resize the track so its last sample falls on the effect's end frame at the material frame rate (holds the last value)",
                () => { if (Mat is { } m) VfxEditing.Apply(Doc, $"Resize {name.ToLowerInvariant()} track", f => VfxEdit.ResizeTrack(f, Index, track, TrackIndex(m, Doc.EndFrame, int.MaxValue) + 1)); }));
    }

    /// <summary>Puts a "..." button opening <see cref="VfxTexturePicker"/> beside a texture name box (one undo step on OK).</summary>
    private void AddPickButton(System.Windows.Controls.TextBox box, int slot)
    {
        if (box.Parent is not System.Windows.Controls.Grid row) return;
        int col = System.Windows.Controls.Grid.GetColumn(box);
        row.Children.Remove(box);
        var b = new System.Windows.Controls.Button { Content = "...", MinWidth = 30, Padding = new System.Windows.Thickness(4, 0, 4, 0), Margin = new System.Windows.Thickness(4, 0, 0, 0), ToolTip = "Choose a texture from the game folder and archives, with a preview" };
        System.Windows.Automation.AutomationProperties.SetName(b, $"Choose texture {slot + 1}");
        b.Click += (_, _) =>
        {
            if (VfxTexturePicker.Pick(System.Windows.Window.GetWindow(this), () => Doc.Resolver, box.Text.Trim()) is { Length: > 0 } name && name != box.Text.Trim())
                VfxEditing.Apply(Doc, $"Edit texture {slot + 1}", f => VfxEdit.SetTexture(f, Index, name, slot));
        };
        b.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(IsEnabled)) { Source = box });
        var dock = new System.Windows.Controls.DockPanel();
        System.Windows.Controls.DockPanel.SetDock(b, System.Windows.Controls.Dock.Right);
        dock.Children.Add(b);
        dock.Children.Add(box);
        System.Windows.Controls.Grid.SetColumn(dock, col);
        row.Children.Add(dock);
    }

    /// <summary>Curve strips of the selected material by track name (self-test access).</summary>
    internal Dictionary<string, VfxCurveStrip> Strips { get; } = [];

    private void Fill(VfxMaterialTrack track, Func<VfxMaterial, IReadOnlyList<float>?> get, Func<IReadOnlyList<float>, int, (float, float)[]> points)
    {
        if (Mat is not { } m || get(m) is not { Count: > 0 } t) return;
        int at = TrackIndex(m, (float)Doc.TimelineFrame, t.Count);
        VfxEditing.Apply(Doc, $"Fill {track} track", f => VfxEdit.FillTrack(f, Index, track, t.Count, points(t, at)));
    }

    private void SelectUsers()
    {
        int o = Ordinal; bool first = true;
        for (int i = 0; i < File.Sections.Length; i++)
            if (File.Sections[i] is VfxMesh { MaterialIndices: { } mi } && mi.Contains(o) || File.Sections[i] is VfxParticleSystem { MaterialIndex: var pm } && pm == o)
            { Doc.Selection.Select(i, add: !first); first = false; }
        if (first) Doc.ShowStatus("No object uses this material.");
    }
}
