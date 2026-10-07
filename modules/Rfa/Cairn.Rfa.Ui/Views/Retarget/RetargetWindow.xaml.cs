using System.ComponentModel;
using System.Windows;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels.Retargeting;

namespace Cairn.Rfa.Ui.Views.Retarget;

/// <summary>
/// Clip › Retarget…: settings in tabs on the left, the live preview on the right (target mesh with the
/// result and the source skeleton as a ghost, or the source mesh beside it). Resizable; its size is
/// remembered. Every way of closing it disposes the view-model (the previews stop).
/// </summary>
public partial class RetargetWindow : Window
{
    private const string SizeKey = "rfa.retargetDialog";
    private readonly RetargetDialogViewModel _model;
    private bool _modal;

    private RetargetWindow(RetargetDialogViewModel model)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        InitializeComponent();
        DataContext = model;
        var (w, h) = model.Shell.DialogSize(SizeKey, 1280, 820);
        Width = Math.Min(w, SystemParameters.WorkArea.Width);
        Height = Math.Min(h, SystemParameters.WorkArea.Height);
        model.PropertyChanged += OnModelChanged;
        Closing += (_, _) =>
        {
            if (WindowState == WindowState.Normal && !model.Shell.IsDiagnosticRun) model.Shell.SetDialogSize(SizeKey, ActualWidth, ActualHeight);
        };
        Closed += (_, _) => model.Dispose();
        Loaded += (_, _) =>
        {
            if (ClipList.SelectedItem is { } selected) ClipList.ScrollIntoView(selected);
        };
        ApplySideBySide();
    }

    private void OnClipSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ClipList.SelectedItem is { } selected) ClipList.ScrollIntoView(selected);
    }

    /// <summary>The view-model behind the window.</summary>
    public RetargetDialogViewModel Model => _model;

    /// <summary>Shows the dialog modally. Returns true when a result was opened or saved.</summary>
    public static bool ShowModal(Window? owner, RetargetDialogViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var dialog = new RetargetWindow(model) { Owner = owner is { IsLoaded: true } ? owner : null, _modal = true };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try
        {
            using (ModalScope.Enter()) return dialog.ShowDialog() == true;
        }
        finally
        {
            model.Dispose();
        }
    }

    /// <summary>A window for the diagnostic capture (shown non-modally by the caller).</summary>
    internal static RetargetWindow CreateForCapture(RetargetDialogViewModel model) => new(model);

    /// <summary>Selects a settings page by index (diagnostics: 0 setup, 1 bone map, 2 options, 3 report).</summary>
    internal void ShowPage(int index) => Pages.SelectedIndex = Math.Clamp(index, 0, Pages.Items.Count - 1);

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RetargetDialogViewModel.IsSideBySide)) ApplySideBySide();
    }

    private void ApplySideBySide()
    {
        bool on = _model.IsSideBySide;
        SourceColumn.Width = on ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SourceViewBorder.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRetarget(object sender, RoutedEventArgs e)
    {
        if (_model.Retarget() is null) return;
        Finish();
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_model.SaveAs() is null) return;
        Finish();
    }

    private void Finish()
    {
        if (_modal) DialogResult = true;
        else Close();
    }
}
