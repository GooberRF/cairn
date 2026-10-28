using System.Diagnostics;
using System.Numerics;
using Cairn.Formats.Obj;

namespace Cairn.Rfa.Tests;

/// <summary>
/// The OBJ reader on files shaped like Blender, 3ds Max and Maya output (written by the tests, in the
/// layout those exporters use), and on the real .obj/.mtl files of the local research data when present.
/// </summary>
public class ObjSampleTests
{
    private static string Lf(params string[] lines) => string.Join("\n", lines) + "\n";

    private static string CrLf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    /// <summary>Blender 4.x "Wavefront (.obj)" export: LF, header comments, o per object, s 0/1, quads.</summary>
    private static readonly string BlenderObj = Lf(
        "# Blender 4.2.0",
        "# www.blender.org",
        "mtllib crate.mtl",
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
        "o Plane",
        "v -1.000000 0.000000 1.000000",
        "v 1.000000 0.000000 1.000000",
        "v -1.000000 0.000000 -1.000000",
        "v 1.000000 0.000000 -1.000000",
        "vn -0.0000 1.0000 -0.0000",
        "vt 0.000000 0.000000",
        "vt 1.000000 0.000000",
        "vt 1.000000 1.000000",
        "vt 0.000000 1.000000",
        "s 1",
        "usemtl Floor.001",
        "f 9/15/7 10/16/7 12/17/7 11/18/7");

    private static readonly string BlenderMtl = Lf(
        "# Blender 4.2.0 MTL File: 'crate.blend'",
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
        "map_Kd textures/crate wood.png",
        "",
        "newmtl Floor.001",
        "Ns 250.000000",
        "Kd 0.200000 0.300000 0.400000",
        "d 0.500000",
        "illum 2",
        "map_Kd -s 2.000000 2.000000 1.000000 floor.tga");

    /// <summary>3ds Max (gw::OBJ-Exporter) layout: CRLF, double spaces, trailing spaces, vt with w, g per object, s n.</summary>
    private static readonly string MaxObj = CrLf(
        "# 3ds Max Wavefront OBJ Exporter v0.97b - (c)2007 guruware",
        "# File Created: 25.10.2026 12:00:00",
        "",
        "mtllib boxes.mtl",
        "",
        "#",
        "# object Box001",
        "#",
        "",
        "v  -10.0000 0.0000 10.0000",
        "v  -10.0000 0.0000 -10.0000",
        "v  10.0000 0.0000 -10.0000",
        "v  10.0000 0.0000 10.0000",
        "v  -10.0000 20.0000 10.0000",
        "v  10.0000 20.0000 10.0000",
        "# 6 vertices",
        "",
        "vn 0.0000 -1.0000 -0.0000",
        "vn 0.0000 0.0000 1.0000",
        "# 2 vertex normals",
        "",
        "vt 1.0000 0.0000 0.0000",
        "vt 1.0000 1.0000 0.0000",
        "vt 0.0000 1.0000 0.0000",
        "vt 0.0000 0.0000 0.0000",
        "# 4 texture coords",
        "",
        "g Box001",
        "usemtl Material__25",
        "s 2",
        "f 1/1/1 2/2/1 3/3/1 4/4/1 ",
        "s 4",
        "f 1/4/2 4/1/2 6/2/2 5/3/2 ",
        "# 2 polygons",
        "",
        "#",
        "# object Box002",
        "#",
        "",
        "v  30.0000 0.0000 0.0000",
        "v  40.0000 0.0000 0.0000",
        "v  40.0000 10.0000 0.0000",
        "# 3 vertices",
        "",
        "vn 0.0000 0.0000 1.0000",
        "# 1 vertex normals",
        "",
        "vt 0.0000 0.0000 0.0000",
        "vt 1.0000 0.0000 0.0000",
        "vt 1.0000 1.0000 0.0000",
        "# 3 texture coords",
        "",
        "g Box002",
        "usemtl Material__26",
        "s off",
        "f 7/5/3 8/6/3 9/7/3 ",
        "# 1 polygons",
        "");

    private static readonly string MaxMtl = CrLf(
        "# 3ds Max Wavefront OBJ Exporter v0.97b - (c)2007 guruware",
        "# File Created: 25.10.2026 12:00:00",
        "",
        "newmtl Material__25",
        "\tNs 10.0000",
        "\tNi 1.5000",
        "\td 1.0000",
        "\tTr 0.0000",
        "\tTf 1.0000 1.0000 1.0000 ",
        "\tillum 2",
        "\tKa 0.5880 0.5880 0.5880",
        "\tKd 0.5880 0.5880 0.5880",
        "\tKs 0.0000 0.0000 0.0000",
        "\tKe 0.0000 0.0000 0.0000",
        "\tmap_Ka ..\\maps\\crate.tga",
        "\tmap_Kd ..\\maps\\crate.tga",
        "",
        "newmtl Material__26",
        "\tNs 10.0000",
        "\tTr 0.2500",
        "\tKd 1.0000 0.0000 0.0000");

    /// <summary>Maya layout: "g default" before the vertices, "s off", hierarchical group names, a continued face line.</summary>
    private static readonly string MayaObj = Lf(
        "# This file uses centimeters as units for non-parametric coordinates.",
        "",
        "mtllib wall.mtl",
        "g default",
        "v -0.5 -0.5 0.5",
        "v 0.5 -0.5 0.5",
        "v 0.5 0.5 0.5",
        "v -0.5 0.5 0.5",
        "v 0 0.75 0.5",
        "vt 0 0",
        "vt 1 0",
        "vt 1 1",
        "vt 0 1",
        "vt 0.5 1.25",
        "vn 0 0 1",
        "vn 0 0 1",
        "vn 0 0 1",
        "vn 0 0 1",
        "vn 0 0 1",
        "s off",
        "g pWall1 walls",
        "usemtl initialShadingGroup",
        "f 1/1/1 2/2/2 3/3/3 \\",
        "  5/5/5 4/4/4");

    [Fact]
    public void ReadsBlenderStyleOutput()
    {
        using var temp = new TempFolder();
        string obj = temp.Write("crate.obj", BlenderObj);
        temp.Write("crate.mtl", BlenderMtl);

        var doc = ObjReader.ReadFile(obj);
        Assert.Equal(["Cube", "Plane"], doc.Objects.Select(o => o.Name));
        Assert.Equal((12, 18, 7), (doc.Positions.Count, doc.TexCoords.Count, doc.Normals.Count));
        Assert.Equal(["Blender 4.2.0", "www.blender.org"], doc.Comments.Select(c => c.Text));
        var cube = doc.Objects[0].Faces;
        Assert.Equal(6, cube.Count);
        Assert.All(cube, f => Assert.Equal((4, "Crate", 0), (f.Corners.Count, f.Material, f.SmoothingGroup)));
        Assert.Equal(new ObjCorner(0, 0, 0), cube[0].Corners[0]);
        var plane = Assert.Single(doc.Objects[1].Faces);
        Assert.Equal(("Floor.001", 1), (plane.Material, plane.SmoothingGroup));
        Assert.Equal(new ObjCorner(11, 16, 6), plane.Corners[2]);
        Assert.Equal(new Vector3(1, 0, -1), doc.Positions[11]);
        Assert.Equal(["Cube", "Plane"], doc.SplitIntoParts().Select(p => p.Name));

        var (mtl, missing) = ObjReader.ReadMaterialLibraries(doc, temp.Path);
        Assert.Empty(missing);
        var crate = mtl.Find("Crate")!;
        Assert.Equal(250f, crate.SpecularExponent);
        Assert.Equal(new Vector3(0.8f), crate.Diffuse);
        Assert.Equal(1f, crate.Dissolve);
        Assert.Equal("crate wood.png", crate.DiffuseMap!.FileName);
        Assert.Equal([new("Ni", "1.450000")], crate.OtherStatements);
        var floor = mtl.Find("Floor.001")!;
        Assert.Equal(0.5f, floor.Dissolve);
        Assert.Equal(new MtlTextureMap("floor.tga", "-s 2.000000 2.000000 1.000000"), floor.DiffuseMap);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void ReadsMaxStyleOutput()
    {
        using var temp = new TempFolder();
        string obj = temp.Write("boxes.obj", MaxObj);
        temp.Write("boxes.mtl", MaxMtl);

        var doc = ObjReader.ReadFile(obj);
        // No "o" lines: everything is in the unnamed object, split by group.
        var only = Assert.Single(doc.Objects);
        Assert.Equal("", only.Name);
        Assert.Equal((9, 7, 3), (doc.Positions.Count, doc.TexCoords.Count, doc.Normals.Count));
        Assert.Equal(new Vector3(-10, 20, 10), doc.Positions[4]);
        Assert.Equal(new Vector2(1, 1), doc.TexCoords[1]);
        Assert.Equal([2, 4, 0], only.Faces.Select(f => f.SmoothingGroup));
        Assert.Equal(["Material__25", "Material__25", "Material__26"], only.Faces.Select(f => f.Material));
        Assert.Equal([new ObjCorner(6, 4, 2), new ObjCorner(7, 5, 2), new ObjCorner(8, 6, 2)], only.Faces[2].Corners);
        var parts = doc.SplitIntoParts();
        Assert.Equal(["Box001", "Box002"], parts.Select(p => p.Name));
        Assert.Equal([2, 1], parts.Select(p => p.Faces.Count));
        Assert.Contains(doc.Comments, c => c.Text == "object Box002");
        Assert.Contains(doc.Comments, c => c.Text == "File Created: 25.10.2026 12:00:00" && c.ObjectIndex == -1);

        var (mtl, missing) = ObjReader.ReadMaterialLibraries(doc, temp.Path);
        Assert.Empty(missing);
        var m25 = mtl.Find("Material__25")!;
        Assert.Equal(1f, m25.Dissolve);
        Assert.Equal(new Vector3(0.588f), m25.Diffuse);
        Assert.Equal("crate.tga", m25.DiffuseMap!.FileName);
        Assert.Equal("..\\maps\\crate.tga", m25.DiffuseMap.Path);
        Assert.Equal(["Ni", "Tf", "map_Ka"], m25.OtherStatements.Select(s => s.Key));
        Assert.Equal(0.75f, mtl.Find("Material__26")!.Dissolve);

        AssertRoundTrips(doc);
    }

    [Fact]
    public void ReadsMayaStyleOutput()
    {
        var doc = ObjReader.Read(MayaObj, "wall.obj");
        var face = Assert.Single(Assert.Single(doc.Objects).Faces);
        Assert.Equal(5, face.Corners.Count);
        Assert.Equal([0, 1, 2, 4, 3], face.Corners.Select(c => c.Position));
        Assert.Equal(["pWall1", "walls"], face.Groups);
        Assert.Equal("initialShadingGroup", face.Material);
        Assert.Equal("pWall1", Assert.Single(doc.SplitIntoParts()).Name);
        // The pentagon (a wall with a gable) triangulates into three triangles facing +Z.
        var triangles = ObjTriangulator.Triangulate(doc.Positions, face);
        Assert.Equal(3, triangles.Count);
        Assert.All(triangles, t =>
        {
            Vector3 a = doc.Positions[face.Corners[t.A].Position], b = doc.Positions[face.Corners[t.B].Position], c = doc.Positions[face.Corners[t.C].Position];
            Assert.True(Vector3.Cross(b - a, c - a).Z > 0);
        });
        AssertRoundTrips(doc);
    }

    /// <summary>
    /// Every .obj (and the .mtl files it names) in the local REDUX research data reads, and writing it
    /// back gives the same document. Bounded to a handful of files so the normal run stays fast; passes
    /// trivially when the data is not configured.
    /// </summary>
    [Fact]
    public void RealSamplesReadAndRoundTrip()
    {
        if (TestPaths.ReduxResearch is not { } root) return;
        var files = Directory.EnumerateFiles(root, "*.obj", SearchOption.AllDirectories)
            .Where(f => new FileInfo(f).Length <= 16 * 1024 * 1024)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        var watch = Stopwatch.StartNew();
        foreach (string file in files)
        {
            var doc = ObjReader.ReadFile(file);
            Assert.True(doc.Positions.Count > 0, file);
            Assert.True(doc.FaceCount > 0, file);
            Assert.All(doc.Objects.SelectMany(o => o.Faces), f => Assert.True(f.Corners.Count >= 3));
            var (mtl, _) = ObjReader.ReadMaterialLibraries(doc, Path.GetDirectoryName(file)!);
            var materials = new HashSet<string>(mtl.Materials.Select(m => m.Name), StringComparer.Ordinal);
            // REDUX writes a material for every texture it uses.
            if (mtl.Materials.Count > 0)
                Assert.All(doc.Objects.SelectMany(o => o.Faces).Where(f => f.Material is not null), f => Assert.Contains(f.Material!, materials));
            AssertRoundTrips(doc);
            Assert.Equal(ObjWriter.WriteMtl(mtl), ObjWriter.WriteMtl(ObjReader.ReadMtl(ObjWriter.WriteMtl(mtl), "back.mtl")));
        }
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"{files.Count} samples took {watch.Elapsed}");
    }

    /// <summary>
    /// Writing in the shortest float form and reading back gives the same document, comments included:
    /// the writer moves vertex blocks to the top, but comments are placed by object and face, not by line.
    /// </summary>
    private static void AssertRoundTrips(ObjDocument doc)
    {
        var back = ObjReader.Read(ObjWriter.Write(doc, new ObjWriteOptions { Decimals = null }), "back.obj");
        ObjWriterTests.AssertSameDocument(doc, back);
    }
}
