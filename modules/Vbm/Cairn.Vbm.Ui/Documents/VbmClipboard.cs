using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Cairn.Formats.Imaging;
using Cairn.Previews;
using Cairn.Ui.Services;

namespace Cairn.Vbm.Ui.Documents;

/// <summary>What a paste can use from the clipboard.</summary>
/// <param name="Token">The marker Cairn put next to frames it copied, or null.</param>
/// <param name="Images">Images on the clipboard (a picture, or image files copied in Explorer), in order.</param>
public sealed record VbmClipboardContent(string? Token, IReadOnlyList<(string Name, BgraImage Image)> Images);

/// <summary>The clipboard as the bitmap module uses it; self-tests put a fake in its place.</summary>
public interface IVbmClipboard
{
    /// <summary>Reads the clipboard; false when it could not be read at all.</summary>
    bool TryRead(out VbmClipboardContent content);

    /// <summary>Puts <paramref name="image"/> (may be null) and the marker <paramref name="token"/> on the clipboard; false when it failed.</summary>
    bool TryWrite(string token, BgraImage? image);
}

/// <summary>Frames copied from a bitmap: the snapshot they came from and which ones.</summary>
public sealed record VbmFrameClip(string Token, VbmFile Source, IReadOnlyList<int> Indices, string SourceName);

/// <summary>The Windows clipboard, through <see cref="SystemClipboard"/>'s retries.</summary>
public sealed class SystemVbmClipboard : IVbmClipboard
{
    /// <summary>The private clipboard format holding the marker of frames copied in Cairn.</summary>
    public const string TokenFormat = "Cairn.VbmFrames";

    /// <summary>Image files a paste takes from a copied file list.</summary>
    public static readonly IReadOnlyList<string> ImageExtensions = [".tga", ".png", ".jpg", ".jpeg", ".dds", ".bmp"];

    public bool TryRead(out VbmClipboardContent content)
    {
        bool ok = SystemClipboard.TryRead(Read, out var read);
        content = read ?? new VbmClipboardContent(null, []);
        return ok;
    }

    public bool TryWrite(string token, BgraImage? image)
    {
        var data = new DataObject();
        data.SetData(TokenFormat, token);
        if (image is not null)
        {
            data.SetData("PNG", new MemoryStream(PngEncoder.Encode(image)), false);
            data.SetImage(ImageData.ToBitmap(image, false));
        }
        return SystemClipboard.TrySetData(data);
    }

    private static VbmClipboardContent Read(IDataObject? data)
    {
        if (data is null) return new(null, []);
        string? token = data.GetDataPresent(TokenFormat) ? data.GetData(TokenFormat) as string : null;
        var images = new List<(string, BgraImage)>();
        try
        {
            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                images.Add(("clipboard image", ImageDecoder.Decode(png.ToArray(), "clipboard.png")));
            else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
                images.Add(("clipboard image", FromBitmap(bitmap, opaqueWhenNoAlpha: true)));
            else if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
                foreach (string file in files.Where(f => ImageExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                    images.Add((Path.GetFileName(file), VbmImages.DecodeFile(file)));
        }
        catch (Exception ex) when (ex is ImageDecodeException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            images.Clear(); // an image that cannot be read is no image
        }
        return new(token, images);
    }

    /// <summary>
    /// A WPF bitmap as BGRA. A device-independent bitmap from the clipboard often has its alpha byte all zero (Windows
    /// keeps no alpha there): with <paramref name="opaqueWhenNoAlpha"/> such an image is made opaque.
    /// </summary>
    public static BgraImage FromBitmap(BitmapSource source, bool opaqueWhenNoAlpha)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Format != PixelFormats.Bgra32) source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var image = new BgraImage(source.PixelWidth, source.PixelHeight);
        source.CopyPixels(image.Pixels, image.Stride, 0);
        if (opaqueWhenNoAlpha)
        {
            var p = image.Pixels;
            bool any = false;
            for (int i = 3; i < p.Length && !any; i += 4) any = p[i] != 0;
            if (!any) for (int i = 3; i < p.Length; i += 4) p[i] = 255;
        }
        return image;
    }
}

/// <summary>Reading image files for frames: everything <see cref="ImageDecoder"/> reads, plus Windows bitmaps (.bmp).</summary>
public static class VbmImages
{
    /// <summary>Decodes <paramref name="path"/> (a .bmp through Windows' decoder).</summary>
    public static BgraImage DecodeFile(string path)
    {
        if (!path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)) return ImageDecoder.DecodeFile(path);
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes, writable: false);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (frame.PixelWidth > EngineFormats.MaxDimension || frame.PixelHeight > EngineFormats.MaxDimension)
                throw new ImageDecodeException($"'{Path.GetFileName(path)}' is {frame.PixelWidth} x {frame.PixelHeight}, which is out of range.");
            // 32-bit .bmp files usually leave the fourth byte at 0: treat that as opaque
            return SystemVbmClipboard.FromBitmap(frame, opaqueWhenNoAlpha: true);
        }
        catch (ImageDecodeException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException or FileFormatException)
        {
            throw new ImageDecodeException($"'{Path.GetFileName(path)}' could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>The extensions a frame can come from (the file dialogs, drops onto the strip and pasted file lists).</summary>
    public static IReadOnlyList<string> Extensions { get; } = [.. ImageProbe.ReadableExtensions.Union([".bmp"])];

    /// <summary>True when <paramref name="path"/> names an image file (or a .vbm) frames can come from.</summary>
    public static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
}
