using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Vpp;
using Cairn.Formats.Imaging;

namespace Cairn.Rfa.Tests;

public class AssetTests
{
    private static byte[] Tga(int size, byte shade) => TestImages.Tga24(size, size, shade, shade, shade);

    // ── VPP ──────────────────────────────────────────────────────────────────

    [Fact]
    public void VppRoundTripsNamesSizesAndContent()
    {
        using var temp = new TempFolder();
        var one = Tga(4, 10);
        var two = RfaWriter.Write(RfaFormatTests.SampleClip());
        string path = temp.Write("pack.vpp", VppWriter.Build(
        [
            ("first.tga", one),
            ("STAND.RFA", two),
            ("empty.bin", []),
        ]));

        var archive = VppArchive.Open(path);
        Assert.Equal(1u, archive.Version);
        Assert.Equal(["first.tga", "STAND.RFA", "empty.bin"], archive.Entries.Select(e => e.Name));
        Assert.Equal(one.Length, archive.Entries[0].Size);

        // Lookup ignores case, like RF's file system.
        Assert.True(archive.TryGetEntry("stand.rfa", out var entry));
        Assert.Equal(two, archive.ReadEntry(entry));

        // An entry stream reads exactly like a loose file.
        using var stream = archive.OpenEntry(entry);
        Assert.Equal(3, RfaReader.Read(stream, "stand.rfa").BoneCount);
        Assert.Empty(archive.ReadEntry(archive.Entries[2]));
    }

    [Fact]
    public void VppEntriesAreAlignedToTwoKilobyteBoundaries()
    {
        using var temp = new TempFolder();
        string path = temp.Write("pack.vpp", VppWriter.Build([("a.bin", [1]), ("b.bin", [2])]));
        var archive = VppArchive.Open(path);
        Assert.Equal(0, archive.Entries[0].Offset % VppArchive.BlockSize);
        Assert.Equal(VppArchive.BlockSize, archive.Entries[1].Offset - archive.Entries[0].Offset);
    }

    [Fact]
    public void NonVppFilesAreRejected()
    {
        using var temp = new TempFolder();
        Assert.Throws<VppFormatException>(() => VppArchive.Open(temp.Write("not.vpp", new byte[4096])));
        Assert.Throws<VppFormatException>(() => VppArchive.Open(temp.Write("short.vpp", VppWriter.Build([("a.bin", [1])])[..100])));
        // A stub claiming a million files is refused before its directory is allocated.
        var stub = VppWriter.Build([("a.bin", [1])]);
        BitConverter.GetBytes(1_000_000).CopyTo(stub, 8);
        Assert.Throws<VppFormatException>(() => VppArchive.Open(temp.Write("stub.vpp", stub)));
    }

    // ── Resolver ─────────────────────────────────────────────────────────────

    [Fact]
    public void SearchOrderIsDocumentThenSearchFoldersThenArchivesThenGame()
    {
        using var temp = new TempFolder();
        string doc = temp.SubDirectory("doc");
        string search = temp.SubDirectory("search");
        string game = temp.SubDirectory("game");
        byte[] clip = RfaWriter.Write(RfaFormatTests.SampleClip());
        File.WriteAllBytes(Path.Combine(search, "pack.vpp"), VppWriter.Build([("walk.rfa", clip), ("run.rfa", clip)]));
        File.WriteAllBytes(Path.Combine(search, "walk.rfa"), clip);
        File.WriteAllBytes(Path.Combine(doc, "stand.rfa"), clip);
        File.WriteAllBytes(Path.Combine(game, "meshes.vpp"), VppWriter.Build([("stand.rfa", clip), ("miner.v3c", [1, 2]), ("entity.tbl", [3])]));

        var resolver = new AssetResolver(new AssetResolverOptions
        {
            DocumentFolder = doc,
            SearchFolders = [search],
            GameDirectory = game,
        });
        Assert.Equal(AssetSourceKind.DocumentFolder, resolver.Resolve("stand.rfa")!.Kind);
        Assert.Equal(AssetSourceKind.SearchFolder, resolver.Resolve("walk.rfa")!.Kind);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, resolver.Resolve("RUN.rfa")!.Kind);
        var mesh = resolver.Resolve(@"some\folder\miner.v3c");
        Assert.Equal(AssetSourceKind.GameArchive, mesh!.Kind);
        Assert.True(mesh.IsArchived);
        Assert.Equal([1, 2], mesh.ReadAllBytes());
        Assert.NotNull(resolver.Resolve("entity.tbl"));
        Assert.Null(resolver.Resolve("missing.rfa"));
        Assert.Equal("miner.v3c", resolver.ResolveFirst(["miner.v3m", "miner.v3c"])!.ResolvedName);

        var clips = resolver.Enumerate([".rfa"]);
        Assert.Equal(["stand.rfa", "walk.rfa", "run.rfa"], clips.Select(c => c.ResolvedName.ToLowerInvariant()));
        Assert.Equal(AssetSourceKind.DocumentFolder, clips[0].Kind);
    }

    [Fact]
    public void OnlyTexturesFollowTheSupersedeChain()
    {
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("search");
        File.WriteAllBytes(Path.Combine(folder, "skin.tga"), Tga(4, 1));
        File.WriteAllBytes(Path.Combine(folder, "skin.dds"), TestImages.Dds32(8, 8, 1, 1, 1, 1));
        File.WriteAllBytes(Path.Combine(folder, "anim.rfa"), [1]);
        File.WriteAllBytes(Path.Combine(folder, "anim.dds"), [1]);

        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        var tex = resolver.Resolve("skin.tga")!;
        Assert.Equal("skin.dds", tex.ResolvedName);
        Assert.True(tex.IsSupersede);
        Assert.Equal(8, resolver.ProbeImage("skin.tga")!.Width);
        // A clip never resolves to an image sibling.
        Assert.Equal("anim.rfa", resolver.Resolve("anim.rfa")!.ResolvedName);
    }

    [Fact]
    public void WithDocumentFolderSearchesTheNewFolderFirstAndSharesTheIndexes()
    {
        using var temp = new TempFolder();
        string docA = temp.SubDirectory("a");
        string docB = temp.SubDirectory("b");
        string game = temp.SubDirectory("game");
        byte[] clip = RfaWriter.Write(RfaFormatTests.SampleClip());
        File.WriteAllBytes(Path.Combine(docB, "stand.rfa"), clip);
        File.WriteAllBytes(Path.Combine(game, "meshes.vpp"), VppWriter.Build([("stand.rfa", clip), ("walk.rfa", clip)]));

        var shared = new AssetResolver(new AssetResolverOptions { DocumentFolder = docA, GameDirectory = game });
        Assert.Equal(AssetSourceKind.GameArchive, shared.Resolve("stand.rfa")!.Kind);

        var forB = shared.WithDocumentFolder(docB);
        Assert.Equal(docB, forB.Options.DocumentFolder);
        Assert.Equal(game, forB.Options.GameDirectory);
        Assert.Equal(AssetSourceKind.DocumentFolder, forB.Resolve("stand.rfa")!.Kind);
        Assert.Equal(AssetSourceKind.GameArchive, forB.Resolve("walk.rfa")!.Kind);
        // The probe cache is one object, so a texture probed through one resolver is cached for all.
        Assert.Same(shared.Cache, forB.Cache);
        // The original resolver is unaffected by the derived one's document folder.
        Assert.Equal(AssetSourceKind.GameArchive, shared.Resolve("stand.rfa")!.Kind);
        Assert.Equal(AssetSourceKind.GameArchive, shared.WithDocumentFolder(null).Resolve("stand.rfa")!.Kind);
    }

    [Fact]
    public void ADocumentFolderArchiveIsListedButNotSearched()
    {
        using var temp = new TempFolder();
        string doc = temp.SubDirectory("doc");
        File.WriteAllBytes(Path.Combine(doc, "local.vpp"), VppWriter.Build([("inner.rfa", [1])]));
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = doc });
        Assert.Null(resolver.Resolve("inner.rfa"));
        var group = Assert.Single(resolver.DescribeArchiveSources());
        Assert.False(group.IsSearched);
    }

    [Fact]
    public void CacheRemembersResultsAndFailuresUntilTheFileChanges()
    {
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("f");
        string path = Path.Combine(folder, "bad.tga");
        File.WriteAllBytes(path, [1, 2, 3]);
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        var location = resolver.Resolve("bad.tga")!;
        Assert.Throws<ImageDecodeException>(() => resolver.Cache.ProbeImage(location));
        Assert.Throws<ImageDecodeException>(() => resolver.Cache.ProbeImage(location));
        Assert.Equal(1, resolver.Cache.Count);

        int calls = 0;
        int Probe(Stream s, string n)
        {
            calls++;
            return (int)s.Length;
        }
        Assert.Equal(3, resolver.Cache.GetOrProbe(location, "length", Probe));
        Assert.Equal(3, resolver.Cache.GetOrProbe(location, "length", Probe));
        Assert.Equal(1, calls);

        File.WriteAllBytes(path, Tga(2, 9));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(2, resolver.Cache.ProbeImage(location).Width);
    }

    // ── Game directory ───────────────────────────────────────────────────────

    private sealed class FakeRegistry(Dictionary<string, string> values) : IRegistryReader
    {
        public string? Read(Microsoft.Win32.RegistryHive hive, Microsoft.Win32.RegistryView view, string key, string value) =>
            values.TryGetValue($"{hive}|{key}|{value}", out var v) ? v : null;
    }

    [Fact]
    public void GameDirectoryDetectionPrefersAlpineFactionsSetting()
    {
        using var temp = new TempFolder();
        string alpine = temp.SubDirectory("alpine");
        string retail = temp.SubDirectory("retail");
        File.WriteAllBytes(Path.Combine(alpine, "RF.exe"), [0]);
        File.WriteAllBytes(Path.Combine(retail, "tables.vpp"), [0]);
        var registry = new FakeRegistry(new()
        {
            [@"CurrentUser|SOFTWARE\Volition\Red Faction\Alpine Faction|Executable Path"] = "\"" + Path.Combine(alpine, "RF.exe") + "\"",
            [@"LocalMachine|SOFTWARE\Volition\Red Faction|InstallPath"] = retail,
        });
        var found = GameDirectoryLocator.DetectDetailed(registry, []);
        Assert.Equal(Path.GetFullPath(alpine), found!.Directory);
        Assert.Equal(GameDirectorySource.AlpineFaction, found.Source);
        Assert.Null(GameDirectoryLocator.DetectDetailed(new FakeRegistry([]), [temp.Path]));
        Assert.Equal(GameDirectorySource.CommonPath, GameDirectoryLocator.DetectDetailed(new FakeRegistry([]), [retail])!.Source);
    }
}
