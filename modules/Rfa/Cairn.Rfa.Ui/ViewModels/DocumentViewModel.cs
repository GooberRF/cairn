using System.Collections.ObjectModel;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>What a tab holds.</summary>
public enum DocumentKind
{
    /// <summary>An .rfa animation clip.</summary>
    Clip,
    /// <summary>A .v3c character mesh.</summary>
    CharacterMesh,
    /// <summary>A .v3m static mesh (opens read-only).</summary>
    StaticMesh,
}

/// <summary>
/// One tab. Everything the shell needs regardless of what the tab holds: identity and origin (a file,
/// or an entry inside a .vpp), dirty state and undo/redo, save support, external-change detection,
/// diagnostics and the Problems panel, the viewport scene and transport, the bone selection, and the
/// inspector's tab list. <see cref="DocumentViewModel{T}"/> adds the immutable snapshot and its history.
/// </summary>
public abstract partial class DocumentViewModel : ObservableObject, IDisposable, IViewportHost, IDocument
{
    private readonly FileChangeWatcher _watcher;
    private IReadOnlyList<Diagnostic> _diagnostics = [];
    private string? _filePath;
    private string _displayName;
    private bool _hasExternalChange;
    private bool _isMissingOnDisk;
    private string? _statusMessage;
    private InspectorTab? _selectedInspectorTab;
    private DispatcherTimer? _statusTimer;

    protected DocumentViewModel(RfaWorkspace shell, string displayName, string? filePath, AssetLocation? archiveOrigin)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _displayName = displayName;
        _filePath = filePath;
        ArchiveOrigin = archiveOrigin;
        Selection = new BoneSelection();
        Scene = new SceneViewModel(shell.Display, Selection, shell.Textures);
        Playback = new PlaybackViewModel(() => shell.TimeUnit, RfaTime.Base);
        Playback.UnitCycleRequested += (_, _) => shell.CycleTimeUnit();
        Problems = new ProblemsViewModel(this);

        _watcher = new FileChangeWatcher(shell.Dispatcher);
        _watcher.Changed += (_, _) => OnFileChangedOnDisk();
        WatchFile();

        ReloadCommand = new RelayCommand(ReloadFromDisk, () => _filePath is not null);
        KeepMineCommand = new RelayCommand(KeepMine);
        DismissMissingCommand = new RelayCommand(() => IsMissingOnDisk = false);
    }

    /// <summary>The shell that owns the tab.</summary>
    public RfaWorkspace Shell { get; }

    /// <summary>A stable id for the tab (recovery snapshots are keyed by it).</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>What the tab holds.</summary>
    public abstract DocumentKind Kind { get; }

    /// <summary>The file extension a save writes: ".rfa", ".v3c" or ".v3m".</summary>
    public abstract string Extension { get; }

    /// <summary>The recovery store's kind string ("rfa", "v3c", "v3m").</summary>
    public string RecoveryKind => Extension.TrimStart('.');

    /// <summary>The file on disk, or null for a document from a .vpp (or recovered without a path).</summary>
    public string? FilePath
    {
        get => _filePath;
        protected set
        {
            if (!Set(ref _filePath, value)) return;
            WatchFile();
            RaiseAll(nameof(TabToolTip), nameof(Folder), nameof(OriginText), nameof(IsFromArchive));
            ReloadCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The archive entry the document came from (read-only at its origin), or null.</summary>
    public AssetLocation? ArchiveOrigin { get; protected set; }

    /// <summary>True when the document came from inside a .vpp and has not been saved elsewhere yet: Save becomes Save As.</summary>
    public bool IsFromArchive => _filePath is null && ArchiveOrigin is not null;

    /// <summary>The folder the document lives in (its resolver searches it first), or null.</summary>
    public string? Folder => _filePath is null ? null : Path.GetDirectoryName(_filePath);

    /// <summary>The file name shown on the tab.</summary>
    public string DisplayName
    {
        get => _displayName;
        protected set
        {
            if (!Set(ref _displayName, value)) return;
            RaiseAll(nameof(TabHeader), nameof(TabToolTip));
        }
    }

    /// <summary>True when the document can play a clip (the transport shows); false for a static mesh.</summary>
    public virtual bool HasTransport => true;

    /// <summary>True when nothing may be edited (a .v3m).</summary>
    public virtual bool IsReadOnly => false;

    /// <summary>The tab caption: name, a star when dirty.</summary>
    public string TabHeader => DisplayName + (IsDirty ? " *" : string.Empty);

    /// <summary>The tab tooltip: where the document comes from.</summary>
    public string TabToolTip => OriginText + (IsReadOnly ? "\nRead-only." : string.Empty);

    /// <summary>"C:\…\ult2_walk.rfa", or "ult2_walk.rfa in anims.vpp (read-only origin: Save will ask for a new location)".</summary>
    public string OriginText => _filePath
        ?? (ArchiveOrigin is { } a
            ? $"{a.ResolvedName} in {a.ArchivePath} (read-only origin: Save asks where to write a copy)"
            : $"{DisplayName} (not saved yet)");

    // ── Editing state (implemented by DocumentViewModel<T>) ──────────────────

    /// <summary>True when the snapshot differs from the one last saved.</summary>
    public abstract bool IsDirty { get; }

    public abstract bool CanUndo { get; }

    public abstract bool CanRedo { get; }

    /// <summary>The label of the step Undo reverts, e.g. "Set ramp in".</summary>
    public abstract string? UndoLabel { get; }

    public abstract string? RedoLabel { get; }

    public abstract void Undo();

    public abstract void Redo();

    /// <summary>The current snapshot serialised for saving. Throws <see cref="ArgumentException"/> when the model cannot be written.</summary>
    public abstract byte[] Serialize();

    /// <summary>
    /// A serializer bound to the current (immutable) snapshot, safe to call from any thread: the crash
    /// handler uses it when the UI thread cannot be reached.
    /// </summary>
    public abstract Func<byte[]> CaptureSerializer();

    /// <summary>Records a successful save to <paramref name="path"/>.</summary>
    public virtual void MarkSaved(string path)
    {
        FilePath = path;
        DisplayName = Path.GetFileName(path);
        ArchiveOrigin = null;
        HasExternalChange = false;
        IsMissingOnDisk = false;
        NoteCurrentWriteTime();
        OnSavedCore();
        RaiseAll(nameof(IsFromArchive), nameof(OriginText), nameof(TabToolTip));
    }

    /// <summary>Marks the history saved (the generic part).</summary>
    protected abstract void OnSavedCore();

    /// <summary>Replaces the document's content with bytes read from disk (Reload), as a clean state.</summary>
    protected abstract void LoadBytes(byte[] bytes, bool keepDirty);

    /// <summary>Puts recovered bytes in as an unsaved change (recovery, reopen closed tab).</summary>
    public void RestoreBytes(byte[] bytes) => LoadBytes(bytes, keepDirty: true);

    /// <summary>
    /// Marks a document made in memory (a retarget result, an import) as never saved: it stays dirty
    /// (the tab shows a star, closing asks) until it is saved, even after undoing back to its first state.
    /// </summary>
    public abstract void MarkAsNew();

    // ── Shared parts ─────────────────────────────────────────────────────────

    /// <summary>The bone selection (viewport, Problems panel, structure tree, later the timeline).</summary>
    public BoneSelection Selection { get; }

    /// <summary>What the viewport shows.</summary>
    public SceneViewModel Scene { get; }

    /// <summary>The shared viewport display toggles (the viewport toolbar binds here).</summary>
    public ViewportDisplaySettings Display => Shell.Display;

    /// <summary>The transport.</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>The Problems panel's view-model for this document.</summary>
    public ProblemsViewModel Problems { get; }

    /// <summary>The inspector's tabs (Clip / Structure now; Bone and Key in phase 5).</summary>
    public ObservableCollection<InspectorTab> InspectorTabs { get; } = [];

    /// <summary>The inspector tab in front.</summary>
    public InspectorTab? SelectedInspectorTab
    {
        get => _selectedInspectorTab;
        set => Set(ref _selectedInspectorTab, value);
    }

    /// <summary>Current problems, errors first.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    public int ErrorCount { get; private set; }

    public int WarningCount { get; private set; }

    public int InfoCount { get; private set; }

    /// <summary>The bone count shown in the status bar.</summary>
    public abstract string StatusBonesText { get; }

    /// <summary>The duration shown in the status bar.</summary>
    public abstract string StatusDurationText { get; }

    /// <summary>A transient message (an edit the Core refused, a reload) shown in the status bar.</summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => Set(ref _statusMessage, value);
    }

    /// <summary>Shows <paramref name="message"/> in the status bar for a few seconds.</summary>
    public void ShowStatus(string message)
    {
        StatusMessage = message;
        _statusTimer ??= new DispatcherTimer(DispatcherPriority.Background, Shell.Dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick -= OnStatusTimer;
        _statusTimer.Tick += OnStatusTimer;
        _statusTimer.Stop();
        _statusTimer.Start();
        Shell.OnDocumentStatusChanged(this);
    }

    private void OnStatusTimer(object? sender, EventArgs e)
    {
        _statusTimer?.Stop();
        StatusMessage = null;
        Shell.OnDocumentStatusChanged(this);
    }

    /// <summary>Replaces the diagnostics and refreshes the panel and status bar.</summary>
    protected void SetDiagnostics(IReadOnlyList<Diagnostic> diagnostics)
    {
        _diagnostics = diagnostics;
        ErrorCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);
        WarningCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);
        InfoCount = diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);
        RaiseAll(nameof(Diagnostics), nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount), nameof(StatusItems));
        Problems.Refresh();
        OnDiagnosticsChanged();
        Shell.OnDocumentDiagnosticsChanged(this);
    }

    /// <summary>Called after the diagnostics change (inspector validation lines refresh here).</summary>
    protected virtual void OnDiagnosticsChanged() { }

    /// <summary>Applies a quick fix: pure edits go through the document's single edit path.</summary>
    public abstract void ApplyQuickFix(QuickFix fix, Diagnostic diagnostic);

    /// <summary>True when the quick fix can be carried out from this document now.</summary>
    public abstract bool CanApplyQuickFix(QuickFix fix);

    /// <summary>Selects what a diagnostic points at (a field, a bone, a key's time, a mesh node).</summary>
    public abstract void Reveal(DiagnosticLocation location);

    /// <summary>Human-readable location for the Problems panel.</summary>
    public abstract string DescribeLocation(DiagnosticLocation location);

    /// <summary>Called by the shell after settings change (resolver, library): rebuilds what depends on them.</summary>
    public virtual void OnAssetsChanged() { }

    /// <summary>Called by the shell when the library snapshot or the table index changes.</summary>
    public virtual void OnLibraryChanged() { }

    /// <summary>The resolver for this document (its own folder first).</summary>
    public AssetResolver Resolver => Shell.Assets.ResolverFor(Folder);

    /// <summary>The document's identity for the lint context (file path, or archive path|entry).</summary>
    public string? LintDocumentPath => _filePath
        ?? (ArchiveOrigin is { } a ? ClipLintContextBuilder.ArchivedDocumentPath(a) : null);

    // ── External changes ─────────────────────────────────────────────────────

    /// <summary>True while the "changed on disk" bar is showing.</summary>
    public bool HasExternalChange
    {
        get => _hasExternalChange;
        private set => Set(ref _hasExternalChange, value);
    }

    /// <summary>True while the "deleted or renamed" bar is showing.</summary>
    public bool IsMissingOnDisk
    {
        get => _isMissingOnDisk;
        private set => Set(ref _isMissingOnDisk, value);
    }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand KeepMineCommand { get; }

    public RelayCommand DismissMissingCommand { get; }

    /// <summary>Stops the file watcher reporting the app's own save.</summary>
    public void SuspendFileWatch(bool suspend) => _watcher.IsSuspended = suspend;

    /// <summary>Recovered or reopened work: the bar must not offer to throw it away for the disk copy.</summary>
    public void NoteDiskIsNewer() => HasExternalChange = true;

    private void WatchFile()
    {
        if (_filePath is null) _watcher.Watch(null);
        else _watcher.Watch(Path.GetDirectoryName(_filePath), Path.GetFileName(_filePath));
        NoteCurrentWriteTime();
    }

    /// <summary>
    /// Remembers the file's write time, so a watcher event that does not change it (an attribute
    /// change, a virus scanner opening the file, our own save arriving late) is not taken for an edit.
    /// </summary>
    private void NoteCurrentWriteTime()
    {
        try
        {
            if (_filePath is not null && File.Exists(_filePath)) _lastSeenWrite = File.GetLastWriteTimeUtc(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private DateTime _lastSeenWrite;

    private void OnFileChangedOnDisk()
    {
        if (_filePath is null) return;
        bool exists;
        DateTime write = default;
        try
        {
            exists = File.Exists(_filePath);
            if (exists) write = File.GetLastWriteTimeUtc(_filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (!exists)
        {
            if (!IsMissingOnDisk)
            {
                IsMissingOnDisk = true;
                // The tab now holds the only copy: it must not close (or exit) without asking.
                MarkAsNew();
            }
            return;
        }
        IsMissingOnDisk = false;
        if (write == _lastSeenWrite) return;
        // While a dialog is open (a save prompt, a tool) the change waits; the watcher's next event or the
        // dialog's end brings it back here.
        if (ModalScope.IsOpen)
        {
            _deferredCheck ??= new DispatcherTimer(DispatcherPriority.Background, Shell.Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
            _deferredCheck.Tick -= OnDeferredCheck;
            _deferredCheck.Tick += OnDeferredCheck;
            _deferredCheck.Start();
            return;
        }
        byte[] disk;
        try
        {
            disk = Cairn.Workspace.AtomicFile.ReadAllBytes(_filePath);
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            // Still being written or locked by the program that changed it: the next event tries again.
            return;
        }
        _lastSeenWrite = write;
        // Touched but not changed (a sync tool, a checkout of the same content): nothing to do, and the
        // undo/redo history stays.
        if (MatchesSaved(disk)) return;
        if (IsDirty)
        {
            HasExternalChange = true;
            return;
        }
        // Nothing here that the disk copy would lose: take it, and say so. A copy that cannot be read is
        // not forced on the tab; it keeps the last good version, marked unsaved.
        try
        {
            LoadBytes(disk, keepDirty: false);
            HasExternalChange = false;
            ShowStatus($"{DisplayName} changed on disk and was reloaded.");
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            MarkAsNew();
            ShowStatus($"{DisplayName} changed on disk but the new copy cannot be read ({ex.Message}); this tab keeps the last good version, unsaved.");
        }
    }

    private DispatcherTimer? _deferredCheck;

    private void OnDeferredCheck(object? sender, EventArgs e)
    {
        _deferredCheck?.Stop();
        if (!_disposed) OnFileChangedOnDisk();
    }

    /// <summary>True when <paramref name="bytes"/> are exactly what was last opened or saved.</summary>
    protected abstract bool MatchesSaved(byte[] bytes);

    private void ReloadFromDisk()
    {
        if (_filePath is null) return;
        try
        {
            byte[] bytes = Cairn.Workspace.AtomicFile.ReadAllBytes(_filePath);
            LoadBytes(bytes, keepDirty: false);
            HasExternalChange = false;
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            Shell.Dialogs.ShowError("The file could not be reloaded.", $"'{_filePath}' could not be read.", ex.Message);
        }
    }

    private void KeepMine()
    {
        HasExternalChange = false;
        // The disk no longer holds the saved state: undoing back to it must not make the tab look clean.
        MarkAsNew();
        Shell.OnDocumentDirtyChanged(this);
    }

    /// <summary>Raises the dirty-related properties and tells the shell.</summary>
    protected void NotifyDirtyChanged()
    {
        RaiseAll(nameof(IsDirty), nameof(TabHeader), nameof(CanUndo), nameof(CanRedo), nameof(UndoLabel), nameof(RedoLabel));
        Shell.OnDocumentDirtyChanged(this);
    }

    private bool _disposed;

    public virtual void Dispose()
    {
        _disposed = true;
        _deferredCheck?.Stop();
        Playback.Pause();
        _watcher.Dispose();
        _statusTimer?.Stop();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A document whose state is one immutable snapshot <typeparamref name="T"/> (an <c>RfaClip</c> or a
/// <c>V3dFile</c>) inside a <see cref="History{T}"/>. Every change goes through <see cref="Apply"/>
/// (one labelled undo step) or the <see cref="BeginEdit"/> / <see cref="UpdateEdit"/> /
/// <see cref="CommitEdit"/> trio (a drag or wheel-spin coalesced into one step). Later phases only
/// write the Core calls: <c>document.Apply("Trim", c =&gt; ClipEdit.Trim(c, a, b))</c>.
/// </summary>
public abstract class DocumentViewModel<T> : DocumentViewModel where T : class
{
    private T? _editBase;

    protected DocumentViewModel(RfaWorkspace shell, T initial, string displayName, string? filePath, AssetLocation? archiveOrigin)
        : base(shell, displayName, filePath, archiveOrigin)
    {
        History = new History<T>(initial);
        History.Changed += OnHistoryChanged;
        _lastSnapshot = initial;
        SavedSnapshot = initial;
    }

    /// <summary>The snapshot as last opened, saved or reloaded (what an inspector row's reset goes back to).</summary>
    public T SavedSnapshot { get; private set; }

    /// <summary>The undo history.</summary>
    public History<T> History { get; }

    /// <summary>The current snapshot (the live value during a drag).</summary>
    public T Current => History.Current;

    private T _lastSnapshot;

    public override bool IsDirty => History.IsDirty;

    public override bool CanUndo => !IsReadOnly && History.CanUndo;

    public override bool CanRedo => !IsReadOnly && History.CanRedo;

    public override string? UndoLabel => History.UndoLabel;

    public override void MarkAsNew() => History.MarkUnsaved();

    public override string? RedoLabel => History.RedoLabel;

    public override void Undo()
    {
        if (CanUndo) History.Undo();
    }

    public override void Redo()
    {
        if (CanRedo) History.Redo();
    }

    /// <summary>
    /// THE edit path: applies a pure Core function to the current snapshot as one undo step labelled
    /// <paramref name="label"/>. A function that returns the same instance adds no step. A function
    /// that throws <see cref="ArgumentException"/> (the Core's way of refusing an inconsistent edit) or
    /// <see cref="InvalidOperationException"/> leaves the document untouched and the reason in the status
    /// bar. Returns true when the snapshot changed.
    /// </summary>
    public bool Apply(string label, Func<T, T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (IsReadOnly)
        {
            ShowStatus($"{DisplayName} is read-only.");
            return false;
        }
        if (History.IsCoalescing) CommitEdit();
        T next;
        try
        {
            next = edit(Current);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowStatus($"{label}: {UserMessage(ex)}");
            return false;
        }
        if (ReferenceEquals(next, Current)) return false;
        History.Push(next, label);
        return true;
    }

    /// <summary>Starts a coalesced edit (a drag). <see cref="UpdateEdit"/> then recomputes from the pre-drag snapshot.</summary>
    public void BeginEdit(string label)
    {
        if (IsReadOnly) return;
        if (History.IsCoalescing) CommitEdit();
        _editBase = Current;
        History.BeginCoalesce(label);
    }

    /// <summary>Shows <paramref name="edit"/> of the pre-drag snapshot as the live value (no undo step yet).</summary>
    public void UpdateEdit(Func<T, T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (!History.IsCoalescing || _editBase is null) return;
        try
        {
            History.UpdateCoalesce(edit(_editBase));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            ShowStatus(UserMessage(ex));
        }
    }

    /// <summary>Ends a coalesced edit as one undo step (none when nothing changed).</summary>
    public void CommitEdit()
    {
        _editBase = null;
        History.CommitCoalesce();
    }

    /// <summary>Abandons a coalesced edit, restoring the pre-drag snapshot.</summary>
    public void CancelEdit()
    {
        _editBase = null;
        History.CancelCoalesce();
    }

    /// <summary>An exception's message without the " (Parameter 'x')" suffix ArgumentException appends.</summary>
    protected static string UserMessage(Exception ex)
    {
        string message = ex.Message;
        int at = ex is ArgumentException { ParamName: { } p } ? message.LastIndexOf($" (Parameter '{p}')", StringComparison.Ordinal) : -1;
        return at > 0 ? message[..at] : message;
    }

    /// <summary>True while a coalesced edit is in progress.</summary>
    public bool IsEditing => History.IsCoalescing;

    private void OnHistoryChanged(object? sender, EventArgs e)
    {
        var current = History.Current;
        if (!ReferenceEquals(current, _lastSnapshot))
        {
            var previous = _lastSnapshot;
            _lastSnapshot = current;
            OnSnapshotChanged(previous, current);
        }
        // Undo/redo labels change on every step even when the dirty flag does not, so always notify.
        NotifyDirtyChanged();
        Shell.Record(this);
    }

    /// <summary>Called on the UI thread after the snapshot changes (edit, undo, redo, reload): relint and refresh views.</summary>
    protected abstract void OnSnapshotChanged(T previous, T current);

    /// <summary>Parses file bytes into a snapshot.</summary>
    protected abstract T Parse(byte[] bytes, string name);

    /// <summary>Serialises a snapshot.</summary>
    protected abstract byte[] Write(T snapshot);

    public override byte[] Serialize() => Write(Current);

    protected override bool MatchesSaved(byte[] bytes)
    {
        try
        {
            return Write(SavedSnapshot).AsSpan().SequenceEqual(bytes);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public override Func<byte[]> CaptureSerializer()
    {
        var snapshot = Current;
        return () => Write(snapshot);
    }

    protected override void OnSavedCore()
    {
        History.MarkSaved();
        SavedSnapshot = Current;
        OnSnapshotChanged(Current, Current);
    }

    protected override void LoadBytes(byte[] bytes, bool keepDirty)
    {
        var snapshot = Parse(bytes, DisplayName);
        if (keepDirty)
        {
            History.Push(snapshot, "Restore recovered changes");
        }
        else
        {
            SavedSnapshot = snapshot;
            History.Reset(snapshot);
        }
    }
}
