using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.Linting;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.SampleGen;

namespace Cairn.Atx.Tests;

/// <summary>
/// Checks the generated sample set. The committed files under samples/ come from
/// <c>dotnet run --project tools/Cairn.Atx.SampleGen -- samples</c>; these tests regenerate them
/// into a scratch folder so they pass whether or not the repository is present.
/// </summary>
public class SampleTests
{
    private static TempFolder Generate()
    {
        var temp = new TempFolder();
        SampleSet.WriteAll(temp.Path);
        return temp;
    }

    [Fact]
    public void TheCleanSampleHasNoProblemsAtAll()
    {
        using var temp = Generate();
        string text = File.ReadAllText(temp.File("hazard_strip.atx"));
        var parse = AtxParser.Parse(text, "hazard_strip.atx");

        Assert.Empty(AtxLinter.Analyze(parse,
            new LintOptions { DocumentPath = temp.File("hazard_strip.atx") }));

        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });
        Assert.Empty(AtxAssetLinter.Analyze(parse, resolver).Diagnostics);

        Assert.Equal(SampleSet.HazardFrames, parse.Model!.Frames.Count);
        Assert.Equal(80, parse.Model.Header.EffectiveFrameTimeMs);
        Assert.Equal(AtxAnimationMode.Loop, parse.Model.Header.EffectiveAnimationMode);
    }

    [Fact]
    public void TheBrokenSampleTripsAWideSpreadOfRules()
    {
        using var temp = Generate();
        string path = temp.File("broken_example.atx");
        var parse = AtxParser.Parse(File.ReadAllText(path), "broken_example.atx");
        var resolver = new AssetResolver(new AssetResolverOptions { DocumentFolder = temp.Path });

        var codes = AtxLinter.Analyze(parse, new LintOptions { DocumentPath = path })
            .Concat(AtxAssetLinter.Analyze(parse, resolver).Diagnostics)
            .Select(d => d.Code)
            .Distinct()
            .ToHashSet();

        foreach (string expected in new[]
        {
            AtxRules.NestedAtx, AtxRules.UnknownFormat, AtxRules.UnknownMaterial,
            AtxRules.FrameImageNotFound, AtxRules.FrameMismatch, AtxRules.WrongType,
            AtxRules.FrameTimeTooSmall, AtxRules.AnimationModeOutOfRange, AtxRules.UnknownKey,
            AtxRules.PathSeparator, AtxRules.NameTooLong, AtxRules.InitiallyOnWithStatic,
            AtxRules.EmptyString,
        })
        {
            Assert.Contains(expected, codes);
        }
        Assert.DoesNotContain(AtxRules.Syntax, codes);
    }

    [Fact]
    public void EveryHazardFrameIsA64By64TwentyFourBitTga()
    {
        using var temp = Generate();
        for (int i = 0; i < SampleSet.HazardFrames; i++)
        {
            var info = ImageProbe.ProbeFile(temp.File($"hazard_strip_{i:00}.tga"));
            Assert.Equal(ImageContainer.Tga, info.Container);
            Assert.Equal(64, info.Width);
            Assert.Equal(64, info.Height);
            Assert.Equal(EngineFormat.Rgb888, info.Format);
        }
    }

    [Fact]
    public void ConsecutiveHazardFramesActuallyDiffer()
    {
        using var temp = Generate();
        var first = ImageDecoder.DecodeFile(temp.File("hazard_strip_00.tga"));
        for (int i = 1; i < SampleSet.HazardFrames; i++)
        {
            var next = ImageDecoder.DecodeFile(temp.File($"hazard_strip_{i:00}.tga"));
            Assert.NotEqual(first.Pixels, next.Pixels);
        }
    }

    [Fact]
    public void TheMaskIsEightBitGreyscaleAndTheSameSizeAsTheFrames()
    {
        using var temp = Generate();
        var info = ImageProbe.ProbeFile(temp.File("hazard_strip_mask.tga"));
        Assert.Equal(EngineFormat.Paletted8, info.Format);
        Assert.Equal(64, info.Width);
        Assert.Equal(64, info.Height);

        var mask = ImageDecoder.DecodeFile(temp.File("hazard_strip_mask.tga"));
        Assert.Equal((byte)255, mask.Get(32, 32).R); // opaque in the middle
        Assert.Equal((byte)0, mask.Get(0, 0).R);     // transparent in the corner
    }

    [Fact]
    public void TheDxtPairIsAMatchingCompressedPair()
    {
        using var temp = Generate();
        var first = ImageProbe.ProbeFile(temp.File("dxt_pulse_00.dds"));
        var second = ImageProbe.ProbeFile(temp.File("dxt_pulse_01.dds"));
        Assert.Equal(EngineFormat.Dxt1, first.Format);
        Assert.Equal(first with { Note = second.Note }, second);
        Assert.NotEqual(
            ImageDecoder.DecodeFile(temp.File("dxt_pulse_00.dds")).Pixels,
            ImageDecoder.DecodeFile(temp.File("dxt_pulse_01.dds")).Pixels);
    }

    [Fact]
    public void TheMismatchedSampleIsDeliberatelyTheWrongSize()
    {
        using var temp = Generate();
        var info = ImageProbe.ProbeFile(temp.File("wrong_size_32.tga"));
        Assert.Equal(32, info.Width);
        Assert.Equal(32, info.Height);
    }

    [Fact]
    public void EverySampleFrameNameFitsTheEnginesBuffer()
    {
        using var temp = Generate();
        foreach (string path in Directory.EnumerateFiles(temp.Path))
        {
            Assert.True(Path.GetFileName(path).Length <= AtxSchema.MaxBitmapNameLength,
                $"{Path.GetFileName(path)} is too long");
        }
    }

    [Fact]
    public void TheCommittedSamplesMatchWhatTheGeneratorProduces()
    {
        string? committed = TestPaths.Samples();
        if (committed is null || !Directory.Exists(committed)) return;

        using var temp = Generate();
        foreach (string path in Directory.EnumerateFiles(temp.Path))
        {
            string name = Path.GetFileName(path);
            string other = Path.Combine(committed, name);
            Assert.True(File.Exists(other), $"samples/atx/{name} is missing — re-run the sample generator");
            Assert.Equal(File.ReadAllBytes(path), File.ReadAllBytes(other));
        }
    }
}
