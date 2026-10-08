using System.Collections.Immutable;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>The facts the library needs about a mesh, read without decoding any geometry.</summary>
/// <param name="Kind">Static mesh or character.</param>
/// <param name="SubmeshNames">Names of the SUBM sections found, in order.</param>
/// <param name="LodCounts">LOD count of each submesh, in order.</param>
/// <param name="BoneNames">Bone names in index order (empty for a mesh without bones).</param>
/// <param name="BoneParents">Parent index of each bone.</param>
/// <param name="CollisionSphereCount">Number of CSPH sections found.</param>
/// <param name="StructureReadable">
/// False when the section walk failed part way and the bones were found by scanning for the BONE
/// signature instead (the counts above then cover only what was walked).
/// </param>
public sealed record V3dProbeResult(
    V3dKind Kind,
    ImmutableArray<string> SubmeshNames,
    ImmutableArray<int> LodCounts,
    ImmutableArray<string> BoneNames,
    ImmutableArray<int> BoneParents,
    int CollisionSphereCount,
    bool StructureReadable)
{
    /// <summary>Number of bones.</summary>
    public int BoneCount => BoneNames.Length;

    /// <summary>Number of submeshes walked.</summary>
    public int SubmeshCount => SubmeshNames.Length;

    /// <summary>Triangles of each submesh's first (most detailed) LOD, from its batch headers, in order.</summary>
    public ImmutableArray<int> TriangleCounts { get; init; } = [];

    /// <summary>
    /// "v3d" or "vcm" when the file is an uncompiled exporter mesh (the same header as a compiled one), whatever
    /// its name; null for a compiled .v3m/.v3c. The facts above then describe what converting it would make.
    /// </summary>
    public string? ExporterFormat { get; init; }
}

/// <summary>
/// Cheap mesh probe for the library: walks the section list, stepping over every LOD data block by
/// its size instead of decoding it, and reads the bone names. If the walk fails, the BONE section is
/// located by scanning for its on-disk signature "ENOB" and accepting the single hit whose size field
/// equals <c>4 + 56 * count</c> (the method of <c>rfanim.py: read_v3c_skeleton</c>).
/// </summary>
public static class V3dProbe
{
    /// <summary>Probes a file on disk.</summary>
    /// <exception cref="AssetFormatException">The file is not a V3D mesh.</exception>
    public static V3dProbeResult ProbeFile(string path) => Probe(FileBytes.ReadFile(path), Path.GetFileName(path));

    /// <summary>Probes a stream.</summary>
    public static V3dProbeResult Probe(Stream stream, string name) => Probe(FileBytes.Read(stream, name), name);

    /// <summary>Probes an in-memory file.</summary>
    public static V3dProbeResult Probe(byte[] data, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (Legacy.ExporterMeshReader.IsExporterLayout(data)) return ProbeExporter(data, name);
        var r = new BinaryCursor(data, name);
        var header = V3dReader.ReadHeader(r);

        var submeshes = new List<string>();
        var lodCounts = new List<int>();
        var triangles = new List<int>();
        var boneNames = ImmutableArray<string>.Empty;
        var boneParents = ImmutableArray<int>.Empty;
        bool bonesFound = false;
        int spheres = 0;
        try
        {
            while (true)
            {
                int type = r.ReadInt32("section type");
                int size = r.ReadInt32("section size");
                if (type == V3dSectionType.End) break;
                if (type == V3dSectionType.Submesh)
                {
                    var (subName, lods, tris) = SkipSubmesh(r);
                    submeshes.Add(subName);
                    lodCounts.Add(lods);
                    triangles.Add(tris);
                    continue;
                }
                // spheres and bones occupy their whole record even when the size field says less (as the game reads them)
                if (V3dReader.EffectiveRecordSize(r, type, size) is { } whole) size = whole;
                if (size < 0) throw r.Fail("a section has a negative size.");
                int bodyStart = r.Position;
                r.Need(size, "section");
                if (type == V3dSectionType.CollisionSphere) spheres++;
                if (type == V3dSectionType.Bones && !bonesFound)
                {
                    (boneNames, boneParents) = ReadBones(r, size);
                    bonesFound = true;
                }
                r.Position = bodyStart + size;
            }
        }
        catch (AssetFormatException)
        {
            if (!bonesFound) (boneNames, boneParents) = ScanBones(data);
            return new V3dProbeResult(header.Kind, [.. submeshes], [.. lodCounts], boneNames, boneParents, spheres, false) { TriangleCounts = [.. triangles] };
        }
        return new V3dProbeResult(header.Kind, [.. submeshes], [.. lodCounts], boneNames, boneParents, spheres, true) { TriangleCounts = [.. triangles] };
    }

    /// <summary>An exporter mesh (.v3d/.vcm layout): the facts of the mesh converting it makes.</summary>
    private static V3dProbeResult ProbeExporter(byte[] data, string name)
    {
        var file = Legacy.ExporterMeshReader.Read(data, name);
        string format = file.Kind == V3dKind.Character ? "vcm" : "v3d";
        var d = Legacy.ExporterMeshConverter.Convert(file, name, format).Description;
        var bones = d.Bones;
        return new V3dProbeResult(d.Kind,
            [.. d.Submeshes.Select(s => s.Name.Text)],
            [.. d.Submeshes.Select(s => s.Lods.Length)],
            [.. bones.Select(b => b.Name.Text)],
            [.. bones.Select(b => b.ParentIndex)],
            d.CollisionSpheres.Length,
            true)
        {
            TriangleCounts = [.. d.Submeshes.Select(s => s.Lods.IsDefaultOrEmpty ? 0 : s.Lods[0].Groups.Sum(g => g.Triangles.Length))],
            ExporterFormat = format,
        };
    }

    private static (ImmutableArray<string>, ImmutableArray<int>) ReadBones(BinaryCursor r, int size)
    {
        int count = r.ReadInt32("bone count");
        if (count < 0 || (long)count * V3dBone.Size > size - 4) throw r.Fail("the bone count does not fit its section.");
        var names = new string[count];
        var parents = new int[count];
        for (int i = 0; i < count; i++)
        {
            names[i] = r.ReadFixedString(V3dBone.NameSize).Text;
            r.Skip(28);
            parents[i] = r.ReadInt32();
        }
        return ([.. names], [.. parents]);
    }

    private static (string Name, int Lods, int Triangles) SkipSubmesh(BinaryCursor r)
    {
        string name = r.ReadFixedString(V3dSubmesh.NameSize).Text;
        r.Skip(V3dSubmesh.NameSize + 4);
        int lods = r.ReadInt32("LOD count");
        r.EnsureCount(lods, 4, "LOD distances");
        r.Skip(lods * 4 + 40);
        int triangles = 0;
        for (int i = 0; i < lods; i++)
        {
            r.Skip(8);
            ushort batches = r.ReadUInt16();
            int dataSize = r.ReadInt32();
            if (dataSize < 0) throw r.Fail("a LOD data block has a negative size.");
            r.Skip(dataSize);
            r.Skip(4);
            r.EnsureCount(batches, 18, "batch info");
            for (int b = 0; b < batches; b++)
            {
                // batch info: vertex count, triangle count, then sizes and render flags (18 bytes)
                r.Skip(2);
                int batchTriangles = r.ReadUInt16();
                if (i == 0) triangles += batchTriangles;
                r.Skip(14);
            }
            r.Skip(4);
            int textures = r.ReadInt32();
            r.EnsureCount(textures, 2, "texture list");
            for (int t = 0; t < textures; t++)
            {
                r.Skip(1);
                r.ReadCString();
            }
        }
        int materials = r.ReadInt32();
        r.EnsureCount(materials, V3dMaterial.Size, "materials");
        r.Skip(materials * V3dMaterial.Size);
        int trailers = r.ReadInt32();
        r.EnsureCount(trailers, V3dSubmeshTrailer.Size, "trailing list");
        r.Skip(trailers * V3dSubmeshTrailer.Size);
        return (name, lods, triangles);
    }

    private static (ImmutableArray<string>, ImmutableArray<int>) ScanBones(byte[] data)
    {
        ReadOnlySpan<byte> tag = "ENOB"u8;
        int found = -1, hits = 0;
        var span = data.AsSpan();
        for (int i = span.IndexOf(tag); i >= 0 && i + 12 <= data.Length;)
        {
            int size = BitConverter.ToInt32(data, i + 4);
            int count = BitConverter.ToInt32(data, i + 8);
            if (count is >= 1 and <= V3dBoneSection.MaxBones && size == 4 + count * V3dBone.Size
                && i + 8 + size <= data.Length)
            {
                found = i;
                hits++;
            }
            int next = span[(i + 1)..].IndexOf(tag);
            i = next < 0 ? -1 : i + 1 + next;
        }
        if (hits != 1) return ([], []);
        var r = new BinaryCursor(data, "") { Position = found + 8 };
        return ReadBones(r, BitConverter.ToInt32(data, found + 4));
    }
}
