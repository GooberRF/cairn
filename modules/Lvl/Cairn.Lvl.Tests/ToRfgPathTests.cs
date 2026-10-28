using Cairn.Formats.Obj;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Interchange;
using Cairn.Workspace;
using static Cairn.Lvl.Tests.LvlConvertTestData;

namespace Cairn.Lvl.Tests;

/// <summary>
/// The same source by different paths to .rfg: a mesh directly, through its glTF and through its OBJ give the same
/// brushes (one level of detail, double-sided triangles both ways), and a level's untextured faces and odd texture
/// names come back as they were through Cairn's own glTF and OBJ.
/// </summary>
public sealed class ToRfgPathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cairn-lvl-paths-" + Guid.NewGuid().ToString("N"));

    public ToRfgPathTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static V3dMaterialGroup Triangles(float y, params ushort[] flags)
    {
        var vertices = new List<V3dMeshVertex>();
        var triangles = new List<V3dMeshTriangle>();
        for (int i = 0; i < flags.Length; i++)
        {
            int a = vertices.Count;
            vertices.Add(new V3dMeshVertex(new Vector3(i * 2, y, 0), Vector3.UnitY, Vector2.Zero));
            vertices.Add(new V3dMeshVertex(new Vector3(i * 2 + 1, y, 0), Vector3.UnitY, Vector2.UnitX));
            vertices.Add(new V3dMeshVertex(new Vector3(i * 2, y, 1), Vector3.UnitY, Vector2.UnitY));
            triangles.Add(new V3dMeshTriangle(a, a + 1, a + 2, flags[i]));
        }
        return new V3dMaterialGroup { Vertices = [.. vertices], Triangles = [.. triangles] };
    }

    /// <summary>One submesh with two levels of detail: LOD 0 has a one-sided and a double-sided triangle, LOD 1 one triangle.</summary>
    private static V3dFile TwoLodMesh()
    {
        var material = new V3dMaterial(Formats.FixedString.FromText("leaf.tga", 32), 0, 0, 0, 0, Formats.FixedString.FromText("", 32), 0);
        var description = new V3dMeshDescription
        {
            Kind = V3dKind.StaticMesh,
            Submeshes =
            [
                new V3dSubmeshDescription
                {
                    Offset = new Vector3(1, 2, 3),
                    Materials = [material],
                    Lods =
                    [
                        new V3dLodDescription { Groups = [Triangles(0, 0, V3dTriangle.DoubleSided)] },
                        new V3dLodDescription { Distance = 10, Groups = [Triangles(0, 0)] },
                    ],
                },
            ],
        };
        return V3dBuilder.Build(description, V3dBuildOptions.Default);
    }

    private static RfGroupFile Group(LvlConversion conversion)
    {
        Assert.True(conversion.HasOutput, conversion.Report.ToText());
        return Assert.IsType<RfGroupFile>(conversion.Output);
    }

    private static (int Brushes, int Faces) Count(RfGroupFile file) =>
        (file.Groups.Sum(g => g.Brushes.Length), file.Groups.Sum(g => g.Brushes.Sum(b => b.Geometry.Faces.Length)));

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    [InlineData(4, 1)]
    public void A_mesh_gltf_gives_one_brush_from_the_chosen_lod_like_the_mesh(int lod, int faces)
    {
        var mesh = TwoLodMesh();
        var options = new RfgImportOptions { Lod = lod };
        Assert.Equal((1, faces), Count(Group(LvlConverter.MeshToRfg(mesh, "leaf.v3m", options))));
        var conversion = LvlConverter.GltfToRfg(GltfExport.Export(mesh).Document, "leaf.glb", options);
        Assert.Equal((1, faces), Count(Group(conversion)));
        Assert.DoesNotContain(conversion.Report.Issues, i => i.Severity != LvlIssueSeverity.Info && i.Code == LvlConvertCodes.ObjectsDropped);
        Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.FaceData), i => i.Message.Contains("other levels of detail", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 1)]
    public void A_mesh_obj_keeps_double_sided_triangles_and_one_lod_like_the_mesh(int lod, int faces)
    {
        var mesh = TwoLodMesh();
        // One LOD exported, then every LOD exported (an object per LOD).
        foreach (int? exported in (int?[])[lod, null])
        {
            var obj = ObjMeshExport.Export(mesh, new ObjMeshExportOptions { Lod = exported });
            var conversion = LvlConverter.ObjToRfg(obj.Obj, obj.Mtl, "leaf.obj", new RfgImportOptions { Lod = lod });
            Assert.Equal((1, faces), Count(Group(conversion)));
            if (lod == 0) Assert.Contains(conversion.Report.WithCode(LvlRfgCodes.DoubleSided), _ => true);
        }
    }

    [Fact]
    public void Stock_meshes_give_the_same_faces_directly_through_gltf_and_through_obj()
    {
        if (LocalPaths.GameDirectory is not { } dir || !System.IO.File.Exists(Path.Combine(dir, "meshes.vpp"))) return;
        string pack = Path.Combine(dir, "meshes.vpp");
        var samples = LvlSamples.PackEntries(pack, ".v3m").Where(e => e.Sample.Name.EndsWith("Port_Light01.v3m", StringComparison.OrdinalIgnoreCase)).Take(1)
            .Concat(LvlSamples.PackEntries(pack, ".v3c").Take(3)).Select(e => e.Sample);
        foreach (var s in samples)
        {
            var mesh = V3dReader.Read(s.Load(), s.Name);
            var direct = Count(Group(LvlConverter.MeshToRfg(mesh, s.Name)));
            Assert.Equal(direct, Count(Group(LvlConverter.GltfToRfg(GltfExport.Export(mesh).Document, s.Name))));
            var obj = ObjMeshExport.Export(mesh);
            Assert.Equal(direct, Count(Group(LvlConverter.ObjToRfg(obj.Obj, obj.Mtl, s.Name))));
        }
    }

    private static RfLevel OneBrush(string[]? textures = null) =>
        Level(brushes: [Brush(7, Box(new Vector3(1, 2, 3), textures ?? ["wall.tga"], (k, f) => k == 0 ? f with { Texture = -1 } : f), new Vector3(5, 6, 7))]);

    private static List<string> FaceTextures(RfGroupFile file)
    {
        var brush = file.Groups.SelectMany(g => g.Brushes).Single();
        return [.. brush.Geometry.Faces.Select(f => brush.Geometry.TextureOf(f) ?? "(none)")];
    }

    [Fact]
    public void Untextured_faces_stay_untextured_through_gltf_and_obj()
    {
        var input = LvlConvertInput.FromLevel(OneBrush());
        var gltf = LvlConverter.Convert(input, new LvlConvertOptions { Target = LvlConvertTarget.Glb, Geometry = LvlGeometryChoice.Brushes }).Gltf!;
        var viaGltf = FaceTextures(Group(LvlConverter.GltfToRfg(gltf, "x.glb")));
        Assert.Equal(["(none)", "wall.tga", "wall.tga", "wall.tga", "wall.tga", "wall.tga"], viaGltf);

        foreach (bool metadata in (bool[])[true, false])
        {
            var obj = LvlConverter.Convert(input, new LvlConvertOptions { Target = LvlConvertTarget.Obj, Geometry = LvlGeometryChoice.Brushes, ObjMetadata = metadata }).Obj!;
            var viaObj = FaceTextures(Group(LvlConverter.ObjToRfg(obj.Obj, obj.Mtl, "x.obj")));
            Assert.Equal("(none)", viaObj[0]);
            Assert.DoesNotContain("untextured.tga", viaObj);
        }
    }

    [Fact]
    public void A_texture_name_with_a_line_break_writes_obj_and_reads_back()
    {
        const string name = "bad\nname.tga";
        string path = Path.Combine(_folder, "nl.obj");
        var result = LvlConverter.ConvertToFile(LvlConvertInput.FromLevel(OneBrush([name])), path,
            new LvlConvertOptions { Target = LvlConvertTarget.Obj, Geometry = LvlGeometryChoice.Brushes, ExportTextures = false });
        Assert.Equal(path, result.Files[0]);
        var obj = ObjReader.ReadFile(path);
        var (mtl, missing) = ObjReader.ReadMaterialLibraries(obj, _folder);
        Assert.Empty(missing);
        Assert.Contains(name, FaceTextures(Group(LvlConverter.ObjToRfg(obj, mtl, "nl.obj"))));
    }

    [Fact]
    public void Metadata_text_keeps_line_breaks_quotes_and_backslashes_on_one_line()
    {
        const string text = "a\r\nb\"c\\d\\n";
        string line = LvlObjMetadata.Line("rf_texture", ("name", LvlObjMetadata.Text(text)));
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Equal(text, LvlObjMetadata.Parse(line)!.Value.Values["name"]);
    }
}
