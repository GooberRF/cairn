using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Ui.Controls;
using Cairn.Ui.Documents;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Modules;

namespace Cairn.Atx.Ui;

/// <summary>The animated textures module hosted by the Cairn shell.</summary>
public sealed class AtxModule : ModuleBase, IAssetPreviewProvider
{
    /// <inheritdoc/>
    public bool CanPreview(string fileName) => fileName.EndsWith(".atx", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public System.Windows.FrameworkElement? CreatePreview(byte[] bytes, string fileName) => CreatePreview(bytes, fileName, null);

    /// <inheritdoc/>
    public System.Windows.FrameworkElement? CreatePreview(byte[] bytes, string fileName, IAssetSiblings? siblings)
    {
        if (!CanPreview(fileName)) return null;
        var assets = Shell?.Assets;
        // Frames and the mask beside the texture (same packfile) win over the game data's.
        return new Preview.AtxPreview(bytes, fileName, () => siblings.Layer(assets?.Resolver));
    }

    private readonly AtxKind _kind = new();
    private List<MenuContribution>? _menus;

    /// <summary>The module's workspace (old MainViewModel), once initialized.</summary>
    internal static AtxWorkspace? Workspace { get; private set; }

    /// <summary>The "atx" document kind.</summary>
    internal static IDocumentKind DocumentKind { get; private set; } = null!;

    public AtxModule() => DocumentKind = _kind;

    public override string Id => "atx";

    public override string DisplayName => "Animated textures";

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        Workspace = new AtxWorkspace(shell);
        DocumentKind = _kind;
    }

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [_kind];

    public override IReadOnlyList<IFileImporter> Importers { get; } = [new VbmImporterEntry()];

    public override IReadOnlyList<MenuContribution> Menus => _menus ??= BuildMenus();

    private List<ToolbarContribution>? _toolbar;
    private List<ShortcutInfo>? _shortcuts;

    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts ??= BuildShortcuts();

    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _toolbar ??= BuildToolbar();

    public override IReadOnlyList<HelpTopic> HelpTopics =>
        [new("atx.format", "ATX format reference", HelpDocuments.FormatReference)];

    public override IReadOnlyList<ISettingsPage> SettingsPages => [new AtxSettingsPage(Workspace!)];

    public override IReadOnlyList<System.Windows.ResourceDictionary> Resources =>
        [new() { Source = new Uri("/Cairn.Atx.Ui;component/Themes/AtxResources.xaml", UriKind.Relative) }];

    /// <summary>
    /// Diagnostic runs: <c>--frame n</c> selects frame n (0-based) of the active .atx, <c>--problems true</c>
    /// shows the problems pane, <c>--find text</c> opens the find bar on that pattern, <c>--completion true</c>
    /// opens the completion list in the source editor. (A flag directly before a file path takes
    /// that path as its value, so give the flags a value or put them after the files.)
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (Active is not { } doc) return;
        if (options.TryGetValue("problems", out var problems) && Workspace is { } ws)
            ws.IsProblemsVisible = !problems.Equals("false", StringComparison.OrdinalIgnoreCase);
        var busy = Cairn.Ui.Services.BusyTracker.Begin("atx diagnostic options");
        System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            try
            {
                if (options.TryGetValue("frame", out var frame) && int.TryParse(frame, out int index)
                    && index >= 0 && index < doc.Frames.Rows.Count)
                    doc.Frames.SelectOnly(index);
                if ((doc.View as Views.DocumentView)?.SourceEditor is { } editor)
                {
                    if (options.TryGetValue("find", out var find)) editor.OpenSearchFor(find);
                    else if (options.ContainsKey("completion")) editor.ShowCompletionForCapture();
                }
            }
            finally { busy.Dispose(); }
        });
    }

    private static bool IsAtx(IDocument? document) => document is DocumentViewModel;

    private static DocumentViewModel? Active => Workspace?.ActiveDocument;

    private static Views.SourceEditorView? ActiveEditor =>
        (Active?.View as Views.DocumentView)?.SourceEditor;

    /// <summary>A command forwarded to the active document's frames list (its state follows activation).</summary>
    private static ICommand Frames(Func<FrameListViewModel, ICommand> pick) => new RelayCommand(
        p => { if (Active is { } d) pick(d.Frames).Execute(p); },
        p => Active is { } d && pick(d.Frames).CanExecute(p));

    /// <summary>An action on the active document's source editor.</summary>
    private static ICommand Editor(Action<Views.SourceEditorView> act) => new RelayCommand(
        () => { if (ActiveEditor is { } e) act(e); }, () => Active is not null);

    private ICommand? _addSequence, _find, _replace, _comment, _focusSource;

    private ICommand AddSequenceCommand => _addSequence ??= new RelayCommand(
        () => { if (Active is { } d) { Workspace!.CommitPendingEdits(); Workspace.Dialogs.ShowAddSequence(d); } },
        () => Active?.CanEditStructure == true);
    private ICommand FindCommand => _find ??= Editor(e => e.OpenSearch(replace: false));
    private ICommand ReplaceCommand => _replace ??= Editor(e => e.OpenSearch(replace: true));
    private ICommand ToggleCommentCommand => _comment ??= Editor(e => e.ToggleComment());
    private ICommand FocusSourceCommand => _focusSource ??= Editor(e => e.FocusEditor());

    private List<ShortcutInfo> BuildShortcuts()
    {
        var ws = Workspace!;
        const ModifierKeys Ctrl = ModifierKeys.Control, CtrlShift = ModifierKeys.Control | ModifierKeys.Shift;
        // The frames list, preview and editor handle Insert, Ctrl+D, Del, Alt+Up/Down, Ctrl+A,
        // Ctrl+X/C/V, F2, Ctrl+F/H, F3 and Ctrl+/ themselves when they have focus, as before; the
        // window-wide keys are the ones bound here. Space, Ctrl+A and Del never fire in a text box.
        return
        [
            // creates a document, but the gesture is RFA's inspector toggle too: only with no document or an .atx active
            new("File", "Import a .vbm as an .atx", Key.I, CtrlShift, ws.ImportVbmCommand, d => d is null || IsAtx(d)),
            new("Frames", "Add frames from a .vpp archive", Key.V, CtrlShift, ws.AddFramesFromVppCommand, IsAtx),
            new("Frames", "Bulk frame timing", Key.T, Ctrl, ws.BulkTimingCommand, IsAtx),
            new("View", "Show or hide the problems panel", Key.M, CtrlShift, ws.ToggleProblemsCommand, IsAtx, AllowInTextInput: true),
            new("View", "Play or pause the preview", Key.Space, ModifierKeys.None, ws.PlayPauseCommand, IsAtx),
            new("View", "Focus the source editor", Key.E, Ctrl, FocusSourceCommand, IsAtx, AllowInTextInput: true),
            // 1.1.0: F1 = ATX format reference. The router tries the active document's module first,
            // so with an .atx active F1 opens the reference (Help > Keyboard Shortcuts stays in the menu);
            // likewise Ctrl+Shift+M above toggles the problems pane instead of the shell's bottom pane.
            new("Help", "ATX format reference", Key.F1, ModifierKeys.None,
                new RelayCommand(() => Shell.ShowHelp("atx.format")), IsAtx, AllowInTextInput: true),
            // Display-only rows for the shortcut table: the focused view handles these keys itself
            // (the router skips them because the command can never execute).
            new("Edit", "Cut the selected frames as TOML (frames list)", Key.X, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Copy the selected frames as TOML (frames list)", Key.C, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Paste frames from TOML (frames list)", Key.V, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Find in the source", Key.F, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Find and replace in the source", Key.H, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Next match", Key.F3, ModifierKeys.None, DisplayOnly, IsAtx),
            new("Edit", "Previous match", Key.F3, ModifierKeys.Shift, DisplayOnly, IsAtx),
            new("Edit", "Comment or uncomment the selected lines", Key.OemQuestion, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Quick fixes at the caret", Key.OemPeriod, Ctrl, DisplayOnly, IsAtx),
            new("Edit", "Suggest keys and values", Key.Space, Ctrl, DisplayOnly, IsAtx),
            new("Frames", "Add frames from image files", Key.Insert, ModifierKeys.None, DisplayOnly, IsAtx),
            new("Frames", "Duplicate the selected frames", Key.D, Ctrl, DisplayOnly, IsAtx),
            new("Frames", "Remove the selected frames", Key.Delete, ModifierKeys.None, DisplayOnly, IsAtx),
            new("Frames", "Move the selection up", Key.Up, ModifierKeys.Alt, DisplayOnly, IsAtx),
            new("Frames", "Move the selection down", Key.Down, ModifierKeys.Alt, DisplayOnly, IsAtx),
            new("Frames", "Select every frame (frames list)", Key.A, Ctrl, DisplayOnly, IsAtx),
            new("Frames", "Rename a frame's file", Key.F2, ModifierKeys.None, DisplayOnly, IsAtx),
            new("View", "Previous frame (timeline focused)", Key.Left, ModifierKeys.None, DisplayOnly, IsAtx),
            new("View", "Next frame (timeline focused)", Key.Right, ModifierKeys.None, DisplayOnly, IsAtx),
        ];
    }

    /// <summary>A command that never executes, for shortcut rows that only document a view-local key.</summary>
    private static readonly RelayCommand DisplayOnly = new(() => { }, () => false);

    private static MenuItem Item(string header, ICommand command, string? gesture = null, string? tip = null)
    {
        var item = new MenuItem { Header = header, Command = command, InputGestureText = gesture ?? "" };
        if (tip is not null) item.ToolTip = tip;
        return item;
    }

    private List<MenuContribution> BuildMenus()
    {
        var ws = Workspace!;
        var addFrames = new MenuItem { Header = "_Add Frames" };
        addFrames.Items.Add(Item("_Browse...", Frames(f => f.AddFramesCommand), "Insert", "Pick image files from disk"));
        addFrames.Items.Add(Item("From _VPP...", ws.AddFramesFromVppCommand, "Ctrl+Shift+V",
            "Browse the .vpp archives the game searches and pick images out of them"));
        var frames = new MenuItem { Header = "F_rames", Name = "AtxFramesMenu" };
        foreach (var child in new Control[]
        {
            addFrames,
            Item("Add _Sequence...", AddSequenceCommand, null, "Detect a numbered run of images, or generate names from a pattern"),
            Item("_Locate File...", Frames(f => f.LocateFileCommand), null,
                "Pick the image for a frame whose file cannot be found. Available when one such frame is selected."),
            new Separator(),
            Item("_Duplicate", Frames(f => f.DuplicateCommand), "Ctrl+D"),
            Item("Re_move", Frames(f => f.RemoveCommand), "Del"),
            Item("Move _Up", Frames(f => f.MoveUpCommand), "Alt+Up"),
            Item("Move Do_wn", Frames(f => f.MoveDownCommand), "Alt+Down"),
            new Separator(),
            Item("Re_verse Selection", Frames(f => f.ReverseCommand)),
            Item("Sort Selection by _Name", Frames(f => f.SortCommand)),
            Item("Select _All Frames", Frames(f => f.SelectAllCommand), "Ctrl+A"),
            new Separator(),
            Item("_Bulk Frame Timing...", ws.BulkTimingCommand, "Ctrl+T", "Retime many frames at once: set, scale, distribute or ramp"),
        })
            frames.Items.Add(child);

        return
        [
            new(MenuSlot.FileImport, 10, Item("_Import VBM...", ws.ImportVbmCommand, "Ctrl+Shift+I",
                "Turn a legacy animated .vbm into one TGA per frame plus a matching .atx")),
            new(MenuSlot.FileImport, 11, Item("Import VBM from V_PP...", ws.ImportVbmFromVppCommand, null,
                "Pick a .vbm out of the game's archives. Needs a file open, because the archive list comes from that file's search path.")),
            new(MenuSlot.Edit, 10, new Separator(), IsAtx),
            new(MenuSlot.Edit, 11, Item("Cu_t Frames", Frames(f => f.CutCommand), "Ctrl+X"), IsAtx),
            new(MenuSlot.Edit, 12, Item("_Copy Frames", Frames(f => f.CopyCommand), "Ctrl+C"), IsAtx),
            new(MenuSlot.Edit, 13, Item("_Paste Frames", Frames(f => f.PasteCommand), "Ctrl+V"), IsAtx),
            new(MenuSlot.Edit, 14, new Separator(), IsAtx),
            new(MenuSlot.Edit, 15, Item("_Find...", FindCommand, "Ctrl+F"), IsAtx),
            new(MenuSlot.Edit, 16, Item("Find and Re_place...", ReplaceCommand, "Ctrl+H"), IsAtx),
            new(MenuSlot.Edit, 17, Item("Toggle Co_mment", ToggleCommentCommand, "Ctrl+/"), IsAtx),
            new(MenuSlot.TopLevel, 10, frames, IsAtx),
            new(MenuSlot.View, 10, Item("_Problems Panel", ws.ToggleProblemsCommand, "Ctrl+Shift+M",
                "Show or hide the list of problems in the active file"), IsAtx),
            new(MenuSlot.View, 11, Item("Pla_y / Pause Preview", ws.PlayPauseCommand, "Space",
                "Start or stop the animated preview (Space when the preview has focus)"), IsAtx),
            new(MenuSlot.View, 12, Item("Focus _Source Editor", FocusSourceCommand, "Ctrl+E"), IsAtx),
        ];
    }

    private List<ToolbarContribution> BuildToolbar()
    {
        var ws = Workspace!;
        var add = new SplitToolButton
        {
            GlyphSize = 13, Glyph = "",
            PrimaryCommand = Frames(f => f.AddFramesCommand),
            SecondaryCommand = ws.AddFramesFromVppCommand,
            PrimaryToolTip = "Add frames from image files (Insert)",
            SecondaryToolTip = "Add frames from a .vpp archive (Ctrl+Shift+V)",
            MenuToolTip = "More ways to add frames",
        };
        Button Tool(string glyph, ICommand command, string tip)
        {
            var b = new Button { Content = glyph, Command = command, ToolTip = tip };
            b.SetResourceReference(System.Windows.FrameworkElement.StyleProperty, "ToolButton");
            b.FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            System.Windows.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }
        var problems = new System.Windows.Controls.Primitives.ToggleButton
        {
            Content = "Problems", Padding = new System.Windows.Thickness(8, 2, 8, 2),
            ToolTip = "Show or hide the problems panel (Ctrl+Shift+M)", DataContext = ws,
        };
        problems.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new System.Windows.Data.Binding(nameof(AtxWorkspace.IsProblemsVisible)) { Mode = System.Windows.Data.BindingMode.TwoWay });
        return
        [
            new(10, add, IsAtx),
            new(11, Tool("", Frames(f => f.DuplicateCommand), "Duplicate frames (Ctrl+D)"), IsAtx),
            new(12, Tool("", Frames(f => f.RemoveCommand), "Remove frames (Delete)"), IsAtx),
            new(13, problems, IsAtx),
        ];
    }

    /// <summary>The .atx document kind.</summary>
    private sealed class AtxKind : IDocumentKind
    {
        private int _untitled;
        private static AtxWorkspace workspace => Workspace!;

        public string Id => "atx";
        public string DisplayName => "Animated texture";
        public IReadOnlyList<string> Extensions => [".atx"];
        public string FileFilter => "Animated textures (*.atx)|*.atx";
        public bool CanCreateNew => true;
        public string AssociationDescription => "Animated texture";

        public IDocument? CreateNew() => new DocumentViewModel(workspace,
            NewFileTemplates.Create(workspace.AtxSettings.NewFileTemplate), null, $"Untitled {++_untitled}.atx");

        public IDocument Open(string path)
        {
            var file = AtxTextFiles.Read(path);
            return new DocumentViewModel(workspace, file.Text, Path.GetFullPath(path), Path.GetFileName(path), file.Encoding);
        }

        public IDocument OpenBytes(byte[] bytes, string displayName, string originText)
        {
            var (text, encoding) = AtxTextFiles.Decode(bytes);
            return new DocumentViewModel(workspace, text, null, displayName, encoding) { OriginText = originText };
        }

        public IDocument Restore(RecoverySnapshot snapshot)
        {
            string text = System.Text.Encoding.UTF8.GetString(snapshot.Data);
            DocumentViewModel document;
            if (snapshot.OriginalPath is { } path && File.Exists(path))
                document = (DocumentViewModel)Open(path);
            else
                document = new DocumentViewModel(workspace, "", null, snapshot.DisplayName);
            document.RestoreText(text);
            return document;
        }
    }

    /// <summary>File &gt; Import of a .vbm: the Import VBM flow.</summary>
    private sealed class VbmImporterEntry : IFileImporter
    {
        private static AtxWorkspace workspace => Workspace!;
        public string DisplayName => "VBM as animated texture";
        public IReadOnlyList<string> Extensions => [".vbm"];
        public string FileFilter => "Volition bitmaps (*.vbm)|*.vbm";
        public int Probe(string path) => path.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase) ? 10 : 0;
        public void Import(string path) => workspace.ImportVbmCommand.Execute(VbmImportSource.FromFile(path));
    }
}
