using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Cairn.Formats;
using Cairn.Formats.Rfl;
using Cairn.Formats.Vpp;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

public sealed class RflReaderTests(ITestOutputHelper output)
{
    // ---------- synthetic levels (always available) ----------

    [Fact]
    public void SyntheticStockLevelReadsEveryField()
    {
        var s = RflReader.ReadSummary(SyntheticLevel.Stock(), "synthetic.rfl");
        Assert.Equal(200, s.Version);
        Assert.Equal(RflEra.Stock12, s.Era);
        Assert.Equal("RED 1.2 (200)", s.VersionLabel);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(SyntheticLevel.Timestamp), s.SavedUtc);
        Assert.Equal("Synthetic", s.LevelName);
        Assert.Equal("Header name", s.HeaderLevelName);
        Assert.Equal("Tester", s.Author);
        Assert.Equal("mymod", s.ModName);
        Assert.True(s.IsMultiplayer);
        Assert.NotNull(s.Properties);
        Assert.Equal("rock02.tga", s.Properties!.GeomodTexture);
        Assert.Equal(50, s.Properties.Hardness);
        Assert.Equal(new RflColor(10, 20, 30, 255), s.Properties.AmbientColor);
        Assert.Equal(100f, s.Properties.FogFar);
        Assert.Equal(1, s.Rooms);
        Assert.Equal(1, s.LiquidRooms);
        Assert.Equal(1, s.Vertices);
        Assert.Equal(0, s.Faces);
        Assert.Equal(2, s.GeometryTextures);
        Assert.Equal(1, s.Count("Lights"));
        Assert.Equal(1, s.OmniLights);
        Assert.Equal(2, s.Count("Events"));
        Assert.Equal(1, s.EventClasses["Play_Sound"]);
        Assert.True(s.HasRedPlusData);
        Assert.False(s.IsTruncated);

        var names = s.References.Select(r => (r.Kind, r.Name)).ToList();
        Assert.Contains((RflReferenceKind.Texture, "wall.tga"), names);
        Assert.Contains((RflReferenceKind.Texture, "floor.vbm"), names);
        Assert.Contains((RflReferenceKind.Texture, "water.tga"), names);
        Assert.Contains((RflReferenceKind.Texture, "rock02.tga"), names);
        Assert.Contains((RflReferenceKind.Sound, "wind.wav"), names);
        Assert.Contains((RflReferenceKind.Sound, "boom.wav"), names);
        // RED keeps stale strings on events whose class changed: those are not references.
        Assert.DoesNotContain(names, n => n.Name == "stale.v3m");
        Assert.Equal(["event Play_Sound"], s.References.Single(r => r.Name == "boom.wav").Places);
        // The unknown section is skipped by its length and noted.
        Assert.Contains(s.Sections, x => x.Id == 0x0AFBAE0C && x.Status == RflSectionStatus.Unknown);
        Assert.Contains(s.Notes, n => n.Contains("0x0AFBAE0C"));
    }

    [Fact]
    public void StreamAndSpanAgree()
    {
        byte[] bytes = SyntheticLevel.Stock();
        var a = RflReader.ReadSummary(bytes, "x.rfl");
        var b = RflReader.ReadSummary(new MemoryStream(bytes), "x.rfl");
        var c = RflReader.ReadSummary(new NonSeekable(bytes), "x.rfl");
        foreach (var other in new[] { b, c })
        {
            AssertSameSummary(a, other);
        }
    }

    [Fact]
    public void AlpinePropertiesShorterThanTheirVersionKeepDefaults()
    {
        // Chunk version 6, but the body stops after the version-2 fields.
        var s = RflReader.ReadSummary(SyntheticLevel.AlpineShortProps(), "alpine.rfl");
        Assert.Equal(RflEra.Alpine, s.Era);
        Assert.Equal("Alpine 1.5.0 (306)", s.VersionLabel);
        Assert.NotNull(s.Alpine);
        Assert.Equal(6u, s.Alpine!.ChunkVersion);
        Assert.False(s.Alpine.LegacyCyclicTimers);
        Assert.True(s.Alpine.LegacyMovers);
        Assert.Null(s.Alpine.StartsWithHeadlamp);
        Assert.Null(s.Alpine.SunEnabled);
        Assert.True(s.Alpine.EndedEarly);
        Assert.Equal(7, s.Count("Alpine coronas"));
        var rows = RflFacts.Rows(s, null);
        Assert.Contains(rows, r => r.Label == "Starts with headlamp" && r.Value.Contains("default"));
        Assert.Contains(rows, r => r.Label == "Loads in" && r.Value == "Alpine Faction 1.5.0 or later");
    }

    [Fact]
    public void NotALevelAndShortHeadersThrowFormatErrors()
    {
        Assert.Contains("empty", Assert.Throws<AssetFormatException>(() => RflReader.ReadSummary([], "e.rfl")).Message);
        Assert.Contains("not a Red Faction level",
            Assert.Throws<AssetFormatException>(() => RflReader.ReadSummary(new byte[] { 0, 9, 0, 0, 200, 0, 0, 0 }, "m.rfl")).Message);
        byte[] level = SyntheticLevel.Stock();
        Assert.Contains("header", Assert.Throws<AssetFormatException>(() => RflReader.ReadSummary(level.AsSpan(0, 20), "h.rfl")).Message);
    }

    [Fact]
    public void TruncatedAndHandEditedLevelsDegrade()
    {
        byte[] level = SyntheticLevel.Stock();
        var cut = RflReader.ReadSummary(level.AsSpan(0, level.Length - 30), "cut.rfl");
        Assert.True(cut.IsTruncated);
        Assert.Contains(cut.Notes, n => n.Contains("claims") || n.Contains("end marker") || n.Contains("section header"));
        Assert.Equal(1, cut.Count("Lights"));

        byte[] edited = (byte[])level.Clone();
        BitConverter.GetBytes(250).CopyTo(edited, 4);
        var odd = RflReader.ReadSummary(edited, "odd.rfl");
        Assert.Equal(RflEra.Unknown, odd.Era);
        Assert.Empty(odd.Sections);
        Assert.Contains(odd.Notes, n => n.Contains("Unsupported level version 250"));
    }

    [Fact]
    public void FactsShowIdentityPropertiesAndPresence()
    {
        var s = RflReader.ReadSummary(SyntheticLevel.Stock(), "synthetic.rfl");
        var rows = RflFacts.Rows(s, name => name switch
        {
            "wall.tga" => AssetPresence.SamePackfile,
            "boom.wav" => AssetPresence.Missing,
            _ => AssetPresence.GameData,
        });
        Assert.Equal("Level", rows[0].Section);
        Assert.Contains(rows, r => r.Label == "Name" && r.Value == "Synthetic");
        Assert.Contains(rows, r => r.Label == "Saved" && r.Value.Contains("UTC"));
        Assert.Contains(rows, r => r.Label == "Ambient light" && r.Value == "10, 20, 30");
        Assert.Contains(rows, r => r.Label == "boom.wav" && r.Flagged && r.Value.Contains("missing"));
        Assert.Contains(rows, r => r.Label == "wall.tga" && !r.Flagged && r.Value.Contains("this packfile"));
        Assert.Contains(rows, r => r.Section == "References" && r.Label == "Missing" && r.Flagged);
        // Section order follows the details pane: identity first, the section list last.
        var order = rows.Select(r => r.Section).Distinct().ToList();
        Assert.True(order.IndexOf("Level") < order.IndexOf("Properties"));
        Assert.Equal("Sections", order[^1]);
    }

    [Fact]
    public void MutationFuzzOnlyEverThrowsFormatErrors()
    {
        var samples = new List<(string Name, byte[] Bytes)>
        {
            ("synthetic.rfl", SyntheticLevel.Stock()),
            ("alpine.rfl", SyntheticLevel.AlpineShortProps()),
        };
        foreach (var (pack, entry) in new[] { (@"levelspf.vpp", "pdm01.rfl"), (@"user_maps\multi\DM-fy_snow.vpp", "DM-fy_snow.rfl") })
        {
            if (ReadGameLevel(pack, entry) is { } bytes) samples.Add((entry, bytes));
        }

        var random = new Random(1234);
        int runs = 0;
        long worstTicks = 0, worstAllocated = 0;
        foreach (var (name, original) in samples)
        {
            var mutants = new List<byte[]>();
            // Truncations everywhere in the first few kilobytes, then coarser.
            for (int len = 0; len < original.Length; len += len < 4096 ? 7 : Math.Max(1, original.Length / 200))
            {
                mutants.Add(original[..len]);
            }
            var offsets = SectionHeaderOffsets(original);
            for (int i = 0; i < 400; i++)
            {
                byte[] m = (byte[])original.Clone();
                switch (i % 4)
                {
                    case 0: // inflated or negative count anywhere
                        Write32(m, random.Next(8, m.Length - 4) & ~3, random.Next(2) == 0 ? int.MaxValue - random.Next(1000) : -random.Next(1, 1000));
                        break;
                    case 1: // bogus section length
                        if (offsets.Count > 0) Write32(m, offsets[random.Next(offsets.Count)] + 4, random.Next(3) switch { 0 => -1, 1 => int.MaxValue, _ => random.Next(0, 64) });
                        break;
                    case 2: // byte flips
                        for (int k = 0; k < 8; k++) m[random.Next(m.Length)] ^= (byte)random.Next(1, 256);
                        break;
                    default: // string lengths blown up
                        int at = random.Next(8, m.Length - 2);
                        m[at] = 0xFF; m[at + 1] = 0xFF;
                        break;
                }
                mutants.Add(m);
            }

            foreach (var m in mutants)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                var watch = Stopwatch.StartNew();
                try
                {
                    RflReader.ReadSummary(m, name);
                    RflReader.ReadSummary(new MemoryStream(m), name);
                }
                catch (AssetFormatException)
                {
                }
                watch.Stop();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                worstTicks = Math.Max(worstTicks, watch.ElapsedTicks);
                worstAllocated = Math.Max(worstAllocated, allocated - 4L * m.Length);
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{name}: a mutant took {watch.Elapsed}");
                Assert.True(allocated < 16L * m.Length + (4 << 20), $"{name}: a mutant of {m.Length} bytes allocated {allocated}");
                runs++;
            }
        }
        output.WriteLine($"{runs} mutants of {samples.Count} levels; slowest {TimeSpan.FromTicks(worstTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency).TotalMilliseconds:F1} ms; "
            + $"worst allocation beyond 4x the file {worstAllocated / 1024} KB.");
    }

    // ---------- the game folder's corpus ----------

    [Fact]
    public void EveryLevelInTheGamePackfilesReads()
    {
        var packfiles = TestData.GamePackfiles();
        if (packfiles.Count == 0) return;

        var watch = Stopwatch.StartNew();
        var failures = new ConcurrentBag<string>();
        var formatErrors = new ConcurrentBag<string>();
        var implausibleTimes = new ConcurrentBag<string>();
        var mismatches = new ConcurrentBag<string>();
        var versions = new ConcurrentDictionary<int, int>();
        var noteSamples = new ConcurrentBag<string>();
        int levels = 0, truncated = 0, damaged = 0, withNotes = 0, comparedOverloads = 0;
        long bytes = 0;
        Parallel.ForEach(packfiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) }, file =>
        {
            VppArchive archive;
            try { archive = VppArchive.Open(file.FullName); }
            catch (AssetFormatException) { return; }
            catch (IOException) { return; }
            foreach (var entry in archive.Entries)
            {
                if (!entry.Name.EndsWith(".rfl", StringComparison.OrdinalIgnoreCase)) continue;
                string where = $"{Path.GetRelativePath(LocalPaths.GameDirectory!, file.FullName)}|{entry.Name}";
                int n = Interlocked.Increment(ref levels);
                Interlocked.Add(ref bytes, entry.Size);
                try
                {
                    RflSummary s;
                    using (var stream = archive.OpenEntry(entry))
                    {
                        s = RflReader.ReadSummary(stream, entry.Name);
                    }
                    // Every 25th level (and every small one) also goes through the in-memory path: same answer.
                    if (n % 25 == 0 || entry.Size < 64 * 1024)
                    {
                        var span = RflReader.ReadSummary(archive.ReadEntry(entry), entry.Name);
                        if (!SameSummary(s, span)) mismatches.Add(where);
                        Interlocked.Increment(ref comparedOverloads);
                    }
                    versions.AddOrUpdate(s.Version, 1, (_, c) => c + 1);
                    if (s.IsTruncated) Interlocked.Increment(ref truncated);
                    if (s.Sections.Any(x => x.Status == RflSectionStatus.Damaged)) Interlocked.Increment(ref damaged);
                    foreach (var note in s.Notes) noteSamples.Add($"{where} (v{s.Version}): {note}");
                    if (s.Notes.Count > 0) Interlocked.Increment(ref withNotes);
                    if (s.SavedUtc is { } t && RflReader.IsSupportedVersion(s.Version)
                        && (t.Year < 2000 || t > DateTimeOffset.UtcNow.AddDays(2)))
                    {
                        implausibleTimes.Add($"{where}: {t:u}");
                    }
                    _ = RflFacts.Rows(s, _ => AssetPresence.Unknown);
                }
                catch (AssetFormatException e)
                {
                    formatErrors.Add($"{where}: {e.Message}");
                }
                catch (Exception e)
                {
                    failures.Add($"{where}: {e.GetType().Name}: {e.Message}");
                }
            }
        });
        watch.Stop();

        output.WriteLine($"{levels} levels ({bytes / (1 << 20)} MB) in {packfiles.Count} packfiles: {watch.Elapsed.TotalSeconds:F1} s.");
        output.WriteLine($"Versions: {string.Join(", ", versions.OrderBy(p => p.Key).Select(p => $"{p.Key} x{p.Value}"))}.");
        output.WriteLine($"Format errors {formatErrors.Count}, truncated {truncated}, with a damaged section {damaged}, with notes {withNotes}; "
            + $"{comparedOverloads} compared stream vs memory.");
        foreach (var e in formatErrors.Take(20)) output.WriteLine("  format error: " + e);
        foreach (var e in implausibleTimes.Take(20)) output.WriteLine("  implausible time: " + e);
        foreach (var e in noteSamples.OrderBy(x => x).Take(60)) output.WriteLine("  note: " + e);

        Assert.Empty(failures);
        Assert.Empty(mismatches);
        // The only files that may not open are the categories research found: empty files, files that
        // are not levels (wrong signature) and levels whose header is cut short.
        Assert.All(formatErrors, e => Assert.Matches("is empty|is not a Red Faction level|too short|header", e));
        Assert.Empty(implausibleTimes);
    }

    public static TheoryData<string, string> SpotCheckLevels => new()
    {
        { "levels1.vpp", "L1S1.rfl" },
        { "levelspf.vpp", "pdm01.rfl" },
        { @"user_maps\multi\DM-fy_snow.vpp", "DM-fy_snow.rfl" },
        { @"user_maps\multi\CTF-OutlawsB3.vpp", "CTF-OutlawsB3.rfl" },
        { @"user_maps\multi\battle_royal.vpp", "battle_royal.rfl" },
    };

    /// <summary>Counts and names agree exactly with the research parser's output for these levels.</summary>
    [Theory]
    [MemberData(nameof(SpotCheckLevels))]
    public void SpotChecksAgreeWithTheResearchParser(string packfile, string entry)
    {
        if (ReadGameLevel(packfile, entry) is not { } bytes) return;
        var s = RflReader.ReadSummary(bytes, entry);
        Assert.Empty(s.Notes);
        Assert.False(s.IsTruncated);
        switch (entry)
        {
            case "L1S1.rfl":
                Assert.Equal(RflEra.Stock10, s.Era);
                Assert.Equal(998713409, s.SavedUtc!.Value.ToUnixTimeSeconds());
                Assert.Equal("Live Mines", s.LevelName);
                Assert.Null(s.HeaderLevelName);
                Assert.Equal("Jasen Whiteside ", s.Author);
                Assert.Equal("Friday, August 24, 2001 23:23:29", s.DateText);
                Assert.False(s.IsMultiplayer);
                Assert.Equal(("rck_brown08.tga", 0, new RflColor(96, 96, 96, 255), new RflColor(173, 126, 39, 255), 64f),
                    (s.Properties!.GeomodTexture, s.Properties.Hardness, s.Properties.AmbientColor, s.Properties.FogColor, s.Properties.FogFar));
                AssertGeometry(s, rooms: 54, faces: 7418, vertices: 6658, sky: 0, liquid: 3, portals: 29, textures: 17);
                AssertCounts(s, ("Lights", 231), ("Events", 184), ("Entities", 78), ("Items", 22), ("Clutter", 170), ("Triggers", 61),
                    ("Brushes", 160), ("Groups", 28), ("Movers", 5), ("Moving groups", 5), ("Decals", 43), ("Particle emitters", 2), ("Nav points", 333));
                Assert.Equal((172, 8, 51), (s.OmniLights, s.SpotLights, s.TubeLights));
                Assert.Equal(5, s.EntityClasses.Count);
                Assert.Equal(42, s.EntityClasses["env_guard"]);
                Assert.Equal(11, s.EntityClasses["Grabber"]);
                Assert.Equal(32, s.EventClasses.Count);
                Assert.Equal(5, s.EventClasses["Delay"]);
                Assert.Equal(3, s.ItemClasses["Handgun"]);
                Assert.Equal(13, s.ClutterClasses.Count);
                AssertPreloads(s, 53, 5, 129, 37, 10);
                Assert.Contains(s.References, r => r.Name == "Amb_Cave_03L.wav" && r.Places.Contains("ambient sound"));
                Assert.Contains(s.References, r => r.Name == "DoorLoop_2.5.wav" && r.Places.Contains("moving group sound"));
                Assert.Contains(s.References, r => r.Name == "mtl_L7_door.tga" && r.Places.Contains("mover geometry"));
                Assert.Contains(s.References, r => r.Name == "rck_brown08.tga" && r.Places.Contains("level geometry") && r.Places.Contains("geomod texture"));
                Assert.Equal(8, s.ReferencesOf(RflReferenceKind.Sound).Count(r => r.Places.Contains("ambient sound")));
                break;
            case "pdm01.rfl":
                Assert.Equal(RflEra.Stock12, s.Era);
                Assert.Equal(1248930882, s.SavedUtc!.Value.ToUnixTimeSeconds());
                Assert.Equal("Use and Abuse v2.1", s.LevelName);
                Assert.Equal("Chris \"Goober\" Parsons", s.Author);
                Assert.True(s.IsMultiplayer);
                Assert.True(s.HasMovers);
                Assert.False(s.Properties!.HasFog);
                AssertGeometry(s, rooms: 32, faces: 2505, vertices: 3146, sky: 1, liquid: 4, portals: 4, textures: 29);
                AssertCounts(s, ("Lights", 84), ("Events", 21), ("Items", 29), ("Clutter", 13), ("Triggers", 13), ("Respawn points", 7),
                    ("Brushes", 257), ("Groups", 13), ("Moving groups", 9), ("Decals", 4), ("Nav points", 0));
                Assert.Null(s.Count("Entities"));
                Assert.Equal((7, 7, 7), (s.RespawnRed, s.RespawnBlue, s.RespawnBot));
                Assert.Equal((72, 6, 6), (s.OmniLights, s.SpotLights, s.TubeLights));
                Assert.Equal(9, s.EventClasses["Delay"]);
                Assert.Equal(2, s.ItemClasses["10gauge_ammo"]);
                AssertPreloads(s, 9, 1, 45, 27, 6);
                break;
            case "DM-fy_snow.rfl":
                Assert.Equal("Alpine 1.2.0 (302)", s.VersionLabel);
                Assert.Equal(1767301627, s.SavedUtc!.Value.ToUnixTimeSeconds());
                Assert.Equal("MysticaL-AceR", s.Author);
                AssertGeometry(s, rooms: 6, faces: 621, vertices: 810, sky: 1, liquid: 0, portals: 0, textures: 11);
                AssertCounts(s, ("Lights", 9), ("Items", 53), ("Respawn points", 12), ("Brushes", 64), ("Decals", 1));
                Assert.Equal((6, 6, 12), (s.RespawnRed, s.RespawnBlue, s.RespawnBot));
                Assert.Equal(new RflColor(154, 217, 248, 255), s.Properties!.AmbientColor);
                Assert.Equal(3u, s.Alpine!.ChunkVersion);
                Assert.Equal((false, false, true, false, 2f), (s.Alpine.LegacyCyclicTimers, s.Alpine.LegacyMovers, s.Alpine.StartsWithHeadlamp,
                    s.Alpine.OverrideStaticMeshAmbient, s.Alpine.StaticMeshAmbientModifier));
                Assert.False(s.Alpine.EndedEarly);
                AssertPreloads(s, 2, 1, 45, 27, 2);
                break;
            case "CTF-OutlawsB3.rfl":
                Assert.Equal("Alpine 1.4.0 (305)", s.VersionLabel);
                Assert.True(s.HasRedPlusData);
                Assert.Contains(s.Sections, x => x.Id == 0x5ED00001 && x.Status == RflSectionStatus.Skipped);
                Assert.Equal("Romek", s.Author);
                AssertGeometry(s, rooms: 60, faces: 5661, vertices: 7347, sky: 1, liquid: 0, portals: 2, textures: 42);
                AssertCounts(s, ("Lights", 46), ("Events", 1), ("Items", 90), ("Triggers", 1), ("Respawn points", 18), ("Brushes", 507),
                    ("Groups", 6), ("Decals", 18), ("Alpine meshes", 111));
                Assert.Equal((17, 29, 0), (s.OmniLights, s.SpotLights, s.TubeLights));
                Assert.Equal((9, 9, 18), (s.RespawnRed, s.RespawnBlue, s.RespawnBot));
                Assert.Equal(4u, s.Alpine!.ChunkVersion);
                Assert.Equal(0, s.Alpine.GeoableBrushes);
                Assert.Equal(28, s.References.Count(r => r.Places.Contains("Alpine mesh")));
                Assert.Contains(s.References, r => r.Name == "spool_wire.v3m" && r.Kind == RflReferenceKind.Mesh);
                Assert.Contains(s.References, r => r.Name == "rf_red01_A.tga" && r.Places.Contains("decal"));
                AssertPreloads(s, 15, 1, 45, 30, 5);
                break;
            case "battle_royal.rfl":
                Assert.Equal("Alpine 1.5.0 (306)", s.VersionLabel);
                Assert.Equal(1790540143, s.SavedUtc!.Value.ToUnixTimeSeconds());
                Assert.Equal("No Level Name", s.LevelName);
                AssertGeometry(s, rooms: 15249, faces: 518973, vertices: 327011, sky: 0, liquid: 0, portals: 0, textures: 17);
                AssertCounts(s, ("Brushes", 16144), ("Groups", 1), ("Nav points", 0));
                Assert.Null(s.Count("Lights"));
                var a = s.Alpine!;
                Assert.Equal(5u, a.ChunkVersion);
                Assert.Equal((false, 0f, 90f, new RflColor(255, 255, 255, 255), 1f), (a.SunEnabled, a.SunYaw, a.SunPitch, a.SunColor, a.SunIntensity));
                Assert.Equal((true, true, 0, true, false, false, true, false, false), (a.SunCastsBakedShadows, a.SunAffectsMeshes, a.SunMeshMode,
                    a.SunDrivesShadowMap, a.LegacyLighting, a.HighResolutionLightmaps, a.LiquidOccludes, a.InvisibleFacesOcclude, a.AlphaFacesOcclude));
                Assert.Equal((0, 0, 0), (a.LightmapDensity, a.LightmapFlags, a.LightmapCompression));
                Assert.False(a.EndedEarly);
                AssertPreloads(s, 1, 1, 45, 14, 2);
                break;
        }
    }

    // ---------- helpers ----------

    private static byte[]? ReadGameLevel(string packfile, string entry)
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null) return null;
        string path = Path.Combine(dir, packfile);
        if (!File.Exists(path)) return null;
        var archive = VppArchive.Open(path);
        return archive.TryGetEntry(entry, out var e) ? archive.ReadEntry(e) : null;
    }

    private static void AssertGeometry(RflSummary s, int rooms, int faces, int vertices, int sky, int liquid, int portals, int textures) =>
        Assert.Equal((rooms, faces, vertices, sky, liquid, portals, textures),
            (s.Rooms!.Value, s.Faces!.Value, s.Vertices!.Value, s.SkyRooms!.Value, s.LiquidRooms!.Value, s.Portals!.Value, s.GeometryTextures!.Value));

    private static void AssertCounts(RflSummary s, params (string Label, int Count)[] expected)
    {
        foreach (var (label, count) in expected) Assert.True(s.Count(label) == count, $"{label}: expected {count}, read {s.Count(label)}");
    }

    private static void AssertPreloads(RflSummary s, int bitmaps, int characterMeshes, int animations, int staticMeshes, int effects) =>
        Assert.Equal((bitmaps, characterMeshes, animations, staticMeshes, effects),
            (s.Preloads.Bitmaps.Count, s.Preloads.CharacterMeshes.Count, s.Preloads.Animations.Count, s.Preloads.StaticMeshes.Count, s.Preloads.Effects.Count));

    private static bool SameSummary(RflSummary a, RflSummary b)
    {
        try
        {
            AssertSameSummary(a, b);
            return true;
        }
        catch (Xunit.Sdk.XunitException)
        {
            return false;
        }
    }

    private static void AssertSameSummary(RflSummary a, RflSummary b)
    {
        Assert.Equal(a.Version, b.Version);
        Assert.Equal(a.SavedUtc, b.SavedUtc);
        Assert.Equal(a.LevelName, b.LevelName);
        Assert.Equal(a.Properties, b.Properties);
        Assert.Equal(a.Alpine, b.Alpine);
        Assert.Equal((a.Rooms, a.Faces, a.Vertices), (b.Rooms, b.Faces, b.Vertices));
        Assert.Equal(a.Counts, b.Counts);
        Assert.Equal(a.References.Select(r => (r.Name, r.Kind, string.Join("|", r.Places))), b.References.Select(r => (r.Name, r.Kind, string.Join("|", r.Places))));
        Assert.Equal(a.Sections, b.Sections);
        Assert.Equal(a.Notes, b.Notes);
        Assert.Equal(a.IsTruncated, b.IsTruncated);
    }

    private static List<int> SectionHeaderOffsets(byte[] level)
    {
        var offsets = new List<int>();
        try
        {
            var s = RflReader.ReadSummary(level, "x.rfl");
            offsets.AddRange(s.Sections.Select(x => (int)x.Offset));
        }
        catch (AssetFormatException)
        {
        }
        return offsets;
    }

    private static void Write32(byte[] b, int at, int value)
    {
        if (at >= 0 && at + 4 <= b.Length) BitConverter.GetBytes(value).CopyTo(b, at);
    }

    /// <summary>A read-only stream that cannot seek, like a network or pipe stream.</summary>
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 100));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Small hand-built levels that exercise each part of the reader.</summary>
internal static class SyntheticLevel
{
    public const uint Timestamp = 1_700_000_000;

    public static byte[] Stock()
    {
        var w = new LevelWriter(200, "Header name", "mymod");
        w.Section(0x900, b =>
        {
            b.Str("rock02.tga"); b.I32(50); b.Bytes(10, 20, 30, 255); b.U8(0); b.Bytes(1, 2, 3, 255); b.F32(5); b.F32(100);
        });
        w.Section(0x100, b =>
        {
            b.I32(0); b.I32(0); b.Str("");
            b.I32(2); b.Str("wall.tga"); b.Str("floor.vbm");
            b.I32(0); // face scrolls
            b.I32(1); // one liquid room with ambient light
            b.I32(1); b.Zero(24); b.Bytes(0, 0, 0, 0, 1, 1, 0, 0); b.F32(0); b.Str("");
            b.F32(1); b.Bytes(0, 0, 255, 255); b.Str("water.tga"); b.Zero(37);
            b.Bytes(9, 9, 9, 255);
            b.I32(0); b.I32(0); // sub-rooms, portals
            b.I32(1); b.Zero(12); // one vertex
            b.I32(0); b.I32(0); // faces, surfaces
        });
        w.Section(0x300, b =>
        {
            b.I32(1);
            ObjectHead(b, "Light");
            b.I32(1 << 4); b.Zero(4 + 16 + 4 + 28);
        });
        w.Section(0x500, b =>
        {
            b.I32(1); b.I32(5); b.Zero(12); b.U8(0); b.Str("wind.wav"); b.Zero(16);
        });
        w.Section(0x600, b =>
        {
            b.I32(2);
            Event(b, "Play_Sound", "boom.wav");
            Event(b, "Delay", "stale.v3m");
        });
        w.Section(0x5ED00001, b => b.Bytes(1, 2, 3));
        w.Section(0x0AFBAE0C, b => b.Zero(10));
        w.Section(0x01000000, b =>
        {
            b.I32(1); b.Str("Synthetic"); b.Str("Tester"); b.Str("Monday, November 13, 2023 22:13:20"); b.U8(0); b.U8(1);
            b.Bytes(0xFF, 0xFF, 0xFF, 0xFF, 0x12); // garbage editor views are never parsed
        });
        return w.Finish();
    }

    public static byte[] AlpineShortProps()
    {
        var w = new LevelWriter(306, "Alpine", "");
        w.Section(0x0AFBA5ED, b => { b.I32(6); b.U8(0); b.U8(1); });
        w.Section(0x0AFBAE03, b => { b.I32(7); b.Zero(7 * 20); });
        w.Section(0x01000000, b => { b.I32(1); b.Str("Alpine"); b.Str("Me"); b.Str(""); b.U8(0); b.U8(0); });
        return w.Finish();
    }

    private static void ObjectHead(LevelWriter.Body b, string cls)
    {
        b.I32(1); b.Str(cls); b.Zero(48); b.Str(""); b.U8(0);
    }

    private static void Event(LevelWriter.Body b, string cls, string s1)
    {
        b.I32(2); b.Str(cls); b.Zero(12); b.Str(""); b.U8(0); b.F32(0); b.U8(0); b.U8(0); b.I32(0); b.I32(0); b.F32(0); b.F32(0);
        b.Str(s1); b.Str(""); b.I32(0); b.Zero(4);
    }

    private sealed class LevelWriter
    {
        private readonly MemoryStream _out = new();

        public LevelWriter(int version, string name, string mod)
        {
            var b = new Body(_out);
            b.I32(unchecked((int)0xD4BADA55)); b.I32(version); b.I32((int)Timestamp); b.I32(0); b.I32(0); b.I32(0); b.I32(0);
            b.Str(name); b.Str(mod);
        }

        public void Section(uint id, Action<Body> write)
        {
            var body = new MemoryStream();
            write(new Body(body));
            var b = new Body(_out);
            b.I32(unchecked((int)id)); b.I32((int)body.Length);
            body.WriteTo(_out);
        }

        public byte[] Finish()
        {
            var b = new Body(_out);
            b.I32(0); b.I32(0);
            return _out.ToArray();
        }

        public sealed class Body(Stream s)
        {
            public void I32(int v) => s.Write(BitConverter.GetBytes(v));
            public void F32(float v) => s.Write(BitConverter.GetBytes(v));
            public void U8(byte v) => s.WriteByte(v);
            public void Bytes(params byte[] v) => s.Write(v);
            public void Zero(int n) => s.Write(new byte[n]);
            public void Str(string v)
            {
                var bytes = Encoding.Latin1.GetBytes(v);
                s.Write(BitConverter.GetBytes((ushort)bytes.Length));
                s.Write(bytes);
            }
        }
    }
}
