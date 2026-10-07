using System.Buffers.Binary;

namespace Cairn.Rfa.SampleGen;

/// <summary>
/// The one image encoder the sample generator needs: an uncompressed 24-bit TGA. Linked as source
/// into the test project so tests and samples write textures the same way. Deliberately not part of
/// the Core library, which only ever decodes images.
/// </summary>
public static class TinyTgaWriter
{
    /// <summary>
    /// Uncompressed 24-bit true-colour TGA (image type 2), stored bottom-up (descriptor 0, the
    /// classic origin every TGA reader accepts) with no ID field, colour map or footer.
    /// </summary>
    /// <param name="width">Width in pixels (1..65535).</param>
    /// <param name="height">Height in pixels (1..65535).</param>
    /// <param name="pixels">Row-major BGR triples, top row first.</param>
    public static byte[] Tga24(int width, int height, byte[] pixels)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        if (width is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(width));
        if (height is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(height));
        int stride = width * 3;
        if (pixels.Length != stride * height)
            throw new ArgumentException($"Expected {stride * height} bytes of BGR pixels, got {pixels.Length}.", nameof(pixels));

        var file = new byte[18 + pixels.Length];
        file[2] = 2;                                                        // uncompressed true colour
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), (ushort)height);
        file[16] = 24;                                                      // bits per pixel
        file[17] = 0;                                                       // bottom-up, no alpha bits
        for (int y = 0; y < height; y++)
        {
            // File row 0 is the bottom of the image.
            Array.Copy(pixels, (height - 1 - y) * stride, file, 18 + y * stride, stride);
        }
        return file;
    }
}
