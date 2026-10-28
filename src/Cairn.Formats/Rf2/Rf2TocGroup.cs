using System.Buffers.Binary;
using System.Text;

namespace Cairn.Formats.Rf2;

/// <summary>One file listed by a Red Faction II packfile group.</summary>
/// <param name="Name">The file's name (no folders).</param>
/// <param name="Size">Its length in bytes as the group lists it.</param>
/// <param name="Offset">Packed groups: the byte offset of its data in the <c>.packfile</c>; loose groups: -1.</param>
/// <param name="StoredPath">Loose groups: its path relative to the game folder (backslashes, as stored); packed groups: null.</param>
public sealed record Rf2TocEntry(string Name, long Size, long Offset, string? StoredPath);

/// <summary>
/// A Red Faction II packfile group (<c>pc_media\&lt;name&gt;.toc_group</c>): the list of files the game loads as one
/// group, either packed into one <c>.packfile</c> (raw data back to back, no header) or kept as loose files below the
/// game folder. Read only: Cairn never writes these.
/// </summary>
/// <remarks>
/// <para>
/// The file has no signature and no version. All values are little-endian and every string is NUL-terminated
/// single-byte text: the group name, the group's folder (<c>pc_media\&lt;name&gt;\</c>), a 32-bit flags value and a
/// second 32-bit value. Flags bit 1 (value 2) means packed; bit 2 (value 4) is set on some groups and its meaning is
/// unknown, so it is only shown.
/// </para>
/// <para>
/// <b>Packed</b>: the second value is unknown (1 or 0 in the stock files). Then the packfile's path relative to the
/// game folder, the file count, and per file its name, its u32 size and its u32 offset into the packfile.
/// <b>Loose</b>: the second value is the file count; per file its name, its u32 size and its path relative to the
/// game folder.
/// </para>
/// <para>
/// There being no signature, the reader is strict instead: every count must fit the bytes left, every string must be
/// terminated inside the file and printable, stored paths must stay below the game folder, and the last entry must
/// end exactly at the end of the file. Packed offsets are checked against the packfile with
/// <see cref="CheckPackfile"/>, which needs only its length.
/// </para>
/// </remarks>
public sealed class Rf2TocGroup
{
    /// <summary>The group file's extension.</summary>
    public const string Extension = ".toc_group";

    /// <summary>The packed data file's extension.</summary>
    public const string PackfileExtension = ".packfile";

    /// <summary>Flags bit 1: the files are packed into one <c>.packfile</c>.</summary>
    public const uint PackedFlag = 2;

    /// <summary>The largest group file read (the stock ones are at most a few hundred KB).</summary>
    public const int MaxTocBytes = 64 << 20;

    // Smallest entry: a one-character name and its NUL, the size, then the offset (packed) or a one-character path
    // and its NUL (loose).
    private const int MinPackedEntryBytes = 2 + 4 + 4;
    private const int MinLooseEntryBytes = 2 + 4 + 2;

    private Rf2TocGroup(string groupName, string folder, uint flags, uint second, string? packfilePath, IReadOnlyList<Rf2TocEntry> entries, int byteLength)
    {
        GroupName = groupName;
        Folder = folder;
        Flags = flags;
        Second = second;
        PackfilePath = packfilePath;
        Entries = entries;
        ByteLength = byteLength;
    }

    /// <summary>The group's name (the stock files match their own base names).</summary>
    public string GroupName { get; }

    /// <summary>The group's folder as stored (<c>pc_media\&lt;name&gt;\</c>).</summary>
    public string Folder { get; }

    /// <summary>The flags value, raw (bit 1 = packed; bit 2 unknown).</summary>
    public uint Flags { get; }

    /// <summary>The second header value, raw: the file count of a loose group; unknown for a packed one.</summary>
    public uint Second { get; }

    /// <summary>True when the files are packed into one <c>.packfile</c>.</summary>
    public bool IsPacked => (Flags & PackedFlag) != 0;

    /// <summary>Packed groups: the packfile's path relative to the game folder, as stored; loose groups: null.</summary>
    public string? PackfilePath { get; }

    /// <summary>The files in the group's order.</summary>
    public IReadOnlyList<Rf2TocEntry> Entries { get; }

    /// <summary>The group file's length in bytes (the whole file was read).</summary>
    public int ByteLength { get; }

    /// <summary>The sum of the listed sizes.</summary>
    public long DataBytes => Entries.Sum(e => e.Size);

    /// <summary>True for a name ending in <c>.toc_group</c> (the only way to tell: the format has no signature).</summary>
    public static bool HasExtension(string? path) => path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a group file from disk.</summary>
    /// <exception cref="AssetFormatException">The file is not a readable packfile group.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static Rf2TocGroup Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(path);
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096))
        {
            if (stream.Length > MaxTocBytes) throw NotAGroup(name, $"it is {stream.Length:N0} bytes, far larger than any packfile group");
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        return Read(bytes, name);
    }

    /// <summary>Reads a group file's bytes.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <exception cref="AssetFormatException">The bytes are not a readable packfile group.</exception>
    public static Rf2TocGroup Read(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        name ??= "packfile group";
        if (bytes.Length > MaxTocBytes) throw NotAGroup(name, $"it is {bytes.Length:N0} bytes, far larger than any packfile group");
        var r = new Reader(bytes, name);
        string group = r.Text("the group name", path: false);
        string folder = r.Text("the group folder", path: true);
        uint flags = r.U32("the flags");
        uint second = r.U32("the second header value");
        string? packfile = null;
        uint count;
        bool packed = (flags & PackedFlag) != 0;
        if (packed)
        {
            packfile = r.Text("the packfile path", path: true);
            count = r.U32("the file count");
        }
        else count = second;
        if (count > (bytes.Length - r.Position) / (packed ? MinPackedEntryBytes : MinLooseEntryBytes))
            throw NotAGroup(name, $"it lists {count:N0} files, more than its remaining {bytes.Length - r.Position:N0} bytes can hold");
        var entries = new Rf2TocEntry[count];
        for (int i = 0; i < entries.Length; i++)
        {
            string what = $"file {i + 1:N0}";
            string entryName = r.Text($"the name of {what}", path: false);
            uint size = r.U32($"the size of {entryName}");
            if (packed) entries[i] = new Rf2TocEntry(entryName, size, r.U32($"the offset of {entryName}"), null);
            else entries[i] = new Rf2TocEntry(entryName, size, -1, r.Text($"the path of {entryName}", path: true));
        }
        if (r.Position != bytes.Length)
            throw NotAGroup(name, $"{bytes.Length - r.Position:N0} bytes follow its last entry (at offset {r.Position:N0})");
        return new Rf2TocGroup(group, folder, flags, second, packfile, entries, bytes.Length);
    }

    /// <summary>
    /// Checks every packed entry against the packfile's length: each must lie inside it. Loose groups pass.
    /// </summary>
    /// <param name="packfileLength">The packfile's length in bytes.</param>
    /// <param name="packfileName">The packfile's name, for the message.</param>
    /// <exception cref="AssetFormatException">An entry runs past the end of the packfile.</exception>
    public void CheckPackfile(long packfileLength, string packfileName)
    {
        if (!IsPacked) return;
        foreach (var e in Entries)
        {
            if (e.Offset < 0 || e.Offset > packfileLength || e.Size > packfileLength - e.Offset)
                throw new AssetFormatException($"'{packfileName}' does not match its group: {e.Name} is listed at offset {e.Offset:N0} with {e.Size:N0} bytes, "
                    + $"but the packfile is only {packfileLength:N0} bytes long.");
        }
    }

    /// <summary>
    /// The game folder a group file belongs to: the parent of the folder it is in (<c>&lt;game&gt;\pc_media\x.toc_group</c>).
    /// Null when the group file is at a drive's root.
    /// </summary>
    public static string? GameFolderOf(string tocPath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(tocPath));
        return folder is null ? null : Path.GetDirectoryName(folder);
    }

    /// <summary>
    /// Where a packed group's <c>.packfile</c> may be, in the order to try: in the group's own folder beside the group
    /// file (<c>&lt;toc folder&gt;\&lt;group&gt;\&lt;name&gt;.packfile</c>), directly beside the group file, then by the stored
    /// path below the game folder. Empty for a loose group.
    /// </summary>
    public IReadOnlyList<string> PackfileCandidates(string tocPath)
    {
        if (PackfilePath is not { } stored) return [];
        string tocFolder = Path.GetDirectoryName(Path.GetFullPath(tocPath)) ?? string.Empty;
        string file = LastSegment(stored);
        var list = new List<string> { Path.Combine(tocFolder, GroupName, file), Path.Combine(tocFolder, file) };
        if (GameFolderOf(tocPath) is { } game) list.Add(Path.Combine(game, ToLocal(stored)));
        return [.. list.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The first of <see cref="PackfileCandidates"/> that exists, or null.</summary>
    public string? FindPackfile(string tocPath) => PackfileCandidates(tocPath).FirstOrDefault(File.Exists);

    /// <summary>A loose entry's file: its stored path below the game folder. Null for a packed entry.</summary>
    public static string? LooseFilePath(string tocPath, Rf2TocEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.StoredPath is not { } stored) return null;
        string game = GameFolderOf(tocPath) ?? Path.GetDirectoryName(Path.GetFullPath(tocPath)) ?? string.Empty;
        return Path.GetFullPath(Path.Combine(game, ToLocal(stored)));
    }

    private static string ToLocal(string stored) => stored.Replace('/', '\\').Replace('\\', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);

    private static string LastSegment(string stored)
    {
        string trimmed = stored.TrimEnd('\\', '/');
        int cut = trimmed.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? trimmed : trimmed[(cut + 1)..];
    }

    private static AssetFormatException NotAGroup(string name, string why) =>
        new($"'{name}' is not a readable Red Faction II packfile group: {why}.");

    /// <summary>The strict reader: printable NUL-terminated strings and 32-bit values, all inside the file.</summary>
    private sealed class Reader(byte[] bytes, string name)
    {
        public int Position { get; private set; }

        public uint U32(string what)
        {
            if (bytes.Length - Position < 4) throw NotAGroup(name, $"it ends inside {what} (offset {Position:N0})");
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Position));
            Position += 4;
            return value;
        }

        /// <summary>
        /// A NUL-terminated string of printable ASCII (never empty). Paths must also stay below the game folder: no
        /// drive, no leading separator, no ".." segment, and none of the characters Windows refuses in paths.
        /// </summary>
        public string Text(string what, bool path)
        {
            int start = Position;
            int nul = Array.IndexOf(bytes, (byte)0, start);
            if (nul < 0) throw NotAGroup(name, $"{what} at offset {start:N0} is not terminated");
            if (nul == start) throw NotAGroup(name, $"{what} at offset {start:N0} is empty");
            for (int i = start; i < nul; i++)
            {
                byte b = bytes[i];
                if (b is < 0x20 or > 0x7E)
                    throw NotAGroup(name, $"{what} at offset {start:N0} holds the byte 0x{b:X2}, which is not printable text");
            }
            string text = Encoding.ASCII.GetString(bytes, start, nul - start);
            if (path)
            {
                bool rooted = text[0] is '\\' or '/' || text.Contains(':', StringComparison.Ordinal);
                bool escapes = text.Split('\\', '/').Any(s => s == "..");
                if (rooted || escapes || text.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
                    throw NotAGroup(name, $"{what} at offset {start:N0} (\"{text}\") is not a path inside the game folder");
            }
            Position = nul + 1;
            return text;
        }
    }
}
