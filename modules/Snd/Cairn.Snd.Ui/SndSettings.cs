using Cairn.Formats.Audio;
using Cairn.Ui.Modules;

namespace Cairn.Snd.Ui;

/// <summary>
/// The sounds module's remembered choices ("snd." keys): what Convert offers first (format, Ogg quality, where the files
/// go, the folder, loop points). The Convert window remembers what was last used; the settings page edits the same values.
/// Replacing files or entries of the same name is never remembered: it is chosen (and confirmed) for each conversion.
/// </summary>
public sealed class SndSettings(Func<ModuleSettings?> store)
{
    /// <summary>The output format (WAV or Ogg Vorbis).</summary>
    public SoundOutputFormat Format
    {
        get => store()?.Get("format", SoundOutputFormat.Wav) is { } f && Enum.IsDefined(f) ? f : SoundOutputFormat.Wav;
        set => store()?.Set("format", value);
    }

    /// <summary>The Ogg Vorbis quality, -0.1 to 1.0 in steps of 0.1 (0.5 = "q5").</summary>
    public float Quality
    {
        get => Snap(store()?.Get("quality", (double)OggVorbisWriter.DefaultQuality) ?? OggVorbisWriter.DefaultQuality);
        set => store()?.Set("quality", (double)Snap(value));
    }

    /// <summary>A quality clamped to -0.1..1.0 and rounded to the slider's 0.1 steps (NaN gives the default).</summary>
    public static float Snap(double quality) =>
        double.IsNaN(quality) ? OggVorbisWriter.DefaultQuality
            : (float)Math.Round(Math.Clamp(quality, OggVorbisWriter.MinQuality, OggVorbisWriter.MaxQuality), 1);

    /// <summary>Where converted files go when the source allows it.</summary>
    public SoundTarget Target
    {
        get => store()?.Get("target", SoundTarget.IntoPackfile) is { } t && Enum.IsDefined(t) ? t : SoundTarget.IntoPackfile;
        set => store()?.Set("target", value);
    }

    /// <summary>The folder last chosen ("" for none).</summary>
    public string Folder
    {
        get => store()?.Get("folder", string.Empty) ?? string.Empty;
        set => store()?.Set("folder", value ?? string.Empty);
    }

    /// <summary>True to keep loops (a WAVE 'smpl' chunk, Ogg LOOPSTART/LOOPLENGTH comments).</summary>
    public bool WriteLoop
    {
        get => store()?.Get("writeLoop", true) ?? true;
        set => store()?.Set("writeLoop", value);
    }

    /// <summary>The playing volume (0..1), shared by every sound tab.</summary>
    public double Volume
    {
        get => Math.Clamp(store()?.Get("volume", 0.8) ?? 0.8, 0, 1);
        set => store()?.Set("volume", Math.Clamp(value, 0, 1));
    }
}
