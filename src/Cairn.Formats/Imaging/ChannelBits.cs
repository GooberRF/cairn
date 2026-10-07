namespace Cairn.Formats.Imaging;

/// <summary>
/// How a colour channel narrower than eight bits is turned into a byte, and back again.
///
/// <para>
/// The expansion is <b>bit replication</b>: the stored bits are shifted up and the top of the value
/// is repeated into the bits that were vacated, which is what Direct3D and every RF-era loader do.
/// It matters here because it is the only expansion that survives a round trip. Truncating a
/// replicated byte back to the same width always gives the original value —
/// <c>Quantise(Expand(v, n), n) == v</c> for every <c>v</c> and every width — so a 16-bit VBM pixel
/// widened to 8888, written to a TGA and converted back to 1555 / 4444 / 565 by the game is exactly
/// the pixel the VBM held. Scaling by <c>v * 255 / max</c> looks equivalent and is not: a 5-bit 17
/// becomes 139, which truncates back to 17 but displays a step away from the 140 the engine shows.
/// </para>
/// </summary>
public static class ChannelBits
{
    /// <summary>
    /// Widens a <paramref name="bits"/>-bit channel value to a full byte by bit replication.
    /// </summary>
    /// <param name="value">The stored value; bits above <paramref name="bits"/> are ignored.</param>
    /// <param name="bits">Channel width, 1 to 8.</param>
    public static byte Expand(int value, int bits)
    {
        if (bits is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(bits));
        int keep = value & ((1 << bits) - 1);
        int result = 0;
        // Repeat the pattern up the byte: 5 bits give bbbbb|bbb, 6 give bbbbbb|bb, 3 give bbb|bbb|bb.
        for (int filled = 0; filled < 8; filled += bits)
        {
            result = (result << Math.Min(bits, 8 - filled)) | (keep >> Math.Max(0, bits - (8 - filled)));
        }
        return (byte)result;
    }

    /// <summary>
    /// Narrows a byte to <paramref name="bits"/> bits the way the engine's format conversion does:
    /// by keeping the top bits and discarding the rest.
    /// </summary>
    /// <param name="value">The byte to narrow.</param>
    /// <param name="bits">Channel width, 1 to 8.</param>
    public static int Quantise(byte value, int bits)
    {
        if (bits is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(bits));
        return value >> (8 - bits);
    }

    /// <summary>Narrows and widens again, i.e. the byte as it would look after the conversion.</summary>
    /// <param name="value">The byte to reduce.</param>
    /// <param name="bits">Channel width, 1 to 8.</param>
    public static byte Reduce(byte value, int bits) => Expand(Quantise(value, bits), bits);
}
