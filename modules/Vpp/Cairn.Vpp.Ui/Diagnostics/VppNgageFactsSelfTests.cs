using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.XForge;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.XForge;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// The facts of Red Faction for N-Gage's particle systems (<c>.pr3</c>) and scenes (<c>.xff</c>) in the packfile tab
/// (<c>Cairn.exe --probe-module --selftest-only vpp.ngage-facts*</c>): a synthetic archive always, and the game's
/// <c>redfaction.cfl</c> when it is configured (only read). <c>explosion.pr3</c>, <c>level_01a.xff</c> and
/// <c>enemy_ug.xff</c> are selected in the tab and captured dark and light to <c>--preview-shots &lt;folder&gt;</c> (else
/// a temp folder); the facts must show whether or not a module previews the scene.
/// </summary>
internal static class VppNgageFactsSelfTests
{
    private static readonly string[] Shown = ["explosion.pr3", "level_01a.xff", "enemy_ug.xff"];

    [SelfTest("vpp.ngage-facts", Order = 312)]
    public static async Task Synthetic(SelfTestContext ctx)
    {
        string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "cairn-ngage-facts-" + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            string path = Path.Combine(folder, "redfaction.cfl");
            File.WriteAllBytes(path, SyntheticNgage.Archive());
            await CheckArchiveAsync(ctx, path, "ngage-facts-synthetic", real: false);
        }
        finally
        {
            Work.VppWorkFolder.TryDeleteFolder(folder);
        }
    }

    [SelfTest("vpp.ngage-facts-real", Order = 313)]
    public static async Task Real(SelfTestContext ctx)
    {
        string? dir = LocalPaths.NgageDirectory;
        string? path = dir is null ? null : Path.Combine(dir, "redfaction.cfl");
        if (path is null || !File.Exists(path)) { ctx.Skip("no Red Faction for N-Gage archive (" + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory") + ")"); return; }
        var stamp = VppArchiveStamp.Of(path);
        await CheckArchiveAsync(ctx, path, "ngage-facts", real: true);
        ctx.Check(VppArchiveStamp.Of(path) == stamp, "the archive was never written");
    }

    private static async Task CheckArchiveAsync(SelfTestContext ctx, string path, string shot, bool real)
    {
        if (!ctx.Check(ctx.Shell.OpenFile(path), $"the shell opens {Path.GetFileName(path)}")) return;
        await ctx.SettleAsync(TimeSpan.FromSeconds(20));
        var doc = ctx.Shell.Documents.OfType<VppDocument>().FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (!ctx.Check(doc?.Current.Cfl is not null, "the archive is open in a tab as an X-Forge archive")) return;
        try
        {
            var package = doc!.Current;
            // the entries' facts: every particle system and scene reads, and no RF1-only warning shows on any entry
            var context = new VppFactsContext(package);
            var sheets = await Task.Run(() => package.Items.Select(i => (Item: i, Sheet: VppFacts.Describe(i, context))).ToList());
            var pr3 = sheets.Where(s => Pr3File.HasExtension(s.Item.Name)).ToList();
            var xff = sheets.Where(s => XffFile.HasExtension(s.Item.Name)).ToList();
            ctx.Check(pr3.Count == (real ? 7 : 1) && pr3.All(s => s.Sheet["Error"] is null && s.Sheet["Lifetime"] is not null),
                $"every particle system's facts read ({pr3.Count}: {string.Join(", ", pr3.Select(s => s.Item.Name))})");
            ctx.Check(xff.Count == (real ? 264 : 3) && xff.All(s => s.Sheet["Error"] is null && s.Sheet["Scene: Kind"] is not null),
                $"every scene's facts read ({xff.Count}; {string.Join(", ", xff.GroupBy(s => s.Sheet["Scene: Kind"]?.Split(' ')[0]).Select(g => $"{g.Count()} {g.Key}"))})");
            var warnings = sheets.SelectMany(s => s.Sheet.Warnings.Select(w => $"{s.Item.Name}: {w}")).ToList();
            foreach (string w in warnings) ctx.Log("  " + w);
            ctx.Check(warnings.All(w => w.Contains(" not in this archive", StringComparison.Ordinal)),
                $"the only warnings on the entries are files missing from the archive ({warnings.Count})");
            ctx.Check(doc.Problems.All(p => p.Code.StartsWith("CFL", StringComparison.Ordinal)), $"the archive's problems are CFL ones only ({string.Join(", ", doc.Problems.Select(p => p.Code).Distinct())})");
            var hud = package.Items.FirstOrDefault(i => i.Name.StartsWith("pic_textbox_red", StringComparison.OrdinalIgnoreCase));
            if (hud is not null)
                ctx.Check(!sheets.First(s => ReferenceEquals(s.Item, hud)).Sheet.Warnings.Any(), $"{hud.Name} (a HUD image of any size) gets no texture-size warning");
            var lines = package.Items.Where(i => Pr3File.HasExtension(i.Name) || XffFile.HasExtension(i.Name)).Select(i => (i.Name, Line: VppInfo.Summarize(i))).ToList();
            ctx.Check(lines.All(l => !l.Line.IsUnreadable && l.Line.Text.Length > 0), $"the Info column summarises them ({string.Join("; ", lines.Where(l => Shown.Contains(l.Name, StringComparer.OrdinalIgnoreCase)).Select(l => $"{l.Name}: {l.Line.Text}"))})");

            // the three entries selected in the tab, dark and light
            var view = (VppDocumentView)doc.View;
            string shots = ShotsFolder(ctx);
            var theme = ctx.Shell.Theme;
            var before = theme.Requested;
            foreach (string name in Shown)
            {
                var item = package.Find(name);
                if (!ctx.Check(item is not null, $"{name} is in the archive")) continue;
                foreach (var light in new[] { false, true })
                {
                    theme.Apply(light ? AppTheme.Light : AppTheme.Dark);
                    try
                    {
                        doc.SetSelection([item!]);
                        await Task.Delay(VppPreviewController.Debounce + TimeSpan.FromMilliseconds(100));
                        if (view.PreviewHost.Content is VppPreviewPane pane) await pane.Pending;
                        var details = view.DetailsHost.Content as VppDetailsPane;
                        if (details is not null) await details.Pending;
                        await SettleAsync();
                        string stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
                        int colours = SavePng(view, Path.Combine(shots, $"{shot}-{stem}-{(light ? "light" : "dark")}.png"));
                        ctx.Check(colours > 2, $"{name} ({(light ? "light" : "dark")}): the capture is not blank ({colours} colours)");
                        if (details is null) { ctx.Check(false, "the tab has a details pane"); continue; }
                        string Row(string label) => details.Rows.FirstOrDefault(r => r.Label == label)?.Value ?? "(none)";
                        bool scene = XffFile.HasExtension(name);
                        ctx.Check(scene ? Row("Kind").StartsWith(stem.StartsWith("level", StringComparison.Ordinal) ? "Level" : "Character", StringComparison.Ordinal) && Row("Triangles") != "(none)"
                            : Row("Lifetime").EndsWith("ms (likely)", StringComparison.Ordinal) && details.Rows.Any(r => r.Section == "Unknown fields") && details.Rows.Any(r => r.Section == "Frames"),
                            $"{name} ({(light ? "light" : "dark")}): the details show its facts ({(scene ? $"{Row("Kind")}; {Row("Triangles")} triangles; {Row("Animation")}" : $"{Row("Lifetime")}; {Row("Frames")} frames")})");
                    }
                    finally
                    {
                        theme.Apply(before);
                    }
                }
                // the full sheet, in a tall preview area of its own (the tab shows its first rows)
                await CaptureFullAsync(ctx, item!, package, Path.Combine(shots, $"{shot}-{Path.GetFileNameWithoutExtension(name).ToLowerInvariant()}-full-dark.png"));
            }
        }
        finally
        {
            ctx.Shell.Close(doc!);
        }
    }

    private static async Task CaptureFullAsync(SelfTestContext ctx, VppItem item, VppPackage package, string path)
    {
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        theme.Apply(AppTheme.Dark);
        var window = new Window { Width = 900, Height = 1400, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "N-Gage facts self-test" };
        window.SetResourceReference(Control.BackgroundProperty, "App.WindowBackground");
        window.Show();
        var area = new VppPreviewArea(ctx.Shell);
        window.Content = area;
        try
        {
            area.Show(item, [item], package, immediate: true);
            await area.SettleAsync();
            await SettleAsync();
            ctx.Check(area.Details.Rows.Count > 10 && SavePng(area, path) > 2, $"{item.Name}: the full facts ({area.Details.Rows.Count} rows, {area.Details.Warnings.Count} warnings)");
        }
        finally
        {
            area.Dispose();
            window.Content = null;
            window.Close();
            theme.Apply(before);
        }
    }

    private static string ShotsFolder(SelfTestContext ctx)
    {
        string dir = ctx.Options.TryGetValue("preview-shots", out var d) && !string.IsNullOrWhiteSpace(d) ? d : Path.Combine(Path.GetTempPath(), "cairn-preview-shots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task SettleAsync()
    {
        var watch = Stopwatch.StartNew();
        await Task.Delay(150);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        while (watch.Elapsed < TimeSpan.FromSeconds(15) && BusyTracker.Describe().Any(d => d.Contains("preview", StringComparison.OrdinalIgnoreCase)))
            await Task.Delay(30);
        for (int i = 0; i < 2; i++) await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
