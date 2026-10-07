using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels.MeshEditing;

/// <summary>One editable value of a mesh node editor; <see cref="Refresh"/> re-reads it from the document.</summary>
public interface IMeshField
{
    /// <summary>Re-reads the value from the current snapshot.</summary>
    void Refresh();
}

/// <summary>Helpers shared by the fields.</summary>
internal static class MeshFieldText
{
    /// <summary>The <see cref="FormatDocs"/> tooltip of <paramref name="docId"/>, after <paramref name="extra"/> when both exist.</summary>
    public static string ToolTip(string label, string? docId, string? extra)
    {
        string reference = docId is null ? string.Empty : FormatDocs.Find(docId)?.Tooltip ?? string.Empty;
        if (string.IsNullOrEmpty(extra)) return reference.Length > 0 ? reference : label;
        return reference.Length > 0 ? extra + "\n\n" + reference : extra;
    }
}

/// <summary>
/// A number of a mesh node (a coordinate, a radius, an angle). An edit is one undo step through the
/// editor's <see cref="MeshNodeEditor.TryApply"/>; a spinner or wheel run (the view calls
/// <see cref="BeginInteraction"/> / <see cref="EndInteraction"/>) is coalesced into one step recomputed
/// from the snapshot the run started on.
/// </summary>
public sealed class MeshNumberField : ObservableObject, IMeshField
{
    private readonly MeshNodeEditor _owner;
    private readonly Func<V3dFile, double> _read;
    private readonly Func<V3dFile, double, V3dFile> _write;
    private readonly Func<string> _editLabel;
    private double _value;
    private bool _interacting;

    public MeshNumberField(
        MeshNodeEditor owner, string label, string? docId, string? extraToolTip,
        Func<V3dFile, double> read, Func<V3dFile, double, V3dFile> write, Func<string> editLabel,
        int decimals = 3, double step = 0.01, double minimum = -1e6, double maximum = 1e6, string suffix = "")
    {
        _owner = owner;
        Label = label;
        _read = read;
        _write = write;
        _editLabel = editLabel;
        Decimals = decimals;
        Step = step;
        Minimum = minimum;
        Maximum = maximum;
        Suffix = suffix;
        ToolTip = MeshFieldText.ToolTip(label, docId, extraToolTip);
        Refresh();
    }

    public string Label { get; }

    /// <summary>The short caption above a compact box ("X", "Yaw"); defaults to <see cref="Label"/>.</summary>
    public string Caption { get => _caption ?? Label; init => _caption = value; }

    private readonly string? _caption;

    public string ToolTip { get; }

    public int Decimals { get; }

    public double Step { get; }

    public double Minimum { get; }

    public double Maximum { get; }

    public string Suffix { get; }

    public bool IsReadOnly => _owner.IsReadOnly;

    /// <summary>True while a coalesced run is in progress.</summary>
    public bool IsInteracting => _interacting;

    public double Value
    {
        get => _value;
        set
        {
            if (!double.IsFinite(value) || IsReadOnly) return;
            double v = Math.Clamp(value, Minimum, Maximum);
            if (Math.Abs(v - _value) < 1e-12) return;
            if (_interacting)
            {
                _owner.UpdateCoalesced(m => _write(m, v));
                _value = v;
                Raise(nameof(Value));
                return;
            }
            _owner.TryApply(_editLabel(), m => _write(m, v));
            Refresh();
        }
    }

    /// <summary>A stepping gesture started: further changes coalesce into one undo step.</summary>
    public void BeginInteraction()
    {
        if (_interacting || IsReadOnly) return;
        _interacting = true;
        _owner.ClearError();
        _owner.Document.BeginEdit(_editLabel());
    }

    /// <summary>The gesture ended.</summary>
    public void EndInteraction()
    {
        if (!_interacting) return;
        _interacting = false;
        _owner.Document.CommitEdit();
        Refresh();
    }

    public void Refresh()
    {
        double v;
        try
        {
            v = _read(_owner.Document.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            v = 0;
        }
        // Shown rounded to the box's digits, and never as "-0".
        _value = double.IsFinite(v) ? Math.Round(v, Math.Clamp(Decimals + 2, 0, 15)) + 0.0 : 0;
        if (Math.Abs(_value) < Math.Pow(10, -Decimals) / 2) _value = 0;
        Raise(nameof(Value));
        Raise(nameof(IsReadOnly));
    }
}

/// <summary>A name of a mesh node; typing commits on Enter or on leaving the box (one undo step).</summary>
public sealed class MeshTextField : ObservableObject, IMeshField
{
    private readonly MeshNodeEditor _owner;
    private readonly Func<V3dFile, string> _read;
    private readonly Func<V3dFile, string, V3dFile> _write;
    private readonly Func<string, string> _editLabel;
    private string _text = string.Empty;

    public MeshTextField(
        MeshNodeEditor owner, string label, string? docId, string? extraToolTip, int maxLength,
        Func<V3dFile, string> read, Func<V3dFile, string, V3dFile> write, Func<string, string> editLabel)
    {
        _owner = owner;
        Label = label;
        MaxLength = maxLength;
        _read = read;
        _write = write;
        _editLabel = editLabel;
        ToolTip = MeshFieldText.ToolTip(label, docId, extraToolTip);
        Refresh();
    }

    public string Label { get; }

    public string ToolTip { get; }

    /// <summary>The longest text the field holds (the box stops there; the Core checks again).</summary>
    public int MaxLength { get; }

    public bool IsReadOnly => _owner.IsReadOnly;

    public string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (IsReadOnly || string.Equals(value, Read(), StringComparison.Ordinal))
            {
                Refresh();
                return;
            }
            _owner.TryApply(_editLabel(value), m => _write(m, value));
            Refresh();
        }
    }

    private string Read()
    {
        try
        {
            return _read(_owner.Document.Current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    public void Refresh()
    {
        _text = Read();
        Raise(nameof(Text));
        Raise(nameof(IsReadOnly));
    }
}

/// <summary>One entry of a <see cref="MeshChoiceField"/>.</summary>
/// <param name="Value">The stored value (a bone index, -1 for none).</param>
/// <param name="Label">What the list shows.</param>
public sealed record MeshChoice(int Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A pick from a list (a bone, or none); picking is one undo step.</summary>
public sealed class MeshChoiceField : ObservableObject, IMeshField
{
    private readonly MeshNodeEditor _owner;
    private readonly Func<V3dFile, int> _read;
    private readonly Func<V3dFile, int, V3dFile> _write;
    private readonly Func<V3dFile, IReadOnlyList<MeshChoice>> _options;
    private readonly Func<MeshChoice, string> _editLabel;
    private MeshChoice? _selected;
    private bool _syncing;

    public MeshChoiceField(
        MeshNodeEditor owner, string label, string? docId, string? extraToolTip,
        Func<V3dFile, IReadOnlyList<MeshChoice>> options, Func<V3dFile, int> read, Func<V3dFile, int, V3dFile> write,
        Func<MeshChoice, string> editLabel)
    {
        _owner = owner;
        Label = label;
        _options = options;
        _read = read;
        _write = write;
        _editLabel = editLabel;
        ToolTip = MeshFieldText.ToolTip(label, docId, extraToolTip);
        Refresh();
    }

    public string Label { get; }

    public string ToolTip { get; }

    public bool IsReadOnly => _owner.IsReadOnly;

    public bool IsEditable => !_owner.IsReadOnly;

    public ObservableCollection<MeshChoice> Options { get; } = [];

    public MeshChoice? Selected
    {
        get => _selected;
        set
        {
            if (_syncing || value is null || ReferenceEquals(value, _selected)) return;
            _selected = value;
            if (!IsReadOnly && value.Value != _read(_owner.Document.Current))
                _owner.TryApply(_editLabel(value), m => _write(m, value.Value));
            // Refresh after the binding finishes, so a refused pick snaps back in the box too.
            _owner.Document.Shell.Dispatcher.BeginInvoke(new Action(Refresh));
        }
    }

    /// <summary>Picks the entry with <paramref name="value"/> (what the view does when the user picks it).</summary>
    public void Pick(int value)
    {
        var choice = Options.FirstOrDefault(o => o.Value == value) ?? new MeshChoice(value, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _selected = null;
        Selected = choice;
        Refresh();
    }

    public void Refresh()
    {
        var mesh = _owner.Document.Current;
        var options = _options(mesh);
        _syncing = true;
        try
        {
            if (!options.Select(o => o.Label).SequenceEqual(Options.Select(o => o.Label)))
            {
                Options.Clear();
                foreach (var o in options) Options.Add(o);
            }
            int value;
            try
            {
                value = _read(mesh);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The node went away (removed, undone) before this editor was replaced: show nothing.
                value = int.MinValue;
            }
            _selected = Options.FirstOrDefault(o => o.Value == value);
            if (_selected is null)
            {
                // An out-of-range index (a damaged file) is shown, not hidden.
                _selected = new MeshChoice(value, $"{value} (missing)");
                Options.Add(_selected);
            }
            Raise(nameof(Selected));
        }
        finally { _syncing = false; }
        RaiseAll(nameof(IsReadOnly), nameof(IsEditable));
    }
}
