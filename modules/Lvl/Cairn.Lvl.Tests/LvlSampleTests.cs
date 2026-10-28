using System.Collections.Concurrent;
using Cairn.Lvl.Reading;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Real levels and groups (bounded samples in the normal run; everything with <see cref="LvlSamples.CorpusVariable"/>).
/// Each test passes trivially when its data is absent.
/// </summary>
public class LvlSampleTests(ITestOutputHelper output)
{
    // Files in the community corpus known not to read exactly, each for a reason the format notes give.
    private static readonly string[] KnownBadLevelNames =
    [
        "terrain_brokenew",   // Alpine terrain test levels named broken by their author (a face count of -1)
        "dm-discohell",       // tampered file: garbage groups
        "tt1111",             // pre-release Alpine 300 with a partly stripped event
    ];

    private static readonly string[] KnownBadGroupNames =
    [
        "hall01.rfg",         // truncated inside a face (57,344 bytes = 14 x 4 KiB)
        "elevator shaft.rfg", // v80 with the only non-empty legacy geometry list
        "333.rfg", "abc.rfg", "abc2.rfg", // early Alpine build with an obsolete trailing chunk
    ];

    private void Report(string title, int total, ConcurrentBag<(string Name, List<string> Issues)> failures)
    {
        output.WriteLine($"{title}: {total} files, {failures.Count} with unexplained issues");
        foreach (var (name, issues) in failures.OrderBy(f => f.Name).Take(50))
            output.WriteLine($"  {name}: {string.Join("; ", issues.Take(3))}");
    }

    private (int Total, ConcurrentBag<(string, List<string>)> Failures) Run(IEnumerable<LvlSamples.Sample> samples, bool groups)
    {
        var failures = new ConcurrentBag<(string, List<string>)>();
        int total = 0;
        Parallel.ForEach(samples, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, s =>
        {
            byte[] data = s.Load();
            if (!groups && LvlSamples.IsRf2Level(data) && data.Length > 4) return; // RF2 levels have their own reader
            Interlocked.Increment(ref total);
            var issues = groups ? LvlSamples.CheckGroup(data, s.Name) : LvlSamples.CheckLevel(data, s.Name);
            if (issues.Count > 0) failures.Add((s.Name, issues));
        });
        return (total, failures);
    }

    [Fact]
    public void Stock_levels_in_the_game_packfiles_read_exactly()
    {
        // About 60 MB of levels in the normal run; all of them with the corpus switch.
        var samples = LvlSamples.GameLevels(LvlSamples.CorpusEnabled ? long.MaxValue : 60L << 20).ToList();
        if (samples.Count == 0) return;
        var (total, failures) = Run(samples, groups: false);
        Report("game levels", total, failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void Redux_research_rf1_and_alpine_levels_read_exactly()
    {
        var all = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfl", recursive: true, f => !LvlSamples.InRf2Folder(f)).ToList();
        if (all.Count == 0) return;
        // A spread of 24 in the normal run (the folder holds about 500 MB).
        var samples = LvlSamples.CorpusEnabled ? all : LvlSamples.Spread(all, 24).ToList();
        var (total, failures) = Run(samples, groups: false);
        Report("REDUX levels", total, failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void Redux_research_groups_read()
    {
        var all = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfg", recursive: true).ToList();
        if (all.Count == 0) return;
        var samples = LvlSamples.CorpusEnabled ? all : LvlSamples.Spread(all, 30).ToList();
        var (total, failures) = Run(samples, groups: true);
        Report("REDUX groups", total, failures);
        Assert.Empty(failures);
    }

    [Fact]
    public void Editor_and_community_groups_read()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        var all = LvlSamples.Files(Path.Combine(dir, "red", "groups"), "*.rfg", recursive: true).ToList();
        if (all.Count == 0) return;
        var (total, failures) = Run(all, groups: true);
        Report("editor groups", total, failures);
        Assert.DoesNotContain(failures, f => !IsKnownBadGroup(f.Item1));
    }

    [Fact]
    public void A_spread_of_community_levels_reads_exactly()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        var all = LvlSamples.Files(Path.Combine(dir, "user_maps", "single"), "*.rfl", recursive: false,
            f => new FileInfo(f).Length <= 24 << 20).ToList();
        if (all.Count == 0) return;
        var (total, failures) = Run(LvlSamples.Spread(all, 16).ToList(), groups: false);
        Report("community levels", total, failures);
        Assert.DoesNotContain(failures, f => !IsKnownBadLevel(f.Item1));
    }

    private static bool IsKnownBadLevel(string name) =>
        KnownBadLevelNames.Any(k => Path.GetFileName(name).Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsKnownBadGroup(string name) =>
        KnownBadGroupNames.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opt-in: every loose level and group under the game folder, and every level inside every packfile there.</summary>
    [Fact]
    public void Corpus_every_level_and_group_under_the_game_folder()
    {
        if (!LvlSamples.CorpusEnabled || LocalPaths.GameDirectory is not { } dir) return;
        var levels = LvlSamples.Files(dir, "*.rfl", recursive: true).ToList();
        var packed = Directory.EnumerateFiles(dir, "*.vpp", SearchOption.AllDirectories)
            .SelectMany(p => LvlSamples.PackEntries(p, ".rfl").Select(e => e.Sample));
        var (total, failures) = Run(levels.Concat(packed), groups: false);
        var groups = LvlSamples.Files(dir, "*.rfg", recursive: true).ToList();
        var (gTotal, gFailures) = Run(groups, groups: true);

        var byReason = failures.GroupBy(f => CorpusReason(f.Item1, f.Item2)).ToList();
        output.WriteLine($"corpus levels: {total} files, {failures.Count} not read exactly");
        foreach (var g in byReason.OrderBy(g => g.Key))
            output.WriteLine($"  {g.Key}: {g.Count()}");
        var unexplained = byReason.Where(g => g.Key == Unexplained).SelectMany(g => g).ToList();
        foreach (var (name, issues) in unexplained.OrderBy(f => f.Item1))
            output.WriteLine($"  UNEXPLAINED {name}: {string.Join("; ", issues.Take(3))}");
        Report("corpus groups", gTotal, gFailures);

        Assert.Empty(unexplained);
        Assert.DoesNotContain(gFailures, f => !IsKnownBadGroup(f.Item1));
    }

    private const string Unexplained = "~unexplained";

    /// <summary>The stated reason a corpus level does not read exactly (from the format notes), or <see cref="Unexplained"/>.</summary>
    private static string CorpusReason(string name, List<string> issues)
    {
        if (IsKnownBadLevel(name)) return "known damaged or tampered file";
        if (issues.All(i => i.StartsWith("refused:", StringComparison.Ordinal) && i.EndsWith("is empty.", StringComparison.Ordinal)))
            return "empty file";
        if (issues.All(i => i.StartsWith("refused:", StringComparison.Ordinal) && i.Contains("is not a Red Faction level", StringComparison.Ordinal)))
            return "not a level (wrong signature)";
        if (issues.All(i => i.StartsWith("refused:", StringComparison.Ordinal) && i.Contains("level version", StringComparison.Ordinal)))
            return "hand-edited or unsupported version";
        if (issues.All(i => i == "truncated" || i.Contains(": Truncated", StringComparison.Ordinal)))
            return "truncated save (no end marker or a section past the end)";
        if (issues.All(i => i.StartsWith("Alpine meshes", StringComparison.Ordinal) || i == "truncated"))
            return "dev-era Alpine mesh layout (kept as stored)";
        return Unexplained;
    }

    private static int PeekVersion(string path)
    {
        using var s = File.OpenRead(path);
        var head = new byte[8];
        return s.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 ? BitConverter.ToInt32(head, 4) : 0;
    }

    /// <summary>True when the rows form a proper rotation (right x up = forward), which a wrong row order breaks.</summary>
    private static bool IsRotation(Model.RfMatrix m) =>
        Vector3.Distance(Vector3.Cross(m.Right, m.Up), m.Forward) < 1e-2f && Math.Abs(m.Right.Length() - 1) < 1e-2f;

    [Fact]
    public void Real_orientations_are_proper_rotations_in_stock_and_alpine_sections()
    {
        var stock = LvlSamples.GameLevels(1).FirstOrDefault();
        if (stock is not null)
        {
            var level = RflLevelReader.Read(stock.Load(), stock.Name);
            Assert.NotEmpty(level.Brushes);
            Assert.All(level.Brushes, b => Assert.True(IsRotation(b.Orientation), $"brush {b.Uid}: {b.Orientation}"));
            Assert.All(level.Objects.Lights, l => Assert.True(IsRotation(l.Orientation), $"light {l.Uid}"));
        }

        // Alpine chunks store the rows in another order; the reader must put them in the same places.
        var alpine = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfl", recursive: true, f => !LvlSamples.InRf2Folder(f) && PeekVersion(f) >= 304)
            .Select(s => RflLevelReader.Read(s.Load(), s.Name))
            .FirstOrDefault(l => l.Objects.AlpineMeshes.Length > 0);
        if (alpine is not null)
            Assert.All(alpine.Objects.AlpineMeshes, m => Assert.True(IsRotation(m.Orientation), $"mesh {m.Uid}: {m.Orientation}"));
    }

    [Fact]
    public void Reading_never_throws_anything_but_asset_format_exceptions_on_a_real_level()
    {
        var sample = LvlSamples.GameLevels(1).FirstOrDefault();
        if (sample is null) return;
        var bytes = sample.Load();
        var level = RflLevelReader.Read(bytes, sample.Name);
        Assert.True(level.StaticGeometry.Faces.Length > 0);
        Assert.True(level.Brushes.Length > 0);
        LvlFuzzTests.Mutate(bytes, sample.Name, iterations: 40, seed: 7);
    }
}
