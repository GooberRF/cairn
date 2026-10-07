using System.Collections.Immutable;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Editing;

/// <summary>Which of a bone's two key lists a key lives in.</summary>
public enum KeyKind
{
    /// <summary>A rotation key (<see cref="RfaRotKey"/>).</summary>
    Rotation,
    /// <summary>A position key (<see cref="RfaPosKey"/>).</summary>
    Position,
}

/// <summary>Addresses one key of a clip: the bone index, the key list and the key's index in it.</summary>
/// <param name="Bone">Bone (track) index.</param>
/// <param name="Kind">Rotation or position list.</param>
/// <param name="Index">Index of the key in that list.</param>
public readonly record struct KeyRef(int Bone, KeyKind Kind, int Index) : IComparable<KeyRef>
{
    /// <summary>Orders by bone, then kind (rotation first), then index.</summary>
    public int CompareTo(KeyRef other)
    {
        int c = Bone.CompareTo(other.Bone);
        if (c != 0) return c;
        c = Kind.CompareTo(other.Kind);
        return c != 0 ? c : Index.CompareTo(other.Index);
    }

    /// <summary>True when this key exists in <paramref name="clip"/>.</summary>
    public bool IsValidIn(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if ((uint)Bone >= (uint)clip.BoneCount) return false;
        var track = clip.Bones[Bone];
        int count = Kind == KeyKind.Rotation ? track.RotationKeys.Length : track.PositionKeys.Length;
        return (uint)Index < (uint)count;
    }

    /// <summary>The key's time in <paramref name="clip"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The key does not exist.</exception>
    public int TimeIn(RfaClip clip)
    {
        if (!IsValidIn(clip)) throw new ArgumentOutOfRangeException(nameof(clip), $"Key {this} is not in the clip.");
        var track = clip.Bones[Bone];
        return Kind == KeyKind.Rotation ? track.RotationKeys[Index].Time : track.PositionKeys[Index].Time;
    }
}

/// <summary>
/// An immutable set of keys of one clip, kept sorted and free of duplicates. Selections address keys
/// by index, so an edit that inserts or removes keys returns (or the caller rebuilds) a new selection;
/// <see cref="Validate"/> drops entries that no longer exist.
/// </summary>
public sealed record KeySelection
{
    private KeySelection(ImmutableArray<KeyRef> keys) => Keys = keys;

    /// <summary>Nothing selected.</summary>
    public static KeySelection Empty { get; } = new([]);

    /// <summary>The selected keys, sorted by bone, kind and index, without duplicates.</summary>
    public ImmutableArray<KeyRef> Keys { get; }

    /// <summary>Number of selected keys.</summary>
    public int Count => Keys.Length;

    /// <summary>True when nothing is selected.</summary>
    public bool IsEmpty => Keys.IsDefaultOrEmpty;

    /// <summary>A selection of the given keys (sorted and de-duplicated).</summary>
    public static KeySelection Of(IEnumerable<KeyRef> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var set = new SortedSet<KeyRef>(keys);
        return set.Count == 0 ? Empty : new KeySelection([.. set]);
    }

    /// <summary>A selection of the given keys.</summary>
    public static KeySelection Of(params KeyRef[] keys) => Of((IEnumerable<KeyRef>)keys);

    /// <summary>Every key of the clip, or only of the given kinds.</summary>
    public static KeySelection All(RfaClip clip, bool rotations = true, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Bones(clip, Enumerable.Range(0, clip.BoneCount), rotations, positions);
    }

    /// <summary>Every key of the given bones.</summary>
    public static KeySelection Bones(RfaClip clip, IEnumerable<int> bones, bool rotations = true, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(bones);
        var list = new List<KeyRef>();
        foreach (int b in bones)
        {
            if ((uint)b >= (uint)clip.BoneCount) continue;
            var track = clip.Bones[b];
            if (rotations) for (int i = 0; i < track.RotationKeys.Length; i++) list.Add(new KeyRef(b, KeyKind.Rotation, i));
            if (positions) for (int i = 0; i < track.PositionKeys.Length; i++) list.Add(new KeyRef(b, KeyKind.Position, i));
        }
        return Of(list);
    }

    /// <summary>
    /// Every key whose time lies in [<paramref name="from"/>, <paramref name="to"/>] (inclusive), on
    /// the given bones (all bones when null).
    /// </summary>
    public static KeySelection InTimeRange(
        RfaClip clip, int from, int to, IEnumerable<int>? bones = null, bool rotations = true, bool positions = true)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var list = new List<KeyRef>();
        foreach (int b in bones ?? Enumerable.Range(0, clip.BoneCount))
        {
            if ((uint)b >= (uint)clip.BoneCount) continue;
            var track = clip.Bones[b];
            if (rotations)
            {
                for (int i = 0; i < track.RotationKeys.Length; i++)
                {
                    int t = track.RotationKeys[i].Time;
                    if (t >= from && t <= to) list.Add(new KeyRef(b, KeyKind.Rotation, i));
                }
            }
            if (positions)
            {
                for (int i = 0; i < track.PositionKeys.Length; i++)
                {
                    int t = track.PositionKeys[i].Time;
                    if (t >= from && t <= to) list.Add(new KeyRef(b, KeyKind.Position, i));
                }
            }
        }
        return Of(list);
    }

    /// <summary>True when the key is selected.</summary>
    public bool Contains(KeyRef key) => !IsEmpty && Keys.BinarySearch(key) >= 0;

    /// <summary>The selected keys of one bone and kind, as ascending indices.</summary>
    public IEnumerable<int> IndicesOf(int bone, KeyKind kind)
    {
        foreach (var k in Keys)
        {
            if (k.Bone == bone && k.Kind == kind) yield return k.Index;
        }
    }

    /// <summary>The distinct bones that have at least one selected key, ascending.</summary>
    public IEnumerable<int> SelectedBones => Keys.Select(k => k.Bone).Distinct();

    /// <summary>This selection with the keys that do not exist in <paramref name="clip"/> removed.</summary>
    public KeySelection Validate(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (IsEmpty) return this;
        var kept = Keys.Where(k => k.IsValidIn(clip)).ToImmutableArray();
        return kept.Length == Keys.Length ? this : kept.Length == 0 ? Empty : new KeySelection(kept);
    }

    /// <summary>The union of two selections.</summary>
    public KeySelection Union(KeySelection other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Of(Keys.Concat(other.Keys));
    }

    /// <summary>The earliest and latest selected key time, or null when nothing (valid) is selected.</summary>
    public (int Min, int Max)? TimeSpan(RfaClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int min = int.MaxValue, max = int.MinValue;
        foreach (var k in Keys)
        {
            if (!k.IsValidIn(clip)) continue;
            int t = k.TimeIn(clip);
            if (t < min) min = t;
            if (t > max) max = t;
        }
        return min <= max ? (min, max) : null;
    }
}
