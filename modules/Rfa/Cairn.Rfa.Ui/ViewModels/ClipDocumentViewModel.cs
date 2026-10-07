using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>An entry of a clip document's preview mesh picker.</summary>
/// <param name="Mesh">The library mesh.</param>
/// <param name="Label">"ult2_guard.v3c".</param>
/// <param name="Note">"tables", "24 bones"…</param>
/// <param name="Fits">True when its bone count matches the clip.</param>
public sealed record PreviewMeshOption(LibraryMesh Mesh, string Label, string Note, bool Fits)
{
    public string ToolTip => $"{Mesh.Name} · {Mesh.BoneCount} bones · {Mesh.Location.DisplayLocation}";

    public override string ToString() => Label;
}

/// <summary>
/// A tab holding an .rfa clip: the clip's history, its preview mesh (chosen automatically per
/// DESIGN.md section 5 and changeable), the viewport scene and transport driven by it, the Clip
/// inspector, and linting (structural rules at once; table, library and mesh rules asynchronously).
/// </summary>
public sealed class ClipDocumentViewModel : DocumentViewModel<RfaClip>
{
    private V3dFile? _previewMesh;
    private LibraryMesh? _previewLibraryMesh;
    private PreviewMeshOption? _selectedPreviewMesh;
    private bool _userChoseMesh;
    private int _meshRequest;
    private bool _syncingPicker;
    private CancellationTokenSource? _lintCts;
    private ClipLintContext? _context;
    private object? _contextKey;
    private string _fileSizeText = string.Empty;

    public ClipDocumentViewModel(RfaWorkspace shell, RfaClip clip, string displayName, string? filePath, AssetLocation? archiveOrigin)
        : base(shell, clip, displayName, filePath, archiveOrigin)
    {
        Inspector = new ClipInspectorViewModel(this);
        InspectorTabs.Add(new InspectorTab("clip", "Clip", Inspector, "The clip's header fields and facts"));
        BoneInspector = new BoneInspectorViewModel(this);
        InspectorTabs.Add(new InspectorTab("bone", "Bone", BoneInspector, "The selected bones: weight in this clip, key counts, offset against the mesh"));
        KeyInspector = new KeyInspectorViewModel(this);
        InspectorTabs.Add(new InspectorTab("key", "Key", KeyInspector, "The selected keys: time, rotation, eases, position and control points"));
        SelectedInspectorTab = InspectorTabs[0];
        Timeline = new TimelineViewModel(this);
        KeySelectionChanged += (_, _) => Timeline.RefreshCommands();
        Layered = new LayeredPreviewViewModel(this);
        Scene.BonePicked += (_, bone) => OnBonePicked(bone);

        Playback.TimeChanged += (_, _) => Scene.SetAnimation(Current, Playback.Time);
        UpdateFileSize(clip);
        Playback.SetClip(clip);
        Scene.SetAnimation(clip, Playback.Time);
        Relint();
        ChoosePreviewMesh();
    }

    public override DocumentKind Kind => DocumentKind.Clip;

    public override string Extension => ".rfa";

    // ── Key selection and bone naming (phase 5 seams) ─────────────────────────

    private KeySelection _keySelection = KeySelection.Empty;

    /// <summary>
    /// The selected keys of <see cref="DocumentViewModel{T}.Current"/> (timeline, Key inspector, clipboard).
    /// Indices always address the current snapshot: an edit that moves or inserts keys sets the selection
    /// it returns (<see cref="ApplyAndSelect"/>), and any other snapshot change drops entries that no longer exist.
    /// </summary>
    public KeySelection KeySelection => _keySelection;

    /// <summary>Raised after <see cref="KeySelection"/> changes.</summary>
    public event EventHandler? KeySelectionChanged;

    /// <summary>Replaces the key selection (validated against the current clip).</summary>
    public void SetKeySelection(KeySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var valid = selection.Validate(Current);
        if (valid.IsEmpty && _keySelection.IsEmpty) return;
        if (!valid.IsEmpty && !_keySelection.IsEmpty && valid.Keys.SequenceEqual(_keySelection.Keys)) return;
        _keySelection = valid;
        KeySelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// <see cref="DocumentViewModel{T}.Apply"/> for an edit that also says which keys to select afterwards
    /// (moved, pasted, inserted keys). Returns true when the snapshot changed.
    /// </summary>
    public bool ApplyAndSelect(string label, Func<RfaClip, (RfaClip Clip, KeySelection Selection)> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        KeySelection? selected = null;
        bool changed = Apply(label, c =>
        {
            var (next, selection) = edit(c);
            selected = selection;
            return next;
        });
        if (changed && selected is not null) SetKeySelection(selected);
        return changed;
    }

    /// <summary>
    /// <see cref="DocumentViewModel{T}.UpdateEdit"/> for a coalesced edit that also returns the selection to
    /// show (a key drag): the live clip and the selection change together.
    /// </summary>
    public void UpdateEditAndSelect(Func<RfaClip, (RfaClip Clip, KeySelection Selection)> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        KeySelection? selected = null;
        UpdateEdit(c =>
        {
            var (next, selection) = edit(c);
            selected = selection;
            return next;
        });
        if (selected is not null) SetKeySelection(selected);
    }

    /// <summary>
    /// The preview mesh's skeleton when it fits the clip (same bone count), else null: what model-space
    /// edits, IK, mirroring and bone names need.
    /// </summary>
    public Skeleton? FittingSkeleton => Scene.Skeleton.Count > 0 && Scene.Skeleton.Count == Current.BoneCount ? Scene.Skeleton : null;

    /// <summary>True when the preview mesh's bone names apply to the clip's bones.</summary>
    public bool HasBoneNames => FittingSkeleton is not null;

    /// <summary>The name shown for a bone: the preview mesh's when it fits, else "Bone N".</summary>
    public string BoneDisplayName(int bone) =>
        FittingSkeleton is { } s && (uint)bone < (uint)s.Count && !string.IsNullOrEmpty(s.Names[bone])
            ? s.Names[bone]
            : $"Bone {bone}";

    /// <summary>The bone names for the clipboard and name matching (null when no fitting mesh gives names).</summary>
    public IReadOnlyList<string>? ClipboardBoneNames => FittingSkeleton?.Names;

    /// <summary>Raised when the preview mesh (and so the skeleton and bone names) changes.</summary>
    public event EventHandler? PreviewSkeletonChanged;

    /// <summary>
    /// Shows <paramref name="clip"/> in the viewport instead of the document's clip, without touching the
    /// document (a clip tool's live preview); null goes back to the document. Undo, saving and linting
    /// are unaffected.
    /// </summary>
    public void SetPreviewClip(RfaClip? clip) => Scene.SetPreviewClip(clip);

    /// <summary>The clip the viewport shows (a tool's preview while one is open, else the document).</summary>
    public RfaClip ShownClip => Scene.PreviewClip ?? Current;

    private ClipTools.ClipCompareViewModel? _compare;

    /// <summary>Clip › Compare With…: the clip shown as a ghost beside this one (the viewport header's chip).</summary>
    public ClipTools.ClipCompareViewModel Compare => _compare ??= new ClipTools.ClipCompareViewModel(this);

    /// <summary>The Clip inspector tab.</summary>
    public ClipInspectorViewModel Inspector { get; }

    /// <summary>The Bone inspector tab (the bone selection).</summary>
    public BoneInspectorViewModel BoneInspector { get; }

    /// <summary>The Key inspector tab (the key selection).</summary>
    public KeyInspectorViewModel KeyInspector { get; }

    /// <summary>The dope sheet (the Timeline bottom tab).</summary>
    public TimelineViewModel Timeline { get; }

    /// <summary>"Play as action over state" (transport bar).</summary>
    public LayeredPreviewViewModel Layered { get; }

    /// <summary>The preview mesh picker's entries: table meshes, then compatible, then the rest.</summary>
    public ObservableCollection<PreviewMeshOption> PreviewMeshOptions { get; } = [];

    /// <summary>The picked preview mesh (setting it loads the mesh and remembers the choice for this clip).</summary>
    public PreviewMeshOption? SelectedPreviewMesh
    {
        get => _selectedPreviewMesh;
        set
        {
            if (!Set(ref _selectedPreviewMesh, value) || _syncingPicker || value is null) return;
            _userChoseMesh = true;
            Shell.RememberPreviewMesh(LintDocumentPath, Current.BoneCount, value.Mesh.Name);
            LoadPreviewMesh(value.Mesh);
        }
    }

    /// <summary>Raised when a quick fix asks for the picker (the view opens it).</summary>
    public event EventHandler? PreviewPickerRequested;

    /// <summary>Asks the view to open the preview mesh picker (Clip › Choose Preview Mesh, the RFA002 quick fix).</summary>
    public void RequestPreviewPicker() => PreviewPickerRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The preview mesh's bone count (0 without one).</summary>
    public int PreviewSkeletonCount => Scene.Skeleton.Count;

    /// <summary>"5,428 bytes", or why the clip cannot be written.</summary>
    public string FileSizeText => _fileSizeText;

    public override string StatusBonesText
    {
        get
        {
            string text = Current.BoneCount == 1 ? "1 bone" : $"{Current.BoneCount} bones";
            if (PreviewSkeletonCount > 0 && PreviewSkeletonCount != Current.BoneCount) text += $" (mesh {PreviewSkeletonCount})";
            return text;
        }
    }

    public override string StatusDurationText => TimeFormat.Duration(Current.Duration);

    /// <summary>Uses <paramref name="mesh"/> as the preview mesh (library "Preview on this mesh", drag onto the viewport).</summary>
    public void UsePreviewMesh(LibraryMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _userChoseMesh = true;
        Shell.RememberPreviewMesh(LintDocumentPath, Current.BoneCount, mesh.Name);
        RebuildPickerOptions();
        LoadPreviewMesh(mesh);
    }

    /// <summary>
    /// Uses a mesh already in memory as the preview mesh (a retarget result previews on its target, which
    /// may not be in the library). Textures resolve from <paramref name="textureFolder"/> first.
    /// </summary>
    public void UsePreviewMesh(V3dFile mesh, string name, string? textureFolder)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _userChoseMesh = true;
        ++_meshRequest;
        var library = Shell.Assets.Snapshot.FindMesh(name);
        _previewMesh = mesh;
        _previewLibraryMesh = library;
        Shell.RememberLastPreviewMesh(Current.BoneCount, name);
        Scene.SetMesh(mesh, name, library is not null ? TextureResolverFor(library) : Shell.Assets.ResolverFor(textureFolder ?? Folder));
        Scene.SetAnimation(Current, Playback.Time);
        RebuildPickerOptions();
        UpdateMeshNotice();
        _contextKey = null;
        Relint();
        Inspector.Refresh();
        RaiseAll(nameof(PreviewSkeletonCount), nameof(StatusBonesText), nameof(FittingSkeleton), nameof(HasBoneNames));
        Shell.OnDocumentStatusChanged(this);
        Shell.OnPreviewPartnerChanged(this);
        PreviewSkeletonChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The preview mesh in memory, or null while none is loaded.</summary>
    public V3dFile? PreviewMesh => _previewMesh;

    /// <summary>The preview mesh's library entry, or null (none, or one given in memory that the library lacks).</summary>
    public LibraryMesh? PreviewLibraryMesh => _previewLibraryMesh;

    protected override RfaClip Parse(byte[] bytes, string name) => RfaReader.Read(bytes, name);

    protected override byte[] Write(RfaClip snapshot) => RfaWriter.Write(snapshot);

    protected override void OnSnapshotChanged(RfaClip previous, RfaClip current)
    {
        var validSelection = _keySelection.Validate(current);
        if (!ReferenceEquals(validSelection, _keySelection))
        {
            _keySelection = validSelection;
            KeySelectionChanged?.Invoke(this, EventArgs.Empty);
        }
        UpdateFileSize(current);
        Playback.SetClip(current);
        Scene.SetAnimation(current, Playback.Time);
        Inspector.Refresh();
        Timeline.OnClipChanged();
        BoneInspector.Refresh();
        KeyInspector.Refresh();
        if (previous.BoneCount != current.BoneCount) UpdateMeshNotice();
        Relint();
        RaiseAll(nameof(StatusBonesText), nameof(StatusDurationText));
        Shell.OnDocumentStatusChanged(this);
    }

    public override void OnLibraryChanged()
    {
        RebuildPickerOptions();
        Layered.RebuildOptions();
        if (_previewMesh is null && !_userChoseMesh) ChoosePreviewMesh();
        _contextKey = null;
        Relint();
    }

    public override void OnAssetsChanged()
    {
        // The resolver changed: the textures must be looked up again.
        if (_previewMesh is not null && _previewLibraryMesh is not null)
            Scene.SetMesh(_previewMesh, _previewLibraryMesh.Name, TextureResolverFor(_previewLibraryMesh));
    }

    /// <summary>Re-reads time-unit dependent text.</summary>
    public void OnTimeUnitChanged()
    {
        Inspector.Refresh();
        Playback.RefreshText();
        Timeline.OnUnitChanged();
        KeyInspector.Refresh();
        BoneInspector.Refresh();
    }

    // ── Preview mesh ─────────────────────────────────────────────────────────

    private void ChoosePreviewMesh()
    {
        var assets = Shell.Assets;
        var library = assets.Snapshot;
        RebuildPickerOptions();
        if (library.Meshes.Length == 0)
        {
            Scene.SetNotice(assets.IsLoading
                ? "Looking for a mesh to preview this clip on…"
                : assets.HasSources
                    ? "No character mesh was found to preview this clip on."
                    : "No preview mesh: set the game directory (Tools › Settings) or add a search folder so a mesh can be found.");
            return;
        }

        var pick = PickPreviewMesh(library, assets.Usage, DisplayName, Current.BoneCount, Shell.RememberedPreviewMesh(LintDocumentPath), Shell.LastPreviewMeshFor(Current.BoneCount));
        if (pick is null)
        {
            Scene.SetNotice($"No mesh in the library has {Current.BoneCount} bones. Pick one above to preview the clip anyway.", warning: true);
            return;
        }
        LoadPreviewMesh(pick);
    }

    /// <summary>
    /// The library mesh a clip previews on: the one remembered for it, else the library's default (tables, then
    /// <paramref name="lastUsed"/>, then a mesh with the clip's bone count); null when nothing fits. Shared with
    /// the read-only clip preview.
    /// </summary>
    internal static LibraryMesh? PickPreviewMesh(LibrarySnapshot library, ClipUsageIndex usage, string clipName, int boneCount, string? remembered, string? lastUsed)
    {
        LibraryMesh? pick = remembered is null ? null : library.FindMesh(remembered);
        pick ??= library.DefaultPreviewMesh(clipName, usage, lastUsed);
        // DefaultPreviewMesh takes the clip's bone count from the library's copy of the clip; a clip that
        // is not in the library (a new one, or a renamed copy) falls back to its own count here.
        if (pick is null || (library.FindClip(clipName) is null && pick.BoneCount != boneCount))
            pick = library.CompatibleMeshes(boneCount).FirstOrDefault(m => m.HasSkeleton) ?? pick;
        return pick;
    }

    private void RebuildPickerOptions()
    {
        var library = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        int bones = Current.BoneCount;
        var options = new List<PreviewMeshOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in usage.MeshesForClip(DisplayName))
        {
            if (library.FindMesh(name) is { HasSkeleton: true } m && seen.Add(m.Name))
                options.Add(new PreviewMeshOption(m, m.Name, m.BoneCount == bones ? "tables" : $"tables · {m.BoneCount} bones", m.BoneCount == bones));
        }
        foreach (var m in library.CompatibleMeshes(bones))
        {
            if (m.HasSkeleton && seen.Add(m.Name)) options.Add(new PreviewMeshOption(m, m.Name, $"{m.BoneCount} bones", true));
        }
        foreach (var m in library.Meshes.Where(m => m.HasSkeleton && m.BoneCount != bones))
        {
            if (seen.Add(m.Name)) options.Add(new PreviewMeshOption(m, m.Name, $"{m.BoneCount} bones — does not fit", false));
        }
        _syncingPicker = true;
        try
        {
            PreviewMeshOptions.Clear();
            foreach (var o in options) PreviewMeshOptions.Add(o);
            SelectedPreviewMesh = _previewLibraryMesh is null
                ? null
                : PreviewMeshOptions.FirstOrDefault(o => string.Equals(o.Mesh.Name, _previewLibraryMesh.Name, StringComparison.OrdinalIgnoreCase));
        }
        finally { _syncingPicker = false; }
    }

    private AssetResolver TextureResolverFor(LibraryMesh mesh) =>
        Shell.Assets.ResolverFor(mesh.Location.FilePath is { } path ? Path.GetDirectoryName(path) : Folder);

    private void LoadPreviewMesh(LibraryMesh mesh)
    {
        int request = ++_meshRequest;
        Scene.SetNotice($"Loading {mesh.Name}…");
        var busy = BusyTracker.Begin("preview mesh " + mesh.Name);
        _ = Task.Run(() => V3dReader.Read(mesh.Location.ReadAllBytes(), mesh.Name)).ContinueWith(task =>
        {
            Shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (request != _meshRequest) return;
                    if (!task.IsCompletedSuccessfully)
                    {
                        string reason = task.Exception?.InnerException?.Message ?? "it could not be read";
                        Scene.SetNotice($"{mesh.Name} could not be loaded: {reason}", warning: true);
                        return;
                    }
                    _previewMesh = task.Result;
                    _previewLibraryMesh = mesh;
                    Shell.RememberLastPreviewMesh(Current.BoneCount, mesh.Name);
                    Scene.SetMesh(_previewMesh, mesh.Name, TextureResolverFor(mesh));
                    Scene.SetAnimation(Current, Playback.Time);
                    _syncingPicker = true;
                    try
                    {
                        SelectedPreviewMesh = PreviewMeshOptions.FirstOrDefault(o => string.Equals(o.Mesh.Name, mesh.Name, StringComparison.OrdinalIgnoreCase));
                    }
                    finally { _syncingPicker = false; }
                    UpdateMeshNotice();
                    _contextKey = null;
                    Relint();
                    Inspector.Refresh();
                    RaiseAll(nameof(PreviewSkeletonCount), nameof(StatusBonesText), nameof(FittingSkeleton), nameof(HasBoneNames));
                    Shell.OnDocumentStatusChanged(this);
                    Shell.OnPreviewPartnerChanged(this);
                    PreviewSkeletonChanged?.Invoke(this, EventArgs.Empty);
                }
                finally { busy.Dispose(); }
            }));
        }, TaskScheduler.Default);
    }

    private void UpdateMeshNotice()
    {
        if (_previewMesh is null) return;
        int meshBones = Scene.Skeleton.Count, clipBones = Current.BoneCount;
        if (meshBones != clipBones)
        {
            Scene.SetNotice(
                $"This clip has {clipBones} bones but {Scene.MeshName} has {meshBones}. The game matches bones by index "
                + "and would play it wrong on this mesh; the preview shows it the same way.", warning: true);
        }
        else
        {
            Scene.SetNotice(null);
        }
    }

    // ── Linting ──────────────────────────────────────────────────────────────

    private void Relint()
    {
        var clip = Current;
        _lintCts?.Cancel();
        // Structural rules (plus whatever context is already known) at once…
        SetDiagnostics(ClipLinter.Analyze(clip, _context ?? new ClipLintContext { FileName = DisplayName }));

        // …and the context rules (tables, library, preview mesh) off the UI thread when the context is stale.
        var library = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        var mesh = _previewMesh;
        var key = (library, usage, mesh, DisplayName, LintDocumentPath);
        if (Equals(_contextKey, key) && _context is not null) return;
        var cts = new CancellationTokenSource();
        _lintCts = cts;
        var busy = BusyTracker.Begin("clip lint context");
        string fileName = DisplayName;
        string? documentPath = LintDocumentPath;
        string? meshName = _previewLibraryMesh?.Name ?? Scene.MeshName;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100, cts.Token).ConfigureAwait(false);
            var context = ClipLintContextBuilder.Build(fileName, documentPath, mesh, meshName, library, usage, cts.Token);
            return (context, ClipLinter.Analyze(clip, context));
        }, cts.Token).ContinueWith(task =>
        {
            Shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy.Dispose();
                if (!task.IsCompletedSuccessfully || cts.IsCancellationRequested) return;
                _context = task.Result.context;
                _contextKey = key;
                if (ReferenceEquals(clip, Current)) SetDiagnostics(task.Result.Item2);
                else SetDiagnostics(ClipLinter.Analyze(Current, _context));
            }));
        }, TaskScheduler.Default);
    }

    protected override void OnDiagnosticsChanged() => Inspector.ApplyDiagnostics(Diagnostics);

    public override bool CanApplyQuickFix(QuickFix fix) => fix.Kind switch
    {
        QuickFixKind.Edit => !IsReadOnly && fix.ClipEdit is not null,
        QuickFixKind.PickPreviewMesh => PreviewMeshOptions.Count > 0,
        QuickFixKind.ConformToSkeleton => !IsReadOnly,
        QuickFixKind.SaveAs or QuickFixKind.OpenSearchSettings => true,
        _ => false,
    };

    /// <summary>
    /// The mesh a "Conform to skeleton…" quick fix picks in the tool: the one the diagnostic names (RFA013's
    /// table mesh), else the preview mesh (RFA002, RFA007); null when there is neither.
    /// </summary>
    internal string? ConformTarget(QuickFix fix) =>
        fix.Payload is { Length: > 0 } named ? named : _previewLibraryMesh?.Name ?? Scene.MeshName;

    public override void ApplyQuickFix(QuickFix fix, Diagnostic diagnostic)
    {
        switch (fix.Kind)
        {
            case QuickFixKind.Edit when fix.ClipEdit is { } edit:
                Apply(fix.Title, edit);
                break;
            case QuickFixKind.PickPreviewMesh:
                PreviewPickerRequested?.Invoke(this, EventArgs.Empty);
                break;
            case QuickFixKind.ConformToSkeleton:
                Shell.ClipTools.OpenConform(this, ConformTarget(fix));
                break;
            case QuickFixKind.SaveAs:
                Shell.SaveAs(this);
                break;
            case QuickFixKind.OpenSearchSettings:
                Shell.SettingsCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// A bone picked in the viewport: the Bone inspector comes forward (unless the Key tab is in use with
    /// keys selected, which a pick must not take away) and the bone's row is revealed in the timeline.
    /// </summary>
    internal void OnBonePicked(int bone)
    {
        bool workingOnKeys = SelectedInspectorTab?.Id == "key" && !KeySelection.IsEmpty;
        if (!workingOnKeys) SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "bone") ?? SelectedInspectorTab;
        Timeline.RevealBone(bone);
    }

    public override void Reveal(DiagnosticLocation location)
    {
        switch (location.Target)
        {
            case DiagnosticTarget.HeaderField when location.Field is { } field:
                SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "clip");
                Inspector.FocusField(field);
                break;
            case DiagnosticTarget.Bone when location.Bone is { } bone:
                Selection.Select(bone);
                break;
            case DiagnosticTarget.Key when location.Bone is { } keyBone:
                Selection.Select(keyBone);
                if (location.Time is { } time) Playback.Seek(time);
                break;
            case DiagnosticTarget.Morph:
                SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "clip");
                Inspector.FocusField("rfa.num_morph_vertices");
                break;
        }
    }

    public override string DescribeLocation(DiagnosticLocation location) => location.Target switch
    {
        DiagnosticTarget.HeaderField => "Header · " + (FormatDocs.Find(location.Field ?? "")?.Name ?? location.Field),
        DiagnosticTarget.Bone => BoneName(location.Bone ?? -1),
        DiagnosticTarget.Key => BoneName(location.Bone ?? -1)
            + (location.Time is { } t ? " · key at " + TimeFormat.Format(t, Shell.TimeUnit) : string.Empty),
        DiagnosticTarget.Morph => "Morph data",
        _ => DisplayName,
    };

    private string BoneName(int bone)
    {
        if (bone < 0) return "Bone";
        string? name = bone < Scene.Skeleton.Count ? Scene.Skeleton.Names[bone] : null;
        return name is null ? $"Bone {bone}" : $"Bone {bone} ({name})";
    }

    private void UpdateFileSize(RfaClip clip)
    {
        try
        {
            _fileSizeText = string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", RfaWriter.Write(clip).Length);
        }
        catch (ArgumentException ex)
        {
            _fileSizeText = "cannot be written: " + ex.Message;
        }
        Raise(nameof(FileSizeText));
    }

    public override void Dispose()
    {
        // A preview mesh still loading must not land on a closed tab.
        ++_meshRequest;
        _lintCts?.Cancel();
        base.Dispose();
    }
}
