using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Cairn.Formats.XForge;

/// <summary>One file of an X-Forge CFL archive, as its directory lists it.</summary>
/// <param name="Name">The name as stored, byte for byte (Latin-1); never empty, never with control characters.</param>
/// <param name="Offset">Byte offset of the entry's data block from the start of the file (the archive's start plus the stored offset).</param>
/// <param name="UnpackedSize">The size the directory lists: what the entry holds once unpacked.</param>
/// <param name="Compression">The stored compression value: <see cref="CflArchive.Stored"/>, <see cref="CflArchive.Zlib"/>, or a plug-in's value Cairn does not read.</param>
/// <param name="PackedSize">The data block's own length field: the bytes stored after it (for zlib, the unpacked-size field and the stream).</param>
public sealed record CflEntry(string Name, long Offset, uint UnpackedSize, uint Compression, uint PackedSize)
{
    /// <summary>True when Cairn reads the entry's compression (stored or zlib).</summary>
    public bool IsSupported => Compression is CflArchive.Stored or CflArchive.Zlib;

    /// <summary>"stored", "zlib", or "compression N (not supported)".</summary>
    public string CompressionText => CflArchive.CompressionName(Compression);

    /// <summary>
    /// The most bytes checking or reading the entry unpacks: a stored entry's block, a zlib entry's listed size or less
    /// when its block cannot hold that much (it is refused before unpacking then), nothing for another compression.
    /// </summary>
    public long MaxUnpackBytes => Compression switch
    {
        CflArchive.Stored => PackedSize,
        CflArchive.Zlib => PackedSize < 6 ? 0 : Math.Min(UnpackedSize, (PackedSize - 6L) * CflArchive.MaxDeflateRatio + 1024),
        _ => 0,
    };
}

/// <summary>
/// An X-Forge "Compressed File Library" of version 3 (<c>.cfl</c>), the archive of Fathammer's X-Forge engine (Red
/// Faction for N-Gage keeps every asset in <c>redfaction.cfl</c>). Read only: Cairn never writes these.
/// </summary>
/// <remarks>
/// <para>
/// All values are little-endian and every offset is relative to the start of the archive. A 12-byte header (u32
/// signature <c>CFL3</c>, u32 directory offset, u32 directory unpacked size) and an 8-byte footer (u32 total archive
/// size, u32 signature <c>3CFL</c>). The footer lets an archive be appended to another file: when its total is smaller
/// than the file, the archive starts that many bytes before the end.
/// </para>
/// <para>
/// At the directory offset: u32 compression, then a data block. Every entry's data is a data block too: u32 length,
/// then that many bytes. A stored block (compression 0) is the data itself; a zlib block (compression 1) is a u32
/// unpacked size and a zlib stream. The unpacked directory is a run of records to its end: u32 unpacked size, u32 data
/// offset, u32 compression, u16 name length, the name (no terminator).
/// </para>
/// <para>
/// The archive's zlib writer leaves its streams short (the stock archive's have no Adler-32 trailer at all), so the
/// streams are inflated as raw deflate data after their 2-byte header, and an entry is checked against its listed
/// size, never against the checksum. CFL3 allows other compressors and ciphers as plug-ins: an entry with any other
/// compression value is listed, but reading it fails ("compression N is not supported").
/// </para>
/// <para>
/// The reader is strict about the structure (offsets, sizes and names must lie inside the archive and the directory
/// must unpack to exactly its size) and refuses a file that breaks it. An entry whose data does not unpack to its listed
/// size is corrupt, but the rest of the archive is still valid: see <see cref="Check"/>. Names may be listed twice (the
/// stock archive lists nine twice); <see cref="Find"/> takes the first.
/// </para>
/// </remarks>
public sealed class CflArchive
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".cfl";

    /// <summary>"CFL3" read as a little-endian u32.</summary>
    public const uint Signature = 0x334C4643;

    /// <summary>"3CFL" read as a little-endian u32: the footer's signature.</summary>
    public const uint FooterSignature = 0x4C464333;

    /// <summary>Header length in bytes.</summary>
    public const int HeaderBytes = 12;

    /// <summary>Footer length in bytes.</summary>
    public const int FooterBytes = 8;

    /// <summary>The compression value of data stored as it is.</summary>
    public const uint Stored = 0;

    /// <summary>The compression value of zlib data.</summary>
    public const uint Zlib = 1;

    /// <summary>The largest unpacked directory Cairn reads (the stock archive's is 39,780 bytes).</summary>
    public const int MaxDirectoryBytes = 64 << 20;

    // A directory record without its name: u32 unpacked size, u32 data offset, u32 compression, u16 name length
    private const int RecordFixedBytes = 4 + 4 + 4 + 2;

    // Deflate cannot expand data by more than about 1032 to 1, so a larger listed size is refused before allocating.
    internal const long MaxDeflateRatio = 1032;

    private CflArchive(string name, long start, long length, long directoryOffset, uint directoryCompression, uint directoryPackedSize, uint directoryUnpackedSize, IReadOnlyList<CflEntry> entries)
    {
        Name = name;
        Start = start;
        Length = length;
        DirectoryOffset = directoryOffset;
        DirectoryCompression = directoryCompression;
        DirectoryPackedSize = directoryPackedSize;
        DirectoryUnpackedSize = directoryUnpackedSize;
        Entries = entries;
    }

    /// <summary>The file name used in messages.</summary>
    public string Name { get; }

    /// <summary>Byte offset of the archive in the file: 0, or the length of the data it was appended to.</summary>
    public long Start { get; }

    /// <summary>The archive's length in bytes (the footer's total).</summary>
    public long Length { get; }

    /// <summary>Byte offset of the directory block from the start of the file.</summary>
    public long DirectoryOffset { get; }

    /// <summary>The directory's compression value (stored or zlib).</summary>
    public uint DirectoryCompression { get; }

    /// <summary>The directory block's length field.</summary>
    public uint DirectoryPackedSize { get; }

    /// <summary>The directory's unpacked size (header).</summary>
    public uint DirectoryUnpackedSize { get; }

    /// <summary>The entries in directory order.</summary>
    public IReadOnlyList<CflEntry> Entries { get; }

    /// <summary>True when the path ends in <c>.cfl</c>.</summary>
    public static bool HasExtension(string? path) => path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the bytes start with the <c>CFL3</c> signature.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;

    /// <summary>"stored", "zlib", or "compression N (not supported)".</summary>
    public static string CompressionName(uint compression) => compression switch
    {
        Stored => "stored",
        Zlib => "zlib",
        _ => string.Create(CultureInfo.InvariantCulture, $"compression {compression} (not supported)"),
    };

    /// <summary>
    /// A cheap check of a seekable stream: true when it ends with the <c>3CFL</c> footer and has the <c>CFL3</c> header
    /// where the footer says the archive starts. Nothing else is read; <see cref="Read"/> checks the rest.
    /// </summary>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static bool Probe(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        if (length < HeaderBytes + FooterBytes) return false;
        Span<byte> buffer = stackalloc byte[FooterBytes];
        stream.Position = length - FooterBytes;
        if (stream.ReadAtLeast(buffer, FooterBytes, throwOnEndOfStream: false) < FooterBytes) return false;
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]) != FooterSignature || total < HeaderBytes + FooterBytes || total > length) return false;
        stream.Position = length - total;
        return stream.ReadAtLeast(buffer[..4], 4, throwOnEndOfStream: false) == 4 && HasSignature(buffer);
    }

    /// <summary>Reads an archive's directory from a file (no entry data is read).</summary>
    /// <exception cref="AssetFormatException">The file is not a CFL3 archive, or is damaged.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static CflArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        // a small buffer: the directory walk reads 4 bytes at each record's offset, and a large one would be read for each
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        return Read(stream, Path.GetFileName(path));
    }

    /// <summary>Reads an archive's directory from a seekable stream holding it at its end (no entry data is read).</summary>
    /// <param name="stream">The file: the archive alone, or appended to other data.</param>
    /// <param name="name">The file name used in messages.</param>
    /// <exception cref="AssetFormatException">The stream is not a CFL3 archive, or is damaged.</exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public static CflArchive Read(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(name);
        long length = stream.Length;
        var head = new byte[HeaderBytes];
        stream.Position = 0;
        int got = stream.ReadAtLeast(head, 4, throwOnEndOfStream: false);
        bool startsAsCfl = got >= 4 && HasSignature(head);
        if (length < HeaderBytes + FooterBytes)
            throw startsAsCfl ? Damaged(name, $"it is {length:N0} bytes, shorter than the {HeaderBytes}-byte header and {FooterBytes}-byte footer.") : NotCfl(name);

        var footer = new byte[FooterBytes];
        ReadAt(stream, length - FooterBytes, footer, name, "its footer");
        uint total = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(4)) != FooterSignature)
        {
            if (startsAsCfl) throw Damaged(name, "it does not end with the 3CFL footer (the file is cut short or was changed).");
            if (got >= 4 && head[0] == 'C' && head[1] == 'F' && head[2] == 'L')
                throw new AssetFormatException($"'{name}' is an X-Forge CFL archive of version '{(char)head[3]}'; Cairn reads CFL3.");
            throw NotCfl(name);
        }
        if (total < HeaderBytes + FooterBytes || total > length)
            throw Damaged(name, $"its footer gives an archive size of {total:N0} bytes, but the file is {length:N0} bytes.");
        long start = length - total;
        ReadAt(stream, start, head, name, "its header");
        if (!HasSignature(head))
        {
            throw start == 0 ? Damaged(name, "it ends with the 3CFL footer but does not start with the CFL3 header.")
                : Damaged(name, $"its footer says the archive starts at offset {start:N0}, but there is no CFL3 header there.");
        }
        uint directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(4));
        uint directoryUnpacked = BinaryPrimitives.ReadUInt32LittleEndian(head.AsSpan(8));

        // every block lies between the header and the footer
        long end = total - FooterBytes;
        if (directoryOffset < HeaderBytes || directoryOffset > end - 8)
            throw Damaged(name, $"its directory offset {directoryOffset:N0} is outside the archive ({HeaderBytes:N0} to {end:N0}).");
        var block = new byte[8];
        ReadAt(stream, start + directoryOffset, block, name, "its directory");
        uint directoryCompression = BinaryPrimitives.ReadUInt32LittleEndian(block);
        uint directoryPacked = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(4));
        if (directoryPacked > end - directoryOffset - 8)
            throw Damaged(name, $"its directory ({directoryPacked:N0} bytes at offset {directoryOffset:N0}) runs past the end of the archive.");
        if (directoryUnpacked > MaxDirectoryBytes)
            throw Damaged(name, $"its directory unpacks to {directoryUnpacked:N0} bytes, more than the {MaxDirectoryBytes:N0} Cairn reads.");
        if (directoryCompression is not (Stored or Zlib))
            throw new AssetFormatException($"'{name}' cannot be read: its directory uses compression {directoryCompression}, which is not supported.");
        var packed = new byte[directoryPacked];
        ReadAt(stream, start + directoryOffset + 8, packed, name, "its directory");
        byte[] directory = Unpack(packed, directoryCompression, directoryUnpacked, out string? problem)
            ?? throw Damaged(name, "its directory " + problem);

        var entries = new List<CflEntry>();
        var size = new byte[4];
        int at = 0;
        while (at < directory.Length)
        {
            int record = entries.Count + 1;
            if (directory.Length - at < RecordFixedBytes) throw Damaged(name, $"its directory ends inside record {record:N0}.");
            uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 4));
            uint compression = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(at + 8));
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(directory.AsSpan(at + 12));
            at += RecordFixedBytes;
            if (nameLength == 0) throw Damaged(name, $"record {record:N0} of its directory has an empty name.");
            if (nameLength > directory.Length - at) throw Damaged(name, $"the name of record {record:N0} runs past the end of its directory.");
            string entryName = Encoding.Latin1.GetString(directory, at, nameLength);
            at += nameLength;
            if (entryName.Any(c => c < ' ' || c is >= '\x7F' and <= '\x9F'))
                throw Damaged(name, $"record {record:N0} has a name with control characters ({Printable(entryName)}).");
            if (offset < HeaderBytes || offset > end - 4)
                throw Damaged(name, $"{entryName} has its data at offset {offset:N0}, outside the archive ({HeaderBytes:N0} to {end:N0}).");
            ReadAt(stream, start + offset, size, name, entryName);
            uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(size);
            if (packedSize > end - offset - 4)
                throw Damaged(name, $"the data of {entryName} ({packedSize:N0} bytes at offset {offset:N0}) runs past the end of the archive.");
            entries.Add(new CflEntry(entryName, start + offset, unpacked, compression, packedSize));
        }
        return new CflArchive(name, start, total, start + directoryOffset, directoryCompression, directoryPacked, directoryUnpacked, entries);
    }

    /// <summary>The first entry with this name, ignoring case (the first listed when a name is listed twice), or null.</summary>
    public CflEntry? Find(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var e in Entries) if (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>Reads and unpacks an entry from <paramref name="stream"/> (the whole file).</summary>
    /// <exception cref="AssetFormatException">
    /// The entry's compression is not supported ("compression N is not supported"), or its data is corrupt (it does not
    /// unpack to exactly its listed size, or its block does not match the directory).
    /// </exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public byte[] ReadEntry(Stream stream, CflEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsSupported) throw new AssetFormatException($"'{entry.Name}' in '{Name}' cannot be read: compression {entry.Compression} is not supported.");
        return TryRead(stream, entry, out string? problem) ?? throw new AssetFormatException($"'{entry.Name}' in '{Name}' is corrupt: {problem}");
    }

    /// <summary>An entry's unpacked data as a read-only stream (see <see cref="ReadEntry"/>).</summary>
    /// <exception cref="AssetFormatException">The entry cannot be read; the message says why.</exception>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public Stream OpenEntry(Stream stream, CflEntry entry) => new MemoryStream(ReadEntry(stream, entry), writable: false);

    /// <summary>
    /// Null when an entry reads and unpacks to exactly its listed size; otherwise why not, as a sentence fragment
    /// ("compression 2 is not supported", "it unpacks to 10 bytes, not the 12 listed"). The entry is unpacked a piece at
    /// a time, so only its packed block is held, never its unpacked data.
    /// </summary>
    /// <param name="stream">The whole file.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="hash">When given, the unpacked data is added to it as it is checked (meaningful only when the result is null).</param>
    /// <exception cref="IOException">The stream cannot be read.</exception>
    public string? Check(Stream stream, CflEntry entry, IncrementalHash? hash = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.IsSupported) return string.Create(CultureInfo.InvariantCulture, $"compression {entry.Compression} is not supported");
        if (ReadBlock(stream, entry) is not { } block) return "the file ends inside its data";
        return Unpack(block, entry.Compression, entry.UnpackedSize, keep: false, hash, out _) is { } problem ? "it " + problem : null;
    }

    private static byte[]? TryRead(Stream stream, CflEntry entry, out string? problem)
    {
        if (ReadBlock(stream, entry) is not { } block)
        {
            problem = "the file ends inside its data";
            return null;
        }
        var data = Unpack(block, entry.Compression, entry.UnpackedSize, out problem);
        if (problem is not null) problem = "it " + problem;
        return data;
    }

    /// <summary>An entry's data block (the bytes after its length field), or null when the file ends inside it.</summary>
    private static byte[]? ReadBlock(Stream stream, CflEntry entry)
    {
        // Read already checked that the block lies inside the archive.
        var block = new byte[entry.PackedSize];
        stream.Position = entry.Offset + 4;
        return stream.ReadAtLeast(block, block.Length, throwOnEndOfStream: false) < block.Length ? null : block;
    }

    /// <summary>A stored or zlib block's data when it is exactly <paramref name="size"/> bytes, else null and why.</summary>
    private static byte[]? Unpack(byte[] block, uint compression, uint size, out string? problem)
    {
        problem = Unpack(block, compression, size, keep: true, hash: null, out byte[]? data);
        return data;
    }

    /// <summary>
    /// Null when a stored or zlib block unpacks to exactly <paramref name="size"/> bytes, else why not. With
    /// <paramref name="keep"/>, <paramref name="data"/> is then the data; without, the data is unpacked a piece at a time
    /// (nothing of its size is allocated) and each piece is added to <paramref name="hash"/> when one is given.
    /// </summary>
    private static string? Unpack(byte[] block, uint compression, uint size, bool keep, IncrementalHash? hash, out byte[]? data)
    {
        data = null;
        if (compression == Stored)
        {
            if (block.Length != size) return string.Create(CultureInfo.CurrentCulture, $"is stored with {block.Length:N0} bytes, but {size:N0} are listed");
            if (keep) data = block;
            else hash?.AppendData(block);
            return null;
        }
        // a zlib block: u32 unpacked size, then the zlib stream (2-byte header, deflate data, an Adler-32 the writer leaves short)
        if (block.Length < 4 + 2) return string.Create(CultureInfo.CurrentCulture, $"holds {block.Length:N0} bytes, too few for zlib data");
        if (BinaryPrimitives.ReadUInt32LittleEndian(block) != size)
            return string.Create(CultureInfo.CurrentCulture, $"says it unpacks to {BinaryPrimitives.ReadUInt32LittleEndian(block):N0} bytes, but {size:N0} are listed");
        byte cmf = block[4], flg = block[5];
        if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
            return string.Create(CultureInfo.InvariantCulture, $"does not start with a zlib header (0x{cmf:X2} 0x{flg:X2})");
        long deflated = block.Length - 6;
        if (size > Array.MaxLength || size > deflated * MaxDeflateRatio + 1024)
            return string.Create(CultureInfo.CurrentCulture, $"lists {size:N0} bytes, more than {deflated:N0} bytes of deflate data can hold");
        // kept: the whole entry; checked: one piece at a time
        var buffer = keep ? new byte[size] : new byte[(int)Math.Clamp(size, 1, 1 << 16)];
        long read = 0;
        bool longer = false;
        try
        {
            using var inflater = new DeflateStream(new MemoryStream(block, 6, block.Length - 6, writable: false), CompressionMode.Decompress);
            while (read < size)
            {
                int n = keep ? inflater.Read(buffer, (int)read, (int)(size - read)) : inflater.Read(buffer, 0, (int)Math.Min(buffer.Length, size - read));
                if (n <= 0) break;
                if (!keep) hash?.AppendData(buffer, 0, n);
                read += n;
            }
            if (read == size) longer = inflater.Read(new byte[1], 0, 1) > 0;
        }
        catch (InvalidDataException)
        {
            // a stream damaged past what it unpacked: the size check below decides
        }
        if (read == size && !longer)
        {
            if (keep) data = buffer;
            return null;
        }
        return longer
            ? string.Create(CultureInfo.CurrentCulture, $"unpacks to more than the {size:N0} bytes listed")
            : string.Create(CultureInfo.CurrentCulture, $"unpacks to {read:N0} bytes, not the {size:N0} listed");
    }

    private static void ReadAt(Stream stream, long offset, byte[] buffer, string name, string what)
    {
        stream.Position = offset;
        if (stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw Damaged(name, $"the file ends inside {what} (at offset {offset:N0}).");
    }

    /// <summary>The name with control characters shown as \xNN.</summary>
    private static string Printable(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c < ' ' || c is >= '\x7F' and <= '\x9F') sb.Append(CultureInfo.InvariantCulture, $"\\x{(int)c:X2}");
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static AssetFormatException NotCfl(string name) =>
        new($"'{name}' is not an X-Forge CFL archive (it does not end with the 3CFL footer).");

    private static AssetFormatException Damaged(string name, string reason) => new($"'{name}' is damaged: {reason}");
}
