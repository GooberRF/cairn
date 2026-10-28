using Cairn.Formats;

namespace Cairn.Lvl.Reading;

/// <summary>Options for <see cref="RflLevelReader"/>.</summary>
public sealed record RfReadOptions
{
    public static RfReadOptions Default { get; } = new();

    /// <summary>Keep the stock lightmap pixels (they can be many megabytes and nothing in the preview needs them).</summary>
    public bool IncludeLightmapPixels { get; init; }
}

/// <summary>Whole-file loading shared by the level and group readers.</summary>
internal static class LvlFiles
{
    /// <summary>The largest level or group file the readers load (the largest known level is about 204 MB).</summary>
    public const long MaxFileBytes = 1L << 30;

    public static byte[] ReadAll(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        string name = Path.GetFileName(path);
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxFileBytes)
            throw new AssetFormatException($"'{name}' is larger than {MaxFileBytes >> 20} MB, which no level or group is.");
        return FileBytes.Read(stream, name, (int)MaxFileBytes);
    }
}
