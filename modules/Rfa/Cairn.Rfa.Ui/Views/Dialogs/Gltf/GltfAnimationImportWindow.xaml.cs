using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.GltfTools;

namespace Cairn.Rfa.Ui.Views.Dialogs.Gltf;

/// <summary>
/// File › Import Animation from glTF…. The view-model owns the file, the bone map, the preview and the
/// import; this window restores and remembers its size, ends the view-model on every close (which stops
/// the preview and disposes its scene) and closes itself when the import has opened the clips.
/// </summary>
public partial class GltfAnimationImportWindow : Window
{
    private readonly GltfAnimationImportViewModel _model;
    private bool _modal;

    private GltfAnimationImportWindow(GltfAnimationImportViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, GltfAnimationImportViewModel.DialogKey);
        model.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            model.RequestClose -= OnRequestClose;
            GltfWindowSize.Remember(this, model.Shell, GltfAnimationImportViewModel.DialogKey);
            model.End();
        };
        Loaded += (_, _) => AnimationList.Focus();
    }

    /// <summary>The view-model behind the window.</summary>
    public GltfAnimationImportViewModel Model => _model;

    /// <summary>Shows the dialog modally. Returns true when clips were imported.</summary>
    public static bool ShowModal(Window? owner, GltfAnimationImportViewModel model)
    {
        var dialog = new GltfAnimationImportWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
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
    internal static GltfAnimationImportWindow CreateForCapture(GltfAnimationImportViewModel model) => new(model);

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_modal) DialogResult = true;
        else Close();
    }
}
