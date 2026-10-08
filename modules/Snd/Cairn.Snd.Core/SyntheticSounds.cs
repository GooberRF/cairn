using System.Buffers.Binary;
using Cairn.Formats.Audio;

namespace Cairn.Snd;

/// <summary>
/// Sounds built in code for tests and self-tests (no game data): PS2 .vse (both headers) and .vmu files from a simple
/// PS ADPCM encoder, and a big-endian AIFF. Cairn never writes PS2 sounds otherwise.
/// </summary>
public static class SyntheticSounds
{
    public const int Pitch22k = 0x0759, Pitch11k = 0x03AC, Pitch44k = 0x0EB3;

    /// <summary>A sine of <paramref name="frames"/> ADPCM frames (28 samples each).</summary>
    public static short[] Sine(int frames, double period = 50, double amplitude = 12000) =>
        [.. Enumerable.Range(0, frames * PsAdpcm.SamplesPerFrame).Select(i => (short)Math.Round(amplitude * Math.Sin(2 * Math.PI * i / period)))];

    /// <summary>One raw frame: filter, shift, flags and 28 nibbles (low nibble first).</summary>
    public static byte[] Frame(int filter, int shift, int flags, IReadOnlyList<int> nibbles)
    {
        var f = new byte[PsAdpcm.FrameBytes];
        f[0] = (byte)((filter << 4) | shift);
        f[1] = (byte)flags;
        for (int i = 0; i < PsAdpcm.SamplesPerFrame; i++) f[2 + i / 2] |= (byte)((nibbles[i] & 0xF) << ((i & 1) * 4));
        return f;
    }

    /// <summary>The end-marker frame (flags 7, residuals 0x77) that follows a one-shot sound.</summary>
    public static byte[] EndMarker()
    {
        var f = Enumerable.Repeat((byte)0x77, PsAdpcm.FrameBytes).ToArray();
        f[0] = 0;
        f[1] = PsAdpcm.EndMarker;
        return f;
    }

    /// <summary>
    /// A simple PS ADPCM encoder: per frame, every filter and shift, keeping the one with the least error (the decoder's
    /// arithmetic is followed exactly). <paramref name="flags"/> gives each frame's flags.
    /// </summary>
    public static byte[] Encode(short[] pcm, Func<int, int>? flags = null)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        int[,] filters = { { 0, 0 }, { 60, 0 }, { 115, -52 }, { 98, -55 }, { 122, -60 } };
        int frames = pcm.Length / PsAdpcm.SamplesPerFrame;
        var output = new byte[frames * PsAdpcm.FrameBytes];
        int h1 = 0, h2 = 0;
        var nibbles = new int[PsAdpcm.SamplesPerFrame];
        for (int fr = 0; fr < frames; fr++)
        {
            long bestError = long.MaxValue;
            byte[]? best = null;
            int bestH1 = 0, bestH2 = 0;
            for (int filter = 0; filter < 5; filter++)
            {
                for (int shift = 0; shift <= 12; shift++)
                {
                    int a = h1, b = h2;
                    long error = 0;
                    for (int i = 0; i < PsAdpcm.SamplesPerFrame; i++)
                    {
                        int predicted = (a * filters[filter, 0] + b * filters[filter, 1] + 32) >> 6;
                        int target = pcm[fr * PsAdpcm.SamplesPerFrame + i];
                        int n = Math.Clamp((int)Math.Round((target - predicted) * (double)(1 << shift) / 4096), -8, 7);
                        nibbles[i] = n & 0xF;
                        int value = Math.Clamp(((short)(n << 12) >> shift) + predicted, short.MinValue, short.MaxValue);
                        error += (long)(value - target) * (value - target);
                        b = a;
                        a = value;
                    }
                    if (error < bestError)
                    {
                        bestError = error;
                        best = Frame(filter, shift, flags?.Invoke(fr) ?? 0, nibbles);
                        bestH1 = a;
                        bestH2 = b;
                    }
                }
            }
            best!.CopyTo(output, fr * PsAdpcm.FrameBytes);
            h1 = bestH1;
            h2 = bestH2;
        }
        return output;
    }

    /// <summary>A one-shot .vse: the sine, an end flag on its last frame, then the end marker.</summary>
    public static byte[] OneShotVse(int frames = 400, int pitch = Pitch22k, double period = 50)
    {
        var data = Encode(Sine(frames, period), f => f == frames - 1 ? PsAdpcm.FlagLoopEnd : 0);
        return Vse([.. data, .. EndMarker()], pitch);
    }

    /// <summary>A looping .vse: loop start on the first frame, loop end + repeat on the last.</summary>
    public static byte[] LoopingVse(int frames = 300, int pitch = Pitch11k) =>
        Vse(Encode(Sine(frames, 40), f => f == 0 ? 6 : f == frames - 1 ? 3 : 2), pitch, looping: true);

    /// <summary>A .vse with the current 24-byte header around <paramref name="data"/>.</summary>
    public static byte[] Vse(byte[] data, int pitch = Pitch22k, bool looping = false, int? ms = null, uint envelope = 0x000F0003, int? declaredSize = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        var b = new byte[24 + data.Length];
        int length = ms ?? (int)Math.Round(data.Length / PsAdpcm.FrameBytes * PsAdpcm.SamplesPerFrame * 1000.0 / PsAdpcm.RateFromPitch(pitch));
        BinaryPrimitives.WriteUInt32LittleEndian(b, looping ? 1u : (uint)length + 150);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), looping ? 0u : (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), envelope);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), (uint)(declaredSize ?? data.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), (ushort)pitch);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), (ushort)(looping ? 2 : 0));
        data.CopyTo(b, 24);
        return b;
    }

    /// <summary>A .vse with the older 12-byte header (duration, data size, pitch).</summary>
    public static byte[] VseOld(byte[] data, int ms, int pitch = Pitch22k)
    {
        ArgumentNullException.ThrowIfNull(data);
        var b = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)ms);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), (uint)pitch);
        data.CopyTo(b, 12);
        return b;
    }

    /// <summary>A .vmu of two channels' frames (equal length): 16 KB per channel per block, the last block halved.</summary>
    public static byte[] Vmu(byte[] left, byte[] right, bool looping = false, int pitch = Pitch44k)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Length != right.Length) throw new ArgumentException("The channels must be equally long.", nameof(right));
        const int Block = Ps2Sound.VmuInterleave;
        int full = left.Length / Block, rest = left.Length - full * Block;
        var b = new byte[12 + left.Length * 2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(full + (rest > 0 ? 1 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)(pitch | (looping ? 0x8000 : 0)));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 371);
        int at = 12;
        for (int k = 0; k < full; k++)
        {
            left.AsSpan(k * Block, Block).CopyTo(b.AsSpan(at)); at += Block;
            right.AsSpan(k * Block, Block).CopyTo(b.AsSpan(at)); at += Block;
        }
        left.AsSpan(full * Block, rest).CopyTo(b.AsSpan(at)); at += rest;
        right.AsSpan(full * Block, rest).CopyTo(b.AsSpan(at));
        return b;
    }

    /// <summary>A looping stereo .vmu of <paramref name="frames"/> frames per channel (two different tones).</summary>
    public static byte[] Music(int frames = 2000) =>
        Vmu(Encode(Sine(frames, 100, 9000), _ => 2), Encode(Sine(frames, 63, 7000), _ => 2), looping: true);

    /// <summary>A 16-bit big-endian AIFF of interleaved samples.</summary>
    public static byte[] Aiff(short[] samples, int channels, int rate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int frames = samples.Length / channels, data = samples.Length * 2;
        var b = new byte[12 + 26 + 16 + data];
        "FORM"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), b.Length - 8);
        "AIFF"u8.CopyTo(b.AsSpan(8));
        "COMM"u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(16), 18);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(20), (short)channels);
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(22), frames);
        BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(26), 16);
        // 80-bit extended rate
        int exponent = 16383 + 31 - System.Numerics.BitOperations.LeadingZeroCount((uint)rate);
        ulong mantissa = (ulong)rate << (63 - (exponent - 16383));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(28), (ushort)exponent);
        BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(30), mantissa);
        "SSND"u8.CopyTo(b.AsSpan(38));
        BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(42), 8 + data);
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16BigEndian(b.AsSpan(54 + i * 2), samples[i]);
        return b;
    }

    /// <summary>A 24-bit PCM WAVE of interleaved samples (each 16-bit sample widened), with a LIST chunk after the data.</summary>
    public static byte[] Wav24(short[] samples, int channels, int rate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        int data = samples.Length * 3;
        byte[] list = [.. "LIST"u8, 12, 0, 0, 0, .. "INFOISFT"u8, 0, 0, 0, 0];
        var b = new byte[12 + 24 + 8 + data + (data & 1) + list.Length];
        "RIFF"u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), b.Length - 8);
        "WAVE"u8.CopyTo(b.AsSpan(8));
        "fmt "u8.CopyTo(b.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(22), (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), rate * channels * 3);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(32), (short)(channels * 3));
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(34), 24);
        "data"u8.CopyTo(b.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(40), data);
        for (int i = 0; i < samples.Length; i++)
        {
            int at = 44 + i * 3;
            b[at] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(at + 1), samples[i]);
        }
        list.CopyTo(b.AsSpan(44 + data + (data & 1)));
        return b;
    }
}
