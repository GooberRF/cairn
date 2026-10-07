using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Formats.Maths;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// MeshEdit on the stock corpus (read in place; each test passes trivially without it): identity edits
/// change nothing, bone reorders are lossless, and reorder + conform keeps an animated character's
/// skinned vertices identical.
/// </summary>
public class MeshEditCorpusTests(ITestOutputHelper output)
{
    [Fact]
    public void IdentityEditsOnEveryStockMeshChangeNothing()
    {
        if (TestPaths.Corpus is null) return;
        int characters = 0, statics = 0, byteIdentical = 0, reordersRestored = 0, bindChecks = 0;
        double maxBindError = 0;
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus).Order(StringComparer.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".v3c" or ".v3m")) continue;
            if (ext == ".v3c") characters++;
            else statics++;
            string name = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            var mesh = V3dReader.Read(bytes, name);

            var r = IdentityEdits(mesh);
            Assert.Same(mesh, r);
            Assert.True(bytes.AsSpan().SequenceEqual(V3dWriter.Write(r)), $"{name}: identity edits changed the bytes");
            byteIdentical++;

            // SetBoneBind through the conversion (the same rotation with the other sign is not bitwise equal).
            var skeleton = Skeleton.FromFile(mesh);
            for (int b = 0; b < mesh.Bones.Length; b++)
            {
                var stored = mesh.Bones[b];
                foreach (var space in new[] { BindSpace.World, BindSpace.Local })
                {
                    var bind = space == BindSpace.World ? skeleton.RestWorld[b] : skeleton.RestLocal[b];
                    Assert.Same(mesh, MeshEdit.SetBoneBind(mesh, b, bind, space));
                    var set = MeshEdit.SetBoneBind(mesh, b, bind with { Rotation = Quat.Negate(bind.Rotation) }, space).Bones[b];
                    var expectedRot = Quat.Normalize(stored.Rotation);
                    float tolerance = 1e-5f * Math.Max(1f, stored.Position.Length());
                    double posError = Vector3.Distance(stored.Position, set.Position);
                    double rotError = new Vector4(set.Rotation.X - expectedRot.X, set.Rotation.Y - expectedRot.Y,
                        set.Rotation.Z - expectedRot.Z, set.Rotation.W - expectedRot.W).Length();
                    maxBindError = Math.Max(maxBindError, Math.Max(posError / Math.Max(1f, stored.Position.Length()), rotError));
                    Assert.True(posError <= tolerance, $"{name} bone {b} {space}: position {stored.Position} -> {set.Position}");
                    Assert.True(rotError <= 1e-5, $"{name} bone {b} {space}: rotation {expectedRot} -> {set.Rotation}");
                    Assert.Equal(stored.Name, set.Name);
                    Assert.Equal(stored.ParentIndex, set.ParentIndex);
                    bindChecks++;
                }
            }

            // A non-trivial reorder is lossless: reversing and reversing back restores every byte.
            int n = mesh.Bones.Length;
            if (n > 1)
            {
                var reversed = MeshEdit.ReorderBones(mesh, [.. Enumerable.Range(0, n).Reverse()]);
                Assert.NotSame(mesh, reversed.Mesh);
                // Writes, reads back to the same bytes, and adds no lint errors (a cheaper form of
                // MeshEditTests.AssertValidEdit, whose reflection walk is slow on big meshes).
                byte[] written = V3dWriter.Write(reversed.Mesh);
                Assert.True(written.AsSpan().SequenceEqual(V3dWriter.Write(V3dReader.Read(written, name))), $"{name}: reordered mesh does not read back");
                MeshEditTests.AssertNoNewErrors(mesh, reversed.Mesh);
                var back = MeshEdit.ReorderBones(reversed.Mesh, reversed.OldToNew);
                Assert.True(bytes.AsSpan().SequenceEqual(V3dWriter.Write(back.Mesh)), $"{name}: reorder there and back changed the bytes");
                reordersRestored++;
            }
        }
        output.WriteLine($"{characters} .v3c + {statics} .v3m: {byteIdentical} byte-identical after identity edits; "
            + $"{reordersRestored} reversed bone orders restored byte for byte; {bindChecks} bind conversions, max relative error {maxBindError:E2}");
        Assert.Equal(95, characters);
        Assert.Equal(427, statics);
    }

    /// <summary>Every identity edit MeshEdit offers, chained; each must return its input instance.</summary>
    private static V3dFile IdentityEdits(V3dFile mesh)
    {
        var r = mesh;
        V3dFile Check(V3dFile edited)
        {
            Assert.Same(r, edited);
            return edited;
        }

        for (int b = 0; b < mesh.Bones.Length; b++)
        {
            r = Check(MeshEdit.RenameBone(r, b, mesh.Bones[b].Name.Text));
            r = Check(MeshEdit.ReparentBone(r, b, mesh.Bones[b].ParentIndex));
        }
        r = Check(MeshEdit.ReorderBones(r, [.. Enumerable.Range(0, mesh.Bones.Length)]).Mesh);

        int si = 0;
        foreach (var sub in mesh.Submeshes)
        {
            r = Check(MeshEdit.RenameSubmesh(r, si, sub.Name.Text));
            r = Check(MeshEdit.SetLodDistances(r, si, [.. sub.LodDistances]));
            for (int mi = 0; mi < sub.Materials.Length; mi++)
            {
                r = Check(MeshEdit.SetMaterial(r, si, mi, sub.Materials[mi] with { }));
                r = Check(MeshEdit.SetTextureName(r, si, mi, sub.Materials[mi].DiffuseMap.Text));
            }
            si++;
        }

        int ci = 0;
        foreach (var s in mesh.CollisionSpheres)
        {
            r = Check(MeshEdit.SetCollisionSphere(r, ci++, s.Name.Text, s.BoneIndex, s.Position, s.Radius));
        }

        if (mesh.Submeshes.FirstOrDefault() is { Lods.Length: > 0 } first)
        {
            var props = first.Lods[0].PropPoints;
            Assert.Equal(props.Length, MeshEdit.PropPointCount(mesh));
            for (int pi = 0; pi < props.Length; pi++)
                r = Check(MeshEdit.SetPropPoint(r, pi, props[pi].Name.Text, props[pi].ParentIndex, props[pi].Rotation, props[pi].Position));
        }
        return r;
    }

    [Fact]
    public void ReorderAndConformKeepTheSkinnedStockCharacter()
    {
        if (TestPaths.CorpusFile("ult2_guard.v3c") is not { } meshPath || TestPaths.CorpusFile("ult2_stand.rfa") is not { } clipPath) return;
        var mesh = V3dReader.ReadFile(meshPath);
        var clip = RfaReader.ReadFile(clipPath);
        int n = mesh.Bones.Length;
        // Odd indices first (reversed), then even ones: parents move after and before their children.
        int[] order = [.. Enumerable.Range(0, n).Where(i => i % 2 == 1).Reverse(), .. Enumerable.Range(0, n).Where(i => i % 2 == 0)];
        int vertices = AssertReorderKeepsSkinning(mesh, clip, order);
        output.WriteLine($"ult2_guard: {n} bones reordered, {vertices} LOD 0 vertices skinned identically at 7 times");
    }

    /// <summary>
    /// Reorders <paramref name="mesh"/>'s bones, conforms <paramref name="clip"/> to the new order by name,
    /// and asserts that every LOD 0 batch skins to bit-identical positions and normals at several
    /// times. Returns the number of vertices compared per time.
    /// </summary>
    internal static int AssertReorderKeepsSkinning(V3dFile mesh, RfaClip clip, int[] order)
    {
        var skeleton = Skeleton.FromFile(mesh);
        var reordered = MeshEdit.ReorderBones(mesh, order);
        Assert.NotSame(mesh, reordered.Mesh);
        var target = Skeleton.FromFile(reordered.Mesh);
        var conformed = ClipEdit.ConformToSkeleton(clip, skeleton.Names, target);
        Assert.Empty(conformed.DroppedBones);
        Assert.Empty(conformed.AddedBones);
        Assert.Equal(order, conformed.SourceOfTarget.ToArray());

        var poseA = new Pose(skeleton);
        var poseB = new Pose(target);
        var skinA = new Matrix4x4[skeleton.Count];
        var skinB = new Matrix4x4[target.Count];
        int compared = 0;
        for (int step = 0; step < 7; step++)
        {
            float time = clip.StartTime + (clip.EndTime - clip.StartTime) * step / 6f + (step % 2) * 13;
            poseA.Sample(clip, time);
            poseB.Sample(conformed.Clip, time);
            Skinning.ComputeSkinMatrices(skeleton, poseA.World, skinA);
            Skinning.ComputeSkinMatrices(target, poseB.World, skinB);
            compared = 0;
            var subsB = reordered.Mesh.Submeshes.ToArray();
            int si = 0;
            foreach (var subA in mesh.Submeshes)
            {
                var lodA = subA.Lods[0];
                var lodB = subsB[si++].Lods[0];
                for (int bi = 0; bi < lodA.Batches.Length; bi++)
                {
                    var a = lodA.Batches[bi];
                    var b = lodB.Batches[bi];
                    int nv = a.VertexCount;
                    var posA = new Vector3[nv];
                    var posB = new Vector3[nv];
                    var nrmA = new Vector3[nv];
                    var nrmB = new Vector3[nv];
                    Skinning.Skin(a.Positions.AsSpan(), a.Normals.AsSpan(), a.BoneLinks.AsSpan(), skinA, posA, nrmA);
                    Skinning.Skin(b.Positions.AsSpan(), b.Normals.AsSpan(), b.BoneLinks.AsSpan(), skinB, posB, nrmB);
                    for (int v = 0; v < nv; v++)
                    {
                        Assert.True(Bits(posA[v]) == Bits(posB[v]), $"time {time}, submesh {si - 1} batch {bi} vertex {v}: {posA[v]} vs {posB[v]}");
                        Assert.True(Bits(nrmA[v]) == Bits(nrmB[v]), $"time {time}, submesh {si - 1} batch {bi} normal {v}: {nrmA[v]} vs {nrmB[v]}");
                    }
                    compared += nv;
                }
            }
        }
        Assert.True(compared > 0);
        return compared;

        static (int, int, int) Bits(Vector3 v) =>
            (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));
    }
}
