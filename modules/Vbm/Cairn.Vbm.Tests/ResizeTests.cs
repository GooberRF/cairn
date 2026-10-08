using Cairn.Formats.Imaging;

namespace Cairn.Vbm.Tests;

public sealed class ResizeTests
{
    [Theory]
    [InlineData(64, 16, 32, 16, VbmFitMode.Stretch, 0, 0, 64, 16, 0, 0, 32, 16)]
    [InlineData(64, 16, 32, 16, VbmFitMode.KeepAspect, 0, 0, 64, 16, 0, 4, 32, 8)]
    [InlineData(16, 64, 32, 16, VbmFitMode.KeepAspect, 0, 0, 16, 64, 14, 0, 4, 16)]
    [InlineData(64, 16, 32, 16, VbmFitMode.CropCentre, 16, 0, 32, 16, 0, 0, 32, 16)]
    [InlineData(16, 64, 32, 16, VbmFitMode.CropCentre, 0, 28, 16, 8, 0, 0, 32, 16)]
    [InlineData(128, 64, 32, 16, VbmFitMode.KeepAspect, 0, 0, 128, 64, 0, 0, 32, 16)] // same shape: all of both
    [InlineData(128, 64, 32, 16, VbmFitMode.CropCentre, 0, 0, 128, 64, 0, 0, 32, 16)]
    [InlineData(1000, 1, 16, 16, VbmFitMode.KeepAspect, 0, 0, 1000, 1, 0, 7, 16, 1)] // never thinner than a pixel
    [InlineData(1000, 1, 16, 16, VbmFitMode.CropCentre, 499, 0, 1, 1, 0, 0, 16, 16)]
    public void Place_GivesTheSourceAndTargetRectangles(int sw, int sh, int w, int h, VbmFitMode fit,
        int sx, int sy, int sWidth, int sHeight, int tx, int ty, int tWidth, int tHeight)
    {
        Assert.Equal(new VbmFitPlacement(sx, sy, sWidth, sHeight, tx, ty, tWidth, tHeight), VbmResize.Place(sw, sh, w, h, fit));
    }

    public static TheoryData<VbmResizeFilter, VbmFitMode> AllOptions()
    {
        var data = new TheoryData<VbmResizeFilter, VbmFitMode>();
        foreach (var filter in Enum.GetValues<VbmResizeFilter>())
            foreach (var fit in Enum.GetValues<VbmFitMode>()) data.Add(filter, fit);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllOptions))]
    public void Fit_AlwaysGivesTheFrameSize(VbmResizeFilter filter, VbmFitMode fit)
    {
        var options = new VbmResizeOptions(filter, fit);
        foreach (var (w, h) in new[] { (64, 16), (16, 64), (7, 3), (1, 1), (100, 100) })
        {
            var result = VbmResize.Fit(TestData.Gradient(w, h, 3), 32, 16, options);
            Assert.Equal((32, 16), (result.Width, result.Height));
        }
    }

    [Fact]
    public void Fit_ReturnsAnImageOfTheRightSizeUnchanged()
    {
        var image = TestData.Gradient(32, 16);
        Assert.Same(image, VbmResize.Fit(image, 32, 16, new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre)));
    }

    [Fact]
    public void Fit_KeepAspectPadsWithTransparentPixels()
    {
        var result = VbmResize.Fit(TestData.Flat(64, 16, 10, 20, 30), 32, 16, new VbmResizeOptions(VbmResizeFilter.HighQuality, VbmFitMode.KeepAspect));
        for (int y = 0; y < 16; y++)
        {
            var p = result.Get(16, y);
            if (y is < 4 or >= 12) Assert.Equal((0, 0, 0, 0), p);
            else Assert.Equal((10, 20, 30, 255), p);
        }
    }

    [Fact]
    public void Fit_CropCentreKeepsTheMiddle()
    {
        // columns coloured by x: the middle 16 of 48 columns survive
        var image = new BgraImage(48, 16);
        for (int y = 0; y < 16; y++) for (int x = 0; x < 48; x++) image.Set(x, y, (byte)x, 0, 0, 255);
        var result = VbmResize.Fit(image, 16, 16, new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre));
        for (int x = 0; x < 16; x++) Assert.Equal(16 + x, result.Get(x, 5).B);
    }

    [Fact]
    public void Nearest_RepeatsPixelsWhenEnlarging()
    {
        var image = new BgraImage(2, 2);
        image.Set(0, 0, 1, 1, 1, 255); image.Set(1, 0, 2, 2, 2, 255); image.Set(0, 1, 3, 3, 3, 255); image.Set(1, 1, 4, 4, 4, 255);
        var big = VbmResize.Resize(image, 6, 4, VbmResizeFilter.Nearest);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 6; x++)
                Assert.Equal(1 + x / 3 + (y / 2) * 2, big.Get(x, y).B);
    }

    [Fact]
    public void Nearest_PicksPixelCentresWhenShrinking()
    {
        var image = new BgraImage(8, 1);
        for (int x = 0; x < 8; x++) image.Set(x, 0, (byte)x, 0, 0, 255);
        var small = VbmResize.Resize(image, 4, 1, VbmResizeFilter.Nearest);
        Assert.Equal([1, 3, 5, 7], Enumerable.Range(0, 4).Select(x => (int)small.Get(x, 0).B));
    }

    [Fact]
    public void SmoothFiltersBlendWhereNearestDoesNot()
    {
        var image = new BgraImage(2, 1);
        image.Set(0, 0, 0, 0, 0, 255);
        image.Set(1, 0, 255, 255, 255, 255);
        int Distinct(VbmResizeFilter f) { var r = VbmResize.Resize(image, 16, 1, f); return Enumerable.Range(0, 16).Select(x => r.Get(x, 0)).Distinct().Count(); }
        Assert.Equal(2, Distinct(VbmResizeFilter.Nearest));
        Assert.True(Distinct(VbmResizeFilter.Bilinear) > 2);
        Assert.True(Distinct(VbmResizeFilter.HighQuality) > 2);
    }

    [Fact]
    public void Crop_CopiesTheRectangleAndRejectsOutOfRange()
    {
        var image = TestData.Gradient(10, 6, 2);
        var part = VbmResize.Crop(image, 3, 2, 4, 3);
        Assert.Equal((4, 3), (part.Width, part.Height));
        Assert.Equal(image.Get(3, 2), part.Get(0, 0));
        Assert.Equal(image.Get(6, 4), part.Get(3, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => VbmResize.Crop(image, 8, 0, 4, 1));
    }

    [Theory]
    [InlineData(256, 256, 5)]
    [InlineData(64, 64, 3)]
    [InlineData(256, 32, 2)]
    [InlineData(31, 31, 1)]
    [InlineData(16, 16, 1)]
    [InlineData(1, 1, 1)]
    public void DefaultMipLevels_HalveDownTo16Pixels(int w, int h, int levels) => Assert.Equal(levels, VbmResize.DefaultMipLevels(w, h));

    [Fact]
    public void Editing_UsesTheResizeOptions()
    {
        var file = VbmEditing.Create([TestData.Gradient(32, 16)], VbmPixelFormat.Argb4444, 15, 3);
        var replaced = VbmEditing.ReplaceFrame(file, 0, TestData.Flat(64, 16, 255, 255, 255), new VbmResizeOptions(VbmResizeFilter.Bilinear, VbmFitMode.KeepAspect));
        var frame = replaced.Decode(0);
        Assert.Equal(0, frame.Get(10, 1).A);
        Assert.Equal(255, frame.Get(10, 8).A);
        Assert.Equal(3, replaced.Frames[0].Levels.Count);
        var inserted = VbmEditing.InsertFrames(file, 1, [TestData.Flat(16, 64, 0, 0, 0)], new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre));
        Assert.Equal(255, inserted.Decode(1).Get(0, 0).A);
        var created = VbmEditing.Create([TestData.Flat(8, 8, 1, 1, 1), TestData.Flat(16, 8, 1, 1, 1)], VbmPixelFormat.Argb4444, 15, 1, 0, 0,
            new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.KeepAspect));
        Assert.Equal(0, created.Decode(1).Get(0, 0).A);
    }
}
