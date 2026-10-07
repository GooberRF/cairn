using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Ui.Diagnostics;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;

namespace Cairn.Vpp.Ui.Conversion;

/// <summary>
/// DDS converter self-tests (<c>Cairn.exe --selftest</c>) on a generated packfile: TGA (opaque and with alpha),
/// PNG and JPG entries of power-of-two and other sizes plus one non-image. No game data.
/// </summary>
public static class VppDdsConvertSelfTests
{
    private static readonly string[] ImageNames = ["wall_opaque.tga", "glass_alpha.tga", "decal_odd.png", "photo.jpg"];

    private static BgraImage Pattern(int w, int h, bool alpha, int seed)
    {
        var img = new BgraImage(w, h);
        var rng = new Random(seed);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                img.Set(x, y, (byte)(64 + rng.Next(0, 8)), (byte)(y * 255 / Math.Max(h - 1, 1)), (byte)(x * 255 / Math.Max(w - 1, 1)),
                    alpha ? (byte)((x + y) * 255 / (w + h - 2)) : (byte)255);
        return img;
    }

    private static byte[] Jpeg(BgraImage img)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = 95 };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(img.Width, img.Height, 96, 96, PixelFormats.Bgra32, null, img.Pixels, img.Stride)));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    /// <summary>A new packfile document holding the generated entries (not shown unless asked).</summary>
    private static VppDocument Sample(SelfTestContext? ctx, VppModule module)
    {
        var doc = (VppDocument)module.Kind.CreateNew()!;
        var entries = new List<(string, VppSource)>
        {
            (ImageNames[0], new MemorySource(TgaWriter.Write(Pattern(64, 64, false, 1), false))),
            (ImageNames[1], new MemorySource(TgaWriter.Write(Pattern(32, 32, true, 2), true))),
            (ImageNames[2], new MemorySource(PngEncoder.Encode(Pattern(30, 20, true, 3)))),
            (ImageNames[3], new MemorySource(Jpeg(Pattern(48, 40, false, 4)))),
            ("readme.txt", new MemorySource("not an image"u8.ToArray())),
        };
        doc.ApplyEdit("Add samples", p => VppEdit.AddSources(p, entries, VppClashPolicy.KeepBoth).Package);
        ctx?.Log("dds-convert sample: " + string.Join(", ", doc.Current.Items.Select(i => i.Name)));
        return doc;
    }

    private static double Psnr(BgraImage a, BgraImage b)
    {
        double sum = 0;
        for (int i = 0; i < a.Pixels.Length; i++) { if (i % 4 == 3) continue; double d = a.Pixels[i] - b.Pixels[i]; sum += d * d; }
        double mse = sum / (a.Pixels.Length * 3 / 4);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static List<VppItem> Images(VppDocument doc) => doc.Current.Items.Where(i => DdsConversion.IsConvertible(i.Name)).ToList();

    [SelfTest("vpp.dds-convert-replace")]
    public static async Task ReplaceAndUndo(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        using var doc = Sample(ctx, module);
        var before = doc.Serialize();
        var sources = doc.Current.Items.ToDictionary(i => i.Name, i => i.Source.ReadAll());
        var (images, skipped) = DdsConversion.Split(doc.Current.Items);
        ctx.Check(images.Count == 4 && skipped.Count == 1 && skipped[0].Name == "readme.txt", "four images to convert, readme.txt skipped");

        bool ok = await module.RunConversionAsync(doc, images, new DdsConvertSettings());
        ctx.Check(ok, "conversion finished");
        ctx.Check(doc.UndoLabel == "Convert 4 images to DDS", $"one undo step ({doc.UndoLabel})");
        ctx.Check(doc.Current.Items.Select(i => i.Name).SequenceEqual(["wall_opaque.dds", "glass_alpha.dds", "decal_odd.dds", "photo.dds", "readme.txt"]),
            "each original replaced in place by its .dds: " + string.Join(", ", doc.Current.Items.Select(i => i.Name)));
        var expect = new Dictionary<string, (EngineFormat Format, int W, int H)>
        {
            ["wall_opaque"] = (EngineFormat.Dxt1, 64, 64), ["glass_alpha"] = (EngineFormat.Dxt5, 32, 32),
            ["decal_odd"] = (EngineFormat.Dxt5, 32, 16), ["photo"] = (EngineFormat.Dxt1, 48, 40),
        };
        foreach (var (stem, want) in expect)
        {
            var bytes = doc.Current.Find(stem + ".dds")!.Source.ReadAll();
            var info = DdsCodec.Probe(bytes, stem);
            ctx.Check(info.Format == want.Format && info.Width == want.W && info.Height == want.H,
                $"{stem}.dds: {info.Describe()} (want {want.Format} {want.W} x {want.H})");
            ctx.Check(info.MipLevels == DdsEncoder.FullChainLength(want.W, want.H), $"{stem}.dds has a full mip chain ({info.MipLevels})");
            var original = sources.First(s => Path.GetFileNameWithoutExtension(s.Key) == stem);
            var source = ImageDecoder.Decode(original.Value, original.Key);
            if (source.Width != want.W || source.Height != want.H) source = ImageResampler.Resize(source, want.W, want.H, ResampleFilter.Triangle);
            double psnr = Psnr(source, DdsCodec.Decode(bytes, stem));
            ctx.Check(psnr > 30, $"{stem}.dds decodes close to the source (PSNR {psnr:F1} dB)");
        }
        ctx.Check(doc.Current.Find("readme.txt")!.Source.ReadAll().AsSpan().SequenceEqual(sources["readme.txt"]), "the non-image is untouched");
        doc.Undo();
        ctx.Check(doc.Serialize().AsSpan().SequenceEqual(before), "undo restores the originals byte-identical");
        doc.Redo();
        ctx.Check(doc.Current.Contains("photo.dds"), "redo converts again");
    }

    [SelfTest("vpp.dds-convert-modes")]
    public static async Task KeepFolderCancel(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        using var doc = Sample(ctx, module);
        var before = doc.Serialize();

        // Keep originals too.
        await module.RunConversionAsync(doc, Images(doc), new DdsConvertSettings { Output = DdsOutputMode.KeepOriginals });
        ctx.Check(doc.Current.Count == 9 && ImageNames.All(doc.Current.Contains) && doc.Current.Contains("decal_odd.dds"), $"keep both: originals stay, .dds added ({doc.Current.Count})");
        ctx.Check(ImageNames.All(n => doc.Current.Find(n)!.State == VppItemState.Added), "originals unchanged");
        doc.Undo();
        ctx.Check(doc.Serialize().AsSpan().SequenceEqual(before), "undo removes the added .dds files");

        // Existing .dds with Skip: an image whose .dds exists is left alone.
        doc.ApplyEdit("Add existing", p => VppEdit.AddBytes(p, "photo.dds", [1, 2, 3], VppClashPolicy.Replace).Package);
        await module.RunConversionAsync(doc, Images(doc), new DdsConvertSettings { Existing = DdsExistingPolicy.Skip });
        ctx.Check(doc.Current.Contains("photo.jpg") && doc.Current.Find("photo.dds")!.Size == 3 && doc.Current.Contains("wall_opaque.dds"),
            "skip: an existing .dds is kept and its image not converted; the others are");
        doc.Undo();
        doc.Undo();

        // Write to a folder: packfile unchanged; an existing file is never overwritten without asking (declined here).
        string folder = Path.Combine(Path.GetTempPath(), "cairn-dds-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var settings = new DdsConvertSettings { Output = DdsOutputMode.Folder, Folder = folder };
            await module.RunConversionAsync(doc, Images(doc), settings);
            var written = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
            ctx.Check(written.SequenceEqual(["decal_odd.dds", "glass_alpha.dds", "photo.dds", "wall_opaque.dds"]), "folder: four .dds files written: " + string.Join(", ", written));
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(before), "folder: the packfile is unchanged");
            var first = File.ReadAllBytes(Path.Combine(folder, "photo.dds"));
            await module.RunConversionAsync(doc, Images(doc), settings with { Encode = new DdsEncodeOptions { Format = DdsTargetFormat.Argb8888 } });
            ctx.Check(File.ReadAllBytes(Path.Combine(folder, "photo.dds")).AsSpan().SequenceEqual(first), "folder: existing files are not overwritten when the prompt is declined");
            ctx.Check(Directory.GetFiles(folder).Length == 4, "folder: no temporary files left");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // Cancel mid-batch: nothing changes.
        bool finished = await module.RunConversionAsync(doc, Images(doc), new DdsConvertSettings { Encode = new DdsEncodeOptions { Quality = DdsQuality.Best } },
            op => op.Cancel());
        ctx.Check(!finished && doc.Serialize().AsSpan().SequenceEqual(before) && doc.Operation is null, "cancel: nothing applied, no operation left");
    }

    [SelfTest("vpp.dds-convert-dialog")]
    public static async Task Dialog(SelfTestContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        using var doc = Sample(ctx, module);
        var (images, skipped) = DdsConversion.Split(doc.Current.Items);
        var window = VppDdsConvertWindow.Create(doc, images, skipped);
        window.Owner = ctx.MainWindow;
        window.Show();
        try
        {
            await WaitAsync(() => window.Rows.All(r => r.Image is not null || r.Error is not null));
            await ctx.SettleAsync();
            ctx.Check(window.Rows.All(r => r.Facts.Contains(" x ", StringComparison.Ordinal)), "every row shows its size and alpha: " + string.Join(" | ", window.Rows.Select(r => r.Facts)));
            ctx.Check(window.Rows[1].Facts.Contains("smooth alpha", StringComparison.Ordinal) && window.Rows[0].Facts.Contains("opaque", StringComparison.Ordinal), "alpha kinds detected");
            var images2 = FindImages(window);
            ctx.Check(images2.Count >= 2 && images2.All(i => i.Source is not null), "before and after previews render");
            window.Apply(window.Current() with { Encode = new DdsEncodeOptions { Format = DdsTargetFormat.Rgb565, Mips = DdsMipMode.None } });
            await ctx.SettleAsync();
            ctx.Check(images2.All(i => i.Source is not null), "the preview follows a settings change");
        }
        finally { window.Close(); }
    }

    private static List<System.Windows.Controls.Image> FindImages(DependencyObject root)
    {
        var found = new List<System.Windows.Controls.Image>();
        void Walk(DependencyObject d)
        {
            if (d is System.Windows.Controls.Image img) found.Add(img);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i));
        }
        Walk(root);
        return found;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!condition() && DateTime.UtcNow < limit) await Task.Delay(50);
    }

    /// <summary><c>--dialog vpp.dds-convert</c>: the converter on the generated sample, the alpha TGA selected.</summary>
    [ScreenshotDialog("vpp.dds-convert")]
    public static async Task<Window?> ConvertDialog(ScreenshotContext ctx)
    {
        var module = ctx.Shell.Modules.OfType<VppModule>().First();
        var doc = Sample(null, module);
        ctx.Shell.AddDocument(doc);
        var (images, skipped) = DdsConversion.Split(doc.Current.Items);
        var window = VppDdsConvertWindow.Create(doc, images, skipped);
        window.Owner = ctx.MainWindow;
        window.Show();
        await WaitAsync(() => window.Rows.All(r => r.Image is not null || r.Error is not null));
        await ctx.SettleAsync();
        window.SelectRow(1);
        await ctx.SettleAsync();
        return window;
    }
}
