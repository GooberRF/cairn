using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Assets;
using Cairn.Formats.Imaging;

namespace Cairn.Atx.Ui.Services;

/// <summary>
/// Decodes frame images off the UI thread and hands back frozen <see cref="BitmapSource"/>s.
/// Results are cached by where the file was found plus its timestamp and size, so re-linting or
/// re-ordering frames never re-reads a byte. Thumbnails are box-filtered down to the requested
/// size before the bitmap is created, so a 2048px texture never costs 16 MB of UI memory.
/// </summary>
public sealed class ThumbnailService
{
    /// <summary>
    /// How many images may be decoded at once. A 500-frame document asked for 500 thread-pool
    /// items, each holding a whole decoded image; four at a time keeps the list filling smoothly
    /// while the memory in flight stays bounded whatever the frames turn out to be.
    /// </summary>
    private const int MaxConcurrentDecodes = 4;

    /// <summary>
    /// How many results the cache keeps. Each is a small bitmap, but a session that scrolls
    /// through a few large sequences would otherwise hold every one of them for ever.
    /// </summary>
    private const int MaxCachedThumbnails = 4000;

    private readonly object _gate = new();
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.Ordinal);
    /// <summary>Cache keys in the order they were last used, oldest first.</summary>
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _decodes = new(MaxConcurrentDecodes, MaxConcurrentDecodes);

    /// <summary>Drops every cached bitmap; call this when the ATX folder changes on disk.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
            _order.Clear();
            _nodes.Clear();
        }
    }

    /// <summary>Reads a cached result, and marks it as the most recently used.</summary>
    private bool TryTake(string key, out BitmapSource? value)
    {
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out value)) return false;
            if (_nodes.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
            }
            return true;
        }
    }

    /// <summary>Stores a result, dropping the least recently used ones once the cache is full.</summary>
    private void Store(string key, BitmapSource? value)
    {
        lock (_gate)
        {
            _cache[key] = value;
            if (_nodes.TryGetValue(key, out var existing)) _order.Remove(existing);
            _nodes[key] = _order.AddLast(key);
            while (_order.Count > MaxCachedThumbnails)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value);
                _cache.Remove(oldest.Value);
            }
        }
    }

    /// <summary>
    /// Resolves and decodes <paramref name="requestedName"/>, scaled so neither side exceeds
    /// <paramref name="maxSize"/>. Returns null when the image is missing or unreadable.
    /// </summary>
    /// <param name="resolver">Where to look for the file.</param>
    /// <param name="requestedName">The name written in the .atx.</param>
    /// <param name="maxSize">Longest side in pixels, or 0 for the image's native size.</param>
    /// <param name="cancellationToken">Cancels a superseded request.</param>
    public Task<BitmapSource?> GetAsync(
        AssetResolver resolver, string? requestedName, int maxSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (string.IsNullOrWhiteSpace(requestedName)) return Task.FromResult<BitmapSource?>(null);
        return RunBoundedAsync(
            () => Get(resolver, requestedName!, maxSize, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Runs a decode with at most <see cref="MaxConcurrentDecodes"/> others, so a list of hundreds
    /// of frames does not put hundreds of decoded images in flight at once.
    /// </summary>
    private async Task<BitmapSource?> RunBoundedAsync(
        Func<BitmapSource?> work, CancellationToken cancellationToken)
    {
        await _decodes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        finally { _decodes.Release(); }
    }

    /// <summary>
    /// Decodes a loose file by full path, for pickers that show images the document's resolver has
    /// never heard of — Add Sequence lists files straight out of a folder the user just browsed to.
    /// </summary>
    /// <param name="fullPath">Full path of the image.</param>
    /// <param name="maxSize">Longest side in pixels, or 0 for the image's native size.</param>
    /// <param name="cancellationToken">Cancels a superseded request.</param>
    public Task<BitmapSource?> GetFileAsync(
        string fullPath, int maxSize, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return Task.FromResult<BitmapSource?>(null);
        return RunBoundedAsync(() => GetFile(fullPath, maxSize, cancellationToken), cancellationToken);
    }

    private BitmapSource? GetFile(string fullPath, int maxSize, CancellationToken cancellationToken)
    {
        string key;
        try
        {
            var info = new System.IO.FileInfo(fullPath);
            if (!info.Exists) return null;
            key = $"file:{fullPath}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{maxSize}";
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            return null;
        }

        if (TryTake(key, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = System.IO.File.OpenRead(fullPath);
            // maxSize is passed down so a PNG or JPEG is decoded at the size we will draw rather
            // than at its own; a legitimate 4096-square texture would otherwise cost 64 MB for a
            // 48-pixel thumbnail.
            var decoded = ImageDecoder.Decode(stream, System.IO.Path.GetFileName(fullPath), maxSize);
            cancellationToken.ThrowIfCancellationRequested();
            result = ToBitmap(maxSize > 0 ? Downscale(decoded, maxSize) : decoded);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ImageDecodeException or System.IO.IOException
            or UnauthorizedAccessException or NotSupportedException)
        {
            result = null;
        }

        Store(key, result);
        return result;
    }

    private BitmapSource? Get(
        AssetResolver resolver, string requestedName, int maxSize, CancellationToken cancellationToken)
    {
        AssetLocation? location;
        try
        {
            location = resolver.Resolve(requestedName, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (location is null) return null;

        string key = CacheKey(location, maxSize);
        if (TryTake(key, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = location.Open();
            var decoded = ImageDecoder.Decode(stream, location.ResolvedName, maxSize);
            cancellationToken.ThrowIfCancellationRequested();
            result = ToBitmap(maxSize > 0 ? Downscale(decoded, maxSize) : decoded);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Cached as a miss: an unreadable file must not be retried on every keystroke. The
            // catch is deliberately total — a malformed header can make a codec, or
            // BitmapSource.Create for a zero-sized image, throw almost anything, and every caller
            // is fire-and-forget, so an escape would either crash the app out of an async void or
            // vanish unobserved. A blank thumbnail is the right outcome for a broken image.
            result = null;
        }

        Store(key, result);
        return result;
    }

    private static string CacheKey(AssetLocation location, int maxSize)
    {
        string path = location.FilePath ?? location.ArchivePath ?? location.ResolvedName;
        long ticks = 0, length = 0;
        try
        {
            var info = new System.IO.FileInfo(path);
            if (info.Exists) { ticks = info.LastWriteTimeUtc.Ticks; length = info.Length; }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        return $"{path}|{location.ResolvedName}|{ticks}|{length}|{maxSize}";
    }

    /// <summary>
    /// Creates a frozen BGRA32 bitmap. Frozen bitmaps may be built on a worker thread and used
    /// from the UI thread, which is what keeps decoding off the dispatcher entirely.
    /// </summary>
    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Area-averages down to at most <paramref name="maxSize"/> on the longest side. Averaging (as
    /// opposed to point sampling) is what makes a 16-frame strip of small icons legible.
    /// </summary>
    private static BgraImage Downscale(BgraImage source, int maxSize)
    {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= maxSize) return source;

        double scale = (double)maxSize / longest;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var result = new BgraImage(width, height);

        var src = source.Pixels;
        var dst = result.Pixels;
        for (int y = 0; y < height; y++)
        {
            int y0 = y * source.Height / height;
            int y1 = Math.Max(y0 + 1, (y + 1) * source.Height / height);
            for (int x = 0; x < width; x++)
            {
                int x0 = x * source.Width / width;
                int x1 = Math.Max(x0 + 1, (x + 1) * source.Width / width);
                long b = 0, g = 0, r = 0, a = 0;
                int count = 0;
                for (int sy = y0; sy < y1; sy++)
                {
                    int row = sy * source.Stride;
                    for (int sx = x0; sx < x1; sx++)
                    {
                        int i = row + sx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
                        count++;
                    }
                }
                int o = (y * width + x) * 4;
                dst[o] = (byte)(b / count);
                dst[o + 1] = (byte)(g / count);
                dst[o + 2] = (byte)(r / count);
                dst[o + 3] = (byte)(a / count);
            }
        }
        return result;
    }
}
