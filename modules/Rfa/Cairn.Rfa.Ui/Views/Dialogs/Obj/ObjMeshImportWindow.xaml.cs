using System.Windows;
using Cairn.Rfa.Ui.ViewModels.ObjTools;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;

namespace Cairn.Rfa.Ui.Views.Dialogs.Obj;

/// <summary>
/// File › Import › Mesh from OBJ…. The view-model owns the build, the pre-flight list and the preview; this window
/// restores and remembers its size, ends the view-model on every close and closes itself once the mesh has been opened.
/// </summary>
public partial class ObjMeshImportWindow : Window
{
    private readonly ObjMeshImportViewModel _model;
    private bool _modal;

    private ObjMeshImportWindow(ObjMeshImportViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, ObjMeshImportViewModel.DialogKey);
        model.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            model.RequestClose -= OnRequestClose;
            GltfWindowSize.Remember(this, model.Shell, ObjMeshImportViewModel.DialogKey);
            model.End();
        };
        Loaded += (_, _) => StaticRadio.Focus();
    }

    /// <summary>The view-model behind the window.</summary>
    public ObjMeshImportViewModel Model => _model;

    /// <summary>Shows the dialog modally. Returns true when a mesh was imported.</summary>
    public static bool ShowModal(Window? owner, ObjMeshImportViewModel model)
    {
        var dialog = new ObjMeshImportWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
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
    internal static ObjMeshImportWindow CreateForCapture(ObjMeshImportViewModel model) => new(model);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_modal) DialogResult = true;
        else Close();
    }
}
