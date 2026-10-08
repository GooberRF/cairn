using System.Windows;
using Cairn.Rfa.Ui.Views;

namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The shell's side of a clip or mesh document: the <see cref="IDocument"/> members the existing structure
/// does not already provide under the same name.
/// </summary>
public abstract partial class DocumentViewModel
{
    private DocumentView? _view;
    private bool _errorSaveConfirmed;

    /// <summary>The shell's document kind (<c>rfa.clip</c> or <c>rfa.mesh</c>).</summary>
    IDocumentKind IDocument.Kind => Kind == DocumentKind.Clip ? RfaModule.ClipKind
        : this is MeshDocumentViewModel { IsLegacy: true } m ? (m.Kind == DocumentKind.CharacterMesh ? RfaModule.LegacyCharacterSaveKind : RfaModule.LegacyStaticSaveKind)
        : RfaModule.MeshKind;

    string? IDocument.TabToolTip => TabToolTip;

    /// <summary>Set by <see cref="RfaWorkspace.CloseDiscarding"/>: the shell then closes the tab without asking.</summary>
    internal bool DiscardOnClose { get; set; }

    bool IDocument.IsDirty => IsDirty && !DiscardOnClose;

    /// <summary>Flushes a half-typed field or a drag before the shell reads the document.</summary>
    public void CommitPendingEdits() => Shell.CommitPendingEdits();

    /// <summary>Asks once per document before a file with errors is written (as RFA Workbench did).</summary>
    public bool ConfirmSave()
    {
        if (ErrorCount == 0 || _errorSaveConfirmed) return true;
        if (!Shell.Dialogs.ConfirmSaveWithErrors(DisplayName, ErrorCount)) return false;
        _errorSaveConfirmed = true;
        return true;
    }

    /// <summary>False when <see cref="RefuseSave"/> refuses every path (the shell then disables Save and Save As).</summary>
    bool IDocument.CanSave => RefuseSave(null) is null;

    /// <summary>
    /// Why the document cannot be written to <paramref name="path"/> (null: any path), or null when it can. A legacy
    /// mesh tab that could not be read has nothing to write; one that was read is never written over its own source.
    /// </summary>
    protected virtual string? RefuseSave(string? path) => null;

    /// <summary>Writes the document atomically to <paramref name="path"/> and makes that its saved state.</summary>
    public void SaveTo(string path)
    {
        if (RefuseSave(path) is { } refusal) throw new InvalidOperationException(refusal);
        byte[] bytes = Serialize();
        SuspendFileWatch(true);
        try
        {
            AtomicFile.WriteAllBytes(path, bytes);
        }
        finally
        {
            SuspendFileWatch(false);
        }
        MarkSaved(path);
        Shell.Record(this);
        Shell.RefreshCommands();
    }

    /// <summary>The current content for the recovery store; null while it cannot be written.</summary>
    public byte[]? CaptureRecovery()
    {
        try { return CaptureSerializer()(); }
        catch (ArgumentException) { return null; }
    }

    /// <summary>1.0.1's status bar: error and warning counts (open the Problems tab), bones, duration, playhead and speed.</summary>
    public IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            var items = new List<StatusItem>
            {
                new($"⊗ {ErrorCount}", "Errors: click to show the Problems tab (again to hide the panel)", Shell.ShowProblemsCommand),
                new($"⚠ {WarningCount}", "Warnings: click to show the Problems tab (again to hide the panel)", Shell.ShowProblemsCommand),
            };
            if (!string.IsNullOrEmpty(StatusBonesText)) items.Add(new StatusItem(StatusBonesText));
            if (!string.IsNullOrEmpty(StatusDurationText)) items.Add(new StatusItem(StatusDurationText, "Clip duration"));
            if (Playback.HasClip)
            {
                items.Add(new StatusItem(Playback.TimeText, "The playhead"));
                items.Add(new StatusItem(Playback.SpeedText, "Playback speed"));
            }
            return items;
        }
    }

    /// <summary>Re-reads <see cref="StatusItems"/> (the workspace calls it, throttled, when the playhead moves).</summary>
    public void RaiseStatusItems() => Raise(nameof(StatusItems));

    /// <summary>The document's view, created on first use.</summary>
    public FrameworkElement View => _view ??= new DocumentView { DataContext = this };

    /// <summary>Called by the shell when this tab comes to the front.</summary>
    public void OnActivated() { }

    /// <summary>Called by the shell when another tab comes to the front: playback stops.</summary>
    public void OnDeactivated() => Playback.Pause();
}
