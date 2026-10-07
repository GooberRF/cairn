using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

/// <summary>
/// <see cref="FrameEdit"/>: the conversions behind the mesh gizmos (model-space drags to bone-relative
/// values), in the bind pose and in an animated pose, with round trips, and through <c>MeshEdit</c>.
/// </summary>
public class FrameEditTests
{
    private static void Near(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");

    private static void SameRotation(Quaternion expected, Quaternion actual, float degrees = 1e-3f) =>
        Assert.True(Quat.AngleDegrees(expected, actual) <= degrees, $"expected {expected}, got {actual}");

    private static readonly OffsetSpace[] Spaces = [OffsetSpace.Local, OffsetSpace.Parent, OffsetSpace.Model];

    /// <summary>The sample character's skeleton posed away from its bind pose (every bone turned and the root moved).</summary>
    private static Pose AnimatedPose(V3dFile mesh)
    {
        var pose = new Pose(Skeleton.FromFile(mesh));
        for (int i = 0; i < pose.Count; i++)
        {
            var turn = Quat.FromAxisAngle(Vector3.Normalize(new Vector3(0.3f + i, 1f, -0.4f * i)), 0.35f + 0.2f * i);
            pose.Local[i] = new Rigid(Quat.Normalize(Quat.Mul(pose.Local[i].Rotation, turn)), pose.Local[i].Position + new Vector3(0.02f * i, 0.05f, -0.03f));
        }
        pose.SolveWorld();
        return pose;
    }

    [Fact]
    public void ParentFrameIsTheBoneOrTheModel()
    {
        var pose = AnimatedPose(MeshEditTests.Sample());
        Assert.Equal(pose.World[1], FrameEdit.ParentFrame(pose.World, 1));
        Assert.Equal(Rigid.Identity, FrameEdit.ParentFrame(pose.World, -1));
        Assert.Equal(Rigid.Identity, FrameEdit.ParentFrame(pose.World, pose.Count));
        Assert.Equal(Rigid.Identity, FrameEdit.ParentFrame([], 0));
    }

    [Fact]
    public void TranslateLocalMovesTheModelPointByTheDeltaInTheBindPoseAndAnAnimatedPose()
    {
        var mesh = MeshEditTests.Sample();
        var skeleton = Skeleton.FromFile(mesh);
        var animated = AnimatedPose(mesh);
        var local = new Vector3(0.1f, -0.25f, 0.4f);
        var delta = new Vector3(0.07f, -0.02f, 0.13f);
        foreach (var world in new[] { skeleton.RestWorld.ToArray(), animated.World })
        {
            for (int bone = -1; bone < skeleton.Count; bone++)
            {
                var parent = FrameEdit.ParentFrame(world, bone);
                var before = parent.TransformPoint(local);
                var moved = FrameEdit.TranslateLocal(local, parent.Rotation, delta);
                Near(before + delta, parent.TransformPoint(moved));
                // Round trips: back from the model point, and back by the opposite delta.
                Near(moved, FrameEdit.LocalPoint(parent, before + delta));
                Near(local, FrameEdit.TranslateLocal(moved, parent.Rotation, -delta));
                Near(local, FrameEdit.LocalPoint(parent, before));
            }
        }
    }

    [Fact]
    public void RotateLocalComposesInEachSpaceAndRoundTrips()
    {
        var pose = AnimatedPose(MeshEditTests.Sample());
        var parent = pose.World[1].Rotation;
        var local = Quat.Normalize(new Quaternion(0.2f, -0.4f, 0.1f, 0.85f));
        var r = Quat.FromAxisAngle(Vector3.Normalize(new Vector3(1, 2, -1)), 0.6f);
        var model = Quat.Mul(parent, local);
        foreach (var space in Spaces)
        {
            var next = FrameEdit.RotateLocal(space, local, parent, r);
            var expectedModel = space switch
            {
                OffsetSpace.Local => Quat.Mul(model, r),
                OffsetSpace.Parent => Quat.Mul(parent, Quat.Mul(r, local)),
                _ => Quat.Mul(r, model),
            };
            SameRotation(expectedModel, Quat.Mul(parent, next));
            // A turn about the space's own axis is a turn about the gizmo's drawn axis.
            var axes = FrameEdit.SpaceAxes(space, parent, local);
            var drawnAxis = Quat.Rotate(axes, Vector3.UnitY);
            var turned = FrameEdit.RotateLocal(space, local, parent, Quat.FromAxisAngle(Vector3.UnitY, 0.5f));
            SameRotation(Quat.Mul(Quat.FromAxisAngle(drawnAxis, 0.5f), model), Quat.Mul(parent, turned));
            SameRotation(local, FrameEdit.RotateLocal(space, next, parent, Quat.Conj(r)));
        }
        Assert.Equal(Quaternion.Identity, FrameEdit.SpaceAxes(OffsetSpace.Model, parent, local));
        Assert.Equal(parent, FrameEdit.SpaceAxes(OffsetSpace.Parent, parent, local));
        SameRotation(model, FrameEdit.SpaceAxes(OffsetSpace.Local, parent, local));
    }

    [Fact]
    public void PropOrientationAndStoredRoundTripInTheOldHemisphere()
    {
        var stored = Quat.Normalize(new Quaternion(-0.3f, 0.5f, 0.2f, -0.78f));
        var orientation = FrameEdit.PropOrientation(stored);
        SameRotation(Quat.Conj(stored), orientation);
        var back = FrameEdit.PropStored(orientation, stored);
        Assert.True(Quat.Dot(back, stored) > 0.99999f, $"{back} vs {stored}");
        Assert.Equal(Quaternion.Identity, FrameEdit.PropOrientation(default));
        // A turn keeps the stored value in the old hemisphere.
        var turned = FrameEdit.PropStored(Quat.Mul(orientation, Quat.FromAxisAngle(Vector3.UnitX, 0.1f)), stored);
        Assert.True(Quat.Dot(turned, stored) > 0, $"{turned} left the hemisphere of {stored}");
    }

    [Fact]
    public void APropPointRotatedInAnAnimatedPoseTurnsByTheModelDelta()
    {
        var mesh = MeshEditTests.Sample();
        var pose = AnimatedPose(mesh);
        var prop = mesh.Submeshes.Single().Lods[0].PropPoints[0];
        var bone = FrameEdit.ParentFrame(pose.World, prop.ParentIndex);
        var orientation = FrameEdit.PropOrientation(prop.Rotation);
        var r = Quat.FromAxisAngle(Vector3.UnitZ, 0.4f);
        var next = FrameEdit.RotateLocal(OffsetSpace.Model, orientation, bone.Rotation, r);
        var stored = FrameEdit.PropStored(next, prop.Rotation);
        SameRotation(Quat.Mul(r, Quat.Mul(bone.Rotation, orientation)), Quat.Mul(bone.Rotation, FrameEdit.PropOrientation(stored)));

        // Through MeshEdit: every LOD's copy gets the same values.
        var edited = MeshEdit.SetPropPoint(mesh, 0, prop.Name.Text, prop.ParentIndex, stored, FrameEdit.TranslateLocal(prop.Position, bone.Rotation, new Vector3(0, 0.1f, 0)));
        var lods = edited.Submeshes.Single().Lods;
        Assert.All(lods, l => Assert.Equal(lods[0].PropPoints[0], l.PropPoints[0]));
        Near(bone.TransformPoint(prop.Position) + new Vector3(0, 0.1f, 0), bone.TransformPoint(lods[1].PropPoints[0].Position));
    }

    [Fact]
    public void ASphereMovedInAnAnimatedPoseEndsWhereItWasDragged()
    {
        var mesh = MeshEditTests.Sample();
        var pose = AnimatedPose(mesh);
        var sphere = mesh.CollisionSpheres.First();
        var bone = FrameEdit.ParentFrame(pose.World, sphere.BoneIndex);
        var delta = new Vector3(-0.05f, 0.12f, 0.03f);
        var position = FrameEdit.TranslateLocal(sphere.Position, bone.Rotation, delta);
        var edited = MeshEdit.SetCollisionSphere(mesh, 0, sphere.Name.Text, sphere.BoneIndex, position, sphere.Radius);
        Near(bone.TransformPoint(sphere.Position) + delta, bone.TransformPoint(edited.CollisionSpheres.First().Position));
    }

    [Fact]
    public void BindMovesAndTurnsThroughSetBoneBindLandWhereTheGizmoSays()
    {
        var mesh = MeshEditTests.Sample();
        var skeleton = Skeleton.FromFile(mesh);
        int bone = 0; // pelvis: has a parent (root) and a child (spine)
        var world = skeleton.RestWorld[bone];
        var local = skeleton.RestLocal[bone];
        var parent = FrameEdit.ParentFrame(skeleton.RestWorld.AsSpan(), skeleton.EffectiveParents[bone]);
        var delta = new Vector3(0.1f, 0.02f, -0.04f);
        var r = Quat.FromAxisAngle(Vector3.UnitX, 0.3f);
        foreach (var space in Spaces)
        {
            var moved = MeshEdit.SetBoneBind(mesh, bone, new Rigid(local.Rotation, FrameEdit.TranslateLocal(local.Position, parent.Rotation, delta)), BindSpace.Local);
            Near(world.Position + delta, Skeleton.FromFile(moved).RestWorld[bone].Position);

            var turned = MeshEdit.SetBoneBind(mesh, bone, new Rigid(FrameEdit.RotateLocal(space, local.Rotation, parent.Rotation, r), local.Position), BindSpace.Local);
            var after = Skeleton.FromFile(turned).RestWorld[bone];
            var axes = FrameEdit.SpaceAxes(space, parent.Rotation, local.Rotation);
            var modelTurn = Quat.Mul(axes, Quat.Mul(r, Quat.Conj(axes)));
            SameRotation(Quat.Mul(modelTurn, world.Rotation), after.Rotation);
            Near(world.Position, after.Position);
        }
    }
}
