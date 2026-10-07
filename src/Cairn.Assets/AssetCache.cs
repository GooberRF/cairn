using System.Runtime.ExceptionServices;
using Cairn.Formats.Imaging;

namespace Cairn.Assets;

/// <summary>
/// Caches probe results (image headers, clip and mesh facts, anything a caller computes from an
/// asset's bytes) by (probe kind, location, last-write time, size), so re-linting or rebuilding a
/// view does not re-read every file. Failures are cached too — an unreadable file should not be
/// retried on every keystroke — and rethrown as the original exception. Safe to use from several
/// threads.
/// </summary>
public sealed class AssetCache
{
    /// <summary>
    /// How many results are remembered. Each is small, but a session across many folders would
    /// otherwise keep every name it has ever seen.
    /// </summary>
    public const int MaxEntries = 20_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(DateTime Time, long Size, object? Value, ExceptionDispatchInfo? Error);

    /// <summary>How many results are currently remembered.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Forgets every cached result.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>
    /// Returns the cached result of <paramref name="probe"/> for <paramref name="location"/>, running
    /// it if the file changed or was never seen. <paramref name="kind"/> separates different probes of
    /// the same file (e.g. "image", "rfa", "v3d").
    /// </summary>
    /// <param name="location">The asset.</param>
    /// <param name="kind">A short name for the probe.</param>
    /// <param name="probe">Reads the asset's stream; its exceptions are cached and rethrown.</param>
    public T GetOrProbe<T>(AssetLocation location, string kind, Func<Stream, string, T> probe)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(probe);
        var (time, size) = location.Stamp();
        string key = $"{kind}|{location.ArchivePath}|{location.FilePath}|{location.ResolvedName}";

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached) && cached.Time == time && cached.Size == size)
            {
                cached.Error?.Throw();
                return (T)cached.Value!;
            }
        }

        object? value = null;
        ExceptionDispatchInfo? error = null;
        try
        {
            using var stream = location.Open();
            value = probe(stream, location.ResolvedName);
        }
        catch (Exception ex)
        {
            error = ExceptionDispatchInfo.Capture(ex);
        }

        lock (_gate)
        {
            // There is no useful order to evict in, and every entry is cheap to rebuild, so starting
            // again is both correct and simpler than tracking ages.
            if (_entries.Count >= MaxEntries) _entries.Clear();
            _entries[key] = new Entry(time, size, value, error);
        }
        error?.Throw();
        return (T)value!;
    }

    /// <summary>
    /// The cached image header probe for <paramref name="location"/>.
    /// </summary>
    /// <exception cref="ImageDecodeException">The file exists but could not be read as an image.</exception>
    public ImageInfo ProbeImage(AssetLocation location)
    {
        try
        {
            return GetOrProbe(location, "image", ImageProbe.Probe);
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"'{location.ResolvedName}' could not be read: {ex.Message}", ex);
        }
    }
}
