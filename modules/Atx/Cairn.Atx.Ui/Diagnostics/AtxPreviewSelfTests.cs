using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Cairn.Atx.Ui.Preview;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;

namespace Cairn.Atx.Ui.Diagnostics;

/// <summary>Self-tests of the read-only animated texture preview other modules embed (<c>IAssetPreviewProvider</c>).</summary>
public static class AtxPreviewSelfTests
{
    private static List<string> SampleFiles()
    {
        var list = new List<string>();
        if (LocalPaths.RepositoryRoot is { } root && Directory.Exists(Path.Combine(root, "samples", "atx")))
            list.AddRange(Directory.GetFiles(Path.Combine(root, "samples", "atx"), "*.atx").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        if (LocalPaths.Research is { } research && Directory.Exists(Path.Combine(research, "atx_workbench")))
            list.AddRange(Directory.GetFiles(Path.Combine(research, "atx_workbench"), "*.atx", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Take(3));
        return list;
    }

    private static AtxModule Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<AtxModule>().FirstOrDefault() ?? new AtxModule();

    /// <summary>
    /// A preview for a loose file: frames resolve next to it first, as they would beside the file in a
    /// packfile (the module's own provider has only the name and resolves through the game data).
    /// </summary>
    private static FrameworkElement Create(SelfTestContext ctx, string path) =>
        new AtxPreview(File.ReadAllBytes(path), Path.GetFileName(path), () => ctx.Shell.Assets.ResolverFor(Path.GetDirectoryName(path)));

    [SelfTest("atx.previews-render")]
    public static async Task PreviewsRenderAndCollect(SelfTestContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0) { ctx.Skip("no sample .atx files found"); return; }
        var module = Module(ctx);
        ctx.Check(module.CanPreview("a.ATX") && !module.CanPreview("a.vfx"), "CanPreview takes .atx only");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var viaModule = module.CreatePreview(File.ReadAllBytes(files[0]), Path.GetFileName(files[0]));
        ctx.Check(viaModule is AtxPreview && timer.ElapsedMilliseconds < 200, $"the module creates an AtxPreview ({timer.ElapsedMilliseconds} ms)");
        (viaModule as IDisposable)?.Dispose();
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var window = PreviewTestKit.Host();
        try
        {
            foreach (var path in files)
            {
                var weak = await RenderOneAsync(ctx, window, path, shots);
                ctx.Check(await PreviewTestKit.CollectedAsync(weak), $"{Path.GetFileName(path)}: preview collectable after Dispose");
            }
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RenderOneAsync(SelfTestContext ctx, Window window, string path, string shots)
    {
        string name = Path.GetFileName(path);
        AtxPreview? preview = (AtxPreview)Create(ctx, path);
        var weak = new WeakReference(preview);
        window.Content = preview;
        await preview.Loading;
        await PreviewTestKit.SettleAsync();
        bool broken = name.StartsWith("broken", StringComparison.OrdinalIgnoreCase);
        ctx.Log($"{name}: {preview.FramesShown} frames shown, frame {preview.CurrentFrame}; message: {preview.Message ?? "none"}");
        if (!broken) ctx.Check(preview.FramesShown > 0 || preview.Message?.Contains("not found", StringComparison.Ordinal) == true,
            $"{name}: frames composited, or the missing frame file named ({preview.FramesShown} frames)");
        int first = preview.CurrentFrame;
        await Task.Delay(600);
        if (!broken && preview.FramesShown > 1) ctx.Log($"{name}: frame {first} -> {preview.CurrentFrame} after 0.6 s");
        int colours = PreviewTestKit.SavePng(preview, Path.Combine(shots, "atx_" + Path.GetFileNameWithoutExtension(name) + ".png"));
        ctx.Log($"{name}: {colours} colours in the capture");
        ctx.Check(colours > 2, $"{name}: capture is not blank ({colours} colours)");
        window.Content = null;
        preview.Dispose();
        preview = null;
        return weak;
    }

    [SelfTest("atx.previews-rapid")]
    public static async Task RapidCreateDispose(SelfTestContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0) { ctx.Skip("no sample .atx files found"); return; }
        var window = PreviewTestKit.Host();
        try
        {
            await PreviewTestKit.IdleAsync();
            double before = PreviewTestKit.ManagedMegabytes();
            var weaks = await CycleAsync(ctx, window, files, 50);
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
    private static async Task<List<WeakReference>> CycleAsync(SelfTestContext ctx, Window window, List<string> files, int count)
    {
        var weaks = new List<WeakReference>();
        IDisposable? previous = null;
        for (int i = 0; i < count; i++)
        {
            var preview = Create(ctx, files[i % files.Count]);
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

    [SelfTest("atx.previews-siblings")]
    public static async Task PreviewUsesSiblings(SelfTestContext ctx)
    {
        string? path = SampleFiles().FirstOrDefault(p => Path.GetFileName(p).Equals("hazard_strip.atx", StringComparison.OrdinalIgnoreCase));
        if (path is null) { ctx.Skip("samples/atx/hazard_strip.atx not found"); return; }
        // The sample with its frames and mask renamed to names no game data has, offered only as siblings
        // (the provider itself resolves through the game data; nothing next to the file is searched).
        string folder = Path.GetDirectoryName(path)!;
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(File.ReadAllText(path).Replace("hazard_strip_", "cairnsib_hs_", StringComparison.OrdinalIgnoreCase));
        var files = Directory.GetFiles(folder, "hazard_strip_*.tga")
            .Select(f => new KeyValuePair<string, byte[]>(Path.GetFileName(f).Replace("hazard_strip_", "cairnsib_hs_", StringComparison.OrdinalIgnoreCase), File.ReadAllBytes(f)))
            .ToList();
        var resolver = ctx.Shell.Assets.Resolver;
        foreach (var (name, _) in files) ctx.Check(resolver.Resolve(name) is null, $"{name}: not in the game data");
        var siblings = AssetSiblings.FromMemory("siblings.vpp", files);
        var module = Module(ctx);
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var window = PreviewTestKit.Host();
        try
        {
            var preview = (AtxPreview)module.CreatePreview(bytes, "cairnsib_hs.atx", siblings)!;
            window.Content = preview;
            await preview.Loading;
            await PreviewTestKit.SettleAsync();
            ctx.Log($"with siblings: {preview.FramesShown} frames; message {preview.Message ?? "none"}; reads {string.Join(", ", files.Select(f => $"{f.Key}={siblings.ReadCount(f.Key)}"))}");
            ctx.Check(preview.FramesShown == 8 && preview.Message is null, $"all 8 frames composited from the siblings ({preview.FramesShown})");
            ctx.Check(files.All(f => siblings.ReadCount(f.Key) > 0), "every frame and the mask were read from the siblings");
            int colours = PreviewTestKit.SavePng(preview, Path.Combine(shots, "atx_siblings.png"));
            ctx.Check(colours > 2, $"capture is not blank ({colours} colours)");
            window.Content = null;
            preview.Dispose();

            var without = (AtxPreview)module.CreatePreview(bytes, "cairnsib_hs.atx")!;
            await without.Loading;
            ctx.Check(without.FramesShown == 0 && without.Message?.Contains("not found", StringComparison.Ordinal) == true, $"without siblings the frames are missing ({without.Message ?? "no message"})");
            without.Dispose();
        }
        finally { window.Close(); }
    }

    [SelfTest("atx.previews-corrupt")]
    public static async Task CorruptShowsMessage(SelfTestContext ctx)
    {
        var module = Module(ctx);
        var files = SampleFiles();
        byte[] good = files.Count > 0 ? File.ReadAllBytes(files[0]) : new byte[64];
        foreach (var (what, bytes) in PreviewTestKit.Corrupt(good))
        {
            var preview = (AtxPreview)module.CreatePreview(bytes, "broken.atx")!;
            await preview.Loading;
            ctx.Check(preview.Message is { Length: > 0 } && preview.FramesShown == 0 || preview.Message is null && preview.FramesShown > 0,
                $"{what}: a message or frames, no exception ({preview.Message ?? $"{preview.FramesShown} frames"})");
            if (what == "random bytes") ctx.Check(preview.Message?.StartsWith("Cannot preview broken.atx", StringComparison.Ordinal) == true, "random bytes: the parse failure is the message");
            preview.Dispose();
        }
        ctx.Check(module.CreatePreview([1, 2, 3], "x.vfx") is null, "another type gives null");
    }
}
