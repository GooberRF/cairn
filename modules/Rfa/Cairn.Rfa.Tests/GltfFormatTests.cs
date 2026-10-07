using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json.Nodes;
using Cairn.Formats.Gltf;

namespace Cairn.Rfa.Tests;

public class GltfFormatTests
{
    // ---- sample document ----

    /// <summary>A valid 2x1 RGBA PNG, built from scratch (zlib IDAT, real CRCs).</summary>
    internal static byte[] TinyPng()
    {
        static void Chunk(MemoryStream ms, string type, byte[] body)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            byte[] typed = [.. System.Text.Encoding.ASCII.GetBytes(type), .. body];
            ms.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(typed));
            ms.Write(len);
        }
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, 2);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA
        byte[] raw = [0, 255, 0, 0, 255, 0, 0, 255, 128];
        using var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true)) zs.Write(raw);
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", ihdr);
        Chunk(ms, "IDAT", z.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static void Decorate(GltfProperty p, string tag)
    {
        p.Extras = new JsonObject { ["tag"] = tag, ["n"] = 1.25, ["list"] = new JsonArray(1, "two", null, true) };
        p.Extensions = new JsonObject { ["EXT_test"] = new JsonObject { ["v"] = tag } };
        p.UnknownMembers["x_unknown"] = JsonValue.Create(tag);
    }

    /// <summary>
    /// A document exercising every component type (normalised and not), interleaving, sparse
    /// accessors with and without a buffer view, matrices, skinning with two joint sets, morph
    /// targets, materials and images, an animation with every interpolation, and extras,
    /// extensions and unknown members on every object.
    /// </summary>
    internal static GltfDocument Sample()
    {
        var doc = new GltfDocument();
        doc.Asset.Generator = "RFA Workbench tests";
        doc.Asset.Copyright = "none";
        var b = new GltfBufferBuilder(doc);

        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(0.1f, 1f / 3f, -2.5f)];
        int pos = b.AddVector3(positions, minMax: true);
        int nrm = b.AddIntegers([127, 0, -128, 0, 127, 0, -127, 64, 1, 0, 0, 127], GltfComponentType.Byte, GltfAccessorType.Vec3, normalized: true, target: 34962);
        int uv0 = b.AddIntegers([0, 65535, 32768, 0, 65535, 65535, 1, 2], GltfComponentType.UnsignedShort, GltfAccessorType.Vec2, normalized: true, target: 34962);
        int uv1 = b.AddIntegers([-32768, 32767, 0, -1, 100, -100, 32767, 32767], GltfComponentType.Short, GltfAccessorType.Vec2, normalized: true, target: 34962);
        int col = b.AddIntegers([255, 0, 128, 255, 0, 255, 0, 255, 1, 2, 3, 4, 10, 20, 30, 40], GltfComponentType.UnsignedByte, GltfAccessorType.Vec4, normalized: true, target: 34962);
        int j0 = b.AddUnsignedShortVec4([0, 1, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 1, 0, 0, 0]);
        int w0 = b.AddVector4([new(0.5f, 0.5f, 0, 0), new(1, 0, 0, 0), new(0.25f, 0.75f, 0, 0), new(1, 0, 0, 0)]);
        int j1 = b.AddIntegers([1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0], GltfComponentType.UnsignedByte, GltfAccessorType.Vec4, target: 34962);
        int w1 = b.AddIntegers([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], GltfComponentType.UnsignedByte, GltfAccessorType.Vec4, normalized: true, target: 34962);
        int custom = b.AddIntegers([-5, 7, -128, 127], GltfComponentType.Byte, GltfAccessorType.Scalar);
        int customShort = b.AddIntegers([-30000, 30000, 0, 1], GltfComponentType.Short, GltfAccessorType.Scalar);
        int customUInt = b.AddIntegers([0, 1, 70000, int.MaxValue], GltfComponentType.UnsignedInt, GltfAccessorType.Scalar);
        int idx = b.AddIndices([0, 1, 2, 0, 2, 3]);
        int bigIdx = b.AddIndices([0, 70000, 3]);
        int mat2 = b.AddIntegers([1, 2, 3, 4, 5, 6, 7, 8], GltfComponentType.UnsignedByte, GltfAccessorType.Mat2);
        int mat3 = b.AddIntegers([1, -2, 3, -4, 5, -6, 7, -8, 9], GltfComponentType.Short, GltfAccessorType.Mat3, normalized: true);

        // Interleaved: 12 bytes of float position + 4 bytes of colour, stride 16.
        var inter = new byte[16 * 2];
        for (int v = 0; v < 2; v++)
        {
            for (int k = 0; k < 3; k++) BinaryPrimitives.WriteSingleLittleEndian(inter.AsSpan(v * 16 + k * 4), v * 10 + k);
            for (int k = 0; k < 4; k++) inter[v * 16 + 12 + k] = (byte)(v * 100 + k * 50);
        }
        int interView = b.AddBufferView(inter, byteStride: 16, target: 34962);
        doc.Accessors.Add(new GltfAccessor { BufferView = interView, ComponentType = GltfComponentType.Float, Count = 2, Type = GltfAccessorType.Vec3 });
        int interPos = doc.Accessors.Count - 1;
        doc.Accessors.Add(new GltfAccessor { BufferView = interView, ByteOffset = 12, ComponentType = GltfComponentType.UnsignedByte, Normalized = true, Count = 2, Type = GltfAccessorType.Vec4 });
        int interCol = doc.Accessors.Count - 1;

        // Sparse on top of a buffer view, and sparse over implicit zeros.
        var sparseIdx = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(sparseIdx, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(sparseIdx.AsSpan(2), 3);
        int sparseIdxView = b.AddBufferView(sparseIdx);
        var sparseVal = new byte[24];
        float[] sv = [9, 8, 7, 6, 5, 4];
        for (int k = 0; k < 6; k++) BinaryPrimitives.WriteSingleLittleEndian(sparseVal.AsSpan(k * 4), sv[k]);
        int sparseValView = b.AddBufferView(sparseVal);
        doc.Accessors.Add(new GltfAccessor
        {
            BufferView = doc.Accessors[pos].BufferView,
            ComponentType = GltfComponentType.Float,
            Count = 4,
            Type = GltfAccessorType.Vec3,
            Sparse = new GltfSparse
            {
                Count = 2,
                Indices = new GltfSparseIndices { BufferView = sparseIdxView, ComponentType = GltfComponentType.UnsignedShort },
                Values = new GltfSparseValues { BufferView = sparseValView },
            },
        });
        int sparseBased = doc.Accessors.Count - 1;
        doc.Accessors.Add(new GltfAccessor
        {
            ComponentType = GltfComponentType.Float,
            Count = 4,
            Type = GltfAccessorType.Vec3,
            Sparse = new GltfSparse
            {
                Count = 1,
                Indices = new GltfSparseIndices { BufferView = sparseIdxView, ByteOffset = 2, ComponentType = GltfComponentType.UnsignedShort },
                Values = new GltfSparseValues { BufferView = sparseValView, ByteOffset = 12 },
            },
        });
        int sparseZero = doc.Accessors.Count - 1;

        int target0 = b.AddVector3([new(0, 0.5f, 0), Vector3.Zero, Vector3.Zero, new(1, 1, 1)]);
        int ibm = b.AddMatrices([Matrix4x4.Identity, Matrix4x4.CreateTranslation(-1, -2, -3)]);

        int times = b.AddScalars([0, 0.5f, 1]);
        int trans = b.AddVector3([new(0, 0, 0), new(1, 2, 3), new(4, 5, 6)], target: null);
        int rots = b.AddQuaternions([Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 2)]);
        int scales = b.AddFloats(Enumerable.Range(0, 27).Select(i => i * 0.5f).ToArray(), GltfAccessorType.Vec3);
        int weights = b.AddScalars([0, 1, 0.5f], minMax: false);

        byte[] png = TinyPng();
        int pngView = b.AddBufferView(png);
        b.Finish();

        doc.Images.Add(new GltfImage { Name = "inView", BufferView = pngView, MimeType = "image/png" });
        doc.Images.Add(new GltfImage { Name = "embedded", Data = png });
        doc.Images.Add(new GltfImage { Name = "file", Uri = "tex/a%20b.png", Data = png });
        doc.Samplers.Add(new GltfSampler { MagFilter = 9729, MinFilter = 9987, WrapS = 33071, WrapT = 10497 });
        doc.Textures.Add(new GltfTexture { Name = "t0", Sampler = 0, Source = 0 });
        doc.Textures.Add(new GltfTexture { Source = 1 });
        doc.Textures.Add(new GltfTexture { Source = 2 });
        doc.Materials.Add(new GltfMaterial
        {
            Name = "mat",
            PbrMetallicRoughness = new GltfPbrMetallicRoughness
            {
                BaseColorFactor = [0.1f, 1f / 3f, float.Epsilon, 1],
                BaseColorTexture = new GltfTextureInfo { Index = 0, TexCoord = 1 },
                MetallicFactor = 0.7f,
                RoughnessFactor = float.MaxValue,
                MetallicRoughnessTexture = new GltfTextureInfo { Index = 1 },
            },
            NormalTexture = new GltfTextureInfo { Index = 2, Scale = 0.3f },
            OcclusionTexture = new GltfTextureInfo { Index = 1, Strength = 0.9f },
            EmissiveTexture = new GltfTextureInfo { Index = 0 },
            EmissiveFactor = [1, 0.5f, 0.25f],
            AlphaMode = "MASK",
            AlphaCutoff = 0.4f,
            DoubleSided = true,
        });

        var prim = new GltfPrimitive { Indices = idx, Material = 0, Mode = 4 };
        prim.Attributes["POSITION"] = pos;
        prim.Attributes["NORMAL"] = nrm;
        prim.Attributes["TEXCOORD_0"] = uv0;
        prim.Attributes["TEXCOORD_1"] = uv1;
        prim.Attributes["COLOR_0"] = col;
        prim.Attributes["JOINTS_0"] = j0;
        prim.Attributes["WEIGHTS_0"] = w0;
        prim.Attributes["JOINTS_1"] = j1;
        prim.Attributes["WEIGHTS_1"] = w1;
        prim.Attributes["_CUSTOM"] = custom;
        prim.Targets.Add(new Dictionary<string, int> { ["POSITION"] = target0 });
        var mesh = new GltfMesh { Name = "mesh", Weights = [0.5f] };
        mesh.Primitives.Add(prim);
        var prim2 = new GltfPrimitive { Indices = bigIdx, Mode = 0 };
        prim2.Attributes["POSITION"] = interPos;
        prim2.Attributes["COLOR_0"] = interCol;
        mesh.Primitives.Add(prim2);
        doc.Meshes.Add(mesh);

        var root = new GltfNode { Name = "root", Translation = new Vector3(0.1f, -0f, 1e-30f), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.5f), Scale = new Vector3(2, 2, 2) };
        root.Children.AddRange([1, 2, 3]);
        doc.Nodes.Add(root);
        var m = Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(1, 2, 3);
        doc.Nodes.Add(new GltfNode { Name = "matrixJoint", Matrix = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44] });
        doc.Nodes.Add(new GltfNode { Name = "joint2", Translation = new Vector3(0, 1, 0) });
        doc.Nodes.Add(new GltfNode { Name = "skinned", Mesh = 0, Skin = 0, Camera = 0, Weights = [0.25f] });
        var skin = new GltfSkin { Name = "skin", InverseBindMatrices = ibm, Skeleton = 0 };
        skin.Joints.AddRange([1, 2]);
        doc.Skins.Add(skin);
        var scene = new GltfScene { Name = "scene" };
        scene.Nodes.Add(0);
        doc.Scenes.Add(scene);
        doc.Scene = 0;
        doc.Cameras.Add(JsonNode.Parse("""{"type":"perspective","perspective":{"yfov":0.8,"znear":0.01}}"""));

        var anim = new GltfAnimation { Name = "anim" };
        anim.Samplers.Add(new GltfAnimationSampler { Input = times, Output = trans });
        anim.Samplers.Add(new GltfAnimationSampler { Input = times, Output = rots, Interpolation = GltfInterpolation.Step });
        anim.Samplers.Add(new GltfAnimationSampler { Input = times, Output = scales, Interpolation = GltfInterpolation.CubicSpline });
        anim.Samplers.Add(new GltfAnimationSampler { Input = times, Output = weights, Interpolation = GltfInterpolation.Linear });
        anim.Channels.Add(new GltfAnimationChannel { Sampler = 0, Target = new GltfAnimationTarget { Node = 1, Path = "translation" } });
        anim.Channels.Add(new GltfAnimationChannel { Sampler = 1, Target = new GltfAnimationTarget { Node = 1, Path = "rotation" } });
        anim.Channels.Add(new GltfAnimationChannel { Sampler = 2, Target = new GltfAnimationTarget { Node = 2, Path = "scale" } });
        anim.Channels.Add(new GltfAnimationChannel { Sampler = 3, Target = new GltfAnimationTarget { Node = 3, Path = "weights" } });
        doc.Animations.Add(anim);

        doc.ExtensionsUsed.Add("EXT_test");
        doc.ExtensionsRequired.Add("EXT_test");
        doc.UnknownMembers["x_topLevel"] = new JsonObject { ["a"] = 1 };

        // Extras, extensions and unknown members on every object.
        Decorate(doc, "doc");
        Decorate(doc.Asset, "asset");
        foreach (var s in doc.Scenes) Decorate(s, "scene");
        for (int i = 0; i < doc.Nodes.Count; i++) Decorate(doc.Nodes[i], $"node{i}");
        foreach (var me in doc.Meshes)
        {
            Decorate(me, "mesh");
            foreach (var p in me.Primitives) Decorate(p, "prim");
        }
        Decorate(skin, "skin");
        var mat = doc.Materials[0];
        Decorate(mat, "mat");
        Decorate(mat.PbrMetallicRoughness!, "pbr");
        Decorate(mat.PbrMetallicRoughness!.BaseColorTexture!, "baseTex");
        Decorate(mat.NormalTexture!, "normalTex");
        foreach (var t in doc.Textures) Decorate(t, "tex");
        for (int i = 0; i < doc.Images.Count; i++) Decorate(doc.Images[i], $"img{i}");
        foreach (var s in doc.Samplers) Decorate(s, "sampler");
        Decorate(anim, "anim");
        foreach (var c in anim.Channels) { Decorate(c, "chan"); Decorate(c.Target, "target"); }
        foreach (var s in anim.Samplers) Decorate(s, "asampler");
        for (int i = 0; i < doc.Accessors.Count; i++) Decorate(doc.Accessors[i], $"acc{i}");
        var sp = doc.Accessors[sparseBased].Sparse!;
        Decorate(sp, "sparse");
        Decorate(sp.Indices, "sparseIdx");
        Decorate(sp.Values, "sparseVal");
        for (int i = 0; i < doc.BufferViews.Count; i++) Decorate(doc.BufferViews[i], $"view{i}");
        Decorate(doc.Buffers[0], "buffer");
        _ = (customShort, customUInt, mat2, mat3, sparseZero);
        return doc;
    }

    // ---- comparison ----

    /// <summary>
    /// Asserts two documents are equivalent: the JSON trees (minus the storage-dependent buffers,
    /// views and images) are identical, every accessor reads the same values, and images agree.
    /// </summary>
    internal static void AssertEquivalent(GltfDocument expected, GltfDocument actual)
    {
        var a = Strip(expected);
        var b = Strip(actual);
        Assert.Equal(a.ToJsonString(), b.ToJsonString());
        Assert.True(JsonNode.DeepEquals(a, b));

        Assert.Equal(expected.Accessors.Count, actual.Accessors.Count);
        for (int i = 0; i < expected.Accessors.Count; i++)
        {
            float[] fe = GltfAccessorReader.ReadFloats(expected, i, "expected");
            float[] fa = GltfAccessorReader.ReadFloats(actual, i, "actual");
            Assert.Equal(fe, fa);
            if (expected.Accessors[i].ComponentType != GltfComponentType.Float)
                Assert.Equal(GltfAccessorReader.ReadInts(expected, i), GltfAccessorReader.ReadInts(actual, i));
        }
        Assert.Equal(expected.Images.Count, actual.Images.Count);
        for (int i = 0; i < expected.Images.Count; i++)
        {
            Assert.Equal(expected.Images[i].Name, actual.Images[i].Name);
            Assert.Equal(ImageBytes(expected, i), ImageBytes(actual, i));
            Assert.True(JsonNode.DeepEquals(expected.Images[i].Extras, actual.Images[i].Extras));
            Assert.True(JsonNode.DeepEquals(expected.Images[i].Extensions, actual.Images[i].Extensions));
            Assert.True(JsonNode.DeepEquals(expected.Images[i].UnknownMembers["x_unknown"], actual.Images[i].UnknownMembers["x_unknown"]));
        }
    }

    /// <summary>An image's bytes, whether held in Data or only in its buffer view.</summary>
    private static byte[]? ImageBytes(GltfDocument doc, int image)
    {
        var img = doc.Images[image];
        if (img.Data is not null || img.BufferView is not int v) return img.Data;
        var view = doc.BufferViews[v];
        return doc.Buffers[view.Buffer].Data!.AsSpan(view.ByteOffset, view.ByteLength).ToArray();
    }

    private static JsonObject Strip(GltfDocument doc)
    {
        var root = JsonNode.Parse(GltfWriter.ToJson(doc))!.AsObject();
        root.Remove("buffers");
        root.Remove("bufferViews");
        root.Remove("images");
        return root;
    }

    // ---- round trips ----

    [Fact]
    public void GltfWithBinRoundTrips()
    {
        using var temp = new TempFolder();
        var doc = Sample();
        string path = temp.File("model one.gltf");
        GltfWriter.WriteGltf(doc, path);
        Assert.True(File.Exists(temp.File("model one.bin")));
        Assert.True(File.Exists(temp.File(Path.Combine("tex", "a b.png"))));

        var back = GltfReader.ReadFile(path, new GltfReadOptions { LoadImages = true });
        Assert.Equal("model%20one.bin", back.Buffers[0].Uri);
        AssertEquivalent(doc, back);
        Assert.Equal("tex/a%20b.png", back.Images[2].Uri);
        Assert.StartsWith("data:image/png;base64,", back.Images[1].Uri);

        // Second generation is byte-identical.
        string path2 = temp.File("again.gltf");
        GltfWriter.WriteGltf(back, path2);
        var back2 = GltfReader.ReadFile(path2, new GltfReadOptions { LoadImages = true });
        AssertEquivalent(back, back2);
        Assert.Equal(File.ReadAllBytes(temp.File("model one.bin")), File.ReadAllBytes(temp.File("again.bin")));
    }

    [Fact]
    public void GltfWithDataUrisRoundTrips()
    {
        using var temp = new TempFolder();
        var doc = Sample();
        string path = temp.File("embedded.gltf");
        GltfWriter.WriteGltf(doc, path, new GltfWriteOptions { EmbedBuffers = true, Indented = false });
        Assert.False(File.Exists(temp.File("embedded.bin")));
        string json = File.ReadAllText(path);
        Assert.Contains("data:application/octet-stream;base64,", json, StringComparison.Ordinal);

        var back = GltfReader.ReadFile(path, new GltfReadOptions { LoadImages = true });
        AssertEquivalent(doc, back);

        // Without LoadImages the external image is left unread.
        var lazy = GltfReader.ReadFile(path);
        Assert.Null(lazy.Images[2].Data);
        Assert.NotNull(lazy.Images[0].Data);
    }

    [Fact]
    public void GlbRoundTripsAndMovesImagesIntoTheBinChunk()
    {
        using var temp = new TempFolder();
        var doc = Sample();
        string imageUriBefore = doc.Images[2].Uri!;
        int viewsBefore = doc.BufferViews.Count;
        string path = temp.File("model.glb");
        GltfWriter.WriteGlb(doc, path);

        // The document itself is untouched.
        Assert.Equal(imageUriBefore, doc.Images[2].Uri);
        Assert.Null(doc.Images[1].BufferView);
        Assert.Equal(viewsBefore, doc.BufferViews.Count);

        byte[] glb = File.ReadAllBytes(path);
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal(0, glb.Length % 4);
        var back = GltfReader.ReadFile(path);
        AssertEquivalent(doc, back);
        Assert.Null(back.Buffers[0].Uri);
        foreach (var img in back.Images)
        {
            Assert.Null(img.Uri);
            Assert.NotNull(img.BufferView);
            Assert.Equal("image/png", img.MimeType);
        }
        Assert.Equal(viewsBefore + 2, back.BufferViews.Count);

        // GLB -> GLB is stable.
        Assert.Equal(glb, GltfWriter.ToGlb(back));
    }

    [Fact]
    public void GlbWithoutBuffersStillEmbedsImages()
    {
        var doc = new GltfDocument();
        doc.Images.Add(new GltfImage { Data = TinyPng() });
        var back = GltfReader.Read(GltfWriter.ToGlb(doc), "img.glb", null);
        Assert.Single(back.Buffers);
        Assert.Equal(TinyPng(), back.Images[0].Data);
        Assert.Equal("image/png", back.Images[0].MimeType);
    }

    [Fact]
    public void FloatsRoundTripExactly()
    {
        float[] tricky = [0.1f, 1f / 3f, float.Epsilon, float.MaxValue, -float.MaxValue, 1e-30f, 16777217f, 3.4028235e38f, 0.3f, 123456.79f, -0f];
        var doc = new GltfDocument();
        doc.Nodes.Add(new GltfNode { Weights = tricky });
        doc.Nodes.Add(new GltfNode { Matrix = Enumerable.Range(0, 16).Select(i => MathF.Sqrt(i + 0.123f)).ToArray() });
        var back = GltfReader.Read(GltfWriter.ToGlb(doc), "f.glb", null);
        for (int i = 0; i < tricky.Length; i++)
            Assert.Equal(BitConverter.SingleToInt32Bits(tricky[i]), BitConverter.SingleToInt32Bits(back.Nodes[0].Weights![i]));
        Assert.Equal(doc.Nodes[1].Matrix, back.Nodes[1].Matrix);
        Assert.DoesNotContain("0.10000000149", GltfWriter.ToJson(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtrasExtensionsAndUnknownMembersSurviveEverywhere()
    {
        var doc = Sample();
        var back = GltfReader.Read(GltfWriter.ToGlb(doc), "x.glb", null);
        Assert.True(JsonNode.DeepEquals(doc.Extras, back.Extras));
        Assert.True(JsonNode.DeepEquals(doc.Asset.Extensions, back.Asset.Extensions));
        Assert.Equal("asset", back.Asset.UnknownMembers["x_unknown"]!.GetValue<string>());
        Assert.Equal("prim", back.Meshes[0].Primitives[1].UnknownMembers["x_unknown"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(doc.Materials[0].NormalTexture!.Extras, back.Materials[0].NormalTexture!.Extras));
        Assert.Equal("target", back.Animations[0].Channels[2].Target.UnknownMembers["x_unknown"]!.GetValue<string>());
        Assert.Equal("sparseVal", back.Accessors[18].Sparse!.Values.UnknownMembers["x_unknown"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(doc.UnknownMembers["x_topLevel"], back.UnknownMembers["x_topLevel"]));
        Assert.Equal(["EXT_test"], back.ExtensionsUsed);
        Assert.Equal(["EXT_test"], back.ExtensionsRequired);
        Assert.True(JsonNode.DeepEquals(doc.Cameras[0], back.Cameras[0]));
        Assert.Equal(["POSITION", "NORMAL", "TEXCOORD_0", "TEXCOORD_1", "COLOR_0", "JOINTS_0", "WEIGHTS_0", "JOINTS_1", "WEIGHTS_1", "_CUSTOM"],
            back.Meshes[0].Primitives[0].Attributes.Keys);
    }

    [Fact]
    public void WriterOmitsUnsetMembersAndUsesConventionalOrder()
    {
        var doc = new GltfDocument();
        doc.Nodes.Add(new GltfNode());
        doc.Accessors.Add(new GltfAccessor { ComponentType = GltfComponentType.Float, Count = 0, Type = GltfAccessorType.Vec3 });
        var json = JsonNode.Parse(GltfWriter.ToJson(doc))!.AsObject();
        Assert.Equal(["asset", "nodes", "accessors"], json.Select(p => p.Key));
        Assert.Empty(json["nodes"]![0]!.AsObject());
        Assert.Equal(["componentType", "count", "type"], json["accessors"]![0]!.AsObject().Select(p => p.Key));
        Assert.Equal("2.0", json["asset"]!["version"]!.GetValue<string>());
    }

    // ---- accessor reader ----

    [Fact]
    public void NormalisationFollowsTheSpec()
    {
        var doc = Sample();
        float[] nrm = GltfAccessorReader.ReadFloats(doc, 1);
        Assert.Equal([1f, 0f, -1f, 0f, 1f, 0f, -1f, 64 / 127f, 1 / 127f, 0, 0, 1], nrm);
        float[] uv1 = GltfAccessorReader.ReadFloats(doc, 3);
        Assert.Equal(-1f, uv1[0]);
        Assert.Equal(1f, uv1[1]);
        Assert.Equal(-1 / 32767f, uv1[3]);
        float[] uv0 = GltfAccessorReader.ReadFloats(doc, 2);
        Assert.Equal(1f, uv0[1]);
        Assert.Equal(32768 / 65535f, uv0[2]);
        float[] col = GltfAccessorReader.ReadFloats(doc, 4);
        Assert.Equal(1f, col[0]);
        Assert.Equal(0f, col[1]);
        Assert.Equal(128 / 255f, col[2]);

        // Non-normalised integers convert as is.
        Assert.Equal([-5f, 7f, -128f, 127f], GltfAccessorReader.ReadFloats(doc, 9));
        Assert.Equal([-30000, 30000, 0, 1], GltfAccessorReader.ReadInts(doc, 10));
        Assert.Equal([0, 1, 70000, int.MaxValue], GltfAccessorReader.ReadInts(doc, 11));
        Assert.Equal([0, 1, 2, 0, 2, 3], GltfAccessorReader.ReadInts(doc, 12));
        Assert.Equal(GltfComponentType.UnsignedShort, doc.Accessors[12].ComponentType);
        Assert.Equal(GltfComponentType.UnsignedInt, doc.Accessors[13].ComponentType);
        Assert.Equal([0, 70000, 3], GltfAccessorReader.ReadInts(doc, 13));
        Assert.Equal([0f], doc.Accessors[12].Min!);
        Assert.Equal([3f], doc.Accessors[12].Max!);
    }

    [Fact]
    public void ByteNormalisationEdgeCases()
    {
        var doc = new GltfDocument();
        var b = new GltfBufferBuilder(doc);
        int s = b.AddIntegers([-32768, -32767, 32767, 0], GltfComponentType.Short, GltfAccessorType.Scalar, normalized: true);
        int ub = b.AddIntegers([255, 0], GltfComponentType.UnsignedByte, GltfAccessorType.Scalar, normalized: true);
        int sb = b.AddIntegers([-128, -127, 127], GltfComponentType.Byte, GltfAccessorType.Scalar, normalized: true);
        int us = b.AddIntegers([65535, 0], GltfComponentType.UnsignedShort, GltfAccessorType.Scalar, normalized: true);
        b.Finish();
        Assert.Equal([-1f, -1f, 1f, 0f], GltfAccessorReader.ReadFloats(doc, s));
        Assert.Equal([1f, 0f], GltfAccessorReader.ReadFloats(doc, ub));
        Assert.Equal([-1f, -1f, 1f], GltfAccessorReader.ReadFloats(doc, sb));
        Assert.Equal([1f, 0f], GltfAccessorReader.ReadFloats(doc, us));
    }

    [Fact]
    public void MatrixColumnPaddingIsApplied()
    {
        var doc = Sample();
        // MAT2 of bytes: two 2-byte columns, each padded to 4 (two elements of 8 bytes).
        Assert.Equal(16, doc.BufferViews[doc.Accessors[14].BufferView!.Value].ByteLength);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], GltfAccessorReader.ReadInts(doc, 14));
        // MAT3 of shorts: three 6-byte columns, each padded to 8.
        Assert.Equal(24, doc.BufferViews[doc.Accessors[15].BufferView!.Value].ByteLength);
        Assert.Equal([1, -2, 3, -4, 5, -6, 7, -8, 9], GltfAccessorReader.ReadInts(doc, 15).Select(v => (int)(short)v));
        var mat2Bytes = doc.Buffers[0].Data!.AsSpan(doc.BufferViews[doc.Accessors[14].BufferView!.Value].ByteOffset, 16).ToArray();
        Assert.Equal(new byte[] { 1, 2, 0, 0, 3, 4, 0, 0, 5, 6, 0, 0, 7, 8, 0, 0 }, mat2Bytes);
    }

    [Fact]
    public void InterleavedSparseAndMatrixReads()
    {
        var doc = Sample();
        // Interleaved position (accessor 16) shares a stride-16 view with a colour.
        Assert.Equal([new Vector3(0, 1, 2), new Vector3(10, 11, 12)], GltfAccessorReader.ReadVector3(doc, 16));

        // Sparse based on POSITION: elements 1 and 3 replaced.
        Vector3[] sparse = GltfAccessorReader.ReadVector3(doc, 18);
        Assert.Equal(new Vector3(0, 0, 0), sparse[0]);
        Assert.Equal(new Vector3(9, 8, 7), sparse[1]);
        Assert.Equal(new Vector3(0, 1, 0), sparse[2]);
        Assert.Equal(new Vector3(6, 5, 4), sparse[3]);

        // Sparse over zeros: only element 3 set.
        Assert.Equal([Vector3.Zero, Vector3.Zero, Vector3.Zero, new Vector3(6, 5, 4)], GltfAccessorReader.ReadVector3(doc, 19));

        // MAT4 back as row-vector matrices with translation in M41..M43.
        Matrix4x4[] ibm = GltfAccessorReader.ReadMatrices(doc, doc.Skins[0].InverseBindMatrices!.Value);
        Assert.Equal(Matrix4x4.Identity, ibm[0]);
        Assert.Equal(new Vector3(-1, -2, -3), ibm[1].Translation);

        Quaternion[] q = GltfAccessorReader.ReadQuaternions(doc, doc.Animations[0].Samplers[1].Output);
        Assert.Equal(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 1), q[1]);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0, 0), GltfAccessorReader.ReadVector4(doc, 6)[0]);
        Assert.Equal(new Vector2(-1, 1), GltfAccessorReader.ReadVector2(doc, 3)[0]);
    }

    [Fact]
    public void InterleavedAccessorsAreTheExpectedOnes()
    {
        var doc = Sample();
        var pos = doc.Accessors[16];
        var col = doc.Accessors[17];
        Assert.Equal(pos.BufferView, col.BufferView);
        Assert.Equal(16, doc.BufferViews[pos.BufferView!.Value].ByteStride);
        Assert.Equal([0f, 50 / 255f, 100 / 255f, 150 / 255f, 100 / 255f, 150 / 255f, 200 / 255f, 250 / 255f], GltfAccessorReader.ReadFloats(doc, 17));
        Assert.NotNull(doc.Accessors[18].Sparse);
        Assert.Null(doc.Accessors[19].BufferView);
    }

    [Fact]
    public void ReadIntsRejectsFloatsAndExpectRejectsWrongTypes()
    {
        var doc = Sample();
        var ex = Assert.Throws<Cairn.Formats.AssetFormatException>(() => GltfAccessorReader.ReadInts(doc, 0, "m.gltf"));
        Assert.Contains("'m.gltf'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("accessor 0", ex.Message, StringComparison.Ordinal);
        Assert.Throws<Cairn.Formats.AssetFormatException>(() => GltfAccessorReader.ReadVector2(doc, 0));
        Assert.Throws<Cairn.Formats.AssetFormatException>(() => GltfAccessorReader.ReadFloats(doc, 999));
        GltfAccessorReader.Expect(doc, 0, "m.gltf", GltfAccessorType.Vec2, GltfAccessorType.Vec3);
    }

    // ---- hierarchy ----

    [Fact]
    public void LocalMatrixMatchesForMatrixAndTrs()
    {
        var trs = new GltfNode { Translation = new Vector3(1, 2, 3), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.7f), Scale = new Vector3(2, 3, 4) };
        Matrix4x4 expected = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateFromQuaternion(trs.Rotation.Value) * Matrix4x4.CreateTranslation(1, 2, 3);
        Assert.Equal(expected, trs.LocalMatrix());

        // Column-major file matrix: translation lives in elements 12..14.
        var mat = new GltfNode { Matrix = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 5, 6, 7, 1] };
        Assert.Equal(new Vector3(5, 6, 7), Vector3.Transform(Vector3.Zero, mat.LocalMatrix()));
        Assert.Equal(Matrix4x4.Identity, new GltfNode().LocalMatrix());
    }

    [Fact]
    public void WorldMatricesComposeAndSurviveCycles()
    {
        var doc = new GltfDocument();
        doc.Nodes.Add(new GltfNode { Translation = new Vector3(1, 0, 0) });
        doc.Nodes.Add(new GltfNode { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        doc.Nodes.Add(new GltfNode { Translation = new Vector3(1, 0, 0) });
        doc.Nodes[0].Children.Add(1);
        doc.Nodes[1].Children.Add(2);
        doc.Nodes[0].Children.Add(2); // node 2 now has two parents; node 0 comes first in node order, so it wins
        int[] parents = doc.ComputeParents();
        Assert.Equal([-1, 0, 0], parents);

        var chain = new GltfDocument();
        chain.Nodes.Add(new GltfNode { Translation = new Vector3(1, 0, 0) });
        chain.Nodes.Add(new GltfNode { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2) });
        chain.Nodes.Add(new GltfNode { Translation = new Vector3(1, 0, 0) });
        chain.Nodes[0].Children.Add(1);
        chain.Nodes[1].Children.Add(2);
        var world = chain.ComputeWorldMatrices();
        Vector3 p = Vector3.Transform(Vector3.Zero, world[2]);
        Assert.Equal(1f, p.X, 5);
        Assert.Equal(2f, p.Y, 5);

        // A cycle (0 -> 1 -> 0) and a self-reference terminate.
        var cyc = new GltfDocument();
        cyc.Nodes.Add(new GltfNode { Translation = Vector3.UnitX });
        cyc.Nodes.Add(new GltfNode { Translation = Vector3.UnitY });
        cyc.Nodes[0].Children.Add(1);
        cyc.Nodes[1].Children.Add(0);
        cyc.Nodes[1].Children.Add(1);
        cyc.Nodes[1].Children.Add(99);
        Assert.Equal(2, cyc.ComputeWorldMatrices().Length);

        // A deep chain does not overflow the stack.
        var deep = new GltfDocument();
        for (int i = 0; i < 100_000; i++)
        {
            deep.Nodes.Add(new GltfNode { Translation = Vector3.UnitX });
            if (i > 0) deep.Nodes[i - 1].Children.Add(i);
        }
        Assert.Equal(100_000f, deep.ComputeWorldMatrices()[^1].Translation.X);
    }

    // ---- builder and writer contracts ----

    [Fact]
    public void BuilderAlignsViewsAndRefusesUseAfterFinish()
    {
        var doc = new GltfDocument();
        var b = new GltfBufferBuilder(doc);
        b.AddBufferView([1, 2, 3]);
        int ub = b.AddIntegers([1, 2, 3, 4, 5, 6], GltfComponentType.UnsignedByte, GltfAccessorType.Vec3, target: 34962);
        b.AddScalars([1, 2]);
        Assert.Equal(-1, doc.BufferViews[0].Buffer);
        Assert.Equal(0, b.Finish("x.bin"));
        Assert.All(doc.BufferViews, v => Assert.Equal(0, v.ByteOffset % 4));
        Assert.All(doc.BufferViews, v => Assert.Equal(0, v.Buffer));
        Assert.Equal(4, doc.BufferViews[doc.Accessors[ub].BufferView!.Value].ByteStride);
        Assert.Equal([1, 2, 3, 4, 5, 6], GltfAccessorReader.ReadInts(doc, ub));
        Assert.Equal([1f], doc.Accessors[1].Min!);
        Assert.Equal([2f], doc.Accessors[1].Max!);
        Assert.Throws<InvalidOperationException>(() => b.AddScalars([1]));
        Assert.Throws<InvalidOperationException>(() => b.Finish());
        Assert.Throws<ArgumentException>(() => new GltfBufferBuilder(doc).AddIntegers([256], GltfComponentType.UnsignedByte, GltfAccessorType.Scalar));
        Assert.Throws<ArgumentException>(() => new GltfBufferBuilder(doc).AddFloats([1, 2], GltfAccessorType.Vec3));
    }

    [Fact]
    public void WriterRejectsInconsistentDocuments()
    {
        var unfinished = new GltfDocument();
        new GltfBufferBuilder(unfinished).AddScalars([1]);
        var ex = Assert.Throws<ArgumentException>(() => GltfWriter.ToGlb(unfinished));
        Assert.Contains("Finish", ex.Message, StringComparison.Ordinal);

        var collide = new GltfDocument();
        collide.Nodes.Add(new GltfNode { Mesh = null, Name = "n" });
        collide.Nodes[0].UnknownMembers["name"] = "dup";
        Assert.Throws<ArgumentException>(() => GltfWriter.ToJson(collide));

        var badIndex = new GltfDocument();
        badIndex.Nodes.Add(new GltfNode { Mesh = 3 });
        Assert.Throws<ArgumentException>(() => GltfWriter.ToJson(badIndex));

        var badMatrix = new GltfDocument();
        badMatrix.Nodes.Add(new GltfNode { Matrix = [1, 2, 3] });
        Assert.Throws<ArgumentException>(() => GltfWriter.ToJson(badMatrix));

        var nan = new GltfDocument();
        nan.Nodes.Add(new GltfNode { Weights = [float.NaN] });
        Assert.Throws<ArgumentException>(() => GltfWriter.ToJson(nan));
    }

    [Fact]
    public void SafeRelativeUris()
    {
        Assert.True(GltfReader.IsSafeRelativeUri("model.bin"));
        Assert.True(GltfReader.IsSafeRelativeUri("tex/a%20b.png"));
        Assert.True(GltfReader.IsSafeRelativeUri("./x.bin"));
        foreach (string bad in new[] { "", "../x.bin", "a/../../x.bin", "%2e%2e/x.bin", "%2E%2E%5Cx.bin", "C:\\x.bin", "C:/x.bin", "c:x.bin",
            "/etc/passwd", "\\\\server\\share\\x.bin", "http://x/y.bin", "file:///c:/x.bin", "data:application/octet-stream;base64,AAAA",
            "x.bin:stream", "...\\x.bin", "a%00b.bin", "%2Fetc%2Fpasswd" })
        {
            Assert.False(GltfReader.IsSafeRelativeUri(bad), bad);
        }
    }
}
