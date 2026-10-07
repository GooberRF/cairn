using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Editing;
using Cairn.Atx.Schema;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>
/// The frames list. Refreshes diff the new model against the existing rows rather than rebuilding
/// the collection, which is what keeps selection, scroll position and decoded thumbnails stable
/// while the user types in the source editor. Every command produces a
/// <see cref="Cairn.Atx.Text.TextEditBatch"/> and applies it to the document text; nothing here mutates
/// a model.
/// </summary>
public sealed class FrameListViewModel : ObservableObject
{
    /// <summary>Longest side, in pixels, that a row thumbnail is decoded to.</summary>
    public const int ThumbnailPixels = 64;

    private readonly DocumentViewModel _document;
    private bool _suppressSelection;
    private bool _isFocused;
    private int _playingIndex = -1;

    public FrameListViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));

        AddFramesCommand = new RelayCommand(AddFrames, () => CanEditStructure);
        AddFramesFromVppCommand = new RelayCommand(AddFramesFromVpp, () => CanEditStructure);
        RemoveCommand = new RelayCommand(RemoveSelected, () => CanEditStructure && HasSelection);
        DuplicateCommand = new RelayCommand(DuplicateSelected, () => CanEditStructure && HasSelection);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1), () => CanEditStructure && HasSelection);
        MoveDownCommand = new RelayCommand(() => MoveSelected(1), () => CanEditStructure && HasSelection);
        CopyCommand = new RelayCommand(CopySelected, () => HasSelection);
        CutCommand = new RelayCommand(CutSelected, () => CanEditStructure && HasSelection);
        PasteCommand = new RelayCommand(PasteFrames, () => CanEditStructure);
        ReverseCommand = new RelayCommand(ReverseSelected, () => CanEditStructure && SelectedIndices.Count > 1);
        SortCommand = new RelayCommand(SortSelected, () => CanEditStructure && SelectedIndices.Count > 1);
        SelectAllCommand = new RelayCommand(SelectAll, () => Rows.Count > 0);
        RenameCommand = new RelayCommand(
            () => PrimaryRow?.BeginEditName(), () => CanEditStructure && PrimaryRow is not null);
        EditTimeCommand = new RelayCommand(
            () => PrimaryRow?.BeginEditTime(), () => CanEditStructure && PrimaryRow is not null);
        LocateFileCommand = new RelayCommand(LocateFile, () => CanLocateFile);
    }

    /// <summary>The rows, in frame order.</summary>
    public ObservableCollection<FrameRowViewModel> Rows { get; } = [];

    /// <summary>True when the list itself has keyboard focus, which suppresses caret-driven sync.</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set => Set(ref _isFocused, value);
    }

    /// <summary>
    /// The selected frame indices, ascending. Cached, because one selection change fans out into
    /// a dozen reads (every command's CanExecute, then the inspector's own refresh) and rebuilding
    /// a sorted list from every row each time is quadratic on a large document.
    /// </summary>
    public IReadOnlyList<int> SelectedIndices =>
        _selectedIndices ??= [.. Rows.Where(r => r.IsSelected).Select(r => r.Index).OrderBy(i => i)];

    private IReadOnlyList<int>? _selectedIndices;

    /// <summary>Forgets the cached selection after anything that can change it.</summary>
    private void InvalidateSelection() => _selectedIndices = null;

    /// <summary>The first selected frame, or -1.</summary>
    public int PrimarySelectedIndex
    {
        get
        {
            foreach (var row in Rows)
            {
                if (row.IsSelected) return row.Index;
            }
            return -1;
        }
    }

    /// <summary>The first selected row, or null.</summary>
    public FrameRowViewModel? PrimaryRow => Rows.FirstOrDefault(r => r.IsSelected);

    /// <summary>
    /// The frame the preview is showing. Setting it moves a subtle marker down the list without
    /// touching the selection, which is what lets playback run while the user edits a frame.
    /// </summary>
    public int PlayingIndex
    {
        get => _playingIndex;
        set
        {
            if (_playingIndex == value) return;
            // Only two rows ever change, and this runs on every advance at up to 60 Hz — walking
            // the whole list would be 2,000 iterations a frame on a long sequence.
            if (_playingIndex >= 0 && _playingIndex < Rows.Count) Rows[_playingIndex].IsPlayingFrame = false;
            _playingIndex = value;
            if (value >= 0 && value < Rows.Count) Rows[value].IsPlayingFrame = true;
            Raise();
        }
    }

    /// <summary>True when at least one row is selected.</summary>
    public bool HasSelection => Rows.Any(r => r.IsSelected);

    /// <summary>True when structural edits are allowed (valid TOML in the standard layout).</summary>
    public bool CanEditStructure => _document.CanEditStructure;

    /// <summary>True when the list has no frames at all, so the empty state shows.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The footer summary: frame count, loop length and how many frames override something.</summary>
    public string FooterText
    {
        get
        {
            var model = _document.Model;
            if (model is null || model.Frames.Count == 0) return "No frames";
            int overrides = model.OverrideCount;
            string frames = model.Frames.Count == 1 ? "1 frame" : $"{model.Frames.Count} frames";
            string loop = _document.LoopDurationText;
            return overrides == 0
                ? $"{frames} · {loop}"
                : $"{frames} · {loop} · {overrides} override{(overrides == 1 ? "" : "s")}";
        }
    }

    /// <summary>Add Frames &gt; Browse…: the multi-select file picker, unchanged.</summary>
    public RelayCommand AddFramesCommand { get; }

    /// <summary>
    /// Add Frames &gt; From VPP…: browse the .vpp archives on the search path. Same insertion path,
    /// so a batch from an archive is still one Ctrl+Z.
    /// </summary>
    public RelayCommand AddFramesFromVppCommand { get; }

    public RelayCommand RemoveCommand { get; }
    public RelayCommand DuplicateCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand CutCommand { get; }
    public RelayCommand PasteCommand { get; }
    public RelayCommand ReverseCommand { get; }
    public RelayCommand SortCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand EditTimeCommand { get; }

    /// <summary>
    /// Points the selected frame at its image, for a frame whose image cannot be found. Deliberately
    /// single-selection: the flow is one file picker per frame, and a menu item that silently opens
    /// six pickers in a row is worse than one that tells the truth about what it does.
    /// </summary>
    public RelayCommand LocateFileCommand { get; }

    private bool CanLocateFile =>
        CanEditStructure && SelectedIndices.Count == 1 && _document.IsFrameImageMissing(SelectedIndices[0]);

    private void LocateFile()
    {
        if (!CanLocateFile) return;
        _document.LocateFrameImage(SelectedIndices[0]);
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    /// <summary>Diffs the rows against the current model, reusing rows so the UI never flickers.</summary>
    public void Refresh()
    {
        var model = _document.Model;
        int count = model?.Frames.Count ?? 0;
        var diagnostics = ByFrame(_document.Diagnostics);

        _suppressSelection = true;
        try
        {
            while (Rows.Count > count) Rows.RemoveAt(Rows.Count - 1);
            for (int i = 0; i < count; i++)
            {
                if (i >= Rows.Count) Rows.Add(new FrameRowViewModel(this, i));
                Rows[i].Index = i;
                Rows[i].Update(model!.Frames[i], model, diagnostics);
            }
        }
        finally { _suppressSelection = false; InvalidateSelection(); }

        // Rows are recycled, so the playing marker has to be re-applied to the new indices.
        if (_playingIndex >= Rows.Count) _playingIndex = Rows.Count - 1;
        for (int i = 0; i < Rows.Count; i++) Rows[i].IsPlayingFrame = i == _playingIndex;

        RequestThumbnails();
        RaiseAll(nameof(IsEmpty), nameof(FooterText), nameof(CanEditStructure), nameof(HasSelection));
        RefreshCommands();
    }

    /// <summary>Re-applies diagnostics and thumbnails after the asset pass finishes.</summary>
    public void OnAssetsChanged()
    {
        var diagnostics = ByFrame(_document.Diagnostics);
        foreach (var row in Rows) row.ApplyDiagnostics(diagnostics);
        RequestThumbnails();
        // Whether a frame's image is missing is only known once the asset pass has run, and that is
        // exactly what "Locate File…" is enabled on.
        LocateFileCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Groups diagnostics by frame once, so each row can pick up its own in O(1).</summary>
    private static ILookup<int, Cairn.Atx.Linting.Diagnostic> ByFrame(
        IReadOnlyList<Cairn.Atx.Linting.Diagnostic> diagnostics) =>
        diagnostics.Where(d => d.FrameIndex is not null).ToLookup(d => d.FrameIndex!.Value);

    /// <summary>Drops every thumbnail, e.g. after an image changed on disk.</summary>
    public void ClearThumbnails()
    {
        _document.CancelThumbnails();
        foreach (var row in Rows) row.ResetThumbnail();
        RequestThumbnails();
    }

    /// <summary>
    /// How many rows either side of a visible one are fetched as well, so scrolling finds the next
    /// few thumbnails already there.
    /// </summary>
    private const int ThumbnailLookahead = 8;

    /// <summary>
    /// Fetches the thumbnails of the rows the list has actually put on screen, plus a short
    /// look-ahead. Asking for all of them was the problem: a 500-frame document started 500 image
    /// decodes the instant it opened, for rows nobody had scrolled to yet.
    /// </summary>
    private void RequestThumbnails()
    {
        foreach (var row in Rows)
        {
            if (row.IsRealised) RequestThumbnailsAround(row.Index);
        }
    }

    /// <summary>
    /// A row has just come on screen. Called by the view when a row container is realised or
    /// scrolled into view, which is what makes a long list fill in as it is scrolled rather than
    /// all at once.
    /// </summary>
    /// <param name="index">The row's index.</param>
    internal void OnRowRealised(int index)
    {
        if ((uint)index >= (uint)Rows.Count) return;
        var row = Rows[index];
        // Already reported: the list tells us about every container it has each time it makes one,
        // so most calls are about rows that have been on screen for a while.
        if (row.IsRealised) return;
        row.IsRealised = true;
        RequestThumbnailsAround(index);
    }

    private void RequestThumbnailsAround(int index)
    {
        int first = Math.Max(0, index - ThumbnailLookahead);
        int last = Math.Min(Rows.Count - 1, index + ThumbnailLookahead);
        var service = _document.Shell.Thumbnails;
        var resolver = _document.Resolver;
        var token = _document.ThumbnailToken;
        for (int i = first; i <= last; i++)
        {
            var row = Rows[i];
            if (row.ThumbnailRequested || string.IsNullOrEmpty(row.FileName)) continue;
            row.ThumbnailRequested = true;
            _ = LoadThumbnailAsync(service, resolver, row, row.FileName, token);
        }
    }

    private static async System.Threading.Tasks.Task LoadThumbnailAsync(
        Services.ThumbnailService service, Cairn.Assets.AssetResolver resolver,
        FrameRowViewModel row, string name, System.Threading.CancellationToken token)
    {
        try
        {
            var bitmap = await service.GetAsync(resolver, name, ThumbnailPixels, token).ConfigureAwait(true);
            if (token.IsCancellationRequested) return;
            if (string.Equals(row.FileName, name, StringComparison.Ordinal)) row.Thumbnail = bitmap;
        }
        catch (OperationCanceledException) { }
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by a row when its selection state changes. Rows are no longer what the list box
    /// reads, so this only ever runs for a change this class made itself.
    /// </summary>
    internal void OnRowSelectionChanged()
    {
        InvalidateSelection();
        if (_suppressSelection) return;
        RaiseAll(nameof(HasSelection), nameof(SelectedIndices), nameof(PrimarySelectedIndex));
        RefreshCommands();
        _document.OnFrameSelectionChanged();
    }

    /// <summary>
    /// Raised when the list box should make its selection match these rows. The list box is the
    /// authority on what is selected; this is how a selection decided here reaches it.
    /// </summary>
    public event EventHandler<IReadOnlyList<FrameRowViewModel>>? SelectionRequested;

    /// <summary>
    /// Called by the view whenever the list box's own selection changes — a click, a Shift+click,
    /// a rubber band, or rows disappearing under it during a refresh. This is the one place the
    /// view-model learns what is selected.
    /// </summary>
    /// <param name="selected">The selected items, as the list box has them.</param>
    public void OnListSelectionChanged(IEnumerable<object?> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        var chosen = new HashSet<FrameRowViewModel>(selected.OfType<FrameRowViewModel>());
        bool quiet = _suppressSelection;
        _suppressSelection = true;
        try
        {
            foreach (var row in Rows) row.IsSelected = chosen.Contains(row);
        }
        finally { _suppressSelection = quiet; InvalidateSelection(); }

        // A refresh is already going to raise everything; fanning out from inside it would ask the
        // inspector and the preview to read a row list that is still being rebuilt.
        if (quiet) return;
        RaiseAll(nameof(HasSelection), nameof(SelectedIndices), nameof(PrimarySelectedIndex));
        RefreshCommands();
        _document.OnFrameSelectionChanged();
    }

    /// <summary>Selects exactly <paramref name="count"/> rows starting at <paramref name="start"/>.</summary>
    public void SelectRange(int start, int count) =>
        ApplySelection([.. Rows.Where(r => r.Index >= start && r.Index < start + count)]);

    /// <summary>Selects everything (Ctrl+A in the list).</summary>
    public void SelectAll() => ApplySelection([.. Rows]);

    /// <summary>
    /// Narrows the selection to one frame. The list view uses this to finish a plain click on an
    /// already-multi-selected row, which it defers to mouse-up so a drag can move the whole
    /// selection instead of just the row under the cursor.
    /// </summary>
    public void SelectOnly(int index) => SelectRange(index, 1);

    /// <summary>
    /// Makes <paramref name="rows"/> the selection, in the view-model and in the list box.
    /// The view-model is updated first so a caller that reads <see cref="SelectedIndices"/> on the
    /// next line sees the new answer even when no view is attached.
    /// </summary>
    private void ApplySelection(IReadOnlyList<FrameRowViewModel> rows)
    {
        var chosen = new HashSet<FrameRowViewModel>(rows);
        _suppressSelection = true;
        try
        {
            foreach (var row in Rows) row.IsSelected = chosen.Contains(row);
        }
        finally { _suppressSelection = false; InvalidateSelection(); }

        SelectionRequested?.Invoke(this, rows);
        OnRowSelectionChanged();
    }

    /// <summary>
    /// Selects one frame because the source caret moved into its block. Never scrolls the source
    /// back, so the caret the user is driving stays where they put it.
    /// </summary>
    public void SelectFromEditor(int index)
    {
        if (index < 0 || index >= Rows.Count) return;
        if (SelectedIndices.Count == 1 && PrimarySelectedIndex == index) return;

        var only = new[] { Rows[index] };
        _suppressSelection = true;
        try
        {
            foreach (var row in Rows) row.IsSelected = row.Index == index;
        }
        finally { _suppressSelection = false; InvalidateSelection(); }

        SelectionRequested?.Invoke(this, only);
        RaiseAll(nameof(HasSelection), nameof(SelectedIndices), nameof(PrimarySelectedIndex));
        RefreshCommands();
        _document.Inspector.Refresh();
        ScrollIntoViewRequested?.Invoke(this, index);
    }

    /// <summary>Raised when the list should scroll a row into view.</summary>
    public event EventHandler<int>? ScrollIntoViewRequested;

    /// <summary>Re-evaluates every command's enabled state.</summary>
    public void RefreshCommands()
    {
        AddFramesCommand.RaiseCanExecuteChanged();
        AddFramesFromVppCommand.RaiseCanExecuteChanged();
        RemoveCommand.RaiseCanExecuteChanged();
        DuplicateCommand.RaiseCanExecuteChanged();
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        CutCommand.RaiseCanExecuteChanged();
        PasteCommand.RaiseCanExecuteChanged();
        ReverseCommand.RaiseCanExecuteChanged();
        SortCommand.RaiseCanExecuteChanged();
        SelectAllCommand.RaiseCanExecuteChanged();
        RenameCommand.RaiseCanExecuteChanged();
        EditTimeCommand.RaiseCanExecuteChanged();
        LocateFileCommand.RaiseCanExecuteChanged();
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private void AddFrames()
    {
        var paths = _document.Shell.Dialogs.OpenImageFiles(_document.AtxFolder);
        if (paths.Length == 0) return;
        int insert = HasSelection ? SelectedIndices[^1] + 1 : _document.FrameCount;
        _document.AddImageFiles(paths, insert);
    }

    private void AddFramesFromVpp()
    {
        if (!CanEditStructure) return;
        // Same guard the other dialog commands use: an inline editor still open in the list would
        // otherwise have its pending value written after the dialog's own edit.
        _document.Shell.CommitPendingEdits();
        _document.EnsureParsed();
        _document.Shell.Dialogs.ShowVppBrowser(_document);
    }

    private void RemoveSelected()
    {
        var indices = SelectedIndices;
        if (indices.Count == 0) return;
        int next = Math.Max(0, indices[0] - 0);
        _document.Edit(e => e.RemoveFrames(indices));
        if (Rows.Count > 0) SelectRange(Math.Min(next, Rows.Count - 1), 1);
    }

    private void DuplicateSelected()
    {
        var indices = SelectedIndices;
        if (indices.Count == 0) return;
        _document.Edit(e => e.DuplicateFrames(indices));
        SelectRange(indices[^1] + 1, indices.Count);
    }

    private void MoveSelected(int delta)
    {
        var indices = SelectedIndices;
        if (indices.Count == 0) return;
        int target = delta < 0 ? indices[0] - 1 : indices[^1] + 2;
        if (target < 0 || indices[^1] + 1 > _document.FrameCount) return;
        if (delta > 0 && indices[^1] == _document.FrameCount - 1) return;
        if (delta < 0 && indices[0] == 0) return;
        MoveSelectionTo(target);
    }

    /// <summary>
    /// Moves the selection so it lands before <paramref name="targetIndex"/> in the current order.
    /// Drag-and-drop and Alt+Up/Down both go through here.
    /// </summary>
    public void MoveSelectionTo(int targetIndex)
    {
        var indices = SelectedIndices;
        if (indices.Count == 0 || !CanEditStructure) return;

        // Work out where the block lands so the same frames stay selected afterwards. The set
        // matters: Contains on the index list is a linear scan, so dragging a select-all on a
        // 2,000-frame file would be four million comparisons per drop.
        var moving = indices.ToHashSet();
        int landing = 0;
        for (int i = 0; i < targetIndex && i < _document.FrameCount; i++)
        {
            if (!moving.Contains(i)) landing++;
        }

        if (!_document.Edit(e => e.MoveFrames(indices, targetIndex))) return;
        SelectRange(landing, indices.Count);
    }

    private void CopySelected() => _ = TryCopySelected();

    private bool TryCopySelected()
    {
        var model = _document.Model;
        var indices = SelectedIndices;
        if (model is null || indices.Count == 0) return false;
        return SetClipboardText(FrameClipboard.ToToml(model, indices, _document.LineEnding));
    }

    private void CutSelected()
    {
        // Never remove the frames unless they actually reached the clipboard: another process can
        // be holding it, and a cut that deletes with nothing to paste back loses the user's work.
        if (!TryCopySelected())
        {
            _document.Shell.Dialogs.ShowError(
                "Those frames could not be cut.",
                "The clipboard is in use by another program, so the frames were left alone. "
                + "Try again in a moment.",
                null);
            return;
        }
        RemoveSelected();
    }

    private void PasteFrames()
    {
        string? text = GetClipboardText();
        var frames = FrameClipboard.Parse(text);
        if (frames.Count == 0) return;
        int insert = HasSelection ? SelectedIndices[^1] + 1 : _document.FrameCount;
        _document.Edit(e => e.InsertFrames(insert, frames));
        SelectRange(insert, frames.Count);
        ClearThumbnails();
    }

    private void ReverseSelected() => _document.Edit(e => e.ReverseFrames(SelectedIndices));

    private void SortSelected() => _document.Edit(e => e.SortFrames(SelectedIndices));

    /// <summary>Writes a new file name into one frame (the F2 inline editor).</summary>
    public void RenameFrame(int index, string fileName)
    {
        string name = (fileName ?? string.Empty).Trim();
        if (name.Length == 0) return;
        _document.Edit(e => e.SetFrameValue([index], AtxSchema.KeyFile, AtxValue.String(name)));
        ClearThumbnails();
    }

    /// <summary>
    /// Writes a per-frame time (the inline time editor). An empty box removes the override, which
    /// is how the frame goes back to inheriting the texture's frame time.
    /// </summary>
    public void SetFrameTime(int index, string text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            _document.Edit(e => e.RemoveFrameKey([index], AtxSchema.KeyFrameTime));
            return;
        }
        if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.CurrentCulture, out int ms)) return;
        ms = Math.Clamp(ms, AtxSchema.MinFrameTimeMs, 600000);
        _document.Edit(e => e.SetFrameValue([index], AtxSchema.KeyFrameTime, AtxValue.Integer(ms)));
    }

    // The shared clipboard helper retries while another program (a clipboard manager, a remote-desktop client)
    // briefly holds the clipboard, instead of failing on the first attempt.
    private static bool SetClipboardText(string text) => Cairn.Ui.Services.SystemClipboard.TrySetText(text);

    private static string? GetClipboardText() => Cairn.Ui.Services.SystemClipboard.TryGetText(out var text) ? text : null;
}
