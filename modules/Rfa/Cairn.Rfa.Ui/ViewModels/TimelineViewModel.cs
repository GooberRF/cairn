using System.Globalization;
using System.Windows;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>One row of the timeline: the summary row (<see cref="Bone"/> = -1) or a bone.</summary>
/// <param name="Bone">The bone index, or -1 for the summary row.</param>
/// <param name="Depth">Indent level in the hierarchy (0 for roots and for the flat fallback).</param>
/// <param name="HasChildren">True when the bone has children (an expand/collapse arrow is drawn).</param>
/// <param name="Name">The label ("ult2-bdbn-hand-l", "Bone 7", "All keys").</param>
public sealed record TimelineRow(int Bone, int Depth, bool HasChildren, string Name)
{
    /// <summary>True for the summary row on top.</summary>
    public bool IsSummary => Bone < 0;
}

/// <summary>How a click combines with the existing key selection.</summary>
public enum SelectMode
{
    /// <summary>Replace the selection.</summary>
    Replace,
    /// <summary>Add to it (Shift).</summary>
    Add,
    /// <summary>Toggle membership (Ctrl).</summary>
    Toggle,
}

/// <summary>
/// The dope sheet's model for one clip document (DESIGN.md "Timeline"): the rows (bones in hierarchy
/// order from the fitting preview skeleton, or index order with "Bone N" names), expand/collapse and
/// filters, the zoom/scroll state, and every key operation — select, move/copy by dragging, scale,
/// delete, cut/copy/paste through <see cref="KeyClipboard"/> JSON on the system clipboard, key at the
/// playhead, start/end handle drags. Every edit goes through the document's edit path (one labelled
/// undo step, drags coalesced) and the key selection lives on the document
/// (<see cref="ClipDocumentViewModel.KeySelection"/>), so the Key inspector and the viewport share it.
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly ClipDocumentViewModel _doc;
    private readonly HashSet<int> _collapsed = [];
    private List<TimelineRow> _allRows = [];
    private List<TimelineRow> _visibleRows = [];
    private string _filter = string.Empty;
    private bool _onlyWithKeys;
    private bool _onlySelected;
    private Dictionary<int, DiagnosticSeverity> _markers = [];
    private Skeleton? _rowSkeleton;
    private int _rowBoneCount = -1;

    // Drag state (relative to the pre-drag snapshot).
    private KeySelection _dragSelection = KeySelection.Empty;
    private int _dragGrabTime;
    private int _dragEdgeTime;
    private bool _dragCopy;
    private DragKind _drag;
    private int _lastDelta = int.MinValue;

    private enum DragKind { None, Move, Scale, StartHandle, EndHandle }

    internal TimelineViewModel(ClipDocumentViewModel document)
    {
        _doc = document ?? throw new ArgumentNullException(nameof(document));
        _doc.KeySelectionChanged += (_, _) => Changed?.Invoke(this, TimelineChange.Selection);
        _doc.PreviewSkeletonChanged += (_, _) => RebuildRows();
        _doc.Selection.Changed += (_, _) =>
        {
            if (_onlySelected) ApplyFilters();
            Changed?.Invoke(this, TimelineChange.BoneSelection);
        };
        _doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentViewModel.Diagnostics)) RebuildMarkers();
        };
        DeleteCommand = new RelayCommand(DeleteSelected, () => !_doc.KeySelection.IsEmpty && !_doc.IsReadOnly);
        CopyCommand = new RelayCommand(() => Copy(), () => !_doc.KeySelection.IsEmpty);
        CutCommand = new RelayCommand(() => Cut(), () => !_doc.KeySelection.IsEmpty && !_doc.IsReadOnly);
        PasteCommand = new RelayCommand(() => Paste(), () => !_doc.IsReadOnly);
        PasteMirroredCommand = new RelayCommand(() => PasteMirrored(), () => !_doc.IsReadOnly);
        KeyAtPlayheadCommand = new RelayCommand(() => KeySelectedBonesAtPlayhead(), () => !_doc.IsReadOnly);
        SelectAllCommand = new RelayCommand(SelectAll);
        SelectNoneCommand = new RelayCommand(() => _doc.SetKeySelection(KeySelection.Empty));
        ScaleCommand = new RelayCommand(p =>
        {
            if (p is double f) ScaleSelected(f);
            else if (double.TryParse(p as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)) ScaleSelected(parsed);
        }, _ => !_doc.KeySelection.IsEmpty && !_doc.IsReadOnly);
        ExpandAllCommand = new RelayCommand(() => { _collapsed.Clear(); ApplyFilters(); });
        CollapseAllCommand = new RelayCommand(() =>
        {
            foreach (var r in _allRows) if (r.HasChildren) _collapsed.Add(r.Bone);
            ApplyFilters();
        });
        FitCommand = new RelayCommand(() => FitRequested?.Invoke(this, EventArgs.Empty));
        RebuildRows();
    }

    /// <summary>The document.</summary>
    public ClipDocumentViewModel Document => _doc;

    /// <summary>The clip the timeline shows and edits (the document's current snapshot).</summary>
    public RfaClip Clip => _doc.Current;

    /// <summary>The shared playhead.</summary>
    public PlaybackViewModel Playback => _doc.Playback;

    /// <summary>The time unit text is shown in.</summary>
    public TimeUnit Unit => _doc.Shell.TimeUnit;

    /// <summary>Raised when something the control draws changed (what, so it can redraw only that).</summary>
    public event EventHandler<TimelineChange>? Changed;

    /// <summary>Raised to ask the control to fit the clip's range into view (Home).</summary>
    public event EventHandler? FitRequested;

    // ── Rows ─────────────────────────────────────────────────────────────────

    /// <summary>Every row (summary first, then bones in hierarchy order), ignoring collapse and filters.</summary>
    public IReadOnlyList<TimelineRow> AllRows => _allRows;

    /// <summary>The rows drawn: the summary row, then the bones that pass collapse and the filters.</summary>
    public IReadOnlyList<TimelineRow> VisibleRows => _visibleRows;

    /// <summary>True when rows are named from the preview mesh (false: "Bone N", index order).</summary>
    public bool HasHierarchy => _rowSkeleton is not null;

    /// <summary>Substring filter on bone names (case-insensitive).</summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (Set(ref _filter, value ?? string.Empty)) ApplyFilters();
        }
    }

    /// <summary>Only bones that have at least one key.</summary>
    public bool OnlyWithKeys
    {
        get => _onlyWithKeys;
        set
        {
            if (Set(ref _onlyWithKeys, value)) ApplyFilters();
        }
    }

    /// <summary>Only the selected bones.</summary>
    public bool OnlySelected
    {
        get => _onlySelected;
        set
        {
            if (Set(ref _onlySelected, value)) ApplyFilters();
        }
    }

    /// <summary>"25 bones · 163 rotation, 61 position keys · 4 selected" for the toolbar.</summary>
    public string SummaryText
    {
        get
        {
            var c = Clip;
            int rot = 0, pos = 0;
            foreach (var b in c.Bones)
            {
                rot += b.RotationKeys.Length;
                pos += b.PositionKeys.Length;
            }
            string text = string.Format(CultureInfo.CurrentCulture, "{0} bones · {1:N0} rotation, {2:N0} position keys", c.BoneCount, rot, pos);
            int sel = _doc.KeySelection.Count;
            if (sel > 0) text += string.Format(CultureInfo.CurrentCulture, " · {0:N0} selected", sel);
            if (!HasHierarchy && c.BoneCount > 0) text += " · no fitting mesh: bones by index";
            return text;
        }
    }

    /// <summary>True when the bone's row is collapsed.</summary>
    public bool IsCollapsed(int bone) => _collapsed.Contains(bone);

    /// <summary>Expands or collapses a bone's children.</summary>
    public void ToggleExpanded(int bone)
    {
        if (!_collapsed.Remove(bone)) _collapsed.Add(bone);
        ApplyFilters();
    }

    /// <summary>
    /// Makes a bone's row visible (a bone picked in the viewport): collapsed ancestors are expanded, then
    /// the control scrolls the row into view. A text filter the user typed is left alone.
    /// </summary>
    public void RevealBone(int bone)
    {
        if (bone < 0) return;
        if (_rowSkeleton?.EffectiveParents is IReadOnlyList<int> parents && bone < parents.Count)
        {
            bool expanded = false;
            for (int p = parents[bone], guard = 0; p >= 0 && guard < parents.Count; p = parents[p], guard++)
                expanded |= _collapsed.Remove(p);
            if (expanded) ApplyFilters();
        }
        Changed?.Invoke(this, TimelineChange.BoneSelection);
    }

    /// <summary>The bone's weight in this clip, or NaN for a bone the clip lacks.</summary>
    public float Weight(int bone) => (uint)bone < (uint)Clip.BoneCount ? Clip.Bones[bone].Weight : float.NaN;

    /// <summary>The worst problem the linter reports for a bone, or null.</summary>
    public DiagnosticSeverity? Marker(int bone) => _markers.TryGetValue(bone, out var s) ? s : null;

    /// <summary>The first problem message for a bone (row tooltip), or null.</summary>
    public string? MarkerText(int bone) =>
        _doc.Diagnostics.FirstOrDefault(d => d.Location.Bone == bone)?.Message;

    /// <summary>Called by the document after its snapshot changed (the rows depend on the bone count).</summary>
    internal void OnClipChanged()
    {
        if (Clip.BoneCount != _rowBoneCount) RebuildRows();
        else if (_onlyWithKeys) ApplyFilters();
        Raise(nameof(SummaryText));
        Changed?.Invoke(this, TimelineChange.Keys);
    }

    /// <summary>Called when the time unit changes (ruler labels).</summary>
    internal void OnUnitChanged() => Changed?.Invoke(this, TimelineChange.Layout);

    private void RebuildRows()
    {
        var clip = Clip;
        var skeleton = _doc.FittingSkeleton;
        _rowSkeleton = skeleton;
        _rowBoneCount = clip.BoneCount;
        var rows = new List<TimelineRow> { new(-1, 0, false, "All keys") };
        if (skeleton is not null)
        {
            var parents = skeleton.EffectiveParents;
            var children = new List<int>[skeleton.Count];
            for (int i = 0; i < children.Length; i++) children[i] = [];
            var roots = new List<int>();
            for (int i = 0; i < skeleton.Count; i++)
            {
                if (parents[i] >= 0) children[parents[i]].Add(i);
                else roots.Add(i);
            }
            void Visit(int bone, int depth)
            {
                rows.Add(new TimelineRow(bone, depth, children[bone].Count > 0, _doc.BoneDisplayName(bone)));
                foreach (int c in children[bone]) Visit(c, depth + 1);
            }
            foreach (int r in roots) Visit(r, 0);
        }
        else
        {
            for (int i = 0; i < clip.BoneCount; i++) rows.Add(new TimelineRow(i, 0, false, $"Bone {i}"));
        }
        _allRows = rows;
        _collapsed.RemoveWhere(b => b >= clip.BoneCount);
        RebuildMarkers(raise: false);
        ApplyFilters();
        Raise(nameof(HasHierarchy));
        Raise(nameof(SummaryText));
    }

    private void ApplyFilters()
    {
        var clip = Clip;
        var parents = _rowSkeleton?.EffectiveParents;
        bool filtering = _filter.Length > 0 || _onlyWithKeys || _onlySelected;
        var visible = new List<TimelineRow>(_allRows.Count);
        foreach (var row in _allRows)
        {
            if (row.IsSummary)
            {
                visible.Add(row);
                continue;
            }
            int b = row.Bone;
            if (_filter.Length > 0 && row.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (_onlyWithKeys && (b >= clip.BoneCount || clip.Bones[b].RotationKeys.Length + clip.Bones[b].PositionKeys.Length == 0)) continue;
            if (_onlySelected && !_doc.Selection.Contains(b)) continue;
            // Collapse hides descendants (a filter shows matches flat, ignoring collapse).
            if (!filtering && parents is not null && IsHiddenByCollapse(b, parents)) continue;
            visible.Add(row);
        }
        _visibleRows = visible;
        Changed?.Invoke(this, TimelineChange.Rows);
    }

    private bool IsHiddenByCollapse(int bone, IReadOnlyList<int> parents)
    {
        for (int p = parents[bone], guard = 0; p >= 0 && guard < parents.Count; p = parents[p], guard++)
        {
            if (_collapsed.Contains(p)) return true;
        }
        return false;
    }

    private void RebuildMarkers(bool raise = true)
    {
        var markers = new Dictionary<int, DiagnosticSeverity>();
        foreach (var d in _doc.Diagnostics)
        {
            if (d.Location.Bone is not int b) continue;
            if (!markers.TryGetValue(b, out var s) || d.Severity > s) markers[b] = d.Severity;
        }
        _markers = markers;
        if (raise) Changed?.Invoke(this, TimelineChange.Rows);
    }

    // ── Key queries ──────────────────────────────────────────────────────────

    /// <summary>Every distinct key time of the clip, sorted (the summary row).</summary>
    public IReadOnlyList<int> AllKeyTimes => Playback.KeyTimes;

    /// <summary>The keys at exactly <paramref name="time"/> on every bone (a summary-row mark).</summary>
    public KeySelection KeysAtTime(int time)
    {
        var clip = Clip;
        var list = new List<KeyRef>();
        for (int b = 0; b < clip.BoneCount; b++)
        {
            var t = clip.Bones[b];
            for (int i = 0; i < t.RotationKeys.Length; i++) if (t.RotationKeys[i].Time == time) list.Add(new KeyRef(b, KeyKind.Rotation, i));
            for (int i = 0; i < t.PositionKeys.Length; i++) if (t.PositionKeys[i].Time == time) list.Add(new KeyRef(b, KeyKind.Position, i));
        }
        return KeySelection.Of(list);
    }

    /// <summary>The keys whose time lies in [from, to] on the given rows (box select).</summary>
    public KeySelection KeysIn(IEnumerable<TimelineRow> rows, int from, int to)
    {
        var bones = new HashSet<int>();
        bool summary = false;
        foreach (var r in rows)
        {
            if (r.IsSummary) summary = true;
            else bones.Add(r.Bone);
        }
        return summary
            ? KeySelection.InTimeRange(Clip, from, to)
            : KeySelection.InTimeRange(Clip, from, to, bones);
    }

    // ── Selection ────────────────────────────────────────────────────────────

    /// <summary>Combines <paramref name="keys"/> with the current selection.</summary>
    public void Select(KeySelection keys, SelectMode mode)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var current = _doc.KeySelection;
        KeySelection next = mode switch
        {
            SelectMode.Add => current.Union(keys),
            SelectMode.Toggle => Toggle(current, keys),
            _ => keys,
        };
        _doc.SetKeySelection(next);
        Raise(nameof(SummaryText));
    }

    private static KeySelection Toggle(KeySelection current, KeySelection keys)
    {
        // Toggling a group: if every key of it is selected, remove them all; otherwise add them all.
        if (keys.Keys.All(current.Contains))
            return KeySelection.Of(current.Keys.Where(k => !keys.Contains(k)));
        return current.Union(keys);
    }

    /// <summary>Selects every key of the clip (Ctrl+A).</summary>
    public void SelectAll() => Select(KeySelection.All(Clip), SelectMode.Replace);

    /// <summary>Selects every key of the given bones (row context menu, Bone inspector).</summary>
    public void SelectBoneKeys(IEnumerable<int> bones, SelectMode mode = SelectMode.Replace) =>
        Select(KeySelection.Bones(Clip, bones), mode);

    /// <summary>Sets the bone selection from a row click (the viewport and inspector follow).</summary>
    public void SelectRow(TimelineRow row, SelectMode mode)
    {
        if (row.IsSummary) return;
        var bones = _doc.Selection;
        switch (mode)
        {
            case SelectMode.Toggle:
                bones.Toggle(row.Bone);
                break;
            case SelectMode.Add when bones.Active >= 0:
                // Shift: the visible rows between the active bone and this one.
                int a = _visibleRows.FindIndex(r => r.Bone == bones.Active), b = _visibleRows.FindIndex(r => r.Bone == row.Bone);
                if (a < 0 || b < 0)
                {
                    bones.Toggle(row.Bone);
                    break;
                }
                var range = _visibleRows.Skip(Math.Min(a, b)).Take(Math.Abs(a - b) + 1).Where(r => !r.IsSummary).Select(r => r.Bone).ToList();
                // Keep the clicked bone last (it becomes the active one).
                range.Remove(row.Bone);
                range.Add(row.Bone);
                bones.Set(bones.Bones.Concat(range));
                break;
            default:
                bones.Select(row.Bone);
                break;
        }
    }

    // ── Edits ────────────────────────────────────────────────────────────────

    public RelayCommand DeleteCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand PasteMirroredCommand { get; }
    public RelayCommand KeyAtPlayheadCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand ScaleCommand { get; }
    public RelayCommand ExpandAllCommand { get; }
    public RelayCommand CollapseAllCommand { get; }
    public RelayCommand FitCommand { get; }

    /// <summary>Refreshes the commands' enabled state (selection or read-only changed).</summary>
    public void RefreshCommands()
    {
        DeleteCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        CutCommand.RaiseCanExecuteChanged();
        ScaleCommand.RaiseCanExecuteChanged();
    }

    /// <summary>"12 keys", "1 key".</summary>
    public static string Keys(int n) => n == 1 ? "1 key" : string.Format(CultureInfo.CurrentCulture, "{0:N0} keys", n);

    /// <summary>The playhead as a whole tick.</summary>
    public int PlayheadTick => (int)Math.Round(Playback.Time);

    /// <summary>Deletes the selected keys (one undo step).</summary>
    public void DeleteSelected()
    {
        var sel = _doc.KeySelection;
        if (sel.IsEmpty) return;
        if (_doc.ApplyAndSelect("Delete " + Keys(sel.Count), c => (ClipEdit.DeleteKeys(c, sel.Validate(c)), KeySelection.Empty)))
            _doc.ShowStatus($"Deleted {Keys(sel.Count)}. A bone left without position keys collapses onto its parent (see Problems).");
    }

    /// <summary>Copies the selected keys to the system clipboard as KeyClipboard JSON. Returns false when nothing was copied.</summary>
    public bool Copy()
    {
        var sel = _doc.KeySelection;
        if (sel.IsEmpty) return false;
        var clipboard = KeyClipboard.Copy(Clip, sel, _doc.ClipboardBoneNames);
        if (clipboard.IsEmpty) return false;
        if (!KeyClipboardStore.Put(clipboard))
        {
            _doc.ShowStatus("The system clipboard is busy; the keys were copied inside Cairn only.");
            return true;
        }
        _doc.ShowStatus($"Copied {Keys(clipboard.KeyCount)} from {clipboard.Bones.Length} {(clipboard.Bones.Length == 1 ? "bone" : "bones")}.");
        return true;
    }

    /// <summary>Copies, then deletes the selected keys (one undo step: "Cut N keys").</summary>
    public bool Cut()
    {
        var sel = _doc.KeySelection;
        if (sel.IsEmpty || !Copy()) return false;
        _doc.ApplyAndSelect("Cut " + Keys(sel.Count), c => (ClipEdit.DeleteKeys(c, sel.Validate(c)), KeySelection.Empty));
        return true;
    }

    /// <summary>Pastes keys from the clipboard at the playhead, matching bones by name (one undo step).</summary>
    public bool Paste() => PasteCore(mirrored: false);

    /// <summary>Pastes the clipboard's keys mirrored left/right (onto each bone's partner).</summary>
    public bool PasteMirrored() => PasteCore(mirrored: true);

    private bool PasteCore(bool mirrored)
    {
        if (KeyClipboardStore.Get() is not { } clipboard || clipboard.IsEmpty)
        {
            _doc.ShowStatus("The clipboard holds no keys. Copy keys in a timeline first (Ctrl+C).");
            return false;
        }
        int time = PlayheadTick;
        var names = _doc.ClipboardBoneNames;
        if (mirrored && names is null)
        {
            _doc.ShowStatus("Paste mirrored needs bone names to find left/right partners: pick a preview mesh that fits the clip.");
            return false;
        }
        PasteResult? result = null;
        string label = (mirrored ? "Paste mirrored " : "Paste ") + Keys(clipboard.KeyCount);
        bool changed = _doc.ApplyAndSelect(label, c =>
        {
            result = mirrored
                ? clipboard.PasteMirrored(c, time, names, BonePairs.Detect(names!), new MirrorOptions(Skeleton: _doc.FittingSkeleton))
                : clipboard.Paste(c, time, names);
            return (result.Clip, result.Pasted);
        });
        if (result is { UnmatchedBones.Length: > 0 } r)
        {
            _doc.ShowStatus($"{(r.UnmatchedBones.Length == 1 ? "1 copied bone found no bone here and was skipped" : $"{r.UnmatchedBones.Length} copied bones found no bone here and were skipped")}:{string.Join(", ", r.UnmatchedBones.Take(4))}"
                + (r.UnmatchedBones.Length > 4 ? "…" : "."));
        }
        else if (changed)
        {
            _doc.ShowStatus($"Pasted {Keys(result!.Pasted.Count)} at {TimeFormat.Format(time, Unit)}.");
        }
        return changed;
    }

    /// <summary>K: keys the selected bones at the playhead (the motion is unchanged), selecting the new keys.</summary>
    public bool KeySelectedBonesAtPlayhead()
    {
        var bones = _doc.Selection.Bones.Where(b => b < Clip.BoneCount).ToList();
        if (bones.Count == 0)
        {
            _doc.ShowStatus("Select one or more bones first (click a row or a joint), then press K to key them at the playhead.");
            return false;
        }
        int time = PlayheadTick;
        string label = bones.Count == 1
            ? $"Key {_doc.BoneDisplayName(bones[0])} at {TimeFormat.Format(time, Unit)}"
            : $"Key {bones.Count} bones at {TimeFormat.Format(time, Unit)}";
        return _doc.ApplyAndSelect(label, c =>
        {
            var next = ClipEdit.KeyPoseAtTime(c, bones, time);
            return (next, KeySelection.InTimeRange(next, time, time, bones));
        });
    }

    /// <summary>Scales the selected keys' times about the playhead (negative reverses them).</summary>
    public bool ScaleSelected(double factor)
    {
        var sel = _doc.KeySelection;
        if (sel.IsEmpty || !double.IsFinite(factor) || factor == 0) return false;
        int pivot = PlayheadTick;
        return _doc.ApplyAndSelect($"Scale {Keys(sel.Count)} ×{factor.ToString("0.##", CultureInfo.CurrentCulture)}", c =>
        {
            var next = ClipEdit.ScaleKeys(c, sel.Validate(c), pivot, factor, out var scaled);
            return (next, scaled);
        });
    }

    // ── Drags (coalesced, live) ──────────────────────────────────────────────

    /// <summary>True while a key or handle drag is in progress.</summary>
    public bool IsDragging => _drag != DragKind.None;

    /// <summary>The time a drag currently puts the grabbed key on (for the readout), or null.</summary>
    public int? DragReadoutTime { get; private set; }

    /// <summary>Starts dragging the selected keys; <paramref name="grabTime"/> is the grabbed key's time.</summary>
    public void BeginMove(int grabTime, bool copy)
    {
        if (_doc.IsReadOnly || _doc.KeySelection.IsEmpty) return;
        _dragSelection = _doc.KeySelection;
        _dragGrabTime = grabTime;
        _dragCopy = copy;
        _drag = DragKind.Move;
        _lastDelta = int.MinValue;
        _doc.BeginEdit((copy ? "Copy " : "Move ") + Keys(_dragSelection.Count));
    }

    /// <summary>
    /// Updates a move/copy drag by <paramref name="rawDelta"/> ticks: snapped so the grabbed key lands on a
    /// frame (unless <paramref name="free"/>), and for a move limited so keys stop short of unselected
    /// neighbours (<see cref="ClipEdit.ClampMoveDelta"/>).
    /// </summary>
    public void UpdateMove(int rawDelta, bool free)
    {
        if (_drag != DragKind.Move) return;
        int target = free ? _dragGrabTime + rawDelta : SnapToFrame(_dragGrabTime + rawDelta);
        int delta = target - _dragGrabTime;
        if (delta == _lastDelta) return;
        _lastDelta = delta;
        var selection = _dragSelection;
        var names = _doc.ClipboardBoneNames;
        if (_dragCopy)
        {
            DragReadoutTime = _dragGrabTime + delta;
            if (delta == 0)
            {
                _doc.UpdateEditAndSelect(c => (c, selection));
                return;
            }
            _doc.UpdateEditAndSelect(c =>
            {
                var source = KeyClipboard.Copy(c, selection, names);
                var result = source.Paste(c, source.SourceTime + delta, names, new PasteOptions(MatchByIndex: true));
                return (result.Clip, result.Pasted);
            });
            return;
        }
        _doc.UpdateEditAndSelect(c =>
        {
            int clamped = ClipEdit.ClampMoveDelta(c, selection, delta);
            DragReadoutTime = _dragGrabTime + clamped;
            var next = ClipEdit.MoveKeys(c, selection, clamped, out var moved);
            return (next, moved);
        });
    }

    /// <summary>Starts scaling the selection about the playhead by dragging its edge key at <paramref name="edgeTime"/>.</summary>
    public void BeginScale(int edgeTime)
    {
        if (_doc.IsReadOnly || _doc.KeySelection.IsEmpty || edgeTime == PlayheadTick) return;
        _dragSelection = _doc.KeySelection;
        _dragEdgeTime = edgeTime;
        _drag = DragKind.Scale;
        _lastDelta = int.MinValue;
        _doc.BeginEdit("Scale " + Keys(_dragSelection.Count));
    }

    /// <summary>Updates a scale drag: the edge key follows the mouse (snapped to frames unless free).</summary>
    public void UpdateScale(int rawDelta, bool free)
    {
        if (_drag != DragKind.Scale) return;
        int pivot = PlayheadTick;
        int target = free ? _dragEdgeTime + rawDelta : SnapToFrame(_dragEdgeTime + rawDelta);
        if (target == _lastDelta) return;
        _lastDelta = target;
        double factor = (double)(target - pivot) / (_dragEdgeTime - pivot);
        if (Math.Abs(factor) < 1e-3) factor = Math.Sign(factor == 0 ? 1 : factor) * 1e-3;
        DragReadoutTime = target;
        var selection = _dragSelection;
        _doc.UpdateEditAndSelect(c =>
        {
            var next = ClipEdit.ScaleKeys(c, selection, pivot, factor, out var scaled);
            return (next, scaled);
        });
    }

    /// <summary>Starts dragging the clip's start (true) or end handle on the ruler.</summary>
    public void BeginRangeDrag(bool start)
    {
        if (_doc.IsReadOnly) return;
        _drag = start ? DragKind.StartHandle : DragKind.EndHandle;
        _lastDelta = int.MinValue;
        _doc.BeginEdit(start ? "Set start time" : "Set end time");
    }

    /// <summary>Moves the dragged handle to <paramref name="time"/> ticks (snapped to frames unless free).</summary>
    public void UpdateRangeDrag(int time, bool free)
    {
        if (_drag is not (DragKind.StartHandle or DragKind.EndHandle)) return;
        int t = free ? time : SnapToFrame(time);
        if (t == _lastDelta) return;
        _lastDelta = t;
        DragReadoutTime = t;
        bool start = _drag == DragKind.StartHandle;
        _doc.UpdateEdit(c =>
        {
            int value = start ? Math.Min(t, c.EndTime) : Math.Max(t, c.StartTime);
            return ClipEdit.SetHeader(c, start ? new ClipHeaderChange { StartTime = value } : new ClipHeaderChange { EndTime = value });
        });
    }

    /// <summary>Ends the drag as one undo step.</summary>
    public void EndDrag()
    {
        if (_drag == DragKind.None) return;
        _drag = DragKind.None;
        DragReadoutTime = null;
        _doc.CommitEdit();
        Changed?.Invoke(this, TimelineChange.Keys);
    }

    /// <summary>Abandons the drag (Esc), restoring the clip and the selection.</summary>
    public void CancelDrag()
    {
        if (_drag == DragKind.None) return;
        var selection = _dragSelection;
        bool keys = _drag is DragKind.Move or DragKind.Scale;
        _drag = DragKind.None;
        DragReadoutTime = null;
        _doc.CancelEdit();
        if (keys) _doc.SetKeySelection(selection);
        Changed?.Invoke(this, TimelineChange.Keys);
    }

    /// <summary>A time on the frame grid of the clip (start + n × 160).</summary>
    public int SnapToFrame(double ticks)
    {
        int start = Clip.StartTime;
        return start + (int)Math.Round((ticks - start) / RfaClip.TicksPerFrame) * RfaClip.TicksPerFrame;
    }

    /// <summary>Seeks to a key and brings the Key inspector forward (double-click on a key).</summary>
    public void OpenKey(KeyRef key)
    {
        if (!key.IsValidIn(Clip)) return;
        Playback.Pause();
        Playback.Seek(key.TimeIn(Clip));
        _doc.SetKeySelection(KeySelection.Of(key));
        if (!_doc.Selection.Contains(key.Bone)) _doc.Selection.Select(key.Bone);
        _doc.SelectedInspectorTab = _doc.InspectorTabs.FirstOrDefault(t => t.Id == "key") ?? _doc.SelectedInspectorTab;
    }
}

/// <summary>What changed in the timeline, so the control redraws only that.</summary>
public enum TimelineChange
{
    /// <summary>The rows (collapse, filter, names, markers).</summary>
    Rows,
    /// <summary>Key data (an edit).</summary>
    Keys,
    /// <summary>The key selection.</summary>
    Selection,
    /// <summary>The bone selection (row highlight).</summary>
    BoneSelection,
    /// <summary>Unit or other layout-affecting settings.</summary>
    Layout,
}

/// <summary>
/// The key clipboard: KeyClipboard JSON on the system clipboard (so keys paste across tabs and
/// instances), with an in-process copy as a fallback when the system clipboard is busy or holds
/// something else that the user did not mean to replace.
/// </summary>
public static class KeyClipboardStore
{
    private static KeyClipboard? _last;
    private static string? _lastJson;

    /// <summary>True when the last <see cref="Put"/> could not open the system clipboard (another program held it).</summary>
    public static bool LastPutFailed { get; private set; }

    /// <summary>Puts keys on the clipboard. Returns false when only the in-process copy could be kept.</summary>
    public static bool Put(KeyClipboard clipboard)
    {
        ArgumentNullException.ThrowIfNull(clipboard);
        _last = clipboard;
        _lastJson = clipboard.ToJson();
        // Another program can hold the clipboard open for a moment: SystemClipboard retries on
        // CLIPBRD_E_CANT_OPEN before giving up (the in-process copy is kept either way).
        LastPutFailed = !SystemClipboard.TrySetText(_lastJson);
        return !LastPutFailed;
    }

    /// <summary>The keys on the system clipboard (or the last in-process copy), or null.</summary>
    public static KeyClipboard? Get()
    {
        if (!SystemClipboard.TryGetText(out string? text)) return _last;
        if (text is null) return _last;
        if (text == _lastJson && _last is not null) return _last;
        if (!text.Contains(KeyClipboard.FormatName, StringComparison.Ordinal)) return null;
        try
        {
            return KeyClipboard.FromJson(text);
        }
        catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
