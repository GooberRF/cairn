using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.Retargeting;

namespace Cairn.Rfa.Ui.Views.Retarget;

/// <summary>
/// Tools › Batch Retarget…: settings in tabs on the left (queue and output, rigs, bone map, options),
/// progress and results on the right. Resizable; its size is remembered. Closing stops a running batch.
/// </summary>
public partial class BatchRetargetWindow : Window
{
    private const string SizeKey = "rfa.batchRetargetDialog";
    private readonly BatchRetargetViewModel _model;

    private BatchRetargetWindow(BatchRetargetViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        var (w, h) = model.Shell.DialogSize(SizeKey, 1320, 840);
        Width = Math.Min(w, SystemParameters.WorkArea.Width);
        Height = Math.Min(h, SystemParameters.WorkArea.Height);
        Closing += OnClosing;
        Closed += (_, _) => model.Dispose();
    }

    /// <summary>The view-model behind the window.</summary>
    public BatchRetargetViewModel Model => _model;

    /// <summary>Shows the dialog modally.</summary>
    public static void ShowModal(Window? owner, BatchRetargetViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dialog = new BatchRetargetWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using (ModalScope.Enter()) dialog.ShowDialog();
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static BatchRetargetWindow CreateForCapture(BatchRetargetViewModel model) => new(model);

    /// <summary>Selects a settings page by index (diagnostics).</summary>
    internal void ShowPage(int index) => Pages.SelectedIndex = Math.Clamp(index, 0, Pages.Items.Count - 1);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_model.IsRunning)
        {
            // Stop first; the window closes once the batch has wound down (between two clips).
            _model.StopCommand.Execute(null);
            e.Cancel = true;
            _model.PropertyChanged += CloseWhenStopped;
            return;
        }
        if (WindowState == WindowState.Normal && !_model.Shell.IsDiagnosticRun) _model.Shell.SetDialogSize(SizeKey, ActualWidth, ActualHeight);
    }

    private void CloseWhenStopped(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BatchRetargetViewModel.IsRunning) || _model.IsRunning) return;
        _model.PropertyChanged -= CloseWhenStopped;
        Dispatcher.BeginInvoke(new Action(Close));
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnQueueSelectionChanged(object sender, SelectionChangedEventArgs e) => _model.OnSelectionChanged();

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultList.SelectedItem is BatchResultRow row && e.OriginalSource is DependencyObject) _model.OpenResult(row);
    }

    private void OnResultKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ResultList.SelectedItem is BatchResultRow row)
        {
            _model.OpenResult(row);
            e.Handled = true;
        }
    }
}
