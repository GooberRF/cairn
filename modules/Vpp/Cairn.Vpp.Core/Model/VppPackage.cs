using System.Text;
using Cairn.Formats;
using Cairn.Formats.Vpp;

namespace Cairn.Vpp.Model;

/// <summary>Size and timestamp of a packfile when it was opened, so a save can tell that it changed underneath.</summary>
public sealed record VppArchiveStamp(long Length, DateTime LastWriteUtc)
{
    public static VppArchiveStamp Of(string path)
    {
        var info = new FileInfo(path);
        return new VppArchiveStamp(info.Length, info.LastWriteTimeUtc);
    }
}

/// <summary>
/// An immutable packfile: an ordered list of named entries whose data lives in the original archive,
/// in files on disk or in memory. Edits (<see cref="Editing.VppEdit"/>) return new packages, so a
/// document can keep them in an undo history. Names are compared ignoring case, as the game does.
/// </summary>
public sealed class VppPackage
{
    private Dictionary<string, int>? _index;

    /// <summary>Creates a package from parts. <paramref name="path"/> is null for a packfile that was never saved.</summary>
    public VppPackage(string? path, ImmutableArray<VppItem> items, VppArchiveStamp? stamp = null)
    {
        Path = path;
        Items = items.IsDefault ? [] : items;
        Stamp = stamp;
    }

    /// <summary>An empty, never-saved packfile.</summary>
    public static VppPackage Empty { get; } = new(null, []);

    /// <summary>The packfile on disk this package was opened from or last saved to; null when new.</summary>
    public string? Path { get; }

    /// <summary>The entries in directory order.</summary>
    public ImmutableArray<VppItem> Items { get; }

    /// <summary>Length and timestamp of <see cref="Path"/> when it was read; null for a new package.</summary>
    public VppArchiveStamp? Stamp { get; }

    /// <summary>Number of entries.</summary>
    public int Count => Items.Length;

    /// <summary>Total data bytes (without padding).</summary>
    public long DataBytes => Items.Sum(i => i.Size);

    /// <summary>The size the packfile will have when written: header, directory and data, each padded to 2048 bytes.</summary>
    public long ArchiveBytes => ComputeArchiveBytes(Items.Select(i => i.Size), Items.Length);

    /// <summary>True when any entry was added, replaced, renamed, removed or moved since the package was read.</summary>
    public bool IsModified
    {
        get
        {
            if (Path is null) return true;
            for (int i = 0; i < Items.Length; i++)
            {
                if (Items[i].State != VppItemState.Original || Items[i].OriginalIndex != i) return true;
            }
            return OriginalCount != Items.Length;
        }
    }

    /// <summary>Entry count of the directory on disk when the package was read.</summary>
    public int OriginalCount { get; init; }

    /// <summary>Finds an entry by name (ignoring case); -1 when absent.</summary>
    public int IndexOf(string name)
    {
        var index = _index;
        if (index is null)
        {
            index = new Dictionary<string, int>(Items.Length, Editing.VppNames.Comparer);
            for (int i = 0; i < Items.Length; i++) index.TryAdd(Items[i].Name, i);
            _index = index;
        }
        return index.TryGetValue(name, out int at) ? at : -1;
    }

    /// <summary>True when an entry has this name (ignoring case).</summary>
    public bool Contains(string name) => IndexOf(name) >= 0;

    /// <summary>The entry with this name (ignoring case), or null.</summary>
    public VppItem? Find(string name) => IndexOf(name) is int i and >= 0 ? Items[i] : null;

    /// <summary>A package with other entries and the same path and stamp; this instance when the list is the same.</summary>
    public VppPackage WithItems(ImmutableArray<VppItem> items)
    {
        if (items == Items) return this;
        if (items.Length == Items.Length)
        {
            bool same = true;
            for (int i = 0; i < items.Length && same; i++) same = ReferenceEquals(items[i], Items[i]) || items[i] == Items[i];
            if (same) return this;
        }
        return new VppPackage(Path, items, Stamp) { OriginalCount = OriginalCount, HeaderArchiveSize = HeaderArchiveSize };
    }

    /// <summary>The size a packfile with these entry sizes occupies on disk.</summary>
    public static long ComputeArchiveBytes(IEnumerable<long> sizes, int count)
    {
        long total = VppArchive.BlockSize + AlignUp((long)count * VppArchive.EntryBytes);
        foreach (long size in sizes) total += AlignUp(size);
        return total;
    }

    /// <summary>Rounds up to the 2048-byte block size.</summary>
    public static long AlignUp(long value) => (value + VppArchive.BlockSize - 1) / VppArchive.BlockSize * VppArchive.BlockSize;

    /// <summary>
    /// Opens a packfile's directory (no file data is read). Names are taken byte for byte (Latin-1) so
    /// that an unmodified packfile writes back identically.
    /// </summary>
    /// <exception cref="AssetFormatException">The file is not a readable RF1 packfile.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static VppPackage Open(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        var stamp = VppArchiveStamp.Of(full);
        var archive = VppArchive.Read(stream, full);
        var names = ReadRawNames(stream, archive.Entries.Count, full);
        stream.Position = 12;
        var sizeField = new byte[4];
        stream.ReadExactly(sizeField);
        var items = ImmutableArray.CreateBuilder<VppItem>(archive.Entries.Count);
        for (int i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            string name = names[i];
            items.Add(new VppItem(name, new ArchiveSource(full, entry.Offset, entry.Size), VppItemState.Original)
            {
                OriginalName = name,
                OriginalIndex = i,
            });
        }
        int count = items.Count;
        return new VppPackage(full, items.MoveToImmutable(), stamp)
        {
            OriginalCount = count,
            HeaderArchiveSize = BitConverter.ToUInt32(sizeField),
        };
    }

    /// <summary>The archive-size field of the header when the package was read (the game never checks it).</summary>
    public long? HeaderArchiveSize { get; init; }

    /// <summary>
    /// True when the entry list is exactly the packfile's on disk (same entries, names, sizes and order),
    /// so the writer may copy the original directory slack and keep the file byte-identical.
    /// </summary>
    public bool HasOriginalDirectory
    {
        get
        {
            if (Path is null || Items.Length != OriginalCount) return false;
            for (int i = 0; i < Items.Length; i++)
            {
                var item = Items[i];
                if (item.State != VppItemState.Original || item.OriginalIndex != i || item.Source is not ArchiveSource a
                    || !string.Equals(a.ArchivePath, Path, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// The shared reader decodes names as trimmed ASCII; the editor needs them exactly as stored
    /// (Latin-1, untrimmed) so they survive a re-save. The directory is small, so it is read again.
    /// </summary>
    private static string[] ReadRawNames(Stream stream, int count, string path)
    {
        stream.Position = VppArchive.BlockSize;
        var directory = new byte[count * VppArchive.EntryBytes];
        int read = 0;
        while (read < directory.Length)
        {
            int n = stream.Read(directory, read, directory.Length - read);
            if (n <= 0) throw new VppFormatException($"'{path}' ends before its directory does.");
            read += n;
        }
        var names = new string[count];
        for (int i = 0; i < count; i++)
        {
            int at = i * VppArchive.EntryBytes;
            int nul = Array.IndexOf(directory, (byte)0, at, VppArchive.NameBytes);
            int length = nul < 0 ? VppArchive.NameBytes : nul - at;
            names[i] = Encoding.Latin1.GetString(directory, at, length);
        }
        return names;
    }
}
