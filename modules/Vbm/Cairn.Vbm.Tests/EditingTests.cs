using Cairn.Formats.Imaging;

namespace Cairn.Vbm.Tests;

public sealed class EditingTests
{
    private static VbmFile Sample(int frames = 4, VbmPixelFormat format = VbmPixelFormat.Argb4444, int mips = 3) =>
        VbmEditing.Create([.. Enumerable.Range(0, frames).Select(i => TestData.Gradient(16, 16, i))], format, 15, mips);

    [Fact]
    public void Create_EncodesEveryImageAtTheFirstImagesSize()
    {
        var file = VbmEditing.Create([TestData.Gradient(32, 16), TestData.Gradient(8, 8, 1)], VbmPixelFormat.Argb1555, 20, 99);
        Assert.Equal((32, 16, 2, 20), (file.Width, file.Height, file.FrameCount, file.Fps));
        Assert.Equal(VbmFile.MaxMipLevels(32, 16), file.MipLevels);
        Assert.Equal(1u, file.Version);
        Assert.Equal(2u, VbmEditing.Create([TestData.Gradient(4, 4)], VbmPixelFormat.Rgb565, 15, 1).Version);
        file.Validate();
        var sized = VbmEditing.Create([TestData.Gradient(32, 16)], VbmPixelFormat.Rgb565, 15, 1, 64, 64);
        Assert.Equal((64, 64), (sized.Width, sized.Height));
    }

    [Fact]
    public void SuggestFormat_FollowsTheAlphaTheImagesUse()
    {
        Assert.Equal(VbmPixelFormat.Rgb565, VbmEditing.SuggestFormat([TestData.Gradient(4, 4, 0, opaque: true)]));
        var cutout = TestData.Flat(4, 4, 1, 2, 3);
        cutout.Set(1, 1, 0, 0, 0, 0);
        Assert.Equal(VbmPixelFormat.Argb1555, VbmEditing.SuggestFormat([cutout]));
        Assert.Equal(VbmPixelFormat.Argb4444, VbmEditing.SuggestFormat([cutout, TestData.Gradient(4, 4)]));
    }

    [Fact]
    public void Fps_ChangesOnlyTheHeader()
    {
        var file = Sample();
        var faster = VbmEditing.WithFps(file, 30);
        Assert.Equal(30, faster.Fps);
        Assert.Same(file.Frames, faster.Frames);
        Assert.Same(file, VbmEditing.WithFps(file, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => VbmEditing.WithFps(file, -1));
        var bytes = faster.Write();
        Assert.Equal(30, VbmCodec.ReadInfo(bytes, "x.vbm").Fps);
    }

    [Fact]
    public void ReplaceFrame_ResizesAndKeepsTheOtherFramesBytes()
    {
        var file = Sample();
        var replaced = VbmEditing.ReplaceFrame(file, 2, TestData.Flat(40, 10, 0, 0, 255));
        Assert.Equal(4, replaced.FrameCount);
        Assert.Same(file.Frames[0], replaced.Frames[0]);
        Assert.Same(file.Frames[3], replaced.Frames[3]);
        Assert.Equal(3, replaced.Frames[2].Levels.Count);
        var (b, g, r, a) = replaced.Decode(2).Get(8, 8);
        Assert.Equal((0, 0, 255, 255), (b, g, r, a));
        replaced.Validate();
    }

    [Fact]
    public void InsertRemoveDuplicateAndMove_KeepFramesWhole()
    {
        var file = Sample();
        var inserted = VbmEditing.InsertFrames(file, 1, [TestData.Flat(16, 16, 9, 9, 9), TestData.Flat(16, 16, 8, 8, 8)]);
        Assert.Equal(6, inserted.FrameCount);
        Assert.Same(file.Frames[1], inserted.Frames[3]);

        var removed = VbmEditing.RemoveFrames(inserted, [1, 2]);
        Assert.Equal(file.Frames, removed.Frames);
        Assert.Throws<InvalidOperationException>(() => VbmEditing.RemoveFrames(file, [0, 1, 2, 3]));

        var duplicated = VbmEditing.DuplicateFrames(file, [0, 2], out var copies);
        Assert.Equal([3, 4], copies);
        Assert.Same(file.Frames[0], duplicated.Frames[3]);
        Assert.Same(file.Frames[2], duplicated.Frames[4]);

        var later = VbmEditing.MoveFrames(file, [0, 1], 1, out var moved);
        Assert.Equal([1, 2], moved);
        Assert.Equal([file.Frames[2], file.Frames[0], file.Frames[1], file.Frames[3]], later.Frames);
        Assert.Same(file, VbmEditing.MoveFrames(file, [0], -1, out _));
        var earlier = VbmEditing.MoveFrames(file, [3], -1, out moved);
        Assert.Equal([2], moved);
        Assert.Same(file.Frames[3], earlier.Frames[2]);

        var reversed = VbmEditing.Reverse(file);
        Assert.Equal(file.Frames.Reverse(), reversed.Frames);
        Assert.Throws<ArgumentException>(() => VbmEditing.Reorder(file, [0, 0, 1, 2]));
    }

    [Fact]
    public void FormatAndMipChanges_RebuildFromLevelZero()
    {
        var file = Sample(2, VbmPixelFormat.Argb4444, 1);
        var converted = VbmEditing.ConvertFormat(file, VbmPixelFormat.Argb1555);
        Assert.Equal((VbmPixelFormat.Argb1555, 1u), (converted.Format, converted.Version));
        converted.Validate();
        var withMips = VbmEditing.WithMipLevels(file, 5);
        Assert.Equal(5, withMips.MipLevels);
        Assert.Equal(file.Frames[0].Levels[0], withMips.Frames[0].Levels[0]);
        var fewer = VbmEditing.WithMipLevels(withMips, 2);
        Assert.Equal(withMips.Frames[1].Levels.Take(2), fewer.Frames[1].Levels);
        fewer.Validate();
    }

    [Fact]
    public void Checks_ReportGameLimits()
    {
        var file = Sample(1) with { Fps = 0 };
        Assert.DoesNotContain(VbmChecks.Check(file, null), p => p.Code == "VBM007");
        var animated = Sample(2) with { Fps = 0 };
        Assert.Contains(VbmChecks.Check(animated, null), p => p.Code == "VBM007");
        var many = animated with { Fps = 15, Frames = [.. Enumerable.Repeat(animated.Frames[0], 256)] };
        Assert.Contains(VbmChecks.Check(many, null), p => p.Code == "VBM005");
        var odd = VbmEditing.Create([TestData.Gradient(12, 8)], VbmPixelFormat.Rgb565, 15, 1);
        Assert.Contains(VbmChecks.Check(odd, null), p => p.Code == "VBM006" && p.Severity == VbmSeverity.Info);
        var v2 = Sample(1, VbmPixelFormat.Argb1555) with { Version = 2 };
        Assert.Contains(VbmChecks.Check(v2, null), p => p.Code == "VBM010");
        Assert.Empty(VbmChecks.Check(Sample(), null));
    }

    [Fact]
    public void Facts_DescribeSizeFormatFramesAndMips()
    {
        var facts = VbmFacts.For(Sample(), 1234);
        string Value(string label) => facts.First(f => f.Label == label).Value;
        Assert.Equal("16 x 16", Value("Size"));
        Assert.Contains("4444", Value("Format"), StringComparison.Ordinal);
        Assert.Equal("4", Value("Frames"));
        Assert.Contains("15 fps", Value("Frame rate"), StringComparison.Ordinal);
        Assert.StartsWith("3 (16 x 16 down to 4 x 4)", Value("Mip levels"), StringComparison.Ordinal);
        Assert.Contains("1", Value("File size"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 0, 1 }, 4, new[] { 2, 3, 0, 1, 4 }, new[] { 2, 3 })]
    [InlineData(new[] { 0, 1 }, 5, new[] { 2, 3, 4, 0, 1 }, new[] { 3, 4 })]
    [InlineData(new[] { 3, 4 }, 0, new[] { 3, 4, 0, 1, 2 }, new[] { 0, 1 })]
    [InlineData(new[] { 0, 4 }, 2, new[] { 1, 0, 4, 2, 3 }, new[] { 1, 2 })]
    [InlineData(new[] { 2 }, 2, new[] { 0, 1, 2, 3, 4 }, new[] { 2 })]
    [InlineData(new[] { 2 }, 3, new[] { 0, 1, 2, 3, 4 }, new[] { 2 })]
    public void MoveFramesTo_DropsTheBlockBeforeTheTarget(int[] pick, int before, int[] expected, int[] moved)
    {
        var file = Sample(5);
        var result = VbmEditing.MoveFramesTo(file, pick, before, out var positions);
        Assert.Equal(expected, result.Frames.Select(f => file.Frames.ToList().IndexOf(f)));
        Assert.Equal(moved, positions);
        if (expected.SequenceEqual([0, 1, 2, 3, 4])) Assert.Same(file, result);
        Assert.Throws<ArgumentOutOfRangeException>(() => VbmEditing.MoveFramesTo(file, pick, 6, out _));
    }

    [Fact]
    public void InsertFramesFrom_CopiesExactFramesWhenTheLayoutMatches()
    {
        var source = Sample(3);
        var target = VbmEditing.Create([TestData.Gradient(16, 16, 9)], VbmPixelFormat.Argb4444, 15, 3);
        Assert.True(VbmEditing.SameLayout(source, target));
        var result = VbmEditing.InsertFramesFrom(target, 1, source, [2, 0]);
        Assert.Equal(3, result.FrameCount);
        Assert.Same(source.Frames[2], result.Frames[1]);
        Assert.Same(source.Frames[0], result.Frames[2]);
        Assert.Same(target, VbmEditing.InsertFramesFrom(target, 0, source, [7]));
    }

    [Fact]
    public void InsertFramesFrom_ConvertsAndFitsOtherLayouts()
    {
        var source = Sample(2); // 16 x 16 4444, 3 mips
        var target = VbmEditing.Create([TestData.Gradient(8, 4, opaque: true)], VbmPixelFormat.Rgb565, 15, 1);
        Assert.False(VbmEditing.SameLayout(source, target));
        var result = VbmEditing.InsertFramesFrom(target, 0, source, [1], new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.Stretch));
        Assert.Equal(2, result.FrameCount);
        Assert.Single(result.Frames[0].Levels);
        Assert.Equal(8 * 4 * 2, result.Frames[0].Levels[0].Length);
        result.Validate();
    }
}
