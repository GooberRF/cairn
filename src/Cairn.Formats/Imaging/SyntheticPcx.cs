using System.Buffers.Binary;

namespace Cairn.Formats.Imaging;

/// <summary>How <see cref="SyntheticPcx"/> lays a file out, beyond its pixels.</summary>
public sealed record SyntheticPcxOptions
{
    /// <summary>The header's version byte.</summary>
    public int Version { get; init; } = 5;

    /// <summary>Run-length encoded (encoding 1), else stored (encoding 0).</summary>
    public bool Rle { get; init; } = true;

    /// <summary>Encode the whole image as one stream, so runs carry on from one scanline into the next.</summary>
    public bool RunsCrossLines { get; init; }

    /// <summary>The window's left edge (the right edge follows from the width).</summary>
    public int XMin { get; init; }

    /// <summary>The window's top edge (the bottom edge follows from the height).</summary>
    public int YMin { get; init; }

    /// <summary>Bytes per plane line; null for the smallest even length that holds the line.</summary>
    public int? BytesPerLine { get; init; }

    /// <summary>The value of the padding bytes at the end of each plane line.</summary>
    public byte Padding { get; init; }

    /// <summary>For an 8-bit, one-plane image: write the 256-colour palette at the end.</summary>
    public bool EndPalette { get; init; } = true;
}

/// <summary>
/// Writes small PCX files for tests and self-tests (Cairn never writes <c>.pcx</c> files otherwise), in the layout
/// <see cref="PcxCodec"/> reads: any of its forms, with or without run-length encoding, padded lines, an offset
/// window and runs that cross scanlines. Malformed cases are made by editing the bytes this returns.
/// </summary>
public static class SyntheticPcx
{
    /// <summary>A PCX of <paramref name="width"/> x <paramref name="height"/> pixels.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bitsPerPixel">Bits per pixel in each plane (1, 2, 4 or 8).</param>
    /// <param name="planes">Number of planes (3 with 8 bits is 24-bit colour).</param>
    /// <param name="pixel">The pixel at (x, y): a palette index, or 0xRRGGBB for 24-bit colour.</param>
    /// <param name="palette">
    /// RGB triples: the 256-colour palette at the end of an 8-bit image, or the header's 16 colours for fewer
    /// bits; padded with black. Ignored for 24-bit colour.
    /// </param>
    /// <param name="options">The layout; null for version 5, run-length encoded, unpadded beyond an even length.</param>
    public static byte[] Write(int width, int height, int bitsPerPixel, int planes, Func<int, int, int> pixel,
        byte[]? palette = null, SyntheticPcxOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pixel);
        var o = options ?? new SyntheticPcxOptions();
        int minimum = (width * bitsPerPixel + 7) / 8;
        int stride = o.BytesPerLine ?? minimum + (minimum & 1);
        bool trueColour = bitsPerPixel == 8 && planes == 3;

        // The decoded scanlines: each plane's line in turn, then the padding.
        var lines = new byte[height * planes * stride];
        lines.AsSpan().Fill(o.Padding);
        for (int y = 0; y < height; y++)
        {
            for (int p = 0; p < planes; p++)
            {
                var line = lines.AsSpan((y * planes + p) * stride, stride);
                line[..minimum].Clear();
                for (int x = 0; x < width; x++)
                {
                    int value = pixel(x, y);
                    if (trueColour) { line[x] = (byte)(value >> (16 - 8 * p)); continue; }
                    if (bitsPerPixel == 8) { line[x] = (byte)value; continue; }
                    int bits = (value >> (p * bitsPerPixel)) & ((1 << bitsPerPixel) - 1);
                    int at = x * bitsPerPixel;
                    line[at >> 3] |= (byte)(bits << (8 - bitsPerPixel - (at & 7)));
                }
            }
        }

        var header = new byte[PcxCodec.HeaderSize];
        header[0] = PcxCodec.Manufacturer;
        header[1] = (byte)o.Version;
        header[2] = (byte)(o.Rle ? 1 : 0);
        header[3] = (byte)bitsPerPixel;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)o.XMin);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), (ushort)o.YMin);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), (ushort)(o.XMin + width - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)(o.YMin + height - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 72);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(14), 72);
        bool endPalette = bitsPerPixel == 8 && planes == 1;
        if (!endPalette && !trueColour && palette is not null) palette.AsSpan(0, Math.Min(48, palette.Length)).CopyTo(header.AsSpan(16));
        header[65] = (byte)planes;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(66), (ushort)stride);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(68), 1);

        using var ms = new MemoryStream();
        ms.Write(header);
        if (!o.Rle) ms.Write(lines);
        else if (o.RunsCrossLines) Encode(ms, lines);
        else for (int y = 0; y < height * planes; y++) Encode(ms, lines.AsSpan(y * stride, stride));
        if (endPalette && o.EndPalette)
        {
            ms.WriteByte(0x0C);
            var colours = new byte[768];
            palette?.AsSpan(0, Math.Min(768, palette.Length)).CopyTo(colours);
            ms.Write(colours);
        }
        return ms.ToArray();
    }

    /// <summary>ZSoft's run-length encoding: runs of up to 63 as (0xC0 | count, value); a lone byte below 0xC0 as itself.</summary>
    private static void Encode(MemoryStream ms, ReadOnlySpan<byte> data)
    {
        for (int i = 0; i < data.Length;)
        {
            byte value = data[i];
            int run = 1;
            while (run < 63 && i + run < data.Length && data[i + run] == value) run++;
            if (run > 1 || value >= 0xC0) ms.WriteByte((byte)(0xC0 | run));
            ms.WriteByte(value);
            i += run;
        }
    }
}
