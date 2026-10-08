using System.Text;

namespace Cairn.Vf.Model;

/// <summary>The pixel formats of a VFNT font (the header's format field; version 0 files are always <see cref="Mono"/>).</summary>
public enum VfPixelFormat : uint
{
    /// <summary>One byte per pixel: coverage 0 (clear) to 14 (solid), drawn white.</summary>
    Mono = 0x0000000F,
    /// <summary>Two bytes per pixel, 4 bits each of alpha, red, green and blue (alpha in the top bits).</summary>
    Rgba4444 = 0x0F0F0F0F,
    /// <summary>One byte per pixel, an index into the 256-colour palette stored after the pixels.</summary>
    Indexed = 0xFFFFFFF0,
}

/// <summary>One kerning pair: when glyph <see cref="Left"/> is followed by glyph <see cref="Right"/>, the pen moves <see cref="Offset"/> more pixels.</summary>
/// <param name="Left">Glyph index (character minus the font's first character) of the first character.</param>
/// <param name="Right">Glyph index of the following character.</param>
/// <param name="Offset">Added to the left glyph's spacing (usually negative).</param>
public sealed record VfKernPair(byte Left, byte Right, sbyte Offset);

/// <summary>
/// One glyph as the file stores it: metrics, the glyph table's bookkeeping fields, and its pixels (row-major,
/// <c>Width × font height</c> pixels of the font's <see cref="VfFont.BytesPerPixel"/>).
/// </summary>
/// <param name="Spacing">How far the pen moves after this character, in pixels.</param>
/// <param name="Width">Width of the glyph's pixels (the drawn rectangle is Width × font height).</param>
/// <param name="PixelOffset">Byte offset of the pixels in the file's pixel data, as stored.</param>
/// <param name="FirstKernIndex">Index of this glyph's first kerning pair, or -1 for none, as stored.</param>
/// <param name="UserData">A 16-bit field the game ignores (0 in every known font).</param>
/// <param name="Pixels">The glyph's pixels.</param>
public sealed record VfGlyph(int Spacing, int Width, uint PixelOffset, short FirstKernIndex, ushort UserData, ImmutableArray<byte> Pixels);

/// <summary>
/// A VFNT bitmap font (<c>.vf</c>) as an immutable snapshot. It holds every field the file stores, so writing an
/// unchanged font gives back the same bytes; the edit helpers in <see cref="VfEdits"/> keep the derived fields (pixel
/// offsets, kerning indices, unused size fields) consistent. Glyph <c>i</c> is the character <c>FirstCharacter + i</c>.
/// </summary>
public sealed record VfFont
{
    /// <summary>The file signature "VFNT" as a little-endian integer.</summary>
    public const uint Signature = 0x544E4656;

    /// <summary>The number of palette entries an indexed font stores.</summary>
    public const int PaletteSize = 256;

    /// <summary>Format version: 0 (40-byte header, always <see cref="VfPixelFormat.Mono"/>) or 1 (36-byte header with a format field).</summary>
    public int Version { get; init; } = 1;
    /// <summary>The pixel format.</summary>
    public VfPixelFormat Format { get; init; } = VfPixelFormat.Mono;
    /// <summary>The character code of glyph 0 (32, the space, in every stock font).</summary>
    public int FirstCharacter { get; init; } = 32;
    /// <summary>How far the pen moves for a character the font has no glyph for.</summary>
    public int DefaultSpacing { get; init; }
    /// <summary>The height of every glyph, in pixels (also the line height).</summary>
    public int Height { get; init; }
    /// <summary>The kerning pairs, in file order (the game expects them sorted by left, then right glyph).</summary>
    public ImmutableArray<VfKernPair> Kerning { get; init; } = [];
    /// <summary>The glyphs, glyph i being the character <see cref="FirstCharacter"/> + i.</summary>
    public ImmutableArray<VfGlyph> Glyphs { get; init; } = [];
    /// <summary>The 256 palette entries of an indexed font (0xAARRGGBB), else empty.</summary>
    public ImmutableArray<uint> Palette { get; init; } = [];
    /// <summary>
    /// The pixel data block as read (or as last laid out by <see cref="VfEdits.Normalize"/>). The writer stores it
    /// unchanged while every glyph's pixels still match it at their offsets, and lays the pixels out afresh otherwise.
    /// </summary>
    public ImmutableArray<byte> PixelData { get; init; } = [];
    /// <summary>Version 0's "kerning data size" field, as stored (the game ignores it; normally pairs × 3).</summary>
    public uint LegacyKernDataSize { get; init; }
    /// <summary>Version 0's "character data size" field, as stored (the game ignores it; normally glyphs × 16).</summary>
    public uint LegacyCharDataSize { get; init; }
    /// <summary>Bytes after the end of the font (the game ignores them); kept so the file round-trips.</summary>
    public ImmutableArray<byte> TrailingData { get; init; } = [];

    /// <summary>Bytes per pixel of <see cref="Format"/> (2 for RGBA 4444, else 1).</summary>
    public int BytesPerPixel => BytesPer(Format);

    /// <summary>The number of glyphs.</summary>
    public int GlyphCount => Glyphs.Length;

    /// <summary>The character code of the last glyph.</summary>
    public int LastCharacter => FirstCharacter + Glyphs.Length - 1;

    /// <summary>The character code glyph <paramref name="index"/> draws.</summary>
    public int CharacterOf(int index) => FirstCharacter + index;

    /// <summary>The glyph index of character code <paramref name="code"/> (0-255), or -1 when the font has none.</summary>
    public int IndexOf(int code)
    {
        int i = (code & 0xFF) - FirstCharacter;
        return i >= 0 && i < Glyphs.Length ? i : -1;
    }

    /// <summary>The widest glyph's width.</summary>
    public int MaxGlyphWidth => Glyphs.IsDefaultOrEmpty ? 0 : Glyphs.Max(g => g.Width);

    /// <summary>The number of bytes glyph pixels of <paramref name="width"/> take in this font.</summary>
    public int PixelBytes(int width) => Math.Max(0, width) * Math.Max(0, Height) * BytesPerPixel;

    /// <summary>Bytes per pixel of a format.</summary>
    public static int BytesPer(VfPixelFormat format) => format == VfPixelFormat.Rgba4444 ? 2 : 1;

    /// <summary>"4-bit monochrome", "RGBA 4444", "8-bit indexed (palette)".</summary>
    public static string FormatName(VfPixelFormat format) => format switch
    {
        VfPixelFormat.Mono => "4-bit monochrome",
        VfPixelFormat.Rgba4444 => "RGBA 4444",
        VfPixelFormat.Indexed => "8-bit indexed (palette)",
        _ => $"0x{(uint)format:X8}",
    };

    /// <summary>True for the three formats the game loads.</summary>
    public static bool IsKnown(VfPixelFormat format) => format is VfPixelFormat.Mono or VfPixelFormat.Rgba4444 or VfPixelFormat.Indexed;

    private static readonly Lazy<Encoding> Ansi = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
    });

    /// <summary>The Windows code page the game's text uses (1252): text typed for a preview is encoded with it.</summary>
    public static Encoding TextEncoding => Ansi.Value;

    /// <summary>The character a code stands for in <see cref="TextEncoding"/> ("A", "é"), or a name for invisible ones ("space").</summary>
    public static string CharacterText(int code)
    {
        if (code < 0 || code > 255) return "?";
        if (code == 32) return "space";
        if (code == 160) return "no-break space";
        if (code == 173) return "soft hyphen";
        if (code < 32 || code == 127) return $"control {code}";
        string s = TextEncoding.GetString([(byte)code]);
        return s.Length != 1 || s[0] == '�' || char.IsControl(s[0]) || (s[0] == '?' && code != '?') ? $"undefined {code}" : s;
    }
}
