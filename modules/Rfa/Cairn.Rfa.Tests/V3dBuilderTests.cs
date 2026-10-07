using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Animation;
using Cairn.Formats;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Formats.Maths;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

public class V3dBuilderTests(ITestOutputHelper output)
{
    private static FixedString Name(string text, int length) => FixedString.FromText(text, length);

    private static V3dMaterial Material(string texture) => new(Name(texture, 32), 0f, 0f, 0f, 0f, Name("", 32), 1);

    /// <summary>A two-bone skinned quad with two materials and a second LOD.</summary>
    internal static V3dMeshDescription SampleDescription()
    {
        V3dMeshVertex V(float x, float y, int bone, float u, float v) =>
            new(new Vector3(x, y, 0), -Vector3.UnitZ, new Vector2(u, v), [new V3dBoneInfluence(bone, 1f)]);
        var quad = new V3dMaterialGroup
        {
            Material = 0,
            Vertices = [V(0, 0, 0, 0, 1), V(1, 0, 0, 1, 1), V(1, 1, 1, 1, 0), V(0, 1, 1, 0, 0)],
            Triangles = [new(0, 1, 2), new(0, 2, 3)],
        };
        var flap = new V3dMaterialGroup
        {
            Material = 1,
            Vertices = [V(1, 0, 0, 0, 0), V(2, 0, 0, 1, 0), V(1, 1, 1, 0, 1)],
            Triangles = [new(0, 1, 2, V3dTriangle.DoubleSided)],
        };
        return new V3dMeshDescription
        {
            Kind = V3dKind.Character,
            Bones =
            [
                V3dBuilder.BoneFromRestWorld("root", -1, Rigid.Identity),
                V3dBuilder.BoneFromRestWorld("upper", 0, new Rigid(Quaternion.Identity, new Vector3(0, 1, 0))),
            ],
            CollisionSpheres = [new V3dCollisionSphere(Name("head", 24), 1, new Vector3(0, 0.1f, 0), 0.25f, [])],
            PropPoints = [new V3dPropPoint(Name("hand", 0x44), Quaternion.Identity, new Vector3(0, 0.5f, 0), 1)],
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Name = Name("body", 24),
                    Materials = [Material("skin.tga"), Material("flap.tga")],
                    Lods =
                    [
                        new V3dLodDescription { Distance = 0, Groups = [quad, flap] },
                        new V3dLodDescription { Distance = 10, Groups = [quad] },
                    ],
                },
            ],
        };
    }

    [Fact]
    public void BuildDerivesEveryFieldAndPassesTheWriterAndLinter()
    {
        var file = V3dBuilder.Build(SampleDescription());
        var bytes = V3dWriter.Write(file);
        var read = V3dReader.Read(bytes, "built.v3c");
        ModelAssert.Equal(file, read);
        Assert.DoesNotContain(MeshLinter.Analyze(read), d => d.Severity == DiagnosticSeverity.Error);

        Assert.Equal(V3dHeader.CharacterSignature, file.Header.Signature);
        Assert.Equal(1, file.Header.SubmeshCount);
        Assert.Equal(2, file.Header.TotalMaterials);
        Assert.Equal(1, file.Header.CollisionSphereCount);
        Assert.Equal([typeof(V3dSubmesh), typeof(V3dCollisionSphere), typeof(V3dBoneSection)], file.Sections.Select(s => s.GetType()));

        var sub = file.Submeshes.Single();
        Assert.Equal(new Vector3(0, 0, 0), sub.AabbMin);
        Assert.Equal(new Vector3(2, 1, 0), sub.AabbMax);
        Assert.Equal(2f, sub.Radius);
        Assert.Equal("body", sub.Trailers.Single().Name.Text);
        Assert.Equal([0f, 10f], sub.LodDistances.ToArray());

        var lod0 = sub.Lods[0];
        Assert.Equal(V3dBuilder.DefaultCharacterLodFlags, lod0.Flags);
        Assert.Equal(2, lod0.Batches.Length);
        Assert.Equal(["skin.tga", "flap.tga"], lod0.Textures.Select(t => t.FileName));
        // Distinct positions: (0,0) (1,0) (1,1) (0,1) (2,0) = 5 originals; the flap shares three... two of them.
        Assert.Equal(5, lod0.VertexCount);
        Assert.Equal([0, 1, 2, 3, -1], lod0.Batches[0].MorphMap.Select(x => (int)x));
        Assert.Equal([-1, 0, 2, -1, 1], lod0.Batches[1].MorphMap.Select(x => (int)x));
        Assert.Equal(V3dBatchSizes.Canonical(4, 2, true), lod0.Batches[0].Sizes);
        Assert.Equal(new V3dBoneLink(255, 0, 0, 0, 1, 0xFF, 0xFF, 0xFF), lod0.Batches[0].BoneLinks[2]);
        Assert.Equal(V3dTriangle.DoubleSided, lod0.Batches[1].Triangles[0].Flags);
        Assert.Empty(lod0.Batches[0].Planes);
        Assert.Single(lod0.PropPoints);
        Assert.Single(sub.Lods[1].PropPoints);
        Assert.Equal(["skin.tga"], sub.Lods[1].Textures.Select(t => t.FileName));

        // The bones' rest pose is what BoneFromRestWorld was given.
        var sk = Skeleton.FromFile(file);
        Assert.Equal(new Vector3(0, 1, 0), sk.RestWorld[1].Position);
    }

    [Fact]
    public void StaticMeshesGetPlanesZeroLinksAndNoMorphMap()
    {
        var d = SampleDescription() with { Kind = V3dKind.StaticMesh, Bones = [], CollisionSpheres = [], PropPoints = [] };
        var file = V3dBuilder.Build(d);
        Assert.Equal(V3dHeader.StaticSignature, file.Header.Signature);
        Assert.DoesNotContain(file.Sections, s => s is V3dBoneSection);
        var b = file.Submeshes.Single().Lods[0].Batches[0];
        Assert.Equal(V3dBuilder.DefaultStaticLodFlags, file.Submeshes.Single().Lods[0].Flags);
        Assert.Empty(b.MorphMap);
        Assert.All(b.BoneLinks, l => Assert.Equal(default, l));
        Assert.Equal(2, b.Planes.Length);
        // Counter-clockwise in RF's left-handed space seen from -Z: cross((1,0,0),(1,1,0)) = +Z.
        Assert.Equal(Vector3.UnitZ, b.Planes[0].Normal);
        Assert.Equal(0f, b.Planes[0].Distance);
        V3dReader.Read(V3dWriter.Write(file), "built.v3m");
    }

    [Fact]
    public void SamePositionOffsetsPointAtTheFirstVertexWithThatPosition()
    {
        Vector3[] p = [new(0, 0, 0), new(1, 0, 0), new(0, 0, 0), new(-0f, 0, 0), new(1, 0, 0)];
        Assert.Equal([0, 0, 2, 3, 3], V3dBuilder.SamePositionOffsets(p).Select(x => (int)x));
    }

    [Fact]
    public void DegenerateTrianglesGetNaNPlanesAsInStock()
    {
        Vector3[] p = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)];
        var plane = V3dBuilder.ComputePlanes(p, [new V3dTriangle(0, 1, 2, 0)]).Single();
        Assert.True(float.IsNaN(plane.Normal.X) && float.IsNaN(plane.Distance));
    }

    [Theory]
    [InlineData(new[] { 0.5f, 0.5f }, new byte[] { 128, 127 })]
    [InlineData(new[] { 1f, 1f, 1f }, new byte[] { 85, 85, 85 })]
    [InlineData(new[] { 0.7f, 0.2f, 0.06f, 0.03f, 0.01f }, new byte[] { 180, 52, 15, 8 })]
    [InlineData(new[] { 0.999f, 0.001f }, new byte[] { 255 })]
    public void NormalisedWeightsSumTo255InDescendingOrder(float[] weights, byte[] expected)
    {
        var inf = weights.Select((w, i) => new V3dBoneInfluence(i, w)).ToList();
        var link = V3dBuilder.PackLink(inf, V3dWeightMode.Normalize);
        var bytes = Enumerable.Range(0, 4).Select(link.GetWeight).ToArray();
        Assert.Equal(255, bytes.Sum(b => b));
        Assert.Equal(expected, bytes.Take(expected.Length));
        for (int s = expected.Length; s < 4; s++) Assert.Equal(V3dBoneLink.NoBone, link.GetBone(s));
    }

    [Fact]
    public void DuplicateBonesMergeAndInvalidInfluencesAreDropped()
    {
        var link = V3dBuilder.PackLink([new(3, 0.25f), new(3, 0.25f), new(5, 0.5f), new(-1, 1f), new(7, -1f), new(9, float.NaN)], V3dWeightMode.Normalize);
        Assert.Equal(new V3dBoneLink(128, 127, 0, 0, 3, 5, 0xFF, 0xFF), link);
        var preserved = V3dBuilder.PackLink([new(2, 200 / 255f), new(-1, 0f), new(4, 54 / 255f)], V3dWeightMode.Preserve);
        Assert.Equal(new V3dBoneLink(200, 0, 54, 0, 2, 0xFF, 4, 0xFF), preserved);
        Assert.Equal(preserved, V3dBuilder.PackLink(V3dBuilder.Influences(preserved), V3dWeightMode.Preserve));
    }

    [Fact]
    public void GroupsLargerThanABatchAreSplit()
    {
        int n = 3000; // 3000 separate triangles = 9000 vertices > 5460
        var verts = new List<V3dMeshVertex>();
        var tris = new List<V3dMeshTriangle>();
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < 3; k++) verts.Add(new V3dMeshVertex(new Vector3(i, k, 0), Vector3.UnitZ, Vector2.Zero, [new(0, 1)]));
            tris.Add(new V3dMeshTriangle(3 * i, 3 * i + 1, 3 * i + 2));
        }
        var d = SampleDescription();
        var sub = d.Submeshes[0];
        d = d with { Submeshes = [sub with { Lods = [sub.Lods[0] with { Groups = [new V3dMaterialGroup { Material = 0, Vertices = [.. verts], Triangles = [.. tris] }] }] }] };
        var file = V3dBuilder.Build(d);
        var lod = file.Submeshes.Single().Lods[0];
        Assert.Equal(2, lod.Batches.Length);
        Assert.Equal(5460, lod.Batches[0].VertexCount);
        Assert.Equal(n * 3, lod.Batches.Sum(b => b.VertexCount));
        Assert.All(lod.Batches, b => Assert.Equal(0, b.TextureIndex));
        Assert.Single(lod.Textures);
        V3dReader.Read(V3dWriter.Write(file), "big.v3c");
    }

    [Fact]
    public void BadDescriptionsAreRejected()
    {
        var d = SampleDescription();
        var sub = d.Submeshes[0];
        var badTri = sub with { Lods = [sub.Lods[0] with { Groups = [sub.Lods[0].Groups[0] with { Triangles = [new(0, 1, 9)] }] }] };
        Assert.Throws<ArgumentException>(() => V3dBuilder.Build(d with { Submeshes = [badTri] }));
        var badMat = sub with { Lods = [sub.Lods[0] with { Groups = [sub.Lods[0].Groups[0] with { Material = 5 }] }] };
        Assert.Throws<ArgumentException>(() => V3dBuilder.Build(d with { Submeshes = [badMat] }));
        var badBone = sub with { Lods = [sub.Lods[0] with { Groups = [sub.Lods[0].Groups[0] with { Vertices = [.. sub.Lods[0].Groups[0].Vertices.Select(v => v with { Influences = [new(300, 1)] })] }] }] };
        Assert.Throws<ArgumentException>(() => V3dBuilder.Build(d with { Submeshes = [badBone] }, V3dBuildOptions.Preserve));
        Assert.Throws<ArgumentException>(() => V3dBuilder.Build(d with { Submeshes = [sub with { Lods = [] }] }));
    }

    [Fact]
    public void DecomposeThenBuildRestoresTheSyntheticMeshExactly()
    {
        var original = V3dBuilder.Build(SampleDescription());
        var rebuilt = V3dBuilder.Build(V3dBuilder.Decompose(original), V3dBuildOptions.Preserve);
        Assert.Equal(V3dWriter.Write(original), V3dWriter.Write(rebuilt));
    }

    // â”€â”€ Corpus â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private sealed class Tally
    {
        public readonly SortedDictionary<string, (long Same, long Total)> Fields = new();
        public int Files, ByteIdentical;

        public void Add(string field, bool same)
        {
            Fields.TryGetValue(field, out var v);
            Fields[field] = (v.Same + (same ? 1 : 0), v.Total + 1);
        }

        public string Report(string title)
        {
            var lines = new List<string> { $"{title}: {Files} files, {ByteIdentical} byte-identical" };
            lines.AddRange(Fields.Select(kv => $"  {kv.Key,-34} {kv.Value.Same,8} / {kv.Value.Total,-8} {(kv.Value.Total == 0 ? 0 : 100.0 * kv.Value.Same / kv.Value.Total),6:F2}%"));
            return string.Join(Environment.NewLine, lines);
        }
    }

    private static V3dMeshDescription RulesOnly(V3dMeshDescription d) => d with
    {
        Submeshes = [.. d.Submeshes.Select(s => s with
        {
            Trailers = null,
            Lods = [.. s.Lods.Select(l => l with
            {
                Flags = null,
                Textures = null,
                MorphVertices = null,
                VertexCountOverride = null,
                Groups = [.. l.Groups.Select(g => g with { HeaderReserved0 = null, HeaderReserved1 = null })],
            })],
        })],
    };

    private static void Compare(V3dFile stock, V3dFile built, Tally t, bool rulesOnly)
    {
        t.Files++;
        if (!rulesOnly && V3dWriter.Write(stock).AsSpan().SequenceEqual(V3dWriter.Write(built))) t.ByteIdentical++;
        t.Add("header", stock.Header == built.Header);
        t.Add("section order", stock.Sections.Select(s => s.GetType()).SequenceEqual(built.Sections.Select(s => s.GetType())));
        t.Add("bones", ModelAssert.Diff(stock.Bones, built.Bones, "b") is null);
        t.Add("collision spheres", ModelAssert.Diff(stock.CollisionSpheres.ToList(), built.CollisionSpheres.ToList(), "c") is null);
        var ss = stock.Submeshes.ToList();
        var bs = built.Submeshes.ToList();
        Assert.Equal(ss.Count, bs.Count);
        for (int i = 0; i < ss.Count; i++)
        {
            var a = ss[i];
            var b = bs[i];
            // Primary data must come back unchanged whatever the mode.
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Offset, b.Offset);
            Assert.Equal(a.LodDistances.ToArray(), b.LodDistances.ToArray());
            Assert.Null(ModelAssert.Diff(a.Materials, b.Materials, "materials"));
            t.Add("submesh trailers", ModelAssert.Diff(a.Trailers, b.Trailers, "t") is null);
            t.Add("submesh radius (bits)", BitConverter.SingleToInt32Bits(a.Radius) == BitConverter.SingleToInt32Bits(b.Radius));
            t.Add("submesh radius (1e-6 rel)", MathF.Abs(a.Radius - b.Radius) <= 1e-6f * MathF.Max(1f, a.Radius));
            t.Add("submesh bounding box", a.AabbMin == b.AabbMin && a.AabbMax == b.AabbMax);
            Assert.Equal(a.Lods.Length, b.Lods.Length);
            for (int l = 0; l < a.Lods.Length; l++)
            {
                var la = a.Lods[l];
                var lb = b.Lods[l];
                Assert.Null(ModelAssert.Diff(la.PropPoints, lb.PropPoints, "props"));
                t.Add("LOD flags", la.Flags == lb.Flags);
                t.Add("LOD unknown1", la.Unknown1 == lb.Unknown1);
                t.Add("LOD vertex count", la.VertexCount == lb.VertexCount);
                t.Add("LOD texture list", ModelAssert.Diff(la.Textures, lb.Textures, "x") is null);
                Assert.Equal(la.Batches.Length, lb.Batches.Length);
                for (int k = 0; k < la.Batches.Length; k++)
                {
                    var ba = la.Batches[k];
                    var bb = lb.Batches[k];
                    // Geometry is primary data.
                    Assert.True(ba.Positions.SequenceEqual(bb.Positions));
                    Assert.True(ba.Normals.AsSpan().SequenceEqual(bb.Normals.AsSpan()) || ba.Normals.Zip(bb.Normals).All(z => Bits(z.First) == Bits(z.Second)));
                    Assert.True(ba.TexCoords.SequenceEqual(bb.TexCoords));
                    Assert.True(ba.Triangles.SequenceEqual(bb.Triangles));
                    Assert.True(ba.BoneLinks.SequenceEqual(bb.BoneLinks));
                    Assert.Equal(ba.RenderFlags, bb.RenderFlags);
                    t.Add("batch texture index", ba.TextureIndex == bb.TextureIndex);
                    t.Add("batch header reserved bytes", ba.HeaderReserved0.SequenceEqual(bb.HeaderReserved0) && ba.HeaderReserved1.SequenceEqual(bb.HeaderReserved1));
                    t.Add("batch_info sizes", ba.Sizes == bb.Sizes);
                    t.Add("same-position offsets (batches)", ba.SamePositionOffsets.SequenceEqual(bb.SamePositionOffsets));
                    if ((la.Flags & V3dLod.FlagMorphVerticesMap) != 0 && (lb.Flags & V3dLod.FlagMorphVerticesMap) != 0)
                        t.Add("morph map (batches)", ba.MorphMap.SequenceEqual(bb.MorphMap));
                    if (ba.Planes.Length > 0 && bb.Planes.Length == ba.Planes.Length)
                    {
                        for (int p = 0; p < ba.Planes.Length; p++)
                        {
                            var pa = ba.Planes[p];
                            var pb = bb.Planes[p];
                            bool nan = float.IsNaN(pa.Normal.X) && float.IsNaN(pb.Normal.X);
                            t.Add("plane normal (bits)", nan || Bits(pa.Normal) == Bits(pb.Normal));
                            t.Add("plane normal (1e-6)", nan || (pa.Normal - pb.Normal).Length() <= 1e-6f);
                            t.Add("plane distance (bits)", nan || BitConverter.SingleToInt32Bits(pa.Distance) == BitConverter.SingleToInt32Bits(pb.Distance));
                            t.Add("plane distance (1e-5)", nan || MathF.Abs(pa.Distance - pb.Distance) <= 1e-5f * MathF.Max(1f, MathF.Abs(pa.Distance)));
                        }
                    }
                }
            }
        }
    }

    private static (int, int, int) Bits(Vector3 v) =>
        (BitConverter.SingleToInt32Bits(v.X), BitConverter.SingleToInt32Bits(v.Y), BitConverter.SingleToInt32Bits(v.Z));

    [Fact]
    public void DecomposeBuildReproducesStockMeshes()
    {
        if (TestPaths.Corpus is null) return;
        var described = new Tally();
        var rules = new Tally();
        foreach (var path in Directory.EnumerateFiles(TestPaths.Corpus)
                     .Where(p => p.EndsWith(".v3c", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".v3m", StringComparison.OrdinalIgnoreCase))
                     .Order(StringComparer.OrdinalIgnoreCase))
        {
            var stock = V3dReader.ReadFile(path);
            var d = V3dBuilder.Decompose(stock);
            Compare(stock, V3dBuilder.Build(d, V3dBuildOptions.Preserve), described, rulesOnly: false);
            Compare(stock, V3dBuilder.Build(RulesOnly(d), V3dBuildOptions.Preserve), rules, rulesOnly: true);
        }
        output.WriteLine(described.Report("Decompose -> Build (description keeps explicit lists/leftovers)"));
        output.WriteLine(rules.Report("Decompose -> Build, rules only (texture lists, original-vertex order, LOD flags, leftovers re-derived)"));

        // What the rules must reproduce (DESIGN.md section 9): everything except planes' last bits,
        // radius in ~20% of files, 4 morph-map LODs and LOD 0-only bounding boxes in 2 statics.
        Assert.Equal(522, described.Files);
        foreach (var field in new[] { "header", "section order", "bones", "collision spheres", "batch_info sizes", "same-position offsets (batches)", "batch texture index" })
            Assert.Equal(described.Fields[field].Total, described.Fields[field].Same);
        Assert.True(described.Fields["plane normal (1e-6)"].Same >= described.Fields["plane normal (1e-6)"].Total - 10);
        Assert.True(described.Fields["plane distance (1e-5)"].Same >= described.Fields["plane distance (1e-5)"].Total - 10);
        Assert.True(described.Fields["morph map (batches)"].Same >= described.Fields["morph map (batches)"].Total - 20);
    }
}
