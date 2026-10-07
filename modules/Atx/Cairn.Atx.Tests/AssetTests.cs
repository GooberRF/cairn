using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

public class AssetTests
{
    private static byte[] Tga(int size, byte shade) =>
        TinyImageWriter.Tga24(size, size, Enumerable.Repeat(shade, size * size * 3).ToArray());

    // ── VPP ──────────────────────────────────────────────────────────────────

    [Fact]
    public void VppRoundTripsNamesSizesAndContent()
    {
        using var temp = new TempFolder();
        var one = Tga(4, 10);
        var two = Tga(8, 20);
        string path = temp.Write("pack.vpp", VppWriter.Build(
        [
            ("first.tga", one),
            ("SECOND.TGA", two),
            ("empty.bin", []),
        ]));

        var archive = VppArchive.Open(path);
        Assert.Equal(1u, archive.Version);
        Assert.Equal(["first.tga", "SECOND.TGA", "empty.bin"], archive.Entries.Select(e => e.Name));
        Assert.Equal(one.Length, archive.Entries[0].Size);

        // Lookup ignores case, like RF's file system.
        Assert.True(archive.TryGetEntry("Second.Tga", out var entry));
        Assert.Equal(two.Length, entry.Size);

        using var stream = archive.OpenEntry(entry);
        var read = new byte[entry.Size];
        stream.ReadExactly(read);
        Assert.Equal(two, read);

        // And the entry stream probes exactly like a loose file.
        using var probeStream = archive.OpenEntry("first.tga")!;
        var info = ImageProbe.Probe(probeStream, "first.tga");
        Assert.Equal(4, info.Width);
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
        string path = temp.Write("not.vpp", new byte[4096]);
        Assert.Throws<VppFormatException>(() => VppArchive.Open(path));

        string truncated = temp.Write("short.vpp", VppWriter.Build([("a.bin", [1])])[..100]);
        Assert.Throws<VppFormatException>(() => VppArchive.Open(truncated));
    }

    // ── Resolver ─────────────────────────────────────────────────────────────

    [Fact]
    public void AtxFolderBeatsSearchFoldersAndArchives()
    {
        using var temp = new TempFolder();
        string atxFolder = temp.SubDirectory("atx");
        string searchFolder = temp.SubDirectory("search");
        File.WriteAllBytes(Path.Combine(atxFolder, "frame.tga"), Tga(4, 10));
        File.WriteAllBytes(Path.Combine(searchFolder, "frame.tga"), Tga(8, 20));
        File.WriteAllBytes(Path.Combine(searchFolder, "pack.vpp"),
            VppWriter.Build([("frame.tga", Tga(16, 30))]));

        var resolver = new AssetResolver(new AssetResolverOptions
        {
            DocumentFolder = atxFolder,
            SearchFolders = [searchFolder],
        });

        var hit = resolver.Resolve("frame.tga");
        Assert.NotNull(hit);
        Assert.Equal(AssetSourceKind.DocumentFolder, hit!.Kind);
        Assert.Equal(4, resolver.ProbeImage(hit).Width);
    }

    [Fact]
    public void LooseSearchFolderFilesBeatArchivesInTheSameFolder()
    {
        using var temp = new TempFolder();
        string searchFolder = temp.SubDirectory("search");
        File.WriteAllBytes(Path.Combine(searchFolder, "frame.tga"), Tga(8, 20));
        File.WriteAllBytes(Path.Combine(searchFolder, "pack.vpp"),
            VppWriter.Build([("frame.tga", Tga(16, 30))]));

        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [searchFolder] });
        Assert.Equal(AssetSourceKind.SearchFolder, resolver.Resolve("frame.tga")!.Kind);
    }

    [Fact]
    public void ArchivesAreSearchedWhenNoLooseFileMatches()
    {
        using var temp = new TempFolder();
        string searchFolder = temp.SubDirectory("search");
        File.WriteAllBytes(Path.Combine(searchFolder, "pack.vpp"),
            VppWriter.Build([("frame.tga", Tga(16, 30))]));

        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [searchFolder] });
        var hit = resolver.Resolve("frame.tga");
        Assert.NotNull(hit);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, hit!.Kind);
        Assert.Equal("pack.vpp", hit.DisplayLocation);
        Assert.Equal(16, resolver.ProbeImage(hit).Width);
    }

    [Fact]
    public void SupersedingSiblingsWinInTheEnginesOrder()
    {
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("atx");
        File.WriteAllBytes(Path.Combine(folder, "frame.tga"), Tga(4, 10));
        File.WriteAllBytes(Path.Combine(folder, "frame.png"),
            TinyImageWriter.Png(8, 8, 2, RowsOf(8, 8, 3)));
        File.WriteAllBytes(Path.Combine(folder, "frame.dds"),
            TinyImageWriter.DdsDxt1(16, 16, (_, _) => (0xF800, 0x001F, 0u)));

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = folder });
        var hit = resolver.Resolve("frame.tga");
        Assert.NotNull(hit);
        Assert.Equal("frame.dds", hit!.ResolvedName);
        Assert.True(hit.IsSupersede);

        File.Delete(Path.Combine(folder, "frame.dds"));
        resolver.Invalidate();
        Assert.Equal("frame.png", resolver.Resolve("frame.tga")!.ResolvedName);

        File.Delete(Path.Combine(folder, "frame.png"));
        resolver.Invalidate();
        var literal = resolver.Resolve("frame.tga")!;
        Assert.Equal("frame.tga", literal.ResolvedName);
        Assert.False(literal.IsSupersede);
    }

    [Fact]
    public void TheProbeOrderIsByExtensionEvenWhenTheRequestedNameIsInTheChain()
    {
        // read_stb_sibling walks .png, .jpg, .jpeg in that order for whatever was requested, so
        // asking for frame.png while a frame.jpg also exists must still resolve to frame.png.
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("atx");
        File.WriteAllBytes(Path.Combine(folder, "frame.png"),
            TinyImageWriter.Png(8, 8, 2, RowsOf(8, 8, 3)));
        File.WriteAllBytes(Path.Combine(folder, "frame.jpg"), TestImages.Jpeg(8, 8));

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = folder });
        var asked = resolver.Resolve("frame.png")!;
        Assert.Equal("frame.png", asked.ResolvedName);
        Assert.False(asked.IsSupersede);

        // Asking for the .jpg still gets the .png, because .png comes first in the chain.
        Assert.Equal("frame.png", resolver.Resolve("frame.jpg")!.ResolvedName);
    }

    [Fact]
    public void GameDirectoryIsSearchedLastIncludingUserMaps()
    {
        using var temp = new TempFolder();
        string game = temp.SubDirectory("game");
        Directory.CreateDirectory(Path.Combine(game, "user_maps", "textures"));
        File.WriteAllBytes(Path.Combine(game, "tables.vpp"), VppWriter.Build([("stock.tga", Tga(4, 1))]));
        File.WriteAllBytes(Path.Combine(game, "user_maps", "textures", "custom.tga"), Tga(8, 2));

        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = game });
        Assert.Equal(AssetSourceKind.GameArchive, resolver.Resolve("stock.tga")!.Kind);
        Assert.Equal(AssetSourceKind.GameFolder, resolver.Resolve("custom.tga")!.Kind);
        Assert.Null(resolver.Resolve("nothing.tga"));
    }

    [Fact]
    public void GameSubFoldersListsEveryUserMapsAssetDirectory()
    {
        // projects is a game asset directory exactly like the other three; the engine loads its
        // packfiles first (load_additional_packfiles_new), ahead of single and multi.
        Assert.Equal(new[] { "textures", "projects", "single", "multi" }, AssetResolver.GameSubFolders);
    }

    [Fact]
    public void UserMapsProjectsIsSearchedLikeTheOtherGameFolders()
    {
        using var temp = new TempFolder();
        string game = temp.SubDirectory("game");
        string projects = Path.Combine(game, "user_maps", "projects");
        Directory.CreateDirectory(projects);
        File.WriteAllBytes(Path.Combine(projects, "loose.tga"), Tga(8, 3));
        string vpp = Path.Combine(projects, "project.vpp");
        File.WriteAllBytes(vpp, VppWriter.Build([("packed.tga", Tga(4, 4))]));

        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = game });

        // A loose image and one inside a .vpp both resolve, under the right source kinds.
        Assert.Equal(AssetSourceKind.GameFolder, resolver.Resolve("loose.tga")!.Kind);
        var packed = resolver.Resolve("packed.tga")!;
        Assert.Equal(AssetSourceKind.GameFolderArchive, packed.Kind);
        Assert.Equal(vpp, packed.ArchivePath);

        // It appears in both descriptions, and counts as searched.
        Assert.Contains(projects, resolver.DescribeSearchOrder());
        Assert.Contains(Path.Combine(projects, "*.vpp"), resolver.DescribeSearchOrder());

        var group = resolver.DescribeArchiveSources().Single(g => g.Folder == projects);
        Assert.Equal(Path.Combine("user_maps", "projects"), group.Label);
        Assert.Equal(AssetSourceKind.GameFolderArchive, group.Kind);
        Assert.True(group.IsSearched);
        Assert.Equal(new[] { vpp }, group.ArchivePaths);
        Assert.True(resolver.IsSearchedArchive(vpp));
    }

    [Fact]
    public void MissingFilesResolveToNull()
    {
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = @"Z:\does\not\exist" });
        Assert.Null(resolver.Resolve("frame.tga"));
        Assert.Null(resolver.Resolve(""));
    }

    [Fact]
    public async Task ResolveAsyncCanBeCancelled()
    {
        using var temp = new TempFolder();
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync("frame.tga", cts.Token));
    }

    [Fact]
    public void InvalidateRaisesIndexChanged()
    {
        var resolver = new AssetResolver(new AssetResolverOptions());
        int raised = 0;
        resolver.IndexChanged += (_, _) => raised++;
        resolver.Invalidate();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ProbesAreCachedUntilTheFileChanges()
    {
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("atx");
        string file = Path.Combine(folder, "frame.tga");
        File.WriteAllBytes(file, Tga(4, 10));

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = folder });
        var location = resolver.Resolve("frame.tga")!;
        Assert.Equal(4, resolver.ProbeImage(location).Width);
        Assert.Equal(1, resolver.Cache.Count);
        Assert.Equal(4, resolver.ProbeImage(location).Width);
        Assert.Equal(1, resolver.Cache.Count);

        File.WriteAllBytes(file, Tga(8, 10));
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(8, resolver.ProbeImage(location).Width);
    }

    [Fact]
    public void UnreadableFilesReportAnImageDecodeException()
    {
        using var temp = new TempFolder();
        string folder = temp.SubDirectory("atx");
        File.WriteAllBytes(Path.Combine(folder, "frame.tga"), [1, 2, 3]);

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = folder });
        var location = resolver.Resolve("frame.tga")!;
        Assert.Throws<ImageDecodeException>(() => resolver.ProbeImage(location));
        // The failure is cached too, so repeated linting does not re-read a broken file.
        Assert.Throws<ImageDecodeException>(() => resolver.ProbeImage(location));
    }

    private static byte[][] RowsOf(int width, int height, int bytesPerPixel)
    {
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new byte[width * bytesPerPixel];
            Array.Fill(rows[y], (byte)(y * 8));
        }
        return rows;
    }
}
