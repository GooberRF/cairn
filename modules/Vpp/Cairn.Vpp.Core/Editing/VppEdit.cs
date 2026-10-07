using Cairn.Vpp.Model;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Editing;

/// <summary>What to do when an added file has the name of an entry already in the packfile.</summary>
public enum VppClashPolicy
{
    /// <summary>The new data replaces the existing entry's (keeping its position).</summary>
    Replace,
    /// <summary>The new entry gets a free name such as "name (2).ext".</summary>
    KeepBoth,
    /// <summary>The new file is not added.</summary>
    Skip,
}

/// <summary>Sort orders for <see cref="VppEdit.Sort"/>.</summary>
public enum VppSortKey
{
    Name,
    Type,
    Size,
    /// <summary>The order of the packfile on disk; added entries follow in the order they were added.</summary>
    OriginalOrder,
}

/// <summary>What an add operation did with each input.</summary>
/// <param name="Added">Names of new entries.</param>
/// <param name="Replaced">Names of existing entries whose data was replaced.</param>
/// <param name="Renamed">Inputs that were added under another name (KeepBoth): (wanted name, name used).</param>
/// <param name="Skipped">Names that clashed and were skipped.</param>
/// <param name="Failed">Inputs that could not be added: (path or name, reason).</param>
public sealed record VppAddReport(
    ImmutableArray<string> Added,
    ImmutableArray<string> Replaced,
    ImmutableArray<(string Wanted, string Used)> Renamed,
    ImmutableArray<string> Skipped,
    ImmutableArray<(string Input, string Reason)> Failed)
{
    public static VppAddReport Empty { get; } = new([], [], [], [], []);

    /// <summary>True when nothing was added or replaced.</summary>
    public bool ChangedNothing => Added.IsEmpty && Replaced.IsEmpty;
}

/// <summary>A package after an add operation, with the report.</summary>
public sealed record VppAddResult(VppPackage Package, VppAddReport Report);

/// <summary>
/// Pure edits of a <see cref="VppPackage"/>. Each returns the same instance when nothing changes, so a
/// caller can skip recording a no-op in its undo history. Name identity ignores case everywhere.
/// </summary>
public static class VppEdit
{
    /// <summary>Adds files from disk (each under its own file name), resolving clashes by <paramref name="policy"/>.</summary>
    public static VppAddResult AddFiles(VppPackage package, IEnumerable<string> paths, VppClashPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(paths);
        var inputs = new List<(string Name, Func<VppSource> Source, string Input)>();
        foreach (string path in paths)
        {
            inputs.Add((VppNames.Normalize(Path.GetFileName(path)), () => FileSource.FromFile(path), path));
        }
        return AddMany(package, inputs, policy);
    }

    /// <summary>
    /// Adds every file under a folder (and its subfolders when <paramref name="recursive"/>), flattened:
    /// packfiles have no folders, so two files of the same name in different subfolders clash.
    /// </summary>
    public static VppAddResult AddFolder(VppPackage package, string folder, VppClashPolicy policy, bool recursive = true)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            return new VppAddResult(package, VppAddReport.Empty with { Failed = [(folder, "the folder does not exist")] });
        }
        var files = Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return AddFiles(package, files, policy);
    }

    /// <summary>Adds an in-memory entry.</summary>
    public static VppAddResult AddBytes(VppPackage package, string name, byte[] bytes, VppClashPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return AddMany(package, [(VppNames.Normalize(name), () => new MemorySource(bytes), name)], policy);
    }

    /// <summary>Adds entries from arbitrary sources (for callers that build their own sources).</summary>
    public static VppAddResult AddSources(VppPackage package, IEnumerable<(string Name, VppSource Source)> entries, VppClashPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return AddMany(package, entries.Select(e => (VppNames.Normalize(e.Name), (Func<VppSource>)(() => e.Source), e.Name)).ToList(), policy);
    }

    private static VppAddResult AddMany(VppPackage package, IReadOnlyList<(string Name, Func<VppSource> Source, string Input)> inputs, VppClashPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(package);
        var items = package.Items.ToBuilder();
        var index = new Dictionary<string, int>(VppNames.Comparer);
        for (int i = 0; i < items.Count; i++) index.TryAdd(items[i].Name, i);
        var added = ImmutableArray.CreateBuilder<string>();
        var replaced = ImmutableArray.CreateBuilder<string>();
        var renamed = ImmutableArray.CreateBuilder<(string, string)>();
        var skipped = ImmutableArray.CreateBuilder<string>();
        var failed = ImmutableArray.CreateBuilder<(string, string)>();

        foreach (var (name, makeSource, input) in inputs)
        {
            if (name.Length == 0) { failed.Add((input, "it has no usable name")); continue; }
            VppSource source;
            try
            {
                source = makeSource();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failed.Add((input, ex.Message));
                continue;
            }
            if (source.Size > int.MaxValue)
            {
                failed.Add((input, $"it is {source.Size:N0} bytes; a packfile entry holds at most {int.MaxValue:N0}"));
                continue;
            }

            if (index.TryGetValue(name, out int at))
            {
                switch (policy)
                {
                    case VppClashPolicy.Skip:
                        skipped.Add(name);
                        continue;
                    case VppClashPolicy.Replace:
                        items[at] = WithSource(items[at], source);
                        replaced.Add(items[at].Name);
                        continue;
                    default:
                        string unique = VppNames.MakeUnique(name, index.ContainsKey);
                        renamed.Add((name, unique));
                        Append(unique);
                        continue;
                }
            }
            Append(name);

            void Append(string entryName)
            {
                index[entryName] = items.Count;
                items.Add(new VppItem(entryName, source, VppItemState.Added));
                added.Add(entryName);
            }
        }

        var report = new VppAddReport(added.ToImmutable(), replaced.ToImmutable(), renamed.ToImmutable(), skipped.ToImmutable(), failed.ToImmutable());
        return new VppAddResult(package.WithItems(items.ToImmutable()), report);
    }

    /// <summary>Replaces an entry's data (keeping its name and position). Unknown names change nothing.</summary>
    public static VppPackage Replace(VppPackage package, string name, VppSource source)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(source);
        int at = package.IndexOf(name);
        if (at < 0 || package.Items[at].Source == source) return package;
        return package.WithItems(package.Items.SetItem(at, WithSource(package.Items[at], source)));
    }

    private static VppItem WithSource(VppItem item, VppSource source) => item with
    {
        Source = source,
        State = item.State == VppItemState.Added ? VppItemState.Added : VppItemState.Replaced,
    };

    /// <summary>Removes the named entries (ignoring case); unknown names are ignored.</summary>
    public static VppPackage Remove(VppPackage package, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(package);
        var remove = new HashSet<string>(names, VppNames.Comparer);
        if (remove.Count == 0) return package;
        var kept = package.Items.Where(i => !remove.Contains(i.Name)).ToImmutableArray();
        return kept.Length == package.Items.Length ? package : package.WithItems(kept);
    }

    /// <summary>
    /// Renames an entry. The new name is normalised; a change of case only is allowed.
    /// </summary>
    /// <exception cref="ArgumentException">The new name is not storable or belongs to another entry.</exception>
    /// <exception cref="KeyNotFoundException">No entry has <paramref name="oldName"/>.</exception>
    public static VppPackage Rename(VppPackage package, string oldName, string newName)
    {
        ArgumentNullException.ThrowIfNull(package);
        int at = package.IndexOf(oldName);
        if (at < 0) throw new KeyNotFoundException($"The packfile has no entry named '{oldName}'.");
        string name = VppNames.Normalize(newName);
        if (VppNames.Validate(name) is { } problem) throw new ArgumentException(problem, nameof(newName));
        var item = package.Items[at];
        if (string.Equals(item.Name, name, StringComparison.Ordinal)) return package;
        int other = package.IndexOf(name);
        if (other >= 0 && other != at) throw new ArgumentException($"Another entry is already named '{package.Items[other].Name}'.", nameof(newName));

        var state = item.State;
        if (state is VppItemState.Original or VppItemState.Renamed)
        {
            state = string.Equals(item.OriginalName, name, StringComparison.Ordinal) ? VppItemState.Original : VppItemState.Renamed;
        }
        return package.WithItems(package.Items.SetItem(at, item with { Name = name, State = state }));
    }

    /// <summary>
    /// Proposes a name of at most <paramref name="maxLength"/> characters for <paramref name="name"/>: the extension is
    /// kept and the stem cut (trailing spaces, dots, dashes and underscores dropped); when that name is taken
    /// (<paramref name="isTaken"/>, case-insensitive like the game), the stem ends in "~1", "~2", ... instead.
    /// Returns the name itself when it already fits.
    /// </summary>
    public static string ShortName(string name, Func<string, bool> isTaken, int maxLength = VppValidator.MaxAssetNameLength)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(isTaken);
        if (name.Length <= maxLength) return name;
        string ext = VppNames.ExtensionOf(name);
        ext = ext.Length >= maxLength - 1 ? string.Empty : name[^ext.Length..]; // keep its case; absurd extension: cut the whole name
        string stem = name[..(name.Length - ext.Length)];
        for (int n = 0; ; n++)
        {
            string suffix = n == 0 ? string.Empty : "~" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            int room = maxLength - ext.Length - suffix.Length;
            string cut = stem[..Math.Min(stem.Length, room)].TrimEnd(' ', '.', '-', '_');
            if (cut.Length == 0) cut = stem[..Math.Min(stem.Length, room)];
            string candidate = cut + suffix + ext;
            if (!isTaken(candidate)) return candidate;
        }
    }

    /// <summary>Renames several entries in one step (for one undo step). Each pair goes through <see cref="Rename"/>.</summary>
    public static VppPackage RenameMany(VppPackage package, IEnumerable<(string OldName, string NewName)> renames)
    {
        ArgumentNullException.ThrowIfNull(renames);
        foreach (var (oldName, newName) in renames) package = Rename(package, oldName, newName);
        return package;
    }

    /// <summary>Reorders the entries. Ties keep their current relative order.</summary>
    public static VppPackage Sort(VppPackage package, VppSortKey key, bool descending = false)
    {
        ArgumentNullException.ThrowIfNull(package);
        var numbered = package.Items.Select((item, i) => (item, i));
        IOrderedEnumerable<(VppItem item, int i)> ordered = key switch
        {
            VppSortKey.Name => By(numbered, x => x.item.Name, StringComparer.OrdinalIgnoreCase, descending),
            VppSortKey.Type => By(numbered, x => x.item.Extension, StringComparer.Ordinal, descending)
                .ThenBy(x => x.item.Name, StringComparer.OrdinalIgnoreCase),
            VppSortKey.Size => By(numbered, x => x.item.Size, Comparer<long>.Default, descending),
            _ => By(numbered, x => x.item.OriginalIndex < 0 ? int.MaxValue : x.item.OriginalIndex, Comparer<int>.Default, descending),
        };
        return package.WithItems(ordered.ThenBy(x => x.i).Select(x => x.item).ToImmutableArray());
    }

    private static IOrderedEnumerable<T> By<T, TKey>(IEnumerable<T> source, Func<T, TKey> key, IComparer<TKey> comparer, bool descending) =>
        descending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);
}
