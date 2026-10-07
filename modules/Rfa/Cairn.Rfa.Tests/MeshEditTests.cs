using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Tests;

/// <summary>MeshEdit operations on synthetic meshes (no stock data).</summary>
public class MeshEditTests
{
    private static FixedString Name(string text, int length) => FixedString.FromText(text, length);

    /// <summary><see cref="V3dFormatTests.SampleCharacter"/> with LOD 0's prop point copied to LOD 1, as in every stock mesh.</summary>
    internal static V3dFile Sample()
    {
        var mesh = V3dFormatTests.SampleCharacter();
        var sub = mesh.Submeshes.Single();
        var lod1 = sub.Lods[1] with { PropPoints = sub.Lods[0].PropPoints };
        return mesh with { Sections = mesh.Sections.SetItem(0, sub with { Lods = [sub.Lods[0], lod1] }) };
    }

    private static V3dSubmesh Sub(V3dFile mesh, int index = 0) => mesh.Submeshes.ElementAt(index);

    private static Dictionary<string, int> Errors(V3dFile mesh) => MeshLinter.Analyze(mesh)
        .Where(d => d.Severity == DiagnosticSeverity.Error)
        .GroupBy(d => d.Code)
        .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>The result writes, reads back to the same model, and has no Error diagnostic the input lacked.</summary>
    internal static void AssertValidEdit(V3dFile before, V3dFile after)
    {
        var read = V3dReader.Read(V3dWriter.Write(after), "edited.v3c");
        ModelAssert.Equal(after, read);
        AssertNoNewErrors(before, after);
    }

    /// <summary>The result has no Error diagnostic (counted per code) the input lacked.</summary>
    internal static void AssertNoNewErrors(V3dFile before, V3dFile after)
    {
        var was = Errors(before);
        foreach (var (code, count) in Errors(after))
            Assert.True(count <= was.GetValueOrDefault(code), $"The edit added {code} errors ({was.GetValueOrDefault(code)} -> {count}).");
    }

    private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"{expected} vs {actual}");

    private static void AssertSameRotation(Quaternion expected, Quaternion actual, float degrees = 1e-3f) =>
        Assert.True(Quat.AngleDegrees(expected, actual) <= degrees, $"{expected} vs {actual}");

    // ── Bones ────────────────────────────────────────────────────────────────

    [Fact]
    public void RenameBoneChangesOnlyThatName()
    {
        var mesh = Sample();
        var result = MeshEdit.RenameBone(mesh, 1, "chest");
        Assert.Equal("chest", result.Bones[1].Name.Text);
        Assert.Equal(V3dBone.NameSize, result.Bones[1].Name.Length);
        ModelAssert.Equal(mesh with { Sections = mesh.Sections.SetItem(2, new V3dBoneSection(mesh.Bones.SetItem(1, mesh.Bones[1] with { Name = Name("chest", 24) }), [])) }, result);
        Assert.Same(mesh, MeshEdit.RenameBone(mesh, 1, "spine"));
        AssertValidEdit(mesh, result);
        Assert.Equal(new string('x', 23), MeshEdit.RenameBone(mesh, 0, new string('x', 23)).Bones[0].Name.Text);
    }

    [Fact]
    public void RenameBoneKeepsLeftoverBytesOnlyWhenTheTextIsUnchanged()
    {
        var mesh = Sample();
        var dirty = FixedString.FromBytes([.. "spine\0zz"u8, .. new byte[16]]);
        mesh = mesh with { Sections = mesh.Sections.SetItem(2, new V3dBoneSection(mesh.Bones.SetItem(1, mesh.Bones[1] with { Name = dirty }), [])) };
        Assert.Same(mesh, MeshEdit.RenameBone(mesh, 1, "spine"));
        var renamed = MeshEdit.RenameBone(mesh, 1, "spine2");
        Assert.False(renamed.Bones[1].Name.HasTrailingBytes);
    }

    [Fact]
    public void RenameBoneRejectsBadNamesAndIndices()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentException>(() => MeshEdit.RenameBone(mesh, 0, new string('x', 24)));
        Assert.Throws<ArgumentException>(() => MeshEdit.RenameBone(mesh, 0, "boneā"));
        Assert.Throws<ArgumentException>(() => MeshEdit.RenameBone(mesh, 0, "a\0b"));
        Assert.Throws<ArgumentNullException>(() => MeshEdit.RenameBone(mesh, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RenameBone(mesh, 3, "x"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RenameBone(mesh, -1, "x"));
        var ex = Assert.Throws<ArgumentException>(() => MeshEdit.RenameBone(mesh, 0, new string('y', 30)));
        Assert.Contains("23", ex.Message);
    }

    [Fact]
    public void ReparentBoneKeepsTheRestWorldPose()
    {
        var mesh = Sample(); // parents: pelvis(0) -> root(2), spine(1) -> pelvis(0), root(2) = -1
        var result = MeshEdit.ReparentBone(mesh, 1, 2);
        Assert.Equal(2, result.Bones[1].ParentIndex);
        Assert.Equal(mesh.Bones[1].Rotation, result.Bones[1].Rotation);
        Assert.Equal(mesh.Bones[1].Position, result.Bones[1].Position);
        var before = Skeleton.FromFile(mesh);
        var after = Skeleton.FromFile(result);
        for (int i = 0; i < 3; i++) Assert.Equal(before.RestWorld[i], after.RestWorld[i]);
        Assert.NotEqual(before.RestLocal[1], after.RestLocal[1]);
        AssertValidEdit(mesh, result);

        Assert.Equal(-1, MeshEdit.ReparentBone(mesh, 0, -1).Bones[0].ParentIndex);
        Assert.Same(mesh, MeshEdit.ReparentBone(mesh, 1, 0));
    }

    [Fact]
    public void ReparentBoneRefusesSelfCyclesAndBadIndices()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentException>(() => MeshEdit.ReparentBone(mesh, 0, 0));
        Assert.Throws<ArgumentException>(() => MeshEdit.ReparentBone(mesh, 2, 1)); // 1 -> 0 -> 2: a loop
        Assert.Throws<ArgumentException>(() => MeshEdit.ReparentBone(mesh, 0, 1)); // 1 is 0's child
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.ReparentBone(mesh, 0, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.ReparentBone(mesh, 0, -2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.ReparentBone(mesh, 5, 0));
    }

    [Fact]
    public void ReorderBonesRemapsEveryBoneReference()
    {
        var mesh = Sample();
        var r = MeshEdit.ReorderBones(mesh, [2, 0, 1]); // new: root, pelvis, spine
        Assert.Equal([2, 0, 1], r.NewToOld.ToArray());
        Assert.Equal([1, 2, 0], r.OldToNew.ToArray());
        var m = r.Mesh;
        Assert.Equal(["root", "pelvis", "spine"], m.Bones.Select(b => b.Name.Text).ToArray());
        Assert.Equal([-1, 0, 1], m.Bones.Select(b => b.ParentIndex).ToArray());
        foreach (var lod in Sub(m).Lods)
        {
            var links = lod.Batches[0].BoneLinks;
            Assert.Equal(new V3dBoneLink(255, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF), links[0]);
            Assert.Equal(new V3dBoneLink(128, 127, 0, 0, 1, 2, 0xFF, 0xFF), links[1]);
            Assert.Equal(new V3dBoneLink(255, 0, 0, 0, 2, 0xFF, 0xFF, 0xFF), links[2]);
            Assert.Equal(2, lod.PropPoints[0].ParentIndex);
        }
        Assert.Equal(2, m.CollisionSpheres.Single().BoneIndex);
        // The rest pose of each bone moves with it.
        var before = Skeleton.FromFile(mesh);
        var after = Skeleton.FromFile(m);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(before.RestWorld[r.NewToOld[i]], after.RestWorld[i]);
            Assert.Equal(before.RestLocal[r.NewToOld[i]], after.RestLocal[i]);
        }
        AssertValidEdit(mesh, m);

        // The inverse permutation restores the original byte for byte.
        var back = MeshEdit.ReorderBones(m, r.OldToNew);
        Assert.Equal(V3dWriter.Write(mesh), V3dWriter.Write(back.Mesh));
    }

    [Fact]
    public void ReorderBonesWithTheIdentityReturnsTheMeshAndRejectsNonPermutations()
    {
        var mesh = Sample();
        var r = MeshEdit.ReorderBones(mesh, [0, 1, 2]);
        Assert.Same(mesh, r.Mesh);
        Assert.Equal([0, 1, 2], r.OldToNew.ToArray());
        Assert.Throws<ArgumentException>(() => MeshEdit.ReorderBones(mesh, [0, 1]));
        Assert.Throws<ArgumentException>(() => MeshEdit.ReorderBones(mesh, [0, 1, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.ReorderBones(mesh, [0, 1, 3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.ReorderBones(mesh, [0, -1, 2]));
        Assert.Throws<ArgumentNullException>(() => MeshEdit.ReorderBones(mesh, null!));
    }

    [Fact]
    public void BoneBindRoundTripsInBothSpaces()
    {
        var mesh = Sample();
        var skeleton = Skeleton.FromFile(mesh);
        for (int b = 0; b < 3; b++)
        {
            Assert.Equal(skeleton.RestWorld[b], MeshEdit.GetBoneBind(mesh, b, BindSpace.World));
            Assert.Equal(skeleton.RestLocal[b], MeshEdit.GetBoneBind(mesh, b, BindSpace.Local));
            foreach (var space in new[] { BindSpace.World, BindSpace.Local })
            {
                var bind = MeshEdit.GetBoneBind(mesh, b, space);
                Assert.Same(mesh, MeshEdit.SetBoneBind(mesh, b, bind, space));
                // The same rotation with the other sign goes through the conversion and lands on the stored values.
                var flipped = MeshEdit.SetBoneBind(mesh, b, bind with { Rotation = Quat.Negate(bind.Rotation) }, space);
                AssertClose(mesh.Bones[b].Position, flipped.Bones[b].Position);
                Assert.True(Quat.Dot(mesh.Bones[b].Rotation, flipped.Bones[b].Rotation) > 0.99999f);
            }
        }
    }

    [Fact]
    public void SetBoneBindConvertsToTheStoredInverseBind()
    {
        var mesh = Sample();
        var rot = Quat.FromAxisAngle(new Vector3(1, 2, 3), 0.7f);
        var pos = new Vector3(0.3f, 1.2f, -0.4f);
        var result = MeshEdit.SetBoneBind(mesh, 2, new Rigid(rot, pos), BindSpace.World);
        var stored = result.Bones[2];
        AssertSameRotation(rot, stored.Rotation, 1e-4f);
        AssertClose(-Quat.Rotate(Quat.Conj(rot), pos), stored.Position);
        var world = Skeleton.FromFile(result).RestWorld[2];
        AssertSameRotation(rot, world.Rotation, 1e-4f);
        AssertClose(pos, world.Position);
        AssertValidEdit(mesh, result);

        // Local: relative to the parent's rest world.
        var local = new Rigid(Quat.FromAxisAngle(Vector3.UnitZ, 0.3f), new Vector3(0, 0.5f, 0));
        result = MeshEdit.SetBoneBind(mesh, 1, local, BindSpace.Local);
        var parentWorld = Skeleton.FromFile(mesh).RestWorld[0];
        var expected = parentWorld.Compose(local);
        var actual = MeshEdit.GetBoneBind(result, 1, BindSpace.World);
        AssertSameRotation(expected.Rotation, actual.Rotation, 1e-4f);
        AssertClose(expected.Position, actual.Position);
        var back = MeshEdit.GetBoneBind(result, 1, BindSpace.Local);
        AssertSameRotation(local.Rotation, back.Rotation, 1e-4f);
        AssertClose(local.Position, back.Position);
    }

    [Fact]
    public void SetBoneBindMovesChildrenOnlyWhenAsked()
    {
        var mesh = Sample(); // root(2) -> pelvis(0) -> spine(1)
        var before = Skeleton.FromFile(mesh);
        var moved = new Rigid(Quat.FromAxisAngle(Vector3.UnitY, 0.5f), new Vector3(1, 2, 3));

        var still = MeshEdit.SetBoneBind(mesh, 2, moved, BindSpace.World, childrenFollow: false);
        Assert.Equal(mesh.Bones[0], still.Bones[0]);
        Assert.Equal(mesh.Bones[1], still.Bones[1]);
        var stillSkeleton = Skeleton.FromFile(still);
        Assert.Equal(before.RestWorld[0], stillSkeleton.RestWorld[0]);

        var follow = MeshEdit.SetBoneBind(mesh, 2, moved, BindSpace.World, childrenFollow: true);
        var after = Skeleton.FromFile(follow);
        for (int b = 0; b < 2; b++)
        {
            AssertSameRotation(before.RestLocal[b].Rotation, after.RestLocal[b].Rotation, 1e-3f);
            AssertClose(before.RestLocal[b].Position, after.RestLocal[b].Position);
        }
        AssertClose(moved.Position, after.RestWorld[2].Position);
        AssertValidEdit(mesh, follow);
    }

    [Fact]
    public void SetBoneBindRejectsBadInput()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentException>(() => MeshEdit.SetBoneBind(mesh, 0, new Rigid(new Quaternion(float.NaN, 0, 0, 1), Vector3.Zero), BindSpace.World));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetBoneBind(mesh, 0, new Rigid(default, Vector3.Zero), BindSpace.World));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetBoneBind(mesh, 0, new Rigid(Quaternion.Identity, new Vector3(float.PositiveInfinity, 0, 0)), BindSpace.Local));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetBoneBind(mesh, 3, Rigid.Identity, BindSpace.World));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetBoneBind(mesh, 0, Rigid.Identity, (BindSpace)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.GetBoneBind(mesh, -1, BindSpace.World));
    }

    // ── Collision spheres ────────────────────────────────────────────────────

    [Fact]
    public void AddCollisionSphereGoesAfterTheLastSphereAndUpdatesTheHeader()
    {
        var mesh = Sample();
        var result = MeshEdit.AddCollisionSphere(mesh, "chest", 0, new Vector3(0, 1, 0), 0.4f);
        Assert.Equal(2, result.Header.CollisionSphereCount);
        var added = Assert.IsType<V3dCollisionSphere>(result.Sections[2]);
        Assert.Equal("chest", added.Name.Text);
        Assert.Equal(0, added.BoneIndex);
        Assert.Equal(0.4f, added.Radius);
        Assert.Empty(added.Extra);
        Assert.IsType<V3dBoneSection>(result.Sections[3]);
        Assert.Same(mesh.Sections[1], result.Sections[1]);
        AssertValidEdit(mesh, result);

        // Without any sphere it goes after the last submesh, before the bones.
        var none = MeshEdit.RemoveCollisionSphere(mesh, 0);
        Assert.Equal(0, none.Header.CollisionSphereCount);
        Assert.IsType<V3dBoneSection>(none.Sections[1]);
        var first = MeshEdit.AddCollisionSphere(none, "a", -1, Vector3.Zero, 1f);
        Assert.IsType<V3dCollisionSphere>(first.Sections[1]);
        Assert.IsType<V3dBoneSection>(first.Sections[2]);
        Assert.Equal(1, first.Header.CollisionSphereCount);
        AssertValidEdit(none, first);

        // Adding back what was removed (the last sphere) restores the file.
        var head = mesh.CollisionSpheres.Single();
        Assert.Equal(V3dWriter.Write(mesh), V3dWriter.Write(MeshEdit.AddCollisionSphere(none, "head", head.BoneIndex, head.Position, head.Radius)));
    }

    [Fact]
    public void RemoveCollisionSphereRejectsMissingSpheres()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RemoveCollisionSphere(mesh, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RemoveCollisionSphere(mesh, -1));
        var none = MeshEdit.RemoveCollisionSphere(mesh, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RemoveCollisionSphere(none, 0));
    }

    [Fact]
    public void SetCollisionSphereKeepsExtraBytesAndRawNames()
    {
        var mesh = Sample();
        var dirty = FixedString.FromBytes([.. "head\0q"u8, .. new byte[18]]);
        mesh = mesh with { Sections = mesh.Sections.SetItem(1, ((V3dCollisionSphere)mesh.Sections[1]) with { Name = dirty, Extra = [9, 8, 7, 6] }) };
        var old = mesh.CollisionSpheres.Single();
        Assert.Same(mesh, MeshEdit.SetCollisionSphere(mesh, 0, "head", old.BoneIndex, old.Position, old.Radius));

        var result = MeshEdit.SetCollisionSphere(mesh, 0, "head", 2, new Vector3(1, 2, 3), 0.5f);
        var s = result.CollisionSpheres.Single();
        Assert.Equal(dirty, s.Name);
        Assert.Equal([9, 8, 7, 6], s.Extra.ToArray());
        Assert.Equal(2, s.BoneIndex);
        Assert.Equal(new Vector3(1, 2, 3), s.Position);
        Assert.Equal(0.5f, s.Radius);
        AssertValidEdit(mesh, result);

        var renamed = MeshEdit.SetCollisionSphere(mesh, 0, "skull", old.BoneIndex, old.Position, old.Radius).CollisionSpheres.Single();
        Assert.Equal("skull", renamed.Name.Text);
        Assert.False(renamed.Name.HasTrailingBytes);
    }

    [Fact]
    public void CollisionSphereEditsRejectBadValues()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentException>(() => MeshEdit.AddCollisionSphere(mesh, new string('s', 24), 0, Vector3.Zero, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.AddCollisionSphere(mesh, "s", 3, Vector3.Zero, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.AddCollisionSphere(mesh, "s", -2, Vector3.Zero, 1f));
        Assert.Throws<ArgumentException>(() => MeshEdit.AddCollisionSphere(mesh, "s", 0, new Vector3(float.NaN), 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.AddCollisionSphere(mesh, "s", 0, Vector3.Zero, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.AddCollisionSphere(mesh, "s", 0, Vector3.Zero, -1f));
        Assert.Throws<ArgumentException>(() => MeshEdit.AddCollisionSphere(mesh, "s", 0, Vector3.Zero, float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetCollisionSphere(mesh, 0, "head", 1, Vector3.Zero, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetCollisionSphere(mesh, 0, "head", 7, Vector3.Zero, 1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetCollisionSphere(mesh, 1, "head", 1, Vector3.Zero, 1f));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetCollisionSphere(mesh, 0, "h一", 1, Vector3.Zero, 1f));
    }

    // ── Materials, textures, LODs, submeshes ─────────────────────────────────

    [Fact]
    public void SetMaterialReplacesTheRecordOnly()
    {
        var mesh = Sample();
        var old = Sub(mesh).Materials[1];
        Assert.Same(mesh, MeshEdit.SetMaterial(mesh, 0, 1, old with { }));
        var value = old with { Emissive = 0.25f, Flags = 0x19, DiffuseMap = Name("other.tga", 32) };
        var result = MeshEdit.SetMaterial(mesh, 0, 1, value);
        Assert.Same(value, Sub(result).Materials[1]);
        Assert.Same(Sub(mesh).Materials[0], Sub(result).Materials[0]);
        // LOD texture names are not touched.
        Assert.Equal("eyes.vbm", Sub(result).Lods[1].Textures[1].FileName);
        Assert.Same(Sub(mesh).Lods[0], Sub(result).Lods[0]);
        AssertValidEdit(mesh, result);

        Assert.Throws<ArgumentException>(() => MeshEdit.SetMaterial(mesh, 0, 1, old with { DiffuseMap = Name("x.tga", 24) }));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetMaterial(mesh, 0, 1, old with { ReflectionMap = FixedString.FromBytes(new byte[32].Select(_ => (byte)'a').ToArray()) }));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetMaterial(mesh, 0, 1, old with { Emissive = float.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetMaterial(mesh, 0, 2, old));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetMaterial(mesh, 1, 0, old));
        Assert.Throws<ArgumentNullException>(() => MeshEdit.SetMaterial(mesh, 0, 0, null!));
    }

    /// <summary>The sample with LOD 1's skin entry renamed to a LOD-specific name and an upper-case copy in LOD 0.</summary>
    private static V3dFile TextureSample()
    {
        var mesh = Sample();
        var sub = Sub(mesh);
        var lod0 = sub.Lods[0] with { Textures = [new V3dLodTexture(0, "SKIN.TGA")] };
        var lod1 = sub.Lods[1] with { Textures = [new V3dLodTexture(0, "skin-mip1.tga"), new V3dLodTexture(1, "eyes.vbm")] };
        return mesh with { Sections = mesh.Sections.SetItem(0, sub with { Lods = [lod0, lod1] }) };
    }

    [Fact]
    public void SetTextureNameRenamesMatchingLodEntries()
    {
        var mesh = TextureSample();
        var result = MeshEdit.SetTextureName(mesh, 0, 0, "flesh.tga");
        var sub = Sub(result);
        Assert.Equal("flesh.tga", sub.Materials[0].DiffuseMap.Text);
        Assert.Equal("flesh.tga", sub.Lods[0].Textures[0].FileName); // matched case-insensitively
        Assert.Equal("skin-mip1.tga", sub.Lods[1].Textures[0].FileName); // LOD-specific name kept
        Assert.Same(Sub(mesh).Lods[1], sub.Lods[1]);
        Assert.Same(Sub(mesh).Materials[1], sub.Materials[1]);
        AssertValidEdit(mesh, result);

        var all = Sub(MeshEdit.SetTextureName(mesh, 0, 0, "flesh.tga", LodTextureUpdate.All));
        Assert.Equal("flesh.tga", all.Lods[1].Textures[0].FileName);
        Assert.Equal("eyes.vbm", all.Lods[1].Textures[1].FileName);

        Assert.Same(mesh, MeshEdit.SetTextureName(mesh, 0, 0, "skin.tga"));
        var unified = Sub(MeshEdit.SetTextureName(mesh, 0, 0, "skin.tga", LodTextureUpdate.All));
        Assert.Equal(["skin.tga"], unified.Lods[0].Textures.Select(t => t.FileName).ToArray());
        Assert.Equal("skin.tga", unified.Lods[1].Textures[0].FileName);
        Assert.Equal(Sub(mesh).Materials[0].DiffuseMap, unified.Materials[0].DiffuseMap);
    }

    [Fact]
    public void SetTextureNameRejectsBadNames()
    {
        var mesh = Sample();
        Assert.Equal(new string('t', 31), Sub(MeshEdit.SetTextureName(mesh, 0, 0, new string('t', 31))).Materials[0].DiffuseMap.Text);
        Assert.Throws<ArgumentException>(() => MeshEdit.SetTextureName(mesh, 0, 0, new string('t', 32)));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetTextureName(mesh, 0, 0, "€.tga"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetTextureName(mesh, 0, 5, "a.tga"));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetTextureName(mesh, 0, 0, "a.tga", (LodTextureUpdate)9));
    }

    [Fact]
    public void SetLodDistancesNeedsOneFiniteNonNegativeDistancePerLod()
    {
        var mesh = Sample();
        Assert.Same(mesh, MeshEdit.SetLodDistances(mesh, 0, [0f, 10f]));
        var result = MeshEdit.SetLodDistances(mesh, 0, [0f, 25.5f]);
        Assert.Equal([0f, 25.5f], Sub(result).LodDistances.ToArray());
        Assert.True(Sub(mesh).Lods == Sub(result).Lods, "the LOD array was rebuilt");
        AssertValidEdit(mesh, result);
        Assert.Throws<ArgumentException>(() => MeshEdit.SetLodDistances(mesh, 0, [0f]));
        Assert.Throws<ArgumentException>(() => MeshEdit.SetLodDistances(mesh, 0, [0f, 1f, 2f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetLodDistances(mesh, 0, [0f, -1f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetLodDistances(mesh, 0, [float.NaN, 1f]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetLodDistances(mesh, 0, [0f, float.PositiveInfinity]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetLodDistances(mesh, 1, [0f, 1f]));
    }

    [Fact]
    public void RenameSubmeshRenamesMatchingTrailersAndParent()
    {
        var mesh = Sample();
        var sub = Sub(mesh) with
        {
            ParentName = Name("body", 24),
            Trailers = [new V3dSubmeshTrailer(Name("body", 24), 0f), new V3dSubmeshTrailer(Name("other", 24), 1f)],
        };
        mesh = mesh with { Sections = mesh.Sections.SetItem(0, sub) };
        Assert.Same(mesh, MeshEdit.RenameSubmesh(mesh, 0, "body"));
        var result = Sub(MeshEdit.RenameSubmesh(mesh, 0, "torso"));
        Assert.Equal("torso", result.Name.Text);
        Assert.Equal("torso", result.ParentName.Text);
        Assert.Equal("torso", result.Trailers[0].Name.Text);
        Assert.Same(sub.Trailers[1], result.Trailers[1]);
        Assert.True(sub.Lods == result.Lods, "the LOD array was rebuilt");

        var plain = Sample();
        var renamed = MeshEdit.RenameSubmesh(plain, 0, "torso");
        Assert.Equal("None", Sub(renamed).ParentName.Text);
        Assert.Equal("torso", Sub(renamed).Trailers[0].Name.Text);
        AssertValidEdit(plain, renamed);
        Assert.Throws<ArgumentException>(() => MeshEdit.RenameSubmesh(plain, 0, new string('n', 24)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RenameSubmesh(plain, 1, "x"));
    }

    // ── Prop points ──────────────────────────────────────────────────────────

    [Fact]
    public void PropPointsAreEditedInEveryLod()
    {
        var mesh = Sample();
        Assert.Equal(1, MeshEdit.PropPointCount(mesh));

        var name67 = new string('p', 67);
        var added = MeshEdit.AddPropPoint(mesh, name67, 0, new Quaternion(0, 0, 0.5f, 0.8660254f), new Vector3(1, 2, 3));
        Assert.Equal(2, MeshEdit.PropPointCount(added));
        foreach (var lod in Sub(added).Lods)
        {
            Assert.Equal(2, lod.PropPoints.Length);
            Assert.Same(Sub(mesh).Lods[0].PropPoints[0], lod.PropPoints[0]);
            Assert.Equal(name67, lod.PropPoints[1].Name.Text);
            Assert.Equal(V3dPropPoint.NameSize, lod.PropPoints[1].Name.Length);
            Assert.Equal(0, lod.PropPoints[1].ParentIndex);
        }
        AssertValidEdit(mesh, added);

        var old = Sub(mesh).Lods[0].PropPoints[0];
        Assert.Same(mesh, MeshEdit.SetPropPoint(mesh, 0, "muzzle_1", old.ParentIndex, old.Rotation, old.Position));
        var set = MeshEdit.SetPropPoint(mesh, 0, "muzzle_1", 2, old.Rotation, new Vector3(5, 5, 5));
        foreach (var lod in Sub(set).Lods)
        {
            Assert.Equal(old.Name, lod.PropPoints[0].Name); // raw bytes kept
            Assert.True(lod.PropPoints[0].Name.HasTrailingBytes);
            Assert.Equal(2, lod.PropPoints[0].ParentIndex);
            Assert.Equal(new Vector3(5, 5, 5), lod.PropPoints[0].Position);
        }
        AssertValidEdit(mesh, set);
        Assert.False(Sub(MeshEdit.SetPropPoint(mesh, 0, "gun", old.ParentIndex, old.Rotation, old.Position)).Lods[1].PropPoints[0].Name.HasTrailingBytes);

        var removed = MeshEdit.RemovePropPoint(added, 0);
        Assert.Equal(1, MeshEdit.PropPointCount(removed));
        Assert.All(Sub(removed).Lods, lod => Assert.Equal(name67, lod.PropPoints.Single().Name.Text));
        Assert.Equal(V3dWriter.Write(mesh), V3dWriter.Write(MeshEdit.RemovePropPoint(added, 1)));
    }

    [Fact]
    public void PropPointEditsOnlyTouchLodsThatHaveTheIndex()
    {
        var mesh = V3dFormatTests.SampleCharacter(); // LOD 1 has no prop points
        var set = MeshEdit.SetPropPoint(mesh, 0, "muzzle_2", 1, Quaternion.Identity, Vector3.One);
        Assert.Equal("muzzle_2", Sub(set).Lods[0].PropPoints[0].Name.Text);
        Assert.Same(Sub(mesh).Lods[1], Sub(set).Lods[1]);
        var removed = MeshEdit.RemovePropPoint(mesh, 0);
        Assert.Empty(Sub(removed).Lods[0].PropPoints);
        Assert.Same(Sub(mesh).Lods[1], Sub(removed).Lods[1]);
        Assert.Equal(0, MeshEdit.PropPointCount(removed));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RemovePropPoint(removed, 0));
    }

    [Fact]
    public void PropPointEditsRejectBadValues()
    {
        var mesh = Sample();
        Assert.Throws<ArgumentException>(() => MeshEdit.AddPropPoint(mesh, new string('p', 68), 0, Quaternion.Identity, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.AddPropPoint(mesh, "p", 3, Quaternion.Identity, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => MeshEdit.AddPropPoint(mesh, "p", 0, new Quaternion(0, 0, 0, float.NaN), Vector3.Zero));
        Assert.Throws<ArgumentException>(() => MeshEdit.AddPropPoint(mesh, "p", 0, default, Vector3.Zero));
        Assert.Throws<ArgumentException>(() => MeshEdit.AddPropPoint(mesh, "p", 0, Quaternion.Identity, new Vector3(0, float.NegativeInfinity, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetPropPoint(mesh, 1, "p", 0, Quaternion.Identity, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.SetPropPoint(mesh, 0, "p", -5, Quaternion.Identity, Vector3.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshEdit.RemovePropPoint(mesh, -1));
        var empty = new V3dFile { Header = mesh.Header with { SubmeshCount = 0, TotalMaterials = 0, CollisionSphereCount = 0 } };
        Assert.Throws<ArgumentException>(() => MeshEdit.AddPropPoint(empty, "p", -1, Quaternion.Identity, Vector3.Zero));
        Assert.Equal(0, MeshEdit.PropPointCount(empty));
    }

    // ── Every result is a valid mesh ─────────────────────────────────────────

    [Fact]
    public void EveryEditWritesReadsBackAndAddsNoLintErrors()
    {
        var mesh = TextureSample();
        var old = Sub(mesh).Materials[0];
        Func<V3dFile, V3dFile>[] edits =
        [
            m => MeshEdit.RenameBone(m, 2, "base"),
            m => MeshEdit.ReparentBone(m, 1, 2),
            m => MeshEdit.ReparentBone(m, 0, -1),
            m => MeshEdit.ReorderBones(m, [1, 2, 0]).Mesh,
            m => MeshEdit.SetBoneBind(m, 0, new Rigid(Quat.FromAxisAngle(Vector3.UnitX, 1f), Vector3.One), BindSpace.Local, childrenFollow: true),
            m => MeshEdit.AddCollisionSphere(m, "knee", 0, Vector3.UnitY, 0.1f),
            m => MeshEdit.RemoveCollisionSphere(m, 0),
            m => MeshEdit.SetCollisionSphere(m, 0, "brain", -1, Vector3.Zero, 2f),
            m => MeshEdit.SetMaterial(m, 0, 0, old with { Emissive = 1f, Unknown0 = 0.5f }),
            m => MeshEdit.SetTextureName(m, 0, 0, "x.tga", LodTextureUpdate.All),
            m => MeshEdit.SetLodDistances(m, 0, [0f, 3f]),
            m => MeshEdit.AddPropPoint(m, "thruster", -1, Quaternion.Identity, Vector3.Zero),
            m => MeshEdit.RemovePropPoint(m, 0),
            m => MeshEdit.SetPropPoint(m, 0, "x", 0, Quaternion.Identity, Vector3.UnitZ),
            m => MeshEdit.RenameSubmesh(m, 0, "torso"),
        ];
        var chained = mesh;
        foreach (var edit in edits)
        {
            var result = edit(mesh);
            Assert.NotSame(mesh, result);
            AssertValidEdit(mesh, result);
            var next = edit(chained);
            AssertValidEdit(chained, next);
            chained = next;
        }
        Assert.Empty(Errors(chained));
    }

    [Fact]
    public void ReorderAndConformKeepTheSkinnedSyntheticMesh()
    {
        var mesh = Sample();
        var clip = EditingTestClips.Make(bones: 3);
        MeshEditCorpusTests.AssertReorderKeepsSkinning(mesh, clip, [1, 2, 0]);
        MeshEditCorpusTests.AssertReorderKeepsSkinning(mesh, clip, [2, 1, 0]);
    }

    [Fact]
    public void EditsNeverChangeTheInput()
    {
        var mesh = Sample();
        byte[] before = V3dWriter.Write(mesh);
        MeshEdit.ReorderBones(mesh, [2, 1, 0]);
        MeshEdit.SetBoneBind(mesh, 2, Rigid.Identity with { Position = Vector3.One }, BindSpace.World, true);
        MeshEdit.AddPropPoint(mesh, "a", -1, Quaternion.Identity, Vector3.Zero);
        MeshEdit.SetTextureName(mesh, 0, 0, "q.tga", LodTextureUpdate.All);
        MeshEdit.AddCollisionSphere(mesh, "a", -1, Vector3.Zero, 1f);
        Assert.Equal(before, V3dWriter.Write(mesh));
    }
}
