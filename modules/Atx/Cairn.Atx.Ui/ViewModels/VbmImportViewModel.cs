using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Ui.Services;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Atx.Playback;
using Cairn.Atx.Schema;
using Cairn.Workspace;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>
/// Where a .vbm being imported came from: a file on disk, or an entry inside a .vpp archive.
/// </summary>
/// <param name="Name">The .vbm's own file name, which every default is derived from.</param>
/// <param name="FilePath">Full path on disk, or null for an archive entry.</param>
/// <param name="ArchivePath">The .vpp it lives in, or null for a file on disk.</param>
public sealed record VbmImportSource(string Name, string? FilePath, string? ArchivePath)
{
    /// <summary>A .vbm picked from disk.</summary>
    /// <param name="path">Its full path.</param>
    public static VbmImportSource FromFile(string path) =>
        new(Path.GetFileName(path), path, null);

    /// <summary>
    /// A .vbm inside an archive. The entry name is sanitised the way anything written out of a
    /// .vpp is, because the file names this import generates are derived from it.
    /// </summary>
    /// <param name="archivePath">Full path of the .vpp.</param>
    /// <param name="entryName">The entry name as the archive stores it.</param>
    public static VbmImportSource FromArchive(string archivePath, string entryName) =>
        new(VppExtraction.IsSafeEntryName(entryName)
                ? entryName
                : VbmImportPlan.Sanitise(Path.GetFileName(entryName)),
            null, archivePath);

    /// <summary>The entry name inside the archive, which is the same as <see cref="Name"/>.</summary>
    public string EntryName => Name;

    /// <summary>True when the bytes come out of a .vpp rather than off the disk.</summary>
    public bool IsFromArchive => ArchivePath is not null;

    /// <summary>Where the dialog says this came from.</summary>
    public string OriginText => FilePath
        ?? (ArchivePath is null ? "an unsaved bitmap" : $"{Path.GetFileName(ArchivePath)} — {ArchivePath}");

    /// <summary>The .vbm's own folder, or null when it has none because it lives in an archive.</summary>
    public string? OwnFolder
    {
        get
        {
            if (FilePath is null) return null;
            try { return Path.GetDirectoryName(Path.GetFullPath(FilePath)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException or System.Security.SecurityException)
            {
                return null;
            }
        }
    }
}

/// <summary>The result of reading a .vbm: its bytes and header, or the reason neither arrived.</summary>
/// <param name="Bytes">The whole file.</param>
/// <param name="Info">Its header.</param>
/// <param name="Error">What went wrong, phrased for a person.</param>
/// <param name="Details">The exception text, for the Technical details expander.</param>
public sealed record VbmImportLoad(byte[]? Bytes, VbmInfo? Info, string? Error, string? Details)
{
    /// <summary>True when there is a file to import.</summary>
    public bool Ok => Bytes is not null && Info is not null;
}

/// <summary>
/// Reads a <see cref="VbmImportSource"/> into memory. Always called from a worker thread: it opens
/// files and archives, and one of those is on whatever drive the user pointed at.
/// </summary>
public static class VbmImportLoader
{
    /// <summary>The largest .vbm this will read. A stock one is a few hundred kilobytes.</summary>
    public const long MaxBytes = 128L * 1024 * 1024;

    /// <summary>Reads the source and its header, turning every failure into a sentence.</summary>
    /// <param name="source">What to read.</param>
    public static VbmImportLoad Load(VbmImportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] bytes;
        try
        {
            bytes = source.ArchivePath is { } archivePath
                ? ReadFromArchive(archivePath, source.EntryName)
                : ReadFromDisk(source.FilePath!);
        }
        catch (VppFormatException ex)
        {
            return new VbmImportLoad(
                null, null, $"'{source.Name}' could not be read out of the archive.", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or System.Security.SecurityException)
        {
            return new VbmImportLoad(null, null, $"'{source.Name}' could not be read.", ex.Message);
        }

        try
        {
            return new VbmImportLoad(bytes, VbmCodec.ReadInfo(bytes, source.Name), null, null);
        }
        catch (ImageDecodeException ex)
        {
            return new VbmImportLoad(null, null, ex.Message, null);
        }
    }

    private static byte[] ReadFromDisk(string path)
    {
        var info = new FileInfo(path);
        if (info.Exists && info.Length > MaxBytes) throw TooLarge(Path.GetFileName(path));
        return File.ReadAllBytes(path);
    }

    private static byte[] ReadFromArchive(string archivePath, string entryName)
    {
        var archive = VppArchive.Open(archivePath);
        if (!archive.TryGetEntry(entryName, out var entry))
            throw new FileNotFoundException($"'{entryName}' is no longer in the archive.", entryName);
        if (entry.Size > MaxBytes) throw TooLarge(entryName);

        using var stream = archive.OpenEntry(entry);
        using var buffer = new MemoryStream(entry.Size);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IOException TooLarge(string name) => new(
        $"'{name}' is larger than {MaxBytes / (1024 * 1024)} MB, which is far more than any "
        + "animated texture. It is probably not a VBM.");
}

/// <summary>
/// File &gt; Import VBM…: turn a legacy animated <c>.vbm</c> into one TGA per frame plus a matching
/// <c>.atx</c>.
///
/// <para>
/// Everything the dialog shows comes from a <see cref="VbmImportPlan"/> rebuilt on every keystroke,
/// so the file list, the sizes and the validation messages are literally what would be written —
/// there is no second description of the plan that can drift from the first. The preview plays the
/// source through <see cref="AtxPlayback"/> with the frame time the generated .atx will carry, so
/// what the dialog shows is what the game will show.
/// </para>
/// </summary>
public sealed class VbmImportViewModel : ObservableObject, IDisposable
{
    /// <summary>How many decoded frames are kept. Past this they are decoded again on demand.</summary>
    public const long PreviewCacheBudgetBytes = 64L * 1024 * 1024;

    /// <summary>How many file names the list shows before it stops and counts the rest.</summary>
    private const int FileListPreviewCount = 6;

    private readonly AtxWorkspace _shell;
    private readonly VbmImportSource _source;
    private readonly byte[] _bytes;
    private readonly VbmInfo _info;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly Dictionary<int, BitmapSource> _frames = [];
    private readonly HashSet<int> _decoding = [];

    private VbmImportPlan _plan;
    private AtxPlayback _playback;
    private CancellationTokenSource? _import;
    private bool _disposed;

    private string _outputFolder;
    private string _atxName;
    private string _frameBaseName;
    private bool _setFormat = true;
    private bool _includeComments = true;
    private AtxAnimationMode _mode;
    private bool _useRange;
    private int _rangeFirst;
    private int _rangeLast;

    private BitmapSource? _previewImage;
    private int _previewFrame;
    private bool _isPlaying;
    private bool _isImporting;
    private double _progress;
    private string _progressText = string.Empty;

    /// <param name="shell">The shell, for settings, prompts and the dispatcher.</param>
    /// <param name="source">Where the .vbm came from.</param>
    /// <param name="bytes">Its bytes, already read on a worker thread.</param>
    /// <param name="info">Its header.</param>
    public VbmImportViewModel(
        AtxWorkspace shell, VbmImportSource source, byte[] bytes, VbmInfo info)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        _info = info ?? throw new ArgumentNullException(nameof(info));

        _outputFolder = DefaultFolder(shell, source);
        _atxName = VbmImportPlan.DefaultAtxName(source.Name);
        _frameBaseName = VbmImportPlan.DefaultFrameBaseName(source.Name, info.FrameCount);
        _mode = info.FrameCount > 1 ? AtxAnimationMode.Loop : AtxAnimationMode.Static;
        _rangeLast = info.FrameCount - 1;

        _plan = BuildPlan();
        _playback = BuildPlayback();

        BrowseFolderCommand = new RelayCommand(BrowseFolder);
        PlayPauseCommand = new RelayCommand(TogglePlay, () => info.FrameCount > 1);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => CanImport);
        CancelImportCommand = new RelayCommand(() => _import?.Cancel(), () => _isImporting);

        _timer = new DispatcherTimer(DispatcherPriority.Render, shell.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };
        _timer.Tick += OnTick;

        _ = PrefetchAsync();
        if (info.FrameCount > 1) StartPlaying();
    }

    // ── The source ────────────────────────────────────────────────────────────

    /// <summary>The .vbm's own name.</summary>
    public string SourceName => _source.Name;

    /// <summary>Where the file came from: a full path, or the archive that holds it.</summary>
    public string SourceOrigin => _source.OriginText;

    /// <summary>The header in one line, e.g. "VBM v1 · 256 x 256 · 4444 ARGB · 16 frames · 15 fps".</summary>
    public string SourceSummary => _info.Describe();

    /// <summary>True when the file holds more than one frame.</summary>
    public bool IsAnimated => _info.FrameCount > 1;

    /// <summary>The gentle note a single-frame VBM earns.</summary>
    public string SingleFrameNote =>
        "This VBM holds one frame, so the .atx will have one frame and nothing to animate. "
        + "It is still a valid texture, and you can add frames to it afterwards.";

    /// <summary>The line about what the generated .atx does to the original texture.</summary>
    public string SupersedeNote => _plan.SupersedeNote;

    /// <summary>True when the .atx keeps the .vbm's name and therefore replaces it in game.</summary>
    public bool ReplacesSource => _plan.ReplacesSource;

    /// <summary>How the frame rate came out, rounding and all.</summary>
    public string TimingSummary => _plan.TimingSummary;

    // ── Output ────────────────────────────────────────────────────────────────

    /// <summary>The folder the frames and the .atx are written into.</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set { if (Set(ref _outputFolder, value ?? string.Empty)) Replan(); }
    }

    /// <summary>The generated file's name.</summary>
    public string AtxName
    {
        get => _atxName;
        set { if (Set(ref _atxName, value ?? string.Empty)) Replan(); }
    }

    /// <summary>The stem every frame file is named from.</summary>
    public string FrameBaseName
    {
        get => _frameBaseName;
        set { if (Set(ref _frameBaseName, value ?? string.Empty)) Replan(); }
    }

    /// <summary>Picks the output folder.</summary>
    public RelayCommand BrowseFolderCommand { get; }

    /// <summary>The first few file names, then a count of the rest.</summary>
    public string FileListText
    {
        get
        {
            var names = _plan.FrameFileNames;
            if (names.Count == 0) return "Nothing to write.";
            var lines = names.Take(FileListPreviewCount).ToList();
            if (names.Count > lines.Count)
                lines.Add($"… and {names.Count - lines.Count} more");
            lines.Add(_plan.AtxFileName);
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>"16 frames + 1 .atx · 4.2 MB", the line under the file list.</summary>
    public string FileTotalsText
    {
        get
        {
            int count = _plan.FrameFileNames.Count;
            if (count == 0) return string.Empty;
            string frames = count == 1 ? "1 frame" : $"{count} frames";
            string depth = _plan.FramesHaveAlpha ? "32-bit TGA" : "24-bit TGA";
            return $"{frames} as {depth} plus {_plan.AtxFileName} · "
                + VppImageNodeViewModel.FormatBytes(_plan.TotalFrameBytes);
        }
    }

    /// <summary>Everything the plan objects to, one per line, worst first.</summary>
    public string ValidationText => string.Join(
        Environment.NewLine,
        _plan.Messages
            .OrderBy(m => (int)m.Severity)
            .Select(m => m.Text));

    /// <summary>True when there is anything in <see cref="ValidationText"/>.</summary>
    public bool HasValidationText => _plan.Messages.Count > 0;

    /// <summary>True when something in the plan actually blocks the import.</summary>
    public bool HasBlockingProblem =>
        _plan.Messages.Any(m => m.Severity == VbmImportSeverity.Error);

    // ── Options ───────────────────────────────────────────────────────────────

    /// <summary>Write <c>format</c> so the game stores the frames as the VBM stored them.</summary>
    public bool SetFormatToMatchSource
    {
        get => _setFormat;
        set { if (Set(ref _setFormat, value)) Replan(); }
    }

    /// <summary>The label on that checkbox, naming the format it would write.</summary>
    public string SetFormatLabel => $"Set _format to match the source ({_plan.FormatToken() ?? "unknown"})";

    /// <summary>What setting the format buys, spelled out.</summary>
    public string SetFormatToolTip =>
        $"The game converts every frame to {_plan.FormatToken() ?? "the source format"} after "
        + "loading, so the texture looks the same and takes the same video memory as the .vbm did. "
        + "Unchecked, the frames stay at the 24- or 32-bit depth of the exported TGA files.";

    /// <summary>Put the explanatory comment block at the top of the generated file.</summary>
    public bool IncludeComments
    {
        get => _includeComments;
        set { if (Set(ref _includeComments, value)) Replan(); }
    }

    /// <summary>The animation mode written into the header.</summary>
    public AtxAnimationMode AnimationMode
    {
        get => _mode;
        set
        {
            if (!Set(ref _mode, value)) return;
            RaiseAll(nameof(IsLoop), nameof(IsPingPong), nameof(IsPlayOnce), nameof(IsStatic),
                nameof(AnimationModeDescription));
            Replan();
            RestartPlayback();
        }
    }

    public bool IsLoop
    {
        get => _mode == AtxAnimationMode.Loop;
        set { if (value) AnimationMode = AtxAnimationMode.Loop; }
    }

    public bool IsPingPong
    {
        get => _mode == AtxAnimationMode.PingPong;
        set { if (value) AnimationMode = AtxAnimationMode.PingPong; }
    }

    public bool IsPlayOnce
    {
        get => _mode == AtxAnimationMode.PlayOnce;
        set { if (value) AnimationMode = AtxAnimationMode.PlayOnce; }
    }

    public bool IsStatic
    {
        get => _mode == AtxAnimationMode.Static;
        set { if (value) AnimationMode = AtxAnimationMode.Static; }
    }

    /// <summary>The one-line description of the selected mode, from the schema.</summary>
    public string AnimationModeDescription =>
        AtxSchema.AnimationModes.First(m => m.Mode == _mode).Description;

    /// <summary>True when only part of the file is exported.</summary>
    public bool UseFrameRange
    {
        get => _useRange;
        set
        {
            if (!Set(ref _useRange, value)) return;
            Raise(nameof(RangeEnabled));
            Replan();
        }
    }

    /// <summary>True when the from/to boxes apply, i.e. there is more than one frame to trim.</summary>
    public bool RangeEnabled => _useRange && _info.FrameCount > 1;

    /// <summary>First source frame exported.</summary>
    public int RangeFirst
    {
        get => _rangeFirst;
        set { if (Set(ref _rangeFirst, Math.Clamp(value, 0, _info.FrameCount - 1))) Replan(); }
    }

    /// <summary>Last source frame exported.</summary>
    public int RangeLast
    {
        get => _rangeLast;
        set { if (Set(ref _rangeLast, Math.Clamp(value, 0, _info.FrameCount - 1))) Replan(); }
    }

    /// <summary>The highest frame number the range boxes accept.</summary>
    public int MaxFrameIndex => Math.Max(0, _info.FrameCount - 1);

    // ── Preview ───────────────────────────────────────────────────────────────

    /// <summary>The frame being shown, or null while the first one decodes.</summary>
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set { if (Set(ref _previewImage, value)) Raise(nameof(HasPreviewImage)); }
    }

    /// <summary>True when there is a picture to draw.</summary>
    public bool HasPreviewImage => _previewImage is not null;

    /// <summary>What the preview area says while there is no picture.</summary>
    public string PreviewPlaceholder { get; private set; } = "Reading…";

    /// <summary>Which source frame the preview is on.</summary>
    public int PreviewFrame
    {
        get => _previewFrame;
        set
        {
            int wanted = Math.Clamp(value, 0, Math.Max(0, _info.FrameCount - 1));
            if (!Set(ref _previewFrame, wanted)) return;
            _playback.SetFrame(wanted);
            Raise(nameof(PreviewFrameText));
            ShowFrame(wanted);
        }
    }

    /// <summary>"Frame 3 of 16 · 67 ms".</summary>
    public string PreviewFrameText =>
        $"Frame {_previewFrame} of {_info.FrameCount - 1} · {_plan.FrameTimeMs} ms";

    /// <summary>True while the preview is running.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayPauseGlyph)); }
    }

    /// <summary>The Segoe MDL2 glyph for the transport button.</summary>
    public string PlayPauseGlyph => _isPlaying ? "" : "";

    /// <summary>Play or pause the preview.</summary>
    public RelayCommand PlayPauseCommand { get; }

    // ── Importing ─────────────────────────────────────────────────────────────

    /// <summary>Writes the files.</summary>
    public AsyncRelayCommand ImportCommand { get; }

    /// <summary>Stops an import that is already running.</summary>
    public RelayCommand CancelImportCommand { get; }

    /// <summary>True when the Import button can do anything.</summary>
    public bool CanImport => _plan.CanRun && !_isImporting;

    /// <summary>True while files are being written.</summary>
    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (!Set(ref _isImporting, value)) return;
            Raise(nameof(CanImport));
            ImportCommand.RaiseCanExecuteChanged();
            CancelImportCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>0 to 1, for the progress bar.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    /// <summary>"Writing flame_07.tga — 8 of 16".</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>The .atx that was generated, once the import has succeeded.</summary>
    public string? ImportedPath { get; private set; }

    /// <summary>Raised when the dialog should close itself.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Runs the import as the Import button does and completes when it has finished (no dialog needed).</summary>
    internal Task RunImportAsync() => ImportAsync();

    private async Task ImportAsync()
    {
        if (!CanImport) return;
        var plan = _plan;
        Pause();

        _import?.Dispose();
        _import = new CancellationTokenSource();
        var token = _import.Token;
        IsImporting = true;
        Progress = 0;
        ProgressText = "Starting…";

        var progress = new Progress<VbmImportProgress>(p =>
        {
            Progress = p.Fraction;
            ProgressText = p.Completed >= p.Total
                ? $"Writing {p.CurrentFile}…"
                : $"Writing {p.CurrentFile} — {p.Completed + 1} of {p.Total}";
        });

        VbmImportResult result;
        try
        {
            result = await Task.Run(
                () => VbmImporter.Run(_bytes, plan, AskAboutCollisions, progress, token), token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            IsImporting = false;
            ProgressText = string.Empty;
            return;
        }
        catch (Exception ex)
        {
            // An import is fire-and-forget as far as the dialog is concerned, so nothing else would
            // ever see this; the log is the only place it can be recovered from.
            ErrorLog.Write("VBM import", ex);
            IsImporting = false;
            ProgressText = string.Empty;
            _shell.Dialogs.ShowError(
                "The import could not be finished.",
                $"Something went wrong while writing into '{plan.OutputFolder}'.", ex.Message);
            return;
        }

        IsImporting = false;
        ProgressText = string.Empty;

        switch (result.Outcome)
        {
            case VbmImportOutcome.Cancelled:
                return;
            case VbmImportOutcome.Failed:
                _shell.Dialogs.ShowError(
                    "The import did not finish.",
                    result.Message ?? "The files could not be written.",
                    result.Failures.Count > 0
                        ? string.Join(Environment.NewLine, result.Failures)
                        : string.Join(Environment.NewLine, plan.AllPaths));
                return;
            default:
                ImportedPath = result.AtxPath;
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
        }
    }

    /// <summary>
    /// The collision question, asked from the worker thread the import runs on and answered with
    /// the app's own Replace / Keep existing / Cancel prompt on the UI thread — the same one that
    /// guards copying images in from outside, so a user sees one rule, not two.
    /// </summary>
    private VbmCollisionChoice AskAboutCollisions(IReadOnlyList<string> names)
    {
        string folderName = FolderLabel(_plan.OutputFolder);
        var answer = _shell.Dispatcher.Invoke(
            () => _shell.Dialogs.AskReplaceExisting(names, folderName));
        return answer switch
        {
            ReplaceChoice.Replace => VbmCollisionChoice.Replace,
            ReplaceChoice.KeepExisting => VbmCollisionChoice.KeepExisting,
            _ => VbmCollisionChoice.Cancel,
        };
    }

    private static string FolderLabel(string folder)
    {
        try
        {
            string name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
            return name.Length > 0 ? name : folder;
        }
        catch (ArgumentException) { return folder; }
    }

    private void BrowseFolder()
    {
        string? chosen = _shell.Dialogs.PickFolder(
            Directory.Exists(_outputFolder) ? _outputFolder : null,
            "Where should the frames and the .atx go?");
        if (chosen is not null) OutputFolder = chosen;
    }

    // ── Plan ──────────────────────────────────────────────────────────────────

    /// <summary>The plan as it stands, which is exactly what Import would carry out.</summary>
    public VbmImportPlan Plan => _plan;

    private VbmImportPlan BuildPlan() => VbmImportPlan.Create(
        _info, _source.Name, _outputFolder, _atxName, _frameBaseName,
        new VbmImportOptions
        {
            SetFormatToMatchSource = _setFormat,
            AnimationMode = _mode,
            IncludeComments = _includeComments,
            FirstFrame = RangeEnabled ? Math.Min(_rangeFirst, _rangeLast) : 0,
            LastFrame = RangeEnabled ? Math.Max(_rangeFirst, _rangeLast) : null,
        });

    private void Replan()
    {
        _plan = BuildPlan();
        RaiseAll(
            nameof(Plan), nameof(FileListText), nameof(FileTotalsText), nameof(ValidationText),
            nameof(HasValidationText), nameof(HasBlockingProblem), nameof(SupersedeNote),
            nameof(ReplacesSource), nameof(TimingSummary), nameof(CanImport),
            nameof(SetFormatLabel), nameof(SetFormatToolTip), nameof(PreviewFrameText));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private static string DefaultFolder(AtxWorkspace shell, VbmImportSource source)
    {
        // A .vbm on disk brings its own folder with it, which is almost always where the frames
        // belong. One out of a .vpp has none, so the last import folder is the next best answer,
        // then the open document's folder. The game directory is never offered: an editor writing
        // into a game install by default is how a stock file gets quietly replaced.
        if (source.OwnFolder is { Length: > 0 } own && Directory.Exists(own)) return own;
        if (shell.AtxSettings.LastImportFolder is { Length: > 0 } last && Directory.Exists(last))
            return last;
        if (shell.ActiveDocument?.AtxFolder is { Length: > 0 } atx && Directory.Exists(atx))
            return atx;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ── Preview playback ──────────────────────────────────────────────────────

    private AtxPlayback BuildPlayback() => new(new PlaybackSpec(
        _info.FrameCount > 1 ? _mode : AtxAnimationMode.Static,
        InitiallyOn: true,
        _plan.FrameTimeMs,
        new int?[_info.FrameCount]));

    private void RestartPlayback()
    {
        bool wasPlaying = _isPlaying;
        int frame = _previewFrame;
        _playback = BuildPlayback();
        _playback.SetFrame(frame);
        if (wasPlaying) _playback.Play();
        else _playback.Pause();
    }

    private void TogglePlay()
    {
        if (_isPlaying) Pause();
        else StartPlaying();
    }

    private void StartPlaying()
    {
        if (_info.FrameCount < 2) return;
        _playback.Play();
        IsPlaying = true;
        _clock.Restart();
        _timer.Start();
    }

    /// <summary>Stops the preview, e.g. while files are being written.</summary>
    public void Pause()
    {
        _timer.Stop();
        _clock.Stop();
        _playback.Pause();
        IsPlaying = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double elapsed = _clock.Elapsed.TotalSeconds;
        _clock.Restart();
        if (!_playback.Advance(elapsed)) return;
        if (Set(ref _previewFrame, _playback.CurrentFrame, nameof(PreviewFrame)))
        {
            Raise(nameof(PreviewFrameText));
            ShowFrame(_previewFrame);
        }
        if (!_playback.Playing) IsPlaying = false;
    }

    /// <summary>
    /// Shows one frame, decoding it if it is not in the cache. A frame that is still decoding
    /// leaves the previous one on screen rather than flashing an empty canvas at the user.
    /// </summary>
    private void ShowFrame(int index)
    {
        if (_frames.TryGetValue(index, out var cached))
        {
            PreviewImage = cached;
            return;
        }
        _ = DecodeAsync(index);
    }

    private async Task PrefetchAsync()
    {
        for (int i = 0; i < _info.FrameCount; i++)
        {
            if (_disposed) return;
            if (CacheBytes() >= PreviewCacheBudgetBytes) break;
            await DecodeAsync(i).ConfigureAwait(true);
            if (i == 0) ShowFrame(0);
        }
    }

    private async Task DecodeAsync(int index)
    {
        if (_disposed || _frames.ContainsKey(index) || !_decoding.Add(index)) return;
        byte[] bytes = _bytes;
        string name = _source.Name;

        BitmapSource? bitmap = null;
        string? error = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                var image = VbmCodec.DecodeFrame(bytes, index, name);
                var source = BitmapSource.Create(
                    image.Width, image.Height, 96, 96,
                    System.Windows.Media.PixelFormats.Bgra32, null, image.Pixels, image.Stride);
                source.Freeze();
                return source;
            }).ConfigureAwait(true);
        }
        catch (ImageDecodeException ex) { error = ex.Message; }
        catch (Exception ex)
        {
            ErrorLog.Write("VBM import preview", ex);
            error = $"Frame {index} could not be shown.";
        }
        finally { _decoding.Remove(index); }

        if (_disposed) return;
        if (bitmap is null)
        {
            // One unreadable frame stops the preview rather than spinning on every tick; the plan's
            // own validation already says the file looks truncated.
            Pause();
            PreviewPlaceholder = error ?? "This frame could not be shown.";
            Raise(nameof(PreviewPlaceholder));
            return;
        }

        if (CacheBytes() < PreviewCacheBudgetBytes) _frames[index] = bitmap;
        if (index == _previewFrame) PreviewImage = bitmap;
    }

    private long CacheBytes() =>
        (long)_frames.Count * _info.Width * _info.Height * 4;

    // ── Lifetime ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _clock.Stop();
        _import?.Cancel();
        _import?.Dispose();
        _frames.Clear();
    }
}
