namespace Cairn.Workspace;

/// <summary>
/// Snapshot undo/redo for an immutable document (an <c>RfaClip</c> or a <c>V3dFile</c>): every edit
/// pushes the whole next snapshot with a label, so undo is just stepping back through references.
/// Tracks the saved point for the dirty flag and coalesces drags into one step.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The history is a line of snapshots with a cursor. <see cref="Push"/> drops everything after
/// the cursor (the redo branch) and appends; <see cref="Undo"/>/<see cref="Redo"/> move the cursor.</item>
/// <item>Dirty tracking: <see cref="MarkSaved"/> remembers the snapshot at the cursor; the document is
/// dirty whenever the cursor is on any other snapshot, so undoing back to the saved snapshot makes it
/// clean again. If the saved snapshot leaves the history (its redo branch is discarded by a push, or
/// the depth cap trims it) the document stays dirty until the next <see cref="MarkSaved"/>.</item>
/// <item>Coalescing (a gizmo or slider drag): <see cref="BeginCoalesce"/>, any number of
/// <see cref="UpdateCoalesce"/> calls (each makes <see cref="Current"/> the live value without adding
/// a step), then <see cref="CommitCoalesce"/> (one step from the pre-drag snapshot to the last value)
/// or <see cref="CancelCoalesce"/> (back to the pre-drag snapshot, no step). <see cref="Push"/>,
/// <see cref="Undo"/>, <see cref="Redo"/> and <see cref="BeginCoalesce"/> during a drag commit it first;
/// <see cref="Reset"/> discards it.</item>
/// </list>
/// Not thread-safe: use it from one thread (the UI thread). <see cref="Changed"/> is raised
/// synchronously on that thread.
/// </remarks>
/// <typeparam name="T">The immutable snapshot type. Snapshots are compared by reference only.</typeparam>
public sealed class History<T> where T : class
{
    private readonly List<Entry> _entries = [];
    private int _index;
    private long _nextId;
    private long _savedId;
    private Coalesce? _coalesce;

    /// <summary>Starts a history at <paramref name="initial"/>, which counts as saved (not dirty).</summary>
    /// <param name="initial">The opening snapshot.</param>
    /// <param name="maxDepth">The most undo steps kept (at least 1); older steps are trimmed first.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDepth"/> is less than 1.</exception>
    public History(T initial, int maxDepth = 200)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (maxDepth < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDepth), $"The undo depth must be at least 1; got {maxDepth}.");
        MaxDepth = maxDepth;
        ResetCore(initial);
    }

    /// <summary>Raised after every change of <see cref="Current"/>, the dirty state, or the undo/redo steps.</summary>
    public event EventHandler? Changed;

    /// <summary>The most undo steps kept.</summary>
    public int MaxDepth { get; }

    /// <summary>The current snapshot; during a coalesce, the live (uncommitted) value.</summary>
    public T Current => _coalesce?.Live ?? _entries[_index].State;

    /// <summary>True when there is a step to undo (a coalesce in progress counts if it changed anything).</summary>
    public bool CanUndo => _index > 0 || CoalesceChanged;

    /// <summary>True when there is a step to redo (never during a coalesce that changed something, since committing it discards the redo branch).</summary>
    public bool CanRedo => _index < _entries.Count - 1 && !CoalesceChanged;

    /// <summary>The label of the step <see cref="Undo"/> would revert, or null.</summary>
    public string? UndoLabel => CoalesceChanged ? _coalesce!.Label : _index > 0 ? _entries[_index].Label : null;

    /// <summary>The label of the step <see cref="Redo"/> would re-apply, or null.</summary>
    public string? RedoLabel => CanRedo ? _entries[_index + 1].Label : null;

    /// <summary>Labels of every undoable step, most recent first.</summary>
    public IReadOnlyList<string> UndoLabels
    {
        get
        {
            var list = new List<string>();
            if (CoalesceChanged) list.Add(_coalesce!.Label);
            for (int i = _index; i > 0; i--) list.Add(_entries[i].Label);
            return list;
        }
    }

    /// <summary>Labels of every redoable step, the next one first.</summary>
    public IReadOnlyList<string> RedoLabels
    {
        get
        {
            var list = new List<string>();
            if (CoalesceChanged) return list;
            for (int i = _index + 1; i < _entries.Count; i++) list.Add(_entries[i].Label);
            return list;
        }
    }

    /// <summary>True when <see cref="Current"/> is not the snapshot last marked saved.</summary>
    public bool IsDirty => CoalesceChanged || _entries[_index].Id != _savedId;

    /// <summary>True between <see cref="BeginCoalesce"/> and the matching commit or cancel.</summary>
    public bool IsCoalescing => _coalesce is not null;

    /// <summary>
    /// Makes <paramref name="next"/> the current snapshot as one undo step labelled
    /// <paramref name="label"/>. Discards the redo branch. Pushing the reference that is already
    /// current does nothing. A coalesce in progress is committed first.
    /// </summary>
    public void Push(T next, string label)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(label);
        bool changed = CommitCore();
        if (!ReferenceEquals(next, _entries[_index].State))
        {
            Append(next, label);
            changed = true;
        }
        if (changed) OnChanged();
    }

    /// <summary>Steps back one snapshot (committing a coalesce first). Returns false when there is nothing to undo.</summary>
    public bool Undo()
    {
        bool committed = CommitCore();
        bool moved = false;
        if (_index > 0)
        {
            _index--;
            moved = true;
        }
        if (committed || moved) OnChanged();
        return moved;
    }

    /// <summary>Steps forward one snapshot (committing a coalesce first, which discards the redo branch if it changed anything). Returns false when there is nothing to redo.</summary>
    public bool Redo()
    {
        bool committed = CommitCore();
        bool moved = false;
        if (_index < _entries.Count - 1)
        {
            _index++;
            moved = true;
        }
        if (committed || moved) OnChanged();
        return moved;
    }

    /// <summary>Records that <see cref="Current"/> has been saved (commits a coalesce in progress first).</summary>
    public void MarkSaved()
    {
        bool changed = CommitCore();
        long id = _entries[_index].Id;
        if (_savedId != id)
        {
            _savedId = id;
            changed = true;
        }
        if (changed) OnChanged();
    }

    /// <summary>
    /// Records that <see cref="Current"/> has never been saved (a document made in memory: a retarget
    /// result, an import): it is dirty until <see cref="MarkSaved"/>, even after undoing back to it.
    /// </summary>
    public void MarkUnsaved()
    {
        bool changed = CommitCore();
        if (_savedId != -1)
        {
            _savedId = -1;
            changed = true;
        }
        if (changed) OnChanged();
    }

    /// <summary>
    /// Starts a drag that will become one undo step labelled <paramref name="label"/>. A coalesce
    /// already in progress is committed first.
    /// </summary>
    public void BeginCoalesce(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        bool changed = CommitCore();
        _coalesce = new Coalesce(label, _entries[_index].State);
        if (changed) OnChanged();
    }

    /// <summary>Shows <paramref name="next"/> as the live value of the drag (no undo step yet).</summary>
    /// <exception cref="InvalidOperationException">No coalesce is in progress.</exception>
    public void UpdateCoalesce(T next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var c = _coalesce ?? throw new InvalidOperationException("No drag is in progress; call BeginCoalesce before UpdateCoalesce.");
        if (ReferenceEquals(c.Live, next)) return;
        c.Live = next;
        OnChanged();
    }

    /// <summary>
    /// Ends the drag as one undo step from the pre-drag snapshot to the last live value. Adds no
    /// step when the live value is the pre-drag snapshot. Does nothing when no coalesce is in progress.
    /// </summary>
    public void CommitCoalesce()
    {
        if (CommitCore()) OnChanged();
    }

    /// <summary>Ends the drag without a step, restoring the pre-drag snapshot. Does nothing when no coalesce is in progress.</summary>
    public void CancelCoalesce()
    {
        var c = _coalesce;
        if (c is null) return;
        _coalesce = null;
        if (!ReferenceEquals(c.Live, c.Base)) OnChanged();
    }

    /// <summary>Clears every step and any coalesce; <paramref name="initial"/> becomes current and saved (not dirty).</summary>
    public void Reset(T initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        ResetCore(initial);
        OnChanged();
    }

    private bool CoalesceChanged => _coalesce is { } c && !ReferenceEquals(c.Live, c.Base);

    private void ResetCore(T initial)
    {
        _entries.Clear();
        _coalesce = null;
        _entries.Add(new Entry(initial, "", _nextId++));
        _index = 0;
        _savedId = _entries[0].Id;
    }

    // Ends a coalesce in progress; true when that changed anything observable.
    private bool CommitCore()
    {
        var c = _coalesce;
        if (c is null) return false;
        _coalesce = null;
        if (ReferenceEquals(c.Live, c.Base)) return false;
        Append(c.Live, c.Label);
        return true;
    }

    private void Append(T state, string label)
    {
        if (_index < _entries.Count - 1) _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        _entries.Add(new Entry(state, label, _nextId++));
        _index = _entries.Count - 1;
        int excess = _entries.Count - 1 - MaxDepth;
        if (excess > 0)
        {
            _entries.RemoveRange(0, excess);
            _index -= excess;
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record Entry(T State, string Label, long Id);

    private sealed class Coalesce(string label, T baseState)
    {
        public string Label { get; } = label;
        public T Base { get; } = baseState;
        public T Live { get; set; } = baseState;
    }
}
