using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Dialogs;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Self-tests that new particle systems emit out of the box and reach the renderer.</summary>
public static class VfxParticleSelfTests
{
    [SelfTest("VFX: the particle fountain template and Effect > Add particle system emit live, rendered particles")]
    public static void NewParticlesAreAlive(SelfTestContext ctx)
    {
        var added = VfxCreation.AddObject(VfxBuilder.NewFile(), "Particle system").File;
        foreach (var (label, file, keep) in new[] { ("fountain template", VfxCreation.Template("Particle fountain"), "cairn-fountain.vfx"), ("added particle system", added, "cairn-added-particles.vfx") })
        {
            // kept in %TEMP% so it can be opened for a screenshot
            var path = Path.Combine(Path.GetTempPath(), keep);
            File.WriteAllBytes(path, VfxWriter.Write(file));
            var doc = (VfxDocument)new VfxKind { Shell = ctx.Shell }.Open(path);
            try
            {
                var view = (VfxDocumentView)doc.View;
                var renderer = view.Viewport.Renderer;
                foreach (int frame in new[] { 5, doc.Current.EndFrame / 2, doc.Current.EndFrame - 2 })
                {
                    doc.SeekFrame(frame);
                    renderer.Update(view.Viewport.Camera);
                    int live = doc.Simulators.Sum(s => s.Particles.Length);
                    ctx.Check(live >= 5, $"{label}: {live} live particles at frame {frame}");
                    ctx.Check(renderer.ParticleQuadCount > 0 && renderer.ParticleQuadCount <= live, $"{label}: {renderer.ParticleQuadCount} particle quads at frame {frame}");
                    // dying particles fade and shrink to zero: most must still be clearly visible
                    int visible = doc.Simulators.Sum(s => s.Particles.ToArray().Count(p => p.Size > 0.01f && p.Alpha > 0.05f));
                    ctx.Check(visible * 2 >= live, $"{label}: {visible} of {live} particles have size and alpha at frame {frame}");
                }
            }
            finally { doc.Dispose(); }
        }
    }
}
