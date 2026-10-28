using System.Globalization;
using System.Numerics;
using System.Text;
using Cairn.Formats.Obj;

namespace Cairn.Rfa.Tests;

/// <summary>The OBJ/MTL reader on synthetic input: indices, n-gons, missing data, state, comments, malformed files.</summary>
public class ObjReaderTests
{
    internal static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void ReadsPositionsUvsNormalsAndFullCorners()
    {
        var doc = ObjReader.Read(Lines(
            "v 0 0 0", "v 1 0 0", "v 1 1 0",
            "vt 0 0", "vt 1 0", "vt 1 1",
            "vn 0 0 1",
            "f 1/1/1 2/2/1 3/3/1"), "t.obj");

        Assert.Equal(3, doc.Positions.Count);
        Assert.Equal(new Vector3(1, 1, 0), doc.Positions[2]);
        Assert.Equal(new Vector2(1, 0), doc.TexCoords[1]);
        Assert.Equal(new Vector3(0, 0, 1), doc.Normals[0]);
        var obj = Assert.Single(doc.Objects);
        Assert.Equal("", obj.Name);
        var face = Assert.Single(obj.Faces);
        Assert.Equal([new ObjCorner(0, 0, 0), new ObjCorner(1, 1, 0), new ObjCorner(2, 2, 0)], face.Corners);
        Assert.Null(face.Material);
        Assert.Empty(face.Groups);
        Assert.Equal(0, face.SmoothingGroup);
        Assert.Null(doc.Colors);
    }

    [Fact]
    public void NegativeIndicesAreRelativeToWhatIsDefinedSoFar()
    {
        var doc = ObjReader.Read(Lines(
            "o A",
            "v 0 0 0", "v 1 0 0", "v 1 1 0", "v 0 1 0",
            "vt 0 0", "vt 1 1",
            "vn 0 0 1",
            "f -4/-2/-1 -3/-1/-1 -2/-1/-1 -1/-2/-1",
            "o B",
            "v 5 0 0", "v 6 0 0", "v 6 1 0",
            "f -3 -2 -1",
            "f 1 -1 -2"), "rel.obj");

        var a = doc.Objects[0].Faces[0];
        Assert.Equal([new ObjCorner(0, 0, 0), new ObjCorner(1, 1, 0), new ObjCorner(2, 1, 0), new ObjCorner(3, 0, 0)], a.Corners);
        var b = doc.Objects[1].Faces;
        Assert.Equal([4, 5, 6], b[0].Corners.Select(c => c.Position));
        Assert.Equal([0, 6, 5], b[1].Corners.Select(c => c.Position));
        Assert.All(b[0].Corners, c => Assert.False(c.HasTexCoord || c.HasNormal));
    }

    [Fact]
    public void ReadsQuadsAndNGonsAsPolygons()
    {
        var text = new StringBuilder();
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            text.Append(CultureInfo.InvariantCulture, $"v {Math.Cos(angle)} {Math.Sin(angle)} 0\n");
        }
        text.Append("f 1 2 3 4\n");
        text.Append("f 1 2 3 4 5 6 7 8\n");
        var doc = ObjReader.Read(text.ToString(), "ngon.obj");

        var faces = doc.Objects[0].Faces;
        Assert.Equal(4, faces[0].Corners.Count);
        Assert.Equal(8, faces[1].Corners.Count);
        Assert.Equal(Enumerable.Range(0, 8), faces[1].Corners.Select(c => c.Position));
    }

    [Fact]
    public void CornersMayLackTexCoordsOrNormals()
    {
        var doc = ObjReader.Read(Lines(
            "v 0 0 0", "v 1 0 0", "v 1 1 0",
            "vt 0 0", "vt 1 0", "vt 1 1",
            "vn 0 0 1",
            "f 1 2 3",
            "f 1//1 2//1 3//1",
            "f 1/1 2/2 3/3",
            "f 1/1/ 2//1 3"), "missing.obj");

        var faces = doc.Objects[0].Faces;
        Assert.Equal([new ObjCorner(0), new ObjCorner(1), new ObjCorner(2)], faces[0].Corners);
        Assert.Equal([new ObjCorner(0, -1, 0), new ObjCorner(1, -1, 0), new ObjCorner(2, -1, 0)], faces[1].Corners);
        Assert.Equal([new ObjCorner(0, 0), new ObjCorner(1, 1), new ObjCorner(2, 2)], faces[2].Corners);
        Assert.Equal([new ObjCorner(0, 0), new ObjCorner(1, -1, 0), new ObjCorner(2)], faces[3].Corners);
    }

    [Fact]
    public void FileWithoutUvsOrNormalsHasEmptyPools()
    {
        var doc = ObjReader.Read(Lines("v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3"), "bare.obj");
        Assert.Empty(doc.TexCoords);
        Assert.Empty(doc.Normals);
        Assert.Equal(1, doc.FaceCount);
    }

    [Fact]
    public void TracksObjectsGroupsMaterialsAndSmoothing()
    {
        var doc = ObjReader.Read(Lines(
            "mtllib scene.mtl",
            "v 0 0 0", "v 1 0 0", "v 0 1 0",
            "o First",
            "usemtl stone",
            "s 1",
            "f 1 2 3",
            "usemtl metal",
            "g left top",
            "s off",
            "f 1 2 3",
            "o Second",
            "f 3 2 1",
            "g",
            "usemtl",
            "s 3",
            "f 3 2 1"), "state.obj");

        Assert.Equal(["scene.mtl"], doc.MaterialLibraries);
        Assert.Equal(["First", "Second"], doc.Objects.Select(o => o.Name));
        var first = doc.Objects[0].Faces;
        Assert.Equal(("stone", 1), (first[0].Material, first[0].SmoothingGroup));
        Assert.Empty(first[0].Groups);
        Assert.Equal(("metal", 0), (first[1].Material, first[1].SmoothingGroup));
        Assert.Equal(["left", "top"], first[1].Groups);
        // State carries over an "o" line, as the format says.
        var second = doc.Objects[1].Faces;
        Assert.Equal(("metal", 0), (second[0].Material, second[0].SmoothingGroup));
        Assert.Equal(["left", "top"], second[0].Groups);
        Assert.Null(second[1].Material);
        Assert.Empty(second[1].Groups);
        Assert.Equal(3, second[1].SmoothingGroup);
    }

    [Fact]
    public void KeepsCommentsWithTheirPlace()
    {
        var doc = ObjReader.Read(Lines(
            "# header line",
            "#tight",
            "v 0 0 0", "v 1 0 0", "v 0 1 0",
            "o Brush_5",
            "# rf_brush uid=5 flags=0x10",
            "f 1 2 3",
            "#   rf_face 0 flags=0x20   ",
            "f 1 2 3",
            "v 0 0 1 # trailing comments on data lines are dropped"), "c.obj");

        Assert.Equal(
            [new ObjComment("header line", -1, 0), new ObjComment("tight", -1, 0), new ObjComment("rf_brush uid=5 flags=0x10", 0, 0), new ObjComment("rf_face 0 flags=0x20", 0, 1)],
            doc.Comments);
        Assert.Equal(4, doc.Positions.Count);
        var brush = Assert.Single(doc.CommentsStartingWith("rf_brush "));
        Assert.Equal("Brush_5", doc.Objects[brush.ObjectIndex].Name);
    }

    [Fact]
    public void JoinsBackslashContinuations()
    {
        var doc = ObjReader.Read(Lines("v 0 0 0", "v 1 0 0", "v 1 1 0", "v 0 1 0", "f 1 2 \\", "  3 \\", "4"), "cont.obj");
        Assert.Equal([0, 1, 2, 3], doc.Objects[0].Faces[0].Corners.Select(c => c.Position));
    }

    [Fact]
    public void ReadsVertexWeightsAndColours()
    {
        var doc = ObjReader.Read(Lines("v 0 0 0", "v 1 0 0 0.5", "v 0 1 0 1 0 0", "vt 0.25", "vt 0.5 0.75 0.1"), "col.obj");
        Assert.Equal(new Vector3(1, 0, 0), doc.Positions[1]);
        Assert.NotNull(doc.Colors);
        Assert.Equal([Vector3.One, Vector3.One, new Vector3(1, 0, 0)], doc.Colors);
        Assert.Equal([new Vector2(0.25f, 0), new Vector2(0.5f, 0.75f)], doc.TexCoords);
    }

    [Fact]
    public void NonFiniteNormalsAreReadAsZeroAndCounted()
    {
        var doc = ObjReader.Read(Lines("vn NaN NaN NaN", "vn 0 Infinity 0", "vn 0 0 1"), "nan.obj");
        Assert.Equal([Vector3.Zero, Vector3.Zero, Vector3.UnitZ], doc.Normals);
        Assert.Equal(2, doc.NonFiniteNormals);
    }

    [Fact]
    public void ToleratesCaseTabsBlankLinesAndLineEndings()
    {
        string text = "\uFEFFV\t0  0\t0\r\n\r\nv 1 0 0\rv 0 1 0   \n\n  F 1 2 3  \r\n";
        var doc = ObjReader.Read(Encoding.UTF8.GetBytes(text), "ws.obj");
        Assert.Equal(3, doc.Positions.Count);
        Assert.Equal(1, doc.FaceCount);
    }

    [Fact]
    public void CountsStatementsItDoesNotModel()
    {
        var doc = ObjReader.Read(Lines("v 0 0 0", "v 1 0 0", "v 0 1 0", "l 1 2", "p 1", "vp 0.5", "cstype bezier", "zb_custom 1", "s on", "f 1 2 3"), "ign.obj");
        Assert.Equal(1, doc.IgnoredStatements["l"]);
        Assert.Equal(1, doc.IgnoredStatements["p"]);
        Assert.Equal(1, doc.IgnoredStatements["vp"]);
        Assert.Equal(1, doc.IgnoredStatements["cstype"]);
        Assert.Equal(1, doc.IgnoredStatements["zb_custom"]);
        Assert.Equal(1, doc.IgnoredStatements["s"]);
        Assert.Equal(0, doc.Objects[0].Faces[0].SmoothingGroup);
    }

    [Theory]
    [InlineData("mtllib a.mtl", new[] { "a.mtl" })]
    [InlineData("mtllib my materials.mtl", new[] { "my materials.mtl" })]
    [InlineData("mtllib a.mtl b.mtl", new[] { "a.mtl", "b.mtl" })]
    [InlineData("mtllib \"quoted name.mtl\"", new[] { "quoted name.mtl" })]
    [InlineData("mtllib ../lib/x.mtl", new[] { "../lib/x.mtl" })]
    public void SplitsMaterialLibraryNames(string line, string[] expected)
    {
        Assert.Equal(expected, ObjReader.Read(Lines(line), "m.obj").MaterialLibraries);
    }

    [Fact]
    public void UndecodableUtf8FallsBackToLatin1()
    {
        byte[] bytes = [.. "usemtl caf"u8, 0xE9, .. "\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n"u8];
        var doc = ObjReader.Read(bytes, "latin.obj");
        Assert.Equal("caf\u00E9", doc.Objects[0].Faces[0].Material);
    }

    [Fact]
    public void EmptyFileIsAnEmptyDocument()
    {
        var doc = ObjReader.Read([], "empty.obj");
        Assert.Empty(doc.Positions);
        Assert.Empty(doc.Objects);
    }

    [Theory]
    [InlineData("f 0 1 2", "line 4", "index 0")]
    [InlineData("f 1 2 9", "line 4", "out of range")]
    [InlineData("f 1/7 2/1 3/1", "line 4", "out of range")]
    [InlineData("f 1//4 2//1 3//1", "line 4", "out of range")]
    [InlineData("f -4 -3 -2", "line 4", "before the first")]
    [InlineData("f 1 2", "line 4", "at least three")]
    [InlineData("f 1/2/3/4 2 3", "line 4", "more than three parts")]
    [InlineData("f a b c", "line 4", "not a valid position index")]
    [InlineData("f /1 2 3", "line 4", "no position index")]
    [InlineData("v 1.2.3 0 0", "line 4", "not a finite number")]
    [InlineData("v nan 0 0", "line 4", "not a finite number")]
    [InlineData("v 1 2", "line 4", "three coordinates")]
    [InlineData("vn 0 1", "line 4", "three values")]
    [InlineData("vt", "line 4", "at least one value")]
    [InlineData("v 1,5 0 0", "line 4", "not a finite number")]
    [InlineData("{\"asset\":1}", "line 4", "not an OBJ statement")]
    public void MalformedInputRaisesAssetFormatExceptionWithTheLine(string badLine, string where, string what)
    {
        string text = Lines("v 0 0 0", "v 1 0 0", "v 0 1 0", badLine, "vt 0 0", "vn 0 0 1");
        var ex = Assert.Throws<AssetFormatException>(() => ObjReader.Read(text, "bad.obj"));
        Assert.Contains("'bad.obj'", ex.Message);
        Assert.Contains(where, ex.Message);
        Assert.Contains(what, ex.Message);
    }

    [Fact]
    public void BinaryDataIsRefused()
    {
        Assert.Throws<AssetFormatException>(() => ObjReader.Read([0x56, 0x33, 0x4D, 0x00, 0x01, 0x02], "x.obj"));
        Assert.Throws<AssetFormatException>(() => ObjReader.Read("v 0 0 0\n\u0001\u0002\n", "x.obj"));
    }

    [Fact]
    public void MutatedInputOnlyEverThrowsAssetFormatException()
    {
        byte[] seed = Encoding.ASCII.GetBytes(Lines(
            "mtllib a.mtl", "o A", "v 0 0 0", "v 1 0 0", "v 0 1 0", "vt 0 0", "vn 0 0 1", "usemtl m", "s 1", "g g1",
            "f 1/1/1 2/1/1 3/1/1", "f -3//-1 -2//-1 -1//-1"));
        byte[] alphabet = Encoding.ASCII.GetBytes("0123456789-/ .#\\\nfvtnsgo\0e+");
        var random = new Random(1234);
        for (int round = 0; round < 3000; round++)
        {
            byte[] bytes = (byte[])seed.Clone();
            int edits = 1 + random.Next(4);
            for (int e = 0; e < edits; e++) bytes[random.Next(bytes.Length)] = alphabet[random.Next(alphabet.Length)];
            try { ObjReader.Read(bytes, "fuzz.obj"); }
            catch (AssetFormatException) { }
        }
    }

    [Fact]
    public void ReadsMtlTypedAndOtherStatements()
    {
        var mtl = ObjReader.ReadMtl(Lines(
            "# library header",
            "newmtl stone",
            "Ns 96.078431",
            "Ka 1 1 1",
            "Kd 0.640000 0.5 0.25",
            "Ks 0.5",
            "Ni 1.45",
            "d 0.5",
            "illum 2",
            "map_Kd -s 1 1 1 -o 0.5 0.5 -clamp on textures\\stone wall.tga",
            "map_Bump -bm 0.3 bump.png",
            "# after stone",
            "newmtl glass",
            "Tr 0.25",
            "Kd spectral glass.rfl",
            "map_Kd \"../art/glass.png\"",
            "newmtl tr_after_d",
            "d 0.75",
            "Tr 0.5"), "lib.mtl");

        Assert.Equal(["stone", "glass", "tr_after_d"], mtl.Materials.Select(m => m.Name));
        var stone = mtl.Materials[0];
        Assert.Equal(96.078431f, stone.SpecularExponent);
        Assert.Equal(Vector3.One, stone.Ambient);
        Assert.Equal(new Vector3(0.64f, 0.5f, 0.25f), stone.Diffuse);
        Assert.Equal(new Vector3(0.5f), stone.Specular);
        Assert.Equal(0.5f, stone.Dissolve);
        Assert.Equal(2, stone.Illumination);
        Assert.Equal(new MtlTextureMap("textures\\stone wall.tga", "-s 1 1 1 -o 0.5 0.5 -clamp on"), stone.DiffuseMap);
        Assert.Equal("stone wall.tga", stone.DiffuseMap!.FileName);
        Assert.Equal([new("Ni", "1.45"), new("map_Bump", "-bm 0.3 bump.png")], stone.OtherStatements);

        var glass = mtl.Materials[1];
        Assert.Equal(0.75f, glass.Dissolve);
        Assert.Null(glass.Diffuse);
        Assert.Equal([new("Kd", "spectral glass.rfl")], glass.OtherStatements);
        Assert.Equal("glass.png", glass.DiffuseMap!.FileName);
        Assert.Equal(0.75f, mtl.Materials[2].Dissolve);

        Assert.Equal([new MtlComment("library header", -1), new MtlComment("after stone", 0)], mtl.Comments);
        Assert.Same(glass, mtl.Find("glass"));
        Assert.Null(mtl.Find("missing"));
    }

    [Fact]
    public void MalformedMtlRaisesAssetFormatException()
    {
        Assert.Throws<AssetFormatException>(() => ObjReader.ReadMtl([0, 1, 2, 3], "bin.mtl"));
        // Values that do not parse are kept untyped rather than refused.
        var mtl = ObjReader.ReadMtl(Lines("newmtl a", "Kd x y z", "d"), "odd.mtl");
        Assert.Null(mtl.Materials[0].Diffuse);
        Assert.Equal(2, mtl.Materials[0].OtherStatements.Count);
    }

    [Fact]
    public void SplitsIntoPartsByObjectAndGroup()
    {
        var doc = ObjReader.Read(Lines(
            "v 0 0 0", "v 1 0 0", "v 0 1 0",
            "g default",
            "g Box001", "f 1 2 3", "f 1 2 3",
            "g Box002", "f 1 2 3",
            "o Cube", "f 1 2 3",
            "o Pair", "g left", "f 1 2 3", "g right", "f 1 2 3"), "parts.obj");

        var parts = doc.SplitIntoParts();
        Assert.Equal(["Box001", "Box002", "Cube", "Pair/left", "Pair/right"], parts.Select(p => p.Name));
        Assert.Equal([2, 1, 1, 1, 1], parts.Select(p => p.Faces.Count));
    }

    [Fact]
    public void ReadsMaterialLibrariesBesideTheFile()
    {
        using var temp = new TempFolder();
        temp.Write("a.mtl", Lines("newmtl one", "map_Kd one.tga"));
        temp.Write("b.mtl", Lines("newmtl two"));
        var doc = ObjReader.Read(Lines("mtllib a.mtl", "mtllib exports\\old\\b.mtl", "mtllib gone.mtl"), "m.obj");

        var (materials, missing) = ObjReader.ReadMaterialLibraries(doc, temp.Path);
        Assert.Equal(["one", "two"], materials.Materials.Select(m => m.Name));
        Assert.Equal(["gone.mtl"], missing);
    }

    [Fact]
    public void ReadFileRefusesOversizedFiles()
    {
        using var temp = new TempFolder();
        string path = temp.Write("big.obj", Lines("v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3"));
        Assert.Equal(1, ObjReader.ReadFile(path).FaceCount);
        Assert.Throws<AssetFormatException>(() => ObjReader.ReadFile(path, maxBytes: 4));
    }
}

/// <summary>The OBJ/MTL writer: round trips, options, triangulation and validation.</summary>
public class ObjWriterTests
{
    /// <summary>A document with every feature: colours, groups, materials, smoothing, comments, missing vt/vn, an n-gon.</summary>
    internal static ObjDocument Sample()
    {
        var doc = new ObjDocument();
        doc.MaterialLibraries.Add("scene.mtl");
        doc.Positions.AddRange([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0), new(0.5f, 1.5f, 0), new(-0.125f, 0.333333f, 2.5f)]);
        doc.Colors = [Vector3.One, new(1, 0, 0), new(0, 1, 0), new(0, 0, 1), new(0.5f), Vector3.Zero];
        doc.TexCoords.AddRange([new(0, 0), new(1, 0), new(1, 1), new(0, 1)]);
        doc.Normals.AddRange([new(0, 0, 1), new(0, 0, -1)]);

        var a = new ObjObject("Brush 1");
        IReadOnlyList<string> groups = ["walls", "outer"];
        a.Faces.Add(Face("stone", groups, 1, new ObjCorner(0, 0, 0), new ObjCorner(1, 1, 0), new ObjCorner(2, 2, 0), new ObjCorner(3, 3, 0)));
        a.Faces.Add(Face("stone", groups, 1, new ObjCorner(3, 3, 1), new ObjCorner(2, 2, 1), new ObjCorner(4)));
        a.Faces.Add(Face("metal", [], 0, new ObjCorner(0, -1, 0), new ObjCorner(1, -1, 0), new ObjCorner(2, -1, 0), new ObjCorner(4, -1, 0), new ObjCorner(3, -1, 0)));
        var b = new ObjObject("Second");
        b.Faces.Add(Face("metal", [], 4, new ObjCorner(5, 0), new ObjCorner(1, 1), new ObjCorner(2, 2)));
        b.Faces.Add(Face(null, ["g2"], 4, new ObjCorner(5), new ObjCorner(2), new ObjCorner(1)));
        doc.Objects.AddRange([a, b, new ObjObject("Empty")]);

        doc.Comments.AddRange([
            new ObjComment("written by a test", -1, 0),
            new ObjComment("rf_brush uid=1 flags=0x0", 0, 0),
            new ObjComment("rf_face 2", 0, 2),
            new ObjComment("end of brush", 0, 3),
            new ObjComment("rf_brush uid=2", 1, 0),
        ]);
        return doc;

        static ObjFace Face(string? material, IReadOnlyList<string> groups, int smoothing, params ObjCorner[] corners)
        {
            var face = new ObjFace { Material = material, Groups = groups, SmoothingGroup = smoothing };
            face.Corners.AddRange(corners);
            return face;
        }
    }

    internal static void AssertSameDocument(ObjDocument expected, ObjDocument actual, float tolerance = 0)
    {
        Assert.Equal(expected.MaterialLibraries, actual.MaterialLibraries);
        AssertClose(expected.Positions, actual.Positions, tolerance);
        Assert.Equal(expected.Colors is null, actual.Colors is null);
        if (expected.Colors is not null) AssertClose(expected.Colors, actual.Colors!, tolerance);
        Assert.Equal(expected.TexCoords.Count, actual.TexCoords.Count);
        for (int i = 0; i < expected.TexCoords.Count; i++)
            Assert.True(Vector2.Distance(expected.TexCoords[i], actual.TexCoords[i]) <= tolerance * 2, $"vt {i}: {expected.TexCoords[i]} vs {actual.TexCoords[i]}");
        AssertClose(expected.Normals, actual.Normals, tolerance);
        Assert.Equal(expected.Objects.Select(o => o.Name), actual.Objects.Select(o => o.Name));
        for (int o = 0; o < expected.Objects.Count; o++)
        {
            var e = expected.Objects[o].Faces;
            var a = actual.Objects[o].Faces;
            Assert.Equal(e.Count, a.Count);
            for (int f = 0; f < e.Count; f++)
            {
                Assert.Equal(e[f].Corners, a[f].Corners);
                Assert.Equal(e[f].Material, a[f].Material);
                Assert.Equal(e[f].Groups, a[f].Groups);
                Assert.Equal(e[f].SmoothingGroup, a[f].SmoothingGroup);
            }
        }
        Assert.Equal(expected.Comments, actual.Comments);
    }

    private static void AssertClose(IReadOnlyList<Vector3> expected, IReadOnlyList<Vector3> actual, float tolerance)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
            Assert.True(Vector3.Distance(expected[i], actual[i]) <= tolerance * 2, $"item {i}: {expected[i]} vs {actual[i]}");
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, null)]
    [InlineData(true, 6)]
    public void RoundTripKeepsEverything(bool crlf, int? decimals)
    {
        var doc = Sample();
        var options = new ObjWriteOptions { UseCrLf = crlf, Decimals = decimals };
        string text = ObjWriter.Write(doc, options);
        var back = ObjReader.Read(text, "rt.obj");
        AssertSameDocument(doc, back, decimals is null ? 0 : 1e-6f);
        // Writing what was read gives the same text again.
        Assert.Equal(text, ObjWriter.Write(back, options));
    }

    [Fact]
    public void ShortestFormRoundTripsEveryFloatExactly()
    {
        var doc = new ObjDocument();
        var random = new Random(7);
        for (int i = 0; i < 2000; i++)
            doc.Positions.Add(new Vector3((float)((random.NextDouble() - 0.5) * 2e6), (float)random.NextDouble() - 0.5f, -(float)random.NextDouble() * 1e-6f));
        var back = ObjReader.Read(ObjWriter.Write(doc, new ObjWriteOptions { Decimals = null }), "f.obj");
        Assert.Equal(doc.Positions, back.Positions);
    }

    [Fact]
    public void WritesTheChosenLineEndingOnly()
    {
        string crlf = ObjWriter.Write(Sample(), new ObjWriteOptions { UseCrLf = true });
        string lf = ObjWriter.Write(Sample(), new ObjWriteOptions { UseCrLf = false });
        Assert.DoesNotContain("\n", crlf.Replace("\r\n", ""));
        Assert.DoesNotContain("\r", lf);
        Assert.EndsWith("\r\n", crlf);
    }

    [Fact]
    public void WritesInvariantNumbersWithTheChosenPrecision()
    {
        var doc = new ObjDocument();
        doc.Positions.Add(new Vector3(1f / 3, -0.0000001f, 1234.5f));
        doc.TexCoords.Add(new Vector2(0.5f, 1f));
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("v 0.33 0 1234.5\r\n", ObjWriter.Write(doc, new ObjWriteOptions { Decimals = 2 }));
            Assert.Contains("v 0.333333 0 1234.5\r\n", ObjWriter.Write(doc));
            Assert.Contains("v 0 0 1234\r\n", ObjWriter.Write(doc, new ObjWriteOptions { Decimals = 0 }).Replace("1235", "1234"));
            Assert.Contains("vt 0.5 1\r\n", ObjWriter.Write(doc));
            Assert.Contains("v 0.33333334 -1E-07 1234.5", ObjWriter.Write(doc, new ObjWriteOptions { Decimals = null }));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => ObjWriter.Write(doc, new ObjWriteOptions { Decimals = 16 }));
    }

    [Fact]
    public void WritesStateChangesAndCornerForms()
    {
        string text = ObjWriter.Write(Sample(), new ObjWriteOptions { UseCrLf = false });
        string[] lines = text.Split('\n');
        Assert.Equal("# written by a test", lines[0]);
        Assert.Equal("mtllib scene.mtl", lines[1]);
        Assert.Contains("v 1 0 0 1 0 0", lines);
        int brush = Array.IndexOf(lines, "o Brush 1");
        Assert.Equal(["o Brush 1", "# rf_brush uid=1 flags=0x0", "g walls outer", "usemtl stone", "s 1", "f 1/1/1 2/2/1 3/3/1 4/4/1", "f 4/4/2 3/3/2 5"],
            lines[brush..(brush + 7)]);
        Assert.Contains("f 1//1 2//1 3//1 5//1 4//1", lines);
        int second = Array.IndexOf(lines, "o Second");
        // usemtl is repeated at the start of an object even though it did not change.
        Assert.Equal(["o Second", "# rf_brush uid=2", "usemtl metal", "s 4", "f 6/1 2/2 3/3", "g g2", "usemtl", "f 6 3 2", "o Empty", ""],
            lines[second..]);
    }

    [Fact]
    public void TriangulatesOnRequestKeepingWinding()
    {
        var doc = Sample();
        var back = ObjReader.Read(ObjWriter.Write(doc, new ObjWriteOptions { Triangulate = true }), "tri.obj");
        Assert.All(back.Objects.SelectMany(o => o.Faces), f => Assert.Equal(3, f.Corners.Count));
        // quad -> 2, triangle -> 1, pentagon -> 3, then two triangles.
        Assert.Equal(6, back.Objects[0].Faces.Count);
        Assert.Equal(2, back.Objects[1].Faces.Count);
        // Every triangle keeps the source face's attributes and corners.
        var quadCorners = doc.Objects[0].Faces[0].Corners;
        Assert.All(back.Objects[0].Faces.Take(2), t =>
        {
            Assert.Equal("stone", t.Material);
            Assert.All(t.Corners, c => Assert.Contains(c, quadCorners));
            Assert.True(Normal(back.Positions, t).Z > 0);
        });
    }

    [Fact]
    public void EarClippingHandlesConcavePolygons()
    {
        // An L shape (counter-clockwise seen from +Z), whose fan from corner 0 would cover the notch.
        Vector3[] l = [new(0, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(1, 2, 0), new(0, 2, 0)];
        AssertTiles(l, area: 3);
        // The same shape from the back, in the XZ plane.
        Vector3[] back = l.Reverse().Select(p => new Vector3(p.X, 0, p.Y)).ToArray();
        AssertTiles(back, area: 3);
        // A star (concave, ten corners) in the YZ plane.
        var star = new Vector3[10];
        for (int i = 0; i < 10; i++)
        {
            double angle = i * Math.PI / 5, radius = i % 2 == 0 ? 2 : 0.8;
            star[i] = new Vector3(0, (float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle)));
        }
        AssertTiles(star, area: 10 * 0.5 * 2 * 0.8 * Math.Sin(Math.PI / 5));
        // Degenerate input falls back to a fan.
        Assert.Equal(2, ObjTriangulator.Triangulate([Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero]).Count);
        Assert.Empty(ObjTriangulator.Triangulate([Vector3.Zero, Vector3.One]));

        static void AssertTiles(Vector3[] polygon, double area)
        {
            var triangles = ObjTriangulator.Triangulate(polygon);
            Assert.Equal(polygon.Length - 2, triangles.Count);
            Vector3 expectedNormal = Vector3.Zero;
            for (int i = 0; i < polygon.Length; i++)
                expectedNormal += Vector3.Cross(polygon[i], polygon[(i + 1) % polygon.Length]);
            double total = 0;
            foreach (var (a, b, c) in triangles)
            {
                var cross = Vector3.Cross(polygon[b] - polygon[a], polygon[c] - polygon[a]);
                Assert.True(Vector3.Dot(cross, expectedNormal) > 0, "triangle winding flipped");
                total += cross.Length() / 2;
            }
            Assert.Equal(area, total, 4);
        }
    }

    [Fact]
    public void WritesMtl()
    {
        var mtl = new MtlDocument();
        var stone = new MtlMaterial("stone") { Diffuse = new(0.8f, 0.8f, 0.8f), Dissolve = 1, Illumination = 1, SpecularExponent = 10, DiffuseMap = new MtlTextureMap("stone wall.tga", "-clamp on") };
        stone.OtherStatements.Add(new("Ni", "1.45"));
        mtl.Materials.AddRange([stone, new MtlMaterial("bare")]);
        mtl.Comments.AddRange([new MtlComment("header"), new MtlComment("about stone", 0)]);

        string text = ObjWriter.WriteMtl(mtl, new ObjWriteOptions { UseCrLf = false });
        Assert.Equal(ObjReaderTests.Lines("# header", "", "newmtl stone", "Ns 10", "Kd 0.8 0.8 0.8", "d 1", "illum 1", "map_Kd -clamp on stone wall.tga", "Ni 1.45", "# about stone", "", "newmtl bare"), text);

        var back = ObjReader.ReadMtl(text, "back.mtl");
        Assert.Equal(mtl.Comments, back.Comments);
        Assert.Equal(stone.DiffuseMap, back.Materials[0].DiffuseMap);
        Assert.Equal(stone.Diffuse, back.Materials[0].Diffuse);
        Assert.Equal(stone.OtherStatements, back.Materials[0].OtherStatements);
        Assert.Equal(text, ObjWriter.WriteMtl(back, new ObjWriteOptions { UseCrLf = false }));
    }

    [Fact]
    public void WriteFilesWritesTheMtlBesideTheObj()
    {
        using var temp = new TempFolder();
        var doc = Sample();
        var mtl = new MtlDocument();
        mtl.Materials.AddRange([new MtlMaterial("stone") { DiffuseMap = new("stone.png") }, new MtlMaterial("metal")]);
        string objPath = temp.File("level one.obj");
        ObjWriter.WriteFiles(objPath, doc, mtl);

        var back = ObjReader.ReadFile(objPath);
        Assert.Equal(["level one.mtl"], back.MaterialLibraries);
        var (materials, missing) = ObjReader.ReadMaterialLibraries(back, temp.Path);
        Assert.Empty(missing);
        Assert.Equal("stone.png", materials.Find("stone")!.DiffuseMap!.Path);
        Assert.False(File.ReadAllBytes(objPath).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));

        // Without a library, the document's own mtllib lines are kept and no .mtl is written.
        string bare = temp.File("bare.obj");
        ObjWriter.WriteFiles(bare, doc, null);
        Assert.Equal(["scene.mtl"], ObjReader.ReadFile(bare).MaterialLibraries);
        Assert.False(File.Exists(temp.File("bare.mtl")));
    }

    [Fact]
    public void UnnamedFirstObjectAndEmptyObjectsRoundTrip()
    {
        var doc = ObjReader.Read(ObjReaderTests.Lines("v 0 0 0", "v 1 0 0", "v 0 1 0", "f 1 2 3", "o", "f 1 2 3", "o Empty"), "u.obj");
        Assert.Equal(["", "", "Empty"], doc.Objects.Select(o => o.Name));
        AssertSameDocument(doc, ObjReader.Read(ObjWriter.Write(doc), "u2.obj"));
    }

    [Fact]
    public void InconsistentDocumentsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].Corners[0] = new ObjCorner(99))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].Corners[0] = new ObjCorner(0, 99))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].Corners[0] = new ObjCorner(0, -1, 99))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[1].Corners.RemoveAt(0))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Name = "two\nlines")));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].Material = "a\rb")));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].Groups = ["has space"])));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Colors!.RemoveAt(0))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Objects[0].Faces[0].SmoothingGroup = -1)));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.Positions[0] = new Vector3(float.NaN))));
        Assert.Throws<ArgumentException>(() => ObjWriter.Write(With(d => d.TexCoords[0] = new Vector2(float.PositiveInfinity, 0))));

        static ObjDocument With(Action<ObjDocument> change)
        {
            var doc = Sample();
            change(doc);
            return doc;
        }
    }

    private static Vector3 Normal(IReadOnlyList<Vector3> positions, ObjFace face)
    {
        Vector3 a = positions[face.Corners[0].Position], b = positions[face.Corners[1].Position], c = positions[face.Corners[2].Position];
        return Vector3.Cross(b - a, c - a);
    }
}
