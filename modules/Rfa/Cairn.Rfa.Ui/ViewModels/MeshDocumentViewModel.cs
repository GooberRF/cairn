using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Rfa.Ui.Services;
using Cairn.Assets;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>An entry of a mesh document's preview clip picker; <see cref="Clip"/> null is "Bind pose".</summary>
public sealed record PreviewClipOption(LibraryClip? Clip, string Label, string Note)
{
    public string ToolTip => Clip is null
        ? "Show the mesh's bind (rest) pose"
        : $"{Clip.Name} · {Clip.BoneCount} bones · {Clip.Facts?.Duration / (double)RfaClip.TicksPerFrame:0.#} frames · {Clip.Location.DisplayLocation}";

    public override string ToString() => Label;
}

/// <summary>
/// A tab holding a .v3c (editable in phase 6) or a .v3m (read-only): the mesh's history, the viewport
/// scene, the preview clip picker (compatible clips, the tables' clips for this mesh on top), the
/// structure tree, and linting.
/// </summary>
public sealed class MeshDocumentViewModel : DocumentViewModel<V3dFile>
{
    private readonly bool _isStatic;
    private PreviewClipOption? _selectedPreviewClip;
    private RfaClip? _previewClip;
    private bool _userChoseClip;
    private bool _syncingPicker;
    private int _clipRequest;
    private CancellationTokenSource? _lintCts;

    public MeshDocumentViewModel(RfaWorkspace shell, V3dFile mesh, string displayName, string? filePath, AssetLocation? archiveOrigin,
        LegacyMeshSource? legacy = null)
        : base(shell, mesh, displayName, filePath, archiveOrigin)
    {
        _legacy = legacy;
        ConvertLegacyCommand = new RelayCommand(() => Shell.Legacy.ConvertDocument(this), () => _legacy?.Mesh is not null);
        _isStatic = legacy is not null
            ? mesh.Kind == V3dKind.StaticMesh
            : mesh.Kind == V3dKind.StaticMesh || displayName.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase);
        Structure = new MeshStructureViewModel(this);
        InspectorTabs.Add(new InspectorTab("structure", "Structure", Structure, "Submeshes, LODs, batches, materials, bones, spheres and prop points"));
        SelectedInspectorTab = InspectorTabs[0];

        Playback.TimeChanged += (_, _) => Scene.SetAnimation(_previewClip, Playback.Time);
        // One selection: a bone selected anywhere (viewport, Problems, Select All) is the tree's node and
        // its editor; a bone picked in the viewport also brings the Structure tab forward.
        Selection.Changed += (_, _) => Structure.FollowBoneSelection(Selection.Active);
        Scene.BonePicked += (_, _) =>
        {
            SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "structure") ?? SelectedInspectorTab;
            Structure.FollowBoneSelection(Selection.Active);
        };
        // A collision sphere or prop point clicked in the viewport is the tree's node and its editor, exactly as a
        // bone is; a click on empty space lets a selected sphere or prop point go, as it lets bones go.
        Scene.ItemPicked += (_, pick) => OnItemPicked(pick);
        _shown = mesh;
        Scene.SetMesh(mesh, displayName, Resolver);
        Structure.Rebuild(mesh);
        Playback.SetClip(null);
        Relint();
        RebuildPickerOptions();
        ChoosePreviewClip();
    }

    public override DocumentKind Kind => _isStatic ? DocumentKind.StaticMesh : DocumentKind.CharacterMesh;

    public override string Extension => _isStatic ? ".v3m" : ".v3c";

    /// <summary>Static meshes open read-only, and so do the formats Cairn only reads and converts.</summary>
    public override bool IsReadOnly => _isStatic || _legacy is not null;

    // ── Exporter and PS2 meshes (.v3d, .vcm, .rfm, .rfc) ─────────────────────

    private LegacyMeshSource? _legacy;

    /// <summary>The exporter or PS2 file this tab shows (as the mesh converting it makes), or null for a .v3m/.v3c.</summary>
    public LegacyMeshSource? Legacy => _legacy;

    /// <summary>True for a .v3d, .vcm, .rfm or .rfc tab (until it is saved as .v3m/.v3c).</summary>
    public bool IsLegacy => _legacy is not null;

    /// <summary>Opens the Convert window for this mesh.</summary>
    public RelayCommand ConvertLegacyCommand { get; }

    public override bool ShowsReadOnlyBanner => IsReadOnly && _legacy is null;

    public override string? ConvertBannerText => _legacy switch
    {
        null => null,
        { Mesh: null } l => $"{l.FormatTitle} could not be read: {l.Error} Problems lists the details.",
        { } l => $"{l.FormatTitle} is read-only in Cairn. Convert to {Extension} to use it in the PC game.",
    };

    public override System.Windows.Input.ICommand? ConvertCommand => _legacy is null ? null : ConvertLegacyCommand;

    /// <summary>A save writes the converted mesh: from then on the tab is that .v3m/.v3c.</summary>
    public override void MarkSaved(string path)
    {
        bool wasLegacy = _legacy is not null;
        _legacy = null;
        base.MarkSaved(path);
        if (wasLegacy)
        {
            RaiseAll(nameof(IsReadOnly), nameof(ShowsReadOnlyBanner), nameof(ConvertBannerText), nameof(ConvertCommand), nameof(IsLegacy), nameof(Legacy), nameof(TabToolTip));
            Relint();
            Shell.OnDocumentStatusChanged(this);
        }
    }

    /// <summary>
    /// A legacy file that could not be read has nothing to save (an empty mesh is not a conversion); one that was read
    /// is saved as a new .v3m/.v3c, never over the file it was read from.
    /// </summary>
    protected override string? RefuseSave(string? path)
    {
        if (_legacy is not { } l) return null;
        if (l.Mesh is null) return $"{DisplayName} could not be read, so there is nothing to save: {l.Error}";
        if (path is not null && FilePath is { } source
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            return $"Saving would replace {Path.GetFileName(source)}, the {l.FormatTitle} this tab was read from. Save the converted mesh under another name.";
        return null;
    }

    /// <summary>A legacy tab is captured (closed-tab reopen, session) as the file it shows, so it reopens as that file.</summary>
    public override Func<byte[]> CaptureSerializer()
    {
        if (_legacy is not { } l) return base.CaptureSerializer();
        byte[] bytes = l.Bytes;
        return () => bytes;
    }

    protected override bool MatchesSaved(byte[] bytes) =>
        _legacy is { } legacy ? legacy.Bytes.AsSpan().SequenceEqual(bytes) : base.MatchesSaved(bytes);

    /// <summary>The Problems rows of a legacy mesh: why it could not be read, or what converting it approximates.</summary>
    private IEnumerable<Diagnostic> LegacyDiagnostics()
    {
        if (_legacy is not { } l) yield break;
        if (l.Mesh is null)
        {
            yield return new Diagnostic(LegacyMeshSource.UnreadableCode, DiagnosticSeverity.Error, $"{DisplayName} could not be read: {l.Error}",
                "Cairn reads the exporter meshes (.v3d, .vcm) and Red Faction's PlayStation 2 meshes (.rfm, .rfc). Red Faction II's meshes and damaged files cannot be converted, and the tab cannot be saved: there is no mesh to write.",
                DiagnosticLocation.Document);
            yield break;
        }
        foreach (string note in l.Mesh.Notes)
            yield return new Diagnostic(LegacyMeshSource.NoteCode, DiagnosticSeverity.Info, note,
                "Converting the mesh does this; the converted file is what the game would load.", DiagnosticLocation.Document);
    }

    public override bool HasTransport => HasSkeleton;

    /// <summary>True when the mesh has bones, so a clip can play on it.</summary>
    public bool HasSkeleton => Scene.Skeleton.Count > 0;

    /// <summary>The Structure inspector tab.</summary>
    public MeshStructureViewModel Structure { get; }

    /// <summary>The preview clip picker's entries: bind pose, then table clips, then compatible clips.</summary>
    public ObservableCollection<PreviewClipOption> PreviewClipOptions { get; } = [];

    /// <summary>The picked preview clip (setting it loads and plays it).</summary>
    public PreviewClipOption? SelectedPreviewClip
    {
        get => _selectedPreviewClip;
        set
        {
            if (!Set(ref _selectedPreviewClip, value) || _syncingPicker || value is null) return;
            _userChoseClip = true;
            LoadPreviewClip(value.Clip);
        }
    }

    /// <summary>The loaded preview clip, or null.</summary>
    public RfaClip? PreviewClip => _previewClip;

    public override string StatusBonesText => !HasSkeleton ? "static mesh" : Scene.Skeleton.Count == 1 ? "1 bone" : $"{Scene.Skeleton.Count} bones";

    public override string StatusDurationText => _previewClip is { } clip ? TimeFormat.Duration(clip.Duration) : string.Empty;

    /// <summary>
    /// Shows <paramref name="clip"/> on this mesh (library "Preview this clip", double-click); with
    /// <paramref name="play"/> it starts playing once loaded.
    /// </summary>
    public void UsePreviewClip(LibraryClip clip, bool play = false)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _userChoseClip = true;
        if (!PreviewClipOptions.Any(o => ReferenceEquals(o.Clip, clip)))
            PreviewClipOptions.Insert(1, new PreviewClipOption(clip, clip.Name, $"{clip.BoneCount} bones"));
        LoadPreviewClip(clip, play);
    }

    protected override V3dFile Parse(byte[] bytes, string name)
    {
        if (_legacy is null) return V3dReader.Read(bytes, name);
        // A legacy file changed on disk: read it again (a file that no longer reads keeps the tab as it was).
        var (mesh, compiled) = Formats.Legacy.LegacyMeshSupport.ReadCompiled(bytes, name);
        _legacy = _legacy with { Bytes = bytes, Mesh = mesh, Error = null };
        return compiled;
    }

    protected override byte[] Write(V3dFile snapshot) => V3dWriter.Write(snapshot);

    protected override void OnSnapshotChanged(V3dFile previous, V3dFile current)
    {
        // A gizmo drag in progress: only what the drag changes is shown live (the viewport and the editor's numbers).
        if (_liveEdit && IsEditing)
        {
            Scene.SetMeshLive(current);
            Structure.Editor?.Refresh();
            return;
        }
        ShowSnapshot(current, rebuild: !ReferenceEquals(previous, current) || !ReferenceEquals(_shown, current));
    }

    /// <summary>The snapshot the viewport, the tree, the lint and previewing clips were last brought up to date with.</summary>
    private V3dFile _shown;

    private bool _liveEdit;

    private void ShowSnapshot(V3dFile current, bool rebuild)
    {
        if (rebuild)
        {
            Scene.SetMesh(current, DisplayName, Resolver);
            Scene.SetAnimation(_previewClip, Playback.Time);
            Structure.Rebuild(current);
            // Clips previewing this very snapshot (conformed to a reorder) follow the edit, undo or redo.
            if (!ReferenceEquals(_shown, current)) Shell.MeshTools.OnMeshSnapshotChanged(this, _shown, current);
        }
        _shown = current;
        Relint();
        RaiseAll(nameof(StatusBonesText), nameof(HasSkeleton), nameof(HasTransport));
        Shell.OnDocumentStatusChanged(this);
    }

    /// <summary>
    /// Starts showing a gizmo drag live (after <see cref="DocumentViewModel{T}.BeginEdit"/>): each update of the
    /// coalesced edit only re-skins or repaints the viewport (<see cref="SceneViewModel.SetMeshLive"/>) and
    /// refreshes the editor pane's numbers; the tree, the lint and previewing clips wait for
    /// <see cref="EndLiveEdit"/>, so a drag never rebuilds them per mouse move.
    /// </summary>
    internal void BeginLiveEdit() => _liveEdit = IsEditing;

    /// <summary>True while a gizmo drag is shown live.</summary>
    internal bool IsLiveEditing => _liveEdit;

    /// <summary>
    /// Ends the live drag: commits it as one undo step (or cancels it, restoring the pre-drag snapshot exactly)
    /// and brings everything up to date with the result: viewport, tree, editor, lint, previewing clips.
    /// </summary>
    internal void EndLiveEdit(bool commit)
    {
        _liveEdit = false;
        if (IsEditing)
        {
            if (commit) CommitEdit();
            else CancelEdit();
        }
        if (!ReferenceEquals(_shown, Current) || !ReferenceEquals(Scene.Mesh, Current)) ShowSnapshot(Current, rebuild: true);
    }

    public override void OnLibraryChanged()
    {
        RebuildPickerOptions();
        if (_previewClip is null && !_userChoseClip) ChoosePreviewClip();
    }

    public override void OnAssetsChanged()
    {
        Scene.SetMesh(Current, DisplayName, Resolver);
        Scene.SetAnimation(_previewClip, Playback.Time);
        Structure.Rebuild(Current);
        Relint();
    }

    /// <summary>Where the resolver finds a texture, for the structure facts (cheap: the indexes are cached).</summary>
    public string DescribeTexture(string name)
    {
        try
        {
            var location = Resolver.Resolve(name);
            if (location is null) return "not found";
            return (location.IsSupersede ? location.ResolvedName + " in " : "") + location.DisplayLocation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "not readable";
        }
    }

    // ── Preview clip ─────────────────────────────────────────────────────────

    private void RebuildPickerOptions()
    {
        var options = new List<PreviewClipOption> { new(null, "Bind pose", "no clip") };
        if (HasSkeleton)
        {
            var library = Shell.Assets.Snapshot;
            var usage = Shell.Assets.Usage;
            int bones = Scene.Skeleton.Count;
            var seen = new HashSet<LibraryClip>(ReferenceEqualityComparer.Instance);
            if (library.FindMesh(DisplayName) is { } self && self.BoneCount == bones)
            {
                foreach (var clip in library.PreviewClipCandidates(self.Name, usage))
                {
                    if (seen.Add(clip)) options.Add(new PreviewClipOption(clip, clip.Name, NoteFor(clip, usage)));
                }
            }
            foreach (var clip in library.CompatibleClips(bones))
            {
                if (seen.Add(clip)) options.Add(new PreviewClipOption(clip, clip.Name, NoteFor(clip, usage)));
            }
        }
        _syncingPicker = true;
        try
        {
            var selectedClip = _selectedPreviewClip?.Clip;
            PreviewClipOptions.Clear();
            foreach (var o in options) PreviewClipOptions.Add(o);
            SelectedPreviewClip = PreviewClipOptions.FirstOrDefault(o => selectedClip is null ? o.Clip is null : o.Clip is not null && o.Clip.Name == selectedClip.Name)
                ?? PreviewClipOptions[0];
        }
        finally { _syncingPicker = false; }
    }

    private string NoteFor(LibraryClip clip, Cairn.Rfa.Formats.Tbl.ClipUsageIndex usage)
    {
        var uses = usage.UsagesOf(clip.Name);
        var mine = uses.FirstOrDefault(u => usage.MeshClipLists.Any(l => l.Clips.Contains(u)
            && string.Equals(l.MeshName, DisplayName, StringComparison.OrdinalIgnoreCase)));
        if (mine is not null) return (mine.Kind == Cairn.Rfa.Formats.Tbl.ClipUsageKind.State ? "state " : "action ") + mine.SlotName;
        return clip.Facts is { } f ? (f.Duration / (double)RfaClip.TicksPerFrame).ToString("0.#", CultureInfo.CurrentCulture) + " f" : "";
    }

    private void ChoosePreviewClip()
    {
        if (!HasSkeleton) return;
        var library = Shell.Assets.Snapshot;
        LibraryClip? pick = null;
        if (library.FindMesh(DisplayName) is { } self && self.BoneCount == Scene.Skeleton.Count)
            pick = library.DefaultPreviewClip(self.Name, Shell.Assets.Usage);
        pick ??= PreviewClipOptions.Select(o => o.Clip).OfType<LibraryClip>().FirstOrDefault(c => c.BaseName.Contains("stand", StringComparison.OrdinalIgnoreCase))
            ?? PreviewClipOptions.Select(o => o.Clip).OfType<LibraryClip>().FirstOrDefault();
        if (pick is not null) LoadPreviewClip(pick);
    }

    private void LoadPreviewClip(LibraryClip? clip, bool play = false)
    {
        int request = ++_clipRequest;
        if (clip is null)
        {
            _previewClip = null;
            Playback.SetClip(null);
            Scene.SetAnimation(null, Playback.Time);
            SyncPicker(null);
            RaiseAll(nameof(PreviewClip), nameof(StatusDurationText));
            Shell.OnDocumentStatusChanged(this);
            return;
        }
        var busy = BusyTracker.Begin("preview clip " + clip.Name);
        _ = Task.Run(() => RfaReader.Read(clip.Location.ReadAllBytes(), clip.Name)).ContinueWith(task =>
        {
            Shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (request != _clipRequest) return;
                    if (!task.IsCompletedSuccessfully)
                    {
                        Scene.SetNotice($"{clip.Name} could not be loaded: {task.Exception?.InnerException?.Message}", warning: true);
                        return;
                    }
                    _previewClip = task.Result;
                    Playback.SetClip(_previewClip);
                    Scene.SetAnimation(_previewClip, Playback.Time);
                    Scene.SetNotice(_previewClip.BoneCount != Scene.Skeleton.Count
                        ? $"{clip.Name} has {_previewClip.BoneCount} bones; this mesh has {Scene.Skeleton.Count}. The game would play it wrong."
                        : null, warning: true);
                    SyncPicker(clip);
                    RaiseAll(nameof(PreviewClip), nameof(StatusDurationText));
                    if (play)
                    {
                        Playback.Seek(_previewClip.StartTime);
                        Playback.Play();
                    }
                    Shell.OnDocumentStatusChanged(this);
                    Shell.OnPreviewPartnerChanged(this);
                }
                finally { busy.Dispose(); }
            }));
        }, TaskScheduler.Default);
    }

    private void SyncPicker(LibraryClip? clip)
    {
        _syncingPicker = true;
        try
        {
            SelectedPreviewClip = PreviewClipOptions.FirstOrDefault(o => clip is null ? o.Clip is null : ReferenceEquals(o.Clip, clip))
                ?? PreviewClipOptions.FirstOrDefault(o => clip is not null && o.Clip?.Name == clip.Name);
        }
        finally { _syncingPicker = false; }
    }

    // ── Structure selection ──────────────────────────────────────────────────

    private void OnItemPicked(ScenePick pick)
    {
        switch (pick.Kind)
        {
            case SceneItemKind.Sphere:
                SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "structure") ?? SelectedInspectorTab;
                Structure.Reveal(new MeshNodeRef(MeshNodeKind.CollisionSphere, -1, -1, pick.Index));
                break;
            case SceneItemKind.Prop:
                SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "structure") ?? SelectedInspectorTab;
                Structure.Reveal(new MeshNodeRef(MeshNodeKind.PropPoint, 0, 0, pick.Index));
                break;
            default:
                if (Structure.Selected?.Node is { Kind: MeshNodeKind.CollisionSphere or MeshNodeKind.PropPoint }) Structure.Selected = null;
                break;
        }
    }

    /// <summary>Shows <paramref name="clip"/> as the preview clip without the library (self-tests and screenshots).</summary>
    internal void UsePreviewClipForTest(RfaClip? clip)
    {
        ++_clipRequest;
        _userChoseClip = true;
        _previewClip = clip;
        Playback.SetClip(clip);
        Scene.SetAnimation(clip, Playback.Time);
        RaiseAll(nameof(PreviewClip), nameof(StatusDurationText));
    }

    internal void OnStructureSelectionChanged(MeshNodeViewModel? node)
    {
        if (node?.Node is not { } r)
        {
            Scene.SetHighlight(null);
            Selection.Clear();
            return;
        }
        var mesh = Current;
        // One selection: a node other than a bone takes the viewport's bone selection away.
        if (r.Kind != MeshNodeKind.Bone) Selection.Clear();
        switch (r.Kind)
        {
            case MeshNodeKind.Bone:
                Scene.SetHighlight(null);
                // The active bone of a multi-selection keeps the others (a rebuild re-selects it).
                if (Selection.Active != r.Index) Selection.Select(r.Index);
                break;
            case MeshNodeKind.CollisionSphere:
                Scene.SetHighlight(null, sphere: r.Index);
                break;
            case MeshNodeKind.PropPoint:
                Scene.SetHighlight(null, prop: r.Index);
                break;
            case MeshNodeKind.Lod:
                Scene.Lod = r.Lod;
                Scene.SetHighlight(null);
                break;
            case MeshNodeKind.Batch:
                if (r.Lod != Scene.Lod) Scene.Lod = r.Lod;
                Scene.SetHighlight([new BatchRef(r.Submesh, r.Lod, r.Index)]);
                break;
            case MeshNodeKind.Submesh:
                Scene.SetHighlight(Batches(mesh, b => b.Submesh == r.Submesh && b.Lod == Scene.Lod));
                break;
            case MeshNodeKind.Material:
                Scene.SetHighlight(Batches(mesh, b => b.Submesh == r.Submesh && b.Lod == Scene.Lod
                    && TextureMaterial(mesh, b) == r.Index));
                break;
            case MeshNodeKind.Texture:
                if (r.Lod != Scene.Lod) Scene.Lod = r.Lod;
                Scene.SetHighlight(Batches(mesh, b => b.Submesh == r.Submesh && b.Lod == r.Lod
                    && mesh.Submeshes.ElementAt(b.Submesh).Lods[b.Lod].Batches[b.Batch].TextureIndex == r.Index));
                break;
            default:
                Scene.SetHighlight(null);
                break;
        }
    }

    private static int TextureMaterial(V3dFile mesh, BatchRef b)
    {
        var lod = mesh.Submeshes.ElementAt(b.Submesh).Lods[b.Lod];
        int t = lod.Batches[b.Batch].TextureIndex;
        return t >= 0 && t < lod.Textures.Length ? lod.Textures[t].MaterialIndex : -1;
    }

    private static IEnumerable<BatchRef> Batches(V3dFile mesh, Func<BatchRef, bool> where)
    {
        int s = 0;
        foreach (var sub in mesh.Submeshes)
        {
            for (int l = 0; l < sub.Lods.Length; l++)
            {
                for (int b = 0; b < sub.Lods[l].Batches.Length; b++)
                {
                    var r = new BatchRef(s, l, b);
                    if (where(r)) yield return r;
                }
            }
            s++;
        }
    }

    // ── Linting ──────────────────────────────────────────────────────────────

    private void Relint()
    {
        var mesh = Current;
        _lintCts?.Cancel();
        var legacy = LegacyDiagnostics().ToList();
        if (_legacy is { Mesh: null })
        {
            // Nothing was read: the reason is the only problem.
            SetDiagnostics(legacy);
            return;
        }
        SetDiagnostics([.. legacy, .. MeshLinter.Analyze(mesh, new MeshLintContext { FileName = LintName })]);
        var cts = new CancellationTokenSource();
        _lintCts = cts;
        var resolver = Resolver;
        string name = LintName;
        var busy = BusyTracker.Begin("mesh lint");
        var indexed = Shell.Assets.ArchivesIndexed;
        _ = Task.Run(async () =>
        {
            await Task.Delay(100, cts.Token).ConfigureAwait(false);
            // V3C021 resolves every texture: not while the library build is still reading the archives.
            await indexed.WaitAsync(cts.Token).ConfigureAwait(false);
            return MeshLinter.Analyze(mesh, new MeshLintContext { FileName = name, Resolver = resolver });
        }, cts.Token).ContinueWith(task =>
        {
            Shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy.Dispose();
                if (task.IsCompletedSuccessfully && !cts.IsCancellationRequested && ReferenceEquals(mesh, Current))
                    SetDiagnostics([.. legacy, .. task.Result]);
            }));
        }, TaskScheduler.Default);
    }

    /// <summary>The name the linter checks: a legacy mesh is checked as the .v3m/.v3c converting it makes.</summary>
    private string LintName => _legacy is null ? DisplayName : Path.ChangeExtension(DisplayName, Extension);

    public override bool CanApplyQuickFix(QuickFix fix) => fix.Kind switch
    {
        QuickFixKind.Edit => !IsReadOnly && fix.MeshEdit is not null,
        QuickFixKind.LocateFile => !IsReadOnly,
        QuickFixKind.SaveAs or QuickFixKind.OpenSearchSettings => true,
        _ => false,
    };

    public override void ApplyQuickFix(QuickFix fix, Diagnostic diagnostic)
    {
        switch (fix.Kind)
        {
            case QuickFixKind.Edit when fix.MeshEdit is { } edit:
                Apply(fix.Title, edit);
                break;
            case QuickFixKind.LocateFile:
                LocateTexture(diagnostic);
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
    /// The "Locate the file…" quick fix of a missing texture (V3C021): the texture browser the material
    /// editor's Browse… opens, for the material that owns the LOD texture entry. The pick becomes that
    /// material's texture name as one undo step, exactly as the material editor sets it (texture names only:
    /// the game finds files by name; the LOD entries that carried the old name follow). Returns true when
    /// the mesh changed.
    /// </summary>
    internal bool LocateTexture(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (IsReadOnly) return false;
        if (OwningMaterial(Current, diagnostic.Location) is not { } owner)
        {
            ShowStatus("The texture's material is no longer in the mesh.");
            return false;
        }
        var (si, mi) = owner;
        string current = Current.Submeshes.ElementAt(si).Materials[mi].DiffuseMap.Text;
        string? picked = Shell.MeshTools.PickTexture(this, current);
        if (string.IsNullOrEmpty(picked)) return false;
        return Apply($"Set texture of material {mi} to '{picked}'",
            m => MeshEdit.SetTextureName(m, si, mi, picked, LodTextureUpdate.MatchingName));
    }

    /// <summary>The (submesh, material) owning the LOD texture entry <paramref name="location"/> points at, or null.</summary>
    private static (int Submesh, int Material)? OwningMaterial(V3dFile mesh, DiagnosticLocation location)
    {
        if (location.MeshNode is not { Kind: MeshNodeKind.Texture } t
            || mesh.Submeshes.ElementAtOrDefault(t.Submesh) is not { } sub
            || (uint)t.Lod >= (uint)sub.Lods.Length || (uint)t.Index >= (uint)sub.Lods[t.Lod].Textures.Length) return null;
        int material = sub.Lods[t.Lod].Textures[t.Index].MaterialIndex;
        return material < sub.Materials.Length ? (t.Submesh, material) : null;
    }

    public override void Reveal(DiagnosticLocation location)
    {
        if (location.Target == DiagnosticTarget.MeshNode && location.MeshNode is { } node)
        {
            SelectedInspectorTab = InspectorTabs.FirstOrDefault(t => t.Id == "structure");
            Structure.Reveal(node);
        }
        else if (location.Bone is { } bone)
        {
            Selection.Select(bone);
        }
    }

    public override string DescribeLocation(DiagnosticLocation location) => location.Target switch
    {
        DiagnosticTarget.MeshNode when location.MeshNode is { } n => Capitalise(n.ToString()),
        DiagnosticTarget.Bone => $"Bone {location.Bone}",
        _ => DisplayName,
    };

    private static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    public override void Dispose()
    {
        // A preview clip still loading must not start playback on a closed tab.
        ++_clipRequest;
        _lintCts?.Cancel();
        base.Dispose();
    }
}
