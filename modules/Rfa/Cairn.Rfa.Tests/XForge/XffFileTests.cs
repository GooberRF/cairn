using System.Buffers.Binary;
using System.Numerics;
using Cairn.Formats.XForge;

namespace Cairn.Rfa.Tests.XForge;

/// <summary>
/// The X-Forge scene chunk walker (<see cref="XffFile"/>): every typed record read back from a synthetic file, unknown
/// chunks kept raw, and malformed input (framing errors throw, bad records are kept raw with a diagnostic).
/// </summary>
public class XffFileTests
{
    private static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

    private static XffFile Read(SyntheticXff x) => XffFile.Read(x.ToArray(), "test.xff");

    private static T Single<T>(XffFile file) where T : XffChunk => Assert.Single(file.All<T>());

    [Fact]
    public void NodeRecords_ReadBack()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        var trs = new XffTransform(new Vector3(1, 2, 3), Turn, new Vector3(1, 1, 2));
        uint group = x.Group(root, "gr_thing", trs);
        uint obj = x.Object(group, "thing");
        uint marker = x.Marker(3107, root, "gr_door01", trs);
        uint helper = x.Helper(obj, "Dummy01", new Vector3(4, 5, 6));
        var file = Read(x);

        Assert.Empty(file.Diagnostics);
        Assert.Equal(0, file.RawChunkCount);
        Assert.Equal(5, file.Chunks.Count);
        var r = Assert.IsType<XffNode>(file.Find(root));
        Assert.True(r.IsGroup);
        Assert.Equal(XffFile.NoReference, r.Parent);
        Assert.Equal("scene_root", r.Name);
        Assert.Equal(XffTransform.Identity, r.Transform);
        var g = Assert.IsType<XffNode>(file.Find(group));
        Assert.Equal(root, g.Parent);
        Assert.Equal(trs, g.Transform);
        Assert.True(Assert.IsType<XffNode>(file.Find(obj)).IsObject);
        var m = Assert.IsType<XffNode>(file.Find(marker));
        Assert.True(m.IsMarker);
        Assert.Equal(3107, m.Type);
        var h = Assert.IsType<XffHelper>(file.Find(helper));
        Assert.Equal((obj, "Dummy01", new Vector3(4, 5, 6)), (h.Parent, h.Name, h.Position));
        Assert.True(XffFile.LooksLikeXff(x.ToArray()));
    }

    [Fact]
    public void MeshChunks_ReadBack()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint node = x.Object(root, "box");
        uint texture = x.Texture("lvl0_crate_01.pcx");
        uint material = x.Material(single0: 0.25f, value0: 3, value1: 4, byte0: 5, byte1: 6, single1: -0.5f);
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0)];
        Vector3[] normals = [-Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ, -Vector3.UnitZ];
        uint vb = x.VertexBuffer(positions, normals);
        ushort[] indices = [0, 1, 2, 2, 1, 3];
        uint il = x.IndexList(indices);
        XffColour[] colours = [.. Enumerable.Range(0, 6).Select(i => new XffColour((byte)i, (byte)(i * 2), (byte)(i * 3), 0))];
        Vector2[] uvs = [.. Enumerable.Range(0, 6).Select(i => new Vector2(i / 8f, 1 - i / 8f))];
        uint fa = x.FaceAttributes(2, colours, uvs);
        uint mesh = x.MeshInstance(node, "mesh_box", [SyntheticXff.Submesh(vb, il, 6, fa, material, texture)]);
        var file = Read(x);

        Assert.Empty(file.Diagnostics);
        Assert.Equal("lvl0_crate_01.pcx", Single<XffTexture>(file).FileName);
        var mat = Single<XffMaterial>(file);
        Assert.Equal(new XffColour(229, 229, 229, 0), mat.UnknownColour1);
        Assert.Equal((0.25f, 3u, 4u, (byte)5, (byte)6, -0.5f), (mat.UnknownSingle0, mat.UnknownFlags, mat.UnknownValue1, mat.UnknownByte0, mat.UnknownByte1, mat.UnknownSingle1));
        Assert.Equal(XffMaterial.Size, mat.Header.PayloadSize);
        var v = Single<XffVertexBuffer>(file);
        Assert.Equal((XffVertexFormat.Normal, 24, 4), (v.Format, v.Stride, v.Count));
        Assert.Equal(positions, v.Positions);
        Assert.Equal(normals, v.Normals);
        Assert.Empty(v.UnknownVertexBytes);
        Assert.Equal(indices, Single<XffIndexList>(file).Indices);
        var f = Single<XffFaceAttributes>(file);
        Assert.Equal((2, XffFaceContents.Colours | XffFaceContents.Uvs), (f.TriangleCount, f.Contents));
        Assert.Equal(colours, f.Colours);
        Assert.Equal(uvs, f.Uvs);
        var m = Assert.IsType<XffMeshInstance>(file.Find(mesh));
        Assert.Equal((node, "mesh_box"), (m.Node, m.Name));
        var s = Assert.Single(m.Submeshes);
        Assert.Equal(new XffSubmesh(vb, il, fa, material, texture, XffFile.NoReference, 0, 6, 1), s);
    }

    [Fact]
    public void FaceAttributes_ColoursOnlyAndUvsOnly()
    {
        var x = new SyntheticXff();
        x.Root();
        x.FaceAttributes(1, [new(1, 2, 3, 0), new(4, 5, 6, 0), new(7, 8, 9, 0)], null);
        x.FaceAttributes(1, null, [new(0, 0), new(1, 0), new(0, 1)]);
        var file = Read(x);
        var all = file.All<XffFaceAttributes>().ToList();
        Assert.Equal(XffFaceContents.Colours, all[0].Contents);
        Assert.Equal(3, all[0].Colours.Count);
        Assert.Empty(all[0].Uvs);
        Assert.Equal(XffFaceContents.Uvs, all[1].Contents);
        Assert.Empty(all[1].Colours);
        Assert.Equal(new Vector2(0, 1), all[1].Uvs[2]);
    }

    [Fact]
    public void OtherRecords_ReadBack()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        uint obj = x.Object(root, "enemy");
        XffKey[] keys = [new(new(0, 1, 0), Quaternion.Identity), new(new(0, 2, 0), Turn)];
        uint track = x.Track(root, "anim_enemy", -266, 267, 30f, keys);
        var trs = new XffTransform(new Vector3(5, 0, -5), Turn, Vector3.One);
        uint prefab = x.Prefab(root, "health01", trs, "item_healthpack.xff", 0);
        uint property = x.Property(obj, "portal:room02");
        uint emitter = x.Emitter(obj, "PS_gib", "gib.pr3");
        uint sprite = x.Sprite(obj, "SP_flare01", "flare.pcx", 0.2f, 0.3f);
        uint box = x.CollisionBox(obj, "col_enemy", new(-1, -2, -3), new(1, 2, 3), trs, shape: 1);
        uint fromNode = x.CollisionFromNode(root, "col_enemy2", obj);
        uint camera = x.Camera(root, "Camera01", new(1, 2, 3), Turn);
        uint light = x.Light(root, "Direct01", new(0, 10, 0), Turn, 3, new(255, 255, 255, 0), [10, 1, 0.25f, 0, 16, 254, 292]);
        var file = Read(x);

        Assert.Empty(file.Diagnostics);
        var t = Assert.IsType<XffAnimationTrack>(file.Find(track));
        Assert.Equal((root, "anim_enemy", "enemy", 1, -266, 267, 30f, XffAnimationFlags.LoopPositionRotation), (t.Parent, t.Name, t.TargetName, t.Animations.Count, t.Start, t.End, t.Rate, t.Flags));
        Assert.Equal(keys, t.Keys);
        var p = Assert.IsType<XffPrefab>(file.Find(prefab));
        Assert.Equal((root, "health01", trs, "item_healthpack.xff", (byte)0), (p.Parent, p.Name, p.Transform, p.FileName, p.UnknownFlag));
        var prop = Assert.IsType<XffProperty>(file.Find(property));
        Assert.Equal((obj, "portal:room02", "portal", "room02"), (prop.Node, prop.Text, prop.Key, prop.Value));
        var e = Assert.IsType<XffEmitter>(file.Find(emitter));
        Assert.Equal((obj, "PS_gib", 1f, "gib.pr3"), (e.Parent, e.Name, e.UnknownSingle, e.EffectFile));
        var s = Assert.IsType<XffSprite>(file.Find(sprite));
        Assert.Equal((obj, "SP_flare01", 0.2f, 0.3f, "flare.pcx", XffFile.NoReference), (s.Parent, s.Name, s.Width, s.Height, s.TextureName, s.UnknownReference));
        Assert.Equal(new byte[] { 2, 2, 2 }, s.UnknownBytes);
        var b = Assert.IsType<XffCollision>(file.Find(box));
        Assert.True(b.IsBox);
        Assert.Equal((obj, (byte)1, (byte)0, "col_enemy", (byte)1, new Vector3(-1, -2, -3), new Vector3(1, 2, 3), trs, XffFile.NoReference),
            (b.Parent, b.UnknownForm, b.UnknownByte, b.Name, b.UnknownShape, b.Min, b.Max, b.Transform, b.Source));
        var n = Assert.IsType<XffCollision>(file.Find(fromNode));
        Assert.False(n.IsBox);
        Assert.Equal((XffCollision.NodeForm, obj, XffTransform.Identity), (n.UnknownForm, n.Source, n.Transform));
        var c = Assert.IsType<XffCamera>(file.Find(camera));
        Assert.Equal((root, XffFile.NoReference, "Camera01", new Vector3(1, 2, 3), Turn, 1.134f, 0.1f, 40f),
            (c.Parent, c.Target, c.Name, c.Position, c.Rotation, c.FieldOfView, c.NearPlane, c.FarPlane));
        var l = Assert.IsType<XffLight>(file.Find(light));
        Assert.Equal((root, "Direct01", new Vector3(0, 10, 0), (byte)3, new XffColour(255, 255, 255, 0)), (l.Parent, l.Name, l.Position, l.UnknownKind, l.UnknownColour));
        Assert.Equal(new float[] { 10, 1, 0.25f, 0, 16, 254, 292 }, l.UnknownSingles);
    }

    [Fact]
    public void UnknownTypesAndVersions_AreKeptRawAndCounted()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(77, 1, [1, 2, 3]);
        x.Chunk(77, 1, []);
        x.Chunk(XffChunkTypes.MeshInstance, 2, [9, 9]); // a mesh instance of another version
        x.Texture("a.tga");
        var file = Read(x);
        Assert.Empty(file.Diagnostics);
        Assert.Equal(3, file.RawChunkCount);
        Assert.Equal(2, file.RawChunkCounts[(77, 1)]);
        Assert.Equal(1, file.RawChunkCounts[(XffChunkTypes.MeshInstance, 2)]);
        var raw = Assert.IsType<XffRawChunk>(file.Chunks[1]);
        Assert.Null(raw.Problem);
        Assert.Equal(new byte[] { 1, 2, 3 }, raw.Payload.ToArray());
        Assert.Equal((77, 1u, 3), (raw.Type, raw.Id, raw.Header.PayloadSize));
        Assert.IsType<XffTexture>(file.Chunks[4]);
    }

    // ── malformed input ─────────────────────────────────────────────────────

    [Fact]
    public void EmptyFile_Throws() => Assert.Throws<AssetFormatException>(() => XffFile.Read([], "empty.xff"));

    [Fact]
    public void TruncatedChunkHeader_Throws()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Bytes([1, 0, 1, 0, 5]);
        var ex = Assert.Throws<AssetFormatException>(() => Read(x));
        Assert.Contains("test.xff", ex.Message);
        Assert.Contains("5 bytes", ex.Message);
    }

    [Fact]
    public void PayloadPastTheEnd_Throws()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(XffChunkTypes.Texture, 1, [3, (byte)'a', (byte)'b', (byte)'c'], sizeOverride: 400);
        var ex = Assert.Throws<AssetFormatException>(() => Read(x));
        Assert.Contains("runs past the end", ex.Message);
        // a hostile size near 4 GB is refused the same way, without allocating
        var y = new SyntheticXff();
        y.Chunk(XffChunkTypes.GroupNode, 1, [], sizeOverride: 0xFFFFFFF0);
        Assert.Throws<AssetFormatException>(() => Read(y));
    }

    [Fact]
    public void TruncatedPayload_KeepsTheChunkRawWithADiagnostic()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(XffChunkTypes.ObjectNode, 1, [0, 0, 0, 0, 4, (byte)'a', (byte)'b', (byte)'c', (byte)'d', 0, 0]); // the TRS is missing
        var file = Read(x);
        var raw = Assert.IsType<XffRawChunk>(file.Chunks[1]);
        Assert.Contains("past the end of the payload", raw.Problem);
        var d = Assert.Single(file.Diagnostics);
        Assert.Equal((1u, XffChunkTypes.ObjectNode), (d.ChunkId, d.Type));
        Assert.Equal(1, file.RawChunkCounts[(XffChunkTypes.ObjectNode, 1)]);
    }

    [Fact]
    public void LeftoverBytes_KeepTheChunkRaw()
    {
        var x = new SyntheticXff();
        var good = new SyntheticXff();
        good.Texture("a.pcx");
        var payload = good.ToArray()[XffChunkHeader.Size..];
        x.Chunk(XffChunkTypes.Texture, 1, [.. payload, 0xEE]);
        var file = Read(x);
        Assert.Contains("left over", Assert.IsType<XffRawChunk>(Assert.Single(file.Chunks)).Problem);
        Assert.Single(file.Diagnostics);
    }

    [Fact]
    public void WrongSubmeshCount_KeepsTheMeshRaw()
    {
        var x = new SyntheticXff();
        x.Root();
        x.MeshInstance(0, "mesh_a", [SyntheticXff.Submesh(1, 2, 3, 4, 5, 6)]);
        var bytes = x.ToArray();
        // the mesh instance's submesh count (after the node id and the name "mesh_a") says 2
        int countAt = XffFile.Read(bytes, "t").Chunks[1].Header.PayloadOffset + 4 + 1 + 6;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(countAt), 2);
        var file = XffFile.Read(bytes, "t");
        Assert.Contains("declares 2 submeshes", Assert.IsType<XffRawChunk>(file.Chunks[1]).Problem);
        var scene = XffScene.Build(file);
        Assert.Empty(scene.Meshes);
    }

    [Fact]
    public void Stride_IsHonoured()
    {
        var x = new SyntheticXff();
        x.Root();
        uint wide = x.VertexBuffer([new(1, 2, 3), new(4, 5, 6)], [Vector3.UnitY, Vector3.UnitX], stride: 28, padByte: 0x5A);
        var file = Read(x);
        var v = Assert.IsType<XffVertexBuffer>(file.Find(wide));
        Assert.Equal((28, 2), (v.Stride, v.Count));
        Assert.Equal(new Vector3[] { new(1, 2, 3), new(4, 5, 6) }, v.Positions);
        Assert.Equal(new[] { Vector3.UnitY, Vector3.UnitX }, v.Normals);
        Assert.Equal(Enumerable.Repeat((byte)0x5A, 8), v.UnknownVertexBytes);
    }

    [Fact]
    public void WrongStride_KeepsTheBufferRaw()
    {
        // a stride that does not match the payload (24-byte vertices declared as 28)
        var x = new SyntheticXff();
        x.Root();
        x.VertexBuffer([new(1, 2, 3)], [Vector3.UnitY]);
        var bytes = x.ToArray();
        int strideAt = XffFile.Read(bytes, "t").Chunks[1].Header.PayloadOffset + 4;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(strideAt), 28);
        Assert.Contains("declares 1 vertices of 28 bytes", Assert.IsType<XffRawChunk>(XffFile.Read(bytes, "t").Chunks[1]).Problem);
        // a stride too small for a position and a normal
        var y = new SyntheticXff();
        y.Root();
        y.VertexBuffer([new(1, 2, 3)], [Vector3.UnitY], stride: 24);
        bytes = y.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(strideAt), 12);
        Assert.Contains("smaller than its vertex format needs (24 bytes)", Assert.IsType<XffRawChunk>(XffFile.Read(bytes, "t").Chunks[1]).Problem);
    }

    [Fact]
    public void HostileCounts_AreRefusedWithoutAllocating()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(XffChunkTypes.VertexBuffer, 1, [1, 0, 0, 0, 24, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        x.Chunk(XffChunkTypes.AnimationTrack, 2, [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 7, 0, 0xFF, 0xFF, 0xFF, 0xFF]);
        var file = Read(x);
        Assert.Equal(2, file.RawChunkCount);
        Assert.Equal(2, file.Diagnostics.Count);
    }

    /// <summary>A track chunk (1005 v2) with one animation whose flags name no key channel (loop only: keys of 0 bytes) and <paramref name="keyCount"/> keys.</summary>
    private static byte[] KeylessTrackFile(uint keyCount)
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        var payload = new List<byte>();
        void U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); payload.AddRange(b); }
        U32(root);
        payload.Add(8);
        payload.AddRange("anim_box"u8.ToArray());
        payload.AddRange([1, 0]);           // one animation
        U32(0);                             // start
        U32(100);                           // end
        var rate = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(rate, 30f);
        payload.AddRange(rate);
        payload.AddRange([(byte)XffAnimationFlags.Loop, 0]); // no position, rotation or scale
        U32(keyCount);
        x.Chunk(XffChunkTypes.AnimationTrack, 2, payload.ToArray());
        return x.ToArray();
    }

    /// <summary>
    /// Keys with no channel take no bytes, so no payload can back their count: a 112-byte file declaring billions of them
    /// is a layout problem (the chunk kept raw with a diagnostic) found before anything is allocated for the count, and the
    /// content claim still classifies the file instead of throwing.
    /// </summary>
    [Theory]
    [InlineData(1u)]
    [InlineData(50_000_000u)]
    [InlineData((uint)int.MaxValue)]
    [InlineData(uint.MaxValue)]
    public void KeylessTrackWithKeys_IsKeptRawWithoutAllocating(uint keyCount)
    {
        byte[] bytes = KeylessTrackFile(keyCount);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var file = XffFile.Read(bytes, "hostile.xff");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1 << 20, $"reading a {bytes.Length}-byte file allocated {allocated:N0} bytes");
        var raw = Assert.IsType<XffRawChunk>(file.Chunks[1]);
        Assert.Contains("name no key channel", raw.Problem);
        Assert.Contains("name no key channel", Assert.Single(file.Diagnostics).Message);
        Assert.Equal(XffKind.Model, XffClassifier.TryClassify(bytes, "hostile.xff"));
    }

    /// <summary>A keyless track without keys is still a track (nothing to back, nothing wrong).</summary>
    [Fact]
    public void KeylessTrackWithoutKeys_IsRead()
    {
        var file = XffFile.Read(KeylessTrackFile(0), "t.xff");
        Assert.Empty(file.Diagnostics);
        Assert.Empty(Assert.IsType<XffAnimationTrack>(file.Chunks[1]).Keys);
    }

    [Fact]
    public void OtherBadRecords_AreKeptRaw()
    {
        var x = new SyntheticXff();
        x.Root();
        x.Chunk(XffChunkTypes.Property, 1, [0, 0, 0, 0, 2, 0, 0, 0, (byte)'h', (byte)'i']); // no terminating zero
        x.Chunk(XffChunkTypes.FaceAttributes, 1, [0, 0, 2, 0, 0, 0]); // layout bit 2 is not known
        x.Chunk(XffChunkTypes.AnimationTrack, 2, [0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0, 0]); // flag bit 4
        x.Chunk(XffChunkTypes.Collision, 1, [0, 0, 0, 0, 7, 0, 0, 1]); // form 7
        x.Chunk(XffChunkTypes.IndexList, 2, [3, 0, 1, 0]); // three indices declared, one stored
        x.Chunk(XffChunkTypes.VertexBuffer, 1, [8, 0, 0, 0, 12, 0, 0, 0, 0, 0]); // vertex format bit 3
        x.Chunk(XffChunkTypes.AnimationTrack, 2, [0, 0, 0, 0, 0, 9, 0]); // nine animations in no bytes
        var file = Read(x);
        Assert.Equal(7, file.RawChunkCount);
        Assert.Collection(file.Diagnostics,
            d => Assert.Contains("does not end with a zero", d.Message),
            d => Assert.Contains("layout value 2", d.Message),
            d => Assert.Contains("animation flags 16", d.Message),
            d => Assert.Contains("form 7", d.Message),
            d => Assert.Contains("declares 3 indices", d.Message),
            d => Assert.Contains("vertex format 8", d.Message),
            d => Assert.Contains("9 animations", d.Message));
    }

    [Fact]
    public void Tracks_HoldAnyChannelsAndSeveralAnimations()
    {
        var x = new SyntheticXff();
        uint root = x.Root();
        var scaled = new XffKey(new(1, 2, 3), Quaternion.Identity, new(2, 2, 2));
        uint track = x.Track(root, "anim_a",
        [
            new XffAnimation(0, 66, 30, XffAnimationFlags.Position | XffAnimationFlags.Scale, [scaled, scaled]),
            new XffAnimation(-33, 33, 30, XffAnimationFlags.Loop | XffAnimationFlags.Rotation, [new(Vector3.Zero, Turn)]),
        ]);
        uint empty = x.Track(root, "anim_b", []);
        var file = Read(x);
        Assert.Empty(file.Diagnostics);
        var t = Assert.IsType<XffAnimationTrack>(file.Find(track));
        Assert.Equal(2, t.Animations.Count);
        Assert.Equal(new[] { scaled, scaled }, t.Keys);
        Assert.Equal((XffAnimationFlags.Position | XffAnimationFlags.Scale, 33), (t.Flags, t.First.Interval));
        Assert.Equal(new XffKey(Vector3.Zero, Turn, Vector3.One), t.Animations[1].Keys[0]);
        Assert.Equal(12 + 12, XffKey.SizeFor(t.Flags));
        var e = Assert.IsType<XffAnimationTrack>(file.Find(empty));
        Assert.Empty(e.Animations);
        Assert.Empty(e.Keys);
    }

    [Fact]
    public void VertexFormats_ReadTheirChannels()
    {
        var x = new SyntheticXff();
        x.Root();
        uint full = x.VertexBuffer(XffVertexFormat.Normal | XffVertexFormat.Colour | XffVertexFormat.Uv, [new(1, 2, 3)], [Vector3.UnitZ],
            [new XffColour(1, 2, 3, 4)], [new Vector2(0.5f, 0.25f)]);
        uint bare = x.VertexBuffer(XffVertexFormat.Position, [new(4, 5, 6)], null, null, null);
        var file = Read(x);
        Assert.Empty(file.Diagnostics);
        var f = Assert.IsType<XffVertexBuffer>(file.Find(full));
        Assert.Equal(12 + 12 + 4 + 8, f.Stride);
        Assert.Equal((Vector3.UnitZ, new XffColour(1, 2, 3, 4), new Vector2(0.5f, 0.25f)), (f.Normals[0], f.Colours[0], f.Uvs[0]));
        var b = Assert.IsType<XffVertexBuffer>(file.Find(bare));
        Assert.Equal((12, 1), (b.Stride, b.Count));
        Assert.Empty(b.Normals);
        Assert.Empty(b.Colours);
    }

    [Fact]
    public void DuplicateIds_AreReportedAndTheFirstWins()
    {
        var x = new SyntheticXff();
        x.Root();
        uint id = x.Texture("first.tga");
        x.NextId = id;
        x.Texture("second.tga");
        var file = Read(x);
        Assert.Contains("used again", Assert.Single(file.Diagnostics).Message);
        Assert.Equal("first.tga", Assert.IsType<XffTexture>(file.Find(id)).FileName);
        Assert.Equal(3, file.Chunks.Count);
    }

    [Fact]
    public void LooksLikeXff_RejectsOtherFiles()
    {
        Assert.False(XffFile.LooksLikeXff([]));
        Assert.False(XffFile.LooksLikeXff("RFA\0 not a scene at all"u8));
        var x = new SyntheticXff();
        x.Texture("a.tga");
        Assert.False(XffFile.LooksLikeXff(x.ToArray()));
        var y = new SyntheticXff();
        y.Marker(4015, XffFile.NoReference, "gr_weapon_rocket");
        Assert.True(XffFile.LooksLikeXff(y.ToArray()));
        Assert.True(XffFile.HasExtension("Level_01A.XFF"));
        Assert.False(XffFile.HasExtension("level_01a.xff.bak"));
    }
}
