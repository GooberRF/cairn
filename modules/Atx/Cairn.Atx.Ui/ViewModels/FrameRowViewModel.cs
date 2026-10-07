using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media.Imaging;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>
/// One row of the frames list. Rows are reused across refreshes — the list diffs the new model
/// against the existing rows by index instead of rebuilding the collection — so selection, scroll
/// position and already-decoded thumbnails survive every keystroke in the source editor.
/// </summary>
public sealed class FrameRowViewModel : ObservableObject
{
    private readonly FrameListViewModel _owner;

    private int _index;
    private string _fileName = string.Empty;
    private int _effectiveTimeMs;
    private bool _isTimeOverridden;
    private string? _materialBadge;
    private BitmapSource? _thumbnail;
    private bool _thumbnailRequested;
    private DiagnosticSeverity? _severity;
    private string? _problemTooltip;
    private bool _isSelected;
    private bool _isPlayingFrame;
    private bool _isEditingName;
    private bool _isEditingTime;
    private string _editText = string.Empty;
    private string _editSeed = string.Empty;

    internal FrameRowViewModel(FrameListViewModel owner, int index)
    {
        _owner = owner;
        _index = index;
    }

    /// <summary>0-based frame index, exactly as <c>ATX_Set_Frame</c> uses it.</summary>
    public int Index
    {
        get => _index;
        internal set
        {
            if (Set(ref _index, value))
                RaiseAll(nameof(IndexText), nameof(NameEditorName), nameof(TimeEditorName));
        }
    }

    /// <summary>The index as shown in the row's leading column.</summary>
    public string IndexText => _index.ToString(CultureInfo.CurrentCulture);

    /// <summary>
    /// Screen-reader name for this row's inline rename box. It says which frame it belongs to,
    /// because several rows can be on screen and the inspector has its own file box as well.
    /// </summary>
    public string NameEditorName => $"Frame {IndexText} file name";

    /// <summary>
    /// Screen-reader name for this row's inline time box, distinct from the inspector's frame-time
    /// box — that one edits every selected frame, this one edits exactly this frame.
    /// </summary>
    public string TimeEditorName => $"Frame {IndexText} time";

    /// <summary>The image file name written in the frame.</summary>
    public string FileName
    {
        get => _fileName;
        private set => Set(ref _fileName, value);
    }

    /// <summary>The frame's effective duration, after inheritance and clamping.</summary>
    public int EffectiveTimeMs
    {
        get => _effectiveTimeMs;
        private set { if (Set(ref _effectiveTimeMs, value)) Raise(nameof(TimeText)); }
    }

    /// <summary>The effective duration with its unit, e.g. "200 ms".</summary>
    public string TimeText => _effectiveTimeMs.ToString(CultureInfo.CurrentCulture) + " ms";

    /// <summary>True when the frame sets its own <c>frame_time</c>; the row shows it bold.</summary>
    public bool IsTimeOverridden
    {
        get => _isTimeOverridden;
        private set => Set(ref _isTimeOverridden, value);
    }

    /// <summary>The per-frame material token, or null when the frame inherits.</summary>
    public string? MaterialBadge
    {
        get => _materialBadge;
        private set { if (Set(ref _materialBadge, value)) Raise(nameof(HasMaterialBadge)); }
    }

    /// <summary>True when the row shows a material badge.</summary>
    public bool HasMaterialBadge => !string.IsNullOrEmpty(_materialBadge);

    /// <summary>The decoded thumbnail, or null while it loads or when the image is missing.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        internal set { if (Set(ref _thumbnail, value)) Raise(nameof(HasThumbnail)); }
    }

    /// <summary>True once a thumbnail is available.</summary>
    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>The worst severity among this frame's diagnostics, or null when it is clean.</summary>
    public DiagnosticSeverity? Severity
    {
        get => _severity;
        private set { if (Set(ref _severity, value)) Raise(nameof(HasProblem)); }
    }

    /// <summary>True when the row shows a problem icon.</summary>
    public bool HasProblem => _severity is not null;

    /// <summary>Every problem on this frame, one per line, for the icon's tooltip.</summary>
    public string? ProblemTooltip
    {
        get => _problemTooltip;
        private set => Set(ref _problemTooltip, value);
    }

    /// <summary>Row selection, bound two-way to the list box's container.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) _owner.OnRowSelectionChanged(); }
    }

    /// <summary>
    /// True while the preview is showing this frame. Deliberately separate from
    /// <see cref="IsSelected"/>: the playing frame gets a marker, not the selection, so playback
    /// never moves the caret or changes what the inspector is editing.
    /// </summary>
    public bool IsPlayingFrame
    {
        get => _isPlayingFrame;
        internal set => Set(ref _isPlayingFrame, value);
    }

    /// <summary>True while F2 renaming is in progress.</summary>
    public bool IsEditingName
    {
        get => _isEditingName;
        private set { if (Set(ref _isEditingName, value)) Raise(nameof(IsEditing)); }
    }

    /// <summary>True while the inline frame-time editor is open.</summary>
    public bool IsEditingTime
    {
        get => _isEditingTime;
        private set { if (Set(ref _isEditingTime, value)) Raise(nameof(IsEditing)); }
    }

    /// <summary>True while either inline editor is open.</summary>
    public bool IsEditing => _isEditingName || _isEditingTime;

    /// <summary>The text in whichever inline editor is open.</summary>
    public string EditText
    {
        get => _editText;
        set => Set(ref _editText, value);
    }

    /// <summary>Starts renaming the frame's file (F2).</summary>
    public void BeginEditName()
    {
        if (!_owner.CanEditStructure) return;
        EditText = _fileName;
        _editSeed = _fileName;
        IsEditingTime = false;
        IsEditingName = true;
    }

    /// <summary>
    /// Starts editing the frame's time (double-click on the time column). The box is seeded with
    /// the *effective* time, which for an inheriting frame is the texture's — so the seed is
    /// remembered, and a box the user only looked at commits nothing.
    /// </summary>
    public void BeginEditTime()
    {
        if (!_owner.CanEditStructure) return;
        EditText = _effectiveTimeMs.ToString(CultureInfo.CurrentCulture);
        _editSeed = _editText;
        IsEditingName = false;
        IsEditingTime = true;
    }

    /// <summary>
    /// Writes the inline editor's value back to the document. Text the user did not change is not
    /// written: committing the seeded effective time would add a <c>frame_time</c> override to a
    /// frame that was perfectly happy inheriting one, for the crime of being double-clicked.
    /// Clearing the box is a change, and still removes the override.
    /// </summary>
    public void CommitEdit()
    {
        bool name = _isEditingName;
        bool time = _isEditingTime;
        string text = _editText;
        string seed = _editSeed;
        CancelEdit();
        if (string.Equals(text, seed, StringComparison.Ordinal)) return;
        if (name) _owner.RenameFrame(_index, text);
        else if (time) _owner.SetFrameTime(_index, text);
    }

    /// <summary>Closes the inline editor without changing anything.</summary>
    public void CancelEdit()
    {
        IsEditingName = false;
        IsEditingTime = false;
    }

    /// <summary>
    /// Updates the row from the model. Returns true when the image changed, which is the list's
    /// signal that the thumbnail has to be fetched again.
    /// </summary>
    internal bool Update(AtxFrame frame, AtxModel model, ILookup<int, Diagnostic> diagnostics)
    {
        string file = frame.EffectiveFile ?? string.Empty;
        bool imageChanged = !string.Equals(file, _fileName, StringComparison.Ordinal);
        if (imageChanged)
        {
            FileName = file;
            Thumbnail = null;
            _thumbnailRequested = false;
        }

        EffectiveTimeMs = model.FrameTimeMs(frame.Index);
        IsTimeOverridden = frame.FrameTimeOverrideMs is not null;
        MaterialBadge = frame.MaterialOverride;
        ApplyDiagnostics(diagnostics);
        return imageChanged;
    }

    /// <summary>
    /// Re-reads this frame's diagnostics after an asset pass merges its findings in. The caller
    /// passes a lookup rather than the flat list: scanning the whole list once per row made a
    /// refresh cost (rows × diagnostics), which is four million comparisons on a 2,000-frame file
    /// whose images have not been found yet.
    /// </summary>
    internal void ApplyDiagnostics(ILookup<int, Diagnostic> diagnostics)
    {
        var mine = diagnostics[_index].ToList();
        if (mine.Count == 0)
        {
            Severity = null;
            ProblemTooltip = null;
            return;
        }
        Severity = mine.Max(d => d.Severity);
        ProblemTooltip = string.Join(Environment.NewLine, mine.Select(d => $"{d.Code}: {d.Message}"));
    }

    /// <summary>True when a thumbnail request has already been issued for the current file.</summary>
    internal bool ThumbnailRequested
    {
        get => _thumbnailRequested;
        set => _thumbnailRequested = value;
    }

    /// <summary>
    /// True once the list has drawn this row at least once. Only realised rows ask for a
    /// thumbnail, so opening a 500-frame document does not start 500 image decodes.
    /// </summary>
    internal bool IsRealised { get; set; }

    /// <summary>Forgets the thumbnail so the next refresh fetches it again.</summary>
    internal void ResetThumbnail()
    {
        Thumbnail = null;
        _thumbnailRequested = false;
    }
}
