using System.Numerics;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Editing;

/// <summary>
/// Gizmo maths for a transform stored relative to a parent frame: a bone's bind relative to its parent's
/// rest frame, a collision sphere's centre or a prop point relative to its bone (or to the model when it
/// has none). A drag is measured in model space where the item is drawn; these functions turn it into the
/// stored, parent-relative values, so the item ends exactly where it was dragged in the pose shown (the
/// bind pose or a clip's pose: the parent frame is whichever one the item is drawn in).
/// </summary>
/// <remarks>
/// Active convention throughout (<see cref="Quat"/>, <see cref="Rigid"/>): model = parent ∘ local. A prop
/// point's stored quaternion is the conjugate of its local orientation (the viewport's and REDUX's reading;
/// <see cref="PropOrientation"/> / <see cref="PropStored"/>).
/// </remarks>
public static class FrameEdit
{
    /// <summary>
    /// Bone <paramref name="bone"/>'s model-space frame in <paramref name="world"/> (a pose's world
    /// transforms, or a skeleton's rest world), or the identity (the model itself) when the index is out of
    /// range: an unattached item (-1) or one naming a missing bone, as the viewport places it.
    /// </summary>
    /// <param name="world">Model-space transforms by bone index.</param>
    /// <param name="bone">The parent bone, or -1.</param>
    public static Rigid ParentFrame(ReadOnlySpan<Rigid> world, int bone) =>
        bone >= 0 && bone < world.Length ? world[bone] : Rigid.Identity;

    /// <summary>
    /// The model-space rotation of the axes a gizmo shows in <paramref name="space"/>: the item's own
    /// (Local: <c>parentWorld · local</c>), its parent frame's (Parent) or the model's (Model, identity).
    /// </summary>
    /// <param name="space">The gizmo space.</param>
    /// <param name="parentWorld">The parent frame's model-space rotation.</param>
    /// <param name="local">The item's rotation relative to the parent frame.</param>
    public static Quaternion SpaceAxes(OffsetSpace space, Quaternion parentWorld, Quaternion local) => space switch
    {
        OffsetSpace.Local => Quat.Normalize(Quat.Mul(parentWorld, local)),
        OffsetSpace.Parent => parentWorld,
        _ => Quaternion.Identity,
    };

    /// <summary>
    /// A local rotation after the delta <paramref name="delta"/> given in <paramref name="space"/>'s axes,
    /// composed exactly as <see cref="ClipEdit.OffsetBone"/> composes it: Local <c>L R</c>, Parent
    /// <c>R L</c>, Model <c>Pw⁻¹ R Pw L</c> (so the model rotation becomes <c>R · Pw L</c>).
    /// </summary>
    /// <param name="space">The space the delta is in.</param>
    /// <param name="local">The rotation relative to the parent frame, L.</param>
    /// <param name="parentWorld">The parent frame's model-space rotation, Pw.</param>
    /// <param name="delta">The rotation R.</param>
    public static Quaternion RotateLocal(OffsetSpace space, Quaternion local, Quaternion parentWorld, Quaternion delta) => space switch
    {
        OffsetSpace.Local => Quat.Mul(local, delta),
        OffsetSpace.Parent => Quat.Mul(delta, local),
        _ => Quat.Mul(Quat.Conj(parentWorld), Quat.Mul(delta, Quat.Mul(parentWorld, local))),
    };

    /// <summary>
    /// A local position after a model-space translation <paramref name="modelDelta"/>:
    /// <c>local + Rotate(conj(Pw), d)</c>, so the model-space point (<c>parent.TransformPoint(local)</c>)
    /// moves by exactly <paramref name="modelDelta"/>.
    /// </summary>
    /// <param name="local">The position relative to the parent frame.</param>
    /// <param name="parentWorld">The parent frame's model-space rotation.</param>
    /// <param name="modelDelta">The translation in model space.</param>
    public static Vector3 TranslateLocal(Vector3 local, Quaternion parentWorld, Vector3 modelDelta) =>
        local + Quat.Rotate(Quat.Conj(parentWorld), modelDelta);

    /// <summary>A model-space point expressed in the parent frame (the inverse of <c>parent.TransformPoint</c>).</summary>
    /// <param name="parent">The parent frame (unit rotation).</param>
    /// <param name="model">The point in model space.</param>
    public static Vector3 LocalPoint(Rigid parent, Vector3 model) => parent.Inverse().TransformPoint(model);

    /// <summary>
    /// A prop point's local orientation (active) from its stored quaternion: <c>conj(normalize(stored))</c>,
    /// the identity for a zero quaternion (as the viewport draws it).
    /// </summary>
    /// <param name="stored">The rotation as stored in the file.</param>
    public static Quaternion PropOrientation(Quaternion stored) =>
        stored.LengthSquared() < 1e-8f ? Quaternion.Identity : Quat.Conj(Quat.Normalize(stored));

    /// <summary>
    /// The stored form of a prop point's local orientation: <c>conj(normalize(orientation))</c>, in the
    /// hemisphere of <paramref name="oldStored"/> (as the prop point editor writes it).
    /// </summary>
    /// <param name="orientation">The local orientation (active).</param>
    /// <param name="oldStored">The value stored before the edit.</param>
    public static Quaternion PropStored(Quaternion orientation, Quaternion oldStored) =>
        Quat.Align(Quat.Conj(Quat.Normalize(orientation)), oldStored);
}
