using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cairn.Viewport;

/// <summary>
/// Projection helper shared by the 2D layers: the camera's view × projection for the current size,
/// with near-plane clipping of segments in homogeneous space (so a grid line passing behind the camera
/// is cut where it crosses the near plane, not flipped across the screen).
/// </summary>
public readonly struct Projector
{
    private readonly Matrix3D _vp;
    private readonly Size _size;

    public Projector(OrbitCamera camera, Size size)
    {
        _size = size;
        _vp = camera.ViewProjection(size);
    }

    public bool Point(Vector3 p, out Point screen)
    {
        var c = _vp.Transform(new Point4D(p.X, p.Y, p.Z, 1));
        if (c.Z < 0 || c.W <= 1e-9)
        {
            screen = default;
            return false;
        }
        screen = ToScreen(c);
        return true;
    }

    public bool Segment(Vector3 a, Vector3 b, out Point sa, out Point sb)
    {
        var ca = _vp.Transform(new Point4D(a.X, a.Y, a.Z, 1));
        var cb = _vp.Transform(new Point4D(b.X, b.Y, b.Z, 1));
        // Clip against the near plane z >= 0 (Direct3D clip space), with a margin for w.
        double da = ca.Z, db = cb.Z;
        if (da < 0 && db < 0)
        {
            sa = sb = default;
            return false;
        }
        if (da < 0) ca = Lerp(ca, cb, da / (da - db) + 1e-6);
        else if (db < 0) cb = Lerp(cb, ca, db / (db - da) + 1e-6);
        if (ca.W <= 1e-9 || cb.W <= 1e-9)
        {
            sa = sb = default;
            return false;
        }
        sa = ToScreen(ca);
        sb = ToScreen(cb);
        return true;
    }

    private Point ToScreen(Point4D c) => new((c.X / c.W + 1) * 0.5 * _size.Width, (1 - c.Y / c.W) * 0.5 * _size.Height);

    private static Point4D Lerp(Point4D a, Point4D b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t);
}

/// <summary>The ground grid and the world axes, drawn behind the scene.</summary>
public sealed class GridLayer : FrameworkElement
{
    private Pen? _minor, _major, _x, _y, _z;

    public GridLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public OrbitCamera? Camera { get; set; }

    public bool ShowGrid { get; set; } = true;

    /// <summary>Forgets the pens (theme changed).</summary>
    public void ResetPens() => _minor = null;

    protected override void OnRender(DrawingContext dc)
    {
        if (Camera is not { } camera || !ShowGrid || ActualWidth < 2 || ActualHeight < 2) return;
        EnsurePens();
        var projector = new Projector(camera, new Size(ActualWidth, ActualHeight));

        // Spacing grows by tens with the orbit distance, so the grid stays readable at any zoom.
        double spacing = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1e-3, camera.Distance * 0.3))));
        if (camera.Distance * 0.3 / spacing > 5) spacing *= 5;
        const int lines = 20;
        double cx = Math.Round(camera.Target.X / spacing) * spacing;
        double cz = Math.Round(camera.Target.Z / spacing) * spacing;
        double extent = lines * spacing;
        for (int i = -lines; i <= lines; i++)
        {
            double x = cx + i * spacing, z = cz + i * spacing;
            bool majorX = Math.Abs(Math.Round(x / spacing) % 5) < 0.5;
            bool majorZ = Math.Abs(Math.Round(z / spacing) % 5) < 0.5;
            Line(dc, projector, new Vector3((float)x, 0, (float)(cz - extent)), new Vector3((float)x, 0, (float)(cz + extent)), majorX ? _major! : _minor!);
            Line(dc, projector, new Vector3((float)(cx - extent), 0, (float)z), new Vector3((float)(cx + extent), 0, (float)z), majorZ ? _major! : _minor!);
        }
        float axis = (float)(spacing * 5);
        Line(dc, projector, Vector3.Zero, new Vector3(axis, 0, 0), _x!);
        Line(dc, projector, Vector3.Zero, new Vector3(0, axis, 0), _y!);
        Line(dc, projector, Vector3.Zero, new Vector3(0, 0, axis), _z!);
    }

    private static void Line(DrawingContext dc, Projector p, Vector3 a, Vector3 b, Pen pen)
    {
        if (p.Segment(a, b, out var sa, out var sb)) dc.DrawLine(pen, sa, sb);
    }

    private void EnsurePens()
    {
        if (_minor is not null) return;
        _minor = Frozen(new Pen(Brush("Viewport.Grid"), 1));
        _major = Frozen(new Pen(Brush("Viewport.GridMajor"), 1));
        _x = Frozen(new Pen(Brush("Viewport.AxisX"), 2));
        _y = Frozen(new Pen(Brush("Viewport.AxisY"), 2));
        _z = Frozen(new Pen(Brush("Viewport.AxisZ"), 2));
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
