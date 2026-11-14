using Cairn.Formats;
using Cairn.Formats.XForge;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

/// <summary>Red Faction for N-Gage levels (.xff) through synthetic scenes: space, winding, objects, triggers, the table and the conversions.</summary>
public sealed class XffLevelTests
{
    private static XffTransform At(float x, float y, float z) => new(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);

    /// <summary>
    /// A level of one area: a floor triangle (node at 1, 2, 3; corners (0, 0, 0), (1, 0, 0), (0, 0, 1), seen from
    /// above), a room box, a portal box to a second area, the player, a guard, a health pack, a left-out robot, a
    /// placed door, a spawn point with its enemy-group volume, a script trigger, the level end and an unknown volume.
    /// </summary>
    internal static byte[] Level(out uint floorNode)
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        uint area2 = x.Marker(XffMarkers.Area, root, "room02", At(10, 0, 0));
        floorNode = x.MeshObject(area, "floor",
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ],
            [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY],
            [0, 1, 2],
            [new XffColour(10, 20, 30, 0), new XffColour(40, 50, 60, 0), new XffColour(70, 80, 90, 0)],
            [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)],
            "txtr_floor.pcx", At(1, 2, 3));
        x.MeshObject(area2, "wall", [Vector3.Zero, Vector3.UnitY, Vector3.UnitZ], [Vector3.UnitX, Vector3.UnitX, Vector3.UnitX], [0, 1, 2], null,
            [Vector2.Zero, Vector2.UnitX, Vector2.UnitY], "txtr_wall.tga");
        x.Property(x.CollisionBox(area, "col_room01", new Vector3(-1), new Vector3(2)), "room");
        x.Property(x.CollisionBox(area, "col_portal", new Vector3(3, 0, 0), new Vector3(4, 1, 1)), "portal:room02");
        x.Prefab(area, "LD_player", At(0, 0, 1), "player.xff");
        x.Prefab(area, "LD_GUARD01", At(1, 0, 0), "enemy_ug.xff");
        x.Prefab(area, "LD_hp", At(0, 0.5f, 0), "item_healthpack.xff");
        x.Prefab(area, "LD_lbot", At(0, 0, 0), "enemy_lrbt.xff");
        x.Prefab(area, "LD_door", At(2, 0, 0), "lvl1a_door01.xff");
        x.Marker(4202, area, "enemy01", At(5, 0, 0));
        x.Marker(4202, area, "enemy01", At(5, 0, 1));
        x.Marker(4202, area, "enemy02", At(6, 0, 0));
        uint group = x.Marker(XffMarkers.EnemyGroup, area, "gr_enemy01", At(2, 0, 2));
        x.CollisionBox(group, "col_gr_enemy01", new Vector3(-1, 0, -0.5f), new Vector3(1, 2, 0.5f));
        uint empty = x.Marker(XffMarkers.EnemyGroup, area, "gr_enemy09");
        x.CollisionBox(empty, "col_gr_enemy09", Vector3.Zero, Vector3.One);
        uint script = x.Marker(XffMarkers.ScriptTrigger, area, "gr_entrig");
        x.CollisionBox(script, "col_gr_entrig", Vector3.Zero, Vector3.One);
        uint end = x.Marker(XffMarkers.LevelEnd, area, "gr_levelend_col");
        x.CollisionBox(end, "col_levelend", Vector3.Zero, Vector3.One);
        uint unknown = x.Marker(3117, area, "gr_Col_01");
        x.CollisionBox(unknown, "col_gr_Col_01", Vector3.Zero, Vector3.One);
        return x.ToArray();
    }

    /// <summary>A placed door: one quad (two triangles) with a door marker, as the level-specific door files are.</summary>
    internal static byte[] Door()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        x.Marker(XffMarkers.Door, root, "gr_door01");
        x.MeshObject(root, "door", [Vector3.Zero, Vector3.UnitX, new Vector3(1, 1, 0), Vector3.UnitY], [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ],
            [0, 1, 2, 0, 2, 3], null, [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.Zero, Vector2.One, Vector2.UnitY], "txtr_door.pcx");
        return x.ToArray();
    }

    private static byte[]? Files(string name) => name.ToLowerInvariant() switch
    {
        "lvl1a_door01.xff" => Door(),
        "c01_entrig.txt" => "END"u8.ToArray(),
        _ => null,
    };

    private static RfLevel Read(float scale = XffSpace.RfScale) =>
        XffLevelReader.Read(Level(out _), "level_01a.xff", new XffLevelReadOptions { ReadFile = Files, Scale = scale });

    [Fact]
    public void Positions_are_scaled_into_red_faction_space_with_the_axes_kept()
    {
        var level = Read();
        var g = level.StaticGeometry;
        // the floor's corners (0,0,0), (1,0,0), (0,0,1) at (1,2,3), times 2.35
        var floor = g.Faces.First(f => g.TextureOf(f) == "txtr_floor.pcx");
        var points = floor.Corners.Select(c => g.Vertices[c.Vertex]).ToList();
        static bool Near(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-4f;
        Assert.True(Near(new Vector3(2.35f, 4.7f, 7.05f), points[0]), points[0].ToString());
        Assert.Contains(points, p => Near(p, new Vector3(4.7f, 4.7f, 7.05f)));
        Assert.Contains(points, p => Near(p, new Vector3(2.35f, 4.7f, 9.4f)));
        Assert.Equal(2.35f, level.Xff!.Scale);
    }

    [Fact]
    public void Triangles_are_turned_with_their_uvs_and_face_the_visible_side()
    {
        var level = Read();
        var g = level.StaticGeometry;
        var floor = g.Faces.First(f => g.TextureOf(f) == "txtr_floor.pcx");
        // (A, B, C) becomes (A, C, B): the corner UVs move with their corners
        Assert.Equal(new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 0) }, floor.Corners.Select(c => c.Uv));
        var p = floor.Corners.Select(c => g.Vertices[c.Vertex]).ToArray();
        var normal = Vector3.Normalize(Vector3.Cross(p[1] - p[0], p[2] - p[0]));
        // the XFF normals point down (away from the visible side); the floor faces up in Red Faction
        Assert.True(Vector3.Dot(normal, Vector3.UnitY) > 0.999f);
        Assert.NotNull(floor.Plane);
        Assert.True(Vector3.Dot(floor.Plane!.Value.Normal, Vector3.UnitY) > 0.999f);
        Assert.Equal(RfFaceFlags.None, floor.Flags);
        // baked colours are dropped, with a report line saying to add lights
        Assert.All(floor.Corners, c => Assert.Null(c.Color));
        Assert.Contains(level.Xff!.Notes, n => n.Code == LvlConvertCodes.VertexColors && n.Message.Contains("Add lights"));
    }

    [Fact]
    public void Areas_are_rooms_and_portal_boxes_join_them()
    {
        var level = Read();
        var g = level.StaticGeometry;
        Assert.Equal(new[] { "room01", "room02" }, level.Xff!.Areas);
        Assert.Equal(2, g.Rooms.Length);
        Assert.All(g.Faces.Where(f => g.TextureOf(f) == "txtr_floor.pcx"), f => Assert.Equal(0, f.Room));
        Assert.All(g.Faces.Where(f => g.TextureOf(f) == "txtr_wall.tga"), f => Assert.Equal(1, f.Room));
        var portal = Assert.Single(g.Portals);
        Assert.Equal((0, 1), (portal.RoomA, portal.RoomB));
        Assert.Equal(new Vector3(3, 0, 0) * 2.35f, portal.Point1);
        Assert.Equal(1, level.Xff.RoomBoxes);
        // the room box grows room 0 to (-1, -1, -1) .. (2, 2, 2) metres x 2.35
        Assert.Equal(new Vector3(-2.35f), g.Rooms[0].BoundsMin);
    }

    [Fact]
    public void An_area_that_does_not_close_into_a_room_becomes_an_invisible_air_box_around_its_meshes()
    {
        // room01 is a floor triangle, room02 a wall triangle: neither closes into a room
        var level = Read();
        var air = level.Brushes.Where(b => b.IsAir).ToList();
        Assert.Equal(2, air.Count);
        foreach (var b in air)
        {
            Assert.False(b.IsDetail);
            var (closed, volume) = RfgBrushBuilder.Closure(b.Geometry);
            Assert.True(closed && volume < 0, "a closed box whose faces point in, as RED stores an air brush");
            Assert.Equal(6, b.Geometry.Faces.Length);
            Assert.All(b.Geometry.Faces, f => Assert.Equal(RfFaceFlags.Invisible, f.Flags));
            Assert.All(b.Geometry.Faces, f => Assert.Equal(XffLevelReader.AirTexture, b.Geometry.TextureOf(f)));
        }
        // every corner of the meshes is inside an air brush, at least the margin from its faces
        var g = level.StaticGeometry;
        Assert.All(g.Vertices, v => Assert.Contains(air, b => Holds(b, v)));
        // room 0 is its room box (-1 .. 2 x 2.35) and the meshes in it, grown by the margin
        var first = Bounds(air[0]);
        Assert.True(Vector3.Distance(new Vector3(-2.35f - XffLevelReader.AirMargin), first.Min) < 1e-4f, first.Min.ToString());
        Assert.Contains(level.Xff!.BrushNotes, n => n.Message.Contains("2 areas' walls do not close into a room") && n.Message.Contains("room01 (more hole than wall)"));
        Assert.DoesNotContain(level.Xff.BrushNotes, n => n.Message.Contains("became air brushes"));

        static bool Holds(RfBrush b, Vector3 v)
        {
            var (min, max) = Bounds(b);
            var m = new Vector3(XffLevelReader.AirMargin - 1e-3f);
            return Vector3.Min(v, min + m) == min + m && Vector3.Max(v, max - m) == max - m;
        }
    }

    private static (Vector3 Min, Vector3 Max) Bounds(RfBrush b) =>
        (b.Position + b.Geometry.Vertices.Aggregate(Vector3.Min), b.Position + b.Geometry.Vertices.Aggregate(Vector3.Max));

    /// <summary>
    /// Two box rooms side by side, seen from inside: room01 from (0, 0, 0) to (2, 1, 1) without its +X wall, room02 from
    /// (2, 0, 0) to (4, 1, 1) without its -X wall, a portal box between them when <paramref name="portal"/>; a crate in
    /// room01. Without <paramref name="second"/> only room01 and its crate.
    /// </summary>
    private static byte[] Rooms(bool second, bool portal)
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        InsideOfBox(x, area, "walls01", Vector3.Zero, new Vector3(2, 1, 1), skipAxis: 0, skipSide: 1, "txtr_wall.tga");
        // a crate (outward faces): becomes a detail brush, not part of the room
        var crate = BoxFaces(new Vector3(0.5f, 0, 0.5f), new Vector3(0.7f, 0.2f, 0.7f), inward: false, -1, 0);
        x.MeshObject(area, "crate", crate.Positions, [.. crate.Positions.Select(_ => Vector3.UnitY)], crate.Indices, null, crate.Uvs, "txtr_crate.tga");
        if (second)
        {
            uint area2 = x.Marker(XffMarkers.Area, root, "room02");
            InsideOfBox(x, area2, "walls02", new Vector3(2, 0, 0), new Vector3(4, 1, 1), skipAxis: 0, skipSide: -1, "txtr_wall2.tga");
        }
        if (portal) x.Property(x.CollisionBox(area, "col_portal", new Vector3(1.9f, -0.1f, -0.1f), new Vector3(2.1f, 1.1f, 1.1f)), "portal:room02");
        return x.ToArray();

        static void InsideOfBox(SyntheticXff x, uint parent, string name, Vector3 min, Vector3 max, int skipAxis, int skipSide, string texture)
        {
            var box = BoxFaces(min, max, inward: true, skipAxis, skipSide);
            x.MeshObject(parent, name, box.Positions, [.. box.Positions.Select(_ => Vector3.UnitY)], box.Indices, null, box.Uvs, texture);
        }
    }

    /// <summary>The faces of a box (one side left out when <paramref name="skipAxis"/> is 0..2), wound so that in Red Faction space they face in or out.</summary>
    private static (List<Vector3> Positions, List<ushort> Indices, List<Vector2> Uvs) BoxFaces(Vector3 min, Vector3 max, bool inward, int skipAxis, int skipSide)
    {
        var positions = new List<Vector3>();
        var indices = new List<ushort>();
        var uvs = new List<Vector2>();
        float[] lo = [min.X, min.Y, min.Z], hi = [max.X, max.Y, max.Z];
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            foreach (int side in new[] { -1, 1 })
            {
                if (axis == skipAxis && side == skipSide) continue;
                // in this (u, v) order the quad faces +axis; a face of the inside faces -side, of the outside +side
                (int A, int B)[] square = [(0, 0), (1, 0), (1, 1), (0, 1)];
                if ((inward ? -side : side) < 0) Array.Reverse(square);
                int first = positions.Count;
                foreach (var (a, b) in square)
                {
                    var p = new float[3];
                    p[axis] = side < 0 ? lo[axis] : hi[axis];
                    p[u] = a == 0 ? lo[u] : hi[u];
                    p[v] = b == 0 ? lo[v] : hi[v];
                    positions.Add(new Vector3(p[0], p[1], p[2]));
                }
                // Red Faction triangles (0, 1, 2) and (0, 2, 3), stored the N-Gage way: (A, B, C) as (A, C, B)
                foreach (int k in new[] { 0, 2, 1, 0, 3, 2 })
                {
                    indices.Add((ushort)(first + k));
                    uvs.Add(new Vector2(square[k].A, square[k].B));
                }
            }
        }
        return (positions, indices, uvs);
    }

    [Fact]
    public void An_area_that_closes_becomes_an_air_brush_of_its_own_walls_with_tunnels_at_its_doorways()
    {
        foreach (bool portal in new[] { true, false })
        {
            // with the portal box, or found by the next area's corners at the hole
            var level = XffLevelReader.Read(Rooms(second: true, portal), "level_01a.xff");
            var air = level.Brushes.Where(b => b.IsAir).ToList();
            Assert.Equal(2, air.Count);
            foreach (var b in air)
            {
                Assert.False(b.IsDetail);
                Assert.True(RfgBrushBuilder.Closure(b.Geometry) is (true, < 0), "closed, its faces pointing in, as RED stores an air brush");
                // the room's own 5 walls (10 triangles, their texture kept), then a tunnel of 4 sides carrying on the walls
                // beside them and an invisible end
                var own = b.Geometry.Faces.Where(f => (f.Flags & RfFaceFlags.Invisible) == 0).ToList();
                Assert.Equal(14, own.Count);
                Assert.All(own, f => Assert.StartsWith("txtr_wall", b.Geometry.TextureOf(f)));
                Assert.Equal(1, b.Geometry.Faces.Count(f => f.Flags == RfFaceFlags.Invisible && b.Geometry.TextureOf(f) == XffLevelReader.AirTexture));
                // the sides carry on the walls beside them: 0.25 m more of a 4.7 m wall the texture spans once
                var g = b.Geometry;
                bool At(Vector3 p, float x) => MathF.Abs(p.X + b.Position.X - x) < 1e-3f;
                var sides = own.Where(f => f.Corners.Any(c => At(g.Vertices[c.Vertex], 4.7f + XffAreaShell.HoleDepth) || At(g.Vertices[c.Vertex], 4.7f - XffAreaShell.HoleDepth))).ToList();
                Assert.Equal(4, sides.Count);
                foreach (var f in sides)
                    foreach (var c in f.Corners.Where(c => !At(g.Vertices[c.Vertex], 4.7f)))
                    {
                        var d = f.Corners.Single(o => Vector3.Distance(g.Vertices[o.Vertex], g.Vertices[c.Vertex]) is > 0.2f and < 0.3f);
                        Assert.Equal(XffAreaShell.HoleDepth / 4.7f, Vector2.Distance(c.Uv, d.Uv), 3);
                    }
            }
            // the tunnels reach into the next room, so the two air brushes overlap at the doorway (x = 2 x 2.35)
            Assert.Equal(4.7f + XffAreaShell.HoleDepth, Bounds(air[0]).Max.X, 3);
            Assert.Equal(4.7f - XffAreaShell.HoleDepth, Bounds(air[1]).Min.X, 3);
            // the crate is the only other brush, a detail brush
            Assert.Single(level.Brushes, b => !b.IsAir);
            Assert.True(level.Brushes.Single(b => !b.IsAir).IsDetail);
            Assert.Contains(level.Xff!.BrushNotes, n => n.Message.Contains("2 areas became air brushes") && n.Message.Contains("2 doorways"));
        }
    }

    [Fact]
    public void An_area_with_only_props_has_no_air_brush_and_its_props_are_detail_brushes()
    {
        // room02 holds only a crate, inside room01's space: its largest piece faces out, so it is no room
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "room01");
        var walls = BoxFaces(Vector3.Zero, new Vector3(2, 1, 1), inward: true, -1, 0);
        x.MeshObject(area, "walls01", walls.Positions, [.. walls.Positions.Select(_ => Vector3.UnitY)], walls.Indices, null, walls.Uvs, "txtr_wall.tga");
        uint props = x.Marker(XffMarkers.Area, root, "room02");
        var crate = BoxFaces(new Vector3(0.5f, 0, 0.5f), new Vector3(0.7f, 0.2f, 0.7f), inward: false, -1, 0);
        x.MeshObject(props, "crate", crate.Positions, [.. crate.Positions.Select(_ => Vector3.UnitY)], crate.Indices, null, crate.Uvs, "txtr_crate.tga");
        var level = XffLevelReader.Read(x.ToArray(), "level_01a.xff");
        var room = level.Brushes.Single(b => b.IsAir);
        Assert.Equal(12, room.Geometry.Faces.Length);
        Assert.True(RfgBrushBuilder.Closure(room.Geometry) is (true, < 0));
        Assert.True(level.Brushes.Single(b => !b.IsAir).IsDetail);
        Assert.Contains(level.Xff!.BrushNotes, n => n.Message.Contains("1 area has no walls of its own") && n.Message.Contains("room02"));
        Assert.DoesNotContain(level.Xff.BrushNotes, n => n.Message.Contains("wholly beyond"));
    }

    [Fact]
    public void A_hole_that_leads_nowhere_becomes_a_recess_textured_like_the_wall_beside_it()
    {
        var level = XffLevelReader.Read(Rooms(second: false, portal: false), "level_01a.xff");
        var room = level.Brushes.Single(b => b.IsAir);
        Assert.True(RfgBrushBuilder.Closure(room.Geometry) is (true, < 0));
        Assert.DoesNotContain(room.Geometry.Faces, f => (f.Flags & RfFaceFlags.Invisible) != 0);
        Assert.Equal(15, room.Geometry.Faces.Length);
        Assert.All(room.Geometry.Faces, f => Assert.Equal("txtr_wall.tga", room.Geometry.TextureOf(f)));
        Assert.Equal(4.7f + XffAreaShell.HoleDepth, Bounds(room).Max.X, 3);
        Assert.Contains(level.Xff!.BrushNotes, n => n.Message.Contains("1 other hole") && n.Message.Contains("recesses"));
    }

    [Fact]
    public void Each_mesh_node_becomes_one_brush_and_placed_doors_are_static_geometry()
    {
        var level = Read();
        // floor, wall and the placed door's mesh (then the air brushes)
        var meshes = level.Brushes.Where(b => !b.IsAir).ToList();
        Assert.Equal(3, meshes.Count);
        Assert.All(meshes, b => Assert.True(b.IsDetail, "a mesh becomes a detail brush"));
        Assert.Contains("txtr_door.pcx", level.StaticGeometry.Textures);
        Assert.Contains("lvl1a_door01.xff (door)", level.Xff!.PlacedGeometry);
        Assert.Contains(level.Xff.Notes, n => n.Message.Contains("cannot become movers"));
        Assert.Empty(level.Xff.MissingFiles);
        Assert.Equal(4, level.Xff.Triangles);
    }

    [Fact]
    public void Placed_files_that_are_not_found_are_reported()
    {
        var level = XffLevelReader.Read(Level(out _), "level_01a.xff");
        Assert.Contains("lvl1a_door01.xff", level.Xff!.MissingFiles);
        Assert.Contains(level.Xff.Notes, n => n.Severity == LvlIssueSeverity.Warning && n.Message.Contains("lvl1a_door01.xff"));
        Assert.Null(Assert.Single(level.Xff.Scripts).Found);
    }

    [Fact]
    public void Objects_are_mapped_through_the_table()
    {
        var level = Read();
        var o = level.Objects;
        var guard = Assert.Single(o.Entities, e => !e.StartHidden);
        Assert.Equal("guard1", guard.ClassName);
        Assert.Equal(new Vector3(2.35f, 0, 0), guard.Position);
        var item = Assert.Single(o.Items);
        Assert.Equal(("Medical Kit", 25), (item.ClassName, item.Count));
        Assert.NotNull(level.PlayerStart);
        Assert.Equal(new Vector3(0, 0, 2.35f), level.PlayerStart!.Position);
        Assert.Equal(RfMatrix.Identity, level.PlayerStart.Orientation);
    }

    [Fact]
    public void Unmapped_objects_and_markers_are_reported_not_guessed()
    {
        var level = Read();
        var unmapped = level.Xff!.Unmapped;
        Assert.Contains(unmapped, u => u.What == "enemy_lrbt.xff" && u.Count == 1 && u.Reason.Contains("no Red Faction counterpart"));
        Assert.Contains(unmapped, u => u.What == "marker 3117" && u.Names.Contains("gr_Col_01"));
        Assert.Contains(level.Xff.Notes, n => n.Severity == LvlIssueSeverity.Warning && n.Message.Contains("enemy_lrbt.xff x1"));
        Assert.DoesNotContain(level.Objects.All(), x => x is RfPlacedObject p && p.ClassName.Length == 0);
    }

    [Fact]
    public void Spawn_points_are_hidden_entities_that_their_volume_unhides()
    {
        var level = Read();
        var o = level.Objects;
        var hidden = o.Entities.Where(e => e.StartHidden).ToList();
        Assert.Equal(3, hidden.Count);
        Assert.All(hidden, e => Assert.Equal("guard1", e.ClassName));
        var trigger = Assert.Single(o.Triggers, t => t.ScriptName == "gr_enemy01");
        var unhide = Assert.Single(o.Events);
        Assert.Equal("UnHide", unhide.ClassName);
        Assert.Equal(new[] { unhide.Uid }, trigger.Links);
        Assert.Equal(hidden.Where(e => e.ScriptName == "enemy01").Select(e => e.Uid).Order(), unhide.Links.Order());
        // the volume's box: (-1, 0, -0.5) .. (1, 2, 0.5) at (2, 0, 2); size kept as height, width, depth
        Assert.Equal(1, trigger.Shape);
        Assert.Equal(new Vector3(2, 1, 2) * 2.35f, trigger.Position);
        Assert.Equal(new Vector3(2, 2, 1) * 2.35f, trigger.Size);
        // enemy02 has no volume; gr_enemy09 names no spawn point
        Assert.Contains(level.Xff!.Notes, n => n.Message.Contains("enemy02") && n.Message.Contains("stay hidden"));
        Assert.Contains(level.Xff.Notes, n => n.Message.Contains("gr_enemy09"));
        Assert.DoesNotContain(o.Triggers, t => t.ScriptName == "gr_enemy09");
        Assert.Equal(new[] { "guard1" }, o.Entities.Where(e => !e.StartHidden).Select(e => e.ClassName));
    }

    [Fact]
    public void Script_triggers_and_the_level_end_become_triggers_with_the_script_named()
    {
        var level = Read();
        Assert.Contains(level.Objects.Triggers, t => t.ScriptName == "gr_entrig" && t.Shape == 1 && t.Links.IsEmpty);
        Assert.Contains(level.Objects.Triggers, t => t.ScriptName == "gr_levelend_col");
        var script = Assert.Single(level.Xff!.Scripts);
        Assert.Equal(("gr_entrig", "C01_entrig.txt", (bool?)true), (script.TriggerName, script.ScriptFile, script.Found));
        Assert.Contains(level.Xff.Notes, n => n.Message.Contains("gr_entrig runs C01_entrig.txt") && n.Message.Contains("cannot run"));
        Assert.Contains(level.Xff.Notes, n => n.Message.Contains("Load_Level"));
    }

    [Fact]
    public void Material_blends_map_to_face_flags()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint area = x.Marker(XffMarkers.Area, root, "a");
        foreach (var (name, src, dst) in new[] { ("alpha", (byte)5, (byte)6), ("add", (byte)2, (byte)2), ("dark", (byte)1, (byte)4) })
        {
            uint node = x.Object(area, name);
            uint material = x.Material(byte0: src, byte1: dst);
            uint vb = x.VertexBuffer([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY]);
            uint il = x.IndexList([0, 1, 2]);
            uint fa = x.FaceAttributes(1, null, [Vector2.Zero, Vector2.UnitX, Vector2.UnitY]);
            uint tex = x.Texture(name + ".tga");
            x.MeshInstance(node, "mesh_" + name, [SyntheticXff.Submesh(vb, il, 3, fa, material, tex)]);
        }
        var level = XffLevelReader.Read(x.ToArray(), "level_99.xff");
        var g = level.StaticGeometry;
        RfFaceFlags Of(string t) => g.Faces.Single(f => g.TextureOf(f) == t).Flags;
        Assert.Equal(RfFaceFlags.HasAlpha, Of("alpha.tga"));
        Assert.Equal(RfFaceFlags.FullBright, Of("add.tga"));
        Assert.Equal(RfFaceFlags.HasAlpha, Of("dark.tga"));
        Assert.Contains(level.Xff!.Notes, n => n.Code == LvlConvertCodes.Transparency && n.Message.Contains("additive"));
    }

    [Fact]
    public void A_model_is_refused_as_a_level()
    {
        var e = Assert.Throws<AssetFormatException>(() => XffLevelReader.Read(Door(), "lvl1a_door01.xff"));
        Assert.Contains("not a level", e.Message);
        Assert.Throws<AssetFormatException>(() => XffLevelReader.Read([1, 2, 3], "broken.xff"));
    }

    [Fact]
    public void The_object_table_loads_and_every_class_name_is_set()
    {
        var table = XffObjectTable.Default;
        Assert.NotEmpty(table.Rows);
        Assert.All(table.Rows.Where(r => r.Kind != XffObjectKind.PlayerStart), r => Assert.False(string.IsNullOrWhiteSpace(r.ClassName), r.FileName));
        Assert.All(table.Rows.Where(r => r.Kind == XffObjectKind.Item), r => Assert.True(r.Count > 0, r.FileName));
        Assert.Equal("Oil Drum", table.Find("EXPLODING_BARREL.XFF")!.ClassName);
        Assert.Equal(XffObjectKind.PlayerStart, table.Find("player.xff")!.Kind);
        Assert.NotNull(table.LeftOutReason("enemy_dopefish.xff"));
        // every enemy type a spawn point can name maps to one class
        Assert.All(table.Rows.Where(r => r.Marker is not null).GroupBy(r => r.Marker), g => Assert.Single(g.Select(r => r.ClassName).Distinct()));
        Assert.Throws<AssetFormatException>(() => XffObjectTable.Parse("""{ "objects": { "a.xff": { "kind": "item", "class": " " } } }"""));
        Assert.Throws<AssetFormatException>(() => XffObjectTable.Parse("""{ "objects": { "a.xff": { "kind": "monster", "class": "x" } } }"""));
        Assert.Throws<AssetFormatException>(() => XffObjectTable.Parse("not json"));
    }

    [Fact]
    public void A_conversion_reads_the_level_again_at_the_chosen_scale()
    {
        var level = Read();
        var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(level), new LvlConvertOptions { Target = LvlConvertTarget.Rfg, Xff = new LvlXffOptions { Scale = 1f } });
        var file = Assert.IsType<RfGroupFile>(conversion.Output);
        var guard = file.Groups.SelectMany(g => g.Objects.Entities).Single(e => !e.StartHidden);
        Assert.Equal(new Vector3(1, 0, 0), guard.Position);
        Assert.Contains(conversion.Report.Issues, i => i.Message.Contains("read at 1 Red Faction metres"));
    }

    [Fact]
    public void Converting_to_a_group_file_keeps_the_brushes_and_objects_and_copies_the_textures()
    {
        var level = Read();
        var folder = Path.Combine(Path.GetTempPath(), "cairn-xff-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "txtr_floor.pcx"), Pcx());
            var resolver = new Cairn.Assets.AssetResolver(new Cairn.Assets.AssetResolverOptions { DocumentFolder = folder });
            var options = new LvlConvertOptions { Target = LvlConvertTarget.Rfg, TextureResolver = resolver };
            var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), Path.Combine(folder, "out", "level_01a.rfg"), options);
            Assert.True(result.Succeeded, result.Report.ToText());
            var back = RfgReader.Read(File.ReadAllBytes(result.Files[0]), "level_01a.rfg");
            Assert.Empty(back.Problems);
            Assert.Equal(level.Brushes.Length, back.AllBrushes.Count());
            Assert.Equal(2, back.AllBrushes.Count(b => b.IsAir && !b.IsDetail));
            Assert.All(back.AllBrushes.Where(b => !b.IsAir), b => Assert.True(b.IsDetail));
            Assert.All(back.AllBrushes.Where(b => b.IsAir).SelectMany(b => b.Geometry.Faces), f => Assert.Equal(RfFaceFlags.Invisible, f.Flags));
            int objects = level.Objects.Count;
            Assert.Equal(objects, back.Groups.Sum(g => g.Objects.Count));
            var trigger = back.Groups.SelectMany(g => g.Objects.Triggers).Single(t => t.ScriptName == "gr_enemy01");
            var unhide = back.Groups.SelectMany(g => g.Objects.Events).Single();
            Assert.Equal(new[] { unhide.Uid }, trigger.Links);
            Assert.Equal(2, unhide.Links.Length);
            Assert.All(back.Groups.SelectMany(g => g.Objects.Entities).Where(e => e.ScriptName.StartsWith("enemy", StringComparison.Ordinal)), e => Assert.True(e.StartHidden));
            // the floor's .pcx is named and written as .tga (Red Faction's editor takes .tga); the others are named as missing
            var textures = back.AllBrushes.SelectMany(b => b.Geometry.Textures).ToList();
            Assert.Contains("txtr_floor.tga", textures);
            Assert.DoesNotContain(textures, t => t.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
            Assert.False(File.Exists(Path.Combine(folder, "out", "txtr_floor.pcx")));
            AssertPcxPixels(File.ReadAllBytes(Path.Combine(folder, "out", "txtr_floor.tga")));
            Assert.Contains(result.Report.Issues, i => i.Code == LvlConvertCodes.XffTextures && i.Message.Contains("written as .tga"));
            Assert.Contains(result.Report.Issues, i => i.Code == LvlConvertCodes.XffTextures && i.Message.Contains("txtr_wall.tga"));
            Assert.Contains(result.Report.Issues, i => i.Message.Contains("player start is at"));
            Assert.Contains(result.Report.Issues, i => i.Code == LvlRfgCodes.OpenMesh && i.Message.Contains("are detail brushes"));
            Assert.Contains(result.Report.Issues, i => i.Code == LvlConvertCodes.XffLevel && i.Message.Contains("invisible air box"));
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>A 2 x 1 PCX, red then blue.</summary>
    private static byte[] Pcx() => Cairn.Formats.Imaging.SyntheticPcx.Write(2, 1, 8, 1, (x, _) => x, [255, 0, 0, 0, 0, 255]);

    /// <summary>A TGA with <see cref="Pcx"/>'s pixels.</summary>
    private static void AssertPcxPixels(byte[] tga)
    {
        var image = Cairn.Formats.Imaging.ImageDecoder.Decode(tga, "copy.tga");
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), image.Get(0, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), image.Get(1, 0));
    }

    /// <summary>A 2 x 2 raw 16-bit TGA declaring one alpha bit, with the pixels given.</summary>
    private static byte[] Tga16(params ushort[] pixels)
    {
        var bytes = new List<byte> { 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 16, 0x21 };
        foreach (ushort p in pixels) { bytes.Add((byte)p); bytes.Add((byte)(p >> 8)); }
        return [.. bytes];
    }

    /// <summary>
    /// The N-Gage game draws a 16-bit texture whose alpha bit no pixel sets as opaque, so the level's copies beside a
    /// group file (or static mesh) say "no alpha" in their header, and the glTF and OBJ PNGs are opaque, as the Meshes
    /// module converts them; a .pcx is written as the .tga the output names.
    /// </summary>
    [Theory]
    [InlineData(LvlConvertTarget.Rfg)]
    [InlineData(LvlConvertTarget.V3m)]
    [InlineData(LvlConvertTarget.Obj)]
    [InlineData(LvlConvertTarget.Gltf)]
    public void Sixteen_bit_textures_whose_alpha_bit_is_never_set_are_converted_opaque(LvlConvertTarget target)
    {
        var level = Read();
        var folder = Path.Combine(Path.GetTempPath(), "cairn-xff-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(folder);
            byte[] wall = Tga16(0x7C00, 0x03E0, 0x001F, 0x7FFF);
            File.WriteAllBytes(Path.Combine(folder, "txtr_wall.tga"), wall);
            File.WriteAllBytes(Path.Combine(folder, "txtr_floor.pcx"), Pcx());
            var resolver = new Cairn.Assets.AssetResolver(new Cairn.Assets.AssetResolverOptions { DocumentFolder = folder });
            var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), Path.Combine(folder, "out", "level_01a" + LvlConvertOptions.ExtensionOf(target)),
                new LvlConvertOptions { Target = target, TextureResolver = resolver });
            Assert.True(result.Succeeded, result.Report.ToText());
            Assert.Contains(result.Report.Issues, i => i.Code == LvlConvertCodes.XffTextures && i.Message.Contains("an alpha bit that no pixel sets (txtr_wall.tga)"));
            if (target is LvlConvertTarget.Rfg or LvlConvertTarget.V3m)
            {
                byte[] copy = File.ReadAllBytes(Path.Combine(folder, "out", "txtr_wall.tga"));
                Assert.Equal(0x20, copy[17]);
                Assert.Equal(wall.AsSpan(18).ToArray(), copy.AsSpan(18).ToArray());
                AssertPcxPixels(File.ReadAllBytes(Path.Combine(folder, "out", "txtr_floor.tga")));
                Assert.False(File.Exists(Path.Combine(folder, "out", "txtr_floor.pcx")));
            }
            else
            {
                var png = Cairn.Formats.Imaging.ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, "out", "txtr_wall.png")), "txtr_wall.png");
                Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(255, png.Pixels[i * 4 + 3]));
            }
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    /// <summary>One scale range for both modules (<see cref="XffSpace.IsValidScale"/>): the reader and the conversion refuse any other, never falling back to 2.35.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(0.009f)]
    [InlineData(100.5f)]
    [InlineData(float.NaN)]
    public void A_scale_out_of_range_is_refused(float scale)
    {
        var e = Assert.Throws<AssetFormatException>(() => Read(scale));
        Assert.Contains("0.01 to 100", e.Message);
        var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(Read()), new LvlConvertOptions { Target = LvlConvertTarget.Rfg, Xff = new LvlXffOptions { Scale = scale } });
        Assert.False(conversion.HasOutput);
        Assert.Contains(conversion.Report.Issues, i => i.Severity == LvlIssueSeverity.Error && i.Code == LvlConvertCodes.XffLevel && i.Message.Contains("0.01 to 100"));
        Assert.Equal(100f, Read(100f).Xff!.Scale);
        Assert.Equal(0.01f, Read(0.01f).Xff!.Scale);
    }

    /// <summary>Depth costs nothing extra: a mesh 20,000 nodes below its area reads (each node's room found once) in well under a second and lies in that room.</summary>
    [Fact]
    public void A_deep_hierarchy_reads_in_linear_time()
    {
        const int depth = 20_000;
        var x = new SyntheticXff();
        uint root = x.Root();
        uint parent = x.Marker(XffMarkers.Area, root, "room01");
        for (int i = 0; i < depth; i++) parent = x.Group(parent, "g" + i);
        x.MeshObject(parent, "floor", [Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [-Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY], [0, 1, 2], null,
            [new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1)], "txtr_floor.pcx");
        byte[] bytes = x.ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(XffKind.Level, XffClassifier.TryClassify(bytes, "level_deep.xff"));
        var level = XffLevelReader.Read(bytes, "level_deep.xff");
        watch.Stop();
        Assert.Equal(0, Assert.Single(level.StaticGeometry.Faces).Room);
        // generous for a loaded machine; walking to the root for every node took seconds here
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"classifying and reading took {watch.ElapsedMilliseconds:N0} ms");
    }

    [Theory]
    [InlineData(LvlConvertTarget.Glb)]
    [InlineData(LvlConvertTarget.Obj)]
    [InlineData(LvlConvertTarget.V3m)]
    public void The_other_targets_convert_the_level(LvlConvertTarget target)
    {
        var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(Read()), new LvlConvertOptions { Target = target });
        Assert.True(conversion.HasOutput, conversion.Report.ToText());
        Assert.False(conversion.Report.HasErrors, conversion.Report.ToText());
        Assert.Equal(4, conversion.Report.Stats.Triangles);
        Assert.Contains(conversion.Report.Issues, i => i.Code == LvlConvertCodes.XffLevel);
    }
}
