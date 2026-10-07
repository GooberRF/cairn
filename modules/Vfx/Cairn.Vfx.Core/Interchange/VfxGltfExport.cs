using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Interchange;

public sealed record VfxGltfExportOptions
{
    /// <summary>Root node name; defaults to "vfx".</summary>
    public string? SourceName { get; init; }
}

/// <summary>
/// VFX -> glTF in REDUX's layout. Every node also carries Cairn's lossless
/// <c>cairn_record</c>; REDUX ignores it, Cairn's importer prefers it while the glTF view still agrees.
/// </summary>
public static class VfxGltfExport
{
    public static GltfDocument Export(VfxFile file, VfxGltfExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        // REDUX exports from its always-current model; rf_version keeps the version that was read.
        var original = file;
        if (file.Version < VfxVersion.Current) file = VfxUpgrade.ToCurrent(file);
        string name = options?.SourceName ?? "vfx";
        var doc = new GltfDocument { Asset = { Generator = "Cairn VfxGltfExport" } };
        var buf = new GltfBufferBuilder(doc);
        var anim = new GltfAnimation { Name = name + "_vfx" };
        var materials = file.Sections.OfType<VfxMaterial>().ToList();
        var table = new JsonArray();
        for (int i = 0; i < materials.Count; i++)
        {
            var extras = MaterialExtras(materials[i], i);
            table.Add(extras.DeepClone());
            doc.Materials.Add(Material(doc, materials[i], i, extras));
        }
        var order = new JsonArray();
        var root = new GltfNode { Name = name, Translation = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One };
        doc.Nodes.Add(root);
        var pending = new List<Pending>();
        int ordinal = 0;
        foreach (var s in file.Sections)
        {
            ordinal++;
            string tag = TagText(s.Tag);
            string sname = SectionName(s) ?? $"rf_{(s.Tag == VfxSectionTag.MaterialModifier ? "mmod" : tag)}_{ordinal}";
            order.Add(new JsonObject { ["type"] = tag, ["name"] = s is VfxMaterial or VfxOpaqueSection ? "" : sname });
            if (s is VfxMaterial) continue;
            var node = new GltfNode { Name = sname };
            var p = new Pending(node, doc.Nodes.Count, sname, s switch
            {
                VfxMesh m => m.Parent, VfxDummy d => d.Parent, VfxParticleSystem ps => ps.Parent, VfxLight l => l.Parent, VfxSpacewarp w => w.Parent, _ => null,
            });
            var extras = s switch
            {
                VfxMesh m => MeshNode(doc, buf, anim, p, m, materials),
                VfxDummy d => Samples(p, DummyExtras(d), d.Frames.Select(f => new VfxTransform(f.Position, f.Orientation, Vector3.One)).ToArray(), 0,
                    new VfxTransform(d.Position, d.Orientation, Vector3.One)),
                VfxParticleSystem ps => Samples(p, ParticleExtras(ps),
                    ps.Frames.Select(f => new VfxTransform(f.Position, f.Orientation, Vector3.One)).ToArray(), ps.StartTime, null),
                VfxLight l => Samples(p, LightExtras(l), l.Frames.Select(f => new VfxTransform(f.Position, Quaternion.Identity, Vector3.One)).ToArray(), 0,
                    new VfxTransform(l.Initial.Position, Quaternion.Identity, Vector3.One)),
                VfxSpacewarp w => Samples(p, WarpExtras(w), w.Frames.Select(f => new VfxTransform(f.Position, f.Orientation, Vector3.One)).ToArray(), 0, null),
                VfxOpaqueSection { Tag: VfxSectionTag.MaterialModifier, Body.Length: 4 } o =>
                    new JsonObject { ["rf_type"] = "vfx_material_modifier", ["rf_material_index"] = BitConverter.ToInt32(o.Body.AsSpan()) },
                // Chains, cameras, selsets and unknown tags travel in REDUX's raw form, which its importer writes back verbatim.
                VfxOpaqueSection o => new JsonObject { ["rf_type"] = "vfx_unknown", ["rf_section_type"] = (int)o.Tag, ["rf_section_tag"] = tag,
                    ["rf_raw_base64"] = Convert.ToBase64String(o.Body.AsSpan()) },
                _ => new JsonObject(),
            };
            extras[VfxGltfRecords.Key] = VfxGltfRecords.ToNode(s);
            node.Extras = extras;
            doc.Nodes.Add(node);
            pending.Add(p);
        }
        AssignHierarchy(pending, doc);
        foreach (var p in pending) Animate(buf, anim, p);
        root.Extras = new JsonObject
        {
            ["rf_type"] = "vfx", ["rf_version"] = original.Version, ["rf_header_flags"] = original.HeaderFlags ?? 0,
            ["rf_end_frame"] = original.EndFrame, ["rf_selset_object_count"] = original.SelsetObjectCount ?? 0,
            ["rf_section_order"] = order, ["rf_material_table"] = table,
            ["cairn_file"] = new JsonObject
            {
                ["version"] = file.Version, ["header_flags"] = file.HeaderFlags, ["legacy_unk1"] = file.LegacyUnk1,
                ["selset_object_count"] = file.SelsetObjectCount, ["camera_frame_count"] = file.CameraFrameCount,
            },
        };
        doc.Scenes.Add(new GltfScene { Nodes = { 0 } });
        doc.Scene = 0;
        if (anim.Channels.Count > 0) doc.Animations.Add(anim);
        buf.Finish(name + ".bin");
        return doc;
    }

    /// <summary>Writes <c>.gltf</c> + <c>&lt;stem&gt;.bin</c> (REDUX's layout), or GLB when <paramref name="glb"/>.</summary>
    public static void Save(VfxFile file, string path, bool glb = false, VfxGltfExportOptions? options = null)
    {
        var doc = Export(file, options ?? new VfxGltfExportOptions { SourceName = Path.GetFileNameWithoutExtension(path) });
        if (glb) GltfWriter.WriteGlb(doc, path);
        else GltfWriter.WriteGltf(doc, path, new GltfWriteOptions { Indented = false });
    }

    internal static string TagText(uint tag) =>
        tag switch
        {
            VfxSectionTag.Mesh => "SFXO", VfxSectionTag.Material => "MATL", VfxSectionTag.ParticleSystem => "PART",
            VfxSectionTag.Dummy => "DMMY", VfxSectionTag.Light => "ALGT", VfxSectionTag.Spacewarp => "WARP",
            VfxSectionTag.Chain => "CHNE", VfxSectionTag.Camera => "CMRA", VfxSectionTag.MaterialModifier => "MMOD",
            _ => Encoding.ASCII.GetString(BitConverter.GetBytes(tag)).Replace('\0', '_'),
        };

    private static string? SectionName(VfxSection s) => s switch
    {
        VfxMesh m => m.Name, VfxDummy d => d.Name, VfxParticleSystem p => p.Name, VfxLight l => l.Name, VfxSpacewarp w => w.Name, _ => null,
    };

    private static JsonObject Obj(string type, string name, string parent, byte? saveParent, params (string Key, object? Value)[] more)
    {
        var o = new JsonObject { ["rf_type"] = type, ["rf_name"] = name, ["rf_parent_name"] = parent };
        if (saveParent is { } sp) o["rf_save_parent"] = sp != 0;
        foreach (var (k, v) in more) o[k] = v is null ? null : JsonValue.Create(v);
        return o;
    }

    private static JsonArray Arr<T>(IEnumerable<T> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonArray V3(Vector3 v) => Arr(new[] { v.X, v.Y, v.Z });

    /// <summary>REDUX's slot identity: the tex0 name, else <c>color_r_g_b</c> for color-only, else empty.</summary>
    private static string MaterialIdentity(VfxMaterial m) =>
        m.Texture0?.Name is { } n && !string.IsNullOrWhiteSpace(n) ? n : m.Type == 2 && m.SolidColor is { } c ? $"color_{c.R}_{c.G}_{c.B}" : "";

    private static JsonObject MaterialExtras(VfxMaterial m, int i)
    {
        JsonObject? Tex(VfxTexture? t) => t is null ? null : new JsonObject
        { ["name"] = t.Name, ["start_frame"] = t.StartFrame ?? 0, ["playback_rate"] = t.PlaybackRate ?? 0, ["anim_type"] = t.AnimType ?? 0 };
        var sgr = m.SpecularGlossReflection ?? Vector3.Zero;
        var o = new JsonObject
        {
            ["rf_type"] = "vfx_material", ["rf_material_index"] = i,
            ["rf_mat_type"] = m.Type switch { 0 => "image", 1 => "vmix", 2 => "color_only", _ => m.Type.ToString() },
            ["rf_mat_type_id"] = m.Type, ["rf_additive"] = (m.Additive ?? 0) != 0, ["rf_fps"] = m.Fps ?? m.LegacyFps ?? 15,
            ["rf_mix_frames"] = Arr(m.Mix ?? []), ["rf_specular_level"] = sgr.X, ["rf_glossiness"] = sgr.Y, ["rf_reflection_amount"] = sgr.Z,
            ["rf_refl_tex_name"] = m.ReflectionTexture ?? "",
            ["rf_solid_color"] = Arr(m.SolidColor is { } c ? new[] { c.R, c.G, c.B } : new[] { 255, 255, 255 }),
            ["rf_self_illumination"] = Arr(m.SelfIllumination), ["rf_opacity"] = Arr(m.Opacity ?? []),
        };
        if (Tex(m.Texture0) is { } t0) o["tex_0"] = t0;
        if (Tex(m.Texture1) is { } t1) o["tex_1"] = t1;
        o[VfxGltfRecords.Key] = VfxGltfRecords.ToNode(m);
        return o;
    }

    private static GltfMaterial Material(GltfDocument doc, VfxMaterial m, int i, JsonObject extras)
    {
        float opMax = m.Opacity is { Length: > 0 } op ? op.Max() : 1, opMin = m.Opacity is { Length: > 0 } op2 ? op2.Min() : 1;
        var rgb = m.Type == 2 && m.SolidColor is { } c ? new Vector3(c.R, c.G, c.B) / 255f : Vector3.One;
        var mat = new GltfMaterial
        {
            Name = $"{i:D3}_{(m.Type == 2 ? "color_only" : m.Texture0?.Name is { Length: > 0 } n ? n : "material")}",
            DoubleSided = true, Extras = extras,
            PbrMetallicRoughness = new GltfPbrMetallicRoughness
            { BaseColorFactor = [rgb.X, rgb.Y, rgb.Z, Math.Clamp(opMax, 0, 1)], MetallicFactor = 0, RoughnessFactor = 1 },
        };
        if (opMin < 1 || (m.Additive ?? 0) != 0) mat.AlphaMode = "BLEND";
        float s = m.SelfIllumination.Length > 0 ? Math.Clamp(m.SelfIllumination.Max(), 0, 1) : 0;
        if (s > 0) mat.EmissiveFactor = [s, s, s];
        if (m.Texture0?.Name is { Length: > 0 } tex && !tex.StartsWith('$'))
        {
            if (doc.Samplers.Count == 0) doc.Samplers.Add(new GltfSampler { MagFilter = 9729, MinFilter = 9729, WrapS = 10497, WrapT = 10497 });
            doc.Images.Add(new GltfImage { Uri = tex.Replace('\\', '/'), Name = Path.GetFileNameWithoutExtension(tex) });
            doc.Textures.Add(new GltfTexture { Sampler = 0, Source = doc.Images.Count - 1 });
            mat.PbrMetallicRoughness.BaseColorTexture = new GltfTextureInfo { Index = doc.Textures.Count - 1 };
        }
        return mat;
    }

    /// <summary>A section node awaiting REDUX's hierarchy pass; world and samples are glTF-space TRS.</summary>
    private sealed class Pending(GltfNode node, int index, string name, string? parent)
    {
        public GltfNode Node { get; } = node;
        public int Index { get; } = index;
        public string Name { get; } = name;
        public string? Parent { get; } = parent;
        public VfxTransform World { get; set; } = VfxTrs.Identity;
        public VfxTransform ParentWorld { get; set; } = VfxTrs.Identity;
        public VfxTransform[] Samples { get; set; } = [];
        public float[] Times { get; set; } = [];
        public bool Morph { get; set; }

        public bool IsAnimated => Morph || (Samples.Length >= 2 && Samples.Any(t => Changes(t, Samples[0])));
    }

    private static bool Changes(VfxTransform t, VfxTransform first) =>
        Vector3.DistanceSquared(t.Translation, first.Translation) > 1e-12f || Math.Abs(Quaternion.Dot(t.Rotation, first.Rotation)) < 1 - 1e-7f
        || Vector3.DistanceSquared(t.Scale, first.Scale) > 1e-12f;

    /// <summary>Records RF-space samples (and the rest pose when there are none) as the node's world track.</summary>
    private static JsonObject Samples(Pending p, JsonObject extras, VfxTransform[] rfSamples, float startFrame, VfxTransform? rest, float[]? times = null)
    {
        p.Samples = rfSamples.Select(VfxGltfSpace.Transform).ToArray();
        p.Times = times ?? Enumerable.Range(0, p.Samples.Length).Select(i => (startFrame + i) / VfxTrs.DefaultFps).ToArray();
        if (p.Samples.Length > 0) p.World = p.Samples[0];
        else if (rest is not null) p.World = VfxGltfSpace.Transform(rest);
        return extras;
    }

    /// <summary>
    /// REDUX's rule: a node goes under its authored parent only when that parent exists (first name wins), is static, is
    /// not itself and creates no cycle; its TRS and samples are then relative to the parent's world. Otherwise under node 0.
    /// </summary>
    private static void AssignHierarchy(List<Pending> pending, GltfDocument doc)
    {
        var byName = new Dictionary<string, Pending>(StringComparer.Ordinal);
        foreach (var p in pending) if (p.Name.Length > 0) byName.TryAdd(p.Name, p);
        foreach (var p in pending)
        {
            string parentName = p.Parent ?? "Scene Root";
            Pending? parent = parentName != "Scene Root" && byName.TryGetValue(parentName, out var c) && c != p && !c.IsAnimated
                && !CreatesCycle(c, p, byName) ? c : null;
            p.ParentWorld = parent?.World ?? VfxTrs.Identity;
            var local = parent is null ? p.World : Relative(p.World, parent.World);
            doc.Nodes[parent?.Index ?? 0].Children.Add(p.Index);
            p.Node.Translation = local.Translation; p.Node.Rotation = local.Rotation; p.Node.Scale = local.Scale;
        }
    }

    private static bool CreatesCycle(Pending candidate, Pending child, Dictionary<string, Pending> byName)
    {
        Pending? cur = candidate;
        for (int guard = 0; cur is not null && guard < 64; guard++)
        {
            if (cur == child) return true;
            string p = cur.Parent ?? "Scene Root";
            if (p == "Scene Root") return false;
            byName.TryGetValue(p, out cur);
        }
        return false;
    }

    private static bool IsIdentity(VfxTransform t) =>
        t.Translation.LengthSquared() < 1e-16f && Math.Abs(t.Rotation.W - 1) < 1e-7f
        && new Vector3(t.Rotation.X, t.Rotation.Y, t.Rotation.Z).LengthSquared() < 1e-14f && (t.Scale - Vector3.One).LengthSquared() < 1e-14f;

    /// <summary>REDUX's <c>Relative</c>: <paramref name="world"/> expressed in <paramref name="parent"/>'s TRS frame.</summary>
    private static VfxTransform Relative(VfxTransform world, VfxTransform parent)
    {
        if (IsIdentity(parent)) return world;
        var invR = Quaternion.Conjugate(Quaternion.Normalize(parent.Rotation));
        static float Inv(float s) => Math.Abs(s) > 1e-9f ? 1 / s : 1;
        var invS = new Vector3(Inv(parent.Scale.X), Inv(parent.Scale.Y), Inv(parent.Scale.Z));
        var r = Quaternion.Normalize(Quaternion.Multiply(invR, world.Rotation));
        return new VfxTransform(Vector3.Transform(world.Translation - parent.Translation, invR) * invS,
            r.LengthSquared() < 1e-12f ? Quaternion.Identity : r, world.Scale * invS);
    }

    /// <summary>LINEAR channels (relative to the static parent) for the components that change.</summary>
    private static void Animate(GltfBufferBuilder buf, GltfAnimation anim, Pending p)
    {
        if (p.Samples.Length < 2) return;
        var g = p.Samples.Select(s => Relative(s, p.ParentWorld)).ToArray();
        var times = p.Times;
        int nodeIndex = p.Index;
        int? input = null;
        void Channel(string path, bool changes, Func<GltfBufferBuilder, int> output)
        {
            if (!changes) return;
            input ??= buf.AddScalars(times);
            anim.Samplers.Add(new GltfAnimationSampler { Input = input.Value, Output = output(buf), Interpolation = GltfInterpolation.Linear });
            anim.Channels.Add(new GltfAnimationChannel { Sampler = anim.Samplers.Count - 1, Target = { Node = nodeIndex, Path = path } });
        }
        Channel("translation", g.Any(t => Vector3.DistanceSquared(t.Translation, g[0].Translation) > 1e-12f),
            b => b.AddVector3(g.Select(t => t.Translation).ToArray(), null));
        Channel("rotation", g.Any(t => Math.Abs(Quaternion.Dot(t.Rotation, g[0].Rotation)) < 1 - 1e-7f),
            b => b.AddQuaternions(g.Select(t => t.Rotation).ToArray()));
        Channel("scale", g.Any(t => Vector3.DistanceSquared(t.Scale, g[0].Scale) > 1e-12f),
            b => b.AddVector3(g.Select(t => t.Scale).ToArray(), null));
    }

    private static JsonArray Q4(Quaternion q) => Arr(new[] { q.X, q.Y, q.Z, q.W });

    private static JsonArray Flat<T>(IEnumerable<T> frames, Func<T, float[]> f) => Arr(frames.SelectMany(f));

    private static JsonObject DummyExtras(VfxDummy d)
    {
        var o = Obj("vfx_dummy", d.Name, d.Parent, d.SaveParent);
        o["rf_pos"] = V3(d.Position); o["rf_orient"] = Q4(d.Orientation);
        o["rf_frames"] = new JsonArray(d.Frames.Select(f => (JsonNode)new JsonObject { ["pos"] = V3(f.Position), ["orient"] = Q4(f.Orientation) }).ToArray());
        return o;
    }

    private static JsonObject ParticleExtras(VfxParticleSystem p)
    {
        uint flags = p.Flags ?? 0;
        var o = Obj("vfx_particle_system", p.Name, p.Parent, p.SaveParent, ("rf_flags", (long)flags),
            ("rf_flag_apply_gravity", (flags & 0x2) != 0), ("rf_flag_randomize_orientation", (flags & 0x10) != 0),
            ("rf_flag_no_cull", (flags & 0x20) != 0), ("rf_flag_drops", (flags & 0x100) != 0));
        o["rf_warps"] = Arr(p.Warps);
        foreach (var (k, v) in new (string, object)[]
        {
            ("rf_start_time", p.StartTime), ("rf_num_frames", p.Frames.Length), ("rf_material_index", p.MaterialIndex ?? -1),
            ("rf_particle_count", p.ParticleCount), ("rf_start", p.Start), ("rf_lifetime", p.Lifetime), ("rf_lifetime_variation", p.LifetimeVariation),
            ("rf_emitter_type", p.EmitterType), ("rf_shrink_at_birth", p.Shrink?.X ?? 0f), ("rf_shrink_at_death", p.Shrink?.Y ?? 0f),
            ("rf_fade_at_birth", p.Fade?.X ?? 0f), ("rf_fade_at_death", p.Fade?.Y ?? 0f),
        })
            o[k] = JsonValue.Create(v);
        var fr = p.Frames;
        o["rf_frame_pos"] = Flat(fr, f => [f.Position.X, f.Position.Y, f.Position.Z]);
        o["rf_frame_orient"] = Flat(fr, f => [f.Orientation.X, f.Orientation.Y, f.Orientation.Z, f.Orientation.W]);
        o["rf_frame_width"] = Arr(fr.Select(f => f.Width)); o["rf_frame_height"] = Arr(fr.Select(f => f.Height));
        o["rf_frame_drop_size"] = Arr(fr.Select(f => f.DropSize)); o["rf_frame_speed"] = Arr(fr.Select(f => f.Speed));
        o["rf_frame_speed_variation"] = Arr(fr.Select(f => f.SpeedVariation)); o["rf_frame_birth_rate"] = Arr(fr.Select(f => f.BirthRate));
        if (p.TailDistance is { } tail) o["rf_tail_distance"] = tail;
        if (fr.Length > 0 && fr[0].Opacity is not null) o["rf_frame_opacity"] = Arr(fr.Select(f => f.Opacity ?? 0));
        return o;
    }

    private static JsonObject LightExtras(VfxLight l)
    {
        static JsonObject Params(VfxLightParams p) => new()
        { ["pos"] = V3(p.Position), ["radius"] = p.Radius, ["multiplier"] = p.Multiplier, ["color"] = V3(p.Color), ["is_on"] = p.IsOn != 0 };
        var o = Obj("vfx_light", l.Name, l.Parent, l.SaveParent);
        o["rf_params"] = Params(l.Initial);
        o["rf_frames"] = new JsonArray(l.Frames.Select(f => (JsonNode)Params(f)).ToArray());
        return o;
    }

    private static JsonObject WarpExtras(VfxSpacewarp w)
    {
        var o = Obj("vfx_spacewarp", w.Name, w.Parent, null, ("rf_warp_type", w.Type));
        var fr = w.Frames;
        o["rf_frame_pos"] = Flat(fr, f => [f.Position.X, f.Position.Y, f.Position.Z]);
        o["rf_frame_orient"] = Flat(fr, f => [f.Orientation.X, f.Orientation.Y, f.Orientation.Z, f.Orientation.W]);
        o["rf_frame_strength"] = Arr(fr.Select(f => f.Strength)); o["rf_frame_decay"] = Arr(fr.Select(f => f.Decay));
        o["rf_frame_turbulence"] = Arr(fr.Select(f => f.Turbulence)); o["rf_frame_frequency"] = Arr(fr.Select(f => f.Frequency));
        o["rf_frame_scale"] = Arr(fr.Select(f => f.Scale));
        return o;
    }

    private static JsonObject MeshNode(GltfDocument doc, GltfBufferBuilder buf, GltfAnimation anim, Pending p, VfxMesh m, List<VfxMaterial> table)
    {
        var node = p.Node;
        int nodeIndex = p.Index, tableSize = table.Count;
        var extras = Obj("vfx_mesh", m.Name, m.Parent, m.SaveParent, ("rf_flags", (long)m.Flags), ("rf_fps", m.Fps ?? 15),
            ("rf_start_time", m.StartTime ?? 0), ("rf_end_time", m.EndTime ?? 0), ("rf_num_frames", m.Frames.Length),
            ("rf_vertex_count", m.NumVertices), ("rf_is_keyframed", (m.IsKeyframed ?? 0) != 0),
            ("rf_bounding_radius", m.BoundingRadius));
        string[] flagNames = ["facing", "no_interp", "morph", "fire", "fullbright", "seethrough", "corona", "sky", "dump_uvs", "facing_rod"];
        uint[] flagBits = [VfxMeshFlags.Facing, VfxMeshFlags.NoInterp, VfxMeshFlags.Morph, VfxMeshFlags.Fire, VfxMeshFlags.Fullbright,
            VfxMeshFlags.SeeThrough, VfxMeshFlags.Corona, VfxMeshFlags.Sky, VfxMeshFlags.DumpUvs, VfxMeshFlags.FacingRod];
        for (int i = 0; i < flagNames.Length; i++) extras["rf_flag_" + flagNames[i]] = (m.Flags & flagBits[i]) != 0;
        extras["rf_bounding_center"] = V3(m.BoundingCenter);
        extras["rf_material_indices"] = Arr(m.MaterialIndices ?? []);
        extras["rf_material_names"] = Arr((m.MaterialIndices ?? []).Select(i => i >= 0 && i < tableSize ? MaterialIdentity(table[i]) : ""));
        extras["rf_smoothing_groups"] = Arr(m.Faces.Select(f => f.SmoothingGroup));
        extras["rf_face_material_index"] = Arr(m.Faces.Select(f => f.MaterialIndex));
        extras["rf_face_indices"] = Arr(m.Faces.SelectMany(f => new[] { f.V0, f.V1, f.V2 }));
        extras["rf_face_colors"] = Arr(m.Faces.SelectMany(f => new[] { f.Color0, f.Color1, f.Color2 }).SelectMany(c => new[] { c.X, c.Y, c.Z }));
        extras["rf_face_normals"] = Arr(m.Faces.SelectMany(f => new[] { f.Normal.X, f.Normal.Y, f.Normal.Z }));
        extras["rf_face_centers"] = Arr(m.Faces.SelectMany(f => new[] { f.Center.X, f.Center.Y, f.Center.Z }));
        extras["rf_face_radii"] = Arr(m.Faces.Select(f => f.Radius));
        extras["rf_face_vertex_indices"] = Arr(m.Faces.SelectMany(f => new[] { f.FaceVertex0, f.FaceVertex1, f.FaceVertex2 }));
        extras["rf_face_vertex_raw"] = new JsonObject
        {
            ["smoothing"] = Arr(m.FaceVertices.Select(v => v.SmoothingGroup)), ["vertex_index"] = Arr(m.FaceVertices.Select(v => v.VertexIndex)),
            ["uv_bits_b64"] = Convert.ToBase64String(MemoryMarshal.AsBytes(m.FaceVertices.SelectMany(v => new[] { v.RawU, v.RawV }).ToArray().AsSpan())),
            ["adjacent_counts"] = Arr(m.FaceVertices.Select(v => v.AdjacentFaces.Length)),
            ["adjacent"] = Arr(m.FaceVertices.SelectMany(v => v.AdjacentFaces)),
        };
        var posFrames = new JsonArray();
        for (int i = 0; i < m.Frames.Length; i++)
            if (m.Frames[i].Positions is { } cp)
                posFrames.Add(new JsonObject { ["frame"] = i, ["center"] = V3(cp.Center), ["multiplier"] = V3(cp.Multiplier),
                    ["s16"] = Convert.ToBase64String(MemoryMarshal.AsBytes(cp.Raw.AsSpan())) });
        extras["rf_pos_frames"] = posFrames;
        if (m.Frames.Any(f => f.FacingSize is not null))
            extras["rf_frame_sizes"] = new JsonArray(m.Frames.Select((f, i) => (f, i)).Where(x => x.f.FacingSize is not null)
                .Select(x => (JsonNode)Arr(new[] { (float)x.i, x.f.FacingSize!.Value.X, x.f.FacingSize.Value.Y })).ToArray());
        if (m.Frames.Any(f => f.Uvs is not null))
            extras["rf_uv_frames"] = new JsonArray(m.Frames.Select((f, i) => (f, i)).Where(x => x.f.Uvs is not null)
                .Select(x => (JsonNode)new JsonObject { ["frame"] = x.i, ["uvs"] = Arr(x.f.Uvs!.Value.SelectMany(u => new[] { u.X, u.Y })) }).ToArray());
        if (m.Frames.Any(f => f.Transform is not null))
            extras["rf_frame_transforms"] = new JsonArray(m.Frames.Select((f, i) => (f, i)).Where(x => x.f.Transform is not null)
                .Select(x => (JsonNode)new JsonObject { ["frame"] = x.i, ["translation"] = V3(x.f.Transform!.Translation),
                    ["rotation"] = Arr(new[] { x.f.Transform.Rotation.X, x.f.Transform.Rotation.Y, x.f.Transform.Rotation.Z, x.f.Transform.Rotation.W }),
                    ["scale"] = V3(x.f.Transform.Scale) }).ToArray());
        if (m.Frames.Any(f => f.Opacity is not null)) extras["rf_frame_opacity"] = Arr(m.Frames.Where(f => f.Opacity is not null).Select(f => f.Opacity!.Value));
        if (m.Frames.Length > 0 && m.Frames[0].FacingSize is { } size) { extras["rf_width"] = size.X; extras["rf_height"] = size.Y; }
        if (m.Frames.Length > 0 && m.Frames[0].UpVector is { } up) extras["rf_up_vector"] = V3(up);
        if (m.Pivot is { } pv)
        {
            extras["rf_pivot_translation"] = V3(pv.Translation); extras["rf_pivot_scale"] = V3(pv.Scale);
            extras["rf_pivot_rotation"] = Arr(new[] { pv.Rotation.X, pv.Rotation.Y, pv.Rotation.Z, pv.Rotation.W });
        }
        if (m.Keys is { } k)
            extras["rf_keyframes"] = new JsonObject
            {
                ["translation"] = VKeys(k.Translation), ["scale"] = VKeys(k.Scale),
                ["rotation"] = new JsonArray(k.Rotation.Select(r => (JsonNode)new JsonObject { ["time"] = r.Time,
                    ["value"] = Arr(new[] { r.Value.X, r.Value.Y, r.Value.Z, r.Value.W }), ["tension"] = r.Tension, ["continuity"] = r.Continuity,
                    ["bias"] = r.Bias, ["ease_in"] = r.EaseIn, ["ease_out"] = r.EaseOut }).ToArray()),
            };

        // Geometry: frame-0 positions split per corner by (vertex, uv, color); normals by RF's smoothing rule.
        var pos0 = m.DecodePositions(0) ?? m.LegacyPositions?.ToArray() ?? new Vector3[m.NumVertices];
        var uv0 = m.Frames.Length > 0 ? m.Frames[0].Uvs : null;
        var normals = CornerNormals(m, pos0);
        var key = new Dictionary<(int, Vector2, Vector3, Vector3), int>();
        var source = new List<int>(); var gPos = new List<Vector3>(); var gNrm = new List<Vector3>(); var gUv = new List<Vector2>(); var gCol = new List<Vector4>();
        var corner = new int[m.Faces.Length * 3];
        for (int f = 0; f < m.Faces.Length; f++)
            for (int c = 0; c < 3; c++)
            {
                var face = m.Faces[f];
                int v = c == 0 ? face.V0 : c == 1 ? face.V1 : face.V2;
                var uv = uv0 is { } u && f * 3 + c < u.Length ? u[f * 3 + c] : face.LegacyUvs is { } lu ? lu[c] : Vector2.Zero;
                var col = c == 0 ? face.Color0 : c == 1 ? face.Color1 : face.Color2;
                var n = normals[f * 3 + c];
                if (!key.TryGetValue((v, uv, col, n), out int g))
                {
                    key[(v, uv, col, n)] = g = gPos.Count;
                    source.Add(v); gPos.Add(VfxGltfSpace.Vector(v < pos0.Length ? pos0[v] : Vector3.Zero)); gNrm.Add(VfxGltfSpace.Vector(n));
                    gUv.Add(uv); gCol.Add(new Vector4(col, 1));
                }
                corner[f * 3 + c] = g;
            }
        extras["rf_gltf_vertex_source"] = Arr(source);
        var mesh = new GltfMesh { Name = m.Name };
        if (gPos.Count > 0)
        {
            int aPos = buf.AddVector3(gPos.ToArray(), 34962, minMax: true), aNrm = buf.AddVector3(gNrm.ToArray()),
                aUv = buf.AddVector2(gUv.ToArray()), aCol = buf.AddVector4(gCol.ToArray());
            var targets = new List<Dictionary<string, int>>();
            if (m.IsMorph && m.Frames.Length > 1)
                for (int i = 1; i < m.Frames.Length; i++)
                {
                    var pi = m.DecodePositions(i) ?? pos0;
                    targets.Add(new() { ["POSITION"] = buf.AddVector3(source.Select((s, g) => VfxGltfSpace.Vector(pi[s]) - gPos[g]).ToArray(), 34962, minMax: true) });
                }
            int triangles = 0;
            int slots = Math.Max(m.MaterialCount, m.Faces.Length == 0 ? 0 : m.Faces.Max(f => f.MaterialIndex) + 1);
            for (int slot = 0; slot < slots; slot++)
            {
                var idx = new List<int>();
                for (int f = 0; f < m.Faces.Length; f++)
                    if (m.Faces[f].MaterialIndex == slot) idx.AddRange([corner[f * 3], corner[f * 3 + 2], corner[f * 3 + 1]]);
                if (idx.Count < 3) continue;
                var prim = new GltfPrimitive { Indices = buf.AddIndices(idx.ToArray()), Mode = 4,
                    Extras = new JsonObject { ["rf_type"] = "vfx_mesh_primitive", ["rf_face_material_index"] = slot } };
                prim.Attributes["POSITION"] = aPos; prim.Attributes["NORMAL"] = aNrm; prim.Attributes["TEXCOORD_0"] = aUv; prim.Attributes["COLOR_0"] = aCol;
                if (m.MaterialIndices is { } mi && slot < mi.Length && mi[slot] >= 0 && mi[slot] < tableSize) prim.Material = mi[slot];
                prim.Targets.AddRange(targets);
                mesh.Primitives.Add(prim);
                triangles += idx.Count / 3;
            }
            extras["cairn_triangle_count"] = triangles;
            if (mesh.Primitives.Count > 0)
            {
                node.Mesh = doc.Meshes.Count;
                doc.Meshes.Add(mesh);
                if (targets.Count > 0)
                {
                    node.Weights = new float[targets.Count];
                    p.Morph = true;
                    var times = VfxTrs.MeshTimes(m, m.Frames.Length);
                    var w = new float[times.Length * targets.Count];
                    for (int i = 1; i < times.Length; i++) w[i * targets.Count + i - 1] = 1;
                    anim.Samplers.Add(new GltfAnimationSampler { Input = buf.AddScalars(times), Output = buf.AddScalars(w, false), Interpolation = GltfInterpolation.Step });
                    anim.Channels.Add(new GltfAnimationChannel { Sampler = anim.Samplers.Count - 1, Target = { Node = nodeIndex, Path = "weights" } });
                }
            }
        }
        return VfxTrs.MeshSamples(m) is { } samples ? Samples(p, extras, samples, 0, null, VfxTrs.MeshTimes(m, samples.Length)) : extras;
    }

    private static JsonArray VKeys(IEnumerable<VfxVectorKey> keys) => new(keys.Select(k => (JsonNode)new JsonObject
    { ["time"] = k.Time, ["value"] = V3(k.Value), ["in_tangent"] = V3(k.InTangent), ["out_tangent"] = V3(k.OutTangent) }).ToArray());

    /// <summary>RF's rule: group 0 is flat, otherwise average faces sharing the position and any smoothing bit.</summary>
    internal static Vector3[] CornerNormals(VfxMesh m, Vector3[] pos)
    {
        var faceN = m.Faces.Select(f => f.Normal.LengthSquared() > 0 ? Vector3.Normalize(f.Normal) : Vector3.UnitY).ToArray();
        var byVertex = new Dictionary<int, List<int>>();
        for (int f = 0; f < m.Faces.Length; f++)
            foreach (int v in new[] { m.Faces[f].V0, m.Faces[f].V1, m.Faces[f].V2 })
                (byVertex.TryGetValue(v, out var l) ? l : byVertex[v] = []).Add(f);
        var result = new Vector3[m.Faces.Length * 3];
        for (int f = 0; f < m.Faces.Length; f++)
        {
            var face = m.Faces[f];
            int[] vs = [face.V0, face.V1, face.V2];
            for (int c = 0; c < 3; c++)
            {
                if (face.SmoothingGroup == 0) { result[f * 3 + c] = faceN[f]; continue; }
                var sum = Vector3.Zero;
                foreach (int o in byVertex[vs[c]]) if (o == f || (m.Faces[o].SmoothingGroup & face.SmoothingGroup) != 0) sum += faceN[o];
                result[f * 3 + c] = sum.LengthSquared() > 1e-12f ? Vector3.Normalize(sum) : faceN[f];
            }
        }
        return result;
    }
}
