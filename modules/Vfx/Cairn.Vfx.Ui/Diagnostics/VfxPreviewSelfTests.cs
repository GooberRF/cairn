using System.Runtime.CompilerServices;
using System.Windows;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vfx.Ui.Diagnostics;

/// <summary>Self-tests of the read-only effect preview other modules embed (<c>IAssetPreviewProvider</c>).</summary>
public static class VfxPreviewSelfTests
{
    private static readonly string[] Samples = ["Explosion_Sub.vfx", "laser01.vfx", "Driller_SoftGlow01.vfx", "CTFflag-blue.vfx", "WaterSplash01.vfx"];

    private static List<string> SampleFiles() =>
        LocalPaths.Corpus is { } c ? [.. Samples.Select(n => Path.Combine(c, n)).Where(File.Exists)] : [];

    private static VfxModule Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VfxModule>().FirstOrDefault() ?? new VfxModule();

    [SelfTest("VFX: previews render, dispose and are collectable")]
    public static async Task PreviewsRenderAndCollect(SelfTestContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0) { ctx.Skip("corpus not found"); return; }
        var module = Module(ctx);
        ctx.Check(module.CanPreview("a.VFX") && !module.CanPreview("a.rfa"), "CanPreview takes .vfx only");
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var window = PreviewTestKit.Host();
        try
        {
            foreach (var path in files)
            {
                var weak = await RenderOneAsync(ctx, module, window, path, shots);
                ctx.Check(await PreviewTestKit.CollectedAsync(weak), $"{Path.GetFileName(path)}: preview collectable after Dispose");
            }
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RenderOneAsync(SelfTestContext ctx, VfxModule module, Window window, string path, string shots)
    {
        string name = Path.GetFileName(path);
        var created = System.Diagnostics.Stopwatch.StartNew();
        VfxPreview? preview = (VfxPreview?)module.CreatePreview(File.ReadAllBytes(path), name);
        ctx.Check(preview is not null && created.ElapsedMilliseconds < 200, $"{name}: preview created in {created.ElapsedMilliseconds} ms");
        if (preview is null) return new WeakReference(null);
        var weak = new WeakReference(preview);
        window.Content = preview;
        await preview.Loading;
        await PreviewTestKit.SettleAsync();
        ctx.Check(preview.Message is null, $"{name}: shows the effect ({preview.Message ?? "no message"})");
        ctx.Check(preview.Playback?.IsPlaying == true, $"{name}: plays (looping)");
        int models = preview.Renderer is { } r ? ((System.Windows.Media.Media3D.Model3DGroup)r.Root).Children.Count : 0;
        int colours = PreviewTestKit.SavePng(preview, Path.Combine(shots, "vfx_" + Path.GetFileNameWithoutExtension(name) + ".png"));
        ctx.Log($"{name}: {models} models, {preview.Renderer?.LoadedTextures ?? 0} textures, {colours} colours in the capture");
        ctx.Check(colours > 20, $"{name}: capture is not blank ({colours} colours)");
        window.Content = null;
        preview.Dispose();
        ctx.Check(preview.Playback is null, $"{name}: Dispose stops and drops playback");
        preview = null;
        return weak;
    }

    [SelfTest("VFX: 50 previews created and disposed in quick succession")]
    public static async Task RapidCreateDispose(SelfTestContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0) { ctx.Skip("corpus not found"); return; }
        var module = Module(ctx);
        var bytes = files.Select(File.ReadAllBytes).ToArray();
        var window = PreviewTestKit.Host();
        try
        {
            await PreviewTestKit.IdleAsync();
            double before = PreviewTestKit.ManagedMegabytes();
            var weaks = await CycleAsync(module, window, files, bytes, 50);
            await PreviewTestKit.SettleAsync();
            int alive = 0;
            foreach (var w in weaks) if (!await PreviewTestKit.CollectedAsync(w)) alive++;
            double after = PreviewTestKit.ManagedMegabytes();
            ctx.Log($"managed memory {before:0.0} MB -> {after:0.0} MB after 50 previews; {alive} still alive");
            ctx.Check(alive == 0, $"every preview collectable ({alive} of {weaks.Count} alive)");
            ctx.Check(after - before < 30, $"memory growth below 30 MB ({after - before:0.0} MB)");
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<List<WeakReference>> CycleAsync(VfxModule module, Window window, List<string> files, byte[][] bytes, int count)
    {
        var weaks = new List<WeakReference>();
        IDisposable? previous = null;
        for (int i = 0; i < count; i++)
        {
            var preview = module.CreatePreview(bytes[i % bytes.Length], Path.GetFileName(files[i % files.Count]))!;
            weaks.Add(new WeakReference(preview));
            window.Content = preview;
            previous?.Dispose();
            previous = preview as IDisposable;
            // arrowing through a list: some previews get to load, most are replaced first
            await System.Windows.Threading.Dispatcher.Yield(i % 5 == 0 ? System.Windows.Threading.DispatcherPriority.ApplicationIdle : System.Windows.Threading.DispatcherPriority.Background);
        }
        window.Content = null;
        previous?.Dispose();
        return weaks;
    }

    [SelfTest("VFX: a preview takes its textures from the files beside it")]
    public static async Task PreviewUsesSiblings(SelfTestContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0 || ctx.Shell.Assets.Resolver is not { } resolver) { ctx.Skip("corpus or game data not found"); return; }
        var module = Module(ctx);
        // An effect whose texture references are renamed to names no game data has; the renamed textures
        // (the stock bytes, read through the shell's resolver) are offered only as siblings.
        VfxFile? effect = null; string? sourceName = null;
        var siblingFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var file = VfxUpgrade.ToCurrent(VfxReader.Read(File.ReadAllBytes(path), Path.GetFileName(path)));
            siblingFiles.Clear();
            for (int i = 0; i < file.Sections.Length; i++)
            {
                if (file.Sections[i] is not VfxMaterial { Texture0: { Name.Length: > 0 } t }) continue;
                if (resolver.Resolve(t.Name) is not { } hit) continue;
                string unique = $"cairnsib_{siblingFiles.Count}_{Path.GetFileNameWithoutExtension(t.Name)}.tga";
                // the sibling keeps the resolved file's own extension: a .tga reference finds a .dds sibling by supersede
                siblingFiles[Path.ChangeExtension(unique, Path.GetExtension(hit.ResolvedName))] = hit.ReadAllBytes();
                file = VfxEdit.SetTexture(file, i, unique);
            }
            if (siblingFiles.Count > 0) { effect = file; sourceName = Path.GetFileName(path); break; }
        }
        if (effect is null) { ctx.Skip("no sample effect has resolvable textures"); return; }
        foreach (var name in siblingFiles.Keys)
            ctx.Check(resolver.Resolve(name) is null, $"{name}: not in the game data");
        byte[] bytes = VfxWriter.Write(effect);
        var siblings = AssetSiblings.FromMemory("siblings.vpp", siblingFiles);
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var window = PreviewTestKit.Host();
        try
        {
            int withSiblings = await LoadedTexturesAsync(module.CreatePreview(bytes, "siblings_" + sourceName, siblings), window, Path.Combine(shots, "vfx_siblings.png"));
            int without = await LoadedTexturesAsync(module.CreatePreview(bytes, "siblings_" + sourceName), window, null);
            ctx.Log($"{sourceName}: {siblingFiles.Count} renamed textures; {withSiblings} loaded with siblings, {without} without; reads {string.Join(", ", siblingFiles.Keys.Select(n => $"{n}={siblings.ReadCount(n)}"))}");
            ctx.Check(withSiblings == siblingFiles.Count, $"every renamed texture loads from the siblings ({withSiblings} of {siblingFiles.Count})");
            ctx.Check(siblingFiles.Keys.All(n => siblings.ReadCount(n) > 0), "every sibling texture was read");
            ctx.Check(without == 0, $"without siblings none of them loads ({without})");
            var supersede = AssetSiblings.FromMemory("x", [new("a.dds", [1]), new("a.tga", [2]), new("b.TGA", [3])]);
            ctx.Check(supersede.Find("A.tga") == "A.dds" && supersede.Find("b.tga") == "b.tga" && supersede.Find("c.tga") is null, "sibling lookup follows the supersede chain");
        }
        finally { window.Close(); }
    }

    private static async Task<int> LoadedTexturesAsync(FrameworkElement? element, Window window, string? png)
    {
        if (element is not VfxPreview preview) return -1;
        window.Content = preview;
        await preview.Loading;
        await PreviewTestKit.SettleAsync();
        for (int i = 0; i < 100 && preview.Renderer is { PendingTextures: > 0 }; i++) await Task.Delay(50);
        await PreviewTestKit.IdleAsync();
        if (png is not null) PreviewTestKit.SavePng(preview, png);
        int loaded = preview.Renderer?.LoadedTextures ?? -1;
        window.Content = null;
        preview.Dispose();
        return loaded;
    }

    [SelfTest("VFX: a corrupt effect shows a message, not an exception")]
    public static async Task CorruptShowsMessage(SelfTestContext ctx)
    {
        var files = SampleFiles();
        byte[] good = files.Count > 0 ? File.ReadAllBytes(files[0]) : new byte[64];
        var module = Module(ctx);
        foreach (var (what, bytes) in PreviewTestKit.Corrupt(good))
        {
            var preview = (VfxPreview)module.CreatePreview(bytes, "broken.vfx")!;
            await preview.Loading;
            ctx.Check(preview.Message?.StartsWith("Cannot preview broken.vfx", StringComparison.Ordinal) == true, $"{what}: message shown ({preview.Message ?? "none"})");
            preview.Dispose();
        }
        ctx.Check(module.CreatePreview([1, 2, 3], "x.rfa") is null, "another type gives null");
    }
}
