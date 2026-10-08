using System.Text.Json.Nodes;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Sheets;
using Cairn.Vf.Validation;
using Cairn.Workspace;

namespace Cairn.Vf.Tests;

public sealed class VfEditTests
{
    public static TheoryData<VfPixelFormat, int> Formats => new()
    {
        { VfPixelFormat.Mono, 0 }, { VfPixelFormat.Mono, 1 }, { VfPixelFormat.Rgba4444, 1 }, { VfPixelFormat.Indexed, 1 },
    };

    /// <summary>Export → PNG bytes → decode → import, as a user's round trip through a file.</summary>
    private static VfFont RoundTrip(VfFont font, VfSheetOptions? options = null, Func<string, string>? editJson = null, Action<BgraImage>? editImage = null)
    {
        var (image, json) = VfSheet.Export(font, "sheet.png", options);
        editImage?.Invoke(image);
        var decoded = ImageDecoder.Decode(PngEncoder.Encode(image), "sheet.png");
        return VfSheet.Import(font, decoded, editJson is null ? json : editJson(json));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Sheet_RoundTripIsLossless(VfPixelFormat format, int version)
    {
        var font = VfSamples.Create(format, version);
        Assert.Same(font, RoundTrip(font));
        Assert.Same(font, RoundTrip(font, new VfSheetOptions(Columns: 10, ExtraWidth: 0, Guide: null, Mono: VfMonoStyle.Alpha)));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Sheet_ImportTakesEditedPixelsAndWidths(VfPixelFormat format, int version)
    {
        var font = VfSamples.Create(format, version);
        int a = font.IndexOf('A');
        var (image, json) = VfSheet.Export(font, "sheet.png");
        var node = JsonNode.Parse(json)!;
        int cellW = (int)node["layout"]!["cellWidth"]!;
        var glyph = node["glyphs"]![a]!;
        int oldWidth = (int)glyph["width"]!;
        glyph["width"] = oldWidth + 2;
        glyph["spacing"] = 20;
        node["kerning"] = new JsonArray();
        // the new columns: solid white
        var (cx, cy) = VfSheet.CellOrigin(a, 16, cellW, font.Height);
        for (int y = 0; y < font.Height; y++) for (int x = oldWidth; x < oldWidth + 2; x++) image.Set(cx + x, cy + y, 255, 255, 255, 255);
        var result = VfSheet.Import(font, ImageDecoder.Decode(PngEncoder.Encode(image), "s.png"), node.ToJsonString());
        var g = result.Glyphs[a];
        Assert.Equal((oldWidth + 2, 20), (g.Width, g.Spacing));
        Assert.Empty(result.Kerning);
        Assert.Equal(VfRender.RawPixel(font, font.Glyphs[a], 0, 0), VfRender.RawPixel(result, g, 0, 0)); // old columns kept
        var solid = VfRender.Expand(VfRender.ToArgb4444(result, VfRender.RawPixel(result, g, oldWidth, 3)));
        Assert.True(solid.A == 255 && solid.R == 255, $"new column drawn white ({solid})");
        Assert.DoesNotContain(VfValidator.Validate(result), p => p.Severity == VfSeverity.Error);
    }

    [Fact]
    public void Sheet_RejectsBrokenInputClearly()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var (image, json) = VfSheet.Export(font, "sheet.png");
        Assert.Contains("not valid JSON", Assert.Throws<VfSheetException>(() => VfSheet.Import(font, image, "{")).Message);
        Assert.Contains("kind", Assert.Throws<VfSheetException>(() => VfSheet.Import(font, image, "{\"kind\":\"x\"}")).Message);
        var small = new BgraImage(10, 10);
        Assert.Contains("Keep the sheet's size", Assert.Throws<VfSheetException>(() => VfSheet.Import(font, small, json)).Message);
        var node = JsonNode.Parse(json)!;
        node["glyphs"]![5]!["width"] = 999;
        Assert.Contains("does not fit", Assert.Throws<VfSheetException>(() => VfSheet.Import(font, image, node.ToJsonString())).Message);
        Assert.Equal("sheet.png", VfSheet.ImageNameOf(json));
    }

    [Fact]
    public void Sheet_GuideFillsOutsideGlyphs()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var (image, _) = VfSheet.Export(font, "s.png");
        Assert.Equal((byte)0xFF, image.Pixels[2]); // corner: guide magenta (R)
        Assert.Equal((byte)0x00, image.Pixels[1]);
        var (clear, _) = VfSheet.Export(font, "s.png", new VfSheetOptions(Guide: null));
        Assert.Equal((byte)0, clear.Pixels[3]);
    }

    [Fact]
    public void Convert_ColoursToEachFormat()
    {
        var mono = VfSamples.Create(VfPixelFormat.Mono);
        Assert.Equal(14, VfPixelConvert.RawFromColour(mono, 0xFFFFFFFF, VfCoverage.Luminance));
        Assert.Equal(0, VfPixelConvert.RawFromColour(mono, 0xFF000000, VfCoverage.Luminance));
        Assert.Equal(14, VfPixelConvert.RawFromColour(mono, 0xFF000000, VfCoverage.DarkOnLight));
        Assert.Equal(7, VfPixelConvert.RawFromColour(mono, 0x80FFFFFF, VfCoverage.Alpha));
        Assert.Equal(14, VfPixelConvert.RawFromColour(mono, 0x80FFFFFF, VfCoverage.Alpha, threshold: 100));
        Assert.Equal(0, VfPixelConvert.RawFromColour(mono, 0x40FFFFFF, VfCoverage.Alpha, threshold: 100));
        for (int v = 0; v <= 14; v++)
        {
            Assert.Equal(v, VfPixelConvert.RawFromColour(mono, VfPixelConvert.SheetColour(mono, v), VfCoverage.Luminance));
            Assert.Equal(v, VfPixelConvert.RawFromColour(mono, VfPixelConvert.SheetColour(mono, v, VfMonoStyle.Alpha), VfCoverage.Alpha));
        }
        var rgba = VfSamples.Create(VfPixelFormat.Rgba4444);
        Assert.Equal(0xF80C, VfPixelConvert.RawFromColour(rgba, 0xFF8800CC, VfCoverage.Auto));
        Assert.Equal(0x1234, VfPixelConvert.RawFromColour(rgba, VfPixelConvert.SheetColour(rgba, 0x1234), VfCoverage.Auto));
        var indexed = VfSamples.Create(VfPixelFormat.Indexed) with { Palette = [.. Enumerable.Range(0, 256).Select(i => i < 2 ? (i == 0 ? 0u : 0xFFFFFFFFu) : 0xFF0000FFu)] };
        Assert.Equal(1, VfPixelConvert.RawFromColour(indexed, 0xFFF0F0F0, VfCoverage.Auto));
        Assert.Equal(0, VfPixelConvert.RawFromColour(indexed, 0x00123456, VfCoverage.Auto));
        Assert.Equal(2, VfPixelConvert.RawFromColour(indexed, 0xFF1010E0, VfCoverage.Auto));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void Convert_ImageBecomesGlyph(VfPixelFormat format, int version)
    {
        var font = VfSamples.Create(format, version);
        // a 6 × 16 opaque image: white bar on black in column 1 → scaled to the 8-pixel font: 3 wide
        var image = new BgraImage(6, 16);
        for (int y = 0; y < 16; y++) for (int x = 0; x < 6; x++) { byte v = x is 2 or 3 ? (byte)255 : (byte)0; image.Set(x, y, v, v, v, 255); }
        var (width, pixels) = VfPixelConvert.GlyphFromImage(font, image, new VfImageOptions());
        Assert.Equal(3, width);
        Assert.Equal(font.PixelBytes(3), pixels.Length);
        var edited = VfEdits.WithGlyphPixels(font, 0, width, pixels);
        var lit = VfRender.Expand(VfRender.ToArgb4444(edited, VfRender.RawPixel(edited, edited.Glyphs[0], 1, 4)));
        Assert.True(lit.A > 128 && lit.R > 128, $"{format}: the bar is drawn ({lit})");
        var (kept, _) = VfPixelConvert.GlyphFromImage(font, image, new VfImageOptions(HeightFit: VfHeightFit.KeepTop, Width: 4));
        Assert.Equal(4, kept);
        Assert.Equal(VfCoverage.Luminance, VfPixelConvert.Resolve(image, VfCoverage.Auto));
    }

    [Fact]
    public void Edits_HeightFormatKerningRange()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        int a = font.IndexOf('A');
        var taller = VfEdits.WithHeight(font, 10);
        Assert.Equal(10 * font.Glyphs[a].Width, taller.Glyphs[a].Pixels.Length);
        Assert.Equal(VfRender.RawPixel(font, font.Glyphs[a], 0, 0), VfRender.RawPixel(taller, taller.Glyphs[a], 0, 0));
        var shorter = VfEdits.WithHeight(font, 6, keepTop: false);
        Assert.Equal(VfRender.RawPixel(font, font.Glyphs[a], 0, 7), VfRender.RawPixel(shorter, shorter.Glyphs[a], 0, 5));

        foreach (var target in new[] { VfPixelFormat.Indexed, VfPixelFormat.Rgba4444 })
        {
            var converted = VfEdits.WithFormat(font with { Version = 0 }, target);
            Assert.Equal(1, converted.Version);
            for (int i = 0; i < font.GlyphCount; i++)
                Assert.Equal(VfRender.Glyph(font, i).Bgra, VfRender.Glyph(converted, i).Bgra); // looks the same in the game
            var back = VfEdits.WithFormat(converted, VfPixelFormat.Mono);
            for (int i = 0; i < font.GlyphCount; i++) Assert.Equal(VfRender.Glyph(font, i).Bgra, VfRender.Glyph(back, i).Bgra);
            Assert.DoesNotContain(VfValidator.Validate(converted), p => p.Severity != VfSeverity.Information);
        }
        var rgba = VfSamples.Create(VfPixelFormat.Rgba4444);
        var indexed = VfEdits.WithFormat(rgba, VfPixelFormat.Indexed);
        Assert.Equal(VfFont.PaletteSize, indexed.Palette.Length);
        Assert.DoesNotContain(VfValidator.Validate(indexed), p => p.Severity == VfSeverity.Error);
        // a font of few colours converts exactly
        var few = VfEdits.WithFormat(VfEdits.WithFormat(font, VfPixelFormat.Rgba4444), VfPixelFormat.Indexed);
        for (int i = 0; i < font.GlyphCount; i++) Assert.Equal(VfRender.Glyph(font, i).Bgra, VfRender.Glyph(few, i).Bgra);

        byte b = (byte)font.IndexOf('B');
        var kerned = VfEdits.WithKernPair(font, a, b, -2);
        Assert.Equal(kerned.Kerning.OrderBy(k => k.Left).ThenBy(k => k.Right), kerned.Kerning);
        Assert.Equal(font.Glyphs[a].Spacing - 2, VfLayout.Advance(kerned, (byte)'A', (byte)'B', out _, out _));
        Assert.Equal(font.Kerning.ToArray(), VfEdits.WithKernPair(kerned, a, b, 0).Kerning.ToArray());
        var high = VfEdits.WithKernPair(VfEdits.WithRange(font, 32, 200), 150, a, -1);
        Assert.Contains(VfValidator.Validate(high), p => p.Code == "VF044");

        var wide = VfEdits.WithRange(font, 32, 100, 5);
        Assert.Equal((5, font.PixelBytes(5)), (wide.Glyphs[99].Width, wide.Glyphs[99].Pixels.Length));
        Assert.Equal(font.Glyphs[a], wide.Glyphs[a] with { PixelOffset = font.Glyphs[a].PixelOffset });
        Assert.Equal(7, VfEdits.WithUserData(font, a, 7).Glyphs[a].UserData);
    }

    // ── Game and PS2 demo data (pass trivially when absent) ──────────────────────────────────────────────────

    private static IEnumerable<(string Name, byte[] Bytes)> FontsIn(string? folder, string packfile)
    {
        if (folder is null || !Directory.Exists(folder)) yield break;
        string path = Directory.EnumerateFiles(folder, "*.vpp").FirstOrDefault(p => Path.GetFileName(p).Equals(packfile, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!File.Exists(path)) yield break;
        var archive = VppArchive.Open(path);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".vf", StringComparison.OrdinalIgnoreCase)))
            yield return (entry.Name, archive.ReadEntry(entry));
    }

    [Fact]
    public void StockFonts_SheetRoundTripIsByteIdentical()
    {
        foreach (var (name, bytes) in FontsIn(LocalPaths.GameDirectory, "ui.vpp").Concat(FontsIn(LocalPaths.Ps2Directory, "RF_PS2.VPP")))
        {
            var font = VfReader.Read(bytes, name);
            foreach (var options in new[] { new VfSheetOptions(), new VfSheetOptions(Guide: null, Mono: VfMonoStyle.Alpha) })
            {
                var back = RoundTrip(font, options);
                Assert.True(VfWriter.Write(back).AsSpan().SequenceEqual(bytes), $"{name} ({options.Mono}) comes back byte for byte");
            }
            // the pixels alone (old values not consulted): import into a font with the other basis must give the same pixels
            var (image, json) = VfSheet.Export(font, "s.png");
            var fresh = VfSheet.Import(font with { Height = font.Height + 1 }, image, json);
            Assert.True(VfWriter.Write(fresh).AsSpan().SequenceEqual(bytes) || font.Format == VfPixelFormat.Indexed || font.Glyphs.Any(g => g.Pixels.Any(p => p > 14)),
                $"{name}: pixels convert back exactly from colours alone");
        }
    }
}
