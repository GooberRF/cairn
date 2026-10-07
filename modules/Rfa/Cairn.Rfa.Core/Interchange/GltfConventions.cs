using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Interchange;

/// <summary>
/// The glTF conventions RFA Workbench shares with REDUX (the author's MIT tool), so files move
/// between the two:
/// <list type="bullet">
/// <item>Space: RF is left-handed (+X right, +Y up, +Z forward), glTF right-handed; the conversion
/// mirrors X: a point <c>(x, y, z)</c> becomes <c>(-x, y, z)</c>, an ACTIVE rotation <c>(x, y, z, w)</c>
/// becomes <c>(x, -y, -z, w)</c> (equivalently REDUX's <c>(-x, y, z, w)</c> applied to the conjugated
/// FILE quaternion), triangle winding is reversed, UVs are unchanged, 1 unit = 1 metre.</item>
/// <item>Nodes: bones are joint nodes named <c>name__rfbi{index}</c> with extras
/// <c>rf_type = "bone"</c>, <c>rf_bone_index</c>; mesh nodes are <c>{submesh}_LOD{n}</c> with
/// <c>rf_type = "brush"</c> and the submesh/LOD extras; prop points <c>rf_prop::{name}</c> and collision
/// spheres <c>rf_csphere::{name}</c> are children of their bone's node (scene roots when unattached).</item>
/// <item>Animations: times are ticks / 4800 (absolute, so a stock clip starts at 0.0333 s), rotations
/// LINEAR, translations CUBICSPLINE with tangents <c>3 (control - key) / dt</c>; header fields in
/// animation extras.</item>
/// </list>
/// The extras keys REDUX defines are in <see cref="GltfExtras"/>; keys RFA Workbench adds are marked there.
/// </summary>
public static class GltfSpace
{
    /// <summary>An RF point or direction in glTF space (X mirrored). Its own inverse.</summary>
    public static Vector3 ToGltf(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>A glTF point or direction in RF space (X mirrored). Its own inverse.</summary>
    public static Vector3 FromGltf(Vector3 v) => new(-v.X, v.Y, v.Z);

    /// <summary>An RF ACTIVE rotation in glTF space: <c>M R M</c> with <c>M = diag(-1, 1, 1)</c>, i.e. <c>(x, -y, -z, w)</c>.</summary>
    public static Quaternion ToGltf(Quaternion active) => new(active.X, -active.Y, -active.Z, active.W);

    /// <summary>A glTF rotation as an RF ACTIVE rotation (the same mirror; its own inverse).</summary>
    public static Quaternion FromGltf(Quaternion gltf) => new(gltf.X, -gltf.Y, -gltf.Z, gltf.W);

    /// <summary>A rigid transform in glTF space.</summary>
    public static Rigid ToGltf(Rigid t) => new(ToGltf(t.Rotation), ToGltf(t.Position));

    /// <summary>A glTF rigid transform in RF space.</summary>
    public static Rigid FromGltf(Rigid t) => new(FromGltf(t.Rotation), FromGltf(t.Position));

    /// <summary>The glTF node name REDUX gives a bone: its name (any old suffix stripped) + <c>__rfbi{index}</c>.</summary>
    public static string BoneNodeName(string boneName, int index)
    {
        string name = string.IsNullOrWhiteSpace(boneName) ? $"bone_{index}" : boneName.Trim();
        return StripBoneIndexSuffix(name, out _) + "__rfbi" + index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A node name without REDUX's <c>__rfbi{n}</c> suffix; <paramref name="index"/> gets n, or -1.</summary>
    public static string StripBoneIndexSuffix(string name, out int index)
    {
        index = -1;
        if (string.IsNullOrEmpty(name)) return name ?? string.Empty;
        int at = name.LastIndexOf("__rfbi", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 6 >= name.Length) return name;
        for (int i = at + 6; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) return name;
        }
        if (!int.TryParse(name.AsSpan(at + 6), NumberStyles.None, CultureInfo.InvariantCulture, out index)) return name;
        return name[..at];
    }

    /// <summary>The LOD index of a name ending in <c>_LOD{n}</c> (REDUX's rule: the digits must end the name), or -1.</summary>
    public static int LodIndexFromName(string? name, out string baseName)
    {
        baseName = name ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name)) return -1;
        int at = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
        if (at < 0 || at + 4 >= name.Length) return -1;
        for (int i = at + 4; i < name.Length; i++)
        {
            if (!char.IsAsciiDigit(name[i])) return -1;
        }
        if (!int.TryParse(name.AsSpan(at + 4), NumberStyles.None, CultureInfo.InvariantCulture, out int lod)) return -1;
        baseName = name[..at];
        return lod;
    }

    /// <summary>REDUX's collision sphere node name prefix.</summary>
    public const string CollisionSpherePrefix = "rf_csphere::";

    /// <summary>REDUX's prop point node name prefix.</summary>
    public const string PropPointPrefix = "rf_prop::";

    /// <summary>Animation time in seconds of a tick count.</summary>
    public static float Seconds(int ticks) => ticks / (float)Formats.Rfa.RfaClip.TicksPerSecond;

    /// <summary>A glTF time in seconds as ticks (rounded to the nearest tick).</summary>
    public static int Ticks(double seconds) => (int)Math.Round(seconds * Formats.Rfa.RfaClip.TicksPerSecond, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Extras keys. REDUX defines everything not marked "RFA Workbench"; those keys are additions that
/// REDUX ignores (it never reads unknown extras) and RFA Workbench uses to restore a clip exactly.
/// </summary>
public static class GltfExtras
{
    /// <summary>Node: "bone", "brush", "prop_point", "collision_sphere".</summary>
    public const string Type = "rf_type";
    /// <summary>Node (bone): the bone's index in the mesh.</summary>
    public const string BoneIndex = "rf_bone_index";
    /// <summary>Node (prop/sphere): the RF name.</summary>
    public const string Name = "rf_name";
    /// <summary>Node (prop/sphere): parent bone index or -1.</summary>
    public const string ParentBone = "rf_parent_bone";
    /// <summary>Node (prop): the authored RF quaternion (stock ones are not always unit length).</summary>
    public const string Orientation = "rf_orientation";
    /// <summary>Node (sphere): radius.</summary>
    public const string Radius = "rf_radius";
    /// <summary>Node/primitive (brush): sequential brush id.</summary>
    public const string BrushUid = "rf_brush_uid";
    /// <summary>Node/primitive (brush): <c>{submesh}_LOD{n}</c>.</summary>
    public const string BrushName = "rf_brush_name";
    /// <summary>Node (brush): texture name per submesh material.</summary>
    public const string MaterialSlots = "rf_material_slots";
    /// <summary>Node (brush): emissive/specular/glossiness/reflection/refl_map/flags per material.</summary>
    public const string MaterialProps = "rf_material_props";
    /// <summary>Node/primitive (brush): LOD index.</summary>
    public const string LodIndex = "rf_lod_index";
    /// <summary>Node (brush): LOD distance.</summary>
    public const string LodDistance = "rf_lod_distance";
    /// <summary>Node (brush): LOD flags.</summary>
    public const string LodFlags = "rf_lod_flags";
    /// <summary>Node (brush): the LOD texture list [{slot, name}].</summary>
    public const string LodTextures = "rf_lod_textures";
    /// <summary>Node (brush): submesh parent name.</summary>
    public const string SubmeshParent = "rf_submesh_parent";
    /// <summary>Node/primitive (brush): submesh index.</summary>
    public const string SubmeshIndex = "rf_submesh_index";
    /// <summary>Node (brush): submesh offset in RF space (vertices are exported in model space = local + offset).</summary>
    public const string SubmeshOffset = "rf_submesh_offset";
    /// <summary>Primitive/material: RF texture name.</summary>
    public const string Texture = "rf_texture";
    /// <summary>Primitive/material: submesh material index.</summary>
    public const string TextureSlot = "rf_texture_slot";
    /// <summary>Primitive/material: batch render flags.</summary>
    public const string RenderFlags = "rf_render_flags";
    /// <summary>Primitive: triangle flag 0x20.</summary>
    public const string DoubleSided = "rf_double_sided";
    /// <summary>Animation: clip start tick.</summary>
    public const string StartTime = "rf_start_time";
    /// <summary>Animation: clip end tick.</summary>
    public const string EndTime = "rf_end_time";
    /// <summary>Animation: ramp-in ticks.</summary>
    public const string RampIn = "rf_ramp_in_time";
    /// <summary>Animation: ramp-out ticks.</summary>
    public const string RampOut = "rf_ramp_out_time";
    /// <summary>Animation: pos_reduction.</summary>
    public const string PosReduction = "rf_pos_reduction";
    /// <summary>Animation: rot_reduction.</summary>
    public const string RotReduction = "rf_rot_reduction";

    /// <summary>RFA Workbench. Animation: RFA version (7 or 8).</summary>
    public const string Version = "rf_version";
    /// <summary>RFA Workbench. Animation: total_rotation as stored [x, y, z, w].</summary>
    public const string TotalRotation = "rf_total_rotation";
    /// <summary>RFA Workbench. Animation: total_translation as stored [x, y, z].</summary>
    public const string TotalTranslation = "rf_total_translation";
    /// <summary>RFA Workbench. Animation: number of morph vertices the source clip had (morph data is not exported).</summary>
    public const string MorphVertexCount = "rf_morph_vertices_omitted";
    /// <summary>RFA Workbench. Channel: the bone's weight in this clip.</summary>
    public const string Weight = "rf_weight";
    /// <summary>
    /// RFA Workbench. Sampler: the RFA keys this sampler was made from, so an unedited round trip is
    /// exact: an object with <c>count</c> and accessor indices (rotation: <c>times</c> UNSIGNED_INT,
    /// <c>values</c> SHORT VEC4 raw file quaternions in RF space, <c>eases</c> BYTE VEC2 in/out,
    /// <c>pads</c> SHORT; translation: <c>times</c>, <c>controls</c> FLOAT VEC3 in/out control points in
    /// RF space). Used only while the glTF keys still sample the same motion.
    /// </summary>
    public const string Keys = "rf_keys";

    // ── JSON helpers ────────────────────────────────────────────────────────

    /// <summary>The member of an extras object, or null.</summary>
    public static JsonNode? Get(JsonNode? extras, string key) => extras is JsonObject o && o.TryGetPropertyValue(key, out var v) ? v : null;

    /// <summary>A string extra.</summary>
    public static bool TryGetString(JsonNode? extras, string key, out string value)
    {
        value = string.Empty;
        if (Get(extras, key) is JsonValue v && v.TryGetValue(out string? s) && s is not null)
        {
            value = s;
            return true;
        }
        return false;
    }

    /// <summary>A number extra as double (integers and floats).</summary>
    public static bool TryGetNumber(JsonNode? extras, string key, out double value) => TryNumber(Get(extras, key), out value);

    /// <summary>An integer extra (a number with no fractional part in int range).</summary>
    public static bool TryGetInt(JsonNode? extras, string key, out int value)
    {
        value = 0;
        if (!TryGetNumber(extras, key, out double d) || d != Math.Floor(d) || d < int.MinValue || d > int.MaxValue) return false;
        value = (int)d;
        return true;
    }

    /// <summary>A boolean extra (true/false, or a number: non-zero is true).</summary>
    public static bool TryGetBool(JsonNode? extras, string key, out bool value)
    {
        value = false;
        var node = Get(extras, key);
        if (node is JsonValue v)
        {
            if (v.TryGetValue(out bool b))
            {
                value = b;
                return true;
            }
            if (TryNumber(v, out double d))
            {
                value = d != 0;
                return true;
            }
        }
        return false;
    }

    /// <summary>A numeric array extra.</summary>
    public static bool TryGetFloats(JsonNode? extras, string key, int count, out float[] values)
    {
        values = [];
        if (Get(extras, key) is not JsonArray a || a.Count < count) return false;
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            if (!TryNumber(a[i], out double d)) return false;
            result[i] = (float)d;
        }
        values = result;
        return true;
    }

    /// <summary>A JSON value as a number.</summary>
    public static bool TryNumber(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v) return false;
        if (v.TryGetValue(out double d)) { value = d; return true; }
        if (v.TryGetValue(out float f)) { value = f; return true; }
        if (v.TryGetValue(out long l)) { value = l; return true; }
        if (v.TryGetValue(out int i)) { value = i; return true; }
        if (v.TryGetValue(out uint u)) { value = u; return true; }
        if (v.TryGetValue(out System.Text.Json.JsonElement e) && e.ValueKind == System.Text.Json.JsonValueKind.Number && e.TryGetDouble(out d)) { value = d; return true; }
        return false;
    }

    /// <summary>The extras object of a property, created when missing.</summary>
    public static JsonObject Ensure(Cairn.Formats.Gltf.GltfProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (property.Extras is JsonObject o) return o;
        var created = new JsonObject();
        property.Extras = created;
        return created;
    }

    /// <summary>A JSON array of floats.</summary>
    public static JsonArray Array(params float[] values)
    {
        var a = new JsonArray();
        foreach (float v in values) a.Add(v);
        return a;
    }
}
