using Cairn.Atx.Editing;
using Cairn.Atx.Parsing;

namespace Cairn.Atx.Tests;

public class BulkTimingTests
{
    private const string Document = """
        [header]
        frame_time = 100

        [[frame]]
        file = "a.tga"

        [[frame]]
        file = "b.tga"
        frame_time = 200

        [[frame]]
        file = "c.tga"

        [[frame]]
        file = "d.tga"

        """;

    private static Cairn.Atx.Model.AtxModel Model => AtxParser.Parse(Document).Model!;

    private static int[] After(BulkTimingRequest request, IReadOnlyList<int>? selection = null) =>
        [.. BulkTiming.Plan(Model, request, selection).Select(c => c.AfterMs)];

    [Fact]
    public void ScopeAllCoversEveryFrame()
    {
        var scope = BulkTiming.ResolveScope(Model, new BulkTimingRequest());
        Assert.Equal([0, 1, 2, 3], scope);
    }

    [Fact]
    public void ScopeSelectedUsesTheSelection()
    {
        var request = new BulkTimingRequest { Scope = BulkTimingScope.Selected };
        Assert.Equal([1, 3], BulkTiming.ResolveScope(Model, request, [3, 1, 3, 99]));
    }

    [Fact]
    public void ScopeRangeIsInclusiveAndOrderAgnostic()
    {
        var request = new BulkTimingRequest { Scope = BulkTimingScope.Range, RangeStart = 2, RangeEnd = 1 };
        Assert.Equal([1, 2], BulkTiming.ResolveScope(Model, request));
    }

    [Fact]
    public void ScopeEveryNthStartsFromTheOffset()
    {
        var request = new BulkTimingRequest { Scope = BulkTimingScope.EveryNth, Nth = 2, NthOffset = 1 };
        Assert.Equal([1, 3], BulkTiming.ResolveScope(Model, request));
    }

    [Fact]
    public void BeforeValuesShowInheritanceAndOverrides()
    {
        var plan = BulkTiming.Plan(Model, new BulkTimingRequest { Value = 50 });
        Assert.Equal([100, 200, 100, 100], plan.Select(c => c.BeforeMs));
        Assert.Equal([false, true, false, false], plan.Select(c => c.BeforeIsOverride));
        Assert.All(plan, c => Assert.True(c.Changes));
    }

    [Fact]
    public void SetValueClampsToOne()
    {
        Assert.Equal([50, 50, 50, 50], After(new BulkTimingRequest { Value = 50 }));
        Assert.Equal([1, 1, 1, 1], After(new BulkTimingRequest { Value = 0 }));
    }

    [Fact]
    public void ClearOverrideReturnsFramesToTheTextureTime()
    {
        var plan = BulkTiming.Plan(Model,
            new BulkTimingRequest { Operation = BulkTimingOperation.ClearOverride });
        Assert.All(plan, c => Assert.False(c.AfterIsOverride));
        Assert.All(plan, c => Assert.Equal(100, c.AfterMs));
        Assert.True(plan[1].Changes);
        Assert.False(plan[0].Changes);
    }

    [Fact]
    public void ScaleByPercentRoundsAwayFromZeroAndClamps()
    {
        Assert.Equal([50, 100, 50, 50],
            After(new BulkTimingRequest { Operation = BulkTimingOperation.ScalePercent, Percent = 50 }));
        Assert.Equal([1, 2, 1, 1],
            After(new BulkTimingRequest { Operation = BulkTimingOperation.ScalePercent, Percent = 1 }));
    }

    [Fact]
    public void OffsetAddsOrSubtractsMilliseconds()
    {
        Assert.Equal([130, 230, 130, 130],
            After(new BulkTimingRequest { Operation = BulkTimingOperation.OffsetMs, Offset = 30 }));
        Assert.Equal([1, 100, 1, 1],
            After(new BulkTimingRequest { Operation = BulkTimingOperation.OffsetMs, Offset = -100 }));
    }

    [Fact]
    public void DistributeTotalSplitsExactlyWithTheRemainderAtTheFront()
    {
        var values = After(new BulkTimingRequest
        {
            Operation = BulkTimingOperation.DistributeTotal,
            TotalMs = 1002,
        });
        Assert.Equal([251, 251, 250, 250], values);
        Assert.Equal(1002, values.Sum());
    }

    [Fact]
    public void RampsInterpolateBetweenTheEndpoints()
    {
        var linear = After(new BulkTimingRequest
        {
            Operation = BulkTimingOperation.RampLinear,
            FromMs = 100,
            ToMs = 400,
        });
        Assert.Equal([100, 200, 300, 400], linear);

        var easeIn = After(new BulkTimingRequest
        {
            Operation = BulkTimingOperation.RampEaseIn,
            FromMs = 0,
            ToMs = 900,
        });
        Assert.Equal([1, 100, 400, 900], easeIn);

        var easeOut = After(new BulkTimingRequest
        {
            Operation = BulkTimingOperation.RampEaseOut,
            FromMs = 0,
            ToMs = 900,
        });
        Assert.Equal([1, 500, 800, 900], easeOut);
    }

    [Fact]
    public void ApplyingAPlanIsOneBatchThatReparsesAsIntended()
    {
        var editor = AtxEditor.Create(Document);
        var batch = BulkTiming.Apply(editor, new BulkTimingRequest
        {
            Operation = BulkTimingOperation.RampLinear,
            FromMs = 100,
            ToMs = 400,
        });
        var model = AtxParser.Parse(batch.Apply(Document)).Model!;
        Assert.Equal([100, 200, 300, 400], model.Frames.Select(f => f.FrameTimeOverrideMs));
    }

    [Fact]
    public void ClearingOverridesRemovesTheLines()
    {
        var editor = AtxEditor.Create(Document);
        var batch = BulkTiming.Apply(editor,
            new BulkTimingRequest { Operation = BulkTimingOperation.ClearOverride });
        string result = batch.Apply(Document);
        Assert.DoesNotContain("frame_time = 200", result, StringComparison.Ordinal);
        var model = AtxParser.Parse(result).Model!;
        Assert.All(model.Frames, f => Assert.Null(f.FrameTimeOverrideMs));
        Assert.Equal(100, model.Header.EffectiveFrameTimeMs);
    }

    [Fact]
    public void AnEmptyScopePlansNothing()
    {
        var request = new BulkTimingRequest { Scope = BulkTimingScope.Selected };
        Assert.Empty(BulkTiming.Plan(Model, request, []));
        Assert.True(BulkTiming.Apply(AtxEditor.Create(Document), request, []).IsEmpty);
    }
}
