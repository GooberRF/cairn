using Cairn.Formats.Imaging;

namespace Cairn.Rfa.Tests;

public class ImagingTests
{
    [Fact]
    public void TgaDecodesToBgra()
    {
        var image = ImageDecoder.Decode(TestImages.Tga24(4, 2, 10, 20, 30), "skin.tga");
        Assert.Equal(4, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal((10, 20, 30, 255), image.Get(3, 1));
        var info = ImageProbe.Probe(TestImages.Tga24(4, 2, 0, 0, 0), "skin.tga");
        Assert.Equal(ImageContainer.Tga, info.Container);
        Assert.Equal(EngineFormat.Rgb888, info.Format);
    }

    [Fact]
    public void VbmFrameZeroDecodes()
    {
        // 565: pure red is 0xF800.
        var image = ImageDecoder.Decode(TestImages.Vbm565(2, 2, 0xF800), "anim.vbm");
        Assert.Equal((0, 0, 255, 255), image.Get(1, 1));
        Assert.Equal(ImageContainer.Vbm, ImageProbe.Probe(TestImages.Vbm565(2, 2, 0), "x.vbm").Container);
    }

    [Fact]
    public void DdsAndPngDecode()
    {
        var dds = ImageDecoder.Decode(TestImages.Dds32(2, 2, 1, 2, 3, 4), "x.dds");
        Assert.Equal((1, 2, 3, 4), dds.Get(0, 0));
        var png = ImageDecoder.Decode(TestImages.Png(3, 3, 50, 60, 70, 255), "x.png");
        Assert.Equal((50, 60, 70, 255), png.Get(2, 2));
    }

    [Fact]
    public void HostileHeadersAreRefusedBeforeAllocating()
    {
        // An 18-byte TGA claiming 16384 x 16384 has no pixels to back it.
        var header = TestImages.Tga24(1, 1, 0, 0, 0)[..18];
        header[12] = 0x00;
        header[13] = 0x40;
        header[14] = 0x00;
        header[15] = 0x40;
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode(header, "big.tga"));
        Assert.Throws<ImageTooLargeException>(() => DecodeLimits.EnsureWithinBudget(16384, 16384, "x"));
        Assert.Throws<ImageDecodeException>(() => ImageDecoder.Decode([1, 2, 3], "nothing.bin"));
    }
}
