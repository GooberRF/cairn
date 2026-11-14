using System.Numerics;
using System.Text;

namespace Cairn.Formats.XForge;

/// <summary>
/// Writes small X-Forge scenes for tests and self-tests (Cairn never writes <c>.xff</c> files otherwise): every chunk
/// <see cref="XffFile"/> types, plus raw chunks for malformed cases. Ids are given out in order from 0, so the first
/// chunk added is id 0. Each method returns the id of the chunk it added.
/// </summary>
public sealed class SyntheticXff
{
    private readonly BinaryBuilder _out = new();

    /// <summary>The id the next chunk gets.</summary>
    public uint NextId { get; set; }

    /// <summary>The file written so far.</summary>
    public byte[] ToArray() => _out.ToArray();

    /// <summary>Adds a chunk with <paramref name="payload"/> as it is (a raw or deliberately broken chunk); <paramref name="sizeOverride"/> writes another payload size.</summary>
    public uint Chunk(ushort type, ushort version, ReadOnlySpan<byte> payload, uint? sizeOverride = null)
    {
        uint id = NextId++;
        _out.WriteUInt16(type);
        _out.WriteUInt16(version);
        _out.WriteUInt32(id);
        _out.WriteUInt32(sizeOverride ?? (uint)payload.Length);
        _out.WriteBytes(payload);
        return id;
    }

    /// <summary>Adds bytes that are not a chunk (to make a file end badly).</summary>
    public void Bytes(ReadOnlySpan<byte> bytes) => _out.WriteBytes(bytes);

    private uint Add(ushort type, ushort version, Action<BinaryBuilder> write)
    {
        var payload = new BinaryBuilder(64);
        write(payload);
        return Chunk(type, version, payload.ToArray());
    }

    private static void Name(BinaryBuilder b, string name)
    {
        var bytes = Encoding.Latin1.GetBytes(name);
        if (bytes.Length > byte.MaxValue) throw new ArgumentException($"'{name}' is longer than 255 bytes.", nameof(name));
        b.WriteByte((byte)bytes.Length);
        b.WriteBytes(bytes);
    }

    private static void Trs(BinaryBuilder b, XffTransform t)
    {
        b.WriteVector3(t.Position);
        b.WriteQuaternion(t.Rotation);
        b.WriteVector3(t.Scale);
    }

    private static void Colour(BinaryBuilder b, XffColour c)
    {
        b.WriteByte(c.R);
        b.WriteByte(c.G);
        b.WriteByte(c.B);
        b.WriteByte(c.A);
    }

    /// <summary>A node record: group (2), object (3) or a marker (2000 and up).</summary>
    public uint Node(ushort type, uint parent, string name, XffTransform? transform = null) =>
        Add(type, 1, b => { b.WriteUInt32(parent); Name(b, name); Trs(b, transform ?? XffTransform.Identity); });

    /// <summary>The usual first chunk: a group node <c>scene_root</c> with no parent.</summary>
    public uint Root(string name = "scene_root") => Node(XffChunkTypes.GroupNode, XffFile.NoReference, name);

    /// <summary>A group node.</summary>
    public uint Group(uint parent, string name, XffTransform? transform = null) => Node(XffChunkTypes.GroupNode, parent, name, transform);

    /// <summary>An object node.</summary>
    public uint Object(uint parent, string name, XffTransform? transform = null) => Node(XffChunkTypes.ObjectNode, parent, name, transform);

    /// <summary>A game-object marker of <paramref name="type"/> (2000 and up).</summary>
    public uint Marker(ushort type, uint parent, string name, XffTransform? transform = null) => Node(type, parent, name, transform);

    /// <summary>A helper node (type 7): parent, name and position.</summary>
    public uint Helper(uint parent, string name, Vector3 position) =>
        Add(XffChunkTypes.Helper, 1, b => { b.WriteUInt32(parent); Name(b, name); b.WriteVector3(position); });

    /// <summary>A camera (type 5).</summary>
    public uint Camera(uint parent, string name, Vector3 position, Quaternion rotation, float fieldOfView = 1.134f, float nearPlane = 0.1f, float farPlane = 40f,
        uint target = XffFile.NoReference) =>
        Add(XffChunkTypes.Camera, 1, b =>
        {
            b.WriteUInt32(parent);
            b.WriteUInt32(target);
            Name(b, name);
            b.WriteVector3(position);
            b.WriteQuaternion(rotation);
            b.WriteSingle(fieldOfView);
            b.WriteSingle(nearPlane);
            b.WriteSingle(farPlane);
        });

    /// <summary>A light (type 6) with seven floats.</summary>
    public uint Light(uint parent, string name, Vector3 position, Quaternion rotation, byte kind, XffColour colour, IReadOnlyList<float> singles) =>
        Add(XffChunkTypes.Light, 2, b =>
        {
            if (singles.Count != 7) throw new ArgumentException("A light has seven floats.", nameof(singles));
            b.WriteUInt32(parent);
            Name(b, name);
            b.WriteVector3(position);
            b.WriteQuaternion(rotation);
            b.WriteByte(kind);
            Colour(b, colour);
            foreach (float f in singles) b.WriteSingle(f);
        });

    /// <summary>A particle emitter (type 8).</summary>
    public uint Emitter(uint parent, string name, string effectFile, float single = 1f) =>
        Add(XffChunkTypes.Emitter, 1, b => { b.WriteUInt32(parent); Name(b, name); b.WriteSingle(single); Name(b, effectFile); });

    /// <summary>A sprite (type 9).</summary>
    public uint Sprite(uint parent, string name, string textureName, float width = 0.1f, float height = 0.1f, uint reference = XffFile.NoReference) =>
        Add(XffChunkTypes.Sprite, 1, b =>
        {
            b.WriteUInt32(parent);
            Name(b, name);
            b.WriteSingle(width);
            b.WriteSingle(height);
            Name(b, textureName);
            b.WriteBytes([2, 2, 2]);
            b.WriteUInt32(reference);
        });

    /// <summary>A collision box (type 12, form 1).</summary>
    public uint CollisionBox(uint parent, string name, Vector3 min, Vector3 max, XffTransform? transform = null, byte shape = 4) =>
        Add(XffChunkTypes.Collision, 1, b =>
        {
            b.WriteUInt32(parent);
            b.WriteByte(XffCollision.BoxForm);
            b.WriteByte(0);
            Name(b, name);
            b.WriteByte(shape);
            b.WriteVector3(min);
            b.WriteVector3(max);
            Trs(b, transform ?? XffTransform.Identity);
        });

    /// <summary>A collision that uses another node's mesh (type 12, form 101).</summary>
    public uint CollisionFromNode(uint parent, string name, uint source, byte shape = 4) =>
        Add(XffChunkTypes.Collision, 1, b =>
        {
            b.WriteUInt32(parent);
            b.WriteByte(XffCollision.NodeForm);
            b.WriteByte(0);
            Name(b, name);
            b.WriteByte(shape);
            b.WriteUInt32(source);
        });

    /// <summary>A vertex buffer of positions and normals (format 1, as the game's files); a stride above 24 pads each vertex with <paramref name="padByte"/>.</summary>
    public uint VertexBuffer(IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, int stride = XffVertexBuffer.PositionNormalBytes, byte padByte = 0xAB) =>
        VertexBuffer(XffVertexFormat.Normal, positions, normals, null, null, stride, padByte);

    /// <summary>A vertex buffer of any <paramref name="format"/>: the lists it names must have one entry per position.</summary>
    public uint VertexBuffer(XffVertexFormat format, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3>? normals, IReadOnlyList<XffColour>? colours,
        IReadOnlyList<Vector2>? uvs, int? stride = null, byte padByte = 0xAB) =>
        Add(XffChunkTypes.VertexBuffer, 1, b =>
        {
            int needed = XffVertexBuffer.VertexBytes(format), size = stride ?? needed;
            b.WriteUInt32((uint)format);
            b.WriteUInt16((ushort)size);
            b.WriteUInt32((uint)positions.Count);
            for (int i = 0; i < positions.Count; i++)
            {
                b.WriteVector3(positions[i]);
                if (format.HasFlag(XffVertexFormat.Normal)) b.WriteVector3(normals![i]);
                if (format.HasFlag(XffVertexFormat.Colour)) Colour(b, colours![i]);
                if (format.HasFlag(XffVertexFormat.Uv)) b.WriteVector2(uvs![i]);
                for (int k = needed; k < size; k++) b.WriteByte(padByte);
            }
        });

    /// <summary>An index list.</summary>
    public uint IndexList(IReadOnlyList<ushort> indices) =>
        Add(XffChunkTypes.IndexList, 2, b =>
        {
            b.WriteUInt16((ushort)indices.Count);
            foreach (ushort i in indices) b.WriteUInt16(i);
        });

    /// <summary>Face attributes: three colours and/or three UVs per triangle (either list may be null).</summary>
    public uint FaceAttributes(int triangles, IReadOnlyList<XffColour>? colours, IReadOnlyList<Vector2>? uvs) =>
        Add(XffChunkTypes.FaceAttributes, 1, b =>
        {
            if (colours is not null && colours.Count != triangles * 3) throw new ArgumentException("Three colours per triangle.", nameof(colours));
            if (uvs is not null && uvs.Count != triangles * 3) throw new ArgumentException("Three UVs per triangle.", nameof(uvs));
            b.WriteUInt16((ushort)triangles);
            b.WriteUInt32((uint)((colours is null ? 0 : XffFaceContents.Colours) | (uvs is null ? 0 : XffFaceContents.Uvs)));
            for (int t = 0; t < triangles; t++)
            {
                if (colours is not null)
                    for (int c = 0; c < 3; c++) Colour(b, colours[t * 3 + c]);
                if (uvs is not null)
                    for (int c = 0; c < 3; c++) b.WriteVector2(uvs[t * 3 + c]);
            }
        });

    /// <summary>A material (the defaults are the common opaque one).</summary>
    public uint Material(XffColour? colour0 = null, XffColour? colour1 = null, XffColour? colour2 = null, XffColour? colour3 = null,
        float single0 = 0.1f, uint value0 = 2, uint value1 = 1, byte byte0 = 2, byte byte1 = 1, float single1 = 0f) =>
        Add(XffChunkTypes.Material, 2, b =>
        {
            Colour(b, colour0 ?? new XffColour(50, 50, 137, 0));
            Colour(b, colour1 ?? new XffColour(229, 229, 229, 0));
            Colour(b, colour2 ?? new XffColour(16, 16, 45, 0));
            Colour(b, colour3 ?? new XffColour(127, 127, 127, 0));
            b.WriteSingle(single0);
            b.WriteUInt32(value0);
            b.WriteUInt32(value1);
            b.WriteByte(byte0);
            b.WriteByte(byte1);
            b.WriteSingle(single1);
        });

    /// <summary>A texture.</summary>
    public uint Texture(string fileName) => Add(XffChunkTypes.Texture, 1, b => Name(b, fileName));

    /// <summary>A mesh instance with the given submesh records.</summary>
    public uint MeshInstance(uint node, string name, IReadOnlyList<XffSubmesh> submeshes) =>
        Add(XffChunkTypes.MeshInstance, 3, b =>
        {
            b.WriteUInt32(node);
            Name(b, name);
            b.WriteUInt16((ushort)submeshes.Count);
            foreach (var s in submeshes)
            {
                b.WriteUInt32(s.VertexBuffer);
                b.WriteUInt32(s.IndexList);
                b.WriteUInt32(s.FaceAttributes);
                b.WriteUInt32(s.Material);
                b.WriteUInt32(s.Texture);
                b.WriteUInt32(s.ReflectionTexture);
                b.WriteUInt32(s.FirstIndex);
                b.WriteUInt32(s.IndexCount);
                b.WriteByte(s.PrimitiveType);
            }
        });

    /// <summary>A submesh record over a whole index list, as the game's files have them.</summary>
    public static XffSubmesh Submesh(uint vertexBuffer, uint indexList, int indexCount, uint faceAttributes, uint material, uint texture) =>
        new(vertexBuffer, indexList, faceAttributes, material, texture, XffFile.NoReference, 0, (uint)indexCount, XffSubmesh.TriangleList);

    /// <summary>An animation track (1005) with one animation, looping, of keys of position and rotation unless <paramref name="flags"/> says otherwise.</summary>
    public uint Track(uint parent, string name, int start, int end, float rate, IReadOnlyList<XffKey> keys,
        XffAnimationFlags flags = XffAnimationFlags.LoopPositionRotation) =>
        Track(parent, name, [new XffAnimation(start, end, rate, flags, keys)]);

    /// <summary>An animation track (1005) with any number of animations; each key writes the channels its animation's flags name.</summary>
    public uint Track(uint parent, string name, IReadOnlyList<XffAnimation> animations) =>
        Add(XffChunkTypes.AnimationTrack, 2, b =>
        {
            b.WriteUInt32(parent);
            Name(b, name);
            b.WriteUInt16((ushort)animations.Count);
            foreach (var a in animations)
            {
                b.WriteInt32(a.Start);
                b.WriteInt32(a.End);
                b.WriteSingle(a.Rate);
                b.WriteUInt16((ushort)a.Flags);
                b.WriteUInt32((uint)a.Keys.Count);
                foreach (var k in a.Keys)
                {
                    if (a.Flags.HasFlag(XffAnimationFlags.Position)) b.WriteVector3(k.Position);
                    if (a.Flags.HasFlag(XffAnimationFlags.Rotation)) b.WriteQuaternion(k.Rotation);
                    if (a.Flags.HasFlag(XffAnimationFlags.Scale)) b.WriteVector3(k.Scale);
                }
            }
        });

    /// <summary>A prefab (1006) placing another scene file.</summary>
    public uint Prefab(uint parent, string name, XffTransform transform, string fileName, byte flag = 1) =>
        Add(XffChunkTypes.Prefab, 1, b => { b.WriteUInt32(parent); Name(b, name); Trs(b, transform); Name(b, fileName); b.WriteByte(flag); });

    /// <summary>A node property (1009).</summary>
    public uint Property(uint node, string text) =>
        Add(XffChunkTypes.Property, 1, b =>
        {
            var bytes = Encoding.Latin1.GetBytes(text);
            b.WriteUInt32(node);
            b.WriteUInt32((uint)bytes.Length + 1);
            b.WriteBytes(bytes);
            b.WriteByte(0);
        });

    /// <summary>
    /// An object node under <paramref name="parent"/> with a one-submesh mesh: a vertex buffer, an index list, face
    /// attributes (colours and UVs when given), a material and, when <paramref name="textureName"/> is set, a texture.
    /// Returns the object node's id.
    /// </summary>
    public uint MeshObject(uint parent, string name, IReadOnlyList<Vector3> positions, IReadOnlyList<Vector3> normals, IReadOnlyList<ushort> indices,
        IReadOnlyList<XffColour>? colours, IReadOnlyList<Vector2>? uvs, string? textureName, XffTransform? transform = null)
    {
        uint node = Object(parent, name, transform);
        uint texture = textureName is null ? XffFile.NoReference : Texture(textureName);
        uint material = Material();
        uint vb = VertexBuffer(positions, normals);
        uint il = IndexList(indices);
        uint fa = colours is null && uvs is null ? XffFile.NoReference : FaceAttributes(indices.Count / 3, colours, uvs);
        MeshInstance(node, "mesh_" + name, [Submesh(vb, il, indices.Count, fa, material, texture)]);
        return node;
    }
}
