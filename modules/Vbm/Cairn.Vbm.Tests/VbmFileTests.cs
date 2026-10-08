using Cairn.Formats.Imaging;

namespace Cairn.Vbm.Tests;

public sealed class VbmFileTests(Xunit.Abstractions.ITestOutputHelper output)
{
    public static TheoryData<VbmPixelFormat, uint> Formats() => new()
    {
        { VbmPixelFormat.Argb1555, 1 }, { VbmPixelFormat.Argb1555, 2 },
        { VbmPixelFormat.Argb4444, 1 }, { VbmPixelFormat.Argb4444, 2 },
        { VbmPixelFormat.Rgb565, 1 }, { VbmPixelFormat.Rgb565, 2 },
    };

    [Fact]
    public void EveryStockBitmap_WritesBackByteIdentical()
    {
        var stock = TestData.Stock();
        if (stock.Count == 0) return; // no game folder: nothing to compare against
        foreach (var sample in stock)
        {
            var read = VbmFile.Read(sample.Bytes, sample.Name);
            Assert.False(read.IsTruncated, sample.Name);
            Assert.Equal(sample.Bytes, read.File.Write());
        }
        output.WriteLine($"{stock.Count} stock bitmaps from {stock.Select(s => s.Packfile).Distinct().Count()} packfiles written back byte for byte");
    }

    [Fact]
    public void EveryStockLevel_DecodesAndEncodesBackToTheSameBytes()
    {
        var stock = TestData.Stock();
        if (stock.Count == 0) return;
        int levels = 0;
        foreach (var sample in stock)
        {
            var file = VbmFile.Read(sample.Bytes, sample.Name).File;
            for (int f = 0; f < file.FrameCount; f++)
            {
                for (int l = 0; l < file.MipLevels; l++)
                {
                    var image = file.Decode(f, l);
                    Assert.Equal(file.Frames[f].Levels[l], VbmEncoder.EncodeLevel(image, file.Format, file.Version));
                    levels++;
                }
            }
            // the shared decoder and this one agree on frame 0
            Assert.Equal(VbmCodec.DecodeFrame(sample.Bytes, 0, sample.Name).Pixels, file.Decode(0).Pixels);
        }
        Assert.True(levels > 0);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void SyntheticPixels_RoundTripThroughEveryFormat(VbmPixelFormat format, uint version)
    {
        // every 16-bit value decodes and encodes back to itself
        var raw = new byte[256 * 256 * 2];
        for (int v = 0; v < 65536; v++) { raw[v * 2] = (byte)v; raw[v * 2 + 1] = (byte)(v >> 8); }
        var image = VbmEncoder.DecodeLevel(raw, 256, 256, format, version);
        var encoded = VbmEncoder.EncodeLevel(image, format, version);
        if (format == VbmPixelFormat.Rgb565 || format == VbmPixelFormat.Argb4444 || format == VbmPixelFormat.Argb1555)
            Assert.Equal(raw, encoded);

        // and a BGRA image survives as the quantised image the engine would show
        var gradient = TestData.Gradient(37, 19, 3);
        var back = VbmEncoder.DecodeLevel(VbmEncoder.EncodeLevel(gradient, format, version), 37, 19, format, version);
        Assert.Equal(FormatSimulator.Quantise(gradient, format.ToEngine()).Pixels, back.Pixels);
    }

    [Fact]
    public void Version1_StoresThe1555AlphaBitInverted()
    {
        var image = TestData.Flat(1, 2, 255, 255, 255, 255);
        image.Set(0, 1, 0, 0, 0, 0);
        var v1 = VbmEncoder.EncodeLevel(image, VbmPixelFormat.Argb1555, 1);
        var v2 = VbmEncoder.EncodeLevel(image, VbmPixelFormat.Argb1555, 2);
        Assert.Equal(0x7FFF, v1[0] | v1[1] << 8); // opaque white: bit clear in version 1
        Assert.Equal(0x8000, v1[2] | v1[3] << 8); // transparent black: bit set, as in the stock panels
        Assert.Equal(0xFFFF, v2[0] | v2[1] << 8);
        Assert.Equal(0x0000, v2[2] | v2[3] << 8);
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void WrittenFiles_ReadBackThroughTheSharedCodec(VbmPixelFormat format, uint version)
    {
        var frames = Enumerable.Range(0, 3).Select(i => VbmEncoder.EncodeFrame(TestData.Gradient(32, 16, i), format, version, 4)).ToArray();
        var file = new VbmFile(version, 32, 16, format, 12, 4, frames, []);
        var bytes = file.Write();
        var info = VbmCodec.ReadInfo(bytes, "synthetic.vbm");
        Assert.Equal((32, 16, 3, 4, 12), (info.Width, info.Height, info.FrameCount, info.MipLevels, info.Fps));
        Assert.True(info.LengthMatchesHeader);
        Assert.Equal(format.ToEngine(), info.Format);
        for (int i = 0; i < 3; i++) Assert.Equal(file.Decode(i).Pixels, VbmCodec.DecodeFrame(bytes, i, "synthetic.vbm").Pixels);
        Assert.Equal(bytes, VbmFile.Read(bytes, "synthetic.vbm").File.Write());
    }

    [Fact]
    public void MipChains_HalveDownToTheRequestedLevel()
    {
        var frame = VbmEncoder.EncodeFrame(TestData.Gradient(64, 16), VbmPixelFormat.Argb4444, 2, 7);
        int[] expected = [64 * 16, 32 * 8, 16 * 4, 8 * 2, 4 * 1, 2 * 1, 1 * 1];
        Assert.Equal(expected.Select(n => n * 2), frame.Levels.Select(l => l.Length));
        Assert.Equal(7, VbmFile.MaxMipLevels(64, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => VbmEncoder.EncodeFrame(TestData.Gradient(4, 4), VbmPixelFormat.Rgb565, 2, 4));

        // a flat colour stays that colour on every level
        var flat = VbmEncoder.EncodeFrame(TestData.Flat(16, 16, 0x40, 0x80, 0xC0), VbmPixelFormat.Rgb565, 2, 5);
        foreach (var level in flat.Levels) Assert.All(Enumerable.Range(0, level.Length / 2), i => Assert.Equal(flat.Levels[0][0], level[i * 2]));
    }

    [Fact]
    public void TruncatedFiles_KeepTheirCompleteFrames()
    {
        byte[] payload = TestData.Noise(4 * 4 * 2 * 3 - 5, 1); // three frames declared, the last one cut short
        var read = VbmFile.Read(TestData.Raw(1, 4, 4, 2, 15, 3, 0, payload), "cut.vbm");
        Assert.True(read.IsTruncated);
        Assert.Equal(2, read.File.FrameCount);
        Assert.Equal(3, read.DeclaredFrameCount);
        Assert.Equal(5, read.MissingBytes);
        Assert.Contains(VbmChecks.Check(read.File, read), p => p.Code == "VBM002" && p.Severity == VbmSeverity.Error);
        Assert.Equal(32 + 64, read.File.Write().Length);

        Assert.Throws<ImageDecodeException>(() => VbmFile.Read(TestData.Raw(1, 4, 4, 2, 15, 1, 0, new byte[10]), "short.vbm"));
        Assert.Throws<ImageDecodeException>(() => VbmFile.Read(TestData.Raw(1, 4, 4, 7, 15, 1, 0, new byte[32]), "format.vbm"));
        Assert.Throws<ImageDecodeException>(() => VbmFile.Read([1, 2, 3], "tiny.vbm"));
    }

    [Fact]
    public void TrailingBytesAndOddHeaders_AreReportedAndKept()
    {
        byte[] payload = [.. TestData.Noise(32, 2), 1, 2, 3];
        byte[] bytes = TestData.Raw(2, 4, 4, 1, 0, 1, 0, payload);
        var read = VbmFile.Read(bytes, "tail.vbm");
        Assert.Equal(3, read.File.Trailing.Length);
        Assert.Equal(bytes, read.File.Write());
        Assert.Contains(VbmChecks.Check(read.File, read), p => p.Code == "VBM003");

        var zeroFrames = VbmFile.Read(TestData.Raw(1, 4, 4, 2, 15, 0, 0, new byte[32]), "zero.vbm");
        Assert.Equal(1, zeroFrames.File.FrameCount);
        Assert.Contains(VbmChecks.Check(zeroFrames.File, zeroFrames), p => p.Code == "VBM009");

        var badMips = VbmFile.Read(TestData.Raw(1, 4, 4, 2, 15, 1, 9, new byte[32 + 8 + 2]), "mips.vbm");
        Assert.Equal(3, badMips.File.MipLevels);
        Assert.Contains(VbmChecks.Check(badMips.File, badMips), p => p.Code == "VBM004");
    }

    [Fact]
    public void Write_RefusesFramesThatDoNotMatchTheHeader()
    {
        var frame = VbmEncoder.EncodeFrame(TestData.Gradient(8, 8), VbmPixelFormat.Rgb565, 2, 1);
        Assert.Throws<InvalidOperationException>(() => new VbmFile(2, 8, 8, VbmPixelFormat.Rgb565, 15, 2, [frame], []).Write());
        Assert.Throws<InvalidOperationException>(() => new VbmFile(2, 16, 8, VbmPixelFormat.Rgb565, 15, 1, [frame], []).Write());
        Assert.Throws<InvalidOperationException>(() => new VbmFile(2, 8, 8, VbmPixelFormat.Rgb565, 15, 1, [], []).Write());
    }
}
