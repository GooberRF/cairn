using System.Runtime.InteropServices;
using Cairn.Formats;
using Cairn.Vf.Model;

namespace Cairn.Vf.Formats;

/// <summary>
/// Reads VFNT fonts (<c>.vf</c>), versions 0 and 1, in all three pixel formats. Layout (little-endian): signature,
/// version, [v1: format], glyph count, first character, default spacing, height, kerning pair count,
/// [v0: kerning data size, character data size], pixel data size; then the kerning pairs (3 bytes each), the glyph
/// table (16 bytes each), the pixel data and, for indexed fonts, 256 palette entries. Every count and offset is
/// checked against the bytes present before anything is allocated.
/// </summary>
public static class VfReader
{
    /// <summary>Header size of version 0 files.</summary>
    public const int HeaderSizeV0 = 40;
    /// <summary>Header size of version 1 files.</summary>
    public const int HeaderSizeV1 = 36;
    /// <summary>Bytes per kerning pair.</summary>
    public const int KernPairSize = 3;
    /// <summary>Bytes per glyph table entry.</summary>
    public const int GlyphEntrySize = 16;
    /// <summary>The tallest font read (the game's font texture is 256 pixels high; anything far beyond is damage).</summary>
    public const int MaxHeight = 1024;
    /// <summary>The widest glyph read (the game's font texture is 256 pixels wide; anything far beyond is damage).</summary>
    public const int MaxGlyphWidth = 4096;
    /// <summary>
    /// How many bytes of glyph pixels may lie past the pixel data (shown as clear) before the file counts as damaged
    /// rather than truncated. Also the slack over twice the file size that glyphs sharing pixels may add up to.
    /// </summary>
    public const long MissingSlack = 1 << 20;

    /// <summary>Reads a font, throwing on anything it cannot read.</summary>
    /// <exception cref="AssetFormatException">Not a VFNT font, an unsupported version or pixel format, or damaged.</exception>
    public static VfFont Read(byte[] bytes, string name) => Read(bytes, name, null);

    /// <summary>
    /// Reads a font. Problems that still leave a usable font (a glyph whose pixels lie outside the pixel data, bytes
    /// after the end) are added to <paramref name="problems"/>; anything worse throws.
    /// </summary>
    /// <exception cref="AssetFormatException">Not a VFNT font, an unsupported version or pixel format, or damaged.</exception>
    public static VfFont Read(byte[] bytes, string name, ICollection<VfProblem>? problems)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var c = new BinaryCursor(bytes, name);
        if (bytes.Length < 8 || c.ReadUInt32("signature") != VfFont.Signature)
            throw new AssetFormatException($"'{name}' is not a VFNT font (the file does not start with \"VFNT\").");
        int version = c.ReadInt32("version");
        if (version is < 0 or > 1)
            throw new AssetFormatException($"'{name}' is font version {version}; the game reads versions 0 and 1 only.");
        c.Need((version == 0 ? HeaderSizeV0 : HeaderSizeV1) - 8, "the header");
        var format = version == 0 ? VfPixelFormat.Mono : (VfPixelFormat)c.ReadUInt32("format");
        if (!VfFont.IsKnown(format))
            throw new AssetFormatException($"'{name}' uses pixel format 0x{(uint)format:X8}, which the game does not load "
                + "(it reads 0x0000000F monochrome, 0x0F0F0F0F RGBA 4444 and 0xFFFFFFF0 8-bit indexed).");
        int count = c.ReadInt32();
        int first = c.ReadInt32();
        int defaultSpacing = c.ReadInt32();
        int height = c.ReadInt32();
        int kernCount = c.ReadInt32();
        uint legacyKern = (uint)Math.Max(0, kernCount) * KernPairSize, legacyChar = (uint)Math.Max(0, count) * GlyphEntrySize;
        if (version == 0) { legacyKern = c.ReadUInt32(); legacyChar = c.ReadUInt32(); }
        int pixelSize = c.ReadInt32();

        c.EnsureCount(kernCount, KernPairSize, "the kerning table");
        var kerning = ImmutableArray.CreateBuilder<VfKernPair>(kernCount);
        for (int i = 0; i < kernCount; i++) kerning.Add(new VfKernPair(c.ReadByte(), c.ReadByte(), c.ReadSByte()));

        c.EnsureCount(count, GlyphEntrySize, "the glyph table");
        var entries = new (int Spacing, int Width, uint Offset, short Kern, ushort User)[count];
        for (int i = 0; i < count; i++)
            entries[i] = (c.ReadInt32(), c.ReadInt32(), c.ReadUInt32(), c.ReadInt16(), c.ReadUInt16());

        c.EnsureCount(pixelSize, 1, "the pixel data");
        var pixelData = ImmutableArray.Create(c.ReadBytes(pixelSize).ToArray());
        var palette = ImmutableArray<uint>.Empty;
        if (format == VfPixelFormat.Indexed)
        {
            c.EnsureCount(VfFont.PaletteSize, 4, "the palette");
            var p = new uint[VfFont.PaletteSize];
            for (int i = 0; i < p.Length; i++) p[i] = c.ReadUInt32("the palette");
            palette = ImmutableArray.Create(p);
        }
        var trailing = c.Remaining > 0 ? ImmutableArray.Create(c.ReadBytes(c.Remaining).ToArray()) : [];
        if (trailing.Length > 0)
            problems?.Add(new(VfSeverity.Warning, "VF020", $"{trailing.Length:N0} bytes after the end of the font are ignored by the game."));

        // Sizes the game could never use are damage: its font texture is at most 256 × 256 pixels.
        if (height > MaxHeight)
            throw new AssetFormatException($"'{name}' is damaged: its height is {height:N0} pixels (fonts are at most {MaxHeight:N0} pixels high; the game's font texture is 256 pixels).");
        int bpp = VfFont.BytesPer(format);
        var glyphs = ImmutableArray.CreateBuilder<VfGlyph>(count);
        long missingTotal = 0, allocated = 0, budget = MissingSlack + 2L * bytes.Length;
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            if (e.Width > MaxGlyphWidth)
                throw new AssetFormatException($"'{name}' is damaged: {Describe(first + i)} claims to be {e.Width:N0} pixels wide (at most {MaxGlyphWidth:N0}).");
            int width = e.Width;
            if (width < 0)
            {
                // The editing operations and the writer need a real width; the glyph draws nothing either way.
                problems?.Add(new(VfSeverity.Error, "VF050", $"{Describe(first + i)} has a negative width ({e.Width}), which is damage. Cairn reads it as 0 (an empty glyph); saving writes 0.", i));
                width = 0;
            }
            long size = (long)width * Math.Max(0, height) * bpp;
            long available = Math.Clamp(pixelData.Length - (long)e.Offset, 0, size);
            // Pixels past the data are shown as clear; a little of that is a truncated file, a lot is a damaged table
            // (16 table bytes must never make Cairn allocate megabytes).
            missingTotal += size - available;
            allocated += size;
            if (missingTotal > MissingSlack || allocated > budget)
                throw new AssetFormatException($"'{name}' is damaged: its glyph table gives the glyphs up to {Describe(first + i)} {allocated:N0} bytes of pixels, but the file holds only {pixelData.Length:N0} bytes of pixel data.");
            var pixels = new byte[size];
            if (available > 0) pixelData.AsSpan().Slice((int)e.Offset, (int)available).CopyTo(pixels);
            if (available < size)
                problems?.Add(new(VfSeverity.Error, "VF010",
                    $"The pixels of {Describe(first + i)} (offset {e.Offset:N0}, {size:N0} bytes) run past the end of the pixel data ({pixelData.Length:N0} bytes); the game would draw whatever lies beyond. Cairn shows the missing part as clear.", i));
            glyphs.Add(new VfGlyph(e.Spacing, width, e.Offset, e.Kern, e.User, ImmutableCollectionsMarshal.AsImmutableArray(pixels)));
        }

        return new VfFont
        {
            Version = version,
            Format = format,
            FirstCharacter = first,
            DefaultSpacing = defaultSpacing,
            Height = height,
            Kerning = kerning.MoveToImmutable(),
            Glyphs = glyphs.MoveToImmutable(),
            Palette = palette,
            PixelData = pixelData,
            LegacyKernDataSize = legacyKern,
            LegacyCharDataSize = legacyChar,
            TrailingData = trailing,
        };
    }

    /// <summary>Reads a font and validates it: every problem, or the reason it cannot be read as one error. Never throws for bad data.</summary>
    public static (VfFont? Font, IReadOnlyList<VfProblem> Problems) Inspect(byte[] bytes, string name)
    {
        var problems = new List<VfProblem>();
        try
        {
            var font = Read(bytes, name, problems);
            problems.AddRange(Validation.VfValidator.Validate(font));
            return (font, problems);
        }
        catch (AssetFormatException ex)
        {
            problems.Add(new(VfSeverity.Error, "VF000", ex.Message));
            return (null, problems);
        }
    }

    /// <summary>"'A' (65)", "space (32)".</summary>
    public static string Describe(int code)
    {
        string text = VfFont.CharacterText(code);
        return text.Length == 1 ? $"'{text}' ({code})" : $"{text} ({code})";
    }
}
