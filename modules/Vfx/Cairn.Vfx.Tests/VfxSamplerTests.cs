using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxSamplerTests(ITestOutputHelper output)
{
    private const float Eps = 1e-5f;

    private static List<(string Name, VfxFile File)>? StockFiles()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return null;
        return [.. Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => (Path.GetFileName(f), VfxReader.Read(File.ReadAllBytes(f), Path.GetFileName(f))))];
    }

    [Fact]
    public void BezierEaseAndSlerpMatchHandValues()
    {
        Assert.Equal(1.5f, VfxKeyframeMath.Bezier(Vector3.Zero, Vector3.UnitX, 2 * Vector3.UnitX, 3 * Vector3.UnitX, 0.5f).X, Eps);
        Assert.Equal(0.375f, VfxKeyframeMath.Bezier(Vector3.Zero, Vector3.UnitY, Vector3.Zero, Vector3.Zero, 0.5f).Y, Eps);

        Assert.Equal(0.5f, VfxKeyframeMath.Ease(0.5f, 0, 0), Eps);
        Assert.Equal(0.125f, VfxKeyframeMath.Ease(0.25f, 0.5f, 0.5f), Eps);
        Assert.Equal(0.125f, VfxKeyframeMath.Ease(0.25f, 1, 1), Eps); // normalised by the sum
        Assert.Equal(0.03125f, VfxKeyframeMath.Ease(0.1f, 0.2f, 0.2f), Eps);
        Assert.Equal(0.5f, VfxKeyframeMath.Ease(0.5f, 0.2f, 0.2f), Eps);
        Assert.Equal(0.96875f, VfxKeyframeMath.Ease(0.9f, 0.2f, 0.2f), Eps);

        var q90 = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        var half = VfxKeyframeMath.Slerp(Quaternion.Identity, q90, 0.5f);
        AssertQuat(new Quaternion(0, 0.38268343f, 0, 0.92387953f), half);
        AssertQuat(half, VfxKeyframeMath.Slerp(Quaternion.Identity, Quaternion.Negate(q90), 0.5f)); // shortest path

        Assert.Equal(8191 / 16383f, VfxKeyframeMath.Quantise(new Quaternion(0.5f, 0, 0, 0)).X, 1e-7f);

        Assert.True(VfxKeyframeMath.FindSegment([0, 320, 960], 640, out int n0, out int n1, out float u));
        Assert.Equal((1, 2, 0.5f), (n0, n1, u));
        VfxKeyframeMath.FindSegment([0, 320, 960], -5, out n0, out n1, out u);
        Assert.Equal((0, 1, 0f), (n0, n1, u));
        VfxKeyframeMath.FindSegment([0, 320, 960], 1000, out n0, out n1, out u);
        Assert.Equal((1, 2, 1f), (n0, n1, u));

        ImmutableArray<VfxRotationKey> rot = [new(0, Quaternion.Identity, 0, 0, 0, 0, 0.2f), new(320, q90, 0, 0, 0, 0.2f, 0)];
        AssertQuat(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2 * 0.03125f), VfxKeyframeMath.EvaluateRotation(rot, 32));
        ImmutableArray<VfxVectorKey> tr = [new(0, Vector3.Zero, Vector3.Zero, Vector3.UnitX), new(320, 3 * Vector3.UnitX, 2 * Vector3.UnitX, Vector3.Zero)];
        Assert.Equal(1.5f, VfxKeyframeMath.EvaluateVector(tr, 160, Vector3.Zero).X, Eps);
        Assert.Equal(3f, VfxKeyframeMath.EvaluateVector(tr, 9999, Vector3.Zero).X, Eps);
    }

    [Fact]
    public void PlaybackModesMirrorTheEngine()
    {
        Assert.Equal(new VfxPlaybackState(15, true, true, 1), VfxPlayback.Evaluate(VfxPlaybackMode.Loop, 3, 30));
        Assert.Equal(new VfxPlaybackState(30, false, false, 0), VfxPlayback.Evaluate(VfxPlaybackMode.OneShot, 3, 30));
        Assert.Equal(new VfxPlaybackState(30, true, false, 0), VfxPlayback.Evaluate(VfxPlaybackMode.HoldLastFrame, 3, 30));
        Assert.Equal(new VfxPlaybackState(15, true, true, 0), VfxPlayback.Evaluate(VfxPlaybackMode.OneShot, 1, 30));
        Assert.NotEmpty(VfxEngineNotes.All);
    }

    [Fact]
    public void SamplerInvariantsHoldOnStockFiles()
    {
        if (StockFiles() is not { } files) return;
        int morphHits = 0, meshes = 0;
        var s = new VfxMeshSample();
        foreach (var (name, file) in files)
        {
            var sampler = new VfxSampler(file, _ => new VfxBitmapInfo(8, 15));
            for (int m = 0; m < sampler.Meshes.Count; m++, meshes++)
            {
                var v = sampler.Meshes[m];
                if (v.FrameCount == 0) continue;
                float start = v.StartSeconds * 15;
                Assert.False(sampler.SampleMesh(m, start - 1, s), $"{name}/{v.Mesh.Name} before start");
                Assert.False(sampler.SampleMesh(m, (v.StartSeconds + (v.FrameCount + 1f) / v.Fps) * 15, s), $"{name}/{v.Mesh.Name} after end");
                for (float f = -3; f <= file.EndFrame + 3; f += 0.37f)
                {
                    if (!sampler.SampleMesh(m, f, s, normals: true)) continue;
                    Assert.True(s.Positions.All(Finite) && s.Normals.All(Finite) && Finite(s.Center), $"{name}/{v.Mesh.Name} NaN at {f}");
                    Assert.True(s.Opacity is null or (>= 0 and <= 1), $"{name}/{v.Mesh.Name} opacity {s.Opacity}");
                    Assert.True(float.IsFinite(s.Width) && float.IsFinite(s.Height));
                }
                if (v.IsKeyframed || !v.Mesh.IsMorph) continue;
                for (int k = 0; k < v.FrameCount; k++)
                {
                    Assert.True(sampler.SampleMesh(m, (v.StartSeconds + k / (float)v.Fps) * 15, s));
                    if (s.Fraction != 0) continue;
                    Assert.Equal(v.Mesh.DecodePositions(s.Frame0) ?? v.Positions[s.Frame0], s.Positions);
                    morphHits++;
                }
            }
            for (int i = 0; i < sampler.Materials.Count; i++)
                for (float f = 0; f <= file.EndFrame; f += 0.5f)
                {
                    var ms = sampler.SampleMaterial(i, f);
                    Assert.True(ms.Opacity is >= 0 and <= 1 && ms.SelfIllumination is >= 0 and <= 1 && ms.Mix is >= 0 and <= 1, $"{name} material {i}");
                    Assert.True(ms.TextureFrame0 < 8 && ms.TextureFrame1 < 8);
                }
            for (float f = 0; f <= file.EndFrame; f += 0.5f)
            {
                for (int i = 0; i < sampler.Dummies.Count; i++) Assert.True(Finite(sampler.SampleDummy(i, f).Position));
                for (int i = 0; i < sampler.Lights.Count; i++) Assert.True(Finite(sampler.SampleLight(i, f).Color));
                for (int i = 0; i < sampler.Spacewarps.Count; i++) Assert.True(sampler.SampleSpacewarp(i, f) is not { } w || Finite(w.Position));
                for (int i = 0; i < sampler.ParticleSystems.Count; i++) Assert.True(sampler.SampleEmitter(i, f).Opacity is >= 0 and <= 1);
            }
        }
        output.WriteLine($"{files.Count} files, {meshes} meshes, {morphHits} exact morph frames");
        Assert.True(morphHits > 0);
    }

    [Fact]
    public void SimulatorRespectsCapOnStockFiles()
    {
        if (StockFiles() is not { } files) return;
        int systems = 0, peak = 0, approximate = 0;
        foreach (var (name, file) in files)
        {
            var sampler = new VfxSampler(file);
            for (int i = 0; i < sampler.ParticleSystems.Count; i++, systems++)
            {
                var sim = new VfxParticleSimulator(sampler, i, seed: 7);
                if (sim.IsApproximate) approximate++;
                for (float f = 0; f <= Math.Max(30, file.EndFrame * 2); f += 1.3f)
                {
                    sim.Advance(f);
                    Assert.True(sim.Particles.Length <= sim.Capacity, $"{name} psys {i}");
                    foreach (var p in sim.Particles)
                        Assert.True(Finite(p.Position) && p.Alpha is >= 0 and <= 1 && float.IsFinite(p.Size), $"{name} psys {i} at {f}");
                    peak = Math.Max(peak, sim.Particles.Length);
                }
            }
        }
        output.WriteLine($"{systems} particle systems, peak {peak} live, {approximate} approximate");
        Assert.True(systems == 0 || peak > 0);
    }

    [Fact]
    public void SimulatorIsDeterministicAndScrubbable()
    {
        var sampler = new VfxSampler(SyntheticEmitter(cap: 40, emitterType: 0));
        var fresh = Run(sampler, 7, 37.5f);
        Assert.NotEmpty(fresh);
        Assert.Equal(fresh, Run(sampler, 7, 37.5f));

        var scrub = new VfxParticleSimulator(sampler, 0, seed: 7);
        for (float f = 0; f <= 60; f += 0.7f) scrub.Advance(f);
        scrub.Advance(12);
        scrub.Advance(37.5f);
        Assert.Equal(fresh, scrub.Particles.ToArray());
        Assert.NotEqual(fresh, Run(sampler, 8, 37.5f));

        var capped = new VfxParticleSimulator(new VfxSampler(SyntheticEmitter(cap: 5, emitterType: 1)), 0);
        int peak = 0;
        for (float f = 0; f < 40; f += 0.5f) { capped.Advance(f); Assert.True(capped.Particles.Length <= 5); peak = Math.Max(peak, capped.Particles.Length); }
        Assert.Equal(5, peak); // births beyond the cap were dropped

        static VfxParticle[] Run(VfxSampler s, int seed, float frame)
        {
            var sim = new VfxParticleSimulator(s, 0, seed);
            sim.Advance(frame);
            return sim.Particles.ToArray();
        }
    }

    private static VfxFile SyntheticEmitter(int cap, int emitterType)
    {
        var frames = Enumerable.Range(0, 30).Select(i => new VfxParticleFrame(new Vector3(i * 0.1f, 0, 0), Quaternion.Identity,
            1, 1, 0.2f, 3, 0.5f, 0.002f, 1f)).ToImmutableArray();
        var psys = new VfxParticleSystem("p", "", 0, 2u | 0x10u, [], 0, null, null, cap, 0, 4800, 0.25f, emitterType, null,
            new Vector2(0.1f, 0.8f), null, null, new Vector2(0, 0.5f), null, null, frames);
        return new VfxFile(VfxVersion.Current, 0, 30, null, 0, [psys]);
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static void AssertQuat(Quaternion expected, Quaternion actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-4f); Assert.Equal(expected.Y, actual.Y, 1e-4f);
        Assert.Equal(expected.Z, actual.Z, 1e-4f); Assert.Equal(expected.W, actual.W, 1e-4f);
    }
}
