using System.Buffers.Binary;
using System.Text;
using Cairn.Formats;
using Cairn.Formats.Gltf;

namespace Cairn.Rfa.Tests;

/// <summary>Damaged and hostile glTF input must raise AssetFormatException, and nothing else.</summary>
public class GltfMalformedTests
{
    private static byte[] Gltf(string body) => Encoding.UTF8.GetBytes("{\"asset\":{\"version\":\"2.0\"}" + (body.Length > 0 ? "," + body : "") + "}");

    private static string Buffer(int byteLength, int actual = -1) =>
        $"\"buffers\":[{{\"byteLength\":{byteLength},\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(new byte[actual < 0 ? byteLength : actual])}\"}}]";

    private static GltfDocument Parse(string body) => GltfReader.Read(Gltf(body), "bad.gltf", null);

    private static AssetFormatException Rejected(Action action)
    {
        var ex = Assert.Throws<AssetFormatException>(action);
        Assert.Contains("bad.", ex.Message, StringComparison.Ordinal);
        return ex;
    }

    // ---- accessor validation ----

    [Fact]
    public void AccessorPastTheEndOfItsView()
    {
        var doc = Parse(Buffer(24) + ""","bufferViews":[{"buffer":0,"byteLength":24}],"accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"}]""");
        var ex = Rejected(() => GltfAccessorReader.ReadVector3(doc, 0, "bad.gltf"));
        Assert.Contains("accessor 0 reads past the end of buffer view 0", ex.Message, StringComparison.Ordinal);

        var offset = Parse(Buffer(24) + ""","bufferViews":[{"buffer":0,"byteLength":24}],"accessors":[{"bufferView":0,"byteOffset":16,"componentType":5126,"count":1,"type":"VEC3"}]""");
        Rejected(() => GltfAccessorReader.ReadFloats(offset, 0, "bad.gltf"));
    }

    [Fact]
    public void ViewPastTheEndOfItsBuffer()
    {
        var doc = Parse(Buffer(24) + ""","bufferViews":[{"buffer":0,"byteOffset":8,"byteLength":24}],"accessors":[{"bufferView":0,"componentType":5126,"count":1,"type":"SCALAR"}]""");
        Rejected(() => GltfAccessorReader.ReadFloats(doc, 0, "bad.gltf"));
        var missing = Parse(Buffer(24) + ""","bufferViews":[{"buffer":3,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5126,"count":1,"type":"SCALAR"},{"bufferView":7,"componentType":5126,"count":1,"type":"SCALAR"}]""");
        Rejected(() => GltfAccessorReader.ReadFloats(missing, 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadFloats(missing, 1, "bad.gltf"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(256)]
    [InlineData(-4)]
    public void BadStrideIsRejected(int stride)
    {
        var doc = Parse(Buffer(48) + $$""","bufferViews":[{"buffer":0,"byteLength":48,"byteStride":{{stride}}}],"accessors":[{"bufferView":0,"componentType":5126,"count":2,"type":"VEC3"}]""");
        var ex = Rejected(() => GltfAccessorReader.ReadVector3(doc, 0, "bad.gltf"));
        Assert.Contains("byteStride", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BadAccessorTypesAndCountsAreRejected()
    {
        Rejected(() => GltfAccessorReader.ReadFloats(Parse(Buffer(4) + ""","bufferViews":[{"buffer":0,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5126,"count":1,"type":"VEC5"}]"""), 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadFloats(Parse(Buffer(4) + ""","bufferViews":[{"buffer":0,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5124,"count":1,"type":"SCALAR"}]"""), 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadFloats(Parse("""  "accessors":[{"componentType":5126,"count":2000000000,"type":"MAT4"}]"""), 0, "bad.gltf"));
        // UNSIGNED_INT beyond int.MaxValue cannot be an index.
        var big = Parse("\"buffers\":[{\"byteLength\":4,\"uri\":\"data:application/octet-stream;base64,/////w==\"}]" +
            ""","bufferViews":[{"buffer":0,"byteLength":4}],"accessors":[{"bufferView":0,"componentType":5125,"count":1,"type":"SCALAR"}]""");
        Rejected(() => GltfAccessorReader.ReadInts(big, 0, "bad.gltf"));
        Assert.Equal(4294967295f, GltfAccessorReader.ReadFloats(big, 0, "bad.gltf")[0]);
    }

    [Fact]
    public void AccessorWithoutViewIsZeros()
    {
        var doc = Parse("""  "accessors":[{"componentType":5123,"count":3,"type":"VEC2"}]""");
        Assert.Equal(new float[6], GltfAccessorReader.ReadFloats(doc, 0, "bad.gltf"));
    }

    private static string SparseModel(string indicesBase64, int indexCount, int count = 4) =>
        "\"buffers\":[{\"byteLength\":4,\"uri\":\"data:application/octet-stream;base64," + indicesBase64 + "\"},"
        + "{\"byteLength\":24,\"uri\":\"data:application/octet-stream;base64," + Convert.ToBase64String(new byte[24]) + "\"}],"
        + "\"bufferViews\":[{\"buffer\":0,\"byteLength\":4},{\"buffer\":1,\"byteLength\":24}],"
        + "\"accessors\":[{\"componentType\":5126,\"count\":" + count + ",\"type\":\"VEC3\",\"sparse\":{\"count\":" + indexCount
        + ",\"indices\":{\"bufferView\":0,\"componentType\":5121},\"values\":{\"bufferView\":1}}}]";

    [Fact]
    public void SparseIndicesAreValidated()
    {
        // Valid: indices 1, 3.
        var ok = Parse(SparseModel(Convert.ToBase64String([1, 3, 0, 0]), 2));
        Assert.Equal(4, GltfAccessorReader.ReadVector3(ok, 0, "bad.gltf").Length);
        // Out of range.
        var ex = Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([1, 4, 0, 0]), 2)), 0, "bad.gltf"));
        Assert.Contains("element 4", ex.Message, StringComparison.Ordinal);
        // Not increasing / repeated.
        Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([2, 1, 0, 0]), 2)), 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([1, 1, 0, 0]), 2)), 0, "bad.gltf"));
        // More substitutions than elements, more indices than the view holds, more values than the view holds.
        Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([0, 1, 2, 3]), 4, count: 3)), 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([0, 1, 2, 3]), 5, count: 9)), 0, "bad.gltf"));
        Rejected(() => GltfAccessorReader.ReadVector3(Parse(SparseModel(Convert.ToBase64String([0, 1, 2, 3]), 3, count: 9)), 0, "bad.gltf"));
    }

    // ---- JSON shape ----

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},}")]
    [InlineData("{\"nodes\":[]}")]
    [InlineData("{\"asset\":{}}")]
    [InlineData("{\"asset\":{\"version\":\"1.0\"}}")]
    [InlineData("{\"asset\":{\"version\":2}}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"accessors\":[{\"componentType\":5126,\"count\":\"3\",\"type\":\"VEC3\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"accessors\":[{\"componentType\":5126,\"count\":-1,\"type\":\"VEC3\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"accessors\":[{\"componentType\":5126,\"type\":\"VEC3\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":{}}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"translation\":[1,2]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"matrix\":[1,2,3]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"children\":[1.5]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":7}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"extensions\":5}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"weights\":[1e999]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"doubleSided\":1}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"materials\":[{\"normalTexture\":{}}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"meshes\":[{\"primitives\":[{\"attributes\":{\"POSITION\":-1}}]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"bufferViews\":[{\"buffer\":0}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"animations\":[{\"channels\":[{\"sampler\":0,\"target\":{}}],\"samplers\":[]}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"extensionsUsed\":[1]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"\\uD800\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"extras\":{\"k\":\"\\uDC00x\"}}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":4}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":4,\"uri\":\"data:application/octet-stream,abcd\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":4,\"uri\":\"data:application/octet-stream;base64,!!!!\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"buffers\":[{\"byteLength\":8,\"uri\":\"data:application/octet-stream;base64,AAAA\"}]}")]
    [InlineData("{\"asset\":{\"version\":\"2.0\"},\"images\":[{\"bufferView\":0}]}")]
    public void MalformedJsonIsRejected(string json)
    {
        Rejected(() => GltfReader.Read(Encoding.UTF8.GetBytes(json), "bad.gltf", null));
    }

    [Fact]
    public void ToleratedQuirks()
    {
        // BOM, duplicate keys in extras (last wins), whole numbers written as 3.0, unknown chunk-free JSON.
        byte[] bom = [0xEF, 0xBB, 0xBF, .. Gltf("""  "nodes":[{"mesh":3.0,"extras":{"a":1,"a":2}}]""")];
        var doc = GltfReader.Read(bom, "ok.gltf", null);
        Assert.Equal(3, doc.Nodes[0].Mesh);
        Assert.Equal(2, doc.Nodes[0].Extras!["a"]!.GetValue<int>());
    }

    // ---- external URIs ----

    [Theory]
    [InlineData("../x.bin")]
    [InlineData("C:\\\\x.bin")]
    [InlineData("%2e%2e/x.bin")]
    [InlineData("http://x/y.bin")]
    [InlineData("/x.bin")]
    [InlineData("sub/../../x.bin")]
    [InlineData("%2E%2E%5Cx.bin")]
    public void UnsafeBufferUrisAreRefused(string uri)
    {
        bool called = false;
        var data = Gltf($$"""  "buffers":[{"byteLength":4,"uri":"{{uri}}"}]""");
        var ex = Assert.Throws<AssetFormatException>(() => GltfReader.Read(data, "bad.gltf", _ => { called = true; return new byte[4]; }));
        Assert.Contains("outside its folder", ex.Message, StringComparison.Ordinal);
        Assert.False(called);
    }

    [Fact]
    public void ReadFileRefusesTraversalAndReportsMissingFiles()
    {
        using var temp = new TempFolder();
        temp.Write("secret.bin", new byte[4]);
        string sub = temp.SubDirectory("model");
        string traversal = temp.Write(Path.Combine("model", "bad.gltf"), Gltf("""  "buffers":[{"byteLength":4,"uri":"../secret.bin"}]"""));
        var ex = Assert.Throws<AssetFormatException>(() => GltfReader.ReadFile(traversal));
        Assert.Contains("outside its folder", ex.Message, StringComparison.Ordinal);

        string missing = temp.Write(Path.Combine("model", "bad2.gltf"), Gltf("""  "buffers":[{"byteLength":4,"uri":"gone%20away.bin"}]"""));
        ex = Assert.Throws<AssetFormatException>(() => GltfReader.ReadFile(missing));
        Assert.Contains("gone away.bin", ex.Message, StringComparison.Ordinal);
        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);

        // A missing image is not an error: its Data stays null.
        temp.Write(Path.Combine("model", "ok.bin"), new byte[4]);
        string image = temp.Write(Path.Combine("model", "ok.gltf"), Gltf("""  "buffers":[{"byteLength":4,"uri":"ok.bin"}],"images":[{"uri":"nope.png"}]"""));
        var doc = GltfReader.ReadFile(image, new GltfReadOptions { LoadImages = true });
        Assert.Null(doc.Images[0].Data);
        Assert.Equal(4, doc.Buffers[0].Data!.Length);

        // Size limit.
        Assert.Throws<AssetFormatException>(() => GltfReader.ReadFile(image, new GltfReadOptions { MaxBytes = 10 }));
        _ = sub;
    }

    // ---- GLB container ----

    private static byte[] SmallGlb()
    {
        var doc = new GltfDocument();
        var b = new GltfBufferBuilder(doc);
        int pos = b.AddVector3([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], minMax: true);
        int idx = b.AddIndices([0, 1, 2]);
        int nrm = b.AddIntegers([0, 0, 127, 0, 0, 127, 0, 0, 127], GltfComponentType.Byte, GltfAccessorType.Vec3, normalized: true, target: 34962);
        b.AddMatrices([System.Numerics.Matrix4x4.Identity]);
        b.Finish();
        var prim = new GltfPrimitive { Indices = idx };
        prim.Attributes["POSITION"] = pos;
        prim.Attributes["NORMAL"] = nrm;
        var mesh = new GltfMesh();
        mesh.Primitives.Add(prim);
        doc.Meshes.Add(mesh);
        doc.Nodes.Add(new GltfNode { Mesh = 0 });
        doc.Accessors.Add(new GltfAccessor
        {
            BufferView = doc.Accessors[pos].BufferView,
            ComponentType = GltfComponentType.Float,
            Count = 3,
            Type = GltfAccessorType.Vec3,
            Sparse = new GltfSparse
            {
                Count = 1,
                Indices = new GltfSparseIndices { BufferView = doc.Accessors[idx].BufferView!.Value, ByteOffset = 2, ComponentType = GltfComponentType.UnsignedShort },
                Values = new GltfSparseValues { BufferView = doc.Accessors[pos].BufferView!.Value },
            },
        });
        return GltfWriter.ToGlb(doc, new GltfWriteOptions { Indented = false });
    }

    [Fact]
    public void GlbContainerDamageIsRejected()
    {
        byte[] glb = SmallGlb();
        Assert.NotNull(GltfReader.Read(glb, "bad.glb", null));

        Rejected(() => GltfReader.Read(glb[..^5], "bad.glb", null));                 // truncated
        Rejected(() => GltfReader.Read(glb[..11], "bad.glb", null));                 // shorter than the header

        var magic = (byte[])glb.Clone();
        magic[0] = (byte)'x';
        Rejected(() => GltfReader.Read(magic, "bad.glb", null));                     // bad magic: not JSON either

        var version = (byte[])glb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(version.AsSpan(4), 1);
        Rejected(() => GltfReader.Read(version, "bad.glb", null));

        var overflow = (byte[])glb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(overflow.AsSpan(12), 0xFFFFFFF0);
        Rejected(() => GltfReader.Read(overflow, "bad.glb", null));                  // chunk length overflow

        var binOverflow = (byte[])glb.Clone();
        int binAt = 20 + (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(binOverflow.AsSpan(binAt), 0x7FFFFFFF);
        Rejected(() => GltfReader.Read(binOverflow, "bad.glb", null));

        var shortBin = (byte[])glb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(shortBin.AsSpan(binAt), 4);
        Array.Resize(ref shortBin, binAt + 12);
        BinaryPrimitives.WriteUInt32LittleEndian(shortBin.AsSpan(8), (uint)shortBin.Length);
        Rejected(() => GltfReader.Read(shortBin, "bad.glb", null));                  // BIN chunk shorter than buffer 0

        var notJson = (byte[])glb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(notJson.AsSpan(16), 0x004E4942);
        Rejected(() => GltfReader.Read(notJson, "bad.glb", null));                   // first chunk is BIN

        var length = (byte[])glb.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(length.AsSpan(8), (uint)glb.Length + 4);
        Rejected(() => GltfReader.Read(length, "bad.glb", null));

        byte[] trailing = [.. glb, 1, 2, 3, 4];
        BinaryPrimitives.WriteUInt32LittleEndian(trailing.AsSpan(8), (uint)trailing.Length);
        Rejected(() => GltfReader.Read(trailing, "bad.glb", null));                  // ends inside a chunk header
    }

    [Fact]
    public void FuzzedGlbRaisesOnlyAssetFormatException()
    {
        byte[] glb = SmallGlb();
        var rng = new Random(12345);
        int parsed = 0;
        for (int iteration = 0; iteration < 2000; iteration++)
        {
            var bytes = (byte[])glb.Clone();
            int flips = 1 + rng.Next(3);
            for (int f = 0; f < flips; f++)
            {
                // Bias half the flips towards the headers and the BIN chunk, which are small.
                int at = rng.Next(2) == 0 ? rng.Next(bytes.Length) : (rng.Next(2) == 0 ? rng.Next(24) : bytes.Length - 1 - rng.Next(Math.Min(bytes.Length, 160)));
                bytes[at] = rng.Next(3) switch { 0 => (byte)rng.Next(256), 1 => (byte)(bytes[at] ^ (1 << rng.Next(8))), _ => (byte)"0123456789-.e\"[]{},"[rng.Next(19)] };
            }
            GltfDocument doc;
            try
            {
                doc = GltfReader.Read(bytes, "fuzz.glb", null);
            }
            catch (AssetFormatException)
            {
                continue;
            }
            parsed++;
            for (int a = 0; a < doc.Accessors.Count; a++)
            {
                try { GltfAccessorReader.ReadVector3(doc, a, "fuzz.glb"); }
                catch (AssetFormatException) { }
                try { GltfAccessorReader.ReadFloats(doc, a, "fuzz.glb"); }
                catch (AssetFormatException) { }
                try { GltfAccessorReader.ReadInts(doc, a, "fuzz.glb"); }
                catch (AssetFormatException) { }
            }
            doc.ComputeWorldMatrices();
        }
        Assert.True(parsed > 100, $"only {parsed} fuzzed files parsed; the fuzzer is not reaching the accessor reader");
    }
}
