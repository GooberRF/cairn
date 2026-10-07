using Cairn.Assets;
using Cairn.Formats.Rfl;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>
/// Whether a file a level references counts as present follows the engine's lookup rules: bare file name,
/// animations cut at the first dot and loaded as <c>.rfa</c>, the texture supersede chain, A-Z-only case folding.
/// </summary>
public sealed class LevelReferenceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("walk.mvf", "walk.rfa")]
    [InlineData("walk.rfa", "walk.rfa")]
    [InlineData("walk.old.mvf", "walk.rfa")]
    [InlineData(@"anims\walk.MVF", "walk.rfa")]
    [InlineData("maps/door.v3m", "door.v3m")]
    [InlineData("boom.wav", "boom.wav")]
    public void EngineCandidates_FollowTheEngine(string reference, string first)
    {
        Assert.Equal(first, VppFacts.EngineCandidates(reference)[0]);
    }

    [Fact]
    public void EngineCandidates_TextureTriesTheSupersedeChainFirst()
    {
        var candidates = VppFacts.EngineCandidates("wall.tga");
        Assert.Equal(["wall.atx", "wall.dds", "wall.png", "wall.jpg", "wall.jpeg", "wall.vbm", "wall.tga", "wall.pcx", "wall.vaf", "wall.m2v"], candidates);
        Assert.Equal(["miner.v3c"], VppFacts.EngineCandidates("miner.vcm"));
        Assert.Equal(["chair.v3m", "chair.v3c"], VppFacts.EngineCandidates("chair.V3D").Select(c => c.ToLowerInvariant()));
        Assert.Empty(VppFacts.EngineCandidates(@"folder\"));
    }

    [Fact]
    public void Presence_InThisPackfile_UsesTheEngineNames()
    {
        var package = TestData.Small(("walk.rfa", 16), ("WALL.dds", 16), ("door.v3m", 16), ("Übel.wav", 16));
        var presence = VppFacts.LevelPresence(new VppFactsContext(package))!;
        Assert.Equal(AssetPresence.SamePackfile, presence("walk.mvf"));
        Assert.Equal(AssetPresence.SamePackfile, presence("walk.x.rfa"));
        Assert.Equal(AssetPresence.SamePackfile, presence("wall.tga"));
        Assert.Equal(AssetPresence.SamePackfile, presence(@"meshes\DOOR.V3M"));
        Assert.Equal(AssetPresence.SamePackfile, presence("Übel.WAV"));
        // Without game data nothing else can be checked; a name that differs outside A-Z is not the same name.
        Assert.Equal(AssetPresence.Unknown, presence("übel.wav"));
        Assert.Equal(AssetPresence.Unknown, presence("run.mvf"));
    }

    [Fact]
    public void MissingWarning_IsWorded()
    {
        using var empty = new TempFolder();
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [empty.Path] });
        byte[] level = SyntheticLevel.Stock();
        var references = RflReader.ReadSummary(level, "synthetic.rfl").References.Select(r => r.Name).ToList();
        Assert.True(references.Count > 2);

        string Warning(VppPackage package) => Assert.Single(
            VppFacts.Describe("synthetic.rfl", () => new MemoryStream(level), level.Length, new VppFactsContext(package, resolver)).Warnings,
            w => w.Contains("referenced by this level", StringComparison.Ordinal));

        // every reference but one in the packfile: singular
        var allButOne = TestData.Small([.. references.Skip(1).Select(n => (n, 8))]);
        Assert.StartsWith($"1 file referenced by this level is not in this packfile or the game data: {references[0]}.", Warning(allButOne));
        // none in the packfile: plural
        string plural = Warning(VppPackage.Empty);
        Assert.StartsWith($"{references.Count} files referenced by this level are not in this packfile or the game data: ", plural);
    }

    /// <summary>
    /// The stock single-player levels against the game folder: every reference the game would find counts as
    /// present (L1S1 used to report 183 <c>.mvf</c> names as missing).
    /// </summary>
    [Fact]
    public void StockSinglePlayerLevels_ReferencesAreFound()
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null || !Directory.Exists(dir)) return;
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = dir });
        int levels = 0, references = 0, missingTotal = 0;
        foreach (string pack in new[] { "levels1.vpp", "levels2.vpp", "levels3.vpp" })
        {
            string? path = TestData.Stock(pack);
            if (path is null) continue;
            var package = VppPackage.Open(path);
            var context = new VppFactsContext(package, resolver);
            var presence = VppFacts.LevelPresence(context)!;
            foreach (var item in package.Items.Where(i => i.Extension == ".rfl"))
            {
                RflSummary summary;
                using (var stream = item.Source.Open()) summary = RflReader.ReadSummary(stream, item.Name);
                var missing = summary.References.Where(r => presence(r.Name) == AssetPresence.Missing).ToList();
                var p = summary.Preloads;
                var preloads = p.Bitmaps.Concat(p.CharacterMeshes).Concat(p.Animations).Concat(p.StaticMeshes).Concat(p.Effects)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var missingPreloads = preloads.Where(n => presence(n) == AssetPresence.Missing).ToList();
                levels++;
                references += summary.References.Count + preloads.Count;
                missingTotal += missing.Count + missingPreloads.Count;
                output.WriteLine($"{pack}|{item.Name}: {summary.References.Count} references, {missing.Count} missing"
                    + (missing.Count > 0 ? " (" + string.Join(", ", missing.Select(m => $"{m.Name}: {m.Kind}, {string.Join(", ", m.Places)}")) + ")" : "")
                    + $"; {preloads.Count} preloads, {missingPreloads.Count} missing" + (missingPreloads.Count > 0 ? " (" + string.Join(", ", missingPreloads) + ")" : ""));
                // What stays missing in the stock levels are sounds the stock data really lacks (stale level-editor
                // names such as "DoorLoop_.70.wav" or "use_Sink 01.wav"); every texture, mesh, clip and effect is found.
                Assert.All(missing, m => Assert.Equal(RflReferenceKind.Sound, m.Kind));
                Assert.Empty(missingPreloads);
                var sheet = VppFacts.Describe(item, context);
                Assert.DoesNotContain(sheet.Warnings, w => w.Contains("file(s)", StringComparison.Ordinal) || w.Contains("neither", StringComparison.Ordinal));
                var warning = sheet.Warnings.SingleOrDefault(w => w.Contains("referenced by this level", StringComparison.Ordinal));
                Assert.Equal(missing.Count + missingPreloads.Count > 0, warning is not null);
                if (warning is not null)
                    Assert.Matches(@"^(1 file referenced by this level is|([2-9]|\d\d+) files referenced by this level are) not in this packfile or the game data: ", warning);
            }
        }
        output.WriteLine($"{levels} levels, {references} references, {missingTotal} missing");
    }
}
