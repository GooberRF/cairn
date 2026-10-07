using System;
using System.Windows;
using Cairn.Atx.Ui.ViewModels;

namespace Cairn.Atx.Ui.Views.Dialogs;

/// <summary>
/// Frames &gt; Add Sequence…. Two ways in — detect a run on disk from one of its files, or generate
/// names from a pattern for files that do not exist yet — and one way out: the names, in order,
/// handed to <see cref="DocumentViewModel.AddImageFiles"/>, which is the same path Add Frames…
/// and drag-and-drop use, so the copy-or-reference prompt and the single undo step come for free.
/// </summary>
public partial class AddSequenceDialog : Window
{
    private readonly DocumentViewModel _document;
    private readonly AddSequenceViewModel _model;

    private AddSequenceDialog(DocumentViewModel document)
    {
        _document = document;
        _model = new AddSequenceViewModel(document);
        InitializeComponent();
        DataContext = _model;
        Closed += (_, _) => _model.Dispose();
        // Nothing can happen until a sequence is chosen, so that is where focus belongs.
        Loaded += (_, _) => BrowseButton.Focus();
    }

    /// <summary>Shows the dialog and applies the result. Returns true when frames were added.</summary>
    /// <param name="owner">Window to centre on.</param>
    /// <param name="document">The document the frames go into.</param>
    /// <summary>The dialog, not shown, for screenshot runs.</summary>
    internal static Window CreateForCapture(Window owner, DocumentViewModel document) =>
        new AddSequenceDialog(document) { Owner = owner };

    public static bool Show(Window? owner, DocumentViewModel document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var dialog = new AddSequenceDialog(document)
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
        };
        if (dialog.Owner is null) dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        using (Cairn.Ui.Services.ModalScope.Enter()) return dialog.ShowDialog() == true;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        string? chosen = _document.Shell.Dialogs.OpenImageFile(
            _model.SourceFolder ?? _document.AtxFolder, AddSequenceViewModel.BrowseTitle);
        if (chosen is null) return;
        _model.LoadFrom(chosen);
        OkButton.Focus();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!_model.CanApply) return;
        _model.Apply();
        DialogResult = true;
    }
}
