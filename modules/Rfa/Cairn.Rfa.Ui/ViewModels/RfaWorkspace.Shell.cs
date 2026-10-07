namespace Cairn.Rfa.Ui.ViewModels;

/// <summary>
/// The workspace inside the Cairn shell: the shell owns the tabs, saving and recovery; this keeps
/// <see cref="Documents"/> and <see cref="ActiveDocument"/> following the shell's RFA documents.
/// </summary>
public sealed partial class RfaWorkspace
{
    /// <summary>The shell hosting the workspace, or null when it runs on its own (self-tests of the old app).</summary>
    public IShellContext? Host { get; private set; }

    /// <summary>Hands tabs, saving and the active document to <paramref name="shell"/>.</summary>
    public void AttachShell(IShellContext shell)
    {
        Host = shell ?? throw new ArgumentNullException(nameof(shell));
        shell.DocumentsChanged += (_, _) => SyncDocuments();
        shell.ActiveDocumentChanged += (_, _) =>
        {
            SyncDocuments();
            ActiveDocument = shell.ActiveDocument as DocumentViewModel;
        };
    }

    /// <summary>
    /// Brings the shell's panel tab for an old bottom/left tab id ("timeline" -> "rfa.timeline") to the
    /// front, as 1.0.1 did through <see cref="SelectedBottomTab"/> (New Clip, the self-tests).
    /// </summary>
    internal void SelectShellPanel(string id)
    {
        if (Host is null || System.Windows.Application.Current?.MainWindow is not { } window) return;
        foreach (var name in new[] { "BottomTabs", "LeftTabs" })
        {
            if (window.FindName(name) is not System.Windows.Controls.TabControl tabs) continue;
            foreach (var item in tabs.Items.OfType<System.Windows.Controls.TabItem>())
                if (item.Tag is string tag && (tag == "rfa." + id || tag == id)) { tabs.SelectedItem = item; return; }
        }
    }

    /// <summary>Follows the shell's tab list: new RFA documents are tracked, closed ones dropped.</summary>
    private void SyncDocuments()
    {
        if (Host is null) return;
        var open = Host.Documents.OfType<DocumentViewModel>().ToList();
        foreach (var gone in Documents.Where(d => !open.Contains(d)).ToList())
        {
            Documents.Remove(gone);
            _states.TryRemove(gone.Id, out _);
        }
        foreach (var added in open.Where(d => !Documents.Contains(d))) Track(added);
        if (_active is not null && !open.Contains(_active)) ActiveDocument = null;
        Raise(nameof(HasDocument));
    }

    /// <summary>Parses a clip (.rfa) or mesh (.v3c/.v3m) for the shell's document kinds.</summary>
    internal DocumentViewModel CreateForShell(byte[] bytes, string name, string? path) => CreateDocument(bytes, name, path, null);

    /// <summary>
    /// An archive entry opened by the shell ("&lt;entry&gt; in &lt;archive&gt;.vpp"): a read-only-origin document as 1.0.1
    /// opened from the library (origin shown, Save asks where to write a copy). The archive's full path is looked up in
    /// the library's sources; when it is not there the archive name alone describes the origin.
    /// </summary>
    internal DocumentViewModel CreateArchivedForShell(byte[] bytes, string name, string originText) =>
        CreateDocument(bytes, name, null, ArchiveLocationFor(name, originText));

    private AssetLocation ArchiveLocationFor(string name, string originText)
    {
        int split = originText.LastIndexOf(" in ", StringComparison.Ordinal);
        string entry = split > 0 ? originText[..split] : name;
        string archive = split > 0 ? originText[(split + 4)..] : originText;
        try
        {
            if (Assets.Resolver.Resolve(entry) is { ArchivePath: { } full } found
                && string.Equals(Path.GetFileName(full), Path.GetFileName(archive), StringComparison.OrdinalIgnoreCase))
                return found;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // The library cannot be searched now: describe the origin by name.
        }
        return new AssetLocation(entry, entry, AssetSourceKind.GameArchive, null, archive, null);
    }

    /// <summary>A recovered document: the disk copy with the recovered work as one unsaved step on top.</summary>
    internal DocumentViewModel RestoreForShell(RecoverySnapshot snapshot) =>
        CreateRestoredDocument(snapshot.Data, snapshot.DisplayName, snapshot.OriginalPath, null, wasDirty: true);
}
