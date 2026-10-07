using System.Collections.Immutable;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Interchange;

public sealed record VfxGltfImportOptions
{
    /// <summary>Frame rate used to sample node animation of plain meshes (no extras).</summary>
    public float PlainFps { get; init; } = 15f;
}

public sealed record VfxGltfImportResult(VfxFile File, ImmutableArray<string> Messages);

/// <summary>glTF -> VFX (version 0x40006), REDUX-compatible.</summary>
public static class VfxGltfImport
{
    private const int Version = VfxVersion.Current;

    public static VfxGltfImportResult Import(string path, VfxGltfImportOptions? options = null) =>
        Import(GltfReader.ReadFile(path), options);

    /// <summary>1 for a glTF carrying VFX extras, 0.25 for any other readable glTF (importable as plain meshes), 0 otherwise.</summary>
    public static float Probe(string path)
    {
        try { return GltfReader.ReadFile(path).Nodes.Any(n => RfType(n) is { } t && (t == "vfx" || t.StartsWith("vfx_", StringComparison.Ordinal))) ? 1f : 0.25f; }
        catch (Exception e) when (e is IOException or FormatException or UnauthorizedAccessException or System.Text.Json.JsonException
            or Cairn.Formats.AssetFormatException) { return 0f; }
    }

    /// <summary>As <see cref="Import(string, VfxGltfImportOptions?)"/>; <paramref name="cancel"/> is checked in the node and frame loops.</summary>
    public static VfxGltfImportResult Import(string path, VfxGltfImportOptions? options, CancellationToken cancel) =>
        Import(GltfReader.ReadFile(path), options, cancel);

    public static VfxGltfImportResult Import(GltfDocument doc, VfxGltfImportOptions? options = null) => Import(doc, options, CancellationToken.None);

    /// <summary>
    /// Imports <paramref name="doc"/>. Malformed content throws <see cref="Cairn.Formats.AssetFormatException"/> (never another
    /// exception type); <paramref name="cancel"/> throws <see cref="OperationCanceledException"/> between nodes and frames.
    /// </summary>
    public static VfxGltfImportResult Import(GltfDocument doc, VfxGltfImportOptions? options, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var ctx = new Context(doc, options ?? new VfxGltfImportOptions(), cancel);
        try { return ctx.Run(); }
        catch (Exception e) when (e is IndexOutOfRangeException or ArgumentException or InvalidOperationException or OverflowException
            or NullReferenceException or KeyNotFoundException or FormatException or InvalidCastException or OutOfMemoryException)
        {
            // Backstop for malformed input the explicit checks below do not name (review-findings-2 #6).
            throw new Cairn.Formats.AssetFormatException($"Malformed glTF: {e.Message}", e);
        }
    }

    /// <summary>The most mesh frames a sampled glTF animation yields (the VFX reader's own frame limit); longer ones are clipped with a message.</summary>
    public const int MaxSampledFrames = 100_000;

    internal static string? RfType(GltfProperty p) => Extras(p)?["rf_type"] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>The extras object, re-parsing Blender's Python-repr string fallback.</summary>
    internal static JsonObject? Extras(GltfProperty p)
    {
        if (p.Extras is JsonObject o) return o;
        if (p.Extras is JsonValue v && v.TryGetValue(out string? s)) return ParseRepr(s) as JsonObject;
        return null;
    }

    internal static JsonNode? ParseRepr(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '\'' or '"')
            {
                sb.Append('"');
                for (i++; i < s.Length && s[i] != c; i++)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i] == '\'' ? "'" : "\\" + s[i]); }
                    else sb.Append(s[i] == '"' ? "\\\"" : s[i].ToString());
                }
                sb.Append('"');
            }
            else if (char.IsLetter(c))
            {
                int j = i; while (j < s.Length && char.IsLetter(s[j])) j++;
                string word = s[i..j];
                sb.Append(word switch { "True" => "true", "False" => "false", "None" => "null", _ => word });
                i = j - 1;
            }
            else sb.Append(c);
        }
        try { return JsonNode.Parse(sb.ToString()); } catch (System.Text.Json.JsonException) { return null; }
    }

    private sealed class Context(GltfDocument doc, VfxGltfImportOptions options, CancellationToken cancel)
    {
        private readonly List<string> _messages = [];
        private readonly List<VfxMaterial> _table = [];
        private readonly Dictionary<int, int> _gltfMaterialToTable = [];
        private int[] _parent = [];
        private bool _recordsUsable;

        public VfxGltfImportResult Run()
        {
            _parent = Enumerable.Repeat(-1, doc.Nodes.Count).ToArray();
            for (int i = 0; i < doc.Nodes.Count; i++) foreach (int c in doc.Nodes[i].Children) if (c >= 0 && c < _parent.Length) _parent[c] = i;
            CheckAcyclic();
            int root = doc.Nodes.FindIndex(n => RfType(n) == "vfx");
            var rootX = root >= 0 ? Extras(doc.Nodes[root])! : null;
            if (doc.Nodes.Count(n => RfType(n) == "vfx") > 1) _messages.Add("Warning: more than one VFX root node; extra roots are treated as groups.");
            var file = rootX?["cairn_file"] as JsonObject;
            _recordsUsable = file?["version"]?.GetValue<int>() == Version;
            if (file is not null && !_recordsUsable) _messages.Add($"The glTF was exported from version 0x{file["version"]?.GetValue<int>():X}; rebuilding as 0x{Version:X} from the glTF view.");

            if (rootX?["rf_material_table"] is JsonArray table)
                foreach (var e in table) _table.Add(MaterialFromExtras(e as JsonObject));
            else
                foreach (var m in doc.Materials.Where(m => Extras(m)?["rf_material_index"] is not null).OrderBy(m => Int(Extras(m)!["rf_material_index"]!)))
                    _table.Add(MaterialFromExtras(Extras(m)));
            for (int i = 0; i < doc.Materials.Count; i++)
                if (Extras(doc.Materials[i])?["rf_material_index"] is { } idx && Int(idx) < _table.Count) _gltfMaterialToTable[i] = Int(idx);

            var pending = new List<(string Tag, string Name, VfxSection Section)>();
            for (int i = 0; i < doc.Nodes.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                if (i == root) continue;
                if (BuildSection(i) is { } s) pending.Add((VfxGltfExport.TagText(s.Tag), NameOf(s, doc.Nodes[i].Name), s));
            }

            var sections = new List<VfxSection>();
            var materials = new Queue<VfxMaterial>(_table);
            if (rootX?["rf_section_order"] is JsonArray order)
                foreach (var e in order.OfType<JsonObject>())
                {
                    string tag = e["type"]?.GetValue<string>() ?? "", name = e["name"]?.GetValue<string>() ?? "";
                    if (tag == "MATL") { if (materials.Count > 0) sections.Add(materials.Dequeue()); continue; }
                    int k = pending.FindIndex(p => p.Tag == tag && p.Name == name);
                    if (k < 0) k = pending.FindIndex(p => p.Tag == tag && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (k < 0) k = pending.FindIndex(p => p.Tag == tag);
                    if (k < 0) continue;
                    sections.Add(pending[k].Section);
                    pending.RemoveAt(k);
                }
            sections.AddRange(pending.Select(p => p.Section));
            sections.AddRange(materials);

            int endFrame = rootX?["rf_end_frame"] is { } ef ? Int(ef) : 0;
            if (endFrame == 0) endFrame = Math.Max(0, sections.OfType<VfxMesh>().Select(m => m.Frames.Length).DefaultIfEmpty(1).Max() - 1);
            int? headerFlags = file?["header_flags"] is { } hf && _recordsUsable ? Int(hf) : rootX?["rf_header_flags"] is { } rhf ? Int(rhf) : 0;
            int? selsets = file?["selset_object_count"] is { } so && _recordsUsable ? Int(so) : rootX?["rf_selset_object_count"] is { } rso ? Int(rso) : 0;
            var vfx = new VfxFile(Version, headerFlags, endFrame, null, selsets, [.. sections])
            { CameraFrameCount = _recordsUsable && file?["camera_frame_count"] is { } cf ? Int(cf) : 0 };
            return new VfxGltfImportResult(vfx, [.. _messages]);
        }

        private static int Int(JsonNode n) => (int)n.GetValue<double>();

        private static string NameOf(VfxSection s, string? nodeName) => s switch
        {
            VfxMesh m => m.Name, VfxDummy d => d.Name, VfxParticleSystem p => p.Name, VfxLight l => l.Name, VfxSpacewarp w => w.Name,
            VfxOpaqueSection => "", _ => nodeName ?? "",
        };

        private VfxMaterial MaterialFromExtras(JsonObject? x)
        {
            if (_recordsUsable && VfxGltfRecords.FromNode(x?[VfxGltfRecords.Key]) is VfxMaterial rec) return rec;
            float[] F(string k) => x?[k] is JsonArray a ? a.Select(v => (float)v!.GetValue<double>()).ToArray() : [];
            VfxTexture? T(string k) => x?[k] is JsonObject t ? new VfxTexture(t["name"]?.GetValue<string>() ?? "",
                t["start_frame"] is { } s ? Int(s) : 0, t["playback_rate"] is { } r ? (float)r.GetValue<double>() : 0, t["anim_type"] is { } a ? Int(a) : 0) : null;
            int type = x?["rf_mat_type_id"] is { } ti ? Int(ti) : 0;
            var sc = F("rf_solid_color");
            var self = F("rf_self_illumination"); var op = F("rf_opacity");
            return new VfxMaterial(type, x?["rf_fps"] is { } fps ? Int(fps) : 15, (byte)(x?["rf_additive"]?.GetValue<bool>() == true ? 1 : 0),
                type != 2 ? T("tex_0") ?? new VfxTexture("", 0, 0, 0) : null, type == 1 ? T("tex_1") ?? new VfxTexture("", 0, 0, 0) : null, null,
                type == 1 ? [.. F("rf_mix_frames")] : null,
                type != 2 ? new Vector3(F("rf_specular_level").FirstOrDefault(), 0, 0) with
                { X = x?["rf_specular_level"] is { } s1 ? (float)s1.GetValue<double>() : 0, Y = x?["rf_glossiness"] is { } s2 ? (float)s2.GetValue<double>() : 0,
                  Z = x?["rf_reflection_amount"] is { } s3 ? (float)s3.GetValue<double>() : 0 } : null,
                type != 2 ? x?["rf_refl_tex_name"]?.GetValue<string>() ?? "" : null,
                type == 2 ? new VfxColorI(sc.Length > 0 ? (int)sc[0] : 255, sc.Length > 1 ? (int)sc[1] : 255, sc.Length > 2 ? (int)sc[2] : 255) : null,
                self.Length > 0 ? [.. self] : [1f], op.Length > 0 ? [.. op] : [1f]);
        }

        private int _defaultMaterial = -1;

        /// <summary>Table index of a grey colour material for primitives that have no material at all (appended once).</summary>
        private int DefaultMaterial()
        {
            if (_defaultMaterial >= 0) return _defaultMaterial;
            _table.Add(Cairn.Vfx.Editing.VfxBuilder.ColorMaterial());
            _messages.Add($"Grey colour material added at table index {_table.Count - 1} for meshes without a material.");
            return _defaultMaterial = _table.Count - 1;
        }

        /// <summary>Global table index of a glTF material; plain materials are appended with REDUX's defaults.</summary>
        private int TableIndex(int gltfMaterial)
        {
            if (_gltfMaterialToTable.TryGetValue(gltfMaterial, out int t)) return t;
            var gm = doc.Materials[gltfMaterial];
            string? tex = null;
            if (gm.PbrMetallicRoughness?.BaseColorTexture is { } bt && bt.Index < doc.Textures.Count && doc.Textures[bt.Index].Source is int src && src < doc.Images.Count)
                tex = Path.GetFileName((doc.Images[src].Uri ?? doc.Images[src].Name ?? "").Replace('\\', '/'));
            string norm = System.Text.RegularExpressions.Regex.Replace(gm.Name ?? "", @"^\d{3}_|\.\d{3}$", "");
            int byName = _table.FindIndex(m => (m.Texture0?.Name ?? "") == norm && norm.Length > 0);
            if (byName >= 0) return _gltfMaterialToTable[gltfMaterial] = byName;
            if (tex is { Length: > 0 } && !tex.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) && !tex.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase))
            {
                string renamed = Path.GetFileNameWithoutExtension(tex) + ".tga";
                _messages.Add($"Texture '{tex}' renamed to '{renamed}'.");
                tex = renamed;
            }
            _table.Add(new VfxMaterial(0, 15, 0, new VfxTexture(tex ?? "", 0, 0, 0), null, null, null, Vector3.Zero, "", null, [1f], [1f]));
            _messages.Add($"New material '{tex ?? gm.Name}' appended at table index {_table.Count - 1}.");
            return _gltfMaterialToTable[gltfMaterial] = _table.Count - 1;
        }

        private VfxSection? BuildSection(int nodeIndex)
        {
            var node = doc.Nodes[nodeIndex];
            var x = Extras(node);
            var rec = _recordsUsable ? VfxGltfRecords.FromNode(x?[VfxGltfRecords.Key]) : null;
            if (rec is VfxMesh m) return CheckMesh(nodeIndex, m);
            if (rec is not null) return rec;
            string? type = x?["rf_type"]?.GetValue<string>();
            if (x is not null)
                switch (type)
                {
                    case "vfx_mesh" when x["rf_face_indices"] is JsonArray && x["rf_pos_frames"] is JsonArray: return CheckMesh(nodeIndex, MeshFromExtras(x, node.Name));
                    case "vfx_dummy": return CheckDummy(nodeIndex, DummyFromExtras(x, node.Name));
                    case "vfx_particle_system": return CheckParticles(nodeIndex, ParticlesFromExtras(x, node.Name));
                    case "vfx_light": return LightFromExtras(x, node.Name);
                    case "vfx_spacewarp": return WarpFromExtras(x, node.Name);
                    case "vfx_unknown" when x["rf_raw_base64"] is JsonValue raw:
                        return new VfxOpaqueSection(x["rf_section_type"] is { } st ? (uint)(long)st.GetValue<double>() : TagOf(Str(x, "rf_section_tag")),
                            [.. Convert.FromBase64String(raw.GetValue<string>())]);
                    case "vfx_material_modifier":
                        return new VfxOpaqueSection(VfxSectionTag.MaterialModifier, [.. BitConverter.GetBytes(I(x, "rf_material_index"))]);
                    case "vfx_camera" or "vfx_chain":
                        // Cairn keeps camera/chain sections opaque (exported as vfx_unknown with raw bytes); REDUX's decoded
                        // form cannot be re-encoded without the raw section, so it is dropped with a message.
                        _messages.Add($"Section '{node.Name}' ({type}) is REDUX's decoded form, which Cairn cannot re-encode; skipped.");
                        return null;
                }
            if (node.Mesh is int && (type is null or "vfx_mesh" || !type.StartsWith("vfx", StringComparison.Ordinal)))
            {
                _messages.Add($"New mesh '{node.Name}' built from glTF geometry.");
                return RebuildMesh(nodeIndex, null, x);
            }
            if (type is not null && type != "vfx" && type != "vfx_mesh_primitive")
                _messages.Add($"Node '{node.Name}' ({type}) has no usable Cairn record; skipped (REDUX-only extras are not imported for this type yet).");
            return null;
        }

        private static uint TagOf(string s) => s.Length == 4 ? BitConverter.ToUInt32(Encoding.ASCII.GetBytes(s)) : 0;
        private static string Str(JsonObject x, string k, string d = "") => x[k] is JsonValue v && v.TryGetValue(out string? s) ? s : d;
        private static int I(JsonObject? x, string k, int d = 0) => x?[k] is JsonValue v ? v.TryGetValue(out bool b) ? (b ? 1 : 0) : (int)(long)v.GetValue<double>() : d;
        private static float Fl(JsonObject? x, string k, float d = 0) => x?[k] is JsonValue v ? (float)v.GetValue<double>() : d;
        private static bool B(JsonObject? x, string k) => x?[k] is JsonValue v && (v.TryGetValue(out bool b) ? b : v.GetValue<double>() != 0);
        private static float[] Fa(JsonNode? n) => n is JsonArray a ? a.Select(v => v is JsonValue jv && jv.TryGetValue(out bool b) ? (b ? 1f : 0f) : (float)v!.GetValue<double>()).ToArray() : [];
        private static int[] Ia(JsonNode? n) => n is JsonArray a ? a.Select(v => (int)(long)v!.GetValue<double>()).ToArray() : [];
        private static Vector3 V3(JsonNode? n, Vector3 d = default) => Fa(n) is { Length: >= 3 } f ? new Vector3(f[0], f[1], f[2]) : d;
        private static Quaternion Q(JsonNode? n) => Fa(n) is { Length: >= 4 } f ? new Quaternion(f[0], f[1], f[2], f[3]) : Quaternion.Identity;
        private static Vector3 V3At(float[] f, int i) => 3 * i + 2 < f.Length ? new Vector3(f[3 * i], f[3 * i + 1], f[3 * i + 2]) : Vector3.Zero;
        private static Quaternion QAt(float[] f, int i) => 4 * i + 3 < f.Length ? new Quaternion(f[4 * i], f[4 * i + 1], f[4 * i + 2], f[4 * i + 3]) : Quaternion.Identity;
        private static float At(float[] f, int i) => i < f.Length ? f[i] : 0;
        private static string Parent(JsonObject x) => Str(x, "rf_parent_name", "Scene Root");

        /// <summary>A mesh rebuilt from REDUX's authored tables (raw RF space, bit-exact positions).</summary>
        private static VfxMesh MeshFromExtras(JsonObject x, string? nodeName)
        {
            uint flags = x["rf_flags"] is JsonValue fv ? (uint)(long)fv.GetValue<double>() : 0;
            bool keyed = I(x, "rf_is_keyframed") != 0;
            var idx = Ia(x["rf_face_indices"]); var cols = Fa(x["rf_face_colors"]); var nrm = Fa(x["rf_face_normals"]); var ctr = Fa(x["rf_face_centers"]);
            var rad = Fa(x["rf_face_radii"]); var slot = Ia(x["rf_face_material_index"]); var sg = Ia(x["rf_smoothing_groups"]); var fvi = Ia(x["rf_face_vertex_indices"]);
            int Ix(int[] a, int i) => i < a.Length ? a[i] : 0;
            var faces = Enumerable.Range(0, idx.Length / 3).Select(f => new VfxFace(idx[3 * f], idx[3 * f + 1], idx[3 * f + 2], null,
                V3At(cols, 3 * f), V3At(cols, 3 * f + 1), V3At(cols, 3 * f + 2), V3At(nrm, f), V3At(ctr, f), At(rad, f),
                Ix(slot, f), Ix(sg, f), Ix(fvi, 3 * f), Ix(fvi, 3 * f + 1), Ix(fvi, 3 * f + 2))).ToImmutableArray();
            var raw = x["rf_face_vertex_raw"] as JsonObject;
            var rsg = Ia(raw?["smoothing"]); var rvi = Ia(raw?["vertex_index"]); var rac = Ia(raw?["adjacent_counts"]); var radj = Ia(raw?["adjacent"]);
            var uv = raw?["uv_bits_b64"] is JsonValue ub ? Convert.FromBase64String(ub.GetValue<string>()) : [];
            var fverts = ImmutableArray.CreateBuilder<VfxFaceVertex>(rsg.Length);
            for (int r = 0, a = 0; r < rsg.Length; a += Ix(rac, r), r++)
                fverts.Add(new VfxFaceVertex(rsg[r], Ix(rvi, r), 8 * r + 8 <= uv.Length ? BitConverter.ToUInt32(uv, 8 * r) : 0, 8 * r + 8 <= uv.Length ? BitConverter.ToUInt32(uv, 8 * r + 4) : 0,
                    [.. radj.Skip(a).Take(Ix(rac, r))]));
            var pos = (x["rf_pos_frames"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(p => I(p, "frame"), p =>
            {
                var b = Convert.FromBase64String(Str(p, "s16"));
                return new VfxCompressedPositions(V3(p["center"]), V3(p["multiplier"]), [.. Enumerable.Range(0, b.Length / 2).Select(i => BitConverter.ToInt16(b, 2 * i))]);
            });
            var sizes = (x["rf_frame_sizes"] as JsonArray ?? []).Select(Fa).Where(s => s.Length >= 3).ToDictionary(s => (int)s[0], s => new Vector2(s[1], s[2]));
            var uvs = (x["rf_uv_frames"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(u => I(u, "frame"), u =>
            { var f = Fa(u["uvs"]); return Enumerable.Range(0, f.Length / 2).Select(i => new Vector2(f[2 * i], f[2 * i + 1])).ToImmutableArray(); });
            var trs = (x["rf_frame_transforms"] as JsonArray ?? []).OfType<JsonObject>().ToDictionary(t => I(t, "frame"),
                t => new VfxTransform(V3(t["translation"]), Q(t["rotation"]), V3(t["scale"], Vector3.One)));
            var opacity = Fa(x["rf_frame_opacity"]);
            VfxTransform? pivot = x["rf_pivot_translation"] is not null || keyed
                ? new VfxTransform(V3(x["rf_pivot_translation"]), Q(x["rf_pivot_rotation"]), V3(x["rf_pivot_scale"], Vector3.One)) : null;
            // A keyframed mesh's frame-0 transform is not in the extras; REDUX writes the pivot there.
            if (keyed && pivot is not null) trs.TryAdd(0, pivot);
            int count = Math.Max(1, I(x, "rf_num_frames", pos.Count));
            var frames = Enumerable.Range(0, count).Select(i =>
            {
                var l = VfxMesh.FrameLayout(Version, flags, keyed, i);
                return new VfxMeshFrame(l.Positions ? pos.GetValueOrDefault(i) : null,
                    l.FacingSize ? sizes.TryGetValue(i, out var s) ? s : new Vector2(Fl(x, "rf_width", 1), Fl(x, "rf_height", 1)) : null,
                    l.UpVector ? V3(x["rf_up_vector"], Vector3.UnitY) : null, l.Uvs ? uvs.GetValueOrDefault(i, []) : null,
                    l.Transform ? trs.GetValueOrDefault(i, VfxTrs.Identity) : null, l.Pad ? (byte)0 : null, l.Opacity ? i < opacity.Length ? opacity[i] : 1f : null);
            }).ToImmutableArray();
            VfxKeyLists? keys = null;
            if (keyed && x["rf_keyframes"] is JsonObject k)
            {
                ImmutableArray<VfxVectorKey> Vk(string c) => [.. (k[c] as JsonArray ?? []).OfType<JsonObject>().Select(e =>
                    new VfxVectorKey(I(e, "time"), V3(e["value"]), V3(e["in_tangent"]), V3(e["out_tangent"])))];
                keys = new VfxKeyLists(Vk("translation"), [.. (k["rotation"] as JsonArray ?? []).OfType<JsonObject>().Select(e => new VfxRotationKey(I(e, "time"),
                    Q(e["value"]), Fl(e, "tension"), Fl(e, "continuity"), Fl(e, "bias"), Fl(e, "ease_in"), Fl(e, "ease_out")))], Vk("scale"));
            }
            return new VfxMesh(Str(x, "rf_name", nodeName ?? ""), Parent(x), (byte)I(x, "rf_save_parent"), I(x, "rf_vertex_count"), null, faces,
                I(x, "rf_fps", 15), Fl(x, "rf_start_time"), Fl(x, "rf_end_time"), null, null, [.. Ia(x["rf_material_indices"])], null,
                V3(x["rf_bounding_center"]), Fl(x, "rf_bounding_radius"), null, flags, null, fverts.ToImmutable(), (byte)(keyed ? 1 : 0), frames,
                pivot, keys);
        }

        private static VfxDummy DummyFromExtras(JsonObject x, string? nodeName) =>
            new(Str(x, "rf_name", nodeName ?? ""), Parent(x), (byte)I(x, "rf_save_parent"), V3(x["rf_pos"]), Q(x["rf_orient"]),
                [.. (x["rf_frames"] as JsonArray ?? []).OfType<JsonObject>().Select(f => new VfxDummyFrame(V3(f["pos"]), Q(f["orient"])))]);

        private static VfxParticleSystem ParticlesFromExtras(JsonObject x, string? nodeName)
        {
            var pos = Fa(x["rf_frame_pos"]); var orient = Fa(x["rf_frame_orient"]); var w = Fa(x["rf_frame_width"]); var h = Fa(x["rf_frame_height"]);
            var drop = Fa(x["rf_frame_drop_size"]); var speed = Fa(x["rf_frame_speed"]); var sv = Fa(x["rf_frame_speed_variation"]);
            var birth = Fa(x["rf_frame_birth_rate"]); // rf_frame_opacity is a pre-0x40006 field: dropped, as REDUX's writer does

            int n = I(x, "rf_num_frames", w.Length);
            return new VfxParticleSystem(Str(x, "rf_name", nodeName ?? ""), Parent(x), (byte)I(x, "rf_save_parent"),
                x["rf_flags"] is JsonValue fv ? (uint)(long)fv.GetValue<double>() : 0,
                [.. (x["rf_warps"] as JsonArray ?? []).Select(v => v!.GetValue<string>())], I(x, "rf_start_time"), I(x, "rf_material_index"), null,
                I(x, "rf_particle_count"), I(x, "rf_start"), I(x, "rf_lifetime"), Fl(x, "rf_lifetime_variation"), I(x, "rf_emitter_type"), null,
                new Vector2(Fl(x, "rf_shrink_at_birth"), Fl(x, "rf_shrink_at_death")), null, null, new Vector2(Fl(x, "rf_fade_at_birth"), Fl(x, "rf_fade_at_death")),
                x["rf_tail_distance"] is not null ? Fl(x, "rf_tail_distance") : null, null,
                [.. Enumerable.Range(0, n).Select(i => new VfxParticleFrame(V3At(pos, i), QAt(orient, i), At(w, i), At(h, i), At(drop, i), At(speed, i), At(sv, i),
                    At(birth, i), null))]);
        }

        private static VfxLightParams LightParams(JsonObject? p) =>
            new(V3(p?["pos"]), Fl(p, "radius"), Fl(p, "multiplier"), V3(p?["color"]), (byte)(B(p, "is_on") ? 1 : 0));

        private static VfxLight LightFromExtras(JsonObject x, string? nodeName) =>
            new(Str(x, "rf_name", nodeName ?? ""), Parent(x), (byte)I(x, "rf_save_parent"), LightParams(x["rf_params"] as JsonObject),
                [.. (x["rf_frames"] as JsonArray ?? []).OfType<JsonObject>().Select(LightParams)]);

        private static VfxSpacewarp WarpFromExtras(JsonObject x, string? nodeName)
        {
            var pos = Fa(x["rf_frame_pos"]); var orient = Fa(x["rf_frame_orient"]); var st = Fa(x["rf_frame_strength"]); var de = Fa(x["rf_frame_decay"]);
            var tu = Fa(x["rf_frame_turbulence"]); var fr = Fa(x["rf_frame_frequency"]); var sc = Fa(x["rf_frame_scale"]);
            return new VfxSpacewarp(Str(x, "rf_name", nodeName ?? ""), Parent(x), I(x, "rf_warp_type"),
                [.. Enumerable.Range(0, st.Length).Select(i => new VfxSpacewarpFrame(V3At(pos, i), QAt(orient, i), st[i], At(de, i), At(tu, i), At(fr, i), At(sc, i)))]);
        }

        /// <summary>Authored per-frame pos/orient kept when the node animation, sampled at <paramref name="time"/>(i), agrees (REDUX's rule).</summary>
        private (Vector3 Pos, Quaternion Orient)[]? EditedTrack(int nodeIndex, IReadOnlyList<(Vector3 Pos, Quaternion Orient)> authored, Func<int, float> time)
        {
            if (authored.Count == 0) return null;
            var w = Enumerable.Range(0, authored.Count).Select(i => VfxGltfSpace.Transform(SampleWorld(nodeIndex, time(i)))).ToArray();
            bool same = w.Zip(authored).All(p => Vector3.Distance(p.First.Translation, p.Second.Pos) <= 1e-4f * Math.Max(1, p.Second.Pos.Length())
                && Math.Abs(Quaternion.Dot(Quaternion.Normalize(p.First.Rotation), Quaternion.Normalize(p.Second.Orient))) >= 1 - 1e-4f);
            return same ? null : [.. w.Select(t => (t.Translation, t.Rotation))];
        }

        private VfxDummy CheckDummy(int nodeIndex, VfxDummy d)
        {
            if (EditedTrack(nodeIndex, [.. d.Frames.Select(f => (f.Position, f.Orientation))], i => i / 15f) is not { } t) return d;
            _messages.Add($"Dummy '{d.Name}': glTF animation was edited; frames re-sampled.");
            return d with { Frames = [.. t.Select(p => new VfxDummyFrame(p.Pos, p.Orient))] };
        }

        private VfxParticleSystem CheckParticles(int nodeIndex, VfxParticleSystem p)
        {
            if (EditedTrack(nodeIndex, [.. p.Frames.Select(f => (f.Position, f.Orientation))], i => (p.StartTime + i) / 15f) is not { } t) return p;
            _messages.Add($"Particle system '{p.Name}': glTF animation was edited; frames re-sampled.");
            return p with { Frames = [.. p.Frames.Select((f, i) => f with { Position = t[i].Pos, Orientation = t[i].Orient })] };
        }

        private VfxMesh CheckMesh(int nodeIndex, VfxMesh m)
        {
            var node = doc.Nodes[nodeIndex];
            bool geometryAgrees = GeometryAgrees(node, m);
            if (!geometryAgrees)
            {
                _messages.Add($"Mesh '{m.Name}': glTF geometry was edited; rebuilt.");
                m = RebuildMesh(nodeIndex, m, Extras(node));
            }
            // REDUX's ApplyTransformPolicy: skipped for morph meshes and meshes without frames; compared in glTF space.
            if (m.IsMorph || m.Frames.Length == 0 || VfxTrs.MeshSamples(m) is not { } samples) return m;
            var times = VfxTrs.MeshTimes(m, samples.Length);
            var sampledGltf = times.Select(t => SampleWorld(nodeIndex, t)).ToArray();
            if (samples.Zip(sampledGltf).All(p => VfxTrs.ReduxNear(VfxGltfSpace.Transform(p.First), p.Second))) return m;
            var world = sampledGltf.Select(VfxGltfSpace.Transform).ToArray();
            _messages.Add($"Mesh '{m.Name}': glTF animation was edited; transform re-baked.");
            if (m.Keys is not null)
            {
                ImmutableArray<VfxVectorKey> V(Func<VfxTransform, Vector3> f) =>
                    world.All(w => Vector3.Distance(f(w), f(world[0])) <= 1e-4f) ? [Vk(0, f(world[0]))] : [.. world.Select((w, i) => Vk(i * VfxTrs.TicksPerFrame, f(w)))];
                var rot = world.All(w => Math.Abs(Quaternion.Dot(w.Rotation, world[0].Rotation)) >= 1 - 1e-4f) ? world.Take(1) : world;
                return m with
                {
                    Pivot = VfxTrs.Identity,
                    Keys = new VfxKeyLists(V(w => w.Translation), [.. rot.Select((w, i) => new VfxRotationKey(i * VfxTrs.TicksPerFrame, w.Rotation, 0, 0, 0, 0, 0))], V(w => w.Scale)),
                };
            }
            return m with { Frames = [.. m.Frames.Select((f, i) => f.Transform is null ? f : f with { Transform = world[Math.Min(i, world.Length - 1)] })] };
        }

        private static VfxVectorKey Vk(int t, Vector3 v) => new(t, v, v, v);

        private bool GeometryAgrees(GltfNode node, VfxMesh m)
        {
            int expected = Extras(node)?["cairn_triangle_count"] is { } tc ? Int(tc) : m.Faces.Length;
            // REDUX's ResolveGeometry: a node without glTF geometry (no mesh, or no triangles) has nothing to rebuild
            // from, so the authored tables are kept (Cairn's export omits the glTF mesh when no face is drawable).
            if (node.Mesh is not int mi || mi < 0 || mi >= doc.Meshes.Count) return true;
            var mesh = doc.Meshes[mi];
            int tris = 0;
            foreach (var p in mesh.Primitives)
                if (p.Mode is null or 4 && p.Indices is int ix && p.Attributes.ContainsKey("POSITION")) tris += doc.Accessors[ix].Count / 3;
            if (tris == 0) return true;
            if (Extras(node)?["rf_gltf_vertex_source"] is not JsonArray srcArr) return false;
            if (tris != expected || mesh.Primitives.Count == 0 || !mesh.Primitives[0].Attributes.TryGetValue("POSITION", out int pa)) return false;
            var pos = GltfAccessorReader.ReadVector3(doc, pa);
            var src = srcArr.Select(v => Int(v!)).ToArray();
            if (pos.Length != src.Length) return false;
            // Frame 0 against POSITION; each morph frame against POSITION + its target delta (REDUX rebuilds the frames on any difference).
            var targets = mesh.Primitives[0].Targets;
            int morphFrames = m.IsMorph ? m.Frames.Length - 1 : 0;
            if (targets.Count != morphFrames) return false;
            for (int f = 0; f <= morphFrames; f++)
            {
                var want0 = m.DecodePositions(f) ?? [];
                var delta = f == 0 ? null : targets[f - 1].TryGetValue("POSITION", out int ta) ? GltfAccessorReader.ReadVector3(doc, ta) : null;
                if (f > 0 && delta?.Length != pos.Length) return false;
                for (int g = 0; g < src.Length; g++)
                {
                    var want = src[g] >= 0 && src[g] < want0.Length ? VfxGltfSpace.Vector(want0[src[g]]) : Vector3.Zero;
                    var have = delta is null ? pos[g] : pos[g] + delta[g];
                    if (!have.Equals(want) && !(Vector3.Distance(have, want) <= 1e-4f * Math.Max(1, want.Length()))) return false;
                }
            }
            return true;
        }

        /// <summary>World TRS (glTF space) of a node at time t: animated local TRS composed with the static parent world.</summary>
        private VfxTransform SampleWorld(int nodeIndex, float t)
        {
            var n = doc.Nodes[nodeIndex];
            Vector3 tr = n.Translation ?? Vector3.Zero, sc = n.Scale ?? Vector3.One;
            Quaternion rot = n.Rotation ?? Quaternion.Identity;
            if (n.Matrix is { Length: 16 } mx)
            {
                var d = VfxTrs.Decompose(new Matrix4x4(mx[0], mx[1], mx[2], mx[3], mx[4], mx[5], mx[6], mx[7], mx[8], mx[9], mx[10], mx[11], mx[12], mx[13], mx[14], mx[15]));
                (tr, rot, sc) = (d.Translation, d.Rotation, d.Scale);
            }
            foreach (var a in doc.Animations)
                foreach (var ch in a.Channels.Where(c => c.Target.Node == nodeIndex))
                {
                    var s = a.Samplers[ch.Sampler];
                    switch (ch.Target.Path)
                    {
                        case "translation": tr = Sample(s, t, 3, v => new Vector3(v[0], v[1], v[2]), Vector3.Lerp, tr); break;
                        case "scale": sc = Sample(s, t, 3, v => new Vector3(v[0], v[1], v[2]), Vector3.Lerp, sc); break;
                        case "rotation": rot = Quaternion.Normalize(Sample(s, t, 4, v => new Quaternion(v[0], v[1], v[2], v[3]), Quaternion.Slerp, rot)); break;
                    }
                }
            // REDUX's SampleNodeWorld: Compose(static parent world, animated local) with its TRS composition (no matrices).
            var world = new VfxTransform(tr, rot, sc);
            for (int p = _parent[nodeIndex], guard = 0; p >= 0 && guard++ < 64; p = _parent[p])
                world = VfxTrs.Compose(LocalStatic(p), world);
            return world;
        }

        private VfxTransform LocalStatic(int nodeIndex)
        {
            var n = doc.Nodes[nodeIndex];
            if (n.Matrix is { Length: 16 } mx)
                return VfxTrs.Decompose(new Matrix4x4(mx[0], mx[1], mx[2], mx[3], mx[4], mx[5], mx[6], mx[7], mx[8], mx[9], mx[10], mx[11], mx[12], mx[13], mx[14], mx[15]));
            return new VfxTransform(n.Translation ?? Vector3.Zero, n.Rotation ?? Quaternion.Identity, n.Scale ?? Vector3.One);
        }

        private readonly Dictionary<int, (float[] Values, bool Sorted)> _floats = [];

        private (float[] Values, bool Sorted) Cached(int accessor)
        {
            if (_floats.TryGetValue(accessor, out var c)) return c;
            var v = GltfAccessorReader.ReadFloats(doc, accessor);
            bool sorted = true;
            for (int k = 1; k < v.Length && sorted; k++) sorted = v[k - 1] <= v[k];
            return _floats[accessor] = (v, sorted && Array.TrueForAll(v, float.IsFinite));
        }

        /// <summary>First index with times[i] &gt;= t in sorted, finite times (-1 when none): the same answer as the linear FindIndex.</summary>
        private static int LowerBound(float[] times, float t)
        {
            int lo = 0, hi = times.Length;
            while (lo < hi) { int mid = (lo + hi) >>> 1; if (times[mid] >= t) hi = mid; else lo = mid + 1; }
            return lo < times.Length ? lo : -1;
        }

        /// <summary>LINEAR / STEP / CUBICSPLINE (Hermite; output holds in-tangent, value, out-tangent per key).</summary>
        private T Sample<T>(GltfAnimationSampler s, float t, int width, Func<float[], T> make, Func<T, T, float, T> lerp, T fallback)
        {
            // Accessors are read once per import, not once per sampled frame (sampling is bounded by MaxSampledFrames).
            var (times, sorted) = Cached(s.Input);
            var raw = Cached(s.Output).Values;
            bool cubic = s.Interpolation == GltfInterpolation.CubicSpline;
            int stride = cubic ? 3 * width : width, off = cubic ? width : 0;
            if (times.Length == 0 || raw.Length < times.Length * stride) return fallback;
            float[] At(int k, int part) => raw.AsSpan(k * stride + part, width).ToArray();
            if (t <= times[0]) return make(At(0, off));
            int i = sorted ? LowerBound(times, t) : Array.FindIndex(times, x => x >= t);
            if (i < 0) return make(At(times.Length - 1, off));
            if (times[i] == t) return make(At(i, off));
            float dt = times[i] - times[i - 1], u = (t - times[i - 1]) / dt;
            if (s.Interpolation == GltfInterpolation.Step) return make(At(i - 1, off));
            if (!cubic) return lerp(make(At(i - 1, 0)), make(At(i, 0)), u);
            float u2 = u * u, u3 = u2 * u;
            float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u, h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
            var p0 = At(i - 1, width); var m0 = At(i - 1, 2 * width); var p1 = At(i, width); var m1 = At(i, 0);
            return make(Enumerable.Range(0, width).Select(c => h00 * p0[c] + h10 * dt * m0[c] + h01 * p1[c] + h11 * dt * m1[c]).ToArray());
        }

        private VfxMesh RebuildMesh(int nodeIndex, VfxMesh? template, JsonObject? x)
        {
            var node = doc.Nodes[nodeIndex];
            var b = new VfxMeshRebuild();
            var slotsByGlobal = new List<int>(template?.MaterialIndices ?? []);
            var rangeByAccessor = new Dictionary<int, int>();
            foreach (var prim in node.Mesh is int mi ? doc.Meshes[mi].Primitives : [])
            {
                if (prim.Mode is not (null or 4) || !prim.Attributes.TryGetValue("POSITION", out int pa)) continue;
                if (!rangeByAccessor.TryGetValue(pa, out int baseIndex))
                {
                    var targets = prim.Targets.Select(tg => tg.TryGetValue("POSITION", out int ta) ? Finite(GltfAccessorReader.ReadVector3(doc, ta), ta) : null).ToList();
                    rangeByAccessor[pa] = baseIndex = b.AddVertices(Finite(GltfAccessorReader.ReadVector3(doc, pa), pa),
                        prim.Attributes.TryGetValue("NORMAL", out int na) ? GltfAccessorReader.ReadVector3(doc, na) : null,
                        prim.Attributes.TryGetValue("TEXCOORD_0", out int ua) ? GltfAccessorReader.ReadVector2(doc, ua) : null,
                        prim.Attributes.TryGetValue("COLOR_0", out int ca) ? Colors(ca) : null, targets!);
                }
                int slot;
                if (Extras(prim)?["rf_face_material_index"] is { } fmi) slot = Int(fmi);
                else
                {
                    int global = prim.Material is int gmat ? TableIndex(gmat) : template is null ? DefaultMaterial() : -1;
                    slot = global >= 0 ? slotsByGlobal.IndexOf(global) : 0;
                    if (slot < 0) { slotsByGlobal.Add(global); slot = slotsByGlobal.Count - 1; }
                }
                if (slot >= slotsByGlobal.Count)
                {
                    int g = prim.Material is int gm2 ? TableIndex(gm2) : _table.Count > 0 ? 0 : DefaultMaterial();
                    while (slotsByGlobal.Count <= slot) slotsByGlobal.Add(g);
                }
                var idx = prim.Indices is int ia ? GltfAccessorReader.ReadInts(doc, ia) : Enumerable.Range(0, doc.Accessors[pa].Count).ToArray();
                int vertexCount = doc.Accessors[pa].Count;
                foreach (int v in idx)
                    if ((uint)v >= (uint)vertexCount)
                        throw new Cairn.Formats.AssetFormatException($"Node {nodeIndex} ('{node.Name}'): index {v} is outside its {vertexCount}-vertex POSITION accessor.");
                for (int k = 0; k + 2 < idx.Length; k += 3) b.AddTriangle(baseIndex + idx[k], baseIndex + idx[k + 2], baseIndex + idx[k + 1], slot);
            }
            if (slotsByGlobal.Count == 0) slotsByGlobal.Add(0);
            if (x?["rf_face_material_index"] is JsonArray fm && fm.Count == b.FaceCount && x["rf_smoothing_groups"] is JsonArray sg && sg.Count == b.FaceCount)
                b.RestoreAuthoredOrder(sg.Select(v => Int(v!)).ToArray());

            bool plain = template is null;
            float fps = template?.Fps is > 0 ? template.Fps.Value : options.PlainFps;
            uint flags = (template?.Flags ?? 0) & ~VfxMeshFlags.Morph;
            if (b.TargetCount > 0) flags |= VfxMeshFlags.Morph;
            bool keyed = (template?.IsKeyframed ?? 0) != 0;
            int frames = b.TargetCount > 0 ? b.TargetCount + 1 : template?.Frames.Length ?? 1;
            VfxTransform[]? plainTrack = null;
            if (plain && b.TargetCount == 0)
            {
                float end = doc.Animations.SelectMany(a => a.Channels.Where(c => c.Target.Node == nodeIndex).Select(c => a.Samplers[c.Sampler]))
                    .Select(s => Times(s.Input).DefaultIfEmpty(0).Max()).DefaultIfEmpty(0).Max();
                // Bounded sampling (review-findings-2 #2): computed in double so a huge end cannot overflow the int.
                double wanted = Math.Round((double)end * fps) + 1;
                frames = (int)Math.Clamp(wanted, 1, MaxSampledFrames);
                if (wanted > MaxSampledFrames)
                    _messages.Add($"Warning: '{node.Name}': the animation ({end:0.###} s) would sample {wanted:0} frames; clipped to the first {MaxSampledFrames}.");
                plainTrack = new VfxTransform[frames];
                for (int i = 0; i < frames; i++)
                {
                    if ((i & 1023) == 0) cancel.ThrowIfCancellationRequested();
                    var w = SampleWorld(nodeIndex, i / fps);
                    if (!float.IsFinite(w.Translation.X + w.Translation.Y + w.Translation.Z + w.Scale.X + w.Scale.Y + w.Scale.Z
                        + w.Rotation.X + w.Rotation.Y + w.Rotation.Z + w.Rotation.W))
                        throw new Cairn.Formats.AssetFormatException($"Node {nodeIndex} ('{node.Name}'): the animation yields a non-finite transform at frame {i}.");
                    plainTrack[i] = VfxGltfSpace.Transform(w);
                }
            }
            var t0 = template?.Frames.Length > 0 ? template.Frames[0] : null;
            var built = b.Build(frames, flags, keyed, i =>
            {
                var tf = template is { } tm && i < tm.Frames.Length ? tm.Frames[i] : t0;
                return (tf?.FacingSize ?? new Vector2(1, 1), tf?.UpVector ?? t0?.UpVector ?? Vector3.UnitY, tf?.Uvs,
                    plainTrack?[i] ?? tf?.Transform ?? VfxTrs.Identity);
            });
            float start = template?.StartTime ?? 0;
            return new VfxMesh(template?.Name ?? node.Name ?? $"Object{nodeIndex:D2}", template?.Parent ?? ParentName(nodeIndex), template?.SaveParent ?? 0,
                built.Vertices, null, built.Faces, (int)fps, start, start + (frames - 1) / fps, null, null, [.. slotsByGlobal], null,
                built.Center, built.Radius, null, flags, null, built.FaceVertices, (byte)(keyed ? 1 : 0), built.Frames,
                keyed ? template!.Pivot : null, keyed ? template!.Keys : null);
        }

        /// <summary>Rejects a cyclic node hierarchy (review-findings-2 #1): each parent walk ends at a root or a node already proven acyclic.</summary>
        private void CheckAcyclic()
        {
            var state = new byte[_parent.Length]; // 0 unknown, 1 on the current walk, 2 proven acyclic
            var walk = new List<int>();
            for (int i = 0; i < _parent.Length; i++)
            {
                walk.Clear();
                int p = i;
                while (p >= 0 && state[p] == 0) { state[p] = 1; walk.Add(p); p = _parent[p]; }
                if (p >= 0 && state[p] == 1)
                    throw new Cairn.Formats.AssetFormatException($"The glTF node hierarchy is cyclic at node {p} ('{doc.Nodes[p].Name}').");
                foreach (int w in walk) state[w] = 2;
            }
        }

        private string ParentName(int nodeIndex)
        {
            for (int p = _parent[nodeIndex], guard = 0; p >= 0 && guard++ < _parent.Length; p = _parent[p])
                if (RfType(doc.Nodes[p]) is { } t && t != "vfx" || doc.Nodes[p].Mesh is not null) return doc.Nodes[p].Name ?? "Scene Root";
            return "Scene Root";
        }

        /// <summary>Rejects non-finite accessor floats (review-findings-2 #5): one NaN would corrupt a whole axis on quantisation.</summary>
        private static Vector3[] Finite(Vector3[] v, int accessor)
        {
            for (int i = 0; i < v.Length; i++)
                if (!float.IsFinite(v[i].X) || !float.IsFinite(v[i].Y) || !float.IsFinite(v[i].Z))
                    throw new Cairn.Formats.AssetFormatException($"Accessor {accessor}: element {i} is not finite ({v[i]}).");
            return v;
        }

        /// <summary>A sampler's key times: finite and not negative, or the file is rejected.</summary>
        private float[] Times(int accessor)
        {
            var t = GltfAccessorReader.ReadFloats(doc, accessor);
            foreach (float x in t)
                if (!float.IsFinite(x) || x < 0) throw new Cairn.Formats.AssetFormatException($"Accessor {accessor}: animation time {x} is not a finite, non-negative number.");
            return t;
        }

        private Vector3[] Colors(int accessor)
        {
            var f = GltfAccessorReader.ReadFloats(doc, accessor);
            int w = doc.Accessors[accessor].Type == GltfAccessorType.Vec4 ? 4 : 3;
            return Enumerable.Range(0, f.Length / w).Select(i => new Vector3(f[i * w], f[i * w + 1], f[i * w + 2])).ToArray();
        }
    }
}
