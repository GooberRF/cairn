using Cairn.Vf.Formats;
using Cairn.Vf.Model;

namespace Cairn.Vf.Rendering;

/// <summary>Where the game puts the glyphs in the font's texture.</summary>
/// <param name="Size">The square texture's side: 64, 128 or 256.</param>
/// <param name="Fits">False when the game stops with "Font too big!" while loading the font.</param>
/// <param name="FailedGlyph">The glyph that did not fit, or -1.</param>
/// <param name="UsedHeight">Rows used, in pixels (up to the failing glyph).</param>
public sealed record VfAtlasPlan(int Size, bool Fits, int FailedGlyph, int UsedHeight);

/// <summary>
/// The game's texture for a font: its side is 64 when twice the pixel data size (half of it for RGBA 4444) is under
/// 4,096, 128 under 16,384, else 256; glyphs are placed left to right in rows one font height tall, starting a new
/// row when a glyph would reach the right edge. A font whose rows run past the bottom stops the game with
/// "Font too big!" (checked when a new row starts, so a single row taller than the texture is not caught).
/// </summary>
public static class VfAtlas
{
    /// <summary>The texture side for a pixel data size and format.</summary>
    public static int SizeFor(long pixelDataSize, VfPixelFormat format)
    {
        long n = (format == VfPixelFormat.Rgba4444 ? pixelDataSize / 2 : pixelDataSize) * 2;
        return n < 0x1000 ? 64 : n < 0x4000 ? 128 : 256;
    }

    /// <summary>The pixel data size the writer will store for <paramref name="font"/>.</summary>
    public static long PixelDataSize(VfFont font) =>
        VfWriter.StoredLayoutMatches(font) ? font.PixelData.Length : font.Glyphs.Sum(g => (long)g.Pixels.Length);

    /// <summary>Places the glyphs as the game does.</summary>
    public static VfAtlasPlan Plan(VfFont font)
    {
        int size = SizeFor(PixelDataSize(font), font.Format);
        int x = 0, row = 0;
        for (int i = 0; i < font.Glyphs.Length; i++)
        {
            int w = Math.Max(0, font.Glyphs[i].Width);
            if (x + w >= size)
            {
                row += font.Height;
                x = 0;
                if (row + font.Height > size) return new VfAtlasPlan(size, false, i, row);
            }
            x += w;
        }
        // The game checks the size only when it starts a new row: glyphs that all fit one row load even when the font
        // is taller than the texture (they overrun it; see Overruns).
        return new VfAtlasPlan(size, true, -1, row + font.Height);
    }

    /// <summary>
    /// True when the glyphs fit one row but the font is taller than its texture: the game loads it without "Font too
    /// big!" and writes the rows past the bottom of the texture.
    /// </summary>
    public static bool Overruns(VfFont font, VfAtlasPlan plan) => plan.Fits && font.Glyphs.Length > 0 && font.Height > plan.Size;
}
