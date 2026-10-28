using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Lvl.Model;
using Cairn.Rfa.Interchange;

namespace Cairn.Lvl.Conversion;

/// <summary>
/// The glTF extras keys of level conversions. REDUX defines the brush keys (<see cref="GltfExtras"/>); the rest are
/// Cairn's, so glTF → .rfg can restore what glTF itself has no place for. All values are in RF space.
/// </summary>
public static class LvlGltfKeys
{
    /// <summary>Node: "level", "group_file", "group", "brush", "room", "mover", or an object type (see <see cref="LvlExtras.ObjectTypes"/>).</summary>
    public const string Type = GltfExtras.Type;
    /// <summary>Node/primitive (REDUX): brush uid (generated for rooms).</summary>
    public const string BrushUid = GltfExtras.BrushUid;
    /// <summary>Node/primitive (REDUX): brush name.</summary>
    public const string BrushName = GltfExtras.BrushName;
    /// <summary>Node (REDUX): the solid's texture list.</summary>
    public const string MaterialSlots = GltfExtras.MaterialSlots;
    /// <summary>Primitive/material (REDUX): RF texture name.</summary>
    public const string Texture = GltfExtras.Texture;
    /// <summary>Primitive (REDUX): index into the solid's texture list.</summary>
    public const string TextureSlot = GltfExtras.TextureSlot;
    /// <summary>Primitive/material (REDUX): V3M batch render flags.</summary>
    public const string RenderFlags = GltfExtras.RenderFlags;
    /// <summary>Primitive (REDUX): double-sided (never for level faces).</summary>
    public const string DoubleSided = GltfExtras.DoubleSided;

    /// <summary>Node (brush, mover): solid flags as stored.</summary>
    public const string SolidFlags = "rf_solid_flags";
    /// <summary>Node (brush, mover): life (-1 = indestructible).</summary>
    public const string Life = "rf_life";
    /// <summary>Node (brush, mover): editor state.</summary>
    public const string State = "rf_state";
    /// <summary>Node: position in RF space [x, y, z].</summary>
    public const string Position = "rf_position";
    /// <summary>Node: orientation in RF space, nine numbers (right, up, forward rows).</summary>
    public const string Orientation = "rf_orientation";
    /// <summary>Node (brush): true when the vertices are in world space (the node has no transform).</summary>
    public const string TransformApplied = "rf_transform_applied";
    /// <summary>Node (brush): Alpine data {flags, material}.</summary>
    public const string Alpine = "rf_alpine";
    /// <summary>Node (room): the room's index in the compiled geometry.</summary>
    public const string RoomIndex = "rf_room_index";
    /// <summary>Node (room): every room field (sky, liquid, ambient ...).</summary>
    public const string Room = "rf_room";
    /// <summary>Node (group): the group name.</summary>
    public const string GroupName = "rf_group_name";
    /// <summary>Node (group): moving-group data (every field, keyframes included).</summary>
    public const string Moving = "rf_moving";
    /// <summary>Node (group, levels): member object uids.</summary>
    public const string ObjectUids = "rf_object_uids";
    /// <summary>Node (group, levels): member brush uids.</summary>
    public const string BrushUids = "rf_brush_uids";
    /// <summary>Node (object): the object's uid.</summary>
    public const string Uid = "rf_uid";
    /// <summary>Node (object): every field of the object record (see <see cref="LvlExtras"/>).</summary>
    public const string Fields = "rf_fields";
    /// <summary>Node (light): an editor-only light.</summary>
    public const string EditorOnly = "rf_editor_only";
    /// <summary>Node (keyframe): its index in the moving group.</summary>
    public const string KeyframeIndex = "rf_keyframe_index";
    /// <summary>Primitive: triangles per face, in triangle order (faces are rebuilt from them).</summary>
    public const string FaceTriangles = "rf_face_triangles";
    /// <summary>Primitive: RF1 face flags per face.</summary>
    public const string FaceFlags = "rf_face_flags";
    /// <summary>Primitive: 32-bit raw face flags per face, present when any differs from the RF1 flags (RF2).</summary>
    public const string FaceRawFlags = "rf_face_raw_flags";
    /// <summary>Primitive: smoothing-group bits per face.</summary>
    public const string FaceSmoothing = "rf_face_smoothing";
    /// <summary>Primitive: face ids.</summary>
    public const string FaceIds = "rf_face_ids";
    /// <summary>Primitive: scroll speed per face [u0, v0, u1, v1 ...], present when any face scrolls.</summary>
    public const string FaceScroll = "rf_face_scroll";
    /// <summary>Material: "blend" or "mask" when the faces have alpha or holes.</summary>
    public const string Alpha = "rf_alpha";
    /// <summary>Material: full-bright faces.</summary>
    public const string FullBright = "rf_fullbright";
    /// <summary>Root: level or group-file facts (name, version, geometry, properties).</summary>
    public const string Source = "rf_source";
    /// <summary>Root: the file version.</summary>
    public const string Version = "rf_version";
    /// <summary>Root: "compiled" or "brushes".</summary>
    public const string Geometry = "rf_geometry";
    /// <summary>Root (levels): level properties.</summary>
    public const string Properties = "rf_properties";
    /// <summary>Root (levels): level info.</summary>
    public const string Info = "rf_info";
}

/// <summary>
/// Level/group → glTF: a root node for the file, a parent node per group, one node per brush, room or mover with one
/// primitive per (material, lightmapped), deduplicated materials named after their texture, objects as empty nodes
/// (lights also as KHR_lights_punctual), everything glTF cannot hold in REDUX's and Cairn's extras.
/// </summary>
internal sealed class LvlGltfConverter(LvlScene scene, LvlConvertOptions options, LvlTextureSet textures, LvlReportBuilder report, CancellationToken ct)
{
    private const string LightsExtension = "KHR_lights_punctual";
    private const string UnlitExtension = "KHR_materials_unlit";

    private readonly GltfDocument _doc = new();
    private GltfBufferBuilder _buffer = null!;
    private readonly Dictionary<(string Texture, string? Alpha, bool Unlit), int> _materials = [];
    private readonly Dictionary<string, int?> _textureIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonArray _lights = [];
    private readonly HashSet<string> _usedTextures = new(StringComparer.OrdinalIgnoreCase);
    private int _sampler = -1;
    private int _triangulated, _untextured, _scrolling, _lightmapped, _fullBright, _alpha, _holes, _specialFlags, _tubeLights, _rotationless, _badPositions, _lightsWritten;

    public GltfDocument Run()
    {
        _doc.Asset.Generator = options.Generator;
        _buffer = new GltfBufferBuilder(_doc);
        var root = new GltfNode { Name = Path.GetFileNameWithoutExtension(scene.Name) };
        var rootX = GltfExtras.Ensure(root);
        rootX[LvlGltfKeys.Type] = scene.Level is not null ? "level" : "group_file";
        rootX[LvlGltfKeys.Source] = scene.Name;
        rootX[LvlGltfKeys.Version] = scene.Level?.Version ?? scene.GroupFile?.Version ?? 0;
        rootX[LvlGltfKeys.Geometry] = scene.Geometry == LvlGeometryChoice.Compiled ? "compiled" : "brushes";
        if (scene.Level?.Properties is { } props) rootX[LvlGltfKeys.Properties] = LvlExtras.ToJson(props);
        if (scene.Level?.Info is { } info) rootX[LvlGltfKeys.Info] = LvlExtras.ToJson(info);
        _doc.Nodes.Add(root);

        var groupNodes = new int[scene.Groups.Count];
        for (int g = 0; g < scene.Groups.Count; g++)
        {
            var group = scene.Groups[g];
            var node = new GltfNode { Name = string.IsNullOrWhiteSpace(group.Name) ? $"group_{g}" : group.Name };
            var x = GltfExtras.Ensure(node);
            x[LvlGltfKeys.Type] = "group";
            x[LvlGltfKeys.GroupName] = group.Name;
            if (group.Moving is { } moving) x[LvlGltfKeys.Moving] = LvlExtras.ToJson(moving);
            if (scene.Level is not null)
            {
                x[LvlGltfKeys.ObjectUids] = Ints(group.ObjectUids);
                x[LvlGltfKeys.BrushUids] = Ints(group.BrushUids);
            }
            groupNodes[g] = Add(node);
        }

        foreach (var part in scene.Parts)
        {
            ct.ThrowIfCancellationRequested();
            int? node = AddPart(part);
            if (node is { } n) Attach(n, part.Group, groupNodes);
        }
        foreach (var obj in scene.Objects)
        {
            int n = AddObject(obj);
            Attach(n, obj.Group, groupNodes);
        }
        foreach (int g in groupNodes) Attach(g, -1, groupNodes);

        if (_buffer.Length > 0 || _doc.Accessors.Count > 0) _buffer.Finish();
        if (_lights.Count > 0)
        {
            _doc.Extensions ??= new JsonObject();
            _doc.Extensions[LightsExtension] = new JsonObject { ["lights"] = _lights };
            _doc.ExtensionsUsed.Add(LightsExtension);
        }
        if (_fullBright > 0) _doc.ExtensionsUsed.Add(UnlitExtension);
        var scene0 = new GltfScene { Name = root.Name };
        scene0.Nodes.Add(0);
        _doc.Scenes.Add(scene0);
        _doc.Scene = 0;
        Summarise();
        return _doc;
    }

    private int Add(GltfNode node)
    {
        _doc.Nodes.Add(node);
        return _doc.Nodes.Count - 1;
    }

    private void Attach(int node, int group, int[] groupNodes) =>
        _doc.Nodes[group >= 0 && group < groupNodes.Length ? groupNodes[group] : 0].Children.Add(node);

    private static JsonArray Ints(IEnumerable<int> values)
    {
        var a = new JsonArray();
        foreach (int v in values) a.Add(v);
        return a;
    }

    // ── Parts ──────────────────────────────────────────────────────────────

    private sealed class PrimitiveBuilder(int material, bool lightmapped)
    {
        public readonly int Material = material;
        public readonly bool Lightmapped = lightmapped;
        public readonly List<Vector3> Positions = [];
        public readonly List<Vector3> Normals = [];
        public readonly List<Vector2> Uvs = [];
        public readonly List<Vector2> LightmapUvs = [];
        public readonly List<int> Indices = [];
        public readonly Dictionary<(Vector3, Vector3, Vector2, Vector2), int> Map = [];
        public readonly JsonArray FaceTriangles = [];
        public readonly JsonArray FaceFlags = [];
        public readonly JsonArray FaceRawFlags = [];
        public readonly JsonArray FaceSmoothing = [];
        public readonly JsonArray FaceIds = [];
        public readonly JsonArray FaceScroll = [];
        public bool AnyRaw;
        public bool AnyScroll;
        public int TextureSlot = -1;
        public string Texture = "";

        public int Vertex(LvlCorner c)
        {
            var p = GltfSpace.ToGltf(c.Position);
            var n = GltfSpace.ToGltf(c.Normal);
            var lm = Lightmapped ? c.LightmapUv ?? Vector2.Zero : Vector2.Zero;
            var key = (p, n, c.Uv, lm);
            if (Map.TryGetValue(key, out int index)) return index;
            index = Positions.Count;
            Positions.Add(p);
            Normals.Add(n);
            Uvs.Add(c.Uv);
            if (Lightmapped) LightmapUvs.Add(lm);
            Map[key] = index;
            return index;
        }
    }

    private int? AddPart(LvlPart part)
    {
        var prims = new Dictionary<(int, bool), PrimitiveBuilder>();
        var order = new List<PrimitiveBuilder>();
        foreach (var polygon in part.Polygons)
        {
            int material = Material(polygon);
            bool lightmapped = options.IncludeLightmapUvs && polygon.Corners[0].LightmapUv.HasValue;
            if (lightmapped) _lightmapped++;
            if (!prims.TryGetValue((material, lightmapped), out var pb))
            {
                prims[(material, lightmapped)] = pb = new PrimitiveBuilder(material, lightmapped) { TextureSlot = polygon.TextureSlot, Texture = polygon.Texture };
                order.Add(pb);
            }
            var tris = LvlGeometryHelpers.Triangulate(polygon);
            if (polygon.Corners.Length > 3) _triangulated++;
            foreach (var (a, b, c) in tris)
            {
                // The X mirror into glTF space turns the winding over, so b and c swap.
                pb.Indices.Add(pb.Vertex(polygon.Corners[a]));
                pb.Indices.Add(pb.Vertex(polygon.Corners[c]));
                pb.Indices.Add(pb.Vertex(polygon.Corners[b]));
            }
            var f = polygon.Face;
            pb.FaceTriangles.Add(tris.Count);
            pb.FaceFlags.Add((int)f.Flags);
            pb.FaceRawFlags.Add(f.RawFlags);
            if (f.RawFlags != (uint)f.Flags) pb.AnyRaw = true;
            pb.FaceSmoothing.Add(f.Smoothing);
            pb.FaceIds.Add(f.FaceId);
            var scroll = f.Scroll ?? Vector2.Zero;
            pb.FaceScroll.Add(LvlExtras.Number(scroll.X));
            pb.FaceScroll.Add(LvlExtras.Number(scroll.Y));
            if (f.Scroll is not null)
            {
                pb.AnyScroll = true;
                _scrolling++;
            }
            if ((polygon.Classes & (RfFaceClass.Sky | RfFaceClass.Invisible | RfFaceClass.LiquidSurface | RfFaceClass.Portal)) != 0) _specialFlags++;
            report.Faces++;
            report.Triangles += tris.Count;
        }
        if (order.Count == 0) return null;

        var mesh = new GltfMesh { Name = part.Name };
        foreach (var pb in order)
        {
            var prim = new GltfPrimitive { Material = pb.Material };
            prim.Attributes["POSITION"] = _buffer.AddVector3(pb.Positions.ToArray(), minMax: true);
            prim.Attributes["NORMAL"] = _buffer.AddVector3(pb.Normals.ToArray());
            prim.Attributes["TEXCOORD_0"] = _buffer.AddVector2(pb.Uvs.ToArray());
            if (pb.Lightmapped) prim.Attributes["TEXCOORD_1"] = _buffer.AddVector2(pb.LightmapUvs.ToArray());
            prim.Indices = _buffer.AddIndices(pb.Indices.ToArray());
            report.Vertices += pb.Positions.Count;
            var x = GltfExtras.Ensure(prim);
            x[LvlGltfKeys.BrushUid] = part.Uid;
            x[LvlGltfKeys.BrushName] = part.Name;
            x[LvlGltfKeys.Texture] = pb.Texture;
            x[LvlGltfKeys.TextureSlot] = pb.TextureSlot;
            x[LvlGltfKeys.RenderFlags] = Rfa.Formats.V3d.V3dBuilder.DefaultRenderFlags;
            x[LvlGltfKeys.DoubleSided] = false;
            x[LvlGltfKeys.FaceTriangles] = pb.FaceTriangles;
            x[LvlGltfKeys.FaceFlags] = pb.FaceFlags;
            if (pb.AnyRaw) x[LvlGltfKeys.FaceRawFlags] = pb.FaceRawFlags;
            x[LvlGltfKeys.FaceSmoothing] = pb.FaceSmoothing;
            x[LvlGltfKeys.FaceIds] = pb.FaceIds;
            if (pb.AnyScroll) x[LvlGltfKeys.FaceScroll] = pb.FaceScroll;
            mesh.Primitives.Add(prim);
        }
        _doc.Meshes.Add(mesh);

        var node = new GltfNode { Name = part.Name, Mesh = _doc.Meshes.Count - 1 };
        var nx = GltfExtras.Ensure(node);
        nx[LvlGltfKeys.Type] = part.Kind switch { LvlPartKind.Room => "room", LvlPartKind.Mover => "mover", _ => "brush" };
        nx[LvlGltfKeys.BrushUid] = part.Uid;
        nx[LvlGltfKeys.BrushName] = part.Name;
        var slots = new JsonArray();
        foreach (string t in part.Geometry.Textures) slots.Add(t);
        nx[LvlGltfKeys.MaterialSlots] = slots;
        if (part.Brush is { } brush)
        {
            nx[LvlGltfKeys.SolidFlags] = (uint)brush.SolidFlags;
            nx[LvlGltfKeys.Life] = brush.Life;
            nx[LvlGltfKeys.State] = brush.State;
            nx[LvlGltfKeys.Position] = LvlExtras.Numbers(brush.Position.X, brush.Position.Y, brush.Position.Z);
            nx[LvlGltfKeys.Orientation] = LvlExtras.Matrix(brush.Orientation);
            nx[LvlGltfKeys.TransformApplied] = part.TransformApplied;
            if (brush.Alpine is { } alpine) nx[LvlGltfKeys.Alpine] = LvlExtras.ToJson(alpine);
            if (!part.TransformApplied)
            {
                node.Translation = GltfSpace.ToGltf(brush.Position);
                node.Rotation = GltfSpace.ToGltf(LvlGeometryHelpers.Rotation(brush.Orientation));
            }
        }
        if (part.Kind == LvlPartKind.Room)
        {
            nx[LvlGltfKeys.RoomIndex] = part.RoomIndex;
            if (part.Room is { } room) nx[LvlGltfKeys.Room] = LvlExtras.ToJson(room);
        }
        report.Parts++;
        return Add(node);
    }

    // ── Materials ──────────────────────────────────────────────────────────

    private int Material(LvlPolygon polygon)
    {
        string texture = polygon.Texture;
        _usedTextures.Add(texture);
        if (texture.Length == 0) _untextured++;
        int? textureIndex = TextureIndex(texture);
        bool unlit = (polygon.Classes & RfFaceClass.FullBright) != 0;
        string? alpha = (polygon.Classes & RfFaceClass.Alpha) != 0 ? "blend"
            : (polygon.Classes & RfFaceClass.Hole) != 0 ? "mask"
            : textureIndex is not null && textures.Get(texture)?.HasAlpha == true ? "mask" : null;
        if (unlit) _fullBright++;
        if ((polygon.Classes & RfFaceClass.Alpha) != 0) _alpha++;
        if ((polygon.Classes & RfFaceClass.Hole) != 0) _holes++;
        var key = (texture.ToLowerInvariant(), alpha, unlit);
        if (_materials.TryGetValue(key, out int existing)) return existing;

        var pbr = new GltfPbrMetallicRoughness { BaseColorFactor = [1f, 1f, 1f, 1f], MetallicFactor = 0f, RoughnessFactor = 1f };
        if (textureIndex is { } ti) pbr.BaseColorTexture = new GltfTextureInfo { Index = ti };
        var mat = new GltfMaterial
        {
            // Named after the texture, so a tool that drops extras still finds the RF bitmap (REDUX does the same).
            Name = texture.Length > 0 ? texture : "untextured",
            PbrMetallicRoughness = pbr,
        };
        if (alpha == "blend") mat.AlphaMode = "BLEND";
        else if (alpha == "mask")
        {
            mat.AlphaMode = "MASK";
            mat.AlphaCutoff = 0.5f;
        }
        if (unlit) mat.Extensions = new JsonObject { [UnlitExtension] = new JsonObject() };
        var x = GltfExtras.Ensure(mat);
        x[LvlGltfKeys.Texture] = texture;
        x[LvlGltfKeys.RenderFlags] = Rfa.Formats.V3d.V3dBuilder.DefaultRenderFlags;
        if (alpha is not null) x[LvlGltfKeys.Alpha] = alpha;
        if (unlit) x[LvlGltfKeys.FullBright] = true;
        _doc.Materials.Add(mat);
        _materials[key] = _doc.Materials.Count - 1;
        report.Materials++;
        return _doc.Materials.Count - 1;
    }

    private int? TextureIndex(string name)
    {
        if (name.Length == 0) return null;
        if (_textureIndex.TryGetValue(name, out var cached)) return cached;
        int? result = null;
        if (textures.Get(name) is { } image)
        {
            _doc.Images.Add(new GltfImage { Name = name, Uri = image.FileName, MimeType = "image/png", Data = image.Png });
            if (_sampler < 0)
            {
                _sampler = _doc.Samplers.Count;
                _doc.Samplers.Add(new GltfSampler { MagFilter = 9729, MinFilter = 9987, WrapS = 10497, WrapT = 10497 });
            }
            _doc.Textures.Add(new GltfTexture { Sampler = _sampler, Source = _doc.Images.Count - 1 });
            result = _doc.Textures.Count - 1;
        }
        _textureIndex[name] = result;
        return result;
    }

    // ── Objects ────────────────────────────────────────────────────────────

    private int AddObject(LvlSceneObject obj)
    {
        string label = obj.Record switch
        {
            RfPlacedObject p when p.ClassName.Length > 0 => $"{obj.TypeName}_{obj.Uid}_{p.ClassName}",
            RfEvent e when e.ClassName.Length > 0 => $"{obj.TypeName}_{obj.Uid}_{e.ClassName}",
            _ when obj.Uid >= 0 => $"{obj.TypeName}_{obj.Uid}",
            _ => obj.TypeName,
        };
        var at = obj.Position;
        var node = new GltfNode { Name = label };
        if (float.IsFinite(at.X) && float.IsFinite(at.Y) && float.IsFinite(at.Z)) node.Translation = GltfSpace.ToGltf(at);
        else _badPositions++;
        if (obj.Orientation is { } m)
        {
            if (LvlScene.IsRotation(m)) node.Rotation = GltfSpace.ToGltf(LvlGeometryHelpers.Rotation(m));
            else _rotationless++;
        }
        var x = GltfExtras.Ensure(node);
        x[LvlGltfKeys.Type] = obj.TypeName;
        if (obj.Uid >= 0 || obj.Record is RfObject) x[LvlGltfKeys.Uid] = obj.Uid;
        if (obj.EditorOnly) x[LvlGltfKeys.EditorOnly] = true;
        if (obj.KeyframeIndex >= 0) x[LvlGltfKeys.KeyframeIndex] = obj.KeyframeIndex;
        x[LvlGltfKeys.Fields] = LvlExtras.ToJson(obj.Record);
        int index = Add(node);
        if (obj.Record is RfLight light) AddLight(index, light);
        report.Objects++;
        return index;
    }

    private void AddLight(int nodeIndex, RfLight light)
    {
        var def = new JsonObject
        {
            ["name"] = _doc.Nodes[nodeIndex].Name,
            ["color"] = LvlExtras.Numbers(light.Color.R / 255f, light.Color.G / 255f, light.Color.B / 255f),
        };
        float range = float.IsFinite(light.Range) && light.Range > 0 ? light.Range : 0f;
        float on = float.IsFinite(light.OnIntensity) ? MathF.Max(0f, light.OnIntensity) : 1f;
        // RF lights fade out at their range; a punctual light has inverse-square falloff. Candela = intensity * range²
        // makes the illuminance at the range about the light's own intensity, which is what viewers show best.
        def["intensity"] = LvlExtras.Number(range > 0 ? on * range * range : on);
        if (range > 0) def["range"] = LvlExtras.Number(range);
        bool spot = light.Type == RfLightType.Spot;
        if (light.Type == RfLightType.Tube) _tubeLights++;
        if (spot)
        {
            float outer = Math.Clamp(DegreesToRadians(light.Fov) / 2f, 0.01f, MathF.PI / 2f);
            float inner = Math.Clamp(outer - DegreesToRadians(light.FovDropoff) / 2f, 0f, outer * 0.999f);
            def["type"] = "spot";
            def["spot"] = new JsonObject { ["innerConeAngle"] = LvlExtras.Number(inner), ["outerConeAngle"] = LvlExtras.Number(outer) };
        }
        else
        {
            def["type"] = "point";
        }
        int lightIndex = _lights.Count;
        _lights.Add(def);
        _lightsWritten++;
        var ext = new JsonObject { [LightsExtension] = new JsonObject { ["light"] = lightIndex } };
        if (!spot)
        {
            _doc.Nodes[nodeIndex].Extensions = ext;
            return;
        }
        // A glTF spot light shines down its node's -Z; RF's shines along the object's forward (+Z), so the light sits
        // on a child turned half way round Y.
        var child = new GltfNode { Name = _doc.Nodes[nodeIndex].Name + "_light", Rotation = new Quaternion(0f, 1f, 0f, 0f), Extensions = ext };
        _doc.Nodes[nodeIndex].Children.Add(Add(child));
    }

    private static float DegreesToRadians(float degrees) => float.IsFinite(degrees) ? degrees * MathF.PI / 180f : 0f;

    // ── Report ─────────────────────────────────────────────────────────────

    private void Summarise()
    {
        textures.Summarise(report, _usedTextures);
        if (_triangulated > 0)
            report.Info(LvlConvertCodes.Triangulated, $"{LvlReportBuilder.Count(_triangulated, "polygon")} with more than three corners were triangulated; rf_face_triangles says which triangles make each face.", LvlIssueKind.Split);
        if (report.Vertices > 0)
            report.Info(LvlConvertCodes.VertexSplit, $"{LvlReportBuilder.Count(report.SourceVertices, "source vertex", "source vertices")} became {LvlReportBuilder.Count(report.Vertices, "glTF vertex", "glTF vertices")}: a vertex is split wherever its corners differ in texture coordinates or normal.", LvlIssueKind.Split);
        if (_untextured > 0)
            report.Info(LvlConvertCodes.Untextured, $"{LvlReportBuilder.Count(_untextured, "face")} have no texture; they use the 'untextured' material.");
        if (_scrolling > 0)
            report.Info(LvlConvertCodes.Scroll, $"{LvlReportBuilder.Count(_scrolling, "face")} scroll their texture; glTF cannot animate that, so the speed is kept in rf_face_scroll.", LvlIssueKind.Approximated);
        if (_lightmapped > 0)
            report.Info(LvlConvertCodes.Lightmaps, options.IncludeLightmapUvs
                ? $"Lightmap coordinates of {LvlReportBuilder.Count(_lightmapped, "face")} are in TEXCOORD_1; the lightmap images are not exported."
                : "Lightmap coordinates and images are not exported.", LvlIssueKind.Dropped);
        if (_fullBright > 0)
            report.Info(LvlConvertCodes.FullBright, $"{LvlReportBuilder.Count(_fullBright, "full-bright face")} use unlit materials (KHR_materials_unlit); they stay one-sided.", LvlIssueKind.Approximated);
        if (_alpha + _holes > 0)
            report.Info(LvlConvertCodes.Transparency, $"Faces with alpha use blended materials and faces with holes use alpha-masked ones ({_alpha} and {_holes}).", LvlIssueKind.Approximated);
        if (_specialFlags > 0)
            report.Info(LvlConvertCodes.FaceFlags, $"{LvlReportBuilder.Count(_specialFlags, "face")} are sky, invisible, liquid-surface or portal faces; glTF has no such thing, so their flags are kept in rf_face_flags.", LvlIssueKind.Approximated);
        if (scene.Parts.Any(p => p.Brush is not null))
            report.Info(LvlConvertCodes.BrushData, "Solid flags, life, state, position and orientation of every brush are kept in node extras for Cairn to read back.");
        if (scene.Objects.Count > 0)
            report.Info(LvlConvertCodes.Objects, $"{LvlReportBuilder.Count(scene.Objects.Count, "object")} written as empty nodes with every field in rf_fields.");
        if (scene.ObjectsFiltered > 0)
            report.Info(LvlConvertCodes.ObjectsDropped, $"{LvlReportBuilder.Count(scene.ObjectsFiltered, "object")} left out by the object filter.", LvlIssueKind.Dropped);
        if (_lightsWritten > 0)
            report.Info(LvlConvertCodes.Lights, $"{LvlReportBuilder.Count(_lightsWritten, "light")} also written as KHR_lights_punctual lights; their brightness is an approximation of RF's falloff"
                + (_tubeLights > 0 ? $", and {LvlReportBuilder.Count(_tubeLights, "tube light")} became point lights." : "."), LvlIssueKind.Approximated);
        if (_rotationless > 0)
            report.Info(LvlConvertCodes.NotARotation, $"{LvlReportBuilder.Count(_rotationless, "object")} have an orientation that is not a pure rotation; their nodes have no rotation (rf_fields keeps the stored one).", LvlIssueKind.Approximated);
        if (_badPositions > 0)
            report.Warning(LvlConvertCodes.Objects, $"{LvlReportBuilder.Count(_badPositions, "object")} have a position that is not a number; their nodes sit at the origin (rf_fields keeps the stored value).", LvlIssueKind.Approximated);
        if (scene.Groups.Count > 0)
            report.Info(LvlConvertCodes.Groups, $"{LvlReportBuilder.Count(scene.Groups.Count, "group")} written as parent nodes; moving groups keep their data in rf_moving and their keyframes as nodes.");
    }
}
