using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Previews;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Vbm.Ui.Dialogs;
using Cairn.Vbm.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Vbm.Ui.Diagnostics;

/// <summary>
/// Self-tests of the bitmap module: opening, playback, edits with undo, saving byte for byte, damaged files, export,
/// Convert to ATX (through whichever module converts) and the packfile preview's "Open in Cairn". Files are generated in
/// a temporary folder; the stock test reads the game folder's packfiles when they are there.
/// </summary>
internal static class VbmSelfTests
{
    private static VbmModule? Module(SelfTestContext ctx) => ctx.Shell.Modules.OfType<VbmModule>().FirstOrDefault();

    internal static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "cairn-vbm-selftest-" + Environment.ProcessId, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    internal static BgraImage Gradient(int w, int h, int seed)
    {
        var image = new BgraImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                image.Set(x, y, (byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)(seed * 70), (byte)(seed % 2 == 0 ? 255 : 128 + x));
        return image;
    }

    internal static BgraImage Flat(int w, int h, byte b, byte g, byte r)
    {
        var image = new BgraImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) image.Set(x, y, b, g, r, 255);
        return image;
    }

    /// <summary>A synthetic animated bitmap: <paramref name="frames"/> 32 x 16 4444 frames, 3 mip levels, 10 fps.</summary>
    internal static byte[] Synthetic(int frames = 3) =>
        VbmEditing.Create([.. Enumerable.Range(0, frames).Select(i => Gradient(32, 16, i))], VbmPixelFormat.Argb4444, 10, 3).Write();

    internal static async Task<VbmDocument?> OpenAsync(SelfTestContext ctx, string path)
    {
        if (!ctx.Check(ctx.Shell.OpenFile(path), $"the shell opens {Path.GetFileName(path)}")) return null;
        await ctx.SettleAsync();
        var doc = ctx.Shell.ActiveDocument as VbmDocument;
        ctx.Check(doc is not null, $"{Path.GetFileName(path)} opens as a bitmap document ({ctx.Shell.ActiveDocument?.Kind.Id})");
        return doc;
    }

    internal static async Task<bool> WaitAsync(Func<bool> condition, int ms = 3000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < ms) await Task.Delay(30);
        return condition();
    }

    [SelfTest("vbm.document")]
    public static async Task Document(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        string path = Path.Combine(folder, "glow.vbm");
        byte[] original = Synthetic();
        File.WriteAllBytes(path, original);
        if (await OpenAsync(ctx, path) is not { } doc) return;
        try
        {
            var view = (VbmDocumentView)doc.View;
            ctx.Check(view.Strip.Items.Count == 3, $"the frame strip lists the 3 frames ({view.Strip.Items.Count})");
            ctx.Check(doc.Problems.Count == 0, $"no problems ({string.Join("; ", doc.Problems.Select(p => p.Code))})");
            ctx.Check(doc.StatusItems.Any(s => s.Text == "32 x 16") && doc.StatusItems.Any(s => s.Text.Contains("3 frames at 10 fps", StringComparison.Ordinal)),
                $"the status bar shows the size, frames and rate ({string.Join(" | ", doc.StatusItems.Select(s => s.Text))})");
            ctx.Check(ctx.Shell.Modules.SelectMany(m => m.DocumentKinds).Any(k => k.Extensions.Contains(".vbm") && k.CanCreateNew),
                "a creatable .vbm kind is listed (file associations, New, the start page's modules)");
            ctx.Check(VbmFacts.For(doc.Current, doc.FileSize).Any(f => f.Label == "File size" && (f.Value.Contains("KB", StringComparison.Ordinal) || f.Value.Contains("bytes", StringComparison.Ordinal))),
                "the facts give the file size");

            // playback at the file's rate
            doc.IsPlaying = true;
            int first = view.ShownFrame;
            bool advanced = await WaitAsync(() => view.ShownFrame != first, 2000);
            ctx.Check(advanced && doc.IsPlaying, $"the animation plays (frame {first + 1} -> {view.ShownFrame + 1})");
            module.PlayPauseCommand.Execute(null);
            ctx.Check(!doc.IsPlaying, "Play/Pause pauses");
            module.NextFrameCommand.Execute(null);
            int stepped = doc.CurrentFrame;
            ctx.Check(view.ShownFrame == stepped && doc.SelectedFrames.SequenceEqual([stepped]), "Next Frame steps and selects that frame");

            // frame rate edit, undo, redo
            ctx.Check(doc.SetFps(24) && doc.Current.Fps == 24 && doc.IsDirty, "the frame rate changes as an edit");
            doc.Undo();
            ctx.Check(doc.Current.Fps == 10 && !doc.IsDirty, "undo restores the frame rate and the saved state");
            doc.Redo();
            ctx.Check(doc.Current.Fps == 24, "redo applies it again");
            doc.Undo();

            // replace (with resize), add, duplicate, move, remove
            ctx.Check(doc.ReplaceFrame(1, Flat(64, 64, 0, 0, 255)) && doc.Current.Decode(1).Get(4, 4) == (0, 0, 255, 255)
                && ReferenceEquals(doc.Current.Frames[0], doc.SavedSnapshot.Frames[0]), "Replace Frame resizes and converts one frame, the others keep their data");
            ctx.Check(doc.InsertFrames(3, [Flat(32, 16, 0, 255, 0), Flat(32, 16, 255, 0, 0)]) && doc.Current.FrameCount == 5
                && doc.SelectedFrames.SequenceEqual([3, 4]), "Add Frames appends and selects the new frames");
            await ctx.SettleAsync();
            ctx.Check(view.Strip.Items.Count == 5 && view.Strip.SelectedItems.Count == 2, $"the strip follows ({view.Strip.Items.Count} items, {view.Strip.SelectedItems.Count} selected)");
            ctx.Check(doc.MoveSelected(-1) && doc.SelectedFrames.SequenceEqual([2, 3]), "Move Earlier moves the selection as a block");
            ctx.Check(doc.DuplicateSelected() && doc.Current.FrameCount == 7, "Duplicate copies the selection");
            ctx.Check(doc.RemoveSelected() && doc.Current.FrameCount == 5, "Remove deletes the selection");
            doc.SelectedFrames = [0, 1, 2, 3, 4];
            ctx.Check(!module.RemoveFramesCommand.CanExecute(null), "removing every frame is not offered");
            while (doc.CanUndo) doc.Undo();
            ctx.Check(!doc.IsDirty && doc.Current.FrameCount == 3, "undoing everything gets back to the file as opened");
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(original), "the unchanged bitmap writes the same bytes");

            // save and reopen
            doc.SetFps(30);
            ctx.Check(ctx.Shell.Save(doc) && !doc.IsDirty, "Save writes the file");
            var info = VbmCodec.ReadInfo(File.ReadAllBytes(path), "glow.vbm");
            ctx.Check(info.Fps == 30 && info.FrameCount == 3 && info.MipLevels == 3, $"the saved file reads back ({info.Describe()})");
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
        }
    }

    [SelfTest("vbm.damaged-files")]
    public static async Task DamagedFiles(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        byte[] whole = Synthetic(3);
        string cut = Path.Combine(folder, "cut.vbm");
        File.WriteAllBytes(cut, whole[..^100]);
        if (await OpenAsync(ctx, cut) is { } truncated)
        {
            ctx.Check(truncated.Current.FrameCount == 2 && truncated.Problems.Any(p => p.Code == "VBM002" && p.Severity == VbmSeverity.Error),
                $"a file that stops early keeps its complete frames and reports it ({truncated.Current.FrameCount} frames, {string.Join(", ", truncated.Problems.Select(p => p.Code))})");
            ctx.Check(ctx.Shell.ShowPanel(VbmProblemsPanel.PanelId), "the Problems tab is there for a bitmap");
            await ctx.SettleAsync();
            ctx.Check(VbmProblemsPanel.For(truncated).Shown.Count == truncated.Problems.Count, "the Problems tab lists the problems");
            ctx.Check(truncated.StatusItems.Any(s => s.Text.Contains("error", StringComparison.Ordinal)), "the status bar counts the error");
            ctx.Shell.Close(truncated);
        }
        string junk = Path.Combine(folder, "junk.vbm");
        File.WriteAllBytes(junk, Encoding.ASCII.GetBytes("this is not a bitmap at all, just some text"));
        if (await OpenAsync(ctx, junk) is { } broken)
        {
            ctx.Check(broken.IsBroken && broken.IsReadOnly && broken.Problems.Single().Code == "VBM001", "an unreadable file opens read-only with the reason as its problem");
            ctx.Check(!broken.ConfirmSave() && broken.CaptureRecovery() is null, "an unreadable file is never saved or recovered");
            // fixed on disk and reloaded: editable again, the old problem gone
            File.WriteAllBytes(junk, Synthetic(2));
            broken.Reload();
            await ctx.SettleAsync();
            ctx.Check(!broken.IsBroken && !broken.IsReadOnly && broken.Current.FrameCount == 2 && broken.Problems.All(p => p.Code != "VBM001"),
                $"a reload that reads the fixed file makes it editable ({broken.Current.FrameCount} frames, {string.Join(", ", broken.Problems.Select(p => p.Code))})");
            ctx.Check(broken.SetFps(12) && broken.Current.Fps == 12 && broken.ConfirmSave(), "and it can be edited and saved");
            broken.Undo();
            ctx.Shell.Close(broken);
        }
        await ctx.SettleAsync();
    }

    /// <summary>The Problems tab draws its columns (a header row and one cell per column), not each row's text dump.</summary>
    [SelfTest("vbm.problems-columns")]
    public static async Task ProblemsColumns(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        string path = Path.Combine(NewFolder(), "trail.vbm");
        File.WriteAllBytes(path, [.. Synthetic(1), 1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);
        if (await OpenAsync(ctx, path) is not { } doc) return;
        try
        {
            ctx.Check(ctx.Shell.ShowPanel(VbmProblemsPanel.PanelId), "the Problems tab is shown");
            await ctx.SettleAsync();
            var list = VbmProblemsPanel.For(doc).List;
            var (headers, rows, cells) = Cairn.Ui.Diagnostics.GridListCheck.Count(list);
            ctx.Log(Cairn.Ui.Diagnostics.GridListCheck.Describe(list));
            ctx.Check(list.Items.Count > 0, $"the tab lists {list.Items.Count} problems");
            ctx.Check(headers == 1, $"the list shows a column header row ({headers})");
            ctx.Check(rows == list.Items.Count && cells >= rows * 4, $"every problem is a row of cells ({rows} rows, {cells} cells for {list.Items.Count} problems)");

            // A frame rate spin keeps the strip (and its thumbnails) and checks the file once, at the end of the gesture.
            var view = (VbmDocumentView)doc.View;
            var items = view.StripItems;
            var problems = doc.Problems;
            doc.BeginEdit("Change frame rate");
            for (int fps = 11; fps <= 14; fps++) { int v = fps; doc.UpdateEdit(f => VbmEditing.WithFps(f, v)); }
            ctx.Check(ReferenceEquals(items, view.StripItems), "the strip is kept while the frame rate spins");
            ctx.Check(ReferenceEquals(problems, doc.Problems), "the checks wait for the end of the spin");
            doc.CommitEdit();
            ctx.Check(doc.Current.Fps == 14 && !ReferenceEquals(problems, doc.Problems), "and run once it ends");
            doc.Undo();
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
        }
    }

    [SelfTest("vbm.export")]
    public static async Task Export(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        string path = Path.Combine(folder, "spark.vbm");
        File.WriteAllBytes(path, Synthetic(4));
        if (await OpenAsync(ctx, path) is not { } doc) return;
        try
        {
            string output = Path.Combine(folder, "frames");
            int all = await module.ExportAsync(doc, new VbmExportRequest(output, "spark", VbmExportFormat.Tga, [0, 1, 2, 3]), confirmReplace: false);
            ctx.Check(all == 4 && File.Exists(Path.Combine(output, "spark_00.tga")) && File.Exists(Path.Combine(output, "spark_03.tga")), $"all frames export as TGA ({all})");
            int some = await module.ExportAsync(doc, new VbmExportRequest(output, "spark", VbmExportFormat.Png, [1, 3]), confirmReplace: false);
            ctx.Check(some == 2 && File.Exists(Path.Combine(output, "spark_01.png")) && !File.Exists(Path.Combine(output, "spark_00.png")), "selected frames export as PNG, keeping their numbers");
            var decoded = ImageDecoder.DecodeFile(Path.Combine(output, "spark_03.png"));
            ctx.Check(decoded.Pixels.AsSpan().SequenceEqual(doc.Current.Decode(3).Pixels), "an exported frame decodes to the bitmap's pixels");
            int again = await module.ExportAsync(doc, new VbmExportRequest(output, "spark", VbmExportFormat.Tga, [0]), confirmReplace: true);
            ctx.Check(again == 0, "replacing existing images needs a yes (declined in an unattended run)");

            var window = new VbmExportWindow(ctx.Shell.Dialogs, "spark.vbm", 4, [1, 3], output, VbmExportFormat.Png);
            var request = window.Current();
            ctx.Check(request is { Format: VbmExportFormat.Png, BaseName: "spark" } && request.Frames.SequenceEqual([1, 3]), "the export window starts with the selection and the stem");
            window.Close();
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
        }
    }

    /// <summary>Records what a conversion was asked for (stands in for the converting module).</summary>
    private sealed class RecordingConverter : IAssetConverter
    {
        public AssetConversionRequest? Last { get; private set; }
        public bool CanConvert(string sourceName, string targetExtension) => targetExtension == ".atx";
        public Task<string?> ConvertAsync(AssetConversionRequest request) { Last = request; return Task.FromResult<string?>(null); }
    }

    [SelfTest("vbm.convert-to-atx")]
    public static async Task ConvertToAtx(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        string path = Path.Combine(folder, "fire.vbm");
        File.WriteAllBytes(path, Synthetic(3));
        if (await OpenAsync(ctx, path) is not { } doc) return;
        var opened = new List<IDocument> { doc };
        try
        {
            // the request: the current content, the loose file's path
            var recorder = new RecordingConverter();
            VbmModule.ConverterOverride = recorder;
            doc.SetFps(20);
            await module.ConvertToAtxAsync(doc);
            ctx.Check(recorder.Last is { FileName: "fire.vbm", TargetExtension: ".atx", ArchivePath: null, Interactive: true } r && r.FilePath == path
                && VbmCodec.ReadInfo(r.Bytes, "fire.vbm").Fps == 20, "Convert to ATX hands over the current content and the file's path");
            doc.Undo();
            VbmModule.ConverterOverride = null;

            var converter = ctx.Shell.Modules.OfType<IAssetConverter>().FirstOrDefault(c => c.CanConvert("fire.vbm", ".atx"));
            if (converter is null)
            {
                ctx.Check(!module.ConvertToAtxCommand.CanExecute(null), "without a converting module the command is unavailable");
                ctx.Log("  (no module converts .vbm to .atx in this build: the real conversion is skipped)");
                return;
            }
            ctx.Check(module.ConvertToAtxCommand.CanExecute(null), "the animated textures module offers the conversion");
            string output = Path.Combine(folder, "atx");
            Directory.CreateDirectory(output);
            var before = ctx.Shell.Documents.ToHashSet();
            string? atx = await module.ConvertToAtxAsync(doc, interactive: false, outputFolder: output);
            await ctx.SettleAsync();
            opened.AddRange(ctx.Shell.Documents.Where(d => !before.Contains(d)));
            ctx.Check(atx is not null && File.Exists(atx) && Path.GetExtension(atx) == ".atx", $"the conversion writes an .atx ({atx})");
            ctx.Check(Directory.GetFiles(output, "*.tga").Length == 3, $"one TGA per frame ({Directory.GetFiles(output, "*.tga").Length})");
            ctx.Check(ctx.Shell.ActiveDocument is { } active && active.Kind.Id == "atx" && active.FilePath == atx, $"the .atx opens in its module ({ctx.Shell.ActiveDocument?.Kind.Id})");
        }
        finally
        {
            VbmModule.ConverterOverride = null;
            foreach (var d in opened) ctx.Shell.Close(d);
            await ctx.SettleAsync();
        }
    }

    /// <summary>A minimal packfile holding <paramref name="files"/>.</summary>
    internal static byte[] Packfile(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        const int Block = VppArchive.BlockSize;
        static int Align(int v) => (v + Block - 1) / Block * Block;
        using var ms = new MemoryStream();
        var header = new byte[Block];
        BinaryPrimitives.WriteUInt32LittleEndian(header, VppArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)files.Count);
        ms.Write(header);
        var directory = new byte[Align(files.Count * VppArchive.EntryBytes)];
        for (int i = 0; i < files.Count; i++)
        {
            int at = i * VppArchive.EntryBytes;
            Encoding.ASCII.GetBytes(files[i].Name).CopyTo(directory, at);
            BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(at + VppArchive.NameBytes), files[i].Data.Length);
        }
        ms.Write(directory);
        foreach (var (_, data) in files)
        {
            ms.Write(data);
            ms.Write(new byte[Align(data.Length) - data.Length]);
        }
        var bytes = ms.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)bytes.Length);
        return bytes;
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

    [SelfTest("vbm.packfile")]
    public static async Task PackfileEntry(SelfTestContext ctx)
    {
        if (Module(ctx) is not { } module) { ctx.Skip("the bitmap module is not loaded"); return; }
        string folder = NewFolder();
        string vpp = Path.Combine(folder, "bitmaps.vpp");
        File.WriteAllBytes(vpp, Packfile([("pulse.vbm", Synthetic(4)), ("readme.txt", Encoding.ASCII.GetBytes("text\r\n"))]));

        // the shared preview pane: an image preview with "Open in Cairn" above it
        var window = new Window { Width = 700, Height = 500, ShowInTaskbar = false, WindowStyle = WindowStyle.ToolWindow, Left = -2000, Top = -2000 };
        var pane = new AssetPreviewPane(ctx.Shell);
        window.Content = pane;
        window.Show();
        try
        {
            pane.Show(AssetPreviewSource.FromBytes("pulse.vbm", Synthetic(4)));
            await WaitAsync(() => pane.Kind == AssetPreviewKind.Image, 5000);
            await ctx.SettleAsync();
            ctx.Check(pane.Kind == AssetPreviewKind.Image && pane.View is ModulePreviewHost { Inner: ImagePreview },
                $"a .vbm previews as an animated image with a bar above it ({pane.Kind}, {pane.View?.GetType().Name})");
            ctx.Check(Descendants<Button>(pane).Any(b => Equals(b.Content, "Open in Cairn")), "the preview offers Open in Cairn");
        }
        finally
        {
            pane.Dispose();
            window.Close();
        }

        // "Open in Cairn" from a packfile: the work copy opens in this module and Convert names the packfile
        if (ctx.Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault() is not { } provider || !provider.CanOpenArchive(vpp))
        {
            ctx.Log("  (no packfile module in this build: the work-copy part is skipped)");
            return;
        }
        var before = ctx.Shell.Documents.ToHashSet();
        ctx.Check(provider.OpenArchiveEntry(vpp, "pulse.vbm"), "the packfile module opens the entry");
        await WaitAsync(() => ctx.Shell.Documents.OfType<VbmDocument>().Any(d => !before.Contains(d)), 8000);
        await ctx.SettleAsync();
        var opened = ctx.Shell.Documents.Where(d => !before.Contains(d)).ToList();
        try
        {
            var doc = opened.OfType<VbmDocument>().FirstOrDefault();
            if (!ctx.Check(doc is not null, $"the entry opens as a bitmap ({string.Join(", ", opened.Select(d => d.Kind.Id))})")) return;
            ctx.Check(doc!.FilePath is { } copy && provider.IsWorkCopy(copy) && doc.Current.FrameCount == 4, "from the packfile's work copy, with its 4 frames");
            var request = module.RequestFor(doc);
            ctx.Check(request.ArchivePath == vpp && request.FilePath is null && request.FileName == "pulse.vbm",
                $"Convert to ATX names the packfile, not the temporary copy ({request.ArchivePath}, {request.FilePath})");
            ctx.Check(!module.DefaultExportFolder(doc).StartsWith(Path.GetDirectoryName(doc.FilePath!)!, StringComparison.OrdinalIgnoreCase),
                "Export does not offer the temporary folder");
        }
        finally
        {
            foreach (var d in opened) ctx.Shell.Close(d);
            await ctx.SettleAsync();
        }
    }

    [SelfTest("vbm.stock")]
    public static async Task Stock(SelfTestContext ctx)
    {
        if (Module(ctx) is null) { ctx.Skip("the bitmap module is not loaded"); return; }
        if (LocalPaths.GameDirectory is not { } game || !Directory.Exists(game)) { ctx.Skip("no game folder"); return; }
        int files = 0, animated = 0;
        string? sample = null;
        byte[]? sampleBytes = null;
        foreach (string vpp in Directory.EnumerateFiles(game, "*.vpp", SearchOption.TopDirectoryOnly))
        {
            VppArchive archive;
            try { archive = VppArchive.Open(vpp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException) { continue; }
            foreach (var entry in archive.Entries.Where(e => e.Name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)))
            {
                byte[] bytes = archive.ReadEntry(entry);
                var read = VbmFile.Read(bytes, entry.Name);
                files++;
                if (!read.File.Write().AsSpan().SequenceEqual(bytes)) ctx.Check(false, $"{entry.Name} writes back unchanged");
                if (read.File.IsAnimated) { animated++; if (sample is null && read.File.FrameCount >= 4) { sample = entry.Name; sampleBytes = bytes; } }
            }
        }
        ctx.Check(files > 0, $"{files} stock bitmaps ({animated} animated) write back byte for byte");
        if (sampleBytes is null) return;
        var doc = (VbmDocument)Module(ctx)!.Kind.OpenBytes(sampleBytes, sample!, sample + " in the game data");
        ctx.Shell.AddDocument(doc);
        try
        {
            await ctx.SettleAsync();
            doc.IsPlaying = true;
            var view = (VbmDocumentView)doc.View;
            int first = view.ShownFrame;
            ctx.Check(await WaitAsync(() => view.ShownFrame != first, 3000), $"{sample} plays ({doc.Current.FrameCount} frames at {doc.Current.Fps} fps)");
            ctx.Check(doc.FilePath is null && doc.Serialize().AsSpan().SequenceEqual(sampleBytes), "an entry opened from bytes has no path and is unchanged");
        }
        finally
        {
            ctx.Shell.Close(doc);
            await ctx.SettleAsync();
        }
    }

    /// <summary>
    /// The <c>--vbm-*</c> options again after the other steps, for a bitmap that opened during them (a packfile's
    /// <c>--vpp-open-in-cairn</c>).
    /// </summary>
    [ScreenshotStep(950)]
    public static async Task ApplyOptionsToLateDocument(ScreenshotContext ctx)
    {
        if (ctx.Shell.ActiveDocument is not VbmDocument || ctx.Shell.Modules.OfType<VbmModule>().FirstOrDefault() is not { } module) return;
        await ctx.SettleAsync();
        module.ApplyDiagnosticOptions(ctx.Options);
        await ctx.SettleAsync();
    }

    [ScreenshotDialog("vbm.export")]
    public static Window? ExportDialog(ScreenshotContext ctx) =>
        new VbmExportWindow(ctx.Shell.Dialogs, "glow.vbm", 16, [2, 3, 4], Path.Combine(Path.GetTempPath(), "frames"), VbmExportFormat.Tga) { Owner = ctx.MainWindow };

    [ScreenshotDialog("vbm.new")]
    public static Window? NewDialog(ScreenshotContext ctx) =>
        new VbmNewWindow([.. Enumerable.Range(0, 6).Select(i => ($"flare_{i:00}.tga", Gradient(64, 64, i)))]) { Owner = ctx.MainWindow };
}
