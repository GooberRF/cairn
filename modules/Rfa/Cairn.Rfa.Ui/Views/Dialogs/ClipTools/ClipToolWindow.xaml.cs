using System.Windows;
using System.Windows.Input;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipTools;

namespace Cairn.Rfa.Ui.Views.Dialogs.ClipTools;

/// <summary>
/// The one window every Clip menu tool uses (in the manner of ATX Workbench's bulk timing dialog): the
/// tool's settings (a data template per view-model type, in ClipToolTemplates.xaml), what will change,
/// the document's transport to watch the live preview, and OK/Cancel. Every way of closing it — OK,
/// Cancel, Esc, the title bar's X, the diagnostic runner — ends the view-model, which clears the preview.
/// </summary>
public partial class ClipToolWindow : Window
{
    private readonly ClipDialogViewModel _model;

    private ClipToolWindow(ClipDialogViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        Closed += (_, _) => model.End();
        Loaded += (_, _) => ToolContent.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    /// <summary>The view-model behind the window.</summary>
    public ClipDialogViewModel Model => _model;

    /// <summary>Shows the tool modally beside the owner's viewport. Returns true when OK applied it.</summary>
    public static bool ShowModal(Window? owner, ClipDialogViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dialog = new ClipToolWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
        if (dialog.Owner is { } o) dialog.PlaceBeside(o);
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using (ModalScope.Enter()) return dialog.ShowDialog() == true;
        }
        finally
        {
            model.End();
        }
    }

    /// <summary>A window for the diagnostic capture or a self-test (shown non-modally by the caller).</summary>
    internal static ClipToolWindow CreateForCapture(ClipDialogViewModel model) => new(model);

    /// <summary>Puts the dialog over the owner's right-hand side (the inspector), so the viewport stays in sight.</summary>
    private void PlaceBeside(Window owner)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Rect area = owner.WindowState == WindowState.Maximized
            ? SystemParameters.WorkArea
            : new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
        Left = Math.Max(area.Left, area.Right - Width - 28);
        Top = area.Top + 90;
    }

    private bool _modal;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_model.Commit()) return;
        if (_modal) DialogResult = true;
        else Close();
    }
}
