using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Ui.ViewModels.GltfTools;

namespace Cairn.Rfa.Ui.ViewModels.ObjTools;

/// <summary>
/// File › Import › Mesh from OBJ…: options for <see cref="ObjMeshImport"/> (static or character build, the skeleton of
/// an open mesh to keep, scale, Z-up, texture names, LOD distances, Cairn's metadata), the pre-flight list (MI0xx, with
/// a fix for each) that blocks Import while it has errors, a preview of the built mesh and a summary. Rebuilt off the
/// UI thread whenever an option changes; Import opens a new unsaved mesh document.
/// </summary>
public sealed class ObjMeshImportViewModel : ObservableObject
{
    internal const string DialogKey = "rfa.objImportMeshDialog";

    private readonly BackgroundRecompute<ObjMeshImportResult> _build;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ObjSource? _source;
    private bool _isCharacter;
    private double _scale = 1;
    private bool _zUp;
    private bool _forceTga = true;
    private bool _useMetadata = true;
    private double _lod0;
    private double _lod1 = 10;
    private double _lod2 = 50;
    private bool _keepSkeleton;
    private GltfKeepSource? _keepSource;
    private ObjMeshImportResult? _result;
    private IReadOnlyList<MeshImportIssueRow> _issues = [];
    private IReadOnlyList<string> _summary = [];
    private string _sourceSummary = "Reading the file…";
    private string? _loadError;
    private string? _buildError;
    private bool _hasMetadata;
    private bool _ended;

    /// <summary>
    /// Opens the model for an .obj file (read off the UI thread with its .mtl files unless <paramref name="preloaded"/>
    /// is given; then <paramref name="path"/> only names it and gives the folder textures are looked up in).
    /// </summary>
    /// <param name="shell">The workspace.</param>
    /// <param name="path">The file.</param>
    /// <param name="preloaded">The file already read (a packfile entry), or null.</param>
    /// <param name="kind">The build to start with (Convert to… picks it), or null for what the file says.</param>
    public ObjMeshImportViewModel(RfaWorkspace shell, string path, ObjSource? preloaded = null, V3dKind? kind = null)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        FileName = Path.GetFileName(path);
        StartKind = kind;
        Preview = new ViewportPreviewHost(shell);
        string? folder = null;
        if (preloaded is null)
        {
            try { folder = Path.GetDirectoryName(Path.GetFullPath(path)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        Textures = shell.Assets.ResolverFor(folder);
        foreach (var d in shell.Documents.OfType<MeshDocumentViewModel>().Where(d => d.HasSkeleton)) KeepSources.Add(new GltfKeepSource(d));
        _keepSource = KeepSources.FirstOrDefault(k => ReferenceEquals(k.Document, shell.ActiveDocument)) ?? KeepSources.FirstOrDefault();
        _build = new BackgroundRecompute<ObjMeshImportResult>(shell.Dispatcher, "OBJ mesh build", PrepareBuild, OnBuilt, OnBuildFailed);
        _build.PendingChanged += (_, _) =>
        {
            RaiseAll(nameof(IsBusy), nameof(CanImport), nameof(Problem), nameof(PreviewMessage), nameof(HasPreviewMessage));
            ImportCommand?.RaiseCanExecuteChanged();
        };
        ImportCommand = new RelayCommand(() => Import(), () => CanImport);
        _ = LoadAsync(preloaded);
    }

    public RfaWorkspace Shell { get; }

    public string FilePath { get; }

    public string FileName { get; }

    /// <summary>The build the caller asked for, or null.</summary>
    public V3dKind? StartKind { get; }

    /// <summary>The dialog's own scene (the built mesh).</summary>
    public ViewportPreviewHost Preview { get; }

    /// <summary>Resolves the built mesh's textures (the .obj's folder first).</summary>
    internal Cairn.Assets.AssetResolver Textures { get; }

    /// <summary>Completes once the file is read (or the read failed).</summary>
    public Task Ready => _ready.Task;

    public string Title => "Import Mesh from OBJ";

    public string Heading => $"Import a mesh from {FileName}";

    public string Description =>
        "Builds an RF mesh from the OBJ's objects: one submesh per object or group (_LOD0/_LOD1 name endings give levels of detail), "
        + "one batch per material, polygons triangulated, and normals from the file or from its smoothing groups. "
        + "Texture names come from the .mtl's map_Kd.";

    public string SourceSummary
    {
        get => _sourceSummary;
        private set => Set(ref _sourceSummary, value);
    }

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (!Set(ref _loadError, value)) return;
            RaiseAll(nameof(CanImport), nameof(Problem));
        }
    }

    /// <summary>True when the file carries Cairn's metadata lines (an export of a Cairn mesh).</summary>
    public bool HasMetadata
    {
        get => _hasMetadata;
        private set => Set(ref _hasMetadata, value);
    }

    // ── Options ─────────────────────────────────────────────────────────────

    public bool IsCharacter
    {
        get => _isCharacter;
        set
        {
            if (!Set(ref _isCharacter, value)) return;
            if (!value && _keepSkeleton) KeepSkeleton = false;
            RaiseAll(nameof(IsStatic), nameof(OutputName));
            Schedule();
        }
    }

    public bool IsStatic
    {
        get => !_isCharacter;
        set => IsCharacter = !value;
    }

    public double Scale
    {
        get => _scale;
        set { if (Set(ref _scale, value)) Schedule(); }
    }

    /// <summary>Read the file as Z-up.</summary>
    public bool IsZUp
    {
        get => _zUp;
        set { if (Set(ref _zUp, value)) Schedule(); }
    }

    public bool IsForceTga
    {
        get => _forceTga;
        set
        {
            if (!Set(ref _forceTga, value)) return;
            Raise(nameof(IsKeepNames));
            Schedule();
        }
    }

    public bool IsKeepNames
    {
        get => !_forceTga;
        set => IsForceTga = !value;
    }

    /// <summary>Use Cairn's metadata lines when the file has them.</summary>
    public bool UseMetadata
    {
        get => _useMetadata;
        set { if (Set(ref _useMetadata, value)) Schedule(); }
    }

    public double Lod0Distance
    {
        get => _lod0;
        set { if (Set(ref _lod0, Math.Max(0, value))) Schedule(); }
    }

    public double Lod1Distance
    {
        get => _lod1;
        set { if (Set(ref _lod1, Math.Max(0, value))) Schedule(); }
    }

    public double Lod2Distance
    {
        get => _lod2;
        set { if (Set(ref _lod2, Math.Max(0, value))) Schedule(); }
    }

    /// <summary>Open character meshes whose skeleton, spheres and prop points can be kept.</summary>
    public ObservableCollection<GltfKeepSource> KeepSources { get; } = [];

    public bool HasKeepSources => KeepSources.Count > 0;

    public string KeepToolTip => HasKeepSources
        ? "OBJ has no skeleton: use the bones, collision spheres and prop points of the chosen open mesh, with every vertex following its nearest bone. Clips made for that mesh play on the result; weight-paint in a 3D tool and import glTF for real skinning."
        : "Open the character mesh (.v3c) whose skeleton you want to use, then import again.";

    /// <summary>Use the skeleton, spheres and prop points of <see cref="KeepSource"/>.</summary>
    public bool KeepSkeleton
    {
        get => _keepSkeleton;
        set
        {
            if (!Set(ref _keepSkeleton, value && HasKeepSources)) return;
            if (_keepSkeleton) IsCharacter = true;
            Schedule();
        }
    }

    public GltfKeepSource? KeepSource
    {
        get => _keepSource;
        set
        {
            if (!Set(ref _keepSource, value)) return;
            if (_keepSkeleton) Schedule();
        }
    }

    /// <summary>The Core options the dialog's fields give.</summary>
    public ObjMeshImportOptions Options => new()
    {
        Kind = _isCharacter ? V3dKind.Character : V3dKind.StaticMesh,
        KeepSkeletonFrom = _isCharacter && _keepSkeleton ? _keepSource?.Document.Current : null,
        Scale = (float)_scale,
        ZUp = _zUp,
        TextureNames = _forceTga ? TextureNamePolicy.ForceTga : TextureNamePolicy.Keep,
        DefaultLodDistances = [(float)_lod0, (float)_lod1, (float)_lod2],
        UseMetadata = _useMetadata,
    };

    // ── Result ──────────────────────────────────────────────────────────────

    public bool IsBusy => _build.IsPending;

    /// <summary>The last build.</summary>
    public ObjMeshImportResult? Result => _result;

    /// <summary>The pre-flight list, errors first.</summary>
    public IReadOnlyList<MeshImportIssueRow> Issues
    {
        get => _issues;
        private set
        {
            if (!Set(ref _issues, value)) return;
            RaiseAll(nameof(HasIssues), nameof(IssuesSummary), nameof(HasErrors));
        }
    }

    public bool HasIssues => _issues.Count > 0;

    /// <summary>True when a build finished with nothing to report.</summary>
    public bool ShowNoFindings => _result is not null && _issues.Count == 0;

    /// <summary>Why the preview is empty, or null while it shows a mesh (or is being built).</summary>
    public string? PreviewMessage => _build.IsPending ? null
        : _loadError is not null ? "The file could not be read."
        : _source is null ? null
        : _buildError is not null ? "Nothing to preview: the mesh could not be built."
        : _result is { Mesh: null } ? "Nothing to preview: the pre-flight errors below stop the mesh from being built."
        : null;

    public bool HasPreviewMessage => PreviewMessage is not null;

    public bool HasErrors => _issues.Any(i => i.Severity == MeshImportSeverity.Error);

    /// <summary>"2 errors · 1 warning · 3 notes", or "Nothing to report".</summary>
    public string IssuesSummary
    {
        get
        {
            if (_result is null) return _buildError is null ? "Building…" : "The mesh could not be built.";
            int e = _issues.Count(i => i.Severity == MeshImportSeverity.Error);
            int w = _issues.Count(i => i.Severity == MeshImportSeverity.Warning);
            int n = _issues.Count - e - w;
            if (_issues.Count == 0) return "Nothing to report: the mesh fits every engine limit.";
            var parts = new List<string>();
            if (e > 0) parts.Add(GltfText.Count(e, "error"));
            if (w > 0) parts.Add(GltfText.Count(w, "warning"));
            if (n > 0) parts.Add(GltfText.Count(n, "note"));
            return string.Join(" · ", parts) + (e > 0 ? " — fix the errors before importing." : ".");
        }
    }

    /// <summary>Submeshes, LODs, batches, vertices, bones, textures.</summary>
    public IReadOnlyList<string> SummaryLines
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    /// <summary>A refusal or failure of the build itself (not a pre-flight issue), or null.</summary>
    public string? BuildError
    {
        get => _buildError;
        private set
        {
            if (!Set(ref _buildError, value)) return;
            Raise(nameof(HasBuildError));
        }
    }

    public bool HasBuildError => _buildError is not null;

    /// <summary>"crate.v3m".</summary>
    public string OutputName => GltfText.SafeFileName(Path.GetFileNameWithoutExtension(FileName), "imported") + (_isCharacter ? ".v3c" : ".v3m");

    // ── Import ──────────────────────────────────────────────────────────────

    public RelayCommand ImportCommand { get; }

    public event EventHandler? RequestClose;

    /// <summary>The document Import opened, or null.</summary>
    public MeshDocumentViewModel? Imported { get; private set; }

    public bool CanImport => !_ended && Problem is null;

    /// <summary>Why Import is disabled, or null.</summary>
    public string? Problem
    {
        get
        {
            if (_loadError is not null) return _loadError;
            if (_source is null) return "Reading the file…";
            if (_build.IsPending) return "Building the mesh…";
            if (_buildError is not null) return _buildError;
            if (_result is null) return "Building the mesh…";
            if (_result.HasErrors) return "The pre-flight list has errors: fix them in the OBJ (or change the options) first.";
            if (_result.Mesh is null) return "The mesh could not be built.";
            return null;
        }
    }

    /// <summary>Waits until the file is read and the build has settled.</summary>
    public async Task<bool> SettleAsync(int timeoutMs = 120_000)
    {
        await Ready.ConfigureAwait(true);
        return await _build.SettleAsync(timeoutMs).ConfigureAwait(true);
    }

    /// <summary>Opens the built mesh as a new unsaved document (null when Import is not possible).</summary>
    public MeshDocumentViewModel? Import()
    {
        if (!CanImport || _result?.Mesh is not { } mesh) return null;
        Imported = Shell.OpenNewMesh(mesh, OutputName);
        End();
        RequestClose?.Invoke(this, EventArgs.Empty);
        return Imported;
    }

    /// <summary>Ends the dialog's live work (every close path). Idempotent.</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _build.Stop();
        Preview.Dispose();
        RaiseAll(nameof(CanImport), nameof(IsBusy));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private void Schedule()
    {
        if (_ended) return;
        RaiseAll(nameof(CanImport), nameof(Problem));
        ImportCommand.RaiseCanExecuteChanged();
        if (_source is not null) _build.Schedule();
    }

    private async Task LoadAsync(ObjSource? preloaded)
    {
        try
        {
            ObjSource source;
            if (preloaded is not null) source = preloaded;
            else
            {
                using var busy = BusyTracker.Begin("read " + FileName);
                string path = FilePath;
                source = await Task.Run(() => ObjMeshImport.ReadFile(path)).ConfigureAwait(true);
            }
            if (_ended) return;
            _source = source;
            var obj = source.Obj;
            HasMetadata = ObjMeshImport.HasCairnMetadata(obj);
            _isCharacter = StartKind is { } kind ? kind == V3dKind.Character : HasMetadata && ObjMeshImport.MetadataKind(obj) == V3dKind.Character;
            RaiseAll(nameof(IsCharacter), nameof(IsStatic), nameof(OutputName));
            int parts = obj.SplitIntoParts().Count;
            var materials = obj.Objects.SelectMany(o => o.Faces).Select(f => f.Material).Where(m => m is not null).Distinct(StringComparer.Ordinal).Count();
            SourceSummary = string.Join(" · ", new[]
            {
                GltfText.Count(parts, "object"),
                GltfText.Count(obj.FaceCount, "face"),
                GltfText.Count(obj.Positions.Count, "vertex", "vertices"),
                GltfText.Count(materials, "material"),
                obj.MaterialLibraries.Count == 0 ? "no .mtl" : source.MissingLibraries.Count > 0 ? $"{string.Join(", ", source.MissingLibraries)} not found" : string.Join(", ", obj.MaterialLibraries),
                obj.Comments.FirstOrDefault(c => c.ObjectIndex < 0)?.Text is { Length: > 0 } first ? first : null,
            }.Where(s => s is not null));
            _build.RunNow();
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            LoadError = $"{FileName} could not be read: {GltfText.UserMessage(ex)}";
            SourceSummary = "The file could not be read.";
        }
        finally
        {
            _ready.TrySetResult();
            RaiseAll(nameof(CanImport), nameof(Problem), nameof(PreviewMessage), nameof(HasPreviewMessage));
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    private Func<CancellationToken, ObjMeshImportResult> PrepareBuild()
    {
        var source = _source ?? throw new InvalidOperationException("The file is still being read.");
        if (!(_scale > 0) || !double.IsFinite(_scale)) throw new ArgumentException("The scale must be a positive number (1 keeps the file's size; RF uses metres).");
        if (_isCharacter && _keepSkeleton && _keepSource is null) throw new ArgumentException("Choose the open mesh whose skeleton to use.");
        var options = Options;
        string file = FileName;
        // The documents are only read: an import never changes them, and no other thread writes them.
        return ct =>
        {
            ct.ThrowIfCancellationRequested();
            return ObjMeshImport.Import(source, options, file);
        };
    }

    private void OnBuilt(ObjMeshImportResult result)
    {
        _result = result;
        BuildError = null;
        Issues = [.. result.Issues
            .OrderBy(i => i.Severity == MeshImportSeverity.Error ? 0 : i.Severity == MeshImportSeverity.Warning ? 1 : 2)
            .Select(i => new MeshImportIssueRow(i.Severity, i.Code, i.Message, FixFor(i.Code)))];
        SummaryLines = Summarise(result);
        Preview.SetClip(null);
        Preview.SetMesh(result.Mesh, OutputName, Textures);
        GltfText.FrameSoon(Preview);
        RaiseAll(nameof(Result), nameof(IssuesSummary), nameof(CanImport), nameof(Problem), nameof(ShowNoFindings), nameof(PreviewMessage), nameof(HasPreviewMessage));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private void OnBuildFailed(string message)
    {
        _result = null;
        BuildError = message;
        Issues = [];
        SummaryLines = [];
        Preview.SetMesh(null, null, null);
        RaiseAll(nameof(Result), nameof(IssuesSummary), nameof(CanImport), nameof(Problem), nameof(ShowNoFindings), nameof(PreviewMessage), nameof(HasPreviewMessage));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private static List<string> Summarise(ObjMeshImportResult result)
    {
        var lines = new List<string>();
        if (result.Mesh is not { } mesh)
        {
            lines.Add("No mesh was built (see the errors).");
            return lines;
        }
        var subs = mesh.Submeshes.ToList();
        int lods = subs.Count == 0 ? 0 : subs.Max(s => s.Lods.Length);
        var lod0 = subs.SelectMany(s => s.Lods.Take(1)).ToList();
        int batches = subs.Sum(s => s.Lods.Sum(l => l.Batches.Length));
        int vertices = lod0.Sum(l => l.Batches.Sum(b => b.Positions.Length));
        int triangles = lod0.Sum(l => l.Batches.Sum(b => b.Triangles.Length));
        var textures = subs.SelectMany(s => s.Materials).Select(m => m.DiffuseMap.Text).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        int bones = Skeleton.FromFile(mesh).Count;
        int props = subs.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints.Length ?? 0;
        lines.Add($"{(mesh.Kind == V3dKind.Character ? "Character (.v3c)" : "Static mesh (.v3m)")}: {GltfText.Count(subs.Count, "submesh", "submeshes")}, {GltfText.Count(lods, "LOD")}, {GltfText.Count(batches, "batch", "batches")} in all");
        lines.Add($"LOD 0: {GltfText.Count(vertices, "vertex", "vertices")}, {GltfText.Count(triangles, "triangle")}");
        if (lods > 1)
        {
            var distances = subs.First(s => s.Lods.Length == lods).LodDistances.Select(d => d.ToString("0.##", CultureInfo.CurrentCulture));
            lines.Add($"LOD distances: {string.Join(", ", distances)} m");
        }
        lines.Add(mesh.Kind == V3dKind.Character
            ? $"{GltfText.Count(bones, "bone")} · {GltfText.Count(mesh.CollisionSpheres.Count(), "collision sphere")} · {GltfText.Count(props, "prop point")}"
            : $"No bones · {GltfText.Count(props, "prop point")}");
        lines.Add($"{GltfText.Count(textures.Count, "texture")}: {(textures.Count == 0 ? "none" : string.Join(", ", textures.Take(10)) + (textures.Count > 10 ? $" and {textures.Count - 10} more" : ""))}");
        if (result.HasMetadata) lines.Add("Restored from Cairn's metadata lines (render flags, double-sided faces, material properties, LOD distances, prop points).");
        return lines;
    }

    /// <summary>How to fix each pre-flight finding, in plain words (the glTF import's for the codes they share).</summary>
    public static string FixFor(string code) => code switch
    {
        "MI002" => "Fix: rename it in your 3D tool to fit (objects 23 characters, textures 31), using plain Latin letters. Long object names are shortened for you.",
        "MI009" => "Fix: export normals from your 3D tool (Blender: Geometry › Normals) if the shading looks wrong.",
        "MI010" => "Fix: unwrap the mesh (add a UV map) in your 3D tool and export UV coordinates; textures need them.",
        "MI012" => string.Empty,
        "MI013" => "Fix: export the mesh objects as faces (not only lines or points), or check that the right file was chosen.",
        "MI017" => "Fix: open the character mesh (.v3c) whose skeleton to use and tick “use its skeleton”, or build a static mesh (.v3m).",
        "MI020" => "Nothing to fix for a mesh: lines, points and curves are not geometry the game draws.",
        "MI021" => "Fix: keep the .mtl next to the .obj (same name as its mtllib line), or rename the materials after their textures.",
        "MI022" => "Check the texture names in the summary; set a diffuse image (map_Kd) on each material in your 3D tool to control them.",
        "MI023" => "Nothing to fix; triangulate in your 3D tool first if you want to control how polygons are split.",
        "MI024" => "Usually harmless; merge vertices by distance in your 3D tool to remove degenerate faces.",
        "MI025" => "Fix: for real skinning, weight-paint the mesh to the skeleton in your 3D tool and import it as glTF.",
        "MI026" => string.Empty,
        "MI027" => string.Empty,
        _ => GltfMeshImportViewModel.FixFor(code),
    };
}
