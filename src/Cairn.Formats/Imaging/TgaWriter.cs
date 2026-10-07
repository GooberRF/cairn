namespace Cairn.Formats.Imaging;

/// <summary>
/// Writes uncompressed true-colour TGA files (image type 2) — the plainest thing a Targa can be,
/// and therefore the one every loader in the chain agrees about.
///
/// <para>
/// Three choices are deliberate. <b>Uncompressed</b>, because RLE buys nothing on a texture that is
/// about to be loaded into video memory and is one more thing a stock-engine loader can disagree
/// about. <b>Bottom-left origin</b> (the image descriptor's vertical-flip bit clear), because that
/// is Targa's own default and what RF's loader and every art tool expect; a top-down file is legal
/// and is still read upside down by something. And <b>24-bit unless the source has alpha</b>, which
/// keeps a 565 export a third smaller and stops an ATX picking up a transparency channel its source
/// never had. A 32-bit file declares eight attribute bits in the descriptor, so a reader has no
/// reason to treat the fourth byte as padding.
/// </para>
/// </summary>
public static class TgaWriter
{
    private const int HeaderSize = 18;

    /// <summary>
    /// Encodes <paramref name="image"/> as a TGA.
    /// </summary>
    /// <param name="image">The pixels, in BGRA32 with the top row first.</param>
    /// <param name="includeAlpha">
    /// True for a 32-bit file carrying the alpha channel, false for a 24-bit opaque one.
    /// </param>
    public static byte[] Write(BgraImage image, bool includeAlpha)
    {
        ArgumentNullException.ThrowIfNull(image);
        int bytesPerPixel = includeAlpha ? 4 : 3;
        long total = HeaderSize + (long)image.Width * image.Height * bytesPerPixel;
        if (total > int.MaxValue)
            throw new ImageDecodeException("That image is too large to write as a TGA file.");

        var bytes = new byte[(int)total];
        bytes[2] = 2;                                   // uncompressed true-colour
        WriteUInt16(bytes, 12, image.Width);
        WriteUInt16(bytes, 14, image.Height);
        bytes[16] = (byte)(bytesPerPixel * 8);
        // Low nibble = attribute (alpha) bits; bit 5 clear = rows run bottom to top.
        bytes[17] = includeAlpha ? (byte)8 : (byte)0;

        var src = image.Pixels;
        int at = HeaderSize;
        for (int y = image.Height - 1; y >= 0; y--)
        {
            int row = y * image.Width * 4;
            for (int x = 0; x < image.Width; x++)
            {
                int i = row + x * 4;
                bytes[at] = src[i];                     // B
                bytes[at + 1] = src[i + 1];             // G
                bytes[at + 2] = src[i + 2];             // R
                if (includeAlpha) bytes[at + 3] = src[i + 3];
                at += bytesPerPixel;
            }
        }
        return bytes;
    }

    /// <summary>
    /// The engine pixel format a file written by <see cref="Write"/> will be read back as, which is
    /// what <see cref="TgaCodec.Probe"/> reports and what the linter compares frames by.
    /// </summary>
    /// <param name="includeAlpha">Whether the file carries alpha.</param>
    public static EngineFormat FormatOf(bool includeAlpha) =>
        includeAlpha ? EngineFormat.Argb8888 : EngineFormat.Rgb888;

    /// <summary>
    /// Whether a frame taken from <paramref name="format"/> needs an alpha channel in its TGA. The
    /// two 16-bit formats that carry alpha do; 565 does not, and giving it one would invent
    /// transparency the source never had.
    /// </summary>
    /// <param name="format">The source image's engine format.</param>
    public static bool NeedsAlpha(EngineFormat format) => EngineFormats.HasAlpha(format);

    /// <summary>How many bytes <see cref="Write"/> will produce for an image of this size.</summary>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <param name="includeAlpha">Whether the file carries alpha.</param>
    public static long SizeOf(int width, int height, bool includeAlpha) =>
        HeaderSize + (long)width * height * (includeAlpha ? 4 : 3);

    private static void WriteUInt16(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)(value & 0xFF);
        bytes[at + 1] = (byte)((value >> 8) & 0xFF);
    }
}
