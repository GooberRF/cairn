using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// The raw contents of a Red Faction II <c>.rfm</c> (static) / <c>.rfc</c> (character) mesh, version 0x114, as
/// stored. Same magic as the Red Faction PlayStation 2 meshes (<c>12 87 12 87</c>), with the format version in the
/// second field.
/// <para>Layout (little-endian; names are NUL-terminated unless said otherwise):</para>
/// <list type="bullet">
/// <item>magic, version, a byte that is 1 for a skinned mesh;</item>
/// <item>collision spheres (name, parent bone or -1, centre, radius), prop points (name, parent bone or -1,
/// position, orientation quaternion) and, for a skinned mesh, bones (name, parent, a 3x3 rotation and a
/// translation: the inverse bind transform);</item>
/// <item>the object count, the largest LOD count, per object its LOD switch distances, then for every LOD of every
/// object a name, a parent name and two numbers, then every LOD's body;</item>
/// <item>a LOD body: texture names, material records, geometries (static: own vertex list with normals; skinned:
/// triangles only), the LOD's offset, radius and box, and for a skinned mesh one vertex block shared by the LOD's
/// geometries (position, normal, an optional welded source index, four bone/weight pairs).</item>
/// </list>
/// The older Red Faction II prototypes (versions 5, 8, 9 and 10) use another layout and are not read.
/// </summary>
internal sealed record Rf2MeshFile
{
    /// <summary>The magic at offset 0 (bytes <c>12 87 12 87</c>).</summary>
    public const uint Magic = 0x87128712;

    /// <summary>The only version read: every Red Faction II mesh the game loads.</summary>
    public const int Version = 0x114;

    /// <summary>Bone index meaning "no bone" in a vertex's influence slots.</summary>
    public const int NoBone = 0xFF;

    /// <summary>The byte after the version: 1 for a skinned mesh (every <c>.rfc</c>).</summary>
    public bool SkinnedFlag { get; init; }

    /// <summary>True when the LOD bodies use the skinned layout (the flag set and at least one bone).</summary>
    public bool IsSkinned => SkinnedFlag && Bones.Length > 0;

    /// <summary>The bone count stored in a file whose skinned flag is clear (its bones are not stored); 0 normally.</summary>
    public int UnstoredBoneCount { get; init; }

    public ImmutableArray<Rf2Sphere> Spheres { get; init; } = [];

    public ImmutableArray<Rf2Prop> Props { get; init; } = [];

    public ImmutableArray<Rf2Bone> Bones { get; init; } = [];

    /// <summary>The header's largest LOD count.</summary>
    public int MaxLods { get; init; }

    public ImmutableArray<Rf2Object> Objects { get; init; } = [];

    /// <summary>Bytes after the last LOD body (0 in every sample).</summary>
    public int TrailingBytes { get; init; }
}

/// <summary>A collision sphere: name, parent bone (-1 none), centre and radius.</summary>
internal sealed record Rf2Sphere(string Name, int Parent, Vector3 Position, float Radius);

/// <summary>A prop point: name, parent bone (-1 none), position and orientation.</summary>
internal sealed record Rf2Prop(string Name, int Parent, Vector3 Position, Quaternion Orientation);

/// <summary>
/// A bone: name, parent (-1 root), the nine rotation floats as stored (row-major, model space to bone space) and the
/// inverse bind translation.
/// </summary>
internal sealed record Rf2Bone(string Name, int Parent, Matrix4x4 Rotation, Vector3 Translation)
{
    /// <summary>True when the stored rotation is the identity (to 1e-4).</summary>
    public bool IsIdentity =>
        Near(Rotation.M11, 1) && Near(Rotation.M22, 1) && Near(Rotation.M33, 1)
        && Near(Rotation.M12, 0) && Near(Rotation.M13, 0) && Near(Rotation.M21, 0)
        && Near(Rotation.M23, 0) && Near(Rotation.M31, 0) && Near(Rotation.M32, 0);

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;
}

/// <summary>One object: its LOD switch distances (the float maximum for a single LOD) and its LODs.</summary>
internal sealed record Rf2Object(ImmutableArray<float> LodDistances, ImmutableArray<Rf2Lod> Lods);

/// <summary>One level of detail of an object.</summary>
internal sealed record Rf2Lod
{
    public string Name { get; init; } = "";
    public string Parent { get; init; } = "";
    public uint Header0 { get; init; }
    public uint Header1 { get; init; }
    public ImmutableArray<string> Textures { get; init; } = [];
    public ImmutableArray<Rf2Material> Materials { get; init; } = [];
    public ImmutableArray<Rf2Geometry> Geometries { get; init; } = [];
    public Vector3 Offset { get; init; }
    public float Radius { get; init; }
    public Vector3 BoxMin { get; init; }
    public Vector3 BoxMax { get; init; }

    /// <summary>Skinned meshes: the vertices every geometry of the LOD indexes.</summary>
    public ImmutableArray<Rf2SkinVertex> SkinVertices { get; init; } = [];

    /// <summary>Skinned meshes: the number of welded source vertices, or -1 when the vertices carry no source index.</summary>
    public int SourceVertexCount { get; init; } = -1;
}

/// <summary>
/// A material record. Only the two names are understood: the texture the first block names and the second texture
/// (an environment map such as <c>Chrome.tga</c>); the other fields are kept for the facts.
/// </summary>
internal sealed record Rf2Material
{
    public uint Field0 { get; init; }
    public uint Field1 { get; init; }
    public byte Field2 { get; init; }
    public uint Field3 { get; init; }
    public uint Field4 { get; init; }

    /// <summary>The first block's texture name (null when the block is absent).</summary>
    public string? Texture { get; init; }

    /// <summary>The second block's name (never present in the samples).</summary>
    public string? SecondTexture { get; init; }

    /// <summary>The second texture name after the blocks (an environment map), or empty.</summary>
    public string EnvironmentMap { get; init; } = "";
}

/// <summary>A geometry: one texture and material over a set of triangles.</summary>
internal sealed record Rf2Geometry
{
    /// <summary>Static meshes: the geometry's own vertices (position, normal). Empty for skinned meshes.</summary>
    public ImmutableArray<(Vector3 Position, Vector3 Normal)> Vertices { get; init; } = [];
    public ImmutableArray<Rf2Triangle> Triangles { get; init; } = [];

    /// <summary>Index into the LOD's texture names.</summary>
    public int Texture { get; init; }

    /// <summary>Index into the LOD's material records.</summary>
    public int Material { get; init; }

    public float Unknown { get; init; }
}

/// <summary>A triangle corner: a vertex index and its UV.</summary>
internal readonly record struct Rf2Corner(int Vertex, Vector2 Uv);

/// <summary>A triangle as stored: three corners.</summary>
internal readonly record struct Rf2Triangle(Rf2Corner A, Rf2Corner B, Rf2Corner C);

/// <summary>A skinned vertex: position, normal, welded source index (-1 none) and four bone/weight pairs (bone 0xFF unused).</summary>
internal readonly record struct Rf2SkinVertex(Vector3 Position, Vector3 Normal, int Source, Rf2Influences Influences);

/// <summary>Four bone/weight pairs.</summary>
internal readonly record struct Rf2Influences(int Bone0, float Weight0, int Bone1, float Weight1, int Bone2, float Weight2, int Bone3, float Weight3)
{
    public (int Bone, float Weight) this[int slot] => slot switch
    {
        0 => (Bone0, Weight0),
        1 => (Bone1, Weight1),
        2 => (Bone2, Weight2),
        _ => (Bone3, Weight3),
    };
}

/// <summary>Reads the raw layout; never throws (malformed input gives an error).</summary>
internal static class Rf2MeshParser
{
    /// <summary>
    /// True when the field at offset 4 is a Red Faction II format version rather than the Red Faction PlayStation 2
    /// kind (0 or 1): 0x114 in the meshes the game loads, 5, 8, 9 or 10 in older prototypes.
    /// </summary>
    public static bool IsRf2Version(int value) => value is >= 2 and <= 0x1000;

    /// <summary>The Red Faction II version of a mesh head, or null when it is not one.</summary>
    public static int? Rf2VersionOf(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8 || BinaryPrimitives.ReadUInt32LittleEndian(head) != Rf2MeshFile.Magic) return null;
        int version = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        return IsRf2Version(version) ? version : null;
    }

    /// <summary>True for a readable (0x114) Red Faction II head with the skinned flag set; null when it is not one.</summary>
    public static bool? IsSkinned(ReadOnlySpan<byte> head) =>
        Rf2VersionOf(head) == Rf2MeshFile.Version && head.Length >= 9 ? head[8] != 0 : null;

    public static bool TryParse(ReadOnlySpan<byte> data, string name, out Rf2MeshFile? file, out LegacyMeshError? error)
    {
        file = null;
        error = null;
        int? version = Rf2VersionOf(data);
        if (version is null)
        {
            error = new LegacyMeshError($"{name} is not a Red Faction II mesh (the file does not start with 12 87 12 87 and a Red Faction II version).", 0);
            return false;
        }
        if (version != Rf2MeshFile.Version)
        {
            error = new LegacyMeshError(
                $"Red Faction II prototype mesh (version {version}): not supported. Cairn reads the version the game loads (0x114); versions 5, 8, 9 and 10 are early test files with another layout.", 4);
            return false;
        }
        var r = new Cursor(data) { Position = 8 };
        try
        {
            file = Parse(ref r);
            return true;
        }
        catch (Rf2FormatException ex)
        {
            error = new LegacyMeshError($"{name}: {ex.Message}", ex.Offset);
            return false;
        }
    }

    /// <summary>
    /// The bones of a readable (0x114) skinned Red Faction II mesh, read without its geometry (choosing a mesh to play a
    /// clip on); false for anything else.
    /// </summary>
    public static bool TryReadBones(ReadOnlySpan<byte> data, out ImmutableArray<Rf2Bone> bones)
    {
        bones = [];
        if (Rf2VersionOf(data) != Rf2MeshFile.Version) return false;
        var r = new Cursor(data) { Position = 8 };
        try
        {
            var head = ReadHead(ref r);
            if (!head.SkinnedFlag || head.Bones.Count == 0) return false;
            bones = head.Bones.ToImmutable();
            return true;
        }
        catch (Rf2FormatException)
        {
            return false;
        }
    }

    private static Rf2MeshFile Parse(ref Cursor r)
    {
        var (skinnedFlag, spheres, props, bones, boneCount) = ReadHead(ref r);
        bool skinned = skinnedFlag && boneCount > 0;

        int objectCount = r.Count("object", 4);
        int maxLods = r.Int32("header");
        var distances = new ImmutableArray<float>[objectCount];
        int lodTotal = 0;
        for (int o = 0; o < objectCount; o++)
        {
            int k = r.Count("LOD distance", 4);
            var list = ImmutableArray.CreateBuilder<float>(k);
            for (int i = 0; i < k; i++) list.Add(r.Single("LOD distance"));
            distances[o] = list.MoveToImmutable();
            lodTotal += k;
            if (lodTotal > r.Remaining) throw new Rf2FormatException($"the objects declare {lodTotal:N0} levels of detail, more than the bytes left.", r.Position);
        }

        var headers = new (string Name, string Parent, uint A, uint B)[lodTotal];
        for (int i = 0; i < lodTotal; i++)
            headers[i] = (r.CString("LOD name"), r.CString("LOD parent name"), r.UInt32("LOD header"), r.UInt32("LOD header"));

        var lods = new Rf2Lod[lodTotal];
        for (int i = 0; i < lodTotal; i++)
        {
            int at = r.Position;
            try
            {
                lods[i] = ReadLod(ref r, skinned, boneCount) with
                {
                    Name = headers[i].Name,
                    Parent = headers[i].Parent,
                    Header0 = headers[i].A,
                    Header1 = headers[i].B,
                };
            }
            catch (Rf2FormatException ex) when (lodTotal > 1)
            {
                throw new Rf2FormatException($"LOD {i + 1} of {lodTotal} ('{headers[i].Name}', at 0x{at:X}): {ex.Message}", ex.Offset);
            }
        }

        var objects = ImmutableArray.CreateBuilder<Rf2Object>(objectCount);
        int next = 0;
        for (int o = 0; o < objectCount; o++)
        {
            objects.Add(new Rf2Object(distances[o], [.. lods.Skip(next).Take(distances[o].Length)]));
            next += distances[o].Length;
        }

        return new Rf2MeshFile
        {
            SkinnedFlag = skinnedFlag,
            UnstoredBoneCount = skinnedFlag ? 0 : boneCount,
            Spheres = spheres.MoveToImmutable(),
            Props = props.MoveToImmutable(),
            Bones = bones.ToImmutable(),
            MaxLods = maxLods,
            Objects = objects.MoveToImmutable(),
            TrailingBytes = r.Remaining,
        };
    }

    /// <summary>The skinned flag, the collision spheres, the prop points and (when skinned) the bones.</summary>
    private static (bool SkinnedFlag, ImmutableArray<Rf2Sphere>.Builder Spheres, ImmutableArray<Rf2Prop>.Builder Props, ImmutableArray<Rf2Bone>.Builder Bones, int BoneCount) ReadHead(ref Cursor r)
    {
        bool skinnedFlag = r.Byte("header") != 0;

        int sphereCount = r.Count("collision sphere", 1 + 4 + 16);
        var spheres = ImmutableArray.CreateBuilder<Rf2Sphere>(sphereCount);
        for (int i = 0; i < sphereCount; i++)
            spheres.Add(new Rf2Sphere(r.CString("collision sphere name"), r.Int32("collision sphere"), r.Vector3("collision sphere"), r.Single("collision sphere")));

        int propCount = r.Count("prop point", 1 + 4 + 28);
        var props = ImmutableArray.CreateBuilder<Rf2Prop>(propCount);
        for (int i = 0; i < propCount; i++)
        {
            string propName = r.CString("prop point name");
            int parent = r.Int32("prop point");
            var position = r.Vector3("prop point");
            var q = new Quaternion(r.Single("prop point"), r.Single("prop point"), r.Single("prop point"), r.Single("prop point"));
            props.Add(new Rf2Prop(propName, parent, position, q));
        }

        int boneCount = r.Count("bone", skinnedFlag ? 1 + 4 + 48 : 0);
        var bones = ImmutableArray.CreateBuilder<Rf2Bone>(skinnedFlag ? boneCount : 0);
        if (skinnedFlag)
        {
            Span<float> m = stackalloc float[9];
            for (int i = 0; i < boneCount; i++)
            {
                string boneName = r.CString("bone name");
                int parent = r.Int32("bone");
                for (int k = 0; k < 9; k++) m[k] = r.Single("bone");
                var rotation = new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, 0, 0, 0, 1);
                bones.Add(new Rf2Bone(boneName, parent, rotation, r.Vector3("bone")));
            }
        }
        return (skinnedFlag, spheres, props, bones, boneCount);
    }

    /// <summary>
    /// One static LOD body starting at <paramref name="position"/> (a Red Faction II effect embeds one per mesh object),
    /// read exactly as a static mesh's LOD; <paramref name="position"/> moves past it. Throws <see cref="Rf2FormatException"/>.
    /// </summary>
    internal static Rf2Lod ReadStaticLod(ReadOnlySpan<byte> data, ref int position)
    {
        var r = new Cursor(data) { Position = position };
        // an effect's body has no material records and names material -1 (none)
        var lod = ReadLod(ref r, skinned: false, boneCount: 0, allowNoMaterial: true);
        position = r.Position;
        return lod;
    }

    private static Rf2Lod ReadLod(ref Cursor r, bool skinned, int boneCount, bool allowNoMaterial = false)
    {
        int textureCount = r.Count("texture name", 1);
        var textures = ImmutableArray.CreateBuilder<string>(textureCount);
        for (int i = 0; i < textureCount; i++) textures.Add(r.CString("texture name"));

        int materialCount = r.Count("material", 20);
        var materials = ImmutableArray.CreateBuilder<Rf2Material>(materialCount);
        for (int i = 0; i < materialCount; i++) materials.Add(ReadMaterial(ref r));

        int geometryCount = r.Count("geometry", skinned ? 20 : 16);
        var geometries = ImmutableArray.CreateBuilder<Rf2Geometry>(geometryCount);
        for (int g = 0; g < geometryCount; g++)
        {
            var vertices = ImmutableArray<(Vector3, Vector3)>.Empty;
            if (!skinned)
            {
                int nv = r.Count("vertex", 24);
                var list = ImmutableArray.CreateBuilder<(Vector3, Vector3)>(nv);
                for (int v = 0; v < nv; v++) list.Add((r.Vector3("vertex"), r.Vector3("vertex")));
                vertices = list.MoveToImmutable();
            }
            int nt = r.Count("triangle", 36);
            var triangles = ImmutableArray.CreateBuilder<Rf2Triangle>(nt);
            for (int t = 0; t < nt; t++) triangles.Add(new Rf2Triangle(Corner(ref r), Corner(ref r), Corner(ref r)));
            int texture = r.Int32("geometry");
            int material = r.Int32("geometry");
            float unknown = r.Single("geometry");
            if (skinned) r.UInt32("geometry"); // leftover memory in every skinned geometry
            if ((uint)texture >= (uint)textureCount)
                throw new Rf2FormatException($"geometry {g + 1} uses texture {texture}, but the LOD lists {textureCount}.", r.Position);
            if ((uint)material >= (uint)materialCount && !(allowNoMaterial && material == -1))
                throw new Rf2FormatException($"geometry {g + 1} uses material {material}, but the LOD has {materialCount}.", r.Position);
            var tris = triangles.MoveToImmutable();
            if (!skinned) CheckCorners(tris, vertices.Length, g, r.Position);
            geometries.Add(new Rf2Geometry
            {
                Vertices = vertices,
                Triangles = tris,
                Texture = texture,
                Material = material,
                Unknown = unknown,
            });
        }

        var offset = r.Vector3("LOD bounds");
        float radius = r.Single("LOD bounds");
        var min = r.Vector3("LOD bounds");
        var max = r.Vector3("LOD bounds");

        var skin = ImmutableArray<Rf2SkinVertex>.Empty;
        int sources = -1;
        if (skinned)
        {
            int nv = r.Count("skinned vertex", 56);
            sources = r.Int32("skinned vertex block");
            // a source index is 16 bits, so a larger count cannot be addressed
            if (sources < -1 || sources > 0x10000) throw new Rf2FormatException($"the skinned vertex block declares {sources:N0} source vertices.", r.Position - 4);
            r.Require((long)nv * (sources == -1 ? 56 : 58), "skinned vertex block");
            var list = ImmutableArray.CreateBuilder<Rf2SkinVertex>(nv);
            Span<int> b = stackalloc int[4];
            Span<float> w = stackalloc float[4];
            for (int v = 0; v < nv; v++)
            {
                var position = r.Vector3("skinned vertex");
                var normal = r.Vector3("skinned vertex");
                int source = -1;
                if (sources != -1)
                {
                    source = r.UInt16("skinned vertex");
                    if (source >= sources)
                        throw new Rf2FormatException($"skinned vertex {v} names source vertex {source}, but the LOD has {sources}.", r.Position - 2);
                }
                for (int s = 0; s < 4; s++)
                {
                    b[s] = r.Int32("skinned vertex");
                    w[s] = r.Single("skinned vertex");
                    if (b[s] != Rf2MeshFile.NoBone && (b[s] < 0 || b[s] >= boneCount))
                        throw new Rf2FormatException($"skinned vertex {v} is weighted to bone {b[s]}, but the mesh has {boneCount} bones.", r.Position - 8);
                }
                list.Add(new Rf2SkinVertex(position, normal, source, new Rf2Influences(b[0], w[0], b[1], w[1], b[2], w[2], b[3], w[3])));
            }
            skin = list.MoveToImmutable();
            for (int g = 0; g < geometries.Count; g++) CheckCorners(geometries[g].Triangles, skin.Length, g, r.Position);
        }

        return new Rf2Lod
        {
            Textures = textures.MoveToImmutable(),
            Materials = materials.MoveToImmutable(),
            Geometries = geometries.MoveToImmutable(),
            Offset = offset,
            Radius = radius,
            BoxMin = min,
            BoxMax = max,
            SkinVertices = skin,
            SourceVertexCount = sources,
        };
    }

    private static Rf2Corner Corner(ref Cursor r)
    {
        int vertex = r.Int32("triangle");
        return new Rf2Corner(vertex, new Vector2(r.Single("triangle"), r.Single("triangle")));
    }

    private static void CheckCorners(ImmutableArray<Rf2Triangle> triangles, int vertexCount, int geometry, int offset)
    {
        for (int t = 0; t < triangles.Length; t++)
        {
            var tri = triangles[t];
            if ((uint)tri.A.Vertex >= (uint)vertexCount || (uint)tri.B.Vertex >= (uint)vertexCount || (uint)tri.C.Vertex >= (uint)vertexCount)
                throw new Rf2FormatException($"geometry {geometry + 1} triangle {t} uses a vertex outside its {vertexCount}.", offset);
        }
    }

    private static Rf2Material ReadMaterial(ref Cursor r)
    {
        uint f0 = r.UInt32("material");
        uint f1 = r.UInt32("material");
        byte f2 = r.Byte("material");
        uint f3 = r.UInt32("material");
        uint f4 = r.UInt32("material");
        string? first = Block(ref r);
        string? second = Block(ref r);
        r.UInt32("material");
        Floats(ref r);
        r.Require(12, "material");
        r.Position += 12;
        string environment = r.Name65("material");
        r.UInt32("material");
        Floats(ref r);
        r.Single("material");
        Floats(ref r);
        return new Rf2Material
        {
            Field0 = f0,
            Field1 = f1,
            Field2 = f2,
            Field3 = f3,
            Field4 = f4,
            Texture = first,
            SecondTexture = second,
            EnvironmentMap = environment,
        };

        static string? Block(ref Cursor r)
        {
            if (r.Byte("material") == 0) return null;
            r.UInt32("material"); // a pointer left in the file
            string text = r.Name65("material");
            r.UInt32("material");
            r.Single("material");
            r.UInt32("material");
            return text;
        }

        static void Floats(ref Cursor r)
        {
            int n = r.Count("material", 4);
            r.Position += n * 4;
        }
    }

    /// <summary>A bounds-checked little-endian cursor; running out of bytes throws <see cref="Rf2FormatException"/>.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; set; }

        public readonly int Remaining => Math.Max(0, _data.Length - Position);

        public readonly void Require(long count, string what)
        {
            if (count < 0 || count > Remaining)
                throw new Rf2FormatException($"the file ends inside a {what} (needs {count:N0} bytes, {Remaining:N0} left).", Position);
        }

        public byte Byte(string what)
        {
            Require(1, what);
            return _data[Position++];
        }

        public int UInt16(string what)
        {
            Require(2, what);
            int v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
            Position += 2;
            return v;
        }

        public uint UInt32(string what)
        {
            Require(4, what);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return v;
        }

        public int Int32(string what) => (int)UInt32(what);

        public float Single(string what) => BitConverter.Int32BitsToSingle(Int32(what));

        public Vector3 Vector3(string what) => new(Single(what), Single(what), Single(what));

        /// <summary>A count of records at least <paramref name="minimumSize"/> bytes each, checked against the bytes left.</summary>
        public int Count(string what, int minimumSize)
        {
            int at = Position;
            int n = Int32(what + " count");
            if (n < 0 || (long)n * minimumSize > Remaining)
                throw new Rf2FormatException($"the {what} count {n:N0} at 0x{at:X} does not fit in the {Remaining:N0} bytes left.", at);
            return n;
        }

        public string CString(string what)
        {
            int nul = _data[Position..].IndexOf((byte)0);
            if (nul < 0) throw new Rf2FormatException($"a {what} has no terminator before the end of the file.", Position);
            string s = Encoding.Latin1.GetString(_data.Slice(Position, nul));
            Position += nul + 1;
            return s;
        }

        /// <summary>A name with a 16-bit length (65 in the samples; 0 and 0xFFFF mean none) cut at its first NUL.</summary>
        public string Name65(string what)
        {
            int n = UInt16(what);
            if (n is 0 or 0xFFFF) return "";
            Require(n, what);
            var bytes = _data.Slice(Position, n);
            Position += n;
            int nul = bytes.IndexOf((byte)0);
            return Encoding.Latin1.GetString(nul < 0 ? bytes : bytes[..nul]);
        }
    }
}

/// <summary>Malformed Red Faction II mesh data; caught inside the parser and turned into a <see cref="LegacyMeshError"/>.</summary>
internal sealed class Rf2FormatException(string message, long offset) : Exception(message)
{
    public long Offset { get; } = offset;
}
