using Cairn.Formats.Imaging;
using Cairn.Vf.Rendering;

namespace Cairn.Vf.Model;

/// <summary>Where a monochrome font's coverage comes from when an image is turned into glyph pixels.</summary>
public enum VfCoverage
{
    /// <summary>Alpha when the image has transparency, else brightness (dark-on-light images inverted).</summary>
    Auto,
    /// <summary>The image's alpha (white or coloured text on a transparent background).</summary>
    Alpha,
    /// <summary>Brightness: light text on a dark background.</summary>
    Luminance,
    /// <summary>Darkness: dark text on a light background.</summary>
    DarkOnLight,
}

/// <summary>How an image's height is fitted to the font height.</summary>
public enum VfHeightFit
{
    /// <summary>Scaled (keeping its proportions) to the font height.</summary>
    Scale,
    /// <summary>Kept at its size, top-aligned: rows are cut or added at the bottom.</summary>
    KeepTop,
    /// <summary>Kept at its size, bottom-aligned: rows are cut or added at the top.</summary>
    KeepBottom,
}

/// <summary>How an image becomes a glyph.</summary>
/// <param name="Coverage">Monochrome fonts: where coverage comes from.</param>
/// <param name="Threshold">0 keeps soft edges; 1-255 makes every pixel solid or clear at this coverage or alpha.</param>
/// <param name="HeightFit">How the image's height is fitted to the font.</param>
/// <param name="Width">The glyph width, or null for the (fitted) image's width; the image is cut or padded on the right.</param>
public sealed record VfImageOptions(VfCoverage Coverage = VfCoverage.Auto, int Threshold = 0, VfHeightFit HeightFit = VfHeightFit.Scale, int? Width = null);

/// <summary>How monochrome pixels are drawn on an image sheet.</summary>
public enum VfMonoStyle
{
    /// <summary>Opaque grey levels on black (coverage 14 is white).</summary>
    Gray,
    /// <summary>White with the coverage as transparency.</summary>
    Alpha,
}

/// <summary>
/// Pixel conversions between glyph values and 8-bit colours: what an image pixel becomes in each format (monochrome
/// coverage 0-14, a 16-bit 4444 colour, the nearest palette entry) and the colour a glyph value is shown as on an
/// image sheet. Colours are 0xAARRGGBB.
/// </summary>
public static class VfPixelConvert
{
    /// <summary>Highest monochrome coverage the game shows.</summary>
    public const int MonoMax = 14;

    /// <summary>The 4444 colour of monochrome coverage <paramref name="v"/> in the game (white with alpha).</summary>
    public static ushort WhiteRampArgb4444(int v) => (ushort)(((Math.Clamp(v, 0, MonoMax) * 255 / MonoMax >> 4) << 12) | 0x0FFF);

    /// <summary>The palette of an indexed font made from a monochrome one: entry v is white with coverage v's alpha (as in the stock fonts); the rest are clear.</summary>
    public static ImmutableArray<uint> WhiteRampPalette()
    {
        var p = new uint[VfFont.PaletteSize];
        for (int v = 0; v <= MonoMax; v++) p[v] = Widen4444(WhiteRampArgb4444(v));
        return ImmutableArray.Create(p);
    }

    /// <summary>A 4444 colour as 0xAARRGGBB with each 4-bit channel repeated (0xF → 0xFF).</summary>
    public static uint Widen4444(ushort c)
    {
        uint a = (uint)((c >> 12) & 0xF), r = (uint)((c >> 8) & 0xF), g = (uint)((c >> 4) & 0xF), b = (uint)(c & 0xF);
        return (a * 17) << 24 | (r * 17) << 16 | (g * 17) << 8 | b * 17;
    }

    /// <summary>An 8-bit colour rounded to 4444.</summary>
    public static ushort Narrow4444(uint argb) =>
        (ushort)(Nib(argb >> 24) << 12 | Nib(argb >> 16) << 8 | Nib(argb >> 8) << 4 | Nib(argb));

    private static int Nib(uint channel) => ((int)(channel & 0xFF) + 8) / 17;

    /// <summary>The highest raw value a pixel of <paramref name="format"/> takes (14 for monochrome, 255 for indexed, 0xFFFF for 4444).</summary>
    public static int MaxValue(VfPixelFormat format) => format switch
    {
        VfPixelFormat.Rgba4444 => 0xFFFF,
        VfPixelFormat.Indexed => 255,
        _ => MonoMax,
    };

    /// <summary>
    /// The colour raw value <paramref name="raw"/> is written as on an image sheet: monochrome as grey on black or
    /// white with alpha (values above 14 as 14), RGBA 4444 widened, indexed as its palette entry as stored.
    /// </summary>
    public static uint SheetColour(VfFont font, int raw, VfMonoStyle mono = VfMonoStyle.Gray)
    {
        switch (font.Format)
        {
            case VfPixelFormat.Rgba4444: return Widen4444((ushort)raw);
            case VfPixelFormat.Indexed:
                uint entry = raw >= 0 && raw < font.Palette.Length ? font.Palette[raw] : 0;
                if (mono == VfMonoStyle.Gray && IsWhitePalette(font)) { uint a = entry >> 24; return 0xFF000000 | a << 16 | a << 8 | a; } // white ramp: drawn like monochrome
                return entry;
            default:
                uint level = (uint)Math.Round(Math.Clamp(raw, 0, MonoMax) * 255.0 / MonoMax);
                return mono == VfMonoStyle.Alpha ? level << 24 | 0xFFFFFF : 0xFF000000 | level << 16 | level << 8 | level;
        }
    }

    /// <summary>The coverage source <see cref="VfCoverage.Auto"/> stands for with this image.</summary>
    public static VfCoverage Resolve(BgraImage image, VfCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (coverage != VfCoverage.Auto) return coverage;
        var px = image.Pixels;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) return VfCoverage.Alpha;
        // Opaque: the border's brightness tells the background.
        long sum = 0; int n = 0;
        for (int x = 0; x < image.Width; x++) { sum += Lum(px, x * 4); sum += Lum(px, ((image.Height - 1) * image.Width + x) * 4); n += 2; }
        for (int y = 0; y < image.Height; y++) { sum += Lum(px, y * image.Width * 4); sum += Lum(px, (y * image.Width + image.Width - 1) * 4); n += 2; }
        return n > 0 && sum / n > 127 ? VfCoverage.DarkOnLight : VfCoverage.Luminance;
    }

    private static int Lum(byte[] px, int o) => (px[o + 2] * 299 + px[o + 1] * 587 + px[o] * 114) / 1000;

    /// <summary>
    /// The raw value an 8-bit colour becomes in <paramref name="font"/>'s format. <paramref name="coverage"/> must be
    /// resolved (not <see cref="VfCoverage.Auto"/>); <paramref name="threshold"/> 1-255 makes the pixel solid or clear.
    /// </summary>
    public static int RawFromColour(VfFont font, uint argb, VfCoverage coverage, int threshold = 0)
    {
        int a = (int)(argb >> 24), r = (int)(argb >> 16) & 0xFF, g = (int)(argb >> 8) & 0xFF, b = (int)argb & 0xFF;
        switch (font.Format)
        {
            case VfPixelFormat.Rgba4444:
                if (threshold > 0) argb = (argb & 0xFFFFFF) | (a >= threshold ? 0xFF000000u : 0u);
                return Narrow4444(argb);
            case VfPixelFormat.Indexed:
                // A palette of white with alpha (the stock fonts') is monochrome in disguise: coverage picks the entry.
                if (coverage != VfCoverage.Auto && IsWhitePalette(font)) argb = (uint)Coverage(a, r, g, b, coverage) << 24 | 0xFFFFFF;
                if (threshold > 0) argb = (argb & 0xFFFFFF) | (argb >> 24 >= threshold ? 0xFF000000u : 0u);
                return NearestIndex(font, argb);
            default:
                int c = Coverage(a, r, g, b, coverage);
                if (threshold > 0) return c >= threshold ? MonoMax : 0;
                return (int)Math.Round(c * MonoMax / 255.0);
        }
    }

    private static int Coverage(int a, int r, int g, int b, VfCoverage coverage)
    {
        int lum = (r * 299 + g * 587 + b * 114) / 1000;
        return coverage switch
        {
            VfCoverage.Luminance => lum * a / 255,
            VfCoverage.DarkOnLight => (255 - lum) * a / 255,
            _ => a,
        };
    }

    /// <summary>True when every palette entry is white (any alpha) or fully clear, as in the stock indexed fonts: images then convert by coverage.</summary>
    public static bool IsWhitePalette(VfFont font) =>
        font.Format == VfPixelFormat.Indexed && !font.Palette.IsDefaultOrEmpty && font.Palette.All(p => (p & 0xFFFFFF) == 0xFFFFFF || p >> 24 == 0);

    /// <summary>The palette entry closest to <paramref name="argb"/> (an exact match first, the lowest index on ties); clear colours match any clear entry.</summary>
    public static int NearestIndex(VfFont font, uint argb)
    {
        var palette = font.Palette;
        if (palette.IsDefaultOrEmpty) return 0;
        for (int i = 0; i < palette.Length; i++) if (palette[i] == argb) return i;
        int a = (int)(argb >> 24), r = (int)(argb >> 16) & 0xFF, g = (int)(argb >> 8) & 0xFF, b = (int)argb & 0xFF;
        long best = long.MaxValue; int at = 0;
        for (int i = 0; i < palette.Length; i++)
        {
            uint p = palette[i];
            int pa = (int)(p >> 24), pr = (int)(p >> 16) & 0xFF, pg = (int)(p >> 8) & 0xFF, pb = (int)p & 0xFF;
            long colour = (long)(pr - r) * (pr - r) + (long)(pg - g) * (pg - g) + (long)(pb - b) * (pb - b);
            long d = 3L * (pa - a) * (pa - a) * 255 + colour * Math.Min(pa, a);
            if (d < best) { best = d; at = i; }
        }
        return at;
    }

    /// <summary>
    /// An image as glyph pixels of <paramref name="font"/>: fitted to the font height, cut or padded to the width, and
    /// converted pixel by pixel (<see cref="RawFromColour"/>). Returns the width and the pixels (font bytes per pixel).
    /// </summary>
    public static (int Width, byte[] Pixels) GlyphFromImage(VfFont font, BgraImage image, VfImageOptions options)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(options);
        int h = Math.Max(1, font.Height);
        var source = image;
        if (options.HeightFit == VfHeightFit.Scale && image.Height != h)
        {
            int w = Math.Max(1, (int)Math.Round(image.Width * (double)h / image.Height));
            source = ImageResampler.Resize(image, w, h, ResampleFilter.Triangle);
        }
        var coverage = Resolve(source, options.Coverage);
        int width = Math.Clamp(options.Width ?? source.Width, 0, 255);
        int bpp = font.BytesPerPixel;
        var pixels = new byte[width * h * bpp];
        int top = options.HeightFit == VfHeightFit.KeepBottom ? source.Height - h : 0;
        for (int y = 0; y < h; y++)
        {
            int sy = y + top;
            for (int x = 0; x < width; x++)
            {
                uint argb = 0;
                if (sy >= 0 && sy < source.Height && x < source.Width)
                {
                    int o = (sy * source.Width + x) * 4;
                    var px = source.Pixels;
                    argb = (uint)px[o + 3] << 24 | (uint)px[o + 2] << 16 | (uint)px[o + 1] << 8 | px[o];
                }
                else if (coverage is VfCoverage.Luminance or VfCoverage.DarkOnLight && (font.Format == VfPixelFormat.Mono || IsWhitePalette(font)))
                {
                    argb = coverage == VfCoverage.DarkOnLight ? 0xFFFFFFFF : 0xFF000000; // padding is background
                }
                int raw = RawFromColour(font, argb, coverage, options.Threshold);
                int p = (y * width + x) * bpp;
                pixels[p] = (byte)raw;
                if (bpp == 2) pixels[p + 1] = (byte)(raw >> 8);
            }
        }
        return (width, pixels);
    }

    /// <summary>A glyph's pixels as an 8-bit image the way an image sheet shows them (null for a 0-width glyph).</summary>
    public static BgraImage? GlyphImage(VfFont font, int index, VfMonoStyle mono = VfMonoStyle.Gray)
    {
        var g = font.Glyphs[index];
        if (g.Width <= 0 || font.Height <= 0) return null;
        var image = new BgraImage(g.Width, font.Height);
        for (int y = 0; y < font.Height; y++)
            for (int x = 0; x < g.Width; x++)
            {
                uint c = SheetColour(font, VfRender.RawPixel(font, g, x, y), mono);
                image.Set(x, y, (byte)c, (byte)(c >> 8), (byte)(c >> 16), (byte)(c >> 24));
            }
        return image;
    }
}
