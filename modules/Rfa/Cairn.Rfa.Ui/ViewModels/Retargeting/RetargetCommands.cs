using System.ComponentModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Views.Retarget;
using Cairn.Assets;

namespace Cairn.Rfa.Ui.ViewModels.Retargeting;

/// <summary>Clip › Retarget… (Ctrl+R), Tools › Batch Retarget…, and the library's Retarget… (exposed on <see cref="RfaWorkspace.Retarget"/>).</summary>
public sealed class RetargetCommands
{
    private readonly RfaWorkspace _shell;

    public RetargetCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        RetargetCommand = new RelayCommand(OpenForActive, () => _shell.ActiveDocument is ClipDocumentViewModel);
        BatchCommand = new RelayCommand(OpenBatch);
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    /// <summary>Clip › Retarget… for the active clip document.</summary>
    public RelayCommand RetargetCommand { get; }

    /// <summary>Tools › Batch Retarget….</summary>
    public RelayCommand BatchCommand { get; }

    /// <summary>Opens the retarget dialog on a library clip (the library's context menu).</summary>
    public void RetargetLibraryClip(LibraryClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _shell.CommitPendingEdits();
        RetargetWindow.ShowModal(_shell.Dialogs.Owner, new RetargetDialogViewModel(_shell, null, clip));
    }

    private void OpenForActive()
    {
        if (_shell.ActiveDocument is not ClipDocumentViewModel document) return;
        _shell.CommitPendingEdits();
        document.Playback.Pause();
        RetargetWindow.ShowModal(_shell.Dialogs.Owner, new RetargetDialogViewModel(_shell, document));
    }

    private void OpenBatch()
    {
        _shell.CommitPendingEdits();
        BatchRetargetWindow.ShowModal(_shell.Dialogs.Owner, new BatchRetargetViewModel(_shell));
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.IsClipDocument)) RetargetCommand.RaiseCanExecuteChanged();
    }
}
