using System.Buffers.Binary;
using System.IO.Compression;

namespace Cairn.Atx.SampleGen;

/// <summary>
/// Minimal encoders for the image formats the ATX module reads. Used by the sample generator and,
/// linked as source, by the test project so both synthesise files the same way. Deliberately not
/// part of the Core library: nothing in the app ever writes an image.
/// </summary>
public static class TinyImageWriter
{
    // ── TGA ───────────────────────────────────────────────────────────────────

    /// <summary>Uncompressed 24-bit true-colour TGA (image type 2).</summary>
    /// <param name="pixels">Row-major BGR triples, top row first.</param>
    /// <param name="topDown">Set the top-to-bottom origin bit rather than flipping rows.</param>
    public static byte[] Tga24(int width, int height, byte[] pixels, bool topDown = true)
    {
        var body = new byte[width * height * 3];
        CopyRows(pixels, body, width, height, 3, topDown);
        return TgaWithHeader(width, height, 24, imageType: 2, descriptor: topDown ? 0x20 : 0x00, body);
    }

    /// <summary>Uncompressed 32-bit true-colour TGA (image type 2) from BGRA pixels.</summary>
    public static byte[] Tga32(int width, int height, byte[] pixels, bool topDown = true)
    {
        var body = new byte[width * height * 4];
        CopyRows(pixels, body, width, height, 4, topDown);
        return TgaWithHeader(width, height, 32, imageType: 2, descriptor: (topDown ? 0x20 : 0x00) | 0x08, body);
    }

    /// <summary>Uncompressed 8-bit greyscale TGA (image type 3).</summary>
    public static byte[] Tga8Grey(int width, int height, byte[] grey, bool topDown = true)
    {
        var body = new byte[width * height];
        CopyRows(grey, body, width, height, 1, topDown);
        return TgaWithHeader(width, height, 8, imageType: 3, descriptor: topDown ? 0x20 : 0x00, body);
    }

    /// <summary>Uncompressed 16-bit TGA (image type 2) from ARGB1555 words.</summary>
    public static byte[] Tga16(int width, int height, ushort[] pixels, bool topDown = true)
    {
        var raw = new byte[width * height * 2];
        for (int i = 0; i < pixels.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), pixels[i]);
        var body = new byte[raw.Length];
        CopyRows(raw, body, width, height, 2, topDown);
        return TgaWithHeader(width, height, 16, imageType: 2, descriptor: (topDown ? 0x20 : 0x00) | 0x01, body);
    }

    /// <summary>8-bit colour-mapped TGA (image type 1) with a 24-bit palette.</summary>
    public static byte[] Tga8Paletted(int width, int height, byte[] indices, byte[] paletteBgr, bool topDown = true)
    {
        int entries = paletteBgr.Length / 3;
        var body = new byte[width * height];
        CopyRows(indices, body, width, height, 1, topDown);

        using var ms = new MemoryStream();
        WriteTgaHeader(ms, width, height, 8, imageType: 1, descriptor: topDown ? 0x20 : 0x00,
            colorMapType: 1, colorMapLength: entries, colorMapEntryBits: 24);
        ms.Write(paletteBgr);
        ms.Write(body);
        return ms.ToArray();
    }

    /// <summary>Run-length encoded 24-bit TGA (image type 10).</summary>
    public static byte[] Tga24Rle(int width, int height, byte[] pixels, bool topDown = true)
    {
        var source = new byte[width * height * 3];
        CopyRows(pixels, source, width, height, 3, topDown);

        using var body = new MemoryStream();
        int total = width * height;
        int i = 0;
        while (i < total)
        {
            int run = 1;
            while (run < 128 && i + run < total && SamePixel(source, i, i + run, 3)) run++;
            if (run > 1)
            {
                body.WriteByte((byte)(0x80 | (run - 1)));
                body.Write(source, i * 3, 3);
                i += run;
            }
            else
            {
                int raw = 1;
                while (raw < 128 && i + raw < total && !SamePixel(source, i + raw - 1, i + raw, 3)) raw++;
                body.WriteByte((byte)(raw - 1));
                body.Write(source, i * 3, raw * 3);
                i += raw;
            }
        }
        return TgaWithHeader(width, height, 24, imageType: 10, descriptor: topDown ? 0x20 : 0x00, body.ToArray());
    }

    /// <summary>Run-length encoded 8-bit greyscale TGA (image type 11).</summary>
    public static byte[] Tga8GreyRle(int width, int height, byte[] grey, bool topDown = true)
    {
        var source = new byte[width * height];
        CopyRows(grey, source, width, height, 1, topDown);

        using var body = new MemoryStream();
        int total = width * height;
        int i = 0;
        while (i < total)
        {
            int run = 1;
            while (run < 128 && i + run < total && source[i] == source[i + run]) run++;
            if (run > 1)
            {
                body.WriteByte((byte)(0x80 | (run - 1)));
                body.WriteByte(source[i]);
                i += run;
            }
            else
            {
                int raw = 1;
                while (raw < 128 && i + raw < total && source[i + raw - 1] != source[i + raw]) raw++;
                body.WriteByte((byte)(raw - 1));
                body.Write(source, i, raw);
                i += raw;
            }
        }
        return TgaWithHeader(width, height, 8, imageType: 11, descriptor: topDown ? 0x20 : 0x00, body.ToArray());
    }

    private static bool SamePixel(byte[] data, int a, int b, int size)
    {
        for (int k = 0; k < size; k++)
        {
            if (data[a * size + k] != data[b * size + k]) return false;
        }
        return true;
    }

    private static byte[] TgaWithHeader(
        int width, int height, int depth, int imageType, int descriptor, byte[] body)
    {
        using var ms = new MemoryStream();
        WriteTgaHeader(ms, width, height, depth, imageType, descriptor, 0, 0, 0);
        ms.Write(body);
        return ms.ToArray();
    }

    private static void WriteTgaHeader(
        Stream stream, int width, int height, int depth, int imageType, int descriptor,
        int colorMapType, int colorMapLength, int colorMapEntryBits)
    {
        Span<byte> header = stackalloc byte[18];
        header[0] = 0;                        // id length
        header[1] = (byte)colorMapType;
        header[2] = (byte)imageType;
        BinaryPrimitives.WriteUInt16LittleEndian(header[3..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[5..], (ushort)colorMapLength);
        header[7] = (byte)colorMapEntryBits;
        BinaryPrimitives.WriteUInt16LittleEndian(header[8..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[10..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)height);
        header[16] = (byte)depth;
        header[17] = (byte)descriptor;
        stream.Write(header);
    }

    /// <summary>Copies rows, flipping vertically when the file will use a bottom-up origin.</summary>
    private static void CopyRows(byte[] source, byte[] destination, int width, int height, int size, bool topDown)
    {
        int stride = width * size;
        for (int y = 0; y < height; y++)
        {
            int from = (topDown ? y : height - 1 - y) * stride;
            Array.Copy(source, from, destination, y * stride, stride);
        }
    }

    // ── DDS ───────────────────────────────────────────────────────────────────

    private const uint DdsMagic = 0x20534444;
    private const uint FlagsMipMap = 0x00020000;
    private const uint PfRgb = 0x40;
    private const uint PfAlphaPixels = 0x1;
    private const uint PfFourCc = 0x4;

    /// <summary>A DXT1 DDS built from 4x4 blocks that all use the two given 565 colours.</summary>
    public static byte[] DdsDxt1(int width, int height, Func<int, int, (ushort C0, ushort C1, uint Bits)> block,
        int mipLevels = 1)
    {
        using var ms = new MemoryStream();
        WriteDdsHeader(ms, width, height, mipLevels, fourCc: 0x31545844, rgbBitCount: 0,
            r: 0, g: 0, b: 0, a: 0);
        int w = width, h = height;
        var raw = new byte[8];
        for (int level = 0; level < mipLevels; level++)
        {
            for (int by = 0; by < (h + 3) / 4; by++)
            {
                for (int bx = 0; bx < (w + 3) / 4; bx++)
                {
                    var (c0, c1, bits) = block(bx, by);
                    BinaryPrimitives.WriteUInt16LittleEndian(raw, c0);
                    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2), c1);
                    BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4), bits);
                    ms.Write(raw);
                }
            }
            w = Math.Max(w / 2, 1);
            h = Math.Max(h / 2, 1);
        }
        return ms.ToArray();
    }

    /// <summary>A DXT3 or DXT5 DDS whose blocks are produced by <paramref name="block"/>.</summary>
    public static byte[] DdsDxtWithAlpha(int width, int height, bool dxt5, Func<int, int, byte[]> block)
    {
        using var ms = new MemoryStream();
        WriteDdsHeader(ms, width, height, 1, fourCc: dxt5 ? 0x35545844u : 0x33545844u,
            rgbBitCount: 0, r: 0, g: 0, b: 0, a: 0);
        for (int by = 0; by < (height + 3) / 4; by++)
        {
            for (int bx = 0; bx < (width + 3) / 4; bx++) ms.Write(block(bx, by));
        }
        return ms.ToArray();
    }

    /// <summary>An uncompressed 32-bit BGRA DDS.</summary>
    public static byte[] DdsBgra32(int width, int height, byte[] bgra)
    {
        using var ms = new MemoryStream();
        WriteDdsHeader(ms, width, height, 1, fourCc: 0, rgbBitCount: 32,
            r: 0x00FF0000, g: 0x0000FF00, b: 0x000000FF, a: 0xFF000000);
        ms.Write(bgra);
        return ms.ToArray();
    }

    /// <summary>An uncompressed 16-bit 565 DDS.</summary>
    public static byte[] Dds565(int width, int height, ushort[] pixels)
    {
        using var ms = new MemoryStream();
        WriteDdsHeader(ms, width, height, 1, fourCc: 0, rgbBitCount: 16,
            r: 0xF800, g: 0x07E0, b: 0x001F, a: 0);
        var raw = new byte[2];
        foreach (ushort p in pixels)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw, p);
            ms.Write(raw);
        }
        return ms.ToArray();
    }

    private static void WriteDdsHeader(
        Stream stream, int width, int height, int mipLevels,
        uint fourCc, int rgbBitCount, uint r, uint g, uint b, uint a)
    {
        var header = new byte[128];
        var span = header.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, DdsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124);                 // size
        uint flags = 0x1 | 0x2 | 0x4 | 0x1000;                                    // caps|height|width|pixelformat
        if (mipLevels > 1) flags |= FlagsMipMap;
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], flags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], 0);                  // pitch
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);                  // depth
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)mipLevels);
        // 11 reserved uints occupy 32..75
        BinaryPrimitives.WriteUInt32LittleEndian(span[76..], 32);                 // pixel format size
        uint pfFlags = fourCc != 0 ? PfFourCc : PfRgb | (a != 0 ? PfAlphaPixels : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(span[80..], pfFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(span[84..], fourCc);
        BinaryPrimitives.WriteUInt32LittleEndian(span[88..], (uint)rgbBitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[92..], r);
        BinaryPrimitives.WriteUInt32LittleEndian(span[96..], g);
        BinaryPrimitives.WriteUInt32LittleEndian(span[100..], b);
        BinaryPrimitives.WriteUInt32LittleEndian(span[104..], a);
        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], 0x1000);            // caps: texture
        stream.Write(header);
    }

    // ── VBM ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A VBM with one frame. <paramref name="mipField"/> is written to the header's mip field
    /// verbatim and <paramref name="levels"/> levels of pixel data are emitted, so tests can build
    /// files whose field and contents agree or disagree. <paramref name="version"/> goes into the
    /// header's version field, which is what decides how 1555 alpha is read.
    /// </summary>
    public static byte[] Vbm(
        int width, int height, int format, ushort[] topLevel, int mipField, int levels,
        uint version = 1)
        => VbmFrames(width, height, format, [topLevel], mipField, levels, version);

    /// <summary>
    /// A VBM with any number of frames. Each frame is a whole mip chain of
    /// <paramref name="levels"/> levels laid down back to back, largest first, which is what the
    /// real format does and therefore what a frame-by-frame reader has to walk.
    ///
    /// <para>
    /// Level 0 of frame <c>f</c> comes from <c>frames[f]</c>; the levels below it are filled with
    /// <c>0xF00D + f</c> rather than zeroes, so a test can tell a reader that skipped the mip data
    /// correctly from one that landed in the middle of it.
    /// </para>
    /// </summary>
    /// <param name="width">Level 0 width.</param>
    /// <param name="height">Level 0 height.</param>
    /// <param name="format">0 = 1555, 1 = 4444, 2 = 565.</param>
    /// <param name="frames">Level-0 pixels for each frame.</param>
    /// <param name="mipField">Written to the header's mip field verbatim.</param>
    /// <param name="levels">How many levels of data are actually emitted per frame.</param>
    /// <param name="version">Header version; 1 stores 1555 alpha inverted.</param>
    /// <param name="fps">Header fps field.</param>
    /// <param name="frameCountOverride">
    /// Written to the header's frame-count field instead of the real count, for tests about
    /// headers that lie.
    /// </param>
    public static byte[] VbmFrames(
        int width, int height, int format, ushort[][] frames, int mipField, int levels,
        uint version = 1, int fps = 0, int? frameCountOverride = null)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using var ms = new MemoryStream();
        Span<byte> header = stackalloc byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x6D62762E);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], (uint)format);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], unchecked((uint)fps));
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[24..], unchecked((uint)(frameCountOverride ?? frames.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)mipField);
        ms.Write(header);

        var raw = new byte[2];
        for (int f = 0; f < frames.Length; f++)
        {
            int w = width, h = height;
            var topLevel = frames[f] ?? [];
            for (int level = 0; level < levels; level++)
            {
                for (int i = 0; i < w * h; i++)
                {
                    ushort value = level == 0
                        ? (i < topLevel.Length ? topLevel[i] : (ushort)0)
                        : unchecked((ushort)(0xF00D + f));
                    BinaryPrimitives.WriteUInt16LittleEndian(raw, value);
                    ms.Write(raw);
                }
                w = Math.Max(w / 2, 1);
                h = Math.Max(h / 2, 1);
            }
        }
        return ms.ToArray();
    }

    // ── PNG ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A PNG written by hand so tests can control the colour type and the presence of tRNS, which
    /// is exactly what the channel-count probe depends on.
    /// </summary>
    /// <param name="colorType">0 grey, 2 RGB, 3 palette, 4 grey+alpha, 6 RGBA.</param>
    /// <param name="rows">One byte array per row, already in the colour type's byte layout.</param>
    /// <param name="palette">RGB triples, required for colour type 3.</param>
    /// <param name="transparency">tRNS chunk contents, or null for no tRNS.</param>
    public static byte[] Png(
        int width, int height, int colorType, byte[][] rows,
        byte[]? palette = null, byte[]? transparency = null)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;                 // bit depth
        ihdr[9] = (byte)colorType;
        WriteChunk(ms, "IHDR", ihdr);

        if (palette is not null) WriteChunk(ms, "PLTE", palette);
        if (transparency is not null) WriteChunk(ms, "tRNS", transparency);

        using var raw = new MemoryStream();
        foreach (var row in rows)
        {
            raw.WriteByte(0); // filter: none
            raw.Write(row);
        }
        using var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw.ToArray());
        }
        WriteChunk(ms, "IDAT", compressed.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string tag, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var type = System.Text.Encoding.ASCII.GetBytes(tag);
        stream.Write(type);
        stream.Write(data);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, type.Length);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(crcInput));
        stream.Write(crc);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

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

    private static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}
