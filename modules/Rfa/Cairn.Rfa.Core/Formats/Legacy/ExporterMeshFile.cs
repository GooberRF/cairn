using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>A vertex normal of an exporter submesh: the normal and the position it belongs to.</summary>
/// <param name="Normal">The normal as stored (some exporter files hold uninitialised memory here).</param>
/// <param name="Vertex">Index of the position this normal belongs to.</param>
public readonly record struct ExporterNormal(Vector3 Normal, int Vertex);

/// <summary>A triangle of an exporter submesh: position and normal indices and a UV per corner.</summary>
/// <param name="A">First position index.</param>
/// <param name="B">Second position index.</param>
/// <param name="C">Third position index.</param>
/// <param name="NormalA">Normal index of the first corner.</param>
/// <param name="NormalB">Normal index of the second corner.</param>
/// <param name="NormalC">Normal index of the third corner.</param>
/// <param name="UvA">UV of the first corner.</param>
/// <param name="UvB">UV of the second corner.</param>
/// <param name="UvC">UV of the third corner.</param>
/// <param name="Material">Index into the submesh's materials.</param>
public readonly record struct ExporterFace(
    int A, int B, int C, int NormalA, int NormalB, int NormalC, Vector2 UvA, Vector2 UvB, Vector2 UvC, int Material)
{
    /// <summary>Stored size.</summary>
    public const int Size = 52;
}

/// <summary>An entry of an exporter submesh's LOD list: another SUBM section, by name, and its distance.</summary>
/// <param name="Name">The SUBM section that holds this level of detail.</param>
/// <param name="Distance">Camera distance at which it starts.</param>
public readonly record struct ExporterLodLink(FixedString Name, float Distance)
{
    /// <summary>Stored size.</summary>
    public const int Size = 28;
}

/// <summary>A prop point of an exporter mesh (a DUMB section, 56 bytes).</summary>
/// <param name="Name">24-byte name.</param>
/// <param name="ParentIndex">Bone index, or -1.</param>
/// <param name="Rotation">Orientation as stored.</param>
/// <param name="Position">Position as stored.</param>
public sealed record ExporterPropPoint(FixedString Name, int ParentIndex, Quaternion Rotation, Vector3 Position)
{
    /// <summary>Size of the name field.</summary>
    public const int NameSize = 24;

    /// <summary>Stored size.</summary>
    public const int Size = 56;
}

/// <summary>One SUBM section of an exporter mesh: uncompiled geometry exactly as 3ds Max exported it.</summary>
public sealed record ExporterSubmesh
{
    /// <summary>The section's size field (valid in exporter files).</summary>
    public int SizeField { get; init; }

    /// <summary>24-byte object name.</summary>
    public FixedString Name { get; init; } = FixedString.FromText("", V3dSubmesh.NameSize);

    /// <summary>24-byte parent name ("None" nearly always).</summary>
    public FixedString ParentName { get; init; } = FixedString.FromText("None", V3dSubmesh.NameSize);

    /// <summary>The positions (the "original" vertices).</summary>
    public ImmutableArray<Vector3> Positions { get; init; } = [];

    /// <summary>The vertex normals the faces' normal indices address.</summary>
    public ImmutableArray<ExporterNormal> Normals { get; init; } = [];

    /// <summary>The materials, in the same record as a compiled mesh's.</summary>
    public ImmutableArray<V3dMaterial> Materials { get; init; } = [];

    /// <summary>The triangles.</summary>
    public ImmutableArray<ExporterFace> Faces { get; init; } = [];

    /// <summary>Bounding sphere centre (the compiled mesh's submesh offset).</summary>
    public Vector3 Center { get; init; }

    /// <summary>Bounding sphere radius.</summary>
    public float Radius { get; init; }

    /// <summary>Bounding box minimum.</summary>
    public Vector3 AabbMin { get; init; }

    /// <summary>Bounding box maximum.</summary>
    public Vector3 AabbMax { get; init; }

    /// <summary>The lower levels of detail, each naming another SUBM section (this one is level 0).</summary>
    public ImmutableArray<ExporterLodLink> Lods { get; init; } = [];
}

/// <summary>
/// An uncompiled exporter mesh: a <c>.v3d</c> (static, "RF3D") or <c>.vcm</c> (character, "RFCM"). The same
/// container as <c>.v3m</c>/<c>.v3c</c> (40-byte header, typed sections ending with type 0), but every section
/// size is valid and a SUBM holds the raw object: positions, indexed normals, materials and faces with a UV per
/// corner. Characters add a BONE section and a WAIT section of per-vertex weights.
/// </summary>
public sealed record ExporterMeshFile
{
    /// <summary>"TIAW" on disk: the per-vertex bone weights of a character.</summary>
    public const int WeightsSection = 0x57414954;

    /// <summary>The version every exporter character mesh (.vcm) has.</summary>
    public const int CharacterVersion = 0x10000;

    /// <summary>The header fields as stored (the totals are filled in, unlike a compiled mesh's).</summary>
    public V3dHeader Header { get; init; }

    /// <summary>The SUBM sections in file order (LOD levels are SUBM sections too).</summary>
    public ImmutableArray<ExporterSubmesh> Submeshes { get; init; } = [];

    /// <summary>The DUMB sections (prop points), in file order.</summary>
    public ImmutableArray<ExporterPropPoint> PropPoints { get; init; } = [];

    /// <summary>The CSPH sections, in file order.</summary>
    public ImmutableArray<V3dCollisionSphere> CollisionSpheres { get; init; } = [];

    /// <summary>The bones of the BONE section, in index order (none for a static mesh).</summary>
    public ImmutableArray<V3dBone> Bones { get; init; } = [];

    /// <summary>
    /// The WAIT section's weights, one per position of the submeshes in file order (characters), as the same
    /// four (weight, bone) bytes a compiled mesh stores; empty when there is no WAIT section.
    /// </summary>
    public ImmutableArray<V3dBoneLink> Weights { get; init; } = [];

    /// <summary>Type codes of sections this reader does not know (skipped by their size).</summary>
    public ImmutableArray<int> UnknownSections { get; init; } = [];

    /// <summary>Static mesh or character, by signature.</summary>
    public V3dKind Kind => Header.Kind;
}

/// <summary>
/// Reads exporter meshes. Every section size, count and index is checked against the bytes present before
/// anything is allocated; bad input raises <see cref="AssetFormatException"/>.
/// </summary>
public static class ExporterMeshReader
{
    /// <summary>
    /// True when <paramref name="data"/> is an exporter mesh rather than a compiled one: an "RF3D"/"RFCM"
    /// header whose first section is a SUBM that parses in the exporter layout to exactly its stated size.
    /// A compiled .v3m/.v3c never passes (its SUBM size is 0 and its body is laid out differently), whatever
    /// the file is called.
    /// </summary>
    public static bool IsExporterLayout(ReadOnlySpan<byte> data)
    {
        if (!HasExporterHeader(data)) return false;
        // Prop points (DUMB) may come first; every exporter section has a valid size, so walk to the first SUBM.
        int at = V3dHeader.Size;
        for (int guard = 0; guard < 4096 && at <= data.Length - 8; guard++)
        {
            int type = BinaryPrimitives.ReadInt32LittleEndian(data[at..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
            if (type == V3dSectionType.End || size < 0 || size > data.Length - at - 8) return false;
            if (type == V3dSectionType.Submesh) return size > 0 && SubmeshFits(data.Slice(at + 8, size));
            at += 8 + size;
        }
        return false;
    }

    /// <summary>
    /// True when the first bytes could start an exporter mesh: "RF3D" or "RFCM" with version 0x40000 or 0x10000,
    /// header totals filled in (the compiler zeroes them) and, when at least 48 bytes are given, a first section
    /// that is not a size-less SUBM (a compiled mesh stores 0 there).
    /// </summary>
    public static bool LooksLikeExporterMesh(ReadOnlySpan<byte> head)
    {
        if (!HasExporterHeader(head)) return false;
        if (head.Length >= 20 && BinaryPrimitives.ReadInt32LittleEndian(head[12..]) <= 0 && BinaryPrimitives.ReadInt32LittleEndian(head[16..]) <= 0)
            return false;
        if (head.Length < V3dHeader.Size + 8) return true;
        int type = BinaryPrimitives.ReadInt32LittleEndian(head[V3dHeader.Size..]);
        int size = BinaryPrimitives.ReadInt32LittleEndian(head[(V3dHeader.Size + 4)..]);
        return type != V3dSectionType.End && (type != V3dSectionType.Submesh || size > 0);
    }

    private static bool HasExporterHeader(ReadOnlySpan<byte> head)
    {
        if (head.Length < 8) return false;
        uint signature = BinaryPrimitives.ReadUInt32LittleEndian(head);
        int version = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
        return signature is V3dHeader.StaticSignature or V3dHeader.CharacterSignature
            && version is V3dHeader.CurrentVersion or ExporterMeshFile.CharacterVersion;
    }

    private static bool SubmeshFits(ReadOnlySpan<byte> body)
    {
        try
        {
            var r = new BinaryCursor(body.ToArray(), "");
            ReadSubmesh(r, body.Length, 0);
            return r.Position == body.Length;
        }
        catch (AssetFormatException)
        {
            return false;
        }
    }

    /// <summary>Reads a whole exporter mesh.</summary>
    /// <exception cref="AssetFormatException">The bytes are not a readable exporter mesh.</exception>
    public static ExporterMeshFile Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new BinaryCursor(data, name);
        if (data.Length < V3dHeader.Size)
            throw new AssetFormatException($"'{name}' is too short to be a mesh ({data.Length} bytes).");
        uint signature = r.ReadUInt32();
        if (signature is not (V3dHeader.StaticSignature or V3dHeader.CharacterSignature))
            throw new AssetFormatException($"'{name}' is not an exporter mesh (signature 0x{signature:X8}, expected \"RF3D\" or \"RFCM\").");
        int version = r.ReadInt32();
        if (version is not (V3dHeader.CurrentVersion or ExporterMeshFile.CharacterVersion))
            throw new AssetFormatException($"'{name}' is mesh version 0x{version:X}; exporter meshes are version 0x40000 or 0x10000.");
        var header = new V3dHeader(signature, version, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
            r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());

        var submeshes = ImmutableArray.CreateBuilder<ExporterSubmesh>();
        var props = ImmutableArray.CreateBuilder<ExporterPropPoint>();
        var spheres = ImmutableArray.CreateBuilder<V3dCollisionSphere>();
        var unknown = ImmutableArray.CreateBuilder<int>();
        ImmutableArray<V3dBone>? bones = null;
        ImmutableArray<V3dBoneLink>? weights = null;
        while (true)
        {
            int at = r.Position;
            if (r.Remaining < 8) throw r.Fail($"the section list ends at offset {at} without an END section.");
            int type = r.ReadInt32();
            int size = r.ReadInt32();
            if (type == V3dSectionType.End) break;
            if (size < 0) throw r.Fail($"the section at offset {at} has a negative size ({size}).");
            r.Need(size, $"the section at offset {at}");
            var body = new BinaryCursor(r.Data.AsSpan(r.Position, size).ToArray(), name);
            string what = $"the section at offset {at}";
            switch (type)
            {
                case V3dSectionType.Submesh:
                    submeshes.Add(ReadSubmesh(body, size, submeshes.Count));
                    break;
                case V3dSectionType.Dumb:
                    body.Need(ExporterPropPoint.Size, $"{what} (a prop point)");
                    var propName = body.ReadFixedString(ExporterPropPoint.NameSize);
                    int parent = body.ReadInt32();
                    var rot = body.ReadQuaternion();
                    props.Add(new ExporterPropPoint(propName, parent, rot, body.ReadVector3()));
                    break;
                case V3dSectionType.CollisionSphere:
                    body.Need(V3dCollisionSphere.Size, $"{what} (a collision sphere)");
                    var sphereName = body.ReadFixedString(V3dCollisionSphere.NameSize);
                    int bone = body.ReadInt32();
                    var pos = body.ReadVector3();
                    spheres.Add(new V3dCollisionSphere(sphereName, bone, pos, body.ReadSingle(), []));
                    break;
                case V3dSectionType.Bones:
                    if (bones is null) bones = ReadBones(body, size);
                    break;
                case ExporterMeshFile.WeightsSection:
                    if (weights is null) weights = ReadWeights(body);
                    break;
                default:
                    unknown.Add(type);
                    break;
            }
            r.Position = at + 8 + size;
        }

        var file = new ExporterMeshFile
        {
            Header = header,
            Submeshes = submeshes.ToImmutable(),
            PropPoints = props.ToImmutable(),
            CollisionSpheres = spheres.ToImmutable(),
            Bones = bones ?? [],
            Weights = weights ?? [],
            UnknownSections = unknown.ToImmutable(),
        };
        if (file.Submeshes.Length == 0) throw new AssetFormatException($"'{name}' has no submesh.");
        return file;
    }

    private static ImmutableArray<V3dBone> ReadBones(BinaryCursor r, int size)
    {
        int count = r.ReadInt32("bone count");
        if (count < 0 || (long)count * V3dBone.Size > size - 4)
            throw r.Fail($"the bone section declares {count} bones, which do not fit in its {size} bytes.");
        var bones = new V3dBone[count];
        for (int i = 0; i < count; i++)
        {
            var name = r.ReadFixedString(V3dBone.NameSize, "bone name");
            var rot = r.ReadQuaternion();
            var pos = r.ReadVector3();
            bones[i] = new V3dBone(name, rot, pos, r.ReadInt32());
        }
        return [.. bones];
    }

    private static ImmutableArray<V3dBoneLink> ReadWeights(BinaryCursor r)
    {
        int count = r.ReadInt32("weight count");
        r.EnsureCount(count, V3dBoneLink.Size, "the vertex weights");
        var links = new V3dBoneLink[count];
        for (int i = 0; i < count; i++)
        {
            // Four interleaved (weight, bone) byte pairs; bone 0xFF marks an unused slot.
            var b = r.ReadBytes(V3dBoneLink.Size);
            links[i] = new V3dBoneLink(b[0], b[2], b[4], b[6], b[1], b[3], b[5], b[7]);
        }
        return [.. links];
    }

    private static ExporterSubmesh ReadSubmesh(BinaryCursor r, int sizeField, int index)
    {
        string what = $"submesh {index}";
        var name = r.ReadFixedString(V3dSubmesh.NameSize, what);
        var parent = r.ReadFixedString(V3dSubmesh.NameSize, what);

        int positionCount = r.ReadInt32(what);
        r.EnsureCount(positionCount, 12, $"{what}'s positions");
        var positions = new Vector3[positionCount];
        for (int i = 0; i < positionCount; i++) positions[i] = r.ReadVector3();

        int normalCount = r.ReadInt32(what);
        r.EnsureCount(normalCount, 16, $"{what}'s normals");
        var normals = new ExporterNormal[normalCount];
        for (int i = 0; i < normalCount; i++)
        {
            var n = r.ReadVector3();
            int vertex = r.ReadInt32();
            if ((uint)vertex >= (uint)positionCount)
                throw r.Fail($"{what}'s normal {i} belongs to position {vertex}, but the submesh has {positionCount} positions.");
            normals[i] = new ExporterNormal(n, vertex);
        }

        int materialCount = r.ReadInt32(what);
        r.EnsureCount(materialCount, V3dMaterial.Size, $"{what}'s materials");
        var materials = new V3dMaterial[materialCount];
        for (int i = 0; i < materialCount; i++)
        {
            var diffuse = r.ReadFixedString(V3dMaterial.NameSize);
            float emissive = r.ReadSingle(), specular = r.ReadSingle(), gloss = r.ReadSingle(), reflection = r.ReadSingle();
            var reflectionMap = r.ReadFixedString(V3dMaterial.NameSize);
            materials[i] = new V3dMaterial(diffuse, emissive, specular, gloss, reflection, reflectionMap, r.ReadUInt32());
        }

        int faceCount = r.ReadInt32(what);
        r.EnsureCount(faceCount, ExporterFace.Size, $"{what}'s faces");
        var faces = new ExporterFace[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            int a = r.ReadInt32(), b = r.ReadInt32(), c = r.ReadInt32();
            int na = r.ReadInt32(), nb = r.ReadInt32(), nc = r.ReadInt32();
            var ua = r.ReadVector2();
            var ub = r.ReadVector2();
            var uc = r.ReadVector2();
            int material = r.ReadInt32();
            if ((uint)a >= (uint)positionCount || (uint)b >= (uint)positionCount || (uint)c >= (uint)positionCount)
                throw r.Fail($"{what}'s face {i} uses position {Math.Max(a, Math.Max(b, c))}, but the submesh has {positionCount}.");
            if ((uint)na >= (uint)normalCount || (uint)nb >= (uint)normalCount || (uint)nc >= (uint)normalCount)
                throw r.Fail($"{what}'s face {i} uses normal {Math.Max(na, Math.Max(nb, nc))}, but the submesh has {normalCount}.");
            if ((uint)material >= (uint)materialCount)
                throw r.Fail($"{what}'s face {i} uses material {material}, but the submesh has {materialCount}.");
            faces[i] = new ExporterFace(a, b, c, na, nb, nc, ua, ub, uc, material);
        }

        var center = r.ReadVector3(what);
        float radius = r.ReadSingle(what);
        var min = r.ReadVector3(what);
        var max = r.ReadVector3(what);

        int lodCount = r.ReadInt32(what);
        r.EnsureCount(lodCount, ExporterLodLink.Size, $"{what}'s LOD list");
        var lods = new ExporterLodLink[lodCount];
        for (int i = 0; i < lodCount; i++)
        {
            var lodName = r.ReadFixedString(V3dSubmesh.NameSize);
            lods[i] = new ExporterLodLink(lodName, r.ReadSingle());
        }
        if (r.Position != sizeField)
            throw r.Fail($"{what} is {sizeField} bytes but its contents end at {r.Position}.");

        return new ExporterSubmesh
        {
            SizeField = sizeField,
            Name = name,
            ParentName = parent,
            Positions = [.. positions],
            Normals = [.. normals],
            Materials = [.. materials],
            Faces = [.. faces],
            Center = center,
            Radius = radius,
            AabbMin = min,
            AabbMax = max,
            Lods = [.. lods],
        };
    }
}
