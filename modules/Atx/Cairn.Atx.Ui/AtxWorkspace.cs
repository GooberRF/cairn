using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Cairn.Atx.Ui.Services;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Formats.Imaging;
using Cairn.Ui.Modules;

namespace Cairn.Atx.Ui;

/// <summary>
/// ATX Workbench's old <c>MainViewModel</c> reduced to its ATX-specific members (VBM import, frame
/// commands routed to the active document, pane layout, image caches), under the same member names.
/// The generic members ATX code still calls forward to the shell's <see cref="IShellContext"/>.
/// </summary>
public sealed class AtxWorkspace : ObservableObject
{
    /// <summary>Settings key for whether the problems panel is shown.</summary>
    public const string PanelProblems = "atx.problems";

    /// <summary>Settings key for whether the texture settings pane is expanded.</summary>
    public const string PanelTextureSettings = "atx.textureSettings";

    /// <summary>Settings key for the left pane's width.</summary>
    public const string LayoutLeftPane = "atx.leftPane";

    /// <summary>Settings key for the middle pane's width.</summary>
    public const string LayoutMiddlePane = "atx.middlePane";

    /// <summary>Settings key for the problems panel's height.</summary>
    public const string LayoutProblemsHeight = "atx.problemsHeight";

    /// <summary>Settings key for the texture settings pane's share of the left column.</summary>
    public const string LayoutSettingsShare = "atx.settingsShare";

    private bool _isProblemsVisible;
    private bool _isSettingsExpanded;

    public AtxWorkspace(IShellContext shell)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Dialogs = new AtxDialogs { Owner = shell.MainWindow, ShellDialogs = shell.Dialogs };
        AtxSettings = new AtxSettings(shell.Settings);
        _isProblemsVisible = !Settings.Panels.TryGetValue(PanelProblems, out bool p) || p;
        _isSettingsExpanded = !Settings.Panels.TryGetValue(PanelTextureSettings, out bool e) || e;

        AddFramesFromVppCommand = new RelayCommand(
            () => ActiveDocument?.Frames.AddFramesFromVppCommand.Execute(null),
            () => ActiveDocument is not null);
        BulkTimingCommand = new RelayCommand(
            () => { if (ActiveDocument is { } d) { CommitPendingEdits(); Dialogs.ShowBulkTiming(d); } },
            () => ActiveDocument is not null);
        ImportVbmCommand = new AsyncRelayCommand(ImportVbmAsync);
        ImportVbmFromVppCommand = new AsyncRelayCommand(ImportVbmFromVppAsync, () => ActiveDocument is not null);
        PlayPauseCommand = new RelayCommand(
            () => ActiveDocument?.Preview.PlayPauseCommand.Execute(null),
            () => ActiveDocument is not null);
        ToggleProblemsCommand = new RelayCommand(() => IsProblemsVisible = !IsProblemsVisible);
        SettingsCommand = new RelayCommand(() => Shell.ShowSettings("Animated textures"));
        Shell.ActiveDocumentChanged += (_, _) => { Raise(nameof(ActiveDocument)); RefreshCommands(); };
    }

    /// <summary>The hosting shell.</summary>
    public IShellContext Shell { get; }

    public Dispatcher Dispatcher => Shell.Dispatcher;

    /// <summary>ATX pickers and prompts.</summary>
    public AtxDialogs Dialogs { get; }

    public ThumbnailService Thumbnails { get; } = new();

    public FrameCompositor Compositor { get; } = new();

    /// <summary>The shared settings (game directory, search folders, layout, panels).</summary>
    public AppSettings Settings => Shell.Settings;

    /// <summary>The ATX settings (<c>atx.*</c> keys).</summary>
    public AtxSettings AtxSettings { get; }

    public ThemeService Theme => Shell.Theme;

    /// <summary>The open ATX documents.</summary>
    public IEnumerable<DocumentViewModel> Documents => Shell.Documents.OfType<DocumentViewModel>();

    /// <summary>The active document when it is an ATX one.</summary>
    public DocumentViewModel? ActiveDocument => Shell.ActiveDocument as DocumentViewModel;

    public RelayCommand AddFramesFromVppCommand { get; }

    public RelayCommand BulkTimingCommand { get; }

    /// <summary>File &gt; Import VBM… With a <see cref="VbmImportSource"/> parameter it skips the picker.</summary>
    public AsyncRelayCommand ImportVbmCommand { get; }

    public AsyncRelayCommand ImportVbmFromVppCommand { get; }

    public RelayCommand PlayPauseCommand { get; }

    public RelayCommand ToggleProblemsCommand { get; }

    public RelayCommand SettingsCommand { get; }

    /// <summary>Raised before structural commands so half-typed fields commit first.</summary>
    public event EventHandler? CommitPendingEditsRequested;

    public void CommitPendingEdits() => CommitPendingEditsRequested?.Invoke(this, EventArgs.Empty);

    public void InvalidateImageCaches()
    {
        Thumbnails.Invalidate();
        Compositor.Invalidate();
    }

    public bool IsProblemsVisible
    {
        get => _isProblemsVisible;
        set
        {
            if (!Set(ref _isProblemsVisible, value)) return;
            Settings.Panels[PanelProblems] = value;
            SaveSettings();
        }
    }

    public bool IsSettingsExpanded
    {
        get => _isSettingsExpanded;
        set
        {
            if (!Set(ref _isSettingsExpanded, value)) return;
            Settings.Panels[PanelTextureSettings] = value;
            SaveSettings();
        }
    }

    /// <summary>Adds a search folder and makes every open document see it. True when the list changed.</summary>
    public bool AddSearchFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        if (Settings.SearchFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return false;
        Settings.SearchFolders.Add(folder);
        SaveSettings();
        Shell.Assets.Reconfigure(Settings);
        foreach (var document in Documents) document.RebuildResolver();
        return true;
    }

    public double LayoutSize(string key, double fallback) =>
        Settings.Layout.TryGetValue(key, out double value) && value > 40 ? value : fallback;

    public void SetLayoutSize(string key, double value)
    {
        if (double.IsNaN(value) || value <= 0) return;
        Settings.Layout[key] = Math.Round(value);
    }

    public double LayoutShare(string key, double fallback) =>
        Settings.Layout.TryGetValue(key, out double value) && value is > 0.05 and < 0.95 ? value : fallback;

    public bool HasLayoutShare(string key) =>
        Settings.Layout.TryGetValue(key, out double value) && value is > 0.05 and < 0.95;

    public void SetLayoutShare(string key, double value)
    {
        if (double.IsNaN(value) || value is <= 0.05 or >= 0.95) return;
        Settings.Layout[key] = Math.Round(value, 3);
    }

    public void RefreshCommands()
    {
        AddFramesFromVppCommand.RaiseCanExecuteChanged();
        ImportVbmFromVppCommand.RaiseCanExecuteChanged();
        BulkTimingCommand.RaiseCanExecuteChanged();
        PlayPauseCommand.RaiseCanExecuteChanged();
        Shell.RefreshCommands();
    }

    public bool SaveAs(DocumentViewModel? document) => document is not null && Shell.SaveAs(document);

    /// <summary>Recovery is the shell's (through <see cref="DocumentViewModel.CaptureRecovery"/>): no-op.</summary>
    public void RecordDocumentState(DocumentViewModel document, string text) { }

    /// <summary>Recovery is the shell's: no-op.</summary>
    public void ForgetDocumentState(string id) { }

    public void OnDocumentDirtyChanged(DocumentViewModel document) => RefreshCommands();

    public void OnDocumentDiagnosticsChanged(DocumentViewModel document) => RefreshCommands();

    /// <summary>The caret status item lives on the document now: no-op.</summary>
    public void OnCaretMoved() { }

    public void SaveSettings()
    {
        if (!Shell.IsDiagnosticRun) SettingsStore.Save(Settings);
    }

    private async Task ImportVbmAsync(object? parameter)
    {
        CommitPendingEdits();
        var source = parameter as VbmImportSource;
        if (source is null)
        {
            string? path = Dialogs.OpenVbmFile(ImportBrowseFolder());
            if (path is null) return;
            source = VbmImportSource.FromFile(path);
        }
        await ImportVbmAsync(source).ConfigureAwait(true);
    }

    private async Task ImportVbmFromVppAsync()
    {
        if (ActiveDocument is not { } active) return;
        CommitPendingEdits();
        if (Dialogs.PickVbmFromVpp(active) is not { } picked) return;
        await ImportVbmAsync(VbmImportSource.FromArchive(picked.ArchivePath, picked.EntryName)).ConfigureAwait(true);
    }

    /// <summary>Loads <paramref name="source"/>, shows the import dialog and opens the generated .atx.</summary>
    public async Task ImportVbmAsync(VbmImportSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var load = await Task.Run(() => VbmImportLoader.Load(source)).ConfigureAwait(true);
        if (!load.Ok)
        {
            Dialogs.ShowError("That VBM could not be imported.",
                load.Error ?? $"'{source.Name}' could not be read.", load.Details);
            return;
        }
        string? generated = Dialogs.ShowVbmImport(this, source, load.Bytes!, load.Info!);
        if (generated is null) return;
        RememberImportFolder(generated);
        Shell.OpenFile(generated);
    }

    /// <summary>
    /// The import for bytes already in memory (another module's "Convert to ATX..."): the dialog, or with
    /// <paramref name="interactive"/> false the defaults written into <paramref name="outputFolder"/>; the generated .atx
    /// opens in a tab. Returns its path, or null when cancelled or failed (reported).
    /// </summary>
    public async Task<string?> ConvertVbmAsync(VbmImportSource source, byte[] bytes, bool interactive, string? outputFolder)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(bytes);
        CommitPendingEdits();
        VbmInfo info;
        try { info = VbmCodec.ReadInfo(bytes, source.Name); }
        catch (ImageDecodeException ex)
        {
            Dialogs.ShowError("That VBM could not be converted.", ex.Message);
            return null;
        }
        string? generated;
        if (interactive)
        {
            generated = Dialogs.ShowVbmImport(this, source, bytes, info);
        }
        else
        {
            using var model = new VbmImportViewModel(this, source, bytes, info);
            if (outputFolder is not null) model.OutputFolder = outputFolder;
            model.Pause();
            await model.RunImportAsync().ConfigureAwait(true);
            generated = model.ImportedPath;
        }
        if (generated is null) return null;
        if (interactive) RememberImportFolder(generated);
        Shell.OpenFile(generated);
        return generated;
    }

    private string? ImportBrowseFolder()
    {
        if (AtxSettings.LastImportFolder is { Length: > 0 } last && Directory.Exists(last)) return last;
        return ActiveDocument?.AtxFolder;
    }

    private void RememberImportFolder(string generatedPath)
    {
        try
        {
            string? folder = Path.GetDirectoryName(Path.GetFullPath(generatedPath));
            if (string.IsNullOrEmpty(folder)) return;
            AtxSettings.LastImportFolder = folder;
            SaveSettings();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
            or PathTooLongException or System.Security.SecurityException)
        {
            ErrorLog.Write("remember import folder", ex);
        }
    }
}
