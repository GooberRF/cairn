using System.Numerics;

namespace Cairn.Formats.XForge;

/// <summary>
/// The space of X-Forge scenes and the one place their conventions are applied: how a stored rotation rotates
/// (<see cref="Rotation"/>), how a transform becomes a matrix (<see cref="LocalMatrix(XffTransform)"/>), and the transform from XFF
/// space to Red Faction's (<see cref="ToRfPosition"/> and the rest), which the meshes and levels conversions apply once.
/// </summary>
/// <remarks>
/// <para>
/// XFF space is left-handed with Y up, +Z forward and +X right: the same axes as Red Faction. A stored quaternion
/// (x, y, z, w) is an active rotation applied to column vectors, which is what <see cref="System.Numerics.Quaternion"/>
/// means too (<see cref="Vector3.Transform(Vector3, Quaternion)"/>), so it is used as stored. A node's world transform
/// is its parent's composed with its own: with System.Numerics' row vectors, <c>local * parentWorld</c>.
/// </para>
/// <para>
/// The two spaces differ in three ways. The front face is the reverse of Red Faction's (the corners A, B, C of a
/// triangle become A, C, B, and the stored normals are negated); UVs are the same (V = 0 is the image's top row). The
/// scale is <see cref="RfScale"/>, measured from the player's collision radius and the height of people against Red
/// Faction's: likely, not proven.
/// </para>
/// </remarks>
public static class XffSpace
{
    /// <summary>
    /// Red Faction metres per XFF unit (likely: the player's collision radius 0.385 against Red Faction's 0.9, and
    /// people 0.76 tall against about 1.8 m).
    /// </summary>
    public const float RfScale = 2.35f;

    /// <summary>
    /// The smallest and largest scale (Red Faction metres per XFF unit) Cairn takes: the one range of the Meshes and
    /// Levels modules' Convert windows, the mesh conversion and the level reader. Anything else is refused, never
    /// replaced by the default.
    /// </summary>
    public const float MinScale = 0.01f, MaxScale = 100f;

    /// <summary>True when <paramref name="scale"/> is a finite number from <see cref="MinScale"/> to <see cref="MaxScale"/>.</summary>
    public static bool IsValidScale(float scale) => float.IsFinite(scale) && scale >= MinScale && scale <= MaxScale;

    /// <summary>The scale range as text for messages: "0.01 to 100".</summary>
    public static string ScaleRangeText => $"{MinScale.ToString(System.Globalization.CultureInfo.InvariantCulture)} to {MaxScale.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>True: Red Faction's front face is the other side, so a conversion swaps two corners of every triangle (<see cref="ToRfTriangle"/>).</summary>
    public const bool ConversionFlipsWinding = true;

    /// <summary>The rotation a stored quaternion stands for (as System.Numerics rotates: <c>q v q*</c>). The only place the convention lives.</summary>
    public static Quaternion Rotation(Quaternion stored) => stored;

    /// <summary>A local transform as a matrix (row vectors: scale, then rotation, then translation).</summary>
    public static Matrix4x4 LocalMatrix(Vector3 position, Quaternion rotation, Vector3 scale) =>
        Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(SafeRotation(rotation)) * Matrix4x4.CreateTranslation(position);

    /// <summary>A local transform as a matrix (row vectors: scale, then rotation, then translation).</summary>
    public static Matrix4x4 LocalMatrix(XffTransform transform) => LocalMatrix(transform.Position, transform.Rotation, transform.Scale);

    /// <summary>A node's world matrix from its local matrix and its parent's world matrix.</summary>
    public static Matrix4x4 Compose(Matrix4x4 local, Matrix4x4 parentWorld) => local * parentWorld;

    /// <summary>The stored rotation normalised for building a matrix; a zero or non-finite quaternion is the identity.</summary>
    public static Quaternion SafeRotation(Quaternion stored)
    {
        var q = Rotation(stored);
        float length = q.Length();
        return float.IsFinite(length) && length > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;
    }

    /// <summary>A position (or translation) in XFF space, in Red Faction metres.</summary>
    public static Vector3 ToRfPosition(Vector3 xff) => xff * RfScale;

    /// <summary>A stored vertex normal as Red Faction's (the front faces are the other side, so it is negated).</summary>
    public static Vector3 ToRfNormal(Vector3 xff) => -xff;

    /// <summary>A rotation in XFF space as the same rotation in Red Faction space (the axes are the same).</summary>
    public static Quaternion ToRfRotation(Quaternion xff) => Rotation(xff);

    /// <summary>A triangle's corners in Red Faction's order: (A, B, C) becomes (A, C, B). Per-corner colours and UVs go with their corners.</summary>
    public static (T A, T B, T C) ToRfTriangle<T>(T a, T b, T c) => (a, c, b);
}
