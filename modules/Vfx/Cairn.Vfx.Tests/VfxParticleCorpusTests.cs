using System.Collections.Immutable;
using System.Numerics;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Workspace;
using Xunit;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>Pins the particle simulator's units against the engine's particle functions.</summary>
public sealed class VfxParticleCorpusTests(ITestOutputHelper output)
{
    // 1/128 per tick = 320/128 = 2.5 births per 15 fps frame, exactly representable
    private static VfxFile Emitter(uint flags, int start = 0, int frames = 30, int end = 60) =>
        new(VfxVersion.Current, 0, end, null, 0,
        [
            new VfxParticleSystem("p", "", 0, flags, [], 0, null, null, 1000, start, 48000, 0f, 0, null, null, null, null, null, null, null,
                [.. Enumerable.Repeat(new VfxParticleFrame(Vector3.Zero, Quaternion.Identity, 0, 0, 0.1f, 3, 0, 1f / 128, 1f), frames)]),
        ]);

    [Fact]
    public void BirthRateIsPerTickAndBirthsStayInTheWindow()
    {
        var sim = new VfxParticleSimulator(new VfxSampler(Emitter(0)), 0, mode: VfxPlaybackMode.OneShot, stepsPerFrame: 1);
        int[] expected = [0, 2, 5, 7, 10]; // floor of the running 2.5/frame accumulator, births from local frame 1
        for (int f = 0; f < expected.Length; f++) { sim.Advance(f); Assert.Equal(expected[f], sim.Particles.Length); }
        sim.Advance(29); Assert.Equal(72, sim.Particles.Length); // 29 frames x 2.5
        sim.Advance(45); Assert.Equal(72, sim.Particles.Length); // no births at local >= frame count (30)

        var late = new VfxParticleSimulator(new VfxSampler(Emitter(0, start: 10)), 0, stepsPerFrame: 1);
        late.Advance(10); Assert.Equal(0, late.Particles.Length); // only while start < local
        late.Advance(11); Assert.Equal(2, late.Particles.Length);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    public void ParticlesMoveInMetresPerSecondWithOptionalGravity(uint flags)
    {
        var sim = new VfxParticleSimulator(new VfxSampler(Emitter(flags)), 0, stepsPerFrame: 8);
        sim.Advance(20);
        Assert.NotEmpty(sim.Particles.ToArray());
        foreach (var p in sim.Particles)
        {
            float age = p.LifeFraction * 10; // lifetime 48000 ticks = 10 s
            float y = -3 * age - (flags == 2 ? 4.9f * age * age : 0); // fired along local -Y at 3 m/s
            Assert.Equal(y, p.Position.Y, flags == 2 ? 0.06f : 1e-3f);
            Assert.Equal(0.2f, p.Size, 1e-5f); // radius = 2 x drop size (no shrink set: full size)
        }
    }

    [Fact]
    public void EveryStockParticleSystemEmits()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return;
        int total = 0, alive = 0, mid = 0;
        foreach (var path in Directory.GetFiles(dir, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var file = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
            var sampler = new VfxSampler(file);
            for (int i = 0; i < sampler.ParticleSystems.Count; i++, total++)
            {
                var sim = new VfxParticleSimulator(sampler, i, seed: 7);
                int peak = 0;
                for (int f = 0; f <= file.EndFrame; f++) { sim.Advance(f); peak = Math.Max(peak, sim.Particles.Length); }
                sim.Advance(file.EndFrame / 2f);
                if (sim.Particles.Length > 0) mid++;
                if (peak > 0) alive++;
                else output.WriteLine($"no particles: {Path.GetFileName(path)} #{i}");
            }
        }
        output.WriteLine($"{alive}/{total} systems emit; {mid} have live particles at mid-effect");
        // Every stock system emits within one pass of its effect; the rest at mid-effect are bursts that ended.
        Assert.Equal(total, alive);
    }
}
