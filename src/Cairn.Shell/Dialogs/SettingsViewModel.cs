using System.Collections.ObjectModel;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Shell.Dialogs;

/// <summary>
/// Tools &gt; Settings… (ported from RFA Workbench). Edits a copy so Cancel really cancels, except the
/// theme, which applies as it is picked (Cancel puts it back). OK writes everything back, commits the
/// module pages, applies the theme and reconfigures the asset host.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    /// <summary>The <see cref="AppSettings.Values"/> key remembering how the game directory was found.</summary>
    public const string FoundViaKey = "gameDirectoryFoundVia";

    private readonly ShellViewModel _shell;
    private readonly AppSettings _working;
    private readonly AppTheme _originalTheme;
    private string? _selectedFolder;
    private string? _validatedDirectory;
    private bool _directoryLooksRight;
    private string _detectionHint = string.Empty;
    private bool _isDetecting;
    private DispatcherTimer? _validateDebounce;

    /// <param name="shell">The shell.</param>
    /// <param name="associations">Registry access for the File associations page; null = the real registry (self-tests pass a fake).</param>
    public SettingsViewModel(ShellViewModel shell, IAssociationStore? associations = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _working = shell.Settings.Clone();
        _originalTheme = shell.Settings.Theme;
        foreach (var folder in _working.SearchFolders) SearchFolders.Add(folder);
        Associations = new AssociationsPage(new AssociationsModel(associations ?? new WindowsAssociationStore(), shell.Modules, shell.Dialogs, shell.ShowStatus));
        Pages = [Associations, .. shell.Modules.SelectMany(m => m.SettingsPages)];

        AutoDetectCommand = new RelayCommand(AutoDetectGameDirectory, () => !_isDetecting);
        BrowseGameDirectoryCommand = new RelayCommand(BrowseGameDirectory);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => _selectedFolder is not null);
        MoveUpCommand = new RelayCommand(() => MoveFolder(-1), () => CanMove(-1));
        MoveDownCommand = new RelayCommand(() => MoveFolder(+1), () => CanMove(+1));

        // The first check runs off the UI thread like every later one (it touches the disk).
        CheckDirectory();
        if (_working.Get<string>(FoundViaKey) is { Length: > 0 } via && HasGameDirectory)
            _detectionHint = $"Found via {via}.";
    }

    /// <summary>The shell's File associations page, then the modules' pages in module order; each is loaded when the dialog opens.</summary>
    public IReadOnlyList<ISettingsPage> Pages { get; }

    /// <summary>The shell's File associations page (first of <see cref="Pages"/>).</summary>
    public AssociationsPage Associations { get; }

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
            var stored = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_working.GameDirectory, stored, StringComparison.Ordinal)) return;
            _working.GameDirectory = stored;
            DetectionHint = string.Empty;
            _working.Set<string>(FoundViaKey, null);
            RaiseAll(nameof(GameDirectory), nameof(GameDirectoryStatus), nameof(HasGameDirectory));
            ScheduleDirectoryCheck();
        }
    }

    public bool HasGameDirectory => !string.IsNullOrWhiteSpace(_working.GameDirectory);

    public bool IsGameDirectoryValid => _directoryLooksRight;

    public string GameDirectoryStatus => !HasGameDirectory
        ? "Not set. Assets will only be looked for next to the open file and in your search folders."
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
        _validateDebounce ??= new DispatcherTimer(DispatcherPriority.Background, _shell.Dispatcher)
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
        if (!string.Equals(_working.GameDirectory, _validatedDirectory, StringComparison.Ordinal)) CheckDirectory();
    }

    private void CheckDirectory()
    {
        var path = _working.GameDirectory;
        _ = Task.Run(() => GameDirectoryLocator.LooksLikeGameDirectory(path)).ContinueWith(
            task => _shell.Dispatcher.BeginInvoke(() =>
            {
                if (!string.Equals(path, _working.GameDirectory, StringComparison.Ordinal)) return;
                _validatedDirectory = path;
                _directoryLooksRight = task.IsCompletedSuccessfully && task.Result;
                RaiseAll(nameof(IsGameDirectoryValid), nameof(GameDirectoryStatus));
            }),
            TaskScheduler.Default);
    }

    private void AutoDetectGameDirectory()
    {
        if (_isDetecting) return;
        IsDetecting = true;
        DetectionHint = string.Empty;
        _ = Task.Run(GameDirectoryLocator.DetectDetailed).ContinueWith(
            task => _shell.Dispatcher.BeginInvoke(() =>
            {
                IsDetecting = false;
                var found = task.IsCompletedSuccessfully ? task.Result : null;
                if (found is null)
                {
                    _shell.Dialogs.ShowError(
                        "Red Faction was not found.",
                        "Cairn looked at Alpine Faction's own setting, then the registry entries the retail, Steam "
                        + "and GOG releases write, then the usual install paths. Use Browse to point at the folder that holds RF.exe.");
                    return;
                }
                GameDirectory = found.Directory;
                _working.Set(FoundViaKey, found.SourceDescription);
                DetectionHint = found.Hint;
            }),
            TaskScheduler.Default);
    }

    private void BrowseGameDirectory()
    {
        var chosen = _shell.Dialogs.PickFolder(HasGameDirectory ? _working.GameDirectory : null, "Choose the Red Faction folder");
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
        var chosen = _shell.Dialogs.PickFolder(SearchFolders.LastOrDefault(), "Add a folder to search for assets");
        if (chosen is null) return;
        if (SearchFolders.Any(f => string.Equals(f, chosen, StringComparison.OrdinalIgnoreCase))) return;
        SearchFolders.Add(chosen);
        SelectedFolder = chosen;
    }

    private void RemoveFolder()
    {
        if (_selectedFolder is null) return;
        var index = SearchFolders.IndexOf(_selectedFolder);
        if (index < 0) return;
        SearchFolders.RemoveAt(index);
        SelectedFolder = SearchFolders.Count == 0 ? null : SearchFolders[Math.Clamp(index, 0, SearchFolders.Count - 1)];
    }

    private bool CanMove(int delta)
    {
        if (_selectedFolder is null) return false;
        var index = SearchFolders.IndexOf(_selectedFolder);
        return index >= 0 && index + delta >= 0 && index + delta < SearchFolders.Count;
    }

    private void MoveFolder(int delta)
    {
        if (!CanMove(delta) || _selectedFolder is null) return;
        var index = SearchFolders.IndexOf(_selectedFolder);
        SearchFolders.Move(index, index + delta);
        MoveUpCommand.RaiseCanExecuteChanged();
        MoveDownCommand.RaiseCanExecuteChanged();
    }

    // ── Commit ───────────────────────────────────────────────────────────────

    /// <summary>OK: writes the working copy back, commits the module pages, applies theme and assets.</summary>
    public void Apply()
    {
        var s = _shell.Settings;
        s.Theme = _working.Theme;
        s.GameDirectory = _working.GameDirectory;
        s.SearchFolders = [.. SearchFolders];
        s.Set(FoundViaKey, _working.Get<string>(FoundViaKey));
        // The shared resolver first: a module page that rebuilds its own asset services shares its indexes.
        _shell.Assets.Reconfigure(s);
        foreach (var page in Pages) page.Commit();
        _shell.Theme.Apply(s.Theme);
    }

    /// <summary>Cancel: puts back the theme picked live; nothing else was written.</summary>
    public void Revert()
    {
        _validateDebounce?.Stop();
        if (_working.Theme != _originalTheme) _shell.Theme.Apply(_originalTheme);
    }
}
