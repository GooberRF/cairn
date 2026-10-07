using System.Buffers.Binary;
using Cairn.Formats;

namespace Cairn.Formats.Audio;

/// <summary>Bytes ready for a player: a PCM WAVE (".wav") or an MP3 stream (".mp3") that Windows plays itself.</summary>
public sealed record AudioPlayback(byte[] Bytes, string Extension);

/// <summary>
/// Turns the audio found in packfiles into something any player handles without extra codecs: PCM
/// WAVE for Ogg Vorbis (NVorbis), AIFF/AIFF-C (PCM and Apple IMA4 ADPCM) and ADPCM WAVE (Microsoft
/// and IMA); MP3 and MP3-in-WAVE are handed over as MP3. The type is taken from the content first and
/// the name second, since some entries are named for a different type than they hold.
/// </summary>
public static class AudioDecoding
{
    /// <summary>Largest input read into memory for decoding.</summary>
    public const int MaxInputBytes = 256 << 20;

    /// <summary>Largest decoded output (about 25 minutes of 44.1 kHz stereo).</summary>
    public const long MaxOutputBytes = 256L << 20;

    /// <summary>
    /// A PCM WAVE for the entry, or null when it is MP3 (or MP3 inside a WAVE), which Windows plays as is.
    /// </summary>
    /// <exception cref="AssetFormatException">Not audio, damaged, or an unsupported compression (the message says which).</exception>
    public static byte[]? ToWav(string name, Stream stream)
    {
        var playback = ForPlayback(name, stream);
        return playback.Extension == ".wav" ? playback.Bytes : null;
    }

    /// <summary>Bytes to play: a PCM WAVE, or MP3 bytes for MP3 content.</summary>
    /// <exception cref="AssetFormatException">Not audio, damaged, or an unsupported compression.</exception>
    public static AudioPlayback ForPlayback(string name, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(stream);
        byte[] bytes = ReadAll(stream, name);
        ReadOnlySpan<byte> b = bytes;
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8)) return FromWav(bytes, name);
        if (b.Length >= 4 && b[..4].SequenceEqual("OggS"u8)) return new AudioPlayback(FromOgg(bytes, name), ".wav");
        if (b.Length >= 12 && b[..4].SequenceEqual("FORM"u8)) return new AudioPlayback(FromAiff(bytes, name), ".wav");
        if (b.Length >= 3 && (b[..3].SequenceEqual("ID3"u8) || (b[0] == 0xFF && (b[1] & 0xE0) == 0xE0)))
        {
            return new AudioPlayback(bytes, ".mp3");
        }
        throw new AssetFormatException($"'{name}' does not contain WAVE, Ogg Vorbis, AIFF or MP3 audio.");
    }

    private static byte[] ReadAll(Stream stream, string name)
    {
        if (stream.CanSeek && stream.Length - stream.Position > MaxInputBytes)
        {
            throw new AssetFormatException($"'{name}' is too large to decode for preview.");
        }
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            memory.Write(buffer, 0, n);
            if (memory.Length > MaxInputBytes) throw new AssetFormatException($"'{name}' is too large to decode for preview.");
        }
        return memory.ToArray();
    }

    private static AudioPlayback FromWav(byte[] bytes, string name)
    {
        var layout = AudioProbe.ReadWav(bytes, name);
        var data = bytes.AsSpan((int)layout.DataOffset, (int)layout.DataLength);
        switch (layout.FormatTag)
        {
            case 1:
                return new AudioPlayback(bytes, ".wav");
            case 2:
                return new AudioPlayback(Wav16(DecodeMsAdpcm(data, layout, name), layout.Channels, layout.SampleRate), ".wav");
            case 0x11:
                return new AudioPlayback(Wav16(DecodeImaWav(data, layout, name), layout.Channels, layout.SampleRate), ".wav");
            case 0x55:
                return new AudioPlayback(data.ToArray(), ".mp3");
            default:
                throw new AssetFormatException($"'{name}' uses {layout.Compression}, which the preview cannot decode.");
        }
    }

    private static byte[] FromOgg(byte[] bytes, string name)
    {
        try
        {
            using var reader = new NVorbis.VorbisReader(new MemoryStream(bytes, writable: false), closeOnDispose: true);
            int channels = reader.Channels;
            if (channels is < 1 or > 8) throw new AssetFormatException($"'{name}' has {channels} channels.");
            var pcm = new MemoryStream();
            var floats = new float[4096 * channels];
            var shorts = new byte[floats.Length * 2];
            int read;
            while ((read = reader.ReadSamples(floats, 0, floats.Length)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(shorts.AsSpan(i * 2), (short)Math.Clamp((int)MathF.Round(floats[i] * 32767f), short.MinValue, short.MaxValue));
                }
                pcm.Write(shorts, 0, read * 2);
                if (pcm.Length > MaxOutputBytes) throw new AssetFormatException($"'{name}' is too long to decode for preview.");
            }
            return Wav16(pcm.ToArray(), channels, reader.SampleRate);
        }
        catch (Exception ex) when (ex is not (AssetFormatException or OutOfMemoryException))
        {
            throw new AssetFormatException($"'{name}' is not a readable Ogg Vorbis stream: {ex.Message}", ex);
        }
    }

    private static byte[] FromAiff(byte[] bytes, string name)
    {
        var layout = AudioProbe.ReadAiff(bytes, name, out _, out _);
        if (layout.DataOffset < 0) throw new AssetFormatException($"'{name}' has no sound data (SSND chunk).");
        if (layout.Channels is < 1 or > 8 || layout.SampleRate <= 0) throw new AssetFormatException($"'{name}' has an implausible format.");
        var data = bytes.AsSpan((int)layout.DataOffset, (int)layout.DataLength);
        if (layout.Compression.Contains("'ima4'", StringComparison.Ordinal))
        {
            // checked before allocating: the output is ~3.8x the input (64 16-bit samples per 34-byte packet per channel)
            long packets = data.Length / (34L * layout.Channels);
            if (packets * 64 * layout.Channels * 2 + 44 > MaxOutputBytes) throw new AssetFormatException($"'{name}' is too long to decode for preview.");
            return Wav16(DecodeIma4(data, layout.Channels), layout.Channels, layout.SampleRate);
        }
        if (layout.FormatTag != 1) throw new AssetFormatException($"'{name}' uses {layout.Compression}, which the preview cannot decode.");
        if (layout.BitsPerSample is < 1 or > 32) throw new AssetFormatException($"'{name}' has {layout.BitsPerSample}-bit samples.");

        // WAVE PCM is little-endian, and 8-bit WAVE is unsigned where AIFF is signed.
        int width = (layout.BitsPerSample + 7) / 8;
        int frames = data.Length / (width * layout.Channels);
        var pcm = new byte[frames * width * layout.Channels];
        for (int i = 0; i < pcm.Length; i += width)
        {
            if (width == 1) pcm[i] = (byte)(data[i] ^ 0x80);
            else if (layout.BigEndian) for (int k = 0; k < width; k++) pcm[i + k] = data[i + width - 1 - k];
            else data.Slice(i, width).CopyTo(pcm.AsSpan(i));
        }
        return Wav(pcm, layout.Channels, layout.SampleRate, width * 8);
    }

    // ---- ADPCM ----

    private static readonly int[] ImaSteps =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
        1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
    ];

    private static readonly int[] ImaIndexSteps = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

    private struct ImaState
    {
        public int Predictor;
        public int Index;

        public short Next(int nibble)
        {
            int step = ImaSteps[Index];
            int diff = step >> 3;
            if ((nibble & 1) != 0) diff += step >> 2;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 4) != 0) diff += step;
            Predictor = Math.Clamp((nibble & 8) != 0 ? Predictor - diff : Predictor + diff, short.MinValue, short.MaxValue);
            Index = Math.Clamp(Index + ImaIndexSteps[nibble], 0, 88);
            return (short)Predictor;
        }
    }

    /// <summary>Apple IMA4 (AIFF-C 'ima4'): 34-byte packets of 64 samples per channel, channels interleaved by packet.</summary>
    internal static byte[] DecodeIma4(ReadOnlySpan<byte> data, int channels)
    {
        const int PacketBytes = 34, PacketSamples = 64;
        int packets = data.Length / (PacketBytes * channels);
        var output = new byte[packets * PacketSamples * channels * 2];
        for (int p = 0; p < packets; p++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                var packet = data.Slice((p * channels + ch) * PacketBytes, PacketBytes);
                int header = BinaryPrimitives.ReadUInt16BigEndian(packet);
                var state = new ImaState { Predictor = (short)(header & 0xFF80), Index = Math.Min(header & 0x7F, 88) };
                for (int i = 0; i < PacketSamples; i++)
                {
                    int b = packet[2 + i / 2];
                    short sample = state.Next((i & 1) == 0 ? b & 0x0F : b >> 4);
                    int frame = p * PacketSamples + i;
                    BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((frame * channels + ch) * 2), sample);
                }
            }
        }
        return output;
    }

    /// <summary>IMA/DVI ADPCM in WAVE (format 0x11): per block a 4-byte header per channel, then 4-byte groups of 8 samples per channel.</summary>
    private static byte[] DecodeImaWav(ReadOnlySpan<byte> data, PcmLayout layout, string name)
    {
        int channels = layout.Channels, block = layout.BlockAlign;
        if (channels is < 1 or > 2 || block < 4 * channels) throw new AssetFormatException($"'{name}' has an implausible IMA ADPCM layout.");
        int samplesPerBlock = (block - 4 * channels) * 2 / channels + 1;
        int blocks = data.Length / block;
        long size = (long)blocks * samplesPerBlock * channels * 2;
        if (size > MaxOutputBytes) throw new AssetFormatException($"'{name}' is too long to decode for preview.");
        var output = new byte[size];
        var states = new ImaState[channels];
        for (int bl = 0; bl < blocks; bl++)
        {
            var span = data.Slice(bl * block, block);
            int frameBase = bl * samplesPerBlock;
            for (int ch = 0; ch < channels; ch++)
            {
                states[ch].Predictor = BinaryPrimitives.ReadInt16LittleEndian(span[(ch * 4)..]);
                states[ch].Index = Math.Min((int)span[ch * 4 + 2], 88);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((frameBase * channels + ch) * 2), (short)states[ch].Predictor);
            }
            int at = 4 * channels;
            int decoded = 1;
            while (at + 4 * channels <= block && decoded < samplesPerBlock)
            {
                for (int ch = 0; ch < channels; ch++)
                {
                    for (int k = 0; k < 8; k++)
                    {
                        int b = span[at + ch * 4 + k / 2];
                        short sample = states[ch].Next((k & 1) == 0 ? b & 0x0F : b >> 4);
                        int frame = frameBase + decoded + k;
                        if (decoded + k < samplesPerBlock) BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan((frame * channels + ch) * 2), sample);
                    }
                }
                at += 4 * channels;
                decoded += 8;
            }
        }
        return output;
    }

    private static readonly int[] MsAdaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];
    private static readonly int[,] MsCoefficients = { { 256, 0 }, { 512, -256 }, { 0, 0 }, { 192, 64 }, { 240, 0 }, { 460, -208 }, { 392, -232 } };

    /// <summary>Microsoft ADPCM in WAVE (format 2) with the standard seven coefficient pairs.</summary>
    private static byte[] DecodeMsAdpcm(ReadOnlySpan<byte> data, PcmLayout layout, string name)
    {
        int channels = layout.Channels, block = layout.BlockAlign;
        if (channels is < 1 or > 2 || block < 7 * channels) throw new AssetFormatException($"'{name}' has an implausible MS ADPCM layout.");
        int samplesPerBlock = (block - 7 * channels) * 2 / channels + 2;
        int blocks = data.Length / block;
        long size = (long)blocks * samplesPerBlock * channels * 2;
        if (size > MaxOutputBytes) throw new AssetFormatException($"'{name}' is too long to decode for preview.");
        var output = new byte[size];
        Span<int> coef1 = stackalloc int[2], coef2 = stackalloc int[2], delta = stackalloc int[2], s1 = stackalloc int[2], s2 = stackalloc int[2];
        int o = 0;
        for (int bl = 0; bl < blocks; bl++)
        {
            var span = data.Slice(bl * block, block);
            int at = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                int predictor = Math.Min((int)span[at++], 6);
                coef1[ch] = MsCoefficients[predictor, 0];
                coef2[ch] = MsCoefficients[predictor, 1];
            }
            for (int ch = 0; ch < channels; ch++) { delta[ch] = BinaryPrimitives.ReadInt16LittleEndian(span[at..]); at += 2; }
            for (int ch = 0; ch < channels; ch++) { s1[ch] = BinaryPrimitives.ReadInt16LittleEndian(span[at..]); at += 2; }
            for (int ch = 0; ch < channels; ch++) { s2[ch] = BinaryPrimitives.ReadInt16LittleEndian(span[at..]); at += 2; }
            for (int ch = 0; ch < channels; ch++) { BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(o), (short)s2[ch]); o += 2; }
            for (int ch = 0; ch < channels; ch++) { BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(o), (short)s1[ch]); o += 2; }
            int nibbles = (samplesPerBlock - 2) * channels;
            for (int n = 0; n < nibbles; n++)
            {
                int b = span[at + n / 2];
                int nibble = (n & 1) == 0 ? b >> 4 : b & 0x0F;
                int ch = n % channels;
                int signed = nibble >= 8 ? nibble - 16 : nibble;
                int predicted = ((s1[ch] * coef1[ch]) + (s2[ch] * coef2[ch])) >> 8;
                int sample = Math.Clamp(predicted + signed * delta[ch], short.MinValue, short.MaxValue);
                s2[ch] = s1[ch];
                s1[ch] = sample;
                delta[ch] = Math.Max(16, (MsAdaptation[nibble] * delta[ch]) >> 8);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(o), (short)sample);
                o += 2;
            }
        }
        return output;
    }

    // ---- WAVE output ----

    private static byte[] Wav16(byte[] pcm, int channels, int rate) => Wav(pcm, channels, rate, 16);

    /// <summary>Wraps little-endian PCM in a canonical 44-byte WAVE header.</summary>
    internal static byte[] Wav(byte[] pcm, int channels, int rate, int bits)
    {
        int blockAlign = channels * bits / 8;
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], (short)bits);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}
