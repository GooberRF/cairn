using System;
using System.Windows;
using Cairn.Atx.Ui.ViewModels;
using Cairn.Formats.Imaging;

namespace Cairn.Atx.Ui.Views.Dialogs;

/// <summary>
/// File &gt; Import VBM…: the dialog that turns a legacy <c>.vbm</c> into frames plus an
/// <c>.atx</c>.
///
/// The view-model owns the plan, the preview and the writing; this file only wires the window to
/// it — remembered size, first focus on the ATX name box (the one field whose value decides whether
/// the result replaces the original texture), and closing when the import has succeeded.
/// </summary>
public partial class VbmImportDialog : Window
{
    private const string LayoutWidth = "vbmImportWidth";
    private const string LayoutHeight = "vbmImportHeight";

    private readonly VbmImportViewModel _model;

    private VbmImportDialog(
        AtxWorkspace shell, VbmImportSource source, byte[] bytes, VbmInfo info)
    {
        _model = new VbmImportViewModel(shell, source, bytes, info);
        InitializeComponent();
        DataContext = _model;

        RestoreSize(shell);
        _model.RequestClose += OnRequestClose;
        Closed += (_, _) =>
        {
            _model.RequestClose -= OnRequestClose;
            RememberSize(shell);
            _model.Dispose();
        };
        // The .atx name is the decision that matters most, so that is where the caret starts.
        Loaded += (_, _) => { AtxNameBox.Focus(); AtxNameBox.SelectAll(); };
    }

    /// <summary>
    /// Shows the dialog. Returns the generated .atx's path, or null when nothing was written.
    /// </summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="shell">The shell, for settings and prompts.</param>
    /// <param name="source">Where the .vbm came from.</param>
    /// <param name="bytes">Its bytes, already read on a worker thread.</param>
    /// <param name="info">Its header.</param>
    internal static Window CreateForCapture(
        Window owner, AtxWorkspace shell, VbmImportSource source, byte[] bytes, VbmInfo info) =>
        new VbmImportDialog(shell, source, bytes, info) { Owner = owner };

    public static string? Show(
        Window? owner, AtxWorkspace shell, VbmImportSource source, byte[] bytes, VbmInfo info)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var dialog = new VbmImportDialog(shell, source, bytes, info)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Cairn.Ui.Services.ModalScope.Enter())
        {
            return dialog.ShowDialog() == true ? dialog._model.ImportedPath : null;
        }
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        // The preview is still playing when the import finishes; stop it before the window goes.
        _model.Pause();
        DialogResult = true;
    }

    private void RestoreSize(AtxWorkspace shell)
    {
        double width = shell.LayoutSize(LayoutWidth, Width);
        double height = shell.LayoutSize(LayoutHeight, Height);
        if (width >= MinWidth) Width = Math.Min(width, SystemParameters.VirtualScreenWidth);
        if (height >= MinHeight) Height = Math.Min(height, SystemParameters.VirtualScreenHeight);
    }

    private void RememberSize(AtxWorkspace shell)
    {
        if (WindowState != WindowState.Normal) return;
        shell.SetLayoutSize(LayoutWidth, ActualWidth);
        shell.SetLayoutSize(LayoutHeight, ActualHeight);
        shell.SaveSettings();
    }
}
