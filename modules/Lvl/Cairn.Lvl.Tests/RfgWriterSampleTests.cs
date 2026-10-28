using Cairn.Formats;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Lvl.Writing;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Lvl.Tests;

/// <summary>
/// Real group files: read, write with the file's own version and layouts, read again. The model must come back
/// equal, and the bytes identical wherever the source is canonical (written the way RED writes); every other file
/// must differ for a stated reason.
/// </summary>
public class RfgWriterSampleTests(ITestOutputHelper output)
{
    /// <summary>Properties that say where something was in the file, not what it is.</summary>
    internal static readonly string[] PlacementProperties = ["Offset"];

    /// <summary>The result of one real file's round trip.</summary>
    internal sealed record Outcome(string Name, bool Exact, string? Reason, IReadOnlyList<string> Differences, IReadOnlyList<string> UpgradeDifferences);

    internal static Outcome? RoundTrip(byte[] data, string name)
    {
        RfGroupFile file;
        try
        {
            file = RfgReader.Read(data, name);
        }
        catch (AssetFormatException)
        {
            return null; // refused files are the reader's business (its sample tests list them)
        }
        var written = RfgWriter.Write(file, RfgWriteOptions.AsStoredFor(file)).Bytes;
        var again = RfgReader.Read(written, name);
        var diffs = RfModelDiff.Compare(file with { Problems = [], UnknownDataOffset = null },
            again with { Problems = [], UnknownDataOffset = null }, 10, PlacementProperties);

        // Files of the released layouts (200 and later) also go through the default writer (version 300): the
        // model must survive the upgrade unchanged, apart from the version and the fallbacks it no longer needs.
        IReadOnlyList<string> upgrade = [];
        if (file.Version >= 0xC8 && (file.Fallbacks & RfGroupFallbacks.ReduxEventMatrices) == 0)
        {
            var upgraded = RfgReader.Read(RfgWriter.Write(file).Bytes, name);
            var expected = CurrentMeshLayout(file) with { Problems = [], UnknownDataOffset = null, Version = 300, Fallbacks = RfGroupFallbacks.None };
            upgrade = RfModelDiff.Compare(expected, upgraded with { Problems = [], UnknownDataOffset = null }, 10, PlacementProperties);
        }
        bool exact = written.AsSpan().SequenceEqual(data);
        return new Outcome(name, exact, exact ? null : Reason(file, data, written), diffs, upgrade);
    }

    /// <summary>
    /// A solid's scroll list in RED's internal order: the model keeps each face's speed, not the list order, so the
    /// writer lists them in face order (same bytes, other order).
    /// </summary>
    internal const string ScrollOrderReason = "a scroll list of several entries in RED's own order (written in face order)";

    /// <summary>The file as the current Alpine mesh layout stores it: fields older builds did not write get Alpine's defaults.</summary>
    internal static RfGroupFile CurrentMeshLayout(RfGroupFile file) => file with
    {
        Groups = [.. file.Groups.Select(g => g with
        {
            Objects = g.Objects with
            {
                AlpineMeshes = [.. g.Objects.AlpineMeshes.Select(m => m with
                {
                    Material = m.Material ?? 0,
                    Clutter = m.Clutter is { } k ? k with { Corpse = k.Corpse ?? "", CorpseAnim = k.CorpseAnim ?? "", CorpseCollision = k.CorpseCollision ?? 0, CorpseMaterial = k.CorpseMaterial ?? -1 } : null,
                    TrailingFlags = m.TrailingFlags ?? 0,
                    BrushGeometrySource = m.BrushGeometrySource ?? 0,
                    CollisionMesh = m.CollisionMesh ?? "",
                    DrawScale = m.DrawScale ?? 1f,
                })],
            },
        })],
    };

    /// <summary>Why a file does not come back byte for byte.</summary>
    internal static string Reason(RfGroupFile file, byte[] data, byte[] written)
    {
        if (file.UnknownDataOffset is not null) return "data after the groups that is not an Alpine chunk (not kept)";
        if (written.Length > data.Length && written.AsSpan(0, data.Length).SequenceEqual(data))
            return "the file ends early (RED reads the missing lists as empty; the writer writes them)";
        if (written.Length == data.Length && file.AllBrushes.Any(b => b.Geometry.Faces.Count(f => f.Scroll is not null) + b.Geometry.OrphanScrolls.Length > 1))
            return ScrollOrderReason;
        int at = 0;
        int n = Math.Min(data.Length, written.Length);
        while (at < n && data[at] == written[at]) at++;
        return $"first difference at offset {at} of {data.Length} (written {written.Length})";
    }

    private void Report(IReadOnlyList<Outcome> outcomes)
    {
        output.WriteLine($"{outcomes.Count} files, {outcomes.Count(o => o.Exact)} byte-identical");
        foreach (var g in outcomes.Where(o => !o.Exact).GroupBy(o => o.Reason!.StartsWith("first difference", StringComparison.Ordinal) ? "other" : o.Reason))
        {
            output.WriteLine($"  {g.Count()} x {g.Key}");
            foreach (var o in g.Take(40)) output.WriteLine($"    {o.Name}: {o.Reason}");
        }
        foreach (var o in outcomes.Where(o => o.Differences.Count > 0).Take(20))
            output.WriteLine($"  MODEL DIFFERS {o.Name}: {string.Join("; ", o.Differences.Take(3))}");
    }

    private static List<Outcome> Run(IEnumerable<LvlSamples.Sample> samples)
    {
        var outcomes = new List<Outcome>();
        foreach (var s in samples)
            if (RoundTrip(s.Load(), s.Name) is { } o) outcomes.Add(o);
        return outcomes;
    }

    [Fact]
    public void Editor_groups_round_trip()
    {
        if (LocalPaths.GameDirectory is not { } dir) return;
        var outcomes = Run(LvlSamples.Files(Path.Combine(dir, "red", "groups"), "*.rfg", recursive: true));
        Report(outcomes);
        Check(outcomes);
    }

    [Fact]
    public void Redux_groups_round_trip()
    {
        var all = LvlSamples.Files(LocalPaths.ReduxResearchData, "*.rfg", recursive: true).ToList();
        var outcomes = Run(LvlSamples.CorpusEnabled ? all : LvlSamples.Spread(all, 30));
        Report(outcomes);
        Check(outcomes);
    }

    [Fact]
    public void Corpus_groups_round_trip()
    {
        if (!LvlSamples.CorpusEnabled || LocalPaths.GameDirectory is not { } dir) return;
        var outcomes = Run(LvlSamples.Files(dir, "*.rfg", recursive: true));
        Report(outcomes);
        Check(outcomes);
    }

    /// <summary>Every model comes back equal (also through the upgrade to 300), and every file that is not byte-identical has a stated reason.</summary>
    private static void Check(List<Outcome> outcomes)
    {
        Assert.All(outcomes, o => Assert.True(o.Differences.Count == 0, $"{o.Name}: {string.Join("; ", o.Differences)}"));
        Assert.All(outcomes, o => Assert.True(o.UpgradeDifferences.Count == 0, $"{o.Name} (written as 300): {string.Join("; ", o.UpgradeDifferences)}"));
        Assert.DoesNotContain(outcomes, o => !o.Exact && o.Reason!.StartsWith("first difference", StringComparison.Ordinal));
    }
}
