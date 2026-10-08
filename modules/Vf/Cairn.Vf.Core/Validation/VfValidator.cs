using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;

namespace Cairn.Vf.Validation;

/// <summary>
/// Checks a font against what the game does with it (stock loader and text drawing, unchanged by Alpine Faction for
/// <c>.vf</c> files): sizes, character range, metrics, kerning, the texture limit and pixel values.
/// </summary>
public static class VfValidator
{
    /// <summary>Spacings beyond this many pixels (either way) are reported: no screen is that wide.</summary>
    public const int MaxSensibleSpacing = 4096;

    /// <summary>Every problem found, errors first within each area.</summary>
    public static IReadOnlyList<VfProblem> Validate(VfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        var p = new List<VfProblem>();
        string Ch(int i) => VfReader.Describe(font.CharacterOf(i));
        int n = font.Glyphs.Length;

        // Header
        if (font.Version == 0 && font.Format != VfPixelFormat.Mono)
            p.Add(new(VfSeverity.Error, "VF005", $"A version 0 font can only hold monochrome pixels, but this one is {VfFont.FormatName(font.Format)}."));
        if (n == 0) p.Add(new(VfSeverity.Error, "VF002", "The font has no characters."));
        if (font.Height <= 0) p.Add(new(VfSeverity.Error, "VF001", $"The font height is {font.Height}; it must be at least 1 pixel."));
        if (font.FirstCharacter is < 0 or > 255)
            p.Add(new(VfSeverity.Error, "VF003", $"The first character is {font.FirstCharacter}; character codes are 0 to 255."));
        else if (n > 0 && font.LastCharacter > 255)
            p.Add(new(VfSeverity.Information, "VF031", $"The font has glyphs up to character {font.LastCharacter}, but text has only 256 character codes: the last {Plural(font.LastCharacter - 255, "glyph")} can never be drawn."));
        if (font.DefaultSpacing <= 0)
            p.Add(new(VfSeverity.Warning, "VF004", $"The default spacing (used for characters the font does not have) is {font.DefaultSpacing}; such characters take no room."));
        else if (font.DefaultSpacing > MaxSensibleSpacing)
            p.Add(new(VfSeverity.Warning, "VF055", $"The default spacing is {font.DefaultSpacing:N0} pixels: a character the font lacks pushes the rest of the text far off any screen, and such text is too wide for Cairn to draw."));
        if (font.Format == VfPixelFormat.Indexed && font.Palette.Length != VfFont.PaletteSize)
            p.Add(new(VfSeverity.Error, "VF006", $"An indexed font needs {VfFont.PaletteSize} palette entries; this one has {font.Palette.Length}."));

        // Character map
        if (font.FirstCharacter != 32 && font.FirstCharacter is >= 0 and <= 255)
            p.Add(new(VfSeverity.Information, "VF030", $"The font starts at character {VfReader.Describe(font.FirstCharacter)}; stock fonts start at 32 (space). Characters below it use the default spacing and draw nothing."));
        var empty = Enumerable.Range(0, n).Where(i => font.Glyphs[i].Width == 0 && font.CharacterOf(i) is not 32 and not 160 && font.Glyphs[i].Spacing >= 0).ToList();
        if (empty.Count > 0)
            p.Add(new(VfSeverity.Information, "VF033", $"{Plural(empty.Count, "character")} draw nothing (width 0): {List(empty.Select(Ch))}.", empty[0]));

        // Glyphs
        for (int i = 0; i < n; i++)
        {
            var g = font.Glyphs[i];
            if (g.Width < 0) p.Add(new(VfSeverity.Error, "VF050", $"{Ch(i)} has a negative width ({g.Width}).", i));
            else if (g.Pixels.Length != font.PixelBytes(g.Width))
                p.Add(new(VfSeverity.Error, "VF054", $"{Ch(i)} has {g.Pixels.Length:N0} bytes of pixels; {g.Width} × {font.Height} needs {font.PixelBytes(g.Width):N0}.", i));
            if (g.Spacing < 0) p.Add(new(VfSeverity.Warning, "VF051", $"{Ch(i)} has a negative spacing ({g.Spacing}): the next character is drawn to its left.", i));
            else if (g.Spacing == 0 && g.Width > 0) p.Add(new(VfSeverity.Information, "VF052", $"{Ch(i)} has spacing 0: the next character is drawn on top of it.", i));
            if (g.Width >= 256) p.Add(new(VfSeverity.Error, "VF053", $"{Ch(i)} is {g.Width} pixels wide; the font texture is at most 256 pixels wide.", i));
            if (Math.Abs((long)g.Spacing) > MaxSensibleSpacing)
                p.Add(new(VfSeverity.Warning, "VF055", $"{Ch(i)} has a spacing of {g.Spacing:N0} pixels: the next character lands far off any screen, and text with it is too wide for Cairn to draw.", i));
        }

        // Kerning
        CheckKerning(font, p, Ch);

        // Texture
        var atlas = VfAtlas.Plan(font);
        if (!atlas.Fits && n > 0 && font.Height > 0)
            p.Add(new(VfSeverity.Error, "VF060", $"Font too big: the game stops with \"Font too big!\" when it loads this font. Its glyphs need more rows than the {atlas.Size} × {atlas.Size} font texture holds (they run out at {Ch(Math.Max(0, atlas.FailedGlyph))}). Make the glyphs narrower or shorter, or use fewer characters.", atlas.FailedGlyph >= 0 ? atlas.FailedGlyph : null));
        else if (VfAtlas.Overruns(font, atlas))
            p.Add(new(VfSeverity.Error, "VF061", $"The font is {font.Height} pixels high, taller than its {atlas.Size} × {atlas.Size} font texture. The game's \"Font too big!\" check only runs when the glyphs need a second row, so it loads this font and writes the glyph rows past the bottom of the texture: the characters draw wrongly and the game can crash. Make the font at most {atlas.Size} pixels high."));

        // Pixel values
        if (font.Format == VfPixelFormat.Mono)
        {
            var over = Enumerable.Range(0, n).Where(i => font.Glyphs[i].Pixels.Any(b => b > 14)).ToList();
            if (over.Count > 0)
                p.Add(new(VfSeverity.Information, "VF070", $"{Plural(over.Count, "character")} use coverage values above 14 (the most the game shows); they are drawn as 14: {List(over.Select(Ch))}.", over[0]));
        }
        if (font.Format == VfPixelFormat.Indexed && font.Palette.Length == VfFont.PaletteSize)
        {
            var used = new bool[256];
            foreach (var g in font.Glyphs) foreach (byte b in g.Pixels) used[b] = true;
            var lossy = Enumerable.Range(0, 256).Where(i => used[i] && !NibblesRepeat(font.Palette[i])).ToList();
            if (lossy.Count > 0)
                p.Add(new(VfSeverity.Information, "VF071", $"{Plural(lossy.Count, "palette colour")} in use ({List(lossy.Select(i => $"{i}: 0x{font.Palette[i]:X8}"))}) have channels whose two hex digits differ; the game keeps only the low digit of each channel, so they look different in the game (Cairn shows them as the game does)."));
        }

        // File layout
        if (VfWriter.StoredLayoutMatches(font))
        {
            long used = font.Glyphs.Sum(g => (long)g.Pixels.Length);
            if (font.PixelData.Length > used && !Overlaps(font))
                p.Add(new(VfSeverity.Information, "VF072", $"{font.PixelData.Length - used:N0} bytes of the pixel data belong to no glyph."));
        }
        if (font.Version == 0 && (font.LegacyKernDataSize != (uint)font.Kerning.Length * 3 || font.LegacyCharDataSize != (uint)n * 16))
            p.Add(new(VfSeverity.Information, "VF073", $"The header's unused size fields ({font.LegacyKernDataSize}, {font.LegacyCharDataSize}) do not match the tables ({font.Kerning.Length * 3}, {n * 16}); the game ignores them."));
        return p;
    }

    private static void CheckKerning(VfFont font, List<VfProblem> p, Func<int, string> ch)
    {
        int n = font.Glyphs.Length;
        var pairs = font.Kerning;
        string Pair(VfKernPair k) => $"{(k.Left < n ? ch(k.Left) : $"glyph {k.Left}")} + {(k.Right < n ? ch(k.Right) : $"glyph {k.Right}")} ({k.Offset:+0;-0;0})";
        var seen = new HashSet<(byte, byte)>();
        for (int i = 0; i < pairs.Length; i++)
        {
            var k = pairs[i];
            if (k.Left >= n || k.Right >= n)
            {
                p.Add(new(VfSeverity.Error, "VF040", $"Kerning pair {i + 1} ({Pair(k)}) names a glyph the font does not have ({n} glyphs).", k.Left < n ? k.Left : null));
                continue;
            }
            if (!seen.Add((k.Left, k.Right)))
                p.Add(new(VfSeverity.Information, "VF043", $"Kerning pair {Pair(k)} is listed more than once; only one can apply.", k.Left));
            else if (k.Left >= 128 || k.Right >= 128)
                p.Add(new(VfSeverity.Warning, "VF044", $"Kerning pair {Pair(k)} never applies: the game compares kerning glyph numbers as signed bytes, so pairs involving glyph 128 or later are ignored.", k.Left));
            else if (Applied(font, k) != k.Offset)
                p.Add(new(VfSeverity.Warning, "VF041", $"Kerning pair {Pair(k)} never applies in the game: pairs must be sorted by first, then second character, and the first character's kerning index must point at its first pair.", k.Left));
        }

        // Pairs the game applies to characters that have none of their own.
        foreach (var (left, right, k) in VfLayout.PhantomPairs(font))
        {
            var applied = pairs[k];
            p.Add(new(VfSeverity.Warning, "VF046", PhantomText(font, left, right, applied, ch), left));
        }

        // Each glyph's stored first-pair index against the table.
        for (int i = 0; i < n; i++)
        {
            short stored = font.Glyphs[i].FirstKernIndex;
            int expected = -1;
            for (int j = 0; j < pairs.Length; j++) if (pairs[j].Left == i) { expected = j; break; }
            if (stored == expected) continue;
            if (stored >= pairs.Length)
                p.Add(new(VfSeverity.Error, "VF045", $"{ch(i)}'s kerning index ({stored}) points past the kerning table ({pairs.Length} pairs); the game reads beyond it.", i));
            else if (stored >= 0 || expected >= 0)
                p.Add(new(VfSeverity.Warning, "VF042", expected < 0
                    ? $"{ch(i)}'s kerning index ({stored}) points at a pair for another character; it has no pairs of its own."
                    : $"{ch(i)}'s kerning index is {stored}, but its first pair is number {expected}; some of its pairs are not used.", i));
        }
    }

    /// <summary>
    /// "In the game, 'A' (65) followed by 'V' (86) is kerned -3 by the pair 'B' (66) + 'V' (86) …": what a phantom pair
    /// (<see cref="VfLayout.PhantomPairs"/>) does and why.
    /// </summary>
    public static string PhantomText(VfFont font, int left, int right, VfKernPair applied, Func<int, string>? describe = null)
    {
        describe ??= i => VfReader.Describe(font.CharacterOf(i));
        string own = font.Kerning.LastOrDefault(k => k.Left == left) is { } last ? $"after its last pair ({describe(last.Left)} + {describe(last.Right)})" : "from a pair of another character";
        string effect = applied.Offset < 0 ? $"{-applied.Offset} px closer" : $"{applied.Offset} px further apart";
        return $"In the game, {describe(left)} followed by {describe(right)} is drawn {effect} although the font has no pair for them: the game's kerning lookup "
            + $"for {describe(left)} goes on {own} and applies the next pair in the table, {describe(applied.Left)} + {describe(applied.Right)} ({applied.Offset:+0;-0;0}). "
            + $"Cairn's preview shows the same. Give {describe(left)} + {describe(right)} a pair of its own to set that spacing.";
    }

    /// <summary>The advance change the game applies for a pair (0 when it finds none).</summary>
    private static int Applied(VfFont font, VfKernPair k)
    {
        int c1 = font.CharacterOf(k.Left), c2 = font.CharacterOf(k.Right);
        if (c1 is < 0 or > 255 || c2 is < 1 or > 255 || c2 == '\n') return 0;
        return VfLayout.Advance(font, (byte)c1, (byte)c2, out _, out _) - font.Glyphs[k.Left].Spacing;
    }

    private static bool NibblesRepeat(uint v)
    {
        for (int s = 0; s < 32; s += 8)
        {
            uint b = (v >> s) & 0xFF;
            if ((b >> 4) != (b & 0xF)) return false;
        }
        return true;
    }

    private static bool Overlaps(VfFont font) =>
        font.Glyphs.Select(g => g.PixelOffset).Distinct().Count() < font.Glyphs.Count(g => g.Pixels.Length > 0);

    private static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        return all.Count <= 8 ? string.Join(", ", all) : string.Join(", ", all.Take(8)) + $" and {all.Count - 8} more";
    }
}
