using Cairn.Formats;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Rfe;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

/// <summary>Red Faction II effects (.rfe): the reader on the stock files and damaged input, the sampler, and the conversion to .vfx.</summary>
public sealed class RfeTests(ITestOutputHelper output)
{
    /// <summary>The six stock files in the older layout the PC game refuses.</summary>
    private static readonly string[] OldLayout = ["fan.rfe", "fan2.rfe", "fire_tiny.rfe", "gib2.rfe", "stm_red_lg.rfe", "stm_red_md.rfe"];

    /// <summary>Every .rfe of the Red Faction II install's packfile groups (group, name, bytes); empty without an install.</summary>
    private static List<(string Group, string Name, byte[] Bytes)> Corpus()
    {
        var list = new List<(string, string, byte[])>();
        if (LocalPaths.Rf2GameDirectory is not { } game || !Directory.Exists(Path.Combine(game, "pc_media"))) return list;
        foreach (string toc in Directory.EnumerateFiles(Path.Combine(game, "pc_media"), "*.toc_group").Order(StringComparer.OrdinalIgnoreCase))
        {
            var group = new Rf2GroupFiles(toc);
            foreach (string name in group.Names.Where(RfeReader.HasExtension))
                list.Add((Path.GetFileNameWithoutExtension(toc), name, group.Read(name, int.MaxValue) ?? throw new IOException($"{name} could not be read")));
        }
        return list;
    }

    [Fact]
    public void EveryStockEffectReadsToItsLastByteAndTheOldLayoutIsRefused()
    {
        var corpus = Corpus();
        if (corpus.Count == 0) { output.WriteLine("No Red Faction II install: " + LocalPaths.HowToSet(LocalPaths.Rf2GameDirectoryVariable, "rf2GameDirectory")); return; }
        int read = 0, meshes = 0, emitters = 0, materials = 0;
        var refused = new List<string>();
        foreach (var (group, name, bytes) in corpus)
        {
            if (RfeReader.Identify(bytes) == RfeLayout.OldLayout)
            {
                var ex = Assert.Throws<AssetFormatException>(() => RfeReader.Read(bytes, name));
                Assert.Contains("older Red Faction II effect layout that the PC game does not load either", ex.Message, StringComparison.Ordinal);
                refused.Add(name);
                continue;
            }
            var file = RfeReader.Read(bytes, name);
            Assert.Equal(bytes.Length, file.ByteLength);
            read++;
            meshes += file.Meshes.Length; emitters += file.Emitters.Length; materials += file.Materials.Length;
            Assert.All(file.Meshes, m => Assert.Single(m.Body.Textures));
            _ = group;
        }
        output.WriteLine($"{corpus.Count} files: {read} read, {refused.Count} refused; {meshes} meshes, {emitters} emitters, {materials} materials");
        Assert.Equal(517, corpus.Count);
        Assert.Equal(511, read);
        Assert.Equal(OldLayout.Order(StringComparer.OrdinalIgnoreCase), refused.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryStockEffectConvertsReadsBackLintsWithoutErrorsAndKeepsItsMeshMotion()
    {
        var corpus = Corpus();
        if (corpus.Count == 0) { output.WriteLine("No Red Faction II install."); return; }
        int converted = 0, approximated = 0;
        double worstPosition = 0, worstAngle = 0, worstScale = 0, worstPoint = 0;
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, name, bytes) in corpus)
        {
            if (RfeReader.Identify(bytes) != RfeLayout.Pc) continue;
            var effect = RfeReader.Read(bytes, name);
            var conversion = RfeToVfx.Convert(effect, name);
            var back = VfxReader.Read(conversion.Bytes, conversion.OutputName);
            var errors = VfxLinter.Lint(back, new VfxLintContext(conversion.OutputName)).Where(d => d.Severity == Cairn.Rfa.Linting.DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{name}: {string.Join("; ", errors)}");
            converted++;
            if (conversion.Approximations > 0) approximated++;
            foreach (var n in conversion.Notes.Where(n => n.Kind != RfeNoteKind.Exact))
            {
                string text = System.Text.RegularExpressions.Regex.Replace(n.Text, @"\d[\d,]*", "#");
                string key = n.Kind + ": " + text[..Math.Min(60, text.Length)];
                kinds[key] = kinds.GetValueOrDefault(key) + 1;
            }
            // the motion: Red Faction's keyframes against the game's even keys at several times
            var vfxMeshes = back.Sections.OfType<VfxMesh>().ToList();
            Assert.Equal(effect.Meshes.Length, vfxMeshes.Count);
            for (int m = 0; m < effect.Meshes.Length; m++)
            {
                var keys = vfxMeshes[m].Keys!;
                float size = MathF.Max(1, effect.Meshes[m].Body.Radius);
                foreach (float f in new[] { 0f, 0.13f, 0.25f, 0.5f, 0.61f, 0.77f, 0.999f })
                {
                    float seconds = f * effect.Duration;
                    var (p, q, s) = RfeSampler.Transform(effect.Meshes[m], RfeSampler.Fraction(seconds, effect.Duration));
                    float tick = seconds * VfxTime.TicksPerSecond;
                    var kt = VfxKeyframeMath.EvaluateVector(keys.Translation, tick, Vector3.Zero);
                    var ks = VfxKeyframeMath.EvaluateVector(keys.Scale, tick, Vector3.One);
                    var kq = VfxKeyframeMath.EvaluateRotation(keys.Rotation, tick);
                    // a point on the mesh's bounding sphere, placed both ways
                    var point = new Vector3(0.6f, 0.5f, 0.62f) * size;
                    worstPosition = Math.Max(worstPosition, (p - kt).Length());
                    worstScale = Math.Max(worstScale, (s - ks).Length());
                    float dot = MathF.Min(1, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(q), Quaternion.Normalize(kq))));
                    worstAngle = Math.Max(worstAngle, 2 * MathF.Acos(dot));
                    var a = p + Vector3.Transform(s * point, q);
                    var b = VfxKeyframeMath.ApplyKeyed(kt, kq, ks, Vector3.Zero, Quaternion.Identity, Vector3.One, point);
                    // keyframes sit on whole ticks: a key may move by up to half a tick, so allow the point's speed times a tick
                    float dt = 2f / VfxTime.TicksPerSecond;
                    var ahead = RfeSampler.Apply(effect.Meshes[m], RfeSampler.Fraction(seconds + dt, effect.Duration), point);
                    var behind = RfeSampler.Apply(effect.Meshes[m], RfeSampler.Fraction(seconds - dt, effect.Duration), point);
                    float speed = MathF.Max((ahead - a).Length(), (a - behind).Length()) / dt;
                    float error = (a - b).Length();
                    Assert.True(error < 2e-4f * size + 1e-4f + speed / VfxTime.TicksPerSecond, $"{name} mesh {m} at {seconds:0.###} s: {a} against {b} (point speed {speed:0.###} m/s)");
                    worstPoint = Math.Max(worstPoint, error);
                }
            }
        }
        output.WriteLine($"{converted} converted; {approximated} with at least one approximation; worst position {worstPosition:0.######} m, angle {worstAngle:0.######} rad, scale {worstScale:0.######}, point {worstPoint:0.######} m");
        foreach (var (k, n) in kinds.OrderByDescending(k => k.Value)) output.WriteLine($"{n,4} {k}");
        Assert.Equal(511, converted);
    }

    [Fact]
    public void StockParticlesStayNearTheirEffect()
    {
        var corpus = Corpus();
        if (corpus.Count == 0) { output.WriteLine("No Red Faction II install."); return; }
        var worst = new List<(float Distance, string Name, int Emitter)>();
        foreach (var (_, name, bytes) in corpus.DistinctBy(c => c.Name))
        {
            if (RfeReader.Identify(bytes) != RfeLayout.Pc) continue;
            var effect = RfeReader.Read(bytes, name);
            if (effect.Emitters.Length == 0) continue;
            var sampler = new VfxSampler(RfeToVfx.Convert(effect with { Meshes = [] }, name).File);
            for (int e = 0; e < effect.Emitters.Length; e++)
            {
                var sim = new VfxParticleSimulator(sampler, e, extras: RfeToVfx.Extras(effect, e));
                float far = 0;
                foreach (float f in new[] { 0.25f, 0.5f, 0.75f, 1f, 1.5f })
                {
                    sim.Advance(f * effect.Duration * VfxTime.FramesPerSecond);
                    foreach (var p in sim.Particles)
                    {
                        Assert.True(float.IsFinite(p.Position.X) && float.IsFinite(p.Position.Y) && float.IsFinite(p.Position.Z), $"{name} emitter {e}: {p.Position}");
                        far = MathF.Max(far, p.Position.Length());
                    }
                }
                worst.Add((far, name, e));
            }
        }
        foreach (var (d, n, e) in worst.OrderByDescending(w => w.Distance).Take(12)) output.WriteLine($"{d,10:0.0} m  {n} emitter {e}");
        Assert.True(worst.Max(w => w.Distance) < 1000, "a particle flew off");
    }

    [Fact]
    public void TheSyntheticEffectReadsBackAsWritten()
    {
        var sample = SyntheticRfe.Sample();
        var bytes = SyntheticRfe.Write(sample);
        var file = RfeReader.Read(bytes, "sample.rfe");
        Assert.Equal(bytes.Length, file.ByteLength);
        Assert.Equal(sample.Duration, file.Duration);
        Assert.Equal(sample.Textures.ToArray(), file.Textures.ToArray());
        Assert.Equal(2, file.Meshes.Length);
        Assert.Equal(RfeFacing.Billboard, file.Meshes[1].FacingMode);
        Assert.Equal(sample.Meshes[0].Positions.ToArray(), file.Meshes[0].Positions.ToArray());
        Assert.Equal(4, file.Meshes[0].Body.VertexCount);
        Assert.Equal(2, file.Meshes[0].Body.TriangleCount);
        var e = Assert.Single(file.Emitters);
        Assert.True(e.HasGravity && !e.HasAcceleration);
        Assert.Equal(sample.Emitters[0].BirthRate.ToArray(), e.BirthRate.ToArray());
    }

    [Fact]
    public void EvenKeysAreLerpedAndSlerpedOverTheDuration()
    {
        var mesh = SyntheticRfe.Sample().Meshes[0];
        // 5 position keys: 0, 0.5, 1, 1.5, 2 m along X
        Assert.Equal(0, RfeSampler.Sample(mesh.Positions, 0, default).X, 6);
        Assert.Equal(1, RfeSampler.Sample(mesh.Positions, 0.5f, default).X, 6);
        Assert.Equal(0.25f, RfeSampler.Sample(mesh.Positions, 0.125f, default).X, 5);
        Assert.Equal(2, RfeSampler.Sample(mesh.Positions, 1, default).X, 6);
        Assert.Equal(2, RfeSampler.Sample(mesh.Positions, 7, default).X, 6); // clamped
        // 3 rotation keys, 0, 45 and 90 degrees about Y: a quarter of the way is 22.5 degrees
        var q = RfeSampler.Sample(mesh.Rotations, 0.25f);
        Assert.Equal(MathF.PI / 8, 2 * MathF.Acos(q.W), 4);
        // 2 scale keys
        Assert.Equal(1.5f, RfeSampler.Sample(mesh.Scales, 0.5f, Vector3.One).Y, 6);
        // one key is constant, none is the identity
        Assert.Equal(new Vector3(0, 1.5f, 0), RfeSampler.Sample(SyntheticRfe.Sample().Meshes[1].Positions, 0.7f, default));
        Assert.Equal(Vector3.One, RfeSampler.Sample(ImmutableArray<Vector3>.Empty, 0.3f, Vector3.One));
        Assert.Equal((2, 3, 0.5f), RfeSampler.EvenKeys(5, 0.625f));
        // the game's rotation is the stored quaternion's conjugate: +90 degrees about Y stored turns +X to +Z
        var turned = RfeSampler.Apply(mesh with { Positions = [], Scales = [], Rotations = [Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)] }, 0, Vector3.UnitX);
        Assert.Equal(1, turned.Z, 5);
    }

    [Fact]
    public void TracksAreKeyedInSecondsAndHoldTheirEnds()
    {
        var m = SyntheticRfe.Sample().Materials[0]; // opacity 0, 1 at 0.5 s, 1 at 2.5 s, 0 at 3 s
        Assert.Equal(0, RfeSampler.Opacity(m, 0), 6);
        Assert.Equal(0.5f, RfeSampler.Opacity(m, 0.25f), 5);
        Assert.Equal(1, RfeSampler.Opacity(m, 1.7f), 6);
        Assert.Equal(0.5f, RfeSampler.Opacity(m, 2.75f), 5);
        Assert.Equal(0, RfeSampler.Opacity(m, 9), 6);
        Assert.Equal(0.5f, RfeSampler.SelfIllumination(m, 2), 6);
        Assert.Equal(0.8f, RfeSampler.ParticleOpacity(SyntheticRfe.Sample().Materials[2]), 6);
        Assert.Equal(1.5f, RfeSampler.EffectTime(4.5, 3, loop: true), 5);
        Assert.Equal(3, RfeSampler.EffectTime(4.5, 3, loop: false), 5);
    }

    [Fact]
    public void TheOldLayoutAndOtherVersionsAreRefusedClearly()
    {
        var old = SyntheticRfe.OldLayout();
        Assert.Equal(RfeLayout.OldLayout, RfeReader.Identify(old));
        Assert.Contains("PC game does not load either", Assert.Throws<AssetFormatException>(() => RfeReader.Read(old, "fan.rfe")).Message, StringComparison.Ordinal);
        var v2 = SyntheticRfe.Write(SyntheticRfe.Sample());
        BitConverter.GetBytes(2).CopyTo(v2, 4);
        Assert.Contains("version 3 only", Assert.Throws<AssetFormatException>(() => RfeReader.Read(v2, "x.rfe")).Message, StringComparison.Ordinal);
        Assert.Throws<AssetFormatException>(() => RfeReader.Read("GEKV"u8.ToArray(), "x.rfe"));
        Assert.Throws<AssetFormatException>(() => RfeReader.Read([], "x.rfe"));
    }

    [Fact]
    public void DamagedInputThrowsOnlyAssetFormatExceptionAndWhatReadsAlsoConverts()
    {
        var sources = new List<byte[]> { SyntheticRfe.Write(SyntheticRfe.Sample()) };
        sources.AddRange(Corpus().Where(c => RfeReader.Identify(c.Bytes) == RfeLayout.Pc).Select(c => c.Bytes).DistinctBy(b => Convert.ToBase64String(b)).Take(25));
        var random = new Random(4242);
        int reads = 0, refusals = 0;
        foreach (var source in sources)
        {
            for (int trial = 0; trial < 120; trial++)
            {
                var bytes = (byte[])source.Clone();
                switch (trial % 4)
                {
                    case 0: bytes = bytes[..random.Next(0, bytes.Length)]; break;
                    case 1: for (int k = 0; k < 4; k++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256); break;
                    case 2: // a count or float field replaced by an extreme value
                        int at = random.Next(8, Math.Max(9, bytes.Length - 4)) & ~3;
                        BitConverter.GetBytes(random.Next(4) switch { 0 => -1, 1 => int.MaxValue, 2 => 0x7FC00000, _ => random.Next() }).CopyTo(bytes, Math.Min(at, bytes.Length - 4));
                        break;
                    default: bytes = [.. bytes, .. new byte[random.Next(1, 9)]]; break;
                }
                RfeFile file;
                try { file = RfeReader.Read(bytes, "fuzz.rfe"); }
                catch (AssetFormatException) { refusals++; continue; }
                reads++;
                // whatever reads must sample and convert without throwing
                foreach (var mesh in file.Meshes) _ = RfeSampler.Matrix(mesh, 0.5f);
                var conversion = RfeToVfx.Convert(file, "fuzz.rfe");
                _ = VfxReader.Read(conversion.Bytes, "fuzz.vfx");
                for (int e = 0; e < file.Emitters.Length; e++)
                {
                    var sampler = new VfxSampler(conversion.File);
                    var sim = new VfxParticleSimulator(sampler, e, extras: RfeToVfx.Extras(file, e));
                    sim.Advance(40);
                }
            }
        }
        output.WriteLine($"{reads} damaged files still read, {refusals} refused");
        Assert.True(refusals > 0);
    }

    [Fact]
    public void ParticlesFollowTheEmitterFieldsThroughTheSimulator()
    {
        var effect = SyntheticRfe.Sample();
        var conversion = RfeToVfx.Convert(effect, "sample.rfe");
        var sampler = new VfxSampler(conversion.File);
        var sim = new VfxParticleSimulator(sampler, 0, extras: RfeToVfx.Extras(effect, 0));
        sim.Advance(4); // 0.27 s: young particles only, all still rising
        Assert.NotEmpty(sim.Particles.ToArray());
        Assert.All(sim.Particles.ToArray(), p => Assert.True(p.Velocity.Y > 0, $"launched upwards (local -Y turned up): {p.Velocity}"));
        sim.Advance(15); // 1 s: about 27 born (30/s from 0.1 s), none dead yet (life 1.2 s - 25 %)
        int live = sim.Particles.Length;
        Assert.InRange(live, 20, 32);
        foreach (var p in sim.Particles)
        {
            Assert.InRange(p.Size, 0, 0.1f + 1e-4f); // twice the radius at most
            Assert.InRange(p.Alpha, 0, 0.8f + 1e-4f); // the material's first opacity value at most
        }
        // gravity: the oldest particles have slowed down
        Assert.Contains(sim.Particles.ToArray(), p => p.Velocity.Y < 2.5f);
        // the constant acceleration applies with flag bit 4
        var lifted = effect with { Emitters = [effect.Emitters[0] with { Flags = 4, Acceleration = new Vector3(0, 0, 5) }] };
        var sim2 = new VfxParticleSimulator(new VfxSampler(RfeToVfx.Convert(lifted, "x.rfe").File), 0, extras: RfeToVfx.Extras(lifted, 0));
        sim2.Advance(15);
        Assert.All(sim2.Particles.ToArray().Where(p => p.LifeFraction > 0.4f), p => Assert.True(p.Velocity.Z > 0, $"pushed along +Z: {p.Velocity}"));
        // the conversion reports what it approximated
        Assert.Contains(conversion.Notes, n => n.Kind == RfeNoteKind.Approximated && n.Text.Contains("cone", StringComparison.Ordinal));
        Assert.Equal(RfeToVfx.EndFrame(effect.Duration), conversion.File.EndFrame);
        Assert.Equal(45, conversion.File.EndFrame);
    }
}
