using System.Numerics;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Animation;

/// <summary>
/// Samples clip tracks the way RF.exe does, returning ACTIVE-convention locals. This is the boundary
/// where the file convention (conjugated, int16-scaled) becomes the active one. Every rule below was
/// read from RF.exe's disassembly (DESIGN.md section 9, phase 2):
/// <list type="bullet">
/// <item>Rotation (<c>Skeleton::find_animation_rotation</c>, 0x00539ED0): 0 keys give the identity;
/// otherwise the segment holding the time is found by a linear scan (clamped to the first/last
/// segment), its linear parameter is eased (<see cref="Ease"/>, 0x0053A040: earlier key's ease-out and
/// later key's ease-in over 127) and the int16 quaternions are slerped by <see cref="SlerpShort"/>
/// (0x0051A000), whose result is stored back to int16.</item>
/// <item>Position (<c>Skeleton::find_animation_translation</c>, 0x0053A130): 0 keys give the origin
/// (the bone collapses onto its parent); clamped at both ends; between keys a cubic Bezier through
/// ABSOLUTE control points <c>(k0.pos, k0.out, k1.in, k1.pos)</c> with a linear parameter (no ease).</item>
/// </list>
/// The engine unpacks the int16 result without normalising it (x 1/16383); <see cref="KeyRotation"/>
/// normalises, which only removes the tiny scale (|q| within 1e-3 of 1) the engine's matrix carries.
/// It agrees with <c>rfanim.py: sample_rot / sample_pos</c> except where the reference lerps and the
/// engine does not (keys closer than about 0.16 degrees, see <see cref="NearParallel"/>).
/// </summary>
public static class ClipSampler
{
    /// <summary>A key's rotation in the active convention: <c>conj(normalize(stored / 16383))</c>.</summary>
    public static Quaternion KeyRotation(RfaRotKey key) => Quat.Conj(Quat.Normalize(key.FileQuaternion));

    /// <summary>
    /// The engine's ease curve (<c>ca_ease</c>, 0x0053A040, confirmed from the disassembly).
    /// <paramref name="u"/> is the linear segment parameter,
    /// <paramref name="a"/> the earlier key's ease-out and <paramref name="b"/> the later key's
    /// ease-in, both already divided by 127. Endpoints and a zero ease pass <paramref name="u"/>
    /// through; eases summing past 1 are scaled down to sum to 1.
    /// </summary>
    public static float Ease(float u, float a, float b)
    {
        if (u == 0f || u == 1f) return u;
        float s = a + b;
        if (s == 0f) return u;
        if (s > 1f)
        {
            a /= s;
            b /= s;
        }
        float k = 1f / (2f - a - b);
        if (u < a) return k / a * u * u;
        if (u < 1f - b) return (u + u - a) * k;
        return 1f - k / b * (1f - u) * (1f - u);
    }

    /// <summary>
    /// The engine's int16 quaternion slerp (<c>slerp_short</c>, 0x0051A000), read from RF.exe, on
    /// stored FILE-convention components:
    /// <list type="number">
    /// <item><paramref name="t"/> is wrapped into [0, 1] (adding or subtracting 1).</item>
    /// <item>The second key is negated when <c>|q0 + q1|^2 &lt;= |q0 - q1|^2</c> (the short arc; the
    /// integer dot products are exact).</item>
    /// <item><c>d = dot(q0, q1) / 16383^2</c>. When <c>1 - d &lt;= 1e-6</c> (keys less than about 0.16
    /// degrees apart) the weights are 0 and 1: the result is the SECOND key, whatever <c>t</c> is.
    /// Otherwise the weights are <c>sin((1 - t) w) / sin w</c> and <c>sin(t w) / sin w</c>,
    /// <c>w = acos d</c>. (A <c>1 + d &lt;= 1e-6</c> branch exists but cannot be reached after the flip.)</item>
    /// <item>Each component is <c>trunc(c0 * s0 + c1 * s1)</c> on the raw int16 values, stored as int16;
    /// a resulting w of exactly 0 becomes 1.</item>
    /// </list>
    /// The arithmetic here is double precision with the weights divided (not multiplied by a
    /// reciprocal), so <c>t = 0</c> and <c>t = 1</c> reproduce the keys exactly; the engine's x87 code
    /// can differ by one int16 unit in rare rounding cases.
    /// </summary>
    /// <returns>The interpolated components, still scaled by 16383 and in the file convention.</returns>
    public static (short X, short Y, short Z, short W) SlerpShort(RfaRotKey q0, RfaRotKey q1, float t)
    {
        if (t < 0f)
        {
            do t += 1f; while (t < 0f);
        }
        if (t > 1f)
        {
            do t -= 1f; while (t > 1f);
        }

        int ax = q0.X, ay = q0.Y, az = q0.Z, aw = q0.W;
        int bx = q1.X, by = q1.Y, bz = q1.Z, bw = q1.W;
        int diff = Sq(ax - bx) + Sq(ay - by) + Sq(az - bz) + Sq(aw - bw);
        int sum = Sq(ax + bx) + Sq(ay + by) + Sq(az + bz) + Sq(aw + bw);
        if (sum <= diff)
        {
            bx = -bx;
            by = -by;
            bz = -bz;
            bw = -bw;
        }

        // The engine keeps the dot product as a float (0x00589E10 = 1/16383^2).
        float dot = (float)((ax * bx + ay * by + az * bz + aw * bw) * (double)DotScale);
        short x, y, z, w;
        if (dot + 1f <= NearParallel)
        {
            // Unreachable after the flip above; ported for completeness (0x0051A23E). The second
            // quaternion's perpendicular is used, as in the engine.
            double s0 = Math.Sin((1.0 - t) * (Math.PI / 2)), s1 = Math.Sin(t * (Math.PI / 2));
            x = Store(ax * s0 - by * s1);
            y = Store(ay * s0 + bx * s1);
            z = Store(az * s0 - bw * s1);
            w = Store(aw * s0 + bz * s1);
        }
        else
        {
            double s0, s1;
            if (1f - dot <= NearParallel)
            {
                s0 = 0.0;
                s1 = 1.0;
            }
            else
            {
                double omega = Math.Acos(dot);
                double sin = Math.Sin(omega);
                s0 = Math.Sin((1.0 - t) * omega) / sin;
                s1 = Math.Sin(t * omega) / sin;
            }
            x = Store(ax * s0 + bx * s1);
            y = Store(ay * s0 + by * s1);
            z = Store(az * s0 + bz * s1);
            w = Store(aw * s0 + bw * s1);
        }
        if (w == 0) w = 1;
        return (x, y, z, w);

        static int Sq(int v) => v * v;
        // __ftol truncates toward zero; the low 16 bits are stored.
        static short Store(double c) => unchecked((short)(int)Math.Truncate(c));
    }

    /// <summary>
    /// <c>1 - dot</c> at or below this (0x00589DF4) makes the engine's slerps return the second
    /// quaternion: keys closer than about 0.16 degrees do not interpolate.
    /// </summary>
    public const float NearParallel = 1e-6f;

    /// <summary>The engine's scale for the integer dot product of two stored quaternions (1/16383^2).</summary>
    private const float DotScale = 3.725745e-9f;

    /// <summary>
    /// A bone's active-convention local rotation at <paramref name="time"/> ticks, as
    /// <c>Skeleton::find_animation_rotation</c> (0x00539ED0) computes it: with two or more keys the
    /// result always goes through <see cref="SlerpShort"/> — at or before the first key between keys 0
    /// and 1 with t = 0, at or after the last between the last two with t = 1 — so a clamped sample
    /// in a near-parallel end segment is the later key, exactly as in the game. With one key the
    /// engine returns it after the last time but reads past the track before it; the key is returned
    /// for both.
    /// </summary>
    public static Quaternion SampleRotation(ReadOnlySpan<RfaRotKey> keys, float time)
    {
        int n = keys.Length;
        if (n == 0) return Quaternion.Identity;
        if (n == 1) return KeyRotation(keys[0]);
        RfaRotKey k0, k1;
        float u;
        if (time <= keys[0].Time)
        {
            k0 = keys[0];
            k1 = keys[1];
            u = 0f;
        }
        else if (time >= keys[n - 1].Time)
        {
            k0 = keys[n - 2];
            k1 = keys[n - 1];
            u = 1f;
        }
        else
        {
            int i = Segment(keys, time);
            k0 = keys[i - 1];
            k1 = keys[i];
            u = Parameter(k0.Time, k1.Time, time);
            u = Ease(u, k0.EaseOut / RfaClip.EaseScale, k1.EaseIn / RfaClip.EaseScale);
        }
        var (x, y, z, w) = SlerpShort(k0, k1, u);
        return KeyRotation(new RfaRotKey(0, x, y, z, w));
    }

    /// <summary>A bone's local position (parent frame) at <paramref name="time"/> ticks.</summary>
    public static Vector3 SamplePosition(ReadOnlySpan<RfaPosKey> keys, float time)
    {
        int n = keys.Length;
        if (n == 0) return Vector3.Zero;
        if (time <= keys[0].Time) return keys[0].Position;
        if (time >= keys[n - 1].Time) return keys[n - 1].Position;
        int i = Segment(keys, time);
        var k0 = keys[i - 1];
        var k1 = keys[i];
        float t = Parameter(k0.Time, k1.Time, time);
        float u = 1f - t;
        float c0 = u * u * u, c1 = 3f * t * u * u, c2 = 3f * t * t * u, c3 = t * t * t;
        return c0 * k0.Position + c1 * k0.OutControl + c2 * k1.InControl + c3 * k1.Position;
    }

    /// <summary>A bone's active-convention local transform at <paramref name="time"/> ticks.</summary>
    public static Rigid SampleBone(RfaBoneTrack track, float time)
    {
        ArgumentNullException.ThrowIfNull(track);
        return new Rigid(SampleRotation(track.RotationKeys.AsSpan(), time), SamplePosition(track.PositionKeys.AsSpan(), time));
    }

    /// <summary>
    /// Samples every bone of <paramref name="clip"/> into <paramref name="locals"/>. Slots beyond the
    /// clip's bone count are set to <paramref name="missing"/> (the identity when null): the engine
    /// would read past the clip's bone table there, so callers flag the count mismatch instead.
    /// Allocation-free.
    /// </summary>
    public static void SampleLocals(RfaClip clip, float time, Span<Rigid> locals, ReadOnlySpan<Rigid> missing = default)
    {
        ArgumentNullException.ThrowIfNull(clip);
        int n = Math.Min(clip.BoneCount, locals.Length);
        for (int i = 0; i < n; i++) locals[i] = SampleBone(clip.Bones[i], time);
        for (int i = n; i < locals.Length; i++) locals[i] = i < missing.Length ? missing[i] : Rigid.Identity;
    }

    /// <summary>The index of the later key of the segment holding <paramref name="time"/> (the engine's linear scan).</summary>
    private static int Segment(ReadOnlySpan<RfaRotKey> keys, float time)
    {
        int i = 1;
        while (i < keys.Length && time >= keys[i].Time) i++;
        return i;
    }

    private static int Segment(ReadOnlySpan<RfaPosKey> keys, float time)
    {
        int i = 1;
        while (i < keys.Length && time >= keys[i].Time) i++;
        return i;
    }

    // Non-increasing key times would divide by zero or go backwards; such a segment holds its start.
    private static float Parameter(int t0, int t1, float time) => t1 > t0 ? (time - t0) / (t1 - t0) : 0f;
}
