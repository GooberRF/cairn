using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cairn.Atx.Ui.Services;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Ui.Diagnostics;

namespace Cairn.Atx.Ui.Diagnostics;

/// <summary>Self-tests of every ATX editing flow, driven through the view models (run with --selftest).</summary>
internal static class AtxFlowTests
{
    private static void Pump(int ms = 300) => AtxSelfTestSupport.Pump(ms);

    /// <summary>A fresh copy of samples/atx in a temp folder, so edits and saves never touch the samples.</summary>
    private static string? Workspace(SelfTestContext ctx, string name)
    {
        string? samples = AtxSelfTestSupport.SamplesFolder();
        if (!ctx.Check(samples is not null, "samples/atx found")) return null;
        string dir = Path.Combine(Path.GetTempPath(), "cairn-atx-flow", name);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        foreach (string f in Directory.GetFiles(samples!)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        return dir;
    }

    private static DocumentViewModel Open(string path)
    {
        var doc = (DocumentViewModel)AtxModule.DocumentKind.Open(path);
        Pump();
        return doc;
    }

    /// <summary>Runs one edit, checks it changed the text, then that a single undo restores it exactly.</summary>
    private static void OneStep(SelfTestContext ctx, DocumentViewModel doc, string what, Action select, Action edit)
    {
        select();
        Pump(100);
        string before = doc.TextForSave;
        edit();
        Pump();
        ctx.Check(doc.TextForSave != before, $"{what} changes the document");
        ctx.Check(doc.TextForSave.Contains("# 12.5 frames a second.", StringComparison.Ordinal), $"{what} keeps comments");
        doc.Undo();
        Pump();
        ctx.Check(doc.TextForSave == before, $"{what} is one undo step");
    }

    [SelfTest("atx.frame-edits-one-undo-each")]
    public static void FrameEdits(SelfTestContext ctx)
    {
        if (Workspace(ctx, "edits") is not { } dir) return;
        var dialogs = AtxModule.Workspace!.Dialogs;
        using var doc = Open(Path.Combine(dir, "hazard_strip.atx"));
        var f = doc.Frames;
        void Two() => f.SelectRange(1, 2);
        void All() => f.SelectAll();
        OneStep(ctx, doc, "duplicate", Two, () => f.DuplicateCommand.Execute(null));
        OneStep(ctx, doc, "remove", Two, () => f.RemoveCommand.Execute(null));
        OneStep(ctx, doc, "move up", Two, () => f.MoveUpCommand.Execute(null));
        OneStep(ctx, doc, "move down", Two, () => f.MoveDownCommand.Execute(null));
        OneStep(ctx, doc, "reverse", All, () => f.ReverseCommand.Execute(null));
        OneStep(ctx, doc, "cut", Two, () => f.CutCommand.Execute(null));
        OneStep(ctx, doc, "copy + paste", Two, () => { f.CopyCommand.Execute(null); f.PasteCommand.Execute(null); });
        All();
        f.ReverseCommand.Execute(null); // sort needs an unsorted list; this setup step is undone below
        Pump();
        OneStep(ctx, doc, "sort", All, () => f.SortCommand.Execute(null));
        doc.Undo();
        Pump();
        OneStep(ctx, doc, "add by name", Two, () => doc.AddImageNames(["hazard_strip_00.tga"], -1));

        // Add from disk, outside the search path: the copy-or-reference prompt answered by the hook.
        string outside = Path.Combine(Path.GetTempPath(), "cairn-atx-flow", "outside");
        Directory.CreateDirectory(outside);
        string image = Path.Combine(outside, "outside_frame.tga");
        File.Copy(Path.Combine(dir, "hazard_strip_00.tga"), image, true);
        try
        {
            dialogs.OutsideFilesAnswer = _ => OutsideFileChoice.Reference;
            OneStep(ctx, doc, "add from disk (reference)", Two, () => doc.AddImageFiles([image], -1));
            ctx.Check(!File.Exists(Path.Combine(dir, "outside_frame.tga")), "reference does not copy the image");
            dialogs.OutsideFilesAnswer = _ => OutsideFileChoice.Copy;
            dialogs.ReplaceExistingAnswer = _ => ReplaceChoice.Replace;
            OneStep(ctx, doc, "add from disk (copy)", Two, () => doc.AddImageFiles([image], -1));
            ctx.Check(File.Exists(Path.Combine(dir, "outside_frame.tga")), "copy puts the image next to the .atx");
        }
        finally
        {
            dialogs.OutsideFilesAnswer = null;
            dialogs.ReplaceExistingAnswer = null;
        }

        OneStep(ctx, doc, "bulk timing", All, () =>
        {
            var vm = new BulkTimingViewModel(doc) { Scope = Cairn.Atx.Editing.BulkTimingScope.All, Operation = Cairn.Atx.Editing.BulkTimingOperation.SetValue, Value = 45 };
            ctx.Check(vm.Apply(), "bulk timing applies");
        });
        OneStep(ctx, doc, "add sequence", Two, () =>
        {
            using var vm = new AddSequenceViewModel(doc) { TabIndex = 1, Prefix = "hazard_strip_", PatternStart = 0, PatternEnd = 3, Padding = 2, Extension = ".tga" };
            vm.Apply();
        });
        ctx.Check(!doc.IsDirty, "after undoing every edit the document is clean");
    }

    [SelfTest("atx.source-and-structure-share-undo")]
    public static void SharedUndo(SelfTestContext ctx)
    {
        if (Workspace(ctx, "shared") is not { } dir) return;
        using var doc = Open(Path.Combine(dir, "hazard_strip.atx"));
        string original = doc.TextForSave;
        int frames = doc.FrameCount;
        doc.Document.Insert(0, "# typed in the editor\n");
        Pump();
        string typed = doc.TextForSave;
        doc.Frames.SelectAll();
        doc.Frames.DuplicateCommand.Execute(null);
        Pump();
        ctx.Check(doc.FrameCount == frames * 2 && doc.TextForSave.StartsWith("# typed in the editor", StringComparison.Ordinal),
            "structural edit keeps the typed comment");
        doc.Undo();
        Pump();
        ctx.Check(doc.TextForSave == typed && doc.FrameCount == frames, "first undo takes back the structural edit only");
        doc.Undo();
        Pump();
        ctx.Check(doc.TextForSave == original && !doc.IsDirty, "second undo takes back the typing");
        doc.Redo();
        Pump();
        ctx.Check(doc.TextForSave == typed, "redo replays the typing");
    }

    [SelfTest("atx.lint-and-quick-fix")]
    public static void LintAndQuickFix(SelfTestContext ctx)
    {
        if (Workspace(ctx, "lint") is not { } dir) return;
        using var doc = Open(Path.Combine(dir, "broken_example.atx"));
        Pump(600);
        ctx.Check(doc.Diagnostics.Count > 0, $"broken_example has diagnostics ({doc.Diagnostics.Count})");
        var pair = doc.Diagnostics.SelectMany(d => d.QuickFixes.Select(q => (d, q)))
            .FirstOrDefault(p => p.q.Kind == Cairn.Atx.Linting.QuickFixKind.Edit);
        if (!ctx.Check(pair.q is not null, "an edit quick fix is offered")) return;
        string before = doc.TextForSave;
        int count = doc.Diagnostics.Count;
        doc.ApplyQuickFix(pair.q!, pair.d);
        Pump(600);
        ctx.Check(doc.TextForSave != before, $"quick fix '{pair.q!.Title}' edits the text");
        ctx.Log($"diagnostics {count} -> {doc.Diagnostics.Count}");
        doc.Undo();
        Pump();
        ctx.Check(doc.TextForSave == before, "quick fix is one undo step");
    }

    [SelfTest("atx.preview-playback")]
    public static void PreviewPlayback(SelfTestContext ctx)
    {
        if (Workspace(ctx, "play") is not { } dir) return;
        using var doc = Open(Path.Combine(dir, "hazard_strip.atx"));
        if (!ctx.Check(doc.Model is not null, "model parsed")) return;
        var reference = new Cairn.Atx.Playback.AtxPlayback(Cairn.Atx.Playback.PlaybackSpec.FromModel(doc.Model!));
        reference.Play();
        doc.Preview.Seek(0, pause: true);
        doc.Preview.Play();
        bool same = true;
        for (int i = 0; i < 40; i++)
        {
            doc.Preview.Tick(0.033);
            reference.Advance(0.033);
            same &= doc.Preview.CurrentIndex == reference.CurrentFrame;
        }
        doc.Preview.Pause();
        ctx.Check(same && reference.CurrentFrame != 0, $"preview follows AtxPlayback over 40 ticks (frame {reference.CurrentFrame})");
    }

    [SelfTest("atx.save-keeps-encoding-and-eol")]
    public static void SaveEncodings(SelfTestContext ctx)
    {
        if (Workspace(ctx, "encoding") is not { } dir) return;
        string body = File.ReadAllText(Path.Combine(dir, "hazard_strip.atx")).Replace("\r\n", "\n") + "# café\n";
        var cases = new (string Name, byte[] Bytes, bool Bom, string Eol)[]
        {
            ("utf8-lf", Encoding.UTF8.GetBytes(body), false, "\n"),
            // 1.1.0 always writes UTF-8 without a BOM (AtomicFile.SaveDocument), so a BOM is dropped on save.
            ("bom-crlf", [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(body.Replace("\n", "\r\n"))], false, "\r\n"),
            ("ansi-crlf", Encoding.Latin1.GetBytes(body.Replace("\n", "\r\n")), false, "\r\n"),
        };
        foreach (var c in cases)
        {
            string path = Path.Combine(dir, c.Name + ".atx");
            File.WriteAllBytes(path, c.Bytes);
            using var doc = Open(path);
            ctx.Check(doc.TextForSave.Contains("café", StringComparison.Ordinal), $"{c.Name}: decoded correctly");
            doc.Frames.SelectRange(0, 1);
            doc.Frames.DuplicateCommand.Execute(null);
            Pump();
            doc.SaveTo(path);
            byte[] saved = File.ReadAllBytes(path);
            bool bom = saved.Length > 2 && saved[0] == 0xEF && saved[1] == 0xBB && saved[2] == 0xBF;
            string text = new UTF8Encoding(false, true).GetString(saved, bom ? 3 : 0, saved.Length - (bom ? 3 : 0));
            bool eolOk = c.Eol == "\r\n" ? !text.Replace("\r\n", "").Contains('\n') : !text.Contains('\r');
            // ANSI files are converted to UTF-8 on save (with the notice), as in 1.1.0.
            ctx.Check(bom == c.Bom && eolOk && text.Contains("café", StringComparison.Ordinal) && !doc.IsDirty,
                $"{c.Name}: saved with BOM={bom}, line endings kept, text intact");
        }
    }

    [SelfTest("atx.external-change")]
    public static void ExternalChange(SelfTestContext ctx)
    {
        if (Workspace(ctx, "external") is not { } dir) return;
        string path = Path.Combine(dir, "hazard_strip.atx");
        using var doc = Open(path);
        doc.Document.Insert(0, "# mine\n");
        Pump();
        File.AppendAllText(path, "# theirs\n");
        Pump(1500);
        ctx.Check(doc.HasExternalChange, "a dirty document flags a change on disk");
        doc.KeepMine();
        ctx.Check(!doc.HasExternalChange && doc.IsDirty && doc.TextForSave.StartsWith("# mine", StringComparison.Ordinal), "Keep mine keeps the edit");
        doc.ReloadFromDisk();
        Pump();
        ctx.Check(doc.TextForSave.Contains("# theirs", StringComparison.Ordinal) && !doc.TextForSave.StartsWith("# mine", StringComparison.Ordinal),
            "Reload takes the disk version");
        File.Delete(path);
        Pump(1500);
        ctx.Check(doc.IsMissingOnDisk, "deleting the file shows missing-on-disk");
    }

    [SelfTest("atx.second-tab-refused")]
    public static void SecondTabRefused(SelfTestContext ctx)
    {
        if (Workspace(ctx, "tabs") is not { } dir) return;
        string path = Path.Combine(dir, "hazard_strip.atx");
        ctx.Shell.OpenFile(path);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        ctx.Shell.OpenFile(path);
        long reopenMs = clock.ElapsedMilliseconds;
        Pump();
        var open = ctx.Shell.Documents.Where(d => d.FilePath is { } p && string.Equals(Path.GetFullPath(p), path, StringComparison.OrdinalIgnoreCase)).ToList();
        ctx.Check(open.Count == 1, $"opening the same .atx twice gives one tab ({open.Count})");
        ctx.Check(open.Count == 1 && ctx.Shell.ActiveDocument == open[0] && reopenMs < 2000, $"the second open activates the existing tab at once ({reopenMs} ms)");
        foreach (var d in open) ctx.Shell.Close(d);
    }

    [SelfTest("atx.large-file-off-ui-thread")]
    public static void LargeFile(SelfTestContext ctx)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cairn-atx-selftest", "large");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "large.atx");
        var text = new System.Text.StringBuilder("[header]\nframe_time = 80\nanimation_mode = 2\n");
        while (text.Length < AtxTextFiles.LargeFileBytes + 4096)
            text.Append("# padding so the file is above the large-file threshold and parses off the UI thread\n");
        for (int i = 0; i < 3; i++) text.Append($"\n[[frame]]\nfile = \"missing_{i:00}.tga\"\n");
        File.WriteAllText(path, text.ToString());
        try
        {
            var doc = (DocumentViewModel)AtxModule.DocumentKind.Open(path);
            ctx.Check(doc.IsOpening, "a file above the threshold opens in the Opening state (parse deferred)");
            ctx.Shell.AddDocument(doc);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int pumps = 0;
            while (doc.IsOpening && clock.ElapsedMilliseconds < 20000) { Pump(50); pumps++; }
            ctx.Check(!doc.IsOpening && pumps > 0, $"background parse finished while the UI kept pumping ({clock.ElapsedMilliseconds} ms, {pumps} pumps)");
            Pump();
            ctx.Check(doc.Frames.Rows.Count == 3, $"frames arrive after the parse ({doc.Frames.Rows.Count})");
            ctx.Check(doc.Diagnostics.Count > 0, $"diagnostics arrive (missing frame files: {doc.Diagnostics.Count})");
            ctx.Shell.Close(doc);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [SelfTest("atx.archive-entry-read-only-origin")]
    public static void ArchiveEntry(SelfTestContext ctx)
    {
        if (Workspace(ctx, "bytes") is not { } dir) return;
        using var doc = (DocumentViewModel)AtxModule.DocumentKind.OpenBytes(
            File.ReadAllBytes(Path.Combine(dir, "hazard_strip.atx")), "hazard_strip.atx", "hazard_strip.atx in test.vpp");
        Pump();
        ctx.Check(doc.IsReadOnly && doc.FilePath is null && doc.TabToolTip.Contains("test.vpp", StringComparison.Ordinal),
            "an archive entry opens read-only with its origin");
    }

    /// <summary>A 4x4 RGB565 .vbm with <paramref name="frames"/> flat frames, one mip level.</summary>
    internal static byte[] Vbm(int frames)
    {
        using var ms = new MemoryStream();
        var header = new byte[32];
        uint[] fields = [0x6D62762E, 1, 4, 4, 2, 10, (uint)frames, 0];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(i * 4), fields[i]);
        ms.Write(header);
        for (int f = 0; f < frames; f++)
            for (int p = 0; p < 16; p++) { ms.WriteByte((byte)(f * 40)); ms.WriteByte(0xF8); }
        return ms.ToArray();
    }

    [SelfTest("atx.vbm-import")]
    public static void VbmImport(SelfTestContext ctx)
    {
        if (Workspace(ctx, "vbm") is not { } dir) return;
        string vbm = Path.Combine(dir, "glow.vbm");
        File.WriteAllBytes(vbm, Vbm(3));
        var source = VbmImportSource.FromFile(vbm);
        var load = VbmImportLoader.Load(source);
        if (!ctx.Check(load.Bytes is not null && load.Info is not null, $"generated .vbm loads ({load.Error})")) return;
        string output = Path.Combine(dir, "imported");
        Directory.CreateDirectory(output);
        using var vm = new VbmImportViewModel(AtxModule.Workspace!, source, load.Bytes!, load.Info!) { OutputFolder = output };
        ctx.Check(vm.CanImport, "import plan can run");
        vm.ImportCommand.Execute(null);
        for (int i = 0; i < 50 && vm.ImportedPath is null; i++) Pump(100);
        ctx.Check(vm.ImportedPath is { } atx && File.Exists(atx), $"import wrote {vm.ImportedPath}");
        if (vm.ImportedPath is { } written)
        {
            using var doc = Open(written);
            ctx.Check(doc.FrameCount == 3, $"imported .atx has 3 frames ({doc.FrameCount})");
        }
    }

    [SelfTest("atx.vpp-browser")]
    public static void VppBrowser(SelfTestContext ctx)
    {
        if (Workspace(ctx, "vpp") is not { } dir) return;
        byte[] tga = File.ReadAllBytes(Path.Combine(dir, "hazard_strip_00.tga"));
        string vpp = Path.Combine(dir, "test_frames.vpp");
        File.WriteAllBytes(vpp, AtxSelfTestSupport.BuildVpp([("vppframe_a.tga", tga), ("vppframe_b.tga", tga)]));
        using var doc = Open(Path.Combine(dir, "hazard_strip.atx"));
        using var vm = new VppBrowserViewModel(doc, VppBrowserMode.AddFrames);
        vm.OpenArchive(vpp);
        Pump(800);
        var images = new List<VppImageNodeViewModel>();
        void Walk(IEnumerable<VppNodeViewModel> nodes)
        {
            foreach (var n in nodes)
            {
                if (n is VppImageNodeViewModel img && img.Label.StartsWith("vppframe_", StringComparison.Ordinal)) images.Add(img);
                Walk(n.Children);
            }
        }
        Walk(vm.Roots);
        ctx.Check(images.Count == 2, $"browser lists the archive's images ({images.Count})");
        int frames = doc.FrameCount;
        foreach (var img in images) img.IsChecked = true;
        ctx.Check(vm.Apply(), "browser applies");
        Pump();
        ctx.Check(doc.FrameCount == frames + images.Count, $"checked images become frames ({doc.FrameCount})");
        doc.Undo();
        Pump();
        ctx.Check(doc.FrameCount == frames, "adding from a .vpp is one undo step");
    }
}

/// <summary>Helpers shared by the ATX self-tests.</summary>
internal static class AtxSelfTestSupport
{
    /// <summary>Runs the dispatcher for <paramref name="ms"/> so background parses and bindings settle.</summary>
    /// <remarks>Bounded: see <see cref="Cairn.Ui.Diagnostics.SelfTestPump"/> (a stalled Background queue once held a test for minutes).</remarks>
    public static void Pump(int ms) => Cairn.Ui.Diagnostics.SelfTestPump.Pump(ms);

    /// <summary>The repository's samples/atx folder, found by walking up from the executable.</summary>
    public static string? SamplesFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "atx"))) return Path.Combine(dir.FullName, "samples", "atx");
        return null;
    }

    /// <summary>A version 1 .vpp holding <paramref name="files"/>.</summary>
    public static byte[] BuildVpp(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        const int Block = Cairn.Formats.Vpp.VppArchive.BlockSize;
        static int Align(int v) => (v + Block - 1) / Block * Block;
        using var ms = new MemoryStream();
        var header = new byte[Block];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Cairn.Formats.Vpp.VppArchive.Signature);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)files.Count);
        ms.Write(header);
        var directory = new byte[Align(files.Count * Cairn.Formats.Vpp.VppArchive.EntryBytes)];
        for (int i = 0; i < files.Count; i++)
        {
            int at = i * Cairn.Formats.Vpp.VppArchive.EntryBytes;
            Encoding.ASCII.GetBytes(files[i].Name).CopyTo(directory, at);
            BinaryPrimitives.WriteInt32LittleEndian(directory.AsSpan(at + Cairn.Formats.Vpp.VppArchive.NameBytes), files[i].Data.Length);
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
}
