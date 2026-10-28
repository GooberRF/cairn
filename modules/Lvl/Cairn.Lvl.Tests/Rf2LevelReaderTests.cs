using Cairn.Formats;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;

namespace Cairn.Lvl.Tests;

/// <summary>The Red Faction II level reader on synthetic levels written from the format notes.</summary>
public sealed class Rf2LevelReaderTests
{
    private static RfLevel Read() => RflLevelReader.Read(Rf2LevelTestData.Level(), "synthetic_rf2.rfl");

    [Fact]
    public void Every_section_is_read_exactly_or_kept_for_its_reason()
    {
        var level = Read();
        Assert.True(level.IsRf2);
        Assert.False(level.IsTruncated);
        Assert.Null(level.Header.ModName);
        Assert.Equal("Synthetic RF2", level.Header.LevelName);
        Assert.Empty(Rf2Samples.Unexplained(level));
        Assert.Contains(level.Problems, p => p.Contains("no colour set", StringComparison.Ordinal));
        var opaque = level.Sections.Where(s => s.Status == RfSectionStatus.Opaque).Select(s => s.Id).ToList();
        Assert.Equal([0x30000u, 0x03000000u], opaque);
        Assert.Equal(2, level.OpaqueSections.Length);
        Assert.Contains("entities", level.Sections.Single(s => s.Id == 0x30000).Detail);
    }

    [Fact]
    public void Faces_keep_raw_flags_map_them_to_rf1_and_turn_scroll_periods_into_speeds()
    {
        var g = Read().StaticGeometry;
        Assert.Equal(Rf2LevelTestData.StaticTextures, g.Textures);
        Assert.Equal(12, g.Vertices.Length);
        Assert.Equal(3, g.Faces.Length);
        Assert.Equal(2, g.Rooms.Length);
        Assert.Equal("eax_room", g.Rooms[0].EaxEffect);
        Assert.Equal(100, g.Rooms[0].Id);
        Assert.Equal(44, g.Rooms[0].Rf2Data.Length);
        Assert.Single(g.SubroomLists);
        Assert.Equal(8, g.Rf2RoomLinks.Length);

        var hex = g.Faces[0];
        Assert.Equal(6, hex.Corners.Length);
        Assert.Equal(0x8000u | 0x10 | 0x20 | 0x1000, hex.RawFlags);
        Assert.Equal(RfFaceFlags.ScrollTexture | RfFaceFlags.FullBright, hex.Flags);
        Assert.Equal(new Vector2(0.5f, 0), hex.Scroll);
        Assert.Equal(new Vector2(2, 0), hex.Rf2!.ScrollPeriod);
        Assert.Equal(((byte)0xFF, (byte)0xB4, (byte)0x5A), hex.Rf2.EmissiveColor);
        Assert.Equal(1.4f, hex.Rf2.EmissiveIntensity);
        Assert.Equal(7, hex.Rf2.Unknown);
        Assert.Equal(1000, hex.FaceId);
        Assert.Equal(1u, hex.Smoothing);
        Assert.All(hex.Corners, c => Assert.Equal(Rf2LevelTestData.Neutral, c.Color));
        Assert.All(hex.Corners, c => Assert.Null(c.LightmapUv));
        Assert.Equal(new Vector2(0.25f, 0.75f), hex.Corners[1].Uv);
        Assert.Equal(RfFaceClass.FullBright | RfFaceClass.Scrolling, RfFaceClasses.OfCompiled(hex));

        // A sky face draws the sky, so its periods give no scroll; a scroll flag without periods gives none either.
        Assert.Null(g.Faces[1].Scroll);
        Assert.Equal(RfFaceFlags.ShowSky, g.Faces[1].Flags);
        Assert.Null(g.Faces[2].Scroll);
        Assert.Null(g.Faces[2].Rf2!.ScrollPeriod);
        Assert.Equal(RfFaceFlags.ScrollTexture | RfFaceFlags.Detail, g.Faces[2].Flags);
    }

    [Fact]
    public void Brushes_movers_and_moving_groups_are_read_with_their_rf2_fields()
    {
        var level = Read();
        Assert.Equal([27, 28, 29, 30], level.Brushes.Select(b => b.Uid));
        Assert.All(level.Brushes, b => Assert.Equal(6, b.Geometry.Faces.Length));
        Assert.Equal((RfSolidFlags)0x24, level.Brushes[1].SolidFlags);
        Assert.Equal(50, level.Brushes[1].Life);
        Assert.Equal(Enumerable.Range(1, 29).Select(i => (byte)i), level.Brushes[2].Rf2Extra);
        Assert.Empty(level.Brushes[0].Rf2Extra);
        Assert.Equal(new Vector3(2, 0, 1), level.Brushes[1].ToWorld(new Vector3(0, 0, 1)));

        var mover = Assert.Single(level.Movers);
        Assert.Equal(24, mover.Uid);
        var group = Assert.Single(level.MovingGroups);
        Assert.Equal("door", group.Name);
        Assert.NotNull(group.Moving);
        Assert.True(group.Moving!.IsDoor);
        Assert.True(group.Moving.UseTravelTimeAsSpeed);
        Assert.Equal(2, group.Moving.MovementType);
        Assert.Equal("start.wav", group.Moving.StartSound.File);
        Assert.Equal(0.5f, group.Moving.StopSound.Volume);
        Assert.Equal(90f, Assert.Single(group.Moving.Keyframes).DegreesAboutAxis);
        Assert.Equal(new[] { 25 }, group.ObjectUids);
        Assert.Equal(new[] { 24 }, group.BrushUids);
        var rf2 = group.Rf2Data!;
        Assert.Equal(new byte[] { 1, 0, 1, 0 }, rf2.Flags);
        Assert.Equal(7, rf2.MovingByte);
        Assert.Equal(42, rf2.Unknown);
        Assert.Equal(26, rf2.ParentUid);
        Assert.Equal(Vector3.One, rf2.ParentPosition);
        Assert.Equal(Enumerable.Range(0, 9), rf2.Tail);
    }

    [Fact]
    public void Objects_are_read_with_the_layout_fixes()
    {
        var o = Read().Objects;
        var light = Assert.Single(o.Lights);
        Assert.Equal(5f, light.Range);
        Assert.Equal(RfLightType.Omni, light.Type);
        Assert.Equal(0.5f, light.OnIntensity);
        Assert.Equal("amb_wind.wav", Assert.Single(o.AmbientSounds).SoundFile);

        Assert.Equal(2, o.Events.Length);
        Assert.Equal(RfMatrix.Identity, o.Events[0].Orientation);
        Assert.Equal(new Rf2EventData(-1, 0, 7, 8, 0, ""), o.Events[0].Rf2Data);
        Assert.True(o.Events[0].Bool1);
        Assert.Equal((5, 6), (o.Events[0].Int1, o.Events[0].Int2));
        Assert.Equal((0.25f, 0.5f), (o.Events[0].Float1, o.Events[0].Float2));
        Assert.Equal(new[] { 10 }, o.Events[0].Links);
        Assert.Null(o.Events[1].Orientation);
        Assert.Equal("third", o.Events[1].Rf2Data!.Str3);
        Assert.Equal(new RflColor(5, 6, 7, 8), o.Events[1].Color);

        Assert.True(Assert.Single(o.RespawnPoints).RedTeam);

        Assert.Equal(3, o.Coronas.Length);
        var c = o.Coronas[0];
        Assert.Equal(-Vector3.UnitY, c.Orientation.Forward);
        Assert.Equal(Vector3.UnitZ, c.Orientation.Up);
        Assert.Equal(Vector3.UnitX, c.Orientation.Right);
        Assert.Equal(30f, c.ConeAngle);
        Assert.Equal(2f, c.Intensity);
        Assert.Equal(50f, c.DiminishDistance);
        Assert.Equal("beam.tga", c.VolumetricBitmap);
        Assert.Equal((3f, 4f), (c.VolumetricHeight, c.VolumetricLength));
        Assert.Equal(14, c.Rf2Data.Length);
        // The corona without a bitmap stores no glare values and still ends where its record does.
        var bare = o.Coronas[1];
        Assert.Equal("", bare.CoronaBitmap);
        Assert.Equal(0f, bare.Intensity);
        Assert.True(bare.HiddenInEditor);
        Assert.Equal(10, bare.Rf2Data.Length);

        var decal = Assert.Single(o.Decals);
        Assert.Equal("dec_scorch.tga", decal.Texture);
        Assert.Equal(2f, decal.Scale);
        Assert.Equal(new Rf2DecalData(0.5f, 1, 2), decal.Rf2Data);

        Assert.Equal(2, o.PushRegions.Length);
        Assert.Equal((1, 3f, 10f), (o.PushRegions[0].Shape, o.PushRegions[0].Radius, o.PushRegions[0].Strength));
        Assert.Equal(0x40, o.PushRegions[0].Flags);
        Assert.Equal(new Vector3(1, 2, 3), o.PushRegions[1].Extents);
        Assert.Equal(2, o.PushRegions[1].Turbulence);

        var item = Assert.Single(o.Items);
        Assert.Equal(("inven_HealthKit", 30), (item.ClassName, item.RespawnTime));
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0, 0 }, item.Rf2Extra);
        Assert.Equal(["skin_a", ""], o.Clutter.Select(k => k.Skin));
        Assert.Equal(new[] { 20 }, o.Clutter[0].Links);

        var t = Assert.Single(o.Triggers);
        Assert.Equal(("trig", 1, "key"), (t.ScriptName, t.Shape, t.KeyName));
        Assert.True(t.UseKey);
        Assert.Equal(3, t.ActivatedBy);
        Assert.Equal(new Vector3(1, 2, 3), t.Size);
        Assert.Equal(21, t.UseClutterUid);
        Assert.True(t.IsAuto);
        Assert.Equal(new Rf2TriggerData(1e9f, 0, 0x1234, 0x8), t.Rf2Data);
        Assert.Equal(new[] { 12 }, t.Links);
    }

    [Fact]
    public void Level_wide_sections_are_read()
    {
        var level = Read();
        var p = level.Properties!;
        Assert.Equal(("geo_rock.tga", 50, 200f), (p.GeomodTexture, p.Hardness, p.FogFar));
        Assert.Equal(32, p.Rf2Data.Length);
        var rf2 = level.Rf2!.Properties!;
        Assert.Equal(2f, rf2.LightmapMultiplier);
        Assert.Equal(3, rf2.HardlightIntensity);
        Assert.Equal(0.75f, rf2.SunIntensity);

        var vl = level.Rf2.VertexLighting!;
        Assert.Equal(5, vl.Version);
        Assert.Equal(new[] { "rck_invisible.tga" }, vl.TextureNames);
        var tri = Assert.Single(Assert.Single(vl.Entries).Triangles);
        Assert.Equal(new RflColor(255, 20, 10, 255), tri.ColorA); // 300 clamps to 255
        Assert.Equal(new RflColor(0, 50, 60, 255), tri.ColorC);   // -5 clamps to 0
        Assert.Equal((byte)0, level.Rf2.Section7901);

        Assert.Equal([0x7000u, 0x7001u, 0x7002u, 0x7003u, 0x7004u, 0x7005u], level.Preloads.Select(l => l.SectionId));
        Assert.Equal(new[] { "burke.vcm" }, level.Preloads[1].Names);
        Assert.Equal(new[] { 3 }, level.Preloads[1].Counts);
        Assert.Equal(new[] { "synthetic.peg" }, level.Preloads[5].Names);
        Assert.Equal("author", level.Info!.Author);
        Assert.Equal(new Vector3(1, 2, 3), level.PlayerStart!.Position);
        Assert.Equal(new[] { 1, 2 }, Assert.Single(level.WaypointLists).NavPoints);
    }

    [Fact]
    public void A_damaged_section_costs_only_itself()
    {
        var data = Rf2LevelTestData.Level();
        // Find the lights section and claim one record too many.
        int at = FindSection(data, 0x300);
        BitConverter.GetBytes(2).CopyTo(data, at + 8);
        var level = RflLevelReader.Read(data, "damaged.rfl");
        var s = level.Sections.Single(x => x.Id == 0x300);
        Assert.Equal(RfSectionStatus.Damaged, s.Status);
        Assert.Empty(level.Objects.Lights);
        Assert.Contains(level.OpaqueSections, x => x.Id == 0x300);
        Assert.Equal(3, level.Objects.Coronas.Length);
        Assert.Equal(4, level.Brushes.Length);
    }

    [Theory]
    [InlineData(0xC9)]
    [InlineData(0x100)]
    [InlineData(0x126)]
    [InlineData(0x128)]
    [InlineData(0x12B)]
    public void Versions_between_rf1_and_alpine_other_than_rf2_are_refused(int version)
    {
        var data = Rf2LevelTestData.Level();
        BitConverter.GetBytes(version).CopyTo(data, 4);
        var e = Assert.Throws<AssetFormatException>(() => RflLevelReader.Read(data, "odd.rfl"));
        Assert.Contains($"version {version}", e.Message);
    }

    [Fact]
    public void Truncated_and_mutated_rf2_levels_only_throw_asset_format_exceptions()
    {
        var data = Rf2LevelTestData.Level();
        for (int n = 0; n < data.Length; n += Math.Max(1, data.Length / 600))
        {
            try
            {
                var level = RflLevelReader.Read(data[..n], "cut.rfl");
                LvlSamples.CheckModel(level);
            }
            catch (AssetFormatException)
            {
            }
        }
        LvlFuzzTests.Mutate(data, "rf2.rfl", 300, seed: 295);
    }

    private static int FindSection(byte[] data, uint id)
    {
        // The header: magic, version, timestamp, two offsets, two totals, the level name.
        int pos = 28 + 2 + BitConverter.ToUInt16(data, 28);
        while (pos + 8 <= data.Length)
        {
            uint sid = BitConverter.ToUInt32(data, pos);
            if (sid == id) return pos;
            if (sid == 0) break;
            pos += 8 + BitConverter.ToInt32(data, pos + 4);
        }
        throw new InvalidOperationException($"section 0x{id:X} not found");
    }
}
