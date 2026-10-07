namespace Cairn.Formats.Imaging;

/// <summary>
/// Reads DirectDraw Surface files. Format classification and the mip count follow
/// <c>bm_format_from_dds</c> and <c>read_dds_header</c> exactly, so what the probe reports is
/// what the engine will see.
/// </summary>
public static class DdsCodec
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint HeaderFlagsMipMap = 0x00020000;
    private const uint PixelFormatRgb = 0x40;
    private const uint PixelFormatFourCc = 0x4;
    private const int HeaderSize = 124;

    private sealed record PixelFormat(uint Flags, uint FourCc, int RgbBitCount,
        uint RMask, uint GMask, uint BMask, uint AMask);

    private sealed record Header(uint Flags, int Width, int Height, int MipMapCount, PixelFormat Pf);

    /// <summary>Reads dimensions, engine format and mip count without decoding pixels.</summary>
    public static ImageInfo Probe(byte[] bytes, string name)
    {
        var h = ReadHeader(new ByteReader(bytes, name), name);
        var format = FormatOf(h.Pf);
        if (format == EngineFormat.None)
            throw new ImageDecodeException($"'{name}' uses a DDS pixel format the engine does not support.");
        // read_dds_header takes mipMapCount verbatim when the mipmap flag is set, and bm_read_header
        // then rejects the file outright if that leaves num_levels below 1. Saying "1 mip level"
        // here would promise a load that never happens, so report it the way it will behave.
        int mips = 1;
        if ((h.Flags & HeaderFlagsMipMap) != 0)
        {
            if (h.MipMapCount < 1)
            {
                throw new ImageDecodeException(
                    $"'{name}' says it has mip levels but declares {h.MipMapCount} of them, "
                    + "so the game refuses to load it.");
            }
            mips = h.MipMapCount;
        }
        return new ImageInfo(ImageContainer.Dds, h.Width, h.Height, format, mips, DescribePixelFormat(h.Pf));
    }

    /// <summary>Decodes the top mip into BGRA32.</summary>
    public static BgraImage Decode(byte[] bytes, string name)
    {
        var r = new ByteReader(bytes, name);
        var h = ReadHeader(r, name);
        var format = FormatOf(h.Pf);
        if (format == EngineFormat.None)
            throw new ImageDecodeException($"'{name}' uses a DDS pixel format the engine does not support.");

        // Check the declared pixel data against what the file actually carries before allocating
        // for it: a 128-byte DDS header can claim 16384 x 16384 just as easily as a real one can.
        if (EngineFormats.IsCompressed(format))
        {
            bool dxt1 = format == EngineFormat.Dxt1;
            long blocks = (long)((h.Width + 3) / 4) * ((h.Height + 3) / 4);
            DecodeLimits.EnsureDataAvailable(blocks * (dxt1 ? 8 : 16), r.Remaining, name);
        }
        else
        {
            int bytesPerPixel = h.Pf.RgbBitCount / 8;
            if (bytesPerPixel is < 1 or > 4) throw r.Fail("unsupported uncompressed DDS bit count.");
            DecodeLimits.EnsureDataAvailable((long)h.Width * h.Height * bytesPerPixel, r.Remaining, name);
        }
        DecodeLimits.EnsureWithinBudget(h.Width, h.Height, name);

        var image = new BgraImage(h.Width, h.Height);
        if (EngineFormats.IsCompressed(format)) DecodeBlocks(r, h, format, image);
        else DecodeUncompressed(r, h, image);
        return image;
    }

    private static Header ReadHeader(ByteReader r, string name)
    {
        if (r.ReadUInt32() != Magic) throw new ImageDecodeException($"'{name}' is not a DDS file.");
        uint size = r.ReadUInt32();
        if (size != HeaderSize) throw new ImageDecodeException($"'{name}' has an invalid DDS header size ({size}).");
        uint flags = r.ReadUInt32();
        int height = (int)r.ReadUInt32();
        int width = (int)r.ReadUInt32();
        r.Skip(4);  // pitchOrLinearSize
        r.Skip(4);  // depth
        int mipMapCount = (int)r.ReadUInt32();
        r.Skip(11 * 4); // reserved1

        uint pfSize = r.ReadUInt32();
        if (pfSize != 32) throw new ImageDecodeException($"'{name}' has an invalid DDS pixel format size ({pfSize}).");
        var pf = new PixelFormat(r.ReadUInt32(), r.ReadUInt32(), (int)r.ReadUInt32(),
            r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32());
        r.Skip(5 * 4); // caps1..4 + reserved2

        if (width <= 0 || height <= 0)
            throw new ImageDecodeException($"'{name}' declares invalid dimensions {width} x {height}.");
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"'{name}' is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");

        return new Header(flags, width, height, mipMapCount, pf);
    }

    /// <summary>Port of <c>bm_format_from_dds</c>.</summary>
    private static EngineFormat FormatOf(PixelFormat pf)
    {
        if ((pf.Flags & PixelFormatRgb) != 0)
        {
            return pf.RgbBitCount switch
            {
                32 => pf.AMask != 0 ? EngineFormat.Argb8888 : EngineFormat.None,
                24 => EngineFormat.Rgb888,
                16 => pf.AMask == 0x8000 ? EngineFormat.Argb1555
                    : pf.AMask != 0 ? EngineFormat.Argb4444 : EngineFormat.Rgb565,
                _ => EngineFormat.None,
            };
        }
        if ((pf.Flags & PixelFormatFourCc) != 0)
        {
            return pf.FourCc switch
            {
                0x31545844 => EngineFormat.Dxt1, // "DXT1"
                0x32545844 => EngineFormat.Dxt2,
                0x33545844 => EngineFormat.Dxt3,
                0x34545844 => EngineFormat.Dxt4,
                0x35545844 => EngineFormat.Dxt5,
                _ => EngineFormat.None,
            };
        }
        return EngineFormat.None;
    }

    private static string DescribePixelFormat(PixelFormat pf) =>
        (pf.Flags & PixelFormatFourCc) != 0
            ? $"FourCC {(char)(pf.FourCc & 0xFF)}{(char)((pf.FourCc >> 8) & 0xFF)}" +
              $"{(char)((pf.FourCc >> 16) & 0xFF)}{(char)((pf.FourCc >> 24) & 0xFF)}"
            : $"{pf.RgbBitCount}-bit uncompressed";

    // ── Uncompressed ─────────────────────────────────────────────────────────

    private static void DecodeUncompressed(ByteReader r, Header h, BgraImage image)
    {
        int bytesPerPixel = h.Pf.RgbBitCount / 8;
        if (bytesPerPixel is < 1 or > 4) throw r.Fail("unsupported uncompressed DDS bit count.");
        var shifts = new[]
        {
            MaskInfo(h.Pf.RMask), MaskInfo(h.Pf.GMask), MaskInfo(h.Pf.BMask), MaskInfo(h.Pf.AMask),
        };
        for (int y = 0; y < h.Height; y++)
        {
            for (int x = 0; x < h.Width; x++)
            {
                var raw = r.ReadBytes(bytesPerPixel);
                uint v = 0;
                for (int i = 0; i < bytesPerPixel; i++) v |= (uint)raw[i] << (8 * i);
                byte red = Channel(v, shifts[0]);
                byte green = Channel(v, shifts[1]);
                byte blue = Channel(v, shifts[2]);
                byte alpha = h.Pf.AMask == 0 ? (byte)255 : Channel(v, shifts[3]);
                image.Set(x, y, blue, green, red, alpha);
            }
        }
    }

    private static (uint Mask, int Shift, int Bits) MaskInfo(uint mask)
    {
        if (mask == 0) return (0, 0, 0);
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        int bits = System.Numerics.BitOperations.PopCount(mask);
        return (mask, shift, bits);
    }

    private static byte Channel(uint value, (uint Mask, int Shift, int Bits) info)
    {
        if (info.Mask == 0) return 0;
        uint raw = (value & info.Mask) >> info.Shift;
        int max = (1 << info.Bits) - 1;
        return max == 0 ? (byte)0 : (byte)(raw * 255 / (uint)max);
    }

    // ── Block compression ────────────────────────────────────────────────────

    private static void DecodeBlocks(ByteReader r, Header h, EngineFormat format, BgraImage image)
    {
        int blocksX = (h.Width + 3) / 4;
        int blocksY = (h.Height + 3) / 4;
        bool dxt1 = format == EngineFormat.Dxt1;
        bool explicitAlpha = format is EngineFormat.Dxt2 or EngineFormat.Dxt3;
        int blockBytes = dxt1 ? 8 : 16;
        long need = (long)blocksX * blocksY * blockBytes;
        if (need > r.Remaining) throw r.Fail("compressed pixel data is truncated.");

        var colors = new (byte B, byte G, byte R)[4];
        var alphas = new byte[16];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                var block = r.ReadBytes(blockBytes);
                var colorBlock = dxt1 ? block : block[8..];
                if (dxt1)
                {
                    Array.Fill(alphas, (byte)255);
                }
                else if (explicitAlpha)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int nibble = (block[i / 2] >> ((i % 2) * 4)) & 0x0F;
                        alphas[i] = (byte)(nibble * 17);
                    }
                }
                else
                {
                    DecodeDxt5Alpha(block, alphas);
                }

                ushort c0 = (ushort)(colorBlock[0] | (colorBlock[1] << 8));
                ushort c1 = (ushort)(colorBlock[2] | (colorBlock[3] << 8));
                colors[0] = From565(c0);
                colors[1] = From565(c1);
                bool punchThrough = dxt1 && c0 <= c1;
                if (punchThrough)
                {
                    colors[2] = Mix(colors[0], colors[1], 1, 1, 2);
                    colors[3] = (0, 0, 0);
                }
                else
                {
                    colors[2] = Mix(colors[0], colors[1], 2, 1, 3);
                    colors[3] = Mix(colors[0], colors[1], 1, 2, 3);
                }

                uint bits = (uint)(colorBlock[4] | (colorBlock[5] << 8) | (colorBlock[6] << 16) | (colorBlock[7] << 24));
                for (int py = 0; py < 4; py++)
                {
                    for (int px = 0; px < 4; px++)
                    {
                        int i = py * 4 + px;
                        int sel = (int)((bits >> (i * 2)) & 3);
                        var c = colors[sel];
                        byte a = alphas[i];
                        if (punchThrough && sel == 3) a = 0;
                        image.Set(bx * 4 + px, by * 4 + py, c.B, c.G, c.R, a);
                    }
                }
            }
        }
    }

    private static void DecodeDxt5Alpha(ReadOnlySpan<byte> block, byte[] alphas)
    {
        Span<byte> table = stackalloc byte[8];
        table[0] = block[0];
        table[1] = block[1];
        if (table[0] > table[1])
        {
            for (int i = 1; i < 7; i++)
                table[i + 1] = (byte)(((7 - i) * table[0] + i * table[1]) / 7);
        }
        else
        {
            for (int i = 1; i < 5; i++)
                table[i + 1] = (byte)(((5 - i) * table[0] + i * table[1]) / 5);
            table[6] = 0;
            table[7] = 255;
        }
        ulong indices = 0;
        for (int i = 0; i < 6; i++) indices |= (ulong)block[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++) alphas[i] = table[(int)((indices >> (i * 3)) & 7)];
    }

    private static (byte B, byte G, byte R) From565(ushort v) =>
        ((byte)(((v & 0x1F) << 3) | ((v & 0x1F) >> 2)),
         (byte)((((v >> 5) & 0x3F) << 2) | (((v >> 5) & 0x3F) >> 4)),
         (byte)((((v >> 11) & 0x1F) << 3) | (((v >> 11) & 0x1F) >> 2)));

    private static (byte B, byte G, byte R) Mix(
        (byte B, byte G, byte R) a, (byte B, byte G, byte R) b, int wa, int wb, int total) =>
        ((byte)((a.B * wa + b.B * wb) / total),
         (byte)((a.G * wa + b.G * wb) / total),
         (byte)((a.R * wa + b.R * wb) / total));
}
