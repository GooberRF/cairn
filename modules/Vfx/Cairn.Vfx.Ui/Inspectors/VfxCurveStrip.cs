using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Commands;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Inspectors;

/// <summary>
/// Editable strip for a material track (opacity, self-illumination, mix): one point per sample over the material's
/// frame axis, playhead marker. Click sets the sample under the cursor, vertical drag moves it, horizontal drag paints a
/// range; each gesture is one undo step.
/// </summary>
internal sealed class VfxCurveStrip : FrameworkElement
{
    private const double Pad = 6;
    private readonly VfxDocument _doc;
    private readonly Func<int> _section;
    private readonly VfxMaterialTrack _track;
    private readonly Func<VfxMaterial, IReadOnlyList<float>?> _get;
    private readonly string _label;
    private bool _dragging;
    private int _last = -1;

    public VfxCurveStrip(VfxDocument doc, Func<int> section, VfxMaterialTrack track, Func<VfxMaterial, IReadOnlyList<float>?> get, string label)
    {
        _doc = doc; _section = section; _track = track; _get = get; _label = label;
        Height = 72;
        Focusable = true;
        Cursor = Cursors.Cross;
        AutomationProperties.SetName(this, label + " curve");
        ToolTip = "Click to set a sample, drag vertically to adjust it, drag across to paint a range (one undo step per gesture)";
    }

    private VfxMaterial? Mat => _section() is var i && i >= 0 && i < _doc.Current.Sections.Length ? _doc.Current.Sections[i] as VfxMaterial : null;
    private IReadOnlyList<float> Values => Mat is { } m && _get(m) is { } t ? t : [];

    /// <summary>Sample index under an x position (nearest), or -1 without samples.</summary>
    internal int SampleAt(double x)
    {
        int n = Values.Count;
        if (n == 0) return -1;
        if (n == 1) return 0;
        double w = Math.Max(1, ActualWidthOr() - 2 * Pad);
        return Math.Clamp((int)Math.Round((x - Pad) / w * (n - 1)), 0, n - 1);
    }

    /// <summary>Track value (0-1) at a y position.</summary>
    internal double ValueAt(double y) => Math.Clamp(1 - (y - Pad) / Math.Max(1, Height - 2 * Pad), 0, 1);

    internal Point PointOf(int i, float v)
    {
        int n = Values.Count;
        double w = ActualWidthOr() - 2 * Pad;
        return new(Pad + (n <= 1 ? w / 2 : w * i / (n - 1)), Pad + (1 - Math.Clamp(v, 0, 1)) * (Height - 2 * Pad));
    }

    private double ActualWidthOr() => ActualWidth > 0 ? ActualWidth : 300;

    // Gesture API (mouse handlers and self-tests): Press starts one coalesced edit, Drag paints/moves, Release commits.
    internal void Press(Point p)
    {
        if (Mat is null || Values.Count == 0) return;
        _dragging = VfxEditing.Begin(_doc, $"Edit {_label.ToLowerInvariant()} curve");
        if (!_dragging) return;
        _last = SampleAt(p.X);
        Paint(_last, _last, ValueAt(p.Y));
    }

    internal void Drag(Point p)
    {
        if (!_dragging) return;
        int at = SampleAt(p.X);
        Paint(_last, at, ValueAt(p.Y));
        _last = at;
    }

    internal void Release()
    {
        if (!_dragging) return;
        _dragging = false;
        VfxEditing.Commit(_doc);
    }

    internal void Cancel()
    {
        if (!_dragging) return;
        _dragging = false;
        _doc.CancelEdit();
    }

    private void Paint(int from, int to, double v)
    {
        int i = _section(), lo = Math.Min(from, to), hi = Math.Max(from, to);
        VfxEditing.Update(_doc, f =>
        {
            for (int s = lo; s <= hi; s++) f = VfxEdit.SetTrackSample(f, i, _track, s, (float)v);
            return f;
        });
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Press(e.GetPosition(this));
        if (_dragging) CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e) { if (_dragging) Drag(e.GetPosition(this)); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { Release(); ReleaseMouseCapture(); e.Handled = true; }

    protected override void OnLostMouseCapture(MouseEventArgs e) => Release();

    protected override void OnKeyDown(KeyEventArgs e) { if (e.Key == Key.Escape && _dragging) { Cancel(); ReleaseMouseCapture(); e.Handled = true; } }

    private Brush B(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidthOr(), h = Height;
        var border = new Pen(B("App.Border", Colors.Gray), 1);
        var grid = new Pen(B("App.Border", Colors.Gray), 0.5) { DashStyle = DashStyles.Dot };
        dc.DrawRectangle(B("App.PaneBackground", Colors.Black), border, new Rect(0.5, 0.5, w - 1, h - 1));
        foreach (double g in new[] { 0.0, 0.5, 1.0 }) dc.DrawLine(grid, new Point(Pad, Pad + g * (h - 2 * Pad)), new Point(w - Pad, Pad + g * (h - 2 * Pad)));
        var vals = Values;
        var accent = B("App.Accent", Colors.DodgerBlue);
        if (vals.Count == 0)
        {
            var ft = new FormattedText("no samples", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, B("App.SecondaryText", Colors.Gray), 1.0);
            dc.DrawText(ft, new Point(Pad + 2, h / 2 - ft.Height / 2));
            return;
        }
        var line = new Pen(accent, 1.5);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(PointOf(0, vals[0]), false, false);
            for (int i = 1; i < vals.Count; i++) g.LineTo(PointOf(i, vals[i]), true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, line, geo);
        double r = vals.Count > (w - 2 * Pad) / 4 ? 1.5 : 3;
        for (int i = 0; i < vals.Count; i++) dc.DrawEllipse(accent, null, PointOf(i, vals[i]), r, r);
        if (Mat is { } m)
        {
            int at = VfxMaterialTab.TrackIndex(m, (float)_doc.TimelineFrame, vals.Count);
            double x = PointOf(at, 0).X;
            dc.DrawLine(new Pen(B("App.Text", Colors.White), 1), new Point(x, 1), new Point(x, h - 1));
        }
    }
}
