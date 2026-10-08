using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace Cairn.Snd.Ui.Documents;

/// <summary>
/// The waveform of a sound: one lane per channel (minimum to maximum per pixel column), a time ruler, the loop region
/// and the play head. Zoomed and scrolled by its owner (<see cref="FirstFrame"/>, <see cref="FramesPerPixel"/>); the
/// mouse wheel zooms around the pointer, Shift+wheel scrolls, a click moves the play position.
/// </summary>
public sealed class SndWaveformView : FrameworkElement
{
    private const double RulerHeight = 18;
    private readonly SndDocument _doc;
    private double _firstFrame, _framesPerPixel = 1;
    private long _playhead;
    private float[] _min = [], _max = [];

    public SndWaveformView(SndDocument doc)
    {
        _doc = doc;
        MinHeight = 120;
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.IBeam;
        ToolTip = "Click to move the play position; mouse wheel zooms, Shift+wheel scrolls";
        AutomationProperties.SetName(this, "Waveform");
    }

    /// <summary>Raised with a frame when the user clicks the waveform.</summary>
    public event Action<long>? Seek;
    /// <summary>Raised when the wheel asks to zoom: (factor, frame under the pointer, pointer x).</summary>
    public event Action<double, double, double>? ZoomRequested;
    /// <summary>Raised when Shift+wheel asks to scroll by a number of pixels.</summary>
    public event Action<double>? ScrollRequested;

    /// <summary>The frame at the left edge.</summary>
    public double FirstFrame { get => _firstFrame; set { if (_firstFrame != value) { _firstFrame = value; InvalidateVisual(); } } }

    /// <summary>Frames per pixel column (zoom).</summary>
    public double FramesPerPixel { get => _framesPerPixel; set { value = Math.Max(1.0 / 64, value); if (_framesPerPixel != value) { _framesPerPixel = value; InvalidateVisual(); } } }

    /// <summary>The play head's frame.</summary>
    public long Playhead { get => _playhead; set { if (_playhead != value) { _playhead = value; InvalidateVisual(); } } }

    /// <summary>The frame at x (pixels).</summary>
    public double FrameAt(double x) => _firstFrame + x * _framesPerPixel;

    /// <summary>The x (pixels) of a frame.</summary>
    public double XOf(double frame) => (frame - _firstFrame) / _framesPerPixel;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Seek?.Invoke((long)Math.Clamp(Math.Round(FrameAt(e.GetPosition(this).X)), 0, _doc.Sound.FrameCount));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        double x = e.GetPosition(this).X;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) ScrollRequested?.Invoke(-e.Delta / 120.0 * ActualWidth / 8);
        else ZoomRequested?.Invoke(e.Delta > 0 ? 1 / 1.5 : 1.5, FrameAt(x), x);
        e.Handled = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        Brush Find(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
        var background = Find("Preview.Background", Brushes.Black);
        var wave = Find("App.Accent", Brushes.SteelBlue);
        var line = Find("App.SubtleBorder", Brushes.Gray);
        var text = Find("App.SecondaryText", Brushes.Gray);
        var loopFill = Find("App.AccentSoft", Brushes.DarkSlateBlue);
        var head = Find("Severity.Warning", Brushes.Orange);
        var chrome = Find("App.ChromeBackground", Brushes.DimGray);
        dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
        dc.DrawRectangle(chrome, null, new Rect(0, 0, w, RulerHeight));

        var sound = _doc.Sound;
        long frames = sound.FrameCount;
        int channels = Math.Max(1, sound.Channels);
        double laneHeight = (h - RulerHeight) / channels;
        double endX = Math.Min(w, XOf(frames));

        // the loop region under everything
        if (sound.Loop is { } loop)
        {
            double a = Math.Max(0, XOf(loop.Start)), b = Math.Min(w, XOf(loop.End));
            if (b > a)
            {
                var fill = loopFill.Clone();
                fill.Opacity = _doc.Looping ? 0.55 : 0.25;
                fill.Freeze();
                dc.DrawRectangle(fill, null, new Rect(a, RulerHeight, b - a, h - RulerHeight));
                var pen = new Pen(wave, 1) { DashStyle = DashStyles.Dash };
                pen.Freeze();
                dc.DrawLine(pen, new Point(Math.Round(XOf(loop.Start)) + 0.5, RulerHeight), new Point(Math.Round(XOf(loop.Start)) + 0.5, h));
                dc.DrawLine(pen, new Point(Math.Round(XOf(loop.End)) + 0.5, RulerHeight), new Point(Math.Round(XOf(loop.End)) + 0.5, h));
            }
        }

        int columns = (int)Math.Ceiling(w);
        if (_min.Length < columns) { _min = new float[columns]; _max = new float[columns]; }
        var linePen = new Pen(line, 1);
        linePen.Freeze();
        var wavePen = new Pen(wave, 1);
        wavePen.Freeze();
        for (int c = 0; c < channels; c++)
        {
            double top = RulerHeight + c * laneHeight, mid = top + laneHeight / 2, half = laneHeight / 2 - 3;
            if (c > 0) dc.DrawLine(linePen, new Point(0, top + 0.5), new Point(w, top + 0.5));
            dc.DrawLine(linePen, new Point(0, Math.Round(mid) + 0.5), new Point(Math.Max(0, endX), Math.Round(mid) + 0.5));
            if (frames == 0) continue;
            int count = (int)Math.Max(0, Math.Min(columns, Math.Ceiling(endX)));
            _doc.Peaks.Compute(c, _firstFrame, _framesPerPixel, _min.AsSpan(0, count), _max.AsSpan(0, count));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                for (int x = 0; x < count; x++)
                {
                    double y1 = mid - _max[x] * half, y2 = mid - _min[x] * half;
                    if (y2 - y1 < 1) { y1 -= 0.5; y2 += 0.5; }
                    g.BeginFigure(new Point(x + 0.5, y1), false, false);
                    g.LineTo(new Point(x + 0.5, y2), true, false);
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, wavePen, geometry);
            if (channels == 2)
            {
                var label = new FormattedText(c == 0 ? "L" : "R", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, text, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(label, new Point(4, top + 2));
            }
        }

        DrawRuler(dc, w, text, linePen);
        double px = Math.Round(XOf(_playhead)) + 0.5;
        if (px >= 0 && px <= w)
        {
            var headPen = new Pen(head, 1.5);
            headPen.Freeze();
            dc.DrawLine(headPen, new Point(px, 0), new Point(px, h));
        }
    }

    /// <summary>Time ticks at a step that leaves room for the labels.</summary>
    private void DrawRuler(DrawingContext dc, double w, Brush text, Pen pen)
    {
        int rate = Math.Max(1, _doc.Sound.SampleRate);
        double secondsPerPixel = _framesPerPixel / rate;
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s / secondsPerPixel >= 70, 600);
        double firstSecond = _firstFrame / rate;
        double t = Math.Ceiling(firstSecond / step) * step;
        double dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (; ; t += step)
        {
            double x = XOf(t * rate);
            if (x > w) break;
            dc.DrawLine(pen, new Point(Math.Round(x) + 0.5, RulerHeight - 5), new Point(Math.Round(x) + 0.5, RulerHeight));
            string label = step < 1 ? t.ToString(step < 0.01 ? "0.000" : step < 0.1 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " s"
                : SndDocument.FormatTime(TimeSpan.FromSeconds(t))[..^4];
            dc.DrawText(new FormattedText(label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, text, dip), new Point(x + 3, 1));
        }
    }
}
