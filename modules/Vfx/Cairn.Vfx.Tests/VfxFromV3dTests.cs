using System.Numerics;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Linting;
using Cairn.Workspace;
using Xunit.Abstractions;

namespace Cairn.Vfx.Tests;

public sealed class VfxFromV3dTests(ITestOutputHelper output)
{
    /// <summary>A spread of stock meshes: every 40th .v3m and every 10th .v3c by name.</summary>
    private static IEnumerable<string> Sample()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return [];
        string[] Files(string pattern) => [.. Directory.GetFiles(dir, pattern).Order(StringComparer.OrdinalIgnoreCase)];
        return Files("*.v3m").Where((_, i) => i % 40 == 0).Concat(Files("*.v3c").Where((_, i) => i % 10 == 0));
    }

    /// <summary>Per source triangle (batch order), the average of its stored corner normals.</summary>
    private static List<Vector3> CornerNormals(V3dSubmesh submesh, int lodIndex = 0)
    {
        var lod = submesh.Lods[Math.Min(lodIndex, submesh.Lods.Length - 1)];
        var list = new List<Vector3>();
        foreach (var b in lod.Batches)
            foreach (var t in b.Triangles)
                list.Add(b.Normals[t.A] + b.Normals[t.B] + b.Normals[t.C]);
        return list;
    }

    [Fact]
    public void StockMeshesConvertWriteAndLintClean()
    {
        int files = 0, faces = 0, agree = 0;
        foreach (var path in Sample())
        {
            var v3d = V3dReader.ReadFile(path);
            var submeshes = VfxFromV3d.Select(v3d);
            var built = VfxFromV3d.CreateFile(v3d);
            var vfx = VfxReader.Read(VfxWriter.Write(built), "x.vfx");
            var meshes = vfx.Sections.OfType<VfxMesh>().ToList();
            Assert.Equal(submeshes.Count, meshes.Count);
            Assert.Equal(meshes.Count, meshes.Select(m => m.Name).Distinct().Count());
            for (int s = 0; s < submeshes.Count; s++)
            {
                var lod = submeshes[s].Lods[0];
                var mesh = meshes[s];
                Assert.Equal(lod.Batches.Sum(b => b.TriangleCount), mesh.Faces.Length);
                Assert.InRange(mesh.NumVertices, 1, lod.Batches.Sum(b => b.VertexCount));
                Assert.Equal(0u, mesh.Flags & VfxMeshFlags.Fullbright);
                Assert.Equal(lod.Textures.Select(t => t.FileName.ToLowerInvariant()).Distinct().Count(), mesh.MaterialIndices!.Value.Length);
                var normals = CornerNormals(submeshes[s]);
                for (int f = 0; f < mesh.Faces.Length; f++)
                {
                    faces++;
                    if (Vector3.Dot(mesh.Faces[f].Normal, normals[f]) > 0) agree++;
                }
            }
            var errors = VfxLinter.Lint(vfx).Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            Assert.True(errors.Count == 0, $"{Path.GetFileName(path)}: {string.Join("; ", errors)}");
            files++;
        }
        if (files == 0) return;
        output.WriteLine($"{files} meshes, {faces} faces, {agree} face normals agree with the stored vertex normals ({100.0 * agree / faces:F1}%)");
        Assert.True(agree > 0.9 * faces, $"only {agree} of {faces} faces agree");
    }

    [Fact]
    public void WindingAgreesAcrossCorpus()
    {
        if (LocalPaths.Corpus is not { } dir || !Directory.Exists(dir)) return;
        int faces = 0, agree = 0;
        foreach (var path in Directory.GetFiles(dir, "*.v3?"))
        {
            var v3d = V3dReader.ReadFile(path);
            var file = VfxFromV3d.CreateFile(v3d);
            var meshes = file.Sections.OfType<VfxMesh>().ToList();
            var submeshes = VfxFromV3d.Select(v3d);
            for (int s = 0; s < submeshes.Count; s++)
            {
                var normals = CornerNormals(submeshes[s]);
                for (int f = 0; f < meshes[s].Faces.Length; f++)
                {
                    if (normals[f].LengthSquared() < 1e-6f) continue;
                    faces++;
                    if (Vector3.Dot(meshes[s].Faces[f].Normal, normals[f]) > 0) agree++;
                }
            }
        }
        output.WriteLine($"{faces} faces, {agree} agree ({100.0 * agree / Math.Max(faces, 1):F2}%)");
        Assert.True(agree > 0.95 * faces);
    }

    [Fact]
    public void AddToReusesMaterialsAndMakesNamesUnique()
    {
        var path = Sample().FirstOrDefault(p => V3dReader.ReadFile(p).Submeshes.Any(s => s.Lods.Length > 0 && s.Lods[0].Textures.Length > 0));
        if (path is null) return;
        var v3d = V3dReader.ReadFile(path);
        var first = VfxFromV3d.CreateFile(v3d, new VfxFromV3dOptions { SubmeshIndex = 0 });
        var result = VfxFromV3d.Add(first, v3d, new VfxFromV3dOptions { SubmeshName = v3d.Submeshes.First().Name.Text, LodIndex = 9 });
        Assert.Single(result.MeshSections);
        var meshes = result.File.Sections.OfType<VfxMesh>().ToList();
        Assert.Equal(2, meshes.Count);
        Assert.Equal($"{meshes[0].Name}_2", meshes[1].Name);
        Assert.Same(meshes[1], result.File.Sections[result.MeshSections[0]]);
        Assert.Equal(first.Sections.OfType<VfxMaterial>().Count(), result.File.Sections.OfType<VfxMaterial>().Count());
        Assert.Throws<ArgumentException>(() => VfxFromV3d.Add(first, v3d, new VfxFromV3dOptions { SubmeshIndex = 99 }));
        Assert.DoesNotContain(VfxLinter.Lint(result.File), d => d.Severity == DiagnosticSeverity.Error);
    }
}
