using System.Numerics;
using System.Windows;
using System.Windows.Media.Media3D;

namespace Cairn.Viewport;

/// <summary>
/// The viewport camera: an orbit around <see cref="Target"/> with yaw, pitch and distance, and the
/// view and projection matrices the app computes itself for WPF's <see cref="MatrixCamera"/>.
/// </summary>
/// <remarks>
/// <para>
/// Core keeps everything in RF space: left-handed, +X right, +Y up, +Z forward. Nothing is mirrored on
/// the way in. The handedness lives in the view matrix: it is a left-handed look-at (Direct3D's
/// <c>LookAtLH</c>), so view space is +X right, +Y up, +Z into the screen, and the projection is
/// Direct3D's left-handed perspective/orthographic (clip z in [0, w]), which is what WPF's 3D pipeline
/// clips with. Seen from the front (camera on +Z looking down -Z), world -X is on the screen's right:
/// a character's left side appears on the viewer's right, as when facing a person.
/// </para>
/// <para>
/// The overlay uses <see cref="Project"/>, which runs the same two matrices and the same NDC to
/// viewport mapping WPF applies to a <c>Viewport3D</c> of the same size, so a joint drawn in 2D sits
/// exactly on the skinned mesh at any angle, projection and DPI (both work in device-independent units).
/// Matrices use WPF's row-vector convention (<c>p' = p * M</c>), like System.Numerics.
/// </para>
/// </remarks>
public sealed class OrbitCamera
{
    /// <summary>Vertical field of view of the perspective projection, in degrees.</summary>
    public const double FieldOfViewDegrees = 40;

    private const double MinPitch = -89.5, MaxPitch = 89.5;

    private double _pitch = 12;
    private double _distance = 4;

    /// <summary>Raised whenever the camera moves.</summary>
    public event EventHandler? Changed;

    /// <summary>The point orbited, in RF space.</summary>
    public Vector3 Target { get; set; } = new(0, 1, 0);

    /// <summary>Heading in degrees: 0 puts the camera on +Z looking back at -Z (the "front" view).</summary>
    public double Yaw { get; set; }

    /// <summary>Elevation in degrees, positive above the target.</summary>
    public double Pitch
    {
        get => _pitch;
        set => _pitch = Math.Clamp(value, MinPitch, MaxPitch);
    }

    /// <summary>Distance from the target in metres (also the orthographic view height scale).</summary>
    public double Distance
    {
        get => _distance;
        set => _distance = Math.Clamp(value, 0.02, 5000);
    }

    /// <summary>Perspective (true) or orthographic projection.</summary>
    public bool IsPerspective { get; set; } = true;

    /// <summary>True once the camera has been framed on something (auto-framing happens only once).</summary>
    public bool HasBeenFramed { get; set; }

    /// <summary>Tells listeners the camera moved (call after changing properties).</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Unit vector from the target towards the eye.</summary>
    public Vector3 Backward
    {
        get
        {
            double yaw = Yaw * Math.PI / 180, pitch = Pitch * Math.PI / 180;
            return new Vector3(
                (float)(Math.Sin(yaw) * Math.Cos(pitch)),
                (float)Math.Sin(pitch),
                (float)(Math.Cos(yaw) * Math.Cos(pitch)));
        }
    }

    /// <summary>The eye position.</summary>
    public Vector3 Eye => Target + Backward * (float)Distance;

    /// <summary>The viewing direction (eye towards target).</summary>
    public Vector3 Forward => -Backward;

    /// <summary>Screen right in world space (left-handed: up × forward).</summary>
    public Vector3 Right
    {
        get
        {
            var r = Vector3.Cross(Vector3.UnitY, Forward);
            return r.LengthSquared() < 1e-10f ? Vector3.UnitX : Vector3.Normalize(r);
        }
    }

    /// <summary>Screen up in world space.</summary>
    public Vector3 Up => Vector3.Normalize(Vector3.Cross(Forward, Right));

    /// <summary>Near plane distance, scaled with the orbit so small and huge meshes both work.</summary>
    public double Near => Math.Max(0.005, Distance * 0.01);

    /// <summary>Far plane distance.</summary>
    public double Far => Math.Max(100, Distance * 60);

    /// <summary>Half the orthographic view height, matched to the perspective view at the target.</summary>
    public double OrthoHalfHeight => Distance * Math.Tan(FieldOfViewDegrees * Math.PI / 360);

    /// <summary>The left-handed view matrix (world to view).</summary>
    public Matrix3D ViewMatrix
    {
        get
        {
            Vector3 z = Forward, x = Right, y = Up, eye = Eye;
            return new Matrix3D(
                x.X, y.X, z.X, 0,
                x.Y, y.Y, z.Y, 0,
                x.Z, y.Z, z.Z, 0,
                -Vector3.Dot(x, eye), -Vector3.Dot(y, eye), -Vector3.Dot(z, eye), 1);
        }
    }

    /// <summary>The left-handed projection for a viewport of the given aspect (width / height).</summary>
    public Matrix3D ProjectionMatrix(double aspect)
    {
        if (!(aspect > 0) || double.IsInfinity(aspect)) aspect = 1;
        double zn = Near, zf = Far;
        if (IsPerspective)
        {
            double ys = 1 / Math.Tan(FieldOfViewDegrees * Math.PI / 360);
            double xs = ys / aspect;
            return new Matrix3D(
                xs, 0, 0, 0,
                0, ys, 0, 0,
                0, 0, zf / (zf - zn), 1,
                0, 0, -zn * zf / (zf - zn), 0);
        }
        double h = OrthoHalfHeight, w = h * aspect;
        return new Matrix3D(
            1 / w, 0, 0, 0,
            0, 1 / h, 0, 0,
            0, 0, 1 / (zf - zn), 0,
            0, 0, -zn / (zf - zn), 1);
    }

    /// <summary>
    /// Projects an RF-space point into a viewport of <paramref name="size"/> (DIPs). Returns false when
    /// the point is behind the near plane.
    /// </summary>
    public bool Project(Vector3 point, Size size, Matrix3D viewProjection, out Point screen, out double depth)
    {
        var p = viewProjection.Transform(new Point4D(point.X, point.Y, point.Z, 1));
        depth = p.W;
        if (p.Z < 0 || p.W <= 1e-9)
        {
            screen = default;
            return false;
        }
        screen = new Point((p.X / p.W + 1) * 0.5 * size.Width, (1 - p.Y / p.W) * 0.5 * size.Height);
        return true;
    }

    /// <summary>The combined view × projection for a viewport of <paramref name="size"/>.</summary>
    public Matrix3D ViewProjection(Size size) => ViewMatrix * ProjectionMatrix(size.Width / Math.Max(1, size.Height));

    /// <summary>
    /// World units per screen DIP at the target's depth: how far a pan of one pixel moves the target.
    /// </summary>
    public double UnitsPerPixel(Size size) => 2 * OrthoHalfHeight / Math.Max(1, size.Height);

    /// <summary>The world-space point under a screen position, on the plane through the target facing the camera.</summary>
    public Vector3 PointOnFocalPlane(Point screen, Size size)
    {
        double aspect = size.Width / Math.Max(1, size.Height);
        double ndcX = screen.X / Math.Max(1, size.Width) * 2 - 1;
        double ndcY = 1 - screen.Y / Math.Max(1, size.Height) * 2;
        double h = OrthoHalfHeight;
        return Target + Right * (float)(ndcX * h * aspect) + Up * (float)(ndcY * h);
    }

    /// <summary>Puts the camera at a preset direction, keeping target and distance.</summary>
    public void SetView(double yaw, double pitch)
    {
        Yaw = yaw;
        Pitch = pitch;
        NotifyChanged();
    }

    /// <summary>Frames a bounding sphere so it fills most of the view.</summary>
    public void Frame(Vector3 centre, double radius)
    {
        if (!(radius > 1e-4) || double.IsNaN(radius)) radius = 1;
        Target = centre;
        Distance = radius / Math.Sin(FieldOfViewDegrees * Math.PI / 360) * 1.08;
        HasBeenFramed = true;
        NotifyChanged();
    }

    /// <summary>
    /// Frames a bounding box from the current direction so all eight corners are on screen, in both fields of
    /// view (<paramref name="aspect"/> = width / height) and with perspective: vertically within
    /// <paramref name="verticalFill"/> of the half-height (what the viewport's toolbar leaves clear), horizontally
    /// within 90 %. A sphere around a tall, thin mesh only just fits vertically, which left its top under the
    /// toolbar of the short glTF import previews.
    /// </summary>
    public void FrameBox(Vector3 min, Vector3 max, double aspect, double verticalFill = 0.86)
    {
        var centre = (min + max) * 0.5f;
        if (!float.IsFinite(centre.X) || !float.IsFinite(centre.Y) || !float.IsFinite(centre.Z))
        {
            Frame(Vector3.Zero, 1);
            return;
        }
        if (!(aspect > 0) || !double.IsFinite(aspect)) aspect = 1;
        double tanV = Math.Tan(FieldOfViewDegrees * Math.PI / 360) * Math.Clamp(verticalFill, 0.2, 1.0);
        double tanH = Math.Tan(FieldOfViewDegrees * Math.PI / 360) * aspect * 0.90;
        Vector3 right = Right, up = Up, back = Backward;
        double distance = 0.05;
        for (int i = 0; i < 8; i++)
        {
            var corner = new Vector3((i & 1) == 0 ? min.X : max.X, (i & 2) == 0 ? min.Y : max.Y, (i & 4) == 0 ? min.Z : max.Z) - centre;
            double cx = Math.Abs(Vector3.Dot(corner, right)), cy = Math.Abs(Vector3.Dot(corner, up)), cz = Vector3.Dot(corner, back);
            // Perspective: the corner sits (distance - cz) in front of the eye; orthographic: the view is distance * tan tall.
            distance = Math.Max(distance, Math.Max(cz + cy / tanV, cz + cx / tanH));
            distance = Math.Max(distance, Math.Max(cy / tanV, cx / tanH));
        }
        Target = centre;
        Distance = distance;
        HasBeenFramed = true;
        NotifyChanged();
    }
}
