namespace Cairn.Formats;

/// <summary>Reads whole asset files into memory with a size cap, for the format readers.</summary>
public static class FileBytes
{
    /// <summary>
    /// The largest mesh or clip any reader will load. The biggest stock file is well under a
    /// megabyte; this only exists so a mislabelled multi-gigabyte file cannot be pulled into memory.
    /// </summary>
    public const int MaxAssetBytes = 64 * 1024 * 1024;

    public static byte[] ReadFile(string path, int maxBytes = MaxAssetBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        return Read(stream, Path.GetFileName(path), maxBytes);
    }

    public static byte[] Read(Stream stream, string name, int maxBytes = MaxAssetBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
            throw new AssetFormatException($"'{name}' is larger than {maxBytes / (1024 * 1024)} MB, which no mesh or clip is.");
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > maxBytes)
                throw new AssetFormatException($"'{name}' is larger than {maxBytes / (1024 * 1024)} MB, which no mesh or clip is.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Reads at most <paramref name="count"/> bytes from the start of a stream.</summary>
    public static byte[] ReadPrefix(Stream stream, int count)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var buffer = new byte[count];
        int total = 0;
        while (total < count)
        {
            int n = stream.Read(buffer, total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total == count ? buffer : buffer[..total];
    }
}
