using Cairn.Formats.Vpp;
using Cairn.Formats.Imaging;

namespace Cairn.Assets;

/// <summary>Where a resolved asset came from, in the engine's own search order.</summary>
public enum AssetSourceKind
{
    /// <summary>Next to the active document.</summary>
    DocumentFolder,
    /// <summary>A loose file in one of the user's search folders.</summary>
    SearchFolder,
    /// <summary>Inside a .vpp in one of the user's search folders.</summary>
    SearchFolderArchive,
    /// <summary>A .vpp in the Red Faction install root.</summary>
    GameArchive,
    /// <summary>A loose file under the game's user_maps folders.</summary>
    GameFolder,
    /// <summary>A .vpp under the game's user_maps folders.</summary>
    GameFolderArchive,
}

/// <summary>Where a requested file was found, and under what name.</summary>
/// <param name="RequestedName">The name asked for.</param>
/// <param name="ResolvedName">The name actually used, which for a texture may be a superseding sibling.</param>
/// <param name="Kind">Which search location provided it.</param>
/// <param name="FilePath">Full path, for a loose file.</param>
/// <param name="ArchivePath">Full path of the .vpp, for an archived file.</param>
/// <param name="Entry">The archive entry, for an archived file.</param>
public sealed record AssetLocation(
    string RequestedName,
    string ResolvedName,
    AssetSourceKind Kind,
    string? FilePath,
    string? ArchivePath,
    VppEntry? Entry)
{
    /// <summary>True when a different extension took priority over the requested one (textures only).</summary>
    public bool IsSupersede =>
        !string.Equals(RequestedName, ResolvedName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the file lives inside a .vpp (and is therefore read-only at its origin).</summary>
    public bool IsArchived => ArchivePath is not null;

    /// <summary>A short description for the UI, e.g. the folder or "meshes.vpp".</summary>
    public string DisplayLocation => Origin ?? (ArchivePath is not null
        ? Path.GetFileName(ArchivePath)
        : Path.GetDirectoryName(FilePath) ?? string.Empty);

    /// <summary>
    /// For an asset found in a resolver's overlay (see <see cref="AssetResolver.WithOverlay"/>): the overlay's
    /// label, shown as <see cref="DisplayLocation"/>. Null for files and archive entries.
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>
    /// The archive this entry came out of, as the resolver already had it indexed, so opening the
    /// asset does not re-read the archive's whole directory.
    /// </summary>
    internal VppArchive? Archive { get; init; }

    /// <summary>Opens an overlay asset (no file or archive entry behind it).</summary>
    internal Func<Stream>? Opener { get; init; }

    /// <summary>Opens the asset's bytes.</summary>
    /// <exception cref="IOException">The asset has no readable location any more.</exception>
    public Stream Open()
    {
        if (Opener is not null) return Opener();
        if (FilePath is not null) return File.OpenRead(FilePath);
        if (Entry is not null)
        {
            var archive = Archive ?? (ArchivePath is not null ? VppArchive.Open(ArchivePath) : null);
            if (archive is not null) return archive.OpenEntry(Entry);
        }
        throw new IOException($"'{ResolvedName}' has no readable location.");
    }

    /// <summary>Reads the asset's bytes into memory.</summary>
    public byte[] ReadAllBytes()
    {
        using var stream = Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The last-write time and size used to key caches (the archive's, for an archived file).</summary>
    public (DateTime Time, long Size) Stamp()
    {
        if (Opener is not null) return (DateTime.MinValue, -1); // overlay: its resolver keeps its own cache
        try
        {
            string path = FilePath ?? ArchivePath!;
            var info = new FileInfo(path);
            return (info.LastWriteTimeUtc, info.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (DateTime.MinValue, -1);
        }
    }
}

/// <summary>
/// The .vpp archives one search location contributes, grouped for a browser. Groups come back in
/// the resolver's own search order.
/// </summary>
/// <param name="Kind">Which search location this is.</param>
/// <param name="Folder">The folder the archives were found in.</param>
/// <param name="Label">The heading a browser shows, e.g. "Red Faction (D:\Games\RF)".</param>
/// <param name="ArchivePaths">Full paths of the .vpp files, in the order they are searched.</param>
/// <param name="IsSearched">
/// True when a name inside these archives is one the engine — and this resolver — would find. A .vpp
/// sitting next to the document is listed for convenience but is <i>not</i> searched: RF only reads
/// packfiles from its own install.
/// </param>
public sealed record VppSourceGroup(
    AssetSourceKind Kind,
    string Folder,
    string Label,
    IReadOnlyList<string> ArchivePaths,
    bool IsSearched);

/// <summary>Where <see cref="AssetResolver"/> looks.</summary>
public sealed class AssetResolverOptions
{
    /// <summary>The folder holding the active document, searched first (loose files only).</summary>
    public string? DocumentFolder { get; init; }

    /// <summary>Extra folders the user configured, searched in order.</summary>
    public IReadOnlyList<string> SearchFolders { get; init; } = [];

    /// <summary>The Red Faction install directory, searched last.</summary>
    public string? GameDirectory { get; init; }
}

/// <summary>
/// Finds any file the engine would find by name — clips, meshes, tables, textures — through the
/// document's folder, the user's search folders (loose files, then their .vpp archives) and finally
/// the game install (root .vpp files, then <c>user_maps</c>). Texture names alone go through the
/// engine's supersede chain first (.dds, .png, .jpg, .jpeg siblings, then the literal name). Indexes
/// are built lazily, cached by folder/archive timestamp, and safe to use from several threads.
/// </summary>
public sealed class AssetResolver
{
    private readonly object _gate;
    private readonly Dictionary<string, FolderIndex> _folders;
    private readonly Dictionary<string, ArchiveIndex> _archives;
    private readonly object _providerGate = new();
    private readonly ReadStats _stats;
    private readonly Overlay? _overlay;
    private List<Provider>? _providers;

    public AssetResolver(AssetResolverOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _gate = new object();
        _folders = new(StringComparer.OrdinalIgnoreCase);
        _archives = new(StringComparer.OrdinalIgnoreCase);
        _stats = new ReadStats();
        Cache = new AssetCache();
    }

    private AssetResolver(AssetResolverOptions options, AssetResolver shared, Overlay? overlay)
    {
        Options = options;
        _gate = shared._gate;
        _folders = shared._folders;
        _archives = shared._archives;
        _stats = shared._stats;
        _overlay = overlay;
        // Overlay locations have no file stamp, so a new overlay gets its own probe cache.
        Cache = ReferenceEquals(overlay, shared._overlay) ? shared.Cache : new AssetCache();
    }

    /// <summary>An in-memory layer searched before everything else (see <see cref="WithOverlay"/>).</summary>
    private sealed record Overlay(string Label, Func<string, bool> Contains, Func<string, Stream?> Open);

    /// <summary>
    /// State shared by a resolver and every <see cref="WithDocumentFolder"/> copy of it (guarded by
    /// <c>_gate</c>): read counters, and one lock per archive path so concurrent lookups that miss the
    /// index read an archive's directory once between them instead of once each.
    /// </summary>
    private sealed class ReadStats
    {
        public int Reads;
        public int Repeats;
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// How many archive directories this resolver (and the copies sharing its indexes) has read from
    /// disk, and how many of those reads were of an archive already read before (a changed file, or an
    /// <see cref="Invalidate"/>). Diagnostics only.
    /// </summary>
    public (int Reads, int Repeats) ArchiveDirectoryReads
    {
        get
        {
            lock (_gate) return (_stats.Reads, _stats.Repeats);
        }
    }

    /// <summary>
    /// A resolver with the same search folders and game directory but another document folder,
    /// sharing this one's folder listings, archive directories and probe cache (which stay keyed by
    /// path and timestamp, so sharing them is safe). Opening several documents therefore reads the
    /// game's archive directories once, not once per document. <see cref="Invalidate"/> on either
    /// clears the shared indexes.
    /// </summary>
    /// <param name="documentFolder">The new document folder, or null for none.</param>
    public AssetResolver WithDocumentFolder(string? documentFolder) => new(new AssetResolverOptions
    {
        DocumentFolder = documentFolder,
        SearchFolders = Options.SearchFolders,
        GameDirectory = Options.GameDirectory,
    }, this, _overlay);

    /// <summary>
    /// A resolver that first looks in an in-memory layer, for example the other entries of the packfile a
    /// previewed asset came from, and otherwise resolves exactly as this one (sharing its indexes). A name
    /// goes through the whole probe order (<see cref="ProbeCandidates"/>) in the layer before any folder or
    /// archive is searched, so an asset beside the previewed one wins over the game's copies. Locations found
    /// in the layer have <see cref="AssetSourceKind.DocumentFolder"/>, no file or archive path, and
    /// <paramref name="label"/> as their <see cref="AssetLocation.DisplayLocation"/>. The layer is not
    /// enumerated (<see cref="Enumerate"/>, <see cref="EnumerateAll(IReadOnlyCollection{string}, Func{string, IReadOnlyList{VppEntry}?}?, CancellationToken)"/>).
    /// </summary>
    /// <param name="label">Where the layer's assets come from ("maps.vpp").</param>
    /// <param name="contains">True when the layer has a bare name (case-insensitive); called from any thread.</param>
    /// <param name="open">Opens a name the layer has, or null when it is gone; called from any thread.</param>
    public AssetResolver WithOverlay(string label, Func<string, bool> contains, Func<string, Stream?> open)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(contains);
        ArgumentNullException.ThrowIfNull(open);
        return new(Options, this, new Overlay(label, contains, open));
    }

    /// <summary>
    /// The names <see cref="Resolve"/> probes for <paramref name="bareName"/>, in order: for a texture name the
    /// supersede siblings (<see cref="SupersedeProbeExtensions"/>) and then the name itself; otherwise the name.
    /// </summary>
    public static IEnumerable<string> ProbeCandidates(string bareName)
    {
        ArgumentNullException.ThrowIfNull(bareName);
        if (IsTextureName(bareName))
        {
            string stem = StripTextureExtension(bareName);
            foreach (string ext in SupersedeProbeExtensions) yield return stem + ext;
            // The stock loader then takes a .vbm before a .tga of the same name, whichever was asked for
            // (Alpine Faction's bmpman.cpp: "ATX > DDS > PNG/JPG > VBM > TGA").
            if (IsVbmOrTga(bareName))
            {
                yield return stem + ".vbm";
                yield return stem + ".tga";
                yield break;
            }
        }
        yield return bareName;
    }

    private static bool IsVbmOrTga(string name) =>
        name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>user_maps</c> subfolders the game treats as asset directories, in search order.
    /// </summary>
    // Order: "textures" is the editor's own custom-texture folder (editor_patch/textures.cpp), not an
    // engine packfile directory, so it stays first; the other three mirror Alpine Faction's
    // load_additional_packfiles_new (game_patch/misc/vpackfile.cpp), which loads projects, single then
    // multi — also their precedence, since a user_maps entry never overrides one already registered.
    public static IReadOnlyList<string> GameSubFolders { get; } = ["textures", "projects", "single", "multi"];

    /// <summary>The game folder's sub-folder the game plays Bink movies (<c>.bik</c>) from, as loose files.</summary>
    public static string MovieFolder { get; } = Path.Combine("data", "movies");

    /// <summary>
    /// Every extension <c>bm_read_header</c> can resolve a texture from, in precedence order
    /// (<c>g_texture_extensions</c> in Alpine Faction's bmpman.cpp).
    /// </summary>
    public static IReadOnlyList<string> TextureExtensions { get; } =
        [".atx", ".dds", ".png", ".jpg", ".jpeg", ".vbm", ".tga", ".pcx", ".vaf", ".m2v"];

    /// <summary>
    /// Extensions the engine probes as siblings of a texture name before the literal name: the
    /// supersede chain minus <c>.atx</c>.
    /// </summary>
    public static IReadOnlyList<string> SupersedeProbeExtensions { get; } = [".dds", ".png", ".jpg", ".jpeg"];

    /// <summary>The folders this resolver searches.</summary>
    public AssetResolverOptions Options { get; }

    /// <summary>Probe results cached by path, timestamp and size.</summary>
    public AssetCache Cache { get; }

    /// <summary>Raised after the on-disk index changes, so dependent views can refresh.</summary>
    public event EventHandler? IndexChanged;

    /// <summary>True when <paramref name="fileName"/> ends with a texture extension the engine resolves.</summary>
    public static bool IsTextureName(string fileName) =>
        TextureExtensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Port of <c>bm_strip_texture_ext</c>: removes a recognised texture extension.</summary>
    public static string StripTextureExtension(string fileName)
    {
        foreach (string ext in TextureExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return fileName[..^ext.Length];
        }
        return fileName;
    }

    /// <summary>
    /// Drops every cached folder listing, archive directory and probe. Call this from a
    /// <c>FileSystemWatcher</c> when files change on disk.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _folders.Clear();
            _archives.Clear();
        }
        lock (_providerGate) _providers = null;
        Cache.Clear();
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds the folder and archive indexes off the UI thread.</summary>
    public Task PrepareAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => { foreach (var p in Providers()) p.Ensure(this, cancellationToken); }, cancellationToken);

    /// <summary>
    /// Resolves <paramref name="requestedName"/> (any folder part is ignored: RF's file system is
    /// flat), or returns null when nothing matches. A texture name tries the supersede chain first;
    /// anything else is looked up literally.
    /// </summary>
    public AssetLocation? Resolve(string requestedName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(requestedName)) return null;
        string bare = Path.GetFileName(requestedName.Trim());
        if (_overlay is { } overlay)
        {
            foreach (string candidate in ProbeCandidates(bare))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!overlay.Contains(candidate)) continue;
                string name = candidate;
                return new AssetLocation(bare, name, AssetSourceKind.DocumentFolder, null, null, null)
                {
                    Origin = overlay.Label,
                    Opener = () => overlay.Open(name) ?? throw new IOException($"'{name}' is no longer in {overlay.Label}."),
                };
            }
        }
        var scope = new LookupScope();
        if (IsTextureName(bare))
        {
            string stem = StripTextureExtension(bare);
            // The engine probes the chain by extension, in order, regardless of what was asked for. A
            // candidate equal to the requested name is probed in its own slot rather than skipped,
            // otherwise asking for "foo.png" while foo.png and foo.jpg both exist would find the .jpg.
            foreach (string ext in SupersedeProbeExtensions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Find(bare, stem + ext, cancellationToken, scope) is { } superseded) return superseded;
            }
            // A .tga or .vbm request: the .vbm comes before the .tga (see ProbeCandidates).
            if (IsVbmOrTga(bare))
                return Find(bare, stem + ".vbm", cancellationToken, scope) ?? Find(bare, stem + ".tga", cancellationToken, scope);
        }
        return Find(bare, bare, cancellationToken, scope);
    }

    /// <summary>
    /// Resolves the first of several candidate names that exists (for example the
    /// <see cref="Formats.Tbl.TblFileName.Candidates"/> of a table's <c>.v3d</c>), or null.
    /// </summary>
    public AssetLocation? ResolveFirst(IEnumerable<string> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        foreach (string name in candidates)
        {
            if (Resolve(name, cancellationToken) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>Resolves a name on a background thread.</summary>
    public Task<AssetLocation?> ResolveAsync(string requestedName, CancellationToken cancellationToken = default) =>
        Task.Run(() => Resolve(requestedName, cancellationToken), cancellationToken);

    /// <summary>
    /// Resolves and probes an image in one step. Returns null when the file is not found; throws
    /// <see cref="ImageDecodeException"/> when it is found but unreadable.
    /// </summary>
    public ImageInfo? ProbeImage(string requestedName, CancellationToken cancellationToken = default)
    {
        var location = Resolve(requestedName, cancellationToken);
        return location is null ? null : Cache.ProbeImage(location);
    }

    /// <summary>Probes an already-resolved asset, caching by path, timestamp and size.</summary>
    public ImageInfo ProbeImage(AssetLocation location) => Cache.ProbeImage(location);

    /// <summary>Resolves and probes an image on a background thread.</summary>
    public Task<ImageInfo?> ProbeImageAsync(string requestedName, CancellationToken cancellationToken = default) =>
        Task.Run(() => ProbeImage(requestedName, cancellationToken), cancellationToken);

    /// <summary>
    /// True when a name inside <paramref name="archivePath"/> is one this resolver would find, so
    /// a reference may safely name it alone (archives next to the document are listed but not searched).
    /// </summary>
    /// <param name="archivePath">Full path of the .vpp.</param>
    public bool IsSearchedArchive(string? archivePath)
    {
        if (string.IsNullOrWhiteSpace(archivePath)) return false;
        foreach (var group in DescribeArchiveSources())
        {
            if (!group.IsSearched) continue;
            foreach (string path in group.ArchivePaths)
            {
                if (string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    /// <summary>The search locations in order, for the Settings and Help screens.</summary>
    public IReadOnlyList<string> DescribeSearchOrder() => [.. Providers().Select(p => p.Describe())];

    /// <summary>
    /// Every loose file and archive entry this resolver can see whose name ends with one of
    /// <paramref name="extensions"/>, in search order, each name once (the location that wins
    /// resolution). This is what the library indexes.
    /// </summary>
    public IReadOnlyList<AssetLocation> Enumerate(IReadOnlyCollection<string> extensions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AssetLocation>();
        foreach (var provider in Providers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var location in provider.List(this, cancellationToken))
            {
                string name = location.ResolvedName;
                if (!extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))) continue;
                if (seen.Add(name)) result.Add(location);
            }
        }
        return result;
    }

    /// <summary>
    /// Every loose file and archive entry this resolver can see whose name ends with one of
    /// <paramref name="extensions"/>, in search order, INCLUDING the copies a higher-priority location
    /// shadows (a name can appear several times; the first occurrence is the one
    /// <see cref="Resolve"/> finds). The library uses this to say "shadowed by ...".
    /// </summary>
    /// <param name="extensions">Extensions to keep, e.g. ".rfa".</param>
    /// <param name="cachedArchiveEntries">
    /// Optional: given a searched archive's full path, returns its entries from the caller's own cache
    /// (for example one validated by the archive's path, last-write time and size), or null to have the
    /// archive's directory read as usual. Lets a warm caller skip opening hundreds of archives. Entries
    /// returned this way produce locations that open the archive on demand.
    /// </param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public IReadOnlyList<AssetLocation> EnumerateAll(
        IReadOnlyCollection<string> extensions,
        Func<string, IReadOnlyList<VppEntry>?>? cachedArchiveEntries = null,
        CancellationToken cancellationToken = default) =>
        EnumerateAll(extensions, cachedArchiveEntries, null, cancellationToken);

    /// <summary>
    /// <see cref="EnumerateAll(IReadOnlyCollection{string}, Func{string, IReadOnlyList{VppEntry}?}?, CancellationToken)"/>,
    /// reporting progress through the searched archives: <paramref name="archiveDone"/> gets (archives
    /// done, archives in all searched folders) after each one, on the calling thread.
    /// </summary>
    public IReadOnlyList<AssetLocation> EnumerateAll(
        IReadOnlyCollection<string> extensions,
        Func<string, IReadOnlyList<VppEntry>?>? cachedArchiveEntries,
        Action<int, int>? archiveDone,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        bool Wanted(string name) => extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        var result = new List<AssetLocation>();
        var providers = Providers();
        int totalArchives = archiveDone is null ? 0 : providers.OfType<ArchiveFolderProvider>().Sum(p => GetFolder(p.Folder).Archives.Count);
        int doneArchives = 0;
        foreach (var provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider is ArchiveFolderProvider archives)
            {
                foreach (string vpp in GetFolder(archives.Folder).Archives)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cachedArchiveEntries?.Invoke(vpp) is { } entries)
                    {
                        foreach (var entry in entries)
                        {
                            if (Wanted(entry.Name))
                                result.Add(new AssetLocation(entry.Name, entry.Name, archives.Kind, null, vpp, entry));
                        }
                    }
                    else if (GetArchive(vpp) is { } archive)
                    {
                        foreach (var entry in archive.Entries)
                        {
                            if (Wanted(entry.Name))
                                result.Add(new AssetLocation(entry.Name, entry.Name, archives.Kind, null, vpp, entry) { Archive = archive });
                        }
                    }
                    archiveDone?.Invoke(++doneArchives, totalArchives);
                }
                continue;
            }
            foreach (var location in provider.List(this, cancellationToken))
            {
                if (Wanted(location.ResolvedName)) result.Add(location);
            }
        }
        return result;
    }

    /// <summary>
    /// Every .vpp archive this resolver can see, grouped by search location and in search order.
    /// Folder listings are the cached ones resolution uses; the archives are not opened here.
    /// </summary>
    public IReadOnlyList<VppSourceGroup> DescribeArchiveSources()
    {
        var groups = new List<VppSourceGroup>();

        if (!string.IsNullOrWhiteSpace(Options.DocumentFolder))
        {
            // Listed but not searched: RF reads packfiles from its install, never from a document's
            // folder, so referring into one of these by name would leave the asset missing in game.
            Add(Options.DocumentFolder!, AssetSourceKind.DocumentFolder, "Next to this document", searched: false);
        }

        foreach (string folder in Options.SearchFolders)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            Add(folder, AssetSourceKind.SearchFolderArchive, folder, searched: true);
        }

        if (!string.IsNullOrWhiteSpace(Options.GameDirectory))
        {
            string game = Options.GameDirectory!;
            Add(game, AssetSourceKind.GameArchive, $"Red Faction ({game})", searched: true);
            foreach (string sub in GameSubFolders)
            {
                string dir = Path.Combine(game, "user_maps", sub);
                Add(dir, AssetSourceKind.GameFolderArchive, Path.Combine("user_maps", sub), searched: true);
            }
        }

        return groups;

        void Add(string folder, AssetSourceKind kind, string label, bool searched)
        {
            var archives = GetFolder(folder).Archives;
            if (archives.Count == 0) return;
            groups.Add(new VppSourceGroup(kind, folder, label, [.. archives], searched));
        }
    }

    /// <summary>
    /// The archive at <paramref name="path"/>, out of the same cache resolution uses (keyed by path,
    /// timestamp and size), or null when it is missing or unreadable.
    /// </summary>
    public VppArchive? OpenCachedArchive(string path) =>
        string.IsNullOrWhiteSpace(path) ? null : GetArchive(path);

    private AssetLocation? Find(string requested, string candidate, CancellationToken cancellationToken, LookupScope scope)
    {
        foreach (var provider in Providers())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.TryFind(this, requested, candidate, cancellationToken, scope) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>
    /// State of one <see cref="Resolve"/> call: the archives' stamps (last write, size) per folder, taken from ONE
    /// listing of that folder the first time the call searches it. A name that is in no archive walks every indexed
    /// archive for every probe candidate; checking each archive's stamp with its own file-system call made such a
    /// miss cost thousands of calls (about 60 ms over a 1,400-packfile game folder). A changed archive is still
    /// noticed by the very next lookup.
    /// </summary>
    private sealed class LookupScope
    {
        private Dictionary<string, Dictionary<string, (DateTime Time, long Size)>?>? _stamps;

        /// <summary>File name -> stamp of every .vpp in <paramref name="folder"/>; null when the folder cannot be listed.</summary>
        public Dictionary<string, (DateTime Time, long Size)>? StampsFor(string folder)
        {
            _stamps ??= new(StringComparer.OrdinalIgnoreCase);
            if (_stamps.TryGetValue(folder, out var known)) return known;
            Dictionary<string, (DateTime Time, long Size)>? stamps = new(StringComparer.OrdinalIgnoreCase);
            try
            {
                // The listing carries each file's size and last-write time, so no per-file call is needed.
                foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
                {
                    if (file.Name.EndsWith(".vpp", StringComparison.OrdinalIgnoreCase))
                        stamps[file.Name] = (file.LastWriteTimeUtc, file.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable folder: its archives are checked one by one.
                stamps = null;
            }
            return _stamps[folder] = stamps;
        }
    }

    // ── Providers ─────────────────────────────────────────────────────────────

    private List<Provider> Providers()
    {
        lock (_providerGate)
        {
            if (_providers is not null) return _providers;
            var list = new List<Provider>();

            if (!string.IsNullOrWhiteSpace(Options.DocumentFolder))
                list.Add(new FolderProvider(Options.DocumentFolder!, AssetSourceKind.DocumentFolder));

            foreach (string folder in Options.SearchFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                list.Add(new FolderProvider(folder, AssetSourceKind.SearchFolder));
            }
            // Loose files in every search folder come before any archive in them.
            foreach (string folder in Options.SearchFolders)
            {
                if (string.IsNullOrWhiteSpace(folder)) continue;
                list.Add(new ArchiveFolderProvider(folder, AssetSourceKind.SearchFolderArchive));
            }

            if (!string.IsNullOrWhiteSpace(Options.GameDirectory))
            {
                string game = Options.GameDirectory!;
                list.Add(new ArchiveFolderProvider(game, AssetSourceKind.GameArchive));
                // The game plays Bink movies (intro.bik, the cutscenes) as loose files from data\movies (RF.exe
                // holds "data\movies" and "*.bik"); nothing else is read from that folder.
                list.Add(new FolderProvider(Path.Combine(game, MovieFolder), AssetSourceKind.GameFolder, ".bik"));
                foreach (string sub in GameSubFolders)
                {
                    string dir = Path.Combine(game, "user_maps", sub);
                    list.Add(new FolderProvider(dir, AssetSourceKind.GameFolder));
                    list.Add(new ArchiveFolderProvider(dir, AssetSourceKind.GameFolderArchive));
                }
            }

            _providers = list;
            return list;
        }
    }

    private abstract class Provider
    {
        public abstract AssetLocation? TryFind(
            AssetResolver owner, string requested, string candidate, CancellationToken cancellationToken, LookupScope scope);

        public abstract IEnumerable<AssetLocation> List(AssetResolver owner, CancellationToken cancellationToken);

        public abstract void Ensure(AssetResolver owner, CancellationToken cancellationToken);

        public abstract string Describe();
    }

    // only: when set, the folder serves just files with this extension (the game's movie folder).
    private sealed class FolderProvider(string folder, AssetSourceKind kind, string? only = null) : Provider
    {
        private bool Serves(string name) => only is null || name.EndsWith(only, StringComparison.OrdinalIgnoreCase);

        public override AssetLocation? TryFind(
            AssetResolver owner, string requested, string candidate, CancellationToken cancellationToken, LookupScope scope)
        {
            if (!Serves(candidate)) return null;
            var index = owner.GetFolder(folder);
            return index.Files.TryGetValue(candidate, out string? path)
                ? new AssetLocation(requested, candidate, kind, path, null, null)
                : null;
        }

        public override IEnumerable<AssetLocation> List(AssetResolver owner, CancellationToken cancellationToken)
        {
            foreach (var (name, path) in owner.GetFolder(folder).Files.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                if (Serves(name)) yield return new AssetLocation(name, name, kind, path, null, null);
        }

        public override void Ensure(AssetResolver owner, CancellationToken cancellationToken) => owner.GetFolder(folder);

        public override string Describe() => only is null ? folder : Path.Combine(folder, "*" + only);
    }

    private sealed class ArchiveFolderProvider(string folder, AssetSourceKind kind) : Provider
    {
        public string Folder => folder;

        public AssetSourceKind Kind => kind;

        public override AssetLocation? TryFind(
            AssetResolver owner, string requested, string candidate, CancellationToken cancellationToken, LookupScope scope)
        {
            var stamps = scope.StampsFor(folder);
            foreach (string vpp in owner.GetFolder(folder).Archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VppArchive? archive;
                if (stamps is null) archive = owner.GetArchive(vpp);
                else if (stamps.TryGetValue(Path.GetFileName(vpp), out var stamp)) archive = owner.GetArchive(vpp, stamp.Time, stamp.Size);
                else continue; // gone since the folder was indexed

                if (archive?.TryGetEntry(candidate, out var entry) == true)
                    return new AssetLocation(requested, entry.Name, kind, null, vpp, entry) { Archive = archive };
            }
            return null;
        }

        public override IEnumerable<AssetLocation> List(AssetResolver owner, CancellationToken cancellationToken)
        {
            foreach (string vpp in owner.GetFolder(folder).Archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archive = owner.GetArchive(vpp);
                if (archive is null) continue;
                foreach (var entry in archive.Entries)
                    yield return new AssetLocation(entry.Name, entry.Name, kind, null, vpp, entry) { Archive = archive };
            }
        }

        public override void Ensure(AssetResolver owner, CancellationToken cancellationToken)
        {
            foreach (string vpp in owner.GetFolder(folder).Archives)
            {
                cancellationToken.ThrowIfCancellationRequested();
                owner.GetArchive(vpp);
            }
        }

        public override string Describe() => Path.Combine(folder, "*.vpp");
    }

    // ── Indexes ───────────────────────────────────────────────────────────────

    private sealed record FolderIndex(DateTime Stamp, Dictionary<string, string> Files, List<string> Archives);

    private sealed record ArchiveIndex(DateTime Time, long Size, VppArchive? Archive);

    private FolderIndex GetFolder(string folder)
    {
        DateTime stamp;
        try
        {
            stamp = Directory.Exists(folder) ? Directory.GetLastWriteTimeUtc(folder) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            stamp = DateTime.MinValue;
        }

        lock (_gate)
        {
            if (_folders.TryGetValue(folder, out var cached) && cached.Stamp == stamp) return cached;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var archives = new List<string>();
        try
        {
            if (Directory.Exists(folder))
            {
                foreach (string path in Directory.EnumerateFiles(folder))
                {
                    string name = Path.GetFileName(path);
                    files.TryAdd(name, path);
                    if (name.EndsWith(".vpp", StringComparison.OrdinalIgnoreCase)) archives.Add(path);
                }
                archives.Sort(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable folder simply contributes nothing.
        }

        var index = new FolderIndex(stamp, files, archives);
        lock (_gate) _folders[folder] = index;
        return index;
    }

    private VppArchive? GetArchive(string path)
    {
        DateTime time;
        long size;
        try
        {
            var info = new FileInfo(path);
            time = info.LastWriteTimeUtc;
            size = info.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        return GetArchive(path, time, size);
    }

    /// <summary>The archive at <paramref name="path"/> given its current stamp (from a folder listing), read only when the cached one differs.</summary>
    private VppArchive? GetArchive(string path, DateTime time, long size)
    {
        object pathLock;
        lock (_gate)
        {
            if (_archives.TryGetValue(path, out var cached) && cached.Time == time && cached.Size == size)
                return cached.Archive;
            if (!_stats.PathLocks.TryGetValue(path, out pathLock!)) _stats.PathLocks[path] = pathLock = new object();
        }

        // Single flight: the library build, the table load and every texture lookup walk the same
        // archives in the same order, so on a cold start they used to read the same directories at the
        // same time (measured: a third more reads, all concurrent random I/O). Now the first caller
        // reads and the others wait for its result.
        lock (pathLock)
        {
            lock (_gate)
            {
                if (_archives.TryGetValue(path, out var cached) && cached.Time == time && cached.Size == size)
                    return cached.Archive;
                _stats.Reads++;
                if (!_stats.Seen.Add(path)) _stats.Repeats++;
            }

            VppArchive? archive = null;
            try
            {
                archive = VppArchive.Open(path);
            }
            catch (Exception ex) when (ex is VppFormatException or IOException or UnauthorizedAccessException)
            {
                // A broken archive is skipped rather than failing the whole search.
            }

            lock (_gate) _archives[path] = new ArchiveIndex(time, size, archive);
            return archive;
        }
    }

    /// <summary>
    /// Puts an archive directory the caller already knows (the library's persistent cache, validated by
    /// the archive's last-write time and size) into the shared index, so lookups do not read it from
    /// disk. An entry already indexed with the same stamp is kept.
    /// </summary>
    internal void SeedArchive(VppArchive archive, DateTime time, long size)
    {
        ArgumentNullException.ThrowIfNull(archive);
        lock (_gate)
        {
            if (_archives.TryGetValue(archive.Path, out var existing) && existing.Time == time && existing.Size == size) return;
            _archives[archive.Path] = new ArchiveIndex(time, size, archive);
        }
    }

    /// <summary>The archive at <paramref name="path"/> if it is already indexed (never reads the disk).</summary>
    internal VppArchive? PeekArchive(string path)
    {
        lock (_gate) return _archives.TryGetValue(path, out var cached) ? cached.Archive : null;
    }
}
