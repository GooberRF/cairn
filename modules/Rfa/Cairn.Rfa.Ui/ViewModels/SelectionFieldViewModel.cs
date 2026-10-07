using Cairn.Ui.Mvvm;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// One number of an inspector that edits a multi-selection (keys or bones), in ATX Workbench's frame
/// inspector manner: it shows the common value, or an indeterminate box when the selected items
/// disagree; an edit applies to every selected item as ONE undo step; a spinner drag / wheel run /
/// slider drag is coalesced into one step (the view calls <see cref="BeginInteraction"/> and
/// <see cref="EndInteraction"/>).
/// </summary>
public sealed class SelectionFieldViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _doc;
    private readonly Func<RfaClip, IReadOnlyList<double>> _read;
    private readonly Func<RfaClip, double, RfaClip> _write;
    private readonly Func<string> _editLabel;
    private double _value;
    private bool _isMixed;
    private bool _isAvailable;
    private bool _interacting;

    /// <param name="document">The clip document.</param>
    /// <param name="label">Caption ("Ease in").</param>
    /// <param name="docId">The <see cref="FormatDocs"/> id for the tooltip, or null.</param>
    /// <param name="extraToolTip">Text appended to (or used instead of) the reference text.</param>
    /// <param name="read">The field's value for every selected item it applies to (empty = not applicable).</param>
    /// <param name="write">Sets the value on every selected item it applies to.</param>
    /// <param name="editLabel">The undo label ("Set ease in of 3 keys").</param>
    /// <param name="decimals">Digits shown.</param>
    /// <param name="suffix">Unit text.</param>
    /// <param name="step">Spinner step.</param>
    /// <param name="minimum">Lowest value.</param>
    /// <param name="maximum">Highest value.</param>
    public SelectionFieldViewModel(
        ClipDocumentViewModel document, string label, string? docId, string? extraToolTip,
        Func<RfaClip, IReadOnlyList<double>> read, Func<RfaClip, double, RfaClip> write, Func<string> editLabel,
        Func<int> decimals, Func<string> suffix, Func<double> step, double minimum = -1e7, double maximum = 1e7)
    {
        _doc = document;
        Label = label;
        _read = read;
        _write = write;
        _editLabel = editLabel;
        DecimalsSource = decimals;
        SuffixSource = suffix;
        StepSource = step;
        Minimum = minimum;
        Maximum = maximum;
        string reference = docId is null ? string.Empty : FormatDocs.Find(docId)?.Tooltip ?? string.Empty;
        ToolTip = string.IsNullOrEmpty(extraToolTip) ? (reference.Length > 0 ? reference : label)
            : reference.Length > 0 ? extraToolTip + "\n\n" + reference : extraToolTip;
    }

    public string Label { get; }

    /// <summary>The short caption drawn above a compact box ("X", "qW"); defaults to <see cref="Label"/>.</summary>
    public string Caption
    {
        get => _caption ?? Label;
        init => _caption = value;
    }

    private readonly string? _caption;

    public string ToolTip { get; }

    /// <summary>Called when a coalesced gesture starts (before the first write).</summary>
    public Action? Began { get; init; }

    /// <summary>Called after an edit landed (a single edit, or the end of a gesture).</summary>
    public Action? Committed { get; init; }

    private Func<int> DecimalsSource { get; }

    private Func<string> SuffixSource { get; }

    private Func<double> StepSource { get; }

    public int Decimals => DecimalsSource();

    public string Suffix => SuffixSource();

    public double Step => StepSource();

    public double Minimum { get; }

    public double Maximum { get; }

    /// <summary>
    /// The left end of a slider bound to <see cref="SliderValue"/> (defaults to <see cref="Minimum"/>):
    /// a stored value below it is shown at the end without being changed (phase 6: negative ease bytes).
    /// </summary>
    public double SliderMinimum
    {
        get => _sliderMinimum ?? Minimum;
        init => _sliderMinimum = value;
    }

    private readonly double? _sliderMinimum;

    /// <summary>
    /// <see cref="Value"/> clamped into [<see cref="SliderMinimum"/>, <see cref="Maximum"/>] for a slider.
    /// Because it never leaves the slider's range, the slider never coerces it and writes the coerced
    /// value back, so showing a key never edits it; a real slider move edits like <see cref="Value"/>.
    /// </summary>
    public double SliderValue
    {
        get => Math.Clamp(_value, SliderMinimum, Maximum);
        set
        {
            if (!double.IsFinite(value) || Math.Abs(value - SliderValue) < 1e-9) return;
            Value = value;
            Raise(nameof(SliderValue));
        }
    }

    public bool IsReadOnly => _doc.IsReadOnly;

    /// <summary>True when the selection holds at least one item the field applies to.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set => Set(ref _isAvailable, value);
    }

    /// <summary>True when the selected items disagree (the box shows no number).</summary>
    public bool IsMixed
    {
        get => _isMixed;
        private set => Set(ref _isMixed, value);
    }

    /// <summary>The common value (the first item's when mixed); setting it edits every selected item.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || !_isAvailable) return;
            if (!_isMixed && Math.Abs(value - _value) < 1e-9) return;
            double v = Math.Clamp(value, Minimum, Maximum);
            if (_interacting)
            {
                _doc.UpdateEdit(c => _write(c, v));
                _value = v;
                Raise(nameof(Value));
                Raise(nameof(SliderValue));
                return;
            }
            if (_doc.Apply(_editLabel(), c => _write(c, v))) Committed?.Invoke();
            Refresh();
        }
    }

    /// <summary>A stepping gesture started: further changes coalesce into one undo step.</summary>
    public void BeginInteraction()
    {
        if (_interacting || _doc.IsReadOnly || !_isAvailable) return;
        Began?.Invoke();
        _interacting = true;
        _doc.BeginEdit(_editLabel());
    }

    /// <summary>The gesture ended.</summary>
    public void EndInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _doc.CommitEdit();
        Committed?.Invoke();
        Refresh();
    }

    /// <summary>True while a coalesced gesture is running.</summary>
    public bool IsInteracting => _interacting;

    /// <summary>Re-reads the value from the document.</summary>
    public void Refresh()
    {
        var values = _read(_doc.Current);
        IsAvailable = values.Count > 0;
        double first = values.Count > 0 ? values[0] : 0;
        bool mixed = false;
        double tolerance = Math.Pow(10, -Math.Clamp(DecimalsSource(), 0, 6)) * 0.5;
        for (int i = 1; i < values.Count; i++)
        {
            if (Math.Abs(values[i] - first) > tolerance)
            {
                mixed = true;
                break;
            }
        }
        IsMixed = mixed;
        if (_value != first)
        {
            _value = first;
            Raise(nameof(Value));
        }
        else Raise(nameof(Value));
        RaiseAll(nameof(SliderValue), nameof(Decimals), nameof(Suffix), nameof(Step), nameof(IsReadOnly));
    }
}
