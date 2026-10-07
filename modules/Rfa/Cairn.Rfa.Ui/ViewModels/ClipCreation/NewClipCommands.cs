using System.ComponentModel;
using Cairn.Ui.Mvvm;
using Cairn.Rfa.Ui.Views.Dialogs;
using Cairn.Assets;

namespace Cairn.Rfa.Ui.ViewModels.ClipCreation;

/// <summary>
/// File › New Clip… (Ctrl+N, the toolbar and the welcome page), Mesh › New Clip for This Mesh… and the
/// library's "New Clip for This Mesh…" (exposed on <see cref="RfaWorkspace.NewClips"/>).
/// </summary>
public sealed class NewClipCommands
{
    private readonly RfaWorkspace _shell;

    public NewClipCommands(RfaWorkspace shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        NewClipCommand = new RelayCommand(() => Show(null));
        ForActiveMeshCommand = new RelayCommand(() => Show(null), () => _shell.ActiveDocument is MeshDocumentViewModel { HasSkeleton: true });
        _shell.PropertyChanged += OnShellPropertyChanged;
    }

    /// <summary>File › New Clip…: always available (the dialog offers open tabs, the library and Browse…).</summary>
    public RelayCommand NewClipCommand { get; }

    /// <summary>Mesh › New Clip for This Mesh…: a mesh tab with bones in front.</summary>
    public RelayCommand ForActiveMeshCommand { get; }

    /// <summary>True for a library entry a new clip can be made for (a readable character mesh).</summary>
    public static bool CanCreateFor(object? item) => item is LibraryMesh { HasSkeleton: true, IsReadable: true };

    /// <summary>The library's "New Clip for This Mesh…".</summary>
    public void ShowForLibraryMesh(LibraryMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (CanCreateFor(mesh)) Show(mesh);
    }

    /// <summary>Re-queries the commands' enabled state.</summary>
    public void Refresh() => ForActiveMeshCommand.RaiseCanExecuteChanged();

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RfaWorkspace.ActiveDocument) or nameof(RfaWorkspace.HasDocument)) Refresh();
    }

    private void Show(LibraryMesh? preferred)
    {
        _shell.CommitPendingEdits();
        NewClipWindow.ShowModal(_shell.Dialogs.Owner, new NewClipViewModel(_shell, preferred));
    }
}
