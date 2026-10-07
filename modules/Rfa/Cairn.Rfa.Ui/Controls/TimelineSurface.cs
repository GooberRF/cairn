using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Ui.Controls;

/// <summary>
/// The dope sheet: one custom-drawn, virtualised element (no element per row or key). Left, the row
/// headers (bone name with hierarchy indent and expand arrow, lint marker, weight); top, the time ruler
/// in the user's unit with the clip's start/end handles and the shaded ramps; below, one row per
/// visible bone with its rotation keys (diamonds, upper half) and position keys (squares, lower half),
/// and the summary row pinned on top (it never scrolls away; the bone rows scroll under it). Only the rows and keys inside the view are drawn, keys closer than a
/// pixel are merged, and each mark style is one <see cref="StreamGeometry"/>, so a redraw costs the
/// same on the largest stock clip as on a small one.
/// </summary>
/// <remarks>
/// Four visual layers: the background (header, ruler, stripes, shading), the keys, an overlay (hover,
/// selection box, drag readout) and the playhead, which is drawn once and moved by a transform so
/// playback never redraws the rest. Mouse: click / Ctrl-click / Shift-click / box select keys; drag
/// keys to move them (snapped to frames; Alt: free; Ctrl: copy); drag a selection edge grip on the
/// ruler, or Alt+drag the first/last selected key, to scale about the playhead; click or drag the ruler
/// to seek; drag the start/end handles to change the clip's range; Ctrl+wheel zooms about the cursor,
/// Shift+wheel or middle-drag pans; double-click a key to seek to it and open the Key inspector.
/// </remarks>
public sealed class TimelineSurface : FrameworkElement
{
    public const double RulerHeight = 28;
    public const double RowHeight = 22;
    public const double MinHeaderWidth = 140;
    private const double KeyHit = 6;
    private const double IndentStep = 12;
    private const double MergePx = 2.0;

    private readonly DrawingVisual _background = new();
    private readonly DrawingVisual _keys = new();
    private readonly DrawingVisual _overlay = new();
    private readonly DrawingVisual _playhead = new();
    private readonly TranslateTransform _playheadOffset = new();
    private readonly Dictionary<string, FormattedText> _text = new(StringComparer.Ordinal);
    private readonly ToolTip _toolTip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    private TimelineViewModel? _model;
    private double _headerWidth = 230;
    private double _pxPerTick = 0.05;
    private double _timeLeft;
    private double _scrollY;
    private bool _fitted;
    private double _dpi = 1;
    private Brushes? _b;

    // Interaction
    private enum Gesture { None, Pending, BoxSelect, MoveKeys, ScaleKeys, Scrub, Handle, Pan, HeaderResize }
    private Gesture _gesture;
    private Point _down;
    private Point _last;
    private KeyRef? _hoverKey;
    private int _hoverSummaryTime = int.MinValue;
    private int _hoverRow = -1;
    private Rect? _box;
    private bool _boxAdd;
    private KeySelection _boxBase = KeySelection.Empty;
    private int _grabTime;
    private bool _scaleFromGrip;
    private int _downRow = -1;
    private KeyRef? _downKey;
    private int _downSummaryTime = int.MinValue;
    private string? _toolTipText;

    public TimelineSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _playhead.Transform = _playheadOffset;
        AddVisualChild(_background);
        AddVisualChild(_keys);
        AddVisualChild(_overlay);
        AddVisualChild(_playhead);
        ToolTip = _toolTip;
        ToolTipService.SetInitialShowDelay(this, 700);
        ToolTipService.SetBetweenShowDelay(this, 200);
        _toolTip.Opened += (_, _) => _toolTip.Content = _toolTipText ?? DefaultToolTip;
        AutomationProperties.SetName(this, "Timeline: keys of every bone over time");
        DataContextChanged += (_, _) => Attach(DataContext as TimelineViewModel);
        SizeChanged += (_, _) => { ClampScroll(); RedrawAll(); };
        Loaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged += OnThemeChanged;
            RedrawAll();
        };
        Unloaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged -= OnThemeChanged;
        };
    }

    private const string DefaultToolTip =
        "Click a key to select it (Ctrl: toggle, Shift: add); drag to box-select. Drag keys to move them (snaps to frames; Alt: free; Ctrl: copy). "
        + "Drag a selection grip on the ruler (or Alt+drag the first/last selected key) to scale about the playhead. "
        + "Delete, Ctrl+C/X/V, Ctrl+Shift+V paste mirrored, K keys the selected bones at the playhead, Ctrl+A selects all, Home fits. "
        + "Ctrl+wheel zooms, Shift+wheel or middle-drag pans. Double-click a key to open it in the Key inspector.";

    /// <summary>Milliseconds the last full redraw (background + keys) took.</summary>
    public double LastRedrawMs { get; private set; }

    /// <summary>The slowest full redraw since the surface was created.</summary>
    public double MaxRedrawMs { get; private set; }

    /// <summary>Marks drawn by the last key redraw (after culling and merging).</summary>
    public int LastMarksDrawn { get; private set; }

    /// <summary>Raised when the scroll extents or offsets change (the view updates its scroll bars).</summary>
    public event EventHandler? ScrollChanged;

    /// <summary>
    /// Rows pinned under the ruler that do not scroll: the summary ("All keys") row when it leads the
    /// visible rows (always, today), else 0.
    /// </summary>
    public int PinnedRows => _model is { } m && m.VisibleRows.Count > 0 && m.VisibleRows[0].IsSummary ? 1 : 0;

    /// <summary>The top of the scrolling rows (below the ruler and the pinned rows), in DIPs.</summary>
    public double ScrollTop => RulerHeight + PinnedRows * RowHeight;

    /// <summary>Total height of the scrolling rows (every visible row but the pinned ones), in DIPs.</summary>
    public double ContentHeight => Math.Max(0, (_model?.VisibleRows.Count ?? 0) - PinnedRows) * RowHeight;

    /// <summary>Height available for the scrolling rows.</summary>
    public double ViewportHeight => Math.Max(0, ActualHeight - ScrollTop);

    /// <summary>Vertical offset of the rows, in DIPs.</summary>
    public double VerticalOffset => _scrollY;

    /// <summary>Width of the key area.</summary>
    public double BodyWidth => Math.Max(1, ActualWidth - _headerWidth);

    /// <summary>The time at the left edge of the key area, in ticks.</summary>
    public double TimeLeft => _timeLeft;

    /// <summary>Ticks covered by the key area.</summary>
    public double VisibleTicks => BodyWidth / _pxPerTick;

    /// <summary>The scrollable time range (the clip with some margin), in ticks.</summary>
    public (double Min, double Max) TimeExtent
    {
        get
        {
            if (_model is null) return (0, 1);
            var c = _model.Clip;
            double pad = Math.Max(RfaClip.TicksPerFrame * 4, (c.EndTime - c.StartTime) * 0.25);
            double min = Math.Min(c.StartTime, KeyMin(c)) - pad, max = Math.Max(c.EndTime, KeyMax(c)) + pad;
            return (min, Math.Max(min + 1, max));
        }
    }

    protected override int VisualChildrenCount => 4;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _background,
        1 => _keys,
        2 => _overlay,
        _ => _playhead,
    };

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    // ── Model ────────────────────────────────────────────────────────────────

    private void Attach(TimelineViewModel? model)
    {
        if (_model is not null)
        {
            // A tab switch in the middle of a key drag: finish it on the document it belongs to, so that
            // document is not left with an open coalesced edit.
            if (_gesture is Gesture.MoveKeys or Gesture.ScaleKeys or Gesture.Handle) _model.EndDrag();
            _gesture = Gesture.None;
            if (IsMouseCaptured) ReleaseMouseCapture();
            _model.Changed -= OnModelChanged;
            _model.FitRequested -= OnFitRequested;
            _model.Playback.TimeChanged -= OnTimeChanged;
            _model.Playback.PropertyChanged -= OnPlaybackPropertyChanged;
        }
        _model = model;
        _fitted = false;
        if (_model is not null)
        {
            _model.Changed += OnModelChanged;
            _model.FitRequested += OnFitRequested;
            _model.Playback.TimeChanged += OnTimeChanged;
            _model.Playback.PropertyChanged += OnPlaybackPropertyChanged;
        }
        RedrawAll();
    }

    private void OnModelChanged(object? sender, TimelineChange change)
    {
        switch (change)
        {
            case TimelineChange.Selection:
                DrawBackground();
                DrawKeys();
                DrawOverlay();
                break;
            case TimelineChange.Keys:
                ClampScroll();
                RedrawAll();
                break;
            case TimelineChange.BoneSelection:
                // A bone picked elsewhere (viewport, Problems) scrolls its row into view.
                if (_model is { } m && IndexOfRow(m.Document.Selection.Active) is int row and >= 0 && !IsRowFullyVisible(row))
                {
                    BringRowIntoView(row);
                }
                else RedrawAll();
                break;
            default:
                ClampScroll();
                RedrawAll();
                break;
        }
    }

    private void OnFitRequested(object? sender, EventArgs e) => Fit();

    private void OnTimeChanged(object? sender, EventArgs e) => MovePlayhead();

    private void OnPlaybackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.UnitSuffix))
        {
            _text.Clear();
            RedrawAll();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _b = null;
        _text.Clear();
        RedrawAll();
    }

    // ── Coordinates ──────────────────────────────────────────────────────────

    private double X(double ticks) => _headerWidth + (ticks - _timeLeft) * _pxPerTick;

    private double Ticks(double x) => _timeLeft + (x - _headerWidth) / _pxPerTick;

    private int RowAt(double y)
    {
        if (_model is null || y < RulerHeight) return -1;
        int pinned = PinnedRows;
        double scrollTop = RulerHeight + pinned * RowHeight;
        int i = y < scrollTop
            ? (int)Math.Floor((y - RulerHeight) / RowHeight)
            : pinned + (int)Math.Floor((y - scrollTop + _scrollY) / RowHeight);
        return i >= 0 && i < _model.VisibleRows.Count ? i : -1;
    }

    private double RowTop(int index)
    {
        int pinned = PinnedRows;
        return index < pinned
            ? RulerHeight + index * RowHeight
            : RulerHeight + pinned * RowHeight + (index - pinned) * RowHeight - _scrollY;
    }

    /// <summary>The visible row index under a y coordinate (pinned rows included), or -1 (diagnostics).</summary>
    public int RowIndexAt(double y) => RowAt(y);

    /// <summary>The top of a visible row on screen, in DIPs (diagnostics).</summary>
    public double RowTopOf(int index) => RowTop(index);

    /// <summary>The scrolling rows on screen (first, last; last &lt; first when none).</summary>
    private (int First, int Last) ScrollingRange()
    {
        int count = _model?.VisibleRows.Count ?? 0;
        int pinned = PinnedRows;
        int first = pinned + Math.Max(0, (int)Math.Floor(_scrollY / RowHeight));
        int last = Math.Min(count - 1, pinned + (int)Math.Ceiling((_scrollY + ViewportHeight) / RowHeight));
        return (first, last);
    }

    /// <summary>True when a row is entirely on screen (pinned rows always are).</summary>
    private bool IsRowFullyVisible(int index)
    {
        int pinned = PinnedRows;
        if (index < pinned) return true;
        double top = (index - pinned) * RowHeight;
        return top >= _scrollY && top + RowHeight <= _scrollY + ViewportHeight;
    }

    private static double RotY(double top) => top + RowHeight * 0.36;

    private static double PosY(double top) => top + RowHeight * 0.74;

    private static int KeyMin(RfaClip c)
    {
        int m = c.StartTime;
        foreach (var b in c.Bones)
        {
            if (b.RotationKeys.Length > 0) m = Math.Min(m, b.RotationKeys[0].Time);
            if (b.PositionKeys.Length > 0) m = Math.Min(m, b.PositionKeys[0].Time);
        }
        return m;
    }

    private static int KeyMax(RfaClip c)
    {
        int m = c.EndTime;
        foreach (var b in c.Bones)
        {
            if (b.RotationKeys.Length > 0) m = Math.Max(m, b.RotationKeys[^1].Time);
            if (b.PositionKeys.Length > 0) m = Math.Max(m, b.PositionKeys[^1].Time);
        }
        return m;
    }

    // ── Scrolling and zoom (called by the view's scroll bars too) ─────────────

    /// <summary>Fits the clip's range into the key area (Home).</summary>
    public void Fit()
    {
        if (_model is null || ActualWidth < 2) return;
        var c = _model.Clip;
        double span = Math.Max(RfaClip.TicksPerFrame, c.EndTime - c.StartTime);
        double margin = 18;
        _pxPerTick = Math.Max(1e-5, (BodyWidth - 2 * margin) / span);
        _timeLeft = c.StartTime - margin / _pxPerTick;
        _fitted = true;
        RedrawAll();
    }

    /// <summary>Sets the vertical offset in DIPs.</summary>
    public void SetVerticalOffset(double offset)
    {
        _scrollY = offset;
        ClampScroll();
        RedrawAll();
    }

    /// <summary>Sets the time at the left edge.</summary>
    public void SetTimeLeft(double ticks)
    {
        _timeLeft = ticks;
        RedrawAll();
    }

    /// <summary>Zooms by <paramref name="factor"/> keeping the time under <paramref name="anchorX"/> fixed.</summary>
    public void Zoom(double factor, double anchorX)
    {
        double t = Ticks(anchorX);
        _pxPerTick = Math.Clamp(_pxPerTick * factor, 1e-5, 20);
        _timeLeft = t - (anchorX - _headerWidth) / _pxPerTick;
        RedrawAll();
    }

    /// <summary>Scrolls so a row is visible.</summary>
    public void BringRowIntoView(int index)
    {
        int pinned = PinnedRows;
        double top = (index - pinned) * RowHeight;
        // Pinned rows never scroll away.
        if (index >= pinned && top < _scrollY) _scrollY = top;
        else if (index >= pinned && top + RowHeight > _scrollY + ViewportHeight) _scrollY = top + RowHeight - ViewportHeight;
        ClampScroll();
        RedrawAll();
    }

    private void ClampScroll()
    {
        double max = Math.Max(0, ContentHeight - ViewportHeight);
        _scrollY = Math.Clamp(_scrollY, 0, max);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    private sealed class Brushes
    {
        public Brush Background = null!, Header = null!, Alternate = null!, RowSelected = null!, Grid = null!, GridMajor = null!;
        public Brush OutOfRange = null!, Ramp = null!, Rot = null!, Pos = null!, Selected = null!, Hover = null!, Summary = null!;
        public Brush Text = null!, Secondary = null!, Accent = null!, Error = null!, Warning = null!, Info = null!, Box = null!, Handle = null!;
        public Pen GridPen = null!, MajorPen = null!, BorderPen = null!, KeyOutline = null!, BoxPen = null!, PlayheadPen = null!, HoverPen = null!, FocusPen = null!;
    }

    private Brushes B
    {
        get
        {
            if (_b is not null) return _b;
            Brush Get(string key) => TryFindResource(key) as Brush ?? System.Windows.Media.Brushes.Gray;
            Pen P(Brush b, double w) { var p = new Pen(b, w); p.Freeze(); return p; }
            var b = new Brushes
            {
                Background = Get("Timeline.Background"),
                Header = Get("Timeline.HeaderBackground"),
                Alternate = Get("Timeline.RowAlternate"),
                RowSelected = Get("Timeline.RowSelected"),
                Grid = Get("Timeline.Grid"),
                GridMajor = Get("Timeline.GridMajor"),
                OutOfRange = Get("Timeline.OutOfRange"),
                Ramp = Get("Scrubber.Ramp"),
                Rot = Get("Timeline.RotationKey"),
                Pos = Get("Timeline.PositionKey"),
                Selected = Get("Timeline.KeySelected"),
                Hover = Get("Timeline.KeyHover"),
                Summary = Get("Timeline.SummaryKey"),
                Text = Get("App.Text"),
                Secondary = Get("App.SecondaryText"),
                Accent = Get("Scrubber.Current"),
                Error = Get("Severity.Error"),
                Warning = Get("Severity.Warning"),
                Info = Get("Severity.Info"),
                Box = Get("Timeline.SelectionBox"),
                Handle = Get("Timeline.Handle"),
            };
            b.GridPen = P(b.Grid, 1);
            b.MajorPen = P(b.GridMajor, 1);
            b.BorderPen = P(Get("App.Border"), 1);
            b.KeyOutline = P(Get("Timeline.KeyOutline"), 1);
            b.BoxPen = P(Get("Timeline.SelectionBoxBorder"), 1);
            b.PlayheadPen = P(b.Accent, 1.5);
            b.HoverPen = P(b.Hover, 2);
            b.FocusPen = P(Get("App.Accent"), 1.5);
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _b = b;
            return b;
        }
    }

    private FormattedText Text(string s, double size, Brush brush, bool bold = false)
    {
        string key = $"{size}|{(bold ? 1 : 0)}|{brush.GetHashCode()}|{s}";
        if (_text.TryGetValue(key, out var t)) return t;
        if (_text.Count > 4000) _text.Clear();
        t = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, _dpi);
        _text[key] = t;
        return t;
    }

    /// <summary>Redraws every layer (layout, rows, keys or theme changed).</summary>
    public void RedrawAll()
    {
        if (_model is not null && !_fitted && ActualWidth > 2) Fit();
        long start = Stopwatch.GetTimestamp();
        DrawBackground();
        DrawKeys();
        LastRedrawMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        MaxRedrawMs = Math.Max(MaxRedrawMs, LastRedrawMs);
        DrawOverlay();
        DrawPlayhead();
        MovePlayhead();
        ScrollChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawBackground()
    {
        using var dc = _background.RenderOpen();
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 2) return;
        var b = B;
        dc.DrawRectangle(b.Background, null, new Rect(0, 0, w, h));
        if (_model is not { } m) return;
        var clip = m.Clip;
        var rows = m.VisibleRows;
        var selectedBones = m.Document.Selection;

        // Clip range shading and ramps in the body.
        double bodyTop = RulerHeight;
        double xs = X(clip.StartTime), xe = X(Math.Max(clip.StartTime, clip.EndTime));
        dc.PushClip(new RectangleGeometry(new Rect(_headerWidth, 0, Math.Max(0, w - _headerWidth), h)));
        if (xs > _headerWidth) dc.DrawRectangle(b.OutOfRange, null, new Rect(_headerWidth, bodyTop, xs - _headerWidth, h - bodyTop));
        if (xe < w) dc.DrawRectangle(b.OutOfRange, null, new Rect(Math.Max(_headerWidth, xe), bodyTop, w - Math.Max(_headerWidth, xe), h - bodyTop));

        // Row stripes and bone-selection highlight: the scrolling rows clipped below the pinned rows,
        // then the pinned (summary) rows, which never scroll.
        int pinned = PinnedRows;
        double scrollTop = ScrollTop;
        var (first, last) = ScrollingRange();
        void Stripe(int i)
        {
            double top = RowTop(i);
            var row = rows[i];
            if (!row.IsSummary && selectedBones.Contains(row.Bone)) dc.DrawRectangle(b.RowSelected, null, new Rect(_headerWidth, top, w - _headerWidth, RowHeight));
            else if (i % 2 == 1) dc.DrawRectangle(b.Alternate, null, new Rect(_headerWidth, top, w - _headerWidth, RowHeight));
        }
        dc.PushClip(new RectangleGeometry(new Rect(0, scrollTop, w, Math.Max(0, h - scrollTop))));
        for (int i = first; i <= last; i++) Stripe(i);
        dc.Pop();
        for (int i = 0; i < pinned; i++) Stripe(i);

        // Frame grid.
        var (step, major) = GridStep();
        double t0 = clip.StartTime + Math.Floor((_timeLeft - clip.StartTime) / step) * step;
        for (double t = t0; X(t) <= w; t += step)
        {
            double x = Math.Round(X(t)) + 0.5;
            if (x < _headerWidth) continue;
            bool isMajor = Math.Abs(Math.IEEERemainder(t - clip.StartTime, major)) < 0.5;
            dc.DrawLine(isMajor ? b.MajorPen : b.GridPen, new Point(x, bodyTop), new Point(x, h));
        }

        // Ramps, shaded lightly over every row (as the transport shows them: ramp-in wins where they overlap).
        dc.PushOpacity(0.32);
        if (clip.RampIn > 0)
        {
            double x1 = Math.Min(X(clip.StartTime + clip.RampIn), xe);
            dc.DrawRectangle(b.Ramp, null, new Rect(xs, bodyTop, Math.Max(0, x1 - xs), h - bodyTop));
        }
        if (clip.RampOut > 0)
        {
            double x0 = Math.Max(X(clip.EndTime - clip.RampOut), X(clip.StartTime + clip.RampIn));
            if (x0 < xe) dc.DrawRectangle(b.Ramp, null, new Rect(x0, bodyTop, xe - x0, h - bodyTop));
        }
        dc.Pop();
        dc.Pop();

        // Ruler.
        dc.DrawRectangle(b.Header, null, new Rect(_headerWidth, 0, w - _headerWidth, RulerHeight));
        dc.PushClip(new RectangleGeometry(new Rect(_headerWidth, 0, Math.Max(0, w - _headerWidth), RulerHeight)));
        if (clip.RampIn > 0)
        {
            double x1 = Math.Min(X(clip.StartTime + clip.RampIn), xe);
            dc.DrawRectangle(b.Ramp, null, new Rect(xs, RulerHeight - 7, Math.Max(0, x1 - xs), 7));
        }
        if (clip.RampOut > 0)
        {
            double x0 = Math.Max(X(clip.EndTime - clip.RampOut), X(clip.StartTime + clip.RampIn));
            if (x0 < xe) dc.DrawRectangle(b.Ramp, null, new Rect(x0, RulerHeight - 7, xe - x0, 7));
        }
        var unit = m.Unit;
        for (double t = t0; X(t) <= w + 40; t += step)
        {
            bool isMajor = Math.Abs(Math.IEEERemainder(t - clip.StartTime, major)) < 0.5;
            double x = Math.Round(X(t)) + 0.5;
            dc.DrawLine(isMajor ? b.MajorPen : b.GridPen, new Point(x, isMajor ? RulerHeight - 10 : RulerHeight - 5), new Point(x, RulerHeight));
            if (isMajor)
            {
                var label = Text(TimeFormat.Number(t, unit), 10, b.Secondary);
                dc.DrawText(label, new Point(x + 3, 3));
            }
        }
        // Selection span with scale grips.
        if (SelectionSpan(m) is { } span && span.Max > span.Min)
        {
            double a = X(span.Min), z = X(span.Max);
            dc.DrawRectangle(b.Box, b.BoxPen, new Rect(a, RulerHeight - 5, Math.Max(1, z - a), 4));
            DrawGrip(dc, a, b);
            DrawGrip(dc, z, b);
        }
        DrawHandle(dc, xs, true, b);
        DrawHandle(dc, xe, false, b);
        dc.Pop();
        dc.DrawLine(b.BorderPen, new Point(_headerWidth, RulerHeight - 0.5), new Point(w, RulerHeight - 0.5));

        // Row headers.
        dc.DrawRectangle(b.Header, null, new Rect(0, 0, _headerWidth, h));
        var title = Text(m.HasHierarchy ? "BONES" : "BONES (by index)", 10.5, b.Secondary, bold: true);
        dc.DrawText(title, new Point(8, (RulerHeight - title.Height) / 2));
        dc.PushClip(new RectangleGeometry(new Rect(0, scrollTop, _headerWidth, Math.Max(0, h - scrollTop))));
        for (int i = first; i <= last; i++) HeaderRow(i);
        dc.Pop();
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, _headerWidth, Math.Max(0, Math.Min(h, scrollTop) - RulerHeight))));
        for (int i = 0; i < pinned; i++) HeaderRow(i);
        dc.Pop();
        if (pinned > 0 && scrollTop < h)
        {
            // The pinned rows' lower edge: the bone rows scroll under it.
            dc.DrawLine(b.BorderPen, new Point(0, scrollTop - 0.5), new Point(w, scrollTop - 0.5));
        }
        var weightLabel = Text("w", 10.5, b.Secondary, bold: true);
        dc.DrawText(weightLabel, new Point(_headerWidth - 6 - weightLabel.Width - 4, (RulerHeight - weightLabel.Height) / 2));
        dc.DrawLine(b.BorderPen, new Point(_headerWidth - 0.5, 0), new Point(_headerWidth - 0.5, h));
        if (IsKeyboardFocused) dc.DrawRectangle(null, b.FocusPen, new Rect(0.75, 0.75, Math.Max(0, w - 1.5), Math.Max(0, h - 1.5)));

        void HeaderRow(int i)
        {
            double top = RowTop(i);
            var row = rows[i];
            bool selected = !row.IsSummary && selectedBones.Contains(row.Bone);
            if (selected) dc.DrawRectangle(b.RowSelected, null, new Rect(0, top, _headerWidth, RowHeight));
            else if (i % 2 == 1) dc.DrawRectangle(b.Alternate, null, new Rect(0, top, _headerWidth, RowHeight));
            if (i == _hoverRow && _gesture == Gesture.None) dc.DrawRectangle(b.Alternate, null, new Rect(0, top, _headerWidth, RowHeight));
            double x = 6 + row.Depth * IndentStep;
            double mid = top + RowHeight / 2;
            if (row.HasChildren)
            {
                var arrow = new StreamGeometry();
                using (var g = arrow.Open())
                {
                    if (m.IsCollapsed(row.Bone))
                    {
                        g.BeginFigure(new Point(x + 2, mid - 4), true, true);
                        g.LineTo(new Point(x + 7, mid), false, false);
                        g.LineTo(new Point(x + 2, mid + 4), false, false);
                    }
                    else
                    {
                        g.BeginFigure(new Point(x, mid - 2.5), true, true);
                        g.LineTo(new Point(x + 8, mid - 2.5), false, false);
                        g.LineTo(new Point(x + 4, mid + 2.5), false, false);
                    }
                }
                arrow.Freeze();
                dc.DrawGeometry(b.Secondary, null, arrow);
            }
            x += 12;
            // Weight and lint marker on the right.
            double right = _headerWidth - 6;
            if (!row.IsSummary)
            {
                float weight = m.Weight(row.Bone);
                string wt = float.IsNaN(weight) ? "—" : weight.ToString("0.##", CultureInfo.CurrentCulture);
                var wtext = Text(wt, 10.5, b.Secondary);
                dc.DrawText(wtext, new Point(right - wtext.Width, mid - wtext.Height / 2));
                right -= 30;
                if (m.Marker(row.Bone) is { } severity)
                {
                    var brush = severity switch { DiagnosticSeverity.Error => b.Error, DiagnosticSeverity.Warning => b.Warning, _ => b.Info };
                    dc.DrawEllipse(brush, null, new Point(right + 4, mid), 3.5, 3.5);
                }
                right -= 12;
            }
            var name = Text(row.Name, row.IsSummary ? 11 : 11.5, row.IsSummary ? b.Secondary : b.Text, bold: row.IsSummary || selected);
            dc.PushClip(new RectangleGeometry(new Rect(x, top, Math.Max(0, right - x), RowHeight)));
            dc.DrawText(name, new Point(x, mid - name.Height / 2));
            dc.Pop();
        }
    }

    private static void DrawGrip(DrawingContext dc, double x, Brushes b)
    {
        dc.DrawRectangle(b.Handle, b.BoxPen, new Rect(x - 3, RulerHeight - 9, 6, 9));
    }

    private static void DrawHandle(DrawingContext dc, double x, bool start, Brushes b)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x, RulerHeight - 1), true, true);
            ctx.LineTo(new Point(start ? x + 7 : x - 7, RulerHeight - 1), false, false);
            ctx.LineTo(new Point(x, RulerHeight - 12), false, false);
        }
        g.Freeze();
        dc.DrawGeometry(b.Accent, null, g);
        dc.DrawLine(b.PlayheadPen, new Point(x, RulerHeight - 12), new Point(x, RulerHeight));
    }

    /// <summary>The frame grid step (ticks) and the major step, chosen so labels stay ~60 px apart.</summary>
    private (double Step, double Major) GridStep()
    {
        int[] frames = [1, 2, 5, 10, 15, 30, 60, 150, 300, 600, 1500, 3000];
        double frame = RfaClip.TicksPerFrame;
        int minor = frames.FirstOrDefault(f => f * frame * _pxPerTick >= 8, frames[^1]);
        int major = frames.FirstOrDefault(f => f >= minor && f * frame * _pxPerTick >= 60 && f % minor == 0, minor * 5);
        return (minor * frame, major * frame);
    }

    // Per selection (and clip) lookups, rebuilt only when either instance changes: the selected flags of
    // each track, the selected times (summary row) and the selection's time span.
    private KeySelection? _cacheSelection;
    private RfaClip? _cacheClip;
    private Dictionary<(int Bone, KeyKind Kind), bool[]> _selectedFlags = [];
    private HashSet<int> _selectedTimes = [];
    private (int Min, int Max)? _selectionSpan;

    private void EnsureSelectionCache(KeySelection selection, RfaClip clip)
    {
        if (ReferenceEquals(selection, _cacheSelection) && ReferenceEquals(clip, _cacheClip)) return;
        _cacheSelection = selection;
        _cacheClip = clip;
        var flags = new Dictionary<(int, KeyKind), bool[]>();
        var times = new HashSet<int>();
        int min = int.MaxValue, max = int.MinValue;
        foreach (var k in selection.Keys)
        {
            if ((uint)k.Bone >= (uint)clip.BoneCount) continue;
            var track = clip.Bones[k.Bone];
            int count = k.Kind == KeyKind.Rotation ? track.RotationKeys.Length : track.PositionKeys.Length;
            if ((uint)k.Index >= (uint)count) continue;
            if (!flags.TryGetValue((k.Bone, k.Kind), out var f)) flags[(k.Bone, k.Kind)] = f = new bool[count];
            f[k.Index] = true;
            int t = k.Kind == KeyKind.Rotation ? track.RotationKeys[k.Index].Time : track.PositionKeys[k.Index].Time;
            times.Add(t);
            if (t < min) min = t;
            if (t > max) max = t;
        }
        _selectedFlags = flags;
        _selectedTimes = times;
        _selectionSpan = min <= max ? (min, max) : null;
    }

    private (int Min, int Max)? SelectionSpan(TimelineViewModel m)
    {
        EnsureSelectionCache(m.Document.KeySelection, m.Clip);
        return _selectionSpan;
    }

    private static int FirstAtOrAfter(System.Collections.Immutable.ImmutableArray<RfaRotKey> keys, double time)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (keys[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int FirstAtOrAfter(System.Collections.Immutable.ImmutableArray<RfaPosKey> keys, double time)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (keys[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private void DrawKeys()
    {
        using var dc = _keys.RenderOpen();
        LastMarksDrawn = 0;
        if (_model is not { } m || ActualWidth < 2 || ActualHeight < 2) return;
        var b = B;
        var clip = m.Clip;
        var rows = m.VisibleRows;
        var selection = m.Document.KeySelection;
        EnsureSelectionCache(selection, clip);
        double w = ActualWidth;
        double left = _headerWidth - KeyHit, right = w + KeyHit;
        int pinned = PinnedRows;
        double scrollTop = ScrollTop;
        var (first, last) = ScrollingRange();

        var rot = new StreamGeometry();
        var rotSel = new StreamGeometry();
        var pos = new StreamGeometry();
        var posSel = new StreamGeometry();
        var sum = new StreamGeometry();
        var sumSel = new StreamGeometry();
        int marks = 0;
        const double r = 4.6, s = 3.4;
        using (var cRot = rot.Open())
        using (var cRotSel = rotSel.Open())
        using (var cPos = pos.Open())
        using (var cPosSel = posSel.Open())
        using (var cSum = sum.Open())
        using (var cSumSel = sumSel.Open())
        {
            double tLeft = Ticks(left);
            // The pinned rows (summary), then the scrolling rows on screen.
            foreach (int i in Enumerable.Range(0, pinned).Concat(Enumerable.Range(first, Math.Max(0, last - first + 1))))
            {
                double top = RowTop(i);
                var row = rows[i];
                if (row.IsSummary)
                {
                    double y = top + RowHeight / 2, lastX = double.NegativeInfinity, lastSelX = double.NegativeInfinity;
                    foreach (int t in m.AllKeyTimes)
                    {
                        double x = X(t);
                        if (x < left) continue;
                        if (x > right) break;
                        if (_selectedTimes.Contains(t))
                        {
                            if (x - lastSelX < MergePx) continue;
                            lastSelX = x;
                            Diamond(cSumSel, x, y, r);
                        }
                        else
                        {
                            if (x - lastX < MergePx) continue;
                            lastX = x;
                            Diamond(cSum, x, y, r);
                        }
                        marks++;
                    }
                    continue;
                }
                if (row.Bone >= clip.BoneCount) continue;
                var track = clip.Bones[row.Bone];
                double ry = RotY(top), py = PosY(top);
                _selectedFlags.TryGetValue((row.Bone, KeyKind.Rotation), out var rotFlags);
                _selectedFlags.TryGetValue((row.Bone, KeyKind.Position), out var posFlags);
                double lastRot = double.NegativeInfinity, lastRotSel = double.NegativeInfinity;
                var rk = track.RotationKeys;
                for (int k = FirstAtOrAfter(rk, tLeft); k < rk.Length; k++)
                {
                    double x = X(rk[k].Time);
                    if (x < left) continue;
                    if (x > right) break;
                    if (rotFlags is not null && rotFlags[k])
                    {
                        if (x - lastRotSel < MergePx) continue;
                        lastRotSel = x;
                        Diamond(cRotSel, x, ry, r);
                    }
                    else
                    {
                        if (x - lastRot < MergePx) continue;
                        lastRot = x;
                        Diamond(cRot, x, ry, r);
                    }
                    marks++;
                }
                double lastPos = double.NegativeInfinity, lastPosSel = double.NegativeInfinity;
                var pk = track.PositionKeys;
                for (int k = FirstAtOrAfter(pk, tLeft); k < pk.Length; k++)
                {
                    double x = X(pk[k].Time);
                    if (x < left) continue;
                    if (x > right) break;
                    if (posFlags is not null && posFlags[k])
                    {
                        if (x - lastPosSel < MergePx) continue;
                        lastPosSel = x;
                        Square(cPosSel, x, py, s);
                    }
                    else
                    {
                        if (x - lastPos < MergePx) continue;
                        lastPos = x;
                        Square(cPos, x, py, s);
                    }
                    marks++;
                }
            }
        }
        // Bone rows scroll under the pinned summary row: each group is clipped to its own band.
        double h = ActualHeight;
        var bands = new[]
        {
            (Clip: new Rect(_headerWidth, scrollTop, Math.Max(0, w - _headerWidth), Math.Max(0, h - scrollTop)),
             Marks: new[] { (rot, b.Rot), (pos, b.Pos), (rotSel, b.Selected), (posSel, b.Selected) }),
            (Clip: new Rect(_headerWidth, RulerHeight, Math.Max(0, w - _headerWidth), Math.Max(0, Math.Min(h, scrollTop) - RulerHeight)),
             Marks: new[] { (sum, b.Summary), (sumSel, b.Selected) }),
        };
        foreach (var (band, groups) in bands)
        {
            dc.PushClip(new RectangleGeometry(band));
            foreach (var (g, brush) in groups)
            {
                g.Freeze();
                // Outlines only while marks are sparse enough to see them; a dense track reads better (and draws faster) without.
                dc.DrawGeometry(brush, marks > 6000 ? null : b.KeyOutline, g);
            }
            dc.Pop();
        }
        LastMarksDrawn = marks;
    }

    private static void Diamond(StreamGeometryContext g, double x, double y, double r)
    {
        g.BeginFigure(new Point(x, y - r), true, true);
        g.LineTo(new Point(x + r, y), true, false);
        g.LineTo(new Point(x, y + r), true, false);
        g.LineTo(new Point(x - r, y), true, false);
    }

    private static void Square(StreamGeometryContext g, double x, double y, double s)
    {
        g.BeginFigure(new Point(x - s, y - s), true, true);
        g.LineTo(new Point(x + s, y - s), true, false);
        g.LineTo(new Point(x + s, y + s), true, false);
        g.LineTo(new Point(x - s, y + s), true, false);
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (_model is not { } m || ActualWidth < 2) return;
        var b = B;
        dc.PushClip(new RectangleGeometry(new Rect(_headerWidth, 0, Math.Max(0, ActualWidth - _headerWidth), ActualHeight)));
        if (_hoverKey is { } hk && hk.IsValidIn(m.Clip))
        {
            int rowIndex = IndexOfRow(hk.Bone);
            if (rowIndex >= 0)
            {
                double top = RowTop(rowIndex);
                double x = X(hk.TimeIn(m.Clip));
                double y = hk.Kind == KeyKind.Rotation ? RotY(top) : PosY(top);
                // A bone row's ring stays under the pinned summary row.
                dc.PushClip(new RectangleGeometry(new Rect(0, ScrollTop, ActualWidth, Math.Max(0, ActualHeight - ScrollTop))));
                dc.DrawEllipse(null, b.HoverPen, new Point(x, y), 7, 7);
                dc.Pop();
            }
        }
        else if (_hoverSummaryTime != int.MinValue)
        {
            int rowIndex = m.VisibleRows.Count > 0 && m.VisibleRows[0].IsSummary ? 0 : -1;
            if (rowIndex == 0) dc.DrawEllipse(null, b.HoverPen, new Point(X(_hoverSummaryTime), RowTop(0) + RowHeight / 2), 7, 7);
        }
        if (_box is { } box) dc.DrawRectangle(b.Box, b.BoxPen, box);
        if (m.DragReadoutTime is { } t)
        {
            var label = Text(TimeFormat.Format(t, m.Unit), 11, b.Text, bold: true);
            double x = Math.Clamp(X(t) + 8, _headerWidth + 2, Math.Max(_headerWidth + 2, ActualWidth - label.Width - 8));
            var rect = new Rect(x - 4, RulerHeight + 2, label.Width + 8, label.Height + 2);
            dc.DrawRoundedRectangle(b.Header, b.BoxPen, rect, 3, 3);
            dc.DrawText(label, new Point(x, RulerHeight + 3));
        }
        dc.Pop();
    }

    private void DrawPlayhead()
    {
        using var dc = _playhead.RenderOpen();
        if (_model is null || ActualHeight < 2) return;
        var b = B;
        dc.DrawLine(b.PlayheadPen, new Point(0, 0), new Point(0, ActualHeight));
        var head = new StreamGeometry();
        using (var g = head.Open())
        {
            g.BeginFigure(new Point(-6, 0), true, true);
            g.LineTo(new Point(6, 0), false, false);
            g.LineTo(new Point(6, 8), false, false);
            g.LineTo(new Point(0, 13), false, false);
            g.LineTo(new Point(-6, 8), false, false);
        }
        head.Freeze();
        dc.DrawGeometry(b.Accent, null, head);
    }

    private void MovePlayhead()
    {
        if (_model is null) return;
        double x = X(_model.Playback.Time);
        _playheadOffset.X = x;
        // The clip region keeps the line out of the header column; recompute it when it crosses.
        _playhead.Opacity = x < _headerWidth - 1 ? 0 : 1;
    }

    private int IndexOfRow(int bone)
    {
        if (_model is null) return -1;
        var rows = _model.VisibleRows;
        for (int i = 0; i < rows.Count; i++) if (rows[i].Bone == bone) return i;
        return -1;
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    private KeyRef? KeyAt(Point p)
    {
        if (_model is not { } m || p.X < _headerWidth) return null;
        int index = RowAt(p.Y);
        if (index < 0) return null;
        var row = m.VisibleRows[index];
        if (row.IsSummary || row.Bone >= m.Clip.BoneCount) return null;
        double top = RowTop(index);
        var track = m.Clip.Bones[row.Bone];
        KeyRef? best = null;
        double bestDistance = KeyHit + 0.5;
        var selection = m.Document.KeySelection;
        void Consider(KeyKind kind, int k, int time, double y)
        {
            double d = Math.Abs(X(time) - p.X) + Math.Abs(y - p.Y) * 0.35;
            var key = new KeyRef(row.Bone, kind, k);
            // Prefer a selected key when marks overlap, so a drag picks up the selection.
            if (selection.Contains(key)) d -= 0.25;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = key;
            }
        }
        double ry = RotY(top), py = PosY(top);
        bool upper = p.Y < top + RowHeight * 0.55;
        var rk = track.RotationKeys;
        for (int k = 0; k < rk.Length; k++)
        {
            if (Math.Abs(X(rk[k].Time) - p.X) <= KeyHit) Consider(KeyKind.Rotation, k, rk[k].Time, upper ? p.Y : ry);
        }
        var pk = track.PositionKeys;
        for (int k = 0; k < pk.Length; k++)
        {
            if (Math.Abs(X(pk[k].Time) - p.X) <= KeyHit) Consider(KeyKind.Position, k, pk[k].Time, upper ? py : p.Y);
        }
        return best;
    }

    private int SummaryTimeAt(Point p)
    {
        if (_model is not { } m || p.X < _headerWidth) return int.MinValue;
        int index = RowAt(p.Y);
        if (index < 0 || !m.VisibleRows[index].IsSummary) return int.MinValue;
        int best = int.MinValue;
        double bestDistance = KeyHit;
        foreach (int t in m.AllKeyTimes)
        {
            double d = Math.Abs(X(t) - p.X);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = t;
            }
        }
        return best;
    }

    private bool OnHandle(Point p, out bool start)
    {
        start = false;
        if (_model is null || p.Y > RulerHeight || p.X < _headerWidth) return false;
        var c = _model.Clip;
        double xs = X(c.StartTime), xe = X(c.EndTime);
        if (Math.Abs(p.X - xs) <= 6 && p.Y >= RulerHeight - 14) { start = true; return true; }
        if (Math.Abs(p.X - xe) <= 6 && p.Y >= RulerHeight - 14) { start = false; return true; }
        return false;
    }

    private bool OnGrip(Point p, out int edgeTime)
    {
        edgeTime = 0;
        if (_model is not { } m || p.Y > RulerHeight || p.Y < RulerHeight - 11 || p.X < _headerWidth) return false;
        if (SelectionSpan(m) is not { } span || span.Max <= span.Min) return false;
        int pivot = m.PlayheadTick;
        if (Math.Abs(p.X - X(span.Min)) <= 5 && span.Min != pivot) { edgeTime = span.Min; return true; }
        if (Math.Abs(p.X - X(span.Max)) <= 5 && span.Max != pivot) { edgeTime = span.Max; return true; }
        return false;
    }

    // ── Mouse ────────────────────────────────────────────────────────────────

    private static SelectMode ModeFromModifiers()
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) return SelectMode.Toggle;
        if (mods.HasFlag(ModifierKeys.Shift)) return SelectMode.Add;
        return SelectMode.Replace;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_model is not { } m) return;
        Focus();
        var p = e.GetPosition(this);
        _down = _last = p;
        if (e.ChangedButton == MouseButton.Middle)
        {
            _gesture = Gesture.Pan;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.Right)
        {
            OpenContextMenu(p);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        // Header column.
        if (p.X < _headerWidth)
        {
            if (Math.Abs(p.X - _headerWidth) <= 4)
            {
                _gesture = Gesture.HeaderResize;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            int index = RowAt(p.Y);
            if (index >= 0)
            {
                var row = m.VisibleRows[index];
                double arrowX = 6 + row.Depth * IndentStep;
                if (row.HasChildren && p.X >= arrowX - 2 && p.X <= arrowX + 11) m.ToggleExpanded(row.Bone);
                else if (e.ClickCount == 2 && !row.IsSummary) m.SelectBoneKeys([row.Bone], ModeFromModifiers());
                else m.SelectRow(row, ModeFromModifiers());
            }
            e.Handled = true;
            return;
        }

        // Ruler: handles, scale grips, seek.
        if (p.Y < RulerHeight)
        {
            if (OnHandle(p, out bool start))
            {
                m.BeginRangeDrag(start);
                _gesture = Gesture.Handle;
            }
            else if (OnGrip(p, out int edge))
            {
                m.BeginScale(edge);
                _scaleFromGrip = true;
                _grabTime = edge;
                _gesture = Gesture.ScaleKeys;
            }
            else
            {
                m.Playback.Pause();
                _gesture = Gesture.Scrub;
                Seek(p.X);
            }
            CaptureMouse();
            e.Handled = true;
            return;
        }

        // Body.
        var key = KeyAt(p);
        int summaryTime = key is null ? SummaryTimeAt(p) : int.MinValue;
        if (key is null && summaryTime == int.MinValue && Math.Abs(p.X - X(m.Playback.Time)) <= 4 && e.ClickCount == 1)
        {
            // Grabbing the playhead line scrubs.
            m.Playback.Pause();
            _gesture = Gesture.Scrub;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        _downKey = key;
        _downSummaryTime = summaryTime;
        _downRow = RowAt(p.Y);
        if (e.ClickCount == 2 && key is { } dk)
        {
            m.OpenKey(dk);
            e.Handled = true;
            return;
        }
        var mode = ModeFromModifiers();
        if (key is { } k)
        {
            var selection = m.Document.KeySelection;
            bool alreadySelected = selection.Contains(k);
            if (mode == SelectMode.Toggle && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && alreadySelected)
            {
                // Ctrl on a selected key: maybe a copy-drag; decide on mouse up (no movement = toggle off).
            }
            else if (!alreadySelected) m.Select(KeySelection.Of(k), mode == SelectMode.Replace ? SelectMode.Replace : SelectMode.Add);
            if (!m.Document.Selection.Contains(k.Bone) && mode == SelectMode.Replace) m.Document.Selection.Select(k.Bone);
            _grabTime = k.TimeIn(m.Clip);
            _gesture = Gesture.Pending;
        }
        else if (summaryTime != int.MinValue)
        {
            var atTime = m.KeysAtTime(summaryTime);
            if (!atTime.Keys.All(m.Document.KeySelection.Contains) || mode != SelectMode.Replace)
                m.Select(atTime, mode);
            _grabTime = summaryTime;
            _gesture = Gesture.Pending;
        }
        else
        {
            _gesture = Gesture.BoxSelect;
            _boxAdd = mode != SelectMode.Replace;
            _boxBase = _boxAdd ? m.Document.KeySelection : KeySelection.Empty;
            if (!_boxAdd) m.Select(KeySelection.Empty, SelectMode.Replace);
            _box = new Rect(p, p);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_model is not { } m) return;
        var p = e.GetPosition(this);
        var delta = p - _last;
        _last = p;
        switch (_gesture)
        {
            case Gesture.None:
                UpdateHover(p);
                return;
            case Gesture.Pan:
                _timeLeft -= delta.X / _pxPerTick;
                _scrollY -= delta.Y;
                ClampScroll();
                RedrawAll();
                return;
            case Gesture.HeaderResize:
                _headerWidth = Math.Clamp(p.X, MinHeaderWidth, Math.Max(MinHeaderWidth, ActualWidth - 120));
                RedrawAll();
                return;
            case Gesture.Scrub:
                Seek(p.X);
                return;
            case Gesture.Handle:
                m.UpdateRangeDrag((int)Math.Round(Ticks(p.X)), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                DrawOverlay();
                return;
            case Gesture.Pending:
                if ((p - _down).Length < 4) return;
                bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
                var span = SelectionSpan(m);
                if (alt && _downKey is not null && span is { } sp && sp.Max > sp.Min && (_grabTime == sp.Min || _grabTime == sp.Max) && _grabTime != m.PlayheadTick)
                {
                    m.BeginScale(_grabTime);
                    _scaleFromGrip = false;
                    _gesture = Gesture.ScaleKeys;
                }
                else
                {
                    m.BeginMove(_grabTime, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                    _gesture = Gesture.MoveKeys;
                }
                goto case Gesture.MoveKeys;
            case Gesture.MoveKeys:
                if (_gesture == Gesture.MoveKeys)
                    m.UpdateMove((int)Math.Round((p.X - _down.X) / _pxPerTick), Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                else
                    m.UpdateScale((int)Math.Round((p.X - _down.X) / _pxPerTick), _scaleFromGrip ? Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                AutoScrollTowards(p);
                DrawOverlay();
                return;
            case Gesture.ScaleKeys:
                m.UpdateScale((int)Math.Round((p.X - _down.X) / _pxPerTick), _scaleFromGrip ? Keyboard.Modifiers.HasFlag(ModifierKeys.Alt) : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                DrawOverlay();
                return;
            case Gesture.BoxSelect:
                _box = new Rect(_down, p);
                UpdateBoxSelection();
                DrawOverlay();
                return;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_model is not { } m) return;
        var gesture = _gesture;
        _gesture = Gesture.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
        switch (gesture)
        {
            case Gesture.Pending:
                // A click without a drag: Ctrl on a selected key toggles it off; a plain click narrows to it.
                var mode = ModeFromModifiers();
                if (_downKey is { } k)
                {
                    if (mode == SelectMode.Toggle && m.Document.KeySelection.Contains(k)) m.Select(KeySelection.Of(k), SelectMode.Toggle);
                    else if (mode == SelectMode.Replace) m.Select(KeySelection.Of(k), SelectMode.Replace);
                }
                else if (_downSummaryTime != int.MinValue && mode == SelectMode.Replace)
                {
                    m.Select(m.KeysAtTime(_downSummaryTime), SelectMode.Replace);
                }
                break;
            case Gesture.MoveKeys:
            case Gesture.ScaleKeys:
            case Gesture.Handle:
                m.EndDrag();
                break;
            case Gesture.BoxSelect:
                _box = null;
                DrawOverlay();
                break;
            case Gesture.HeaderResize:
                break;
        }
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_gesture is Gesture.MoveKeys or Gesture.ScaleKeys or Gesture.Handle) _model?.EndDrag();
        if (_gesture == Gesture.BoxSelect)
        {
            _box = null;
            DrawOverlay();
        }
        _gesture = Gesture.None;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey is not null || _hoverSummaryTime != int.MinValue || _hoverRow >= 0)
        {
            _hoverKey = null;
            _hoverSummaryTime = int.MinValue;
            int oldRow = _hoverRow;
            _hoverRow = -1;
            if (oldRow >= 0) DrawBackground();
            DrawOverlay();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) Zoom(Math.Pow(1.2, e.Delta / 120.0), Math.Max(p.X, _headerWidth));
        else if (mods.HasFlag(ModifierKeys.Shift)) SetTimeLeft(_timeLeft - e.Delta / 120.0 * BodyWidth * 0.1 / _pxPerTick);
        else SetVerticalOffset(_scrollY - e.Delta / 120.0 * RowHeight * 3);
        e.Handled = true;
    }

    private void Seek(double x)
    {
        if (_model is not { } m) return;
        double t = Ticks(x);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) t = m.SnapToFrame(t);
        m.Playback.Seek((float)t);
    }

    private void AutoScrollTowards(Point p)
    {
        if (p.X > ActualWidth - 10) SetTimeLeft(_timeLeft + 20 / _pxPerTick);
        else if (p.X < _headerWidth + 10 && p.X > 0) SetTimeLeft(_timeLeft - 20 / _pxPerTick);
    }

    private void UpdateBoxSelection()
    {
        if (_model is not { } m || _box is not { } box) return;
        int a = RowAt(Math.Max(RulerHeight, box.Top)), z = RowAt(Math.Min(ActualHeight - 1, box.Bottom));
        if (a < 0) a = box.Top < RulerHeight ? 0 : -1;
        if (z < 0) z = m.VisibleRows.Count - 1;
        if (a < 0 || m.VisibleRows.Count == 0)
        {
            m.Select(_boxBase, SelectMode.Replace);
            return;
        }
        var rows = m.VisibleRows.Skip(a).Take(z - a + 1);
        int from = (int)Math.Ceiling(Ticks(Math.Max(_headerWidth, box.Left)));
        int to = (int)Math.Floor(Ticks(box.Right));
        var keys = to >= from ? m.KeysIn(rows, from, to) : KeySelection.Empty;
        m.Select(_boxBase.Union(keys), SelectMode.Replace);
    }

    private void UpdateHover(Point p)
    {
        if (_model is not { } m) return;
        var key = p.Y >= RulerHeight ? KeyAt(p) : null;
        int summary = key is null && p.Y >= RulerHeight ? SummaryTimeAt(p) : int.MinValue;
        int row = p.X < _headerWidth ? RowAt(p.Y) : -1;
        bool rowChanged = row != _hoverRow;
        if (!Nullable.Equals(key, _hoverKey) || summary != _hoverSummaryTime || rowChanged)
        {
            _hoverKey = key;
            _hoverSummaryTime = summary;
            _hoverRow = row;
            if (rowChanged) DrawBackground();
            DrawOverlay();
        }
        Cursor = OnHandle(p, out _) || OnGrip(p, out _) || Math.Abs(p.X - _headerWidth) <= 4 ? Cursors.SizeWE
            : key is not null || summary != int.MinValue ? Cursors.Hand
            : p.X > _headerWidth && Math.Abs(p.X - X(m.Playback.Time)) <= 4 ? Cursors.SizeWE
            : null;
        _toolTipText = DescribeAt(p, key, summary);
        if (_toolTip.IsOpen) _toolTip.Content = _toolTipText ?? DefaultToolTip;
    }

    private string? DescribeAt(Point p, KeyRef? key, int summary)
    {
        if (_model is not { } m) return null;
        var unit = m.Unit;
        if (key is { } k && k.IsValidIn(m.Clip))
        {
            int t = k.TimeIn(m.Clip);
            string bone = m.Document.BoneDisplayName(k.Bone);
            string time = $"{TimeFormat.Format(t, unit)} ({t} ticks)";
            if (k.Kind == KeyKind.Rotation)
            {
                var rk = m.Clip.Bones[k.Bone].RotationKeys[k.Index];
                return $"Rotation key {k.Index} of {bone} at {time}\nEase in {rk.EaseIn * 100 / 127} %, ease out {rk.EaseOut * 100 / 127} %\nDouble-click to open it in the Key inspector.";
            }
            var pk = m.Clip.Bones[k.Bone].PositionKeys[k.Index];
            return string.Format(CultureInfo.CurrentCulture, "Position key {0} of {1} at {2}\n({3:0.####}, {4:0.####}, {5:0.####}) m\nDouble-click to open it in the Key inspector.",
                k.Index, bone, time, pk.Position.X, pk.Position.Y, pk.Position.Z);
        }
        if (summary != int.MinValue)
        {
            int atTime = m.KeysAtTime(summary).Count;
            return $"{atTime} {(atTime == 1 ? "key" : "keys")} at {TimeFormat.Format(summary, unit)} ({summary} ticks). Click to select them all.";
        }
        if (p.X < _headerWidth && RowAt(p.Y) is int ri && ri >= 0)
        {
            var row = m.VisibleRows[ri];
            if (row.IsSummary) return "Every key time of the clip. Click a mark to select every key at that time.";
            float w = m.Weight(row.Bone);
            var track = row.Bone < m.Clip.BoneCount ? m.Clip.Bones[row.Bone] : null;
            string text = $"Bone {row.Bone}: {row.Name}\nWeight {w.ToString("0.##", CultureInfo.CurrentCulture)} (0–10; as an action, 10 replaces the states on this bone)"
                + (track is null ? string.Empty : $"\n{track.RotationKeys.Length} rotation, {track.PositionKeys.Length} position keys")
                + "\nClick to select the bone (Ctrl: toggle, Shift: range); double-click selects its keys.";
            if (m.MarkerText(row.Bone) is { } problem) text += "\n\nProblem: " + problem;
            return text;
        }
        if (p.Y < RulerHeight)
        {
            if (OnHandle(p, out bool start)) return start ? "Drag to change the clip's start time (snaps to frames; Alt: free)." : "Drag to change the clip's end time (snaps to frames; Alt: free).";
            if (OnGrip(p, out _)) return "Drag to scale the selected keys about the playhead (snaps to frames; Alt: free).";
            return "Click or drag to move the playhead (Alt: between frames). Shaded: ramp in and ramp out.";
        }
        return null;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_model is not { } m || e.Handled) return;
        var mods = Keyboard.Modifiers;
        // Ctrl+Alt+letter belongs to the window (Clip tools: Ctrl+Alt+V is Reverse), not to Ctrl+letter here.
        bool alt = mods.HasFlag(ModifierKeys.Alt);
        bool ctrl = mods.HasFlag(ModifierKeys.Control) && !alt, shift = mods.HasFlag(ModifierKeys.Shift);
        if (alt && mods.HasFlag(ModifierKeys.Control)) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Delete or Key.Back when !ctrl:
                m.DeleteSelected();
                break;
            case Key.C when ctrl:
                m.Copy();
                break;
            case Key.X when ctrl:
                m.Cut();
                break;
            case Key.V when ctrl && shift:
                m.PasteMirrored();
                break;
            case Key.V when ctrl:
                m.Paste();
                break;
            case Key.A when ctrl:
                m.SelectAll();
                break;
            case Key.K when !ctrl:
                m.KeySelectedBonesAtPlayhead();
                break;
            case Key.Home when !ctrl:
                Fit();
                break;
            case Key.Escape:
                if (_gesture is Gesture.MoveKeys or Gesture.ScaleKeys or Gesture.Handle)
                {
                    _gesture = Gesture.None;
                    m.CancelDrag();
                    if (IsMouseCaptured) ReleaseMouseCapture();
                }
                else if (_gesture == Gesture.BoxSelect)
                {
                    _gesture = Gesture.None;
                    _box = null;
                    m.Select(_boxBase, SelectMode.Replace);
                    if (IsMouseCaptured) ReleaseMouseCapture();
                    DrawOverlay();
                }
                else m.Select(KeySelection.Empty, SelectMode.Replace);
                break;
            case Key.Space:
                m.Playback.TogglePlay();
                break;
            case Key.Left:
                m.Playback.StepBackCommand.Execute(null);
                break;
            case Key.Right:
                m.Playback.StepForwardCommand.Execute(null);
                break;
            case Key.Up or Key.Down:
                MoveBoneSelection(key == Key.Up ? -1 : 1);
                break;
            case Key.OemPlus or Key.Add when ctrl:
                Zoom(1.25, _headerWidth + BodyWidth / 2);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Zoom(0.8, _headerWidth + BodyWidth / 2);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void MoveBoneSelection(int step)
    {
        if (_model is not { } m) return;
        var rows = m.VisibleRows;
        int current = IndexOfRow(m.Document.Selection.Active);
        int next = Math.Clamp(current < 0 ? (step > 0 ? 1 : rows.Count - 1) : current + step, 0, rows.Count - 1);
        if (next < rows.Count && rows[next].IsSummary) next = Math.Min(rows.Count - 1, next + (step > 0 ? 1 : 1));
        if (next >= 0 && next < rows.Count && !rows[next].IsSummary)
        {
            m.Document.Selection.Select(rows[next].Bone);
            BringRowIntoView(next);
        }
    }

    private bool _altUsedWithMouse;

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (_gesture != Gesture.None && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) _altUsedWithMouse = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        // Releasing Alt after an Alt+drag (free move, scale, scrub between frames) must not open the menu bar.
        if (e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt && _altUsedWithMouse)
        {
            _altUsedWithMouse = false;
            e.Handled = true;
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        DrawBackground();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        DrawBackground();
    }

    // ── Context menus ────────────────────────────────────────────────────────

    private void OpenContextMenu(Point p)
    {
        if (_model is not { } m) return;
        var menu = new ContextMenu { PlacementTarget = this };
        MenuItem Item(string header, Action action, string tip, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, ToolTip = tip, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
            item.Click += (_, _) => action();
            AutomationProperties.SetName(item, header.Replace("_", string.Empty, StringComparison.Ordinal));
            return item;
        }
        bool readOnly = m.Document.IsReadOnly;
        bool hasSelection = !m.Document.KeySelection.IsEmpty;
        if (p.X < _headerWidth && RowAt(p.Y) is int ri && ri >= 0 && !m.VisibleRows[ri].IsSummary)
        {
            var row = m.VisibleRows[ri];
            if (!m.Document.Selection.Contains(row.Bone)) m.Document.Selection.Select(row.Bone);
            var bones = m.Document.Selection.Bones.ToList();
            string what = bones.Count == 1 ? row.Name : $"{bones.Count} bones";
            menu.Items.Add(Item($"_Select keys of {what}", () => m.SelectBoneKeys(bones), "Select every rotation and position key of the selected bones"));
            menu.Items.Add(Item("_Key at playhead", () => m.KeySelectedBonesAtPlayhead(), "Add a key holding the current pose on the selected bones (the motion does not change)", "K", !readOnly));
            menu.Items.Add(Item($"_Delete all keys of {what}", () =>
            {
                m.SelectBoneKeys(bones);
                m.DeleteSelected();
            }, "Delete every key of the selected bones (a bone without position keys collapses onto its parent)", null, !readOnly));
            menu.Items.Add(new Separator());
            if (row.HasChildren)
                menu.Items.Add(Item(m.IsCollapsed(row.Bone) ? "E_xpand" : "C_ollapse", () => m.ToggleExpanded(row.Bone), "Show or hide this bone's children"));
            menu.Items.Add(Item("Expand _all", () => m.ExpandAllCommand.Execute(null), "Show every bone"));
            menu.Items.Add(Item("Collapse a_ll", () => m.CollapseAllCommand.Execute(null), "Show only the top of the hierarchy"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Open in the _Bone inspector", () =>
            {
                m.Document.SelectedInspectorTab = m.Document.InspectorTabs.FirstOrDefault(t => t.Id == "bone") ?? m.Document.SelectedInspectorTab;
            }, "Show the bone's weight, key counts and offset in the inspector"));
        }
        else
        {
            var key = KeyAt(p);
            if (key is { } k && !m.Document.KeySelection.Contains(k)) m.Select(KeySelection.Of(k), SelectMode.Replace);
            hasSelection = !m.Document.KeySelection.IsEmpty;
            int count = m.Document.KeySelection.Count;
            string keys = TimelineViewModel.Keys(count);
            if (key is { } open)
                menu.Items.Add(Item("_Open in the Key inspector", () => m.OpenKey(open), "Seek to this key and show its values in the Key inspector"));
            menu.Items.Add(Item(hasSelection ? $"_Delete {keys}" : "_Delete", m.DeleteSelected, "Delete the selected keys", "Del", hasSelection && !readOnly));
            menu.Items.Add(Item("Cu_t", () => m.Cut(), "Copy the selected keys to the clipboard and delete them", "Ctrl+X", hasSelection && !readOnly));
            menu.Items.Add(Item("_Copy", () => m.Copy(), "Copy the selected keys to the clipboard (they paste in any clip, matched by bone name)", "Ctrl+C", hasSelection));
            menu.Items.Add(Item("_Paste at playhead", () => m.Paste(), "Paste copied keys with the earliest at the playhead, matched by bone name", "Ctrl+V", !readOnly));
            menu.Items.Add(Item("Paste _mirrored", () => m.PasteMirrored(), "Paste copied keys onto each bone's left/right partner, mirrored", "Ctrl+Shift+V", !readOnly));
            menu.Items.Add(new Separator());
            var scale = new MenuItem { Header = "_Scale about playhead", IsEnabled = hasSelection && !readOnly, ToolTip = "Stretch or squeeze the selected keys in time around the playhead" };
            foreach (var (label, factor) in new[] { ("50 %", 0.5), ("75 %", 0.75), ("150 %", 1.5), ("200 %", 2.0), ("Reverse (−100 %)", -1.0) })
                scale.Items.Add(Item(label, () => m.ScaleSelected(factor), $"Scale the selected keys' times by {label} about the playhead"));
            menu.Items.Add(scale);
            menu.Items.Add(Item("_Key selected bones at playhead", () => m.KeySelectedBonesAtPlayhead(), "Add a key holding the current pose on the selected bones", "K", !readOnly));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Select _all", m.SelectAll, "Select every key of the clip", "Ctrl+A"));
            menu.Items.Add(Item("Select _none", () => m.Select(KeySelection.Empty, SelectMode.Replace), "Clear the key selection", "Esc", hasSelection));
            menu.Items.Add(Item("_Fit the clip", Fit, "Zoom so the whole clip fits", "Home"));
        }
        menu.IsOpen = true;
    }
}
