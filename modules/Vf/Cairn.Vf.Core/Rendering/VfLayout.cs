using Cairn.Vf.Model;

namespace Cairn.Vf.Rendering;

/// <summary>A glyph placed by <see cref="VfLayout.Layout"/>: its index and the top-left corner of its rectangle.</summary>
public readonly record struct VfPlacedGlyph(int Glyph, int X, int Y);

/// <summary>A laid-out string.</summary>
/// <param name="Glyphs">The glyphs drawn, in order (characters without a glyph are not listed).</param>
/// <param name="Width">The width the game reports for the string (sum of advances; the widest line).</param>
/// <param name="Height">The height the game reports (font height per line).</param>
/// <param name="Right">The right edge of the rightmost drawn pixel column (may exceed <see cref="Width"/> when glyphs overhang).</param>
public sealed record VfTextLayout(ImmutableArray<VfPlacedGlyph> Glyphs, int Width, int Height, int Right);

/// <summary>
/// Text layout exactly as the game does it (stock <c>gr_string</c> / <c>gr_get_string_size</c>, which Alpine Faction
/// keeps for <c>.vf</c> fonts): the pen starts at the left; each character is drawn at the pen with its glyph's
/// width and the font height, then the pen moves by the glyph's spacing plus the kerning offset for the next
/// character. A character the font has no glyph for moves the pen by the default spacing and draws nothing.
/// A line feed starts a new line one font height lower; other characters below 32 are skipped when drawing but
/// still counted (default spacing) when the game measures a string.
/// </summary>
public static class VfLayout
{
    /// <summary>
    /// The pen advance after character <paramref name="c1"/> when <paramref name="c2"/> follows (0 = end of text), and
    /// the glyph drawn (-1 for none) with its width. Mirrors the game's lookup, including its kerning scan: starting at
    /// the left glyph's first pair, it walks pairs with the same left glyph while their right glyph is lower than the
    /// wanted one, then applies the pair it stopped on when that pair's right glyph matches. Glyph indices in pairs are
    /// compared as signed bytes, as the game does, so pairs for glyph 128 and above never apply.
    /// </summary>
    public static int Advance(VfFont font, byte c1, byte c2, out int glyph, out int width)
    {
        glyph = c1 - font.FirstCharacter;
        if (glyph < 0 || glyph >= font.Glyphs.Length) { glyph = -1; width = 0; return font.DefaultSpacing; }
        var g = font.Glyphs[glyph];
        width = g.Width;
        int spacing = g.Spacing;
        if (c2 == 0 || c2 == (byte)'\n') return spacing;
        int k = AppliedPair(font, glyph, c2 - font.FirstCharacter);
        return k >= 0 ? spacing + font.Kerning[k].Offset : spacing;
    }

    /// <summary>
    /// The index of the kerning pair the game applies when glyph <paramref name="left"/> is followed by glyph
    /// <paramref name="right"/>, or -1 for none. This is the game's scan (see <see cref="Advance"/>): it can stop on a
    /// pair of the next left glyph and apply it, so the pair found is not always one of <paramref name="left"/>'s.
    /// </summary>
    public static int AppliedPair(VfFont font, int left, int right)
    {
        if (left < 0 || left >= font.Glyphs.Length || right < 0 || right >= font.Glyphs.Length) return -1;
        int k = font.Glyphs[left].FirstKernIndex;
        var pairs = font.Kerning;
        if (k <= -1 || k >= pairs.Length) return -1; // past the table the game would read beyond it
        if ((sbyte)pairs[k].Left == left)
        {
            while ((sbyte)pairs[k].Right < right && k < pairs.Length - 1)
            {
                k++;
                if ((sbyte)pairs[k].Left != left) break;
            }
        }
        return (sbyte)pairs[k].Right == right ? k : -1;
    }

    /// <summary>
    /// Character pairs the game kerns although the table has no pair for them: its scan runs past the left glyph's own
    /// pairs and applies the pair it stopped on (another left glyph's), so adding or changing a pair can move other
    /// text. Each entry names the two glyphs and the index of the pair applied.
    /// </summary>
    public static IReadOnlyList<(int Left, int Right, int Pair)> PhantomPairs(VfFont font)
    {
        var found = new List<(int, int, int)>();
        var pairs = font.Kerning;
        if (pairs.IsDefaultOrEmpty) return found;
        var listed = new HashSet<(int, int)>(pairs.Select(p => ((int)p.Left, (int)p.Right)));
        var rights = pairs.Select(p => (int)p.Right).Where(r => r < font.Glyphs.Length).Distinct().Order().ToList();
        for (int left = 0; left < font.Glyphs.Length; left++)
        {
            int c1 = font.CharacterOf(left);
            if (c1 is < 0x20 or > 0xFF) continue; // never drawn
            foreach (int right in rights)
            {
                int c2 = font.CharacterOf(right);
                if (c2 is < 1 or > 0xFF || c2 == '\n' || listed.Contains((left, right))) continue;
                int k = AppliedPair(font, left, right);
                if (k >= 0 && pairs[k].Left != left) found.Add((left, right, k));
            }
        }
        return found;
    }

    /// <summary>Lays out <paramref name="text"/> (bytes in the game's code page) as the game draws it.</summary>
    public static VfTextLayout Layout(VfFont font, ReadOnlySpan<byte> text)
    {
        var placed = ImmutableArray.CreateBuilder<VfPlacedGlyph>();
        // Positions are summed in 64 bits and kept within ±1e9: a damaged font's spacings must not wrap around.
        long x = 0, y = 0, right = 0;
        for (int i = 0; i < text.Length && text[i] != 0; i++)
        {
            byte c = text[i];
            if (c < 0x20)
            {
                if (c == (byte)'\n') { y = Clamp(y + font.Height); x = 0; }
                continue;
            }
            byte next = i + 1 < text.Length ? text[i + 1] : (byte)0;
            int advance = Advance(font, c, next, out int glyph, out int width);
            if (glyph >= 0)
            {
                placed.Add(new VfPlacedGlyph(glyph, (int)x, (int)y));
                right = Math.Max(right, Clamp(x + width));
            }
            x = Clamp(x + advance);
            right = Math.Max(right, x);
        }
        var (w, h) = Measure(font, text);
        return new VfTextLayout(placed.ToImmutable(), w, h, (int)Math.Max(right, w));
    }

    private const long Limit = 1_000_000_000;

    private static long Clamp(long v) => Math.Clamp(v, -Limit, Limit);

    /// <summary>The size the game reports for <paramref name="text"/> (<c>gr_get_string_size</c>).</summary>
    public static (int Width, int Height) Measure(VfFont font, ReadOnlySpan<byte> text)
    {
        long line = 0, widest = 0, height = font.Height;
        for (int i = 0; i < text.Length && text[i] != 0; i++)
        {
            byte c = text[i];
            byte next = i + 1 < text.Length ? text[i + 1] : (byte)0;
            if (c == (byte)'\n')
            {
                line = 0;
                if (next != 0) height = Clamp(height + font.Height);
                continue;
            }
            line = Clamp(line + Advance(font, c, next, out _, out _));
            widest = Math.Max(widest, line);
        }
        return ((int)widest, (int)height);
    }

    /// <summary>Text as the game's code page bytes (characters it cannot hold become '?').</summary>
    public static byte[] Encode(string text) => VfFont.TextEncoding.GetBytes(text ?? string.Empty);

    /// <summary>Every character of the font, <paramref name="perLine"/> to a line, as bytes for <see cref="Layout"/>.</summary>
    public static byte[] AllCharacters(VfFont font, int perLine = 16)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < font.Glyphs.Length; i++)
        {
            int code = font.CharacterOf(i);
            if (code is < 0x20 or > 0xFF) continue;
            if (bytes.Count > 0 && i % perLine == 0) bytes.Add((byte)'\n');
            bytes.Add((byte)code);
        }
        return [.. bytes];
    }
}
