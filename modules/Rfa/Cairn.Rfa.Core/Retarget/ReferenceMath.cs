using System.Numerics;
using Cairn.Rfa.Formats.Rfa;

namespace Cairn.Rfa.Retarget;

/// <summary>A double-precision vector for the retarget maths (see <see cref="RefMath"/>).</summary>
internal readonly record struct DVec3(double X, double Y, double Z)
{
    public static DVec3 Zero => default;

    public static DVec3 From(Vector3 v) => new(v.X, v.Y, v.Z);

    /// <summary>Rounds each component to the nearest float, as <c>struct.pack('&lt;f')</c> does.</summary>
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}

/// <summary>A double-precision quaternion (x, y, z, w), active convention, Hamilton product.</summary>
internal readonly record struct DQuat(double X, double Y, double Z, double W)
{
    public static DQuat Identity => new(0.0, 0.0, 0.0, 1.0);

    public static DQuat From(Quaternion q) => new(q.X, q.Y, q.Z, q.W);

    public Quaternion ToQuaternion() => new((float)X, (float)Y, (float)Z, (float)W);
}

/// <summary>A double-precision rigid transform (rotation, then translation).</summary>
internal readonly record struct DRigid(DQuat Rot, DVec3 Pos);

/// <summary>A position key held in double precision until the clip is written.</summary>
internal readonly record struct DPosKey(int Time, DVec3 Pos, DVec3 In, DVec3 Out)
{
    public static DPosKey From(RfaPosKey k) => new(k.Time, DVec3.From(k.Position), DVec3.From(k.InControl), DVec3.From(k.OutControl));

    public static DPosKey Constant(int time, DVec3 p) => new(time, p, p, p);

    public RfaPosKey ToKey() => new(Time, Pos.ToVector3(), In.ToVector3(), Out.ToVector3());
}

/// <summary>
/// The quaternion and vector maths of the reference implementation
/// (<c>research/anim_retarget/tools/rfanim.py</c>), ported operation for operation in double
/// precision so the retargeter reproduces the reference's output (the golden clips) to the bit.
/// Every expression keeps Python's evaluation order, and every place the reference calls Python's
/// built-in <c>sum()</c> goes through <see cref="Sum3"/> / <see cref="Sum4"/>, which reproduce
/// CPython 3.12's compensated (Neumaier) float summation (the goldens were made with 3.12). A naive
/// sum can differ in the last bit; on the nine goldens that happens to change no output byte, but
/// mirroring it keeps the port exact by construction rather than by luck.
/// </summary>
internal static class RefMath
{
    /// <summary>The int16 quaternion scale as the reference spells it.</summary>
    public const double QuatScale = 16383.0;

    /// <summary>The int8 ease scale as the reference spells it.</summary>
    public const double EaseScale = 127.0;

    // ── CPython 3.12 sum() ───────────────────────────────────────────────────

    public static double Sum3(double a, double b, double c)
    {
        double f = 0.0 + a, comp = 0.0;
        Add(ref f, ref comp, b);
        Add(ref f, ref comp, c);
        return Finish(f, comp);
    }

    public static double Sum4(double a, double b, double c, double d)
    {
        double f = 0.0 + a, comp = 0.0;
        Add(ref f, ref comp, b);
        Add(ref f, ref comp, c);
        Add(ref f, ref comp, d);
        return Finish(f, comp);
    }

    private static void Add(ref double f, ref double comp, double x)
    {
        double t = f + x;
        if (Math.Abs(f) >= Math.Abs(x)) comp += (f - t) + x;
        else comp += (x - t) + f;
        f = t;
    }

    private static double Finish(double f, double comp) => comp != 0.0 && double.IsFinite(comp) ? f + comp : f;

    // ── quaternions ──────────────────────────────────────────────────────────

    public static DQuat Mul(DQuat a, DQuat b) => new(
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W,
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z);

    public static DQuat Conj(DQuat q) => new(-q.X, -q.Y, -q.Z, q.W);

    public static DQuat Negate(DQuat q) => new(-q.X, -q.Y, -q.Z, -q.W);

    public static double Dot(DQuat a, DQuat b) => Sum4(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);

    public static DQuat Norm(DQuat q)
    {
        double n = Math.Sqrt(Sum4(q.X * q.X, q.Y * q.Y, q.Z * q.Z, q.W * q.W));
        if (n == 0.0) n = 1.0;
        return new DQuat(q.X / n, q.Y / n, q.Z / n, q.W / n);
    }

    public static DVec3 Rotate(DQuat q, DVec3 v)
    {
        var r = Mul(Mul(q, new DQuat(v.X, v.Y, v.Z, 0.0)), Conj(q));
        return new DVec3(r.X, r.Y, r.Z);
    }

    /// <summary>Angle between two rotations in degrees, ignoring sign (<c>qangle_deg</c>).</summary>
    public static double AngleDegrees(DQuat a, DQuat b)
    {
        double d = Math.Min(1.0, Math.Abs(Dot(Norm(a), Norm(b))));
        return 2.0 * Math.Acos(d) * (180.0 / Math.PI);
    }

    public static DQuat Slerp(DQuat a, DQuat b, double t)
    {
        double d = Dot(a, b);
        if (d < 0.0)
        {
            b = Negate(b);
            d = -d;
        }
        if (d > 0.9995)
        {
            return Norm(new DQuat(
                a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.W + (b.W - a.W) * t));
        }
        double th = Math.Acos(d);
        double s = Math.Sin(th);
        double wa = Math.Sin((1.0 - t) * th) / s;
        double wb = Math.Sin(t * th) / s;
        return Norm(new DQuat(wa * a.X + wb * b.X, wa * a.Y + wb * b.Y, wa * a.Z + wb * b.Z, wa * a.W + wb * b.W));
    }

    /// <summary>Shortest-arc rotation taking unit vector <paramref name="a"/> onto unit vector <paramref name="b"/> (<c>qfrom_to</c>).</summary>
    public static DQuat FromTo(DVec3 a, DVec3 b)
    {
        var cx = Cross(a, b);
        double d = Sum3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);
        if (d < -0.999999)
        {
            var axis = Math.Abs(a.X) < 0.9 ? new DVec3(1.0, 0.0, 0.0) : new DVec3(0.0, 1.0, 0.0);
            var c = Cross(a, axis);
            double n = Length(c);
            return new DQuat(c.X / n, c.Y / n, c.Z / n, 0.0);
        }
        return Norm(new DQuat(cx.X, cx.Y, cx.Z, 1.0 + d));
    }

    // ── vectors ──────────────────────────────────────────────────────────────

    public static DVec3 Cross(DVec3 a, DVec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static DVec3 Add(DVec3 a, DVec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static DVec3 Sub(DVec3 a, DVec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static DVec3 Scale(DVec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static double DotV(DVec3 a, DVec3 b) => Sum3(a.X * b.X, a.Y * b.Y, a.Z * b.Z);

    public static double Length(DVec3 a) => Math.Sqrt(Sum3(a.X * a.X, a.Y * a.Y, a.Z * a.Z));

    public static DVec3 Normalize(DVec3 a)
    {
        double n = Length(a);
        if (n == 0.0) n = 1.0;
        return new DVec3(a.X / n, a.Y / n, a.Z / n);
    }

    // ── key encoding (retarget.py: encode_rot / make_rot_keys) ───────────────

    /// <summary>
    /// Quantises active-convention rotations into keys: normalise, conjugate to the file convention,
    /// flip to the previous key's hemisphere, <c>round(c * 16383)</c> half to even (Python's
    /// <c>round</c>).
    /// </summary>
    /// <remarks>
    /// With <paramref name="withinUnit"/> a rounded key longer than 1 is replaced by the floor/ceiling
    /// combination closest in angle that is not: the engine's slerp compares the
    /// RAW dot product of neighbouring keys with 1 - 1e-6, so over-long keys make segments of up to a
    /// degree or two snap instead of interpolating (see <c>RfaRotKey.QuantizeWithinUnit</c>).
    /// </remarks>
    public static List<RfaRotKey> MakeRotKeys(IEnumerable<(int Time, DQuat Rot, sbyte EaseIn, sbyte EaseOut)> samples, bool withinUnit = false)
    {
        var keys = new List<RfaRotKey>();
        DQuat? prev = null;
        foreach (var (time, q, easeIn, easeOut) in samples)
        {
            var f = Conj(Norm(q));
            if (prev is { } p && Sum4(f.X * p.X, f.Y * p.Y, f.Z * p.Z, f.W * p.W) < 0.0) f = Negate(f);
            short x = Quantize(f.X), y = Quantize(f.Y), z = Quantize(f.Z), w = Quantize(f.W);
            if (withinUnit) ShortenToUnit(f, ref x, ref y, ref z, ref w);
            prev = new DQuat(x / QuatScale, y / QuatScale, z / QuatScale, w / QuatScale);
            keys.Add(new RfaRotKey(time, x, y, z, w, easeIn, easeOut, 0));
        }
        return keys;
    }

    private static void ShortenToUnit(DQuat exact, ref short x, ref short y, ref short z, ref short w)
    {
        if ((long)x * x + (long)y * y + (long)z * z + (long)w * w <= 16383L * 16383L) return;
        (x, y, z, w) = RfaRotKey.WithinUnit(exact.X * QuatScale, exact.Y * QuatScale, exact.Z * QuatScale, exact.W * QuatScale);
    }

    private static short Quantize(double c) =>
        (short)Math.Clamp(Math.Round(c * QuatScale, MidpointRounding.ToEven), short.MinValue, short.MaxValue);

    /// <summary>
    /// The shortest decimal a float prints as, read back as a double: <c>-0.15f</c> becomes the double
    /// <c>-0.15</c>. Profile values are typed-in decimals, and the reference holds them as doubles.
    /// </summary>
    public static double Decimal(float value) =>
        float.IsFinite(value)
            ? double.Parse(value.ToString("R", System.Globalization.CultureInfo.InvariantCulture), System.Globalization.CultureInfo.InvariantCulture)
            : value;

    public static DVec3 Decimal(Vector3 v) => new(Decimal(v.X), Decimal(v.Y), Decimal(v.Z));
}

/// <summary>
/// The reference sampler: a double-precision port of <c>rfanim.py: ease / sample_rot / sample_pos /
/// qslerp</c>. It is what produced the golden clips, so the retargeter samples its source (and its
/// own partial output) with it to reproduce them. It differs from the engine's sampler
/// (<see cref="Animation.ClipSampler"/>, which stores each slerp result back to int16 and works in
/// single precision) by under 0.2 degrees; reports use the engine's sampler by default.
/// </summary>
internal static class ReferenceSampler
{
    /// <summary>A key's active rotation: <c>conj(normalize(stored / 16383))</c>.</summary>
    public static DQuat KeyRotation(RfaRotKey k) =>
        RefMath.Conj(RefMath.Norm(new DQuat(k.X / RefMath.QuatScale, k.Y / RefMath.QuatScale, k.Z / RefMath.QuatScale, k.W / RefMath.QuatScale)));

    public static double Ease(double t, double easeOutPrev, double easeInNext)
    {
        if (t == 0.0 || t == 1.0) return t;
        double a = easeOutPrev, b = easeInNext;
        double s = a + b;
        if (s == 0.0) return t;
        if (s > 1.0)
        {
            a /= s;
            b /= s;
        }
        double k = 1.0 / (2.0 - a - b);
        if (t < a) return k / a * t * t;
        if (t < 1.0 - b) return (t + t - a) * k;
        return 1.0 - k / b * (1.0 - t) * (1.0 - t);
    }

    public static DQuat SampleRotation(ReadOnlySpan<RfaRotKey> keys, double time)
    {
        int n = keys.Length;
        if (n == 0) return DQuat.Identity;
        if (n == 1 || time <= keys[0].Time) return KeyRotation(keys[0]);
        if (time >= keys[n - 1].Time) return KeyRotation(keys[n - 1]);
        int i = 1;
        while (i < n && time >= keys[i].Time) i++;
        var k0 = keys[i - 1];
        var k1 = keys[i];
        double t = (time - k0.Time) / (double)(k1.Time - k0.Time);
        t = Ease(t, k0.EaseOut / RefMath.EaseScale, k1.EaseIn / RefMath.EaseScale);
        return RefMath.Slerp(KeyRotation(k0), KeyRotation(k1), t);
    }

    public static DVec3 SamplePosition(ReadOnlySpan<DPosKey> keys, double time)
    {
        int n = keys.Length;
        if (n == 0) return DVec3.Zero;
        if (time <= keys[0].Time) return keys[0].Pos;
        if (time >= keys[n - 1].Time) return keys[n - 1].Pos;
        int i = 1;
        while (i < n && time >= keys[i].Time) i++;
        var k0 = keys[i - 1];
        var k1 = keys[i];
        double t = (time - k0.Time) / (double)(k1.Time - k0.Time);
        double u = 1.0 - t;
        double c0 = u * u * u, c1 = 3 * t * u * u, c2 = 3 * t * t * u, c3 = t * t * t;
        return new DVec3(
            RefMath.Sum4(c0 * k0.Pos.X, c1 * k0.Out.X, c2 * k1.In.X, c3 * k1.Pos.X),
            RefMath.Sum4(c0 * k0.Pos.Y, c1 * k0.Out.Y, c2 * k1.In.Y, c3 * k1.Pos.Y),
            RefMath.Sum4(c0 * k0.Pos.Z, c1 * k0.Out.Z, c2 * k1.In.Z, c3 * k1.Pos.Z));
    }

    /// <summary>Converts a stored position track to double precision.</summary>
    public static DPosKey[] Positions(ReadOnlySpan<RfaPosKey> keys)
    {
        var result = new DPosKey[keys.Length];
        for (int i = 0; i < keys.Length; i++) result[i] = DPosKey.From(keys[i]);
        return result;
    }

    /// <summary>Forward kinematics of double-precision locals (<c>rfanim.py: fk</c>).</summary>
    public static DRigid[] Solve(ReadOnlySpan<int> parents, ReadOnlySpan<int> order, ReadOnlySpan<DRigid> local)
    {
        var world = new DRigid[parents.Length];
        foreach (int i in order)
        {
            int p = parents[i];
            if (p < 0)
            {
                world[i] = local[i];
            }
            else
            {
                var pw = world[p];
                world[i] = new DRigid(RefMath.Mul(pw.Rot, local[i].Rot), RefMath.Add(pw.Pos, RefMath.Rotate(pw.Rot, local[i].Pos)));
            }
        }
        return world;
    }
}
