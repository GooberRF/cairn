using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Animation;

/// <summary>
/// Keyframe evaluation as the engine performs it: segment search on integer ticks, cubic Bezier with
/// absolute control points for translation and scale, and eased slerp for rotation (TCB unused).
/// </summary>
public static class VfxKeyframeMath
{
    /// <summary>Scale applied when the engine quantises rotation keys to 16-bit quaternions at load.</summary>
    public const float QuaternionQuantum = 16383f;

    /// <summary>
    /// Finds the segment (n0, n1) and the parameter u for time <paramref name="t"/> over the key times.
    /// Returns false when there are no keys. A single key yields n0 = n1 = 0, u = 0.
    /// </summary>
    public static bool FindSegment(ReadOnlySpan<int> times, float t, out int n0, out int n1, out float u)
    {
        n0 = n1 = 0; u = 0;
        if (times.Length == 0) return false;
        if (times.Length == 1) return true;
        if (t <= times[0]) { n0 = 0; n1 = 1; u = 0; return true; }
        int last = times.Length - 1;
        if (t >= times[last]) { n0 = last - 1; n1 = last; u = 1; return true; }
        n1 = 1;
        while (n1 < last && times[n1] <= t) n1++;
        n0 = n1 - 1;
        int span = times[n1] - times[n0];
        u = span == 0 ? 0 : (t - times[n0]) / span;
        return true;
    }

    /// <summary>Cubic Bezier with absolute control points: p0, out-tangent of p0, in-tangent of p1, p1.</summary>
    public static Vector3 Bezier(Vector3 p0, Vector3 out0, Vector3 in1, Vector3 p1, float u)
    {
        float v = 1 - u;
        return v * v * v * p0 + 3 * v * v * u * out0 + 3 * v * u * u * in1 + u * u * u * p1;
    }

    /// <summary>The 3ds Max ease curve with ease-out <paramref name="a"/> of the first key and ease-in <paramref name="b"/> of the second.</summary>
    public static float Ease(float u, float a, float b)
    {
        if (u == 0 || u == 1) return u;
        float s = a + b;
        if (s == 0) return u;
        if (s > 1) { a /= s; b /= s; }
        float k = 1 / (2 - a - b);
        if (u < a) return k / a * u * u;
        if (u < 1 - b) return k * (2 * u - a);
        float w = 1 - u;
        return 1 - k / b * w * w;
    }

    /// <summary>Shortest-path spherical interpolation, falling back to normalised lerp for nearly equal inputs.</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float u)
    {
        float cos = Quaternion.Dot(a, b);
        if (cos < 0) { cos = -cos; b = Quaternion.Negate(b); }
        float wa, wb;
        if (cos > 0.9999f) { wa = 1 - u; wb = u; }
        else
        {
            float angle = MathF.Acos(cos), sin = MathF.Sin(angle);
            wa = MathF.Sin((1 - u) * angle) / sin;
            wb = MathF.Sin(u * angle) / sin;
        }
        var q = new Quaternion(wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z, wa * a.W + wb * b.W);
        float len = q.Length();
        return len > 0 ? Quaternion.Multiply(q, 1 / len) : Quaternion.Identity;
    }

    /// <summary>Quantises each component to a signed 16-bit value (x16383, truncated) and back, as the engine's loader does.</summary>
    public static Quaternion Quantise(Quaternion q) => new(
        (short)(q.X * QuaternionQuantum) / QuaternionQuantum, (short)(q.Y * QuaternionQuantum) / QuaternionQuantum,
        (short)(q.Z * QuaternionQuantum) / QuaternionQuantum, (short)(q.W * QuaternionQuantum) / QuaternionQuantum);

    /// <summary>Evaluates translation or scale keys at <paramref name="tick"/>; <paramref name="fallback"/> when there are none.</summary>
    public static Vector3 EvaluateVector(ImmutableArray<VfxVectorKey> keys, float tick, Vector3 fallback)
    {
        if (keys.IsDefaultOrEmpty) return fallback;
        if (keys.Length == 1) return keys[0].Value;
        Segment(keys, static k => k.Time, tick, out int n0, out int n1, out float u);
        var k0 = keys[n0]; var k1 = keys[n1];
        return Bezier(k0.Value, k0.OutTangent, k1.InTangent, k1.Value, u);
    }

    /// <summary>Evaluates rotation keys at <paramref name="tick"/>: ease, then slerp; optionally on 16-bit quantised keys.</summary>
    public static Quaternion EvaluateRotation(ImmutableArray<VfxRotationKey> keys, float tick, bool quantise = false)
    {
        if (keys.IsDefaultOrEmpty) return Quaternion.Identity;
        if (keys.Length == 1) return quantise ? Quantise(keys[0].Value) : keys[0].Value;
        Segment(keys, static k => k.Time, tick, out int n0, out int n1, out float u);
        var k0 = keys[n0]; var k1 = keys[n1];
        Quaternion q0 = k0.Value, q1 = k1.Value;
        if (quantise) { q0 = Quantise(q0); q1 = Quantise(q1); }
        return Slerp(q0, q1, Ease(u, k0.EaseOut, k1.EaseIn));
    }

    /// <summary>
    /// Places a keyframed mesh's vertex <paramref name="p"/> in the engine's order: pivot TRS first
    /// (<paramref name="pt"/>, <paramref name="pq"/>, <paramref name="ps"/>), then the sampled key TRS
    /// (<paramref name="kt"/>, <paramref name="kq"/>, <paramref name="ks"/>); each TRS is t + R(s * p).
    /// </summary>
    public static Vector3 ApplyKeyed(Vector3 kt, Quaternion kq, Vector3 ks, Vector3 pt, Quaternion pq, Vector3 ps, Vector3 p) =>
        kt + Vector3.Transform(ks * (pt + Vector3.Transform(ps * p, pq)), kq);

    // Same search as FindSegment over the keys themselves (the static lambdas are cached, so no allocation).
    private static void Segment<T>(ImmutableArray<T> keys, Func<T, int> key, float t, out int n0, out int n1, out float u)
    {
        int last = keys.Length - 1;
        int time(int i) => key(keys[i]);
        if (t <= time(0)) { n0 = 0; n1 = 1; u = 0; return; }
        if (t >= time(last)) { n0 = last - 1; n1 = last; u = 1; return; }
        n1 = 1;
        while (n1 < last && time(n1) <= t) n1++;
        n0 = n1 - 1;
        int span = time(n1) - time(n0);
        u = span == 0 ? 0 : (t - time(n0)) / span;
    }
}
