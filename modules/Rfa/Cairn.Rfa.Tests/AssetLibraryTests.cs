using System.Collections.Immutable;
using System.Diagnostics;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Tests;

public class AssetLibraryTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static byte[] Clip(int bones, int end = 480)
    {
        var clip = RfaFormatTests.SampleClip(8, morph: false);
        var tracks = Enumerable.Range(0, bones).Select(i => clip.Bones[i % clip.Bones.Length]);
        return RfaWriter.Write(clip with { Bones = [.. tracks], EndTime = end });
    }

    private static byte[] Character(params string[] boneNames)
    {
        var mesh = V3dFormatTests.SampleCharacter();
        var section = mesh.BoneSection!;
        var bones = boneNames.Select((name, i) => section.Bones[i] with
        {
            Name = FixedString.FromText(name, V3dBone.NameSize),
            ParentIndex = i == 0 ? -1 : i - 1,
        });
        int at = mesh.Sections.IndexOf(section);
        return V3dWriter.Write(mesh with { Sections = mesh.Sections.SetItem(at, section with { Bones = [.. bones] }) });
    }

    private static byte[] StaticMesh()
    {
        var mesh = V3dFormatTests.SampleCharacter();
        return V3dWriter.Write(mesh with
        {
            Header = mesh.Header with { Signature = V3dHeader.StaticSignature, CollisionSphereCount = 0 },
            Sections = [mesh.Submeshes.First()],
        });
    }

    /// <summary>
    /// Two search folders: the first holds loose files, the second a loose file and a VPP. Search order
    /// is first's loose files, second's loose files, then first's and second's archives.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            First = Temp.SubDirectory("first");
            Second = Temp.SubDirectory("second");
            File.WriteAllBytes(Path.Combine(First, "stand.rfa"), Clip(3));
            File.WriteAllBytes(Path.Combine(First, "walk.rfa"), Clip(3, end: 800));
            File.WriteAllBytes(Path.Combine(First, "corrupt.rfa"), [1, 2, 3, 4, 5]);
            File.WriteAllBytes(Path.Combine(First, "guard.v3c"), Character("pelvis", "spine", "head"));
            File.WriteAllBytes(Path.Combine(First, "crate.v3m"), StaticMesh());
            File.WriteAllBytes(Path.Combine(First, "notes.txt"), [1]);
            File.WriteAllBytes(Path.Combine(Second, "walk.rfa"), Clip(2));
            File.WriteAllBytes(Path.Combine(Second, "pack.vpp"), VppWriter.Build(
            [
                ("stand.rfa", Clip(2)),
                ("run.rfa", Clip(2)),
                ("guard.v3c", Character("a", "b")),
                ("twin.v3c", Character("twin-bdbn-pelvis", "twin-bdbn-spine", "twin-bdbn-head")),
                ("tiny.v3c", Character("root", "tip")),
                ("bad.v3c", [9, 9, 9, 9]),
                ("readme.txt", [1, 2]),
            ]));
            Resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [First, Second] });
        }

        public TempFolder Temp { get; } = new();

        public string First { get; }

        public string Second { get; }

        public AssetResolver Resolver { get; }

        public string CachePath => Temp.File("cache\\library-cache.json");

        public void Dispose() => Temp.Dispose();
    }

    private const string Tables = """
        $Name:                  "guard_class"
        $V3D Filename:          "guard.vcm"
        +State:                 "stand"                 "stand.mvf"
        +Action:                "fire"                  "walk.mvf"                ""
        +State:                 "crouch"                "nothere.mvf"
        """;

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolverEnumerateAllKeepsShadowedCopiesInSearchOrder()
    {
        using var f = new Fixture();
        var all = f.Resolver.EnumerateAll([".rfa"]);
        Assert.Equal(["corrupt.rfa", "stand.rfa", "walk.rfa", "walk.rfa", "stand.rfa", "run.rfa"], all.Select(l => l.ResolvedName));
        Assert.Equal(
            [AssetSourceKind.SearchFolder, AssetSourceKind.SearchFolder, AssetSourceKind.SearchFolder, AssetSourceKind.SearchFolder,
             AssetSourceKind.SearchFolderArchive, AssetSourceKind.SearchFolderArchive],
            all.Select(l => l.Kind));
        // The winners are exactly what Enumerate (and therefore Resolve) gives.
        Assert.Equal(f.Resolver.Enumerate([".rfa"]).Select(l => (l.ResolvedName, l.Kind, l.FilePath, l.ArchivePath)),
            all.DistinctBy(l => l.ResolvedName, StringComparer.OrdinalIgnoreCase).Select(l => (l.ResolvedName, l.Kind, l.FilePath, l.ArchivePath)));

        // Cached archive entries stand in for the archive's directory.
        var fake = new List<Cairn.Formats.Vpp.VppEntry> { new("ghost.rfa", 2048, 10), new("other.bin", 4096, 1) };
        var substituted = f.Resolver.EnumerateAll([".rfa"], vpp => fake);
        Assert.Equal("ghost.rfa", substituted[^1].ResolvedName);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, substituted[^1].Kind);
    }

    [Fact]
    public async Task TheLibraryIndexesLooseFilesAndArchivesWithShadowing()
    {
        using var f = new Fixture();
        var library = new AssetLibrary(f.Resolver);
        Assert.Same(LibrarySnapshot.Empty, library.Snapshot);
        int changed = 0;
        library.SnapshotChanged += (_, _) => Interlocked.Increment(ref changed);

        var snap = await library.BuildAsync();
        Assert.Same(snap, library.Snapshot);
        Assert.Equal(1, changed);

        Assert.Equal(["corrupt.rfa", "run.rfa", "stand.rfa", "walk.rfa"], snap.Clips.Select(c => c.Name));
        Assert.Equal(6, snap.AllClips.Length);
        Assert.Equal(["bad.v3c", "crate.v3m", "guard.v3c", "tiny.v3c", "twin.v3c"], snap.Meshes.Select(m => m.Name));
        Assert.Equal(6, snap.AllMeshes.Length);

        // The loose copy in the first folder wins; the archived one is kept, marked shadowed.
        var stand = snap.CopiesOfClip("STAND.mvf");
        Assert.Equal(2, stand.Count);
        Assert.Same(stand[0], snap.FindClip(@"any\folder\stand.rfa"));
        Assert.False(stand[0].IsShadowed);
        Assert.Equal(AssetSourceKind.SearchFolder, stand[0].Location.Kind);
        Assert.Equal(Path.Combine(f.First, "stand.rfa"), stand[0].Location.FilePath);
        Assert.True(stand[1].IsShadowed);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, stand[1].Location.Kind);
        Assert.Equal(stand[0].Location, stand[1].ShadowedBy);
        Assert.Equal(3, stand[0].BoneCount);
        Assert.Equal(2, stand[1].BoneCount);

        // A loose file in the second folder still beats the first folder's archive, but not its loose files.
        var walk = snap.CopiesOfClip("walk");
        Assert.Equal([f.First, f.Second], walk.Select(c => Path.GetDirectoryName(c.Location.FilePath)));
        Assert.Equal(800, walk[0].Facts!.EndTime);
        var guard = snap.CopiesOfMesh("guard.vcm");
        Assert.Equal(3, guard[0].BoneCount);
        Assert.Equal("pack.vpp", guard[1].Location.DisplayLocation);
        Assert.True(guard[1].IsShadowed);

        // Probed facts.
        var run = snap.FindClip("run")!;
        Assert.Equal(8, run.Facts!.Version);
        Assert.Equal(160, run.Facts.StartTime);
        Assert.Equal(2, run.BoneCount);
        Assert.Equal(V3dKind.StaticMesh, snap.FindMesh("crate")!.Kind);
        Assert.Equal(V3dKind.Character, snap.FindMesh("guard")!.Kind);
        Assert.Equal(["pelvis", "spine", "head"], snap.FindMesh("guard.v3c")!.Facts!.BoneNames.AsEnumerable());

        // Unreadable files are entries with a plain-language error, not exceptions.
        var corrupt = snap.FindClip("corrupt")!;
        Assert.Null(corrupt.Facts);
        Assert.False(corrupt.IsReadable);
        Assert.Contains("corrupt.rfa", corrupt.Error);
        var bad = snap.FindMesh("bad.v3c")!;
        Assert.Null(bad.Facts);
        Assert.Contains("bad.v3c", bad.Error);
        Assert.Equal(2, snap.Stats.Errors);
        Assert.Equal(12, snap.Stats.Locations);
        Assert.Equal(12, snap.Stats.Probed);

        Assert.Null(snap.FindClip("missing"));
        Assert.Null(snap.FindMesh("missing.v3c"));
        Assert.Empty(snap.CopiesOfClip(""));
    }

    [Fact]
    public async Task FamiliesFiltersAndCompatibility()
    {
        using var f = new Fixture();
        var snap = await new AssetLibrary(f.Resolver).BuildAsync();

        // guard and twin share canonical bone names (the exporter prefix is dropped); tiny is its own family.
        Assert.Equal(2, snap.Families.Length);
        var family = snap.FamilyOf("twin.v3c")!;
        Assert.Equal(["guard.v3c", "twin.v3c"], family.Members.AsEnumerable());
        Assert.Same(family, snap.FamilyOf("GUARD"));
        Assert.Equal(["tiny.v3c"], snap.FamilyOf("tiny.v3c")!.Members.AsEnumerable());
        Assert.Null(snap.FamilyOf("crate.v3m"));
        Assert.Equal(["bad.v3c", "crate.v3m"], snap.StaticMeshes.Select(m => m.Name));

        Assert.Equal(["stand.rfa"], snap.FilterClips("TAN").Select(c => c.Name));
        Assert.Equal(["stand.rfa"], snap.FilterClips("*t*d").Select(c => c.Name)); // wildcard on the base name
        Assert.Equal(["run.rfa"], snap.FilterClips("?un").Select(c => c.Name));
        Assert.Equal(["walk.rfa"], snap.FilterClips("W*").Select(c => c.Name));
        Assert.Equal(2, snap.FilterClips("w*", includeShadowed: true).Count);
        Assert.Equal(4, snap.FilterClips("*.rfa").Count); // or the whole file name
        Assert.Equal(4, snap.FilterClips(null).Count);
        Assert.Equal(["crate.v3m"], snap.FilterMeshes("*.v3m").Select(m => m.Name));
        Assert.Equal(["tiny.v3c", "twin.v3c"], snap.FilterMeshes("t*").Select(m => m.Name));
        Assert.True(LibrarySnapshot.Matches("ult2_stand.rfa", "ult2*stand"));
        Assert.False(LibrarySnapshot.Matches("ult2_stand.rfa", "stand*"));

        var guard = snap.FindMesh("guard.v3c")!;
        Assert.Equal(["stand.rfa", "walk.rfa"], snap.CompatibleClips(guard).Select(c => c.Name));
        Assert.Equal(["run.rfa"], snap.CompatibleClips(2).Select(c => c.Name));
        Assert.Empty(snap.CompatibleClips(0));
        Assert.Equal(["guard.v3c", "twin.v3c"], snap.CompatibleMeshes(3).Select(m => m.Name));
        Assert.Equal(ClipFit.ClipHasFewerBones, LibrarySnapshot.Fit(snap.FindClip("run")!, 3));
        Assert.Equal(ClipFit.ClipHasMoreBones, LibrarySnapshot.Fit(snap.FindClip("stand")!, 2));
        Assert.Null(LibrarySnapshot.Fit(snap.FindClip("corrupt")!, 3));
        var skeleton = Skeleton.FromFile(V3dReader.Read(Character("pelvis", "spine", "head"), "guard.v3c"));
        Assert.Equal(["stand.rfa", "walk.rfa"], snap.CompatibleClips(skeleton).Select(c => c.Name));
    }

    [Fact]
    public async Task TableUsageDrivesThePreviewDefaults()
    {
        using var f = new Fixture();
        var snap = await new AssetLibrary(f.Resolver).BuildAsync();
        var usage = ClipUsageIndex.FromTexts(Tables, null, null, null);

        var used = snap.ClipsUsedByMesh("guard.v3c", usage);
        Assert.Equal(["stand", "fire", "crouch"], used.Select(u => u.Usage.SlotName));
        Assert.Same(snap.FindClip("stand"), used[0].Clip);
        Assert.Null(used[2].Clip); // no searched location has nothere.rfa

        // Clip -> mesh: table usage first, then the caller's hint, then the first mesh with the bone count.
        Assert.Equal("guard.v3c", snap.DefaultPreviewMesh("walk.rfa", usage)!.Name);
        Assert.Equal("guard.v3c", snap.DefaultPreviewMesh("walk.rfa", usage, lastUsedMesh: "twin.v3c")!.Name);
        Assert.Equal("twin.v3c", snap.DefaultPreviewMesh("walk.rfa", null, lastUsedMesh: "twin.v3c")!.Name);
        Assert.Equal("guard.v3c", snap.DefaultPreviewMesh("walk.rfa", null, lastUsedMesh: "tiny.v3c")!.Name); // hint's bone count differs
        Assert.Equal("tiny.v3c", snap.DefaultPreviewMesh("run")!.Name);
        Assert.Null(snap.DefaultPreviewMesh("corrupt"));

        // Mesh -> clip: compatible clips, table usage on top, a stand clip preferred.
        Assert.Equal(["stand.rfa", "walk.rfa"], snap.PreviewClipCandidates("guard.v3c", usage).Select(c => c.Name));
        Assert.Equal("stand.rfa", snap.DefaultPreviewClip("guard.v3c", usage)!.Name);
        Assert.Equal("stand.rfa", snap.DefaultPreviewClip("twin.v3c", usage)!.Name);
        Assert.Equal("run.rfa", snap.DefaultPreviewClip("tiny.v3c")!.Name);
        Assert.Null(snap.DefaultPreviewClip("crate.v3m", usage));
        Assert.Empty(snap.PreviewClipCandidates("unknown.v3c"));
    }

    [Fact]
    public async Task TheCacheFileSavesEveryProbeForTheNextLibrary()
    {
        using var f = new Fixture();
        var first = new AssetLibrary(f.Resolver, f.CachePath);
        var cold = await first.BuildAsync();
        Assert.Equal(12, cold.Stats.Probed);
        Assert.Equal(1, cold.Stats.ArchivesOpened);
        Assert.True(cold.Stats.CacheFileWritten);
        Assert.True(File.Exists(f.CachePath));

        // Same instance: the memory cache answers, nothing is written.
        var again = await first.BuildAsync();
        Assert.Equal(0, again.Stats.Probed);
        Assert.False(again.Stats.CacheFileWritten);

        // A new library (as after a restart) reads the file: no entry bytes and no archive directory read.
        var resolver = new AssetResolver(f.Resolver.Options);
        var warm = await new AssetLibrary(resolver, f.CachePath).BuildAsync();
        Assert.True(warm.Stats.CacheFileLoaded);
        Assert.Equal(0, warm.Stats.Probed);
        Assert.Equal(0, warm.Stats.ArchivesOpened);
        Assert.Equal(1, warm.Stats.ArchivesFromCache);
        Assert.Equal(12, warm.Stats.FromCache);
        Assert.Equal(Describe(cold), Describe(warm));
        Assert.Equal(cold.Families.Select(x => string.Join(",", x.Members)), warm.Families.Select(x => string.Join(",", x.Members)));
        // An entry known only from the cache still opens.
        Assert.Equal(2, RfaReader.Read(warm.CopiesOfClip("stand")[1].Location.ReadAllBytes(), "stand.rfa").BoneCount);

        // A changed loose file is probed again; nothing else is.
        string standPath = Path.Combine(f.First, "stand.rfa");
        File.WriteAllBytes(standPath, Clip(5));
        File.SetLastWriteTimeUtc(standPath, DateTime.UtcNow.AddMinutes(1));
        var changed = await new AssetLibrary(new AssetResolver(f.Resolver.Options), f.CachePath).BuildAsync();
        Assert.Equal(1, changed.Stats.Probed);
        Assert.Equal(5, changed.FindClip("stand")!.BoneCount);
        Assert.True(changed.Stats.CacheFileWritten);

        // A changed archive is re-read as a whole.
        string pack = Path.Combine(f.Second, "pack.vpp");
        File.WriteAllBytes(pack, VppWriter.Build([("run.rfa", Clip(4)), ("solo.rfa", Clip(4))]));
        File.SetLastWriteTimeUtc(pack, DateTime.UtcNow.AddMinutes(2));
        var repacked = await new AssetLibrary(new AssetResolver(f.Resolver.Options), f.CachePath).BuildAsync();
        Assert.Equal(1, repacked.Stats.ArchivesOpened);
        Assert.Equal(2, repacked.Stats.Probed);
        Assert.Equal(4, repacked.FindClip("solo")!.BoneCount);
        Assert.Null(repacked.FindMesh("tiny.v3c"));
    }

    private static IEnumerable<string> Describe(LibrarySnapshot s) =>
        s.AllClips.Select(c => $"{c.Name}|{c.Location.Kind}|{c.Location.FilePath}|{c.Location.ArchivePath}|{c.Location.Entry?.Offset}|{c.Facts}|{c.Error}|{c.IsShadowed}")
            .Concat(s.AllMeshes.Select(m => $"{m.Name}|{m.Location.Kind}|{m.Location.Entry?.Offset}|{m.Kind}|{string.Join(",", m.Facts?.BoneNames ?? [])}|{string.Join(",", m.Facts?.BoneParents ?? [])}|{m.Facts?.LodCounts.Length}|{m.Error}|{m.IsShadowed}"));

    [Fact]
    public async Task AnArchiveReachedTwiceIsCachedOnce()
    {
        using var f = new Fixture();
        // The same folder twice (as when the game directory is also a search folder).
        var options = new AssetResolverOptions { SearchFolders = [f.Second, f.Second] };
        var cold = await new AssetLibrary(new AssetResolver(options), f.CachePath).BuildAsync();
        var warm = await new AssetLibrary(new AssetResolver(options), f.CachePath).BuildAsync();
        var again = await new AssetLibrary(new AssetResolver(options), f.CachePath).BuildAsync();
        Assert.Equal(cold.Stats.Locations, warm.Stats.Locations);
        Assert.Equal(cold.Stats.Locations, again.Stats.Locations);
        Assert.Equal(0, again.Stats.Probed);
        Assert.Equal(Describe(cold), Describe(again));
    }

    [Fact]
    public async Task ACorruptCacheFileIsRebuilt()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(f.CachePath)!);
        File.WriteAllText(f.CachePath, "{ this is not json");
        var snap = await new AssetLibrary(f.Resolver, f.CachePath).BuildAsync();
        Assert.Equal(12, snap.Stats.Probed);
        Assert.NotNull(snap.Stats.CacheFileError);
        Assert.True(snap.Stats.CacheFileWritten);
        var warm = await new AssetLibrary(new AssetResolver(f.Resolver.Options), f.CachePath).BuildAsync();
        Assert.Equal(0, warm.Stats.Probed);
        Assert.Null(warm.Stats.CacheFileError);
    }

    [Fact]
    public async Task BuildsReportProgressAndCanBeCancelled()
    {
        using var f = new Fixture();
        var library = new AssetLibrary(f.Resolver);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.BuildAsync(null, new CancellationToken(true)));
        Assert.Same(LibrarySnapshot.Empty, library.Snapshot);

        // Cancel as soon as probing starts: the previous snapshot stays.
        using var cts = new CancellationTokenSource();
        var cancelling = new SyncProgress(p =>
        {
            if (p.Phase == LibraryBuildPhase.Probing) cts.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => library.BuildAsync(cancelling, cts.Token));
        Assert.Same(LibrarySnapshot.Empty, library.Snapshot);

        var phases = new List<LibraryBuildPhase>();
        var snap = await library.BuildAsync(new SyncProgress(p => { lock (phases) phases.Add(p.Phase); }));
        Assert.Equal(LibraryBuildPhase.Enumerating, phases[0]);
        Assert.Contains(LibraryBuildPhase.Probing, phases);
        Assert.Equal(LibraryBuildPhase.Done, phases[^1]);
        Assert.Equal(12, snap.Stats.Locations);
    }

    [Fact]
    public async Task ConcurrentBuildsAreSerialisedAndReadersSeeWholeSnapshots()
    {
        using var f = new Fixture();
        var library = new AssetLibrary(f.Resolver);
        var builds = Enumerable.Range(0, 4).Select(_ => library.BuildAsync()).ToArray();
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                var s = library.Snapshot;
                Assert.True(s.Clips.Length is 0 or 4);
                Assert.Equal(s.AllClips.Count(c => !c.IsShadowed), s.Clips.Length);
            }
        });
        var results = await Task.WhenAll(builds);
        await reader;
        Assert.All(results, r => Assert.Equal(4, r.Clips.Length));
        Assert.Equal(12, results.Sum(r => r.Stats.Probed)); // only the first build probed
    }

    // ── Real data ────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheStockCorpusFormsTheKnownFamilies()
    {
        if (TestPaths.Corpus is null) return;
        using var temp = new TempFolder();
        var snap = await new AssetLibrary(new AssetResolver(new AssetResolverOptions { SearchFolders = [TestPaths.Corpus] }),
            temp.File("cache.json")).BuildAsync();
        Assert.Equal(1009, snap.Clips.Length);
        Assert.Equal(95 + 427, snap.Meshes.Length);
        Assert.Equal(0, snap.Stats.Errors);
        Assert.Contains("ult2_guard.v3c", snap.FamilyOf("miner.v3c")!.Members, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("nurse1.v3c", snap.FamilyOf("masako.v3c")!.Members, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(snap.CompatibleClips(snap.FindMesh("miner.v3c")!), c => c.Name.Equals("ult2_stand.rfa", StringComparison.OrdinalIgnoreCase));

        if (TestPaths.Tables is { } tables)
        {
            var usage = ClipUsageIndex.FromTexts(TblTokenizer.ReadText(Path.Combine(tables, "entity.tbl")),
                TblTokenizer.ReadText(Path.Combine(tables, "weapons.tbl")),
                TblTokenizer.ReadText(Path.Combine(tables, "pc_multi.tbl")),
                TblTokenizer.ReadText(Path.Combine(tables, "fpgun.tbl")));
            var mesh = snap.DefaultPreviewMesh("park_jeep_driver.rfa", usage)!;
            Assert.Contains(mesh.Name, usage.MeshesForClip("park_jeep_driver"), StringComparer.OrdinalIgnoreCase);
            Assert.Equal(snap.FindClip("park_jeep_driver")!.BoneCount, mesh.BoneCount);
            var clip = snap.DefaultPreviewClip("miner.v3c", usage)!;
            Assert.Contains("stand", clip.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(snap.FindMesh("miner.v3c")!.BoneCount, clip.BoneCount);
        }
    }

    [Fact]
    public async Task TheRealInstallIndexesQuicklyFromAWarmCache()
    {
        if (TestPaths.GameDirectory is not { } game || !File.Exists(Path.Combine(game, "meshes.vpp"))) return;
        using var temp = new TempFolder();
        string cache = temp.File("library-cache.json");
        var options = new AssetResolverOptions { GameDirectory = game };

        // A truly cold build reads every archive directory of the install (tens of seconds for a large,
        // heavily modded one). When the app has already built its own cache, a copy of it seeds the first
        // build (read only; entries that no longer match the disk are rebuilt), so the test measures the
        // warm path in a second or two.
        string appCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairn", "library-cache.json");
        bool seeded = false;
        try
        {
            if (File.Exists(appCache))
            {
                File.Copy(appCache, cache);
                seeded = true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var cold = await new AssetLibrary(new AssetResolver(options), cache).BuildAsync();
        Assert.True(cold.Clips.Length > 1000, $"{cold.Clips.Length} clips");
        Assert.True(seeded || cold.Stats.CacheFileWritten);

        // "Quickly" is asserted as work not done, not as wall-clock time (which failed under parallel build load):
        // the warm build reads the cache file, takes every entry and every archive directory from it, opens no
        // archive and probes no file.
        var warm = await new AssetLibrary(new AssetResolver(options), cache).BuildAsync();
        Assert.True(warm.Stats.CacheFileLoaded, warm.Stats.CacheFileError);
        Assert.Equal(0, warm.Stats.Probed);
        Assert.Equal(0, warm.Stats.ArchivesOpened);
        Assert.True(warm.Stats.ArchivesFromCache > 0, $"{warm.Stats.ArchivesFromCache} archives from the cache");
        Assert.Equal(0, warm.Stats.Errors);
        Assert.Equal(cold.AllClips.Length + cold.AllMeshes.Length, warm.Stats.Locations);
        Assert.Equal(warm.Stats.Locations, warm.Stats.FromCache);
        Assert.Equal(AssetSourceKind.GameArchive, warm.FindClip("ult2_stand")!.Location.Kind);
    }

    private sealed class SyncProgress(Action<LibraryProgress> report) : IProgress<LibraryProgress>
    {
        public void Report(LibraryProgress value) => report(value);
    }
}
