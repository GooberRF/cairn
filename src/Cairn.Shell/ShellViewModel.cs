using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Shell;

/// <summary>
/// The generic half of the old apps' main view model: documents, tabs, file commands, recovery and
/// the module host. It knows modules only through the contracts in <c>Cairn.Ui.Modules</c>.
/// </summary>
public sealed class ShellViewModel : ObservableObject, IShellContext
{
    private const int MaxClosedTabs = 20;
    private readonly List<ClosedTab> _closed = [];
    private readonly RecentFilesList _recent;
    private readonly DispatcherTimer _recoveryTimer;
    private IDocument? _active;
    private string _status = "Ready";

    public ShellViewModel(Window window, AppSettings settings, ThemeService theme, IReadOnlyList<IModule> modules,
        bool diagnostic, IDialogService dialogs, RecoveryStore recovery)
    {
        MainWindow = window;
        Settings = settings;
        Theme = theme;
        Modules = modules;
        IsDiagnosticRun = diagnostic;
        Dialogs = dialogs;
        Recovery = recovery;
        Assets = new AssetHost(window.Dispatcher);
        _recent = new RecentFilesList(settings.RecentFiles, settings.MaxRecentFiles);
        Kinds = modules.SelectMany(m => m.DocumentKinds).ToList();
        Importers = modules.SelectMany(m => m.Importers).ToList();

        NewCommand = new RelayCommand(p => { if (p is IDocumentKind k && k.CreateNew() is { } d) AddDocument(d); });
        OpenCommand = new RelayCommand(Open);
        OpenRecentCommand = new RelayCommand(p => { if (p is string item && !OpenRecent(item)) { _recent.Remove(item); SaveRecent(); } });
        SaveCommand = new RelayCommand(() => { if (_active != null) Save(_active); }, () => _active != null);
        SaveAsCommand = new RelayCommand(() => { if (_active != null) SaveAs(_active); }, () => _active != null);
        SaveAllCommand = new RelayCommand(() => { foreach (var d in Documents.Where(d => d.IsDirty).ToList()) if (!Save(d)) break; }, () => Documents.Any(d => d.IsDirty));
        CloseTabCommand = new RelayCommand(p => { if ((p as IDocument ?? _active) is { } d) Close(d); }, _ => _active != null);
        CloseAllCommand = new RelayCommand(() => CloseAll(), () => Documents.Count > 0);
        ReopenClosedCommand = new RelayCommand(ReopenClosed, () => _closed.Count > 0);
        UndoCommand = new RelayCommand(() => { _active?.CommitPendingEdits(); _active?.Undo(); }, () => _active?.CanUndo == true);
        RedoCommand = new RelayCommand(() => _active?.Redo(), () => _active?.CanRedo == true);

        _recoveryTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => SnapshotRecovery(), window.Dispatcher);
        _recoveryTimer.Start();
    }

    // ---- IShellContext ----
    public Dispatcher Dispatcher => MainWindow.Dispatcher;
    public Window MainWindow { get; }
    public IDialogService Dialogs { get; set; }
    public AppSettings Settings { get; }
    public ThemeService Theme { get; }
    public AssetHost Assets { get; }
    public bool IsDiagnosticRun { get; }
    public ObservableCollection<IDocument> OpenDocuments { get; } = [];
    public IReadOnlyList<IDocument> Documents => OpenDocuments;
    public event EventHandler? ActiveDocumentChanged;
    public event EventHandler? DocumentsChanged;

    public IReadOnlyList<IModule> Modules { get; }
    public IReadOnlyList<IDocumentKind> Kinds { get; }
    public IReadOnlyList<IFileImporter> Importers { get; }
    public RecoveryStore Recovery { get; }
    /// <summary>
    /// The Recent list, newest first: file paths and archive entries (<see cref="RecentFilesList.EntryReference"/>).
    /// Paths inside a module's temporary work area, recorded by older versions, are left out.
    /// </summary>
    public IReadOnlyList<string> RecentFiles => _recent.Items.Where(i => !IsWorkCopy(i)).ToList();
    public int ClosedTabCount => _closed.Count;

    public ICommand NewCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand CloseAllCommand { get; }
    public RelayCommand ReopenClosedCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }

    public IDocument? ActiveDocument
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            if (_active != null) { _active.CommitPendingEdits(); _active.OnDeactivated(); _active.PropertyChanged -= OnActivePropertyChanged; }
            _active = value;
            if (_active != null) { _active.PropertyChanged += OnActivePropertyChanged; _active.OnActivated(); }
            RaiseAll(nameof(ActiveDocument), nameof(ActiveView), nameof(HasDocuments), nameof(StatusItems), nameof(WindowTitle));
            RefreshCommandStates();
            ActiveDocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public object? ActiveView => _active?.View;
    public bool HasDocuments => OpenDocuments.Count > 0;
    public IReadOnlyList<StatusItem> StatusItems => _active?.StatusItems ?? [];
    public string WindowTitle => _active is { } d ? $"{d.TabHeader} - Cairn" : "Cairn";
    public string StatusText { get => _status; private set => Set(ref _status, value); }
    public string UndoHeader => _active?.UndoLabel is { } l ? $"_Undo {l}" : "_Undo";
    public string RedoHeader => _active?.RedoLabel is { } l ? $"_Redo {l}" : "_Redo";

    private void OnActivePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IDocument.StatusItems)) Raise(nameof(StatusItems));
        if (e.PropertyName is nameof(IDocument.TabHeader) or nameof(IDocument.IsDirty) or null) Raise(nameof(WindowTitle));
        RefreshCommandStates();
    }

    public void AddDocument(IDocument document, bool activate = true)
    {
        OpenDocuments.Add(document);
        Raise(nameof(HasDocuments));
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
        if (activate || _active == null) ActiveDocument = document;
        RefreshCommandStates();
    }

    public void Activate(IDocument document) => ActiveDocument = document;

    public bool OpenFile(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        if (OpenDocuments.FirstOrDefault(d => d.FilePath != null && string.Equals(Path.GetFullPath(d.FilePath), full, StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            Activate(open);
            ShowStatus($"{Path.GetFileName(full)} is already open");
            return true;
        }
        if (!File.Exists(full))
        {
            Dialogs.ShowError("File not found", full);
            return false;
        }
        var ext = Path.GetExtension(full).ToLowerInvariant();
        try
        {
            if (Kinds.FirstOrDefault(k => k.Extensions.Contains(ext)) is { } kind)
            {
                AddDocument(kind.Open(full));
            }
            else
            {
                var scored = Importers.Select(i => (Importer: i, Score: SafeProbe(i, full))).Where(s => s.Score > 0).OrderByDescending(s => s.Score).ToList();
                if (scored.Count == 0)
                {
                    Dialogs.ShowError("Cairn cannot open this file", $"{Path.GetFileName(full)}: no module reads {ext} files.");
                    return false;
                }
                var tied = scored.TakeWhile(s => s.Score == scored[0].Score).Select(s => s.Importer).ToList();
                var pick = tied.Count == 1 ? 0 : Dialogs.Choose("Open as", $"Several modules can read {Path.GetFileName(full)}.", [.. tied.Select(t => t.DisplayName), "Cancel"], tied.Count);
                if (pick < 0 || pick >= tied.Count) return false;
                tied[pick].Import(full);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException)
        {
            Dialogs.ShowError($"Could not open {Path.GetFileName(full)}", ex.Message, ex.ToString());
            return false;
        }
        AddRecent(full);
        ShowStatus($"Opened {Path.GetFileName(full)}");
        return true;
    }

    /// <summary>Opens several paths (Explorer forwarding from a second instance, drag-drop); an already-open path activates its tab.</summary>
    public void OpenFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths) OpenFile(path);
    }

    private static int SafeProbe(IFileImporter importer, string path)
    {
        try { return importer.Probe(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    public bool OpenLocation(AssetLocation location)
    {
        if (location.ArchivePath is null && location.FilePath is { } file) return OpenFile(file);
        var ext = Path.GetExtension(location.ResolvedName).ToLowerInvariant();
        if (Kinds.FirstOrDefault(k => k.Extensions.Contains(ext)) is not { } kind) return false;
        try
        {
            AddDocument(kind.OpenEntry(location, location.ReadAllBytes(), $"{location.ResolvedName} in {Path.GetFileName(location.ArchivePath)}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException)
        {
            Dialogs.ShowError($"Could not open {location.ResolvedName}", ex.Message, ex.ToString());
            return false;
        }
    }

    public bool Save(IDocument document)
    {
        if (document.FilePath is null || document.IsReadOnly) return SaveAs(document);
        return document.ChooseSaveAsInstead() switch
        {
            null => false,
            true => SaveAs(document),
            false => SaveTo(document, document.FilePath),
        };
    }

    public bool SaveAs(IDocument document)
    {
        document.CommitPendingEdits();
        var ext = document.Kind.Extensions.FirstOrDefault() ?? string.Empty;
        var folder = document.FilePath is { } p ? Path.GetDirectoryName(p) : Settings.LastSaveFolder;
        var suggested = Path.GetFileNameWithoutExtension(document.DisplayName.TrimEnd('*', ' '));
        var path = Dialogs.SaveDocument(folder, suggested + ext, ext, document.Kind.FileFilter);
        if (path is null) return false;
        // Two tabs owning one file would overwrite each other's saves; the document's own path is a normal save.
        if (OpenDocuments.FirstOrDefault(d => d != document && d.FilePath != null && string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) is { } other)
        {
            Dialogs.ShowError($"Could not save {Path.GetFileName(path)}", $"This file is open in the tab \"{other.DisplayName}\". Close that tab first, or save under another name.");
            return false;
        }
        if (!SaveTo(document, path)) return false;
        Settings.LastSaveFolder = Path.GetDirectoryName(path);
        return true;
    }

    private bool SaveTo(IDocument document, string path)
    {
        document.CommitPendingEdits();
        if (!document.ConfirmSave()) return false;
        try { document.SaveTo(path); }
        catch (OperationCanceledException)
        {
            // The user cancelled the save (IDocument.SaveTo): nothing was written and the document stays dirty.
            ShowStatus("Save cancelled; the file was not changed.");
            RefreshCommandStates();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Dialogs.ShowError($"Could not save {Path.GetFileName(path)}", ex.Message, ex.ToString());
            return false;
        }
        Recovery.Discard(document.Id);
        AddRecent(path);
        ShowStatus($"Saved {Path.GetFileName(path)}");
        RefreshCommandStates();
        return true;
    }

    public bool Close(IDocument document)
    {
        document.CommitPendingEdits();
        if (document.IsDirty && !ResolveUnsaved([document])) return false;
        Remove(document);
        return true;
    }

    /// <summary>Closes <paramref name="document"/> discarding unsaved changes, with no prompt (self-tests: a document an earlier test left dirty must not block the run on a modal).</summary>
    public void CloseDiscarding(IDocument document)
    {
        if (OpenDocuments.Contains(document)) Remove(document);
    }

    /// <summary>Closes every tab after one prompt listing the unsaved ones; false when cancelled.</summary>
    public bool CloseAll()
    {
        foreach (var d in OpenDocuments) d.CommitPendingEdits();
        var dirty = OpenDocuments.Where(d => d.IsDirty).ToList();
        if (dirty.Count > 0 && !ResolveUnsaved(dirty)) return false;
        foreach (var d in OpenDocuments.ToList()) Remove(d);
        return true;
    }

    private bool ResolveUnsaved(IReadOnlyList<IDocument> dirty)
    {
        switch (Dialogs.AskUnsavedChanges([.. dirty.Select(d => d.DisplayName)]))
        {
            case UnsavedChoice.Save: return dirty.All(Save);
            case UnsavedChoice.DontSave: return true;
            default: return false;
        }
    }

    private void Remove(IDocument document)
    {
        var index = OpenDocuments.IndexOf(document);
        if (index < 0) return;
        _closed.Add(new ClosedTab(document.Kind.Id, document.FilePath, document.DisplayName, document.IsDirty || document.FilePath is null ? document.CaptureRecovery() : null));
        if (_closed.Count > MaxClosedTabs) _closed.RemoveAt(0);
        OpenDocuments.RemoveAt(index);
        if (ReferenceEquals(_active, document))
            ActiveDocument = OpenDocuments.Count == 0 ? null : OpenDocuments[Math.Min(index, OpenDocuments.Count - 1)];
        Recovery.Discard(document.Id);
        document.Dispose();
        Raise(nameof(HasDocuments));
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
        RefreshCommandStates();
    }

    private void ReopenClosed()
    {
        if (_closed.Count == 0) return;
        var tab = _closed[^1];
        _closed.RemoveAt(_closed.Count - 1);
        if (tab.Data is null && tab.Path is { } path) { OpenFile(path); return; }
        if (tab.Data is null || Kinds.FirstOrDefault(k => k.Id == tab.KindId) is not { } kind) return;
        AddDocument(kind.Restore(new RecoverySnapshot(Guid.NewGuid().ToString("N"), tab.Path, tab.DisplayName, DateTime.UtcNow, tab.KindId, tab.Data)));
        RefreshCommandStates();
    }

    private sealed record ClosedTab(string KindId, string? Path, string DisplayName, byte[]? Data);

    public void ShowStatus(string message) => StatusText = message;

    /// <summary>Raised when a module asks for a refresh: the window re-evaluates contribution visibility and panels.</summary>
    public event EventHandler? ContributionsInvalidated;

    public void RefreshCommands()
    {
        RefreshCommandStates();
        ContributionsInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The shell's own cheap refresh (commands and Undo/Redo labels only), used on every document change.</summary>
    private void RefreshCommandStates()
    {
        foreach (var c in new[] { SaveCommand, SaveAsCommand, SaveAllCommand, CloseTabCommand, CloseAllCommand, ReopenClosedCommand, UndoCommand, RedoCommand })
            c.RaiseCanExecuteChanged();
        RaiseAll(nameof(UndoHeader), nameof(RedoHeader));
        CommandManager.InvalidateRequerySuggested();
    }

    public void ShowHelp(string topicId) => Cairn.Shell.Dialogs.HelpWindow.Show(this, topicId);

    /// <summary>
    /// The topic the shell's F1 opens: the first help topic of the module that owns the active document's kind,
    /// unless that module routes F1 itself (then the shell's F1 is never reached for its documents); null = the shortcut table.
    /// </summary>
    public HelpTopic? ContextHelpTopic
    {
        get
        {
            var kindId = ActiveDocument?.Kind.Id;
            var owner = kindId is null ? null : Modules.FirstOrDefault(m => m.DocumentKinds.Any(k => k.Id == kindId));
            if (owner is null || owner.Shortcuts.Any(s => s.Key == Key.F1 && s.Modifiers == ModifierKeys.None)) return null;
            return owner.HelpTopics.FirstOrDefault();
        }
    }

    /// <summary>F1: the active document module's help topic, else the keyboard shortcuts.</summary>
    public void ShowContextHelp() => ShowHelp(ContextHelpTopic?.Id ?? "shortcuts");

    public void ShowSettings(string? pageTitle = null) => global::Cairn.Shell.Dialogs.SettingsDialog.Show(this, pageTitle);

    public bool ShowPanel(string panelId) => MainWindow is global::Cairn.Shell.MainWindow window && window.ShowPanel(panelId);

    private void Open()
    {
        var filter = string.Join("|", Kinds.Select(k => k.FileFilter).Concat(Importers.Select(i => i.FileFilter)));
        var all = string.Join(";", Kinds.SelectMany(k => k.Extensions).Concat(Importers.SelectMany(i => i.Extensions)).Distinct().Select(e => "*" + e));
        filter = (all.Length > 0 ? $"Supported files|{all}|" : string.Empty) + (filter.Length > 0 ? filter + "|" : string.Empty) + "All files (*.*)|*.*";
        foreach (var path in Dialogs.OpenDocuments(Settings.LastSaveFolder, filter)) OpenFile(path);
    }

    /// <summary>
    /// Records an opened or saved file. A module's temporary work copy is recorded as the archive entry it came from
    /// (or not at all when that is unknown): its path breaks once the copy is cleaned up and means nothing to the user.
    /// </summary>
    private void AddRecent(string path)
    {
        string? item = path;
        if (Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.IsWorkCopy(path)) is { } provider)
            item = provider.ArchiveEntryOf(path) is { } entry ? RecentFilesList.EntryReference(entry.ArchivePath, entry.EntryName) : null;
        if (item is not null) _recent.Add(item);
        SaveRecent();
    }

    /// <summary>
    /// Writes the list back to the settings, dropping work-area paths older versions recorded, and tells the views.
    /// Called once the modules are initialised (they know their work areas), so the next settings save drops them.
    /// </summary>
    public void SaveRecent()
    {
        foreach (var stale in _recent.Items.Where(IsWorkCopy).ToList()) _recent.Remove(stale);
        Settings.RecentFiles = [.. _recent.Items];
        Raise(nameof(RecentFiles));
    }

    /// <summary>Self-tests: puts <paramref name="item"/> at the top of the list as it is (as an older version may have recorded it), without saving.</summary>
    internal void InjectRecentForTest(string item) { _recent.Add(item); Raise(nameof(RecentFiles)); }

    /// <summary>Self-tests: removes <paramref name="item"/> again and saves the list.</summary>
    internal void ForgetRecentForTest(string item) { _recent.Remove(item); SaveRecent(); }

    private bool IsWorkCopy(string item) =>
        !RecentFilesList.TryParseEntry(item, out _, out _) && Modules.OfType<IWorkCopyProvider>().Any(p => p.IsWorkCopy(item));

    /// <summary>Opens a Recent item: a file, or an archive entry through the module that opened it. False when it could not be opened (the user was told).</summary>
    public bool OpenRecent(string item)
    {
        if (!RecentFilesList.TryParseEntry(item, out var archive, out var entry)) return OpenFile(item);
        if (!File.Exists(archive))
        {
            Dialogs.ShowError("File not found", $"{archive}\n\n{entry} was opened from this packfile, which is no longer there.");
            return false;
        }
        if (Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.CanOpenArchive(archive)) is not { } provider)
        {
            Dialogs.ShowError("Cairn cannot open this file", $"{Path.GetFileName(archive)}: no module opens entries of {Path.GetExtension(archive)} files.");
            return false;
        }
        return provider.OpenArchiveEntry(archive, entry);
    }

    /// <summary>How a Recent item reads: the file name (or "archive › entry") and the folder shown dimmed beside it.</summary>
    public static (string Name, string Folder) DescribeRecent(string item) =>
        RecentFilesList.TryParseEntry(item, out var archive, out var entry)
            ? ($"{Path.GetFileName(archive)} {(char)0x203A} {entry}", Path.GetDirectoryName(archive) ?? string.Empty)
            : (Path.GetFileName(item), Path.GetDirectoryName(item) ?? string.Empty);

    // ---- recovery ----

    /// <summary>Writes a snapshot of every dirty document; called by the 30 s timer and on a crash.</summary>
    public IReadOnlyList<string> SnapshotRecovery()
    {
        var saved = new List<string>();
        foreach (var d in OpenDocuments)
        {
            try
            {
                if (d.IsDirty && d.CaptureRecovery() is { } bytes && Recovery.Save(d.Id, d.FilePath, d.DisplayName, d.Kind.Id, bytes)) saved.Add(d.DisplayName);
            }
            catch (Exception ex)
            {
                // Best effort, also on the crash path: one failing document must not cost the others their snapshot.
                System.Diagnostics.Trace.TraceWarning($"Recovery snapshot of {d.DisplayName} failed: {ex.Message}");
            }
        }
        return saved;
    }

    /// <summary>
    /// Offers the snapshots left by a previous session (see <see cref="ChooseRecovery"/>). Chosen ones
    /// open as unsaved tabs and the others offered are deleted; "Not now" keeps them all for next time.
    /// Snapshots of a kind no loaded module opens are never offered or touched.
    /// </summary>
    public int OfferRecovery()
    {
        var snapshots = Recovery.List().Where(s => Kinds.Any(k => k.Id == s.DocumentKind)).ToList();
        if (snapshots.Count == 0) return 0;
        var chosen = ChooseRecovery is { } choose ? choose(snapshots) : Cairn.Shell.Dialogs.RecoveryDialog.Show(MainWindow, snapshots);
        if (chosen is null) return 0;
        foreach (var s in snapshots.Where(s => !chosen.Any(c => c.Id == s.Id))) Recovery.Discard(s.Id);
        snapshots = [.. chosen];
        var restored = 0;
        foreach (var s in snapshots)
        {
            try
            {
                AddDocument(Kinds.First(k => k.Id == s.DocumentKind).Restore(s));
                Recovery.Discard(s.Id);
                restored++;
            }
            catch (OperationCanceledException)
            {
                // The user chose to discard this snapshot in the kind's own prompt: delete it quietly, never offer it again.
                Recovery.Discard(s.Id);
                StatusText = $"Recovered changes to {s.DisplayName} discarded.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or Cairn.Formats.AssetFormatException)
            {
                Dialogs.ShowError($"Could not recover {s.DisplayName}", ex.Message, ex.ToString());
            }
        }
        return restored;
    }

    public void StopRecoveryTimer() => _recoveryTimer.Stop();

    // ---- shortcuts ----

    /// <summary>Every shortcut the shell knows: its own rows, then every module's.</summary>
    public IReadOnlyList<ShortcutInfo> AllShortcuts => [.. ShellShortcuts, .. Modules.SelectMany(m => m.Shortcuts)];

    public IReadOnlyList<ShortcutInfo> ShellShortcuts =>
    [
        new("File", "Open a file", Key.O, ModifierKeys.Control, OpenCommand),
        new("File", "Save", Key.S, ModifierKeys.Control, SaveCommand),
        new("File", "Save as", Key.S, ModifierKeys.Control | ModifierKeys.Shift, SaveAsCommand),
        new("File", "Close tab", Key.W, ModifierKeys.Control, CloseTabCommand, AllowInTextInput: true),
        new("File", "Close tab", Key.F4, ModifierKeys.Control, CloseTabCommand, AllowInTextInput: true),
        new("File", "Reopen closed tab", Key.T, ModifierKeys.Control | ModifierKeys.Shift, ReopenClosedCommand, AllowInTextInput: true),
        new("Edit", "Undo", Key.Z, ModifierKeys.Control, UndoCommand),
        new("Edit", "Redo", Key.Y, ModifierKeys.Control, RedoCommand),
        new("Edit", "Redo", Key.Z, ModifierKeys.Control | ModifierKeys.Shift, RedoCommand),
        new("Window", "Next tab", Key.Tab, ModifierKeys.Control, new RelayCommand(() => Cycle(1)), AllowInTextInput: true),
        new("Window", "Previous tab", Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, new RelayCommand(() => Cycle(-1)), AllowInTextInput: true),
        new("Help", "Help for the active document (else keyboard shortcuts)", Key.F1, ModifierKeys.None, new RelayCommand(ShowContextHelp), AllowInTextInput: true),
        new("View", "Show or hide the left pane", Key.L, ModifierKeys.Control | ModifierKeys.Shift, ToggleLeftPaneCommand, AllowInTextInput: true),
        new("View", "Show or hide the bottom pane", Key.M, ModifierKeys.Control | ModifierKeys.Shift, ToggleBottomPaneCommand, AllowInTextInput: true),
    ];

    private void Cycle(int step)
    {
        if (OpenDocuments.Count < 2 || _active is null) return;
        ActiveDocument = OpenDocuments[(OpenDocuments.IndexOf(_active) + step + OpenDocuments.Count) % OpenDocuments.Count];
    }


    // ---- pane toggles (the main window owns the panes) ----

    /// <summary>Raised with "left" or "bottom" when a pane toggle shortcut or menu item fires.</summary>
    public event EventHandler<string>? PaneToggleRequested;

    private RelayCommand? _toggleLeftPane, _toggleBottomPane;

    public RelayCommand ToggleLeftPaneCommand => _toggleLeftPane ??= new RelayCommand(() => PaneToggleRequested?.Invoke(this, "left"));

    public RelayCommand ToggleBottomPaneCommand => _toggleBottomPane ??= new RelayCommand(() => PaneToggleRequested?.Invoke(this, "bottom"));

    /// <summary>
    /// Picks which recovery snapshots to restore: returns the ones to restore (the others offered are
    /// deleted) or null to keep them all for next time. Null (the default) shows the recovery dialog;
    /// self-tests set it so no UI appears.
    /// </summary>
    public Func<IReadOnlyList<RecoverySnapshot>, IReadOnlyList<RecoverySnapshot>?>? ChooseRecovery { get; set; }
}