using System.Numerics;

namespace Cairn.Formats.Maths;

/// <summary>
/// Quaternion helpers on <see cref="Quaternion"/>, in the one convention every part of RFA
/// Workbench uses (research/anim_retarget/math.md):
/// <list type="bullet">
/// <item>components (x, y, z, w), Hamilton product;</item>
/// <item>ACTIVE rotation, <c>Rotate(q, v) = q v q*</c>;</item>
/// <item>composition <c>Mul(a, b)</c> applies <c>b</c> first, then <c>a</c>.</item>
/// </list>
/// Always compose with <see cref="Mul"/>. <c>Quaternion.Concatenate(a, b)</c> is <c>b * a</c> and
/// mixing the two is exactly the bug that makes poses look plausible on symmetric bones and explode
/// on the hands. Both file formats store the conjugate of the active rotation; that conversion
/// happens at the <c>Animation</c> boundary, never here.
/// </summary>
public static class Quat
{
    /// <summary>The identity rotation.</summary>
    public static Quaternion Identity => Quaternion.Identity;

    /// <summary>The Hamilton product <c>a * b</c>: rotate by <paramref name="b"/>, then by <paramref name="a"/>.</summary>
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    /// <summary>The conjugate, which is the inverse of a unit quaternion.</summary>
    public static Quaternion Conj(Quaternion q) => new(-q.X, -q.Y, -q.Z, q.W);

    /// <summary>Four-component dot product.</summary>
    public static float Dot(Quaternion a, Quaternion b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z + a.W * b.W;

    /// <summary>Unit length; a zero (or non-finite) quaternion becomes the identity rather than NaN.</summary>
    public static Quaternion Normalize(Quaternion q)
    {
        float n = MathF.Sqrt(Dot(q, q));
        if (!(n > 0f) || !float.IsFinite(n)) return Quaternion.Identity;
        return new Quaternion(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    /// <summary>The negated quaternion (the same rotation).</summary>
    public static Quaternion Negate(Quaternion q) => new(-q.X, -q.Y, -q.Z, -q.W);

    /// <summary>
    /// <paramref name="q"/> or its negation, whichever is in the same hemisphere as
    /// <paramref name="reference"/>, so consecutive keys stay sign-continuous.
    /// </summary>
    public static Quaternion Align(Quaternion q, Quaternion reference) => Dot(q, reference) < 0f ? Negate(q) : q;

    /// <summary>Active rotation of a vector: <c>q v q*</c>.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        // Expanded q v q* for a unit q: v + 2w(u x v) + 2 u x (u x v), u = q.xyz.
        var u = new Vector3(q.X, q.Y, q.Z);
        var t = 2f * Vector3.Cross(u, v);
        return v + q.W * t + Vector3.Cross(u, t);
    }

    /// <summary>
    /// Spherical interpolation the short way round, matching <c>rfanim.py: qslerp</c>: the second
    /// quaternion is negated when the dot product is negative, and nearly parallel inputs fall back to
    /// a normalised lerp. The result is unit length.
    /// </summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float t)
    {
        float d = Dot(a, b);
        if (d < 0f)
        {
            b = Negate(b);
            d = -d;
        }
        if (d > 0.9995f)
        {
            return Normalize(new Quaternion(
                a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t));
        }
        float th = MathF.Acos(d);
        float s = MathF.Sin(th);
        float wa = MathF.Sin((1f - t) * th) / s;
        float wb = MathF.Sin(t * th) / s;
        return Normalize(new Quaternion(
            wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z, wa * a.W + wb * b.W));
    }

    /// <summary>
    /// The shortest-arc rotation taking direction <paramref name="from"/> onto <paramref name="to"/>
    /// (both are normalised first). Opposite directions turn 180 degrees about a perpendicular axis.
    /// </summary>
    public static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        var a = SafeNormalize(from);
        var b = SafeNormalize(to);
        float d = Vector3.Dot(a, b);
        if (d < -0.999999f)
        {
            var axis = MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var c = Vector3.Normalize(Vector3.Cross(a, axis));
            return new Quaternion(c.X, c.Y, c.Z, 0f);
        }
        var cross = Vector3.Cross(a, b);
        return Normalize(new Quaternion(cross.X, cross.Y, cross.Z, 1f + d));
    }

    /// <summary>Active rotation of <paramref name="radians"/> about <paramref name="axis"/> (right-hand rule in the frame's own handedness).</summary>
    public static Quaternion FromAxisAngle(Vector3 axis, float radians)
    {
        var n = SafeNormalize(axis);
        if (n == Vector3.Zero) return Quaternion.Identity;
        float s = MathF.Sin(radians * 0.5f);
        return new Quaternion(n.X * s, n.Y * s, n.Z * s, MathF.Cos(radians * 0.5f));
    }

    /// <summary>
    /// The angle between two rotations in degrees, 0..180, ignoring sign. Computed in double precision as
    /// <c>2 atan2(|v|, |w|)</c> of the relative rotation, which stays accurate for tiny angles (an
    /// <c>acos</c> of a float dot product cannot resolve less than about 0.03 degrees).
    /// </summary>
    public static float AngleDegrees(Quaternion a, Quaternion b)
    {
        a = Normalize(a);
        b = Normalize(b);
        // r = conj(b) * a, in double.
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W, bx = -b.X, by = -b.Y, bz = -b.Z, bw = b.W;
        double x = bw * ax + bx * aw + by * az - bz * ay;
        double y = bw * ay - bx * az + by * aw + bz * ax;
        double z = bw * az + bx * ay - by * ax + bz * aw;
        double w = bw * aw - bx * ax - by * ay - bz * az;
        double v = Math.Sqrt(x * x + y * y + z * z);
        return (float)(2.0 * Math.Atan2(v, Math.Abs(w)) * (180.0 / Math.PI));
    }

    /// <summary>
    /// Euler angles in degrees to a rotation. Convention: <c>q = Ry(yaw) * Rx(pitch) * Rz(roll)</c>
    /// with <c>euler = (pitch, yaw, roll)</c> about X, Y and Z, i.e. roll is applied first and yaw
    /// last (Y is up in RF's frame). This is the convention the inspector shows.
    /// </summary>
    public static Quaternion FromEulerDegrees(Vector3 euler)
    {
        const float toRad = MathF.PI / 180f;
        var qx = FromAxisAngle(Vector3.UnitX, euler.X * toRad);
        var qy = FromAxisAngle(Vector3.UnitY, euler.Y * toRad);
        var qz = FromAxisAngle(Vector3.UnitZ, euler.Z * toRad);
        return Mul(qy, Mul(qx, qz));
    }

    /// <summary>
    /// The inverse of <see cref="FromEulerDegrees"/>: (pitch, yaw, roll) in degrees, pitch in
    /// [-90, 90]. At the poles roll is folded into yaw.
    /// </summary>
    public static Vector3 ToEulerDegrees(Quaternion q)
    {
        q = Normalize(q);
        float x = q.X, y = q.Y, z = q.Z, w = q.W;
        // Rotation matrix entries (active, column-vector) for R = Ry * Rx * Rz.
        float m12 = 2f * (y * z - w * x);       // = -sin(pitch)
        float sinPitch = Math.Clamp(-m12, -1f, 1f);
        float pitch = MathF.Asin(sinPitch);
        float yaw, roll;
        if (MathF.Abs(sinPitch) < 0.99999f)
        {
            float m02 = 2f * (x * z + w * y);   // = cos(pitch) sin(yaw)
            float m22 = 1f - 2f * (x * x + y * y); // = cos(pitch) cos(yaw)
            float m10 = 2f * (x * y + w * z);   // = cos(pitch) sin(roll)
            float m11 = 1f - 2f * (x * x + z * z); // = cos(pitch) cos(roll)
            yaw = MathF.Atan2(m02, m22);
            roll = MathF.Atan2(m10, m11);
        }
        else
        {
            float m00 = 1f - 2f * (y * y + z * z);
            float m20 = 2f * (x * z - w * y);
            yaw = MathF.Atan2(-m20, m00);
            roll = 0f;
        }
        const float toDeg = 180f / MathF.PI;
        return new Vector3(pitch * toDeg, yaw * toDeg, roll * toDeg);
    }

    private static Vector3 SafeNormalize(Vector3 v)
    {
        float len = v.Length();
        return len > 0f && float.IsFinite(len) ? v / len : Vector3.Zero;
    }
}
