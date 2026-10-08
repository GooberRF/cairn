using System.Text.Json.Nodes;
using Cairn.Formats;
using Cairn.Vf.Formats;
using Cairn.Vf.Model;
using Cairn.Vf.Rendering;
using Cairn.Vf.Sheets;
using Cairn.Vf.Validation;

namespace Cairn.Vf.Tests;

/// <summary>Damaged and hand-edited input: the reader, renderer, edits and sheet import report it instead of failing.</summary>
public sealed class VfDamageTests
{
    private sealed record Entry(int Spacing, int Width, uint Offset = 0, short Kern = -1);

    /// <summary>A version 1 monochrome font file with the given header values and glyph table.</summary>
    private static byte[] File(int height, IReadOnlyList<Entry> entries, byte[] pixels, int defaultSpacing = 5)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(VfFont.Signature); w.Write(1); w.Write((uint)VfPixelFormat.Mono);
        w.Write(entries.Count); w.Write(32); w.Write(defaultSpacing); w.Write(height); w.Write(0); w.Write(pixels.Length);
        foreach (var e in entries) { w.Write(e.Spacing); w.Write(e.Width); w.Write(e.Offset); w.Write(e.Kern); w.Write((ushort)0); }
        w.Write(pixels);
        return ms.ToArray();
    }

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Reader_SizesBeyondTheFileAreDamageNotAllocations()
    {
        // review: 4 glyph entries of 20,000 × 20,000 in a 100-byte file made the reader allocate 3.2 GB
        byte[] amplify = File(20_000, [.. Enumerable.Repeat(new Entry(1, 20_000), 4)], []);
        AssetFormatException? error = null;
        long bytes = Allocated(() => error = Assert.Throws<AssetFormatException>(() => VfReader.Read(amplify, "amp.vf")));
        Assert.Contains("damaged", error!.Message);
        Assert.True(bytes < 1_000_000, $"{bytes:N0} bytes allocated");

        // within the per-glyph limits, but far more pixels than the file holds
        byte[] wide = File(1000, [.. Enumerable.Repeat(new Entry(1, 4000), 50)], new byte[16]);
        bytes = Allocated(() => error = Assert.Throws<AssetFormatException>(() => VfReader.Read(wide, "wide.vf")));
        Assert.Contains("pixel data", error!.Message);
        Assert.True(bytes < 16_000_000, $"{bytes:N0} bytes allocated");
        Assert.Contains("pixels wide", Assert.Throws<AssetFormatException>(() => VfReader.Read(File(8, [new Entry(1, 9000)], []), "x.vf")).Message);

        // a font cut short a little still opens, the missing pixels clear (VF010)
        var problems = new List<VfProblem>();
        var cut = VfReader.Read(File(8, [new Entry(4, 3), new Entry(4, 3, 24)], new byte[30]), "cut.vf", problems);
        Assert.Equal(24, cut.Glyphs[1].Pixels.Length);
        Assert.Contains(problems, p => p.Code == "VF010" && p.Glyph == 1);
    }

    [Fact]
    public void Reader_HugeHeightIsDamage()
    {
        // review: a 36-byte header with height 1,000,000,000 read fine, then rendering overflowed
        var (font, problems) = VfReader.Inspect(File(1_000_000_000, [], []), "tall.vf");
        Assert.Null(font);
        Assert.Contains("height", Assert.Single(problems).Message);
    }

    [Fact]
    public void Render_RefusesImagesOverTheBudgetBeforeAllocating()
    {
        var font = VfSamples.Create(VfPixelFormat.Mono);
        int a = font.IndexOf('A');
        var far = font with { Glyphs = font.Glyphs.SetItem(a, font.Glyphs[a] with { Spacing = 200_000_000 }) };
        var layout = VfLayout.Layout(far, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8);
        Assert.True(layout.Right > 0, "the layout does not wrap around int"); // 43 × 200,000,000 overflows int
        VfImageTooLargeException? error = null;
        long bytes = Allocated(() => error = Assert.Throws<VfImageTooLargeException>(() => VfRender.Text(far, layout)));
        Assert.Contains("too large to draw", error!.Message);
        Assert.True(bytes < 1_000_000, $"{bytes:N0} bytes allocated");
        Assert.Contains(VfValidator.Validate(far), p => p.Code == "VF055" && p.Glyph == a);

        // a font built in memory with an absurd height: refused, not an OverflowException
        var tall = font with { Height = 1_000_000_000 };
        Assert.Throws<VfImageTooLargeException>(() => VfRender.Text(tall, VfLayout.Layout(tall, "A"u8)));
        Assert.Throws<VfImageTooLargeException>(() => VfRender.Glyph(tall, a));
        Assert.True(VfRender.CanDraw(4096, 1024) && !VfRender.CanDraw(1_000_000, 1_000_000));
    }

    [Fact]
    public void NegativeWidth_ReadAsDamageAndEveryEditWorks()
    {
        // review: width -3 was kept; Change Height then threw OverflowException
        var problems = new List<VfProblem>();
        var font = VfReader.Read(File(8, [new Entry(4, 3), new Entry(4, -3)], new byte[24]), "neg.vf", problems);
        Assert.Equal(0, font.Glyphs[1].Width);
        Assert.Contains(problems, p => p.Code == "VF050" && p.Glyph == 1 && p.Severity == VfSeverity.Error);
        Assert.Equal(0, VfReader.Read(VfWriter.Write(font), "x.vf").Glyphs[1].Width); // saved as 0
        Assert.Equal(9, VfEdits.WithHeight(font, 9).Height);
        Assert.Equal(7, VfEdits.WithHeight(font, 7, keepTop: false).Height);
        Assert.Equal(2, VfEdits.WithWidth(font, 1, 2).Glyphs[1].Width);
        Assert.Equal(VfPixelFormat.Rgba4444, VfEdits.WithFormat(font, VfPixelFormat.Rgba4444).Format);

        // a font built in memory with a negative width: the edits treat it as 0 instead of throwing
        var made = font with { Glyphs = font.Glyphs.SetItem(1, font.Glyphs[1] with { Width = -3 }) };
        Assert.Equal(0, VfEdits.WithHeight(made, 9).Glyphs[1].Width);
        Assert.Equal(0, VfEdits.WithHeight(made, 7, keepTop: false).Glyphs[1].Width);
        Assert.Equal(2 * 8, VfEdits.WithWidth(made, 1, 2).Glyphs[1].Pixels.Length);
        Assert.Equal(0, VfEdits.WithFormat(made, VfPixelFormat.Indexed).Glyphs[1].Width);
        // a glyph whose pixels are shorter than its size: widening keeps what is there
        var shortPixels = font with { Glyphs = font.Glyphs.SetItem(0, font.Glyphs[0] with { Pixels = [1, 2, 3] }) };
        Assert.Equal(4 * 8, VfEdits.WithWidth(shortPixels, 0, 4).Glyphs[0].Pixels.Length);
    }

    public static TheoryData<string> BadSidecars => new()
    {
        "columns", "cellWidth", "glyph-null", "kerning-null", "palette-null", "glyph-count",
    };

    [Theory]
    [MemberData(nameof(BadSidecars))]
    public void SheetImport_MalformedSidecarIsAReportedError(string damage)
    {
        var font = VfSamples.Create(damage == "palette-null" ? VfPixelFormat.Indexed : VfPixelFormat.Mono);
        var (image, json) = VfSheet.Export(font, "s.png");
        var node = JsonNode.Parse(json)!;
        switch (damage)
        {
            case "columns": node["layout"]!["columns"] = 4_194_304; node["layout"]!["cellWidth"] = 1000; break; // overflowed the size check
            case "cellWidth": node["layout"]!["cellWidth"] = int.MaxValue; break;
            case "glyph-null": node["glyphs"]![0] = null; break;
            case "kerning-null": node["kerning"] = new JsonArray((JsonNode?)null); break;
            case "palette-null": node["palette"]![3] = null; break;
            case "glyph-count": var many = new JsonArray(); for (int i = 0; i < 5000; i++) many.Add(new JsonObject { ["code"] = 32 + i }); node["glyphs"] = many; break;
        }
        var error = Assert.Throws<VfSheetException>(() => VfSheet.Import(font, image, node.ToJsonString()));
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void Validator_ReportsKerningTheGameAppliesWithoutAPair()
    {
        // review: pairs A+T and B+V; the game's scan for "AV" runs past A's pairs and applies B+V's -3
        var font = VfReader.Read(File(8, [.. Enumerable.Repeat(new Entry(6, 0), 59)], []), "k.vf");
        font = VfEdits.WithKernPair(font, 'A' - 32, 'T' - 32, -2);
        font = VfEdits.WithKernPair(font, 'B' - 32, 'V' - 32, -3);
        Assert.Equal(3, VfLayout.Advance(font, (byte)'A', (byte)'V', out _, out _)); // the game's (and Cairn's) behaviour stays
        var phantom = Assert.Single(VfLayout.PhantomPairs(font));
        Assert.Equal(('A' - 32, 'V' - 32), (phantom.Left, phantom.Right));
        var problem = Assert.Single(VfValidator.Validate(font), p => p.Code == "VF046");
        Assert.Equal(VfSeverity.Warning, problem.Severity);
        Assert.Equal('A' - 32, problem.Glyph);
        Assert.Contains("'A' (65) followed by 'V' (86)", problem.Message);
        Assert.Contains("'B' (66) + 'V' (86)", problem.Message);
        Assert.Contains("'A' (65) + 'T' (84)", problem.Message);
        Assert.Contains("3 px closer", problem.Message);
        // giving A+V a pair of its own ends it
        Assert.Empty(VfLayout.PhantomPairs(VfEdits.WithKernPair(font, 'A' - 32, 'V' - 32, -1)));
        // the samples have none
        foreach (var f in new[] { VfPixelFormat.Mono, VfPixelFormat.Indexed, VfPixelFormat.Rgba4444 })
            Assert.Empty(VfLayout.PhantomPairs(VfSamples.Create(f)));
    }

    [Fact]
    public void Atlas_OneRowTallerThanTheTextureLoadsButOverruns()
    {
        // The game checks "Font too big!" only when it starts a new row.
        var font = VfReader.Read(File(300, [new Entry(11, 10)], new byte[3000]), "tall.vf");
        var plan = VfAtlas.Plan(font);
        Assert.True(plan.Fits);
        Assert.True(VfAtlas.Overruns(font, plan));
        var codes = VfValidator.Validate(font).Select(p => p.Code).ToList();
        Assert.Contains("VF061", codes);
        Assert.DoesNotContain("VF060", codes);
        Assert.False(VfAtlas.Overruns(VfSamples.Create(VfPixelFormat.Mono), VfAtlas.Plan(VfSamples.Create(VfPixelFormat.Mono))));
    }
}
