using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Cairn.Formats;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Vpp;

namespace Cairn.Rfa.Assets;

/// <summary>
/// Index of every <c>.rfa</c>, <c>.v3c</c> and <c>.v3m</c> the resolver can see — loose files and
/// archive entries, in the resolver's search order — with their probed facts (<see cref="RfaProbe"/>,
/// <see cref="V3dProbe"/>). <see cref="BuildAsync"/> runs off the calling thread, reports progress and
/// can be cancelled; it publishes a new immutable <see cref="LibrarySnapshot"/> when it finishes, so
/// readers never see a half-built index.
/// </summary>
/// <remarks>
/// Probe results are cached in memory and, when a cache file path is given, on disk, keyed by location
/// (file path, or archive path plus entry name and offset) and stamp (last-write time and size). An
/// archive is validated by its own stamp: when it matches, its entry list and every entry's facts come
/// from the cache and the archive is not opened at all. An unreadable file becomes an entry with an
/// error, never an exception. Only one build runs at a time; a second call waits for the first.
/// </remarks>
public sealed class AssetLibrary
{
    /// <summary>The extensions the library indexes.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".rfa", ".v3c", ".v3m"];

    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private Dictionary<string, LibraryArchiveCache> _archives = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, LibraryFileCache> _files = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheFileRead;
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;

    /// <summary>Creates an empty library over <paramref name="resolver"/>; call <see cref="BuildAsync"/> to fill it.</summary>
    /// <param name="resolver">Where to look (its search order decides which copy of a name wins).</param>
    /// <param name="cacheFilePath">
    /// Optional persistent cache file, e.g. <c>%LOCALAPPDATA%\Cairn\library-cache.json</c>. Read
    /// on the first build and rewritten after any build that learned something new. Never place it in
    /// the game directory.
    /// </param>
    public AssetLibrary(AssetResolver resolver, string? cacheFilePath = null)
    {
        Resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        CacheFilePath = string.IsNullOrWhiteSpace(cacheFilePath) ? null : cacheFilePath;
    }

    /// <summary>The resolver the library indexes.</summary>
    public AssetResolver Resolver { get; }

    /// <summary>The persistent cache file, or null for a memory-only cache.</summary>
    public string? CacheFilePath { get; }

    /// <summary>The current snapshot (<see cref="LibrarySnapshot.Empty"/> before the first build). Thread-safe.</summary>
    public LibrarySnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Raised (on the build's worker thread) after a build publishes a new snapshot.</summary>
    public event EventHandler? SnapshotChanged;

    /// <summary>
    /// Rebuilds the index on a worker thread and publishes the new snapshot. Files the cache covers are
    /// not read; the rest are probed in parallel (one stream per archive). Cancellation leaves the
    /// previous snapshot in place, though probes already finished are kept in the memory cache.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public Task<LibrarySnapshot> BuildAsync(IProgress<LibraryProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            await _buildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            LibrarySnapshot snapshot;
            try
            {
                snapshot = Build(progress, cancellationToken);
                Volatile.Write(ref _snapshot, snapshot);
            }
            finally
            {
                _buildGate.Release();
            }
            // Raised outside the gate, so a handler may start another build.
            SnapshotChanged?.Invoke(this, EventArgs.Empty);
            return snapshot;
        }, cancellationToken);

    /// <summary>Forgets the memory cache (the cache file is left alone and re-read on the next build).</summary>
    public void ClearMemoryCache()
    {
        _buildGate.Wait();
        try
        {
            _archives = new(StringComparer.OrdinalIgnoreCase);
            _files = new(StringComparer.OrdinalIgnoreCase);
            _cacheFileRead = false;
        }
        finally
        {
            _buildGate.Release();
        }
    }

    // ── Build ────────────────────────────────────────────────────────────────

    private sealed class Work(AssetLocation location, int index)
    {
        public AssetLocation Location { get; } = location;
        public int Index { get; } = index;
        public LibraryProbe? Probe { get; set; }
        public bool Cached { get; set; }
    }

    private LibrarySnapshot Build(IProgress<LibraryProgress>? progress, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        bool cacheLoaded = false;
        string? cacheError = null;

        if (CacheFilePath is not null && !_cacheFileRead)
        {
            progress?.Report(new LibraryProgress(LibraryBuildPhase.LoadingCache, 0, 1, CacheFilePath));
            _cacheFileRead = true;
            if (File.Exists(CacheFilePath))
            {
                try
                {
                    var (archives, files) = LibraryCacheFile.Load(CacheFilePath);
                    // Anything already in memory is at least as fresh as the file.
                    foreach (var (k, v) in archives) _archives.TryAdd(k, v);
                    foreach (var (k, v) in files) _files.TryAdd(k, v);
                    cacheLoaded = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                    or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    cacheError = $"The library cache '{CacheFilePath}' could not be read and will be rebuilt: {ex.Message}";
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Enumerate, letting valid cached archives stand in for their directories.
        progress?.Report(new LibraryProgress(LibraryBuildPhase.Enumerating, 0, 0));
        var archiveStamps = new Dictionary<string, (DateTime Time, long Size)>(StringComparer.OrdinalIgnoreCase);
        var archivesFromCache = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<VppEntry>? CachedEntries(string vpp)
        {
            var stamp = Stamp(vpp);
            archiveStamps[vpp] = stamp;
            if (stamp.Size < 0 || !_archives.TryGetValue(vpp, out var cached) || cached.Time != stamp.Time || cached.Size != stamp.Size)
                return null;
            archivesFromCache.Add(vpp);
            // The whole directory goes to the resolver too, so texture and table lookups in this
            // archive need not read it from disk either.
            if (cached.Directory is { } directory) Resolver.SeedArchive(directory, stamp.Time, stamp.Size);
            return [.. cached.Entries.Select(e => new VppEntry(e.Name, e.Offset, e.Size))];
        }
        void ArchiveDone(int done, int total)
        {
            if (progress is not null && (done == total || done % 16 == 0))
                progress.Report(new LibraryProgress(LibraryBuildPhase.Enumerating, done, total));
        }
        var locations = Resolver.EnumerateAll(Extensions, CachedEntries, ArchiveDone, cancellationToken);
        int archivesOpened = archiveStamps.Count(p => p.Value.Size >= 0 && !archivesFromCache.Contains(p.Key));

        // 2. Take what the cache knows.
        var work = new Work[locations.Count];
        var misses = new List<Work>();
        var fileStamps = new Dictionary<string, (DateTime Time, long Size)>(StringComparer.OrdinalIgnoreCase);
        var entryLookup = new Dictionary<string, Dictionary<(string, long), LibraryProbe>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < locations.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var location = locations[i];
            var w = work[i] = new Work(location, i);
            if (location.ArchivePath is { } vpp)
            {
                if (archivesFromCache.Contains(vpp))
                {
                    if (!entryLookup.TryGetValue(vpp, out var map))
                    {
                        map = [];
                        foreach (var e in _archives[vpp].Entries) map.TryAdd((e.Name.ToUpperInvariant(), e.Offset), e);
                        entryLookup[vpp] = map;
                    }
                    if (map.TryGetValue((location.ResolvedName.ToUpperInvariant(), location.Entry!.Offset), out var probe))
                    {
                        w.Probe = probe;
                        w.Cached = true;
                        continue;
                    }
                }
            }
            else if (location.FilePath is { } path)
            {
                var stamp = Stamp(path);
                fileStamps[path] = stamp;
                if (_files.TryGetValue(path, out var cached) && cached.Time == stamp.Time && cached.Size == stamp.Size)
                {
                    w.Probe = cached.Probe;
                    w.Cached = true;
                    continue;
                }
            }
            misses.Add(w);
        }

        // 3. Probe the rest, one container (archive or loose file) per task.
        try
        {
            ProbeAll(misses, progress, cancellationToken);
        }
        finally
        {
            // Keep whatever finished, even on cancellation: it is valid and saves work next time.
            MergeIntoCache(work, archiveStamps, fileStamps, archivesFromCache);
        }

        // 4. Persist.
        bool written = false;
        bool learned = misses.Count > 0 || archivesOpened > 0;
        if (CacheFilePath is not null && (learned || !File.Exists(CacheFilePath) || cacheError is not null))
        {
            progress?.Report(new LibraryProgress(LibraryBuildPhase.SavingCache, 0, 1, CacheFilePath));
            try
            {
                LibraryCacheFile.Save(CacheFilePath, _archives, _files);
                written = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                cacheError = $"The library cache '{CacheFilePath}' could not be written: {ex.Message}";
            }
        }

        // 5. Snapshot: the first occurrence of a name in search order is the one the engine loads.
        progress?.Report(new LibraryProgress(LibraryBuildPhase.Indexing, 0, work.Length));
        var winners = new Dictionary<string, AssetLocation>(StringComparer.OrdinalIgnoreCase);
        var clips = ImmutableArray.CreateBuilder<LibraryClip>();
        var meshes = ImmutableArray.CreateBuilder<LibraryMesh>();
        int errors = 0;
        foreach (var w in work)
        {
            var location = w.Location;
            string name = location.ResolvedName;
            AssetLocation? shadowedBy = winners.TryGetValue(name, out var winner) ? winner : null;
            if (shadowedBy is null) winners[name] = location;
            var probe = w.Probe ?? new LibraryProbe(name, 0, 0, null, null, $"'{name}' was not read.");
            if (probe.Error is not null) errors++;
            if (IsClip(name)) clips.Add(new LibraryClip(name, location, probe.Clip, probe.Error, shadowedBy));
            else meshes.Add(new LibraryMesh(name, location, probe.Mesh, probe.Error, shadowedBy));
        }

        var stats = new LibraryBuildStats(work.Length, misses.Count, work.Length - misses.Count, archivesOpened,
            archivesFromCache.Count, errors, cacheLoaded, written, cacheError, clock.Elapsed);
        var snapshot = new LibrarySnapshot(clips.ToImmutable(), meshes.ToImmutable(), stats, DateTime.UtcNow);
        progress?.Report(new LibraryProgress(LibraryBuildPhase.Done, work.Length, work.Length));
        return snapshot;
    }

    private static void ProbeAll(List<Work> misses, IProgress<LibraryProgress>? progress, CancellationToken cancellationToken)
    {
        if (misses.Count == 0) return;
        var groups = misses
            .GroupBy(w => w.Location.ArchivePath ?? w.Location.FilePath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(w => w.Location.Entry?.Offset ?? 0).ToList())
            // Big archives first, so the parallel tail is short.
            .OrderByDescending(g => g.Count)
            .ToList();
        int done = 0;
        int total = misses.Count;
        progress?.Report(new LibraryProgress(LibraryBuildPhase.Probing, 0, total));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8),
        };
        Parallel.ForEach(groups, options, group =>
        {
            var first = group[0].Location;
            FileStream? stream = null;
            string? openError = null;
            try
            {
                stream = new FileStream(first.ArchivePath ?? first.FilePath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1, FileOptions.RandomAccess);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                openError = ex.Message;
            }
            using (stream)
            {
                foreach (var w in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    w.Probe = stream is null
                        ? Failed(w.Location, openError ?? "it could not be opened")
                        : ProbeOne(stream, w.Location);
                    int n = Interlocked.Increment(ref done);
                    if (progress is not null && (n == total || n % 64 == 0))
                        progress.Report(new LibraryProgress(LibraryBuildPhase.Probing, n, total, w.Location.ResolvedName));
                }
            }
        });
    }

    private static LibraryProbe ProbeOne(FileStream stream, AssetLocation location)
    {
        string name = location.ResolvedName;
        long offset = location.Entry?.Offset ?? 0;
        long size = location.Entry?.Size ?? stream.Length;
        try
        {
            if (offset + size > stream.Length)
                throw new AssetFormatException($"'{name}' runs past the end of {Path.GetFileName(location.ArchivePath ?? location.FilePath)}.");
            if (IsClip(name))
            {
                byte[] head = ReadAt(stream, offset, (int)Math.Min(size, RfaClip.BoneTableOffset));
                return new LibraryProbe(name, offset, (int)size, RfaProbe.Probe(head, name), null, null);
            }
            if (size > FileBytes.MaxAssetBytes)
                throw new AssetFormatException($"'{name}' is larger than {FileBytes.MaxAssetBytes / (1024 * 1024)} MB, which no mesh is.");
            byte[] bytes = ReadAt(stream, offset, (int)size);
            return new LibraryProbe(name, offset, (int)size, null, V3dProbe.Probe(bytes, name), null);
        }
        catch (AssetFormatException ex)
        {
            return new LibraryProbe(name, offset, (int)Math.Min(size, int.MaxValue), null, null, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failed(location, ex.Message);
        }
    }

    private static LibraryProbe Failed(AssetLocation location, string reason) =>
        new(location.ResolvedName, location.Entry?.Offset ?? 0, location.Entry?.Size ?? 0, null, null,
            $"'{location.ResolvedName}' could not be read: {reason}");

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = RandomAccess.Read(stream.SafeFileHandle, buffer.AsSpan(total), offset + total);
            if (n <= 0) break;
            total += n;
        }
        return total == count ? buffer : buffer[..total];
    }

    private void MergeIntoCache(
        Work[] work,
        Dictionary<string, (DateTime Time, long Size)> archiveStamps,
        Dictionary<string, (DateTime Time, long Size)> fileStamps,
        HashSet<string> archivesFromCache)
    {
        var archives = new Dictionary<string, LibraryArchiveCache>(_archives, StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, LibraryFileCache>(_files, StringComparer.OrdinalIgnoreCase);

        // An archive is cached only when every indexed entry it holds has a result, so a cache hit
        // never needs to open it. Archives with nothing indexed are cached too (as empty).
        var byArchive = work.Where(w => w.Location.ArchivePath is not null)
            .GroupBy(w => w.Location.ArchivePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var (vpp, stamp) in archiveStamps)
        {
            if (archivesFromCache.Contains(vpp) || stamp.Size < 0) continue;
            // The same archive can be reached from two search locations; keep each entry once.
            var entries = (byArchive.TryGetValue(vpp, out var list) ? list : [])
                .DistinctBy(w => (w.Location.ResolvedName.ToUpperInvariant(), w.Location.Entry!.Offset)).ToList();
            if (entries.Any(w => w.Probe is null)) continue;
            archives[vpp] = new LibraryArchiveCache(stamp.Time, stamp.Size, [.. entries.Select(w => w.Probe!)], Resolver.PeekArchive(vpp));
        }
        foreach (var w in work)
        {
            if (w.Cached || w.Probe is null || w.Location.FilePath is not { } path) continue;
            var stamp = fileStamps[path];
            if (stamp.Size >= 0) files[path] = new LibraryFileCache(stamp.Time, stamp.Size, w.Probe);
        }

        // Forget files that no longer exist (an archive or file not seen this build but still on disk
        // stays: a search folder switched off and on again should not cost a re-probe).
        foreach (string gone in archives.Keys.Where(k => !archiveStamps.ContainsKey(k) && !File.Exists(k)).ToList()) archives.Remove(gone);
        foreach (string gone in files.Keys.Where(k => !fileStamps.ContainsKey(k) && !File.Exists(k)).ToList()) files.Remove(gone);

        _archives = archives;
        _files = files;
    }

    private static bool IsClip(string name) => name.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase);

    private static (DateTime Time, long Size) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (DateTime.MinValue, -1);
        }
    }
}
