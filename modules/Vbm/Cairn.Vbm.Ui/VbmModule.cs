using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Ui.Services;
using Cairn.Vbm.Ui.Dialogs;
using Cairn.Vbm.Ui.Documents;
using Cairn.Workspace;

namespace Cairn.Vbm.Ui;

/// <summary>
/// The Volition bitmaps (.vbm) module: opens a bitmap as a document with an animated preview, a frame strip and its
/// facts; edits frames and the frame rate with undo; exports frames as TGA or PNG; makes new bitmaps from images; and
/// hands a bitmap to whichever module converts .vbm to .atx (found through <see cref="IAssetConverter"/>, so this
/// module does not depend on the animated textures module).
/// </summary>
public sealed class VbmModule : ModuleBase
{
    /// <summary>The Problems tab's id.</summary>
    public const string ProblemsPanelId = VbmProblemsPanel.PanelId;

    private VbmKind? _kind;
    private ModuleSettings? _settings;
    private List<MenuContribution>? _menus;
    private List<ToolbarContribution>? _toolbar;
    private List<ShortcutInfo>? _shortcuts;

    public VbmModule()
    {
        PlayPauseCommand = new RelayCommand(() => { if (Active is { } d) d.IsPlaying = !d.IsPlaying; }, () => Active is { IsBroken: false, Current.IsAnimated: true });
        PreviousFrameCommand = new RelayCommand(() => StepActive(-1), () => Active is { IsBroken: false, Current.IsAnimated: true });
        NextFrameCommand = new RelayCommand(() => StepActive(1), () => Active is { IsBroken: false, Current.IsAnimated: true });
        ConvertToAtxCommand = new AsyncRelayCommand(() => Active is { } d ? ConvertToAtxAsync(d) : Task.CompletedTask, () => Active is { IsBroken: false } d && FindConverter(d.DisplayName) is not null);
        ExportFramesCommand = new AsyncRelayCommand(() => Active is { } d ? ExportFramesAsync(d) : Task.CompletedTask, () => Active is { IsBroken: false });
        ReplaceFrameCommand = new AsyncRelayCommand(() => Active is { } d ? ReplaceFrameAsync(d) : Task.CompletedTask, CanEdit);
        AddFramesCommand = new AsyncRelayCommand(() => Active is { } d ? AddFramesAsync(d) : Task.CompletedTask, CanEdit);
        RemoveFramesCommand = new RelayCommand(() => Active?.RemoveSelected(), () => CanEdit() && Active!.SelectedFrames.Count < Active.Current.FrameCount);
        DuplicateFramesCommand = new RelayCommand(() => Active?.DuplicateSelected(), CanEdit);
        MoveEarlierCommand = new RelayCommand(() => Active?.MoveSelected(-1), () => CanEdit() && Active!.SelectedFrames[0] > 0);
        MoveLaterCommand = new RelayCommand(() => Active?.MoveSelected(1), () => CanEdit() && Active!.SelectedFrames[^1] < Active.Current.FrameCount - 1);
        ReverseCommand = new RelayCommand(() => Active?.ReverseFrames(), () => CanEdit() && Active!.Current.IsAnimated);
        CopyFramesCommand = new RelayCommand(() => { if (Active is { } d) CopyFrames(d); }, () => Active is { IsBroken: false });
        PasteFramesCommand = new AsyncRelayCommand(() => Active is { } d ? PasteAsync(d) : Task.CompletedTask, CanEdit);
        ShowProblemsCommand = new RelayCommand(() => Shell?.ShowPanel(ProblemsPanelId));
    }

    public override string Id => "vbm";
    public override string DisplayName => "Volition bitmaps";

    public override void Initialize(IShellContext shell)
    {
        base.Initialize(shell);
        shell.ActiveDocumentChanged += (_, _) => RefreshCommands();
    }

    /// <summary>Re-queries this module's commands and the shell's (after a selection, edit or activation change).</summary>
    internal void RefreshCommands()
    {
        foreach (var command in new[] { PlayPauseCommand, PreviousFrameCommand, NextFrameCommand, RemoveFramesCommand, DuplicateFramesCommand,
                     MoveEarlierCommand, MoveLaterCommand, ReverseCommand, CopyFramesCommand })
            command.RaiseCanExecuteChanged();
        foreach (var command in new[] { ConvertToAtxCommand, ExportFramesCommand, ReplaceFrameCommand, AddFramesCommand, PasteFramesCommand })
            command.RaiseCanExecuteChanged();
        Shell?.RefreshCommands();
    }

    /// <summary>The shell (for documents).</summary>
    internal IShellContext ShellContext => Shell;

    /// <summary>The module's settings ("vbm." keys).</summary>
    internal ModuleSettings Settings => _settings ??= new ModuleSettings(Shell.Settings, Id);

    /// <summary>The module's typed settings (defaults for new bitmaps, resize choice).</summary>
    public VbmSettings Options => new(Settings);

    public override IReadOnlyList<ISettingsPage> SettingsPages => _pages ??= [new VbmSettingsPage(Options)];

    private List<ISettingsPage>? _pages;

    /// <summary>The .vbm document kind.</summary>
    public VbmKind Kind => _kind ??= new VbmKind(this);

    public override IReadOnlyList<IDocumentKind> DocumentKinds => [Kind];

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand PreviousFrameCommand { get; }
    public RelayCommand NextFrameCommand { get; }
    public AsyncRelayCommand ConvertToAtxCommand { get; }
    public AsyncRelayCommand ExportFramesCommand { get; }
    public AsyncRelayCommand ReplaceFrameCommand { get; }
    public AsyncRelayCommand AddFramesCommand { get; }
    public RelayCommand RemoveFramesCommand { get; }
    public RelayCommand DuplicateFramesCommand { get; }
    public RelayCommand MoveEarlierCommand { get; }
    public RelayCommand MoveLaterCommand { get; }
    public RelayCommand ReverseCommand { get; }
    public RelayCommand CopyFramesCommand { get; }
    public AsyncRelayCommand PasteFramesCommand { get; }
    public RelayCommand ShowProblemsCommand { get; }

    private VbmDocument? Active => Shell?.ActiveDocument as VbmDocument;

    private bool CanEdit() => Active is { IsReadOnly: false };

    private static bool IsVbm(IDocument? document) => document is VbmDocument;

    private void StepActive(int delta)
    {
        if (Active is not { } d) return;
        if ((d.View as VbmDocumentView) is { } view) view.Step(delta);
    }

    public override IReadOnlyList<ResourceDictionary> Resources =>
        [new() { Source = new Uri("/Cairn.Vbm.Ui;component/Themes/VbmResources.xaml", UriKind.Relative) }];

    public override IReadOnlyList<PanelContribution> Panels =>
        [new(ProblemsPanelId, "Problems", PanelSide.Bottom, 10, d => d is VbmDocument v ? VbmProblemsPanel.For(v) : null)];

    public override IReadOnlyList<HelpTopic> HelpTopics => [new("vbm.help", "Volition bitmaps", VbmHelp.Build)];

    public override IReadOnlyList<ShortcutInfo> Shortcuts => _shortcuts ??=
    [
        new("Bitmap", "Play or pause the animation", Key.Space, ModifierKeys.None, PlayPauseCommand, IsVbm),
        new("Bitmap", "Previous frame", Key.OemComma, ModifierKeys.None, PreviousFrameCommand, IsVbm),
        new("Bitmap", "Next frame", Key.OemPeriod, ModifierKeys.None, NextFrameCommand, IsVbm),
        new("Bitmap", "Replace the selected frame with an image", Key.R, ModifierKeys.Control, ReplaceFrameCommand, IsVbm),
        new("Bitmap", "Add images as frames", Key.Insert, ModifierKeys.None, AddFramesCommand, IsVbm),
        new("Bitmap", "Duplicate the selected frames", Key.D, ModifierKeys.Control, DuplicateFramesCommand, IsVbm),
        new("Bitmap", "Remove the selected frames", Key.Delete, ModifierKeys.None, RemoveFramesCommand, IsVbm),
        new("Bitmap", "Move the selected frames earlier", Key.Left, ModifierKeys.Alt, MoveEarlierCommand, IsVbm),
        new("Bitmap", "Move the selected frames later", Key.Right, ModifierKeys.Alt, MoveLaterCommand, IsVbm),
        new("Bitmap", "Copy the selected frames", Key.C, ModifierKeys.Control, CopyFramesCommand, IsVbm),
        new("Bitmap", "Paste copied frames, or an image, as new frames after the selection", Key.V, ModifierKeys.Control, PasteFramesCommand, IsVbm),
        new("Bitmap", "Export frames as images", Key.E, ModifierKeys.Control | ModifierKeys.Shift, ExportFramesCommand, IsVbm),
    ];

    private static MenuItem Item(string header, ICommand command, string? gesture = null, string? tip = null)
    {
        var item = new MenuItem { Header = header, Command = command, InputGestureText = gesture ?? "" };
        if (tip is not null) item.ToolTip = tip;
        return item;
    }

    public override IReadOnlyList<MenuContribution> Menus => _menus ??= BuildMenus();

    private List<MenuContribution> BuildMenus()
    {
        var format = new MenuItem { Header = "Pixel _Format" };
        foreach (var f in new[] { VbmPixelFormat.Argb1555, VbmPixelFormat.Argb4444, VbmPixelFormat.Rgb565 })
        {
            var item = new MenuItem { Header = f.DisplayName(), IsCheckable = true, Tag = f };
            item.Click += (_, _) => { if (Active is { IsReadOnly: false } d) d.ConvertFormat(f); };
            format.Items.Add(item);
        }
        format.SubmenuOpened += (_, _) =>
        {
            foreach (MenuItem item in format.Items) { item.IsChecked = Active?.Current.Format == (VbmPixelFormat)item.Tag; item.IsEnabled = CanEdit(); }
        };
        var mips = new MenuItem { Header = "_Mip Levels" };
        mips.Items.Add(new MenuItem { Header = "(none)" });
        mips.SubmenuOpened += (_, _) =>
        {
            mips.Items.Clear();
            if (Active is not { } d) return;
            int max = VbmFile.MaxMipLevels(d.Current.Width, d.Current.Height);
            for (int levels = 1; levels <= max; levels++)
            {
                var (w, h) = d.Current.LevelSize(levels - 1);
                int n = levels;
                var item = new MenuItem
                {
                    Header = levels == 1 ? "1 (no mipmaps)" : string.Format(CultureInfo.CurrentCulture, "{0} (down to {1} x {2})", levels, w, h),
                    IsCheckable = true, IsChecked = d.Current.MipLevels == levels, IsEnabled = !d.IsReadOnly,
                };
                item.Click += (_, _) => d.SetMipLevels(n);
                mips.Items.Add(item);
            }
        };

        var bitmap = new MenuItem { Header = "_Bitmap" };
        foreach (var child in new Control[]
        {
            Item("_Play / Pause", PlayPauseCommand, "Space"),
            Item("Pre_vious Frame", PreviousFrameCommand, ","),
            Item("_Next Frame", NextFrameCommand, "."),
            new Separator(),
            Item("_Replace Frame...", ReplaceFrameCommand, "Ctrl+R", "Replace the selected frame with an image (resized and converted to this bitmap's format)"),
            Item("_Add Frames...", AddFramesCommand, "Insert", "Add images as new frames after the selection"),
            Item("_Duplicate Frames", DuplicateFramesCommand, "Ctrl+D"),
            Item("Re_move Frames", RemoveFramesCommand, "Del"),
            Item("_Copy Frames", CopyFramesCommand, "Ctrl+C", "Copy the selected frames (paste them in this or another bitmap)"),
            Item("_Paste Frames", PasteFramesCommand, "Ctrl+V", "Paste copied frames, or an image from the clipboard, as new frames after the selection"),
            Item("Move _Earlier", MoveEarlierCommand, "Alt+Left"),
            Item("Move _Later", MoveLaterCommand, "Alt+Right"),
            Item("Re_verse Frame Order", ReverseCommand),
            new Separator(),
            format,
            mips,
            new Separator(),
            Item("E_xport Frames...", ExportFramesCommand, "Ctrl+Shift+E", "Write the frames as TGA or PNG images"),
            Item("_Convert to ATX...", ConvertToAtxCommand, null, "Turn this bitmap into one image per frame plus an animated texture (.atx), and open it"),
        })
            bitmap.Items.Add(child);

        return
        [
            new(MenuSlot.TopLevel, 30, bitmap, IsVbm),
            new(MenuSlot.FileExport, 30, Item("VBM _Frames as Images...", ExportFramesCommand, "Ctrl+Shift+E", "Write the bitmap's frames as TGA or PNG images"), IsVbm),
            new(MenuSlot.FileExport, 31, Item("VBM as _ATX...", ConvertToAtxCommand, null, "Turn this bitmap into frame images plus an .atx"), IsVbm),
            new(MenuSlot.View, 30, Item("Pla_y / Pause Bitmap", PlayPauseCommand, "Space"), IsVbm),
            new(MenuSlot.Edit, 30, Item("_Copy Frames", CopyFramesCommand, "Ctrl+C", "Copy the selected frames"), IsVbm),
            new(MenuSlot.Edit, 31, Item("_Paste Frames", PasteFramesCommand, "Ctrl+V", "Paste copied frames, or an image from the clipboard, as new frames"), IsVbm),
        ];
    }

    public override IReadOnlyList<ToolbarContribution> ToolbarItems => _toolbar ??=
    [
        new(30, ToolText("Convert to ATX...", ConvertToAtxCommand, "Turn this bitmap into frame images plus an animated texture (.atx), and open it"), IsVbm),
        new(31, ToolText("Export Frames...", ExportFramesCommand, "Write the frames as TGA or PNG images (Ctrl+Shift+E)"), IsVbm),
    ];

    private static Button ToolText(string text, ICommand command, string tip)
    {
        var b = new Button { Content = text, Command = command, ToolTip = tip, Padding = new Thickness(8, 2, 8, 2) };
        b.SetResourceReference(FrameworkElement.StyleProperty, "ToolButton");
        System.Windows.Automation.AutomationProperties.SetName(b, text.TrimEnd('.'));
        return b;
    }

    // ── Convert to ATX ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set by self-tests to stand in for the converting module.</summary>
    internal static IAssetConverter? ConverterOverride { get; set; }

    /// <summary>The module that converts <paramref name="name"/> to .atx, or null when none is loaded.</summary>
    internal IAssetConverter? FindConverter(string name) =>
        ConverterOverride ?? Shell?.Modules.OfType<IAssetConverter>().FirstOrDefault(c => SafeCanConvert(c, name));

    private static bool SafeCanConvert(IAssetConverter converter, string name)
    {
        try { return converter.CanConvert(name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase) ? name : name + ".vbm", ".atx"); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    /// <summary>
    /// The conversion request for <paramref name="document"/>: its current content, and where it came from. A packfile
    /// entry (a work copy from "Open in Cairn", or an entry opened directly) names its packfile, so the converter does
    /// not offer the temporary work folder as the place for the frames.
    /// </summary>
    internal AssetConversionRequest RequestFor(VbmDocument document, bool interactive = true, string? outputFolder = null)
    {
        string name = document.DisplayName;
        string? path = document.FilePath, archive = document.ArchivePath;
        if (path is not null && Shell.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.IsWorkCopy(path)) is { } provider)
        {
            if (provider.ArchiveEntryOf(path) is { } entry) { archive = entry.ArchivePath; name = entry.EntryName; }
            path = null;
        }
        if (!name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) name += ".vbm";
        return new AssetConversionRequest(name, document.Serialize(), ".atx")
        {
            FilePath = path, ArchivePath = path is null ? archive : null, Interactive = interactive, OutputFolder = outputFolder,
        };
    }

    /// <summary>"Convert to ATX...": the converting module's dialog over this bitmap's current content; the .atx opens.</summary>
    internal async Task<string?> ConvertToAtxAsync(VbmDocument document, bool interactive = true, string? outputFolder = null)
    {
        document.CommitPendingEdits();
        document.IsPlaying = false;
        if (FindConverter(document.DisplayName) is not { } converter)
        {
            Shell.Dialogs.ShowError("Convert to ATX", "No module in this Cairn converts bitmaps to animated textures.");
            return null;
        }
        try
        {
            return await converter.ConvertAsync(RequestFor(document, interactive, outputFolder)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Shell.Dialogs.ShowError("Convert to ATX", "The bitmap could not be converted: " + ex.Message);
            return null;
        }
    }

    // ── Export ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The folder the export window offers: the bitmap's own (not a temporary work copy's), else the last one used.</summary>
    internal string DefaultExportFolder(VbmDocument document)
    {
        if (Settings.Get<string>("exportFolder") is { Length: > 0 } last && Directory.Exists(last)) return last;
        if (document.Folder is { } folder && !Shell.Modules.OfType<IWorkCopyProvider>().Any(p => p.IsWorkCopy(document.FilePath!))) return folder;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private async Task ExportFramesAsync(VbmDocument document)
    {
        document.CommitPendingEdits();
        document.IsPlaying = false;
        var format = Settings.Get("exportFormat", VbmExportFormat.Tga);
        var window = new VbmExportWindow(Shell.Dialogs, document.DisplayName, document.Current.FrameCount, document.SelectedFrames, DefaultExportFolder(document), format)
        {
            Owner = Shell.MainWindow,
        };
        bool ok;
        using (ModalScope.Enter()) ok = window.ShowDialog() == true;
        if (!ok || window.Result is not { } request) return;
        if (!Shell.IsDiagnosticRun)
        {
            Settings.Set("exportFolder", request.Folder);
            Settings.Set("exportFormat", request.Format);
        }
        await ExportAsync(document, request, confirmReplace: true).ConfigureAwait(true);
    }

    /// <summary>Writes the frames <paramref name="request"/> names (off the UI thread); asks before replacing files.</summary>
    internal async Task<int> ExportAsync(VbmDocument document, VbmExportRequest request, bool confirmReplace)
    {
        var file = document.Current;
        var plan = VbmFrameExport.Plan(request.Folder, request.BaseName, request.Frames, file.FrameCount, request.Format);
        if (plan.Count == 0) return 0;
        var existing = VbmFrameExport.Existing(plan);
        if (existing.Count > 0 && confirmReplace)
        {
            string list = string.Join("\n", existing.Take(6).Select(Path.GetFileName)) + (existing.Count > 6 ? $"\n... and {existing.Count - 6} more" : "");
            int choice = Shell.Dialogs.Choose(existing.Count == 1 ? "Replace the existing image?" : $"Replace {existing.Count} existing images?",
                $"These files are already in {request.Folder}:\n{list}", ["_Replace", "Cancel"], 1);
            if (choice != 0) return 0;
        }
        int written;
        try
        {
            using var busy = BusyTracker.Begin("vbm export");
            written = await Task.Run(() => VbmFrameExport.Write(file, plan, request.Format)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException)
        {
            Shell.Dialogs.ShowError("The frames could not be exported", ex.Message);
            return 0;
        }
        Shell.ShowStatus(string.Format(CultureInfo.CurrentCulture, "Exported {0} frame{1} to {2}", written, written == 1 ? "" : "s", request.Folder));
        return written;
    }

    // ── Images in ────────────────────────────────────────────────────────────────────────────────────────────

    private static string ImageFilter()
    {
        string all = string.Join(";", VbmImages.Extensions.Select(e => "*" + e));
        return $"Images ({all})|{all}|Targa (*.tga)|*.tga|PNG (*.png)|*.png|Volition bitmaps (*.vbm)|*.vbm|All files (*.*)|*.*";
    }

    private string? ImageFolder(VbmDocument? document) =>
        Settings.Get<string>("imageFolder") is { Length: > 0 } last && Directory.Exists(last) ? last : document?.Folder;

    /// <summary>
    /// Asks for image files and decodes them off the UI thread (a .vbm gives every frame). Null when cancelled or when one
    /// could not be read (reported).
    /// </summary>
    internal async Task<IReadOnlyList<(string Name, BgraImage Image)>?> PickImagesAsync(VbmDocument? document, string title, bool multiple)
    {
        var paths = Shell.Dialogs.OpenFiles(ImageFolder(document), title, ImageFilter(), multiple);
        if (paths.Length == 0) return null;
        if (!Shell.IsDiagnosticRun && Path.GetDirectoryName(paths[0]) is { } folder) Settings.Set("imageFolder", folder);
        try
        {
            using var busy = BusyTracker.Begin("vbm images");
            return await Task.Run(() => LoadImages(paths)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException)
        {
            Shell.Dialogs.ShowError("The image could not be read", ex.Message);
            return null;
        }
    }

    /// <summary>Decodes image files in order; a .vbm adds each of its frames.</summary>
    internal static IReadOnlyList<(string Name, BgraImage Image)> LoadImages(IEnumerable<string> paths)
    {
        var images = new List<(string, BgraImage)>();
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            if (path.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase))
            {
                var file = VbmFile.Read(File.ReadAllBytes(path), name).File;
                for (int i = 0; i < file.FrameCount; i++) images.Add((file.FrameCount == 1 ? name : $"{name} [{i + 1}]", file.Decode(i)));
            }
            else images.Add((name, VbmImages.DecodeFile(path)));
        }
        return images;
    }

    /// <summary>
    /// Self-tests stand in for the resize window with this: it gets the images that are another size and returns the
    /// choice (null = cancel).
    /// </summary>
    internal static Func<IReadOnlyList<(string Name, BgraImage Image)>, VbmResizeOptions?>? ResizePromptOverride { get; set; }

    /// <summary>
    /// How images that are not <paramref name="document"/>'s frame size are fitted: the resize window (filter, fit, preview)
    /// when one is another size and the settings say to ask, else the remembered choice. The choice made is remembered.
    /// Null when cancelled. An unattended run never shows the window.
    /// </summary>
    internal VbmResizeOptions? AskResize(VbmDocument document, IReadOnlyList<(string Name, BgraImage Image)> images)
    {
        var f = document.Current;
        var options = Options;
        var odd = images.Where(i => i.Image.Width != f.Width || i.Image.Height != f.Height).ToList();
        if (odd.Count == 0) return options.Resize;
        VbmResizeOptions? chosen;
        if (ResizePromptOverride is { } prompt) chosen = prompt(odd);
        else if (!options.AskResize || Shell.IsDiagnosticRun) return options.Resize;
        else
        {
            var window = new VbmResizeWindow(odd, f.Width, f.Height, f.Format, f.Version, options.Resize) { Owner = Shell.MainWindow };
            bool ok;
            using (ModalScope.Enter()) ok = window.ShowDialog() == true;
            chosen = ok ? window.Result : null;
            if (ok && window.DontAskAgain) options.AskResize = false;
        }
        if (chosen is not null) options.Resize = chosen; // diagnostic runs keep settings in memory only
        return chosen;
    }

    private async Task ReplaceFrameAsync(VbmDocument document)
    {
        document.IsPlaying = false;
        if (await PickImagesAsync(document, "Replace frame " + (document.SelectedFrames[0] + 1), multiple: false).ConfigureAwait(true) is not { Count: > 0 } images) return;
        if (AskResize(document, [images[0]]) is not { } resize) return;
        document.ReplaceFrame(document.SelectedFrames[0], images[0].Image, resize);
    }

    private async Task AddFramesAsync(VbmDocument document)
    {
        document.IsPlaying = false;
        if (await PickImagesAsync(document, "Add frames", multiple: true).ConfigureAwait(true) is not { Count: > 0 } images) return;
        if (AskResize(document, images) is not { } resize) return;
        document.InsertFrames(document.SelectedFrames[^1] + 1, [.. images.Select(i => i.Image)], resize);
    }

    /// <summary>Image files dropped on the strip: decoded off the UI thread and inserted before frame <paramref name="index"/>.</summary>
    internal async Task<bool> InsertImageFilesAsync(VbmDocument document, IReadOnlyList<string> paths, int index)
    {
        document.IsPlaying = false;
        IReadOnlyList<(string Name, BgraImage Image)> images;
        try
        {
            using var busy = BusyTracker.Begin("vbm images");
            images = await Task.Run(() => LoadImages(paths)).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException)
        {
            Shell.Dialogs.ShowError("The image could not be read", ex.Message);
            return false;
        }
        if (images.Count == 0 || AskResize(document, images) is not { } resize) return false;
        return document.InsertFrames(Math.Clamp(index, 0, document.Current.FrameCount), [.. images.Select(i => i.Image)], resize,
            images.Count == 1 ? "Drop frame" : "Drop frames");
    }

    /// <summary>
    /// Frames <paramref name="indices"/> of <paramref name="source"/> inserted into <paramref name="document"/> before
    /// <paramref name="index"/> (paste, or a drag from another bitmap): exact copies when the layouts match, else fitted
    /// (asking how when the size differs) and converted.
    /// </summary>
    internal bool InsertFramesFrom(VbmDocument document, int index, VbmFile source, IReadOnlyList<int> indices, string label)
    {
        document.IsPlaying = false;
        var f = document.Current;
        VbmResizeOptions? resize = null;
        if (source.Width != f.Width || source.Height != f.Height)
        {
            // the first frame stands for all of them (they share the source's size)
            string name = indices.Count == 1
                ? string.Format(CultureInfo.CurrentCulture, "Frame {0} of {1}", indices[0] + 1, source.FrameCount)
                : string.Format(CultureInfo.CurrentCulture, "Each of the {0} frames", indices.Count);
            if (AskResize(document, [(name, source.Decode(indices[0]))]) is not { } chosen) return false;
            resize = chosen;
        }
        return document.InsertFramesFrom(index, source, indices, resize, label);
    }

    // ── Copy and paste ───────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The clipboard; self-tests put a fake here so they never touch the real one.</summary>
    internal static IVbmClipboard ClipboardService { get; set; } = new SystemVbmClipboard();

    /// <summary>The frames copied last (shared by every bitmap tab), or null.</summary>
    internal VbmFrameClip? FrameClip { get; private set; }

    /// <summary>
    /// Copies the selected frames: kept by the module for pasting into any bitmap tab, and the first one goes on the
    /// clipboard as an image (with a marker, so pasting here gives back the exact frames).
    /// </summary>
    internal bool CopyFrames(VbmDocument document)
    {
        if (document.IsBroken) return false;
        document.CommitPendingEdits();
        var file = document.Current;
        var indices = document.SelectedFrames.ToList();
        var clip = new VbmFrameClip(Guid.NewGuid().ToString("N"), file, indices, document.DisplayName);
        FrameClip = clip;
        BgraImage? image = null;
        try { image = file.Decode(indices[0]); }
        catch (ImageDecodeException) { }
        bool onClipboard = ClipboardService.TryWrite(clip.Token, image);
        string what = indices.Count == 1 ? string.Format(CultureInfo.CurrentCulture, "frame {0}", indices[0] + 1) : string.Format(CultureInfo.CurrentCulture, "{0} frames", indices.Count);
        Shell.ShowStatus(onClipboard ? $"Copied {what}" : $"Copied {what} (for pasting in Cairn only: the Windows clipboard is not available)");
        return true;
    }

    /// <summary>
    /// Pastes after the selection: the frames copied in Cairn when the clipboard still holds them (or cannot be read),
    /// else the clipboard's image (or copied image files) as new frames. False when there was nothing to paste.
    /// </summary>
    internal Task<bool> PasteAsync(VbmDocument document)
    {
        if (document.IsReadOnly) return Task.FromResult(false);
        document.CommitPendingEdits();
        int index = document.SelectedFrames[^1] + 1;
        bool readable = ClipboardService.TryRead(out var content);
        if (FrameClip is { } clip && (!readable || content.Token == clip.Token))
        {
            string label = clip.Indices.Count == 1 ? "Paste frame" : "Paste frames";
            return Task.FromResult(InsertFramesFrom(document, index, clip.Source, clip.Indices, label));
        }
        if (readable && content.Images.Count > 0)
        {
            document.IsPlaying = false;
            if (AskResize(document, content.Images) is not { } resize) return Task.FromResult(false);
            return Task.FromResult(document.InsertFrames(index, [.. content.Images.Select(i => i.Image)], resize,
                content.Images.Count == 1 ? "Paste image as frame" : "Paste images as frames"));
        }
        Shell.ShowStatus(readable ? "Nothing to paste: the clipboard holds no image or frames" : "The clipboard could not be read");
        return Task.FromResult(false);
    }

    /// <summary>File &gt; New &gt; Volition bitmap: images picked, then the new-bitmap window; null when cancelled.</summary>
    internal IDocument? CreateFromImages()
    {
        var paths = Shell.Dialogs.OpenFiles(ImageFolder(Active), "New VBM from images", ImageFilter(), true);
        if (paths.Length == 0) return null;
        IReadOnlyList<(string Name, BgraImage Image)> images;
        try { images = LoadImages(paths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageDecodeException)
        {
            Shell.Dialogs.ShowError("The image could not be read", ex.Message);
            return null;
        }
        if (!Shell.IsDiagnosticRun && Path.GetDirectoryName(paths[0]) is { } folder) Settings.Set("imageFolder", folder);
        var options = Options;
        var window = new VbmNewWindow(images, options.DefaultFps, options.DefaultFormat, options.Mipmaps, options.Resize) { Owner = Shell.MainWindow };
        bool ok;
        using (ModalScope.Enter()) ok = window.ShowDialog() == true;
        if (!ok || window.Result is not { } s) return null;
        var file = VbmEditing.Create([.. images.Select(i => i.Image)], s.Format, s.Fps, s.MipLevels, s.Width, s.Height, options.Resize);
        return VbmDocument.New(this, Kind, file, Kind.NextUntitledName());
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Diagnostic runs, applied to the active bitmap: <c>--vbm-frame n</c> shows frame n (1-based) paused,
    /// <c>--vbm-play true|false</c>, <c>--vbm-select 1,3</c> selects frames, <c>--vbm-zoom 100|fit</c>,
    /// <c>--vbm-alpha true</c> shows the alpha channel, <c>--vbm-mip n</c> shows mip level n, <c>--vbm-scaling
    /// auto|smooth|pixels</c>, <c>--vbm-play-frame n</c> shows frame n (the strip's play mark) keeping the selection.
    /// </summary>
    public override void ApplyDiagnosticOptions(IReadOnlyDictionary<string, string> options)
    {
        if (Active is not { } doc || doc.View is not VbmDocumentView view) return;
        static int? Int(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : null;
        if (options.TryGetValue("vbm-play", out var play)) doc.IsPlaying = !play.Equals("false", StringComparison.OrdinalIgnoreCase);
        if (options.TryGetValue("vbm-frame", out var frame) && Int(frame) is { } f)
        {
            doc.IsPlaying = false;
            doc.CurrentFrame = f - 1;
            doc.SelectedFrames = [doc.CurrentFrame];
        }
        if (options.TryGetValue("vbm-select", out var select))
            doc.SelectedFrames = [.. select.Split(',').Select(s => Int(s.Trim())).OfType<int>().Select(i => i - 1)];
        if (options.TryGetValue("vbm-zoom", out var zoom)) view.SetZoom(zoom.Equals("fit", StringComparison.OrdinalIgnoreCase) ? 0 : (Int(zoom) ?? 100) / 100.0);
        if (options.TryGetValue("vbm-alpha", out var alpha)) view.SetAlphaOnly(!alpha.Equals("false", StringComparison.OrdinalIgnoreCase));
        if (options.TryGetValue("vbm-mip", out var mip) && Int(mip) is { } m) view.SetMipLevel(m);
        if (options.TryGetValue("vbm-scaling", out var scaling) && Enum.TryParse<VbmScaling>(scaling, true, out var s)) view.SetScaling(s);
        // --vbm-play-frame n: the preview (and the strip's play mark) on frame n while the selection stays as given
        if (options.TryGetValue("vbm-play-frame", out var shown) && Int(shown) is { } p)
        {
            doc.IsPlaying = false;
            var keep = doc.SelectedFrames;
            doc.CurrentFrame = p - 1;
            doc.SelectedFrames = keep;
        }
    }
}

/// <summary>The <c>.vbm</c> document kind.</summary>
public sealed class VbmKind(VbmModule module) : IDocumentKind
{
    private int _untitled;

    public string Id => "vbm";
    public string DisplayName => "Volition bitmap";
    public IReadOnlyList<string> Extensions { get; } = [".vbm"];
    public string FileFilter => "Volition bitmaps (*.vbm)|*.vbm";
    public bool CanCreateNew => true;
    public string AssociationDescription => "Volition bitmap";

    /// <summary>"Untitled.vbm", "Untitled 2.vbm"...</summary>
    internal string NextUntitledName() => ++_untitled == 1 ? "Untitled.vbm" : $"Untitled {_untitled}.vbm";

    public IDocument? CreateNew() => module.CreateFromImages();

    public IDocument Open(string path) =>
        VbmDocument.Load(module, this, AtomicFile.ReadAllBytes(path), Path.GetFileName(path), Path.GetFullPath(path), null);

    public IDocument OpenBytes(byte[] bytes, string displayName, string originText) =>
        VbmDocument.Load(module, this, bytes, displayName, null, originText);

    /// <summary>A packfile entry: the document remembers the packfile (Convert to ATX names it).</summary>
    public IDocument OpenEntry(AssetLocation location, byte[] bytes, string originText)
    {
        var document = VbmDocument.Load(module, this, bytes, location.ResolvedName, null, originText);
        document.ArchivePath = location.ArchivePath;
        return document;
    }

    public IDocument Restore(RecoverySnapshot snapshot)
    {
        var document = VbmDocument.Load(module, this, snapshot.Data, snapshot.DisplayName, snapshot.OriginalPath, null);
        document.MarkAsNew();
        return document;
    }
}
