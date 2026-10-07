using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Cairn.Ui.Documents;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Commands;
using Cairn.Vpp.Ui.List;
using Cairn.Vpp.Ui.Work;
using Cairn.Vpp.Validation;
using Cairn.Vpp.Writing;

namespace Cairn.Vpp.Ui.Documents;

/// <summary>
/// An open packfile. The snapshot is an immutable <see cref="VppPackage"/> (directory plus where each entry's data
/// comes from), so every edit is one undo step and nothing is written until Save. Saving streams to a temporary file
/// beside the target on a worker thread (<see cref="VppSaver"/>) while the UI keeps running, then swaps it in.
/// </summary>
public sealed class VppDocument : SnapshotDocument<VppPackage>
{
    private readonly FileChangeWatcher _diskWatcher;
    private VppArchiveStamp? _diskStamp;
    private bool _ownWrite;
    private IReadOnlyList<VppItem> _selected = [];
    private IReadOnlyList<VppProblem> _problems = [];
    private IReadOnlyList<StatusItem> _statusItems = [];
    private VppOperation? _operation;
    private string? _notice;
    private VppWorkFolder? _work;

    public VppDocument(VppModule module, VppPackage package, string displayName, string? path, string? originText = null)
        : base(module.Host, module.Kind, package, displayName, path, originText)
    {
        Module = module;
        // The base class re-reads the whole file on every change to compare bytes: far too much for a packfile of up
        // to 1.5 GB. Its watcher stays suspended; this one compares length and time stamp instead.
        SuspendFileWatch(true);
        _diskWatcher = new FileChangeWatcher(module.Host.Dispatcher, 500);
        _diskWatcher.Changed += (_, _) => CheckDisk();
        _diskStamp = package.Stamp ?? StampOf(path);
        WatchDisk();
        List = new VppFileList { InfoCache = module.InfoColumnEnabled ? new VppInfoCache(module.Host.Dispatcher) : null };
        Commands = new VppDocumentCommands(this);
        ReloadFromDiskCommand = new RelayCommand(ReloadFromDisk, () => FilePath is not null && File.Exists(FilePath) && !IsBusy);
        DismissNoticeCommand = new RelayCommand(() => Notice = null);
        ShowProblemsCommand = new RelayCommand(() => Cairn.Vpp.Ui.Dialogs.VppProblemsWindow.Show(this));
        Refresh();
    }

    public VppModule Module { get; }
    /// <summary>The file list's view model (rows, filter, sort).</summary>
    public VppFileList List { get; }
    /// <summary>Every packfile command (add, extract, rename, ...), also reached from the menu and shortcuts.</summary>
    public VppDocumentCommands Commands { get; }
    public RelayCommand ReloadFromDiskCommand { get; }
    public RelayCommand DismissNoticeCommand { get; }
    public RelayCommand ShowProblemsCommand { get; }

    /// <summary>The selected entries (list order). The preview and details panes follow this.</summary>
    public IReadOnlyList<VppItem> SelectedItems => _selected;
    /// <summary>Raised on the UI thread when <see cref="SelectedItems"/> changes.</summary>
    public event EventHandler? SelectionChanged;
    /// <summary>Set by the list view: selects rows by entry name (and scrolls the first into view).</summary>
    internal Action<IReadOnlyCollection<string>>? SelectNamesHandler { get; set; }
    /// <summary>Set by the list view: starts the inline rename of an entry (F2).</summary>
    internal Action<VppItem>? BeginRenameHandler { get; set; }

    /// <summary>Validation of the current snapshot (and of the file name when it has one).</summary>
    public IReadOnlyList<VppProblem> Problems { get => _problems; private set => Set(ref _problems, value); }
    /// <summary>Entries added, replaced, renamed or removed since the last save or load.</summary>
    public int PendingChanges { get; private set; }
    /// <summary>The running save/extract/add, or null.</summary>
    public VppOperation? Operation { get => _operation; private set { if (Set(ref _operation, value)) { Raise(nameof(IsBusy)); Shell.RefreshCommands(); } } }
    public bool IsBusy => _operation is not null;
    /// <summary>A dismissible message shown above the list (lost recovery entries, a name the game will not load, ...).</summary>
    public string? Notice { get => _notice; set => Set(ref _notice, value); }
    /// <summary>The work copies (created on first use).</summary>
    public VppWorkFolder Work
    {
        get
        {
            if (_work is null)
            {
                _work = new VppWorkFolder(Path.Combine(Module.Settings.WorkRoot, Id), Shell.Dispatcher);
                _work.ChangesChanged += (_, _) => Raise(nameof(WorkChanges));
            }
            return _work;
        }
    }
    /// <summary>Changed work copies waiting for "Update packfile" or "Ignore".</summary>
    public IReadOnlyList<VppWorkChange> WorkChanges => _work?.Changes ?? [];
    internal bool HasWorkFolder => _work is not null;

    public override IReadOnlyList<StatusItem> StatusItems => _statusItems;

    public void SetSelection(IReadOnlyList<VppItem> items)
    {
        if (items.SequenceEqual(_selected)) return;
        _selected = items;
        Raise(nameof(SelectedItems));
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Shell.RefreshCommands();
    }

    /// <summary>Raises <see cref="SelectionChanged"/> again for the current selection (a view attached late).</summary>
    internal void NotifySelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Selects the entries named <paramref name="names"/> in the list (or directly when no view exists yet).</summary>
    public void SelectNames(IEnumerable<string> names)
    {
        var set = names.ToList();
        if (SelectNamesHandler is { } handler) { handler(set); return; }
        var lookup = set.ToHashSet(StringComparer.OrdinalIgnoreCase);
        SetSelection([.. Current.Items.Where(i => lookup.Contains(i.Name))]);
    }

    /// <summary>One undoable edit, refused while a save or extraction runs.</summary>
    public bool ApplyEdit(string label, Func<VppPackage, VppPackage> edit)
    {
        if (IsBusy) return false;
        // an edit would make a new snapshot that no longer carries the "reload first" mark (review finding 14)
        if (NeedsReload) { ShowStatus("Reload the packfile before changing it: the saved file could not be read back."); return false; }
        return Apply(label, edit);
    }

    protected override void OnSnapshotChanged() => Refresh();

    private void Refresh()
    {
        var p = Current;
        _work?.Follow(p); // work copies follow their entries through rename, undo and redo
        var problems = VppValidator.Validate(p).ToList();
        if (FilePath is { } path) problems.AddRange(VppValidator.ValidateTargetPath(path));
        List.Load(p, problems); // rows show their own warnings and errors
        // keep the selection on the same names (snapshots replace the item records)
        if (_selected.Count > 0)
        {
            var names = _selected.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var again = p.Items.Where(i => names.Contains(i.Name)).ToList();
            _selected = [];
            SetSelection(again);
        }
        int changed = 0, kept = 0;
        foreach (var item in p.Items)
        {
            if (item.State != VppItemState.Original || item.IsRenamed) changed++;
            if (item.OriginalIndex >= 0) kept++;
        }
        PendingChanges = changed + Math.Max(0, p.OriginalCount - kept);
        Problems = problems;
        Raise(nameof(PendingChanges));
        RebuildStatus();
    }

    private void RebuildStatus()
    {
        var p = Current;
        var items = new List<StatusItem>();
        if (_operation is { } op) items.Add(new StatusItem(op.StatusText + "  (cancel)", "Click to cancel " + op.Title.ToLowerInvariant(), op.CancelCommand));
        items.Add(new StatusItem(p.Count == 1 ? "1 entry" : $"{p.Count:N0} entries", "Entries in the packfile"));
        items.Add(new StatusItem(VppEntryRow.FormatSize(p.ArchiveBytes), $"The packfile will be {p.ArchiveBytes:N0} bytes when saved ({p.DataBytes:N0} bytes of file data)"));
        if (PendingChanges > 0) items.Add(new StatusItem(PendingChanges == 1 ? "1 pending change" : $"{PendingChanges:N0} pending changes", "Added, replaced, renamed or removed entries not saved yet"));
        int errors = _problems.Count(x => x.Severity == VppSeverity.Error), warnings = _problems.Count(x => x.Severity == VppSeverity.Warning);
        if (errors + warnings > 0)
        {
            string text = errors > 0 ? (errors == 1 ? "1 error" : $"{errors} errors") + (warnings > 0 ? $", {warnings} warning" + (warnings == 1 ? "" : "s") : "")
                : warnings == 1 ? "1 warning" : $"{warnings} warnings";
            items.Add(new StatusItem(text, "Click to see the problems", ShowProblemsCommand));
        }
        _statusItems = items;
        Raise(nameof(StatusItems));
    }

    // ---- long operations -------------------------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="body"/> as the document's operation (progress bar, Cancel). Throws
    /// <see cref="InvalidOperationException"/> when another one runs.
    /// </summary>
    public async Task<bool> RunOperationAsync(string title, Func<VppOperation, Task> body)
    {
        if (IsBusy) throw new InvalidOperationException("Another operation is running on this packfile.");
        var op = new VppOperation(title, Shell.Dispatcher);
        op.Progressed += (_, _) => RebuildStatus();
        Operation = op;
        RebuildStatus();
        using var busy = BusyTracker.Begin(title);
        try { await body(op); return !op.IsCancelled; }
        catch (OperationCanceledException) { return false; }
        finally { Operation = null; RebuildStatus(); }
    }

    /// <summary>Waits for <paramref name="task"/> while the UI keeps running (a nested message loop, no modal window).</summary>
    public static void WaitPumping(Task task, Dispatcher dispatcher)
    {
        if (task.IsCompleted) return;
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => dispatcher.BeginInvoke(DispatcherPriority.Send, () => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
    }

    // ---- saving ----------------------------------------------------------------------------------------------

    /// <summary>Snapshots that must not be read (a save whose file could not be read back), with the reason shown.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<VppPackage, string> ReloadNeeded = new();

    /// <summary>True after a save whose file could not be read back: nothing is read until <see cref="ReloadFromDisk"/>.</summary>
    public bool NeedsReload => ReloadNeeded.TryGetValue(Current, out _);

    /// <summary>Test seam for review finding 14: replaces the read-back of the saved file.</summary>
    internal Func<string, VppPackage>? ReopenForTest { get; set; }

    public override bool ConfirmSave()
    {
        if (IsBusy) { ShowStatus("Wait for the current operation to finish, or cancel it."); return false; }
        if (ReloadNeeded.TryGetValue(Current, out var mustReload)) { Shell.Dialogs.ShowError($"{DisplayName} needs to be reloaded", mustReload); return false; }
        var problems = VppSaver.CheckBeforeSave(Current);
        var errors = problems.Where(p => p.Severity == VppSeverity.Error).ToList();
        if (errors.Count == 0) return true;
        Shell.Dialogs.ShowError($"{DisplayName} cannot be saved yet",
            "Fix these first (the problem list in the status bar shows them all):\n\n" + string.Join("\n", errors.Take(12).Select(e => "• " + e.Message)) + (errors.Count > 12 ? $"\n... and {errors.Count - 12} more." : string.Empty));
        return false;
    }

    /// <summary>
    /// Saves on a worker thread through <see cref="VppSaver"/> (temporary file, verify, atomic replace; never in
    /// place), showing progress with a Cancel button, and returns when it finished. The shell's save path is
    /// synchronous, so the wait pumps the dispatcher: the window stays live, edits are refused meanwhile.
    /// </summary>
    public override void SaveTo(string path)
    {
        CommitPendingEdits();
        var package = Current;
        Task<VppPackage>? save = null;
        var run = RunOperationAsync($"Saving {Path.GetFileName(path)}", async op =>
        {
            var progress = new Progress<VppSaveProgress>(p => op.Report(p.BytesTotal > 0 ? p.Fraction : null, p.CurrentName ?? p.Phase.ToString()));
            _ownWrite = true;
            _diskWatcher.IsSuspended = true;
            save = VppSaver.SaveAsync(package, path, progress, op.Token, new VppSaveOptions(KeepBackup: Module.Settings.KeepBackup) { ReopenForTest = ReopenForTest });
            try { await save.ConfigureAwait(true); }
            finally { _diskWatcher.IsSuspended = false; _ownWrite = false; }
        });
        WaitPumping(run, Shell.Dispatcher);
        if (save is null) throw new InvalidOperationException("The save could not start.");
        if (save.IsCanceled || (save.IsFaulted && save.Exception?.GetBaseException() is OperationCanceledException))
            throw new OperationCanceledException("The save was cancelled. The file on disk was not changed.");
        if (save.IsFaulted && save.Exception!.GetBaseException() is VppSavedButUnreadableException unreadable)
        {
            // Review finding 14: the file on disk is the new one, but its directory could not be read back. The save is
            // done; the old snapshot's offsets point into the replaced file, so every read is refused until a reload.
            string reason = $"{Path.GetFileName(path)} was saved, but Cairn could not read the saved file back ({unreadable.InnerException?.Message}). "
                + "Its entries are not read until the packfile is reloaded: use Reload.";
            History.Reset(package);
            ReloadNeeded.AddOrUpdate(package, reason);
            _diskStamp = StampOf(path);
            MarkSaved(path);
            WatchDisk();
            Refresh();
            HasExternalChange = true;
            Notice = reason;
            return;
        }
        if (save.IsFaulted)
        {
            var ex = save.Exception!.GetBaseException();
            if (ex is IOException or UnauthorizedAccessException or InvalidOperationException) ExceptionDispatchInfo.Throw(ex);
            throw new IOException(ex.Message, ex);
        }
        var saved = save.Result;
        // The saved package reads from the new file; older snapshots may point into data that was just replaced, so
        // the history starts again here.
        History.Reset(saved);
        _diskStamp = saved.Stamp ?? StampOf(path);
        MarkSaved(path);
        WatchDisk();
        Refresh();
        if (Path.GetFileName(path).Length > VppValidator.MaxPackfileNameLength)
            Notice = $"\"{Path.GetFileName(path)}\" has {Path.GetFileName(path).Length} characters: the game only loads packfiles whose file name has at most {VppValidator.MaxPackfileNameLength}. Use File > Save As to choose a shorter name.";
    }

    /// <summary>In-memory data above the inline cap is kept in side files beside the recovery store's manifests.</summary>
    public override byte[]? CaptureRecovery() => IsDirty
        ? VppRecoveryManifest.Capture(Current, spillFolder: Path.Combine(Cairn.Workspace.RecoveryStore.DefaultDirectory, "vpp-" + Id)).ToJson()
        : null;

    /// <summary>Recovery data is a manifest (original path, operations, added files), never the packfile's bytes.</summary>
    protected override VppPackage Parse(byte[] bytes, string name) => VppRecoveryManifest.FromJson(bytes).Restore(out _);

    /// <summary>The whole packfile in memory: only for small packfiles (tests); saving streams instead.</summary>
    protected override byte[] Write(VppPackage snapshot)
    {
        using var ms = new MemoryStream();
        VppWriter.Write(snapshot, ms, null, CancellationToken.None);
        return ms.ToArray();
    }

    protected override bool MatchesSaved(byte[] bytes) => false;

    // ---- the packfile on disk --------------------------------------------------------------------------------

    private static VppArchiveStamp? StampOf(string? path)
    {
        try { return path is not null && File.Exists(path) ? VppArchiveStamp.Of(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private void WatchDisk()
    {
        if (FilePath is null) { _diskWatcher.Stop(); return; }
        _diskWatcher.Watch(Path.GetDirectoryName(FilePath), Path.GetFileName(FilePath));
    }

    /// <summary>Compares the packfile's length and time with the ones it was read with (no data is read).</summary>
    internal void CheckDisk()
    {
        if (FilePath is null || _ownWrite || IsBusy) return;
        if (!File.Exists(FilePath)) { IsMissingOnDisk = true; return; }
        IsMissingOnDisk = false;
        var now = StampOf(FilePath);
        if (now is not null && _diskStamp is not null && now != _diskStamp) HasExternalChange = true;
    }

    /// <summary>
    /// Null when the entries of <paramref name="items"/> stored in <paramref name="package"/>'s own file can be read:
    /// that file still has the length and time it was opened with. Otherwise why they cannot (their offsets would point
    /// at other data). Entries held in memory or added from other files are not affected.
    /// </summary>
    internal static string? StaleProblem(VppPackage package, IEnumerable<VppItem> items)
    {
        if (ReloadNeeded.TryGetValue(package, out var mustReload)) return mustReload;
        if (package.Path is not { } path || package.Stamp is not { } stamp) return null;
        var affected = items.Where(i => i.Source is ArchiveSource a && string.Equals(a.ArchivePath, path, StringComparison.OrdinalIgnoreCase)).ToList();
        if (affected.Count == 0) return null;
        VppArchiveStamp? now;
        try { now = File.Exists(path) ? VppArchiveStamp.Of(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { now = null; }
        if (now == stamp) return null;
        string names = string.Join(", ", affected.Take(5).Select(i => i.Name)) + (affected.Count > 5 ? $" and {affected.Count - 5:N0} more" : "");
        return (now is null ? $"{Path.GetFileName(path)} is no longer on disk" : $"{Path.GetFileName(path)} changed on disk after it was opened")
            + $", so the data of {names} cannot be read correctly. Reload the packfile (or reopen it) first.";
    }

    /// <summary>
    /// Review finding 12: true when <paramref name="items"/> can be read. When the packfile changed on disk, nothing is
    /// read: without pending changes the user may reload it at once; otherwise they are told which entries are affected.
    /// </summary>
    internal bool EnsureReadable(IReadOnlyList<VppItem> items)
    {
        if (StaleProblem(Current, items) is not { } problem) return true;
        if (FilePath is not null && File.Exists(FilePath)) HasExternalChange = true; else IsMissingOnDisk = true;
        string title = NeedsReload ? $"{DisplayName} needs to be reloaded" : $"{DisplayName} changed on disk";
        if (!IsDirty && !IsMissingOnDisk && Shell.Dialogs.Confirm(title, problem + "\n\nReload it now? Nothing was read.", "Reload"))
            ReloadFromDisk();
        else Shell.Dialogs.ShowError(title, problem + (IsDirty ? " Your pending changes are kept; entries held in memory or added from files can still be saved with Save As." : ""));
        return false;
    }

    /// <summary>Reads the packfile again from disk; pending changes are dropped (after a confirmation when there are any).</summary>
    public void ReloadFromDisk()
    {
        if (FilePath is null || IsBusy) return;
        if (IsDirty && !Shell.Dialogs.Confirm("Reload the packfile from disk?", $"Your {PendingChanges} pending change(s) to {DisplayName} will be lost. This cannot be undone.", "Reload"))
            return;
        try
        {
            var fresh = VppPackage.Open(FilePath);
            History.Reset(fresh);
            _diskStamp = fresh.Stamp ?? StampOf(FilePath);
            MarkSaved(FilePath);
            HasExternalChange = false;
            Refresh();
            ShowStatus($"Reloaded {DisplayName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException)
        {
            ShowStatus("The packfile could not be reloaded: " + ex.Message);
        }
    }

    protected override FrameworkElement CreateView() => new VppDocumentView(this);

    public override void Dispose()
    {
        _operation?.Cancel();
        List.InfoCache?.Dispose();
        _diskWatcher.Dispose();
        if (_work is { } work && !work.TryDelete()) Module.DeleteLater(work.Root);
        _work = null;
        base.Dispose();
    }
}
