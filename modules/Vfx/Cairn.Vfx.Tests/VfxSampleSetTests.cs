using Cairn.Rfa.Linting;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Vfx.SampleGen;

namespace Cairn.Vfx.Tests;

/// <summary>The samples/vfx set regenerates, re-reads and lints clean (the folder itself comes from running the tool).</summary>
public sealed class VfxSampleSetTests
{
    [Fact]
    public void SamplesWriteRereadAndLintClean()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cairn-vfx-samples-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = VfxSampleSet.WriteAll(dir);
            var effects = written.Where(p => p.EndsWith(".vfx", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Equal(4 + Enum.GetValues<Cairn.Vfx.Ui.Dialogs.VfxPrimitiveKind>().Length, effects.Count);
            foreach (var path in effects)
            {
                var bytes = File.ReadAllBytes(path);
                var file = VfxReader.Read(bytes, Path.GetFileName(path));
                Assert.Equal(bytes, VfxWriter.Write(file));
                var errors = VfxLinter.Lint(file).Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Code} {d.Message}").ToList();
                Assert.True(errors.Count == 0, $"{Path.GetFileName(path)}: {string.Join("; ", errors)}");
                Assert.Contains(VfxSampleSet.TextureName, System.Text.Encoding.Latin1.GetString(bytes)); // the placeholder, never a game texture
            }
            var tga = File.ReadAllBytes(Path.Combine(dir, VfxSampleSet.TextureName));
            Assert.Equal(2, tga[2]);                                   // uncompressed true colour
            Assert.Equal(64, BitConverter.ToUInt16(tga, 12));          // width
            Assert.Equal(64, BitConverter.ToUInt16(tga, 14));          // height
            Assert.Equal(32, tga[16]);                                 // with alpha
            Assert.Contains("Cairn.Vfx.SampleGen", File.ReadAllText(Path.Combine(dir, "README.md")));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>The committed fountain sample (not only a freshly generated one) has live particles once it is running.</summary>
    [Fact]
    public void CommittedParticleFountainEmits()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "samples", "vfx", "Particle_fountain.vfx"))) dir = dir.Parent;
        if (dir is null) return; // not run from inside the repository
        var path = Path.Combine(dir.FullName, "samples", "vfx", "Particle_fountain.vfx");
        var file = VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path));
        var sim = new Cairn.Vfx.Animation.VfxParticleSimulator(new Cairn.Vfx.Animation.VfxSampler(file), 0);
        sim.Advance(30);
        Assert.True(sim.Particles.Length > 0, "the committed Particle_fountain.vfx emits nothing at frame 30: regenerate samples/vfx with Cairn.Vfx.SampleGen");
    }

    [Fact]
    public void RingShockwaveGrowsAndFades()
    {
        var file = Cairn.Vfx.Ui.Dialogs.VfxCreation.Template("Ring shockwave");
        var mesh = file.Sections.OfType<VfxMesh>().Single();
        Assert.NotNull(mesh.Keys);
        float first = VfxEdit.SampleKeys(mesh.Keys!, VfxEdit.FrameTick(mesh, 0)).Scale.X;
        float last = VfxEdit.SampleKeys(mesh.Keys!, VfxEdit.FrameTick(mesh, mesh.Frames.Length - 1)).Scale.X;
        Assert.InRange(first, 0.99f, 1.01f);
        Assert.InRange(last, 5.9f, 6.1f);
    }
}
