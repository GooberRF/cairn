using System.Numerics;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

public class MathsTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void NearRotation(Quaternion expected, Quaternion actual, float tolerance = 1e-5f) =>
        Assert.True(MathF.Abs(MathF.Abs(Quat.Dot(Quat.Normalize(expected), Quat.Normalize(actual))) - 1f) <= tolerance,
            $"expected {expected}, got {actual}");

    [Fact]
    public void MulIsTheHamiltonProductAndAppliesTheRightOperandFirst()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitZ, MathF.PI / 2);   // X -> Y
        var b = Quat.FromAxisAngle(Vector3.UnitX, MathF.PI / 2);   // Y -> Z
        // Rotate by b first, then a: X stays X under b, then becomes Y under a.
        Near(Vector3.UnitY, Quat.Rotate(Quat.Mul(a, b), Vector3.UnitX));
        Near(Quat.Rotate(a, Quat.Rotate(b, new Vector3(1, 2, 3))), Quat.Rotate(Quat.Mul(a, b), new Vector3(1, 2, 3)));
        // System.Numerics' Multiply is the same Hamilton product; Concatenate is the reverse.
        var q = Quat.Mul(a, b);
        Assert.True(Quat.Dot(Quaternion.Multiply(a, b), q) > 0.99999f);
        Assert.True(Quat.Dot(Quaternion.Concatenate(b, a), q) > 0.99999f);
        Assert.True(Quat.Dot(Quaternion.Concatenate(a, b), q) < 0.99f);
    }

    [Fact]
    public void RotateIsTheActiveRotation()
    {
        var q = Quat.FromAxisAngle(Vector3.UnitY, MathF.PI / 2);
        Near(new Vector3(0, 0, -1), Quat.Rotate(q, Vector3.UnitX));
        Near(Vector3.Transform(new Vector3(0.3f, -2, 5), q), Quat.Rotate(q, new Vector3(0.3f, -2, 5)));
    }

    [Fact]
    public void ConjugateInvertsAUnitQuaternion()
    {
        var q = Quat.Normalize(new Quaternion(0.1f, 0.7f, -0.2f, 0.6f));
        NearRotation(Quaternion.Identity, Quat.Mul(q, Quat.Conj(q)));
        Near(new Vector3(1, 2, 3), Quat.Rotate(Quat.Conj(q), Quat.Rotate(q, new Vector3(1, 2, 3))));
    }

    [Fact]
    public void NormalizeTurnsZeroIntoTheIdentity()
    {
        Assert.Equal(Quaternion.Identity, Quat.Normalize(default));
        Assert.Equal(Quaternion.Identity, Quat.Normalize(new Quaternion(float.NaN, 0, 0, 1)));
        Assert.Equal(1f, Quat.Normalize(new Quaternion(1, 2, 3, 4)).Length(), 5);
    }

    [Fact]
    public void SlerpInterpolatesAlongTheShortArc()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitX, 0.2f);
        var b = Quat.FromAxisAngle(Vector3.UnitX, 1.2f);
        NearRotation(a, Quat.Slerp(a, b, 0f));
        NearRotation(b, Quat.Slerp(a, b, 1f));
        NearRotation(Quat.FromAxisAngle(Vector3.UnitX, 0.7f), Quat.Slerp(a, b, 0.5f));
        // -b is the same rotation; the result must not take the long way round.
        NearRotation(Quat.FromAxisAngle(Vector3.UnitX, 0.7f), Quat.Slerp(a, Quat.Negate(b), 0.5f));
        // Nearly parallel inputs fall back to a normalised lerp without NaN.
        var c = Quat.FromAxisAngle(Vector3.UnitX, 0.2001f);
        Assert.True(float.IsFinite(Quat.Slerp(a, c, 0.5f).W));
    }

    [Fact]
    public void FromToRotatesOneDirectionOntoAnother()
    {
        var a = Vector3.Normalize(new Vector3(1, 2, 3));
        var b = Vector3.Normalize(new Vector3(-2, 0.5f, 1));
        Near(b, Quat.Rotate(Quat.FromTo(a, b), a));
        Near(-a, Quat.Rotate(Quat.FromTo(a, -a), a));
        Near(Vector3.UnitX * -1, Quat.Rotate(Quat.FromTo(Vector3.UnitX, -Vector3.UnitX), Vector3.UnitX));
        NearRotation(Quaternion.Identity, Quat.FromTo(a, a));
    }

    [Theory]
    [InlineData(0f, 0f, 0f)]
    [InlineData(30f, 0f, 0f)]
    [InlineData(0f, 45f, 0f)]
    [InlineData(0f, 0f, -60f)]
    [InlineData(10f, -120f, 35f)]
    [InlineData(-80f, 170f, -175f)]
    public void EulerRoundTrips(float pitch, float yaw, float roll)
    {
        var q = Quat.FromEulerDegrees(new Vector3(pitch, yaw, roll));
        var e = Quat.ToEulerDegrees(q);
        NearRotation(q, Quat.FromEulerDegrees(e), 1e-4f);
        Assert.Equal(pitch, e.X, 2);
        Assert.Equal(yaw, e.Y, 2);
        Assert.Equal(roll, e.Z, 2);
    }

    [Fact]
    public void EulerOrderAppliesRollThenPitchThenYaw()
    {
        var q = Quat.FromEulerDegrees(new Vector3(20, 30, 40));
        var expected = Quat.Mul(Quat.FromAxisAngle(Vector3.UnitY, 30 * MathF.PI / 180),
            Quat.Mul(Quat.FromAxisAngle(Vector3.UnitX, 20 * MathF.PI / 180), Quat.FromAxisAngle(Vector3.UnitZ, 40 * MathF.PI / 180)));
        NearRotation(expected, q);
        // At the pole the decomposition still reproduces the rotation.
        var pole = Quat.FromEulerDegrees(new Vector3(90, 25, 10));
        NearRotation(pole, Quat.FromEulerDegrees(Quat.ToEulerDegrees(pole)), 1e-3f);
    }

    [Fact]
    public void RigidComposesInverseAndMatrixAgree()
    {
        var parent = new Rigid(Quat.FromAxisAngle(Vector3.UnitY, 0.8f), new Vector3(1, 2, 3));
        var child = new Rigid(Quat.FromAxisAngle(Vector3.UnitX, -0.4f), new Vector3(0, 1, 0));
        var world = parent.Compose(child);
        var p = new Vector3(0.5f, -0.25f, 2f);
        Near(parent.TransformPoint(child.TransformPoint(p)), world.TransformPoint(p));
        Near(p, world.Inverse().TransformPoint(world.TransformPoint(p)));
        Near(world.TransformPoint(p), Vector3.Transform(p, world.ToMatrix()));
        Near(world.TransformVector(p), Vector3.TransformNormal(p, world.ToMatrix()));
    }

    [Fact]
    public void AngleAndAlignIgnoreSign()
    {
        var a = Quat.FromAxisAngle(Vector3.UnitZ, 0.5f);
        Assert.Equal(0f, Quat.AngleDegrees(a, Quat.Negate(a)), 3);
        Assert.Equal(90f, Quat.AngleDegrees(Quaternion.Identity, Quat.FromAxisAngle(Vector3.UnitY, MathF.PI / 2)), 3);
        Assert.True(Quat.Dot(Quat.Align(Quat.Negate(a), a), a) > 0);
    }
}
