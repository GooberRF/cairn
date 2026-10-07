using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Assets;

namespace Cairn.Rfa.Ui.ViewModels.ClipTools;

/// <summary>An entry of a tool's combo box or radio list.</summary>
/// <param name="Value">What it stands for.</param>
/// <param name="Label">The text shown.</param>
/// <param name="Note">A short note shown dimmed beside it, or null.</param>
public sealed record Choice<T>(T Value, string Label, string? Note = null)
{
    public override string ToString() => Label;
}

/// <summary>A library clip in a tool's picker.</summary>
/// <param name="Clip">The library entry.</param>
/// <param name="Note">"tables: stand", "24 bones"…</param>
public sealed record LibraryClipChoice(LibraryClip Clip, string Note)
{
    public string Label => Clip.Name;

    public string ToolTip => $"{Clip.Name} · {Clip.BoneCount} bones · {Clip.Location.DisplayLocation}";

    public override string ToString() => Clip.Name;
}

/// <summary>A library mesh in a tool's picker.</summary>
/// <param name="Mesh">The library entry.</param>
/// <param name="Note">"26 bones · other skeleton"…</param>
public sealed record LibraryMeshChoice(LibraryMesh Mesh, string Note)
{
    public string Label => Mesh.Name;

    public string ToolTip => $"{Mesh.Name} · {Mesh.BoneCount} bones · {Mesh.Location.DisplayLocation}";

    public override string ToString() => Mesh.Name;
}

/// <summary>
/// A picker list with a filter box (substring or wildcard, as the library's): <see cref="Items"/> is
/// the filtered view of <see cref="All"/>; setting <see cref="Selected"/> raises <see cref="SelectionChanged"/>.
/// </summary>
public sealed class FilteredList<T> : ObservableObject where T : class
{
    private readonly Func<T, string> _text;
    private string _filter = string.Empty;
    private T? _selected;

    public FilteredList(IEnumerable<T> items, Func<T, string> text)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        All = [.. items];
        Refresh();
    }

    /// <summary>Every entry, in order.</summary>
    public IReadOnlyList<T> All { get; }

    /// <summary>The entries the filter lets through.</summary>
    public ObservableCollection<T> Items { get; } = [];

    /// <summary>The filter text ("*stand*", "run").</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!Set(ref _filter, value ?? string.Empty)) return;
            Refresh();
        }
    }

    /// <summary>The chosen entry (kept when the filter hides it).</summary>
    public T? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True when there is nothing to pick from at all.</summary>
    public bool IsEmpty => All.Count == 0;

    /// <summary>Raised after <see cref="Selected"/> changes.</summary>
    public event EventHandler? SelectionChanged;

    private void Refresh()
    {
        Items.Clear();
        foreach (var item in All)
        {
            if (LibrarySnapshot.Matches(_text(item), _filter)) Items.Add(item);
        }
    }
}
