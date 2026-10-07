using System.Buffers.Binary;

namespace Cairn.Rfa.Tests;

/// <summary>Synthesises tiny images in memory for the imaging and resolver tests.</summary>
internal static class TestImages
{
    /// <summary>An uncompressed 24-bit top-down TGA filled with one BGR colour.</summary>
    public static byte[] Tga24(int width, int height, byte b, byte g, byte r)
    {
        var bytes = new byte[18 + width * height * 3];
        bytes[2] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), (ushort)height);
        bytes[16] = 24;
        bytes[17] = 0x20; // top-left origin
        for (int i = 0; i < width * height; i++)
        {
            bytes[18 + i * 3] = b;
            bytes[18 + i * 3 + 1] = g;
            bytes[18 + i * 3 + 2] = r;
        }
        return bytes;
    }

    /// <summary>A one-frame version 2 VBM in 565 with every pixel set to <paramref name="pixel"/>.</summary>
    public static byte[] Vbm565(int width, int height, ushort pixel)
    {
        var bytes = new byte[32 + width * height * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x6D62762E);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 1);
        for (int i = 0; i < width * height; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32 + i * 2), pixel);
        return bytes;
    }

    /// <summary>An uncompressed 32-bit ARGB DDS with every pixel set to the given BGRA.</summary>
    public static byte[] Dds32(int width, int height, byte b, byte g, byte r, byte a)
    {
        var bytes = new byte[128 + width * height * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x20534444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x1007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76), 32);           // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 0x41);         // RGB | alpha pixels
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), 0x00FF0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), 0x0000FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100), 0x000000FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104), 0xFF000000);
        for (int i = 0; i < width * height; i++)
        {
            int o = 128 + i * 4;
            bytes[o] = b;
            bytes[o + 1] = g;
            bytes[o + 2] = r;
            bytes[o + 3] = a;
        }
        return bytes;
    }

    /// <summary>A PNG encoded by WPF, the decoder's own counterpart.</summary>
    public static byte[] Png(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            pixels[i * 4] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = a;
        }
        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }
}
