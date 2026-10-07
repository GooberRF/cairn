using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The inspector's Key tab (DESIGN.md "Inspector / Key"): the selected keys' time, rotation (Euler
/// degrees and quaternion, editing either updates the other), eases with a drawn ease-curve preview,
/// position, Bezier control points with Auto (linear) / Auto (smooth), and the read-only model-space
/// joint position. Mixed values show indeterminate; an edit applies to every selected key of the kind
/// it concerns as one undo step; spinner and slider drags coalesce.
/// </summary>
public sealed class KeyInspectorViewModel : ObservableObject
{
    /// <summary>The Euler convention, stated in the rotation tooltips.</summary>
    public const string EulerConvention =
        "Euler angles in degrees: X = pitch, Y = yaw, Z = roll, composed q = Ry(yaw) · Rx(pitch) · Rz(roll) — roll is applied first, yaw last — "
        + "in RF's left-handed frame (+X right, +Y up, +Z forward), as the active rotation of the bone relative to its parent "
        + "(the file stores the conjugate). Pitch is kept in [−90°, 90°]; at ±90° roll folds into yaw.";

    private readonly ClipDocumentViewModel _doc;
    private KeySelection? _frozen;
    private KeySelection? _moved;
    private string _title = "No key selected";
    private string _detail = string.Empty;
    private string _modelPositionText = "—";
    private string _modelPositionNote = string.Empty;
    private float _curveInA, _curveInB, _curveOutA, _curveOutB;
    private bool _curveHasIn, _curveHasOut;

    internal KeyInspectorViewModel(ClipDocumentViewModel document)
    {
        _doc = document;
        var shell = document.Shell;
        int TimeDecimals() => TimeFormat.Decimals(shell.TimeUnit);
        string TimeSuffix() => TimeFormat.Suffix(shell.TimeUnit);
        double TimeStep() => shell.TimeUnit switch { TimeUnit.Ticks => RfaClip.TicksPerFrame, TimeUnit.Seconds => 1.0 / 30, _ => 1 };

        Time = new SelectionFieldViewModel(document, "Time", "rfa.rotkey.time",
            "The time of the selected keys (the earliest one when several are selected; the others keep their spacing). Ticks are in the tooltip of the readout below.",
            c => Sel(c).TimeSpan(c) is { } span ? [TimeFormat.ToUnit(span.Min, shell.TimeUnit)] : [],
            (c, v) => SetTime(c, TimeFormat.FromUnit(v, shell.TimeUnit)),
            () => $"Move {TimelineViewModel.Keys(Sel(_doc.Current).Count)}",
            TimeDecimals, TimeSuffix, TimeStep)
        {
            Began = () => _frozen = _doc.KeySelection,
            Committed = () =>
            {
                _frozen = null;
                if (_moved is { } moved) _doc.SetKeySelection(moved);
                _moved = null;
            },
        };

        string[] axes = ["X pitch", "Y yaw", "Z roll"];
        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            Euler.Add(new SelectionFieldViewModel(document, "Euler " + axes[i], "rfa.rotkey.rotation", EulerConvention,
                c => RotKeys(c).Select(k => (double)Component(Quat.ToEulerDegrees(ClipEdit.KeyRotation(k.Key)), axis)).ToList(),
                (c, v) => EditRotations(c, q =>
                {
                    var e = Quat.ToEulerDegrees(q);
                    e = axis switch { 0 => e with { X = (float)v }, 1 => e with { Y = (float)v }, _ => e with { Z = (float)v } };
                    return Quat.FromEulerDegrees(e);
                }),
                () => RotationLabel(), () => 2, () => "°", () => 1, -360, 360) { Caption = axes[axis] });
        }
        string[] comps = ["X", "Y", "Z", "W"];
        for (int i = 0; i < 4; i++)
        {
            int comp = i;
            Quaternion.Add(new SelectionFieldViewModel(document, "Quaternion " + comps[i], "rfa.rotkey.rotation",
                "A component of the key's rotation as a unit quaternion (active convention; the file stores the conjugate scaled by 16383). "
                + "Changing one component renormalises the others. " + EulerConvention,
                c => RotKeys(c).Select(k => (double)Component(ClipEdit.KeyRotation(k.Key), comp)).ToList(),
                (c, v) => EditRotations(c, q => Quat.Normalize(WithComponent(q, comp, (float)v))),
                () => RotationLabel(), () => 4, () => string.Empty, () => 0.01, -1, 1) { Caption = "q" + comps[comp] });
        }

        // The box shows the stored signed byte as a percentage of 127, negatives included (-128 = -100.8 %);
        // the slider spans the authoring range 0..100 % and shows a negative byte at its left end without
        // touching it (SliderValue). Editing writes 0..127 only (a typed negative sets 0).
        EaseIn = new SelectionFieldViewModel(document, "Ease in", "rfa.rotkey.ease_in",
            "How gently the segment ending at this key arrives: 0–100 % is the ease byte 0–127 (the stored signed byte is shown beside the box).\n"
            + NegativeEaseHelp("the segment arriving at this key runs linearly, stops short of the key and snaps onto it when the key is reached"),
            c => RotKeys(c).Select(k => k.Key.EaseIn * 100.0 / 127).ToList(),
            (c, v) => ClipEdit.SetEases(c, RotationSelection(c), ToEase(v), null),
            () => $"Set ease in of {TimelineViewModel.Keys(RotationSelection(_doc.Current).Count)}",
            () => 0, () => "%", () => 5, EaseMinimumPercent, 100) { SliderMinimum = 0 };
        EaseOut = new SelectionFieldViewModel(document, "Ease out", "rfa.rotkey.ease_out",
            "How gently the segment starting at this key leaves it: 0–100 % is the ease byte 0–127 (the stored signed byte is shown beside the box).\n"
            + NegativeEaseHelp("the segment leaving this key jumps part of the way at once just after the key, then runs linearly"),
            c => RotKeys(c).Select(k => k.Key.EaseOut * 100.0 / 127).ToList(),
            (c, v) => ClipEdit.SetEases(c, RotationSelection(c), null, ToEase(v)),
            () => $"Set ease out of {TimelineViewModel.Keys(RotationSelection(_doc.Current).Count)}",
            () => 0, () => "%", () => 5, EaseMinimumPercent, 100) { SliderMinimum = 0 };

        for (int i = 0; i < 3; i++)
        {
            int axis = i;
            Position.Add(new SelectionFieldViewModel(document, "Position " + comps[i], "rfa.poskey.position",
                "The key's position in metres, in the parent bone's frame (model space for the root). The control points move with it, keeping the curve's shape.",
                c => PosKeys(c).Select(k => (double)Component(k.Key.Position, axis)).ToList(),
                (c, v) => EditPositions(c, k =>
                {
                    var p = WithComponent(k.Position, axis, (float)v);
                    var d = p - k.Position;
                    return k with { Position = p, InControl = k.InControl + d, OutControl = k.OutControl + d };
                }),
                () => PositionLabel("position"), () => 4, () => "m", () => 0.01) { Caption = comps[axis] });
            InControl.Add(new SelectionFieldViewModel(document, "In control " + comps[i], "rfa.poskey.in_ctrl", null,
                c => PosKeys(c).Select(k => (double)Component(k.Key.InControl, axis)).ToList(),
                (c, v) => EditPositions(c, k => k with { InControl = WithComponent(k.InControl, axis, (float)v) }),
                () => PositionLabel("in control point"), () => 4, () => "m", () => 0.01) { Caption = "In " + comps[axis] });
            OutControl.Add(new SelectionFieldViewModel(document, "Out control " + comps[i], "rfa.poskey.out_ctrl", null,
                c => PosKeys(c).Select(k => (double)Component(k.Key.OutControl, axis)).ToList(),
                (c, v) => EditPositions(c, k => k with { OutControl = WithComponent(k.OutControl, axis, (float)v) }),
                () => PositionLabel("out control point"), () => 4, () => "m", () => 0.01) { Caption = "Out " + comps[axis] });
        }

        AutoLinearCommand = new RelayCommand(() => AutoControls(ControlPointMode.Linear), () => HasPositionKeys && !_doc.IsReadOnly);
        AutoSmoothCommand = new RelayCommand(() => AutoControls(ControlPointMode.Smooth), () => HasPositionKeys && !_doc.IsReadOnly);
        SeekCommand = new RelayCommand(() =>
        {
            if (Sel(_doc.Current).TimeSpan(_doc.Current) is { } span) _doc.Playback.Seek(span.Min);
        }, () => HasKeys);

        _doc.KeySelectionChanged += (_, _) => Refresh();
        _doc.PreviewSkeletonChanged += (_, _) => Refresh();
        Refresh();
    }

    public SelectionFieldViewModel Time { get; }

    public ObservableCollection<SelectionFieldViewModel> Euler { get; } = [];

    public ObservableCollection<SelectionFieldViewModel> Quaternion { get; } = [];

    public SelectionFieldViewModel EaseIn { get; }

    public SelectionFieldViewModel EaseOut { get; }

    /// <summary>The lowest stored ease byte (-128) as a percentage of 127.</summary>
    public const double EaseMinimumPercent = -128 * 100.0 / 127;

    private static string NegativeEaseHelp(string effect) =>
        "A negative byte (−1 to −128) is not an ease: the engine's ca_ease (0x0053A040) reads the bytes as signed, and with a negative value "
        + effect + " — a visible pop of up to about half the segment (the curve preview draws the jump as a dotted stroke in the warning colour). "
        + "No stock clip stores one. This editor writes 0–127 only: typing a negative value sets 0; a negative byte you do not edit is kept exactly.";

    private string _easeInByteText = string.Empty, _easeOutByteText = string.Empty, _negativeEaseNote = string.Empty;

    /// <summary>The stored ease-in byte(s) of the selected rotation keys: "byte 2", "bytes −64 … 0".</summary>
    public string EaseInByteText { get => _easeInByteText; private set => Set(ref _easeInByteText, value); }

    /// <summary>The stored ease-out byte(s) of the selected rotation keys.</summary>
    public string EaseOutByteText { get => _easeOutByteText; private set => Set(ref _easeOutByteText, value); }

    /// <summary>What a negative ease byte of the selected key does (empty when none is negative).</summary>
    public string NegativeEaseNote { get => _negativeEaseNote; private set => Set(ref _negativeEaseNote, value); }

    /// <summary>True when a selected rotation key stores a negative ease byte.</summary>
    public bool HasNegativeEase => _negativeEaseNote.Length > 0;

    public ObservableCollection<SelectionFieldViewModel> Position { get; } = [];

    public ObservableCollection<SelectionFieldViewModel> InControl { get; } = [];

    public ObservableCollection<SelectionFieldViewModel> OutControl { get; } = [];

    public RelayCommand AutoLinearCommand { get; }

    public RelayCommand AutoSmoothCommand { get; }

    public RelayCommand SeekCommand { get; }

    /// <summary>"Rotation key 3 of ult2-bdbn-hand-l", "5 keys on 2 bones".</summary>
    public string Title { get => _title; private set => Set(ref _title, value); }

    /// <summary>"3 rotation, 2 position keys · 12 f … 40 f".</summary>
    public string Detail { get => _detail; private set => Set(ref _detail, value); }

    public bool HasKeys { get; private set; }

    public bool HasRotationKeys { get; private set; }

    public bool HasPositionKeys { get; private set; }

    /// <summary>The time readout's tooltip: ticks and the other units.</summary>
    public string TimeToolTip { get; private set; } = string.Empty;

    /// <summary>"(0.123, 1.204, 0.045) m" — the joint in model space at the key's time.</summary>
    public string ModelPositionText { get => _modelPositionText; private set => Set(ref _modelPositionText, value); }

    /// <summary>Which key the model-space position belongs to, or why there is none.</summary>
    public string ModelPositionNote { get => _modelPositionNote; private set => Set(ref _modelPositionNote, value); }

    public string ModelPositionToolTip =>
        "Where the joint is in model space at the key's time (forward kinematics of the preview mesh's skeleton with this clip). Read-only.";

    // Ease curve preview: incoming segment (previous ease out → this ease in) and outgoing (this ease out → next ease in), as 0..1.
    public float CurveInA => _curveInA;
    public float CurveInB => _curveInB;
    public float CurveOutA => _curveOutA;
    public float CurveOutB => _curveOutB;
    public bool CurveHasIn => _curveHasIn;
    public bool CurveHasOut => _curveHasOut;

    public bool IsReadOnly => _doc.IsReadOnly;

    /// <summary>Re-reads everything (selection, snapshot, unit or skeleton changed).</summary>
    public void Refresh()
    {
        var clip = _doc.Current;
        var sel = Sel(clip);
        int rot = sel.Keys.Count(k => k.Kind == KeyKind.Rotation), pos = sel.Count - rot;
        HasKeys = sel.Count > 0;
        HasRotationKeys = rot > 0;
        HasPositionKeys = pos > 0;
        var unit = _doc.Shell.TimeUnit;
        if (sel.Count == 0)
        {
            Title = "No key selected";
            Detail = "Select keys in the timeline (click, Ctrl-click, drag a box) to see and edit them here.";
            TimeToolTip = string.Empty;
        }
        else
        {
            var first = sel.Keys[0];
            int bones = sel.SelectedBones.Count();
            Title = sel.Count == 1
                ? $"{(first.Kind == KeyKind.Rotation ? "Rotation" : "Position")} key {first.Index} of {_doc.BoneDisplayName(first.Bone)}"
                : $"{TimelineViewModel.Keys(sel.Count)} on {(bones == 1 ? _doc.BoneDisplayName(first.Bone) : $"{bones} bones")}";
            var span = sel.TimeSpan(clip);
            string range = span is { } s ? (s.Min == s.Max ? TimeFormat.Format(s.Min, unit) : $"{TimeFormat.Format(s.Min, unit)} – {TimeFormat.Format(s.Max, unit)}") : "";
            Detail = $"{rot:N0} rotation, {pos:N0} position · {range}";
            TimeToolTip = span is { } t2
                ? $"{t2.Min} ticks{(t2.Max != t2.Min ? $" – {t2.Max} ticks" : string.Empty)} · {TimeFormat.Format(t2.Min, TimeUnit.Frames)} · {TimeFormat.Format(t2.Min, TimeUnit.Seconds)}"
                : string.Empty;
        }
        foreach (var f in AllFields()) f.Refresh();
        UpdateCurve(clip, sel);
        UpdateModelPosition(clip, sel);
        RaiseAll(nameof(HasKeys), nameof(HasRotationKeys), nameof(HasPositionKeys), nameof(TimeToolTip), nameof(IsReadOnly));
        AutoLinearCommand.RaiseCanExecuteChanged();
        AutoSmoothCommand.RaiseCanExecuteChanged();
        SeekCommand.RaiseCanExecuteChanged();
    }

    private IEnumerable<SelectionFieldViewModel> AllFields() =>
        new[] { Time, EaseIn, EaseOut }.Concat(Euler).Concat(Quaternion).Concat(Position).Concat(InControl).Concat(OutControl);

    // ── Selection helpers ────────────────────────────────────────────────────

    /// <summary>The selection edits use: frozen during a time drag, else the document's, valid in <paramref name="clip"/>.</summary>
    private KeySelection Sel(RfaClip clip) => (_frozen ?? _doc.KeySelection).Validate(clip);

    private KeySelection RotationSelection(RfaClip clip) => KeySelection.Of(Sel(clip).Keys.Where(k => k.Kind == KeyKind.Rotation));

    private IEnumerable<(KeyRef Ref, RfaRotKey Key)> RotKeys(RfaClip clip) =>
        Sel(clip).Keys.Where(k => k.Kind == KeyKind.Rotation).Select(k => (k, clip.Bones[k.Bone].RotationKeys[k.Index]));

    private IEnumerable<(KeyRef Ref, RfaPosKey Key)> PosKeys(RfaClip clip) =>
        Sel(clip).Keys.Where(k => k.Kind == KeyKind.Position).Select(k => (k, clip.Bones[k.Bone].PositionKeys[k.Index]));

    private string RotationLabel()
    {
        var keys = RotationSelection(_doc.Current);
        return keys.Count == 1 ? $"Rotate key of {_doc.BoneDisplayName(keys.Keys[0].Bone)}" : $"Rotate {TimelineViewModel.Keys(keys.Count)}";
    }

    private string PositionLabel(string what)
    {
        int n = Sel(_doc.Current).Keys.Count(k => k.Kind == KeyKind.Position);
        return $"Set {what} of {TimelineViewModel.Keys(n)}";
    }

    // ── Edits ────────────────────────────────────────────────────────────────

    private RfaClip SetTime(RfaClip clip, int ticks)
    {
        var sel = Sel(clip);
        if (sel.TimeSpan(clip) is not { } span) return clip;
        int delta = ticks - span.Min;
        if (delta == 0) return clip;
        var next = ClipEdit.MoveKeys(clip, sel, delta, out var moved);
        _moved = moved;
        return next;
    }

    /// <summary>Applies a rotation change to each selected rotation key (re-quantised, sign-continuous, eases kept).</summary>
    private RfaClip EditRotations(RfaClip clip, Func<System.Numerics.Quaternion, System.Numerics.Quaternion> change)
    {
        var result = clip;
        foreach (var k in Sel(clip).Keys.Where(k => k.Kind == KeyKind.Rotation))
        {
            var track = result.Bones[k.Bone].RotationKeys;
            var key = track[k.Index];
            var active = change(ClipEdit.KeyRotation(key));
            RfaRotKey? previous = k.Index > 0 ? track[k.Index - 1] : k.Index + 1 < track.Length ? track[k.Index + 1] : null;
            var replaced = ClipEdit.QuantizeRotation(key.Time, active, previous, key.EaseIn, key.EaseOut);
            result = ClipEdit.ReplaceRotationKey(result, k.Bone, k.Index, replaced);
        }
        return result;
    }

    private RfaClip EditPositions(RfaClip clip, Func<RfaPosKey, RfaPosKey> change)
    {
        var result = clip;
        foreach (var k in Sel(clip).Keys.Where(k => k.Kind == KeyKind.Position))
        {
            var key = result.Bones[k.Bone].PositionKeys[k.Index];
            result = ClipEdit.ReplacePositionKey(result, k.Bone, k.Index, change(key));
        }
        return result;
    }

    private void AutoControls(ControlPointMode mode)
    {
        var positions = KeySelection.Of(Sel(_doc.Current).Keys.Where(k => k.Kind == KeyKind.Position));
        if (positions.IsEmpty) return;
        string label = $"Auto control points ({(mode == ControlPointMode.Linear ? "linear" : "smooth")}) of {TimelineViewModel.Keys(positions.Count)}";
        _doc.Apply(label, c => ClipEdit.AutoControlPoints(c, positions.Validate(c), mode));
    }

    private static sbyte ToEase(double percent) => (sbyte)Math.Clamp((int)Math.Round(percent * 127 / 100), 0, 127);

    // ── Read-only facts ──────────────────────────────────────────────────────

    private void UpdateCurve(RfaClip clip, KeySelection sel)
    {
        _curveHasIn = _curveHasOut = false;
        _curveInA = _curveInB = _curveOutA = _curveOutB = 0;
        var first = sel.Keys.FirstOrDefault(k => k.Kind == KeyKind.Rotation);
        if (sel.Keys.Any(k => k.Kind == KeyKind.Rotation))
        {
            var track = clip.Bones[first.Bone].RotationKeys;
            var key = track[first.Index];
            // Signed, exactly as the engine reads them (a negative byte draws as a jump).
            if (first.Index > 0)
            {
                _curveHasIn = true;
                _curveInA = track[first.Index - 1].EaseOut / RfaClip.EaseScale;
                _curveInB = key.EaseIn / RfaClip.EaseScale;
            }
            if (first.Index + 1 < track.Length)
            {
                _curveHasOut = true;
                _curveOutA = key.EaseOut / RfaClip.EaseScale;
                _curveOutB = track[first.Index + 1].EaseIn / RfaClip.EaseScale;
            }
        }
        RaiseAll(nameof(CurveInA), nameof(CurveInB), nameof(CurveOutA), nameof(CurveOutB), nameof(CurveHasIn), nameof(CurveHasOut));
        UpdateEaseBytes(clip, sel);
    }

    private void UpdateEaseBytes(RfaClip clip, KeySelection sel)
    {
        var keys = RotKeys(clip).Select(k => k.Key).ToList();
        static string Bytes(IEnumerable<sbyte> values)
        {
            var list = values.ToList();
            if (list.Count == 0) return string.Empty;
            int min = list.Min(v => (int)v), max = list.Max(v => (int)v);
            return min == max
                ? string.Format(CultureInfo.CurrentCulture, "byte {0}", min)
                : string.Format(CultureInfo.CurrentCulture, "bytes {0} … {1}", min, max);
        }
        EaseInByteText = Bytes(keys.Select(k => k.EaseIn));
        EaseOutByteText = Bytes(keys.Select(k => k.EaseOut));

        int negative = keys.Count(k => k.EaseIn < 0 || k.EaseOut < 0);
        string note = string.Empty;
        if (negative > 0)
        {
            // What the engine does with the first selected key's negative bytes (ClipSampler.Ease = ca_ease).
            var parts = new List<string>();
            var first = sel.Keys.First(k => k.Kind == KeyKind.Rotation);
            var key = clip.Bones[first.Bone].RotationKeys[first.Index];
            if (key.EaseIn < 0 && _curveHasIn)
            {
                double snap = 1 - ClipSampler.Ease(1 - 1e-5f, _curveInA, _curveInB);
                parts.Add(string.Format(CultureInfo.CurrentCulture, "ease in {0} makes the arriving segment stop {1:0} % short and snap onto the key", (int)key.EaseIn, snap * 100));
            }
            if (key.EaseOut < 0 && _curveHasOut)
            {
                double jump = ClipSampler.Ease(1e-5f, _curveOutA, _curveOutB);
                parts.Add(string.Format(CultureInfo.CurrentCulture, "ease out {0} makes the leaving segment jump {1:0} % at once just after the key", (int)key.EaseOut, jump * 100));
            }
            note = (keys.Count == 1 ? "Negative ease byte" : $"{negative} of the selected rotation keys store a negative ease byte")
                + ": the engine reads ease bytes as signed, so this is a pop, not an ease"
                + (parts.Count > 0 ? (keys.Count == 1 ? "" : " (first key)") + ": " + string.Join("; ", parts) + "." : ".")
                + " Kept as stored unless you set the ease.";
        }
        NegativeEaseNote = note;
        Raise(nameof(HasNegativeEase));
    }

    private void UpdateModelPosition(RfaClip clip, KeySelection sel)
    {
        if (sel.IsEmpty)
        {
            ModelPositionText = "—";
            ModelPositionNote = string.Empty;
            return;
        }
        if (_doc.FittingSkeleton is not { } skeleton)
        {
            ModelPositionText = "—";
            ModelPositionNote = "Needs a preview mesh whose bone count matches the clip.";
            return;
        }
        var k = sel.Keys[0];
        int time = k.TimeIn(clip);
        var pose = new Pose(skeleton);
        pose.Sample(clip, time);
        var p = pose.World[k.Bone].Position;
        ModelPositionText = string.Format(CultureInfo.CurrentCulture, "({0:0.####}, {1:0.####}, {2:0.####}) m", p.X, p.Y, p.Z);
        ModelPositionNote = sel.Count == 1
            ? $"{_doc.BoneDisplayName(k.Bone)} at {TimeFormat.Format(time, _doc.Shell.TimeUnit)}"
            : $"First selected key: {_doc.BoneDisplayName(k.Bone)} at {TimeFormat.Format(time, _doc.Shell.TimeUnit)}";
    }

    private static float Component(Vector3 v, int i) => i switch { 0 => v.X, 1 => v.Y, _ => v.Z };

    private static float Component(System.Numerics.Quaternion q, int i) => i switch { 0 => q.X, 1 => q.Y, 2 => q.Z, _ => q.W };

    private static Vector3 WithComponent(Vector3 v, int i, float value) => i switch
    {
        0 => v with { X = value },
        1 => v with { Y = value },
        _ => v with { Z = value },
    };

    private static System.Numerics.Quaternion WithComponent(System.Numerics.Quaternion q, int i, float value) => i switch
    {
        0 => q with { X = value },
        1 => q with { Y = value },
        2 => q with { Z = value },
        _ => q with { W = value },
    };
}
