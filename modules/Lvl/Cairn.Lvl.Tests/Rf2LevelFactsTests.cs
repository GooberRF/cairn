using System.Security.Cryptography;
using System.Text;
using Cairn.Formats.Rfl;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// The level facts (the details pane beside a level's preview and in the packfile view) of Red Faction II levels: the
/// summary reader walks RF2's sections, so an RF2 level gets the identity, properties (fog, the sun and hardlight),
/// statistics, classes, references, resource lists and section list an RF1 level gets. RF1 and Alpine rows are pinned
/// to what they were before the RF2 path existed.
/// </summary>
public sealed class Rf2LevelFactsTests(ITestOutputHelper output)
{
    private static IReadOnlyList<RflFactRow> Facts(byte[] bytes, string name, Func<string, AssetPresence>? presence = null) =>
        RflFacts.Rows(RflReader.ReadSummary(bytes, name), presence, TimeZoneInfo.Utc);

    private static string? Value(IReadOnlyList<RflFactRow> rows, string section, string label) =>
        rows.FirstOrDefault(r => r.Section == section && r.Label == label)?.Value;

    [Fact]
    public void Synthetic_rf2_level_gets_identity_properties_statistics_classes_references_and_sections()
    {
        byte[] bytes = Rf2LevelTestData.Level();
        var s = RflReader.ReadSummary(bytes, "synthetic.rfl");
        // the stream reader (which reads only the sections it needs) agrees with the span reader
        var streamed = RflReader.ReadSummary(new MemoryStream(bytes), "synthetic.rfl");
        Assert.Equal(RflFacts.Rows(s, null, TimeZoneInfo.Utc), RflFacts.Rows(streamed, null, TimeZoneInfo.Utc));
        var rows = RflFacts.Rows(s, _ => AssetPresence.Unknown, TimeZoneInfo.Utc);

        Assert.Equal("No Level Name", Value(rows, "Level", "Name"));
        Assert.Equal("Synthetic RF2", Value(rows, "Level", "Name in header"));
        Assert.Equal("author", Value(rows, "Level", "Author"));
        Assert.Equal("date", Value(rows, "Level", "Editor's date"));
        Assert.Equal("Red Faction II (295)", Value(rows, "Level", "Format version"));
        Assert.Equal("Red Faction II", Value(rows, "Level", "Loads in"));
        Assert.Equal("Single player", Value(rows, "Level", "Mode"));
        Assert.Equal("yes", Value(rows, "Level", "Has movers"));
        Assert.Null(Value(rows, "Level", "Requires mod"));

        Assert.Equal("40, 50, 60", Value(rows, "Properties", "Ambient light"));
        Assert.Equal("colour 1, 2, 3, from 10 to 200", Value(rows, "Properties", "Fog"));
        Assert.Equal("geo_rock.tga", Value(rows, "Properties", "Geomod texture"));
        Assert.Equal("50", Value(rows, "Properties", "Geomod hardness"));
        Assert.Equal("255, 240, 200", Value(rows, "Properties", "Sun colour (light bake)"));
        Assert.Equal("yaw 30, pitch 45", Value(rows, "Properties", "Sun direction (light bake)"));
        Assert.Equal("0.75", Value(rows, "Properties", "Sun intensity (light bake)"));
        Assert.Equal("89.9", Value(rows, "Properties", "Sun spread (light bake)"));
        Assert.Equal("9, 8, 7", Value(rows, "Properties", "Hardlight colour"));
        Assert.Equal("3", Value(rows, "Properties", "Hardlight intensity"));
        Assert.Equal("2", Value(rows, "Properties", "Lightmap multiplier"));

        // RF2 rooms store no sky or liquid flags: the rooms row has no breakdown
        Assert.Equal("2", Value(rows, "Statistics", "Rooms"));
        Assert.Equal("3", Value(rows, "Statistics", "Faces"));
        Assert.Equal("12", Value(rows, "Statistics", "Vertices"));
        Assert.Equal("2", Value(rows, "Statistics", "Geometry textures"));
        Assert.Equal("1 (1 omni, 0 spot, 0 tube)", Value(rows, "Statistics", "Lights"));
        Assert.Equal("1 (1 red, 0 blue, 1 bot)", Value(rows, "Statistics", "Respawn points"));
        foreach (var (label, count) in new[] { ("Ambient sounds", "1"), ("Events", "2"), ("Coronas", "3"), ("Decals", "1"), ("Push regions", "2"),
            ("Entities", "1"), ("Waypoint lists", "1"), ("Items", "1"), ("Clutter", "2"), ("Triggers", "1"), ("Movers", "1"),
            ("Moving groups", "1"), ("Keyframes", "1"), ("Brushes", "4"), ("Player start", "yes") })
            Assert.True(Value(rows, "Statistics", label) == count, $"{label}: {Value(rows, "Statistics", label)} (want {count})");

        Assert.Equal("1", Value(rows, "Item classes", "inven_HealthKit"));
        Assert.Equal("1", Value(rows, "Clutter classes", "minelight01"));
        Assert.Equal("1", Value(rows, "Event classes", "Teleport"));

        var refs = rows.Where(r => r.Section == "References").ToDictionary(r => r.Label, r => r.Value);
        Assert.Equal("texture; level geometry", refs["zzz_unknown.tga"]);
        Assert.Equal("texture; geomod texture", refs["geo_rock.tga"]);
        Assert.Equal("texture; corona", refs["corona_red.tga"]);
        Assert.Equal("texture; corona volumetric", refs["beam.tga"]);
        Assert.Equal("texture; decal", refs["dec_scorch.tga"]);
        Assert.Equal("texture; mover geometry", refs["mtl_door.tga"]);
        Assert.Equal("sound; ambient sound", refs["amb_wind.wav"]);
        Assert.Equal("sound; moving group sound", refs["start.wav"]);
        // nothing an RF2 level names is called missing (its files are in RF2's packfiles)
        Assert.DoesNotContain("Missing", refs.Keys);

        Assert.Equal("2", Value(rows, "Preloads", "Bitmaps"));
        Assert.Equal("1", Value(rows, "Preloads", "Character meshes"));
        Assert.Equal("1", Value(rows, "Preloads", "Texture packs"));

        var sections = rows.Where(r => r.Section == "Sections").Select(r => r.Label).ToList();
        Assert.Equal(["Bitmap list", "Character mesh list", "Animation list", "Static mesh list", "Effect list", "Texture pack list", "Level properties",
            "Static geometry", "Lights", "Ambient sounds", "Events", "Respawn points", "Coronas", "Decals", "Push regions", "Vertex lighting",
            "RF2 section 0x7901", "Entities", "Player start", "Waypoint lists", "Items", "Clutter", "Triggers", "Movers", "Moving groups",
            "Level info", "Brushes", "Groups"], sections);
        Assert.Contains("skipped (version 5, 1 textures, 1 groups)", Value(rows, "Sections", "Vertex lighting"), StringComparison.Ordinal);
        Assert.Contains("read (value 0)", Value(rows, "Sections", "RF2 section 0x7901"), StringComparison.Ordinal);
        Assert.Contains("count only; layout not known", Value(rows, "Sections", "Entities"), StringComparison.Ordinal);
        // the synthetic editor groups section is 0xCD filler: its count does not fit, and that is the one note
        Assert.Contains(rows, r => r.Section == "Sections" && r.Label == "Groups" && r.Flagged && r.Value.Contains("damaged", StringComparison.Ordinal));
        Assert.Equal(["Groups: the record count (-842150451) does not fit the section."], rows.Where(r => r.Section == "Notes").Select(r => r.Value));
        Assert.All(rows.Where(r => r.Section == "Sections" && r.Label != "Groups"), r => Assert.False(r.Flagged, r.Label));
    }

    [Fact]
    public void An_rf1_level_with_an_edited_version_is_not_walked_as_rf2()
    {
        // version 295, but the bytes after the level name are an RF1 mod name, not RF2's first section
        var w = new LvlBytes(0x127);
        w.U32(RflReader.Magic).I32(0x127).U32(1_040_000_000).I32(0).I32(0).I32(0).I32(0).Str("Edited").Str("");
        w.Section(0x100, s => s.I32(0)).EndMarker();
        var s = RflReader.ReadSummary(w.ToArray(), "edited.rfl");
        Assert.Empty(s.Sections);
        Assert.Contains(s.Notes, n => n.Contains("not a Red Faction II level's", StringComparison.Ordinal));
    }

    [Fact]
    public void Object_heads_are_found_only_with_a_class_name_and_an_orthonormal_orientation()
    {
        var w = new LvlBytes(0x127);
        w.I32(2);
        w.Head(1, "Bolt Emitter", new Vector3(1, 2, 3)).Bytes([9, 9, 9, 9, 9, 9, 9]);
        w.Head(2, "Bolt Emitter", new Vector3(4, 5, 6)).F32(1);
        // a class name followed by a matrix that is not a rotation is not a head
        w.I32(3).Str("Fake").Vec3(Vector3.Zero);
        for (int i = 0; i < 9; i++) w.F32(2);
        w.Str("").U8(0);
        var section = Rf2Body(0xE00, w.ToArray());
        var rows = Facts(section, "scan.rfl");
        Assert.Equal("2", Value(rows, "Statistics", "Bolt emitters"));
        Assert.Contains("class \"Bolt Emitter\"", Value(rows, "Sections", "Bolt emitters"), StringComparison.Ordinal);
    }

    /// <summary>An RF2 level of the bitmap list and one more section.</summary>
    private static byte[] Rf2Body(uint id, byte[] body)
    {
        var w = Rf2LevelTestData.Header("Scan");
        w.Section(0x7000, s => s.I32(0));
        w.Section(id, s => s.Bytes(body));
        w.EndMarker();
        return w.ToArray();
    }

    // ---------- real levels (skipped when the files are not on this machine) ----------

    [Fact]
    public void Real_rf2_levels_show_their_author_fog_sun_hardlight_and_counts()
    {
        if (Rf2Samples.ReduxFolder is { } folder && File.Exists(Path.Combine(folder, "ctfo02.rfl")))
        {
            var rows = Facts(File.ReadAllBytes(Path.Combine(folder, "ctfo02.rfl")), "ctfo02.rfl");
            Assert.Equal("Cover Fire", Value(rows, "Level", "Name"));
            Assert.Equal("Luke Schneider", Value(rows, "Level", "Author"));
            Assert.Equal("2003-01-14 21:37:54 (UTC+00:00)", Value(rows, "Level", "Saved"));
            Assert.Equal("Tuesday, January 14, 2003 16:37:54", Value(rows, "Level", "Editor's date"));
            Assert.Equal("Multiplayer", Value(rows, "Level", "Mode"));
            Assert.Equal("colour 100, 90, 80, from 0 to 120", Value(rows, "Properties", "Fog"));
            Assert.Equal("20, 18, 16", Value(rows, "Properties", "Ambient light"));
            Assert.Equal("0, 255, 0", Value(rows, "Properties", "Hardlight colour"));
            Assert.Equal("6", Value(rows, "Properties", "Hardlight intensity"));
            Assert.Equal("1.2", Value(rows, "Properties", "Lightmap multiplier"));
            Assert.Equal("104", Value(rows, "Statistics", "Rooms"));
            Assert.Equal("3,309", Value(rows, "Statistics", "Faces"));
            Assert.Equal("16 (8 red, 8 blue, 16 bot)", Value(rows, "Statistics", "Respawn points"));
            Assert.Equal("2", Value(rows, "Item classes", "wep_Shotgun"));
            Assert.DoesNotContain(rows, r => r.Section == "Notes");
            output.WriteLine($"ctfo02: {rows.Count} rows, {rows.Count(r => r.Section == "Sections")} sections");
        }
        if (Rf2Samples.PackedLevels().FirstOrDefault(s => s.Name.EndsWith("/dmo11.rfl", StringComparison.OrdinalIgnoreCase)) is { } dmo11)
        {
            var rows = Facts(dmo11.Load(), "dmo11.rfl");
            Assert.Equal("Cubic Cycle", Value(rows, "Level", "Name"));
            Assert.Equal("Darren Dienst", Value(rows, "Level", "Author"));
            Assert.Equal("colour 100, 50, 0, from 10 to 240", Value(rows, "Properties", "Fog"));
            Assert.Equal("128, 255, 128", Value(rows, "Properties", "Hardlight colour"));
            Assert.Equal("1.3", Value(rows, "Properties", "Lightmap multiplier"));
            Assert.Contains(rows, r => r.Section == "Sections" && r.Label == "Vertex lighting" && r.Value.Contains("version 5", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_rf2_level_of_all_levels_gets_plausible_facts()
    {
        var levels = Rf2Samples.PackedLevels();
        if (levels.Count == 0) return;
        var failures = new List<string>();
        int fogOn = 0, fogOff = 0, entityLevels = 0;
        var authors = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var sample in levels)
        {
            byte[] bytes = sample.Load();
            string name = Path.GetFileName(sample.Name);
            RflSummary s;
            try { s = RflReader.ReadSummary(bytes, name); }
            catch (Exception e) { failures.Add($"{name}: throws {e.Message}"); continue; }
            var rows = RflFacts.Rows(s, null, TimeZoneInfo.Utc);
            if (s.Notes.Count > 0) failures.Add($"{name}: notes {string.Join("; ", s.Notes)}");
            if (s.Sections.Any(x => x.Status is RflSectionStatus.Damaged or RflSectionStatus.Truncated or RflSectionStatus.Unknown))
                failures.Add($"{name}: a section was not read as designed");
            if (s.Sections.Any(x => x.Detail?.Contains("not read", StringComparison.Ordinal) == true)) failures.Add($"{name}: a section was not read to its end");
            if (s.Properties is not { } p || s.Rf2Properties is not { } p2) { failures.Add($"{name}: no level properties"); continue; }
            if (p.HasFog) fogOn++; else fogOff++;
            if (p.FogNear < 0 || p.FogFar < 0 || (p.HasFog && p.FogNear > p.FogFar)) failures.Add($"{name}: fog {p.FogNear}..{p.FogFar}");
            if (p.Hardness is < 0 or > 100) failures.Add($"{name}: hardness {p.Hardness}");
            if (p2.LightmapMultiplier is <= 0 or > 10 || p2.SunIntensity is < 0 or > 10 || p2.SunSpread is < 0 or > 180 || p2.HardlightIntensity is < 0 or > 255)
                failures.Add($"{name}: RF2 properties {p2}");
            if (s.SavedUtc is not { Year: 2002 or 2003 }) failures.Add($"{name}: saved {s.SavedUtc}");
            // l03s1 stores an empty author (no row, as for RF1)
            if (s.DateText is null || s.LevelName is null || s.IsMultiplayer is null) failures.Add($"{name}: no level info");
            if (s.Rooms is not > 0 || s.Faces is not > 0 || s.Vertices is not > 0) failures.Add($"{name}: geometry {s.Rooms}/{s.Faces}/{s.Vertices}");
            if (!s.HasPlayerStart) failures.Add($"{name}: no player start");
            if (s.Count("Entities") is { } entities && entities > 0)
            {
                entityLevels++;
                if (s.EntityClasses.Values.Sum() != entities) failures.Add($"{name}: {entities} entities, {s.EntityClasses.Values.Sum()} with a class");
            }
            if (!rows.Any(r => r.Section == "Sections") || Value(rows, "Level", "Loads in") != "Red Faction II") failures.Add($"{name}: rows");
            authors[s.Author ?? ""] = authors.GetValueOrDefault(s.Author ?? "") + 1;
        }
        output.WriteLine($"{levels.Count} RF2 levels; fog on {fogOn}, off {fogOff}; {entityLevels} with entities, all classed");
        output.WriteLine("authors: " + string.Join(", ", authors.OrderByDescending(a => a.Value).Select(a => $"{(a.Key.Length == 0 ? "(empty)" : a.Key)} {a.Value}")));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(40)));
        Assert.Equal(levels.Count, fogOn + fogOff);
    }

    // ---------- RF1 and Alpine rows are unchanged ----------

    /// <summary>
    /// The rows of real RF1 and Alpine levels, hashed as "Section | Label | Value[ !]" lines with a fixed presence and UTC
    /// times, as taken before the Red Faction II path was added: the RF2 work must not change one byte of them.
    /// </summary>
    [Theory]
    [InlineData("game", "levels1.vpp/L1S1.rfl", "6c4cb5c0ccb338e72c48519386e2885dd9d6254e626b671b4e598e7968de1392")]
    [InlineData("game", "levelsm.vpp/dm01.rfl", "3302eecc0c275cfdc0c107921dde67e20eb7f4492e65d5c9adc765a10f56bc8c")]
    [InlineData("redux", "rf1_rfls/ctf01.rfl", "a21011ce81c2064d4f8eb750106dff3e3f382a84e9d739665b740f867f819cf4")]
    [InlineData("redux", "rf1_rfls/dm02~.rfl", "dcecc2ad328622357a0d8a662009dd0e36e3c737aa148b8996decf9b4ef2d59b")]
    public void Rf1_and_alpine_rows_are_unchanged(string source, string name, string sha256)
    {
        byte[]? bytes = null;
        if (source == "game" && LocalPaths.GameDirectory is { } game)
        {
            string pack = Path.Combine(game, name.Split('/')[0]);
            if (File.Exists(pack)) bytes = LvlSamples.PackEntries(pack, ".rfl").FirstOrDefault(e => e.Sample.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Sample?.Load();
        }
        if (source == "redux" && LocalPaths.ReduxResearchData is { } data && File.Exists(Path.Combine(data, name)))
            bytes = File.ReadAllBytes(Path.Combine(data, name));
        if (bytes is null) return;
        var rows = Facts(bytes, Path.GetFileName(name), n => (n.Length % 3) switch { 0 => AssetPresence.SamePackfile, 1 => AssetPresence.GameData, _ => AssetPresence.Missing });
        var text = new StringBuilder();
        foreach (var r in rows) text.Append(r.Section).Append(" | ").Append(r.Label).Append(" | ").Append(r.Value).Append(r.Flagged ? " !" : "").Append('\n');
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))));
        Assert.DoesNotContain(rows, r => r.Label.EndsWith("(light bake)", StringComparison.Ordinal));
    }
}
