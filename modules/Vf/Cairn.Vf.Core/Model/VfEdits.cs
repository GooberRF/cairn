namespace Cairn.Vf.Model;

/// <summary>
/// Pure edit operations on a <see cref="VfFont"/> (each returns a new snapshot). They keep the derived fields
/// consistent: every edit ends with <see cref="Normalize"/>, so pixel offsets, the pixel data block, each glyph's
/// first kerning index and version 0's unused size fields match the content, as in the stock fonts.
/// </summary>
public static class VfEdits
{
    /// <summary>
    /// Lays the pixels out back to back in glyph order, sorts nothing but recomputes each glyph's first kerning index
    /// from the table (first pair whose left glyph is it, -1 for none) and the version 0 size fields.
    /// </summary>
    public static VfFont Normalize(VfFont font)
    {
        var (data, offsets) = Formats.VfWriter.PackedLayout(font);
        var first = new Dictionary<int, int>();
        for (int j = 0; j < font.Kerning.Length; j++) first.TryAdd(font.Kerning[j].Left, j);
        var glyphs = font.Glyphs.Select((g, i) => g with
        {
            PixelOffset = offsets[i],
            FirstKernIndex = (short)(first.TryGetValue(i, out int k) ? k : -1),
        }).ToImmutableArray();
        return font with
        {
            Glyphs = glyphs,
            PixelData = ImmutableArray.Create(data),
            LegacyKernDataSize = (uint)font.Kerning.Length * 3,
            LegacyCharDataSize = (uint)font.Glyphs.Length * 16,
        };
    }

    /// <summary>Replaces glyph <paramref name="index"/>.</summary>
    /// <exception cref="ArgumentException">The glyph's pixels do not match its width and the font height.</exception>
    public static VfFont WithGlyph(VfFont font, int index, VfGlyph glyph)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, font.Glyphs.Length);
        if (glyph.Width < 0 || glyph.Pixels.Length != font.PixelBytes(glyph.Width))
            throw new ArgumentException($"A glyph {glyph.Width} pixels wide needs {font.PixelBytes(glyph.Width)} bytes of pixels (got {glyph.Pixels.Length}).", nameof(glyph));
        return Normalize(font with { Glyphs = font.Glyphs.SetItem(index, glyph) });
    }

    /// <summary>Gives glyph <paramref name="index"/> new pixels of <paramref name="width"/> × font height (row-major, the font's bytes per pixel).</summary>
    public static VfFont WithGlyphPixels(VfFont font, int index, int width, ReadOnlySpan<byte> pixels) =>
        WithGlyph(font, index, font.Glyphs[index] with { Width = width, Pixels = ImmutableArray.Create(pixels.ToArray()) });

    /// <summary>Sets glyph <paramref name="index"/>'s spacing (pen advance).</summary>
    public static VfFont WithSpacing(VfFont font, int index, int spacing) =>
        WithGlyph(font, index, font.Glyphs[index] with { Spacing = spacing });

    /// <summary>
    /// Changes glyph <paramref name="index"/>'s width, keeping its pixels on the left: columns are cut off on the
    /// right, or clear columns added.
    /// </summary>
    public static VfFont WithWidth(VfFont font, int index, int width)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        var g = font.Glyphs[index];
        int bpp = font.BytesPerPixel, h = Math.Max(0, font.Height), oldWidth = Math.Max(0, g.Width);
        var pixels = new byte[font.PixelBytes(width)];
        int keep = Math.Min(width, oldWidth) * bpp;
        // Rows the glyph's pixels do not hold (a damaged glyph) stay clear.
        for (int y = 0; y < h && (long)(y * oldWidth + oldWidth) * bpp <= g.Pixels.Length; y++)
            g.Pixels.AsSpan(y * oldWidth * bpp, keep).CopyTo(pixels.AsSpan(y * width * bpp));
        return WithGlyph(font, index, g with { Width = width, Pixels = ImmutableArray.Create(pixels) });
    }

    /// <summary>
    /// Replaces the kerning table: pairs are sorted by left then right glyph (the order the game needs), a later
    /// duplicate replaces an earlier one, and pairs with offset 0 are dropped.
    /// </summary>
    public static VfFont WithKerning(VfFont font, IEnumerable<VfKernPair> pairs)
    {
        var unique = new Dictionary<(byte, byte), VfKernPair>();
        foreach (var k in pairs) unique[(k.Left, k.Right)] = k;
        var sorted = unique.Values.Where(k => k.Offset != 0).OrderBy(k => k.Left).ThenBy(k => k.Right).ToImmutableArray();
        return Normalize(font with { Kerning = sorted });
    }

    /// <summary>Sets the default spacing (the advance for characters the font has no glyph for).</summary>
    public static VfFont WithDefaultSpacing(VfFont font, int spacing) => font with { DefaultSpacing = spacing };

    /// <summary>
    /// Changes the character range to <paramref name="first"/> .. <paramref name="first"/> + <paramref name="count"/> - 1.
    /// Glyphs of characters in both ranges are kept; new characters get empty glyphs (width 0, default spacing);
    /// kerning pairs of removed characters are dropped and the rest renumbered.
    /// </summary>
    public static VfFont WithRange(VfFont font, int first, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(first);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var glyphs = ImmutableArray.CreateBuilder<VfGlyph>(count);
        for (int i = 0; i < count; i++)
        {
            int old = first + i - font.FirstCharacter;
            glyphs.Add(old >= 0 && old < font.Glyphs.Length ? font.Glyphs[old] : new VfGlyph(font.DefaultSpacing, 0, 0, -1, 0, []));
        }
        int shift = font.FirstCharacter - first;
        var kerning = font.Kerning
            .Select(k => (Left: k.Left + shift, Right: k.Right + shift, k.Offset))
            .Where(k => k.Left >= 0 && k.Left < count && k.Right >= 0 && k.Right < count && k.Left <= 255 && k.Right <= 255)
            .Select(k => new VfKernPair((byte)k.Left, (byte)k.Right, k.Offset));
        return WithKerning(font with { FirstCharacter = first, Glyphs = glyphs.MoveToImmutable() }, kerning);
    }

    /// <summary>
    /// <see cref="WithRange(VfFont, int, int)"/>, giving each new character a blank glyph <paramref name="newWidth"/>
    /// pixels wide that advances by the default spacing.
    /// </summary>
    public static VfFont WithRange(VfFont font, int first, int count, int newWidth)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newWidth);
        var result = WithRange(font, first, count);
        if (newWidth == 0) return result;
        var glyphs = result.Glyphs.ToBuilder();
        for (int i = 0; i < count; i++)
        {
            int old = first + i - font.FirstCharacter;
            if (old >= 0 && old < font.Glyphs.Length) continue;
            glyphs[i] = glyphs[i] with { Width = newWidth, Pixels = ImmutableArray.Create(new byte[font.PixelBytes(newWidth)]) };
        }
        return Normalize(result with { Glyphs = glyphs.MoveToImmutable() });
    }

    /// <summary>Sets glyph <paramref name="index"/>'s user data (a field the game ignores).</summary>
    public static VfFont WithUserData(VfFont font, int index, ushort userData) =>
        WithGlyph(font, index, font.Glyphs[index] with { UserData = userData });

    /// <summary>
    /// Adds the pair <paramref name="left"/> + <paramref name="right"/> (glyph indices) or changes its offset; offset 0
    /// removes it. The table stays sorted.
    /// </summary>
    public static VfFont WithKernPair(VfFont font, int left, int right, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(left);
        ArgumentOutOfRangeException.ThrowIfNegative(right);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(left, 255);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(right, 255);
        var pairs = font.Kerning.Where(k => k.Left != left || k.Right != right).ToList();
        if (offset != 0) pairs.Add(new VfKernPair((byte)left, (byte)right, (sbyte)Math.Clamp(offset, sbyte.MinValue, sbyte.MaxValue)));
        return WithKerning(font, pairs);
    }

    /// <summary>
    /// Changes the font height. <paramref name="keepTop"/> keeps the top rows (rows are added or cut at the bottom);
    /// otherwise the bottom rows are kept (rows are added or cut at the top). New rows are clear.
    /// </summary>
    public static VfFont WithHeight(VfFont font, int height, bool keepTop = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int bpp = font.BytesPerPixel, oldH = Math.Max(0, font.Height);
        var resized = font with { Height = height };
        var glyphs = font.Glyphs.Select(g =>
        {
            int width = Math.Max(0, g.Width); // a damaged negative width becomes an empty glyph, as the writer needs
            int row = width * bpp;
            var pixels = new byte[(long)row * height];
            int shift = keepTop ? 0 : height - oldH; // destination row of source row 0
            for (int y = 0; y < oldH; y++)
            {
                int dy = y + shift;
                if (dy < 0 || dy >= height || (y + 1) * row > g.Pixels.Length) continue;
                g.Pixels.AsSpan(y * row, row).CopyTo(pixels.AsSpan(dy * row));
            }
            return g with { Width = width, Pixels = ImmutableArray.Create(pixels) };
        }).ToImmutableArray();
        return Normalize(resized with { Glyphs = glyphs });
    }

    /// <summary>
    /// Converts every glyph to <paramref name="format"/>, keeping what the game shows as far as the format allows:
    /// monochrome becomes white with the same transparency; colours become monochrome coverage from their alpha;
    /// indexed fonts get a palette of the colours in use (the stock white ramp from monochrome). A version 0 font
    /// becomes version 1 unless the result is monochrome.
    /// </summary>
    public static VfFont WithFormat(VfFont font, VfPixelFormat format)
    {
        if (!VfFont.IsKnown(format)) throw new ArgumentException($"Unknown pixel format 0x{(uint)format:X8}.", nameof(format));
        if (format == font.Format) return font;
        // Every glyph through the game's 4444 colours.
        var argb = font.Glyphs.Select((g, i) =>
        {
            var c = new ushort[Math.Max(0, g.Width) * Math.Max(0, font.Height)];
            for (int y = 0; y < font.Height; y++)
                for (int x = 0; x < g.Width; x++)
                    c[y * g.Width + x] = Rendering.VfRender.ToArgb4444(font, Rendering.VfRender.RawPixel(font, g, x, y));
            return c;
        }).ToList();
        ImmutableArray<uint> palette = [];
        Func<ushort, int> encode;
        switch (format)
        {
            case VfPixelFormat.Mono:
                encode = c => (int)Math.Round(((c >> 12) & 0xF) * 14 / 15.0);
                break;
            case VfPixelFormat.Rgba4444:
                encode = c => c;
                break;
            default:
                if (font.Format == VfPixelFormat.Mono)
                {
                    palette = VfPixelConvert.WhiteRampPalette();
                    var map = new Dictionary<ushort, int>();
                    for (int v = 14; v >= 0; v--) map[VfPixelConvert.WhiteRampArgb4444(v)] = v;
                    encode = c => map.TryGetValue(c, out int v) ? v : 0;
                }
                else
                {
                    var counts = argb.SelectMany(a => a).GroupBy(c => c).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
                    var chosen = counts.Take(VfFont.PaletteSize).ToList();
                    var entries = new uint[VfFont.PaletteSize];
                    for (int i = 0; i < chosen.Count; i++) entries[i] = VfPixelConvert.Widen4444(chosen[i]);
                    palette = ImmutableArray.Create(entries);
                    var exact = chosen.Select((c, i) => (c, i)).ToDictionary(p => p.c, p => p.i);
                    var probe = font with { Format = VfPixelFormat.Indexed, Palette = palette };
                    encode = c => exact.TryGetValue(c, out int i) ? i : VfPixelConvert.NearestIndex(probe, VfPixelConvert.Widen4444(c));
                }
                break;
        }
        var result = font with { Format = format, Palette = palette, Version = format == VfPixelFormat.Mono ? font.Version : 1 };
        int bpp = VfFont.BytesPer(format);
        var glyphs = font.Glyphs.Select((g, i) =>
        {
            var pixels = new byte[argb[i].Length * bpp];
            for (int p = 0; p < argb[i].Length; p++)
            {
                int raw = encode(argb[i][p]);
                if (bpp == 2) { pixels[2 * p] = (byte)raw; pixels[2 * p + 1] = (byte)(raw >> 8); }
                else pixels[p] = (byte)raw;
            }
            return g with { Width = Math.Max(0, g.Width), Pixels = ImmutableArray.Create(pixels) };
        }).ToImmutableArray();
        return Normalize(result with { Glyphs = glyphs });
    }
}
