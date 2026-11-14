using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Conversion;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// PCX images and 8-bit text in the packfile preview (<c>Cairn.exe --probe-module --selftest-only vpp.preview-*</c>): a
/// synthetic PCX entry shows as an image with its facts and offers the image conversions; a Latin-1 dialogue file shows
/// its accented letters, not replacement characters; and, when Red Faction for N-Gage is configured, a texture and a
/// script of its <c>redfaction.cfl</c> in the packfile tab (only read). Captures go to <c>--preview-shots &lt;folder&gt;</c>
/// (else a temp folder), dark and light.
/// </summary>
internal static class VppNgageMediaSelfTests
{
    /// <summary>"*1 / En: ... / Sp: ..." with é, í, ó, ñ, ¡ and ¿ as single bytes, as the N-Gage dialogue files hold them.</summary>
    private const string Dialogue =
        "// Dialogue, level 1\r\n*1\r\nEn: Welcome to Mars, miner. The café is closed.\r\nGr: Willkommen auf dem Mars. Die Tür ist zu.\r\n"
        + "Fr: Bienvenue sur Mars. Où est le café ?\r\nIt: Benvenuto su Marte. Perché?\r\nSp: ¡Bienvenido a Marte! ¿Dónde está la señal?\r\n*2\r\nEn: END\r\n";

    /// <summary>The Unicode replacement character, which a wrong decoding shows in place of an 8-bit letter.</summary>
    private const char Replacement = (char)0xFFFD;

    private static byte[] Latin1(string text) => System.Text.Encoding.Latin1.GetBytes(text);

    /// <summary>A 128 x 64 8-bit PCX: a hue ramp across, darker rows down, a checker in one corner.</summary>
    private static byte[] Pcx()
    {
        var palette = new byte[768];
        for (int i = 0; i < 256; i++)
        {
            double h = (i % 32) / 32.0 * 6, v = 1 - (i / 32) / 9.0;
            int s = (int)h;
            double f = h - s;
            (double r, double g, double b) = s switch
            {
                0 => (1.0, f, 0.0),
                1 => (1.0 - f, 1.0, 0.0),
                2 => (0.0, 1.0, f),
                3 => (0.0, 1.0 - f, 1.0),
                4 => (f, 0.0, 1.0),
                _ => (1.0, 0.0, 1.0 - f),
            };
            palette[i * 3] = (byte)(r * v * 255);
            palette[i * 3 + 1] = (byte)(g * v * 255);
            palette[i * 3 + 2] = (byte)(b * v * 255);
        }
        return SyntheticPcx.Write(128, 64, 8, 1, (x, y) => x < 16 && y < 16 ? ((x / 4 + y / 4) % 2 == 0 ? 7 * 32 : 0) : x / 4 + y / 8 * 32, palette);
    }

    private static object? PreviewView(VppPreviewArea area) => (area.Preview.View as ModulePreviewHost)?.Inner ?? area.Preview.View;

    private static bool Is(VppItem item, string extension) => string.Equals(item.Extension, extension, StringComparison.OrdinalIgnoreCase);

    [SelfTest("vpp.preview-pcx", Order = 305)]
    public static async Task PcxEntry(SelfTestContext ctx)
    {
        var item = new VppItem("n_hud_ammo.pcx", new MemorySource(Pcx()), VppItemState.Added);
        var package = new VppPackage(null, [item]);
        await ShowBothThemesAsync(ctx, item, package, "pcx", area =>
        {
            ctx.Check(PreviewView(area) is ImagePreview, $"{item.Name} previews as an image ({area.Preview.Kind})");
            string Row(string label) => area.Details.Rows.FirstOrDefault(r => r.Label == label)?.Value ?? "(none)";
            ctx.Check(Row("Format") == "PCX" && Row("Dimensions") == "128 x 64", $"the details: {Row("Type")}, {Row("Format")}, {Row("Dimensions")}, {Row("Pixel format")}");
        });
        var info = VppInfo.Summarize(item);
        ctx.Check(info.Text == "128x64, 8-bit paletted, RLE compressed", $"the Info column: '{info.Text}'");

        // the image conversions a .tga gets
        var vpp = ctx.Shell.Modules.OfType<VppModule>().First();
        var targets = AssetConversions.TargetsFor(ctx.Shell.Modules, item.Name);
        ctx.Check(targets.Any(t => ReferenceEquals(t.Converter, vpp) && t.Target.Extension == ".dds")
            && targets.Select(t => t.Target.Extension).OrderBy(e => e).SequenceEqual(AssetConversions.TargetsFor(ctx.Shell.Modules, "n_hud_ammo.tga").Select(t => t.Target.Extension).OrderBy(e => e)),
            $"Convert to offers what a .tga gets ({string.Join(", ", targets.Select(t => t.Target.Extension))})");
        ctx.Check(DdsConversion.IsConvertible(item.Name) && PngExport.CanExport(item.Name), "Convert to DDS and Extract as PNG take it");
        var png = PngExport.Images(item.Name, item.Source.ReadAll(), CancellationToken.None).Single();
        ctx.Check(png.Name == "n_hud_ammo.png" && png.Image.Width == 128 && png.Image.Get(0, 0) != png.Image.Get(4, 0), $"Extract as PNG decodes it ({png.Name})");
    }

    [SelfTest("vpp.preview-latin1", Order = 306)]
    public static async Task Latin1Text(SelfTestContext ctx)
    {
        byte[] bytes = Latin1(Dialogue);
        var data = TextData.Decode(bytes, CancellationToken.None);
        ctx.Check(data.EncodingName == "Windows-1252", $"8-bit text is read as Windows-1252 ({data.EncodingName})");
        ctx.Check(data.Lines.Any(l => l.Text == "Sp: ¡Bienvenido a Marte! ¿Dónde está la señal?") && !data.Lines.Any(l => l.Text.Contains(Replacement)),
            "the accented letters show, not replacement characters");
        ctx.Check(TextData.Decode(System.Text.Encoding.UTF8.GetBytes("café"), CancellationToken.None) is { EncodingName: "UTF-8" } utf8 && utf8.Lines[0].Text == "café", "UTF-8 text stays UTF-8");

        var item = new VppItem("D01.txt", new MemorySource(bytes), VppItemState.Added);
        await ShowBothThemesAsync(ctx, item, new VppPackage(null, [item]), "latin1-text", area =>
        {
            var view = PreviewView(area) as TextPreview;
            ctx.Check(view is not null && view.LineCount == 9, $"{item.Name} previews as text ({area.Preview.Kind}, {view?.LineCount} lines)");
            ctx.Check(view?.Find(forward: true, "Dónde") == 6, $"find matches an accented word ({view?.FindStatus})");
        });
        var sheet = VppFacts.Describe(item);
        ctx.Check(sheet["First lines"]?.Contains("café", StringComparison.Ordinal) == true, "the details' first lines show the accents");
    }

    [SelfTest("vpp.preview-ngage", Order = 311)]
    public static async Task NgageArchive(SelfTestContext ctx)
    {
        string? dir = LocalPaths.NgageDirectory;
        string? path = dir is null ? null : Path.Combine(dir, "redfaction.cfl");
        if (path is null || !File.Exists(path)) { ctx.Skip("no Red Faction for N-Gage archive (" + LocalPaths.HowToSet(LocalPaths.NgageDirectoryVariable, "ngageDirectory") + ")"); return; }
        if (!ctx.Check(ctx.Shell.OpenFile(path), "the shell opens redfaction.cfl")) return;
        await ctx.SettleAsync(TimeSpan.FromSeconds(20));
        var doc = ctx.Shell.Documents.OfType<VppDocument>().FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (!ctx.Check(doc is not null, "the archive is open in a tab")) return;
        try
        {
            var view = (VppDocumentView)doc!.View;
            string shots = ShotsFolder(ctx);
            var theme = ctx.Shell.Theme;
            var before = theme.Requested;

            // every texture of the archive decodes
            var textures = doc.Current.Items.Where(i => Is(i, ".pcx")).ToList();
            int decoded = await Task.Run(() => textures.Count(t =>
            {
                try { return ImageDecoder.Decode(t.Source.ReadAll(), t.Name).Width > 0; }
                catch (Exception ex) when (ex is ImageDecodeException or IOException) { return false; }
            }));
            ctx.Check(textures.Count == 342 && decoded == textures.Count, $"every .pcx decodes ({decoded} of {textures.Count})");

            // a texture, a cutscene script and the dialogue file with its accented languages
            var pcx = textures.OrderByDescending(t => t.Size).First();
            var script = doc.Current.Find("C01_hendrix1.txt");
            var text = doc.Current.Find("D01.txt");
            foreach (var item in new[] { pcx, script, text })
            {
                if (!ctx.Check(item is not null, "the entry is in the archive")) continue;
                foreach (var light in new[] { false, true })
                {
                    theme.Apply(light ? AppTheme.Light : AppTheme.Dark);
                    try
                    {
                        doc.SetSelection([item!]);
                        await Task.Delay(VppPreviewController.Debounce + TimeSpan.FromMilliseconds(100));
                        var pane = view.PreviewHost.Content as VppPreviewPane;
                        if (pane is not null) await pane.Pending;
                        await SettleAsync();
                        var shown = (pane?.View as ModulePreviewHost)?.Inner ?? pane?.View;
                        bool image = Is(item!, ".pcx");
                        ctx.Check(image ? shown is ImagePreview : shown is TextPreview, $"{item!.Name} ({(light ? "light" : "dark")}) previews as {(image ? "an image" : "text")} ({pane?.Kind})");
                        SavePng(view, Path.Combine(shots, $"ngage-{Path.GetFileNameWithoutExtension(item.Name).ToLowerInvariant()}-{(light ? "light" : "dark")}.png"));
                    }
                    finally
                    {
                        theme.Apply(before);
                    }
                }
            }
            if (text is not null)
            {
                var data = TextData.Decode(text.Source.ReadAll(), CancellationToken.None);
                ctx.Log($"  {text.Name}: {data.EncodingName}, {data.Lines.Count} lines");
                ctx.Check(data.EncodingName == "Windows-1252", $"{text.Name} is read as Windows-1252 ({data.EncodingName})");
                ctx.Check(!data.Lines.Any(l => l.Text.Contains(Replacement)), $"{text.Name} has no replacement characters ({data.EncodingName})");
            }
            var scripts = doc.Current.Items.Where(i => Is(i, ".txt")).ToList();
            int eightBit = 0, replaced = 0;
            foreach (var file in scripts)
            {
                var data = TextData.Decode(file.Source.ReadAll(), CancellationToken.None);
                if (data.EncodingName == "Windows-1252") eightBit++;
                if (data.Lines.Any(l => l.Text.Contains(Replacement))) replaced++;
            }
            ctx.Log($"  {scripts.Count} .txt: {eightBit} read as Windows-1252, the rest ASCII or UTF-8");
            ctx.Check(replaced == 0, $"no .txt of the archive shows replacement characters ({replaced} of {scripts.Count})");
        }
        finally
        {
            ctx.Shell.Close(doc!);
        }
    }

    /// <summary>Shows <paramref name="item"/> in a fresh preview area in the dark and the light theme, checks it, and captures each.</summary>
    private static async Task ShowBothThemesAsync(SelfTestContext ctx, VppItem item, VppPackage package, string shot, Action<VppPreviewArea> check)
    {
        var theme = ctx.Shell.Theme;
        var before = theme.Requested;
        string shots = ShotsFolder(ctx);
        try
        {
            foreach (var light in new[] { false, true })
            {
                theme.Apply(light ? AppTheme.Light : AppTheme.Dark);
                var window = new Window { Width = 900, Height = 700, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Title = "Packfile preview self-test" };
                window.SetResourceReference(Control.BackgroundProperty, "App.WindowBackground");
                window.Show();
                var area = new VppPreviewArea(ctx.Shell);
                window.Content = area;
                try
                {
                    area.Show(item, [item], package, immediate: true);
                    await area.SettleAsync();
                    await SettleAsync();
                    if (!light) check(area);
                    int colours = SavePng(area, Path.Combine(shots, $"{shot}-{(light ? "light" : "dark")}.png"));
                    ctx.Check(colours > 2, $"{shot} ({(light ? "light" : "dark")}): the capture is not blank ({colours} colours)");
                }
                finally
                {
                    area.Dispose();
                    window.Content = null;
                    window.Close();
                }
            }
        }
        finally
        {
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
