using System.Buffers.Binary;
using System.Numerics;
using Cairn.Formats;
using Cairn.Formats.XForge;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;
using Cairn.Vpp.XForge;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>
/// Red Faction for N-Gage's X-Forge types in the packfile view: the particle system reader (<see cref="Pr3File"/>, on
/// files built by <see cref="SyntheticPr3"/>), the facts and Info column of particle systems and scenes, the file-type
/// list, and that an N-Gage archive's entries get no RF1-only warnings; then the real archive when it is configured.
/// </summary>
public sealed class NgageFactsTests(ITestOutputHelper output)
{
    // ---- the particle system reader ----------------------------------------------------------------------------

    /// <summary>The offsets of the block's 32-bit integers (the value groups' kinds among them); every other field is a float.</summary>
    private static readonly int[] IntegerOffsets = [0x04, 0x14, 0x20, 0x2C, 0x50, 0x5C, 0x68, 0x78, 0x7C, 0x88, 0x8C, 0x9C, 0xA8, 0xBC, 0xCC, 0xD8, 0xE4, 0x108];

    /// <summary>A particle system whose every field holds its own offset (as an integer or a float).</summary>
    private static byte[] EveryFieldItsOffset()
    {
        var raw = new Dictionary<int, uint>();
        for (int at = 4; at < Pr3File.BlockSize; at += 4)
            raw[at] = IntegerOffsets.Contains(at) ? (uint)at : BitConverter.SingleToUInt32Bits(at);
        return new SyntheticPr3 { Raw = raw }.Write();
    }

    [Fact]
    public void Pr3_ReadsEveryFieldAtItsOffset()
    {
        var ps = Pr3File.Read(EveryFieldItsOffset(), "fields.pr3");
        static Vector3 V(int at) => new(at, at + 4, at + 8);
        static Pr3ValueGroup G(int at) => new(at, at + 4, at + 8);
        Assert.Equal(3, ps.Version);
        Assert.Equal(0x04, ps.Unknown04);
        Assert.Equal(V(0x08), ps.EmitterOffset);
        Assert.Equal(new[] { G(0x14), G(0x20), G(0x2C) }, ps.Unknown14);
        Assert.Equal(V(0x38), ps.Damping);
        Assert.Equal(V(0x44), ps.Gravity);
        Assert.Equal(new[] { G(0x50), G(0x5C), G(0x68) }, ps.Unknown50);
        Assert.Equal(0x74, ps.EmissionRate);
        Assert.Equal(0x78, ps.LifetimeMs);
        Assert.Equal(G(0x7C), ps.Unknown7C);
        Assert.Equal(0x88, ps.Unknown88);
        Assert.Equal(0x8C, ps.MaxParticles);
        Assert.Equal(0x90, ps.Unknown90);
        Assert.Equal(0x94, ps.Unknown94);
        Assert.Equal(0x98, ps.Unknown98);
        Assert.Equal(0x9C, ps.Unknown9C);
        Assert.Equal(0xA0, ps.StartAlpha);
        Assert.Equal(0xA4, ps.UnknownA4);
        Assert.Equal(G(0xA8), ps.UnknownA8);
        Assert.Equal(0xB4, ps.StartSize);
        Assert.Equal(0xB8, ps.EndSize);
        Assert.Equal(G(0xBC), ps.UnknownBC);
        Assert.Equal(0xC8, ps.UnknownC8);
        Assert.Equal(new[] { G(0xCC), G(0xD8), G(0xE4) }, ps.UnknownCC);
        Assert.Equal(V(0xF0), ps.UnknownF0);
        Assert.Equal(V(0xFC), ps.UnknownFC);
        Assert.Equal(0x108, ps.Unknown108);
        Assert.Equal(6, ps.Frames.Count);
        Assert.Empty(ps.Problems);
    }

    [Fact]
    public void Pr3_TheDefaultsReadBack()
    {
        var ps = Pr3File.Read(new SyntheticPr3 { Gravity = new(0, -0.52f, 0), LifetimeMs = 1814 }.Write(), "gib.pr3");
        Assert.Equal(104, ps.EmissionRate);
        Assert.Equal(1814, ps.LifetimeMs);
        Assert.Equal(new Vector3(0, -0.52f, 0), ps.Gravity);
        Assert.Equal(0.15f, ps.StartSize);
        Assert.Equal(5, ps.EndSize);
        Assert.Equal(0x003E0000, ps.Unknown04);
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"spr_rlexplosion_{i:00}.pcx"), ps.Frames);
        Assert.True(ps.FrameListEnded);
        Assert.Null(ps.DroppedName);
        Assert.Equal(1, ps.TrailingBytes);
        Assert.Empty(ps.Problems);
        Assert.True(Pr3File.LooksLikePr3(new SyntheticPr3().Write()));
        Assert.True(Pr3File.HasExtension("Explosion.PR3"));
    }

    [Theory]
    [InlineData("a.pcx|b.pcx|||", new[] { "a.pcx", "b.pcx" }, true, null, 1)]
    [InlineData("a.pcx|b.pcx||", new[] { "a.pcx", "b.pcx" }, true, null, 0)]
    [InlineData("a.pcx||junk", new[] { "a.pcx" }, true, null, 4)]
    [InlineData("a.pcx|b.pcx", new[] { "a.pcx" }, false, "b.pcx", 0)]
    [InlineData("a.pcx|", new[] { "a.pcx" }, false, null, 0)]
    [InlineData("||", new string[0], true, null, 1)]
    [InlineData("", new string[0], false, null, 0)]
    public void Pr3_TheFrameListReadsAsTheGameReadsIt(string text, string[] frames, bool ended, string? dropped, int trailing)
    {
        var ps = Pr3File.Read(new SyntheticPr3 { Frames = [], End = text }.Write(), "frames.pr3");
        Assert.Equal(frames, ps.Frames);
        Assert.Equal(ended, ps.FrameListEnded);
        Assert.Equal(dropped, ps.DroppedName);
        Assert.Equal(trailing, ps.TrailingBytes);
        bool clean = ended && frames.Length > 0;
        Assert.Equal(clean, ps.Problems.Count == 0);
        if (dropped is not null) Assert.Contains(ps.Problems, p => p.Contains("does not use", StringComparison.Ordinal) && p.Contains(dropped, StringComparison.Ordinal));
        if (frames.Length == 0) Assert.Contains(ps.Problems, p => p.Contains("empty", StringComparison.Ordinal));
    }

    [Fact]
    public void Pr3_Malformed_IsRefusedWithAMessage()
    {
        byte[] good = new SyntheticPr3().Write();
        Assert.Contains("not an X-Forge particle system", Assert.Throws<AssetFormatException>(() => Pr3File.Read([], "empty.pr3")).Message, StringComparison.Ordinal);
        Assert.Contains("not an X-Forge particle system", Assert.Throws<AssetFormatException>(() => Pr3File.Read("PX\0\u0003"u8.ToArray(), "x.pr3")).Message, StringComparison.Ordinal);
        Assert.Contains("version 2", Assert.Throws<AssetFormatException>(() => Pr3File.Read(new SyntheticPr3 { Version = 2 }.Write(), "old.pr3")).Message, StringComparison.Ordinal);
        Assert.Contains("version 768", Assert.Throws<AssetFormatException>(() => Pr3File.Read([(byte)'P', (byte)'S', 3, 0, .. good[4..]], "swapped.pr3")).Message, StringComparison.Ordinal);
        var cut = Assert.Throws<AssetFormatException>(() => Pr3File.Read(good[..200], "cut.pr3"));
        Assert.Contains("'cut.pr3' is damaged", cut.Message, StringComparison.Ordinal);
        Assert.Contains("268", cut.Message, StringComparison.Ordinal);

        // every prefix either reads or is refused, never another exception
        for (int length = 0; length <= good.Length; length++)
        {
            try
            {
                _ = Pr3File.Read(good[..length], "prefix.pr3");
                Assert.True(length >= Pr3File.BlockSize);
            }
            catch (AssetFormatException)
            {
                Assert.True(length < Pr3File.BlockSize);
            }
        }
        // random damage to the block reads (fields are whatever they hold), with problems when the game could not use them
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            byte[] bad = (byte[])good.Clone();
            for (int k = 0; k < 8; k++) bad[4 + random.Next(bad.Length - 4)] = (byte)random.Next(256);
            Pr3File.Read(bad, "random.pr3");
        }
    }

    [Fact]
    public void Pr3_ValuesTheGameCannotUse_AreProblems()
    {
        var nan = Pr3File.Read(new SyntheticPr3 { EmissionRate = float.NaN, StartSize = 40000 }.Write(), "nan.pr3");
        Assert.Contains(nan.Problems, p => p.Contains("emission rate at offset 0x74", StringComparison.Ordinal));
        Assert.Contains(nan.Problems, p => p.Contains("start size at offset 0xB4", StringComparison.Ordinal));
        Assert.Contains(Pr3File.Read(new SyntheticPr3 { MaxParticles = -1 }.Write(), "neg.pr3").Problems, p => p.Contains("negative", StringComparison.Ordinal));
        Assert.Contains(Pr3File.Read(new SyntheticPr3 { Frames = [new string('a', 300)] }.Write(), "long.pr3").Problems, p => p.Contains("300 characters", StringComparison.Ordinal));
        Assert.Contains(Pr3File.Read(new SyntheticPr3 { Frames = [.. Enumerable.Range(0, 257).Select(i => $"f{i}.pcx")] }.Write(), "many.pr3").Problems, p => p.Contains("257 frames", StringComparison.Ordinal));
    }

    // ---- facts, Info column, types -----------------------------------------------------------------------------

    private static VppItem Item(string name, byte[] bytes) => new(name, new MemorySource(bytes), VppItemState.Added);

    /// <summary>The synthetic N-Gage archive opened as the packfile module opens one.</summary>
    private static VppPackage SyntheticArchive(TempFolder temp) => CflArchives.Open(temp.Write("redfaction.cfl", SyntheticNgage.Archive()));

    private static VppFactSheet Facts(VppPackage package, string name) => VppFacts.Describe(package.Find(name)!, new VppFactsContext(package));

    private void Dump(VppFactSheet sheet)
    {
        foreach (var row in sheet.Rows) output.WriteLine($"{row.Label} = {row.Value}");
        foreach (var warning in sheet.Warnings) output.WriteLine($"WARNING {warning}");
    }

    [Fact]
    public void Pr3_Facts_NameTheKnownFields_ResolveTheFrames_AndShowTheRestRaw()
    {
        using var temp = new TempFolder();
        var package = SyntheticArchive(temp);
        var sheet = Facts(package, "explosion.pr3");
        Dump(sheet);
        Assert.Equal("X-Forge particle system", sheet["Type"]);
        Assert.Equal("Red Faction for N-Gage (read only in Cairn)", sheet["Game"]);
        Assert.Null(sheet["Error"]);
        Assert.Equal("X-Forge particle system, version 3", sheet["Format"]);
        Assert.Equal("6", sheet["Frames"]);
        Assert.StartsWith("104 per second", sheet["Emission rate"], StringComparison.Ordinal);
        Assert.Equal("500 ms (likely)", sheet["Lifetime"]);
        Assert.Equal("at most 3 alive at once", sheet["Particles"]);
        Assert.StartsWith($"{0.15f.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture)} at birth, 5 at death", sheet["Particle size"], StringComparison.Ordinal);
        Assert.Equal("spr_rlexplosion_01.pcx, in this archive", sheet["Frames: 1"]);
        Assert.Equal($"{SyntheticNgage.MissingFrame}, not in this archive; missing", sheet["Frames: 6"]);
        Assert.Contains(sheet.Warnings, w => w.StartsWith("1 frame texture is not in this archive", StringComparison.Ordinal) && w.Contains(SyntheticNgage.MissingFrame, StringComparison.Ordinal));
        Assert.Equal(17, sheet.Rows.Count(r => r.Label.StartsWith("Unknown fields: ", StringComparison.Ordinal)));
        Assert.StartsWith("4,063,232 (0x003E0000)".Replace(",", System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator, StringComparison.Ordinal), sheet["Unknown fields: 0x04"], StringComparison.Ordinal);
        Assert.Equal("kind 0, 0, 0; kind 0, 0, 0; kind 0, 0, 0", sheet["Unknown fields: 0x14"]);
        Assert.NotNull(sheet["Converts to"]);

        // loose (no package to look in): the frames are listed without a verdict
        var loose = VppFacts.Describe(Item("explosion.pr3", SyntheticNgage.Explosion()));
        Assert.Equal("spr_rlexplosion_06.pcx", loose["Frames: 6"]);
        Assert.Equal("Not loaded by the game", loose["Game"]);
        Assert.Empty(loose.Warnings);
    }

    [Fact]
    public void Pr3_Facts_OfABrokenFile_AreAnError_NotAnException()
    {
        var sheet = VppFacts.Describe(Item("bad.pr3", new SyntheticPr3().Write()[..100]));
        Assert.Contains("damaged", sheet["Error"], StringComparison.Ordinal);
        Assert.True(VppInfo.Summarize("bad.pr3", () => new MemoryStream(new byte[] { 1, 2, 3 }), 3).IsUnreadable);
    }

    [Fact]
    public void Xff_Facts_OfALevel()
    {
        using var temp = new TempFolder();
        var package = SyntheticArchive(temp);
        var sheet = Facts(package, "level_01a.xff");
        Dump(sheet);
        Assert.Equal("X-Forge scene", sheet["Type"]);
        Assert.Null(sheet["Error"]);
        Assert.StartsWith("Level (it has level markers", sheet["Scene: Kind"], StringComparison.Ordinal);
        Assert.Equal("2", sheet["Geometry: Meshes"]);
        Assert.Equal("4", sheet["Geometry: Triangles"]);
        Assert.Equal("3", sheet["Geometry: Textures"]);
        Assert.Equal("in this archive", sheet["Textures: lvl0_floor_01.pcx"]);
        Assert.Equal("in this archive", sheet["Textures: flare.pcx"]);
        Assert.Equal("not in this archive; missing", sheet["Textures: " + SyntheticNgage.MissingTexture]);
        Assert.Contains(sheet.Warnings, w => w.StartsWith("1 texture this scene names is not in this archive", StringComparison.Ordinal) && w.Contains(SyntheticNgage.MissingTexture, StringComparison.Ordinal));
        Assert.Equal("1: starthall", sheet["Markers: 2013 area"]);
        Assert.Equal("1: gr_entrig", sheet["Markers: 3106 script trigger"]);
        Assert.Equal("1: enemy01", sheet["Markers: 4202 enemy spawn (of type 3202)"]);
        Assert.Equal("2, placing 1 scene", sheet["Scene: Prefabs"]);
        Assert.Equal("2, in this archive", sheet["Prefabs: item_healthpack.xff"]);
        Assert.Equal("in this archive", sheet["Particle systems: explosion.pr3"]);
        Assert.Equal("none", sheet["Scene: Animation"]);
        Assert.Equal("none", sheet["Scene: Problems"]);
        Assert.Null(sheet["Scene: Raw chunks"]);
        Assert.Single(sheet.Warnings);
    }

    [Fact]
    public void Xff_Facts_OfACharacter_AndOfRawChunksAndProblems()
    {
        using var temp = new TempFolder();
        var package = SyntheticArchive(temp);
        var sheet = Facts(package, "enemy_ug.xff");
        Dump(sheet);
        Assert.StartsWith("Character (it has 1 animated mesh nodes and an enemy marker (type 3202))", sheet["Scene: Kind"], StringComparison.Ordinal);
        Assert.Equal("1 track, 533 ms long (from 0 to 533 ms), 30 keys per second, looping", sheet["Scene: Animation"]);
        Assert.Equal("1: player", sheet["Markers: 3202 enemy type"]);
        Assert.Equal("not in this archive; missing", sheet["Particle systems: gib.pr3"]);
        Assert.Empty(sheet.Warnings);

        // a chunk type nobody reads, and a mesh whose node does not exist
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(1007, 1, [1, 2, 3, 4]);
        uint vb = x.VertexBuffer([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ]);
        uint il = x.IndexList([0, 1, 2]);
        x.MeshInstance(999, "mesh_ghost", [SyntheticXff.Submesh(vb, il, 3, XffFile.NoReference, XffFile.NoReference, XffFile.NoReference)]);
        var odd = VppFacts.Describe(Item("odd.xff", x.ToArray()));
        Dump(odd);
        Assert.StartsWith("Model", odd["Scene: Kind"], StringComparison.Ordinal);
        Assert.Equal("1 spring (version 1) (kept as bytes, not read)", odd["Scene: Raw chunks"]);
        Assert.NotEqual("none", odd["Scene: Problems"]);
        Assert.Contains(odd.Warnings, w => w.Contains("mesh_ghost", StringComparison.Ordinal));
        Assert.Equal("named by the scene", VppFacts.Describe(Item("level.xff", SyntheticNgage.Level()))["Textures: flare.pcx"]);
        Assert.Equal("1: starthall", VppFacts.Describe(Item("level.xff", SyntheticNgage.Level()))["Markers: 2013 area"]);
    }

    [Fact]
    public void Xff_Facts_OfABrokenFile_AreAnError_NotAnException()
    {
        byte[] cut = SyntheticNgage.Level()[..100];
        var sheet = VppFacts.Describe(Item("cut.xff", cut));
        Assert.Contains("damaged", sheet["Error"], StringComparison.Ordinal);
        var line = VppInfo.Summarize("cut.xff", () => new MemoryStream(cut), cut.Length);
        Assert.True(line.IsUnreadable);
    }

    [Fact]
    public void InfoColumn_ForParticleSystemsAndScenes()
    {
        static string Line(string name, byte[] b) => VppInfo.Summarize(name, () => new MemoryStream(b), b.Length).Text;
        Assert.Equal("6 frames, 500 ms life, up to 3 particles", Line("explosion.pr3", SyntheticNgage.Explosion()));
        Assert.Equal("level, 2 meshes, 4 triangles, 3 textures", Line("level_01a.xff", SyntheticNgage.Level()));
        Assert.Equal("character, 1 mesh, 2 triangles, 1 texture, 533 ms animation", Line("enemy_ug.xff", SyntheticNgage.Character()));
    }

    [Fact]
    public void FileTypes_TheNgageTypesAreNotLoadedByThePcGame()
    {
        foreach (string name in new[] { "a.xff", "a.pr3", "a.cfl" })
        {
            var type = VppFileTypes.Describe(name);
            Assert.False(type.GameLoads, name);
            Assert.StartsWith("X-Forge", type.DisplayName, StringComparison.Ordinal);
        }
        Assert.Equal("X-Forge scene", VppFileTypes.Describe("level_01a.xff").DisplayName);
        Assert.Equal("X-Forge particle system", VppFileTypes.Describe("explosion.pr3").DisplayName);
        Assert.True(VppFileTypes.Describe("a.pcx").GameLoads);
        Assert.True(VppFileTypes.Describe("a.tga").GameLoads);

        // in an RF1 packfile they are flagged as types the game does not load, saying where they come from
        var package = new VppPackage(null, [Item("level_01a.xff", SyntheticNgage.Level()), Item("explosion.pr3", SyntheticNgage.Explosion())]);
        var problems = VppValidator.Validate(package).Where(p => p.Code == "VPP009").ToList();
        Assert.Equal(2, problems.Count);
        Assert.All(problems, p => Assert.Contains("from Red Faction for N-Gage: the PC game does not load", p.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveEntries_GetNoRf1OnlyWarnings_WhileAPackfileKeepsThem()
    {
        using var temp = new TempFolder();
        var package = SyntheticArchive(temp);
        // the HUD image is 151 x 37: RF1's powers-of-two texture rule does not apply in an N-Gage archive
        Assert.Empty(Facts(package, "pic_textbox_red.pcx").Warnings);
        Assert.All(VppValidator.Validate(package), p => Assert.StartsWith("CFL", p.Code, StringComparison.Ordinal));
        // the same image in an RF1 packfile is still flagged
        var hud = Item("pic_textbox_red.pcx", SyntheticNgage.Pcx(151, 37));
        Assert.Contains(VppFacts.Describe(hud, new VppFactsContext(new VppPackage(null, [hud]))).Warnings, w => w.Contains("powers of two", StringComparison.Ordinal));
        // every entry: only the N-Gage checks (the missing texture and frame)
        var warnings = package.Items.SelectMany(i => VppFacts.Describe(i, new VppFactsContext(package)).Warnings.Select(w => (i.Name, w))).ToList();
        foreach (var (name, w) in warnings) output.WriteLine($"{name}: {w}");
        Assert.Equal(new[] { "explosion.pr3", "level_01a.xff" }, warnings.Select(w => w.Name).Order(StringComparer.Ordinal));
    }

    // ---- the real archive --------------------------------------------------------------------------------------

    private static string? GameArchive() =>
        LocalPaths.NgageDirectory is { } dir && File.Exists(Path.Combine(dir, "redfaction.cfl")) ? Path.Combine(dir, "redfaction.cfl") : null;

    private static VppPackage? s_game;

    /// <summary>The real archive, opened once for the tests that use it.</summary>
    private static VppPackage? Game()
    {
        if (GameArchive() is not { } path) return null;
        return s_game ??= CflArchives.Open(path);
    }

    [Fact]
    public void NgageArchive_EveryParticleSystemReads()
    {
        if (Game() is not { } package) return;
        var items = package.Items.Where(i => Pr3File.HasExtension(i.Name)).ToList();
        Assert.Equal(7, items.Count);
        var names = package.Items.Select(i => i.Name).ToHashSet(Editing.VppNames.Comparer);
        foreach (var item in items)
        {
            var ps = Pr3File.Read(item.Source.ReadAll(), item.Name);
            output.WriteLine($"{item.Name}: {ps.Frames.Count} frames, rate {ps.EmissionRate}, life {ps.LifetimeMs} ms, counts {ps.Unknown88}/{ps.MaxParticles}, size {ps.StartSize} -> {ps.EndSize}, trailing {ps.TrailingBytes}, missing {string.Join(", ", ps.Frames.Where(f => !names.Contains(f)))}");
            Assert.Empty(ps.Problems);
            Assert.True(ps.FrameListEnded && ps.TrailingBytes == 1, item.Name);
            Assert.NotEmpty(ps.Frames);
            var sheet = VppFacts.Describe(item, new VppFactsContext(package));
            Assert.Null(sheet["Error"]);
        }
        var explosion = Pr3File.Read(package.Find("explosion.pr3")!.Source.ReadAll(), "explosion.pr3");
        Assert.Equal((500, 3, 0.15f, 5f, 6), (explosion.LifetimeMs, explosion.MaxParticles, MathF.Round(explosion.StartSize, 2), explosion.EndSize, explosion.Frames.Count));
        Assert.Equal(unchecked((int)0xCDCDCC12), Pr3File.Read(package.Find("gib.pr3")!.Source.ReadAll(), "gib.pr3").Unknown04);
    }

    [Fact]
    public void NgageArchive_TheFactsOfEveryScene_Build()
    {
        if (Game() is not { } package) return;
        var scenes = package.Items.Where(i => XffFile.HasExtension(i.Name)).ToList();
        Assert.Equal(264, scenes.Count);
        var kinds = new Dictionary<string, int>();
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in scenes)
        {
            var sheet = VppFacts.Describe(item, new VppFactsContext(package));
            Assert.True(sheet["Error"] is null, $"{item.Name}: {sheet["Error"]}");
            Assert.Equal("none", sheet["Scene: Problems"]);
            Assert.Null(sheet["Scene: Raw chunks"]);
            string kind = sheet["Scene: Kind"]!.Split(' ')[0];
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            foreach (var row in sheet.Rows.Where(r => r.Label.StartsWith("Textures: ", StringComparison.Ordinal) && r.Value.EndsWith("; missing", StringComparison.Ordinal)))
                missing.Add(row.Label["Textures: ".Length..]);
            Assert.False(VppInfo.Summarize(item).IsUnreadable, item.Name);
        }
        output.WriteLine(string.Join(", ", kinds.Select(k => $"{k.Key} {k.Value}")));
        output.WriteLine("missing textures: " + string.Join(", ", missing));
        Assert.Equal(35, kinds["Level"]);
        Assert.Equal(25, kinds["Character"]);
        Assert.Equal(204, kinds["Model"]);
        Assert.Equal(5, missing.Count);
    }

    [Fact]
    public void NgageArchive_OpensWithNoRf1OnlyWarningsOnItsEntries()
    {
        if (Game() is not { } package) return;
        var problems = VppValidator.Validate(package);
        Assert.All(problems, p => Assert.Equal("CFL003", p.Code));
        Assert.Equal(9, problems.Count);
        var warnings = new List<string>();
        foreach (var item in package.Items)
            foreach (string w in VppFacts.Describe(item, new VppFactsContext(package)).Warnings)
                warnings.Add($"{item.Name}: {w}");
        foreach (string w in warnings) output.WriteLine(w);
        // only the N-Gage checks may speak: textures a scene names that the archive does not hold
        Assert.All(warnings, w => Assert.Matches(@"^[^:]+\.xff: \d+ textures? this scene names (is|are) not in this archive", w));
    }
}
