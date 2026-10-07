using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

/// <summary>
/// Import VBM as ATX: reading every frame of a VBM, writing them out as TGAs, and generating the
/// .atx that binds them together.
/// </summary>
public class VbmImportTests
{
    // ── Colour fidelity ───────────────────────────────────────────────────────
    //
    // The whole point of the export is that the result is the same texture. The game will convert
    // the 8888 TGAs back to 1555 / 4444 / 565 by truncation, so the expansion has to be the one
    // truncation undoes — bit replication — for every value, not merely for most of them.

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void EveryNarrowChannelValueSurvivesExpansionAndRequantisation(int bits)
    {
        for (int v = 0; v < (1 << bits); v++)
        {
            byte expanded = ChannelBits.Expand(v, bits);
            Assert.Equal(v, ChannelBits.Quantise(expanded, bits));
            // And the byte itself is a fixed point: reducing it again changes nothing.
            Assert.Equal(expanded, ChannelBits.Reduce(expanded, bits));
        }
    }

    [Fact]
    public void ExpansionFillsTheLowBitsRatherThanScaling()
    {
        // 5-bit 17 is 0b10001, which replicates to 0b10001_100 = 140. Scaling by 255/31 gives 139,
        // which truncates back to 17 but is a shade away from what the engine shows.
        Assert.Equal(140, ChannelBits.Expand(17, 5));
        Assert.Equal(255, ChannelBits.Expand(31, 5));
        Assert.Equal(0, ChannelBits.Expand(0, 5));
        Assert.Equal(69, ChannelBits.Expand(17, 6));
        Assert.Equal(102, ChannelBits.Expand(6, 4));
    }

    [Theory]
    [InlineData(0, EngineFormat.Argb1555)]
    [InlineData(1, EngineFormat.Argb4444)]
    [InlineData(2, EngineFormat.Rgb565)]
    public void ExportedFrameQuantisesBackToTheVbmPixelExactly(int vbmFormat, EngineFormat format)
    {
        // Every distinguishable pixel pattern this format has, spread over one image.
        var pixels = new ushort[256];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (ushort)(i * 257);
        var bytes = TinyImageWriter.Vbm(16, 16, vbmFormat, pixels, mipField: 0, levels: 1);

        var source = VbmCodec.DecodeFrame(bytes, 0, "a.vbm");
        byte[] tga = TgaWriter.Write(source, TgaWriter.NeedsAlpha(format));
        var roundTrip = TgaCodec.Decode(tga, "a.tga");
        var simulated = FormatSimulator.Quantise(roundTrip, format);

        Assert.Equal(source.Pixels, simulated.Pixels);
    }

    // ── TgaWriter ─────────────────────────────────────────────────────────────

    [Fact]
    public void TgaWriterRoundTripsThroughOurOwnDecoder()
    {
        var image = new BgraImage(3, 2);
        for (int y = 0; y < 2; y++)
        {
            for (int x = 0; x < 3; x++)
                image.Set(x, y, (byte)(x * 40), (byte)(y * 90), 200, (byte)(x * 60 + y));
        }

        var opaque = TgaWriter.Write(image, includeAlpha: false);
        var info24 = ImageProbe.Probe(opaque, "o.tga");
        Assert.Equal(EngineFormat.Rgb888, info24.Format);
        Assert.Equal(TgaWriter.FormatOf(false), info24.Format);
        Assert.Equal(3, info24.Width);
        Assert.Equal(2, info24.Height);
        Assert.Equal(TgaWriter.SizeOf(3, 2, false), opaque.Length);
        var back24 = ImageDecoder.Decode(opaque, "o.tga");
        Assert.Equal((80, 90, 200, 255), back24.Get(2, 1));

        var withAlpha = TgaWriter.Write(image, includeAlpha: true);
        var info32 = ImageProbe.Probe(withAlpha, "a.tga");
        Assert.Equal(EngineFormat.Argb8888, info32.Format);
        Assert.Equal(TgaWriter.FormatOf(true), info32.Format);
        var back32 = ImageDecoder.Decode(withAlpha, "a.tga");
        Assert.Equal(image.Pixels, back32.Pixels);
    }

    [Fact]
    public void TgaWriterWritesTypeTwoBottomLeftOrigin()
    {
        var image = new BgraImage(1, 1);
        image.Set(0, 0, 1, 2, 3, 4);
        var bytes = TgaWriter.Write(image, includeAlpha: true);
        Assert.Equal(0, bytes[1]);        // no colour map
        Assert.Equal(2, bytes[2]);        // uncompressed true-colour
        Assert.Equal(32, bytes[16]);      // bits per pixel
        Assert.Equal(8, bytes[17]);       // eight attribute bits, vertical-flip bit clear
        Assert.Equal(0, bytes[17] & 0x20);
    }

    [Fact]
    public void OnlyFormatsWithAlphaGetAThirtyTwoBitFrame()
    {
        Assert.True(TgaWriter.NeedsAlpha(EngineFormat.Argb1555));
        Assert.True(TgaWriter.NeedsAlpha(EngineFormat.Argb4444));
        Assert.False(TgaWriter.NeedsAlpha(EngineFormat.Rgb565));
    }

    // ── Reading every frame ───────────────────────────────────────────────────

    private static ushort[] Flat(int count, ushort value)
    {
        var pixels = new ushort[count];
        Array.Fill(pixels, value);
        return pixels;
    }

    private static byte[] ThreeDistinctFrames(int mipField = 2, int levels = 3, uint version = 1,
        int fps = 15, int format = 2) =>
        TinyImageWriter.VbmFrames(
            4, 4, format,
            [Flat(16, 0xF800), Flat(16, 0x07E0), Flat(16, 0x001F)],
            mipField, levels, version, fps);

    [Fact]
    public void ReadInfoReportsEveryHeaderField()
    {
        var info = VbmCodec.ReadInfo(ThreeDistinctFrames(), "a.vbm");
        Assert.Equal(1, info.Version);
        Assert.Equal(4, info.Width);
        Assert.Equal(4, info.Height);
        Assert.Equal(EngineFormat.Rgb565, info.Format);
        Assert.Equal(15, info.Fps);
        Assert.Equal(3, info.FrameCount);
        Assert.Equal(3, info.MipLevels);
        Assert.True(info.IsAnimated);
        // 4x4 + 2x2 + 1x1, two bytes a pixel.
        Assert.Equal(42, info.FrameStrideBytes);
        Assert.True(info.LengthMatchesHeader);
        Assert.Contains("3 frames", info.Describe(), StringComparison.Ordinal);
        Assert.Contains("15 fps", info.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void EachFrameDecodesToItsOwnPixelsWithTheMipsSkipped()
    {
        var bytes = ThreeDistinctFrames();
        Assert.Equal((0, 0, 255, 255), VbmCodec.DecodeFrame(bytes, 0, "a.vbm").Get(1, 1));
        Assert.Equal((0, 255, 0, 255), VbmCodec.DecodeFrame(bytes, 1, "a.vbm").Get(1, 1));
        Assert.Equal((255, 0, 0, 255), VbmCodec.DecodeFrame(bytes, 2, "a.vbm").Get(1, 1));
        // Decode() is still frame 0, which is what everything that shows one frame relies on.
        Assert.Equal(VbmCodec.DecodeFrame(bytes, 0, "a.vbm").Pixels, VbmCodec.Decode(bytes, "a.vbm").Pixels);
    }

    [Fact]
    public void FramesWithoutMipsAreWalkedWithTheShorterStride()
    {
        var bytes = ThreeDistinctFrames(mipField: 0, levels: 1);
        Assert.Equal(32, VbmCodec.ReadInfo(bytes, "a.vbm").FrameStrideBytes);
        Assert.Equal((255, 0, 0, 255), VbmCodec.DecodeFrame(bytes, 2, "a.vbm").Get(0, 0));
    }

    [Theory]
    [InlineData(1u, 0)]     // version 1: the top bit means transparent
    [InlineData(2u, 255)]   // version 2: it means opaque, as D3D has it
    public void AlphaConventionStillFollowsTheVersionOnALaterFrame(uint version, int expectedAlpha)
    {
        var bytes = TinyImageWriter.VbmFrames(
            2, 2, format: 0, [Flat(4, 0x0000), Flat(4, 0x8000)], mipField: 0, levels: 1, version);
        Assert.Equal((byte)expectedAlpha, VbmCodec.DecodeFrame(bytes, 1, "a.vbm").Get(0, 0).A);
    }

    [Fact]
    public void AFrameIndexOutsideTheFileIsRefused()
    {
        var bytes = ThreeDistinctFrames();
        var ex = Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(bytes, 3, "a.vbm"));
        Assert.Contains("no frame 3", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(bytes, -1, "a.vbm"));
    }

    [Fact]
    public void AFrameThatRunsPastTheEndOfTheFileIsRefused()
    {
        var bytes = ThreeDistinctFrames();
        // Keep frames 0 and 1 whole and cut frame 2 in half.
        var truncated = bytes[..(32 + 42 * 2 + 10)];
        Assert.Equal((0, 255, 0, 255), VbmCodec.DecodeFrame(truncated, 1, "a.vbm").Get(0, 0));
        Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(truncated, 2, "a.vbm"));
    }

    // ── Hostile headers ───────────────────────────────────────────────────────

    [Fact]
    public void AHeaderClaimingMillionsOfFramesIsRefusedBeforeAnythingIsAllocated()
    {
        var bytes = TinyImageWriter.VbmFrames(
            4, 4, 2, [Flat(16, 0)], mipField: 0, levels: 1, frameCountOverride: 2_000_000_000);
        var ex = Assert.Throws<ImageDecodeException>(() => VbmCodec.ReadInfo(bytes, "evil.vbm"));
        Assert.Contains("2,000,000,000 frames", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(bytes, 0, "evil.vbm"));
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes, "evil.vbm"));
    }

    [Fact]
    public void AHeaderJustOverTheFrameCapIsRefusedAndOneJustUnderItIsNot()
    {
        var one = new ushort[][] { Flat(1, 0) };
        var over = TinyImageWriter.VbmFrames(
            1, 1, 2, one, 0, 1, frameCountOverride: VbmCodec.MaxFrameCount + 1);
        Assert.Throws<ImageDecodeException>(() => VbmCodec.ReadInfo(over, "a.vbm"));

        var under = TinyImageWriter.VbmFrames(
            1, 1, 2, one, 0, 1, frameCountOverride: VbmCodec.MaxFrameCount);
        Assert.Equal(VbmCodec.MaxFrameCount, VbmCodec.ReadInfo(under, "a.vbm").FrameCount);
        // …and the frames it does not actually hold still fail one at a time, cheaply.
        Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(under, 9_999, "a.vbm"));
    }

    [Fact]
    public void AHeaderClaimingAnEnormousImageIsRefusedFromTheHeaderAlone()
    {
        var bytes = TinyImageWriter.VbmFrames(4, 4, 2, [Flat(16, 0)], 0, 1);
        // 30000 x 30000, past the engine's own 16384 limit.
        bytes[8] = 0x30; bytes[9] = 0x75; bytes[10] = 0; bytes[11] = 0;
        bytes[12] = 0x30; bytes[13] = 0x75; bytes[14] = 0; bytes[15] = 0;
        Assert.Throws<ImageDecodeException>(() => VbmCodec.ReadInfo(bytes, "huge.vbm"));

        // 8200 x 8200 is legal for the engine but past the decode budget, and must be refused
        // without reserving a quarter of a gigabyte for it.
        bytes[8] = 0x08; bytes[9] = 0x20; bytes[12] = 0x08; bytes[13] = 0x20;
        Assert.Throws<ImageDecodeException>(() => VbmCodec.DecodeFrame(bytes, 0, "huge.vbm"));
    }

    // ── Naming ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 2)]
    [InlineData(9, 2)]
    [InlineData(10, 2)]
    [InlineData(100, 2)]
    [InlineData(101, 3)]
    [InlineData(1000, 3)]
    [InlineData(1001, 4)]
    public void PaddingIsWideEnoughForTheHighestFrameNumberAndNeverNarrowerThanTwo(
        int frameCount, int expected) =>
        Assert.Equal(expected, VbmImportPlan.PadWidth(frameCount));

    [Fact]
    public void FrameNamesStartAtZeroAndAreZeroPadded()
    {
        var plan = Plan(FramesOf(12), "flame.vbm");
        Assert.Equal(12, plan.FrameFileNames.Count);
        Assert.Equal("flame_00.tga", plan.FrameFileNames[0]);
        Assert.Equal("flame_11.tga", plan.FrameFileNames[11]);
        Assert.Equal("flame.atx", plan.AtxFileName);
        Assert.True(plan.ReplacesSource);
    }

    [Fact]
    public void TheDefaultFrameNameIsCutDownUntilEveryNameFitsTheEngineLimit()
    {
        const string long1 = "mtl_reactor_coolant_pipe_hazard.vbm";   // 31 characters of stem
        string baseName = VbmImportPlan.DefaultFrameBaseName(long1, frameCount: 1000);
        var plan = Plan(FramesOf(1000), long1);

        Assert.Equal(baseName, plan.FrameBaseName);
        Assert.All(plan.FrameFileNames,
            n => Assert.True(n.Length <= AtxSchema.MaxBitmapNameLength, n));
        // "_999.tga" is eight characters, so the stem gets the remaining 23.
        Assert.Equal(AtxSchema.MaxBitmapNameLength, plan.FrameFileNames[^1].Length);
        Assert.Equal("mtl_reactor_coolant_pip", baseName);

        // The .atx, though, has to keep the .vbm's own name to supersede it, and that name is
        // already past the engine's limit — so the warning is about the .atx and nothing else.
        var warning = Assert.Single(plan.Messages, m => m.Severity == VbmImportSeverity.Warning);
        Assert.Contains("The .atx name", warning.Text, StringComparison.Ordinal);
        Assert.True(plan.CanRun);
    }

    [Fact]
    public void ATruncatedDefaultNameDoesNotEndInASeparator()
    {
        // The cut lands exactly on the underscore of "…_hazard"; leaving it would read as a typo.
        string baseName = VbmImportPlan.DefaultFrameBaseName("abcdefghijklmnopqrstuvw_x.vbm", 10);
        Assert.Equal("abcdefghijklmnopqrstuvw", baseName);
        Assert.DoesNotContain(
            VbmImportPlan.FrameFileName(baseName, 0, 2), "__", StringComparison.Ordinal);
    }

    [Fact]
    public void NamesDerivedFromAnArchiveEntryAreSanitisedBeforeUse()
    {
        // A .vpp name field holds 60 raw bytes and is not obliged to be a file name.
        Assert.Equal("a_b_c", VbmImportPlan.Sanitise("a<b>c"));
        Assert.Equal(".._.._evil", VbmImportPlan.Sanitise("..\\..\\evil"));
        Assert.Equal("evil_.atx", VbmImportPlan.DefaultAtxName("evil?.vbm"));
    }

    [Fact]
    public void AnEmptyOrImpossibleNameBlocksTheImport()
    {
        Assert.False(Plan(FramesOf(3), "a.vbm", atxName: "   ").CanRun);
        Assert.False(Plan(FramesOf(3), "a.vbm", atxName: "a<b>.atx").CanRun);
        Assert.False(Plan(FramesOf(3), "a.vbm", frameBase: string.Empty).CanRun);
        Assert.False(Plan(FramesOf(3), "a.vbm", frameBase: "a/b").CanRun);
        // A reserved device name would make a file Windows refuses to create.
        Assert.False(Plan(FramesOf(3), "a.vbm", atxName: "NUL.atx").CanRun);
    }

    [Fact]
    public void AnOverlongNameIsAWarningRatherThanARefusal()
    {
        var plan = Plan(FramesOf(3), "a.vbm", frameBase: new string('x', 40));
        Assert.True(plan.CanRun);
        Assert.Contains(plan.Messages, m =>
            m.Severity == VbmImportSeverity.Warning
            && m.Text.Contains("bitmap name", StringComparison.Ordinal));
    }

    [Fact]
    public void AFrameSharingTheAtxStemIsWarnedAboutButAllowed()
    {
        // "flame_00.tga" with the .atx called "flame_00.atx": legal, and baffling six months later.
        var plan = Plan(FramesOf(3), "flame.vbm", atxName: "flame_00.atx");
        Assert.True(plan.CanRun);
        Assert.Contains(plan.Messages, m =>
            m.Severity == VbmImportSeverity.Warning
            && m.Text.Contains("same stem", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAtxNamedSomethingElseSaysItWillNotReplaceTheVbm()
    {
        var plan = Plan(FramesOf(3), "flame.vbm", atxName: "torch.atx");
        Assert.False(plan.ReplacesSource);
        Assert.Contains("will not replace", plan.SupersedeNote, StringComparison.Ordinal);
        Assert.Contains("flame.atx", plan.SupersedeNote, StringComparison.Ordinal);
    }

    // ── Timing ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(15, 67)]
    [InlineData(10, 100)]
    [InlineData(30, 33)]
    [InlineData(1, 1000)]
    [InlineData(0, 100)]        // no usable rate: the format's own default
    [InlineData(-5, 100)]
    [InlineData(5000, 1)]       // faster than a millisecond a frame: clamped to the engine minimum
    public void FrameTimeIsTheNearestWholeMillisecondAndNeverBelowOne(int fps, int expected) =>
        Assert.Equal(expected, VbmImportPlan.FrameTimeFor(fps));

    [Fact]
    public void TheTimingSummaryReportsWhatTheRoundingActuallyGives()
    {
        var plan = Plan(FramesOf(4, fps: 15), "a.vbm");
        Assert.Equal(67, plan.FrameTimeMs);
        Assert.Contains("15 fps", plan.TimingSummary, StringComparison.Ordinal);
        Assert.Contains("67 ms", plan.TimingSummary, StringComparison.Ordinal);
        Assert.Contains("14.9", plan.TimingSummary, StringComparison.Ordinal);

        // A rate that divides exactly has nothing to confess to.
        var exact = Plan(FramesOf(4, fps: 10), "a.vbm");
        Assert.Equal("10 fps → 100 ms per frame.", exact.TimingSummary);
    }

    [Fact]
    public void ASingleFrameVbmIgnoresItsFpsFieldEntirely()
    {
        // The format says fps is meaningless with one frame, and stock files carry 1, 2, 15 and
        // everything between in that field. Reading one literally writes frame_time = 1000 into a
        // texture that never advances.
        var plan = Plan(FramesOf(1, fps: 1), "panel.vbm");
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, plan.FrameTimeMs);
        Assert.Contains("never advances", plan.TimingSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("at 1 fps", plan.ImportComment(), StringComparison.Ordinal);
    }

    [Fact]
    public void AVbmWithNoUsableFrameRateGetsTheDefaultAndSaysSo()
    {
        var plan = Plan(FramesOf(4, fps: 0), "a.vbm");
        Assert.Equal(AtxSchema.DefaultFrameTimeMs, plan.FrameTimeMs);
        Assert.Contains(plan.Messages, m =>
            m.Severity == VbmImportSeverity.Note
            && m.Text.Contains("no usable frame rate", StringComparison.Ordinal));
    }

    // ── Frame range and modes ─────────────────────────────────────────────────

    [Fact]
    public void AFrameRangeTrimsTheExportAndStillNumbersFromZero()
    {
        var plan = Plan(FramesOf(10), "a.vbm",
            options: new VbmImportOptions { FirstFrame = 4, LastFrame = 6 });
        Assert.Equal(["a_00.tga", "a_01.tga", "a_02.tga"], plan.FrameFileNames);
        Assert.Equal([4, 5, 6], plan.SourceFrames);
    }

    [Fact]
    public void AnEmptyRangeBlocksTheImport()
    {
        var plan = Plan(FramesOf(10), "a.vbm",
            options: new VbmImportOptions { FirstFrame = 6, LastFrame = 4 });
        Assert.False(plan.CanRun);
        Assert.Empty(plan.FrameFileNames);
    }

    [Fact]
    public void ModeDefaultsToLoopForAnAnimationAndStaticForASingleFrame()
    {
        Assert.Equal(AtxAnimationMode.Loop, Plan(FramesOf(8), "a.vbm").AnimationMode);
        Assert.Equal(AtxAnimationMode.Static, Plan(FramesOf(1), "a.vbm").AnimationMode);
        Assert.Contains(Plan(FramesOf(1), "a.vbm").Messages, m =>
            m.Severity == VbmImportSeverity.Note
            && m.Text.Contains("nothing to", StringComparison.Ordinal));
    }

    // ── The generated .atx ────────────────────────────────────────────────────

    [Fact]
    public void TheGeneratedTextIsTheLayoutTheEditorItselfWrites()
    {
        var plan = Plan(FramesOf(2, fps: 10, format: 1), "flame.vbm");
        string text = plan.AtxText;

        Assert.Contains("\r\n", text, StringComparison.Ordinal);
        Assert.StartsWith("# Imported from flame.vbm (VBM v1, 4 x 4, 4444, 2 frames at 10 fps) "
            + "by ATX Workbench.", text, StringComparison.Ordinal);
        Assert.Contains("frame_time = 100", text, StringComparison.Ordinal);
        Assert.Contains("animation_mode = 2", text, StringComparison.Ordinal);
        Assert.Contains("format = \"4444\"", text, StringComparison.Ordinal);
        Assert.Contains("file = \"flame_00.tga\"", text, StringComparison.Ordinal);
        // initially_on is the default, so it is not written at all.
        Assert.DoesNotContain("initially_on", text, StringComparison.Ordinal);
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommentBlockCanBeTurnedOffAndTheFormatKeyWithIt()
    {
        var plan = Plan(FramesOf(2), "a.vbm", options: new VbmImportOptions
        {
            IncludeComments = false,
            SetFormatToMatchSource = false,
        });
        Assert.StartsWith("[header]", plan.AtxText, StringComparison.Ordinal);
        Assert.DoesNotContain("format", plan.AtxText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGeneratedTextParsesToTheModelItDescribes()
    {
        var plan = Plan(FramesOf(3, fps: 20, format: 0), "flame.vbm");
        var parse = AtxParser.Parse(plan.AtxText);
        var model = parse.Model;

        Assert.NotNull(model);
        Assert.True(parse.IsCanonical);
        Assert.Equal(50, model!.Header.EffectiveFrameTimeMs);
        Assert.Equal(AtxAnimationMode.Loop, model.Header.EffectiveAnimationMode);
        Assert.Equal(3, model.Frames.Count);
        Assert.Equal("flame_00.tga", model.Frames[0].EffectiveFile);
        Assert.Equal(
            EngineFormat.Argb1555,
            AtxSchema.ParseFormatToken(model.Header.EffectiveFormat)?.Format);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void TheGeneratedFileLintsCleanOnceItsFramesAreOnDisk(int vbmFormat)
    {
        using var temp = new TempFolder();
        var bytes = TinyImageWriter.VbmFrames(
            8, 8, vbmFormat, [Flat(64, 0x1234), Flat(64, 0x4321), Flat(64, 0x2143)],
            mipField: 1, levels: 2, version: 1, fps: 12);
        var info = VbmCodec.ReadInfo(bytes, "hazard.vbm");
        var plan = VbmImportPlan.Create(info, "hazard.vbm", temp.Path);

        var result = VbmImporter.Run(bytes, plan);
        Assert.Equal(VbmImportOutcome.Succeeded, result.Outcome);

        var parse = AtxParser.Parse(File.ReadAllText(plan.AtxPath));
        var structural = AtxLinter.Analyze(parse);
        Assert.Empty(structural);

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        var assets = AtxAssetLinter.Analyze(parse, resolver);
        Assert.Empty(assets.Diagnostics);
        // ATX035 and ATX036 are about an alpha mask meeting a format; there is no mask here, so
        // neither has anything to say however the format key reads.
        Assert.DoesNotContain(assets.Diagnostics, d => d.Code is AtxRules.MaskPromotesFormat
            or AtxRules.MaskWith1555);
    }

    // ── Running the import ────────────────────────────────────────────────────

    [Fact]
    public void ImportWritesEveryFrameAndTheAtxLast()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames(fps: 20);
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);

        var steps = new List<VbmImportProgress>();
        var result = VbmImporter.Run(
            bytes, plan, progress: new Progress<VbmImportProgress>(steps.Add));

        Assert.Equal(VbmImportOutcome.Succeeded, result.Outcome);
        Assert.Equal(plan.AtxPath, result.AtxPath);
        Assert.Equal(4, result.Written.Count);
        Assert.Empty(result.Failures);
        foreach (string path in plan.AllPaths) Assert.True(File.Exists(path), path);

        // Each frame really is its own frame, decoded through our own reader.
        Assert.Equal((0, 0, 255, 255), ImageDecoder.Decode(
            File.ReadAllBytes(plan.FramePaths[0]), "f0.tga").Get(0, 0));
        Assert.Equal((255, 0, 0, 255), ImageDecoder.Decode(
            File.ReadAllBytes(plan.FramePaths[2]), "f2.tga").Get(0, 0));

        // No temporary files survive a successful run.
        Assert.Empty(Directory.GetFiles(temp.Path, "*.tmp"));
    }

    [Fact]
    public void AnAlphaCarryingSourceProducesThirtyTwoBitFramesAndASolidOneTwentyFour()
    {
        using var temp = new TempFolder();
        var masked = TinyImageWriter.VbmFrames(2, 2, 1, [Flat(4, 0x8F00)], 0, 1, fps: 10);
        var solid = TinyImageWriter.VbmFrames(2, 2, 2, [Flat(4, 0xF800)], 0, 1, fps: 10);

        var maskedPlan = PlanFor(masked, "soft.vbm", temp.Path);
        var solidPlan = PlanFor(solid, "hard.vbm", temp.Path);
        Assert.Equal(VbmImportOutcome.Succeeded, VbmImporter.Run(masked, maskedPlan).Outcome);
        Assert.Equal(VbmImportOutcome.Succeeded, VbmImporter.Run(solid, solidPlan).Outcome);

        Assert.Equal(EngineFormat.Argb8888,
            ImageProbe.Probe(File.ReadAllBytes(maskedPlan.FramePaths[0]), "a.tga").Format);
        Assert.Equal(EngineFormat.Rgb888,
            ImageProbe.Probe(File.ReadAllBytes(solidPlan.FramePaths[0]), "b.tga").Format);
        Assert.Equal((byte)136,
            ImageDecoder.Decode(File.ReadAllBytes(maskedPlan.FramePaths[0]), "a.tga").Get(0, 0).A);
    }

    [Fact]
    public void ACancelledImportLeavesTheFolderExactlyAsItFoundIt()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);

        var result = VbmImporter.Run(
            bytes, plan,
            progress: new Progress<VbmImportProgress>(_ => { }),
            cancellationToken: new CancellationToken(canceled: true));

        Assert.Equal(VbmImportOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void ATruncatedFileFailsBeforeAnythingReachesTheFolder()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);

        var result = VbmImporter.Run(bytes[..(32 + 42 + 4)], plan);
        Assert.Equal(VbmImportOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void CollisionsAreOneQuestionForTheWholeBatch()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);
        File.WriteAllText(plan.FramePaths[0], "mine");
        File.WriteAllText(plan.FramePaths[2], "also mine");

        int asked = 0;
        IReadOnlyList<string> seen = [];
        var result = VbmImporter.Run(bytes, plan, names =>
        {
            asked++;
            seen = names;
            return VbmCollisionChoice.KeepExisting;
        });

        Assert.Equal(1, asked);
        Assert.Equal(["flame_00.tga", "flame_02.tga"], seen);
        Assert.Equal(VbmImportOutcome.Succeeded, result.Outcome);
        Assert.Equal("mine", File.ReadAllText(plan.FramePaths[0]));
        Assert.Equal("also mine", File.ReadAllText(plan.FramePaths[2]));
        Assert.True(File.Exists(plan.FramePaths[1]));
        Assert.Equal(["flame_00.tga", "flame_02.tga"], (string[])[.. result.Kept.Order()]);
    }

    [Fact]
    public void ReplaceOverwritesAndCancelTouchesNothing()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);
        File.WriteAllText(plan.FramePaths[0], "mine");

        Assert.Equal(VbmImportOutcome.Cancelled,
            VbmImporter.Run(bytes, plan, _ => VbmCollisionChoice.Cancel).Outcome);
        Assert.Equal("mine", File.ReadAllText(plan.FramePaths[0]));
        Assert.Single(Directory.GetFiles(temp.Path));

        Assert.Equal(VbmImportOutcome.Succeeded,
            VbmImporter.Run(bytes, plan, _ => VbmCollisionChoice.Replace).Outcome);
        Assert.NotEqual("mine", File.ReadAllText(plan.FramePaths[0]));
    }

    [Fact]
    public void WithNoCollisionCallbackExistingFilesAreNeverOverwritten()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);
        File.WriteAllText(plan.AtxPath, "hand written");

        Assert.Equal(VbmImportOutcome.Cancelled, VbmImporter.Run(bytes, plan).Outcome);
        Assert.Equal("hand written", File.ReadAllText(plan.AtxPath));
    }

    [Fact]
    public void ImportCreatesTheOutputFolderWhenItIsNotThereYet()
    {
        using var temp = new TempFolder();
        string nested = Path.Combine(temp.Path, "frames", "flame");
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", nested);

        Assert.Equal(VbmImportOutcome.Succeeded, VbmImporter.Run(bytes, plan).Outcome);
        Assert.True(File.Exists(plan.AtxPath));
    }

    [Fact]
    public void AnImportThatCannotRunIsRefusedWithItsOwnFirstError()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var info = VbmCodec.ReadInfo(bytes, "flame.vbm");
        var plan = VbmImportPlan.Create(info, "flame.vbm", temp.Path, atxFileName: "a<b>.atx");

        var result = VbmImporter.Run(bytes, plan);
        Assert.Equal(VbmImportOutcome.Failed, result.Outcome);
        Assert.Contains("characters a file name cannot hold", result.Message!, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void TheEstimatedSizeMatchesWhatIsActuallyWritten()
    {
        using var temp = new TempFolder();
        var bytes = ThreeDistinctFrames();
        var plan = PlanFor(bytes, "flame.vbm", temp.Path);
        Assert.Equal(VbmImportOutcome.Succeeded, VbmImporter.Run(bytes, plan).Outcome);

        long onDisk = plan.FramePaths.Sum(p => new FileInfo(p).Length);
        Assert.Equal(plan.TotalFrameBytes, onDisk);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] FramesOf(int count, int fps = 10, int format = 2)
    {
        var frames = new ushort[count][];
        for (int i = 0; i < count; i++) frames[i] = Flat(16, (ushort)(i + 1));
        return TinyImageWriter.VbmFrames(4, 4, format, frames, mipField: 0, levels: 1, fps: fps);
    }

    private static VbmImportPlan Plan(
        byte[] vbm, string name, string? atxName = null, string? frameBase = null,
        VbmImportOptions? options = null) =>
        VbmImportPlan.Create(
            VbmCodec.ReadInfo(vbm, name), name, @"C:\out", atxName, frameBase, options);

    private static VbmImportPlan PlanFor(byte[] vbm, string name, string folder) =>
        VbmImportPlan.Create(VbmCodec.ReadInfo(vbm, name), name, folder);
}
