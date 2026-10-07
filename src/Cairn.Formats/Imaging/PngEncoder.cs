using System.Buffers.Binary;
using System.IO.Compression;

namespace Cairn.Formats.Imaging;

/// <summary>
/// A minimal PNG writer for <see cref="BgraImage"/>: 8-bit RGBA (or RGB when every pixel is
/// opaque), the "up" filter on every row, zlib deflate. Used by glTF export to turn RF textures
/// (TGA/VBM/DDS) into images every glTF viewer reads. No UI dependency, safe on any thread.
/// </summary>
public static class PngEncoder
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Encodes the image as PNG bytes.</summary>
    public static byte[] Encode(BgraImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        int w = image.Width, h = image.Height;
        var px = image.Pixels;
        bool opaque = true;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 255)
            {
                opaque = false;
                break;
            }
        }
        int channels = opaque ? 3 : 4;
        int rowBytes = w * channels;

        // Raw scanlines: filter byte (2 = up) + the row minus the row above.
        var raw = new byte[(rowBytes + 1) * h];
        var prev = new byte[rowBytes];
        var row = new byte[rowBytes];
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4;
            for (int x = 0, o = 0; x < w; x++, src += 4)
            {
                row[o++] = px[src + 2];
                row[o++] = px[src + 1];
                row[o++] = px[src];
                if (!opaque) row[o++] = px[src + 3];
            }
            int dst = y * (rowBytes + 1);
            raw[dst] = 2;
            for (int i = 0; i < rowBytes; i++) raw[dst + 1 + i] = (byte)(row[i] - prev[i]);
            (prev, row) = (row, prev);
        }

        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(raw);
            compressed = ms.ToArray();
        }

        using var output = new MemoryStream(compressed.Length + 64);
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8;                          // bit depth
        ihdr[9] = (byte)(opaque ? 2 : 6);     // colour type: RGB / RGBA
        WriteChunk(output, "IHDR", ihdr);
        WriteChunk(output, "IDAT", compressed);
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, data.Length);
        s.Write(buf);
        Span<byte> typeBytes = stackalloc byte[4];
        for (int i = 0; i < 4; i++) typeBytes[i] = (byte)type[i];
        s.Write(typeBytes);
        s.Write(data);
        uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        s.Write(buf);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}
