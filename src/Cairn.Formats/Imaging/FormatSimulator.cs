namespace Cairn.Formats.Imaging;

/// <summary>
/// Quantises a decoded image the way the engine's format conversion would, so the preview can show
/// what the chosen <c>format</c> actually costs in quality.
/// </summary>
public static class FormatSimulator
{
    /// <summary>
    /// Returns a copy of <paramref name="source"/> reduced to <paramref name="format"/> and
    /// expanded back to BGRA32 for display. Formats without alpha come back fully opaque.
    /// </summary>
    public static BgraImage Quantise(BgraImage source, EngineFormat format)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!EngineFormats.IsUncompressedRgb(format)) return Clone(source);

        var result = new BgraImage(source.Width, source.Height);
        var src = source.Pixels;
        var dst = result.Pixels;
        for (int i = 0; i < src.Length; i += 4)
        {
            byte b = src[i], g = src[i + 1], r = src[i + 2], a = src[i + 3];
            switch (format)
            {
                case EngineFormat.Rgb565:
                    b = Q(b, 5); g = Q(g, 6); r = Q(r, 5); a = 255;
                    break;
                case EngineFormat.Argb4444:
                    b = Q(b, 4); g = Q(g, 4); r = Q(r, 4); a = Q(a, 4);
                    break;
                case EngineFormat.Argb1555:
                    b = Q(b, 5); g = Q(g, 5); r = Q(r, 5); a = a >= 128 ? (byte)255 : (byte)0;
                    break;
                case EngineFormat.Rgb888:
                    a = 255;
                    break;
            }
            dst[i] = b; dst[i + 1] = g; dst[i + 2] = r; dst[i + 3] = a;
        }
        return result;
    }

    /// <summary>An independent copy of <paramref name="source"/>.</summary>
    public static BgraImage Clone(BgraImage source)
    {
        var copy = new BgraImage(source.Width, source.Height);
        Array.Copy(source.Pixels, copy.Pixels, source.Pixels.Length);
        return copy;
    }

    /// <summary>
    /// Rounds a channel down to <paramref name="bits"/> bits and expands it back to 8, by the same
    /// truncate-and-replicate rule the engine uses — so an image whose channels already came from a
    /// narrower format survives this untouched. That is what makes "simulate target format" over a
    /// frame exported from a 16-bit VBM show the VBM's own pixels rather than a shade beside them.
    /// </summary>
    private static byte Q(byte value, int bits) => ChannelBits.Reduce(value, bits);
}
