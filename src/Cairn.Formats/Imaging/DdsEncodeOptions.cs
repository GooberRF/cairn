namespace Cairn.Formats.Imaging;

/// <summary>
/// The DDS pixel formats the converter writes: exactly those Alpine Faction loads and renders
/// (legacy header only; DXT2/DXT4, 24-bit, X8R8G8B8 and DX10-header formats are not usable in game).
/// </summary>
public enum DdsTargetFormat
{
    /// <summary>DXT1 when opaque, DXT1 with 1-bit alpha when alpha is only 0/255, DXT5 otherwise.</summary>
    Auto,
    /// <summary>DXT1 (BC1), opaque, 4 bits per pixel.</summary>
    Dxt1,
    /// <summary>DXT1 (BC1) with 1-bit (cut-out) alpha.</summary>
    Dxt1Alpha,
    /// <summary>DXT3 (BC2), explicit 4-bit alpha.</summary>
    Dxt3,
    /// <summary>DXT5 (BC3), interpolated 8-bit alpha.</summary>
    Dxt5,
    /// <summary>Uncompressed 32-bit A8R8G8B8.</summary>
    Argb8888,
    /// <summary>Uncompressed 16-bit R5G6B5 (no alpha).</summary>
    Rgb565,
    /// <summary>Uncompressed 16-bit A1R5G5B5.</summary>
    Argb1555,
    /// <summary>Uncompressed 16-bit A4R4G4B4.</summary>
    Argb4444,
}

/// <summary>How many mip levels to write.</summary>
public enum DdsMipMode
{
    /// <summary>The full chain down to 1 x 1.</summary>
    Full,
    /// <summary>Only the top level.</summary>
    None,
    /// <summary>At most <see cref="DdsEncodeOptions.MipCount"/> levels.</summary>
    Count,
}

/// <summary>The resampling filter for mip levels and power-of-two resizing.</summary>
public enum ResampleFilter
{
    Box,
    Triangle,
    Lanczos3,
}

/// <summary>Block-compression effort.</summary>
public enum DdsQuality
{
    Fast,
    Balanced,
    Best,
}

/// <summary>When to resize to a power of two.</summary>
public enum DdsResize
{
    /// <summary>Never resize (a block-compressed format then needs both sides a multiple of 4).</summary>
    Never,
    /// <summary>Only when the chosen format needs it (block compression with a side that is not a multiple of 4).</summary>
    WhenNeeded,
    /// <summary>Whenever a side is not a power of two.</summary>
    Always,
}

/// <summary>Which power of two a side is resized to.</summary>
public enum PowerOfTwoRounding
{
    Nearest,
    Larger,
    Smaller,
}

/// <summary>What the source's alpha channel holds.</summary>
public enum AlphaKind
{
    /// <summary>Every pixel is fully opaque.</summary>
    Opaque,
    /// <summary>Alpha is only 0 or 255 (cut-out).</summary>
    Binary,
    /// <summary>Alpha has intermediate values.</summary>
    Smooth,
}

/// <summary>Settings for <see cref="DdsEncoder"/>. Defaults suit game textures.</summary>
public sealed record DdsEncodeOptions
{
    public DdsTargetFormat Format { get; init; } = DdsTargetFormat.Auto;

    public DdsMipMode Mips { get; init; } = DdsMipMode.Full;

    /// <summary>The level limit for <see cref="DdsMipMode.Count"/> (at least 1).</summary>
    public int MipCount { get; init; } = 4;

    public ResampleFilter MipFilter { get; init; } = ResampleFilter.Triangle;

    public DdsQuality Quality { get; init; } = DdsQuality.Balanced;

    public DdsResize Resize { get; init; } = DdsResize.WhenNeeded;

    public PowerOfTwoRounding Rounding { get; init; } = PowerOfTwoRounding.Nearest;

    /// <summary>
    /// Multiplies colour by alpha before encoding. Off by default: the game expects straight alpha
    /// (it has no premultiplied DDS format), so this only suits textures drawn additively.
    /// </summary>
    public bool PremultiplyAlpha { get; init; }
}

/// <summary>What <see cref="DdsEncoder.EncodeDetailed"/> produced.</summary>
/// <param name="Bytes">The complete DDS file.</param>
/// <param name="Format">The engine format written.</param>
/// <param name="Target">The concrete target (never <see cref="DdsTargetFormat.Auto"/>).</param>
/// <param name="Width">Width of the top level (after any resize).</param>
/// <param name="Height">Height of the top level (after any resize).</param>
/// <param name="MipCount">Levels written.</param>
/// <param name="SourceAlpha">What the source's alpha held.</param>
/// <param name="Resized">True when the image was resized to a power of two.</param>
public sealed record DdsEncodeResult(
    byte[] Bytes,
    EngineFormat Format,
    DdsTargetFormat Target,
    int Width,
    int Height,
    int MipCount,
    AlphaKind SourceAlpha,
    bool Resized);
