using System.Buffers.Binary;
using System.Text;

namespace Cairn.Formats.Alpine;

/// <summary>One named blob of an Alpine Faction cache pack.</summary>
/// <param name="Name">The name as stored, byte for byte (Latin-1); never empty.</param>
/// <param name="Tag">The writer's own 64-bit value (what the data was made from; Alpine's map thumbnails store 0).</param>
/// <param name="Offset">Byte offset of the data from the start of the file.</param>
/// <param name="Size">Data length in bytes.</param>
/// <param name="Crc">The data's CRC-32 (zlib's) as stored; data that does not match it is corrupt, and the game does not use it.</param>
public sealed record CachePackEntry(string Name, ulong Tag, long Offset, uint Size, uint Crc);

/// <summary>
/// An Alpine Faction cache pack (<c>.cache</c>, such as <c>alpine_cache\map_thumbs.cache</c> in the game folder): named
/// blobs the game keeps between sessions. Read only: Cairn never writes these.
/// </summary>
/// <remarks>
/// <para>
/// All values are little-endian. A 20-byte header (u32 signature <c>AFCP</c>, u32 version 1, u64 write id, u32 entry
/// count), then the index: per entry a u16 name length, the name's bytes, a u64 tag, a u32 data size and the u32 CRC-32 (zlib's) of the data. The data
/// follows the index, each entry's bytes in index order with no padding, and ends exactly at the end of the file.
/// The game writes a new write id every time it rewrites the pack, which it always does whole.
/// </para>
/// <para>
/// The game treats a pack that breaks any of this as no pack at all (and replaces it on its next save), so the reader
/// is as strict: names must be non-empty and unique (compared byte for byte), the index must fit in the file, and the
/// data sizes must add up to exactly what follows the index. Other versions are refused. An entry whose data does not
/// match its CRC is corrupt (the game does not use it), but the rest of the pack is still valid: see <see cref="DataMatches"/>.
/// </para>
/// </remarks>
public sealed class CachePack
{
    /// <summary>The file extension.</summary>
    public const string Extension = ".cache";

    /// <summary>"AFCP" read as a little-endian u32.</summary>
    public const uint Signature = 0x50434641;

    /// <summary>The only version the game reads.</summary>
    public const uint SupportedVersion = 1;

    /// <summary>Header length in bytes.</summary>
    public const int HeaderBytes = 20;

    // An index entry without its name: u16 name length, u64 tag, u32 data size, u32 data CRC
    private const int EntryFixedBytes = 2 + 8 + 4 + 4;

    private CachePack(ulong writeId, IReadOnlyList<CachePackEntry> entries, long dataStart, long length)
    {
        WriteId = writeId;
        Entries = entries;
        DataStart = dataStart;
        Length = length;
    }

    /// <summary>The value the game changes on every rewrite of the pack.</summary>
    public ulong WriteId { get; }

    /// <summary>The entries in index order.</summary>
    public IReadOnlyList<CachePackEntry> Entries { get; }

    /// <summary>Byte offset of the first entry's data (the header and index length).</summary>
    public long DataStart { get; }

    /// <summary>The file's length in bytes.</summary>
    public long Length { get; }

    /// <summary>Total data bytes of every entry.</summary>
    public long DataBytes => Length - DataStart;

    /// <summary>True when the path ends in <c>.cache</c> (the extension is shared with other programs: check the signature too).</summary>
    public static bool HasExtension(string? path) => path is not null && path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the bytes start with the cache pack signature.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> head) => head.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(head) == Signature;

    /// <summary>Reads a cache pack's index from a file (no entry data is read).</summary>
    /// <exception cref="AssetFormatException">The file is not a cache pack of version 1, or is damaged.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static CachePack Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(stream, Path.GetFileName(path));
    }

    /// <summary>Reads a cache pack's index from the start of a seekable stream (no entry data is read).</summary>
    /// <param name="stream">The whole pack.</param>
    /// <param name="name">The file name used in messages.</param>
    /// <exception cref="AssetFormatException">The stream is not a cache pack of version 1, or is damaged.</exception>
    public static CachePack Read(Stream stream, string name)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        stream.Position = 0;
        var header = new byte[HeaderBytes];
        int got = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (got < 4 || !HasSignature(header))
            throw new AssetFormatException($"'{name}' is not an Alpine Faction cache pack (it does not start with AFCP).");
        if (got < HeaderBytes) throw Damaged(name, $"it is {length:N0} bytes, shorter than the {HeaderBytes}-byte header.");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        if (version != SupportedVersion)
            throw new AssetFormatException($"'{name}' is an Alpine Faction cache pack of version {version}; Cairn reads version {SupportedVersion}.");
        ulong writeId = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        // every entry has a name of at least one byte, so a count the file cannot hold costs nothing
        if (count > (length - HeaderBytes) / (EntryFixedBytes + 1))
            throw Damaged(name, $"it lists {count:N0} entries, more than its {length:N0} bytes can hold.");

        // grown as entries are read, so a count that only just fits the file costs nothing until they exist
        var entries = new List<CachePackEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fixedPart = new byte[8 + 4 + 4];
        var nameBytes = new byte[ushort.MaxValue];
        long position = HeaderBytes, data = 0;
        for (int i = 0; i < count; i++)
        {
            Need(stream, header.AsSpan(0, 2), ref position, length, name, i);
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (nameLength == 0) throw Damaged(name, $"entry {i + 1:N0} of {count:N0} has an empty name.");
            Need(stream, nameBytes.AsSpan(0, nameLength), ref position, length, name, i);
            string entryName = Encoding.Latin1.GetString(nameBytes, 0, nameLength);
            Need(stream, fixedPart, ref position, length, name, i);
            if (!names.Add(entryName)) throw Damaged(name, $"the name '{entryName}' is stored twice.");
            ulong tag = BinaryPrimitives.ReadUInt64LittleEndian(fixedPart);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(8));
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(fixedPart.AsSpan(12));
            entries.Add(new CachePackEntry(entryName, tag, data, size, crc));
            data += size;
        }
        long dataStart = position;
        if (dataStart + data != length)
            throw Damaged(name, $"its entries hold {data:N0} bytes of data, but {length - dataStart:N0} follow the index.");
        for (int i = 0; i < entries.Count; i++) entries[i] = entries[i] with { Offset = dataStart + entries[i].Offset };
        return new CachePack(writeId, entries, dataStart, length);
    }

    /// <summary>True when an entry's data, read from <paramref name="stream"/> (the whole pack), matches its CRC.</summary>
    /// <exception cref="IOException">The data cannot be read.</exception>
    public static bool DataMatches(Stream stream, CachePackEntry entry)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(entry);
        stream.Position = entry.Offset;
        var buffer = new byte[(int)Math.Min(entry.Size, 1 << 16)];
        uint crc = 0;
        for (long left = entry.Size; left > 0;)
        {
            int got = stream.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
            if (got <= 0) return false;
            crc = Crc32(buffer.AsSpan(0, got), crc);
            left -= got;
        }
        return crc == entry.Crc;
    }

    /// <summary>The CRC-32 of <paramref name="data"/> as zlib computes it, continuing from <paramref name="crc"/> (0 to start).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data, uint crc = 0)
    {
        crc = ~crc;
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    private static void Need(Stream stream, Span<byte> buffer, ref long position, long length, string name, int entry)
    {
        if (buffer.Length > length - position || stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) < buffer.Length)
            throw Damaged(name, $"its index ends inside entry {entry + 1:N0} (at offset {position:N0} of {length:N0}).");
        position += buffer.Length;
    }

    private static AssetFormatException Damaged(string name, string reason) => new($"'{name}' is damaged: {reason}");
}
