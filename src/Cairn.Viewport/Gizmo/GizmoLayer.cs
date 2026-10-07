using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Formats.Maths;
using Quaternion = System.Numerics.Quaternion;
using Vector = System.Windows.Vector;

namespace Cairn.Viewport;

/// <summary>
/// The viewport gizmos, drawn in 2D above the scene's overlays with the same projection
/// (<see cref="Projector"/>), hit-tested in 2D, and the translation of mouse drags into
/// <see cref="IGizmoTarget"/> calls. The target says where the gizmo sits and which kind it is.
/// </summary>
/// <remarks>
/// <para>
/// Rotate: three axis rings (X red, Y green, Z blue) in the controller's space, a screen ring and a
/// free trackball inside. A ring drag intersects the mouse ray with the ring's plane and measures the
/// angle turned about the centre, so the rotation follows the mouse exactly at any tilt; a ring seen
/// edge-on falls back to dragging along its tangent. The trackball turns about the screen-plane axis
/// perpendicular to the drag (one radian per ring radius). Ctrl snaps angles to 5°.
/// </para>
/// <para>
/// Move: three axis arrows and a screen-plane square. An arrow drag takes the point of the axis line
/// closest to the mouse ray; the square intersects the ray with the plane through the joint facing the
/// camera. Ctrl snaps distances to 1 cm. With IK the same handles move the chain's end target.
/// </para>
/// <para>
/// Radius (a collision sphere in a mesh tab, with the move gizmo): a ring over the sphere's outline and a
/// grip on it. A drag intersects the mouse ray with the plane through the centre facing the camera; the
/// radius grows by how much further from the centre that point is than where the drag started. Ctrl
/// snaps the radius to 1 cm.
/// </para>
/// </remarks>
public sealed class GizmoLayer : FrameworkElement
{
    /// <summary>Angle snap step with Ctrl held, in degrees.</summary>
    public const double AngleSnapDegrees = 5;

    /// <summary>Distance snap step with Ctrl held, in metres.</summary>
    public const double DistanceSnapMetres = 0.01;

    /// <summary>Radius of the axis rings on screen (DIPs).</summary>
    public const double RingRadius = 64;

    private const double ScreenRingFactor = 1.22;
    private const double ArrowLength = 78;
    private const double HitTolerance = 7;
    private const int RingSegments = 72;

    // Up and to the left: clear of the sphere's name label (drawn up and to the right of it).
    private const double GripAngle = -3 * Math.PI / 4;
    private const double GripMinimum = 22;
    private const double GripRadius = 6;

    private readonly Dictionary<string, FormattedText> _texts = new(StringComparer.Ordinal);
    private IGizmoTarget? _controller;
    private readonly Pen?[] _axisPen = new Pen?[3];
    private readonly Pen?[] _axisBackPen = new Pen?[3];
    private readonly Brush?[] _axisBrush = new Brush?[3];
    private Pen? _outline, _outlineThin, _screenPen, _hoverPen, _hoverOutline, _radiusPen, _gripOutline;
    private Brush? _trackball, _trackballHover, _screenBrush, _hoverBrush, _readoutBackground, _readoutText;
    private double _dpi = 1;
    private int _hover = GizmoHandle.None;

    // Drag state.
    private int _dragHandle = GizmoHandle.None;
    private Point _dragStart;
    private Point _lastPoint;
    private bool _hasPointer;
    private bool _moved;
    private Vector3 _dragCentre;
    private Vector3 _dragAxis;
    private bool _planeMode;
    private Vector3 _planeU, _planeV;
    private double _lastRawAngle, _angleTotal;
    private bool _rotating;
    private Vector _tangent;
    private Quaternion _free = Quaternion.Identity;
    private Vector3 _planeStart;
    private double _axisStart;
    private Vector3 _screenNormal;
    private double _radiusStart, _radiusGrab;
    private bool _scaling;
    private double _scaleArm;
    private Vector3 _planeNormal;
    private double _scalePlaneStart;
    private readonly Brush?[] _planeBrush = new Brush?[3];
    private readonly Pen?[] _planePen = new Pen?[3];

    /// <summary>Plane handles: squares between two arrows, from this fraction of the arrow length out to <see cref="PlaneOuter"/>.</summary>
    private const double PlaneInner = 0.26, PlaneOuter = 0.46;
    /// <summary>A plane seen closer to edge-on than this (|cos| between its normal and the view) has no plane handle.</summary>
    private const float PlaneMinFacing = 0.2f;

    /// <summary>Half the size of a scale handle's cube on screen (DIPs).</summary>
    private const double ScaleCubeHalf = 5.5;

    /// <summary>Scale-factor snap step with Ctrl held.</summary>
    public const double ScaleSnap = 0.05;

    public GizmoLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;
    }

    public OrbitCamera? Camera { get; set; }

    /// <summary>The bone under a point (the overlay's picking), so a joint wins over the trackball.</summary>
    public Func<Point, int>? BonePicker { get; set; }

    /// <summary>Whether a picked bone is selected (a selected joint inside the trackball does not take the click).</summary>
    public Func<int, bool>? IsBoneSelected { get; set; }

    /// <summary>The document's gizmo target (null for a dialog's preview viewport).</summary>
    public IGizmoTarget? Controller
    {
        get => _controller;
        set
        {
            if (ReferenceEquals(_controller, value)) return;
            if (_controller is not null)
            {
                _controller.Changed -= OnControllerChanged;
                if (_controller.IsDragging) _controller.CommitDrag();
            }
            _controller = value;
            _dragHandle = GizmoHandle.None;
            _hover = GizmoHandle.None;
            if (_controller is not null) _controller.Changed += OnControllerChanged;
            InvalidateVisual();
        }
    }

    /// <summary>The handle under the mouse, or <see cref="GizmoHandle.None"/>.</summary>
    public int HoverHandle => _hover;

    /// <summary>True during a gizmo drag.</summary>
    public bool IsDragging => _dragHandle != GizmoHandle.None;

    /// <summary>Forgets cached pens (theme or DPI changed).</summary>
    public void ResetResources()
    {
        _outline = null;
        _texts.Clear();
        InvalidateVisual();
    }

    private void OnControllerChanged(object? sender, EventArgs e) => InvalidateVisual();

    // ── Geometry ─────────────────────────────────────────────────────────────

    private readonly record struct Geometry(
        PoseGizmo Kind, Vector3 Centre, Point CentreScreen, double WorldPerPixel, float Radius,
        Quaternion Frame, Vector3 ToEye, Projector Projector, Size Size, double RadiusScreen = -1)
    {
        /// <summary>True when the radius handle is drawn (a collision sphere's outline).</summary>
        public bool HasRadius => RadiusScreen >= 0;

        /// <summary>The radius handle's grip on screen: on the outline up and to the left, never inside the move square.</summary>
        public Point Grip
        {
            get
            {
                double r = Math.Max(RadiusScreen, GripMinimum);
                return new Point(CentreScreen.X + Math.Cos(GripAngle) * r, CentreScreen.Y + Math.Sin(GripAngle) * r);
            }
        }
    }

    private bool TryGeometry(out Geometry g)
    {
        g = default;
        if (_controller is not { } c || Camera is not { } camera) return false;
        if (c.Gizmo == PoseGizmo.None || ActualWidth < 2 || ActualHeight < 2) return false;
        if (!c.TryGetPlacement(out var centre, out var placementFrame)) return false;
        var size = new Size(ActualWidth, ActualHeight);
        var projector = new Projector(camera, size);
        if (!projector.Point(centre, out var cs)) return false;
        var right = camera.Right;
        float probe = (float)Math.Max(1e-3, camera.Distance * 0.05);
        if (!projector.Point(centre + right * probe, out var rs)) return false;
        double pixels = (rs - cs).Length;
        if (pixels < 1e-6) return false;
        double wpp = probe / pixels;
        var toEye = camera.IsPerspective ? Vector3.Normalize(camera.Eye - centre) : camera.Backward;
        if (!float.IsFinite(toEye.X)) toEye = camera.Backward;
        double radiusScreen = -1;
        if (c.HasRadiusHandle && c.Gizmo == PoseGizmo.Move)
        {
            // Projected as the overlay draws the sphere's outline (the camera's up vector).
            radiusScreen = projector.Point(centre + camera.Up * Math.Max(0.001f, c.GizmoRadius), out var edge) ? (edge - cs).Length : 0;
        }
        g = new Geometry(c.Gizmo, centre, cs, wpp, (float)(RingRadius * wpp), placementFrame, toEye, projector, size, radiusScreen);
        return true;
    }

    private static (Vector3 U, Vector3 V) Basis(Vector3 axis)
    {
        var u = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
        var v = Vector3.Cross(axis, u);
        return (u, v);
    }

    private static Vector3 AxisVector(int handle) => handle switch
    {
        GizmoHandle.X => Vector3.UnitX,
        GizmoHandle.Y => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>The projected ring of one axis: points and whether each faces the camera.</summary>
    private static void RingPoints(in Geometry g, Vector3 axis, Span<Point> points, Span<bool> front, Span<bool> visible)
    {
        var (u, v) = Basis(axis);
        for (int i = 0; i < points.Length; i++)
        {
            double a = 2 * Math.PI * i / points.Length;
            var offset = (u * (float)Math.Cos(a) + v * (float)Math.Sin(a)) * g.Radius;
            visible[i] = g.Projector.Point(g.Centre + offset, out points[i]);
            front[i] = Vector3.Dot(offset, g.ToEye) >= -1e-6f * g.Radius;
        }
    }

    // ── Diagnostics (the pose-gizmo self-test drives the mouse path with these) ─

    /// <summary>The gizmo centre on screen, or null when no gizmo is drawn.</summary>
    public Point? CentreScreen => TryGeometry(out var g) ? g.CentreScreen : null;

    /// <summary>The screen point of an axis ring at <paramref name="radians"/> round it (from its U axis towards V).</summary>
    public Point? RingPoint(int axis, double radians)
    {
        if (!TryGeometry(out var g)) return null;
        var (u, v) = Basis(Quat.Rotate(g.Frame, AxisVector(axis)));
        var world = g.Centre + (u * (float)Math.Cos(radians) + v * (float)Math.Sin(radians)) * g.Radius;
        return g.Projector.Point(world, out var s) ? s : null;
    }

    /// <summary>The screen point <paramref name="metres"/> along a move axis from the joint.</summary>
    public Point? AxisPoint(int axis, double metres)
    {
        if (!TryGeometry(out var g)) return null;
        var world = g.Centre + Quat.Rotate(g.Frame, AxisVector(axis)) * (float)metres;
        return g.Projector.Point(world, out var s) ? s : null;
    }

    /// <summary>World length of the drawn arrows (metres at the joint's depth).</summary>
    public double ArrowMetres => TryGeometry(out var g) ? ArrowLength * g.WorldPerPixel : 0;

    /// <summary>The radius handle's grip on screen, or null when there is none.</summary>
    public Point? RadiusGrip => TryGeometry(out var g) && g.HasRadius ? g.Grip : null;

    /// <summary>The screen point <paramref name="metres"/> from the gizmo centre towards the grip, in the plane facing the camera.</summary>
    public Point? RadiusPoint(double metres)
    {
        if (!TryGeometry(out var g) || !g.HasRadius || Camera is not { } camera) return null;
        // Towards the grip on screen, kept in the plane through the centre facing the eye (what the drag measures in).
        var direction = camera.Right * (float)Math.Cos(GripAngle) - camera.Up * (float)Math.Sin(GripAngle);
        direction -= g.ToEye * Vector3.Dot(direction, g.ToEye);
        if (direction.LengthSquared() < 1e-12f) return null;
        direction = Vector3.Normalize(direction);
        return g.Projector.Point(g.Centre + direction * (float)metres, out var s) ? s : null;
    }

    /// <summary>
    /// The screen circle the drawn gizmo covers (the screen ring, or the arrows with their heads), or null
    /// when no gizmo is drawn: the overlay keeps bone labels outside it.
    /// </summary>
    public (Point Centre, double Radius)? Footprint
    {
        get
        {
            if (!TryGeometry(out var g)) return null;
            if (g.Kind == PoseGizmo.Rotate) return (g.CentreScreen, RingRadius * ScreenRingFactor + 2);
            // Move: the arrows as drawn (foreshortened ones are shorter), heads included; at least the square.
            double radius = 12;
            for (int axis = 0; axis < 3; axis++)
            {
                if (ArrowEnd(g, axis, out var end)) radius = Math.Max(radius, (end - g.CentreScreen).Length + 6);
            }
            if (g.HasRadius) radius = Math.Max(radius, Math.Max(g.RadiusScreen, GripMinimum) + GripRadius + 4);
            return (g.CentreScreen, radius);
        }
    }

    // ── Hit testing ──────────────────────────────────────────────────────────

    /// <summary>The handle under <paramref name="p"/>, or <see cref="GizmoHandle.None"/>.</summary>
    public int HitTest(Point p)
    {
        if (!TryGeometry(out var g)) return GizmoHandle.None;
        double fromCentre = (p - g.CentreScreen).Length;
        if (g.Kind == PoseGizmo.Rotate)
        {
            int best = GizmoHandle.None;
            double bestDistance = HitTolerance;
            Span<Point> points = stackalloc Point[RingSegments];
            Span<bool> front = stackalloc bool[RingSegments];
            Span<bool> visible = stackalloc bool[RingSegments];
            for (int axis = 0; axis < 3; axis++)
            {
                RingPoints(g, Quat.Rotate(g.Frame, AxisVector(axis)), points, front, visible);
                for (int i = 0; i < RingSegments; i++)
                {
                    int j = (i + 1) % RingSegments;
                    if (!visible[i] || !visible[j] || !(front[i] || front[j])) continue;
                    double d = DistanceToSegment(p, points[i], points[j]);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = axis;
                    }
                }
            }
            if (best != GizmoHandle.None) return best;
            if (Math.Abs(fromCentre - RingRadius * ScreenRingFactor) < HitTolerance) return GizmoHandle.Screen;
            if (fromCentre < RingRadius * 0.94)
            {
                int bone = BonePicker?.Invoke(p) ?? -1;
                if (bone >= 0 && bone != _controller!.ActiveBone && (IsBoneSelected?.Invoke(bone) != true)) return GizmoHandle.None;
                return GizmoHandle.Free;
            }
            return GizmoHandle.None;
        }

        if (Math.Max(Math.Abs(p.X - g.CentreScreen.X), Math.Abs(p.Y - g.CentreScreen.Y)) < 9) return GizmoHandle.Screen;
        if (g.Kind == PoseGizmo.Scale)
        {
            // The cubes at the axis ends win; then the shafts.
            int bestCube = GizmoHandle.None;
            double bestCubeDistance = ScaleCubeHalf + 3;
            for (int axis = 0; axis < 3; axis++)
            {
                if (!ArrowEnd(g, axis, out var end)) continue;
                double d = Math.Max(Math.Abs(p.X - end.X), Math.Abs(p.Y - end.Y));
                if (d < bestCubeDistance) { bestCubeDistance = d; bestCube = axis; }
            }
            if (bestCube != GizmoHandle.None) return bestCube;
        }
        // The radius grip is a deliberate target: it wins over an arrow running under it.
        if (g.HasRadius && (p - g.Grip).Length <= GripRadius + 3) return GizmoHandle.Radius;
        // Plane squares sit between the arrows, clear of the shafts.
        if (g.Kind != PoseGizmo.Scale || _controller is IGizmoScaleTarget)
        {
            int plane = HitPlane(g, p);
            if (plane != GizmoHandle.None) return plane;
        }
        int bestArrow = GizmoHandle.None;
        double bestArrowDistance = HitTolerance;
        for (int axis = 0; axis < 3; axis++)
        {
            if (!ArrowEnd(g, axis, out var end)) continue;
            double d = DistanceToSegment(p, g.CentreScreen, end);
            if (d < bestArrowDistance)
            {
                bestArrowDistance = d;
                bestArrow = axis;
            }
        }
        if (bestArrow == GizmoHandle.None && g.HasRadius && g.RadiusScreen > 12 && Math.Abs(fromCentre - g.RadiusScreen) < HitTolerance)
            return GizmoHandle.Radius;
        return bestArrow;
    }

    /// <summary>
    /// The screen corners of the plane handle perpendicular to <paramref name="normalAxis"/>: a square spanning the
    /// other two axes between <see cref="PlaneInner"/> and <see cref="PlaneOuter"/> of the arrow length. False when the
    /// plane is seen nearly edge-on (a drag in it would be unstable) or a corner is behind the camera.
    /// </summary>
    private static bool PlaneQuad(in Geometry g, int normalAxis, Span<Point> corners)
    {
        var normal = Quat.Rotate(g.Frame, AxisVector(normalAxis));
        if (MathF.Abs(Vector3.Dot(normal, g.ToEye)) < PlaneMinFacing) return false;
        var u = Quat.Rotate(g.Frame, AxisVector((normalAxis + 1) % 3));
        var v = Quat.Rotate(g.Frame, AxisVector((normalAxis + 2) % 3));
        float length = (float)(ArrowLength * g.WorldPerPixel);
        float a = (float)PlaneInner * length, b = (float)PlaneOuter * length;
        return g.Projector.Point(g.Centre + u * a + v * a, out corners[0])
            && g.Projector.Point(g.Centre + u * b + v * a, out corners[1])
            && g.Projector.Point(g.Centre + u * b + v * b, out corners[2])
            && g.Projector.Point(g.Centre + u * a + v * b, out corners[3]);
    }

    /// <summary>True when <paramref name="p"/> is inside the convex quad (either winding).</summary>
    private static bool InQuad(Point p, ReadOnlySpan<Point> q)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            var a = q[i];
            var b = q[(i + 1) % 4];
            double cross = (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
            int s = Math.Sign(cross);
            if (s == 0) continue;
            if (sign == 0) sign = s;
            else if (s != sign) return false;
        }
        return true;
    }

    /// <summary>The plane handle under <paramref name="p"/>, or <see cref="GizmoHandle.None"/>.</summary>
    private static int HitPlane(in Geometry g, Point p)
    {
        Span<Point> quad = stackalloc Point[4];
        for (int n = 0; n < 3; n++)
            if (PlaneQuad(g, n, quad) && InQuad(p, quad)) return GizmoHandle.PlaneOf(n);
        return GizmoHandle.None;
    }

    /// <summary>The screen point at the middle of a plane handle (self-tests drive drags from here).</summary>
    public Point? PlanePoint(int normalAxis)
    {
        if (!TryGeometry(out var g)) return null;
        Span<Point> quad = stackalloc Point[4];
        if (!PlaneQuad(g, normalAxis, quad)) return null;
        return new Point((quad[0].X + quad[2].X) / 2, (quad[0].Y + quad[2].Y) / 2);
    }

    private static bool ArrowEnd(in Geometry g, int axis, out Point end)
    {
        var dir = Quat.Rotate(g.Frame, AxisVector(axis));
        float length = (float)(ArrowLength * g.WorldPerPixel);
        if (!g.Projector.Point(g.Centre + dir * length, out end)) return false;
        // An axis pointing at the camera has no usable screen direction.
        return (end - g.CentreScreen).Length > 14;
    }

    /// <summary>Updates the hover highlight; true when it changed.</summary>
    public bool UpdateHover(Point p)
    {
        _lastPoint = p;
        _hasPointer = true;
        int hover = IsDragging ? _dragHandle : HitTest(p);
        if (hover == _hover) return false;
        _hover = hover;
        InvalidateVisual();
        return true;
    }

    /// <summary>Clears the hover highlight (the mouse left).</summary>
    public void ClearHover()
    {
        _hasPointer = false;
        if (_hover == GizmoHandle.None) return;
        _hover = GizmoHandle.None;
        InvalidateVisual();
    }

    // ── Dragging ─────────────────────────────────────────────────────────────

    /// <summary>Starts a drag when <paramref name="p"/> is on a handle; false lets the click through.</summary>
    public bool TryBeginDrag(Point p)
    {
        if (_controller is not { } c || Camera is null) return false;
        int handle = HitTest(p);
        if (handle == GizmoHandle.None || !TryGeometry(out var g)) return false;
        if (handle == GizmoHandle.Radius) return TryBeginRadiusDrag(c, g, p);
        if (g.Kind == PoseGizmo.Scale) return TryBeginScaleDrag(c, g, p, handle);
        var kind = g.Kind == PoseGizmo.Rotate ? PoseDragKind.Rotate : c.MoveDragKind;
        // A plane square is a free move kept in its plane: targets see a screen-plane drag (world translations).
        if (!c.BeginDrag(kind, GizmoHandle.IsPlane(handle) ? GizmoHandle.Screen : handle)) return false;
        _scaling = false;
        _dragHandle = handle;
        _hover = handle;
        _dragStart = _lastPoint = p;
        _hasPointer = true;
        _moved = false;
        _dragCentre = g.Centre;
        _angleTotal = 0;
        _free = Quaternion.Identity;
        _screenNormal = g.ToEye;
        _rotating = kind == PoseDragKind.Rotate;
        if (_rotating)
        {
            _dragAxis = handle == GizmoHandle.Screen ? g.ToEye : handle == GizmoHandle.Free ? Vector3.Zero : Quat.Rotate(g.Frame, AxisVector(handle));
            if (handle != GizmoHandle.Free)
            {
                (_planeU, _planeV) = Basis(_dragAxis);
                var ray = Ray(p, g.Size);
                double facing = Math.Abs(Vector3.Dot(Vector3.Normalize(ray.Direction), _dragAxis));
                _planeMode = handle == GizmoHandle.Screen || facing > 0.2;
                if (_planeMode && PlaneAngle(p, g.Size, out double a))
                {
                    _lastRawAngle = a;
                }
                else
                {
                    _planeMode = false;
                    // Edge-on ring: drag along the screen tangent of the ring at the grabbed point.
                    var grab = NearestRingPoint(g, _dragAxis, p);
                    const float eps = 0.02f;
                    var turned = g.Centre + Quat.Rotate(Quat.FromAxisAngle(_dragAxis, eps), grab - g.Centre);
                    if (g.Projector.Point(grab, out var gs) && g.Projector.Point(turned, out var ts)) _tangent = (ts - gs) / eps;
                    if (_tangent.Length < 1e-3) _tangent = new Vector(RingRadius, 0);
                }
            }
        }
        else
        {
            if (handle == GizmoHandle.Screen)
            {
                _planeStart = PlanePoint(p, g.Size, _dragCentre, _screenNormal) ?? _dragCentre;
            }
            else if (GizmoHandle.IsPlane(handle))
            {
                _planeNormal = Quat.Rotate(g.Frame, AxisVector(GizmoHandle.PlaneNormal(handle)));
                _planeStart = PlanePoint(p, g.Size, _dragCentre, _planeNormal) ?? _dragCentre;
            }
            else
            {
                _dragAxis = Quat.Rotate(g.Frame, AxisVector(handle));
                _axisStart = AxisParameter(p, g.Size) ?? 0;
            }
        }
        InvalidateVisual();
        return true;
    }

    private bool TryBeginScaleDrag(IGizmoTarget c, in Geometry g, Point p, int handle)
    {
        if (c is not IGizmoScaleTarget || handle is not (GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z or GizmoHandle.Screen
            or GizmoHandle.PlaneYZ or GizmoHandle.PlaneZX or GizmoHandle.PlaneXY)) return false;
        if (!c.BeginDrag(PoseDragKind.Scale, handle)) return false;
        _dragHandle = handle;
        _hover = handle;
        _dragStart = _lastPoint = p;
        _hasPointer = true;
        _moved = false;
        _rotating = false;
        _scaling = true;
        _dragCentre = g.Centre;
        _screenNormal = g.ToEye;
        _scaleArm = ArrowLength * g.WorldPerPixel;
        if (GizmoHandle.IsPlane(handle))
        {
            // Scales by how far from the centre the mouse is in the plane, relative to where the drag started.
            _planeNormal = Quat.Rotate(g.Frame, AxisVector(GizmoHandle.PlaneNormal(handle)));
            var start = PlanePoint(p, g.Size, _dragCentre, _planeNormal);
            _scalePlaneStart = start is { } s ? Vector3.Distance(s, _dragCentre) : 0;
            if (_scalePlaneStart < 1e-6) _scalePlaneStart = (PlaneInner + PlaneOuter) / 2 * Math.Sqrt(2) * _scaleArm;
        }
        else if (handle != GizmoHandle.Screen)
        {
            _dragAxis = Quat.Rotate(g.Frame, AxisVector(handle));
            _axisStart = AxisParameter(p, g.Size) ?? _scaleArm;
        }
        InvalidateVisual();
        return true;
    }

    /// <summary>
    /// Scale factor of a drag to <paramref name="p"/>: an axis cube scales by how far along its axis the mouse went
    /// relative to the arm length (dragging the cube to twice the arm doubles); the centre scales uniformly by the
    /// screen drag right/up (one arm length of pixels = ×2). Ctrl snaps to 0.05.
    /// </summary>
    private double? ScaleFactor(Point p, Size size, bool snap)
    {
        double factor;
        if (_dragHandle == GizmoHandle.Screen)
        {
            factor = 1 + ((p.X - _dragStart.X) - (p.Y - _dragStart.Y)) / ArrowLength;
        }
        else if (GizmoHandle.IsPlane(_dragHandle))
        {
            if (PlanePoint(p, size, _dragCentre, _planeNormal) is not { } hit || _scalePlaneStart <= 0) return null;
            factor = Vector3.Distance(hit, _dragCentre) / _scalePlaneStart;
        }
        else
        {
            if (AxisParameter(p, size) is not { } s || _scaleArm <= 0) return null;
            factor = 1 + (s - _axisStart) / _scaleArm;
        }
        if (snap) factor = Snap(factor, ScaleSnap);
        return Math.Max(0.01, factor);
    }

    private bool TryBeginRadiusDrag(IGizmoTarget c, in Geometry g, Point p)
    {
        if (!c.BeginRadiusDrag()) return false;
        _scaling = false;
        _dragHandle = GizmoHandle.Radius;
        _hover = GizmoHandle.Radius;
        _dragStart = _lastPoint = p;
        _hasPointer = true;
        _moved = false;
        _rotating = false;
        _dragCentre = g.Centre;
        _screenNormal = g.ToEye;
        _radiusStart = c.GizmoRadius;
        _radiusGrab = PlanePoint(p, g.Size, _dragCentre, _screenNormal) is { } grab ? Vector3.Distance(grab, _dragCentre) : _radiusStart;
        InvalidateVisual();
        return true;
    }

    /// <summary>Feeds a mouse move of the drag in progress.</summary>
    public void Drag(Point p, ModifierKeys modifiers)
    {
        if (_controller is not { } c || !IsDragging) return;
        if (!c.IsDragging)
        {
            _dragHandle = GizmoHandle.None;
            InvalidateVisual();
            return;
        }
        var previous = _lastPoint;
        _lastPoint = p;
        _hasPointer = true;
        if (!_moved && (p - _dragStart).Length < 2) return;
        _moved = true;
        bool snap = modifiers.HasFlag(ModifierKeys.Control);
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        int handle = c.DragHandle;
        if (_dragHandle == GizmoHandle.Radius)
        {
            if (PlanePoint(p, size, _dragCentre, _screenNormal) is not { } point) return;
            double radius = _radiusStart + (Vector3.Distance(point, _dragCentre) - _radiusGrab);
            if (snap) radius = Math.Max(DistanceSnapMetres, Snap(radius, DistanceSnapMetres));
            c.UpdateRadius(radius);
        }
        else if (_scaling)
        {
            if (c is not IGizmoScaleTarget st || ScaleFactor(p, size, snap) is not { } factor) return;
            st.UpdateScale(_dragHandle, factor);
        }
        else if (_rotating)
        {
            if (handle == GizmoHandle.Free)
            {
                var delta = p - previous;
                if (delta.Length < 1e-9 || Camera is not { } camera) return;
                var drag = camera.Right * (float)delta.X - camera.Up * (float)delta.Y;
                var axis = Vector3.Cross(_screenNormal, Vector3.Normalize(drag));
                _free = Quat.Normalize(Quat.Mul(Quat.FromAxisAngle(axis, (float)(delta.Length / RingRadius)), _free));
                var shown = _free;
                if (snap) shown = SnapRotation(shown);
                c.UpdateWorldRotation(shown, GizmoHandle.Free);
            }
            else
            {
                double angle;
                if (_planeMode)
                {
                    if (!PlaneAngle(p, size, out double raw)) return;
                    double step = raw - _lastRawAngle;
                    while (step > Math.PI) step -= 2 * Math.PI;
                    while (step < -Math.PI) step += 2 * Math.PI;
                    _angleTotal += step;
                    _lastRawAngle = raw;
                    angle = _angleTotal;
                }
                else
                {
                    var moved = p - _dragStart;
                    angle = (moved.X * _tangent.X + moved.Y * _tangent.Y) / _tangent.LengthSquared;
                }
                if (snap) angle = Snap(angle, AngleSnapDegrees * Math.PI / 180);
                // The screen ring's axis points at the viewer; the view is mirrored (left-handed), so a positive turn
                // about it reads clockwise on screen. The readout counts anticlockwise on screen as positive.
                if (handle == GizmoHandle.Screen)
                    c.UpdateWorldRotation(Quat.FromAxisAngle(_dragAxis, (float)angle), GizmoHandle.Screen, -angle * 180 / Math.PI);
                else
                    c.UpdateAxisRotation(handle, angle);
            }
        }
        else
        {
            if (GizmoHandle.IsPlane(_dragHandle))
            {
                if (PlanePoint(p, size, _dragCentre, _planeNormal) is not { } point) return;
                var delta = point - _planeStart;
                // Keep exactly in the plane (float error) before snapping.
                delta -= _planeNormal * Vector3.Dot(delta, _planeNormal);
                if (snap) delta = new Vector3(SnapF(delta.X), SnapF(delta.Y), SnapF(delta.Z));
                c.UpdateWorldTranslation(delta);
            }
            else if (c.DragHandle == GizmoHandle.Screen)
            {
                if (PlanePoint(p, size, _dragCentre, _screenNormal) is not { } point) return;
                var delta = point - _planeStart;
                if (snap) delta = new Vector3(SnapF(delta.X), SnapF(delta.Y), SnapF(delta.Z));
                c.UpdateWorldTranslation(delta);
            }
            else
            {
                if (AxisParameter(p, size) is not { } s) return;
                double metres = s - _axisStart;
                if (snap) metres = Snap(metres, DistanceSnapMetres);
                c.UpdateAxisTranslation(c.DragHandle, metres);
            }
        }
        InvalidateVisual();
    }

    /// <summary>Ends the drag as one undo step.</summary>
    public void EndDrag()
    {
        if (!IsDragging) return;
        _dragHandle = GizmoHandle.None;
        _controller?.CommitDrag();
        InvalidateVisual();
    }

    /// <summary>Abandons the drag (Esc): the clip goes back to the pre-drag snapshot.</summary>
    public void CancelDrag()
    {
        if (!IsDragging) return;
        _dragHandle = GizmoHandle.None;
        _controller?.CancelDrag();
        InvalidateVisual();
    }

    private static double Snap(double value, double step) => Math.Round(value / step) * step;

    private static float SnapF(float value) => (float)Snap(value, DistanceSnapMetres);

    private static Quaternion SnapRotation(Quaternion q)
    {
        q = Quat.Normalize(q);
        if (q.W < 0) q = Quat.Negate(q);
        var axis = new Vector3(q.X, q.Y, q.Z);
        float s = axis.Length();
        if (s < 1e-7f) return Quaternion.Identity;
        double angle = 2 * Math.Atan2(s, q.W);
        angle = Snap(angle, AngleSnapDegrees * Math.PI / 180);
        return Quat.FromAxisAngle(axis / s, (float)angle);
    }

    private readonly record struct WorldRay(Vector3 Origin, Vector3 Direction);

    /// <summary>The view ray through a screen point (inverse of the overlay's projection).</summary>
    private WorldRay Ray(Point p, Size size)
    {
        var vp = Camera!.ViewProjection(size);
        if (!vp.HasInverse) return new WorldRay(Camera.Eye, Camera.Forward);
        vp.Invert();
        double nx = p.X / size.Width * 2 - 1, ny = 1 - p.Y / size.Height * 2;
        var near = vp.Transform(new Point4D(nx, ny, 0, 1));
        var far = vp.Transform(new Point4D(nx, ny, 1, 1));
        var a = new Vector3((float)(near.X / near.W), (float)(near.Y / near.W), (float)(near.Z / near.W));
        var b = new Vector3((float)(far.X / far.W), (float)(far.Y / far.W), (float)(far.Z / far.W));
        var dir = b - a;
        return dir.LengthSquared() > 0 ? new WorldRay(a, Vector3.Normalize(dir)) : new WorldRay(Camera.Eye, Camera.Forward);
    }

    private Vector3? PlanePoint(Point p, Size size, Vector3 origin, Vector3 normal)
    {
        var ray = Ray(p, size);
        float denom = Vector3.Dot(ray.Direction, normal);
        if (MathF.Abs(denom) < 1e-5f) return null;
        float t = Vector3.Dot(origin - ray.Origin, normal) / denom;
        return ray.Origin + ray.Direction * t;
    }

    private bool PlaneAngle(Point p, Size size, out double angle)
    {
        angle = 0;
        if (PlanePoint(p, size, _dragCentre, _dragAxis) is not { } hit) return false;
        var d = hit - _dragCentre;
        float x = Vector3.Dot(d, _planeU), y = Vector3.Dot(d, _planeV);
        if (x * x + y * y < 1e-12f) return false;
        // Angle measured from U towards V: a positive step is a positive rotation about the axis (V = axis × U).
        angle = Math.Atan2(y, x);
        return true;
    }

    /// <summary>The parameter along the drag axis (metres from the joint) of the point closest to the mouse ray.</summary>
    private double? AxisParameter(Point p, Size size)
    {
        var ray = Ray(p, size);
        var d = _dragAxis;
        var r = ray.Direction;
        var w0 = _dragCentre - ray.Origin;
        float b = Vector3.Dot(d, r);
        float denom = 1 - b * b;
        if (denom < 1e-4f) return null;
        float dd = Vector3.Dot(d, w0), e = Vector3.Dot(r, w0);
        return (b * e - dd) / denom;
    }

    private static Vector3 NearestRingPoint(in Geometry g, Vector3 axis, Point p)
    {
        var (u, v) = Basis(axis);
        var best = g.Centre + u * g.Radius;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < RingSegments; i++)
        {
            double a = 2 * Math.PI * i / RingSegments;
            var world = g.Centre + (u * (float)Math.Cos(a) + v * (float)Math.Sin(a)) * g.Radius;
            if (!g.Projector.Point(world, out var s)) continue;
            double d = (s - p).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = world;
            }
        }
        return best;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    protected override void OnRender(DrawingContext dc)
    {
        if (!TryGeometry(out var g)) return;
        EnsureResources();
        int active = IsDragging ? _dragHandle : _controller!.IsDragging ? _controller.DragHandle : GizmoHandle.None;
        int hover = active != GizmoHandle.None ? active : _hover;
        if (g.Kind == PoseGizmo.Rotate) DrawRotate(dc, g, hover, active);
        else if (g.Kind == PoseGizmo.Scale) DrawScale(dc, g, hover, active);
        else DrawMove(dc, g, hover, active);
        if (g.HasRadius && (active == GizmoHandle.None || active == GizmoHandle.Radius)) DrawRadius(dc, g, hover == GizmoHandle.Radius);

        if (_controller!.Readout is { } readout)
        {
            var at = _hasPointer && (IsDragging || _controller.IsDragging) && _moved
                ? new Point(_lastPoint.X + 18, _lastPoint.Y + 12)
                : new Point(g.CentreScreen.X + RingRadius * 0.9, g.CentreScreen.Y - RingRadius * 1.3);
            DrawReadout(dc, readout, at);
        }
    }

    private void DrawRotate(DrawingContext dc, in Geometry g, int hover, int active)
    {
        bool dragging = active != GizmoHandle.None;
        // Trackball disc.
        if (!dragging || active == GizmoHandle.Free)
            dc.DrawEllipse(hover == GizmoHandle.Free ? _trackballHover : _trackball, null, g.CentreScreen, RingRadius, RingRadius);

        Span<Point> points = stackalloc Point[RingSegments];
        Span<bool> front = stackalloc bool[RingSegments];
        Span<bool> visible = stackalloc bool[RingSegments];
        var rings = new (Point[] Points, bool[] Front, bool[] Visible)[3];
        for (int axis = 0; axis < 3; axis++)
        {
            RingPoints(g, Quat.Rotate(g.Frame, AxisVector(axis)), points, front, visible);
            rings[axis] = (points.ToArray(), front.ToArray(), visible.ToArray());
        }
        // Back halves first (thin, dim), then the outlined front halves.
        for (int axis = 0; axis < 3; axis++)
        {
            if (dragging && active != axis) continue;
            var (p, f, v) = rings[axis];
            for (int i = 0; i < RingSegments; i++)
            {
                int j = (i + 1) % RingSegments;
                if (v[i] && v[j] && !(f[i] || f[j])) dc.DrawLine(_axisBackPen[axis]!, p[i], p[j]);
            }
        }
        for (int axis = 0; axis < 3; axis++)
        {
            if (dragging && active != axis) continue;
            var (p, f, v) = rings[axis];
            var geometry = FrontGeometry(p, f, v);
            if (geometry is null) continue;
            bool hot = hover == axis;
            dc.DrawGeometry(null, hot ? _hoverOutline : _outline, geometry);
            dc.DrawGeometry(null, hot ? _hoverPen : _axisPen[axis], geometry);
        }
        // Screen ring.
        if (!dragging || active == GizmoHandle.Screen)
        {
            double r = RingRadius * ScreenRingFactor;
            bool hot = hover == GizmoHandle.Screen;
            dc.DrawEllipse(null, _outlineThin, g.CentreScreen, r, r);
            dc.DrawEllipse(null, hot ? _hoverPen : _screenPen, g.CentreScreen, r, r);
        }
        // Centre dot.
        dc.DrawEllipse(_screenBrush, _outlineThin, g.CentreScreen, 2.5, 2.5);
    }

    private static StreamGeometry? FrontGeometry(Point[] p, bool[] f, bool[] v)
    {
        var geometry = new StreamGeometry();
        bool any = false;
        using (var ctx = geometry.Open())
        {
            bool open = false;
            // Start where a front run begins so a run crossing index 0 stays one figure.
            int start = 0;
            for (int i = 0; i < p.Length; i++)
            {
                int prev = (i + p.Length - 1) % p.Length;
                if (!(f[prev] && v[prev]) && f[i] && v[i])
                {
                    start = i;
                    break;
                }
            }
            for (int k = 0; k <= p.Length; k++)
            {
                int i = (start + k) % p.Length;
                bool on = f[i] && v[i];
                if (on)
                {
                    if (!open)
                    {
                        ctx.BeginFigure(p[i], false, false);
                        open = true;
                    }
                    else
                    {
                        ctx.LineTo(p[i], true, true);
                        any = true;
                    }
                }
                else
                {
                    open = false;
                }
            }
        }
        if (!any) return null;
        geometry.Freeze();
        return geometry;
    }

    /// <summary>True when <paramref name="axis"/> lies in the plane of the plane handle <paramref name="active"/>.</summary>
    private static bool InActivePlane(int active, int axis) => GizmoHandle.IsPlane(active) && GizmoHandle.PlaneNormal(active) != axis;

    /// <summary>The plane squares: translucent in the colour of the axis they are perpendicular to (as Blender does).</summary>
    private void DrawPlanes(DrawingContext dc, in Geometry g, int hover, int active)
    {
        bool dragging = active != GizmoHandle.None;
        Span<Point> quad = stackalloc Point[4];
        for (int n = 0; n < 3; n++)
        {
            int handle = GizmoHandle.PlaneOf(n);
            if (dragging && active != handle) continue;
            if (!PlaneQuad(g, n, quad)) continue;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(quad[0], true, true);
                ctx.LineTo(quad[1], true, true);
                ctx.LineTo(quad[2], true, true);
                ctx.LineTo(quad[3], true, true);
            }
            geometry.Freeze();
            bool hot = hover == handle;
            dc.DrawGeometry(hot ? _hoverBrush : _planeBrush[n], hot ? _outline : _outlineThin, geometry);
            if (!hot) dc.DrawGeometry(null, _planePen[n], geometry);
        }
    }

    private void DrawMove(DrawingContext dc, in Geometry g, int hover, int active)
    {
        bool dragging = active != GizmoHandle.None;
        DrawPlanes(dc, g, hover, active);
        for (int axis = 0; axis < 3; axis++)
        {
            if (dragging && active != axis && !InActivePlane(active, axis)) continue;
            if (!ArrowEnd(g, axis, out var end)) continue;
            bool hot = hover == axis;
            var dir = end - g.CentreScreen;
            dir.Normalize();
            var start = g.CentreScreen + dir * 11;
            var shaftEnd = end - dir * 9;
            dc.DrawLine(hot ? _hoverOutline! : _outline!, start, shaftEnd);
            dc.DrawLine(hot ? _hoverPen! : _axisPen[axis]!, start, shaftEnd);
            var normal = new Vector(-dir.Y, dir.X);
            var head = new StreamGeometry();
            using (var ctx = head.Open())
            {
                ctx.BeginFigure(end + dir * 3, true, true);
                ctx.LineTo(end - dir * 11 + normal * 6, true, true);
                ctx.LineTo(end - dir * 11 - normal * 6, true, true);
            }
            head.Freeze();
            dc.DrawGeometry(hot ? _hoverBrush : _axisBrush[axis], _outlineThin, head);
        }
        if (!dragging || active == GizmoHandle.Screen)
        {
            bool hot = hover == GizmoHandle.Screen;
            var box = new Rect(g.CentreScreen.X - 7, g.CentreScreen.Y - 7, 14, 14);
            dc.DrawRectangle(hot ? _trackballHover : _trackball, _outline, box);
            dc.DrawRectangle(null, hot ? _hoverPen : _screenPen, box);
        }
    }

    /// <summary>Scale: shafts ending in small cubes (squares on screen) per axis and a uniform square at the centre.</summary>
    private void DrawScale(DrawingContext dc, in Geometry g, int hover, int active)
    {
        bool dragging = active != GizmoHandle.None;
        if (_controller is IGizmoScaleTarget) DrawPlanes(dc, g, hover, active);
        for (int axis = 0; axis < 3; axis++)
        {
            if (dragging && active != axis && active != GizmoHandle.Screen && !InActivePlane(active, axis)) continue;
            if (!ArrowEnd(g, axis, out var end)) continue;
            bool hot = hover == axis || active == GizmoHandle.Screen || InActivePlane(active, axis);
            var dir = end - g.CentreScreen;
            dir.Normalize();
            var start = g.CentreScreen + dir * 11;
            var shaftEnd = end - dir * ScaleCubeHalf;
            dc.DrawLine(hot ? _hoverOutline! : _outline!, start, shaftEnd);
            dc.DrawLine(hot ? _hoverPen! : _axisPen[axis]!, start, shaftEnd);
            var cube = new Rect(end.X - ScaleCubeHalf, end.Y - ScaleCubeHalf, 2 * ScaleCubeHalf, 2 * ScaleCubeHalf);
            dc.DrawRectangle(hover == axis ? _hoverBrush : _axisBrush[axis], _outlineThin, cube);
        }
        if (!dragging || active == GizmoHandle.Screen)
        {
            bool hot = hover == GizmoHandle.Screen;
            var box = new Rect(g.CentreScreen.X - 7, g.CentreScreen.Y - 7, 14, 14);
            dc.DrawRectangle(hot ? _hoverBrush : _screenBrush, _outline, box);
            dc.DrawRectangle(null, _outlineThin, box);
        }
    }

    /// <summary>The radius handle: a ring over the sphere's outline and a grip on it (joined by a short line when the sphere is tiny on screen).</summary>
    private void DrawRadius(DrawingContext dc, in Geometry g, bool hot)
    {
        double r = g.RadiusScreen;
        if (r > 1)
        {
            dc.DrawEllipse(null, _outlineThin, g.CentreScreen, r, r);
            dc.DrawEllipse(null, hot ? _hoverPen : _radiusPen, g.CentreScreen, r, r);
        }
        var grip = g.Grip;
        if (r < GripMinimum)
        {
            var onRing = new Point(g.CentreScreen.X + Math.Cos(GripAngle) * r, g.CentreScreen.Y + Math.Sin(GripAngle) * r);
            dc.DrawLine(_radiusPen!, onRing, grip);
        }
        dc.DrawEllipse(hot ? _hoverBrush : _screenBrush, _gripOutline, grip, GripRadius, GripRadius);
    }

    private void DrawReadout(DrawingContext dc, string text, Point at)
    {
        if (!_texts.TryGetValue(text, out var formatted))
        {
            if (_texts.Count > 256) _texts.Clear();
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 12, _readoutText!, _dpi);
            _texts[text] = formatted;
        }
        double x = Math.Clamp(at.X, 2, Math.Max(2, ActualWidth - formatted.Width - 10));
        double y = Math.Clamp(at.Y, 2, Math.Max(2, ActualHeight - formatted.Height - 6));
        var box = new Rect(x - 5, y - 2, formatted.Width + 10, formatted.Height + 4);
        dc.DrawRoundedRectangle(_readoutBackground, null, box, 3, 3);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 > 0 ? Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / len2, 0, 1) : 0;
        return (p - (a + ab * t)).Length;
    }

    private void EnsureResources()
    {
        if (_outline is not null) return;
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string[] keys = ["Gizmo.AxisX", "Gizmo.AxisY", "Gizmo.AxisZ"];
        for (int i = 0; i < 3; i++)
        {
            var brush = Brush(keys[i]);
            _axisBrush[i] = brush;
            _axisPen[i] = Frozen(new Pen(brush, 2.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            var dim = brush.CloneCurrentValue();
            dim.Opacity = 0.35;
            dim.Freeze();
            _axisBackPen[i] = Frozen(new Pen(dim, 1.1));
            var plane = brush.CloneCurrentValue();
            plane.Opacity = 0.4;
            plane.Freeze();
            _planeBrush[i] = plane;
            _planePen[i] = Frozen(new Pen(brush, 1.3) { LineJoin = PenLineJoin.Round });
        }
        var outline = Brush("Gizmo.Outline");
        _outline = Frozen(new Pen(outline, 5.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        _hoverOutline = Frozen(new Pen(outline, 6.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        _outlineThin = Frozen(new Pen(outline, 1.2));
        _screenBrush = Brush("Gizmo.Screen");
        _screenPen = Frozen(new Pen(_screenBrush, 1.8));
        _hoverBrush = Brush("Gizmo.Hover");
        _hoverPen = Frozen(new Pen(_hoverBrush, 3.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
        _radiusPen = Frozen(new Pen(_screenBrush, 1.8) { DashStyle = new DashStyle([4, 2.5], 0) });
        _gripOutline = Frozen(new Pen(outline, 1.6));
        _trackball = Brush("Gizmo.Trackball");
        _trackballHover = Brush("Gizmo.TrackballHover");
        _readoutBackground = Brush("Gizmo.ReadoutBackground");
        _readoutText = Brush("Gizmo.ReadoutText");
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
