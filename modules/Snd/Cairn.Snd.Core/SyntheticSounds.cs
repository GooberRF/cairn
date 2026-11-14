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

    /// <summary>The rows of <see cref="Xm"/>'s pattern, and its speed (ticks per row) and tempo (BPM).</summary>
    public const int XmRows = 16, XmSpeed = 6, XmTempo = 125;

    /// <summary>How long <see cref="Xm"/>'s song plays once: 16 rows of 6 ticks of 2.5 / 125 s (1.92 s).</summary>
    public static TimeSpan XmLength => TimeSpan.FromSeconds(XmRows * XmSpeed * 2.5 / XmTempo);

    /// <summary>
    /// A minimal FastTracker 2 module (version 1.04): one 16-row pattern on two channels playing C-4, E-4, G-4 and C-5
    /// (with a fifth below on the second channel), and one instrument with one 32-sample looping 8-bit sine (delta-coded,
    /// as XM stores samples).
    /// </summary>
    public static byte[] Xm(string title = "Cairn test song")
    {
        const int Channels = 2;
        var b = new List<byte>();
        void Text(string s, int length) { var t = new byte[length]; System.Text.Encoding.ASCII.GetBytes(s.AsSpan(0, Math.Min(s.Length, length)), t); b.AddRange(t); }
        void U16(int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        void U32(int v) { U16(v & 0xFFFF); U16(v >> 16); }

        b.AddRange("Extended Module: "u8.ToArray());
        Text(title, 20);
        b.Add(0x1A);
        Text("Cairn tests", 20);
        U16(0x0104);
        U32(276);           // header size from here
        U16(1);             // song length (orders)
        U16(0);             // restart position
        U16(Channels);
        U16(1);             // patterns
        U16(1);             // instruments
        U16(1);             // flags: linear frequencies
        U16(XmSpeed);
        U16(XmTempo);
        b.AddRange(new byte[256]); // order table: pattern 0

        // the pattern: unpacked cells (note, instrument, volume, effect, parameter); 0x80 is an empty packed cell
        var cells = new List<byte>();
        int[] melody = [49, 53, 56, 61]; // C-4, E-4, G-4, C-5
        for (int row = 0; row < XmRows; row++)
            for (int ch = 0; ch < Channels; ch++)
            {
                if (row % 4 == 0) cells.AddRange(new byte[] { (byte)(melody[row / 4] - (ch == 1 ? 7 : 0)), 1, 0, 0, 0 });
                else cells.Add(0x80);
            }
        U32(9);             // pattern header size
        b.Add(0);           // packing type
        U16(XmRows);
        U16(cells.Count);
        b.AddRange(cells);

        // the instrument, with one sample
        const int SampleLength = 32;
        U32(263);
        Text("sine", 22);
        b.Add(0);
        U16(1);             // samples
        U32(40);            // sample header size
        b.AddRange(new byte[96 + 48 + 48]); // every note plays sample 0; no envelopes
        b.AddRange(new byte[2 + 6 + 2 + 4]); // envelope point counts, sustain and loops, types, vibrato
        U16(0);             // fadeout
        b.AddRange(new byte[22]);
        U32(SampleLength);
        U32(0);             // loop start
        U32(SampleLength);  // loop length
        b.Add(48);          // volume
        b.Add(0);           // finetune
        b.Add(1);           // forward loop, 8-bit
        b.Add(128);         // panning
        b.Add(0);           // relative note
        b.Add(0);
        Text("sine", 22);
        sbyte previous = 0;
        for (int i = 0; i < SampleLength; i++)
        {
            var value = (sbyte)Math.Round(100 * Math.Sin(2 * Math.PI * i / SampleLength));
            b.Add((byte)(value - previous));
            previous = value;
        }
        return [.. b];
    }
}
