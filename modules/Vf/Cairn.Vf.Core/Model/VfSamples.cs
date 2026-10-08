namespace Cairn.Vf.Model;

/// <summary>
/// Small generated fonts in each format (characters 32-126, 8 pixels high, a few kerning pairs) for tests,
/// self-tests and as a starting point. Each glyph is a simple pattern whose width depends on the character.
/// </summary>
public static class VfSamples
{
    /// <summary>
    /// The kerning pairs every sample has: "AV" -1, "VA" -1, "Y." -2. Chosen so the game's kerning scan applies no pair
    /// to a character pair that has none of its own (with "To", the game would also kern "Ao" by -2).
    /// </summary>
    public static readonly (char Left, char Right, sbyte Offset)[] Pairs = [('A', 'V', -1), ('V', 'A', -1), ('Y', '.', -2)];

    /// <summary>A sample font of <paramref name="format"/> (version 0 for monochrome when <paramref name="version"/> is 0).</summary>
    public static VfFont Create(VfPixelFormat format, int version = 1, int height = 8, int first = 32, int count = 95)
    {
        if (version == 0 && format != VfPixelFormat.Mono) throw new ArgumentException("Version 0 fonts are monochrome.", nameof(version));
        int bpp = VfFont.BytesPer(format);
        var glyphs = ImmutableArray.CreateBuilder<VfGlyph>(count);
        for (int i = 0; i < count; i++)
        {
            int code = first + i;
            int width = code == 32 ? 0 : 3 + code % 5;
            var pixels = new byte[width * height * bpp];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool on = x == 0 || y == height - 1 || (x + y + code) % 3 == 0;
                    int level = on ? 14 : (x * y) % 4;
                    int o = (y * width + x) * bpp;
                    switch (format)
                    {
                        case VfPixelFormat.Mono: pixels[o] = (byte)level; break;
                        case VfPixelFormat.Indexed: pixels[o] = (byte)(level * 18); break;
                        default:
                            ushort v = (ushort)(level * 15 / 14 << 12 | (code % 16) << 8 | (x * 3 % 16) << 4 | (y * 2 % 16));
                            pixels[o] = (byte)v; pixels[o + 1] = (byte)(v >> 8);
                            break;
                    }
                }
            glyphs.Add(new VfGlyph(code == 32 ? 3 : width + 1, width, 0, -1, 0, ImmutableArray.Create(pixels)));
        }
        var palette = format == VfPixelFormat.Indexed
            ? Enumerable.Range(0, VfFont.PaletteSize).Select(i => (uint)(Math.Min(15, i / 16) * 0x11) << 24 | 0x00FFFFFFu).ToImmutableArray()
            : [];
        var font = new VfFont
        {
            Version = version,
            Format = format,
            FirstCharacter = first,
            DefaultSpacing = 4,
            Height = height,
            Glyphs = glyphs.MoveToImmutable(),
            Palette = palette,
        };
        var pairs = Pairs.Where(p => font.IndexOf(p.Left) >= 0 && font.IndexOf(p.Right) >= 0)
            .Select(p => new VfKernPair((byte)font.IndexOf(p.Left), (byte)font.IndexOf(p.Right), p.Offset));
        return VfEdits.WithKerning(font, pairs);
    }
}
