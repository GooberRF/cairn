using Cairn.Vf.Model;

namespace Cairn.Vf.Rendering;

/// <summary>A straight-alpha BGRA image (4 bytes per pixel: blue, green, red, alpha), row-major.</summary>
public sealed record VfBitmap(int Width, int Height, byte[] Bgra);

/// <summary>An image <see cref="VfRender"/> will not draw because it would be larger than <see cref="VfRender.MaxImageBytes"/>.</summary>
public sealed class VfImageTooLargeException(string message) : InvalidOperationException(message);

/// <summary>
/// Turns glyph pixels into colours the way the game does when it builds a font's texture (a 4444 ARGB texture):
/// monochrome coverage 0-14 becomes white with alpha <c>(min(v, 14) × 255 / 14) &gt;&gt; 4</c>; an indexed pixel takes
/// the low 4 bits of each byte of its palette entry (0xAARRGGBB); RGBA 4444 pixels are used as they are (alpha in the
/// top 4 bits). Each 4-bit channel is widened to 8 bits by repeating it, so what Cairn shows is what the game has.
/// </summary>
public static class VfRender
{
    /// <summary>
    /// The largest image drawn, in bytes (64 MB, 16 million pixels: far beyond any font the game can load). Sizes come
    /// from the file (height, widths, spacings), so a damaged font must not make Cairn allocate gigabytes.
    /// </summary>
    public const long MaxImageBytes = 64L << 20;

    /// <summary>True when an image of <paramref name="width"/> × <paramref name="height"/> pixels is within <see cref="MaxImageBytes"/>.</summary>
    public static bool CanDraw(long width, long height) => Math.Max(0, width) * Math.Max(0, height) <= MaxImageBytes / 4;

    private static byte[] Canvas(int width, int height, string what)
    {
        if (!CanDraw(width, height))
            throw new VfImageTooLargeException($"{what} would be {width:N0} × {height:N0} pixels, too large to draw (Cairn draws at most {MaxImageBytes / 4:N0} pixels). The font's height, widths or spacings are far beyond what the game can show.");
        return new byte[(long)width * height * 4];
    }

    /// <summary>The 4444 ARGB value the game's font texture holds for a raw pixel value (byte, or 16-bit for RGBA 4444).</summary>
    public static ushort ToArgb4444(VfFont font, int raw)
    {
        switch (font.Format)
        {
            case VfPixelFormat.Rgba4444:
                return (ushort)raw;
            case VfPixelFormat.Indexed:
                uint p = raw >= 0 && raw < font.Palette.Length ? font.Palette[raw] : 0;
                return (ushort)((((p >> 24) & 0xF) << 12) | (((p >> 16) & 0xF) << 8) | (((p >> 8) & 0xF) << 4) | (p & 0xF));
            default:
                int v = Math.Min(Math.Max(raw, 0), 14);
                int alpha = v * 255 / 14 >> 4;
                return (ushort)((alpha << 12) | 0x0FFF);
        }
    }

    /// <summary>A 4444 ARGB value as 8-bit (blue, green, red, alpha).</summary>
    public static (byte B, byte G, byte R, byte A) Expand(ushort argb) =>
        ((byte)((argb & 0xF) * 17), (byte)(((argb >> 4) & 0xF) * 17), (byte)(((argb >> 8) & 0xF) * 17), (byte)(((argb >> 12) & 0xF) * 17));

    /// <summary>The raw value of pixel (<paramref name="x"/>, <paramref name="y"/>) of a glyph.</summary>
    public static int RawPixel(VfFont font, VfGlyph glyph, int x, int y)
    {
        int i = y * glyph.Width + x;
        if (font.Format == VfPixelFormat.Rgba4444)
            return 2 * i + 1 < glyph.Pixels.Length ? glyph.Pixels[2 * i] | glyph.Pixels[2 * i + 1] << 8 : 0;
        return i < glyph.Pixels.Length ? glyph.Pixels[i] : 0;
    }

    /// <summary>Glyph <paramref name="index"/> as a BGRA image of its width × the font height (a 0-width glyph gives an empty image).</summary>
    /// <exception cref="VfImageTooLargeException">The glyph is larger than <see cref="MaxImageBytes"/>.</exception>
    public static VfBitmap Glyph(VfFont font, int index)
    {
        var g = font.Glyphs[index];
        int w = Math.Max(0, g.Width), h = Math.Max(0, font.Height);
        var bgra = Canvas(w, h, $"Glyph {index}");
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (b, gr, r, a) = Expand(ToArgb4444(font, RawPixel(font, g, x, y)));
                int o = (y * w + x) * 4;
                bgra[o] = b; bgra[o + 1] = gr; bgra[o + 2] = r; bgra[o + 3] = a;
            }
        return new VfBitmap(w, h, bgra);
    }

    /// <summary>The colour palette entry <paramref name="index"/> gives in the game (8-bit BGRA).</summary>
    public static (byte B, byte G, byte R, byte A) PaletteColour(VfFont font, int index) => Expand(ToArgb4444(font, index));

    /// <summary>
    /// <paramref name="text"/> drawn as the game draws it, on a transparent image as wide as the drawn pixels and as
    /// high as the measured lines (at least 1 × 1). Overlapping glyphs are blended over each other.
    /// </summary>
    public static VfBitmap Text(VfFont font, ReadOnlySpan<byte> text) => Text(font, VfLayout.Layout(font, text));

    /// <summary>A layout drawn on a transparent image.</summary>
    /// <exception cref="VfImageTooLargeException">The drawn text is larger than <see cref="MaxImageBytes"/>.</exception>
    public static VfBitmap Text(VfFont font, VfTextLayout layout)
    {
        int w = Math.Max(1, layout.Right), h = Math.Max(1, layout.Height);
        var canvas = Canvas(w, h, "The text");
        var cache = new Dictionary<int, VfBitmap>();
        foreach (var p in layout.Glyphs)
        {
            if (!cache.TryGetValue(p.Glyph, out var bmp)) cache[p.Glyph] = bmp = Glyph(font, p.Glyph);
            BlendOver(canvas, w, h, bmp, p.X, p.Y);
        }
        return new VfBitmap(w, h, canvas);
    }

    /// <summary>Straight-alpha "over" of <paramref name="src"/> at (<paramref name="x0"/>, <paramref name="y0"/>), clipped.</summary>
    private static void BlendOver(byte[] dst, int dw, int dh, VfBitmap src, int x0, int y0)
    {
        for (int y = 0; y < src.Height; y++)
        {
            int dy = y0 + y;
            if (dy < 0 || dy >= dh) continue;
            for (int x = 0; x < src.Width; x++)
            {
                int dx = x0 + x;
                if (dx < 0 || dx >= dw) continue;
                int s = (y * src.Width + x) * 4, d = (dy * dw + dx) * 4;
                int sa = src.Bgra[s + 3];
                if (sa == 0) continue;
                int da = dst[d + 3];
                int oa = sa + da * (255 - sa) / 255;
                for (int c = 0; c < 3; c++)
                    dst[d + c] = (byte)((src.Bgra[s + c] * sa + dst[d + c] * da * (255 - sa) / 255) / Math.Max(1, oa));
                dst[d + 3] = (byte)oa;
            }
        }
    }
}
