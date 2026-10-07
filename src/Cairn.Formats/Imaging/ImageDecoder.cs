using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cairn.Formats.Imaging;

/// <summary>
/// Decodes any supported image into BGRA32. TGA, DDS and VBM are decoded in-house; PNG and JPEG
/// go through WPF's decoders, which is what the design calls for.
/// </summary>
public static class ImageDecoder
{
    /// <summary>Decodes a file on disk.</summary>
    /// <param name="path">Full path of the image.</param>
    /// <param name="maxOutputSize">
    /// Longest side the caller needs, or 0 for the image at its own size. A thumbnail asks for the
    /// size it will draw, so a legitimate 4096 × 4096 PNG is decoded straight to that size instead
    /// of materialising 64 MB of pixels that are then thrown away.
    /// </param>
    public static BgraImage DecodeFile(string path, int maxOutputSize = 0)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Decode(stream, Path.GetFileName(path), maxOutputSize);
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex)
        {
            throw new ImageDecodeException($"'{Path.GetFileName(path)}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>Decodes an image from a stream.</summary>
    /// <param name="stream">The image bytes.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <param name="maxOutputSize">Longest side the caller needs, or 0 for the image's own size.</param>
    public static BgraImage Decode(Stream stream, string name, int maxOutputSize = 0) =>
        Decode(StreamBytes.ReadAll(stream, name), name, maxOutputSize);

    /// <summary>Decodes an in-memory image.</summary>
    /// <param name="bytes">The image bytes.</param>
    /// <param name="name">The file name, for messages.</param>
    /// <param name="maxOutputSize">Longest side the caller needs, or 0 for the image's own size.</param>
    public static BgraImage Decode(byte[] bytes, string name, int maxOutputSize = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            return ImageProbe.Detect(bytes, name) switch
            {
                ImageContainer.Tga => TgaCodec.Decode(bytes, name),
                ImageContainer.Dds => DdsCodec.Decode(bytes, name),
                ImageContainer.Vbm => VbmCodec.Decode(bytes, name),
                ImageContainer.Png or ImageContainer.Jpeg => DecodeWithWpf(bytes, name, maxOutputSize),
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
    /// PNG/JPEG pixel decode. The bitmap is loaded eagerly and frozen so the result is safe to use
    /// from any thread.
    /// </summary>
    private static BgraImage DecodeWithWpf(byte[] bytes, string name, int maxOutputSize)
    {
        // Read the dimensions out of the file's own header first. WPF's decoder has no idea what we
        // are prepared to allocate: a 255 KB PNG can legitimately declare 16384 x 16384, and by the
        // time BitmapFrame.Create has returned it has already built the gigabyte. The header probe
        // costs nothing and refuses the file before any of that happens.
        var info = PngJpegProbe.IsPng(bytes)
            ? PngJpegProbe.ProbePng(bytes, name)
            : PngJpegProbe.ProbeJpeg(bytes, name);
        DecodeLimits.EnsureWithinBudget(info.Width, info.Height, name);

        using var stream = new MemoryStream(bytes, writable: false);
        BitmapSource source;
        if (maxOutputSize > 0 && (info.Width > maxOutputSize || info.Height > maxOutputSize))
        {
            // Let the decoder scale as it reads, so the full-size pixels are never materialised.
            var scaled = new BitmapImage();
            scaled.BeginInit();
            scaled.CacheOption = BitmapCacheOption.OnLoad;
            scaled.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            scaled.StreamSource = stream;
            if (info.Width >= info.Height) scaled.DecodePixelWidth = maxOutputSize;
            else scaled.DecodePixelHeight = maxOutputSize;
            scaled.EndInit();
            source = scaled;
        }
        else
        {
            source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
        }

        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        }
        source.Freeze();

        int width = source.PixelWidth, height = source.PixelHeight;
        if (width <= 0 || height <= 0 ||
            width > EngineFormats.MaxDimension || height > EngineFormats.MaxDimension)
        {
            throw new ImageDecodeException($"'{name}' is {width} x {height}, which is out of range.");
        }
        var image = new BgraImage(width, height);
        source.CopyPixels(image.Pixels, image.Stride, 0);
        return image;
    }
}
