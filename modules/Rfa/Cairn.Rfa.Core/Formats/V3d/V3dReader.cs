using System.Collections.Immutable;
using System.Numerics;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>
/// Reads .v3c and .v3m files into a <see cref="V3dFile"/>. SUBM sections carry no usable size, so
/// every submesh is parsed field by field, including each LOD's data block (whose alignment padding
/// is relative to the block's start). Every count, size and offset is checked against the bytes
/// actually present before anything is allocated; bad input raises <see cref="AssetFormatException"/>.
/// </summary>
public static class V3dReader
{
    /// <summary>Reads a mesh from disk.</summary>
    /// <exception cref="AssetFormatException">The file is not a readable V3D mesh.</exception>
    /// <exception cref="IOException">The file could not be opened.</exception>
    public static V3dFile ReadFile(string path) => Read(FileBytes.ReadFile(path), Path.GetFileName(path));

    /// <summary>Reads a mesh from a stream (for example a VPP entry).</summary>
    public static V3dFile Read(Stream stream, string name) => Read(FileBytes.Read(stream, name), name);

    /// <summary>Reads a mesh from its file image.</summary>
    /// <param name="data">The whole file.</param>
    /// <param name="name">The file name, for messages.</param>
    public static V3dFile Read(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        var r = new BinaryCursor(data, name);
        var header = ReadHeader(r);

        var sections = ImmutableArray.CreateBuilder<V3dSection>();
        int endSize;
        while (true)
        {
            int at = r.Position;
            if (r.Remaining < 8)
                throw r.Fail($"the section list ends at offset {at} without an END section.");
            int type = r.ReadInt32();
            int size = r.ReadInt32();
            if (type == V3dSectionType.End)
            {
                endSize = size;
                break;
            }
            sections.Add(type switch
            {
                V3dSectionType.Submesh => ReadSubmesh(r, size, sections.Count),
                V3dSectionType.CollisionSphere => ReadSphere(r, size),
                V3dSectionType.Bones => ReadBones(r, size),
                V3dSectionType.Dumb => new V3dDumbSection(ReadBody(r, size, "DUMB section")),
                _ => new V3dUnknownSection(type, ReadBody(r, size, $"section 0x{type:X8} at offset {at}")),
            });
        }

        return new V3dFile
        {
            Header = header,
            Sections = sections.ToImmutable(),
            EndSizeField = endSize,
            TrailingBytes = [.. r.ReadBytes(r.Remaining)],
        };
    }

    internal static V3dHeader ReadHeader(BinaryCursor r)
    {
        if (r.Length < V3dHeader.Size)
            throw new AssetFormatException($"'{r.Name}' is too short to be a V3D mesh ({r.Length} bytes).");
        r.Position = 0;
        uint signature = r.ReadUInt32();
        if (signature is not (V3dHeader.StaticSignature or V3dHeader.CharacterSignature))
            throw new AssetFormatException(
                $"'{r.Name}' is not a V3D mesh (signature 0x{signature:X8}, expected \"RF3D\" or \"RFCM\").");
        int version = r.ReadInt32();
        if (version != V3dHeader.CurrentVersion)
            throw new AssetFormatException(
                $"'{r.Name}' is V3D version 0x{version:X}; only version 0x{V3dHeader.CurrentVersion:X} is supported.");
        return new V3dHeader(signature, version, r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32(),
            r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
    }

    private static ImmutableArray<byte> ReadBody(BinaryCursor r, int size, string what)
    {
        if (size < 0) throw r.Fail($"{what} has a negative size ({size}).");
        return [.. r.ReadBytes(size, what)];
    }

    private static V3dCollisionSphere ReadSphere(BinaryCursor r, int size)
    {
        if (size < V3dCollisionSphere.Size)
            throw r.Fail($"a collision sphere section is {size} bytes, smaller than the {V3dCollisionSphere.Size} it needs.");
        r.Need(size, "collision sphere section");
        var name = r.ReadFixedString(V3dCollisionSphere.NameSize);
        int bone = r.ReadInt32();
        var pos = r.ReadVector3();
        float radius = r.ReadSingle();
        var extra = r.ReadBytes(size - V3dCollisionSphere.Size);
        return new V3dCollisionSphere(name, bone, pos, radius, [.. extra]);
    }

    private static V3dBoneSection ReadBones(BinaryCursor r, int size)
    {
        if (size < 4) throw r.Fail($"the bone section is {size} bytes, too small for its count.");
        r.Need(size, "bone section");
        int count = r.ReadInt32("bone count");
        if (count < 0 || (long)count * V3dBone.Size > size - 4)
            throw r.Fail($"the bone section declares {count} bones, which do not fit in its {size} bytes.");
        var bones = new V3dBone[count];
        for (int i = 0; i < count; i++)
        {
            var name = r.ReadFixedString(V3dBone.NameSize, "bone name");
            var rot = r.ReadQuaternion();
            var pos = r.ReadVector3();
            int parent = r.ReadInt32();
            bones[i] = new V3dBone(name, rot, pos, parent);
        }
        var extra = r.ReadBytes(size - 4 - count * V3dBone.Size);
        return new V3dBoneSection([.. bones], [.. extra]);
    }

    private static V3dSubmesh ReadSubmesh(BinaryCursor r, int sizeField, int index)
    {
        string what = $"submesh {index}";
        var name = r.ReadFixedString(V3dSubmesh.NameSize, what);
        var parent = r.ReadFixedString(V3dSubmesh.NameSize, what);
        int version = r.ReadInt32(what);
        int lodCount = r.ReadInt32(what);
        r.EnsureCount(lodCount, 4, $"{what}'s LOD distances");
        var distances = new float[lodCount];
        for (int i = 0; i < lodCount; i++) distances[i] = r.ReadSingle();
        var offset = r.ReadVector3(what);
        float radius = r.ReadSingle(what);
        var min = r.ReadVector3(what);
        var max = r.ReadVector3(what);

        // Each LOD needs at least its 22 fixed bytes; prove they fit before sizing the array.
        r.EnsureCount(lodCount, 22, $"{what}'s LODs");
        var lods = new V3dLod[lodCount];
        for (int i = 0; i < lodCount; i++) lods[i] = ReadLod(r, $"{what} LOD {i}");

        int materialCount = r.ReadInt32(what);
        r.EnsureCount(materialCount, V3dMaterial.Size, $"{what}'s materials");
        var materials = new V3dMaterial[materialCount];
        for (int i = 0; i < materialCount; i++)
        {
            var diffuse = r.ReadFixedString(V3dMaterial.NameSize);
            float emissive = r.ReadSingle(), u0 = r.ReadSingle(), u1 = r.ReadSingle(), refCof = r.ReadSingle();
            var refMap = r.ReadFixedString(V3dMaterial.NameSize);
            uint flags = r.ReadUInt32();
            materials[i] = new V3dMaterial(diffuse, emissive, u0, u1, refCof, refMap, flags);
        }

        int trailerCount = r.ReadInt32(what);
        r.EnsureCount(trailerCount, V3dSubmeshTrailer.Size, $"{what}'s trailing list");
        var trailers = new V3dSubmeshTrailer[trailerCount];
        for (int i = 0; i < trailerCount; i++)
        {
            var trailerName = r.ReadFixedString(V3dSubmesh.NameSize);
            trailers[i] = new V3dSubmeshTrailer(trailerName, r.ReadSingle());
        }

        return new V3dSubmesh
        {
            SizeField = sizeField,
            Name = name,
            ParentName = parent,
            Version = version,
            LodDistances = [.. distances],
            Offset = offset,
            Radius = radius,
            AabbMin = min,
            AabbMax = max,
            Lods = [.. lods],
            Materials = [.. materials],
            Trailers = [.. trailers],
        };
    }

    private readonly record struct BatchInfo(
        ushort Vertices, ushort Triangles, V3dBatchSizes Sizes, uint RenderFlags);

    private static V3dLod ReadLod(BinaryCursor r, string what)
    {
        uint flags = r.ReadUInt32(what);
        int vertexCount = r.ReadInt32(what);
        ushort batchCount = r.ReadUInt16(what);
        int dataSize = r.ReadInt32(what);
        if (vertexCount < 0) throw r.Fail($"{what} declares {vertexCount} vertices.");
        if (dataSize < 0) throw r.Fail($"{what} declares a data block of {dataSize} bytes.");
        var block = r.ReadBytes(dataSize, $"{what}'s data block").ToArray();
        int unknown1 = r.ReadInt32(what);

        r.EnsureCount(batchCount, 18, $"{what}'s batch info");
        var infos = new BatchInfo[batchCount];
        for (int i = 0; i < batchCount; i++)
        {
            ushort nv = r.ReadUInt16(), nt = r.ReadUInt16();
            ushort posBytes = r.ReadUInt16(), triBytes = r.ReadUInt16(), sameBytes = r.ReadUInt16();
            ushort linkBytes = r.ReadUInt16(), uvBytes = r.ReadUInt16();
            uint renderFlags = r.ReadUInt32();
            infos[i] = new BatchInfo(nv, nt, new V3dBatchSizes(posBytes, triBytes, sameBytes, linkBytes, uvBytes), renderFlags);
        }

        int propCount = r.ReadInt32(what);
        int textureCount = r.ReadInt32(what);
        if (propCount < 0) throw r.Fail($"{what} declares {propCount} prop points.");
        r.EnsureCount(textureCount, 2, $"{what}'s texture list");
        var textures = new V3dLodTexture[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            byte id = r.ReadByte();
            textures[i] = new V3dLodTexture(id, r.ReadCString($"{what} texture {i}'s name"));
        }

        var (batches, props) = ReadDataBlock(new BinaryCursor(block, r.Name), what, flags, vertexCount, infos, propCount);
        return new V3dLod
        {
            Flags = flags,
            VertexCount = vertexCount,
            Batches = batches,
            Unknown1 = unknown1,
            PropPoints = props,
            Textures = [.. textures],
        };
    }

    private static (ImmutableArray<V3dBatch>, ImmutableArray<V3dPropPoint>) ReadDataBlock(
        BinaryCursor b, string what, uint flags, int lodVertexCount, BatchInfo[] infos, int propCount)
    {
        string blockWhat = $"{what}'s data block";
        b.EnsureCount(infos.Length, V3dBatch.HeaderSize, $"{blockWhat} batch headers");
        var headers = new (byte[] R0, int Texture, byte[] R1)[infos.Length];
        for (int i = 0; i < infos.Length; i++)
        {
            var r0 = b.ReadBytes(0x20).ToArray();
            int texture = b.ReadInt32();
            var r1 = b.ReadBytes(0x14).ToArray();
            headers[i] = (r0, texture, r1);
        }
        Align(b, blockWhat);

        var batches = ImmutableArray.CreateBuilder<V3dBatch>(infos.Length);
        for (int i = 0; i < infos.Length; i++)
        {
            var info = infos[i];
            string bw = $"{what} batch {i}";
            int nv = info.Vertices, nt = info.Triangles;
            var s = info.Sizes;

            var positions = ReadStream(b, nv, 12, s.PositionsBytes, $"{bw} positions", c => c.ReadVector3());
            var normals = ReadStream(b, nv, 12, s.PositionsBytes, $"{bw} normals", c => c.ReadVector3());
            var uvs = ReadStream(b, nv, 8, s.TexCoordsBytes, $"{bw} UVs", c => c.ReadVector2());
            var triangles = ReadStream(b, nt, V3dTriangle.Size, s.TrianglesBytes, $"{bw} triangles",
                c => new V3dTriangle(c.ReadUInt16(), c.ReadUInt16(), c.ReadUInt16(), c.ReadUInt16()));
            var planes = (flags & V3dLod.FlagTrianglePlanes) != 0
                ? ReadStream(b, nt, V3dPlane.Size, nt * V3dPlane.Size, $"{bw} planes", c => new V3dPlane(c.ReadVector3(), c.ReadSingle()))
                : [];
            var same = ReadStream(b, nv, 2, s.SamePositionOffsetsBytes, $"{bw} same-position offsets", c => c.ReadInt16());
            var links = s.BoneLinksBytes > 0
                ? ReadStream(b, nv, V3dBoneLink.Size, s.BoneLinksBytes, $"{bw} bone links",
                    c => new V3dBoneLink(c.ReadByte(), c.ReadByte(), c.ReadByte(), c.ReadByte(),
                        c.ReadByte(), c.ReadByte(), c.ReadByte(), c.ReadByte()))
                : [];
            var morphMap = (flags & V3dLod.FlagMorphVerticesMap) != 0
                ? ReadStream(b, lodVertexCount, 2, (long)lodVertexCount * 2, $"{bw} morph map", c => c.ReadInt16())
                : [];

            batches.Add(new V3dBatch
            {
                HeaderReserved0 = [.. headers[i].R0],
                TextureIndex = headers[i].Texture,
                HeaderReserved1 = [.. headers[i].R1],
                Positions = positions,
                Normals = normals,
                TexCoords = uvs,
                Triangles = triangles,
                Planes = planes,
                SamePositionOffsets = same,
                BoneLinks = links,
                MorphMap = morphMap,
                Sizes = s,
                RenderFlags = info.RenderFlags,
            });
        }

        // Every stream above ends aligned, so this is a no-op for any file the readers have met; it
        // stays because the format defines a pad before the prop points.
        Align(b, blockWhat);

        b.EnsureCount(propCount, V3dPropPoint.Size, $"{blockWhat} prop points");
        var props = new V3dPropPoint[propCount];
        for (int i = 0; i < propCount; i++)
        {
            var name = b.ReadFixedString(V3dPropPoint.NameSize);
            var rot = b.ReadQuaternion();
            var pos = b.ReadVector3();
            props[i] = new V3dPropPoint(name, rot, pos, b.ReadInt32());
        }
        if (b.Remaining != 0)
            throw b.Fail($"{blockWhat} is {b.Length} bytes but its contents end at {b.Position}.");
        return (batches.MoveToImmutable(), [.. props]);
    }

    private static ImmutableArray<T> ReadStream<T>(
        BinaryCursor b, int count, int elementSize, long declaredBytes, string what, Func<BinaryCursor, T> read)
    {
        long needed = (long)count * elementSize;
        if (declaredBytes < needed)
            throw b.Fail($"{what} declare {declaredBytes} bytes but {count} elements need {needed}.");
        b.Need(declaredBytes, what);
        var items = new T[count];
        for (int i = 0; i < count; i++) items[i] = read(b);
        b.Skip((int)(declaredBytes - needed));
        Align(b, what);
        return [.. items];
    }

    private static void Align(BinaryCursor b, string what)
    {
        int pad = (16 - b.Position % 16) % 16;
        b.Skip(pad, $"{what} alignment padding");
    }
}
