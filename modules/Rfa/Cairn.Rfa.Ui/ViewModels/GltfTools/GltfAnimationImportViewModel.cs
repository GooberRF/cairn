using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Rfa.Ui.ViewModels.Retargeting;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;

namespace Cairn.Rfa.Ui.ViewModels.GltfTools;

/// <summary>One animation of the glTF file.</summary>
public sealed class GltfAnimationRow : CheckRow
{
    internal GltfAnimationRow(int index, string name, string durationText, int channels, bool hasRfExtras)
    {
        Index = index;
        Name = name;
        DurationText = durationText;
        Channels = channels;
        HasRfExtras = hasRfExtras;
    }

    /// <summary>Index in the file.</summary>
    public int Index { get; }

    public string Name { get; }

    public override string Label => Name;

    /// <summary>"40 f".</summary>
    public string DurationText { get; }

    public int Channels { get; }

    /// <summary>True when the animation carries REDUX/RFA Workbench header extras.</summary>
    public bool HasRfExtras { get; }

    /// <summary>"40 f · 52 channels · RF extras".</summary>
    public string Note => $"{DurationText} · {GltfText.Count(Channels, "channel")}" + (HasRfExtras ? " · RF extras" : "");

    public string ToolTip => $"{Name}: {DurationText}, {GltfText.Count(Channels, "channel")}"
        + (HasRfExtras ? ". It carries RF header extras (start, end, ramps), so its timing comes from the file." : ".")
        + " Tick it to import it; click it to preview it.";

    public string AutomationName => $"Import {Name}";
}

/// <summary>A mesh the animation can be imported onto.</summary>
public sealed class GltfTargetChoice
{
    internal GltfTargetChoice(string meshName, string note, string toolTip, Func<CancellationToken, Task<V3dFile>> load, AssetResolver? textures, string? textureFolder)
    {
        MeshName = meshName;
        Note = note;
        ToolTip = toolTip;
        Load = load;
        Textures = textures;
        TextureFolder = textureFolder;
    }

    /// <summary>"ult2_guard.v3c".</summary>
    public string MeshName { get; }

    public string Label => MeshName;

    /// <summary>"open tab", "26 bones"…</summary>
    public string Note { get; }

    public string ToolTip { get; }

    internal Func<CancellationToken, Task<V3dFile>> Load { get; }

    internal AssetResolver? Textures { get; }

    internal string? TextureFolder { get; }

    public override string ToString() => MeshName;
}

/// <summary>A reference clip choice ("none" holds the bind pose).</summary>
/// <param name="Clip">The library clip, or null for none.</param>
/// <param name="Label">The text shown.</param>
/// <param name="Note">A dimmed note.</param>
public sealed record GltfReferenceChoice(LibraryClip? Clip, string Label, string Note)
{
    public string ToolTip => Clip is null
        ? "Bones without a source node hold the target mesh's bind pose."
        : $"Bones without a source node hold {Clip.Name}'s pose at its start ({Clip.Location.DisplayLocation}).";

    public override string ToString() => Label;
}

/// <summary>One target bone of the import report.</summary>
public sealed record GltfBoneReportRow(int Bone, string BoneName, string Source, GltfBoneImportMode Mode, int RotationKeys, int PositionKeys)
{
    public string IndexText => Bone.ToString(CultureInfo.InvariantCulture);

    public string RotationText => RotationKeys.ToString("N0", CultureInfo.CurrentCulture);

    public string PositionText => PositionKeys.ToString("N0", CultureInfo.CurrentCulture);

    public string ModeText => Mode switch
    {
        GltfBoneImportMode.Restored => "restored",
        GltfBoneImportMode.Keys => "keys",
        GltfBoneImportMode.Resampled => "resampled",
        GltfBoneImportMode.RestPose => "bind pose",
        GltfBoneImportMode.ReferencePose => "reference pose",
        _ => Mode.ToString(),
    };

    public string ModeToolTip => Mode switch
    {
        GltfBoneImportMode.Restored => "Restored key for key from the RF key extras: exactly the keys that were exported.",
        GltfBoneImportMode.Keys => "The glTF keys converted one for one (same times).",
        GltfBoneImportMode.Resampled => "Resampled every sample step: its node hangs off a different parent or has a different rest frame than the bone, or the file uses curves the engine cannot store.",
        GltfBoneImportMode.RestPose => "No source node: holds the target mesh's bind pose.",
        GltfBoneImportMode.ReferencePose => "No source node: holds the reference clip's pose.",
        _ => string.Empty,
    };

    public string ModeBrushKey => Mode switch
    {
        GltfBoneImportMode.Restored or GltfBoneImportMode.Keys => "Severity.InfoSoft",
        GltfBoneImportMode.Resampled => "Severity.WarningSoft",
        _ => "App.AccentSoft",
    };
}

/// <summary>What one preview recompute produced.</summary>
internal sealed record GltfAnimationPreview(GltfImportedClip? Imported, string? Message);

/// <summary>
/// File › Import Animation from glTF…: reads the file off the UI thread, lists its animations, maps its
/// nodes to a target mesh's bones with the shared bone map table, previews the selected animation on the
/// target (recomputed off-thread, debounced, cancellable, whenever anything changes) with a per-bone
/// report, and on Import opens every ticked animation as a new unsaved clip document whose preview mesh
/// is the target.
/// </summary>
public sealed class GltfAnimationImportViewModel : ObservableObject
{
    internal const string DialogKey = "rfa.gltfImportAnimationDialog";

    private readonly object _docGate = new();
    private readonly BackgroundRecompute<GltfAnimationPreview> _preview;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GltfDocument? _doc;
    private GltfSourceSkeleton? _source;
    private GltfAnimationRow? _selectedAnimation;
    private V3dFile? _targetMesh;
    private Skeleton? _targetSkeleton;
    private GltfTargetChoice? _targetChoice;
    private int _targetGeneration;
    private bool _targetLoading;
    private RfaClip? _referenceClip;
    private int _referenceGeneration;
    private bool _referenceLoading;
    private FilteredList<GltfTargetChoice> _targets;
    private FilteredList<GltfReferenceChoice> _references;
    private string _sourceSummary = "Reading the file…";
    private string? _loadError;
    private string? _previewMessage;
    private GltfImportedClip? _imported;
    private IReadOnlyList<GltfBoneReportRow> _reportRows = [];
    private IReadOnlyList<string> _warnings = [];
    private string _reportSummary = string.Empty;
    private bool _isImporting;
    private string _progressText = string.Empty;
    private string? _error;
    private CancellationTokenSource? _importCts;
    private bool _ended;

    // Options (times in ticks; the fields show them in the time unit the dialog opened with).
    private double _defaultWeight = 10;
    private int _startTick = RfaClip.TicksPerFrame;
    private int _sampleStepTicks = RfaClip.TicksPerFrame;
    private int _version = 8;
    private int _rampIn;
    private int _rampOut;
    private bool _reduce;
    private double _rotationTolerance = 0.1;
    private double _positionTolerance = 0.0005;
    private bool _useKeyExtras = true;

    /// <summary>Opens the model for a glTF file (read off the UI thread unless <paramref name="preloaded"/> is given).</summary>
    public GltfAnimationImportViewModel(RfaWorkspace shell, string path, GltfDocument? preloaded = null)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        FileName = Path.GetFileName(path);
        Unit = shell.TimeUnit;
        Preview = new ViewportPreviewHost(shell);
        BoneMap = new BoneMapEditorViewModel { SourceHeader = "glTF node", TargetHeader = "Target bone" };
        BoneMap.MapChanged += (_, _) =>
        {
            RaiseAll(nameof(CanImport), nameof(Problem));
            Schedule();
        };
        _preview = new BackgroundRecompute<GltfAnimationPreview>(shell.Dispatcher, "glTF animation preview", PreparePreview, OnPreview, OnPreviewFailed);
        _preview.PendingChanged += (_, _) => RaiseAll(nameof(IsBusy), nameof(CanImport));

        _targets = new FilteredList<GltfTargetChoice>(BuildTargets(), t => t.MeshName);
        _targets.SelectionChanged += OnTargetSelected;
        _references = new FilteredList<GltfReferenceChoice>([NoReference], r => r.Label);
        _references.Selected = NoReference;
        _references.SelectionChanged += OnReferenceSelected;

        ImportCommand = new RelayCommand(() => _ = ImportAsync(), () => CanImport);
        StopCommand = new RelayCommand(() => _importCts?.Cancel(), () => _isImporting);
        BrowseTargetCommand = new RelayCommand(BrowseTarget, () => !_isImporting);
        _ = LoadAsync(preloaded);
    }

    private static readonly GltfReferenceChoice NoReference = new(null, "(none — the bind pose)", "default");

    public RfaWorkspace Shell { get; }

    /// <summary>The glTF file.</summary>
    public string FilePath { get; }

    public string FileName { get; }

    /// <summary>The dialog's own scene and transport (the preview).</summary>
    public ViewportPreviewHost Preview { get; }

    /// <summary>The playback of the preview (the play/scrub row binds here).</summary>
    public PlaybackViewModel Playback => Preview.Playback;

    /// <summary>The shared bone map table (glTF nodes → target bones).</summary>
    public BoneMapEditorViewModel BoneMap { get; }

    /// <summary>Completes once the file is read and a default target is set up (or the read failed).</summary>
    public Task Ready => _ready.Task;

    public string Title => "Import Animation from glTF";

    public string Heading => $"Import animation from {FileName}";

    public string Description =>
        "Converts glTF animations into RF clips for a target mesh: nodes map to its bones by name, channels are converted or "
        + "resampled, and RF extras (an RFA Workbench or REDUX export) restore timing, ramps, weights and exact keys.";

    /// <summary>"3 animations · 26-joint skin · Blender 4.1".</summary>
    public string SourceSummary
    {
        get => _sourceSummary;
        private set => Set(ref _sourceSummary, value);
    }

    /// <summary>Why the file could not be read, or null.</summary>
    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (!Set(ref _loadError, value)) return;
            RaiseAll(nameof(CanImport), nameof(Problem));
        }
    }

    // ── Animations ──────────────────────────────────────────────────────────

    /// <summary>The file's animations (ticked ones are imported).</summary>
    public ObservableCollection<GltfAnimationRow> Animations { get; } = [];

    /// <summary>The animation the preview and the report show.</summary>
    public GltfAnimationRow? SelectedAnimation
    {
        get => _selectedAnimation;
        set
        {
            if (!Set(ref _selectedAnimation, value)) return;
            Raise(nameof(HasRfExtras));
            Schedule();
        }
    }

    /// <summary>"2 of 3 animations ticked".</summary>
    public string AnimationsSummary => Animations.Count == 0
        ? (_doc is null ? string.Empty : "The file has no animations.")
        : $"{Animations.Count(a => a.IsChecked)} of {GltfText.Count(Animations.Count, "animation")} will be imported (one clip each).";

    /// <summary>True when the selected animation carries header extras.</summary>
    public bool HasRfExtras => _selectedAnimation?.HasRfExtras == true;

    // ── Target ──────────────────────────────────────────────────────────────

    /// <summary>Meshes the animation can go onto: open documents first, then library characters.</summary>
    public FilteredList<GltfTargetChoice> Targets
    {
        get => _targets;
        private set => Set(ref _targets, value);
    }

    /// <summary>"ult2_guard.v3c · 26 bones", or what is happening.</summary>
    public string TargetText => _targetLoading
        ? $"Loading {_targetChoice?.MeshName}…"
        : _targetSkeleton is { } s && _targetChoice is { } c
            ? $"{c.MeshName} · {GltfText.Count(s.Count, "bone")}"
            : "Pick the mesh the animation is for.";

    /// <summary>The target skeleton, or null before one is loaded.</summary>
    public Skeleton? TargetSkeleton => _targetSkeleton;

    /// <summary>The target mesh, or null.</summary>
    public V3dFile? TargetMesh => _targetMesh;

    public RelayCommand BrowseTargetCommand { get; }

    // ── Options ─────────────────────────────────────────────────────────────

    /// <summary>The time unit the dialog's time fields use.</summary>
    public TimeUnit Unit { get; }

    public string UnitSuffix => TimeFormat.Suffix(Unit);

    public int UnitDecimals => TimeFormat.Decimals(Unit);

    public double UnitStep => TimeFormat.ToUnit(RfaClip.TicksPerFrame, Unit);

    /// <summary>The smallest sample step (one tick) in the unit.</summary>
    public double MinimumStep => TimeFormat.ToUnit(1, Unit);

    /// <summary>Clips on the target skeleton whose start pose unmapped bones hold.</summary>
    public FilteredList<GltfReferenceChoice> References
    {
        get => _references;
        private set => Set(ref _references, value);
    }

    public double DefaultWeight
    {
        get => _defaultWeight;
        set { if (Set(ref _defaultWeight, value)) Schedule(); }
    }

    public double StartTime
    {
        get => TimeFormat.ToUnit(_startTick, Unit);
        set { if (SetTicks(ref _startTick, TimeFormat.FromUnit(value, Unit))) Schedule(); }
    }

    public double SampleStep
    {
        get => TimeFormat.ToUnit(_sampleStepTicks, Unit);
        set { if (SetTicks(ref _sampleStepTicks, Math.Max(1, TimeFormat.FromUnit(value, Unit)))) Schedule(); }
    }

    public bool IsVersion8
    {
        get => _version == 8;
        set
        {
            if (!value || _version == 8) return;
            _version = 8;
            RaiseAll(nameof(IsVersion8), nameof(IsVersion7));
            Schedule();
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
            Schedule();
        }
    }

    public double RampIn
    {
        get => TimeFormat.ToUnit(_rampIn, Unit);
        set { if (SetTicks(ref _rampIn, Math.Max(0, TimeFormat.FromUnit(value, Unit)))) Schedule(); }
    }

    public double RampOut
    {
        get => TimeFormat.ToUnit(_rampOut, Unit);
        set { if (SetTicks(ref _rampOut, Math.Max(0, TimeFormat.FromUnit(value, Unit)))) Schedule(); }
    }

    public bool Reduce
    {
        get => _reduce;
        set { if (Set(ref _reduce, value)) Schedule(); }
    }

    public double RotationTolerance
    {
        get => _rotationTolerance;
        set { if (Set(ref _rotationTolerance, Math.Max(0, value))) Schedule(); }
    }

    public double PositionTolerance
    {
        get => _positionTolerance;
        set { if (Set(ref _positionTolerance, Math.Max(0, value))) Schedule(); }
    }

    public bool UseKeyExtras
    {
        get => _useKeyExtras;
        set { if (Set(ref _useKeyExtras, value)) Schedule(); }
    }

    private bool SetTicks(ref int field, int value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null) => Set(ref field, value, name);

    /// <summary>The Core options the dialog's fields give (for one animation, or all when <paramref name="animation"/> is null).</summary>
    public GltfAnimationImportOptions OptionsFor(int? animation) => new()
    {
        AnimationIndex = animation,
        BoneMap = BoneMap.Map,
        ReferenceClip = _referenceClip,
        DefaultWeight = (float)_defaultWeight,
        StartTick = _startTick,
        SampleStepTicks = _sampleStepTicks,
        Version = _version,
        RampIn = _rampIn,
        RampOut = _rampOut,
        Reduce = _reduce ? new ReduceOptions { RotationToleranceDegrees = (float)_rotationTolerance, PositionTolerance = (float)_positionTolerance } : null,
        UseKeyExtras = _useKeyExtras,
    };

    // ── Preview and report ──────────────────────────────────────────────────

    /// <summary>True while the preview is being recomputed (or the target or reference loads).</summary>
    public bool IsBusy => _preview.IsPending || _targetLoading || _referenceLoading;

    /// <summary>Why there is no preview ("Pick a target mesh"), or a refusal; null when the preview shows.</summary>
    public string? PreviewMessage
    {
        get => _previewMessage;
        private set
        {
            if (!Set(ref _previewMessage, value)) return;
            Raise(nameof(HasPreviewMessage));
        }
    }

    public bool HasPreviewMessage => _previewMessage is not null;

    /// <summary>The last preview's clip and report.</summary>
    public GltfImportedClip? Imported => _imported;

    /// <summary>One row per target bone.</summary>
    public IReadOnlyList<GltfBoneReportRow> ReportRows
    {
        get => _reportRows;
        private set => Set(ref _reportRows, value);
    }

    /// <summary>"24 restored · 2 bind pose · 1,234 rotation keys…".</summary>
    public string ReportSummary
    {
        get => _reportSummary;
        private set => Set(ref _reportSummary, value);
    }

    /// <summary>The importer's notes for the previewed animation.</summary>
    public IReadOnlyList<string> Warnings
    {
        get => _warnings;
        private set
        {
            if (!Set(ref _warnings, value)) return;
            Raise(nameof(HasWarnings));
        }
    }

    public bool HasWarnings => _warnings.Count > 0;

    // ── Import ──────────────────────────────────────────────────────────────

    public RelayCommand ImportCommand { get; }

    public RelayCommand StopCommand { get; }

    public bool IsImporting
    {
        get => _isImporting;
        private set
        {
            if (!Set(ref _isImporting, value)) return;
            RaiseAll(nameof(IsIdle), nameof(CanImport));
            RefreshCommands();
        }
    }

    public bool IsIdle => !_isImporting;

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>Why the import failed; null when fine.</summary>
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

    /// <summary>Raised when the import succeeded and the dialog may close.</summary>
    public event EventHandler? RequestClose;

    /// <summary>True when Import would run.</summary>
    public bool CanImport => !_isImporting && !_ended && Problem is null;

    /// <summary>Why Import is disabled, or null.</summary>
    public string? Problem
    {
        get
        {
            if (_loadError is not null) return _loadError;
            if (_doc is null) return "Reading the file…";
            if (Animations.Count == 0) return "The file has no animations to import.";
            if (!Animations.Any(a => a.IsChecked)) return "Tick at least one animation.";
            if (_targetLoading) return "Loading the target mesh…";
            if (_targetSkeleton is null) return "Pick the target mesh.";
            if (BoneMap.Map is null) return "Mapping the bones…";
            if (BoneMap.HasErrors) return "Fix the bone map's errors (listed under the table) first.";
            if (_referenceLoading) return "Loading the reference clip…";
            return null;
        }
    }

    private void RefreshCommands()
    {
        ImportCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        BrowseTargetCommand.RaiseCanExecuteChanged();
    }

    private void Schedule()
    {
        if (_ended) return;
        RaiseAll(nameof(CanImport), nameof(Problem));
        RefreshCommands();
        if (_doc is null) return;
        _preview.Schedule();
    }

    /// <summary>Waits until the file, the target, the reference clip and the preview have all settled.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 120_000)
    {
        await Ready.ConfigureAwait(true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while ((_targetLoading || _referenceLoading || _preview.IsPending) && !_ended)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    private async Task LoadAsync(GltfDocument? preloaded)
    {
        try
        {
            var doc = preloaded ?? await GltfText.ReadAsync(FilePath).ConfigureAwait(true);
            if (_ended) return;
            GltfSourceSkeleton source;
            var rows = new List<GltfAnimationRow>();
            lock (_docGate)
            {
                source = GltfAnimationImport.SourceSkeleton(doc);
                var names = GltfAnimationImport.ListAnimations(doc);
                for (int i = 0; i < doc.Animations.Count; i++)
                {
                    var a = doc.Animations[i];
                    int ticks = (int)Math.Round(GltfText.AnimationSeconds(doc, a) * RfaClip.TicksPerSecond);
                    bool extras = a.Extras is JsonObject o && o.ContainsKey(GltfExtras.StartTime);
                    rows.Add(new GltfAnimationRow(i, names[i], TimeFormat.Format(ticks, Unit), a.Channels.Count, extras));
                }
            }
            _doc = doc;
            _source = source;
            foreach (var row in rows)
            {
                row.IsChecked = true;
                row.CheckedChanged += (_, _) =>
                {
                    Raise(nameof(AnimationsSummary));
                    RaiseAll(nameof(CanImport), nameof(Problem));
                    RefreshCommands();
                };
                Animations.Add(row);
            }
            SourceSummary = string.Join(" · ", new[]
            {
                GltfText.Count(doc.Animations.Count, "animation"),
                source.Nodes.Length > 0 ? $"{GltfText.Count(source.Nodes.Length, "joint")} in the skeleton" : "no skeleton nodes",
                doc.Meshes.Count > 0 ? GltfText.Count(doc.Meshes.Count, "mesh", "meshes") : null,
                string.IsNullOrWhiteSpace(doc.Asset.Generator) ? null : $"made by {doc.Asset.Generator}",
            }.Where(s => s is not null));
            Raise(nameof(AnimationsSummary));
            _selectedAnimation = Animations.FirstOrDefault();
            RaiseAll(nameof(SelectedAnimation), nameof(HasRfExtras));
            ChooseDefaultTarget(source);
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            LoadError = $"{FileName} could not be read: {GltfText.UserMessage(ex)}";
            SourceSummary = "The file could not be read.";
        }
        finally
        {
            _ready.TrySetResult();
            RaiseAll(nameof(CanImport), nameof(Problem));
            RefreshCommands();
        }
    }

    private List<GltfTargetChoice> BuildTargets(GltfTargetChoice? extra = null)
    {
        var list = new List<GltfTargetChoice>();
        if (extra is not null) list.Add(extra);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Open documents: the active one first.
        var docs = Shell.Documents.ToList();
        if (Shell.ActiveDocument is { } active && docs.Remove(active)) docs.Insert(0, active);
        foreach (var d in docs)
        {
            switch (d)
            {
                case MeshDocumentViewModel { HasSkeleton: true } m:
                {
                    var doc = m;
                    var mesh = m.Current;
                    list.Add(new GltfTargetChoice(m.DisplayName, ReferenceEquals(d, Shell.ActiveDocument) ? "active tab" : "open tab",
                        $"{m.DisplayName} as it is now in its tab ({GltfText.Count(m.Scene.Skeleton.Count, "bone")})", _ => Task.FromResult(mesh),
                        doc.Scene.TextureResolver, doc.Folder));
                    seen.Add(m.DisplayName);
                    break;
                }
                case ClipDocumentViewModel { PreviewMesh: { } preview } c when Skeleton.FromFile(preview).Count > 0:
                {
                    string name = c.Scene.MeshName ?? "preview mesh";
                    if (!seen.Add(name)) break;
                    list.Add(new GltfTargetChoice(name, $"preview of {c.DisplayName}",
                        $"{name}, the preview mesh of {c.DisplayName}", _ => Task.FromResult(preview), c.Scene.TextureResolver, null));
                    break;
                }
            }
        }
        var assets = Shell.Assets;
        foreach (var m in assets.Snapshot.Meshes.Where(m => m.HasSkeleton).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var entry = m;
            string? folder = entry.Location.FilePath is { } p ? Path.GetDirectoryName(p) : null;
            list.Add(new GltfTargetChoice(entry.Name, GltfText.Count(entry.BoneCount, "bone"), $"{entry.Name} · {entry.BoneCount} bones · {entry.Location.DisplayLocation}",
                ct => assets.LoadMeshAsync(entry, ct), assets.ResolverFor(folder), folder));
        }
        return list;
    }

    private void ChooseDefaultTarget(GltfSourceSkeleton source)
    {
        var all = _targets.All;
        // The active document's mesh (a mesh tab, or a clip tab's preview mesh) when it has a skeleton.
        GltfTargetChoice? pick = null;
        if (Shell.ActiveDocument is MeshDocumentViewModel { HasSkeleton: true } || Shell.ActiveDocument is ClipDocumentViewModel { PreviewMesh: not null })
            pick = all.FirstOrDefault(t => t.Note is "active tab" || t.Note.StartsWith("preview of", StringComparison.Ordinal));
        // Else a library mesh named like the file ("ult2_guard.glb" → ult2_guard.v3c), else one with as many bones as the file's skeleton.
        string stem = Path.GetFileNameWithoutExtension(FileName);
        pick ??= all.FirstOrDefault(t => string.Equals(Path.GetFileNameWithoutExtension(t.MeshName), stem, StringComparison.OrdinalIgnoreCase));
        if (pick is null && source.Nodes.Length > 0)
        {
            var mesh = Shell.Assets.Snapshot.Meshes.FirstOrDefault(m => m.BoneCount == source.Nodes.Length);
            if (mesh is not null) pick = all.FirstOrDefault(t => string.Equals(t.MeshName, mesh.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (pick is not null) _targets.Selected = pick;
        else
        {
            BoneMap.Reset("Pick the target mesh to map the bones.");
            PreviewMessage = "Pick the target mesh: the clip is laid out for its skeleton.";
        }
    }

    /// <summary>Selects a target by mesh name (tests and diagnostics). Returns false when it is not offered.</summary>
    public bool SelectTarget(string meshName)
    {
        var pick = _targets.All.FirstOrDefault(t => string.Equals(t.MeshName, meshName, StringComparison.OrdinalIgnoreCase));
        if (pick is null) return false;
        _targets.Selected = pick;
        return true;
    }

    /// <summary>Uses a mesh made in memory as the target (tests and diagnostics).</summary>
    public void UseTarget(V3dFile mesh, string name, string? textureFolder = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var choice = new GltfTargetChoice(name, "in memory", name, _ => Task.FromResult(mesh),
            textureFolder is null ? null : Shell.Assets.ResolverFor(textureFolder), textureFolder);
        ReplaceTargets(choice);
    }

    private void ReplaceTargets(GltfTargetChoice extra)
    {
        _targets.SelectionChanged -= OnTargetSelected;
        var list = new FilteredList<GltfTargetChoice>(BuildTargets(extra), t => t.MeshName);
        list.SelectionChanged += OnTargetSelected;
        Targets = list;
        list.Selected = extra;
    }

    private void BrowseTarget()
    {
        string? folder = _targetChoice?.TextureFolder ?? Path.GetDirectoryName(FilePath);
        var paths = Shell.Dialogs.OpenFiles(folder, "Choose the target mesh", "Character meshes (*.v3c)|*.v3c|All files (*.*)|*.*", false);
        if (paths.Length == 0) return;
        string path = paths[0];
        string name = Path.GetFileName(path);
        var choice = new GltfTargetChoice(name, "file", path, async ct =>
        {
            using var busy = BusyTracker.Begin("read " + name);
            return await Task.Run(() => V3dReader.ReadFile(path), ct).ConfigureAwait(false);
        }, Shell.Assets.ResolverFor(Path.GetDirectoryName(path)), Path.GetDirectoryName(path));
        ReplaceTargets(choice);
    }

    private async void OnTargetSelected(object? sender, EventArgs e)
    {
        if (_targets.Selected is not { } choice || ReferenceEquals(choice, _targetChoice) && _targetSkeleton is not null) return;
        int generation = ++_targetGeneration;
        _targetChoice = choice;
        _targetLoading = true;
        RaiseAll(nameof(TargetText), nameof(IsBusy), nameof(CanImport), nameof(Problem));
        RefreshCommands();
        using var busy = BusyTracker.Begin("glTF import target");
        try
        {
            var mesh = await choice.Load(CancellationToken.None).ConfigureAwait(true);
            if (generation != _targetGeneration || _ended) return;
            var skeleton = Skeleton.FromFile(mesh);
            if (skeleton.Count == 0) throw new ArgumentException($"{choice.MeshName} has no skeleton: an animation needs a character mesh (.v3c).");
            Cairn.Rfa.Retarget.BoneMap map;
            var doc = _doc;
            if (doc is null) return;
            lock (_docGate) map = GltfAnimationImport.MapBones(doc, skeleton);
            _targetMesh = mesh;
            _targetSkeleton = skeleton;
            _targetLoading = false;
            Preview.SetMesh(mesh, choice.MeshName, choice.Textures);
            GltfText.FrameSoon(Preview);
            BuildReferences(skeleton.Count, choice.MeshName);
            BoneMap.Load(map, () =>
            {
                lock (_docGate) return GltfAnimationImport.MapBones(doc, skeleton);
            });
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            if (generation != _targetGeneration) return;
            _targetMesh = null;
            _targetSkeleton = null;
            _targetLoading = false;
            Preview.SetMesh(null, null, null);
            Preview.SetClip(null);
            BoneMap.Reset("No target skeleton.");
            PreviewMessage = $"{choice.MeshName} cannot be used: {GltfText.UserMessage(ex)}";
        }
        finally
        {
            if (generation == _targetGeneration)
            {
                _targetLoading = false;
                RaiseAll(nameof(TargetText), nameof(TargetSkeleton), nameof(TargetMesh), nameof(IsBusy), nameof(CanImport), nameof(Problem));
                RefreshCommands();
                Schedule();
            }
        }
    }

    private void BuildReferences(int bones, string meshName)
    {
        var snapshot = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        var library = snapshot.FindMesh(meshName);
        var preferred = library is { } m && m.BoneCount == bones ? snapshot.DefaultPreviewClip(m.Name, usage) : null;
        var list = new List<GltfReferenceChoice> { NoReference };
        list.AddRange(snapshot.CompatibleClips(bones)
            .OrderBy(c => ReferenceEquals(c, preferred) ? 0 : c.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new GltfReferenceChoice(c, c.BaseName, ReferenceEquals(c, preferred) ? "tables: stand" : $"{c.BoneCount} bones")));
        _references.SelectionChanged -= OnReferenceSelected;
        var refs = new FilteredList<GltfReferenceChoice>(list, r => r.Label) { Selected = NoReference };
        refs.SelectionChanged += OnReferenceSelected;
        References = refs;
        _referenceClip = null;
        _referenceGeneration++;
        _referenceLoading = false;
    }

    private async void OnReferenceSelected(object? sender, EventArgs e)
    {
        int generation = ++_referenceGeneration;
        if (_references.Selected?.Clip is not { } clip)
        {
            _referenceClip = null;
            _referenceLoading = false;
            Schedule();
            return;
        }
        _referenceLoading = true;
        RaiseAll(nameof(IsBusy), nameof(CanImport), nameof(Problem));
        try
        {
            var loaded = await Shell.Assets.LoadClipAsync(clip).ConfigureAwait(true);
            if (generation != _referenceGeneration) return;
            _referenceClip = loaded;
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            if (generation != _referenceGeneration) return;
            _referenceClip = null;
            PreviewMessage = $"{clip.Name} could not be read: {GltfText.UserMessage(ex)}";
        }
        finally
        {
            if (generation == _referenceGeneration)
            {
                _referenceLoading = false;
                RaiseAll(nameof(IsBusy), nameof(CanImport), nameof(Problem));
                Schedule();
            }
        }
    }

    // ── Preview ─────────────────────────────────────────────────────────────

    private Func<CancellationToken, GltfAnimationPreview> PreparePreview()
    {
        var doc = _doc;
        GltfAnimationPreview Message(string m) => new(null, m);
        if (doc is null) return _ => Message("Reading the file…");
        if (Animations.Count == 0) return _ => Message("The file has no animations.");
        if (_targetLoading) return _ => Message("Loading the target mesh…");
        if (_targetSkeleton is not { } skeleton) return _ => Message("Pick the target mesh: the clip is laid out for its skeleton.");
        if (BoneMap.Map is null) return _ => Message("Mapping the bones…");
        if (BoneMap.HasErrors) return _ => Message("The bone map has errors (listed under the table): fix them to see the preview.");
        if (_referenceLoading) return _ => Message("Loading the reference clip…");
        var row = _selectedAnimation ?? Animations[0];
        var options = OptionsFor(row.Index);
        string file = FileName;
        var gate = _docGate;
        return ct =>
        {
            ct.ThrowIfCancellationRequested();
            System.Collections.Immutable.ImmutableArray<GltfImportedClip> result;
            lock (gate) result = GltfAnimationImport.Import(doc, skeleton, options, file);
            ct.ThrowIfCancellationRequested();
            return new GltfAnimationPreview(result.FirstOrDefault(), null);
        };
    }

    private void OnPreview(GltfAnimationPreview preview)
    {
        _imported = preview.Imported;
        Raise(nameof(Imported));
        if (preview.Imported is not { } imported)
        {
            PreviewMessage = preview.Message;
            Preview.SetClip(null);
            ReportRows = [];
            ReportSummary = string.Empty;
            Warnings = [];
            return;
        }
        PreviewMessage = null;
        Preview.SetClip(imported.Clip);
        var report = imported.Report;
        ReportRows = [.. report.Bones.Select(b => new GltfBoneReportRow(b.Bone, b.Name, b.SourceName ?? "—", b.Mode, b.RotationKeys, b.PositionKeys))];
        var modes = report.Bones.GroupBy(b => b.Mode).OrderBy(g => g.Key).Select(g => $"{GltfText.Count(g.Count(), "bone")} {ModeSummary(g.Key)}");
        var clip = imported.Clip;
        ReportSummary = $"{imported.Name}: {string.Join(" · ", modes)} · {GltfText.N(report.RotationKeys)} rotation and {GltfText.N(report.PositionKeys)} position keys · "
            + $"{TimeFormat.Format(clip.StartTime, Unit)} to {TimeFormat.Format(clip.EndTime, Unit)} ({TimeFormat.Duration(clip.Duration)}) · version {clip.Version}"
            + (report.UsedRfExtras ? " · timing from the file's RF extras" : "");
        var notes = new List<string>(report.Warnings);
        if (report.UnusedNodes.Length > 0)
            notes.Add($"Animated nodes no bone follows (their motion is dropped): {string.Join(", ", report.UnusedNodes)}");
        Warnings = notes;
    }

    private static string ModeSummary(GltfBoneImportMode mode) => mode switch
    {
        GltfBoneImportMode.Restored => "restored exactly",
        GltfBoneImportMode.Keys => "with converted keys",
        GltfBoneImportMode.Resampled => "resampled",
        GltfBoneImportMode.RestPose => "in the bind pose",
        GltfBoneImportMode.ReferencePose => "in the reference pose",
        _ => mode.ToString(),
    };

    private void OnPreviewFailed(string message)
    {
        _imported = null;
        Raise(nameof(Imported));
        PreviewMessage = message;
        Preview.SetClip(null);
        ReportRows = [];
        ReportSummary = string.Empty;
        Warnings = [];
    }

    // ── Import ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Imports every ticked animation (off the UI thread, Stop cancels between animations) and opens each
    /// as a new unsaved clip document on the target mesh. Returns the documents opened (empty when
    /// refused, failed or stopped).
    /// </summary>
    public async Task<IReadOnlyList<ClipDocumentViewModel>> ImportAsync()
    {
        if (!CanImport || _doc is not { } doc || _targetSkeleton is not { } skeleton || _targetMesh is not { } mesh || _targetChoice is not { } target)
            return [];
        var rows = Animations.Where(a => a.IsChecked).ToList();
        var cts = new CancellationTokenSource();
        _importCts = cts;
        var token = cts.Token;
        Error = null;
        IsImporting = true;
        using var busy = BusyTracker.Begin("glTF animation import");
        var clips = new List<GltfImportedClip>();
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                ProgressText = $"Importing {rows[i].Name} ({i + 1} of {rows.Count})…";
                var options = OptionsFor(rows[i].Index);
                string file = FileName;
                var result = await Task.Run(() =>
                {
                    lock (_docGate) return GltfAnimationImport.Import(doc, skeleton, options, file);
                }, token).ConfigureAwait(true);
                clips.AddRange(result);
            }
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Stopped. No clips were opened.";
            IsImporting = false;
            _importCts = null;
            return [];
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            Error = "The import failed: " + GltfText.UserMessage(ex);
            ProgressText = string.Empty;
            IsImporting = false;
            _importCts = null;
            return [];
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("glTF animation import", ex);
            Error = GltfText.Unexpected(ex);
            ProgressText = string.Empty;
            IsImporting = false;
            _importCts = null;
            return [];
        }

        var opened = new List<ClipDocumentViewModel>();
        foreach (var imported in clips)
            opened.Add(Shell.OpenNewClip(imported.Clip, GltfText.SafeFileName(imported.Name, "imported"), mesh, target.MeshName, target.TextureFolder));
        ProgressText = $"Opened {GltfText.Count(opened.Count, "new clip")}.";
        IsImporting = false;
        _importCts = null;
        End();
        RequestClose?.Invoke(this, EventArgs.Empty);
        return opened;
    }

    /// <summary>Ends the dialog's live work (every close path). Idempotent.</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _importCts?.Cancel();
        _preview.Stop();
        _targetGeneration++;
        _referenceGeneration++;
        _targetLoading = false;
        _referenceLoading = false;
        Preview.Dispose();
        RaiseAll(nameof(CanImport), nameof(IsBusy));
        RefreshCommands();
    }
}
