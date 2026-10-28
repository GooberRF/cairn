using Cairn.Formats.Rf2;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Where a converted Red Faction II clip may go. The converted clip keeps its <c>.rfa</c> name, so it must never take the
/// place of a Red Faction II clip (told by its first bytes: the source itself or any other) or of a file being converted,
/// not even when replacing was asked for: such a name gets a free one ("walk (2).rfa") and both are kept.
/// </summary>
public static class Rf2ClipOutputs
{
    /// <summary>True when the file at <paramref name="path"/> is a Red Faction II clip by its first bytes; false when it is missing or cannot be read.</summary>
    public static bool IsRf2ClipFile(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 16);
            Span<byte> head = stackalloc byte[8];
            return stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && Rf2AnimationConverter.IsRedFactionII(head);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>"stem (2).ext", "stem (3).ext"…: the first that <paramref name="taken"/> refuses.</summary>
    public static string FreeName(string name, Func<string, bool> taken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(taken);
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// The path a converted file named <paramref name="name"/> is written to in <paramref name="folder"/>: its own name
    /// when that is free, or when replacing and the file there may go; else the first free "stem (n).ext". A file that
    /// must stay (<paramref name="mustKeep"/>, by full path; see <see cref="MustKeepFile"/>) is never replaced.
    /// </summary>
    /// <param name="folder">The output folder.</param>
    /// <param name="name">The output's file name.</param>
    /// <param name="replace">True when a file in the way may be replaced.</param>
    /// <param name="taken">True for a path that holds a file (or another output of the same run).</param>
    /// <param name="mustKeep">True for a path whose file is never replaced.</param>
    public static string FolderPath(string folder, string name, bool replace, Func<string, bool> taken, Func<string, bool> mustKeep)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(taken);
        ArgumentNullException.ThrowIfNull(mustKeep);
        string path = Path.Combine(folder, name);
        if (!mustKeep(path) && (replace || !taken(path))) return path;
        return Path.Combine(folder, FreeName(name, n => Path.Combine(folder, n) is var p && (taken(p) || mustKeep(p))));
    }

    /// <summary>
    /// True when the file at <paramref name="path"/> must not be replaced by a converted file: it is one of
    /// <paramref name="sources"/> (full paths of the files being converted) or a Red Faction II clip.
    /// </summary>
    public static bool MustKeepFile(string path, IReadOnlySet<string> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return (FullPath(path) is { } full && sources.Contains(full)) || IsRf2ClipFile(path);
    }

    /// <summary>The full form of <paramref name="path"/> (for comparing paths), or null when it is not a valid path.</summary>
    public static string? FullPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>A set of full paths (case-insensitive, as Windows compares them).</summary>
    public static HashSet<string> PathSet(IEnumerable<string?> paths) =>
        new(paths.OfType<string>().Select(FullPath).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The loose files behind the entries <paramref name="entryNames"/> of the Red Faction II packfile group at
    /// <paramref name="tocPath"/> (a loose group's files lie below the game folder); none for a packed group, for another
    /// kind of file, or when the group cannot be read.
    /// </summary>
    public static IReadOnlyList<string> LooseFiles(string? tocPath, IEnumerable<string> entryNames)
    {
        ArgumentNullException.ThrowIfNull(entryNames);
        if (!Rf2TocGroup.HasExtension(tocPath) || !File.Exists(tocPath)) return [];
        try
        {
            var group = Rf2TocGroup.Open(tocPath!);
            if (group.IsPacked) return [];
            var wanted = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
            return [.. group.Entries.Where(e => wanted.Contains(e.Name)).Select(e => Rf2TocGroup.LooseFilePath(tocPath!, e)).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// The names converted files are added to a packfile under: a clip whose name is that of an entry being converted
    /// (<paramref name="sourceEntries"/>) or of an existing entry that is a Red Faction II clip gets the first free
    /// "stem (n).ext" among the entries and the other outputs, so both are kept; every other file keeps its name (the add
    /// then replaces or keeps both, as asked).
    /// </summary>
    /// <param name="files">The converted files, in order.</param>
    /// <param name="sourceEntries">The names of the entries being converted.</param>
    /// <param name="existingEntries">Every entry name of the packfile.</param>
    /// <param name="readEntry">Reads an existing entry (null when it cannot be read).</param>
    public static IReadOnlyList<(string Name, byte[] Bytes)> PackfileNames(IReadOnlyList<(string Name, byte[] Bytes)> files,
        IEnumerable<string> sourceEntries, IEnumerable<string> existingEntries, Func<string, byte[]?> readEntry)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(readEntry);
        var sources = new HashSet<string>(sourceEntries, StringComparer.OrdinalIgnoreCase);
        var existing = new HashSet<string>(existingEntries, StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, _) in files) taken.Add(name);
        bool Rf2Entry(string name)
        {
            if (!existing.Contains(name) || !name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) return false;
            try { return readEntry(name) is { } bytes && Rf2AnimationConverter.IsRedFactionII(bytes); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
        }
        var named = new List<(string Name, byte[] Bytes)>(files.Count);
        foreach (var (name, bytes) in files)
        {
            if (!sources.Contains(name) && !Rf2Entry(name)) { named.Add((name, bytes)); continue; }
            string free = FreeName(name, taken.Contains);
            taken.Add(free);
            named.Add((free, bytes));
        }
        return named;
    }
}
