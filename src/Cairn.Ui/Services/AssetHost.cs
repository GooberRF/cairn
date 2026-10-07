using System.Windows.Threading;
using Cairn.Assets;
using Cairn.Workspace;

namespace Cairn.Ui.Services;

/// <summary>
/// The app-wide game-data context built from the settings: one <see cref="AssetResolver"/> (search
/// folders and game directory, no document folder) with its shared <see cref="AssetCache"/>. Indexing
/// runs off the UI thread; the change events are raised on the UI thread. Documents get their own
/// resolver through <see cref="ResolverFor"/>, which shares this one's folder and archive indexes.
/// Module-specific catalogues (libraries, tables) are built by the modules on top of <see cref="Resolver"/>.
/// </summary>
public sealed class AssetHost
{
    private readonly Dispatcher _dispatcher;
    private CancellationTokenSource? _build;
    private int _generation;
    private TaskCompletionSource _indexed = CompletedSource();

    public AssetHost(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        Resolver = new AssetResolver(new AssetResolverOptions());
    }

    /// <summary>The resolver for the current settings (no document folder).</summary>
    public AssetResolver Resolver { get; private set; }

    /// <summary>The probe cache shared by <see cref="Resolver"/> and every resolver from <see cref="ResolverFor"/>.</summary>
    public AssetCache Cache => Resolver.Cache;

    /// <summary>True when there is anywhere to look at all (a game directory or a search folder).</summary>
    public bool HasSources { get; private set; }

    /// <summary>True while the archive indexes are being built.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>
    /// Completes once the current configuration's folder and archive directories are indexed, or the
    /// indexing has ended (failed or superseded). Lookups that would otherwise walk every archive wait for it.
    /// </summary>
    public Task ArchivesIndexed => _indexed.Task;

    /// <summary>Raised on the UI thread when the resolver is replaced (settings changed or refreshed).</summary>
    public event EventHandler? Changed;

    /// <summary>A resolver for a document in <paramref name="documentFolder"/>, sharing the indexes.</summary>
    public AssetResolver ResolverFor(string? documentFolder) => Resolver.WithDocumentFolder(documentFolder);

    /// <summary>
    /// Finds the game directory the way <see cref="GameDirectoryLocator"/> does (registry, common
    /// install folders). Null when nothing that looks like Red Faction is found.
    /// </summary>
    public static GameDirectoryDetection? DetectGameDirectory() => GameDirectoryLocator.DetectDetailed();

    /// <summary>True when <paramref name="directory"/> looks like a Red Faction install.</summary>
    public static bool LooksLikeGameDirectory(string? directory) => GameDirectoryLocator.LooksLikeGameDirectory(directory);

    /// <summary>
    /// Rebuilds the resolver from <paramref name="settings"/>' game directory and search folders and
    /// starts indexing in the background. Indexing already running is cancelled.
    /// </summary>
    public void Reconfigure(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _build?.Cancel();
        var options = new AssetResolverOptions
        {
            SearchFolders = [.. settings.SearchFolders.Where(f => !string.IsNullOrWhiteSpace(f))],
            GameDirectory = string.IsNullOrWhiteSpace(settings.GameDirectory) ? null : settings.GameDirectory,
        };
        HasSources = options.GameDirectory is not null || options.SearchFolders.Count > 0;
        Resolver = new AssetResolver(options);
        StartIndexing();
    }

    /// <summary>Re-reads every folder and archive, keeping the resolver's options.</summary>
    public void Refresh()
    {
        _build?.Cancel();
        Resolver.Invalidate();
        StartIndexing();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.SetResult();
        return done;
    }

    private void StartIndexing()
    {
        var cts = new CancellationTokenSource();
        _build = cts;
        int generation = ++_generation;
        var resolver = Resolver;
        var previous = _indexed;
        var indexed = _indexed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
        RaiseChanged(generation, loading: HasSources);

        if (!HasSources)
        {
            indexed.TrySetResult();
            return;
        }

        _ = RunAsync();

        async Task RunAsync()
        {
            var busy = BusyTracker.Begin("asset indexing");
            try
            {
                await resolver.PrepareAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer configuration; it reports for itself.
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Lookups still work (they index on demand); only the head start is lost.
                ErrorLog.Write("asset indexing", ex);
            }
            finally
            {
                indexed.TrySetResult();
                busy.Dispose();
                RaiseChanged(generation, loading: false);
            }
        }
    }

    private void RaiseChanged(int generation, bool loading)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => RaiseChanged(generation, loading)));
            return;
        }
        if (generation != _generation) return;
        IsLoading = loading;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
