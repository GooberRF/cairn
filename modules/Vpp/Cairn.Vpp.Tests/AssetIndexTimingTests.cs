using System.Diagnostics;
using Cairn.Assets;
using Cairn.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Cairn.Vpp.Tests;

/// <summary>
/// How long indexing the game folder takes, and what a lookup costs afterwards: a name that is in no packfile
/// walks every indexed archive, which must stay a memory walk, not a disk check per packfile.
/// </summary>
public sealed class AssetIndexTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task GameFolder_IndexOnceThenLookupsAreCheap()
    {
        string? dir = LocalPaths.GameDirectory;
        if (dir is null || !Directory.Exists(dir)) return;
        var resolver = new AssetResolver(new AssetResolverOptions { GameDirectory = dir });
        var sw = Stopwatch.StartNew();
        await resolver.PrepareAsync();
        output.WriteLine($"index: {sw.ElapsedMilliseconds:N0} ms, {resolver.ArchiveDirectoryReads.Reads:N0} archive directories read");

        sw.Restart();
        await resolver.PrepareAsync();
        output.WriteLine($"index again (warm): {sw.ElapsedMilliseconds:N0} ms");

        sw.Restart();
        const int lookups = 200;
        for (int i = 0; i < lookups; i++) Assert.Null(resolver.Resolve($"cairn_not_there_{i}.tga"));
        double perMiss = sw.Elapsed.TotalMilliseconds / lookups;
        output.WriteLine($"missing texture lookup: {perMiss:F2} ms each");

        sw.Restart();
        int reads = resolver.ArchiveDirectoryReads.Reads;
        for (int i = 0; i < lookups; i++) _ = resolver.Resolve("L1S1.rfl");
        output.WriteLine($"found lookup: {sw.Elapsed.TotalMilliseconds / lookups:F3} ms each");
        Assert.Equal(reads, resolver.ArchiveDirectoryReads.Reads);
        Assert.True(perMiss < 50, $"a missing name costs {perMiss:F1} ms");
    }
}
