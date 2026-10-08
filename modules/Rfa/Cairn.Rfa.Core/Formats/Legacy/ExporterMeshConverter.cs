using System.Collections.Immutable;
using System.Numerics;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Formats.Legacy;

/// <summary>
/// Turns an <see cref="ExporterMeshFile"/> into the description <see cref="V3dBuilder"/> compiles, following
/// what the game's own mesh compiler did (measured on the stock meshes built from the same exporter files):
/// <list type="bullet">
/// <item>a SUBM named in another SUBM's LOD list is that submesh's lower level of detail, not a submesh of its
/// own; the others are the submeshes, each with its LOD list's distances;</item>
/// <item>a static submesh's offset is the SUBM's bounding centre (a character's is zero); positions are kept;</item>
/// <item>normals are smooth: per position, the normalised sum of the unit normals of the faces around it (the
/// exporter's own normals are not used; some files hold uninitialised memory there);</item>
/// <item>each triangle's UVs move by whole tiles so their smallest U and V lie in 0..1;</item>
/// <item>one material group per texture (materials naming the same texture share the first one's batch), in
/// material order; a vertex per distinct (position, UV) corner, corners of one position whose UVs differ by
/// less than <see cref="UvWeldTolerance"/> sharing the first one's vertex; double-sided materials make
/// double-sided triangles;</item>
/// <item>a static LOD's declared vertex count is its position count, and a character LOD's morph vertices are
/// its positions in order (what animations' morph keys address);</item>
/// <item>prop points (DUMB) go in every LOD; collision spheres are copied; bones keep their pose with lower-case
/// names and rotations in the sign a matrix-to-quaternion round trip gives; a character's WAIT weights become
/// each vertex's bone links byte for byte.</item>
/// </list>
/// </summary>
public static class ExporterMeshConverter
{
    /// <summary>Exporter material flag: the material is double-sided.</summary>
    public const uint ExporterTwoSided = 0x1;

    /// <summary>Compiled material flags: bit 0 is set in every stock material.</summary>
    public const uint CompiledBaseFlags = 0x1;

    /// <summary>Compiled material flag of a double-sided material.</summary>
    public const uint CompiledTwoSided = 0x10;

    /// <summary>Exporter material flag that the compiler turned into <see cref="CompiledAlpha"/>.</summary>
    public const uint ExporterAlpha = 0x2;

    /// <summary>
    /// Compiled material flag of a see-through texture. The compiler also set it when the texture file itself
    /// has alpha, which a conversion cannot see; the engine does not read material flags.
    /// </summary>
    public const uint CompiledAlpha = 0x8;

    /// <summary>
    /// Corners of one position whose U and V both differ by less than this share a vertex, as in the stock
    /// meshes (every pair closer than 0.029 was joined there, none 0.030 or more apart in a mesh built from the
    /// same exporter file).
    /// </summary>
    public const float UvWeldTolerance = 0.03f;

    /// <summary>Converts a read file. Never throws for a file the reader accepted.</summary>
    /// <param name="file">The exporter mesh.</param>
    /// <param name="name">The file name, for notes.</param>
    /// <param name="sourceFormat">The source extension without the dot ("v3d" or "vcm").</param>
    public static LegacyMesh Convert(ExporterMeshFile file, string name, string sourceFormat)
    {
        ArgumentNullException.ThrowIfNull(file);
        var notes = new List<string>();
        var kind = file.Kind;
        bool character = kind == V3dKind.Character;

        // Which SUBM sections are lower levels of detail of another.
        var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < file.Submeshes.Length; i++) byName.TryAdd(file.Submeshes[i].Name.Text, i);
        var isLod = new bool[file.Submeshes.Length];
        for (int i = 0; i < file.Submeshes.Length; i++)
        {
            foreach (var link in file.Submeshes[i].Lods)
            {
                if (byName.TryGetValue(link.Name.Text, out int j) && j != i) isLod[j] = true;
            }
        }
        if (isLod.All(x => x)) Array.Fill(isLod, false); // a cycle: keep every SUBM as a submesh

        // A character's weights run over the positions of every SUBM in file order.
        var weightStart = new int[file.Submeshes.Length];
        int totalPositions = 0;
        for (int i = 0; i < file.Submeshes.Length; i++)
        {
            weightStart[i] = totalPositions;
            totalPositions += file.Submeshes[i].Positions.Length;
        }
        bool useWeights = character && file.Weights.Length > 0;
        if (character && file.Weights.Length == 0) notes.Add("The character mesh has no vertex weights; every vertex was left unweighted.");
        else if (useWeights && file.Weights.Length != totalPositions)
            notes.Add($"The vertex weights list {file.Weights.Length} vertices but the mesh has {totalPositions}; vertices without an entry were left unweighted.");
        if (character && file.Bones.Length == 0) notes.Add("The character mesh has no bones.");

        var stats = new LodStats();
        var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>();
        for (int i = 0; i < file.Submeshes.Length; i++)
        {
            if (isLod[i]) continue;
            var top = file.Submeshes[i];
            var materials = top.Materials.Select(CompiledMaterial).ToList();
            var levels = new List<(ExporterSubmesh Sub, int Index, float Distance)> { (top, i, 0f) };
            foreach (var link in top.Lods)
            {
                if (!byName.TryGetValue(link.Name.Text, out int j) || j == i)
                {
                    notes.Add($"Submesh '{top.Name.Text}' names '{link.Name.Text}' as a level of detail, but the file has no such submesh; it was left out.");
                    continue;
                }
                levels.Add((file.Submeshes[j], j, link.Distance));
            }
            if (levels.Count > V3dBuilder.MaxLods)
            {
                notes.Add($"Submesh '{top.Name.Text}' has {levels.Count} levels of detail; the game shows at most {V3dBuilder.MaxLods}, so the rest were left out.");
                levels.RemoveRange(V3dBuilder.MaxLods, levels.Count - V3dBuilder.MaxLods);
            }

            var lods = ImmutableArray.CreateBuilder<V3dLodDescription>(levels.Count);
            foreach (var (sub, index, distance) in levels)
                lods.Add(BuildLod(sub, distance, character, materials, useWeights ? file.Weights : [], weightStart[index], stats));

            submeshes.Add(new V3dSubmeshDescription
            {
                Name = top.Name,
                ParentName = top.ParentName,
                Offset = character ? Vector3.Zero : top.Center,
                Lods = lods.MoveToImmutable(),
                Materials = [.. materials],
            });
        }

        if (stats.Garbage.Count > 0)
            notes.Add($"{Names(stats.Garbage)} had no usable normals; smooth normals were computed from the faces.");
        if (stats.Smoothed.Count > 0)
            notes.Add($"The hard edges of {Names(stats.Smoothed)} were smoothed: like the game's mesh compiler, the conversion gives each position one smooth normal.");
        if (stats.Welded > 0)
            notes.Add($"{Count(stats.Welded, "corner")} whose UVs differed from a neighbour's by less than {UvWeldTolerance:0.##} were joined with it, as the game's mesh compiler does (UVs moved by up to {stats.WeldMax:0.###}).");
        if (file.UnknownSections.Length > 0)
            notes.Add($"{Count(file.UnknownSections.Length, "section")} of unknown type (" + string.Join(", ", file.UnknownSections.Distinct().Select(t => $"0x{t:X8}")) + ") were left out.");

        var description = new V3dMeshDescription
        {
            Kind = kind,
            Submeshes = submeshes.ToImmutable(),
            Bones = [.. file.Bones.Select(CompiledBone)],
            CollisionSpheres = file.CollisionSpheres,
            PropPoints = [.. file.PropPoints.Select(p => new V3dPropPoint(Widen(p.Name, V3dPropPoint.NameSize), p.Rotation, p.Position, p.ParentIndex))],
        };
        return new LegacyMesh(sourceFormat, description, [.. notes]);
    }

    /// <summary>A compiled material from an exporter one: the same fields, the flags in the compiled meaning.</summary>
    public static V3dMaterial CompiledMaterial(V3dMaterial exported)
    {
        ArgumentNullException.ThrowIfNull(exported);
        uint flags = CompiledBaseFlags | (IsTwoSided(exported) ? CompiledTwoSided : 0) | ((exported.Flags & ExporterAlpha) != 0 ? CompiledAlpha : 0);
        return exported with { Flags = flags };
    }

    /// <summary>A compiled bone: the name in lower case, the rotation normalised and signed as the compiler left it (the same rotation).</summary>
    public static V3dBone CompiledBone(V3dBone bone)
    {
        var q = bone.Rotation;
        float length = q.Length();
        if (length > 0f && float.IsFinite(length) && MathF.Abs(length - 1f) > 1e-6f) q = Quaternion.Normalize(q);
        // The compiler went through a rotation matrix and back (the trace method): W comes out positive when the
        // trace is positive (|W| > 0.5), otherwise the largest of X, Y and Z does.
        float ax = MathF.Abs(q.X), ay = MathF.Abs(q.Y), az = MathF.Abs(q.Z);
        float sign = q.W * q.W > 0.25f ? q.W : ax >= ay && ax >= az ? q.X : ay >= az ? q.Y : q.Z;
        if (sign < 0f) q = -q;
        q = new Quaternion(q.X + 0f, q.Y + 0f, q.Z + 0f, q.W + 0f);
        string lower = bone.Name.Text.ToLowerInvariant();
        var name = lower == bone.Name.Text ? bone.Name : FixedString.FromText(lower, V3dBone.NameSize);
        return bone with { Name = name, Rotation = q };
    }

    private static bool IsTwoSided(V3dMaterial exported) => (exported.Flags & ExporterTwoSided) != 0;

    private sealed class LodStats
    {
        public readonly List<string> Garbage = [];
        public readonly List<string> Smoothed = [];
        public int Welded;
        public float WeldMax;
    }

    private sealed class Group
    {
        public readonly List<V3dMeshVertex> Vertices = [];
        public readonly Dictionary<int, List<int>> ByPosition = [];
        public readonly List<V3dMeshTriangle> Triangles = [];
    }

    private static V3dLodDescription BuildLod(
        ExporterSubmesh sub, float distance, bool character, List<V3dMaterial> materials,
        ImmutableArray<V3dBoneLink> weights, int weightStart, LodStats stats)
    {
        // The LOD's own materials, found in (or added to) the submesh's list; then the batch each one draws in:
        // the first material with the same texture.
        var map = new int[sub.Materials.Length];
        for (int m = 0; m < sub.Materials.Length; m++)
        {
            var compiled = CompiledMaterial(sub.Materials[m]);
            int found = materials.FindIndex(x => x == compiled);
            if (found < 0)
            {
                found = materials.Count;
                materials.Add(compiled);
            }
            string texture = compiled.DiffuseMap.Text;
            int first = materials.FindIndex(x => string.Equals(x.DiffuseMap.Text, texture, StringComparison.OrdinalIgnoreCase));
            map[m] = first >= 0 ? first : found;
        }

        var normals = SmoothNormals(sub, out var stored);
        if (stored == StoredNormals.Garbage) stats.Garbage.Add($"'{sub.Name.Text}'");
        else if (stored == StoredNormals.HardEdges) stats.Smoothed.Add($"'{sub.Name.Text}'");

        var groups = new SortedDictionary<int, Group>();
        foreach (var f in sub.Faces)
        {
            int material = map[f.Material];
            if (!groups.TryGetValue(material, out var g))
            {
                g = new Group();
                groups[material] = g;
            }
            var shift = new Vector2(
                -MathF.Floor(MathF.Min(f.UvA.X, MathF.Min(f.UvB.X, f.UvC.X))),
                -MathF.Floor(MathF.Min(f.UvA.Y, MathF.Min(f.UvB.Y, f.UvC.Y))));
            int a = Corner(g, f.A, f.UvA + shift);
            int b = Corner(g, f.B, f.UvB + shift);
            int c = Corner(g, f.C, f.UvC + shift);
            ushort flags = IsTwoSided(sub.Materials[f.Material]) ? V3dTriangle.DoubleSided : (ushort)0;
            g.Triangles.Add(new V3dMeshTriangle(a, b, c, flags));
        }

        int Corner(Group g, int position, Vector2 uv)
        {
            if (!g.ByPosition.TryGetValue(position, out var list))
            {
                list = [];
                g.ByPosition[position] = list;
            }
            foreach (int v in list)
            {
                var d = g.Vertices[v].TexCoord - uv;
                if (MathF.Abs(d.X) < UvWeldTolerance && MathF.Abs(d.Y) < UvWeldTolerance)
                {
                    float moved = MathF.Max(MathF.Abs(d.X), MathF.Abs(d.Y));
                    if (moved > 0f)
                    {
                        stats.Welded++;
                        stats.WeldMax = MathF.Max(stats.WeldMax, moved);
                    }
                    return v;
                }
            }
            int at = g.Vertices.Count;
            int w = weightStart + position;
            var influences = character && w < weights.Length ? V3dBuilder.Influences(weights[w]) : [];
            g.Vertices.Add(new V3dMeshVertex(sub.Positions[position], normals[position], uv, influences));
            list.Add(at);
            return at;
        }

        return new V3dLodDescription
        {
            Distance = distance,
            Groups = [.. groups.Select(kv => new V3dMaterialGroup
            {
                Material = kv.Key,
                Vertices = [.. kv.Value.Vertices],
                Triangles = [.. kv.Value.Triangles],
            })],
            MorphVertices = character ? sub.Positions : null,
            VertexCountOverride = character ? null : sub.Positions.Length,
        };
    }

    private enum StoredNormals { Smooth, HardEdges, Garbage }

    /// <summary>
    /// One normal per position: the normalised sum of the unit normals of the faces using it (a position no
    /// face uses gets +Y). <paramref name="stored"/> says how the exporter's own normals compare.
    /// </summary>
    private static Vector3[] SmoothNormals(ExporterSubmesh sub, out StoredNormals stored)
    {
        var sums = new Vector3[sub.Positions.Length];
        foreach (var f in sub.Faces)
        {
            var p0 = sub.Positions[f.A];
            var cross = Vector3.Cross(sub.Positions[f.B] - p0, sub.Positions[f.C] - p0);
            float length = cross.Length();
            if (!(length > 0f) || !float.IsFinite(length)) continue;
            var unit = cross / length;
            sums[f.A] += unit;
            sums[f.B] += unit;
            sums[f.C] += unit;
        }
        var result = new Vector3[sums.Length];
        for (int i = 0; i < result.Length; i++)
        {
            float length = sums[i].Length();
            result[i] = length > 0f && float.IsFinite(length) ? sums[i] / length : Vector3.UnitY;
        }

        stored = StoredNormals.Smooth;
        foreach (var n in sub.Normals)
        {
            float length = n.Normal.Length();
            if (!float.IsFinite(length) || MathF.Abs(length - 1f) > 0.01f)
            {
                stored = StoredNormals.Garbage;
                return result;
            }
        }
        foreach (var f in sub.Faces)
        {
            if (Differs(f.A, f.NormalA) || Differs(f.B, f.NormalB) || Differs(f.C, f.NormalC))
            {
                stored = StoredNormals.HardEdges;
                break;
            }
        }
        return result;

        // More than about 2.5 degrees apart.
        bool Differs(int position, int normal) => Vector3.Dot(result[position], sub.Normals[normal].Normal) < 0.999f;
    }

    /// <summary>A name field copied into a wider one (prop point names are 24 bytes here, 68 in a compiled mesh).</summary>
    private static FixedString Widen(FixedString name, int length)
    {
        var bytes = new byte[length];
        var text = name.Bytes.AsSpan();
        int nul = text.IndexOf((byte)0);
        (nul < 0 ? text : text[..nul]).CopyTo(bytes);
        return FixedString.FromBytes(bytes);
    }

    private static string Names(List<string> names) =>
        names.Count == 1 ? "Submesh " + names[0] : "Submeshes " + string.Join(", ", names.Distinct());

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";
}
