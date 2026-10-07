using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// The acceptance tests of the format layer: every stock clip and mesh re-serialises byte for byte,
/// and the community-exported samples next door load and survive read, write, read unchanged. The
/// files are read in place; each test passes trivially when its folder is absent.
/// </summary>
public class CorpusRoundTripTests(ITestOutputHelper output)
{
    private const int StockClipCount = 1009;
    private const int StockCharacterCount = 95;
    private const int StockStaticMeshCount = 427;

    [Fact]
    public void EveryStockClipRoundTripsByteForByte()
    {
        if (TestPaths.Corpus is null) return;
        var (count, failures) = RoundTrip("*.rfa", bytes => RfaWriter.Write(RfaReader.Read(bytes, "clip")));
        output.WriteLine($".rfa: {count - failures.Count}/{count} byte-identical");
        Assert.Empty(failures);
        Assert.Equal(StockClipCount, count);
    }

    [Fact]
    public void EveryStockCharacterMeshRoundTripsByteForByte()
    {
        if (TestPaths.Corpus is null) return;
        var (count, failures) = RoundTrip("*.v3c", bytes => V3dWriter.Write(V3dReader.Read(bytes, "mesh")));
        output.WriteLine($".v3c: {count - failures.Count}/{count} byte-identical");
        Assert.Empty(failures);
        Assert.Equal(StockCharacterCount, count);
    }

    [Fact]
    public void EveryStockStaticMeshRoundTripsByteForByte()
    {
        if (TestPaths.Corpus is null) return;
        var (count, failures) = RoundTrip("*.v3m", bytes => V3dWriter.Write(V3dReader.Read(bytes, "mesh")));
        output.WriteLine($".v3m: {count - failures.Count}/{count} byte-identical");
        Assert.Empty(failures);
        Assert.Equal(StockStaticMeshCount, count);
    }

    [Fact]
    public void EveryStockMeshProbesConsistentlyWithTheFullReader()
    {
        if (TestPaths.Corpus is null) return;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus).Where(IsMesh))
        {
            byte[] bytes = File.ReadAllBytes(path);
            var probe = V3dProbe.Probe(bytes, Path.GetFileName(path));
            var full = V3dReader.Read(bytes, Path.GetFileName(path));
            Assert.True(probe.StructureReadable, path);
            Assert.Equal(full.Kind, probe.Kind);
            Assert.Equal(full.Submeshes.Select(s => s.Lods.Length), probe.LodCounts);
            Assert.Equal(full.Bones.Select(b => b.Name.Text), probe.BoneNames);
            Assert.Equal(full.CollisionSpheres.Count(), probe.CollisionSphereCount);
        }
    }

    [Fact]
    public void EveryStockClipProbesConsistentlyWithTheFullReader()
    {
        if (TestPaths.Corpus is null) return;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa"))
        {
            var probe = RfaProbe.ProbeFile(path);
            var clip = RfaReader.ReadFile(path);
            Assert.Equal(clip.Version, probe.Version);
            Assert.Equal(clip.BoneCount, probe.BoneCount);
            Assert.Equal(clip.StartTime, probe.StartTime);
            Assert.Equal(clip.EndTime, probe.EndTime);
            Assert.Equal(clip.Morph.VertexCount, probe.MorphVertexCount);
            Assert.Equal(clip.Morph.KeyframeCount, probe.MorphKeyframeCount);
        }
    }

    [Fact]
    public void CommunityExportedSamplesLoadAndRoundTripStructurally()
    {
        if (TestPaths.ReduxResearch is null) return;
        int loaded = 0;
        var failures = new List<string>();
        foreach (string folder in new[] { "anim", "dev", "br" })
        {
            string dir = Path.Combine(TestPaths.ReduxResearch, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".rfa" or ".v3c" or ".v3m")) continue;
                string name = Path.GetFileName(path);
                try
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    object first, second;
                    byte[] written;
                    if (ext == ".rfa")
                    {
                        var clip = RfaReader.Read(bytes, name);
                        written = RfaWriter.Write(clip);
                        first = clip;
                        second = RfaReader.Read(written, name);
                    }
                    else
                    {
                        var mesh = V3dReader.Read(bytes, name);
                        written = V3dWriter.Write(mesh);
                        first = mesh;
                        second = V3dReader.Read(written, name);
                    }
                    if (ModelAssert.Diff(first, second, name) is { } diff) failures.Add(diff);
                    loaded++;
                    output.WriteLine($"{folder}/{name}: loaded, {(bytes.AsSpan().SequenceEqual(written) ? "byte-identical" : "rewritten canonically")}");
                }
                catch (Exception ex)
                {
                    failures.Add($"{folder}/{name}: {ex.Message}");
                }
            }
        }
        output.WriteLine($"{loaded} community files loaded");
        Assert.Empty(failures);
    }

    private static bool IsMesh(string path) =>
        Path.GetExtension(path).Equals(".v3c", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".v3m", StringComparison.OrdinalIgnoreCase);

    private (int Count, List<string> Failures) RoundTrip(string pattern, Func<byte[], byte[]> roundTrip)
    {
        int count = 0;
        var failures = new List<string>();
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus!, pattern).Order(StringComparer.OrdinalIgnoreCase))
        {
            // EnumerateFiles' pattern also matches longer extensions (".rfa*"); be exact.
            if (!Path.GetExtension(path).Equals(pattern[1..], StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            byte[] original = File.ReadAllBytes(path);
            try
            {
                byte[] written = roundTrip(original);
                if (!original.AsSpan().SequenceEqual(written))
                {
                    int at = original.AsSpan().CommonPrefixLength(written);
                    failures.Add($"{Path.GetFileName(path)}: differs at byte {at} ({original.Length} vs {written.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        foreach (string f in failures) output.WriteLine(f);
        return (count, failures);
    }
}
