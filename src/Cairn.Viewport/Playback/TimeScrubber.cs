using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Cairn.Viewport;

/// <summary>
/// The transport's time bar: the clip's range from start to end with the ramp-in and ramp-out regions
/// shaded, frame ticks, a mark per key time, and the playhead. Click or drag to scrub (playback
/// pauses); arrow keys step a frame (Shift: ten), Home/End jump to the ends; Alt+drag scrubs without
/// snapping to frames.
/// </summary>
/// <remarks>
/// Two visuals: the track (redrawn only when the range, keys, size, unit or theme change) and the
/// playhead, which is drawn once and moved with a <see cref="TranslateTransform"/>, so playback costs
/// one property write per frame rather than a redraw.
/// </remarks>
public sealed class TimeScrubber : FrameworkElement
{
    /// <summary>The theme service whose changes redraw every scrubber (the shell sets it once at start-up).</summary>
    public static Cairn.Ui.Services.ThemeService? ThemeSource { get; set; }

    private const double Inset = 8;
    private readonly DrawingVisual _track = new();
    private readonly DrawingVisual _playhead = new();
    private readonly TranslateTransform _playheadOffset = new();
    private bool _dragging;
    private PlaybackViewModel? _playback;
    private bool _trackDirty = true;

    public TimeScrubber()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Height = 30;
        Cursor = Cursors.Hand;
        _playhead.Transform = _playheadOffset;
        AddVisualChild(_track);
        AddVisualChild(_playhead);
        DataContextChanged += (_, _) => Attach(DataContext as PlaybackViewModel);
        SizeChanged += (_, _) => Redraw();
        // Weak: a scrubber whose Unloaded never fires (tab content dropped) must not be kept alive by the theme service.
        if (ThemeSource is { } theme) WeakEventManager<Cairn.Ui.Services.ThemeService, EventArgs>.AddHandler(theme, nameof(theme.ThemeChanged), OnThemeChanged);
        Loaded += (_, _) => Redraw();
        AutomationProperties.SetName(this, "Time scrubber");
        ToolTip = "Click or drag to move the playhead (Alt: between frames). Arrow keys step a frame (Shift: ten frames); Home and End jump to the ends. Shaded: ramp in and ramp out.";
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index == 0 ? _track : _playhead;

    private void OnThemeChanged(object? sender, EventArgs e) => Redraw();

    private void Attach(PlaybackViewModel? playback)
    {
        if (_playback is not null)
        {
            _playback.TimeChanged -= OnTimeChanged;
            _playback.PropertyChanged -= OnPlaybackPropertyChanged;
        }
        _playback = playback;
        if (_playback is not null)
        {
            _playback.TimeChanged += OnTimeChanged;
            _playback.PropertyChanged += OnPlaybackPropertyChanged;
        }
        Redraw();
    }

    private void OnTimeChanged(object? sender, EventArgs e) => MovePlayhead();

    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.StartTime) or nameof(PlaybackViewModel.EndTime)
            or nameof(PlaybackViewModel.KeyTimes) or nameof(PlaybackViewModel.HasClip) or nameof(PlaybackViewModel.RampIn)
            or nameof(PlaybackViewModel.RampOut) or nameof(PlaybackViewModel.UnitSuffix))
        {
            Redraw();
        }
    }

    /// <summary>Redraws the track and the playhead (range, keys, size, unit or theme changed).</summary>
    public void Redraw()
    {
        _trackDirty = true;
        DrawTrack();
        DrawPlayhead();
        MovePlayhead();
    }

    private double X(double ticks)
    {
        var p = _playback!;
        double span = Math.Max(1, p.EndTime - p.StartTime);
        return Inset + (ticks - p.StartTime) / span * Math.Max(1, ActualWidth - 2 * Inset);
    }

    private double Ticks(double x)
    {
        var p = _playback!;
        double span = Math.Max(1, p.EndTime - p.StartTime);
        return p.StartTime + (x - Inset) / Math.Max(1, ActualWidth - 2 * Inset) * span;
    }

    private void DrawTrack()
    {
        if (!_trackDirty) return;
        _trackDirty = false;
        double w = ActualWidth, h = ActualHeight;
        using var dc = _track.RenderOpen();
        if (w < 2 || h < 2) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var trackRect = new Rect(Inset, 7, Math.Max(0, w - 2 * Inset), h - 14);
        dc.DrawRoundedRectangle(Brush("Scrubber.Track"), null, trackRect, 3, 3);
        if (IsKeyboardFocused)
            dc.DrawRoundedRectangle(null, new Pen(Brush("App.Accent"), 1.5), new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2)), 3, 3);
        if (_playback is not { HasClip: true } p) return;

        var ramp = Brush("Scrubber.Ramp");
        double start = p.StartTime, end = p.EndTime;
        if (p.RampIn > 0)
        {
            double x1 = Math.Min(X(start + p.RampIn), X(end));
            dc.DrawRectangle(ramp, null, new Rect(X(start), trackRect.Top, Math.Max(0, x1 - X(start)), trackRect.Height));
        }
        if (p.RampOut > 0)
        {
            // Ramp-in wins where the two overlap, as in the engine.
            double x0 = Math.Max(X(end - p.RampOut), X(start + p.RampIn));
            if (x0 < X(end)) dc.DrawRectangle(ramp, null, new Rect(x0, trackRect.Top, X(end) - x0, trackRect.Height));
        }

        var tickPen = new Pen(Brush("Scrubber.Tick"), 1);
        tickPen.Freeze();
        double frames = (end - start) / p.TimeBase.TicksPerFrame;
        double pxPerFrame = frames > 0 ? trackRect.Width / frames : trackRect.Width;
        int step = Math.Max(1, (int)Math.Ceiling(5 / Math.Max(0.01, pxPerFrame)));
        for (int f = 0; f <= frames; f += step)
        {
            double x = X(start + f * p.TimeBase.TicksPerFrame);
            bool major = f % (step * 5) == 0;
            dc.DrawLine(tickPen, new Point(x, trackRect.Bottom - (major ? 7 : 4)), new Point(x, trackRect.Bottom));
        }

        var keyBrush = Brush("Scrubber.KeyMark");
        double last = double.NegativeInfinity;
        foreach (int k in p.KeyTimes)
        {
            double x = X(k);
            if (x - last < 2) continue;
            last = x;
            dc.DrawRectangle(keyBrush, null, new Rect(x - 0.75, trackRect.Top + 1, 1.5, 4));
        }

        var unit = p.Unit;
        var secondary = Brush("App.SecondaryText");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var startText = new FormattedText(TimeFormat.Number(start, unit, p.TimeBase), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, secondary, dpi);
        var endText = new FormattedText(TimeFormat.Number(end, unit, p.TimeBase), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, secondary, dpi);
        dc.DrawText(startText, new Point(trackRect.Left + 3, trackRect.Top + (trackRect.Height - startText.Height) / 2));
        dc.DrawText(endText, new Point(trackRect.Right - endText.Width - 3, trackRect.Top + (trackRect.Height - endText.Height) / 2));
    }

    private void DrawPlayhead()
    {
        using var dc = _playhead.RenderOpen();
        if (_playback is not { HasClip: true } || ActualHeight < 2) return;
        var accent = Brush("Scrubber.Current");
        double h = ActualHeight;
        dc.DrawRectangle(accent, null, new Rect(-1, 2, 2, h - 4));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(-5, 1), true, true);
            g.LineTo(new Point(5, 1), false, false);
            g.LineTo(new Point(0, 7), false, false);
        }
        head.Freeze();
        dc.DrawGeometry(accent, null, head);
    }

    private void MovePlayhead()
    {
        if (_playback is not { HasClip: true } p || ActualWidth < 2) return;
        _playheadOffset.X = X(p.Time);
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_playback is not { HasClip: true } || e.ChangedButton != MouseButton.Left) return;
        Focus();
        _playback.Pause();
        _dragging = CaptureMouse();
        _playback.Seek(SnapIfNeeded(Ticks(e.GetPosition(this).X)));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _playback is null) return;
        _playback.Seek(SnapIfNeeded(Ticks(e.GetPosition(this).X)));
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    /// <summary>Scrubbing lands on whole frames unless Alt is held.</summary>
    private float SnapIfNeeded(double ticks)
    {
        if (_playback is null) return (float)ticks;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return (float)ticks;
        double frame = Math.Round((ticks - _playback.StartTime) / _playback.TimeBase.TicksPerFrame);
        return (float)(_playback.StartTime + frame * _playback.TimeBase.TicksPerFrame);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_playback is not { HasClip: true } p) return;
        int frames = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Left: p.Pause(); p.Seek((float)(p.Time - frames * p.TimeBase.TicksPerFrame)); break;
            case Key.Right: p.Pause(); p.Seek((float)(p.Time + frames * p.TimeBase.TicksPerFrame)); break;
            case Key.Home: p.FirstCommand.Execute(null); break;
            case Key.End: p.LastCommand.Execute(null); break;
            case Key.Space: p.TogglePlay(); break;
            default: return;
        }
        e.Handled = true;
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Redraw();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Redraw();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
