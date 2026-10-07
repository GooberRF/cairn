using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Ui.Preview;
using BaseContext = Cairn.Ui.Diagnostics.SelfTestContext;

namespace Cairn.Rfa.Ui.Diagnostics;

/// <summary>Self-tests of the read-only mesh and clip previews other modules embed (<c>IAssetPreviewProvider</c>).</summary>
public static class RfaPreviewSelfTests
{
    private static readonly string[] Samples = ["2PartSwitch.v3m", "AirCompressor01.v3m", "admin_male.v3c", "auto_turret.v3c", "ult2_walk.rfa", "CS5_PARK_Shot01_22.rfa"];

    private static List<string> SampleFiles() =>
        LocalPaths.Corpus is { } c ? [.. Samples.Select(n => Path.Combine(c, n)).Where(File.Exists)] : [];

    private static RfaModule Module(BaseContext ctx) => ctx.Shell.Modules.OfType<RfaModule>().FirstOrDefault() ?? new RfaModule();

    [SelfTest("rfa.previews-render")]
    public static async Task PreviewsRenderAndCollect(BaseContext ctx)
    {
        var files = SampleFiles();
        if (files.Count == 0) { ctx.Skip("corpus not found"); return; }
        var module = Module(ctx);
        ctx.Check(module.CanPreview("a.V3M") && module.CanPreview("b.v3c") && module.CanPreview("c.rfa") && !module.CanPreview("d.vfx"), "CanPreview takes .v3m, .v3c and .rfa");
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
    private static async Task<WeakReference> RenderOneAsync(BaseContext ctx, RfaModule module, Window window, string path, string shots)
    {
        string name = Path.GetFileName(path);
        var created = System.Diagnostics.Stopwatch.StartNew();
        RfaPreview? preview = (RfaPreview?)module.CreatePreview(File.ReadAllBytes(path), name);
        ctx.Check(preview is not null && created.ElapsedMilliseconds < 300, $"{name}: preview created in {created.ElapsedMilliseconds} ms");
        if (preview is null) return new WeakReference(null);
        var weak = new WeakReference(preview);
        window.Content = preview;
        await preview.Loading;
        if (preview.IsClip)
        {
            // the library may still be indexing the game data: give the preview mesh up to a minute
            var wait = await Task.WhenAny(preview.MeshLoading, Task.Delay(60_000));
            ctx.Check(wait == preview.MeshLoading, $"{name}: preview mesh chosen or reported");
        }
        await PreviewTestKit.SettleAsync();
        ctx.Check(preview.Message is null, $"{name}: shows the file ({preview.Message ?? "no message"})");
        if (preview.IsClip)
        {
            ctx.Check(preview.Playback?.IsPlaying == true, $"{name}: the clip plays");
            ctx.Log($"{name}: mesh {preview.MeshName ?? "none"}; notice: {preview.Notice ?? "none"}");
            if (RfaModule.Workspace?.Assets.Snapshot.Meshes.Length > 0) ctx.Check(preview.MeshName is not null, $"{name}: plays on a library mesh ({preview.MeshName})");
        }
        else ctx.Check(preview.MeshName == name, $"{name}: mesh shown");
        int colours = PreviewTestKit.SavePng(preview, Path.Combine(shots, "rfa_" + name.Replace('.', '_') + ".png"));
        ctx.Log($"{name}: {colours} colours in the capture");
        ctx.Check(colours > 20, $"{name}: capture is not blank ({colours} colours)");
        window.Content = null;
        preview.Dispose();
        ctx.Check(preview.Playback is null, $"{name}: Dispose stops and drops playback");
        preview = null;
        return weak;
    }

    [SelfTest("rfa.previews-siblings")]
    public static async Task PreviewsUseSiblings(BaseContext ctx)
    {
        const string ClipName = "ult2_walk.rfa", StockMesh = "miner.v3c", MeshName = "cairnsib_miner.v3c";
        string? clipPath = SampleFiles().FirstOrDefault(p => Path.GetFileName(p).Equals(ClipName, StringComparison.OrdinalIgnoreCase));
        if (clipPath is null || ctx.Shell.Assets.Resolver.Resolve(StockMesh) is not { } stock) { ctx.Skip("corpus or game data not found"); return; }
        var resolver = ctx.Shell.Assets.Resolver;
        byte[] clipBytes = File.ReadAllBytes(clipPath);
        var mesh = V3dReader.Read(stock.ReadAllBytes(), StockMesh);
        int clipBones = RfaReader.Read(clipBytes, ClipName).BoneCount;
        if (mesh.Bones.Length != clipBones) { ctx.Skip($"{StockMesh} has {mesh.Bones.Length} bones, {ClipName} {clipBones}"); return; }
        // The stock mesh under a new name, its textures renamed to names no game data has; all of them only as siblings.
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var submeshes = mesh.Submeshes.ToList();
        for (int s = 0; s < submeshes.Count; s++)
        {
            for (int m = 0; m < submeshes[s].Materials.Length; m++)
            {
                string old = mesh.Submeshes.ElementAt(s).Materials[m].DiffuseMap.Text;
                if (old.Length == 0) continue;
                if (!renamed.TryGetValue(old, out string? unique))
                {
                    if (resolver.Resolve(old) is not { } hit) continue;
                    string stem = $"csib{renamed.Count}_{Path.GetFileNameWithoutExtension(old)}";
                    unique = stem[..Math.Min(stem.Length, 26)] + Path.GetExtension(old);
                    renamed[old] = unique;
                    files[Path.ChangeExtension(unique, Path.GetExtension(hit.ResolvedName))] = hit.ReadAllBytes();
                }
                mesh = Cairn.Rfa.Editing.MeshEdit.SetTextureName(mesh, s, m, unique);
            }
        }
        files[MeshName] = V3dWriter.Write(mesh);
        foreach (var name in files.Keys) ctx.Check(resolver.Resolve(name) is null, $"{name}: not in the game data");
        var textures = files.Keys.Where(n => !n.Equals(MeshName, StringComparison.OrdinalIgnoreCase)).ToList();
        // the viewport shows the first LOD only, so the "-mipN" textures of the lower LODs are never read
        var shown = textures.Where(n => !n.Contains("-mip", StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Check(shown.Count > 0, $"{textures.Count} textures renamed, {shown.Count} on the first LOD");
        var module = Module(ctx);
        string shots = PreviewTestKit.ShotsFolder(ctx);
        var window = PreviewTestKit.Host();
        try
        {
            // the clip plays on the sibling mesh, not the library's pick
            var clipSiblings = Cairn.Ui.Modules.AssetSiblings.FromMemory("siblings.vpp", files);
            var clip = (RfaPreview)module.CreatePreview(clipBytes, ClipName, clipSiblings)!;
            window.Content = clip;
            await clip.Loading;
            ctx.Check(await Task.WhenAny(clip.MeshLoading, Task.Delay(60_000)) == clip.MeshLoading, "clip: preview mesh chosen");
            await PreviewTestKit.SettleAsync();
            ctx.Log($"clip: mesh {clip.MeshName ?? "none"}; notice {clip.Notice ?? "none"}; reads {string.Join(", ", files.Keys.Select(n => $"{n}={clipSiblings.ReadCount(n)}"))}");
            ctx.Check(string.Equals(clip.MeshName, MeshName, StringComparison.OrdinalIgnoreCase), $"clip: plays on the sibling mesh ({clip.MeshName})");
            ctx.Check(clipSiblings.ReadCount(MeshName) > 0 && shown.All(n => clipSiblings.ReadCount(n) > 0), $"clip: the mesh and the {shown.Count} textures of the shown LOD were read from the siblings");
            int colours = PreviewTestKit.SavePng(clip, Path.Combine(shots, "rfa_siblings_clip.png"));
            ctx.Check(colours > 20, $"clip: capture is not blank ({colours} colours)");
            window.Content = null;
            clip.Dispose();

            // the renamed mesh itself, with and without the siblings
            var meshSiblings = Cairn.Ui.Modules.AssetSiblings.FromMemory("siblings.vpp", files);
            var meshPreview = (RfaPreview)module.CreatePreview(files[MeshName], MeshName, meshSiblings)!;
            window.Content = meshPreview;
            await meshPreview.Loading;
            await PreviewTestKit.SettleAsync();
            ctx.Check(shown.All(n => meshSiblings.ReadCount(n) > 0), $"mesh: the {shown.Count} textures of the shown LOD were read from the siblings");
            PreviewTestKit.SavePng(meshPreview, Path.Combine(shots, "rfa_siblings_mesh.png"));
            window.Content = null;
            meshPreview.Dispose();
            var bare = (RfaPreview)module.CreatePreview(files[MeshName], MeshName)!;
            window.Content = bare;
            await bare.Loading;
            await PreviewTestKit.SettleAsync();
            ctx.Check(bare.Message is null, "mesh without siblings still shows (textures missing)");
            PreviewTestKit.SavePng(bare, Path.Combine(shots, "rfa_siblings_mesh_without.png"));
            window.Content = null;
            bare.Dispose();
        }
        finally { window.Close(); }
    }

    [SelfTest("rfa.previews-rapid")]
    public static async Task RapidCreateDispose(BaseContext ctx)
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
    private static async Task<List<WeakReference>> CycleAsync(RfaModule module, Window window, List<string> files, byte[][] bytes, int count)
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

    [SelfTest("rfa.previews-corrupt")]
    public static async Task CorruptShowsMessage(BaseContext ctx)
    {
        var files = SampleFiles();
        var module = Module(ctx);
        foreach (string ext in new[] { ".v3c", ".rfa" })
        {
            string? sample = files.FirstOrDefault(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            byte[] good = sample is null ? new byte[64] : File.ReadAllBytes(sample);
            foreach (var (what, bytes) in PreviewTestKit.Corrupt(good))
            {
                var preview = (RfaPreview)module.CreatePreview(bytes, "broken" + ext)!;
                await preview.Loading;
                ctx.Check(preview.Message?.StartsWith("Cannot preview broken" + ext, StringComparison.Ordinal) == true, $"{ext} {what}: message shown ({preview.Message ?? "none"})");
                preview.Dispose();
            }
        }
        ctx.Check(module.CreatePreview([1, 2, 3], "x.vfx") is null, "another type gives null");
    }
}
