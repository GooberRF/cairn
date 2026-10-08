using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cairn.Formats.Imaging;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;

namespace Cairn.Vf.Sheets;

/// <summary>How an image sheet is laid out.</summary>
/// <param name="Columns">Cells per row.</param>
/// <param name="ExtraWidth">Clear columns added to every cell beyond the widest glyph (room to widen glyphs in an image editor).</param>
/// <param name="Guide">The colour (0xAARRGGBB) outside the glyphs and between cells, or null for clear.</param>
/// <param name="Mono">How monochrome pixels are drawn.</param>
public sealed record VfSheetOptions(int Columns = 16, int ExtraWidth = 4, uint? Guide = VfSheet.DefaultGuide, VfMonoStyle Mono = VfMonoStyle.Gray);

/// <summary>Why an image sheet could not be read back.</summary>
public sealed class VfSheetException(string message) : Exception(message);

/// <summary>
/// Exports a font as an image sheet (a PNG of glyph cells plus a JSON sidecar with every metric) and reads an edited
/// sheet back. Cells form a grid with a 1-pixel line between them; each glyph sits at the top left of its cell, its
/// width × the font height; the rest of the cell and the lines are the guide colour. The sidecar's widths, spacings,
/// user data, kerning, default spacing and palette are applied on import, so metrics can be edited there too. An
/// exported sheet imported unchanged gives back the same font bytes.
/// </summary>
public static class VfSheet
{
    /// <summary>The sidecar's "kind" value.</summary>
    public const string Kind = "cairn-vf-sheet";

    /// <summary>The default guide colour (opaque magenta).</summary>
    public const uint DefaultGuide = 0xFFFF00FF;

    /// <summary>The most cells per row a sidecar may give (export uses at most this many).</summary>
    public const int MaxColumns = 256;
    /// <summary>The widest cell a sidecar may give: the widest glyph read plus the most extra width export adds.</summary>
    public const int MaxCellWidth = VfReader.MaxGlyphWidth + 255;
    /// <summary>The most glyphs a sidecar may list.</summary>
    public const int MaxGlyphs = 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "é", not "é": the file is edited by hand
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The sheet image and its sidecar text for <paramref name="font"/>.</summary>
    /// <param name="font">The font.</param>
    /// <param name="imageName">The PNG's file name, recorded in the sidecar.</param>
    /// <param name="options">Layout; null for the defaults.</param>
    public static (BgraImage Image, string Json) Export(VfFont font, string imageName, VfSheetOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        options ??= new VfSheetOptions();
        if (font.GlyphCount == 0 || font.Height <= 0) throw new VfSheetException("A font without glyphs or height cannot be exported as a sheet.");
        int columns = Math.Clamp(options.Columns, 1, 256);
        int cellW = Math.Max(1, font.MaxGlyphWidth) + Math.Clamp(options.ExtraWidth, 0, 255), cellH = font.Height;
        int rows = (font.GlyphCount + columns - 1) / columns;
        var image = new BgraImage(1 + columns * (cellW + 1), 1 + rows * (cellH + 1));
        uint guide = options.Guide ?? 0;
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++) Put(image, x, y, guide);
        for (int i = 0; i < font.GlyphCount; i++)
        {
            var (cx, cy) = CellOrigin(i, columns, cellW, cellH);
            var g = font.Glyphs[i];
            for (int y = 0; y < cellH; y++)
                for (int x = 0; x < g.Width; x++)
                    Put(image, cx + x, cy + y, VfPixelConvert.SheetColour(font, VfRender.RawPixel(font, g, x, y), options.Mono));
        }
        var doc = new SheetDoc
        {
            Kind = Kind,
            Version = 1,
            Image = imageName,
            Font = new FontDoc { Version = font.Version, Format = FormatId(font.Format), Height = font.Height, FirstCharacter = font.FirstCharacter, DefaultSpacing = font.DefaultSpacing },
            Layout = new LayoutDoc { Columns = columns, CellWidth = cellW, CellHeight = cellH, Gap = 1, MonoStyle = options.Mono == VfMonoStyle.Alpha ? "alpha" : "gray", Guide = options.Guide is { } c ? $"#{c:X8}" : null },
            Glyphs = [.. font.Glyphs.Select((g, i) => new GlyphDoc { Code = font.CharacterOf(i), Char = VfFont.CharacterText(font.CharacterOf(i)), Width = g.Width, Spacing = g.Spacing, UserData = g.UserData })],
            Kerning = [.. font.Kerning.Select(k => new KernDoc { Left = font.CharacterOf(k.Left), Right = font.CharacterOf(k.Right), Offset = k.Offset })],
            Palette = font.Format == VfPixelFormat.Indexed ? [.. font.Palette.Select(p => p.ToString("X8", CultureInfo.InvariantCulture))] : null,
        };
        return (image, JsonSerializer.Serialize(doc, Json));
    }

    /// <summary>The top-left pixel of glyph <paramref name="index"/>'s cell.</summary>
    public static (int X, int Y) CellOrigin(int index, int columns, int cellWidth, int cellHeight) =>
        (1 + index % columns * (cellWidth + 1), 1 + index / columns * (cellHeight + 1));

    /// <summary>The PNG name a sidecar names (null when it names none or cannot be read).</summary>
    public static string? ImageNameOf(string json)
    {
        try { return JsonSerializer.Deserialize<SheetDoc>(json, Json)?.Image; }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Reads an edited sheet back into <paramref name="current"/>: the sidecar sets the character range, metrics,
    /// kerning, height, format and palette; each glyph's pixels come from its cell. Pixels whose colour is what the
    /// export wrote for the current value keep that value, so an unchanged sheet gives back <paramref name="current"/>
    /// itself.
    /// </summary>
    /// <exception cref="VfSheetException">The sidecar or the image does not describe a sheet Cairn can read (the message says why).</exception>
    public static VfFont Import(VfFont current, BgraImage image, string json)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(image);
        SheetDoc doc;
        try { doc = JsonSerializer.Deserialize<SheetDoc>(json, Json) ?? throw new VfSheetException("The sidecar is empty."); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException) { throw new VfSheetException($"The sidecar is not valid JSON: {ex.Message}"); }
        if (doc.Kind != Kind) throw new VfSheetException($"The sidecar is not a Cairn font sheet (its \"kind\" should be \"{Kind}\").");
        if (doc.Font is not { } f || doc.Layout is not { } l || doc.Glyphs is not { Count: > 0 } glyphs)
            throw new VfSheetException("The sidecar lacks its \"font\", \"layout\" or \"glyphs\" section.");
        // The sidecar is edited by hand: every number is checked before it sizes anything.
        if (glyphs.Count > MaxGlyphs) throw new VfSheetException($"The sidecar lists {glyphs.Count:N0} glyphs; a font has at most {MaxGlyphs:N0}.");
        int empty = glyphs.IndexOf(null);
        if (empty >= 0) throw new VfSheetException($"Glyph {empty + 1} of the sidecar's \"glyphs\" list is empty (null); each needs its code, width and spacing.");
        var format = ParseFormat(f.Format);
        if (f.Height is < 1 or > 255) throw new VfSheetException($"The height {f.Height} is not 1 to 255.");
        if (f.FirstCharacter is < 0 or > 255) throw new VfSheetException($"The first character {f.FirstCharacter} is not 0 to 255.");
        if (l.Columns is < 1 or > MaxColumns || l.CellWidth is < 1 or > MaxCellWidth || l.CellHeight != f.Height || l.Gap != 1)
            throw new VfSheetException($"The sidecar's layout is not one Cairn wrote (1 to {MaxColumns} columns, cells 1 to {MaxCellWidth} pixels wide and as tall as the font, a 1-pixel gap).");
        long rows = (glyphs.Count + l.Columns - 1) / l.Columns;
        long needW = 1 + (long)l.Columns * (l.CellWidth + 1), needH = 1 + rows * (l.CellHeight + 1);
        if (image.Width < needW || image.Height < needH)
            throw new VfSheetException($"The image is {image.Width} × {image.Height}; the sidecar's {glyphs.Count} cells need {needW} × {needH}. Keep the sheet's size when editing it.");

        ImmutableArray<uint> palette = [];
        if (format == VfPixelFormat.Indexed)
        {
            if (doc.Palette is not { Count: VfFont.PaletteSize } entries)
                throw new VfSheetException($"An indexed font's sidecar needs {VfFont.PaletteSize} palette entries.");
            var parsed = new uint[VfFont.PaletteSize];
            for (int i = 0; i < parsed.Length; i++)
                if (entries[i] is not { } entry || !uint.TryParse(entry.Trim().TrimStart('#').Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed[i]))
                    throw new VfSheetException($"Palette entry {i} ({(entries[i] is null ? "empty" : $"\"{entries[i]}\"")}) is not a hex colour AARRGGBB.");
            palette = ImmutableArray.Create(parsed);
        }
        var mono = string.Equals(l.MonoStyle, "alpha", StringComparison.OrdinalIgnoreCase) ? VfMonoStyle.Alpha : VfMonoStyle.Gray;
        var coverage = mono == VfMonoStyle.Gray && (format == VfPixelFormat.Mono || VfPixelConvert.IsWhitePalette(current with { Format = format, Palette = palette })) ? VfCoverage.Luminance : VfCoverage.Alpha;
        var target = current with
        {
            Format = format,
            Version = format == VfPixelFormat.Mono ? current.Version : 1,
            Height = f.Height,
            FirstCharacter = f.FirstCharacter,
            DefaultSpacing = f.DefaultSpacing,
            Palette = palette,
        };
        bool sameBasis = current.Format == format && current.Height == f.Height && (format != VfPixelFormat.Indexed || current.Palette.SequenceEqual(palette));
        int bpp = target.BytesPerPixel;
        var built = ImmutableArray.CreateBuilder<VfGlyph>(glyphs.Count);
        for (int i = 0; i < glyphs.Count; i++)
        {
            var gd = glyphs[i]!; // none is null (checked above)
            if (gd.Code != f.FirstCharacter + i)
                throw new VfSheetException($"Glyph {i + 1} of the sidecar is character {gd.Code}; the glyphs must run from {f.FirstCharacter} without gaps.");
            if (gd.Width < 0 || gd.Width > l.CellWidth)
                throw new VfSheetException($"Character {gd.Code}'s width {gd.Width} does not fit its {l.CellWidth}-pixel cell.");
            int old = gd.Code - current.FirstCharacter;
            if (old < 0 || old >= current.GlyphCount) old = -1;
            var oldGlyph = sameBasis && old >= 0 ? current.Glyphs[old] : null;
            var (cx, cy) = CellOrigin(i, l.Columns, l.CellWidth, l.CellHeight);
            var pixels = new byte[gd.Width * f.Height * bpp];
            for (int y = 0; y < f.Height; y++)
                for (int x = 0; x < gd.Width; x++)
                {
                    int o = ((cy + y) * image.Width + cx + x) * 4;
                    var px = image.Pixels;
                    uint argb = (uint)px[o + 3] << 24 | (uint)px[o + 2] << 16 | (uint)px[o + 1] << 8 | px[o];
                    int raw;
                    if (oldGlyph is not null && x < oldGlyph.Width && VfPixelConvert.SheetColour(current, VfRender.RawPixel(current, oldGlyph, x, y), mono) == argb)
                        raw = VfRender.RawPixel(current, oldGlyph, x, y);
                    else
                        raw = VfPixelConvert.RawFromColour(target, argb, coverage);
                    int p = (y * gd.Width + x) * bpp;
                    pixels[p] = (byte)raw;
                    if (bpp == 2) pixels[p + 1] = (byte)(raw >> 8);
                }
            var keep = old >= 0 ? current.Glyphs[old] : new VfGlyph(0, 0, 0, -1, 0, []);
            built.Add(keep with { Width = gd.Width, Spacing = gd.Spacing, UserData = gd.UserData, Pixels = ImmutableArray.Create(pixels) });
        }
        target = target with { Glyphs = built.MoveToImmutable() };

        var pairs = new List<VfKernPair>();
        foreach (var k in doc.Kerning ?? [])
        {
            if (k is null) throw new VfSheetException("The sidecar's \"kerning\" list has an empty (null) entry; each pair needs its left and right character and offset.");
            int left = k.Left - f.FirstCharacter, right = k.Right - f.FirstCharacter;
            if (left < 0 || left >= glyphs.Count || right < 0 || right >= glyphs.Count || left > 255 || right > 255)
                throw new VfSheetException($"The kerning pair {k.Left} + {k.Right} names a character the sheet does not have.");
            if (k.Offset is < sbyte.MinValue or > sbyte.MaxValue)
                throw new VfSheetException($"The kerning pair {k.Left} + {k.Right} has offset {k.Offset}; offsets are -128 to 127.");
            pairs.Add(new VfKernPair((byte)left, (byte)right, (sbyte)k.Offset));
        }
        var result = pairs.SequenceEqual(current.Kerning) && f.FirstCharacter == current.FirstCharacter
            ? VfEdits.Normalize(target with { Kerning = current.Kerning })
            : VfEdits.WithKerning(target, pairs);
        return SameBytes(result, current) ? current : result;
    }

    private static bool SameBytes(VfFont a, VfFont b)
    {
        try { return VfWriter.Write(a).AsSpan().SequenceEqual(VfWriter.Write(b)); }
        catch (InvalidOperationException) { return false; } // the current font cannot be written as it is
    }

    private static void Put(BgraImage image, int x, int y, uint argb) =>
        image.Set(x, y, (byte)argb, (byte)(argb >> 8), (byte)(argb >> 16), (byte)(argb >> 24));

    /// <summary>"mono", "rgba4444" or "indexed".</summary>
    public static string FormatId(VfPixelFormat format) => format switch
    {
        VfPixelFormat.Rgba4444 => "rgba4444",
        VfPixelFormat.Indexed => "indexed",
        _ => "mono",
    };

    private static VfPixelFormat ParseFormat(string? id) => id?.ToLowerInvariant() switch
    {
        "mono" => VfPixelFormat.Mono,
        "rgba4444" => VfPixelFormat.Rgba4444,
        "indexed" => VfPixelFormat.Indexed,
        _ => throw new VfSheetException($"The format \"{id}\" is not mono, rgba4444 or indexed."),
    };

    private sealed class SheetDoc
    {
        public string? Kind { get; set; }
        public int Version { get; set; }
        public string? Image { get; set; }
        public FontDoc? Font { get; set; }
        public LayoutDoc? Layout { get; set; }
        // Entries may be null in a hand-edited file: Import checks each one.
        public List<GlyphDoc?>? Glyphs { get; set; }
        public List<KernDoc?>? Kerning { get; set; }
        public List<string?>? Palette { get; set; }
    }

    private sealed class FontDoc
    {
        public int Version { get; set; }
        public string? Format { get; set; }
        public int Height { get; set; }
        public int FirstCharacter { get; set; }
        public int DefaultSpacing { get; set; }
    }

    private sealed class LayoutDoc
    {
        public int Columns { get; set; }
        public int CellWidth { get; set; }
        public int CellHeight { get; set; }
        public int Gap { get; set; }
        public string? MonoStyle { get; set; }
        public string? Guide { get; set; }
    }

    private sealed class GlyphDoc
    {
        public int Code { get; set; }
        public string? Char { get; set; }
        public int Width { get; set; }
        public int Spacing { get; set; }
        public ushort UserData { get; set; }
    }

    private sealed class KernDoc
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public int Offset { get; set; }
    }
}
