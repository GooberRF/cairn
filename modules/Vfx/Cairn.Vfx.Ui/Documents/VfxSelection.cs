using Cairn.Ui.Mvvm;
using Cairn.Vfx.Editing;

namespace Cairn.Vfx.Ui.Documents;

/// <summary>
/// What is selected in a VFX document: section indices (into <c>VfxFile.Sections</c>) with a primary one,
/// and a material view index. Later slices add keys and vertices here.
/// </summary>
public sealed class VfxSelection : ObservableObject
{
    private readonly List<int> _sections = [];
    private int _primary = -1;
    private int _material = -1;

    /// <summary>Raised after any change.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<int> Sections => _sections;
    public int Primary => _primary;
    public int Material { get => _material; set { if (Set(ref _material, value)) Changed?.Invoke(this, EventArgs.Empty); } }
    public bool IsEmpty => _sections.Count == 0;
    public bool Contains(int section) => _sections.Contains(section);

    /// <summary>Selects one section (or none with -1); <paramref name="add"/> toggles it in the set.</summary>
    public void Select(int section, bool add = false)
    {
        if (!add) _sections.Clear();
        if (section >= 0)
        {
            if (add && _sections.Remove(section)) section = _sections.Count > 0 ? _sections[^1] : -1;
            else _sections.Add(section);
        }
        _primary = section;
        RaiseAll(nameof(Sections), nameof(Primary), nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---- key selection (timeline): keys are identified by (section, channel, tick) ----
    private readonly HashSet<VfxKeyRef> _keys = [];

    /// <summary>Selected transform keys (timeline). A key is (mesh section, channel, key time in ticks).</summary>
    public IReadOnlyCollection<VfxKeyRef> Keys => _keys;
    public bool ContainsKey(VfxKeyRef key) => _keys.Contains(key);

    /// <summary>Replaces (or with <paramref name="add"/> toggles into) the key selection; raises <see cref="Changed"/>.</summary>
    public void SelectKeys(IEnumerable<VfxKeyRef> keys, bool add = false)
    {
        if (!add) _keys.Clear();
        foreach (var k in keys) if (!(add && _keys.Remove(k))) _keys.Add(k);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops every selected key (no event when already empty).</summary>
    public void ClearKeys() { if (_keys.Count == 0) return; _keys.Clear(); Changed?.Invoke(this, EventArgs.Empty); }

    /// <summary>
    /// Moves selected sections and keys to their new indices after an edit (<paramref name="map"/> returns -1 for a
    /// section that is gone, which is dropped). Raises <see cref="Changed"/> only when something moved or went.
    /// </summary>
    public void Remap(Func<int, int> map)
    {
        var sections = _sections.Select(map).Where(i => i >= 0).Distinct().ToList();
        int primary = _primary >= 0 ? map(_primary) : -1;
        var keys = _keys.Select(k => k with { Section = map(k.Section) }).Where(k => k.Section >= 0).ToList();
        if (primary == _primary && sections.SequenceEqual(_sections) && keys.Count == _keys.Count && keys.All(_keys.Contains)) return;
        _sections.Clear();
        _sections.AddRange(sections);
        _keys.Clear();
        _keys.UnionWith(keys);
        _primary = primary >= 0 && _sections.Contains(primary) ? primary : _sections.Count > 0 ? _sections[^1] : -1;
        RaiseAll(nameof(Sections), nameof(Primary), nameof(IsEmpty));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Drops indices that no longer exist after an edit.</summary>
    public void Clamp(int sectionCount)
    {
        if (_sections.RemoveAll(i => i >= sectionCount) == 0 && _primary < sectionCount) return;
        Select(_sections.Count > 0 ? _sections[^1] : -1);
    }
}

/// <summary>A transform key: mesh section index, channel and key time in ticks (effect frame x 320).</summary>
public readonly record struct VfxKeyRef(int Section, VfxKeyChannel Channel, int Time);
