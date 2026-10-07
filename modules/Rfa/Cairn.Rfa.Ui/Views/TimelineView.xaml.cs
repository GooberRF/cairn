using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Cairn.Rfa.Ui.Views;

/// <summary>
/// The Timeline bottom tab: a toolbar over the custom-drawn <see cref="Controls.TimelineSurface"/>, with
/// the scroll bars kept in step with the surface's own scrolling (wheel, middle-drag, zoom).
/// </summary>
public partial class TimelineView : UserControl
{
    private bool _syncing;

    public TimelineView()
    {
        InitializeComponent();
        Surface.ScrollChanged += (_, _) => SyncBars();
        VerticalBar.ValueChanged += (_, e) =>
        {
            if (!_syncing) Surface.SetVerticalOffset(e.NewValue);
        };
        HorizontalBar.ValueChanged += (_, e) =>
        {
            if (!_syncing) Surface.SetTimeLeft(e.NewValue);
        };
        VerticalBar.Scroll += (_, e) =>
        {
            if (e.ScrollEventType is ScrollEventType.SmallIncrement or ScrollEventType.SmallDecrement) Surface.SetVerticalOffset(VerticalBar.Value);
        };
    }

    /// <summary>The drawing surface (diagnostics read its redraw timing).</summary>
    public Controls.TimelineSurface TimelineSurface => Surface;

    private void SyncBars()
    {
        _syncing = true;
        try
        {
            double viewport = Surface.ViewportHeight;
            double max = Math.Max(0, Surface.ContentHeight - viewport);
            VerticalBar.Minimum = 0;
            VerticalBar.Maximum = max;
            VerticalBar.ViewportSize = Math.Max(1, viewport);
            VerticalBar.LargeChange = Math.Max(1, viewport * 0.9);
            VerticalBar.SmallChange = Controls.TimelineSurface.RowHeight;
            VerticalBar.Value = Math.Clamp(Surface.VerticalOffset, 0, max);
            VerticalBar.Visibility = max > 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            // The bar spans only the scrolling bone rows (the ruler and the pinned summary row stay put).
            VerticalBar.Margin = new System.Windows.Thickness(0, Surface.ScrollTop, 0, 0);

            var (min, maxT) = Surface.TimeExtent;
            double visible = Surface.VisibleTicks;
            double left = Surface.TimeLeft;
            double lo = Math.Min(min, left), hi = Math.Max(maxT - visible, left);
            HorizontalBar.Minimum = lo;
            HorizontalBar.Maximum = Math.Max(lo, hi);
            HorizontalBar.ViewportSize = Math.Max(1, visible);
            HorizontalBar.LargeChange = Math.Max(1, visible * 0.9);
            HorizontalBar.SmallChange = Math.Max(1, visible * 0.05);
            HorizontalBar.Value = Math.Clamp(left, HorizontalBar.Minimum, HorizontalBar.Maximum);
        }
        finally { _syncing = false; }
    }
}
