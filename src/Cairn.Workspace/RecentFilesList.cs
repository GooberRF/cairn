namespace Cairn.Workspace;

/// <summary>
/// The File menu's recent-files list: newest first, no duplicates (paths compare case-insensitively
/// as Windows does), capped at a fixed length. Backed directly by the settings list so adding a
/// file and saving settings is all the app has to do. An item is either a file path or an entry of an
/// archive (<see cref="EntryReference"/>: the archive's path, <see cref="EntrySeparator"/>, the entry's
/// name), so older lists of plain paths load unchanged.
/// </summary>
public sealed class RecentFilesList
{
    /// <summary>Separates an archive path from an entry name in an entry reference ('|' never occurs in a Windows path).</summary>
    public const char EntrySeparator = '|';

    private readonly List<string> _items;

    /// <summary>The item for <paramref name="entryName"/> inside the archive at <paramref name="archivePath"/>.</summary>
    public static string EntryReference(string archivePath, string entryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);
        return NormalizePath(archivePath) + EntrySeparator + entryName;
    }

    /// <summary>True when <paramref name="item"/> is an entry reference; gives the archive path and entry name.</summary>
    public static bool TryParseEntry(string? item, out string archivePath, out string entryName)
    {
        int at = item?.IndexOf(EntrySeparator) ?? -1; // the path cannot hold one; the entry name might
        archivePath = at > 0 ? item![..at] : string.Empty;
        entryName = at > 0 ? item![(at + 1)..] : string.Empty;
        return at > 0 && entryName.Length > 0;
    }

    /// <param name="items">The backing list, usually <see cref="AppSettings.RecentFiles"/>.</param>
    /// <param name="capacity">How many entries to keep.</param>
    public RecentFilesList(List<string> items, int capacity = 12)
    {
        _items = items ?? throw new ArgumentNullException(nameof(items));
        Capacity = Math.Clamp(capacity, 1, 50);
        Trim();
    }

    /// <summary>How many entries are kept.</summary>
    public int Capacity { get; }

    /// <summary>The list, newest first.</summary>
    public IReadOnlyList<string> Items => _items;

    /// <summary>Adds or promotes a file to the top of the list.</summary>
    public void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full = Normalize(path);
        _items.RemoveAll(p => string.Equals(Normalize(p), full, StringComparison.OrdinalIgnoreCase));
        _items.Insert(0, full);
        Trim();
    }

    /// <summary>Removes a file from the list, e.g. after it turns out to be gone.</summary>
    public bool Remove(string path)
    {
        string full = Normalize(path);
        return _items.RemoveAll(p =>
            string.Equals(Normalize(p), full, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    /// <summary>Empties the list.</summary>
    public void Clear() => _items.Clear();

    /// <summary>Drops items whose file (for an archive entry, whose archive) no longer exists.</summary>
    public int RemoveMissing()
    {
        return _items.RemoveAll(p =>
        {
            try { return !File.Exists(TryParseEntry(p, out var archive, out _) ? archive : p); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return true;
            }
        });
    }

    private void Trim()
    {
        if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
    }

    private static string Normalize(string item) =>
        TryParseEntry(item, out var archive, out var entry) ? NormalizePath(archive) + EntrySeparator + entry : NormalizePath(item);

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
