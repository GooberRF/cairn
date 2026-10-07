using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Atx.Ui.Services;
using Cairn.Assets;
using Cairn.Atx.Editing;
using Cairn.Atx.Linting;
using Cairn.Atx.Model;
using Cairn.Atx.Parsing;
using Cairn.Atx.Schema;
using Cairn.Atx.Text;
using Cairn.Workspace;
using ICSharpCode.AvalonEdit.Document;

namespace Cairn.Atx.Ui.ViewModels;

/// <summary>
/// One open .atx document: the AvalonEdit <see cref="TextDocument"/> that holds the only copy of
/// its state, and everything derived from it.
///
/// The pipeline is the design's central rule. A text change (typed or produced by a GUI action)
/// schedules a parse — immediately for GUI edits, after a short debounce for typing. The parse
/// produces a model and a syntax map; the structural linter runs synchronously; the view-models
/// refresh by diffing against the new model; and the asset linter runs on a background task whose
/// results merge into the same diagnostic list when they arrive. Nothing else holds document state,
/// so undo, comment preservation and two-way editing all fall out of that one rule.
/// </summary>
public sealed partial class DocumentViewModel : ObservableObject, IDisposable, Cairn.Ui.Documents.IDocument
{
    private const int TypingDebounceMs = 150;

    private readonly AtxWorkspace _main;
    private readonly DispatcherTimer _parseDebounce;
    private readonly FileChangeWatcher _documentWatcher;
    private readonly FileChangeWatcher _folderWatcher;

    private CancellationTokenSource? _assetLint;
    private CancellationTokenSource _thumbnails = new();
    private bool _parseStale;
    private bool _isOpening;
    private bool _applyingGuiEdit;
    private bool _disposed;
    private int _interactionDepth;

    private string? _filePath;
    private string _displayName;
    private string? _impliedFolder;
    private string _savedText;
    private bool _isDirty;
    private LineEndingKind _lineEnding = LineEndingKind.CrLf;

    private AtxParseResult _parse;
    private AtxModel? _model;
    private IReadOnlyList<Diagnostic> _structural = [];
    private IReadOnlyList<Diagnostic> _diagnostics = [];
    private AssetAnalysis? _assets;
    private Diagnostic? _assetLintError;
    private bool _assetsStale;
    private AssetResolver _resolver;

    private bool _hasExternalChange;
    private bool _isMissingOnDisk;
    private AtxFileEncoding _encoding = AtxFileEncoding.Utf8;
    private bool _encodingNoticeDismissed;
    private int _caretLine = 1;
    private int _caretColumn = 1;
    private bool _syncingSelection;

    /// <param name="main">The shell, for settings, services and status updates.</param>
    /// <param name="text">The document's initial text.</param>
    /// <param name="filePath">Where it came from, or null for a new document.</param>
    /// <param name="displayName">Tab caption for an unsaved document.</param>
    /// <param name="encoding">How the file on disk was decoded; new documents are UTF-8.</param>
    public DocumentViewModel(
        AtxWorkspace main, string text, string? filePath, string displayName,
        AtxFileEncoding encoding = AtxFileEncoding.Utf8)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        Id = Guid.NewGuid().ToString("N");
        _filePath = filePath;
        _displayName = displayName;
        _savedText = text;
        _encoding = encoding;
        _lineEnding = LineEndings.Detect(text);

        Document = new TextDocument(text);
        Document.UndoStack.SizeLimit = 400;
        Document.UndoStack.ClearAll();
        Document.UndoStack.MarkAsOriginalFile();
        Document.Changed += OnDocumentChanged;
        Document.UndoStack.PropertyChanged += OnUndoStackChanged;

        // A file big enough to take noticeable time is parsed on a worker thread instead, so the
        // window never locks up while it happens. Until that lands the document reads as empty,
        // which is the same state a file with a syntax error is in — the panels already cope.
        _isOpening = text.Length >= AtxTextFiles.LargeFileBytes;
        _parse = _isOpening ? AtxParser.Parse(string.Empty) : AtxParser.Parse(text);
        _model = _parse.Model;
        _resolver = BuildResolver();

        _parseDebounce = new DispatcherTimer(DispatcherPriority.Background, main.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(TypingDebounceMs),
        };
        _parseDebounce.Tick += (_, _) => { _parseDebounce.Stop(); Reparse(); };

        _documentWatcher = new FileChangeWatcher(main.Dispatcher, 400);
        _documentWatcher.Changed += (_, _) => CheckExternalChange();
        _folderWatcher = new FileChangeWatcher(main.Dispatcher, 400);
        _folderWatcher.Changed += (_, _) => OnAssetFolderChanged();

        GoToSyntaxErrorCommand = new RelayCommand(
            () => { if (SyntaxError is { } d) SelectInSource(d.Span); }, () => SyntaxError is not null);
        ConvertLayoutCommand = new RelayCommand(ConvertToStandardLayout, () => !IsCanonical);
        ReloadCommand = new RelayCommand(ReloadFromDisk);
        KeepMineCommand = new RelayCommand(KeepMine);
        DismissEncodingNoticeCommand = new RelayCommand(DismissEncodingNotice);
        SaveACopyCommand = new RelayCommand(() => _main.SaveAs(this));
        KeepEditingCommand = new RelayCommand(KeepEditingMissingFile);

        Header = new HeaderViewModel(this);
        Frames = new FrameListViewModel(this);
        Inspector = new FrameInspectorViewModel(this);
        Problems = new ProblemsViewModel(this);
        Preview = new PreviewViewModel(this);

        if (_isOpening)
        {
            // The parse on hand describes an empty document, not this one. Mark it stale so that if
            // anything does ask for an edit before the worker lands, EnsureParsed re-parses for real
            // rather than building spans against the wrong text.
            _parseStale = true;
            StartFirstParse(text);
        }
        else Reparse(_parse);
        // Start on the first frame so the preview and inspector have something to show.
        if (Frames.Rows.Count > 0) Frames.SelectRange(0, 1);
        StartWatching();
    }

    /// <summary>
    /// True while a large file's first parse is still running on a worker thread. The panels are
    /// empty and structural editing is off until it finishes, exactly as for a syntax error.
    /// </summary>
    public bool IsOpening => _isOpening;

    /// <summary>What the "Opening…" banner says.</summary>
    public string OpeningText =>
        $"Opening {_displayName} — it is a large file, so this takes a moment. "
        + "The rest of the window stays usable while it loads.";

    /// <summary>
    /// Parses a large document off the UI thread and applies the result when it lands. The text is
    /// a plain immutable string, so the worker never touches the TextDocument; if the user has
    /// typed in the meantime the result is dropped and the ordinary debounced reparse takes over.
    /// </summary>
    private void StartFirstParse(string text)
    {
        _ = System.Threading.Tasks.Task.Run(() => AtxParser.Parse(text))
            .ContinueWith(
                task => _main.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_disposed) return;
                    _isOpening = false;
                    RaiseAll(nameof(IsOpening), nameof(OpeningText));
                    Reparse(task.IsCompletedSuccessfully
                        && string.Equals(task.Result.Text, Document.Text, StringComparison.Ordinal)
                            ? task.Result
                            : null);
                    if (Frames.Rows.Count > 0 && Frames.SelectedIndices.Count == 0)
                        Frames.SelectRange(0, 1);
                })),
                System.Threading.Tasks.TaskScheduler.Default);
    }

    // ── Identity ──────────────────────────────────────────────────────────────

    /// <summary>Stable id used for crash-recovery snapshots.</summary>
    public string Id { get; }

    /// <summary>The single source of document state.</summary>
    public TextDocument Document { get; }

    /// <summary>Full path on disk, or null while the document has never been saved.</summary>
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (!Set(ref _filePath, value)) return;
            RaiseAll(nameof(TabToolTip), nameof(AtxFolder), nameof(IsSaved));
            RebuildResolver();
            StartWatching();
        }
    }

    /// <summary>True once the document has a path on disk.</summary>
    public bool IsSaved => _filePath is not null;

    /// <summary>The tab caption, without the dirty marker.</summary>
    public string DisplayName
    {
        get => _filePath is not null ? Path.GetFileName(_filePath) : _displayName;
        set { _displayName = value; Raise(); Raise(nameof(TabHeader)); }
    }

    /// <summary>The tab caption with the dirty asterisk.</summary>
    public string TabHeader => IsDirty ? DisplayName + "*" : DisplayName;

    /// <summary>Full path for the tab tooltip.</summary>
    public string TabToolTip => _filePath
        ?? (OriginText is { } origin ? $"{origin} — read-only, Save As to keep changes" : $"{DisplayName} — not saved yet");

    /// <summary>The document's line ending, preserved across saves.</summary>
    public LineEndingKind LineEnding => _lineEnding;

    /// <summary>"CRLF" / "LF" / "CR" for the status bar.</summary>
    public string LineEndingLabel => _lineEnding switch
    {
        LineEndingKind.Lf => "LF",
        LineEndingKind.Cr => "CR",
        _ => "CRLF",
    };

    /// <summary>True when the text differs from what is on disk.</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (!Set(ref _isDirty, value)) return;
            Raise(nameof(TabHeader));
            _main.RecordDocumentState(this, Document.Text);
            _main.OnDocumentDirtyChanged(this);
        }
    }

    /// <summary>
    /// The folder the .atx lives in, or — for a document that has never been saved — the folder of
    /// the first image added to it, which the design makes the implied ATX folder.
    /// </summary>
    public string? AtxFolder => _filePath is not null
        ? Path.GetDirectoryName(Path.GetFullPath(_filePath))
        : _impliedFolder;

    // ── Derived state ─────────────────────────────────────────────────────────

    /// <summary>The most recent parse of the current text.</summary>
    public AtxParseResult Parse => _parse;

    /// <summary>The last model that parsed cleanly; kept while the text has syntax errors.</summary>
    public AtxModel? Model => _model;

    /// <summary>True when the current text is not valid TOML, so the GUI is read-only.</summary>
    public bool HasSyntaxErrors => _parse.Model is null;

    /// <summary>True when the document uses the standard <c>[header]</c> + <c>[[frame]]</c> layout.</summary>
    public bool IsCanonical => _parse.IsCanonical;

    /// <summary>True when GUI controls may edit values at all.</summary>
    public bool CanEditValues => !HasSyntaxErrors && !_isOpening;

    /// <summary>True when GUI controls may add, remove or reorder blocks.</summary>
    public bool CanEditStructure => !HasSyntaxErrors && IsCanonical && !_isOpening;

    /// <summary>The first syntax error, for the read-only banner.</summary>
    public Diagnostic? SyntaxError =>
        HasSyntaxErrors ? _parse.Diagnostics.FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error) : null;

    /// <summary>Banner text for the syntax-error state.</summary>
    public string SyntaxErrorText
    {
        get
        {
            var error = SyntaxError;
            if (error is null) return string.Empty;
            // The message already opens with "Line N, column M:", so prefixing the line again
            // produced "Line 5: Line 5, column 8: …".
            return $"{error.Message} Fix it in the source to resume visual editing.";
        }
    }

    /// <summary>Banner text explaining why the layout is non-standard.</summary>
    public string NonCanonicalText
    {
        get
        {
            if (IsCanonical) return string.Empty;
            string reason = _parse.NonCanonicalReasons.FirstOrDefault() switch
            {
                NonCanonicalReason.FramesAsInlineTableArray =>
                    "the frames are written as an inline array instead of [[frame]] blocks",
                NonCanonicalReason.HeaderAsInlineTable =>
                    "the header is written as an inline table instead of a [header] section",
                NonCanonicalReason.HeaderAsDottedKeys =>
                    "the header values are written as dotted keys instead of a [header] section",
                NonCanonicalReason.DottedKeyInTable => "a value is written as a dotted key",
                NonCanonicalReason.SubTable => "the file contains a sub-table",
                _ => "the file does not use the standard layout",
            };
            return $"This file is valid, but {reason}, so frames cannot be edited visually.";
        }
    }

    /// <summary>Every diagnostic for this document, structural and asset, ordered by position.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;

    /// <summary>The asset pass's findings, including per-frame resolutions. Null until it first runs.</summary>
    public AssetAnalysis? Assets => _assets;

    /// <summary>Where this document's images are looked up.</summary>
    public AssetResolver Resolver => _resolver;

    /// <summary>Errors in the current document.</summary>
    public int ErrorCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>Warnings in the current document.</summary>
    public int WarningCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

    /// <summary>Informational notes in the current document.</summary>
    public int InfoCount => _diagnostics.Count(d => d.Severity == DiagnosticSeverity.Info);

    /// <summary>How many frames the last good model had.</summary>
    public int FrameCount => _model?.Frames.Count ?? 0;

    /// <summary>
    /// How long one cycle takes, in milliseconds, for the animation mode the file actually asks
    /// for. Zero when nothing animates.
    /// </summary>
    public long LoopDurationMs => _model is null ? 0 : Cairn.Atx.Playback.AtxPlayback.CycleDurationMs(_model);

    /// <summary>
    /// The timing summary for the status bar and the frames footer. The wording follows the mode,
    /// because "per loop" would be a plain untruth for a Static texture and would understate a
    /// ping-pong, which pays for the way back down as well.
    /// </summary>
    public string LoopDurationText
    {
        get
        {
            if (_model is null || _model.Frames.Count == 0) return "no frames";
            if (_model.Frames.Count == 1) return "one frame";
            string time = FormatDuration(LoopDurationMs);
            return _model.Header.EffectiveAnimationMode switch
            {
                AtxAnimationMode.Static => "does not animate",
                AtxAnimationMode.PingPong => time + " there and back",
                AtxAnimationMode.PlayOnce => time + " to play through",
                _ => time + " per loop",
            };
        }
    }

    /// <summary>Formats a millisecond count as "1.04 s" or "820 ms".</summary>
    public static string FormatDuration(long milliseconds) => milliseconds >= 1000
        ? (milliseconds / 1000.0).ToString("0.00", CultureInfo.CurrentCulture) + " s"
        : milliseconds.ToString(CultureInfo.CurrentCulture) + " ms";

    // ── Panels ────────────────────────────────────────────────────────────────

    public HeaderViewModel Header { get; }

    public FrameListViewModel Frames { get; }

    public FrameInspectorViewModel Inspector { get; }

    public ProblemsViewModel Problems { get; }

    /// <summary>The animated preview player for this document.</summary>
    public PreviewViewModel Preview { get; }

    /// <summary>Jumps to the first syntax error (the read-only banner's action).</summary>
    public RelayCommand GoToSyntaxErrorCommand { get; }

    /// <summary>Rewrites the file in the standard layout (the non-canonical banner's action).</summary>
    public RelayCommand ConvertLayoutCommand { get; }

    /// <summary>Re-reads the file from disk (the external-change bar's action).</summary>
    public RelayCommand ReloadCommand { get; }

    /// <summary>Keeps the in-memory version (the external-change bar's action).</summary>
    public RelayCommand KeepMineCommand { get; }

    /// <summary>Hides the "this file is not UTF-8" bar for this tab.</summary>
    public RelayCommand DismissEncodingNoticeCommand { get; }

    /// <summary>Save As… from the "deleted or renamed on disk" bar.</summary>
    public RelayCommand SaveACopyCommand { get; }

    /// <summary>Dismisses the "deleted or renamed on disk" bar and keeps editing.</summary>
    public RelayCommand KeepEditingCommand { get; }

    /// <summary>The shell that owns this document.</summary>
    public AtxWorkspace Shell => _main;

    // ── Caret / selection ─────────────────────────────────────────────────────

    /// <summary>1-based caret line, for the status bar.</summary>
    public int CaretLine { get => _caretLine; private set => Set(ref _caretLine, value); }

    /// <summary>1-based caret column, for the status bar.</summary>
    public int CaretColumn { get => _caretColumn; private set => Set(ref _caretColumn, value); }

    /// <summary>Raised when the source editor should scroll a span into view without taking focus.</summary>
    public event EventHandler<TextSpan>? RevealRequested;

    /// <summary>Raised when the source editor should select a span and take focus.</summary>
    public event EventHandler<TextSpan>? SelectRequested;

    /// <summary>Raised after a parse or an asset pass, so views can refresh their derived visuals.</summary>
    public event EventHandler? Refreshed;

    /// <summary>Raised when the diagnostics list changes, so squiggles and the panel update.</summary>
    public event EventHandler? DiagnosticsChanged;

    /// <summary>Called by the source editor whenever the caret moves.</summary>
    /// <param name="offset">Caret offset in the document.</param>
    /// <param name="line">1-based line.</param>
    /// <param name="column">1-based column.</param>
    /// <param name="editorHasFocus">True when the user is actually in the editor.</param>
    public void OnCaretMoved(int offset, int line, int column, bool editorHasFocus)
    {
        CaretLine = line;
        CaretColumn = column;
        if (!editorHasFocus || _syncingSelection) return;

        // Moving the caret into a frame block selects that frame — but only when the list is not
        // the thing the user is driving, so the two never fight.
        int? index = _parse.SyntaxMap.FrameIndexAt(offset);
        if (index is { } i && !Frames.IsFocused) Frames.SelectFromEditor(i);
    }

    /// <summary>The span of the selected frame's block, for the editor's soft highlight.</summary>
    public TextSpan? SelectedBlockSpan
    {
        get
        {
            int index = Frames.PrimarySelectedIndex;
            var blocks = _parse.SyntaxMap.FrameBlocks;
            return index >= 0 && index < blocks.Count ? blocks[index].Span : null;
        }
    }

    /// <summary>Scrolls a span into view in the source editor without stealing focus.</summary>
    public void Reveal(TextSpan span) => RevealRequested?.Invoke(this, span);

    /// <summary>Selects a span in the source editor and focuses it.</summary>
    public void SelectInSource(TextSpan span) => SelectRequested?.Invoke(this, span);

    /// <summary>Called by the frame list when its selection changes.</summary>
    public void OnFrameSelectionChanged()
    {
        _syncingSelection = true;
        try
        {
            Inspector.Refresh();
            if (SelectedBlockSpan is { } span) Reveal(span);
            Raise(nameof(SelectedBlockSpan));
            // The design's rule: picking a frame in the list pauses the preview on that frame.
            Preview.OnFrameSelected(Frames.PrimarySelectedIndex);
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        finally { _syncingSelection = false; }
    }

    // ── The pipeline ──────────────────────────────────────────────────────────

    private void OnDocumentChanged(object? sender, DocumentChangeEventArgs e)
    {
        _parseStale = true;
        if (_applyingGuiEdit) return; // Reparse runs immediately once the batch is applied.
        _parseDebounce.Stop();
        _parseDebounce.Start();
    }

    private void OnUndoStackChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Document.UndoStack.IsOriginalFile)
            or nameof(Document.UndoStack.CanUndo) or nameof(Document.UndoStack.CanRedo))
        {
            IsDirty = !Document.UndoStack.IsOriginalFile;
            _main.RefreshCommands();
        }
    }

    /// <summary>
    /// Makes sure <see cref="Parse"/> describes the current text. GUI edits must never be built
    /// against a stale parse, because every span in a <see cref="TextEditBatch"/> would be wrong.
    /// </summary>
    public void EnsureParsed()
    {
        if (_parseStale) { _parseDebounce.Stop(); Reparse(); }
    }

    /// <param name="ready">
    /// A parse of the current text that has already been done — by the constructor, or by the
    /// worker thread that opens a large file. Anything else re-parses here.
    /// </param>
    private void Reparse(AtxParseResult? ready = null)
    {
        if (_disposed) return;
        _parseStale = false;
        string text = Document.Text;
        _parse = ready is not null && string.Equals(ready.Text, text, StringComparison.Ordinal)
            ? ready
            : AtxParser.Parse(text);
        if (_parse.Model is not null) _model = _parse.Model;

        _structural = AtxLinter.Analyze(_parse, new LintOptions { DocumentPath = _filePath });
        // Keep the previous asset facts on screen (dimensions, engine format, where each file was
        // found) until the new pass replaces them — blanking them on every typing pause makes the
        // inspector and the preview readout flicker. Only the diagnostics are held back, because
        // their spans belong to the parse that produced them.
        _assetsStale = true;
        MergeDiagnostics();

        RaiseAll(
            nameof(Parse), nameof(Model), nameof(HasSyntaxErrors), nameof(IsCanonical),
            nameof(CanEditValues), nameof(CanEditStructure), nameof(SyntaxError),
            nameof(SyntaxErrorText), nameof(NonCanonicalText), nameof(FrameCount),
            nameof(LoopDurationMs), nameof(LoopDurationText), nameof(SelectedBlockSpan));

        GoToSyntaxErrorCommand.RaiseCanExecuteChanged();
        ConvertLayoutCommand.RaiseCanExecuteChanged();

        Header.Refresh();
        Frames.Refresh();
        Inspector.Refresh();
        Preview.OnDocumentRefreshed();
        Refreshed?.Invoke(this, EventArgs.Empty);
        _main.RefreshCommands();

        // A plain copy of the text, taken here on the UI thread, is what the crash handler writes
        // when the dispatcher is too wedged to hand it the real TextDocument.
        _main.RecordDocumentState(this, text);

        StartAssetLint();
    }

    private void StartAssetLint()
    {
        _assetLint?.Cancel();
        _assetLint?.Dispose();
        _assetLint = null;

        var parse = _parse;
        if (parse.Model is null) return;   // nothing to check until the TOML parses again

        var cts = new CancellationTokenSource();
        _assetLint = cts;
        _ = RunAssetLintAsync(parse, _resolver, cts.Token);
    }

    private async Task RunAssetLintAsync(AtxParseResult parse, AssetResolver resolver, CancellationToken token)
    {
        try
        {
            var analysis = await AtxAssetLinter
                .AnalyzeAsync(parse, resolver, new LintOptions { DocumentPath = _filePath }, token)
                .ConfigureAwait(true);
            if (token.IsCancellationRequested || _disposed || !ReferenceEquals(parse, _parse)) return;

            _assetLintError = null;
            _assets = analysis;
            _assetsStale = false;
            MergeDiagnostics();
            Raise(nameof(Assets));
            Frames.OnAssetsChanged();
            // The header panel reads the asset diagnostics too (the alpha-mask message and its
            // "Locate file…" action), and the asset pass is the only thing that produces them.
            Header.Refresh();
            Inspector.Refresh();
            Preview.OnDocumentRefreshed();
            Refreshed?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // This task is started with a discard, so anything that escapes here would come back
            // later as an unobserved task exception and take the whole app down with the crash
            // handler. The user gets one line in the Problems panel instead, and the details go to
            // the log.
            ErrorLog.Write("asset lint", ex);
            if (token.IsCancellationRequested || _disposed || !ReferenceEquals(parse, _parse)) return;
            _assetLintError = new Diagnostic(
                AtxRules.ImageUnreadable, DiagnosticSeverity.Warning,
                "The images in this file could not be checked.",
                "Something went wrong while looking at the frame images, so the image warnings below "
                + "may be incomplete. Editing the file runs the check again. Details are in "
                + ErrorLog.Path + ".",
                new Cairn.Atx.Text.TextSpan(0, 0));
            MergeDiagnostics();
        }
    }

    private void MergeDiagnostics()
    {
        var merged = new List<Diagnostic>(_structural);
        if (_assets is not null && !_assetsStale) merged.AddRange(_assets.Diagnostics);
        if (_assetLintError is not null) merged.Add(_assetLintError);
        _diagnostics =
        [
            .. merged
                .OrderBy(d => d.Span.Start)
                .ThenByDescending(d => d.Severity)
                .ThenBy(d => d.Code, StringComparer.Ordinal),
        ];
        RaiseAll(nameof(Diagnostics), nameof(ErrorCount), nameof(WarningCount), nameof(InfoCount));
        Problems.Refresh();
        DiagnosticsChanged?.Invoke(this, EventArgs.Empty);
        _main.OnDocumentDiagnosticsChanged(this);
    }

    // ── Applying edits ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a batch with <paramref name="operation"/> and applies it. This is the only way the
    /// GUI changes a document: no view-model ever mutates a model.
    /// </summary>
    /// <returns>True when the document text actually changed.</returns>
    public bool Edit(Func<AtxEditor, TextEditBatch> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (HasSyntaxErrors) return false;
        EnsureParsed();
        var editor = new AtxEditor(Document.Text, _parse);
        return ApplyEdits(operation(editor));
    }

    /// <summary>
    /// Applies a batch to the document as one undo step. Edits are applied back to front so the
    /// offsets of the earlier ones stay valid, and the whole batch sits inside a single
    /// <c>BeginUpdate</c>/<c>EndUpdate</c> group, which is what makes Ctrl+Z undo a GUI action in
    /// one press.
    /// </summary>
    /// <returns>True when anything was applied.</returns>
    public bool ApplyEdits(TextEditBatch batch)
    {
        if (batch is null || batch.IsEmpty || HasSyntaxErrors) return false;

        _applyingGuiEdit = true;
        Document.BeginUpdate();
        try
        {
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                var edit = batch[i];
                int start = Math.Clamp(edit.Span.Start, 0, Document.TextLength);
                int length = Math.Clamp(edit.Span.Length, 0, Document.TextLength - start);
                Document.Replace(start, length, edit.NewText);
            }
        }
        finally
        {
            Document.EndUpdate();
            _applyingGuiEdit = false;
        }

        _parseDebounce.Stop();
        Reparse();
        return true;
    }

    /// <summary>
    /// Opens an undo group that stays open until <see cref="EndInteraction"/>. Spinner drags and
    /// wheel stepping use this so a whole gesture is one Ctrl+Z.
    /// </summary>
    public void BeginInteraction()
    {
        Document.UndoStack.StartUndoGroup();
        _interactionDepth++;
    }

    /// <summary>Closes the group opened by <see cref="BeginInteraction"/>.</summary>
    public void EndInteraction()
    {
        if (_interactionDepth == 0) return;
        _interactionDepth--;
        try { Document.UndoStack.EndUndoGroup(); }
        catch (InvalidOperationException) { /* already closed by an undo during the gesture */ }
    }

    /// <summary>
    /// Closes any gesture group still open. AvalonEdit throws from Undo, Redo and ClearAll while a
    /// group is open, and a spinner's group stays open for up to 600 ms after the last step — long
    /// enough for the user to reach Ctrl+Z, or to switch tabs and never close it at all.
    /// </summary>
    private void CloseOpenInteractions()
    {
        while (_interactionDepth > 0) EndInteraction();
    }

    /// <summary>Undoes the last change of any kind — typed, or produced by a GUI control.</summary>
    public void Undo()
    {
        CloseOpenInteractions();
        if (Document.UndoStack.CanUndo) Document.UndoStack.Undo();
    }

    /// <summary>Redoes the last undone change.</summary>
    public void Redo()
    {
        CloseOpenInteractions();
        if (Document.UndoStack.CanRedo) Document.UndoStack.Redo();
    }

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => Document.UndoStack.CanUndo;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => Document.UndoStack.CanRedo;

    /// <summary>Rewrites the file in the standard layout (the non-canonical banner's button).</summary>
    public void ConvertToStandardLayout()
    {
        EnsureParsed();
        var editor = new AtxEditor(Document.Text, _parse);
        ApplyEdits(editor.Normalize());
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>Marks the current text as the saved state and records the path.</summary>
    public void MarkSaved(string path)
    {
        // Remember what actually reached the disk, not what is in the buffer: the save normalises
        // line endings, so for a file with mixed endings the two differ. If they differ, the very
        // next watcher tick reads the file back, decides it changed externally and silently
        // replaces the document — taking the whole undo history with it.
        _lineEnding = LineEndings.Detect(Document.Text);
        _savedText = LineEndings.Normalize(Document.Text, _lineEnding);
        FilePath = path;
        Document.UndoStack.MarkAsOriginalFile();
        IsDirty = false;
        HasExternalChange = false;
        IsMissingOnDisk = false;
        // The save is the conversion the notice bar promised: what is on disk is UTF-8 now.
        SetEncoding(AtxFileEncoding.Utf8);
        _encodingNoticeDismissed = false;
        RaiseAll(nameof(DisplayName), nameof(TabHeader), nameof(LineEndingLabel),
            nameof(HasEncodingNotice), nameof(MissingFileText));
        // The path feeds the name-length rule, so the document has to be re-linted after a Save As.
        Reparse();
    }

    /// <summary>The text as it would be written to disk.</summary>
    public string TextForSave => Document.Text;

    /// <summary>Replaces the whole document, for Reload and crash recovery.</summary>
    public void ReplaceAll(string text, bool markSaved)
    {
        // UndoStack.ClearAll below throws while a gesture group is open.
        CloseOpenInteractions();
        Document.Replace(0, Document.TextLength, text);
        if (markSaved)
        {
            _savedText = text;
            _lineEnding = LineEndings.Detect(text);
            Document.UndoStack.ClearAll();
            Document.UndoStack.MarkAsOriginalFile();
            IsDirty = false;
            Raise(nameof(LineEndingLabel));
        }
        HasExternalChange = false;
        _parseDebounce.Stop();
        Reparse();
    }

    // ── External changes ──────────────────────────────────────────────────────

    /// <summary>True when the file changed on disk behind the app's back.</summary>
    public bool HasExternalChange
    {
        get => _hasExternalChange;
        private set => Set(ref _hasExternalChange, value);
    }

    /// <summary>
    /// True when the file this tab came from is no longer where it was. Saving would quietly create
    /// it again somewhere the user is no longer looking, so the tab says so instead.
    /// </summary>
    public bool IsMissingOnDisk
    {
        get => _isMissingOnDisk;
        private set => Set(ref _isMissingOnDisk, value);
    }

    /// <summary>What the "deleted or renamed" bar says.</summary>
    public string MissingFileText =>
        $"'{DisplayName}' was deleted or renamed on disk. Saving would write it back to the old path.";

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>How the file was decoded when it was read.</summary>
    public AtxFileEncoding Encoding => _encoding;

    /// <summary>
    /// True while the tab should say that this file is not UTF-8. The game's TOML parser only reads
    /// UTF-8, and this app only writes UTF-8, so the conversion is the repair — but it is a change
    /// to the bytes on disk and the user is entitled to know it is coming.
    /// </summary>
    public bool HasEncodingNotice =>
        !_encodingNoticeDismissed && _encoding is not (AtxFileEncoding.Utf8 or AtxFileEncoding.Utf8Bom);

    /// <summary>The notice bar's sentence.</summary>
    public string EncodingNoticeText =>
        $"This file was saved as {AtxTextFiles.DescribeEncoding(_encoding)}. It will be converted to "
        + "UTF-8 when you save, which is what the game expects.";

    private void DismissEncodingNotice()
    {
        _encodingNoticeDismissed = true;
        Raise(nameof(HasEncodingNotice));
    }

    private void SetEncoding(AtxFileEncoding encoding)
    {
        if (_encoding == encoding) return;
        _encoding = encoding;
        _encodingNoticeDismissed = false;
        RaiseAll(nameof(Encoding), nameof(HasEncodingNotice), nameof(EncodingNoticeText));
    }

    private void KeepEditingMissingFile()
    {
        IsMissingOnDisk = false;
        // The tab is the only copy left, so it must not close without asking.
        MarkDirtyForLostFile();
    }

    private void MarkDirtyForLostFile()
    {
        if (IsDirty) return;
        Document.UndoStack.DiscardOriginalFileMarker();
        IsDirty = true;
    }

    /// <summary>Suspends the file watchers while the app itself writes the file.</summary>
    public void SuspendFileWatch(bool suspended)
    {
        _documentWatcher.IsSuspended = suspended;
        // The atomic save writes a temp file next to the .atx and then replaces it, both of which
        // the folder watcher sees. Without this, every Ctrl+S drops every thumbnail and reloads
        // the preview for no reason.
        _folderWatcher.IsSuspended = suspended;
    }

    /// <summary>Discards in-memory changes and re-reads the file.</summary>
    public void ReloadFromDisk()
    {
        if (_filePath is null) return;
        try
        {
            var file = AtxTextFiles.Read(_filePath);
            SetEncoding(file.Encoding);
            IsMissingOnDisk = false;
            ReplaceAll(file.Text, markSaved: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave the bar up: the reload did not happen, so the offer should still stand.
            _main.Dialogs.ShowError(
                "That file could not be reloaded.",
                $"'{Path.GetFileName(_filePath)}' could not be read just now — it is probably still "
                + "open in another program.",
                ex.Message);
        }
    }

    /// <summary>
    /// Puts recovered or reopened text into a tab that already exists, as unsaved changes. Used
    /// instead of opening a second tab on the same file, which is how one of the two ends up
    /// silently overwriting the other.
    /// </summary>
    /// <param name="text">The text to restore.</param>
    public void RestoreText(string text)
    {
        if (string.Equals(Document.Text, text, StringComparison.Ordinal))
        {
            KeepMine();
            return;
        }
        ReplaceAll(text, markSaved: false);
        KeepMine();
    }

    /// <summary>
    /// Says that the file on disk is newer than the text this tab is holding, so Ctrl+S would
    /// overwrite somebody's newer work. Shows the same bar an external change raises.
    /// </summary>
    public void NoteDiskIsNewer()
    {
        if (_filePath is null) return;
        HasExternalChange = true;
        MarkDirtyForLostFile();
    }

    /// <summary>Keeps the in-memory version and stops nagging about the external change.</summary>
    public void KeepMine()
    {
        HasExternalChange = false;
        IsDirty = true;
        Document.UndoStack.DiscardOriginalFileMarker();
    }

    private void CheckExternalChange()
    {
        if (_filePath is null || _disposed) return;
        try
        {
            if (!File.Exists(_filePath))
            {
                // Deleted or renamed. Saving would recreate it at the old path without a word, so
                // say what happened and make the tab dirty: it now holds the only copy.
                if (!IsMissingOnDisk)
                {
                    IsMissingOnDisk = true;
                    HasExternalChange = false;
                    MarkDirtyForLostFile();
                    RaiseAll(nameof(MissingFileText));
                }
                return;
            }
            IsMissingOnDisk = false;

            // A silent adopt-from-disk while a modal is open would replace the document the dialog
            // has already read its plan from; leave it for when the dialog closes.
            if (Cairn.Ui.Services.ModalScope.IsOpen)
            {
                Cairn.Ui.Services.ModalScope.Closed += OnModalClosed;
                return;
            }

            var file = AtxTextFiles.Read(_filePath);
            SetEncoding(file.Encoding);
            string onDisk = file.Text;
            if (string.Equals(onDisk, Document.Text, StringComparison.Ordinal))
            {
                HasExternalChange = false;
                return;
            }
            // What is on disk is exactly what we last wrote, so nothing changed behind our back —
            // whether or not the user has typed since. Suspending the watcher around the write is
            // not enough on its own: the notification is delivered asynchronously and usually
            // arrives after the suspension has already been lifted.
            if (string.Equals(onDisk, _savedText, StringComparison.Ordinal))
            {
                HasExternalChange = false;
                return;
            }
            if (!IsDirty)
            {
                // Nothing to lose: adopt the new content silently, which is what an artist
                // re-saving the .atx from another tool expects.
                ReplaceAll(onDisk, markSaved: true);
                return;
            }
            HasExternalChange = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file is mid-write; the next notification in the burst will settle it.
        }
    }

    private void OnModalClosed(object? sender, EventArgs e)
    {
        Cairn.Ui.Services.ModalScope.Closed -= OnModalClosed;
        if (!_disposed) CheckExternalChange();
    }

    private void StartWatching()
    {
        if (_filePath is not null)
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(_filePath));
            _documentWatcher.Watch(folder, Path.GetFileName(_filePath));
        }
        else
        {
            _documentWatcher.Watch(null);
        }
        _folderWatcher.Watch(AtxFolder);
    }

    private void OnAssetFolderChanged()
    {
        _resolver.Invalidate();
        _main.InvalidateImageCaches();
        Frames.ClearThumbnails();
        Preview.ReloadAll();
        StartAssetLint();
        Frames.OnAssetsChanged();
    }

    // ── Assets ────────────────────────────────────────────────────────────────

    /// <summary>Rebuilds the resolver after the ATX folder or the app's search folders change.</summary>
    public void RebuildResolver()
    {
        _resolver = BuildResolver();
        _main.InvalidateImageCaches();
        Frames.ClearThumbnails();
        Preview.ReloadAll();
        Raise(nameof(Resolver));
        StartWatching();
        StartAssetLint();
        Frames.OnAssetsChanged();
    }

    private AssetResolver BuildResolver() => _main.Shell.Assets.ResolverFor(AtxFolder);

    /// <summary>A token that cancels every in-flight thumbnail request for this document.</summary>
    public CancellationToken ThumbnailToken => _thumbnails.Token;

    /// <summary>Cancels outstanding thumbnail work, e.g. after a reload.</summary>
    public void CancelThumbnails()
    {
        var old = _thumbnails;
        _thumbnails = new CancellationTokenSource();
        old.Cancel();
        old.Dispose();
    }

    /// <summary>The asset resolution for one frame, once the asset pass has run.</summary>
    public ResolvedAsset? ResolvedFrame(int index)
    {
        var frames = _assets?.Frames;
        if (frames is null || index < 0 || index >= frames.Count) return null;
        // While a pass is pending these are last pass's answers. They stay right for an edit that
        // does not change the frame list, which is the common case; when the list did change, the
        // indices no longer line up, so say nothing rather than name the wrong file.
        if (_assetsStale && frames.Count != (_model?.Frames.Count ?? 0)) return null;
        return frames[index];
    }

    // ── Adding frames ─────────────────────────────────────────────────────────

    /// <summary>
    /// Adds image files as frames at <paramref name="insertIndex"/>. Files that live outside every
    /// search location trigger the design's one-per-batch copy-or-reference prompt; frames always
    /// store the bare file name, because RF's file system is flat.
    /// </summary>
    /// <param name="paths">Full paths of the chosen images.</param>
    /// <param name="insertIndex">Where to insert, or -1 to append.</param>
    /// <param name="naturalSort">True to order a multi-file selection naturally.</param>
    /// <param name="replaceAll">
    /// True to drop every existing frame first. The removal and the insertion share one undo group,
    /// so replacing a sequence is still a single Ctrl+Z — and the copy-or-reference prompt is
    /// answered before anything is removed, so cancelling it leaves the document untouched.
    /// </param>
    public void AddImageFiles(
        IReadOnlyList<string> paths, int insertIndex, bool naturalSort = true, bool replaceAll = false)
    {
        if (paths.Count == 0 || !CanEditStructure) return;

        var ordered = naturalSort && paths.Count > 1
            ? paths.OrderBy(p => Path.GetFileName(p), Cairn.Formats.Text.NaturalStringComparer.Instance).ToList()
            : [.. paths];

        // An unsaved document adopts the first added image's folder as its ATX folder.
        if (_filePath is null && _impliedFolder is null)
        {
            _impliedFolder = Path.GetDirectoryName(Path.GetFullPath(ordered[0]));
            Raise(nameof(AtxFolder));
            RebuildResolver();
        }

        // A sequence with a reversed tail names the same file twice; ask about it once.
        var outside = ordered
            .Where(p => !IsInSearchScope(p))
            .DistinctBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (outside.Count > 0 && !CopyOrReference(outside)) return;

        InsertFrameNames([.. ordered.Select(p => Path.GetFileName(p))], insertIndex, replaceAll);
    }

    /// <summary>
    /// Adds frames by name, for images that are already somewhere the game will look — entries of a
    /// .vpp on the search path, chosen in the Add Frames from VPP browser. The names go through the
    /// same insertion path as <see cref="AddImageFiles"/>, so one Ctrl+Z still undoes the lot; what
    /// is skipped is only the copy-or-reference prompt, because the browser has already settled that
    /// question and done any extraction itself.
    /// </summary>
    /// <param name="names">Bare file names, in the order they should become frames.</param>
    /// <param name="insertIndex">Where to insert, or -1 to append.</param>
    /// <param name="replaceAll">True to drop every existing frame first, in the same undo step.</param>
    public void AddImageNames(IReadOnlyList<string> names, int insertIndex, bool replaceAll = false)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0 || !CanEditStructure) return;
        // RF's file system is flat and a frame stores a bare name, so anything that came back with
        // a folder on it is not a name this document can use.
        var bare = new List<string>(names.Count);
        foreach (string name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            string only;
            try { only = Path.GetFileName(name); }
            catch (ArgumentException) { continue; }
            if (only.Length > 0) bare.Add(only);
        }
        if (bare.Count == 0) return;
        InsertFrameNames(bare, insertIndex, replaceAll);
    }

    /// <summary>
    /// The one insertion path: bare names in, one undo step out, caches invalidated and the new
    /// frames selected. Both Add Frames… and Add Frames from VPP… end here, so neither can drift
    /// into producing a different kind of edit from the other.
    /// </summary>
    private void InsertFrameNames(IReadOnlyList<string> names, int insertIndex, bool replaceAll)
    {
        var frames = names.Select(n => new NewFrame(n)).ToList();
        int index = insertIndex < 0 ? FrameCount : insertIndex;

        if (replaceAll && FrameCount > 0)
        {
            index = 0;
            BeginInteraction();
            try
            {
                Edit(editor => editor.RemoveFrames([.. Enumerable.Range(0, FrameCount)]));
                Edit(editor => editor.InsertFrames(0, frames));
            }
            finally { EndInteraction(); }
        }
        else
        {
            Edit(editor => editor.InsertFrames(index, frames));
        }

        _resolver.Invalidate();
        _main.InvalidateImageCaches();
        Frames.ClearThumbnails();
        Preview.ReloadAll();
        Frames.SelectRange(index, frames.Count);
        StartAssetLint();
        Frames.OnAssetsChanged();
    }

    /// <summary>
    /// Writes archive entries out next to the .atx, so frames referring to them by name resolve
    /// like any other loose file. Uses the same collision rule as copying a file in from outside —
    /// a name already taken by *different* bytes is never silently left alone, and the user is
    /// asked once for the whole batch. Returns false when they cancelled, which means the whole
    /// operation is off.
    /// </summary>
    /// <param name="entries">The archive each entry came from, and the entry itself.</param>
    public bool ExtractArchiveEntries(IReadOnlyList<(VppArchive Archive, VppEntry Entry)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0) return true;
        if (AtxFolder is not { } folder) return false;

        string folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(folderName)) folderName = folder;

        var plan = new List<(VppArchive Archive, VppEntry Entry, string Destination)>();
        var clashes = new List<(VppArchive Archive, VppEntry Entry, string Destination)>();
        var refused = new List<string>();

        foreach (var (archive, entry) in entries)
        {
            // A .vpp may have come out of a downloaded map pack and its 60-byte name field can hold
            // anything, including a path that climbs out of the folder. Nothing is written without
            // passing this first.
            string? destination = VppExtraction.DestinationFor(folder, entry.Name);
            if (destination is null)
            {
                refused.Add(entry.Name);
                continue;
            }
            if (!File.Exists(destination)) { plan.Add((archive, entry, destination)); continue; }
            if (!SameEntryContent(archive, entry, destination))
                clashes.Add((archive, entry, destination));
        }

        if (clashes.Count > 0)
        {
            var answer = _main.Dialogs.AskReplaceExisting(
                [.. clashes.Select(c => Path.GetFileName(c.Destination))], folderName);
            if (answer == ReplaceChoice.Cancel) return false;
            if (answer == ReplaceChoice.Replace) plan.AddRange(clashes);
        }

        var failures = new List<string>();
        foreach (var (archive, entry, destination) in plan)
        {
            try
            {
                using var source = archive.OpenEntry(entry);
                using var target = File.Create(destination);
                source.CopyTo(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException or Cairn.Formats.Vpp.VppFormatException)
            {
                failures.Add($"{entry.Name} — {ex.Message}");
            }
        }

        if (refused.Count > 0)
        {
            _main.Dialogs.ShowError(
                refused.Count == 1 ? "One entry could not be extracted." : "Some entries could not be extracted.",
                "Their names inside the archive are not plain file names, so writing them could put "
                + "a file somewhere other than your project folder. They were left alone; the frames "
                + "still refer to them by name.",
                string.Join(Environment.NewLine, refused));
        }
        if (failures.Count > 0)
        {
            _main.Dialogs.ShowError(
                failures.Count == 1 ? "An image could not be extracted." : "Some images could not be extracted.",
                $"They could not be written into {folderName}. The frames still refer to them by name.",
                string.Join(Environment.NewLine, failures));
        }
        return true;
    }

    /// <summary>True when an archive entry holds exactly the bytes already on disk at that name.</summary>
    private static bool SameEntryContent(VppArchive archive, VppEntry entry, string destination)
    {
        try
        {
            var existing = new FileInfo(destination);
            if (!existing.Exists || existing.Length != entry.Size) return false;
            using var stream = archive.OpenEntry(entry);
            var fromArchive = System.Security.Cryptography.SHA256.HashData(stream);
            using var onDisk = File.OpenRead(destination);
            return fromArchive.SequenceEqual(System.Security.Cryptography.SHA256.HashData(onDisk));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException or Cairn.Formats.Vpp.VppFormatException)
        {
            // If we cannot tell them apart, treat them as different: asking is the safe answer.
            return false;
        }
    }

    /// <summary>
    /// Points one existing frame at an image chosen from disk, applying the same copy-or-reference
    /// rule as Add Frames… because the frame will only store the bare file name.
    /// </summary>
    /// <param name="path">Full path of the chosen image.</param>
    /// <param name="frameIndex">The frame to repoint.</param>
    public void AddFrameImageFromPath(string path, int frameIndex)
    {
        if (!CanEditStructure) return;
        string name = Path.GetFileName(path);

        if (_filePath is null && _impliedFolder is null)
        {
            _impliedFolder = Path.GetDirectoryName(Path.GetFullPath(path));
            Raise(nameof(AtxFolder));
            RebuildResolver();
        }

        if (!IsInSearchScope(path) && !CopyOrReference([path])) return;

        Edit(editor => editor.SetFrameValue([frameIndex], AtxSchema.KeyFile, AtxValue.String(name)));
        _resolver.Invalidate();
        _main.InvalidateImageCaches();
        Frames.ClearThumbnails();
        Preview.ReloadAll();
        StartAssetLint();
    }

    /// <summary>
    /// A file name safe to pre-fill a picker with, or null. The value comes out of the .atx, so it
    /// may be anything at all — a path, a device name, characters Windows will not accept.
    /// </summary>
    private static string? SafeFileNameSuggestion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string name;
        try { name = Path.GetFileName(value); }
        catch (ArgumentException) { return null; }
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        return name;
    }

    /// <summary>
    /// Asks whether images from outside the search path should be copied next to the .atx, and
    /// copies them if so. Returns false when the user cancelled, which means the whole operation
    /// is off.
    /// </summary>
    /// <param name="sources">Full paths of the images concerned.</param>
    private bool CopyOrReference(IReadOnlyList<string> sources)
    {
        string folderName = AtxFolder is { } f
            ? Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar))
            : "the .atx folder";
        if (string.IsNullOrEmpty(folderName)) folderName = AtxFolder ?? "the .atx folder";

        // Full paths, not bare names: which file this is about is exactly the thing the user needs
        // to see before agreeing to copy it into their project folder.
        var choice = _main.Dialogs.AskOutsideFiles(sources, folderName);
        if (choice == OutsideFileChoice.Cancel) return false;
        if (choice != OutsideFileChoice.Copy) return true;
        return CopyNextToAtx(sources, folderName);
    }

    /// <summary>
    /// Copies images next to the .atx. A name already taken by a different file is never silently
    /// left alone — that is how a frame ends up showing an image the designer did not choose — so
    /// the user is asked once for the whole batch. Returns false when they cancelled.
    /// </summary>
    private bool CopyNextToAtx(IReadOnlyList<string> sources, string folderName)
    {
        if (AtxFolder is not { } folder) return true;

        var plan = new List<(string Source, string Destination)>();
        var clashes = new List<(string Source, string Destination)>();
        foreach (string source in sources)
        {
            string destination;
            try
            {
                destination = Path.Combine(folder, Path.GetFileName(source));
                if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // already exactly where it needs to be
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                or PathTooLongException)
            {
                continue;
            }

            if (!File.Exists(destination)) { plan.Add((source, destination)); continue; }
            // Same bytes under the same name is not a clash at all; there is nothing to ask about.
            if (!SameFileContent(source, destination)) clashes.Add((source, destination));
        }

        if (clashes.Count > 0)
        {
            var answer = _main.Dialogs.AskReplaceExisting(
                [.. clashes.Select(c => Path.GetFileName(c.Destination))], folderName);
            if (answer == ReplaceChoice.Cancel) return false;
            if (answer == ReplaceChoice.Replace) plan.AddRange(clashes);
        }

        foreach (var (source, destination) in plan)
        {
            try { File.Copy(source, destination, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or NotSupportedException)
            {
                _main.Dialogs.ShowError(
                    "The image could not be copied.",
                    $"'{Path.GetFileName(source)}' could not be copied next to the .atx file. "
                    + "The frame still refers to it by name.", ex.Message);
            }
        }
        return true;
    }

    /// <summary>True when two files hold exactly the same bytes.</summary>
    private static bool SameFileContent(string a, string b)
    {
        try
        {
            var first = new FileInfo(a);
            var second = new FileInfo(b);
            if (!first.Exists || !second.Exists || first.Length != second.Length) return false;
            return Hash(a).SequenceEqual(Hash(b));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            // If we cannot tell them apart, treat them as different: asking is the safe answer.
            return false;
        }

        static byte[] Hash(string path)
        {
            using var stream = File.OpenRead(path);
            return System.Security.Cryptography.SHA256.HashData(stream);
        }
    }

    /// <summary>True when a full path is somewhere the engine (and the resolver) would look.</summary>
    public bool IsInSearchScope(string path)
    {
        try
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
            if (Same(folder, AtxFolder)) return true;
            foreach (string search in _main.Settings.SearchFolders)
            {
                if (Same(folder, search)) return true;
            }
            if (_main.Settings.GameDirectory is { } game)
            {
                if (Same(folder, game)) return true;
                foreach (string sub in AssetResolver.GameSubFolders)
                {
                    if (Same(folder, Path.Combine(game, "user_maps", sub))) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool Same(string a, string? b)
    {
        if (string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    // ── Quick fixes ───────────────────────────────────────────────────────────

    /// <summary>
    /// Carries out a quick fix. Edit fixes are applied directly; the rest are UI actions the
    /// design assigns to the app (a file picker, the Add Frames flow, the layout conversion).
    /// </summary>
    /// <param name="fix">The fix to apply.</param>
    /// <param name="diagnostic">The diagnostic it came from, for context.</param>
    public void ApplyQuickFix(QuickFix fix, Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(fix);
        switch (fix.Kind)
        {
            case QuickFixKind.Edit:
                ApplyEditFix(fix, diagnostic);
                break;
            case QuickFixKind.ConvertToStandardLayout:
                ConvertToStandardLayout();
                break;
            case QuickFixKind.AddFrames:
                Frames.AddFramesCommand.Execute(null);
                break;
            case QuickFixKind.OpenSearchSettings:
                OpenSearchSettings();
                break;
            case QuickFixKind.LocateFile:
                LocateFile(fix.Payload, diagnostic.FrameIndex, diagnostic.Key);
                break;
        }
    }

    /// <summary>
    /// Applies an edit quick fix, but only one that still describes the text as it is now.
    /// A <see cref="QuickFix"/> is a closure over the spans of the parse that produced it, so
    /// applying a fix the user raised before typing — or before a debounced reparse landed while
    /// the Ctrl+. menu was open — would replace the wrong range and corrupt the file. Reference
    /// identity against the live list is the provenance test: <c>MergeDiagnostics</c> builds a new
    /// list of new objects on every parse, so a diagnostic still in it belongs to the current one.
    /// </summary>
    private void ApplyEditFix(QuickFix fix, Diagnostic diagnostic)
    {
        EnsureParsed();
        if (Diagnostics.Any(d => ReferenceEquals(d, diagnostic)))
        {
            ApplyEdits(fix.Apply());
            return;
        }

        // The text moved under the fix. Re-offer the same fix from the current diagnostics if the
        // problem is still there, so the click is not simply lost; otherwise do nothing, because
        // the problem the user clicked no longer exists.
        var live = Diagnostics.FirstOrDefault(d =>
            d.Code == diagnostic.Code && d.FrameIndex == diagnostic.FrameIndex
            && string.Equals(d.Key, diagnostic.Key, StringComparison.Ordinal));
        var replacement = live?.QuickFixes.FirstOrDefault(f =>
            f.Kind == QuickFixKind.Edit && string.Equals(f.Title, fix.Title, StringComparison.Ordinal));
        if (replacement is not null) ApplyEdits(replacement.Apply());
    }

    /// <summary>Opens Settings, where the game folder and the extra search folders live.</summary>
    public void OpenSearchSettings() => _main.SettingsCommand.Execute(null);

    /// <summary>
    /// True when the asset pass has looked for <paramref name="index"/>'s image and found nothing.
    /// Deliberately false while the pass has not run yet, or while its answers no longer line up
    /// with the frame list: a "not found" that is really "not looked for" would put a warning on a
    /// frame that is perfectly fine.
    /// </summary>
    public bool IsFrameImageMissing(int index)
    {
        var resolved = ResolvedFrame(index);
        return resolved is { Location: null } && resolved.RequestedName.Length > 0
            && !resolved.RequestedName.EndsWith(".atx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name frame <paramref name="index"/> asks for, as written in the file.</summary>
    public string? RequestedFrameImage(int index) => ResolvedFrame(index)?.RequestedName;

    /// <summary>
    /// Points one frame at an image the user picks, for the "Locate file…" actions outside the
    /// Problems panel. Same flow as the quick fix: one picker, the copy-or-reference prompt when
    /// the file lives outside every search location, and one undo step if the name has to change.
    /// </summary>
    /// <param name="index">The frame whose image is missing.</param>
    public void LocateFrameImage(int index)
    {
        if (!CanEditStructure) return;
        string? name = RequestedFrameImage(index);
        if (string.IsNullOrEmpty(name)) return;
        LocateFile(name, index, AtxSchema.KeyFile);
    }

    /// <summary>Points the header's alpha mask at an image the user picks.</summary>
    public void LocateAlphaMask()
    {
        if (!CanEditValues) return;
        string? name = _model?.Header.EffectiveAlphaMask;
        if (string.IsNullOrEmpty(name)) return;
        LocateFile(name, null, AtxSchema.KeyAlphaMask);
    }

    private void LocateFile(string? missingName, int? frameIndex, string? key)
    {
        // Only the bare name goes into the picker, and only when it is a usable file name. The
        // value comes out of the .atx, which may have been written by anyone: a "file" of
        // "..\\..\\Users\\someone\\.ssh\\id_rsa" would otherwise pre-fill the dialog with a path
        // pointing well outside the folder the designer thinks they are looking at.
        string? suggested = SafeFileNameSuggestion(missingName);
        string? chosen = _main.Dialogs.OpenImageFile(
            AtxFolder, $"Locate '{UserText.Printable(missingName)}'", suggested);
        if (chosen is null) return;

        string chosenName = Path.GetFileName(chosen);
        if (!IsInSearchScope(chosen) && !CopyOrReference([chosen])) return;

        // If the picked file has a different name from the one written in the .atx, rewrite it.
        if (!string.Equals(chosenName, missingName, StringComparison.OrdinalIgnoreCase))
        {
            if (frameIndex is { } frame)
            {
                Edit(editor => editor.SetFrameValue([frame], AtxSchema.KeyFile, AtxValue.String(chosenName)));
            }
            else if (key == AtxSchema.KeyAlphaMask)
            {
                Edit(editor => editor.SetHeaderValue(AtxSchema.KeyAlphaMask, AtxValue.String(chosenName)));
            }
        }

        _resolver.Invalidate();
        _main.InvalidateImageCaches();
        Frames.ClearThumbnails();
        Preview.ReloadAll();
        StartAssetLint();
        Frames.OnAssetsChanged();
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cairn.Ui.Services.ModalScope.Closed -= OnModalClosed;
        _main.ForgetDocumentState(Id);
        Preview.Dispose();
        _parseDebounce.Stop();
        Document.Changed -= OnDocumentChanged;
        Document.UndoStack.PropertyChanged -= OnUndoStackChanged;
        _assetLint?.Cancel();
        _assetLint?.Dispose();
        _thumbnails.Cancel();
        _thumbnails.Dispose();
        _documentWatcher.Dispose();
        _folderWatcher.Dispose();
    }
}
