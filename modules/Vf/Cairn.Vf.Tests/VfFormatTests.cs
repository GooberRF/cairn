using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Formats.Vpp;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Validation;
using Cairn.Workspace;

namespace Cairn.Vf.Tests;

public sealed class VfFormatTests
{
    public static TheoryData<VfPixelFormat, int> Formats => new()
    {
        { VfPixelFormat.Mono, 0 }, { VfPixelFormat.Mono, 1 }, { VfPixelFormat.Rgba4444, 1 }, { VfPixelFormat.Indexed, 1 },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void SyntheticFonts_RoundTripByteIdentical(VfPixelFormat format, int version)
    {
        var font = VfSamples.Create(format, version);
        byte[] bytes = VfWriter.Write(font);
        var read = VfReader.Read(bytes, "sample.vf");
        Assert.Equal(version, read.Version);
        Assert.Equal(format, read.Format);
        Assert.Equal(95, read.GlyphCount);
        Assert.Equal(3, read.Kerning.Length);
        Assert.Equal(bytes, VfWriter.Write(read));
        for (int i = 0; i < font.GlyphCount; i++) Assert.Equal(font.Glyphs[i].Pixels.AsSpan().ToArray(), read.Glyphs[i].Pixels.AsSpan().ToArray());
        Assert.Equal(version == 0 ? 40 : 36, version == 0 ? VfReader.HeaderSizeV0 : VfReader.HeaderSizeV1);
        Assert.DoesNotContain(VfValidator.Validate(read), p => p.Severity != VfSeverity.Information);
    }

    [Fact]
    public void SyntheticLayout_KerningIndicesPointAtFirstPair()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        int a = font.IndexOf('A'), v = font.IndexOf('V'), y = font.IndexOf('Y');
        Assert.Equal(0, font.Glyphs[a].FirstKernIndex);
        Assert.Equal(1, font.Glyphs[v].FirstKernIndex);
        Assert.Equal(2, font.Glyphs[y].FirstKernIndex);
        Assert.Equal(-1, font.Glyphs[font.IndexOf('B')].FirstKernIndex);
    }

    [Fact]
    public void Layout_FollowsSpacingKerningAndDefaultSpacing()
    {
        var font = VfSamples.Create(VfPixelFormat.Indexed);
        int Sp(char c) => font.Glyphs[font.IndexOf(c)].Spacing;
        Assert.Equal((Sp('A') - 1 + Sp('V'), 8), VfLayout.Measure(font, "AV"u8));
        Assert.Equal((Sp('V') - 1 + Sp('A'), 8), VfLayout.Measure(font, "VA"u8));
        Assert.Equal((Sp('A') + Sp('A'), 8), VfLayout.Measure(font, "AA"u8));
        Assert.Equal((Sp('Y') - 2 + Sp('.'), 8), VfLayout.Measure(font, "Y."u8));
        // a character the font lacks (127 is past '~') advances by the default spacing
        Assert.Equal((Sp('A') + font.DefaultSpacing, 8), VfLayout.Measure(font, [(byte)'A', 200]));
        // lines: widest wins; a trailing line feed adds no line
        Assert.Equal((Sp('A') * 3, 16), VfLayout.Measure(font, "A\nAAA"u8));
        Assert.Equal((Sp('A'), 8), VfLayout.Measure(font, "A\n"u8));
        var layout = VfLayout.Layout(font, "AV\nT"u8);
        Assert.Equal([new VfPlacedGlyph(font.IndexOf('A'), 0, 0), new VfPlacedGlyph(font.IndexOf('V'), Sp('A') - 1, 0), new VfPlacedGlyph(font.IndexOf('T'), 0, 8)], layout.Glyphs.ToArray());
        Assert.Equal(16, layout.Height);
        var bitmap = VfRender.Text(font, layout);
        Assert.Equal(layout.Right, bitmap.Width);
        Assert.Equal(16, bitmap.Height);
    }

    [Fact]
    public void Layout_KerningScanMirrorsTheGame()
    {
        // Pairs of one left glyph must be sorted by right glyph: the game stops at the first right glyph >= the wanted one.
        var font = VfSamples.Create(VfPixelFormat.Mono);
        byte a = (byte)font.IndexOf('A'), b = (byte)font.IndexOf('B'), c = (byte)font.IndexOf('C');
        var unsorted = font with { Kerning = [new(a, c, -3), new(a, b, -2)], Glyphs = font.Glyphs.SetItem(a, font.Glyphs[a] with { FirstKernIndex = 0 }) };
        int sp = font.Glyphs[a].Spacing;
        Assert.Equal(sp - 3, VfLayout.Advance(unsorted, (byte)'A', (byte)'C', out _, out _));
        Assert.Equal(sp, VfLayout.Advance(unsorted, (byte)'A', (byte)'B', out _, out _)); // never found
        Assert.Contains(VfValidator.Validate(unsorted), p => p.Code == "VF041");
        var fixedFont = VfEdits.WithKerning(unsorted, unsorted.Kerning);
        Assert.Equal(sp - 2, VfLayout.Advance(fixedFont, (byte)'A', (byte)'B', out _, out _));
        Assert.DoesNotContain(VfValidator.Validate(fixedFont), p => p.Code is "VF041" or "VF042");
    }

    [Fact]
    public void Render_MatchesTheGameTexture()
    {
        var mono = VfSamples.Create(VfPixelFormat.Mono);
        Assert.Equal(0xFFFF, VfRender.ToArgb4444(mono, 14));
        Assert.Equal(0xFFFF, VfRender.ToArgb4444(mono, 200));
        Assert.Equal(0x0FFF, VfRender.ToArgb4444(mono, 0));
        Assert.Equal(((7 * 255 / 14) >> 4 << 12) | 0xFFF, VfRender.ToArgb4444(mono, 7));
        var indexed = VfSamples.Create(VfPixelFormat.Indexed) with { Palette = [.. Enumerable.Repeat(0x8F3A1C05u, 256)] };
        Assert.Equal(0xFAC5, VfRender.ToArgb4444(indexed, 3)); // low nibble of each byte
        Assert.Equal(((byte)0x55, (byte)0xCC, (byte)0xAA, (byte)0xFF), VfRender.Expand(0xFAC5));
        var rgba = VfSamples.Create(VfPixelFormat.Rgba4444);
        Assert.Equal(0x1234, VfRender.ToArgb4444(rgba, 0x1234));
        var g = VfRender.Glyph(mono, mono.IndexOf('A'));
        Assert.Equal(mono.Glyphs[mono.IndexOf('A')].Width * 8 * 4, g.Bgra.Length);
        Assert.Equal(255, g.Bgra[3]); // column 0 is solid
    }

    [Fact]
    public void Reader_ReportsDamageClearly()
    {
        byte[] good = VfWriter.Write(VfSamples.Create(VfPixelFormat.Indexed));
        Assert.Contains("not a VFNT font", Assert.Throws<AssetFormatException>(() => VfReader.Read([1, 2, 3, 4, 5, 6, 7, 8], "x.vf")).Message);
        var v2 = (byte[])good.Clone();
        v2[4] = 2;
        Assert.Contains("version 2", Assert.Throws<AssetFormatException>(() => VfReader.Read(v2, "x.vf")).Message);
        var fmt = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(fmt.AsSpan(8), 0x12345678);
        Assert.Contains("0x12345678", Assert.Throws<AssetFormatException>(() => VfReader.Read(fmt, "x.vf")).Message);
        Assert.Contains("palette", Assert.Throws<AssetFormatException>(() => VfReader.Read(good[..^10], "x.vf")).Message);
        Assert.Contains("glyph table", Assert.Throws<AssetFormatException>(() => VfReader.Read(good[..60], "x.vf")).Message);
        var (font, problems) = VfReader.Inspect(good[..20], "x.vf");
        Assert.Null(font);
        Assert.Equal("VF000", Assert.Single(problems).Code);

        // trailing bytes are kept and reported
        byte[] longer = [.. good, 1, 2, 3];
        var list = new List<VfProblem>();
        var read = VfReader.Read(longer, "x.vf", list);
        Assert.Equal("VF020", Assert.Single(list).Code);
        Assert.Equal(longer, VfWriter.Write(read));

        // a glyph whose pixels lie past the pixel data
        var mono = VfSamples.Create(VfPixelFormat.Mono);
        int i = mono.IndexOf('Z');
        byte[] bad = VfWriter.Write(mono);
        BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(VfReader.HeaderSizeV1 + mono.Kerning.Length * 3 + i * 16 + 8), 1_000_000);
        list.Clear();
        var damaged = VfReader.Read(bad, "x.vf", list);
        Assert.Contains(list, p => p.Code == "VF010" && p.Glyph == i);
        // saving lays the pixels out afresh (the missing part stays clear), which repairs the offset
        list.Clear();
        VfReader.Read(VfWriter.Write(damaged), "x.vf", list);
        Assert.Empty(list);
    }

    [Fact]
    public void Validator_FindsFontLevelProblems()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        var codes = (VfFont f) => VfValidator.Validate(f).Select(p => p.Code).ToHashSet();
        Assert.Contains("VF031", codes(font with { FirstCharacter = 200 }));
        Assert.Contains("VF030", codes(font with { FirstCharacter = 33 }));
        Assert.Contains("VF001", codes(font with { Height = 0 }));
        Assert.Contains("VF004", codes(font with { DefaultSpacing = 0 }));
        var pairOut = font with { Kerning = [new(5, 250, -1)] };
        Assert.Contains("VF040", codes(pairOut));
        var big = VfEdits.WithRange(font, 32, 224);
        for (int i = 0; i < big.GlyphCount; i++) big = big with { Glyphs = big.Glyphs.SetItem(i, big.Glyphs[i] with { Width = 40, Pixels = [.. new byte[40 * 8]] }) };
        big = VfEdits.Normalize(big with { Height = 8 });
        var tall = VfEdits.Normalize(big with { Height = 40, Glyphs = [.. big.Glyphs.Select(g => g with { Pixels = [.. new byte[40 * 40]] })] });
        Assert.Contains("VF060", codes(tall));
        Assert.Equal(256, VfAtlas.Plan(tall).Size);
        Assert.False(VfAtlas.Plan(tall).Fits);
    }

    [Fact]
    public void Edits_KeepDerivedFieldsConsistent()
    {
        var font = VfSamples.Create(VfPixelFormat.Rgba4444);
        int a = font.IndexOf('A');
        var wider = VfEdits.WithWidth(font, a, 12);
        Assert.Equal(12 * 8 * 2, wider.Glyphs[a].Pixels.Length);
        Assert.Equal(font.Glyphs[a].Pixels.AsSpan(0, font.Glyphs[a].Width * 2).ToArray(), wider.Glyphs[a].Pixels.AsSpan(0, font.Glyphs[a].Width * 2).ToArray());
        Assert.True(VfWriter.StoredLayoutMatches(wider));
        Assert.Equal(wider, VfReader.Read(VfWriter.Write(wider), "x.vf") with { }, new FontComparer());
        var range = VfEdits.WithRange(font, 30, 100);
        Assert.Equal(30, range.FirstCharacter);
        Assert.Equal(0, range.Glyphs[0].Width);
        Assert.Equal(font.Glyphs[0].Spacing, range.Glyphs[2].Spacing);
        Assert.Equal(range.IndexOf('A'), range.Kerning[0].Left);
        Assert.Equal(0, range.Glyphs[range.IndexOf('A')].FirstKernIndex);
        Assert.DoesNotContain(VfValidator.Validate(range), p => p.Severity == VfSeverity.Error);
    }

    private sealed class FontComparer : IEqualityComparer<VfFont>
    {
        public bool Equals(VfFont? x, VfFont? y) => x is not null && y is not null && VfWriter.Write(x).AsSpan().SequenceEqual(VfWriter.Write(y));
        public int GetHashCode(VfFont obj) => 0;
    }

    // ── Game and PS2 demo data (pass trivially when absent) ──────────────────────────────────────────────────

    private static IEnumerable<(string Name, byte[] Bytes)> FontsIn(string? folder, string packfile)
    {
        if (folder is null) yield break;
        string path = Directory.EnumerateFiles(folder, "*.vpp").FirstOrDefault(p => Path.GetFileName(p).Equals(packfile, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (!File.Exists(path)) yield break;
        var archive = VppArchive.Open(path);
        foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".vf", StringComparison.OrdinalIgnoreCase)))
            yield return (entry.Name, archive.ReadEntry(entry));
    }

    [Fact]
    public void StockFonts_RoundTripAndValidate()
    {
        var fonts = FontsIn(LocalPaths.GameDirectory, "ui.vpp").ToList();
        if (fonts.Count == 0) return;
        Assert.Equal(5, fonts.Count);
        foreach (var (name, bytes) in fonts)
        {
            var problems = new List<VfProblem>();
            var font = VfReader.Read(bytes, name, problems);
            Assert.Equal(bytes, VfWriter.Write(font));
            Assert.Equal(bytes, VfWriter.Write(VfEdits.Normalize(font))); // stock layout is the canonical one
            problems.AddRange(VfValidator.Validate(font));
            Assert.True(problems.All(p => p.Severity == VfSeverity.Information), name + ": " + string.Join("; ", problems));
            Assert.True(VfAtlas.Plan(font).Fits, name);
        }
        var big = VfReader.Read(fonts.Single(f => f.Name.Equals("bigfont.vf", StringComparison.OrdinalIgnoreCase)).Bytes, "bigfont.vf");
        Assert.Equal((0, VfPixelFormat.Mono, 69, 49), (big.Version, big.Format, big.GlyphCount, big.Height));
        var medium = VfReader.Read(fonts.Single(f => f.Name.Equals("rfpc-medium.vf", StringComparison.OrdinalIgnoreCase)).Bytes, "rfpc-medium.vf");
        Assert.Equal((1, VfPixelFormat.Indexed, 221, 12), (medium.Version, medium.Format, medium.GlyphCount, medium.Height));
    }

    [Fact]
    public void Ps2DemoFonts_RoundTripAndValidate()
    {
        var fonts = FontsIn(LocalPaths.Ps2Directory, "RF_PS2.VPP").ToList();
        if (fonts.Count == 0) return;
        Assert.Equal(8, fonts.Count);
        Assert.Contains(fonts, f => VfReader.Read(f.Bytes, f.Name).Format == VfPixelFormat.Rgba4444);
        foreach (var (name, bytes) in fonts)
        {
            var font = VfReader.Read(bytes, name);
            Assert.Equal(bytes, VfWriter.Write(font));
            var bad = VfValidator.Validate(font).Where(p => p.Severity == VfSeverity.Error).ToList();
            Assert.True(bad.Count == 0, name + ": " + string.Join("; ", bad));
        }
        // font01.vf has 159 kerning pairs, every one applied by the game's scan
        var kerned = fonts.Select(f => VfReader.Read(f.Bytes, f.Name)).First(f => f.Kerning.Length == 159);
        Assert.DoesNotContain(VfValidator.Validate(kerned), p => p.Code is "VF041" or "VF042" or "VF044");
        var first = kerned.Kerning[0];
        int c1 = kerned.CharacterOf(first.Left), c2 = kerned.CharacterOf(first.Right);
        Assert.Equal(kerned.Glyphs[first.Left].Spacing + first.Offset, VfLayout.Advance(kerned, (byte)c1, (byte)c2, out _, out _));
    }
}
