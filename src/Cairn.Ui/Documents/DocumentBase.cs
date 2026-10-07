using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Ui.Documents;

/// <summary>An open document as the shell sees it (one tab).</summary>
public interface IDocument : INotifyPropertyChanged, IDisposable
{
    /// <summary>Stable id; the recovery key.</summary>
    string Id { get; }
    /// <summary>The kind that opened it.</summary>
    IDocumentKind Kind { get; }
    /// <summary>File name or "Untitled".</summary>
    string DisplayName { get; }
    /// <summary>Null for new, archive-origin and restored-unsaved documents.</summary>
    string? FilePath { get; }
    /// <summary>Display name plus dirty marker.</summary>
    string TabHeader { get; }
    /// <summary>Where it came from.</summary>
    string? TabToolTip { get; }
    /// <summary>True = Save always becomes Save As.</summary>
    bool IsReadOnly { get; }
    /// <summary>True when it differs from the saved point.</summary>
    bool IsDirty { get; }
    /// <summary>True when <see cref="Undo"/> would do something.</summary>
    bool CanUndo { get; }
    /// <summary>True when <see cref="Redo"/> would do something.</summary>
    bool CanRedo { get; }
    /// <summary>Label of the edit Undo reverts, or null.</summary>
    string? UndoLabel { get; }
    /// <summary>Label of the edit Redo reapplies, or null.</summary>
    string? RedoLabel { get; }
    /// <summary>Reverts the last edit (document-owned history).</summary>
    void Undo();
    /// <summary>Reapplies the last undone edit.</summary>
    void Redo();
    /// <summary>Flushes half-typed fields and drags before save, close and snapshots.</summary>
    void CommitPendingEdits();
    /// <summary>Last chance to cancel a save (e.g. "save with errors?"). False cancels.</summary>
    bool ConfirmSave();
    /// <summary>
    /// Asked by the shell's Save (Ctrl+S, File > Save, Save All) only when the document has a
    /// <see cref="FilePath"/> and is not <see cref="IsReadOnly"/>, before <see cref="ConfirmSave"/> and the write over
    /// that path: true turns this save into Save As, false saves in place, null cancels. Never asked by Save As, nor when
    /// Save already becomes Save As (no path, e.g. a new document or one opened from an archive, or read-only), so a
    /// document is never asked twice. The default (false) suits documents whose in-place save loses nothing; override
    /// it when overwriting would drop data the original had (VFX: the first save after converting an older version).
    /// The implementation may show a dialog through the shell's dialog service and should remember a final answer.
    /// </summary>
    bool? ChooseSaveAsInstead() => false;
    /// <summary>
    /// Atomic write; updates <see cref="FilePath"/> and the saved point. Throws on failure. A save the user cancelled
    /// (a long save with a Cancel button) throws <see cref="OperationCanceledException"/> with the file on disk
    /// unchanged: the shell then ends the save quietly (status message, no error dialog, document stays dirty).
    /// </summary>
    void SaveTo(string path);
    /// <summary>Current content for the recovery store, or null when not worth saving.</summary>
    byte[]? CaptureRecovery();
    /// <summary>Right side of the status bar (raise PropertyChanged to refresh).</summary>
    IReadOnlyList<StatusItem> StatusItems { get; }
    /// <summary>The document's view, created lazily once and hosted in the shell's document area.</summary>
    FrameworkElement View { get; }
    /// <summary>Called when its tab becomes active.</summary>
    void OnActivated();
    /// <summary>Called when another tab becomes active (pause playback, ...).</summary>
    void OnDeactivated();
}

/// <summary>One status-bar entry.</summary>
public sealed record StatusItem(string Text, string? ToolTip = null, ICommand? Command = null);

/// <summary>
/// The generic half of a document: identity, path and archive origin, tab header, status message, and a
/// file watcher offering Reload / Keep mine / missing-on-disk. Content, history and saving are the subclass's.
/// </summary>
public abstract class DocumentBase : ObservableObject, IDocument
{
    private readonly FileChangeWatcher _watcher;
    private string? _filePath;
    private string _displayName;
    private string _statusMessage = string.Empty;
    private bool _hasExternalChange, _isMissingOnDisk;
    private FrameworkElement? _view;

    /// <param name="shell">The shell.</param>
    /// <param name="kind">The kind that opened it.</param>
    /// <param name="displayName">Name shown on the tab.</param>
    /// <param name="filePath">The file, or null.</param>
    /// <param name="originText">Where an archive or restored document came from, or null.</param>
    protected DocumentBase(IShellContext shell, IDocumentKind kind, string displayName, string? filePath, string? originText = null)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Kind = kind ?? throw new ArgumentNullException(nameof(kind));
        _displayName = displayName;
        _filePath = filePath;
        ArchiveOriginText = originText;
        _watcher = new FileChangeWatcher(shell.Dispatcher);
        _watcher.Changed += (_, _) => OnDiskChanged();
        ReloadCommand = new RelayCommand(Reload, () => _filePath is not null && File.Exists(_filePath));
        KeepMineCommand = new RelayCommand(() => HasExternalChange = false);
        DismissMissingCommand = new RelayCommand(() => IsMissingOnDisk = false);
        Watch();
    }

    public IShellContext Shell { get; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IDocumentKind Kind { get; }
    /// <summary>Where a document without a path came from (archive entry, restored snapshot), or null.</summary>
    public string? ArchiveOriginText { get; protected set; }
    public bool IsFromArchive => _filePath is null && ArchiveOriginText is not null;
    public string? Folder => _filePath is null ? null : Path.GetDirectoryName(_filePath);
    /// <summary>A resolver that also searches the document's folder.</summary>
    public AssetResolver Resolver => Shell.Assets.ResolverFor(Folder);

    public string DisplayName
    {
        get => _displayName;
        protected set { if (Set(ref _displayName, value)) Raise(nameof(TabHeader)); }
    }

    public string? FilePath
    {
        get => _filePath;
        protected set
        {
            if (!Set(ref _filePath, value)) return;
            if (value is not null) DisplayName = Path.GetFileName(value);
            Raise(nameof(Folder)); Raise(nameof(IsFromArchive)); Raise(nameof(OriginText)); Raise(nameof(TabToolTip));
            Watch();
        }
    }

    public virtual bool IsReadOnly => false;
    public string TabHeader => DisplayName + (IsDirty ? " *" : string.Empty);
    public string? TabToolTip => OriginText + (IsReadOnly ? "\nRead-only." : string.Empty);
    public string OriginText => _filePath ?? ArchiveOriginText ?? "Not saved yet.";

    /// <summary>The last transient message for the status bar.</summary>
    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public void ShowStatus(string message)
    {
        StatusMessage = message;
        Shell.ShowStatus(message);
    }

    /// <summary>True when the file changed on disk since it was loaded or saved.</summary>
    public bool HasExternalChange { get => _hasExternalChange; protected set => Set(ref _hasExternalChange, value); }
    /// <summary>True when the file was deleted or renamed on disk.</summary>
    public bool IsMissingOnDisk { get => _isMissingOnDisk; protected set => Set(ref _isMissingOnDisk, value); }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand KeepMineCommand { get; }
    public RelayCommand DismissMissingCommand { get; }
    /// <summary>Ignores the document's own writes.</summary>
    public void SuspendFileWatch(bool suspend) => _watcher.IsSuspended = suspend;
    public void NoteDiskIsNewer() => HasExternalChange = true;

    public abstract bool IsDirty { get; }
    public abstract bool CanUndo { get; }
    public abstract bool CanRedo { get; }
    public abstract string? UndoLabel { get; }
    public abstract string? RedoLabel { get; }
    public abstract void Undo();
    public abstract void Redo();
    public abstract void SaveTo(string path);
    public abstract byte[]? CaptureRecovery();
    /// <summary>Replaces the content with the file's bytes (Reload).</summary>
    protected abstract void LoadBytes(byte[] bytes, bool keepDirty);
    /// <summary>True when <paramref name="bytes"/> equal the saved content (a touch, not a change).</summary>
    protected abstract bool MatchesSaved(byte[] bytes);
    /// <summary>Creates the view once, on first use of <see cref="View"/>.</summary>
    protected abstract FrameworkElement CreateView();

    public virtual void CommitPendingEdits() { }
    public virtual bool ConfirmSave() => true;
    public virtual bool? ChooseSaveAsInstead() => false;
    public virtual IReadOnlyList<StatusItem> StatusItems => [];
    public FrameworkElement View => _view ??= CreateView();
    public virtual void OnActivated() { }
    public virtual void OnDeactivated() { }

    /// <summary>Raises the dirty-dependent properties and refreshes the shell's commands.</summary>
    protected void NotifyDirtyChanged()
    {
        Raise(nameof(IsDirty)); Raise(nameof(TabHeader));
        Raise(nameof(CanUndo)); Raise(nameof(CanRedo)); Raise(nameof(UndoLabel)); Raise(nameof(RedoLabel));
        Shell.RefreshCommands();
    }

    /// <summary>Re-reads the file from disk, replacing the content (undoable as one step where supported).</summary>
    public void Reload()
    {
        if (_filePath is null) return;
        try
        {
            LoadBytes(AtomicFile.ReadAllBytes(_filePath), keepDirty: false);
            HasExternalChange = false;
            IsMissingOnDisk = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException)
        {
            // The document keeps its content; the banner stays so the user can retry or keep theirs.
            ShowStatus("The file could not be reloaded: " + ex.Message);
        }
    }

    private void Watch()
    {
        if (_filePath is null) { _watcher.Stop(); return; }
        _watcher.Watch(Path.GetDirectoryName(_filePath), Path.GetFileName(_filePath));
    }

    private void OnDiskChanged()
    {
        if (_filePath is null) return;
        if (!File.Exists(_filePath)) { IsMissingOnDisk = true; return; }
        IsMissingOnDisk = false;
        try
        {
            if (!MatchesSaved(AtomicFile.ReadAllBytes(_filePath))) HasExternalChange = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { HasExternalChange = true; }
    }

    public virtual void Dispose()
    {
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
