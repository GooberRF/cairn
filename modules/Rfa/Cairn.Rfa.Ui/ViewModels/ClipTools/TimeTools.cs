using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>Clip › Trim / Crop to Range: <see cref="ClipEdit.Trim"/>.</summary>
public sealed class TrimToolViewModel : ClipToolViewModel
{
    private double _from;
    private double _to;

    public TrimToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        var span = document.KeySelection.TimeSpan(Original);
        bool useKeys = span is { } s && s.Max > s.Min;
        (int a, int b) = useKeys ? span!.Value : (Original.StartTime, Original.EndTime);
        _from = ToUnit(a);
        _to = ToUnit(b);
        DefaultText = useKeys ? "From and To start at the selected keys' span." : "From and To start at the clip's range.";
        UseClipRangeCommand = new RelayCommand(() => { From = ToUnit(Original.StartTime); To = ToUnit(Original.EndTime); });
        UseKeySpanCommand = new RelayCommand(() =>
        {
            if (document.KeySelection.TimeSpan(Original) is { } k) { From = ToUnit(k.Min); To = ToUnit(k.Max); }
        }, () => document.KeySelection.TimeSpan(Original) is { } k && k.Max > k.Min);
        FromPlayheadCommand = new RelayCommand(() => From = ToUnit(Math.Round(Playback.Time)));
        ToPlayheadCommand = new RelayCommand(() => To = ToUnit(Math.Round(Playback.Time)));
        Start();
    }

    public override string ToolId => "trim";

    public override string Title => "Trim / Crop to Range";

    public override string Heading => "Trim the clip to a range";

    public override string Description =>
        "Keeps only the motion between From and To. Keys outside are dropped and a key is added at each end where the motion "
        + "carries on beyond it, so what stays plays exactly as before. Start and end become the range.";

    /// <summary>Where the defaults came from.</summary>
    public string DefaultText { get; }

    /// <summary>Range start in the display unit.</summary>
    public double From
    {
        get => _from;
        set => SetParameter(ref _from, value);
    }

    /// <summary>Range end in the display unit.</summary>
    public double To
    {
        get => _to;
        set => SetParameter(ref _to, value);
    }

    public RelayCommand UseClipRangeCommand { get; }

    public RelayCommand UseKeySpanCommand { get; }

    public RelayCommand FromPlayheadCommand { get; }

    public RelayCommand ToPlayheadCommand { get; }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        int from = ToTicks(_from), to = ToTicks(_to);
        var clip = Original;
        var unit = Unit;
        string label = $"Trim to {Num(from)}–{Num(to)}";
        return Work(_ =>
        {
            var result = ClipEdit.Trim(clip, from, to);
            var diff = KeyDiff.Of(clip, result);
            var lines = new List<string> { ClipToolSummary.RangeLine(clip, result, unit), diff.KeysLine() };
            if (result.RampIn != clip.RampIn || result.RampOut != clip.RampOut)
                lines.Add($"Ramps clamped to the new duration: in {TimeFormat.Format(clip.RampIn, unit)} → {TimeFormat.Format(result.RampIn, unit)}, out {TimeFormat.Format(clip.RampOut, unit)} → {TimeFormat.Format(result.RampOut, unit)}");
            return new ClipToolResult(result, label, lines);
        });
    }
}

/// <summary>Clip › Shift in Time: <see cref="ClipEdit.Shift"/>.</summary>
public sealed class ShiftToolViewModel : ClipToolViewModel
{
    private double _delta;

    public ShiftToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        StartAtFirstFrameCommand = new RelayCommand(() => Delta = ToUnit(RfaClip.TicksPerFrame - Original.StartTime));
        Start();
    }

    public override string ToolId => "shift";

    public override string Title => "Shift in Time";

    public override string Heading => "Move the whole clip in time";

    public override string Description =>
        "Adds the same amount to every key time and to start and end. The motion and its length are unchanged; "
        + "a negative amount moves the clip earlier.";

    /// <summary>How far to move, in the display unit.</summary>
    public double Delta
    {
        get => _delta;
        set => SetParameter(ref _delta, value);
    }

    /// <summary>"Start at 1 f" (stock clips start at tick 160).</summary>
    public string StartAtFirstFrameText => $"Start at {Time(RfaClip.TicksPerFrame)}";

    public RelayCommand StartAtFirstFrameCommand { get; }

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        int delta = ToTicks(_delta);
        if (delta == 0)
        {
            var nothing = Nothing("Enter how far to move the clip (a negative amount moves it earlier).");
            return Work(_ => nothing);
        }
        var clip = Original;
        var unit = Unit;
        string label = $"Shift by {(delta > 0 ? "+" : "")}{Num(delta)} {UnitSuffix}";
        string moved = Time(delta);
        return Work(_ =>
        {
            var result = ClipEdit.Shift(clip, delta);
            return new ClipToolResult(result, label,
            [
                ClipToolSummary.RangeLine(clip, result, unit),
                $"All {KeyDiff.KeyCount(clip):N0} keys move by {moved}; values and ramps are unchanged.",
            ]);
        });
    }
}

/// <summary>Where <see cref="RetimeToolViewModel"/> scales about.</summary>
public enum RetimePivot
{
    Start,
    Playhead,
    End,
}

/// <summary>Clip › Retime: <see cref="ClipEdit.Retime"/> by a factor or to a new length.</summary>
public sealed class RetimeToolViewModel : ClipToolViewModel
{
    private bool _byFactor = true;
    private double _percent = 100;
    private double _length;
    private RetimePivot _pivot = RetimePivot.Start;

    public RetimeToolViewModel(ClipDocumentViewModel document) : base(document)
    {
        _length = ToUnit(Original.Duration);
        Start();
    }

    public override string ToolId => "retime";

    public override string Title => "Retime";

    public override string Heading => "Speed the clip up or slow it down";

    public override string Description =>
        "Scales every key time (and start, end and the ramps) about a pivot. Values are untouched, so the motion keeps its shape "
        + "and only its speed changes. Keys that land on the same tick merge (the later one wins).";

    /// <summary>True: scale by <see cref="Percent"/>; false: scale to <see cref="Length"/>.</summary>
    public bool IsByFactor
    {
        get => _byFactor;
        set
        {
            if (SetParameter(ref _byFactor, value)) Raise(nameof(IsToLength));
        }
    }

    public bool IsToLength
    {
        get => !_byFactor;
        set => IsByFactor = !value;
    }

    /// <summary>The new duration as a percentage of the current one (200 = twice as long, half the speed).</summary>
    public double Percent
    {
        get => _percent;
        set => SetParameter(ref _percent, value);
    }

    /// <summary>The new duration in the display unit.</summary>
    public double Length
    {
        get => _length;
        set => SetParameter(ref _length, value);
    }

    public bool IsPivotStart
    {
        get => _pivot == RetimePivot.Start;
        set { if (value) SetPivot(RetimePivot.Start); }
    }

    public bool IsPivotPlayhead
    {
        get => _pivot == RetimePivot.Playhead;
        set { if (value) SetPivot(RetimePivot.Playhead); }
    }

    public bool IsPivotEnd
    {
        get => _pivot == RetimePivot.End;
        set { if (value) SetPivot(RetimePivot.End); }
    }

    /// <summary>The pivot.</summary>
    public RetimePivot Pivot
    {
        get => _pivot;
        set => SetPivot(value);
    }

    private void SetPivot(RetimePivot pivot)
    {
        if (SetParameter(ref _pivot, pivot, nameof(Pivot))) RaiseAll(nameof(IsPivotStart), nameof(IsPivotPlayhead), nameof(IsPivotEnd));
    }

    /// <summary>"Current length: 40 frames (1.333 s)".</summary>
    public string CurrentLengthText => "Current length: " + TimeFormat.Duration(Original.Duration);

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        double factor;
        string label;
        if (_byFactor)
        {
            factor = _percent / 100.0;
            label = string.Format(CultureInfo.CurrentCulture, "Retime ×{0:0.###}", factor);
        }
        else
        {
            if (clip.Duration <= 0) throw new ArgumentException("The clip has no duration to scale (end is not after start).");
            int length = ToTicks(_length);
            if (length <= 0) throw new ArgumentException("The new length must be more than zero.");
            factor = length / (double)clip.Duration;
            label = $"Retime to {Num(length)} {UnitSuffix}";
        }
        if (Math.Abs(factor - 1) < 1e-9)
        {
            var nothing = Nothing(_byFactor ? "Enter a percentage other than 100 %." : "Enter a length other than the current one.");
            return Work(_ => nothing);
        }
        int pivot = _pivot switch
        {
            RetimePivot.Playhead => (int)Math.Round(Playback.Time),
            RetimePivot.End => clip.EndTime,
            _ => clip.StartTime,
        };
        string pivotText = _pivot switch
        {
            RetimePivot.Playhead => $"the playhead ({Time(pivot)})",
            RetimePivot.End => "the end",
            _ => "the start",
        };
        var unit = Unit;
        return Work(_ =>
        {
            var result = ClipEdit.Retime(clip, factor, pivot);
            var lines = new List<string>
            {
                ClipToolSummary.RangeLine(clip, result, unit),
                string.Format(CultureInfo.CurrentCulture, "Every time scales by {0:0.###} about {1}; the clip plays {2}.", factor, pivotText,
                    factor > 1 ? $"{factor:0.##}× slower" : $"{1 / factor:0.##}× faster"),
            };
            int merged = KeyDiff.KeyCount(clip) - KeyDiff.KeyCount(result);
            if (merged > 0) lines.Add($"{merged:N0} keys merge with a neighbour that lands on the same tick.");
            if (result.RampIn != clip.RampIn || result.RampOut != clip.RampOut)
                lines.Add($"Ramps: in {TimeFormat.Format(clip.RampIn, unit)} → {TimeFormat.Format(result.RampIn, unit)}, out {TimeFormat.Format(clip.RampOut, unit)} → {TimeFormat.Format(result.RampOut, unit)}");
            return new ClipToolResult(result, label, lines);
        });
    }
}

/// <summary>Clip › Reverse: <see cref="ClipEdit.Reverse"/>.</summary>
public sealed class ReverseToolViewModel : ClipToolViewModel
{
    public ReverseToolViewModel(ClipDocumentViewModel document) : base(document) => Start();

    public override string ToolId => "reverse";

    public override string Title => "Reverse";

    public override string Heading => "Play the clip backwards";

    public override string Description =>
        "Every key moves to start + end − its time and the key order flips. Eases and control points swap sides, so each "
        + "segment is the exact mirror in time; reversing twice gives back the clip bit for bit.";

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        var unit = Unit;
        return Work(_ =>
        {
            var result = ClipEdit.Reverse(clip);
            var lines = new List<string>
            {
                ClipToolSummary.RangeLine(clip, result, unit),
                $"All {KeyDiff.KeyCount(clip):N0} keys move; the last pose becomes the first.",
            };
            if (clip.Version == 7 && !clip.Morph.IsEmpty)
                lines.Add("This clip has version 7 morph data, which runs one keyframe step early when reversed (convert to version 8 first when that matters).");
            return new ClipToolResult(result, "Reverse clip", lines);
        });
    }
}

/// <summary>Clip › Recompute Start/End: <see cref="ClipEdit.RecomputeRange"/>.</summary>
public sealed class RecomputeRangeToolViewModel : ClipToolViewModel
{
    public RecomputeRangeToolViewModel(ClipDocumentViewModel document) : base(document) => Start();

    public override string ToolId => "range";

    public override string Title => "Recompute Start/End";

    public override string Heading => "Fit start and end to the keys";

    public override string Description =>
        "Sets start to the earliest key and end to the latest, over every rotation and position key of every bone. Keys and ramps are unchanged.";

    protected override Func<CancellationToken, Task<ClipToolResult>> Prepare()
    {
        var clip = Original;
        var unit = Unit;
        return Work(_ =>
        {
            var result = ClipEdit.RecomputeRange(clip);
            if (result.StartTime == clip.StartTime && result.EndTime == clip.EndTime)
                return Nothing($"Start and end already match the first and last keys ({TimeFormat.Number(clip.StartTime, unit)}–{TimeFormat.Number(clip.EndTime, unit)} {TimeFormat.Suffix(unit)}).");
            return new ClipToolResult(result, "Recompute start/end", [ClipToolSummary.RangeLine(clip, result, unit)]);
        });
    }
}
