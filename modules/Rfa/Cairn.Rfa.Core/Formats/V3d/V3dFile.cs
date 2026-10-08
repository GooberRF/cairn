using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>Which kind of mesh file this is, by signature.</summary>
public enum V3dKind
{
    /// <summary>"RF3D", <c>.v3m</c>: a static mesh.</summary>
    StaticMesh,
    /// <summary>"RFCM", <c>.v3c</c>: a character mesh with bones and collision spheres.</summary>
    Character,
}

/// <summary>
/// The 40-byte file header, every field as stored. The counts here are NOT recomputed on write:
/// derived fields are <c>V3dBuilder</c>'s job, and a reader/writer pair that rewrote them could not
/// be byte-exact.
/// </summary>
/// <param name="Signature">0x52463344 ("RF3D") or 0x5246434D ("RFCM").</param>
/// <param name="Version">0x40000 in every known file.</param>
/// <param name="SubmeshCount">Declared number of SUBM sections.</param>
/// <param name="TotalVertices">Reset to 0 by ccrunch.</param>
/// <param name="TotalTriangles">Reset to 0 by ccrunch.</param>
/// <param name="Unknown0">Reset to 0 by ccrunch (normals count in some notes).</param>
/// <param name="TotalMaterials">Sum of the submeshes' material counts.</param>
/// <param name="Unknown1">0 in game files.</param>
/// <param name="Unknown2">0 in game files (dumb section count before ccrunch).</param>
/// <param name="CollisionSphereCount">Declared number of CSPH sections.</param>
public readonly record struct V3dHeader(
    uint Signature,
    int Version,
    int SubmeshCount,
    int TotalVertices,
    int TotalTriangles,
    int Unknown0,
    int TotalMaterials,
    int Unknown1,
    int Unknown2,
    int CollisionSphereCount)
{
    /// <summary>Header size in bytes.</summary>
    public const int Size = 40;

    /// <summary>"RF3D", the .v3m signature.</summary>
    public const uint StaticSignature = 0x52463344;

    /// <summary>"RFCM", the .v3c signature.</summary>
    public const uint CharacterSignature = 0x5246434D;

    /// <summary>The only format version the engine loads.</summary>
    public const int CurrentVersion = 0x40000;

    /// <summary>The mesh kind the signature names.</summary>
    public V3dKind Kind => Signature == CharacterSignature ? V3dKind.Character : V3dKind.StaticMesh;
}

/// <summary>Section type codes (the int32 before each section).</summary>
public static class V3dSectionType
{
    /// <summary>Terminates the section list.</summary>
    public const int End = 0;

    /// <summary>"SUBM": a submesh; its size field is unusable (0 after ccrunch).</summary>
    public const int Submesh = 0x5355424D;

    /// <summary>"CSPH": a collision sphere.</summary>
    public const int CollisionSphere = 0x43535048;

    /// <summary>"BONE" (reads "ENOB" on disk): the skeleton.</summary>
    public const int Bones = 0x424F4E45;

    /// <summary>"DUMB": exporter group data, removed by ccrunch.</summary>
    public const int Dumb = 0x44554D42;
}

/// <summary>One top-level section, in file order.</summary>
public abstract record V3dSection
{
    /// <summary>The type code written before the section.</summary>
    public abstract int Type { get; }
}

/// <summary>Batch triangle: three vertex indices into the batch and the flags word.</summary>
/// <param name="A">First vertex index.</param>
/// <param name="B">Second vertex index.</param>
/// <param name="C">Third vertex index.</param>
/// <param name="Flags">0x20 = double-sided.</param>
public readonly record struct V3dTriangle(ushort A, ushort B, ushort C, ushort Flags)
{
    /// <summary>Disables back-face culling.</summary>
    public const ushort DoubleSided = 0x20;

    /// <summary>Stored size.</summary>
    public const int Size = 8;
}

/// <summary>Triangle plane used for culling (present when the LOD has <see cref="V3dLod.FlagTrianglePlanes"/>).</summary>
/// <param name="Normal">Plane normal.</param>
/// <param name="Distance">Plane distance term.</param>
public readonly record struct V3dPlane(Vector3 Normal, float Distance)
{
    /// <summary>Stored size.</summary>
    public const int Size = 16;
}

/// <summary>
/// Up to four bone influences of one vertex: weights 0..255 (summing to 255 in stock characters)
/// and bone indices (0xFF when the slot is unused).
/// </summary>
public readonly record struct V3dBoneLink(
    byte Weight0, byte Weight1, byte Weight2, byte Weight3,
    byte Bone0, byte Bone1, byte Bone2, byte Bone3)
{
    /// <summary>Stored size.</summary>
    public const int Size = 8;

    /// <summary>Marks an unused slot's bone index.</summary>
    public const byte NoBone = 0xFF;

    /// <summary>Weight of slot 0..3.</summary>
    public byte GetWeight(int slot) => slot switch
    {
        0 => Weight0, 1 => Weight1, 2 => Weight2, 3 => Weight3,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>Bone index of slot 0..3.</summary>
    public byte GetBone(int slot) => slot switch
    {
        0 => Bone0, 1 => Bone1, 2 => Bone2, 3 => Bone3,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };
}

/// <summary>
/// The byte sizes a batch's <c>batch_info</c> declares for its streams, kept verbatim. Stock files
/// round positions, normals, UVs and bone links up to 16 bytes and pad the same-position offsets by a
/// few elements; community exporters often store exact sizes. Each size must be at least what the
/// element count needs; the slack after the elements (and the alignment padding) is written as zeros,
/// which is what every stock and sample file holds there.
/// </summary>
/// <param name="PositionsBytes">Bytes reserved for positions (and, separately, the same for normals).</param>
/// <param name="TrianglesBytes">Bytes reserved for triangles.</param>
/// <param name="SamePositionOffsetsBytes">Bytes reserved for same-position offsets.</param>
/// <param name="BoneLinksBytes">Bytes reserved for bone links; 0 means the batch has none.</param>
/// <param name="TexCoordsBytes">Bytes reserved for UVs.</param>
public readonly record struct V3dBatchSizes(
    ushort PositionsBytes,
    ushort TrianglesBytes,
    ushort SamePositionOffsetsBytes,
    ushort BoneLinksBytes,
    ushort TexCoordsBytes)
{
    /// <summary>
    /// The sizes the stock exporter uses for these counts (all 1737 stock batches follow it):
    /// positions, UVs and bone links rounded up to 16 bytes, triangles exact, and same-position
    /// offsets <c>4 * align4(n) - 2 * n</c> bytes, i.e. <c>2 * align4(n) - n</c> elements (exact when
    /// the vertex count is a multiple of 4, otherwise 6, 4 or 2 elements of slack for n mod 4 = 1, 2, 3).
    /// </summary>
    public static V3dBatchSizes Canonical(int vertices, int triangles, bool hasBoneLinks) => new(
        (ushort)Align16(vertices * 12),
        (ushort)(triangles * V3dTriangle.Size),
        (ushort)(4 * Align4(vertices) - 2 * vertices),
        hasBoneLinks ? (ushort)Align16(vertices * V3dBoneLink.Size) : (ushort)0,
        (ushort)Align16(vertices * 8));

    private static int Align16(int value) => (value + 15) & ~15;

    private static int Align4(int value) => (value + 3) & ~3;
}

/// <summary>
/// One geometry batch (a draw call with one texture): every vertex stream plus the raw batch header.
/// Arrays are exactly as long as the counts they encode; stream sizes live in <see cref="Sizes"/>.
/// </summary>
public sealed record V3dBatch
{
    /// <summary>Size of the in-file batch header (reserved bytes around the texture index).</summary>
    public const int HeaderSize = 0x38;

    /// <summary>Header bytes 0x00..0x1F: overwritten in memory by the engine; stock files hold leftovers here.</summary>
    public ImmutableArray<byte> HeaderReserved0 { get; init; } = [.. new byte[0x20]];

    /// <summary>Index into the LOD's texture list.</summary>
    public int TextureIndex { get; init; }

    /// <summary>Header bytes 0x24..0x37: overwritten in memory by the engine; kept as stored.</summary>
    public ImmutableArray<byte> HeaderReserved1 { get; init; } = [.. new byte[0x14]];

    /// <summary>Vertex positions, submesh-local.</summary>
    public ImmutableArray<Vector3> Positions { get; init; } = [];

    /// <summary>Vertex normals; same count as positions.</summary>
    public ImmutableArray<Vector3> Normals { get; init; } = [];

    /// <summary>Diffuse UVs; same count as positions.</summary>
    public ImmutableArray<Vector2> TexCoords { get; init; } = [];

    /// <summary>Triangles.</summary>
    public ImmutableArray<V3dTriangle> Triangles { get; init; } = [];

    /// <summary>One plane per triangle when the LOD has <see cref="V3dLod.FlagTrianglePlanes"/>; otherwise empty.</summary>
    public ImmutableArray<V3dPlane> Planes { get; init; } = [];

    /// <summary>
    /// Per vertex: when positive, this vertex has the same position as the vertex that many places
    /// earlier (used by clipping and by the morph apply). Same count as positions.
    /// </summary>
    public ImmutableArray<short> SamePositionOffsets { get; init; } = [];

    /// <summary>Per-vertex bone influences; empty when <see cref="V3dBatchSizes.BoneLinksBytes"/> is 0.</summary>
    public ImmutableArray<V3dBoneLink> BoneLinks { get; init; } = [];

    /// <summary>
    /// When the LOD has <see cref="V3dLod.FlagMorphVerticesMap"/>: <see cref="V3dLod.VertexCount"/>
    /// entries mapping an RFA morph vertex index to a vertex of this batch. Otherwise empty.
    /// </summary>
    public ImmutableArray<short> MorphMap { get; init; } = [];

    /// <summary>The stream sizes declared in <c>batch_info</c>.</summary>
    public V3dBatchSizes Sizes { get; init; }

    /// <summary>Render flags from <c>batch_info</c> (e.g. 0x518C41, 0x110C21 additive).</summary>
    public uint RenderFlags { get; init; }

    /// <summary>Number of vertices.</summary>
    public int VertexCount => Positions.Length;

    /// <summary>Number of triangles.</summary>
    public int TriangleCount => Triangles.Length;
}

/// <summary>A named point a prop or effect attaches to (stored in the LOD data block, 0x64 bytes).</summary>
/// <param name="Name">0x44-byte name; stock files keep leftovers after the terminator.</param>
/// <param name="Rotation">Raw quaternion as stored.</param>
/// <param name="Position">Position as stored (model space for static meshes).</param>
/// <param name="ParentIndex">Bone index, or -1.</param>
public sealed record V3dPropPoint(FixedString Name, Quaternion Rotation, Vector3 Position, int ParentIndex)
{
    /// <summary>Size of the name field.</summary>
    public const int NameSize = 0x44;

    /// <summary>Stored size.</summary>
    public const int Size = 0x64;
}

/// <summary>An entry of a LOD's texture list.</summary>
/// <param name="MaterialIndex">Index into the submesh's materials.</param>
/// <param name="FileName">Zero-terminated name (a copy of the material's diffuse map name).</param>
public sealed record V3dLodTexture(byte MaterialIndex, string FileName);

/// <summary>One level of detail of a submesh.</summary>
public sealed record V3dLod
{
    /// <summary>Batches carry a morph map (characters).</summary>
    public const uint FlagMorphVerticesMap = 0x01;

    /// <summary>Set for characters.</summary>
    public const uint FlagCharacter = 0x02;

    /// <summary>Uses reflective materials.</summary>
    public const uint FlagReflection = 0x04;

    /// <summary>Use the most detailed LOD for collision.</summary>
    public const uint Flag10 = 0x10;

    /// <summary>Batches carry triangle planes.</summary>
    public const uint FlagTrianglePlanes = 0x20;

    /// <summary>LOD flags.</summary>
    public uint Flags { get; init; }

    /// <summary>
    /// The LOD's declared vertex count. NOT the sum of batch vertex counts in most stock files: it is
    /// the original (pre-split) vertex count, and the length of every batch's morph map.
    /// </summary>
    public int VertexCount { get; init; }

    /// <summary>The geometry batches.</summary>
    public ImmutableArray<V3dBatch> Batches { get; init; } = [];

    /// <summary>The int32 after the data block: -1 in most files, 0 in some.</summary>
    public int Unknown1 { get; init; } = -1;

    /// <summary>Prop points, stored at the end of the data block.</summary>
    public ImmutableArray<V3dPropPoint> PropPoints { get; init; } = [];

    /// <summary>Textures used by this LOD (at most 7 in the engine).</summary>
    public ImmutableArray<V3dLodTexture> Textures { get; init; } = [];
}

/// <summary>A submesh material (84 bytes).</summary>
/// <param name="DiffuseMap">32-byte texture name.</param>
/// <param name="Emissive">0..1 self-illumination; 1 is full bright.</param>
/// <param name="Unknown0">0 in game files (specular level in some notes).</param>
/// <param name="Unknown1">0 in game files (glossiness in some notes).</param>
/// <param name="ReflectionCoefficient">Not used by the engine.</param>
/// <param name="ReflectionMap">32-byte name; not used by the engine.</param>
/// <param name="Flags">Bitfield (0x1, 0x9, 0x11, 0x19 seen); not used by the engine.</param>
public sealed record V3dMaterial(
    FixedString DiffuseMap,
    float Emissive,
    float Unknown0,
    float Unknown1,
    float ReflectionCoefficient,
    FixedString ReflectionMap,
    uint Flags)
{
    /// <summary>Size of each name field.</summary>
    public const int NameSize = 32;

    /// <summary>Stored size.</summary>
    public const int Size = 84;
}

/// <summary>An entry of the submesh's trailing list (24-byte name plus a float; usually one entry naming the submesh).</summary>
/// <param name="Name">24-byte name.</param>
/// <param name="Value">0 in game files.</param>
public sealed record V3dSubmeshTrailer(FixedString Name, float Value)
{
    /// <summary>Stored size.</summary>
    public const int Size = 28;
}

/// <summary>A SUBM section: one exported object with 1-3 LODs.</summary>
public sealed record V3dSubmesh : V3dSection
{
    /// <summary>Size of each name field.</summary>
    public const int NameSize = 24;

    /// <inheritdoc />
    public override int Type => V3dSectionType.Submesh;

    /// <summary>The section size field as stored (0 after ccrunch; never used to find the end).</summary>
    public int SizeField { get; init; }

    /// <summary>24-byte object name.</summary>
    public FixedString Name { get; init; } = FixedString.FromText("", NameSize);

    /// <summary>24-byte "None" or the 3ds max group name.</summary>
    public FixedString ParentName { get; init; } = FixedString.FromText("None", NameSize);

    /// <summary>7 in every known file.</summary>
    public int Version { get; init; } = 7;

    /// <summary>Camera distance at which each LOD starts; one per LOD.</summary>
    public ImmutableArray<float> LodDistances { get; init; } = [];

    /// <summary>Submesh offset.</summary>
    public Vector3 Offset { get; init; }

    /// <summary>Bounding sphere radius.</summary>
    public float Radius { get; init; }

    /// <summary>Bounding box minimum.</summary>
    public Vector3 AabbMin { get; init; }

    /// <summary>Bounding box maximum.</summary>
    public Vector3 AabbMax { get; init; }

    /// <summary>Levels of detail, most detailed first; same count as <see cref="LodDistances"/>.</summary>
    public ImmutableArray<V3dLod> Lods { get; init; } = [];

    /// <summary>Materials.</summary>
    public ImmutableArray<V3dMaterial> Materials { get; init; } = [];

    /// <summary>The trailing list (one entry in every stock file).</summary>
    public ImmutableArray<V3dSubmeshTrailer> Trailers { get; init; } = [];
}

/// <summary>A CSPH section: one collision sphere.</summary>
/// <param name="Name">24-byte name.</param>
/// <param name="BoneIndex">Bone the sphere follows, or -1.</param>
/// <param name="Position">Centre relative to the bone.</param>
/// <param name="Radius">Radius.</param>
/// <param name="Extra">Bytes after the 44 known ones when the size field says there are any (none in stock files).</param>
public sealed record V3dCollisionSphere(FixedString Name, int BoneIndex, Vector3 Position, float Radius, ImmutableArray<byte> Extra)
    : V3dSection
{
    /// <summary>Size of the name field.</summary>
    public const int NameSize = 24;

    /// <summary>Size of the known fields.</summary>
    public const int Size = 44;

    /// <inheritdoc />
    public override int Type => V3dSectionType.CollisionSphere;

    /// <summary>
    /// The section size field as stored when it is smaller than the 44 bytes the sphere occupies (a mod tool wrote 40;
    /// the game reads the 44-byte record whatever the field says), kept so the file round-trips; null: the size written.
    /// </summary>
    public int? ShortSizeField { get; init; }
}

/// <summary>
/// One bone as stored (56 bytes). <see cref="Rotation"/> and <see cref="Position"/> are the INVERSE
/// bind transform with the rotation conjugated (v3c_skeleton.md); <c>Animation.Skeleton</c> converts.
/// </summary>
/// <param name="Name">24-byte name.</param>
/// <param name="Rotation">Raw stored quaternion (x y z w).</param>
/// <param name="Position">Raw stored translation (model -> bone).</param>
/// <param name="ParentIndex">-1 for the root; may be greater than this bone's index.</param>
public readonly record struct V3dBone(FixedString Name, Quaternion Rotation, Vector3 Position, int ParentIndex)
{
    /// <summary>Size of the name field.</summary>
    public const int NameSize = 24;

    /// <summary>Stored size.</summary>
    public const int Size = 56;
}

/// <summary>A BONE section.</summary>
/// <param name="Bones">The bones in index order (the order every clip addresses).</param>
/// <param name="Extra">Bytes after the bone array when the size field says there are any (none in stock files).</param>
public sealed record V3dBoneSection(ImmutableArray<V3dBone> Bones, ImmutableArray<byte> Extra) : V3dSection
{
    /// <summary>The engine's bone limit.</summary>
    public const int MaxBones = 50;

    /// <inheritdoc />
    public override int Type => V3dSectionType.Bones;

    /// <summary>
    /// The section size field as stored when it is smaller than the bone array (a mod tool counted 44 bytes a bone; the
    /// game reads 56-byte bones whatever the field says), kept so the file round-trips; null: the size written.
    /// </summary>
    public int? ShortSizeField { get; init; }
}

/// <summary>A DUMB section (3ds max group data, removed by ccrunch; none in stock files). Kept raw.</summary>
/// <param name="Body">The section body.</param>
public sealed record V3dDumbSection(ImmutableArray<byte> Body) : V3dSection
{
    /// <inheritdoc />
    public override int Type => V3dSectionType.Dumb;

    /// <summary>The 24-byte group name at the start of the body, when present.</summary>
    public string GroupName => Body.Length >= 24 ? FixedString.FromBytes(Body.AsSpan(0, 24)).Text : string.Empty;
}

/// <summary>A section of a type this reader does not know, kept byte for byte.</summary>
/// <param name="SectionType">The stored type code.</param>
/// <param name="Body">The section body (its size field's worth of bytes).</param>
public sealed record V3dUnknownSection(int SectionType, ImmutableArray<byte> Body) : V3dSection
{
    /// <inheritdoc />
    public override int Type => SectionType;
}

/// <summary>
/// An immutable .v3c or .v3m file: header, sections in file order, the END section's size field and
/// any bytes after it. <see cref="V3dReader"/> and <see cref="V3dWriter"/> round-trip every stock mesh
/// byte for byte. Fields are raw (file conventions); the bone conversion lives in <c>Animation</c>.
/// </summary>
public sealed record V3dFile
{
    /// <summary>The header fields as stored.</summary>
    public V3dHeader Header { get; init; }

    /// <summary>Every section before END, in file order.</summary>
    public ImmutableArray<V3dSection> Sections { get; init; } = [];

    /// <summary>The END section's size field (0 in every known file).</summary>
    public int EndSizeField { get; init; }

    /// <summary>Bytes after the END section (none in any known file).</summary>
    public ImmutableArray<byte> TrailingBytes { get; init; } = [];

    /// <summary>Static mesh or character, from the signature.</summary>
    public V3dKind Kind => Header.Kind;

    /// <summary>The submeshes, in file order.</summary>
    public IEnumerable<V3dSubmesh> Submeshes => Sections.OfType<V3dSubmesh>();

    /// <summary>The collision spheres, in file order.</summary>
    public IEnumerable<V3dCollisionSphere> CollisionSpheres => Sections.OfType<V3dCollisionSphere>();

    /// <summary>The first BONE section, or null for a mesh without bones.</summary>
    public V3dBoneSection? BoneSection => Sections.OfType<V3dBoneSection>().FirstOrDefault();

    /// <summary>The bones of the first BONE section, or none.</summary>
    public ImmutableArray<V3dBone> Bones => BoneSection?.Bones ?? [];
}
