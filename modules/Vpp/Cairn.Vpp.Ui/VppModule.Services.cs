using Cairn.Ui.Modules;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Ui.Work;

namespace Cairn.Vpp.Ui;

// Services other modules find through IShellContext.Modules: the details pane's facts for any file, the packfile
// a work copy came from, and (for the shell's Recent list) which entry a work copy is and how to open it again.
public sealed partial class VppModule : IAssetFactsProvider, IAssetSiblingsProvider, IWorkCopyProvider
{
    /// <inheritdoc/>
    public bool IsWorkCopy(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        // the current location and the default one (a copy recorded before the setting changed is just as temporary)
        foreach (var root in new[] { _store is null ? null : Settings.WorkRoot, VppWorkRoot.Default })
        {
            if (root is null) continue;
            string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public (string ArchivePath, string EntryName)? ArchiveEntryOf(string path)
    {
        if (string.IsNullOrEmpty(path) || Shell is null) return null;
        foreach (var doc in Shell.Documents.OfType<VppDocument>())
        {
            if (!doc.HasWorkFolder || doc.FilePath is not { } archive) continue;
            if (doc.Work.EntryOf(path) is { } entry) return (archive, entry);
        }
        return null;
    }

    /// <inheritdoc/>
    public bool CanOpenArchive(string archivePath) => Kind.Extensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public bool OpenArchiveEntry(string archivePath, string entryName)
    {
        string full = Path.GetFullPath(archivePath);
        var doc = Shell.Documents.OfType<VppDocument>().FirstOrDefault(d => d.FilePath is { } p && string.Equals(Path.GetFullPath(p), full, StringComparison.OrdinalIgnoreCase));
        if (doc is null)
        {
            if (!Shell.OpenFile(full)) return false; // the shell said why
            doc = Shell.ActiveDocument as VppDocument;
            if (doc is null) return false;
        }
        else Shell.Activate(doc);
        if (doc.Current.Find(entryName) is not { } item)
        {
            Shell.Dialogs.ShowError("Entry not found", $"{Path.GetFileName(full)} no longer holds {entryName}. The packfile is open so you can look for it.");
            return false;
        }
        if (!CairnOpens(item.Name))
        {
            Shell.Dialogs.ShowError($"Could not open {item.Name}", $"No module in this Cairn opens {Path.GetExtension(item.Name)} files.");
            return false;
        }
        doc.SelectNames([item.Name]);
        doc.Commands.Fire(() => doc.Commands.OpenInCairnAsync(item));
        return true;
    }

    /// <inheritdoc/>
    public IReadOnlyList<(string Section, string Label, string Value, bool Flagged)> Describe(string name, Func<Stream> open, long size)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(open);
        var sheet = VppFacts.Describe(name, open, size, new VppFactsContext(null, Shell?.Assets.Resolver));
        return [.. VppDetailsPane.FactRows(name, sheet).Select(r => (r.Section, r.Label, r.Value, r.Flagged))];
    }

    /// <inheritdoc/>
    public IAssetSiblings? SiblingsFor(string path)
    {
        if (string.IsNullOrEmpty(path) || Shell is null) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        foreach (var doc in Shell.Documents.OfType<VppDocument>())
        {
            if (!doc.HasWorkFolder) continue;
            string root = Path.GetFullPath(doc.Work.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return new VppSiblings(doc.Current);
        }
        return null;
    }
}
