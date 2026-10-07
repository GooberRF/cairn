using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Tests;

/// <summary>The context rules fed from the real tables and a library over the stock corpus.</summary>
public class LintContextTests
{
    [Fact]
    public async Task TablesLibraryAndPreviewMeshFeedTheContextRules()
    {
        if (TestPaths.Corpus is null || TestPaths.Tables is null) return;
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [TestPaths.Corpus, TestPaths.Tables] });
        var library = await new AssetLibrary(resolver).BuildAsync();
        var usage = ClipUsageIndex.Load(resolver);
        var miner = V3dReader.ReadFile(Path.Combine(TestPaths.Corpus, "miner.v3c"));
        string stockPath = Path.Combine(TestPaths.Corpus, "park_jeep_driver.rfa");
        var stock = RfaReader.ReadFile(stockPath);

        // The stock clip opened in place: used by miner1 on a 25-bone mesh, no collision with itself.
        var own = ClipLintContextBuilder.Build("park_jeep_driver.rfa", stockPath, miner, "miner.v3c", library.Clips.Length > 0 ? library : null, usage);
        Assert.Contains(own.TableUses, u => u.ClassName == "miner1" && u.SlotName == "jeep_drive" && u.MeshBoneCount == 25);
        Assert.Empty(own.CollidingClips);
        Assert.NotNull(own.ReferenceClip);
        Assert.DoesNotContain(ClipLinter.Analyze(stock, own), d => d.Severity == DiagnosticSeverity.Error);

        // A copy saved elsewhere under the stock name sees the stock one; while it is byte-identical it is the
        // same clip (no RFA024), once it differs it collides; with 27 bones it no longer fits the table's mesh.
        var copy = ClipLintContextBuilder.Build("park_jeep_driver.rfa", @"C:\mods\park_jeep_driver.rfa", miner, "miner.v3c", library, usage);
        Assert.NotEmpty(copy.CollidingClips);
        Assert.DoesNotContain(ClipLinter.Analyze(stock, copy), d => d.Code == ClipRules.NameCollision);
        var wide = stock with { Bones = stock.Bones.AddRange(stock.Bones.Take(2)) };
        var results = ClipLinter.Analyze(wide, copy);
        Assert.Contains(results, d => d.Code == ClipRules.NameCollision);
        Assert.Contains(results, d => d.Code == ClipRules.TableMeshBoneCount);
        Assert.Contains(results, d => d.Code == ClipRules.BoneCountMismatch);

        // A brand-new name: no table uses, no collision, still a reference clip for proportions.
        var fresh = ClipLintContextBuilder.Build("my_new_clip.rfa", null, miner, "miner.v3c", library, usage);
        Assert.Empty(fresh.TableUses);
        Assert.Empty(fresh.CollidingClips);
        Assert.NotNull(fresh.ReferenceClip);
    }

    [Fact]
    public async Task NameCollisionIgnoresTheDocumentItselfAndIdenticalCopies()
    {
        using var temp = new TempFolder();
        var clip = RfaFormatTests.SampleClip(8, morph: false);
        byte[] bytes = RfaWriter.Write(clip);
        var different = clip with { EndTime = clip.EndTime + 160 };
        string game = temp.SubDirectory("search");
        File.WriteAllBytes(Path.Combine(game, "pack.vpp"), VppWriter.Build([("walk.rfa", bytes), ("other.rfa", bytes)]));
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [game] });
        var library = await new AssetLibrary(resolver).BuildAsync();
        var stock = library.FindClip("walk.rfa")!;
        Assert.NotNull(stock.Location.ArchivePath);

        // Opened from the archive entry itself: not a collision, however the archive path is spelt.
        string own = ClipLintContextBuilder.ArchivedDocumentPath(stock.Location);
        Assert.Empty(ClipLintContextBuilder.Build("walk.rfa", own, null, null, library, null).CollidingClips);
        string respelt = stock.Location.ArchivePath!.Replace('\\', '/') + "|WALK.RFA";
        Assert.Empty(ClipLintContextBuilder.Build("walk.rfa", respelt, null, null, library, null).CollidingClips);

        // A loose copy elsewhere with the same bytes: the copy is listed (with its bytes) but RFA024 stays quiet.
        string loose = temp.Write(@"elsewhere\walk.rfa", bytes);
        var context = ClipLintContextBuilder.Build("walk.rfa", loose, null, null, library, null);
        Assert.Single(context.CollidingClips);
        Assert.Equal(bytes, context.CollidingClipBytes[0]);
        Assert.DoesNotContain(ClipLinter.Analyze(clip, context), d => d.Code == ClipRules.NameCollision);

        // Edited (or a genuinely different clip of that name): the warning is back.
        var edited = ClipLinter.Analyze(different, context);
        Assert.Contains(edited, d => d.Code == ClipRules.NameCollision && d.Severity == DiagnosticSeverity.Warning);

        // Without the bytes (not read) the old behaviour holds.
        Assert.Contains(ClipLinter.Analyze(clip, context with { CollidingClipBytes = [] }), d => d.Code == ClipRules.NameCollision);
    }

    [Fact]
    public async Task TheLooseStockWalkCopyDoesNotCollideWithTheStockClip()
    {
        // research/rf_decomp/meshes_anims/ult2_walk.rfa is a loose copy of the clip in motions.vpp; a library
        // over the corpus and a second folder holding the same bytes sees two copies.
        string? walk = TestPaths.CorpusFile("ult2_walk.rfa");
        if (walk is null) return;
        using var temp = new TempFolder();
        byte[] bytes = File.ReadAllBytes(walk);
        string other = temp.SubDirectory("mods");
        File.WriteAllBytes(Path.Combine(other, "pack.vpp"), VppWriter.Build([("ult2_walk.rfa", bytes)]));
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [other] });
        var library = await new AssetLibrary(resolver).BuildAsync();
        var clip = RfaReader.ReadFile(walk);
        var context = ClipLintContextBuilder.Build("ult2_walk.rfa", walk, null, null, library, null);
        Assert.Single(context.CollidingClips);
        Assert.DoesNotContain(ClipLinter.Analyze(clip, context), d => d.Code == ClipRules.NameCollision);
    }

    [Fact]
    public void StockMorphClipsFitTheMeshesTheyWereMadeFor()
    {
        foreach (var (clipName, meshName) in new[] { ("NURS_talk.rfa", "nurse1.v3c"), ("masa_talk.rfa", "masako.v3c"), ("eos_talk.rfa", "eos.v3c") })
        {
            string? clipPath = TestPaths.CorpusFile(clipName), meshPath = TestPaths.CorpusFile(meshName);
            if (clipPath is null || meshPath is null) continue;
            var mesh = V3dReader.ReadFile(meshPath);
            var context = ClipLintContextBuilder.Build(clipName, clipPath, mesh, meshName, null, null);
            Assert.DoesNotContain(ClipLinter.Analyze(RfaReader.ReadFile(clipPath), context), d => d.Severity == DiagnosticSeverity.Error);
        }
    }
}
