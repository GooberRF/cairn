using System.ComponentModel;
using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.GltfTools;

namespace Cairn.Rfa.Ui.Views.Dialogs.Gltf;

/// <summary>
/// File › Export to glTF…. The view-model owns the choices and the worker; this window restores and
/// remembers its size, stops a running export when it closes, and is modal under <see cref="ModalScope"/>.
/// </summary>
public partial class GltfExportWindow : Window
{
    private const string SizeKey = "rfa.gltfExportDialog";
    private readonly GltfExportViewModel _model;

    private GltfExportWindow(GltfExportViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, SizeKey);
        Closing += OnClosing;
        Closed += (_, _) => GltfWindowSize.Remember(this, model.Shell, SizeKey);
        Loaded += (_, _) => PathBox.Focus();
    }

    /// <summary>The view-model behind the window.</summary>
    public GltfExportViewModel Model => _model;

    /// <summary>Shows the dialog modally.</summary>
    public static void ShowModal(Window? owner, GltfExportViewModel model)
    {
        var dialog = new GltfExportWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (ModalScope.Enter()) dialog.ShowDialog();
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static GltfExportWindow CreateForCapture(GltfExportViewModel model) => new(model);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // A running export stops between stages; nothing is written after that.
        if (_model.IsExporting) _model.Stop();
    }
}

/// <summary>Restores and remembers a glTF dialog's size (per dialog, in the settings).</summary>
internal static class GltfWindowSize
{
    public static void Restore(Window window, ViewModels.RfaWorkspace shell, string key)
    {
        var (width, height) = shell.DialogSize(key, window.Width, window.Height);
        if (width >= window.MinWidth) window.Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
        if (height >= window.MinHeight) window.Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
    }

    public static void Remember(Window window, ViewModels.RfaWorkspace shell, string key)
    {
        if (window.WindowState != WindowState.Normal || shell.IsDiagnosticRun || window.ActualWidth <= 0) return;
        shell.SetDialogSize(key, window.ActualWidth, window.ActualHeight);
    }
}
