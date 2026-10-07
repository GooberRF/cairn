using System.Diagnostics;
using Cairn.Ui.Diagnostics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;
using Cairn.Workspace;
using Cairn.Viewport;
using Cairn.Vfx.Animation;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Self-tests for the effects module (run by the shell with --selftest).</summary>
public static class VfxSelfTests
{
    private static readonly string[] Samples = ["Explosion_Sub.vfx", "NanoShieldHitBig.vfx", "DrillMissile01.vfx", "WaterSplash01.vfx", "Driller_SoftGlow01.vfx"];

    private static List<string> CorpusFiles() =>
        LocalPaths.Corpus is { } c && Directory.Exists(c) ? [.. Directory.GetFiles(c, "*.vfx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)] : [];

    private static (VfxDocument Doc, VfxDocumentView View) OpenWithView(SelfTestContext ctx, string path)
    {
        var doc = (VfxDocument)new VfxKind { Shell = ctx.Shell }.Open(path);
        var view = (VfxDocumentView)doc.View;
        view.Measure(new System.Windows.Size(1200, 800)); view.Arrange(new System.Windows.Rect(0, 0, 1200, 800));
        return (doc, view);
    }

    private static int VisibleModels(VfxDocumentView view) => ((System.Windows.Media.Media3D.Model3DGroup)view.Viewport.Renderer.Root).Children.Count;

    [SelfTest("VFX: every stock effect opens, renders and saves")]
    public static void CorpusOpenRenderSave(SelfTestContext ctx)
    {
        var files = CorpusFiles();
        if (files.Count == 0) { ctx.Log("corpus not found; skipped"); return; }
        int identical = 0, current = 0;
        foreach (var path in files)
        {
            string name = Path.GetFileName(path);
            var (doc, view) = OpenWithView(ctx, path);
            try
            {
                foreach (float t in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
                {
                    doc.SeekFrame(t * doc.EndFrame);
                    view.Viewport.Renderer.Update(view.Viewport.Camera);
                }
                if (!doc.IsOlderVersion)
                {
                    current++;
                    string tmp = Path.Combine(Path.GetTempPath(), $"cairn-vfx-{Guid.NewGuid():N}.vfx");
                    try { doc.SaveTo(tmp); if (File.ReadAllBytes(tmp).AsSpan().SequenceEqual(File.ReadAllBytes(path))) identical++; else ctx.Check(false, $"{name} saves byte-identical"); }
                    finally { File.Delete(tmp); }
                }
            }
            catch (Exception ex) { ctx.Check(false, $"{name}: {ex.GetType().Name}: {ex.Message}"); }
            finally { doc.Dispose(); }
        }
        ctx.Log($"{files.Count} effects opened and rendered at 5 times; {identical}/{current} current-version files saved byte-identical");
        ctx.Check(files.Count == 61, $"61 stock effects found ({files.Count})");
    }

    [SelfTest("VFX: renderer update time on the heaviest effects")]
    public static void RenderTiming(SelfTestContext ctx)
    {
        var files = CorpusFiles().OrderByDescending(f => new FileInfo(f).Length).Take(4)
            .Concat(CorpusFiles().Where(f => Samples.Contains(Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))).Distinct();
        foreach (var path in files)
        {
            var (doc, view) = OpenWithView(ctx, path);
            int frames = Math.Min(doc.EndFrame, 60);
            for (int f = 0; f < 3; f++) { doc.SeekFrame(f); view.Viewport.Renderer.Update(view.Viewport.Camera); } // warm up
            var watch = Stopwatch.StartNew();
            for (int f = 0; f <= frames; f++) { doc.SeekFrame(f); view.Viewport.Renderer.Update(view.Viewport.Camera); }
            double ms = watch.Elapsed.TotalMilliseconds / (frames + 1);
            ctx.Log($"{Path.GetFileName(path)}: {ms:0.00} ms/frame (sample + geometry + sort, {frames + 1} frames)");
            ctx.Check(ms < 16, $"{Path.GetFileName(path)} updates in under 16 ms ({ms:0.0})");
            doc.Dispose();
        }
    }

    [SelfTest("VFX: selection, visibility, playback modes and picking")]
    public static void Interaction(SelfTestContext ctx)
    {
        var path = CorpusFiles().FirstOrDefault(f => Path.GetFileName(f).Equals("CTFflag-blue.vfx", StringComparison.OrdinalIgnoreCase));
        if (path is null) { ctx.Log("corpus not found; skipped"); return; }
        var (doc, view) = OpenWithView(ctx, path);
        try
        {
            int mesh = VfxSections.SectionIndex<VfxMesh>(doc.Current, 0);
            // outliner -> selection model -> renderer highlight (bounds) -> back
            view.Outliner.SelectRow(mesh);
            ctx.Check(doc.Selection.Primary == mesh, "outliner row selects the object");
            doc.SeekFrame(5); view.Viewport.Renderer.Update(view.Viewport.Camera);
            ctx.Check(view.Viewport.Renderer.TryGetBounds(mesh, out var min, out var max), "selected object has highlight bounds");
            doc.Selection.Select(-1);
            doc.Selection.Select(mesh);
            ctx.Check(view.Outliner.ShowsSelected(mesh), "selection model selects the outliner row");
            // click-pick with a ray through the object's centre
            var centre = (min + max) / 2;
            var eye = view.Viewport.Camera.Eye;
            int hit = view.Viewport.Renderer.Pick(new WorldRay(eye, System.Numerics.Vector3.Normalize(centre - eye)), out _);
            ctx.Check(hit >= 0 && VfxSections.KindOf(doc.Current.Sections[hit]) == VfxSectionKind.Mesh, $"ray at a mesh centre picks a mesh ({hit})");
            // hide / show
            int before = VisibleModels(view);
            doc.SetHidden(mesh, true); view.Viewport.Renderer.Update(view.Viewport.Camera);
            int hidden = VisibleModels(view);
            doc.SetHidden(mesh, false); view.Viewport.Renderer.Update(view.Viewport.Camera);
            ctx.Check(hidden < before && VisibleModels(view) == before, $"hide/show object ({before} -> {hidden} -> {VisibleModels(view)} models)");
            // playback modes at the end of the timeline
            doc.Mode = VfxPlaybackMode.OneShot; doc.SeekFrame(doc.EndFrame + 10); view.Viewport.Renderer.Update(view.Viewport.Camera);
            ctx.Check(!doc.State.MeshesVisible && VisibleModels(view) == 0, "one-shot: nothing drawn past the end");
            doc.Mode = VfxPlaybackMode.HoldLastFrame; view.Viewport.Renderer.Update(view.Viewport.Camera);
            ctx.Check(doc.State.MeshesVisible && VisibleModels(view) > 0 && doc.State.Frame > doc.EndFrame - 2, $"hold: last frame drawn ({doc.State.Frame})");
            doc.Mode = VfxPlaybackMode.Loop; view.Viewport.Renderer.Update(view.Viewport.Camera);
            ctx.Check(doc.State.Frame < 1 && VisibleModels(view) > 0, $"loop: wraps to the start ({doc.State.Frame})");
        }
        finally { doc.Dispose(); }
    }

    [SelfTest("VFX: particles are alive mid-animation")]
    public static void Particles(SelfTestContext ctx)
    {
        int systems = 0, alive = 0;
        foreach (var path in CorpusFiles())
        {
            var (doc, view) = OpenWithView(ctx, path);
            if (doc.Simulators.Length > 0)
            {
                doc.SeekFrame(doc.EndFrame / 2f); view.Viewport.Renderer.Update(view.Viewport.Camera);
                systems += doc.Simulators.Length;
                alive += doc.Simulators.Count(s => s.Particles.Length > 0);
            }
            doc.Dispose();
        }
        ctx.Log($"{alive}/{systems} particle systems have live particles at mid-animation");
        ctx.Check(systems == 0 || alive * 2 >= systems, "most particle systems emit by mid-animation");
    }

    [SelfTest("VFX: convert older version is undoable")]
    public static void ConvertUndo(SelfTestContext ctx)
    {
        var path = CorpusFiles().FirstOrDefault(f => Path.GetFileName(f).Equals("Driller_SoftGlow01.vfx", StringComparison.OrdinalIgnoreCase));
        if (path is null) { ctx.Log("corpus not found; skipped"); return; }
        var (doc, _) = OpenWithView(ctx, path);
        try
        {
            ctx.Check(doc.IsOlderVersion && doc.ConvertCommand.CanExecute(null), "older file offers convert");
            doc.ConvertCommand.Execute(null);
            ctx.Check(!doc.IsOlderVersion && doc.IsDirty && doc.CanUndo, "converted to the current version");
            doc.Undo();
            ctx.Check(doc.IsOlderVersion && doc.CanRedo, "undo restores the older version");
            doc.Redo();
            ctx.Check(!doc.IsOlderVersion && doc.Current.Version == VfxVersion.Current, "redo converts again");
        }
        finally { doc.Dispose(); }
    }

    /// <summary>Before a screenshot: wait (up to 5 s) for the active effect's textures, then redraw.</summary>
    [ScreenshotStep(100)]
    public static async Task WaitForTextures(ScreenshotContext ctx)
    {
        if (ctx.Shell.ActiveDocument is not VfxDocument { View: VfxDocumentView view }) return;
        var watch = Stopwatch.StartNew();
        await ctx.SettleAsync();
        while (view.Viewport.Renderer.PendingTextures > 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(50);
        view.Viewport.Invalidate();
        await ctx.SettleAsync();
        ctx.Log($"textures: {view.Viewport.Renderer.LoadedTextures} loaded; camera distance {view.Viewport.Camera.Distance:0.###}");
        if (!ctx.Options.ContainsKey("vfx-dump")) return;
        var doc = (VfxDocument)ctx.Shell.ActiveDocument!;
        var sample = new Cairn.Vfx.Animation.VfxMeshSample();
        for (int m = 0; m < doc.Sampler.Meshes.Count; m++)
        {
            var mv = doc.Sampler.Meshes[m];
            bool on = doc.Sampler.SampleMesh(m, doc.State.Frame, sample);
            ctx.Log($"mesh {m} flags 0x{mv.Flags:X} active {on} centre {sample.Center} w {sample.Width} h {sample.Height} up {sample.Up} p0 {(sample.Positions.Length > 0 ? sample.Positions[0] : default)}");
        }
    }

    [SelfTest("VFX: new effect saves and reopens equal")]
    public static void NewSaveReopen(SelfTestContext ctx)
    {
        var kind = new VfxKind { Shell = ctx.Shell };
        var doc = (VfxDocument)kind.CreateNew()!;
        string path = Path.Combine(Path.GetTempPath(), $"cairn-vfx-{Guid.NewGuid():N}.vfx");
        try
        {
            doc.SaveTo(path);
            var again = VfxReader.Read(File.ReadAllBytes(path), "new.vfx");
            ctx.Check(again.Version == VfxVersion.Current && again.Sections.Length == 0, "new effect reopens as an empty current-version effect");
            ctx.Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(VfxWriter.Write(VfxBuilder.NewFile())), "bytes equal");
        }
        finally { File.Delete(path); doc.Dispose(); }
    }
}
