using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Tests;

public class MorphSamplerTests
{
    private static RfaClip V7(int start, int end, int keyframes, params float[] xs) => new()
    {
        Version = 7,
        StartTime = start,
        EndTime = end,
        Morph = new RfaMorph([0], keyframes, [], null, [], [.. xs.Select(x => new Vector3(x, 0, 0))]),
    };

    [Fact]
    public void Version7SpreadsKeyframesOverTheDurationDividedByTheirCount()
    {
        // 4 keyframes over 160..800: keyframe k at 160 + k * 160, the last held to the end.
        var clip = V7(160, 800, 4, 0f, 1f, 2f, 3f);
        Assert.Equal(new MorphFrame(0, 1, 0f), MorphSampler.Locate(clip, 160));
        Assert.Equal(0.5f, MorphSampler.Sample(clip, 240)[0].X, 5);
        Assert.Equal(1f, MorphSampler.Sample(clip, 320)[0].X, 5);
        Assert.Equal(2.25f, MorphSampler.Sample(clip, 520)[0].X, 5);
        Assert.Equal(new MorphFrame(3, 3, 0f), MorphSampler.Locate(clip, 640));
        Assert.Equal(3f, MorphSampler.Sample(clip, 700)[0].X);
        Assert.Equal(3f, MorphSampler.Sample(clip, 800)[0].X);    // the engine would read past the data here
        Assert.Equal(0f, MorphSampler.Sample(clip, -50)[0].X);
    }

    [Fact]
    public void Version8UsesItsTimesClampedAndLinear()
    {
        var clip = new RfaClip
        {
            Version = 8,
            StartTime = 160,
            EndTime = 2000,
            Morph = new RfaMorph([0], 3, [400, 600, 1400], new RfaMorphBounds(Vector3.Zero, new Vector3(255, 255, 255)),
                [0, 0, 0, 100, 0, 0, 200, 0, 0], []),
        };
        Assert.Equal(0f, MorphSampler.Sample(clip, 160)[0].X, 4);
        Assert.Equal(50f, MorphSampler.Sample(clip, 500)[0].X, 3);
        Assert.Equal(150f, MorphSampler.Sample(clip, 1000)[0].X, 3);
        Assert.Equal(200f, MorphSampler.Sample(clip, 1900)[0].X, 3);
        Assert.Equal(new MorphFrame(2, 2, 0f), MorphSampler.Locate(clip, 1400));
    }

    [Fact]
    public void NoMorphDataMeansNothingToApply()
    {
        var clip = new RfaClip();
        Assert.False(MorphSampler.HasMorph(clip));
        Assert.Null(MorphSampler.Locate(clip, 0));
        Assert.Empty(MorphSampler.Sample(clip, 0));
        // A v8 clip with keyframe times but no vertices (one stock file) does not morph either.
        Assert.False(MorphSampler.HasMorph(clip with { Morph = new RfaMorph([], 61, [.. new int[61]], null, [], []) }));
    }

    [Fact]
    public void ApplyGoesThroughTheMorphMapThenCopiesSamePositionDuplicates()
    {
        var batch = new V3dBatch
        {
            Positions = [Vector3.Zero, Vector3.One, new Vector3(2), new Vector3(3)],
            SamePositionOffsets = [0, 0, 0, 2],      // vertex 3 duplicates vertex 1
            MorphMap = [1, -1, 0],                   // clip index 0 -> vertex 1, 1 -> skipped, 2 -> vertex 0
        };
        ReadOnlySpan<short> indices = [0, 1, 2];
        ReadOnlySpan<Vector3> morphed = [new Vector3(10), new Vector3(20), new Vector3(30)];
        var output = new Vector3[4];
        MorphSampler.ApplyToBatch(batch, indices, morphed, useMorphMap: true, output);
        Assert.Equal([new Vector3(30), new Vector3(10), new Vector3(2), new Vector3(10)], output);

        // Without the map the clip index is the batch vertex.
        MorphSampler.ApplyToBatch(batch, indices, morphed, useMorphMap: false, output);
        Assert.Equal([new Vector3(10), new Vector3(20), new Vector3(30), new Vector3(20)], output);
    }

    [Theory]
    [InlineData("NURS_talk.rfa", "nurse1.v3c", 0f)]
    [InlineData("NURS_talk_short.rfa", "nurse1.v3c", 0f)]
    [InlineData("masa_talk.rfa", "masako.v3c", 1f)]
    [InlineData("eos_talk.rfa", "eos.v3c", 1f)]
    [InlineData("tech01_talk.rfa", "tech01.v3c", 2f)]   // one vertex is 1.03 steps off
    public void TheFirstKeyframeReproducesTheMeshItWasMadeFor(string clipName, string meshName, float quantisationSteps)
    {
        string? clipPath = TestPaths.CorpusFile(clipName);
        string? meshPath = TestPaths.CorpusFile(meshName);
        if (clipPath is null || meshPath is null) return;
        var clip = RfaReader.ReadFile(clipPath);
        var mesh = V3dReader.ReadFile(meshPath);
        var lod = mesh.Submeshes.First().Lods[0];
        float time = clip.Version >= 8 ? clip.Morph.KeyframeTimes[0] : clip.StartTime;
        var batches = MorphSampler.ApplyToLod(lod, clip, time);
        Assert.NotNull(batches);

        var step = clip.Morph.Bounds is { } box ? (box.Max - box.Min) / 255f : Vector3.Zero;
        float tolerance = quantisationSteps * MathF.Max(step.X, MathF.Max(step.Y, step.Z)) + 1e-6f;
        int changedVertices = 0;
        for (int b = 0; b < lod.Batches.Length; b++)
        {
            var original = lod.Batches[b].Positions;
            for (int v = 0; v < original.Length; v++)
            {
                float d = Vector3.Distance(original[v], batches![b][v]);
                Assert.True(d <= tolerance * 1.7321f, $"{clipName}: batch {b} vertex {v} moved {d} (allowed {tolerance} per axis)");
                if (d > 0f) changedVertices++;
            }
        }
        if (quantisationSteps == 0f) Assert.Equal(0, changedVertices);
    }

    [Fact]
    public void EveryStockMorphClipAppliesWithinItsMeshCapacity()
    {
        if (TestPaths.Corpus is null) return;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa"))
        {
            var clip = RfaReader.ReadFile(path);
            if (!MorphSampler.HasMorph(clip)) continue;
            var positions = MorphSampler.Sample(clip, (clip.StartTime + clip.EndTime) / 2f);
            Assert.Equal(clip.Morph.VertexCount, positions.Length);
            Assert.All(positions, p => Assert.True(float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)));
        }
    }
}
