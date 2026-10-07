namespace Cairn.Formats.Imaging;

/// <summary>
/// Reads Truevision TGA files: image types 1, 2, 3 (uncompressed colour-mapped, true-colour and
/// greyscale) and 9, 10, 11 (their RLE forms), at 8, 15, 16, 24 and 32 bits per pixel, honouring
/// the origin bits in the image descriptor.
/// </summary>
public static class TgaCodec
{
    private const int HeaderSize = 18;

    private sealed record Header(
        int IdLength, int ColorMapType, int ImageType,
        int ColorMapFirst, int ColorMapLength, int ColorMapEntryBits,
        int Width, int Height, int PixelDepth, int Descriptor)
    {
        public bool IsRle => ImageType is 9 or 10 or 11;

        public bool IsGreyscale => ImageType is 3 or 11;

        public bool IsColorMapped => ImageType is 1 or 9;

        public int AlphaBits => Descriptor & 0x0F;

        public bool RightToLeft => (Descriptor & 0x10) != 0;

        public bool TopToBottom => (Descriptor & 0x20) != 0;
    }

    /// <summary>Reads dimensions and the engine format without decoding pixels.</summary>
    public static ImageInfo Probe(byte[] bytes, string name)
    {
        var h = ReadHeader(new ByteReader(bytes, name), name);
        return new ImageInfo(ImageContainer.Tga, h.Width, h.Height, FormatOf(h), null,
            $"TGA image type {h.ImageType}, {h.PixelDepth}-bit");
    }

    /// <summary>Decodes the image into BGRA32.</summary>
    public static BgraImage Decode(byte[] bytes, string name)
    {
        var r = new ByteReader(bytes, name);
        var h = ReadHeader(r, name);
        r.Skip(h.IdLength);

        byte[]? palette = null;
        if (h.ColorMapType == 1 || h.IsColorMapped)
        {
            int entryBytes = (h.ColorMapEntryBits + 7) / 8;
            if (h.ColorMapLength > 0 && entryBytes is < 1 or > 4)
                throw r.Fail($"unsupported colour map entry size {h.ColorMapEntryBits}.");
            palette = new byte[Math.Max(1, h.ColorMapLength) * 4];
            var raw = r.ReadBytes(h.ColorMapLength * entryBytes);
            for (int i = 0; i < h.ColorMapLength; i++)
            {
                var (b, g, rr, a) = UnpackPixel(raw[(i * entryBytes)..((i + 1) * entryBytes)],
                    h.ColorMapEntryBits, alphaBits: h.ColorMapEntryBits == 32 ? 8 : 0);
                palette[i * 4] = b;
                palette[i * 4 + 1] = g;
                palette[i * 4 + 2] = rr;
                palette[i * 4 + 3] = a;
            }
        }

        int bytesPerPixel = (h.PixelDepth + 7) / 8;
        long pixelCount = (long)h.Width * h.Height;

        // Check the declared image against what is actually in the file before allocating for it.
        // An eighteen-byte header can claim 16384 x 16384; uncompressed that is 1 GB of pixels we
        // would otherwise reserve before reading the first byte. A run-length image is smaller,
        // but even the densest packing needs one control byte plus one pixel per 128 pixels.
        long needed = h.IsRle
            ? (pixelCount + 127) / 128 * (1 + bytesPerPixel)
            : pixelCount * bytesPerPixel;
        DecodeLimits.EnsureDataAvailable(needed, r.Remaining, name);
        // Truncation is checked first: for a file that does not hold its pixels, "truncated" is the
        // more useful answer than "too large", and it is the cheaper one to be sure of.
        DecodeLimits.EnsureWithinBudget(h.Width, h.Height, name);

        var image = new BgraImage(h.Width, h.Height);
        // Decode straight into the image when the rows are already the right way round, which is
        // the usual case; only a flipped or mirrored file needs a second buffer.
        bool inPlace = h.TopToBottom && !h.RightToLeft;
        byte[] flat = inPlace ? image.Pixels : new byte[pixelCount * 4];

        if (h.IsRle) DecodeRle(r, h, bytesPerPixel, (int)pixelCount, palette, flat);
        else DecodeRaw(r, h, bytesPerPixel, (int)pixelCount, palette, flat);
        if (inPlace) return image;

        // Apply the origin bits: TGA rows default to bottom-up, left-to-right.
        for (int y = 0; y < h.Height; y++)
        {
            int srcRow = h.TopToBottom ? y : h.Height - 1 - y;
            for (int x = 0; x < h.Width; x++)
            {
                int srcCol = h.RightToLeft ? h.Width - 1 - x : x;
                int si = (srcRow * h.Width + srcCol) * 4;
                int di = (y * h.Width + x) * 4;
                Array.Copy(flat, si, image.Pixels, di, 4);
            }
        }
        return image;
    }

    private static Header ReadHeader(ByteReader r, string name)
    {
        if (r.Length < HeaderSize) throw new ImageDecodeException($"'{name}' is too short to be a TGA.");
        int idLength = r.ReadByte();
        int colorMapType = r.ReadByte();
        int imageType = r.ReadByte();
        int cmFirst = r.ReadUInt16();
        int cmLength = r.ReadUInt16();
        int cmEntryBits = r.ReadByte();
        r.Skip(4); // x/y origin
        int width = r.ReadUInt16();
        int height = r.ReadUInt16();
        int depth = r.ReadByte();
        int descriptor = r.ReadByte();

        if (imageType is not (1 or 2 or 3 or 9 or 10 or 11))
            throw new ImageDecodeException($"'{name}' uses unsupported TGA image type {imageType}.");
        if (depth is not (8 or 15 or 16 or 24 or 32))
            throw new ImageDecodeException($"'{name}' uses unsupported TGA bit depth {depth}.");
        if (width <= 0 || height <= 0)
            throw new ImageDecodeException($"'{name}' declares invalid dimensions {width} x {height}.");
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"'{name}' is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");

        return new Header(idLength, colorMapType, imageType, cmFirst, cmLength, cmEntryBits,
            width, height, depth, descriptor);
    }

    /// <summary>
    /// How RF classifies a TGA. 8-bit files — indexed and greyscale alike — become
    /// <see cref="EngineFormat.Paletted8"/>; the greyscale fix in bmpman.cpp synthesises a ramp
    /// palette for types 3 and 11 rather than changing the format.
    /// </summary>
    private static EngineFormat FormatOf(Header h) => h.PixelDepth switch
    {
        8 => EngineFormat.Paletted8,
        15 or 16 => EngineFormat.Argb1555,
        24 => EngineFormat.Rgb888,
        _ => EngineFormat.Argb8888,
    };

    private static void DecodeRaw(ByteReader r, Header h, int bpp, int pixelCount, byte[]? palette, byte[] flat)
    {
        for (int i = 0; i < pixelCount; i++)
        {
            WritePixel(r.ReadBytes(bpp), h, palette, flat, i, r);
        }
    }

    private static void DecodeRle(ByteReader r, Header h, int bpp, int pixelCount, byte[]? palette, byte[] flat)
    {
        int i = 0;
        while (i < pixelCount)
        {
            int packet = r.ReadByte();
            int count = (packet & 0x7F) + 1;
            if (i + count > pixelCount)
                throw r.Fail("RLE data runs past the end of the image.");
            if ((packet & 0x80) != 0)
            {
                var pixel = r.ReadBytes(bpp).ToArray();
                for (int n = 0; n < count; n++) WritePixel(pixel, h, palette, flat, i++, r);
            }
            else
            {
                for (int n = 0; n < count; n++) WritePixel(r.ReadBytes(bpp), h, palette, flat, i++, r);
            }
        }
    }

    private static void WritePixel(
        ReadOnlySpan<byte> raw, Header h, byte[]? palette, byte[] flat, int index, ByteReader r)
    {
        byte b, g, rr, a;
        if (h.IsColorMapped)
        {
            int entry = raw[0] - h.ColorMapFirst;
            if (palette is null || entry < 0 || (entry + 1) * 4 > palette.Length)
                throw r.Fail("colour-mapped pixel refers to a palette entry that does not exist.");
            b = palette[entry * 4];
            g = palette[entry * 4 + 1];
            rr = palette[entry * 4 + 2];
            a = palette[entry * 4 + 3];
        }
        else if (h.IsGreyscale)
        {
            b = g = rr = raw[0];
            a = 255;
        }
        else
        {
            (b, g, rr, a) = UnpackPixel(raw, h.PixelDepth, h.AlphaBits);
        }
        int o = index * 4;
        flat[o] = b;
        flat[o + 1] = g;
        flat[o + 2] = rr;
        flat[o + 3] = a;
    }

    private static (byte B, byte G, byte R, byte A) UnpackPixel(ReadOnlySpan<byte> raw, int depth, int alphaBits)
    {
        switch (depth)
        {
            case 8:
                return (raw[0], raw[0], raw[0], 255);
            case 15:
            case 16:
            {
                int v = raw[0] | (raw[1] << 8);
                byte r5 = (byte)((v >> 10) & 0x1F);
                byte g5 = (byte)((v >> 5) & 0x1F);
                byte b5 = (byte)(v & 0x1F);
                byte a = depth == 16 && alphaBits > 0 ? ((v & 0x8000) != 0 ? (byte)255 : (byte)0) : (byte)255;
                return (Expand5(b5), Expand5(g5), Expand5(r5), a);
            }
            case 24:
                return (raw[0], raw[1], raw[2], 255);
            default:
                return (raw[0], raw[1], raw[2], raw[3]);
        }
    }

    private static byte Expand5(byte v) => (byte)((v << 3) | (v >> 2));
}
