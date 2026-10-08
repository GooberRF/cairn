namespace Cairn.Formats.Audio;

/// <summary>
/// PlayStation SPU ADPCM ("PS ADPCM", "VAG"): 16-byte frames of 28 samples. Byte 0 holds the predictor filter (high
/// nibble, 0-4) and the shift (low nibble, 0-12); byte 1 the flags (bit 0 loop end, bit 1 repeat, bit 2 loop start);
/// bytes 2-15 the 4-bit residuals, low nibble first. Each channel keeps its own two-sample history.
/// </summary>
public static class PsAdpcm
{
    /// <summary>Bytes per frame.</summary>
    public const int FrameBytes = 16;

    /// <summary>Samples per frame.</summary>
    public const int SamplesPerFrame = 28;

    /// <summary>Flag bit 0: the voice jumps to the loop address (or stops, without <see cref="FlagRepeat"/>) after this frame.</summary>
    public const int FlagLoopEnd = 1;

    /// <summary>Flag bit 1: with <see cref="FlagLoopEnd"/>, keep playing from the loop start instead of stopping.</summary>
    public const int FlagRepeat = 2;

    /// <summary>Flag bit 2: this frame is the loop start.</summary>
    public const int FlagLoopStart = 4;

    /// <summary>Flags 7 on a frame of 0x77 residuals: the end marker after a sound (never played).</summary>
    public const int EndMarker = 7;

    /// <summary>The predictor filters (coefficient pairs over 64).</summary>
    private static readonly int[,] Filters = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };

    /// <summary>The highest valid filter number.</summary>
    public const int MaxFilter = 4;

    /// <summary>The highest valid shift.</summary>
    public const int MaxShift = 12;

    /// <summary>
    /// Decodes one frame into <paramref name="output"/> (28 samples) and returns its flags. An out-of-range filter
    /// (5-15) is decoded as filter 0 and a shift of 13-15 as 9 (what the SPU does); <paramref name="outOfRange"/> says so.
    /// </summary>
    /// <param name="frame">At least 16 bytes.</param>
    /// <param name="output">At least 28 samples.</param>
    /// <param name="hist1">The previous sample (updated).</param>
    /// <param name="hist2">The sample before that (updated).</param>
    /// <param name="outOfRange">True when the frame's filter or shift was out of range.</param>
    public static int DecodeFrame(ReadOnlySpan<byte> frame, Span<short> output, ref int hist1, ref int hist2, out bool outOfRange)
    {
        if (frame.Length < FrameBytes) throw new ArgumentException("A PS ADPCM frame is 16 bytes.", nameof(frame));
        if (output.Length < SamplesPerFrame) throw new ArgumentException("A PS ADPCM frame decodes to 28 samples.", nameof(output));
        int filter = frame[0] >> 4, shift = frame[0] & 0x0F;
        outOfRange = filter > MaxFilter || shift > MaxShift;
        if (filter > MaxFilter) filter = 0;
        if (shift > MaxShift) shift = 9;
        int f0 = Filters[filter, 0], f1 = Filters[filter, 1];
        int h1 = hist1, h2 = hist2;
        for (int i = 0; i < SamplesPerFrame; i++)
        {
            int b = frame[2 + (i >> 1)];
            int nibble = (i & 1) == 0 ? b & 0x0F : b >> 4;
            int residual = (short)(nibble << 12) >> shift;
            int sample = Math.Clamp(residual + ((h1 * f0 + h2 * f1 + 32) >> 6), short.MinValue, short.MaxValue);
            output[i] = (short)sample;
            h2 = h1;
            h1 = sample;
        }
        hist1 = h1;
        hist2 = h2;
        return frame[1];
    }

    /// <summary>The filter number of a frame header byte (may be out of range).</summary>
    public static int FilterOf(byte header) => header >> 4;

    /// <summary>The shift of a frame header byte (may be out of range).</summary>
    public static int ShiftOf(byte header) => header & 0x0F;

    /// <summary>
    /// The SPU pitch (4096 = 48,000 Hz) as a sample rate: the standard rate whose pitch the console tools would have
    /// written (rate × 4096 / 48,000, truncated), else the exact rate rounded.
    /// </summary>
    public static int RateFromPitch(int pitch)
    {
        if (pitch <= 0) return 0;
        foreach (int rate in StandardRates)
        {
            if (rate * 4096L / 48000 == pitch || (int)Math.Round(rate * 4096.0 / 48000) == pitch) return rate;
        }
        return (int)Math.Round(pitch * 48000.0 / 4096);
    }

    /// <summary>The rate the console actually plays an SPU pitch at.</summary>
    public static double ExactRateFromPitch(int pitch) => pitch * 48000.0 / 4096;

    private static readonly int[] StandardRates = [8000, 11025, 12000, 16000, 22050, 24000, 32000, 44100, 48000];
}
