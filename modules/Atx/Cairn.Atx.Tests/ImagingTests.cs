using Cairn.Formats.Imaging;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

public class ImagingTests
{
    private static byte[] Bgr(int width, int height, Func<int, int, (byte B, byte G, byte R)> shade)
    {
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var (b, g, r) = shade(x, y);
                int i = (y * width + x) * 3;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        }
        return pixels;
    }

    // ── TGA ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Tga24RoundTrips()
    {
        var bytes = TinyImageWriter.Tga24(4, 3, Bgr(4, 3, (x, y) => ((byte)(x * 10), (byte)(y * 20), 200)));
        var info = ImageProbe.Probe(bytes, "a.tga");
        Assert.Equal(ImageContainer.Tga, info.Container);
        Assert.Equal(4, info.Width);
        Assert.Equal(3, info.Height);
        Assert.Equal(EngineFormat.Rgb888, info.Format);
        Assert.Null(info.MipLevels);

        var image = ImageDecoder.Decode(bytes, "a.tga");
        Assert.Equal((20, 40, 200, 255), image.Get(2, 2));
    }

    [Fact]
    public void Tga24BottomUpIsFlippedBack()
    {
        var pixels = Bgr(2, 2, (x, y) => ((byte)(y == 0 ? 255 : 0), 0, 0));
        var top = ImageDecoder.Decode(TinyImageWriter.Tga24(2, 2, pixels, topDown: true), "t.tga");
        var bottom = ImageDecoder.Decode(TinyImageWriter.Tga24(2, 2, pixels, topDown: false), "b.tga");
        Assert.Equal(top.Pixels, bottom.Pixels);
        Assert.Equal((byte)255, top.Get(0, 0).B);
        Assert.Equal((byte)0, top.Get(0, 1).B);
    }

    [Fact]
    public void Tga32CarriesAlpha()
    {
        var pixels = new byte[] { 1, 2, 3, 128, 4, 5, 6, 255 };
        var bytes = TinyImageWriter.Tga32(2, 1, pixels);
        Assert.Equal(EngineFormat.Argb8888, ImageProbe.Probe(bytes, "a.tga").Format);
        var image = ImageDecoder.Decode(bytes, "a.tga");
        Assert.Equal((1, 2, 3, 128), image.Get(0, 0));
    }

    [Fact]
    public void EightBitGreyscaleTgaIsClassifiedAsPaletted()
    {
        var bytes = TinyImageWriter.Tga8Grey(2, 2, [0, 64, 128, 255]);
        var info = ImageProbe.Probe(bytes, "mask.tga");
        Assert.Equal(EngineFormat.Paletted8, info.Format);
        var image = ImageDecoder.Decode(bytes, "mask.tga");
        Assert.Equal((128, 128, 128, 255), image.Get(0, 1));
    }

    [Fact]
    public void PalettedTgaResolvesThroughItsColourMap()
    {
        byte[] palette = [0, 0, 255, /* entry 0: red */ 0, 255, 0 /* entry 1: green */];
        var bytes = TinyImageWriter.Tga8Paletted(2, 1, [0, 1], palette);
        var info = ImageProbe.Probe(bytes, "p.tga");
        Assert.Equal(EngineFormat.Paletted8, info.Format);
        var image = ImageDecoder.Decode(bytes, "p.tga");
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((0, 255, 0, 255), image.Get(1, 0));
    }

    [Fact]
    public void SixteenBitTgaIsArgb1555()
    {
        ushort[] pixels = [0x8000 | (31 << 10), 0x001F];
        var bytes = TinyImageWriter.Tga16(2, 1, pixels);
        Assert.Equal(EngineFormat.Argb1555, ImageProbe.Probe(bytes, "a.tga").Format);
        var image = ImageDecoder.Decode(bytes, "a.tga");
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((255, 0, 0, 0), image.Get(1, 0));
    }

    [Fact]
    public void RleTgaDecodesIdenticallyToItsUncompressedForm()
    {
        var pixels = Bgr(8, 4, (x, y) => ((byte)(x < 4 ? 200 : 20), (byte)(y * 3), 128));
        var plain = ImageDecoder.Decode(TinyImageWriter.Tga24(8, 4, pixels), "plain.tga");
        var rle = ImageDecoder.Decode(TinyImageWriter.Tga24Rle(8, 4, pixels), "rle.tga");
        Assert.Equal(plain.Pixels, rle.Pixels);
    }

    [Fact]
    public void RleGreyscaleTgaDecodesIdenticallyToItsUncompressedForm()
    {
        var grey = new byte[16];
        for (int i = 0; i < grey.Length; i++) grey[i] = (byte)(i < 8 ? 10 : i * 7);
        var plain = ImageDecoder.Decode(TinyImageWriter.Tga8Grey(4, 4, grey), "plain.tga");
        var rle = ImageDecoder.Decode(TinyImageWriter.Tga8GreyRle(4, 4, grey), "rle.tga");
        Assert.Equal(plain.Pixels, rle.Pixels);
    }

    [Fact]
    public void TruncatedTgaThrowsOneTypedException()
    {
        var bytes = TinyImageWriter.Tga24(8, 8, Bgr(8, 8, (_, _) => (1, 2, 3)));
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(bytes[..40], "short.tga"));
    }

    [Fact]
    public void TgaWithAbsurdDimensionsIsRejected()
    {
        var bytes = TinyImageWriter.Tga24(1, 1, [0, 0, 0]);
        bytes[12] = 0xFF; bytes[13] = 0xFF; // width 65535
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes, "huge.tga"));
    }

    [Fact]
    public void UnsupportedTgaImageTypeIsRejected()
    {
        var bytes = TinyImageWriter.Tga24(1, 1, [0, 0, 0]);
        bytes[2] = 32; // "compressed colour-mapped, Huffman" — not supported
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes, "odd.tga"));
    }

    // ── DDS ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Dxt1ProbeReportsFormatAndMipCount()
    {
        var bytes = TinyImageWriter.DdsDxt1(8, 8, (_, _) => (0xF800, 0x001F, 0x00000000u), mipLevels: 4);
        var info = ImageProbe.Probe(bytes, "a.dds");
        Assert.Equal(ImageContainer.Dds, info.Container);
        Assert.Equal(EngineFormat.Dxt1, info.Format);
        Assert.Equal(4, info.MipLevels);
        Assert.Equal(8, info.Width);
    }

    [Fact]
    public void DdsWithoutTheMipmapFlagReportsOneLevel()
    {
        var bytes = TinyImageWriter.DdsDxt1(8, 8, (_, _) => (0xF800, 0x001F, 0u));
        Assert.Equal(1, ImageProbe.Probe(bytes, "a.dds").MipLevels);
    }

    [Fact]
    public void Dxt1DecodesItsBlockColours()
    {
        // Selector bits all zero: every pixel takes colour 0, which is pure red in 565.
        var bytes = TinyImageWriter.DdsDxt1(4, 4, (_, _) => (0xF800, 0x001F, 0u));
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((0, 0, 255, 255), image.Get(3, 3));
    }

    [Fact]
    public void Dxt1PunchThroughAlphaIsTransparent()
    {
        // c0 <= c1 selects the 3-colour + transparent mode; selector 3 is the transparent index.
        var bytes = TinyImageWriter.DdsDxt1(4, 4, (_, _) => (0x0000, 0xFFFF, 0xFFFFFFFFu));
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal((byte)0, image.Get(0, 0).A);
    }

    [Fact]
    public void Dxt3ExplicitAlphaIsRead()
    {
        var bytes = TinyImageWriter.DdsDxtWithAlpha(4, 4, dxt5: false, (_, _) =>
        {
            var block = new byte[16];
            for (int i = 0; i < 8; i++) block[i] = 0xF0; // alternate nibbles 0 and 15
            block[8] = 0x00; block[9] = 0xF8;            // colour 0 = red
            return block;
        });
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal(EngineFormat.Dxt3, ImageProbe.Probe(bytes, "a.dds").Format);
        Assert.Equal((byte)0, image.Get(0, 0).A);
        Assert.Equal((byte)255, image.Get(1, 0).A);
    }

    [Fact]
    public void Dxt5InterpolatedAlphaIsRead()
    {
        var bytes = TinyImageWriter.DdsDxtWithAlpha(4, 4, dxt5: true, (_, _) =>
        {
            var block = new byte[16];
            block[0] = 255; block[1] = 0;   // a0 > a1: eight-step interpolation
            block[10] = 0x00; block[11] = 0xF8;
            return block;
        });
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal(EngineFormat.Dxt5, ImageProbe.Probe(bytes, "a.dds").Format);
        Assert.Equal((byte)255, image.Get(0, 0).A); // index 0 selects a0
    }

    [Fact]
    public void UncompressedBgraDdsDecodes()
    {
        byte[] bgra = [10, 20, 30, 40, 50, 60, 70, 80];
        var bytes = TinyImageWriter.DdsBgra32(2, 1, bgra);
        var info = ImageProbe.Probe(bytes, "a.dds");
        Assert.Equal(EngineFormat.Argb8888, info.Format);
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal((10, 20, 30, 40), image.Get(0, 0));
        Assert.Equal((50, 60, 70, 80), image.Get(1, 0));
    }

    [Fact]
    public void Uncompressed565DdsDecodes()
    {
        var bytes = TinyImageWriter.Dds565(2, 1, [0xF800, 0x001F]);
        Assert.Equal(EngineFormat.Rgb565, ImageProbe.Probe(bytes, "a.dds").Format);
        var image = ImageDecoder.Decode(bytes, "a.dds");
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((255, 0, 0, 255), image.Get(1, 0));
    }

    [Fact]
    public void TruncatedDdsThrows()
    {
        var bytes = TinyImageWriter.DdsDxt1(16, 16, (_, _) => (0xF800, 0x001F, 0u));
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(bytes[..140], "short.dds"));
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes[..20], "short.dds"));
    }

    // ── VBM ──────────────────────────────────────────────────────────────────

    // The mip field counts the levels below the base, so a file holding three levels writes 2.
    // Settled against every .vbm in a retail install's root .vpp archives; see VbmCodec's remarks.
    [Fact]
    public void VbmMipFieldCountsTheLevelsBelowTheBase()
    {
        var bytes = TinyImageWriter.Vbm(8, 8, format: 0, topLevel: new ushort[64], mipField: 2, levels: 3);
        var info = ImageProbe.Probe(bytes, "a.vbm");
        Assert.Equal(ImageContainer.Vbm, info.Container);
        Assert.Equal(EngineFormat.Argb1555, info.Format);
        Assert.Equal(3, info.MipLevels);
        Assert.Equal(string.Empty, info.Note is null ? string.Empty : NoteSuffix(info.Note));
    }

    [Fact]
    public void VbmMipFieldOfZeroMeansTheBaseLevelOnly()
    {
        var bytes = TinyImageWriter.Vbm(8, 8, format: 0, topLevel: new ushort[64], mipField: 0, levels: 1);
        Assert.Equal(1, ImageProbe.Probe(bytes, "a.vbm").MipLevels);
    }

    [Fact]
    public void VbmMipCountIsFlaggedWhenTheFileLengthDisagrees()
    {
        // Claims three levels below the base but holds only the base: the count is still what the
        // header says, with a note, because there is nothing better to go on.
        var bytes = TinyImageWriter.Vbm(8, 8, format: 0, topLevel: new ushort[64], mipField: 3, levels: 1);
        var info = ImageProbe.Probe(bytes, "a.vbm");
        Assert.Equal(4, info.MipLevels);
        Assert.Contains("could not be verified", info.Note, StringComparison.Ordinal);
    }

    // ── VBM 1555 alpha ───────────────────────────────────────────────────────

    // Version 1 stores the top bit as "transparent"; version 2 stores it the standard way round.
    [Theory]
    [InlineData(1u, 0x0000, 255)]   // v1, bit clear → opaque
    [InlineData(1u, 0x8000, 0)]     // v1, bit set   → transparent
    [InlineData(2u, 0x0000, 0)]     // v2, bit clear → transparent
    [InlineData(2u, 0x8000, 255)]   // v2, bit set   → opaque
    public void Vbm1555AlphaBitDependsOnTheHeaderVersion(uint version, int pixel, int expectedAlpha)
    {
        var bytes = TinyImageWriter.Vbm(
            1, 1, format: 0, topLevel: [(ushort)pixel], mipField: 0, levels: 1, version: version);
        Assert.Equal((byte)expectedAlpha, ImageDecoder.Decode(bytes, "a.vbm").Get(0, 0).A);
    }

    [Fact]
    public void Vbm1555ColourIsUnaffectedByTheAlphaConvention()
    {
        // 0x7C00 is full red with the bit clear; 0xFC00 is the same red with it set.
        var v1 = TinyImageWriter.Vbm(1, 1, 0, [0xFC00], mipField: 0, levels: 1, version: 1);
        var v2 = TinyImageWriter.Vbm(1, 1, 0, [0xFC00], mipField: 0, levels: 1, version: 2);
        Assert.Equal((0, 0, 255, 0), ImageDecoder.Decode(v1, "a.vbm").Get(0, 0));
        Assert.Equal((0, 0, 255, 255), ImageDecoder.Decode(v2, "a.vbm").Get(0, 0));
    }

    [Fact]
    public void VbmProbeReportsTheHeaderVersion()
    {
        var v1 = ImageProbe.Probe(
            TinyImageWriter.Vbm(2, 2, 0, new ushort[4], 0, 1, version: 1), "a.vbm");
        var v2 = ImageProbe.Probe(
            TinyImageWriter.Vbm(2, 2, 0, new ushort[4], 0, 1, version: 2), "a.vbm");
        Assert.Equal(1, v1.ContainerVersion);
        Assert.Equal(2, v2.ContainerVersion);
        Assert.Equal("VBM v1", v1.ContainerLabel);
        Assert.Contains("VBM v2", v2.Describe(), StringComparison.Ordinal);
    }

    /// <summary>The parenthesised tail of a probe note, or empty when there is none.</summary>
    private static string NoteSuffix(string note)
    {
        int at = note.IndexOf(" (", StringComparison.Ordinal);
        return at < 0 ? string.Empty : note[at..];
    }

    [Theory]
    [InlineData(0, EngineFormat.Argb1555)]
    [InlineData(1, EngineFormat.Argb4444)]
    [InlineData(2, EngineFormat.Rgb565)]
    public void VbmFormatsMapToEngineFormats(int format, EngineFormat expected)
    {
        var bytes = TinyImageWriter.Vbm(2, 2, format, new ushort[4], mipField: 0, levels: 1);
        Assert.Equal(expected, ImageProbe.Probe(bytes, "a.vbm").Format);
    }

    [Fact]
    public void VbmDecodes565Pixels()
    {
        var bytes = TinyImageWriter.Vbm(2, 1, format: 2, topLevel: [0xF800, 0x001F], mipField: 0, levels: 1);
        var image = ImageDecoder.Decode(bytes, "a.vbm");
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((255, 0, 0, 255), image.Get(1, 0));
    }

    [Fact]
    public void VbmWithUnknownFormatOrTruncatedBodyThrows()
    {
        var bad = TinyImageWriter.Vbm(2, 2, format: 9, new ushort[4], mipField: 0, levels: 1);
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bad, "a.vbm"));

        var truncated = TinyImageWriter.Vbm(8, 8, format: 0, new ushort[64], mipField: 0, levels: 1);
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(truncated[..40], "a.vbm"));
    }

    // ── PNG and JPEG ─────────────────────────────────────────────────────────

    private static byte[][] Rows(int width, int height, int bytesPerPixel, byte fill)
    {
        var rows = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new byte[width * bytesPerPixel];
            Array.Fill(rows[y], fill);
        }
        return rows;
    }

    [Theory]
    [InlineData(0, 1, false, EngineFormat.Rgb888)]   // greyscale
    [InlineData(0, 1, true, EngineFormat.Argb8888)]  // greyscale + tRNS = 2 channels
    [InlineData(2, 3, false, EngineFormat.Rgb888)]   // RGB
    [InlineData(2, 3, true, EngineFormat.Argb8888)]  // RGB + tRNS = 4 channels
    [InlineData(4, 2, false, EngineFormat.Argb8888)] // greyscale + alpha
    [InlineData(6, 4, false, EngineFormat.Argb8888)] // RGBA
    public void PngChannelCountFollowsStbImage(
        int colorType, int bytesPerPixel, bool transparency, EngineFormat expected)
    {
        byte[]? trns = transparency ? (colorType == 0 ? [0, 0] : [0, 0, 0, 0, 0, 0]) : null;
        var bytes = TinyImageWriter.Png(4, 2, colorType, Rows(4, 2, bytesPerPixel, 0x40), null, trns);
        var info = ImageProbe.Probe(bytes, "a.png");
        Assert.Equal(ImageContainer.Png, info.Container);
        Assert.Equal(expected, info.Format);
        Assert.Equal(1, info.MipLevels);
        Assert.Equal(4, info.Width);
        Assert.Equal(2, info.Height);
    }

    [Fact]
    public void PalettePngIsThreeChannelsAndFourWithTransparency()
    {
        byte[] palette = [255, 0, 0, 0, 255, 0];
        var opaque = TinyImageWriter.Png(2, 1, 3, [[0, 1]], palette);
        Assert.Equal(EngineFormat.Rgb888, ImageProbe.Probe(opaque, "p.png").Format);

        var masked = TinyImageWriter.Png(2, 1, 3, [[0, 1]], palette, [0, 255]);
        Assert.Equal(EngineFormat.Argb8888, ImageProbe.Probe(masked, "p.png").Format);
    }

    [Fact]
    public void PngPixelsDecodeThroughWpf()
    {
        var rows = new byte[][] { [255, 0, 0, 0, 255, 0], [0, 0, 255, 255, 255, 255] };
        var bytes = TinyImageWriter.Png(2, 2, 2, rows);
        var image = ImageDecoder.Decode(bytes, "a.png");
        Assert.Equal(2, image.Width);
        Assert.Equal((0, 0, 255, 255), image.Get(0, 0));
        Assert.Equal((255, 0, 0, 255), image.Get(0, 1));
    }

    [Fact]
    public void TruncatedPngThrows()
    {
        var bytes = TinyImageWriter.Png(4, 4, 2, Rows(4, 4, 3, 0x20));
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(bytes[..12], "short.png"));
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(bytes[..30], "short.png"));
    }

    [Fact]
    public void JpegProbeReadsTheFrameHeaderAndAlwaysReports888()
    {
        var jpeg = TestImages.Jpeg(8, 4);
        var info = ImageProbe.Probe(jpeg, "a.jpg");
        Assert.Equal(ImageContainer.Jpeg, info.Container);
        Assert.Equal(8, info.Width);
        Assert.Equal(4, info.Height);
        Assert.Equal(EngineFormat.Rgb888, info.Format);
        Assert.Equal(1, info.MipLevels);
        Assert.Equal(8, ImageDecoder.Decode(jpeg, "a.jpg").Width);
    }

    [Fact]
    public void TruncatedJpegThrows()
    {
        var jpeg = TestImages.Jpeg(8, 4);
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(jpeg[..4], "short.jpg"));
    }

    [Fact]
    public void UnknownContentIsRejected()
    {
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe([1, 2, 3, 4, 5, 6, 7, 8], "a.bin"));
        Assert.Throws<ImageDecodeException>(() => ImageProbe.Probe(new MemoryStream(), "empty.tga"));
    }

    // ── Format simulation and masks ──────────────────────────────────────────

    [Fact]
    public void QuantisingTo565DropsAlphaAndBanding()
    {
        var source = new BgraImage(1, 1);
        source.Set(0, 0, 9, 9, 9, 128);
        var result = FormatSimulator.Quantise(source, EngineFormat.Rgb565);
        Assert.Equal((byte)255, result.Get(0, 0).A);
        Assert.Equal((byte)8, result.Get(0, 0).B); // 9 >> 3 == 1, back to 8
    }

    [Fact]
    public void QuantisingTo1555MakesAlphaBinary()
    {
        var source = new BgraImage(2, 1);
        source.Set(0, 0, 0, 0, 0, 127);
        source.Set(1, 0, 0, 0, 0, 128);
        var result = FormatSimulator.Quantise(source, EngineFormat.Argb1555);
        Assert.Equal((byte)0, result.Get(0, 0).A);
        Assert.Equal((byte)255, result.Get(1, 0).A);
    }

    [Fact]
    public void AlphaMaskReplacesTheAlphaChannelPerFormat()
    {
        var frame = new BgraImage(1, 1);
        frame.Set(0, 0, 10, 20, 30, 255);
        var mask = new BgraImage(1, 1);
        mask.Set(0, 0, 100, 100, 100, 255);

        Assert.Equal((byte)100, AlphaMask.Apply(frame, mask, EngineFormat.Argb8888).Get(0, 0).A);
        Assert.Equal((byte)102, AlphaMask.Apply(frame, mask, EngineFormat.Argb4444).Get(0, 0).A);
        Assert.Equal((byte)0, AlphaMask.Apply(frame, mask, EngineFormat.Argb1555).Get(0, 0).A);
    }

    [Fact]
    public void AlphaMaskSizeMismatchThrows()
    {
        var frame = new BgraImage(2, 2);
        var mask = new BgraImage(1, 1);
        Assert.Throws<ImageDecodeException>(() => AlphaMask.Apply(frame, mask, EngineFormat.Argb8888));
    }

    [Fact]
    public void EffectiveFormatPromotesWhenAMaskIsPresent()
    {
        Assert.Equal(EngineFormat.Argb4444,
            AlphaMask.EffectiveFormat(EngineFormat.Rgb888, EngineFormat.Rgb565, hasMask: true));
        Assert.Equal(EngineFormat.Argb8888,
            AlphaMask.EffectiveFormat(EngineFormat.Rgb888, null, hasMask: true));
        Assert.Equal(EngineFormat.Rgb888,
            AlphaMask.EffectiveFormat(EngineFormat.Rgb888, null, hasMask: false));
    }
}
