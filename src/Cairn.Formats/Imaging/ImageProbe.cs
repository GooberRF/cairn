namespace Cairn.Formats.Imaging;

/// <summary>
/// Reads just enough of an image to say what the engine would make of it. Works from any stream,
/// so files inside a VPP archive probe exactly like loose files.
/// </summary>
public static class ImageProbe
{
    /// <summary>Probes a file on disk.</summary>
    /// <exception cref="ImageDecodeException">The file is missing, truncated or not a supported image.</exception>
    public static ImageInfo ProbeFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Probe(stream, Path.GetFileName(path));
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"'{Path.GetFileName(path)}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Probes <paramref name="stream"/>, reading only as much as the headers need.
    /// </summary>
    /// <param name="stream">The image bytes; read from the current position.</param>
    /// <param name="name">Filename used for the container guess and for error messages.</param>
    public static ImageInfo Probe(Stream stream, string name)
    {
        byte[] bytes;
        try
        {
            bytes = StreamBytes.ReadHeader(stream, name);
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"'{name}' could not be read: {ex.Message}", ex);
        }
        return Probe(bytes, name);
    }

    /// <summary>Probes an in-memory image.</summary>
    public static ImageInfo Probe(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            return Detect(bytes, name) switch
            {
                ImageContainer.Png => PngJpegProbe.ProbePng(bytes, name),
                ImageContainer.Jpeg => PngJpegProbe.ProbeJpeg(bytes, name),
                ImageContainer.Dds => DdsCodec.Probe(bytes, name),
                ImageContainer.Vbm => VbmCodec.Probe(bytes, name),
                ImageContainer.Tga => TgaCodec.Probe(bytes, name),
                _ => throw new ImageDecodeException(
                    $"'{name}' is not an image format Cairn can read."),
            };
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"'{name}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Works out the container from the file's magic bytes, falling back to its extension. TGA has
    /// no magic at the start of the file, so it is only ever the fallback.
    /// </summary>
    public static ImageContainer Detect(ReadOnlySpan<byte> bytes, string name)
    {
        if (PngJpegProbe.IsPng(bytes)) return ImageContainer.Png;
        if (PngJpegProbe.IsJpeg(bytes)) return ImageContainer.Jpeg;
        if (bytes.Length >= 4)
        {
            uint magic = (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
            if (magic == 0x20534444) return ImageContainer.Dds;
            if (magic == VbmCodec.Signature) return ImageContainer.Vbm;
        }
        return FromExtension(name);
    }

    /// <summary>
    /// Every file extension the decoders here can read, i.e. each one <see cref="FromExtension"/>
    /// maps to a known container.
    /// </summary>
    public static IReadOnlyList<string> ReadableExtensions { get; } =
        [".dds", ".png", ".jpg", ".jpeg", ".vbm", ".tga"];

    /// <summary>The container an extension implies, or <see cref="ImageContainer.Unknown"/>.</summary>
    public static ImageContainer FromExtension(string name)
    {
        string ext = Path.GetExtension(name);
        return ext.ToLowerInvariant() switch
        {
            ".tga" => ImageContainer.Tga,
            ".dds" => ImageContainer.Dds,
            ".vbm" => ImageContainer.Vbm,
            ".png" => ImageContainer.Png,
            ".jpg" or ".jpeg" => ImageContainer.Jpeg,
            _ => ImageContainer.Unknown,
        };
    }
}
