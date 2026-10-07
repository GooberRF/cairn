using System.Diagnostics;
using System.Windows.Controls;
using Cairn.Ui.Diagnostics;
using Cairn.Ui.Services;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Commands;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.List;
using Cairn.Vpp.Writing;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui.Diagnostics;

/// <summary>
/// Packfile document self-tests (<c>Cairn.exe --selftest</c>): everything is driven through the document, its
/// commands and the list view model, with prompts answered by the dialog service's non-interactive hook. Files are
/// generated in a temporary folder; game packfiles are only read (a copy is saved, never the original).
/// </summary>
public static class VppDocumentSelfTests
{
    private sealed class Kit : IDisposable
    {
        public Kit(SelfTestContext ctx)
        {
            Ctx = ctx;
            Module = ctx.Shell.Modules.OfType<VppModule>().First();
            Folder = Path.Combine(Path.GetTempPath(), "cairn-vpp-test-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Folder);
        }

        public SelfTestContext Ctx { get; }
        public VppModule Module { get; }
        public string Folder { get; }
        private readonly List<VppDocument> _docs = [];

        public string File(string name, int size, int seed)
        {
            var bytes = new byte[size];
            new Random(seed).NextBytes(bytes);
            string path = Path.Combine(Folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>A packfile of <paramref name="count"/> generated entries (mixed types and sizes) written to disk.</summary>
        public string Packfile(string name, int count, int maxSize = 5000)
        {
            string[] exts = [".tga", ".wav", ".v3m", ".rfl", ".tbl", ".vfx", ".rfa", ".txt"];
            var rng = new Random(count);
            var p = VppPackage.Empty;
            var items = new List<(string, VppSource)>();
            for (int i = 0; i < count; i++)
            {
                var bytes = new byte[rng.Next(0, maxSize)];
                rng.NextBytes(bytes);
                items.Add(($"entry{i:D4}{exts[i % exts.Length]}", new MemorySource(bytes)));
            }
            p = VppEdit.AddSources(p, items, VppClashPolicy.KeepBoth).Package;
            string path = Path.Combine(Folder, name);
            VppSaver.Save(p, path, null, CancellationToken.None, VppSaveOptions.Default);
            return path;
        }

        public VppDocument Open(string path, bool show = false)
        {
            var doc = (VppDocument)Module.Kind.Open(path);
            if (show) Ctx.Shell.AddDocument(doc);
            _docs.Add(doc);
            return doc;
        }

        public VppDocument Track(VppDocument doc) { _docs.Add(doc); return doc; }

        public static byte[] Entry(VppPackage p, string name) => p.Find(name)!.Source.ReadAll();

        public void Dispose()
        {
            foreach (var d in _docs)
            {
                if (Ctx.Shell.Documents.Contains(d)) Ctx.Shell.Close(d); else d.Dispose();
            }
            VppWorkFolder_TryDelete(Folder);
        }

        private static void VppWorkFolder_TryDelete(string folder) => Work.VppWorkFolder.TryDeleteFolder(folder);
    }

    private static void Answer(SelfTestContext ctx, Func<string, IReadOnlyList<string>, int>? hook)
    {
        if (ctx.Shell.Dialogs is DialogService service) service.NonInteractiveChoice = hook;
    }

    [SelfTest("vpp.new-add-save-reopen")]
    public static async Task NewAddSaveReopen(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Track((VppDocument)kit.Module.Kind.CreateNew()!);
        ctx.Check(doc.Current.Count == 0 && !doc.IsDirty, "File > New > Packfile gives an empty, clean document");
        var a = kit.File("in/a.tga", 3000, 1);
        var b = kit.File("in/b.wav", 10, 2);
        var c = kit.File("in/sub/c.tbl", 2049, 3);
        var report = await doc.Commands.AddPathsAsync([a, b]);
        ctx.Check(report?.Added.Length == 2 && doc.Current.Count == 2, "two files added");
        ctx.Check(doc.UndoLabel == "Add 2 files", $"one undo step labelled 'Add 2 files' ({doc.UndoLabel})");
        await doc.Commands.AddPathsAsync([Path.Combine(kit.Folder, "in", "sub")]);
        ctx.Check(doc.Current.Contains("c.tbl") && doc.UndoLabel == "Add c.tbl", "a folder adds its files flattened, one undo step");
        ctx.Check(doc.IsDirty && doc.PendingChanges == 3, $"dirty with 3 pending changes ({doc.PendingChanges})");
        string target = Path.Combine(kit.Folder, "new.vpp");
        doc.SaveTo(target);
        ctx.Check(!doc.IsDirty && doc.FilePath == target && doc.PendingChanges == 0, "saved: clean, path set, no pending changes");
        var reopened = VppPackage.Open(target);
        ctx.Check(reopened.Items.Select(i => i.Name).SequenceEqual(["a.tga", "b.wav", "c.tbl"]), "re-opened entries in order");
        ctx.Check(Kit.Entry(reopened, "a.tga").AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(a)) && Kit.Entry(reopened, "c.tbl").AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(c)), "re-opened data equals the source files");
        string longName = Path.Combine(kit.Folder, "a_packfile_name_that_is_far_too_long.vpp");
        doc.SaveTo(longName);
        ctx.Check(doc.Notice?.Contains("31", StringComparison.Ordinal) == true, "a file name over 31 characters shows the notice");
        ctx.Check(doc.Problems.Any(p => p.Code is "VPP024" or "VPP025"), "... and a problem");
    }

    [SelfTest("vpp.clash-policies")]
    public static async Task ClashPolicies(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("clash.vpp", 8));
        int count = doc.Current.Count;
        var file = kit.File("x/entry0000.tga", 77, 9);
        await doc.Commands.AddPathsAsync([file], VppClashPolicy.Replace);
        ctx.Check(doc.Current.Count == count && doc.Current.Find("entry0000.tga")!.State == VppItemState.Replaced && doc.Current.Find("entry0000.tga")!.Size == 77, "Replace keeps the count and replaces the data");
        doc.Undo();
        await doc.Commands.AddPathsAsync([file], VppClashPolicy.KeepBoth);
        ctx.Check(doc.Current.Count == count + 1 && doc.Current.Contains("entry0000 (2).tga"), "Keep both adds 'entry0000 (2).tga'");
        doc.Undo();
        var none = await doc.Commands.AddPathsAsync([file], VppClashPolicy.Skip);
        ctx.Check(none is null && !doc.IsDirty, "Skip changes nothing");
        // the prompt: a batch with two clashes, "Keep both for all"
        var second = kit.File("x/entry0001.wav", 5, 10);
        var asked = new List<string>();
        Answer(ctx, (heading, buttons) => { asked.Add(heading); return buttons.ToList().FindIndex(b => b.Contains("Keep both", StringComparison.Ordinal)); });
        try { await doc.Commands.AddPathsAsync([file, second]); }
        finally { Answer(ctx, null); }
        ctx.Check(asked.Count == 1 && asked[0].StartsWith("2 names", StringComparison.Ordinal), $"one prompt for the batch ({string.Join(" | ", asked)})");
        ctx.Check(doc.Current.Count == count + 2, "Keep both for all added both");
        doc.Undo();
        Answer(ctx, (_, buttons) => buttons.Count - 1); // Cancel
        try { await doc.Commands.AddPathsAsync([file]); }
        finally { Answer(ctx, null); }
        ctx.Check(!doc.IsDirty, "cancelling the clash prompt adds nothing");
    }

    [SelfTest("vpp.edits-undo-redo")]
    public static async Task EditsUndoRedo(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("edits.vpp", 12));
        byte[] original = doc.Serialize();
        var file = kit.File("r/new.tga", 4100, 4);
        var folder = Path.GetDirectoryName(kit.File("f/one.txt", 12, 5))!;
        kit.File("f/deep/two.txt", 13, 6);
        var edits = new (string Name, Func<Task> Run)[]
        {
            ("add file", async () => await doc.Commands.AddPathsAsync([file])),
            ("add folder", async () => await doc.Commands.AddPathsAsync([folder], VppClashPolicy.KeepBoth)),
            ("rename", () => { ctx.Check(doc.Commands.Rename("entry0003.rfl", "renamed.rfl") is null, "rename accepted"); return Task.CompletedTask; }),
            ("replace", () => { ctx.Check(doc.Commands.ReplaceWith("entry0004.tbl", file), "replace accepted"); return Task.CompletedTask; }),
            ("remove", () => { doc.SelectNames(["entry0005.vfx", "entry0006.rfa"]); doc.Commands.RemoveSelected(); return Task.CompletedTask; }),
            ("sort", () => { doc.Commands.SortPackfile(VppSortKey.Size, descending: true); return Task.CompletedTask; }),
        };
        foreach (var (name, run) in edits)
        {
            byte[] before = doc.Serialize();
            await run();
            byte[] after = doc.Serialize();
            ctx.Check(!after.AsSpan().SequenceEqual(before), $"{name}: changes the packfile ({doc.UndoLabel})");
            doc.Undo();
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(before), $"{name}: undo restores byte-identical output");
            doc.Redo();
            ctx.Check(doc.Serialize().AsSpan().SequenceEqual(after), $"{name}: redo restores byte-identical output");
        }
        ctx.Check(doc.Commands.Rename("renamed.rfl", "entry0000.tga") is not null, "renaming onto a taken name is refused");
        ctx.Check(doc.Commands.Rename("renamed.rfl", "a/b.rfl") is not null, "a name with a path separator is refused");
        while (doc.CanUndo) doc.Undo();
        ctx.Check(doc.Serialize().AsSpan().SequenceEqual(original) && !doc.IsDirty, "undoing everything gives the original bytes and a clean document");
        ctx.Check(doc.Serialize().AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(doc.FilePath!)), "... equal to the file on disk");
    }

    [SelfTest("vpp.types-panel-filters-after-edits")]
    public static void TypesPanelFiltersAfterEdits(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("panel.vpp", 40));
        var panel = VppTypesPanel.For(doc);
        // Any snapshot change rebuilds the type options; the panel must bind to the new ones.
        doc.Commands.Rename("entry0001.wav", "renamed.wav");
        var shown = panel.ShownOptions;
        ctx.Check(shown.SequenceEqual(doc.List.TypeOptions), "the panel shows the current type options after an edit");
        var wav = shown.FirstOrDefault(o => !o.IsCategory && o.Key == ".wav");
        var rfl = shown.FirstOrDefault(o => !o.IsCategory && o.Key == ".rfl");
        ctx.Check(wav is not null && rfl is not null, "the panel lists .wav and .rfl");
        if (wav is null || rfl is null) return;
        wav.IsChecked = true;
        ctx.Check(doc.List.Visible.Count > 0 && doc.List.Visible.All(r => r.Extension == ".wav"), $"ticking .wav in the panel shows only .wav ({doc.List.Visible.Count})");
        rfl.IsChecked = true;
        ctx.Check(doc.List.Visible.All(r => r.Extension is ".wav" or ".rfl") && doc.List.Visible.Any(r => r.Extension == ".rfl"), "ticking .rfl as well shows both");
        doc.List.ShowOnlyTypes([]);
        ctx.Check(!doc.List.IsFiltered && panel.ShownOptions.All(o => !o.IsChecked), "Show all types clears the panel's ticks");
    }

    [SelfTest("vpp.types-panel-problems-only-when-present")]
    public static async Task TypesPanelProblemsOnlyWhenPresent(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("problems.vpp", 8));
        var panel = VppTypesPanel.For(doc);
        var option = doc.List.LongNameOption;
        bool Shown() => panel.LongNames.Visibility == System.Windows.Visibility.Visible && panel.ProblemsHeader.Visibility == System.Windows.Visibility.Visible;
        bool Hidden() => panel.LongNames.Visibility == System.Windows.Visibility.Collapsed && panel.ProblemsHeader.Visibility == System.Windows.Visibility.Collapsed;
        ctx.Check(option.Count == 0 && Hidden(), "no long names: the Problems header and its box are collapsed");
        string longName = new string('t', 36) + ".tga";
        await doc.Commands.AddPathsAsync([kit.File("long/" + longName, 10, 4)]);
        ctx.Check(doc.Current.Contains(longName) && option.Count == 1 && Shown(), $"adding a 40-character texture name shows them ({option.Count})");
        panel.LongNames.IsChecked = true; // as a click would
        ctx.Check(doc.List.Visible.Select(r => r.Name).SequenceEqual([longName]), "ticking the box lists only the long name");
        ctx.Check(doc.Commands.Rename(longName, "short.tga") is null, "the long name is renamed to fit");
        ctx.Check(Hidden() && !option.IsChecked && panel.LongNames.IsChecked != true, "renaming the last long name hides the group and unticks the box");
        ctx.Check(!doc.List.IsFiltered && doc.List.Visible.Count == doc.Current.Count, $"... so the list is not left filtered ({doc.List.Visible.Count} of {doc.Current.Count})");
        doc.Undo();
        ctx.Check(Shown() && !option.IsChecked, "undoing the rename shows the box again, unticked");
        doc.Undo();
        ctx.Check(!doc.Current.Contains(longName) && Hidden(), "removing the long-named entry (undoing the add) hides the group again");
    }

    [SelfTest("vpp.list-filter-sort")]
    public static void ListFilterSort(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("list.vpp", 40));
        var list = doc.List;
        ctx.Check(list.Visible.Count == 40 && !list.IsFiltered, "all 40 rows shown");
        list.FilterText = "ENTRY001";
        ctx.Check(list.Visible.Count == 10 && list.FilterSummary == "10 of 40 shown", $"substring, case-insensitive ({list.Visible.Count})");
        list.FilterText = "*.tga";
        ctx.Check(list.Visible.Count == 5 && list.Visible.All(r => r.Extension == ".tga"), "wildcard *.tga");
        list.FilterText = "entry00?2.*";
        ctx.Check(list.Visible.Select(r => r.Name[..9]).SequenceEqual(["entry0002", "entry0012", "entry0022", "entry0032"]), "wildcard ? matches one character");
        list.FilterText = "";
        list.ShowOnlyTypes([".wav", ".rfl"]);
        ctx.Check(list.Visible.Count == 10 && list.TypeFilterLabel is ".rfl, .wav" or ".wav, .rfl",$"type filter .wav + .rfl ({list.Visible.Count}, {list.TypeFilterLabel})");
        var category = list.TypeOptions.First(o => o.IsCategory && o.Category == Facts.VppFileCategory.Image);
        list.ShowOnlyTypes([]);
        category.IsChecked = true;
        ctx.Check(list.Visible.Count > 0 && list.Visible.All(r => r.Category == Facts.VppFileCategory.Image), "checking a category shows its extensions");
        ctx.Check(category.Count == list.Visible.Count, $"category count matches ({category.Count})");
        list.ShowOnlyTypes([]);
        list.SortBy(VppListSort.Size, descending: true);
        ctx.Check(list.Visible.Zip(list.Visible.Skip(1)).All(p => p.First.Size >= p.Second.Size), "sorted by size, descending (by bytes)");
        list.SortBy(VppListSort.Name, descending: false);
        ctx.Check(list.Visible.Zip(list.Visible.Skip(1)).All(p => string.Compare(p.First.Name, p.Second.Name, StringComparison.OrdinalIgnoreCase) <= 0), "sorted by name");
        list.SortBy(VppListSort.PackfileOrder, descending: false);
        doc.Commands.Rename("entry0001.wav", "zzz.wav");
        ctx.Check(list.Visible[1].Name == "zzz.wav" && list.Visible[1].StateText == "renamed", "a rename shows in place with state 'renamed'");
        ctx.Check(VppEntryRow.FormatSize(1536) == 1.5.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " KB", "human-readable size");
    }

    [SelfTest("vpp.save-over-itself-and-cancel")]
    public static async Task SaveOverItselfAndCancel(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        string path = kit.Packfile("self.vpp", 30, 400_000);
        var doc = kit.Open(path);
        doc.Commands.Rename("entry0002.v3m", "moved.v3m");
        byte[] expected = doc.Serialize();
        byte[] before = System.IO.File.ReadAllBytes(path);

        // cancel at the first progress report: the file must stay as it was, with no temporary file left
        void CancelSoon(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VppDocument.Operation) && doc.Operation is { } op) op.Cancel();
        }
        doc.PropertyChanged += CancelSoon;
        bool threw = false;
        try { doc.SaveTo(path); }
        catch (OperationCanceledException ex) { threw = ex.Message.Contains("cancelled", StringComparison.OrdinalIgnoreCase); }
        finally { doc.PropertyChanged -= CancelSoon; }
        ctx.Check(threw, "a cancelled save reports that it was cancelled (OperationCanceledException)");
        ctx.Check(System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "... and leaves the file untouched");
        ctx.Check(Directory.GetFiles(kit.Folder, "*.tmp").Length == 0, "... with no temporary file left");
        ctx.Check(doc.IsDirty && !doc.IsBusy, "... and the document still dirty and idle");

        // the same through the shell's Save (Ctrl+S): a quiet end, a status message and no error dialog
        var collected = (ctx.Shell.Dialogs as DialogService)?.CollectErrors;
        int errorsBefore = collected?.Count ?? 0;
        doc.PropertyChanged += CancelSoon;
        bool saved;
        try { saved = ctx.Shell.Save(doc); }
        finally { doc.PropertyChanged -= CancelSoon; }
        string? status = ctx.Shell.GetType().GetProperty("StatusText")?.GetValue(ctx.Shell) as string;
        ctx.Check(!saved && doc.IsDirty, "a save cancelled through the shell returns false and the document stays dirty");
        ctx.Check(status == "Save cancelled; the file was not changed.", $"... the status says so ({status})");
        ctx.Check(collected is null || collected.Count == errorsBefore, $"... and no error dialog was shown ({(collected?.Count ?? 0) - errorsBefore})");
        ctx.Check(System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(before), "... and the file is untouched");

        var sw = Stopwatch.StartNew();
        doc.SaveTo(path);
        ctx.Log($"saved {new FileInfo(path).Length:N0} bytes over itself in {sw.ElapsedMilliseconds} ms");
        ctx.Check(System.IO.File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), "saving over itself writes the expected bytes");
        ctx.Check(!doc.IsDirty && !doc.CanUndo && doc.Current.Contains("moved.v3m"), "clean afterwards, entries now read from the new file");
        ctx.Check(doc.Serialize().AsSpan().SequenceEqual(expected), "the document reads its data from the saved file correctly");
        await Task.Yield();
    }

    [SelfTest("vpp.extract")]
    public static async Task Extract(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("extract.vpp", 16));
        string outAll = Path.Combine(kit.Folder, "all");
        var written = await doc.Commands.ExtractAsync(doc.Current.Items, outAll);
        ctx.Check(written?.Count == 16, "every entry extracted");
        ctx.Check(doc.Current.Items.All(i => System.IO.File.ReadAllBytes(Path.Combine(outAll, i.Name)).AsSpan().SequenceEqual(i.Source.ReadAll())), "extracted bytes equal the entries");
        doc.SelectNames(["entry0003.rfl", "entry0004.tbl"]);
        string outSel = Path.Combine(kit.Folder, "sel");
        await doc.Commands.ExtractAsync(doc.SelectedItems, outSel);
        ctx.Check(Directory.GetFiles(outSel).Length == 2, "only the selection extracted");
        System.IO.File.WriteAllText(Path.Combine(outSel, "entry0003.rfl"), "mine");
        await doc.Commands.ExtractAsync(doc.SelectedItems, outSel, VppOverwritePolicy.SkipExisting);
        ctx.Check(System.IO.File.ReadAllText(Path.Combine(outSel, "entry0003.rfl")) == "mine", "Skip existing keeps the file on disk");
        Answer(ctx, (_, buttons) => 0); // Overwrite
        try { await doc.Commands.ExtractAsync(doc.SelectedItems, outSel); }
        finally { Answer(ctx, null); }
        ctx.Check(System.IO.File.ReadAllBytes(Path.Combine(outSel, "entry0003.rfl")).AsSpan().SequenceEqual(Kit.Entry(doc.Current, "entry0003.rfl")), "Overwrite (answered in the prompt) replaces it");
        var staged = await doc.Commands.StageSelectionAsync();
        ctx.Check(staged?.Count == 2 && staged.All(System.IO.File.Exists), "staging for the clipboard / drag-out writes the files");
    }

    /// <summary>
    /// Review finding 9: two entries mapping to one file name, or a file already at the destination, are resolved by
    /// Overwrite / Keep both / Skip (one prompt per clash, or answers for the whole batch); unattended: Keep both.
    /// </summary>
    [SelfTest("vpp.extract-collisions")]
    public static async Task ExtractCollisions(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        // "Ü.tga" and "ü.tga" are distinct entries (only A-Z case is folded) but one file name on NTFS
        var pkg = VppEdit.AddSources(VppPackage.Empty, [("Ü.tga", new MemorySource([1, 1, 1])), ("ü.tga", new MemorySource([2, 2])), ("b.tbl", new MemorySource([3]))], VppClashPolicy.KeepBoth).Package;
        string path = Path.Combine(kit.Folder, "twins.vpp");
        VppSaver.Save(pkg, path, null, CancellationToken.None, VppSaveOptions.Default);
        var doc = kit.Open(path);
        var asked = new List<string>();
        int run = 0;
        async Task<(IReadOnlyList<string>? Written, string Dir)> Extract(string? answer, bool mineOnDisk, params string[] more)
        {
            string dir = Path.Combine(kit.Folder, "x" + run++);
            if (mineOnDisk)
            {
                Directory.CreateDirectory(dir);
                System.IO.File.WriteAllText(Path.Combine(dir, "Ü.tga"), "mine");
                System.IO.File.WriteAllText(Path.Combine(dir, "b.tbl"), "mine");
            }
            asked.Clear();
            var queue = new Queue<string>(more);
            Func<string, IReadOnlyList<string>, int> hook = (heading, buttons) =>
            {
                asked.Add(heading);
                string want = asked.Count == 1 ? answer! : queue.Count > 0 ? queue.Dequeue() : "Cancel";
                return buttons.ToList().FindIndex(b => b.Replace("_", "", StringComparison.Ordinal).StartsWith(want, StringComparison.Ordinal));
            };
            Answer(ctx, answer is null ? null : hook);
            try { return (await doc.Commands.ExtractAsync(doc.Current.Items, dir), dir); }
            finally { Answer(ctx, null); }
        }
        static byte[] Read(string dir, string name) => System.IO.File.Exists(Path.Combine(dir, name)) ? System.IO.File.ReadAllBytes(Path.Combine(dir, name)) : [];
        static int Count(string dir) => Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0;

        // two entries of the batch, one file name: one prompt for that clash
        var (w, d) = await Extract("Overwrite", false);
        ctx.Check(asked.Count == 1 && asked[0].StartsWith("Another selected entry", StringComparison.Ordinal) && w?.Count == 2 && Count(d) == 2 && Read(d, "ü.tga").SequenceEqual(new byte[] { 2, 2 }),
            $"in-batch Overwrite: the later entry wins, 2 files ({string.Join(" | ", asked)})");
        (w, d) = await Extract("Keep both", false);
        ctx.Check(w?.Count == 3 && Read(d, "Ü.tga").SequenceEqual(new byte[] { 1, 1, 1 }) && Read(d, "ü (2).tga").SequenceEqual(new byte[] { 2, 2 }), "in-batch Keep both: 'ü (2).tga' beside the first");
        (w, d) = await Extract("Skip", false);
        ctx.Check(w?.Count == 2 && Count(d) == 2 && Read(d, "Ü.tga").SequenceEqual(new byte[] { 1, 1, 1 }), "in-batch Skip: the later entry is not extracted");
        (w, d) = await Extract("Cancel", false);
        ctx.Check(w is null && Count(d) == 0, "Cancel writes nothing");

        // files already on disk ("Ü.tga" and "b.tbl"): three clashes, one prompt for the batch
        (w, d) = await Extract("Skip all", true);
        ctx.Check(asked.Count == 1 && asked[0].StartsWith("3 files", StringComparison.Ordinal) && w?.Count == 0 && System.IO.File.ReadAllText(Path.Combine(d, "Ü.tga")) == "mine" && System.IO.File.ReadAllText(Path.Combine(d, "b.tbl")) == "mine",
            $"Skip all keeps the files on disk ({string.Join(" | ", asked)})");
        (w, d) = await Extract("Keep both for all", true);
        ctx.Check(w?.Count == 3 && Count(d) == 5 && System.IO.File.ReadAllText(Path.Combine(d, "Ü.tga")) == "mine" && Read(d, "Ü (2).tga").SequenceEqual(new byte[] { 1, 1, 1 })
            && Read(d, "ü (3).tga").SequenceEqual(new byte[] { 2, 2 }) && Read(d, "b (2).tbl").SequenceEqual(new byte[] { 3 }), "Keep both for all: free names, nothing overwritten");
        (w, d) = await Extract("Overwrite all", true);
        ctx.Check(w?.Count == 2 && Count(d) == 2 && Read(d, "Ü.tga").SequenceEqual(new byte[] { 2, 2 }) && Read(d, "b.tbl").SequenceEqual(new byte[] { 3 }), "Overwrite all: the entries replace the files (the later twin last)");
        (w, d) = await Extract("Decide for each", true, "Overwrite", "Skip", "Keep both");
        ctx.Check(asked.Count == 4 && Read(d, "Ü.tga").SequenceEqual(new byte[] { 1, 1, 1 }) && System.IO.File.ReadAllText(Path.Combine(d, "b.tbl")) == "mine" && Read(d, "b (2).tbl").SequenceEqual(new byte[] { 3 }) && w?.Count == 2,
            $"Decide for each: one prompt per clash, each answer applied ({asked.Count} prompts)");

        // unattended run without an answer: the safe default, Keep both, without a prompt
        (w, d) = await Extract(null, true);
        ctx.Check(w?.Count == 3 && Count(d) == 5 && System.IO.File.ReadAllText(Path.Combine(d, "Ü.tga")) == "mine", "unattended: Keep both, nothing overwritten");
        ctx.Check(System.IO.File.ReadAllText(Path.Combine(d, "b.tbl")) == "mine" && Read(d, "b (2).tbl").SequenceEqual(new byte[] { 3 }), "... for every clash");
    }

    [SelfTest("vpp.work-copy-update")]
    public static async Task WorkCopyUpdate(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("work.vpp", 6), show: true);
        doc.SelectNames(["entry0002.v3m"]);
        await doc.Commands.OpenSelectedAsync(); // diagnostic run: prepares the work copy, launches nothing
        string? copy = doc.Work.PathOf("entry0002.v3m");
        ctx.Check(copy is not null && System.IO.File.Exists(copy) && copy.StartsWith(doc.Work.Root, StringComparison.OrdinalIgnoreCase), "Open makes a work copy in the document's work folder");
        byte[] edited = [1, 2, 3, 4, 5, 6, 7];
        await System.IO.File.WriteAllBytesAsync(copy!, edited);
        var sw = Stopwatch.StartNew();
        while (doc.WorkChanges.Count == 0 && sw.ElapsedMilliseconds < 8000) await Task.Delay(100);
        ctx.Check(doc.WorkChanges.Count == 1, $"the change is noticed ({sw.ElapsedMilliseconds} ms)");
        await Task.Delay(50);
        ctx.Check(doc.View is VppDocumentView v && v.IsWorkBarVisible, "the work-copy bar is shown");
        ctx.Check(doc.Commands.UpdateFromWorkCopies() && doc.UndoLabel == "Update entry0002.v3m from its work copy", "Update packfile is one undo step");
        ctx.Check(doc.WorkChanges.Count == 0, "the bar clears");
        string saved = Path.Combine(kit.Folder, "work-saved.vpp");
        doc.SaveTo(saved);
        ctx.Check(Kit.Entry(VppPackage.Open(saved), "entry0002.v3m").AsSpan().SequenceEqual(edited), "the saved packfile contains the new bytes");
        string root = doc.Work.Root;
        ctx.Shell.Close(doc);
        ctx.Check(!Directory.Exists(root), "the work folder is deleted when the packfile closes");
    }

    /// <summary>Review finding 12: nothing is read from a packfile that changed on disk after it was opened.</summary>
    [SelfTest("vpp.changed-on-disk-refuses-reads")]
    public static async Task ChangedOnDiskRefusesReads(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        string path = kit.Packfile("stale.vpp", 6);
        var doc = kit.Open(path, show: true);
        var items = doc.Current.Items.ToList();
        // another program rewrites the packfile: same names, other data at other offsets
        var other = VppEdit.AddSources(VppPackage.Empty, [.. items.Select(i => (i.Name, (VppSource)new MemorySource(Enumerable.Repeat((byte)0xAB, (int)i.Size + 17).ToArray())))], VppClashPolicy.KeepBoth).Package;
        await Task.Delay(1100);
        VppSaver.Save(other, path, null, CancellationToken.None, VppSaveOptions.Default);
        ctx.Check(VppDocument.StaleProblem(doc.Current, items) is { } why && why.Contains("changed on disk"), "the change is detected before reading (preview refuses)");
        string outDir = Path.Combine(kit.Folder, "stale-out");
        var written = await doc.Commands.ExtractAsync(items, outDir, VppOverwritePolicy.Overwrite, quiet: true);
        ctx.Check(written is null && !Directory.Exists(outDir), "extraction refuses and writes nothing");
        doc.SelectNames([items[2].Name]);
        await doc.Commands.OpenSelectedAsync();
        ctx.Check(!doc.HasWorkFolder || doc.Work.PathOf(items[2].Name) is null, "no work copy is made from stale offsets");
        ctx.Check(!doc.ConfirmSave(), "saving is refused (VPP012)");
        doc.ReloadFromDisk();
        written = await doc.Commands.ExtractAsync([doc.Current.Items[0]], outDir, VppOverwritePolicy.Overwrite, quiet: true);
        ctx.Check(written is { Count: 1 } && System.IO.File.ReadAllBytes(written[0]).All(b => b == 0xAB), "after Reload the new data is read");
        ctx.Shell.Close(doc);
    }

    /// <summary>
    /// Review finding 14: after a save the document reads the NEW file; when the saved file cannot be read back (a
    /// scanner holding it), the save counts as done and nothing is read from the old offsets until a reload.
    /// </summary>
    [SelfTest("vpp.save-reopen-fails-needs-reload")]
    public static async Task SaveReopenFailsNeedsReload(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        string path = kit.Packfile("reopen.vpp", 6);
        var doc = kit.Open(path, show: true);
        // a successful Save As: every entry is read from the new file, a fresh directory, the saved point set
        doc.ApplyEdit("Remove", p => VppEdit.Remove(p, [p.Items[0].Name]));
        string copy = Path.Combine(kit.Folder, "reopen-copy.vpp");
        doc.SaveTo(copy);
        ctx.Check(!doc.IsDirty && doc.FilePath == copy && doc.Current.Path == copy, "Save As: clean, the document is the new file");
        ctx.Check(doc.Current.Items.All(i => i.Source is ArchiveSource a && a.ArchivePath == copy && i.State == VppItemState.Original), "every entry is an original entry of the new file");
        ctx.Check(doc.Current.Stamp == VppArchiveStamp.Of(copy), "the directory and stamp were read from the new file");

        // the read-back fails for longer than the retries: the file on disk is new, the document must not read old offsets
        var before = doc.Current.Items.ToList();
        doc.ApplyEdit("Remove", p => VppEdit.Remove(p, [p.Items[0].Name]));
        int calls = 0;
        doc.ReopenForTest = _ => { calls++; throw new IOException("locked by a scanner (simulated)"); };
        try { doc.SaveTo(copy); }
        catch (Exception ex) { ctx.Check(false, "the save is reported as done, not failed: " + ex.Message); }
        finally { doc.ReopenForTest = null; }
        ctx.Check(calls > 1, $"the read-back was retried ({calls} attempts)");
        ctx.Check(VppPackage.Open(copy).Count == before.Count - 1, "the file on disk holds the saved packfile");
        ctx.Check(!doc.IsDirty && doc.FilePath == copy && doc.NeedsReload, "the document is saved, clean and needs a reload");
        ctx.Check(VppDocument.StaleProblem(doc.Current, doc.Current.Items) is { } why && why.Contains("Reload", StringComparison.Ordinal), "previews refuse with a message offering Reload");
        string outDir = Path.Combine(kit.Folder, "reopen-out");
        var written = await doc.Commands.ExtractAsync(doc.Current.Items, outDir, VppOverwritePolicy.Overwrite, quiet: true);
        ctx.Check(written is null && !Directory.Exists(outDir), "extraction refuses and writes nothing");
        ctx.Check(!doc.ConfirmSave(), "saving again is refused until the reload");
        ctx.Check(!doc.ApplyEdit("Remove", p => VppEdit.Remove(p, [p.Items[0].Name])) && doc.NeedsReload, "edits are refused (they would drop the mark)");
        doc.ReloadFromDisk();
        ctx.Check(!doc.NeedsReload && doc.Current.Items.All(i => i.Source is ArchiveSource a && a.ArchivePath == copy), "after Reload the document reads the new file");
        written = await doc.Commands.ExtractAsync(doc.Current.Items, outDir, VppOverwritePolicy.Overwrite, quiet: true);
        ctx.Check(written is { } w && w.Count == doc.Current.Count && w.All(f => System.IO.File.ReadAllBytes(f).AsSpan().SequenceEqual(Kit.Entry(VppPackage.Open(copy), Path.GetFileName(f)))),
            "after Reload every entry extracts with the saved data");
        ctx.Shell.Close(doc);
    }

    /// <summary>Review finding 5: a work copy follows its entry through Rename, Undo and Redo, and is never overwritten.</summary>
    [SelfTest("vpp.work-copy-rename-undo")]
    public static async Task WorkCopyRenameUndo(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("wru.vpp", 6), show: true);
        doc.SelectNames(["entry0004.tbl"]);
        await doc.Commands.OpenSelectedAsync();
        string? copy = doc.Work.PathOf("entry0004.tbl");
        ctx.Check(copy is not null, "work copy made");
        byte[] edited = [9, 8, 7, 6, 5];
        await System.IO.File.WriteAllBytesAsync(copy!, edited);
        ctx.Check(doc.Commands.Rename("entry0004.tbl", "renamed.tbl") is null, "renamed");
        doc.Undo();
        ctx.Check(doc.Current.Contains("entry0004.tbl") && !doc.Current.Contains("renamed.tbl"), "undo restores the old name");
        ctx.Check(doc.Work.PathOf("entry0004.tbl") == copy, $"after undo the copy maps to the old name ({doc.Work.PathOf("entry0004.tbl")})");
        // opening the entry again reuses the copy holding the edits, it never re-extracts over it
        doc.SelectNames(["entry0004.tbl"]);
        await doc.Commands.OpenSelectedAsync();
        ctx.Check(System.IO.File.ReadAllBytes(copy!).AsSpan().SequenceEqual(edited), "opening again keeps the edited copy");
        var sw = Stopwatch.StartNew();
        while (doc.WorkChanges.Count == 0 && sw.ElapsedMilliseconds < 8000) await Task.Delay(100);
        int count = doc.Current.Count;
        ctx.Check(doc.Commands.UpdateFromWorkCopies(), "update after rename + undo");
        ctx.Check(doc.Current.Count == count && Kit.Entry(doc.Current, "entry0004.tbl").AsSpan().SequenceEqual(edited) && !doc.Current.Contains("renamed.tbl"),
            $"... replaced the right entry, nothing added ({string.Join(", ", doc.Current.Items.Select(i => i.Name))})");
        // redo path: rename, undo, redo, change the copy again: the update goes to the renamed entry
        ctx.Check(doc.Commands.Rename("entry0004.tbl", "renamed.tbl") is null, "renamed again");
        doc.Undo();
        doc.Redo();
        ctx.Check(doc.Current.Contains("renamed.tbl") && doc.Work.PathOf("renamed.tbl") == copy, "redo of the rename: the copy maps to the new name");
        byte[] again = [1, 1, 2, 3, 5, 8];
        await Task.Delay(1100); // a new write time
        await System.IO.File.WriteAllBytesAsync(copy!, again);
        sw.Restart();
        while (doc.WorkChanges.Count == 0 && sw.ElapsedMilliseconds < 8000) await Task.Delay(100);
        ctx.Check(doc.WorkChanges.Count == 1 && doc.WorkChanges[0].EntryName == "renamed.tbl", $"after redo the change is shown for the new name ({string.Join(", ", doc.WorkChanges.Select(c => c.EntryName))})");
        ctx.Check(doc.Commands.UpdateFromWorkCopies() && doc.Current.Count == count && Kit.Entry(doc.Current, "renamed.tbl").AsSpan().SequenceEqual(again),
            $"... and the update replaces the renamed entry ({string.Join(", ", doc.Current.Items.Select(i => i.Name))})");
        ctx.Shell.Close(doc);
    }

    [SelfTest("vpp.recovery")]
    public static async Task Recovery(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("recover.vpp", 10));
        await doc.Commands.AddPathsAsync([kit.File("add/extra.wav", 900, 7)]);
        doc.Commands.Rename("entry0001.wav", "renamed.wav");
        doc.ApplyEdit("Add memory", p => VppEdit.AddBytes(p, "small.txt", [65, 66, 67], VppClashPolicy.KeepBoth).Package);
        byte[]? data = doc.CaptureRecovery();
        ctx.Check(data is not null && data.Length < 64 * 1024, $"recovery is a small manifest ({data?.Length:N0} bytes)");
        var restored = kit.Track((VppDocument)kit.Module.Kind.Restore(new RecoverySnapshot(doc.Id, doc.FilePath, doc.DisplayName, DateTime.UtcNow, "vpp", data!)));
        ctx.Check(restored.IsDirty && restored.Serialize().AsSpan().SequenceEqual(doc.Serialize()), "restoring gives the same packfile, dirty");
        ctx.Check(restored.Notice is null, "nothing was lost");
    }

    [SelfTest("vpp.game-packfile")]
    public static void GamePackfile(SelfTestContext ctx)
    {
        if (LocalPaths.GameDirectory is not { } game || !Directory.Exists(game)) { ctx.Skip("no game directory"); return; }
        var source = Directory.EnumerateFiles(game, "*.vpp").Select(p => new FileInfo(p)).Where(f => f.Length is > 4096 and < 40_000_000).OrderBy(f => f.Length).LastOrDefault();
        if (source is null) { ctx.Skip("no packfile under 40 MB in the game directory"); return; }
        using var kit = new Kit(ctx);
        string copy = Path.Combine(kit.Folder, source.Name);
        System.IO.File.Copy(source.FullName, copy);
        var doc = kit.Open(copy, show: true);
        ctx.Check(doc.Current.Count > 0 && doc.List.Visible.Count == doc.Current.Count, $"{source.Name}: {doc.Current.Count} entries listed");
        doc.SaveTo(copy);
        ctx.Check(System.IO.File.ReadAllBytes(copy).AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(source.FullName)), $"{source.Name}: re-saving the copy unchanged is byte-identical");
    }

    [SelfTest("vpp.large-list-performance")]
    public static void LargeListPerformance(SelfTestContext ctx)
    {
        using var kit = new Kit(ctx);
        var doc = kit.Open(kit.Packfile("large.vpp", 2600, 64), show: true);
        SelfTestPump.Pump(300);
        // the Info column fills in the background: measure the filled list (later loads take the cached lines)
        var info = Stopwatch.StartNew();
        while (doc.List.InfoCache is { IsFilling: true } && info.ElapsedMilliseconds < 20_000) SelfTestPump.Pump(50);
        SelfTestPump.Pump(100);
        ctx.Log(doc.List.InfoCache is null ? "no Info column (--vpp-info off)"
            : $"Info column filled in {info.ElapsedMilliseconds} ms after the first 300 ms ({doc.List.AllRows.Count(r => r.Info is not null)} of 2,600 rows)");
        var view = (VppDocumentView)doc.View;
        var listView = view.FileList.List;
        var sw = Stopwatch.StartNew();
        doc.List.Load(doc.Current);
        double load = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        doc.List.FilterText = "*7*.tga";
        listView.UpdateLayout();
        double filter = sw.Elapsed.TotalMilliseconds;
        doc.List.FilterText = "";
        listView.UpdateLayout();
        var scroller = FindScrollViewer(listView);
        sw.Restart();
        int pages = 0;
        for (; pages < 60 && scroller is not null; pages++) { scroller.PageDown(); listView.UpdateLayout(); }
        double scroll = sw.Elapsed.TotalMilliseconds / Math.Max(1, pages);
        int containers = CountItems(listView);
        ctx.Log($"2,600 entries: rows {load:0.0} ms, filter+layout {filter:0.0} ms, scroll {scroll:0.0} ms per page, {containers} row containers realised");
        ctx.Check(load < 150 && filter < 250, "rebuilding and filtering stay fast");
        ctx.Check(scroll < 50, "scrolling a page stays under 50 ms");
        ctx.Check(containers < 200, "rows are virtualised");
        sw.Restart();
        view.FileList.SelectAllShown();
        ctx.Check(doc.SelectedItems.Count == 2600, $"Ctrl+A selects all ({sw.ElapsedMilliseconds} ms)");
    }

    private static ScrollViewer? FindScrollViewer(System.Windows.DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private static int CountItems(System.Windows.DependencyObject root)
    {
        int n = root is ListViewItem ? 1 : 0;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++) n += CountItems(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        return n;
    }
}
