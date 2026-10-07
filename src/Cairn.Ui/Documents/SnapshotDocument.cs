using Cairn.Ui.Modules;
using Cairn.Workspace;

namespace Cairn.Ui.Documents;

/// <summary>
/// A document whose content is an immutable snapshot <typeparamref name="T"/> kept in a <see cref="History{T}"/>.
/// Edits replace the snapshot (<see cref="Apply"/>, or <see cref="BeginEdit"/> ... <see cref="CommitEdit"/> for drags).
/// </summary>
public abstract class SnapshotDocument<T> : DocumentBase where T : class
{
    protected SnapshotDocument(IShellContext shell, IDocumentKind kind, T initial, string displayName, string? filePath, string? originText = null)
        : base(shell, kind, displayName, filePath, originText)
    {
        History = new History<T>(initial);
        SavedSnapshot = initial;
        History.Changed += (_, _) => { NotifyDirtyChanged(); OnSnapshotChanged(); };
    }

    /// <summary>The snapshot at the last save or load.</summary>
    public T SavedSnapshot { get; private set; }
    public History<T> History { get; }
    public T Current => History.Current;

    public override bool IsDirty => History.IsDirty;
    public override bool CanUndo => !IsReadOnly && History.CanUndo;
    public override bool CanRedo => !IsReadOnly && History.CanRedo;
    public override string? UndoLabel => History.UndoLabel;
    public override string? RedoLabel => History.RedoLabel;
    public override void Undo() { if (CanUndo) History.Undo(); }
    public override void Redo() { if (CanRedo) History.Redo(); }
    /// <summary>Marks a fresh or restored document as unsaved.</summary>
    public void MarkAsNew() => History.MarkUnsaved();

    /// <summary>Parses file bytes into a snapshot. Throws on malformed input.</summary>
    protected abstract T Parse(byte[] bytes, string name);
    /// <summary>Serialises a snapshot to file bytes.</summary>
    protected abstract byte[] Write(T snapshot);
    /// <summary>Called after every snapshot change (edit, undo, redo, reload).</summary>
    protected virtual void OnSnapshotChanged() { }

    /// <summary>The current content as file bytes.</summary>
    public byte[] Serialize() => Write(Current);
    /// <summary>Captures the snapshot now; the returned function serialises it later (off the UI thread).</summary>
    public Func<byte[]> CaptureSerializer()
    {
        var snapshot = Current;
        return () => Write(snapshot);
    }
    /// <summary>Replaces the content with recovered bytes, keeping it dirty.</summary>
    public void RestoreBytes(byte[] bytes) => LoadBytes(bytes, keepDirty: true);

    /// <summary>Applies one undoable edit. False when read-only or the edit changed nothing.</summary>
    public bool Apply(string label, Func<T, T> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (IsReadOnly) return false;
        var next = edit(Current);
        if (ReferenceEquals(next, Current)) return false;
        History.Push(next, label);
        return true;
    }

    /// <summary>Starts a coalesced edit (a drag); <see cref="UpdateEdit"/> as it moves.</summary>
    public void BeginEdit(string label) { if (!IsReadOnly) History.BeginCoalesce(label); }
    public void UpdateEdit(Func<T, T> edit) { if (History.IsCoalescing) History.UpdateCoalesce(edit(Current)); }
    public void CommitEdit() { if (History.IsCoalescing) History.CommitCoalesce(); }
    public void CancelEdit() { if (History.IsCoalescing) History.CancelCoalesce(); }

    public override void CommitPendingEdits() => CommitEdit();

    public override byte[]? CaptureRecovery() => IsDirty ? Serialize() : null;

    /// <summary>Writes atomically to <paramref name="path"/> and moves the saved point.</summary>
    public override void SaveTo(string path)
    {
        CommitPendingEdits();
        byte[] bytes = Serialize();
        SuspendFileWatch(true);
        try { AtomicFile.WriteAllBytes(path, bytes); }
        finally { SuspendFileWatch(false); }
        MarkSaved(path);
    }

    /// <summary>Records <paramref name="path"/> as the file and the current snapshot as saved.</summary>
    public virtual void MarkSaved(string path)
    {
        FilePath = path;
        SavedSnapshot = Current;
        History.MarkSaved();
        HasExternalChange = false;
        IsMissingOnDisk = false;
        OnSavedCore();
        NotifyDirtyChanged();
    }

    /// <summary>Hook after a save.</summary>
    protected virtual void OnSavedCore() { }

    protected override void LoadBytes(byte[] bytes, bool keepDirty)
    {
        var snapshot = Parse(bytes, DisplayName);
        if (keepDirty) { History.Push(snapshot, "Restore"); return; }
        if (IsDirty)
        {
            // Unsaved edits stay reachable: the disk content becomes one undo step and the new saved point.
            SavedSnapshot = snapshot;
            History.Push(snapshot, "Reload from disk");
            History.MarkSaved();
            return;
        }
        History.Reset(snapshot);
        SavedSnapshot = snapshot;
        NotifyDirtyChanged();
        OnSnapshotChanged();
    }

    protected override bool MatchesSaved(byte[] bytes) => bytes.AsSpan().SequenceEqual(Write(SavedSnapshot));
}
