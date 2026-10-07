using System.Numerics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>
/// Object tab: every field of the selected mesh / particle system / dummy / light / spacewarp. Sections of the
/// primary's type in the selection are edited together; per-frame values are at the playhead with "apply to all".
/// </summary>
public sealed class VfxObjectTab(VfxDocument doc) : VfxInspectorPage(doc)
{
    private VfxFrameFill _fill = VfxFrameFill.Hold;
    protected override bool UsesFrame => true;

    protected override string StructureKey()
    {
        var p = Doc.SelectedSection;
        string extra = p switch { VfxMesh m => $"{m.MaterialIndices?.Length}/{m.IsKeyframed}", VfxParticleSystem => string.Join(",", File.Sections.OfType<VfxSpacewarp>().Select(w => w.Name)), _ => "" };
        return $"{p?.GetType().Name}|{string.Join(",", Doc.Selection.Sections)}|{extra}|{File.Sections.Length}";
    }

    private List<int> Sel<T>() where T : VfxSection => VfxEditing.SelectedOf<T>(Doc);
    private IEnumerable<R> Vals<T, R>(Func<T, R> get) where T : VfxSection => Sel<T>().Select(i => get((T)File.Sections[i]));
    private Func<VfxFile, V, VfxFile> Each<T, V>(Func<VfxFile, int, V, VfxFile> edit) where T : VfxSection => (f, v) => Sel<T>().Aggregate(f, (a, i) => edit(a, i, v));
    private int FrameOf(int count) => VfxEditing.FrameIndex(Doc, count);

    private IReadOnlyList<string> ParentChoices() =>
        ["Scene Root", .. File.Sections.Where(VfxTransplant.IsObject).Select(VfxSections.NameOf).Where(n => n.Length > 0).Distinct()];

    protected override void Build()
    {
        var p = Doc.SelectedSection;
        if (p is null || !VfxTransplant.IsObject(p)) { Header("No object selected"); Fact("Hint", () => "Select an object to edit it: click it in the preview or the object list. Materials are edited on the Material tab."); return; }
        int n = Doc.Selection.Sections.Count(i => i < File.Sections.Length && File.Sections[i].GetType() == p.GetType());
        Header($"{VfxSections.KindText(VfxSections.KindOf(p))}{(n > 1 ? $" ({n} selected)" : "")}");
        if (n == 1)
            Text("Name", Tip("vfx.object.name", "Object name; children and spacewarp lists that use it follow a rename"), () => [VfxSections.NameOf(Doc.SelectedSection!) ?? ""],
                (f, v) => VfxEdit.Rename(f, Doc.Selection.Primary, v));
        Choice("Parent", Tip("vfx.object.parent", "Parent object, or any external bone / prop name typed in"), ParentChoices,
            () => Sel<VfxSection>().Select(i => VfxEdit.ParentOf(File.Sections[i]) ?? "Scene Root"), (f, v) => Sel<VfxSection>().Aggregate(f, (a, i) => VfxEdit.Reparent(a, i, v)), editable: true);
        switch (p)
        {
            case VfxMesh: BuildMesh(); break;
            case VfxParticleSystem: BuildParticles(); break;
            case VfxDummy: BuildDummy(); break;
            case VfxLight: BuildLight(); break;
            case VfxSpacewarp: BuildWarp(); break;
        }
    }

    private static readonly (string Flag, string Label, string Tip)[] MeshFlags =
    [
        ("Facing", "Facing (billboard)", "Always turns to face the camera; uses the facing width/height"),
        ("FacingRod", "Facing rod", "Turns around the up vector only (beams, trails)"),
        ("Morph", "Morph (per-frame vertices)", "Stores vertex positions per frame; switching converts the mesh"),
        ("Fullbright", "Fullbright", "Ignores scene lighting"),
        ("DumpUvs", "Animated UVs", "Stores UVs per frame (UV scroll)"),
        ("NoInterp", "No interpolation", "Steps between frames instead of blending"),
        ("Fire", "Fire (rare)", "Rarely used: the game draws the mesh with its fire shader path"),
        ("Corona", "Corona (rare)", "Rarely used: drawn as a corona/glow, faded when occluded"),
        ("Sky", "Sky (rare)", "Rarely used: drawn with the sky (behind the level)"),
        ("SeeThrough", "See-through (rare)", "Rarely used: drawn without depth test"),
    ];

    private void BuildMesh()
    {
        Header("Flags");
        foreach (var (flag, label, tip) in MeshFlags)
        {
            uint bit = VfxEdit.FlagNames(Doc.SelectedSection!)[flag];
            Check(label, Tip("vfx.mesh.flags", tip), () => Vals<VfxMesh, bool>(m => (m.Flags & bit) != 0), Each<VfxMesh, bool>((f, i, on) => VfxEdit.SetFlag(f, i, flag, on)));
        }
        Header("Timing");
        Number("Frame rate", Tip("vfx.mesh.fps", "Frames per second of this mesh's animation"), () => Vals<VfxMesh, double>(m => m.Fps ?? 15), Each<VfxMesh, double>((f, i, v) => VfxEdit.SetFps(f, i, (int)v)), 0, 1, 1, 120, " fps");
        Number("Start time", Tip("vfx.mesh.start_time", "When the mesh appears, in seconds from the effect start"), () => Vals<VfxMesh, double>(m => m.StartTime ?? 0), Each<VfxMesh, double>((f, i, v) => VfxEdit.SetStartTime(f, i, F(v))), 3, 1 / 15.0, 0, 10000, " s");
        Number("Frame count", Tip("vfx.mesh.num_frames", "Shortening trims; lengthening fills as chosen below"), () => Vals<VfxMesh, double>(m => m.Frames.Length), Each<VfxMesh, double>((f, i, v) => VfxEdit.SetFrameCount(f, i, Math.Max(1, (int)v), _fill)), 0, 1, 1, 100000);
        var fill = new System.Windows.Controls.ComboBox { ItemsSource = new[] { "Hold last frame", "Loop from start", "Resample (stretch)" }, SelectedIndex = (int)_fill, ToolTip = "How a new frame count is filled: hold extends with the last frame, loop repeats from the start, resample stretches the animation" };
        System.Windows.Automation.AutomationProperties.SetName(fill, "Frame count fill");
        fill.SelectionChanged += (_, _) => _fill = (VfxFrameFill)Math.Max(0, fill.SelectedIndex);
        Row("Lengthen by", fill, (string)fill.ToolTip);
        Fact("Animation", () => Vals<VfxMesh, string>(m => (m.Flags & VfxMeshFlags.Morph) != 0 ? "Morph (per-frame vertices)" : m.IsKeyframed is 1 ? "Keyframed transform" : m.Frames.Length > 1 ? "Per-frame transforms" : "Static").FirstOrDefault() ?? "", "vfx.mesh.is_keyframed");
        Buttons(("Static", "Keep the frame at the playhead only", () => VfxEditing.ApplyEach(Doc, "Convert to static", Sel<VfxMesh>(), (f, i) => VfxEdit.ToStatic(f, i, FrameOf(((VfxMesh)f.Sections[i]).Frames.Length)))),
            ("Per-frame", "Bake keys into one transform per frame", () => VfxEditing.ApplyEach(Doc, "Convert to per-frame transforms", Sel<VfxMesh>(), VfxEdit.ToPerFrameTransforms)),
            ("Keyframed", "One key per frame (reduced)", () => VfxEditing.ApplyEach(Doc, "Convert to keyframes", Sel<VfxMesh>(), (f, i) => VfxEdit.ToKeyframes(f, i))),
            ("Morph", "Bake transforms into per-frame vertices", () => VfxEditing.ApplyEach(Doc, "Convert to morph", Sel<VfxMesh>(), VfxEdit.ToMorph)));
        if (Doc.SelectedSection is VfxMesh { IsKeyframed: 1 }) BuildPivot();
        Header("Facing (at the playhead)");
        Vector2 Size(VfxMesh m) => m.Frames[FrameOf(m.Frames.Length)].FacingSize ?? Vector2.Zero;
        Number("Width", "Facing quad width at the current frame", () => Vals<VfxMesh, double>(m => Size(m).X), Each<VfxMesh, double>((f, i, v) => { var m = (VfxMesh)f.Sections[i]; return VfxEdit.SetFacingSize(f, i, new(F(v), Size(m).Y), FrameOf(m.Frames.Length)); }), 3, 0.1, 0, suffix: " m");
        Number("Height", "Facing quad height at the current frame", () => Vals<VfxMesh, double>(m => Size(m).Y), Each<VfxMesh, double>((f, i, v) => { var m = (VfxMesh)f.Sections[i]; return VfxEdit.SetFacingSize(f, i, new(Size(m).X, F(v)), FrameOf(m.Frames.Length)); }), 3, 0.1, 0, suffix: " m");
        Buttons(("Apply size to all frames", "Copy the width/height at the playhead to every frame", () => VfxEditing.ApplyEach(Doc, "Facing size to all frames", Sel<VfxMesh>(), (f, i) => VfxEdit.SetFacingSize(f, i, Size((VfxMesh)f.Sections[i]), null))));
        Vector3 Up(VfxMesh m) => m.Frames[0].UpVector ?? Vector3.UnitY;
        foreach (var (axis, get, set) in new (string, Func<Vector3, float>, Func<Vector3, float, Vector3>)[] { ("X", u => u.X, (u, v) => u with { X = v }), ("Y", u => u.Y, (u, v) => u with { Y = v }), ("Z", u => u.Z, (u, v) => u with { Z = v }) })
            Number("Rod up " + axis, "Facing rod axis (up vector), all frames", () => Vals<VfxMesh, double>(m => get(Up(m))), Each<VfxMesh, double>((f, i, v) => VfxEdit.SetUpVector(f, i, set(Up((VfxMesh)f.Sections[i]), F(v)))), 3, 0.1);
        Header("Material slots");
        var mesh = (VfxMesh)Doc.SelectedSection!;
        for (int s = 0; s < (mesh.MaterialIndices?.Length ?? 0); s++)
        {
            int slot = s;
            Choice($"Slot {slot}", Tip("vfx.mesh.material_indices", "Material used by faces in this slot"), () => VfxMaterialTab.Labels(File),
                () => Vals<VfxMesh, string>(m => m.MaterialIndices is { } mi && slot < mi.Length ? VfxMaterialTab.Label(File, mi[slot]) : ""),
                Each<VfxMesh, string>((f, i, v) => { var mi = ((VfxMesh)f.Sections[i]).MaterialIndices!.Value.ToArray(); if (slot < mi.Length) mi[slot] = VfxMaterialTab.Labels(f).ToList().IndexOf(v); return mi[slot] < 0 ? f : VfxEdit.SetMaterialSlots(f, i, mi); }));
        }
        Buttons(("Add slot", "Add a material slot using the first material", () => VfxEditing.ApplyEach(Doc, "Add material slot", Sel<VfxMesh>(), (f, i) => VfxEdit.SetMaterialSlots(f, i, [.. ((VfxMesh)f.Sections[i]).MaterialIndices ?? [], 0]))),
            ("Remove last slot", "Remove the last slot (its faces must be reassigned first)", () => VfxEditing.ApplyEach(Doc, "Remove material slot", Sel<VfxMesh>(), (f, i) => VfxEdit.SetMaterialSlots(f, i, [.. (((VfxMesh)f.Sections[i]).MaterialIndices ?? []).SkipLast(1)]))));
        Header("Geometry");
        Fact("Vertices / faces", () => string.Join("; ", Vals<VfxMesh, string>(m => $"{m.NumVertices} / {m.Faces.Length}")), "vfx.mesh.num_vertices");
        Fact("Bounds", () => Vals<VfxMesh, string>(m => $"centre {m.BoundingCenter.X:0.##}, {m.BoundingCenter.Y:0.##}, {m.BoundingCenter.Z:0.##}; radius {m.BoundingRadius:0.###}").FirstOrDefault() ?? "");
    }

    /// <summary>Particle flag bits as the engine reads them.</summary>
    internal static readonly (uint Bit, string Name, string Tip)[] ParticleFlagRows =
    [
        (0x1, "Fullbright", "Drawn at full colour; otherwise the particle is lit, floored by the material's self-illumination"),
        (0x2, "Gravity", "Particles accelerate downward at 9.8 units/s²"),
        (0x4, "Texture animates over particle life", "An animated texture plays from birth to death of each particle; otherwise it follows effect time"),
        (0x8, "Inherit host velocity", "The velocity of the object carrying the effect is added at birth"),
        (0x10, "Random orientation", "Each particle starts at a random roll angle; otherwise 0"),
        (0x20, "No cull", "Stored by the tools; no engine test of this bit was found"),
        (0x100, "Drops (streaks)", "Drawn as a streak along the velocity instead of a sprite; Tail distance sets its length"),
    ];

    /// <summary>Pivot of keyframed meshes: translation, rotation as Euler degrees (yaw about Y, pitch about X, roll about Z), scale.</summary>
    private void BuildPivot()
    {
        Header("Pivot");
        IEnumerable<VfxTransform> Pivots() => Vals<VfxMesh, VfxTransform?>(m => m.Pivot).OfType<VfxTransform>();
        Func<VfxFile, double, VfxFile> Set(Func<VfxTransform, float, VfxTransform> change) =>
            Each<VfxMesh, double>((f, i, v) => ((VfxMesh)f.Sections[i]).Pivot is { } pv ? VfxEdit.SetPivot(f, i, change(pv, F(v))) : f);
        string[] axes = ["X", "Y", "Z"];
        for (int a = 0; a < 3; a++)
        {
            int k = a;
            Number("Pivot " + axes[k], Tip("vfx.mesh.pivot", $"Pivot translation {axes[k]} (units)"), () => Pivots().Select(p => (double)Get(p.Translation, k)),
                Set((p, v) => p with { Translation = With(p.Translation, k, v) }), 3, 0.1);
        }
        for (int a = 0; a < 3; a++)
        {
            int k = a;
            Number("Pivot rotation " + axes[k], Tip("vfx.mesh.pivot", $"Pivot rotation about {axes[k]} in degrees (stored as a quaternion)"),
                () => Pivots().Select(p => Math.Round(Get(ToEuler(p.Rotation), k), 3)),
                Set((p, v) => p with { Rotation = FromEuler(With(ToEuler(p.Rotation), k, v)) }), 2, 5, -360, 360, "°");
        }
        for (int a = 0; a < 3; a++)
        {
            int k = a;
            Number("Pivot scale " + axes[k], Tip("vfx.mesh.pivot", $"Pivot scale {axes[k]}"), () => Pivots().Select(p => (double)Get(p.Scale, k)),
                Set((p, v) => p with { Scale = With(p.Scale, k, v) }), 3, 0.05);
        }
    }

    private static float Get(System.Numerics.Vector3 v, int k) => k == 0 ? v.X : k == 1 ? v.Y : v.Z;
    private static System.Numerics.Vector3 With(System.Numerics.Vector3 v, int k, float x) => k == 0 ? v with { X = x } : k == 1 ? v with { Y = x } : v with { Z = x };

    /// <summary>Quaternion to Euler degrees (X = pitch, Y = yaw, Z = roll), the inverse of <see cref="FromEuler"/>.</summary>
    internal static System.Numerics.Vector3 ToEuler(System.Numerics.Quaternion q)
    {
        double sp = Math.Clamp(2 * (q.W * q.X - q.Y * q.Z), -1, 1);
        double pitch = Math.Asin(sp);
        double yaw = Math.Atan2(2 * (q.W * q.Y + q.X * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y));
        double roll = Math.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.X * q.X + q.Z * q.Z));
        const double d = 180 / Math.PI;
        return new((float)(pitch * d), (float)(yaw * d), (float)(roll * d));
    }

    internal static System.Numerics.Quaternion FromEuler(System.Numerics.Vector3 deg)
    {
        const float r = MathF.PI / 180;
        return System.Numerics.Quaternion.CreateFromYawPitchRoll(deg.Y * r, deg.X * r, deg.Z * r);
    }

    private void BuildParticles()
    {
        Header("Emission");
        Number("Start", Tip("vfx.particle.frames", "Effect frame (15 fps frames, not seconds) after which emission starts"), () => Vals<VfxParticleSystem, double>(p => p.StartTime), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { StartTime = (int)v })), 0, 1, 0, 100000, " frames");
        Number("Particle count", Tip("vfx.particle.count", "Maximum live particles"), () => Vals<VfxParticleSystem, double>(p => p.ParticleCount), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { ParticleCount = (int)v })), 0, 1, 0, 100000);
        Number("Lifetime", Tip("vfx.particle.lifetime", "Particle lifetime in ticks (4800 per second, 320 per frame)"), () => Vals<VfxParticleSystem, double>(p => p.Lifetime), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Lifetime = (int)v })), 0, 320, 0, 10000000, " ticks");
        Fact("Lifetime is", () => Vals<VfxParticleSystem, string>(p => $"{p.Lifetime / 4800.0:0.###} s = {p.Lifetime / 320.0:0.##} frames").FirstOrDefault() ?? "");
        Number("Lifetime variation", "Random variation of the lifetime", () => Vals<VfxParticleSystem, double>(p => p.LifetimeVariation), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { LifetimeVariation = F(v) })), 3, 0.05);
        Choice("Emitter", Tip("vfx.particle.emitter_type", "How the engine emits (emitter_type):\n0 = rectangle: births on the emitter's local XZ plane (width along X, height along Z), fired along local -Y; speed variation is box jitter.\n1 = sphere shell: births on a shell of radius = width, moving radially outward; the emitter orientation is ignored.\nThe old format docs call the sphere 2; the stock files use only 0 and 1."),
            () => ["0: Rectangle (fires -Y)", "1: Sphere shell (radial)"], () => Vals<VfxParticleSystem, string>(p => p.EmitterType == 0 ? "0: Rectangle (fires -Y)" : "1: Sphere shell (radial)"),
            Each<VfxParticleSystem, string>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { EmitterType = v.StartsWith('1') ? 1 : 0 })));
        Header("Particle flags");
        foreach (var (bit, name, tip) in ParticleFlagRows)
            Check(name, Tip("vfx.particle.flags", $"{tip} (flag 0x{bit:X})"), () => Vals<VfxParticleSystem, bool>(p => ((p.Flags ?? 0) & bit) != 0),
                Each<VfxParticleSystem, bool>((f, i, on) => bit == VfxParticleFlags.Drops ? VfxEdit.SetFlag(f, i, "Drops", on) : VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Flags = on ? (p.Flags ?? 0) | bit : (p.Flags ?? 0) & ~bit })));
        Number("Tail distance", "Drop tail length, in metres", () => Vals<VfxParticleSystem, double>(p => p.TailDistance ?? 0), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { TailDistance = F(v) })), 3, 0.1, suffix: " m");
        Number("Shrink at birth", "Size scale at birth", () => Vals<VfxParticleSystem, double>(p => p.Shrink?.X ?? 0), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Shrink = new(F(v), p.Shrink?.Y ?? 0) })), 3, 0.05);
        Number("Shrink at death", "Size scale at death", () => Vals<VfxParticleSystem, double>(p => p.Shrink?.Y ?? 0), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Shrink = new(p.Shrink?.X ?? 0, F(v)) })), 3, 0.05);
        Number("Fade at birth", "Opacity ramp at birth", () => Vals<VfxParticleSystem, double>(p => p.Fade?.X ?? 0), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Fade = new(F(v), p.Fade?.Y ?? 0) })), 3, 0.05);
        Number("Fade at death", "Opacity ramp at death", () => Vals<VfxParticleSystem, double>(p => p.Fade?.Y ?? 0), Each<VfxParticleSystem, double>((f, i, v) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Fade = new(p.Fade?.X ?? 0, F(v)) })), 3, 0.05);
        Choice("Material", Tip("vfx.particle.material", "Material of the particles"), () => VfxMaterialTab.Labels(File), () => Vals<VfxParticleSystem, string>(p => VfxMaterialTab.Label(File, p.MaterialIndex ?? -1)),
            Each<VfxParticleSystem, string>((f, i, v) => VfxEdit.SetParticleMaterial(f, i, VfxMaterialTab.Labels(f).ToList().IndexOf(v))));
        Header("Space warps");
        foreach (var w in File.Sections.OfType<VfxSpacewarp>().Select(w => w.Name).Distinct())
            Check(w, Tip("vfx.particle.warps", "This space warp acts on the particles"), () => Vals<VfxParticleSystem, bool>(p => p.Warps.Contains(w, StringComparer.OrdinalIgnoreCase)),
                Each<VfxParticleSystem, bool>((f, i, on) => VfxEdit.Update<VfxParticleSystem>(f, i, p => p with { Warps = on ? (p.Warps.Contains(w, StringComparer.OrdinalIgnoreCase) ? p.Warps : p.Warps.Add(w)) : p.Warps.RemoveAll(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase)) })));
        FrameValues<VfxParticleSystem>(p => p.Frames.Length, [VfxObjectValue.Width, VfxObjectValue.Height, VfxObjectValue.DropSize, VfxObjectValue.Speed, VfxObjectValue.SpeedVariation, VfxObjectValue.BirthRate],
            (p, fi, v) => { var fr = p.Frames[fi]; return v switch { VfxObjectValue.Width => fr.Width, VfxObjectValue.Height => fr.Height, VfxObjectValue.DropSize => fr.DropSize, VfxObjectValue.Speed => fr.Speed, VfxObjectValue.SpeedVariation => fr.SpeedVariation, _ => fr.BirthRate }; },
            (p, fi) => p.Frames[fi].Position);
    }

    private void BuildDummy() => FrameValues<VfxDummy>(d => d.Frames.Length, [], (_, _, _) => 0, (d, fi) => d.Frames.Length > 0 ? d.Frames[fi].Position : d.Position);

    private void BuildLight()
    {
        Check("On", Tip("vfx.light.params", "Light is on at the current frame"), () => Vals<VfxLight, bool>(l => LightAt(l).IsOn != 0), Each<VfxLight, bool>((f, i, on) => VfxEdit.SetLightOn(f, i, on, FrameOf(((VfxLight)f.Sections[i]).Frames.Length))));
        // colour as one row of three boxes; each box keeps its own name ("Red", "Green", "Blue")
        var rgb = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var (c, get) in new (string, Func<Vector3, float>)[] { ("Red", v => v.X), ("Green", v => v.Y), ("Blue", v => v.Z) })
        {
            var box = Number(c, Tip("vfx.light.params", $"Light colour, {c.ToLowerInvariant()} component (0-1) at the current frame"), () => Vals<VfxLight, double>(l => get(LightAt(l).Color)),
                Each<VfxLight, double>((f, i, v) => { var l = (VfxLight)f.Sections[i]; var col = LightAt(l).Color; col = c == "Red" ? col with { X = F(v) } : c == "Green" ? col with { Y = F(v) } : col with { Z = F(v) }; return VfxEdit.SetLightColor(f, i, col, FrameOf(l.Frames.Length)); }), 3, 0.05, 0, 1, addRow: false);
            box.Margin = new System.Windows.Thickness(c == "Red" ? 0 : 2, 0, 0, 0);
            rgb.Children.Add(box);
        }
        Row("Colour (R, G, B)", rgb, "Light colour at the current frame, 0-1 per component");
        FrameValues<VfxLight>(l => l.Frames.Length, [VfxObjectValue.Radius, VfxObjectValue.Multiplier], (l, fi, v) => v == VfxObjectValue.Radius ? l.Frames[fi].Radius : l.Frames[fi].Multiplier, (l, fi) => l.Frames.Length > 0 ? l.Frames[fi].Position : l.Initial.Position);
    }

    private VfxLightParams LightAt(VfxLight l) => l.Frames.Length > 0 ? l.Frames[FrameOf(l.Frames.Length)] : l.Initial;

    private void BuildWarp()
    {
        Number("Type", Tip("vfx.spacewarp.type", "Space warp type (engine id)"), () => Vals<VfxSpacewarp, double>(w => w.Type), Each<VfxSpacewarp, double>((f, i, v) => VfxEdit.Update<VfxSpacewarp>(f, i, w => w with { Type = (int)v })), 0, 1, 0, 100);
        FrameValues<VfxSpacewarp>(w => w.Frames.Length, [VfxObjectValue.Strength, VfxObjectValue.Decay, VfxObjectValue.Turbulence, VfxObjectValue.Frequency, VfxObjectValue.Scale],
            (w, fi, v) => { var fr = w.Frames[fi]; return v switch { VfxObjectValue.Strength => fr.Strength, VfxObjectValue.Decay => fr.Decay, VfxObjectValue.Turbulence => fr.Turbulence, VfxObjectValue.Frequency => fr.Frequency, _ => fr.Scale }; },
            (w, fi) => w.Frames[fi].Position);
    }

    /// <summary>Frame count, position and the given per-frame scalars at the playhead, plus "apply to all frames".</summary>
    private void FrameValues<T>(Func<T, int> count, VfxObjectValue[] fields, Func<T, int, VfxObjectValue, float> get, Func<T, int, Vector3> pos) where T : VfxSection
    {
        Header("Animation");
        Number("Frame count", "Number of per-frame records (lengthening holds the last frame)", () => Vals<T, double>(s => count(s)), Each<T, double>((f, i, v) => VfxEdit.ResizeObjectFrames(f, i, Math.Max(1, (int)v))), 0, 1, 1, 100000);
        Header("At the playhead");
        int Fi(T s) => FrameOf(count(s));
        foreach (var field in fields)
            Number(field switch { VfxObjectValue.SpeedVariation => "Speed variation", VfxObjectValue.BirthRate => "Birth rate", VfxObjectValue.DropSize => "Drop size", _ => field.ToString() },
                "Value at the current frame", () => Vals<T, double>(s => count(s) > 0 ? get(s, Fi(s), field) : 0), Each<T, double>((f, i, v) => VfxEdit.SetObjectValue(f, i, field, F(v), Fi((T)f.Sections[i]))), 3, 0.1,
                suffix: field switch { VfxObjectValue.Width or VfxObjectValue.Height or VfxObjectValue.DropSize or VfxObjectValue.Radius => " m", VfxObjectValue.Speed or VfxObjectValue.SpeedVariation => " m/s", _ => "" });
        foreach (var (axis, a) in new[] { ("X", 0), ("Y", 1), ("Z", 2) })
            Number("Position " + axis, "Position at the current frame", () => Vals<T, double>(s => count(s) > 0 || s is VfxDummy or VfxLight ? pos(s, Fi(s))[a] : 0),
                Each<T, double>((f, i, v) => { var s = (T)f.Sections[i]; var p = pos(s, Fi(s)); p[a] = F(v); return VfxEdit.SetObjectPosition(f, i, p, Fi(s)); }), 3, 0.1, suffix: " m");
        Buttons(("Apply to all frames", "Copy the values at the playhead to every frame", () => VfxEditing.ApplyEach(Doc, "Apply frame values to all frames", Sel<T>(), (f, i) =>
        {
            var s = (T)f.Sections[i]; int fi = Fi(s);
            foreach (var field in fields) f = VfxEdit.SetObjectValue(f, i, field, get(s, fi, field), null);
            return VfxEdit.SetObjectPosition(f, i, pos(s, fi), null);
        })));
    }
}
