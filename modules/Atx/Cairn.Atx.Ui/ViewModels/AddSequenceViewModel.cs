using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Sequences;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>Where the new frames go.</summary>
public enum SequenceInsertMode
{
    /// <summary>Straight after the frames currently selected in the list.</summary>
    AfterSelection,

    /// <summary>At the end of the frame list.</summary>
    AtEnd,

    /// <summary>Instead of every frame the file has now.</summary>
    ReplaceAll,
}

/// <summary>One detected file in the sequence list.</summary>
public sealed class SequenceItemViewModel : ObservableObject
{
    private bool _isIncluded = true;
    private BitmapSource? _thumbnail;

    internal SequenceItemViewModel(string name, bool exists)
    {
        Name = name;
        Exists = exists;
    }

    /// <summary>The bare file name, which is what a frame stores.</summary>
    public string Name { get; }

    /// <summary>True when the file is actually on disk.</summary>
    public bool Exists { get; }

    /// <summary>"on disk" or "not found", for the pattern tab's live check.</summary>
    public string ExistsText => Exists ? "on disk" : "not found yet";

    /// <summary>Whether this file becomes a frame.</summary>
    public bool IsIncluded
    {
        get => _isIncluded;
        set { if (Set(ref _isIncluded, value)) IncludedChanged?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>The decoded thumbnail, or null while it loads or when the file is missing.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        internal set => Set(ref _thumbnail, value);
    }

    /// <summary>Raised when the check box changes, so the dialog can recount.</summary>
    internal event EventHandler? IncludedChanged;
}

/// <summary>
/// Add Sequence…: turn a run of numbered images into frames. The "From files" tab detects the run
/// on disk from any one of its files (<see cref="FrameSequence.DetectOnDisk"/>); the "From pattern"
/// tab generates names for files an artist has not exported yet
/// (<see cref="FrameSequence.Generate"/>). Both end in the same place — the names, in order, handed
/// to the document's existing add-images path, so the copy-or-reference prompt and the single undo
/// step are the ones the rest of the app already uses.
/// </summary>
public sealed class AddSequenceViewModel : ObservableObject, IDisposable
{
    /// <summary>Longest side a sequence thumbnail is decoded to.</summary>
    private const int ThumbnailPixels = 40;

    private readonly DocumentViewModel _document;
    private CancellationTokenSource _thumbnails = new();

    private string? _sourceFolder;
    private string _sourceFile = string.Empty;
    private int _rangeStart;
    private int _rangeEnd;
    private int _step = 1;
    private bool _reverse;
    private bool _appendReversed;
    private bool _applyingRange;
    private int _tabIndex;

    private string _prefix = string.Empty;
    private int _patternStart;
    private int _patternEnd = 7;
    private int _padding = 2;
    private string _extension = ".tga";
    private int _patternStep = 1;
    private string _patternOverflowText = string.Empty;
    private System.Windows.Threading.DispatcherTimer? _patternDebounce;
    /// <summary>Every generated name, of which only the first few hundred get a preview row.</summary>
    private IReadOnlyList<string> _patternNames = [];

    private SequenceInsertMode _insertMode = SequenceInsertMode.AfterSelection;

    internal AddSequenceViewModel(DocumentViewModel document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _insertMode = document.Frames.HasSelection
            ? SequenceInsertMode.AfterSelection
            : SequenceInsertMode.AtEnd;
        RefreshPattern();
    }

    // ── Tab 1: from files ─────────────────────────────────────────────────────

    /// <summary>The detected run, in natural order.</summary>
    public ObservableCollection<SequenceItemViewModel> Items { get; } = [];

    /// <summary>The file the user picked, for the "detected from" line.</summary>
    public string SourceFile
    {
        get => _sourceFile;
        private set { if (Set(ref _sourceFile, value)) Raise(nameof(SourceSummary)); }
    }

    /// <summary>The folder the detected files live in.</summary>
    public string? SourceFolder
    {
        get => _sourceFolder;
        private set { if (Set(ref _sourceFolder, value)) Raise(nameof(SourceSummary)); }
    }

    /// <summary>What the dialog says above the list.</summary>
    public string SourceSummary => Items.Count == 0
        ? "Pick any one file of a numbered run and the rest are found automatically."
        : $"{Items.Count} file{(Items.Count == 1 ? "" : "s")} found in {SourceFolder}";

    /// <summary>True once a run has been detected.</summary>
    public bool HasItems => Items.Count > 0;

    /// <summary>First file of the range, 0-based within the detected list.</summary>
    public int RangeStart
    {
        get => _rangeStart;
        set { if (Set(ref _rangeStart, Math.Clamp(value, 0, Math.Max(0, Items.Count - 1)))) ApplyRange(); }
    }

    /// <summary>Last file of the range, 0-based within the detected list.</summary>
    public int RangeEnd
    {
        get => _rangeEnd;
        set { if (Set(ref _rangeEnd, Math.Clamp(value, 0, Math.Max(0, Items.Count - 1)))) ApplyRange(); }
    }

    /// <summary>Take every Nth file of the range.</summary>
    public int Step
    {
        get => _step;
        set { if (Set(ref _step, Math.Clamp(value, 1, 99))) ApplyRange(); }
    }

    /// <summary>Add the frames back to front.</summary>
    public bool Reverse
    {
        get => _reverse;
        set { if (Set(ref _reverse, value)) RaiseResult(); }
    }

    /// <summary>
    /// Append the run in reverse, minus the endpoints, so a Loop texture ping-pongs without using
    /// the Ping-Pong mode — which is what "manual ping-pong" means.
    /// </summary>
    public bool AppendReversed
    {
        get => _appendReversed;
        set { if (Set(ref _appendReversed, value)) RaiseResult(); }
    }

    /// <summary>Which tab is in front: 0 from files, 1 from pattern.</summary>
    public int TabIndex
    {
        get => _tabIndex;
        set { if (Set(ref _tabIndex, value)) RaiseResult(); }
    }

    /// <summary>Detects the run that <paramref name="filePath"/> belongs to.</summary>
    public void LoadFrom(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        CancelThumbnails();
        Items.Clear();

        string folder;
        IReadOnlyList<string> names;
        try
        {
            folder = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
            names = FrameSequence.DetectOnDisk(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return;
        }

        SourceFolder = folder;
        SourceFile = Path.GetFileName(filePath);
        foreach (string name in names)
        {
            var item = new SequenceItemViewModel(name, exists: true);
            item.IncludedChanged += (_, _) => RaiseResult();
            Items.Add(item);
        }

        _rangeStart = 0;
        _rangeEnd = Math.Max(0, Items.Count - 1);
        _step = 1;
        RaiseAll(nameof(RangeStart), nameof(RangeEnd), nameof(Step), nameof(HasItems),
            nameof(SourceSummary), nameof(MaxIndex));
        ApplyRange();
        LoadThumbnails();

        // A pattern that matches the picked file is a sensible starting point for the other tab.
        if (FrameSequence.DetectPattern(SourceFile) is { } pattern)
        {
            _prefix = pattern.Prefix;
            _padding = pattern.Padding;
            _extension = pattern.Extension;
            _patternStart = pattern.Number;
            _patternEnd = pattern.Number + Math.Max(0, Items.Count - 1);
            RaiseAll(nameof(Prefix), nameof(Padding), nameof(Extension),
                nameof(PatternStart), nameof(PatternEnd));
            RefreshPattern();
        }
    }

    /// <summary>Highest selectable index in the detected list.</summary>
    public int MaxIndex => Math.Max(0, Items.Count - 1);

    private void ApplyRange()
    {
        if (_applyingRange) return;
        _applyingRange = true;
        try
        {
            int start = Math.Min(_rangeStart, _rangeEnd);
            int end = Math.Max(_rangeStart, _rangeEnd);
            for (int i = 0; i < Items.Count; i++)
            {
                Items[i].IsIncluded = i >= start && i <= end && (i - start) % Math.Max(1, _step) == 0;
            }
        }
        finally { _applyingRange = false; }
        RaiseResult();
    }

    private void LoadThumbnails()
    {
        if (SourceFolder is not { } folder) return;
        var token = _thumbnails.Token;
        foreach (var item in Items)
        {
            var target = item;
            string path = Path.Combine(folder, item.Name);
            _ = LoadThumbnailAsync(target, path, token);
        }
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(
        SequenceItemViewModel item, string path, CancellationToken token)
    {
        try
        {
            var bitmap = await _document.Shell.Thumbnails
                .GetFileAsync(path, ThumbnailPixels, token).ConfigureAwait(true);
            if (!token.IsCancellationRequested) item.Thumbnail = bitmap;
        }
        catch (OperationCanceledException) { }
    }

    private void CancelThumbnails()
    {
        var old = _thumbnails;
        _thumbnails = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }

    // ── Tab 2: from pattern ───────────────────────────────────────────────────

    /// <summary>Generated names, with a note about whether each one exists yet.</summary>
    public ObservableCollection<SequenceItemViewModel> PatternItems { get; } = [];

    /// <summary>Everything before the number, e.g. <c>hazard_strip_</c>.</summary>
    public string Prefix
    {
        get => _prefix;
        set { if (Set(ref _prefix, value ?? string.Empty)) SchedulePattern(); }
    }

    /// <summary>First number, inclusive.</summary>
    public int PatternStart
    {
        get => _patternStart;
        set { if (Set(ref _patternStart, Math.Clamp(value, 0, 99999))) SchedulePattern(); }
    }

    /// <summary>Last number, inclusive.</summary>
    public int PatternEnd
    {
        get => _patternEnd;
        set { if (Set(ref _patternEnd, Math.Clamp(value, 0, 99999))) SchedulePattern(); }
    }

    /// <summary>How many digits the number is written with; 0 means no padding.</summary>
    public int Padding
    {
        get => _padding;
        set { if (Set(ref _padding, Math.Clamp(value, 0, 8))) SchedulePattern(); }
    }

    /// <summary>The extension, with or without its dot.</summary>
    public string Extension
    {
        get => _extension;
        set { if (Set(ref _extension, value ?? string.Empty)) SchedulePattern(); }
    }

    /// <summary>Increment between numbers.</summary>
    public int PatternStep
    {
        get => _patternStep;
        set { if (Set(ref _patternStep, Math.Clamp(value, 1, 99))) SchedulePattern(); }
    }

    /// <summary>The folder generated names will be looked for — and stored relative to.</summary>
    public string? PatternFolder => _document.AtxFolder ?? SourceFolder;

    /// <summary>The sentence under the pattern fields.</summary>
    public string PatternFolderText => PatternFolder is { } folder
        ? $"Checked against {folder}"
        : "Save the .atx first (or add one frame from disk) so the app knows which folder "
          + "these images belong to.";

    /// <summary>False when the pattern tab cannot produce usable frames yet.</summary>
    public bool PatternFolderKnown => PatternFolder is not null;

    /// <summary>The most rows the preview list shows; the rest are summarised in one line.</summary>
    public const int MaxPreviewRows = 500;

    /// <summary>
    /// How long after the last keystroke the preview is rebuilt. Typing "0" then "9999" in the
    /// end-number box ran the whole listing five times, once per digit.
    /// </summary>
    private const int PatternDebounceMs = 200;

    /// <summary>
    /// A line under the preview when the range asks for more names than are shown, or than are
    /// generated at all. Empty when everything is on screen.
    /// </summary>
    public string PatternOverflowText
    {
        get => _patternOverflowText;
        private set => Set(ref _patternOverflowText, value);
    }

    /// <summary>True when <see cref="PatternOverflowText"/> has something to say.</summary>
    public bool HasPatternOverflow => _patternOverflowText.Length > 0;

    /// <summary>
    /// Rebuilds the preview after a short pause. Every pattern field calls this, and a field like
    /// the end number changes once per keystroke.
    /// </summary>
    private void SchedulePattern()
    {
        _patternDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PatternDebounceMs),
        };
        _patternDebounce.Tick -= OnPatternDebounceTick;
        _patternDebounce.Tick += OnPatternDebounceTick;
        _patternDebounce.Stop();
        _patternDebounce.Start();
    }

    private void OnPatternDebounceTick(object? sender, EventArgs e)
    {
        _patternDebounce?.Stop();
        RefreshPattern();
    }

    private void RefreshPattern()
    {
        PatternItems.Clear();
        string extension = _extension.Trim();
        long wanted = FrameSequence.CountFor(_patternStart, _patternEnd, _patternStep);
        var names = FrameSequence.Generate(
            _prefix, _patternStart, _patternEnd, _padding, extension, _patternStep);
        _patternNames = names;
        string? folder = PatternFolder;

        // One directory listing, not one File.Exists per name. At 100,000 names the per-name form
        // was 100,000 filesystem round trips on the UI thread for every keystroke.
        var onDisk = ListFolder(folder);

        int shown = Math.Min(names.Count, MaxPreviewRows);
        for (int i = 0; i < shown; i++)
        {
            string name = names[i];
            bool exists = onDisk is not null && onDisk.Contains(name);
            var item = new SequenceItemViewModel(name, exists);
            item.IncludedChanged += (_, _) => RaiseResult();
            PatternItems.Add(item);
            // A generated name that already exists on disk gets its thumbnail too, so the user can
            // see they are pointing at the run they think they are.
            if (exists && folder is not null)
            {
                _ = LoadThumbnailAsync(item, Path.Combine(folder, name), _thumbnails.Token);
            }
        }

        PatternOverflowText = BuildOverflowText(wanted, names.Count, shown);
        RaiseAll(nameof(PatternFolderText), nameof(PatternFolderKnown), nameof(HasPatternOverflow));
        RaiseResult();
    }

    private static string BuildOverflowText(long wanted, int generated, int shown)
    {
        if (wanted > generated)
        {
            return $"That range covers {wanted:N0} names. Only the first {generated:N0} will be "
                + $"added, and the first {shown:N0} are listed — narrow the range to see the rest.";
        }
        if (generated > shown)
        {
            return $"Showing the first {shown:N0} of {generated:N0}. All {generated:N0} will be added.";
        }
        return string.Empty;
    }

    /// <summary>
    /// The names in <paramref name="folder"/>, compared the way Windows compares them, or null
    /// when there is no folder to read.
    /// </summary>
    private static HashSet<string>? ListFolder(string? folder)
    {
        if (folder is null) return null;
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in Directory.EnumerateFiles(folder)) names.Add(Path.GetFileName(path));
            return names;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    // ── Result ────────────────────────────────────────────────────────────────

    /// <summary>Where the new frames go.</summary>
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

    /// <summary>The file names that will become frames, in order, duplicates and all.</summary>
    public IReadOnlyList<string> ResultNames
    {
        get
        {
            List<string> names;
            if (_tabIndex == 1)
            {
                // The preview lists only the first few hundred names, but every generated name is
                // added: the ticks apply to the rows the user can actually see and untick.
                var dropped = PatternItems.Where(i => !i.IsIncluded)
                    .Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
                names = [.. _patternNames.Where(n => !dropped.Contains(n))];
            }
            else
            {
                names = [.. Items.Where(i => i.IsIncluded).Select(i => i.Name)];
                if (_reverse) names.Reverse();
            }
            return _appendReversed ? FrameSequence.WithReversedTail(names) : names;
        }
    }

    /// <summary>"12 frames will be added" — the live count under the list.</summary>
    public string ResultCountText
    {
        get
        {
            int count = ResultNames.Count;
            if (count == 0) return "Nothing selected yet.";
            string verb = _insertMode == SequenceInsertMode.ReplaceAll ? "replace every frame with" : "add";
            return string.Format(
                CultureInfo.CurrentCulture, "Will {0} {1} frame{2}.", verb, count, count == 1 ? "" : "s");
        }
    }

    /// <summary>The resulting names, one per line, so the user can see exactly what they get.</summary>
    public string ResultPreview => string.Join(Environment.NewLine, ResultNames);

    /// <summary>False when OK cannot do anything useful.</summary>
    public bool CanApply => ResultNames.Count > 0
        && (_tabIndex == 0 ? SourceFolder is not null : PatternFolderKnown);

    /// <summary>A sentence explaining why OK is disabled, or null when it is not.</summary>
    public string? Blocker
    {
        get
        {
            if (CanApply) return null;
            if (_tabIndex == 1 && !PatternFolderKnown) return PatternFolderText;
            return _tabIndex == 0 && Items.Count == 0
                ? "Choose a file from the sequence to begin."
                : "Tick at least one file.";
        }
    }

    /// <summary>The folder the chosen names live in.</summary>
    public string? ResultFolder => _tabIndex == 1 ? PatternFolder : SourceFolder;

    private void RaiseResult() => RaiseAll(
        nameof(ResultNames), nameof(ResultCountText), nameof(ResultPreview),
        nameof(CanApply), nameof(Blocker), nameof(ResultFolder));

    /// <summary>Writes the sequence into the document as one undo step.</summary>
    public void Apply()
    {
        var names = ResultNames;
        if (names.Count == 0 || ResultFolder is not { } folder) return;

        // The document may have been reparsed — or reloaded from disk — while the dialog was up,
        // so the insert position is worked out against the file as it is now, not as it was.
        _document.EnsureParsed();
        var paths = names.Select(n => Path.Combine(folder, n)).ToList();
        int insert = _insertMode switch
        {
            SequenceInsertMode.AfterSelection when _document.Frames.HasSelection =>
                Math.Min(_document.Frames.SelectedIndices[^1] + 1, _document.FrameCount),
            SequenceInsertMode.ReplaceAll => 0,
            _ => -1,
        };

        _document.AddImageFiles(
            paths, insert, naturalSort: false,
            replaceAll: _insertMode == SequenceInsertMode.ReplaceAll);
    }

    /// <summary>The file filter the Browse button uses.</summary>
    public static string BrowseTitle => "Choose any file of the sequence";

    public void Dispose()
    {
        if (_patternDebounce is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnPatternDebounceTick;
            _patternDebounce = null;
        }
        _thumbnails.Cancel();
        _thumbnails.Dispose();
    }
}
