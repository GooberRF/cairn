namespace Cairn.Formats.Imaging;

/// <summary>
/// Header-only probes for PNG and JPEG that predict the channel count stb_image would report, so
/// the engine format we show matches <c>bm_format_from_stb_channels</c> exactly. Pixels are
/// decoded elsewhere, by WPF.
/// </summary>
public static class PngJpegProbe
{
    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>True when the buffer starts with the PNG signature.</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 8 && bytes[..8].SequenceEqual(PngMagic);

    /// <summary>True when the buffer starts with a JPEG SOI marker.</summary>
    public static bool IsJpeg(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF;

    /// <summary>
    /// Reads a PNG's IHDR and scans for a tRNS chunk, reproducing stb_image's channel count:
    /// greyscale 1, greyscale+alpha 2, RGB 3, palette 3, RGBA 4, and +1 whenever tRNS is present
    /// on a greyscale or RGB image (palette + tRNS becomes 4).
    /// </summary>
    public static ImageInfo ProbePng(byte[] bytes, string name)
    {
        var r = new ByteReader(bytes, name);
        if (!IsPng(bytes)) throw new ImageDecodeException($"'{name}' is not a PNG file.");
        r.Skip(8);

        int width = 0, height = 0, channels = 0;
        int colorType = -1;
        bool sawIhdr = false;

        while (r.Remaining >= 8)
        {
            uint length = r.ReadUInt32BigEndian();
            if (length > int.MaxValue - 12) throw r.Fail("chunk length is out of range.");
            var type = r.ReadBytes(4);
            string tag = System.Text.Encoding.ASCII.GetString(type);

            if (tag == "IHDR")
            {
                if (length < 13) throw r.Fail("IHDR chunk is too short.");
                width = (int)r.ReadUInt32BigEndian();
                height = (int)r.ReadUInt32BigEndian();
                r.Skip(1); // bit depth
                colorType = r.ReadByte();
                r.Skip((int)length - 10);
                channels = colorType switch
                {
                    0 => 1, 2 => 3, 3 => 3, 4 => 2, 6 => 4,
                    _ => throw r.Fail($"unsupported PNG colour type {colorType}."),
                };
                sawIhdr = true;
            }
            else if (tag == "tRNS")
            {
                r.Skip((int)length);
                // Palette images gain a full alpha channel; greyscale/RGB gain one channel.
                channels = colorType == 3 ? 4 : channels + 1;
            }
            else if (tag == "IDAT" || tag == "IEND")
            {
                break;
            }
            else
            {
                r.Skip((int)length);
            }
            r.Skip(4); // CRC
        }

        if (!sawIhdr) throw new ImageDecodeException($"'{name}' has no PNG header chunk.");
        Validate(width, height, name);
        return new ImageInfo(ImageContainer.Png, width, height,
            EngineFormats.FromStbChannels(channels), 1, $"PNG colour type {colorType}, {channels} channel(s)");
    }

    /// <summary>
    /// Reads a JPEG's first SOFn marker. stb_image reports 3 channels for anything with 3 or more
    /// components and 1 otherwise, so a JPEG always becomes 888 RGB.
    /// </summary>
    public static ImageInfo ProbeJpeg(byte[] bytes, string name)
    {
        var r = new ByteReader(bytes, name);
        if (!IsJpeg(bytes)) throw new ImageDecodeException($"'{name}' is not a JPEG file.");
        r.Skip(2);

        while (r.Remaining >= 4)
        {
            byte marker = r.ReadByte();
            if (marker != 0xFF) continue;         // resynchronise on fill bytes
            byte code = r.ReadByte();
            while (code == 0xFF && r.Remaining > 0) code = r.ReadByte();
            if (code is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7) continue;
            if (code == 0xD9) break;              // EOI

            int length = r.ReadUInt16BigEndian();
            if (length < 2) throw r.Fail("JPEG segment length is invalid.");

            bool isSof = code is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC);
            if (isSof)
            {
                r.Skip(1); // precision
                int height = r.ReadUInt16BigEndian();
                int width = r.ReadUInt16BigEndian();
                int components = r.ReadByte();
                Validate(width, height, name);
                int channels = components >= 3 ? 3 : 1;
                return new ImageInfo(ImageContainer.Jpeg, width, height,
                    EngineFormats.FromStbChannels(channels), 1,
                    $"JPEG, {components} component(s)");
            }
            r.Skip(length - 2);
        }
        throw new ImageDecodeException($"'{name}' has no JPEG frame header.");
    }

    private static void Validate(int width, int height, string name)
    {
        if (width <= 0 || height <= 0)
            throw new ImageDecodeException($"'{name}' declares invalid dimensions {width} x {height}.");
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"'{name}' is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");
    }
}
