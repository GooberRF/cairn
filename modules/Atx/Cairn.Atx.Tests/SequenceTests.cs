using Cairn.Atx.Sequences;

namespace Cairn.Atx.Tests;

public class SequenceTests
{
    [Theory]
    [InlineData("hazard_strip_07.tga", "hazard_strip_", 7, 2, ".tga")]
    [InlineData("frame1.png", "frame", 1, 1, ".png")]
    [InlineData("shot_0000123.dds", "shot_", 123, 7, ".dds")]
    [InlineData("noext_12", "noext_", 12, 2, "")]
    public void PatternsAreSplitAtTheTrailingDigitGroup(
        string name, string prefix, int number, int padding, string extension)
    {
        var pattern = FrameSequence.DetectPattern(name);
        Assert.NotNull(pattern);
        Assert.Equal(prefix, pattern!.Prefix);
        Assert.Equal(number, pattern.Number);
        Assert.Equal(padding, pattern.Padding);
        Assert.Equal(extension, pattern.Extension);
    }

    [Theory]
    [InlineData("texture.tga")]
    [InlineData("12_leading.tga")]
    [InlineData("")]
    public void NonNumberedNamesHaveNoPattern(string name) =>
        Assert.Null(FrameSequence.DetectPattern(name));

    [Fact]
    public void PatternsRebuildNamesWithTheSamePadding()
    {
        var pattern = FrameSequence.DetectPattern("hazard_strip_07.tga")!;
        Assert.Equal("hazard_strip_12.tga", pattern.NameFor(12));
        Assert.Equal("hazard_strip_123.tga", pattern.NameFor(123));
        Assert.Equal("hazard_strip_7.tga", pattern.NameForUnpadded(7));
    }

    [Fact]
    public void RunsOnDiskAreDetectedInNaturalOrder()
    {
        using var temp = new TempFolder();
        foreach (string name in new[] { "f_1.tga", "f_2.tga", "f_10.tga", "f_09.tga", "other_1.tga", "f_1.png" })
        {
            temp.Write(name, "x");
        }
        var run = FrameSequence.DetectOnDisk(temp.File("f_2.tga"));
        Assert.Equal(["f_1.tga", "f_2.tga", "f_09.tga", "f_10.tga"], run);
    }

    /// <summary>
    /// The Add Sequence dialog's whole premise: point at one file of the shipped sample run and get
    /// the other seven back, in order, without the mask or the odd-sized file tagging along.
    /// </summary>
    [Fact]
    public void TheSampleHazardStripIsDetectedAsEightFrames()
    {
        string? first = TestPaths.Samples("hazard_strip_00.tga");
        if (first is null || !File.Exists(first)) return;

        var run = FrameSequence.DetectOnDisk(first);
        Assert.Equal(
        [
            "hazard_strip_00.tga", "hazard_strip_01.tga", "hazard_strip_02.tga", "hazard_strip_03.tga",
            "hazard_strip_04.tga", "hazard_strip_05.tga", "hazard_strip_06.tga", "hazard_strip_07.tga",
        ], run);
        Assert.DoesNotContain("hazard_strip_mask.tga", run);
    }

    [Fact]
    public void ADetachedFileIsItsOwnRun()
    {
        using var temp = new TempFolder();
        temp.Write("single.tga", "x");
        Assert.Equal(["single.tga"], FrameSequence.DetectOnDisk(temp.File("single.tga")));
    }

    [Fact]
    public void GeneratedNamesFollowTheRequestedRangeAndPadding()
    {
        Assert.Equal(["a_00.tga", "a_01.tga", "a_02.tga"],
            FrameSequence.Generate("a_", 0, 2, 2, ".tga"));
        Assert.Equal(["a_0.tga", "a_2.tga", "a_4.tga"],
            FrameSequence.Generate("a_", 0, 5, 0, "tga", step: 2));
        Assert.Equal(["a_3.tga", "a_2.tga", "a_1.tga"],
            FrameSequence.Generate("a_", 3, 1, 0, ".tga"));
    }

    [Fact]
    public void ReversedTailMakesAManualPingPong()
    {
        Assert.Equal(["a", "b", "c", "d", "c", "b"],
            FrameSequence.WithReversedTail(["a", "b", "c", "d"]));
        Assert.Equal(["a", "b"], FrameSequence.WithReversedTail(["a", "b"]));
    }

    [Theory]
    [InlineData("f_2.tga", "f_10.tga", -1)]
    [InlineData("f_10.tga", "f_2.tga", 1)]
    [InlineData("A.tga", "a.tga", 0)]
    [InlineData("f_02.tga", "f_2.tga", 1)]
    public void NaturalOrderSortsNumbersNumerically(string a, string b, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(a, b)));
    }
}
