using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

public class AssetLinterTests
{
    private static byte[] Tga24(int size) =>
        TinyImageWriter.Tga24(size, size, new byte[size * size * 3]);

    private static byte[] Tga32(int size) =>
        TinyImageWriter.Tga32(size, size, new byte[size * size * 4]);

    private static byte[] Grey(int size) => TinyImageWriter.Tga8Grey(size, size, new byte[size * size]);

    private static byte[] Dxt1(int size, int mips = 1) =>
        TinyImageWriter.DdsDxt1(size, size, (_, _) => (0xF800, 0x001F, 0u), mips);

    private static AssetAnalysis Analyze(TempFolder temp, string text)
    {
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        return AtxAssetLinter.Analyze(AtxParser.Parse(text), resolver);
    }

    private static string[] Codes(AssetAnalysis analysis) =>
        [.. analysis.Diagnostics.Select(d => d.Code).Distinct()];

    private const string TwoFrames = "[[frame]]\nfile = \"a.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n";

    [Fact]
    public void MatchingFramesProduceNoAssetProblems()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", Tga24(16));
        var analysis = Analyze(temp, TwoFrames);
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(EngineFormat.Rgb888, analysis.EffectiveFormat);
        Assert.All(analysis.Frames, f => Assert.NotNull(f.Info));
    }

    [Fact]
    public void Atx010MissingFrameImage()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        var analysis = Analyze(temp, TwoFrames);

        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.FrameImageNotFound);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.FrameIndex);
        Assert.Contains("b.tga", diagnostic.Message, StringComparison.Ordinal);
        // Locate the file, or go and add the folder it lives in — the help text names both, so
        // both are one click away.
        var locate = Assert.Single(diagnostic.QuickFixes, f => f.Kind == QuickFixKind.LocateFile);
        Assert.Equal("b.tga", locate.Payload);
        Assert.Contains(diagnostic.QuickFixes, f => f.Kind == QuickFixKind.OpenSearchSettings);
    }

    [Fact]
    public void Atx016UnreadableFrameImage()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", [1, 2, 3, 4]);
        var analysis = Analyze(temp, TwoFrames);

        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.ImageUnreadable);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.FrameIndex);
    }

    [Fact]
    public void Atx011SizeMismatchIsAnError()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", Tga24(8));
        var diagnostic = Assert.Single(Analyze(temp, TwoFrames).Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("8 x 8", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("16 x 16", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx011FormatMismatchIsAnError()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", Tga32(16));
        var diagnostic = Assert.Single(Analyze(temp, TwoFrames).Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("888 RGB", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx011MipMismatchBetweenTheSameContainerIsAnError()
    {
        using var temp = new TempFolder();
        temp.Write("a.dds", Dxt1(16, mips: 3));
        temp.Write("b.dds", Dxt1(16, mips: 1));
        var analysis = Analyze(temp, "[[frame]]\nfile = \"a.dds\"\n\n[[frame]]\nfile = \"b.dds\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("mip level", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx011MipCountAcrossContainerTypesIsOnlyAWarning()
    {
        using var temp = new TempFolder();
        // Same size and engine format, but one file states its mip count and the other cannot.
        temp.Write("a.dds", TinyImageWriter.DdsBgra32(4, 4, new byte[4 * 4 * 4]));
        temp.Write("b.tga", Tga32(4));
        var analysis = Analyze(temp, "[[frame]]\nfile = \"a.dds\"\n\n[[frame]]\nfile = \"b.tga\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("could not be verified", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TgaFramesOfEqualSizeNeverDisagreeAboutMips()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", Tga24(16));
        Assert.DoesNotContain(AtxRules.FrameMismatch, Codes(Analyze(temp, TwoFrames)));
    }

    [Fact]
    public void Atx012MissingAlphaMask()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        var analysis = Analyze(temp, "[header]\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.MaskNotFound);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains(diagnostic.QuickFixes, f => f.Kind == QuickFixKind.LocateFile);
    }

    [Fact]
    public void Atx013MaskSizeMismatch()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("m.tga", Grey(8));
        var analysis = Analyze(temp, "[header]\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.MaskMismatch);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("8 x 8", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx014MaskMustBeEightBit()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("m.tga", Tga24(16));
        var analysis = Analyze(temp, "[header]\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.MaskNotGreyscale);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void AGoodMaskIsAcceptedAndPromotesTheFormat()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("m.tga", Grey(16));
        var analysis = Analyze(temp, "[header]\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Equal(EngineFormat.Argb8888, analysis.EffectiveFormat);
        Assert.NotNull(analysis.Mask);
        Assert.Equal(EngineFormat.Paletted8, analysis.Mask!.Info!.Format);

        // With no `format` key the promotion is driven by the frames' own format, which only the
        // asset pass can see, so the note has to come from here.
        var note = Assert.Single(analysis.Diagnostics);
        Assert.Equal(AtxRules.MaskPromotesFormat, note.Code);
        Assert.Equal(DiagnosticSeverity.Info, note.Severity);
        Assert.Contains("8888 ARGB", note.Message, StringComparison.Ordinal);

        // Saying it explicitly makes the note go away.
        var stated = Analyze(temp,
            "[header]\nformat = \"8888\"\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.tga\"\n");
        Assert.Empty(stated.Diagnostics);
        Assert.Equal(EngineFormat.Argb8888, stated.EffectiveFormat);
    }

    [Fact]
    public void Atx015FormatOnCompressedFramesIsAnError()
    {
        using var temp = new TempFolder();
        temp.Write("a.dds", Dxt1(16));
        var text = "[header]\nformat = \"8888\"\n\n[[frame]]\nfile = \"a.dds\"\n";
        var analysis = Analyze(temp, text);

        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.CompressedTransform);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("format", diagnostic.Key);

        var fix = Assert.Single(diagnostic.QuickFixes);
        Assert.Equal(QuickFixKind.Edit, fix.Kind);
        string fixedText = fix.Apply().Apply(text);
        Assert.DoesNotContain("format", fixedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Atx015AlphaMaskOnCompressedFramesIsAnError()
    {
        using var temp = new TempFolder();
        temp.Write("a.dds", Dxt1(16));
        temp.Write("m.tga", Grey(16));
        var analysis = Analyze(temp, "[header]\nalpha_mask = \"m.tga\"\n\n[[frame]]\nfile = \"a.dds\"\n");
        var diagnostic = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.CompressedTransform);
        Assert.Equal("alpha_mask", diagnostic.Key);
    }

    [Fact]
    public void AFormatEqualToTheSourceNeedsNoTransform()
    {
        using var temp = new TempFolder();
        temp.Write("a.dds", Dxt1(16));
        var analysis = Analyze(temp, "[[frame]]\nfile = \"a.dds\"\n");
        Assert.Empty(analysis.Diagnostics);
        Assert.Equal(EngineFormat.Dxt1, analysis.EffectiveFormat);
    }

    [Fact]
    public void SupersedingSiblingsAreReportedInTheResolvedAsset()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("a.dds", TinyImageWriter.DdsBgra32(16, 16, new byte[16 * 16 * 4]));
        var analysis = Analyze(temp, "[[frame]]\nfile = \"a.tga\"\n");
        var frame = Assert.Single(analysis.Frames);
        Assert.Equal("a.dds", frame.Location!.ResolvedName);
        Assert.True(frame.Location.IsSupersede);
        Assert.Equal(EngineFormat.Argb8888, frame.Info!.Format);
    }

    [Fact]
    public void StructuralProblemsAreNotRepeatedByTheAssetPass()
    {
        using var temp = new TempFolder();
        var analysis = Analyze(temp, "[[frame]]\nfile = \"nested.atx\"\n");
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public async Task AnalyzeAsyncRespectsCancellation()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => AtxAssetLinter.AnalyzeAsync(AtxParser.Parse(TwoFrames), resolver, null, cts.Token));
    }

    [Fact]
    public async Task AnalyzeAsyncReturnsTheSameResultAsTheSyncPass()
    {
        using var temp = new TempFolder();
        temp.Write("a.tga", Tga24(16));
        temp.Write("b.tga", Tga24(8));
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        var analysis = await AtxAssetLinter.AnalyzeAsync(AtxParser.Parse(TwoFrames), resolver);
        Assert.Contains(AtxRules.FrameMismatch, Codes(analysis));
    }

    [Fact]
    public void WhenFrameZeroCannotBeReadTheMessageNamesTheFrameItCompared()
    {
        // The game always measures against frame 0 and refuses the file if frame 0 will not load.
        // We can still compare the rest to each other, but calling frame 1 "frame 0" would be a
        // plain untruth about the designer's own file.
        using var temp = new TempFolder();
        temp.Write("b.tga", Tga24(16));
        temp.Write("c.tga", Tga24(8));
        var analysis = Analyze(temp,
            "[[frame]]\nfile = \"missing.tga\"\n\n[[frame]]\nfile = \"b.tga\"\n\n"
            + "[[frame]]\nfile = \"c.tga\"\n");

        var mismatch = Assert.Single(analysis.Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Contains("frame 1 ('b.tga'), the first frame that could be read",
            mismatch.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("frame 0 ('b.tga')", mismatch.Message, StringComparison.Ordinal);

        // With frame 0 readable it is named plainly, as before.
        using var ok = new TempFolder();
        ok.Write("a.tga", Tga24(16));
        ok.Write("b.tga", Tga24(8));
        var plain = Assert.Single(Analyze(ok, TwoFrames).Diagnostics, d => d.Code == AtxRules.FrameMismatch);
        Assert.Contains("frame 0 ('a.tga')", plain.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableDocumentProducesNothing()
    {
        using var temp = new TempFolder();
        var analysis = Analyze(temp, "[header\n");
        Assert.Empty(analysis.Diagnostics);
        Assert.Empty(analysis.Frames);
    }
}
