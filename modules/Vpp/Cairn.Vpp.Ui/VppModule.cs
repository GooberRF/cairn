using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Vpp.Editing;
using Cairn.Vpp.Model;
using Cairn.Vpp.Ui.Dialogs;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Work;
using Cairn.Workspace;

namespace Cairn.Vpp.Ui;

/// <summary>The packfile module hosted by the Cairn shell.</summary>
public sealed partial class VppModule : ModuleBase
{
    private readonly List<MenuContribution> _menus = [];
    private readonly List<ToolbarContribution> _toolbar = [];
    private readonly List<ShortcutInfo> _shortcuts = [];
    private readonly List<string> _deleteLater = [];
    private ModuleSettings? _store;
    private string? _stagingRoot;

    public VppModule()
    {
        Kind = new VppKind(this);
        PegKind = new PegKind(this);
        Rf2Kind = new Rf2GroupKind(this);
        CacheKind = new CachePackKind(this);
        Settings = new VppSettings(() => _store);
        SettingsPages = [new VppSettingsPage(Settings)];
    }

    public override string Id => "vpp";
    public override string DisplayName => "Packfiles";
    /// <summary>The .vpp document kind.</summary>
    public VppKind Kind { get; }
    /// <summary>The module's settings ("vpp." keys).</summary>
    public VppSettings Settings { get; }
    /// <summary>The shell (public for the module's documents and self-tests).</summary>
    public IShellContext Host => Shell;
    /// <summary>The Red Faction II packfile group kind (.toc_group, read only).</summary>
    public Rf2GroupKind Rf2Kind { get; }
    /// <summary>The Alpine Faction cache pack kind (.cache, read only).</summary>
    public CachePackKind CacheKind { get; }
    public override IReadOnlyList<IDocumentKind> DocumentKinds => [Kind, PegKind, Rf2Kind, CacheKind];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _toolbar;
    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts;
    public override IReadOnlyList<ISettingsPage> SettingsPages { get; }
    public override IReadOnlyList<HelpTopic> HelpTopics { get; } = [new HelpTopic(VppHelp.TopicId, "Packfiles: format and limits", VppHelp.Build)];
    /// <summary>Module styles and templates (<c>Themes/VppResources.xaml</c>), merged into the application resources.</summary>
    public override IReadOnlyList<ResourceDictionary> Resources { get; } =
        [new ResourceDictionary { Source = new Uri("pack://application:,,,/Cairn.Vpp.Ui;component/Themes/VppResources.xaml") }];

    private VppDocument? Active => Shell.ActiveDocument as VppDocument;
    private static bool IsPackfile(IDocument? d) => d is VppDocument;
    private static bool IsEditablePackfile(IDocument? d) => d is VppDocument { IsReadOnly: false };

    // Packfile menu items that change the packfile: off for a read-only Red Faction II group, whose tooltip then says why.
    private readonly List<(MenuItem Item, string ToolTip)> _editItems = [];

    private void UpdateEditTips()
    {
        var readOnly = Active is { IsReadOnly: true } active ? active : null;
        foreach (var (item, tip) in _editItems)
        {
            item.ToolTip = readOnly?.ReadOnlyReason ?? tip;
            if (item.Command is null) item.IsEnabled = readOnly is null; // a submenu (Sort by)
        }
    }

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        _store = new ModuleSettings(shell.Settings, Id);
        _menus.Add(new(MenuSlot.TopLevel, 0, BuildMenu(), IsPackfile));
        BuildToolbar();
        BuildShortcuts();
        InitializeConvert(); // VppModule.Convert.cs
        InitializePs2(); // VppModule.Ps2.cs
        InitializeBatchConverters(); // VppModule.Batch.cs
        InitializeSources(); // VppModule.Services.cs
        if (!shell.IsDiagnosticRun) Task.Run(() => VppWorkRoot.RemoveStale(Settings.WorkRoot));
    }

    /// <summary>Called by each new document view; the preview/details panes attach here (VppModule.Preview.cs).</summary>
    internal void OnViewCreated(VppDocument document, VppDocumentView view) => AttachPanes(document, view);

    /// <summary>Fills <see cref="VppDocumentView.PreviewHost"/> and <see cref="VppDocumentView.DetailsHost"/> for a new view.</summary>
    partial void AttachPanes(VppDocument document, VppDocumentView view);

    // ---- menu, toolbar, shortcuts ----------------------------------------------------------------------------

    private RelayCommand Cmd(Action<VppDocument> action, Func<VppDocument, bool>? can = null) =>
        new(() => { if (Active is { } d) action(d); }, () => Active is { IsBusy: false } d && (can?.Invoke(d) ?? true));

    private RelayCommand AsyncCmd(Func<VppDocument, Task> action, Func<VppDocument, bool>? can = null) =>
        Cmd(d => d.Commands.Fire(() => action(d)), can);

    private MenuItem BuildMenu()
    {
        var menu = new MenuItem { Header = "_Packfile", Name = "PackfileMenu" };
        MenuItem Add(string header, string tip, ICommand command, string? gesture = null)
        {
            var item = new MenuItem { Header = header, ToolTip = tip, Command = command, InputGestureText = gesture ?? string.Empty };
            menu.Items.Add(item);
            return item;
        }
        MenuItem Edit(string header, string tip, Action<VppDocument> action, Func<VppDocument, bool>? can = null, string? gesture = null)
        {
            var item = Add(header, tip, Cmd(action, d => !d.IsReadOnly && (can?.Invoke(d) ?? true)), gesture);
            ToolTipService.SetShowOnDisabled(item, true);
            _editItems.Add((item, tip));
            return item;
        }
        var addFiles = Add("Add _files...", "Add files to the packfile", AsyncCmd(d => d.Commands.AddFilesAsync(), d => !d.IsReadOnly));
        var addFolder = Add("Add f_older...", "Add every file of a folder and its sub-folders (flattened to file names)", AsyncCmd(d => d.Commands.AddFolderAsync(), d => !d.IsReadOnly));
        foreach (var item in new[] { addFiles, addFolder }) { ToolTipService.SetShowOnDisabled(item, true); _editItems.Add((item, (string)item.ToolTip)); }
        menu.Items.Add(new Separator());
        Add("_Extract selected...", "Write the selected entries to a folder", AsyncCmd(d => d.Commands.ExtractSelectedToAsync(), d => d.SelectedItems.Count > 0), "Ctrl+E");
        Add("Extract _all...", "Write every entry to a folder", AsyncCmd(d => d.Commands.ExtractAllToAsync(), d => d.Current.Count > 0));
        menu.Items.Add(new Separator());
        Edit("_Remove", "Remove the selected entries (undoable)", d => d.Commands.RemoveSelected(), d => d.SelectedItems.Count > 0, "Del");
        Edit("Re_name", "Rename the selected entry", d => d.Commands.BeginRename(), d => d.SelectedItems.Count == 1, "F2");
        Edit("Rename to _fit...", "Shorten the selected names the game cannot use (longer than 31 characters) to 31 characters, as one undo step",
            d => d.Commands.RenameToFit(), d => d.Commands.CanRenameToFit);
        Edit("Re_place...", "Replace the selected entry's data with a file", d => d.Commands.ReplaceSelected(), d => d.SelectedItems.Count == 1);
        Add("Select all of _type", "Select every entry with the selected entry's extension", Cmd(d => d.Commands.SelectAllOfType(), d => d.SelectedItems.Count > 0));
        const string sortTip = "Reorder the entries inside the packfile (undoable; the list's column headers only sort the view)";
        var sort = new MenuItem { Header = "_Sort by", ToolTip = sortTip };
        foreach (var (key, label) in new[] { (VppSortKey.Name, "_Name"), (VppSortKey.Type, "_Type"), (VppSortKey.Size, "_Size"), (VppSortKey.OriginalOrder, "_Original order") })
            sort.Items.Add(new MenuItem { Header = label, Command = Cmd(d => d.Commands.SortPackfile(key), d => !d.IsReadOnly && d.Current.Count > 1) });
        _editItems.Add((sort, sortTip));
        menu.Items.Add(sort);
        menu.SubmenuOpened += (_, e) => { if (ReferenceEquals(e.OriginalSource, menu)) UpdateEditTips(); };
        menu.Items.Add(new Separator());
        Add("_Validate", "Check the packfile against the game's limits now (also whether added files changed on disk)", Cmd(d => d.Commands.Validate()));
        return menu;
    }

    private void BuildToolbar()
    {
        Button Tool(string glyph, string tip, ICommand command)
        {
            var b = new Button { Content = glyph, ToolTip = tip, Command = command, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets") };
            b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
            AutomationProperties.SetName(b, tip);
            return b;
        }
        _toolbar.Add(new(100, Tool("", "Add files to the packfile", AsyncCmd(d => d.Commands.AddFilesAsync(), d => !d.IsReadOnly)), IsEditablePackfile));
        _toolbar.Add(new(101, Tool("", "Extract the selected entries (Ctrl+E)", AsyncCmd(d => d.Commands.ExtractSelectedToAsync(), d => d.SelectedItems.Count > 0)), IsPackfile));

        // Autoplay: a selected sound starts playing at once instead of waiting for Play
        var autoPlay = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "", // Volume
            ToolTip = "Autoplay sounds: play a sound as soon as it is selected",
            IsChecked = Settings.AutoPlaySounds,
            FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
        };
        autoPlay.SetResourceReference(FrameworkElement.StyleProperty, "ToolToggle");
        AutomationProperties.SetName(autoPlay, "Autoplay sounds");
        autoPlay.Click += (_, _) =>
        {
            Settings.AutoPlaySounds = autoPlay.IsChecked == true;
            // turning it on plays the sound already selected
            if (Settings.AutoPlaySounds && Active?.View is VppDocumentView { PreviewHost.Content: Preview.VppPreviewPane { View: Cairn.Previews.AudioPreview { IsPlaying: false } audio } }) audio.Play();
        };
        AutoPlayToggle = autoPlay;
        Preview.VppPreviewPane.AutoPlaySounds = () => Settings.AutoPlaySounds;
        _toolbar.Add(new(102, autoPlay, IsPackfile));
    }

    /// <summary>The toolbar's autoplay toggle (self-tests).</summary>
    internal System.Windows.Controls.Primitives.ToggleButton? AutoPlayToggle { get; private set; }

    private void BuildShortcuts()
    {
        void Key(Key key, ModifierKeys mods, string description, RelayCommand command) => _shortcuts.Add(new("Packfile", description, key, mods, command, IsPackfile));
        Key(System.Windows.Input.Key.Delete, ModifierKeys.None, "Remove the selected entries", Cmd(d => d.Commands.RemoveSelected(), d => !d.IsReadOnly && d.SelectedItems.Count > 0));
        Key(System.Windows.Input.Key.F2, ModifierKeys.None, "Rename the selected entry", Cmd(d => d.Commands.BeginRename(), d => !d.IsReadOnly && d.SelectedItems.Count == 1));
        Key(System.Windows.Input.Key.A, ModifierKeys.Control, "Select every entry shown", Cmd(d => (d.View as VppDocumentView)?.FileList.SelectAllShown()));
        Key(System.Windows.Input.Key.C, ModifierKeys.Control, "Copy the selected entries as files (paste in Explorer)", AsyncCmd(d => d.Commands.CopyFilesAsync(), d => d.SelectedItems.Count > 0));
        Key(System.Windows.Input.Key.Enter, ModifierKeys.None, "Open the selected entries in their Windows programs", AsyncCmd(d => d.Commands.OpenSelectedAsync(), d => d.SelectedItems.Count > 0));
        Key(System.Windows.Input.Key.E, ModifierKeys.Control, "Extract the selected entries to a folder", AsyncCmd(d => d.Commands.ExtractSelectedToAsync(), d => d.SelectedItems.Count > 0));
    }

    // ---- shell services for documents ------------------------------------------------------------------------

    /// <summary>True when a loaded module opens <paramref name="fileName"/>'s type (a kind or an importer).</summary>
    public bool CairnOpens(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (ext.Length == 0) return false;
        bool Has(IReadOnlyList<string> list) => list.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
        var modules = Shell.Modules.Count > 0 ? Shell.Modules : [this];
        return modules.Any(m => m.DocumentKinds.Any(k => Has(k.Extensions)) || m.Importers.Any(i => Has(i.Extensions)));
    }

    /// <summary>A new empty folder for files handed to Explorer (clipboard, drag-out); deleted when Cairn exits.</summary>
    public string NewStagingFolder()
    {
        _stagingRoot ??= VppWorkRoot.CreateFolder(Settings.WorkRoot, $"out-{Environment.ProcessId}");
        var folder = Path.Combine(_stagingRoot, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>A work folder that could not be deleted yet (a program still holds a file): retried at exit.</summary>
    internal void DeleteLater(string folder)
    {
        if (!_deleteLater.Contains(folder, StringComparer.OrdinalIgnoreCase)) _deleteLater.Add(folder);
    }

    public override void OnShutdown()
    {
        // Never an error at the user: whatever is still locked stays for the next start's clean-up.
        // Only folders Cairn created itself (marked; see VppWorkRoot).
        foreach (var folder in _deleteLater.ToList()) if (VppWorkRoot.TryDeleteOwnFolder(folder)) _deleteLater.Remove(folder);
        if (_stagingRoot is not null) VppWorkRoot.TryDeleteOwnFolder(_stagingRoot);
    }

    /// <summary>
    /// False after the diagnostic option <c>--vpp-info off</c>: packfiles opened from then on have no Info column and
    /// read nothing for it (to time the list with and without it in the same run conditions).
    /// </summary>
    internal bool InfoColumnEnabled { get; private set; } = true;

    /// <summary>Diagnostic options: <c>--vpp-filter text</c>, <c>--vpp-select name[,name]</c>, <c>--vpp-convert-pegs all|name[,name]</c>, <c>--vpp-mpeg2-black threshold[,soft]</c>, <c>--vpp-sort name|type|size|state|info</c>, <c>--vpp-columns name=W[,type=W,...]</c>, <c>--vpp-problems long-names</c>, <c>--vpp-library [filter]</c>, <c>--vpp-library-collapse heading[,heading]</c>, <c>--vpp-info off</c>.</summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("vpp-info", out var infoOption) && infoOption.Equals("off", StringComparison.OrdinalIgnoreCase)) InfoColumnEnabled = false;
        // --vpp-library [filter]: bring the Packfiles tab forward (start page or beside a packfile), optionally filtered
        if (options.TryGetValue("vpp-library", out var library) && Shell.ShowPanel(LibraryPanelId) && library != "true") Library.Filter = library;
        // --vpp-library-collapse heading[,heading]: collapse those folder headings of the Packfiles tab
        if (options.TryGetValue("vpp-library-collapse", out var collapse))
            Library.Collapse(collapse.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // --vpp-mpeg2-black threshold[,soft]: "Treat black as transparent" on for this run (a diagnostic run's settings
        // are never saved), before --vpp-select shows a preview
        if (options.TryGetValue("vpp-mpeg2-black", out var black))
        {
            var parts = black.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Settings.Mpeg2BlackTransparent = true;
            if (parts.Length > 0 && int.TryParse(parts[0], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var threshold)) Settings.Mpeg2BlackThreshold = threshold;
            Settings.Mpeg2SoftEdge = parts.Contains("soft", StringComparer.OrdinalIgnoreCase);
        }
        if (Active is not { } doc) return;
        if (options.TryGetValue("vpp-filter", out var filter) && doc.View is VppDocumentView view) view.FilterBox.Text = filter;
        if (options.TryGetValue("vpp-sort", out var sort) && Enum.TryParse<List.VppListSort>(sort, true, out var s)) doc.List.SortBy(s, false);
        // --vpp-columns name=W[,type=W,...]: entry list column widths for captures (not remembered; Info takes the rest)
        if (options.TryGetValue("vpp-columns", out var columns) && doc.View is VppDocumentView columnsView)
        {
            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (part.Split('=', 2) is [var key, var value] && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var width))
                    widths[key.Trim()] = width;
            columnsView.FileList.SetColumnWidths(widths);
        }
        // --vpp-convert-pegs all|name[,name]: "Convert to .tga..." on those .peg entries of the active packfile (every
        // texture, black as the settings say, no dialog), selecting the --vpp-select names after it
        if (options.TryGetValue("vpp-convert-pegs", out var pegList))
        {
            string? after = options.TryGetValue("vpp-select", out var afterNames) ? afterNames : null;
            var names = pegList is "true" or "all" ? null : pegList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            doc.Commands.Fire(async () =>
            {
                await ConvertPegTexturesAsync(doc, AllTextures(doc, names), showSkipped: false);
                if (after is not null) doc.SelectNames(after.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            });
            return;
        }
        if (options.TryGetValue("vpp-select", out var select)) doc.SelectNames(select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (options.TryGetValue("vpp-problems", out var problems) && problems.Equals("long-names", StringComparison.OrdinalIgnoreCase))
        {
            // the File types panel's "Names longer than 31 characters" box (shown only when some name is that long)
            if (doc.List.LongNameOption.HasMatches) doc.List.LongNameOption.IsChecked = true;
            Shell.ShowPanel(TypesPanel.Id);
        }
    }
}

/// <summary>
/// The Red Faction II packfile group kind: a <c>.toc_group</c> opens as a read-only packfile of the files it lists (from
/// its <c>.packfile</c>, or loose below the game folder). Nothing is ever written to it.
/// </summary>
public sealed class Rf2GroupKind(VppModule module) : IDocumentKind
{
    public string Id => "vpp.rf2";
    public string DisplayName => "RF2 packfile group";
    public IReadOnlyList<string> Extensions { get; } = [Cairn.Formats.Rf2.Rf2TocGroup.Extension];
    public string FileFilter => "Red Faction II packfile groups (*.toc_group)|*.toc_group";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Red Faction II packfile group";

    public IDocument? CreateNew() => null;

    /// <summary>Reads the group (and checks it against its packfile); throws <see cref="Cairn.Formats.AssetFormatException"/> with the reason when it cannot.</summary>
    public IDocument Open(string path)
    {
        string full = Path.GetFullPath(path);
        return new VppDocument(module, Cairn.Vpp.Rf2.Rf2Packfiles.Open(full), Path.GetFileName(full), full, kind: this);
    }

    /// <summary>Refused: a group's files are beside it on disk, so it is opened from the game folder.</summary>
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) =>
        throw new InvalidDataException($"{displayName} ({originText}) lists files stored beside it: its .packfile or loose files below the game folder. Open it from the Red Faction II folder instead.");

    /// <summary>Never needed (a read-only group is never dirty); handled as a packfile's recovery data.</summary>
    public IDocument Restore(RecoverySnapshot snapshot) => module.Kind.Restore(snapshot);
}

/// <summary>
/// The Alpine Faction cache pack kind: a <c>.cache</c> (such as <c>alpine_cache\map_thumbs.cache</c>) opens as a
/// read-only packfile of its entries. Nothing is ever written to it.
/// </summary>
public sealed class CachePackKind(VppModule module) : IDocumentKind
{
    public string Id => "vpp.cache";
    public string DisplayName => "Alpine cache pack";
    public IReadOnlyList<string> Extensions { get; } = [Cairn.Formats.Alpine.CachePack.Extension];
    public string FileFilter => "Alpine Faction cache packs (*.cache)|*.cache";
    public bool CanCreateNew => false;
    public string AssociationDescription => "Alpine Faction cache pack";

    public IDocument? CreateNew() => null;

    /// <summary>Reads the pack's index; throws <see cref="Cairn.Formats.AssetFormatException"/> with the reason when it cannot (another program's .cache included).</summary>
    public IDocument Open(string path)
    {
        string full = Path.GetFullPath(path);
        return new VppDocument(module, Cairn.Vpp.Alpine.CachePacks.Open(full), Path.GetFileName(full), full, kind: this);
    }

    /// <summary>Refused: the entries are read from the pack on disk as they are needed.</summary>
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) =>
        throw new InvalidDataException($"{displayName} ({originText}) is an Alpine Faction cache pack stored inside another file. Extract it first, then open the extracted copy.");

    /// <summary>Never needed (a read-only pack is never dirty); handled as a packfile's recovery data.</summary>
    public IDocument Restore(RecoverySnapshot snapshot) => module.Kind.Restore(snapshot);
}

/// <summary>The .vpp document kind.</summary>
public sealed class VppKind(VppModule module) : IDocumentKind
{
    public string Id => "vpp";
    public string DisplayName => "Packfile";
    public IReadOnlyList<string> Extensions { get; } = [".vpp"];
    public string FileFilter => "Packfiles (*.vpp)|*.vpp";
    public bool CanCreateNew => true;
    public string AssociationDescription => "Red Faction packfile";

    public IDocument? CreateNew() => new VppDocument(module, VppPackage.Empty, "Untitled.vpp", null);

    public IDocument Open(string path) => new VppDocument(module, VppPackage.Open(path), Path.GetFileName(path), path);

    /// <summary>Refused: a packfile inside a packfile is opened from an extracted copy, which Cairn can read on demand.</summary>
    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) =>
        throw new InvalidDataException($"{displayName} is a packfile stored inside another packfile ({originText}). Extract it first (or use Open in Cairn in the packfile's list), then open the extracted copy.");

    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var manifest = VppRecoveryManifest.FromJson(snapshot.Data);
        var result = manifest.Restore();
        if (result.IsComplete)
        {
            var whole = new VppDocument(module, result.Package, snapshot.DisplayName, result.Package.Path ?? snapshot.OriginalPath);
            whole.MarkAsNew();
            return whole;
        }

        // Never silently drop or mix data: say exactly what is affected; the rest becomes a NEW packfile (Save As).
        var lines = result.Problems.Take(12).Select(p => "• " + p).ToList();
        if (result.Problems.Count > 12) lines.Add($"• ... and {result.Problems.Count - 12:N0} more");
        string why = result.OriginalChanged ? $"{Path.GetFileName(manifest.OriginalPath)} changed on disk (or is gone) since the recovery data was written. " : string.Empty;
        string message = why + $"{result.Problems.Count:N0} entr{(result.Problems.Count == 1 ? "y" : "ies")} cannot be restored:\n" + string.Join("\n", lines)
            + $"\n\nRestore the other {result.Package.Count:N0} as a new, unsaved packfile (you choose where to save it), or discard the recovered changes?";
        int pick = module.Host.Dialogs.Choose($"{snapshot.DisplayName} can only be partly recovered", message, ["_Restore the rest as a new packfile", "_Discard"], 1);
        // Discard: the shell deletes the snapshot quietly (OperationCanceledException), so it is not offered again.
        if (pick != 0) throw new OperationCanceledException($"The recovered changes to {snapshot.DisplayName} were discarded; the packfile on disk was not changed.");
        string name = Path.GetFileNameWithoutExtension(snapshot.DisplayName) + " (recovered).vpp";
        var doc = new VppDocument(module, result.Package, name, null);
        doc.MarkAsNew();
        doc.Notice = $"Partly recovered: {result.Problems.Count:N0} entr{(result.Problems.Count == 1 ? "y is" : "ies are")} missing ({string.Join(", ", result.Problems.Take(5).Select(p => p.EntryName))}{(result.Problems.Count > 5 ? ", ..." : "")}). This is a new packfile: save it under a name of your choice.";
        return doc;
    }
}
