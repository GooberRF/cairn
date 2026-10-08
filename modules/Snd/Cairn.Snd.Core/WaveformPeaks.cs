namespace Cairn.Snd;

/// <summary>
/// Minimum and maximum sample per screen column of one channel, for drawing a waveform at any zoom. A summary of
/// 256-frame blocks is made once, so a zoomed-out view of minutes of music does not read every sample per draw.
/// </summary>
public sealed class WaveformPeaks
{
    /// <summary>Frames per summary block.</summary>
    public const int BlockFrames = 256;

    private readonly short[] _samples;
    private readonly int _channels;
    private readonly short[][] _blockMin, _blockMax;

    /// <param name="samples">Interleaved 16-bit samples.</param>
    /// <param name="channels">Channels per frame.</param>
    public WaveformPeaks(short[] samples, int channels)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        _samples = samples;
        _channels = channels;
        Frames = samples.LongLength / channels;
        long blocks = (Frames + BlockFrames - 1) / BlockFrames;
        _blockMin = new short[channels][];
        _blockMax = new short[channels][];
        for (int c = 0; c < channels; c++)
        {
            var mins = new short[blocks];
            var maxs = new short[blocks];
            for (long b = 0; b < blocks; b++)
            {
                short lo = short.MaxValue, hi = short.MinValue;
                long end = Math.Min(Frames, (b + 1) * BlockFrames);
                for (long f = b * BlockFrames; f < end; f++)
                {
                    short s = samples[f * channels + c];
                    if (s < lo) lo = s;
                    if (s > hi) hi = s;
                }
                mins[b] = lo;
                maxs[b] = hi;
            }
            _blockMin[c] = mins;
            _blockMax[c] = maxs;
        }
    }

    /// <summary>Frames per channel.</summary>
    public long Frames { get; }

    /// <summary>Channels.</summary>
    public int Channels => _channels;

    /// <summary>
    /// Fills <paramref name="min"/> and <paramref name="max"/> (each -1..1) for columns starting at frame
    /// <paramref name="firstFrame"/>, <paramref name="framesPerColumn"/> frames each. Columns past the end get 0.
    /// </summary>
    public void Compute(int channel, double firstFrame, double framesPerColumn, Span<float> min, Span<float> max)
    {
        if ((uint)channel >= (uint)_channels) throw new ArgumentOutOfRangeException(nameof(channel));
        if (framesPerColumn <= 0) throw new ArgumentOutOfRangeException(nameof(framesPerColumn));
        int columns = Math.Min(min.Length, max.Length);
        for (int x = 0; x < columns; x++)
        {
            long from = (long)Math.Floor(firstFrame + x * framesPerColumn);
            long to = Math.Max(from + 1, (long)Math.Floor(firstFrame + (x + 1) * framesPerColumn));
            from = Math.Max(0, from);
            to = Math.Min(Frames, to);
            if (from >= to) { min[x] = 0; max[x] = 0; continue; }
            int lo = short.MaxValue, hi = short.MinValue;
            if (to - from >= BlockFrames * 2)
            {
                // whole blocks inside the column from the summary, the ragged ends sample by sample
                long firstBlock = (from + BlockFrames - 1) / BlockFrames, lastBlock = to / BlockFrames;
                for (long b = firstBlock; b < lastBlock; b++)
                {
                    lo = Math.Min(lo, _blockMin[channel][b]);
                    hi = Math.Max(hi, _blockMax[channel][b]);
                }
                Scan(channel, from, firstBlock * BlockFrames, ref lo, ref hi);
                Scan(channel, lastBlock * BlockFrames, to, ref lo, ref hi);
            }
            else Scan(channel, from, to, ref lo, ref hi);
            min[x] = lo / 32768f;
            max[x] = hi / 32768f;
        }
    }

    private void Scan(int channel, long from, long to, ref int lo, ref int hi)
    {
        for (long f = from; f < to; f++)
        {
            int s = _samples[f * _channels + channel];
            if (s < lo) lo = s;
            if (s > hi) hi = s;
        }
    }
}
