using Cairn.Ui.Modules;
using Cairn.Vpp.Facts;
using Cairn.Vpp.Ui.Details;
using Cairn.Vpp.Ui.Documents;
using Cairn.Vpp.Ui.Preview;
using Cairn.Vpp.Ui.Work;

namespace Cairn.Vpp.Ui;

// Services other modules find through IShellContext.Modules: the details pane's facts for any file, the packfile
// a work copy came from, and (for the shell's Recent list) which entry a work copy is and how to open it again.
public sealed partial class VppModule : IAssetFactsProvider, IAssetSiblingsProvider, IWorkCopyProvider, IOpenArchiveSources
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
    public bool CanOpenArchive(string archivePath) =>
        Kind.Extensions.Concat(Rf2Kind.Extensions).Concat(CacheKind.Extensions).Concat(CflKind.Extensions).Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase);

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

    // ---- open packfiles as session sources (IOpenArchiveSources) ----------------------------------------------

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<VppDocument, OpenArchiveSource> _sourceOf = new();
    private IReadOnlyList<OpenArchiveSource> _raisedSources = [];
    private bool _sourcesQueued;

    /// <inheritdoc/>
    public IReadOnlyList<OpenArchiveSource> OpenSources => Shell is null ? [] : BuildSources();

    /// <inheritdoc/>
    public event EventHandler? OpenSourcesChanged;

    /// <inheritdoc/>
    public string? SourceKeyOf(string path)
    {
        if (string.IsNullOrEmpty(path) || Shell is null) return null;
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        foreach (var doc in Shell.Documents.OfType<VppDocument>())
        {
            if (!doc.HasWorkFolder) continue;
            string root = Path.GetFullPath(doc.Work.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return doc.Id;
        }
        return null;
    }

    /// <summary>Follows the tabs (called once from <see cref="Initialize"/>).</summary>
    private void InitializeSources() => Shell.DocumentsChanged += (_, _) => QueueSourcesChanged();

    /// <summary>A packfile tab's snapshot changed (an edit, undo, redo, a reload): its source has new entries.</summary>
    internal void OnPackageChanged() => QueueSourcesChanged();

    /// <summary>The packfile tabs as sources; one source object per snapshot, so an unchanged tab keeps its version.</summary>
    private List<OpenArchiveSource> BuildSources()
    {
        var list = new List<OpenArchiveSource>();
        foreach (var doc in Shell.Documents.OfType<VppDocument>())
        {
            var package = doc.Current;
            // a cache pack holds the game's own data (map thumbnails), never assets another file refers to
            if (package.Cache is not null) continue;
            if (!_sourceOf.TryGetValue(doc, out var source) || !ReferenceEquals(source.Version, package) || source.Label != doc.DisplayName || source.ArchivePath != doc.FilePath)
            {
                var owner = doc;
                source = new OpenArchiveSource(doc.Id, doc.DisplayName, doc.FilePath, new VppSiblings(package), package)
                {
                    OpenEntry = name => OpenEntryOf(owner, name),
                    IsRedFactionII = package.Rf2 is not null,
                };
                _sourceOf.AddOrUpdate(doc, source);
            }
            list.Add(source);
        }
        return list;
    }

    /// <summary>Raises <see cref="OpenSourcesChanged"/> once after a burst of changes, and only when a source really changed.</summary>
    private void QueueSourcesChanged()
    {
        if (_sourcesQueued || Shell is null) return;
        _sourcesQueued = true;
        Shell.Dispatcher.BeginInvoke(new Action(() =>
        {
            _sourcesQueued = false;
            var now = BuildSources();
            if (now.Count == _raisedSources.Count && now.Zip(_raisedSources).All(p => ReferenceEquals(p.First, p.Second))) return;
            _raisedSources = now;
            OpenSourcesChanged?.Invoke(this, EventArgs.Empty);
        }));
    }

    /// <summary>"Open in Cairn" for an entry of an open packfile tab, without bringing the packfile forward.</summary>
    private bool OpenEntryOf(VppDocument doc, string entryName)
    {
        if (!Shell.Documents.Contains(doc))
        {
            Shell.ShowStatus($"{doc.DisplayName} is no longer open.");
            return false;
        }
        if (doc.Current.Find(entryName) is not { } item)
        {
            Shell.Dialogs.ShowError("Entry not found", $"{doc.DisplayName} no longer holds {entryName}.");
            return false;
        }
        if (!CairnOpens(item.Name))
        {
            Shell.Dialogs.ShowError($"Could not open {item.Name}", $"No module in this Cairn opens {Path.GetExtension(item.Name)} files.");
            return false;
        }
        doc.Commands.Fire(() => doc.Commands.OpenInCairnAsync(item));
        return true;
    }
}
