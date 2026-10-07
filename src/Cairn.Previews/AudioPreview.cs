using System.Buffers.Binary;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cairn.Formats.Audio;

namespace Cairn.Previews;

/// <summary>Decoded audio for <see cref="AudioPreview"/>: a temporary file the player opens, and waveform peaks.</summary>
public sealed class AudioData : IDisposable
{
    private const int PeakCount = 1_200;

    private AudioData(string path, float[]? peaks, TimeSpan? duration, string format)
    {
        TempPath = path;
        Peaks = peaks;
        Duration = duration;
        Format = format;
    }

    /// <summary>The temporary file (a PCM WAVE, or the MP3 itself); deleted on dispose.</summary>
    public string TempPath { get; }
    /// <summary>Peak level (0..1) per slice of the sound, or null for MP3 (not decoded here).</summary>
    public float[]? Peaks { get; }
    public TimeSpan? Duration { get; }
    public string Format { get; }

    /// <summary>The folder the temporary files go to; emptied of stale files by <see cref="Decode"/>.</summary>
    public static string TempFolder => Path.Combine(Path.GetTempPath(), "Cairn", "preview");

    /// <summary>Decodes through <see cref="AudioDecoding"/> to a temporary file and computes the waveform (pool thread).</summary>
    /// <exception cref="Formats.AssetFormatException">Not audio Cairn can decode.</exception>
    public static AudioData Decode(byte[] bytes, string name, CancellationToken ct)
    {
        AudioPlayback playback;
        using (var input = new MemoryStream(bytes, writable: false)) playback = AudioDecoding.ForPlayback(name, input);
        ct.ThrowIfCancellationRequested();
        float[]? peaks = null;
        TimeSpan? duration = null;
        string format = "MP3";
        if (playback.Extension == ".wav") (peaks, duration, format) = Waveform(playback.Bytes);
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(TempFolder);
        string path = Path.Combine(TempFolder, Guid.NewGuid().ToString("N") + playback.Extension);
        File.WriteAllBytes(path, playback.Bytes);
        return new AudioData(path, peaks, duration, format);
    }

    /// <summary>Peaks of a PCM (or float) WAVE: the loudest sample of any channel per slice.</summary>
    internal static (float[]? Peaks, TimeSpan? Duration, string Format) Waveform(byte[] wav)
    {
        ReadOnlySpan<byte> b = wav;
        int pos = 12, tag = 0, channels = 0, rate = 0, bits = 0;
        int dataAt = -1, dataLength = 0;
        while (pos + 8 <= b.Length)
        {
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(b[pos..]);
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b[(pos + 4)..]), (uint)(b.Length - pos - 8));
            if (id == 0x20746D66 && length >= 16) // "fmt "
            {
                tag = BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 8)..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 10)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(b[(pos + 12)..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 22)..]);
                if (tag == 0xFFFE && length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(b[(pos + 32)..]);
            }
            else if (id == 0x61746164) // "data"
            {
                dataAt = pos + 8;
                dataLength = length;
                break;
            }
            pos += 8 + length + (length & 1);
        }
        int bytesPerSample = bits / 8;
        if (dataAt < 0 || channels <= 0 || rate <= 0 || bytesPerSample is < 1 or > 4 || (tag != 1 && tag != 3)) return (null, null, "WAVE");
        int frameBytes = bytesPerSample * channels;
        long frames = dataLength / frameBytes;
        var duration = TimeSpan.FromSeconds((double)frames / rate);
        string format = string.Format(CultureInfo.CurrentCulture, "{0:N0} Hz, {1}, {2}-bit", rate, channels == 1 ? "mono" : channels == 2 ? "stereo" : channels + " channels", bits);
        if (frames == 0) return (new float[1], duration, format);

        int count = (int)Math.Min(PeakCount, frames);
        var peaks = new float[count];
        for (long f = 0; f < frames; f++)
        {
            int at = dataAt + (int)(f * frameBytes);
            float peak = 0;
            for (int c = 0; c < channels; c++)
            {
                var s = b.Slice(at + c * bytesPerSample, bytesPerSample);
                float v = (tag, bytesPerSample) switch
                {
                    (3, 4) => Math.Abs(BinaryPrimitives.ReadSingleLittleEndian(s)),
                    (_, 1) => Math.Abs(s[0] - 128) / 128f,
                    (_, 2) => Math.Abs(BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f),
                    (_, 3) => Math.Abs(((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f),
                    _ => Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f),
                };
                if (v > peak) peak = v;
            }
            int slot = (int)(f * count / frames);
            if (peak > peaks[slot]) peaks[slot] = Math.Min(1, peak);
        }
        return (peaks, duration, format);
    }

    public void Dispose()
    {
        try { File.Delete(TempPath); }
        catch (IOException) { /* still open in the player: the next start sweeps it */ }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes leftover temporary files older than an hour (from a crash).</summary>
    public static void SweepStale()
    {
        try
        {
            if (!Directory.Exists(TempFolder)) return;
            foreach (var file in Directory.EnumerateFiles(TempFolder))
            {
                try { if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1)) File.Delete(file); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Draws the waveform peaks and the play position; clicking seeks.</summary>
internal sealed class WaveformView : FrameworkElement
{
    private readonly float[]? _peaks;
    private double _position;

    public WaveformView(float[]? peaks)
    {
        _peaks = peaks;
        MinHeight = 60;
        Cursor = Cursors.Hand;
        ToolTip = "Waveform: click to move the play position";
        AutomationProperties.SetName(this, "Waveform");
    }

    /// <summary>Raised with the clicked position (0..1).</summary>
    public event Action<double>? Seek;

    /// <summary>The play position (0..1).</summary>
    public double Position
    {
        get => _position;
        set { if (Math.Abs(_position - value) > 1e-6) { _position = value; InvalidateVisual(); } }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (ActualWidth > 0) Seek?.Invoke(Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var background = TryFindResource("Preview.Background") as Brush ?? Brushes.Black;
        var wave = TryFindResource("App.Accent") as Brush ?? Brushes.SteelBlue;
        var line = TryFindResource("App.SubtleBorder") as Brush ?? Brushes.Gray;
        var head = TryFindResource("Severity.Warning") as Brush ?? Brushes.Orange;
        dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
        double mid = h / 2;
        dc.DrawLine(new Pen(line, 1), new Point(0, mid), new Point(w, mid));
        if (_peaks is { Length: > 0 } peaks && w > 0)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                int columns = (int)Math.Max(1, w);
                for (int x = 0; x < columns; x++)
                {
                    int from = x * peaks.Length / columns, to = Math.Max(from + 1, (x + 1) * peaks.Length / columns);
                    float peak = 0;
                    for (int i = from; i < to && i < peaks.Length; i++) peak = Math.Max(peak, peaks[i]);
                    double half = Math.Max(0.5, peak * (mid - 2));
                    g.BeginFigure(new Point(x + 0.5, mid - half), false, false);
                    g.LineTo(new Point(x + 0.5, mid + half), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(wave, 1), geometry);
        }
        double px = Math.Round(_position * w) + 0.5;
        dc.DrawLine(new Pen(head, 1.5), new Point(px, 0), new Point(px, h));
    }
}

/// <summary>
/// Audio preview: play / pause / stop through WPF's <see cref="MediaPlayer"/>, a position slider, the time,
/// volume and the waveform. Playback stops and the temporary file goes when the preview is disposed.
/// </summary>
public sealed class AudioPreview : UserControl, IDisposable
{
    /// <summary>Volume shared by every audio preview in the session (0..1).</summary>
    public static double Volume { get; set; } = 0.8;

    private readonly AudioData _data;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _timer;
    private readonly Slider _position;
    private readonly Slider _volume;
    private readonly TextBlock _time;
    private readonly TextBlock _status;
    private readonly Button _play;
    private readonly WaveformView _wave;
    private bool _playing;
    private bool _disposed;
    private bool _dragging;
    private bool _updating;

    public AudioPreview(AudioData data, string name)
    {
        _data = data;
        _wave = new WaveformView(data.Peaks);
        _wave.Seek += f => SeekTo(f);

        _play = PreviewUi.Button("Play", "Play or pause (Space)", (_, _) => TogglePlay());
        var stop = PreviewUi.Button("Stop", "Stop and go back to the start", (_, _) => Stop());
        _time = PreviewUi.Secondary("0:00.0");
        _status = PreviewUi.Secondary(data.Format);
        _volume = new Slider { Minimum = 0, Maximum = 1, Value = Volume, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Volume" };
        AutomationProperties.SetName(_volume, "Volume");
        _volume.ValueChanged += (_, e) => { Volume = e.NewValue; _player.Volume = e.NewValue; };
        var bar = PreviewUi.Toolbar(_play, stop, PreviewUi.Divider(), _time, PreviewUi.Divider(), PreviewUi.Secondary("Volume"), _volume, _status);

        _position = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(8, 6, 8, 2), ToolTip = "Play position", IsMoveToPointEnabled = true };
        AutomationProperties.SetName(_position, "Play position");
        _position.ValueChanged += (_, e) => { if (!_updating) SeekTo(e.NewValue); };
        _position.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _dragging = true));
        _position.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _dragging = false; SeekTo(_position.Value); }));

        var title = PreviewUi.Text(name);
        title.Margin = new Thickness(8, 6, 8, 0);
        title.FontWeight = FontWeights.SemiBold;
        var waveBorder = new Border { Child = _wave, Margin = new Thickness(8, 6, 8, 8), BorderThickness = new Thickness(1) };
        waveBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");

        var dock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(_position, Dock.Top);
        dock.Children.Add(bar);
        dock.Children.Add(title);
        dock.Children.Add(_position);
        dock.Children.Add(waveBorder);
        if (data.Peaks is null)
        {
            var note = PreviewUi.Secondary("No waveform for MP3 (Windows plays it directly).");
            DockPanel.SetDock(note, Dock.Bottom);
            dock.Children.Insert(0, note);
        }
        Content = dock;
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        Focusable = true;
        KeyDown += (_, e) => { if (e.Key == Key.Space) { TogglePlay(); e.Handled = true; } };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;
        _player.MediaOpened += OnOpened;
        _player.MediaEnded += OnEnded;
        _player.MediaFailed += OnFailed;
        _player.Volume = Volume;
        _player.Open(new Uri(data.TempPath));
        Unloaded += (_, _) => Pause();
    }

    public bool IsPlaying => _playing;
    /// <summary>The player's position now.</summary>
    public TimeSpan PlayerPosition => _disposed ? TimeSpan.Zero : _player.Position;
    public TimeSpan? Duration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : _data.Duration;
    public string? Failure { get; private set; }

    private void OnOpened(object? sender, EventArgs e) => UpdateTime();

    private void OnEnded(object? sender, EventArgs e)
    {
        Stop();
    }

    private void OnFailed(object? sender, ExceptionEventArgs e)
    {
        Failure = e.ErrorException?.Message ?? "unknown error";
        _status.Text = "Cannot play: " + Failure;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Severity.Warning");
        Pause();
    }

    public void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
    }

    public void Play()
    {
        if (_disposed) return;
        _player.Play();
        _playing = true;
        _play.Content = "Pause";
        _timer.Start();
    }

    public void Pause()
    {
        if (_disposed) return;
        _player.Pause();
        _playing = false;
        _play.Content = "Play";
        _timer.Stop();
        UpdateTime();
    }

    public void Stop()
    {
        if (_disposed) return;
        _player.Stop();
        _playing = false;
        _play.Content = "Play";
        _timer.Stop();
        UpdateTime();
    }

    private void SeekTo(double fraction)
    {
        if (_disposed || _dragging || Duration is not { } d) return;
        _player.Position = TimeSpan.FromTicks((long)(d.Ticks * Math.Clamp(fraction, 0, 1)));
        UpdateTime();
    }

    private void OnTick(object? sender, EventArgs e) => UpdateTime();

    private void UpdateTime()
    {
        if (_disposed) return;
        var position = _player.Position;
        var duration = Duration;
        double fraction = duration is { Ticks: > 0 } d ? Math.Clamp((double)position.Ticks / d.Ticks, 0, 1) : 0;
        _updating = true;
        if (!_dragging) _position.Value = fraction;
        _updating = false;
        _wave.Position = fraction;
        _time.Text = PreviewUi.Time(position) + " / " + (duration is { } t ? PreviewUi.Time(t) : "?");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Tick -= OnTick;
        _player.MediaOpened -= OnOpened;
        _player.MediaEnded -= OnEnded;
        _player.MediaFailed -= OnFailed;
        _player.Stop();
        _player.Close();
        _playing = false;
        _disposed = true;
        _data.Dispose();
    }
}
