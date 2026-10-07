using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Editing;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>One row of the before → after table.</summary>
/// <param name="Change">The planned change this row shows.</param>
public sealed record BulkTimingRowViewModel(BulkTimingChange Change)
{
    /// <summary>The frame this row is about.</summary>
    public int Index => Change.FrameIndex;

    /// <summary>The effective time before, with a note when it was inherited.</summary>
    public string BeforeText => Format(Change.BeforeMs, Change.BeforeIsOverride);

    /// <summary>The effective time after, with a note when it will be inherited.</summary>
    public string AfterText => Format(Change.AfterMs, Change.AfterIsOverride);

    /// <summary>True when this row actually changes something, so the table can emphasise it.</summary>
    public bool Changed => Change.Changes;

    private static string Format(int milliseconds, bool isOverride) =>
        milliseconds.ToString(CultureInfo.CurrentCulture) + " ms" + (isOverride ? "" : " (inherited)");
}

/// <summary>
/// Frames &gt; Bulk Frame Timing…. Every number the dialog shows comes from
/// <see cref="BulkTiming.Plan"/>, which is pure, so the before → after table is the change itself
/// rather than a description of it — and Apply is that same plan turned into one
/// <see cref="Cairn.Atx.Text.TextEditBatch"/>, which is one Ctrl+Z.
/// </summary>
public sealed class BulkTimingViewModel : ObservableObject
{
    private readonly DocumentViewModel _document;

    private BulkTimingScope _scope;
    private BulkTimingOperation _operation = BulkTimingOperation.SetValue;
    private int _value = AtxSchema.DefaultFrameTimeMs;
    private double _percent = 100;
    private int _offset = 10;
    private int _totalMs = 1000;
    private int _fromMs = AtxSchema.DefaultFrameTimeMs;
    private int _toMs = AtxSchema.DefaultFrameTimeMs * 3;
    private int _rangeStart;
    private int _rangeEnd;
    private int _nth = 2;
    private int _nthOffset;
    private readonly int _planFrameCount;

    internal BulkTimingViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        // Every number below is read from the model, and a debounced parse may still be pending
        // from the keystroke that opened this dialog. Planning against the previous text would
        // build a plan whose frame indices no longer mean what they say.
        document.EnsureParsed();
        Selection = [.. document.Frames.SelectedIndices];
        _planFrameCount = document.FrameCount;
        // The design's default: when the user has already picked frames, act on those.
        _scope = Selection.Count >= 2 ? BulkTimingScope.Selected : BulkTimingScope.All;
        _rangeEnd = Math.Max(0, document.FrameCount - 1);
        _value = document.Model?.Header.EffectiveFrameTimeMs ?? AtxSchema.DefaultFrameTimeMs;
        _fromMs = _value;
        _toMs = _value * 3;
        // A plain sum, not the mode-aware cycle length: "distribute so the scope totals X ms"
        // is about the frames themselves, and a ping-pong's way back is not a frame to divide.
        _totalMs = Math.Max(1, SumFrameTimes(document));
        Refresh();
    }

    private static int SumFrameTimes(DocumentViewModel document)
    {
        var model = document.Model;
        if (model is null) return 0;
        int total = 0;
        for (int i = 0; i < model.Frames.Count; i++) total += model.FrameTimeMs(i);
        return total;
    }

    /// <summary>The frames selected in the list when the dialog opened.</summary>
    public IReadOnlyList<int> Selection { get; }

    /// <summary>The before → after table.</summary>
    public ObservableCollection<BulkTimingRowViewModel> Rows { get; } = [];

    /// <summary>Highest frame index, for the range spinners.</summary>
    public int MaxIndex => Math.Max(0, _document.FrameCount - 1);

    // ── Scope ─────────────────────────────────────────────────────────────────

    /// <summary>Which frames the operation touches.</summary>
    public BulkTimingScope Scope
    {
        get => _scope;
        set
        {
            if (!Set(ref _scope, value)) return;
            RaiseAll(nameof(IsScopeAll), nameof(IsScopeSelected), nameof(IsScopeRange),
                nameof(IsScopeEveryNth), nameof(ShowRange), nameof(ShowNth));
            Refresh();
        }
    }

    public bool IsScopeAll
    {
        get => _scope == BulkTimingScope.All;
        set { if (value) Scope = BulkTimingScope.All; }
    }

    public bool IsScopeSelected
    {
        get => _scope == BulkTimingScope.Selected;
        set { if (value) Scope = BulkTimingScope.Selected; }
    }

    public bool IsScopeRange
    {
        get => _scope == BulkTimingScope.Range;
        set { if (value) Scope = BulkTimingScope.Range; }
    }

    public bool IsScopeEveryNth
    {
        get => _scope == BulkTimingScope.EveryNth;
        set { if (value) Scope = BulkTimingScope.EveryNth; }
    }

    /// <summary>"Selected (4 frames)" — so the radio says what it will actually do.</summary>
    public string SelectedScopeLabel => Selection.Count switch
    {
        0 => "Selected (nothing selected)",
        1 => "Selected (1 frame)",
        _ => $"Selected ({Selection.Count} frames)",
    };

    /// <summary>True when the selection radio can be used at all.</summary>
    public bool HasSelection => Selection.Count > 0;

    /// <summary>True when the index-range spinners apply.</summary>
    public bool ShowRange => _scope == BulkTimingScope.Range;

    /// <summary>True when the every-Nth spinners apply.</summary>
    public bool ShowNth => _scope == BulkTimingScope.EveryNth;

    public int RangeStart
    {
        get => _rangeStart;
        set { if (Set(ref _rangeStart, Math.Clamp(value, 0, MaxIndex))) Refresh(); }
    }

    public int RangeEnd
    {
        get => _rangeEnd;
        set { if (Set(ref _rangeEnd, Math.Clamp(value, 0, MaxIndex))) Refresh(); }
    }

    public int Nth
    {
        get => _nth;
        set { if (Set(ref _nth, Math.Clamp(value, 1, 99))) Refresh(); }
    }

    public int NthOffset
    {
        get => _nthOffset;
        set { if (Set(ref _nthOffset, Math.Clamp(value, 0, Math.Max(0, MaxIndex)))) Refresh(); }
    }

    // ── Operation ─────────────────────────────────────────────────────────────

    /// <summary>The operations offered, in the order the combo lists them.</summary>
    public static IReadOnlyList<(BulkTimingOperation Operation, string Label, string Help)> Operations { get; } =
    [
        (BulkTimingOperation.SetValue, "Set to value",
            "Give every frame in scope the same frame_time."),
        (BulkTimingOperation.ClearOverride, "Clear override",
            "Remove frame_time from each frame so it inherits the texture's frame time."),
        (BulkTimingOperation.ScalePercent, "Scale by percent",
            "Multiply each frame's current effective time. 50 % runs twice as fast."),
        (BulkTimingOperation.OffsetMs, "Add or subtract ms",
            "Shift each frame's current effective time. Negative numbers subtract."),
        (BulkTimingOperation.DistributeTotal, "Distribute a total",
            "Spread one duration evenly across the scope, to the millisecond."),
        (BulkTimingOperation.RampLinear, "Ramp (linear)",
            "Step evenly from the first value to the last across the scope."),
        (BulkTimingOperation.RampEaseIn, "Ramp (ease in)",
            "Ramp that starts slowly, so the animation appears to accelerate."),
        (BulkTimingOperation.RampEaseOut, "Ramp (ease out)",
            "Ramp that ends slowly, so the animation appears to settle."),
    ];

    /// <summary>What the operation does to each frame in scope.</summary>
    public BulkTimingOperation Operation
    {
        get => _operation;
        set
        {
            if (!Set(ref _operation, value)) return;
            RaiseAll(nameof(ShowValue), nameof(ShowPercent), nameof(ShowOffset),
                nameof(ShowTotal), nameof(ShowRamp), nameof(OperationHelp));
            Refresh();
        }
    }

    /// <summary>The one-line explanation under the operation picker.</summary>
    public string OperationHelp =>
        Operations.FirstOrDefault(o => o.Operation == _operation).Help ?? string.Empty;

    public bool ShowValue => _operation == BulkTimingOperation.SetValue;

    public bool ShowPercent => _operation == BulkTimingOperation.ScalePercent;

    public bool ShowOffset => _operation == BulkTimingOperation.OffsetMs;

    public bool ShowTotal => _operation == BulkTimingOperation.DistributeTotal;

    public bool ShowRamp => _operation is BulkTimingOperation.RampLinear
        or BulkTimingOperation.RampEaseIn or BulkTimingOperation.RampEaseOut;

    public int Value
    {
        get => _value;
        set { if (Set(ref _value, Math.Clamp(value, AtxSchema.MinFrameTimeMs, 600000))) Refresh(); }
    }

    public double Percent
    {
        get => _percent;
        set { if (Set(ref _percent, Math.Clamp(value, 1, 10000))) Refresh(); }
    }

    /// <summary>The percent as an integer, which is all the spinner needs.</summary>
    public int PercentValue
    {
        get => (int)Math.Round(_percent);
        set => Percent = value;
    }

    public int Offset
    {
        get => _offset;
        set { if (Set(ref _offset, Math.Clamp(value, -600000, 600000))) Refresh(); }
    }

    public int TotalMs
    {
        get => _totalMs;
        set { if (Set(ref _totalMs, Math.Clamp(value, 1, 3600000))) Refresh(); }
    }

    public int FromMs
    {
        get => _fromMs;
        set { if (Set(ref _fromMs, Math.Clamp(value, AtxSchema.MinFrameTimeMs, 600000))) Refresh(); }
    }

    public int ToMs
    {
        get => _toMs;
        set { if (Set(ref _toMs, Math.Clamp(value, AtxSchema.MinFrameTimeMs, 600000))) Refresh(); }
    }

    // ── The plan ──────────────────────────────────────────────────────────────

    private IReadOnlyList<BulkTimingChange> _changes = [];

    /// <summary>"6 of 12 frames · 800 ms → 1.20 s" under the table.</summary>
    public string SummaryText { get; private set; } = string.Empty;

    /// <summary>An inline note about clamping or an impossible request, or null.</summary>
    public string? ValidationText { get; private set; }

    /// <summary>True when the note is a problem rather than a courtesy.</summary>
    public bool ValidationIsError { get; private set; }

    /// <summary>False when Apply would do nothing.</summary>
    public bool CanApply => _changes.Any(c => c.Changes);

    private BulkTimingRequest BuildRequest() => new()
    {
        Scope = _scope,
        Operation = _operation,
        Value = _value,
        Percent = _percent,
        Offset = _offset,
        TotalMs = _totalMs,
        FromMs = _fromMs,
        ToMs = _toMs,
        RangeStart = _rangeStart,
        RangeEnd = _rangeEnd,
        Nth = _nth,
        NthOffset = _nthOffset,
    };

    private void Refresh()
    {
        var model = _document.Model;
        if (model is null)
        {
            _changes = [];
            Rows.Clear();
            SummaryText = string.Empty;
            ValidationText = "This file has a syntax error, so its frames cannot be edited.";
            ValidationIsError = true;
            RaiseAll(nameof(SummaryText), nameof(ValidationText), nameof(ValidationIsError),
                nameof(CanApply), nameof(HasValidation));
            return;
        }

        var request = BuildRequest();
        _changes = BulkTiming.Plan(model, request, Selection);

        Rows.Clear();
        foreach (var change in _changes) Rows.Add(new BulkTimingRowViewModel(change));

        int beforeTotal = 0, afterTotal = 0;
        for (int i = 0; i < model.Frames.Count; i++) beforeTotal += model.FrameTimeMs(i);
        afterTotal = beforeTotal;
        foreach (var change in _changes) afterTotal += change.AfterMs - change.BeforeMs;

        int touched = _changes.Count(c => c.Changes);
        SummaryText = _changes.Count == 0
            ? "No frames are in scope."
            : string.Format(
                CultureInfo.CurrentCulture,
                "{0} frame{1} in scope, {2} changing · loop {3} → {4}",
                _changes.Count, _changes.Count == 1 ? "" : "s", touched,
                DocumentViewModel.FormatDuration(beforeTotal),
                DocumentViewModel.FormatDuration(afterTotal));

        (ValidationText, ValidationIsError) = Validate(request, model.Frames.Count);

        RaiseAll(nameof(SummaryText), nameof(ValidationText), nameof(ValidationIsError),
            nameof(CanApply), nameof(HasValidation), nameof(MaxIndex));
    }

    /// <summary>True when there is a note to show.</summary>
    public bool HasValidation => !string.IsNullOrEmpty(ValidationText);

    private (string?, bool) Validate(BulkTimingRequest request, int frameCount)
    {
        if (frameCount == 0) return ("This file has no frames yet.", true);
        if (_changes.Count == 0)
        {
            return _scope switch
            {
                BulkTimingScope.Selected => ("Nothing is selected in the frames list.", true),
                BulkTimingScope.EveryNth =>
                    ($"Every {_nth} frames from index {_nthOffset} picks no frame in a {frameCount}-frame file.", true),
                _ => ("No frames are in scope.", true),
            };
        }

        if (request.Operation == BulkTimingOperation.DistributeTotal
            && request.TotalMs < _changes.Count * AtxSchema.MinFrameTimeMs)
        {
            return ($"{request.TotalMs} ms cannot be spread over {_changes.Count} frames: "
                + $"each frame needs at least {AtxSchema.MinFrameTimeMs} ms, so the total is raised to "
                + $"{_changes.Count} ms.", false);
        }

        if (_changes.Any(c => c.AfterIsOverride && c.AfterMs <= AtxSchema.MinFrameTimeMs)
            && request.Operation is BulkTimingOperation.ScalePercent or BulkTimingOperation.OffsetMs)
        {
            return ("Some frames would drop below 1 ms, which the game clamps to 1 ms. "
                + "They are shown clamped in the table.", false);
        }

        if (!CanApply) return ("These settings leave every frame exactly as it is.", false);
        return (null, false);
    }

    /// <summary>
    /// Writes the plan into the document as one undo step.
    ///
    /// The plan is rebuilt here rather than reused: the cached one was computed when the table last
    /// refreshed, and between then and the OK click a debounced reparse or a watcher-driven reload
    /// can have changed the file underneath the dialog. Applying a stale plan writes frame times
    /// into the wrong frames. When the file has changed shape entirely, nothing is written and the
    /// dialog says why.
    /// </summary>
    /// <returns>True when the document was actually changed.</returns>
    public bool Apply()
    {
        _document.EnsureParsed();
        var model = _document.Model;
        if (model is null || model.Frames.Count != _planFrameCount)
        {
            ValidationText = "This file changed while the dialog was open, so nothing was applied. "
                + "Close this and try again.";
            ValidationIsError = true;
            RaiseAll(nameof(ValidationText), nameof(ValidationIsError), nameof(HasValidation));
            return false;
        }

        var changes = BulkTiming.Plan(model, BuildRequest(), Selection);
        if (!changes.Any(c => c.Changes)) return false;
        return _document.Edit(editor => BulkTiming.Apply(editor, changes));
    }
}
