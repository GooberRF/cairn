using System.Windows;
using System.Windows.Media.Media3D;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Vfx.Rfe;
using Cairn.Vfx.Ui.Documents;
using Cairn.Vfx.Ui.Rfe;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Red Faction II effects on synthetic files: the read-only tab, its textures, playback, the preview and Convert to.</summary>
public static class RfeSelfTests
{
    [SelfTest("vfx.rfe-synthetic")]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rfe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var opened = new List<IDocument>();
        try
        {
            string path = Path.Combine(folder, "sample.rfe");
            File.WriteAllBytes(path, SyntheticRfe.Write(SyntheticRfe.Sample()));
            File.WriteAllBytes(Path.Combine(folder, "sample_vfx.peg"), SyntheticRf2Mesh.SamplePeg(("crate_side.tga", 8, 8, 0xFF3080C0), ("glow.tga", 8, 8, 0x80FFC040), ("spark.tga", 4, 4, 0xFFFFFFFF)));
            File.WriteAllBytes(Path.Combine(folder, "fan.rfe"), SyntheticRfe.OldLayout());
            var module = ctx.Shell.Modules.OfType<VfxModule>().First();

            // the read-only tab
            if (!ctx.Check(ctx.Shell.OpenFile(path) && ctx.Shell.ActiveDocument is RfeDocument, "sample.rfe opens in a Red Faction II effect tab")) return;
            var doc = (RfeDocument)ctx.Shell.ActiveDocument!;
            opened.Add(doc);
            ctx.Check(doc.IsReadOnly && !((IDocument)doc).CanSave && !doc.CanUndo, "it is read-only and cannot be saved");
            var view = (RfeDocumentView)doc.View;
            {
                await PreviewTestKit.IdleAsync();
                await doc.TexturesFound;
                await PreviewTestKit.SettleAsync();
                ctx.Check(doc.Facts.Any(f => f.Section == "Textures" && f.Value.StartsWith("sample_vfx.peg", StringComparison.Ordinal)), "its textures are found in sample_vfx.peg beside it: " + string.Join("; ", doc.Facts.Where(f => f.Section == "Textures").Select(f => f.Value)));
                ctx.Check(doc.Facts.Any(f => f.Section == "Emitters" && f.Value.Contains("rectangle", StringComparison.Ordinal)), "the facts list the emitter");
                var renderer = view.Viewport.Renderer;
                doc.SeekSeconds(1.2);
                renderer.Update(view.Viewport.Camera);
                ctx.Check(renderer.VisibleMeshes == 2 && renderer.ParticleQuadCount >= 10, $"at 1.2 s both meshes and {renderer.ParticleQuadCount} particles are drawn");
                await PreviewTestKit.SettleAsync();
                ctx.Check(renderer.LoadedTextures == 3, $"{renderer.LoadedTextures} of 3 textures loaded");
                float before = doc.Playback.Time;
                doc.Playback.Play();
                await Task.Delay(400);
                doc.Playback.Pause();
                ctx.Check(doc.Playback.Time != before && doc.Scene.Seconds > 0, $"playback advances ({before} -> {doc.Playback.Time} ticks)");
                // the mesh moves as the game moves it: half way, 1 m along X
                doc.SeekSeconds(doc.Effect.Duration / 2);
                ctx.Check(Math.Abs(RfeSampler.Apply(doc.Effect.Meshes[0], doc.Scene.Fraction, System.Numerics.Vector3.Zero).X - 1) < 1e-3, "half way the quad is 1 m along X");
            }

            // Convert to: a Red Faction effect that reads back, lints without errors and plays
            var choice = AssetConversions.TargetsFor(ctx.Shell, doc).FirstOrDefault(c => c.Target.Extension == ".vfx");
            if (ctx.Check(choice is not null, "File > Convert to lists a Red Faction effect (.vfx)"))
            {
                string output = Path.Combine(folder, "out");
                var request = AssetConversions.RequestFor(ctx.Shell, doc, ".vfx", interactive: false) with { OutputFolder = output };
                string? written = await AssetConversions.RunAsync(ctx.Shell, choice!, request);
                if (ctx.Check(written is not null && File.Exists(written), $"written: {written}"))
                {
                    var file = VfxReader.Read(File.ReadAllBytes(written!), "sample.vfx");
                    var errors = VfxLinter.Lint(file, new VfxLintContext("sample.vfx")).Where(d => d.Severity == Cairn.Rfa.Linting.DiagnosticSeverity.Error).ToList();
                    ctx.Check(errors.Count == 0 && file.Sections.OfType<VfxMesh>().Count() == 2 && file.Sections.OfType<VfxParticleSystem>().Count() == 1, $"it reads back with 2 meshes, 1 particle system and no lint errors ({string.Join("; ", errors)})");
                    if (ctx.Shell.ActiveDocument is VfxDocument vfx)
                    {
                        opened.Add(vfx);
                        ctx.Check(vfx.ConversionNotes.Count > 0 && vfx.ConversionNotes.All(n => n.Severity == Cairn.Rfa.Linting.DiagnosticSeverity.Info), $"the converted tab lists the report in Problems ({vfx.ConversionNotes.Count} notes)");
                    }
                    else ctx.Check(false, "the converted effect opens in a tab");
                    // the source is never replaced; a second conversion takes a free name
                    string? again = await AssetConversions.RunAsync(ctx.Shell, choice!, request);
                    ctx.Check(again is not null && !string.Equals(again, written, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(again) == "sample (2).vfx", $"a second conversion takes a free name ({Path.GetFileName(again ?? "")})");
                    if (ctx.Shell.ActiveDocument is VfxDocument second && !opened.Contains(second)) opened.Add(second);
                    using var preview = (Preview.VfxPreview)module.CreatePreview(File.ReadAllBytes(written!), "sample.vfx")!;
                    await preview.Loading;
                    ctx.Check(preview.Message is null && preview.Renderer is not null, $"the converted effect plays in the effect preview ({preview.Message ?? "shown"})");
                }
            }

            // the packfile preview
            using (var preview = (RfePreview)module.CreatePreview(File.ReadAllBytes(path), "sample.rfe", AssetSiblings.FromMemory("synthetic.packfile",
                [new KeyValuePair<string, byte[]>("sample_vfx.peg", File.ReadAllBytes(Path.Combine(folder, "sample_vfx.peg")))]))!)
            {
                var window = PreviewTestKit.Host();
                try
                {
                    window.Content = preview;
                    await preview.Loading;
                    await PreviewTestKit.SettleAsync();
                    ctx.Check(preview.Message is null && preview.Renderer is { } r && r.LoadedTextures == 3, $"the preview shows the effect with its 3 textures from the packfile ({preview.Message ?? $"{preview.Renderer?.LoadedTextures} loaded"})");
                    ctx.Check(preview.Playback?.IsPlaying == true, "... and plays");
                }
                finally { window.Content = null; window.Close(); }
            }

            // the old layout is refused, clearly
            try
            {
                new RfeKind { Shell = ctx.Shell }.Open(Path.Combine(folder, "fan.rfe")).Dispose();
                ctx.Check(false, "the old layout is refused");
            }
            catch (Cairn.Formats.AssetFormatException ex)
            {
                ctx.Check(ex.Message.Contains("PC game does not load either", StringComparison.Ordinal), "the old layout is refused: " + ex.Message);
            }
            using (var old = (RfePreview)module.CreatePreview(SyntheticRfe.OldLayout(), "fan.rfe", null)!)
            {
                await old.Loading;
                ctx.Check(old.Message?.Contains("older Red Faction II effect layout", StringComparison.Ordinal) == true, "its preview says why: " + old.Message);
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ctx.Shell.Documents.Contains(d)) ctx.Shell.Close(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Regressions from the review: a batch reports a bad entry and goes on; a conversion reads no texture pack on the UI
    /// thread; the packfile preview keeps its particles across a loop; facing meshes turn their local +Z to the camera.
    /// </summary>
    [SelfTest("vfx.rfe-regressions")]
    public static async Task Regressions(SelfTestContext ctx)
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-rfe-reg-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var module = ctx.Shell.Modules.OfType<VfxModule>().First();
        var opened = new List<IDocument>();
        byte[] sample = SyntheticRfe.Write(SyntheticRfe.Sample());
        byte[] shared = SyntheticRf2Mesh.SamplePeg(("crate_side.tga", 8, 8, 0xFF3080C0), ("glow.tga", 8, 8, 0x80FFC040), ("spark.tga", 4, 4, 0xFFFFFFFF));
        try
        {
            // 1. the batch: an over-long effect and an entry that cannot be read are reported and skipped
            var huge = (byte[])sample.Clone();
            BitConverter.GetBytes(1e9f).CopyTo(huge, 12);
            ArchiveBatchEntry[] entries =
            [
                new("sample.rfe", sample.Length, () => sample),
                new("long.rfe", huge.Length, () => huge),
                new("unreadable.rfe", 10, () => throw new NotSupportedException("The entry's storage cannot be read.")),
            ];
            string batchOut = Path.Combine(folder, "batch");
            string? summary;
            try
            {
                summary = await module.RfeTools.ConvertBatchAsync(new ArchiveBatchRequest("synthetic.vpp", null, entries)
                {
                    Interactive = false, Options = new RfeConvertChoice(batchOut), AllEntries = [.. entries, new("shared.peg", shared.Length, () => shared)],
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                summary = "the batch aborted: " + ex.GetType().Name + ": " + ex.Message;
            }
            ctx.Check(summary is not null && summary.StartsWith("Converted 1 of 3", StringComparison.Ordinal) && summary.Contains("long.rfe", StringComparison.Ordinal)
                && summary.Contains("unreadable.rfe", StringComparison.Ordinal) && File.Exists(Path.Combine(batchOut, "sample.vfx")),
                $"a batch reports the over-long and the unreadable effect and converts the rest ({summary})");

            // 2. Convert to from a packfile entry: the packs are read off the UI thread (the texture is in another pack)
            var uiReads = new List<string>();
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["sample.rfe"] = sample, ["shared.peg"] = shared };
            var dispatcher = Application.Current.Dispatcher;
            var request = new AssetConversionRequest("sample.rfe", sample, ".vfx")
            {
                Interactive = false, OutputFolder = Path.Combine(folder, "entry"), ArchiveEntryNames = [.. files.Keys],
                ReadSibling = n =>
                {
                    if (dispatcher.CheckAccess()) lock (uiReads) uiReads.Add(n);
                    return files.GetValueOrDefault(n);
                },
            };
            var keep = module.RfeTools.QuietTextures;
            module.RfeTools.QuietTextures = Rf2TextureMode.KeepOriginals;
            string? written;
            try { written = await module.RfeTools.ConvertAsync(request); }
            finally { module.RfeTools.QuietTextures = keep; }
            if (ctx.Shell.ActiveDocument is VfxDocument converted) opened.Add(converted);
            int tgas = Directory.Exists(request.OutputFolder) ? Directory.GetFiles(request.OutputFolder, "*.tga").Length : 0;
            ctx.Check(written is not null && tgas == 3, $"the entry converts with its 3 textures written from shared.peg ({written}, {tgas} .tga)");
            ctx.Check(uiReads.Count == 0, $"no texture pack is read on the UI thread during the conversion ({string.Join(", ", uiReads)})");

            // 3. the packfile preview: particle time runs on across a loop instead of restarting
            using (var preview = (RfePreview)module.CreatePreview(sample, "sample.rfe", AssetSiblings.FromMemory("synthetic.packfile", [new KeyValuePair<string, byte[]>("shared.peg", shared)]))!)
            {
                var window = PreviewTestKit.Host();
                try
                {
                    window.Content = preview;
                    await preview.Loading;
                    await PreviewTestKit.SettleAsync();
                    if (ctx.Check(preview.Playback is not null && preview.Scene is not null && preview.Renderer is not null, $"the preview shows ({preview.Message})"))
                    {
                        var playback = preview.Playback!;
                        var scene = preview.Scene!;
                        float duration = scene.Effect.Duration;
                        playback.Pause();
                        playback.Seek((float)((duration - 0.15) * RfeDocument.TimeBase.TicksPerSecond));
                        playback.Play();
                        float last = playback.Time;
                        double elapsed = -1;
                        int live = -1;
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        while (watch.Elapsed < TimeSpan.FromSeconds(5))
                        {
                            await Task.Delay(5);
                            float now = playback.Time;
                            if (now + 1 < last)
                            {
                                elapsed = scene.Elapsed;
                                preview.Renderer!.Update(new Cairn.Viewport.OrbitCamera());
                                live = scene.Simulators.Sum(s => s.Particles.Length);
                                break;
                            }
                            last = now;
                        }
                        playback.Pause();
                        ctx.Check(elapsed > duration, $"after a loop the preview's effect time runs on ({elapsed:0.###} s of a {duration:0.###} s effect)");
                        ctx.Check(live >= 10, $"... so the particles of the last loop are still there ({live} live right after the loop)");
                    }
                }
                finally { window.Content = null; window.Close(); }
            }

            // 4. facing meshes in the preview: local +Z turned to the camera (facing 1), about the key's Y axis (facing 2)
            foreach (uint mode in new uint[] { 1, 2 })
            {
                var effect = SyntheticRfe.Sample();
                var tilt = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitX, 0.5f);
                effect = effect with { Meshes = [effect.Meshes[0], effect.Meshes[1] with { Facing = mode, Rotations = [tilt] }] };
                var scene = new RfeScene(effect, "facing.rfe");
                var renderer = new RfeSceneRenderer(scene, new RfeTextureSource(effect, "facing.rfe", RfeSurroundings.None));
                var camera = new Cairn.Viewport.OrbitCamera { Target = new System.Numerics.Vector3(0.4f, 0.3f, 0.2f), Distance = 4 };
                camera.SetView(35, 20);
                scene.Seek(1);
                renderer.Update(camera);
                var glow = ((Model3DGroup)renderer.Root).Children.OfType<GeometryModel3D>().FirstOrDefault(m => Math.Abs(m.Geometry.Bounds.SizeX - 0.6) < 1e-3);
                if (!ctx.Check(glow?.Transform is MatrixTransform3D, $"facing {mode}: the glow is drawn")) continue;
                var m = ((MatrixTransform3D)glow!.Transform).Matrix;
                var at = new System.Numerics.Vector3((float)m.OffsetX, (float)m.OffsetY, (float)m.OffsetZ);
                var z = System.Numerics.Vector3.Normalize(new((float)m.M31, (float)m.M32, (float)m.M33));
                var y = System.Numerics.Vector3.Normalize(new((float)m.M21, (float)m.M22, (float)m.M23));
                var toEye = System.Numerics.Vector3.Normalize(camera.Eye - at);
                var axis = System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY, RfeSampler.Rotation(tilt));
                if (mode == 1)
                    ctx.Check(System.Numerics.Vector3.Dot(z, toEye) > 0.9999f, $"facing 1: local +Z points at the camera (z·to-eye {System.Numerics.Vector3.Dot(z, toEye):0.####})");
                else
                {
                    var perp = System.Numerics.Vector3.Normalize(toEye - axis * System.Numerics.Vector3.Dot(toEye, axis));
                    ctx.Check(System.Numerics.Vector3.Dot(y, axis) > 0.9999f && System.Numerics.Vector3.Dot(z, perp) > 0.9999f,
                        $"facing 2: local Y stays on the key's Y axis and +Z turns towards the camera (y·axis {System.Numerics.Vector3.Dot(y, axis):0.####}, z·to-eye {System.Numerics.Vector3.Dot(z, perp):0.####})");
                }
            }
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(opened)) if (ctx.Shell.Documents.Contains(d)) ctx.Shell.Close(d);
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
