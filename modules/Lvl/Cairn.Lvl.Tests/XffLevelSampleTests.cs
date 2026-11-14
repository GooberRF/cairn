using Cairn.Assets;
using Cairn.Formats.XForge;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// The 35 levels of Red Faction for N-Gage, read from the game's <c>redfaction.cfl</c> through <see cref="CflArchive"/>
/// (<see cref="LocalPaths.NgageDirectory"/>); each test returns early when the archive is not there.
/// </summary>
public sealed class XffLevelSampleTests(ITestOutputHelper output)
{
    /// <summary>The textures the archive does not hold (all used by enemy and ammunition models, none by a level).</summary>
    private static readonly string[] KnownMissing = ["txtr_fcanister1.tga"];

    private sealed class Archive
    {
        public required CflArchive Cfl { get; init; }
        public required byte[] Bytes { get; init; }

        public byte[]? Read(string name)
        {
            if (Cfl.Find(Path.GetFileName(name)) is not { } entry) return null;
            using var stream = new MemoryStream(Bytes, writable: false);
            return Cfl.ReadEntry(stream, entry);
        }
    }

    private static readonly Lazy<Archive?> Shared = new(() =>
    {
        if (LocalPaths.NgageDirectory is not { } dir) return null;
        string path = Path.Combine(dir, "redfaction.cfl");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        return new Archive { Cfl = CflArchive.Read(stream, "redfaction.cfl"), Bytes = bytes };
    });

    private static IReadOnlyList<string> Levels(Archive a) =>
        [.. a.Cfl.Entries.Select(e => e.Name).Where(n => n.StartsWith("level_", StringComparison.OrdinalIgnoreCase) && XffFile.HasExtension(n))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private static RfLevel Read(Archive a, string name) => XffLevelReader.Read(a.Read(name)!, name, new XffLevelReadOptions { ReadFile = a.Read });

    [Fact]
    public void Every_level_reads_and_converts_to_a_group_file_that_reads_back()
    {
        if (Shared.Value is not { } a)
        {
            output.WriteLine("Skipped: " + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory"));
            return;
        }
        var levels = Levels(a);
        Assert.Equal(35, levels.Count);
        var folder = Path.Combine(Path.GetTempPath(), "cairn-xff-levels-" + Guid.NewGuid().ToString("N")[..8]);
        int areas = 0, rooms = 0, boxes = 0;
        try
        {
            foreach (string name in levels)
            {
                var level = Read(a, name);
                var xff = level.Xff!;
                Assert.True(level.StaticGeometry.Faces.Length > 0, name);
                Assert.True(level.Brushes.Length > 0, name);
                // rooms are air brushes (an area's own walls, or a box when they do not close), the rest detail brushes
                var air = level.Brushes.Where(b => b.IsAir).ToList();
                Assert.True(air.Count > 0, name);
                Assert.All(level.Brushes, b => Assert.True(b.IsAir != b.IsDetail, name));
                Assert.All(air, b => Assert.True(XffAreaShell.Sealed(b.Geometry) is (true, < 0), name));
                areas += xff.Areas.Length;
                rooms += air.Count(b => b.Geometry.Faces.Any(f => (f.Flags & RfFaceFlags.Invisible) == 0));
                boxes += air.Count(b => b.Geometry.Faces.All(f => (f.Flags & RfFaceFlags.Invisible) != 0));
                Assert.Empty(xff.MissingFiles);
                Assert.True(xff.Areas.Length > 0, name);
                // every texture the level draws is in the archive
                var missing = xff.Textures.Where(t => a.Cfl.Find(t) is null).Except(KnownMissing, StringComparer.OrdinalIgnoreCase).ToList();
                Assert.True(missing.Count == 0, $"{name}: {string.Join(", ", missing)}");
                bool multiplayer = level.Objects.RespawnPoints.Length > 0;
                Assert.True(multiplayer ? level.PlayerStart is null : level.PlayerStart is not null, name);

                var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), Path.Combine(folder, Path.ChangeExtension(name, ".rfg")),
                    new LvlConvertOptions { Target = LvlConvertTarget.Rfg, Xff = new LvlXffOptions { CopyTextures = false } });
                Assert.True(result.Succeeded, name + ": " + result.Report.ToText());
                Assert.False(result.Report.HasErrors, name + ": " + result.Report.ToText());
                var back = RfgReader.Read(File.ReadAllBytes(result.Files[0]), Path.GetFileName(result.Files[0]));
                Assert.Empty(back.Problems);
                Assert.Equal(level.Brushes.Length, back.AllBrushes.Count());
                Assert.Equal(level.Objects.Count, back.Groups.Sum(g => g.Objects.Count));
                // Red Faction's editor takes .tga textures: the group file names no .pcx
                Assert.DoesNotContain(back.AllBrushes.SelectMany(b => b.Geometry.Textures), t => t.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
                output.WriteLine($"{name}: {level.StaticGeometry.Faces.Length} triangles, {xff.Areas.Length} rooms, {level.StaticGeometry.Portals.Length} portals, {level.Brushes.Length} brushes ({air.Count} air), "
                    + $"{level.Objects.Entities.Count(e => !e.StartHidden)} entities + {level.Objects.Entities.Count(e => e.StartHidden)} spawned, {level.Objects.Items.Length} items, {level.Objects.Clutter.Length} clutter, "
                    + $"{level.Objects.Triggers.Length} triggers, {level.Objects.RespawnPoints.Length} respawns; unmapped: {string.Join(", ", xff.Unmapped.Select(u => $"{u.What} x{u.Count}"))}");
            }
            // every area with walls of its own closes into a room (2026-11-10: 406 of the 407; level_05b's room10 has only
            // props), so no area is a box
            output.WriteLine($"{rooms} of {areas} areas are rooms of their own walls, {boxes} boxes");
            Assert.Equal(406, rooms);
            Assert.Equal(0, boxes);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Level_01a_has_the_objects_the_data_holds()
    {
        if (Shared.Value is not { } a) return;
        var level = Read(a, "level_01a.xff");
        var o = level.Objects;
        // placed: 3 Ultor guards and the security commander; 10 spawn points (4202) released by 10 enemy-group volumes
        Assert.Equal(3, o.Entities.Count(e => !e.StartHidden && e.ClassName == "guard1"));
        Assert.Equal(1, o.Entities.Count(e => !e.StartHidden && e.ClassName == "guard2"));
        Assert.Equal(10, o.Entities.Count(e => e.StartHidden && e.ClassName == "guard1"));
        // 8 health packs, 1 armour, 2 pistol ammo, 2 remote charges, 1 pistol
        Assert.Equal(8, o.Items.Count(i => i.ClassName == "Medical Kit"));
        Assert.Equal(1, o.Items.Count(i => i.ClassName == "Suit Repair"));
        Assert.Equal(2, o.Items.Count(i => i.ClassName == "12mm_ammo"));
        Assert.Equal(2, o.Items.Count(i => i.ClassName == "Remote Charge"));
        Assert.Equal(1, o.Items.Count(i => i.ClassName == "Handgun"));
        Assert.Equal(14, o.Items.Length);
        // 3 script triggers, 10 enemy-group volumes and the level end; one UnHide event per volume
        Assert.Equal(14, o.Triggers.Length);
        Assert.Equal(10, o.Events.Length);
        Assert.All(o.Events, e => Assert.Equal("UnHide", e.ClassName));
        Assert.Equal(new[] { "C01_entrig.txt", "C01_hendrix1.txt", "C01_minemsg1.txt" }, level.Xff!.Scripts.Select(s => s.ScriptFile).Order());
        Assert.All(level.Xff.Scripts, s => Assert.True(s.Found));
        Assert.NotNull(level.PlayerStart);
        Assert.Empty(o.RespawnPoints);
        // the nine 3117 volumes have no known meaning
        Assert.Contains(level.Xff.Unmapped, u => u.What == "marker 3117" && u.Count == 9);
        Assert.Equal(5, level.Xff.PlacedGeometry.Length);
        Assert.Equal(19, level.Xff.Areas.Length);
    }

    [Fact]
    public void Level_01a_converts_to_every_target_with_its_textures()
    {
        if (Shared.Value is not { } a) return;
        var level = Read(a, "level_01a.xff");
        var textures = new Func<string, Cairn.Formats.Imaging.BgraImage?>(name => a.Read(name) is { } bytes ? Cairn.Formats.Imaging.ImageDecoder.Decode(bytes, name) : null);
        foreach (var target in new[] { LvlConvertTarget.Glb, LvlConvertTarget.Obj, LvlConvertTarget.V3m })
        {
            var conversion = LvlConverter.Convert(LvlConvertInput.FromLevel(level), new LvlConvertOptions { Target = target, TextureLoader = textures });
            Assert.True(conversion.HasOutput, target + ": " + conversion.Report.ToText());
            Assert.False(conversion.Report.HasErrors, target + ": " + conversion.Report.ToText());
            if (target != LvlConvertTarget.V3m) Assert.Empty(conversion.Report.MissingTextures);
        }
    }

    /// <summary>A 16-bit TGA that declares alpha bits and sets none, judged by decoding it as it is (every pixel then has alpha 0).</summary>
    private static bool DeclaresAlphaItNeverSets(byte[] tga)
    {
        if (tga.Length < 18 || tga[1] != 0 || tga[2] is not (2 or 10) || tga[16] != 16 || (tga[17] & 0x0F) == 0) return false;
        var image = Cairn.Formats.Imaging.ImageDecoder.Decode(tga, "x.tga");
        for (int i = 3; i < image.Pixels.Length; i += 4)
            if (image.Pixels[i] != 0) return false;
        return true;
    }

    /// <summary>
    /// The levels draw 16-bit textures that declare an alpha bit and never set it (the N-Gage game draws them opaque):
    /// every level converted to a group file with its texture copies gets copies Cairn's own decoder reads as opaque, and
    /// the glTF PNGs of one such level (level_10b's metal grating) are opaque too.
    /// </summary>
    [Fact]
    public void Level_texture_copies_are_opaque_where_the_game_draws_them_opaque()
    {
        if (Shared.Value is not { } a) return;
        var resolver = new AssetResolver(new AssetResolverOptions()).WithOverlay("redfaction.cfl", n => a.Cfl.Find(n) is not null,
            n => a.Read(n) is { } b ? new MemoryStream(b, writable: false) : null);
        var folder = Path.Combine(Path.GetTempPath(), "cairn-xff-opaque-" + Guid.NewGuid().ToString("N")[..8]);
        var seeThrough = new List<string>();
        int checkedCopies = 0;
        try
        {
            foreach (string name in Levels(a))
            {
                var level = Read(a, name);
                // the textures the level's faces draw, on faces that are not alpha faces
                var drawnOpaque = level.StaticGeometry.Faces.Where(f => f.Texture >= 0 && (f.Flags & RfFaceFlags.HasAlpha) == 0)
                    .Select(f => level.StaticGeometry.Textures[f.Texture]).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(t => a.Read(t) is { } bytes && DeclaresAlphaItNeverSets(bytes)).ToList();
                if (drawnOpaque.Count == 0) continue;
                var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(level), Path.Combine(folder, name, Path.ChangeExtension(name, ".rfg")),
                    new LvlConvertOptions { Target = LvlConvertTarget.Rfg, TextureResolver = resolver });
                Assert.True(result.Succeeded, result.Report.ToText());
                // every texture the group file names is written beside it, as a .tga image (or the game lacks it too)
                var back = RfgReader.Read(File.ReadAllBytes(result.Files[0]), Path.GetFileName(result.Files[0]));
                foreach (string named in back.AllBrushes.SelectMany(b => b.Geometry.Textures).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    Assert.EndsWith(".tga", named, StringComparison.OrdinalIgnoreCase);
                    string copy = Path.Combine(folder, name, named);
                    // the air brushes' invisible faces use Red Faction's own texture
                    if (KnownMissing.Contains(named, StringComparer.OrdinalIgnoreCase) || named == XffLevelReader.AirTexture) continue;
                    Assert.True(File.Exists(copy), $"{name}: {named} is not beside the group file");
                    Assert.True(Cairn.Formats.Imaging.ImageDecoder.Decode(File.ReadAllBytes(copy), named).Width > 0);
                }
                foreach (string texture in drawnOpaque)
                {
                    var image = Cairn.Formats.Imaging.ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, name, texture)), texture);
                    checkedCopies++;
                    if (!Enumerable.Range(0, image.Pixels.Length / 4).Any(i => image.Pixels[i * 4 + 3] != 0)) seeThrough.Add($"{name}: {texture}");
                }
            }
            var grating = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(Read(a, "level_10b.xff")), Path.Combine(folder, "gltf", "level_10b.gltf"),
                new LvlConvertOptions { Target = LvlConvertTarget.Gltf, TextureResolver = resolver });
            Assert.True(grating.Succeeded, grating.Report.ToText());
            var png = Cairn.Formats.Imaging.ImageDecoder.Decode(File.ReadAllBytes(Path.Combine(folder, "gltf", "txtr_mtl_dark_grating01.png")), "txtr_mtl_dark_grating01.png");
            Assert.All(Enumerable.Range(0, png.Pixels.Length / 4), i => Assert.Equal(255, png.Pixels[i * 4 + 3]));
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { }
        }
        output.WriteLine($"{checkedCopies} copies of 16-bit textures checked");
        foreach (var s in seeThrough) output.WriteLine("fully see-through copy: " + s);
        Assert.True(checkedCopies > 0);
        Assert.Empty(seeThrough);
    }

    [Fact]
    public void Every_class_the_table_names_is_in_red_factions_tables()
    {
        if (LocalPaths.GameDirectory is not { } game || !Directory.Exists(game))
        {
            output.WriteLine("Skipped: " + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory"));
            return;
        }
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = game });
        string? Table(string name) => resolver.Resolve(name) is { } location ? System.Text.Encoding.Latin1.GetString(location.ReadAllBytes()) : null;
        string? entity = Table("entity.tbl"), items = Table("items.tbl"), clutter = Table("clutter.tbl");
        if (entity is null || items is null || clutter is null)
        {
            output.WriteLine("Skipped: the game directory holds no entity.tbl, items.tbl or clutter.tbl.");
            return;
        }
        // entity.tbl names its classes with $Name, items.tbl and clutter.tbl with $Class Name
        static HashSet<string> Names(string text) =>
            new(System.Text.RegularExpressions.Regex.Matches(text, @"^\s*\$(?:Class )?Name:\s*""([^""]*)""", System.Text.RegularExpressions.RegexOptions.Multiline).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        var entities = Names(entity);
        var itemClasses = Names(items);
        var clutterClasses = Names(clutter);
        foreach (var row in XffObjectTable.Default.Rows)
        {
            var set = row.Kind switch { XffObjectKind.Entity => entities, XffObjectKind.Item => itemClasses, XffObjectKind.Clutter => clutterClasses, _ => null };
            if (set is null) continue;
            Assert.True(set.Contains(row.ClassName), $"{row.FileName}: '{row.ClassName}' is not a {row.Kind} class");
        }
    }
}
