namespace Cairn.Formats.Imaging;

/// <summary>
/// Applies an 8-bit greyscale mask as the alpha channel of a frame, matching
/// <c>bm_overlay_alpha_mask</c>: 8888 takes the mask byte, 4444 keeps only its high nibble, and
/// 1555 thresholds it at 128.
/// </summary>
public static class AlphaMask
{
    /// <summary>
    /// Returns a copy of <paramref name="frame"/> whose alpha comes from <paramref name="mask"/>.
    /// The mask's red channel is used as the grey level, which is what an 8-bit greyscale image
    /// decodes to.
    /// </summary>
    /// <param name="frame">The decoded frame.</param>
    /// <param name="mask">The decoded mask; must be the same size as the frame.</param>
    /// <param name="effectiveFormat">
    /// The format the engine will store the result in, after <c>bm_promote_to_alpha</c>.
    /// </param>
    public static BgraImage Apply(BgraImage frame, BgraImage mask, EngineFormat effectiveFormat)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(mask);
        if (frame.Width != mask.Width || frame.Height != mask.Height)
        {
            throw new ImageDecodeException(
                $"The alpha mask is {mask.Width} x {mask.Height} but the frame is {frame.Width} x {frame.Height}.");
        }

        var result = FormatSimulator.Clone(frame);
        var dst = result.Pixels;
        var src = mask.Pixels;
        for (int i = 0; i < dst.Length; i += 4)
        {
            byte grey = src[i + 2]; // greyscale decodes to equal B/G/R; read red
            dst[i + 3] = effectiveFormat switch
            {
                EngineFormat.Argb1555 => grey >= 128 ? (byte)255 : (byte)0,
                EngineFormat.Argb4444 => (byte)((grey & 0xF0) | ((grey & 0xF0) >> 4)),
                _ => grey,
            };
        }
        return result;
    }

    /// <summary>
    /// The format the engine will actually store frames in, given the source format, an optional
    /// target <c>format</c> and whether an alpha mask is present. Mirrors the transform block of
    /// <c>parse_and_load</c>.
    /// </summary>
    public static EngineFormat EffectiveFormat(EngineFormat source, EngineFormat? target, bool hasMask)
    {
        var effective = target ?? source;
        if (hasMask) effective = EngineFormats.PromoteToAlpha(effective);
        return effective;
    }
}
