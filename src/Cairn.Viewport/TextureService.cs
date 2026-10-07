using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Assets;
using Cairn.Formats;
using Cairn.Formats.Imaging;

namespace Cairn.Viewport;

/// <summary>A decoded texture ready for a WPF material.</summary>
/// <param name="Image">Frozen BGRA bitmap (safe to use from any thread).</param>
/// <param name="HasAlpha">True when any pixel is not fully opaque (the batch is drawn after opaque ones).</param>
/// <param name="ResolvedName">The file actually used (a .tga name may resolve to a superseding .dds).</param>
/// <param name="Location">Where it came from, for the structure facts.</param>
/// <param name="Frames">Every frame of an animated VBM (frame 0 is <paramref name="Image"/>); null for a still image.</param>
/// <param name="FramesPerSecond">The animated VBM's own frame rate; 0 for a still image.</param>
public sealed record TextureImage(BitmapSource Image, bool HasAlpha, string ResolvedName, string Location,
    IReadOnlyList<BitmapSource>? Frames = null, int FramesPerSecond = 0)
{
    /// <summary>Number of frames (1 for a still image).</summary>
    public int FrameCount => Frames?.Count ?? 1;

    /// <summary>True for an animated VBM with more than one frame.</summary>
    public bool IsAnimated => FrameCount > 1;

    /// <summary>The frame <paramref name="index"/> (wrapped into range).</summary>
    public BitmapSource Frame(int index) => Frames is { Count: > 0 } f ? f[((index % f.Count) + f.Count) % f.Count] : Image;

    /// <summary>The frame shown <paramref name="seconds"/> into a looping playback at the VBM's own rate.</summary>
    public BitmapSource FrameAt(double seconds) =>
        IsAnimated && FramesPerSecond > 0 && double.IsFinite(seconds) ? Frame((int)Math.Floor(seconds * FramesPerSecond) % FrameCount) : Image;
}

/// <summary>
/// Resolves texture names through an <see cref="AssetResolver"/> and decodes them with Core's
/// <see cref="ImageDecoder"/> on a worker thread, caching by resolved location and timestamp so a
/// texture shared by several meshes (or reopened) decodes once. A missing or undecodable texture is a
/// null result, never an exception: the viewport then shows a neutral material. An animated VBM decodes every frame.
/// </summary>
public sealed class TextureService
{
    /// <summary>Largest edge decoded; RF textures are at most 512 in stock, so this only limits oddities.</summary>
    private const int MaxEdge = 1024;

    private const int MaxEntries = 400;

    /// <summary>Most frames decoded from one animated VBM; later frames are dropped.</summary>
    private const int MaxFrames = 512;

    private readonly Func<AssetResolver?>? _resolver;

    private readonly ConcurrentDictionary<string, Lazy<Task<TextureImage?>>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="resolver">The resolver the name-only <see cref="GetAsync(string, CancellationToken)"/> uses (read per call, so it can follow settings changes); null when every call passes its own.</param>
    public TextureService(Func<AssetResolver?>? resolver = null) => _resolver = resolver;

    /// <summary>Forgets every decoded texture (settings changed, files changed on disk).</summary>
    public void Clear() => _cache.Clear();

    /// <summary>
    /// What a lookup waits for before resolving (the host sets it to its archive-indexing task),
    /// so texture lookups do not race the library build through the game's archive directories.
    /// </summary>
    public Func<Task>? WaitForIndex { get; set; }

    /// <summary>Resolves <paramref name="textureName"/> through the constructor's resolver; null when there is none or the texture cannot be found or read.</summary>
    public Task<TextureImage?> GetAsync(string textureName, CancellationToken cancellationToken = default) =>
        _resolver?.Invoke() is { } resolver ? GetAsync(resolver, textureName, cancellationToken) : Task.FromResult<TextureImage?>(null);

    /// <summary>Resolves and decodes <paramref name="textureName"/>; null when it cannot be found or read.</summary>
    public Task<TextureImage?> GetAsync(AssetResolver resolver, string textureName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (string.IsNullOrWhiteSpace(textureName)) return Task.FromResult<TextureImage?>(null);
        var wait = WaitForIndex;
        return Task.Run(async () =>
        {
            if (wait is not null) await wait().ConfigureAwait(false);
            AssetLocation? location;
            try
            {
                location = resolver.Resolve(textureName, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (location is null) return null;
            var (time, size) = location.Stamp();
            string key = (location.FilePath ?? $"{location.ArchivePath}|{location.ResolvedName}") + "|" + time.Ticks + "|" + size;
            if (_cache.Count > MaxEntries) _cache.Clear();
            var lazy = _cache.GetOrAdd(key, _ => new Lazy<Task<TextureImage?>>(() => Task.Run(() => Decode(location))));
            return await lazy.Value.ConfigureAwait(false);
        }, cancellationToken);
    }

    private static TextureImage? Decode(AssetLocation location)
    {
        try
        {
            byte[] bytes = location.ReadAllBytes();
            var image = ImageDecoder.Decode(bytes, location.ResolvedName, MaxEdge);
            bool alpha = false;
            var pixels = image.Pixels;
            for (int i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] != 255)
                {
                    alpha = true;
                    break;
                }
            }
            var bitmap = ToBitmap(image);
            if (!location.ResolvedName.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase))
                return new TextureImage(bitmap, alpha, location.ResolvedName, location.DisplayLocation);

            var info = VbmCodec.ReadInfo(bytes, location.ResolvedName);
            if (!info.IsAnimated) return new TextureImage(bitmap, alpha, location.ResolvedName, location.DisplayLocation);
            int count = Math.Min(info.FrameCount, MaxFrames);
            var frames = new BitmapSource[count];
            frames[0] = bitmap;
            for (int f = 1; f < count; f++) frames[f] = ToBitmap(VbmCodec.DecodeFrame(bytes, f, location.ResolvedName));
            return new TextureImage(bitmap, alpha, location.ResolvedName, location.DisplayLocation, frames, Math.Max(0, info.Fps));
        }
        catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException
            or AssetFormatException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
