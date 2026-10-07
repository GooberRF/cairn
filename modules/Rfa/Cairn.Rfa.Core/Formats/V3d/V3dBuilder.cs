using System.Collections.Immutable;
using System.Numerics;
using Cairn.Formats.Maths;

namespace Cairn.Rfa.Formats.V3d;

/// <summary>
/// Builds a valid <see cref="V3dFile"/> from primary data (<see cref="V3dMeshDescription"/>) and
/// decomposes one back. Every derived field follows the rule the stock exporter used, as measured on
/// all 95 stock .v3c and 427 stock .v3m files (DESIGN.md section 9, phase 3):
/// <list type="bullet">
/// <item>one batch per material group, split only when it exceeds 5460 vertices or 8191 triangles
/// (the 16-bit size fields); stock never needed a split;</item>
/// <item><c>batch_info</c> sizes <see cref="V3dBatchSizes.Canonical"/> (all stock batches);</item>
/// <item>triangle planes (LOD flag 0x20): <c>n = normalize(cross(p1 - p0, p2 - p0))</c>,
/// <c>d = -dot(n, p0)</c> in double; degenerate triangles get NaN as in stock;</item>
/// <item>same-position offsets: <c>i - j</c> for the first earlier vertex <c>j</c> at exactly the same
/// position, else 0 (all stock batches);</item>
/// <item>morph map: per batch, original vertex <c>o</c> maps to the first vertex at its position, -1
/// where the batch has none (166 of 170 stock LODs reproduce exactly);</item>
/// <item>bone links: characters as the influences say; static meshes all zero (every stock .v3m);</item>
/// <item>LOD texture list: the materials the LOD uses, in material order, named after the material;</item>
/// <item>bounding box and radius from LOD 0's submesh-local positions (radius = largest
/// <c>|p|</c>), header counts, signature by kind, totals that ccrunch zeroes left at 0.</item>
/// </list>
/// Batch header leftovers, LOD texture lists and original-vertex orders that follow no rule are
/// primary data in the description, so a decomposed stock file rebuilds with them intact.
/// </summary>
public static class V3dBuilder
{
    /// <summary>The render flags of nearly every stock batch.</summary>
    public const uint DefaultRenderFlags = 0x518C41;

    /// <summary>LOD flags of every stock character LOD: morph map + character.</summary>
    public const uint DefaultCharacterLodFlags = V3dLod.FlagMorphVerticesMap | V3dLod.FlagCharacter;

    /// <summary>LOD flags of (all but one) stock static LOD: triangle planes.</summary>
    public const uint DefaultStaticLodFlags = V3dLod.FlagTrianglePlanes;

    /// <summary>The most vertices whose positions fit a 16-bit size field (rounded up to 16 bytes).</summary>
    public const int MaxBatchVertices = 5460;

    /// <summary>The most triangles whose indices fit a 16-bit size field.</summary>
    public const int MaxBatchTriangles = 8191;

    /// <summary>The engine's LOD limit (Alpine Faction's VifLodMesh holds 3 too).</summary>
    public const int MaxLods = 3;

    /// <summary>The engine's limit on textures per LOD.</summary>
    public const int MaxTexturesPerLod = 7;

    /// <summary>
    /// A stored bone from a rest (bind) pose in the active convention: the file holds the inverse
    /// bind with a conjugated rotation, which leaves the rotation equal to the rest rotation and the
    /// position equal to <c>-Rotate(conj(rot), restPos)</c>.
    /// </summary>
    public static V3dBone BoneFromRestWorld(string name, int parent, Rigid restWorld)
    {
        ArgumentNullException.ThrowIfNull(name);
        var rot = Quat.Normalize(restWorld.Rotation);
        var pos = -Quat.Rotate(Quat.Conj(rot), restWorld.Position);
        return new V3dBone(FixedString.FromText(name, V3dBone.NameSize), rot, pos, parent);
    }

    /// <summary>The LOD flags used when a description leaves them null.</summary>
    public static uint DefaultLodFlags(V3dKind kind) => kind == V3dKind.Character ? DefaultCharacterLodFlags : DefaultStaticLodFlags;

    /// <summary>Builds the mesh.</summary>
    /// <exception cref="ArgumentException">The description is inconsistent (an index out of range, a field that cannot be stored).</exception>
    public static V3dFile Build(V3dMeshDescription description, V3dBuildOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        options ??= V3dBuildOptions.Default;
        if (options.MaxBatchVertices is < 3 or > MaxBatchVertices)
            throw new ArgumentException($"MaxBatchVertices must be 3..{MaxBatchVertices}.", nameof(options));
        if (options.MaxBatchTriangles is < 1 or > MaxBatchTriangles)
            throw new ArgumentException($"MaxBatchTriangles must be 1..{MaxBatchTriangles}.", nameof(options));

        var d = description;
        var sections = ImmutableArray.CreateBuilder<V3dSection>();
        for (int s = 0; s < d.Submeshes.Length; s++) sections.Add(BuildSubmesh(d, s, options));
        foreach (var sphere in d.CollisionSpheres) sections.Add(sphere);
        if (d.Kind == V3dKind.Character || d.Bones.Length > 0)
        {
            foreach (var bone in d.Bones)
            {
                if (bone.ParentIndex < -1 || bone.ParentIndex >= d.Bones.Length)
                    throw new ArgumentException($"Bone '{bone.Name.Text}' has parent {bone.ParentIndex}, outside 0..{d.Bones.Length - 1}.");
            }
            sections.Add(new V3dBoneSection(d.Bones, d.BoneSectionExtra));
        }
        sections.AddRange(d.ExtraSections);

        var header = new V3dHeader(
            d.Kind == V3dKind.Character ? V3dHeader.CharacterSignature : V3dHeader.StaticSignature,
            V3dHeader.CurrentVersion,
            d.Submeshes.Length, 0, 0, 0,
            d.Submeshes.Sum(s => s.Materials.Length), 0, 0,
            d.CollisionSpheres.Length);
        return new V3dFile
        {
            Header = header,
            Sections = sections.ToImmutable(),
            EndSizeField = d.EndSizeField,
            TrailingBytes = d.TrailingBytes,
        };
    }

    private static V3dSubmesh BuildSubmesh(V3dMeshDescription d, int index, V3dBuildOptions options)
    {
        var s = d.Submeshes[index];
        string what = $"Submesh {index} ('{s.Name.Text}')";
        if (s.Lods.IsDefaultOrEmpty) throw new ArgumentException($"{what} has no LOD.");
        var lods = new V3dLod[s.Lods.Length];
        for (int l = 0; l < lods.Length; l++) lods[l] = BuildLod(d, s, s.Lods[l], $"{what} LOD {l}", options);

        // Bounds: LOD 0's submesh-local positions (the box and the largest |p|, both float).
        var min = Vector3.Zero;
        var max = Vector3.Zero;
        float radius = 0f;
        bool any = false;
        foreach (var b in lods[0].Batches)
        {
            foreach (var p in b.Positions)
            {
                if (!any)
                {
                    min = max = p;
                    any = true;
                }
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                radius = MathF.Max(radius, p.Length());
            }
        }

        return new V3dSubmesh
        {
            SizeField = s.SizeField,
            Name = s.Name,
            ParentName = s.ParentName,
            Version = s.Version,
            LodDistances = [.. s.Lods.Select(l => l.Distance)],
            Offset = s.Offset,
            Radius = radius,
            AabbMin = min,
            AabbMax = max,
            Lods = [.. lods],
            Materials = s.Materials,
            Trailers = s.Trailers ?? [new V3dSubmeshTrailer(TrailerName(s.Name), 0f)],
        };
    }

    // The trailer's name field is the same size as the submesh name (24 bytes).
    private static FixedString TrailerName(FixedString name) => name;

    private static V3dLod BuildLod(V3dMeshDescription d, V3dSubmeshDescription s, V3dLodDescription lod, string what, V3dBuildOptions options)
    {
        uint flags = lod.Flags ?? DefaultLodFlags(d.Kind);
        bool planes = (flags & V3dLod.FlagTrianglePlanes) != 0;
        bool morph = (flags & V3dLod.FlagMorphVerticesMap) != 0;

        foreach (var g in lod.Groups)
        {
            if ((uint)g.Material >= (uint)s.Materials.Length)
                throw new ArgumentException($"{what} has geometry for material {g.Material}, but the submesh has {s.Materials.Length} materials.");
        }

        // Texture list: explicit, or the used materials in material order named after the material.
        var textures = lod.Textures ?? [.. lod.Groups.Select(g => g.Material).Distinct().Order()
            .Select(m => new V3dLodTexture((byte)m, s.Materials[m].DiffuseMap.Text))];
        foreach (var t in textures)
        {
            if (t.MaterialIndex >= s.Materials.Length)
                throw new ArgumentException($"{what} lists texture '{t.FileName}' for material {t.MaterialIndex}, but the submesh has {s.Materials.Length} materials.");
        }

        // Original vertices for the morph map / declared vertex count.
        ImmutableArray<Vector3> originals = lod.MorphVertices ?? DistinctPositions(lod.Groups);

        var batches = ImmutableArray.CreateBuilder<V3dBatch>();
        foreach (var g in lod.Groups)
        {
            int textureIndex = -1;
            for (int t = 0; t < textures.Length; t++)
            {
                if (textures[t].MaterialIndex == g.Material)
                {
                    textureIndex = t;
                    break;
                }
            }
            if (textureIndex < 0)
                throw new ArgumentException($"{what}: material {g.Material} has geometry but is not in the LOD's texture list.");
            foreach (var chunk in Split(g, what, options))
                batches.Add(BuildBatch(d.Kind, g, chunk.Vertices, chunk.Triangles, textureIndex, planes, morph, originals, options, what));
        }

        int vertexCount = morph ? originals.Length : lod.VertexCountOverride ?? DistinctPositions(lod.Groups).Length;
        return new V3dLod
        {
            Flags = flags,
            VertexCount = vertexCount,
            Batches = batches.ToImmutable(),
            Unknown1 = lod.Unknown1,
            PropPoints = lod.PropPoints ?? d.PropPoints,
            Textures = textures,
        };
    }

    private readonly record struct Chunk(ImmutableArray<V3dMeshVertex> Vertices, ImmutableArray<V3dMeshTriangle> Triangles);

    /// <summary>
    /// The group as one chunk when it fits a batch; otherwise triangles are taken in order into
    /// chunks of at most the vertex/triangle limits, each with its own (re-indexed) vertex list.
    /// </summary>
    private static IEnumerable<Chunk> Split(V3dMaterialGroup g, string what, V3dBuildOptions options)
    {
        foreach (var t in g.Triangles)
        {
            if ((uint)t.A >= (uint)g.Vertices.Length || (uint)t.B >= (uint)g.Vertices.Length || (uint)t.C >= (uint)g.Vertices.Length)
                throw new ArgumentException($"{what}: a triangle of material {g.Material} uses vertex {Math.Max(t.A, Math.Max(t.B, t.C))}, but the group has {g.Vertices.Length} vertices.");
        }
        if (g.Vertices.Length <= options.MaxBatchVertices && g.Triangles.Length <= options.MaxBatchTriangles)
        {
            yield return new Chunk(g.Vertices, g.Triangles);
            yield break;
        }

        var map = new Dictionary<int, int>();
        var verts = new List<V3dMeshVertex>();
        var tris = new List<V3dMeshTriangle>();
        foreach (var t in g.Triangles)
        {
            int extra = (map.ContainsKey(t.A) ? 0 : 1) + (map.ContainsKey(t.B) ? 0 : 1) + (map.ContainsKey(t.C) ? 0 : 1);
            if (verts.Count + extra > options.MaxBatchVertices || tris.Count + 1 > options.MaxBatchTriangles)
            {
                yield return new Chunk([.. verts], [.. tris]);
                map.Clear();
                verts.Clear();
                tris.Clear();
            }
            tris.Add(new V3dMeshTriangle(Remap(t.A), Remap(t.B), Remap(t.C), t.Flags));
        }
        if (tris.Count > 0) yield return new Chunk([.. verts], [.. tris]);

        int Remap(int v)
        {
            if (!map.TryGetValue(v, out int local))
            {
                local = verts.Count;
                verts.Add(g.Vertices[v]);
                map[v] = local;
            }
            return local;
        }
    }

    private static V3dBatch BuildBatch(
        V3dKind kind, V3dMaterialGroup g, ImmutableArray<V3dMeshVertex> vertices, ImmutableArray<V3dMeshTriangle> triangles,
        int textureIndex, bool planes, bool morph, ImmutableArray<Vector3> originals, V3dBuildOptions options, string what)
    {
        int nv = vertices.Length, nt = triangles.Length;
        var positions = new Vector3[nv];
        var normals = new Vector3[nv];
        var uvs = new Vector2[nv];
        var links = new V3dBoneLink[nv];
        for (int i = 0; i < nv; i++)
        {
            var v = vertices[i];
            positions[i] = v.Position;
            normals[i] = v.Normal;
            uvs[i] = v.TexCoord;
            links[i] = kind == V3dKind.Character ? PackLink(v.Influences, options.WeightMode, what) : default;
        }

        var tris = new V3dTriangle[nt];
        for (int i = 0; i < nt; i++)
        {
            var t = triangles[i];
            tris[i] = new V3dTriangle((ushort)t.A, (ushort)t.B, (ushort)t.C, t.Flags);
        }

        var planeArray = planes ? ComputePlanes(positions, tris) : [];
        var same = SamePositionOffsets(positions);
        var morphMap = morph ? MorphMap(positions, originals.AsSpan()) : [];

        return new V3dBatch
        {
            HeaderReserved0 = CheckReserved(g.HeaderReserved0, 0x20, what) ?? [.. new byte[0x20]],
            TextureIndex = textureIndex,
            HeaderReserved1 = CheckReserved(g.HeaderReserved1, 0x14, what) ?? [.. new byte[0x14]],
            Positions = [.. positions],
            Normals = [.. normals],
            TexCoords = [.. uvs],
            Triangles = [.. tris],
            Planes = planeArray,
            SamePositionOffsets = same,
            BoneLinks = [.. links],
            MorphMap = morphMap,
            Sizes = V3dBatchSizes.Canonical(nv, nt, hasBoneLinks: true),
            RenderFlags = g.RenderFlags,
        };
    }

    private static ImmutableArray<byte>? CheckReserved(ImmutableArray<byte>? bytes, int length, string what)
    {
        if (bytes is { } b && b.Length != length)
            throw new ArgumentException($"{what}: a batch header reserved block is {b.Length} bytes; it must be {length}.");
        return bytes;
    }

    /// <summary>
    /// Triangle planes as the stock exporter computes them: the normal of <c>cross(p1 - p0, p2 - p0)</c>
    /// (float cross product, scaled by the float reciprocal of its length — this matches the stock
    /// bits most often; every stock normal agrees within 1e-6) and <c>d = -dot(n, p0)</c> evaluated in
    /// double (every stock distance agrees bit for bit given its normal). A degenerate triangle gets a
    /// NaN plane, which is what the 16 degenerate stock triangles store.
    /// </summary>
    public static ImmutableArray<V3dPlane> ComputePlanes(ReadOnlySpan<Vector3> positions, ReadOnlySpan<V3dTriangle> triangles)
    {
        var result = new V3dPlane[triangles.Length];
        for (int i = 0; i < triangles.Length; i++)
        {
            var t = triangles[i];
            var p0 = positions[t.A];
            var cross = Vector3.Cross(positions[t.B] - p0, positions[t.C] - p0);
            float lengthSquared = cross.X * cross.X + cross.Y * cross.Y + cross.Z * cross.Z;
            if (!(lengthSquared > 0f) || !float.IsFinite(lengthSquared))
            {
                result[i] = new V3dPlane(new Vector3(float.NaN), float.NaN);
                continue;
            }
            var n = cross * (1f / MathF.Sqrt(lengthSquared));
            float dist = (float)-(n.X * (double)p0.X + n.Y * (double)p0.Y + n.Z * (double)p0.Z);
            result[i] = new V3dPlane(n, dist);
        }
        return [.. result];
    }

    /// <summary>
    /// Same-position offsets: for each vertex, how many places back the FIRST vertex with exactly the
    /// same position is (0 when it is the first). Every stock batch follows this rule.
    /// </summary>
    public static ImmutableArray<short> SamePositionOffsets(ReadOnlySpan<Vector3> positions)
    {
        var first = new Dictionary<PositionKey, int>(positions.Length);
        var result = new short[positions.Length];
        for (int i = 0; i < positions.Length; i++)
        {
            var key = new PositionKey(positions[i]);
            if (first.TryGetValue(key, out int j)) result[i] = (short)Math.Min(i - j, short.MaxValue);
            else first[key] = i;
        }
        return [.. result];
    }

    /// <summary>
    /// A batch's morph map: for every original vertex, the first batch vertex at its position, or -1.
    /// </summary>
    public static ImmutableArray<short> MorphMap(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> originals)
    {
        var first = new Dictionary<PositionKey, int>(positions.Length);
        for (int i = 0; i < positions.Length; i++) first.TryAdd(new PositionKey(positions[i]), i);
        var map = new short[originals.Length];
        for (int o = 0; o < originals.Length; o++)
            map[o] = first.TryGetValue(new PositionKey(originals[o]), out int i) && !float.IsNaN(originals[o].X) ? (short)i : (short)-1;
        return [.. map];
    }

    /// <summary>The distinct positions of a LOD's groups, in first-appearance order.</summary>
    public static ImmutableArray<Vector3> DistinctPositions(IEnumerable<V3dMaterialGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var seen = new HashSet<PositionKey>();
        var result = ImmutableArray.CreateBuilder<Vector3>();
        foreach (var g in groups)
        {
            foreach (var v in g.Vertices)
            {
                if (seen.Add(new PositionKey(v.Position))) result.Add(v.Position);
            }
        }
        return result.ToImmutable();
    }

    /// <summary>
    /// Packs up to four influences into a bone link. <see cref="V3dWeightMode.Normalize"/>: positive
    /// weights on bones 0..254 only, duplicates merged, the four largest kept in descending order,
    /// scaled to bytes summing to exactly 255 by the largest-remainder method; slots that round to
    /// 0 are dropped. <see cref="V3dWeightMode.Preserve"/>: slot by slot, <c>round(w * 255)</c>.
    /// Unused slots are (weight 0, bone 0xFF), as in every stock character.
    /// </summary>
    public static V3dBoneLink PackLink(IReadOnlyList<V3dBoneInfluence> influences, V3dWeightMode mode, string what = "mesh")
    {
        Span<byte> w = stackalloc byte[4];
        Span<byte> b = [V3dBoneLink.NoBone, V3dBoneLink.NoBone, V3dBoneLink.NoBone, V3dBoneLink.NoBone];
        if (influences is null || influences.Count == 0) return new V3dBoneLink(0, 0, 0, 0, b[0], b[1], b[2], b[3]);
        if (mode == V3dWeightMode.Preserve)
        {
            if (influences.Count > 4) throw new ArgumentException($"{what}: a vertex has {influences.Count} influences; a bone link holds 4.");
            for (int i = 0; i < influences.Count; i++)
            {
                var inf = influences[i];
                if (inf.Bone < -1 || inf.Bone > 254) throw new ArgumentException($"{what}: bone index {inf.Bone} cannot be stored in a bone link.");
                w[i] = (byte)Math.Clamp((int)MathF.Round(inf.Weight * 255f, MidpointRounding.AwayFromZero), 0, 255);
                b[i] = inf.Bone < 0 ? V3dBoneLink.NoBone : (byte)inf.Bone;
            }
            return new V3dBoneLink(w[0], w[1], w[2], w[3], b[0], b[1], b[2], b[3]);
        }

        var merged = new Dictionary<int, double>();
        foreach (var inf in influences)
        {
            if (inf.Bone < 0 || inf.Bone > 254 || !(inf.Weight > 0f) || !float.IsFinite(inf.Weight)) continue;
            merged[inf.Bone] = merged.GetValueOrDefault(inf.Bone) + inf.Weight;
        }
        var top = merged.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).Take(4).ToList();
        double total = top.Sum(kv => kv.Value);
        if (top.Count == 0 || !(total > 0)) return new V3dBoneLink(0, 0, 0, 0, b[0], b[1], b[2], b[3]);

        var bytes = new int[4];
        var remainder = new double[4];
        int sum = 0;
        for (int i = 0; i < top.Count; i++)
        {
            double exact = top[i].Value / total * 255.0;
            bytes[i] = (int)Math.Floor(exact);
            remainder[i] = exact - bytes[i];
            sum += bytes[i];
        }
        while (sum < 255)
        {
            int best = 0;
            for (int i = 1; i < top.Count; i++) if (remainder[i] > remainder[best]) best = i;
            bytes[best]++;
            remainder[best] = -1;
            sum++;
        }
        // Order by the final bytes (descending, stable), dropping slots that ended at 0.
        var order = Enumerable.Range(0, top.Count).Where(i => bytes[i] > 0).OrderByDescending(i => bytes[i]).ThenBy(i => i).ToList();
        for (int s = 0; s < order.Count; s++)
        {
            w[s] = (byte)bytes[order[s]];
            b[s] = (byte)top[order[s]].Key;
        }
        return new V3dBoneLink(w[0], w[1], w[2], w[3], b[0], b[1], b[2], b[3]);
    }

    // ── Decompose ───────────────────────────────────────────────────────────

    /// <summary>
    /// The primary data of a mesh: everything <see cref="Build"/> needs to make it again. Batches
    /// become material groups (one per batch), bone links become influences slot for slot, the LOD
    /// texture lists, original-vertex orders (from the morph maps), declared vertex counts, batch
    /// header leftovers and trailers are kept explicitly. Prop points become the mesh-level list
    /// (LOD 0 of submesh 0); a LOD whose list differs keeps its own.
    /// Build(Decompose(f), <see cref="V3dBuildOptions.Preserve"/>) gives back the same geometry,
    /// materials, bones, spheres and prop points with re-derived batch data.
    /// </summary>
    /// <exception cref="ArgumentException">A batch's texture index is outside its LOD's texture list.</exception>
    public static V3dMeshDescription Decompose(V3dFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var subs = file.Submeshes.ToList();
        var modelProps = subs.Count > 0 && subs[0].Lods.Length > 0 ? subs[0].Lods[0].PropPoints : [];
        var submeshes = ImmutableArray.CreateBuilder<V3dSubmeshDescription>(subs.Count);
        for (int si = 0; si < subs.Count; si++)
        {
            var s = subs[si];
            var lods = ImmutableArray.CreateBuilder<V3dLodDescription>(s.Lods.Length);
            for (int li = 0; li < s.Lods.Length; li++)
            {
                var l = s.Lods[li];
                string what = $"Submesh {si} LOD {li}";
                var groups = ImmutableArray.CreateBuilder<V3dMaterialGroup>(l.Batches.Length);
                foreach (var b in l.Batches)
                {
                    if ((uint)b.TextureIndex >= (uint)l.Textures.Length)
                        throw new ArgumentException($"{what} has a batch with texture index {b.TextureIndex}, outside its {l.Textures.Length}-entry texture list.");
                    var verts = new V3dMeshVertex[b.VertexCount];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var inf = file.Kind == V3dKind.Character && i < b.BoneLinks.Length ? Influences(b.BoneLinks[i]) : [];
                        verts[i] = new V3dMeshVertex(b.Positions[i], b.Normals[i], b.TexCoords[i], inf);
                    }
                    groups.Add(new V3dMaterialGroup
                    {
                        Material = l.Textures[b.TextureIndex].MaterialIndex,
                        RenderFlags = b.RenderFlags,
                        Vertices = [.. verts],
                        Triangles = [.. b.Triangles.Select(t => new V3dMeshTriangle(t.A, t.B, t.C, t.Flags))],
                        HeaderReserved0 = b.HeaderReserved0,
                        HeaderReserved1 = b.HeaderReserved1,
                    });
                }

                ImmutableArray<Vector3>? originals = null;
                int? countOverride = null;
                if ((l.Flags & V3dLod.FlagMorphVerticesMap) != 0)
                {
                    var p = Enumerable.Repeat(new Vector3(float.NaN), l.VertexCount).ToArray();
                    var known = new bool[p.Length];
                    foreach (var b in l.Batches)
                    {
                        for (int o = 0; o < p.Length && o < b.MorphMap.Length; o++)
                        {
                            int t = b.MorphMap[o];
                            if (t < 0 || t >= b.VertexCount || known[o]) continue;
                            p[o] = b.Positions[t];
                            known[o] = true;
                        }
                    }
                    originals = [.. p];
                }
                else
                {
                    countOverride = l.VertexCount;
                }

                bool sameProps = l.PropPoints.SequenceEqual(modelProps);
                lods.Add(new V3dLodDescription
                {
                    Distance = li < s.LodDistances.Length ? s.LodDistances[li] : 0f,
                    Flags = l.Flags,
                    Unknown1 = l.Unknown1,
                    Groups = groups.MoveToImmutable(),
                    Textures = l.Textures,
                    MorphVertices = originals,
                    VertexCountOverride = countOverride,
                    PropPoints = sameProps ? null : l.PropPoints,
                });
            }
            submeshes.Add(new V3dSubmeshDescription
            {
                Name = s.Name,
                ParentName = s.ParentName,
                Version = s.Version,
                SizeField = s.SizeField,
                Offset = s.Offset,
                Lods = lods.MoveToImmutable(),
                Materials = s.Materials,
                Trailers = s.Trailers,
            });
        }

        return new V3dMeshDescription
        {
            Kind = file.Kind,
            Submeshes = submeshes.MoveToImmutable(),
            Bones = file.Bones,
            CollisionSpheres = [.. file.CollisionSpheres],
            PropPoints = modelProps,
            BoneSectionExtra = file.BoneSection?.Extra ?? [],
            ExtraSections = [.. file.Sections.Where(x => x is not (V3dSubmesh or V3dCollisionSphere or V3dBoneSection))],
            EndSizeField = file.EndSizeField,
            TrailingBytes = file.TrailingBytes,
        };
    }

    /// <summary>A stored link as influences, slot for slot, with trailing unused slots (0, 0xFF) dropped.</summary>
    public static ImmutableArray<V3dBoneInfluence> Influences(V3dBoneLink link)
    {
        int last = -1;
        for (int s = 0; s < 4; s++)
        {
            if (link.GetWeight(s) != 0 || link.GetBone(s) != V3dBoneLink.NoBone) last = s;
        }
        var result = new V3dBoneInfluence[last + 1];
        for (int s = 0; s <= last; s++)
        {
            byte bone = link.GetBone(s);
            result[s] = new V3dBoneInfluence(bone == V3dBoneLink.NoBone ? -1 : bone, link.GetWeight(s) / 255f);
        }
        return [.. result];
    }

    /// <summary>A position as a hash key: exact float equality, with -0 and +0 the same.</summary>
    internal readonly record struct PositionKey
    {
        private readonly int _x, _y, _z;

        public PositionKey(Vector3 p)
        {
            _x = BitConverter.SingleToInt32Bits(p.X + 0f);
            _y = BitConverter.SingleToInt32Bits(p.Y + 0f);
            _z = BitConverter.SingleToInt32Bits(p.Z + 0f);
        }
    }
}
