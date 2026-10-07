using System.Windows;
using Cairn.Tbl.Index;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;
using Cairn.Tbl.Ui.Documents;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Tbl.Ui;

/// <summary>
/// The table (.tbl) module hosted by the Cairn shell. Partial: the editor half (kind, documents, problems, settings,
/// help, preview provider, index) lives here; the navigation half (outline, reference preview, go to definition,
/// usages, compare) adds its contributions through the <c>partial void Add...</c> hooks below.
/// </summary>
public sealed partial class TblModule : ModuleBase, IAssetPreviewProvider
{
    private readonly List<TblDocument> _documents = [];
    private TblKind? _kind;
    private TblSettings? _settings;
    private CancellationTokenSource? _indexBuild;
    private IReadOnlyList<PanelContribution>? _panels;
    private IReadOnlyList<ShortcutInfo>? _shortcuts;
    private IReadOnlyList<MenuContribution>? _menus;

    public override string Id => "tbl";
    public override string DisplayName => "Tables";

    /// <summary>The schemas every document and the index use.</summary>
    public TblSchemaSet Schemas => TblSchemaSet.Default;

    /// <summary>The one cross-table index of the module (game data, search folders, open tables). Thread-safe.</summary>
    public TblIndex Index { get; } = new TblIndex(TblSchemaSet.Default);

    /// <summary>Completes when the current index build finished (or failed / was superseded).</summary>
    public Task IndexReady { get; private set; } = Task.CompletedTask;

    /// <summary>The module's settings.</summary>
    public TblSettings Settings => _settings ?? throw new InvalidOperationException("The module is not initialised.");

    /// <summary>The open table documents.</summary>
    public IReadOnlyList<TblDocument> OpenDocuments => _documents;

    /// <summary>The table kind.</summary>
    public TblKind Kind => _kind ??= new TblKind(this);

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _settings = new TblSettings(shell.Settings);
        _settings.Changed += (_, _) => { foreach (var d in _documents) d.ScheduleParse(immediate: true); };
        shell.Assets.Changed += (_, _) => { if (!Shell!.Assets.IsLoading) RebuildIndex(); };
        Index.Changed += OnIndexChanged;
        RebuildIndex();
        OnInitialized();
    }

    /// <summary>Called at the end of <see cref="Initialize"/> (navigation half).</summary>
    partial void OnInitialized();
    /// <summary>Lets the navigation half add panels.</summary>
    partial void AddPanels(List<PanelContribution> panels);
    /// <summary>Lets the navigation half add shortcuts.</summary>
    partial void AddShortcuts(List<ShortcutInfo> shortcuts);
    /// <summary>Lets the navigation half add menu items.</summary>
    partial void AddMenus(List<MenuContribution> menus);
    /// <summary>Called once per document view, after the editor is set up (fill <c>RightPaneHost</c> here).</summary>
    partial void OnDocumentViewCreated(TblDocument document, TblDocumentView view);
    /// <summary>Called when a document is closed (disposed).</summary>
    partial void OnDocumentClosed(TblDocument document);

    internal void NotifyViewCreated(TblDocument document, TblDocumentView view) => OnDocumentViewCreated(document, view);

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [Kind];

    public override IReadOnlyList<PanelContribution> Panels => _panels ??= BuildPanels();

    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts ??= BuildShortcuts();

    public override IReadOnlyList<MenuContribution> Menus => _menus ??= BuildMenus();

    private List<PanelContribution> BuildPanels()
    {
        var panels = new List<PanelContribution>();
        AddEditorPanels(panels);
        AddPanels(panels);
        return panels;
    }

    private List<ShortcutInfo> BuildShortcuts()
    {
        var shortcuts = new List<ShortcutInfo>();
        AddEditorShortcuts(shortcuts);
        AddShortcuts(shortcuts);
        return shortcuts;
    }

    private List<MenuContribution> BuildMenus()
    {
        var menus = new List<MenuContribution>();
        AddEditorMenus(menus);
        AddMenus(menus);
        return menus;
    }

    // ── Documents ─────────────────────────────────────────────────────────────────────────────────────────────

    internal TblDocument Track(TblDocument document)
    {
        _documents.Add(document);
        return document;
    }

    internal void Forget(TblDocument document)
    {
        _documents.Remove(document);
        ForgetProblems(document);
        OnDocumentClosed(document);
    }

    internal void NoteActivated(TblDocument document) { _ = document; }

    // ── Index ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Re-reads the tables of the game data and search folders off the UI thread, keeping open documents.</summary>
    public void RebuildIndex()
    {
        if (Shell is null) return;
        _indexBuild?.Cancel();
        var cts = _indexBuild = new CancellationTokenSource();
        var resolver = Shell.Assets.Resolver;
        var open = _documents.Where(d => d.FilePath is not null)
            .GroupBy(d => d.IndexKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Text.Text, StringComparer.OrdinalIgnoreCase);
        var wait = Shell.Assets.ArchivesIndexed;
        IndexReady = Task.Run(async () =>
        {
            using var busy = BusyTracker.Begin("table index");
            try
            {
                await wait.ConfigureAwait(false);
                Index.Refresh(resolver, open, cts.Token);
                _stockMissing.Clear(); // names the stock tables leave unresolved may resolve now (re-linted at idle)
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorLog.Write("table index", ex); }
        });
    }

    private volatile bool _relintQueued;
    [ThreadStatic] private static bool _documentUpdate;

    /// <summary>Puts an open document's latest parse into the index without re-linting every open table.</summary>
    internal void UpdateIndex(TblDocument document, Cairn.Tbl.Model.TblDocument parsed)
    {
        _documentUpdate = true;
        var source = document.IndexSource;
        try { Index.Update(source, parsed); }
        finally { _documentUpdate = false; }
        // Review finding 22: when the names this table defines change, the other open tables are re-linted (a fixed or
        // removed definition changes their "name not defined" results).
        string signature = string.Join("\n", TblIndex.Extract(source, parsed).Defs.Select(d => d.Kind + "|" + d.Name.Trim()).Order(StringComparer.OrdinalIgnoreCase));
        bool changed = _definitionSignatures.TryGetValue(document, out var before) && !string.Equals(before, signature, StringComparison.OrdinalIgnoreCase);
        _definitionSignatures.AddOrUpdate(document, signature);
        if (changed)
            foreach (var other in _documents)
                if (!ReferenceEquals(other, document)) other.ScheduleParse(immediate: false, restart: false);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TblDocument, string> _definitionSignatures = [];

    /// <summary>
    /// Takes a closed or renamed table's key out of the index; a file on disk that the game data or search folders show
    /// (the copy the lookup finds) is re-read from disk instead, so its definitions stay (review findings 15, 16).
    /// </summary>
    internal void ForgetIndexKey(string key)
    {
        Index.Remove(key);
        if (key.StartsWith("unsaved:", StringComparison.Ordinal) || Shell is null) return;
        var resolver = Shell.Assets.Resolver;
        _ = Task.Run(() =>
        {
            try
            {
                if (resolver.Resolve(Path.GetFileName(key)) is { FilePath: { } path } location && string.Equals(Path.GetFullPath(path), Path.GetFullPath(key), StringComparison.OrdinalIgnoreCase))
                    Index.Update(TblSource.ForLocation(location), TblTextFiles.Decode(location.ReadAllBytes()).Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        });
    }

    // Per table name: the references the stock table of that name leaves unresolved (cleared on index rebuilds).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string GameDirectory, IReadOnlySet<string> Names)> _stockMissing =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The references the stock table named <paramref name="fileName"/> leaves unresolved under <paramref name="context"/>
    /// (the game tolerates those, so they are information), or null without a game folder or stock table. Any thread.
    /// </summary>
    internal IReadOnlySet<string>? StockMissing(string fileName, string? gameDirectory, Cairn.Tbl.Linting.TblLintContext context)
    {
        if (string.IsNullOrWhiteSpace(gameDirectory) || (context.FileExists is null && context.Index is null)) return null;
        if (_stockMissing.TryGetValue(fileName, out var cached) && string.Equals(cached.GameDirectory, gameDirectory, StringComparison.OrdinalIgnoreCase))
            return cached.Names;
        IReadOnlySet<string> names = new HashSet<string>();
        try
        {
            if (Cairn.Tbl.Compare.TblCompare.FindStock(gameDirectory, fileName) is { } stock)
            {
                // The stock table as the game data sees it (not through a document's folder or packfile).
                var stockContext = Shell?.Assets.Resolver is { } game && context.FileExists is not null
                    ? context with { FileExists = Cairn.Tbl.Linting.TblLintContext.ForResolver(game).FileExists }
                    : context;
                names = Cairn.Tbl.Linting.TblLinter.UnresolvedNames(Cairn.Tbl.Model.TblDocument.Parse(stock.Text, Schemas.Find(fileName)), stockContext);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        _stockMissing[fileName] = (gameDirectory, names);
        return names;
    }

    private void OnIndexChanged(object? sender, EventArgs e)
    {
        // A document's own parse going into the index must not re-lint it (that would never settle). Rebuilds raise
        // many changes in a burst: re-lint the open tables once afterwards, when the UI is idle.
        if (_documentUpdate || _relintQueued || Shell is null) return;
        _relintQueued = true;
        Shell.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            _relintQueued = false;
            foreach (var d in _documents) d.ScheduleParse(immediate: false, restart: false);
        });
    }

    /// <summary>
    /// Diagnostic runs: <c>--tbl-line &lt;n&gt;</c> moves the active table's caret to line n; <c>--tbl-problem &lt;n&gt;</c>
    /// selects the n-th problem (1-based, after the table was linted); <c>--tbl-completion true</c> opens completion.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (Shell?.ActiveDocument is not TblDocument document) return;
        var controller = document.DocumentView?.Controller;
        if (options.TryGetValue("tbl-line", out string? line) && int.TryParse(line, out int n)) controller?.GoToLine(n);
        if (options.TryGetValue("tbl-problem", out string? problem) && int.TryParse(problem, out int p))
        {
            document.WhenModelCurrent.ContinueWith(_ => Shell.Dispatcher.BeginInvoke(() =>
            {
                var all = document.Model.Diagnostics;
                if (all.Length > 0) { var d = all[Math.Clamp(p - 1, 0, all.Length - 1)]; document.NavigateTo(d.Span.Start, 0); }
            }), TaskScheduler.Default);
        }
        if (options.TryGetValue("tbl-completion", out string? completion) && completion == "true") controller?.ShowCompletion();
    }

    public override void OnShutdown()
    {
        _indexBuild?.Cancel();
        base.OnShutdown();
    }

    // ── Preview provider (packfile preview pane) ──────────────────────────────────────────────────────────────

    public bool CanPreview(string fileName) => fileName.EndsWith(".tbl", StringComparison.OrdinalIgnoreCase);

    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => new Editor.TblPreviewView(this, bytes, fileName);

    internal TblDocument Create(string displayName, string? path, string? origin, TblTextFile file, bool readOnlyOrigin) =>
        Track(new TblDocument(this, Kind, displayName, path, origin, file, readOnlyOrigin));

    /// <summary>The files that came with the table at <paramref name="path"/> (a packfile work copy's packfile), from any module that knows.</summary>
    internal IAssetSiblings? SiblingsFor(string path) =>
        Shell?.Modules.OfType<IAssetSiblingsProvider>().Select(p => p.SiblingsFor(path)).FirstOrDefault(s => s is not null);
}

/// <summary>The <c>.tbl</c> document kind.</summary>
public sealed class TblKind(TblModule module) : IDocumentKind
{
    /// <summary>The text File &gt; New &gt; Table starts with.</summary>
    public const string NewTableText = "// New table\r\n// Comments start with // and end at the end of the line.\r\n\r\n";

    public string Id => "tbl";
    public string DisplayName => "Table";
    public IReadOnlyList<string> Extensions { get; } = [".tbl"];
    public string FileFilter => "Tables (*.tbl)|*.tbl";
    public bool CanCreateNew => true;
    public string AssociationDescription => "Red Faction table";

    public IDocument? CreateNew()
    {
        int n = 1;
        while (module.OpenDocuments.Any(d => d.FilePath is null && string.Equals(d.DisplayName, Name(n), StringComparison.OrdinalIgnoreCase))) n++;
        var file = new TblTextFile(NewTableText, TblFileEncoding.Ansi, LineEndingKind.CrLf, false);
        return module.Create(Name(n), null, null, file, readOnlyOrigin: false);

        static string Name(int i) => i == 1 ? "Untitled.tbl" : $"Untitled {i}.tbl";
    }

    public IDocument Open(string path)
    {
        var file = TblTextFiles.Read(path);
        return module.Create(Path.GetFileName(path), path, null, file, readOnlyOrigin: false);
    }

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText)
    {
        var file = TblTextFiles.Decode(bytes);
        return module.Create(displayName, null, originText, file, readOnlyOrigin: true);
    }

    /// <summary>A packfile entry: its references are looked up in that packfile first.</summary>
    public IDocument OpenEntry(Cairn.Assets.AssetLocation location, byte[] bytes, string originText)
    {
        var document = (TblDocument)OpenBytes(bytes, location.ResolvedName, originText);
        if (location.ArchivePath is { } archive) document.Siblings = new References.TblArchiveSiblings(archive);
        return document;
    }

    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var recovered = TblDocument.DecodeRecovery(snapshot.Data);
        TblDocument document;
        if (snapshot.OriginalPath is { } path && File.Exists(path))
        {
            document = module.Create(Path.GetFileName(path), path, null, TblTextFiles.Read(path), readOnlyOrigin: false);
        }
        else
        {
            var empty = new TblTextFile(string.Empty, recovered.Encoding, recovered.LineEnding, false);
            document = module.Create(snapshot.DisplayName, null, "Recovered " + snapshot.SavedUtc.ToLocalTime().ToString("g"), empty, readOnlyOrigin: false);
        }
        if (document.Text.Text != recovered.Text) document.Text.Replace(0, document.Text.TextLength, recovered.Text);
        document.Encoding = recovered.Encoding;
        return document;
    }
}
