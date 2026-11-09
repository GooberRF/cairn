using System.Buffers.Binary;
using System.Text;
using Cairn.Formats.Alpine;

namespace Cairn.Vpp.Alpine;

/// <summary>
/// Builds Alpine Faction cache packs in memory for the unit tests and self-tests (no game files), in the layout
/// <see cref="CachePack"/> documents. Cairn itself never writes cache packs.
/// </summary>
internal static class SyntheticCachePack
{
    /// <summary>A version 1 pack holding <paramref name="entries"/> in order (names are written as Latin-1).</summary>
    public static byte[] Build(ulong writeId, params (string Name, ulong Tag, byte[] Data)[] entries) =>
        Build(CachePack.Signature, CachePack.SupportedVersion, writeId, entries);

    /// <summary>A pack with any signature and version (for the reader's refusals).</summary>
    public static byte[] Build(uint signature, uint version, ulong writeId, params (string Name, ulong Tag, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        var b = new byte[8];
        void U16(int v) { BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v); ms.Write(b, 0, 2); }
        void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(b, v); ms.Write(b, 0, 4); }
        void U64(ulong v) { BinaryPrimitives.WriteUInt64LittleEndian(b, v); ms.Write(b, 0, 8); }
        U32(signature);
        U32(version);
        U64(writeId);
        U32((uint)entries.Length);
        foreach (var (name, tag, data) in entries)
        {
            var bytes = Encoding.Latin1.GetBytes(name);
            U16(bytes.Length);
            ms.Write(bytes);
            U64(tag);
            U32((uint)data.Length);
            U32(CachePack.Crc32(data));
        }
        foreach (var (_, _, data) in entries) ms.Write(data);
        return ms.ToArray();
    }

    /// <summary>The start of a JPEG file (SOI and an APP0 marker): enough for the type to be recognised, not decodable.</summary>
    public static byte[] JpegHead() => [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01];
}
