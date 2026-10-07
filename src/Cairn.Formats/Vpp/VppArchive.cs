namespace Cairn.Formats.Vpp;

/// <summary>One file inside a VPP archive.</summary>
/// <param name="Name">The stored filename (no folders; RF's file system is flat).</param>
/// <param name="Offset">Byte offset of the file's data in the archive.</param>
/// <param name="Size">Length of the file's data in bytes.</param>
public sealed record VppEntry(string Name, long Offset, int Size);

/// <summary>Thrown when a .vpp file cannot be read as an RF1 version 1 archive.</summary>
public sealed class VppFormatException(string message) : AssetFormatException(message);

/// <summary>
/// Reader for Red Faction 1 VPP archives (version 1): a 2048-byte header block, then 64-byte
/// directory entries (a 60-byte NUL-padded name and a 32-bit size) padded up to a 2048-byte
/// boundary, then each file's data padded to 2048 bytes.
/// </summary>
public sealed class VppArchive
{
    /// <summary>The archive signature, 0x51890ACE.</summary>
    public const uint Signature = 0x51890ACE;

    /// <summary>Every offset and block in a VPP is aligned to this.</summary>
    public const int BlockSize = 2048;

    /// <summary>Bytes reserved for an entry's name.</summary>
    public const int NameBytes = 60;

    /// <summary>Size of one directory entry.</summary>
    public const int EntryBytes = 64;

    private const int MaxFiles = 0x100000;

    private readonly Dictionary<string, VppEntry> _byName;

    private VppArchive(string path, uint version, IReadOnlyList<VppEntry> entries)
    {
        Path = path;
        Version = version;
        Entries = entries;
        _byName = new Dictionary<string, VppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries) _byName.TryAdd(e.Name, e);
    }

    /// <summary>Full path to the archive on disk.</summary>
    public string Path { get; }

    /// <summary>The version field from the header (RF1 archives are version 1).</summary>
    public uint Version { get; }

    /// <summary>Every entry, in directory order.</summary>
    public IReadOnlyList<VppEntry> Entries { get; }

    /// <summary>Reads an archive's directory. Only headers are read; file data stays on disk.</summary>
    /// <exception cref="VppFormatException">The file is not a readable RF1 VPP.</exception>
    public static VppArchive Open(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, path);
    }

    /// <summary>Reads an archive's directory from a stream.</summary>
    public static VppArchive Read(Stream stream, string path)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = ReadExactly(stream, BlockSize, path);
        uint signature = BitConverter.ToUInt32(header, 0);
        if (signature != Signature)
            throw new VppFormatException($"'{path}' is not a VPP archive (signature 0x{signature:X8}).");
        uint version = BitConverter.ToUInt32(header, 4);
        if (version < 1)
            throw new VppFormatException($"'{path}' declares unsupported VPP version {version}.");
        uint fileCount = BitConverter.ToUInt32(header, 8);
        if (fileCount > MaxFiles)
            throw new VppFormatException($"'{path}' declares {fileCount} files, which is not plausible.");

        // The count is the first thing a hostile or truncated archive gets wrong, and the directory
        // buffer is sized from it: a two-kilobyte stub claiming a million files would otherwise have
        // us reserve 64 MB before finding out the file ends immediately. Check it against the space
        // the file actually has first.
        long available = stream.CanSeek ? stream.Length - stream.Position : long.MaxValue;
        if ((long)fileCount * EntryBytes > Math.Max(0, available))
        {
            throw new VppFormatException(
                $"'{path}' declares {fileCount} files but is not big enough to hold their directory.");
        }

        int directoryBytes = AlignUp((int)fileCount * EntryBytes);
        var directory = ReadExactly(stream, directoryBytes, path);

        var entries = new List<VppEntry>((int)fileCount);
        long offset = BlockSize + directoryBytes;
        for (int i = 0; i < fileCount; i++)
        {
            int at = i * EntryBytes;
            int nul = Array.IndexOf(directory, (byte)0, at, NameBytes);
            int length = nul < 0 ? NameBytes : nul - at;
            // Names are bytes in the Windows ANSI code page; Latin-1 maps every byte to one character, so
            // names such as "Grüße.tga" survive (ASCII would turn them into '?').
            string name = System.Text.Encoding.Latin1.GetString(directory, at, length).Trim();
            int size = BitConverter.ToInt32(directory, at + NameBytes);
            if (size < 0) throw new VppFormatException($"'{path}' entry '{name}' has a negative size.");
            entries.Add(new VppEntry(name, offset, size));
            offset += AlignUp(size);
        }
        return new VppArchive(path, version, entries);
    }

    /// <summary>
    /// Rebuilds an archive's directory from its entry names and sizes in directory order (as a cache
    /// stores them), without touching the file: offsets follow from the sizes exactly as
    /// <see cref="Read"/> computes them. The caller is responsible for knowing the file is unchanged.
    /// </summary>
    /// <exception cref="VppFormatException">The lists disagree, a size is negative, or there are implausibly many entries.</exception>
    public static VppArchive FromDirectory(string path, uint version, IReadOnlyList<string> names, IReadOnlyList<int> sizes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(sizes);
        if (names.Count != sizes.Count) throw new VppFormatException($"'{path}': {names.Count} names but {sizes.Count} sizes.");
        if (names.Count > MaxFiles) throw new VppFormatException($"'{path}' lists {names.Count} files, which is not plausible.");
        var entries = new List<VppEntry>(names.Count);
        long offset = BlockSize + AlignUp(names.Count * EntryBytes);
        for (int i = 0; i < names.Count; i++)
        {
            if (sizes[i] < 0 || names[i] is null) throw new VppFormatException($"'{path}' entry {i} is not valid.");
            entries.Add(new VppEntry(names[i], offset, sizes[i]));
            offset += AlignUp(sizes[i]);
        }
        return new VppArchive(path, version, entries);
    }

    /// <summary>Finds an entry by name, ignoring case.</summary>
    public bool TryGetEntry(string name, out VppEntry entry) => _byName.TryGetValue(name, out entry!);

    /// <summary>Opens an entry's bytes as a fresh, independent read-only stream.</summary>
    public Stream OpenEntry(VppEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var stream = File.OpenRead(Path);
        try
        {
            return new SubStream(stream, entry.Offset, entry.Size);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Opens an entry by name, or returns null when the archive does not contain it.</summary>
    public Stream? OpenEntry(string name) => TryGetEntry(name, out var entry) ? OpenEntry(entry) : null;

    /// <summary>Reads an entry's bytes into memory.</summary>
    /// <exception cref="VppFormatException">The archive ends before the entry does.</exception>
    public byte[] ReadEntry(VppEntry entry)
    {
        using var stream = OpenEntry(entry);
        // The size comes from the directory: check it against the archive before allocating by it.
        long fileLength = new FileInfo(Path).Length;
        if (entry.Offset > fileLength || entry.Size > fileLength - entry.Offset)
        {
            throw new VppFormatException(
                $"'{Path}' ends before entry '{entry.Name}' does (it needs {entry.Size} bytes at offset {entry.Offset}; the archive is {fileLength} bytes).");
        }
        var bytes = new byte[entry.Size];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = stream.Read(bytes, read, bytes.Length - read);
            if (n <= 0) throw new VppFormatException($"'{Path}' ends before entry '{entry.Name}' does.");
            read += n;
        }
        return bytes;
    }

    private static int AlignUp(int value)
    {
        long aligned = ((long)value + BlockSize - 1) / BlockSize * BlockSize;
        return aligned > int.MaxValue ? int.MaxValue : (int)aligned;
    }

    private static byte[] ReadExactly(Stream stream, int count, string path)
    {
        var buffer = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n <= 0) throw new VppFormatException($"'{path}' ends before its directory does.");
            read += n;
        }
        return buffer;
    }

    /// <summary>A read-only window onto part of another stream, which it owns.</summary>
    private sealed class SubStream(Stream inner, long offset, long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int start, int count)
        {
            if (_position >= length) return 0;
            int want = (int)Math.Min(count, length - _position);
            inner.Position = offset + _position;
            int read = inner.Read(buffer, start, want);
            _position += read;
            return read;
        }

        public override long Seek(long target, SeekOrigin origin)
        {
            long next = origin switch
            {
                SeekOrigin.Begin => target,
                SeekOrigin.Current => _position + target,
                _ => length + target,
            };
            _position = Math.Clamp(next, 0, length);
            return _position;
        }

        public override void Flush() { }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
