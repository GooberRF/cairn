using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats.Obj;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Rfa.Linting;
using Xunit.Abstractions;

namespace Cairn.Rfa.Tests;

/// <summary>
/// Meshes to and from Wavefront OBJ: the space conversion (X mirrored, winding reversed, normals mirrored, V
/// flipped), v3m → obj → v3m geometry equality with Cairn's metadata (and without it), Blender- and 3ds Max-style
/// files, the pre-flight report, characters on a kept skeleton, and a bounded sample of the stock meshes.
/// </summary>
public class ObjMeshTests(ITestOutputHelper output)
{
    private static FixedString Name(string text, int length) => FixedString.FromText(text, length);

    private static V3dMaterial Material(string texture, float emissive = 0f, uint flags = 1) =>
        new(Name(texture, 32), emissive, 0f, 0f, 0f, Name("", 32), flags);

    /// <summary>
    /// A static mesh with two submeshes (one with an offset and two LODs), two materials, an additive batch, a
    /// double-sided triangle, a shared texture and a prop point; positions, UVs and normals are not symmetric so a
    /// lost mirror or flip shows.
    /// </summary>
    internal static V3dFile SampleStatic()
    {
        V3dMeshVertex V(float x, float y, float z, float u, float v, Vector3 n) => new(new Vector3(x, y, z), Vector3.Normalize(n), new Vector2(u, v));
        var up = new Vector3(0.1f, 1f, 0.2f);
        var box = new V3dMaterialGroup
        {
            Material = 0,
            Vertices = [V(0, 0, 0, 0, 1, up), V(1.5f, 0, 0.25f, 1, 1, up), V(1.5f, 0.5f, 2, 1, 0, -Vector3.UnitZ), V(0, 0.75f, 2, 0, 0, Vector3.UnitX)],
            Triangles = [new(0, 1, 2), new(0, 2, 3), new(3, 2, 1, V3dTriangle.DoubleSided)],
        };
        var glow = new V3dMaterialGroup
        {
            Material = 1,
            RenderFlags = 0x110C21,
            Vertices = [V(2, 0, 0, 0.25f, 0.75f, Vector3.UnitY), V(3, 0, 0, 0.5f, 0.75f, Vector3.UnitY), V(2, 1, 0.5f, 0.25f, 0.25f, Vector3.UnitY)],
            Triangles = [new(0, 1, 2)],
        };
        var shared = box with { Material = 0, RenderFlags = V3dBuilder.DefaultRenderFlags };
        var description = new V3dMeshDescription
        {
            Kind = V3dKind.StaticMesh,
            PropPoints = [new V3dPropPoint(Name("light", 0x44), new Quaternion(0.1f, 0.2f, 0.3f, 0.927f), new Vector3(0.5f, 1.25f, -0.75f), -1)],
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Name = Name("crate", 24),
                    Offset = new Vector3(4, -1, 2.5f),
                    Materials = [Material("crate01.tga", 0.5f, 9), Material("glow.tga")],
                    Lods =
                    [
                        new V3dLodDescription { Distance = 0, Groups = [box, glow] },
                        new V3dLodDescription { Distance = 25, Groups = [box with { Triangles = [new(0, 1, 2)] }] },
                    ],
                },
                new V3dSubmeshDescription
                {
                    Name = Name("lid", 24),
                    ParentName = Name("crate", 24),
                    Materials = [Material("crate01.tga")],
                    Lods = [new V3dLodDescription { Distance = 0, Groups = [shared] }],
                },
            ],
        };
        return V3dBuilder.Build(description);
    }

    // ── comparison ──────────────────────────────────────────────────────────

    private readonly record struct Corner(Vector3 Position, Vector2 Uv, Vector3 Normal);

    private readonly record struct Tri(Corner A, Corner B, Corner C, bool DoubleSided);

    private static Vector3 Unit(Vector3 n)
    {
        float len = n.Length();
        return float.IsFinite(len) && len >= 1e-12f ? n / len : Vector3.UnitY;
    }

    /// <summary>
    /// The triangles of every (submesh, LOD, texture, render flags) in drawing order: per batch, its single-sided
    /// triangles and then its double-sided ones (the order the OBJ export writes them), corners in model space.
    /// </summary>
    private static Dictionary<string, List<Tri>> Triangles(V3dFile mesh, int? onlyLod = 0, bool partition = true)
    {
        var result = new Dictionary<string, List<Tri>>(StringComparer.Ordinal);
        foreach (var s in mesh.Submeshes)
        {
            for (int l = 0; l < s.Lods.Length; l++)
            {
                if (onlyLod is { } want && l != Math.Min(want, s.Lods.Length - 1)) continue;
                var lod = s.Lods[l];
                foreach (var b in lod.Batches)
                {
                    string texture = s.Materials[lod.Textures[b.TextureIndex].MaterialIndex].DiffuseMap.Text;
                    string key = $"{s.Name.Text}|{(onlyLod is null ? l : 0)}|{texture}|{b.RenderFlags:X}";
                    if (!result.TryGetValue(key, out var list)) result[key] = list = [];
                    Corner C(int i)
                    {
                        var uv = b.TexCoords[i];
                        return new Corner(b.Positions[i] + s.Offset, float.IsFinite(uv.X) && float.IsFinite(uv.Y) ? uv : Vector2.Zero, Unit(b.Normals[i]));
                    }
                    var tris = b.Triangles.Select(t => new Tri(C(t.A), C(t.B), C(t.C), (t.Flags & V3dTriangle.DoubleSided) != 0)).ToList();
                    list.AddRange(partition ? tris.Where(t => !t.DoubleSided).Concat(tris.Where(t => t.DoubleSided)) : tris);
                }
            }
        }
        return result;
    }

    private static bool Near(float a, float b, float tolerance) => MathF.Abs(a - b) <= tolerance * MathF.Max(1f, MathF.Max(MathF.Abs(a), MathF.Abs(b)));

    private static bool Near(Vector3 a, Vector3 b, float tolerance) => Near(a.X, b.X, tolerance) && Near(a.Y, b.Y, tolerance) && Near(a.Z, b.Z, tolerance);

    private static bool Near(Corner a, Corner b) =>
        Near(a.Position, b.Position, 2e-5f) && Near(a.Uv.X, b.Uv.X, 2e-5f) && Near(a.Uv.Y, b.Uv.Y, 2e-5f) && Near(a.Normal, b.Normal, 2e-5f);

    private static bool Near(Tri a, Tri b) => a.DoubleSided == b.DoubleSided && Near(a.A, b.A) && Near(a.B, b.B) && Near(a.C, b.C);

    /// <summary>Same triangles in the same order with the same winding (corner order), within tolerance. Returns null or the first difference.</summary>
    private static string? CompareOrdered(Dictionary<string, List<Tri>> expected, Dictionary<string, List<Tri>> actual)
    {
        if (!expected.Keys.Order().SequenceEqual(actual.Keys.Order()))
            return $"groups differ: [{string.Join(", ", expected.Keys.Order())}] vs [{string.Join(", ", actual.Keys.Order())}]";
        foreach (var (key, want) in expected)
        {
            var got = actual[key];
            if (want.Count != got.Count) return $"{key}: {want.Count} triangles vs {got.Count}";
            for (int i = 0; i < want.Count; i++)
                if (!Near(want[i], got[i])) return $"{key} triangle {i}: {want[i]} vs {got[i]}";
        }
        return null;
    }

    /// <summary>Same triangles per group in any order (a corner rotation allowed, the winding kept), within tolerance.</summary>
    private static string? CompareUnordered(Dictionary<string, List<Tri>> expected, Dictionary<string, List<Tri>> actual)
    {
        if (!expected.Keys.Order().SequenceEqual(actual.Keys.Order()))
            return $"groups differ: [{string.Join(", ", expected.Keys.Order())}] vs [{string.Join(", ", actual.Keys.Order())}]";
        foreach (var (key, want) in expected)
        {
            var left = actual[key].ToList();
            if (want.Count != left.Count) return $"{key}: {want.Count} triangles vs {left.Count}";
            foreach (var t in want)
            {
                int at = left.FindIndex(g => Near(t, g) || Near(t, g with { A = g.B, B = g.C, C = g.A }) || Near(t, g with { A = g.C, B = g.A, C = g.B }));
                if (at < 0) return $"{key}: no match for {t}";
                left.RemoveAt(at);
            }
        }
        return null;
    }

    /// <summary>Writes the export as text and reads it back, as a file on disk would go.</summary>
    private static (ObjDocument Obj, MtlDocument Mtl, string Text) ThroughText(ObjMeshExportResult result)
    {
        result.Obj.MaterialLibraries.Clear();
        result.Obj.MaterialLibraries.Add("export.mtl");
        string text = ObjWriter.Write(result.Obj);
        string mtl = ObjWriter.WriteMtl(result.Mtl);
        return (ObjReader.Read(text, "export.obj"), ObjReader.ReadMtl(mtl, "export.mtl"), text);
    }

    // ── space and layout ────────────────────────────────────────────────────

    [Fact]
    public void Export_mirrors_x_reverses_winding_mirrors_normals_and_flips_v()
    {
        var mesh = V3dBuilder.Build(new V3dMeshDescription
        {
            Kind = V3dKind.StaticMesh,
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Name = Name("tri", 24),
                    Materials = [Material("rock.tga")],
                    Lods =
                    [
                        new V3dLodDescription
                        {
                            Groups =
                            [
                                new V3dMaterialGroup
                                {
                                    Material = 0,
                                    Vertices =
                                    [
                                        new(new Vector3(1, 2, 3), Vector3.UnitX, new Vector2(0.25f, 0.125f)),
                                        new(new Vector3(4, 2, 3), Vector3.UnitY, new Vector2(1, 0)),
                                        new(new Vector3(1, 5, 3), Vector3.UnitZ, new Vector2(0, 1)),
                                    ],
                                    Triangles = [new(0, 1, 2)],
                                },
                            ],
                        },
                    ],
                },
            ],
        });
        var result = ObjMeshExport.Export(mesh, new ObjMeshExportOptions { WriteMetadata = false });
        var obj = result.Obj;
        Assert.Single(obj.Objects);
        Assert.Equal("tri", obj.Objects[0].Name);
        var face = Assert.Single(obj.Objects[0].Faces);
        Assert.Equal("rock", face.Material);
        // RF (A, B, C) is written (A, C, B): the mirror flips handedness, so the order flips too.
        Assert.Equal(new Vector3(-1, 2, 3), obj.Positions[face.Corners[0].Position]);
        Assert.Equal(new Vector3(-1, 5, 3), obj.Positions[face.Corners[1].Position]);
        Assert.Equal(new Vector3(-4, 2, 3), obj.Positions[face.Corners[2].Position]);
        Assert.Equal(new Vector3(-1, 0, 0), obj.Normals[face.Corners[0].Normal]);
        Assert.Equal(new Vector3(0, 0, 1), obj.Normals[face.Corners[1].Normal]);
        Assert.Equal(new Vector2(0.25f, 0.875f), obj.TexCoords[face.Corners[0].TexCoord]);
        Assert.Equal(new Vector2(0f, 0f), obj.TexCoords[face.Corners[1].TexCoord]);
        var material = Assert.Single(result.Mtl.Materials);
        Assert.Equal("rock", material.Name);
        Assert.Equal("rock.tga", material.DiffuseMap?.Path);
        Assert.DoesNotContain(obj.Comments, c => c.Text.StartsWith(ObjMeshMetadata.Prefix, StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("metadata", StringComparison.Ordinal));

        // The OBJ face is counter-clockwise seen from the side its (mirrored) normals point to, as OBJ tools expect.
        var p = face.Corners.Select(c => obj.Positions[c.Position]).ToArray();
        var geometric = Vector3.Cross(p[1] - p[0], p[2] - p[0]);
        Assert.True(Vector3.Dot(geometric, GltfSpace.ToGltf(Vector3.Cross(new Vector3(3, 0, 0), new Vector3(0, 3, 0)))) > 0);
    }

    [Fact]
    public void Static_mesh_round_trips_with_metadata_exactly_in_order_and_winding()
    {
        var mesh = SampleStatic();
        foreach (int? lod in new int?[] { 0, 1, null })
        {
            var result = ObjMeshExport.Export(mesh, new ObjMeshExportOptions { Lod = lod, SourceName = "crate.v3m" });
            var (obj, mtl, text) = ThroughText(result);
            Assert.Contains("# rf_mesh kind=static", text, StringComparison.Ordinal);
            var back = ObjMeshImport.Import(obj, mtl, null, "export.obj");
            Assert.True(back.HasMetadata);
            Assert.False(back.HasErrors, string.Join("; ", back.Issues));
            var built = Assert.IsType<V3dFile>(back.Mesh);
            Assert.Equal(V3dKind.StaticMesh, built.Kind);
            Assert.Null(CompareOrdered(Triangles(mesh, lod), Triangles(built, lod is null ? null : 0, partition: false)));

            var subs = mesh.Submeshes.ToList();
            var got = built.Submeshes.ToList();
            Assert.Equal(subs.Select(s => s.Name.Text), got.Select(s => s.Name.Text));
            for (int i = 0; i < subs.Count; i++)
            {
                Assert.Equal(subs[i].Offset, got[i].Offset);
                Assert.Equal(subs[i].ParentName.Text, got[i].ParentName.Text);
                // One LOD keeps only the materials its faces use: crate's LOD 1 has no glow batch.
                var expected = lod == 1 && i == 0 ? subs[i].Materials.Take(1) : subs[i].Materials;
                Assert.Equal(expected.ToArray(), got[i].Materials.ToArray());
                if (lod is null)
                {
                    Assert.Equal(subs[i].LodDistances.ToArray(), got[i].LodDistances.ToArray());
                    Assert.Equal(subs[i].Lods.Select(l => l.Flags), got[i].Lods.Select(l => l.Flags));
                }
                else Assert.Single(got[i].Lods);
            }
            var props = got[0].Lods[0].PropPoints;
            Assert.Equal(mesh.Submeshes.First().Lods[0].PropPoints.ToArray(), props.ToArray());
            Assert.DoesNotContain(MeshLinter.Analyze(built), d => d.Severity == DiagnosticSeverity.Error);
        }
    }

    [Fact]
    public void One_lod_of_a_three_lod_mesh_reads_back_with_that_lods_textures_only()
    {
        V3dMaterialGroup Tri(int material) => new()
        {
            Material = material,
            Vertices = [new(Vector3.Zero, Vector3.UnitY, Vector2.Zero), new(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX), new(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY)],
            Triangles = [new(0, 1, 2)],
        };
        // Each LOD uses its own textures (as stock characters do with their -mip2 / -mip3 copies); LOD 1 shares slot 0.
        var mesh = V3dBuilder.Build(new V3dMeshDescription
        {
            Kind = V3dKind.StaticMesh,
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Name = Name("body", 24),
                    Materials = [Material("body.tga"), Material("body-mip2.tga"), Material("head-mip2.tga"), Material("body-mip3.tga")],
                    Lods =
                    [
                        new V3dLodDescription { Distance = 0, Groups = [Tri(0)] },
                        new V3dLodDescription { Distance = 10, Groups = [Tri(1), Tri(2), Tri(0)] },
                        new V3dLodDescription { Distance = 20, Groups = [Tri(3)] },
                    ],
                },
            ],
        });
        foreach (var (lod, textures) in new (int? Lod, string[] Textures)[]
        {
            (0, ["body.tga"]),
            (1, ["body.tga", "body-mip2.tga", "head-mip2.tga"]),
            (2, ["body-mip3.tga"]),
            (null, ["body.tga", "body-mip2.tga", "head-mip2.tga", "body-mip3.tga"]),
        })
        {
            var (obj, mtl, _) = ThroughText(ObjMeshExport.Export(mesh, new ObjMeshExportOptions { Lod = lod }));
            var back = ObjMeshImport.Import(obj, mtl, null, "body.obj");
            Assert.False(back.HasErrors, string.Join("; ", back.Issues));
            var sub = Assert.Single(back.Mesh!.Submeshes);
            Assert.Equal(textures, sub.Materials.Select(m => m.DiffuseMap.Text));
            Assert.Equal(textures.Order(StringComparer.Ordinal), sub.Lods.SelectMany(l => l.Textures).Select(t => t.FileName).Distinct().Order(StringComparer.Ordinal));
            if (lod is not null && textures.Length < 4) Assert.Contains(back.Issues, i => i.Code == "MI026" && i.Message.Contains("left out", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Round_trip_without_metadata_keeps_geometry_and_textures()
    {
        var mesh = SampleStatic();
        var result = ObjMeshExport.Export(mesh, new ObjMeshExportOptions { WriteMetadata = false });
        var (obj, mtl, _) = ThroughText(result);
        var back = ObjMeshImport.Import(obj, mtl, null, "export.obj");
        Assert.False(back.HasMetadata);
        Assert.False(back.HasErrors, string.Join("; ", back.Issues));
        var built = back.Mesh!;
        // Without the metadata every batch has the default render flags, double-sided becomes single-sided and the offset is baked in.
        static Dictionary<string, List<Tri>> Plain(V3dFile m) => Triangles(m).GroupBy(kv => string.Join("|", kv.Key.Split('|').Take(3)))
            .ToDictionary(g => g.Key, g => g.SelectMany(kv => kv.Value).Select(t => t with { DoubleSided = false }).ToList());
        Assert.Null(CompareUnordered(Plain(mesh), Plain(built)));
        Assert.All(built.Submeshes, s => Assert.Equal(Vector3.Zero, s.Offset));
        Assert.Equal(["crate01.tga", "glow.tga"], built.Submeshes.First().Materials.Select(m => m.DiffuseMap.Text));
    }

    [Fact]
    public void Missing_lod_takes_the_least_detailed_and_says_so()
    {
        var result = ObjMeshExport.Export(SampleStatic(), new ObjMeshExportOptions { Lod = 1 });
        Assert.Contains(result.Warnings, w => w.Contains("lid (LOD 0)", StringComparison.Ordinal));
        Assert.Equal(["crate", "lid"], result.Obj.Objects.Select(o => o.Name));
        var all = ObjMeshExport.Export(SampleStatic(), new ObjMeshExportOptions { Lod = null });
        Assert.Equal(["crate_LOD0", "crate_LOD1", "lid_LOD0"], all.Obj.Objects.Select(o => o.Name));
    }

    [Fact]
    public void Textures_are_decoded_to_png_and_named_in_the_mtl()
    {
        using var temp = new TempFolder();
        var tga = TestImages.Tga24(4, 2, 10, 20, 30);
        temp.Write("crate01.tga", tga);
        var resolver = new AssetResolver(new AssetResolverOptions { SearchFolders = [temp.Path] });
        string objPath = temp.File(Path.Combine("out", "crate.obj"));
        var result = ObjMeshExport.ExportToFile(objPath, SampleStatic(), new ObjMeshExportOptions { TextureResolver = resolver });
        Assert.Equal(["glow.tga"], result.MissingTextures.ToArray());
        var image = Assert.Single(result.Images);
        Assert.Equal("crate01.png", image.FileName);
        Assert.True(File.Exists(temp.File(Path.Combine("out", "crate01.png"))));
        Assert.True(File.Exists(temp.File(Path.Combine("out", "crate.mtl"))));
        var source = ObjMeshImport.ReadFile(objPath);
        Assert.Empty(source.MissingLibraries);
        Assert.Equal(["crate.mtl"], source.Obj.MaterialLibraries);
        Assert.Contains(source.Materials.Materials, m => m.DiffuseMap?.Path == "crate01.png");
        Assert.Contains(source.Materials.Materials, m => m.DiffuseMap?.Path == "glow.tga");
        // Importing the PNG names gives the RF names back (Cairn's metadata, and .tga by default without it).
        var back = ObjMeshImport.Import(source);
        Assert.Contains(back.Mesh!.Submeshes.First().Materials, m => m.DiffuseMap.Text == "crate01.tga");
        var plain = ObjMeshImport.Import(source, new ObjMeshImportOptions { UseMetadata = false });
        Assert.Contains(plain.Mesh!.Submeshes.First().Materials, m => m.DiffuseMap.Text == "crate01.tga");
        Assert.Equal(result.SideFiles(objPath).Order(), Directory.GetFiles(temp.File("out")).Where(f => !f.EndsWith(".obj", StringComparison.Ordinal)).Order());
    }

    // ── files from other tools ──────────────────────────────────────────────

    private static string Lf(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Blender 4.x "Wavefront (.obj)": o per object, quads, s 0, usemtl with a .mtl naming PNGs.</summary>
    private static readonly string BlenderObj = Lf(
        "# Blender 4.2.0",
        "# www.blender.org",
        "mtllib scene.mtl",
        "o Cube",
        "v 1.000000 1.000000 -1.000000",
        "v 1.000000 -1.000000 -1.000000",
        "v 1.000000 1.000000 1.000000",
        "v 1.000000 -1.000000 1.000000",
        "v -1.000000 1.000000 -1.000000",
        "v -1.000000 -1.000000 -1.000000",
        "v -1.000000 1.000000 1.000000",
        "v -1.000000 -1.000000 1.000000",
        "vn -0.0000 1.0000 -0.0000",
        "vn -0.0000 -0.0000 1.0000",
        "vn -1.0000 -0.0000 -0.0000",
        "vn -0.0000 -1.0000 -0.0000",
        "vn 1.0000 -0.0000 -0.0000",
        "vn -0.0000 -0.0000 -1.0000",
        "vt 0.625000 0.500000",
        "vt 0.875000 0.500000",
        "vt 0.875000 0.750000",
        "vt 0.625000 0.750000",
        "vt 0.375000 0.750000",
        "vt 0.625000 1.000000",
        "vt 0.375000 1.000000",
        "vt 0.375000 0.000000",
        "vt 0.625000 0.000000",
        "vt 0.625000 0.250000",
        "vt 0.375000 0.250000",
        "vt 0.125000 0.500000",
        "vt 0.375000 0.500000",
        "vt 0.125000 0.750000",
        "s 0",
        "usemtl Crate",
        "f 1/1/1 5/2/1 7/3/1 3/4/1",
        "f 4/5/2 3/4/2 7/6/2 8/7/2",
        "f 8/8/3 7/9/3 5/10/3 6/11/3",
        "f 6/12/4 2/13/4 4/5/4 8/14/4",
        "f 2/13/5 1/1/5 3/4/5 4/5/5",
        "f 6/11/6 5/10/6 1/1/6 2/13/6",
        "o Floor",
        "v -2.000000 -1.000000 2.000000",
        "v 2.000000 -1.000000 2.000000",
        "v -2.000000 -1.000000 -2.000000",
        "v 2.000000 -1.000000 -2.000000",
        "vn -0.0000 1.0000 -0.0000",
        "vt 0.000000 0.000000",
        "vt 1.000000 0.000000",
        "vt 1.000000 1.000000",
        "vt 0.000000 1.000000",
        "s 0",
        "usemtl Floor.001",
        "f 9/15/7 10/16/7 12/17/7 11/18/7");

    private static readonly string BlenderMtl = Lf(
        "# Blender 4.2.0 MTL File: 'scene.blend'",
        "# www.blender.org",
        "",
        "newmtl Crate",
        "Ns 250.000000",
        "Ka 1.000000 1.000000 1.000000",
        "Kd 0.800000 0.800000 0.800000",
        "Ks 0.500000 0.500000 0.500000",
        "Ke 0.000000 0.000000 0.000000",
        "Ni 1.450000",
        "d 1.000000",
        "illum 2",
        "map_Kd ..\\\\textures\\\\crate_wood.png",
        "",
        "newmtl Floor.001",
        "Ns 250.000000",
        "Kd 0.800000 0.800000 0.800000",
        "d 1.000000",
        "illum 2");

    private static ObjMeshImportResult ImportText(string obj, string? mtl, ObjMeshImportOptions? options = null) =>
        ObjMeshImport.Import(ObjReader.Read(obj, "test.obj"), mtl is null ? null : ObjReader.ReadMtl(mtl, "scene.mtl"), options, "test.obj");

    [Fact]
    public void Blender_obj_imports_as_a_static_mesh_with_correct_space_and_winding()
    {
        var result = ImportText(BlenderObj, BlenderMtl);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        Assert.False(result.HasMetadata);
        var mesh = result.Mesh!;
        Assert.Equal(V3dKind.StaticMesh, mesh.Kind);
        var subs = mesh.Submeshes.ToList();
        Assert.Equal(["Cube", "Floor"], subs.Select(s => s.Name.Text));
        Assert.Equal("crate_wood.tga", subs[0].Materials.Single().DiffuseMap.Text);
        // A material without a texture map names the texture after itself (Blender's ".001" is not an image extension).
        Assert.Equal("Floor.001.tga", subs[1].Materials.Single().DiffuseMap.Text);
        Assert.Contains(result.Issues, i => i.Code == "MI023");
        Assert.Contains(result.Issues, i => i.Code == "MI022");

        // 6 quads -> 12 triangles; vertices split by (position, UV, normal): 24 for the flat-shaded cube.
        var cube = subs[0].Lods.Single().Batches.Single();
        Assert.Equal(12, cube.Triangles.Length);
        Assert.Equal(24, cube.Positions.Length);
        // Blender (1, 1, -1) is RF (-1, 1, -1); every triangle's plane faces the way its vertex normals point.
        Assert.Contains(new Vector3(-1, 1, -1), cube.Positions);
        foreach (var batch in subs.SelectMany(s => s.Lods).SelectMany(l => l.Batches))
        {
            for (int t = 0; t < batch.Triangles.Length; t++)
            {
                var tri = batch.Triangles[t];
                Assert.True(Vector3.Dot(batch.Planes[t].Normal, batch.Normals[tri.A]) > 0.99f, $"triangle {t} faces its normals");
            }
        }
        // The floor faces up in RF too, and its UVs are flipped to RF's top-down V.
        var floor = subs[1].Lods.Single().Batches.Single();
        Assert.All(floor.Planes, p => Assert.True(p.Normal.Y > 0.99f));
        Assert.Contains(new Vector2(0, 1), floor.TexCoords);
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Max_style_groups_without_normals_get_smoothing_group_normals()
    {
        // 3ds Max: "g" per object, no "o", no vn; s 1 smooths, s off is flat. A pyramid side shares a vertex with the base.
        string obj = Lf(
            "# 3ds Max Wavefront OBJ Exporter v0.97b",
            "v 0 0 0", "v 1 0 0", "v 1 0 1", "v 0 0 1", "v 0.5 1 0.5",
            "vt 0 0", "vt 1 0", "vt 1 1", "vt 0 1",
            "g Pyramid01",
            "usemtl stone",
            "s 1",
            "f 1/1 5/3 2/2",
            "f 2/2 5/3 3/3",
            "s off",
            "f 1/1 2/2 3/3 4/4");
        var result = ImportText(obj, null);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        Assert.Contains(result.Issues, i => i.Code == "MI009");
        var sub = Assert.Single(result.Mesh!.Submeshes);
        Assert.Equal("Pyramid01", sub.Name.Text);
        Assert.Equal("stone.tga", sub.Materials.Single().DiffuseMap.Text);
        var batch = sub.Lods.Single().Batches.Single();
        // The apex is shared by the two smooth faces (one vertex, averaged normal); the base is flat (its own vertices).
        int apex = batch.Positions.Select((p, i) => (p, i)).Count(x => x.p == new Vector3(-0.5f, 1, 0.5f));
        Assert.Equal(1, apex);
        var baseNormals = batch.Triangles.Skip(2).SelectMany(t => new[] { t.A, t.B, t.C }).Select(i => batch.Normals[i]).ToList();
        Assert.All(baseNormals, n => Assert.True(Vector3.Distance(n, -Vector3.UnitY) < 1e-5f, $"base normal {n}"));
        for (int t = 0; t < batch.Triangles.Length; t++)
            Assert.True(Vector3.Dot(batch.Planes[t].Normal, batch.Normals[batch.Triangles[t].A]) > 0f);
    }

    [Fact]
    public void Z_up_files_turn_upright()
    {
        string obj = Lf("v 0 0 0", "v 1 0 0", "v 0 1 0", "v 0 0 1", "vn 0 0 1", "f 1//1 2//1 3//1");
        var upright = ImportText(obj, null, new ObjMeshImportOptions { ZUp = true });
        var batch = upright.Mesh!.Submeshes.Single().Lods.Single().Batches.Single();
        Assert.All(batch.Normals, n => Assert.Equal(Vector3.UnitY, n));
        Assert.All(batch.Planes, p => Assert.True(p.Normal.Y > 0.99f));
        Assert.Contains(upright.Issues, i => i.Code == "MI027");
        Assert.Equal("default", upright.Mesh!.Submeshes.Single().Name.Text);
    }

    [Fact]
    public void Report_lists_what_was_dropped_or_assumed()
    {
        string obj = Lf("mtllib gone.mtl", "v 0 0 0", "v 1 0 0", "v 0 1 0", "v 0 0 0 1 0 0", "l 1 2", "p 3", "o a_name_that_is_far_too_long_for_rf", "f 1 2 3");
        var result = ObjMeshImport.Import(ObjReader.Read(obj, "x.obj"), null, null, "x.obj", ["gone.mtl"]);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        var codes = result.Issues.Select(i => i.Code).ToList();
        Assert.Contains("MI021", codes);
        Assert.Contains("MI020", codes);
        Assert.Contains("MI010", codes);
        Assert.Contains("MI009", codes);
        Assert.Contains("MI022", codes);
        Assert.Contains(result.Issues, i => i.Code == "MI002" && i.Severity == MeshImportSeverity.Warning);
        var sub = Assert.Single(result.Mesh!.Submeshes);
        Assert.Equal(V3dSubmesh.NameSize - 1, sub.Name.Text.Length);
        Assert.Equal("default.tga", sub.Materials.Single().DiffuseMap.Text);
    }

    [Fact]
    public void Errors_block_the_mesh()
    {
        var empty = ImportText(Lf("v 0 0 0", "v 1 0 0"), null);
        Assert.True(empty.HasErrors);
        Assert.Null(empty.Mesh);
        Assert.Contains(empty.Issues, i => i.Code == "MI013");

        var longTexture = ImportText(Lf("v 0 0 0", "v 1 0 0", "v 0 1 0", "usemtl m", "f 1 2 3"), Lf("newmtl m", "map_Kd an_extremely_long_texture_name_for_rf.png"));
        Assert.True(longTexture.HasErrors);
        Assert.Null(longTexture.Mesh);
        Assert.Contains(longTexture.Issues, i => i.Code == "MI002" && i.Severity == MeshImportSeverity.Error);

        // Eight textures in one LOD are over the engine's seven.
        var lines = new List<string> { "v 0 0 0", "v 1 0 0", "v 0 1 0" };
        for (int i = 0; i < 8; i++) lines.AddRange([$"usemtl t{i}", "f 1 2 3"]);
        var many = ImportText(Lf([.. lines]), null);
        Assert.Contains(many.Issues, i => i.Code == "MI004" && i.Severity == MeshImportSeverity.Error);
    }

    [Fact]
    public void Foreign_rf_comments_without_the_marker_are_ignored()
    {
        string obj = Lf("# rf_submesh index=0 name=evil lod=2", "o thing", "# rf_submesh index=0 name=evil lod=2", "v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3");
        var result = ImportText(obj, null);
        Assert.False(result.HasMetadata);
        var sub = Assert.Single(result.Mesh!.Submeshes);
        Assert.Equal("thing", sub.Name.Text);
        Assert.Single(sub.Lods);
    }

    [Fact]
    public void Damaged_metadata_falls_back_to_defaults()
    {
        var (obj, mtl, text) = ThroughText(ObjMeshExport.Export(SampleStatic()));
        string damaged = text.Replace("# rf_slot submesh=0 slot=1", "# rf_slot submesh=zero slot=1", StringComparison.Ordinal);
        var result = ObjMeshImport.Import(ObjReader.Read(damaged, "export.obj"), mtl);
        Assert.False(result.HasErrors, string.Join("; ", result.Issues));
        Assert.Contains(result.Issues, i => i.Code == "MI026" && i.Severity == MeshImportSeverity.Warning);
        Assert.NotNull(result.Mesh);
        _ = obj;
    }

    // ── characters ──────────────────────────────────────────────────────────

    [Fact]
    public void Character_with_two_objects_warns_that_only_the_last_draws()
    {
        var character = V3dBuilder.Build(V3dBuilderTests.SampleDescription());
        string one = Lf("o a", "v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3");
        string two = Lf("o a", "v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3", "o b", "v 0 0 1", "v 1 0 1", "v 0 1 1", "f 4 5 6");
        var options = new ObjMeshImportOptions { KeepSkeletonFrom = character };
        Assert.DoesNotContain(ImportText(one, null, options).Issues, i => i.Code == "MI028");
        var issue = Assert.Single(ImportText(two, null, options).Issues, i => i.Code == "MI028");
        Assert.Equal(MeshImportSeverity.Warning, issue.Severity);
        Assert.DoesNotContain(ImportText(two, null).Issues, i => i.Code == "MI028");
    }

    [Fact]
    public void Character_exports_its_bind_pose_and_imports_onto_a_kept_skeleton()
    {
        var description = V3dBuilderTests.SampleDescription();
        var character = V3dBuilder.Build(description);
        var result = ObjMeshExport.Export(character, new ObjMeshExportOptions { Lod = null });
        Assert.Contains(result.Warnings, w => w.Contains("skeleton", StringComparison.Ordinal));
        var (obj, mtl, text) = ThroughText(result);
        Assert.Contains("kind=character", text, StringComparison.Ordinal);

        var bare = ObjMeshImport.Import(obj, mtl);
        Assert.Equal(V3dKind.Character, bare.Mesh!.Kind);
        Assert.Contains(bare.Issues, i => i.Code == "MI017");

        var kept = ObjMeshImport.Import(obj, mtl, new ObjMeshImportOptions { KeepSkeletonFrom = character });
        Assert.False(kept.HasErrors, string.Join("; ", kept.Issues));
        var mesh = kept.Mesh!;
        Assert.Equal(character.Bones.ToArray(), mesh.Bones.ToArray());
        Assert.Equal(character.CollisionSpheres.ToArray(), mesh.CollisionSpheres.ToArray());
        Assert.Contains(kept.Issues, i => i.Code == "MI025");
        Assert.Null(CompareOrdered(Triangles(character, null), Triangles(mesh, null, partition: false)));
        // The bottom row is nearest the root, the top row nearest "upper" (its rest position is y = 1).
        var batch = mesh.Submeshes.First().Lods[0].Batches.First();
        for (int i = 0; i < batch.Positions.Length; i++)
            Assert.Equal(batch.Positions[i].Y >= 0.5f ? 1 : 0, batch.BoneLinks[i].GetBone(0));
        Assert.DoesNotContain(MeshLinter.Analyze(mesh), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Committed_sample_figure_round_trips()
    {
        if (TestPaths.Samples is not { } samples) return;
        string path = Path.Combine(samples, "sample_figure.v3c");
        if (!File.Exists(path)) return;
        var figure = V3dReader.ReadFile(path);
        var (obj, mtl, _) = ThroughText(ObjMeshExport.Export(figure, new ObjMeshExportOptions { Lod = null, SourceName = "sample_figure.v3c" }));
        var back = ObjMeshImport.Import(obj, mtl, new ObjMeshImportOptions { KeepSkeletonFrom = figure });
        Assert.False(back.HasErrors, string.Join("; ", back.Issues));
        Assert.Null(CompareOrdered(Triangles(figure, null), Triangles(back.Mesh!, null, partition: false)));
        Assert.Equal(Skeleton.FromFile(figure).Names.ToArray(), Skeleton.FromFile(back.Mesh!).Names.ToArray());
    }

    // ── stock meshes (bounded) ──────────────────────────────────────────────

    /// <summary>
    /// v3m → obj → v3m on a spread of the stock static meshes (every 12th by name, plus every 8th character at LOD 0),
    /// through the text, with Cairn's metadata: the same triangles in the same order and winding, positions, UVs and
    /// normals within 2e-5 (relative), the same materials and prop points. CAIRN_FUZZ_SCALE widens the sample.
    /// </summary>
    [Fact]
    public void Stock_meshes_round_trip()
    {
        if (TestPaths.Corpus is not { } corpus) return;
        int scale = int.TryParse(Environment.GetEnvironmentVariable("CAIRN_FUZZ_SCALE"), out int s) && s > 0 ? s : 1;
        var statics = Directory.GetFiles(corpus, "*.v3m").Order(StringComparer.OrdinalIgnoreCase).Where((_, i) => i % Math.Max(1, 12 / scale) == 0);
        var characters = Directory.GetFiles(corpus, "*.v3c").Order(StringComparer.OrdinalIgnoreCase).Where((_, i) => i % Math.Max(1, 8 / scale) == 0);
        int checkedFiles = 0, triangles = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (string file in statics.Concat(characters))
        {
            var mesh = V3dReader.ReadFile(file);
            int? lod = mesh.Kind == V3dKind.Character ? 0 : null;
            var (obj, mtl, _) = ThroughText(ObjMeshExport.Export(mesh, new ObjMeshExportOptions { Lod = lod, SourceName = Path.GetFileName(file) }));
            var back = ObjMeshImport.Import(obj, mtl, new ObjMeshImportOptions { KeepSkeletonFrom = mesh.Kind == V3dKind.Character ? mesh : null }, Path.GetFileName(file));
            Assert.True(back.Mesh is not null, $"{Path.GetFileName(file)}: {string.Join("; ", back.Issues)}");
            var want = Triangles(mesh, lod);
            Assert.Null(CompareOrdered(want, Triangles(back.Mesh!, lod, partition: false)) is { } diff ? $"{Path.GetFileName(file)}: {diff}" : null);
            var a = mesh.Submeshes.ToList();
            var b = back.Mesh!.Submeshes.Where(x => x.Lods.Length > 0).ToList();
            Assert.Equal(a.Where(x => x.Lods.Any(l => l.Batches.Any(t => t.TriangleCount > 0))).Select(x => x.Name.Text), b.Select(x => x.Name.Text));
            if (mesh.Kind == V3dKind.StaticMesh)
            {
                var props = a.SelectMany(x => x.Lods).FirstOrDefault(l => l.PropPoints.Length > 0)?.PropPoints ?? [];
                // Names by text: stock name fields carry leftover bytes after the terminator, which are not exported.
                static object Plain(V3dPropPoint p) => (p.Name.Text, p.Rotation, p.Position, p.ParentIndex);
                Assert.Equal(props.Select(Plain).ToArray(), b.First().Lods[0].PropPoints.Select(Plain).ToArray());
            }
            checkedFiles++;
            triangles += want.Values.Sum(v => v.Count);
        }
        output.WriteLine($"{checkedFiles} stock meshes, {triangles:N0} triangles round-tripped in {clock.ElapsedMilliseconds} ms");
    }
}
