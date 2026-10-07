using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.GltfTools;

namespace Cairn.Rfa.Ui.Views.Dialogs.Gltf;

/// <summary>
/// File › Import Mesh from glTF…. The view-model owns the build, the pre-flight list and the preview;
/// this window restores and remembers its size, ends the view-model on every close and closes itself
/// once the mesh has been opened.
/// </summary>
public partial class GltfMeshImportWindow : Window
{
    private readonly GltfMeshImportViewModel _model;
    private bool _modal;

    private GltfMeshImportWindow(GltfMeshImportViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, GltfMeshImportViewModel.DialogKey);
        model.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            model.RequestClose -= OnRequestClose;
            GltfWindowSize.Remember(this, model.Shell, GltfMeshImportViewModel.DialogKey);
            model.End();
        };
        Loaded += (_, _) => CharacterRadio.Focus();
    }

    /// <summary>The view-model behind the window.</summary>
    public GltfMeshImportViewModel Model => _model;

    /// <summary>Shows the dialog modally. Returns true when a mesh was imported.</summary>
    public static bool ShowModal(Window? owner, GltfMeshImportViewModel model)
    {
        var dialog = new GltfMeshImportWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using (ModalScope.Enter()) return dialog.ShowDialog() == true;
        }
        finally
        {
            model.End();
        }
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static GltfMeshImportWindow CreateForCapture(GltfMeshImportViewModel model) => new(model);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_modal) DialogResult = true;
        else Close();
    }
}
