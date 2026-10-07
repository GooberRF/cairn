using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Assets;
using Cairn.Formats.Imaging;

namespace Cairn.Atx.Ui.Services;

/// <summary>What the preview wants composited, exactly as the engine would show it.</summary>
/// <param name="FrameName">The name written in the frame's <c>file</c> key.</param>
/// <param name="MaskName">The header's <c>alpha_mask</c>, or null when there is none.</param>
/// <param name="EffectiveFormat">
/// The format the engine will store the frame in, after the target format and any alpha-mask
/// promotion. Used both for the mask's alpha reduction and for the optional quantisation.
/// </param>
/// <param name="Simulate">True to quantise to <paramref name="EffectiveFormat"/>.</param>
/// <param name="AlphaOnly">True to show the alpha channel as greyscale instead of the colour.</param>
public sealed record CompositeRequest(
    string FrameName,
    string? MaskName,
    EngineFormat EffectiveFormat,
    bool Simulate,
    bool AlphaOnly);

/// <summary>One composited frame, or the reason there isn't one.</summary>
/// <param name="Image">The frozen bitmap, or null when the frame could not be shown.</param>
/// <param name="Error">A sentence naming the file and what went wrong, when <paramref name="Image"/> is null.</param>
public sealed record CompositeFrame(BitmapSource? Image, string? Error)
{
    /// <summary>Width in pixels, or 0.</summary>
    public int Width => Image?.PixelWidth ?? 0;

    /// <summary>Height in pixels, or 0.</summary>
    public int Height => Image?.PixelHeight ?? 0;

    /// <summary>Roughly how much memory this entry holds.</summary>
    internal long Bytes => (long)Width * Height * 4;
}

/// <summary>
/// Builds the picture the preview shows: the decoded frame, with the alpha mask applied the way
/// <c>bm_overlay_alpha_mask</c> applies it, optionally quantised to the target pixel format, and
/// optionally reduced to its alpha channel. Everything happens on a worker thread and the result is
/// a frozen <see cref="BitmapSource"/>, so the UI thread never decodes a byte.
///
/// Results are cached by where each file was found plus its timestamp and size, along with every
/// toggle that changes the pixels, so flipping "simulate target format" twice costs one decode and
/// scrubbing a 60-frame sequence decodes each frame once. The cache has a memory budget and evicts
/// the least recently used entries, so a sequence of 2048px frames cannot grow without bound.
/// </summary>
public sealed class FrameCompositor
{
    /// <summary>How much composited pixel data the cache may hold, in bytes.</summary>
    private const long MemoryBudget = 192L * 1024 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _index = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _order = new();

    /// <summary>
    /// Remembers which cache key each request produced. Building a key means resolving the name
    /// through every search location and stat-ing the file, which is real filesystem work, and
    /// <see cref="Peek"/> is called from the UI thread once per frame considered. The memo is
    /// dropped by <see cref="Invalidate"/>, which is exactly when a file may have changed.
    /// </summary>
    private readonly Dictionary<CompositeRequest, string> _keys = [];
    private long _bytes;

    private sealed record Entry(string Key, CompositeFrame Frame);

    /// <summary>Drops every cached composite; call this when an image changes on disk.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _index.Clear();
            _order.Clear();
            _keys.Clear();
            _bytes = 0;
        }
    }

    /// <summary>
    /// The cached composite for a request, or null when it has not been built yet. Safe to call
    /// from the UI thread: a request seen before answers from memory alone.
    /// </summary>
    public CompositeFrame? Peek(AssetResolver resolver, CompositeRequest request)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);
        string? key;
        lock (_gate)
        {
            if (!_keys.TryGetValue(request, out key)) key = null;
        }
        if (key is null) return null;
        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var node)) return null;
            _order.Remove(node);
            _order.AddFirst(node);
            return node.Value.Frame;
        }
    }

    /// <summary>Composites a frame off the UI thread.</summary>
    public Task<CompositeFrame> GetAsync(
        AssetResolver resolver, CompositeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(request);
        return Task.Run(() => Get(resolver, request, cancellationToken), cancellationToken);
    }

    private CompositeFrame Get(
        AssetResolver resolver, CompositeRequest request, CancellationToken cancellationToken)
    {
        AssetLocation? frameLocation;
        try
        {
            frameLocation = resolver.Resolve(request.FrameName, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CompositeFrame(null, $"'{request.FrameName}' could not be read.");
        }

        if (frameLocation is null)
        {
            return new CompositeFrame(null, $"'{request.FrameName}' was not found in any search folder.");
        }

        AssetLocation? maskLocation = null;
        if (!string.IsNullOrWhiteSpace(request.MaskName))
        {
            try { maskLocation = resolver.Resolve(request.MaskName!, cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        string key = BuildKey(frameLocation, maskLocation, request);
        lock (_gate)
        {
            // Record the mapping so a later Peek from the UI thread needs no filesystem work.
            _keys[request] = key;
            if (_index.TryGetValue(key, out var hit))
            {
                _order.Remove(hit);
                _order.AddFirst(hit);
                return hit.Value.Frame;
            }
        }

        CompositeFrame result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = Decode(frameLocation);

            if (maskLocation is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var mask = Decode(maskLocation);
                    if (mask.Width == image.Width && mask.Height == image.Height)
                    {
                        image = AlphaMask.Apply(image, mask, request.EffectiveFormat);
                    }
                    // A mismatched mask is already an error in the problems panel; the preview just
                    // shows the frame unmasked rather than showing nothing at all.
                }
                catch (ImageDecodeException) { }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (request.Simulate && EngineFormats.IsUncompressedRgb(request.EffectiveFormat))
            {
                image = FormatSimulator.Quantise(image, request.EffectiveFormat);
            }
            if (request.AlphaOnly) image = ToAlphaGrey(image);

            result = new CompositeFrame(ToBitmap(image), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (ImageTooLargeException ex)
        {
            // Nothing is wrong with the file — it is simply bigger than anything worth building a
            // preview from. Say the size, because that is the fact the designer needs.
            result = new CompositeFrame(null,
                $"'{ex.Name}' is {ex.Width} × {ex.Height}, too large to preview.");
        }
        catch (ImageDecodeException ex)
        {
            // Every decode failure already names the file it is about, so re-prefixing it here only
            // said the name twice.
            result = new CompositeFrame(null, ex.Message);
        }
        catch (Exception)
        {
            // Total by design: this runs on a fire-and-forget task, so anything a codec, the
            // quantiser or BitmapSource.Create can throw for a malformed image has to end up as a
            // visible placeholder rather than an unobserved fault or a crash.
            result = new CompositeFrame(null, $"'{frameLocation.ResolvedName}' could not be read.");
        }

        Store(key, result);
        return result;
    }

    private static BgraImage Decode(AssetLocation location)
    {
        using var stream = location.Open();
        return ImageDecoder.Decode(stream, location.ResolvedName);
    }

    /// <summary>Replaces every pixel with its own alpha as a grey level, fully opaque.</summary>
    private static BgraImage ToAlphaGrey(BgraImage source)
    {
        var result = new BgraImage(source.Width, source.Height);
        var src = source.Pixels;
        var dst = result.Pixels;
        for (int i = 0; i < src.Length; i += 4)
        {
            byte a = src[i + 3];
            dst[i] = a; dst[i + 1] = a; dst[i + 2] = a; dst[i + 3] = 255;
        }
        return result;
    }

    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bitmap = BitmapSource.Create(
            image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void Store(string key, CompositeFrame frame)
    {
        // A single frame bigger than half the budget would sit in the cache evicting everything
        // else and still be over it, so it is simply not kept — it is cheaper to redo than to
        // hold, and the preview only ever needs one of them at a time.
        if (frame.Bytes > MemoryBudget / 2) return;

        lock (_gate)
        {
            if (_index.ContainsKey(key)) return;
            var node = _order.AddFirst(new Entry(key, frame));
            _index[key] = node;
            _bytes += frame.Bytes;

            while (_bytes > MemoryBudget && _order.Count > 1)
            {
                var last = _order.Last;
                if (last is null) break;
                _order.RemoveLast();
                _index.Remove(last.Value.Key);
                _bytes -= last.Value.Frame.Bytes;
            }
        }
    }

    private static string BuildKey(AssetLocation frame, AssetLocation? mask, CompositeRequest request) =>
        string.Concat(
            Stamp(frame), "|", mask is null ? "-" : Stamp(mask), "|",
            ((int)request.EffectiveFormat).ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Simulate ? "|q" : "|-", request.AlphaOnly ? "|a" : "|-");

    /// <summary>Path, entry name, last-write time and size: the identity of a file's contents.</summary>
    private static string Stamp(AssetLocation location)
    {
        string path = location.FilePath ?? location.ArchivePath ?? location.ResolvedName;
        long ticks = 0, length = 0;
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) { ticks = info.LastWriteTimeUtc.Ticks; length = info.Length; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return $"{path}#{location.ResolvedName}#{ticks}#{length}";
    }
}
