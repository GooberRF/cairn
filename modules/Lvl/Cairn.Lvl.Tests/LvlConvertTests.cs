using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;
using Cairn.Formats.Imaging;
using Cairn.Formats.Obj;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Linting;
using static Cairn.Lvl.Tests.LvlConvertTestData;

namespace Cairn.Lvl.Tests;

/// <summary>Level and group conversions checked numerically on synthetic levels with known UVs, transforms and flags.</summary>
public class LvlConvertTests
{
    private const float Eps = 1e-4f;

    private static readonly RfMatrix Turned = Rotation(0.7f, 0.3f, -0.2f);
    private static readonly Vector3 BrushAt = new(10f, 2f, -3f);

    private static RfLevel BrushLevel(RfSolidFlags solid = RfSolidFlags.None, Func<int, RfFace, RfFace>? tweak = null) =>
        Level(brushes: [Brush(7, Box(new Vector3(1, 2, 3), ["wall.tga", "floor.tga"], tweak), BrushAt, Turned, solid, life: 50, state: 2)]);

    private static LvlConversion Convert(RfLevel level, LvlConvertOptions options) => LvlConverter.Convert(LvlConvertInput.FromLevel(level), options);

    /// <summary>A written glTF read back (GLB in memory), so every check runs on what a viewer would load.</summary>
    private static GltfDocument Reread(GltfDocument doc) => GltfReader.Read(GltfWriter.ToGlb(doc), "test.glb", null);

    private static IEnumerable<(GltfNode Node, int Index)> NodesOfType(GltfDocument doc, string type) =>
        doc.Nodes.Select((n, i) => (n, i)).Where(t => GltfExtras.TryGetString(t.n.Extras, GltfExtras.Type, out var s) && s == type);

    /// <summary>Every vertex of a node's primitives in world space (RF axes), with its normal and UV.</summary>
    private static List<(Vector3 Position, Vector3 Normal, Vector2 Uv)> WorldVertices(GltfDocument doc, int nodeIndex, out List<(int A, int B, int C, GltfPrimitive Prim, int Base)> triangles)
    {
        var world = doc.ComputeWorldMatrices()[nodeIndex];
        var result = new List<(Vector3, Vector3, Vector2)>();
        triangles = [];
        var mesh = doc.Meshes[doc.Nodes[nodeIndex].Mesh!.Value];
        foreach (var prim in mesh.Primitives)
        {
            int start = result.Count;
            var p = GltfAccessorReader.ReadFloats(doc, prim.Attributes["POSITION"]);
            var n = GltfAccessorReader.ReadFloats(doc, prim.Attributes["NORMAL"]);
            var t = GltfAccessorReader.ReadFloats(doc, prim.Attributes["TEXCOORD_0"]);
            for (int i = 0; i < p.Length / 3; i++)
            {
                var pos = Vector3.Transform(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), world);
                var nrm = Vector3.Normalize(Vector3.TransformNormal(new Vector3(n[i * 3], n[i * 3 + 1], n[i * 3 + 2]), world));
                result.Add((GltfSpace.FromGltf(pos), GltfSpace.FromGltf(nrm), new Vector2(t[i * 2], t[i * 2 + 1])));
            }
            var idx = GltfAccessorReader.ReadInts(doc, prim.Indices!.Value);
            for (int i = 0; i < idx.Length; i += 3) triangles.Add((idx[i], idx[i + 1], idx[i + 2], prim, start));
        }
        return result;
    }

    /// <summary>The (world position, UV, normal) every corner of a brush should produce.</summary>
    private static List<(Vector3 Position, Vector3 Normal, Vector2 Uv)> ExpectedCorners(RfBrush brush)
    {
        var g = brush.Geometry;
        var list = new List<(Vector3, Vector3, Vector2)>();
        for (int k = 0; k < g.Faces.Length; k++)
        {
            var normal = Vector3.Normalize(brush.Orientation.Rotate(FaceDirections[k]));
            foreach (var c in g.Faces[k].Corners) list.Add((brush.ToWorld(g.Vertices[c.Vertex]), normal, c.Uv));
        }
        return list;
    }

    private static void AssertSameCorners(List<(Vector3 Position, Vector3 Normal, Vector2 Uv)> expected, List<(Vector3 Position, Vector3 Normal, Vector2 Uv)> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var e in expected)
        {
            Assert.Contains(actual, a => Vector3.Distance(a.Position, e.Position) < Eps && Vector2.Distance(a.Uv, e.Uv) < Eps && Vector3.Distance(a.Normal, e.Normal) < Eps);
        }
    }

    // ── glTF: transforms, per-corner UVs, normals, winding ─────────────────

    [Theory]
    [InlineData(LvlBrushTransformMode.NodeTransform)]
    [InlineData(LvlBrushTransformMode.Apply)]
    public void Gltf_brush_corners_land_where_the_brush_transform_puts_them_with_their_own_uvs(LvlBrushTransformMode mode)
    {
        var level = BrushLevel();
        var conversion = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes, BrushTransforms = mode });
        Assert.False(conversion.Report.HasErrors, conversion.Report.ToText());
        var doc = Reread(conversion.Gltf!);
        var (node, index) = Assert.Single(NodesOfType(doc, "brush"));
        if (mode == LvlBrushTransformMode.NodeTransform)
        {
            Assert.NotNull(node.Translation);
            Assert.NotNull(node.Rotation);
        }
        else
        {
            Assert.Null(node.Translation);
        }
        var actual = WorldVertices(doc, index, out var triangles);
        // 8 positions x 3 faces each, every corner with its own UV: 24 vertices, none shared.
        AssertSameCorners(ExpectedCorners(level.Brushes[0]), actual);
        Assert.Equal(12, triangles.Count);
        // In glTF space (right-handed, counter-clockwise front faces) the geometric normal agrees with the vertex normal.
        var world = doc.ComputeWorldMatrices()[index];
        foreach (var (a, b, c, prim, _) in triangles)
        {
            var p = GltfAccessorReader.ReadFloats(doc, prim.Attributes["POSITION"]);
            var n = GltfAccessorReader.ReadFloats(doc, prim.Attributes["NORMAL"]);
            Vector3 P(int i) => Vector3.Transform(new Vector3(p[i * 3], p[i * 3 + 1], p[i * 3 + 2]), world);
            var normal = Vector3.TransformNormal(new Vector3(n[a * 3], n[a * 3 + 1], n[a * 3 + 2]), world);
            Assert.True(Vector3.Dot(Vector3.Cross(P(b) - P(a), P(c) - P(a)), normal) > 0, "glTF triangle winds against its normal");
        }
    }

    [Fact]
    public void Gltf_node_transform_matches_rf_extras_exactly()
    {
        var level = BrushLevel(RfSolidFlags.Detail | RfSolidFlags.Air);
        var doc = Reread(Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!);
        var (node, _) = Assert.Single(NodesOfType(doc, "brush"));
        var x = node.Extras!;
        Assert.True(GltfExtras.TryGetInt(x, LvlGltfKeys.BrushUid, out int uid) && uid == 7);
        Assert.True(GltfExtras.TryGetInt(x, LvlGltfKeys.SolidFlags, out int solid) && solid == 6);
        Assert.True(GltfExtras.TryGetInt(x, LvlGltfKeys.Life, out int life) && life == 50);
        Assert.True(GltfExtras.TryGetInt(x, LvlGltfKeys.State, out int state) && state == 2);
        Assert.Equal(BrushAt, LvlExtras.ReadVector3(GltfExtras.Get(x, LvlGltfKeys.Position)));
        Assert.Equal(Turned, LvlExtras.ReadMatrix(GltfExtras.Get(x, LvlGltfKeys.Orientation)));
        Assert.Equal(GltfSpace.ToGltf(BrushAt), node.Translation);
    }

    [Fact]
    public void Smoothing_groups_average_normals_of_faces_that_share_a_bit_and_split_the_others()
    {
        // All six faces in group 1: every corner normal is the corner's diagonal (equal-area faces of a cube).
        var cube = Level(brushes: [Brush(1, Box(Vector3.One, ["a.tga"], (k, f) => f with { Smoothing = 1 }), Vector3.Zero)]);
        var doc = Reread(Convert(cube, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!);
        var vertices = WorldVertices(doc, NodesOfType(doc, "brush").Single().Index, out _);
        foreach (var v in vertices) Assert.True(Vector3.Distance(v.Normal, Vector3.Normalize(v.Position)) < Eps, $"{v.Normal} at {v.Position}");

        // +X and -X in group 1, the rest in group 2: the X faces never meet each other, so they keep their own normals;
        // the other four meet around the X axis and share normals there.
        var split = Level(brushes: [Brush(1, Box(Vector3.One, ["a.tga"], (k, f) => f with { Smoothing = k < 2 ? 1u : 2u }), Vector3.Zero)]);
        doc = Reread(Convert(split, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!);
        vertices = WorldVertices(doc, NodesOfType(doc, "brush").Single().Index, out _);
        foreach (var v in vertices.Where(v => v.Uv.X < 2f && v.Uv.Y < 4f))
            Assert.True(Vector3.Distance(v.Normal, new Vector3(MathF.Sign(v.Position.X), 0, 0)) < Eps);
        foreach (var v in vertices.Where(v => !(v.Uv.X < 2f && v.Uv.Y < 4f)))
            Assert.True(Vector3.Distance(v.Normal, Vector3.Normalize(new Vector3(0, v.Position.Y, v.Position.Z))) < Eps);
    }

    // ── Face classes ───────────────────────────────────────────────────────

    [Fact]
    public void Compiled_geometry_filters_faces_by_class_and_makes_one_node_per_room()
    {
        var level = Level(compiled: CompiledTwoRooms());
        var conversion = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb });
        Assert.Equal(12, conversion.Report.Stats.SourceFaces);
        // Portal and invisible faces are left out by default.
        Assert.Equal(10, conversion.Report.Stats.Faces);
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.FilteredFaces && i.Message.Contains("portal 1") && i.Message.Contains("invisible 1"));
        var doc = Reread(conversion.Gltf!);
        var rooms = NodesOfType(doc, "room").ToList();
        Assert.Equal(2, rooms.Count);
        Assert.True(GltfExtras.TryGetInt(rooms[1].Node.Extras, LvlGltfKeys.RoomIndex, out int ri) && ri == 1);
        Assert.True(GltfExtras.Get(GltfExtras.Get(rooms[1].Node.Extras, LvlGltfKeys.Room), "is_sky")?.GetValue<bool>());

        var noSky = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb }.WithFaceClass(RfFaceClass.Sky, false));
        Assert.Equal(9, noSky.Report.Stats.Faces);
        var everything = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, ExcludedFaceClasses = RfFaceClass.Normal });
        Assert.Equal(12, everything.Report.Stats.Faces);
        var onlySpecial = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, IncludeNormalFaces = false, ExcludedFaceClasses = RfFaceClass.Normal });
        Assert.Equal(6, onlySpecial.Report.Stats.Faces);
    }

    [Fact]
    public void Brush_detail_comes_from_the_solid_flag_not_the_face_bit()
    {
        // Brush 1: detail solid. Brush 2: not detail, but every face carries bit 0x08 (an editor bit on brush faces).
        var level = Level(brushes:
        [
            Brush(1, Box(Vector3.One, ["a.tga"]), Vector3.Zero, solid: RfSolidFlags.Detail),
            Brush(2, Box(Vector3.One, ["a.tga"], (k, f) => WithFlags(f, RfFaceFlags.Detail)), new Vector3(5, 0, 0)),
            Brush(3, Box(Vector3.One, ["a.tga"]), new Vector3(10, 0, 0), solid: RfSolidFlags.Portal),
        ]);
        var options = new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes, ExcludedFaceClasses = RfFaceClass.Detail };
        var doc = Convert(level, options).Gltf!;
        var uids = NodesOfType(doc, "brush").Select(n => GltfExtras.TryGetInt(n.Node.Extras, LvlGltfKeys.BrushUid, out int u) ? u : -1).ToList();
        Assert.Equal([2, 3], uids);
        // Portal brushes are left out by default.
        doc = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!;
        Assert.Equal([1, 2], NodesOfType(doc, "brush").Select(n => GltfExtras.TryGetInt(n.Node.Extras, LvlGltfKeys.BrushUid, out int u) ? u : -1));
    }

    // ── Materials and face extras ──────────────────────────────────────────

    [Fact]
    public void Materials_are_deduplicated_and_full_bright_is_unlit_not_double_sided()
    {
        var level = Level(compiled: CompiledTwoRooms());
        var doc = Reread(Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, ExcludedFaceClasses = RfFaceClass.Normal }).Gltf!);
        // floor/wall opaque, wall full bright (face 3 of room b is odd: wall), floor sky is opaque floor, alpha wall (face 5), scroll floor:
        // (floor), (wall), (wall, unlit), (wall, blend) -> 4 materials over both rooms.
        Assert.Equal(4, doc.Materials.Count);
        Assert.All(doc.Materials, m => Assert.Null(m.DoubleSided));
        var unlit = Assert.Single(doc.Materials, m => m.Extensions?.ContainsKey("KHR_materials_unlit") == true);
        Assert.Equal("wall.tga", unlit.Name);
        Assert.Contains("KHR_materials_unlit", doc.ExtensionsUsed);
        Assert.Equal("BLEND", Assert.Single(doc.Materials, m => m.AlphaMode is not null).AlphaMode);
        Assert.All(doc.Materials, m => Assert.True(GltfExtras.TryGetString(m.Extras, GltfExtras.Texture, out var t) && t.Length > 0));
    }

    [Fact]
    public void Primitives_carry_face_flags_smoothing_ids_and_scroll_per_face()
    {
        var level = Level(compiled: CompiledTwoRooms());
        var doc = Reread(Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, ExcludedFaceClasses = RfFaceClass.Normal }).Gltf!);
        var room1 = NodesOfType(doc, "room").Single(n => GltfExtras.TryGetInt(n.Node.Extras, LvlGltfKeys.RoomIndex, out int r) && r == 1).Node;
        var flags = new List<int>();
        var ids = new List<int>();
        var scrolls = new List<Vector2>();
        int triangles = 0;
        foreach (var prim in doc.Meshes[room1.Mesh!.Value].Primitives)
        {
            var x = prim.Extras!;
            var tri = ((JsonArray)GltfExtras.Get(x, LvlGltfKeys.FaceTriangles)!).Select(n => n!.GetValue<int>()).ToList();
            Assert.Equal(GltfAccessorReader.ReadInts(doc, prim.Indices!.Value).Length / 3, tri.Sum());
            triangles += tri.Sum();
            flags.AddRange(((JsonArray)GltfExtras.Get(x, LvlGltfKeys.FaceFlags)!).Select(n => n!.GetValue<int>()));
            ids.AddRange(((JsonArray)GltfExtras.Get(x, LvlGltfKeys.FaceIds)!).Select(n => n!.GetValue<int>()));
            if (GltfExtras.Get(x, LvlGltfKeys.FaceScroll) is JsonArray s)
                for (int i = 0; i < s.Count; i += 2) scrolls.Add(new Vector2(LvlExtras.ReadFloat(s[i]), LvlExtras.ReadFloat(s[i + 1])));
            Assert.True(GltfExtras.TryGetBool(x, GltfExtras.DoubleSided, out bool two) && !two);
        }
        Assert.Equal(12, triangles);
        Assert.Equal([100, 101, 102, 103, 104, 105], ids.Order());
        Assert.Contains((int)RfFaceFlags.Invisible, flags);
        Assert.Contains((int)RfFaceFlags.FullBright, flags);
        Assert.Contains(new Vector2(0.5f, -0.25f), scrolls);
    }

    [Fact]
    public void Lightmap_uvs_go_to_texcoord_1()
    {
        var g = Box(Vector3.One, ["a.tga"], (k, f) => f with { Surface = 0, Corners = [.. f.Corners.Select((c, j) => c with { LightmapUv = new Vector2(k * 0.1f, j * 0.2f) })] });
        var doc = Reread(Convert(Level(compiled: g), new LvlConvertOptions { Target = LvlConvertTarget.Glb }).Gltf!);
        var prim = doc.Meshes.Single().Primitives.Single();
        var lm = GltfAccessorReader.ReadFloats(doc, prim.Attributes["TEXCOORD_1"]);
        Assert.Equal(24 * 2, lm.Length);
        Assert.Contains(lm.Chunk(2), p => MathF.Abs(p[0] - 0.5f) < Eps && MathF.Abs(p[1] - 0.6f) < Eps);
    }

    // ── Objects, groups, lights ────────────────────────────────────────────

    private static RfLevel LevelWithObjects()
    {
        var spot = new RfLight
        {
            Uid = 10, Position = new Vector3(1, 2, 3), Orientation = Rotation(0.5f, 0.2f, 0f), Flags = (RfLightFlags)0x28,
            Color = new RflColor(255, 128, 0, 255), Range = 12f, Fov = 60f, FovDropoff = 20f, OnIntensity = 0.75f,
        };
        var omni = new RfLight { Uid = 11, Position = new Vector3(-4, 1, 0), Flags = (RfLightFlags)0x18, Range = 5f };
        var item = new RfItem { Uid = 12, ClassName = "Medical Kit", Position = new Vector3(0, 0, 5), Count = 2, RespawnTime = 30 };
        var ev = new RfEvent { Uid = 13, ClassName = "Teleport", Position = new Vector3(3, 3, 3), Orientation = Rotation(1f, 0f, 0f), Delay = float.NaN, Links = [12, 14] };
        var trigger = new RfTrigger { Uid = 14, Shape = 1, Size = new Vector3(1, 2, 3), Links = [13], ScriptName = "door trigger" };
        var keyframe = new RfKeyframe { Uid = 20, Position = new Vector3(0, 5, 0), PauseTime = 1.5f };
        var objects = new RfObjectSet { Lights = [spot, omni], Items = [item], Events = [ev], Triggers = [trigger] };
        var group = new RfLevelGroup
        {
            Name = "Door",
            Moving = new RfMovingData { Keyframes = [keyframe], IsDoor = true, MovementType = 2, StartSound = new RfSound("door.wav", 0.5f) },
            ObjectUids = [12],
            BrushUids = [7],
        };
        return Level(brushes: [Brush(7, Box(Vector3.One, ["a.tga"]), BrushAt)], objects: objects, groups: [group]) with
        {
            PlayerStart = new RfPlayerStart(new Vector3(0, 1, 0), Rotation(0.1f, 0f, 0f)),
        };
    }

    [Fact]
    public void Objects_become_nodes_whose_fields_read_back_exactly()
    {
        var level = LevelWithObjects();
        var conversion = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes });
        var doc = Reread(conversion.Gltf!);
        var sources = level.Objects.All().Concat(level.Groups[0].Moving!.Keyframes).ToList();
        foreach (var obj in sources)
        {
            string type = LvlExtras.TypeName(obj);
            var (node, index) = Assert.Single(NodesOfType(doc, type), n => GltfExtras.TryGetInt(n.Node.Extras, LvlGltfKeys.Uid, out int u) && u == obj.Uid);
            var fields = (JsonObject)GltfExtras.Get(node.Extras, LvlGltfKeys.Fields)!;
            var back = LvlExtras.FromJson(LvlExtras.ObjectType(type)!, fields);
            Assert.True(RfModelDiff.AreEqual(obj, back), string.Join("; ", RfModelDiff.Compare(obj, back)));
            var world = doc.ComputeWorldMatrices()[index];
            Assert.True(Vector3.Distance(GltfSpace.FromGltf(world.Translation), obj.Position) < Eps);
        }
        Assert.Single(NodesOfType(doc, "player_start"));
        Assert.Equal(sources.Count + 1, conversion.Report.Stats.Objects);

        // The group holds its brush, its member item and its keyframe; the moving data reads back.
        var (groupNode, _) = Assert.Single(NodesOfType(doc, "group"));
        var children = groupNode.Children.Select(c => doc.Nodes[c].Name).ToList();
        Assert.Contains("brush_7", children);
        Assert.Contains(children, c => c!.StartsWith("item_12", StringComparison.Ordinal));
        Assert.Contains(children, c => c!.StartsWith("keyframe_20", StringComparison.Ordinal));
        var moving = LvlExtras.FromJson<RfMovingData>((JsonObject)GltfExtras.Get(groupNode.Extras, LvlGltfKeys.Moving)!);
        Assert.True(RfModelDiff.AreEqual(level.Groups[0].Moving, moving));

        // Filtering object kinds.
        var noLights = Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes, Objects = LvlObjectKinds.All & ~LvlObjectKinds.Lights });
        Assert.Empty(NodesOfType(noLights.Gltf!, "light"));
        Assert.Contains(noLights.Report.Issues, i => i.Code == LvlConvertCodes.ObjectsDropped);
    }

    [Fact]
    public void Lights_are_also_khr_lights_punctual_and_spot_lights_shine_along_forward()
    {
        var level = LevelWithObjects();
        var doc = Reread(Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!);
        Assert.Contains("KHR_lights_punctual", doc.ExtensionsUsed);
        var lights = (JsonArray)doc.Extensions!["KHR_lights_punctual"]!["lights"]!;
        Assert.Equal(2, lights.Count);
        var world = doc.ComputeWorldMatrices();
        var spot = level.Objects.Lights[0];
        int lightNode = doc.Nodes.FindIndex(n => n.Extensions?["KHR_lights_punctual"] is JsonObject o && lights[o["light"]!.GetValue<int>()]!["type"]!.GetValue<string>() == "spot");
        Assert.True(lightNode >= 0);
        // glTF spot lights shine down local -Z.
        var dir = GltfSpace.FromGltf(Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, world[lightNode])));
        Assert.True(Vector3.Distance(dir, Vector3.Normalize(spot.Orientation.Forward)) < Eps, $"{dir} vs {spot.Orientation.Forward}");
        var def = lights.Single(l => l!["type"]!.GetValue<string>() == "spot")!;
        Assert.Equal(12f, def["range"]!.GetValue<float>());
        Assert.True(def["spot"]!["outerConeAngle"]!.GetValue<float>() > def["spot"]!["innerConeAngle"]!.GetValue<float>());
    }

    [Fact]
    public void A_redux_style_gltf_reader_takes_the_brushes_with_their_textures()
    {
        // Cairn's mesh import follows REDUX's glTF conventions; it must find each brush's texture and its placement.
        var level = BrushLevel();
        var doc = Reread(Convert(level, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes, Objects = LvlObjectKinds.None }).Gltf!);
        var result = GltfMeshImport.Import(doc, new GltfMeshImportOptions { Kind = V3dKind.StaticMesh });
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        var textures = result.Mesh!.Submeshes.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("wall.tga", textures);
        Assert.Contains("floor.tga", textures);
        var points = result.Mesh.Submeshes.SelectMany(s => s.Lods[0].Batches.SelectMany(b => b.Positions.Select(p => p + s.Offset))).ToList();
        foreach (var v in level.Brushes[0].Geometry.Vertices)
            Assert.Contains(points, p => Vector3.Distance(p, level.Brushes[0].ToWorld(v)) < Eps);
    }

    // ── Files: glTF + bin + PNG, re-read equality ───────────────────────────

    private static BgraImage Checker(byte alpha = 255)
    {
        var img = new BgraImage(2, 2);
        for (int i = 0; i < img.Pixels.Length; i += 4)
        {
            img.Pixels[i] = (byte)(i * 20);
            img.Pixels[i + 1] = 100;
            img.Pixels[i + 2] = 200;
            img.Pixels[i + 3] = alpha;
        }
        return img;
    }

    private static string TempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cairn-lvl-convert-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Gltf_files_with_textures_read_back_equal()
    {
        string dir = TempFolder();
        try
        {
            var level = BrushLevel();
            var options = new LvlConvertOptions
            {
                Target = LvlConvertTarget.Gltf,
                Geometry = LvlGeometryChoice.Brushes,
                TextureLoader = name => name.Equals("wall.tga", StringComparison.OrdinalIgnoreCase) ? Checker() : null,
            };
            string path = Path.Combine(dir, "level.gltf");
            var conversion = Convert(level, options);
            var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), path, options);
            Assert.True(result.Succeeded);
            Assert.Equal(conversion.PlannedFiles(path), result.Files);
            Assert.True(File.Exists(Path.Combine(dir, "level.bin")));
            Assert.True(File.Exists(Path.Combine(dir, "wall.png")));
            Assert.Equal("floor.tga", Assert.Single(result.Report.MissingTextures));
            Assert.Contains(result.Report.Issues, i => i.Code == LvlConvertCodes.MissingTexture);

            var back = GltfReader.ReadFile(path, new GltfReadOptions { LoadImages = true });
            var mem = conversion.Gltf!;
            Assert.Equal(mem.Nodes.Count, back.Nodes.Count);
            for (int i = 0; i < mem.Nodes.Count; i++)
            {
                Assert.Equal(mem.Nodes[i].Name, back.Nodes[i].Name);
                Assert.Equal(mem.Nodes[i].Extras?.ToJsonString(), back.Nodes[i].Extras?.ToJsonString());
            }
            for (int a = 0; a < mem.Accessors.Count; a++)
                Assert.Equal(GltfAccessorReader.ReadFloats(mem, a), GltfAccessorReader.ReadFloats(back, a));
            var image = Assert.Single(back.Images);
            Assert.Equal("wall.png", image.Uri);
            var decoded = ImageDecoder.Decode(image.Data!, "wall.png");
            Assert.Equal(Checker().Pixels, decoded.Pixels);
            Assert.Equal("wall.tga", back.Materials.Single(m => m.PbrMetallicRoughness?.BaseColorTexture is not null).Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── OBJ ────────────────────────────────────────────────────────────────

    [Fact]
    public void Obj_mirrors_x_flips_v_reverses_winding_and_keeps_rf_metadata()
    {
        string dir = TempFolder();
        try
        {
            var level = BrushLevel(RfSolidFlags.Detail, (k, f) => k == 0 ? WithFlags(f, RfFaceFlags.ScrollTexture) with { Scroll = new Vector2(1, 2), Smoothing = 5 } : f);
            string path = Path.Combine(dir, "level.obj");
            var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), path, new LvlConvertOptions
            {
                Target = LvlConvertTarget.Obj,
                Geometry = LvlGeometryChoice.Brushes,
                TextureLoader = _ => Checker(128),
            });
            Assert.True(result.Succeeded, result.Report.ToText());
            Assert.Equal(["level.obj", "level.mtl", "wall.png", "floor.png"], result.Files.Select(Path.GetFileName));
            Assert.All(result.Files, f => Assert.True(File.Exists(f), f));
            var obj = ObjReader.ReadFile(path);
            var brush = level.Brushes[0];
            var o = Assert.Single(obj.Objects);
            Assert.Equal("brush_7", o.Name);
            Assert.Equal(6, o.Faces.Count);
            for (int k = 0; k < 6; k++)
            {
                var face = o.Faces[k];
                var src = brush.Geometry.Faces[k];
                Assert.Equal(src.Corners.Length, face.Corners.Count);
                for (int j = 0; j < face.Corners.Count; j++)
                {
                    // Reversed corner order; X mirrored; V flipped.
                    var sc = src.Corners[src.Corners.Length - 1 - j];
                    var oc = face.Corners[j];
                    Assert.True(Vector3.Distance(GltfSpace.FromGltf(obj.Positions[oc.Position]), brush.ToWorld(brush.Geometry.Vertices[sc.Vertex])) < Eps);
                    var uv = obj.TexCoords[oc.TexCoord];
                    Assert.True(Vector2.Distance(new Vector2(uv.X, 1f - uv.Y), sc.Uv) < Eps);
                    Assert.True(Vector3.Distance(GltfSpace.FromGltf(obj.Normals[oc.Normal]), Vector3.Normalize(brush.Orientation.Rotate(FaceDirections[k]))) < Eps);
                }
            }
            var brushLine = LvlObjMetadata.Parse(Assert.Single(obj.CommentsStartingWith("rf_brush ")).Text)!.Value;
            Assert.Equal("7", brushLine.Values["uid"]);
            Assert.Equal("0x4", brushLine.Values["solid"]);
            Assert.Equal("50", brushLine.Values["life"]);
            var orient = brushLine.Values["orientation"].Split(',').Select(s => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(Turned.Forward.Z, orient[8]);
            var faceLines = obj.CommentsStartingWith("rf_face ").ToList();
            var first = LvlObjMetadata.Parse(faceLines[0].Text)!.Value;
            Assert.Equal(0, faceLines[0].FaceIndex);
            Assert.Equal("0x10", first.Values["flags"]);
            Assert.Equal("0x5", first.Values["smoothing"]);
            Assert.Equal("1,2", first.Values["scroll"]);
            Assert.Equal(1, faceLines[1].FaceIndex);

            var (mtl, missing) = ObjReader.ReadMaterialLibraries(obj, dir);
            Assert.Empty(missing);
            Assert.Contains(mtl.Materials, m => m.DiffuseMap?.FileName == "wall.png");
            Assert.All(mtl.Materials, m => Assert.EndsWith("_mask", m.Name, StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Obj_metadata_lines_parse_quoted_text_with_escapes()
    {
        string line = LvlObjMetadata.Line("rf_level", ("name", LvlObjMetadata.Text("a \"b\" c\\d.rfl")), ("version", "200"));
        var parsed = LvlObjMetadata.Parse(line)!.Value;
        Assert.Equal("rf_level", parsed.Keyword);
        Assert.Equal("a \"b\" c\\d.rfl", parsed.Values["name"]);
        Assert.Equal("200", parsed.Values["version"]);
        Assert.Null(LvlObjMetadata.Parse("Cairn OBJ export"));
    }

    // ── V3M ────────────────────────────────────────────────────────────────

    private static V3dFile ToMesh(RfLevel level, LvlConvertOptions options, out LvlConvertReport report)
    {
        var conversion = Convert(level, options with { Target = LvlConvertTarget.V3m });
        report = conversion.Report;
        Assert.True(conversion.HasOutput, report.ToText());
        // Through the writer and the reader, so the test sees what the game would load.
        return V3dReader.Read(V3dWriter.Write(conversion.Mesh!), "level.v3m");
    }

    [Fact]
    public void V3m_per_brush_places_vertices_in_world_space_and_passes_the_linter()
    {
        var level = BrushLevel(tweak: (k, f) => k == 2 ? WithFlags(f, RfFaceFlags.FullBright) : f);
        var mesh = ToMesh(level, new LvlConvertOptions { Geometry = LvlGeometryChoice.Brushes }, out var report);
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(report.Issues, i => i.Severity == LvlIssueSeverity.Error);
        var sub = Assert.Single(mesh.Submeshes);
        Assert.Equal("brush_7", sub.Name.Text);
        Assert.Equal(BrushAt, sub.Offset);
        var expected = ExpectedCorners(level.Brushes[0]);
        var actual = new List<(Vector3, Vector3, Vector2)>();
        var lod = Assert.Single(sub.Lods);
        foreach (var b in lod.Batches)
        {
            for (int i = 0; i < b.VertexCount; i++) actual.Add((b.Positions[i] + sub.Offset, b.Normals[i], b.TexCoords[i]));
            foreach (var t in b.Triangles)
            {
                Assert.Equal(0, t.Flags & V3dTriangle.DoubleSided);
                var cross = Vector3.Cross(b.Positions[t.B] - b.Positions[t.A], b.Positions[t.C] - b.Positions[t.A]);
                Assert.True(Vector3.Dot(cross, b.Normals[t.A]) > 0, "V3M triangles wind like stock meshes");
            }
        }
        AssertSameCorners(expected, actual);
        // wall, floor, and wall full bright (face 2 uses texture 0): three materials, the full-bright one emissive 1.
        Assert.Equal(3, sub.Materials.Length);
        Assert.Equal(1f, Assert.Single(sub.Materials, m => m.Emissive > 0).Emissive);
    }

    [Fact]
    public void V3m_splits_submeshes_at_seven_textures_and_batches_at_the_builder_limits()
    {
        string[] textures = [.. Enumerable.Range(0, 9).Select(i => $"t{i}.tga")];
        var many = Level(brushes: [.. Enumerable.Range(0, 9).Select(i => Brush(i + 1, Box(Vector3.One, [textures[i]]), new Vector3(i * 3, 0, 0)))]);
        var mesh = ToMesh(many, new LvlConvertOptions { Geometry = LvlGeometryChoice.Brushes, V3mGrouping = LvlV3mGrouping.WholeLevel }, out var report);
        Assert.Equal(2, mesh.Submeshes.Count());
        Assert.All(mesh.Submeshes, s => Assert.True(s.Lods[0].Textures.Length <= MeshRules.MaxTexturesPerLod));
        Assert.Contains(report.Issues, i => i.Code == LvlConvertCodes.SubmeshSplit);
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);

        // 80 x 80 quads with no shared vertex: 25,600 vertices and 12,800 triangles in one material.
        var grid = Level(compiled: Grid(80, "floor.tga"));
        mesh = ToMesh(grid, new LvlConvertOptions(), out report);
        var batches = mesh.Submeshes.Single().Lods[0].Batches;
        Assert.True(batches.Length >= 5);
        Assert.All(batches, b => Assert.True(b.VertexCount <= V3dBuilder.MaxBatchVertices && b.TriangleCount <= V3dBuilder.MaxBatchTriangles));
        Assert.Equal(12800, batches.Sum(b => b.TriangleCount));
        Assert.Contains(report.Issues, i => i.Code == LvlConvertCodes.BatchSplit);
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void V3m_reports_names_the_engine_cannot_hold()
    {
        string longTexture = new string('x', 40) + ".tga";
        var level = Level(brushes: [Brush(123456789, Box(Vector3.One, [longTexture]), Vector3.Zero)]);
        var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(level), new LvlConvertOptions { Target = LvlConvertTarget.V3m, Geometry = LvlGeometryChoice.Brushes },
            outputFileName: new string('n', 70) + ".v3m");
        Assert.True(conversion.HasOutput);
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.NameTooLong && i.Severity == LvlIssueSeverity.Error);
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.MeshLint && i.Message.StartsWith(MeshRules.FileNameTooLong, StringComparison.Ordinal));
    }

    // ── Groups, empty input, cancellation ──────────────────────────────────

    [Fact]
    public void Group_files_convert_their_brushes_and_objects_per_group()
    {
        var file = new RfGroupFile
        {
            Name = "two.rfg",
            Version = 300,
            Groups =
            [
                new RfGroup { Name = "A", Brushes = [Brush(1, Box(Vector3.One, ["a.tga"]), Vector3.Zero)], Objects = new RfObjectSet { Items = [new RfItem { Uid = 5, ClassName = "Shotgun" }] } },
                new RfGroup { Name = "B", Brushes = [Brush(2, Box(Vector3.One, ["b.tga"]), new Vector3(4, 0, 0), Turned)] },
            ],
        };
        var conversion = LvlConverter.Convert(LvlConvertInput.FromGroupFile(file), new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Compiled });
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.GeometryChoice);
        var doc = Reread(conversion.Gltf!);
        var groups = NodesOfType(doc, "group").ToList();
        Assert.Equal(["A", "B"], groups.Select(g => g.Node.Name));
        Assert.Equal(2, groups[0].Node.Children.Count);
        var expected = ExpectedCorners(file.Groups[1].Brushes[0]);
        AssertSameCorners(expected, WorldVertices(doc, groups[1].Node.Children[0], out _));
        Assert.True(GltfExtras.TryGetString(doc.Nodes[0].Extras, LvlGltfKeys.Type, out var t) && t == "group_file");
    }

    [Fact]
    public void Nothing_to_convert_is_an_error_without_output()
    {
        var level = Level(compiled: Box(Vector3.One, ["a.tga"], (k, f) => WithFlags(f, RfFaceFlags.Invisible)));
        foreach (var target in new[] { LvlConvertTarget.Gltf, LvlConvertTarget.Obj, LvlConvertTarget.V3m })
        {
            var conversion = Convert(level, new LvlConvertOptions { Target = target });
            Assert.False(conversion.HasOutput);
            Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.Empty && i.Severity == LvlIssueSeverity.Error);
            Assert.Throws<InvalidOperationException>(() => conversion.WriteTo("x" + LvlConvertOptions.ExtensionOf(target)));
        }
    }

    [Fact]
    public void Bad_faces_are_skipped_and_reported()
    {
        var g = Box(Vector3.One, ["a.tga"], (k, f) => k switch
        {
            0 => f with { Corners = [.. f.Corners.Take(2)] },
            1 => f with { Corners = f.Corners.SetItem(0, f.Corners[0] with { Vertex = 99 }) },
            2 => f with { Corners = f.Corners.SetItem(0, f.Corners[0] with { Uv = new Vector2(float.NaN, 0) }) },
            _ => f,
        });
        var conversion = Convert(Level(compiled: g), new LvlConvertOptions { Target = LvlConvertTarget.Glb });
        Assert.Equal(3, conversion.Report.Stats.Faces);
        Assert.Equal(2, conversion.Report.WithCode(LvlConvertCodes.BadVertexFaces).Count());
        Assert.Single(conversion.Report.WithCode(LvlConvertCodes.ShortFaces));
        // What is written is valid glTF.
        Reread(conversion.Gltf!);
    }

    [Fact]
    public void Numbers_that_are_not_finite_never_break_the_writers()
    {
        var nan = new Vector3(float.NaN, 0, 0);
        var level = Level(
            brushes:
            [
                Brush(1, Box(Vector3.One, ["a.tga"]), nan),
                Brush(2, Box(Vector3.One, ["a.tga"]), Vector3.Zero, new RfMatrix(nan, Vector3.UnitY, Vector3.UnitZ)),
                Brush(3, Box(Vector3.One, ["a.tga"]), new Vector3(5, 0, 0)),
            ],
            objects: new RfObjectSet
            {
                Lights = [new RfLight { Uid = 1, Position = nan, Range = float.PositiveInfinity, Fov = float.NaN, Flags = (RfLightFlags)0x28, Orientation = new RfMatrix(nan, nan, nan) }],
                Items = [new RfItem { Uid = 2, Position = new Vector3(1, float.NegativeInfinity, 0) }],
            });
        foreach (var target in new[] { LvlConvertTarget.Glb, LvlConvertTarget.Gltf, LvlConvertTarget.Obj, LvlConvertTarget.V3m })
        {
            var conversion = Convert(level, new LvlConvertOptions { Target = target, Geometry = LvlGeometryChoice.Brushes });
            Assert.True(conversion.HasOutput, conversion.Report.ToText());
            Assert.Equal(6, conversion.Report.Stats.Faces);
            Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.BadVertexFaces);
            if (conversion.Gltf is { } doc) Reread(doc);
            if (conversion.Obj is { } obj) ObjReader.Read(ObjWriter.Write(obj.Obj), "x.obj");
            if (conversion.Mesh is { } mesh) V3dWriter.Write(mesh);
        }
    }

    [Fact]
    public void A_cancelled_conversion_throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            LvlConverter.Convert(LvlConvertInput.FromLevel(Level(compiled: Grid(4, "a.tga"))), new LvlConvertOptions(), cancellationToken: cts.Token));
    }

    [Fact]
    public void Options_map_extensions_and_face_classes()
    {
        foreach (var t in Enum.GetValues<LvlConvertTarget>())
            Assert.Equal(t, LvlConvertOptions.TargetOf(LvlConvertOptions.ExtensionOf(t)));
        var o = new LvlConvertOptions();
        Assert.False(o.Keeps(RfFaceClass.Portal));
        Assert.True(o.Keeps(RfFaceClass.Sky | RfFaceClass.Scrolling));
        Assert.False(o.WithFaceClass(RfFaceClass.Scrolling, false).Keeps(RfFaceClass.Sky | RfFaceClass.Scrolling));
        Assert.False(o.WithFaceClass(RfFaceClass.Normal, false).Keeps(RfFaceClass.Normal));
        Assert.Equal(LvlGeometryChoice.Compiled, o.ResolvedGeometry);
        Assert.Equal(LvlGeometryChoice.Brushes, (o with { Target = LvlConvertTarget.Rfg }).ResolvedGeometry);
    }
}
