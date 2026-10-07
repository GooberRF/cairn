using Cairn.Atx.Model;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;

namespace Cairn.Atx.Editing;

/// <summary>Which frames a bulk timing operation touches.</summary>
public enum BulkTimingScope
{
    /// <summary>Every frame.</summary>
    All,
    /// <summary>The frames currently selected in the list.</summary>
    Selected,
    /// <summary>An inclusive index range.</summary>
    Range,
    /// <summary>Every Nth frame, starting from an offset.</summary>
    EveryNth,
}

/// <summary>What a bulk timing operation does to each frame in scope.</summary>
public enum BulkTimingOperation
{
    /// <summary>Give every frame in scope the same time.</summary>
    SetValue,
    /// <summary>Remove the per-frame override so the frame inherits the texture's time.</summary>
    ClearOverride,
    /// <summary>Multiply the current effective time by a percentage.</summary>
    ScalePercent,
    /// <summary>Add (or, with a negative value, subtract) milliseconds.</summary>
    OffsetMs,
    /// <summary>Spread a total duration evenly across the scope.</summary>
    DistributeTotal,
    /// <summary>Ramp evenly from one time to another.</summary>
    RampLinear,
    /// <summary>Ramp from one time to another, starting slowly.</summary>
    RampEaseIn,
    /// <summary>Ramp from one time to another, ending slowly.</summary>
    RampEaseOut,
}

/// <summary>The settings of one Bulk Frame Timing run.</summary>
public sealed record BulkTimingRequest
{
    public BulkTimingScope Scope { get; init; } = BulkTimingScope.All;

    public BulkTimingOperation Operation { get; init; } = BulkTimingOperation.SetValue;

    /// <summary>Milliseconds for <see cref="BulkTimingOperation.SetValue"/>.</summary>
    public int Value { get; init; } = AtxSchema.DefaultFrameTimeMs;

    /// <summary>Percentage for <see cref="BulkTimingOperation.ScalePercent"/>; 100 means no change.</summary>
    public double Percent { get; init; } = 100;

    /// <summary>Milliseconds added by <see cref="BulkTimingOperation.OffsetMs"/>; may be negative.</summary>
    public int Offset { get; init; }

    /// <summary>Total milliseconds for <see cref="BulkTimingOperation.DistributeTotal"/>.</summary>
    public int TotalMs { get; init; } = 1000;

    /// <summary>First value of a ramp, in milliseconds.</summary>
    public int FromMs { get; init; } = AtxSchema.DefaultFrameTimeMs;

    /// <summary>Last value of a ramp, in milliseconds.</summary>
    public int ToMs { get; init; } = AtxSchema.DefaultFrameTimeMs;

    /// <summary>First index of <see cref="BulkTimingScope.Range"/>, inclusive.</summary>
    public int RangeStart { get; init; }

    /// <summary>Last index of <see cref="BulkTimingScope.Range"/>, inclusive.</summary>
    public int RangeEnd { get; init; }

    /// <summary>The N of <see cref="BulkTimingScope.EveryNth"/>.</summary>
    public int Nth { get; init; } = 2;

    /// <summary>Where <see cref="BulkTimingScope.EveryNth"/> starts counting.</summary>
    public int NthOffset { get; init; }
}

/// <summary>What one frame's timing becomes, for the live before/after preview table.</summary>
/// <param name="FrameIndex">The frame this row is about.</param>
/// <param name="BeforeMs">The effective time before the change.</param>
/// <param name="BeforeIsOverride">Whether that time came from a per-frame override.</param>
/// <param name="AfterMs">The effective time after the change.</param>
/// <param name="AfterIsOverride">False when the frame will inherit the texture's time.</param>
public sealed record BulkTimingChange(
    int FrameIndex, int BeforeMs, bool BeforeIsOverride, int AfterMs, bool AfterIsOverride)
{
    /// <summary>True when this row actually changes anything.</summary>
    public bool Changes => BeforeMs != AfterMs || BeforeIsOverride != AfterIsOverride;
}

/// <summary>
/// The arithmetic behind Frames > Bulk Frame Timing. Planning is pure, so the dialog can show a
/// before/after table before anything is written, and applying is one <see cref="TextEditBatch"/>
/// and therefore one undo step.
/// </summary>
public static class BulkTiming
{
    /// <summary>The frame indices a request covers, in ascending order.</summary>
    public static IReadOnlyList<int> ResolveScope(
        AtxModel model, BulkTimingRequest request, IReadOnlyList<int>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        int count = model.Frames.Count;

        switch (request.Scope)
        {
            case BulkTimingScope.Selected:
                return [.. (selection ?? []).Distinct().Where(i => i >= 0 && i < count).OrderBy(i => i)];
            case BulkTimingScope.Range:
            {
                int start = Math.Clamp(Math.Min(request.RangeStart, request.RangeEnd), 0, Math.Max(0, count - 1));
                int end = Math.Clamp(Math.Max(request.RangeStart, request.RangeEnd), 0, Math.Max(0, count - 1));
                return count == 0 ? [] : [.. Enumerable.Range(start, end - start + 1)];
            }
            case BulkTimingScope.EveryNth:
            {
                int n = Math.Max(1, request.Nth);
                int offset = Math.Max(0, request.NthOffset);
                var picked = new List<int>();
                for (int i = offset; i < count; i += n) picked.Add(i);
                return picked;
            }
            default:
                return [.. Enumerable.Range(0, count)];
        }
    }

    /// <summary>
    /// Works out the new time for every frame in scope without touching the document.
    /// </summary>
    public static IReadOnlyList<BulkTimingChange> Plan(
        AtxModel model, BulkTimingRequest request, IReadOnlyList<int>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(request);
        var scope = ResolveScope(model, request, selection);
        if (scope.Count == 0) return [];

        int inherited = model.Header.EffectiveFrameTimeMs;
        var changes = new List<BulkTimingChange>(scope.Count);
        int[]? distributed = request.Operation == BulkTimingOperation.DistributeTotal
            ? Distribute(request.TotalMs, scope.Count)
            : null;

        for (int position = 0; position < scope.Count; position++)
        {
            int index = scope[position];
            var frame = model.Frames[index];
            int before = frame.FrameTimeOverrideMs ?? inherited;
            bool beforeOverride = frame.FrameTime is not null;

            int after;
            bool afterOverride = true;
            switch (request.Operation)
            {
                case BulkTimingOperation.ClearOverride:
                    after = inherited;
                    afterOverride = false;
                    break;
                case BulkTimingOperation.SetValue:
                    after = request.Value;
                    break;
                case BulkTimingOperation.ScalePercent:
                    after = ToFrameTime(before * request.Percent / 100.0);
                    break;
                case BulkTimingOperation.OffsetMs:
                    // 64-bit: "add 2,000,000,000 ms" to a 5,000 ms frame must saturate at the
                    // largest time a frame can hold, not wrap round to 1 ms.
                    after = ToFrameTime((long)before + request.Offset);
                    break;
                case BulkTimingOperation.DistributeTotal:
                    after = distributed![position];
                    break;
                default:
                    after = Ramp(request, position, scope.Count);
                    break;
            }

            after = Math.Max(AtxSchema.MinFrameTimeMs, after);
            changes.Add(new BulkTimingChange(index, before, beforeOverride, after, afterOverride));
        }
        return changes;
    }

    /// <summary>Turns a plan into a single edit batch.</summary>
    public static TextEditBatch Apply(AtxEditor editor, IReadOnlyList<BulkTimingChange> changes)
    {
        ArgumentNullException.ThrowIfNull(editor);
        ArgumentNullException.ThrowIfNull(changes);
        var values = new Dictionary<int, AtxValue?>();
        foreach (var change in changes)
        {
            values[change.FrameIndex] = change.AfterIsOverride
                ? AtxValue.Integer(change.AfterMs)
                : null;
        }
        return editor.SetFrameValues(AtxSchema.KeyFrameTime, values);
    }

    /// <summary>Plans and applies in one call.</summary>
    public static TextEditBatch Apply(
        AtxEditor editor, BulkTimingRequest request, IReadOnlyList<int>? selection = null)
    {
        ArgumentNullException.ThrowIfNull(editor);
        var model = editor.Model;
        return model is null ? TextEditBatch.Empty : Apply(editor, Plan(model, request, selection));
    }

    /// <summary>
    /// Clamps arithmetic that can leave the range of a frame time into it, so an overflowing
    /// scale or offset saturates at the biggest time a frame can carry instead of wrapping.
    /// A value that is not a number at all (a percentage of NaN) falls back to the minimum.
    /// </summary>
    private static int ToFrameTime(long value) =>
        (int)Math.Clamp(value, AtxSchema.MinFrameTimeMs, int.MaxValue);

    private static int ToFrameTime(double value)
    {
        if (double.IsNaN(value)) return AtxSchema.MinFrameTimeMs;
        double rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (rounded <= AtxSchema.MinFrameTimeMs) return AtxSchema.MinFrameTimeMs;
        return rounded >= int.MaxValue ? int.MaxValue : (int)rounded;
    }

    /// <summary>Splits <paramref name="total"/> across <paramref name="count"/> frames exactly.</summary>
    private static int[] Distribute(int total, int count)
    {
        total = Math.Max(count * AtxSchema.MinFrameTimeMs, total);
        int each = total / count;
        int remainder = total - each * count;
        var result = new int[count];
        for (int i = 0; i < count; i++) result[i] = each + (i < remainder ? 1 : 0);
        return result;
    }

    private static int Ramp(BulkTimingRequest request, int position, int count)
    {
        double t = count <= 1 ? 0 : (double)position / (count - 1);
        double eased = request.Operation switch
        {
            BulkTimingOperation.RampEaseIn => t * t,
            BulkTimingOperation.RampEaseOut => 1 - (1 - t) * (1 - t),
            _ => t,
        };
        return ToFrameTime(request.FromMs + ((double)request.ToMs - request.FromMs) * eased);
    }
}
