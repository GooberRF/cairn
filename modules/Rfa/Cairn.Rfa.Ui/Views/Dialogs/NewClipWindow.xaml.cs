using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.ClipCreation;
using Cairn.Rfa.Ui.Views.Dialogs.Gltf;

namespace Cairn.Rfa.Ui.Views.Dialogs;

/// <summary>
/// File › New Clip…. The view-model owns the mesh, the starting pose, the preview and Create; this window
/// restores and remembers its size, ends the view-model on every close (which disposes the preview) and
/// closes itself when Create has opened the clip.
/// </summary>
public partial class NewClipWindow : Window
{
    private readonly NewClipViewModel _model;
    private bool _modal;

    private NewClipWindow(NewClipViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        GltfWindowSize.Restore(this, model.Shell, NewClipViewModel.DialogKey);
        model.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            model.RequestClose -= OnRequestClose;
            GltfWindowSize.Remember(this, model.Shell, NewClipViewModel.DialogKey);
            model.End();
        };
        Loaded += (_, _) =>
        {
            TargetFilter.Focus();
            if (TargetList.SelectedItem is { } target) TargetList.ScrollIntoView(target);
        };
    }

    /// <summary>The view-model behind the window.</summary>
    public NewClipViewModel Model => _model;

    /// <summary>Shows the dialog modally. Returns true when a clip was created.</summary>
    public static bool ShowModal(Window? owner, NewClipViewModel model)
    {
        var dialog = new NewClipWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
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

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller); <paramref name="scrollToEnd"/> shows the bottom of the options.</summary>
    internal static NewClipWindow CreateForCapture(NewClipViewModel model, bool scrollToEnd = false)
    {
        var window = new NewClipWindow(model);
        if (scrollToEnd)
        {
            // After the mesh and reference clip have loaded and the pick lists have brought their
            // selections into view (which scrolls the options too).
            window.Loaded += async (_, _) =>
            {
                using var busy = BusyTracker.Begin("new clip capture scroll");
                await model.SettleAsync();
                await Task.Delay(300);
                window.OptionsScroller.ScrollToEnd();
            };
        }
        return window;
    }

    /// <summary>Keeps a pick list's selected entry (a default chosen for the user) in view.</summary>
    private void OnPickSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_modal) DialogResult = true;
        else Close();
    }
}
