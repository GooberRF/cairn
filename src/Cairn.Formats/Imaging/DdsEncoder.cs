using BCnEncoder.Encoder;
using BCnEncoder.Shared;

namespace Cairn.Formats.Imaging;

/// <summary>
/// Writes DDS files Alpine Faction loads: a legacy 124-byte header (never the DX10 extension, which
/// the game rejects), DXT1/DXT3/DXT5 via BCnEncoder.NET or uncompressed 8888/565/1555/4444 with the
/// standard D3D masks, and a mip chain generated here with the chosen filter.
/// </summary>
public static class DdsEncoder
{
    private const uint Magic = 0x20534444; // "DDS "
    private const uint FlagCaps = 0x1, FlagHeight = 0x2, FlagWidth = 0x4, FlagPitch = 0x8;
    private const uint FlagPixelFormat = 0x1000, FlagMipMapCount = 0x20000, FlagLinearSize = 0x80000;
    private const uint PfAlphaPixels = 0x1, PfFourCc = 0x4, PfRgb = 0x40;
    private const uint CapsComplex = 0x8, CapsTexture = 0x1000, CapsMipMap = 0x400000;
    private const int FileHeaderSize = 128;

    /// <summary>Encodes <paramref name="image"/> to a complete DDS file.</summary>
    public static byte[] Encode(BgraImage image, DdsEncodeOptions options) => EncodeDetailed(image, options).Bytes;

    /// <summary>Encodes and reports what was written.</summary>
    /// <exception cref="ArgumentException">A block-compressed format was forced on a size that is not a multiple of 4 with resizing off.</exception>
    public static DdsEncodeResult EncodeDetailed(BgraImage image, DdsEncodeOptions options, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        var alpha = AnalyzeAlpha(image);
        var target = ResolveFormat(options.Format, alpha);
        bool compressed = IsBlockCompressed(target);

        var (w, h) = TargetSize(image.Width, image.Height, compressed, options);
        if (compressed && (w % 4 != 0 || h % 4 != 0))
        {
            throw new ArgumentException(
                $"{image.Width} x {image.Height} cannot be block-compressed without resizing: "
                + "the game needs both sides to be a multiple of 4.");
        }

        var level = w == image.Width && h == image.Height ? image : ImageResampler.Resize(image, w, h, options.MipFilter);
        if (options.PremultiplyAlpha) level = Premultiplied(level);

        int levels = LevelCount(w, h, options);
        var data = new MemoryStream();
        data.Write(Header(target, w, h, levels));
        for (int i = 0; i < levels; i++)
        {
            cancel.ThrowIfCancellationRequested();
            if (i > 0) level = ImageResampler.HalveForMip(level, options.MipFilter);
            data.Write(compressed ? EncodeBlocks(level, target, options.Quality) : Pack(level, target));
        }
        return new DdsEncodeResult(data.ToArray(), EngineFormatOf(target), target, w, h, levels, alpha,
            w != image.Width || h != image.Height);
    }

    /// <summary>Classifies the alpha channel.</summary>
    public static AlphaKind AnalyzeAlpha(BgraImage image)
    {
        var p = image.Pixels;
        var kind = AlphaKind.Opaque;
        for (int i = 3; i < p.Length; i += 4)
        {
            byte a = p[i];
            if (a == 255) continue;
            if (a != 0) return AlphaKind.Smooth;
            kind = AlphaKind.Binary;
        }
        return kind;
    }

    /// <summary>The concrete format for <paramref name="requested"/> given the source's alpha.</summary>
    public static DdsTargetFormat ResolveFormat(DdsTargetFormat requested, AlphaKind alpha) =>
        requested != DdsTargetFormat.Auto ? requested : alpha switch
        {
            AlphaKind.Opaque => DdsTargetFormat.Dxt1,
            AlphaKind.Binary => DdsTargetFormat.Dxt1Alpha,
            _ => DdsTargetFormat.Dxt5,
        };

    /// <summary>True for the DXT formats.</summary>
    public static bool IsBlockCompressed(DdsTargetFormat f) =>
        f is DdsTargetFormat.Auto or DdsTargetFormat.Dxt1 or DdsTargetFormat.Dxt1Alpha or DdsTargetFormat.Dxt3 or DdsTargetFormat.Dxt5;

    /// <summary>The engine format a target produces once loaded.</summary>
    public static EngineFormat EngineFormatOf(DdsTargetFormat f) => f switch
    {
        DdsTargetFormat.Dxt3 => EngineFormat.Dxt3,
        DdsTargetFormat.Dxt5 => EngineFormat.Dxt5,
        DdsTargetFormat.Argb8888 => EngineFormat.Argb8888,
        DdsTargetFormat.Rgb565 => EngineFormat.Rgb565,
        DdsTargetFormat.Argb1555 => EngineFormat.Argb1555,
        DdsTargetFormat.Argb4444 => EngineFormat.Argb4444,
        _ => EngineFormat.Dxt1,
    };

    /// <summary>A short label such as "DXT1 (1-bit alpha)".</summary>
    public static string Label(DdsTargetFormat f) => f switch
    {
        DdsTargetFormat.Auto => "Auto",
        DdsTargetFormat.Dxt1 => "DXT1 (opaque)",
        DdsTargetFormat.Dxt1Alpha => "DXT1 (1-bit alpha)",
        DdsTargetFormat.Dxt3 => "DXT3 (4-bit alpha)",
        DdsTargetFormat.Dxt5 => "DXT5 (smooth alpha)",
        DdsTargetFormat.Argb8888 => "A8R8G8B8 (32-bit)",
        DdsTargetFormat.Rgb565 => "R5G6B5 (16-bit)",
        DdsTargetFormat.Argb1555 => "A1R5G5B5 (16-bit)",
        _ => "A4R4G4B4 (16-bit)",
    };

    /// <summary>The top-level size after the resize rule.</summary>
    public static (int Width, int Height) TargetSize(int width, int height, bool blockCompressed, DdsEncodeOptions options)
    {
        bool resize = options.Resize switch
        {
            DdsResize.Always => !IsPowerOfTwo(width) || !IsPowerOfTwo(height),
            DdsResize.WhenNeeded => blockCompressed && (width % 4 != 0 || height % 4 != 0),
            _ => false,
        };
        if (!resize) return (width, height);
        int minimum = blockCompressed ? 4 : 1;
        return (Math.Max(RoundToPowerOfTwo(width, options.Rounding), minimum),
                Math.Max(RoundToPowerOfTwo(height, options.Rounding), minimum));
    }

    /// <summary>True when <paramref name="v"/> is a power of two.</summary>
    public static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    /// <summary>Rounds a side to a power of two (capped at the engine's maximum).</summary>
    public static int RoundToPowerOfTwo(int v, PowerOfTwoRounding rounding)
    {
        if (IsPowerOfTwo(v)) return v;
        int smaller = 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)v));
        int larger = Math.Min(smaller * 2, EngineFormats.MaxDimension);
        return rounding switch
        {
            PowerOfTwoRounding.Larger => larger,
            PowerOfTwoRounding.Smaller => smaller,
            _ => v - smaller < larger - v ? smaller : larger,
        };
    }

    /// <summary>Levels in a full chain for this size.</summary>
    public static int FullChainLength(int width, int height) =>
        32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(width, height));

    /// <summary>Levels the options ask for at this size.</summary>
    public static int LevelCount(int width, int height, DdsEncodeOptions options) => options.Mips switch
    {
        DdsMipMode.None => 1,
        DdsMipMode.Count => Math.Clamp(options.MipCount, 1, FullChainLength(width, height)),
        _ => FullChainLength(width, height),
    };

    /// <summary>Bytes of one level of a format.</summary>
    public static long LevelSize(int width, int height, EngineFormat format) => format switch
    {
        EngineFormat.Dxt1 => (long)((width + 3) / 4) * ((height + 3) / 4) * 8,
        EngineFormat.Dxt2 or EngineFormat.Dxt3 or EngineFormat.Dxt4 or EngineFormat.Dxt5 => (long)((width + 3) / 4) * ((height + 3) / 4) * 16,
        EngineFormat.Argb8888 => (long)width * height * 4,
        EngineFormat.Rgb888 => (long)width * height * 3,
        _ => (long)width * height * 2,
    };

    /// <summary>
    /// Cuts level <paramref name="level"/> out of a DDS written in a format the engine reads and returns
    /// it as a one-level DDS, so the shared <see cref="DdsCodec"/> can decode any mip. Null when the file
    /// has fewer levels or is truncated.
    /// </summary>
    public static byte[]? ExtractLevel(byte[] dds, int level)
    {
        var info = DdsCodec.Probe(dds, "level");
        if (level < 0 || level >= (info.MipLevels ?? 1)) return null;
        long offset = FileHeaderSize;
        int w = info.Width, h = info.Height;
        for (int i = 0; i < level; i++)
        {
            offset += LevelSize(w, h, info.Format);
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
        }
        long size = LevelSize(w, h, info.Format);
        if (offset + size > dds.Length) return null;
        var result = new byte[FileHeaderSize + size];
        Buffer.BlockCopy(dds, 0, result, 0, FileHeaderSize);
        Buffer.BlockCopy(dds, (int)offset, result, FileHeaderSize, (int)size);
        WriteUInt32(result, 8, ReadUInt32(result, 8) & ~FlagMipMapCount);
        WriteUInt32(result, 12, (uint)h);
        WriteUInt32(result, 16, (uint)w);
        WriteUInt32(result, 28, 1);
        return result;
    }

    // ── Header ───────────────────────────────────────────────────────────────

    private static byte[] Header(DdsTargetFormat target, int w, int h, int levels)
    {
        var b = new byte[FileHeaderSize];
        bool compressed = IsBlockCompressed(target);
        var format = EngineFormatOf(target);
        uint flags = FlagCaps | FlagHeight | FlagWidth | FlagPixelFormat
            | (compressed ? FlagLinearSize : FlagPitch) | (levels > 1 ? FlagMipMapCount : 0);
        WriteUInt32(b, 0, Magic);
        WriteUInt32(b, 4, 124);
        WriteUInt32(b, 8, flags);
        WriteUInt32(b, 12, (uint)h);
        WriteUInt32(b, 16, (uint)w);
        WriteUInt32(b, 20, compressed ? (uint)LevelSize(w, h, format) : (uint)(w * (format == EngineFormat.Argb8888 ? 4 : 2)));
        WriteUInt32(b, 28, (uint)levels);
        // Pixel format at 76.
        WriteUInt32(b, 76, 32);
        if (compressed)
        {
            WriteUInt32(b, 80, PfFourCc | (target == DdsTargetFormat.Dxt1Alpha ? PfAlphaPixels : 0));
            WriteUInt32(b, 84, target switch
            {
                DdsTargetFormat.Dxt3 => 0x33545844u,
                DdsTargetFormat.Dxt5 => 0x35545844u,
                _ => 0x31545844u,
            });
        }
        else
        {
            var (bits, r, g, bl, a) = target switch
            {
                DdsTargetFormat.Argb8888 => (32u, 0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u),
                DdsTargetFormat.Rgb565 => (16u, 0xF800u, 0x07E0u, 0x001Fu, 0u),
                DdsTargetFormat.Argb1555 => (16u, 0x7C00u, 0x03E0u, 0x001Fu, 0x8000u),
                _ => (16u, 0x0F00u, 0x00F0u, 0x000Fu, 0xF000u),
            };
            WriteUInt32(b, 80, PfRgb | (a != 0 ? PfAlphaPixels : 0));
            WriteUInt32(b, 88, bits);
            WriteUInt32(b, 92, r);
            WriteUInt32(b, 96, g);
            WriteUInt32(b, 100, bl);
            WriteUInt32(b, 104, a);
        }
        WriteUInt32(b, 108, CapsTexture | (levels > 1 ? CapsComplex | CapsMipMap : 0));
        return b;
    }

    private static void WriteUInt32(byte[] b, int at, uint v) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at), v);

    private static uint ReadUInt32(byte[] b, int at) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at));

    // ── Pixel data ───────────────────────────────────────────────────────────

    private static byte[] EncodeBlocks(BgraImage level, DdsTargetFormat target, DdsQuality quality)
    {
        // Pad levels smaller than a block (2 x 2, 1 x 1) or with partial blocks by repeating the edge,
        // so the block count is exactly what the engine computes and the padding never darkens edges.
        int pw = (level.Width + 3) / 4 * 4, ph = (level.Height + 3) / 4 * 4;
        var source = level;
        if (pw != level.Width || ph != level.Height)
        {
            source = new BgraImage(pw, ph);
            for (int y = 0; y < ph; y++)
            {
                int sy = Math.Min(y, level.Height - 1);
                for (int x = 0; x < pw; x++)
                {
                    int sx = Math.Min(x, level.Width - 1);
                    Buffer.BlockCopy(level.Pixels, (sy * level.Width + sx) * 4, source.Pixels, (y * pw + x) * 4, 4);
                }
            }
        }
        var encoder = new BcEncoder();
        encoder.OutputOptions.GenerateMipMaps = false;
        encoder.OutputOptions.Quality = quality switch
        {
            DdsQuality.Fast => CompressionQuality.Fast,
            DdsQuality.Best => CompressionQuality.BestQuality,
            _ => CompressionQuality.Balanced,
        };
        encoder.OutputOptions.Format = target switch
        {
            DdsTargetFormat.Dxt1Alpha => CompressionFormat.Bc1WithAlpha,
            DdsTargetFormat.Dxt3 => CompressionFormat.Bc2,
            DdsTargetFormat.Dxt5 => CompressionFormat.Bc3,
            _ => CompressionFormat.Bc1,
        };
        // Parallel encoding inside one image would compete with the converter's own batch workers.
        encoder.Options.IsParallel = false;
        var levels = encoder.EncodeToRawBytes(source.Pixels, pw, ph, PixelFormat.Bgra32);
        return levels[0];
    }

    private static byte[] Pack(BgraImage level, DdsTargetFormat target)
    {
        var p = level.Pixels;
        int n = level.Width * level.Height;
        if (target == DdsTargetFormat.Argb8888)
        {
            var copy = new byte[p.Length];
            Buffer.BlockCopy(p, 0, copy, 0, p.Length);
            return copy; // A8R8G8B8 little-endian is BGRA in memory.
        }
        var o = new byte[n * 2];
        for (int i = 0; i < n; i++)
        {
            int b = p[i * 4], g = p[i * 4 + 1], r = p[i * 4 + 2], a = p[i * 4 + 3];
            int v = target switch
            {
                DdsTargetFormat.Rgb565 => (Q(r, 31) << 11) | (Q(g, 63) << 5) | Q(b, 31),
                DdsTargetFormat.Argb1555 => (a >= 128 ? 0x8000 : 0) | (Q(r, 31) << 10) | (Q(g, 31) << 5) | Q(b, 31),
                _ => (Q(a, 15) << 12) | (Q(r, 15) << 8) | (Q(g, 15) << 4) | Q(b, 15),
            };
            o[i * 2] = (byte)v;
            o[i * 2 + 1] = (byte)(v >> 8);
        }
        return o;
    }

    private static int Q(int value, int max) => (value * max + 127) / 255;

    private static BgraImage Premultiplied(BgraImage source)
    {
        var copy = new BgraImage(source.Width, source.Height);
        var s = source.Pixels;
        var d = copy.Pixels;
        for (int i = 0; i < s.Length; i += 4)
        {
            int a = s[i + 3];
            d[i] = (byte)((s[i] * a + 127) / 255);
            d[i + 1] = (byte)((s[i + 1] * a + 127) / 255);
            d[i + 2] = (byte)((s[i + 2] * a + 127) / 255);
            d[i + 3] = (byte)a;
        }
        return copy;
    }
}
