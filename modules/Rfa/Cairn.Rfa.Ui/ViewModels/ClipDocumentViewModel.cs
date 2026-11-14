using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Rfa.Docs;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Legacy;
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

    /// <summary>The picker's group header ("This packfile · All_Levels.toc_group", another packfile, "Library"), or null for none.</summary>
    public string? Group { get; init; }

    /// <summary>True for the first entry of its <see cref="Group"/>: the list shows the heading above it.</summary>
    public bool IsGroupStart { get; init; }

    /// <summary>The heading the list shows above this entry, or null.</summary>
    public string? GroupHeading => IsGroupStart ? Group : null;

    /// <summary>The open packfile the mesh is in, or null for the search-path library's.</summary>
    public PackfileLibrary? Source { get; init; }

    public override string ToString() => Label;
}

/// <summary>The group headers of the preview pickers (packfile sources first, then the search-path library).</summary>
internal static class PickerGroups
{
    /// <summary>The header of the document's own packfile.</summary>
    public static string Own(PackfileLibrary source) => "This packfile · " + source.Label;

    /// <summary>The header of another open packfile.</summary>
    public static string Other(PackfileLibrary source) => "Open packfile · " + source.Label;

    /// <summary>The header of the search-path library (null when it is the only group, so no header shows).</summary>
    public static string? Library(bool others) => others ? "Library" : null;

    /// <summary>Marks the first entry of every group (<paramref name="start"/> returns it marked), so its heading shows above it.</summary>
    public static List<T> MarkStarts<T>(List<T> options, Func<T, string?> group, Func<T, T> start)
    {
        string? previous = null;
        for (int i = 0; i < options.Count; i++)
        {
            string? g = group(options[i]);
            if (g is not null && g != previous) options[i] = start(options[i]);
            previous = g;
        }
        return options;
    }
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

    /// <param name="shell">The workspace.</param>
    /// <param name="clip">The clip as read.</param>
    /// <param name="displayName">The tab's name.</param>
    /// <param name="filePath">The file, or null.</param>
    /// <param name="archiveOrigin">The packfile entry it came from, or null.</param>
    /// <param name="source">The file's bytes: what a Red Faction II clip's tab gives back (it is never written), else unused.</param>
    public ClipDocumentViewModel(RfaWorkspace shell, RfaClip clip, string displayName, string? filePath, AssetLocation? archiveOrigin, byte[]? source = null)
        : base(shell, clip, displayName, filePath, archiveOrigin)
    {
        _rf2Source = clip.IsRedFactionII ? source : null;
        ConvertRf2Command = new RelayCommand(() => _ = Shell.Legacy.ConvertClipDocumentAsync(this), () => IsRedFactionII);
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

    // ── Red Faction II clips (version 9): read-only, converted to Red Faction clips ─────────────────────

    private byte[]? _rf2Source;
    private IReadOnlyList<Diagnostic>? _rf2Notes;

    /// <summary>True for a Red Faction II clip (version 9): it plays, but is never edited or written; it converts.</summary>
    public bool IsRedFactionII => Current.IsRedFactionII;

    /// <summary>A Red Faction II clip is read-only.</summary>
    public override bool IsReadOnly => IsRedFactionII;

    /// <summary>Converts a Red Faction II clip (the banner's Convert, through the Convert window).</summary>
    public RelayCommand ConvertRf2Command { get; }

    public override string? ConvertBannerText => IsRedFactionII
        ? $"{DisplayName} is a Red Faction II clip, read-only in Cairn. Convert it to a Red Faction clip (.rfa version 8) to use it in the PC game."
        : null;

    public override System.Windows.Input.ICommand? ConvertCommand => IsRedFactionII ? ConvertRf2Command : null;

    /// <summary>A Red Faction II clip is never written (not even under another name): it converts.</summary>
    protected override string? RefuseSave(string? path) => IsRedFactionII
        ? $"{DisplayName} is a Red Faction II clip, which Cairn never writes. Convert it to a Red Faction clip instead (File › Convert to)."
        : null;

    /// <summary>The start of the clip's file as it would be written: its signature and version.</summary>
    internal byte[] Head()
    {
        var head = new byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(head, RfaClip.Signature);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(4), Current.Version);
        return head;
    }

    /// <summary>What converting the Red Faction II clip approximates, as Problems rows (none for a Red Faction clip).</summary>
    private IReadOnlyList<Diagnostic> Rf2Notes()
    {
        if (!IsRedFactionII) return [];
        if (_rf2Notes is { } known) return known;
        IReadOnlyList<Diagnostic> notes;
        try
        {
            notes = [.. Rf2AnimationConverter.Convert(Current, DisplayName).Report.Select(n => new Diagnostic(LegacyMeshSource.NoteCode, DiagnosticSeverity.Info, n,
                "Converting the clip does this; the converted clip is what Red Faction would play.", DiagnosticLocation.Document))];
        }
        catch (AssetFormatException ex)
        {
            notes = [new Diagnostic(LegacyMeshSource.UnreadableCode, DiagnosticSeverity.Error, $"{DisplayName} cannot be converted: {ex.Message}",
                "The clip plays here, but no Red Faction clip can be made from it.", DiagnosticLocation.Document)];
        }
        return _rf2Notes = notes;
    }

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
            bool changed = Set(ref _selectedPreviewMesh, value);
            if (_syncingPicker || value is null) return;
            // the entry shown picked again: nothing to do, unless it is not the one playing (its load failed)
            if (!changed && ReferenceEquals(value.Mesh, _previewLibraryMesh)) return;
            _userChoseMesh = true;
            _otherGameDeclined = false;
            _lostNotice = null;
            Shell.RememberPreviewMesh(LintDocumentPath, Current.BoneCount, value.Mesh.Name);
            LoadPreviewMesh(value.Mesh);
        }
    }

    private bool _showOtherGameMeshes;

    /// <summary>Other game was turned off while the other game's mesh played: a remembered pick of it is not taken by itself.</summary>
    private bool _otherGameDeclined;

    /// <summary>Why the preview mesh is no longer the one picked (its packfile entry was removed), shown until another pick.</summary>
    private string? _lostNotice;

    /// <summary>
    /// A Red Faction II clip's picker lists only Red Faction II characters (a clip stores bone indices only, so on a Red
    /// Faction mesh it plays mangled); true lists the library's Red Faction meshes as well, below them.
    /// </summary>
    public bool ShowOtherGameMeshes
    {
        get => _showOtherGameMeshes;
        set
        {
            if (!Set(ref _showOtherGameMeshes, value)) return;
            RebuildPickerOptions();
            // turned off while a Red Faction mesh of the library plays: the picker no longer lists it, so the clip goes
            // back to the automatic choice (a Red Faction II character) rather than play on a mesh the picker leaves blank
            if (!value && IsRedFactionII && _previewLibraryMesh is { } shown && PackfileLibrary.KeyOf(shown) is null)
            {
                _otherGameDeclined = true;
                DropPreviewMesh();
                ChoosePreviewMesh();
            }
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
        _lostNotice = null;
        Shell.RememberPreviewMesh(LintDocumentPath, Current.BoneCount, mesh.Name);
        RebuildPickerOptions();
        LoadPreviewMesh(mesh);
    }

    /// <summary>
    /// Uses a mesh already in memory as the preview mesh (a retarget result previews on its target, which
    /// may not be in the library). Textures resolve from <paramref name="textureFolder"/> first.
    /// </summary>
    /// <param name="mesh">The mesh.</param>
    /// <param name="name">Its name.</param>
    /// <param name="textureFolder">Where its textures are looked for first, or null for the clip's folder.</param>
    /// <param name="entry">
    /// Its library or packfile entry when known (another clip's preview mesh), so a packfile's mesh stays credited to the
    /// packfile; null looks the name up in the library.
    /// </param>
    /// <param name="textures">The resolver of its textures when known (a packfile mesh's: the packfile's entries); null for the usual one.</param>
    public void UsePreviewMesh(V3dFile mesh, string name, string? textureFolder, LibraryMesh? entry = null, AssetResolver? textures = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _userChoseMesh = true;
        _lostNotice = null;
        ++_meshRequest;
        var library = entry ?? (textures is null ? Shell.Assets.Snapshot.FindMesh(name) : null);
        _previewMesh = mesh;
        _previewLibraryMesh = library;
        _previewTextures = textures;
        Shell.RememberLastPreviewMesh(Current.BoneCount, name);
        Scene.SetMesh(mesh, name, textures ?? (library is not null ? TextureResolverFor(library) : Shell.Assets.ResolverFor(textureFolder ?? Folder)));
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

    /// <summary>The textures of a preview mesh from a packfile (its entries first), or null for the usual resolver.</summary>
    internal AssetResolver? PreviewTextures => _previewTextures;

    protected override RfaClip Parse(byte[] bytes, string name)
    {
        var clip = RfaReader.Read(bytes, name);
        if (clip.IsRedFactionII) _rf2Source = bytes;
        return clip;
    }

    /// <summary>A Red Faction II clip gives back the file it was read from (recovery, Convert to); it is never serialised.</summary>
    protected override byte[] Write(RfaClip snapshot) =>
        snapshot.IsRedFactionII && _rf2Source is { } source ? source : RfaWriter.Write(snapshot);

    protected override void OnSnapshotChanged(RfaClip previous, RfaClip current)
    {
        if (!ReferenceEquals(previous, current)) _rf2Notes = null;
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
        bool gameChanged = previous.IsRedFactionII != current.IsRedFactionII;
        if (gameChanged) OnGameChanged();
        else if (previous.BoneCount != current.BoneCount) UpdateMeshNotice();
        Relint();
        RaiseAll(nameof(StatusBonesText), nameof(StatusDurationText));
        Shell.OnDocumentStatusChanged(this);
    }

    /// <summary>
    /// The clip became a Red Faction II one, or a Red Faction one again (a reload of a file replaced on disk): read-only,
    /// the banners and the Convert command follow, and the preview mesh is chosen afresh for the new content (a Red Faction
    /// II character, or the library's mesh).
    /// </summary>
    private void OnGameChanged()
    {
        RaiseAll(nameof(IsRedFactionII), nameof(IsReadOnly), nameof(ConvertBannerText), nameof(ConvertCommand),
            nameof(TabToolTip), nameof(CanUndo), nameof(CanRedo));
        ConvertRf2Command.RaiseCanExecuteChanged();
        // the old mesh belongs to the other game: drop it (a search or load still running is stale too) and choose again
        ++_meshRequest;
        _userChoseMesh = false;
        _rf2Searched = _rf2Searching = false;
        _lostNotice = null;
        _previewMesh = null;
        _previewLibraryMesh = null;
        Scene.SetMesh(null, null, null);
        _contextKey = null;
        ChoosePreviewMesh();
        RaiseAll(nameof(PreviewSkeletonCount), nameof(FittingSkeleton), nameof(HasBoneNames), nameof(Rf2PreviewMeshName));
        Shell.OnPreviewPartnerChanged(this);
        PreviewSkeletonChanged?.Invoke(this, EventArgs.Empty);
    }

    public override void OnLibraryChanged()
    {
        RebuildPickerOptions();
        Layered.RebuildOptions();
        // a Red Faction II character still being looked for is not overtaken by the library's pick
        if (_previewMesh is null && !_userChoseMesh && !_rf2Searching) ChoosePreviewMesh();
        _contextKey = null;
        Relint();
    }

    public override void OnAssetsChanged()
    {
        // The resolver changed: the textures must be looked up again (a packfile mesh keeps its packfile's textures).
        if (_previewMesh is not null && _previewLibraryMesh is not null)
            Scene.SetMesh(_previewMesh, _previewLibraryMesh.Name, _previewTextures ?? TextureResolverFor(_previewLibraryMesh));
    }

    /// <summary>
    /// A packfile opened, changed or closed: the picker follows; a preview mesh from a packfile that changed plays as its
    /// entry is now (or, removed, goes back to the automatic choice with a notice); a mesh picked for this clip before
    /// (remembered) that is in a packfile just read is taken now, unless a mesh was picked since; a Red Faction clip still
    /// without a mesh may find one in a packfile.
    /// </summary>
    public override void OnPackfileSourcesChanged()
    {
        RebuildPickerOptions();
        if (FollowPackfileEntry() || FollowOwnPackfile()) return;
        if (_userChoseMesh) return;
        if (RememberedPackfileMesh() is { } remembered)
        {
            if (!ReferenceEquals(remembered, _previewLibraryMesh)) PickRemembered(remembered);
            return;
        }
        // still nothing to play on (and no search running): an open packfile may have it now
        if (_previewMesh is null && !_rf2Searching) ChoosePreviewMesh();
    }

    /// <summary>
    /// The preview mesh came from a packfile that changed since (an edit, undo or redo, a save: a new snapshot, read
    /// again): its entry as it is now is loaded again (the packfile's textures as they are now with it), or, when the
    /// packfile no longer has a character of that name, the preview goes back to the automatic choice with a notice. A
    /// closed packfile's mesh keeps playing (it is in memory); a packfile still being read again is waited for (the next
    /// change brings it). True when the preview mesh was changed.
    /// </summary>
    private bool FollowPackfileEntry()
    {
        var packfiles = Shell.Packfiles;
        if (_previewLibraryMesh is not { } shown || PackfileLibrary.KeyOf(shown) is not { } key || !packfiles.IsOpen(key)) return false;
        if (packfiles.LibraryOf(key) is not { } library || library.Contains(shown)) return false;
        if (library.FindMesh(shown.Name) is { HasSkeleton: true } now && PackfileLibrary.IsRedFactionIIMesh(now) == PackfileLibrary.IsRedFactionIIMesh(shown))
        {
            LoadPreviewMesh(now);
            return true;
        }
        DropPreviewMesh();
        _lostNotice = $"{shown.Name} is no longer in {library.Label}, so the preview went back to the automatic choice.";
        ChoosePreviewMesh();
        if (_previewMesh is null && Scene.Notice is null) ShowMeshNotice(null);
        return true;
    }

    /// <summary>
    /// A Red Faction II character found by itself in the clip's own packfile (a work copy): when that packfile changed
    /// since the search (an edit, a save), it is searched again as it is now, so the character and its textures are read
    /// from the current entries, never through offsets of a file that was saved over. True when the search started.
    /// </summary>
    private bool FollowOwnPackfile()
    {
        if (_userChoseMesh || _rf2Searching || _previewMesh is null || _previewLibraryMesh is not null || !IsRedFactionII) return false;
        if (Shell.Packfiles.OpenSourceOf(_rf2SearchKey) is not { } source || ReferenceEquals(source.Version, _rf2SearchVersion)) return false;
        ChooseRf2PreviewMesh();
        return true;
    }

    /// <summary>
    /// Forgets the preview mesh (a removed packfile entry, the other game's mesh no longer listed) so the automatic choice
    /// can be made again.
    /// </summary>
    private void DropPreviewMesh()
    {
        ++_meshRequest;
        _userChoseMesh = false;
        _rf2Searched = _rf2Searching = false;
        _previewMesh = null;
        _previewLibraryMesh = null;
        _previewTextures = null;
        Scene.SetMesh(null, null, null);
        Scene.SetNotice(null);
        _contextKey = null;
        RaiseAll(nameof(PreviewSkeletonCount), nameof(StatusBonesText), nameof(FittingSkeleton), nameof(HasBoneNames), nameof(Rf2PreviewMeshName));
        Shell.OnDocumentStatusChanged(this);
        Shell.OnPreviewPartnerChanged(this);
        PreviewSkeletonChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The viewport's notice about the preview mesh, after why the picked one went (<see cref="_lostNotice"/>), if it did.</summary>
    private void ShowMeshNotice(string? text, bool warning = false)
    {
        if (_lostNotice is { } lost) Scene.SetNotice(text is null ? lost : lost + " " + text, warning: true);
        else Scene.SetNotice(text, warning);
    }

    /// <summary>
    /// The mesh picked for this clip before (Preview mesh, remembered by name) when one of the open packfiles has it
    /// (its own first) and it is of the clip's game, else null.
    /// </summary>
    private LibraryMesh? RememberedPackfileMesh()
    {
        if (Shell.RememberedPreviewMesh(LintDocumentPath) is not { } name) return null;
        // a Red Faction clip's pick the library has is the library's (as before packfiles were sources); at startup the
        // library is not read yet, so it is waited for (a packfile's mesh of the same name is not taken meanwhile)
        if (!IsRedFactionII && (LibraryStillLoading || Shell.Assets.Snapshot.FindMesh(name) is not null)) return null;
        foreach (var source in Shell.Packfiles.InPickerOrder(FilePath))
        {
            if (source.FindMesh(name) is { HasSkeleton: true } mesh && source.IsRedFactionII(mesh) == IsRedFactionII) return mesh;
        }
        return null;
    }

    /// <summary>Takes a remembered pick: it wins over the automatic choice (a Red Faction II search still running is dropped).</summary>
    private void PickRemembered(LibraryMesh mesh)
    {
        _userChoseMesh = true;
        _rf2Searched = true;
        _rf2Searching = false;
        LoadPreviewMesh(mesh);
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

    private bool _rf2Searched, _rf2Searching;

    /// <summary>The source key and version of the clip's own packfile when the Red Faction II search last ran (a work copy), else null.</summary>
    private string? _rf2SearchKey;
    private object? _rf2SearchVersion;

    /// <summary>True at startup: the library is being read for the first time (nothing in it yet).</summary>
    private bool LibraryStillLoading => Shell.Assets.IsLoading && Shell.Assets.Snapshot.Meshes.Length == 0;

    /// <summary>
    /// A Red Faction II clip plays on the Red Faction II character that fits it best, converted on the fly: one beside it
    /// (the rest of its packfile, its folder) or of the install it comes from. Without one no Red Faction mesh of the
    /// library is taken by itself (it would play the clip mangled): a notice asks for one from the Preview mesh list.
    /// </summary>
    /// <summary>Plays the Red Faction II clip on the named Red Faction II character beside it or of its install (the <c>--preview</c> option).</summary>
    public void UseRf2PreviewMesh(string meshName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meshName);
        _userChoseMesh = true;
        _lostNotice = null;
        ChooseRf2PreviewMesh(meshName);
    }

    private void ChooseRf2PreviewMesh(string? only = null)
    {
        _rf2Searched = _rf2Searching = true;
        int request = ++_meshRequest;
        var clip = Current;
        string name = DisplayName;
        string? path = FilePath;
        var host = Shell.Host;
        // a work copy: the rest of its packfile (as it is now: searched again when it changes), and the install it belongs to
        var siblings = AssetSiblings.For(host, path);
        _rf2SearchKey = AssetSiblings.SourceKeyOf(host, path);
        _rf2SearchVersion = _rf2SearchKey is null ? null : AssetSiblings.OpenSources(host).FirstOrDefault(s => s.Key == _rf2SearchKey)?.Version;
        string? origin = path is not null && host?.Modules.OfType<IWorkCopyProvider>().FirstOrDefault(p => p.IsWorkCopy(path)) is { } provider
            ? provider.ArchiveEntryOf(path)?.ArchivePath : path;
        if (siblings is null && path is not null && origin == path && Path.GetDirectoryName(path) is { } folder)
            siblings = new FolderSiblings(folder);
        string? install = Rf2PreviewMeshes.InstallOf(origin);
        var under = Shell.Assets.ResolverFor(Folder);
        // the clip's own packfile, already read: its characters' skeletons are known, nothing is read again to rank them
        var known = Shell.Packfiles.OwnOf(path) is { } own
            ? own.Meshes.Where(m => own.Rf2Bones(m.Name) is not null).Select(m => (m.Name, (IReadOnlyList<V3dBone>)own.Rf2Bones(m.Name)!.Value)).ToList()
            : null;
        Scene.SetNotice("Looking for a Red Faction II character to play this clip on…");
        var busy = BusyTracker.Begin("Red Faction II preview mesh for " + name);
        _ = Task.Run(() => Rf2PreviewMeshes.Find(clip, name, siblings, install, under, CancellationToken.None, only, known)).ContinueWith(task =>
        {
            Shell.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (request != _meshRequest) return;
                    _rf2Searching = false;
                    if (task.IsCompletedSuccessfully && task.Result is { } pick) ShowRf2PreviewMesh(pick);
                    else if (only is not null) Scene.SetNotice($"{only} was not found beside the clip or in its Red Faction II install, or does not have its {clip.BoneCount} bones.", warning: true);
                    else ChoosePreviewMesh();
                }
                finally { busy.Dispose(); }
            }));
        }, TaskScheduler.Default);
    }

    private void ShowRf2PreviewMesh(Rf2PreviewMeshes.Pick pick)
    {
        _previewMesh = pick.Mesh;
        _previewLibraryMesh = null;
        _previewTextures = pick.Textures;
        Scene.SetMesh(pick.Mesh, pick.Name, pick.Textures ?? Shell.Assets.ResolverFor(Folder));
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

    /// <summary>
    /// The Red Faction II character the clip plays on (converted: found by itself, or picked from an open packfile), or
    /// null (none yet, a Red Faction mesh, or a Red Faction clip).
    /// </summary>
    public string? Rf2PreviewMeshName => _previewMesh is not null && IsRedFactionII
        && (_previewLibraryMesh is null || _previewLibraryMesh.Name.EndsWith(".rfc", StringComparison.OrdinalIgnoreCase)) ? Scene.MeshName : null;

    /// <summary>The files of a folder by name (a loose Red Faction II clip's neighbours).</summary>
    private sealed class FolderSiblings(string folder) : IAssetSiblings
    {
        private readonly Lazy<IReadOnlyList<string>> _names = new(() =>
        {
            try { return [.. Directory.EnumerateFiles(folder, "*.rfc").Concat(Directory.EnumerateFiles(folder, "*.peg")).Select(Path.GetFileName).OfType<string>()]; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        });

        public string Label => folder;

        public IReadOnlyList<string> Names => _names.Value;

        public bool Contains(string name) => !string.IsNullOrEmpty(name) && File.Exists(Path.Combine(folder, Path.GetFileName(name)));

        public byte[]? Read(string name)
        {
            try { return Contains(name) ? File.ReadAllBytes(Path.Combine(folder, Path.GetFileName(name))) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Reads only the first <paramref name="count"/> bytes of the file (a character's skeleton comes first).</summary>
        public byte[]? ReadHead(string name, int count)
        {
            if (count < 0 || !Contains(name)) return null;
            try
            {
                using var stream = new FileStream(Path.Combine(folder, Path.GetFileName(name)), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[(int)Math.Min(stream.Length, count)];
                stream.ReadExactly(buffer);
                return buffer;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    /// <summary>The viewport's notice for a Red Faction II clip with no Red Faction II character to play on.</summary>
    internal const string NoRf2CharacterNotice = "No Red Faction II character found for this clip (beside it or in its install); open a Red Faction II packfile, or pick a mesh above to preview it on.";

    private void ChoosePreviewMesh()
    {
        if (IsRedFactionII)
        {
            // a character picked for this clip before, in an open packfile, wins over the automatic choice
            if (!_userChoseMesh && RememberedPackfileMesh() is { } remembered)
            {
                RebuildPickerOptions();
                PickRemembered(remembered);
                return;
            }
            if (!_rf2Searched && !_userChoseMesh)
            {
                ChooseRf2PreviewMesh();
                return;
            }
            // No Red Faction II character: a Red Faction mesh of the library would play the clip mangled, so none is
            // taken by itself; only one picked for this clip (the Preview mesh list), now or before.
            RebuildPickerOptions();
            if (_previewMesh is not null) return;
            if (!_otherGameDeclined && Shell.RememberedPreviewMesh(LintDocumentPath) is { } rememberedName && Shell.Assets.Snapshot.FindMesh(rememberedName) is { HasSkeleton: true } picked)
            {
                // the other game's mesh picked for this clip before: Other game on, so the picker lists the mesh it shows
                if (!_showOtherGameMeshes)
                {
                    _showOtherGameMeshes = true;
                    Raise(nameof(ShowOtherGameMeshes));
                    RebuildPickerOptions();
                }
                LoadPreviewMesh(picked);
                return;
            }
            // none beside it or in its install: the best Red Faction II character of the other open packfiles
            if (PreviewMeshOptions.FirstOrDefault(o => o.Source is not null && o.Fits) is { } best)
            {
                LoadPreviewMesh(best.Mesh);
                return;
            }
            ShowMeshNotice(NoRf2CharacterNotice, warning: true);
            return;
        }
        var assets = Shell.Assets;
        var library = assets.Snapshot;
        RebuildPickerOptions();
        if (!_userChoseMesh && RememberedPackfileMesh() is { } rememberedRf1)
        {
            PickRemembered(rememberedRf1);
            return;
        }
        // nothing in the library fits: a Red Faction character of an open packfile (the clip's own first) that does
        var fromPackfile = Shell.Packfiles.InPickerOrder(FilePath).SelectMany(s => s.CharactersFor(Current.BoneCount, false))
            .FirstOrDefault(m => m.BoneCount == Current.BoneCount);
        if (library.Meshes.Length == 0)
        {
            // at startup the library is waited for (the library's change chooses again): a mesh it has, remembered for
            // the clip or its default, is never passed over for a packfile's mesh read sooner
            if (fromPackfile is not null && !assets.IsLoading)
            {
                LoadPreviewMesh(fromPackfile);
                return;
            }
            ShowMeshNotice(assets.IsLoading
                ? "Looking for a mesh to preview this clip on…"
                : assets.HasSources
                    ? "No character mesh was found to preview this clip on."
                    : "No preview mesh: set the game directory (Tools › Settings) or add a search folder so a mesh can be found.");
            return;
        }

        string? rememberedMesh = Shell.RememberedPreviewMesh(LintDocumentPath);
        var pick = PickPreviewMesh(library, assets.Usage, DisplayName, Current.BoneCount, rememberedMesh, Shell.LastPreviewMeshFor(Current.BoneCount));
        // a library mesh that does not fit gives way to a packfile's character that does, unless it was picked for this clip
        bool pickedForClip = pick is not null && string.Equals(pick.Name, rememberedMesh, StringComparison.OrdinalIgnoreCase);
        if (!pickedForClip && (pick is null || pick.BoneCount != Current.BoneCount) && fromPackfile is not null) pick = fromPackfile;
        if (pick is null)
        {
            ShowMeshNotice($"No mesh in the library has {Current.BoneCount} bones. Pick one above to preview the clip anyway.", warning: true);
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
        var options = PickerGroups.MarkStarts(PickerOptions(), o => o.Group, o => o with { IsGroupStart = true });
        _syncingPicker = true;
        try
        {
            PreviewMeshOptions.Clear();
            foreach (var o in options) PreviewMeshOptions.Add(o);
            SelectedPreviewMesh = ShownOption();
        }
        finally { _syncingPicker = false; }
    }

    /// <summary>
    /// The picker's entries, by source: the clip's own packfile, the other open packfiles, then the search-path library.
    /// A Red Faction II clip gets the Red Faction II characters with its bone count, ranked by name and bind-pose fit
    /// (<see cref="Rf2AnimationConverter.RankMeshes"/>), and the library's Red Faction meshes only with
    /// <see cref="ShowOtherGameMeshes"/>; a Red Faction clip gets Red Faction meshes only (as before: the tables' meshes,
    /// then those with its bone count, then the rest), never a Red Faction II character.
    /// </summary>
    private List<PreviewMeshOption> PickerOptions()
    {
        int bones = Current.BoneCount;
        bool rf2 = IsRedFactionII;
        var options = new List<PreviewMeshOption>();
        var own = Shell.Packfiles.OwnOf(FilePath);
        foreach (var source in Shell.Packfiles.InPickerOrder(FilePath))
        {
            string group = ReferenceEquals(source, own) ? PickerGroups.Own(source) : PickerGroups.Other(source);
            if (rf2)
            {
                foreach (var (mesh, offset) in source.RankRf2Meshes(Current, DisplayName))
                    options.Add(new PreviewMeshOption(mesh, mesh.Name, $"{mesh.BoneCount} bones · fit {offset * 1000:0.#} mm", true) { Group = group, Source = source });
            }
            else
            {
                foreach (var mesh in source.CharactersFor(bones, redFactionII: false))
                {
                    bool fits = mesh.BoneCount == bones;
                    options.Add(new PreviewMeshOption(mesh, mesh.Name, fits ? $"{mesh.BoneCount} bones" : $"{mesh.BoneCount} bones — does not fit", fits) { Group = group, Source = source });
                }
            }
        }
        if (rf2 && !_showOtherGameMeshes) return options;

        var library = Shell.Assets.Snapshot;
        var usage = Shell.Assets.Usage;
        string? libraryGroup = PickerGroups.Library(options.Count > 0);
        string other = rf2 ? "Red Faction mesh · " : "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in usage.MeshesForClip(DisplayName))
        {
            if (library.FindMesh(name) is { HasSkeleton: true } m && seen.Add(m.Name))
                options.Add(new PreviewMeshOption(m, m.Name, other + (m.BoneCount == bones ? "tables" : $"tables · {m.BoneCount} bones"), m.BoneCount == bones) { Group = libraryGroup });
        }
        foreach (var m in library.CompatibleMeshes(bones))
        {
            if (m.HasSkeleton && seen.Add(m.Name)) options.Add(new PreviewMeshOption(m, m.Name, $"{other}{m.BoneCount} bones", true) { Group = libraryGroup });
        }
        foreach (var m in library.Meshes.Where(m => m.HasSkeleton && m.BoneCount != bones))
        {
            if (seen.Add(m.Name)) options.Add(new PreviewMeshOption(m, m.Name, $"{other}{m.BoneCount} bones — does not fit", false) { Group = libraryGroup });
        }
        return options;
    }

    /// <summary>
    /// The picker entry of the mesh shown now: the one picked (the same entry; for a packfile's mesh the entry of its name
    /// in the same packfile, by source key, so a packfile of the same name never stands in; for a library mesh one of its
    /// name from the same place, else of its name), or for a Red Faction II character found by itself the packfile entry
    /// of its name (the clip's own packfile first); null when the picker does not list it.
    /// </summary>
    private PreviewMeshOption? ShownOption()
    {
        if (_previewLibraryMesh is { } picked)
        {
            if (PreviewMeshOptions.FirstOrDefault(o => ReferenceEquals(o.Mesh, picked)) is { } same) return same;
            if (PackfileLibrary.KeyOf(picked) is { } key)
                return PreviewMeshOptions.FirstOrDefault(o => o.Source?.Key == key && string.Equals(o.Mesh.Name, picked.Name, StringComparison.OrdinalIgnoreCase));
            return PreviewMeshOptions.FirstOrDefault(o => o.Source is null && string.Equals(o.Mesh.Name, picked.Name, StringComparison.OrdinalIgnoreCase)
                    && o.Mesh.Location.DisplayLocation == picked.Location.DisplayLocation)
                ?? PreviewMeshOptions.FirstOrDefault(o => o.Source is null && string.Equals(o.Mesh.Name, picked.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (_previewMesh is not null && IsRedFactionII && Scene.MeshName is { } shown)
            return PreviewMeshOptions.FirstOrDefault(o => o.Source is not null && string.Equals(o.Mesh.Name, shown, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    private AssetResolver TextureResolverFor(LibraryMesh mesh) =>
        Shell.Assets.ResolverFor(mesh.Location.FilePath is { } path ? Path.GetDirectoryName(path) : Folder);

    /// <summary>The textures of a preview mesh picked from an open packfile (its packfile's entries first), or null.</summary>
    private AssetResolver? _previewTextures;

    private void LoadPreviewMesh(LibraryMesh mesh)
    {
        int request = ++_meshRequest;
        Scene.SetNotice($"Loading {mesh.Name}…");
        var busy = BusyTracker.Begin("preview mesh " + mesh.Name);
        // a packfile's mesh: read from its packfile (a Red Faction II or PlayStation 2 one converted), textures beside it
        var source = Shell.Packfiles.SourceOf(mesh);
        var entries = source is null ? null : Shell.Packfiles.OpenSourceOf(source)?.Entries;
        var under = Shell.Assets.ResolverFor(Folder);
        bool packfile = source is not null || mesh.Location.Origin is not null;
        _ = Task.Run(() => packfile
            ? PackfileSources.LoadMesh(mesh.Name, source?.Contents.Read(mesh.Name) ?? mesh.Location.ReadAllBytes(), entries, under)
            : (V3dReader.Read(mesh.Location.ReadAllBytes(), mesh.Name), null)).ContinueWith(task =>
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
                    _previewMesh = task.Result.Mesh;
                    _previewLibraryMesh = mesh;
                    _previewTextures = packfile ? task.Result.Textures ?? under : null;
                    Shell.RememberLastPreviewMesh(Current.BoneCount, mesh.Name);
                    Scene.SetMesh(_previewMesh, mesh.Name, _previewTextures ?? TextureResolverFor(mesh));
                    Scene.SetAnimation(Current, Playback.Time);
                    _syncingPicker = true;
                    try
                    {
                        SelectedPreviewMesh = ShownOption();
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
            ShowMeshNotice(
                $"This clip has {clipBones} bones but {Scene.MeshName} has {meshBones}. The game matches bones by index "
                + "and would play it wrong on this mesh; the preview shows it the same way.", warning: true);
        }
        else
        {
            ShowMeshNotice(null);
        }
    }

    // ── Linting ──────────────────────────────────────────────────────────────

    private void Relint()
    {
        var clip = Current;
        _lintCts?.Cancel();
        // Structural rules (plus whatever context is already known) at once…
        SetDiagnostics(WithRf2Notes(ClipLinter.Analyze(clip, _context ?? new ClipLintContext { FileName = DisplayName })));

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
                if (ReferenceEquals(clip, Current)) SetDiagnostics(WithRf2Notes(task.Result.Item2));
                else SetDiagnostics(WithRf2Notes(ClipLinter.Analyze(Current, _context)));
            }));
        }, TaskScheduler.Default);
    }

    protected override void OnDiagnosticsChanged() => Inspector.ApplyDiagnostics(Diagnostics);

    /// <summary>The lint results, then (for a Red Faction II clip) what converting it approximates.</summary>
    private IReadOnlyList<Diagnostic> WithRf2Notes(IReadOnlyList<Diagnostic> lint) => IsRedFactionII ? [.. lint, .. Rf2Notes()] : lint;

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
            _fileSizeText = string.Format(CultureInfo.CurrentCulture, "{0:N0} bytes", Write(clip).Length);
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
