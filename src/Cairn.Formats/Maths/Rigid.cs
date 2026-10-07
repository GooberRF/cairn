using System.Numerics;

namespace Cairn.Formats.Maths;

/// <summary>
/// A rotation followed by a translation, in the active convention of <see cref="Quat"/>:
/// <c>Transform(p) = Rotate(Rotation, p) + Position</c>. Used for bone locals (position in the
/// parent's frame), bone worlds (model space) and the inverse bind.
/// </summary>
/// <param name="Rotation">Active rotation; kept unit length by the code that builds it.</param>
/// <param name="Position">Translation applied after the rotation.</param>
public readonly record struct Rigid(Quaternion Rotation, Vector3 Position)
{
    /// <summary>No rotation, no translation.</summary>
    public static Rigid Identity => new(Quaternion.Identity, Vector3.Zero);

    /// <summary>
    /// <c>this * child</c>: the child's transform expressed in this frame's parent space. With
    /// <c>this</c> a parent's world and <paramref name="child"/> a local, this is forward kinematics:
    /// <c>W_rot = P_rot * L_rot</c>, <c>W_pos = P_pos + Rotate(P_rot, L_pos)</c>.
    /// </summary>
    public Rigid Compose(Rigid child) =>
        new(Quat.Mul(Rotation, child.Rotation), Position + Quat.Rotate(Rotation, child.Position));

    /// <summary>The inverse transform (assumes a unit rotation).</summary>
    public Rigid Inverse()
    {
        var inv = Quat.Conj(Rotation);
        return new Rigid(inv, -Quat.Rotate(inv, Position));
    }

    /// <summary>Transforms a point.</summary>
    public Vector3 TransformPoint(Vector3 point) => Quat.Rotate(Rotation, point) + Position;

    /// <summary>Rotates a direction (no translation).</summary>
    public Vector3 TransformVector(Vector3 vector) => Quat.Rotate(Rotation, vector);

    /// <summary>
    /// The same transform as a <see cref="Matrix4x4"/> for <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>
    /// (System.Numerics' row-vector layout): <c>Vector3.Transform(p, ToMatrix()) == TransformPoint(p)</c>.
    /// </summary>
    public Matrix4x4 ToMatrix()
    {
        var m = Matrix4x4.CreateFromQuaternion(Rotation);
        m.Translation = Position;
        return m;
    }
}
