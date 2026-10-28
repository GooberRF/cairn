using System.Globalization;
using Cairn.Assets;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Ui.ViewModels.GltfTools;

namespace Cairn.Rfa.Ui.ViewModels.ObjTools;

/// <summary>A LOD choice of the OBJ export: one LOD index, or every LOD (null).</summary>
/// <param name="Lod">The LOD, or null for every LOD.</param>
/// <param name="Label">"LOD 0 · most detailed".</param>
public sealed record ObjLodChoice(int? Lod, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// File › Export › OBJ… and Convert to › OBJ for a mesh document: the LOD (or every LOD as its own object), PNG
/// textures, Cairn's metadata lines and the output file, written through <see cref="ObjMeshExport"/> off the UI thread.
/// The result (files, what OBJ cannot carry, missing textures) stays in the dialog.
/// </summary>
public sealed class ObjExportViewModel : ObservableObject
{
    internal const string FolderSettingKey = "rfa.objExportFolder";
    internal const string DialogKey = "rfa.objExportDialog";

    private readonly V3dFile _mesh;
    private readonly AssetResolver? _resolver;
    private ObjLodChoice _lod;
    private bool _includeTextures = true;
    private bool _writeMetadata = true;
    private string _outputPath;
    private string? _confirmedPath;
    private string? _lastExportedPath;
    private bool _isExporting;
    private string _progressText = string.Empty;
    private string? _error;
    private IReadOnlyList<GltfResultLine> _resultLines = [];

    /// <summary>Builds the dialog's model for a mesh document.</summary>
    /// <exception cref="ArgumentException">The document has no geometry to export.</exception>
    public ObjExportViewModel(RfaWorkspace shell, MeshDocumentViewModel document)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _mesh = document.Current;
        if (!_mesh.Submeshes.Any(s => s.Lods.Any(l => l.Batches.Any(b => b.TriangleCount > 0))))
            throw new ArgumentException($"{document.DisplayName} has no triangles to export.");
        _resolver = document.Scene.TextureResolver ?? document.Resolver;
        MeshName = document.DisplayName;
        BaseName = Path.GetFileNameWithoutExtension(document.FileName);
        var subs = _mesh.Submeshes.ToList();
        SubmeshCount = subs.Count;
        int lodCount = subs.Max(s => s.Lods.Length);
        var distances = subs.First(s => s.Lods.Length == lodCount).LodDistances;
        for (int i = 0; i < lodCount; i++)
        {
            string note = i == 0 ? "most detailed" : i == lodCount - 1 ? "least detailed" : string.Empty;
            if (i < distances.Length) note += (note.Length > 0 ? ", " : string.Empty) + "from " + distances[i].ToString("0.##", CultureInfo.CurrentCulture) + " m";
            LodChoices.Add(new ObjLodChoice(i, $"LOD {i}" + (note.Length > 0 ? $" ({note})" : string.Empty)));
        }
        if (lodCount > 1) LodChoices.Add(new ObjLodChoice(null, "Every LOD, each as its own object (name_LOD0, name_LOD1…)"));
        _lod = LodChoices[0];
        string folder = shell.DefaultOutputFolder(shell.Settings.Get<string>(FolderSettingKey));
        _outputPath = Path.Combine(folder, GltfText.SafeFileName(BaseName, "export") + ".obj");
        BrowseCommand = new RelayCommand(Browse, () => !_isExporting);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => CanExport);
    }

    public RfaWorkspace Shell { get; }

    /// <summary>The mesh document exported.</summary>
    public MeshDocumentViewModel Document { get; }

    /// <summary>"crate.v3m".</summary>
    public string MeshName { get; }

    /// <summary>The output's default base name.</summary>
    public string BaseName { get; }

    public int SubmeshCount { get; }

    public bool IsCharacter => _mesh.Kind == V3dKind.Character;

    public string Title => "Export to OBJ";

    public string Heading => $"Export {MeshName} to OBJ";

    public string Description =>
        "Writes the mesh as Wavefront OBJ with a .mtl material library, for Blender, 3ds Max and most 3D tools. "
        + "Positions are in metres with Y up and X mirrored, as the glTF export writes them, so both formats line up.";

    /// <summary>"Static mesh · 2 submeshes, 3 LODs · 4 textures".</summary>
    public string SourceSummary
    {
        get
        {
            var subs = _mesh.Submeshes.ToList();
            int textures = subs.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            return string.Join(" · ", new[]
            {
                IsCharacter ? $"Character with {GltfText.Count(_mesh.Bones.Length, "bone")}" : "Static mesh",
                $"{GltfText.Count(subs.Count, "submesh", "submeshes")}, {GltfText.Count(LodChoices.Count(c => c.Lod is not null), "LOD")}",
                GltfText.Count(textures, "texture"),
            });
        }
    }

    /// <summary>What OBJ cannot carry, for a character (null for a static mesh).</summary>
    public string? CharacterNote => IsCharacter
        ? "OBJ holds geometry only: the mesh is written in its bind pose, without its skeleton, bone weights or collision spheres. Export glTF to keep them."
        : null;

    // ── Choices ─────────────────────────────────────────────────────────────

    /// <summary>LOD 0, LOD 1… and every LOD.</summary>
    public List<ObjLodChoice> LodChoices { get; } = [];

    public ObjLodChoice Lod
    {
        get => _lod;
        set
        {
            if (value is null || !Set(ref _lod, value)) return;
            OnOptionChanged();
        }
    }

    public bool HasLodChoice => LodChoices.Count > 1;

    public bool IncludeTextures
    {
        get => _includeTextures;
        set { if (Set(ref _includeTextures, value)) OnOptionChanged(); }
    }

    public bool CanIncludeTextures => _resolver is not null;

    public string TexturesToolTip => _resolver is not null
        ? "Decode each RF texture (found the way the game finds it) and write it as a PNG beside the .obj; the .mtl names the PNGs. Off: the .mtl names the RF textures."
        : "No game directory or search folder is set, so textures cannot be found (Tools › Settings).";

    /// <summary>Write Cairn's <c># rf_…</c> lines.</summary>
    public bool WriteMetadata
    {
        get => _writeMetadata;
        set { if (Set(ref _writeMetadata, value)) OnOptionChanged(); }
    }

    public string MetadataText => _writeMetadata
        ? "Importing the file back into Cairn restores the submeshes, LODs, materials, render flags, double-sided faces and prop points. Other tools ignore these comment lines (and drop them when they save)."
        : "Without them, importing back gives default render flags and material properties, single-sided faces and no prop points.";

    /// <summary>The Core options the dialog's choices give.</summary>
    public ObjMeshExportOptions Options => new()
    {
        Lod = _lod.Lod,
        TextureResolver = _includeTextures ? _resolver : null,
        WriteMetadata = _writeMetadata,
        SourceName = Document.FileName,
    };

    // ── Output ──────────────────────────────────────────────────────────────

    public string OutputPath
    {
        get => _outputPath;
        set
        {
            if (!Set(ref _outputPath, value ?? string.Empty)) return;
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
            return $"{name}.obj, {name}.mtl" + (_includeTextures && _resolver is not null ? " and one PNG per texture" : string.Empty);
        }
    }

    public RelayCommand BrowseCommand { get; }

    private void Browse()
    {
        string? folder = null;
        try { folder = Path.GetDirectoryName(_outputPath); }
        catch (ArgumentException) { }
        if (folder is null || Shell.IsGameDirectory(folder)) folder = Shell.DefaultOutputFolder(Shell.Settings.Get<string>(FolderSettingKey));
        string name;
        try { name = Path.GetFileName(_outputPath); }
        catch (ArgumentException) { name = BaseName + ".obj"; }
        string? chosen = Shell.Dialogs.SaveFile(folder, "Export to OBJ", "Wavefront OBJ (*.obj)|*.obj", name, ".obj");
        if (chosen is null) return;
        if (!chosen.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) chosen += ".obj";
        OutputPath = chosen;
        _confirmedPath = chosen;
    }

    // ── Export ──────────────────────────────────────────────────────────────

    public RelayCommand ExportCommand { get; }

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

    /// <summary>
    /// False for a conversion without a dialog: textures already beside the .obj are replaced without asking (the
    /// caller picked a free name for the .obj and .mtl, and a texture's PNG is the same image again).
    /// </summary>
    internal bool AskBeforeReplacing { get; set; } = true;

    /// <summary>The last export's Core result (tests).</summary>
    public ObjMeshExportResult? LastResult { get; private set; }

    /// <summary>Files the last export wrote, the .obj first.</summary>
    public IReadOnlyList<string> LastFiles { get; private set; } = [];

    public bool CanExport => !_isExporting && Validate() is null;

    /// <summary>Why Export is disabled, or null.</summary>
    public string? Problem => _isExporting ? null : Validate();

    private string? Validate()
    {
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
        if (!full.EndsWith(".obj", StringComparison.OrdinalIgnoreCase)) return "The output file must end in .obj.";
        if (Shell.IsGameDirectory(folder)) return "That is the game folder, which holds the game's own files. Choose another folder.";
        return null;
    }

    private void OnOptionChanged()
    {
        RaiseAll(nameof(CanExport), nameof(Problem), nameof(FilesText), nameof(MetadataText));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        ExportCommand.RaiseCanExecuteChanged();
        BrowseCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Builds and writes the files. Returns the Core result, or null when it was refused, declined or failed (the
    /// reason is in <see cref="Error"/> or <see cref="ProgressText"/>).
    /// </summary>
    public async Task<ObjMeshExportResult?> ExportAsync()
    {
        if (_isExporting) return null;
        if (Validate() is { } problem)
        {
            Error = problem;
            return null;
        }
        string path = Path.GetFullPath(_outputPath);
        if (File.Exists(path) && !string.Equals(_confirmedPath, path, StringComparison.OrdinalIgnoreCase)
            && !Shell.Dialogs.Confirm($"{Path.GetFileName(path)} already exists.", "Exporting replaces it (and its .mtl and textures).", "_Replace"))
            return null;
        var options = Options;
        var mesh = _mesh;
        Error = null;
        ResultLines = [];
        IsExporting = true;
        using var busy = BusyTracker.Begin("OBJ export");
        try
        {
            if (options.TextureResolver is not null)
            {
                ProgressText = "Waiting for the game's archives to be indexed…";
                await Shell.Assets.ArchivesIndexed.ConfigureAwait(true);
            }
            ProgressText = options.TextureResolver is not null ? "Building the OBJ (geometry and textures)…" : "Building the OBJ…";
            var result = await Task.Run(() => ObjMeshExport.Export(mesh, options)).ConfigureAwait(true);
            // The .mtl and PNG files go beside the .obj under their own names: ask once before replacing any.
            if (AskBeforeReplacing && !string.Equals(_lastExportedPath, path, StringComparison.OrdinalIgnoreCase))
            {
                var existing = result.SideFiles(path).Where(File.Exists).ToList();
                if (existing.Count > 0 && !Shell.Dialogs.Confirm(
                        existing.Count == 1 ? $"{Path.GetFileName(existing[0])} is already beside {Path.GetFileName(path)}." : $"{existing.Count} files beside {Path.GetFileName(path)} already exist.",
                        "Exporting writes the material library and the textures next to the .obj and would replace:\n" + string.Join("\n", existing.Take(12).Select(Path.GetFileName))
                        + (existing.Count > 12 ? $"\n…and {existing.Count - 12} more" : string.Empty) + "\n\nExport into an empty folder to keep them.",
                        "_Replace"))
                {
                    ProgressText = "Nothing was written.";
                    return null;
                }
            }
            ProgressText = "Writing files…";
            var files = await Task.Run(() => ObjMeshExport.WriteFiles(path, result)).ConfigureAwait(true);
            LastResult = result;
            LastFiles = files;
            ResultLines = Summarise(result, files, options);
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
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            Error = "The export failed: " + GltfText.UserMessage(ex);
            ProgressText = string.Empty;
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write("OBJ export", ex);
            Error = GltfText.Unexpected(ex);
            ProgressText = string.Empty;
            return null;
        }
        finally
        {
            IsExporting = false;
        }
    }

    private static List<GltfResultLine> Summarise(ObjMeshExportResult result, IReadOnlyList<string> files, ObjMeshExportOptions options)
    {
        var lines = new List<GltfResultLine>();
        string folder = Path.GetDirectoryName(files[0]) ?? string.Empty;
        int images = files.Count(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        var shown = files.Where(f => !f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).Select(Path.GetFileName).ToList();
        if (images > 0) shown.Add(GltfText.Count(images, "PNG texture"));
        lines.Add(new GltfResultLine($"Wrote {GltfText.Count(files.Count, "file")} to {folder}: {string.Join(", ", shown)}"));
        var obj = result.Obj;
        lines.Add(new GltfResultLine($"{(options.Lod is { } l ? $"LOD {l}" : "Every LOD")}: {GltfText.Count(obj.Objects.Count, "object")}, {GltfText.Count(obj.FaceCount, "triangle")}, "
            + $"{GltfText.Count(obj.Positions.Count, "position")}, {GltfText.Count(result.Mtl.Materials.Count, "material")}"));
        lines.Add(new GltfResultLine(options.WriteMetadata ? "Cairn's metadata lines written: importing back here restores what OBJ cannot hold." : "No metadata lines."));
        if (result.MissingTextures.Length > 0)
            lines.Add(new GltfResultLine($"Textures not found or not decodable (the .mtl names the RF texture): {string.Join(", ", result.MissingTextures)}", true));
        foreach (string w in result.Warnings.Where(w => !w.StartsWith("Texture '", StringComparison.Ordinal))) lines.Add(new GltfResultLine(w, true));
        return lines;
    }
}
