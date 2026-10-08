using System.Buffers.Binary;
using Cairn.Formats.Imaging;
using Cairn.Formats.Vpp;
using Cairn.Workspace;

namespace Cairn.Vbm.Tests;

/// <summary>A .vbm's bytes and where they came from.</summary>
public sealed record VbmSample(string Packfile, string Name, byte[] Bytes);

/// <summary>Stock bitmaps from the game folder's own packfiles (none when the folder is absent) and synthetic images.</summary>
internal static class TestData
{
    private static readonly Lazy<IReadOnlyList<VbmSample>> LazyStock = new(LoadStock);

    /// <summary>Every .vbm in the packfiles directly inside the game folder (read only).</summary>
    public static IReadOnlyList<VbmSample> Stock() => LazyStock.Value;

    private static List<VbmSample> LoadStock()
    {
        var result = new List<VbmSample>();
        if (LocalPaths.GameDirectory is not { } game || !Directory.Exists(game)) return result;
        foreach (string vpp in Directory.EnumerateFiles(game, "*.vpp", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            VppArchive archive;
            try { archive = VppArchive.Open(vpp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Cairn.Formats.AssetFormatException) { continue; }
            foreach (var entry in archive.Entries)
                if (entry.Name.EndsWith(".vbm", StringComparison.OrdinalIgnoreCase))
                    result.Add(new VbmSample(Path.GetFileName(vpp), entry.Name, archive.ReadEntry(entry)));
        }
        return result;
    }

    /// <summary>A deterministic image: colour gradients with every alpha value somewhere, varied by <paramref name="seed"/>.</summary>
    public static BgraImage Gradient(int width, int height, int seed = 0, bool opaque = false)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.Set(x, y, (byte)(x * 255 / Math.Max(1, width - 1)), (byte)(y * 255 / Math.Max(1, height - 1)),
                    (byte)((x + y + seed * 37) * 13), opaque ? (byte)255 : (byte)((x * 7 + y * 31 + seed) & 0xFF));
        return image;
    }

    /// <summary>A flat image of one colour.</summary>
    public static BgraImage Flat(int width, int height, byte b, byte g, byte r, byte a = 255)
    {
        var image = new BgraImage(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.Set(x, y, b, g, r, a);
        return image;
    }

    /// <summary>A VBM file built by hand (header fields as given, then <paramref name="payload"/>).</summary>
    public static byte[] Raw(uint version, int width, int height, int format, int fps, int frames, int mipField, byte[] payload)
    {
        var bytes = new byte[32 + payload.Length];
        uint[] fields = [VbmCodec.Signature, version, (uint)width, (uint)height, (uint)format, (uint)fps, (uint)frames, (uint)mipField];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), fields[i]);
        payload.CopyTo(bytes, 32);
        return bytes;
    }

    /// <summary>Deterministic pseudo-random bytes.</summary>
    public static byte[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    /// <summary>A fresh empty folder under the temp directory.</summary>
    public static string TempFolder()
    {
        string path = Path.Combine(Path.GetTempPath(), "cairn-vbm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
