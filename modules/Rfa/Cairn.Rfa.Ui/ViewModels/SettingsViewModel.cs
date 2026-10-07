using System.Collections.ObjectModel;
using Cairn.Ui.Mvvm;
using Cairn.Assets;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// Tools &gt; Settings… (ported from ATX Workbench). Edits copies so Cancel really cancels, except the
/// theme, which applies as it is picked (Cancel puts it back). OK writes everything back, saves, and
/// rebuilds the resolver and library when the game directory or the search folders changed.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly RfaWorkspace _shell;
    private readonly AppSettings _working;
    private readonly AppTheme _originalTheme;
    private string? _selectedFolder;
    private string? _validatedDirectory;
    private bool _directoryLooksRight;
    private string _detectionHint = string.Empty;
    private bool _isDetecting;
    private TimeUnit _timeUnit;
    private System.Windows.Threading.DispatcherTimer? _validateDebounce;

    internal SettingsViewModel(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _working = shell.Settings.Clone();
        _originalTheme = shell.Settings.Theme;
        _timeUnit = shell.TimeUnit;
        _doubleClickPreviews = shell.Library.DoubleClickPreviews;
        Display.CopyFrom(shell.Display);
        foreach (string folder in _working.SearchFolders) SearchFolders.Add(folder);

        AutoDetectCommand = new RelayCommand(AutoDetectGameDirectory, () => !_isDetecting);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => _selectedFolder is not null);
        MoveUpCommand = new RelayCommand(() => MoveFolder(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveFolder(+1), () => CanMove(+1));
        BrowseGameDirectoryCommand = new RelayCommand(BrowseGameDirectory);
        _validatedDirectory = _working.GameDirectory;
        _directoryLooksRight = GameDirectoryLocator.LooksLikeGameDirectory(_working.GameDirectory);
        if (_working.Get<string>("gameDirectoryFoundVia") is { Length: > 0 } via && !string.IsNullOrWhiteSpace(_working.GameDirectory))
            _detectionHint = $"Found via {via}.";
        RefreshSearchOrder();
    }

    // ── Theme ────────────────────────────────────────────────────────────────

    public AppTheme Theme
    {
        get => _working.Theme;
        set
        {
            if (_working.Theme == value) return;
            _working.Theme = value;
            _shell.Theme.Apply(value);
            RaiseAll(nameof(Theme), nameof(IsThemeSystem), nameof(IsThemeLight), nameof(IsThemeDark));
        }
    }

    public bool IsThemeSystem { get => Theme == AppTheme.System; set { if (value) Theme = AppTheme.System; } }

    public bool IsThemeLight { get => Theme == AppTheme.Light; set { if (value) Theme = AppTheme.Light; } }

    public bool IsThemeDark { get => Theme == AppTheme.Dark; set { if (value) Theme = AppTheme.Dark; } }

    // ── Game directory ───────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => _working.GameDirectory ?? string.Empty;
        set
        {
            string? stored = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_working.GameDirectory, stored, StringComparison.Ordinal)) return;
            _working.GameDirectory = stored;
            DetectionHint = string.Empty;
            _working.Set<string>("gameDirectoryFoundVia", null);
            RaiseAll(nameof(GameDirectory), nameof(GameDirectoryStatus), nameof(HasGameDirectory));
            ScheduleDirectoryCheck();
            RefreshSearchOrder();
        }
    }

    public bool HasGameDirectory => !string.IsNullOrWhiteSpace(_working.GameDirectory);

    public bool IsGameDirectoryValid => _directoryLooksRight;

    public string GameDirectoryStatus => !HasGameDirectory
        ? "Not set. Clips, meshes, tables and textures will only be looked for next to the open file and in your search folders."
        : IsGameDirectoryValid
            ? "Looks like a Red Faction install: its .vpp archives and user_maps folders will be searched."
            : "That folder does not contain tables.vpp or RF.exe, so it is probably not the game directory.";

    public RelayCommand AutoDetectCommand { get; }

    public RelayCommand BrowseGameDirectoryCommand { get; }

    public string DetectionHint
    {
        get => _detectionHint;
        private set { if (Set(ref _detectionHint, value)) Raise(nameof(HasDetectionHint)); }
    }

    public bool HasDetectionHint => _detectionHint.Length > 0;

    public bool IsDetecting
    {
        get => _isDetecting;
        private set { if (Set(ref _isDetecting, value)) AutoDetectCommand.RaiseCanExecuteChanged(); }
    }

    private void ScheduleDirectoryCheck()
    {
        _validateDebounce ??= new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background, _shell.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _validateDebounce.Tick -= OnDirectoryCheckTick;
        _validateDebounce.Tick += OnDirectoryCheckTick;
        _validateDebounce.Stop();
        _validateDebounce.Start();
    }

    private void OnDirectoryCheckTick(object? sender, EventArgs e)
    {
        _validateDebounce?.Stop();
        string? path = _working.GameDirectory;
        if (string.Equals(path, _validatedDirectory, StringComparison.Ordinal)) return;
        _ = Task.Run(() => GameDirectoryLocator.LooksLikeGameDirectory(path)).ContinueWith(
            task => _shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.Equals(path, _working.GameDirectory, StringComparison.Ordinal)) return;
                _validatedDirectory = path;
                _directoryLooksRight = task.IsCompletedSuccessfully && task.Result;
                RaiseAll(nameof(IsGameDirectoryValid), nameof(GameDirectoryStatus));
            })),
            TaskScheduler.Default);
    }

    private void AutoDetectGameDirectory()
    {
        if (_isDetecting) return;
        IsDetecting = true;
        DetectionHint = string.Empty;
        _ = Task.Run(GameDirectoryLocator.DetectDetailed).ContinueWith(
            task => _shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                IsDetecting = false;
                var found = task.IsCompletedSuccessfully ? task.Result : null;
                if (found is null)
                {
                    _shell.Dialogs.ShowError(
                        "Red Faction was not found.",
                        "RFA Workbench looked at Alpine Faction's own setting, then the registry entries the retail, Steam "
                        + "and GOG releases write, then the usual install paths. Use Browse to point at the folder that holds RF.exe.");
                    return;
                }
                GameDirectory = found.Directory;
                _working.Set("gameDirectoryFoundVia", found.SourceDescription);
                DetectionHint = found.Hint;
            })),
            TaskScheduler.Default);
    }

    private void BrowseGameDirectory()
    {
        string? chosen = _shell.Dialogs.PickFolder(HasGameDirectory ? _working.GameDirectory : null, "Choose the Red Faction folder");
        if (chosen is not null) GameDirectory = chosen;
    }

    // ── Search folders ───────────────────────────────────────────────────────

    public ObservableCollection<string> SearchFolders { get; } = [];

    public string? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (!Set(ref _selectedFolder, value)) return;
            RemoveFolderCommand.RaiseCanExecuteChanged();
            MoveUpCommand.RaiseCanExecuteChanged();
            MoveDownCommand.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand AddFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }

    private void AddFolder()
    {
        string? chosen = _shell.Dialogs.PickFolder(SearchFolders.LastOrDefault(), "Add a folder to search for clips, meshes, tables and textures");
        if (chosen is null) return;
        if (SearchFolders.Any(f => string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase))) return;
        SearchFolders.Add(chosen);
        SelectedFolder = chosen;
        RefreshSearchOrder();
    }

    private void RemoveFolder()
    {
        if (_selectedFolder is null) return;
        int index = SearchFolders.IndexOf(_selectedFolder);
        if (index < 0) return;
        SearchFolders.RemoveAt(index);
        SelectedFolder = SearchFolders.Count == 0 ? null : SearchFolders[Math.Clamp(index, 0, SearchFolders.Count - 1)];
        RefreshSearchOrder();
    }

    private bool CanMove(int delta)
    {
        if (_selectedFolder is null) return false;
        int index = SearchFolders.IndexOf(_selectedFolder);
        return index >= 0 && index + delta >= 0 && index + delta < SearchFolders.Count;
    }

    private void MoveFolder(int delta)
    {
        if (!CanMove(delta) || _selectedFolder is null) return;
        int index = SearchFolders.IndexOf(_selectedFolder);
        SearchFolders.Move(index, index + delta);
        SelectedFolder = _selectedFolder;
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
        RefreshSearchOrder();
    }

    // ── Viewport defaults and time display ───────────────────────────────────

    /// <summary>A working copy of the viewport toggles (the toolbar edits the same values live).</summary>
    public ViewportDisplaySettings Display { get; } = new();

    public IReadOnlyList<string> BackgroundLabels { get; } = [.. ViewportDisplaySettings.BackgroundChoices.Select(c => c.Label)];

    public int BackgroundIndex
    {
        get
        {
            for (int i = 0; i < ViewportDisplaySettings.BackgroundChoices.Count; i++)
            {
                if (ViewportDisplaySettings.BackgroundChoices[i].Value == Display.Background) return i;
            }
            return 0;
        }
        set
        {
            if (value < 0 || value >= ViewportDisplaySettings.BackgroundChoices.Count) return;
            Display.Background = ViewportDisplaySettings.BackgroundChoices[value].Value;
            Raise();
        }
    }

    public bool IsUnitFrames { get => _timeUnit == TimeUnit.Frames; set { if (value) SetUnit(TimeUnit.Frames); } }

    public bool IsUnitSeconds { get => _timeUnit == TimeUnit.Seconds; set { if (value) SetUnit(TimeUnit.Seconds); } }

    public bool IsUnitTicks { get => _timeUnit == TimeUnit.Ticks; set { if (value) SetUnit(TimeUnit.Ticks); } }

    private void SetUnit(TimeUnit unit)
    {
        _timeUnit = unit;
        RaiseAll(nameof(IsUnitFrames), nameof(IsUnitSeconds), nameof(IsUnitTicks));
    }

    // ── Library ──────────────────────────────────────────────────────────────

    private bool _doubleClickPreviews;

    /// <summary>Double-click in the library previews on the document in front (the default).</summary>
    public bool IsDoubleClickPreview
    {
        get => _doubleClickPreviews;
        set { if (value) SetDoubleClick(true); }
    }

    /// <summary>Double-click in the library always opens a new tab.</summary>
    public bool IsDoubleClickNewTab
    {
        get => !_doubleClickPreviews;
        set { if (value) SetDoubleClick(false); }
    }

    private void SetDoubleClick(bool previews)
    {
        _doubleClickPreviews = previews;
        RaiseAll(nameof(IsDoubleClickPreview), nameof(IsDoubleClickNewTab));
    }

    // ── Search order ─────────────────────────────────────────────────────────

    public ObservableCollection<string> SearchOrder { get; } = [];

    public string SearchOrderCaption => _shell.ActiveDocument?.Folder is { } folder
        ? $"Where '{_shell.ActiveDocument.DisplayName}' looks for meshes, tables and textures, in order:"
        : "Where an open file looks for meshes, tables and textures, in order (its own folder comes first):";

    private void RefreshSearchOrder()
    {
        var resolver = new AssetResolver(new AssetResolverOptions
        {
            DocumentFolder = _shell.ActiveDocument?.Folder,
            SearchFolders = [.. SearchFolders],
            GameDirectory = _working.GameDirectory,
        });
        SearchOrder.Clear();
        foreach (string entry in resolver.DescribeSearchOrder()) SearchOrder.Add(entry);
        Raise(nameof(SearchOrderCaption));
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    public void Apply()
    {
        var settings = _shell.Settings;
        bool assetsChanged = !string.Equals(settings.GameDirectory, _working.GameDirectory, StringComparison.OrdinalIgnoreCase)
            || !settings.SearchFolders.SequenceEqual(SearchFolders, StringComparer.OrdinalIgnoreCase);
        settings.Theme = _working.Theme;
        settings.GameDirectory = _working.GameDirectory;
        settings.SearchFolders = [.. SearchFolders];
        settings.Set("gameDirectoryFoundVia", _working.Get<string>("gameDirectoryFoundVia"));
        settings.Set(LibraryViewModel.DoubleClickSettingKey, _doubleClickPreviews ? "preview" : "newTab");
        _shell.Library.OnDoubleClickSettingChanged();
        _shell.Theme.Apply(settings.Theme);
        _shell.Display.CopyFrom(Display);
        _shell.TimeUnit = _timeUnit;
        _shell.SaveSettings();
        _shell.RaiseThemeFlags();
        if (assetsChanged) _shell.ApplyAssetSettings();
    }

    /// <summary>The shell's settings page: applies only the module's sections (library, viewport, time display).</summary>
    public void ApplyModuleSettings()
    {
        _shell.Settings.Set(LibraryViewModel.DoubleClickSettingKey, _doubleClickPreviews ? "preview" : "newTab");
        _shell.Library.OnDoubleClickSettingChanged();
        _shell.Display.CopyFrom(Display);
        _shell.TimeUnit = _timeUnit;
        _shell.SaveSettings();
    }

    public void Revert()
    {
        if (_working.Theme != _originalTheme) _shell.Theme.Apply(_originalTheme);
    }
}
