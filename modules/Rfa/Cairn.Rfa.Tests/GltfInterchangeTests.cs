using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Editing;
using Cairn.Formats.Gltf;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

public class GltfInterchangeTests(ITestOutputHelper output)
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Largest local rotation (degrees) and local / world position differences over the clip, every 160 ticks.</summary>
    internal static (float Rot, float Pos, float World) PoseError(Skeleton sk, RfaClip a, RfaClip b)
    {
        var pa = new Pose(sk);
        var pb = new Pose(sk);
        float rot = 0, pos = 0, world = 0;
        for (int t = a.StartTime; ; t = Math.Min(t + 160, a.EndTime))
        {
            pa.Sample(a, t);
            pb.Sample(b, t);
            for (int i = 0; i < sk.Count; i++)
            {
                rot = MathF.Max(rot, Quat.AngleDegrees(pa.Local[i].Rotation, pb.Local[i].Rotation));
                pos = MathF.Max(pos, (pa.Local[i].Position - pb.Local[i].Position).Length());
                world = MathF.Max(world, (pa.World[i].Position - pb.World[i].Position).Length());
            }
            if (t >= a.EndTime) break;
        }
        return (rot, pos, world);
    }

    private static GltfDocument RoundTripGlb(GltfDocument doc) => GltfReader.Read(GltfWriter.ToGlb(doc), "test.glb", null);

    private static V3dFile SampleMesh() => V3dBuilder.Build(V3dBuilderTests.SampleDescription());

    /// <summary>A clip for the sample mesh's two bones: eased rotations, Bezier positions, plus a bone with no rotation keys.</summary>
    private static RfaClip SampleClip()
    {
        var clip = EditingTestClips.Make(2);
        return clip with { Bones = clip.Bones.SetItem(1, clip.Bones[1] with { RotationKeys = [] }) };
    }

    private static void AssertSameMeshData(V3dFile expected, V3dFile actual, float tolerance = 1e-5f)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        var es = expected.Submeshes.ToList();
        var @as = actual.Submeshes.ToList();
        Assert.Equal(es.Count, @as.Count);
        for (int s = 0; s < es.Count; s++)
        {
            Assert.Equal(es[s].Name.Text, @as[s].Name.Text);
            Assert.Equal(es[s].LodDistances.ToArray(), @as[s].LodDistances.ToArray());
            Assert.Null(ModelAssert.Diff(es[s].Materials, @as[s].Materials, "materials"));
            Assert.Equal(es[s].Lods.Length, @as[s].Lods.Length);
            for (int l = 0; l < es[s].Lods.Length; l++)
            {
                var el = es[s].Lods[l];
                var al = @as[s].Lods[l];
                Assert.Equal(el.Flags, al.Flags);
                Assert.Equal(el.Textures.Select(t => (t.MaterialIndex, t.FileName)), al.Textures.Select(t => (t.MaterialIndex, t.FileName)));
                Assert.Equal(el.Batches.Length, al.Batches.Length);
                for (int k = 0; k < el.Batches.Length; k++)
                {
                    var eb = el.Batches[k];
                    var ab = al.Batches[k];
                    Assert.Equal(eb.VertexCount, ab.VertexCount);
                    Assert.Equal(eb.TextureIndex, ab.TextureIndex);
                    Assert.Equal(eb.RenderFlags, ab.RenderFlags);
                    for (int v = 0; v < eb.VertexCount; v++)
                    {
                        Assert.True((eb.Positions[v] - ab.Positions[v]).Length() <= tolerance * MathF.Max(1, eb.Positions[v].Length()), $"position {v}");
                        var en = eb.Normals[v];
                        if (float.IsFinite(en.Length()) && en.Length() > 0.5f) Assert.True((Vector3.Normalize(en) - ab.Normals[v]).Length() <= 1e-4f, $"normal {v}");
                        Assert.Equal(eb.TexCoords[v], ab.TexCoords[v]);
                        if (expected.Kind == V3dKind.Character) Assert.Equal(eb.BoneLinks[v], ab.BoneLinks[v]);
                    }
                    // Triangle order inside a batch mixing single- and double-sided triangles is not kept (two primitives, as in REDUX).
                    Assert.Equal(eb.Triangles.OrderBy(t => (t.A, t.B, t.C)), ab.Triangles.OrderBy(t => (t.A, t.B, t.C)));
                }
            }
        }
        Assert.Equal(expected.Bones.Length, actual.Bones.Length);
        var ek = Skeleton.FromFile(expected);
        var ak = Skeleton.FromFile(actual);
        for (int i = 0; i < ek.Count; i++)
        {
            Assert.Equal(ek.Names[i], ak.Names[i]);
            Assert.Equal(ek.EffectiveParents[i], ak.EffectiveParents[i]);
            Assert.True(Quat.AngleDegrees(ek.RestWorld[i].Rotation, ak.RestWorld[i].Rotation) < 0.01f, $"bone {i} rotation");
            Assert.True((ek.RestWorld[i].Position - ak.RestWorld[i].Position).Length() < 1e-4f, $"bone {i} position");
        }
        var ec = expected.CollisionSpheres.ToList();
        var ac = actual.CollisionSpheres.ToList();
        Assert.Equal(ec.Count, ac.Count);
        for (int i = 0; i < ec.Count; i++)
        {
            Assert.Equal(ec[i].Name.Text, ac[i].Name.Text);
            Assert.Equal(ec[i].BoneIndex, ac[i].BoneIndex);
            Assert.True((ec[i].Position - ac[i].Position).Length() < 1e-5f);
            Assert.Equal(ec[i].Radius, ac[i].Radius, 5);
        }
        var ep = es.Count > 0 && es[0].Lods.Length > 0 ? es[0].Lods[0].PropPoints : [];
        var ap = @as.Count > 0 && @as[0].Lods.Length > 0 ? @as[0].Lods[0].PropPoints : [];
        Assert.Equal(ep.Length, ap.Length);
        for (int i = 0; i < ep.Length; i++)
        {
            Assert.Equal(ep[i].Name.Text, ap[i].Name.Text);
            Assert.Equal(ep[i].ParentIndex, ap[i].ParentIndex);
            Assert.True((ep[i].Position - ap[i].Position).Length() < 1e-5f);
            Assert.True(Quat.AngleDegrees(Quat.Normalize(ep[i].Rotation), Quat.Normalize(ap[i].Rotation)) < 0.01f || ep[i].Rotation.LengthSquared() < 1e-8f);
        }
    }

    private static void StripRfExtras(GltfDocument doc)
    {
        foreach (var a in doc.Animations)
        {
            a.Extras = null;
            foreach (var s in a.Samplers) s.Extras = null;
            foreach (var c in a.Channels) c.Extras = null;
        }
    }

    // ── Synthetic ───────────────────────────────────────────────────────────

    [Fact]
    public void ExportFollowsReduxConventions()
    {
        var mesh = SampleMesh();
        var result = GltfExport.Export(mesh, [new GltfExportClip("walk", SampleClip())]);
        var doc = result.Document;
        Assert.Equal("root__rfbi0", doc.Nodes[0].Name);
        Assert.Equal("upper__rfbi1", doc.Nodes[1].Name);
        Assert.True(GltfExtras.TryGetString(doc.Nodes[1].Extras, GltfExtras.Type, out var type) && type == "bone");
        Assert.Contains(1, doc.Nodes[0].Children);
        Assert.Single(doc.Skins);
        Assert.Contains(doc.Nodes, n => n.Name == "body_LOD0" && n.Mesh is not null && n.Skin == 0);
        Assert.Contains(doc.Nodes, n => n.Name == "body_LOD1");
        var sphere = doc.Nodes.Single(n => n.Name == "rf_csphere::head");
        Assert.Contains(doc.Nodes.IndexOf(sphere), doc.Nodes[1].Children);
        Assert.Equal(new Vector3(0.25f), sphere.Scale);
        Assert.Contains(doc.Nodes.IndexOf(doc.Nodes.Single(n => n.Name == "rf_prop::hand")), doc.Nodes[1].Children);
        // X mirrored: the bone at RF (0, 1, 0) stays at (0, 1, 0); the flap vertex at RF x = 2 is at glTF x = -2.
        Assert.Equal(new Vector3(0, 1, 0), doc.Nodes[1].Translation);
        var anim = doc.Animations.Single();
        Assert.Equal("walk", anim.Name);
        Assert.True(GltfExtras.TryGetInt(anim.Extras, GltfExtras.StartTime, out int start) && start == EditingTestClips.Start);
        Assert.True(GltfExtras.TryGetInt(anim.Extras, GltfExtras.Version, out int version) && version == 8);
        Assert.Equal(4, anim.Channels.Count);
        Assert.Equal(GltfInterpolation.CubicSpline, anim.Samplers[anim.Channels[1].Sampler].Interpolation);
        Assert.Equal(1, result.BakedRotationTracks); // bone 0 has eases, bone 1 has no rotation keys
        Assert.Empty(result.MorphOmittedClips);
        // REDUX's importer reads buffers[0] by URI: a .gltf export needs exactly one buffer.
        Assert.Single(doc.Buffers);
    }

    [Fact]
    public void ExportImportRestoresMeshAndClipExactly()
    {
        var mesh = SampleMesh();
        var clip = SampleClip();
        var doc = RoundTripGlb(GltfExport.Export(mesh, [new GltfExportClip("walk", clip)]).Document);

        var imported = GltfMeshImport.Import(doc);
        Assert.False(imported.HasErrors, string.Join("; ", imported.Issues));
        Assert.NotNull(imported.Mesh);
        AssertSameMeshData(mesh, imported.Mesh!);
        Assert.DoesNotContain(MeshLinter.Analyze(imported.Mesh!), d => d.Severity == DiagnosticSeverity.Error);

        var clips = GltfAnimationImport.Import(doc, Skeleton.FromFile(mesh));
        var back = Assert.Single(clips);
        Assert.All(back.Report.Bones, b => Assert.Equal(GltfBoneImportMode.Restored, b.Mode));
        Assert.Equal(RfaWriter.Write(clip), RfaWriter.Write(back.Clip));
    }

    [Fact]
    public void WithoutRfExtrasTheSampledPoseStillMatches()
    {
        var mesh = SampleMesh();
        var sk = Skeleton.FromFile(mesh);
        var clip = SampleClip();
        var doc = RoundTripGlb(GltfExport.Export(mesh, [new GltfExportClip("walk", clip)]).Document);
        StripRfExtras(doc);
        var back = GltfAnimationImport.Import(doc, sk, new GltfAnimationImportOptions { DefaultWeight = 3f }).Single();
        Assert.False(back.Report.UsedRfExtras);
        // The first glTF key (0.0333 s) lands on tick 160, as stock clips start.
        Assert.Equal(EditingTestClips.Start, back.Clip.StartTime);
        Assert.Equal(EditingTestClips.End, back.Clip.EndTime);
        Assert.All(back.Clip.Bones, b => Assert.Equal(3f, b.Weight));
        var (rot, pos, _) = PoseError(sk, clip, back.Clip);
        Assert.True(rot < 0.5f, $"rotation error {rot}");   // eased segments are baked at 30 fps and re-slerped
        Assert.True(pos < 1e-5f, $"position error {pos}");
        Assert.All(back.Clip.Bones.SelectMany(b => b.RotationKeys), k => Assert.True(k.X * k.X + k.Y * k.Y + k.Z * k.Z + (long)k.W * k.W <= 16383L * 16383L));
    }

    [Fact]
    public void ALinearClipComesBackKeyForKeyEvenWithoutKeyExtras()
    {
        var mesh = SampleMesh();
        var sk = Skeleton.FromFile(mesh);
        var clip = EditingTestClips.Make(2);
        clip = clip with { Bones = [.. clip.Bones.Select(b => b with { RotationKeys = [.. b.RotationKeys.Select(k => k with { EaseIn = 0, EaseOut = 0 })] })] };
        var doc = RoundTripGlb(GltfExport.Export(mesh, [new GltfExportClip("walk", clip)]).Document);
        foreach (var s in doc.Animations[0].Samplers) s.Extras = null; // keep the header extras only
        var back = GltfAnimationImport.Import(doc, sk).Single();
        Assert.All(back.Report.Bones, b => Assert.Equal(GltfBoneImportMode.Keys, b.Mode));
        for (int i = 0; i < clip.BoneCount; i++)
        {
            Assert.Equal(clip.Bones[i].RotationKeys.Select(k => k.Time), back.Clip.Bones[i].RotationKeys.Select(k => k.Time));
            Assert.Equal(clip.Bones[i].PositionKeys.Select(k => k.Time), back.Clip.Bones[i].PositionKeys.Select(k => k.Time));
            for (int k = 0; k < clip.Bones[i].RotationKeys.Length; k++)
            {
                var a = clip.Bones[i].RotationKeys[k];
                var b = back.Clip.Bones[i].RotationKeys[k];
                Assert.True(Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Z - b.Z) <= 2 && Math.Abs(a.W - b.W) <= 2);
            }
            for (int k = 0; k < clip.Bones[i].PositionKeys.Length; k++)
            {
                var a = clip.Bones[i].PositionKeys[k];
                var b = back.Clip.Bones[i].PositionKeys[k];
                Assert.True((a.Position - b.Position).Length() < 1e-6f && (a.InControl - b.InControl).Length() < 1e-5f && (a.OutControl - b.OutControl).Length() < 1e-5f);
            }
        }
    }

    [Fact]
    public void ARigWithOtherBoneOrientationsIsResampledInModelSpace()
    {
        // The glTF rig has the same joints but every rest frame rotated 90 degrees about its own axis
        // (as an exporter that re-orients bones would write); animation is authored against it.
        var mesh = SampleMesh();
        var sk = Skeleton.FromFile(mesh);
        var clip = EditingTestClips.Make(2);
        clip = clip with { Bones = [.. clip.Bones.Select(b => b with { RotationKeys = [.. b.RotationKeys.Select(k => k with { EaseIn = 0, EaseOut = 0 })] })] };
        var doc = GltfExport.Export(mesh, [new GltfExportClip("walk", clip)]).Document;
        StripRfExtras(doc);

        // Re-orient joint frames: node local' = local * R (child translations compensated), keys likewise.
        var r = Quat.FromAxisAngle(Vector3.Normalize(new Vector3(1, 2, 3)), MathF.PI / 2);
        var rInv = Quat.Conj(r);
        var anim = doc.Animations[0];
        int[] joints = [0, 1];
        foreach (int j in joints)
        {
            var node = doc.Nodes[j];
            node.Rotation = j == 1 ? Quat.Mul(rInv, Quat.Mul(node.Rotation!.Value, r)) : Quat.Mul(node.Rotation!.Value, r);
            if (j == 1) node.Translation = Quat.Rotate(rInv, node.Translation!.Value); // parent frame rotated by r
        }
        foreach (var ch in anim.Channels)
        {
            var sampler = anim.Samplers[ch.Sampler];
            var acc = doc.Accessors[sampler.Output];
            if (ch.Target.Path == "rotation")
            {
                var q = GltfAccessorReader.ReadQuaternions(doc, sampler.Output);
                var replaced = new GltfBufferBuilder(doc);
                sampler.Output = replaced.AddQuaternions([.. q.Select(x => Quat.Mul(ch.Target.Node == 1 ? Quat.Mul(rInv, x) : x, r))]);
                replaced.Finish();
            }
            else if (ch.Target.Node == 1)
            {
                var v = GltfAccessorReader.ReadVector3(doc, sampler.Output);
                var replaced = new GltfBufferBuilder(doc);
                sampler.Output = replaced.AddVector3([.. v.Select(x => Quat.Rotate(rInv, x))], target: null);
                replaced.Finish();
            }
            _ = acc;
        }
        doc = RoundTripGlb(doc);
        var back = GltfAnimationImport.Import(doc, sk).Single();
        Assert.All(back.Report.Bones, b => Assert.Equal(GltfBoneImportMode.Resampled, b.Mode));
        var (rot, _, world) = PoseError(sk, clip, back.Clip);
        Assert.True(world < 1e-4f, $"world position error {world}");
        Assert.True(rot < 0.6f, $"rotation error {rot}");
    }

    [Fact]
    public void AReparentedTargetBoneIsResampledAndKeepsItsWorldMotion()
    {
        // Source rig: root -> a -> b. Target: the same bones with b hanging off root (MeshEdit.ReparentBone).
        var d = V3dBuilderTests.SampleDescription();
        d = d with
        {
            Bones =
            [
                V3dBuilder.BoneFromRestWorld("root", -1, Rigid.Identity),
                V3dBuilder.BoneFromRestWorld("a", 0, new Rigid(Quat.FromAxisAngle(Vector3.UnitZ, 0.3f), new Vector3(0, 1, 0))),
                V3dBuilder.BoneFromRestWorld("b", 1, new Rigid(Quaternion.Identity, new Vector3(0.2f, 1.8f, 0))),
            ],
        };
        var source = V3dBuilder.Build(d);
        var target = MeshEdit.ReparentBone(source, 2, 0);
        var clip = EditingTestClips.Make(3);
        clip = clip with { Bones = [.. clip.Bones.Select(b => b with { RotationKeys = [.. b.RotationKeys.Select(k => k with { EaseIn = 0, EaseOut = 0 })] })] };
        var doc = RoundTripGlb(GltfExport.Export(source, [new GltfExportClip("walk", clip)]).Document);
        var tk = Skeleton.FromFile(target);
        var back = GltfAnimationImport.Import(doc, tk).Single();
        Assert.Equal(GltfBoneImportMode.Restored, back.Report.Bones[0].Mode);
        Assert.Equal(GltfBoneImportMode.Resampled, back.Report.Bones[2].Mode);

        var sk = Skeleton.FromFile(source);
        var ps = new Pose(sk);
        var pt = new Pose(tk);
        float world = 0, rot = 0;
        for (int t = clip.StartTime; t <= clip.EndTime; t += 160)
        {
            ps.Sample(clip, t);
            pt.Sample(back.Clip, t);
            for (int i = 0; i < 3; i++)
            {
                world = MathF.Max(world, (ps.World[i].Position - pt.World[i].Position).Length());
                rot = MathF.Max(rot, Quat.AngleDegrees(ps.World[i].Rotation, pt.World[i].Rotation));
            }
        }
        Assert.True(world < 1e-4f && rot < 0.05f, $"world position {world}, rotation {rot}");
    }

    [Fact]
    public void UnmappedBonesHoldTheReferencePoseOrTheBind()
    {
        var mesh = SampleMesh();
        var sk = Skeleton.FromFile(mesh);
        var clip = SampleClip();
        var doc = GltfExport.Export(mesh, [new GltfExportClip("walk", clip)]).Document;
        doc.Nodes[1].Name = "something_else";
        var map = GltfAnimationImport.MapBones(doc, sk, new Retarget.BoneMapOptions { Fuzzy = false });
        Assert.Equal(-1, map.SourceOf(1));
        var bind = GltfAnimationImport.Import(doc, sk, new GltfAnimationImportOptions { BoneMap = map }).Single();
        Assert.Equal(GltfBoneImportMode.RestPose, bind.Report.Bones[1].Mode);
        Assert.True(Quat.AngleDegrees(ClipSampler.SampleRotation(bind.Clip.Bones[1].RotationKeys.AsSpan(), 1000), sk.RestLocal[1].Rotation) < 0.01f);
        var reference = GltfAnimationImport.Import(doc, sk, new GltfAnimationImportOptions { BoneMap = map, ReferenceClip = clip }).Single();
        Assert.Equal(GltfBoneImportMode.ReferencePose, reference.Report.Bones[1].Mode);
        Assert.Equal(clip.Bones[1].PositionKeys[0].Position, reference.Clip.Bones[1].PositionKeys[0].Position);
        Assert.Contains("something_else", reference.Report.UnusedNodes);
    }

    [Fact]
    public void KeyReductionAppliesToConvertedTracks()
    {
        var mesh = SampleMesh();
        var sk = Skeleton.FromFile(mesh);
        var clip = ClipEdit.Resample(EditingTestClips.Make(2), 160);
        var doc = GltfExport.Export(mesh, [new GltfExportClip("dense", clip)]).Document;
        StripRfExtras(doc);
        var plain = GltfAnimationImport.Import(doc, sk).Single();
        var reduced = GltfAnimationImport.Import(doc, sk, new GltfAnimationImportOptions { Reduce = new ReduceOptions { RotationToleranceDegrees = 0.2f } }).Single();
        Assert.True(reduced.Report.RotationKeys < plain.Report.RotationKeys);
        Assert.True(PoseError(sk, plain.Clip, reduced.Clip).Rot < 0.3f);
    }

    [Fact]
    public void StaticMeshesRoundTripWithTheirSubmeshOffset()
    {
        var d = V3dBuilderTests.SampleDescription() with { Kind = V3dKind.StaticMesh, Bones = [], CollisionSpheres = [] };
        d = d with { PropPoints = [d.PropPoints[0] with { ParentIndex = -1 }], Submeshes = [d.Submeshes[0] with { Offset = new Vector3(1, 2, 3) }] };
        var mesh = V3dBuilder.Build(d);
        var doc = RoundTripGlb(GltfExport.Export(mesh).Document);
        var back = GltfMeshImport.Import(doc);
        Assert.False(back.HasErrors, string.Join("; ", back.Issues));
        AssertSameMeshData(mesh, back.Mesh!);
        Assert.Equal(new Vector3(1, 2, 3), back.Mesh!.Submeshes.Single().Offset);
    }

    [Fact]
    public void TexturesAreDecodedToPngWhenTheResolverFindsThem()
    {
        using var temp = new TempFolder();
        var image = TestImages.Tga24(4, 4, 10, 20, 30);
        temp.Write("skin.tga", image);
        var resolver = new Cairn.Assets.AssetResolver(new Cairn.Assets.AssetResolverOptions { DocumentFolder = temp.Path });
        var result = GltfExport.Export(SampleMesh(), null, new GltfExportOptions { TextureResolver = resolver });
        var img = Assert.Single(result.Document.Images);
        Assert.Equal("skin.png", img.Uri);
        Assert.Equal(0x89, img.Data![0]);
        Assert.Equal(["flap.tga"], result.MissingTextures.ToArray());
        Assert.Contains(result.Document.Materials, m => m.Name == "flap.tga" && m.PbrMetallicRoughness?.BaseColorTexture is null);

        string path = temp.File("out.gltf");
        GltfWriter.WriteGltf(result.Document, path);
        Assert.True(File.Exists(temp.File("skin.png")));
        Assert.True(File.Exists(temp.File("out.bin")));
        var decoded = Cairn.Formats.Imaging.ImageDecoder.DecodeFile(temp.File("skin.png"));
        Assert.Equal(4, decoded.Width);
    }

    [Fact]
    public void PreflightReportsLimitsAndConversions()
    {
        // A plain glTF: one unskinned mesh with 4 LODs by name, a long bone-less name, no normals.
        var doc = new GltfDocument();
        var b = new GltfBufferBuilder(doc);
        int pos = b.AddVector3([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], minMax: true);
        int idx = b.AddIndices([0, 1, 2]);
        b.Finish();
        for (int l = 0; l < 4; l++)
        {
            var m = new GltfMesh();
            var prim = new GltfPrimitive { Indices = idx };
            prim.Attributes["POSITION"] = pos;
            m.Primitives.Add(prim);
            doc.Meshes.Add(m);
            doc.Nodes.Add(new GltfNode { Name = $"crate_LOD{l}", Mesh = l });
        }
        var result = GltfMeshImport.Import(doc);
        Assert.Contains(result.Issues, i => i.Code == "MI003" && i.Severity == MeshImportSeverity.Error);
        Assert.Contains(result.Issues, i => i.Code == "MI009");
        Assert.Contains(result.Issues, i => i.Code == "MI010");
        Assert.NotNull(result.Mesh);
        Assert.Equal(V3dKind.StaticMesh, result.Mesh!.Kind);
        Assert.Equal(4, result.Mesh.Submeshes.Single().Lods.Length);
        Assert.Equal("default.tga", result.Mesh.Submeshes.Single().Materials.Single().DiffuseMap.Text);

        doc.Nodes[0].Name = new string('x', 30) + "_LOD0";
        var tooLong = GltfMeshImport.Import(doc);
        Assert.Null(tooLong.Mesh);
        Assert.Contains(tooLong.Issues, i => i.Code == "MI002");
    }

    [Fact]
    public void KeepSkeletonReplacesGeometryOnly()
    {
        var mesh = SampleMesh();
        var doc = GltfExport.Export(mesh).Document;
        foreach (var n in doc.Nodes.Where(n => n.Name!.StartsWith("rf_", StringComparison.Ordinal))) n.Name = "helper";
        var result = GltfMeshImport.Import(doc, new GltfMeshImportOptions { KeepSkeletonFrom = mesh });
        Assert.Contains(result.Issues, i => i.Code == "MI012");
        Assert.Equal(mesh.Bones, result.Mesh!.Bones);
        Assert.Null(ModelAssert.Diff(mesh.CollisionSpheres.ToList(), result.Mesh.CollisionSpheres.ToList(), "spheres"));
    }

    [Fact]
    public void ClipsOnAStaticMeshAndMorphDataAreReported()
    {
        var clip = EditingTestClips.Make(2) with { Version = 7, Morph = EditingTestClips.MakeV7Morph() };
        var result = GltfExport.Export(SampleMesh(), [new GltfExportClip("talk", clip)]);
        Assert.Equal(["talk"], result.MorphOmittedClips.ToArray());
        Assert.Contains(result.Warnings, w => w.Contains("morph", StringComparison.OrdinalIgnoreCase));
    }

    // ── Corpus ──────────────────────────────────────────────────────────────

    [Fact]
    public void StockCharactersAndClipsSurviveExportAndImport()
    {
        if (TestPaths.Corpus is null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // One representative mesh per bone count (the clip <-> mesh contract), plus named rigs.
        var meshes = new Dictionary<int, string>();
        foreach (string path in Directory.EnumerateFiles(TestPaths.Corpus, "*.v3c").Order(StringComparer.OrdinalIgnoreCase))
        {
            var probe = V3dReader.ReadFile(path);
            meshes.TryAdd(probe.Bones.Length, path);
        }
        var allClips = Directory.EnumerateFiles(TestPaths.Corpus, "*.rfa").Order(StringComparer.OrdinalIgnoreCase)
            .Select(p => (Path: p, Clip: RfaReader.ReadFile(p))).ToList();
        var clipsByCount = allClips.GroupBy(c => c.Clip.BoneCount).ToDictionary(g => g.Key, g => g.ToList());
        var entries = meshes.Where(kv => kv.Key > 0 && clipsByCount.ContainsKey(kv.Key)).Select(kv => (Mesh: kv.Value, Clips: clipsByCount[kv.Key])).ToList();
        // The four stock humanoid rigs (A, female, merc with its reparented spine03, civilian) with their own clips.
        foreach (var (meshName, prefix) in new[] { ("ult2_guard.v3c", "ult2_"), ("nurse1.v3c", "mnr3f_"), ("merc_grunt.v3c", "mrc2_"), ("tech01.v3c", "tech_") })
        {
            if (TestPaths.CorpusFile(meshName) is not { } p) continue;
            int bones = V3dReader.ReadFile(p).Bones.Length;
            var own = allClips.Where(c => Path.GetFileName(c.Path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && c.Clip.BoneCount == bones).ToList();
            if (own.Count > 0) entries.Add((p, own));
        }

        float worstRestored = 0, worstStripped = 0, worstStrippedWorld = 0, worstStrippedPos = 0;
        int exact = 0, total = 0, families = 0, meshCount = 0;
        foreach (var (meshPath, candidates) in entries)
        {
            var mesh = V3dReader.ReadFile(meshPath);
            var sk = Skeleton.FromFile(mesh);
            families++;
            // Several clips: an eased one, one with multi-key non-root positions, and the first two.
            var chosen = new List<(string Path, RfaClip Clip)>();
            void Pick((string Path, RfaClip Clip)? c) { if (c is { } x && !chosen.Contains(x)) chosen.Add(x); }
            Pick(candidates.FirstOrDefault(c => c.Clip.Bones.Any(b => b.RotationKeys.Any(k => k.EaseIn != 0 || k.EaseOut != 0))) is { Path: not null } e ? e : null);
            Pick(candidates.FirstOrDefault(c => c.Clip.Bones.Skip(1).Any(b => b.PositionKeys.Length > 2)) is { Path: not null } m ? m : null);
            foreach (var c in candidates.Take(2)) Pick(c);

            var export = GltfExport.Export(mesh, [.. chosen.Select(c => new GltfExportClip(Path.GetFileNameWithoutExtension(c.Path), c.Clip))]);
            var doc = RoundTripGlb(export.Document);

            var meshBack = GltfMeshImport.Import(doc);
            Assert.False(meshBack.HasErrors, $"{Path.GetFileName(meshPath)}: {string.Join("; ", meshBack.Issues.Where(i => i.Severity == MeshImportSeverity.Error))}");
            AssertSameMeshData(mesh, meshBack.Mesh!);
            meshCount++;

            var back = GltfAnimationImport.Import(doc, sk);
            for (int i = 0; i < chosen.Count; i++)
            {
                total++;
                var original = chosen[i].Clip.Morph.IsEmpty ? chosen[i].Clip : ClipEdit.StripMorph(chosen[i].Clip);
                if (RfaWriter.Write(original).AsSpan().SequenceEqual(RfaWriter.Write(back[i].Clip))) exact++;
                else output.WriteLine($"not identical: {Path.GetFileName(chosen[i].Path)} on {Path.GetFileName(meshPath)}: {ModelAssert.Diff(original, back[i].Clip, "clip")} modes [{string.Join(",", back[i].Report.Bones.Select(b => b.Mode.ToString()[0]))}]");
                worstRestored = MathF.Max(worstRestored, PoseError(sk, original, back[i].Clip).Rot);
            }

            StripRfExtras(doc);
            var stripped = GltfAnimationImport.Import(doc, sk);
            for (int i = 0; i < chosen.Count; i++)
            {
                var original = chosen[i].Clip;
                // Without the header extras the first key lands on 160; compare against the clip shifted the same way.
                int first = original.Bones.SelectMany(b => b.RotationKeys.Select(k => k.Time).Concat(b.PositionKeys.Select(k => k.Time))).DefaultIfEmpty(original.StartTime).Min();
                var shifted = ClipEdit.Shift(original, stripped[i].Clip.StartTime - first) with { Morph = RfaMorph.Empty };
                var cmp = shifted with { StartTime = stripped[i].Clip.StartTime, EndTime = Math.Min(shifted.EndTime, stripped[i].Clip.EndTime) };
                var (rot, pos, world) = PoseError(sk, cmp, stripped[i].Clip);
                worstStripped = MathF.Max(worstStripped, rot);
                worstStrippedPos = MathF.Max(worstStrippedPos, pos);
                worstStrippedWorld = MathF.Max(worstStrippedWorld, world);
            }
        }
        output.WriteLine($"{families} skeleton families (bone counts), {meshCount} meshes, {total} clips in {sw.ElapsedMilliseconds} ms: {string.Join(", ", entries.Select(e => Path.GetFileName(e.Mesh)))}");
        output.WriteLine($"with rf_* extras: {exact}/{total} clips byte-identical, worst sampled rotation error {worstRestored:G4} deg");
        output.WriteLine($"extras stripped: worst rotation {worstStripped:G4} deg, local position {worstStrippedPos:G4} m, world position {worstStrippedWorld:G4} m");
        Assert.Equal(total, exact);
        Assert.True(worstStripped < 1f && worstStrippedPos < 1e-4f, $"rotation {worstStripped}, position {worstStrippedPos}");
    }
}
