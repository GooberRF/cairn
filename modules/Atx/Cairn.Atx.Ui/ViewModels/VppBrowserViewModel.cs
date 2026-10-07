using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.Schema;
using Cairn.Atx.Sequences;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>What the browser is being used for.</summary>
public enum VppBrowserMode
{
    /// <summary>Frames &gt; Add Frames &gt; From VPP…: tick many images and insert them as frames.</summary>
    AddFrames,

    /// <summary>The alpha-mask row's From VPP…: pick exactly one image and return its name.</summary>
    PickOne,

    /// <summary>
    /// File &gt; Import VBM from VPP…: pick exactly one <c>.vbm</c>. Everything else is filtered
    /// out, and an archive holding no VBM is not listed at all — an import browser full of
    /// archives with nothing importable in them is a list to scroll past, not a list to choose
    /// from.
    /// </summary>
    PickVbm,
}

/// <summary>How a chosen entry gets from the archive to the frame.</summary>
public enum VppReferenceMode
{
    /// <summary>Store the bare name and let the game find it in the .vpp, as it already does.</summary>
    ReferenceByName,

    /// <summary>Write the entry's bytes next to the .atx and refer to that loose file.</summary>
    Extract,

    /// <summary>Add the archive's folder to the search folders, then refer by name.</summary>
    AddFolderToSearch,
}

// ── Tree nodes ────────────────────────────────────────────────────────────────

/// <summary>Anything that can appear in the browser's tree.</summary>
public abstract class VppNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _isFocusedNode;
    private IReadOnlyList<VppNodeViewModel> _children = [];

    /// <summary>The text the row shows.</summary>
    public abstract string Label { get; }

    /// <summary>The row's tooltip and automation name.</summary>
    public abstract string Description { get; }

    /// <summary>
    /// The rows underneath this one. Deliberately a whole list that is replaced rather than an
    /// observable collection that is added to: showing a 3,000-entry archive one
    /// <c>CollectionChanged</c> at a time makes the generator do three thousand pieces of
    /// bookkeeping for a list the user has not even opened yet.
    /// </summary>
    public IReadOnlyList<VppNodeViewModel> Children
    {
        get => _children;
        protected internal set => Set(ref _children, value);
    }

    /// <summary>Whether the row is open. Expanding an archive is what makes it load.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (Set(ref _isExpanded, value) && value) OnExpanded(); }
    }

    /// <summary>True for the row the preview is showing.</summary>
    public bool IsFocusedNode
    {
        get => _isFocusedNode;
        internal set => Set(ref _isFocusedNode, value);
    }

    /// <summary>Called the first time the row is opened.</summary>
    protected virtual void OnExpanded() { }
}

/// <summary>One search location: the .atx's folder, a search folder, the game, or ad-hoc archives.</summary>
public sealed class VppSourceNodeViewModel : VppNodeViewModel
{
    internal VppSourceNodeViewModel(string label, string description, bool isSearched)
    {
        Label = label;
        Description = description;
        IsSearched = isSearched;
        IsExpanded = true;
        // An observable list, assigned once: archives arrive one at a time as the indexing pass
        // finds images in them, and inserting into this notifies WPF about that one row instead of
        // rebuilding every sibling — which would throw away the expansion state of the others.
        Children = Archives;
    }

    /// <summary>The archive rows under this group, in the resolver's order.</summary>
    internal ObservableCollection<VppNodeViewModel> Archives { get; } = [];

    /// <inheritdoc />
    public override string Label { get; }

    /// <inheritdoc />
    public override string Description { get; }

    /// <summary>True when names inside these archives resolve without being extracted.</summary>
    public bool IsSearched { get; }
}

/// <summary>One .vpp archive. Its image rows are built the first time it is opened or searched.</summary>
public sealed class VppArchiveNodeViewModel : VppNodeViewModel
{
    private readonly VppBrowserViewModel _owner;
    private VppImageIndex? _index;
    private string _label;
    private bool _hasEntries;
    private bool? _isChecked = false;
    private bool _updatingCheck;

    internal VppArchiveNodeViewModel(
        VppBrowserViewModel owner, string archivePath, bool isSearched, bool isAdHoc)
    {
        _owner = owner;
        ArchivePath = archivePath;
        IsSearched = isSearched;
        IsAdHoc = isAdHoc;
        _label = Path.GetFileName(archivePath);
        // A placeholder child is what makes WPF draw an expander before anything is loaded.
        Children = [new VppLoadingNodeViewModel()];
    }

    /// <summary>Full path of the .vpp on disk.</summary>
    public string ArchivePath { get; }

    /// <summary>The archive's file name.</summary>
    public string ArchiveName => Path.GetFileName(ArchivePath);

    /// <summary>True when names inside this archive resolve without being extracted.</summary>
    public bool IsSearched { get; }

    /// <summary>True when the user opened this archive by hand rather than finding it on the path.</summary>
    public bool IsAdHoc { get; }

    /// <summary>The group this archive belongs to, whether or not it is listed under it yet.</summary>
    internal VppSourceNodeViewModel? Group { get; set; }

    /// <summary>Where this archive comes in its group's order, so a listed row lands in the right place.</summary>
    internal int OrderInGroup { get; set; }

    /// <summary>True once the row is actually in the tree.</summary>
    internal bool IsListed { get; set; }

    /// <inheritdoc />
    public override string Label => _label;

    /// <inheritdoc />
    public override string Description => IsSearched
        ? $"{ArchivePath} — the game already searches this archive"
        : $"{ArchivePath} — not on the game's search path";

    /// <summary>Every image row, in natural order, whether or not the filter is showing it.</summary>
    internal List<VppImageNodeViewModel> AllImages { get; } = [];

    /// <summary>The index, once it has been read. Null until then.</summary>
    internal VppImageIndex? Index => _index;

    /// <summary>True once the directory has been read.</summary>
    public bool IsLoaded => _index is not null;

    /// <summary>
    /// True once the archive is known to hold at least one entry <i>this browser accepts</i>. Until
    /// that is known the row is not in the tree at all: an archive with nothing to offer is never
    /// listed, and in the VBM import mode "nothing to offer" means no .vbm rather than no image.
    /// </summary>
    public bool HasImages => _hasEntries;

    /// <summary>
    /// The tri-state box on the archive row: on when every image under it is ticked, off when
    /// none is, indeterminate in between. Setting it ticks or unticks everything the filter is
    /// currently showing, which is what a user expects of a header checkbox over a filtered list.
    /// </summary>
    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_updatingCheck) return;
            bool target = value == true;
            _owner.SetChecked(VisibleImages(), target);
        }
    }

    /// <summary>The image rows the filter is currently showing.</summary>
    internal IEnumerable<VppImageNodeViewModel> VisibleImages() =>
        Children.OfType<VppImageNodeViewModel>();

    /// <summary>Re-reads the tri-state from the rows under it.</summary>
    internal void RefreshCheckState()
    {
        int total = 0, ticked = 0;
        foreach (var image in AllImages)
        {
            total++;
            if (image.IsChecked) ticked++;
        }
        bool? next = total == 0 || ticked == 0 ? false : ticked == total ? true : null;
        if (next == _isChecked) return;
        _updatingCheck = true;
        try { _isChecked = next; }
        finally { _updatingCheck = false; }
        Raise(nameof(IsChecked));
    }

    /// <summary>Called once the index has been read on a background thread.</summary>
    internal void Apply(VppImageIndex index)
    {
        _index = index;
        var entries = _owner.AcceptedEntries(index);
        _hasEntries = entries.Count > 0;
        _label = _owner.DescribeArchive(index, entries.Count);
        AllImages.Clear();
        foreach (var entry in entries) AllImages.Add(new VppImageNodeViewModel(_owner, this, entry));
        RaiseAll(nameof(Label), nameof(IsLoaded), nameof(HasImages), nameof(Description));
        RefreshCheckState();
    }

    /// <summary>Replaces the visible rows, e.g. after the filter changed.</summary>
    internal void ShowImages(IReadOnlyList<VppImageNodeViewModel> images)
    {
        // A listed archive always has images, so an empty row list can only mean the filter hid
        // them all.
        Children = images.Count > 0 ? images : IsLoaded ? [new VppEmptyNodeViewModel()] : [];
        RefreshCheckState();
    }

    /// <inheritdoc />
    protected override void OnExpanded() => _owner.EnsureArchiveLoaded(this);
}

/// <summary>One image inside an archive: the row with the tick box.</summary>
public sealed class VppImageNodeViewModel : VppNodeViewModel
{
    private readonly VppBrowserViewModel _owner;
    private bool _isChecked;

    internal VppImageNodeViewModel(
        VppBrowserViewModel owner, VppArchiveNodeViewModel archive, VppImageEntry entry)
    {
        _owner = owner;
        Archive = archive;
        Entry = entry;
    }

    /// <summary>The archive the entry lives in.</summary>
    public VppArchiveNodeViewModel Archive { get; }

    /// <summary>The entry itself.</summary>
    public VppImageEntry Entry { get; }

    /// <inheritdoc />
    public override string Label => Entry.Name;

    /// <inheritdoc />
    public override string Description =>
        $"{Entry.Name} — {FormatBytes(Entry.Size)} in {Archive.ArchiveName}";

    /// <summary>True when the name is longer than the engine's 31-character bitmap name buffer.</summary>
    public bool IsNameTooLong => Entry.Name.Length > AtxSchema.MaxBitmapNameLength;

    /// <summary>Whether this image is one of the chosen ones.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_owner.IsSingleSelect)
            {
                // Single-select: ticking one unticks whatever was ticked before.
                if (value) _owner.SelectOnly(this);
                else if (_isChecked) _owner.SetChecked([this], false);
                return;
            }
            _owner.SetChecked([this], value);
        }
    }

    /// <summary>Sets the flag without going back through the owner, which is doing the bookkeeping.</summary>
    internal void SetCheckedQuietly(bool value)
    {
        if (_isChecked == value) return;
        _isChecked = value;
        Raise(nameof(IsChecked));
    }

    /// <summary>A byte count as a person would read it.</summary>
    internal static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };
}

/// <summary>The placeholder row that gives an unopened archive its expander.</summary>
public sealed class VppLoadingNodeViewModel : VppNodeViewModel
{
    /// <inheritdoc />
    public override string Label => "Reading…";

    /// <inheritdoc />
    public override string Description => "Reading this archive's directory";
}

/// <summary>The row an archive shows when the filter has hidden everything it holds.</summary>
public sealed class VppEmptyNodeViewModel : VppNodeViewModel
{
    /// <inheritdoc />
    public override string Label => "Nothing matches the filter";

    /// <inheritdoc />
    public override string Description => Label;
}

// ── The dialog's view-model ───────────────────────────────────────────────────

/// <summary>
/// Add Frames from VPP…: a tree of the .vpp archives the document's resolver can see, an image
/// preview, and the choice of whether a chosen entry is referred to by name or written out next to
/// the .atx.
///
/// Everything expensive happens off the UI thread. Archive directories are read through the
/// resolver's own cache, so a stock <c>tables.vpp</c> with thousands of entries is parsed once for
/// the whole session rather than once per image; filtering runs over a flat array of names on a
/// worker and only the resulting row lists are swapped in; and the preview decodes through the same
/// budgeted <see cref="ImageDecoder"/> path the rest of the app uses, cancelling the previous decode
/// whenever the focus moves.
/// </summary>
public sealed class VppBrowserViewModel : ObservableObject, IDisposable
{
    /// <summary>How long after the last keystroke the filter is applied.</summary>
    private const int FilterDebounceMs = 200;

    /// <summary>How many decoded previews are kept. They are full-size images, so not many.</summary>
    private const int PreviewCacheSize = 12;

    private readonly DocumentViewModel _document;
    private readonly List<VppArchiveNodeViewModel> _archives = [];
    private readonly List<(VppSourceNodeViewModel Group, List<VppArchiveNodeViewModel> Members)> _groups = [];
    private readonly HashSet<VppImageNodeViewModel> _checked = [];
    private readonly List<string> _adHocArchives = [];
    private readonly Dictionary<string, BitmapSource?> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _previewOrder = new();

    private CancellationTokenSource _preview = new();
    private CancellationTokenSource _filter = new();
    private DispatcherTimer? _filterDebounce;

    private string _query = string.Empty;
    private string _progressText = string.Empty;
    private string _adHocNote = string.Empty;
    private string? _awaitingAdHoc;
    private VppImageNodeViewModel? _focused;
    private VppImageNodeViewModel? _rangeAnchor;
    private ImageInfo? _focusedInfo;
    private BitmapSource? _previewImage;
    private string _previewFacts = string.Empty;
    private string _previewPlaceholder = "Pick an image to see it here.";
    private string _mismatchNote = string.Empty;
    private SequenceInsertMode _insertMode = SequenceInsertMode.AfterSelection;
    private VppReferenceMode _referenceMode = VppReferenceMode.ReferenceByName;
    private bool _alsoExtract;
    private bool _isLoading;

    internal VppBrowserViewModel(DocumentViewModel document, VppBrowserMode mode)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        Mode = mode;
        _insertMode = document.Frames.HasSelection
            ? SequenceInsertMode.AfterSelection
            : SequenceInsertMode.AtEnd;

        ClearSelectionCommand = new RelayCommand(ClearSelection, () => _checked.Count > 0);
        SelectSequenceCommand = new RelayCommand(SelectSequence, () => _focused is not null);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        ClearFilterCommand = new RelayCommand(() => Query = string.Empty, () => _query.Length > 0);
        ImportAsAtxCommand = new RelayCommand(RequestImport, () => CanImportFocusedAsAtx);

        BuildTree();
        StartIndexing();
    }

    /// <summary>What the browser is being used for.</summary>
    public VppBrowserMode Mode { get; }

    /// <summary>True in the multi-select Add Frames mode.</summary>
    public bool IsAddFramesMode => Mode == VppBrowserMode.AddFrames;

    /// <summary>True in either of the modes that pick exactly one entry.</summary>
    public bool IsSingleSelect => Mode is VppBrowserMode.PickOne or VppBrowserMode.PickVbm;

    /// <summary>True in the mode that only lists <c>.vbm</c> entries.</summary>
    public bool IsVbmMode => Mode == VppBrowserMode.PickVbm;

    /// <summary>
    /// The entries of one archive this browser will show. Everything readable, except in the VBM
    /// import mode, where anything that is not a .vbm is not a thing that can be imported.
    /// </summary>
    /// <param name="index">The archive's index.</param>
    internal IReadOnlyList<VppImageEntry> AcceptedEntries(VppImageIndex index) => IsVbmMode
        ? [.. index.Images.Where(e => e.Name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase))]
        : index.Images;

    /// <summary>The line an archive row shows, counting what this browser accepts.</summary>
    /// <param name="index">The archive's index.</param>
    /// <param name="count">How many of its entries were accepted.</param>
    internal string DescribeArchive(VppImageIndex index, int count)
    {
        if (!IsVbmMode) return index.Describe();
        return count == 0
            ? $"{index.ArchiveName} — no VBMs"
            : $"{index.ArchiveName} — {count:N0} VBM{(count == 1 ? "" : "s")}";
    }

    /// <summary>The tree's top-level rows, in the resolver's search order.</summary>
    public ObservableCollection<VppNodeViewModel> Roots { get; } = [];

    /// <summary>
    /// True when there is nothing to browse. Not while the indexing pass is still running: the
    /// rows appear as their archives are read, and flashing "nothing here" in the meantime would
    /// be wrong as often as it is right. The progress line covers that wait.
    /// </summary>
    public bool IsTreeEmpty => Roots.Count == 0 && !_isLoading;

    /// <summary>What the empty state says.</summary>
    public string EmptyStateText
    {
        get
        {
            string wanted = IsVbmMode ? "VBM files" : "images";
            if (_archives.Count > 0)
            {
                // Archives were found and read; every one of them turned out to hold nothing this
                // browser is looking for.
                return _archives.Count == 1
                    ? $"The one .vpp archive Cairn can see holds no {wanted}. You can still "
                      + "open another from anywhere on disk."
                    : $"None of the {_archives.Count} .vpp archives Cairn can see holds any "
                      + $"{wanted}. You can still open one from anywhere on disk.";
            }
            return _document.Shell.Settings.GameDirectory is null
                ? "No .vpp archives were found, and the Red Faction folder is not set. Point ATX "
                  + "Workbench at the game and its textures become browsable here."
                : "No .vpp archives were found next to this .atx, in your search folders, or in the "
                  + "Red Faction folder. You can still open one from anywhere on disk.";
        }
    }

    /// <summary>
    /// What an archive the user opened by hand turned out to be, when it was nothing useful —
    /// "'x.vpp' contains no images." An archive that holds images simply appears in the tree, so
    /// this stays empty.
    /// </summary>
    public string AdHocNote
    {
        get => _adHocNote;
        private set { if (Set(ref _adHocNote, value)) Raise(nameof(HasAdHocNote)); }
    }

    /// <summary>True when <see cref="AdHocNote"/> has something to say.</summary>
    public bool HasAdHocNote => _adHocNote.Length > 0;

    // ── Filter ────────────────────────────────────────────────────────────────

    /// <summary>What the filter box holds. Applied after a short pause, off the UI thread.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (!Set(ref _query, value ?? string.Empty)) return;
            ClearFilterCommand.RaiseCanExecuteChanged();
            Raise(nameof(HasQuery));
            ScheduleFilter();
        }
    }

    /// <summary>True when the filter box has something in it.</summary>
    public bool HasQuery => _query.Length > 0;

    /// <summary>Clears the filter box.</summary>
    public RelayCommand ClearFilterCommand { get; }

    /// <summary>"Reading 12 of 31 archives…", or empty when nothing is in flight.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set { if (Set(ref _progressText, value)) Raise(nameof(HasProgress)); }
    }

    /// <summary>True while <see cref="ProgressText"/> has something to say.</summary>
    public bool HasProgress => _progressText.Length > 0;

    // ── Selection ─────────────────────────────────────────────────────────────

    /// <summary>"12 images selected", the running count under the tree.</summary>
    public string SelectionText
    {
        get
        {
            int count = _checked.Count;
            if (count == 0) return "No images selected.";
            return string.Format(
                CultureInfo.CurrentCulture, "{0} image{1} selected.", count, count == 1 ? "" : "s");
        }
    }

    /// <summary>True when at least one image is ticked.</summary>
    public bool HasSelection => _checked.Count > 0;

    /// <summary>Unticks everything.</summary>
    public RelayCommand ClearSelectionCommand { get; }

    /// <summary>
    /// Ticks the rest of the focused image's numbered run inside its own archive. Uses the same
    /// detection as Add Sequence, over the archive's entry names rather than a folder listing.
    /// </summary>
    public RelayCommand SelectSequenceCommand { get; }

    /// <summary>Opens Settings, from the empty state's "Set game directory…".</summary>
    public RelayCommand OpenSettingsCommand { get; }

    /// <summary>The row the preview is showing.</summary>
    public VppImageNodeViewModel? Focused => _focused;

    /// <summary>Called by the view when the tree's focus moves.</summary>
    /// <param name="node">The newly focused row.</param>
    public void OnFocusChanged(VppNodeViewModel? node)
    {
        if (_focused is { } previous) previous.IsFocusedNode = false;
        _focused = node as VppImageNodeViewModel;
        if (_focused is { } current) current.IsFocusedNode = true;
        _rangeAnchor ??= _focused;
        SelectSequenceCommand.RaiseCanExecuteChanged();
        RaiseAll(nameof(Focused), nameof(NameTooLongText), nameof(HasNameTooLong), nameof(CanApply),
            nameof(Blocker));
        StartPreview(_focused);
    }

    private void RaiseImportHint()
    {
        RaiseAll(nameof(CanImportFocusedAsAtx), nameof(ImportHintText));
        ImportAsAtxCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Ticks or unticks a set of rows and keeps every derived count in step.</summary>
    /// <param name="images">The rows to change.</param>
    /// <param name="value">True to tick, false to untick.</param>
    internal void SetChecked(IEnumerable<VppImageNodeViewModel> images, bool value)
    {
        var touched = new HashSet<VppArchiveNodeViewModel>();
        foreach (var image in images)
        {
            image.SetCheckedQuietly(value);
            if (value) _checked.Add(image);
            else _checked.Remove(image);
            touched.Add(image.Archive);
        }
        foreach (var archive in touched) archive.RefreshCheckState();
        RaiseSelection();
    }

    /// <summary>Makes one image the only ticked one — the single-select mode's whole behaviour.</summary>
    /// <param name="image">The image to tick.</param>
    internal void SelectOnly(VppImageNodeViewModel image)
    {
        var previous = _checked.ToList();
        SetChecked(previous, false);
        SetChecked([image], true);
    }

    /// <summary>
    /// Ticks everything between the last anchor and <paramref name="image"/>, within one archive.
    /// A range that crosses archives is not a range: the two lists are independent.
    /// </summary>
    /// <param name="image">The far end of the range.</param>
    public void SelectRangeTo(VppImageNodeViewModel image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (IsSingleSelect) { SelectOnly(image); return; }
        if (_rangeAnchor is not { } anchor || !ReferenceEquals(anchor.Archive, image.Archive))
        {
            SetChecked([image], true);
            _rangeAnchor = image;
            return;
        }
        var visible = image.Archive.VisibleImages().ToList();
        int from = visible.IndexOf(anchor);
        int to = visible.IndexOf(image);
        if (from < 0 || to < 0) { SetChecked([image], true); return; }
        if (from > to) (from, to) = (to, from);
        SetChecked(visible.GetRange(from, to - from + 1), true);
    }

    /// <summary>Sets the anchor a Shift+click range is measured from.</summary>
    /// <param name="image">The row that was clicked without Shift.</param>
    public void SetRangeAnchor(VppImageNodeViewModel? image) => _rangeAnchor = image;

    /// <summary>Ticks every image the filter is showing in the focused row's archive (Ctrl+A).</summary>
    public void SelectAllVisibleInFocusedArchive()
    {
        if (IsSingleSelect) return;
        var archive = _focused?.Archive ?? _archives.FirstOrDefault(a => a.IsExpanded && a.IsLoaded);
        if (archive is null) return;
        SetChecked(archive.VisibleImages().ToList(), true);
    }

    /// <summary>Toggles the focused row (Space).</summary>
    public void ToggleFocused()
    {
        if (_focused is not { } image) return;
        if (IsSingleSelect) SelectOnly(image);
        else SetChecked([image], !image.IsChecked);
    }

    private void ClearSelection() => SetChecked(_checked.ToList(), false);

    private void SelectSequence()
    {
        if (_focused is not { } image || image.Archive.Index is not { } index) return;
        var run = FrameSequence.DetectInNames(image.Entry.Name, index.Names);
        var wanted = new HashSet<string>(run, StringComparer.OrdinalIgnoreCase);
        var rows = image.Archive.AllImages.Where(i => wanted.Contains(i.Entry.Name)).ToList();
        if (IsSingleSelect) return;
        SetChecked(rows, true);
    }

    private void RaiseSelection()
    {
        RaiseAll(nameof(SelectionText), nameof(HasSelection), nameof(CanApply), nameof(Blocker),
            nameof(ReferenceHint), nameof(NameTooLongText), nameof(HasNameTooLong));
        ClearSelectionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// The chosen images in the order they become frames: natural order within an archive, archives
    /// in tree order. The dialog says so out loud, because "the order I ticked them" is the other
    /// reasonable guess and it is not what happens.
    /// </summary>
    public IReadOnlyList<VppImageNodeViewModel> ChosenImages
    {
        get
        {
            var chosen = new List<VppImageNodeViewModel>(_checked.Count);
            foreach (var archive in _archives)
            {
                foreach (var image in archive.AllImages)
                {
                    if (_checked.Contains(image)) chosen.Add(image);
                }
            }
            return chosen;
        }
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    /// <summary>The decoded image, or null while it loads or when it could not be shown.</summary>
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set { if (Set(ref _previewImage, value)) Raise(nameof(HasPreviewImage)); }
    }

    /// <summary>True when there is a picture to draw.</summary>
    public bool HasPreviewImage => _previewImage is not null;

    /// <summary>The facts beneath the preview: dimensions, format, mips, archive, size.</summary>
    public string PreviewFacts
    {
        get => _previewFacts;
        private set { if (Set(ref _previewFacts, value)) Raise(nameof(HasPreviewFacts)); }
    }

    /// <summary>True when there are facts to show.</summary>
    public bool HasPreviewFacts => _previewFacts.Length > 0;

    /// <summary>What the preview pane says when there is no picture.</summary>
    public string PreviewPlaceholder
    {
        get => _previewPlaceholder;
        private set => Set(ref _previewPlaceholder, value);
    }

    /// <summary>
    /// The note shown when the focused image would not load as a frame of this texture — the same
    /// comparison, and the same phrasing, the linter's ATX011 uses.
    /// </summary>
    public string MismatchNote
    {
        get => _mismatchNote;
        private set { if (Set(ref _mismatchNote, value)) Raise(nameof(HasMismatchNote)); }
    }

    /// <summary>True when <see cref="MismatchNote"/> has something to say.</summary>
    public bool HasMismatchNote => _mismatchNote.Length > 0;

    /// <summary>The inline warning about entry names the engine cannot hold.</summary>
    public string NameTooLongText
    {
        get
        {
            IReadOnlyList<VppImageNodeViewModel> candidates = IsAddFramesMode
                ? ChosenImages
                : _focused is null ? [] : [_focused];
            var offenders = candidates
                .Where(i => i.IsNameTooLong)
                .Select(i => i.Entry.Name)
                .Take(3)
                .ToList();
            if (offenders.Count == 0) return string.Empty;
            return $"{string.Join(", ", offenders)} — longer than "
                + $"{AtxSchema.MaxBitmapNameLength} characters, which is more than the engine's "
                + "bitmap name can hold. The game will not find these.";
        }
    }

    /// <summary>True when <see cref="NameTooLongText"/> has something to say.</summary>
    public bool HasNameTooLong => NameTooLongText.Length > 0;

    private void StartPreview(VppImageNodeViewModel? image)
    {
        _preview.Cancel();
        _preview.Dispose();
        _preview = new CancellationTokenSource();
        MismatchNote = string.Empty;
        _focusedInfo = null;
        RaiseImportHint();

        if (image is null)
        {
            PreviewImage = null;
            PreviewFacts = string.Empty;
            PreviewPlaceholder = "Pick an image to see it here.";
            return;
        }

        string key = image.Archive.ArchivePath + "|" + image.Entry.Name;
        if (TryTakePreview(key, out var cached))
        {
            PreviewImage = cached;
            PreviewPlaceholder = cached is null ? "This image could not be shown." : string.Empty;
        }
        else
        {
            PreviewImage = null;
            PreviewPlaceholder = "Reading…";
        }
        PreviewFacts = string.Empty;
        _ = LoadPreviewAsync(image, key, _preview.Token);
    }

    private async Task LoadPreviewAsync(
        VppImageNodeViewModel image, string key, CancellationToken token)
    {
        string archivePath = image.Archive.ArchivePath;
        string name = image.Entry.Name;
        int size = image.Entry.Size;

        DecodedEntry result;
        try
        {
            result = await Task.Run(() => DecodeEntry(archivePath, name), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested) return;

        StorePreview(key, result.Bitmap);
        PreviewImage = result.Bitmap;
        PreviewPlaceholder = result.Bitmap is null
            ? result.Error ?? "This image could not be shown."
            : string.Empty;
        _focusedInfo = result.Info;
        PreviewFacts = DescribeFacts(name, size, image.Archive.ArchiveName, result.Info, result.Fps);
        MismatchNote = DescribeMismatch(result.Info);
        RaiseImportHint();
    }

    /// <summary>One decoded entry, and the VBM facts a still image does not have.</summary>
    /// <param name="Bitmap">The picture, or null when it could not be shown.</param>
    /// <param name="Info">Its header, or null.</param>
    /// <param name="Fps">The VBM's frame rate, for an animated one.</param>
    /// <param name="Error">Why there is no picture.</param>
    private sealed record DecodedEntry(
        BitmapSource? Bitmap, ImageInfo? Info, int? Fps, string? Error);

    /// <summary>
    /// Reads one entry and decodes it, on a worker thread. The archive comes out of the resolver's
    /// cache, so this does not re-read a directory of several thousand entries per preview.
    /// </summary>
    private DecodedEntry DecodeEntry(string archivePath, string name)
    {
        try
        {
            var archive = _document.Resolver.OpenCachedArchive(archivePath);
            if (archive is null || !archive.TryGetEntry(name, out var entry))
                return new DecodedEntry(null, null, null, "That entry is no longer in the archive.");

            byte[] bytes;
            using (var stream = archive.OpenEntry(entry))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }

            var info = ImageProbe.Probe(bytes, name);
            // The frame rate is the one fact a VBM carries that ImageInfo has no room for, and it
            // is exactly what someone deciding whether to import one wants to see.
            int? fps = info.Container == ImageContainer.Vbm
                ? VbmCodec.ReadInfo(bytes, name).Fps
                : null;
            var decoded = ImageDecoder.Decode(bytes, name);
            var bitmap = BitmapSource.Create(
                decoded.Width, decoded.Height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, decoded.Pixels, decoded.Stride);
            bitmap.Freeze();
            return new DecodedEntry(bitmap, info, fps, null);
        }
        catch (ImageTooLargeException ex) { return new DecodedEntry(null, null, null, ex.Message); }
        catch (ImageDecodeException ex) { return new DecodedEntry(null, null, null, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or VppFormatException or NotSupportedException)
        {
            return new DecodedEntry(null, null, null, $"'{name}' could not be read: {ex.Message}");
        }
    }

    private string DescribeFacts(string name, int size, string archiveName, ImageInfo? info, int? fps)
    {
        if (info is null) return $"{name} · {VppImageNodeViewModel.FormatBytes(size)} · {archiveName}";
        string facts = $"{name}{Environment.NewLine}{info.Describe()}"
            + $"{Environment.NewLine}{archiveName} · {VppImageNodeViewModel.FormatBytes(size)}";
        if (info.Container == ImageContainer.Vbm && info.FrameCount is > 1)
        {
            string rate = fps is > 0 ? $" at {fps} fps" : string.Empty;
            facts += Environment.NewLine + (IsVbmMode
                ? $"Animated VBM — {info.FrameCount} frames{rate}."
                : $"Animated VBM ({info.FrameCount} frames{rate}) — frame 0 will be used.");
        }
        return facts;
    }

    /// <summary>
    /// The "doesn't match this texture's frames" line. The rule and the wording come from
    /// <see cref="ImageComparison"/>, which is the same code ATX011 uses, so this can never say
    /// something the Problems panel would contradict a second later.
    /// </summary>
    private string DescribeMismatch(ImageInfo? info)
    {
        if (info is null) return string.Empty;
        var reference = _document.Assets?.Frames.FirstOrDefault(f => f.Info is not null)?.Info;
        if (reference is null) return string.Empty;

        var problems = ImageComparison.Differences(info, reference, "frame 0", out bool certain);
        if (problems.Count == 0) return string.Empty;
        return "Doesn't match this texture's frames: " + string.Join("; ", problems) + ". "
            + (certain ? "The game would refuse to load the .atx." : "The game may refuse to load the .atx.");
    }

    private bool TryTakePreview(string key, out BitmapSource? value)
    {
        if (!_previewCache.TryGetValue(key, out value)) return false;
        _previewOrder.Remove(key);
        _previewOrder.AddLast(key);
        return true;
    }

    private void StorePreview(string key, BitmapSource? value)
    {
        _previewCache[key] = value;
        _previewOrder.Remove(key);
        _previewOrder.AddLast(key);
        while (_previewOrder.Count > PreviewCacheSize)
        {
            var oldest = _previewOrder.First!;
            _previewOrder.RemoveFirst();
            _previewCache.Remove(oldest.Value);
        }
    }

    // ── Options ───────────────────────────────────────────────────────────────

    /// <summary>Where the new frames go. Same three choices, and wording, as Add Sequence.</summary>
    public SequenceInsertMode InsertMode
    {
        get => _insertMode;
        set
        {
            if (!Set(ref _insertMode, value)) return;
            RaiseAll(nameof(IsAfterSelection), nameof(IsAtEnd), nameof(IsReplaceAll));
        }
    }

    public bool IsAfterSelection
    {
        get => _insertMode == SequenceInsertMode.AfterSelection;
        set { if (value) InsertMode = SequenceInsertMode.AfterSelection; }
    }

    public bool IsAtEnd
    {
        get => _insertMode == SequenceInsertMode.AtEnd;
        set { if (value) InsertMode = SequenceInsertMode.AtEnd; }
    }

    public bool IsReplaceAll
    {
        get => _insertMode == SequenceInsertMode.ReplaceAll;
        set { if (value) InsertMode = SequenceInsertMode.ReplaceAll; }
    }

    /// <summary>How the chosen entries reach the frames.</summary>
    public VppReferenceMode ReferenceMode
    {
        get => _referenceMode;
        set
        {
            if (!Set(ref _referenceMode, value)) return;
            RaiseAll(nameof(IsReferenceByName), nameof(IsExtract), nameof(IsAddFolderToSearch),
                nameof(CanAlsoExtract), nameof(ReferenceHint), nameof(CanApply), nameof(Blocker));
        }
    }

    public bool IsReferenceByName
    {
        get => _referenceMode == VppReferenceMode.ReferenceByName;
        set { if (value) ReferenceMode = VppReferenceMode.ReferenceByName; }
    }

    public bool IsExtract
    {
        get => _referenceMode == VppReferenceMode.Extract;
        set { if (value) ReferenceMode = VppReferenceMode.Extract; }
    }

    public bool IsAddFolderToSearch
    {
        get => _referenceMode == VppReferenceMode.AddFolderToSearch;
        set { if (value) ReferenceMode = VppReferenceMode.AddFolderToSearch; }
    }

    /// <summary>"Also extract copies next to the .atx", offered alongside Reference by name.</summary>
    public bool AlsoExtract
    {
        get => _alsoExtract;
        set => Set(ref _alsoExtract, value);
    }

    /// <summary>True when the checkbox applies at all.</summary>
    public bool CanAlsoExtract => IsReferenceByName && CanExtract;

    /// <summary>
    /// True when there is anywhere to extract to. An unsaved document with no image added yet has
    /// no ATX folder, so extraction is impossible until it is saved; referencing by name still is.
    /// </summary>
    public bool CanExtract => _document.AtxFolder is not null;

    /// <summary>Why extraction is unavailable, or empty when it is available.</summary>
    public string ExtractBlockedText => CanExtract
        ? string.Empty
        : "Save the .atx first — until it has a folder there is nowhere to put the extracted images.";

    /// <summary>The sentence under the reference options, explaining what will happen.</summary>
    public string ReferenceHint
    {
        get
        {
            bool allSearched = ChosenArchives().All(a => a.IsSearched);
            return _referenceMode switch
            {
                VppReferenceMode.ReferenceByName when allSearched =>
                    "Frames will store just the name; the game finds each image inside the .vpp.",
                VppReferenceMode.ReferenceByName =>
                    "This archive is not on the game's search path, so the frames will show as not "
                    + "found until you extract them or add the folder below.",
                VppReferenceMode.Extract =>
                    "Each chosen image is written next to the .atx, and the frames refer to those files.",
                _ => "The archive's folder is added to your search folders, and the frames refer to "
                    + "the images by name.",
            };
        }
    }

    /// <summary>True when the "add this archive's folder" option is worth offering.</summary>
    public bool CanAddFolderToSearch => ChosenArchives().Any(a => !a.IsSearched);

    /// <summary>The line explaining the order frames are added in.</summary>
    public static string OrderNote => "Frames are added in name order, archive by archive.";

    // ── Applying ──────────────────────────────────────────────────────────────

    /// <summary>False when OK cannot do anything useful.</summary>
    public bool CanApply => IsSingleSelect
        ? _focused is not null
        : _checked.Count > 0 && (_referenceMode != VppReferenceMode.Extract || CanExtract);

    /// <summary>A sentence explaining why OK is disabled, or null when it is not.</summary>
    public string? Blocker
    {
        get
        {
            if (CanApply) return null;
            if (IsVbmMode) return "Pick a .vbm.";
            if (IsSingleSelect) return "Pick an image.";
            if (_checked.Count == 0) return "Tick at least one image.";
            return ExtractBlockedText;
        }
    }

    /// <summary>The name the single-select mode returns, or null.</summary>
    public string? PickedName => _focused?.Entry.Name;

    /// <summary>The entry the VBM mode returns, with the archive it lives in.</summary>
    public (string ArchivePath, string EntryName)? PickedEntry =>
        _focused is { } image ? (image.Archive.ArchivePath, image.Entry.Name) : null;

    // ── "Import as ATX…" ──────────────────────────────────────────────────────

    /// <summary>
    /// The entry the user asked to import instead of adding as a frame, or null. Set by the inline
    /// link on a multi-frame VBM, which closes the browser; the dialog service then opens the
    /// import dialog, so the two modals never overlap.
    /// </summary>
    public (string ArchivePath, string EntryName)? ImportRequest { get; private set; }

    /// <summary>Closes the browser and asks for this entry to be imported as an .atx.</summary>
    public RelayCommand ImportAsAtxCommand { get; }

    /// <summary>
    /// True when the focused entry is an animated VBM in the Add Frames browser — the one case
    /// where what the user almost certainly wants is not what this dialog does. Adding it as a
    /// frame gets them frame 0 and nothing else.
    /// </summary>
    public bool CanImportFocusedAsAtx => IsAddFramesMode
        && _focusedInfo is { Container: ImageContainer.Vbm, FrameCount: > 1 };

    /// <summary>The inline hint shown beside that link.</summary>
    public string ImportHintText
    {
        get
        {
            if (!CanImportFocusedAsAtx || _focusedInfo?.FrameCount is not { } frames)
                return string.Empty;
            return string.Format(
                CultureInfo.CurrentCulture,
                "This is an animated VBM with {0} frames. Added as a frame it contributes frame 0 "
                + "and nothing else; importing it turns all {0} frames into an .atx of their own.",
                frames);
        }
    }

    private void RequestImport()
    {
        if (!CanImportFocusedAsAtx || _focused is not { } image) return;
        ImportRequest = (image.Archive.ArchivePath, image.Entry.Name);
        ImportRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the browser should close because an import was asked for.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>
    /// Carries out the choice. Extraction happens first, because it is the part that can fail or be
    /// cancelled; only once it has settled does the document change, so a cancelled batch leaves the
    /// .atx untouched. Returns false when nothing was applied.
    /// </summary>
    public bool Apply()
    {
        if (!CanApply) return false;

        // Importing a VBM writes nothing into this document, so none of the extraction or insertion
        // machinery below applies: the picked entry simply goes back to the caller.
        if (IsVbmMode) return _focused is not null;

        var chosen = IsSingleSelect
            ? (_focused is null ? [] : new List<VppImageNodeViewModel> { _focused })
            : [.. ChosenImages];
        if (chosen.Count == 0) return false;

        if (_referenceMode == VppReferenceMode.AddFolderToSearch && !AddArchiveFoldersToSearch(chosen))
            return false;

        bool extract = _referenceMode == VppReferenceMode.Extract
            || (_referenceMode == VppReferenceMode.ReferenceByName && _alsoExtract);
        if (extract && CanExtract && !ExtractChosen(chosen)) return false;

        if (IsSingleSelect) return true;

        // The document may have been re-parsed, or reloaded from disk, while the dialog was up, so
        // the insert position is worked out against the file as it is now.
        _document.EnsureParsed();
        int insert = _insertMode switch
        {
            SequenceInsertMode.AfterSelection when _document.Frames.HasSelection =>
                Math.Min(_document.Frames.SelectedIndices[^1] + 1, _document.FrameCount),
            SequenceInsertMode.ReplaceAll => 0,
            _ => -1,
        };
        _document.AddImageNames(
            [.. chosen.Select(c => c.Entry.Name)], insert,
            replaceAll: _insertMode == SequenceInsertMode.ReplaceAll);
        return true;
    }

    private bool ExtractChosen(IReadOnlyList<VppImageNodeViewModel> chosen)
    {
        var items = new List<(VppArchive Archive, VppEntry Entry)>();
        foreach (var image in chosen)
        {
            var archive = _document.Resolver.OpenCachedArchive(image.Archive.ArchivePath);
            if (archive?.TryGetEntry(image.Entry.Name, out var entry) == true)
                items.Add((archive, entry));
        }
        return _document.ExtractArchiveEntries(items);
    }

    private bool AddArchiveFoldersToSearch(IReadOnlyList<VppImageNodeViewModel> chosen)
    {
        var folders = chosen
            .Where(c => !c.Archive.IsSearched)
            .Select(c => Path.GetDirectoryName(c.Archive.ArchivePath))
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string? folder in folders) _document.Shell.AddSearchFolder(folder!);
        return true;
    }

    private IEnumerable<VppArchiveNodeViewModel> ChosenArchives()
    {
        if (IsSingleSelect)
        {
            if (_focused is { } focused) yield return focused.Archive;
            yield break;
        }
        var seen = new HashSet<VppArchiveNodeViewModel>();
        foreach (var image in _checked)
        {
            if (seen.Add(image.Archive)) yield return image.Archive;
        }
    }

    // ── Building and loading the tree ─────────────────────────────────────────

    /// <summary>
    /// Builds every group and archive row, but puts none of them in the tree: a row appears only
    /// once its index says it holds images, which <see cref="ShowArchive"/> does. Archives that
    /// turn out to hold none are never listed at all, and a group left with nothing stays out too.
    /// </summary>
    private void BuildTree()
    {
        Roots.Clear();
        _archives.Clear();
        _groups.Clear();

        foreach (var group in _document.Resolver.DescribeArchiveSources())
        {
            if (group.ArchivePaths.Count == 0) continue;
            var node = new VppSourceNodeViewModel(group.Label, group.Folder, group.IsSearched);
            var members = new List<VppArchiveNodeViewModel>();
            foreach (string path in group.ArchivePaths)
            {
                var archive = new VppArchiveNodeViewModel(this, path, group.IsSearched, isAdHoc: false)
                {
                    Group = node,
                    OrderInGroup = members.Count,
                };
                members.Add(archive);
                _archives.Add(archive);
            }
            _groups.Add((node, members));
        }

        if (_adHocArchives.Count > 0)
        {
            var node = new VppSourceNodeViewModel(
                "Archives you opened", "Opened from disk, not on the game's search path", isSearched: false);
            var members = new List<VppArchiveNodeViewModel>();
            foreach (string path in _adHocArchives)
            {
                var archive = new VppArchiveNodeViewModel(this, path, isSearched: false, isAdHoc: true)
                {
                    Group = node,
                    OrderInGroup = members.Count,
                };
                members.Add(archive);
                _archives.Add(archive);
            }
            _groups.Add((node, members));
        }

        RaiseAll(nameof(IsTreeEmpty), nameof(EmptyStateText));
    }

    /// <summary>
    /// Puts one archive row into the tree, in its group's own order, adding the group if this is
    /// the first archive in it with anything to show. Called once per archive as the indexing pass
    /// lands, so rows only ever appear — never appear and then vanish.
    /// </summary>
    private void ShowArchive(VppArchiveNodeViewModel archive)
    {
        if (archive.IsListed || archive.Group is not { } group) return;

        // The listed rows stay in the group's own order, so the new one goes before the first row
        // that started out after it. A search folder can hold hundreds of archives, so this walks
        // the listed rows rather than re-scanning the whole membership per insertion.
        int at = group.Archives.Count;
        for (int i = 0; i < group.Archives.Count; i++)
        {
            if (group.Archives[i] is VppArchiveNodeViewModel row && row.OrderInGroup > archive.OrderInGroup)
            {
                at = i;
                break;
            }
        }
        group.Archives.Insert(at, archive);
        archive.IsListed = true;

        if (!Roots.Contains(group))
        {
            int rootAt = 0;
            foreach (var (candidate, _) in _groups)
            {
                if (ReferenceEquals(candidate, group)) break;
                if (Roots.Contains(candidate)) rootAt++;
            }
            Roots.Insert(rootAt, group);
        }
        RaiseAll(nameof(IsTreeEmpty), nameof(EmptyStateText));
    }

    /// <summary>
    /// Adds an archive the user picked from anywhere on disk. It goes in its own group, is never
    /// treated as searched, and so defaults to being extracted rather than referenced.
    /// </summary>
    /// <param name="path">Full path of the .vpp.</param>
    public void OpenArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            return;
        }
        if (_archives.Any(a => string.Equals(a.ArchivePath, full, StringComparison.OrdinalIgnoreCase)))
            return;

        _adHocArchives.Add(full);
        AdHocNote = string.Empty;
        // Whether this one is worth listing is not known until its directory has been read, so the
        // answer is given by AnnounceAdHoc: expand it, or say it holds nothing.
        _awaitingAdHoc = full;
        RebuildKeepingSelection();
        // An archive the user went and found is not on the search path, so referring to it by name
        // would leave every frame missing. Default to the answer that works.
        ReferenceMode = CanExtract ? VppReferenceMode.Extract : VppReferenceMode.ReferenceByName;
    }

    /// <summary>Rebuilds the tree after Settings changed, keeping what is already ticked.</summary>
    public void RefreshFromSettings() => RebuildKeepingSelection();

    private void RebuildKeepingSelection()
    {
        var ticked = _checked.Select(c => (c.Archive.ArchivePath, c.Entry.Name)).ToHashSet();
        _checked.Clear();
        _focused = null;
        _rangeAnchor = null;
        BuildTree();
        StartIndexing(ticked);
        RaiseSelection();
    }

    /// <summary>
    /// Reads every archive's directory in the background. Only the directory — the entry rows
    /// themselves are still built lazily, when an archive is opened or a filter pass reaches it —
    /// but knowing the counts up front is what lets the tree say "1,204 images" before anything is
    /// expanded, and what makes the first filter instant rather than a thirty-archive stall.
    /// </summary>
    private void StartIndexing(HashSet<(string, string)>? restore = null)
    {
        var pending = _archives.Where(a => !a.IsLoaded).ToList();
        if (pending.Count == 0)
        {
            _isLoading = false;
            ProgressText = string.Empty;
            RaiseAll(nameof(IsTreeEmpty), nameof(EmptyStateText));
            return;
        }
        _isLoading = true;
        ProgressText = $"Reading 0 of {pending.Count} archives…";
        Raise(nameof(IsTreeEmpty));
        _ = IndexAllAsync(pending, restore);
    }

    private async Task IndexAllAsync(
        List<VppArchiveNodeViewModel> pending, HashSet<(string, string)>? restore)
    {
        int done = 0;
        foreach (var node in pending)
        {
            string path = node.ArchivePath;
            VppImageIndex index;
            try
            {
                index = await Task.Run(() => LoadIndex(path)).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                index = VppImageIndex.Unreadable(path);
            }
            if (_disposed) return;
            if (!node.IsLoaded) ApplyIndex(node, index, restore);
            done++;
            ProgressText = done >= pending.Count
                ? string.Empty
                : $"Reading {done} of {pending.Count} archives…";
        }
        _isLoading = false;
        ProgressText = string.Empty;
        // Only now can the tree honestly be called empty: every archive has been read and none of
        // them had an image in it.
        RaiseAll(nameof(IsTreeEmpty), nameof(EmptyStateText));
        ApplyFilter();
    }

    private VppImageIndex LoadIndex(string path)
    {
        try
        {
            var archive = _document.Resolver.OpenCachedArchive(path);
            return archive is null ? VppImageIndex.Unreadable(path) : VppImageIndex.Build(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or VppFormatException or NotSupportedException)
        {
            return VppImageIndex.Unreadable(path);
        }
    }

    private void ApplyIndex(
        VppArchiveNodeViewModel node, VppImageIndex index, HashSet<(string, string)>? restore)
    {
        node.Apply(index);
        if (restore is not null)
        {
            var again = node.AllImages
                .Where(i => restore.Contains((node.ArchivePath, i.Entry.Name)))
                .ToList();
            if (again.Count > 0) SetChecked(again, true);
        }
        node.ShowImages(Matching(node));
        if (node.HasImages) ShowArchive(node);
        AnnounceAdHoc(node, index);
    }

    /// <summary>
    /// Says what happened to an archive the user opened by hand, once its index lands. One that
    /// holds images opens itself; one that holds none would otherwise do nothing visible at all,
    /// which is indistinguishable from the command having been ignored.
    /// </summary>
    private void AnnounceAdHoc(VppArchiveNodeViewModel node, VppImageIndex index)
    {
        if (_awaitingAdHoc is null
            || !string.Equals(_awaitingAdHoc, node.ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        _awaitingAdHoc = null;
        if (node.HasImages)
        {
            node.IsExpanded = true;
            return;
        }
        AdHocNote = index.IsUnreadable
            ? $"'{node.ArchiveName}' could not be read as a .vpp archive."
            : $"'{node.ArchiveName}' contains no {(IsVbmMode ? "VBM files" : "images")}.";
    }

    /// <summary>Makes sure one archive is loaded, for the row the user just opened.</summary>
    /// <param name="node">The archive row.</param>
    internal void EnsureArchiveLoaded(VppArchiveNodeViewModel node)
    {
        if (node.IsLoaded) return;
        _ = EnsureArchiveLoadedAsync(node);
    }

    private async Task EnsureArchiveLoadedAsync(VppArchiveNodeViewModel node)
    {
        string path = node.ArchivePath;
        var index = await Task.Run(() => LoadIndex(path)).ConfigureAwait(true);
        if (_disposed || node.IsLoaded) return;
        ApplyIndex(node, index, null);
    }

    // ── Filtering ─────────────────────────────────────────────────────────────

    private void ScheduleFilter()
    {
        _filterDebounce ??= new DispatcherTimer(DispatcherPriority.Background, _document.Shell.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FilterDebounceMs),
        };
        _filterDebounce.Tick -= OnFilterTick;
        _filterDebounce.Tick += OnFilterTick;
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnFilterTick(object? sender, EventArgs e)
    {
        _filterDebounce?.Stop();
        ApplyFilter();
    }

    /// <summary>
    /// Applies the filter to every loaded archive. Matching runs on a worker over the flat name
    /// arrays; only the resulting row lists come back to the UI thread, so a thirty-archive,
    /// ninety-thousand-entry search never touches a TreeViewItem it does not have to.
    /// </summary>
    private void ApplyFilter()
    {
        _filter.Cancel();
        _filter.Dispose();
        _filter = new CancellationTokenSource();
        _ = ApplyFilterAsync(_query, _filter.Token);
    }

    private async Task ApplyFilterAsync(string query, CancellationToken token)
    {
        var loaded = _archives.Where(a => a.IsLoaded).ToList();
        var work = loaded.Select(a => (Node: a, Names: a.Index!.Names)).ToList();

        List<HashSet<string>> matches;
        try
        {
            matches = await Task.Run(() =>
            {
                var result = new List<HashSet<string>>(work.Count);
                foreach (var (_, names) in work)
                {
                    token.ThrowIfCancellationRequested();
                    var set = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string name in names)
                    {
                        if (VppEntryFilter.Matches(name, query)) set.Add(name);
                    }
                    result.Add(set);
                }
                return result;
            }, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || _disposed) return;

        bool filtering = !string.IsNullOrWhiteSpace(query);
        for (int i = 0; i < work.Count; i++)
        {
            var node = work[i].Node;
            var wanted = matches[i];
            var rows = node.AllImages.Where(r => wanted.Contains(r.Entry.Name)).ToList();
            node.ShowImages(rows);
            // A filter that finds something opens the archive that holds it; clearing the box does
            // not slam every archive shut again, because the user may have opened some by hand.
            if (filtering && rows.Count > 0) node.IsExpanded = true;
        }

        // Archives that have not been read yet are still coming; say so rather than implying the
        // search is finished.
        if (filtering && _isLoading) return;
        ProgressText = string.Empty;
    }

    /// <summary>The rows of one archive that match the current query.</summary>
    private List<VppImageNodeViewModel> Matching(VppArchiveNodeViewModel node) =>
        string.IsNullOrWhiteSpace(_query)
            ? [.. node.AllImages]
            : [.. node.AllImages.Where(r => VppEntryFilter.Matches(r.Entry.Name, _query))];

    private void OpenSettings()
    {
        _document.Shell.SettingsCommand.Execute(null);
        RefreshFromSettings();
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_filterDebounce is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnFilterTick;
            _filterDebounce = null;
        }
        _preview.Cancel();
        _preview.Dispose();
        _filter.Cancel();
        _filter.Dispose();
    }
}
