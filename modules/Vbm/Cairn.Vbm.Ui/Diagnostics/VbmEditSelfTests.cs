using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;
using Cairn.Formats.Imaging;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Modules;
using Cairn.Vbm.Ui.Dialogs;
using Cairn.Vbm.Ui.Documents;
using Cairn.Workspace;
using static Cairn.Vbm.Ui.Diagnostics.VbmSelfTests;

namespace Cairn.Vbm.Ui.Diagnostics;

/// <summary>
/// Self-tests of the frame strip and its editing: dragging frames (one undo step), dropping image files, the play mark,
/// copy and paste through a stand-in clipboard (never the real one), resizing with the filter and fit options, the
/// preview's Smooth / Pixels scaling and the settings page (in memory only: a diagnostic run never saves settings).
/// </summary>
internal static class VbmEditSelfTests
{
    private static VbmModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VbmModule>().FirstOrDefault();

    /// <summary>A clipboard in memory: what the module wrote, what a paste reads, and whether it is "available".</summary>
    private sealed class FakeClipboard : IVbmClipboard
    {
        public bool Available { get; set; } = true;
        public string? Token { get; set; }
        public IReadOnlyList<(string Name, BgraImage Image)> Images { get; set; } = [];
        public int Writes { get; private set; }

        public bool TryRead(out VbmClipboardContent content)
        {
            content = Available ? new VbmClipboardContent(Token, Images) : new VbmClipboardContent(null, []);
            return Available;
        }

        public bool TryWrite(string token, BgraImage? image)
        {
            if (!Available) return false;
            Writes++;
            Token = token;
            Images = image is null ? [] : [("copied frame", image)];
            return true;
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var d in Descendants<T>(child)) yield return d;
        }
    }

    /// <summary>The frames of <paramref name="now"/> as positions in <paramref name="before"/> (by identity), 1-based for messages.</summary>
    private static List<int> Order(IReadOnlyList<VbmFrame> before, VbmFile now) =>
        [.. now.Frames.Select(f => before.ToList().FindIndex(b => ReferenceEquals(b, f)))];

    private static string Text(IEnumerable<int> order) => string.Join(",", order.Select(i => i + 1));

    private static BgraImage Halves(int w, int h, bool vertical)
    {
        var image = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool first = vertical ? y < h / 2 : x < w / 2;
                image.Set(x, y, first ? (byte)0 : (byte)255, 0, first ? (byte)255 : (byte)0, 255); // red then blue
            }
        return image;
    }

    private static bool IsRed((byte B, byte G, byte R, byte A) p) => p.R > 200 && p.B < 50 && p.A > 200;
    private static bool IsBlue((byte B, byte G, byte R, byte A) p) => p.B > 200 && p.R < 50 && p.A > 200;

    private static async Task<VbmDocument?> OpenSynthetic(SelfTestContext ctx, string folder, string name, byte[] bytes)
    {
        string path = System.IO.Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        var doc = await OpenAsync(ctx, path);
        if (doc is not null) doc.IsPlaying = false;
        return doc;
    }

    private static async Task CloseAll(SelfTestContext ctx, IEnumerable<VbmDocument?> docs)
    {
        foreach (var d in docs) if (d is not null) ctx.Shell.Close(d);
        await ctx.SettleAsync();
    }

    [SelfTest("vbm.strip-reorder")]
    public static async Task StripReorder(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        var doc = await OpenSynthetic(ctx, folder, "walk.vbm", Synthetic(5));
        var other = await OpenSynthetic(ctx, folder, "square.vbm",
            VbmEditing.Create([Halves(64, 64, false), Halves(64, 64, true)], VbmPixelFormat.Rgb565, 15, 1).Write());
        if (doc is null || other is null) { await CloseAll(ctx, [doc, other]); return; }
        ctx.Shell.Activate(doc);
        await ctx.SettleAsync();
        doc.IsPlaying = false;
        try
        {
            var view = (VbmDocumentView)doc.View;
            var original = doc.Current.Frames.ToList();
            doc.SelectedFrames = [0, 1];
            await ctx.SettleAsync();
            ctx.Check(view.Strip.SelectedItems.Count == 2, $"two frames selected in the strip ({view.Strip.SelectedItems.Count})");

            // drag frames 1-2 to before frame 5
            bool moved = await view.DropAsync(new DataObject(VbmDocumentView.DragFormat, new VbmFrameDrag(doc, [0, 1])), 4);
            var order = Order(original, doc.Current);
            ctx.Check(moved && order.SequenceEqual([2, 3, 0, 1, 4]), $"dragging frames 1-2 before frame 5 moves them as a block ({Text(order)})");
            ctx.Check(doc.SelectedFrames.SequenceEqual([2, 3]) && doc.CurrentFrame == 2, $"the moved frames stay selected ({Text(doc.SelectedFrames)})");
            ctx.Check(order.All(i => i >= 0), "moved frames keep their exact data");
            ctx.Check(doc.UndoLabel == "Move frames", $"the drop is one undo step named \"{doc.UndoLabel}\"");
            doc.Undo();
            ctx.Check(Order(original, doc.Current).SequenceEqual([0, 1, 2, 3, 4]) && !doc.IsDirty && !doc.CanUndo, "one undo puts every frame back");
            doc.Redo();
            ctx.Check(Order(original, doc.Current).SequenceEqual([2, 3, 0, 1, 4]), "redo moves them again");
            doc.Undo();

            // a drop where the frames already are changes nothing; a drop at the start
            ctx.Check(!await view.DropAsync(new DataObject(VbmDocumentView.DragFormat, new VbmFrameDrag(doc, [0, 1])), 1) && !doc.CanUndo,
                "dropping frames where they already are is no edit");
            await view.DropAsync(new DataObject(VbmDocumentView.DragFormat, new VbmFrameDrag(doc, [3, 4])), 0);
            ctx.Check(Order(original, doc.Current).SequenceEqual([3, 4, 0, 1, 2]), $"frames 4-5 dragged to the start ({Text(Order(original, doc.Current))})");
            doc.Undo();

            // frames dragged in from another bitmap's strip are copied (fitted to this bitmap's size)
            VbmResizeOptions? asked = null;
            VbmModule.ResizePromptOverride = _ => asked = new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.KeepAspect);
            bool copied = await view.DropAsync(new DataObject(VbmDocumentView.DragFormat, new VbmFrameDrag(other, [0, 1])), 1);
            ctx.Check(copied && asked is not null && doc.Current.FrameCount == 7 && other.Current.FrameCount == 2,
                $"frames dragged from another bitmap are copied in after asking how to fit them ({doc.Current.FrameCount} frames)");
            var pasted = doc.Current.Decode(1);
            ctx.Check(pasted.Get(0, 0).A == 0 && IsRed(pasted.Get(10, 8)) && IsBlue(pasted.Get(20, 8)) && pasted.Get(4, 8).A == 0,
                "a 64 x 64 frame kept its shape in the 32 x 16 bitmap (transparent padding left and right)");
            ctx.Check(doc.SelectedFrames.SequenceEqual([1, 2]) && doc.UndoLabel?.StartsWith("Copy frames", StringComparison.Ordinal) == true, $"the copies are selected, one undo step ({doc.UndoLabel})");
            while (doc.CanUndo) doc.Undo();

            // the play mark: on the frame shown, apart from the selection
            doc.SelectedFrames = [0, 1];
            doc.CurrentFrame = 3;
            await ctx.SettleAsync();
            var marked = view.ItemAt(3)!;
            ctx.Check(view.MarkedFrame == 3 && marked.IsCurrent && view.ItemAt(0)!.IsCurrent == false, $"the strip marks frame 4 as the one shown ({view.MarkedFrame + 1})");
            ctx.Check(!view.Strip.SelectedItems.Contains(marked) && view.Strip.SelectedItems.Count == 2, "the selection (frames 1-2) is unchanged by the mark");
            if (view.Strip.ItemContainerGenerator.ContainerFromItem(marked) is ListBoxItem container)
                ctx.Check(Descendants<ShapePath>(container).Any(p => p.Name == "PlayMark" && p.Visibility == Visibility.Visible)
                    && view.Strip.ItemContainerGenerator.ContainerFromItem(view.ItemAt(0)) is ListBoxItem first
                    && Descendants<ShapePath>(first).All(p => p.Visibility != Visibility.Visible), "the marked frame shows the play mark, the others do not");
            doc.IsPlaying = true;
            bool follows = await WaitAsync(() => view.MarkedFrame != 3, 2000);
            ctx.Check(follows && view.MarkedFrame == view.ShownFrame && doc.SelectedFrames.SequenceEqual([0, 1]),
                $"while it plays the mark follows the frame shown and the selection stays ({view.MarkedFrame + 1})");
            doc.IsPlaying = false;
        }
        finally
        {
            VbmModule.ResizePromptOverride = null;
            await CloseAll(ctx, [doc, other]);
        }
    }

    [SelfTest("vbm.drop-files")]
    public static async Task DropFiles(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        var doc = await OpenSynthetic(ctx, folder, "drop.vbm", Synthetic(3));
        if (doc is null) return;
        try
        {
            var view = (VbmDocumentView)doc.View;
            string tga = System.IO.Path.Combine(folder, "red.tga"), png = System.IO.Path.Combine(folder, "green.png"),
                bmp = System.IO.Path.Combine(folder, "blue.bmp"), txt = System.IO.Path.Combine(folder, "notes.txt"), big = System.IO.Path.Combine(folder, "big.png");
            File.WriteAllBytes(tga, TgaWriter.Write(Flat(32, 16, 0, 0, 255), true));
            File.WriteAllBytes(png, PngEncoder.Encode(Flat(32, 16, 0, 255, 0)));
            var blue = Flat(32, 16, 255, 0, 0);
            var encoder = new BmpBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgr32, null, blue.Pixels, blue.Stride)));
            using (var stream = File.Create(bmp)) encoder.Save(stream);
            File.WriteAllText(txt, "not an image");
            File.WriteAllBytes(big, PngEncoder.Encode(Halves(64, 64, true)));

            ctx.Check(!await view.DropAsync(new DataObject(DataFormats.FileDrop, new[] { txt }), 1) && !doc.CanUndo, "a dropped text file adds nothing");
            bool dropped = await view.DropAsync(new DataObject(DataFormats.FileDrop, new[] { tga, txt, png, bmp }), 1);
            ctx.Check(dropped && doc.Current.FrameCount == 6 && doc.SelectedFrames.SequenceEqual([1, 2, 3]),
                $"TGA, PNG and BMP dropped before frame 2 become frames 2-4 ({doc.Current.FrameCount} frames, selected {Text(doc.SelectedFrames)})");
            var (r, g, b) = (doc.Current.Decode(1).Get(5, 5), doc.Current.Decode(2).Get(5, 5), doc.Current.Decode(3).Get(5, 5));
            ctx.Check(r.R > 200 && r.G < 40 && g.G > 200 && g.R < 40 && b.B > 200 && b.R < 40 && b.A > 200,
                $"in the order dropped (red {r}, green {g}, blue {b}; the .bmp opaque)");
            ctx.Check(doc.UndoLabel == "Drop frames", $"one undo step ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(doc.Current.FrameCount == 3 && !doc.IsDirty, "undo removes the dropped frames");

            int prompts = 0;
            VbmModule.ResizePromptOverride = odd => { prompts += odd.Count; return new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre); };
            await view.DropAsync(new DataObject(DataFormats.FileDrop, new[] { big }), 3);
            var frame = doc.Current.Decode(3);
            ctx.Check(prompts == 1 && doc.Current.FrameCount == 4 && IsRed(frame.Get(16, 0)) && IsBlue(frame.Get(16, 15)),
                "a 64 x 64 image dropped at the end asks how to fit it; cropping the centre keeps the middle rows");
        }
        finally
        {
            VbmModule.ResizePromptOverride = null;
            await CloseAll(ctx, [doc]);
        }
    }

    [SelfTest("vbm.clipboard")]
    public static async Task ClipboardFrames(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        var a = await OpenSynthetic(ctx, folder, "a.vbm", Synthetic(4));
        var b = await OpenSynthetic(ctx, folder, "b.vbm", Synthetic(2));
        var c = await OpenSynthetic(ctx, folder, "c.vbm", VbmEditing.Create([Flat(16, 16, 9, 9, 9)], VbmPixelFormat.Rgb565, 15, 1).Write());
        var previous = VbmModule.ClipboardService;
        var fake = new FakeClipboard();
        VbmModule.ClipboardService = fake;
        try
        {
            if (a is null || b is null || c is null) return;
            ctx.Check(module.Shortcuts.Any(s => s.Key == System.Windows.Input.Key.C && s.Modifiers == System.Windows.Input.ModifierKeys.Control && s.Command == module.CopyFramesCommand)
                && module.Shortcuts.Any(s => s.Key == System.Windows.Input.Key.V && s.Modifiers == System.Windows.Input.ModifierKeys.Control && s.Command == module.PasteFramesCommand)
                && module.Shortcuts.Any(s => s.Key == System.Windows.Input.Key.Delete && s.Command == module.RemoveFramesCommand),
                "Ctrl+C, Ctrl+V and Delete are bitmap shortcuts");

            // copy two frames
            a.SelectedFrames = [1, 2];
            ctx.Check(module.CopyFrames(a) && fake.Writes == 1 && fake.Token == module.FrameClip?.Token
                && fake.Images.Count == 1 && fake.Images[0].Image.Pixels.AsSpan().SequenceEqual(a.Current.Decode(1).Pixels),
                "Copy keeps the frames and puts the first one on the clipboard as an image, with a marker");

            // paste into a bitmap with the same layout: the exact frames
            b.SelectedFrames = [0];
            ctx.Check(await module.PasteAsync(b) && b.Current.FrameCount == 4 && ReferenceEquals(b.Current.Frames[1], a.Current.Frames[1])
                && ReferenceEquals(b.Current.Frames[2], a.Current.Frames[2]) && b.SelectedFrames.SequenceEqual([1, 2]),
                "Paste in another tab inserts the exact frames after the selection and selects them");
            ctx.Check(b.UndoLabel == "Paste frames", $"one undo step ({b.UndoLabel})");
            b.Undo();

            // paste into the same tab
            a.SelectedFrames = [3];
            ctx.Check(await module.PasteAsync(a) && a.Current.FrameCount == 6 && ReferenceEquals(a.Current.Frames[4], a.Current.Frames[1]),
                "Paste in the same tab works too");
            a.Undo();

            // another size: asks how to fit, then converts
            VbmResizeOptions? asked = null;
            VbmModule.ResizePromptOverride = _ => asked = new VbmResizeOptions(VbmResizeFilter.Bilinear, VbmFitMode.Stretch);
            ctx.Check(await module.PasteAsync(c) && asked is not null && c.Current.FrameCount == 3 && c.Current.Width == 16 && c.Current.Format == VbmPixelFormat.Rgb565,
                "Paste into a 16 x 16 565 bitmap asks how to fit the 32 x 16 frames and converts them");
            VbmModule.ResizePromptOverride = null;

            // an image someone else put on the clipboard becomes a frame
            fake.Token = null;
            fake.Images = [("clipboard image", Flat(32, 16, 0, 0, 255))];
            b.SelectedFrames = [1];
            ctx.Check(await module.PasteAsync(b) && b.Current.FrameCount == 3 && IsRed(b.Current.Decode(2).Get(3, 3)) && b.UndoLabel == "Paste image as frame",
                "an image on the clipboard pastes as a new frame after the selection");

            // the clipboard cannot be read: the frames copied in Cairn still paste
            fake.Available = false;
            ctx.Check(await module.PasteAsync(b) && b.Current.FrameCount == 5 && ReferenceEquals(b.Current.Frames[3], a.Current.Frames[1]),
                "with the clipboard unavailable, Paste uses the frames copied in Cairn");
            ctx.Check(module.CopyFrames(a) && fake.Writes == 1, "Copy still works (for Cairn) when the clipboard cannot be written");
            fake.Available = true;

            // something else copied since: nothing to paste
            fake.Token = null;
            fake.Images = [];
            int before = b.Current.FrameCount;
            ctx.Check(!await module.PasteAsync(b) && b.Current.FrameCount == before, "nothing pastes when the clipboard holds neither the frames nor an image");

            // a clipboard bitmap without alpha (all zero) reads as opaque
            var dib = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[] { 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3, 0 }, 8);
            ctx.Check(SystemVbmClipboard.FromBitmap(dib, opaqueWhenNoAlpha: true).Get(1, 1) == (1, 2, 3, 255), "a pasted bitmap with no alpha is opaque");
        }
        finally
        {
            VbmModule.ClipboardService = previous;
            VbmModule.ResizePromptOverride = null;
            await CloseAll(ctx, [a, b, c]);
        }
    }

    [SelfTest("vbm.resize-options")]
    public static async Task ResizeOptions(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        var doc = await OpenSynthetic(ctx, folder, "fit.vbm", Synthetic(3));
        if (doc is null) return;
        var options = module.Options;
        var (resize, ask) = (options.Resize, options.AskResize);
        try
        {
            var wide = Halves(64, 16, false);
            int prompts = 0;
            VbmModule.ResizePromptOverride = odd => { prompts += odd.Count; return new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.KeepAspect); };
            var chosen = module.AskResize(doc, [("wide.png", wide)]);
            ctx.Check(chosen == new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.KeepAspect) && prompts == 1, "an image of another size asks for the filter and fit");
            ctx.Check(module.Options.Resize == chosen, "the choice is remembered in the module's settings");
            ctx.Check(module.AskResize(doc, [("same.png", Flat(32, 16, 1, 2, 3))]) == chosen && prompts == 1, "an image of the frame size does not ask");

            // keep aspect: 64 x 16 in 32 x 16 is 32 x 8, centred
            doc.ReplaceFrame(0, wide, chosen);
            var f = doc.Current.Decode(0);
            ctx.Check(f.Width == 32 && f.Height == 16 && doc.Current.MipLevels == 3 && f.Get(5, 1).A == 0 && f.Get(5, 14).A == 0
                && IsRed(f.Get(5, 6)) && IsBlue(f.Get(26, 9)), "Replace with keep aspect: the 64 x 16 image is 32 x 8 in the middle, transparent above and below");
            // crop centre: 16 x 64 (red over blue) into 32 x 16 uses the middle 16 x 8
            doc.ReplaceFrame(1, Halves(16, 64, true), new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre));
            f = doc.Current.Decode(1);
            ctx.Check(IsRed(f.Get(16, 1)) && IsBlue(f.Get(16, 14)) && f.Get(0, 0).A > 200, "Replace with crop centre fills the frame with the image's middle");
            // stretch
            doc.ReplaceFrame(2, Halves(8, 8, false), new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.Stretch));
            f = doc.Current.Decode(2);
            ctx.Check(IsRed(f.Get(0, 0)) && IsRed(f.Get(15, 15)) && IsBlue(f.Get(16, 0)) && IsBlue(f.Get(31, 15)), "Replace with stretch fills the frame, halves stretched");
            // the filter: nearest keeps two colours, bilinear blends at the edge
            int Colours(VbmResizeFilter filter)
            {
                doc.ReplaceFrame(2, Halves(4, 2, false), new VbmResizeOptions(filter, VbmFitMode.Stretch));
                var p = doc.Current.Decode(2);
                return Enumerable.Range(0, 32).Select(x => p.Get(x, 8)).Distinct().Count();
            }
            int nearest = Colours(VbmResizeFilter.Nearest), bilinear = Colours(VbmResizeFilter.Bilinear), quality = Colours(VbmResizeFilter.HighQuality);
            ctx.Check(nearest == 2 && bilinear > 2 && quality > 2, $"nearest keeps hard edges, bilinear and high quality blend ({nearest}, {bilinear}, {quality} colours across a row)");
            ctx.Check(doc.UndoLabel == "Replace frame", "each replace is an undo step");
            while (doc.CanUndo) doc.Undo();

            // the window: starts with the remembered choice, previews the frame as it will be
            var window = new VbmResizeWindow([("wide.png", wide)], 32, 16, VbmPixelFormat.Argb4444, 2, chosen!);
            ctx.Check(window.Current() == chosen && window.PreviewFrame.Width == 32 && window.PreviewFrame.Get(5, 1).A == 0,
                "the resize window starts with the remembered choice and previews the padded frame");
            window.Select(new VbmResizeOptions(VbmResizeFilter.HighQuality, VbmFitMode.Stretch));
            ctx.Check(window.PreviewFrame.Get(5, 1).A > 200, "choosing stretch updates the preview");
            window.Close();

            // cancel: nothing remembered; not asking: the remembered choice without a window
            VbmModule.ResizePromptOverride = _ => null;
            ctx.Check(module.AskResize(doc, [("wide.png", wide)]) is null && module.Options.Resize == chosen, "cancelling changes nothing");
            VbmModule.ResizePromptOverride = null;
            module.Options.AskResize = false;
            ctx.Check(module.AskResize(doc, [("wide.png", wide)]) == chosen, "with asking turned off the remembered choice is used");
        }
        finally
        {
            VbmModule.ResizePromptOverride = null;
            options.Resize = resize;
            options.AskResize = ask;
            await CloseAll(ctx, [doc]);
        }
    }

    [SelfTest("vbm.preview-scaling")]
    public static async Task PreviewScaling(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        var doc = await OpenSynthetic(ctx, folder, "zoom.vbm", Synthetic(2));
        if (doc is null) return;
        try
        {
            var view = (VbmDocumentView)doc.View;
            view.SetZoom(4);
            ctx.Check(view.Scaling == VbmScaling.Auto && view.EffectiveScaling == VbmScaling.Pixels, "zoomed in, the preview shows pixels until told otherwise");
            view.SetZoom(1);
            ctx.Check(view.EffectiveScaling == VbmScaling.Smooth, "at 100% it is smooth (as the animated textures preview)");
            view.SetMipLevel(2);
            ctx.Check(view.EffectiveScaling == VbmScaling.Pixels, "a small mip level shown at full size counts as zoomed in");
            view.SetMipLevel(0);
            view.SetScaling(VbmScaling.Pixels);
            view.SetZoom(0.5);
            ctx.Check(view.EffectiveScaling == VbmScaling.Pixels, "Pixels sticks when zoomed out");
            view.SetScaling(VbmScaling.Smooth);
            view.SetZoom(6);
            ctx.Check(view.EffectiveScaling == VbmScaling.Smooth, "Smooth sticks when zoomed in");
            view.SetScaling(VbmScaling.Auto);
            ctx.Check(view.EffectiveScaling == VbmScaling.Pixels, "Auto follows the zoom again");
            await ctx.SettleAsync();
        }
        finally { await CloseAll(ctx, [doc]); }
    }

    [SelfTest("vbm.settings-page")]
    public static void SettingsPage(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        ctx.Check(ctx.Shell.IsDiagnosticRun, "a diagnostic run (its settings are never saved)");
        string userFile = SettingsStore.DefaultPath;
        var stamp = File.Exists(userFile) ? (File.GetLastWriteTimeUtc(userFile), new FileInfo(userFile).Length) : default;
        var options = module.Options;
        var saved = (options.DefaultFps, options.DefaultFormat, options.Mipmaps, options.Resize, options.AskResize);
        try
        {
            var page = module.SettingsPages.OfType<VbmSettingsPage>().SingleOrDefault();
            if (!ctx.Check(page is { Title: "Volition bitmaps" }, "the module adds a Volition bitmaps settings page")) return;
            page!.Load();
            ctx.Check(page.Fps.Value == options.DefaultFps && page.Ask.IsChecked == options.AskResize, "Load shows the current settings");

            page.Fps.Value = 24;
            page.Format.SelectedItem = page.Format.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, VbmPixelFormat.Argb4444));
            page.Mipmaps.IsChecked = true;
            page.Filter.SelectedItem = page.Filter.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, VbmResizeFilter.Nearest));
            page.Fit.SelectedItem = page.Fit.Items.OfType<ComboBoxItem>().First(i => Equals(i.Tag, VbmFitMode.CropCentre));
            page.Ask.IsChecked = false;
            ctx.Check(options.DefaultFps == saved.DefaultFps && options.Resize == saved.Resize, "nothing changes before OK (Cancel leaves the settings)");
            page.Commit();
            ctx.Check(options.DefaultFps == 24 && options.DefaultFormat == VbmPixelFormat.Argb4444 && options.Mipmaps
                && options.Resize == new VbmResizeOptions(VbmResizeFilter.Nearest, VbmFitMode.CropCentre) && !options.AskResize,
                "OK stores the frame rate, pixel format, mipmaps and resize choice");

            // the values survive a save and load of the settings (to a temporary file, not the user's)
            string temp = System.IO.Path.Combine(NewFolder(), "settings.json");
            ctx.Check(SettingsStore.Save(ctx.Shell.Settings, temp), "the settings save to a temporary file");
            var reloaded = new VbmSettings(new ModuleSettings(SettingsStore.Load(temp), "vbm"));
            ctx.Check(reloaded.DefaultFps == 24 && reloaded.DefaultFormat == VbmPixelFormat.Argb4444 && reloaded.Mipmaps
                && reloaded.Resize == options.Resize && !reloaded.AskResize, "and read back the same");
            var again = new VbmSettingsPage(reloaded);
            again.Load();
            ctx.Check(again.Fps.Value == 24 && Equals((again.Format.SelectedItem as ComboBoxItem)?.Tag, VbmPixelFormat.Argb4444) && again.Mipmaps.IsChecked == true
                && Equals((again.Fit.SelectedItem as ComboBoxItem)?.Tag, VbmFitMode.CropCentre), "a new page shows them");

            // New VBM starts with them
            var window = new VbmNewWindow([("a.tga", Gradient(64, 64, 0))], options.DefaultFps, options.DefaultFormat, options.Mipmaps, options.Resize);
            var s = window.Current();
            ctx.Check(s.Fps == 24 && s.Format == VbmPixelFormat.Argb4444 && s.MipLevels == 3, $"the New VBM window starts with them ({s.Fps} fps, {s.Format}, {s.MipLevels} mip levels)");
            window.Close();
            options.DefaultFormat = null;
            window = new VbmNewWindow([("a.tga", Gradient(64, 64, 0))], options.DefaultFps, options.DefaultFormat, false);
            ctx.Check(window.Current() is { Format: VbmPixelFormat.Rgb565, MipLevels: 1 }, "\"suggested\" picks the format from the images (565 for opaque ones); no mipmaps gives 1 level");
            window.Close();
        }
        finally
        {
            (options.DefaultFps, options.DefaultFormat, options.Mipmaps, options.Resize, options.AskResize) = saved;
        }
        var after = File.Exists(userFile) ? (File.GetLastWriteTimeUtc(userFile), new FileInfo(userFile).Length) : default;
        ctx.Check(after == stamp, "the user's settings file was not written");
    }

    [ScreenshotDialog("vbm.resize")]
    public static Window? ResizeDialog(ScreenshotContext ctx) =>
        new VbmResizeWindow([("flare_wide.tga", Gradient(128, 64, 1))], 64, 64, VbmPixelFormat.Argb4444, 2,
            new VbmResizeOptions(VbmResizeFilter.HighQuality, VbmFitMode.KeepAspect)) { Owner = ctx.MainWindow };
}
