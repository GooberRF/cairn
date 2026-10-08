using System.Buffers.Binary;

namespace Cairn.Formats.Audio;

/// <summary>Writes decoded sounds: 16-bit PCM WAVE, with a <c>smpl</c> chunk carrying the loop when there is one.</summary>
public static class SoundWriter
{
    /// <summary>A 16-bit PCM WAVE of <paramref name="sound"/> (loop in a <c>smpl</c> chunk when it has an enabled loop).</summary>
    public static byte[] ToWav(DecodedSound sound, bool writeLoop = true)
    {
        ArgumentNullException.ThrowIfNull(sound);
        var loop = writeLoop && sound.Loop is { Enabled: true } l && l.Length > 0 ? l : null;
        return Wav16(sound.Samples, sound.Channels, sound.SampleRate, loop);
    }

    /// <summary>
    /// A canonical 16-bit PCM WAVE ("fmt ", optional "smpl", "data"). The <c>smpl</c> chunk holds one forward loop
    /// whose end is the last frame of the loop (inclusive), as the chunk defines it.
    /// </summary>
    public static byte[] Wav16(ReadOnlySpan<short> samples, int channels, int rate, SoundLoop? loop = null)
    {
        if (channels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(channels));
        if (rate <= 0) throw new ArgumentOutOfRangeException(nameof(rate));
        long dataBytes = (long)samples.Length * 2;
        int smplBytes = loop is null ? 0 : 8 + 36 + 24;
        long total = 12 + 8 + 16 + smplBytes + 8 + dataBytes;
        if (total > int.MaxValue) throw new InvalidOperationException("The sound is too long for a WAVE file.");
        var wav = new byte[total];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], (int)(total - 8));
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        int at = 36;
        if (loop is not null)
        {
            "smpl"u8.CopyTo(s[at..]);
            BinaryPrimitives.WriteInt32LittleEndian(s[(at + 4)..], 36 + 24);
            var c = s[(at + 8)..];
            // manufacturer, product, sample period (ns), MIDI unity note, pitch fraction, SMPTE format/offset, loops, extra
            BinaryPrimitives.WriteUInt32LittleEndian(c[8..], (uint)Math.Round(1e9 / rate));
            BinaryPrimitives.WriteUInt32LittleEndian(c[12..], 60);
            BinaryPrimitives.WriteUInt32LittleEndian(c[28..], 1);
            var l = c[36..];
            // cue id, type (0 = forward), start, end (inclusive), fraction, play count (0 = forever)
            BinaryPrimitives.WriteUInt32LittleEndian(l[8..], (uint)Math.Max(0, loop.Start));
            BinaryPrimitives.WriteUInt32LittleEndian(l[12..], (uint)Math.Max(0, loop.End - 1));
            at += smplBytes;
        }
        "data"u8.CopyTo(s[at..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[(at + 4)..], (int)dataBytes);
        at += 8;
        for (int i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(s[(at + i * 2)..], samples[i]);
        return wav;
    }

    /// <summary>
    /// The first forward loop of a WAVE's <c>smpl</c> chunk (start, end exclusive, in frames), or null when it has none.
    /// </summary>
    public static (long Start, long End)? ReadSmplLoop(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8)) return null;
        int at = 12;
        while (at + 8 <= wav.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]);
            int body = at + 8;
            if (wav.Slice(at, 4).SequenceEqual("smpl"u8) && size >= 36 && body + 36 <= wav.Length)
            {
                uint loops = BinaryPrimitives.ReadUInt32LittleEndian(wav[(body + 28)..]);
                if (loops == 0 || size < 36 + 24 || body + 36 + 24 > wav.Length) return null;
                var l = wav[(body + 36)..];
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(l[8..]), end = BinaryPrimitives.ReadUInt32LittleEndian(l[12..]);
                return end >= start ? (start, (long)end + 1) : null;
            }
            long next = body + (long)size + (size & 1);
            if (next > int.MaxValue) break;
            at = (int)next;
        }
        return null;
    }
}
