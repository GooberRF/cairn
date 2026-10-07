using System.Collections.ObjectModel;
using System.Globalization;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Animation;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;

namespace Cairn.Rfa.Ui.ViewModels.GltfTools;

/// <summary>One pre-flight finding as the dialog lists it.</summary>
public sealed record MeshImportIssueRow(MeshImportSeverity Severity, string Code, string Message, string Fix)
{
    public string Glyph => Severity switch
    {
        MeshImportSeverity.Error => "",
        MeshImportSeverity.Warning => "",
        _ => "",
    };

    public string BrushKey => Severity switch
    {
        MeshImportSeverity.Error => "Severity.Error",
        MeshImportSeverity.Warning => "Severity.Warning",
        _ => "Severity.Info",
    };

    public string SeverityText => Severity switch
    {
        MeshImportSeverity.Error => "Error",
        MeshImportSeverity.Warning => "Warning",
        _ => "Note",
    };

    public bool HasFix => Fix.Length > 0;

    /// <summary>"Error MI002: …".</summary>
    public string AutomationName => $"{SeverityText} {Code}: {Message}";
}

/// <summary>An open mesh document whose skeleton, spheres and prop points can be kept.</summary>
/// <param name="Document">The mesh document.</param>
public sealed record GltfKeepSource(MeshDocumentViewModel Document)
{
    public string Label => Document.DisplayName;

    public string ToolTip => $"{Document.DisplayName}: {GltfText.Count(Document.Scene.Skeleton.Count, "bone")}, as it is now in its tab";

    public override string ToString() => Label;
}

/// <summary>
/// File › Import Mesh from glTF…: options for <see cref="GltfMeshImport"/> (character or static build,
/// scale, texture names, LOD distances, keep the skeleton of an open mesh), the pre-flight list (MI0xx,
/// with a fix for each) that blocks Import while it has errors, a bind-pose preview of the built mesh and
/// a summary. Rebuilt off the UI thread whenever an option changes; Import opens a new unsaved mesh
/// document (a static build saves as .v3m).
/// </summary>
public sealed class GltfMeshImportViewModel : ObservableObject
{
    internal const string DialogKey = "rfa.gltfImportMeshDialog";

    private readonly object _docGate = new();
    private readonly BackgroundRecompute<GltfMeshImportResult> _build;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GltfDocument? _doc;
    private bool _isCharacter = true;
    private double _scale = 1;
    private bool _forceTga = true;
    private double _lod0;
    private double _lod1 = 10;
    private double _lod2 = 50;
    private bool _keepSkeleton;
    private GltfKeepSource? _keepSource;
    private GltfMeshImportResult? _result;
    private IReadOnlyList<MeshImportIssueRow> _issues = [];
    private IReadOnlyList<string> _summary = [];
    private string _sourceSummary = "Reading the file…";
    private string? _loadError;
    private string? _buildError;
    private bool _ended;

    /// <summary>Opens the model for a glTF file (read off the UI thread unless <paramref name="preloaded"/> is given).</summary>
    public GltfMeshImportViewModel(RfaWorkspace shell, string path, GltfDocument? preloaded = null)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FilePath = path;
        FileName = Path.GetFileName(path);
        Preview = new ViewportPreviewHost(shell);
        string? folder = null;
        try { folder = Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        Textures = shell.Assets.ResolverFor(folder);
        foreach (var d in shell.Documents.OfType<MeshDocumentViewModel>().Where(d => d.HasSkeleton)) KeepSources.Add(new GltfKeepSource(d));
        _keepSource = KeepSources.FirstOrDefault(k => ReferenceEquals(k.Document, shell.ActiveDocument)) ?? KeepSources.FirstOrDefault();
        _build = new BackgroundRecompute<GltfMeshImportResult>(shell.Dispatcher, "glTF mesh build", PrepareBuild, OnBuilt, OnBuildFailed);
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

    /// <summary>The dialog's own scene (the built mesh in its bind pose).</summary>
    public ViewportPreviewHost Preview { get; }

    /// <summary>Resolves the built mesh's textures (the glTF's folder first).</summary>
    internal Cairn.Assets.AssetResolver Textures { get; }

    /// <summary>Completes once the file is read (or the read failed).</summary>
    public Task Ready => _ready.Task;

    public string Title => "Import Mesh from glTF";

    public string Heading => $"Import a mesh from {FileName}";

    public string Description =>
        "Builds an RF mesh from the glTF's meshes: a character (.v3c, skinned to its skeleton) or a static mesh (.v3m). "
        + "Batches are made per material, weights are reduced to 4 per vertex, LODs come from REDUX extras or _LOD0/_LOD1 names, "
        + "and collision spheres and prop points from rf_csphere:: / rf_prop:: nodes.";

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

    // ── Options ─────────────────────────────────────────────────────────────

    public bool IsCharacter
    {
        get => _isCharacter;
        set
        {
            if (!Set(ref _isCharacter, value)) return;
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
        ? "Replace only the geometry: the bones, collision spheres and prop points come from the chosen open mesh, and the glTF's joints map to its bones by name. Clips made for that mesh keep working."
        : "Open the character mesh (.v3c) whose skeleton you want to keep, then import again.";

    /// <summary>Keep the skeleton, spheres and prop points of <see cref="KeepSource"/>.</summary>
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
    public GltfMeshImportOptions Options => new()
    {
        Kind = _isCharacter ? V3dKind.Character : V3dKind.StaticMesh,
        KeepSkeletonFrom = _keepSkeleton ? _keepSource?.Document.Current : null,
        Scale = (float)_scale,
        TextureNames = _forceTga ? TextureNamePolicy.ForceTga : TextureNamePolicy.Keep,
        DefaultLodDistances = [(float)_lod0, (float)_lod1, (float)_lod2],
    };

    // ── Result ──────────────────────────────────────────────────────────────

    public bool IsBusy => _build.IsPending;

    /// <summary>The last build.</summary>
    public GltfMeshImportResult? Result => _result;

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
    public string? PreviewMessage => _build.IsPending || _doc is null ? null
        : _loadError is not null ? "The file could not be read."
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

    /// <summary>"DW-Fungi2Anim.v3c".</summary>
    public string OutputName => GltfText.SafeFileName(Path.GetFileNameWithoutExtension(FileName), "imported") + (_isCharacter ? ".v3c" : ".v3m");

    // ── Import ──────────────────────────────────────────────────────────────

    public RelayCommand ImportCommand { get; }

    public event EventHandler? RequestClose;

    public bool CanImport => !_ended && Problem is null;

    /// <summary>Why Import is disabled, or null.</summary>
    public string? Problem
    {
        get
        {
            if (_loadError is not null) return _loadError;
            if (_doc is null) return "Reading the file…";
            if (_build.IsPending) return "Building the mesh…";
            if (_buildError is not null) return _buildError;
            if (_result is null) return "Building the mesh…";
            if (_result.HasErrors) return "The pre-flight list has errors: fix them in the glTF (or change the options) first.";
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
        var document = Shell.OpenNewMesh(mesh, OutputName);
        End();
        RequestClose?.Invoke(this, EventArgs.Empty);
        return document;
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
        if (_doc is not null) _build.Schedule();
    }

    private async Task LoadAsync(GltfDocument? preloaded)
    {
        try
        {
            var doc = preloaded ?? await GltfText.ReadAsync(FilePath).ConfigureAwait(true);
            if (_ended) return;
            int meshNodes, skins, spheres, props;
            lock (_docGate)
            {
                meshNodes = doc.Nodes.Count(n => n.Mesh is { } m && m >= 0 && m < doc.Meshes.Count);
                skins = doc.Skins.Count;
                spheres = doc.Nodes.Count(n => n.Name?.StartsWith(GltfSpace.CollisionSpherePrefix, StringComparison.Ordinal) == true);
                props = doc.Nodes.Count(n => n.Name?.StartsWith(GltfSpace.PropPointPrefix, StringComparison.Ordinal) == true);
            }
            _doc = doc;
            bool skinned = doc.Nodes.Any(n => n.Mesh is not null && n.Skin is not null);
            _isCharacter = skinned || (skins > 0 && meshNodes == 0);
            RaiseAll(nameof(IsCharacter), nameof(IsStatic), nameof(OutputName));
            SourceSummary = string.Join(" · ", new[]
            {
                GltfText.Count(meshNodes, "mesh node"),
                skinned ? "skinned" : skins > 0 ? "a skin no mesh uses" : "not skinned",
                spheres > 0 ? GltfText.Count(spheres, "collision sphere") : null,
                props > 0 ? GltfText.Count(props, "prop point") : null,
                doc.Animations.Count > 0 ? GltfText.Count(doc.Animations.Count, "animation") + " (not part of the mesh)" : null,
                string.IsNullOrWhiteSpace(doc.Asset.Generator) ? null : $"made by {doc.Asset.Generator}",
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
            RaiseAll(nameof(CanImport), nameof(Problem));
            ImportCommand.RaiseCanExecuteChanged();
        }
    }

    private Func<CancellationToken, GltfMeshImportResult> PrepareBuild()
    {
        var doc = _doc ?? throw new InvalidOperationException("The file is still being read.");
        if (!(_scale > 0) || !double.IsFinite(_scale)) throw new ArgumentException("The scale must be a positive number (1 keeps the file's size; glTF units are metres, as RF's).");
        if (_keepSkeleton && _keepSource is null) throw new ArgumentException("Choose the open mesh whose skeleton to keep.");
        var options = Options;
        string file = FileName;
        var gate = _docGate;
        return ct =>
        {
            ct.ThrowIfCancellationRequested();
            lock (gate) return GltfMeshImport.Import(doc, options, file);
        };
    }

    private void OnBuilt(GltfMeshImportResult result)
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

    private static List<string> Summarise(GltfMeshImportResult result)
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
        int spheres = mesh.CollisionSpheres.Count();
        int props = subs.FirstOrDefault()?.Lods.FirstOrDefault()?.PropPoints.Length ?? 0;
        lines.Add($"{(mesh.Kind == V3dKind.Character ? "Character (.v3c)" : "Static mesh (.v3m)")}: {GltfText.Count(subs.Count, "submesh", "submeshes")}, {GltfText.Count(lods, "LOD")}, {GltfText.Count(batches, "batch", "batches")} in all");
        lines.Add($"LOD 0: {GltfText.Count(vertices, "vertex", "vertices")}, {GltfText.Count(triangles, "triangle")}");
        if (lods > 1)
        {
            var distances = subs[0].LodDistances.Select(d => d.ToString("0.##", CultureInfo.CurrentCulture));
            lines.Add($"LOD distances: {string.Join(", ", distances)} m");
        }
        lines.Add($"{GltfText.Count(bones, "bone")} · {GltfText.Count(spheres, "collision sphere")} · {GltfText.Count(props, "prop point")}");
        lines.Add($"{GltfText.Count(textures.Count, "texture")}: {(textures.Count == 0 ? "none" : string.Join(", ", textures.Take(10)) + (textures.Count > 10 ? $" and {textures.Count - 10} more" : ""))}");
        return lines;
    }

    /// <summary>How to fix each pre-flight finding, in plain words.</summary>
    public static string FixFor(string code) => code switch
    {
        "MI001" => "Fix: the engine animates at most 50 bones. Remove or merge helper bones in your 3D tool, or keep the skeleton of an existing mesh.",
        "MI002" => "Fix: rename it in your 3D tool to fit (bone and sphere names 23 characters, submeshes 23, textures 31, prop points 67), using plain Latin letters.",
        "MI003" => "Fix: keep at most three LODs (name the objects _LOD0, _LOD1, _LOD2) and delete or merge the rest.",
        "MI004" => "Fix: use at most 7 materials per LOD: merge materials, or bake them onto a shared texture.",
        "MI005" => "Nothing to fix: oversized batches are split automatically (fewer vertices per material draws faster).",
        "MI006" => "Fix: weight-paint those vertices in your 3D tool if they should follow another bone.",
        "MI007" => "Fix: limit the influences to 4 per vertex in your 3D tool (Blender: Weights › Limit Total) to choose what is dropped.",
        "MI008" => "Fix: only triangles are imported; convert or delete the point and line objects.",
        "MI009" => "Fix: export normals from your 3D tool if the smoothing looks wrong.",
        "MI010" => "Fix: unwrap the mesh (add a UV map) in your 3D tool; textures need UVs.",
        "MI011" => "Fix: rename the joint to the kept mesh's bone it stands for, or weight its vertices to a bone that exists.",
        "MI012" => string.Empty,
        "MI013" => "Fix: export the mesh objects too, not only the armature (Blender: include the visible or selected meshes).",
        "MI014" => "Check the LOD order; name the objects _LOD0, _LOD1, _LOD2 to control it.",
        "MI015" => "Fix: weight vertices only to deforming bones, or add those joints to the skin.",
        "MI016" => "Choose Character (.v3c) to keep the skin.",
        "MI017" => "Fix: add an armature and skin the mesh to it, or build a static mesh (.v3m).",
        "MI018" => "Fix: apply the transforms in your 3D tool and re-export; the bind pose was rebuilt from the node transforms.",
        "MI019" => "Usually harmless (stock meshes have such vertices); normalise the weights in your 3D tool if a vertex looks wrong.",
        "MI099" => "Fix: the message names the limit that was hit; change that in your 3D tool and import again.",
        _ => string.Empty,
    };
}
