using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Cairn.Atx.Tests;

/// <summary>Image fixtures that are easier to make with WPF than by hand.</summary>
internal static class TestImages
{
    /// <summary>A baseline JPEG of the given size, encoded by WPF.</summary>
    public static byte[] Jpeg(int width, int height)
    {
        int stride = width * 3;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = (byte)(i % 251);
            pixels[i + 1] = 64;
            pixels[i + 2] = 200;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
