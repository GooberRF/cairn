using Cairn.Formats.Imaging;

namespace Cairn.Vbm.Tests;

public sealed class ExportTests
{
    [Fact]
    public void Plan_NumbersFilesByFramePosition()
    {
        var plan = VbmFrameExport.Plan(@"C:\out", "flame", [7, 2, 2, 99], 12, VbmExportFormat.Tga);
        Assert.Equal([Path.Combine(@"C:\out", "flame_02.tga"), Path.Combine(@"C:\out", "flame_07.tga")], plan.Select(p => p.Path));
        Assert.Equal(3, VbmFrameExport.PadWidth(101));
        Assert.Equal(2, VbmFrameExport.PadWidth(1));
        Assert.Equal("a_b", VbmFrameExport.DefaultBaseName("a|b.vbm"));
        Assert.Equal("frame", VbmFrameExport.DefaultBaseName(".vbm"));
        Assert.EndsWith("_0.png", VbmFrameExport.Plan(@"C:\out", "x", [0], 1, VbmExportFormat.Png)[0].Path.Replace("_00", "_0", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(VbmExportFormat.Tga)]
    [InlineData(VbmExportFormat.Png)]
    public void Write_ProducesImagesThatDecodeToTheFrames(VbmExportFormat format)
    {
        var file = VbmEditing.Create([TestData.Gradient(16, 8, 0), TestData.Gradient(16, 8, 1), TestData.Gradient(16, 8, 2)], VbmPixelFormat.Argb4444, 10, 2);
        string folder = TestData.TempFolder();
        try
        {
            var plan = VbmFrameExport.Plan(Path.Combine(folder, "sub"), "glow", [0, 2], file.FrameCount, format);
            Assert.Equal(3, file.FrameCount);
            Assert.Equal(2, plan.Count);
            Assert.Empty(VbmFrameExport.Existing(plan));
            Assert.Equal(2, VbmFrameExport.Write(file, plan, format));
            Assert.Equal(2, VbmFrameExport.Existing(plan).Count);
            foreach (var item in plan)
            {
                var decoded = ImageDecoder.DecodeFile(item.Path);
                Assert.Equal(file.Decode(item.Frame).Pixels, decoded.Pixels);
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void OpaqueBitmaps_ExportAsTwentyFourBitTgas()
    {
        var file = VbmEditing.Create([TestData.Gradient(4, 4, 0, opaque: true)], VbmPixelFormat.Rgb565, 15, 1);
        byte[] tga = VbmFrameExport.Encode(file, 0, VbmExportFormat.Tga);
        Assert.Equal(24, tga[16]);
        byte[] alpha = VbmFrameExport.Encode(VbmEditing.ConvertFormat(file, VbmPixelFormat.Argb1555), 0, VbmExportFormat.Tga);
        Assert.Equal(32, alpha[16]);
    }
}
