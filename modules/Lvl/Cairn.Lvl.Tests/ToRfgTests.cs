using Cairn.Formats.Gltf;
using Cairn.Formats.Obj;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using static Cairn.Lvl.Tests.LvlConvertTestData;

namespace Cairn.Lvl.Tests;

/// <summary>Conversions to .rfg on synthetic data: levels, group files, glTF, OBJ and meshes, and the pre-flight checks.</summary>
public sealed class ToRfgTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cairn-lvl-rfg-" + Guid.NewGuid().ToString("N"));

    public ToRfgTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly RfMatrix Turn = Rotation(0.3f, 0.2f, 0.1f);

    private static RfGeometry LitBox(Vector3 half) => Box(half, ["rock.tga", "metal.tga"], (k, f) => k switch
    {
        0 => f with { Surface = 0, Corners = [.. f.Corners.Select((c, j) => c with { LightmapUv = new Vector2(j * 0.25f, 0.5f) })] },
        1 => WithFlags(f, RfFaceFlags.ScrollTexture) with { Scroll = new Vector2(0.5f, -0.25f) },
        2 => WithFlags(f, RfFaceFlags.FullBright) with { Smoothing = 3 },
        _ => f,
    }) with { Surfaces = [new RfSurface { Lightmap = 0, Width = 8, Height = 8 }] };

    /// <summary>A level with an ungrouped flagged brush, a plain group, a moving group with its mover brush, objects and level-only data.</summary>
    internal static RfLevel SampleLevel()
    {
        var b1 = Brush(10, LitBox(new Vector3(1, 2, 3)), new Vector3(5, 0, 0), Turn, RfSolidFlags.Detail, life: 100, state: 2)
            with { Alpine = new RfAlpineBrushData(RfAlpineBrushFlags.Breakable, 3, []) };
        var b2 = Brush(11, Box(new Vector3(2, 2, 2), ["wall.tga"]), new Vector3(0, 0, 10), solid: RfSolidFlags.Air);
        var b3 = Brush(12, Box(new Vector3(1, 3, 0.25f), ["door.tga"]), new Vector3(0, 1, -5));
        var moving = new RfMovingData
        {
            Keyframes =
            [
                new RfKeyframe { Uid = 30, Position = new Vector3(0, 1, -5) },
                new RfKeyframe { Uid = 31, Position = new Vector3(0, 4, -5), PauseTime = 2 },
            ],
            MemberTransforms = [new RfMemberTransform(12, Vector3.Zero, RfMatrix.Identity)],
            IsDoor = true,
            MovementType = 2,
            StartSound = new RfSound("open.wav", 1),
        };
        var objects = new RfObjectSet
        {
            Lights = [new RfLight { Uid = 20, ClassName = "Light", Position = new Vector3(0, 3, 10), Flags = (RfLightFlags)0x18, Range = 8, Color = new RflColor(255, 128, 0, 255) }],
            Items = [new RfItem { Uid = 21, ClassName = "Medical Kit", Position = Vector3.One, Count = 1 }],
            Events = [new RfEvent { Uid = 22, ClassName = "Teleport_Player", Position = Vector3.UnitX, Orientation = Turn }],
            AlpineMeshes = [new RfAlpineMesh { Uid = 23, MeshFile = "crate.v3m", Position = new Vector3(0, 0, 10), Material = 0, TrailingFlags = 0, BrushGeometrySource = 0, CollisionMesh = "", DrawScale = 1 }],
            Coronas = [new RfCorona { Uid = 24, CoronaBitmap = "corona.tga", Position = Vector3.UnitY }],
            NavPoints = [new RfNavPoint { Uid = 25, Position = Vector3.UnitZ, Connections = [0] }],
        };
        return Level(brushes: [b1, b2, b3], objects: objects, groups:
        [
            new RfLevelGroup { Name = "walls", BrushUids = [11], ObjectUids = [20, 23] },
            new RfLevelGroup { Name = "door", Moving = moving, BrushUids = [12] },
        ]) with
        {
            PlayerStart = new RfPlayerStart(Vector3.Zero, RfMatrix.Identity),
            Cutscenes = [new RfCutscene { Uid = 40 }],
        };
    }

    /// <summary>Converts, writes the group file, reads it back.</summary>
    private (LvlConversion Conversion, RfGroupFile File) ToRfg(LvlConversion conversion, string name)
    {
        Assert.True(conversion.HasOutput, conversion.Report.ToText());
        string path = Path.Combine(_folder, name);
        var files = conversion.WriteTo(path);
        Assert.Equal(Path.GetFullPath(path), files[0]);
        return (conversion, RfgReader.ReadFile(path));
    }

    private (LvlConversion Conversion, RfGroupFile File) LevelToRfg(RfLevel level, LvlConvertOptions? options = null) =>
        ToRfg(LvlConverter.Convert(LvlConvertInput.FromLevel(level), (options ?? new LvlConvertOptions()) with { Target = LvlConvertTarget.Rfg }), "level.rfg");

    /// <summary>A brush as a group file stores a level brush: no rooms, portals or lightmap placements.</summary>
    private static RfBrush AsGroupBrush(RfBrush b) => b with
    {
        Geometry = b.Geometry with
        {
            Surfaces = [],
            Faces = [.. b.Geometry.Faces.Select(f => f with { Surface = -1, Room = -1, PortalPlus2 = 0, Corners = [.. f.Corners.Select(c => c with { LightmapUv = null })] })],
        },
    };

    [Fact]
    public void Level_brushes_groups_and_objects_go_to_a_group_file()
    {
        var level = SampleLevel();
        var (conversion, file) = LevelToRfg(level);

        Assert.False(conversion.Report.HasErrors, conversion.Report.ToText());
        Assert.Equal(300, file.Version);
        Assert.Equal(["synthetic", "walls", "door"], file.Groups.Select(g => g.Name));

        // The ungrouped brush and objects are in the main group, with the Alpine data that can only travel there.
        var main = file.Groups[0];
        Assert.Empty(RfModelDiff.Compare(AsGroupBrush(level.Brushes[0]), Assert.Single(main.Brushes)));
        Assert.Equal(RfAlpineBrushFlags.Breakable, main.Brushes[0].Alpine!.Flags);
        Assert.Equal([21], main.Objects.Items.Select(o => o.Uid));
        Assert.Equal(Turn, Assert.Single(main.Objects.Events).Orientation);
        // The Alpine mesh was in "walls", but Alpine objects always join the first group.
        Assert.Equal([23], main.Objects.AlpineMeshes.Select(o => o.Uid));
        Assert.Single(main.Objects.Coronas);
        // Nav point connections are level data.
        Assert.Empty(Assert.Single(main.Objects.NavPoints).Connections);

        var walls = file.Groups[1];
        Assert.False(walls.IsMoving);
        Assert.Equal([11], walls.Brushes.Select(b => b.Uid));
        Assert.Equal(RfSolidFlags.Air, walls.Brushes[0].SolidFlags);
        Assert.Equal([20], walls.Objects.Lights.Select(o => o.Uid));

        var door = file.Groups[2];
        Assert.Empty(RfModelDiff.Compare(level.Groups[1].Moving, door.Moving));
        Assert.Equal([12], door.Brushes.Select(b => b.Uid));

        var report = conversion.Report;
        Assert.Contains(report.WithCode(LvlRfgCodes.NotInGroups), i => i.Message.Contains("player start") && i.Message.Contains("1 cutscene") && i.Message.Contains("nav point"));
        Assert.Contains(report.WithCode(LvlRfgCodes.Lightmaps), i => i.Message.StartsWith("1 brush"));
        Assert.Contains(report.WithCode(LvlRfgCodes.Written), i => i.Message.Contains("3 groups (1 moving)"));
        Assert.Equal(3, report.Stats.Parts);
    }

    [Fact]
    public void Flagged_brushes_move_to_the_first_group_unless_they_move()
    {
        var level = SampleLevel();
        var brushes = level.Brushes.ToBuilder();
        brushes[1] = brushes[1] with { Alpine = new RfAlpineBrushData(RfAlpineBrushFlags.Geoable, 0, []) };
        brushes[2] = brushes[2] with { Alpine = new RfAlpineBrushData(RfAlpineBrushFlags.NoShadowCast, 0, []) };
        var (conversion, file) = LevelToRfg(level with { Brushes = brushes.ToImmutable() });

        Assert.Equal([10, 11], file.Groups[0].Brushes.Select(b => b.Uid));
        Assert.Equal(RfAlpineBrushFlags.Geoable, file.Groups[0].Brushes[1].Alpine!.Flags);
        Assert.Null(file.Groups.Single(g => g.Name == "door").Brushes[0].Alpine);
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.AlpineData), i => i.Severity == LvlIssueSeverity.Info && i.Message.Contains("moved to the first group"));
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.AlpineData), i => i.Severity == LvlIssueSeverity.Warning);
    }

    [Fact]
    public void The_object_filter_applies_but_moving_groups_keep_their_keyframes()
    {
        var (conversion, file) = LevelToRfg(SampleLevel(), new LvlConvertOptions { Objects = LvlObjectKinds.Lights });
        Assert.Equal(1, file.Groups.Sum(g => g.Objects.Count));
        Assert.Equal(2, file.Groups.Single(g => g.IsMoving).Moving!.Keyframes.Length);
        Assert.Contains(conversion.Report.WithCode(LvlConvertCodes.ObjectsDropped), i => i.Message.Contains("left out by the object filter"));
    }

    [Fact]
    public void Alpine_object_chunks_travel_when_group_files_read_them_the_same()
    {
        uint notes = 0x0AFBAE02, lights = 0x0AFBAE0C;
        var level = SampleLevel() with
        {
            Header = new RfLevelHeader { Version = 305, LevelName = "Alpine" },
            OpaqueSections =
            [
                new RfOpaqueSection(notes, RflLevelReader.SectionName(notes), 100, new byte[] { 0, 0, 0, 0 }, "kept"),
                new RfOpaqueSection(lights, RflLevelReader.SectionName(lights), 200, new byte[] { 0, 0, 0, 0 }, "kept"),
            ],
        };
        var (conversion, file) = LevelToRfg(level);
        Assert.Equal([notes], file.OpaqueChunks.Select(c => c.Id));
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.AlpineData), i => i.Severity == LvlIssueSeverity.Warning && i.Message.Contains("older layout"));

        // A level of the newest Alpine version carries both.
        var (_, newest) = LevelToRfg(level with { Header = level.Header with { Version = 306 } });
        Assert.Equal([notes, lights], newest.OpaqueChunks.Select(c => c.Id));
    }

    [Fact]
    public void Compiled_geometry_becomes_one_detail_brush_per_room()
    {
        var level = Level(compiled: CompiledTwoRooms());
        var (conversion, file) = LevelToRfg(level, new LvlConvertOptions { Geometry = LvlGeometryChoice.Compiled });

        var brushes = file.Groups.Single().Brushes;
        Assert.Equal(2, brushes.Length);
        Assert.All(brushes, b => Assert.Equal(RfSolidFlags.Detail, b.SolidFlags));
        // Room 1 lost its portal and invisible faces to the default face filter.
        Assert.Equal(6, brushes[0].Geometry.Faces.Length);
        Assert.Equal(4, brushes[1].Geometry.Faces.Length);
        Assert.Contains(brushes[1].Geometry.Faces, f => f.Scroll == new Vector2(0.5f, -0.25f));
        // Each brush sits at its room's centre.
        Assert.Equal(new Vector3(10, 0, 0), brushes[1].Position);
        Assert.Contains(conversion.Report.WithCode(LvlConvertCodes.GeometryChoice), i => i.Message.Contains("detail brushes"));
    }

    [Fact]
    public void Group_files_are_rewritten_with_the_object_filter()
    {
        var source = RfgWriterTests.Rich();
        var conversion = LvlConverter.Convert(LvlConvertInput.FromGroupFile(source with { Version = 200 }),
            new LvlConvertOptions { Target = LvlConvertTarget.Rfg, Objects = LvlObjectKinds.All & ~LvlObjectKinds.Events });
        var (_, file) = ToRfg(conversion, "group.rfg");
        Assert.Equal(300, file.Version);
        Assert.All(file.Groups, g => Assert.Empty(g.Objects.Events));
        Assert.Equal(source.Groups[0].Objects.Lights.Length, file.Groups[0].Objects.Lights.Length);
        Assert.Empty(RfModelDiff.Compare(source.Groups[0].Brushes, file.Groups[0].Brushes));
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.Written), i => i.Message.Contains("version 200"));
    }

    // ── glTF ────────────────────────────────────────────────────────────────

    /// <summary>The faces of two brushes match: same corners in the same order, flags, smoothing, ids, scroll and textures (matched by centre).</summary>
    internal static void AssertSameFaces(RfBrush expected, RfBrush actual, float tolerance, bool faceIds = true, bool startCorner = true)
    {
        Assert.Equal(expected.Geometry.Faces.Length, actual.Geometry.Faces.Length);
        Vector3 At(RfBrush b, RfCorner c) => b.Geometry.Vertices[c.Vertex];
        // The corner of the actual face where the expected face's corners start, or -1 when its corners do not match
        // in order (faces can share a centre: both sides of a plane, for one).
        int Shift(RfFace e, RfFace a)
        {
            if (a.Corners.Length != e.Corners.Length) return -1;
            for (int shift = 0; shift < a.Corners.Length; shift++)
            {
                if (startCorner && shift > 0) break;
                bool all = true;
                for (int k = 0; k < e.Corners.Length && all; k++)
                {
                    var ac = a.Corners[(k + shift) % a.Corners.Length];
                    all = Vector3.Distance(At(expected, e.Corners[k]), At(actual, ac)) <= tolerance && Vector2.Distance(e.Corners[k].Uv, ac.Uv) <= 1e-4f;
                }
                if (all) return shift;
            }
            return -1;
        }
        var used = new HashSet<int>();
        foreach (var e in expected.Geometry.Faces)
        {
            int match = -1;
            for (int i = 0; i < actual.Geometry.Faces.Length && match < 0; i++)
                if (!used.Contains(i) && Shift(e, actual.Geometry.Faces[i]) >= 0 && (!faceIds || actual.Geometry.Faces[i].FaceId == e.FaceId)) match = i;
            Assert.True(match >= 0, $"no face matches the one starting at {At(expected, e.Corners[0])} ({e.Corners.Length} corners)");
            used.Add(match);
            var a = actual.Geometry.Faces[match];
            Assert.Equal(e.Flags, a.Flags);
            Assert.Equal(e.Smoothing, a.Smoothing);
            Assert.Equal(e.Scroll, a.Scroll);
            if (faceIds) Assert.Equal(e.FaceId, a.FaceId);
            Assert.Equal(expected.Geometry.TextureOf(e), actual.Geometry.TextureOf(a));
        }
    }

    private GltfDocument WrittenGltf(RfLevel level, LvlBrushTransformMode mode)
    {
        var options = new LvlConvertOptions { Target = LvlConvertTarget.Gltf, Geometry = LvlGeometryChoice.Brushes, BrushTransforms = mode, ExportTextures = false, ExcludedFaceClasses = RfFaceClass.Normal };
        string path = Path.Combine(_folder, $"level_{mode}.gltf");
        var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), path, options);
        Assert.True(result.Succeeded, result.Report.ToText());
        return GltfReader.ReadFile(path);
    }

    [Fact]
    public void Gltf_round_trip_restores_brushes_groups_and_objects()
    {
        var level = SampleLevel();
        var doc = WrittenGltf(level, LvlBrushTransformMode.NodeTransform);
        var (conversion, file) = ToRfg(LvlConverter.GltfToRfg(doc, "level.gltf"), "from_gltf.rfg");
        Assert.False(conversion.Report.HasErrors, conversion.Report.ToText());

        var brushes = file.AllBrushes.ToDictionary(b => b.Uid);
        foreach (var source in level.Brushes)
        {
            var b = brushes[source.Uid];
            // The stored transform comes back exactly; so do flags, life, state and the Alpine data.
            Assert.Equal(source.Position, b.Position);
            Assert.Equal(source.Orientation, b.Orientation);
            Assert.Equal(source.SolidFlags, b.SolidFlags);
            Assert.Equal(source.Life, b.Life);
            Assert.Equal(source.State, b.State);
            Assert.Empty(RfModelDiff.Compare(source.Alpine, b.Alpine));
            AssertSameFaces(AsGroupBrush(source), b, 1e-5f);
        }
        Assert.Equal(["synthetic", "walls", "door"], file.Groups.Select(g => g.Name));
        Assert.Equal([10], file.Groups[0].Brushes.Select(b => b.Uid));
        Assert.Empty(RfModelDiff.Compare(level.Groups[1].Moving, file.Groups[2].Moving));

        // Every object's fields come back as stored (nav point connections are level data and stay behind).
        var objects = file.Groups.SelectMany(g => g.Objects.All()).ToDictionary(o => o.Uid);
        foreach (var o in level.Objects.All())
            Assert.Empty(RfModelDiff.Compare(o is RfNavPoint n ? n with { Connections = [] } : o, objects[o.Uid]));
        Assert.Equal([20], file.Groups[1].Objects.Lights.Select(l => l.Uid));
    }

    [Fact]
    public void Gltf_with_applied_transforms_restores_the_placement()
    {
        var level = SampleLevel();
        var (_, file) = ToRfg(LvlConverter.GltfToRfg(WrittenGltf(level, LvlBrushTransformMode.Apply), "level.gltf"), "applied.rfg");
        foreach (var source in level.Brushes)
        {
            var b = file.AllBrushes.Single(x => x.Uid == source.Uid);
            Assert.Equal(source.Position, b.Position);
            Assert.Equal(source.Orientation, b.Orientation);
            AssertSameFaces(AsGroupBrush(source), b, 1e-4f);
        }
    }

    [Fact]
    public void Moved_gltf_nodes_take_their_new_placement()
    {
        var level = SampleLevel();
        var doc = WrittenGltf(level, LvlBrushTransformMode.NodeTransform);
        var brushNode = doc.Nodes.First(n => GltfExtras.TryGetInt(n.Extras, LvlGltfKeys.BrushUid, out int u) && u == 11);
        brushNode.Translation = new Vector3(-1, 2, 3);
        brushNode.Scale = new Vector3(2, 2, 2);
        var itemNode = doc.Nodes.First(n => GltfExtras.TryGetInt(n.Extras, LvlGltfKeys.Uid, out int u) && u == 21);
        itemNode.Translation = new Vector3(-7, 0, 0);

        var conversion = LvlConverter.GltfToRfg(doc, "moved.gltf");
        var (_, file) = ToRfg(conversion, "moved.rfg");
        var b = file.AllBrushes.Single(x => x.Uid == 11);
        Assert.Equal(new Vector3(1, 2, 3), b.Position);
        // The scale is baked: the 2 m box became a 4 m one.
        Assert.Equal(4f, b.Geometry.Vertices.Max(v => v.X), 4);
        Assert.Equal(new Vector3(7, 0, 0), file.AllBrushes.Any() ? file.Groups.SelectMany(g => g.Objects.Items).Single().Position : default);
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.TransformBaked), i => i.Message.Contains("scale"));
    }

    /// <summary>A hand-made glTF: a closed cube on a scaled, turned node, an open quad, a double-sided triangle and a light.</summary>
    private static GltfDocument ForeignGltf()
    {
        var doc = new GltfDocument();
        var buffer = new GltfBufferBuilder(doc);
        int Prim(Vector3[] positions, int[] indices, int material)
        {
            var mesh = new GltfMesh();
            var prim = new GltfPrimitive { Material = material };
            prim.Attributes["POSITION"] = buffer.AddVector3(positions, minMax: true);
            prim.Attributes["TEXCOORD_0"] = buffer.AddVector2([.. positions.Select(p => new Vector2(p.X, p.Z))]);
            prim.Indices = buffer.AddIndices(indices);
            mesh.Primitives.Add(prim);
            doc.Meshes.Add(mesh);
            return doc.Meshes.Count - 1;
        }
        // A unit cube wound counter-clockwise seen from outside (glTF's front faces).
        Vector3[] cube = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        int[] cubeIndices = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 6, 2, 3, 7, 6, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];
        doc.Materials.Add(new GltfMaterial { Name = "brick.tga" });
        doc.Materials.Add(new GltfMaterial { Name = "glass", DoubleSided = true });
        doc.Materials.Add(new GltfMaterial { Name = "Material.001", Extensions = new() { ["KHR_materials_unlit"] = new System.Text.Json.Nodes.JsonObject() } });
        doc.Nodes.Add(new GltfNode { Name = "Cube", Mesh = Prim(cube, cubeIndices, 0), Translation = new Vector3(4, 0, 0), Scale = new Vector3(1, 2, 1), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f) });
        doc.Nodes.Add(new GltfNode { Name = "Plane", Mesh = Prim([new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)], [0, 2, 1, 0, 3, 2], 2) });
        doc.Nodes.Add(new GltfNode { Name = "Pane", Mesh = Prim([new(0, 0, 5), new(1, 0, 5), new(0, 1, 5)], [0, 1, 2], 1) });
        doc.Nodes.Add(new GltfNode { Name = "Lamp", Translation = new Vector3(0, 3, 0), Extensions = new() { ["KHR_lights_punctual"] = new System.Text.Json.Nodes.JsonObject { ["light"] = 0 } } });
        doc.Extensions = new() { ["KHR_lights_punctual"] = new System.Text.Json.Nodes.JsonObject { ["lights"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { ["type"] = "point", ["color"] = new System.Text.Json.Nodes.JsonArray(1.0, 0.5, 0.0), ["range"] = 12.0 }) } };
        buffer.Finish();
        var scene = new GltfScene();
        scene.Nodes.AddRange([0, 1, 2, 3]);
        doc.Scenes.Add(scene);
        doc.Scene = 0;
        return doc;
    }

    [Fact]
    public void Foreign_gltf_meshes_go_through_the_preflight()
    {
        var conversion = LvlConverter.GltfToRfg(ForeignGltf(), "scene.glb");
        var (_, file) = ToRfg(conversion, "foreign.rfg");
        var g = Assert.Single(file.Groups);
        Assert.Equal("scene", g.Name);
        Assert.Equal(3, g.Brushes.Length);

        var cube = g.Brushes[0];
        Assert.Equal(RfSolidFlags.None, cube.SolidFlags);              // closed and outward: a solid brush
        Assert.Equal(new Vector3(-4, 0, 0), cube.Position);           // X mirrored into RF space
        Assert.Equal(2f, cube.Geometry.Vertices.Max(v => v.Y), 4);     // the node's scale is baked
        Assert.Equal(12, cube.Geometry.Faces.Length);
        Assert.Equal("brick.tga", cube.Geometry.Textures.Single());
        var (closed, volume) = RfgBrushBuilder.Closure(cube.Geometry);
        Assert.True(closed);
        Assert.True(volume > 0);

        var plane = g.Brushes[1];
        Assert.Equal(RfSolidFlags.Detail, plane.SolidFlags);          // open: a detail brush
        Assert.All(plane.Geometry.Faces, f => Assert.Equal(RfFaceFlags.FullBright, f.Flags));
        Assert.Equal("Material.001.tga", plane.Geometry.Textures.Single());
        // The quad sits at the centre of its bounds, its faces still pointing up (RF Y up).
        Assert.Equal(new Vector3(-0.5f, 0, 0.5f), plane.Position);
        Assert.All(plane.Geometry.Faces, f => Assert.True(f.Plane!.Value.Normal.Y > 0.99f));

        var pane = g.Brushes[2];
        Assert.Equal(2, pane.Geometry.Faces.Length);                   // double-sided: written both ways
        var light = Assert.Single(g.Objects.Lights);
        Assert.Equal(new Vector3(0, 3, 0), light.Position);
        Assert.Equal(12f, light.Range);
        Assert.Equal(new RflColor(255, 128, 0, 255), light.Color);

        var r = conversion.Report;
        Assert.Contains(r.WithCode(LvlRfgCodes.OpenMesh), i => i.Message.Contains("solid brushes"));
        Assert.Contains(r.WithCode(LvlRfgCodes.OpenMesh), i => i.Message.Contains("detail brushes"));
        Assert.Contains(r.WithCode(LvlRfgCodes.TransformBaked), _ => true);
        Assert.Contains(r.WithCode(LvlRfgCodes.DoubleSided), _ => true);
        Assert.Contains(r.WithCode(LvlConvertCodes.Lights), _ => true);
        Assert.Contains(r.WithCode(LvlRfgCodes.TextureNames), _ => true);
    }

    [Fact]
    public void Polygons_are_rebuilt_from_their_triangles()
    {
        // A convex pentagon cut by ear clipping, and two triangles that do not share an edge.
        var tris = ObjTriangulator.Triangulate([new(0, 0, 0), new(2, 0, 0), new(3, 1, 0), new(1, 2, 0), new(-1, 1, 0)]).Select(t => (t.A, t.B, t.C)).ToList();
        Assert.Equal([0, 1, 2, 3, 4], RfgFromGltf.Polygon(tris));
        Assert.Null(RfgFromGltf.Polygon([(0, 1, 2), (3, 4, 5)]));
    }

    // ── OBJ ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Obj_round_trip_restores_brush_data()
    {
        var level = SampleLevel();
        string path = Path.Combine(_folder, "level.obj");
        var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), path,
            new LvlConvertOptions { Target = LvlConvertTarget.Obj, Geometry = LvlGeometryChoice.Brushes, ExportTextures = false, ExcludedFaceClasses = RfFaceClass.Normal });
        Assert.True(result.Succeeded, result.Report.ToText());

        var conversion = LvlConverter.FileToRfg(path);
        var (_, file) = ToRfg(conversion, "from_obj.rfg");
        Assert.False(conversion.Report.HasErrors, conversion.Report.ToText());
        foreach (var source in level.Brushes)
        {
            var b = file.AllBrushes.Single(x => x.Uid == source.Uid);
            Assert.True(Vector3.Distance(source.Position, b.Position) < 1e-5f);
            Assert.Equal(source.SolidFlags, b.SolidFlags);
            Assert.Equal(source.Life, b.Life);
            Assert.Equal(source.State, b.State);
            Assert.Equal(source.Alpine?.Flags, b.Alpine?.Flags);
            // OBJ keeps no face ids; the vertices went through world space, so they are close, not equal.
            AssertSameFaces(AsGroupBrush(source), b, 1e-4f, faceIds: false);
        }
        // Group names come back; OBJ has no keyframes, so the door does not move.
        Assert.Equal([11], file.Groups.Single(g => g.Name == "walls").Brushes.Select(b => b.Uid));
        Assert.False(file.Groups.Single(g => g.Name == "door").IsMoving);
    }

    [Fact]
    public void Foreign_obj_parts_become_brushes()
    {
        // A Blender-style cube (counter-clockwise from outside, right-handed) and a 3ds Max-style open quad group.
        var obj = """
            mtllib scene.mtl
            o Cube
            v -1 -1 -1
            v 1 -1 -1
            v 1 1 -1
            v -1 1 -1
            v -1 -1 1
            v 1 -1 1
            v 1 1 1
            v -1 1 1
            vt 0 0
            vt 1 0
            vt 1 1
            vt 0 1
            usemtl stone
            s 1
            f 1/1 4/4 3/3 2/2
            f 5/1 6/2 7/3 8/4
            f 1/1 2/2 6/3 5/4
            f 4/1 8/4 7/3 3/2
            f 1/1 5/2 8/3 4/4
            f 2/1 3/2 7/3 6/4
            g floor
            v 0 -2 0
            v 4 -2 0
            v 4 -2 4
            v 0 -2 4
            usemtl lit
            f 9/1 12/4 11/3 10/2
            """;
        var mtl = """
            newmtl stone
            map_Kd textures/stone_wall.png
            newmtl lit
            illum 0
            map_Kd ..\maps\floor01.tga
            """;
        var conversion = LvlConverter.ObjToRfg(ObjReader.Read(obj, "scene.obj"), ObjReader.ReadMtl(mtl, "scene.mtl"), "scene.obj");
        var (_, file) = ToRfg(conversion, "scene.rfg");
        var brushes = Assert.Single(file.Groups).Brushes;
        Assert.Equal(2, brushes.Length);
        Assert.Equal(RfSolidFlags.None, brushes[0].SolidFlags);
        Assert.Equal("stone_wall.tga", brushes[0].Geometry.Textures.Single());
        Assert.All(brushes[0].Geometry.Faces, f => Assert.Equal(1u, f.Smoothing));
        Assert.True(RfgBrushBuilder.Closure(brushes[0].Geometry).Volume > 0);
        Assert.Equal(RfSolidFlags.Detail, brushes[1].SolidFlags);
        Assert.Equal("floor01.tga", brushes[1].Geometry.Textures.Single());
        Assert.Equal(RfFaceFlags.FullBright, brushes[1].Geometry.Faces.Single().Flags);
        Assert.True(brushes[1].Geometry.Faces.Single().Plane!.Value.Normal.Y > 0.99f);
        // V flipped: OBJ's (0, 0) is RF's (0, 1).
        Assert.Contains(brushes[1].Geometry.Faces.Single().Corners, c => c.Uv == new Vector2(0, 1));
    }

    // ── Meshes ──────────────────────────────────────────────────────────────

    [Fact]
    public void Meshes_become_one_brush_per_submesh()
    {
        var level = SampleLevel();
        var v3m = LvlConverter.Convert(LvlConvertInput.FromLevel(level),
            new LvlConvertOptions { Target = LvlConvertTarget.V3m, Geometry = LvlGeometryChoice.Brushes, ExcludedFaceClasses = RfFaceClass.Normal }, "level.v3m");
        Assert.True(v3m.HasOutput, v3m.Report.ToText());
        string path = Path.Combine(_folder, "level.v3m");
        v3m.WriteTo(path);

        var conversion = LvlConverter.FileToRfg(path);
        var (_, file) = ToRfg(conversion, "from_v3m.rfg");
        var brushes = Assert.Single(file.Groups).Brushes;
        Assert.Equal(3, brushes.Length);
        // Closed boxes made of triangles: solid brushes, 12 triangle faces each, placed at the submesh offsets.
        Assert.All(brushes, b => Assert.Equal(RfSolidFlags.None, b.SolidFlags));
        Assert.All(brushes, b => Assert.Equal(12, b.Geometry.Faces.Length));
        foreach (var source in level.Brushes)
            Assert.Contains(brushes, b => Vector3.Distance(b.Position, source.Position) < 1e-4f);
        Assert.Contains(brushes[0].Geometry.Faces, f => f.Flags == RfFaceFlags.FullBright);
    }

    [Fact]
    public void Double_sided_mesh_triangles_are_written_both_ways()
    {
        var group = new V3dMaterialGroup
        {
            Vertices = [new V3dMeshVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero), new V3dMeshVertex(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX), new V3dMeshVertex(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY)],
            Triangles = [new V3dMeshTriangle(0, 1, 2, V3dTriangle.DoubleSided)],
        };
        var mesh = new V3dMeshDescription
        {
            Kind = V3dKind.StaticMesh,
            Submeshes = [new V3dSubmeshDescription { Offset = new Vector3(1, 2, 3), Lods = [new V3dLodDescription { Groups = [group] }], Materials = [new V3dMaterial(Formats.FixedString.FromText("leaf.tga", 32), 0, 0, 0, 0, Formats.FixedString.FromText("", 32), 0)] }],
        };
        var conversion = LvlConverter.MeshToRfg(mesh, "leaf.v3m");
        var (_, file) = ToRfg(conversion, "leaf.rfg");
        var brush = file.Groups.Single().Brushes.Single();
        Assert.Equal(new Vector3(1, 2, 3), brush.Position);
        Assert.Equal(2, brush.Geometry.Faces.Length);
        Assert.Equal(RfSolidFlags.Detail, brush.SolidFlags);
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.DoubleSided), _ => true);
    }

    // ── Pre-flight ──────────────────────────────────────────────────────────

    private static RfgSourceFace Face(string texture, params Vector3[] corners) => new([.. corners.Select(p => new RfgSourceCorner(p, new Vector2(p.X, p.Z)))], texture);

    [Fact]
    public void Preflight_drops_degenerate_faces_and_splits_large_ones()
    {
        var report = new LvlReportBuilder();
        var builder = new RfgBrushBuilder(RfgBrushKind.Auto, report);
        var source = new RfgSourceBrush { Name = "test" };
        source.Faces.Add(Face("a.tga", Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2));                // no area
        source.Faces.Add(Face("a.tga", Vector3.Zero, Vector3.UnitX, Vector3.UnitX, Vector3.UnitZ));      // repeated corner: kept as a triangle
        source.Faces.Add(Face("a.tga", Vector3.Zero, Vector3.UnitX, Vector3.UnitZ, Vector3.UnitX));      // a corner used twice
        var circle = Enumerable.Range(0, 20).Select(i => new Vector3(MathF.Cos(-i * MathF.Tau / 20), 5, MathF.Sin(-i * MathF.Tau / 20))).ToArray();
        source.Faces.Add(Face("b.tga", circle));
        var brush = builder.Build(source, 1)!;
        builder.Summarise();
        var built = report.Build();

        Assert.Equal(1 + 18, brush.Geometry.Faces.Length);
        Assert.Equal(3, brush.Geometry.Faces[0].Corners.Length);
        Assert.All(brush.Geometry.Faces, f => Assert.True(f.Corners.Length <= RfgBrushBuilder.MaxFaceCorners));
        Assert.Equal(["a.tga", "b.tga"], brush.Geometry.Textures.ToArray());
        Assert.Contains(built.WithCode(LvlRfgCodes.DegenerateFaces), i => i.Message.EndsWith(": 2 faces.", StringComparison.Ordinal));
        Assert.Contains(built.WithCode(LvlRfgCodes.FacesSplit), i => i.Message.EndsWith(": 1 face.", StringComparison.Ordinal));
        Assert.Equal(RfSolidFlags.Detail, brush.SolidFlags);
    }

    [Theory]
    [InlineData(RfgBrushKind.Auto, false, RfSolidFlags.None, false)]
    [InlineData(RfgBrushKind.Auto, true, RfSolidFlags.Detail, false)]
    [InlineData(RfgBrushKind.Solid, true, RfSolidFlags.None, true)]
    [InlineData(RfgBrushKind.Detail, false, RfSolidFlags.Detail, false)]
    public void Brush_kind_decides_solid_or_detail(RfgBrushKind kind, bool inward, RfSolidFlags expected, bool warns)
    {
        var report = new LvlReportBuilder();
        var builder = new RfgBrushBuilder(kind, report);
        var box = Box(Vector3.One, ["x.tga"]);
        var source = new RfgSourceBrush { Name = "box" };
        foreach (var f in box.Faces)
        {
            var corners = f.Corners.Select(c => box.Vertices[c.Vertex]).ToArray();
            if (inward) Array.Reverse(corners);
            source.Faces.Add(Face("x.tga", corners));
        }
        var brush = builder.Build(source, 1)!;
        builder.Summarise();
        Assert.Equal(expected, brush.SolidFlags);
        Assert.Equal(warns, report.Build().Issues.Any(i => i.Code == LvlRfgCodes.OpenMesh && i.Severity == LvlIssueSeverity.Warning));
    }

    [Fact]
    public void Empty_sources_are_reported_not_written()
    {
        var conversion = LvlConverter.ObjToRfg(ObjReader.Read("o nothing\n", "empty.obj"), null, "empty.obj");
        Assert.False(conversion.HasOutput);
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.Empty && i.Severity == LvlIssueSeverity.Error);
    }
}
