using System.Diagnostics;
using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Formats.Tbl;
using Cairn.Workspace;

namespace Cairn.Rfa.Ui.Services;

/// <summary>
/// The app-wide asset context built from the settings: one <see cref="AssetResolver"/> (search folders
/// and game directory, no document folder), the <see cref="AssetLibrary"/> over it with its persistent
/// cache, and the game tables' <see cref="ClipUsageIndex"/>. Everything heavy runs off the UI thread;
/// the change events are raised on the UI thread. Documents get their own resolver through
/// <see cref="ResolverFor"/>, which shares this one's folder and archive indexes.
/// </summary>
public sealed class AssetServices
{
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _build;
    private int _generation;

    public AssetServices(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Resolver = new AssetResolver(new AssetResolverOptions());
        Library = new AssetLibrary(Resolver);
    }

    /// <summary>
    /// Where the library's probe cache lives (never the game directory). The <c>LOCALAPPDATA</c>
    /// environment variable wins over the shell folder when it is set to a rooted path (it normally names
    /// the same folder), so a diagnostic run can measure a first-ever build against an empty folder
    /// without touching the user's cache.
    /// </summary>
    public static string LibraryCachePath { get; } = Path.Combine(LocalDataFolder(), "Cairn", "library-cache.json");

    private static string LocalDataFolder()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        return !string.IsNullOrWhiteSpace(fromEnvironment) && Path.IsPathFullyQualified(fromEnvironment)
            ? fromEnvironment
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    /// <summary>The resolver for the current settings (no document folder).</summary>
    public AssetResolver Resolver { get; private set; }

    /// <summary>The library over <see cref="Resolver"/>.</summary>
    public AssetLibrary Library { get; private set; }

    /// <summary>The library's latest snapshot (empty until the first build finishes).</summary>
    public LibrarySnapshot Snapshot { get; private set; } = LibrarySnapshot.Empty;

    /// <summary>The game tables' clip usage (empty until loaded, or when no tables are found).</summary>
    public ClipUsageIndex Usage { get; private set; } = ClipUsageIndex.Empty;

    /// <summary>True while a library build or table load is running.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>A one-line description of what the build is doing, or of its result.</summary>
    public string ProgressText { get; private set; } = string.Empty;

    /// <summary>Progress 0..1 of the current phase, or null when unknown.</summary>
    public double? ProgressFraction { get; private set; }

    /// <summary>Wall-clock time of the last completed build, as measured here (UI-observed).</summary>
    public TimeSpan LastBuildTime { get; private set; }

    private readonly List<string> _buildLog = [];
    private TaskCompletionSource _indexed = CompletedSource();

    private static TaskCompletionSource CompletedSource()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    /// <summary>
    /// Completes once the current build has indexed every archive directory (read, or seeded from the
    /// library cache), or has ended. Texture, table and lint lookups wait for it: a lookup that misses
    /// (every texture's <c>.dds</c> probe does) walks every game archive (a modded install can have well
    /// over a thousand), and doing that while the
    /// build reads them (or before the cache has seeded them) means reading them again, from a second
    /// thread, as random I/O.
    /// </summary>
    public Task ArchivesIndexed => _indexed.Task;

    /// <summary>
    /// The latest build's milestones with their times ("0.12 s tables loaded"), for the diagnostic log.
    /// UI thread only.
    /// </summary>
    public IReadOnlyList<string> BuildLog => _buildLog;

    /// <summary>True when there is anywhere to look at all (a game directory or a search folder).</summary>
    public bool HasSources { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Snapshot"/> changes.</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>Raised on the UI thread when <see cref="Usage"/> changes.</summary>
    public event EventHandler? UsageChanged;

    /// <summary>Raised on the UI thread when the resolver is replaced (settings changed).</summary>
    public event EventHandler? ResolverChanged;

    /// <summary>Raised on the UI thread when <see cref="IsLoading"/> or the progress text changes.</summary>
    public event EventHandler? ProgressChanged;

    /// <summary>A resolver for a document in <paramref name="documentFolder"/>, sharing the indexes.</summary>
    public AssetResolver ResolverFor(string? documentFolder) => Resolver.WithDocumentFolder(documentFolder);

    // ── Loading library entries (phase 5) ────────────────────────────────────

    private readonly object _loadGate = new();
    private readonly Dictionary<string, object> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _loadOrder = new();

    /// <summary>
    /// Reads and parses a library clip off the UI thread (a reference clip, a state clip for the layered
    /// preview, a clip to compare with). Recently loaded clips are kept (by location and stamp), so
    /// picking the same one again is instant. Failures surface as the task's exception
    /// (<see cref="IOException"/>, <see cref="Cairn.Formats.AssetFormatException"/>…). Tracked by
    /// <see cref="BusyTracker"/>, so diagnostic runs wait for it.
    /// </summary>
    public Task<Cairn.Rfa.Formats.Rfa.RfaClip> LoadClipAsync(LibraryClip clip, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return LoadAsync(clip.Location, clip.Name, bytes => Cairn.Rfa.Formats.Rfa.RfaReader.Read(bytes, clip.Name), cancellationToken);
    }

    /// <summary>Reads and parses a library mesh off the UI thread (cached like <see cref="LoadClipAsync"/>).</summary>
    public Task<Cairn.Rfa.Formats.V3d.V3dFile> LoadMeshAsync(LibraryMesh mesh, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        return LoadAsync(mesh.Location, mesh.Name, bytes => Cairn.Rfa.Formats.V3d.V3dReader.Read(bytes, mesh.Name), cancellationToken);
    }

    private async Task<T> LoadAsync<T>(AssetLocation location, string name, Func<byte[], T> parse, CancellationToken cancellationToken) where T : class
    {
        var stamp = location.Stamp();
        string key = $"{location.ArchivePath}|{location.FilePath}|{location.ResolvedName}|{location.Entry?.Offset}|{stamp.Time.Ticks}|{stamp.Size}";
        lock (_loadGate)
        {
            if (_loaded.TryGetValue(key, out object? hit) && hit is T cached) return cached;
        }
        using var busy = BusyTracker.Begin("load " + name);
        var result = await Task.Run(() => parse(location.ReadAllBytes()), cancellationToken).ConfigureAwait(true);
        lock (_loadGate)
        {
            if (_loaded.TryAdd(key, result))
            {
                _loadOrder.Enqueue(key);
                while (_loadOrder.Count > 24) _loaded.Remove(_loadOrder.Dequeue());
            }
        }
        return result;
    }

    /// <summary>
    /// Rebuilds the resolver from <paramref name="settings"/> and starts a library build and a table
    /// load in the background. A build already running is cancelled.
    /// </summary>
    /// <param name="settings">The asset settings.</param>
    /// <param name="useCacheFile">True to use the library's cache file.</param>
    /// <param name="shared">
    /// The shell's resolver: when it searches the same places, this one shares its folder and archive indexes, so
    /// the game's archive directories are read once for the whole app rather than once here and once by the shell.
    /// </param>
    public void Reconfigure(AppSettings settings, bool useCacheFile, AssetResolver? shared = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _build?.Cancel();
        var options = new AssetResolverOptions
        {
            SearchFolders = [.. settings.SearchFolders.Where(f => !string.IsNullOrWhiteSpace(f))],
            GameDirectory = string.IsNullOrWhiteSpace(settings.GameDirectory) ? null : settings.GameDirectory,
        };
        HasSources = options.GameDirectory is not null || options.SearchFolders.Count > 0;
        bool same = shared is not null && shared.Options.DocumentFolder is null
            && string.Equals(shared.Options.GameDirectory, options.GameDirectory, StringComparison.OrdinalIgnoreCase)
            && shared.Options.SearchFolders.SequenceEqual(options.SearchFolders, StringComparer.OrdinalIgnoreCase);
        Resolver = same ? shared!.WithDocumentFolder(null) : new AssetResolver(options);
        Library = new AssetLibrary(Resolver, useCacheFile ? LibraryCachePath : null);
        ResolverChanged?.Invoke(this, EventArgs.Empty);
        StartBuild();
    }

    /// <summary>Re-reads everything (Library &gt; Refresh), keeping the resolver's options.</summary>
    public void Refresh()
    {
        _build?.Cancel();
        Resolver.Invalidate();
        StartBuild();
    }

    private void StartBuild()
    {
        var cts = new CancellationTokenSource();
        _build = cts;
        int generation = ++_generation;
        var library = Library;
        var resolver = Resolver;
        var previousIndexed = _indexed;
        var indexed = _indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previousIndexed.TrySetResult();

        if (!HasSources)
        {
            indexed.TrySetResult();
            Snapshot = LibrarySnapshot.Empty;
            Usage = ClipUsageIndex.Empty;
            SetProgress(false, string.Empty, null);
            LibraryChanged?.Invoke(this, EventArgs.Empty);
            UsageChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        SetProgress(true, "Looking for clips and meshes…", null);
        var clock = Stopwatch.StartNew();
        var busy = BusyTracker.Begin("library build");
        _buildLog.Clear();
        LibraryBuildPhase? lastPhase = null;
        void Mark(string what) => _buildLog.Add($"{clock.Elapsed.TotalSeconds:0.000} s {what}");
        var progress = new Progress<LibraryProgress>(p =>
        {
            if (generation != _generation) return;
            if (p.Phase != lastPhase)
            {
                lastPhase = p.Phase;
                Mark($"library {p.Phase}{(p.Total > 0 ? $" ({p.Total:N0})" : "")}");
            }
            string text = p.Phase switch
            {
                LibraryBuildPhase.LoadingCache => "Library: reading the cache…",
                LibraryBuildPhase.Enumerating => p.Total > 0
                    ? $"Library: archive directories {p.Done:N0} of {p.Total:N0}…"
                    : "Library: listing folders and archives…",
                LibraryBuildPhase.Probing => p.Total > 0 ? $"Library: reading headers {p.Done:N0} of {p.Total:N0}…" : "Library: reading headers…",
                LibraryBuildPhase.Indexing => $"Library: indexing {p.Total:N0} clips and meshes…",
                LibraryBuildPhase.SavingCache => "Library: saving the cache…",
                _ => ProgressText,
            };
            SetProgress(true, text, p.Fraction);
        });

        _ = RunBuildAsync();

        async Task RunBuildAsync()
        {
            try
            {
                // The tables live in the game's root archives: read them once those are indexed (they
                // are published only after the library anyway).
                TimeSpan usageTime = default;
                var usageTask = indexed.Task
                    .ContinueWith(_ => ClipUsageIndex.LoadAsync(resolver, cts.Token), cts.Token, TaskContinuationOptions.None, TaskScheduler.Default)
                    .Unwrap()
                    .ContinueWith(t =>
                    {
                        usageTime = clock.Elapsed;
                        return t;
                    }, TaskScheduler.Default).Unwrap();
                // Raised on the build's thread, ahead of the UI-thread progress: the moment the archive
                // directories are all indexed, waiting lookups may go.
                var tracking = new PhaseWatch(progress, phase =>
                {
                    if (phase > LibraryBuildPhase.Enumerating) indexed.TrySetResult();
                });
                var snapshot = await library.BuildAsync(tracking, cts.Token).ConfigureAwait(true);
                if (generation != _generation) return;
                Mark($"library built ({snapshot.Stats.ArchivesOpened:N0} archives opened, {snapshot.Stats.ArchivesFromCache:N0} from the cache, {snapshot.Stats.Probed:N0} probed; Core {snapshot.Stats.Elapsed.TotalSeconds:0.000} s)");
                if (!usageTask.IsCompleted) SetProgress(true, "Library: reading the game's tables…", null);

                // The library and the tables are published together: documents pick their preview
                // partners on LibraryChanged, and that choice prefers what the tables say.
                ClipUsageIndex usage;
                try
                {
                    usage = await usageTask.ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    // Unreadable tables cost the table features, never the library that built fine.
                    ErrorLog.Write("table load", ex);
                    usage = ClipUsageIndex.Empty;
                }
                if (generation != _generation) return;
                Mark($"tables loaded (finished at {usageTime.TotalSeconds:0.000} s)");
                Snapshot = snapshot;
                Usage = usage;
                LibraryChanged?.Invoke(this, EventArgs.Empty);
                UsageChanged?.Invoke(this, EventArgs.Empty);

                LastBuildTime = clock.Elapsed;
                var stats = snapshot.Stats;
                string summary = $"{snapshot.Clips.Length:N0} clips, {snapshot.Meshes.Length:N0} meshes"
                    + (stats.Errors > 0 ? $", {stats.Errors:N0} unreadable" : "")
                    + $" · {LastBuildTime.TotalSeconds:0.0} s";
                SetProgress(false, summary, null);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer build; it reports for itself.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Whatever stopped it, the library must not stay "loading" for the rest of the session.
                ErrorLog.Write("library build", ex);
                if (generation == _generation) SetProgress(false, "The library could not be built: " + ex.Message, null);
            }
            finally
            {
                indexed.TrySetResult();
                busy.Dispose();
            }
        }
    }

    /// <summary>Forwards progress and tells <paramref name="phase"/> about every report, on the reporting thread.</summary>
    private sealed class PhaseWatch(IProgress<LibraryProgress> inner, Action<LibraryBuildPhase> phase) : IProgress<LibraryProgress>
    {
        public void Report(LibraryProgress value)
        {
            phase(value.Phase);
            inner.Report(value);
        }
    }

    private void SetProgress(bool loading, string text, double? fraction)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => SetProgress(loading, text, fraction)));
            return;
        }
        IsLoading = loading;
        ProgressText = text;
        ProgressFraction = fraction;
        ProgressChanged?.Invoke(this, EventArgs.Empty);
    }
}
