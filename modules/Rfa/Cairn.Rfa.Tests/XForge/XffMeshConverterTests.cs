using System.Numerics;
using Cairn.Formats.XForge;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Rfa.Formats.Rfa;
using Cairn.Rfa.Formats.V3d;
using Cairn.Rfa.Linting;

namespace Cairn.Rfa.Tests.XForge;

/// <summary>
/// N-Gage models and characters to Red Faction meshes and clips, on scenes written by <see cref="SyntheticXff"/>: the
/// space (winding, normals, scale), vertex splitting, the blend mapping, the generated colours, a character's skeleton
/// and weights, and the clip's keys against hand-worked times (a negative start and the loop wrap included).
/// </summary>
public class XffMeshConverterTests
{
    private static readonly Vector3[] Triangle = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
    private static readonly Vector3[] Up = [new(0, 0, 1), new(0, 0, 1), new(0, 0, 1)];
    private static readonly Vector2[] TriangleUvs = [new(0, 0), new(1, 0), new(0, 1)];

    private static XffColour Grey(byte a = 0) => new(100, 110, 120, a);

    /// <summary>A model: a root and one textured triangle (corners A, B, C at the origin, +X and +Y), with the material given.</summary>
    private static byte[] OneTriangle(byte blendSource = 2, byte blendDest = 1, string? texture = "crate.pcx")
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint node = x.Object(root, "box");
        uint tex = texture is null ? XffFile.NoReference : x.Texture(texture);
        uint mat = x.Material(byte0: blendSource, byte1: blendDest);
        uint vb = x.VertexBuffer(Triangle, Up);
        uint il = x.IndexList([0, 1, 2]);
        uint fa = x.FaceAttributes(1, [Grey(), Grey(), Grey()], texture is null ? null : TriangleUvs);
        x.MeshInstance(node, "mesh_box", [SyntheticXff.Submesh(vb, il, 3, fa, mat, tex)]);
        return x.ToArray();
    }

    private static V3dMaterialGroup OnlyGroup(LegacyMesh mesh) => Assert.Single(Assert.Single(Assert.Single(mesh.Description.Submeshes).Lods).Groups);

    [Fact]
    public void Triangles_AreReorderedAndNormalsNegated_AndPositionsScaled()
    {
        var mesh = XffMeshConverter.Convert(OneTriangle(), "box.xff");
        Assert.Equal(V3dKind.StaticMesh, mesh.Description.Kind);
        var group = OnlyGroup(mesh);
        var t = Assert.Single(group.Triangles);
        var (a, b, c) = (group.Vertices[t.A], group.Vertices[t.B], group.Vertices[t.C]);
        // (A, B, C) becomes (A, C, B), each corner with its own UV
        Assert.Equal(Vector3.Zero, a.Position);
        Assert.Equal(new Vector3(0, XffSpace.RfScale, 0), b.Position);
        Assert.Equal(new Vector3(XffSpace.RfScale, 0, 0), c.Position);
        Assert.Equal(new Vector2(0, 0), a.TexCoord);
        Assert.Equal(new Vector2(0, 1), b.TexCoord);
        Assert.Equal(new Vector2(1, 0), c.TexCoord);
        Assert.All(group.Vertices, v => Assert.Equal(new Vector3(0, 0, -1), v.Normal));
        // Red Faction's front face: cross(B − A, C − A) agrees with the normals
        Assert.True(Vector3.Dot(Vector3.Cross(b.Position - a.Position, c.Position - a.Position), a.Normal) > 0);
        Assert.False(t.IsDoubleSided);
    }

    [Fact]
    public void Scale_IsTheChosenOne()
    {
        var mesh = XffMeshConverter.Convert(XffScene.Read(OneTriangle(), "box.xff"), "box.xff", 1f);
        var group = OnlyGroup(mesh);
        Assert.Contains(group.Vertices, v => v.Position == new Vector3(1, 0, 0));
        Assert.Contains(group.Vertices, v => v.Position == new Vector3(0, 1, 0));
        Assert.Equal(1f, mesh.Xff!.Scale);
        Assert.Throws<AssetFormatException>(() => XffMeshConverter.Convert(XffScene.Read(OneTriangle(), "box.xff"), "box.xff", 0f));
        Assert.Throws<AssetFormatException>(() => XffMeshConverter.Convert(XffScene.Read(OneTriangle(), "box.xff"), "box.xff", float.NaN));
    }

    [Fact]
    public void Vertices_SplitByPositionNormalAndUv()
    {
        // a quad of four vertices: corner 0 and 2 are shared; 0 keeps its UV in both triangles, 2 does not
        var x = new SyntheticXff();
        uint root = x.Root();
        uint node = x.Object(root, "quad");
        uint tex = x.Texture("wall.tga");
        uint mat = x.Material();
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        uint vb = x.VertexBuffer(positions, [.. positions.Select(_ => Vector3.UnitZ)]);
        uint il = x.IndexList([0, 1, 2, 0, 2, 3]);
        Vector2[] uvs = [new(0, 0), new(1, 0), new(1, 1), new(0, 0), new(0.5f, 0.5f), new(0, 1)];
        uint fa = x.FaceAttributes(2, null, uvs);
        x.MeshInstance(node, "mesh_quad", [SyntheticXff.Submesh(vb, il, 6, fa, mat, tex)]);
        var mesh = XffMeshConverter.Convert(x.ToArray(), "quad.xff");
        var group = OnlyGroup(mesh);
        Assert.Equal(2, group.Triangles.Length);
        Assert.Equal(5, group.Vertices.Length);
        Assert.Contains(mesh.Notes, n => n.Contains("6 corners became 5 vertices", StringComparison.Ordinal));
        Assert.Equal("wall.tga", Assert.Single(Assert.Single(mesh.Description.Submeshes).Materials).DiffuseMap.Text);
    }

    [Theory]
    [InlineData(2, 1, V3dBuilder.DefaultRenderFlags, 0x1u)]
    [InlineData(5, 6, V3dBuilder.DefaultRenderFlags, 0x9u)]
    [InlineData(2, 2, XffMeshConverter.AdditiveRenderFlags, 0x1u)]
    [InlineData(1, 4, V3dBuilder.DefaultRenderFlags, 0x9u)]
    [InlineData(3, 3, V3dBuilder.DefaultRenderFlags, 0x1u)]
    public void BlendPair_MapsToTheNearestState(byte source, byte dest, uint renderFlags, uint materialFlags)
    {
        var mesh = XffMeshConverter.Convert(OneTriangle(source, dest), "glow.xff");
        var group = OnlyGroup(mesh);
        Assert.Equal(renderFlags, group.RenderFlags);
        Assert.Equal(materialFlags, Assert.Single(Assert.Single(mesh.Description.Submeshes).Materials).Flags);
        if ((source, dest) == (1, 4)) Assert.Contains(mesh.Notes, n => n.Contains("darkening blend", StringComparison.Ordinal));
        if ((source, dest) == (3, 3)) Assert.Contains(mesh.Notes, n => n.Contains("3, 3", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("baked vertex lighting", StringComparison.Ordinal));
    }

    [Fact]
    public void UntexturedPart_GetsItsAverageColour()
    {
        var mesh = XffMeshConverter.Convert(OneTriangle(texture: null), "button.xff");
        var material = Assert.Single(Assert.Single(mesh.Description.Submeshes).Materials);
        Assert.Equal("button_colours.tga", material.DiffuseMap.Text);
        var (name, bytes) = Assert.Single(mesh.Xff!.GeneratedTextures);
        Assert.Equal("button_colours.tga", name);
        var image = ImageDecoder.Decode(bytes, name);
        var uv = OnlyGroup(mesh).Vertices[0].TexCoord;
        var (b, g, r, _) = image.Get((int)(uv.X * image.Width), (int)(uv.Y * image.Height));
        Assert.Equal((100, 110, 120), (r, g, b));
        Assert.Empty(mesh.Xff.Textures);
    }

    [Fact]
    public void Conversion_WritesAPcxAsTheTgaTheMeshNames_AndReportsMissingOnes()
    {
        // a 2 x 1 PCX, red then blue: the mesh names crate.tga and the copy is that TGA, the pixels the same
        byte[] pcx = SyntheticPcx.Write(2, 1, 8, 1, (x, _) => x, [255, 0, 0, 0, 0, 255]);
        var withTexture = LegacyMeshSupport.Convert(OneTriangle(), "box.xff", n => n.Equals("crate.pcx", StringComparison.OrdinalIgnoreCase) ? pcx : null);
        Assert.Equal("box.v3m", withTexture.OutputName);
        Assert.Equal("crate.tga", withTexture.Mesh.Submeshes.Single().Materials.Single().DiffuseMap.Text);
        var copy = Assert.Single(withTexture.Textures, t => t.Name == "crate.tga").Bytes;
        var image = ImageDecoder.Decode(copy, "crate.tga");
        Assert.Equal(((byte)0, (byte)0, (byte)255, (byte)255), image.Get(0, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0, (byte)255), image.Get(1, 0));
        Assert.DoesNotContain(withTexture.Textures, t => t.Name.EndsWith(".pcx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(withTexture.Report, n => n.Contains("written as .tga", StringComparison.Ordinal));
        Assert.Null(withTexture.Clip);

        // a .pcx that does not decode is reported and not written
        var bad = LegacyMeshSupport.Convert(OneTriangle(), "box.xff", _ => [1, 2, 3, 4]);
        Assert.Empty(bad.Textures);
        Assert.Contains(bad.Report, n => n.Contains("could not be read as an image (crate.pcx)", StringComparison.Ordinal));

        var off = LegacyMeshSupport.Convert(OneTriangle(), "box.xff", _ => pcx, new LegacyConvertOptions { XffWriteTextures = false });
        Assert.Empty(off.Textures);
        Assert.Contains(off.Report, n => n.Contains("not written", StringComparison.Ordinal));

        var missing = LegacyMeshSupport.Convert(OneTriangle(texture: "txtr_fcanister1.tga"), "ammo.xff", _ => null);
        Assert.Contains(missing.Report, n => n.Contains("txtr_fcanister1.tga", StringComparison.Ordinal) && n.Contains("game's own archive", StringComparison.Ordinal));
        // the result passes the linter
        Assert.DoesNotContain(MeshLinter.Analyze(withTexture.Mesh), d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
    }

    [Fact]
    public void LongTextureName_IsShortenedAndCopiedUnderTheNewName()
    {
        string longName = "txtr_rck_underwaterrock_destroy.tga";
        var conversion = LegacyMeshSupport.Convert(OneTriangle(texture: longName), "rock.xff", _ => [9]);
        string stored = Assert.Single(conversion.Mesh.Submeshes.First().Materials).DiffuseMap.Text;
        Assert.True(stored.Length <= 31);
        Assert.EndsWith(".tga", stored);
        Assert.Contains(conversion.Textures, t => t.Name == stored);
        Assert.Contains(conversion.Report, n => n.Contains(longName + " → " + stored, StringComparison.Ordinal));
    }

    /// <summary>A 2 x 2 16-bit TGA declaring one alpha bit, raw or run-length, with the pixels given.</summary>
    private static byte[] Tga16(bool rle, params ushort[] pixels)
    {
        var bytes = new List<byte> { 0, 0, (byte)(rle ? 10 : 2), 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 2, 0, 16, 0x21 };
        if (rle) bytes.Add(0x80 | 3); // one run of four
        foreach (ushort p in rle ? pixels.Take(1) : pixels) { bytes.Add((byte)p); bytes.Add((byte)(p >> 8)); }
        return [.. bytes];
    }

    [Fact]
    public void SixteenBitTextures_WithAnAlphaBitNeverSet_AreMadeOpaque()
    {
        foreach (bool rle in new[] { false, true })
        {
            byte[] clear = Tga16(rle, 0x7C00, 0x03E0, 0x001F, 0x7FFF);
            byte[] fixedTga = XffTextureFix.Opaque(clear);
            Assert.NotSame(clear, fixedTga);
            Assert.Equal(0x20, fixedTga[17]);
            Assert.Equal(clear.AsSpan(18).ToArray(), fixedTga.AsSpan(18).ToArray());
            var image = ImageDecoder.Decode(fixedTga, "red.tga");
            Assert.Equal(255, image.Get(0, 0).A);
            // a pixel with its alpha bit set: real alpha, kept
            byte[] real = Tga16(rle, 0xFC00, 0x03E0, 0x001F, 0x7FFF);
            Assert.Same(real, XffTextureFix.Opaque(real));
        }
        byte[] cut = Tga16(false, 0x7C00, 0x03E0, 0x001F, 0x7FFF)[..20];
        Assert.Same(cut, XffTextureFix.Opaque(cut));
        Assert.False(XffTextureFix.NeedsFix([1, 2, 3]));
    }

    [Fact]
    public void Level_IsRefused()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        x.Marker(XffMarkers.Area, root, "room01");
        Assert.Throws<AssetFormatException>(() => XffMeshConverter.Convert(x.ToArray(), "level_01a.xff"));
        Assert.False(LegacyMeshSupport.TryRead(x.ToArray(), "level_01a.xff", out _, out var error));
        Assert.Contains("Levels module", error!.Message, StringComparison.Ordinal);
    }

    // ── characters ───────────────────────────────────────────────────────────────────────────────────────

    private static XffKey Key(Vector3 position) => new(position, Quaternion.Identity, Vector3.One);

    private static void Close(float[] expected, IEnumerable<float> actual)
    {
        var got = actual.ToArray();
        Assert.Equal(expected.Length, got.Length);
        for (int i = 0; i < got.Length; i++) Assert.True(MathF.Abs(expected[i] - got[i]) < 1e-3f, $"key {i}: {got[i]}, expected {expected[i]}");
    }

    /// <summary>
    /// A character: root → enemy marker → track anim_body (0..100 ms, x = 0, 1, 2, 3) → body (a triangle) and track anim_arm
    /// (−33..67 ms, y = 10, 20, 30, 40) → arm (a triangle), track anim_tail under the body (0..50 ms, z = 0, 33) → tail, and an
    /// IK helper track without geometry, all at 30 keys a second (33 ms).
    /// </summary>
    private static byte[] Character()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        uint ik = x.Track(root, "anim_IK Chain01", 0, 100, 30f, [Key(Vector3.Zero), Key(Vector3.One)]);
        x.Helper(ik, "IK Chain01", Vector3.Zero);
        uint body = x.Track(marker, "anim_body", 0, 100, 30f, [Key(new(0, 0, 0)), Key(new(1, 0, 0)), Key(new(2, 0, 0)), Key(new(3, 0, 0))]);
        uint bodyNode = x.MeshObject(body, "body", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        uint arm = x.Track(bodyNode, "anim_arm", -33, 67, 30f, [Key(new(0, 10, 0)), Key(new(0, 20, 0)), Key(new(0, 30, 0)), Key(new(0, 40, 0))]);
        x.MeshObject(arm, "arm", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        uint tail = x.Track(body, "anim_tail", 0, 50, 30f, [Key(new(0, 0, 0)), Key(new(0, 0, 33))]);
        x.MeshObject(tail, "tail", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        return x.ToArray();
    }

    [Fact]
    public void Character_HasOneBonePerAnimatedPart_WeightedFully()
    {
        var mesh = XffMeshConverter.Convert(Character(), "enemy_test.xff");
        var d = mesh.Description;
        Assert.Equal(V3dKind.Character, d.Kind);
        Assert.Equal(["body", "arm", "tail"], d.Bones.Select(b => b.Name.Text));
        Assert.Equal([-1, 0, 0], d.Bones.Select(b => b.ParentIndex));
        var submesh = Assert.Single(d.Submeshes);
        var vertices = submesh.Lods[0].Groups.SelectMany(g => g.Vertices).ToList();
        Assert.Equal(9, vertices.Count);
        Assert.All(vertices, v => Assert.Equal(1f, Assert.Single(v.Influences).Weight));
        Assert.Equal(3, vertices.Count(v => v.Influences[0].Bone == 1));
        // the bind pose is the pose at each track's start: the arm's track starts at −33 ms, holding its first key
        var skeleton = Skeleton.FromBones(d.Bones);
        Assert.True(Vector3.Distance(new Vector3(0, 10 * XffSpace.RfScale, 0), skeleton.RestWorld[1].Position) < 1e-4f);
        Assert.Contains(vertices, v => v.Influences[0].Bone == 1 && v.Position == new Vector3(0, 10 * XffSpace.RfScale, 0));
        Assert.Contains(mesh.Notes, n => n.Contains("IK Chain01", StringComparison.Ordinal));
        Assert.Contains(mesh.Notes, n => n.Contains("new character", StringComparison.Ordinal));
        Assert.Equal("enemy_test_walk.rfa", mesh.Xff!.ClipName);
        Assert.DoesNotContain(MeshLinter.Analyze(LegacyMeshSupport.Compile(mesh)), x => x.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Clip_SamplesEveryTrackFromItsOwnStart_OnOneGrid()
    {
        var clip = XffMeshConverter.Convert(Character(), "enemy_test.xff").Xff!.Clip!;
        Assert.Equal(8, clip.Version);
        Assert.Equal(160, clip.StartTime);
        // 100 ms is 480 ticks; keys at 0, 33, 66, 99 and 100 ms (rounded to ticks)
        Assert.Equal(160 + 480, clip.EndTime);
        int[] ticks = [160, 318, 477, 635, 640];
        Assert.All(clip.Bones, b => Assert.Equal(ticks, b.PositionKeys.Select(k => k.Time)));
        Assert.All(clip.Bones, b => Assert.Equal(ticks, b.RotationKeys.Select(k => k.Time)));
        float s = XffSpace.RfScale;
        // body (0..100): keys 0..3 at 0, 33, 66, 99 ms; 100 ms wraps to the start
        Close([0f, 1, 2, 3, 0], clip.Bones[0].PositionKeys.Select(k => k.Position.X / s));
        // arm (−33..67, relative to the body): −33 holds key 0; 0 is key 0; 33 key 1; 66 key 2; 67 is the end and wraps to −33
        Close([10f, 10, 20, 30, 10], clip.Bones[1].PositionKeys.Select(k => k.Position.Y / s));
        // tail (0..50): 0, 33 → keys 0 and 1; 66 wraps to 16 (between them); 99 wraps to 49 (past the last key: held); 100 wraps to 0
        var z = clip.Bones[2].PositionKeys.Select(k => k.Position.Z / s).ToArray();
        Assert.Equal(0f, z[0], 3);
        Assert.Equal(33f, z[1], 3);
        Assert.Equal(16f, z[2], 3);
        Assert.Equal(33f, z[3], 3);
        Assert.Equal(0f, z[4], 3);
        // straight lines between keys, as the game moves positions
        var first = clip.Bones[0].PositionKeys;
        Assert.Equal(first[0].Position + (first[1].Position - first[0].Position) / 3f, first[0].OutControl);
        Assert.DoesNotContain(ClipLinter.AnalyzeStructure(clip), d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
    }

    /// <summary>The characters' texture, skin.pcx: one grey pixel.</summary>
    private static readonly byte[] Skin = SyntheticPcx.Write(1, 1, 8, 1, (_, _) => 0, [128, 128, 128]);

    [Fact]
    public void Character_ConvertsToAMeshAndAClip()
    {
        var conversion = LegacyMeshSupport.Convert(Character(), "enemy_test.xff", n => n == "skin.pcx" ? Skin : null);
        Assert.Equal("enemy_test.v3c", conversion.OutputName);
        Assert.NotNull(conversion.Clip);
        Assert.Equal("enemy_test_walk.rfa", conversion.Clip!.OutputName);
        var read = RfaReader.Read(conversion.Clip.Bytes, conversion.Clip.OutputName);
        Assert.Equal(3, read.BoneCount);
        Assert.Equal(conversion.Mesh.Bones.Length, read.BoneCount);
        Assert.Equal(["enemy_test.v3c", "enemy_test_walk.rfa", "skin.tga"], conversion.AllFiles.Select(f => f.Name));
        // posing the converted mesh with its clip at the start reproduces the bind pose
        var skeleton = Skeleton.FromFile(conversion.Mesh);
        var locals = new Rigid[skeleton.Count];
        ClipSampler.SampleLocals(read, read.StartTime, locals);
        var world = new Rigid[skeleton.Count];
        ForwardKinematics.Solve(skeleton, locals, world);
        for (int b = 0; b < skeleton.Count; b++)
            Assert.True(Vector3.Distance(world[b].Position, skeleton.RestWorld[b].Position) < 1e-3f, $"bone {b}");
    }

    [Fact]
    public void Character_WithAStaticPart_GetsARootBone()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        x.MeshObject(marker, "hat", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        uint body = x.Track(marker, "anim_body", 0, 100, 30f, [Key(Vector3.Zero), Key(Vector3.UnitX)]);
        x.MeshObject(body, "body", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        var mesh = XffMeshConverter.Convert(x.ToArray(), "enemy_hat.xff");
        Assert.Equal([XffMeshConverter.RootBoneName, "body"], mesh.Description.Bones.Select(b => b.Name.Text));
        Assert.Equal([-1, 0], mesh.Description.Bones.Select(b => b.ParentIndex));
        Assert.Equal(2, mesh.Xff!.Clip!.BoneCount);
        Assert.Contains(mesh.Notes, n => n.Contains("not move with any track", StringComparison.Ordinal));
    }

    // ── hostile scenes ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Depth costs nothing extra: a character whose part hangs 20,000 nodes below its track (and one whose 20,000 nested
    /// tracks all move it) classifies, opens and converts without recursion, well under a second each, where a recursive
    /// walk overflowed the stack at 5,000. With nested tracks the first 50 become bones and the part moves with the last.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeepHierarchy_ClassifiesOpensAndConverts(bool nestedTracks)
    {
        const int depth = 20_000;
        var x = new SyntheticXff();
        uint root = x.Root();
        uint parent = x.Marker(3202, root, "player");
        if (!nestedTracks) parent = x.Track(parent, "anim_body", 0, 66, 30f, [Key(Vector3.Zero), Key(Vector3.UnitX), Key(Vector3.Zero)]);
        for (int i = 0; i < depth; i++)
            parent = nestedTracks ? x.Track(parent, "anim_g" + i, 0, 66, 30f, [Key(Vector3.Zero), Key(Vector3.UnitX)]) : x.Group(parent, "g" + i);
        x.MeshObject(parent, "body", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        byte[] bytes = x.ToArray();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(XffKind.Character, XffClassifier.TryClassify(bytes, "enemy_deep.xff"));
        long classify = watch.ElapsedMilliseconds;
        watch.Restart();
        Assert.True(LegacyMeshSupport.TryRead(bytes, "enemy_deep.xff", out var mesh, out var error), error?.Message);
        long open = watch.ElapsedMilliseconds;
        watch.Restart();
        var conversion = LegacyMeshSupport.Convert(bytes, "enemy_deep.xff", n => n == "skin.pcx" ? [1] : null);
        long convert = watch.ElapsedMilliseconds;

        Assert.Equal("enemy_deep.v3c", conversion.OutputName);
        Assert.NotNull(conversion.Clip);
        var bones = mesh!.Description.Bones;
        if (nestedTracks)
        {
            Assert.Equal(V3dBoneSection.MaxBones, bones.Length);
            Assert.Equal(Enumerable.Range(-1, V3dBoneSection.MaxBones), bones.Select(b => b.ParentIndex));
            var vertices = mesh.Description.Submeshes[0].Lods[0].Groups.SelectMany(g => g.Vertices);
            Assert.All(vertices, v => Assert.Equal(V3dBoneSection.MaxBones - 1, Assert.Single(v.Influences).Bone));
            Assert.Contains(mesh.Notes, n => n.Contains("beyond the 50 bones", StringComparison.Ordinal));
        }
        else Assert.Equal(["body"], bones.Select(b => b.Name.Text));
        // generous for a loaded machine; the quadratic walks took over five seconds to classify alone
        Assert.True(classify + open + convert < 3_000, $"classify {classify:N0} ms, open {open:N0} ms, convert {convert:N0} ms");
    }

    /// <summary>
    /// The clip's samples follow the declared range, so a two-key track declared to run 0 to 300,000 ms at 1,000 keys a
    /// second would ask for 300,001 keys a bone: past <see cref="XffMeshConverter.MaxClipKeys"/>, the character converts
    /// without its clip and the notes say why, without allocating for the range. A range just inside the limit keeps it.
    /// </summary>
    [Theory]
    [InlineData(300_000, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(XffMeshConverter.MaxClipKeys - 1, true)]
    public void DeclaredTrackRange_BoundsTheClipSamples(int end, bool clipKept)
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint marker = x.Marker(3202, root, "player");
        uint track = x.Track(marker, "anim_body", 0, end, 1000f, [Key(Vector3.Zero), Key(Vector3.UnitX)]);
        x.MeshObject(track, "body", Triangle, Up, [0, 1, 2], null, TriangleUvs, "skin.pcx");
        byte[] bytes = x.ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(LegacyMeshSupport.TryRead(bytes, "enemy_long.xff", out var mesh, out var error), error?.Message);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 64L << 20, $"opening a {bytes.Length}-byte character allocated {allocated / (1 << 20):N0} MB");
        Assert.Equal(V3dKind.Character, mesh!.Description.Kind);
        if (clipKept)
        {
            Assert.Equal(XffMeshConverter.MaxClipKeys, mesh.Xff!.Clip!.Bones[0].RotationKeys.Length);
            Assert.Equal("enemy_long_walk.rfa", mesh.Xff.ClipName);
            return;
        }
        Assert.Null(mesh.Xff!.Clip);
        Assert.Null(mesh.Xff.ClipName);
        Assert.Contains(mesh.Notes, n => n.Contains("The animation was not converted", StringComparison.Ordinal) && n.Contains("2,000", StringComparison.Ordinal));
        var conversion = LegacyMeshSupport.Convert(bytes, "enemy_long.xff", n => n == "skin.pcx" ? Skin : null);
        Assert.Null(conversion.Clip);
        Assert.Equal(["enemy_long.v3c", "skin.tga"], conversion.AllFiles.Select(f => f.Name));
    }
}
