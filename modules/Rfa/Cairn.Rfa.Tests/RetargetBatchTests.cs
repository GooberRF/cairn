using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Tests;

public class RetargetBatchTests
{
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static readonly RetargetRig Source = SyntheticRig.Rig("srcx-bdbn-");

    private static readonly RetargetRig Target = MakeTarget();

    private static RetargetRig MakeTarget()
    {
        var rig = SyntheticRig.Rig("tgtx-bdbn-", reversed: true, twistFrames: true, scale: 1.1f);
        return rig with { Profile = rig.Profile with { Name = "tgt" } };
    }

    private static BatchItem Item(string name, bool asBytes = true)
    {
        var clip = SyntheticRig.Motion(Source);
        return asBytes
            ? new BatchItem(name, Source, Target) { SourceBytes = RfaWriter.Write(clip) }
            : new BatchItem(name, Source, Target) { SourceClip = clip };
    }

    [Fact]
    public async Task WritesNamedOutputsAndReportsProgress()
    {
        using var temp = new TempFolder();
        var seen = new List<BatchProgress>();
        var items = new[] { Item("srcx_walk.rfa"), Item("srcx_run.rfa", asBytes: false), Item("plain.rfa") with { ClipName = "custom" } };
        var options = new BatchOptions { OutputFolder = temp.Path, NamingPattern = "af_{rig}_{clip}" };

        var results = await BatchRetarget.RunAsync(items, options, new SyncProgress<BatchProgress>(seen.Add));

        Assert.Equal(["af_tgt_walk.rfa", "af_tgt_run.rfa", "af_tgt_custom.rfa"], results.Select(r => r.OutputName));
        Assert.All(results, r =>
        {
            Assert.Equal(BatchItemStatus.Succeeded, r.Status);
            Assert.True(File.Exists(r.OutputPath));
            Assert.Equal(r.OutputBytes.ToArray(), File.ReadAllBytes(r.OutputPath!));
            Assert.True(r.Report!.StructureOk);
            Assert.Equal(SyntheticRig.BoneCount, RfaReader.ReadFile(r.OutputPath!).BoneCount);
        });
        Assert.Equal(6, seen.Count);
        Assert.Equal((3, 3), (seen[^1].Completed, seen[^1].Total));
        Assert.NotNull(seen[^1].Last);
        Assert.Null(seen[0].Last);
        Assert.Equal("af_merc_on_turret.rfa", BatchRetarget.OutputName(
            new BatchItem("ult2_on_turret.rfa", Source, Target with { Profile = RigProfiles.Merc }), "af_{rig}_{clip}.rfa"));
        Assert.Equal("park-jeep", BatchRetarget.ClipToken("park-jeep"));
    }

    [Fact]
    public async Task CollisionsAskTheCallback()
    {
        using var temp = new TempFolder();
        string existing = temp.Write("af_tgt_walk.rfa", [1, 2, 3]);
        var asked = new List<string>();
        var decision = CollisionDecision.Skip;
        var options = new BatchOptions
        {
            OutputFolder = temp.Path,
            OnCollision = path =>
            {
                asked.Add(path);
                return decision;
            },
        };

        var skipped = (await BatchRetarget.RunAsync([Item("srcx_walk.rfa")], options)).Single();
        Assert.Equal(BatchItemStatus.Skipped, skipped.Status);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(existing));
        Assert.Equal(Path.Combine(temp.Path, "af_tgt_walk.rfa"), Assert.Single(asked));

        decision = CollisionDecision.Rename;
        var renamed = (await BatchRetarget.RunAsync([Item("srcx_walk.rfa")], options)).Single();
        Assert.Equal("af_tgt_walk_2.rfa", renamed.OutputName);
        Assert.True(File.Exists(temp.File("af_tgt_walk_2.rfa")));

        decision = CollisionDecision.Overwrite;
        var replaced = (await BatchRetarget.RunAsync([Item("srcx_walk.rfa")], options)).Single();
        Assert.Equal(BatchItemStatus.Succeeded, replaced.Status);
        Assert.Equal(replaced.OutputBytes.ToArray(), File.ReadAllBytes(existing));

        decision = CollisionDecision.Cancel;
        var stopped = await BatchRetarget.RunAsync([Item("srcx_walk.rfa"), Item("srcx_run.rfa")], options);
        Assert.Equal([BatchItemStatus.Cancelled, BatchItemStatus.Cancelled], stopped.Select(r => r.Status));
        Assert.False(File.Exists(temp.File("af_tgt_run.rfa")));

        // No callback: never overwrite.
        var quiet = (await BatchRetarget.RunAsync([Item("srcx_walk.rfa")], options with { OnCollision = null })).Single();
        Assert.Equal(BatchItemStatus.Skipped, quiet.Status);
    }

    [Fact]
    public async Task InMemoryBatchesTouchNoFilesButStillSeeNameClashes()
    {
        using var temp = new TempFolder();
        int asked = 0;
        var options = new BatchOptions
        {
            OutputFolder = temp.Path,
            WriteFiles = false,
            OnCollision = _ =>
            {
                asked++;
                return CollisionDecision.Rename;
            },
        };
        var results = await BatchRetarget.RunAsync([Item("srcx_walk.rfa"), Item("other_walk.rfa")], options);
        Assert.Equal(["af_tgt_walk.rfa", "af_tgt_walk_2.rfa"], results.Select(r => r.OutputName));
        Assert.All(results, r => Assert.NotEmpty(r.OutputBytes));
        Assert.Equal(1, asked);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));

        var none = await BatchRetarget.RunAsync([Item("srcx_walk.rfa")], new BatchOptions { WriteFiles = false });
        Assert.True(none.Single().Success);
        await Assert.ThrowsAsync<ArgumentException>(() => BatchRetarget.RunAsync([Item("x.rfa")], new BatchOptions()));
    }

    [Fact]
    public async Task CancellationStopsBetweenItemsAndReportsTheRest()
    {
        using var temp = new TempFolder();
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<BatchProgress>(p =>
        {
            if (p.Completed == 1) cts.Cancel();
        });
        var items = Enumerable.Range(0, 4).Select(i => Item($"srcx_clip{i}.rfa")).ToArray();
        var results = await BatchRetarget.RunAsync(items, new BatchOptions { OutputFolder = temp.Path }, progress, cts.Token);
        Assert.Equal(4, results.Count);
        Assert.Equal(BatchItemStatus.Succeeded, results[0].Status);
        Assert.All(results.Skip(1), r => Assert.Equal(BatchItemStatus.Cancelled, r.Status));
        Assert.Single(Directory.EnumerateFiles(temp.Path));
    }

    [Fact]
    public async Task BadItemsFailWithReadableErrors()
    {
        var garbage = new BatchItem("broken.rfa", Source, Target) { SourceBytes = new byte[] { 1, 2, 3 } };
        var empty = new BatchItem("empty.rfa", Source, Target);
        var wrong = new BatchItem("wrong.rfa", Target, Source) { SourceClip = SyntheticRig.Motion(Source) with { Bones = SyntheticRig.Motion(Source).Bones.RemoveAt(0) } };
        var results = await BatchRetarget.RunAsync([garbage, empty, wrong, Item("srcx_ok.rfa")], new BatchOptions { WriteFiles = false });
        Assert.Equal([BatchItemStatus.Failed, BatchItemStatus.Failed, BatchItemStatus.Failed, BatchItemStatus.Succeeded], results.Select(r => r.Status));
        Assert.StartsWith("'broken.rfa' could not be read as a clip", results[0].Error);
        Assert.Contains("no clip data", results[1].Error);
        Assert.Contains("bones but the source mesh has", results[2].Error);
        Assert.NotNull(results[2].Result);
    }
}
