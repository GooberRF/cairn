using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.Views.Dialogs;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>A closed tab, kept so Ctrl+Shift+T can bring it back with its unsaved work.</summary>
public sealed record ClosedTab(string? Path, AssetLocation? ArchiveOrigin, string DisplayName, DocumentKind Kind, byte[] Data, bool WasDirty);

/// <summary>What a document last held, recorded on the UI thread for a crash on another thread.</summary>
public sealed record DocumentState(string? Path, string DisplayName, string Kind, Func<byte[]> Serialize, bool IsDirty);

/// <summary>
/// The shell: open documents and the active one, the commands every menu and toolbar binds to, the
/// global panels (library, bottom panel), settings and layout, the recovery store, and the status bar.
/// </summary>
public sealed partial class RfaWorkspace : ObservableObject
{
    /// <summary>How often dirty documents are copied to the recovery folder.</summary>
    private const int RecoveryIntervalSeconds = 30;

    public const string PanelLibrary = "library";
    public const string PanelInspector = "inspector";
    public const string PanelBottom = "bottom";
    public const string LayoutLibraryWidth = "libraryWidth";
    public const string LayoutInspectorWidth = "inspectorWidth";
    public const string LayoutBottomHeight = "bottomHeight";

    private readonly Stack<ClosedTab> _closedTabs = new();
    private readonly HashSet<string> _errorSaveConfirmed = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _recoveryTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly ConcurrentDictionary<string, DocumentState> _states = new();
    private DocumentViewModel? _active;
    private bool _isLibraryVisible;
    private bool _isInspectorVisible;
    private bool _isBottomVisible;
    private PanelTab? _selectedBottomTab;
    private string? _shellStatus;
    private bool _saveCancelled;

    /// <param name="dispatcher">The UI dispatcher.</param>
    /// <param name="dialogs">File pickers and prompts.</param>
    /// <param name="settings">Settings loaded from disk.</param>
    /// <param name="theme">The theme service.</param>
    /// <param name="isDiagnosticRun">True for <c>--screenshot</c>: never write settings, recent files or recovery.</param>
    public RfaWorkspace(Dispatcher dispatcher, IDialogService dialogs, AppSettings settings, ThemeService theme, bool isDiagnosticRun = false)
    {
        Dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Theme = theme ?? throw new ArgumentNullException(nameof(theme));
        IsDiagnosticRun = isDiagnosticRun;
        Recent = new RecentFilesList(settings.RecentFiles, settings.MaxRecentFiles);
        Recovery = new RecoveryStore();
        Assets = new AssetServices(dispatcher);
        Textures.WaitForIndex = () => Assets.ArchivesIndexed;
        Display.Load(settings);
        Display.Changed += (_, _) =>
        {
            Display.Store(Settings);
            SaveSettingsSoon();
        };
        _timeUnit = Settings.Get("rfa.timeUnit", TimeUnit.Frames);

        _recoveryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(RecoveryIntervalSeconds),
        };
        _recoveryTimer.Tick += (_, _) => SnapshotDirtyDocuments();
        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(6) };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            ShellStatus = null;
        };

        _isLibraryVisible = !settings.Panels.TryGetValue(PanelLibrary, out bool lib) || lib;
        _isInspectorVisible = !settings.Panels.TryGetValue(PanelInspector, out bool insp) || insp;
        _isBottomVisible = !settings.Panels.TryGetValue(PanelBottom, out bool bottom) || bottom;

        BottomTabs.Add(new PanelTab("timeline", "Timeline", "The dope sheet: every key of every bone over time (clip documents)",
            d => (d as ClipDocumentViewModel)?.Timeline)
        {
            EmptyText = "The timeline shows a clip's keys. Open a clip (.rfa), or preview a clip on this mesh and open it from the library.",
        });
        BottomTabs.Add(new PanelTab("problems", "Problems", "Problems the linter found in the active document", d => d?.Problems)
        {
            EmptyText = "Open a clip or mesh to see its problems here.",
        });
        BottomTabs.Add(new PanelTab("tables", "Table usage", "Which table lines play the active clip, or which clips the tables give the active mesh",
            TableUsageViewModel.For)
        {
            EmptyText = "Open a clip or mesh to see which lines of the game's tables use it.",
        });
        _selectedBottomTab = BottomTabs[0];

        OpenCommand = new RelayCommand(OpenDocuments);
        OpenRecentCommand = new RelayCommand(p => OpenFile(p as string), p => p is string);
        ClearRecentCommand = new RelayCommand(() => { Recent.Clear(); RefreshRecent(); SaveSettings(); }, () => HasRecentFiles);
        // A read-only document can still be saved when it was never written (an imported static mesh): Save asks where.
        SaveCommand = new RelayCommand(() => Save(_active), () => _active is { IsReadOnly: false } or { FilePath: null, ArchiveOrigin: null, IsDirty: true });
        SaveAsCommand = new RelayCommand(() => SaveAs(_active), () => _active is not null);
        SaveAllCommand = new RelayCommand(SaveAll, () => Documents.Any(d => d.IsDirty));
        CloseTabCommand = new RelayCommand(p => CloseDocument(p as DocumentViewModel ?? _active),
            p => (p as DocumentViewModel ?? _active) is not null);
        CloseAllCommand = new RelayCommand(CloseAll, () => Documents.Count > 0);
        ReopenClosedTabCommand = new RelayCommand(ReopenClosedTab, () => _closedTabs.Count > 0);
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty));
        UndoCommand = new RelayCommand(() => { CommitPendingEdits(); _active?.Undo(); }, () => _active?.CanUndo == true);
        RedoCommand = new RelayCommand(() => { CommitPendingEdits(); _active?.Redo(); }, () => _active?.CanRedo == true);
        ToggleLibraryCommand = new RelayCommand(() => IsLibraryVisible = !IsLibraryVisible);
        ToggleInspectorCommand = new RelayCommand(() => IsInspectorVisible = !IsInspectorVisible);
        ToggleBottomCommand = new RelayCommand(() => IsBottomVisible = !IsBottomVisible);
        ShowProblemsCommand = new RelayCommand(() =>
        {
            if (IsBottomVisible && SelectedBottomTab?.Id == "problems") IsBottomVisible = false;
            else
            {
                IsBottomVisible = true;
                SelectedBottomTab = BottomTabs.FirstOrDefault(t => t.Id == "problems");
            }
        });
        SetThemeCommand = new RelayCommand(p =>
        {
            if (p is not AppTheme requested && !Enum.TryParse(p as string, out requested)) return;
            Settings.Theme = requested;
            Theme.Apply(requested);
            RaiseThemeFlags();
            SaveSettings();
        });
        SetTimeUnitCommand = new RelayCommand(p =>
        {
            if (p is TimeUnit unit || Enum.TryParse(p as string, out unit)) TimeUnit = unit;
        });
        PlayPauseCommand = new RelayCommand(() => _active?.Playback.TogglePlay(), () => _active?.Playback.HasClip == true);
        FrameCommand = new RelayCommand(() => _active?.Scene.RequestFrame(_active.Selection.Count > 0), () => _active is not null);
        SettingsCommand = new RelayCommand(() => { CommitPendingEdits(); Dialogs.ShowSettings(this); });
        FormatReferenceCommand = new RelayCommand(Dialogs.ShowFormatReference);
        ShortcutsCommand = new RelayCommand(Dialogs.ShowShortcuts);
        AboutCommand = new RelayCommand(Dialogs.ShowAbout);
        AssociateFilesCommand = new RelayCommand(ToggleFileAssociation, () => !IsDiagnosticRun);
        RefreshLibraryCommand = new RelayCommand(() => Assets.Refresh(), () => Assets.HasSources && !Assets.IsLoading);

        Library = new LibraryViewModel(this);
        ClipTools = new ClipTools.ClipToolCommands(this);
        Assets.LibraryChanged += (_, _) =>
        {
            foreach (var d in Documents) d.OnLibraryChanged();
        };
        Assets.UsageChanged += (_, _) =>
        {
            foreach (var d in Documents) d.OnLibraryChanged();
        };
        Assets.ProgressChanged += (_, _) =>
        {
            RaiseAll(nameof(LibraryStatusText), nameof(IsLibraryLoading), nameof(StatusMessage));
            RefreshLibraryCommand.RaiseCanExecuteChanged();
        };

        RefreshRecent();
        StartGameDirectoryDetection();
    }

    public Dispatcher Dispatcher { get; }

    public IDialogService Dialogs { get; }

    public AppSettings Settings { get; }

    public ThemeService Theme { get; }

    public RecentFilesList Recent { get; }

    public RecoveryStore Recovery { get; }

    /// <summary>Resolver, library and tables for the current settings.</summary>
    public AssetServices Assets { get; }

    /// <summary>Decoded textures, shared by every viewport.</summary>
    public TextureService Textures { get; } = new();

    /// <summary>The viewport display toggles (shared and persisted).</summary>
    public ViewportDisplaySettings Display { get; } = new();

    /// <summary>The Library panel.</summary>
    public LibraryViewModel Library { get; }

    /// <summary>The Clip menu's tool commands (phase 5 clip tools and Compare With…).</summary>
    public ClipTools.ClipToolCommands ClipTools { get; }

    /// <summary>True for a <c>--screenshot</c> run: settings, recent files and recovery are left alone.</summary>
    public bool IsDiagnosticRun { get; }

    /// <summary>Open documents, in tab order.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>Recent files, newest first.</summary>
    public ObservableCollection<string> RecentFiles { get; } = [];

    /// <summary>The bottom panel's tabs (Problems now; Timeline and Table usage in phase 5).</summary>
    public ObservableCollection<PanelTab> BottomTabs { get; } = [];

    public PanelTab? SelectedBottomTab
    {
        get => _selectedBottomTab;
        set
        {
            if (value is not null) SelectShellPanel(value.Id);
            if (!Set(ref _selectedBottomTab, value)) return;
            if (_active is not null && value is not null) _bottomTabByDocument.AddOrUpdate(_active, value.Id);
        }
    }

    /// <summary>The bottom tab each document last showed (the Timeline by default for clips, Problems for meshes).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocumentViewModel, string> _bottomTabByDocument = [];

    private void SelectBottomTabFor(DocumentViewModel? document)
    {
        if (document is null) return;
        string id = _bottomTabByDocument.TryGetValue(document, out string? remembered) ? remembered
            : document is ClipDocumentViewModel ? "timeline" : "problems";
        var tab = BottomTabs.FirstOrDefault(t => t.Id == id) ?? BottomTabs.FirstOrDefault();
        if (!ReferenceEquals(tab, _selectedBottomTab))
        {
            _selectedBottomTab = tab;
            Raise(nameof(SelectedBottomTab));
        }
    }

    /// <summary>Raised when the app should close itself (File &gt; Exit).</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Raised before anything reads document state, so the window can commit a half-typed value.</summary>
    public event EventHandler? CommitPendingEditsRequested;

    /// <summary>The tab in front, or null when the welcome surface shows.</summary>
    public DocumentViewModel? ActiveDocument
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value)) return;
            // Inside the shell the tab in front is the shell's: ask it, and follow its ActiveDocumentChanged.
            if (Host is not null && value is not null && !ReferenceEquals(Host.ActiveDocument, value))
            {
                Host.Activate(value);
                return;
            }
            CommitPendingEdits();
            _active?.Playback.Pause();
            if (!Set(ref _active, value)) return;
            foreach (var tab in BottomTabs) tab.Update(value);
            SelectBottomTabFor(value);
            RaiseStatus();
            RaiseAll(nameof(HasDocument), nameof(WindowTitle), nameof(UndoHeader), nameof(RedoHeader), nameof(UndoToolTip), nameof(RedoToolTip),
                nameof(IsClipDocument), nameof(IsSkeletalMeshDocument));
            RefreshCommands();
            Library.OnActiveDocumentChanged();
        }
    }

    public bool HasDocument => Documents.Count > 0;

    public bool IsClipDocument => _active is ClipDocumentViewModel;

    public string WindowTitle => _active is null ? "RFA Workbench" : $"{_active.TabHeader} — RFA Workbench";

    /// <summary>"_Undo Set ramp in".</summary>
    public string UndoHeader => _active?.UndoLabel is { Length: > 0 } label && _active.CanUndo ? $"_Undo {label}" : "_Undo";

    public string RedoHeader => _active?.RedoLabel is { Length: > 0 } label && _active.CanRedo ? $"_Redo {label}" : "_Redo";

    /// <summary>The toolbar's Undo tooltip (a tooltip shows no access keys, so no underscore), with its shortcut.</summary>
    public string UndoToolTip => UndoHeader.TrimStart('_') + " (Ctrl+Z)";

    /// <summary>The toolbar's Redo tooltip, with its shortcut.</summary>
    public string RedoToolTip => RedoHeader.TrimStart('_') + " (Ctrl+Y)";

    /// <summary>True when the tab in front is a mesh with bones (Mesh › Choose Preview Clip).</summary>
    public bool IsSkeletalMeshDocument => _active is MeshDocumentViewModel { HasSkeleton: true };

    // ── Time unit ────────────────────────────────────────────────────────────

    private TimeUnit _timeUnit;

    /// <summary>How times are displayed (frames, seconds, ticks); a global, persisted choice.</summary>
    public TimeUnit TimeUnit
    {
        get => _timeUnit;
        set
        {
            if (!Set(ref _timeUnit, value)) return;
            Settings.Set("rfa.timeUnit", value);
            SaveSettingsSoon();
            foreach (var d in Documents)
            {
                d.Playback.RefreshText();
                if (d is ClipDocumentViewModel clip) clip.OnTimeUnitChanged();
                d.Problems.Refresh();
            }
            RaiseAll(nameof(IsUnitFrames), nameof(IsUnitSeconds), nameof(IsUnitTicks));
            RaiseStatus();
        }
    }

    public bool IsUnitFrames => _timeUnit == TimeUnit.Frames;

    public bool IsUnitSeconds => _timeUnit == TimeUnit.Seconds;

    public bool IsUnitTicks => _timeUnit == TimeUnit.Ticks;

    /// <summary>Frames → seconds → ticks → frames (the transport readout's click).</summary>
    public void CycleTimeUnit() => TimeUnit = (TimeUnit)(((int)_timeUnit + 1) % 3);

    // ── Layout ───────────────────────────────────────────────────────────────

    public bool IsLibraryVisible
    {
        get => _isLibraryVisible;
        set
        {
            if (!Set(ref _isLibraryVisible, value)) return;
            Settings.Panels[PanelLibrary] = value;
            SaveSettingsSoon();
        }
    }

    public bool IsInspectorVisible
    {
        get => _isInspectorVisible;
        set
        {
            if (!Set(ref _isInspectorVisible, value)) return;
            Settings.Panels[PanelInspector] = value;
            SaveSettingsSoon();
        }
    }

    public bool IsBottomVisible
    {
        get => _isBottomVisible;
        set
        {
            if (!Set(ref _isBottomVisible, value)) return;
            Settings.Panels[PanelBottom] = value;
            SaveSettingsSoon();
        }
    }

    /// <summary>Reads a persisted size, falling back to <paramref name="fallback"/>.</summary>
    public double LayoutSize(string key, double fallback) =>
        Settings.Layout.TryGetValue(key, out double value) && value > 40 ? value : fallback;

    /// <summary>Records a size for the next run.</summary>
    public void SetLayoutSize(string key, double value)
    {
        if (double.IsNaN(value) || value <= 40) return;
        Settings.Layout[key] = Math.Round(value);
    }

    public void RaiseThemeFlags() => RaiseAll(nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark));

    public bool IsThemeSystem => Settings.Theme == AppTheme.System;

    public bool IsThemeLight => Settings.Theme == AppTheme.Light;

    public bool IsThemeDark => Settings.Theme == AppTheme.Dark;

    // ── Status bar ───────────────────────────────────────────────────────────

    public int ErrorCount => _active?.ErrorCount ?? 0;

    public int WarningCount => _active?.WarningCount ?? 0;

    public string StatusBonesText => _active?.StatusBonesText ?? string.Empty;

    public string StatusDurationText => _active?.StatusDurationText ?? string.Empty;

    public string StatusTimeText => _active?.Playback.HasClip == true ? _active.Playback.TimeText : string.Empty;

    public string StatusSpeedText => _active?.Playback.HasClip == true ? _active.Playback.SpeedText : string.Empty;

    /// <summary>The transient message: the active document's, else the shell's, else the library's progress.</summary>
    public string StatusMessage => _active?.StatusMessage ?? _shellStatus ?? (Assets.IsLoading ? Assets.ProgressText : string.Empty);

    public string LibraryStatusText => Assets.ProgressText;

    public bool IsLibraryLoading => Assets.IsLoading;

    private string? ShellStatus
    {
        get => _shellStatus;
        set
        {
            if (Set(ref _shellStatus, value)) Raise(nameof(StatusMessage));
        }
    }

    /// <summary>Shows a message in the status bar for a few seconds.</summary>
    public void ShowShellStatus(string message)
    {
        ShellStatus = message;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void RaiseStatus() => RaiseAll(nameof(ErrorCount), nameof(WarningCount), nameof(StatusBonesText),
        nameof(StatusDurationText), nameof(StatusTimeText), nameof(StatusSpeedText), nameof(StatusMessage));

    // ── Commands ─────────────────────────────────────────────────────────────

    public RelayCommand OpenCommand { get; }
    public RelayCommand OpenRecentCommand { get; }
    public RelayCommand ClearRecentCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand SaveAsCommand { get; }
    public RelayCommand SaveAllCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand CloseAllCommand { get; }
    public RelayCommand ReopenClosedTabCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand ToggleLibraryCommand { get; }
    public RelayCommand ToggleInspectorCommand { get; }
    public RelayCommand ToggleBottomCommand { get; }
    public RelayCommand ShowProblemsCommand { get; }
    public RelayCommand SetThemeCommand { get; }
    public RelayCommand SetTimeUnitCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand FrameCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand FormatReferenceCommand { get; }
    public RelayCommand ShortcutsCommand { get; }
    public RelayCommand AboutCommand { get; }
    public RelayCommand AssociateFilesCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }

    // File association is shell-owned in Cairn (to be removed when RfaWorkspace is reduced).
    public string AssociateMenuHeader => string.Empty;

    public string AssociateMenuToolTip => string.Empty;

    private void ToggleFileAssociation() => RfaUi.Shell?.ShowSettings();

    /// <summary>Re-queries every command's enabled state.</summary>
    public void RefreshCommands()
    {
        SaveCommand.RaiseCanExecuteChanged();
        SaveAsCommand.RaiseCanExecuteChanged();
        SaveAllCommand.RaiseCanExecuteChanged();
        CloseTabCommand.RaiseCanExecuteChanged();
        CloseAllCommand.RaiseCanExecuteChanged();
        ReopenClosedTabCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
        PlayPauseCommand.RaiseCanExecuteChanged();
        FrameCommand.RaiseCanExecuteChanged();
        ClearRecentCommand.RaiseCanExecuteChanged();
    }

    // ── Settings-dependent services ──────────────────────────────────────────

    /// <summary>Applies the asset settings (startup, after Settings OK, after detection) and rebuilds the library.</summary>
    public void ApplyAssetSettings()
    {
        Textures.Clear();
        Assets.Reconfigure(Settings, useCacheFile: true, Host?.Assets.Resolver);
        foreach (var d in Documents) d.OnAssetsChanged();
    }

    /// <summary>On first run, finds Red Faction and adopts it, off the UI thread.</summary>
    private void StartGameDirectoryDetection()
    {
        if (!string.IsNullOrWhiteSpace(Settings.GameDirectory)) return;
        var busy = BusyTracker.Begin("game directory detection");
        _ = Task.Run(GameDirectoryLocator.DetectDetailed).ContinueWith(
            task => Dispatcher.BeginInvoke(new Action(() =>
            {
                busy.Dispose();
                if (!task.IsCompletedSuccessfully || task.Result is not { } found) return;
                if (!string.IsNullOrWhiteSpace(Settings.GameDirectory)) return;
                Settings.GameDirectory = found.Directory;
                Settings.Set("gameDirectoryFoundVia", found.SourceDescription);
                SaveSettings();
                ApplyAssetSettings();
                ShowShellStatus($"Found Red Faction in {found.Directory} ({found.Hint.TrimEnd('.')}).");
            })),
            TaskScheduler.Default);
    }

    /// <summary>The library's empty-state button: pick the game directory.</summary>
    public void PickGameDirectory()
    {
        string? chosen = Dialogs.PickFolder(Settings.GameDirectory, "Choose the Red Faction folder (the one holding RF.exe)");
        if (chosen is null) return;
        Settings.GameDirectory = chosen;
        SaveSettings();
        ApplyAssetSettings();
    }

    /// <summary>The library's empty-state button: add a search folder.</summary>
    public void PickSearchFolder()
    {
        string? chosen = Dialogs.PickFolder(Settings.SearchFolders.LastOrDefault(), "Add a folder to search for clips, meshes, tables and textures");
        if (chosen is null) return;
        if (!Settings.SearchFolders.Contains(chosen, StringComparer.OrdinalIgnoreCase)) Settings.SearchFolders.Add(chosen);
        SaveSettings();
        ApplyAssetSettings();
    }

    // ── Preview partner memory ───────────────────────────────────────────────

    private const string PreviewByClipKey = "rfa.previewMeshByClip";
    private const string PreviewByBonesKey = "rfa.previewMeshByBones";

    /// <summary>The preview mesh the user picked for this clip before, or null.</summary>
    public string? RememberedPreviewMesh(string? clipPath)
    {
        if (string.IsNullOrWhiteSpace(clipPath)) return null;
        var map = Settings.Get<Dictionary<string, string>>(PreviewByClipKey);
        return map is not null && map.TryGetValue(clipPath.ToLowerInvariant(), out string? mesh) ? mesh : null;
    }

    /// <summary>The mesh last used for clips with this bone count, or null.</summary>
    public string? LastPreviewMeshFor(int boneCount)
    {
        var map = Settings.Get<Dictionary<string, string>>(PreviewByBonesKey);
        return map is not null && map.TryGetValue(boneCount.ToString(System.Globalization.CultureInfo.InvariantCulture), out string? mesh) ? mesh : null;
    }

    /// <summary>Remembers the user's preview mesh for a clip (and for its bone count).</summary>
    public void RememberPreviewMesh(string? clipPath, int boneCount, string meshName)
    {
        if (!string.IsNullOrWhiteSpace(clipPath))
        {
            var map = Settings.Get<Dictionary<string, string>>(PreviewByClipKey) ?? [];
            map[clipPath.ToLowerInvariant()] = meshName;
            // Bounded: a user who previews thousands of clips should not grow the settings file forever.
            if (map.Count > 500) map.Remove(map.Keys.First());
            Settings.Set(PreviewByClipKey, map);
        }
        RememberLastPreviewMesh(boneCount, meshName);
    }

    /// <summary>Remembers the last mesh used for a bone count.</summary>
    public void RememberLastPreviewMesh(int boneCount, string meshName)
    {
        var bones = Settings.Get<Dictionary<string, string>>(PreviewByBonesKey) ?? [];
        bones[boneCount.ToString(System.Globalization.CultureInfo.InvariantCulture)] = meshName;
        Settings.Set(PreviewByBonesKey, bones);
        SaveSettingsSoon();
    }

    // ── Opening ──────────────────────────────────────────────────────────────

    /// <summary>The extensions this app opens.</summary>
    public static bool IsOpenable(string path) =>
        path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase)
        || Formats.Legacy.LegacyMeshSupport.IsLegacyName(path);

    private void OpenDocuments()
    {
        string? folder = _active?.Folder ?? (Recent.Items.Count > 0 ? Path.GetDirectoryName(Recent.Items[0]) : null);
        string[] paths = Dialogs.OpenDocuments(folder);
        if (paths.Length > 0) OpenFiles(paths);
    }

    private sealed record OpenFailure(string Path, string Reason, string? Details);

    /// <summary>Opens a file (or brings its tab forward). Failures are reported.</summary>
    public DocumentViewModel? OpenFile(string? path)
    {
        var document = OpenFile(path, out var failure);
        if (failure is not null) Report([failure]);
        return document;
    }

    /// <summary>Opens several files; failures are reported together.</summary>
    public void OpenFiles(IEnumerable<string> paths)
    {
        DocumentViewModel? first = null;
        var failures = new List<OpenFailure>();
        foreach (string path in paths)
        {
            var opened = OpenFile(path, out var failure);
            if (failure is not null) failures.Add(failure);
            first ??= opened;
        }
        if (first is not null) ActiveDocument = first;
        Report(failures);
    }

    private DocumentViewModel? OpenFile(string? path, out OpenFailure? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            failure = new OpenFailure(path, "is not a usable file path.", ex.Message);
            return null;
        }

        if (DocumentAt(full) is { } existing)
        {
            ActiveDocument = existing;
            return existing;
        }
        if (Directory.Exists(full))
        {
            failure = new OpenFailure(full, "is a folder, not a clip or mesh.", null);
            return null;
        }
        if (!File.Exists(full))
        {
            failure = new OpenFailure(full, "does not exist.", null);
            Recent.Remove(full);
            RefreshRecent();
            return null;
        }
        if (!IsOpenable(full))
        {
            failure = new OpenFailure(full, "is not an .rfa, .v3c, .v3m, .gltf or .glb file.", null);
            return null;
        }
        if (IsGltf(full))
        {
            // A glTF file is not a document: it is imported (animation and/or mesh) through its dialog.
            Gltf.ImportFile(full);
            return null;
        }

        DocumentViewModel document;
        try
        {
            byte[] bytes = AtomicFile.ReadAllBytes(full);
            document = CreateDocument(bytes, Path.GetFileName(full), full, null);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            if (ex is not (IOException or UnauthorizedAccessException or AssetFormatException or NotSupportedException or ArgumentException))
                ErrorLog.Write("open " + full, ex);
            failure = new OpenFailure(full, ex is IOException or UnauthorizedAccessException ? "could not be read." : "is not a valid file: " + ex.Message, ex.Message);
            return null;
        }
        Add(document);
        if (!IsDiagnosticRun)
        {
            Recent.Add(full);
            RefreshRecent();
            SaveSettings();
        }
        return document;
    }

    /// <summary>
    /// Opens a library entry: a loose file normally, an archive entry as a read-only-origin document.
    /// <paramref name="opened"/> runs on a NEW tab once it exists (not when an open tab is brought forward).
    /// </summary>
    public void OpenLocation(AssetLocation location, string name, Action<DocumentViewModel>? opened = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (location.FilePath is { } path)
        {
            bool wasOpen = DocumentAt(SafeFullPath(path)) is not null;
            if (OpenFile(path) is { } document && !wasOpen) opened?.Invoke(document);
            return;
        }
        var existing = Documents.FirstOrDefault(d => d.FilePath is null && d.ArchiveOrigin is { } a
            && string.Equals(a.ArchivePath, location.ArchivePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.ResolvedName, location.ResolvedName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActiveDocument = existing;
            return;
        }
        // A second double-click while the entry is still being read must not open a second tab.
        string key = location.ArchivePath + "|" + location.ResolvedName;
        if (!_archiveOpensInFlight.Add(key)) return;
        var busy = BusyTracker.Begin("open " + name);
        _ = Task.Run(location.ReadAllBytes).ContinueWith(task => Dispatcher.BeginInvoke(new Action(() =>
        {
            busy.Dispose();
            _archiveOpensInFlight.Remove(key);
            if (!task.IsCompletedSuccessfully)
            {
                Dialogs.ShowError("That file could not be opened.", $"'{name}' in {location.DisplayLocation} could not be read.",
                    task.Exception?.InnerException?.Message);
                return;
            }
            try
            {
                var document = CreateDocument(task.Result, name, null, location);
                Add(document);
                opened?.Invoke(document);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Dialogs.ShowError("That file could not be opened.", $"'{name}' in {location.DisplayLocation} is not valid.", ex.Message);
            }
        })), TaskScheduler.Default);
    }

    private readonly HashSet<string> _archiveOpensInFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A document holding work that was never saved (a recovery snapshot, a closed dirty tab). Its history
    /// starts from what is really on disk (the file, or the archive entry) with the work as one unsaved step
    /// on top, so undo goes back to the saved state and the tab cannot look clean while it differs from the
    /// disk. When the disk copy cannot be read, the work alone is opened and marked as never saved.
    /// </summary>
    private DocumentViewModel CreateRestoredDocument(byte[] data, string name, string? path, AssetLocation? origin, bool wasDirty)
    {
        bool onDisk = path is not null && File.Exists(path);
        if (!wasDirty)
        {
            var clean = CreateDocument(data, name, path, origin);
            // Its file is gone: the tab holds the only copy, so it must not close silently.
            if (path is not null && !onDisk) clean.MarkAsNew();
            return clean;
        }
        byte[]? saved = null;
        try
        {
            if (onDisk) saved = AtomicFile.ReadAllBytes(path!);
            else if (origin is not null) saved = origin.ReadAllBytes();
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            saved = null;
        }
        if (saved is not null)
        {
            try
            {
                var document = CreateDocument(saved, name, path, origin);
                document.RestoreBytes(data);
                return document;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                // The disk copy is not readable any more: fall through to the work on its own.
            }
        }
        var only = CreateDocument(data, name, path, origin);
        only.MarkAsNew();
        return only;
    }

    /// <summary>
    /// True for any failure reading or parsing a file the user handed us (bad bytes can surface as more than
    /// <see cref="AssetFormatException"/> when a reader has a bug): reported as "could not be read", never as a crash.
    /// </summary>
    public static bool IsReadFailure(Exception ex) =>
        ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException or ThreadAbortException);

    private DocumentViewModel CreateDocument(byte[] bytes, string name, string? path, AssetLocation? origin)
    {
        if (name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            return new ClipDocumentViewModel(this, RfaReader.Read(bytes, name), name, path, origin);
        if (IsLegacyMesh(bytes, name)) return CreateLegacyDocument(bytes, name, path, origin);
        return new MeshDocumentViewModel(this, V3dReader.Read(bytes, name), name, path, origin);
    }

    private void Report(IReadOnlyList<OpenFailure> failures)
    {
        if (failures.Count == 0) return;
        if (failures.Count == 1)
        {
            var only = failures[0];
            Dialogs.ShowError("That file could not be opened.", $"'{only.Path}' {only.Reason}", only.Details);
            return;
        }
        string body = string.Join(Environment.NewLine, failures.Select(f => $"'{f.Path}' {f.Reason}"));
        string details = string.Join(Environment.NewLine, failures.Where(f => f.Details is { Length: > 0 }).Select(f => $"{f.Path}: {f.Details}"));
        Dialogs.ShowError($"{failures.Count} files could not be opened.", body, details.Length == 0 ? null : details);
    }

    /// <summary>The open document whose file is <paramref name="path"/>, or null.</summary>
    public DocumentViewModel? DocumentAt(string? path) =>
        path is null ? null : Documents.FirstOrDefault(d => SamePath(d.FilePath, path));

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static bool SamePath(string? a, string? b)
    {
        if (a is null || b is null) return false;
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Adds a document as a new tab and makes it active.</summary>
    public void Add(DocumentViewModel document)
    {
        if (Host is not null)
        {
            // The shell owns the tabs: it raises DocumentsChanged (Track) and ActiveDocumentChanged.
            Host.AddDocument(document);
            return;
        }
        Track(document);
        ActiveDocument = document;
    }

    /// <summary>Starts following a document that became a tab (status, recovery state).</summary>
    private void Track(DocumentViewModel document)
    {
        document.Playback.PropertyChanged += (_, e) =>
        {
            if (!ReferenceEquals(document, _active)) return;
            if (e.PropertyName is nameof(PlaybackViewModel.TimeText) or nameof(PlaybackViewModel.SpeedText))
                OnActiveTimeChanged();
            else if (e.PropertyName == nameof(PlaybackViewModel.HasClip))
                OnDocumentStatusChanged(document);
        };
        Documents.Add(document);
        Record(document);
        Raise(nameof(HasDocument));
        RefreshCommands();
    }

    // ── Saving ───────────────────────────────────────────────────────────────

    /// <summary>Saves a document; one from a .vpp (or never saved) goes through Save As. Returns false when cancelled or failed.</summary>
    public bool Save(DocumentViewModel? document)
    {
        if (document is null) return false;
        if (Host is not null) return Host.Save(document);
        CommitPendingEdits();
        if (document.IsReadOnly) return SaveAs(document);
        if (document.FilePath is null) return SaveAs(document);
        return Write(document, document.FilePath);
    }

    /// <summary>Saves a document under a new name. Returns false when cancelled.</summary>
    public bool SaveAs(DocumentViewModel? document)
    {
        if (document is null) return false;
        if (Host is not null) return Host.SaveAs(document);
        CommitPendingEdits();
        // A document from inside a .vpp has no folder of its own: offer the last folder written to,
        // and never the game directory (it holds the game's own files).
        // A folder is always given: with none the Windows dialog falls back to the last folder it saw,
        // which is often the game directory the stock file came from.
        string? folder = document.Folder;
        if (folder is null || IsGameDirectory(folder)) folder = DefaultOutputFolder();
        string? chosen = Dialogs.SaveDocument(folder, document.DisplayName, document.Extension);
        if (chosen is null)
        {
            _saveCancelled = true;
            return false;
        }
        bool ok = Write(document, chosen);
        if (ok && !IsDiagnosticRun && !IsGameDirectory(Path.GetDirectoryName(chosen)))
        {
            Settings.LastSaveFolder = Path.GetDirectoryName(chosen);
            SaveSettings();
        }
        return ok;
    }

    private void SaveAll()
    {
        CommitPendingEdits();
        var failed = new List<string>();
        foreach (var document in Documents.Where(d => d.IsDirty).ToList())
        {
            _saveCancelled = false;
            if (Save(document)) continue;
            if (_saveCancelled) return;
            failed.Add(document.DisplayName);
        }
        _saveCancelled = false;
        if (failed.Count == 0) return;
        Dialogs.ShowError(
            failed.Count == 1 ? "One file could not be saved." : $"{failed.Count} files could not be saved.",
            "Everything else was saved. These are still only in their tabs:\n" + string.Join("\n", failed));
    }

    private bool Write(DocumentViewModel document, string path)
    {
        var holder = Documents.FirstOrDefault(d => !ReferenceEquals(d, document) && SamePath(d.FilePath, path));
        if (holder is not null)
        {
            if (Dialogs.Confirm($"'{Path.GetFileName(path)}' is already open in another tab.",
                    "Two tabs on the same file would overwrite each other. Switch to the tab that already has it, or save this one under a different name.",
                    "Switch to that tab"))
            {
                ActiveDocument = holder;
            }
            return false;
        }

        bool confirmedNow = false;
        if (document.ErrorCount > 0 && !_errorSaveConfirmed.Contains(document.Id))
        {
            if (!Dialogs.ConfirmSaveWithErrors(document.DisplayName, document.ErrorCount))
            {
                _saveCancelled = true;
                return false;
            }
            confirmedNow = true;
        }

        byte[] bytes;
        try
        {
            bytes = document.Serialize();
        }
        catch (ArgumentException ex)
        {
            Dialogs.ShowError("That file cannot be written yet.", $"'{document.DisplayName}' breaks a limit of the file format.", ex.Message);
            return false;
        }

        document.SuspendFileWatch(true);
        try
        {
            AtomicFile.WriteAllBytes(path, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Dialogs.ShowError("That file could not be saved.", $"'{Path.GetFileName(path)}' could not be written.", ex.Message);
            return false;
        }
        finally
        {
            document.SuspendFileWatch(false);
        }

        if (confirmedNow) _errorSaveConfirmed.Add(document.Id);
        document.MarkSaved(path);
        Recovery.Discard(document.Id);
        if (!IsDiagnosticRun)
        {
            Recent.Add(path);
            RefreshRecent();
            SaveSettings();
        }
        Record(document);
        RefreshCommands();
        Raise(nameof(WindowTitle));
        return true;
    }

    // ── Closing ──────────────────────────────────────────────────────────────

    /// <summary>Closes a tab, prompting when it has unsaved changes. Returns false when cancelled.</summary>
    public bool CloseDocument(DocumentViewModel? document) =>
        Host is not null ? document is not null && Host.Close(document) : CloseDocument(document, askAboutChanges: true);

    /// <summary>
    /// Closes a tab; with <paramref name="askAboutChanges"/> false (Close All after one "Don't save" for every
    /// file) unsaved work is not asked about, but it is still kept for Reopen Closed Tab.
    /// </summary>
    private bool CloseDocument(DocumentViewModel? document, bool askAboutChanges)
    {
        if (document is null) return true;
        CommitPendingEdits();
        if (askAboutChanges && document.IsDirty)
        {
            switch (Dialogs.AskUnsavedChanges([document.DisplayName]))
            {
                case UnsavedChoice.Save when !Save(document): return false;
                case UnsavedChoice.Cancel: return false;
            }
        }

        try
        {
            _closedTabs.Push(new ClosedTab(document.FilePath, document.ArchiveOrigin, document.DisplayName, document.Kind,
                document.Serialize(), document.IsDirty));
        }
        catch (ArgumentException)
        {
            // Unwritable content cannot be reopened from bytes; the tab simply is not offered again.
        }
        int index = Documents.IndexOf(document);
        Documents.Remove(document);
        Recovery.Discard(document.Id);
        _states.TryRemove(document.Id, out _);
        document.Dispose();

        if (Documents.Count == 0) ActiveDocument = null;
        else if (ReferenceEquals(_active, document) || _active is null)
            ActiveDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];

        Raise(nameof(HasDocument));
        RefreshCommands();
        return true;
    }

    private void CloseAll()
    {
        CommitPendingEdits();
        var dirty = Documents.Where(d => d.IsDirty).ToList();
        bool ask = true;
        if (dirty.Count > 1)
        {
            // One prompt listing every file, rather than one per tab.
            switch (Dialogs.AskUnsavedChanges([.. dirty.Select(d => d.DisplayName)]))
            {
                case UnsavedChoice.Cancel: return;
                case UnsavedChoice.Save:
                    foreach (var d in dirty)
                    {
                        if (!Save(d)) return;
                    }
                    break;
                case UnsavedChoice.DontSave:
                    // Asked once for all of them: close without asking again (the work stays reopenable).
                    ask = false;
                    break;
            }
        }
        foreach (var document in Documents.ToList())
        {
            if (!CloseDocument(document, ask)) return;
        }
    }

    private void ReopenClosedTab()
    {
        if (_closedTabs.Count == 0) return;
        var tab = _closedTabs.Pop();
        try
        {
            if (DocumentAt(tab.Path) is { } open)
            {
                ActiveDocument = open;
                if (!tab.WasDirty) return;
                // The file was opened again meanwhile: the closed tab's unsaved work must not be dropped.
                if (!open.IsDirty)
                {
                    open.RestoreBytes(tab.Data);
                    return;
                }
                // Both have unsaved work: the closed tab's comes back as an unsaved copy beside it.
                var copy = CreateDocument(tab.Data, tab.DisplayName, null, null);
                copy.MarkAsNew();
                Add(copy);
                ShowShellStatus($"{tab.DisplayName} is already open with other unsaved changes, so the closed tab's work came back as an unsaved copy.");
                return;
            }
            if (tab.Path is not null && File.Exists(tab.Path) && !tab.WasDirty)
            {
                OpenFile(tab.Path);
                return;
            }
            Add(CreateRestoredDocument(tab.Data, tab.DisplayName, tab.Path, tab.ArchiveOrigin, tab.WasDirty));
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            Dialogs.ShowError("The tab could not be reopened.", tab.DisplayName, ex.Message);
        }
        finally { ReopenClosedTabCommand.RaiseCanExecuteChanged(); }
    }

    /// <summary>Asks about every dirty document at once. Returns false when the user cancelled the shutdown.</summary>
    public bool ConfirmShutdown()
    {
        CommitPendingEdits();
        if (IsSessionEnding)
        {
            // No prompts while Windows ends the session, but the snapshot must hold the latest edits.
            try { SnapshotDirtyDocuments(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return true;
        }
        var dirty = Documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count > 0)
        {
            switch (Dialogs.AskUnsavedChanges([.. dirty.Select(d => d.DisplayName)]))
            {
                case UnsavedChoice.Cancel:
                    return false;
                case UnsavedChoice.Save:
                    foreach (var document in dirty)
                    {
                        if (!Save(document)) return false;
                    }
                    break;
            }
        }
        if (!IsDiagnosticRun)
        {
            foreach (var document in Documents) Recovery.Discard(document.Id);
        }
        return true;
    }

    // ── Recovery ─────────────────────────────────────────────────────────────

    /// <summary>Records a document's current state for the crash handler (UI thread).</summary>
    public void Record(DocumentViewModel document)
    {
        if (document is null) return;
        _states[document.Id] = new DocumentState(document.FilePath, document.DisplayName, document.RecoveryKind,
            document.CaptureSerializer(), document.IsDirty);
    }

    /// <summary>Writes a recovery snapshot of every dirty document (timer, crash handler on the UI thread).</summary>
    public IReadOnlyList<string> SnapshotDirtyDocuments()
    {
        var saved = new List<string>();
        if (IsDiagnosticRun) return saved;
        // A document that became clean again (undo to the saved point, reload) must not come back after a crash.
        foreach (var clean in Documents.Where(d => !d.IsDirty)) Recovery.Discard(clean.Id);
        foreach (var document in Documents.Where(d => d.IsDirty))
        {
            try
            {
                if (Recovery.Save(document.Id, document.FilePath, document.DisplayName, document.RecoveryKind, document.Serialize()))
                    saved.Add(document.DisplayName);
            }
            catch (ArgumentException ex)
            {
                ErrorLog.Write("recovery snapshot", ex);
            }
        }
        return saved;
    }

    /// <summary>Writes snapshots from the last recorded states; safe from any thread (snapshots are immutable).</summary>
    public IReadOnlyList<string> SnapshotFromLastKnownState()
    {
        var saved = new List<string>();
        if (IsDiagnosticRun) return saved;
        foreach (var (id, state) in _states)
        {
            if (!state.IsDirty) continue;
            try
            {
                if (Recovery.Save(id, state.Path, state.DisplayName, state.Kind, state.Serialize())) saved.Add(state.DisplayName);
            }
            catch (ArgumentException ex)
            {
                ErrorLog.Write("recovery snapshot", ex);
            }
        }
        return saved;
    }

    public bool IsSessionEnding { get; private set; }

    /// <summary>Windows is ending the session: snapshot everything dirty, no prompts.</summary>
    public void OnSessionEnding()
    {
        IsSessionEnding = true;
        try { SnapshotDirtyDocuments(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        SaveSettings();
        StopRecoveryTimer();
    }

    /// <summary>
    /// The logoff or shutdown was cancelled (WM_ENDSESSION with wParam 0): the session goes on, so closing
    /// asks about unsaved work again and the recovery timer runs again.
    /// </summary>
    public void OnSessionEndingCancelled()
    {
        if (!IsSessionEnding) return;
        IsSessionEnding = false;
        StartRecoveryTimer();
    }

    /// <summary>Dirty documents as last recorded on the UI thread (for the crash handler off it).</summary>
    public int LastKnownDirtyCount => _states.Values.Count(s => s.IsDirty);

    public void StartRecoveryTimer()
    {
        if (!IsDiagnosticRun) _recoveryTimer.Start();
    }

    public void StopRecoveryTimer() => _recoveryTimer.Stop();

    /// <summary>Offers whatever is in the recovery folder (once, after the window is up).</summary>
    public void OfferRecovery()
    {
        // Recovery is offered by the shell (Cairn.Shell restores through RfaModule's kinds).
    }

    private readonly HashSet<string> _restoredSnapshotIds = new(StringComparer.Ordinal);

    /// <summary>Reopens snapshots as dirty documents that still know where they came from.</summary>
    public IReadOnlyList<DocumentViewModel> RestoreSnapshots(IReadOnlyList<RecoverySnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var restored = new List<DocumentViewModel>();
        DocumentViewModel? first = null;
        foreach (var snapshot in snapshots)
        {
            string name = string.IsNullOrWhiteSpace(snapshot.DisplayName) ? "Recovered." + snapshot.DocumentKind : snapshot.DisplayName;
            try
            {
                var document = DocumentAt(snapshot.OriginalPath);
                if (document is null)
                {
                    document = CreateRestoredDocument(snapshot.Data, name, snapshot.OriginalPath is { } p && File.Exists(p) ? p : null, null, wasDirty: true);
                    Add(document);
                }
                else
                {
                    document.RestoreBytes(snapshot.Data);
                }
                if (IsFileNewerThan(snapshot.OriginalPath, snapshot.SavedUtc)) document.NoteDiskIsNewer();
                restored.Add(document);
                _restoredSnapshotIds.Add(snapshot.Id);
                first ??= document;
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Dialogs.ShowError("A recovered file could not be opened.", name, ex.Message);
            }
        }
        if (first is not null) ActiveDocument = first;
        return restored;
    }

    /// <summary>True when the file at <paramref name="path"/> was written after <paramref name="utc"/>.</summary>
    internal static bool IsFileNewerThan(string? path, DateTime utc)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            if (!File.Exists(path)) return false;
            return File.GetLastWriteTimeUtc(path) > utc.AddSeconds(1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    // ── Notifications from documents ─────────────────────────────────────────

    public void OnDocumentDirtyChanged(DocumentViewModel document)
    {
        SaveAllCommand.RaiseCanExecuteChanged();
        if (!ReferenceEquals(document, _active)) return;
        RaiseAll(nameof(WindowTitle), nameof(UndoHeader), nameof(RedoHeader), nameof(UndoToolTip), nameof(RedoToolTip));
        UndoCommand.RaiseCanExecuteChanged();
        RedoCommand.RaiseCanExecuteChanged();
    }

    public void OnDocumentDiagnosticsChanged(DocumentViewModel document)
    {
        if (!ReferenceEquals(document, _active)) return;
        RaiseAll(nameof(ErrorCount), nameof(WarningCount));
    }

    public void OnDocumentStatusChanged(DocumentViewModel document)
    {
        if (!ReferenceEquals(document, _active)) return;
        RaiseStatus();
        document.RaiseStatusItems();
        PlayPauseCommand.RaiseCanExecuteChanged();
    }

    /// <summary>A document's preview mesh or clip changed (the library's compatibility filter follows).</summary>
    public void OnPreviewPartnerChanged(DocumentViewModel document)
    {
        if (ReferenceEquals(document, _active)) Library.OnActiveDocumentChanged();
    }

    /// <summary>The transport time of the active document changed (status bar).</summary>
    public void OnActiveTimeChanged()
    {
        // During playback the status bar repeats the transport's readout; ten updates a second are plenty.
        long now = Environment.TickCount64;
        if (_active?.Playback.IsPlaying == true && now - _lastStatusTime < 100) return;
        _lastStatusTime = now;
        Raise(nameof(StatusTimeText));
        Raise(nameof(StatusSpeedText));
        _active?.RaiseStatusItems();
    }

    private long _lastStatusTime;

    public void CommitPendingEdits() => CommitPendingEditsRequested?.Invoke(this, EventArgs.Empty);

    // ── Settings ─────────────────────────────────────────────────────────────

    private DispatcherTimer? _saveSoon;

    /// <summary>Persists settings (never in a diagnostic run).</summary>
    public void SaveSettings()
    {
        if (IsDiagnosticRun) return;
        Display.Store(Settings);
        SettingsStore.Save(Settings);
    }

    /// <summary>Saves settings a moment later (toggles flipped in a row are one write).</summary>
    public void SaveSettingsSoon()
    {
        if (IsDiagnosticRun) return;
        _saveSoon ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _saveSoon.Tick -= OnSaveSoon;
        _saveSoon.Tick += OnSaveSoon;
        _saveSoon.Stop();
        _saveSoon.Start();
    }

    private void OnSaveSoon(object? sender, EventArgs e)
    {
        _saveSoon?.Stop();
        SaveSettings();
    }

    public void RefreshRecent()
    {
        RecentFiles.Clear();
        foreach (string path in Recent.Items) RecentFiles.Add(path);
        Raise(nameof(HasRecentFiles));
        ClearRecentCommand?.RaiseCanExecuteChanged();
    }

    public bool HasRecentFiles => RecentFiles.Count > 0;
}
