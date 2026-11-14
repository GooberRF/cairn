using Cairn.Formats.XForge;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests.XForge;

/// <summary>
/// Every scene of Red Faction for N-Gage, read from the game's <c>redfaction.cfl</c> when <c>ngageDirectory</c> is set in
/// research/local-paths.json (skipped otherwise): every file walks to its end, every typed chunk fits its layout, every
/// reference resolves, and the classification gives the 35 <c>level_*</c> files as levels.
/// </summary>
public class XffCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryScene_ReadsCleanly()
    {
        string? path = LocalPaths.NgageDirectory is { } dir ? Path.Combine(dir, "redfaction.cfl") : null;
        if (path is null || !File.Exists(path))
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory"));
            return;
        }
        var archive = CflArchive.Open(path);
        using var stream = File.OpenRead(path);
        var kinds = new Dictionary<XffKind, int>();
        var failures = new List<string>();
        int files = 0, chunks = 0, triangles = 0;
        foreach (var entry in archive.Entries.Where(e => XffFile.HasExtension(e.Name)))
        {
            files++;
            var file = XffFile.Read(archive.ReadEntry(stream, entry), entry.Name);
            var scene = XffScene.Build(file);
            chunks += file.Chunks.Count;
            triangles += scene.Meshes.Sum(m => m.Submeshes.Sum(s => s.TriangleCount));
            if (file.RawChunkCount > 0) failures.Add($"{entry.Name}: {file.RawChunkCount} chunks kept raw");
            failures.AddRange(scene.Problems.Select(p => $"{entry.Name}: {p}"));
            var kind = scene.Kind;
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
            if (entry.Name.StartsWith("level_", StringComparison.OrdinalIgnoreCase) != (kind == XffKind.Level))
                failures.Add($"{entry.Name}: classified as {kind}");
        }
        output.WriteLine($"{files} scenes, {chunks} chunks, {triangles} triangles; " + string.Join(", ", kinds.OrderBy(k => k.Key).Select(k => $"{k.Value} {k.Key}")));
        Assert.Empty(failures);
        Assert.Equal(264, files);
        Assert.Equal(35, kinds.GetValueOrDefault(XffKind.Level));
        Assert.Equal(25, kinds.GetValueOrDefault(XffKind.Character));
    }
}
