using System.Windows.Threading;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.Services;

/// <summary>
/// The packfiles open in Cairn as session sources of clips and meshes (DESIGN.md 12, L14): every archive an
/// <see cref="IOpenArchiveSources"/> offers (the packfile module's tabs) gets a <see cref="PackfileLibrary"/>, probed off the
/// UI thread (headers only, cancellable) and cached per packfile snapshot, so switching tabs never probes again. A source
/// appears when its tab opens, is probed again when its entries change, and disappears when the tab closes. The
/// search-path library (<see cref="AssetServices"/>) is never rebuilt for any of this. <see cref="Changed"/> is raised on
/// the UI thread.
/// </summary>
public sealed class PackfileSources
{
    private readonly Dispatcher _dispatcher;
    private IShellContext? _host;
    private IReadOnlyList<OpenArchiveSource>? _testSources;
    private IReadOnlyList<OpenArchiveSource> _sources = [];
    private IReadOnlyList<PackfileLibrary> _libraries = [];
    private readonly Dictionary<string, (object Version, CancellationTokenSource Cts)> _probing = new(StringComparer.Ordinal);
    // Probed snapshots: the open ones' current snapshots always, plus up to CacheSize others (earlier snapshots of the
    // open packfiles, for undo and redo), least recently used evicted first. A closed packfile's are dropped.
    private const int CacheSize = 16;
    private readonly Dictionary<(string Key, object Version), PackfileLibrary> _cache = [];
    private readonly Dictionary<(string Key, object Version), long> _cacheUse = [];
    private long _useClock;
    private TaskCompletionSource _idle = Completed();

    public PackfileSources(Dispatcher dispatcher) => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>The probed packfiles in tab order (one still being probed is not listed yet; see <see cref="ProbingLabels"/>).</summary>
    public IReadOnlyList<PackfileLibrary> Libraries => _libraries;

    /// <summary>The names of the packfiles still being probed.</summary>
    public IReadOnlyList<string> ProbingLabels => [.. _sources.Where(s => _probing.ContainsKey(s.Key)).Select(s => s.Label)];

    /// <summary>Completes when no probe is running (self-tests wait for it).</summary>
    public Task Idle => _idle.Task;

    /// <summary>Raised on the UI thread when <see cref="Libraries"/> (or <see cref="ProbingLabels"/>) changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Follows the shell's open archives from now on (once, when the module initialises).</summary>
    public void Attach(IShellContext host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        // the providers are other modules: look for them once every module is initialised
        _dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var provider in host.Modules.OfType<IOpenArchiveSources>()) provider.OpenSourcesChanged += (_, _) => Refresh();
            Refresh();
        }), DispatcherPriority.Background);
    }

    /// <summary>Self-tests: use these sources instead of the shell's (null goes back to the shell's).</summary>
    internal void UseTestSources(IReadOnlyList<OpenArchiveSource>? sources)
    {
        _testSources = sources;
        Refresh();
    }

    /// <summary>Takes the current sources: probes the new and changed ones, drops the closed ones. UI thread.</summary>
    public void Refresh()
    {
        var sources = _testSources ?? AssetSiblings.OpenSources(_host);
        _sources = sources;
        var keys = sources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _probing.Keys.Where(k => !keys.Contains(k)).ToList())
        {
            _probing[gone].Cts.Cancel();
            _probing.Remove(gone);
        }
        // a closed packfile's snapshots are never asked for again (a tab opened later has a new key): drop them, so
        // neither their libraries nor the packfile snapshots they were read from stay alive
        foreach (var gone in _cache.Keys.Where(k => !keys.Contains(k.Key)).ToList()) Uncache(gone);
        foreach (var source in sources)
        {
            if (_cache.ContainsKey((source.Key, source.Version)))
            {
                // back to a snapshot already probed (an undo): a probe of another one is not needed any more
                if (_probing.Remove(source.Key, out var stale)) stale.Cts.Cancel();
                continue;
            }
            if (_probing.TryGetValue(source.Key, out var running))
            {
                if (ReferenceEquals(running.Version, source.Version)) continue;
                running.Cts.Cancel();
            }
            StartProbe(source);
        }
        Publish();
    }

    private void StartProbe(OpenArchiveSource source)
    {
        var cts = new CancellationTokenSource();
        _probing[source.Key] = (source.Version, cts);
        if (_idle.Task.IsCompleted) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var busy = BusyTracker.Begin("packfile library " + source.Label);
        var entries = source.Entries;
        var contents = new PackfileContents(source.Key, source.Label, source.ArchivePath, entries.Names, entries.ReadHead, entries.Read)
        {
            IsRedFactionII = source.IsRedFactionII,
        };
        _ = Task.Run(() => PackfileLibrary.Build(contents, cts.Token), cts.Token).ContinueWith(task => _dispatcher.BeginInvoke(new Action(() =>
        {
            busy.Dispose();
            if (_probing.TryGetValue(source.Key, out var current) && ReferenceEquals(current.Cts, cts)) _probing.Remove(source.Key);
            // a probe of a packfile closed meanwhile is not kept
            if (task.IsCompletedSuccessfully && _sources.Any(s => s.Key == source.Key))
            {
                var library = task.Result;
                LastProbeTimes[source.Label] = library.ProbeTime;
                _cache[(source.Key, source.Version)] = library;
                _cacheUse[(source.Key, source.Version)] = ++_useClock;
                Evict();
            }
            else if (task.Exception is { } ex) ErrorLog.Write("packfile library " + source.Label, ex.InnerException ?? ex);
            Publish();
        })), TaskScheduler.Default);
    }

    /// <summary>True when a probed snapshot of the source <paramref name="key"/> is kept (self-tests).</summary>
    internal bool IsCached(string key) => _cache.Keys.Any(k => k.Key == key);

    /// <summary>How long the latest probe of each packfile took, by label (diagnostics, the status file's measurement).</summary>
    public Dictionary<string, TimeSpan> LastProbeTimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the cache to the open packfiles' current snapshots plus <see cref="CacheSize"/> others: the least recently
    /// used of the others go first; a snapshot open now is never evicted (it would disappear from the library and the
    /// pickers, as nothing probes an unchanged packfile again).
    /// </summary>
    private void Evict()
    {
        var current = _sources.Select(s => (s.Key, s.Version)).ToHashSet();
        var others = _cache.Keys.Where(k => !current.Contains(k)).OrderBy(k => _cacheUse.GetValueOrDefault(k)).ToList();
        for (int i = 0; i < others.Count - CacheSize; i++) Uncache(others[i]);
    }

    private void Uncache((string Key, object Version) entry)
    {
        _cache.Remove(entry);
        _cacheUse.Remove(entry);
    }

    private void Publish()
    {
        var libraries = new List<PackfileLibrary>();
        foreach (var source in _sources)
        {
            if (!_cache.TryGetValue((source.Key, source.Version), out var library)) continue;
            libraries.Add(library);
            // in use now: the last to be evicted once the packfile changes (an undo comes back to it)
            _cacheUse[(source.Key, source.Version)] = ++_useClock;
        }
        bool same = libraries.Count == _libraries.Count && libraries.Zip(_libraries).All(p => ReferenceEquals(p.First, p.Second));
        string probing = string.Join("|", ProbingLabels);
        _libraries = libraries;
        // raised when the libraries or the probing labels changed (the panel shows "reading …" for the latter)
        if (!same || probing != _lastProbing)
        {
            _lastProbing = probing;
            Changed?.Invoke(this, EventArgs.Empty);
        }
        if (_probing.Count == 0) _idle.TrySetResult();
    }

    private string _lastProbing = string.Empty;

    // ── Lookups ──────────────────────────────────────────────────────────────

    /// <summary>The packfile a library item (a clip or mesh of a packfile group) belongs to, or null for the search-path library's.</summary>
    public PackfileLibrary? SourceOf(object? item) => item is null ? null : _libraries.FirstOrDefault(l => l.Contains(item));

    /// <summary>
    /// True for a Red Faction II mesh of a packfile (the search-path library has none), also one held from a snapshot no
    /// longer current (<see cref="PackfileLibrary.IsRedFactionIIMesh"/>).
    /// </summary>
    public bool IsRedFactionII(LibraryMesh mesh) => PackfileLibrary.IsRedFactionIIMesh(mesh);

    /// <summary>True while the packfile with the source key <paramref name="key"/> is open (probed or still being probed).</summary>
    public bool IsOpen(string? key) => key is not null && _sources.Any(s => s.Key == key);

    /// <summary>The probed current snapshot of the open packfile <paramref name="key"/>, or null (closed, or being read).</summary>
    public PackfileLibrary? LibraryOf(string? key) => key is null ? null : _libraries.FirstOrDefault(l => l.Key == key);

    /// <summary>The open source with the key <paramref name="key"/> (its current entries and version), or null when it is closed.</summary>
    public OpenArchiveSource? OpenSourceOf(string? key) => key is null ? null : _sources.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// The open packfile the document at <paramref name="path"/> came from (a work copy opened with "Open in Cairn"), or
    /// null. UI thread.
    /// </summary>
    public PackfileLibrary? OwnOf(string? path) =>
        AssetSiblings.SourceKeyOf(_host, path) is { } key ? _libraries.FirstOrDefault(l => l.Key == key) : null;

    /// <summary>The packfiles in picker order for the document at <paramref name="path"/>: its own first, then the others.</summary>
    public IReadOnlyList<PackfileLibrary> InPickerOrder(string? path)
    {
        var own = OwnOf(path);
        return own is null ? _libraries : [own, .. _libraries.Where(l => !ReferenceEquals(l, own))];
    }

    /// <summary>The open source behind a packfile library (its entries, its Open in Cairn), or null when it was closed.</summary>
    public OpenArchiveSource? OpenSourceOf(PackfileLibrary library) => _sources.FirstOrDefault(s => s.Key == library.Key);

    /// <summary>
    /// Opens a packfile item in a tab through its packfile's "Open in Cairn" (the tab is then linked to the packfile).
    /// False when the packfile is closed or offers no such command (the caller opens it another way).
    /// </summary>
    public bool OpenInTab(object item)
    {
        // by its packfile's key: an item held from before the packfile changed still opens from the packfile
        if (OpenSourceOf(PackfileLibrary.KeyOf(item)) is not { OpenEntry: { } open }) return false;
        string? name = item switch { LibraryClip c => c.Name, LibraryMesh m => m.Name, _ => null };
        return name is not null && open(name);
    }

    /// <summary>
    /// Compiles a packfile mesh read into <paramref name="bytes"/> for a preview: a compiled mesh as it is, a Red Faction II
    /// or PlayStation 2 mesh through its converter; its textures come from the packfile's <paramref name="entries"/> (a
    /// Red Faction II mesh's from the <c>.peg</c> entries beside it), then from <paramref name="under"/>; null textures
    /// without entries. Any thread.
    /// </summary>
    /// <exception cref="AssetFormatException">The mesh cannot be read; the message says why.</exception>
    public static (V3dFile Mesh, AssetResolver? Textures) LoadMesh(string name, byte[] bytes, IAssetSiblings? entries, AssetResolver? under)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(bytes);
        var compiled = LegacyMeshSupport.IsLegacyName(name) || LegacyMeshSupport.IsLegacyContent(bytes)
            ? LegacyMeshSupport.ReadCompiled(bytes, name).Compiled
            : V3dReader.Read(bytes, name);
        var textures = entries is null ? null : ViewModels.LegacyMeshTextures.PreviewResolver(bytes, name, entries, under);
        return (compiled, textures);
    }
}
