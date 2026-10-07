using Cairn.Assets;
using Cairn.Atx.Sequences;

namespace Cairn.Atx.Tests;

/// <summary>
/// The UI-independent half of Add Frames from VPP: which entries count as images, how the filter
/// box matches, which entry names are safe to write to disk, and detecting a numbered run inside an
/// archive rather than on disk.
/// </summary>
public sealed class VppBrowsingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cairn-atx-vppbrowse-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string WriteArchive(string name, params (string Name, byte[] Data)[] files)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, VppWriter.Build(files));
        return path;
    }

    private static byte[] Bytes(int length) => new byte[length];

    // ── What counts as an image ───────────────────────────────────────────────

    [Theory]
    [InlineData("hazard.tga", true)]
    [InlineData("HAZARD.TGA", true)]
    [InlineData("pulse.dds", true)]
    [InlineData("thing.vbm", true)]
    [InlineData("shot.png", true)]
    [InlineData("shot.jpg", true)]
    [InlineData("shot.jpeg", true)]
    [InlineData("levels.rfl", false)]
    [InlineData("weapons.tbl", false)]
    [InlineData("old.pcx", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ImageEntriesAreTheReadableExtensions(string? name, bool expected) =>
        Assert.Equal(expected, VppImageIndex.IsImageEntry(name));

    [Fact]
    public void IndexKeepsOnlyImagesAndSortsThemNaturally()
    {
        string path = WriteArchive("mixed.vpp",
            ("hazard_10.tga", Bytes(4)),
            ("weapons.tbl", Bytes(8)),
            ("hazard_2.tga", Bytes(4)),
            ("hazard_1.tga", Bytes(4)),
            ("level.rfl", Bytes(16)));

        var index = VppImageIndex.Build(VppArchive.Open(path));

        Assert.Equal(["hazard_1.tga", "hazard_2.tga", "hazard_10.tga"], index.Names);
        Assert.Equal(5, index.TotalEntryCount);
        Assert.Equal("mixed.vpp", index.ArchiveName);
        Assert.Equal("mixed.vpp — 3 images", index.Describe());
        Assert.False(index.IsEmpty);
    }

    // An archive with no images is not listed in the browser at all; the index is what the tree
    // asks, and it has to be able to say so before a row is created for it.
    [Fact]
    public void ArchiveWithNoImagesReportsItself()
    {
        string path = WriteArchive("tables.vpp", ("weapons.tbl", Bytes(8)));
        var index = VppImageIndex.Build(VppArchive.Open(path));

        Assert.True(index.IsEmpty);
        Assert.False(index.IsUnreadable);
        Assert.Equal("tables.vpp — no images", index.Describe());
    }

    // "Holds no images" and "could not be read" are both empty, and the browser says different
    // things about an archive the user opened by hand depending on which it was.
    [Fact]
    public void AnUnreadableArchiveIsEmptyButSaysWhy()
    {
        var index = VppImageIndex.Unreadable(Path.Combine(_root, "broken.vpp"));

        Assert.True(index.IsEmpty);
        Assert.True(index.IsUnreadable);
        Assert.Equal("broken.vpp", index.ArchiveName);
    }

    [Fact]
    public void AnArchiveHoldingOneImageIsNotEmpty()
    {
        string path = WriteArchive("meshes.vpp",
            ("player.v3d", Bytes(16)), ("logo.tga", Bytes(4)));
        var index = VppImageIndex.Build(VppArchive.Open(path));

        Assert.False(index.IsEmpty);
        Assert.Equal("meshes.vpp — 1 image", index.Describe());
    }

    [Fact]
    public void IndexCarriesEntrySizes()
    {
        string path = WriteArchive("sized.vpp", ("a.tga", Bytes(1234)));
        var index = VppImageIndex.Build(VppArchive.Open(path));

        Assert.Equal(1234, index.Images[0].Size);
    }

    // ── The filter box ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("haz", "hazard_00.tga", true)]
    [InlineData("HAZ", "hazard_00.tga", true)]
    [InlineData("zard", "hazard_00.tga", true)]
    [InlineData("pulse", "hazard_00.tga", false)]
    [InlineData("haz*", "hazard_00.tga", true)]
    [InlineData("*.tga", "hazard_00.tga", true)]
    [InlineData("*.dds", "hazard_00.tga", false)]
    [InlineData("hazard_0?.tga", "hazard_00.tga", true)]
    [InlineData("hazard_0?.tga", "hazard_000.tga", false)]
    [InlineData("*", "anything.tga", true)]
    [InlineData("", "anything.tga", true)]
    [InlineData("   ", "anything.tga", true)]
    [InlineData(null, "anything.tga", true)]
    public void FilterMatchesSubstringsAndWildcards(string? query, string name, bool expected) =>
        Assert.Equal(expected, VppEntryFilter.Matches(name, query));

    [Fact]
    public void FilterPreservesOrderAndReturnsTheSameListWhenEmpty()
    {
        IReadOnlyList<VppImageEntry> entries =
        [
            new("hazard_00.tga", 1), new("pulse_00.dds", 2), new("hazard_01.tga", 3),
        ];

        Assert.Same(entries, VppEntryFilter.Apply(entries, "  "));
        Assert.Equal(
            ["hazard_00.tga", "hazard_01.tga"],
            VppEntryFilter.Apply(entries, "hazard").Select(e => e.Name));
    }

    [Fact]
    public void WildcardIsDetectedFromTheQuery()
    {
        Assert.True(VppEntryFilter.IsWildcard("a*b"));
        Assert.True(VppEntryFilter.IsWildcard("a?b"));
        Assert.False(VppEntryFilter.IsWildcard("ab"));
        Assert.False(VppEntryFilter.IsWildcard(null));
    }

    [Fact]
    public void FilterStaysFastOnAThreeThousandEntryArchive()
    {
        var entries = new List<VppImageEntry>(3000);
        for (int i = 0; i < 3000; i++) entries.Add(new VppImageEntry($"tex_{i:D4}.tga", 64));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (int pass = 0; pass < 20; pass++) VppEntryFilter.Apply(entries, "tex_1");
        watch.Stop();

        // 60,000 comparisons. This is a guard against someone reaching for a compiled regex per
        // keystroke, not a benchmark: a second is thousands of times the real cost.
        Assert.True(watch.ElapsedMilliseconds < 1000, $"filter took {watch.ElapsedMilliseconds} ms");
    }

    // ── Safe extraction ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("hazard_00.tga", true)]
    [InlineData("a.tga", true)]
    [InlineData(@"..\..\Windows\System32\evil.dll", false)]
    [InlineData("../evil.tga", false)]
    [InlineData(@"sub\thing.tga", false)]
    [InlineData("sub/thing.tga", false)]
    [InlineData(@"C:\Windows\evil.tga", false)]
    [InlineData(@"\\server\share\evil.tga", false)]
    [InlineData("..", false)]
    [InlineData(".", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("trailing.", false)]
    [InlineData(" leading.tga", false)]
    [InlineData("CON", false)]
    [InlineData("nul.tga", false)]
    [InlineData("COM1.tga", false)]
    [InlineData("contains:colon.tga", false)]
    public void HostileEntryNamesAreRefused(string? name, bool expected) =>
        Assert.Equal(expected, VppExtraction.IsSafeEntryName(name));

    [Fact]
    public void DestinationStaysInsideTheAtxFolder()
    {
        string folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);

        string? good = VppExtraction.DestinationFor(folder, "hazard_00.tga");
        Assert.Equal(Path.Combine(Path.GetFullPath(folder), "hazard_00.tga"), good);

        Assert.Null(VppExtraction.DestinationFor(folder, @"..\escaped.tga"));
        Assert.Null(VppExtraction.DestinationFor(folder, @"..\..\Windows\System32\evil.dll"));
        Assert.Null(VppExtraction.DestinationFor(null, "hazard_00.tga"));
        Assert.Null(VppExtraction.DestinationFor("   ", "hazard_00.tga"));
    }

    [Fact]
    public void AHostileNameInARealArchiveNeverEscapes()
    {
        // The whole point: a .vpp out of a downloaded map pack can name its entries anything.
        string path = WriteArchive("hostile.vpp",
            (@"..\..\escaped.tga", Bytes(4)),
            ("legit.tga", Bytes(4)));
        var index = VppImageIndex.Build(VppArchive.Open(path));
        string folder = Path.Combine(_root, "dest");
        Directory.CreateDirectory(folder);

        foreach (var entry in index.Images)
        {
            string? destination = VppExtraction.DestinationFor(folder, entry.Name);
            if (destination is null) continue;
            Assert.StartsWith(
                Path.GetFullPath(folder) + Path.DirectorySeparatorChar, destination,
                StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(VppExtraction.DestinationFor(folder, @"..\..\escaped.tga"));
    }

    // ── Sequence detection over a name list ───────────────────────────────────

    [Fact]
    public void SequenceIsDetectedInsideAnArchivesNames()
    {
        string[] names =
        [
            "hazard_00.tga", "hazard_01.tga", "hazard_02.tga",
            "pulse_00.tga", "hazard_00.dds", "unrelated.tga",
        ];

        var run = FrameSequence.DetectInNames("hazard_01.tga", names);

        Assert.Equal(["hazard_00.tga", "hazard_01.tga", "hazard_02.tga"], run);
    }

    [Fact]
    public void SequenceDetectionSortsNaturallyAndIgnoresPadding()
    {
        string[] names = ["s_10.tga", "s_2.tga", "s_1.tga", "s_09.tga"];

        Assert.Equal(["s_1.tga", "s_2.tga", "s_09.tga", "s_10.tga"],
            FrameSequence.DetectInNames("s_2.tga", names));
    }

    [Fact]
    public void SequenceDetectionReturnsJustTheSeedWhenItIsNotNumbered()
    {
        Assert.Equal(["plain.tga"], FrameSequence.DetectInNames("plain.tga", ["plain.tga", "a_01.tga"]));
    }

    [Fact]
    public void SequenceDetectionNeverReturnsANameTwice()
    {
        string[] names = ["a_00.tga", "a_00.tga", "a_01.tga"];
        Assert.Equal(["a_00.tga", "a_01.tga"], FrameSequence.DetectInNames("a_00.tga", names));
    }

    [Fact]
    public void SequenceDetectionCopesWithAnEmptySeedAndAnEmptyList()
    {
        Assert.Empty(FrameSequence.DetectInNames(string.Empty, ["a_00.tga"]));
        Assert.Equal(["a_00.tga"], FrameSequence.DetectInNames("a_00.tga", []));
    }

    // ── Resolver seams the browser uses ───────────────────────────────────────

    [Fact]
    public void ResolverGroupsArchivesBySearchLocationInSearchOrder()
    {
        string game = Path.Combine(_root, "game");
        string search = Path.Combine(_root, "search");
        string atx = Path.Combine(_root, "atx");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(search);
        Directory.CreateDirectory(atx);
        File.WriteAllBytes(Path.Combine(game, "textures.vpp"), VppWriter.Build([("g.tga", Bytes(4))]));
        File.WriteAllBytes(Path.Combine(search, "mine.vpp"), VppWriter.Build([("s.tga", Bytes(4))]));
        File.WriteAllBytes(Path.Combine(atx, "beside.vpp"), VppWriter.Build([("a.tga", Bytes(4))]));

        var resolver = new AssetResolver(new AssetResolverOptions
        {
            DocumentFolder = atx,
            SearchFolders = [search],
            GameDirectory = game,
        });

        var groups = resolver.DescribeArchiveSources();

        Assert.Equal(3, groups.Count);
        Assert.Equal(AssetSourceKind.DocumentFolder, groups[0].Kind);
        Assert.Equal("Next to this document", groups[0].Label);
        // A .vpp beside the .atx is listed but is not a place the engine reads packfiles from.
        Assert.False(groups[0].IsSearched);
        Assert.Equal(AssetSourceKind.SearchFolderArchive, groups[1].Kind);
        Assert.True(groups[1].IsSearched);
        Assert.Equal(AssetSourceKind.GameArchive, groups[2].Kind);
        Assert.StartsWith("Red Faction (", groups[2].Label, StringComparison.Ordinal);
        Assert.True(groups[2].IsSearched);

        Assert.True(resolver.IsSearchedArchive(Path.Combine(game, "textures.vpp")));
        Assert.True(resolver.IsSearchedArchive(Path.Combine(search, "mine.vpp")));
        Assert.False(resolver.IsSearchedArchive(Path.Combine(atx, "beside.vpp")));
        Assert.False(resolver.IsSearchedArchive(Path.Combine(_root, "nowhere.vpp")));
        Assert.False(resolver.IsSearchedArchive(null));
    }

    [Fact]
    public void CachedArchiveIsTheSameInstanceEveryTime()
    {
        string path = WriteArchive("cached.vpp", ("a.tga", Bytes(4)));
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [_root] });

        var first = resolver.OpenCachedArchive(path);
        var second = resolver.OpenCachedArchive(path);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Null(resolver.OpenCachedArchive(Path.Combine(_root, "missing.vpp")));
        Assert.Null(resolver.OpenCachedArchive("   "));
    }

    [Fact]
    public void FoldersWithoutArchivesAreLeftOutOfTheTree()
    {
        string empty = Path.Combine(_root, "no-vpps");
        Directory.CreateDirectory(empty);
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [empty] });

        Assert.Empty(resolver.DescribeArchiveSources());
    }
}
