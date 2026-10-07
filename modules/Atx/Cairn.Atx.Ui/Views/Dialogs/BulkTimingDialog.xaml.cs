using System;
using System.Windows;
using System.Windows.Controls;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views.Dialogs;

/// <summary>
/// Frames &gt; Bulk Frame Timing… (Ctrl+T). The table is the plan, recomputed as the user changes
/// anything; Apply turns that same plan into one edit batch, which is one undo step.
/// </summary>
public partial class BulkTimingDialog : Window
{
    private readonly BulkTimingViewModel _model;
    private bool _syncing;

    private BulkTimingDialog(DocumentViewModel document)
    {
        _model = new BulkTimingViewModel(document);
        InitializeComponent();
        DataContext = _model;

        _syncing = true;
        foreach (var (_, label, _) in BulkTimingViewModel.Operations) OperationBox.Items.Add(label);
        OperationBox.SelectedIndex = 0;
        _syncing = false;

        // The operation is the decision the dialog is really asking for.
        Loaded += (_, _) => OperationBox.Focus();
    }

    /// <summary>Shows the dialog. Returns true when a change was applied.</summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="document">The document to retime.</param>
    /// <summary>The dialog, not shown, for screenshot runs.</summary>
    internal static Window CreateForCapture(Window owner, DocumentViewModel document) =>
        new BulkTimingDialog(document) { Owner = owner };

    public static bool Show(Window? owner, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var dialog = new BulkTimingDialog(document)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Cairn.Ui.Services.ModalScope.Enter()) return dialog.ShowDialog() == true;
    }

    private void OnOperationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        int index = OperationBox.SelectedIndex;
        if (index >= 0 && index < BulkTimingViewModel.Operations.Count)
        {
            _model.Operation = BulkTimingViewModel.Operations[index].Operation;
        }
    }

    /// <summary>
    /// Applies the plan and closes — but only when something was written. Closing with
    /// <c>DialogResult = true</c> after a refused apply tells the caller a change landed that never
    /// did, and leaves the user with no sight of the message saying why.
    /// </summary>
    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!_model.CanApply) return;
        if (!_model.Apply()) return;
        DialogResult = true;
    }
}
