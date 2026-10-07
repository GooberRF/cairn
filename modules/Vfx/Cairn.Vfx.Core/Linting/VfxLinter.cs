using System.Globalization;
using Cairn.Rfa.Linting;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Linting;

/// <summary>
/// Checks a VFX file against what the game accepts and what the stock effects do. Stored header
/// counts are not checked: they are recomputed on save.
/// </summary>
public static class VfxLinter
{
    private const string SceneRoot = "Scene Root";

    /// <summary>Lints <paramref name="file"/>. Never throws on malformed content.</summary>
    public static IReadOnlyList<VfxDiagnostic> Lint(VfxFile file, VfxLintContext? context = null)
    {
        context ??= VfxLintContext.Default;
        var list = new List<VfxDiagnostic>();
        var fn = context.FileName;

        void Add(string code, string message, VfxLocation location, params VfxQuickFix[] fixes)
        {
            var rule = VfxRules.Find(code)!;
            list.Add(new VfxDiagnostic(code, rule.Severity, message, rule.Help, location with { File = fn }, fixes));
        }

        string Hex(int v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);
        var versionLoc = VfxLocation.Header("vfx.header.version");
        if (file.Version < VfxVersion.Minimum || file.Version > VfxVersion.Current)
            Add(VfxRules.BadVersion, $"Version {Hex(file.Version)} is outside the range the game loads.", versionLoc);
        else if (VfxVersion.IsEngineFatal(file.Version))
            Add(VfxRules.EngineFatalVersion, $"Version {Hex(file.Version)} is refused by the game.", versionLoc);
        else if (file.Version < VfxVersion.Current)
            Add(VfxRules.OldVersion, $"Version {Hex(file.Version)} is an older version; convert it to 0x40006 to edit it.", versionLoc);

        var sections = file.Sections;
        var names = new HashSet<string>(sections.Select(NameOf).Where(n => n is not null)!, StringComparer.OrdinalIgnoreCase);
        var materialIndices = new List<int>();
        for (int i = 0; i < sections.Length; i++)
            if (sections[i] is VfxMaterial) materialIndices.Add(i);
        int materialCount = materialIndices.Count;
        bool hasMaterialSections = VfxVersion.HasMaterialSections(file.Version);
        var usedMaterials = new HashSet<int>();

        // Names
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sections.Length; i++)
        {
            if (NameOf(sections[i]) is not { } name) continue;
            if (string.IsNullOrWhiteSpace(name))
            {
                Add(VfxRules.SectionName, $"{Describe(sections[i], i)} has no name.", new VfxLocation(Section: i), UniqueNamesFix);
            }
            else if (!seen.Add(name))
            {
                Add(VfxRules.SectionName, $"The name '{name}' is used by more than one object ({Describe(sections[i], i)}).", new VfxLocation(Section: i), UniqueNamesFix);
            }
        }

        // Parents
        for (int i = 0; i < sections.Length; i++)
        {
            string? parent = sections[i] switch
            {
                VfxMesh m => m.Parent, VfxDummy d => d.Parent, VfxLight l => l.Parent,
                VfxParticleSystem p => p.Parent, VfxSpacewarp w => w.Parent, _ => null,
            };
            if (string.IsNullOrEmpty(parent) || parent.Equals(SceneRoot, StringComparison.OrdinalIgnoreCase) || names.Contains(parent)) continue;
            Add(VfxRules.ParentNotInFile, $"{Describe(sections[i], i)} has the parent '{parent}', which is not an object of this effect.", new VfxLocation(Section: i, Key: "vfx.object.parent"));
        }

        double longestMeshEnd = 0;
        for (int i = 0; i < sections.Length; i++)
        {
            var loc = new VfxLocation(Section: i);
            switch (sections[i])
            {
                case VfxMesh m:
                    LintMesh(file, m, i, materialCount, hasMaterialSections, usedMaterials, Add, ref longestMeshEnd);
                    break;
                case VfxMaterial mat:
                    if (mat.Type == 1) Add(VfxRules.UnexercisedFeature, $"Material section {i} blends two textures (vmix).", loc);
                    if (!AllFinite(mat.SelfIllumination) || (mat.Opacity is { } op && !AllFinite(op)) || (mat.Mix is { } mx && !AllFinite(mx)) || (mat.SpecularGlossReflection is { } sg && !Finite(sg)))
                        Add(VfxRules.NonFinite, $"Material section {i} has an invalid number.", loc);
                    LintTexture(mat.Texture0?.Name, loc, context, Add);
                    if (mat.Type == 1) LintTexture(mat.Texture1?.Name, loc, context, Add);
                    LintTexture(mat.ReflectionTexture, loc, context, Add);
                    break;
                case VfxParticleSystem p:
                    LintParticles(p, i, materialCount, hasMaterialSections, names, sections, usedMaterials, context, Add);
                    break;
                case VfxDummy d:
                    if (!Finite(d.Position) || !Finite(d.Orientation) || d.Frames.Any(f => !Finite(f.Position) || !Finite(f.Orientation)))
                        Add(VfxRules.NonFinite, $"Dummy '{d.Name}' has an invalid number.", loc);
                    break;
                case VfxLight l:
                    if (l.Frames.Prepend(l.Initial).Any(f => !Finite(f.Position) || !Finite(f.Color) || !float.IsFinite(f.Radius) || !float.IsFinite(f.Multiplier)))
                        Add(VfxRules.NonFinite, $"Light '{l.Name}' has an invalid number.", loc);
                    break;
                case VfxSpacewarp w:
                    if (w.Frames.Any(f => !Finite(f.Position) || !Finite(f.Orientation) || !AllFinite([f.Strength, f.Decay, f.Turbulence, f.Frequency, f.Scale])))
                        Add(VfxRules.NonFinite, $"Spacewarp '{w.Name}' has an invalid number.", loc);
                    break;
                case VfxOpaqueSection o:
                    string? what = o.Tag switch
                    {
                        VfxSectionTag.Chain => "a chain", VfxSectionTag.Camera => "a camera", VfxSectionTag.Selset => "a selection set",
                        VfxSectionTag.MaterialModifier => "a material modifier", _ => null,
                    };
                    if (what is not null) Add(VfxRules.UnexercisedFeature, $"Section {i} is {what}.", loc);
                    break;
            }
        }

        if (hasMaterialSections)
            for (int k = 0; k < materialCount; k++)
                if (!usedMaterials.Contains(k))
                {
                    int remove = k;
                    Add(VfxRules.UnusedMaterial, $"Material {k} is not used by any mesh or particle system.",
                        new VfxLocation(Section: materialIndices[k], Material: k),
                        new VfxQuickFix("Remove the material", f => RemoveMaterial(f, remove)));
                }

        // Header end frame vs the longest animated object (header frames are 1/15 s). Dummy and light frames are
        // one per header frame; particle-system frames start at StartTime (VfxSampler.SampleEmitter).
        double longestOtherEnd = 0;
        string? longestOther = null;
        foreach (var s in sections)
        {
            (int count, double end, string? who) = s switch
            {
                VfxParticleSystem p => (p.Frames.Length, p.StartTime + p.Frames.Length - 1.0, $"particle system '{p.Name}'"),
                VfxDummy d => (d.Frames.Length, d.Frames.Length - 1.0, $"dummy '{d.Name}'"),
                VfxLight l => (l.Frames.Length, l.Frames.Length - 1.0, $"light '{l.Name}'"),
                _ => (0, 0.0, null),
            };
            if (count > 1 && end > longestOtherEnd) { longestOtherEnd = end; longestOther = who; }
        }
        bool animatedMesh = sections.Any(s => s is VfxMesh m && m.Frames.Length > 1);
        if (animatedMesh || longestOther is not null)
        {
            double longestEnd = Math.Max(animatedMesh ? longestMeshEnd : 0, longestOtherEnd);
            string what = longestOther is not null && longestOtherEnd > (animatedMesh ? longestMeshEnd : 0) ? $"the {longestOther}" : "the longest mesh";
            int target = (int)Math.Ceiling(longestEnd - 1e-3);
            if (target > 0 && Math.Abs(file.EndFrame - longestEnd) > 1.0)
            {
                string how = file.EndFrame < longestEnd ? "shorter than" : "longer than";
                Add(VfxRules.EndFrameMismatch, $"The end frame ({file.EndFrame}) is {how} the longest animated object, {what}, which ends at frame {longestEnd:0.##}.",
                    VfxLocation.Header("vfx.header.end_frame"), new VfxQuickFix($"Set the end frame to {target}", f => f with { EndFrame = target }));
            }
        }
        return list;
    }

    private static void LintMesh(VfxFile file, VfxMesh m, int i, int materialCount, bool hasMaterialSections,
        HashSet<int> usedMaterials, Action<string, string, VfxLocation, VfxQuickFix[]> add, ref double longestEnd)
    {
        void Add(string code, string msg, VfxLocation loc) => add(code, msg, loc, []);
        string who = $"Mesh '{m.Name}'";
        var loc = new VfxLocation(Section: i);
        if (m.NumVertices <= 0 || m.Faces.IsEmpty) Add(VfxRules.EmptyMesh, $"{who} has no {(m.NumVertices <= 0 ? "vertices" : "faces")}.", loc);

        int slots = m.MaterialCount, fvCount = m.FaceVertices.Length;
        bool reportedFv = false, reportedV = false, reportedMat = false;
        for (int f = 0; f < m.Faces.Length; f++)
        {
            var face = m.Faces[f];
            var floc = loc with { Face = f };
            if (!reportedFv && (Out(face.FaceVertex0, fvCount) || Out(face.FaceVertex1, fvCount) || Out(face.FaceVertex2, fvCount)))
            {
                Add(VfxRules.MissingFaceVertex, $"{who}, face {f} uses a face-vertex record that does not exist (the mesh has {fvCount}).", floc);
                reportedFv = true;
            }
            if (!reportedV && (Out(face.V0, m.NumVertices) || Out(face.V1, m.NumVertices) || Out(face.V2, m.NumVertices)))
            {
                Add(VfxRules.VertexOutOfRange, $"{who}, face {f} uses a vertex index beyond the mesh's {m.NumVertices} vertices.", floc);
                reportedV = true;
            }
            // Stock helper meshes (collision spheres) have no slots and mark every face with -1; the game accepts that.
            // Before 0x40000 stock faces count inline materials from 1 (index == slot count occurs), so allow it there.
            int limit = VfxVersion.HasMaterialSections(file.Version) ? slots : slots + 1;
            if (!reportedMat && Out(face.MaterialIndex, limit) && !(slots == 0 && face.MaterialIndex == -1))
            {
                Add(VfxRules.FaceMaterialOutOfRange, $"{who}, face {f} uses material slot {face.MaterialIndex}, but the mesh has {slots}.", floc);
                reportedMat = true;
            }
            if (!Finite(face.Normal) || !Finite(face.Center) || !float.IsFinite(face.Radius))
                Add(VfxRules.NonFinite, $"{who}, face {f} has an invalid number.", floc);
        }
        for (int k = 0; k < fvCount && !reportedV; k++)
        {
            var fv = m.FaceVertices[k];
            if (Out(fv.VertexIndex, m.NumVertices) || fv.AdjacentFaces.Any(a => Out(a, m.Faces.Length)))
            {
                Add(VfxRules.VertexOutOfRange, $"{who}, face-vertex record {k} names a vertex or face that does not exist.", loc);
                reportedV = true;
            }
        }

        if (m.MaterialIndices is { } mi)
            for (int s = 0; s < mi.Length; s++)
            {
                if (hasMaterialSections && Out(mi[s], materialCount))
                    Add(VfxRules.MaterialSlotOutOfRange, $"{who}, slot {s} names material {mi[s]}, but the file has {materialCount}.", loc with { Material = s });
                else usedMaterials.Add(mi[s]);
            }
        if (m.InlineMaterials is { } inl)
            for (int s = 0; s < inl.Length; s++)
            {
                if (inl[s].Type == 1) Add(VfxRules.UnexercisedFeature, $"{who}, material {s} blends two textures (vmix).", loc with { Material = s });
            }

        // Finite numbers in frames, pivot and keys.
        bool bad = !Finite(m.BoundingCenter) || !float.IsFinite(m.BoundingRadius)
            || (m.LegacyPositions is { } lp && lp.Any(p => !Finite(p)))
            || (m.Pivot is { } pv && !Finite(pv))
            || (m.Keys is { } keys && (keys.Translation.Concat(keys.Scale).Any(k => !Finite(k.Value) || !Finite(k.InTangent) || !Finite(k.OutTangent))
                                       || keys.Rotation.Any(k => !Finite(k.Value) || !AllFinite([k.Tension, k.Continuity, k.Bias, k.EaseIn, k.EaseOut]))))
            || m.Frames.Any(fr => (fr.Positions is { } p && (!Finite(p.Center) || !Finite(p.Multiplier)))
                || (fr.FacingSize is { } fs && !(float.IsFinite(fs.X) && float.IsFinite(fs.Y)))
                || (fr.UpVector is { } up && !Finite(up))
                || (fr.Uvs is { } uv && uv.Any(u => !(float.IsFinite(u.X) && float.IsFinite(u.Y))))
                || (fr.Transform is { } t && !Finite(t))
                || (fr.Opacity is { } o && !float.IsFinite(o)));
        if (bad) Add(VfxRules.NonFinite, $"{who} has an invalid number in its positions, transforms or keys.", loc);

        // Zero-area faces, measured on the first frame.
        Vector3[]? pos = null;
        try { pos = m.LegacyPositions is { } legacy ? [.. legacy] : (m.Frames.Length > 0 ? m.DecodePositions(0) : null); }
        catch (Exception) { pos = null; }
        if (pos is not null && !reportedV)
        {
            int zero = 0, first = -1;
            for (int f = 0; f < m.Faces.Length; f++)
            {
                var face = m.Faces[f];
                if (Out(face.V0, pos.Length) || Out(face.V1, pos.Length) || Out(face.V2, pos.Length)) continue;
                var c = Vector3.Cross(pos[face.V1] - pos[face.V0], pos[face.V2] - pos[face.V0]);
                if (c.LengthSquared() <= 1e-14f) { zero++; if (first < 0) first = f; }
            }
            if (zero > 0) Add(VfxRules.ZeroAreaFace, $"{who} has {zero} zero-area face{(zero == 1 ? "" : "s")} (first: face {first}).", loc with { Face = first });
        }

        // Timing: frame count against start/end/fps, and the window in header frames (1/15 s).
        int n = m.Frames.Length;
        double? start = null, end = null;
        if (m.StartTime is { } st && m.EndTime is { } et && m.Fps is { } fps && fps > 0)
        {
            int expect = (int)Math.Floor((et - st) * fps + 1e-3) + 1;
            if (expect != n) Add(VfxRules.FrameCountMismatch, $"{who} has {n} frames, but its range {st:0.###}-{et:0.###} s at {fps} fps holds {expect}.", loc);
            start = st * 15; end = et * 15;
        }
        else if (m.StartFrame is { } sf && m.EndFrame is { } ef && m.Fps is { } fps2 && fps2 > 0)
        {
            int expect = VfxVersion.HasInclusiveFrameRange(file.Version) ? ef - sf + 1 : ef - sf;
            if (expect != n) Add(VfxRules.FrameCountMismatch, $"{who} has {n} frames, but its range {sf}-{ef} holds {expect}.", loc);
            start = sf * 15.0 / fps2; end = ef * 15.0 / fps2;
        }
        if (start is { } s0 && end is { } e0)
        {
            if (n > 1 && s0 >= file.EndFrame && file.EndFrame > 0)
                Add(VfxRules.MeshOutsideEndFrame, $"{who} starts at frame {s0:0.##}, at or after the effect's end frame {file.EndFrame}.", loc);
            longestEnd = Math.Max(longestEnd, e0);
        }
    }

    private static void LintParticles(VfxParticleSystem p, int i, int materialCount, bool hasMaterialSections,
        HashSet<string> names, ImmutableArray<VfxSection> sections, HashSet<int> usedMaterials, VfxLintContext context,
        Action<string, string, VfxLocation, VfxQuickFix[]> add)
    {
        void Add(string code, string msg, VfxLocation loc) => add(code, msg, loc, []);
        string who = $"Particle system '{p.Name}'";
        var loc = new VfxLocation(Section: i);
        if (p.MaterialIndex is { } mi && hasMaterialSections)
        {
            if (Out(mi, materialCount)) Add(VfxRules.ParticleMaterialOutOfRange, $"{who} uses material {mi}, but the file has {materialCount}.", loc with { Material = mi });
            else usedMaterials.Add(mi);
        }
        if (p.InlineMaterial is { } im)
        {
            if (im.Type == 1) Add(VfxRules.UnexercisedFeature, $"{who} blends two textures (vmix).", loc);
            LintTexture(im.Texture0, loc, context, add);
            if (im.Type == 1) LintTexture(im.Texture1, loc, context, add);
        }
        var warps = new HashSet<string>(sections.OfType<VfxSpacewarp>().Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var w in p.Warps)
            if (!string.IsNullOrEmpty(w) && !warps.Contains(w))
                Add(VfxRules.MissingSpacewarp, $"{who} names the spacewarp '{w}', which is not in the file.", loc with { Key = w });
        if (p.ParticleCount < 2 || p.ParticleCount > 100)
            Add(VfxRules.ParticleRange, $"{who} has {p.ParticleCount} particles (stock effects use 2-100).", loc with { Key = "vfx.particle.count" });
        if (p.Lifetime < 1600 || p.Lifetime > 32000)
            Add(VfxRules.ParticleRange, $"{who} has a lifetime of {p.Lifetime} ticks (stock effects use 1600-32000).", loc with { Key = "vfx.particle.lifetime" });
        if (p.EmitterType is not (0 or 1))
            Add(VfxRules.ParticleRange, $"{who} has emitter type {p.EmitterType} (stock effects use 0 and 1).", loc with { Key = "vfx.particle.emitter_type" });
        if (!float.IsFinite(p.LifetimeVariation) || (p.Shrink is { } sh && !(float.IsFinite(sh.X) && float.IsFinite(sh.Y)))
            || (p.Fade is { } fd && !(float.IsFinite(fd.X) && float.IsFinite(fd.Y))) || (p.TailDistance is { } td && !float.IsFinite(td))
            || p.Frames.Any(f => !Finite(f.Position) || !Finite(f.Orientation)
                || !AllFinite([f.Width, f.Height, f.DropSize, f.Speed, f.SpeedVariation, f.BirthRate, f.Opacity ?? 0])))
            Add(VfxRules.NonFinite, $"{who} has an invalid number.", loc);
    }

    private static void LintTexture(string? name, VfxLocation loc, VfxLintContext context, Action<string, string, VfxLocation, VfxQuickFix[]> add)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var at = loc with { Key = name };
        if (name.StartsWith("$original_map", StringComparison.OrdinalIgnoreCase))
        {
            add(VfxRules.PlaceholderTexture, $"'{name}' is filled in at run time with the host object's texture.", at, []);
            return;
        }
        string ext = System.IO.Path.GetExtension(name);
        if (!ext.Equals(".tga", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".vbm", StringComparison.OrdinalIgnoreCase))
            add(VfxRules.TextureExtension, $"The texture '{name}' is not a .tga or .vbm file.", at, []);
        if (context.Resolver is { } r)
        {
            bool found;
            try { found = r.Resolve(name) is not null; } catch (Exception) { found = false; }
            if (!found) add(VfxRules.TextureNotFound, $"The texture '{name}' was not found.", at, []);
        }
    }

    // ── Quick-fix edits ─────────────────────────────────────────────────────

    /// <summary>
    /// Renames empty and duplicate object names to unique ones (later duplicates get a suffix). Parent and spacewarp
    /// references are kept consistent: the first object with a name keeps it, so references that resolved to it (the
    /// engine looks names up case-insensitively, first match) still do (assuming the engine's lookup takes the first case-insensitive match, unverified); an empty name is never a reference target
    /// (an empty parent means none), so renaming it does not touch references.
    /// </summary>
    public static VfxFile MakeNamesUnique(VfxFile file)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var b = file.Sections.ToBuilder();
        for (int i = 0; i < b.Count; i++)
        {
            if (NameOf(b[i]) is not { } name) continue;
            string baseName = string.IsNullOrWhiteSpace(name) ? KindOf(b[i]) : name;
            string candidate = baseName;
            // A new name must not clash with an earlier name or with any original name still to come.
            for (int k = 2; used.Contains(candidate) || (candidate != name && Taken(file, candidate, i)); k++)
                candidate = $"{baseName}_{k}";
            used.Add(candidate);
            if (candidate != name) b[i] = WithName(b[i], candidate);
        }
        return file with { Sections = b.ToImmutable() };
    }

    private static readonly VfxQuickFix UniqueNamesFix = new("Make the names unique", MakeNamesUnique);

    private static bool Taken(VfxFile file, string name, int except) =>
        file.Sections.Where((s, j) => j > except).Any(s => string.Equals(NameOf(s), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes material section <paramref name="material"/> (index among material sections) and renumbers references.</summary>
    public static VfxFile RemoveMaterial(VfxFile file, int material)
    {
        var b = ImmutableArray.CreateBuilder<VfxSection>();
        int k = 0;
        int Fix(int v) => v > material ? v - 1 : v;
        foreach (var s in file.Sections)
        {
            switch (s)
            {
                case VfxMaterial:
                    if (k++ != material) b.Add(s);
                    break;
                case VfxMesh m when m.MaterialIndices is { } mi:
                    b.Add(m with { MaterialIndices = [.. mi.Select(Fix)] });
                    break;
                case VfxParticleSystem p when p.MaterialIndex is { } pm:
                    b.Add(p with { MaterialIndex = Fix(pm) });
                    break;
                default:
                    b.Add(s);
                    break;
            }
        }
        return file with { Sections = b.ToImmutable() };
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static bool Out(int index, int count) => index < 0 || index >= count;
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Quaternion q) => float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);
    private static bool Finite(VfxTransform t) => Finite(t.Translation) && Finite(t.Rotation) && Finite(t.Scale);
    private static bool AllFinite(IEnumerable<float> values) => values.All(float.IsFinite);

    internal static string? NameOf(VfxSection s) => s switch
    {
        VfxMesh m => m.Name, VfxDummy d => d.Name, VfxLight l => l.Name, VfxParticleSystem p => p.Name, VfxSpacewarp w => w.Name, _ => null,
    };

    private static VfxSection WithName(VfxSection s, string name) => s switch
    {
        VfxMesh m => m with { Name = name }, VfxDummy d => d with { Name = name }, VfxLight l => l with { Name = name },
        VfxParticleSystem p => p with { Name = name }, VfxSpacewarp w => w with { Name = name }, _ => s,
    };

    private static string KindOf(VfxSection s) => s switch
    {
        VfxMesh => "Mesh", VfxDummy => "Dummy", VfxLight => "Light", VfxParticleSystem => "Particles", VfxSpacewarp => "Spacewarp", _ => "Object",
    };

    private static string Describe(VfxSection s, int i) => NameOf(s) is { Length: > 0 } n ? $"{KindOf(s)} '{n}'" : $"{KindOf(s)} (section {i})";
}
