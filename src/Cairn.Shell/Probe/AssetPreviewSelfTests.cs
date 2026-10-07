#if CAIRN_MODULE_VPP
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;

namespace Cairn.Shell;

/// <summary>
/// The shared preview library (<c>Cairn.Previews</c>) previewing files by NAME through the shell's game data, as
/// the table module does for the file names a table references: a texture named <c>.tga</c> that only exists as
/// <c>.dds</c>, a mesh, an effect, a sound, a table, and a name that does not exist. Each pane renders, is disposed
/// and is collected afterwards.
/// </summary>
internal static class AssetPreviewSelfTests
{
    [SelfTest("previews.show-asset", Order = 950)]
    public static async Task ShowAssetByName(SelfTestContext ctx)
    {
        var host = ctx.Shell.Assets;
        if (!host.HasSources)
        {
            ctx.Skip("no game folder or search folders are set");
            return;
        }
        await host.ArchivesIndexed;
        var resolver = host.Resolver;
        var watch = Stopwatch.StartNew();
        // The engine's request-name mapping, applied by AssetLookup for every caller.
        ctx.Check(AssetLookup.EngineNames(@"anims\walk.idle.MVF").SequenceEqual(["walk.rfa"]), ".mvf maps to <stem>.rfa cut at the first dot");
        ctx.Check(AssetLookup.EngineNames("miner.vcm").SequenceEqual(["miner.v3c"]), ".vcm maps to .v3c");
        ctx.Check(AssetLookup.EngineNames("chair.v3d").SequenceEqual(["chair.v3m", "chair.v3c"]), ".v3d maps to .v3m then .v3c");
        ctx.Check(AssetLookup.EngineNames("wall.tga").SequenceEqual(["wall.tga"]) && AssetLookup.EngineNames(@"folder\").Count == 0, "other names pass through; an empty name maps to nothing");
        var found = await Task.Run(() => resolver.Enumerate([".dds", ".tga", ".v3m", ".vfx", ".wav", ".tbl", ".rfa"]));
        ctx.Log($"  {found.Count} candidate files listed in {watch.ElapsedMilliseconds} ms");
        var names = found.Select(l => l.ResolvedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? First(string ext, Func<string, bool>? also = null) =>
            found.Select(l => l.ResolvedName).FirstOrDefault(n => n.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && (also?.Invoke(n) ?? true));

        var cases = new List<(string Label, string? Name, AssetPreviewKind[] Expected)>
        {
            ("texture by .tga name, only a .dds exists", First(".dds", n => !names.Contains(Path.ChangeExtension(n, ".tga"))) is { } dds ? Path.ChangeExtension(dds, ".tga") : null, [AssetPreviewKind.Image]),
            ("mesh", First(".v3m"), ModuleOrHex(ctx.Shell, ".v3m")),
            ("effect", First(".vfx"), ModuleOrHex(ctx.Shell, ".vfx")),
            ("sound", First(".wav"), [AssetPreviewKind.Audio]),
            ("table", First(".tbl"), [AssetPreviewKind.Text, AssetPreviewKind.Module]), // Module when the table module is loaded
            ("animation by legacy .mvf name", First(".rfa", n => n.IndexOf('.') == n.Length - 4) is { } rfa ? Path.ChangeExtension(rfa, ".mvf") : null, ModuleOrHex(ctx.Shell, ".rfa")),
            ("missing", "cairn_no_such_file_7f3a.v3m", [AssetPreviewKind.NotFound]),
        };
        string shots = Path.Combine(Path.GetTempPath(), "cairn-preview-shots");
        if (ctx.Options.TryGetValue("preview-shots", out var dir) && !string.IsNullOrWhiteSpace(dir)) shots = dir;
        Directory.CreateDirectory(shots);

        foreach (var (label, name, expected) in cases)
        {
            if (!ctx.Check(name is not null, $"{label}: a file of this kind exists in the game data")) continue;
            var (weak, kind, detail, error) = await ShowAsync(ctx, name!, Path.Combine(shots, "asset-" + label.Split(' ')[0] + ".png"));
            ctx.Log($"  {label}: {name} -> {kind}; {detail}");
            ctx.Check(error is null, $"{label}: shown without exception{(error is null ? "" : ": " + error)}");
            ctx.Check(expected.Contains(kind), $"{label}: {name} shows as {string.Join(" or ", expected)} ({kind})");
            if (name!.EndsWith(".mvf", StringComparison.OrdinalIgnoreCase))
                ctx.Check(detail.Contains(".rfa", StringComparison.OrdinalIgnoreCase), $"{label}: resolved as the .rfa ({detail})");
            bool collected = await CollectedAsync(weak);
            ctx.Check(collected, $"{label}: preview and details collectable after dispose");
            if (!collected) ctx.Log("  GC root of the " + label + " panes: " + ctx.DescribeRoot(weak.Target!));
        }
    }

    private static AssetPreviewKind[] ModuleOrHex(IShellContext shell, string ext) =>
        shell.Modules.OfType<IAssetPreviewProvider>().Any(p => p.CanPreview("x" + ext)) ? [AssetPreviewKind.Module] : [AssetPreviewKind.Hex];

    // Never inlined, so the panes are referenced only from this frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<(WeakReference Weak, AssetPreviewKind Kind, string Detail, string? Error)> ShowAsync(SelfTestContext ctx, string name, string png)
    {
        var window = new Window { Width = 900, Height = 760, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Asset preview self-test" };
        window.SetResourceReference(Control.BackgroundProperty, "App.WindowBackground");
        window.Show();
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.6, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.4, GridUnitType.Star) });
        var preview = new AssetPreviewPane(ctx.Shell);
        var details = new AssetDetailsPane(ctx.Shell);
        Grid.SetRow(details, 1);
        grid.Children.Add(preview);
        grid.Children.Add(details);
        window.Content = grid;
        var weak = new WeakReference(preview);
        string? error = null;
        string detail = "";
        try
        {
            preview.ShowAsset(name);
            await preview.Pending;
            await SettleAsync();
            if (preview.Lookup is { } lookup)
            {
                details.ShowDetails(AssetDetails.ForLookup(lookup));
                detail = lookup.Describe();
                if (lookup.Found && details.Rows.Count < 3) error = "too few detail rows";
                if (!lookup.Found && details.Warnings.Count == 0) error = "no not-found warning";
            }
            else error = "no lookup result";
            await SettleAsync();
            if (SavePng(grid, png) <= 2) error ??= "blank capture";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error = ex.GetType().Name + ": " + ex.Message;
        }
        var kind = preview.Kind;
        preview.Dispose();
        details.Dispose();
        window.Content = null;
        window.Close();
        return (weak, kind, detail, error);
    }

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

    private static async Task IdleAsync(int times = 2)
    {
        for (int i = 0; i < times; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
}
#endif
