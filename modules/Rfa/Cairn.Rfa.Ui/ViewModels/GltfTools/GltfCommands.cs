using System.ComponentModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;
using Cairn.Formats.Gltf;

namespace Cairn.Rfa.Ui.ViewModels.GltfTools;

/// <summary>
/// File › Export to glTF…, Import Animation from glTF…, Import Mesh from glTF…, and glTF files opened
/// or dropped on the window (exposed on <see cref="RfaWorkspace.Gltf"/>).
/// </summary>
public sealed class GltfCommands
{
    private const string ImportFolderKey = "rfa.gltfImportFolder";
    private readonly RfaWorkspace _shell;

    public GltfCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        ExportCommand = new RelayCommand(Export, () => _shell.ActiveDocument is MeshDocumentViewModel or ClipDocumentViewModel);
        ImportAnimationCommand = new RelayCommand(() => PickAndImport(animation: true));
        ImportMeshCommand = new RelayCommand(() => PickAndImport(animation: false));
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    /// <summary>File › Export to glTF… (mesh and clip documents).</summary>
    public RelayCommand ExportCommand { get; }

    /// <summary>File › Import Animation from glTF….</summary>
    public RelayCommand ImportAnimationCommand { get; }

    /// <summary>File › Import Mesh from glTF….</summary>
    public RelayCommand ImportMeshCommand { get; }

    /// <summary>A .gltf/.glb opened or dropped: offers its animations and/or mesh for import.</summary>
    public async void ImportFile(string path)
    {
        GltfDocument doc;
        try
        {
            doc = await GltfText.ReadAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (GltfText.IsRefusal(ex))
        {
            _shell.Dialogs.ShowError($"{Path.GetFileName(path)} could not be read.", "It is not a glTF 2.0 file Cairn can read.", GltfText.UserMessage(ex));
            return;
        }
        bool hasAnimations = doc.Animations.Count > 0;
        bool hasMesh = doc.Nodes.Any(n => n.Mesh is { } m && m >= 0 && m < doc.Meshes.Count);
        if (!hasAnimations && !hasMesh)
        {
            _shell.Dialogs.ShowError($"{Path.GetFileName(path)} has nothing to import.", "It has no animations and no meshes.");
            return;
        }
        bool animation = hasAnimations;
        if (hasAnimations && hasMesh)
        {
            int choice = _shell.Dialogs.Choose(
                $"{Path.GetFileName(path)} has animations and a mesh. Which do you want to import?",
                $"Animation: its {GltfText.Count(doc.Animations.Count, "animation")} become new clips for a target mesh you choose.\n"
                + "Mesh: its geometry and skeleton become a new .v3c (or a static .v3m).\n"
                + "You can import the other part afterwards from the File menu.",
                ["Import _animation", "Import _mesh", "Cancel"], 2);
            if (choice == 2) return;
            animation = choice == 0;
        }
        ShowImport(path, doc, animation);
    }

    /// <summary>Re-queries the commands' enabled state.</summary>
    public void Refresh() => ExportCommand.RaiseCanExecuteChanged();

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.HasDocument)) Refresh();
    }

    private void Export()
    {
        if (_shell.ActiveDocument is not { } document) return;
        _shell.CommitPendingEdits();
        GltfExportViewModel model;
        try
        {
            model = new GltfExportViewModel(_shell, document);
        }
        catch (ArgumentException ex)
        {
            _shell.Dialogs.ShowError($"{document.DisplayName} cannot be exported yet.", GltfText.UserMessage(ex));
            return;
        }
        GltfExportWindow.ShowModal(_shell.Dialogs.Owner, model);
    }

    /// <summary>
    /// File › Convert to… glTF or GLB for <paramref name="document"/>: the export window with that format chosen, or with
    /// <paramref name="interactive"/> false the window's defaults written into <paramref name="outputFolder"/> (else
    /// the folder it offers). Returns the path written, or null when cancelled or refused (reported).
    /// </summary>
    internal async Task<string?> ExportDocumentAsync(DocumentViewModel document, bool glb, bool interactive, string? outputFolder)
    {
        ArgumentNullException.ThrowIfNull(document);
        _shell.CommitPendingEdits();
        GltfExportViewModel model;
        try
        {
            model = new GltfExportViewModel(_shell, document) { IsGlb = glb };
        }
        catch (ArgumentException ex)
        {
            _shell.Dialogs.ShowError($"{document.DisplayName} cannot be exported yet.", GltfText.UserMessage(ex));
            return null;
        }
        if (interactive)
        {
            GltfExportWindow.ShowModal(_shell.Dialogs.Owner, model);
            return model.LastResult is null ? null : Path.GetFullPath(model.OutputPath);
        }
        if (outputFolder is not null) model.OutputPath = Path.Combine(outputFolder, Path.GetFileName(model.OutputPath));
        if (await model.ExportAsync().ConfigureAwait(true) is null)
        {
            _shell.Dialogs.ShowError($"{document.DisplayName} was not exported.", model.Error ?? model.ProgressText);
            return null;
        }
        return Path.GetFullPath(model.OutputPath);
    }

    private void PickAndImport(bool animation)
    {
        string? folder = _shell.Settings.Get<string>(ImportFolderKey);
        string[] paths = _shell.Dialogs.OpenFiles(folder, animation ? "Import animation from glTF" : "Import mesh from glTF", GltfText.OpenFilter, false);
        if (paths.Length == 0) return;
        if (!_shell.IsDiagnosticRun)
        {
            _shell.Settings.Set(ImportFolderKey, Path.GetDirectoryName(paths[0]));
            _shell.SaveSettingsSoon();
        }
        ShowImport(paths[0], null, animation);
    }

    private void ShowImport(string path, GltfDocument? doc, bool animation)
    {
        _shell.CommitPendingEdits();
        if (animation) GltfAnimationImportWindow.ShowModal(_shell.Dialogs.Owner, new GltfAnimationImportViewModel(_shell, path, doc));
        else GltfMeshImportWindow.ShowModal(_shell.Dialogs.Owner, new GltfMeshImportViewModel(_shell, path, doc));
    }
}
