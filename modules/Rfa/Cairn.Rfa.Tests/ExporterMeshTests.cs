using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.V3d;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

public sealed class ExporterMeshTests(ITestOutputHelper output)
{
    static ExporterMeshTests() => LegacyMeshSupport.EnsureRegistered();

    private static byte[] StaticBox() => SyntheticExporterMesh.SampleStatic();

    private static byte[] Character() => SyntheticExporterMesh.SampleCharacter();

    [Fact]
    public void Static_ReadsEverySection()
    {
        var file = ExporterMeshReader.Read(StaticBox(), "box.v3d");
        Assert.Equal(V3dKind.StaticMesh, file.Kind);
        Assert.Equal(2, file.Submeshes.Length);
        Assert.Equal(8, file.Submeshes[0].Positions.Length);
        Assert.Equal(12, file.Submeshes[0].Faces.Length);
        Assert.Equal(36, file.Submeshes[0].Normals.Length);
        Assert.Equal("Box01_lod", file.Submeshes[0].Lods.Single().Name.Text);
        Assert.Equal("corona_1", file.PropPoints.Single().Name.Text);
        Assert.Equal(0.9f, file.CollisionSpheres.Single().Radius);
        Assert.Empty(file.Bones);
        Assert.Empty(file.Weights);
    }

    [Fact]
    public void Layout_TellsExporterFromCompiled()
    {
        var exporter = StaticBox();
        Assert.True(ExporterMeshReader.IsExporterLayout(exporter));
        Assert.True(ExporterMeshReader.LooksLikeExporterMesh(exporter.AsSpan(0, 48)));
        var compiled = LegacyMeshSupport.Convert(exporter, "box.v3d").Bytes;
        Assert.False(ExporterMeshReader.IsExporterLayout(compiled));
        Assert.False(ExporterMeshReader.LooksLikeExporterMesh(compiled.AsSpan(0, 48)));
        Assert.Null(LegacyMeshSupport.Identify(compiled, "box.v3m"));
        // By content: an exporter mesh named .v3m is still an exporter mesh, and a character one is a .vcm.
        Assert.Equal(".v3d", LegacyMeshSupport.Identify(exporter, "misnamed.v3m")?.Extension);
        Assert.Equal(".vcm", LegacyMeshSupport.Identify(Character(), "guard.v3d")?.Extension);
        Assert.True(LegacyMeshSupport.IsLegacyContent(exporter));
        Assert.False(LegacyMeshSupport.IsLegacyContent(compiled));
    }

    [Fact]
    public void Layout_PropPointsBeforeTheFirstSubmesh()
    {
        // The exporter writes DUMB sections first in some files.
        var bytes = StaticBox();
        Assert.Equal(0x44554D42, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40)));
        Assert.True(ExporterMeshReader.IsExporterLayout(bytes));
        Assert.True(new ExporterV3dFormat().Recognises(bytes.AsSpan(0, 64)));
    }

    [Fact]
    public void Static_ConvertsLikeTheCompiler()
    {
        var conversion = LegacyMeshSupport.Convert(StaticBox(), "box.v3d");
        Assert.Equal("box.v3m", conversion.OutputName);
        var mesh = V3dReader.Read(conversion.Bytes, conversion.OutputName);
        Assert.Equal(V3dKind.StaticMesh, mesh.Kind);
        var sub = Assert.Single(mesh.Submeshes);
        Assert.Equal("Box01", sub.Name.Text);
        Assert.Equal(new Vector3(0, 0.5f, 0), sub.Offset);
        Assert.Equal(new[] { 0f, 12f }, sub.LodDistances.ToArray());
        Assert.Equal(2, sub.Lods.Length);
        Assert.Equal(2, sub.Materials.Length);
        Assert.Equal(ExporterMeshConverter.CompiledBaseFlags, sub.Materials[0].Flags);
        Assert.Equal(ExporterMeshConverter.CompiledBaseFlags | ExporterMeshConverter.CompiledTwoSided, sub.Materials[1].Flags);
        Assert.Equal(1f, sub.Materials[1].Emissive);

        var lod0 = sub.Lods[0];
        Assert.Equal(V3dLod.FlagTrianglePlanes, lod0.Flags);
        Assert.Equal(8, lod0.VertexCount);
        Assert.Equal(2, lod0.Batches.Length);
        Assert.Equal(10, lod0.Batches[0].TriangleCount);
        Assert.All(lod0.Batches[1].Triangles, t => Assert.Equal(V3dTriangle.DoubleSided, t.Flags));
        Assert.All(lod0.Batches[0].Triangles, t => Assert.Equal(0, t.Flags));
        // UVs were exported one tile down (V in -1..0): each triangle moves back to 0..1.
        Assert.All(lod0.Batches.SelectMany(b => b.TexCoords), uv => Assert.InRange(uv.Y, 0f, 1f));
        // Smooth normals: a cube corner's normal points diagonally out.
        var corner = lod0.Batches[0];
        int at = corner.Positions.IndexOf(new Vector3(0.5f, 1f, 0.5f));
        Assert.True(at >= 0);
        Assert.True(Vector3.Dot(Vector3.Normalize(corner.Normals[at]), Vector3.Normalize(new Vector3(1, 1, 1))) > 0.9f);
        // The LOD SUBM is not a submesh of its own; the prop point is in every LOD.
        Assert.Equal(12, sub.Lods[1].Batches.Sum(b => b.TriangleCount));
        Assert.All(sub.Lods, l => Assert.Equal("corona_1", l.PropPoints.Single().Name.Text));
        Assert.Equal(V3dPropPoint.NameSize, lod0.PropPoints[0].Name.Length);
        Assert.Equal(0.9f, mesh.CollisionSpheres.Single().Radius);
        // The stored normals (all +Z) differ from smooth ones: the report says the hard edges were smoothed.
        Assert.Contains(conversion.Report, n => n.Contains("smoothed"));
    }

    [Fact]
    public void Character_KeepsSkeletonWeightsAndMorphOrder()
    {
        var conversion = LegacyMeshSupport.Convert(Character(), "guard.vcm");
        Assert.Equal("guard.v3c", conversion.OutputName);
        var mesh = V3dReader.Read(conversion.Bytes, conversion.OutputName);
        Assert.Equal(V3dKind.Character, mesh.Kind);
        Assert.Equal(["root-bdbn-pelvis", "root-bdbn-spine"], mesh.Bones.Select(b => b.Name.Text));
        // The rotation keeps its meaning; |W| > 0.5 so W comes out positive.
        var spine = mesh.Bones[1].Rotation;
        Assert.True(spine.W > 0);
        Assert.True(MathF.Abs(MathF.Abs(Quaternion.Dot(spine, Quaternion.Normalize(new Quaternion(-0.1f, -0.2f, -0.3f, -0.9273618f)))) - 1f) < 1e-5f);
        var sub = Assert.Single(mesh.Submeshes);
        Assert.Equal(Vector3.Zero, sub.Offset);
        // Both materials name body.tga: one batch, as the compiler did.
        var lod = Assert.Single(sub.Lods);
        Assert.Equal(V3dBuilder.DefaultCharacterLodFlags, lod.Flags);
        Assert.Equal(8, lod.VertexCount);
        var batch = Assert.Single(lod.Batches);
        Assert.Equal(12, batch.TriangleCount);
        for (int i = 0; i < batch.VertexCount; i++)
        {
            var link = batch.BoneLinks[i];
            bool upper = batch.Positions[i].Z > 0;
            if (upper) Assert.Equal(new V3dBoneLink(200, 54, 0, 0, 1, 0, 0xFF, 0xFF), link);
            else Assert.Equal(new V3dBoneLink(255, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF), link);
        }
        // Morph map: original vertex o addresses the batch vertex at the o-th exporter position.
        var cube = SyntheticExporterMesh.Cube();
        for (int o = 0; o < 8; o++) Assert.Equal(cube[o], batch.Positions[batch.MorphMap[o]]);
        Assert.Equal("head", mesh.CollisionSpheres.Single().Name.Text);
        Assert.Equal("eye", lod.PropPoints.Single().Name.Text);
    }

    [Fact]
    public void Uvs_CloseCornersShareAVertex()
    {
        // Two triangles over the same four corners; the second's UVs are 0.01 off at two corners.
        var faces = new SyntheticExporterMesh.Face[]
        {
            new(0, 1, 2, new(0, 0), new(1, 0), new(1, 1), 0),
            new(0, 2, 3, new(0.01f, 0), new(1, 1.01f), new(0, 1), 0),
        };
        Vector3[] quad = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        var bytes = new SyntheticExporterMesh(false).Submesh("Quad", quad, faces, [new("a.tga")]).ToArray();
        var conversion = LegacyMeshSupport.Convert(bytes, "quad.v3d");
        var batch = conversion.Mesh.Submeshes.Single().Lods[0].Batches.Single();
        Assert.Equal(4, batch.VertexCount);
        Assert.Contains(conversion.Report, n => n.Contains("joined"));
        Assert.DoesNotContain(conversion.Report, n => n.Contains("smoothed"));
    }

    [Fact]
    public void Normals_GarbageIsReplaced()
    {
        var garbage = new Vector3(-431602080f);
        var bytes = new SyntheticExporterMesh(false).Submesh("Box", SyntheticExporterMesh.Cube(), SyntheticExporterMesh.CubeFaces(),
            [new("a.tga"), new("b.tga")], storedNormals: Enumerable.Repeat(garbage, 8).ToArray()).ToArray();
        var conversion = LegacyMeshSupport.Convert(bytes, "box.v3d");
        Assert.Contains(conversion.Report, n => n.Contains("no usable normals"));
        Assert.All(conversion.Mesh.Submeshes.Single().Lods[0].Batches.SelectMany(b => b.Normals), n => Assert.InRange(n.Length(), 0.999f, 1.001f));
    }

    [Fact]
    public void Lods_MissingLevelIsReported()
    {
        var bytes = new SyntheticExporterMesh(false)
            .Submesh("Box", SyntheticExporterMesh.Cube(), SyntheticExporterMesh.CubeFaces(), [new("a.tga"), new("b.tga")], [("Nowhere", 5f)])
            .ToArray();
        var conversion = LegacyMeshSupport.Convert(bytes, "box.v3d");
        Assert.Single(conversion.Mesh.Submeshes.Single().Lods);
        Assert.Contains(conversion.Report, n => n.Contains("Nowhere"));
    }

    [Fact]
    public void Formats_RegisteredOnceForAllFourExtensions()
    {
        LegacyMeshSupport.EnsureRegistered();
        foreach (string ext in LegacyMeshSupport.Extensions) Assert.NotNull(LegacyMeshFormats.For(ext));
        Assert.IsType<ExporterV3dFormat>(LegacyMeshFormats.For("v3d"));
        Assert.IsType<ExporterVcmFormat>(LegacyMeshFormats.For(".VCM"));
    }

    [Fact]
    public void Malformed_NeverThrows()
    {
        var format = new ExporterV3dFormat();
        foreach (var good in new[] { StaticBox(), Character() })
        {
            for (int length = 0; length < good.Length; length++)
            {
                var cut = good.AsSpan(0, length).ToArray();
                Assert.False(format.TryRead(cut, "cut.v3d", out _, out var error), $"length {length}");
                Assert.NotNull(error);
                _ = LegacyMeshSupport.IsLegacyContent(cut);
            }
            var random = new Random(7);
            for (int i = 0; i < 3000; i++)
            {
                var bytes = (byte[])good.Clone();
                for (int k = 0; k < 4; k++) bytes[random.Next(8, bytes.Length)] = (byte)random.Next(256);
                if (format.TryRead(bytes, "fuzz.v3d", out var mesh, out _))
                {
                    try { LegacyMeshSupport.Convert(mesh!, "fuzz.v3d"); }
                    catch (AssetFormatException) { }
                }
            }
        }
    }

    [Fact]
    public void Twin_PrefersTheExporterFileForAPs2Mesh()
    {
        var exporter = StaticBox();
        // The PS2 mesh's own bytes are not even needed when its exporter twin is there.
        byte[] ps2 = [0x12, 0x87, 0x12, 0x87, 0, 0, 0, 0, 1, 0, 0, 0];
        var conversion = LegacyMeshSupport.Convert(ps2, "box.rfm", name => name.Equals("box.v3d", StringComparison.OrdinalIgnoreCase) ? exporter : null);
        Assert.Equal("box.v3d", conversion.TwinName);
        Assert.Equal("box.v3m", conversion.OutputName);
        Assert.Contains("box.v3d", conversion.Report[0]);
    }

    // ── Real files (skipped when absent) ─────────────────────────────────────

    private static bool IsExporterName(string name) =>
        name.EndsWith(".v3d", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".vcm", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every .v3d/.vcm in the PS2 folder (packfiles and loose), the exporter samples folder and the game folder.</summary>
    internal static IEnumerable<(string Where, string Name, byte[] Bytes)> RealSamples()
    {
        if (LocalPaths.Ps2Directory is { } ps2 && Directory.Exists(ps2))
        {
            foreach (var vpp in Directory.EnumerateFiles(ps2, "*.vpp"))
            {
                var archive = VppArchive.Open(vpp);
                foreach (var e in archive.Entries.Where(e => IsExporterName(e.Name)))
                    yield return (Path.GetFileName(vpp), e.Name, archive.ReadEntry(e));
            }
            foreach (var file in Directory.EnumerateFiles(ps2, "*.*", SearchOption.AllDirectories).Where(IsExporterName))
                yield return ("ps2 loose", file, File.ReadAllBytes(file));
        }
        foreach (var dir in new[] { LocalPaths.MeshesStuffDirectory, LocalPaths.GameDirectory })
        {
            if (dir is null || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(IsExporterName))
                yield return ("loose", file, File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void RealFiles_AllReadAndConvert()
    {
        int v3d = 0, vcm = 0, notes = 0;
        var failures = new List<string>();
        foreach (var (where, name, bytes) in RealSamples())
        {
            string file = Path.GetFileName(name);
            if (!LegacyMeshSupport.TryRead(bytes, file, out var mesh, out var error))
            {
                failures.Add($"{where}|{name}: {error?.Message}");
                continue;
            }
            Assert.True(ExporterMeshReader.IsExporterLayout(bytes), name);
            var conversion = LegacyMeshSupport.Convert(mesh!, file);
            var back = V3dReader.Read(conversion.Bytes, conversion.OutputName);
            Assert.Equal(mesh!.Description.Kind, back.Kind);
            Assert.Equal(mesh.Description.Submeshes.Sum(s => s.Lods.Sum(l => l.Groups.Sum(g => g.Triangles.Length))),
                back.Submeshes.Sum(s => s.Lods.Sum(l => l.Batches.Sum(b => b.TriangleCount))));
            if (mesh.Description.Kind == V3dKind.Character) vcm++;
            else v3d++;
            notes += conversion.Report.Length;
        }
        Assert.Empty(failures);
        output.WriteLine(v3d + vcm == 0
            ? "skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory")
            : $"{v3d} static and {vcm} character exporter meshes read, converted and read back; {notes} report lines");
    }

    /// <summary>
    /// The PS2 demo's work folder keeps eight .v3d files beside the .v3m the game's compiler made from them: the
    /// conversion matches each in geometry (positions, normals, UVs per triangle corner), materials, LODs, offsets,
    /// bounds, prop points and size.
    /// </summary>
    [Fact]
    public void RealTwins_MatchTheCompilersOutput()
    {
        if (LocalPaths.Ps2Directory is not { } ps2 || !Directory.Exists(Path.Combine(ps2, "work", "meshes")))
        {
            output.WriteLine("skipped: " + LocalPaths.HowToSet(LocalPaths.Ps2DirectoryVariable, "ps2Directory"));
            return;
        }
        string folder = Path.Combine(ps2, "work", "meshes");
        int twins = 0;
        foreach (var v3dPath in Directory.EnumerateFiles(folder).Where(p => p.EndsWith(".v3d", StringComparison.OrdinalIgnoreCase)))
        {
            string stockPath = Path.ChangeExtension(v3dPath, ".v3m");
            if (!File.Exists(stockPath)) continue;
            var conversion = LegacyMeshSupport.Convert(File.ReadAllBytes(v3dPath), Path.GetFileName(v3dPath));
            byte[] stockBytes = File.ReadAllBytes(stockPath);
            var stock = V3dReader.Read(stockBytes, Path.GetFileName(stockPath));
            var problems = TwinComparer.Compare(conversion.Mesh, stock);
            Assert.True(problems.Count == 0, Path.GetFileName(v3dPath) + ": " + string.Join("; ", problems));
            Assert.Equal(stockBytes.Length, conversion.Bytes.Length);
            int same = 0;
            for (int i = 0; i < stockBytes.Length; i++) if (stockBytes[i] == conversion.Bytes[i]) same++;
            output.WriteLine($"{Path.GetFileName(v3dPath)}: same geometry, {stockBytes.Length} bytes, {same * 100.0 / stockBytes.Length:0.0}% identical at the same offsets");
            twins++;
        }
        Assert.Equal(8, twins);
    }

    /// <summary>
    /// The PS2 demo's .vcm files against the stock .v3c of the same name: most stock characters are a later
    /// revision (more LODs), but the first-person weapons below were compiled from these very files.
    /// </summary>
    [Fact]
    public void RealTwins_CharactersMatchStockWhereTheRevisionIsTheSame()
    {
        if (LocalPaths.Ps2Directory is not { } ps2 || TestPaths.Corpus is not { } corpus)
        {
            output.WriteLine("skipped: needs the PS2 folder and the stock corpus");
            return;
        }
        string[] same = ["drone01", "fp_glock", "fp_mp", "fp_riot", "fp_rmt_det", "fp_rocketlauncher", "fp_shotgun", "fp_SniperRifle", "trnsprt_rocket"];
        int checkedCount = 0;
        foreach (var file in Directory.EnumerateFiles(ps2, "*.vcm", SearchOption.AllDirectories)
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            string stem = Path.GetFileNameWithoutExtension(file);
            string stockPath = Path.Combine(corpus, stem + ".v3c");
            if (!File.Exists(stockPath)) continue;
            var conversion = LegacyMeshSupport.Convert(File.ReadAllBytes(file), Path.GetFileName(file));
            var problems = TwinComparer.Compare(conversion.Mesh, V3dReader.ReadFile(stockPath));
            output.WriteLine($"{stem}: {(problems.Count == 0 ? "same as stock" : $"{problems.Count} differences, first: {problems[0]}")}");
            if (same.Contains(stem, StringComparer.OrdinalIgnoreCase))
            {
                Assert.True(problems.Count == 0, stem + ": " + string.Join("; ", problems));
                checkedCount++;
            }
        }
        Assert.Equal(same.Length, checkedCount);
    }
}

/// <summary>Compares a converted mesh with a stock one: everything that matters to the game, order-free where the compiler reordered.</summary>
internal static class TwinComparer
{
    public static List<string> Compare(V3dFile ours, V3dFile stock)
    {
        var p = new List<string>();
        void Check(bool ok, string what) { if (!ok) p.Add(what); }
        var os = ours.Submeshes.ToList();
        var ss = stock.Submeshes.ToList();
        Check(os.Count == ss.Count, $"{os.Count} submeshes, stock {ss.Count}");
        for (int s = 0; s < Math.Min(os.Count, ss.Count); s++)
        {
            var a = os[s];
            var b = ss[s];
            Check(a.Name.Text == b.Name.Text, $"submesh {s} name {a.Name.Text} vs {b.Name.Text}");
            Check(Near(a.Offset, b.Offset) && Near(a.AabbMin, b.AabbMin) && Near(a.AabbMax, b.AabbMax) && MathF.Abs(a.Radius - b.Radius) < 1e-4f, $"submesh {s} bounds");
            Check(a.LodDistances.SequenceEqual(b.LodDistances), $"submesh {s} LOD distances");
            Check(a.Materials.Length == b.Materials.Length, $"submesh {s} materials {a.Materials.Length} vs {b.Materials.Length}");
            for (int m = 0; m < Math.Min(a.Materials.Length, b.Materials.Length); m++)
            {
                var x = a.Materials[m];
                var y = b.Materials[m];
                // Material flag 0x8 marks a texture with alpha; the compiler read the texture to set it.
                Check(x.DiffuseMap.Text == y.DiffuseMap.Text && x.Emissive == y.Emissive && (x.Flags | 0x8) == (y.Flags | 0x8)
                    && x.ReflectionCoefficient == y.ReflectionCoefficient && x.ReflectionMap.Text == y.ReflectionMap.Text, $"submesh {s} material {m}");
            }
            Check(a.Lods.Length == b.Lods.Length, $"submesh {s} LODs {a.Lods.Length} vs {b.Lods.Length}");
            for (int l = 0; l < Math.Min(a.Lods.Length, b.Lods.Length); l++)
            {
                var la = a.Lods[l];
                var lb = b.Lods[l];
                Check(la.Flags == lb.Flags && la.VertexCount == lb.VertexCount, $"submesh {s} LOD {l} flags/vertex count");
                Check(la.Textures.Select(t => t.MaterialIndex + ":" + t.FileName).SequenceEqual(lb.Textures.Select(t => t.MaterialIndex + ":" + t.FileName)), $"submesh {s} LOD {l} textures");
                Check(la.Batches.Length == lb.Batches.Length, $"submesh {s} LOD {l} batches");
                Check(la.PropPoints.Length == lb.PropPoints.Length && la.PropPoints.Zip(lb.PropPoints).All(z => z.First.Name.Text == z.Second.Name.Text
                    && Near(z.First.Position, z.Second.Position) && z.First.ParentIndex == z.Second.ParentIndex), $"submesh {s} LOD {l} prop points");
                for (int bi = 0; bi < Math.Min(la.Batches.Length, lb.Batches.Length); bi++)
                {
                    var A = Corners(la.Batches[bi]);
                    var B = Corners(lb.Batches[bi]);
                    Check(la.Batches[bi].VertexCount == lb.Batches[bi].VertexCount, $"submesh {s} LOD {l} batch {bi} vertices {la.Batches[bi].VertexCount} vs {lb.Batches[bi].VertexCount}");
                    var pool = B.ToList();
                    int missing = 0;
                    foreach (var t in A)
                    {
                        int k = pool.FindIndex(u => Same(t, u));
                        if (k < 0) missing++;
                        else pool.RemoveAt(k);
                    }
                    Check(missing == 0 && pool.Count == 0, $"submesh {s} LOD {l} batch {bi}: {missing} triangles differ");
                }
            }
        }
        var oc = ours.CollisionSpheres.ToList();
        var sc = stock.CollisionSpheres.ToList();
        Check(oc.Count == sc.Count && oc.Zip(sc).All(z => z.First.Name.Text == z.Second.Name.Text && z.First.BoneIndex == z.Second.BoneIndex
            && Near(z.First.Position, z.Second.Position) && MathF.Abs(z.First.Radius - z.Second.Radius) < 1e-4f), "collision spheres");
        Check(ours.Bones.Length == stock.Bones.Length && ours.Bones.Zip(stock.Bones).All(z => z.First.Name.Text == z.Second.Name.Text
            && z.First.ParentIndex == z.Second.ParentIndex && Near(z.First.Position, z.Second.Position)
            && Near(new Vector3(z.First.Rotation.X, z.First.Rotation.Y, z.First.Rotation.Z), new Vector3(z.Second.Rotation.X, z.Second.Rotation.Y, z.Second.Rotation.Z))
            && MathF.Abs(z.First.Rotation.W - z.Second.Rotation.W) < 1e-4f), "bones");
        return p;
    }

    private readonly record struct Corner(Vector3 P, Vector3 N, Vector2 Uv, V3dBoneLink Link);

    private static List<Corner[]> Corners(V3dBatch b) =>
        [.. b.Triangles.Select(t => new[] { t.A, t.B, t.C }.Select(i => new Corner(b.Positions[i], b.Normals[i], b.TexCoords[i],
            i < b.BoneLinks.Length ? b.BoneLinks[i] : default)).ToArray())];

    private static bool Same(Corner[] a, Corner[] b)
    {
        for (int r = 0; r < 3; r++)
        {
            bool ok = true;
            for (int k = 0; k < 3 && ok; k++)
            {
                var x = a[k];
                var y = b[(k + r) % 3];
                ok = Near(x.P, y.P) && Near(x.N, y.N, 2e-3f) && (x.Uv - y.Uv).Length() < 1e-5f && x.Link == y.Link;
            }
            if (ok) return true;
        }
        return false;
    }

    private static bool Near(Vector3 a, Vector3 b, float tolerance = 1e-4f) => (a - b).Length() <= tolerance;
}
