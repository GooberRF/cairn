using System.Buffers.Binary;
using System.Globalization;

namespace Cairn.Formats.Audio;

/// <summary>
/// Decodes any sound Cairn opens to 16-bit PCM with its facts: PlayStation 2 <c>.vse</c>/<c>.vmu</c>
/// (<see cref="Ps2Sound"/>), WAVE (PCM, Microsoft and IMA ADPCM), Ogg Vorbis and AIFF/AIFF-C (through
/// <see cref="AudioDecoding"/>). Loop points come from a WAVE's <c>smpl</c> chunk and an Ogg's LOOPSTART/LOOPLENGTH
/// (or LOOPEND) comments.
/// </summary>
public static class SoundDecoder
{
    /// <summary>The extensions the sounds module opens.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".vse", ".vmu", ".wav", ".ogg", ".aif", ".aiff", ".aifc"];

    /// <summary>True when <see cref="Decode"/> takes files named like <paramref name="name"/>.</summary>
    public static bool CanDecode(string name) => Extensions.Contains(Path.GetExtension(name ?? string.Empty).ToLowerInvariant());

    /// <summary>Decodes <paramref name="bytes"/>.</summary>
    /// <exception cref="AssetFormatException">
    /// Not a sound Cairn decodes (the message says why). Damaged data never surfaces as another exception: a decoder's
    /// unexpected failure is reported as this, so a tab or a batch can list the file as unreadable and go on.
    /// </exception>
    public static DecodedSound Decode(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        try
        {
            return DecodeCore(bytes, name);
        }
        catch (Exception ex) when (ex is not (AssetFormatException or OutOfMemoryException or OperationCanceledException))
        {
            throw new AssetFormatException($"'{name}' could not be decoded: {ex.Message}", ex);
        }
    }

    private static DecodedSound DecodeCore(byte[] bytes, string name)
    {
        ReadOnlySpan<byte> b = bytes;
        bool riff = b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WAVE"u8);
        bool ogg = b.Length >= 4 && b[..4].SequenceEqual("OggS"u8);
        bool aiff = b.Length >= 12 && b[..4].SequenceEqual("FORM"u8);
        if (!riff && !ogg && !aiff && Ps2Sound.IsPs2SoundName(name)) return Ps2Sound.Decode(bytes, name);
        if (bytes.Length == 0) throw new AssetFormatException($"'{name}' is empty.");

        AudioInfo info = riff ? AudioProbe.ProbeWav(bytes, name)
            : ogg ? ProbeOgg(bytes, name)
            : aiff ? AudioProbe.ProbeAiff(bytes, name)
            : throw new AssetFormatException($"'{name}' does not contain WAVE, Ogg Vorbis or AIFF audio.");
        AudioPlayback playback;
        using (var input = new MemoryStream(bytes, writable: false)) playback = AudioDecoding.ForPlayback(name, input);
        if (playback.Extension != ".wav") throw new AssetFormatException($"'{name}' holds MP3 audio, which Cairn plays in packfile previews but does not decode.");

        var (samples, channels, rate, bits) = ReadPcm(playback.Bytes, name);
        var problems = new List<SoundProblem>();
        var details = new List<(string, string)> { ("Container", info.Container), ("Codec", info.Codec) };
        var quirks = new List<string>();
        long frames = samples.LongLength / channels;
        SoundLoop? loop = null;
        if (riff)
        {
            if (SoundWriter.ReadSmplLoop(bytes) is { } l)
            {
                loop = ClampLoop(l.Start, l.End, frames, "'smpl' chunk", problems);
                if (loop is not null) quirks.Add("The loop comes from the file's 'smpl' chunk; many players ignore it.");
            }
            if (info.Codec != "PCM") quirks.Add($"{info.Codec} is lossy: the decoded sound is what a player hears.");
        }
        else if (ogg)
        {
            if (OggLoop(bytes) is { } l)
            {
                loop = ClampLoop(l.Start, l.End, frames, "LOOPSTART/LOOPLENGTH comments", problems);
                if (loop is not null) quirks.Add("The loop comes from LOOPSTART/LOOPLENGTH comments, which the game does not read.");
            }
            quirks.Add("Vorbis is lossy: the decoded sound is what a player hears.");
            if (info.Note is { } vendor) details.Add(("Encoder", vendor));
        }
        else
        {
            quirks.Add("AIFF is big-endian Apple audio; the game does not load it.");
            if (info.Codec.Contains("ima4", StringComparison.Ordinal)) quirks.Add("IMA4 ADPCM is lossy: the decoded sound is what a player hears.");
        }
        if (bits is > 16) problems.Add(new("SND020", SoundSeverity.Info, $"The file has {bits}-bit samples; Cairn plays and converts them as 16-bit."));
        if (info.BitrateKbps is { } kbps) details.Add(("Bitrate", $"{kbps:N0} kbps"));
        details.Add(("Sample rate", $"{rate.ToString("N0", CultureInfo.InvariantCulture)} Hz"));
        if (loop is not null) details.Add(("Loop", loop.IsWhole(frames) ? "the whole sound" : $"samples {loop.Start:N0} to {loop.End:N0}"));
        bool lossy = ogg || info.Codec.Contains("ADPCM", StringComparison.Ordinal) || info.Codec.Contains("ima4", StringComparison.Ordinal);
        return new DecodedSound
        {
            Name = name,
            Format = riff ? "WAVE" : ogg ? "Ogg Vorbis" : info.Container,
            Codec = info.Codec,
            SampleRate = rate,
            Channels = channels,
            BitDepth = ogg ? "Vorbis (compressed)" : info.Codec == "PCM" || info.Codec.StartsWith("PCM", StringComparison.Ordinal) ? $"{bits}-bit" : $"{info.Codec} (decodes to 16-bit)",
            SourceBits = info.Codec.StartsWith("PCM", StringComparison.Ordinal) ? bits : null,
            Samples = samples,
            Loop = loop,
            IsLossySource = lossy,
            FileSize = bytes.LongLength,
            Details = details,
            Quirks = quirks,
            Problems = problems,
        };
    }

    private static AudioInfo ProbeOgg(byte[] bytes, string name)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return AudioProbe.ProbeOgg(stream, name);
    }

    private static SoundLoop? ClampLoop(long start, long end, long frames, string source, List<SoundProblem> problems)
    {
        if (start < 0 || start >= frames || end <= start)
        {
            problems.Add(new("SND021", SoundSeverity.Warning, $"The {source} give a loop ({start:N0} to {end:N0}) outside the sound's {frames:N0} samples; it is ignored."));
            return null;
        }
        if (end > frames)
        {
            problems.Add(new("SND021", SoundSeverity.Warning, $"The {source} give a loop end ({end:N0}) past the sound's end ({frames:N0}); it is cut there."));
            end = frames;
        }
        return new SoundLoop(start, end, true, source);
    }

    /// <summary>LOOPSTART with LOOPLENGTH or LOOPEND (sample frames) from an Ogg Vorbis stream's comments.</summary>
    internal static (long Start, long End)? OggLoop(byte[] bytes)
    {
        try
        {
            using var reader = new NVorbis.VorbisReader(new MemoryStream(bytes, writable: false), closeOnDispose: true);
            static long? Tag(NVorbis.Contracts.ITagData? tags, string key) =>
                tags?.GetTagSingle(key) is { Length: > 0 } text && long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;
            var tags = reader.Tags;
            if (Tag(tags, "LOOPSTART") is not { } start) return null;
            if (Tag(tags, "LOOPLENGTH") is { } length) return (start, start + length);
            if (Tag(tags, "LOOPEND") is { } end) return (start, end);
            return (start, reader.TotalSamples);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Reads a PCM WAVE (8, 16, 24 or 32-bit integer) into 16-bit samples.</summary>
    private static (short[] Samples, int Channels, int Rate, int Bits) ReadPcm(byte[] wav, string name)
    {
        var layout = AudioProbe.ReadWav(wav, name);
        if (layout.FormatTag != 1) throw new AssetFormatException($"'{name}' uses {layout.Compression}, which Cairn cannot decode.");
        int channels = layout.Channels, bits = layout.BitsPerSample;
        if (channels is < 1 or > 32) throw new AssetFormatException($"'{name}' has {channels} channels.");
        if (layout.SampleRate <= 0) throw new AssetFormatException($"'{name}' has a sample rate of {layout.SampleRate}.");
        if (bits is not (8 or 16 or 24 or 32)) throw new AssetFormatException($"'{name}' has {bits}-bit samples, which Cairn cannot decode.");
        int width = bits / 8;
        long frames = layout.DataLength / (width * channels);
        if (frames * channels > AudioDecoding.MaxOutputBytes / 2) throw new AssetFormatException($"'{name}' is too long to decode.");
        var samples = new short[frames * channels];
        var data = wav.AsSpan((int)layout.DataOffset, (int)(frames * channels * width));
        for (int i = 0; i < samples.Length; i++)
        {
            var s = data.Slice(i * width, width);
            samples[i] = width switch
            {
                1 => (short)((s[0] - 128) << 8),
                2 => BinaryPrimitives.ReadInt16LittleEndian(s),
                3 => (short)((s[1]) | (s[2] << 8)),
                _ => (short)(BinaryPrimitives.ReadInt32LittleEndian(s) >> 16),
            };
        }
        return (samples, channels, layout.SampleRate, bits);
    }
}
