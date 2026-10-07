namespace Cairn.Formats.Imaging;

/// <summary>The file container an image came from.</summary>
public enum ImageContainer
{
    Unknown,
    Tga,
    Dds,
    Vbm,
    Png,
    Jpeg,
}

/// <summary>
/// What the engine would make of an image file: dimensions, pixel format and mip count, derived
/// the same way <c>read_dds_header</c>, <c>read_stb_header</c> and RF's own loaders derive them.
/// </summary>
/// <param name="Container">The file type.</param>
/// <param name="Width">Width in pixels of the top mip.</param>
/// <param name="Height">Height in pixels of the top mip.</param>
/// <param name="Format">The engine pixel format this file produces.</param>
/// <param name="MipLevels">
/// Mip count when the file states it (DDS, VBM) or when it is fixed (PNG/JPEG always 1). Null for
/// TGA, where the engine derives the count from the dimensions, so two TGAs of equal size always
/// agree and there is nothing to compare.
/// </param>
/// <param name="Note">Extra detail for an inspector, e.g. the DDS FourCC.</param>
/// <param name="FrameCount">
/// How many frames the file itself holds, for a container that can hold more than one (VBM). Null
/// for every other container. A VBM with more than one frame is legal as a texture, but the
/// engine only ever uses its frame 0, which is worth saying out loud when one is being picked.
/// </param>
/// <param name="ContainerVersion">
/// The container's own version field, when it has one that changes how the file is read. Only VBM
/// does: version 1 stores 1555 alpha inverted and version 2 does not, so the version is worth
/// showing next to the file type rather than buried in a note.
/// </param>
public sealed record ImageInfo(
    ImageContainer Container,
    int Width,
    int Height,
    EngineFormat Format,
    int? MipLevels,
    string? Note = null,
    int? FrameCount = null,
    int? ContainerVersion = null)
{
    /// <summary>The file type as the UI names it: "TGA", "DDS", or "VBM v1" when the version is known.</summary>
    public string ContainerLabel => Container.ToString().ToUpperInvariant()
        + (ContainerVersion is { } v ? $" v{v}" : string.Empty);

    /// <summary>A one-line summary such as "64 x 64 · 888 RGB (24-bit, no alpha) · TGA".</summary>
    public string Describe() =>
        $"{Width} x {Height} · {EngineFormats.DisplayName(Format)} · {ContainerLabel}"
        + (MipLevels is { } m ? $" · {m} mip level{(m == 1 ? "" : "s")}" : string.Empty);
}

/// <summary>Thrown when an image file cannot be read. A missing or unreadable texture is a lint note, not an error dialog.</summary>
public class ImageDecodeException : Exception
{
    public ImageDecodeException(string message) : base(message) { }

    public ImageDecodeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Thrown when an image is within the range the engine accepts but too large for this editor to
/// turn into pixels. Its own type, so the preview and the thumbnails can say "too large to show"
/// rather than "could not be read" — the file is fine, it is just enormous.
/// </summary>
public sealed class ImageTooLargeException : ImageDecodeException
{
    /// <param name="name">The file name.</param>
    /// <param name="width">Its width in pixels.</param>
    /// <param name="height">Its height in pixels.</param>
    public ImageTooLargeException(string name, int width, int height)
        : base(DecodeLimits.TooLargeMessage(name, width, height))
    {
        Name = name;
        Width = width;
        Height = height;
    }

    /// <summary>The file name.</summary>
    public string Name { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }
}

/// <summary>A decoded top-level mip in BGRA32, the format everything downstream works in.</summary>
public sealed class BgraImage
{
    public BgraImage(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ImageDecodeException($"Image has invalid dimensions {width} x {height}.");
        if (width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
            throw new ImageDecodeException(
                $"Image is {width} x {height}, larger than the {EngineFormats.MaxDimension} pixel limit.");
        long bytes = (long)width * height * 4;
        if (bytes > int.MaxValue) throw new ImageDecodeException("Image is too large to decode.");
        Width = width;
        Height = height;
        Pixels = new byte[(int)bytes];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Row-major BGRA bytes, 4 per pixel, top row first.</summary>
    public byte[] Pixels { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;

    /// <summary>Writes one pixel. Out-of-range coordinates are ignored.</summary>
    public void Set(int x, int y, byte b, byte g, byte r, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
        int i = (y * Width + x) * 4;
        Pixels[i] = b;
        Pixels[i + 1] = g;
        Pixels[i + 2] = r;
        Pixels[i + 3] = a;
    }

    /// <summary>Reads one pixel as (B, G, R, A).</summary>
    public (byte B, byte G, byte R, byte A) Get(int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]);
    }
}
