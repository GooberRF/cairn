using System.Collections.Immutable;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Tbl.Index;
using Cairn.Tbl.Linting;
using Cairn.Tbl.Model;
using Cairn.Tbl.Text;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Workspace;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ParsedTable = Cairn.Tbl.Model.TblDocument;

namespace Cairn.Tbl.Ui.Documents;

/// <summary>
/// A table (.tbl) document. The AvalonEdit <see cref="TextDocument"/> is its only state and its undo stack the only
/// history; everything else (<see cref="Model"/>: parse, token classes, diagnostics) is derived from the text after a
/// short pause, off the UI thread for big tables. The file's encoding, BOM and line endings are kept on save.
/// </summary>
public sealed class TblDocument : DocumentBase
{
    /// <summary>Tables at least this long are parsed and linted on a pool thread.</summary>
    internal const int BackgroundThreshold = 64 * 1024;

    private readonly TblModule _module;
    private bool _parsePending;
    private int _scheduleToken;
    private TblFileEncoding _encoding, _savedEncoding;
    private byte[] _savedBytes;
    private bool _isReadOnlyOrigin;
    private int _caretOffset;
    private int _parseGeneration;
    private TaskCompletionSource _modelCurrent = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    internal TblDocument(TblModule module, IDocumentKind kind, string displayName, string? filePath, string? originText,
        TblTextFile file, bool readOnlyOrigin)
        : base(module.ShellContext!, kind, displayName, filePath, originText)
    {
        _module = module;
        _encoding = _savedEncoding = file.Encoding;
        LineEnding = file.LineEnding;
        HasMixedLineEndings = file.HasMixedLineEndings;
        _isReadOnlyOrigin = readOnlyOrigin;
        _savedBytes = TblTextFiles.Encode(file.Text, file.Encoding);

        Text = new TextDocument(file.Text);
        Text.UndoStack.ClearAll();
        Text.UndoStack.MarkAsOriginalFile();
        Text.UndoStack.PropertyChanged += (_, _) => NotifyDirtyChanged();
        Text.TextChanged += OnTextChanged;
        // A packfile work copy resolves its references in its packfile first.
        _siblings = filePath is null ? null : module.SiblingsFor(filePath);

        // A first model at once (parse only, cheap even for the biggest stock table) so views never see null;
        // the lint follows straight away.
        Model = BuildModel(0, file.Text, lint: false, null);
        Reparse();

        ToggleProblemsCommand = new RelayCommand(() => _module.ShowProblems(this));
    }

    // ── The seam other parts of the module (outline, preview, compare) build on ─────────────────────────────────

    /// <summary>The text: the document's only state (one undo history).</summary>
    public TextDocument Text { get; }

    /// <summary>The latest parse of <see cref="Text"/> (may lag behind the text by the debounce).</summary>
    public TblModel Model { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Model"/> was replaced.</summary>
    public event EventHandler? ModelChanged;

    /// <summary>The caret offset in the editor (0 until the view exists).</summary>
    public int CaretOffset
    {
        get => _caretOffset;
        internal set
        {
            if (_caretOffset == value) return;
            _caretOffset = value;
            Raise(nameof(CaretOffset));
            Raise(nameof(StatusItems));
            CaretChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when <see cref="CaretOffset"/> changed.</summary>
    public event EventHandler? CaretChanged;

    /// <summary>The editor once the view exists, else null.</summary>
    public TextEditor? Editor { get; internal set; }

    /// <summary>The module's shared cross-table index (game data, search folders, open tables).</summary>
    public TblIndex Index => _module.Index;

    /// <summary>The module that owns the document.</summary>
    internal TblModule Module => _module;

    private IAssetSiblings? _siblings;

    /// <summary>
    /// The files that came with the table (the packfile it was opened from, or a work copy's packfile): its references
    /// are looked up there first, in the lint and the reference preview. Null for a loose table.
    /// </summary>
    public IAssetSiblings? Siblings
    {
        get => _siblings;
        internal set
        {
            if (ReferenceEquals(_siblings, value)) return;
            _siblings = value;
            ScheduleParse(immediate: true);
        }
    }

    /// <summary>Raised when a file-name or reference token is clicked (ctrl = Ctrl was held).</summary>
    public event Action<TblToken, bool>? TokenActivated;

    /// <summary>Selects <paramref name="length"/> characters at <paramref name="offset"/>, scrolls them into view and focuses the editor.</summary>
    public void NavigateTo(int offset, int length)
    {
        NavigateRequested?.Invoke(this, new TextSpan(Math.Clamp(offset, 0, Text.TextLength),
            Math.Max(0, Math.Min(length, Text.TextLength - Math.Clamp(offset, 0, Text.TextLength)))));
    }

    internal event EventHandler<TextSpan>? NavigateRequested;

    internal void RaiseTokenActivated(TblToken token, bool ctrl) => TokenActivated?.Invoke(token, ctrl);

    // ── File facts ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The encoding the file is written in (changed by the byte-order-mark quick fix).</summary>
    public TblFileEncoding Encoding
    {
        get => _encoding;
        set
        {
            if (_encoding == value) return;
            _encoding = value;
            Raise(nameof(Encoding));
            NotifyDirtyChanged();
            Raise(nameof(StatusItems));
            ScheduleParse(immediate: true);
        }
    }

    /// <summary>The line-ending style detected on open (the text keeps whatever it has).</summary>
    public LineEndingKind LineEnding { get; private set; }

    /// <summary>True when the file mixed line-ending styles.</summary>
    public bool HasMixedLineEndings { get; private set; }

    /// <summary>A stable key for the index (the path, or the document id for unsaved ones).</summary>
    internal string IndexKey => FilePath ?? "unsaved:" + Id;

    /// <summary>True once the document was closed (disposed).</summary>
    internal bool IsDisposed => _disposed;

    /// <summary>
    /// How the index lists this table: a saved one by its path; an unsaved one (new, or opened from a packfile) under
    /// its tab name, located as "unsaved" (plus the packfile it came from), keyed by <see cref="IndexKey"/>.
    /// </summary>
    internal TblSource IndexSource => FilePath is { } path
        ? TblSource.ForPath(path)
        : new TblSource(IndexKey, DisplayName, ArchiveOriginText is { Length: > 0 } origin ? $"unsaved, from {origin}" : "unsaved", TblSourceKind.OpenDocument);

    public override bool IsReadOnly => _isReadOnlyOrigin && FilePath is null;
    public override bool IsDirty => !Text.UndoStack.IsOriginalFile || _encoding != _savedEncoding;
    public override bool CanUndo => Text.UndoStack.CanUndo;
    public override bool CanRedo => Text.UndoStack.CanRedo;
    public override string? UndoLabel => CanUndo ? "Undo typing" : null;
    public override string? RedoLabel => CanRedo ? "Redo typing" : null;
    public override void Undo() { if (Text.UndoStack.CanUndo) Text.UndoStack.Undo(); }
    public override void Redo() { if (Text.UndoStack.CanRedo) Text.UndoStack.Redo(); }

    /// <summary>Completes once <see cref="Model"/> reflects the current text, linted.</summary>
    public Task WhenModelCurrent => _modelCurrent.Task;

    // ── Parse and lint ────────────────────────────────────────────────────────────────────────────────────────

    private void OnTextChanged(object? sender, EventArgs e)
    {
        ScheduleParse(immediate: false);
        Raise(nameof(StatusItems));
    }

    /// <summary>Re-parses after the debounce (or now), e.g. when settings or the index changed.</summary>
    internal void ScheduleParse(bool immediate) => ScheduleParse(immediate, restart: true);

    /// <summary>The last exception a parse or lint threw (diagnostics), or null.</summary>
    internal string? LastParseError { get; private set; }

    /// <summary>Number of parse requests (diagnostics).</summary>
    internal int ParseRequests { get; private set; }

    /// <param name="immediate">Parse at the next idle moment instead of after the typing pause.</param>
    /// <param name="restart">False for requests that are not typing (index or settings changes): they never push back a parse already waiting.</param>
    internal void ScheduleParse(bool immediate, bool restart)
    {
        if (_disposed) return;
        ParseRequests++;
        if (_modelCurrent.Task.IsCompleted) _modelCurrent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!restart && _parsePending) return;
        _parsePending = true;
        int token = ++_scheduleToken;
        int delay = immediate ? 1 : Text.TextLength >= BackgroundThreshold ? 450 : 200;
        // A pool-thread delay posting back (not a DispatcherTimer): it also runs inside nested message loops.
        Task.Delay(delay).ContinueWith(_ => Shell.Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (token != _scheduleToken || _disposed) return;
            _parsePending = false;
            Reparse();
        }), TaskScheduler.Default);
    }

    private void Reparse()
    {
        if (_disposed) return;
        int generation = ++_parseGeneration;
        string text = Text.Text;
        var context = LintContext();
        var settings = _module.Settings;
        (bool e, bool w, bool i) severities = (settings.LintErrors, settings.LintWarnings, settings.LintInformation);
        if (text.Length < BackgroundThreshold)
        {
            try { Apply(generation, BuildModel(generation, text, lint: true, context, severities)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The core promises not to throw on any text; if it does, keep the last model and say why.
                LastParseError = ex.ToString();
                ErrorLog.Write("table parse", ex);
                _modelCurrent.TrySetResult();
            }
            return;
        }
        var busy = BusyTracker.Begin("table lint");
        Task.Run(() => BuildModel(generation, text, lint: true, context, severities)).ContinueWith(t =>
        {
            busy.Dispose();
            if (t.IsCompletedSuccessfully) Shell.Dispatcher.BeginInvoke(() => Apply(generation, t.Result));
            else if (t.Exception is { } ex)
            {
                ErrorLog.Write("table parse", ex);
                Shell.Dispatcher.BeginInvoke(() => { LastParseError = ex.ToString(); _modelCurrent.TrySetResult(); });
            }
        }, TaskScheduler.Default);
    }

    private TblLintContext LintContext()
    {
        AssetResolver resolver = _siblings.Layer(Resolver) ?? Resolver;
        var baseContext = TblLintContext.ForResolver(resolver, _module.Index);
        _lintGameDirectory = Shell.Settings.GameDirectory;
        return new TblLintContext
        {
            FileExists = _module.Index.Sources.Count == 0 && !Shell.Assets.HasSources && _siblings is null ? null : baseContext.FileExists,
            Index = _module.Index.Sources.Count == 0 ? null : _module.Index,
            Encoding = _encoding,
            NewLine = LineEndings.ToText(LineEnding),
        };
    }

    // The game folder when the lint context was made (read on the UI thread, used by the lint on any thread).
    private volatile string? _lintGameDirectory;

    private TblModel BuildModel(int version, string text, bool lint, TblLintContext? context, (bool E, bool W, bool I) severities = default)
    {
        var schema = _module.Schemas.Find(DisplayName);
        var parsed = ParsedTable.Parse(text, schema);
        // A reference the stock table of this name also leaves unresolved is tolerated by the game: information.
        if (lint && context is not null && _module.StockMissing(DisplayName, _lintGameDirectory, context with { Encoding = null }) is { } stockMissing)
            context = context with { StockMissing = stockMissing };
        var classes = TblClassifier.Classify(parsed);
        var diagnostics = lint ? TblSettings.Filter(TblLinter.Lint(parsed, schema, context), severities.E, severities.W, severities.I) : [];
        return new TblModel(version, text, parsed, schema, classes, diagnostics);
    }

    private void Apply(int generation, TblModel model)
    {
        if (_disposed || generation != _parseGeneration) return;
        Model = model;
        if (model.Text.Length == Text.TextLength && !_parsePending && model.Text == Text.Text)
        {
            _modelCurrent.TrySetResult();
        }
        _module.UpdateIndex(this, model.Parsed);
        Raise(nameof(Model));
        Raise(nameof(StatusItems));
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects the next (or previous) problem after (before) the caret, wrapping around (F8 / Shift+F8).</summary>
    public void GoToProblem(bool forward)
    {
        var all = Model.Diagnostics;
        if (all.IsEmpty) { ShowStatus("No problems."); return; }
        var next = forward
            ? all.FirstOrDefault(d => d.Span.Start > _caretOffset) ?? all[0]
            : all.LastOrDefault(d => d.Span.Start < _caretOffset - 1) ?? all[^1];
        NavigateTo(next.Span.Start, next.Span.Length);
        ShowStatus($"{next.Code}: {next.Message}");
    }

    /// <summary>Applies a quick fix's edits as one undo step.</summary>
    public void ApplyEdits(IEnumerable<TextEdit> edits)
    {
        var ordered = edits.OrderByDescending(e => e.Span.Start).ToList();
        if (ordered.Count == 0) return;
        Text.BeginUpdate();
        try
        {
            foreach (var edit in ordered)
            {
                int start = Math.Clamp(edit.Span.Start, 0, Text.TextLength);
                int length = Math.Clamp(edit.Span.Length, 0, Text.TextLength - start);
                Text.Replace(start, length, edit.NewText);
            }
        }
        finally { Text.EndUpdate(); }
    }

    /// <summary>Applies a quick fix: its text edits, or the encoding change the byte-order-mark fix stands for.</summary>
    public void ApplyQuickFix(TblDiagnostic diagnostic, TblQuickFix fix)
    {
        if (fix.Edits.IsEmpty && diagnostic.Code == TblRules.ByteOrderMark.Code)
        {
            // A mark over 8-bit bytes keeps them 8-bit: only the mark goes.
            Encoding = _encoding == TblFileEncoding.Latin1Bom ? TblFileEncoding.Latin1 : TblFileEncoding.Utf8;
            ShowStatus("The table will be saved without a byte-order mark.");
            return;
        }
        // The fix's spans are offsets into the text its diagnostic was computed for. When the buffer has changed
        // since (typing inside the parse delay, or a Problems list from an older model), lint the current text now
        // and apply the same fix from that result, so no edit lands at a stale offset.
        bool current = Model.Diagnostics.Any(d => ReferenceEquals(d, diagnostic))
            && Model.Text.Length == Text.TextLength && Model.Text == Text.Text;
        if (!current)
        {
            var settings = _module.Settings;
            var fresh = BuildModel(_parseGeneration, Text.Text, lint: true, LintContext(), (settings.LintErrors, settings.LintWarnings, settings.LintInformation));
            var match = fresh.Diagnostics.Where(d => d.Code == diagnostic.Code)
                .SelectMany(d => FixesFor(d).Where(f => f.Title == fix.Title).Select(f => (Diagnostic: d, Fix: f)))
                .OrderBy(p => p.Diagnostic.Message == diagnostic.Message ? 0 : 1)
                .ThenBy(p => Math.Abs(p.Diagnostic.Span.Start - diagnostic.Span.Start))
                .FirstOrDefault();
            ScheduleParse(immediate: true);
            if (match.Fix is null)
            {
                ShowStatus("The text has changed and this fix no longer applies.");
                return;
            }
            fix = match.Fix;
        }
        ApplyEdits(fix.Edits);
    }

    /// <summary>The quick fixes for <paramref name="diagnostic"/>, including the encoding fix the core leaves to the UI.</summary>
    public static IReadOnlyList<TblQuickFix> FixesFor(TblDiagnostic diagnostic)
    {
        if (diagnostic.Code == TblRules.ByteOrderMark.Code && diagnostic.QuickFixes.IsEmpty)
        {
            return [new TblQuickFix(SaveWithoutBomTitle, [])];
        }
        return diagnostic.QuickFixes;
    }

    /// <summary>Title of the byte-order-mark quick fix.</summary>
    public const string SaveWithoutBomTitle = "Save without byte-order mark";

    // ── Saving, reloading, recovery ───────────────────────────────────────────────────────────────────────────

    public override bool ConfirmSave()
    {
        var bad = TblTextFiles.Unencodable(Text.Text, _encoding);
        if (bad.Count > 0)
        {
            string chars = new([.. bad.Distinct().Take(8)]);
            if (Shell.IsDiagnosticRun) return false;
            if (!Shell.Dialogs.Confirm("Characters the encoding cannot hold",
                    $"{DisplayName} is {TblTextFiles.Describe(_encoding)}, which cannot hold: {chars}\n\n" +
                    "Save it as UTF-8 instead? The game reads UTF-8 text as single bytes, so those characters may look wrong in game.",
                    "Save as UTF-8"))
            {
                return false;
            }
            Encoding = TblFileEncoding.Utf8;
        }
        int errors = Model.ErrorCount;
        return errors == 0 || Shell.IsDiagnosticRun || Shell.Dialogs.ConfirmSaveWithErrors(DisplayName, errors);
    }

    public override void SaveTo(string path)
    {
        string text = Text.Text;
        byte[] bytes = TblTextFiles.Encode(text, _encoding);
        SuspendFileWatch(true);
        try { AtomicFile.WriteAllBytes(path, bytes); }
        finally { SuspendFileWatch(false); }
        string oldKey = IndexKey;
        _savedBytes = bytes;
        _savedEncoding = _encoding;
        FilePath = path;
        _isReadOnlyOrigin = false;
        ArchiveOriginText = null;
        // Review finding 16: whenever the path changes the old key goes (an old file on disk comes back with the rebuild).
        if (!string.Equals(oldKey, IndexKey, StringComparison.OrdinalIgnoreCase)) _module.ForgetIndexKey(oldKey);
        Text.UndoStack.MarkAsOriginalFile();
        HasExternalChange = false;
        NotifyDirtyChanged();
        Raise(nameof(IsReadOnly));
        Raise(nameof(TabToolTip));
        ScheduleParse(immediate: true);
    }

    protected override bool MatchesSaved(byte[] bytes) => bytes.AsSpan().SequenceEqual(_savedBytes);

    protected override void LoadBytes(byte[] bytes, bool keepDirty)
    {
        var file = TblTextFiles.Decode(bytes);
        Text.Replace(0, Text.TextLength, file.Text);
        _encoding = _savedEncoding = file.Encoding;
        LineEnding = file.LineEnding;
        HasMixedLineEndings = file.HasMixedLineEndings;
        _savedBytes = bytes;
        if (!keepDirty) Text.UndoStack.MarkAsOriginalFile();
        NotifyDirtyChanged();
        Raise(nameof(Encoding));
        Raise(nameof(StatusItems));
    }

    /// <summary>Marker that starts a recovery snapshot: the encoding name follows, then a LF, then the UTF-8 text.</summary>
    internal const string RecoveryMarker = "\u0001cairn-tbl ";

    public override byte[]? CaptureRecovery()
    {
        string header = RecoveryMarker + _encoding + "\n";
        return System.Text.Encoding.UTF8.GetBytes(header + Text.Text);
    }

    /// <summary>Splits a recovery snapshot into its text and encoding (plain UTF-8 text is accepted too).</summary>
    internal static TblTextFile DecodeRecovery(byte[] data)
    {
        string all = new UTF8Encoding(false, false).GetString(data);
        var encoding = TblFileEncoding.Ansi;
        if (all.StartsWith(RecoveryMarker, StringComparison.Ordinal))
        {
            int lf = all.IndexOf('\n', StringComparison.Ordinal);
            if (lf > 0 && Enum.TryParse(all[RecoveryMarker.Length..lf], out TblFileEncoding parsed)) encoding = parsed;
            all = lf >= 0 ? all[(lf + 1)..] : string.Empty;
        }
        return new TblTextFile(all, encoding, LineEndings.Detect(all), LineEndings.IsMixed(all));
    }

    // ── Status bar ────────────────────────────────────────────────────────────────────────────────────────────

    internal ICommand ToggleProblemsCommand { get; }

    public override IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            var model = Model;
            string kind = model.Schema?.Title is { Length: > 0 } title ? title : model.Schema is not null ? model.Schema.File ?? "Table" : "Unknown table";
            var items = new List<StatusItem>
            {
                new(kind, model.Schema?.Doc ?? "No schema describes this table, so only its syntax is checked."),
                new(Plural(model.EntryCount, "entry", "entries")),
                new(Plural(model.ErrorCount, "error", "errors"), "Show the problems (Ctrl+Shift+M)", ToggleProblemsCommand),
                new(Plural(model.WarningCount, "warning", "warnings"), "Show the problems (Ctrl+Shift+M)", ToggleProblemsCommand),
            };
            var line = Text.GetLocation(Math.Clamp(_caretOffset, 0, Text.TextLength));
            items.Add(new StatusItem($"Ln {line.Line}, Col {line.Column}"));
            items.Add(new StatusItem(TblTextFiles.Describe(_encoding),
                _encoding != _savedEncoding ? "Will be saved as " + TblTextFiles.Describe(_encoding) : "The file's encoding (kept on save)."));
            items.Add(new StatusItem(LineEnding switch { LineEndingKind.Lf => "LF", LineEndingKind.Cr => "CR", _ => "CRLF" } + (HasMixedLineEndings ? " (mixed)" : ""),
                "Line endings (kept on save)."));
            return items;
        }
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    // ── View and lifetime ─────────────────────────────────────────────────────────────────────────────────────

    private TblDocumentView? _documentView;

    /// <summary>The view once created (null before first use and after disposal).</summary>
    public TblDocumentView? DocumentView => _documentView;

    protected override FrameworkElement CreateView() => _documentView = new TblDocumentView(this);

    public override void OnActivated() => _module.NoteActivated(this);

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scheduleToken++;
        _module.ForgetIndexKey(IndexKey);
        _module.Forget(this);
        if (Editor is not null) Editor = null;
        ModelChanged = null;
        CaretChanged = null;
        TokenActivated = null;
        NavigateRequested = null;
        _documentView?.Dispose();
        _documentView = null;
        base.Dispose();
    }
}
