using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// The whole stock corpus must lint without errors: every clip against a stock skeleton with its
/// bone count, every mesh structurally. Counts per code are written to the test output.
/// </summary>
public class LintCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryStockClipLintsWithoutErrorsAgainstItsOwnSkeleton()
    {
        if (TestPaths.Corpus is null) return;
        var skeletons = new Dictionary<int, (Skeleton Skeleton, string Name, V3dFile Mesh)>();
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.v3c").Order(StringComparer.OrdinalIgnoreCase))
        {
            var mesh = V3dReader.ReadFile(path);
            var skeleton = Skeleton.FromFile(mesh);
            skeletons.TryAdd(skeleton.Count, (skeleton, Path.GetFileName(path), mesh));
        }

        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<string>();
        int clips = 0;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa"))
        {
            var clip = RfaReader.ReadFile(path);
            clips++;
            var context = new ClipLintContext { FileName = Path.GetFileName(path) };
            if (skeletons.TryGetValue(clip.BoneCount, out var s))
                context = context with { Skeleton = s.Skeleton, PreviewMeshName = s.Name };
            foreach (var d in ClipLinter.Analyze(clip, context))
            {
                counts[d.Code] = counts.GetValueOrDefault(d.Code) + 1;
                if (d.Severity == DiagnosticSeverity.Error && errors.Count < 20) errors.Add($"{Path.GetFileName(path)}: {d}");
            }
        }
        output.WriteLine($"{clips} clips");
        foreach (var (code, n) in counts) output.WriteLine($"{code} {ClipRules.Find(code)?.Severity}: {n}");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public void EveryStockMeshLintsWithoutErrors()
    {
        if (TestPaths.Corpus is null) return;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var errors = new List<string>();
        int meshes = 0;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.v3?"))
        {
            if (!path.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase)) continue;
            var mesh = V3dReader.ReadFile(path);
            meshes++;
            foreach (var d in MeshLinter.Analyze(mesh, new MeshLintContext { FileName = Path.GetFileName(path) }))
            {
                counts[d.Code] = counts.GetValueOrDefault(d.Code) + 1;
                if (d.Severity == DiagnosticSeverity.Error && errors.Count < 20) errors.Add($"{Path.GetFileName(path)}: {d}");
            }
        }
        output.WriteLine($"{meshes} meshes");
        foreach (var (code, n) in counts) output.WriteLine($"{code} {MeshRules.Find(code)?.Severity}: {n}");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
