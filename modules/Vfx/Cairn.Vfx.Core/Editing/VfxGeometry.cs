using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>
/// Derivations for computed mesh fields (research notes §6.1): face normal/centre/radius from frame 0,
/// the bounding sphere over every geometry frame, record-based face-vertex adjacency and quantisation.
/// </summary>
public static class VfxGeometry
{
    /// <summary>The raw u/v value of a freshly built face-vertex record (0xCDCDCDCD, as in stock 0x40006 files).</summary>
    public const uint UnsetUv = 0xCDCDCDCD;

    /// <summary>Normal = normalize(cross(b-a, c-a)), (0,1,0) when degenerate; centroid; max vertex distance to it.</summary>
    public static (Vector3 Normal, Vector3 Center, float Radius) FaceShape(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        n = len > 1e-12f && float.IsFinite(len) ? n / len : Vector3.UnitY;
        var center = (a + b + c) / 3f;
        float r = MathF.Max(Vector3.Distance(a, center), MathF.Max(Vector3.Distance(b, center), Vector3.Distance(c, center)));
        return (n, center, r);
    }

    /// <summary>AABB midpoint of all points and max distance to it; (0,0) for no points.</summary>
    public static (Vector3 Center, float Radius) BoundingSphere(IEnumerable<IReadOnlyList<Vector3>> frames)
    {
        var all = frames.SelectMany(f => f).ToList();
        if (all.Count == 0) return (Vector3.Zero, 0f);
        Vector3 min = all[0], max = all[0];
        foreach (var p in all) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        var c = (min + max) / 2f;
        float r = 0;
        foreach (var p in all) r = MathF.Max(r, Vector3.Distance(p, c));
        return (c, r);
    }

    /// <summary>The bounding sphere over every frame of <paramref name="mesh"/> that carries geometry.</summary>
    public static (Vector3 Center, float Radius) BoundingSphere(VfxMesh mesh) =>
        BoundingSphere(Enumerable.Range(0, mesh.Frames.Length).Select(mesh.DecodePositions).OfType<Vector3[]>());

    /// <summary>Quantises positions through <see cref="VfxPositionCodec"/>.</summary>
    public static VfxCompressedPositions Quantise(ReadOnlySpan<Vector3> points) => VfxPositionCodec.Encode(points);

    /// <summary>
    /// Ascending adjacency per face-vertex record: the faces whose FaceVertex0..2 reference it (a face that
    /// references a record twice is listed once).
    /// </summary>
    public static ImmutableArray<int>[] Adjacency(IReadOnlyList<VfxFace> faces, int recordCount)
    {
        var lists = new List<int>[recordCount];
        for (int i = 0; i < recordCount; i++) lists[i] = [];
        for (int f = 0; f < faces.Count; f++)
            foreach (int r in new[] { faces[f].FaceVertex0, faces[f].FaceVertex1, faces[f].FaceVertex2 }.Distinct())
                if (r >= 0 && r < recordCount) lists[r].Add(f);
        return lists.Select(l => l.ToImmutableArray()).ToArray();
    }

    /// <summary>The same records with their adjacency recomputed from the faces.</summary>
    public static ImmutableArray<VfxFaceVertex> WithAdjacency(IReadOnlyList<VfxFace> faces, ImmutableArray<VfxFaceVertex> records)
    {
        var adj = Adjacency(faces, records.Length);
        return records.Select((r, i) => r.AdjacentFaces.SequenceEqual(adj[i]) ? r : r with { AdjacentFaces = adj[i] }).ToImmutableArray();
    }

    /// <summary>
    /// Builds one record per (vertex, face smoothing group) in first-use order, rewrites the faces'
    /// FaceVertex references and computes adjacency. Raw u/v are <see cref="UnsetUv"/>.
    /// </summary>
    public static (ImmutableArray<VfxFace> Faces, ImmutableArray<VfxFaceVertex> Records) BuildFaceVertices(IReadOnlyList<VfxFace> faces)
    {
        var map = new Dictionary<(int, int), int>();
        var keys = new List<(int V, int Sg)>();
        int Rec(int v, int sg)
        {
            if (!map.TryGetValue((v, sg), out int i)) { i = keys.Count; map[(v, sg)] = i; keys.Add((v, sg)); }
            return i;
        }
        var newFaces = faces.Select(f => f with
        {
            FaceVertex0 = Rec(f.V0, f.SmoothingGroup),
            FaceVertex1 = Rec(f.V1, f.SmoothingGroup),
            FaceVertex2 = Rec(f.V2, f.SmoothingGroup),
        }).ToImmutableArray();
        var adj = Adjacency(newFaces, keys.Count);
        return (newFaces, keys.Select((k, i) => new VfxFaceVertex(k.Sg, k.V, UnsetUv, UnsetUv, adj[i])).ToImmutableArray());
    }

    /// <summary>Face normal/centre/radius from frame 0 plus the all-frame bounding sphere; face-vertex records kept.</summary>
    public static VfxMesh RefreshShape(VfxMesh mesh)
    {
        var p = mesh.Frames.Length > 0 ? mesh.DecodePositions(0) : null;
        var faces = mesh.Faces;
        if (p is not null)
            faces = faces.Select(f =>
            {
                if ((uint)f.V0 >= p.Length || (uint)f.V1 >= p.Length || (uint)f.V2 >= p.Length) return f;
                var (n, c, r) = FaceShape(p[f.V0], p[f.V1], p[f.V2]);
                return f with { Normal = n, Center = c, Radius = r };
            }).ToImmutableArray();
        var (bc, br) = BoundingSphere(mesh);
        return mesh with { Faces = faces, BoundingCenter = bc, BoundingRadius = br };
    }

    /// <summary>Every derived field recomputed: face shapes, bounding sphere, face-vertex records and adjacency.</summary>
    public static VfxMesh Rederive(VfxMesh mesh)
    {
        var (faces, recs) = BuildFaceVertices(mesh.Faces);
        return RefreshShape(mesh with { Faces = faces, FaceVertices = recs });
    }

    /// <summary>Frame count from times: floor((end-start)*fps + 1e-3) + 1.</summary>
    public static int FrameCount(float start, float end, int fps)
    {
        // Same fps floor as EndTime (review-findings-2 #10); non-finite or absurd spans clamp instead of overflowing.
        float n = MathF.Floor((end - start) * Math.Max(fps, 1) + 1e-3f);
        return n >= 0 && n < 1_000_000 ? (int)n + 1 : n >= 1_000_000 ? 1_000_000 : 1;
    }

    /// <summary>End time for <paramref name="frames"/> frames: start + (frames-1)/fps.</summary>
    public static float EndTime(float start, int frames, int fps) => start + (frames - 1) / (float)Math.Max(fps, 1);
}
