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

    /// <summary>The picker's group header (the mesh's own packfile, another one, "Library"), or null for none.</summary>
    public string? Group { get; init; }

    /// <summary>True for the first entry of its <see cref="Group"/>: the list shows the heading above it.</summary>
    public bool IsGroupStart { get; init; }

    /// <summary>The heading the list shows above this entry, or null.</summary>
    public string? GroupHeading => IsGroupStart ? Group : null;

    /// <summary>The open packfile the clip is in, or null for the search-path library's (or Bind pose).</summary>
    public PackfileLibrary? Source { get; init; }

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
    /// <summary>The library or packfile entry <see cref="_previewClip"/> was read from, or null.</summary>
    private LibraryClip? _previewClipEntry;
    /// <summary>Why the preview clip is no longer the one picked (its packfile entry was removed), shown until another pick.</summary>
    private string? _lostNotice;
    /// <summary>The snapshot of its own packfile a work copy's textures were last looked up in, or null.</summary>
    private object? _ownVersion;
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

    /// <summary>True for a Red Faction II mesh's tab: it plays Red Faction II clips only (a clip stores bone indices only).</summary>
    public bool IsRedFactionIIMesh => _legacy?.Mesh?.IsRedFactionII == true;

    /// <summary>Opens the Convert window for this mesh.</summary>
    public RelayCommand ConvertLegacyCommand { get; }

    /// <summary>A Red Faction II mesh also finds its textures inside the .peg packs beside it.</summary>
    public override AssetResolver Resolver =>
        _legacy is { } legacy && LegacyMeshTextures.ResolverFor(legacy, DisplayName, FilePath, AssetSiblings.For(Shell.Host, FilePath), Shell.Assets.ResolverFor(Folder)) is { } layered
            ? layered : base.Resolver;

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
                "Cairn reads the exporter meshes (.v3d, .vcm), Red Faction's PlayStation 2 meshes and Red Faction II's meshes (.rfm, .rfc). Damaged files and Red Faction II's early prototype meshes cannot be converted, and the tab cannot be saved: there is no mesh to write.",
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
            bool changed = Set(ref _selectedPreviewClip, value);
            if (_syncingPicker || value is null) return;
            // the entry shown picked again: nothing to do, unless it is not the one playing
            if (!changed && ReferenceEquals(value.Clip, _previewClipEntry) && (value.Clip is null) == (_previewClip is null)) return;
            _userChoseClip = true;
            _lostNotice = null;
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
        _lostNotice = null;
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

    /// <summary>
    /// A packfile opened, changed or closed: the picker follows; a work copy's textures are looked up again in its packfile
    /// as it is now; a preview clip from a packfile that changed plays as its entry is now (or, removed, goes back to the
    /// automatic choice with a notice).
    /// </summary>
    public override void OnPackfileSourcesChanged()
    {
        RebuildPickerOptions();
        FollowOwnPackfile();
        if (FollowPackfileClip()) return;
        if (_previewClip is null && !_userChoseClip) ChoosePreviewClip();
    }

    /// <summary>
    /// A work copy (Open in Cairn): when its packfile has a new snapshot (an edit, a save), the viewport's resolver is
    /// made again over the packfile's current entries, so a texture is never read through offsets of a file saved over.
    /// </summary>
    private void FollowOwnPackfile()
    {
        if (_liveEdit || Shell.Packfiles.OpenSourceOf(AssetSiblings.SourceKeyOf(Shell.Host, FilePath)) is not { } source || ReferenceEquals(source.Version, _ownVersion)) return;
        _ownVersion = source.Version;
        Scene.SetMesh(Current, DisplayName, Resolver);
        Scene.SetAnimation(_previewClip, Playback.Time);
    }

    /// <summary>
    /// The preview clip came from a packfile that changed since (a new snapshot, read again): its entry as it is now
    /// plays, or, when the packfile no longer has it, the preview goes back to the automatic choice with a notice. A
    /// closed packfile's clip keeps playing (it is in memory); a packfile being read again is waited for. True when the
    /// preview clip was changed.
    /// </summary>
    private bool FollowPackfileClip()
    {
        var packfiles = Shell.Packfiles;
        if (_previewClipEntry is not { } shown || PackfileLibrary.KeyOf(shown) is not { } key || !packfiles.IsOpen(key)) return false;
        if (packfiles.LibraryOf(key) is not { } library || library.Contains(shown)) return false;
        if (library.FindClip(shown.Name) is { IsReadable: true } now && PackfileLibrary.IsRedFactionII(now) == PackfileLibrary.IsRedFactionII(shown))
        {
            LoadPreviewClip(now);
            return true;
        }
        _lostNotice = $"{shown.Name} is no longer in {library.Label}, so the preview went back to the automatic choice.";
        _userChoseClip = false;
        LoadPreviewClip(null);
        ChoosePreviewClip();
        if (_previewClip is null) Scene.SetNotice(_lostNotice, warning: true);
        return true;
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
            bool rf2 = IsRedFactionIIMesh;
            // the open packfiles first (the mesh's own, then the others): clips of its game with its bone count
            var own = Shell.Packfiles.OwnOf(FilePath);
            bool grouped = false;
            foreach (var source in Shell.Packfiles.InPickerOrder(FilePath))
            {
                string group = ReferenceEquals(source, own) ? PickerGroups.Own(source) : PickerGroups.Other(source);
                foreach (var (clip, note) in PackfileClips(source, bones, rf2))
                {
                    options.Add(new PreviewClipOption(clip, clip.Name, note) { Group = group, Source = source });
                    grouped = true;
                }
            }
            // then the library's, of the mesh's game only
            string? libraryGroup = PickerGroups.Library(grouped);
            var seen = new HashSet<LibraryClip>(ReferenceEqualityComparer.Instance);
            if (library.FindMesh(DisplayName) is { } self && self.BoneCount == bones)
            {
                foreach (var clip in library.PreviewClipCandidates(self.Name, usage))
                {
                    if (PackfileLibrary.IsRedFactionII(clip) == rf2 && seen.Add(clip)) options.Add(new PreviewClipOption(clip, clip.Name, NoteFor(clip, usage)) { Group = libraryGroup });
                }
            }
            foreach (var clip in library.CompatibleClips(bones))
            {
                if (PackfileLibrary.IsRedFactionII(clip) == rf2 && seen.Add(clip)) options.Add(new PreviewClipOption(clip, clip.Name, NoteFor(clip, usage)) { Group = libraryGroup });
            }
        }
        _syncingPicker = true;
        try
        {
            var selectedClip = _selectedPreviewClip?.Clip;
            PickerGroups.MarkStarts(options, o => o.Group, o => o with { IsGroupStart = true });
            PreviewClipOptions.Clear();
            foreach (var o in options) PreviewClipOptions.Add(o);
            SelectedPreviewClip = (selectedClip is null ? null : Matching(selectedClip)) ?? PreviewClipOptions.FirstOrDefault(o => selectedClip is null ? o.Clip is null : ReferenceEquals(o.Clip, selectedClip))
                ?? PreviewClipOptions[0];
        }
        finally { _syncingPicker = false; }
    }

    /// <summary>
    /// The picker entry of <paramref name="clip"/>: the same entry; for a packfile's clip the entry of its name in the same
    /// packfile (by source key, never a packfile of the same name); for a library clip one of its name from the same
    /// place, else of its name. Null when the picker does not list it.
    /// </summary>
    private PreviewClipOption? Matching(LibraryClip clip)
    {
        if (PreviewClipOptions.FirstOrDefault(o => ReferenceEquals(o.Clip, clip)) is { } same) return same;
        bool SameName(PreviewClipOption o) => o.Clip is not null && string.Equals(o.Clip.Name, clip.Name, StringComparison.OrdinalIgnoreCase);
        if (PackfileLibrary.KeyOf(clip) is { } key) return PreviewClipOptions.FirstOrDefault(o => SameName(o) && PackfileLibrary.KeyOf(o.Clip) == key);
        return PreviewClipOptions.FirstOrDefault(o => SameName(o) && o.Source is null && o.Clip!.Location.DisplayLocation == clip.Location.DisplayLocation)
            ?? PreviewClipOptions.FirstOrDefault(o => SameName(o) && o.Source is null);
    }

    // ── Clips of the open packfiles ──────────────────────────────────────────

    /// <summary>How the Red Faction II clips of each packfile snapshot rank on this mesh (by fit), once worked out.</summary>
    private readonly Dictionary<PackfileLibrary, IReadOnlyList<(string Name, double JointOffset)>> _rf2Ranks = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<PackfileLibrary> _ranking = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The clips of <paramref name="source"/> this mesh plays, with their picker notes: a Red Faction mesh's Red Faction
    /// clips with its bone count by name; a Red Faction II character's Red Faction II clips ranked by name and bind-pose fit
    /// (<see cref="Formats.Legacy.Rf2AnimationConverter.RankClips"/>; worked out in the background the first time, by name
    /// until then).
    /// </summary>
    private IEnumerable<(LibraryClip Clip, string Note)> PackfileClips(PackfileLibrary source, int bones, bool rf2)
    {
        var clips = source.ClipsFor(bones, rf2);
        if (!rf2 || clips.Count == 0) return clips.Select(c => (c, Frames(c)));
        if (!_rf2Ranks.TryGetValue(source, out var ranks))
        {
            RankRf2Clips(source, clips);
            return clips.Select(c => (c, Frames(c)));
        }
        var byName = clips.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        return ranks.Where(r => byName.ContainsKey(r.Name)).Select(r => (byName[r.Name],
            double.IsNaN(r.JointOffset) ? Frames(byName[r.Name]) : $"{Frames(byName[r.Name])} · fit {r.JointOffset * 1000:0.#} mm"));
    }

    private static string Frames(LibraryClip clip) =>
        clip.Facts is { } f ? (f.Duration / (double)RfaClip.TicksPerFrame).ToString("0.#", CultureInfo.CurrentCulture) + " f" : "";

    /// <summary>Reads the candidate clips off the UI thread and ranks them on this mesh's skeleton; the picker follows.</summary>
    private void RankRf2Clips(PackfileLibrary source, IReadOnlyList<LibraryClip> clips)
    {
        if (!_ranking.Add(source)) return;
        var bones = Current.Bones.ToArray();
        string name = DisplayName;
        var read = source.Contents.Read;
        var busy = BusyTracker.Begin("rank clips for " + name);
        _ = Task.Run(() =>
        {
            var loaded = new List<(string, RfaClip)>();
            foreach (var clip in clips)
            {
                try
                {
                    if (read(clip.Name) is { } bytes) loaded.Add((clip.Name, RfaReader.Read(bytes, clip.Name)));
                }
                catch (AssetFormatException)
                {
                    // an unreadable clip is not ranked (it is still listed)
                }
            }
            return Formats.Legacy.Rf2AnimationConverter.RankClips(name, bones, loaded);
        }).ContinueWith(task => Shell.Dispatcher.BeginInvoke(new Action(() =>
        {
            busy.Dispose();
            _ranking.Remove(source);
            if (!task.IsCompletedSuccessfully) return;
            // unranked clips (unreadable) keep their place at the end
            var ranked = task.Result.ToList();
            foreach (var clip in clips.Where(c => ranked.All(r => !string.Equals(r.Name, c.Name, StringComparison.OrdinalIgnoreCase)))) ranked.Add((clip.Name, double.NaN));
            _rf2Ranks[source] = ranked;
            // forget the rankings of packfile snapshots no longer open
            foreach (var stale in _rf2Ranks.Keys.Where(k => !Shell.Packfiles.Libraries.Contains(k)).ToList()) _rf2Ranks.Remove(stale);
            RebuildPickerOptions();
        })), TaskScheduler.Default);
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
        // a Red Faction II skeleton is not a Red Faction one: a clip that merely has as many bones would twist it, so
        // its tab starts in the bind pose (the picker still lists the clips)
        if (!HasSkeleton || _legacy?.Mesh?.IsRedFactionII == true) return;
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
            _previewClipEntry = null;
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
                    _previewClipEntry = clip;
                    Playback.SetClip(_previewClip);
                    Scene.SetAnimation(_previewClip, Playback.Time);
                    string? mismatch = _previewClip.BoneCount != Scene.Skeleton.Count
                        ? $"{clip.Name} has {_previewClip.BoneCount} bones; this mesh has {Scene.Skeleton.Count}. The game would play it wrong."
                        : null;
                    Scene.SetNotice(_lostNotice is { } lost ? (mismatch is null ? lost : lost + " " + mismatch) : mismatch, warning: true);
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
            SelectedPreviewClip = clip is null ? PreviewClipOptions.FirstOrDefault(o => o.Clip is null) : Matching(clip);
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
        _previewClipEntry = null;
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
        SetDiagnostics([.. legacy, .. Lint(mesh, new MeshLintContext { FileName = LintName })]);
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
            return Lint(mesh, new MeshLintContext { FileName = name, Resolver = resolver });
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

    /// <summary>The mesh's problems. A Red Faction II character's texture count is left out: converting it makes the atlas, and its note says so.</summary>
    private IEnumerable<Diagnostic> Lint(V3dFile mesh, MeshLintContext context)
    {
        bool atlased = _legacy?.Mesh is { IsRedFactionII: true, Description.Kind: V3dKind.Character };
        return [.. MeshLinter.Analyze(mesh, context).Where(d => !atlased || d.Code != MeshRules.TooManyTextures)];
    }

    /// <summary>The name the linter checks: a legacy mesh is checked as the .v3m/.v3c converting it makes.</summary>
    private string LintName => _legacy is null ? DisplayName : Path.ChangeExtension(DisplayName, Extension);

    public override bool CanApplyQuickFix(QuickFix fix) => fix.Kind switch
    {
        QuickFixKind.Edit => !IsReadOnly && fix.MeshEdit is not null,
        QuickFixKind.LocateFile or QuickFixKind.MergeWithAtlas => !IsReadOnly,
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
            case QuickFixKind.MergeWithAtlas:
                MergeSubmeshesWithAtlas();
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

    /// <summary>
    /// The merge fix of V3C028 when the merged mesh would use too many textures: an atlas from the images the tab
    /// resolves, written beside the mesh (or into its packfile), then the merge as one undo step. Returns true when the
    /// mesh changed.
    /// </summary>
    internal bool MergeSubmeshesWithAtlas()
    {
        if (IsReadOnly) return false;
        var resolver = Resolver;
        try
        {
            var merged = V3dSubmeshMerge.Merge(Current);
            var atlas = V3dTextureAtlas.Combine(merged.Submeshes[0], Path.GetFileNameWithoutExtension(DisplayName), name => ReadImage(resolver, name));
            if (!Shell.Legacy.WriteBeside(this, atlas.Files)) return false;
            var joined = V3dBuilder.Build(merged with { Submeshes = [atlas.Submesh] }, V3dBuildOptions.Preserve);
            return Apply("Merge the submeshes", _ => joined);
        }
        catch (Exception ex) when (ex is AssetFormatException or ArgumentException)
        {
            Shell.Dialogs.ShowError($"Could not merge the submeshes of {DisplayName}", ex.Message);
            return false;
        }
    }

    private static BgraImage? ReadImage(AssetResolver? resolver, string name)
    {
        if (resolver?.Resolve(name) is not { } found || found.ResolvedName.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return ImageDecoder.Decode(found.ReadAllBytes(), found.ResolvedName);
        }
        catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
