using System.Collections.Specialized;
using System.Windows;
using System.Windows.Interop;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Work;
using Cairn.Vpp.Validation;

namespace Cairn.Vpp.Ui.Commands;

/// <summary>What to do with files that already exist in the extraction folder.</summary>
public enum VppOverwritePolicy { Ask, Overwrite, SkipExisting }

/// <summary>
/// Every packfile command for one document. The menu, toolbar, shortcuts, context menu and self-tests all come
/// here; each edit is one undo step with a readable label, and anything that reads or writes data runs off the UI
/// thread as the document's operation (progress bar with Cancel).
/// </summary>
public sealed class VppDocumentCommands(VppDocument doc)
{
    private string? _lastAddFolder;

    private Cairn.Ui.Services.IDialogService Dialogs => doc.Shell.Dialogs;
    private IReadOnlyList<VppItem> Selected => doc.SelectedItems;
    private IntPtr OwnerHandle => doc.Shell.MainWindow is { } w ? new WindowInteropHelper(w).Handle : IntPtr.Zero;

    public bool CanEdit => !doc.IsBusy;
    public bool HasSelection => Selected.Count > 0 && !doc.IsBusy;
    public bool HasSingleSelection => Selected.Count == 1 && !doc.IsBusy;

    /// <summary>Runs an async command from a menu or key, reporting failures instead of letting them escape.</summary>
    public void Fire(Func<Task> action)
    {
        _ = RunSafe();
        async Task RunSafe()
        {
            try { await action(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Dialogs.ShowError("The packfile command failed", ex.Message, ex.ToString());
            }
        }
    }

    // ---- adding ----------------------------------------------------------------------------------------------

    public async Task AddFilesAsync()
    {
        var files = Dialogs.OpenFiles(_lastAddFolder, "Add files to the packfile", "All files (*.*)|*.*", multiselect: true);
        if (files.Length == 0) return;
        _lastAddFolder = Path.GetDirectoryName(files[0]);
        await AddPathsAsync(files);
    }

    public async Task AddFolderAsync()
    {
        var folder = Dialogs.PickFolder(_lastAddFolder, "Add a folder's files to the packfile (sub-folders included, flattened)");
        if (folder is null) return;
        _lastAddFolder = folder;
        await AddPathsAsync([folder]);
    }

    /// <summary>
    /// Adds files and folders (folders recursively, flattened to file names) as ONE undo step. Names already in the
    /// packfile are resolved by <paramref name="policy"/>, or by asking once per batch (Replace / Keep both / Skip,
    /// for all or one by one). Returns the report, or null when cancelled or nothing was added.
    /// </summary>
    public async Task<VppAddReport?> AddPathsAsync(IReadOnlyList<string> paths, VppClashPolicy? policy = null)
    {
        if (paths.Count == 0 || doc.IsBusy) return null;
        var failures = new List<(string, string)>();
        List<(string Name, VppSource Source)> entries = [];
        await doc.RunOperationAsync("Reading files to add", async op =>
            entries = await Task.Run(() => Gather(paths, failures, op), op.Token));
        if (entries.Count == 0)
        {
            if (failures.Count > 0) Dialogs.ShowError("Nothing was added", string.Join("\n", failures.Take(10).Select(f => $"{f.Item1}: {f.Item2}")));
            return null;
        }
        var current = doc.Current;
        var clashes = entries.Select(e => e.Name).Where(current.Contains).Distinct(VppNames.Comparer).ToList();
        var decisions = new Dictionary<string, VppClashPolicy>(VppNames.Comparer);
        if (clashes.Count > 0)
        {
            if (policy is { } all) foreach (var c in clashes) decisions[c] = all;
            else if (!AskClashes(clashes, decisions)) return null;
        }

        // Clashing names in the user's chosen way; the rest (only clashing among themselves) keep both.
        var groups = new List<(VppClashPolicy Policy, List<(string, VppSource)> Items)>();
        foreach (var e in entries)
        {
            var p = decisions.TryGetValue(e.Name, out var d) ? d : VppClashPolicy.KeepBoth;
            if (groups.Count == 0 || groups[^1].Policy != p) groups.Add((p, []));
            groups[^1].Items.Add(e);
        }
        VppAddReport total = VppAddReport.Empty;
        string label = entries.Count == 1 ? $"Add {entries[0].Name}" : $"Add {entries.Count:N0} files";
        doc.ApplyEdit(label, package =>
        {
            total = VppAddReport.Empty;
            foreach (var (p, items) in groups)
            {
                var result = VppEdit.AddSources(package, items, p);
                package = result.Package;
                total = Merge(total, result.Report);
            }
            return package;
        });
        var report = total with { Failed = [.. total.Failed, .. failures] };
        doc.SelectNames([.. report.Added, .. report.Replaced, .. report.Renamed.Select(r => r.Used)]);
        doc.ShowStatus(Describe(report));
        if (!report.Failed.IsEmpty)
            Dialogs.ShowError(report.Failed.Length == 1 ? "One file was not added" : $"{report.Failed.Length} files were not added",
                string.Join("\n", report.Failed.Take(12).Select(f => $"{Path.GetFileName(f.Input)}: {f.Reason}")));
        return report.ChangedNothing && report.Renamed.IsEmpty ? null : report;
    }

    private static VppAddReport Merge(VppAddReport a, VppAddReport b) => new(
        [.. a.Added, .. b.Added], [.. a.Replaced, .. b.Replaced], [.. a.Renamed, .. b.Renamed], [.. a.Skipped, .. b.Skipped], [.. a.Failed, .. b.Failed]);

    private static string Describe(VppAddReport r)
    {
        var parts = new List<string>();
        if (r.Added.Length > 0) parts.Add($"{r.Added.Length:N0} added");
        if (r.Renamed.Length > 0) parts.Add($"{r.Renamed.Length:N0} kept under a new name");
        if (r.Replaced.Length > 0) parts.Add($"{r.Replaced.Length:N0} replaced");
        if (r.Skipped.Length > 0) parts.Add($"{r.Skipped.Length:N0} skipped");
        if (r.Failed.Length > 0) parts.Add($"{r.Failed.Length:N0} failed");
        return parts.Count == 0 ? "Nothing was added" : string.Join(", ", parts);
    }

    private static List<(string, VppSource)> Gather(IReadOnlyList<string> paths, List<(string, string)> failures, VppOperation op)
    {
        var result = new List<(string, VppSource)>();
        void AddFile(string file)
        {
            try { result.Add((Path.GetFileName(file), FileSource.FromFile(file))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add((file, ex.Message)); }
        }
        foreach (var path in paths)
        {
            op.Token.ThrowIfCancellationRequested();
            if (Directory.Exists(path))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
                    {
                        op.Token.ThrowIfCancellationRequested();
                        AddFile(file);
                        if (result.Count % 200 == 0) op.Report(null, $"{result.Count:N0} files found");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failures.Add((path, ex.Message)); }
            }
            else if (File.Exists(path)) AddFile(path);
            else failures.Add((path, "The file no longer exists."));
        }
        return result;
    }

    /// <summary>Asks how to resolve <paramref name="clashes"/>; false = cancelled.</summary>
    private bool AskClashes(IReadOnlyList<string> clashes, Dictionary<string, VppClashPolicy> decisions)
    {
        const string body = "Replace puts the new file's data under the existing name. Keep both adds the new file under a free name such as \"name (2).tga\". Skip leaves the packfile's entry as it is.";
        if (clashes.Count == 1)
        {
            int one = Dialogs.Choose($"\"{clashes[0]}\" is already in the packfile", body, ["_Replace", "_Keep both", "_Skip", "Cancel"], 3);
            if (one is < 0 or > 2) return false;
            decisions[clashes[0]] = (VppClashPolicy)one;
            return true;
        }
        string list = string.Join("\n", clashes.Take(8).Select(c => "• " + c)) + (clashes.Count > 8 ? $"\n... and {clashes.Count - 8} more" : string.Empty);
        int pick = Dialogs.Choose($"{clashes.Count} names are already in the packfile", list + "\n\n" + body,
            ["_Replace all", "_Keep both for all", "_Skip all", "_Decide for each...", "Cancel"], 4);
        if (pick is >= 0 and <= 2) { foreach (var c in clashes) decisions[c] = (VppClashPolicy)pick; return true; }
        if (pick != 3) return false;
        foreach (var c in clashes)
        {
            int each = Dialogs.Choose($"\"{c}\" is already in the packfile", body, ["_Replace", "_Keep both", "_Skip", "Cancel"], 3);
            if (each is < 0 or > 2) return false;
            decisions[c] = (VppClashPolicy)each;
        }
        return true;
    }

    /// <summary>
    /// Asks how to resolve extraction collisions (one answer per clash, in order, into <paramref name="decisions"/>):
    /// several clashes get one prompt with answers for the whole batch, or "Decide for each". False = cancelled.
    /// </summary>
    private bool AskExtractClashes(IReadOnlyList<(string File, bool InBatch)> clashes, string folder, List<VppClashPolicy> decisions)
    {
        const string body = "Overwrite replaces the file with this entry. Keep both writes this entry under a free name such as \"name (2).tga\". Skip does not extract this entry.";
        static string Heading((string File, bool InBatch) c, string folder) => c.InBatch
            ? $"Another selected entry is also extracted as \"{c.File}\""
            : $"\"{c.File}\" already exists in {folder}";
        if (clashes.Count > 1)
        {
            string list = string.Join("\n", clashes.Take(8).Select(c => "• " + c.File + (c.InBatch ? " (two entries, one file name)" : " (already on disk)")))
                + (clashes.Count > 8 ? $"\n... and {clashes.Count - 8} more" : string.Empty);
            int pick = Dialogs.Choose($"{clashes.Count} files would get a name that is already taken", list + "\n\n" + body,
                ["_Overwrite all", "_Keep both for all", "_Skip all", "_Decide for each...", "Cancel"], 4);
            if (pick is >= 0 and <= 2) { decisions.AddRange(clashes.Select(_ => (VppClashPolicy)pick)); return true; }
            if (pick != 3) return false;
        }
        foreach (var c in clashes)
        {
            int each = Dialogs.Choose(Heading(c, folder), body, ["_Overwrite", "_Keep both", "_Skip", "Cancel"], 3);
            if (each is < 0 or > 2) return false;
            decisions.Add((VppClashPolicy)each);
        }
        return true;
    }

    // ---- editing ---------------------------------------------------------------------------------------------

    public void RemoveSelected()
    {
        var names = Selected.Select(s => s.Name).ToList();
        if (names.Count == 0 || doc.IsBusy) return;
        if (doc.Module.Settings.ConfirmRemove &&
            !Dialogs.Confirm(names.Count == 1 ? $"Remove {names[0]}?" : $"Remove {names.Count} entries?", "You can undo this with Ctrl+Z until the packfile is saved.", "Remove"))
            return;
        int index = doc.Current.IndexOf(names[0]);
        if (!doc.ApplyEdit(names.Count == 1 ? $"Remove {names[0]}" : $"Remove {names.Count:N0} entries", p => VppEdit.Remove(p, names))) return;
        // select the entry that took the first removed one's place, so Del can be pressed again
        var items = doc.Current.Items;
        if (items.Length > 0) doc.SelectNames([items[Math.Clamp(index, 0, items.Length - 1)].Name]);
        doc.ShowStatus(names.Count == 1 ? $"Removed {names[0]}" : $"Removed {names.Count:N0} entries");
    }

    /// <summary>Starts the inline rename of the selected entry (F2); without a view, nothing happens.</summary>
    public void BeginRename()
    {
        if (HasSingleSelection) doc.BeginRenameHandler?.Invoke(Selected[0]);
    }

    /// <summary>Renames one entry as an undo step. Returns null when done, else why not (also shown in the status bar).</summary>
    public string? Rename(string oldName, string newName)
    {
        newName = newName.Trim();
        if (doc.IsBusy) return "Wait for the current operation to finish.";
        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return null;
        // checked here, before the core's normalising (which would quietly keep only the part after a '/')
        if (VppNames.Validate(newName) is { } invalid) { doc.ShowStatus("Not renamed: " + invalid); return invalid; }
        try
        {
            if (!doc.ApplyEdit($"Rename {oldName} to {newName}", p => VppEdit.Rename(p, oldName, newName))) return null;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            doc.ShowStatus("Not renamed: " + ex.Message);
            return ex.Message;
        }
        if (doc.HasWorkFolder) doc.Work.NoteRenamed(oldName, newName);
        doc.SelectNames([newName]);
        return null;
    }

    /// <summary>True when an entry of the selection has a name the game cuts off (VPP027).</summary>
    public bool CanRenameToFit => HasSelection && Selected.Any(i => VppValidator.IsNameTooLongForGame(i.Name));

    /// <summary>
    /// "Rename to fit...": gives every selected entry (or <paramref name="names"/>) whose name is too long for the game
    /// a name of at most 31 characters, as ONE undo step. Asks first (showing the new names and that tables, meshes and
    /// levels naming the old ones need the same change) unless <paramref name="confirm"/> is false. Returns the renames done.
    /// </summary>
    public IReadOnlyList<(string OldName, string NewName)> RenameToFit(IReadOnlyList<string>? names = null, bool confirm = true)
    {
        if (doc.IsBusy) return [];
        var targets = (names ?? Selected.Select(i => i.Name).ToList()).Where(VppValidator.IsNameTooLongForGame).ToList();
        if (targets.Count == 0)
        {
            doc.ShowStatus($"No selected entry has a name longer than {VppValidator.MaxAssetNameLength} characters.");
            return [];
        }
        var package = doc.Current;
        var taken = new HashSet<string>(package.Items.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var renames = new List<(string OldName, string NewName)>();
        foreach (var old in targets)
        {
            taken.Remove(old);
            string proposed = VppEdit.ShortName(old, taken.Contains);
            taken.Add(proposed);
            renames.Add((old, proposed));
        }
        if (confirm)
        {
            const int shown = 12;
            string list = string.Join("\n", renames.Take(shown).Select(r => $"{r.OldName}  →  {r.NewName}"))
                + (renames.Count > shown ? $"\n... and {renames.Count - shown:N0} more" : string.Empty);
            string body = $"The game keeps texture, sound and font names in {VppValidator.MaxAssetNameLength} characters and does not find longer ones.\n\n{list}\n\n"
                + "Tables, meshes, levels and effects that name the old file names need the same change, or they will still ask for the old names. "
                + "You can undo the rename with Ctrl+Z, or press F2 afterwards to pick another name.";
            if (!Dialogs.Confirm(renames.Count == 1 ? "Rename to fit?" : $"Rename {renames.Count:N0} entries to fit?", body, "Rename")) return [];
        }
        try
        {
            string label = renames.Count == 1 ? $"Rename {renames[0].OldName} to {renames[0].NewName}" : $"Rename {renames.Count:N0} entries to fit {VppValidator.MaxAssetNameLength} characters";
            if (!doc.ApplyEdit(label, p => VppEdit.RenameMany(p, renames))) return [];
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException)
        {
            doc.ShowStatus("Not renamed: " + ex.Message);
            return [];
        }
        if (doc.HasWorkFolder) foreach (var (o, n) in renames) doc.Work.NoteRenamed(o, n);
        doc.SelectNames([.. renames.Select(r => r.NewName)]);
        doc.ShowStatus(renames.Count == 1 ? $"Renamed to {renames[0].NewName}" : $"Renamed {renames.Count:N0} entries to fit");
        return renames;
    }

    public void ReplaceSelected()
    {
        if (!HasSingleSelection) return;
        var name = Selected[0].Name;
        var ext = Path.GetExtension(name);
        var files = Dialogs.OpenFiles(_lastAddFolder, $"Replace {name}", (ext.Length > 0 ? $"{ext} files (*{ext})|*{ext}|" : string.Empty) + "All files (*.*)|*.*", multiselect: false);
        if (files.Length == 1) ReplaceWith(name, files[0]);
    }

    /// <summary>Replaces <paramref name="name"/>'s data with <paramref name="file"/> as one undo step.</summary>
    public bool ReplaceWith(string name, string file)
    {
        try
        {
            var source = FileSource.FromFile(file);
            _lastAddFolder = Path.GetDirectoryName(file);
            return doc.ApplyEdit($"Replace {name}", p => VppEdit.Replace(p, name, source));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException)
        {
            Dialogs.ShowError($"{name} was not replaced", ex.Message);
            return false;
        }
    }

    public void SortPackfile(VppSortKey key, bool descending = false)
    {
        string what = key switch { VppSortKey.Name => "name", VppSortKey.Type => "type", VppSortKey.Size => "size", _ => "original order" };
        if (!doc.ApplyEdit($"Sort entries by {what}", p => VppEdit.Sort(p, key, descending))) doc.ShowStatus($"The entries are already in {what} order");
    }

    /// <summary>Selects every entry with the selected entry's extension.</summary>
    public void SelectAllOfType()
    {
        if (Selected.Count == 0) return;
        var exts = Selected.Select(s => s.Extension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        doc.SelectNames([.. doc.List.Visible.Where(r => exts.Contains(r.Extension)).Select(r => r.Name)]);
    }

    public void CopyNames()
    {
        if (Selected.Count == 0) return;
        if (Cairn.Ui.Services.SystemClipboard.TrySetText(string.Join(Environment.NewLine, Selected.Select(s => s.Name))))
            doc.ShowStatus(Selected.Count == 1 ? $"Copied the name {Selected[0].Name}" : $"Copied {Selected.Count} names");
    }

    /// <summary>Validates now (including whether added files changed on disk) and shows the result.</summary>
    public IReadOnlyList<VppProblem> Validate()
    {
        var problems = Writing.VppSaver.CheckBeforeSave(doc.Current).ToList();
        if (doc.FilePath is { } path) problems.AddRange(VppValidator.ValidateTargetPath(path));
        if (!doc.Shell.IsDiagnosticRun) Cairn.Vpp.Ui.Dialogs.VppProblemsWindow.Show(doc, problems);
        doc.ShowStatus(problems.Count == 0 ? "No problems found" : $"{problems.Count} problem(s) found");
        return problems;
    }

    // ---- extracting ------------------------------------------------------------------------------------------

    public async Task ExtractSelectedToAsync() { if (HasSelection) await ExtractToAsync(Selected); }
    public async Task ExtractAllToAsync() { if (doc.Current.Count > 0 && !doc.IsBusy) await ExtractToAsync(doc.Current.Items); }

    private async Task ExtractToAsync(IReadOnlyList<VppItem> items)
    {
        var folder = Dialogs.PickFolder(doc.Folder, items.Count == 1 ? $"Extract {items[0].Name} to" : $"Extract {items.Count:N0} entries to");
        if (folder is not null) await ExtractAsync(items, folder);
    }

    /// <summary>"Extract here": next to the packfile.</summary>
    public async Task ExtractHereAsync()
    {
        if (HasSelection && doc.Folder is { } folder) await ExtractAsync(Selected, folder);
    }

    /// <summary>
    /// Writes <paramref name="items"/> into <paramref name="folder"/> under their own names (characters Windows refuses
    /// become "_"). Name collisions (a file already there, two entries with one file name): asked unless
    /// <paramref name="overwrite"/> says; unattended runs keep both. Returns the
    /// written paths, or null when cancelled.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ExtractAsync(IReadOnlyList<VppItem> items, string folder, VppOverwritePolicy overwrite = VppOverwritePolicy.Ask, bool quiet = false)
    {
        if (items.Count == 0 || doc.IsBusy) return null;
        if (!doc.EnsureReadable(items)) return null; // the packfile changed on disk: its offsets would read other data
        // Review finding 9: a name collides when a file is already at the destination, or when two entries map to one
        // Windows file name (non-ASCII case twins, characters made "_", trailing dots). Each collision is resolved by
        // Overwrite, Keep both ("name (2).ext") or Skip: asked (with answers for the whole batch) when overwrite is Ask.
        // An explicit policy decides existing files; two entries of the batch then always keep both.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedOnDisk = new List<string>();
        var targets = new List<(VppItem Item, string Path)>();
        var clashes = new List<(VppItem Item, string File, bool InBatch)>();
        foreach (var i in items)
        {
            string file = VppWorkFolder.SafeFileName(i.Name);
            bool inBatch = taken.Contains(file);
            if (inBatch || File.Exists(Path.Combine(folder, file))) { clashes.Add((i, file, inBatch)); continue; }
            taken.Add(file);
            targets.Add((i, Path.Combine(folder, file)));
            if (!string.Equals(file, i.Name, StringComparison.Ordinal)) renamedOnDisk.Add($"{i.Name} -> {file}");
        }
        var decisions = new List<VppClashPolicy>();
        if (clashes.Count > 0 && overwrite == VppOverwritePolicy.Ask)
        {
            if (Dialogs is Cairn.Ui.Services.DialogService { CollectErrors: not null, NonInteractiveChoice: null }) decisions.AddRange(clashes.Select(_ => VppClashPolicy.KeepBoth)); // unattended: the safe answer
            else if (!AskExtractClashes(clashes.Select(c => (c.File, c.InBatch)).ToList(), folder, decisions)) return null;
        }
        else
        {
            var forExisting = overwrite switch { VppOverwritePolicy.Overwrite => VppClashPolicy.Replace, VppOverwritePolicy.SkipExisting => VppClashPolicy.Skip, _ => VppClashPolicy.KeepBoth };
            decisions.AddRange(clashes.Select(c => c.InBatch ? VppClashPolicy.KeepBoth : forExisting));
        }
        int overwritten = 0, skipped = 0;
        for (int k = 0; k < clashes.Count; k++)
        {
            var (item, file, _) = clashes[k];
            string path = Path.Combine(folder, file);
            switch (decisions[k])
            {
                case VppClashPolicy.Skip:
                    skipped++;
                    break;
                case VppClashPolicy.Replace: // the later entry wins over an earlier one of the batch and over the file on disk
                    targets.RemoveAll(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
                    targets.Add((item, path));
                    taken.Add(file);
                    overwritten++;
                    break;
                default:
                    string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file), free;
                    int n = 2;
                    do free = $"{stem} ({n++}){ext}"; while (taken.Contains(free) || File.Exists(Path.Combine(folder, free)));
                    taken.Add(free);
                    targets.Add((item, Path.Combine(folder, free)));
                    renamedOnDisk.Add($"{item.Name} -> {free}");
                    break;
            }
        }
        var written = new List<string>();
        long total = Math.Max(1, targets.Sum(t => t.Item.Size));
        bool done = await doc.RunOperationAsync(items.Count == 1 ? $"Extracting {items[0].Name}" : $"Extracting {items.Count:N0} entries", op => Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            long bytes = 0;
            var buffer = new byte[1 << 20];
            foreach (var (item, path) in targets)
            {
                op.Token.ThrowIfCancellationRequested();
                string temp = path + ".cairn-part";
                try
                {
                    using (var input = item.Source.Open())
                    using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            op.Token.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, n);
                            bytes += n;
                            op.Report((double)bytes / total, item.Name);
                        }
                        // A truncated (or changed) packfile gives a short stream: never write a short file silently.
                        if (output.Length != item.Size)
                            throw new IOException($"'{item.Name}' ends after {output.Length:N0} of {item.Size:N0} bytes (the packfile is truncated or changed on disk); it was not extracted. {written.Count:N0} entr{(written.Count == 1 ? "y was" : "ies were")} extracted before it.");
                    }
                    File.Move(temp, path, overwrite: true);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
                written.Add(path);
            }
            op.Report(1, "done");
        }, op.Token));
        if (!done) { doc.ShowStatus($"Extraction cancelled ({written.Count:N0} of {targets.Count:N0} written)"); return null; }
        string renamedNote = renamedOnDisk.Count == 0 ? string.Empty
            : $" ({renamedOnDisk.Count:N0} saved under another name: {string.Join(", ", renamedOnDisk.Take(3))}{(renamedOnDisk.Count > 3 ? ", ..." : "")})";
        if (overwritten > 0) renamedNote += $"; {overwritten:N0} overwritten";
        if (skipped > 0) renamedNote += $"; {skipped:N0} skipped";
        if (!quiet || renamedNote.Length > 0) doc.ShowStatus((written.Count == 1 ? $"Extracted {Path.GetFileName(written[0])} to {folder}" : $"Extracted {written.Count:N0} files to {folder}") + renamedNote);
        return written;
    }

    /// <summary>Extracts the selection into a fresh staging folder (for the clipboard and drag-out); null when cancelled.</summary>
    public Task<IReadOnlyList<string>?> StageSelectionAsync() =>
        Selected.Count == 0 ? Task.FromResult<IReadOnlyList<string>?>(null) : ExtractAsync(Selected, doc.Module.NewStagingFolder(), VppOverwritePolicy.Overwrite, quiet: true);

    /// <summary>Ctrl+C: the selected entries as files Explorer can paste.</summary>
    public async Task CopyFilesAsync()
    {
        var files = await StageSelectionAsync();
        if (files is null || files.Count == 0) return;
        var list = new StringCollection();
        list.AddRange([.. files]);
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetText(string.Join(Environment.NewLine, Selected.Select(s => s.Name)));
        for (int attempt = 0; ; attempt++)
        {
            try { Clipboard.SetDataObject(data, copy: true); break; }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 4) { await Task.Delay(50); }
            catch (System.Runtime.InteropServices.COMException ex) { doc.ShowStatus("The clipboard is busy: " + ex.Message); return; }
        }
        doc.ShowStatus(files.Count == 1 ? $"Copied {Path.GetFileName(files[0])}: paste it in Explorer" : $"Copied {files.Count:N0} files: paste them in Explorer");
    }

    // ---- opening entries -------------------------------------------------------------------------------------

    /// <summary>Opens the selected entries (at most 10) in their Windows programs, from work copies.</summary>
    public async Task OpenSelectedAsync()
    {
        var items = Selected.Take(10).ToList();
        if (!doc.EnsureReadable(items)) return;
        foreach (var item in items)
        {
            var path = await doc.Work.PrepareAsync(item);
            if (doc.Shell.IsDiagnosticRun) { doc.ShowStatus($"Work copy ready: {path}"); continue; }
            if (VppShellLaunch.Open(path, OwnerHandle) is { } error) Dialogs.ShowError($"Could not open {item.Name}", error);
        }
    }

    public async Task OpenWithAsync()
    {
        if (!HasSingleSelection || !doc.EnsureReadable(Selected)) return;
        var path = await doc.Work.PrepareAsync(Selected[0]);
        if (doc.Shell.IsDiagnosticRun) { doc.ShowStatus($"Work copy ready: {path}"); return; }
        if (VppShellLaunch.OpenWith(path, OwnerHandle) is { } error) Dialogs.ShowError($"Could not open {Selected[0].Name}", error);
    }

    /// <summary>True when a Cairn module opens the selected entry's type.</summary>
    public bool CanOpenInCairn => HasSingleSelection && doc.Module.CairnOpens(Selected[0].Name);

    /// <summary>Opens the selected entry's work copy in a Cairn tab; saving that tab offers to update the packfile.</summary>
    public Task OpenInCairnAsync() => CanOpenInCairn ? OpenInCairnAsync(Selected[0]) : Task.CompletedTask;

    /// <summary>Opens <paramref name="item"/>'s work copy in a Cairn tab (also how the Recent list reopens an entry).</summary>
    public async Task OpenInCairnAsync(VppItem item)
    {
        if (doc.IsBusy || !doc.Module.CairnOpens(item.Name) || !doc.EnsureReadable([item])) return;
        var path = await doc.Work.PrepareAsync(item);
        doc.Shell.OpenFile(path);
    }

    /// <summary>"Update packfile" on the work-copy bar: the changed copies replace their entries (one undo step).</summary>
    public bool UpdateFromWorkCopies(IReadOnlyList<VppWorkChange>? only = null)
    {
        if (doc.IsBusy || !doc.HasWorkFolder) return false;
        IReadOnlyList<(string EntryName, string SnapshotPath, string CopyPath)> taken;
        doc.Work.Follow(doc.Current);
        try { taken = doc.Work.TakeChanges(only); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            doc.ShowStatus("A work copy is still being written; try again in a moment. " + ex.Message);
            return false;
        }
        if (taken.Count == 0) return false;
        string label = taken.Count == 1 ? $"Update {taken[0].EntryName} from its work copy" : $"Update {taken.Count} entries from work copies";
        // each copy remembers the source it becomes, so it keeps finding its entry (also after undo/redo of this step)
        var sources = taken.Select(t => (t.EntryName, Source: (VppSource)FileSource.FromFile(t.SnapshotPath))).ToList();
        for (int i = 0; i < taken.Count; i++) doc.Work.NoteTaken(taken[i].CopyPath, sources[i].Source);
        bool changed = doc.ApplyEdit(label, p => VppEdit.AddSources(p, sources, VppClashPolicy.Replace).Package);
        if (changed) doc.ShowStatus(label + " (save the packfile to keep it)");
        return changed;
    }

    public void IgnoreWorkChanges() { if (doc.HasWorkFolder) doc.Work.IgnoreChanges(); }
}
