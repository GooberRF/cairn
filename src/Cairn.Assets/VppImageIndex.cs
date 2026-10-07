using Cairn.Formats.Text;

namespace Cairn.Assets;

/// <summary>One image file inside a VPP archive, as the browser lists it.</summary>
/// <param name="Name">The stored filename. VPP directories are flat, so this is the whole name.</param>
/// <param name="Size">The file's length in bytes.</param>
public sealed record VppImageEntry(string Name, int Size);

/// <summary>
/// The image entries of one VPP archive, in natural order, built once so the browser can filter
/// thousands of names without touching the archive again. RF's stock <c>.vpp</c> files hold several
/// thousand entries each and a level designer may have thirty of them on the search path, so this
/// is deliberately a flat array of small records rather than anything the UI has to walk.
/// </summary>
public sealed class VppImageIndex
{
    private VppImageIndex(
        string archivePath, IReadOnlyList<VppImageEntry> images, int totalEntries, bool unreadable)
    {
        ArchivePath = archivePath;
        Images = images;
        TotalEntryCount = totalEntries;
        IsUnreadable = unreadable;
        Names = [.. images.Select(i => i.Name)];
    }

    /// <summary>Full path of the archive on disk.</summary>
    public string ArchivePath { get; }

    /// <summary>The archive's file name, e.g. <c>textures.vpp</c>.</summary>
    public string ArchiveName => Path.GetFileName(ArchivePath);

    /// <summary>Every image entry, in natural order.</summary>
    public IReadOnlyList<VppImageEntry> Images { get; }

    /// <summary>Just the names, in the same order, for the filter and for sequence detection.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>How many entries the archive holds in total, images and everything else.</summary>
    public int TotalEntryCount { get; }

    /// <summary>True when the archive holds no images at all.</summary>
    public bool IsEmpty => Images.Count == 0;

    /// <summary>
    /// True when the archive could not be read at all, as opposed to having been read and found to
    /// hold no images. Both are empty; only one of them is the user's file being broken.
    /// </summary>
    public bool IsUnreadable { get; }

    /// <summary>
    /// The line the browser's archive node shows, e.g. "textures.vpp — 1,204 images". An archive
    /// with nothing in it says so rather than showing a zero.
    /// </summary>
    public string Describe() => IsEmpty
        ? $"{ArchiveName} — no images"
        : $"{ArchiveName} — {Images.Count:N0} image{(Images.Count == 1 ? "" : "s")}";

    /// <summary>
    /// True when a VPP entry is an image this app can read — the design's readable set
    /// (<see cref="ImageProbe.ReadableExtensions"/>), which is also what the browser can preview and
    /// compare against frame 0.
    /// </summary>
    /// <param name="name">The entry name.</param>
    public static bool IsImageEntry(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (string extension in ImageProbe.ReadableExtensions)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Builds the index from an already-open archive. Does not read any file data.</summary>
    /// <param name="archive">The archive whose directory has been read.</param>
    public static VppImageIndex Build(VppArchive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        var images = new List<VppImageEntry>();
        foreach (var entry in archive.Entries)
        {
            if (IsImageEntry(entry.Name)) images.Add(new VppImageEntry(entry.Name, entry.Size));
        }
        images.Sort(static (a, b) => NaturalStringComparer.Instance.Compare(a.Name, b.Name));
        return new VppImageIndex(archive.Path, images, archive.Entries.Count, unreadable: false);
    }

    /// <summary>An index for an archive that could not be read, so the caller can still name it.</summary>
    /// <param name="archivePath">Full path of the archive.</param>
    public static VppImageIndex Unreadable(string archivePath) =>
        new(archivePath, [], 0, unreadable: true);

    /// <summary>
    /// The entries whose names match <paramref name="query"/>. Matching is case-insensitive; a
    /// query containing <c>*</c> or <c>?</c> is treated as a wildcard pattern anchored to the whole
    /// name, anything else as a substring. An empty query matches everything.
    /// </summary>
    /// <param name="query">What the user typed in the filter box.</param>
    public IReadOnlyList<VppImageEntry> Filter(string? query) => VppEntryFilter.Apply(Images, query);
}

/// <summary>
/// The filter behind the VPP browser's search box. Kept out of the view-model so it can be tested
/// and so the matching rule — substring, or wildcard when the query says so — is stated in one
/// place.
/// </summary>
public static class VppEntryFilter
{
    /// <summary>True when the query asks for wildcard matching rather than a substring.</summary>
    /// <param name="query">The raw query text.</param>
    public static bool IsWildcard(string? query) =>
        query is not null && (query.Contains('*', StringComparison.Ordinal)
            || query.Contains('?', StringComparison.Ordinal));

    /// <summary>
    /// True when <paramref name="name"/> matches <paramref name="query"/>. An empty or whitespace
    /// query matches everything, which is how clearing the box restores the whole tree.
    /// </summary>
    /// <param name="name">The entry name.</param>
    /// <param name="query">What the user typed.</param>
    public static bool Matches(string name, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        string trimmed = query.Trim();
        return IsWildcard(trimmed)
            ? WildcardMatches(name, trimmed)
            : name.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Filters a list of entries, preserving order.</summary>
    /// <param name="entries">The entries to filter.</param>
    /// <param name="query">What the user typed.</param>
    public static IReadOnlyList<VppImageEntry> Apply(
        IReadOnlyList<VppImageEntry> entries, string? query)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (string.IsNullOrWhiteSpace(query)) return entries;
        var matches = new List<VppImageEntry>();
        foreach (var entry in entries)
        {
            if (Matches(entry.Name, query)) matches.Add(entry);
        }
        return matches;
    }

    /// <summary>
    /// A whole-name wildcard match with <c>*</c> (any run) and <c>?</c> (one character), written
    /// out rather than compiled to a regex: this runs over tens of thousands of names per keystroke
    /// and a user's query is not a pattern worth building a state machine for.
    /// </summary>
    private static bool WildcardMatches(string name, string pattern)
    {
        int n = 0, p = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;

        static bool Same(char a, char b) => char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}

/// <summary>
/// Turning a VPP entry name into a file that may be written next to the .atx. A .vpp is an
/// untrusted container — it may have come out of a downloaded map pack — and its 60-byte name field
/// can hold anything at all, including <c>..\..\Windows\System32\…</c>. Nothing from an archive is
/// ever written without passing through here first.
/// </summary>
public static class VppExtraction
{
    /// <summary>
    /// True when <paramref name="entryName"/> is a plain file name that is safe to create inside a
    /// folder: no directory separators, no drive or UNC prefix, no <c>.</c>/<c>..</c>, no character
    /// Windows refuses, and not a reserved device name. RF's own file system is flat, so a name
    /// that is not a plain file name is not a legitimate RF asset either.
    /// </summary>
    /// <param name="entryName">The name as it was stored in the archive.</param>
    public static bool IsSafeEntryName(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        string name = entryName;
        if (name.Length > 255) return false;
        if (name != name.Trim()) return false;
        if (name is "." or "..") return false;
        if (name.EndsWith('.') || name.EndsWith(' ')) return false;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

        // Belt and braces: whatever the invalid-character set happens to hold on this platform, a
        // name that is not its own GetFileName is a path, not a file name.
        string bare;
        try { bare = Path.GetFileName(name); }
        catch (ArgumentException) { return false; }
        if (!string.Equals(bare, name, StringComparison.Ordinal)) return false;

        return !IsReservedDeviceName(name);
    }

    /// <summary>
    /// The full path an entry would be written to inside <paramref name="folder"/>, or null when
    /// the name is not safe or the combination does not stay inside that folder.
    /// </summary>
    /// <param name="folder">The destination folder, normally the .atx's own.</param>
    /// <param name="entryName">The archive entry name.</param>
    public static string? DestinationFor(string? folder, string? entryName)
    {
        if (string.IsNullOrWhiteSpace(folder) || !IsSafeEntryName(entryName)) return null;
        try
        {
            string root = Path.GetFullPath(folder);
            string combined = Path.GetFullPath(Path.Combine(root, entryName!));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            return combined.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? combined : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    private static bool IsReservedDeviceName(string name)
    {
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot < 0 ? name : name[..dot];
        foreach (string reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
