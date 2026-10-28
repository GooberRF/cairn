using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Assets;
using Cairn.Formats.Imaging;
using Cairn.Ui.Modules;
using Cairn.Viewport;
using Cairn.Vfx.Rfe;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>Where an effect's files are: the names beside it, how to read them, and the Red Faction II install, if known.</summary>
/// <param name="Label">Where the siblings are ("Botmatch_vfx.toc_group", a folder).</param>
/// <param name="Names">The sibling names.</param>
/// <param name="Read">Reads the first bytes (int.MaxValue: all) of a sibling, or null.</param>
/// <param name="Install">The Red Faction II game folder, or null.</param>
/// <param name="Source">The full path of the folder or packfile the siblings are, or null when not known (their texture index is then not kept).</param>
public sealed record RfeSurroundings(string Label, IReadOnlyList<string> Names, Func<string, int, byte[]?> Read, string? Install, string? Source = null)
{
    /// <summary>Nothing beside the effect.</summary>
    public static RfeSurroundings None { get; } = new("", [], (_, _) => null, null);

    /// <summary>The siblings of a packfile entry (the install found through the packfile tab it belongs to, when open).</summary>
    public static RfeSurroundings For(IShellContext? shell, IAssetSiblings siblings, string? archivePath = null)
    {
        archivePath ??= shell?.Modules.OfType<IOpenArchiveSources>().SelectMany(p => p.OpenSources)
            .FirstOrDefault(s => ReferenceEquals(s.Entries, siblings) || (s.IsRedFactionII && s.Label == siblings.Label))?.ArchivePath;
        return new(siblings.Label, siblings.Names, (n, c) => c == int.MaxValue ? siblings.Read(n) : siblings.ReadHead(n, c), RfeTextures.InstallOf(archivePath), archivePath);
    }

    /// <summary>The files of the folder of <paramref name="path"/>.</summary>
    public static RfeSurroundings ForFolder(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        IReadOnlyList<string> names = [];
        try { names = [.. Directory.EnumerateFiles(folder).Select(f => Path.GetFileName(f))]; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(Path.GetFileName(folder), names, (n, c) => ReadFile(Path.Combine(folder, Path.GetFileName(n)), c), RfeTextures.InstallOf(path), folder);
    }

    private static byte[]? ReadFile(string path, int count)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[(int)Math.Min(stream.Length, count)];
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }
}

/// <summary>
/// An effect's textures, from the <c>.peg</c> packs <see cref="RfeTextures.PacksFor"/> finds (built off the UI thread on
/// first use), decoded with every frame of an animated texture. Thread-safe.
/// </summary>
public sealed class RfeTextureSource
{
    /// <summary>The frame rate animated textures play at (the packs store none).</summary>
    public const int AnimatedFps = 15;

    private readonly Lazy<PegTextureSet> _packs;
    private readonly ConcurrentDictionary<string, Task<TextureImage?>> _images = new(StringComparer.OrdinalIgnoreCase);

    public RfeTextureSource(RfeFile effect, string effectName, RfeSurroundings around)
    {
        var names = effect.Textures.Where(t => t.Length > 0).ToList();
        _packs = new Lazy<PegTextureSet>(() => RfeTextures.PacksFor(effectName, names, around.Names, around.Read,
            around.Label.Length > 0 ? around.Label : "nothing beside it", around.Install, around.Source), LazyThreadSafetyMode.ExecutionAndPublication);
        Label = around.Label;
    }

    /// <summary>Where the siblings are.</summary>
    public string Label { get; }

    /// <summary>The packs (built on first use; call off the UI thread).</summary>
    public PegTextureSet Packs => _packs.Value;

    /// <summary>Where <paramref name="name"/> is ("in barrel_vfx.peg", "not found"); call off the UI thread.</summary>
    public string Where(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "no name";
        if (Packs.Find(name) is not { } hit) return "not found";
        string frames = hit.Texture.FrameCount > 1 ? $", {hit.Texture.FrameCount} frames" : "";
        return $"{hit.Pack}{(hit.Pack.Contains('(', StringComparison.Ordinal) ? "" : $" ({Label})")}, {hit.Texture.Width} × {hit.Texture.Height}{frames}";
    }

    /// <summary>The decoded texture (every frame), or null when it is not found or cannot be decoded.</summary>
    public Task<TextureImage?> GetAsync(string name) => _images.GetOrAdd(name, n => Task.Run(() => Load(n)));

    private TextureImage? Load(string name)
    {
        using var busy = Cairn.Ui.Services.BusyTracker.Begin("texture " + name);
        var frames = Packs.DecodeFrames(name);
        if (frames is not { Count: > 0 }) return null;
        var bitmaps = frames.Select(ToBitmap).ToList();
        bool alpha = frames.Any(PegTextureSet.HasAlpha);
        string where = Packs.Find(name)?.Pack ?? Label;
        return new TextureImage(bitmaps[0], alpha, name, where, bitmaps.Count > 1 ? bitmaps : null, bitmaps.Count > 1 ? AnimatedFps : 0);
    }

    private static BitmapSource ToBitmap(BgraImage image)
    {
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32, null, image.Pixels, image.Stride);
        bitmap.Freeze();
        return bitmap;
    }
}
