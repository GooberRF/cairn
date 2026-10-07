using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Ui.Documents;
using Cairn.Ui.Services;
using Cairn.Workspace;

namespace Cairn.Ui.Modules;

/// <summary>A feature module hosted by the Cairn shell (ATX, RFA, VFX). Registered statically by the shell.</summary>
public interface IModule
{
    /// <summary>Short lower-case id ("atx", "rfa", "vfx"); also the prefix of the module's settings keys.</summary>
    string Id { get; }
    /// <summary>Name shown in menus and settings.</summary>
    string DisplayName { get; }
    /// <summary>Called once, on the UI thread, before the main window is shown.</summary>
    void Initialize(IShellContext shell);
    /// <summary>The document kinds this module opens and creates.</summary>
    IReadOnlyList<IDocumentKind> DocumentKinds { get; }
    /// <summary>Importers for non-native files (glTF, ...).</summary>
    IReadOnlyList<IFileImporter> Importers { get; }
    /// <summary>Menu items placed in the shell's menus.</summary>
    IReadOnlyList<MenuContribution> Menus { get; }
    /// <summary>Elements placed on the shell's toolbar.</summary>
    IReadOnlyList<ToolbarContribution> ToolbarItems { get; }
    /// <summary>Keyboard shortcuts (also listed in the help's shortcut table).</summary>
    IReadOnlyList<ShortcutInfo> Shortcuts { get; }
    /// <summary>Left and bottom pane tabs.</summary>
    IReadOnlyList<PanelContribution> Panels { get; }
    /// <summary>Pages shown after the shell's General page in the settings dialog.</summary>
    IReadOnlyList<ISettingsPage> SettingsPages { get; }
    /// <summary>Help topics listed in the Help menu.</summary>
    IReadOnlyList<HelpTopic> HelpTopics { get; }
    /// <summary>Resource dictionaries (data templates, styles) merged after the theme dictionaries.</summary>
    IReadOnlyList<ResourceDictionary> Resources { get; }
    /// <summary>Receives the unknown <c>--name value</c> switches of a diagnostic run.</summary>
    void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options);
    /// <summary>Called once when the app exits, after the documents are closed.</summary>
    void OnShutdown();
}

/// <summary>An <see cref="IModule"/> with empty defaults for everything but <see cref="Id"/> and <see cref="DisplayName"/>.</summary>
public abstract class ModuleBase : IModule
{
    /// <inheritdoc/>
    public abstract string Id { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <summary>The shell, once <see cref="Initialize"/> has run.</summary>
    protected IShellContext Shell { get; private set; } = null!;
    /// <summary>Stores <paramref name="shell"/> in <see cref="Shell"/>; overrides call the base first.</summary>
    public virtual void Initialize(IShellContext shell) => Shell = shell ?? throw new ArgumentNullException(nameof(shell));
    /// <inheritdoc/>
    public virtual IReadOnlyList<IDocumentKind> DocumentKinds => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<IFileImporter> Importers => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<MenuContribution> Menus => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<ToolbarContribution> ToolbarItems => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<ShortcutInfo> Shortcuts => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<PanelContribution> Panels => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<ISettingsPage> SettingsPages => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<HelpTopic> HelpTopics => [];
    /// <inheritdoc/>
    public virtual IReadOnlyList<ResourceDictionary> Resources => [];
    /// <inheritdoc/>
    public virtual void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options) { }
    /// <inheritdoc/>
    public virtual void OnShutdown() { }
}

/// <summary>A kind of document a module opens: its extensions, filter, and factories.</summary>
public interface IDocumentKind
{
    /// <summary>"atx", "rfa.clip", "rfa.mesh", "vfx": also the recovery kind and the ProgID suffix.</summary>
    string Id { get; }
    /// <summary>Friendly name ("Animated texture").</summary>
    string DisplayName { get; }
    /// <summary>Lower case with the dot; the first is the default save extension.</summary>
    IReadOnlyList<string> Extensions { get; }
    /// <summary>Win32 filter ("Animated textures (*.atx)|*.atx").</summary>
    string FileFilter { get; }
    /// <summary>True when File &gt; New lists this kind.</summary>
    bool CanCreateNew { get; }
    /// <summary>File &gt; New: may show a dialog; null when cancelled.</summary>
    IDocument? CreateNew();
    /// <summary>Opens a file. Throws <c>AssetFormatException</c> or <see cref="IOException"/>; the shell reports.</summary>
    IDocument Open(string path);
    /// <summary>Opens an archive entry: read-only origin described by <paramref name="originText"/>, so Save becomes Save As.</summary>
    IDocument OpenBytes(byte[] bytes, string displayName, string originText);
    /// <summary>
    /// Opens the archive entry at <paramref name="location"/> (what <see cref="IShellContext.OpenLocation"/> calls for an
    /// entry of a packfile). A kind whose documents refer to other files can keep the location to look in that packfile
    /// first; the default is <see cref="OpenBytes"/>.
    /// </summary>
    IDocument OpenEntry(AssetLocation location, byte[] bytes, string originText) => OpenBytes(bytes, location.ResolvedName, originText);
    /// <summary>
    /// Rebuilds a document from a recovery snapshot of this kind. Throw <see cref="OperationCanceledException"/> when the
    /// user chose to discard it in the kind's own prompt: the shell then deletes the snapshot quietly.
    /// </summary>
    IDocument Restore(RecoverySnapshot snapshot);
    /// <summary>Friendly type name written with the file association.</summary>
    string AssociationDescription { get; }
}

/// <summary>Imports a non-native file (.gltf/.glb, .vbm) by opening documents through <see cref="IShellContext.AddDocument"/>.</summary>
public interface IFileImporter
{
    /// <summary>Menu text ("glTF as effect").</summary>
    string DisplayName { get; }
    /// <summary>Lower case with the dot.</summary>
    IReadOnlyList<string> Extensions { get; }
    /// <summary>Win32 filter.</summary>
    string FileFilter { get; }
    /// <summary>0 = cannot import; higher = more confident. The shell picks the highest and asks on a tie.</summary>
    int Probe(string path);
    /// <summary>Imports <paramref name="path"/>; may show dialogs.</summary>
    void Import(string path);
}

/// <summary>Where a <see cref="MenuContribution"/> goes. <see cref="TopLevel"/> menus sit between Edit and View.</summary>
public enum MenuSlot { TopLevel, FileNew, FileImport, FileExport, Edit, View, Tools, Help }

/// <summary>A menu item or separator built by a module with its own DataContext.</summary>
/// <param name="Slot">Which menu it joins.</param>
/// <param name="Order">Sort key within the slot.</param>
/// <param name="Item">A <see cref="MenuItem"/> or <see cref="Separator"/>.</param>
/// <param name="IsVisibleFor">Null = always visible; otherwise re-evaluated when the active document changes.</param>
public sealed record MenuContribution(MenuSlot Slot, int Order, Control Item, Func<IDocument?, bool>? IsVisibleFor = null);

/// <summary>A toolbar element; visibility as for <see cref="MenuContribution"/>.</summary>
public sealed record ToolbarContribution(int Order, FrameworkElement Element, Func<IDocument?, bool>? IsVisibleFor = null);

/// <summary>
/// A keyboard shortcut. The shell installs its own plus those whose <paramref name="AppliesTo"/> accepts the
/// active document, re-installing on activation, so two modules may share a gesture. Shortcuts without
/// modifiers (and clipboard/undo gestures) are skipped while a text input has focus unless <paramref name="AllowInTextInput"/>.
/// </summary>
/// <param name="Category">Group in the shortcut table.</param>
/// <param name="Description">What it does.</param>
/// <param name="Key">The key.</param>
/// <param name="Modifiers">The modifiers.</param>
/// <param name="Command">The command executed.</param>
/// <param name="AppliesTo">Null = always installed.</param>
/// <param name="AllowInTextInput">True to fire even while a text box has focus.</param>
public sealed record ShortcutInfo(string Category, string Description, Key Key, ModifierKeys Modifiers, ICommand Command,
    Func<IDocument?, bool>? AppliesTo = null, bool AllowInTextInput = false);

/// <summary>Which pane a <see cref="PanelContribution"/> lives in.</summary>
public enum PanelSide { Left, Bottom }

/// <summary>A pane tab.</summary>
/// <param name="Id">Stable id (persisted selection).</param>
/// <param name="Title">Tab header.</param>
/// <param name="Side">Left or bottom pane.</param>
/// <param name="Order">Sort key within the pane.</param>
/// <param name="Content">For the active document: a view model (templated through the module's resources) or a
/// <see cref="FrameworkElement"/>; null hides the tab. Left panels usually ignore the document.</param>
/// <param name="Exclusive">
/// When true and this panel has content for the active document, its pane shows only the exclusive panels that have
/// content (e.g. a packfile's type filter replaces the asset libraries while a packfile is active). Return null from
/// <paramref name="Content"/> for documents the panel does not belong to, so other documents keep the usual panels.
/// </param>
public sealed record PanelContribution(string Id, string Title, PanelSide Side, int Order, Func<IDocument?, object?> Content, bool Exclusive = false);

/// <summary>A page of the settings dialog.</summary>
public interface ISettingsPage
{
    /// <summary>Page title (also the id <see cref="IShellContext.ShowSettings"/> takes).</summary>
    string Title { get; }
    /// <summary>The page's view.</summary>
    FrameworkElement View { get; }
    /// <summary>Reads the settings into the page each time the dialog opens.</summary>
    void Load();
    /// <summary>Writes the page back when the dialog is accepted.</summary>
    void Commit();
}

/// <summary>A help topic; <paramref name="Build"/> creates its document when shown.</summary>
public sealed record HelpTopic(string Id, string Title, Func<FlowDocument> Build);

/// <summary>What the shell offers modules and documents.</summary>
public interface IShellContext
{
    /// <summary>The UI thread's dispatcher.</summary>
    Dispatcher Dispatcher { get; }
    /// <summary>The main window.</summary>
    Window MainWindow { get; }
    /// <summary>Generic pickers and prompts (owner = the main window).</summary>
    IDialogService Dialogs { get; }
    /// <summary>App settings; modules use <see cref="AppSettings.Values"/> keys prefixed "&lt;moduleId&gt;." (see <see cref="ModuleSettings"/>).</summary>
    AppSettings Settings { get; }
    /// <summary>The theme service.</summary>
    ThemeService Theme { get; }
    /// <summary>Shared game-data resolver (game directory + search folders).</summary>
    AssetHost Assets { get; }
    /// <summary>True during <c>--screenshot</c>/<c>--selftest</c> runs: no prompts, no settings writes.</summary>
    bool IsDiagnosticRun { get; }
    /// <summary>Open documents in tab order.</summary>
    IReadOnlyList<IDocument> Documents { get; }
    /// <summary>The selected document, or null.</summary>
    IDocument? ActiveDocument { get; }
    /// <summary>Raised when <see cref="ActiveDocument"/> changes.</summary>
    event EventHandler? ActiveDocumentChanged;
    /// <summary>Raised when documents are added or removed.</summary>
    event EventHandler? DocumentsChanged;
    /// <summary>Adds a tab for <paramref name="document"/>.</summary>
    void AddDocument(IDocument document, bool activate = true);
    /// <summary>Selects <paramref name="document"/>'s tab.</summary>
    void Activate(IDocument document);
    /// <summary>Opens by extension through the kinds, else the importers. False when nothing opened.</summary>
    bool OpenFile(string path);
    /// <summary>Loose file: <see cref="OpenFile"/>; archive entry: the kind's <see cref="IDocumentKind.OpenBytes"/>.</summary>
    bool OpenLocation(AssetLocation location);
    /// <summary>Save, or Save As when the document has no path or is read-only. False when cancelled or failed.</summary>
    bool Save(IDocument document);
    /// <summary>Save As. False when cancelled or failed.</summary>
    bool SaveAs(IDocument document);
    /// <summary>Closes the tab, prompting when dirty. False when cancelled.</summary>
    bool Close(IDocument document);
    /// <summary>Shows a transient message in the status bar.</summary>
    void ShowStatus(string message);
    /// <summary>Re-queries every command's CanExecute and re-evaluates contribution visibility.</summary>
    void RefreshCommands();
    /// <summary>Opens the help window at a topic id.</summary>
    void ShowHelp(string topicId);
    /// <summary>Opens the settings dialog, optionally at a page title.</summary>
    void ShowSettings(string? pageTitle = null);
    /// <summary>
    /// The loaded modules in load order, so a module can find optional services another module offers
    /// (for example an <see cref="IAssetPreviewProvider"/>). Empty for hosts that do not list them.
    /// </summary>
    IReadOnlyList<IModule> Modules => [];
    /// <summary>
    /// Brings the pane tab <paramref name="panelId"/> (a <see cref="PanelContribution.Id"/>) forward and shows its pane
    /// when it was hidden. False when no such tab is showing for the active document. Hosts without panes do nothing.
    /// </summary>
    bool ShowPanel(string panelId) => false;
}

/// <summary>
/// Optional: implemented by a module class to describe any file the way its own details pane does (type, size,
/// dimensions, frames, references...). Callers find it through <see cref="IShellContext.Modules"/>.
/// </summary>
public interface IAssetFactsProvider
{
    /// <summary>
    /// Rows about the file <paramref name="name"/> (contents read through <paramref name="open"/>, within limits), each
    /// with a section ("Image", "Mesh"...), a label, a value and whether it is worth flagging. Any thread; never throws
    /// for unreadable or malformed contents (it says so in a row instead).
    /// </summary>
    IReadOnlyList<(string Section, string Label, string Value, bool Flagged)> Describe(string name, Func<Stream> open, long size);
}

/// <summary>
/// Optional: implemented by a module class that knows which other files came with a loose file on disk, for example
/// the packfile module for the work copies it extracts (a work copy's siblings are the rest of its packfile). Callers
/// find it through <see cref="IShellContext.Modules"/>.
/// </summary>
public interface IAssetSiblingsProvider
{
    /// <summary>The files that came with the file at <paramref name="path"/>, or null when this module does not know it. UI thread.</summary>
    IAssetSiblings? SiblingsFor(string path);
}

/// <summary>
/// Optional: implemented by a module class that opens archive entries in tabs from temporary work copies on disk (the
/// packfile module's "Open in Cairn"). The shell asks it so the Recent list never records a temporary path: a work copy
/// is recorded as its archive entry instead and reopened through <see cref="OpenArchiveEntry"/>.
/// </summary>
public interface IWorkCopyProvider
{
    /// <summary>True when <paramref name="path"/> lies in this module's temporary work area (whether or not it still exists). Cheap; no I/O beyond path checks.</summary>
    bool IsWorkCopy(string path);
    /// <summary>The archive and entry the work copy at <paramref name="path"/> was extracted from, or null when unknown (for example a packfile never saved). UI thread.</summary>
    (string ArchivePath, string EntryName)? ArchiveEntryOf(string path);
    /// <summary>True when this module reopens entries of the archive at <paramref name="archivePath"/> (decided by its name).</summary>
    bool CanOpenArchive(string archivePath);
    /// <summary>
    /// Opens <paramref name="entryName"/> of the archive at <paramref name="archivePath"/> in a tab the way "Open in Cairn"
    /// does (the archive opens too, so saving the tab can update it). False, after telling the user, when the archive or
    /// the entry is gone or cannot be read. UI thread; the entry's tab may appear a moment after this returns.
    /// </summary>
    bool OpenArchiveEntry(string archivePath, string entryName);
}

/// <summary>
/// Optional: implemented by a module class (the <see cref="IModule"/> itself) to give other modules a
/// read-only preview of an asset, for example the packfile module's preview pane. Callers find
/// providers through <see cref="IShellContext.Modules"/>.
/// </summary>
public interface IAssetPreviewProvider
{
    /// <summary>True when <see cref="CreatePreview(byte[], string)"/> can show <paramref name="fileName"/> (decided by its name, usually the extension). Cheap; no I/O.</summary>
    bool CanPreview(string fileName);

    /// <summary>
    /// Creates a read-only view of the asset in <paramref name="bytes"/>, or null when it cannot be shown.
    /// The view must not touch shell documents, undo history or settings. It may implement
    /// <see cref="IDisposable"/>; the caller disposes it when the preview is replaced or closed. Previews are
    /// created and discarded in rapid succession while the user arrows through a list, so creation must be
    /// quick (defer heavy work), and disposing a view before it finished loading must be safe.
    /// </summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">The asset's name (no path), used for its type and for resolving siblings.</param>
    FrameworkElement? CreatePreview(byte[] bytes, string fileName);

    /// <summary>
    /// <see cref="CreatePreview(byte[], string)"/> for an asset that came with other files, for example an entry of a
    /// packfile: every name the asset refers to (textures, animated texture frames, a mesh to play a clip on) is
    /// looked up in <paramref name="siblings"/> first, through the same supersede rules as the game data, and only
    /// then through the shell's asset host. Providers that do not override this ignore the siblings.
    /// </summary>
    /// <param name="bytes">The whole file.</param>
    /// <param name="fileName">The asset's name (no path).</param>
    /// <param name="siblings">The files beside the asset, or null for none.</param>
    FrameworkElement? CreatePreview(byte[] bytes, string fileName, IAssetSiblings? siblings) => CreatePreview(bytes, fileName);
}

/// <summary>
/// Read-only access to the files that came with a previewed asset, by bare name (any folder part is ignored and
/// case does not matter, as in the engine's flat file system). A preview calls it from background threads, possibly
/// after the caller moved on to another asset, so an implementation must be thread-safe and must not throw for a
/// name it no longer has: <see cref="Contains"/> is false and <see cref="Read"/> null instead.
/// </summary>
public interface IAssetSiblings
{
    /// <summary>Where the siblings come from, for messages ("maps.vpp").</summary>
    string Label { get; }
    /// <summary>The sibling names (for choosing among them, for example a mesh to play a clip on).</summary>
    IReadOnlyList<string> Names { get; }
    /// <summary>True when a sibling has the bare name <paramref name="name"/>. Cheap; no I/O.</summary>
    bool Contains(string name);
    /// <summary>The whole file named <paramref name="name"/>, or null when there is none or it cannot be read.</summary>
    byte[]? Read(string name);
}

/// <summary>Helpers for <see cref="IAssetSiblings"/>.</summary>
public static class AssetSiblings
{
    /// <summary>
    /// A resolver that looks in <paramref name="siblings"/> first (each name through the whole texture supersede
    /// chain, <see cref="AssetResolver.ProbeCandidates"/>) and then as <paramref name="resolver"/> does. Returns
    /// <paramref name="resolver"/> itself when there are no siblings, and a siblings-only resolver when there is
    /// no <paramref name="resolver"/>.
    /// </summary>
    public static AssetResolver? Layer(this IAssetSiblings? siblings, AssetResolver? resolver)
    {
        if (siblings is null) return resolver;
        var under = resolver ?? new AssetResolver(new AssetResolverOptions());
        return under.WithOverlay(siblings.Label, siblings.Contains,
            name => siblings.Read(name) is { } bytes ? new MemoryStream(bytes, writable: false) : null);
    }

    /// <summary>The name a lookup of <paramref name="requestedName"/> finds among <paramref name="siblings"/> (supersede rules applied), or null.</summary>
    public static string? Find(this IAssetSiblings siblings, string requestedName)
    {
        ArgumentNullException.ThrowIfNull(siblings);
        if (string.IsNullOrWhiteSpace(requestedName)) return null;
        string bare = Path.GetFileName(requestedName.Trim());
        return AssetResolver.ProbeCandidates(bare).FirstOrDefault(siblings.Contains);
    }

    /// <summary>Siblings held in memory (tests, generated sets). Thread-safe; counts reads per name.</summary>
    /// <param name="label">Where the files come from.</param>
    /// <param name="files">Name to bytes; names compare case-insensitively.</param>
    public static MemoryAssetSiblings FromMemory(string label, IEnumerable<KeyValuePair<string, byte[]>> files) => new(label, files);
}

/// <summary>An <see cref="IAssetSiblings"/> over files held in memory; <see cref="ReadCount"/> tells which were used.</summary>
public sealed class MemoryAssetSiblings : IAssetSiblings
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _reads = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="label">Where the files come from.</param>
    /// <param name="files">Name to bytes; a later duplicate name replaces an earlier one.</param>
    public MemoryAssetSiblings(string label, IEnumerable<KeyValuePair<string, byte[]>> files)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        foreach (var (name, bytes) in files) _files[Path.GetFileName(name)] = bytes;
        Names = [.. _files.Keys];
    }

    /// <inheritdoc/>
    public string Label { get; }
    /// <inheritdoc/>
    public IReadOnlyList<string> Names { get; }
    /// <inheritdoc/>
    public bool Contains(string name) => !string.IsNullOrEmpty(name) && _files.ContainsKey(Path.GetFileName(name));

    /// <inheritdoc/>
    public byte[]? Read(string name)
    {
        if (string.IsNullOrEmpty(name) || !_files.TryGetValue(Path.GetFileName(name), out var bytes)) return null;
        _reads.AddOrUpdate(Path.GetFileName(name), 1, (_, n) => n + 1);
        return bytes;
    }

    /// <summary>How often <paramref name="name"/> was read.</summary>
    public int ReadCount(string name) => _reads.TryGetValue(name, out int n) ? n : 0;
    /// <summary>Total reads of every name.</summary>
    public int TotalReads => _reads.Values.Sum();
}

/// <summary>Typed access to one module's <see cref="AppSettings.Values"/> keys ("&lt;moduleId&gt;.&lt;key&gt;").</summary>
public sealed class ModuleSettings(AppSettings settings, string moduleId)
{
    private readonly AppSettings _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    private readonly string _prefix = moduleId + ".";

    /// <summary>The full key for <paramref name="key"/>.</summary>
    public string KeyFor(string key) => _prefix + key;
    /// <summary>Reads a value, or <paramref name="fallback"/> when absent or unreadable.</summary>
    public T? Get<T>(string key, T? fallback = default) => _settings.Get(_prefix + key, fallback);
    /// <summary>Writes a value (null removes it, as <see cref="AppSettings.Set{T}"/> does).</summary>
    public void Set<T>(string key, T? value) => _settings.Set(_prefix + key, value);
}
