using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipTools;
using Cairn.Rfa.Animation;
using Cairn.Assets;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;

namespace Cairn.Rfa.Ui.ViewModels.GltfTools;

/// <summary>A clip the export can include: an open clip tab or a library clip.</summary>
public sealed class GltfExportClipRow : CheckRow
{
    internal GltfExportClipRow(string name, string note, string toolTip, Func<CancellationToken, Task<RfaClip>> load, bool isFixed)
    {
        Name = name;
        Note = note;
        ToolTip = toolTip;
        Load = load;
        IsFixed = isFixed;
    }

    /// <summary>The animation name (the clip's base name).</summary>
    public string Name { get; }

    public override string Label => Name;

    /// <summary>"this clip", "open tab · unsaved edits", "tables", "26 bones"…</summary>
    public string Note { get; }

    /// <summary>Where the clip comes from.</summary>
    public string ToolTip { get; }

    /// <summary>True for the clip document being exported (always included).</summary>
    public bool IsFixed { get; }

    /// <summary>False for the fixed row (its check box cannot be cleared).</summary>
    public bool CanToggle => !IsFixed;

    /// <summary>"Export ult2_walk".</summary>
    public string AutomationName => $"Export {Name}";

    internal Func<CancellationToken, Task<RfaClip>> Load { get; }
}

/// <summary>One LOD the export can include.</summary>
public sealed class GltfLodRow : CheckRow
{
    internal GltfLodRow(int index, string note)
    {
        Index = index;
        Note = note;
    }

    /// <summary>The LOD index (0 = most detailed).</summary>
    public int Index { get; }

    public override string Label => $"LOD {Index}";

    /// <summary>"most detailed · from 0 m".</summary>
    public string Note { get; }

    /// <summary>"Export LOD 1".</summary>
    public string AutomationName => $"Export LOD {Index}";
}

/// <summary>A line of the export's result summary.</summary>
/// <param name="Text">What happened.</param>
/// <param name="IsWarning">True for something the user should check.</param>
public sealed record GltfResultLine(string Text, bool IsWarning = false)
{
    /// <summary>Segoe MDL2 glyph: warning or bullet.</summary>
    public string Glyph => IsWarning ? "" : "";

    /// <summary>Theme brush key.</summary>
    public string BrushKey => IsWarning ? "Severity.Warning" : "Severity.Info";
}

/// <summary>
/// File › Export to glTF…: a mesh document (with any compatible clips) or a clip document (on its preview
/// mesh, with optional others of the same bone count) written as <c>.gltf</c> (+ <c>.bin</c> + PNGs) or a
/// single <c>.glb</c> through <see cref="GltfExport"/>. The export runs off the UI thread in stages
/// (reading clips, building, writing) and Stop cancels between them; nothing is written until the last
/// stage. The result (warnings, missing textures, morph data left out, baked tracks, files) stays in
/// the dialog.
/// </summary>
public sealed class GltfExportViewModel : ObservableObject
{
    internal const string FolderSettingKey = "rfa.gltfExportFolder";

    private readonly V3dFile _mesh;
    private readonly AssetResolver? _resolver;
    private bool _skeletonOnly;
    private bool _includeSpheres = true;
    private bool _includeProps = true;
    private bool _includeTextures = true;
    private bool _isGlb;
    private bool _writeKeyExtras = true;
    private string _outputPath;
    private string? _confirmedPath;
    private string? _lastExportedPath;
    private bool _isExporting;
    private string _progressText = string.Empty;
    private string? _error;
    private IReadOnlyList<GltfResultLine> _resultLines = [];
    private CancellationTokenSource? _cts;

    /// <summary>Builds the dialog's model.</summary>
    /// <exception cref="ArgumentException">The document cannot be exported (a clip without a preview mesh).</exception>
    public GltfExportViewModel(RfaWorkspace shell, DocumentViewModel document)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        var snapshot = shell.Assets.Snapshot;
        var usage = shell.Assets.Usage;
        var rows = new List<GltfExportClipRow>();
        string baseName;
        int bones;
        switch (document)
        {
            case MeshDocumentViewModel meshDocument:
                _mesh = meshDocument.Current;
                MeshName = meshDocument.DisplayName;
                _resolver = meshDocument.Scene.TextureResolver ?? meshDocument.Resolver;
                baseName = Path.GetFileNameWithoutExtension(meshDocument.DisplayName);
                bones = Skeleton.FromFile(_mesh).Count;
                IsClipExport = false;
                break;
            case ClipDocumentViewModel clipDocument:
                if (clipDocument.PreviewMesh is not { } preview)
                    throw new ArgumentException($"{clipDocument.DisplayName} has no preview mesh. A glTF animation needs the skeleton it plays on: pick a preview mesh with {clipDocument.Current.BoneCount} bones first (the viewport's mesh picker), then export.");
                _mesh = preview;
                MeshName = clipDocument.Scene.MeshName ?? "preview mesh";
                _resolver = clipDocument.Scene.TextureResolver ?? shell.Assets.Resolver;
                baseName = Path.GetFileNameWithoutExtension(clipDocument.DisplayName);
                bones = clipDocument.Current.BoneCount;
                IsClipExport = true;
                var self = clipDocument;
                var row = new GltfExportClipRow(baseName, self.IsDirty ? "this clip · unsaved edits included" : "this clip",
                    $"{self.DisplayName} as it is now in its tab", _ => Task.FromResult(self.Current), isFixed: true) { IsChecked = true };
                rows.Add(row);
                int meshBones = Skeleton.FromFile(_mesh).Count;
                if (meshBones != bones)
                    MismatchText = $"The preview mesh {MeshName} has {meshBones} bones but the clip has {bones}: bones are matched by index, so the animation will not fit the skeleton. Pick a preview mesh with {bones} bones for a usable export.";
                break;
            default:
                throw new ArgumentException("Only clip and mesh documents can be exported to glTF.");
        }
        BaseName = baseName;
        var skeleton = Skeleton.FromFile(_mesh);
        BoneCount = skeleton.Count;
        SphereCount = _mesh.CollisionSpheres.Count();
        PropCount = _mesh.Submeshes.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints.Length ?? 0;
        var submeshes = _mesh.Submeshes.ToList();
        int lodCount = submeshes.Count == 0 ? 0 : submeshes.Max(s => s.Lods.Length);
        var distances = submeshes.FirstOrDefault()?.LodDistances ?? [];
        for (int i = 0; i < lodCount; i++)
        {
            string note = (i == 0 ? "most detailed" : i == lodCount - 1 ? "least detailed" : "")
                + (i < distances.Length ? $"{(i == 0 || i == lodCount - 1 ? " · " : "")}from {distances[i].ToString("0.##", CultureInfo.CurrentCulture)} m" : "");
            var lod = new GltfLodRow(i, note) { IsChecked = true };
            lod.CheckedChanged += (_, _) => OnOptionChanged();
            Lods.Add(lod);
        }
        SubmeshCount = submeshes.Count;

        // Other clips of the same bone count: open tabs first, then the library (the tables' clips for this mesh first).
        if (bones > 0)
        {
            foreach (var other in shell.Documents.OfType<ClipDocumentViewModel>())
            {
                if (ReferenceEquals(other, document) || other.Current.BoneCount != bones) continue;
                var tab = other;
                rows.Add(new GltfExportClipRow(Path.GetFileNameWithoutExtension(tab.DisplayName), tab.IsDirty ? "open tab · unsaved edits" : "open tab",
                    $"{tab.DisplayName} as it is now in its tab ({tab.OriginText})", _ => Task.FromResult(tab.Current), false));
            }
            var library = snapshot.FindMesh(MeshName);
            var tableClips = new HashSet<LibraryClip>(ReferenceEqualityComparer.Instance);
            IEnumerable<LibraryClip> candidates;
            if (library is { } lm && lm.BoneCount == bones)
            {
                foreach (var u in snapshot.ClipsUsedByMesh(lm.Name, usage))
                {
                    if (u.Clip is { } c) tableClips.Add(c);
                }
                candidates = snapshot.PreviewClipCandidates(lm.Name, usage);
            }
            else candidates = snapshot.CompatibleClips(bones);
            var assets = shell.Assets;
            foreach (var clip in candidates)
            {
                if (IsClipExport && string.Equals(clip.BaseName, baseName, StringComparison.OrdinalIgnoreCase)) continue;
                var entry = clip;
                rows.Add(new GltfExportClipRow(entry.BaseName, tableClips.Contains(entry) ? "tables" : $"{bones} bones",
                    $"{entry.Name} · {entry.Location.DisplayLocation}", ct => assets.LoadClipAsync(entry, ct), false));
            }
        }
        Clips = new FilteredList<GltfExportClipRow>(rows, r => r.Name);
        foreach (var r in rows) r.CheckedChanged += (_, _) => OnClipsChanged();

        string folder = shell.DefaultOutputFolder(shell.Settings.Get<string>(FolderSettingKey));
        _outputPath = Path.Combine(folder, GltfText.SafeFileName(baseName, "export") + ".gltf");

        BrowseCommand = new RelayCommand(Browse, () => !_isExporting);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => CanExport);
        StopCommand = new RelayCommand(Stop, () => _isExporting);
        CheckAllClipsCommand = new RelayCommand(() => SetAllClips(true), () => !_isExporting && Clips.Items.Count > 0);
        ClearClipsCommand = new RelayCommand(() => SetAllClips(false), () => !_isExporting && Clips.All.Any(r => r.IsChecked && !r.IsFixed));
    }

    public RfaWorkspace Shell { get; }

    /// <summary>The document being exported.</summary>
    public DocumentViewModel Document { get; }

    /// <summary>True when a clip document is exported (on its preview mesh).</summary>
    public bool IsClipExport { get; }

    /// <summary>The mesh's name ("ult2_guard.v3c").</summary>
    public string MeshName { get; }

    /// <summary>The output's default base name.</summary>
    public string BaseName { get; }

    public int BoneCount { get; }

    public int SphereCount { get; }

    public int PropCount { get; }

    public int SubmeshCount { get; }

    public bool HasSkeleton => BoneCount > 0;

    /// <summary>A warning when a clip does not fit its preview mesh, or null.</summary>
    public string? MismatchText { get; }

    public string Title => "Export to glTF";

    /// <summary>"Export ult2_guard.v3c to glTF".</summary>
    public string Heading => IsClipExport ? $"Export {Document.DisplayName} to glTF" : $"Export {MeshName} to glTF";

    public string Description => IsClipExport
        ? $"Writes the clip as a glTF animation on its preview mesh, {MeshName}, in REDUX's conventions, so Blender, REDUX and glTF viewers can read it. Add other clips of the same rig to export them together."
        : "Writes the mesh, its skeleton and any clips you pick as glTF 2.0 in REDUX's conventions, so Blender, REDUX and glTF viewers can read it.";

    /// <summary>"26 bones · 1 submesh, 3 LODs · 6 collision spheres · 4 prop points".</summary>
    public string SourceSummary =>
        string.Join(" · ", new[]
        {
            IsClipExport ? $"On {MeshName}" : null,
            HasSkeleton ? GltfText.Count(BoneCount, "bone") : "no skeleton (static mesh)",
            $"{GltfText.Count(SubmeshCount, "submesh", "submeshes")}, {GltfText.Count(Lods.Count, "LOD")}",
            GltfText.Count(SphereCount, "collision sphere"),
            GltfText.Count(PropCount, "prop point"),
        }.Where(s => s is not null));

    // ── What to include ─────────────────────────────────────────────────────

    /// <summary>One row per LOD.</summary>
    public ObservableCollection<GltfLodRow> Lods { get; } = [];

    /// <summary>Export the skeleton (and spheres, props, clips) without geometry.</summary>
    public bool SkeletonOnly
    {
        get => _skeletonOnly;
        set
        {
            if (!Set(ref _skeletonOnly, value)) return;
            RaiseAll(nameof(IncludeMesh));
            OnOptionChanged();
        }
    }

    /// <summary>True when geometry is exported.</summary>
    public bool IncludeMesh => !_skeletonOnly;

    public bool IncludeSpheres
    {
        get => _includeSpheres;
        set { if (Set(ref _includeSpheres, value)) OnOptionChanged(); }
    }

    public bool IncludeProps
    {
        get => _includeProps;
        set { if (Set(ref _includeProps, value)) OnOptionChanged(); }
    }

    public bool IncludeTextures
    {
        get => _includeTextures;
        set { if (Set(ref _includeTextures, value)) OnOptionChanged(); }
    }

    public string SpheresLabel => $"Collision spheres ({SphereCount})";

    public string PropsLabel => $"Prop points ({PropCount})";

    public bool CanIncludeTextures => _resolver is not null;

    public string TexturesToolTip => _resolver is not null
        ? "Decode each RF texture (found the way the game finds it) and write it as a PNG beside the .gltf, or inside the .glb. Off: materials keep their RF texture names but have no image."
        : "No game directory or search folder is set, so textures cannot be found (Tools › Settings).";

    // ── Clips ───────────────────────────────────────────────────────────────

    /// <summary>Clips to add as animations (the clip document itself first and always included).</summary>
    public FilteredList<GltfExportClipRow> Clips { get; }

    public bool HasClipChoices => !Clips.IsEmpty;

    /// <summary>"3 clips will be exported as animations".</summary>
    public string ClipsSummary
    {
        get
        {
            int n = Clips.All.Count(r => r.IsChecked);
            if (!HasSkeleton) return "A static mesh has no skeleton, so it carries no animations.";
            if (Clips.IsEmpty) return $"No clips with {BoneCount} bones are open or in the library.";
            return n == 0 ? "No clips: the mesh and skeleton only." : $"{GltfText.Count(n, "clip")} will be exported as animations.";
        }
    }

    public string ClipsHint => IsClipExport
        ? $"Other clips with {BoneCount} bones (open tabs first, then the library) can go in the same file."
        : $"Clips with {BoneCount} bones: open tabs first, then the library (the clips the game's tables give this mesh first).";

    public RelayCommand CheckAllClipsCommand { get; }

    public RelayCommand ClearClipsCommand { get; }

    private void SetAllClips(bool value)
    {
        foreach (var r in value ? Clips.Items.AsEnumerable() : Clips.All)
        {
            if (!r.IsFixed) r.IsChecked = value;
        }
    }

    // ── Format and extras ───────────────────────────────────────────────────

    public bool IsGlb
    {
        get => _isGlb;
        set
        {
            if (!Set(ref _isGlb, value)) return;
            Raise(nameof(IsGltf));
            OutputPath = Path.ChangeExtension(_outputPath, value ? ".glb" : ".gltf");
        }
    }

    public bool IsGltf
    {
        get => !_isGlb;
        set => IsGlb = !value;
    }

    public string GltfFormatText =>
        "A .gltf (JSON) with a .bin beside it and one PNG per texture. REDUX, Blender and every glTF viewer read it; keep the files together.";

    public string GlbFormatText =>
        "One self-contained .glb with the textures inside. Blender and viewers read it; REDUX's importer reads only .gltf with its .bin.";

    /// <summary>Write the <c>rf_keys</c> sampler extras.</summary>
    public bool WriteKeyExtras
    {
        get => _writeKeyExtras;
        set { if (Set(ref _writeKeyExtras, value)) OnOptionChanged(); }
    }

    public string KeyExtrasRoundTripText => _writeKeyExtras
        ? "Round trip: importing this file back into Cairn restores every key exactly — an unedited clip comes back byte-identical."
        : "Round trip: importing back into Cairn converts the glTF keys (sampled pose within about 0.02°; eases become extra keys).";

    public string KeyExtrasReduxText => _writeKeyExtras
        ? "REDUX ignores these extras: it reads the header extras (start, end, ramps) and the glTF keys, which are written either way."
        : "REDUX is unaffected: it reads the header extras (start, end, ramps) and the glTF keys, which are written either way. The file is a little smaller.";

    // ── Output ──────────────────────────────────────────────────────────────

    /// <summary>The file to write (.gltf or .glb).</summary>
    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (!Set(ref _outputPath, value ?? string.Empty)) return;
            Raise(nameof(FilesText));
            OnOptionChanged();
        }
    }

    /// <summary>What will be written.</summary>
    public string FilesText
    {
        get
        {
            string name;
            try { name = Path.GetFileNameWithoutExtension(_outputPath); }
            catch (ArgumentException) { name = "export"; }
            if (string.IsNullOrWhiteSpace(name)) return string.Empty;
            return _isGlb
                ? $"{name}.glb (textures inside)"
                : $"{name}.gltf, {name}.bin" + (_includeTextures && IncludeMesh && _resolver is not null ? " and one PNG per texture" : "");
        }
    }

    public RelayCommand BrowseCommand { get; }

    private void Browse()
    {
        string? folder = null;
        try { folder = Path.GetDirectoryName(_outputPath); }
        catch (ArgumentException) { }
        if (folder is null || Shell.IsGameDirectory(folder)) folder = Shell.DefaultOutputFolder(Shell.Settings.Get<string>(FolderSettingKey));
        string filter = _isGlb ? "glTF binary (*.glb)|*.glb|glTF (*.gltf)|*.gltf" : "glTF (*.gltf)|*.gltf|glTF binary (*.glb)|*.glb";
        string name;
        try { name = Path.GetFileName(_outputPath); }
        catch (ArgumentException) { name = BaseName + (_isGlb ? ".glb" : ".gltf"); }
        string? chosen = Shell.Dialogs.SaveFile(folder, "Export to glTF", filter, name, _isGlb ? ".glb" : ".gltf");
        if (chosen is null) return;
        if (chosen.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)) IsGlb = true;
        else if (chosen.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) IsGlb = false;
        else chosen += _isGlb ? ".glb" : ".gltf";
        OutputPath = chosen;
        _confirmedPath = chosen;
    }

    // ── Export ──────────────────────────────────────────────────────────────

    public RelayCommand ExportCommand { get; }

    public RelayCommand StopCommand { get; }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (!Set(ref _isExporting, value)) return;
            Raise(nameof(IsIdle));
            RefreshCommands();
        }
    }

    public bool IsIdle => !_isExporting;

    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>Why the export cannot run or failed; null when fine.</summary>
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

    /// <summary>The last export's summary.</summary>
    public IReadOnlyList<GltfResultLine> ResultLines
    {
        get => _resultLines;
        private set
        {
            if (!Set(ref _resultLines, value)) return;
            Raise(nameof(HasResult));
        }
    }

    public bool HasResult => _resultLines.Count > 0;

    /// <summary>The last export's Core result (tests).</summary>
    public GltfExportResult? LastResult { get; private set; }

    /// <summary>Files the last export wrote.</summary>
    public IReadOnlyList<string> LastFiles { get; private set; } = [];

    /// <summary>True when Export would run.</summary>
    public bool CanExport => !_isExporting && Validate() is null;

    /// <summary>Why Export is disabled ("Choose at least one LOD…"), or null.</summary>
    public string? Problem => _isExporting ? null : Validate();

    /// <summary>The Core options the dialog's choices give.</summary>
    public GltfExportOptions Options => new()
    {
        IncludeMesh = IncludeMesh,
        Lods = IncludeMesh ? [.. Lods.Where(l => l.IsChecked).Select(l => l.Index)] : null,
        IncludeCollisionSpheres = _includeSpheres,
        IncludePropPoints = _includeProps,
        TextureResolver = _includeTextures ? _resolver : null,
        WriteKeyExtras = _writeKeyExtras,
    };

    private string? Validate()
    {
        if (IncludeMesh && Lods.Count > 0 && !Lods.Any(l => l.IsChecked)) return "Choose at least one LOD, or export the skeleton only.";
        if (!IncludeMesh && !HasSkeleton) return "A static mesh has no skeleton: export its geometry (pick a LOD).";
        if (string.IsNullOrWhiteSpace(_outputPath)) return "Choose where to write the file.";
        string full, folder;
        try
        {
            full = Path.GetFullPath(_outputPath);
            folder = Path.GetDirectoryName(full) ?? string.Empty;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The output path is not a usable file path.";
        }
        if (!(full.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || full.EndsWith(".glb", StringComparison.OrdinalIgnoreCase)))
            return "The output file must end in .gltf or .glb.";
        if (Shell.IsGameDirectory(folder))
            return "That is the game folder: a loose file there changes what the game loads. Choose another folder.";
        return null;
    }

    private void OnOptionChanged()
    {
        RaiseAll(nameof(CanExport), nameof(Problem), nameof(FilesText), nameof(KeyExtrasRoundTripText), nameof(KeyExtrasReduxText));
        RefreshCommands();
    }

    private void OnClipsChanged()
    {
        Raise(nameof(ClipsSummary));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ExportCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
        CheckAllClipsCommand.RaiseCanExecuteChanged();
        ClearClipsCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stops the export between stages (nothing is written once stopped).</summary>
    public void Stop() => _cts?.Cancel();

    /// <summary>
    /// Runs the export: reads the checked clips, builds the document, writes the files. Returns the
    /// Core result, or null when it was refused, failed or stopped (the reason is in <see cref="Error"/>
    /// or <see cref="ProgressText"/>).
    /// </summary>
    public async Task<GltfExportResult?> ExportAsync()
    {
        if (_isExporting) return null;
        if (Validate() is { } problem)
        {
            Error = problem;
            return null;
        }
        string path = Path.GetFullPath(_outputPath);
        bool glb = path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
        if (File.Exists(path) && !string.Equals(_confirmedPath, path, StringComparison.OrdinalIgnoreCase)
            && !Shell.Dialogs.Confirm($"{Path.GetFileName(path)} already exists.", "Exporting replaces it (and its .bin and textures, for a .gltf).", "_Replace"))
            return null;

        var rows = Clips.All.Where(r => r.IsChecked).ToList();
        var options = Options;
        var mesh = _mesh;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        Error = null;
        ResultLines = [];
        IsExporting = true;
        using var busy = BusyTracker.Begin("glTF export");
        try
        {
            var clips = new List<GltfExportClip>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                ProgressText = $"Reading clips ({i + 1} of {rows.Count}): {rows[i].Name}…";
                var clip = await rows[i].Load(token).ConfigureAwait(true);
                string name = rows[i].Name;
                for (int n = 2; !names.Add(name); n++) name = $"{rows[i].Name} ({n})";
                clips.Add(new GltfExportClip(name, clip));
            }
            if (options.TextureResolver is not null)
            {
                ProgressText = "Waiting for the game's archives to be indexed…";
                await Shell.Assets.ArchivesIndexed.WaitAsync(token).ConfigureAwait(true);
            }
            token.ThrowIfCancellationRequested();
            ProgressText = options.TextureResolver is not null && options.IncludeMesh
                ? "Building the glTF document (geometry, skeleton, textures, animations)…"
                : "Building the glTF document…";
            var result = await Task.Run(() => GltfExport.Export(mesh, clips, options), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            // The .bin and PNG files go beside the .gltf under their own names (a texture's PNG is named after
            // the texture): ask once before replacing any that are already there, unless this very export
            // wrote them last time.
            if (!glb && !string.Equals(_lastExportedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var existing = GltfWriter.SideFiles(result.Document, path).Where(File.Exists).ToList();
                if (existing.Count > 0 && !Shell.Dialogs.Confirm(
                        existing.Count == 1 ? $"{Path.GetFileName(existing[0])} is already beside {Path.GetFileName(path)}." : $"{existing.Count} files beside {Path.GetFileName(path)} already exist.",
                        "Exporting writes the buffer and the textures next to the .gltf and would replace:\n" + string.Join("\n", existing.Take(12).Select(Path.GetFileName))
                        + (existing.Count > 12 ? $"\n…and {existing.Count - 12} more" : string.Empty) + "\n\nExport into an empty folder to keep them, or as .glb (one file).",
                        "_Replace"))
                {
                    ProgressText = "Nothing was written.";
                    return null;
                }
            }
            ProgressText = "Writing files…";
            var files = await Task.Run(() => Write(result.Document, path, glb), CancellationToken.None).ConfigureAwait(true);
            LastResult = result;
            LastFiles = files;
            ResultLines = Summarise(result, files, clips, options);
            ProgressText = $"Exported {Path.GetFileName(path)}.";
            _confirmedPath = path;
            _lastExportedPath = path;
            if (!Shell.IsDiagnosticRun)
            {
                Shell.Settings.Set(FolderSettingKey, Path.GetDirectoryName(path));
                Shell.SaveSettingsSoon();
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Stopped. Nothing was written.";
            return null;
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            Error = "The export failed: " + GltfText.UserMessage(ex);
            ProgressText = string.Empty;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("glTF export", ex);
            Error = GltfText.Unexpected(ex);
            ProgressText = string.Empty;
            return null;
        }
        finally
        {
            _cts = null;
            IsExporting = false;
        }
    }

    /// <summary>Writes the document and returns the files written.</summary>
    private static List<string> Write(GltfDocument doc, string path, bool glb)
    {
        var files = new List<string> { path };
        if (glb)
        {
            GltfWriter.WriteGlb(doc, path);
            return files;
        }
        GltfWriter.WriteGltf(doc, path);
        string folder = Path.GetDirectoryName(path) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(path);
        if (doc.Buffers.Count > 0 && doc.Buffers[0].Data is not null) files.Add(Path.Combine(folder, baseName + ".bin"));
        foreach (var image in doc.Images)
        {
            if (image.Data is null || image.Uri is null || image.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || !GltfReader.IsSafeRelativeUri(image.Uri)) continue;
            files.Add(Path.Combine(folder, Uri.UnescapeDataString(image.Uri)));
        }
        return files;
    }

    private List<GltfResultLine> Summarise(GltfExportResult result, IReadOnlyList<string> files, IReadOnlyList<GltfExportClip> clips, GltfExportOptions options)
    {
        var lines = new List<GltfResultLine>();
        string folder = Path.GetDirectoryName(files[0]) ?? string.Empty;
        int images = files.Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        var shown = files.Where(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
        if (images > 0) shown.Add(GltfText.Count(images, "PNG texture"));
        lines.Add(new GltfResultLine($"Wrote {GltfText.Count(files.Count, "file")} to {folder}: {string.Join(", ", shown)}"));
        if (options.IncludeMesh)
        {
            var lods = options.Lods is { } l ? string.Join(", ", l.Order()) : "all";
            int embedded = result.Document.Images.Count(i => i.Data is not null);
            lines.Add(new GltfResultLine($"Mesh: LOD {lods} of {SubmeshCount} submesh{(SubmeshCount == 1 ? "" : "es")} · {GltfText.Count(result.Document.Materials.Count, "material")}"
                + (options.TextureResolver is null ? " (no images)" : $" · {GltfText.Count(embedded, "texture image")}")));
        }
        else lines.Add(new GltfResultLine("Skeleton only (no geometry)."));
        lines.Add(new GltfResultLine(
            $"{(HasSkeleton ? GltfText.Count(BoneCount, "bone") : "No skeleton")}"
            + (options.IncludeCollisionSpheres ? $" · {GltfText.Count(SphereCount, "collision sphere")}" : "")
            + (options.IncludePropPoints ? $" · {GltfText.Count(PropCount, "prop point")}" : "")));
        lines.Add(new GltfResultLine(clips.Count == 0
            ? "No animations."
            : $"{GltfText.Count(clips.Count, "animation")}: {string.Join(", ", clips.Take(12).Select(c => c.Name))}{(clips.Count > 12 ? $" and {clips.Count - 12} more" : "")}"));
        if (clips.Count > 0)
            lines.Add(new GltfResultLine(options.WriteKeyExtras
                ? "Key extras written: importing back here restores the clips exactly."
                : "No key extras: importing back here converts the glTF keys."));
        if (result.BakedRotationTracks > 0)
            lines.Add(new GltfResultLine($"{GltfText.Count(result.BakedRotationTracks, "rotation track")} with eases gained samples every frame inside eased segments (glTF has no eases)."));
        if (result.MorphOmittedClips.Length > 0)
            lines.Add(new GltfResultLine($"Morph (vertex) animation left out of {string.Join(", ", result.MorphOmittedClips)}: glTF morph targets cannot carry it faithfully.", true));
        if (result.MissingTextures.Length > 0)
            lines.Add(new GltfResultLine($"Textures not found or not decodable (their materials have no image): {string.Join(", ", result.MissingTextures)}", true));
        foreach (string w in result.Warnings)
        {
            if (result.MissingTextures.Any(t => w.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
            if (result.MorphOmittedClips.Any(c => w.Contains(c, StringComparison.OrdinalIgnoreCase) && w.Contains("morph", StringComparison.OrdinalIgnoreCase))) continue;
            lines.Add(new GltfResultLine(w, true));
        }
        return lines;
    }
}
