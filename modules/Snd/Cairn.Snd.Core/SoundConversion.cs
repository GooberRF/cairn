using System.Globalization;
using Cairn.Formats;
using Cairn.Formats.Audio;

namespace Cairn.Snd;

/// <summary>What a sound is converted to.</summary>
public enum SoundOutputFormat
{
    /// <summary>16-bit PCM WAVE (lossless for the decoded sound); loops in a <c>smpl</c> chunk.</summary>
    Wav,
    /// <summary>Ogg Vorbis (lossy, libvorbis VBR at <see cref="SoundConvertOptions.Quality"/>); loops as LOOPSTART/LOOPLENGTH comments.</summary>
    Ogg,
}

/// <summary>Where converted files go.</summary>
public enum SoundTarget
{
    /// <summary>A folder chosen in the dialog.</summary>
    Folder,
    /// <summary>The source file's own folder (or the packfile's folder for an entry).</summary>
    NextToSource,
    /// <summary>Into the packfile the source is an entry of, as new entries (one undo step).</summary>
    IntoPackfile,
}

/// <summary>How sounds are converted.</summary>
public sealed record SoundConvertOptions
{
    /// <summary>The output format.</summary>
    public SoundOutputFormat Format { get; init; } = SoundOutputFormat.Wav;
    /// <summary>True to keep a loop in the output (a WAVE <c>smpl</c> chunk, or Ogg LOOPSTART/LOOPLENGTH comments).</summary>
    public bool WriteLoop { get; init; } = true;
    /// <summary>The Ogg Vorbis quality, -0.1 to 1.0 (0.5 is oggenc's "-q 5"); ignored for WAV.</summary>
    public float Quality { get; init; } = OggVorbisWriter.DefaultQuality;
}

/// <summary>The outcome of converting one sound.</summary>
/// <param name="SourceName">The source's name.</param>
/// <param name="OutputName">The output's file name (same stem, new extension).</param>
/// <param name="Bytes">The converted file, or null when it failed.</param>
/// <param name="Notes">What was approximated or left out (never a reason to refuse).</param>
/// <param name="Error">Why it failed, or null.</param>
public sealed record SoundConvertResult(string SourceName, string OutputName, byte[]? Bytes, IReadOnlyList<string> Notes, string? Error)
{
    /// <summary>True when the conversion produced a file.</summary>
    public bool Succeeded => Bytes is not null;

    /// <summary>
    /// True when the sound was left out on purpose rather than failing (it is in the output format already, or its
    /// output name is taken); <see cref="Error"/> says why.
    /// </summary>
    public bool Skipped { get; init; }
}

/// <summary>Converts decoded sounds to the output formats and says what was approximated.</summary>
public static class SoundConversion
{
    /// <summary>The extension of a format (".wav").</summary>
    public static string ExtensionOf(SoundOutputFormat format) => format switch
    {
        SoundOutputFormat.Ogg => ".ogg",
        _ => ".wav",
    };

    /// <summary>A format's name for menus and reports.</summary>
    public static string DisplayName(SoundOutputFormat format) => format switch
    {
        SoundOutputFormat.Ogg => "Ogg Vorbis (smaller, lossy)",
        _ => "WAV (16-bit PCM)",
    };

    /// <summary>
    /// The quality as a short label with libvorbis' nominal bitrate for 44.1 kHz stereo ("q5, about 160 kbit/s at
    /// 44.1 kHz stereo"); the encoder is variable-rate, so real files land near it, lower for mono and lower rates.
    /// </summary>
    public static string QualityText(float quality)
    {
        // libvorbis' nominal 44.1 kHz stereo bitrates for q-1..q10 (its vorbisenc setup tables)
        int[] kbps = [45, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 500];
        float q = Math.Clamp(quality, OggVorbisWriter.MinQuality, OggVorbisWriter.MaxQuality) * 10 + 1;
        int lo = (int)Math.Floor(q), hi = Math.Min(lo + 1, kbps.Length - 1);
        double rate = kbps[lo] + (kbps[hi] - kbps[lo]) * (q - lo);
        return $"{OggVorbisWriter.QualityLabel(quality)}, about {Math.Round(rate).ToString("N0", CultureInfo.InvariantCulture)} kbit/s at 44.1 kHz stereo";
    }

    /// <summary>
    /// The output name: the source's stem with the format's extension ("explosion.vse" to "explosion.wav"). A source that
    /// already has that extension (a 24-bit "music.wav" made 16-bit) gets "music (converted).wav", so the output is
    /// never named like its source.
    /// </summary>
    public static string OutputName(string sourceName, SoundOutputFormat format)
    {
        string stem = Path.GetFileNameWithoutExtension(sourceName ?? string.Empty);
        string ext = ExtensionOf(format);
        if (string.Equals(Path.GetExtension(sourceName ?? string.Empty), ext, StringComparison.OrdinalIgnoreCase)) stem += " (converted)";
        return (stem.Length == 0 ? "sound" : stem) + ext;
    }

    /// <summary>
    /// Why converting <paramref name="sound"/> to <paramref name="format"/> would make nothing new, or null when it is
    /// worth doing: a 16-bit PCM WAV is what WAV output writes already, and encoding an Ogg Vorbis file again only loses
    /// quality. (A WAV in another coding, such as 24-bit or ADPCM, does convert to a 16-bit WAV.)
    /// </summary>
    public static string? SameFormatReason(DecodedSound sound, SoundOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(sound);
        if (format == SoundOutputFormat.Wav && sound.Format == "WAVE" && sound.Codec.StartsWith("PCM", StringComparison.Ordinal) && sound.SourceBits == 16)
            return "it is a 16-bit WAV already";
        if (format == SoundOutputFormat.Ogg && sound.Format == "Ogg Vorbis")
            return "it is an Ogg Vorbis file already (encoding it again would only lose quality)";
        return null;
    }

    /// <summary>
    /// Decodes and converts <paramref name="bytes"/>; a sound that cannot be decoded, whatever the decoder ran into,
    /// gives a result with an error (a batch lists it and goes on).
    /// </summary>
    public static SoundConvertResult Convert(string name, byte[] bytes, SoundConvertOptions options)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);
        DecodedSound sound;
        try
        {
            sound = SoundDecoder.Decode(bytes, name);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return new SoundConvertResult(name, OutputName(name, options.Format), null, [], ex.Message);
        }
        return Convert(sound, options);
    }

    /// <summary>Converts a decoded sound (one already in the output format is skipped: see <see cref="SameFormatReason"/>).</summary>
    public static SoundConvertResult Convert(DecodedSound sound, SoundConvertOptions options)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(options);
        string output = OutputName(sound.Name, options.Format);
        if (SameFormatReason(sound, options.Format) is { } same)
            return new SoundConvertResult(sound.Name, output, null, [], $"not converted: {same}") { Skipped = true };
        try
        {
            byte[] bytes = options.Format == SoundOutputFormat.Ogg ? ToOgg(sound, options) : SoundWriter.ToWav(sound, options.WriteLoop);
            return new SoundConvertResult(sound.Name, output, bytes, Notes(sound, options), null);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            return new SoundConvertResult(sound.Name, output, null, [], ex.Message);
        }
    }

    /// <summary>
    /// An Ogg Vorbis file of <paramref name="sound"/> at <see cref="SoundConvertOptions.Quality"/>; an enabled loop becomes
    /// LOOPSTART/LOOPLENGTH comments (sample frames), which <see cref="SoundDecoder"/> reads back.
    /// </summary>
    public static byte[] ToOgg(DecodedSound sound, SoundConvertOptions options)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(options);
        var tags = new List<KeyValuePair<string, string>>();
        if (options.WriteLoop && sound.Loop is { Enabled: true } loop && loop.Length > 0)
        {
            tags.Add(new("LOOPSTART", loop.Start.ToString(CultureInfo.InvariantCulture)));
            tags.Add(new("LOOPLENGTH", loop.Length.ToString(CultureInfo.InvariantCulture)));
        }
        return OggVorbisWriter.Write(sound.Samples, sound.Channels, sound.SampleRate, options.Quality, tags);
    }

    /// <summary>What converting <paramref name="sound"/> with <paramref name="options"/> approximates or leaves out.</summary>
    public static IReadOnlyList<string> Notes(DecodedSound sound, SoundConvertOptions options)
    {
        ArgumentNullException.ThrowIfNull(sound);
        ArgumentNullException.ThrowIfNull(options);
        var notes = new List<string>();
        var inv = CultureInfo.InvariantCulture;
        bool ogg = options.Format == SoundOutputFormat.Ogg;
        string kind = ogg ? "Ogg" : "WAV";
        if (ogg)
            notes.Add(sound.IsLossySource
                ? $"{sound.Codec} is lossy already, and Ogg Vorbis ({OggVorbisWriter.QualityLabel(options.Quality)}) is lossy too: the sound is encoded again, losing a little more. WAV keeps it exactly."
                : $"Ogg Vorbis is lossy: at {OggVorbisWriter.QualityLabel(options.Quality)} the sound is close to the original, not identical. WAV keeps it exactly.");
        else if (sound.IsLossySource)
            notes.Add($"{sound.Codec} is lossy already: the WAV keeps the decoded sound exactly, with no further loss.");
        if (sound.SourceBits is > 16)
            notes.Add(ogg ? $"The {sound.SourceBits}-bit samples are encoded from their 16-bit decode." : $"The {sound.SourceBits}-bit samples are written as 16-bit.");
        if (sound.ExactRate is { } exact)
        {
            double percent = Math.Abs(exact - sound.SampleRate) / sound.SampleRate * 100;
            notes.Add($"Written at {sound.SampleRate.ToString("N0", inv)} Hz; the console plays it at {exact.ToString("N1", inv)} Hz ({percent.ToString("0.00", inv)}% {(exact < sound.SampleRate ? "slower" : "faster")}).");
        }
        if (sound.Loop is { Enabled: true } loop)
        {
            string where = loop.IsWhole(sound.FrameCount) ? "the whole sound" : $"samples {loop.Start.ToString("N0", inv)} to {loop.End.ToString("N0", inv)}";
            notes.Add(!options.WriteLoop
                ? $"The loop ({where}) is not kept: loop points are turned off."
                : ogg
                    ? $"The loop ({where}) is written as LOOPSTART/LOOPLENGTH comments; players and tools that ignore them play the sound once."
                    : $"The loop ({where}) is written in a 'smpl' chunk; players and tools that ignore it play the sound once.");
        }
        if (sound.Format.StartsWith("PS2 sound effect", StringComparison.Ordinal))
            notes.Add($"The PS2 header's envelope, key-off time and flags have no {kind} equivalent and are not kept.");
        else if (sound.Format.StartsWith("PS2 music", StringComparison.Ordinal))
            notes.Add($"The PS2 header's block time and loop start block have no {kind} equivalent and are not kept.");
        foreach (var p in sound.Problems.Where(p => p.Severity != SoundSeverity.Info || p.Code is "SND012" or "SND020"))
            notes.Add($"Source {p.Code}: {p.Message}");
        return notes;
    }

    /// <summary>"Converted 12 sounds to WAV; 2 left out (see the report); 1 could not be converted." for a batch.</summary>
    public static string Summary(IReadOnlyList<SoundConvertResult> results, SoundOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(results);
        var inv = CultureInfo.InvariantCulture;
        int ok = results.Count(r => r.Succeeded), skipped = results.Count(r => !r.Succeeded && r.Skipped), failed = results.Count - ok - skipped;
        string what = ok == 1 ? "1 sound" : $"{ok.ToString("N0", inv)} sounds";
        string text = $"Converted {what} to {ExtensionOf(format).TrimStart('.').ToUpperInvariant()}";
        if (skipped > 0) text += $"; {skipped.ToString("N0", inv)} left out (already in that format, or the name is taken)";
        return failed == 0 ? text + "." : $"{text}; {failed.ToString("N0", inv)} could not be converted.";
    }
}
