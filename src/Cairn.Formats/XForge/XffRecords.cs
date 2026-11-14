using System.Numerics;

namespace Cairn.Formats.XForge;

/// <summary>The chunk types of an X-Forge scene (<c>.xff</c>) that <see cref="XffFile"/> reads into typed records.</summary>
/// <remarks>Every type from <see cref="FirstMarker"/> up is a game-object marker: a node whose meaning is its type number.</remarks>
public static class XffChunkTypes
{
    /// <summary>A group node (parent, name, TRS); the first chunk of most files is <c>scene_root</c>.</summary>
    public const ushort GroupNode = 2;
    /// <summary>An object node (parent, name, TRS); a <see cref="MeshInstance"/> chunk holds its mesh.</summary>
    public const ushort ObjectNode = 3;
    /// <summary>A mesh instance: the node it belongs to and its submeshes.</summary>
    public const ushort MeshInstance = 4;
    /// <summary>A camera node.</summary>
    public const ushort Camera = 5;
    /// <summary>A light node (<c>Direct01</c>, <c>weapon_lights00</c>); most of its fields after the transform are unknown.</summary>
    public const ushort Light = 6;
    /// <summary>A helper node (<c>Dummy01</c>, <c>IK Chain01</c>): parent, name and a position only.</summary>
    public const ushort Helper = 7;
    /// <summary>A particle emitter node (<c>PS_gib</c>) naming a <c>.pr3</c> file.</summary>
    public const ushort Emitter = 8;
    /// <summary>A sprite node (<c>SP_flare01</c>) naming a texture.</summary>
    public const ushort Sprite = 9;
    /// <summary>A collision node (<c>col_*</c>): a box with a transform, or a reference to the node whose mesh it uses.</summary>
    public const ushort Collision = 12;
    /// <summary>A vertex buffer: positions and normals.</summary>
    public const ushort VertexBuffer = 1001;
    /// <summary>An index list: a triangle list of <c>u16</c> indices.</summary>
    public const ushort IndexList = 1002;
    /// <summary>A material (34 bytes, mostly unknown fields).</summary>
    public const ushort Material = 1003;
    /// <summary>A texture: a file name in the archive.</summary>
    public const ushort Texture = 1004;
    /// <summary>An animation track: an animated transform node in the hierarchy, with evenly spaced keys.</summary>
    public const ushort AnimationTrack = 1005;
    /// <summary>A prefab: places another <c>.xff</c> file.</summary>
    public const ushort Prefab = 1006;
    /// <summary>Face attributes: per-corner colours and UVs of a triangle list.</summary>
    public const ushort FaceAttributes = 1008;
    /// <summary>A text property of a node (<c>room</c>, <c>portal:room02</c>, <c>w:1.0</c>).</summary>
    public const ushort Property = 1009;
    /// <summary>The first game-object marker type; every type from here up is a node record.</summary>
    public const ushort FirstMarker = 2000;

    /// <summary>True for a game-object marker type (2000 and up).</summary>
    public static bool IsMarker(ushort type) => type >= FirstMarker;

    /// <summary>The version <see cref="XffFile"/> reads for <paramref name="type"/>, or null when the type is not one it types.</summary>
    public static ushort? KnownVersion(ushort type) => type switch
    {
        GroupNode or ObjectNode or Camera or Helper or Emitter or Sprite or Collision => 1,
        MeshInstance => 3,
        Light => 2,
        VertexBuffer or Texture or Prefab or FaceAttributes or Property => 1,
        IndexList or Material or AnimationTrack => 2,
        >= FirstMarker => 1,
        _ => null,
    };

    /// <summary>A short name for messages and facts ("object node", "marker 3107").</summary>
    public static string Describe(ushort type) => type switch
    {
        GroupNode => "group node",
        ObjectNode => "object node",
        MeshInstance => "mesh instance",
        Camera => "camera",
        Light => "light",
        Helper => "helper",
        Emitter => "particle emitter",
        Sprite => "sprite",
        Collision => "collision",
        VertexBuffer => "vertex buffer",
        IndexList => "index list",
        Material => "material",
        Texture => "texture",
        AnimationTrack => "animation track",
        Prefab => "prefab",
        FaceAttributes => "face attributes",
        Property => "property",
        10 => "sound source",
        11 => "microphone",
        1007 => "spring",
        >= FirstMarker => $"marker {type}",
        _ => $"chunk type {type}",
    };
}

/// <summary>The 12-byte header of one chunk.</summary>
/// <param name="Type">The chunk type (<see cref="XffChunkTypes"/>).</param>
/// <param name="Version">The chunk version.</param>
/// <param name="Id">The chunk's id, which other chunks refer to (<see cref="XffFile.NoReference"/> is "none").</param>
/// <param name="Offset">Byte offset of the header in the file.</param>
/// <param name="PayloadSize">Payload length in bytes (after the header).</param>
public readonly record struct XffChunkHeader(ushort Type, ushort Version, uint Id, int Offset, int PayloadSize)
{
    /// <summary>The header's length in bytes.</summary>
    public const int Size = 12;

    /// <summary>Byte offset of the payload in the file.</summary>
    public int PayloadOffset => Offset + Size;
}

/// <summary>A position, rotation and scale, as the node records store them (40 bytes).</summary>
/// <param name="Position">The translation.</param>
/// <param name="Rotation">The rotation as stored (x, y, z, w); <see cref="XffSpace.Rotation"/> says how it rotates.</param>
/// <param name="Scale">The scale (1 in all known files).</param>
public readonly record struct XffTransform(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>The stored size in bytes.</summary>
    public const int Size = 40;

    /// <summary>No translation, no rotation, scale 1.</summary>
    public static XffTransform Identity { get; } = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>This transform as a matrix (row vectors, as System.Numerics: scale, then rotation, then translation).</summary>
    public Matrix4x4 ToMatrix() => XffSpace.LocalMatrix(this);
}

/// <summary>An 8-bit colour as stored: red, green, blue, then a fourth byte (0 in the face attributes, so not an alpha there).</summary>
public readonly record struct XffColour(byte R, byte G, byte B, byte A);

/// <summary>A chunk of an X-Forge scene: its header and payload, typed by a subclass or kept raw (<see cref="XffRawChunk"/>).</summary>
public abstract class XffChunk
{
    private protected XffChunk(XffChunkHeader header, ReadOnlyMemory<byte> payload)
    {
        Header = header;
        Payload = payload;
    }

    /// <summary>The chunk header.</summary>
    public XffChunkHeader Header { get; }

    /// <summary>The chunk's payload bytes, as stored.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The chunk type.</summary>
    public ushort Type => Header.Type;

    /// <summary>The chunk version.</summary>
    public ushort Version => Header.Version;

    /// <summary>The chunk id.</summary>
    public uint Id => Header.Id;

    /// <inheritdoc/>
    public override string ToString() => $"{XffChunkTypes.Describe(Type)} #{Id}";
}

/// <summary>A chunk that sits in the scene tree: it has a parent (or <see cref="XffFile.NoReference"/>) and a name.</summary>
public interface IXffTreeChunk
{
    /// <summary>The parent chunk's id, or <see cref="XffFile.NoReference"/> for a root.</summary>
    uint Parent { get; }

    /// <summary>The node's name.</summary>
    string Name { get; }
}

/// <summary>
/// A chunk kept as bytes: a type or version <see cref="XffFile"/> does not type, or a typed chunk whose payload did not
/// match its layout (then <see cref="Problem"/> says why, and <see cref="XffFile.Diagnostics"/> has it too).
/// </summary>
public sealed class XffRawChunk : XffChunk
{
    internal XffRawChunk(XffChunkHeader header, ReadOnlyMemory<byte> payload, string? problem) : base(header, payload) => Problem = problem;

    /// <summary>Why a chunk of a typed kind was kept raw, or null for a type or version that is not typed.</summary>
    public string? Problem { get; }
}

/// <summary>
/// A node record (types 2, 3 and every marker from 2000 up): parent, name and transform. For a marker the type number
/// is the meaning (<see cref="XffMarkers"/>).
/// </summary>
public sealed class XffNode : XffChunk, IXffTreeChunk
{
    internal XffNode(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, XffTransform transform) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Transform = transform;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The node's local transform.</summary>
    public XffTransform Transform { get; }
    /// <summary>True for a group node (type 2).</summary>
    public bool IsGroup => Type == XffChunkTypes.GroupNode;
    /// <summary>True for an object node (type 3).</summary>
    public bool IsObject => Type == XffChunkTypes.ObjectNode;
    /// <summary>True for a game-object marker (type 2000 and up).</summary>
    public bool IsMarker => XffChunkTypes.IsMarker(Type);
}

/// <summary>A helper node (type 7: <c>Dummy01</c>, <c>IK Chain01</c>, <c>barrel</c>): parent, name and a position (no rotation or scale is stored).</summary>
public sealed class XffHelper : XffChunk, IXffTreeChunk
{
    internal XffHelper(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, Vector3 position) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Position = position;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The helper's position in its parent.</summary>
    public Vector3 Position { get; }
}

/// <summary>A camera node (type 5): parent, target, name, position, rotation, field of view, near and far planes.</summary>
public sealed class XffCamera : XffChunk, IXffTreeChunk
{
    internal XffCamera(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, uint target, string name, Vector3 position,
        Quaternion rotation, float fieldOfView, float nearPlane, float farPlane) : base(header, payload)
    {
        Parent = parent;
        Target = target;
        Name = name;
        Position = position;
        Rotation = rotation;
        FieldOfView = fieldOfView;
        NearPlane = nearPlane;
        FarPlane = farPlane;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <summary>The node the camera looks at, or <see cref="XffFile.NoReference"/> ("none" in both known files).</summary>
    public uint Target { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The camera's position in its parent.</summary>
    public Vector3 Position { get; }
    /// <summary>The camera's rotation in its parent.</summary>
    public Quaternion Rotation { get; }
    /// <summary>The field of view in radians (1.134, 65°, in both known files).</summary>
    public float FieldOfView { get; }
    /// <summary>The near plane distance (0.1 and 0.2).</summary>
    public float NearPlane { get; }
    /// <summary>The far plane distance (40 and 80).</summary>
    public float FarPlane { get; }
}

/// <summary>
/// A light node (type 6, version 2: <c>Direct01</c> in <c>level_04a</c>, <c>weapon_lights00</c>): parent, name,
/// position, rotation, then a kind byte, a colour and seven floats whose meanings are unknown. No light converts.
/// </summary>
public sealed class XffLight : XffChunk, IXffTreeChunk
{
    internal XffLight(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, Vector3 position, Quaternion rotation,
        byte unknownKind, XffColour unknownColour, float[] unknownSingles) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Position = position;
        Rotation = rotation;
        UnknownKind = unknownKind;
        UnknownColour = unknownColour;
        UnknownSingles = unknownSingles;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The node's position in its parent.</summary>
    public Vector3 Position { get; }
    /// <summary>The node's rotation in its parent.</summary>
    public Quaternion Rotation { get; }
    /// <summary>Unknown (3 for the <c>Direct</c> nodes, 1 for the weapon's; perhaps the light type).</summary>
    public byte UnknownKind { get; }
    /// <summary>Unknown colour (white in all known files).</summary>
    public XffColour UnknownColour { get; }
    /// <summary>Seven unknown floats.</summary>
    public IReadOnlyList<float> UnknownSingles { get; }
}

/// <summary>A particle emitter node (type 8, <c>PS_gib</c>): parent, name, a float and the <c>.pr3</c> particle system it plays.</summary>
public sealed class XffEmitter : XffChunk, IXffTreeChunk
{
    internal XffEmitter(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, float unknownSingle, string effectFile) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        UnknownSingle = unknownSingle;
        EffectFile = effectFile;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>Unknown (1.0 in all known files).</summary>
    public float UnknownSingle { get; }
    /// <summary>The particle system's file name (<c>gib.pr3</c>).</summary>
    public string EffectFile { get; }
}

/// <summary>A sprite node (type 9, <c>SP_flare01</c>, a billboard): parent, name, width, height, a texture file name, three bytes and a reference.</summary>
public sealed class XffSprite : XffChunk, IXffTreeChunk
{
    internal XffSprite(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, float width, float height,
        string textureName, byte[] unknownBytes, uint unknownReference) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Width = width;
        Height = height;
        TextureName = textureName;
        UnknownBytes = unknownBytes;
        UnknownReference = unknownReference;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The sprite's width (0.1 to 1).</summary>
    public float Width { get; }
    /// <summary>The sprite's height (equal to the width in all known files).</summary>
    public float Height { get; }
    /// <summary>The sprite's texture file name (<c>flare.pcx</c>).</summary>
    public string TextureName { get; }
    /// <summary>Three unknown bytes (2, 2, 2 in all known files; a blend mode is likely).</summary>
    public IReadOnlyList<byte> UnknownBytes { get; }
    /// <summary>Unknown reference ("none" in all known files).</summary>
    public uint UnknownReference { get; }
}

/// <summary>
/// A collision node (type 12, <c>col_*</c>). With <see cref="UnknownForm"/> 1 it is a box: <see cref="Min"/> and
/// <see cref="Max"/> in the node's own space, placed by <see cref="Transform"/>. With 101 it holds only a reference,
/// <see cref="Source"/>, to the object node whose mesh it uses (named like the collision without <c>col_</c>).
/// </summary>
public sealed class XffCollision : XffChunk, IXffTreeChunk
{
    /// <summary>The <see cref="UnknownForm"/> of a box with a transform.</summary>
    public const byte BoxForm = 1;
    /// <summary>The <see cref="UnknownForm"/> of a collision that refers to a node's mesh.</summary>
    public const byte NodeForm = 101;

    internal XffCollision(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, byte unknownForm, byte unknownByte, string name,
        byte unknownShape, Vector3 min, Vector3 max, XffTransform transform, uint source) : base(header, payload)
    {
        Parent = parent;
        UnknownForm = unknownForm;
        UnknownByte = unknownByte;
        Name = name;
        UnknownShape = unknownShape;
        Min = min;
        Max = max;
        Transform = transform;
        Source = source;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <summary>The record's form: <see cref="BoxForm"/> or <see cref="NodeForm"/> (what else it means is unknown).</summary>
    public byte UnknownForm { get; }
    /// <summary>Unknown (0 in all known files).</summary>
    public byte UnknownByte { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>Unknown shape number: 4 (3,517 colliders) is likely an oriented box, 1 (43) likely a sphere whose bounds are a cube (radius = half the edge).</summary>
    public byte UnknownShape { get; }
    /// <summary>True for a box (<see cref="BoxForm"/>): <see cref="Min"/>, <see cref="Max"/> and <see cref="Transform"/> are set.</summary>
    public bool IsBox => UnknownForm == BoxForm;
    /// <summary>The box's lower corner in its own space (zero unless <see cref="IsBox"/>).</summary>
    public Vector3 Min { get; }
    /// <summary>The box's upper corner in its own space (zero unless <see cref="IsBox"/>).</summary>
    public Vector3 Max { get; }
    /// <summary>The box's transform in its parent (identity unless <see cref="IsBox"/>).</summary>
    public XffTransform Transform { get; }
    /// <summary>The node whose mesh the collision uses (<see cref="NodeForm"/>), else <see cref="XffFile.NoReference"/>.</summary>
    public uint Source { get; }
}

/// <summary>One submesh of a mesh instance (33 bytes): the chunks that hold it and its index range.</summary>
/// <param name="VertexBuffer">The vertex buffer (1001) id.</param>
/// <param name="IndexList">The index list (1002) id.</param>
/// <param name="FaceAttributes">The face attributes (1008) id, or <see cref="XffFile.NoReference"/>.</param>
/// <param name="Material">The material (1003) id.</param>
/// <param name="Texture">The texture (1004) id, or <see cref="XffFile.NoReference"/> for an untextured submesh.</param>
/// <param name="ReflectionTexture">A reflection map texture (1004) id, or <see cref="XffFile.NoReference"/> ("none" in all known files).</param>
/// <param name="FirstIndex">The first index used in the index list (0 in all known files).</param>
/// <param name="IndexCount">How many indices are used (three per triangle).</param>
/// <param name="PrimitiveType">The primitive type: <see cref="TriangleList"/> in all known files (the program also accepts 2 and 3, probably strips and fans).</param>
public readonly record struct XffSubmesh(uint VertexBuffer, uint IndexList, uint FaceAttributes, uint Material, uint Texture, uint ReflectionTexture,
    uint FirstIndex, uint IndexCount, byte PrimitiveType)
{
    /// <summary>The stored size in bytes.</summary>
    public const int Size = 33;

    /// <summary>The <see cref="PrimitiveType"/> of a triangle list, the only one <see cref="XffScene"/> builds.</summary>
    public const byte TriangleList = 1;
}

/// <summary>A mesh instance (type 4, version 3): the node it belongs to, its name (<c>mesh_&lt;node&gt;</c>) and submeshes.</summary>
public sealed class XffMeshInstance : XffChunk
{
    internal XffMeshInstance(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint node, string name, XffSubmesh[] submeshes) : base(header, payload)
    {
        Node = node;
        Name = name;
        Submeshes = submeshes;
    }

    /// <summary>The object node the mesh belongs to.</summary>
    public uint Node { get; }
    /// <summary>The mesh's name.</summary>
    public string Name { get; }
    /// <summary>The submeshes in file order.</summary>
    public IReadOnlyList<XffSubmesh> Submeshes { get; }
}

/// <summary>What each vertex of a vertex buffer holds after its position: the bits of its format value.</summary>
[Flags]
public enum XffVertexFormat : uint
{
    /// <summary>A position only.</summary>
    Position = 0,
    /// <summary>A normal (3 floats).</summary>
    Normal = 1,
    /// <summary>A colour (4 bytes).</summary>
    Colour = 2,
    /// <summary>A UV (2 floats).</summary>
    Uv = 4,
}

/// <summary>
/// A vertex buffer (type 1001): the format (<see cref="XffVertexFormat"/>; 1, position and normal, in all known files),
/// the stride, the count, then per vertex a position (3 floats), a normal, a colour and a UV as the format says, and any
/// further bytes of a longer stride, kept in <see cref="UnknownVertexBytes"/>.
/// </summary>
public sealed class XffVertexBuffer : XffChunk
{
    /// <summary>The bytes of a position and a normal (the stride of every known file).</summary>
    public const int PositionNormalBytes = 24;

    internal XffVertexBuffer(XffChunkHeader header, ReadOnlyMemory<byte> payload, XffVertexFormat format, int stride, Vector3[] positions, Vector3[] normals,
        XffColour[] colours, Vector2[] uvs, byte[] unknownVertexBytes) : base(header, payload)
    {
        Format = format;
        Stride = stride;
        Positions = positions;
        Normals = normals;
        Colours = colours;
        Uvs = uvs;
        UnknownVertexBytes = unknownVertexBytes;
    }

    /// <summary>The bytes of one vertex in <paramref name="format"/> (without any padding of a longer stride).</summary>
    public static int VertexBytes(XffVertexFormat format) =>
        12 + (format.HasFlag(XffVertexFormat.Normal) ? 12 : 0) + (format.HasFlag(XffVertexFormat.Colour) ? 4 : 0) + (format.HasFlag(XffVertexFormat.Uv) ? 8 : 0);

    /// <summary>What each vertex holds.</summary>
    public XffVertexFormat Format { get; }
    /// <summary>Bytes per vertex (24 in all known files).</summary>
    public int Stride { get; }
    /// <summary>The vertex count.</summary>
    public int Count => Positions.Count;
    /// <summary>The positions.</summary>
    public IReadOnlyList<Vector3> Positions { get; }
    /// <summary>The normals, or empty without <see cref="XffVertexFormat.Normal"/>.</summary>
    public IReadOnlyList<Vector3> Normals { get; }
    /// <summary>The vertex colours, or empty without <see cref="XffVertexFormat.Colour"/> (the known files keep colours in the face attributes).</summary>
    public IReadOnlyList<XffColour> Colours { get; }
    /// <summary>The vertex UVs, or empty without <see cref="XffVertexFormat.Uv"/> (the known files keep UVs in the face attributes).</summary>
    public IReadOnlyList<Vector2> Uvs { get; }
    /// <summary>The bytes after the format's data in each vertex, vertex after vertex (empty when the stride holds nothing more).</summary>
    public IReadOnlyList<byte> UnknownVertexBytes { get; }
}

/// <summary>An index list (type 1002, version 2): <c>u16</c> indices, three per triangle.</summary>
public sealed class XffIndexList : XffChunk
{
    internal XffIndexList(XffChunkHeader header, ReadOnlyMemory<byte> payload, ushort[] indices) : base(header, payload) => Indices = indices;

    /// <summary>The indices.</summary>
    public IReadOnlyList<ushort> Indices { get; }
}

/// <summary>What a face attributes chunk holds per corner: the bits of its layout value.</summary>
[Flags]
public enum XffFaceContents : uint
{
    /// <summary>Nothing.</summary>
    None = 0,
    /// <summary>A colour per corner (4 bytes each).</summary>
    Colours = 1,
    /// <summary>A UV per corner (2 floats each).</summary>
    Uvs = 4,
}

/// <summary>
/// Face attributes (type 1008): a triangle count, the layout (<see cref="Contents"/>: 5 for colours and UVs, 1 for
/// colours only, 4 for UVs only), then per triangle its three colours and then its three UVs, corner order as in the
/// index list. The colours are baked vertex lighting.
/// </summary>
public sealed class XffFaceAttributes : XffChunk
{
    internal XffFaceAttributes(XffChunkHeader header, ReadOnlyMemory<byte> payload, int triangleCount, XffFaceContents contents, XffColour[] colours,
        Vector2[] uvs) : base(header, payload)
    {
        TriangleCount = triangleCount;
        Contents = contents;
        Colours = colours;
        Uvs = uvs;
    }

    /// <summary>The triangle count.</summary>
    public int TriangleCount { get; }
    /// <summary>What each corner holds.</summary>
    public XffFaceContents Contents { get; }
    /// <summary>Three colours per triangle, or empty without <see cref="XffFaceContents.Colours"/>.</summary>
    public IReadOnlyList<XffColour> Colours { get; }
    /// <summary>Three UVs per triangle (V = 0 is the image's top row), or empty without <see cref="XffFaceContents.Uvs"/>.</summary>
    public IReadOnlyList<Vector2> Uvs { get; }
}

/// <summary>
/// A material (type 1003, version 2, 34 bytes). Every field is kept; their meanings are not proven. The four colours are
/// probably ambient, diffuse, specular and emissive; <see cref="UnknownByte0"/> and <see cref="UnknownByte1"/> are
/// likely the source and destination blend factors (2, 1 = opaque; 2, 2 = additive; 5, 6 = alpha blend, its alpha from
/// the face attributes' fourth colour byte; 1, 4 = darkening overlay).
/// </summary>
public sealed class XffMaterial : XffChunk
{
    /// <summary>The stored size in bytes.</summary>
    public const int Size = 34;

    internal XffMaterial(XffChunkHeader header, ReadOnlyMemory<byte> payload, XffColour colour0, XffColour colour1, XffColour colour2, XffColour colour3,
        float single0, uint value0, uint value1, byte byte0, byte byte1, float single1) : base(header, payload)
    {
        UnknownColour0 = colour0;
        UnknownColour1 = colour1;
        UnknownColour2 = colour2;
        UnknownColour3 = colour3;
        UnknownSingle0 = single0;
        UnknownFlags = value0;
        UnknownValue1 = value1;
        UnknownByte0 = byte0;
        UnknownByte1 = byte1;
        UnknownSingle1 = single1;
    }

    /// <summary>Unknown colour (probably ambient).</summary>
    public XffColour UnknownColour0 { get; }
    /// <summary>Unknown colour (probably diffuse; 229, 229, 229 in most files).</summary>
    public XffColour UnknownColour1 { get; }
    /// <summary>Unknown colour (probably specular).</summary>
    public XffColour UnknownColour2 { get; }
    /// <summary>Unknown colour (probably emissive; 127, 127, 127 in most files).</summary>
    public XffColour UnknownColour3 { get; }
    /// <summary>Unknown float (0, 0.1, 0.25 or 0.4).</summary>
    public float UnknownSingle0 { get; }
    /// <summary>Flags whose bits 0–3 the game reads as four switches of unknown meaning (0, 1, 2, 3, 8 or 10; bit 3 is on fences, tables and chairs).</summary>
    public uint UnknownFlags { get; }
    /// <summary>Unknown (1 or 4).</summary>
    public uint UnknownValue1 { get; }
    /// <summary>Unknown byte, likely the source blend factor in Direct3D numbering (1 zero, 2 one, 5 source alpha): 2 in opaque, 5 in alpha-blended, 1 in darkening submeshes.</summary>
    public byte UnknownByte0 { get; }
    /// <summary>Unknown byte, likely the destination blend factor (1 zero, 2 one, 4 inverse source colour, 6 inverse source alpha).</summary>
    public byte UnknownByte1 { get; }
    /// <summary>Unknown float (0, ±0.5, 1 and others).</summary>
    public float UnknownSingle1 { get; }
}

/// <summary>A texture (type 1004): the file name in the archive (<c>lvl0_crate_01.pcx</c>).</summary>
public sealed class XffTexture : XffChunk
{
    internal XffTexture(XffChunkHeader header, ReadOnlyMemory<byte> payload, string fileName) : base(header, payload) => FileName = fileName;

    /// <summary>The texture's file name.</summary>
    public string FileName { get; }
}

/// <summary>What an animation's flags say: whether it loops and which channels its keys hold.</summary>
[Flags]
public enum XffAnimationFlags : ushort
{
    /// <summary>Nothing: a clamped animation whose keys hold nothing.</summary>
    None = 0,
    /// <summary>The playing time wraps into [start, end]; without it the time is clamped.</summary>
    Loop = 1,
    /// <summary>Keys hold a position (3 floats).</summary>
    Position = 2,
    /// <summary>Keys hold a rotation (4 floats, x y z w).</summary>
    Rotation = 4,
    /// <summary>Keys hold a scale (3 floats).</summary>
    Scale = 8,
    /// <summary>What every track of the known files has: looping, position and rotation (28-byte keys).</summary>
    LoopPositionRotation = Loop | Position | Rotation,
}

/// <summary>One key of an animation: the channels its flags hold; a channel it does not hold is the identity (0, no rotation, 1).</summary>
/// <param name="Position">The position.</param>
/// <param name="Rotation">The rotation as stored.</param>
/// <param name="Scale">The scale (1 when the keys hold none, as in all known files).</param>
public readonly record struct XffKey(Vector3 Position, Quaternion Rotation, Vector3 Scale)
{
    /// <summary>A key of position and rotation, scale 1.</summary>
    public XffKey(Vector3 position, Quaternion rotation) : this(position, rotation, Vector3.One) { }

    /// <summary>The stored size of a key with <paramref name="flags"/>' channels.</summary>
    public static int SizeFor(XffAnimationFlags flags) =>
        (flags.HasFlag(XffAnimationFlags.Position) ? 12 : 0) + (flags.HasFlag(XffAnimationFlags.Rotation) ? 16 : 0) + (flags.HasFlag(XffAnimationFlags.Scale) ? 12 : 0);
}

/// <summary>
/// One animation of a track: its time range in milliseconds, its key rate, flags and keys. The keys hold no time: key k
/// is at <c>k · floor(1000 / rate)</c> ms from 0 (<see cref="XffSceneTrack.Sample"/> applies the program's rule).
/// </summary>
/// <param name="Start">Where the playing time starts (ms; may be negative).</param>
/// <param name="End">Where it ends or wraps (ms).</param>
/// <param name="Rate">Keys per second (30 in most files).</param>
/// <param name="Flags">Looping and the key channels.</param>
/// <param name="Keys">The keys.</param>
public sealed record XffAnimation(int Start, int End, float Rate, XffAnimationFlags Flags, IReadOnlyList<XffKey> Keys)
{
    /// <summary>The time between keys in whole milliseconds, <c>floor(1000 / rate)</c>, or 0 for a rate that is not positive.</summary>
    public int Interval => Rate > 0 && float.IsFinite(Rate) ? (int)Math.Min(int.MaxValue, Math.Floor(1000.0 / Rate)) : 0;
}

/// <summary>
/// An animation track (type 1005, version 2, the program's "PRS animation group"). It is a transform node in the
/// hierarchy: <see cref="Parent"/> is its parent, and the node it animates (named <see cref="Name"/> without
/// <c>anim_</c>) is its child, with an identity transform of its own. It holds a list of animations, one in every
/// known file; <see cref="Start"/>, <see cref="End"/>, <see cref="Rate"/>, <see cref="Flags"/> and <see cref="Keys"/> are
/// the first one's.
/// </summary>
public sealed class XffAnimationTrack : XffChunk, IXffTreeChunk
{
    /// <summary>The prefix of a track's name before the name of the node it animates.</summary>
    public const string NamePrefix = "anim_";

    private static readonly XffAnimation NoAnimation = new(0, 0, 0, XffAnimationFlags.None, []);

    internal XffAnimationTrack(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, XffAnimation[] animations) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Animations = animations;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>The animations (one in every known file; the program can switch between several).</summary>
    public IReadOnlyList<XffAnimation> Animations { get; }
    /// <summary>The first animation, or an empty one when the track has none.</summary>
    public XffAnimation First => Animations.Count > 0 ? Animations[0] : NoAnimation;
    /// <summary>The first animation's start (milliseconds).</summary>
    public int Start => First.Start;
    /// <summary>The first animation's end (milliseconds).</summary>
    public int End => First.End;
    /// <summary>The first animation's key rate (keys per second).</summary>
    public float Rate => First.Rate;
    /// <summary>The first animation's flags (<see cref="XffAnimationFlags.LoopPositionRotation"/> in all known files).</summary>
    public XffAnimationFlags Flags => First.Flags;
    /// <summary>The first animation's keys.</summary>
    public IReadOnlyList<XffKey> Keys => First.Keys;
    /// <summary>The name of the node the track animates (<see cref="Name"/> without <see cref="NamePrefix"/>), or null when it has no prefix.</summary>
    public string? TargetName => Name.StartsWith(NamePrefix, StringComparison.Ordinal) ? Name[NamePrefix.Length..] : null;
}

/// <summary>A prefab (type 1006): parent, name and transform, then the <c>.xff</c> file it places and a flag.</summary>
public sealed class XffPrefab : XffChunk, IXffTreeChunk
{
    internal XffPrefab(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint parent, string name, XffTransform transform, string fileName,
        byte unknownFlag) : base(header, payload)
    {
        Parent = parent;
        Name = name;
        Transform = transform;
        FileName = fileName;
        UnknownFlag = unknownFlag;
    }

    /// <inheritdoc/>
    public uint Parent { get; }
    /// <inheritdoc/>
    public string Name { get; }
    /// <summary>Where the prefab is placed in its parent.</summary>
    public XffTransform Transform { get; }
    /// <summary>The scene file it places (<c>item_healthpack.xff</c>).</summary>
    public string FileName { get; }
    /// <summary>Unknown (1 in 759 of 763 prefabs, else 0).</summary>
    public byte UnknownFlag { get; }
}

/// <summary>A node property (type 1009): the node it belongs to and its text (<c>room</c>, <c>portal:room02</c>, <c>w:1.0</c>).</summary>
public sealed class XffProperty : XffChunk
{
    internal XffProperty(XffChunkHeader header, ReadOnlyMemory<byte> payload, uint node, string text) : base(header, payload)
    {
        Node = node;
        Text = text;
    }

    /// <summary>The node the property belongs to.</summary>
    public uint Node { get; }
    /// <summary>The text, without its terminating zero.</summary>
    public string Text { get; }
    /// <summary>The part before the first colon (the whole text without one).</summary>
    public string Key => Text.IndexOf(':') is int colon and >= 0 ? Text[..colon] : Text;
    /// <summary>The part after the first colon, or null without one.</summary>
    public string? Value => Text.IndexOf(':') is int colon and >= 0 ? Text[(colon + 1)..] : null;
}

/// <summary>Something a reader or the scene found wrong with one chunk (the file still opens).</summary>
/// <param name="ChunkId">The chunk's id.</param>
/// <param name="Type">The chunk's type.</param>
/// <param name="Offset">Byte offset of the chunk's header.</param>
/// <param name="Message">What is wrong.</param>
public sealed record XffDiagnostic(uint ChunkId, ushort Type, int Offset, string Message)
{
    /// <inheritdoc/>
    public override string ToString() => $"{XffChunkTypes.Describe(Type)} #{ChunkId} at offset {Offset}: {Message}";
}
