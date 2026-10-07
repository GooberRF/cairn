using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Rfa.Ui.ViewModels.GltfTools;
using Cairn.Rfa.Ui.ViewModels.Retargeting;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels.ClipCreation;

/// <summary>A mesh a new clip can be made for.</summary>
public sealed class NewClipTarget
{
    internal NewClipTarget(string meshName, string note, string toolTip, Func<CancellationToken, Task<V3dFile>> load, string? textureFolder,
        DocumentViewModel? document = null, LibraryMesh? library = null)
    {
        MeshName = meshName;
        Note = note;
        ToolTip = toolTip;
        Load = load;
        TextureFolder = textureFolder;
        Document = document;
        Library = library;
    }

    /// <summary>"ult2_guard.v3c".</summary>
    public string MeshName { get; }

    public string Label => MeshName;

    /// <summary>"active tab", "preview of ult2_walk.rfa", "25 bones"…</summary>
    public string Note { get; }

    public string ToolTip { get; }

    internal Func<CancellationToken, Task<V3dFile>> Load { get; }

    /// <summary>The folder textures resolve from first (the mesh's own), or null.</summary>
    internal string? TextureFolder { get; }

    /// <summary>The open tab this mesh comes from (a mesh tab, or a clip tab's preview), or null.</summary>
    internal DocumentViewModel? Document { get; }

    /// <summary>The library entry, or null (an open tab's mesh, a browsed file).</summary>
    internal LibraryMesh? Library { get; }

    public override string ToString() => MeshName;
}

/// <summary>A clip a new clip can take its starting pose from.</summary>
/// <param name="Clip">The library clip.</param>
/// <param name="Note">"tables: stand (guard1)", "rig's stand clip", "25 bones".</param>
/// <param name="IsSuggested">True for the default (the character's stand clip).</param>
public sealed record NewClipReferenceChoice(LibraryClip Clip, string Note, bool IsSuggested)
{
    public string Label => Clip.Name;

    public string ToolTip => $"{Clip.Name} · {Clip.BoneCount} bones · {Clip.Location.DisplayLocation}"
        + (IsSuggested ? "\nThe character's stand clip: its bone lengths are the ones every other clip of this character carries." : string.Empty);

    public override string ToString() => Clip.Name;
}

/// <summary>
/// File › New Clip…: a clip made from nothing for a character mesh, holding a correct starting pose on every
/// bone (a reference clip's pose at a time, by default the character's stand clip; or the mesh's bind pose),
/// two keys per track, ready for posing with the gizmos. The dialog previews that pose on the mesh, checks
/// the name as the retarget dialog does, and Create opens the clip (Core <see cref="NewClip.Create(Skeleton, NewClipOptions)"/>)
/// as a new unsaved tab through <see cref="RfaWorkspace.OpenNewClip"/>, previewed on the mesh, with the
/// timeline in front and the playhead at the start.
/// </summary>
public sealed class NewClipViewModel : ObservableObject
{
    internal const string DialogKey = "rfa.newClipDialog";

    /// <summary>Settings key: the mesh the last new clip was made for (the next dialog's default when no tab has one).</summary>
    internal const string LastMeshKey = "rfa.newClipMesh";

    private FilteredList<NewClipTarget> _targets;
    private FilteredList<NewClipReferenceChoice> _references;
    private NewClipTarget? _targetChoice;
    private V3dFile? _targetMesh;
    private Skeleton? _skeleton;
    private int _targetGeneration;
    private bool _targetLoading;
    private RfaClip? _referenceClip;
    private int _referenceGeneration;
    private bool _referenceLoading;
    private string? _loadError;
    private string _suggestionText = string.Empty;
    private bool _useReference;
    private int _poseTime = RfaClip.TicksPerFrame;
    private NewClipKind _kind = NewClipKind.State;
    private int _length = NewClipOptions.DefaultLength;
    private int _startTime = RfaClip.TicksPerFrame;
    private int _version = 8;
    private double _weight = 10;
    private int _rampIn;
    private int _rampOut;
    private bool _isAdvancedExpanded;
    private string _name = string.Empty;
    private bool _nameEdited;
    private RfaClip? _clip;
    private string? _optionsProblem;
    private string? _error;
    private bool _ended;

    /// <summary>
    /// Opens the dialog's model. <paramref name="preferredMesh"/> (a library character from the library's
    /// context menu) is the default target; otherwise the active mesh tab, else the active clip's preview
    /// mesh, else the mesh the last new clip was made for.
    /// </summary>
    public NewClipViewModel(RfaWorkspace shell, LibraryMesh? preferredMesh = null)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Unit = shell.TimeUnit;
        Preview = new ViewportPreviewHost(shell);
        _targets = new FilteredList<NewClipTarget>(BuildTargets(), t => t.MeshName);
        _targets.SelectionChanged += OnTargetSelected;
        _references = new FilteredList<NewClipReferenceChoice>([], r => r.Label);
        _references.SelectionChanged += OnReferenceSelected;
        CreateCommand = new RelayCommand(() => Create(), () => CanCreate);
        BrowseTargetCommand = new RelayCommand(BrowseTarget, () => !_ended);
        ChooseDefaultTarget(preferredMesh);
    }

    public RfaWorkspace Shell { get; }

    /// <summary>The dialog's own scene and transport (the starting pose on the mesh).</summary>
    public ViewportPreviewHost Preview { get; }

    public string Title => "New Clip";

    public string Heading => "New clip";

    public string Description =>
        "Starts a clip from nothing for a character mesh: every bone holds a starting pose, keyed at the start and the end, "
        + "ready for you to pose with the gizmos and auto-key. Nothing is written until you save.";

    // ── Target mesh ─────────────────────────────────────────────────────────

    /// <summary>Character meshes: open tabs first (a clip tab offers its preview mesh), then the library's.</summary>
    public FilteredList<NewClipTarget> Targets
    {
        get => _targets;
        private set => Set(ref _targets, value);
    }

    /// <summary>"ult2_guard.v3c · 25 bones", or what is happening.</summary>
    public string TargetText => _targetLoading
        ? $"Loading {_targetChoice?.MeshName}…"
        : _skeleton is { } s && _targetChoice is { } c
            ? $"{c.MeshName} · {GltfText.Count(s.Count, "bone")}"
            : _loadError ?? "Pick the character mesh the clip is for.";

    /// <summary>The target mesh's file name, or null.</summary>
    public string? TargetMeshName => _skeleton is null ? null : _targetChoice?.MeshName;

    /// <summary>The loaded target mesh, or null.</summary>
    public V3dFile? TargetMesh => _targetMesh;

    /// <summary>The target's skeleton, or null before one is loaded.</summary>
    public Skeleton? TargetSkeleton => _skeleton;

    public RelayCommand BrowseTargetCommand { get; }

    // ── Kind and length ─────────────────────────────────────────────────────

    /// <summary>The time unit the dialog's time fields use (the app's, when it opened).</summary>
    public TimeUnit Unit { get; }

    public string UnitSuffix => TimeFormat.Suffix(Unit);

    public int UnitDecimals => TimeFormat.Decimals(Unit);

    public double UnitStep => TimeFormat.ToUnit(Unit == TimeUnit.Seconds ? RfaClip.TicksPerSecond / 10 : RfaClip.TicksPerFrame, Unit);

    /// <summary>The shortest clip, one frame, in the unit.</summary>
    public double MinimumLength => TimeFormat.ToUnit(RfaClip.TicksPerFrame, Unit);

    public NewClipKind Kind
    {
        get => _kind;
        set
        {
            if (!Set(ref _kind, value)) return;
            var defaults = NewClipOptions.ForKind(value);
            _rampIn = defaults.RampIn;
            _rampOut = defaults.RampOut;
            RaiseAll(nameof(IsState), nameof(IsAction), nameof(RampIn), nameof(RampOut), nameof(RampsText));
            Rebuild();
        }
    }

    public bool IsState
    {
        get => _kind == NewClipKind.State;
        set { if (value) Kind = NewClipKind.State; }
    }

    public bool IsAction
    {
        get => _kind == NewClipKind.Action;
        set { if (value) Kind = NewClipKind.Action; }
    }

    /// <summary>The clip's length in the unit.</summary>
    public double Length
    {
        get => TimeFormat.ToUnit(_length, Unit);
        set
        {
            int ticks = Math.Max(1, TimeFormat.FromUnit(value, Unit));
            if (!Set(ref _length, ticks)) return;
            RaiseAll(nameof(LengthText), nameof(LengthTicks));
            Rebuild();
        }
    }

    /// <summary>The length in ticks.</summary>
    public int LengthTicks
    {
        get => _length;
        set => Length = TimeFormat.ToUnit(Math.Max(1, value), Unit);
    }

    /// <summary>"= 30 frames (1.000 s), 4800 ticks".</summary>
    public string LengthText => $"= {TimeFormat.Duration(_length)}, {_length.ToString("N0", CultureInfo.CurrentCulture)} ticks";

    /// <summary>"No ramps (a state loops; the game ignores ramps on states)" / "Ramps 480 in, 480 out".</summary>
    public string RampsText => _rampIn == 0 && _rampOut == 0
        ? "No ramps: the game ignores them on a state."
        : $"Fades in over {TimeFormat.Format(_rampIn, Unit)} and out over {TimeFormat.Format(_rampOut, Unit)} (Advanced).";

    // ── Starting pose ───────────────────────────────────────────────────────

    /// <summary>Hold a reference clip's pose (the usual choice).</summary>
    public bool UseReference
    {
        get => _useReference;
        set
        {
            if (value == _useReference) return;
            _useReference = value;
            RaiseAll(nameof(UseReference), nameof(UseBindPose), nameof(PoseSummary));
            Rebuild();
        }
    }

    /// <summary>Hold the mesh's bind (rest) pose.</summary>
    public bool UseBindPose
    {
        get => !_useReference;
        set { if (value) UseReference = false; }
    }

    /// <summary>Clips with the mesh's bone count: the suggested stand clip first, then the tables' clips for the mesh, then the rest.</summary>
    public FilteredList<NewClipReferenceChoice> References
    {
        get => _references;
        private set => Set(ref _references, value);
    }

    /// <summary>True when the library has clips with the mesh's bone count.</summary>
    public bool HasReferences => !_references.IsEmpty;

    /// <summary>"Suggested: ult2_stand.rfa, the stand state the tables give guard1." or why there is none.</summary>
    public string SuggestionText
    {
        get => _suggestionText;
        private set => Set(ref _suggestionText, value);
    }

    /// <summary>The loaded reference clip, or null.</summary>
    public RfaClip? ReferenceClip => _referenceClip;

    /// <summary>The time in the reference clip to take the pose from, in the unit (its own timeline).</summary>
    public double PoseTime
    {
        get => TimeFormat.ToUnit(_poseTime, Unit);
        set
        {
            int ticks = TimeFormat.FromUnit(value, Unit);
            if (_referenceClip is { } r) ticks = Math.Clamp(ticks, r.StartTime, r.EndTime);
            if (!Set(ref _poseTime, ticks)) return;
            Raise(nameof(PoseTimeTicks));
            Rebuild();
        }
    }

    /// <summary>The pose time in ticks.</summary>
    public int PoseTimeTicks
    {
        get => _poseTime;
        set => PoseTime = TimeFormat.ToUnit(value, Unit);
    }

    public double PoseTimeMinimum => TimeFormat.ToUnit(_referenceClip?.StartTime ?? 0, Unit);

    public double PoseTimeMaximum => TimeFormat.ToUnit(_referenceClip?.EndTime ?? int.MaxValue, Unit);

    /// <summary>"ult2_stand.rfa runs from 1 f to 101 f; its start is usually the neutral pose."</summary>
    public string PoseTimeHint => _referenceClip is { } r && _references.Selected is { } c
        ? $"{c.Clip.Name} runs from {TimeFormat.Format(r.StartTime, Unit)} to {TimeFormat.Format(r.EndTime, Unit)}; its start is usually the neutral pose."
        : "The time in the reference clip to take the pose from.";

    /// <summary>One line on what the clip will start from.</summary>
    public string PoseSummary => _useReference
        ? (_references.Selected is { } c ? $"Every bone holds {c.Clip.Name}'s pose at {TimeFormat.Format(_poseTime, Unit)}, with its bone lengths." : "Pick the reference clip.")
        : "Every bone holds the mesh's bind pose, with the bone lengths stored in the mesh.";

    // ── Name ────────────────────────────────────────────────────────────────

    /// <summary>The new clip's file name (the tab's name; Save asks where to write it).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value ?? string.Empty)) return;
            _nameEdited = true;
            RaiseAll(nameof(NameProblem), nameof(HasNameProblem), nameof(CanCreate), nameof(Problem));
            CreateCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Why the name will not do (empty, characters the game cannot load, over 59 characters, another clip's name), or null.</summary>
    public string? NameProblem => RetargetDialogViewModel.NameProblem(_name, Shell);

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>The name with ".rfa".</summary>
    public string FileName => _name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase) ? _name : _name + ".rfa";

    // ── Advanced ────────────────────────────────────────────────────────────

    public bool IsAdvancedExpanded
    {
        get => _isAdvancedExpanded;
        set => Set(ref _isAdvancedExpanded, value);
    }

    public bool IsVersion8
    {
        get => _version == 8;
        set
        {
            if (!value || _version == 8) return;
            _version = 8;
            RaiseAll(nameof(IsVersion8), nameof(IsVersion7));
            Rebuild();
        }
    }

    public bool IsVersion7
    {
        get => _version == 7;
        set
        {
            if (!value || _version == 7) return;
            _version = 7;
            RaiseAll(nameof(IsVersion8), nameof(IsVersion7));
            Rebuild();
        }
    }

    /// <summary>Every bone's weight (0 to 10).</summary>
    public double Weight
    {
        get => _weight;
        set
        {
            if (!Set(ref _weight, Math.Clamp(value, 0, 10))) return;
            Rebuild();
        }
    }

    public double StartTime
    {
        get => TimeFormat.ToUnit(_startTime, Unit);
        set
        {
            if (!Set(ref _startTime, Math.Max(0, TimeFormat.FromUnit(value, Unit)))) return;
            Rebuild();
        }
    }

    public double RampIn
    {
        get => TimeFormat.ToUnit(_rampIn, Unit);
        set
        {
            if (!Set(ref _rampIn, Math.Max(0, TimeFormat.FromUnit(value, Unit)))) return;
            Raise(nameof(RampsText));
            Rebuild();
        }
    }

    public double RampOut
    {
        get => TimeFormat.ToUnit(_rampOut, Unit);
        set
        {
            if (!Set(ref _rampOut, Math.Max(0, TimeFormat.FromUnit(value, Unit)))) return;
            Raise(nameof(RampsText));
            Rebuild();
        }
    }

    // ── Result ──────────────────────────────────────────────────────────────

    /// <summary>The Core options the dialog's fields give.</summary>
    public NewClipOptions Options => new()
    {
        Length = _length,
        StartTime = _startTime,
        Version = _version,
        Weight = (float)_weight,
        RampIn = _rampIn,
        RampOut = _rampOut,
        PoseClip = _useReference ? _referenceClip : null,
        PoseTime = _poseTime,
    };

    /// <summary>The clip Create would open (also what the preview shows), or null while it cannot be made.</summary>
    public RfaClip? Clip => _clip;

    /// <summary>"25 bones · 2 rotation and 2 position keys each · 1 s · version 8 · weight 10".</summary>
    public string Summary => _clip is { } c && _skeleton is { } s
        ? $"{GltfText.Count(s.Count, "bone")}, each with 2 rotation and 2 position keys · {TimeFormat.Format(c.StartTime, Unit)} to {TimeFormat.Format(c.EndTime, Unit)} · "
          + $"version {c.Version} · weight {_weight.ToString("0.##", CultureInfo.CurrentCulture)}"
        : string.Empty;

    public RelayCommand CreateCommand { get; }

    /// <summary>True while a mesh or reference clip loads.</summary>
    public bool IsBusy => _targetLoading || _referenceLoading;

    /// <summary>Why Create is disabled, or null.</summary>
    public string? Problem
    {
        get
        {
            if (_targetLoading) return "Loading the mesh…";
            if (_skeleton is null) return _loadError ?? "Pick the character mesh the clip is for.";
            if (_referenceLoading) return "Loading the reference clip…";
            if (_useReference && _referenceClip is null) return "Pick the reference clip, or start from the bind pose.";
            if (_optionsProblem is not null) return _optionsProblem;
            return NameProblem;
        }
    }

    public bool CanCreate => !_ended && _clip is not null && Problem is null;

    /// <summary>Why Create failed, or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (!Set(ref _error, value)) return;
            Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>Raised when the clip was opened and the dialog may close.</summary>
    public event EventHandler? RequestClose;

    /// <summary>Waits until the mesh and the reference clip have loaded (self-tests, captures).</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 60_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((_targetLoading || _referenceLoading) && !_ended)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    /// <summary>
    /// Opens the clip as a new, unsaved tab previewed on the mesh, with the timeline in front and the playhead
    /// at the start. Returns the document, or null when Create is not possible.
    /// </summary>
    public ClipDocumentViewModel? Create()
    {
        if (!CanCreate || _clip is not { } clip || _targetMesh is not { } mesh || _targetChoice is not { } target) return null;
        Error = null;
        ClipDocumentViewModel document;
        try
        {
            document = Shell.OpenNewClip(clip, FileName, mesh, target.MeshName, target.TextureFolder);
        }
        catch (ArgumentException ex)
        {
            Error = "The clip could not be opened: " + ex.Message;
            return null;
        }
        if (!Shell.IsDiagnosticRun)
        {
            Shell.Settings.Set(LastMeshKey, target.MeshName);
            Shell.SaveSettingsSoon();
        }
        Shell.IsBottomVisible = true;
        if (Shell.BottomTabs.FirstOrDefault(t => t.Id == "timeline") is { } timeline) Shell.SelectedBottomTab = timeline;
        document.Playback.Seek(clip.StartTime);
        document.ShowStatus(NextStepText(Shell.Display.AutoKey));
        End();
        RequestClose?.Invoke(this, EventArgs.Empty);
        return document;
    }

    /// <summary>The status bar's one sentence on what to do next.</summary>
    internal static string NextStepText(bool autoKey) => autoKey
        ? "New clip ready: click a joint, press E and drag a ring to pose it (Key is on, so each drag keys the playhead); Save asks where to write it."
        : "New clip ready: turn Key on in the viewport toolbar, then click a joint, press E and drag a ring to key a pose at the playhead; Save asks where to write it.";

    /// <summary>Ends the dialog's live work (every close path). Idempotent.</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _targetGeneration++;
        _referenceGeneration++;
        _targetLoading = false;
        _referenceLoading = false;
        Preview.Dispose();
        RaiseAll(nameof(CanCreate), nameof(IsBusy));
        CreateCommand.RaiseCanExecuteChanged();
    }

    // ── Targets ─────────────────────────────────────────────────────────────

    private List<NewClipTarget> BuildTargets(NewClipTarget? extra = null)
    {
        var list = new List<NewClipTarget>();
        if (extra is not null) list.Add(extra);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var docs = Shell.Documents.ToList();
        if (Shell.ActiveDocument is { } active && docs.Remove(active)) docs.Insert(0, active);
        foreach (var d in docs)
        {
            bool isActive = ReferenceEquals(d, Shell.ActiveDocument);
            switch (d)
            {
                case MeshDocumentViewModel { HasSkeleton: true } m:
                {
                    var mesh = m.Current;
                    list.Add(new NewClipTarget(m.DisplayName, isActive ? "active tab" : "open tab",
                        $"{m.DisplayName} as it is now in its tab ({GltfText.Count(m.Scene.Skeleton.Count, "bone")})", _ => Task.FromResult(mesh), m.Folder, m));
                    seen.Add(m.DisplayName);
                    break;
                }
                case ClipDocumentViewModel { PreviewMesh: { } preview } c when Skeleton.FromFile(preview).Count > 0:
                {
                    string name = c.Scene.MeshName ?? "preview mesh";
                    if (!seen.Add(name)) break;
                    string? folder = c.PreviewLibraryMesh?.Location.FilePath is { } p ? Path.GetDirectoryName(p) : null;
                    list.Add(new NewClipTarget(name, $"preview of {c.DisplayName}", $"{name}, the preview mesh of {c.DisplayName}",
                        _ => Task.FromResult(preview), folder, c, c.PreviewLibraryMesh));
                    break;
                }
            }
        }
        var assets = Shell.Assets;
        foreach (var m in assets.Snapshot.Meshes.Where(m => m.HasSkeleton && m.IsReadable).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (seen.Contains(m.Name)) continue;
            var entry = m;
            string? folder = entry.Location.FilePath is { } p ? Path.GetDirectoryName(p) : null;
            list.Add(new NewClipTarget(entry.Name, GltfText.Count(entry.BoneCount, "bone"), $"{entry.Name} · {entry.BoneCount} bones · {entry.Location.DisplayLocation}",
                ct => assets.LoadMeshAsync(entry, ct), folder, null, entry));
        }
        return list;
    }

    private void ChooseDefaultTarget(LibraryMesh? preferred)
    {
        var all = _targets.All;
        NewClipTarget? pick = null;
        if (preferred is not null)
        {
            pick = all.FirstOrDefault(t => ReferenceEquals(t.Library, preferred))
                ?? all.FirstOrDefault(t => t.Document is MeshDocumentViewModel && string.Equals(t.MeshName, preferred.Name, StringComparison.OrdinalIgnoreCase));
        }
        // The active mesh tab, else the active clip's preview mesh (both listed first), else the last new clip's mesh.
        if (pick is null && Shell.ActiveDocument is { } active)
            pick = all.FirstOrDefault(t => ReferenceEquals(t.Document, active));
        if (pick is null && Shell.Settings.Get<string>(LastMeshKey) is { Length: > 0 } last)
            pick = all.FirstOrDefault(t => string.Equals(t.MeshName, last, StringComparison.OrdinalIgnoreCase));
        if (pick is not null) _targets.Selected = pick;
        else SetDefaultName();
    }

    /// <summary>Selects a target by mesh name (tests and diagnostics). Returns false when it is not offered.</summary>
    public bool SelectTarget(string meshName)
    {
        var pick = _targets.All.FirstOrDefault(t => string.Equals(t.MeshName, meshName, StringComparison.OrdinalIgnoreCase));
        if (pick is null) return false;
        _targets.Selected = pick;
        return true;
    }

    /// <summary>The target choice selected now, or null.</summary>
    public NewClipTarget? SelectedTarget => _targets.Selected;

    private void BrowseTarget()
    {
        string? folder = _targetChoice?.TextureFolder ?? Shell.DefaultOutputFolder();
        var paths = Shell.Dialogs.OpenFiles(folder, "Choose the character mesh", "Character meshes (*.v3c)|*.v3c|All files (*.*)|*.*", false);
        if (paths.Length == 0) return;
        UseFile(paths[0]);
    }

    /// <summary>Uses a <c>.v3c</c> file that is not open or in the library as the target (Browse…).</summary>
    public void UseFile(string path)
    {
        string name = Path.GetFileName(path);
        string? folder = Path.GetDirectoryName(path);
        var choice = new NewClipTarget(name, "file", path, async ct =>
        {
            using var busy = BusyTracker.Begin("read " + name);
            return await Task.Run(() => V3dReader.ReadFile(path), ct).ConfigureAwait(false);
        }, folder);
        _targets.SelectionChanged -= OnTargetSelected;
        var list = new FilteredList<NewClipTarget>(BuildTargets(choice), t => t.MeshName);
        list.SelectionChanged += OnTargetSelected;
        Targets = list;
        list.Selected = choice;
    }

    private async void OnTargetSelected(object? sender, EventArgs e)
    {
        if (_ended || _targets.Selected is not { } choice || ReferenceEquals(choice, _targetChoice) && _skeleton is not null) return;
        int generation = ++_targetGeneration;
        _targetChoice = choice;
        _targetLoading = true;
        _loadError = null;
        RaiseState();
        using var busy = BusyTracker.Begin("new clip target");
        try
        {
            var mesh = await choice.Load(CancellationToken.None).ConfigureAwait(true);
            if (generation != _targetGeneration || _ended) return;
            var skeleton = Skeleton.FromFile(mesh);
            if (skeleton.Count == 0) throw new ArgumentException($"{choice.MeshName} has no bones: clips only play on character meshes (.v3c) with a skeleton.");
            _targetMesh = mesh;
            _skeleton = skeleton;
            _targetLoading = false;
            Preview.SetMesh(mesh, choice.MeshName, Shell.Assets.ResolverFor(choice.TextureFolder));
            GltfText.FrameSoon(Preview);
            if (!_nameEdited) SetDefaultName();
            BuildReferences(choice.MeshName, skeleton);
        }
        catch (Exception ex) when (ex is ArgumentException || RfaWorkspace.IsReadFailure(ex))
        {
            if (generation != _targetGeneration) return;
            _targetMesh = null;
            _skeleton = null;
            _loadError = $"{choice.MeshName} cannot be used: {ex.Message}";
            Preview.SetMesh(null, null, null);
            Preview.SetClip(null);
            _references.SelectionChanged -= OnReferenceSelected;
            References = new FilteredList<NewClipReferenceChoice>([], r => r.Label);
            _referenceClip = null;
        }
        finally
        {
            if (generation == _targetGeneration)
            {
                _targetLoading = false;
                Raise(nameof(TargetMesh));
                Rebuild();
            }
        }
    }

    // ── References ──────────────────────────────────────────────────────────

    private void BuildReferences(string meshName, Skeleton skeleton)
    {
        var snapshot = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        int bones = skeleton.Count;
        bool Fits(LibraryClip? c) => c is { IsReadable: true } && c.BoneCount == bones;
        var suggestion = NewClip.SuggestPoseClip(meshName, skeleton, usage, name => Fits(snapshot.FindClip(name)));
        var suggested = suggestion is null ? null : snapshot.FindClip(suggestion.ClipName);

        // The tables' clips for this mesh (and its family) first when the library knows the mesh, else every clip with the bone count.
        var library = snapshot.FindMesh(meshName);
        var candidates = library is { } m && m.BoneCount == bones ? snapshot.PreviewClipCandidates(m.Name, usage) : snapshot.CompatibleClips(bones);
        var tableSlots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (library is not null)
        {
            foreach (var u in snapshot.ClipsUsedByMesh(library.Name, usage))
            {
                if (u.Clip is { } c) tableSlots.TryAdd(c.Name, $"tables: {(u.Usage.Kind == Cairn.Rfa.Formats.Tbl.ClipUsageKind.State ? "state" : "action")} {u.Usage.SlotName}");
            }
        }
        var list = new List<NewClipReferenceChoice>();
        if (suggested is not null)
        {
            string note = suggestion!.Reason == NewClipPoseReason.TableStand ? $"tables: stand ({suggestion.ClassName})" : "rig's stand clip";
            list.Add(new NewClipReferenceChoice(suggested, note, true));
        }
        foreach (var c in candidates.Where(c => Fits(c) && !ReferenceEquals(c, suggested)))
            list.Add(new NewClipReferenceChoice(c, tableSlots.TryGetValue(c.Name, out string? slot) ? slot : GltfText.Count(bones, "bone"), false));

        _references.SelectionChanged -= OnReferenceSelected;
        var refs = new FilteredList<NewClipReferenceChoice>(list, r => r.Label);
        refs.SelectionChanged += OnReferenceSelected;
        References = refs;
        _referenceClip = null;
        _referenceGeneration++;
        _referenceLoading = false;
        Raise(nameof(HasReferences));

        if (suggested is not null)
        {
            SuggestionText = suggestion!.Reason == NewClipPoseReason.TableStand
                ? $"Suggested: {suggested.Name}, the stand state the tables give {suggestion.ClassName}."
                : $"Suggested: {suggested.Name}, the {suggestion.ClassName} rig's stand clip.";
            _useReference = true;
            refs.Selected = list[0];
        }
        else
        {
            SuggestionText = list.Count == 0
                ? "The library has no clip with this mesh's bone count, so the clip starts from the bind pose."
                : "No stand clip is known for this mesh (no table names one), so the bind pose is the default. Pick one of its clips below if you have one.";
            _useReference = false;
        }
        RaiseAll(nameof(UseReference), nameof(UseBindPose), nameof(PoseSummary));
    }

    /// <summary>Selects a reference clip by name (tests and diagnostics). Returns false when it is not offered.</summary>
    public bool SelectReference(string clipName)
    {
        var pick = _references.All.FirstOrDefault(r => string.Equals(r.Clip.Name, clipName, StringComparison.OrdinalIgnoreCase));
        if (pick is null) return false;
        _references.Selected = pick;
        return true;
    }

    private async void OnReferenceSelected(object? sender, EventArgs e)
    {
        if (_ended) return;
        int generation = ++_referenceGeneration;
        if (_references.Selected?.Clip is not { } clip)
        {
            _referenceClip = null;
            _referenceLoading = false;
            Rebuild();
            return;
        }
        _useReference = true;
        _referenceLoading = true;
        RaiseAll(nameof(UseReference), nameof(UseBindPose));
        RaiseState();
        try
        {
            var loaded = await Shell.Assets.LoadClipAsync(clip).ConfigureAwait(true);
            if (generation != _referenceGeneration || _ended) return;
            _referenceClip = loaded;
            _poseTime = loaded.StartTime;
            _loadError = null;
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            if (generation != _referenceGeneration) return;
            _referenceClip = null;
            Error = $"{clip.Name} could not be read: {ex.Message}";
        }
        finally
        {
            if (generation == _referenceGeneration)
            {
                _referenceLoading = false;
                RaiseAll(nameof(ReferenceClip), nameof(PoseTime), nameof(PoseTimeTicks), nameof(PoseTimeMinimum), nameof(PoseTimeMaximum), nameof(PoseTimeHint));
                Rebuild();
            }
        }
    }

    // ── Name ────────────────────────────────────────────────────────────────

    /// <summary>"&lt;mesh&gt;_new.rfa", numbered on when the library or an open tab already has that name, within 59 characters.</summary>
    private void SetDefaultName()
    {
        string stem = _targetChoice is { } t ? Path.GetFileNameWithoutExtension(t.MeshName) : "new_clip";
        if (string.IsNullOrWhiteSpace(stem)) stem = "new_clip";
        string name = stem;
        for (int n = 1; n < 1000; n++)
        {
            string suffix = n == 1 ? "_new" : $"_new{n}";
            string candidate = Truncate(stem, Cairn.Rfa.Linting.ClipRules.MaxFileNameLength - suffix.Length - 4) + suffix + ".rfa";
            bool taken = Shell.Assets.Snapshot.FindClip(candidate) is not null
                || Shell.Documents.Any(d => string.Equals(d.DisplayName, candidate, StringComparison.OrdinalIgnoreCase));
            name = candidate;
            if (!taken) break;
        }
        _name = name;
        RaiseAll(nameof(Name), nameof(NameProblem), nameof(HasNameProblem), nameof(FileName));

        static string Truncate(string s, int max) => s.Length <= max ? s : s[..Math.Max(1, max)];
    }

    // ── Rebuild ─────────────────────────────────────────────────────────────

    private void RaiseState()
    {
        RaiseAll(nameof(TargetText), nameof(TargetMeshName), nameof(TargetSkeleton), nameof(IsBusy), nameof(CanCreate), nameof(Problem));
        CreateCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Remakes the clip (a few keys per bone: cheap enough for the UI thread) and shows it.</summary>
    private void Rebuild()
    {
        if (_ended) return;
        _clip = null;
        _optionsProblem = null;
        if (_skeleton is { } skeleton && !_targetLoading && !_referenceLoading && !(_useReference && _referenceClip is null))
        {
            var options = Options;
            _optionsProblem = NewClip.Problem(skeleton, options);
            if (_optionsProblem is null) _clip = NewClip.Create(skeleton, options);
        }
        Preview.SetClip(_clip);
        if (_clip is not null) Preview.Playback.Seek(_clip.StartTime);
        RaiseAll(nameof(Clip), nameof(Summary), nameof(PoseSummary), nameof(PoseTimeHint), nameof(LengthText));
        RaiseState();
    }
}
