using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using Cairn.Rfa.Animation;

namespace Cairn.Rfa.Ui.Controls;

/// <summary>
/// A small drawing of how a rotation key's eases shape time around it: on the left the segment that
/// arrives at the key (previous key's ease-out, this key's ease-in), on the right the one that leaves it
/// (this key's ease-out, next key's ease-in), each the engine's <see cref="ClipSampler.Ease"/> curve
/// (progress against time) over a dashed straight line for no ease.
/// </summary>
public sealed class EaseCurvePreview : FrameworkElement
{
    public static readonly DependencyProperty InAProperty = Register(nameof(InA));
    public static readonly DependencyProperty InBProperty = Register(nameof(InB));
    public static readonly DependencyProperty OutAProperty = Register(nameof(OutA));
    public static readonly DependencyProperty OutBProperty = Register(nameof(OutB));
    public static readonly DependencyProperty HasInProperty = DependencyProperty.Register(nameof(HasIn), typeof(bool), typeof(EaseCurvePreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty HasOutProperty = DependencyProperty.Register(nameof(HasOut), typeof(bool), typeof(EaseCurvePreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    private static DependencyProperty Register(string name) => DependencyProperty.Register(name, typeof(float), typeof(EaseCurvePreview),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender));

    public EaseCurvePreview()
    {
        Height = 64;
        AutomationProperties.SetName(this, "Ease curve preview");
        ToolTip = "How the eases shape time around the key: left, the segment arriving at it; right, the one leaving it. "
            + "Steep = fast, flat = slow; the dashed line is no ease. A dotted vertical stroke in the warning colour is a jump: a negative ease byte, "
            + "which the engine reads as signed (the motion pops instead of easing).";
        Loaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged += OnTheme;
        };
        Unloaded += (_, _) =>
        {
            if (RfaUi.Theme is { } theme) theme.ThemeChanged -= OnTheme;
        };
    }

    private void OnTheme(object? sender, EventArgs e) => InvalidateVisual();

    /// <summary>Previous key's ease-out (signed byte / 127, so about -1..1) for the arriving segment.</summary>
    public float InA { get => (float)GetValue(InAProperty); set => SetValue(InAProperty, value); }

    /// <summary>This key's ease-in (signed byte / 127).</summary>
    public float InB { get => (float)GetValue(InBProperty); set => SetValue(InBProperty, value); }

    /// <summary>This key's ease-out (signed byte / 127).</summary>
    public float OutA { get => (float)GetValue(OutAProperty); set => SetValue(OutAProperty, value); }

    /// <summary>Next key's ease-in (signed byte / 127) for the leaving segment.</summary>
    public float OutB { get => (float)GetValue(OutBProperty); set => SetValue(OutBProperty, value); }

    /// <summary>True when there is a segment arriving at the key.</summary>
    public bool HasIn { get => (bool)GetValue(HasInProperty); set => SetValue(HasInProperty, value); }

    /// <summary>True when there is a segment leaving the key.</summary>
    public bool HasOut { get => (bool)GetValue(HasOutProperty); set => SetValue(HasOutProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;
        Brush Get(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
        var border = new Pen(Get("App.Border"), 1);
        var dashed = new Pen(Get("App.SecondaryText"), 1) { DashStyle = new DashStyle([3, 3], 0) };
        var curve = new Pen(Get("Timeline.RotationKey"), 2);
        var jump = new Pen(Get("Severity.Warning"), 2) { DashStyle = new DashStyle([1.5, 1.5], 0) };
        var key = Get("Timeline.KeySelected");
        dc.DrawRoundedRectangle(Get("App.WindowBackground"), border, new Rect(0.5, 0.5, w - 1, h - 1), 3, 3);
        double pad = 6, mid = w / 2;
        var left = new Rect(pad, pad, mid - pad * 1.5, h - 2 * pad);
        var right = new Rect(mid + pad * 0.5, pad, mid - pad * 1.5, h - 2 * pad);
        Panel(dc, left, HasIn, InA, InB, dashed, curve, jump);
        Panel(dc, right, HasOut, OutA, OutB, dashed, curve, jump);
        dc.DrawLine(border, new Point(mid, pad), new Point(mid, h - pad));
        dc.DrawEllipse(key, null, new Point(mid, left.Top + left.Height / 2), 3.5, 3.5);
    }

    private static void Panel(DrawingContext dc, Rect r, bool present, float a, float b, Pen dashed, Pen curve, Pen jump)
    {
        dc.DrawLine(dashed, r.BottomLeft, r.TopRight);
        if (!present) return;
        // The eases are signed (as the engine reads them): a negative one makes the curve jump at an end,
        // so the inside of the segment is drawn on its own and each jump as a vertical warning stroke.
        const float edge = 1e-5f;
        Point At(float u, float v) => new(r.Left + u * r.Width, r.Bottom - v * r.Height);
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            const int n = 48;
            for (int i = 0; i <= n; i++)
            {
                float u = Math.Clamp(i / (float)n, edge, 1 - edge);
                var p = At(u, ClipSampler.Ease(u, a, b));
                if (i == 0) ctx.BeginFigure(p, false, false);
                else ctx.LineTo(p, true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(null, curve, g);
        float start = ClipSampler.Ease(edge, a, b), end = ClipSampler.Ease(1 - edge, a, b);
        if (start > 0.004f) dc.DrawLine(jump, At(0, 0), At(0, start));
        if (end < 0.996f) dc.DrawLine(jump, At(1, end), At(1, 1));
    }
}
