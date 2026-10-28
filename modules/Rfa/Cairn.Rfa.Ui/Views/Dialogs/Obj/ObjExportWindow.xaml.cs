using System.ComponentModel;
using System.Windows;
using Cairn.Rfa.Ui.ViewModels.ObjTools;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;

namespace Cairn.Rfa.Ui.Views.Dialogs.Obj;

/// <summary>
/// File › Export › OBJ…. The view-model owns the choices and the export; this window restores and remembers its size
/// and does not close while files are being written.
/// </summary>
public partial class ObjExportWindow : Window
{
    private readonly ObjExportViewModel _model;

    private ObjExportWindow(ObjExportViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, ObjExportViewModel.DialogKey);
        Closing += OnClosing;
        Closed += (_, _) => GltfWindowSize.Remember(this, model.Shell, ObjExportViewModel.DialogKey);
        Loaded += (_, _) => PathBox.Focus();
    }

    /// <summary>The view-model behind the window.</summary>
    public ObjExportViewModel Model => _model;

    /// <summary>Shows the dialog modally.</summary>
    public static void ShowModal(Window? owner, ObjExportViewModel model)
    {
        var dialog = new ObjExportWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) dialog.ShowDialog();
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static ObjExportWindow CreateForCapture(ObjExportViewModel model) => new(model);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // The files are being written (a few seconds at most): finish rather than leave half a set.
        if (_model.IsExporting) e.Cancel = true;
    }
}
