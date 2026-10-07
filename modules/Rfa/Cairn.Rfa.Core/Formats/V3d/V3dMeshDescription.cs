using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>
/// One bone influence of a vertex: a bone index and a weight. <see cref="V3dBuilder"/> turns up to
/// four of them into the stored <see cref="V3dBoneLink"/> bytes.
/// </summary>
/// <param name="Bone">Bone index, or -1 for an unused slot (stored as 0xFF).</param>
/// <param name="Weight">Weight; stored bytes are <c>weight * 255</c> (normalised first unless the build preserves weights).</param>
public readonly record struct V3dBoneInfluence(int Bone, float Weight);

/// <summary>A vertex of the intermediate mesh description: everything a batch stores per vertex.</summary>
/// <param name="Position">Submesh-local position (RF space).</param>
/// <param name="Normal">Normal (RF space).</param>
/// <param name="TexCoord">Diffuse UV, stored as is (V down, as RF and glTF both use).</param>
/// <param name="Influences">Up to four bone influences (characters only; ignored for static meshes, whose stock links are all zero).</param>
public sealed record V3dMeshVertex(Vector3 Position, Vector3 Normal, Vector2 TexCoord, ImmutableArray<V3dBoneInfluence> Influences)
{
    /// <summary>A vertex with no bone influences.</summary>
    public V3dMeshVertex(Vector3 position, Vector3 normal, Vector2 texCoord) : this(position, normal, texCoord, []) { }
}

/// <summary>A triangle of a material group: three vertex indices into the group and the stored flags.</summary>
/// <param name="A">First vertex index.</param>
/// <param name="B">Second vertex index.</param>
/// <param name="C">Third vertex index.</param>
/// <param name="Flags">Triangle flags; <see cref="V3dTriangle.DoubleSided"/> (0x20) disables back-face culling.</param>
public readonly record struct V3dMeshTriangle(int A, int B, int C, ushort Flags = 0)
{
    /// <summary>True when the triangle is double-sided.</summary>
    public bool IsDoubleSided => (Flags & V3dTriangle.DoubleSided) != 0;
}

/// <summary>
/// The geometry of one material in one LOD: what becomes one batch (or several, when it is larger
/// than a batch's 16-bit size fields allow).
/// </summary>
public sealed record V3dMaterialGroup
{
    /// <summary>The submesh material this geometry uses.</summary>
    public int Material { get; init; }

    /// <summary>Batch render flags; 0x518C41 is what nearly every stock batch uses, 0x110C21 the additive variant.</summary>
    public uint RenderFlags { get; init; } = V3dBuilder.DefaultRenderFlags;

    /// <summary>The vertices.</summary>
    public ImmutableArray<V3dMeshVertex> Vertices { get; init; } = [];

    /// <summary>The triangles (indices into <see cref="Vertices"/>).</summary>
    public ImmutableArray<V3dMeshTriangle> Triangles { get; init; } = [];

    /// <summary>
    /// Batch header bytes 0x00..0x1F to write (stock files carry leftovers there that the engine
    /// overwrites); null writes zeros, which is what a new mesh should hold.
    /// </summary>
    public ImmutableArray<byte>? HeaderReserved0 { get; init; }

    /// <summary>Batch header bytes 0x24..0x37 to write; null writes zeros.</summary>
    public ImmutableArray<byte>? HeaderReserved1 { get; init; }
}

/// <summary>One level of detail of a submesh in the intermediate description.</summary>
public sealed record V3dLodDescription
{
    /// <summary>Camera distance at which this LOD starts (0 for LOD 0 in every stock mesh).</summary>
    public float Distance { get; init; }

    /// <summary>LOD flags; null uses the stock default for the mesh kind (characters 0x03, static meshes 0x20).</summary>
    public uint? Flags { get; init; }

    /// <summary>The int32 after the data block: -1 in most stock files, 0 in some.</summary>
    public int Unknown1 { get; init; } = -1;

    /// <summary>The geometry, one group per material (written in this order).</summary>
    public ImmutableArray<V3dMaterialGroup> Groups { get; init; } = [];

    /// <summary>
    /// The LOD's texture list. Null derives it: every material the groups use, in material order,
    /// named after the material's diffuse map (what most stock LODs hold). Stock static meshes
    /// sometimes list unused materials or LOD-specific names (<c>foo-mip1.tga</c>), which an explicit
    /// list keeps.
    /// </summary>
    public ImmutableArray<V3dLodTexture>? Textures { get; init; }

    /// <summary>
    /// The LOD's "original" vertices, in order, for the morph map (LODs with
    /// <see cref="V3dLod.FlagMorphVerticesMap"/>): an RFA morph vertex index <c>o</c> addresses the
    /// first vertex of each batch at position <c>MorphVertices[o]</c> (-1 in batches without one), and
    /// the LOD's vertex count is their number. NaN never matches (an original no batch uses). Null
    /// derives them: the LOD's distinct positions in first-appearance order.
    /// </summary>
    public ImmutableArray<Vector3>? MorphVertices { get; init; }

    /// <summary>
    /// The LOD's declared vertex count when it has no morph map (the stock exporter writes the
    /// original vertex count there); null uses the number of distinct positions, which is what 85% of
    /// stock static LODs hold.
    /// </summary>
    public int? VertexCountOverride { get; init; }

    /// <summary>Prop points of this LOD when they differ from the mesh-level list; null uses <see cref="V3dMeshDescription.PropPoints"/>.</summary>
    public ImmutableArray<V3dPropPoint>? PropPoints { get; init; }
}

/// <summary>One submesh in the intermediate description.</summary>
public sealed record V3dSubmeshDescription
{
    /// <summary>24-byte object name.</summary>
    public FixedString Name { get; init; } = FixedString.FromText("mesh", V3dSubmesh.NameSize);

    /// <summary>24-byte parent/group name ("None" in nearly every stock file).</summary>
    public FixedString ParentName { get; init; } = FixedString.FromText("None", V3dSubmesh.NameSize);

    /// <summary>Submesh version (7 in every known file).</summary>
    public int Version { get; init; } = 7;

    /// <summary>The SUBM section size field (0 in every stock file; never used to find the end).</summary>
    public int SizeField { get; init; }

    /// <summary>Submesh offset (zero for characters; static meshes place their pivot with it).</summary>
    public Vector3 Offset { get; init; }

    /// <summary>The LODs, most detailed first.</summary>
    public ImmutableArray<V3dLodDescription> Lods { get; init; } = [];

    /// <summary>The materials.</summary>
    public ImmutableArray<V3dMaterial> Materials { get; init; } = [];

    /// <summary>The trailing list; null writes the stock form, one entry naming the submesh with value 0.</summary>
    public ImmutableArray<V3dSubmeshTrailer>? Trailers { get; init; }
}

/// <summary>
/// A mesh as primary data: what an author or an importer controls. <see cref="V3dBuilder.Build"/>
/// derives everything else (batches, sizes, planes, same-position offsets, morph maps, LOD texture
/// lists, bounds, header counts); <see cref="V3dBuilder.Decompose"/> goes the other way, so mesh
/// edits, glTF export and glTF import share one path.
/// </summary>
public sealed record V3dMeshDescription
{
    /// <summary>Character (.v3c, "RFCM") or static mesh (.v3m, "RF3D").</summary>
    public V3dKind Kind { get; init; } = V3dKind.Character;

    /// <summary>The submeshes.</summary>
    public ImmutableArray<V3dSubmeshDescription> Submeshes { get; init; } = [];

    /// <summary>
    /// The bones as stored (inverse bind, conjugated rotation; see <c>Animation.Skeleton</c>).
    /// <see cref="V3dBuilder.BoneFromRestWorld"/> makes one from a rest pose.
    /// </summary>
    public ImmutableArray<V3dBone> Bones { get; init; } = [];

    /// <summary>Collision spheres, written as CSPH sections in this order.</summary>
    public ImmutableArray<V3dCollisionSphere> CollisionSpheres { get; init; } = [];

    /// <summary>Prop points; every stock mesh stores the same list in every LOD of every submesh.</summary>
    public ImmutableArray<V3dPropPoint> PropPoints { get; init; } = [];

    /// <summary>Bytes after the bone array inside the BONE section (none in stock files).</summary>
    public ImmutableArray<byte> BoneSectionExtra { get; init; } = [];

    /// <summary>Other sections (DUMB, unknown), written after the bones.</summary>
    public ImmutableArray<V3dSection> ExtraSections { get; init; } = [];

    /// <summary>The END section's size field.</summary>
    public int EndSizeField { get; init; }

    /// <summary>Bytes after the END section.</summary>
    public ImmutableArray<byte> TrailingBytes { get; init; } = [];
}

/// <summary>How <see cref="V3dBuilder"/> turns bone influences into bytes.</summary>
public enum V3dWeightMode
{
    /// <summary>
    /// Merge duplicate bones, drop non-positive weights, keep the four largest, sort them descending
    /// and normalise to bytes summing to exactly 255 (largest remainder). For new geometry.
    /// </summary>
    Normalize,

    /// <summary>
    /// Store <c>round(weight * 255)</c> slot by slot exactly as given (bone -1 = 0xFF). Decomposed
    /// stock data comes back byte for byte (22% of stock vertices sum to 254).
    /// </summary>
    Preserve,
}

/// <summary>Options for <see cref="V3dBuilder.Build"/>.</summary>
public sealed record V3dBuildOptions
{
    /// <summary>The defaults: weights normalised.</summary>
    public static V3dBuildOptions Default { get; } = new();

    /// <summary>For rebuilding decomposed data: weights preserved byte for byte.</summary>
    public static V3dBuildOptions Preserve { get; } = new() { WeightMode = V3dWeightMode.Preserve };

    /// <summary>How weights become bytes.</summary>
    public V3dWeightMode WeightMode { get; init; } = V3dWeightMode.Normalize;

    /// <summary>Most vertices one batch may hold: 5460 is the most a 16-bit positions size allows (Alpine Faction documents the same limit).</summary>
    public int MaxBatchVertices { get; init; } = V3dBuilder.MaxBatchVertices;

    /// <summary>Most triangles one batch may hold: 8191 is the most a 16-bit triangle size allows.</summary>
    public int MaxBatchTriangles { get; init; } = V3dBuilder.MaxBatchTriangles;
}
