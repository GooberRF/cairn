namespace Cairn.Formats.Imaging;

/// <summary>
/// What a VBM's header says, read without touching a single pixel.
/// </summary>
/// <param name="Version">The container version. 1 stores 1555 alpha inverted; 2 does not.</param>
/// <param name="Width">Width of mip level 0.</param>
/// <param name="Height">Height of mip level 0.</param>
/// <param name="Format">The engine pixel format the file's 16-bit pixels are in.</param>
/// <param name="Fps">The header's frames-per-second field. Meaningless when there is one frame.</param>
/// <param name="FrameCount">How many frames the file holds; at least 1.</param>
/// <param name="MipLevels">
/// How many mip levels each frame holds, <i>including</i> the full-size level 0 — one more than the
/// header's own field, which counts the levels below the base.
/// </param>
/// <param name="FrameStrideBytes">
/// Bytes from the start of one frame to the start of the next: the whole mip chain, in 64-bit
/// arithmetic because a large image with a full chain and several frames overflows 32 bits.
/// </param>
/// <param name="PayloadBytes">Bytes of pixel data actually present in the file.</param>
public sealed record VbmInfo(
    int Version,
    int Width,
    int Height,
    EngineFormat Format,
    int Fps,
    int FrameCount,
    int MipLevels,
    long FrameStrideBytes,
    long PayloadBytes)
{
    /// <summary>True when the file holds exactly the pixel data the header implies.</summary>
    public bool LengthMatchesHeader => PayloadBytes == FrameStrideBytes * FrameCount;

    /// <summary>True when more than one frame is stored, i.e. the file is an animation.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>Bytes in one frame's level 0, the only level a preview reads.</summary>
    public long TopLevelBytes => (long)Width * Height * 2;

    /// <summary>
    /// A one-line summary for a dialog: "VBM v1 · 256 x 256 · 4444 ARGB (16-bit, 4-bit alpha) ·
    /// 16 frames · 15 fps · 5 mip levels".
    /// </summary>
    public string Describe()
    {
        string frames = FrameCount == 1 ? "1 frame" : $"{FrameCount} frames";
        string fps = FrameCount > 1 ? $" · {Fps} fps" : string.Empty;
        return $"VBM v{Version} · {Width} x {Height} · {EngineFormats.DisplayName(Format)} · "
            + $"{frames}{fps} · {MipLevels} mip level{(MipLevels == 1 ? "" : "s")}";
    }
}

/// <summary>
/// Reads Red Faction VBM (Volition BitMap) files: a 32-byte header of eight 32-bit fields
/// (signature, version, width, height, format, fps, frame count, mip count) followed by 16-bit
/// pixel data.
/// </summary>
/// <remarks>
/// <para>
/// <b>1555 alpha is stored inverted in version 1.</b> In a version 1 VBM the top bit of a 1555
/// pixel means <i>transparent</i>, not opaque — the opposite of D3D's A1R5G5B5, which is what the
/// engine holds in memory (<c>bm_overlay_alpha_mask</c> in
/// <c>common/include/common/bitmap/formats.h</c> sets that bit for an opaque mask pixel, and
/// <c>FORMAT_1555_ARGB</c> is handed straight to <c>D3DFMT_A1R5G5B5</c>), so the inversion belongs
/// to the file format and the loader undoes it. That is what the engine's per-bitmap
/// <c>vbm_version</c> (<c>rf::bm::BitmapEntry</c>, filled from <c>bm_read_header</c>'s
/// <c>vbm_ver_out</c>) is remembered for, so <see cref="Decode(byte[], string)"/> inverts for
/// version 1 and reads version 2 and later the standard way round.
/// </para>
/// <para>
/// Established by surveying every <c>.vbm</c> in the root <c>.vpp</c> archives of a retail RF1
/// install (112 files) plus Alpine Faction's own <c>resources/images</c> (10 files). All 27 1555
/// files found are version 1, and in every one the pixels with the top bit set are the ones whose
/// RGB is pure black — the region an art tool zeroes because it is not drawn. The dominant pixel
/// value in most stock UI panels is literally <c>0x8000</c> (black, bit set): 62.6% of
/// <c>message_log.vbm</c>, 52.9% of <c>titlebar.vbm</c>, 49.4% of <c>server_list_panel.vbm</c>.
/// Reading the bit as opaque would make those panels a large opaque black field with the artwork
/// punched out of it. <c>advanced_server_panel.vbm</c> (512 x 247, the file this was reported
/// against) has the bit set on only 2.1% of its centre and 29.2% of its border; <c>popup_top.vbm</c>
/// has it set nowhere at all, i.e. a fully opaque popup background rather than an invisible one.
/// No version 2 1555 file exists in either corpus, so the version 2 branch follows the engine's
/// in-memory convention rather than a sample.
/// </para>
/// <para>
/// <b>The mip field counts the levels below the base.</b> The same survey settles it: of the 112
/// stock files, 39 have a non-zero mip field, and for every one of them the file length equals the
/// mip chain of <c>field + 1</c> levels times the frame count, and never the chain of
/// <c>field</c> levels — two version 1 files with field 3, two with field 6, and 35 version 2 files
/// with field 4.
/// The remaining 73 have field 0, where both readings mean one level, so nothing contradicts it.
/// <see cref="Probe(byte[], string)"/> therefore reports <c>field + 1</c> levels (capped at the
/// number the dimensions can actually hold) and says so when the file length does not agree, which
/// is all a truncated or oddly padded file can be told apart by.
/// </para>
/// </remarks>
public static class VbmCodec
{
    /// <summary>The VBM signature: the bytes ".vbm" read as a little-endian 32-bit value.</summary>
    public const uint Signature = 0x6D62762E;

    private const int HeaderSize = 32;

    private sealed record Header(
        uint Version, int Width, int Height, int Format, int Fps, int FrameCount, int MipField);

    /// <summary>
    /// The first VBM version that stores 1555 alpha the standard way round. Files below it store
    /// the bit inverted; see the remarks on <see cref="VbmCodec"/>.
    /// </summary>
    public const uint FirstStandardAlphaVersion = 2;

    /// <summary>
    /// The most frames a VBM may claim before the file is treated as damaged rather than read.
    ///
    /// <para>
    /// Nothing in Red Faction comes near it — the longest stock animation is a few dozen frames —
    /// but the count is a raw 32-bit field, and anything that trusted it and allocated per frame
    /// could be made to reserve two billion entries by a 32-byte header. The cap is what turns that
    /// into one sentence instead.
    /// </para>
    /// </summary>
    public const int MaxFrameCount = 10_000;

    /// <summary>Reads dimensions, engine format and mip count without decoding pixels.</summary>
    public static ImageInfo Probe(byte[] bytes, string name)
    {
        var h = ReadHeader(new ByteReader(bytes, name), name);
        int mips = ResolveMipLevels(h, bytes.Length, out string note);
        return new ImageInfo(ImageContainer.Vbm, h.Width, h.Height, FormatOf(h, name), mips,
            $"{h.FrameCount} frame(s){note}", h.FrameCount, (int)h.Version);
    }

    /// <summary>
    /// Reads the whole header — version, size, format, fps, frame count and mip levels — plus the
    /// frame stride an animated file is walked with. No pixel data is touched and nothing is
    /// allocated for the image, so this is safe to call on a file of any claimed size.
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <exception cref="ImageDecodeException">The header is not a readable VBM header.</exception>
    public static VbmInfo ReadInfo(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var h = ReadHeader(new ByteReader(bytes, name), name);
        int levels = ResolveMipLevels(h, bytes.Length, out _);
        return new VbmInfo(
            (int)h.Version, h.Width, h.Height, FormatOf(h, name), h.Fps, h.FrameCount, levels,
            MipChainBytes(h.Width, h.Height, levels), Math.Max(0, bytes.Length - HeaderSize));
    }

    /// <summary>Decodes frame 0's top mip level into BGRA32.</summary>
    public static BgraImage Decode(byte[] bytes, string name) => DecodeFrame(bytes, 0, name);

    /// <summary>
    /// Decodes one frame's top mip level into BGRA32.
    ///
    /// <para>
    /// Frames sit back to back, each one a whole mip chain, so reaching frame <i>n</i> means
    /// skipping <i>n</i> chains. That offset is computed in 64-bit arithmetic and checked against
    /// the bytes the file actually holds <i>before</i> anything is allocated: a header claiming ten
    /// thousand frames of 8192 x 8192 must cost one comparison, not a terabyte.
    /// </para>
    /// </summary>
    /// <param name="bytes">The file's bytes.</param>
    /// <param name="frameIndex">Which frame to decode, counting from 0.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <exception cref="ImageDecodeException">
    /// The index is out of range, or the file stops before that frame's pixels do.
    /// </exception>
    public static BgraImage DecodeFrame(byte[] bytes, int frameIndex, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var r = new ByteReader(bytes, name);
        var h = ReadHeader(r, name);
        var format = FormatOf(h, name);
        int levels = ResolveMipLevels(h, bytes.Length, out _);

        if (frameIndex < 0 || frameIndex >= h.FrameCount)
        {
            throw new ImageDecodeException(
                $"'{name}' holds {h.FrameCount} frame{(h.FrameCount == 1 ? "" : "s")}, "
                + $"so there is no frame {frameIndex}.");
        }

        long stride = MipChainBytes(h.Width, h.Height, levels);
        long topLevel = (long)h.Width * h.Height * 2;
        // The frames before this one have to be whole for the offset to mean anything, and this
        // one's level 0 has to be whole for there to be an image. Later mip levels of the last
        // frame may be missing without costing us anything, so they are not required.
        long needed = stride * frameIndex + topLevel;
        DecodeLimits.EnsureDataAvailable(needed, r.Remaining, name);
        DecodeLimits.EnsureWithinBudget(h.Width, h.Height, name);

        r.Skip(checked((int)(stride * frameIndex)));

        // Version 1 stores the 1555 alpha bit the other way up: set means transparent. See remarks.
        bool alphaBitMeansTransparent = h.Version < FirstStandardAlphaVersion;
        var image = new BgraImage(h.Width, h.Height);
        for (int y = 0; y < h.Height; y++)
        {
            for (int x = 0; x < h.Width; x++)
            {
                ushort v = r.ReadUInt16();
                var (b, g, rr, a) = format switch
                {
                    EngineFormat.Argb1555 => (
                        Expand5((byte)(v & 0x1F)), Expand5((byte)((v >> 5) & 0x1F)),
                        Expand5((byte)((v >> 10) & 0x1F)),
                        ((v & 0x8000) != 0) != alphaBitMeansTransparent ? (byte)255 : (byte)0),
                    EngineFormat.Argb4444 => (
                        Expand4((byte)(v & 0x0F)), Expand4((byte)((v >> 4) & 0x0F)),
                        Expand4((byte)((v >> 8) & 0x0F)), Expand4((byte)((v >> 12) & 0x0F))),
                    _ => (
                        Expand5((byte)(v & 0x1F)), Expand6((byte)((v >> 5) & 0x3F)),
                        Expand5((byte)((v >> 11) & 0x1F)), (byte)255),
                };
                image.Set(x, y, b, g, rr, a);
            }
        }
        return image;
    }

    private static Header ReadHeader(ByteReader r, string name)
    {
        if (r.Length < HeaderSize) throw new ImageDecodeException($"'{name}' is too short to be a VBM.");
        if (r.ReadUInt32() != Signature) throw new ImageDecodeException($"'{name}' is not a VBM file.");
        uint version = r.ReadUInt32();
        int width = (int)r.ReadUInt32();
        int height = (int)r.ReadUInt32();
        int format = (int)r.ReadUInt32();
        int fps = (int)r.ReadUInt32();
        int frames = (int)r.ReadUInt32();
        int mips = (int)r.ReadUInt32();

        if (width <= 0 || height <= 0)
            throw new ImageDecodeException($"'{name}' declares invalid dimensions {width} x {height}.");
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"'{name}' is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");
        if (frames <= 0) frames = 1;
        // A raw 32-bit count, read from a file that may have come out of a downloaded map pack.
        // Refuse an implausible one here, where it costs nothing, rather than in whatever allocates
        // per frame further on.
        if (frames > MaxFrameCount)
        {
            throw new ImageDecodeException(
                $"'{name}' says it holds {frames:N0} frames, which is more than the "
                + $"{MaxFrameCount:N0} Cairn will read. The file is damaged or is not a VBM.");
        }
        if (mips < 0 || mips > 32) mips = 0;

        return new Header(version, width, height, format, fps, frames, mips);
    }

    private static EngineFormat FormatOf(Header h, string name) => h.Format switch
    {
        0 => EngineFormat.Argb1555,
        1 => EngineFormat.Argb4444,
        2 => EngineFormat.Rgb565,
        _ => throw new ImageDecodeException($"'{name}' uses unknown VBM pixel format {h.Format}."),
    };

    /// <summary>
    /// How many mip levels the file holds: the header field counts the levels <i>below</i> the
    /// base, so the answer is one more than it — capped at the number the dimensions can hold,
    /// because a hostile header may claim more levels than a 16384-pixel chain even has.
    /// </summary>
    private static int ResolveMipLevels(Header h, int fileLength, out string note)
    {
        int levels = Math.Clamp(h.MipField + 1, 1, MaxMipLevels(h.Width, h.Height));

        // 64-bit: 16384 x 16384 with a full mip chain and a few frames overflows a 32-bit total,
        // and a wrapped size would happen to "match" the file and confirm a mip count off a
        // hostile header. The comparison below only ever needs to say equal or not equal.
        long payload = fileLength - HeaderSize;
        long expected = MipChainBytes(h.Width, h.Height, levels) * h.FrameCount;
        note = payload == expected ? string.Empty : " (mip count could not be verified)";
        return levels;
    }

    /// <summary>How many levels a chain from <paramref name="width"/> x <paramref name="height"/> has.</summary>
    private static int MaxMipLevels(int width, int height)
    {
        int levels = 1;
        while (width > 1 || height > 1)
        {
            width = Math.Max(width / 2, 1);
            height = Math.Max(height / 2, 1);
            levels++;
        }
        return levels;
    }

    private static long MipChainBytes(int width, int height, int levels)
    {
        long total = 0;
        int w = width, h = height;
        for (int i = 0; i < levels; i++)
        {
            total += (long)w * h * 2;
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
        }
        return total;
    }

    // Bit replication, the one expansion a later re-quantisation to the same width undoes exactly;
    // see ChannelBits. That is what lets an exported frame come back through the engine's own
    // 8888 → 1555 / 4444 / 565 conversion as the very pixels this file holds.
    private static byte Expand4(byte v) => ChannelBits.Expand(v, 4);

    private static byte Expand5(byte v) => ChannelBits.Expand(v, 5);

    private static byte Expand6(byte v) => ChannelBits.Expand(v, 6);
}
