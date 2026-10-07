namespace Cairn.Formats.Imaging;

/// <summary>
/// Engine pixel formats. Values match <c>BM_FORMAT_*</c> in
/// <c>common/include/common/bitmap/formats.h</c>, which in turn match RF.exe's bm enum.
/// </summary>
public enum EngineFormat
{
    None = 0,
    /// <summary>8-bit indexed. 8-bit greyscale TGA/VBM is classified here too.</summary>
    Paletted8 = 1,
    /// <summary>8-bit alpha-only.</summary>
    Alpha8 = 2,
    Rgb565 = 3,
    Argb4444 = 4,
    Argb1555 = 5,
    Rgb888 = 6,
    Argb8888 = 7,
    Dxt1 = 0x11,
    Dxt2 = 0x12,
    Dxt3 = 0x13,
    Dxt4 = 0x14,
    Dxt5 = 0x15,
}

/// <summary>Helpers mirroring the format predicates in <c>common/bitmap/formats.h</c>.</summary>
public static class EngineFormats
{
    /// <summary>Hard upper bound for any externally-sourced texture dimension (matches <c>BM_MAX_DIMENSION</c>).</summary>
    public const int MaxDimension = 16384;

    /// <summary>The five uncompressed direct-colour formats (excludes paletted and DXT).</summary>
    public static bool IsUncompressedRgb(EngineFormat f) => f is EngineFormat.Rgb565
        or EngineFormat.Argb4444 or EngineFormat.Argb1555 or EngineFormat.Rgb888 or EngineFormat.Argb8888;

    /// <summary>True for the block-compressed DXT formats.</summary>
    public static bool IsCompressed(EngineFormat f) => f is EngineFormat.Dxt1 or EngineFormat.Dxt2
        or EngineFormat.Dxt3 or EngineFormat.Dxt4 or EngineFormat.Dxt5;

    /// <summary>True for formats the engine accepts as an alpha mask (8-bit greyscale/paletted).</summary>
    public static bool IsEightBitMask(EngineFormat f) => f is EngineFormat.Paletted8 or EngineFormat.Alpha8;

    /// <summary>True when the format carries an alpha channel.</summary>
    public static bool HasAlpha(EngineFormat f) => f is EngineFormat.Argb4444
        or EngineFormat.Argb1555 or EngineFormat.Argb8888 or EngineFormat.Alpha8
        or EngineFormat.Dxt2 or EngineFormat.Dxt3 or EngineFormat.Dxt4 or EngineFormat.Dxt5;

    /// <summary>Port of <c>bm_promote_to_alpha</c>: 565 to 4444, 888 to 8888, everything else unchanged.</summary>
    public static EngineFormat PromoteToAlpha(EngineFormat f) => f switch
    {
        EngineFormat.Rgb565 => EngineFormat.Argb4444,
        EngineFormat.Rgb888 => EngineFormat.Argb8888,
        _ => f,
    };

    /// <summary>Port of <c>bm_format_from_stb_channels</c>: 2 or 4 channels to 8888, otherwise 888.</summary>
    public static EngineFormat FromStbChannels(int channels) =>
        channels is 2 or 4 ? EngineFormat.Argb8888 : EngineFormat.Rgb888;

    /// <summary>Bytes per pixel, or 0 for block-compressed formats.</summary>
    public static int BytesPerPixel(EngineFormat f) => f switch
    {
        EngineFormat.Paletted8 or EngineFormat.Alpha8 => 1,
        EngineFormat.Rgb565 or EngineFormat.Argb4444 or EngineFormat.Argb1555 => 2,
        EngineFormat.Rgb888 => 3,
        EngineFormat.Argb8888 => 4,
        _ => 0,
    };

    /// <summary>A short label suitable for an inspector, e.g. "8888 ARGB (32-bit)".</summary>
    public static string DisplayName(EngineFormat f) => f switch
    {
        EngineFormat.None => "unknown",
        EngineFormat.Paletted8 => "8-bit paletted/greyscale",
        EngineFormat.Alpha8 => "8-bit alpha",
        EngineFormat.Rgb565 => "565 RGB (16-bit, no alpha)",
        EngineFormat.Argb4444 => "4444 ARGB (16-bit, 4-bit alpha)",
        EngineFormat.Argb1555 => "1555 ARGB (16-bit, 1-bit alpha)",
        EngineFormat.Rgb888 => "888 RGB (24-bit, no alpha)",
        EngineFormat.Argb8888 => "8888 ARGB (32-bit)",
        EngineFormat.Dxt1 => "DXT1 (compressed)",
        EngineFormat.Dxt2 => "DXT2 (compressed)",
        EngineFormat.Dxt3 => "DXT3 (compressed)",
        EngineFormat.Dxt4 => "DXT4 (compressed)",
        EngineFormat.Dxt5 => "DXT5 (compressed)",
        _ => f.ToString(),
    };
}
