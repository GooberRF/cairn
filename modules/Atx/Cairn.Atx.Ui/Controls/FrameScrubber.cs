using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;

namespace Cairn.Atx.Ui.Controls;

/// <summary>
/// The preview's timeline: one segment per frame, each as wide a share of the track as that frame's
/// share of the loop, so a 500 ms frame next to four 100 ms frames looks like what it sounds like.
/// Clicking or dragging seeks; Left/Right/Home/End do the same from the keyboard, and the control is
/// focusable so a keyboard user can reach it at all.
///
/// Drawn directly rather than built from elements: a 200-frame sequence would otherwise be 200
/// rectangles re-measured on every layout pass, and the playing frame changes many times a second.
/// </summary>
public sealed class FrameScrubber : FrameworkElement
{
    private const double SegmentGap = 1.0;

    private int _hoverIndex = -1;
    private bool _dragging;

    public FrameScrubber()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Height = 22;
        MinWidth = 40;
        Cursor = Cursors.Hand;
        System.Windows.Automation.AutomationProperties.SetName(this, "Frame timeline");
        ToolTip = "The loop timeline. Each frame is as wide as its share of the loop; "
            + "click or drag to jump to a frame.";
    }

    /// <summary>Raised when the user asks to show a different frame.</summary>
    public event EventHandler<int>? SeekRequested;

    // ── Properties ────────────────────────────────────────────────────────────

    /// <summary>
    /// One weight per frame, in frame order — normally the frame's time in milliseconds. Widths are
    /// proportional to these; the list's length is the frame count.
    /// </summary>
    public static readonly DependencyProperty WeightsProperty = DependencyProperty.Register(
        nameof(Weights), typeof(IReadOnlyList<double>), typeof(FrameScrubber),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>One weight per frame, in frame order.</summary>
    public IReadOnlyList<double>? Weights
    {
        get => (IReadOnlyList<double>?)GetValue(WeightsProperty);
        set => SetValue(WeightsProperty, value);
    }

    /// <summary>The frame currently showing, highlighted in the accent colour.</summary>
    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex), typeof(int), typeof(FrameScrubber),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame currently showing.</summary>
    public int CurrentIndex
    {
        get => (int)GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    /// <summary>The frame the user has selected in the list, shown with a thin outline.</summary>
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(FrameScrubber),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The frame selected in the frames list.</summary>
    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>How many frames the track is showing.</summary>
    public int Count => Weights?.Count ?? 0;

    /// <summary>Repaints after a theme swap, since the brushes are resolved at render time.</summary>
    public void RefreshTheme() => InvalidateVisual();

    // ── Input ─────────────────────────────────────────────────────────────────

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Count == 0) return;
        Focus();
        _dragging = true;
        CaptureMouse();
        SeekTo(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Count == 0) return;
        int hover = IndexAt(e.GetPosition(this).X);
        if (hover != _hoverIndex) { _hoverIndex = hover; InvalidateVisual(); }
        if (_dragging) SeekTo(e.GetPosition(this).X);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex == -1) return;
        _hoverIndex = -1;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int count = Count;
        if (count == 0) { base.OnKeyDown(e); return; }
        int current = Math.Clamp(CurrentIndex, 0, count - 1);
        switch (e.Key)
        {
            case Key.Left: Raise(current - 1); e.Handled = true; break;
            case Key.Right: Raise(current + 1); e.Handled = true; break;
            case Key.Home: Raise(0); e.Handled = true; break;
            case Key.End: Raise(count - 1); e.Handled = true; break;
            default: base.OnKeyDown(e); break;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    private void SeekTo(double x) => Raise(IndexAt(x));

    private void Raise(int index)
    {
        int count = Count;
        if (count == 0) return;
        index = Math.Clamp(index, 0, count - 1);
        SeekRequested?.Invoke(this, index);
    }

    /// <summary>
    /// Which frame sits under an x position.
    ///
    /// Past the point where there are more frames than pixels the drawn segments are all one pixel
    /// wide and overlap, so walking their rectangles answers 0 for most of the track. There the
    /// position is read straight off the timeline instead — the share of the loop that x stands
    /// for — which is the same answer the widths were trying to express.
    /// </summary>
    private int IndexAt(double x)
    {
        int count = Count;
        double width = ActualWidth;
        if (count == 0 || width <= 0) return -1;

        if (IsCrowded(count, width))
        {
            var weights = Weights!;
            double total = 0;
            for (int i = 0; i < count; i++) total += Math.Max(0.0001, weights[i]);
            double target = Math.Clamp(x / width, 0, 1) * total;
            double running = 0;
            for (int i = 0; i < count; i++)
            {
                running += Math.Max(0.0001, weights[i]);
                if (target < running) return i;
            }
            return count - 1;
        }

        var bounds = Layout(width);
        if (bounds.Length == 0) return -1;
        for (int i = 0; i < bounds.Length; i++)
        {
            if (x < bounds[i].Right || i == bounds.Length - 1) return i;
        }
        return bounds.Length - 1;
    }

    /// <summary>True when there is less than a pixel of track per frame, so segments cannot be drawn apart.</summary>
    private static bool IsCrowded(int count, double width) => count > width;

    // ── Rendering ─────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var track = Resolve("Scrubber.Track", Brushes.Gainsboro);
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), 3, 3);

        var segment = Resolve("Scrubber.Segment", Brushes.LightSteelBlue);
        var alternate = Resolve("Scrubber.SegmentAlternate", Brushes.AliceBlue);
        var hover = Resolve("Scrubber.Hover", Brushes.SteelBlue);
        var current = Resolve("Scrubber.Current", Brushes.DodgerBlue);
        var outline = new Pen(Resolve("Scrubber.SegmentBorder", Brushes.Gray), 1);
        outline.Freeze();
        var selection = new Pen(Resolve("App.Text", Brushes.Black), 1) { DashStyle = DashStyles.Dot };
        selection.Freeze();

        int currentIndex = CurrentIndex;
        int selectedIndex = SelectedIndex;

        // More frames than pixels: per-frame rectangles would all be the same one-pixel sliver,
        // drawn on top of each other, and the playing frame would be painted over by the next one.
        // One band plus markers says the same thing and stays legible.
        if (IsCrowded(Count, width))
        {
            drawingContext.DrawRoundedRectangle(segment, null, new Rect(0, 0, width, height), 3, 3);
            DrawMarker(drawingContext, width, height, selectedIndex, hover, 2);
            DrawMarker(drawingContext, width, height, _hoverIndex, hover, 2);
            DrawMarker(drawingContext, width, height, currentIndex, current, 3);
            DrawFocus(drawingContext, width, height);
            return;
        }

        var bounds = Layout(width);
        if (bounds.Length == 0) return;

        for (int i = 0; i < bounds.Length; i++)
        {
            var rect = new Rect(bounds[i].Left, 0, Math.Max(1, bounds[i].Right - bounds[i].Left), height);
            var fill = i == currentIndex ? current
                : i == _hoverIndex ? hover
                : (i % 2 == 0 ? segment : alternate);
            drawingContext.DrawRectangle(fill, bounds.Length <= 200 ? outline : null, rect);
            if (i == selectedIndex && i != currentIndex)
            {
                drawingContext.DrawRectangle(null, selection, Rect.Inflate(rect, -1.5, -1.5));
            }
        }

        DrawFocus(drawingContext, width, height);
    }

    private void DrawFocus(DrawingContext drawingContext, double width, double height)
    {
        if (!IsKeyboardFocused) return;
        var focus = new Pen(Resolve("App.Accent", Brushes.DodgerBlue), 2);
        focus.Freeze();
        drawingContext.DrawRoundedRectangle(null, focus, new Rect(1, 1, width - 2, height - 2), 3, 3);
    }

    /// <summary>Paints one frame's position as a thin bar, for a track too crowded to draw segments.</summary>
    private void DrawMarker(
        DrawingContext drawingContext, double width, double height, int index, Brush brush, double thickness)
    {
        int count = Count;
        if (index < 0 || index >= count) return;
        var weights = Weights!;
        double total = 0;
        for (int i = 0; i < count; i++) total += Math.Max(0.0001, weights[i]);
        double before = 0;
        for (int i = 0; i < index; i++) before += Math.Max(0.0001, weights[i]);
        double left = Math.Clamp(before / total * width, 0, Math.Max(0, width - thickness));
        drawingContext.DrawRectangle(brush, null, new Rect(left, 0, thickness, height));
    }

    /// <summary>Left/right edges of every segment, widths proportional to the weights.</summary>
    private (double Left, double Right)[] Layout(double width)
    {
        var weights = Weights;
        int count = weights?.Count ?? 0;
        if (count == 0 || width <= 0) return [];

        double total = 0;
        for (int i = 0; i < count; i++) total += Math.Max(0.0001, weights![i]);
        if (total <= 0) return [];

        var result = new (double, double)[count];
        // Gaps only help while the segments are wide enough to still read as blocks.
        double gap = count * 4 < width ? SegmentGap : 0;
        double x = 0;
        double running = 0;
        for (int i = 0; i < count; i++)
        {
            running += Math.Max(0.0001, weights![i]);
            double right = running / total * width;
            result[i] = (x, Math.Max(x + 1, right - gap));
            x = right;
        }
        return result;
    }

    private Brush Resolve(string key, Brush fallback) =>
        TryFindResource(key) as Brush ?? fallback;

    // ── Accessibility ─────────────────────────────────────────────────────────

    protected override AutomationPeer OnCreateAutomationPeer() => new ScrubberPeer(this);

    /// <summary>Exposes the track as a selection of frames, so a screen reader can drive it.</summary>
    private sealed class ScrubberPeer(FrameScrubber owner) : FrameworkElementAutomationPeer(owner),
        IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(FrameScrubber);

        protected override AutomationControlType GetAutomationControlTypeCore() =>
            AutomationControlType.Slider;

        protected override string GetNameCore() =>
            owner.Count == 0
                ? "Frame timeline, empty"
                : string.Format(
                    CultureInfo.CurrentCulture, "Frame timeline, frame {0} of {1}",
                    Math.Max(0, owner.CurrentIndex), owner.Count);

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public bool IsReadOnly => owner.Count == 0;

        public double LargeChange => Math.Max(1, owner.Count / 10.0);

        public double Maximum => Math.Max(0, owner.Count - 1);

        public double Minimum => 0;

        public double SmallChange => 1;

        public double Value => Math.Max(0, owner.CurrentIndex);

        public void SetValue(double value) => owner.Raise((int)Math.Round(value));
    }
}
