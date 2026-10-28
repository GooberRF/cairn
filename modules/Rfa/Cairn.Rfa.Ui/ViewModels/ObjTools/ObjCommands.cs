using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Ui.ViewModels.GltfTools;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;
using Cairn.Rfa.Ui.Views.Dialogs.Obj;

namespace Cairn.Rfa.Ui.ViewModels.ObjTools;

/// <summary>
/// File › Import › Mesh from OBJ…, File › Export › OBJ…, .obj files opened or dropped on the window, and the Convert to…
/// conversions that involve OBJ: a mesh to .obj, a glTF to .obj (through the glTF mesh import), and an .obj to
/// .v3m/.v3c or glTF (through the OBJ import, then the mesh glTF export).
/// </summary>
public sealed class ObjCommands
{
    /// <summary>The file filter for OBJ pickers.</summary>
    public const string OpenFilter = "Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*";

    private const string ImportFolderKey = "rfa.objImportFolder";
    private readonly RfaWorkspace _shell;

    public ObjCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ImportMeshCommand = new RelayCommand(PickAndImport);
        ExportCommand = new RelayCommand(Export, () => _shell.ActiveDocument is MeshDocumentViewModel m && HasGeometry(m));
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.HasDocument)) ExportCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>File › Import › Mesh from OBJ….</summary>
    public RelayCommand ImportMeshCommand { get; }

    /// <summary>File › Export › OBJ… (mesh documents).</summary>
    public RelayCommand ExportCommand { get; }

    private static bool HasGeometry(MeshDocumentViewModel m) => m.Current.Submeshes.Any(s => s.Lods.Any(l => l.Batches.Any(b => b.TriangleCount > 0)));

    /// <summary>An .obj opened or dropped: the import dialog.</summary>
    public void ImportFile(string path) => ShowImport(path, null, null);

    private void PickAndImport()
    {
        string? folder = _shell.Settings.Get<string>(ImportFolderKey);
        string[] paths = _shell.Dialogs.OpenFiles(folder, "Import mesh from OBJ", OpenFilter, false);
        if (paths.Length == 0) return;
        if (!_shell.IsDiagnosticRun)
        {
            _shell.Settings.Set(ImportFolderKey, Path.GetDirectoryName(paths[0]));
            _shell.SaveSettingsSoon();
        }
        ShowImport(paths[0], null, null);
    }

    /// <summary>Shows the import dialog; returns the mesh document it opened, or null.</summary>
    private MeshDocumentViewModel? ShowImport(string path, ObjSource? source, V3dKind? kind)
    {
        _shell.CommitPendingEdits();
        var model = new ObjMeshImportViewModel(_shell, path, source, kind);
        ObjMeshImportWindow.ShowModal(_shell.Dialogs.Owner, model);
        return model.Imported;
    }

    private void Export()
    {
        if (_shell.ActiveDocument is not MeshDocumentViewModel document) return;
        _ = ExportDocumentAsync(document, interactive: true, outputFolder: null);
    }

    /// <summary>
    /// The OBJ export of <paramref name="document"/>: the export window, or with <paramref name="interactive"/> false its
    /// defaults (LOD 0, textures, metadata) written into <paramref name="outputFolder"/> (else the folder it offers).
    /// Returns the .obj written, or null when cancelled or refused (reported).
    /// </summary>
    internal async Task<string?> ExportDocumentAsync(MeshDocumentViewModel document, bool interactive, string? outputFolder)
    {
        ArgumentNullException.ThrowIfNull(document);
        _shell.CommitPendingEdits();
        ObjExportViewModel model;
        try
        {
            model = new ObjExportViewModel(_shell, document);
        }
        catch (ArgumentException ex)
        {
            _shell.Dialogs.ShowError($"{document.DisplayName} cannot be exported to OBJ.", GltfText.UserMessage(ex));
            return null;
        }
        if (interactive)
        {
            ObjExportWindow.ShowModal(_shell.Dialogs.Owner, model);
            return model.LastResult is null ? null : model.LastFiles[0];
        }
        model.AskBeforeReplacing = false;
        string folder = outputFolder ?? Path.GetDirectoryName(model.OutputPath) ?? _shell.DefaultOutputFolder();
        model.OutputPath = FreePath(folder, Path.GetFileNameWithoutExtension(model.OutputPath), ".obj");
        if (await model.ExportAsync().ConfigureAwait(true) is null)
        {
            _shell.Dialogs.ShowError($"{document.DisplayName} was not exported.", model.Error ?? model.ProgressText);
            return null;
        }
        return model.LastFiles[0];
    }

    // ── Convert to… ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>folder/name.ext</c>, or <c>name (2).ext</c>… when that (or, for an .obj, its .mtl; for a .gltf, its .bin) is
    /// taken: a conversion without a dialog never replaces a file.
    /// </summary>
    internal static string FreePath(string folder, string name, string extension)
    {
        bool Taken(string path) => File.Exists(path)
            || extension == ".obj" && File.Exists(Path.ChangeExtension(path, ".mtl"))
            || extension == ".gltf" && File.Exists(Path.ChangeExtension(path, ".bin"));
        string candidate = Path.Combine(folder, name + extension);
        for (int n = 2; Taken(candidate); n++) candidate = Path.Combine(folder, $"{name} ({n}){extension}");
        return candidate;
    }

    /// <summary>True for a .obj name.</summary>
    public static bool IsObj(string name) => name.EndsWith(".obj", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a Convert to… request that involves OBJ (see <see cref="RfaModule"/>'s targets). Interactive requests show the
    /// dialogs (the import's pre-flight, then the export or Save As); others use the defaults and write into
    /// <see cref="AssetConversionRequest.OutputFolder"/>. Returns the main file written, or null (reported).
    /// </summary>
    internal async Task<string?> ConvertAsync(AssetConversionRequest request, IDocumentKind? meshKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        string target = request.TargetExtension.ToLowerInvariant();
        string baseName = GltfText.SafeFileName(Path.GetFileNameWithoutExtension(request.FileName), "converted");
        string? folder = request.FilePath is { } p ? Path.GetDirectoryName(Path.GetFullPath(p)) : null;
        string outputFolder = request.OutputFolder ?? _shell.DefaultOutputFolder(_shell.Settings.Get<string>(ObjExportViewModel.FolderSettingKey));
        try
        {
            if (IsObj(request.FileName)) return await FromObjAsync(request, target, baseName, folder, outputFolder).ConfigureAwait(true);
            if (target != ".obj") return null;
            if (RfaWorkspace.IsGltf(request.FileName)) return await FromGltfAsync(request, baseName, folder, outputFolder).ConfigureAwait(true);

            // A mesh: its tab, or a packfile entry opened in one first (the export works on a tab: its textures).
            if (request.Document is not MeshDocumentViewModel document)
            {
                if (meshKind is null) return null;
                string origin = request.ArchivePath is { } archive ? $"{request.FileName} in {Path.GetFileName(archive)}" : request.FileName;
                if (_shell.Host is not { } host || meshKind.OpenBytes(request.Bytes, request.FileName, origin) is not MeshDocumentViewModel opened) return null;
                host.AddDocument(opened);
                document = opened;
            }
            return await ExportDocumentAsync(document, request.Interactive, request.Interactive ? null : outputFolder).ConfigureAwait(true);
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            _shell.Dialogs.ShowError($"Could not convert {request.FileName}", GltfText.UserMessage(ex));
            return null;
        }
    }

    /// <summary>.obj → .v3m/.v3c (written, or added to the packfile) or glTF.</summary>
    private async Task<string?> FromObjAsync(AssetConversionRequest request, string target, string baseName, string? folder, string outputFolder)
    {
        bool gltf = target is ".gltf" or ".glb";
        V3dKind kind = target == ".v3c" ? V3dKind.Character : V3dKind.StaticMesh;
        var source = await ReadObjAsync(request, folder).ConfigureAwait(true);
        if (request.Interactive)
        {
            var document = ShowImport(request.FilePath ?? request.FileName, source, gltf ? null : kind);
            if (document is null) return null;
            if (gltf) return await _shell.Gltf.ExportDocumentAsync(document, target == ".glb", interactive: true, outputFolder: null).ConfigureAwait(true);
            // The mesh is in a new tab: Save As writes it where the user chooses.
            return _shell.Host?.SaveAs(document) == true ? document.FilePath : null;
        }

        var options = new ObjMeshImportOptions { Kind = gltf ? null : kind };
        var result = await Task.Run(() => ObjMeshImport.Import(source, options, request.FileName)).ConfigureAwait(true);
        if (result.Mesh is not { } mesh)
        {
            var errors = result.Issues.Where(i => i.Severity == MeshImportSeverity.Error).Select(i => $"{i.Code}: {i.Message}");
            _shell.Dialogs.ShowError($"Could not convert {request.FileName}", string.Join("\n", errors));
            return null;
        }
        if (gltf)
        {
            Directory.CreateDirectory(outputFolder);
            string path = FreePath(outputFolder, baseName, target);
            var resolver = _shell.Assets.ResolverFor(folder);
            await _shell.Assets.ArchivesIndexed.ConfigureAwait(true);
            await Task.Run(() => GltfExport.ExportToFile(path, mesh, null, new GltfExportOptions { TextureResolver = resolver, Generator = "Cairn" })).ConfigureAwait(true);
            return path;
        }
        string name = baseName + (mesh.Kind == V3dKind.Character ? ".v3c" : ".v3m");
        byte[] bytes = V3dWriter.Write(mesh);
        if (request.AddToArchive is { } add && request.OutputFolder is null)
            return add($"Convert {request.FileName} to {Path.GetExtension(name)}", [(name, bytes)], false)?.FirstOrDefault();
        Directory.CreateDirectory(outputFolder);
        string output = FreePath(outputFolder, Path.GetFileNameWithoutExtension(name), Path.GetExtension(name));
        AtomicFile.WriteAllBytes(output, bytes);
        return output;
    }

    /// <summary>.gltf/.glb → .obj through the glTF mesh import.</summary>
    private async Task<string?> FromGltfAsync(AssetConversionRequest request, string baseName, string? folder, string outputFolder)
    {
        var read = request.ReadSibling;
        Func<string, byte[]?> resolve = folder is not null
            ? uri => { string path = Path.Combine(folder, Uri.UnescapeDataString(uri)); return File.Exists(path) ? File.ReadAllBytes(path) : read?.Invoke(uri); }
            : uri => read?.Invoke(Uri.UnescapeDataString(uri));
        var bytes = request.Bytes;
        string fileName = request.FileName;
        GltfDocument doc;
        using (BusyTracker.Begin("read " + fileName))
            doc = await Task.Run(() => GltfReader.Read(bytes, fileName, resolve)).ConfigureAwait(true);
        if (request.Interactive)
        {
            _shell.CommitPendingEdits();
            var model = new GltfMeshImportViewModel(_shell, request.FilePath ?? request.FileName, doc);
            if (!GltfMeshImportWindow.ShowModal(_shell.Dialogs.Owner, model) || _shell.ActiveDocument is not MeshDocumentViewModel imported) return null;
            return await ExportDocumentAsync(imported, interactive: true, outputFolder: null).ConfigureAwait(true);
        }
        var result = await Task.Run(() => GltfMeshImport.Import(doc, null, fileName)).ConfigureAwait(true);
        if (result.Mesh is not { } mesh)
        {
            var errors = result.Issues.Where(i => i.Severity == MeshImportSeverity.Error).Select(i => $"{i.Code}: {i.Message}");
            _shell.Dialogs.ShowError($"Could not convert {request.FileName}", string.Join("\n", errors));
            return null;
        }
        Directory.CreateDirectory(outputFolder);
        string objPath = FreePath(outputFolder, baseName, ".obj");
        var resolver = _shell.Assets.ResolverFor(folder);
        await _shell.Assets.ArchivesIndexed.ConfigureAwait(true);
        await Task.Run(() => ObjMeshExport.ExportToFile(objPath, mesh, new ObjMeshExportOptions { TextureResolver = resolver, SourceName = fileName })).ConfigureAwait(true);
        return objPath;
    }

    /// <summary>The request's .obj with its material libraries (beside the file, or elsewhere in its packfile).</summary>
    private static async Task<ObjSource> ReadObjAsync(AssetConversionRequest request, string? folder)
    {
        var bytes = request.Bytes;
        string name = request.FileName;
        var read = request.ReadSibling;
        Func<string, byte[]?> sibling = folder is not null
            ? n => { string path = Path.Combine(folder, n); return File.Exists(path) ? File.ReadAllBytes(path) : read?.Invoke(n); }
            : n => read?.Invoke(n);
        using var busy = BusyTracker.Begin("read " + name);
        return await Task.Run(() => ObjMeshImport.Read(bytes, name, sibling)).ConfigureAwait(true);
    }
}
