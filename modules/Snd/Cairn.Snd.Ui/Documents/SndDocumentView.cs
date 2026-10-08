using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Cairn.Formats.Audio;

namespace Cairn.Snd.Ui.Documents;

/// <summary>
/// A sound tab: transport (play/pause, stop, loop, volume), the waveform with zoom and scrolling, and the details on the
/// right (format, codec, rate, channels, bit depth, duration, loop, size, the header's fields and the format's quirks).
/// </summary>
public sealed class SndDocumentView : DockPanel
{
    private readonly SndDocument _doc;
    private readonly SndModule _module;
    private readonly DispatcherTimer _timer;
    private readonly Button _play;
    private readonly ToggleButton _loop;
    private readonly Slider _volume;
    private readonly TextBlock _time = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 150 };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Horizontal, Height = 16 };
    private readonly StackPanel _details = new() { Margin = new Thickness(10, 8, 10, 8) };
    private bool _fitted = true, _updatingScroll;

    internal SndDocumentView(SndDocument doc, SndModule module)
    {
        _doc = doc;
        _module = module;
        Wave = new SndWaveformView(doc);
        SetResourceReference(BackgroundProperty, "App.PaneBackground");
        _time.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
        _zoomText.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        AutomationProperties.SetName(_time, "Play position");

        _play = Tool("Play", "Play or pause (Space)", (_, _) => _doc.TogglePlay());
        var stop = Tool("Stop", "Stop and go back to the start", (_, _) => _doc.Stop());
        _loop = new ToggleButton { Content = "Loop", ToolTip = "Repeat the loop (the whole sound when it has no loop points) (L)", Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2), IsChecked = doc.Looping };
        _loop.SetResourceReference(StyleProperty, "ToolToggle");
        AutomationProperties.SetName(_loop, "Loop");
        _loop.Click += (_, _) => _doc.Looping = _loop.IsChecked == true;
        _volume = new Slider { Minimum = 0, Maximum = 1, Width = 100, Value = doc.Volume, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Volume" };
        AutomationProperties.SetName(_volume, "Volume");
        _volume.ValueChanged += (_, e) => _doc.Volume = e.NewValue;
        var zoomIn = Tool("+", "Zoom in (Ctrl+Plus, or the mouse wheel)", (_, _) => ZoomBy(1 / 2.0));
        var zoomOut = Tool("−", "Zoom out (Ctrl+Minus)", (_, _) => ZoomBy(2));
        var fit = Tool("Fit", "Show the whole sound (Ctrl+0)", (_, _) => Fit());
        var convert = Tool("Convert...", "Convert this sound to WAV (Ctrl+Shift+E)", (_, _) => _module.ConvertDocument(_doc));
        var bar = new WrapPanel { Margin = new Thickness(4, 3, 4, 3) };
        foreach (var e in new UIElement[] { _play, stop, _loop, Divider(), _time, Divider(), Secondary("Volume"), _volume, Divider(), zoomIn, zoomOut, fit, _zoomText, Divider(), convert })
            bar.Children.Add(e);
        var barBorder = new Border { Child = bar, BorderThickness = new Thickness(0, 0, 0, 1) };
        barBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        barBorder.SetResourceReference(Border.BackgroundProperty, "App.ChromeBackground");
        SetDock(barBorder, Dock.Top);
        Children.Add(barBorder);

        var detailsScroll = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 330 };
        var detailsBorder = new Border { Child = detailsScroll, BorderThickness = new Thickness(1, 0, 0, 0) };
        detailsBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        SetDock(detailsBorder, Dock.Right);
        Children.Add(detailsBorder);

        SetDock(_scroll, Dock.Bottom);
        AutomationProperties.SetName(_scroll, "Scroll the waveform");
        _scroll.ValueChanged += (_, e) => { if (!_updatingScroll) { _fitted = false; Wave.FirstFrame = e.NewValue; } };
        Children.Add(_scroll);
        var waveBorder = new Border { Child = Wave, Margin = new Thickness(6), BorderThickness = new Thickness(1) };
        waveBorder.SetResourceReference(Border.BorderBrushProperty, "App.SubtleBorder");
        Children.Add(waveBorder);

        Wave.Seek += f => _doc.Seek(f);
        Wave.ZoomRequested += (factor, frame, x) => ZoomAround(factor, frame, x);
        Wave.ScrollRequested += pixels => ScrollTo(Wave.FirstFrame + pixels * Wave.FramesPerPixel);
        Wave.SizeChanged += (_, _) => { if (_fitted) Fit(); else UpdateScroll(); };

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => UpdatePlayback();
        _doc.PlaybackChanged += OnPlaybackChanged;
        _doc.SoundChanged += (_, _) => { FillDetails(); Fit(); UpdatePlayback(); };
        _doc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SndDocument.Looping)) { _loop.IsChecked = _doc.Looping; Wave.InvalidateVisual(); }
            if (e.PropertyName == nameof(SndDocument.Volume) && Math.Abs(_volume.Value - _doc.Volume) > 1e-6) _volume.Value = _doc.Volume;
        };
        Unloaded += (_, _) => _timer.Stop();
        Focusable = true;
        PreviewKeyDown += OnKey;
        FillDetails();
        UpdatePlayback();
    }

    /// <summary>The waveform (for self-tests).</summary>
    public SndWaveformView Wave { get; }

    /// <summary>The details as (label, value) pairs (for self-tests).</summary>
    public IReadOnlyList<(string Label, string Value)> DetailRows { get; private set; } = [];

    /// <summary>The time line ("0:00.250 / 0:01.000").</summary>
    public string TimeText => _time.Text;

    private void OnPlaybackChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnPlaybackChanged(sender, e)); return; }
        if (_doc.IsPlaying) _timer.Start();
        else _timer.Stop();
        UpdatePlayback();
    }

    private void UpdatePlayback()
    {
        long position = _doc.Position;
        Wave.Playhead = position;
        _play.Content = _doc.IsPlaying ? "Pause" : "Play";
        _time.Text = _doc.TimeOf(position) + " / " + SndDocument.FormatTime(_doc.Sound.Duration);
        // keep the play head in view while playing a zoomed-in sound
        if (_doc.IsPlaying && !_fitted)
        {
            double x = Wave.XOf(position);
            if (x < 0 || x > Wave.ActualWidth) ScrollTo(position - Wave.ActualWidth * Wave.FramesPerPixel * 0.1);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) { _doc.TogglePlay(); e.Handled = true; }
        else if (e.Key == Key.L && Keyboard.Modifiers == ModifierKeys.None) { _doc.Looping = !_doc.Looping; e.Handled = true; }
        else if (e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.None) { _doc.Seek(0); e.Handled = true; }
    }

    // ---- zoom and scroll ----------------------------------------------------------------------------------------

    /// <summary>Shows the whole sound.</summary>
    public void Fit()
    {
        double width = Math.Max(1, Wave.ActualWidth > 0 ? Wave.ActualWidth : 800);
        Wave.FramesPerPixel = Math.Max(1.0 / 64, Math.Max(1, _doc.Sound.FrameCount) / width);
        Wave.FirstFrame = 0;
        _fitted = true;
        UpdateScroll();
    }

    /// <summary>Shows <paramref name="framesPerPixel"/> from <paramref name="firstFrame"/> (diagnostic options).</summary>
    public void SetView(double framesPerPixel, double firstFrame)
    {
        Wave.FramesPerPixel = framesPerPixel;
        _fitted = false;
        ScrollTo(firstFrame);
    }

    /// <summary>Zooms by <paramref name="factor"/> (below 1 zooms in) around the middle.</summary>
    public void ZoomBy(double factor)
    {
        double width = Math.Max(1, Wave.ActualWidth);
        ZoomAround(factor, Wave.FrameAt(width / 2), width / 2);
    }

    private void ZoomAround(double factor, double frame, double x)
    {
        double width = Math.Max(1, Wave.ActualWidth);
        double fitted = Math.Max(1, _doc.Sound.FrameCount) / width;
        double next = Math.Clamp(Wave.FramesPerPixel * factor, 1.0 / 64, Math.Max(fitted, 1.0 / 64));
        if (next >= fitted - 1e-9) { Fit(); return; }
        Wave.FramesPerPixel = next;
        _fitted = false;
        ScrollTo(frame - x * next);
    }

    private void ScrollTo(double firstFrame)
    {
        double visible = Math.Max(1, Wave.ActualWidth) * Wave.FramesPerPixel;
        Wave.FirstFrame = Math.Clamp(firstFrame, 0, Math.Max(0, _doc.Sound.FrameCount - visible));
        UpdateScroll();
    }

    private void UpdateScroll()
    {
        _updatingScroll = true;
        double visible = Math.Max(1, Wave.ActualWidth) * Wave.FramesPerPixel;
        double max = Math.Max(0, _doc.Sound.FrameCount - visible);
        _scroll.Minimum = 0;
        _scroll.Maximum = max;
        _scroll.ViewportSize = visible;
        _scroll.LargeChange = visible * 0.9;
        _scroll.SmallChange = visible * 0.1;
        _scroll.Value = Math.Clamp(Wave.FirstFrame, 0, max);
        _scroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
        _updatingScroll = false;
        double perPixel = Wave.FramesPerPixel;
        _zoomText.Text = perPixel >= 1
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} samples per pixel", perPixel)
            : string.Format(CultureInfo.CurrentCulture, "{0:N0} pixels per sample", 1 / perPixel);
    }

    // ---- details ------------------------------------------------------------------------------------------------

    private void FillDetails()
    {
        _details.Children.Clear();
        var s = _doc.Sound;
        var rows = new List<(string, string)>();
        void Heading(string text)
        {
            var t = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _details.Children.Count == 0 ? 0 : 14, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            _details.Children.Add(t);
        }
        void Row(string label, string value)
        {
            rows.Add((label, value));
            var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var l = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            l.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            v.SetResourceReference(TextBlock.ForegroundProperty, "App.Text");
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            _details.Children.Add(grid);
        }
        void Para(string text, string brush = "App.SecondaryText")
        {
            var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            _details.Children.Add(t);
        }

        var inv = CultureInfo.CurrentCulture;
        Heading("Sound");
        Row("Format", s.Format);
        Row("Codec", s.Codec);
        Row("Sample rate", s.ExactRate is { } exact
            ? string.Format(inv, "{0:N0} Hz (plays at {1:N1} Hz)", s.SampleRate, exact)
            : string.Format(inv, "{0:N0} Hz", s.SampleRate));
        Row("Channels", s.Channels switch { 1 => "1 (mono)", 2 => "2 (stereo)", _ => s.Channels.ToString(inv) });
        Row("Bit depth", s.BitDepth);
        Row("Duration", SndDocument.FormatTime(s.Duration));
        Row("Samples", string.Format(inv, "{0:N0} per channel", s.FrameCount));
        Row("Loop", s.Loop is { } loop
            ? (loop.IsWhole(s.FrameCount) ? "the whole sound" : string.Format(inv, "samples {0:N0} to {1:N0}", loop.Start, loop.End)) + (loop.Enabled ? "" : " (not used)")
            : "none");
        if (s.Loop is { } l2) Row("Loop from", l2.Source);
        Row("Size", string.Format(inv, "{0:N0} bytes", s.FileSize));

        var header = s.Details.Where(d => d.Label is not ("Sample rate")).ToList();
        if (header.Count > 0)
        {
            Heading(s.Format.StartsWith("PS2", StringComparison.Ordinal) ? "Header and layout" : "File");
            foreach (var (label, value) in header) Row(label, value);
        }
        if (s.Quirks.Count > 0)
        {
            Heading("About this format");
            foreach (var quirk in s.Quirks) Para("• " + quirk);
        }
        var problems = s.Problems.Where(p => p.Severity != SoundSeverity.Info).ToList();
        if (problems.Count > 0)
        {
            Heading("Problems");
            foreach (var p in problems) Para($"{p.Code}: {p.Message}", "Severity.Warning");
        }
        DetailRows = rows;
    }

    // ---- toolbar helpers ----------------------------------------------------------------------------------------

    private static Button Tool(string text, string tip, RoutedEventHandler click)
    {
        var b = new Button { Content = text, ToolTip = tip, Margin = new Thickness(2), Padding = new Thickness(8, 2, 8, 2), MinWidth = 32 };
        b.SetResourceReference(StyleProperty, "ToolButton");
        AutomationProperties.SetName(b, text);
        b.Click += click;
        return b;
    }

    private static Separator Divider()
    {
        var s = new Separator { Margin = new Thickness(6, 4, 6, 4) };
        s.SetResourceReference(StyleProperty, ToolBar.SeparatorStyleKey);
        return s;
    }

    private static TextBlock Secondary(string text)
    {
        var t = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "App.SecondaryText");
        return t;
    }
}
