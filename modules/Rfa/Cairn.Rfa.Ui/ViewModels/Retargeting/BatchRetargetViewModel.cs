using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.ViewModels.Retargeting;

/// <summary>A library clip in the batch dialog's picker, with a check box.</summary>
public sealed class BatchPickRow : ObservableObject
{
    private bool _isChecked;

    internal BatchPickRow(LibraryClip clip, string note)
    {
        Clip = clip;
        Note = note;
    }

    public LibraryClip Clip { get; }

    public string Label => Clip.Name;

    public string Note { get; }

    public string ToolTip => $"{Clip.Name} · {Clip.BoneCount} bones · {Clip.Location.DisplayLocation}";

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

/// <summary>One clip queued for the batch, with the output name it will get.</summary>
public sealed class BatchClipRow : ObservableObject
{
    private string _outputName = string.Empty;
    private string? _problem;
    private bool _isSelected;

    internal BatchClipRow(string name, LibraryClip? library, string? filePath, int? boneCount, string origin)
    {
        Name = name;
        Library = library;
        FilePath = filePath;
        BoneCount = boneCount;
        Origin = origin;
    }

    /// <summary>The source clip's file name.</summary>
    public string Name { get; }

    public LibraryClip? Library { get; }

    public string? FilePath { get; }

    public int? BoneCount { get; }

    /// <summary>"motions.vpp", "C:\…", "miner1 state stand".</summary>
    public string Origin { get; }

    public string BonesText => BoneCount is { } b ? b.ToString(CultureInfo.CurrentCulture) : "?";

    /// <summary>The output file name under the current pattern.</summary>
    public string OutputName { get => _outputName; internal set => Set(ref _outputName, value); }

    /// <summary>Why the output name or the clip is a problem (too long, a stock clip's name, a duplicate, the wrong bone count), or null.</summary>
    public string? Problem
    {
        get => _problem;
        internal set
        {
            if (!Set(ref _problem, value)) return;
            RaiseAll(nameof(HasProblem), nameof(HasNote));
        }
    }

    public bool HasProblem => _problem is not null;

    /// <summary>
    /// A quiet note that is not a problem: the name is already in the output folder (a previous run of the
    /// same batch), so the run will ask once before replacing anything. Null when there is a problem.
    /// </summary>
    public string? Note
    {
        get => _note;
        internal set
        {
            if (!Set(ref _note, value)) return;
            Raise(nameof(HasNote));
        }
    }

    private string? _note;

    public bool HasNote => _note is not null && _problem is null;

    /// <summary>Selected in the queue (Remove works on these).</summary>
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
}

/// <summary>One finished batch item.</summary>
public sealed class BatchResultRow
{
    internal BatchResultRow(BatchItemResult result, RetargetPreset? preset)
    {
        Result = result;
        Preset = preset;
        PresetText = preset switch
        {
            RetargetPreset.Seated => "seated",
            RetargetPreset.Locomotion => "standing",
            RetargetPreset.RotationOnly => "rotation only",
            RetargetPreset.Custom => "custom",
            _ => "—",
        };
        var contacts = result.Result?.Contacts ?? [];
        var worst = contacts.IsDefaultOrEmpty ? null : contacts.MaxBy(c => c.MaxErrorCm);
        StretchedCount = contacts.IsDefaultOrEmpty ? 0 : contacts.Count(c => c.Stretched);
        PinnedCm = worst?.MaxErrorCm ?? double.NaN;
        PinnedText = result.Result is null ? "—"
            : worst is null ? "nothing pinned"
            : string.Format(CultureInfo.CurrentCulture, "{0:0.00} cm ({1}){2}", worst.MaxErrorCm, worst.EndBone,
                StretchedCount == 0 ? string.Empty : $", {StretchedCount} {(contacts.Where(c => c.Stretched).All(c => c.IsLeg) ? "leg" : "limb")}{(StretchedCount == 1 ? "" : "s")} fully stretched");
        if (result.Report is { } report && report.JointOffsets.Where(j => !j.Pinned && !double.IsNaN(j.ModelSpaceMaxCm)).MaxBy(j => j.ModelSpaceMaxCm) is { } offset)
            OffsetText = string.Format(CultureInfo.CurrentCulture, "Largest unpinned offset from the source's (proportions, not an error): {0:0.0} cm ({1}).", offset.ModelSpaceMaxCm, offset.CanonicalName);
        var warnings = result.Result?.Warnings ?? [];
        WarningCount = warnings.Length;
        Details = result.Error ?? (warnings.Length > 0 ? string.Join(" ", warnings) : string.Empty);
    }

    /// <summary>The preset the clip was retargeted with (null when it never ran).</summary>
    public RetargetPreset? Preset { get; }

    /// <summary>"seated", "standing", "rotation only", "custom".</summary>
    public string PresetText { get; }

    /// <summary>Worst pinned-contact distance, cm (NaN when nothing was pinned).</summary>
    public double PinnedCm { get; }

    /// <summary>"0.02 cm (foot-l)", "2.31 cm (foot-r), 1 leg fully stretched", "nothing pinned".</summary>
    public string PinnedText { get; }

    /// <summary>Contacts whose limb could not reach at some time.</summary>
    public int StretchedCount { get; }

    /// <summary>The largest unpinned hand/foot/head offset from the source's, for the tooltip, or empty.</summary>
    public string OffsetText { get; } = string.Empty;

    public BatchItemResult Result { get; }

    public string Clip => Result.Item.SourceName;

    public string StatusText => Result.Status switch
    {
        BatchItemStatus.Succeeded => "done",
        BatchItemStatus.Failed => "failed",
        BatchItemStatus.Skipped => "kept existing",
        _ => "cancelled",
    };

    public string StatusBrushKey => Result.Status switch
    {
        BatchItemStatus.Succeeded => WarningCount > 0 ? "Severity.WarningSoft" : "Severity.InfoSoft",
        BatchItemStatus.Failed => "Severity.ErrorSoft",
        _ => "App.AccentSoft",
    };

    public string Output => Result.OutputName ?? "—";

    public int WarningCount { get; }

    public string WarningsText => WarningCount == 0 ? string.Empty : WarningCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>The error, or the warnings, in one line.</summary>
    public string Details { get; }

    public string ToolTip => string.Join(Environment.NewLine,
        new[] { Details.Length > 0 ? Details : Result.OutputPath ?? Output, OffsetText }.Where(s => s.Length > 0));
}

/// <summary>
/// Tools › Batch Retarget…: the shared setup (<see cref="RetargetSetupViewModel"/>), a queue of clips
/// (picked from the library with a filter, "every clip the tables give class …", or files), the output
/// folder and name pattern with a live preview of every name (flagging names over the engine's 59
/// characters, stock clip names and duplicates), a run through <see cref="BatchRetarget.RunAsync"/> with
/// progress, Stop and one collision prompt per batch, the results table, the table lines for the outputs
/// and a text/markdown report.
/// </summary>
public sealed class BatchRetargetViewModel : ObservableObject, IDisposable
{
    private const string FolderKey = "rfa.batchRetargetFolder";
    private const string PatternKey = "rfa.batchRetargetPattern";
    private readonly RfaWorkspace _shell;
    private string _outputFolder;
    private string _pattern;
    private string? _selectedClass;
    private string? _lastClass;
    private bool _onlyFitting = true;
    private bool _running;
    private double _progress;
    private string _progressText = string.Empty;
    private string _summary = string.Empty;
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private IReadOnlyList<BatchItemResult> _lastResults = [];

    public BatchRetargetViewModel(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _outputFolder = shell.DefaultOutputFolder(shell.Settings.Get<string>(FolderKey));
        _pattern = shell.Settings.Get<string>(PatternKey) is { Length: > 0 } p ? p : "af_{rig}_{clip}.rfa";
        string? sourceMesh = (shell.ActiveDocument as ClipDocumentViewModel)?.PreviewLibraryMesh?.Name
            ?? (shell.Assets.Snapshot.FindMesh("ult2_guard.v3c") is { } guard ? guard.Name : null);
        Setup = new RetargetSetupViewModel(shell, sourceMesh, allowAutomatic: true);
        Setup.Changed += (_, _) => { RebuildPicker(); RefreshNames(); RefreshCommands(); };

        Classes = [.. shell.Assets.Usage.Usages.Where(u => u.Table == ClipUsageIndex.EntityTable).Select(u => u.ClassName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase)];
        _selectedClass = Classes.FirstOrDefault(c => string.Equals(c, "miner1", StringComparison.OrdinalIgnoreCase)) ?? Classes.FirstOrDefault();

        AddCheckedCommand = new RelayCommand(AddChecked, () => !_running);
        AddClassCommand = new RelayCommand(() => AddClass(_selectedClass), () => !_running && _selectedClass is not null);
        AddFilesCommand = new RelayCommand(AddFiles, () => !_running);
        RemoveCommand = new RelayCommand(Remove, () => !_running && Clips.Any(c => c.IsSelected));
        ClearCommand = new RelayCommand(() => { Clips.Clear(); RefreshNames(); RefreshCommands(); }, () => !_running && Clips.Count > 0);
        BrowseFolderCommand = new RelayCommand(BrowseFolder, () => !_running);
        RunCommand = new RelayCommand(() => _ = RunAsync(), () => CanRun);
        StopCommand = new RelayCommand(() => _cts?.Cancel(), () => _running);
        CopyTableLinesCommand = new RelayCommand(CopyTableLines, () => Results.Any(r => r.Result.Success));
        SaveReportCommand = new RelayCommand(SaveReport, () => Results.Count > 0);
        Picker = new ClipTools.FilteredList<BatchPickRow>([], r => r.Label);
        RebuildPicker();
    }

    /// <summary>The shell.</summary>
    public RfaWorkspace Shell => _shell;

    /// <summary>The shared setup (meshes, profiles, map, options).</summary>
    public RetargetSetupViewModel Setup { get; }

    /// <summary>Library clips to pick from (checked ones are added).</summary>
    public ClipTools.FilteredList<BatchPickRow> Picker { get; private set; }

    /// <summary>Only list clips with the source mesh's bone count.</summary>
    public bool OnlyFitting
    {
        get => _onlyFitting;
        set
        {
            if (Set(ref _onlyFitting, value)) RebuildPicker();
        }
    }

    /// <summary>Entity classes in the tables.</summary>
    public IReadOnlyList<string> Classes { get; }

    public string? SelectedClass
    {
        get => _selectedClass;
        set
        {
            if (Set(ref _selectedClass, value)) AddClassCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The queue.</summary>
    public ObservableCollection<BatchClipRow> Clips { get; } = [];

    /// <summary>The folder outputs are written to (never the game directory).</summary>
    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !Set(ref _outputFolder, value)) return;
            Raise(nameof(OutputFolderProblem));
            RefreshNames();
            RefreshCommands();
        }
    }

    /// <summary>Why the folder cannot be used, or null.</summary>
    public string? OutputFolderProblem => _shell.IsGameDirectory(_outputFolder)
        ? "That is the game directory, which holds the game's own files. Pick another folder."
        : null;

    /// <summary>The output name pattern: {rig}, {clip}, {source}, {target}, {sourcerig}.</summary>
    public string NamePattern
    {
        get => _pattern;
        set
        {
            if (!Set(ref _pattern, value ?? string.Empty)) return;
            RefreshNames();
            RefreshCommands();
        }
    }

    /// <summary>"3 names need attention" or empty.</summary>
    public string NamesSummary { get; private set; } = string.Empty;

    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (!Set(ref _running, value)) return;
            RefreshCommands();
        }
    }

    /// <summary>0..1.</summary>
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>"40 done · 2 failed · 1 kept existing".</summary>
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    /// <summary>The results of the last run.</summary>
    public ObservableCollection<BatchResultRow> Results { get; } = [];

    /// <summary>True when Run would start.</summary>
    public bool CanRun => !_running && !_disposed && Clips.Count > 0 && Setup.Inputs is not null && OutputFolderProblem is null
        && !string.IsNullOrWhiteSpace(_pattern) && Directory.Exists(_outputFolder);

    /// <summary>Why Run is unavailable, or null.</summary>
    public string? RunBlocker =>
        _running ? null
        : Clips.Count == 0 ? "Add clips to retarget."
        : Setup.Inputs is null ? Setup.NotReadyReason
        : OutputFolderProblem ?? (!Directory.Exists(_outputFolder) ? "The output folder does not exist." : string.IsNullOrWhiteSpace(_pattern) ? "Give a name pattern." : null);

    /// <summary>False lets a diagnostic capture the dialog while the batch is still running (the runner waits for tracked work).</summary>
    internal bool TrackBusy { get; set; } = true;

    /// <summary>The collision prompt (tests replace it). Returns the decision for the whole batch.</summary>
    public Func<string, CollisionDecision>? AskCollision { get; set; }

    public RelayCommand AddCheckedCommand { get; }
    public RelayCommand AddClassCommand { get; }
    public RelayCommand AddFilesCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand BrowseFolderCommand { get; }
    public RelayCommand RunCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand CopyTableLinesCommand { get; }
    public RelayCommand SaveReportCommand { get; }

    // ── Queue ──────────────────────────────────────────────────────────────

    private void RebuildPicker()
    {
        int bones = Setup.SourceBoneCount;
        var library = _shell.Assets.Snapshot;
        var rows = library.Clips.Where(c => c.IsReadable && (!_onlyFitting || bones == 0 || c.BoneCount == bones))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new BatchPickRow(c, $"{c.BoneCount} bones"));
        string filter = Picker?.Filter ?? string.Empty;
        Picker = new ClipTools.FilteredList<BatchPickRow>(rows, r => r.Label) { Filter = filter };
        Raise(nameof(Picker));
    }

    private void AddChecked()
    {
        foreach (var row in Picker.All.Where(r => r.IsChecked))
        {
            Enqueue(row.Clip, row.Clip.Location.DisplayLocation);
            row.IsChecked = false;
        }
        RefreshNames();
        RefreshCommands();
    }

    /// <summary>Adds every clip the tables give <paramref name="className"/> (entity.tbl), once each.</summary>
    public int AddClass(string? className)
    {
        if (string.IsNullOrWhiteSpace(className)) return 0;
        var library = _shell.Assets.Snapshot;
        int added = 0;
        foreach (var usage in _shell.Assets.Usage.UsagesOfClass(className))
        {
            if (library.FindClip(usage.DiskName) is not { IsReadable: true } clip) continue;
            if (Enqueue(clip, usage.Describe())) added++;
        }
        _lastClass = className;
        RefreshNames();
        RefreshCommands();
        return added;
    }

    /// <summary>Adds a library clip unless it is queued already. Returns true when added.</summary>
    public bool Enqueue(LibraryClip clip, string origin)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (Clips.Any(c => string.Equals(c.Name, clip.Name, StringComparison.OrdinalIgnoreCase))) return false;
        Clips.Add(new BatchClipRow(clip.Name, clip, null, clip.BoneCount, origin));
        return true;
    }

    private void AddFiles()
    {
        string[] files = _shell.Dialogs.OpenFiles(_shell.Settings.LastSaveFolder, "Add clips to retarget", "Animation clips (*.rfa)|*.rfa|All files (*.*)|*.*", multiselect: true);
        foreach (string f in files)
        {
            string name = Path.GetFileName(f);
            if (Clips.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            int? bones = null;
            try { bones = RfaProbe.ProbeFile(f).BoneCount; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException) { }
            Clips.Add(new BatchClipRow(name, null, f, bones, f));
        }
        RefreshNames();
        RefreshCommands();
    }

    private void Remove()
    {
        foreach (var row in Clips.Where(c => c.IsSelected).ToList()) Clips.Remove(row);
        RefreshNames();
        RefreshCommands();
    }

    /// <summary>Recomputes every queued clip's output name and problem.</summary>
    public void RefreshNames()
    {
        var setup = Setup;
        var targetProfile = setup.TargetProfile;
        var sourceProfile = setup.SourceProfile;
        var library = _shell.Assets.Snapshot;
        int bones = setup.SourceBoneCount;
        string targetName = setup.Inputs is { } i ? Path.GetFileNameWithoutExtension(i.TargetMeshName) : Path.GetFileNameWithoutExtension(setup.SelectedTargetMesh?.Name ?? "target");
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Clips)
        {
            if (targetProfile is null || sourceProfile is null || string.IsNullOrWhiteSpace(_pattern))
            {
                row.OutputName = "—";
                row.Problem = null;
                continue;
            }
            var item = new BatchItem(row.Name, new RetargetRig(Skeleton.Empty, sourceProfile), new RetargetRig(Skeleton.Empty, targetProfile)) { TargetName = targetName };
            row.OutputName = BatchRetarget.OutputName(item, _pattern);
            seen[row.OutputName] = seen.TryGetValue(row.OutputName, out int n) ? n + 1 : 1;
        }
        int problems = 0, existing = 0;
        bool folderExists = Directory.Exists(_outputFolder);
        foreach (var row in Clips)
        {
            string? problem = null;
            if (bones > 0 && row.BoneCount is { } b && b != bones) problem = $"{b} bones: not made for the source mesh ({bones} bones); it will fail.";
            else if (row.OutputName.Length > 59) problem = $"{row.OutputName.Length} characters: the game copies clip names into a 60-byte buffer (59 at most).";
            else if (seen.TryGetValue(row.OutputName, out int count) && count > 1) problem = "Two clips would get this name: the later one is written as …_2.rfa. Add {source} to the pattern to keep them apart.";
            else if (row.OutputName != "—" && library.FindClip(row.OutputName) is { } stock && !IsInOutputFolder(stock))
                problem = $"A clip called {row.OutputName} exists ({stock.Location.DisplayLocation}); clip names are global in the game.";
            // Not a problem: a file this batch (or an earlier run of it) wrote. The run asks once before replacing.
            bool there = problem is null && row.OutputName != "—" && folderExists && File.Exists(Path.Combine(_outputFolder, row.OutputName));
            row.Problem = problem;
            row.Note = there ? "Already in the output folder (an earlier run?): the run will ask once before replacing anything." : null;
            if (problem is not null) problems++;
            if (there) existing++;
        }
        string existingText = existing == 0 ? string.Empty : $" {existing} already in the output folder: you will be asked before anything is replaced.";
        NamesSummary = Clips.Count == 0 ? string.Empty
            : (problems == 0 ? $"{Clips.Count} clips; no name problems." : $"{Clips.Count} clips; {problems} need attention (hover the warning).") + existingText;
        Raise(nameof(NamesSummary));
        Raise(nameof(RunBlocker));
    }

    /// <summary>True when the library's copy of a clip is the loose file in the output folder (an earlier run's output, not another clip).</summary>
    private bool IsInOutputFolder(LibraryClip clip)
    {
        if (clip.Location.IsArchived || clip.Location.FilePath is not { } path) return false;
        try
        {
            return string.Equals(Path.GetFullPath(Path.GetDirectoryName(path) ?? string.Empty).TrimEnd('\\', '/'),
                Path.GetFullPath(_outputFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void BrowseFolder()
    {
        string? chosen = _shell.Dialogs.PickFolder(_outputFolder, "Choose the folder the retargeted clips are written to");
        if (chosen is not null) OutputFolder = chosen;
    }

    private void RefreshCommands()
    {
        foreach (var c in new[] { AddCheckedCommand, AddClassCommand, AddFilesCommand, RemoveCommand, ClearCommand, BrowseFolderCommand, RunCommand, StopCommand, CopyTableLinesCommand, SaveReportCommand })
            c.RaiseCanExecuteChanged();
        RaiseAll(nameof(CanRun), nameof(RunBlocker));
    }

    /// <summary>Re-queries Remove (a row's selection changed).</summary>
    public void OnSelectionChanged() => RemoveCommand.RaiseCanExecuteChanged();

    // ── Run ────────────────────────────────────────────────────────────────

    /// <summary>Runs the batch. Returns the results (also in <see cref="Results"/>).</summary>
    public async Task<IReadOnlyList<BatchItemResult>> RunAsync()
    {
        if (!CanRun || Setup.Inputs is not { } inputs) return [];
        var queue = Clips.ToList();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        Results.Clear();
        Progress = 0;
        ProgressText = "Reading the clips…";
        Summary = string.Empty;
        string folder = _outputFolder;
        string pattern = _pattern;
        string targetName = Path.GetFileNameWithoutExtension(inputs.TargetMeshName);
        CollisionDecision? decided = null;
        var dispatcher = _shell.Dispatcher;
        // Files already there before the run are the user's to decide about (asked once); a name an earlier
        // clip of this same batch took is never overwritten: the later clip gets "name_2.rfa".
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string f in Directory.EnumerateFiles(folder, "*.rfa")) existing.Add(Path.GetFullPath(f));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        var replacedHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollisionDecision OnCollision(string path)
        {
            string full = Path.GetFullPath(path);
            if (!existing.Contains(full) || replacedHere.Contains(full)) return CollisionDecision.Rename;
            if (decided is null)
            {
                var ask = AskCollision ?? DefaultAsk;
                decided = dispatcher.CheckAccess() ? ask(path) : dispatcher.Invoke(() => ask(path));
            }
            if (decided == CollisionDecision.Overwrite) replacedHere.Add(full);
            return decided.Value;
        }

        using var busy = TrackBusy ? BusyTracker.Begin("batch retarget") : null;
        try
        {
            // Each clip's options: its own preset with "Automatic (per clip)", else the options as set.
            var perClip = queue.Select(row => (row, options: Setup.OptionsFor(row.Name, out var preset), preset)).ToList();
            var presets = new Dictionary<BatchItem, RetargetPreset>(ReferenceEqualityComparer.Instance);
            var items = await Task.Run(() => perClip.Select(c =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var row = c.row;
                byte[]? bytes = null;
                try { bytes = row.Library is { } l ? l.Location.ReadAllBytes() : File.ReadAllBytes(row.FilePath!); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return new BatchItem(row.Name, inputs.Source, inputs.Target)
                {
                    SourceBytes = bytes is null ? null : (ReadOnlyMemory<byte>?)bytes,
                    BoneMap = inputs.Map,
                    TargetName = targetName,
                    Options = c.options,
                };
            }).ToList(), cts.Token).ConfigureAwait(true);
            for (int n = 0; n < items.Count; n++) presets[items[n]] = perClip[n].preset;
            _presets = presets;

            var progress = new Progress<BatchProgress>(p =>
            {
                Progress = p.Total == 0 ? 1 : p.Completed / (double)p.Total;
                ProgressText = p.Last is null
                    ? $"Retargeting {p.Current} ({p.Completed + 1} of {p.Total})…"
                    : $"{p.Completed} of {p.Total} done";
                if (p.Last is { } last) Results.Add(MakeRow(last));
                UpdateSummary();
            });
            var options = new BatchOptions
            {
                Retarget = inputs.Options,
                OutputFolder = folder,
                NamingPattern = pattern,
                OnCollision = OnCollision,
                WriteFiles = true,
                BuildReports = true,
            };
            var results = await BatchRetarget.RunAsync(items, options, progress, cts.Token).ConfigureAwait(true);
            // Items that never reported (cancelled before starting) are added now.
            foreach (var r in results.Skip(Results.Count)) Results.Add(MakeRow(r));
            _lastResults = results;
            Progress = 1;
            ProgressText = cts.IsCancellationRequested ? "Stopped." : "Finished.";
            UpdateSummary();
            if (!_shell.IsDiagnosticRun)
            {
                _shell.Settings.Set(FolderKey, folder);
                _shell.Settings.Set(PatternKey, pattern);
                _shell.SaveSettingsSoon();
            }
            return results;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Stopped before any clip was retargeted.";
            return [];
        }
        catch (ArgumentException ex)
        {
            ProgressText = "The batch could not start: " + ex.Message;
            return [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Never leave the dialog showing "Retargeting…" forever: say what stopped it.
            ErrorLog.Write("batch retarget", ex);
            ProgressText = "The batch stopped on an unexpected problem: " + ex.Message + " Clips already listed below were written.";
            return [];
        }
        finally
        {
            IsRunning = false;
            RefreshNames();
        }
    }

    private CollisionDecision DefaultAsk(string path)
    {
        int choice = _shell.Dialogs.Choose($"{Path.GetFileName(path)} already exists.",
            "Some outputs already exist in the output folder. Replace every existing file, keep the existing files (those clips are skipped), or stop the batch? The answer applies to the whole batch.",
            ["_Replace", "_Keep existing", "Cancel"], 2);
        return choice switch
        {
            0 => CollisionDecision.Overwrite,
            1 => CollisionDecision.Skip,
            _ => CollisionDecision.Cancel,
        };
    }

    private Dictionary<BatchItem, RetargetPreset> _presets = new(ReferenceEqualityComparer.Instance);

    private BatchResultRow MakeRow(BatchItemResult result) =>
        new(result, result.Result is null ? null : _presets.TryGetValue(result.Item, out var p) ? p : null);

    private void UpdateSummary()
    {
        int done = Results.Count(r => r.Result.Status == BatchItemStatus.Succeeded);
        int failed = Results.Count(r => r.Result.Status == BatchItemStatus.Failed);
        int skipped = Results.Count(r => r.Result.Status == BatchItemStatus.Skipped);
        int cancelled = Results.Count(r => r.Result.Status == BatchItemStatus.Cancelled);
        int warned = Results.Count(r => r.WarningCount > 0 && r.Result.Success);
        int stretched = Results.Count(r => r.StretchedCount > 0 && r.Result.Success);
        var parts = new List<string> { $"{done} done" };
        if (warned > 0) parts.Add($"{warned} with warnings");
        if (stretched > 0) parts.Add($"{stretched} with a limb fully stretched");
        if (failed > 0) parts.Add($"{failed} failed");
        if (skipped > 0) parts.Add($"{skipped} kept existing");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        Summary = string.Join(" · ", parts);
        CopyTableLinesCommand.RaiseCanExecuteChanged();
        SaveReportCommand.RaiseCanExecuteChanged();
    }

    // ── After the run ────────────────────────────────────────────────────────

    /// <summary>Opens a result's output (double-click): the written file, previewed on the target.</summary>
    public void OpenResult(BatchResultRow? row)
    {
        if (row?.Result is not { } r || Setup.Inputs is not { } inputs) return;
        if (r.OutputPath is { } path && File.Exists(path))
        {
            if (_shell.OpenFile(path) is ClipDocumentViewModel doc) doc.UsePreviewMesh(inputs.TargetMesh, inputs.TargetMeshName, inputs.TargetFolder);
        }
        else if (!r.OutputBytes.IsDefaultOrEmpty)
        {
            var clip = RfaReader.Read([.. r.OutputBytes], r.OutputName ?? "retargeted.rfa");
            _shell.OpenNewClip(clip, r.OutputName ?? "retargeted.rfa", inputs.TargetMesh, inputs.TargetMeshName, inputs.TargetFolder);
        }
    }

    /// <summary>The table lines (entity.tbl layout) giving the outputs the slots their sources have.</summary>
    public string TableLines()
    {
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Results.Where(r => r.Result.Success && r.Result.OutputName is not null))
            renames[r.Result.Item.SourceName] = r.Result.OutputName!;
        if (renames.Count == 0) return string.Empty;
        var usage = _shell.Assets.Usage;
        IEnumerable<ClipUsage> usages;
        if (_lastClass is not null) usages = usage.UsagesOfClass(_lastClass);
        else
        {
            // The first entity class that uses each clip.
            usages = renames.Keys.SelectMany(name => usage.UsagesOf(name).Where(u => u.Table == ClipUsageIndex.EntityTable))
                .GroupBy(u => (u.ClassName, u.Kind, u.SlotName, u.WeaponBlock)).Select(g => g.First());
        }
        string block = TblSnippet.RetargetBlock(usages, renames);
        if (block.Length == 0)
        {
            // Not in the tables: offer plain states.
            block = TblSnippet.Block(renames.Values.Select(n => new TblSnippetEntry(ClipUsageKind.State, Path.GetFileNameWithoutExtension(n), n)));
        }
        return block;
    }

    private void CopyTableLines()
    {
        string text = TableLines();
        if (text.Length == 0) return;
        if (SystemClipboard.TrySetText(text)) Summary = Summary.Split(" (copied")[0] + " (copied the table lines)";
        else _shell.Dialogs.ShowError("The table lines could not be copied.", "Another program is holding the clipboard. Try again in a moment.", SystemClipboard.LastError?.Message);
    }

    /// <summary>The report of the last run as Markdown.</summary>
    public string ReportMarkdown()
    {
        var sb = new StringBuilder();
        var inputs = Setup.Inputs;
        sb.AppendLine("# Batch retarget report").AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Date: {DateTime.Now:yyyy-MM-dd HH:mm}");
        if (inputs is not null)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Source: {inputs.SourceMeshName} (profile {inputs.Source.Profile.Name})");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Target: {inputs.TargetMeshName} (profile {inputs.Target.Profile.Name}, reference clip {inputs.Target.ReferenceClipName ?? "none"})");
            var o = inputs.Options;
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Preset: {(Setup.IsAutomatic ? "automatic (per clip)" : RetargetPresets.Title(Setup.CurrentPreset))}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Options{(Setup.IsAutomatic ? " (non-preset part)" : string.Empty)}: rest alignment {(o.RestAlignment ? "on" : "off")}, IK {(o.Ik ? $"on (arms {(o.IkArms ? "on" : "off")}, legs {(o.IkLegs ? "on" : "off")})" : "off")}, root {o.RootMode}{(o.ScaleStride ? " (strides scaled)" : string.Empty)}, off hand on the weapon {(o.OffHandFollowsMainHand ? "on" : "off")}, bone lengths {o.BoneLengths}, extra bones {o.ExtraBonePose}, resample {(o.ResampleStep is { } s ? s + " ticks" : "off")}, rounding {o.Quantization}");
        }
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Output folder: {_outputFolder}; pattern `{_pattern}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Result: {Summary}");
        foreach (var g in Results.Where(r => r.Preset is not null).GroupBy(r => r.PresetText))
            sb.AppendLine(CultureInfo.InvariantCulture, $"- Preset {g.Key}: {g.Count()} clips");
        sb.AppendLine();
        sb.AppendLine("| Clip | Status | Preset | Output | Pinned contacts | Warnings | Details |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var r in Results)
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {r.Clip} | {r.StatusText} | {r.PresetText} | {r.Output} | {r.PinnedText} | {r.WarningCount} | {r.Details.Replace("|", "/", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)} |");
        string lines = TableLines();
        if (lines.Length > 0)
        {
            sb.AppendLine().AppendLine("## Table lines").AppendLine().AppendLine("```").Append(lines.TrimEnd()).AppendLine().AppendLine("```");
        }
        return sb.ToString();
    }

    private void SaveReport()
    {
        string? path = _shell.Dialogs.SaveFile(_outputFolder, "Save the batch report", "Markdown (*.md)|*.md|Text (*.txt)|*.txt", "batch_retarget_report.md", ".md");
        if (path is null) return;
        try
        {
            Cairn.Workspace.AtomicFile.WriteAllText(path, ReportMarkdown());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.Dialogs.ShowError("The report could not be saved.", $"'{path}' could not be written.", ex.Message);
        }
    }

    /// <summary>The results of the last run.</summary>
    public IReadOnlyList<BatchItemResult> LastResults => _lastResults;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
    }
}
