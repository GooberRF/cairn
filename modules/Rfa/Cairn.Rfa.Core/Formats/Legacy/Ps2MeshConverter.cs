using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Turns a parsed <see cref="Ps2MeshFile"/> into a <see cref="V3dMeshDescription"/> for
/// <see cref="V3dBuilder"/>: chunks are merged back into one vertex list per material (copies with the
/// same position, normal, UV and weights become one vertex again), faces get the winding of the source
/// <c>.v3d</c> back, texture names are matched to the submesh's materials, and the parts the PS2 file
/// does not store (submesh names, LOD distances) are made up and reported in the notes.
/// </summary>
internal static class Ps2MeshConverter
{
    /// <summary>The distance step used for LODs, whose distances the PS2 files do not keep.</summary>
    public const float DefaultLodStep = 10f;

    public static LegacyMesh Convert(Ps2MeshFile file, string name)
    {
        var notes = new List<string>();
        string ext = file.IsCharacter ? "rfc" : "rfm";
        var stored = file.Submeshes;
        var chains = LodChains(file, notes);

        string stem = SafeStem(name);
        var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>(chains.Count);
        var stats = new Stats();
        var names = new List<string>();
        for (int s = 0; s < chains.Count; s++)
        {
            string subName = chains.Count == 1 ? Fit(stem, "") : Fit(stem, "_" + (s + 1).ToString(CultureInfo.InvariantCulture));
            names.Add(subName);
            submeshes.Add(BuildSubmesh(file, chains[s], subName, stats));
        }

        if (chains.Count > 0)
        {
            notes.Add(chains.Count == 1
                ? $"PlayStation 2 meshes do not store submesh names; the submesh is named '{names[0]}' after the file."
                : $"PlayStation 2 meshes do not store submesh names; the {chains.Count} submeshes are named '{names[0]}' to '{names[^1]}' after the file.");
        }
        else
        {
            notes.Add("The file holds no geometry.");
        }
        if (stats.StoredCorners > 0)
        {
            notes.Add($"Re-joined vertices: {stats.StoredCorners:N0} face corners in {stats.Chunks:N0} chunks ({stats.StoredVertices:N0} stored vertices, "
                + $"copied into every chunk that uses them; UVs are stored per corner) became {stats.Vertices:N0} vertices by joining corners "
                + (file.IsCharacter ? "with the same position, normal, UV and weights." : "with the same position, normal and UV."));
        }
        if (stats.Flipped > 0)
            notes.Add($"{stats.Flipped:N0} of {stats.Triangles:N0} triangles are stored with the opposite winding; they were turned back using the stored face normal.");
        if (stats.Degenerate > 0)
            notes.Add($"{stats.Degenerate:N0} triangles have no usable face normal; their stored winding is kept.");
        if (stats.AddedMaterials.Count > 0)
            notes.Add($"Textures with no matching material got a plain material: {string.Join(", ", stats.AddedMaterials)}.");
        if (stats.TwoSidedTriangles > 0)
            notes.Add($"{stats.TwoSidedTriangles:N0} triangles use two-sided materials and were made double-sided.");
        if (stats.UnknownMaterialFlags > 0)
            notes.Add($"{stats.UnknownMaterialFlags:N0} materials have flags Cairn does not know; they were kept as stored.");
        if (stats.ResolvedFaces > 0)
            notes.Add($"{stats.ResolvedFaces:N0} faces use a texture that several different materials share; each was given its material by chunk order.");
        if (stats.UnresolvedFaces > 0)
            notes.Add($"{stats.UnresolvedFaces:N0} faces use a texture that several different materials share (for example one two-sided); which one could not be told, so the first was used.");
        if (stats.Chrome + stats.SelfLit > 0)
            notes.Add($"Per-face PlayStation 2 lighting flags are not carried over (chrome on {stats.Chrome:N0} faces, self-illumination on {stats.SelfLit:N0}); the materials keep their own settings.");
        if (file.IsCharacter)
        {
            notes.Add("Bone weights come in steps of 1/16 on the PlayStation 2 (a .v3c uses steps of 1/255), so they can differ from the original model's by up to about 1/25.");
            if (stats.OddWeightSums > 0)
                notes.Add($"{stats.OddWeightSums:N0} vertices have weights that do not add up to 16/16; they were normalised.");
            if (stats.BadBones > 0)
                notes.Add($"{stats.BadBones:N0} weights name a bone the skeleton does not have; they were dropped.");
            if (stats.Unweighted > 0)
                notes.Add($"{stats.Unweighted:N0} vertices have no bone weights.");
            if (stats.MissingSources > 0)
                notes.Add($"{stats.MissingSources:N0} source vertices are used by no chunk; their morph slots are left empty.");
        }
        foreach (var (type, length) in file.UnknownSections)
            notes.Add($"Skipped an unknown section 0x{type:X8} ({length:N0} bytes).");
        if (file.TrailingBytes > 0)
            notes.Add($"Ignored {file.TrailingBytes:N0} bytes after the end of the mesh.");
        if (stored.Any(s => s.UnusedDataBytes > 0))
            notes.Add("A submesh data block has unused bytes at its end; they were ignored.");
        if (stored.Any(s => !s.HasMaterialsSection))
            notes.Add("A submesh has no materials section; its materials were made from its texture names.");
        if (file.Props.Length > 0)
            notes.Add($"Kept {file.Props.Length} prop points.");
        if (file.Spheres.Length > 0)
            notes.Add($"Kept {file.Spheres.Length} collision spheres.");
        if (file.Spheres.Length != file.DeclaredCounts.Spheres || file.Props.Length != file.DeclaredCounts.Props)
            notes.Add($"The header declares {file.DeclaredCounts.Spheres} collision spheres and {file.DeclaredCounts.Props} prop points; the file holds {file.Spheres.Length} and {file.Props.Length}.");

        var description = new V3dMeshDescription
        {
            Kind = file.IsCharacter ? V3dKind.Character : V3dKind.StaticMesh,
            Submeshes = submeshes.MoveToImmutable(),
            Bones = file.Bones,
            CollisionSpheres = file.Spheres,
            PropPoints = [.. file.Props.Select(p => new V3dPropPoint(PropName(p.Name), p.Rotation, p.Position, p.Parent))],
        };
        return new LegacyMesh(ext, description, [.. notes]);
    }

    /// <summary>
    /// Groups the stored submeshes into LOD chains. The header counts submeshes without their LODs,
    /// and the file stores each submesh followed by its lower levels; the distances themselves are
    /// not kept (every sample stores the float maximum).
    /// </summary>
    private static List<List<Ps2Submesh>> LodChains(Ps2MeshFile file, List<string> notes)
    {
        var stored = file.Submeshes;
        int m = stored.Length, n = file.SubmeshCount;
        var chains = new List<List<Ps2Submesh>>();
        int per = 1;
        if (n > 0 && n < m)
        {
            if (m % n == 0 && m / n <= V3dBuilder.MaxLods) per = m / n;
            else notes.Add($"The header declares {n} submeshes but the file stores {m}, which do not split evenly into levels of detail; all {m} are kept as separate submeshes.");
        }
        else if (n != m)
        {
            notes.Add($"The header declares {n} submeshes; the file stores {m}. All stored submeshes are kept.");
        }
        for (int i = 0; i < m; i += per) chains.Add([.. stored.Skip(i).Take(per)]);
        if (per > 1)
        {
            string distances = string.Join(", ", Enumerable.Range(1, per - 1).Select(k => (k * DefaultLodStep).ToString("0.#", CultureInfo.InvariantCulture) + " m"));
            notes.Add(n == 1
                ? $"The mesh has {per} levels of detail. PlayStation 2 meshes do not store their switch distances; Cairn used {distances}."
                : $"Each submesh has {per} levels of detail. PlayStation 2 meshes do not store their switch distances; Cairn used {distances}.");
        }
        return chains;
    }

    internal sealed class Stats
    {
        public int UnknownMaterialFlags, TwoSidedTriangles;
        public int Chunks, StoredVertices, StoredCorners, Vertices, Triangles, Flipped, Degenerate;
        public int Chrome, SelfLit, OddWeightSums, BadBones, Unweighted, MissingSources, ResolvedFaces, UnresolvedFaces;
        public readonly List<string> AddedMaterials = [];
    }

    private static V3dSubmeshDescription BuildSubmesh(Ps2MeshFile file, List<Ps2Submesh> chain, string name, Stats stats)
    {
        // Materials: the first level's list, plus any record a lower level adds.
        var materials = new List<V3dMaterial>(chain[0].Materials);
        var lods = ImmutableArray.CreateBuilder<V3dLodDescription>(chain.Count);
        for (int l = 0; l < chain.Count; l++)
        {
            var sub = chain[l];
            var textureToMaterials = new int[sub.TextureNames.Length][];
            for (int t = 0; t < textureToMaterials.Length; t++)
                textureToMaterials[t] = MaterialsFor(sub, sub.TextureNames[t], materials, stats);
            lods.Add(BuildLod(file, sub, l, ChunkMaterials(sub, textureToMaterials, stats), materials, stats));
        }
        return new V3dSubmeshDescription
        {
            Name = FixedString.FromText(name, V3dSubmesh.NameSize),
            // The PC compiler writes the source's bounding centre as the submesh offset; the PS2 file
            // keeps that centre (all six .v3m twins on the demo disc match it).
            Offset = chain[0].Centre,
            Lods = lods.MoveToImmutable(),
            Materials = [.. materials.Select(m => m with { Flags = CompiledMaterialFlags(m.Flags, stats) })],
        };
    }

    /// <summary>Source material flag: two-sided (the compiled mesh sets 0x10 and double-sided triangles).</summary>
    public const uint SourceTwoSided = 0x1;

    /// <summary>
    /// The PS2 materials keep the exporter's flags (0, 1 or 2 in the samples); compiled meshes set bit
    /// 0x1 in every material and 0x10 in two-sided ones (all six .v3m twins and every stock file). Other
    /// bits are kept as they are, the same rule the .v3d / .vcm conversion uses (the engine does not read
    /// material flags; only the triangles' double-sided flag matters).
    /// </summary>
    internal static uint CompiledMaterialFlags(uint source, Stats? stats = null)
    {
        if ((source & ~0x3u) != 0 && stats is not null) stats.UnknownMaterialFlags++;
        return 0x1u | ((source & SourceTwoSided) != 0 ? 0x10u : 0u) | (source & ~SourceTwoSided);
    }

    /// <summary>
    /// The submesh materials whose diffuse map is <paramref name="texture"/> (case-insensitive), one per
    /// distinct record, in material order. Faces only name a texture, so a texture that several
    /// different materials share (one of them two-sided, say) needs <see cref="ChunkMaterials"/>.
    /// </summary>
    private static int[] MaterialsFor(Ps2Submesh sub, string texture, List<V3dMaterial> materials, Stats stats)
    {
        var found = new List<int>();
        for (int i = 0; i < materials.Count; i++)
        {
            if (!string.Equals(materials[i].DiffuseMap.Text, texture, StringComparison.OrdinalIgnoreCase)) continue;
            if (!found.Any(f => materials[f] == materials[i])) found.Add(i);
        }
        if (found.Count > 0) return [.. found];

        // Not in the first level's materials: take this level's record with that name, else make one.
        var own = sub.Materials.FirstOrDefault(m => string.Equals(m.DiffuseMap.Text, texture, StringComparison.OrdinalIgnoreCase));
        if (own is null)
        {
            string text = new([.. texture.Select(c => c == 0 || c > 255 ? '_' : c)]);
            if (text.Length > V3dMaterial.NameSize - 1) text = text[..(V3dMaterial.NameSize - 1)];
            own = new V3dMaterial(FixedString.FromText(text, V3dMaterial.NameSize), 0f, 0f, 0f, 0f, FixedString.FromText("", V3dMaterial.NameSize), 0);
            stats.AddedMaterials.Add(texture);
        }
        materials.Add(own);
        return [materials.Count - 1];
    }

    /// <summary>
    /// The material of every face, per chunk. The PS2 tool never mixes materials in a chunk and writes
    /// each material's chunks together; materials sharing a texture come in material order (both hold on
    /// every sample with an exporter twin). So for a texture that k different materials share, the k-th
    /// run of chunks drawing only that texture belongs to the k-th of those materials. When the runs do
    /// not line up (a material with no faces, two runs that touch) the faces go to the first material
    /// and are counted.
    /// </summary>
    private static int[][] ChunkMaterials(Ps2Submesh sub, int[][] textureToMaterials, Stats stats)
    {
        var chunks = sub.Chunks;
        var result = new int[chunks.Length][];
        var single = new int[chunks.Length];
        for (int c = 0; c < chunks.Length; c++)
        {
            var faces = chunks[c].Faces;
            single[c] = faces.Length > 0 && faces.All(f => f.Texture == faces[0].Texture) ? faces[0].Texture : -1;
            result[c] = [.. faces.Select(f => textureToMaterials[f.Texture][0])];
        }

        for (int t = 0; t < textureToMaterials.Length; t++)
        {
            var candidates = textureToMaterials[t];
            if (candidates.Length < 2) continue;
            var runs = new List<(int Start, int End)>();
            for (int c = 0; c < chunks.Length; c++)
            {
                if (single[c] != t) continue;
                if (runs.Count > 0 && runs[^1].End == c - 1) runs[^1] = (runs[^1].Start, c);
                else runs.Add((c, c));
            }
            bool mixed = Enumerable.Range(0, chunks.Length).Any(c => single[c] != t && chunks[c].Faces.Any(f => f.Texture == t));
            int faces = chunks.Sum(ch => ch.Faces.Count(f => f.Texture == t));
            if (runs.Count != candidates.Length || mixed)
            {
                stats.UnresolvedFaces += faces;
                continue;
            }
            stats.ResolvedFaces += faces;
            for (int k = 0; k < runs.Count; k++)
            {
                for (int c = runs[k].Start; c <= runs[k].End; c++) Array.Fill(result[c], candidates[k]);
            }
        }
        return result;
    }

    private readonly record struct VertexKey(int Px, int Py, int Pz, int Nx, int Ny, int Nz, int U, int V, ulong Weights)
    {
        public static VertexKey From(Vector3 p, Vector3 n, Vector2 uv, ulong weights) => new(
            Bits(p.X), Bits(p.Y), Bits(p.Z), Bits(n.X), Bits(n.Y), Bits(n.Z), Bits(uv.X), Bits(uv.Y), weights);

        // -0 and +0 are the same value.
        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f + 0f);
    }

    private sealed class Group(int material)
    {
        public int Material { get; } = material;
        public List<V3dMeshVertex> Vertices { get; } = [];
        public Dictionary<VertexKey, int> Index { get; } = [];
        public List<V3dMeshTriangle> Triangles { get; } = [];
    }

    private static V3dLodDescription BuildLod(
        Ps2MeshFile file, Ps2Submesh sub, int level, int[][] faceMaterials, List<V3dMaterial> materials, Stats stats)
    {
        var groups = new Dictionary<int, Group>();
        var order = new List<Group>();
        var influenceCache = new Dictionary<ulong, ImmutableArray<V3dBoneInfluence>>();
        for (int ci = 0; ci < sub.Chunks.Length; ci++)
        {
            var chunk = sub.Chunks[ci];
            stats.Chunks++;
            stats.StoredVertices += chunk.Positions.Length;
            for (int fi = 0; fi < chunk.Faces.Length; fi++)
            {
                var face = chunk.Faces[fi];
                int material = faceMaterials[ci][fi];
                if (!groups.TryGetValue(material, out var g))
                {
                    g = new Group(material);
                    groups.Add(material, g);
                    order.Add(g);
                }
                if (face.Chrome != 0) stats.Chrome++;
                if (face.SelfIllumination != 0) stats.SelfLit++;

                // Stored winding: the face normal says which side is the front.
                int a = face.A, b = face.B, c = face.C;
                var uvA = face.Uv0;
                var uvB = face.Uv1;
                var uvC = face.Uv2;
                var p0 = chunk.Positions[a];
                var cross = Vector3.Cross(chunk.Positions[b] - p0, chunk.Positions[c] - p0);
                float side = Vector3.Dot(cross, face.Normal);
                if (!float.IsFinite(side) || side == 0f)
                {
                    side = Vector3.Dot(cross, chunk.Normals[a] + chunk.Normals[b] + chunk.Normals[c]);
                    if (!float.IsFinite(side) || side == 0f)
                    {
                        stats.Degenerate++;
                        side = 1f;
                    }
                }
                if (side < 0f)
                {
                    (b, c) = (c, b);
                    (uvB, uvC) = (uvC, uvB);
                    stats.Flipped++;
                }

                int ia = Corner(g, chunk, a, uvA);
                int ib = Corner(g, chunk, b, uvB);
                int ic = Corner(g, chunk, c, uvC);
                bool twoSided = (materials[material].Flags & SourceTwoSided) != 0;
                g.Triangles.Add(new V3dMeshTriangle(ia, ib, ic, twoSided ? V3dTriangle.DoubleSided : (ushort)0));
                if (twoSided) stats.TwoSidedTriangles++;
                stats.Triangles++;
                stats.StoredCorners += 3;
            }
        }

        foreach (var g in order) stats.Vertices += g.Vertices.Count;

        ImmutableArray<Vector3>? morph = null;
        int? countOverride = null;
        if (file.IsCharacter)
        {
            // The source vertex order (what RFA morph keys address), from the chunks' vertex maps.
            var originals = new Vector3[sub.SourceVertexCount];
            var known = new bool[originals.Length];
            foreach (var chunk in sub.Chunks)
            {
                for (int o = 0; o < originals.Length && o < chunk.SourceMap.Length; o++)
                {
                    byte local = chunk.SourceMap[o];
                    if (local == 0xFF || known[o]) continue;
                    originals[o] = chunk.Positions[local];
                    known[o] = true;
                }
            }
            for (int o = 0; o < originals.Length; o++)
            {
                if (known[o]) continue;
                originals[o] = new Vector3(float.NaN);
                stats.MissingSources++;
            }
            morph = [.. originals];
        }
        else
        {
            countOverride = sub.SourceVertexCount;
        }

        return new V3dLodDescription
        {
            Distance = level == 0 ? 0f : level * DefaultLodStep,
            Groups = [.. order.OrderBy(g => g.Material).Select(g => new V3dMaterialGroup
            {
                Material = g.Material,
                Vertices = [.. g.Vertices],
                Triangles = [.. g.Triangles],
            })],
            MorphVertices = morph,
            VertexCountOverride = countOverride,
        };

        int Corner(Group g, Ps2Chunk chunk, int local, Vector2 uv)
        {
            ulong weights = file.IsCharacter && local < chunk.Weights.Length ? chunk.Weights[local] : 0UL;
            var key = VertexKey.From(chunk.Positions[local], chunk.Normals[local], uv, weights);
            if (g.Index.TryGetValue(key, out int index)) return index;
            index = g.Vertices.Count;
            g.Index.Add(key, index);
            var influences = file.IsCharacter ? Influences(weights) : [];
            g.Vertices.Add(new V3dMeshVertex(chunk.Positions[local], chunk.Normals[local], uv, influences));
            return index;
        }

        ImmutableArray<V3dBoneInfluence> Influences(ulong packed)
        {
            if (influenceCache.TryGetValue(packed, out var cached)) return cached;
            Span<int> bones = stackalloc int[4];
            Span<int> weights = stackalloc int[4];
            int total = 0, sum16 = 0, count = 0;
            for (int s = 0; s < 4; s++)
            {
                int w = (int)(packed >> (8 * s)) & 0xFF;
                int bone = (int)(packed >> (32 + 8 * s)) & 0xFF;
                if (bone == V3dBoneLink.NoBone || w == 0) continue;
                sum16 += w;
                if (bone >= file.Bones.Length)
                {
                    stats.BadBones++;
                    continue;
                }
                bones[count] = bone;
                weights[count] = w;
                total += w;
                count++;
            }
            if (sum16 != 16) stats.OddWeightSums++;
            if (count == 0) stats.Unweighted++;
            var result = new V3dBoneInfluence[count];
            for (int i = 0; i < count; i++) result[i] = new V3dBoneInfluence(bones[i], weights[i] / (float)total);
            ImmutableArray<V3dBoneInfluence> list = [.. result];
            influenceCache[packed] = list;
            return list;
        }
    }

    private static FixedString PropName(FixedString stored)
    {
        string text = stored.Text;
        return FixedString.FromText(text.Length > V3dPropPoint.NameSize - 1 ? text[..(V3dPropPoint.NameSize - 1)] : text, V3dPropPoint.NameSize);
    }

    /// <summary>The file name without folders or extension, as Latin-1 text a name field can hold.</summary>
    private static string SafeStem(string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name ?? "");
        stem = new string([.. stem.Select(c => c == 0 || c > 255 ? '_' : c)]);
        return stem.Length == 0 ? "mesh" : stem;
    }

    /// <summary>The stem cut so stem + suffix fits a 24-byte name (23 characters).</summary>
    private static string Fit(string stem, string suffix)
    {
        int room = V3dSubmesh.NameSize - 1 - suffix.Length;
        return (stem.Length > room ? stem[..room] : stem) + suffix;
    }
}
