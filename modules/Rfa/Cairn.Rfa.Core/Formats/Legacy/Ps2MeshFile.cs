using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// The raw contents of a Red Faction (PlayStation 2) <c>.rfm</c> / <c>.rfc</c> file, as stored. The PS2
/// build tool ("CCrunch") compiled these from the exporter's <c>.v3d</c> / <c>.vcm</c>: it cut every
/// submesh into chunks small enough for one vector-unit upload, copying a vertex into every chunk that
/// uses it.
/// <para>Layout (little-endian):</para>
/// <list type="bullet">
/// <item>a 32-byte header: magic <c>12 87 12 87</c>, 0 for a static mesh or 1 for a character, version 1,
/// the number of submeshes, a field that is 1 in every sample, then the collision sphere, prop point
/// and material counts;</item>
/// <item>sections, each a type code and a byte length, ending with type 0: bones (<c>ENOB</c>, the
/// <c>.v3c</c> bone record), collision spheres (<c>HPSC</c>), prop points (<c>BMUD</c>), then for every
/// stored submesh a geometry section (0x87251110) followed by its materials (0x11133344, the
/// <c>.v3c</c> material record);</item>
/// <item>a geometry section: LOD distance (always the float maximum), version (5), flags (1 for
/// characters), the source vertex count, the box (maximum then minimum), the bounding sphere, the data
/// block, the chunk table and the texture names;</item>
/// <item>the data block: per chunk, 16-byte aligned, the positions, the normals, 80-byte faces (three
/// chunk-local vertex indices, a texture index, the face normal and per-corner UVs plus intensity and
/// chrome / self-illumination flags), four bone weights in sixteenths and four bone indices per vertex,
/// and (characters only) a byte per source vertex giving its index in this chunk (0xFF when the chunk
/// does not use it).</item>
/// </list>
/// Red Faction II uses the same magic with 0x114 in the second field; it is a different layout.
/// </summary>
internal sealed record Ps2MeshFile
{
    /// <summary>The magic at offset 0 (bytes <c>12 87 12 87</c>).</summary>
    public const uint Magic = 0x87128712;

    /// <summary>The only Red Faction version (offset 8).</summary>
    public const int CurrentVersion = 1;

    /// <summary>The value most Red Faction II files store at offset 4 (its format version).</summary>
    public const int Rf2Version = 0x114;

    /// <summary>
    /// True when the field at offset 4 is a Red Faction II format version rather than the Red Faction kind
    /// (0 or 1): 0x114 in 486 of the 519 Red Faction II samples, 5, 8, 9 or 10 in the older others.
    /// </summary>
    public static bool IsRf2Version(int value) => value is >= 2 and <= 0x1000;

    /// <summary>Header size.</summary>
    public const int HeaderSize = 32;

    /// <summary>Section type: one stored submesh (one LOD).</summary>
    public const uint SubmeshSection = 0x87251110;

    /// <summary>Section type: the materials of the submesh before it.</summary>
    public const uint MaterialsSection = 0x11133344;

    /// <summary>Section type: bones ("ENOB" on disk).</summary>
    public const uint BonesSection = 0x424F4E45;

    /// <summary>Section type: one prop point ("BMUD" on disk).</summary>
    public const uint PropSection = 0x44554D42;

    /// <summary>Section type: one collision sphere ("HPSC" on disk).</summary>
    public const uint SphereSection = 0x43535048;

    /// <summary>Size of a face record in the data block.</summary>
    public const int FaceSize = 80;

    /// <summary>True for a character (.rfc), false for a static mesh (.rfm).</summary>
    public bool IsCharacter { get; init; }

    /// <summary>Header offset 12: the number of submeshes (LOD levels not counted).</summary>
    public int SubmeshCount { get; init; }

    /// <summary>Header offset 16 (1 in every sample).</summary>
    public int Unknown16 { get; init; }

    /// <summary>Header counts of collision spheres, prop points and materials.</summary>
    public (int Spheres, int Props, int Materials) DeclaredCounts { get; init; }

    /// <summary>The stored submeshes (LOD levels included) in file order.</summary>
    public ImmutableArray<Ps2Submesh> Submeshes { get; init; } = [];

    /// <summary>The bones as stored (the .v3c record).</summary>
    public ImmutableArray<V3dBone> Bones { get; init; } = [];

    /// <summary>The collision spheres.</summary>
    public ImmutableArray<V3dCollisionSphere> Spheres { get; init; } = [];

    /// <summary>The prop points: 24-byte name, parent bone, rotation, position.</summary>
    public ImmutableArray<(FixedString Name, int Parent, Quaternion Rotation, Vector3 Position)> Props { get; init; } = [];

    /// <summary>Sections of unknown types (type, length), skipped.</summary>
    public ImmutableArray<(uint Type, int Length)> UnknownSections { get; init; } = [];

    /// <summary>Bytes after the end section.</summary>
    public int TrailingBytes { get; init; }
}

/// <summary>One stored submesh (geometry section plus its materials).</summary>
internal sealed record Ps2Submesh
{
    public float LodDistance { get; init; }
    public int Version { get; init; }
    public int Flags { get; init; }
    public int SourceVertexCount { get; init; }
    public Vector3 AabbMax { get; init; }
    public Vector3 AabbMin { get; init; }
    public Vector3 Centre { get; init; }
    public float Radius { get; init; }
    public ImmutableArray<Ps2Chunk> Chunks { get; init; } = [];
    public ImmutableArray<string> TextureNames { get; init; } = [];
    public ImmutableArray<V3dMaterial> Materials { get; init; } = [];
    public bool HasMaterialsSection { get; init; }

    /// <summary>The section's length field minus the bytes the section really used (0 in every sample).</summary>
    public int LengthMismatch { get; init; }

    /// <summary>Data block bytes after the last chunk (0 in every sample).</summary>
    public int UnusedDataBytes { get; init; }
}

/// <summary>A stored face: chunk-local indices, texture index, face normal, per-corner UVs and flags.</summary>
internal readonly record struct Ps2Face(
    int A, int B, int C, int Texture, Vector3 Normal, Vector2 Uv0, Vector2 Uv1, Vector2 Uv2,
    float Intensity0, float Intensity1, int Chrome, int SelfIllumination);

/// <summary>One chunk: vertices (positions, normals, weights) and faces.</summary>
internal sealed record Ps2Chunk
{
    public ImmutableArray<Vector3> Positions { get; init; } = [];
    public ImmutableArray<Vector3> Normals { get; init; } = [];
    public ImmutableArray<Ps2Face> Faces { get; init; } = [];

    /// <summary>Per vertex: four weights (sixteenths) then four bone indices (0xFF unused). Characters only.</summary>
    public ImmutableArray<ulong> Weights { get; init; } = [];

    /// <summary>Characters: per source vertex, its index in this chunk or 0xFF.</summary>
    public ImmutableArray<byte> SourceMap { get; init; } = [];
}

/// <summary>Reads the raw layout; never throws (malformed input gives an error).</summary>
internal static class Ps2MeshParser
{
    /// <summary>The head's kind: null when it is not an RF1 PS2 mesh; also reports Red Faction II.</summary>
    public static bool? IsCharacter(ReadOnlySpan<byte> head, out bool rf2)
    {
        rf2 = false;
        if (head.Length < 12 || BinaryPrimitives.ReadUInt32LittleEndian(head) != Ps2MeshFile.Magic) return null;
        int kind = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
        if (Ps2MeshFile.IsRf2Version(kind))
        {
            rf2 = true;
            return null;
        }
        if (version != Ps2MeshFile.CurrentVersion || kind is not (0 or 1)) return null;
        return kind == 1;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, string name, out Ps2MeshFile? file, out LegacyMeshError? error)
    {
        file = null;
        error = null;
        if (data.Length < Ps2MeshFile.HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(data) != Ps2MeshFile.Magic)
        {
            error = new LegacyMeshError($"{name} is not a Red Faction PlayStation 2 mesh (the file does not start with 12 87 12 87).", 0);
            return false;
        }
        int kind = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int version = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (Ps2MeshFile.IsRf2Version(kind))
        {
            error = new LegacyMeshError($"Red Faction II mesh (version 0x{kind:X}): not supported. Only Red Faction PlayStation 2 meshes can be read.", 4);
            return false;
        }
        if (version != Ps2MeshFile.CurrentVersion || kind is not (0 or 1))
        {
            error = new LegacyMeshError($"{name} has an unknown PlayStation 2 mesh header (kind {kind}, version {version}); Red Faction meshes have kind 0 or 1 and version 1.", 4);
            return false;
        }

        var r = new Cursor(data);
        try
        {
            file = Parse(ref r, kind == 1);
            return true;
        }
        catch (Ps2FormatException ex)
        {
            error = new LegacyMeshError($"{name}: {ex.Message}", ex.Offset);
            return false;
        }
    }

    private static Ps2MeshFile Parse(ref Cursor r, bool character)
    {
        r.Position = 12;
        int submeshCount = r.Int32();
        int unknown16 = r.Int32();
        int spheres = r.Int32();
        int props = r.Int32();
        int materials = r.Int32();

        var subs = ImmutableArray.CreateBuilder<Ps2Submesh>();
        var bones = ImmutableArray<V3dBone>.Empty;
        var sphereList = ImmutableArray.CreateBuilder<V3dCollisionSphere>();
        var propList = ImmutableArray.CreateBuilder<(FixedString, int, Quaternion, Vector3)>();
        var unknown = ImmutableArray.CreateBuilder<(uint, int)>();
        while (true)
        {
            long at = r.Position;
            uint type = r.UInt32("section type");
            int length = r.Int32("section length");
            if (type == 0) break;
            if (length < 0) throw new Ps2FormatException($"section 0x{type:X8} at 0x{at:X} has a negative length ({length}).", at);
            int start = r.Position;
            switch (type)
            {
                case Ps2MeshFile.SubmeshSection:
                    subs.Add(ReadSubmesh(ref r, character, length));
                    continue; // the reader leaves the cursor at the end of what it parsed
                case Ps2MeshFile.MaterialsSection:
                {
                    r.Require(length, "materials section");
                    var mats = ReadMaterials(r.Slice(start, length), start);
                    if (subs.Count == 0)
                        throw new Ps2FormatException("a materials section comes before any submesh.", at);
                    var last = subs[^1];
                    if (last.HasMaterialsSection)
                        throw new Ps2FormatException("a submesh has two materials sections.", at);
                    subs[^1] = last with { Materials = mats, HasMaterialsSection = true };
                    break;
                }
                case Ps2MeshFile.BonesSection:
                    r.Require(length, "bone section");
                    bones = ReadBones(r.Slice(start, length), start);
                    break;
                case Ps2MeshFile.SphereSection:
                {
                    r.Require(length, "collision sphere");
                    if (length < V3dCollisionSphere.Size) throw new Ps2FormatException($"a collision sphere is {length} bytes; it needs {V3dCollisionSphere.Size}.", at);
                    var s = r.Slice(start, length);
                    sphereList.Add(new V3dCollisionSphere(
                        FixedString.FromBytes(s[..24]), I32(s, 24), V3(s, 28), F32(s, 40), [.. s[V3dCollisionSphere.Size..]]));
                    break;
                }
                case Ps2MeshFile.PropSection:
                {
                    r.Require(length, "prop point");
                    if (length < 56) throw new Ps2FormatException($"a prop point is {length} bytes; it needs 56.", at);
                    var s = r.Slice(start, length);
                    propList.Add((FixedString.FromBytes(s[..24]), I32(s, 24), new Quaternion(F32(s, 28), F32(s, 32), F32(s, 36), F32(s, 40)), V3(s, 44)));
                    break;
                }
                default:
                    r.Require(length, $"section 0x{type:X8}");
                    unknown.Add((type, length));
                    break;
            }
            r.Position = start + length;
        }

        return new Ps2MeshFile
        {
            IsCharacter = character,
            SubmeshCount = submeshCount,
            Unknown16 = unknown16,
            DeclaredCounts = (spheres, props, materials),
            Submeshes = subs.ToImmutable(),
            Bones = bones,
            Spheres = sphereList.ToImmutable(),
            Props = propList.ToImmutable(),
            UnknownSections = unknown.ToImmutable(),
            TrailingBytes = r.Remaining,
        };
    }

    private static Ps2Submesh ReadSubmesh(ref Cursor r, bool character, int lengthField)
    {
        int start = r.Position;
        float distance = r.Single();
        int version = r.Int32();
        int flags = r.Int32();
        int sourceVertices = r.Int32("source vertex count");
        if (sourceVertices < 0 || (character && sourceVertices > r.Length))
            throw new Ps2FormatException($"a submesh declares {sourceVertices} source vertices.", start + 12);
        var max = r.Vector3();
        var min = r.Vector3();
        var centre = r.Vector3();
        float radius = r.Single();
        int dataSize = r.Int32("data block size");
        int dataStart = r.Position;
        r.Require(dataSize, "submesh data block");
        r.Position += dataSize;
        int chunkCount = r.UInt16("chunk count");
        r.Require(chunkCount * 10, "chunk table");
        var table = new (int Vertices, int Faces, int VertexBytes, int FaceBytes, int WeightBytes)[chunkCount];
        for (int i = 0; i < chunkCount; i++)
            table[i] = (r.UInt16(), r.UInt16(), r.UInt16(), r.UInt16(), r.UInt16());
        int texCount = r.Int32("texture count");
        if (texCount < 0 || texCount > r.Remaining)
            throw new Ps2FormatException($"a submesh declares {texCount} texture names, more than the bytes left.", r.Position - 4);
        var textures = ImmutableArray.CreateBuilder<string>(texCount);
        for (int i = 0; i < texCount; i++) textures.Add(r.CString("texture name"));
        int end = r.Position;

        // The data block: per chunk positions, normals, faces, weights and (characters) the source map,
        // each part padded to 16 bytes from the start of the block.
        var block = r.Slice(dataStart, dataSize);
        var chunks = ImmutableArray.CreateBuilder<Ps2Chunk>(chunkCount);
        int q = 0;
        for (int c = 0; c < chunkCount; c++)
        {
            var (nv, nf, vb, fb, wb) = table[c];
            string what = $"chunk {c}";
            if (vb < nv * 12 || fb < nf * Ps2MeshFile.FaceSize || (character && wb < nv * 8))
                throw new Ps2FormatException($"{what} reserves too few bytes for its {nv} vertices and {nf} faces.", dataStart);
            var positions = new Vector3[nv];
            var normals = new Vector3[nv];
            var part = Part(block, ref q, vb, what, dataStart);
            for (int i = 0; i < nv; i++) positions[i] = V3(part, i * 12);
            part = Part(block, ref q, vb, what, dataStart);
            for (int i = 0; i < nv; i++) normals[i] = V3(part, i * 12);
            part = Part(block, ref q, fb, what, dataStart);
            var faces = new Ps2Face[nf];
            for (int i = 0; i < nf; i++)
            {
                var f = part.Slice(i * Ps2MeshFile.FaceSize, Ps2MeshFile.FaceSize);
                faces[i] = new Ps2Face(
                    I32(f, 0), I32(f, 4), I32(f, 8), I32(f, 12), V3(f, 16),
                    new Vector2(F32(f, 32), F32(f, 36)), new Vector2(F32(f, 48), F32(f, 52)), new Vector2(F32(f, 64), F32(f, 68)),
                    F32(f, 40), F32(f, 44), I32(f, 72), I32(f, 76));
                if ((uint)faces[i].A >= (uint)nv || (uint)faces[i].B >= (uint)nv || (uint)faces[i].C >= (uint)nv)
                    throw new Ps2FormatException($"{what} face {i} uses a vertex outside the chunk's {nv}.", dataStart + q - fb + i * Ps2MeshFile.FaceSize);
                if ((uint)faces[i].Texture >= (uint)texCount)
                    throw new Ps2FormatException($"{what} face {i} uses texture {faces[i].Texture}, but the submesh lists {texCount}.", dataStart + q - fb + i * Ps2MeshFile.FaceSize);
            }
            part = Part(block, ref q, wb, what, dataStart);
            var weights = ImmutableArray<ulong>.Empty;
            if (character)
            {
                var w = new ulong[nv];
                for (int i = 0; i < nv; i++) w[i] = BinaryPrimitives.ReadUInt64LittleEndian(part[(i * 8)..]);
                weights = [.. w];
            }
            var map = ImmutableArray<byte>.Empty;
            if (character)
            {
                part = Part(block, ref q, sourceVertices, what, dataStart);
                foreach (byte b in part)
                {
                    if (b != 0xFF && b >= nv)
                        throw new Ps2FormatException($"{what}'s vertex map points at vertex {b}, outside the chunk's {nv}.", dataStart + q);
                }
                map = [.. part];
            }
            chunks.Add(new Ps2Chunk
            {
                Positions = [.. positions],
                Normals = [.. normals],
                Faces = [.. faces],
                Weights = weights,
                SourceMap = map,
            });
        }

        return new Ps2Submesh
        {
            LodDistance = distance,
            Version = version,
            Flags = flags,
            SourceVertexCount = sourceVertices,
            AabbMax = max,
            AabbMin = min,
            Centre = centre,
            Radius = radius,
            Chunks = chunks.MoveToImmutable(),
            TextureNames = textures.MoveToImmutable(),
            LengthMismatch = lengthField - (end - start),
            UnusedDataBytes = dataSize - q,
        };
    }

    /// <summary>The next part of a chunk: <paramref name="size"/> bytes, then padding to 16.</summary>
    private static ReadOnlySpan<byte> Part(ReadOnlySpan<byte> block, ref int q, int size, string what, int blockOffset)
    {
        if (size < 0 || size > block.Length - q)
            throw new Ps2FormatException($"{what} runs past the end of its submesh's data block.", blockOffset + q);
        var part = block.Slice(q, size);
        q = Math.Min(block.Length, (q + size + 15) & ~15);
        return part;
    }

    private static ImmutableArray<V3dMaterial> ReadMaterials(ReadOnlySpan<byte> s, int offset)
    {
        if (s.Length < 4) throw new Ps2FormatException("a materials section is too short for its count.", offset);
        int n = I32(s, 0);
        if (n < 0 || n > (s.Length - 4) / V3dMaterial.Size)
            throw new Ps2FormatException($"a materials section declares {n} materials in {s.Length} bytes.", offset);
        var result = ImmutableArray.CreateBuilder<V3dMaterial>(n);
        for (int i = 0; i < n; i++)
        {
            var m = s.Slice(4 + i * V3dMaterial.Size, V3dMaterial.Size);
            result.Add(new V3dMaterial(FixedString.FromBytes(m[..32]), F32(m, 32), F32(m, 36), F32(m, 40), F32(m, 44),
                FixedString.FromBytes(m.Slice(48, 32)), BinaryPrimitives.ReadUInt32LittleEndian(m[80..])));
        }
        return result.MoveToImmutable();
    }

    private static ImmutableArray<V3dBone> ReadBones(ReadOnlySpan<byte> s, int offset)
    {
        if (s.Length < 4) throw new Ps2FormatException("the bone section is too short for its count.", offset);
        int n = I32(s, 0);
        if (n < 0 || n > (s.Length - 4) / V3dBone.Size)
            throw new Ps2FormatException($"the bone section declares {n} bones in {s.Length} bytes.", offset);
        var result = ImmutableArray.CreateBuilder<V3dBone>(n);
        for (int i = 0; i < n; i++)
        {
            var b = s.Slice(4 + i * V3dBone.Size, V3dBone.Size);
            result.Add(new V3dBone(FixedString.FromBytes(b[..24]),
                new Quaternion(F32(b, 24), F32(b, 28), F32(b, 32), F32(b, 36)), V3(b, 40), I32(b, 52)));
        }
        return result.MoveToImmutable();
    }

    private static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    private static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    private static Vector3 V3(ReadOnlySpan<byte> s, int at) => new(F32(s, at), F32(s, at + 4), F32(s, at + 8));

    /// <summary>A bounds-checked little-endian cursor; running out of bytes throws <see cref="Ps2FormatException"/>.</summary>
    private ref struct Cursor(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; set; }

        public readonly int Length => _data.Length;

        public readonly int Remaining => Math.Max(0, _data.Length - Position);

        public readonly void Require(int count, string what)
        {
            if (count < 0 || count > Remaining)
                throw new Ps2FormatException($"the file ends inside the {what} (needs {count} bytes, {Remaining} left).", Position);
        }

        public readonly ReadOnlySpan<byte> Slice(int start, int length) => _data.Slice(start, length);

        public uint UInt32(string what = "header")
        {
            Require(4, what);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
            Position += 4;
            return v;
        }

        public int Int32(string what = "header") => (int)UInt32(what);

        public int UInt16(string what = "chunk table")
        {
            Require(2, what);
            int v = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
            Position += 2;
            return v;
        }

        public float Single() => BitConverter.Int32BitsToSingle(Int32("submesh header"));

        public Vector3 Vector3() => new(Single(), Single(), Single());

        public string CString(string what)
        {
            int nul = _data[Position..].IndexOf((byte)0);
            if (nul < 0) throw new Ps2FormatException($"a {what} has no terminator before the end of the file.", Position);
            string s = Encoding.Latin1.GetString(_data.Slice(Position, nul));
            Position += nul + 1;
            return s;
        }
    }
}

/// <summary>Malformed PS2 mesh data; caught inside the parser and turned into a <see cref="LegacyMeshError"/>.</summary>
internal sealed class Ps2FormatException(string message, long offset) : Exception(message)
{
    public long Offset { get; } = offset;
}
