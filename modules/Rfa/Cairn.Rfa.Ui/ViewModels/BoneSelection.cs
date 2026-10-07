namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The selected bones of a document, by index, in selection order (the last one is the "active" bone
/// the bone inspector of phase 5 shows). The viewport writes it when a joint is clicked, the Problems
/// panel when a bone location is chosen, the mesh structure tree when a bone node is selected; the
/// timeline and the bone/key inspectors of later phases read and write the same object.
/// </summary>
public sealed class BoneSelection
{
    private readonly List<int> _bones = [];

    /// <summary>Raised after any change.</summary>
    public event EventHandler? Changed;

    /// <summary>Selected bone indices, in selection order.</summary>
    public IReadOnlyList<int> Bones => _bones;

    /// <summary>The most recently selected bone, or -1.</summary>
    public int Active => _bones.Count > 0 ? _bones[^1] : -1;

    /// <summary>Number of selected bones.</summary>
    public int Count => _bones.Count;

    /// <summary>True when <paramref name="bone"/> is selected.</summary>
    public bool Contains(int bone) => _bones.Contains(bone);

    /// <summary>Selects exactly <paramref name="bones"/>.</summary>
    public void Set(IEnumerable<int> bones)
    {
        ArgumentNullException.ThrowIfNull(bones);
        var next = bones.Distinct().ToList();
        if (next.SequenceEqual(_bones)) return;
        _bones.Clear();
        _bones.AddRange(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects one bone (or nothing for -1).</summary>
    public void Select(int bone) => Set(bone >= 0 ? [bone] : []);

    /// <summary>Adds or removes one bone (Ctrl+click).</summary>
    public void Toggle(int bone)
    {
        if (bone < 0) return;
        if (!_bones.Remove(bone)) _bones.Add(bone);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears the selection.</summary>
    public void Clear() => Set([]);

    /// <summary>Drops indices at or beyond <paramref name="count"/> (the skeleton shrank).</summary>
    public void Clamp(int count) => Set(_bones.Where(b => b < count));
}
