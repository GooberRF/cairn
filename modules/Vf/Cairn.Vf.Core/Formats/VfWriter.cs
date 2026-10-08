using Cairn.Formats;
using Cairn.Vf.Model;

namespace Cairn.Vf.Formats;

/// <summary>
/// Writes VFNT fonts. An unchanged font read by <see cref="VfReader"/> comes back byte for byte: the stored pixel
/// data block, offsets, kerning indices, unused version 0 fields and trailing bytes are written as they are. When a
/// glyph's pixels no longer match the stored block at its offset, every glyph is laid out afresh, in order and
/// back to back (as all stock fonts are).
/// </summary>
public static class VfWriter
{
    /// <summary>The file bytes of <paramref name="font"/>.</summary>
    /// <exception cref="InvalidOperationException">The font cannot be written (version 0 with a format other than monochrome, wrong palette size, glyph pixels of the wrong size).</exception>
    public static byte[] Write(VfFont font)
    {
        ArgumentNullException.ThrowIfNull(font);
        if (font.Version is < 0 or > 1) throw new InvalidOperationException($"Font version {font.Version} cannot be written (0 or 1).");
        if (font.Version == 0 && font.Format != VfPixelFormat.Mono) throw new InvalidOperationException("A version 0 font can only hold monochrome pixels; save it as version 1.");
        if (!VfFont.IsKnown(font.Format)) throw new InvalidOperationException($"Pixel format 0x{(uint)font.Format:X8} cannot be written.");
        if (font.Format == VfPixelFormat.Indexed && font.Palette.Length != VfFont.PaletteSize) throw new InvalidOperationException($"An indexed font needs {VfFont.PaletteSize} palette entries (has {font.Palette.Length}).");
        for (int i = 0; i < font.Glyphs.Length; i++)
            if (font.Glyphs[i].Pixels.Length != font.PixelBytes(font.Glyphs[i].Width))
                throw new InvalidOperationException($"Glyph {i} has {font.Glyphs[i].Pixels.Length} bytes of pixels; {font.Glyphs[i].Width} × {font.Height} needs {font.PixelBytes(font.Glyphs[i].Width)}.");

        var (pixelData, offsets) = PixelLayout(font);
        var b = new BinaryBuilder(64 + font.Kerning.Length * 3 + font.Glyphs.Length * 16 + pixelData.Length + font.Palette.Length * 4 + font.TrailingData.Length);
        b.WriteUInt32(VfFont.Signature);
        b.WriteInt32(font.Version);
        if (font.Version >= 1) b.WriteUInt32((uint)font.Format);
        b.WriteInt32(font.Glyphs.Length);
        b.WriteInt32(font.FirstCharacter);
        b.WriteInt32(font.DefaultSpacing);
        b.WriteInt32(font.Height);
        b.WriteInt32(font.Kerning.Length);
        if (font.Version == 0) { b.WriteUInt32(font.LegacyKernDataSize); b.WriteUInt32(font.LegacyCharDataSize); }
        b.WriteInt32(pixelData.Length);
        foreach (var k in font.Kerning) { b.WriteByte(k.Left); b.WriteByte(k.Right); b.WriteSByte(k.Offset); }
        for (int i = 0; i < font.Glyphs.Length; i++)
        {
            var g = font.Glyphs[i];
            b.WriteInt32(g.Spacing);
            b.WriteInt32(g.Width);
            b.WriteUInt32(offsets[i]);
            b.WriteInt16(g.FirstKernIndex);
            b.WriteUInt16(g.UserData);
        }
        b.WriteBytes(pixelData);
        foreach (uint entry in font.Palette) b.WriteUInt32(entry);
        b.WriteBytes(font.TrailingData.AsSpan());
        return b.ToArray();
    }

    /// <summary>True when every glyph's pixels equal the stored pixel data at the glyph's stored offset (the block can be written as it is).</summary>
    public static bool StoredLayoutMatches(VfFont font)
    {
        var data = font.PixelData.AsSpan();
        foreach (var g in font.Glyphs)
        {
            if ((long)g.PixelOffset + g.Pixels.Length > data.Length) return false;
            if (!data.Slice((int)g.PixelOffset, g.Pixels.Length).SequenceEqual(g.Pixels.AsSpan())) return false;
        }
        return true;
    }

    /// <summary>The pixel block and per-glyph offsets the writer uses: the stored ones when they still match, else a fresh back-to-back layout.</summary>
    public static (byte[] Data, uint[] Offsets) PixelLayout(VfFont font)
    {
        if (StoredLayoutMatches(font)) return (font.PixelData.ToArray(), [.. font.Glyphs.Select(g => g.PixelOffset)]);
        return PackedLayout(font);
    }

    /// <summary>Every glyph's pixels back to back, in glyph order.</summary>
    public static (byte[] Data, uint[] Offsets) PackedLayout(VfFont font)
    {
        var offsets = new uint[font.Glyphs.Length];
        var data = new byte[font.Glyphs.Sum(g => (long)g.Pixels.Length)];
        int at = 0;
        for (int i = 0; i < offsets.Length; i++)
        {
            offsets[i] = (uint)at;
            font.Glyphs[i].Pixels.AsSpan().CopyTo(data.AsSpan(at));
            at += font.Glyphs[i].Pixels.Length;
        }
        return (data, offsets);
    }
}
