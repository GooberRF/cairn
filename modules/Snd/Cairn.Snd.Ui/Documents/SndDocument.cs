using System.Globalization;
using System.Windows;
using Cairn.Formats;
using Cairn.Formats.Audio;
using Cairn.Snd.Ui.Playback;
using Cairn.Ui.Documents;
using Cairn.Ui.Modules;
using Cairn.Ui.Mvvm;
using Cairn.Workspace;

namespace Cairn.Snd.Ui.Documents;

/// <summary>
/// An open sound: read-only (PS2 sounds are never written; Save As writes a WAV), decoded once to PCM. Holds the player
/// (play, pause, stop, loop, volume) and the view state (zoom, scroll) the view draws.
/// </summary>
public sealed class SndDocument : DocumentBase
{
    private readonly SndModule _module;
    private byte[] _bytes;
    private SoundPlayer? _player;
    private bool _looping;

    internal SndDocument(SndModule module, IDocumentKind kind, byte[] bytes, DecodedSound sound, string name, string? path, string? origin)
        : base(module.ShellContext, kind, name, path, origin)
    {
        _module = module;
        _bytes = bytes;
        Sound = sound;
        Peaks = new WaveformPeaks(sound.Samples, sound.Channels);
        _looping = sound.Loop is { Enabled: true };
        PlayPauseCommand = new RelayCommand(TogglePlay, () => Sound.FrameCount > 0);
        StopCommand = new RelayCommand(Stop, () => Sound.FrameCount > 0);
        ShowProblemsCommand = new RelayCommand(() => _module.ShowProblems(this));
    }

    /// <summary>Raised after the sound was decoded again (Reload).</summary>
    public event EventHandler? SoundChanged;
    /// <summary>Raised when playing starts, pauses, stops or ends (any thread raises; handlers run on the UI thread).</summary>
    public event EventHandler? PlaybackChanged;

    /// <summary>The decoded sound.</summary>
    public DecodedSound Sound { get; private set; }
    /// <summary>Waveform peaks of <see cref="Sound"/>.</summary>
    public WaveformPeaks Peaks { get; private set; }
    /// <summary>The file's bytes as opened.</summary>
    public byte[] Bytes => _bytes;

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ShowProblemsCommand { get; }

    /// <summary>The player (made on first use; silent in diagnostic runs).</summary>
    public SoundPlayer Player
    {
        get
        {
            if (_player is null)
            {
                _player = SoundPlayer.Create(Sound.Samples, Sound.Channels, Sound.SampleRate, _module.SilentPlayback);
                _player.LoopRegion = Sound.Loop;
                _player.Looping = _looping;
                _player.Volume = _module.Settings.Volume;
                _player.Ended += (_, _) => Shell.Dispatcher.BeginInvoke(() => PlaybackChanged?.Invoke(this, EventArgs.Empty));
            }
            return _player;
        }
    }

    /// <summary>True when playing repeats the loop (the whole sound when it has no loop points).</summary>
    public bool Looping
    {
        get => _looping;
        set
        {
            if (!Set(ref _looping, value)) return;
            if (_player is not null) _player.Looping = value;
            Raise(nameof(StatusItems));
        }
    }

    /// <summary>The volume of every sound tab (0..1, remembered).</summary>
    public double Volume
    {
        get => _module.Settings.Volume;
        set
        {
            if (!_module.Shell.IsDiagnosticRun) _module.Settings.Volume = value;
            if (_player is not null) _player.Volume = value;
            Raise(nameof(Volume));
        }
    }

    public bool IsPlaying => _player?.State == PlayerState.Playing;

    /// <summary>The frame being heard (or where playing resumes).</summary>
    public long Position => _player?.Position ?? 0;

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Play()
    {
        if (Sound.FrameCount == 0) return;
        Player.Play();
        if (Player.Failure is { } failure) ShowStatus("Cannot play: " + failure);
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (_player is null) return;
        _player.Pause();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (_player is null) return;
        _player.Stop();
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the play position to <paramref name="frame"/>.</summary>
    public void Seek(long frame)
    {
        Player.Seek(Math.Clamp(frame, 0, Sound.FrameCount));
        PlaybackChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"0:01.234" for a frame.</summary>
    public string TimeOf(long frame) => FormatTime(Sound.SampleRate <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)frame / Sound.SampleRate));

    public static string FormatTime(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}:{t.Seconds:00}.{t.Milliseconds:000}");

    // ---- the document contract ---------------------------------------------------------------------------------

    /// <summary>Sounds are never written in their own format (PS2 sounds are read-only); Save As writes a WAV.</summary>
    public override bool IsReadOnly => true;
    public override bool IsDirty => false;
    public override bool CanUndo => false;
    public override bool CanRedo => false;
    public override string? UndoLabel => null;
    public override string? RedoLabel => null;
    public override void Undo() { }
    public override void Redo() { }
    public override byte[]? CaptureRecovery() => null;

    /// <summary>Writes the decoded sound as a 16-bit WAV (with its loop); any other extension is refused.</summary>
    public override void SaveTo(string path)
    {
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Cairn writes sounds as WAV (.wav) only. Use Sound > Convert... for the options.");
        var result = SoundConversion.Convert(Sound, new SoundConvertOptions { WriteLoop = _module.Settings.WriteLoop });
        if (result.Bytes is null) throw new InvalidOperationException(result.Error);
        AtomicFile.WriteAllBytes(path, result.Bytes);
        ShowStatus($"Wrote {Path.GetFileName(path)}");
    }

    protected override void LoadBytes(byte[] bytes, bool keepDirty)
    {
        var sound = SoundDecoder.Decode(bytes, DisplayName);
        Stop();
        _player?.Dispose();
        _player = null;
        _bytes = bytes;
        Sound = sound;
        Peaks = new WaveformPeaks(sound.Samples, sound.Channels);
        RaiseAll(nameof(Sound), nameof(Peaks), nameof(StatusItems));
        SoundChanged?.Invoke(this, EventArgs.Empty);
        _module.RefreshCommands();
    }

    protected override bool MatchesSaved(byte[] bytes) => bytes.AsSpan().SequenceEqual(_bytes);

    public override IReadOnlyList<StatusItem> StatusItems
    {
        get
        {
            var s = Sound;
            var items = new List<StatusItem>
            {
                new($"{s.Codec} · {s.RateAndChannels}", s.Format),
                new(FormatTime(s.Duration), $"{s.FrameCount:N0} samples per channel"),
            };
            if (s.Loop is { } loop) items.Add(new(Looping ? "Loops" : "Loop off", $"Loop points: samples {loop.Start:N0} to {loop.End:N0} ({loop.Source})"));
            int errors = s.Problems.Count(p => p.Severity == SoundSeverity.Error), warnings = s.Problems.Count(p => p.Severity == SoundSeverity.Warning);
            if (errors + warnings > 0)
                items.Add(new(string.Join(", ", new[] { errors > 0 ? Plural(errors, "error") : null, warnings > 0 ? Plural(warnings, "warning") : null }.OfType<string>()),
                    "Problems found in this sound: click to open the Problems tab", ShowProblemsCommand));
            return items;
        }
    }

    internal static string Plural(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";

    public override void OnDeactivated() => Pause();

    protected override FrameworkElement CreateView() => new SndDocumentView(this, _module);

    public override void Dispose()
    {
        _player?.Dispose();
        _player = null;
        _module.Forget(this);
        base.Dispose();
    }
}
