using System.Numerics;

namespace Cairn.Rfa.Retarget;

/// <summary>The joints a two-bone IK solve puts the limb on.</summary>
/// <param name="Elbow">Where the middle joint (elbow, knee) goes.</param>
/// <param name="End">Where the end joint (wrist, ankle) goes: the target, or the nearest point the limb reaches on the shoulder-target line.</param>
/// <param name="Clamped">True when the target was out of reach (too far, or too close for the two lengths) and the end stops short of it.</param>
public readonly record struct TwoBoneIkSolution(Vector3 Elbow, Vector3 End, bool Clamped);

/// <summary>
/// Two-bone IK as the retarget reference does it (<c>retarget.py: solve_two_bone</c>), generalised:
/// <list type="number">
/// <item><c>d = clamp(|T - S|, |a - b| + 1e-4, a + b - 1e-4)</c>, <c>u</c> = the unit S->T direction;</item>
/// <item>bend plane: the source elbow's offset from the S->T line; as that offset drops below
/// <c>poleFade</c> metres the pole bias (also taken perpendicular to S->T) fades in, so a nearly
/// straight source limb on a longer target limb bends the way the bias says instead of folding
/// sideways; if both vanish the bend falls back to +Z (forward);</item>
/// <item><c>E = S + a (cos A u + sin A v)</c> with <c>cos A = (a^2 + d^2 - b^2) / (2 a d)</c>.</item>
/// </list>
/// </summary>
public static class TwoBoneIk
{
    /// <summary>Solves one limb.</summary>
    /// <param name="shoulder">The upper joint S (stays fixed).</param>
    /// <param name="sourceElbow">The source's middle joint: the limb bends toward it.</param>
    /// <param name="target">The wanted end joint T.</param>
    /// <param name="upperLength">a: shoulder to elbow.</param>
    /// <param name="lowerLength">b: elbow to end.</param>
    /// <param name="poleBias">Bend direction bias (model-space metres), read as the decimal it prints as.</param>
    /// <param name="poleFade">Metres; the bias is fully in when the source bend offset is 0 and gone at this offset.</param>
    public static TwoBoneIkSolution Solve(
        Vector3 shoulder, Vector3 sourceElbow, Vector3 target, float upperLength, float lowerLength, Vector3 poleBias, float poleFade = 0.10f)
    {
        var S = DVec3.From(shoulder);
        var T = DVec3.From(target);
        double reach = RefMath.Length(RefMath.Sub(T, S));
        double a = upperLength, b = lowerLength;
        var (elbow, end) = Solve(S, DVec3.From(sourceElbow), T, a, b, RefMath.Decimal(poleBias), RefMath.Decimal(poleFade));
        bool clamped = reach > a + b - 1e-4 || reach < Math.Abs(a - b) + 1e-4;
        return new TwoBoneIkSolution(elbow.ToVector3(), end.ToVector3(), clamped);
    }

    /// <summary>The double-precision solve, operation for operation as the reference.</summary>
    internal static (DVec3 Elbow, DVec3 End) Solve(DVec3 S, DVec3 sourceElbow, DVec3 T, double a, double b, DVec3 bias, double poleFade)
    {
        var st = RefMath.Sub(T, S);
        double d = RefMath.Length(st);
        d = Math.Min(Math.Max(d, Math.Abs(a - b) + 1e-4), a + b - 1e-4);
        var u = RefMath.Normalize(st);

        DVec3 Perp(DVec3 w) => RefMath.Sub(w, RefMath.Scale(u, RefMath.DotV(w, u)));

        var pole = Perp(RefMath.Sub(sourceElbow, S));
        double fade = poleFade > 0.0 ? Math.Max(0.0, 1.0 - RefMath.Length(pole) / poleFade) : 0.0;
        pole = RefMath.Add(pole, RefMath.Scale(Perp(bias), fade));
        if (RefMath.Length(pole) < 1e-6) pole = Perp(new DVec3(0.0, 0.0, 1.0));
        var v = RefMath.Normalize(pole);
        double cosA = (a * a + d * d - b * b) / (2 * a * d);
        double sinA = Math.Sqrt(Math.Max(0.0, 1.0 - cosA * cosA));
        var elbow = new DVec3(
            S.X + a * (cosA * u.X + sinA * v.X),
            S.Y + a * (cosA * u.Y + sinA * v.Y),
            S.Z + a * (cosA * u.Z + sinA * v.Z));
        var end = new DVec3(S.X + d * u.X, S.Y + d * u.Y, S.Z + d * u.Z);
        return (elbow, end);
    }
}
