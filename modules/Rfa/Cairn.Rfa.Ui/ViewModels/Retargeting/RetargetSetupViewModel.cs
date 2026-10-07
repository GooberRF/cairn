using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Tbl;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Retarget;

namespace Cairn.Rfa.Ui.ViewModels.Retargeting;

/// <summary>A mesh a rig can come from: a library entry, a file on disk, or one already in memory.</summary>
/// <param name="Library">The library entry, or null.</param>
/// <param name="FilePath">A file picked from disk, or null.</param>
/// <param name="Memory">A mesh already loaded (the active clip's preview mesh outside the library), or null.</param>
/// <param name="Name">The mesh's file name.</param>
/// <param name="Note">"25 bones · miner family".</param>
public sealed record RigMeshOption(LibraryMesh? Library, string? FilePath, V3dFile? Memory, string Name, string Note)
{
    /// <summary>The "use the mesh itself" entry of a rest-mesh picker.</summary>
    public bool IsSelf => Library is null && FilePath is null && Memory is null;

    public string Label => IsSelf ? "(the mesh itself)" : Name;

    public string ToolTip => IsSelf
        ? "Measure against the mesh's own bind pose"
        : Library is not null ? $"{Name} · {Note} · {Library.Location.DisplayLocation}" : FilePath ?? $"{Name} · {Note}";

    /// <summary>The folder the mesh's textures resolve from first.</summary>
    public string? Folder => FilePath is not null ? Path.GetDirectoryName(FilePath) : Library?.Location.FilePath is { } p ? Path.GetDirectoryName(p) : null;

    public override string ToString() => Label;
}

/// <summary>A rig profile in a picker: a built-in, the generic one generated for the skeleton, or one loaded from a file.</summary>
/// <param name="Profile">The profile (null = generate one for the skeleton).</param>
/// <param name="Label">"female — rig B: multi_female AnimType…".</param>
public sealed record ProfileOption(RigProfile? Profile, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A library clip (or none) in the reference-clip picker.</summary>
public sealed record ReferenceClipOption(LibraryClip? Clip, string Label, string Note)
{
    public string ToolTip => Clip is null ? Label : $"{Clip.Name} · {Clip.Location.DisplayLocation}";

    public override string ToString() => Label;
}

/// <summary>
/// A preset in the setup's first choice: one of <see cref="RetargetPresets.All"/>, "Automatic (per clip)"
/// (batch only) or "Custom" (shown only while the options match no preset). Radio-button row.
/// </summary>
public sealed class PresetOption : ObservableObject
{
    private readonly Action<PresetOption> _select;
    private bool _isSelected;
    private bool _isVisible = true;

    internal PresetOption(RetargetPreset preset, bool automatic, Action<PresetOption> select)
    {
        Preset = preset;
        IsAutomatic = automatic;
        _select = select;
        _isVisible = preset != RetargetPreset.Custom;
    }

    /// <summary>The preset (<see cref="RetargetPreset.Locomotion"/> for the automatic row: what its options show).</summary>
    public RetargetPreset Preset { get; }

    /// <summary>True for "Automatic (per clip)".</summary>
    public bool IsAutomatic { get; }

    public bool IsCustom => !IsAutomatic && Preset == RetargetPreset.Custom;

    public string Title => IsAutomatic ? "Automatic (per clip)" : RetargetPresets.Title(Preset);

    public string Description => IsAutomatic
        ? "Each clip gets Seated when the tables play it in a vehicle seat or on a turret (or its name says so), Rotation only when it is a swim, Standing / locomotion otherwise. The results table says which was used."
        : RetargetPresets.Description(Preset);

    /// <summary>The radio button; checking it applies the preset.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (value && !_isSelected) _select(this);
        }
    }

    internal void SetSelected(bool value) => Set(ref _isSelected, value, nameof(IsSelected));

    /// <summary>False for the Custom row while a preset matches.</summary>
    public bool IsVisible { get => _isVisible; internal set => Set(ref _isVisible, value); }

    public override string ToString() => Title;
}

/// <summary>One IK chain of the target profile: on/off and its pole bias (metres, model space).</summary>
public sealed class IkChainRow : ObservableObject
{
    private readonly Action _changed;
    private bool _enabled = true;
    private double _x, _y, _z;

    internal IkChainRow(IkChain chain, bool enabled, Action changed)
    {
        Chain = chain;
        _enabled = enabled;
        _x = Math.Round(chain.PoleBias.X, 3);
        _y = Math.Round(chain.PoleBias.Y, 3);
        _z = Math.Round(chain.PoleBias.Z, 3);
        _changed = changed;
    }

    /// <summary>The chain as the profile defines it.</summary>
    public IkChain Chain { get; }

    /// <summary>"upperarm-l → lowerarm-l → hand-l".</summary>
    public string Label => $"{Chain.Upper} → {Chain.Lower} → {Chain.End}";

    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) _changed(); } }

    public double PoleX { get => _x; set { if (Set(ref _x, value)) _changed(); } }

    public double PoleY { get => _y; set { if (Set(ref _y, value)) _changed(); } }

    public double PoleZ { get => _z; set { if (Set(ref _z, value)) _changed(); } }

    /// <summary>The chain with the edited pole bias.</summary>
    public IkChain Edited => Chain with { PoleBias = new Vector3((float)_x, (float)_y, (float)_z) };
}

/// <summary>Everything one retarget (or a batch) is set up with, ready to run: rigs, map, options.</summary>
/// <param name="Source">The source rig (its rest mesh's skeleton and profile).</param>
/// <param name="Target">The target rig (with its reference clip).</param>
/// <param name="Map">The bone map.</param>
/// <param name="Options">The retarget options.</param>
/// <param name="TargetMesh">The target mesh (the preview, and the result's preview mesh).</param>
/// <param name="TargetMeshName">Its file name.</param>
/// <param name="TargetFolder">The folder its textures resolve from, or null.</param>
/// <param name="SourceMesh">The source mesh (the side-by-side preview).</param>
/// <param name="SourceMeshName">Its file name.</param>
/// <param name="SourceFolder">The folder its textures resolve from, or null.</param>
public sealed record RetargetInputs(
    RetargetRig Source, RetargetRig Target, BoneMap Map, RetargetOptions Options,
    V3dFile TargetMesh, string TargetMeshName, string? TargetFolder,
    V3dFile SourceMesh, string SourceMeshName, string? SourceFolder);

/// <summary>
/// The part of the retarget dialog and the batch dialog that says HOW to retarget: the source mesh (and
/// its T-pose rest mesh), the target mesh (rest override with the T-pose advice, reference clip for bone
/// lengths), both rig profiles (built-ins chosen by <see cref="RigProfiles.FindFor(Skeleton)"/>, else
/// generic; Save/Load profile as JSON), the bone map (<see cref="BoneMapEditorViewModel"/>), the preset
/// (<see cref="Presets"/>: Seated / Standing / Rotation only, "Automatic (per clip)" in the batch, Custom once
/// an option is edited; <see cref="SuggestPresetFor"/> picks one per clip until the user chooses), the source's
/// reference (stand) clip for the hip-height ground, and every <see cref="RetargetOptions"/> setting under the
/// preset. Meshes and clips load off the UI thread; <see cref="Changed"/>
/// fires whenever the result would differ, and <see cref="Inputs"/> is then the new set (or null with
/// <see cref="NotReadyReason"/>).
/// </summary>
public sealed class RetargetSetupViewModel : ObservableObject
{
    private const string LastTargetKey = "rfa.retargetLastTarget";
    private const string ProfileFolderKey = "rfa.retargetProfileFolder";

    private readonly RfaWorkspace _shell;
    private readonly List<RigMeshOption> _extraMeshes = [];
    private readonly List<ProfileOption> _customProfiles = [];
    private RigMeshOption? _sourceMesh, _sourceRest, _targetMesh, _targetRest;
    private ProfileOption? _sourceProfile, _targetProfile;
    private ReferenceClipOption? _referenceClip, _sourceReferenceClip;
    private V3dFile? _sourceMeshFile, _sourceRestFile, _targetMeshFile, _targetRestFile;
    private RfaClip? _referenceClipFile, _sourceReferenceClipFile;
    private Skeleton? _sourceSkeleton, _targetSkeleton;
    private RigProfile? _resolvedSourceProfile, _resolvedTargetProfile;
    private RetargetInputs? _inputs;
    private string? _notReady = "Choose a source mesh and a target mesh.";
    private string? _loadError;
    private int _loading;
    private int _loadGeneration;
    private BoneMap? _pendingProfileMap;
    private bool _suspend;

    // Options
    private bool _restAlignment = true, _ik = true, _ikArms = true, _ikLegs = true, _scaleStride, _offHand, _resample;
    private double _resampleFps = 30;
    private RootMode _rootMode = RootMode.AnchorPelvis;
    private BoneLengthSource _boneLengths = BoneLengthSource.ReferenceClip;
    private ExtraBonePose _extraPose = ExtraBonePose.ReferenceClip;
    private KeyQuantization _quantization = KeyQuantization.WithinUnit;

    // Presets
    private PresetOption? _preset;
    private bool _presetChosen;
    private PresetSuggestion? _suggestion;
    private string? _suggestedFor;

    /// <param name="shell">The shell (library, settings, dialogs).</param>
    /// <param name="sourceMeshName">The source mesh to start with (the clip's preview mesh), or null.</param>
    /// <param name="sourceMemory">That mesh in memory when it is not in the library, or null.</param>
    /// <param name="allowAutomatic">Offer "Automatic (per clip)" as the first preset (the batch dialog), and start on it.</param>
    public RetargetSetupViewModel(RfaWorkspace shell, string? sourceMeshName, V3dFile? sourceMemory = null, bool allowAutomatic = false)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Map = new BoneMapEditorViewModel();
        Map.MapChanged += (_, _) => Recompose();
        if (allowAutomatic) Presets.Add(new PresetOption(RetargetPreset.Locomotion, automatic: true, p => SelectPreset(p, byUser: true)));
        foreach (var preset in RetargetPresets.All) Presets.Add(new PresetOption(preset, automatic: false, p => SelectPreset(p, byUser: true)));
        Presets.Add(new PresetOption(RetargetPreset.Custom, automatic: false, p => SelectPreset(p, byUser: true)));
        _preset = Presets[0];
        _preset.SetSelected(true);
        if (allowAutomatic) LoadOptions(RetargetPresets.Options(RetargetPreset.Locomotion));
        RootModes =
        [
            new(RootMode.AnchorPelvis, "Anchor the pelvis", "The target's pelvis lands where the source's was (a seated rider stays on the seat). The reference's rule."),
            new(RootMode.HipHeight, "Hip height above the ground", "The target's hips stand as high above its own ground as the source's above theirs, scaled by the leg length ratio; feet held by leg IK stay on the target's ground (standing and locomotion)."),
            new(RootMode.CopySource, "Copy the source root", "The root's position keys are copied as they are."),
            new(RootMode.KeepInPlace, "Keep in place", "The root holds the target's own offset for the whole clip (no travel)."),
        ];
        BoneLengthSources =
        [
            new(BoneLengthSource.ReferenceClip, "Target reference clip", "Each bone's offset from the target's stand clip: the AnimType's own proportions (what the game's other clips for this rig use)."),
            new(BoneLengthSource.TargetBind, "Target bind pose", "The offsets of the target mesh's bind pose."),
            new(BoneLengthSource.Source, "Source clip", "The source clip's own offsets: the source's proportions on the target's skeleton."),
        ];
        ExtraPoses =
        [
            new(ExtraBonePose.ReferenceClip, "Reference clip pose", "Bones the source has no counterpart for hold the first pose of the reference clip."),
            new(ExtraBonePose.Bind, "Bind pose", "Bones the source has no counterpart for hold their bind pose relative to their parent."),
        ];
        Quantizations =
        [
            new(KeyQuantization.WithinUnit, "Within unit length (recommended)", "Keys are rounded so none is longer than 1: the game interpolates every segment."),
            new(KeyQuantization.Reference, "Reference (original tool)", "Rounds half to even as the reference tool did, reproducing its files byte for byte; a few slow segments then snap in game."),
        ];

        SaveProfileCommand = new RelayCommand(SaveProfile, () => _inputs is not null);
        LoadProfileCommand = new RelayCommand(LoadProfile);
        BrowseSourceCommand = new RelayCommand(() => Browse(source: true));
        BrowseTargetCommand = new RelayCommand(() => Browse(source: false));

        if (sourceMemory is not null && !string.IsNullOrWhiteSpace(sourceMeshName) && shell.FindLibraryMesh(sourceMeshName) is null)
            _extraMeshes.Add(new RigMeshOption(null, null, sourceMemory, sourceMeshName, $"{sourceMemory.Bones.Length} bones · in memory"));
        RebuildMeshOptions();
        _suspend = true;
        try
        {
            SelectedSourceMesh = Find(SourceMeshOptions, sourceMeshName) ?? SourceMeshOptions.FirstOrDefault();
            string? lastTarget = shell.Settings.Get<string>(LastTargetKey);
            SelectedTargetMesh = Find(TargetMeshOptions, lastTarget) is { } last && !SameName(last.Name, sourceMeshName) ? last
                : TargetMeshOptions.FirstOrDefault(m => !SameName(m.Name, sourceMeshName) && m.Library?.BoneCount != _sourceMesh?.Library?.BoneCount)
                ?? TargetMeshOptions.FirstOrDefault(m => !SameName(m.Name, sourceMeshName));
        }
        finally { _suspend = false; }
        LoadMeshes();
    }

    /// <summary>The shell.</summary>
    public RfaWorkspace Shell => _shell;

    /// <summary>Raised whenever <see cref="Inputs"/> changes (or becomes null).</summary>
    public event EventHandler? Changed;

    /// <summary>The bone map table.</summary>
    public BoneMapEditorViewModel Map { get; }

    // ── Meshes ──────────────────────────────────────────────────────────────

    /// <summary>Every character mesh: library, then files added with Browse.</summary>
    public ObservableCollection<RigMeshOption> SourceMeshOptions { get; } = [];

    public ObservableCollection<RigMeshOption> TargetMeshOptions { get; } = [];

    /// <summary>"(the mesh itself)", then meshes with the source's bone count.</summary>
    public ObservableCollection<RigMeshOption> SourceRestOptions { get; } = [];

    public ObservableCollection<RigMeshOption> TargetRestOptions { get; } = [];

    public ObservableCollection<ReferenceClipOption> ReferenceClipOptions { get; } = [];

    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];

    public RigMeshOption? SelectedSourceMesh
    {
        get => _sourceMesh;
        set
        {
            if (!Set(ref _sourceMesh, value)) return;
            _sourceMeshFile = null;
            RebuildRestOptions(source: true);
            RebuildReferenceOptions(source: true);
            if (!_suspend) LoadMeshes();
        }
    }

    public RigMeshOption? SelectedSourceRest
    {
        get => _sourceRest;
        set
        {
            if (!Set(ref _sourceRest, value)) return;
            _sourceRestFile = null;
            if (!_suspend) LoadMeshes();
        }
    }

    public RigMeshOption? SelectedTargetMesh
    {
        get => _targetMesh;
        set
        {
            if (!Set(ref _targetMesh, value)) return;
            _targetMeshFile = null;
            _targetProfile = null;
            if (value is not null && !_suspend) _shell.Settings.Set(LastTargetKey, value.Name);
            RebuildRestOptions(source: false);
            RebuildReferenceOptions(source: false);
            if (!_suspend) LoadMeshes();
        }
    }

    public RigMeshOption? SelectedTargetRest
    {
        get => _targetRest;
        set
        {
            if (!Set(ref _targetRest, value)) return;
            _targetRestFile = null;
            Raise(nameof(RestHint));
            if (!_suspend) LoadMeshes();
        }
    }

    public ReferenceClipOption? SelectedReferenceClip
    {
        get => _referenceClip;
        set
        {
            if (!Set(ref _referenceClip, value)) return;
            _referenceClipFile = null;
            if (!_suspend) LoadMeshes();
        }
    }

    /// <summary>"(none)", then clips with the source mesh's bone count, its stand clip first: where the source's ground is.</summary>
    public ObservableCollection<ReferenceClipOption> SourceReferenceClipOptions { get; } = [];

    /// <summary>The source rig's stand clip: its ground (lowest foot point) and ankle height for the hip-height root.</summary>
    public ReferenceClipOption? SelectedSourceReferenceClip
    {
        get => _sourceReferenceClip;
        set
        {
            if (!Set(ref _sourceReferenceClip, value)) return;
            _sourceReferenceClipFile = null;
            if (!_suspend) LoadMeshes();
        }
    }

    public ProfileOption? SelectedSourceProfile
    {
        get => _sourceProfile;
        set
        {
            if (!Set(ref _sourceProfile, value) || _suspend) return;
            RebuildRigs(remap: true);
        }
    }

    public ProfileOption? SelectedTargetProfile
    {
        get => _targetProfile;
        set
        {
            if (!Set(ref _targetProfile, value) || _suspend) return;
            RebuildRigs(remap: true);
        }
    }

    /// <summary>The source skeleton (its rest mesh's), or null while loading.</summary>
    public Skeleton? SourceSkeleton => _sourceSkeleton;

    /// <summary>The target skeleton (its rest mesh's), or null while loading.</summary>
    public Skeleton? TargetSkeleton => _targetSkeleton;

    /// <summary>The source mesh's bone count (what the source clips must have), or 0.</summary>
    public int SourceBoneCount => _sourceMeshFile?.Bones.Length ?? _sourceMesh?.Library?.BoneCount ?? 0;

    /// <summary>The profile the source resolved to.</summary>
    public RigProfile? SourceProfile => _resolvedSourceProfile;

    /// <summary>The profile the target resolved to (with edited pole biases).</summary>
    public RigProfile? TargetProfile => _resolvedTargetProfile;

    /// <summary>
    /// The T-pose advice of v3c_skeleton.md when the target profile names a rest mesh: retargeting measures
    /// against the rest mesh's bind, which should be a T-pose.
    /// </summary>
    public string RestHint
    {
        get
        {
            var profile = _resolvedTargetProfile;
            if (profile?.RestMesh is not { } rest) return "Retargeting measures limb directions against the rest mesh's bind pose: pick a mesh of this skeleton whose bind is a T-pose.";
            bool using_ = _targetRest is { IsSelf: false } r ? SameName(r.Name, rest) : SameName(_targetMesh?.Name, rest);
            return using_
                ? $"Using {rest}, the T-pose rest mesh of the {profile.Name} rig."
                : $"The {profile.Name} rig's T-pose rest mesh is {rest}. Stock binds disagree (some have the arms down), so measure against {rest} unless this mesh's bind is a T-pose too.";
        }
    }

    /// <summary>True while a mesh or clip is loading.</summary>
    public bool IsLoading => _loading > 0;

    /// <summary>What a run would use, or null (see <see cref="NotReadyReason"/>).</summary>
    public RetargetInputs? Inputs => _inputs;

    /// <summary>Why nothing can run yet, or null.</summary>
    public string? NotReadyReason
    {
        get => _notReady;
        private set => Set(ref _notReady, value);
    }

    public RelayCommand BrowseSourceCommand { get; }

    public RelayCommand BrowseTargetCommand { get; }

    public RelayCommand SaveProfileCommand { get; }

    public RelayCommand LoadProfileCommand { get; }

    // ── Options ─────────────────────────────────────────────────────────────

    public IReadOnlyList<ClipTools.Choice<RootMode>> RootModes { get; }

    public IReadOnlyList<ClipTools.Choice<BoneLengthSource>> BoneLengthSources { get; }

    public IReadOnlyList<ClipTools.Choice<ExtraBonePose>> ExtraPoses { get; }

    public IReadOnlyList<ClipTools.Choice<KeyQuantization>> Quantizations { get; }

    /// <summary>The target profile's IK chains.</summary>
    public ObservableCollection<IkChainRow> IkChains { get; } = [];

    // ── Presets ─────────────────────────────────────────────────────────────

    /// <summary>The first choice: (Automatic,) Seated, Standing / locomotion, Rotation only, (Custom).</summary>
    public ObservableCollection<PresetOption> Presets { get; } = [];

    /// <summary>The selected preset row.</summary>
    public PresetOption? SelectedPreset
    {
        get => _preset;
        set
        {
            if (value is not null) SelectPreset(value, byUser: true);
        }
    }

    /// <summary>True when "Automatic (per clip)" is selected (batch).</summary>
    public bool IsAutomatic => _preset?.IsAutomatic == true;

    /// <summary>The preset the current options make (<see cref="RetargetPreset.Custom"/> when edited away from every preset).</summary>
    public RetargetPreset CurrentPreset => IsAutomatic ? RetargetPreset.Locomotion : RetargetPresets.Identify(BuildOptions());

    /// <summary>"Suggested for park_jeep_driver.rfa: Seated / fixed controls — the tables play it as the jeep_drive state." or null.</summary>
    public string? SuggestionText => _suggestion is { } s && _suggestedFor is { } clip
        ? $"Suggested for {clip}: {RetargetPresets.Title(s.Preset)} ({s.Reason})."
        : null;

    /// <summary>
    /// Picks the preset for a clip (the dialog calls this whenever its source clip changes): the suggestion
    /// (<see cref="RetargetPresets.Suggest"/>) is applied unless the user has chosen a preset or edited an option.
    /// </summary>
    public void SuggestPresetFor(string clipName)
    {
        ArgumentNullException.ThrowIfNull(clipName);
        _suggestion = RetargetPresets.Suggest(clipName, _shell.Assets.Usage);
        _suggestedFor = Path.GetFileName(clipName);
        Raise(nameof(SuggestionText));
        if (_presetChosen || IsAutomatic) return;
        var row = Presets.FirstOrDefault(p => !p.IsAutomatic && p.Preset == _suggestion.Preset);
        if (row is not null) SelectPreset(row, byUser: false);
    }

    /// <summary>
    /// The options for one clip of a batch: with "Automatic (per clip)" the clip's suggested preset (keeping the
    /// output encoding and the two-handed grip set here), otherwise the options as set.
    /// </summary>
    public RetargetOptions OptionsFor(string clipName, out RetargetPreset preset)
    {
        var options = BuildOptions();
        if (!IsAutomatic)
        {
            preset = RetargetPresets.Identify(options);
            return options;
        }
        preset = RetargetPresets.Suggest(clipName, _shell.Assets.Usage).Preset;
        return PresetOptions(preset);
    }

    /// <summary>
    /// A preset's options keeping the output encoding set here; the two-handed grip is the user's explicit
    /// choice when they made one, else the preset's default (on for standing / locomotion).
    /// </summary>
    private RetargetOptions PresetOptions(RetargetPreset preset) =>
        RetargetPresets.Options(preset, BuildOptions()) with { OffHandFollowsMainHand = _offHandChoice ?? RetargetPresets.GripByDefault(preset) };

    private void SelectPreset(PresetOption row, bool byUser)
    {
        if (byUser) _presetChosen = true;
        if (!row.IsCustom) LoadOptions(PresetOptions(row.IsAutomatic ? RetargetPreset.Locomotion : row.Preset));
        SetPresetRow(row);
        Recompose();
    }

    private void SetPresetRow(PresetOption row)
    {
        _preset = row;
        foreach (var p in Presets) p.SetSelected(ReferenceEquals(p, row));
        foreach (var p in Presets.Where(p => p.IsCustom)) p.IsVisible = row.IsCustom;
        RaiseAll(nameof(SelectedPreset), nameof(IsAutomatic), nameof(CurrentPreset));
    }

    /// <summary>An IK chain row's switch or pole bias changed.</summary>
    private void OnChainEdited()
    {
        if (_suspend) return;
        OnOptionEdited();
    }

    /// <summary>After the user edits an option: the preset row that now matches (Custom when none).</summary>
    private void OnOptionEdited()
    {
        if (_suspend) return;
        _presetChosen = true;
        var current = RetargetPresets.Identify(BuildOptions());
        // Automatic shows the locomotion options; an edit that keeps them (a pole bias) stays automatic.
        var row = IsAutomatic && current == RetargetPreset.Locomotion ? _preset! : Presets.First(p => !p.IsAutomatic && p.Preset == current);
        if (!ReferenceEquals(row, _preset)) SetPresetRow(row);
        Raise(nameof(CurrentPreset));
        Recompose();
    }

    // ── Option values ───────────────────────────────────────────────────────

    public bool RestAlignment { get => _restAlignment; set { if (Set(ref _restAlignment, value)) OnOptionEdited(); } }

    public bool Ik { get => _ik; set { if (Set(ref _ik, value)) OnOptionEdited(); } }

    /// <summary>With IK: run it on the arms.</summary>
    public bool IkArms { get => _ikArms; set { if (Set(ref _ikArms, value)) OnOptionEdited(); } }

    /// <summary>With IK: run it on the legs.</summary>
    public bool IkLegs { get => _ikLegs; set { if (Set(ref _ikLegs, value)) OnOptionEdited(); } }

    /// <summary>With the hip-height root: scale horizontal motion by the leg ratio too.</summary>
    public bool ScaleStride { get => _scaleStride; set { if (Set(ref _scaleStride, value)) OnOptionEdited(); } }

    /// <summary>
    /// Keep the off (left) hand on a two-handed weapon: held relative to the right hand while the source's hands
    /// are close. Goes with any preset; on by default with standing / locomotion. Once the user ticks or clears it,
    /// that choice holds for every preset (and every clip of an automatic batch).
    /// </summary>
    public bool OffHandFollowsMainHand
    {
        get => _offHand;
        set
        {
            if (!Set(ref _offHand, value)) return;
            if (_suspend) return;
            _offHandChoice = value;
            Recompose();
        }
    }

    /// <summary>The user's explicit grip choice (checkbox or a loaded profile), or null for the preset's default.</summary>
    private bool? _offHandChoice;

    public RootMode RootMode
    {
        get => _rootMode;
        set
        {
            if (!Set(ref _rootMode, value)) return;
            RaiseAll(nameof(RootModeDescription), nameof(IsHipHeight));
            OnOptionEdited();
        }
    }

    public string RootModeDescription => RootModes.First(c => c.Value == _rootMode).Note ?? string.Empty;

    /// <summary>True with the hip-height root (enables the stride option).</summary>
    public bool IsHipHeight => _rootMode == RootMode.HipHeight;

    public BoneLengthSource BoneLengths { get => _boneLengths; set { if (Set(ref _boneLengths, value)) { Raise(nameof(BoneLengthsDescription)); OnOptionEdited(); } } }

    public string BoneLengthsDescription => BoneLengthSources.First(c => c.Value == _boneLengths).Note ?? string.Empty;

    public ExtraBonePose ExtraPose { get => _extraPose; set { if (Set(ref _extraPose, value)) { Raise(nameof(ExtraPoseDescription)); OnOptionEdited(); } } }

    public string ExtraPoseDescription => ExtraPoses.First(c => c.Value == _extraPose).Note ?? string.Empty;

    public KeyQuantization Quantization { get => _quantization; set { if (Set(ref _quantization, value)) { Raise(nameof(QuantizationDescription)); Recompose(); } } }

    public string QuantizationDescription => Quantizations.First(c => c.Value == _quantization).Note ?? string.Empty;

    /// <summary>Resample every rotation track at a fixed rate instead of keeping the source's key times.</summary>
    public bool Resample { get => _resample; set { if (Set(ref _resample, value)) Recompose(); } }

    /// <summary>The resample rate in frames per second (30 = every 160 ticks).</summary>
    public double ResampleFps { get => _resampleFps; set { if (Set(ref _resampleFps, Math.Clamp(value, 1, 480))) Recompose(); } }

    /// <summary>The options as set.</summary>
    public RetargetOptions BuildOptions() => new()
    {
        RestAlignment = _restAlignment,
        Ik = _ik,
        IkArms = _ikArms,
        IkLegs = _ikLegs,
        DisabledIkChains = [.. IkChains.Where(c => !c.Enabled).Select(c => c.Chain.Upper)],
        RootMode = _rootMode,
        ScaleStride = _scaleStride,
        OffHandFollowsMainHand = _offHand,
        BoneLengths = _boneLengths,
        ExtraBonePose = _extraPose,
        ResampleStep = _resample ? Math.Max(1, (int)Math.Round(RfaClip.TicksPerSecond / _resampleFps)) : null,
        Quantization = _quantization,
    };

    /// <summary>Sets every option field from <paramref name="options"/> (a loaded profile, a self-test); the preset row follows.</summary>
    public void ApplyOptions(RetargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        LoadOptions(options);
        _offHandChoice = options.OffHandFollowsMainHand;
        _presetChosen = true;
        var current = RetargetPresets.Identify(BuildOptions());
        if (!(IsAutomatic && current == RetargetPreset.Locomotion)) SetPresetRow(Presets.First(p => !p.IsAutomatic && p.Preset == current));
        Recompose();
    }

    /// <summary>Writes every option field without treating it as a user edit (presets, profiles).</summary>
    private void LoadOptions(RetargetOptions options)
    {
        bool was = _suspend;
        _suspend = true;
        try
        {
            RestAlignment = options.RestAlignment;
            Ik = options.Ik;
            IkArms = options.IkArms;
            IkLegs = options.IkLegs;
            RootMode = options.RootMode;
            ScaleStride = options.ScaleStride;
            OffHandFollowsMainHand = options.OffHandFollowsMainHand;
            BoneLengths = options.BoneLengths;
            ExtraPose = options.ExtraBonePose;
            Quantization = options.Quantization;
            Resample = options.ResampleStep is not null;
            if (options.ResampleStep is { } step && step > 0) ResampleFps = RfaClip.TicksPerSecond / (double)step;
            var disabled = new HashSet<string>(options.DisabledIkChains.IsDefault ? [] : options.DisabledIkChains, StringComparer.OrdinalIgnoreCase);
            foreach (var row in IkChains) row.Enabled = !disabled.Contains(row.Chain.Upper);
        }
        finally { _suspend = was; }
    }

    // ── Loading ─────────────────────────────────────────────────────────────

    /// <summary>Waits (without blocking the UI thread) until nothing is loading.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 60_000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (_loading > 0)
        {
            if (clock.ElapsedMilliseconds > timeoutMs) return false;
            await Task.Delay(15).ConfigureAwait(true);
        }
        return true;
    }

    /// <summary>Selects meshes by name (self-tests, the batch dialog's "class" route).</summary>
    public void Select(string? sourceMesh, string? targetMesh)
    {
        _suspend = true;
        try
        {
            if (Find(SourceMeshOptions, sourceMesh) is { } s) SelectedSourceMesh = s;
            if (Find(TargetMeshOptions, targetMesh) is { } t) SelectedTargetMesh = t;
        }
        finally { _suspend = false; }
        LoadMeshes();
    }

    private async void LoadMeshes()
    {
        int generation = ++_loadGeneration;
        _loadError = null;
        _loading++;
        Raise(nameof(IsLoading));
        using var busy = BusyTracker.Begin("retarget setup");
        try
        {
            // Load into locals: an older load finishing late must not put its meshes in the fields.
            var sourceMesh = await Load(_sourceMesh).ConfigureAwait(true);
            var sourceRest = await Load(_sourceRest).ConfigureAwait(true);
            var targetMesh = await Load(_targetMesh).ConfigureAwait(true);
            var targetRest = await Load(_targetRest).ConfigureAwait(true);
            RfaClip? reference = null, sourceReference = null;
            if (_referenceClip?.Clip is { } refClip) reference = await _shell.Assets.LoadClipAsync(refClip).ConfigureAwait(true);
            if (_sourceReferenceClip?.Clip is { } srcRefClip) sourceReference = await _shell.Assets.LoadClipAsync(srcRefClip).ConfigureAwait(true);
            if (generation != _loadGeneration) return;
            _sourceMeshFile = sourceMesh;
            _sourceRestFile = sourceRest;
            _targetMeshFile = targetMesh;
            _targetRestFile = targetRest;
            _referenceClipFile = reference;
            _sourceReferenceClipFile = sourceReference;
            RebuildRigs(remap: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AssetFormatException or ArgumentException or InvalidOperationException)
        {
            if (generation != _loadGeneration) return;
            _loadError = "A mesh or clip could not be read: " + ex.Message;
            SetInputs(null, _loadError);
        }
        finally
        {
            _loading--;
            Raise(nameof(IsLoading));
        }
    }

    private readonly Dictionary<RigMeshOption, V3dFile> _meshCache = new(ReferenceEqualityComparer.Instance);

    private async Task<V3dFile?> Load(RigMeshOption? option)
    {
        if (option is null || option.IsSelf) return null;
        if (option.Memory is { } memory) return memory;
        if (_meshCache.TryGetValue(option, out var cached)) return cached;
        V3dFile file;
        if (option.Library is { } library) file = await _shell.Assets.LoadMeshAsync(library).ConfigureAwait(true);
        else
        {
            string path = option.FilePath!;
            file = await Task.Run(() => V3dReader.ReadFile(path)).ConfigureAwait(true);
        }
        _meshCache[option] = file;
        return file;
    }

    /// <summary>Rebuilds skeletons, profiles, IK rows and (when <paramref name="remap"/>) the automatic map.</summary>
    private void RebuildRigs(bool remap)
    {
        if (_loadError is not null) return;
        var sourceFile = _sourceRestFile ?? _sourceMeshFile;
        var targetFile = _targetRestFile ?? _targetMeshFile;
        if (_sourceMeshFile is null || _targetMeshFile is null || sourceFile is null || targetFile is null)
        {
            SetInputs(null, _loading > 0 ? "Loading…" : "Choose a source mesh and a target mesh.");
            return;
        }
        if (_sourceRestFile is { } srf && !SameBones(srf, _sourceMeshFile))
        {
            SetInputs(null, $"The source rest mesh {_sourceRest!.Name} does not have the same bones as {_sourceMesh!.Name}. Pick a mesh of the same skeleton, or the mesh itself.");
            return;
        }
        if (_targetRestFile is { } trf && !SameBones(trf, _targetMeshFile))
        {
            SetInputs(null, $"The target rest mesh {_targetRest!.Name} does not have the same bones as {_targetMesh!.Name}. Pick a mesh of the same skeleton, or the mesh itself.");
            return;
        }
        if (sourceFile.Bones.Length == 0 || targetFile.Bones.Length == 0)
        {
            SetInputs(null, "Both meshes need a skeleton: static meshes cannot play clips.");
            return;
        }

        var sourceSkeleton = Skeleton.FromFile(sourceFile);
        var targetSkeleton = Skeleton.FromFile(targetFile);
        bool skeletonsChanged = !SameSkeleton(sourceSkeleton, _sourceSkeleton) || !SameSkeleton(targetSkeleton, _targetSkeleton);
        _sourceSkeleton = sourceSkeleton;
        _targetSkeleton = targetSkeleton;

        RebuildProfileOptions(sourceSkeleton, targetSkeleton);
        _resolvedSourceProfile = _sourceProfile?.Profile ?? RigProfile.Generic(sourceSkeleton);
        var baseTarget = _targetProfile?.Profile ?? RigProfile.Generic(targetSkeleton);
        if (skeletonsChanged || IkChains.Count == 0 || !IkChains.Select(c => c.Chain.Upper).SequenceEqual(baseTarget.IkChains.Select(c => c.Upper)))
        {
            var disabled = IkChains.Where(c => !c.Enabled).Select(c => c.Chain.Upper).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IkChains.Clear();
            foreach (var chain in baseTarget.IkChains) IkChains.Add(new IkChainRow(chain, !disabled.Contains(chain.Upper), OnChainEdited));
        }
        _resolvedTargetProfile = baseTarget;
        RaiseAll(nameof(SourceSkeleton), nameof(TargetSkeleton), nameof(SourceProfile), nameof(TargetProfile), nameof(RestHint), nameof(SourceBoneCount));

        if (remap || Map.Map is null || !Map.Map.Fits(sourceSkeleton.Names, targetSkeleton.Names))
        {
            var src = sourceSkeleton;
            var tgt = targetSkeleton;
            var sp = _resolvedSourceProfile;
            var tp = _resolvedTargetProfile;
            BoneMap Auto() => BoneMapper.Map(src, sp, tgt, tp);
            BoneMap map;
            if (_pendingProfileMap is { } loaded && loaded.Fits(src.Names, tgt.Names))
            {
                map = loaded;
                _pendingProfileMap = null;
            }
            else
            {
                try { map = Auto(); }
                catch (Exception ex) when (ex is ArgumentException or FormatException)
                {
                    SetInputs(null, "The bones could not be mapped: " + ex.Message);
                    return;
                }
            }
            Map.Load(map, Auto);   // raises MapChanged → Recompose
        }
        else
        {
            Recompose();
        }
    }

    /// <summary>Builds <see cref="Inputs"/> from the loaded meshes, the map and the options.</summary>
    private void Recompose()
    {
        if (_suspend) return;
        if (_sourceSkeleton is null || _targetSkeleton is null || _resolvedSourceProfile is null || _resolvedTargetProfile is null
            || _sourceMeshFile is null || _targetMeshFile is null || Map.Map is not { } map)
        {
            if (_inputs is not null) SetInputs(null, _loading > 0 ? "Loading…" : "Choose a source mesh and a target mesh.");
            return;
        }
        if (Map.HasErrors)
        {
            SetInputs(null, "Fix the bone map first: " + Map.Problems.First(p => p.IsError).Message);
            return;
        }
        if (!map.Fits(_sourceSkeleton.Names, _targetSkeleton.Names))
        {
            SetInputs(null, "The bone map was made for other skeletons. Press Auto-map.");
            return;
        }
        var targetProfile = _resolvedTargetProfile with { IkChains = [.. IkChains.Select(c => c.Edited)] };
        var problems = targetProfile.Validate().Concat(_resolvedSourceProfile.Validate()).ToList();
        if (problems.Count > 0)
        {
            SetInputs(null, problems[0]);
            return;
        }
        var refClip = _referenceClipFile;
        if (refClip is not null && refClip.BoneCount != _targetSkeleton.Count) refClip = null;
        var srcRefClip = _sourceReferenceClipFile;
        if (srcRefClip is not null && srcRefClip.BoneCount != _sourceSkeleton.Count) srcRefClip = null;
        var source = RetargetRig.FromMesh(_sourceRestFile ?? _sourceMeshFile, _resolvedSourceProfile, srcRefClip, srcRefClip is null ? null : _sourceReferenceClip?.Clip?.Name);
        var target = RetargetRig.FromMesh(_targetRestFile ?? _targetMeshFile, targetProfile, refClip, _referenceClip?.Clip?.Name);
        SetInputs(new RetargetInputs(source, target, map, BuildOptions(),
            _targetMeshFile, _targetMesh!.Name, _targetMesh.Folder, _sourceMeshFile, _sourceMesh!.Name, _sourceMesh.Folder), null);
    }

    private void SetInputs(RetargetInputs? inputs, string? reason)
    {
        _inputs = inputs;
        NotReadyReason = inputs is null ? reason ?? "Not ready." : null;
        Raise(nameof(Inputs));
        SaveProfileCommand.RaiseCanExecuteChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Option lists ────────────────────────────────────────────────────────

    private void RebuildMeshOptions()
    {
        var library = _shell.Assets.Snapshot;
        var all = library.Meshes.Where(m => m.HasSkeleton)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => new RigMeshOption(m, null, null, m.Name, MeshNote(library, m)))
            .Concat(_extraMeshes)
            .ToList();
        SourceMeshOptions.Clear();
        TargetMeshOptions.Clear();
        foreach (var m in all)
        {
            SourceMeshOptions.Add(m);
            TargetMeshOptions.Add(m);
        }
    }

    private static string MeshNote(LibrarySnapshot library, LibraryMesh mesh)
    {
        var family = library.FamilyOf(mesh.Name);
        string familyName = family is { Members.Length: > 1 }
            ? Path.GetFileNameWithoutExtension(family.Members.OrderBy(n => n.Length).ThenBy(n => n, StringComparer.OrdinalIgnoreCase).First()) + " family"
            : "own skeleton";
        string rig = RigProfiles.BuiltIn.FirstOrDefault(p => p.MeshNames.Contains(mesh.Name, StringComparer.OrdinalIgnoreCase)) is { } p ? $" · rig {p.Name}" : string.Empty;
        return $"{mesh.BoneCount} bones · {familyName}{rig}";
    }

    private void RebuildRestOptions(bool source)
    {
        var mesh = source ? _sourceMesh : _targetMesh;
        var list = source ? SourceRestOptions : TargetRestOptions;
        var self = new RigMeshOption(null, null, null, "(the mesh itself)", string.Empty);
        list.Clear();
        list.Add(self);
        if (mesh is null) return;
        int bones = mesh.Library?.BoneCount ?? mesh.Memory?.Bones.Length ?? 0;
        var library = _shell.Assets.Snapshot;
        var family = library.FamilyOf(mesh.Name);
        foreach (var option in (source ? SourceMeshOptions : TargetMeshOptions).Where(o => !SameName(o.Name, mesh.Name)
                     && (family is not null ? family.Members.Contains(o.Name, StringComparer.OrdinalIgnoreCase) : o.Library?.BoneCount == bones)))
            list.Add(option);
        // The profile's T-pose rest mesh, when the mesh is one of a built-in rig's.
        string? rest = RigProfiles.BuiltIn.FirstOrDefault(p => p.MeshNames.Contains(mesh.Name, StringComparer.OrdinalIgnoreCase))?.RestMesh;
        var pick = rest is not null && !SameName(rest, mesh.Name) ? list.FirstOrDefault(o => SameName(o.Name, rest)) : null;
        bool was = _suspend;
        _suspend = true;
        try
        {
            if (source) SelectedSourceRest = pick ?? self;
            else SelectedTargetRest = pick ?? self;
        }
        finally { _suspend = was; }
    }

    private void RebuildReferenceOptions(bool source)
    {
        var list = source ? SourceReferenceClipOptions : ReferenceClipOptions;
        list.Clear();
        var none = new ReferenceClipOption(null, source ? "(none — the clip's own lowest foot point)" : "(none — the target's bind pose)", string.Empty);
        list.Add(none);
        var mesh = source ? _sourceMesh : _targetMesh;
        bool was = _suspend;
        _suspend = true;
        void Pick(ReferenceClipOption option)
        {
            if (source) SelectedSourceReferenceClip = option;
            else SelectedReferenceClip = option;
        }
        try
        {
            if (mesh is null)
            {
                Pick(none);
                return;
            }
            int bones = mesh.Library?.BoneCount ?? mesh.Memory?.Bones.Length ?? 0;
            var library = _shell.Assets.Snapshot;
            var usage = _shell.Assets.Usage;
            var standNames = usage.ClipListsForMesh(mesh.Name).SelectMany(l => l.Clips)
                .Where(u => u.Kind == ClipUsageKind.State && string.Equals(u.SlotName, "stand", StringComparison.OrdinalIgnoreCase))
                .Select(u => u.DiskName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string? profileClip = RigProfiles.BuiltIn.FirstOrDefault(p => p.MeshNames.Contains(mesh.Name, StringComparer.OrdinalIgnoreCase))?.ReferenceClip;
            var clips = (bones > 0 ? library.CompatibleClips(bones) : library.Clips.Where(c => c.IsReadable).ToList())
                .OrderBy(c => SameName(c.Name, profileClip) ? 0 : standNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ? 1 : c.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var c in clips)
            {
                string note = SameName(c.Name, profileClip) ? "rig's stand clip" : standNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ? "tables: stand" : $"{c.BoneCount} bones";
                list.Add(new ReferenceClipOption(c, c.Name, note));
            }
            Pick(list.Count > 1 && (profileClip is not null || standNames.Count > 0)
                ? list[1]
                : list.Skip(1).FirstOrDefault(o => o.Clip!.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase)) ?? none);
        }
        finally { _suspend = was; }
    }

    private void RebuildProfileOptions(Skeleton source, Skeleton target)
    {
        bool was = _suspend;
        _suspend = true;
        try
        {
            var previousSource = _sourceProfile;
            var previousTarget = _targetProfile;
            ProfileOptions.Clear();
            ProfileOptions.Add(new ProfileOption(null, "generic — generated for the skeleton"));
            foreach (var p in RigProfiles.BuiltIn) ProfileOptions.Add(new ProfileOption(p, $"{p.Name} — {p.Title}"));
            foreach (var p in _customProfiles) ProfileOptions.Add(p);
            ProfileOption Auto(Skeleton s) => RigProfiles.FindFor(s) is { } found ? ProfileOptions.First(o => ReferenceEquals(o.Profile, found)) : ProfileOptions[0];
            SelectedSourceProfile = ProfileOptions.FirstOrDefault(o => previousSource is not null && Equals(o, previousSource)) ?? Auto(source);
            SelectedTargetProfile = ProfileOptions.FirstOrDefault(o => previousTarget is not null && Equals(o, previousTarget)) ?? Auto(target);
        }
        finally { _suspend = was; }
    }

    private void Browse(bool source)
    {
        string[] files = _shell.Dialogs.OpenFiles(_shell.Settings.LastSaveFolder, source ? "Choose the source mesh" : "Choose the target mesh",
            "Character meshes (*.v3c)|*.v3c|All files (*.*)|*.*", multiselect: false);
        if (files.Length == 0) return;
        var option = new RigMeshOption(null, files[0], null, Path.GetFileName(files[0]), "from disk");
        _extraMeshes.Add(option);
        SourceMeshOptions.Add(option);
        TargetMeshOptions.Add(option);
        if (source) SelectedSourceMesh = option;
        else SelectedTargetMesh = option;
    }

    // ── Profiles ─────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions OptionsJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The default folder for profiles: <c>%APPDATA%\Cairn\profiles</c>.</summary>
    public static string ProfileFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cairn", "profiles");

    /// <summary>The current setup as profile JSON: both rig profiles, the bone map and the options.</summary>
    public string ProfileJson()
    {
        if (_inputs is not { } inputs) throw new InvalidOperationException("Nothing to save yet.");
        var root = new JsonObject
        {
            ["format"] = "RFA Workbench retarget profile",
            ["version"] = 1,
            ["sourceMesh"] = inputs.SourceMeshName,
            ["targetMesh"] = inputs.TargetMeshName,
            ["sourceProfile"] = JsonNode.Parse(inputs.Source.Profile.ToJson()),
            ["targetProfile"] = JsonNode.Parse(inputs.Target.Profile.ToJson()),
            ["boneMap"] = JsonNode.Parse(inputs.Map.ToJson()),
            ["options"] = JsonSerializer.SerializeToNode(inputs.Options, OptionsJson),
        };
        return root.ToJsonString(OptionsJson);
    }

    /// <summary>
    /// Applies profile JSON written by <see cref="ProfileJson"/> (or a bare <see cref="RigProfile"/> file, taken
    /// as the target's profile). Returns a plain-language summary; throws <see cref="FormatException"/>.
    /// </summary>
    public string ApplyProfileJson(string json, string name)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new FormatException($"'{name}' is not JSON: {ex.Message}", ex); }
        if (root is not JsonObject obj) throw new FormatException($"'{name}' is not a retarget profile.");
        if (!obj.ContainsKey("targetProfile"))
        {
            var bare = RigProfile.FromJson(json);
            var option = AddCustom(bare, name);
            SelectedTargetProfile = option;
            return $"Loaded the rig profile '{bare.Name}' as the target's profile.";
        }
        if (obj["targetProfile"] is not JsonObject tp) throw new FormatException($"'{name}' has no usable target profile.");
        var target = RigProfile.FromJson(tp.ToJsonString());
        var source = obj["sourceProfile"] is JsonObject sp ? RigProfile.FromJson(sp.ToJsonString()) : null;
        BoneMap? map = obj["boneMap"] is { } bm ? BoneMap.FromJson(bm.ToJsonString()) : null;
        RetargetOptions? options = null;
        string? migrated = obj["options"] is JsonObject saved ? RetargetPresets.MigrateOptionsJson(saved) : null;
        try { options = obj["options"] is { } o ? o.Deserialize<RetargetOptions>(OptionsJson) : null; }
        catch (JsonException ex) { throw new FormatException($"The options in '{name}' could not be read: {ex.Message}", ex); }

        _pendingProfileMap = map;
        _suspend = true;
        try
        {
            if (source is not null) _sourceProfile = AddCustom(source, name + " (source)");
            _targetProfile = AddCustom(target, name + " (target)");
        }
        finally { _suspend = false; }
        if (options is not null) ApplyOptions(options);
        bool fits = map is not null && _sourceSkeleton is not null && _targetSkeleton is not null && map.Fits(_sourceSkeleton.Names, _targetSkeleton.Names);
        RebuildRigs(remap: true);
        string message = map is null ? "Loaded the profiles and options."
            : fits ? "Loaded the profiles, the bone map and the options."
            : "Loaded the profiles and options; the saved bone map is for other skeletons, so the bones were mapped automatically.";
        return migrated is null ? message : message + " " + migrated;
    }

    private ProfileOption AddCustom(RigProfile profile, string name)
    {
        var option = new ProfileOption(profile, $"{profile.Name} — loaded from {name}");
        _customProfiles.Add(option);
        if (!ProfileOptions.Contains(option)) ProfileOptions.Add(option);
        return option;
    }

    /// <summary>The last message from Save/Load profile.</summary>
    public string? ProfileMessage
    {
        get => _profileMessage;
        private set => Set(ref _profileMessage, value);
    }

    private string? _profileMessage;

    private void SaveProfile()
    {
        if (_inputs is not { } inputs) return;
        string folder = _shell.Settings.Get<string>(ProfileFolderKey) is { } f && Directory.Exists(f) ? f : ProfileFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        string suggested = $"{Path.GetFileNameWithoutExtension(inputs.SourceMeshName)}_to_{Path.GetFileNameWithoutExtension(inputs.TargetMeshName)}.json";
        string? path = _shell.Dialogs.SaveFile(folder, "Save retarget profile", "Retarget profiles (*.json)|*.json|All files (*.*)|*.*", suggested, ".json");
        if (path is null) return;
        try
        {
            Cairn.Workspace.AtomicFile.WriteAllText(path, ProfileJson());
            _shell.Settings.Set(ProfileFolderKey, Path.GetDirectoryName(path));
            ProfileMessage = $"Saved {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.Dialogs.ShowError("The profile could not be saved.", $"'{path}' could not be written.", ex.Message);
        }
    }

    private void LoadProfile()
    {
        string folder = _shell.Settings.Get<string>(ProfileFolderKey) is { } f && Directory.Exists(f) ? f : ProfileFolder;
        string[] files = _shell.Dialogs.OpenFiles(Directory.Exists(folder) ? folder : null, "Load retarget profile",
            "Retarget and rig profiles (*.json)|*.json|All files (*.*)|*.*", multiselect: false);
        if (files.Length == 0) return;
        try
        {
            // A profile is a few kilobytes; refuse anything absurd before reading it all.
            if (new FileInfo(files[0]).Length > 16 * 1024 * 1024) throw new FormatException("The file is far too large to be a retarget profile.");
            string json = File.ReadAllText(files[0]);
            ProfileMessage = ApplyProfileJson(json, Path.GetFileName(files[0]));
            _shell.Settings.Set(ProfileFolderKey, Path.GetDirectoryName(files[0]));
        }
        catch (Exception ex) when (RfaWorkspace.IsReadFailure(ex))
        {
            if (ex is not (IOException or UnauthorizedAccessException or FormatException or ArgumentException)) ErrorLog.Write("load profile", ex);
            _shell.Dialogs.ShowError("The profile could not be loaded.", $"'{files[0]}' is not a usable retarget profile.", ex.Message);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static RigMeshOption? Find(IEnumerable<RigMeshOption> options, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : options.FirstOrDefault(o => SameName(o.Name, name));

    internal static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    private static bool SameBones(V3dFile a, V3dFile b) =>
        a.Bones.Length == b.Bones.Length
        && a.Bones.Zip(b.Bones).All(z => z.First.ParentIndex == z.Second.ParentIndex
            && string.Equals(SkeletonMatcher.CanonicalBoneName(z.First.Name.Text), SkeletonMatcher.CanonicalBoneName(z.Second.Name.Text), StringComparison.OrdinalIgnoreCase));

    private static bool SameSkeleton(Skeleton a, Skeleton? b) =>
        b is not null && a.Count == b.Count && a.Names.SequenceEqual(b.Names) && a.Parents.SequenceEqual(b.Parents)
        && Enumerable.Range(0, a.Count).All(i => a.RestWorld[i].Position == b.RestWorld[i].Position && a.RestWorld[i].Rotation == b.RestWorld[i].Rotation);

    /// <summary>"0.012" in the current culture.</summary>
    internal static string F(double value, string format = "0.0") => value.ToString(format, CultureInfo.CurrentCulture);

    internal ImmutableArray<string> CanonicalTargetNames => _resolvedTargetProfile is { } p && _targetSkeleton is { } s ? p.CanonicalNames(s.Names) : [];
}
