using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Ui.Views;
using ModuleShortcut = Cairn.Ui.Modules.ShortcutInfo;

namespace Cairn.Rfa.Ui;

/// <summary>The animations and meshes module hosted by the Cairn shell.</summary>
public sealed class RfaModule : ModuleBase, IAssetPreviewProvider, IArchiveBatchConverter
{
    /// <inheritdoc/>
    public bool CanPreview(string fileName) =>
        fileName.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) || Formats.Legacy.LegacyMeshSupport.IsLegacyName(fileName);

    /// <inheritdoc/>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName) => CreatePreview(bytes, fileName, null);

    /// <inheritdoc/>
    public FrameworkElement? CreatePreview(byte[] bytes, string fileName, IAssetSiblings? siblings) =>
        CanPreview(fileName) ? new Preview.RfaPreview(bytes, fileName, Shell?.Assets, Workspace?.Assets, siblings) : null;

    /// <summary>Title of the RFA settings page (also the id for <see cref="IShellContext.ShowSettings"/>).</summary>
    public const string SettingsPageTitle = "Animations and meshes";
    /// <summary>Help topic id of the RFA/V3C format reference.</summary>
    public const string FormatHelpTopicId = "rfa.formats";
    /// <summary>Help topic id of the exporter and PS2 meshes page (.v3d, .vcm, .rfm, .rfc).</summary>
    public const string LegacyHelpTopicId = "rfa.legacy-meshes";

    /// <summary>Clips (.rfa).</summary>
    public static RfaDocumentKind ClipKind { get; } = new("rfa.clip", "Animation clip", [".rfa"], "Animation clips (*.rfa)|*.rfa", "Red Faction animation");

    /// <summary>Meshes (.v3c; .v3m opens read-only).</summary>
    public static RfaDocumentKind MeshKind { get; } = new("rfa.mesh", "Mesh", [".v3c", ".v3m"], "Meshes (*.v3c;*.v3m)|*.v3c;*.v3m", "Red Faction mesh");

    /// <summary>The meshes Cairn reads and converts but never saves: exporter (.v3d, .vcm) and PS2 (.rfm, .rfc).</summary>
    public static RfaDocumentKind LegacyMeshKind { get; } = new("rfa.legacymesh", "Exporter or PS2 mesh", [".v3d", ".vcm", ".rfm", ".rfc"],
        "Exporter and PS2 meshes (*.v3d;*.vcm;*.rfm;*.rfc)|*.v3d;*.vcm;*.rfm;*.rfc", "Red Faction exporter or PS2 mesh");

    /// <summary>
    /// What an open legacy static mesh tab reports to the shell: Save As writes the converted .v3m (the legacy formats
    /// are never written). Not listed in <see cref="DocumentKinds"/>.
    /// </summary>
    internal static RfaDocumentKind LegacyStaticSaveKind { get; } = new("rfa.legacymesh", "Exporter or PS2 mesh", [".v3m"], "Static meshes (*.v3m)|*.v3m", "Red Faction exporter or PS2 mesh");

    /// <summary>The character counterpart of <see cref="LegacyStaticSaveKind"/> (.v3c).</summary>
    internal static RfaDocumentKind LegacyCharacterSaveKind { get; } = new("rfa.legacymesh", "Exporter or PS2 mesh", [".v3c"], "Character meshes (*.v3c)|*.v3c", "Red Faction exporter or PS2 mesh");

    /// <summary>The module's workspace (the old app's main view model, RFA half).</summary>
    public static RfaWorkspace Workspace { get; private set; } = null!;

    private readonly List<MenuContribution> _menus = [];
    private readonly List<ModuleShortcut> _shortcuts = [];
    private readonly List<PanelContribution> _panels = [];
    private readonly List<ToolbarContribution> _toolbar = [];
    private readonly List<ResourceDictionary> _resources = [];
    private LibraryView? _library;

    /// <summary>The shell merges <see cref="Resources"/> before <see cref="Initialize"/>: the templates are made here.</summary>
    public RfaModule()
    {
        var templates = new ResourceDictionary();
        AddTemplate<ProblemsViewModel, ProblemsView>(templates);
        AddTemplate<TimelineViewModel, TimelineView>(templates);
        AddTemplate<TableUsageViewModel, TableUsageView>(templates);
        _resources.Add(templates);
    }

    /// <inheritdoc/>
    public override string Id => "rfa";

    /// <inheritdoc/>
    public override string DisplayName => "Animations and meshes";

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [ClipKind, MeshKind, LegacyMeshKind];
    public override IReadOnlyList<IFileImporter> Importers { get; } = [new GltfImporter()];
    public override IReadOnlyList<MenuContribution> Menus => _menus;
    public override IReadOnlyList<ModuleShortcut> Shortcuts => _shortcuts;
    public override IReadOnlyList<PanelContribution> Panels => _panels;
    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _toolbar;
    public override IReadOnlyList<ISettingsPage> SettingsPages => _settingsPages;
    private readonly List<ISettingsPage> _settingsPages = [];
    public override IReadOnlyList<ResourceDictionary> Resources => _resources;

    private static bool IsRfa(IDocument? d) => d is DocumentViewModel;
    private static bool IsClip(IDocument? d) => d is ClipDocumentViewModel;
    private static bool IsMesh(IDocument? d) => d is MeshDocumentViewModel;
    private static bool IsLegacyMesh(IDocument? d) => d is MeshDocumentViewModel { IsLegacy: true };

    /// <inheritdoc/>
    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        // The one place the legacy mesh readers (.v3d, .vcm, .rfm, .rfc) are registered.
        Formats.Legacy.LegacyMeshSupport.EnsureRegistered();
        RfaUi.Shell = shell;
        RfaUi.Theme = shell.Theme;
        var ws = new RfaWorkspace(shell.Dispatcher, shell.Dialogs, shell.Settings, shell.Theme, shell.IsDiagnosticRun);
        ws.AttachShell(shell);
        Workspace = ws;
        ws.ApplyAssetSettings();

        var menus = new RfaMenus();
        // 1.0.1 always showed both menus (items disabled for the other kind); here while an RFA document is in front.
        _menus.Add(new MenuContribution(MenuSlot.TopLevel, 100, WithContext((MenuItem)menus["ClipMenu"], ws), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.TopLevel, 110, WithContext((MenuItem)menus["MeshMenu"], ws), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 100, WithContext((MenuItem)menus["InspectorItem"], ws), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 110, new Separator(), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 120, WithContext((MenuItem)menus["PlayPauseItem"], ws), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 130, WithContext((MenuItem)menus["FrameItem"], ws), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 140, new Separator(), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.View, 150, WithContext((MenuItem)menus["TimeDisplayItem"], ws), IsRfa));
        var toolbar = (StackPanel)menus["Toolbar"];
        toolbar.DataContext = ws;
        _toolbar.Add(new ToolbarContribution(100, toolbar, IsRfa));
        _settingsPages.Add(new RfaSettingsPage(ws));
        _menus.Add(new MenuContribution(MenuSlot.FileImport, 100, Item("Animation from glTF…", "Import a glTF animation as a new clip", ws.Gltf.ImportAnimationCommand)));
        _menus.Add(new MenuContribution(MenuSlot.FileImport, 110, Item("Mesh from glTF…", "Import a glTF mesh as a new mesh", ws.Gltf.ImportMeshCommand)));
        _menus.Add(new MenuContribution(MenuSlot.FileExport, 100, Item("glTF…", "Export the clip or mesh in front as glTF", ws.Gltf.ExportCommand), IsRfa));
        _menus.Add(new MenuContribution(MenuSlot.FileExport, 105, Item("Convert to .v3m/.v3c…", "Convert the exporter or PS2 mesh in front to the PC format, with a report of what was approximated",
            new RelayCommand(() => { if (shell.ActiveDocument is MeshDocumentViewModel { IsLegacy: true } m) ws.Legacy.ConvertDocument(m); },
                () => shell.ActiveDocument is MeshDocumentViewModel { Legacy.Mesh: not null })), IsLegacyMesh));
        _menus.Add(new MenuContribution(MenuSlot.Tools, 100, Item("_Batch Retarget…", "Retarget many clips onto another skeleton", ws.Retarget.BatchCommand)));
        _menus.Add(new MenuContribution(MenuSlot.Tools, 110, Item("_Refresh Library", "Look through the game directory and search folders again", ws.RefreshLibraryCommand)));
        // 1.0.1's Edit menu after Undo/Redo: bone selection and the timeline's key commands.
        int editOrder = 100;
        foreach (string key in new[] { "SelectAllBonesItem", "ClearBonesItem", "", "CopyKeysItem", "CutKeysItem", "PasteKeysItem",
                     "PasteMirroredItem", "DeleteKeysItem", "SelectAllKeysItem", "KeyBonesItem" })
        {
            Control item = key.Length == 0 ? new Separator() : WithContext((MenuItem)menus[key], ws);
            _menus.Add(new MenuContribution(MenuSlot.Edit, editOrder += 10, item, IsRfa));
        }

        AddShortcuts(ws);

        _panels.Add(new PanelContribution("rfa.library", "Animations", PanelSide.Left, 100,
            _ => _library ??= new LibraryView { DataContext = ws.Library }));
        _panels.Add(new PanelContribution("rfa.timeline", "Timeline", PanelSide.Bottom, 100, d => (d as ClipDocumentViewModel)?.Timeline));
        _panels.Add(new PanelContribution("rfa.problems", "Problems", PanelSide.Bottom, 110, d => (d as DocumentViewModel)?.Problems));
        _panels.Add(new PanelContribution("rfa.tables", "Table usage", PanelSide.Bottom, 120, d => d is DocumentViewModel r ? TableUsageViewModel.For(r) : null));
    }

    // ── "Convert meshes..." for packfile entries (IArchiveBatchConverter) ───────────────────────────────────────

    /// <inheritdoc/>
    public string CommandText => "Convert _meshes...";

    /// <inheritdoc/>
    public string CommandToolTip => "Convert the selected exporter and PS2 meshes (.v3d, .vcm, .rfm, .rfc) to .v3m/.v3c: into the packfile as new entries (one undo step) or into a folder";

    /// <inheritdoc/>
    public bool CanConvert(string entryName) => Formats.Legacy.LegacyMeshSupport.IsLegacyName(entryName);

    /// <inheritdoc/>
    public Task<string?> ConvertAsync(ArchiveBatchRequest request) => Workspace.Legacy.ConvertBatchAsync(request);

    public override IReadOnlyList<HelpTopic> HelpTopics { get; } =
        [new HelpTopic(FormatHelpTopicId, "RFA & V3C format reference", HelpDocuments.FormatReference),
         new HelpTopic(LegacyHelpTopicId, "Exporter and PS2 meshes", HelpDocuments.LegacyMeshes)];

    private IReadOnlyDictionary<string, string>? _pendingOptions;

    /// <summary>
    /// 1.0.1's screenshot options: <c>--show</c> (viewport toggles, never saved) at once; <c>--time</c>, <c>--tab</c>,
    /// <c>--preview</c>, <c>--bone</c> (or <c>--select-bone</c>) and <c>--camera yaw,pitch[,zoom]</c> once the first RFA
    /// document is active and loading has settled, in the order 1.0.1's runner applied them.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("show", out var show)) ApplyShow(Workspace.Display, show);
        _pendingOptions = options;
        if (Workspace.ActiveDocument is not null) _ = ApplyDeferredAsync();
        else Shell.ActiveDocumentChanged += OnFirstActive;
    }

    private void OnFirstActive(object? sender, EventArgs e)
    {
        if (Workspace.ActiveDocument is null) return;
        Shell.ActiveDocumentChanged -= OnFirstActive;
        _ = ApplyDeferredAsync();
    }

    // The options hold one BusyTracker token of their own (so the runner's capture waits for them): wait for the rest.
    private static async Task SettleAsync()
    {
        for (int i = 0; i < 200 && BusyTracker.Count > 1; i++) await Task.Delay(50);
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private async Task ApplyDeferredAsync()
    {
        if (_pendingOptions is not { } options) return;
        _pendingOptions = null;
        using var busy = BusyTracker.Begin("rfa diagnostic options");
        var ws = Workspace;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        await SettleAsync();
        float? time = options.TryGetValue("time", out var t) && float.TryParse(t, System.Globalization.NumberStyles.Float, inv, out var tv) ? tv : null;
        if (time is { } seek) foreach (var d in ws.Documents) d.Playback.Seek(seek);
        if (options.TryGetValue("tab", out var tab))
        {
            if (ws.Documents.FirstOrDefault(d => string.Equals(d.DisplayName, tab, StringComparison.OrdinalIgnoreCase)) is { } named) ws.ActiveDocument = named;
            else if (string.Equals(tab, "Meshes", StringComparison.OrdinalIgnoreCase)) ws.Library.SelectedTab = 0;
            else if (string.Equals(tab, "Clips", StringComparison.OrdinalIgnoreCase) || string.Equals(tab, "Animations", StringComparison.OrdinalIgnoreCase)) ws.Library.SelectedTab = 1;
        }
        if (options.TryGetValue("preview", out var preview) && ws.ActiveDocument is { } target)
        {
            var snapshot = ws.Assets.Snapshot;
            if (target is ClipDocumentViewModel clip && snapshot.FindMesh(preview) is { } mesh) clip.UsePreviewMesh(mesh);
            else if (target is MeshDocumentViewModel meshDoc && snapshot.FindClip(preview) is { } c) meshDoc.UsePreviewClip(c);
            else Console.WriteLine($"[rfa] preview '{preview}' not found in the library");
            await SettleAsync();
            if (time is { } again) target.Playback.Seek(again);
        }
        if ((options.TryGetValue("bone", out var bone) || options.TryGetValue("select-bone", out bone)) && ws.ActiveDocument is { } active)
        {
            int index = active.Scene.Skeleton.IndexOf(bone);
            if (index >= 0) active.Selection.Select(index);
            else Console.WriteLine($"[rfa] bone '{bone}' not found in {active.Scene.MeshName}");
        }
        if (options.TryGetValue("camera", out var cam) && cam.Split(',') is { Length: >= 2 } c3
            && double.TryParse(c3[0], System.Globalization.NumberStyles.Float, inv, out double yaw)
            && double.TryParse(c3[1], System.Globalization.NumberStyles.Float, inv, out double pitch))
        {
            double zoom = c3.Length >= 3 && double.TryParse(c3[2], System.Globalization.NumberStyles.Float, inv, out double z) ? z : 1;
            foreach (var d in ws.Documents)
            {
                d.Scene.RequestFrame(d.Selection.Count > 0);
                d.Scene.Camera.Distance *= zoom;
                d.Scene.Camera.SetView(yaw, pitch);
            }
        }
    }

    /// <summary>The <c>--show</c> tokens, as 1.0.1's <c>ApplyDisplayOverrides</c>.</summary>
    private static void ApplyShow(ViewportDisplaySettings display, string tokens)
    {
        foreach (string token in tokens.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.ToLowerInvariant()))
        {
            switch (token)
            {
                case "names": display.ShowBoneNames = true; break;
                case "nonames": display.ShowBoneNames = false; break;
                case "skeleton": display.ShowSkeleton = true; break;
                case "noskeleton": display.ShowSkeleton = false; break;
                case "grid": display.ShowGrid = true; break;
                case "nogrid": display.ShowGrid = false; break;
                case "spheres": display.ShowSpheres = true; break;
                case "props": display.ShowProps = true; break;
                case "root": display.ShowRootPath = true; break;
                case "bind": display.BindPose = true; break;
                case "textured": display.MeshMode = MeshDisplayMode.Textured; break;
                case "flat": display.MeshMode = MeshDisplayMode.Flat; break;
                case "hidden": display.MeshMode = MeshDisplayMode.Off; break;
                case "fullbright": display.FullBright = true; break;
                case "ortho": display.Perspective = false; break;
                case "persp": display.Perspective = true; break;
                default:
                    if (token.StartsWith("bg:", StringComparison.Ordinal)
                        && Enum.TryParse<ViewportBackground>(token[3..], ignoreCase: true, out var bg)) display.Background = bg;
                    break;
            }
        }
    }

    /// <summary>A row the help table lists but the router never runs (the view handles the key while it has focus).</summary>
    private static readonly RelayCommand ViewLocal = new(() => { }, () => false);

    private static bool IsRfaOrNone(IDocument? d) => d is null || d is DocumentViewModel;

    /// <summary>
    /// 1.0.1's <c>Shortcuts*.cs</c> rows. Window-level keys keep their gesture; F1 (format reference) overrides the
    /// shell's F1 for RFA documents as 1.0.1 had it. Ctrl+Shift+L / Ctrl+Shift+M are the shell's pane toggles (same
    /// meaning) and are not repeated. Keys 1.0.1 handled only while the viewport, time bar or timeline had focus
    /// (Space, F, Q/W/E, 1/3/5/7, K, arrows, Home/End, Del...) stay view-local: they are listed, never routed, so Space
    /// on a focused button or list does not start playback. Mouse-only rows are left out: a <see cref="ModuleShortcut"/>
    /// cannot show a mouse gesture (shell wish: gesture text).
    /// </summary>
    private void AddShortcuts(RfaWorkspace ws)
    {
        const ModifierKeys None = ModifierKeys.None, Ctrl = ModifierKeys.Control, Shift = ModifierKeys.Shift, Alt = ModifierKeys.Alt;
        void Add(string category, string description, Key key, ModifierKeys modifiers, ICommand command, Func<IDocument?, bool>? scope) =>
            _shortcuts.Add(new ModuleShortcut(category, description, key, modifiers, command, scope));
        void Local(string category, string description, Key key, ModifierKeys modifiers, Func<IDocument?, bool> scope) =>
            Add(category, description, key, modifiers, ViewLocal, scope);

        Add("File", "New clip (a starting pose for a character mesh)", Key.N, Ctrl, ws.NewClips.NewClipCommand, IsRfaOrNone);
        Add("File", "Save every open clip and mesh", Key.S, Ctrl | Alt, ws.SaveAllCommand, IsRfa);

        Local("Viewport", "Frame the selection, or everything (viewport focused)", Key.F, None, IsRfa);
        Add("Viewport", "Frame the selection, or everything", Key.F, Ctrl | Shift, ws.FrameCommand, IsRfa);
        Local("Viewport", "Front view (viewport focused; numpad too)", Key.D1, None, IsRfa);
        Local("Viewport", "Side view (viewport focused; numpad too)", Key.D3, None, IsRfa);
        Local("Viewport", "Top view (viewport focused; numpad too)", Key.D7, None, IsRfa);
        Local("Viewport", "Perspective or orthographic (viewport focused)", Key.D5, None, IsRfa);
        Local("Viewport", "Clear the bone selection (viewport focused)", Key.Escape, None, IsRfa);

        Local("Gizmos", "Select tool (no gizmo; clicks select) (viewport focused)", Key.Q, None, IsRfa);
        Local("Gizmos", "Move tool (clip: root, animated positions, IK on a hand or foot; mesh: bind pose, sphere or prop point) (viewport focused)", Key.W, None, IsRfa);
        Local("Gizmos", "Rotate tool (clip: bones; mesh: a bone's bind pose or a prop point) (viewport focused)", Key.E, None, IsRfa);
        Local("Gizmos", "Cancel the drag in progress (while dragging)", Key.Escape, None, IsRfa);

        Local("Playback", "Play or pause (viewport, time bar or timeline focused)", Key.Space, None, IsClip);
        Add("Playback", "Play or pause from anywhere", Key.P, Ctrl | Shift, ws.PlayPauseCommand, IsClip);
        Local("Playback", "Step one frame back (viewport or time bar focused)", Key.Left, None, IsClip);
        Local("Playback", "Step one frame on (viewport or time bar focused)", Key.Right, None, IsClip);
        Local("Playback", "Step ten frames (time bar focused)", Key.Right, Shift, IsClip);
        Local("Playback", "First frame (viewport or time bar focused)", Key.Home, None, IsClip);
        Local("Playback", "Last frame (viewport or time bar focused)", Key.End, None, IsClip);

        Local("Timeline", "Delete the selected keys (timeline focused; Backspace too)", Key.Delete, None, IsClip);
        Local("Timeline", "Clear the key selection, or cancel a drag (timeline focused)", Key.Escape, None, IsClip);
        Local("Timeline", "Zoom in (timeline focused)", Key.OemPlus, Ctrl, IsClip);
        Local("Timeline", "Zoom out (timeline focused)", Key.OemMinus, Ctrl, IsClip);
        Local("Timeline", "Copy the selected keys (timeline focused)", Key.C, Ctrl, IsClip);
        Local("Timeline", "Cut the selected keys (timeline focused)", Key.X, Ctrl, IsClip);
        Local("Timeline", "Paste keys at the playhead (timeline focused)", Key.V, Ctrl, IsClip);
        Local("Timeline", "Paste keys mirrored left/right (timeline focused)", Key.V, Ctrl | Shift, IsClip);
        Local("Timeline", "Key the selected bones at the playhead (timeline focused)", Key.K, None, IsClip);
        Local("Timeline", "Select every key (timeline focused)", Key.A, Ctrl, IsClip);
        Local("Timeline", "Fit the clip into view (timeline focused)", Key.Home, None, IsClip);
        Local("Timeline", "Previous bone (timeline focused)", Key.Up, None, IsClip);
        Local("Timeline", "Next bone (timeline focused)", Key.Down, None, IsClip);

        Local("Table usage", "Copy the selected row's table line (Table usage focused)", Key.C, Ctrl, IsRfa);

        Add("Clip tools", "Trim / crop to a range", Key.T, Ctrl | Alt, ws.ClipTools.TrimCommand, IsClip);
        Add("Clip tools", "Shift in time", Key.H, Ctrl | Alt, ws.ClipTools.ShiftCommand, IsClip);
        Add("Clip tools", "Retime (scale the duration)", Key.R, Ctrl | Alt, ws.ClipTools.RetimeCommand, IsClip);
        Add("Clip tools", "Reverse", Key.V, Ctrl | Alt, ws.ClipTools.ReverseCommand, IsClip);
        Add("Clip tools", "Resample (bake)", Key.B, Ctrl | Alt, ws.ClipTools.ResampleCommand, IsClip);
        Add("Clip tools", "Reduce keys", Key.D, Ctrl | Alt, ws.ClipTools.ReduceCommand, IsClip);
        Add("Clip tools", "Offset the selected bones", Key.F, Ctrl | Alt, ws.ClipTools.OffsetCommand, IsClip);
        Add("Clip tools", "Compare with another clip", Key.G, Ctrl | Alt, ws.ClipTools.CompareCommand, IsClip);

        Add("View", "Show or hide the inspector", Key.I, Ctrl | Shift, ws.ToggleInspectorCommand, IsRfa);
        Add("Help", "RFA & V3C format reference", Key.F1, None, ws.FormatReferenceCommand, IsRfa);

        Add("Retarget", "Retarget the active clip onto another skeleton", Key.R, Ctrl, ws.Retarget.RetargetCommand, IsClip);
        Add("Mesh editing", "Rename the selected bone, sphere, prop point or submesh; retype a texture name", Key.F2, None, ws.MeshTools.RenameSelectedCommand, IsMesh);
        Local("Mesh editing", "Remove the selected collision sphere or prop point (structure tree focused)", Key.Delete, None, IsMesh);
        Local("Mesh editing", "Move the selected bone up in the index order (structure tree focused)", Key.Up, Alt, IsMesh);
        Local("Mesh editing", "Move the selected bone down in the index order (structure tree focused)", Key.Down, Alt, IsMesh);
        Add("glTF", "Export the active mesh or clip to glTF", Key.E, Ctrl, ws.Gltf.ExportCommand, IsRfa);
        Add("glTF", "Import animations from a glTF file", Key.I, Ctrl, ws.Gltf.ImportAnimationCommand, IsRfaOrNone);
    }

    private static MenuItem WithContext(MenuItem item, object context)
    {
        item.DataContext = context;
        return item;
    }

    private static MenuItem Item(string header, string toolTip, ICommand command) =>
        new() { Header = header, ToolTip = toolTip, Command = command, DataContext = Workspace };

    private static void AddTemplate<TModel, TView>(ResourceDictionary dictionary) where TView : FrameworkElement
    {
        var template = new DataTemplate(typeof(TModel)) { VisualTree = new FrameworkElementFactory(typeof(TView)) };
        dictionary.Add(template.DataTemplateKey!, template);
    }

    /// <summary>A clip or mesh kind; the documents are made by <see cref="Workspace"/>.</summary>
    /// <summary>
    /// 1.0.1's library, viewport and time-display settings. Load reads a fresh working copy each time the dialog
    /// opens; only Commit (OK) writes it back, so Cancel leaves the settings untouched.
    /// </summary>
    private sealed class RfaSettingsPage(RfaWorkspace workspace) : ISettingsPage
    {
        private FrameworkElement? _view;
        private SettingsViewModel? _working;

        public string Title => SettingsPageTitle;
        public FrameworkElement View => _view ??= Views.Dialogs.SettingsDialog.TakeModuleSections(workspace);

        public void Load()
        {
            _working = new SettingsViewModel(workspace);
            View.DataContext = _working;
        }

        public void Commit() => _working?.ApplyModuleSettings();
    }

    public sealed class RfaDocumentKind(string id, string displayName, IReadOnlyList<string> extensions, string filter, string association) : IDocumentKind
    {
        public string Id => id;
        public string DisplayName => displayName;
        public IReadOnlyList<string> Extensions => extensions;
        public string FileFilter => filter;
        public bool CanCreateNew => id == "rfa.clip";
        public string AssociationDescription => association;

        /// <summary>New Clip runs RFA's New Clip dialog, which adds the clip itself; nothing is returned.</summary>
        public IDocument? CreateNew()
        {
            if (Workspace.NewClips.NewClipCommand.CanExecute(null)) Workspace.NewClips.NewClipCommand.Execute(null);
            return null;
        }

        public IDocument Open(string path) => Workspace.CreateForShell(AtomicFile.ReadAllBytes(path), Path.GetFileName(path), Path.GetFullPath(path));
        public IDocument OpenBytes(byte[] bytes, string displayName, string originText) => Workspace.CreateArchivedForShell(bytes, displayName, originText);
        public IDocument Restore(RecoverySnapshot snapshot) => Workspace.RestoreForShell(snapshot);
    }

    /// <summary>glTF files: animation and/or mesh import through RFA's dialog (an effect file is left to VFX).</summary>
    private sealed class GltfImporter : IFileImporter
    {
        public string DisplayName => "glTF as animation or mesh";
        public IReadOnlyList<string> Extensions { get; } = [".gltf", ".glb"];
        public string FileFilter => "glTF (*.gltf;*.glb)|*.gltf;*.glb";

        public int Probe(string path)
        {
            try
            {
                var gltf = GltfReader.ReadFile(path);
                foreach (var node in gltf.Nodes)
                {
                    if (node.Extras is JsonObject extras && extras["rf_type"] is JsonValue type && type.TryGetValue(out string? text)
                        && text.StartsWith("vfx", StringComparison.OrdinalIgnoreCase))
                        return 0;
                }
            }
            catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
            {
                // Unreadable here: the import dialog reports why.
            }
            return 10;
        }

        public void Import(string path) => Workspace.Gltf.ImportFile(path);
    }
}
