using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// One editable number in an inspector, in ATX Workbench's row shape: label, control, a reset button
/// when the value differs from the saved file, and an inline validation line fed by the linter. Every
/// change is one undo step through the document's edit path; a wheel-spin or stepper run is coalesced
/// (the view calls <see cref="BeginInteraction"/> / <see cref="EndInteraction"/>).
/// </summary>
public sealed class NumberRowViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _document;
    private readonly Func<RfaClip, double> _get;
    private readonly Func<RfaClip, double, RfaClip> _set;
    private readonly string _label;
    private bool _interacting;
    private string? _validation;
    private DiagnosticSeverity _validationSeverity;

    internal NumberRowViewModel(
        ClipDocumentViewModel document, string fieldId, string label, string editLabel,
        Func<RfaClip, double> get, Func<RfaClip, double, RfaClip> set,
        Func<int> decimals, Func<string> suffix, double minimum, double maximum, Func<double> step)
    {
        _document = document;
        FieldId = fieldId;
        _label = label;
        EditLabel = editLabel;
        _get = get;
        _set = set;
        DecimalsSource = decimals;
        SuffixSource = suffix;
        Minimum = minimum;
        Maximum = maximum;
        StepSource = step;
        ResetCommand = new RelayCommand(Reset, () => IsModified && !_document.IsReadOnly);
    }

    /// <summary>The <see cref="FormatDocs"/> id the row edits (also what diagnostics point at).</summary>
    public string FieldId { get; }

    public string Label => _label;

    /// <summary>The undo step's label, e.g. "Set ramp in".</summary>
    public string EditLabel { get; }

    /// <summary>The field's reference text (summary, engine use, limits).</summary>
    public string ToolTip => FormatDocs.Find(FieldId)?.Tooltip ?? _label;

    private Func<int> DecimalsSource { get; }

    private Func<string> SuffixSource { get; }

    private Func<double> StepSource { get; }

    public int Decimals => DecimalsSource();

    public string Suffix => SuffixSource();

    public double Step => StepSource();

    public double Minimum { get; }

    public double Maximum { get; }

    public bool IsReadOnly => _document.IsReadOnly;

    /// <summary>The value shown (in the display unit for times).</summary>
    public double Value
    {
        get => _get(_document.Current);
        set
        {
            if (!double.IsFinite(value)) return;
            if (Math.Abs(value - Value) < 1e-9) return;
            if (_interacting) _document.UpdateEdit(c => _set(c, value));
            else _document.Apply(EditLabel, c => _set(c, value));
            Raise();
        }
    }

    /// <summary>True when the value differs from the saved file.</summary>
    public bool IsModified => Math.Abs(_get(_document.Current) - _get(_document.SavedSnapshot)) > 1e-9;

    /// <summary>Puts the saved value back (one undo step).</summary>
    public RelayCommand ResetCommand { get; }

    /// <summary>The linter's message about this field, or null.</summary>
    public string? Validation
    {
        get => _validation;
        private set
        {
            if (Set(ref _validation, value)) Raise(nameof(HasValidation));
        }
    }

    public bool HasValidation => _validation is not null;

    public DiagnosticSeverity ValidationSeverity
    {
        get => _validationSeverity;
        private set => Set(ref _validationSeverity, value);
    }

    /// <summary>A stepping gesture started: further changes coalesce into one undo step.</summary>
    public void BeginInteraction()
    {
        if (_interacting || _document.IsReadOnly) return;
        _interacting = true;
        _document.BeginEdit(EditLabel);
    }

    /// <summary>The gesture ended: the coalesced change becomes one undo step.</summary>
    public void EndInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _document.CommitEdit();
    }

    private void Reset()
    {
        double saved = _get(_document.SavedSnapshot);
        _document.Apply("Reset " + _label.ToLowerInvariant(), c => _set(c, saved));
    }

    internal void Refresh()
    {
        RaiseAll(nameof(Value), nameof(IsModified), nameof(Decimals), nameof(Suffix), nameof(Step), nameof(IsReadOnly));
        ResetCommand.RaiseCanExecuteChanged();
    }

    internal void SetValidation(Diagnostic? diagnostic)
    {
        Validation = diagnostic?.Message;
        if (diagnostic is not null) ValidationSeverity = diagnostic.Severity;
    }
}

/// <summary>One read-only fact (label and value) in an inspector.</summary>
/// <param name="Label">The caption.</param>
/// <param name="Value">The value text.</param>
/// <param name="ToolTip">Reference text.</param>
public sealed record FactRow(string Label, string Value, string ToolTip);

/// <summary>
/// The inspector's Clip tab (DESIGN.md "Inspector / Clip"): every header field as an editable row with
/// its <see cref="FormatDocs"/> tooltip, reset and validation line, and the read-only facts.
/// </summary>
public sealed class ClipInspectorViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _document;
    private string _durationText = string.Empty;
    private bool _isAdvancedExpanded;

    internal ClipInspectorViewModel(ClipDocumentViewModel document)
    {
        _document = document;
        var shell = document.Shell;
        int TimeDecimals() => TimeFormat.Decimals(shell.TimeUnit);
        string TimeSuffix() => TimeFormat.Suffix(shell.TimeUnit);
        double TimeStep() => shell.TimeUnit switch { TimeUnit.Ticks => RfaClip.TicksPerFrame, TimeUnit.Seconds => 1.0 / 30, _ => 1 };
        double ToUnit(int ticks) => TimeFormat.ToUnit(ticks, shell.TimeUnit);
        int FromUnit(double v) => TimeFormat.FromUnit(v, shell.TimeUnit);

        Start = new NumberRowViewModel(document, "rfa.start_time", "Start", "Set start time",
            c => ToUnit(c.StartTime), (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { StartTime = FromUnit(v) }),
            TimeDecimals, TimeSuffix, -1e7, 1e7, TimeStep);
        End = new NumberRowViewModel(document, "rfa.end_time", "End", "Set end time",
            c => ToUnit(c.EndTime), (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { EndTime = FromUnit(v) }),
            TimeDecimals, TimeSuffix, -1e7, 1e7, TimeStep);
        RampIn = new NumberRowViewModel(document, "rfa.ramp_in", "Ramp in", "Set ramp in",
            c => ToUnit(c.RampIn), (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { RampIn = FromUnit(v) }),
            TimeDecimals, TimeSuffix, 0, 1e7, TimeStep);
        RampOut = new NumberRowViewModel(document, "rfa.ramp_out", "Ramp out", "Set ramp out",
            c => ToUnit(c.RampOut), (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { RampOut = FromUnit(v) }),
            TimeDecimals, TimeSuffix, 0, 1e7, TimeStep);
        PosReduction = new NumberRowViewModel(document, "rfa.pos_reduction", "Position reduction", "Set position reduction",
            c => c.PosReduction, (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { PosReduction = (float)v }),
            () => 5, () => string.Empty, -1e6, 1e6, () => 0.0001);
        RotReduction = new NumberRowViewModel(document, "rfa.rot_reduction", "Rotation reduction", "Set rotation reduction",
            c => c.RotReduction, (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { RotReduction = (float)v }),
            () => 5, () => string.Empty, -1e6, 1e6, () => 0.0001);

        TimeRows = [Start, End, RampIn, RampOut];
        ReductionRows = [PosReduction, RotReduction];

        string[] q = ["X", "Y", "Z", "W"];
        for (int i = 0; i < 4; i++)
        {
            int component = i;
            TotalRotationRows.Add(new NumberRowViewModel(document, "rfa.total_rotation", "Total rotation " + q[i],
                "Set total rotation",
                c => Component(c.TotalRotation, component),
                (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { TotalRotation = WithComponent(c.TotalRotation, component, (float)v) }),
                () => 5, () => string.Empty, -1e6, 1e6, () => 0.01));
        }
        for (int i = 0; i < 3; i++)
        {
            int component = i;
            TotalTranslationRows.Add(new NumberRowViewModel(document, "rfa.total_translation", "Total translation " + q[i],
                "Set total translation",
                c => component == 0 ? c.TotalTranslation.X : component == 1 ? c.TotalTranslation.Y : c.TotalTranslation.Z,
                (c, v) => ClipEdit.SetHeader(c, new ClipHeaderChange { TotalTranslation = WithComponent(c.TotalTranslation, component, (float)v) }),
                () => 4, () => "m", -1e6, 1e6, () => 0.01));
        }

        ConvertVersionCommand = new RelayCommand(
            p =>
            {
                if (p is not int version && !int.TryParse(p as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out version)) return;
                if (version == _document.Current.Version) return;
                _document.Apply($"Convert to version {version}", c => ClipEdit.ConvertVersion(c, version));
            },
            _ => !_document.IsReadOnly);
        ResetVersionCommand = new RelayCommand(
            () => _document.Apply("Reset version", c => ClipEdit.ConvertVersion(c, _document.SavedSnapshot.Version)),
            () => IsVersionModified && !_document.IsReadOnly);
        Refresh();
    }

    public NumberRowViewModel Start { get; }
    public NumberRowViewModel End { get; }
    public NumberRowViewModel RampIn { get; }
    public NumberRowViewModel RampOut { get; }
    public NumberRowViewModel PosReduction { get; }
    public NumberRowViewModel RotReduction { get; }

    public IReadOnlyList<NumberRowViewModel> TimeRows { get; }

    public IReadOnlyList<NumberRowViewModel> ReductionRows { get; }

    public ObservableCollection<NumberRowViewModel> TotalRotationRows { get; } = [];

    public ObservableCollection<NumberRowViewModel> TotalTranslationRows { get; } = [];

    /// <summary>Read-only facts: bones, keys, morph data, file size, origin.</summary>
    public ObservableCollection<FactRow> Facts { get; } = [];

    /// <summary>The version choices.</summary>
    public IReadOnlyList<int> Versions { get; } = [7, 8];

    /// <summary>The clip's format version (7 or 8); setting it converts (one undo step).</summary>
    public int Version
    {
        get => _document.Current.Version;
        set => ConvertVersionCommand.Execute(value);
    }

    public bool IsVersionModified => _document.Current.Version != _document.SavedSnapshot.Version;

    public string VersionToolTip => FormatDocs.Find("rfa.version")?.Tooltip ?? "Format version";

    public string? VersionValidation { get; private set; }

    public bool HasVersionValidation => VersionValidation is not null;

    public RelayCommand ConvertVersionCommand { get; }

    public RelayCommand ResetVersionCommand { get; }

    /// <summary>"40 frames (1.333 s)".</summary>
    public string DurationText
    {
        get => _durationText;
        private set => Set(ref _durationText, value);
    }

    public string DurationToolTip => FormatDocs.Find("rfa.duration")?.Tooltip ?? "End minus start";

    /// <summary>The total rotation/translation section (unused by the game) starts collapsed.</summary>
    public bool IsAdvancedExpanded
    {
        get => _isAdvancedExpanded;
        set => Set(ref _isAdvancedExpanded, value);
    }

    public bool IsReadOnly => _document.IsReadOnly;

    /// <summary>Raised when the Problems panel asks for a field (the view focuses its box).</summary>
    public event EventHandler<string>? FocusFieldRequested;

    /// <summary>Asks the view to focus the row for <paramref name="fieldId"/>.</summary>
    public void FocusField(string fieldId)
    {
        if (fieldId is "rfa.total_rotation" or "rfa.total_translation") IsAdvancedExpanded = true;
        FocusFieldRequested?.Invoke(this, fieldId);
    }

    /// <summary>Re-reads everything from the document (snapshot or time unit changed).</summary>
    public void Refresh()
    {
        var clip = _document.Current;
        foreach (var row in AllRows()) row.Refresh();
        DurationText = TimeFormat.Duration(clip.Duration);
        RaiseAll(nameof(Version), nameof(IsVersionModified), nameof(IsReadOnly));
        ResetVersionCommand.RaiseCanExecuteChanged();
        RebuildFacts(clip);
    }

    /// <summary>Feeds the linter's header-field diagnostics to the rows' validation lines.</summary>
    public void ApplyDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        Diagnostic? For(string id) => diagnostics.FirstOrDefault(d =>
            d.Location.Target == DiagnosticTarget.HeaderField && string.Equals(d.Location.Field, id, StringComparison.OrdinalIgnoreCase));
        foreach (var row in AllRows()) row.SetValidation(For(row.FieldId));
        VersionValidation = For("rfa.version")?.Message;
        RaiseAll(nameof(VersionValidation), nameof(HasVersionValidation));
    }

    private IEnumerable<NumberRowViewModel> AllRows() =>
        TimeRows.Concat(ReductionRows).Concat(TotalRotationRows).Concat(TotalTranslationRows);

    private void RebuildFacts(RfaClip clip)
    {
        Facts.Clear();
        string Tip(string id) => FormatDocs.Find(id)?.Tooltip ?? string.Empty;
        int meshBones = _document.PreviewSkeletonCount;
        string bones = clip.BoneCount.ToString(CultureInfo.CurrentCulture);
        if (meshBones > 0 && meshBones != clip.BoneCount) bones += $" (the preview mesh has {meshBones})";
        Facts.Add(new FactRow("Bones", bones, Tip("rfa.num_bones")));
        int rot = clip.Bones.Sum(b => b.RotationKeys.Length), pos = clip.Bones.Sum(b => b.PositionKeys.Length);
        Facts.Add(new FactRow("Keys", $"{rot:N0} rotation · {pos:N0} position", Tip("rfa.bone.num_rot_keys")));
        if (clip.Morph.IsEmpty)
        {
            Facts.Add(new FactRow("Morph", "None", Tip("rfa.num_morph_vertices")));
        }
        else
        {
            string morph = $"{clip.Morph.VertexCount:N0} vertices · {clip.Morph.KeyframeCount} keyframes";
            morph += clip.Version >= 8 ? " · timed (v8)" : " · evenly spread (v7)";
            Facts.Add(new FactRow("Morph", morph, Tip("rfa.num_morph_vertices")));
        }
        Facts.Add(new FactRow("File size", _document.FileSizeText, Tip("rfa.file_size")));
        Facts.Add(new FactRow("Origin", _document.OriginText, "Where this clip was opened from."));
    }

    private static double Component(Quaternion q, int i) => i switch { 0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W };

    private static Quaternion WithComponent(Quaternion q, int i, float v) => i switch
    {
        0 => q with { X = v },
        1 => q with { Y = v },
        2 => q with { Z = v },
        _ => q with { W = v },
    };

    private static Vector3 WithComponent(Vector3 p, int i, float v) => i switch
    {
        0 => p with { X = v },
        1 => p with { Y = v },
        _ => p with { Z = v },
    };
}
