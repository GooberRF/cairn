using Cairn.Formats;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

public class RflReaderTests
{
    private static readonly RfMatrix Turned = new(new Vector3(0, 0, -1), Vector3.UnitY, new Vector3(1, 0, 0));

    private static readonly Vector3[] Quad = [new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1)];

    /// <summary>Two faces sharing vertices 1 and 2 with different UVs; the first is lit (lightmap UVs), the second is not.</summary>
    private static LvlBytes.Face[] TwoFaces(uint flags0 = 0x20, uint flags1 = 0x10) =>
    [
        new(0, 0, 7, flags0, [(0, new(0, 0), new(0.1f, 0.1f)), (1, new(1, 0), new(0.2f, 0.1f)), (2, new(1, 1), new(0.2f, 0.2f))], Room: 0, Smoothing: 1),
        new(1, -1, 8, flags1, [(1, new(5, 5), default), (2, new(6, 5), default), (3, new(6, 6), default)], Room: 0, Portal: 3),
    ];

    internal static byte[] StockLevel(int version, Action<LvlBytes>? extra = null)
    {
        var w = LvlBytes.Level(version, "Header name", "");
        w.Section(0x900, s =>
        {
            s.Str("rock02.tga").I32(50).Color(new RflColor(20, 30, 40, 255)).U8(0);
            s.Color(new RflColor(1, 2, 3, 255)).F32(10).F32(100);
        });
        w.Section(0x100, s => s.Solid(["wall.tga", "floor.tga"], Quad, TwoFaces(), [(8, new Vector2(0.5f, -1))],
            rooms: 2, liquidRoom: true, portals: 1, surfaces: 1));
        w.Section(0x300, s => s.I32(2).Light(10, new Vector3(1, 2, 3), 0x18, 12).Light(11, Vector3.Zero, 0x28, 6));
        w.Section(0x600, s => s.I32(2)
            .Event(20, "Teleport", new Vector3(4, 5, 6), Turned, "", 30)
            .Event(21, "Play_Sound", Vector3.Zero, null, "door.wav"));
        w.Section(0x30000, s => s.I32(2).Entity(40, "guard1", Vector3.One, customRange: true).Entity(41, "miner1", Vector3.Zero, customRange: false));
        w.Section(0x40000, s => s.I32(1).Item(50, "Medical Kit", new Vector3(0, 1, 0)));
        w.Section(0x50000, s => s.I32(1).Clutter(60, "barrel", Vector3.UnitZ));
        w.Section(0x60000, s => s.I32(2).Trigger(70, 0, Vector3.Zero).Trigger(71, 1, Vector3.One));
        w.Section(0x20000, s =>
        {
            s.I32(2);
            for (int i = 0; i < 2; i++)
            {
                s.I32(80 + i).U8(0).F32(1).Vec3(new Vector3(i, 0, 0)).F32(0.5f).I32(0).U8(i).Bytes([]);
                if (i == 1) s.StockMatrix(Turned);
                s.U8(0).U8(0).U8(0).F32(0).Uids();
            }
            s.U8(1).I32(1);
            s.U8(1).I32(0);
        });
        w.Section(0x70000, s => s.Vec3(new Vector3(9, 8, 7)).StockMatrix(Turned));
        w.Section(0x01000000, s =>
        {
            s.I32(1).Str("Info name").Str("Author").Str("Monday, January 01, 2024 10:00:00").U8(1).U8(0);
            for (int i = 0; i < 4; i++)
            {
                s.I32(i == 0 ? 0 : 1);
                if (i != 0) s.F32(1);
                s.Vec3(Vector3.Zero).StockMatrix(RfMatrix.Identity);
            }
        });
        w.Section(0x02000000, s => s.I32(1).Brush(100, new Vector3(10, 0, 0), Turned, 0x04,
            b => b.Solid(["crate.tga"], Quad, [new(0, -1, -1, 0x08, [(0, new(0, 0), default), (1, new(1, 0), default), (2, new(1, 1), default)])]),
            life: 50, state: 3));
        w.Section(0x03000000, s =>
        {
            s.I32(2);
            s.Str("door").U8(0).U8(1).MovingData(90, 91).Uids(60).Uids(100);
            s.Str("static").U8(0).U8(0).Uids().Uids(100);
        });
        w.Section(0x12345678, s => s.I32(42));
        extra?.Invoke(w);
        w.EndMarker();
        return w.ToArray();
    }

    [Theory]
    [InlineData(0xC8)]
    [InlineData(0xB4)]
    public void Stock_level_reads_every_section_exactly(int version)
    {
        var level = RflLevelReader.Read(StockLevel(version), "synthetic.rfl");

        Assert.Equal(version, level.Version);
        Assert.False(level.IsTruncated);
        foreach (var s in level.Sections.Where(s => s.Id != 0x12345678))
            Assert.True(s.Status == RfSectionStatus.Read && s.Detail is null, $"{s.Name}: {s.Status} {s.Detail}");
        var unknown = Assert.Single(level.Sections, s => s.Id == 0x12345678);
        Assert.Equal(RfSectionStatus.Unknown, unknown.Status);
        Assert.Single(level.OpaqueSections, o => o.Id == 0x12345678 && o.Body.Length == 4);
        Assert.Contains(level.Problems, p => p.Contains("0x12345678"));

        Assert.Equal("Header name", level.Header.LevelName);
        Assert.Equal("Info name", level.Info!.Name);
        Assert.Equal("Author", level.Info.Author);
        Assert.True(level.Info.HasMovers);
        Assert.Equal(50, level.Properties!.Hardness);
        Assert.Equal(100f, level.Properties.FogFar);
    }

    [Theory]
    [InlineData(0xC8)]
    [InlineData(0xB4)]
    public void Static_geometry_keeps_per_corner_uvs_lightmap_uvs_and_scroll(int version)
    {
        var g = RflLevelReader.Read(StockLevel(version), "synthetic.rfl").StaticGeometry;

        Assert.Equal(["wall.tga", "floor.tga"], g.Textures.ToArray());
        Assert.Equal(4, g.Vertices.Length);
        Assert.Equal(2, g.Faces.Length);
        var lit = g.Faces[0];
        var unlit = g.Faces[1];

        // The same vertex has different UVs on the two faces: corner data, not vertex data.
        Assert.Equal(1, lit.Corners[1].Vertex);
        Assert.Equal(1, unlit.Corners[0].Vertex);
        Assert.Equal(new Vector2(1, 0), lit.Corners[1].Uv);
        Assert.Equal(new Vector2(5, 5), unlit.Corners[0].Uv);

        Assert.True(lit.HasSurface);
        Assert.Equal(new Vector2(0.2f, 0.1f), lit.Corners[1].LightmapUv);
        Assert.False(unlit.HasSurface);
        Assert.All(unlit.Corners, c => Assert.Null(c.LightmapUv));

        Assert.Null(lit.Scroll);
        Assert.Equal(new Vector2(0.5f, -1), unlit.Scroll);
        Assert.Empty(g.OrphanScrolls);

        Assert.Equal(1u, lit.Smoothing);
        Assert.Equal(RfFaceFlags.FullBright, lit.Flags);
        Assert.Equal(0x20u, lit.RawFlags);
        Assert.Equal(RfFaceClass.FullBright, RfFaceClasses.OfCompiled(lit));
        Assert.Equal(RfFaceClass.Scrolling | RfFaceClass.Portal, RfFaceClasses.OfCompiled(unlit));

        Assert.Equal(2, g.Rooms.Length);
        var water = g.Rooms[0];
        Assert.True(water.IsLiquid);
        Assert.Equal("water.tga", water.Liquid!.SurfaceTexture);
        Assert.Equal(new Vector2(0.5f, 0.25f), water.Liquid.Scroll);
        Assert.Equal(new RflColor(10, 20, 30, 255), water.AmbientColor);
        Assert.Equal(version >= 0xB4 ? "eax_water" : "", water.EaxEffect);
        Assert.True(g.Rooms[1].IsSky);
        Assert.Single(g.Portals);
        Assert.Single(g.Surfaces);
        Assert.Equal(8, g.Surfaces[0].Width);
    }

    [Fact]
    public void Brushes_keep_transform_flags_and_classes_from_the_solid_flags()
    {
        var level = RflLevelReader.Read(StockLevel(0xC8), "synthetic.rfl");
        var brush = Assert.Single(level.Brushes);

        Assert.Equal(100, brush.Uid);
        Assert.Equal(Turned, brush.Orientation);
        Assert.Equal(RfSolidFlags.Detail, brush.SolidFlags);
        Assert.True(brush.IsDetail);
        Assert.Equal(50, brush.Life);
        Assert.Equal(3, brush.State);
        Assert.Null(brush.LegacyExtra);
        // Local (1,0,0) along the turned right axis (0,0,-1), then moved to (10,0,0).
        Assert.Equal(new Vector3(10, 0, -1), brush.ToWorld(new Vector3(1, 0, 0)));

        var face = Assert.Single(brush.Geometry.Faces);
        Assert.False(face.HasSurface);
        // Detail comes from the brush's solid flag; face bit 0x08 is an editor bit on brush faces.
        Assert.Equal(RfFaceClass.Detail, RfFaceClasses.OfBrushFace(face, brush));
        Assert.Equal(RfFaceClass.Normal, RfFaceClasses.OfBrushFace(face, brush with { SolidFlags = RfSolidFlags.None }));
    }

    [Fact]
    public void Objects_read_with_every_field()
    {
        var level = RflLevelReader.Read(StockLevel(0xC8), "synthetic.rfl");
        var o = level.Objects;

        Assert.Equal(2, o.Lights.Length);
        Assert.Equal(RfLightType.Omni, o.Lights[0].Type);
        Assert.Equal(RfLightType.Spot, o.Lights[1].Type);
        Assert.Equal(12f, o.Lights[0].Range);
        Assert.Equal(new Vector3(1, 2, 3), o.Lights[0].Position);

        var teleport = o.Events[0];
        Assert.Equal("Teleport", teleport.ClassName);
        Assert.Equal(Turned, teleport.Orientation);
        Assert.Equal([30], teleport.Links.ToArray());
        Assert.Equal(1.5f, teleport.Delay);
        Assert.Equal(new RflColor(1, 2, 3, 255), teleport.Color);
        Assert.Null(o.Events[1].Orientation);
        Assert.Equal("door.wav", o.Events[1].Str1);

        Assert.Equal(42f, o.Entities[0].CustomAttackRange);
        Assert.Equal(15f, o.Entities[1].CustomAttackRange);
        Assert.Equal("flashlight", o.Entities[0].RightHandHolding);
        Assert.Equal("12mm handgun", o.Entities[0].PrimaryWeapon);
        Assert.True(o.Entities[0].Run);
        Assert.True(o.Entities[0].NeverCollideWithPlayer);

        Assert.Equal(5, o.Items[0].Count);
        Assert.Equal([1, 2], o.Clutter[0].Links.ToArray());
        Assert.Equal(3f, o.Triggers[0].Radius);
        Assert.Equal(new Vector3(1, 2, 3), o.Triggers[1].Size);
        Assert.True(o.Triggers[1].OneWay);

        Assert.Equal(2, o.NavPoints.Length);
        Assert.Null(o.NavPoints[0].Orientation);
        Assert.Equal(Turned, o.NavPoints[1].Orientation);
        Assert.Equal([1], o.NavPoints[0].Connections.ToArray());
        Assert.Equal([0], o.NavPoints[1].Connections.ToArray());

        Assert.Equal(new Vector3(9, 8, 7), level.PlayerStart!.Position);
        Assert.Equal(Turned, level.PlayerStart.Orientation);
    }

    [Fact]
    public void Groups_and_moving_groups()
    {
        var level = RflLevelReader.Read(StockLevel(0xC8), "synthetic.rfl");

        Assert.Equal(2, level.Groups.Length);
        var door = level.Groups[0];
        Assert.True(door.IsMoving);
        Assert.Equal([60], door.ObjectUids.ToArray());
        Assert.Equal([100], door.BrushUids.ToArray());
        var m = door.Moving!;
        Assert.Equal(2, m.Keyframes.Length);
        Assert.Equal(new Vector3(1, 0, 0), m.Keyframes[1].Position);
        Assert.Equal(90f, m.Keyframes[0].DegreesAboutAxis);
        Assert.Equal(500, Assert.Single(m.MemberTransforms).Uid);
        Assert.True(m.IsDoor);
        Assert.True(m.NoPlayerCollide);
        Assert.Equal(3, m.MovementType);
        Assert.Equal(new RfSound("close.wav", 1), m.CloseSound);
        Assert.Equal(0.5f, m.LoopingSound.Volume);
        Assert.False(level.Groups[1].IsMoving);
    }

    internal static byte[] AlpineLevel(int version, bool alpineEventMatrix = true)
    {
        var w = LvlBytes.Level(version, "Alpine", "");
        w.Section(0x0AFBA5ED, s =>
        {
            s.U32(6).U8(0).U8(0).U8(1).U8(1).F32(2).U8(1);
            s.U32(1).I32(100).I32(5);              // geoable brush 100 in room 5
            s.U32(1).I32(101).I32(6).U8(0x83);     // breakable brush 101, material 3 + no debris
            s.U32(0);                              // hold open
            s.U8(1).F32(45).F32(30).Color(new RflColor(255, 240, 200, 255)).F32(1).F32(2);
            for (int i = 0; i < 9; i++) s.U8(0);
            s.U32(1).I32(100);                     // no-shadow brush 100
            s.U8(0).U8(4).U8(1).U8(0);
            s.U8(1).F32(50).U8(1).Str("minimap.tga").Vec3(new Vector3(-1)).Vec3(new Vector3(1)).F32(3);
        });
        w.Section(0x100, s => s.Solid(["a.tga"], Quad, [new(0, 0xFFFF, 1, 0, [(0, new(0, 0), default), (1, new(1, 0), default), (2, new(1, 1), default)])]));
        w.Section(0x600, s =>
        {
            s.I32(1).I32(20).Str("AF_Teleport_Player").Vec3(Vector3.Zero).Str("").U8(0).F32(0).U8(0).U8(0).I32(0).I32(0).F32(0).F32(0).Str("").Str("").Uids();
            if (alpineEventMatrix) s.StockMatrix(Turned);
            s.Color(new RflColor(1, 1, 1, 255));
        });
        w.Section(0x02000000, s =>
        {
            s.I32(2);
            for (int uid = 100; uid <= 101; uid++)
                s.Brush(uid, Vector3.Zero, RfMatrix.Identity, 0, b => b.Solid([], [], []));
        });
        w.Section(0x0AFBAE01, s =>
        {
            s.U32(2).AlpineMesh(200, Vector3.One, Turned, clutter: true).AlpineMesh(201, Vector3.Zero, RfMatrix.Identity, clutter: false);
            if (version >= 306)
            {
                s.U8(1).U8(0);
                s.U8(2).Str("collision.v3m").F32(1.5f);
                s.U8(0).Str("").F32(1);
            }
        });
        w.Section(0x0AFBAE03, s => s.U32(2).Corona(300, Vector3.UnitX, "").Corona(301, Vector3.UnitY, "volume.tga"));
        w.Section(0x0AFBAE0B, s => s.U32(1).I32(12345));
        w.EndMarker();
        return w.ToArray();
    }

    [Theory]
    [InlineData(306)]
    [InlineData(305)]
    [InlineData(310)]
    public void Alpine_level_reads_its_chunks(int version)
    {
        var level = RflLevelReader.Read(AlpineLevel(version), "alpine.rfl");

        Assert.True(level.IsAlpine);
        foreach (var s in level.Sections.Where(s => s.Id != 0x0AFBAE0B))
            Assert.True(s.Status == RfSectionStatus.Read && s.Detail is null, $"{s.Name}: {s.Status} {s.Detail}");
        Assert.Equal(RfSectionStatus.Opaque, Assert.Single(level.Sections, s => s.Id == 0x0AFBAE0B).Status);
        Assert.Equal(1L, Assert.Single(level.OpaqueSections).LeadingCount);
        Assert.Equal(version > 306, level.Problems.Any(p => p.Contains("newer")));

        var a = level.AlpineProperties!;
        Assert.False(a.EndedEarly);
        Assert.Equal(0, a.UnreadBytes);
        Assert.Equal(new RfAlpineGeoableBrush(100, 5), Assert.Single(a.GeoableBrushes));
        Assert.Equal("minimap.tga", a.MinimapBitmap);
        Assert.Equal(3f, a.MinimapCutHeight);

        // The brush lists of the level properties land on the brushes.
        Assert.Equal(RfAlpineBrushFlags.Geoable | RfAlpineBrushFlags.NoShadowCast, level.Brushes[0].Alpine!.Flags);
        Assert.Equal(RfAlpineBrushFlags.Breakable, level.Brushes[1].Alpine!.Flags);
        Assert.Equal(0x83, level.Brushes[1].Alpine!.Material);

        // Alpine writes 0x0000FFFF for "no surface": no lightmap UVs.
        var face = Assert.Single(level.StaticGeometry.Faces);
        Assert.False(face.HasSurface);

        Assert.Equal(Turned, Assert.Single(level.Objects.Events).Orientation);

        var meshes = level.Objects.AlpineMeshes;
        Assert.Equal(2, meshes.Length);
        Assert.Equal(Turned, meshes[0].Orientation); // Alpine chunks store right, up, forward
        Assert.Equal("crate.v3m", meshes[0].MeshFile);
        Assert.Equal("override.tga", Assert.Single(meshes[0].TextureOverrides).Texture);
        Assert.Equal(3, meshes[0].Material);
        Assert.Equal(11, meshes[0].Clutter!.DamageFactors.Length);
        Assert.Equal((sbyte)-1, meshes[0].Clutter!.CorpseMaterial);
        Assert.Null(meshes[1].Clutter);
        if (version >= 306)
        {
            Assert.Equal((byte)1, meshes[0].TrailingFlags);
            Assert.Equal("collision.v3m", meshes[0].CollisionMesh);
            Assert.Equal(1.5f, meshes[0].DrawScale);
        }
        else
        {
            Assert.Null(meshes[0].TrailingFlags);
        }

        var coronas = level.Objects.Coronas;
        Assert.Equal(2, coronas.Length);
        Assert.Equal("", coronas[0].VolumetricBitmap);
        Assert.Equal(8f, coronas[1].VolumetricLength);
    }

    [Fact]
    public void Prerelease_alpine_events_without_their_matrix_read_with_a_fallback()
    {
        var level = RflLevelReader.Read(AlpineLevel(300, alpineEventMatrix: false), "early.rfl");

        var events = Assert.Single(level.Sections, s => s.Id == 0x600);
        Assert.Equal(RfSectionStatus.ReadWithFallback, events.Status);
        Assert.Null(Assert.Single(level.Objects.Events).Orientation);
    }

    [Fact]
    public void A_damaged_section_is_kept_and_the_rest_still_reads()
    {
        var bytes = StockLevel(0xC8, w => w.Section(0x40000, s => s.I32(3).Item(1, "x", Vector3.Zero)));
        var level = RflLevelReader.Read(bytes, "damaged.rfl");

        var damaged = level.Sections.Last(s => s.Id == 0x40000);
        Assert.Equal(RfSectionStatus.Damaged, damaged.Status);
        Assert.Contains(level.OpaqueSections, o => o.Id == 0x40000);
        Assert.Contains(level.Problems, p => p.Contains("Items"));
        // The good items section before it is kept.
        Assert.Single(level.Objects.Items);
        Assert.Equal(2, level.Groups.Length);
    }

    [Fact]
    public void A_file_without_end_marker_or_with_an_overlong_section_is_truncated_not_refused()
    {
        var bytes = StockLevel(0xC8);
        var noEnd = bytes[..^8];
        var level = RflLevelReader.Read(noEnd, "noend.rfl");
        Assert.True(level.IsTruncated);
        Assert.Equal(2, level.Groups.Length);

        var cut = bytes[..^40];
        level = RflLevelReader.Read(cut, "cut.rfl");
        Assert.True(level.IsTruncated);
        Assert.Equal(RfSectionStatus.Truncated, level.Sections[^1].Status);
    }

    [Fact]
    public void Header_problems_are_refused_with_a_clear_message()
    {
        Assert.Contains("empty", Assert.Throws<AssetFormatException>(() => RflLevelReader.Read([], "a.rfl")).Message);
        Assert.Contains("not a Red Faction level", Assert.Throws<AssetFormatException>(() => RflLevelReader.Read(new byte[16], "a.rfl")).Message);
        var old = LvlBytes.Level(45).EndMarker().ToArray();
        Assert.Contains("older", Assert.Throws<AssetFormatException>(() => RflLevelReader.Read(old, "a.rfl")).Message);
        var odd = LvlBytes.Level(250).EndMarker().ToArray();
        Assert.Contains("250", Assert.Throws<AssetFormatException>(() => RflLevelReader.Read(odd, "a.rfl")).Message);
        var cutHeader = LvlBytes.Level(0xC8).ToArray()[..14];
        Assert.Contains("header", Assert.Throws<AssetFormatException>(() => RflLevelReader.Read(cutHeader, "a.rfl")).Message);
    }

    [Fact]
    public void Rf2_levels_go_to_the_rf2_path_and_rf1_files_with_an_edited_version_read_as_rf1()
    {
        // A real RF2 header: no mod name, first section 0x7000.
        var w = new LvlBytes(0x127);
        w.U32(RflReader.Magic).I32(0x127).U32(1).I32(0).I32(0).I32(0).I32(0).Str("RF2 level");
        w.Section(0x7000, s => s.I32(0)).EndMarker();
        var rf2 = RflLevelReader.Read(w.ToArray(), "rf2.rfl");
        Assert.True(rf2.IsRf2);
        Assert.Null(rf2.Header.ModName);

        // An RF1 level whose version field was edited to 295 keeps its mod name and RF1 layout.
        var edited = StockLevel(0xC8);
        BitConverter.GetBytes(0x127).CopyTo(edited, 4);
        var level = RflLevelReader.Read(edited, "edited.rfl");
        Assert.False(level.IsRf2);
        Assert.Contains(level.Problems, p => p.Contains("295"));
        Assert.Equal(2, level.StaticGeometry.Faces.Length);
    }

    [Fact]
    public void Model_diff_compares_structurally_and_names_the_difference()
    {
        var a = RflLevelReader.Read(StockLevel(0xC8), "a.rfl");
        var b = RflLevelReader.Read(StockLevel(0xC8), "a.rfl");
        Assert.Empty(RfModelDiff.Compare(a, b));

        var face = b.StaticGeometry.Faces[1];
        var changed = b with
        {
            StaticGeometry = b.StaticGeometry with { Faces = b.StaticGeometry.Faces.SetItem(1, face with { Scroll = Vector2.Zero }) },
        };
        var diff = Assert.Single(RfModelDiff.Compare(a, changed));
        Assert.StartsWith("StaticGeometry.Faces[1].Scroll", diff);
        Assert.True(RfModelDiff.AreEqual(a, b with { Name = "other.rfl" }, "Name"));
    }

    [Fact]
    public void Lightmap_pixels_are_kept_only_when_asked()
    {
        var bytes = StockLevel(0xC8, w => w.Section(0x1200, s => s.I32(1).I32(2).I32(2).Bytes(new byte[12])));
        Assert.True(RflLevelReader.Read(bytes, "lm.rfl").Lightmaps[0].Pixels.IsEmpty);
        var full = RflLevelReader.Read(bytes, "lm.rfl", new RfReadOptions { IncludeLightmapPixels = true });
        Assert.Equal(12, full.Lightmaps[0].Pixels.Length);
        Assert.Equal(2, full.Lightmaps[0].Width);
    }
}
