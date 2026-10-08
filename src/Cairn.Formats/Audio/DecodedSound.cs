namespace Cairn.Formats.Audio;

/// <summary>How serious a <see cref="SoundProblem"/> is.</summary>
public enum SoundSeverity
{
    /// <summary>Worth knowing; nothing is wrong with the sound.</summary>
    Info,
    /// <summary>Damaged or unusual data that was decoded as well as it could be.</summary>
    Warning,
    /// <summary>Part of the sound could not be decoded.</summary>
    Error,
}

/// <summary>Something found while decoding a sound.</summary>
/// <param name="Code">Stable code ("SND003").</param>
/// <param name="Severity">How serious.</param>
/// <param name="Message">What and where.</param>
public sealed record SoundProblem(string Code, SoundSeverity Severity, string Message);

/// <summary>A loop of a sound, in sample frames (one sample per channel).</summary>
/// <param name="Start">First frame of the loop.</param>
/// <param name="End">The frame after the last one of the loop.</param>
/// <param name="Enabled">True when the sound loops when played (false: points are marked but not used).</param>
/// <param name="Source">Where the points come from ("header and ADPCM loop flags", "'smpl' chunk").</param>
public sealed record SoundLoop(long Start, long End, bool Enabled, string Source)
{
    /// <summary>Frames in the loop.</summary>
    public long Length => End - Start;

    /// <summary>True when the loop covers the whole sound of <paramref name="frames"/> frames.</summary>
    public bool IsWhole(long frames) => Start == 0 && End == frames;
}

/// <summary>
/// A sound decoded to 16-bit PCM (interleaved), with what was read about it: the source format, loop points, the
/// header fields and quirks of the source format, and problems found while decoding.
/// </summary>
public sealed class DecodedSound
{
    /// <summary>The file name.</summary>
    public required string Name { get; init; }
    /// <summary>The file type ("PS2 sound effect (.vse)", "WAVE").</summary>
    public required string Format { get; init; }
    /// <summary>The coding ("PS ADPCM", "PCM", "Vorbis", "IMA ADPCM").</summary>
    public required string Codec { get; init; }
    /// <summary>Playback rate written to converted files.</summary>
    public required int SampleRate { get; init; }
    /// <summary>Channel count (1 or more).</summary>
    public required int Channels { get; init; }
    /// <summary>The source's sample size ("16-bit", "4-bit ADPCM", "Vorbis (compressed)").</summary>
    public required string BitDepth { get; init; }
    /// <summary>Bits per sample of a PCM source (8, 16, 24, 32), else null.</summary>
    public int? SourceBits { get; init; }
    /// <summary>Interleaved 16-bit samples (<see cref="Channels"/> per frame).</summary>
    public required short[] Samples { get; init; }
    /// <summary>The loop, when the source has loop points.</summary>
    public SoundLoop? Loop { get; init; }
    /// <summary>True when the source coding already lost information (ADPCM, Vorbis).</summary>
    public bool IsLossySource { get; init; }
    /// <summary>The source file's size in bytes.</summary>
    public long FileSize { get; init; }
    /// <summary>The rate the original hardware plays at when it differs from <see cref="SampleRate"/> (PS2 pitch), else null.</summary>
    public double? ExactRate { get; init; }
    /// <summary>Header fields and layout facts (label, value), in file order.</summary>
    public IReadOnlyList<(string Label, string Value)> Details { get; init; } = [];
    /// <summary>What is special about the source format (shown in details; repeated in conversion reports).</summary>
    public IReadOnlyList<string> Quirks { get; init; } = [];
    /// <summary>What decoding found.</summary>
    public IReadOnlyList<SoundProblem> Problems { get; init; } = [];

    /// <summary>Sample frames (samples per channel).</summary>
    public long FrameCount => Channels <= 0 ? 0 : Samples.LongLength / Channels;

    /// <summary>Playing time.</summary>
    public TimeSpan Duration => SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    /// <summary>"22,050 Hz mono" / "44,100 Hz stereo" / "48,000 Hz, 6 channels".</summary>
    public string RateAndChannels => $"{SampleRate:N0} Hz {ChannelsText(Channels)}";

    /// <summary>"mono", "stereo" or "N channels".</summary>
    public static string ChannelsText(int channels) => channels switch { 1 => "mono", 2 => "stereo", _ => $"{channels} channels" };
}
