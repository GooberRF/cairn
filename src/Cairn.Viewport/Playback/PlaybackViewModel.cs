using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using Cairn.Ui.Mvvm;

namespace Cairn.Viewport;

/// <summary>
/// The transport under a viewport: the playhead in ticks, play/pause with a stopwatch clock (time is
/// elapsed real time × speed × 4800, never a frame count, so playback speed does not depend on the
/// render rate), loop, speed, stepping by frame and by key, and the readout in the user's time unit.
/// The clock runs on <see cref="CompositionTarget.Rendering"/> only while playing.
/// </summary>
public sealed class PlaybackViewModel : ObservableObject
{
    private readonly Func<TimeUnit> _unit;
    private readonly Stopwatch _clock = new();
    private float _time;
    private int _start;
    private int _end;
    private int _rampIn;
    private int _rampOut;
    private bool _isPlaying;
    private bool _loop = true;
    private double _speed = 1;
    private bool _hasClip;
    private int[] _keyTimes = [];
    private float _playStartTime;
    private bool _subscribed;

    /// <param name="unit">Reads the display unit (a global setting) whenever text is formatted.</param>
    /// <param name="timeBase">The tick rate of the times this transport plays.</param>
    public PlaybackViewModel(Func<TimeUnit> unit, TimeBase timeBase)
    {
        _unit = unit ?? throw new ArgumentNullException(nameof(unit));
        if (!(timeBase.TicksPerSecond > 0) || !(timeBase.TicksPerFrame > 0)) throw new ArgumentOutOfRangeException(nameof(timeBase));
        TimeBase = timeBase;
        _time = _start = _end = FirstFrame;
        FirstCommand = new RelayCommand(() => Seek(_start), () => _hasClip);
        LastCommand = new RelayCommand(() => Seek(_end), () => _hasClip);
        StepBackCommand = new RelayCommand(() => Step(-1), () => _hasClip);
        StepForwardCommand = new RelayCommand(() => Step(+1), () => _hasClip);
        PreviousKeyCommand = new RelayCommand(() => JumpKey(-1), () => _hasClip && _keyTimes.Length > 0);
        NextKeyCommand = new RelayCommand(() => JumpKey(+1), () => _hasClip && _keyTimes.Length > 0);
        PlayPauseCommand = new RelayCommand(TogglePlay, () => _hasClip && _end > _start);
        ToggleLoopCommand = new RelayCommand(() => Loop = !Loop);
        CycleUnitCommand = new RelayCommand(() => UnitCycleRequested?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>The speeds the speed box offers.</summary>
    public static IReadOnlyList<double> Speeds { get; } = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];

    /// <summary>Raised whenever <see cref="Time"/> changes (by playback, seeking or a new range).</summary>
    public event EventHandler? TimeChanged;

    /// <summary>Raised when the readout is clicked: the shell cycles the global time unit.</summary>
    public event EventHandler? UnitCycleRequested;

    /// <summary>The playhead, in ticks (fractional while playing).</summary>
    public float Time
    {
        get => _time;
        set => Seek(value);
    }

    public int StartTime => _start;

    public int EndTime => _end;

    public int RampIn => _rampIn;

    public int RampOut => _rampOut;

    /// <summary>Key times of every track (rotation and position), sorted, for previous/next key.</summary>
    public IReadOnlyList<int> KeyTimes => _keyTimes;

    /// <summary>True when there is a clip to play.</summary>
    public bool HasClip => _hasClip;

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!Set(ref _isPlaying, value)) return;
            Raise(nameof(PlayGlyph));
            Raise(nameof(PlayToolTip));
        }
    }

    public bool Loop
    {
        get => _loop;
        set => Set(ref _loop, value);
    }

    /// <summary>Playback speed multiplier, 0.1 to 4.</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            double clamped = Math.Clamp(value, 0.1, 4);
            if (!Set(ref _speed, clamped)) return;
            if (_isPlaying)
            {
                _playStartTime = _time;
                _clock.Restart();
            }
            Raise(nameof(SpeedText));
        }
    }

    /// <summary>"1×", "0.5×".</summary>
    public string SpeedText => _speed.ToString("0.##", CultureInfo.CurrentCulture) + "×";

    /// <summary>Play or pause glyph (Segoe MDL2).</summary>
    public string PlayGlyph => _isPlaying ? "" : "";

    public string PlayToolTip => _isPlaying ? "Pause (Space)" : "Play (Space)";

    /// <summary>"12 / 40 f" in the current unit.</summary>
    public string TimeText
    {
        get
        {
            if (!_hasClip) return "—";
            var unit = _unit();
            return TimeFormat.Number(_time, unit, TimeBase) + " / " + TimeFormat.Number(_end, unit, TimeBase) + " " + TimeFormat.Suffix(unit);
        }
    }

    /// <summary>Tooltip of the readout, giving the other units.</summary>
    public string TimeToolTip => _hasClip
        ? $"{TimeFormat.Format(_time, TimeUnit.Frames, TimeBase)} · {TimeFormat.Format(_time, TimeUnit.Seconds, TimeBase)} · {TimeFormat.Format(_time, TimeUnit.Ticks, TimeBase)}\nClick to switch between frames, seconds and ticks."
        : "No clip";

    public RelayCommand FirstCommand { get; }
    public RelayCommand LastCommand { get; }
    public RelayCommand StepBackCommand { get; }
    public RelayCommand StepForwardCommand { get; }
    public RelayCommand PreviousKeyCommand { get; }
    public RelayCommand NextKeyCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand ToggleLoopCommand { get; }
    public RelayCommand CycleUnitCommand { get; }

    /// <summary>
    /// Sets the range the transport covers (null = no clip). The playhead stays where it was when it
    /// is still inside, else moves to the start.
    /// </summary>
    public void SetClip(PlaybackRange? clip)
    {
        _hasClip = clip is not null;
        if (clip is null)
        {
            Pause();
            _start = _end = FirstFrame;
            _rampIn = _rampOut = 0;
            _keyTimes = [];
        }
        else
        {
            _start = clip.StartTime;
            _end = Math.Max(clip.StartTime, clip.EndTime);
            _rampIn = clip.RampIn;
            _rampOut = clip.RampOut;
            _keyTimes = [.. new SortedSet<int>(clip.KeyTimes)];
        }
        RaiseAll(nameof(StartTime), nameof(EndTime), nameof(RampIn), nameof(RampOut), nameof(KeyTimes), nameof(HasClip));
        RaiseCommands();
        float t = _time;
        if (!_hasClip || t < _start || t > _end) t = _start;
        SetTimeCore(t);
    }

    /// <summary>The display unit now (the global setting the constructor's callback reads).</summary>
    public TimeUnit Unit => _unit();

    /// <summary>The tick rate of this transport's times.</summary>
    public TimeBase TimeBase { get; }

    /// <summary>The time of frame 1 (one frame's ticks), where an empty transport rests.</summary>
    private int FirstFrame => (int)Math.Round(TimeBase.TicksPerFrame);

    /// <summary>Re-formats the readout (the time unit changed).</summary>
    public void RefreshText() => RaiseAll(nameof(TimeText), nameof(TimeToolTip), nameof(UnitSuffix));

    /// <summary>The display unit's suffix ("f", "s", "ticks"); raised when the unit changes.</summary>
    public string UnitSuffix => TimeFormat.Suffix(_unit());

    /// <summary>Moves the playhead (clamped to the clip) without changing play state.</summary>
    public void Seek(float ticks)
    {
        float t = Math.Clamp(ticks, _start, Math.Max(_start, _end));
        if (_isPlaying)
        {
            _playStartTime = t;
            _clock.Restart();
        }
        SetTimeCore(t);
    }

    public void Play()
    {
        if (!_hasClip || _end <= _start || _isPlaying) return;
        if (_time >= _end && !_loop) SetTimeCore(_start);
        _playStartTime = _time;
        _clock.Restart();
        IsPlaying = true;
        if (!_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    public void Pause()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
        _clock.Stop();
        IsPlaying = false;
    }

    public void TogglePlay()
    {
        if (_isPlaying) Pause();
        else Play();
    }

    private void Step(int frames)
    {
        Pause();
        // Snap to the frame grid first, so stepping from a fractional time lands on whole frames.
        double frame = (_time - _start) / TimeBase.TicksPerFrame;
        double next = frames > 0 ? Math.Floor(frame + 1e-6) + frames : Math.Ceiling(frame - 1e-6) + frames;
        Seek((float)(_start + next * TimeBase.TicksPerFrame));
    }

    private void JumpKey(int direction)
    {
        Pause();
        if (_keyTimes.Length == 0) return;
        int target;
        if (direction > 0)
        {
            target = _keyTimes.FirstOrDefault(k => k > _time + 0.5f, _end);
        }
        else
        {
            target = _keyTimes.LastOrDefault(k => k < _time - 0.5f, _start);
        }
        Seek(target);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_isPlaying) return;
        double elapsedTicks = _clock.Elapsed.TotalSeconds * TimeBase.TicksPerSecond * _speed;
        double t = _playStartTime + elapsedTicks;
        double length = _end - _start;
        if (t > _end)
        {
            if (_loop && length > 0)
            {
                t = _start + (t - _start) % length;
            }
            else
            {
                SetTimeCore(_end);
                Pause();
                return;
            }
        }
        SetTimeCore((float)t);
    }

    private void SetTimeCore(float t)
    {
        bool changed = _time != t;
        _time = t;
        if (changed) Raise(nameof(Time));
        Raise(nameof(TimeText));
        if (!_isPlaying) Raise(nameof(TimeToolTip));
        TimeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseCommands()
    {
        FirstCommand.RaiseCanExecuteChanged();
        LastCommand.RaiseCanExecuteChanged();
        StepBackCommand.RaiseCanExecuteChanged();
        StepForwardCommand.RaiseCanExecuteChanged();
        PreviousKeyCommand.RaiseCanExecuteChanged();
        NextKeyCommand.RaiseCanExecuteChanged();
        PlayPauseCommand.RaiseCanExecuteChanged();
    }
}
