using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Vpp;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Phase 5 cold-start fixes: archive directories are read once however many lookups want them at the
/// same time, the library cache carries whole directories and seeds the resolver with them, and the
/// enumeration reports progress per archive.
/// </summary>
public class ArchiveIndexSharingTests
{
    private static byte[] ClipBytes() => RfaWriter.Write(RfaFormatTests.SampleClip(8, morph: false));

    private static string MakeArchives(TempFolder temp, int count)
    {
        string folder = temp.SubDirectory("search");
        for (int i = 0; i < count; i++)
        {
            File.WriteAllBytes(Path.Combine(folder, $"pack{i:D2}.vpp"), VppWriter.Build(
            [
                ($"clip{i}.rfa", ClipBytes()),
                ($"tex{i}.tga", new byte[100 + i]),
                ("readme.txt", [1, 2, 3]),
            ]));
        }
        return folder;
    }

    [Fact]
    public void FromDirectoryRebuildsTheOffsetsReadComputes()
    {
        using var temp = new TempFolder();
        string path = temp.Write("a.vpp", VppWriter.Build([("one.tga", new byte[10]), ("two.rfa", new byte[5000]), ("three.txt", [])]));
        var read = VppArchive.Open(path);
        var rebuilt = VppArchive.FromDirectory(path, read.Version, [.. read.Entries.Select(e => e.Name)], [.. read.Entries.Select(e => e.Size)]);
        Assert.Equal(read.Entries, rebuilt.Entries);
        Assert.True(rebuilt.TryGetEntry("TWO.RFA", out var two));
        Assert.Equal(new byte[5000], rebuilt.ReadEntry(two));
        Assert.Throws<VppFormatException>(() => VppArchive.FromDirectory(path, 1, ["x"], []));
        Assert.Throws<VppFormatException>(() => VppArchive.FromDirectory(path, 1, ["x"], [-1]));
    }

    [Fact]
    public async Task ConcurrentLookupsReadEachArchiveDirectoryOnce()
    {
        using var temp = new TempFolder();
        string folder = MakeArchives(temp, 24);
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        // The library-style enumeration and a crowd of document lookups (each a miss that walks every
        // archive, like a texture's .dds probe) all at once, through resolvers sharing the indexes.
        var tasks = new List<Task>
        {
            Task.Run(() => resolver.EnumerateAll(AssetLibrary.Extensions)),
        };
        for (int i = 0; i < 12; i++)
        {
            var copy = resolver.WithDocumentFolder(null);
            tasks.Add(Task.Run(() => copy.Resolve($"missing{i}.tga")));
        }
        await Task.WhenAll(tasks);
        var (reads, repeats) = resolver.ArchiveDirectoryReads;
        Assert.Equal(24, reads);
        Assert.Equal(0, repeats);
        Assert.NotNull(resolver.Resolve("tex23.tga"));
        Assert.Equal(24, resolver.ArchiveDirectoryReads.Reads);
    }

    [Fact]
    public async Task AWarmLibraryBuildSeedsTheResolverWithWholeDirectories()
    {
        using var temp = new TempFolder();
        string folder = MakeArchives(temp, 6);
        string cache = temp.File(@"cache\library-cache.json");

        var cold = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        var first = await new AssetLibrary(cold, cache).BuildAsync();
        Assert.Equal(6, first.Stats.ArchivesOpened);
        Assert.Equal(6, cold.ArchiveDirectoryReads.Reads);

        // A new process, as far as the indexes go: the cache stands in for every directory, including
        // the entries the library does not index (textures, text files).
        var warm = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        var second = await new AssetLibrary(warm, cache).BuildAsync();
        Assert.Equal(6, second.Stats.ArchivesFromCache);
        var texture = warm.Resolve("tex3.tga");
        Assert.NotNull(texture);
        Assert.Equal(103, texture.Entry!.Size);
        Assert.Equal(new byte[103], texture.ReadAllBytes());
        Assert.Null(warm.Resolve("missing.tga"));
        Assert.NotNull(warm.Resolve("readme.txt"));
        Assert.Equal(0, warm.ArchiveDirectoryReads.Reads);

        // A changed archive is read again, by its stamp, whatever was seeded.
        string changed = Path.Combine(folder, "pack02.vpp");
        File.WriteAllBytes(changed, VppWriter.Build([("tex2.tga", new byte[7]), ("new.tga", new byte[1])]));
        File.SetLastWriteTimeUtc(changed, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(7, warm.Resolve("tex2.tga")!.Entry!.Size);
        Assert.NotNull(warm.Resolve("new.tga"));
        Assert.Equal(1, warm.ArchiveDirectoryReads.Reads);
    }

    [Fact]
    public async Task EnumerationReportsProgressPerArchive()
    {
        using var temp = new TempFolder();
        string folder = MakeArchives(temp, 40);
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] });
        var seen = new List<(int Done, int Total)>();
        resolver.EnumerateAll(AssetLibrary.Extensions, null, (done, total) => seen.Add((done, total)));
        Assert.Equal(40, seen.Count);
        Assert.All(seen, s => Assert.Equal(40, s.Total));
        Assert.Equal(Enumerable.Range(1, 40), seen.Select(s => s.Done));

        // The library turns it into Enumerating progress with counts (throttled, always ending at the total).
        var reports = new List<LibraryProgress>();
        await new AssetLibrary(new AssetResolver(new AssetResolverOptions { SearchFolders = [folder] }))
            .BuildAsync(new SyncProgress(reports.Add));
        var enumerating = reports.Where(r => r.Phase == LibraryBuildPhase.Enumerating && r.Total > 0).ToList();
        Assert.NotEmpty(enumerating);
        Assert.Equal((40, 40), (enumerating[^1].Done, enumerating[^1].Total));
    }

    private sealed class SyncProgress(Action<LibraryProgress> report) : IProgress<LibraryProgress>
    {
        public void Report(LibraryProgress value) => report(value);
    }
}
