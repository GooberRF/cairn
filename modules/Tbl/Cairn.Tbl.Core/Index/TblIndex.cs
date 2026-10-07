using Cairn.Assets;
using Cairn.Tbl.Model;
using Cairn.Tbl.Schema;
using Cairn.Tbl.Text;

namespace Cairn.Tbl.Index;

/// <summary>Where an indexed table came from; earlier kinds win when several define the same name.</summary>
public enum TblSourceKind
{
    /// <summary>A table open in the editor (its current, possibly unsaved, text).</summary>
    OpenDocument,
    /// <summary>A loose table in a search folder or next to the document.</summary>
    SearchFolder,
    /// <summary>A table inside a packfile in a search folder.</summary>
    SearchFolderArchive,
    /// <summary>A table in the game's packfiles or folders.</summary>
    GameData,
}

/// <summary>An indexed table.</summary>
/// <param name="Key">Identity: the full path of a loose file, <c>archive.vpp|entry.tbl</c> for an archive entry, or the caller's key.</param>
/// <param name="FileName">The table's file name (<c>weapons.tbl</c>).</param>
/// <param name="DisplayLocation">Where it is, for the UI (folder or packfile name).</param>
/// <param name="Kind">Which kind of location.</param>
/// <param name="Location">The resolver's location, for opening it read-only; null for open documents.</param>
public sealed record TblSource(string Key, string FileName, string DisplayLocation, TblSourceKind Kind, AssetLocation? Location = null)
{
    /// <summary>The source for a loose file or open document at <paramref name="path"/>.</summary>
    public static TblSource ForPath(string path, TblSourceKind kind = TblSourceKind.OpenDocument) =>
        new(path, Path.GetFileName(path), Path.GetDirectoryName(path) ?? string.Empty, kind);

    /// <summary>The source for a resolver location.</summary>
    public static TblSource ForLocation(AssetLocation location)
    {
        var kind = location.Kind switch
        {
            AssetSourceKind.SearchFolder or AssetSourceKind.DocumentFolder => TblSourceKind.SearchFolder,
            AssetSourceKind.SearchFolderArchive => TblSourceKind.SearchFolderArchive,
            _ => TblSourceKind.GameData,
        };
        string key = location.FilePath ?? (location.ArchivePath + "|" + location.ResolvedName);
        return new TblSource(key, location.ResolvedName, location.DisplayLocation, kind, location);
    }
}

/// <summary>An entry name defined by a table (a weapon, an ammo type...).</summary>
/// <param name="Kind">The kind the section defines (<c>weapon</c>).</param>
/// <param name="Name">The name as written.</param>
/// <param name="Source">The defining table.</param>
/// <param name="Span">The name's characters in that table.</param>
/// <param name="EntrySpan">The whole entry.</param>
/// <param name="Line">1-based line of the name.</param>
/// <param name="Section">The section header without '#', or empty.</param>
public sealed record TblDefinition(string Kind, string Name, TblSource Source, TextSpan Span, TextSpan EntrySpan, int Line, string Section);

/// <summary>A use of an entry name or file name in a table.</summary>
/// <param name="Role">File or entry reference.</param>
/// <param name="Kind">The reference kind (<c>weapon</c>) or file kind (<c>texture</c>).</param>
/// <param name="Name">The name as written.</param>
/// <param name="Source">The table it is used in.</param>
/// <param name="Span">The name's characters.</param>
/// <param name="Line">1-based line.</param>
/// <param name="Field">The field marker it is a value of.</param>
public sealed record TblReference(TblValueRole Role, string Kind, string Name, TblSource Source, TextSpan Span, int Line, string Field);

/// <summary>
/// The cross-table index: entry names defined by every known table and every use of an entry name or
/// file name, kept per source so one table can be replaced at a time. Thread-safe: updates and queries
/// may come from any thread.
/// </summary>
public sealed class TblIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (TblSource Source, List<TblDefinition> Defs, List<TblReference> Refs)> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, List<TblDefinition>>> _defs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<TblReference>> _refsByName = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string> _assetNames = [];

    public TblIndex(TblSchemaSet? schemas = null) => Schemas = schemas ?? TblSchemaSet.Default;

    public TblSchemaSet Schemas { get; }

    /// <summary>Bumped on every change.</summary>
    public int Version { get; private set; }

    /// <summary>Raised (on the updating thread) after a change.</summary>
    public event EventHandler? Changed;

    /// <summary>The indexed sources.</summary>
    public IReadOnlyList<TblSource> Sources
    {
        get { lock (_gate) return [.. _sources.Values.Select(s => s.Source)]; }
    }

    /// <summary>Adds or replaces one table (parsed with the schema for its file name).</summary>
    public void Update(TblSource source, string text)
    {
        ArgumentNullException.ThrowIfNull(source);
        var schema = Schemas.Find(source.FileName);
        Update(source, TblDocument.Parse(text ?? string.Empty, schema));
    }

    /// <summary>Adds or replaces one table from an existing parse.</summary>
    public void Update(TblSource source, TblDocument doc)
    {
        var (defs, refs) = Extract(source, doc);
        lock (_gate)
        {
            RemoveLocked(source.Key);
            _sources[source.Key] = (source, defs, refs);
            foreach (var d in defs)
            {
                if (!_defs.TryGetValue(d.Kind, out var byName)) _defs[d.Kind] = byName = new(StringComparer.OrdinalIgnoreCase);
                if (!byName.TryGetValue(d.Name.Trim(), out var list)) byName[d.Name.Trim()] = list = [];
                list.Add(d);
            }
            foreach (var r in refs)
            {
                string key = RefKey(r.Role, r.Kind, r.Name);
                if (!_refsByName.TryGetValue(key, out var list)) _refsByName[key] = list = [];
                list.Add(r);
            }
            Version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes one table.</summary>
    public void Remove(string key)
    {
        lock (_gate)
        {
            if (!RemoveLocked(key)) return;
            Version++;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the asset names offered for file-name completion (usually from the resolver's enumeration).</summary>
    public void SetAssetNames(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        lock (_gate) { _assetNames = list; Version++; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Where <paramref name="name"/> of <paramref name="kind"/> is defined, open documents first, then search folders, then game data.</summary>
    public ImmutableArray<TblDefinition> Define(string kind, string name)
    {
        lock (_gate)
        {
            if (!_defs.TryGetValue(kind, out var byName) || !byName.TryGetValue(name.Trim(), out var list)) return [];
            // An open table shadows the indexed file of the same name (the game uses one copy; the open one is the
            // one being worked on), so a name it no longer defines is not "defined" by the other copy.
            var open = _sources.Values.Where(s => s.Source.Kind == TblSourceKind.OpenDocument).Select(s => s.Source.FileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return [.. list.Where(d => d.Source.Kind == TblSourceKind.OpenDocument || !open.Contains(d.Source.FileName)).OrderBy(d => d.Source.Kind).ThenBy(d => d.Source.Key, StringComparer.OrdinalIgnoreCase).ThenBy(d => d.Span.Start)];
        }
    }

    /// <summary>True when at least one table defining <paramref name="kind"/> is indexed (so "not defined" can be trusted).</summary>
    public bool HasKind(string kind)
    {
        lock (_gate) return _defs.TryGetValue(kind, out var byName) && byName.Count > 0;
    }

    /// <summary>Every use of entry <paramref name="name"/> of <paramref name="kind"/>.</summary>
    public ImmutableArray<TblReference> Usages(string kind, string name) => Uses(TblValueRole.Ref, kind, name);

    /// <summary>Every use of file <paramref name="fileName"/> (any file kind; <c>.v3d</c> and <c>.v3m</c> spellings kept apart).</summary>
    public ImmutableArray<TblReference> FileUsages(string fileName)
    {
        lock (_gate)
        {
            return [.. _refsByName.Where(p => p.Key.StartsWith("file|", StringComparison.Ordinal) && p.Key.EndsWith("|" + fileName.Trim(), StringComparison.OrdinalIgnoreCase))
                .SelectMany(p => p.Value).OrderBy(r => r.Source.Kind).ThenBy(r => r.Source.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Span.Start)];
        }
    }

    private ImmutableArray<TblReference> Uses(TblValueRole role, string kind, string name)
    {
        lock (_gate)
        {
            return _refsByName.TryGetValue(RefKey(role, kind, name), out var list)
                ? [.. list.OrderBy(r => r.Source.Kind).ThenBy(r => r.Source.Key, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Span.Start)]
                : [];
        }
    }

    /// <summary>The names of <paramref name="kind"/> (for completion), sorted, each once.</summary>
    public IReadOnlyList<string> Names(string kind)
    {
        lock (_gate)
        {
            if (!_defs.TryGetValue(kind, out var byName)) return [];
            return [.. byName.Values.Select(l => l[0].Name.Trim()).Order(StringComparer.OrdinalIgnoreCase)];
        }
    }

    /// <summary>The asset names of a file kind (texture, mesh, ... or "any") for completion.</summary>
    public IReadOnlyList<string> FileNames(string? fileKind)
    {
        IReadOnlyList<string> names;
        lock (_gate) names = _assetNames;
        var exts = TblValueRoles.ExtensionsOf(fileKind);
        // Tables name animations as .mvf and meshes as .v3d: offer those spellings too.
        var result = new List<string>();
        foreach (string n in names)
        {
            string ext = Path.GetExtension(n);
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) result.Add(n);
            else if (ext.Equals(".rfa", StringComparison.OrdinalIgnoreCase) && exts.Contains(".mvf", StringComparer.OrdinalIgnoreCase)) result.Add(Path.ChangeExtension(n, ".mvf"));
        }
        return result;
    }

    /// <summary>Every definition and reference a parsed table contributes.</summary>
    public static (List<TblDefinition> Defs, List<TblReference> Refs) Extract(TblSource source, TblDocument doc)
    {
        var defs = new List<TblDefinition>();
        var refs = new List<TblReference>();
        foreach (var section in doc.Sections)
        {
            string? kind = section.Schema?.Defines;
            if (kind is null) continue;
            foreach (var e in section.Entries)
            {
                if (e.Name.Trim().Length == 0) continue;
                defs.Add(new TblDefinition(kind, e.Name, source, e.NameSpan, e.Span, doc.Lines.LineOf(e.NameSpan.Start), section.Name));
            }
        }
        foreach (var r in TblValueRoles.All(doc))
            refs.Add(new TblReference(r.Role, r.Kind, r.Name, source, r.Span, doc.Lines.LineOf(r.Span.Start), r.Field.Marker));
        return (defs, refs);
    }

    private static string RefKey(TblValueRole role, string kind, string name) =>
        role == TblValueRole.File ? "file|" + name.Trim() : "ref|" + kind + "|" + name.Trim();

    private bool RemoveLocked(string key)
    {
        if (!_sources.Remove(key, out var old)) return false;
        foreach (var d in old.Defs)
        {
            if (_defs.TryGetValue(d.Kind, out var byName) && byName.TryGetValue(d.Name.Trim(), out var list))
            {
                list.Remove(d);
                if (list.Count == 0) byName.Remove(d.Name.Trim());
            }
        }
        foreach (var r in old.Refs)
        {
            string k = RefKey(r.Role, r.Kind, r.Name);
            if (_refsByName.TryGetValue(k, out var list))
            {
                list.Remove(r);
                if (list.Count == 0) _refsByName.Remove(k);
            }
        }
        return true;
    }

    /// <summary>
    /// Indexes every table the resolver can see (game packfiles and folders, search folders) plus the
    /// caller's open documents, whose text replaces any file at the same path; also loads the asset names
    /// for file-name completion. Unreadable tables are skipped. Run off the UI thread.
    /// </summary>
    /// <param name="resolver">The shared resolver.</param>
    /// <param name="openDocuments">Open table documents: full path (or caller key) to current text.</param>
    /// <param name="cancellationToken">Cancels the build.</param>
    /// <param name="schemas">Schemas (default: the embedded ones).</param>
    public static TblIndex Build(AssetResolver resolver, IReadOnlyDictionary<string, string>? openDocuments = null, CancellationToken cancellationToken = default, TblSchemaSet? schemas = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var index = new TblIndex(schemas);
        index.Refresh(resolver, openDocuments, cancellationToken);
        return index;
    }

    private static bool SameFile(AssetLocation a, AssetLocation b) =>
        string.Equals(a.ResolvedName, b.ResolvedName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.ArchivePath, b.ArchivePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Re-reads the resolver's tables and asset names (see <see cref="Build"/>), keeping open documents' text.</summary>
    public void Refresh(AssetResolver resolver, IReadOnlyDictionary<string, string>? openDocuments = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        var open = openDocuments ?? new Dictionary<string, string>();
        var openNames = new HashSet<string>(open.Keys, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in resolver.Enumerate([".tbl"], cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The game loads one copy of each table (no merging): the one its lookup finds first.
            if (resolver.Resolve(location.ResolvedName, cancellationToken) is { } winner && !SameFile(winner, location)) continue;
            var source = TblSource.ForLocation(location);
            seen.Add(source.Key);
            if (openNames.Contains(source.Key)) continue;
            try
            {
                byte[] bytes = location.ReadAllBytes();
                Update(source, TblTextFiles.Decode(bytes).Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Cairn.Formats.AssetFormatException)
            {
                // An unreadable table contributes nothing.
            }
        }
        foreach (var (key, text) in open)
            Update(TblSource.ForPath(key), text);
        // Tables no longer visible (a search folder removed, a file deleted or renamed) are forgotten; open documents stay.
        List<string> gone;
        lock (_gate) gone = [.. _sources.Values.Where(s => s.Source.Kind != TblSourceKind.OpenDocument && !seen.Contains(s.Source.Key)).Select(s => s.Source.Key)];
        foreach (string key in gone) Remove(key);
        try
        {
            SetAssetNames(resolver.Enumerate(TblValueRoles.AllExtensions.Concat([".rfa"]).ToArray(), cancellationToken).Select(l => l.ResolvedName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Completion simply offers no file names.
        }
    }
}
