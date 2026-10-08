using Cairn.Assets;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// Phase 6 additions to the shell: the Retarget, Mesh and glTF command groups, and documents made in
/// memory (a retarget result, an import) that open as new unsaved tabs.
/// </summary>
public sealed partial class RfaWorkspace
{
    private Retargeting.RetargetCommands? _retarget;
    private MeshEditing.MeshCommands? _meshTools;
    private GltfTools.GltfCommands? _gltf;

    /// <summary>Clip › Retarget…, Tools › Batch Retarget… and the library's Retarget… entry.</summary>
    public Retargeting.RetargetCommands Retarget => _retarget ??= new Retargeting.RetargetCommands(this);

    /// <summary>The Mesh menu's editing commands (mesh documents).</summary>
    public MeshEditing.MeshCommands MeshTools => _meshTools ??= new MeshEditing.MeshCommands(this);

    /// <summary>File › Export to glTF…, Import Animation from glTF…, Import Mesh from glTF….</summary>
    public GltfTools.GltfCommands Gltf => _gltf ??= new GltfTools.GltfCommands(this);

    private ClipCreation.NewClipCommands? _newClips;

    /// <summary>File › New Clip…, Mesh › New Clip for This Mesh… and the library's entry (1.0.1).</summary>
    public ClipCreation.NewClipCommands NewClips => _newClips ??= new ClipCreation.NewClipCommands(this);

    /// <summary>True for a .gltf or .glb path.</summary>
    public static bool IsGltf(string path) =>
        path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens a clip made in memory as a new, unsaved tab (nothing is written until the user saves).
    /// <paramref name="previewMesh"/> (with its file name and the folder its textures resolve from) becomes
    /// the preview mesh; null lets the document choose one as usual.
    /// </summary>
    public ClipDocumentViewModel OpenNewClip(RfaClip clip, string name, V3dFile? previewMesh = null, string? previewMeshName = null, string? textureFolder = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)) name += ".rfa";
        var document = new ClipDocumentViewModel(this, clip, name, null, null);
        document.MarkAsNew();
        Add(document);
        if (previewMesh is not null) document.UsePreviewMesh(previewMesh, previewMeshName ?? "preview.v3c", textureFolder);
        return document;
    }

    /// <summary>Opens a mesh made in memory (an import) as a new, unsaved tab. A static mesh saves as .v3m.</summary>
    public MeshDocumentViewModel OpenNewMesh(V3dFile mesh, string name)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string extension = mesh.Kind == V3dKind.StaticMesh ? ".v3m" : ".v3c";
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name) + extension;
        var document = new MeshDocumentViewModel(this, mesh, name, null, null);
        document.MarkAsNew();
        Add(document);
        return document;
    }

    /// <summary>
    /// The folder a new output (retarget result, export) is offered in: <paramref name="remembered"/> when
    /// it still exists, else the last folder saved to, else the documents folder — never the game directory
    /// (it holds the game's own files).
    /// </summary>
    public string DefaultOutputFolder(string? remembered = null)
    {
        foreach (string? candidate in new[] { remembered, Settings.LastSaveFolder })
        {
            if (string.IsNullOrWhiteSpace(candidate) || IsGameDirectory(candidate)) continue;
            try
            {
                if (Directory.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { }
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>True when <paramref name="folder"/> is the game directory or inside it.</summary>
    public bool IsGameDirectory(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(Settings.GameDirectory)) return false;
        try
        {
            string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            string g = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Settings.GameDirectory));
            return f.Equals(g, StringComparison.OrdinalIgnoreCase)
                || f.StartsWith(g + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Reads a persisted dialog size ("retargetDialog" → width, height), or the fallback.</summary>
    public (double Width, double Height) DialogSize(string key, double width, double height) =>
        (LayoutSize(key + ".w", width), LayoutSize(key + ".h", height));

    /// <summary>Records a dialog's size for next time.</summary>
    public void SetDialogSize(string key, double width, double height)
    {
        SetLayoutSize(key + ".w", width);
        SetLayoutSize(key + ".h", height);
        SaveSettingsSoon();
    }

    /// <summary>
    /// Closes a tab without asking, throwing away unsaved changes and not offering it for Reopen Closed
    /// Tab (self-tests close the documents they made this way).
    /// </summary>
    internal void CloseDiscarding(DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        int index = Documents.IndexOf(document);
        if (index < 0) return;
        if (Host is not null)
        {
            // The shell owns the tabs (and recovery): close there, without the unsaved-changes prompt.
            document.DiscardOnClose = true;
            Host.Close(document);
            return;
        }
        Documents.Remove(document);
        Recovery.Discard(document.Id);
        _states.TryRemove(document.Id, out _);
        document.Dispose();
        if (Documents.Count == 0) ActiveDocument = null;
        else if (ReferenceEquals(ActiveDocument, document) || ActiveDocument is null)
            ActiveDocument = Documents[Math.Clamp(index, 0, Documents.Count - 1)];
        Raise(nameof(HasDocument));
        RefreshCommands();
    }

    /// <summary>The library entry for a mesh name, or null (helper for dialogs).</summary>
    public LibraryMesh? FindLibraryMesh(string? name) => string.IsNullOrWhiteSpace(name) ? null : Assets.Snapshot.FindMesh(name);
}
