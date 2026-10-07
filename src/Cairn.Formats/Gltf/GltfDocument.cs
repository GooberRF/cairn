using System.Numerics;
using System.Text.Json.Nodes;

namespace Cairn.Formats.Gltf;

/// <summary>
/// Base of every glTF object. Keeps <c>extras</c>, <c>extensions</c> and any member this DOM does
/// not model, so a file read and written back loses nothing an exporter or another tool put there.
/// </summary>
public abstract class GltfProperty
{
    /// <summary>The object's <c>extras</c> value (any JSON), or null when absent.</summary>
    public JsonNode? Extras { get; set; }

    /// <summary>The object's <c>extensions</c> object, kept raw, or null when absent.</summary>
    public JsonObject? Extensions { get; set; }

    /// <summary>
    /// Members this DOM does not model (for example from a newer spec revision), written back
    /// verbatim after the modelled ones. A name that collides with a modelled member is rejected by
    /// the writer rather than silently producing duplicate keys.
    /// </summary>
    public Dictionary<string, JsonNode?> UnknownMembers { get; } = new(StringComparer.Ordinal);
}

/// <summary>Base of the objects that live in one of the document's top-level arrays and may carry a name.</summary>
public abstract class GltfChildOfRoot : GltfProperty
{
    /// <summary>The user-facing name, or null when the file gives none.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// A whole glTF 2.0 document: the JSON object model plus the buffer and image bytes the reader
/// resolved. Every collection is mutable so importers and exporters can build or edit it in place;
/// indices between objects are plain ints, exactly as in the file.
/// </summary>
public sealed class GltfDocument : GltfProperty
{
    /// <summary>The required <c>asset</c> object (version, generator, copyright).</summary>
    public GltfAsset Asset { get; set; } = new();

    /// <summary>The default scene's index, or null when the file names none.</summary>
    public int? Scene { get; set; }

    /// <summary>The scenes.</summary>
    public List<GltfScene> Scenes { get; } = new();

    /// <summary>The nodes (the transform hierarchy).</summary>
    public List<GltfNode> Nodes { get; } = new();

    /// <summary>The meshes.</summary>
    public List<GltfMesh> Meshes { get; } = new();

    /// <summary>The skins.</summary>
    public List<GltfSkin> Skins { get; } = new();

    /// <summary>The materials.</summary>
    public List<GltfMaterial> Materials { get; } = new();

    /// <summary>The textures.</summary>
    public List<GltfTexture> Textures { get; } = new();

    /// <summary>The images.</summary>
    public List<GltfImage> Images { get; } = new();

    /// <summary>The texture samplers.</summary>
    public List<GltfSampler> Samplers { get; } = new();

    /// <summary>The animations.</summary>
    public List<GltfAnimation> Animations { get; } = new();

    /// <summary>The accessors (typed views of buffer data).</summary>
    public List<GltfAccessor> Accessors { get; } = new();

    /// <summary>The buffer views (byte ranges of buffers).</summary>
    public List<GltfBufferView> BufferViews { get; } = new();

    /// <summary>The buffers.</summary>
    public List<GltfBuffer> Buffers { get; } = new();

    /// <summary>The cameras, kept as raw JSON because the workbench never interprets them.</summary>
    public List<JsonNode?> Cameras { get; } = new();

    /// <summary>Names of the extensions used anywhere in the document.</summary>
    public List<string> ExtensionsUsed { get; } = new();

    /// <summary>Names of the extensions a loader must support to read the document correctly.</summary>
    public List<string> ExtensionsRequired { get; } = new();

    /// <summary>
    /// The parent of every node (-1 for roots), derived from the nodes' <see cref="GltfNode.Children"/>
    /// lists because glTF only stores the downward links. Out-of-range or self references are
    /// ignored; when a broken file lists a node under two parents, the first parent (in node order) wins.
    /// </summary>
    public int[] ComputeParents()
    {
        var parents = new int[Nodes.Count];
        Array.Fill(parents, -1);
        for (int i = 0; i < Nodes.Count; i++)
        {
            foreach (int child in Nodes[i].Children)
            {
                if (child >= 0 && child < parents.Length && child != i && parents[child] == -1)
                    parents[child] = i;
            }
        }
        return parents;
    }

    /// <summary>
    /// World matrices of every node in the System.Numerics row-vector convention
    /// (<c>Vector3.Transform(p, M)</c>), composed as local * parent. A parent cycle in a broken file
    /// is broken by treating the node where the cycle was detected as a root, so this never loops or
    /// overflows the stack, however deep the hierarchy.
    /// </summary>
    public Matrix4x4[] ComputeWorldMatrices()
    {
        int[] parents = ComputeParents();
        var world = new Matrix4x4[Nodes.Count];
        var state = new byte[Nodes.Count]; // 0 = pending, 1 = on the current chain, 2 = done
        var chain = new List<int>();
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (state[i] == 2) continue;
            chain.Clear();
            int cur = i;
            while (cur != -1 && state[cur] == 0)
            {
                state[cur] = 1;
                chain.Add(cur);
                cur = parents[cur];
            }
            // cur is -1 (a root was reached), done (reuse its matrix) or on the chain (a cycle).
            Matrix4x4 parentWorld = cur != -1 && state[cur] == 2 ? world[cur] : Matrix4x4.Identity;
            for (int k = chain.Count - 1; k >= 0; k--)
            {
                int n = chain[k];
                world[n] = Nodes[n].LocalMatrix() * parentWorld;
                state[n] = 2;
                parentWorld = world[n];
            }
        }
        return world;
    }
}

/// <summary>The <c>asset</c> object: which glTF version the file follows and who wrote it.</summary>
public sealed class GltfAsset : GltfProperty
{
    /// <summary>The glTF version the file targets; "2.0" for everything this DOM supports.</summary>
    public string Version { get; set; } = "2.0";

    /// <summary>The tool that wrote the file.</summary>
    public string? Generator { get; set; }

    /// <summary>The copyright message.</summary>
    public string? Copyright { get; set; }

    /// <summary>The minimum glTF version a loader must support.</summary>
    public string? MinVersion { get; set; }
}

/// <summary>A scene: the set of root nodes to show.</summary>
public sealed class GltfScene : GltfChildOfRoot
{
    /// <summary>Indices of the scene's root nodes.</summary>
    public List<int> Nodes { get; } = new();
}

/// <summary>A node of the transform hierarchy, optionally carrying a mesh, skin or camera.</summary>
public sealed class GltfNode : GltfChildOfRoot
{
    /// <summary>Indices of the child nodes.</summary>
    public List<int> Children { get; } = new();

    /// <summary>The mesh drawn at this node.</summary>
    public int? Mesh { get; set; }

    /// <summary>The skin that deforms this node's mesh.</summary>
    public int? Skin { get; set; }

    /// <summary>The camera at this node.</summary>
    public int? Camera { get; set; }

    /// <summary>The local transform as 16 floats, column-major as in the file; null when TRS is used.</summary>
    public float[]? Matrix { get; set; }

    /// <summary>The local translation (TRS form).</summary>
    public Vector3? Translation { get; set; }

    /// <summary>The local rotation as a unit quaternion (x, y, z, w) (TRS form).</summary>
    public Quaternion? Rotation { get; set; }

    /// <summary>The local scale (TRS form).</summary>
    public Vector3? Scale { get; set; }

    /// <summary>Morph target weights overriding the mesh's defaults.</summary>
    public float[]? Weights { get; set; }

    /// <summary>
    /// The local transform as a System.Numerics (row-vector) matrix: <see cref="Matrix"/> transposed
    /// from column-major when it holds 16 values, otherwise scale * rotation * translation, which is
    /// glTF's T * R * S in the row-vector convention. Missing TRS parts are identity.
    /// </summary>
    public Matrix4x4 LocalMatrix()
    {
        if (Matrix is { Length: 16 } m)
        {
            // Column-major m[c*4+r] is the column-vector matrix; its transpose (the row-vector
            // matrix) therefore reads the array in order.
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }
        return Matrix4x4.CreateScale(Scale ?? Vector3.One)
            * Matrix4x4.CreateFromQuaternion(Rotation ?? Quaternion.Identity)
            * Matrix4x4.CreateTranslation(Translation ?? Vector3.Zero);
    }
}

/// <summary>A mesh: a list of primitives drawn together, plus default morph weights.</summary>
public sealed class GltfMesh : GltfChildOfRoot
{
    /// <summary>The primitives (one per material/draw call).</summary>
    public List<GltfPrimitive> Primitives { get; } = new();

    /// <summary>Default morph target weights.</summary>
    public float[]? Weights { get; set; }
}

/// <summary>One drawable part of a mesh: vertex attributes, optional indices, a material and morph targets.</summary>
public sealed class GltfPrimitive : GltfProperty
{
    /// <summary>Attribute semantic (POSITION, NORMAL, TEXCOORD_0, JOINTS_0 ...) to accessor index; written in insertion order.</summary>
    public Dictionary<string, int> Attributes { get; } = new(StringComparer.Ordinal);

    /// <summary>The index accessor, or null for non-indexed geometry.</summary>
    public int? Indices { get; set; }

    /// <summary>The material.</summary>
    public int? Material { get; set; }

    /// <summary>The topology (0 points ... 6 triangle fan); null means 4 (triangles).</summary>
    public int? Mode { get; set; }

    /// <summary>Morph targets: each maps an attribute semantic to the accessor holding its displacements.</summary>
    public List<Dictionary<string, int>> Targets { get; } = new();
}

/// <summary>A skin: the joints a mesh is bound to and their inverse bind matrices.</summary>
public sealed class GltfSkin : GltfChildOfRoot
{
    /// <summary>The MAT4 accessor of inverse bind matrices (identity for every joint when null).</summary>
    public int? InverseBindMatrices { get; set; }

    /// <summary>The node used as the skeleton root.</summary>
    public int? Skeleton { get; set; }

    /// <summary>The joint nodes, in the order JOINTS_n values refer to them.</summary>
    public List<int> Joints { get; } = new();
}

/// <summary>
/// A texture reference from a material. One class serves the plain, normal and occlusion forms, so
/// <see cref="Scale"/> is only meaningful for normalTexture and <see cref="Strength"/> for occlusionTexture.
/// </summary>
public sealed class GltfTextureInfo : GltfProperty
{
    /// <summary>The texture index.</summary>
    public int Index { get; set; }

    /// <summary>Which TEXCOORD_n set to use (null = 0).</summary>
    public int? TexCoord { get; set; }

    /// <summary>The normal map scale (normalTexture only).</summary>
    public float? Scale { get; set; }

    /// <summary>The occlusion strength (occlusionTexture only).</summary>
    public float? Strength { get; set; }
}

/// <summary>The metallic-roughness PBR parameters of a material.</summary>
public sealed class GltfPbrMetallicRoughness : GltfProperty
{
    /// <summary>The base colour factor (RGBA).</summary>
    public float[]? BaseColorFactor { get; set; }

    /// <summary>The base colour texture.</summary>
    public GltfTextureInfo? BaseColorTexture { get; set; }

    /// <summary>The metalness factor.</summary>
    public float? MetallicFactor { get; set; }

    /// <summary>The roughness factor.</summary>
    public float? RoughnessFactor { get; set; }

    /// <summary>The metallic (B) / roughness (G) texture.</summary>
    public GltfTextureInfo? MetallicRoughnessTexture { get; set; }
}

/// <summary>A material.</summary>
public sealed class GltfMaterial : GltfChildOfRoot
{
    /// <summary>The PBR parameters.</summary>
    public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }

    /// <summary>The tangent-space normal map.</summary>
    public GltfTextureInfo? NormalTexture { get; set; }

    /// <summary>The occlusion map.</summary>
    public GltfTextureInfo? OcclusionTexture { get; set; }

    /// <summary>The emissive map.</summary>
    public GltfTextureInfo? EmissiveTexture { get; set; }

    /// <summary>The emissive colour (RGB).</summary>
    public float[]? EmissiveFactor { get; set; }

    /// <summary>"OPAQUE", "MASK" or "BLEND" (null = OPAQUE).</summary>
    public string? AlphaMode { get; set; }

    /// <summary>The alpha cut-off for MASK mode.</summary>
    public float? AlphaCutoff { get; set; }

    /// <summary>Whether back faces are drawn.</summary>
    public bool? DoubleSided { get; set; }
}

/// <summary>A texture: an image plus a sampler.</summary>
public sealed class GltfTexture : GltfChildOfRoot
{
    /// <summary>The sampler index.</summary>
    public int? Sampler { get; set; }

    /// <summary>The image index.</summary>
    public int? Source { get; set; }
}

/// <summary>An image, stored as an external file, a data URI or a buffer view.</summary>
public sealed class GltfImage : GltfChildOfRoot
{
    /// <summary>The URI (relative file or data URI) as written in the file, or null.</summary>
    public string? Uri { get; set; }

    /// <summary>The buffer view holding the image bytes, or null.</summary>
    public int? BufferView { get; set; }

    /// <summary>The MIME type ("image/png", "image/jpeg").</summary>
    public string? MimeType { get; set; }

    /// <summary>
    /// The image bytes when known: filled by the reader (data URI, buffer view, or an external file
    /// when <see cref="GltfReadOptions.LoadImages"/> is set), and used by the writer (see
    /// <see cref="GltfWriteOptions"/>).
    /// </summary>
    public byte[]? Data { get; set; }
}

/// <summary>A texture sampler (filters and wrap modes, as GL enum values).</summary>
public sealed class GltfSampler : GltfChildOfRoot
{
    /// <summary>The magnification filter.</summary>
    public int? MagFilter { get; set; }

    /// <summary>The minification filter.</summary>
    public int? MinFilter { get; set; }

    /// <summary>The S (U) wrap mode.</summary>
    public int? WrapS { get; set; }

    /// <summary>The T (V) wrap mode.</summary>
    public int? WrapT { get; set; }
}

/// <summary>An animation: channels that drive node properties from keyframe samplers.</summary>
public sealed class GltfAnimation : GltfChildOfRoot
{
    /// <summary>The channels.</summary>
    public List<GltfAnimationChannel> Channels { get; } = new();

    /// <summary>The samplers the channels refer to.</summary>
    public List<GltfAnimationSampler> Samplers { get; } = new();
}

/// <summary>Connects an animation sampler to the node property it drives.</summary>
public sealed class GltfAnimationChannel : GltfProperty
{
    /// <summary>The sampler index within the animation.</summary>
    public int Sampler { get; set; }

    /// <summary>The animated node and property.</summary>
    public GltfAnimationTarget Target { get; set; } = new();
}

/// <summary>The node and property an animation channel drives.</summary>
public sealed class GltfAnimationTarget : GltfProperty
{
    /// <summary>The node, or null when an extension supplies the target.</summary>
    public int? Node { get; set; }

    /// <summary>"translation", "rotation", "scale" or "weights".</summary>
    public string Path { get; set; } = "translation";
}

/// <summary>Keyframes of one animated property: times (input), values (output) and interpolation.</summary>
public sealed class GltfAnimationSampler : GltfProperty
{
    /// <summary>The SCALAR float accessor of key times in seconds.</summary>
    public int Input { get; set; }

    /// <summary>The accessor of key values (three per key for CUBICSPLINE: in-tangent, value, out-tangent).</summary>
    public int Output { get; set; }

    /// <summary>The interpolation (<see cref="GltfInterpolation"/>); null means LINEAR.</summary>
    public string? Interpolation { get; set; }
}

/// <summary>The animation sampler interpolation names.</summary>
public static class GltfInterpolation
{
    /// <summary>Linear (slerp for rotations).</summary>
    public const string Linear = "LINEAR";

    /// <summary>Hold each key until the next.</summary>
    public const string Step = "STEP";

    /// <summary>Cubic Hermite spline with explicit tangents.</summary>
    public const string CubicSpline = "CUBICSPLINE";
}

/// <summary>The accessor component type codes (GL enum values) and their sizes.</summary>
public static class GltfComponentType
{
    /// <summary>Signed 8-bit.</summary>
    public const int Byte = 5120;

    /// <summary>Unsigned 8-bit.</summary>
    public const int UnsignedByte = 5121;

    /// <summary>Signed 16-bit.</summary>
    public const int Short = 5122;

    /// <summary>Unsigned 16-bit.</summary>
    public const int UnsignedShort = 5123;

    /// <summary>Unsigned 32-bit.</summary>
    public const int UnsignedInt = 5125;

    /// <summary>32-bit IEEE float.</summary>
    public const int Float = 5126;

    /// <summary>Size in bytes of one component of the given type.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The code is not a glTF component type.</exception>
    public static int Size(int componentType) => componentType switch
    {
        Byte or UnsignedByte => 1,
        Short or UnsignedShort => 2,
        UnsignedInt or Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(componentType), componentType, "Not a glTF component type."),
    };

    /// <summary>True when the code is one of the six glTF component types.</summary>
    public static bool IsValid(int componentType) =>
        componentType is Byte or UnsignedByte or Short or UnsignedShort or UnsignedInt or Float;

    internal static string Describe(int componentType) => componentType switch
    {
        Byte => "BYTE",
        UnsignedByte => "UNSIGNED_BYTE",
        Short => "SHORT",
        UnsignedShort => "UNSIGNED_SHORT",
        UnsignedInt => "UNSIGNED_INT",
        Float => "FLOAT",
        _ => componentType.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

/// <summary>The accessor element type names and their component counts.</summary>
public static class GltfAccessorType
{
    /// <summary>One component.</summary>
    public const string Scalar = "SCALAR";

    /// <summary>Two components.</summary>
    public const string Vec2 = "VEC2";

    /// <summary>Three components.</summary>
    public const string Vec3 = "VEC3";

    /// <summary>Four components.</summary>
    public const string Vec4 = "VEC4";

    /// <summary>2x2 matrix, column-major.</summary>
    public const string Mat2 = "MAT2";

    /// <summary>3x3 matrix, column-major.</summary>
    public const string Mat3 = "MAT3";

    /// <summary>4x4 matrix, column-major.</summary>
    public const string Mat4 = "MAT4";

    /// <summary>The number of components in one element of the given type.</summary>
    /// <exception cref="ArgumentException">The name is not a glTF accessor type.</exception>
    public static int ComponentCount(string type) =>
        TryComponentCount(type, out int n) ? n : throw new ArgumentException($"'{type}' is not a glTF accessor type.", nameof(type));

    /// <summary>The component count, or false when the name is not a glTF accessor type.</summary>
    public static bool TryComponentCount(string? type, out int count)
    {
        count = type switch
        {
            Scalar => 1,
            Vec2 => 2,
            Vec3 => 3,
            Vec4 => 4,
            Mat2 => 4,
            Mat3 => 9,
            Mat4 => 16,
            _ => 0,
        };
        return count != 0;
    }
}

/// <summary>A typed view of buffer data: count elements of one type, optionally sparse.</summary>
public sealed class GltfAccessor : GltfChildOfRoot
{
    /// <summary>The buffer view, or null for all-zero data (optionally overridden by <see cref="Sparse"/>).</summary>
    public int? BufferView { get; set; }

    /// <summary>Offset of the first element within the buffer view.</summary>
    public int ByteOffset { get; set; }

    /// <summary>The component type (<see cref="GltfComponentType"/>).</summary>
    public int ComponentType { get; set; }

    /// <summary>Whether integer components map to [0, 1] / [-1, 1].</summary>
    public bool Normalized { get; set; }

    /// <summary>The number of elements.</summary>
    public int Count { get; set; }

    /// <summary>The element type (<see cref="GltfAccessorType"/>).</summary>
    public string Type { get; set; } = GltfAccessorType.Scalar;

    /// <summary>Per-component minimum.</summary>
    public float[]? Min { get; set; }

    /// <summary>Per-component maximum.</summary>
    public float[]? Max { get; set; }

    /// <summary>Sparse substitutions applied on top of the base data.</summary>
    public GltfSparse? Sparse { get; set; }
}

/// <summary>Sparse storage: a list of element indices and the values that replace them.</summary>
public sealed class GltfSparse : GltfProperty
{
    /// <summary>The number of substituted elements.</summary>
    public int Count { get; set; }

    /// <summary>Where the (strictly increasing) element indices are stored.</summary>
    public GltfSparseIndices Indices { get; set; } = new();

    /// <summary>Where the replacement values are stored.</summary>
    public GltfSparseValues Values { get; set; } = new();
}

/// <summary>The location and type of a sparse accessor's indices.</summary>
public sealed class GltfSparseIndices : GltfProperty
{
    /// <summary>The buffer view.</summary>
    public int BufferView { get; set; }

    /// <summary>Offset within the buffer view.</summary>
    public int ByteOffset { get; set; }

    /// <summary>UNSIGNED_BYTE, UNSIGNED_SHORT or UNSIGNED_INT.</summary>
    public int ComponentType { get; set; }
}

/// <summary>The location of a sparse accessor's replacement values.</summary>
public sealed class GltfSparseValues : GltfProperty
{
    /// <summary>The buffer view.</summary>
    public int BufferView { get; set; }

    /// <summary>Offset within the buffer view.</summary>
    public int ByteOffset { get; set; }
}

/// <summary>A byte range of a buffer, optionally strided (interleaved vertex data).</summary>
public sealed class GltfBufferView : GltfChildOfRoot
{
    /// <summary>The buffer index.</summary>
    public int Buffer { get; set; }

    /// <summary>Offset within the buffer.</summary>
    public int ByteOffset { get; set; }

    /// <summary>Length in bytes.</summary>
    public int ByteLength { get; set; }

    /// <summary>Distance between consecutive elements, for interleaved data; null = tightly packed.</summary>
    public int? ByteStride { get; set; }

    /// <summary>The GL buffer target hint (34962 vertex data, 34963 indices).</summary>
    public int? Target { get; set; }
}

/// <summary>A binary buffer: an external .bin file, a data URI or the GLB binary chunk.</summary>
public sealed class GltfBuffer : GltfChildOfRoot
{
    /// <summary>The URI as written in the file, or null (GLB binary chunk).</summary>
    public string? Uri { get; set; }

    /// <summary>The declared length in bytes (the writer recomputes it from <see cref="Data"/> when present).</summary>
    public int ByteLength { get; set; }

    /// <summary>
    /// The bytes, filled by the reader (external .bin, data URI or the GLB BIN chunk, trimmed to
    /// <see cref="ByteLength"/>) and used by the writer.
    /// </summary>
    public byte[]? Data { get; set; }
}
