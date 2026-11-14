namespace Cairn.Formats.Imaging;

/// <summary>
/// Reads ZSoft PCX files, versions 0 to 5, run-length encoded (encoding 1) or, rarely, stored
/// (encoding 0): 8 bits in one plane with the 256-colour palette at the end of the file, 24-bit
/// colour as three 8-bit planes, and 1, 2 and 4-bit images (planar or packed, up to 16 colours)
/// that take their colours from the header's 16-colour palette. Cairn reads PCX and never writes it.
/// <para>
/// Every image decodes opaque. Whether a game treats one palette index as transparent is the
/// caller's business: pass <c>colorKeyIndex</c> to <see cref="Decode"/>. Nothing passes one yet,
/// because no colour key has been established for the files Cairn meets (the N-Gage release's
/// textures among them).
/// </para>
/// </summary>
public static class PcxCodec
{
    /// <summary>Size of the fixed header that starts every PCX file.</summary>
    public const int HeaderSize = 128;

    /// <summary>The first byte of every PCX file (ZSoft's manufacturer id).</summary>
    public const byte Manufacturer = 0x0A;

    /// <summary>The byte before the 256-colour palette at the end of an 8-bit file.</summary>
    private const byte PaletteMarker = 0x0C;

    /// <summary>The 256-colour palette at the end of the file: the marker byte and 256 RGB triples.</summary>
    private const int EndPaletteSize = 1 + 256 * 3;

    /// <summary>The most bytes one run-length pair (two bytes) can produce.</summary>
    private const int MaxRun = 63;

    /// <summary>
    /// The 16 colours a version 3 file ("no palette information") is drawn with: the standard EGA
    /// palette, as RGB triples.
    /// </summary>
    private static readonly byte[] DefaultEgaPalette =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0xAA, 0x00, 0xAA, 0x00, 0x00, 0xAA, 0xAA,
        0xAA, 0x00, 0x00, 0xAA, 0x00, 0xAA, 0xAA, 0x55, 0x00, 0xAA, 0xAA, 0xAA,
        0x55, 0x55, 0x55, 0x55, 0x55, 0xFF, 0x55, 0xFF, 0x55, 0x55, 0xFF, 0xFF,
        0xFF, 0x55, 0x55, 0xFF, 0x55, 0xFF, 0xFF, 0xFF, 0x55, 0xFF, 0xFF, 0xFF,
    ];

    private sealed record Header(
        int Version, int Encoding, int BitsPerPixel, int XMin, int YMin, int XMax, int YMax,
        int Planes, int BytesPerLine)
    {
        public int Width => XMax - XMin + 1;

        public int Height => YMax - YMin + 1;

        public bool IsRle => Encoding == 1;

        /// <summary>True for 24-bit colour: three 8-bit planes, red, green and blue.</summary>
        public bool IsTrueColour => BitsPerPixel == 8 && Planes == 3;

        /// <summary>True for an 8-bit image that needs the palette at the end of the file.</summary>
        public bool IsPaletted256 => BitsPerPixel == 8 && Planes == 1;

        /// <summary>True for a 1, 2 or 4-bit image coloured from the header's 16-colour palette.</summary>
        public bool IsPaletted16 => BitsPerPixel is 1 or 2 or 4 && Planes >= 1 && BitsPerPixel * Planes <= 4;

        /// <summary>Bits of one pixel over all planes.</summary>
        public int BitsTotal => BitsPerPixel * Planes;

        /// <summary>Decoded bytes of one scanline: every plane's line, padding included.</summary>
        public long ScanlineBytes => (long)Planes * BytesPerLine;
    }

    /// <summary>Reads dimensions and the engine format without decoding pixels (the header alone is enough).</summary>
    public static ImageInfo Probe(byte[] bytes, string name)
    {
        var h = ReadHeader(new ByteReader(bytes, name), name);
        return new ImageInfo(ImageContainer.Pcx, h.Width, h.Height, FormatOf(h), null,
            $"PCX version {h.Version}, {h.BitsTotal}-bit ({h.Planes} plane{(h.Planes == 1 ? "" : "s")} of {h.BitsPerPixel} bit{(h.BitsPerPixel == 1 ? "" : "s")}), "
            + (h.IsRle ? "RLE" : "uncompressed"));
    }

    /// <summary>Decodes the image into BGRA32, opaque.</summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <param name="colorKeyIndex">
    /// The colour-key hook: a palette index to make fully transparent, for a caller that knows its
    /// game keys one. Null (the default, and what every caller passes today) keeps every pixel
    /// opaque. Ignored for 24-bit images, which have no palette.
    /// </param>
    /// <exception cref="ImageDecodeException">The file is truncated, malformed or uses a form this reader does not support.</exception>
    public static BgraImage Decode(byte[] bytes, string name, int? colorKeyIndex = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var r = new ByteReader(bytes, name);
        var h = ReadHeader(r, name);
        r.Position = HeaderSize;

        // The colours, as RGB triples. An 8-bit file's palette sits after the pixel data, which
        // then ends where the palette starts.
        byte[]? palette = null;
        int dataEnd = bytes.Length;
        if (h.IsPaletted256)
        {
            int at = bytes.Length - EndPaletteSize;
            if (at < HeaderSize || bytes[at] != PaletteMarker)
                throw r.Fail("the 256-colour palette is missing from the end of the file (an 8-bit PCX ends with byte 0x0C and 768 bytes of colours).");
            palette = bytes[(at + 1)..];
            dataEnd = at;
        }
        else if (h.IsPaletted16)
        {
            palette = HeaderPalette(bytes, h);
        }

        // Check the declared image against what is in the file before allocating for it, as the
        // TGA reader does: the densest run-length packing is two bytes per 63.
        long total = h.ScanlineBytes * h.Height;
        long available = Math.Max(0, dataEnd - HeaderSize);
        long needed = h.IsRle ? (total + MaxRun - 1) / MaxRun * 2 : total;
        DecodeLimits.EnsureDataAvailable(needed, available, name);
        DecodeLimits.EnsureWithinBudget(h.Width, h.Height, name);

        var image = new BgraImage(h.Width, h.Height);
        var line = new byte[(int)h.ScanlineBytes];
        var stream = new RleState { Position = HeaderSize };
        for (int y = 0; y < h.Height; y++)
        {
            if (h.IsRle) ReadRleLine(bytes, dataEnd, ref stream, line, name);
            else ReadStoredLine(bytes, dataEnd, ref stream.Position, line, name);
            WriteRow(h, line, palette, colorKeyIndex, image, y);
        }
        return image;
    }

    private static Header ReadHeader(ByteReader r, string name)
    {
        if (r.Length < HeaderSize) throw new ImageDecodeException($"'{name}' is too short to be a PCX image ({r.Length} bytes; the header alone is {HeaderSize}).");
        int manufacturer = r.ReadByte();
        int version = r.ReadByte();
        int encoding = r.ReadByte();
        int bits = r.ReadByte();
        int xMin = r.ReadUInt16();
        int yMin = r.ReadUInt16();
        int xMax = r.ReadUInt16();
        int yMax = r.ReadUInt16();
        r.Position = 65;
        int planes = r.ReadByte();
        int bytesPerLine = r.ReadUInt16();

        if (manufacturer != Manufacturer)
            throw new ImageDecodeException($"'{name}' is not a PCX image (it does not start with byte 0x0A).");
        if (version is not (0 or 2 or 3 or 4 or 5))
            throw new ImageDecodeException($"'{name}' uses unknown PCX version {version}.");
        if (encoding is not (0 or 1))
            throw new ImageDecodeException($"'{name}' uses unknown PCX encoding {encoding}.");
        if (xMax < xMin || yMax < yMin)
            throw new ImageDecodeException($"'{name}' declares an empty window ({xMin}, {yMin}) to ({xMax}, {yMax}).");
        int width = xMax - xMin + 1, height = yMax - yMin + 1;
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"'{name}' is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");

        var h = new Header(version, encoding, bits, xMin, yMin, xMax, yMax, planes, bytesPerLine);
        if (!h.IsPaletted256 && !h.IsTrueColour && !h.IsPaletted16)
            throw new ImageDecodeException(
                $"'{name}' uses {planes} plane{(planes == 1 ? "" : "s")} of {bits} bit{(bits == 1 ? "" : "s")}, a PCX form Cairn does not read "
                + "(it reads 8 bits in 1 plane, 8 bits in 3 planes, and 1, 2 or 4-bit images of up to 16 colours).");
        long minimum = ((long)width * bits + 7) / 8;
        if (bytesPerLine < minimum)
            throw new ImageDecodeException(
                $"'{name}' is damaged: it declares {bytesPerLine} bytes per line, but a {width}-pixel line of {bits}-bit pixels needs {minimum}.");
        // Padding is normally a byte or so (lines are kept to an even length). Allow any amount, but
        // not so much that the planes alone would outweigh the largest image Cairn decodes.
        if ((long)planes * bytesPerLine * height > DecodeLimits.MaxDecodePixels * 4)
            throw new ImageDecodeException(
                $"'{name}' is damaged: it declares {bytesPerLine} bytes per line, far more than a {width}-pixel line needs.");
        return h;
    }

    /// <summary>
    /// How RF would classify the image: 24-bit colour is <see cref="EngineFormat.Rgb888"/>, and every
    /// palette image (8-bit, or fewer bits) is <see cref="EngineFormat.Paletted8"/>.
    /// </summary>
    private static EngineFormat FormatOf(Header h) => h.IsTrueColour ? EngineFormat.Rgb888 : EngineFormat.Paletted8;

    /// <summary>
    /// The 16 colours of a 1, 2 or 4-bit image, from the header. A two-colour image is black and
    /// white (index 0 black, index 1 white) when the header has no usable colours for it: a
    /// version 3 file ("no palette information"), or the same colour given twice (writers often
    /// leave the palette zeroed). Other version 3 files use the standard EGA palette.
    /// </summary>
    private static byte[] HeaderPalette(byte[] bytes, Header h)
    {
        var palette = bytes[16..64];
        if (h.BitsTotal == 1 && (h.Version == 3 || palette.AsSpan(0, 3).SequenceEqual(palette.AsSpan(3, 3))))
        {
            palette = new byte[48];
            palette[3] = palette[4] = palette[5] = 0xFF;
            return palette;
        }
        return h.Version == 3 ? DefaultEgaPalette : palette;
    }

    /// <summary>Where the run-length stream is: the read position and the rest of a run that crossed into the next line.</summary>
    private struct RleState
    {
        public int Position;
        public int Pending;
        public byte Value;
    }

    /// <summary>
    /// Fills one scanline from the run-length stream. Runs may continue from one scanline into the
    /// next (writers are told not to, but some do), so a run's unused part is carried over.
    /// </summary>
    private static void ReadRleLine(byte[] bytes, int dataEnd, ref RleState s, byte[] line, string name)
    {
        int i = 0;
        while (i < line.Length)
        {
            if (s.Pending > 0)
            {
                int n = Math.Min(s.Pending, line.Length - i);
                line.AsSpan(i, n).Fill(s.Value);
                i += n;
                s.Pending -= n;
                continue;
            }
            if (s.Position >= dataEnd) throw Truncated(name);
            byte b = bytes[s.Position++];
            if ((b & 0xC0) == 0xC0)
            {
                if (s.Position >= dataEnd) throw Truncated(name);
                s.Pending = b & 0x3F;
                s.Value = bytes[s.Position++];
            }
            else
            {
                line[i++] = b;
            }
        }
    }

    private static void ReadStoredLine(byte[] bytes, int dataEnd, ref int position, byte[] line, string name)
    {
        if (position > dataEnd - line.Length) throw Truncated(name);
        bytes.AsSpan(position, line.Length).CopyTo(line);
        position += line.Length;
    }

    private static ImageDecodeException Truncated(string name) =>
        new($"'{name}' is truncated: its pixel data ends before the last scanline.");

    /// <summary>Turns one decoded scanline (every plane, padding included) into a row of BGRA pixels.</summary>
    private static void WriteRow(Header h, byte[] line, byte[]? palette, int? colorKeyIndex, BgraImage image, int y)
    {
        var pixels = image.Pixels;
        int o = y * image.Stride;
        int width = h.Width, stride = h.BytesPerLine;
        if (h.IsTrueColour)
        {
            for (int x = 0; x < width; x++, o += 4)
            {
                pixels[o] = line[2 * stride + x];
                pixels[o + 1] = line[stride + x];
                pixels[o + 2] = line[x];
                pixels[o + 3] = 255;
            }
            return;
        }

        int bits = h.BitsPerPixel, mask = (1 << bits) - 1;
        for (int x = 0; x < width; x++, o += 4)
        {
            int index;
            if (bits == 8)
            {
                index = line[x];
            }
            else
            {
                // Pixels are packed from the high bit down; plane p supplies bits p * bits and up of the index.
                int bit = x * bits, shift = 8 - bits - (bit & 7);
                index = 0;
                for (int p = 0; p < h.Planes; p++)
                    index |= ((line[p * stride + (bit >> 3)] >> shift) & mask) << (p * bits);
            }
            pixels[o] = palette![index * 3 + 2];
            pixels[o + 1] = palette[index * 3 + 1];
            pixels[o + 2] = palette[index * 3];
            pixels[o + 3] = index == colorKeyIndex ? (byte)0 : (byte)255;
        }
    }
}
