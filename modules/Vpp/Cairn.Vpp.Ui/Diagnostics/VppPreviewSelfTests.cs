using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Self-tests of the preview and details panes on real packfile entries from the game folder (read
/// only): one entry of every type found, rapid selection changes, and audio playback. Captures go to
/// <c>--preview-shots &lt;folder&gt;</c> (else a temp folder).
/// </summary>
internal static class VppPreviewSelfTests
{
    /// <summary>Packfiles scanned at most when looking for one entry of each type.</summary>
    private const int MaxPackfiles = 5000;
    /// <summary>Time spent scanning at most.</summary>
    private static readonly TimeSpan MaxScan = TimeSpan.FromSeconds(60);

    private sealed record Sample(VppPackage Package, VppItem Item);

    [SelfTest("vpp.preview-types", Order = 300)]
    public static async Task EveryType(SelfTestContext ctx)
    {
        var samples = FindSamples(ctx, out var animatedVbm, out _);
        if (samples is null) return;
        string shots = ShotsFolder(ctx);
        ctx.Log($"  {samples.Count} types: {string.Join(" ", samples.Keys.Order())}; shots in {shots}");
        var cases = samples.Select(s => (Name: s.Key.TrimStart('.'), Items: (IReadOnlyList<VppItem>)[s.Value.Item], s.Value.Package)).ToList();
        if (animatedVbm is not null) cases.Add(("vbm-animated", [animatedVbm.Item], animatedVbm.Package));
        var multi = samples.Values.Take(6).ToList();
        if (multi.Count > 1) cases.Add(("multi-select", multi.Select(m => m.Item).ToList(), multi[0].Package));

        foreach (var (name, items, package) in cases)
        {
            var (weak, error) = await CaptureAsync(ctx, items, package, Path.Combine(shots, name + "-dark.png"), name);
            ctx.Check(error is null, $"{name}: preview and details without exception{(error is null ? "" : ": " + error)}");
            bool collected = await CollectedAsync(weak);
            ctx.Check(collected, $"{name}: preview area collectable after dispose");
            if (!collected) LogRoot(ctx, name, weak);
        }

        // A few in the light theme.
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        theme.Apply(AppTheme.Light);
        try
        {
            foreach (var (name, items, package) in cases.Where(c => c.Name is "tga" or "rfl" or "tbl" or "ogg" or "vbm-animated" or "v3m" or "vfx"))
            {
                var (_, error) = await CaptureAsync(ctx, items, package, Path.Combine(shots, name + "-light.png"), name);
                ctx.Check(error is null, $"{name} (light): no exception{(error is null ? "" : ": " + error)}");
            }
        }
        finally
        {
            theme.Apply(before);
        }
    }

    [SelfTest("vpp.preview-rapid", Order = 301)]
    public static async Task RapidSelection(SelfTestContext ctx)
    {
        if (FindSamples(ctx, out _, out var largest) is null || largest is null) return;
        var items = largest.Items.Where(i => i.Size < (8 << 20)).Take(100).ToList();
        ctx.Log($"  {Path.GetFileName(largest.Path)}: {largest.Count} entries, cycling {items.Count}");
        double startMb = ManagedMegabytes();
        var window = Host(900, 700);
        var area = new VppPreviewArea(ctx.Shell);
        window.Content = area;
        Exception? failure = null;
        try
        {
            var random = new Random(7);
            for (int i = 0; i < items.Count; i++)
            {
                area.Show(items[i], [items[i]], largest);
                // Mostly faster than the debounce (arrowing), sometimes slower so loads start and get cancelled.
                await Task.Delay(i % 10 == 9 ? 160 : random.Next(0, 40));
            }
            await area.SettleAsync();
            await IdleAsync();
        }
        catch (Exception ex) { failure = ex; }
        ctx.Check(failure is null, "100 rapid selection changes without exception" + (failure is null ? "" : ": " + failure));
        ctx.Check(ReferenceEquals(area.Preview.Item, items[^1]), $"last selection wins ({area.Preview.Item?.Name} = {items[^1].Name})");
        ctx.Check(area.Preview.Kind != AssetPreviewKind.Loading, $"preview settled ({area.Preview.Kind})");
        ctx.Check(area.Details.Rows.FirstOrDefault()?.Value == items[^1].Name, "details show the last selection");
        area.Dispose();
        window.Close();
        double endMb = ManagedMegabytes();
        ctx.Log($"  managed memory {startMb:0.0} MB -> {endMb:0.0} MB");
        ctx.Check(endMb - startMb < 64, $"memory bounded ({endMb - startMb:+0.0;-0.0} MB)");
    }

    [SelfTest("vpp.preview-audio", Order = 302)]
    public static async Task AudioPlayback(SelfTestContext ctx)
    {
        var samples = FindSamples(ctx, out _, out _);
        if (samples is null) return;
        foreach (string ext in new[] { ".ogg", ".aif" })
        {
            if (!samples.TryGetValue(ext, out var sample)) { ctx.Log($"  no {ext} entry found"); continue; }
            var window = Host(700, 300);
            var area = new VppPreviewArea(ctx.Shell);
            window.Content = area;
            area.Show(sample.Item, [sample.Item], sample.Package, immediate: true);
            await area.SettleAsync();
            await IdleAsync();
            if (!ctx.Check(area.Preview.View is AudioPreview, $"{sample.Item.Name}: audio preview ({area.Preview.Kind})"))
            {
                area.Dispose();
                window.Close();
                continue;
            }
            var audio = (AudioPreview)area.Preview.View!;
            audio.Play();
            var watch = Stopwatch.StartNew();
            // generous: in a long combined run, the media pipeline can take a few seconds to open the first file
            while (watch.ElapsedMilliseconds < 6000 && audio.PlayerPosition < TimeSpan.FromMilliseconds(400)) await Task.Delay(50);
            var position = audio.PlayerPosition;
            ctx.Check(audio.Failure is null, $"{sample.Item.Name}: plays ({audio.Failure ?? "no media failure"})");
            ctx.Check(position > TimeSpan.FromMilliseconds(200), $"{sample.Item.Name}: position advances ({position.TotalSeconds:0.00} s after {watch.ElapsedMilliseconds} ms)");
            var temp = Directory.GetFiles(AudioData.TempFolder);
            area.Dispose();
            ctx.Check(!audio.IsPlaying && audio.PlayerPosition == TimeSpan.Zero, $"{sample.Item.Name}: stopped on dispose");
            await IdleAsync();
            ctx.Check(Directory.GetFiles(AudioData.TempFolder).Length < temp.Length, $"{sample.Item.Name}: temporary file removed");
            window.Close();
        }
    }

    [SelfTest("vpp.preview-in-tab", Order = 304)]
    public static async Task InTab(SelfTestContext ctx)
    {
        var samples = FindSamples(ctx, out _, out _);
        if (samples is null) return;
        if (!samples.TryGetValue(".tga", out var sample)) { ctx.Skip("no .tga entry found"); return; }
        if (!ctx.Check(ctx.Shell.OpenFile(sample.Package.Path!), "packfile opens in a tab")) return;
        await ctx.SettleAsync(TimeSpan.FromSeconds(20));
        var doc = ctx.Shell.Documents.OfType<Documents.VppDocument>().FirstOrDefault(d => string.Equals(d.FilePath, sample.Package.Path, StringComparison.OrdinalIgnoreCase));
        if (!ctx.Check(doc is not null, "packfile document found")) return;
        var view = (Documents.VppDocumentView)doc!.View;
        var item = doc.Current.Find(sample.Item.Name)!;
        doc.SetSelection([item]);
        await Task.Delay(VppPreviewController.Debounce + TimeSpan.FromMilliseconds(100));
        var pane = view.PreviewHost.Content as VppPreviewPane;
        var details = view.DetailsHost.Content as Details.VppDetailsPane;
        if (pane is not null) await pane.Pending;
        if (details is not null) await details.Pending;
        await SettleAsync();
        ctx.Check(pane?.Kind == AssetPreviewKind.Image, $"selecting {item.Name} shows its image in the tab ({pane?.Kind})");
        ctx.Check(details?.Rows.FirstOrDefault()?.Value == item.Name, "the tab's details show the entry");
        SavePng(view, Path.Combine(ShotsFolder(ctx), "in-tab-dark.png"));
        ctx.Shell.Close(doc);
        await IdleAsync();
        ctx.Check(view.PreviewHost.Content is null && pane?.View is null, "closing the tab releases the preview");
    }

    [SelfTest("vpp.preview-siblings", Order = 303)]
    public static async Task SiblingTextures(SelfTestContext ctx)
    {
        string? game = LocalPaths.GameDirectory;
        string maps = game is null ? "" : Path.Combine(game, "user_maps");
        if (!Directory.Exists(maps)) { ctx.Skip("no user_maps folder in the game folder"); return; }
        var resolver = ctx.Shell.Assets.Resolver;
        var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tga", ".dds", ".vbm" };

        // Static meshes from user packfiles that carry textures the game data does not have.
        var candidates = new List<(VppPackage Package, VppItem Mesh, int OnlyHere)>();
        var scan = Stopwatch.StartNew();
        foreach (var file in Directory.EnumerateFiles(maps, "*.vpp", SearchOption.AllDirectories))
        {
            if (candidates.Count >= 6 || scan.Elapsed > MaxScan) break;
            VppPackage package;
            try { package = VppPackage.Open(file); }
            catch (Exception ex) when (ex is IOException or Cairn.Formats.AssetFormatException or UnauthorizedAccessException) { continue; }
            var meshes = package.Items.Where(i => i.Extension is ".v3m" or ".v3c" && i.Size is > 0 and < (4 << 20)).Take(2).ToList();
            var textures = package.Items.Where(i => images.Contains(i.Extension)).Take(40).ToList();
            if (meshes.Count == 0 || textures.Count == 0) continue;
            int onlyHere = await Task.Run(() => textures.Count(t => resolver.Resolve(t.Name) is null));
            if (onlyHere == 0) continue;
            foreach (var mesh in meshes) candidates.Add((package, mesh, onlyHere));
        }
        ctx.Log($"  {candidates.Count} candidate meshes in {scan.Elapsed.TotalSeconds:0.0} s");
        if (candidates.Count == 0) { ctx.Skip("no user packfile with a mesh and textures missing from the game data"); return; }

        string shots = ShotsFolder(ctx);
        int differing = 0;
        foreach (var (package, mesh, onlyHere) in candidates)
        {
            var with = await RenderAsync(ctx, mesh, package, Path.Combine(shots, "siblings-with-" + Path.GetFileNameWithoutExtension(mesh.Name) + ".png"));
            var without = await RenderAsync(ctx, mesh, null, Path.Combine(shots, "siblings-without-" + Path.GetFileNameWithoutExtension(mesh.Name) + ".png"));
            if (with is null || without is null || with.Length != without.Length) { ctx.Log($"  {mesh.Name}: no module preview"); continue; }
            int changed = 0;
            for (int i = 0; i < with.Length; i++) if (with[i] != without[i]) changed++;
            double share = (double)changed / with.Length;
            ctx.Log($"  {Path.GetFileName(package.Path)} / {mesh.Name} ({onlyHere} textures only in the packfile): {share:P1} of pixels differ with the packfile's entries");
            if (share > 0.005) differing++;
        }
        ctx.Check(differing > 0, $"a mesh preview uses textures found only in its own packfile ({differing} of {candidates.Count})");
    }

    /// <summary>The pixels of a module preview of <paramref name="item"/> (null when it is not a module preview).</summary>
    private static async Task<int[]?> RenderAsync(SelfTestContext ctx, VppItem item, VppPackage? package, string path)
    {
        var window = Host(700, 520);
        var area = new VppPreviewArea(ctx.Shell);
        window.Content = area;
        try
        {
            area.Show(item, [item], package, immediate: true);
            await area.SettleAsync();
            await Task.Delay(250);
            await SettleAsync();
            if (area.Preview.Kind != AssetPreviewKind.Module || area.Preview.View is not FrameworkElement view) return null;
            SavePng(view, path);
            view.UpdateLayout();
            int w = (int)Math.Max(1, view.ActualWidth), h = (int)Math.Max(1, view.ActualHeight);
            var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(view);
            var pixels = new int[w * h];
            rtb.CopyPixels(pixels, w * 4, 0);
            return pixels;
        }
        finally
        {
            area.Dispose();
            window.Content = null;
            window.Close();
        }
    }

    /// <summary>One entry per type from the game's packfiles, the first animated VBM, and the packfile with the most entries.</summary>
    private static Dictionary<string, Sample>? FindSamples(SelfTestContext ctx, out Sample? animatedVbm, out VppPackage? largest)
    {
        animatedVbm = null;
        largest = null;
        string? game = LocalPaths.GameDirectory;
        if (game is null || !Directory.Exists(game))
        {
            ctx.Skip("no game folder (" + LocalPaths.HowToSet(LocalPaths.GameDirectoryVariable, "gameDirectory") + ")");
            return null;
        }
        var found = new Dictionary<string, Sample>(StringComparer.OrdinalIgnoreCase);
        var files = Directory.EnumerateFiles(game, "*.vpp", SearchOption.TopDirectoryOnly)
            .Concat(Directory.Exists(Path.Combine(game, "user_maps")) ? Directory.EnumerateFiles(Path.Combine(game, "user_maps"), "*.vpp", SearchOption.AllDirectories) : [])
            .Take(MaxPackfiles);
        var scan = Stopwatch.StartNew();
        int scanned = 0;
        foreach (var file in files)
        {
            if (scan.Elapsed > MaxScan) break;
            scanned++;
            VppPackage package;
            try { package = VppPackage.Open(file); }
            catch (Exception ex) when (ex is IOException or Cairn.Formats.AssetFormatException or UnauthorizedAccessException) { continue; }
            // The rapid test wants a large packfile with a mix of types (not the all-audio one).
            if ((largest is null || package.Count > largest.Count) && package.Items.Select(i => i.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 4)
                largest = package;
            foreach (var item in package.Items)
            {
                string ext = item.Extension.ToLowerInvariant();
                if (ext.Length == 0 || item.Size == 0 || item.Size > (12 << 20)) continue;
                if (!found.ContainsKey(ext)) found[ext] = new Sample(package, item);
                if (animatedVbm is null && ext == ".vbm" && item.Size < (2 << 20))
                {
                    try
                    {
                        if (VbmCodec.ReadInfo(item.Source.ReadAll(), item.Name).FrameCount > 1) animatedVbm = new Sample(package, item);
                    }
                    catch (Exception ex) when (ex is IOException or ImageDecodeException) { }
                }
            }
        }
        ctx.Log($"  scanned {scanned} packfiles in {scan.Elapsed.TotalSeconds:0.0} s");
        if (found.Count == 0)
        {
            ctx.Skip("no packfiles in the game folder");
            return null;
        }
        return found;
    }

    /// <summary>Shows <paramref name="items"/> in a fresh area, renders it to <paramref name="path"/> and disposes it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Weak, string? Error)> CaptureAsync(SelfTestContext ctx, IReadOnlyList<VppItem> items, VppPackage package, string path, string name)
    {
        var window = Host(900, 760);
        var area = new VppPreviewArea(ctx.Shell);
        window.Content = area;
        var weak = new WeakReference(area);
        string? error = null;
        try
        {
            area.Show(items[0], items, package, immediate: true);
            await area.SettleAsync();
            await Task.Delay(name.Contains("vbm", StringComparison.Ordinal) ? 450 : 250);
            await SettleAsync();
            int colours = SavePng(area, path);
            ctx.Log($"  {name}: {items[0].Name} -> {area.Preview.Kind}, {area.Details.Rows.Count} detail rows, {area.Details.Warnings.Count} warnings, {colours} colours");
            // an image a module opens (a .vbm) sits under an "Open in Cairn" bar
            if (((area.Preview.View as ModulePreviewHost)?.Inner ?? area.Preview.View) is ImagePreview { } image && name.Contains("animated", StringComparison.Ordinal))
            {
                int first = image.Frame;
                var watch = Stopwatch.StartNew();
                while (image.Frame == first && watch.ElapsedMilliseconds < 2500) await Task.Delay(50);
                ctx.Check(image.IsPlaying && image.Frame != first, $"{name}: animation playing (frame {first + 1} -> {image.Frame + 1} in {watch.ElapsedMilliseconds} ms)");
            }
            if (colours <= 2) error = "blank capture";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            area.Dispose();
            window.Content = null;
            window.Close();
        }
        return (weak, error);
    }

    private static string ShotsFolder(SelfTestContext ctx)
    {
        string dir = ctx.Options.TryGetValue("preview-shots", out var d) && !string.IsNullOrWhiteSpace(d) ? d : Path.Combine(Path.GetTempPath(), "cairn-preview-shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static Window Host(int width, int height)
    {
        var w = new Window { Width = width, Height = height, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Packfile preview self-test" };
        w.SetResourceReference(Control.BackgroundProperty, "App.WindowBackground");
        w.Show();
        return w;
    }

    private static async Task IdleAsync(int times = 2)
    {
        for (int i = 0; i < times; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Waits for preview work (ours and other modules' embedded previews) to finish, then a few frames.</summary>
    private static async Task SettleAsync()
    {
        var watch = Stopwatch.StartNew();
        for (int pass = 0; pass < 2; pass++)
        {
            await Task.Delay(150);
            await IdleAsync();
            while (watch.Elapsed < TimeSpan.FromSeconds(15) && BusyTracker.Describe().Any(d => d.Contains("preview", StringComparison.OrdinalIgnoreCase) || d.StartsWith("texture", StringComparison.OrdinalIgnoreCase)))
                await Task.Delay(30);
        }
        await IdleAsync();
    }

    private static int SavePng(FrameworkElement element, string path)
    {
        element.UpdateLayout();
        int w = (int)Math.Max(1, element.ActualWidth), h = (int)Math.Max(1, element.ActualHeight);
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using (var stream = File.Create(path)) encoder.Save(stream);
        var pixels = new int[w * h];
        rtb.CopyPixels(pixels, w * 4, 0);
        return pixels.Distinct().Count();
    }

    // Never inlined, so the strong reference taken for the walk lives only in this frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogRoot(SelfTestContext ctx, string name, WeakReference weak)
    {
        if (weak.Target is { } o) ctx.Log($"  GC root of the {name} preview area:{Environment.NewLine}{ctx.DescribeRoot(o)}");
    }

    private static async Task<bool> CollectedAsync(WeakReference weak)
    {
        for (int i = 0; i < 8 && weak.IsAlive; i++)
        {
            await IdleAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (weak.IsAlive) await Task.Delay(50);
        }
        return !weak.IsAlive;
    }

    private static double ManagedMegabytes()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true) / (1024.0 * 1024.0);
    }
}
