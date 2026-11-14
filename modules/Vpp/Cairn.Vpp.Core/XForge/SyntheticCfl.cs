using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Cairn.Formats.XForge;

namespace Cairn.Vpp.XForge;

/// <summary>How <see cref="SyntheticCfl"/> ends each zlib stream.</summary>
internal enum CflZlibEnd
{
    /// <summary>No Adler-32 trailer, as the stock archive's writer leaves its streams.</summary>
    NoTrailer,
    /// <summary>A complete zlib stream with its Adler-32.</summary>
    Adler,
    /// <summary>An Adler-32 that does not match the data.</summary>
    GarbledAdler,
}

/// <summary>One file for <see cref="SyntheticCfl"/>.</summary>
/// <param name="Name">The name (written as Latin-1).</param>
/// <param name="Data">The unpacked data.</param>
/// <param name="Compression">The compression value: stored, zlib, or any other value (its data is then written as it is).</param>
internal sealed record SyntheticCflEntry(string Name, byte[] Data, uint Compression = CflArchive.Zlib)
{
    /// <summary>The size the directory lists instead of the data's own (a corrupt entry).</summary>
    public uint? ListedSize { get; init; }

    /// <summary>
    /// The index of an earlier entry whose data block this record names instead of a block of its own (records that
    /// share one block; <see cref="Data"/> then only gives the listed size).
    /// </summary>
    public int? SharesBlockOf { get; init; }
}

/// <summary>Options for <see cref="SyntheticCfl"/>.</summary>
internal sealed record SyntheticCflOptions
{
    /// <summary>The directory's compression (stored or zlib; the stock archive's is zlib).</summary>
    public uint DirectoryCompression { get; init; } = CflArchive.Zlib;
    /// <summary>Data the archive is appended to (its offsets stay relative to the archive's start).</summary>
    public byte[] Prefix { get; init; } = [];
    /// <summary>How each zlib stream ends.</summary>
    public CflZlibEnd End { get; init; } = CflZlibEnd.NoTrailer;
    /// <summary>Bytes cut from the end of every zlib entry's stream (after <see cref="End"/>).</summary>
    public int CutBytes { get; init; }
}

/// <summary>
/// Builds X-Forge CFL3 archives in memory for the unit tests and self-tests (no game files), in the layout
/// <see cref="CflArchive"/> documents: header, the entries' data blocks, the directory block, footer. By default every
/// zlib stream is left without its Adler-32 trailer, as the stock archive's are. Cairn itself never writes CFL archives.
/// </summary>
internal static class SyntheticCfl
{
    /// <summary>An archive holding <paramref name="entries"/> in order, with a zlib directory and short zlib streams.</summary>
    public static byte[] Build(params SyntheticCflEntry[] entries) => Build(new SyntheticCflOptions(), entries);

    /// <summary>An archive holding <paramref name="entries"/> in order.</summary>
    public static byte[] Build(SyntheticCflOptions options, params SyntheticCflEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(options);
        using var ms = new MemoryStream();
        ms.Write(options.Prefix);
        long start = ms.Position;
        var b = new byte[4];
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        ms.Write(new byte[CflArchive.HeaderBytes]); // filled in at the end

        using var directory = new MemoryStream();
        var d = new byte[4];
        var offsets = new List<uint>();
        foreach (var e in entries)
        {
            uint offset = e.SharesBlockOf is { } shared ? offsets[shared] : (uint)(ms.Position - start);
            offsets.Add(offset);
            if (e.SharesBlockOf is null)
            {
                byte[] block = e.Compression == CflArchive.Zlib ? ZlibBlock(e.Data, options.End, options.CutBytes) : e.Data;
                U32((uint)block.Length);
                ms.Write(block);
            }
            var name = Encoding.Latin1.GetBytes(e.Name);
            BinaryPrimitives.WriteUInt32LittleEndian(d, e.ListedSize ?? (uint)e.Data.Length); directory.Write(d);
            BinaryPrimitives.WriteUInt32LittleEndian(d, offset); directory.Write(d);
            BinaryPrimitives.WriteUInt32LittleEndian(d, e.Compression); directory.Write(d);
            BinaryPrimitives.WriteUInt16LittleEndian(d, (ushort)name.Length); directory.Write(d, 0, 2);
            directory.Write(name);
        }

        byte[] unpacked = directory.ToArray();
        uint directoryOffset = (uint)(ms.Position - start);
        byte[] directoryBlock = options.DirectoryCompression == CflArchive.Zlib ? ZlibBlock(unpacked, options.End, 0) : unpacked;
        U32(options.DirectoryCompression);
        U32((uint)directoryBlock.Length);
        ms.Write(directoryBlock);
        U32((uint)(ms.Position - start + CflArchive.FooterBytes));
        U32(CflArchive.FooterSignature);

        ms.Position = start;
        U32(CflArchive.Signature);
        U32(directoryOffset);
        U32((uint)unpacked.Length);
        return ms.ToArray();
    }

    /// <summary>A zlib data block: u32 unpacked size, then the zlib stream (2-byte header, deflate data, the chosen end).</summary>
    public static byte[] ZlibBlock(byte[] data, CflZlibEnd end = CflZlibEnd.NoTrailer, int cutBytes = 0)
    {
        using var ms = new MemoryStream();
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)data.Length);
        ms.Write(b);
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        if (end != CflZlibEnd.NoTrailer)
        {
            uint adler = Adler32(data) ^ (end == CflZlibEnd.GarbledAdler ? 0x5A5A5A5Au : 0);
            BinaryPrimitives.WriteUInt32BigEndian(b, adler);
            ms.Write(b);
        }
        ms.SetLength(Math.Max(6, ms.Length - cutBytes));
        return ms.ToArray();
    }

    /// <summary>zlib's Adler-32 of <paramref name="data"/>.</summary>
    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, s = 0;
        foreach (byte x in data)
        {
            a = (a + x) % 65521;
            s = (s + a) % 65521;
        }
        return (s << 16) | a;
    }
}
