using System.Collections.Immutable;
using System.Numerics;
using Cairn.Vfx.Editing;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Interchange;

/// <summary>
/// REDUX's geometry rebuild (P:1006): morph-aware 1e-6 weld in RF space, faces, smoothing 0/1 from normals,
/// face-vertex records per (vertex, group) with adjacency, compressed frames, face geometry and bounds.
/// Face shape, quantisation and bounds come from <see cref="VfxGeometry"/>; the weld, smoothing inference and
/// authored-order restore are REDUX-specific and stay here.
/// </summary>
internal sealed class VfxMeshRebuild
{
    private readonly List<Vector3> _pos = [];
    private readonly List<Vector3?> _nrm = [];
    private readonly List<Vector2> _uv = [];
    private readonly List<Vector3> _col = [];
    private readonly List<List<Vector3>> _deltas = [];
    private readonly List<(int A, int B, int C, int Slot)> _tris = [];
    private int[]? _smoothingOverride;

    public int TargetCount => _deltas.Count;
    public int FaceCount => _tris.Count;

    /// <summary>Adds glTF vertices (converted to RF space); returns the base index.</summary>
    public int AddVertices(Vector3[] pos, Vector3[]? nrm, Vector2[]? uv, Vector3[]? col, List<Vector3[]?> targets)
    {
        int b = _pos.Count;
        while (_deltas.Count < targets.Count) _deltas.Add(Enumerable.Repeat(Vector3.Zero, b).ToList());
        for (int i = 0; i < pos.Length; i++)
        {
            _pos.Add(VfxGltfSpace.Vector(pos[i]));
            _nrm.Add(nrm is not null && i < nrm.Length ? VfxGltfSpace.Vector(nrm[i]) : null);
            _uv.Add(uv is not null && i < uv.Length ? uv[i] : Vector2.Zero);
            _col.Add(col is not null && i < col.Length ? col[i] : Vector3.One);
            for (int t = 0; t < _deltas.Count; t++)
                _deltas[t].Add(t < targets.Count && targets[t] is { } d && i < d.Length ? VfxGltfSpace.Vector(d[i]) : Vector3.Zero);
        }
        return b;
    }

    public void AddTriangle(int a, int b, int c, int slot) => _tris.Add((a, b, c, slot));

    /// <summary>Authored face order is a stable sort by slot; the authored smoothing groups then apply.</summary>
    public void RestoreAuthoredOrder(int[] smoothing)
    {
        var sorted = _tris.Select((t, i) => (t, i)).OrderBy(x => x.t.Slot).ThenBy(x => x.i).Select(x => x.t).ToList();
        _tris.Clear(); _tris.AddRange(sorted);
        _smoothingOverride = smoothing;
    }

    public sealed record Result(int Vertices, ImmutableArray<VfxFace> Faces, ImmutableArray<VfxFaceVertex> FaceVertices,
        ImmutableArray<VfxMeshFrame> Frames, Vector3 Center, float Radius);

    public Result Build(int frameCount, uint flags, bool keyed,
        Func<int, (Vector2 Size, Vector3 Up, ImmutableArray<Vector2>? Uvs, VfxTransform Transform)> frameInfo)
    {
        static long Q(float v) => (long)Math.Round(v * 1e6);
        var weld = new Dictionary<string, int>();
        var map = new int[_pos.Count];
        var welded = new List<int>();
        for (int i = 0; i < _pos.Count; i++)
        {
            var parts = new List<long> { Q(_pos[i].X), Q(_pos[i].Y), Q(_pos[i].Z) };
            foreach (var d in _deltas) parts.AddRange([Q(d[i].X), Q(d[i].Y), Q(d[i].Z)]);
            string key = string.Join(",", parts);
            if (!weld.TryGetValue(key, out int w)) { weld[key] = w = welded.Count; welded.Add(i); }
            map[i] = w;
        }
        var p0 = welded.Select(i => _pos[i]).ToArray();
        var faces = new List<VfxFace>();
        var records = new List<(int Sg, int V, List<int> Adj)>();
        var recordIndex = new Dictionary<(int, int), int>();
        var cornerUvs = new List<Vector2>();
        for (int f = 0; f < _tris.Count; f++)
        {
            var (a, b, c, slot) = _tris[f];
            int[] cs = [a, b, c];
            int[] vs = [map[a], map[b], map[c]];
            var (n, center, radius) = VfxGeometry.FaceShape(p0[vs[0]], p0[vs[1]], p0[vs[2]]);
            int sg = _smoothingOverride is { } so && f < so.Length ? so[f]
                : cs.Any(k => _nrm[k] is null) ? 1 : cs.Any(k => Vector3.Dot(Vector3.Normalize(_nrm[k]!.Value), n) < 0.9995f) ? 1 : 0;
            var fv = new int[3];
            for (int k = 0; k < 3; k++)
            {
                var rk = (vs[k], Math.Max(1, sg));
                if (!recordIndex.TryGetValue(rk, out int r)) { recordIndex[rk] = r = records.Count; records.Add((Math.Max(1, sg), vs[k], [])); }
                if (!records[r].Adj.Contains(f)) records[r].Adj.Add(f);
                fv[k] = r;
                cornerUvs.Add(_uv[cs[k]]);
            }
            faces.Add(new VfxFace(vs[0], vs[1], vs[2], null, _col[a], _col[b], _col[c], n, center, radius, slot, sg, fv[0], fv[1], fv[2]));
        }
        ImmutableArray<Vector2> uv0 = [.. cornerUvs];
        var frames = new List<VfxMeshFrame>();
        for (int i = 0; i < frameCount; i++)
        {
            var layout = VfxMesh.FrameLayout(VfxVersion.Current, flags, keyed, i);
            var info = frameInfo(i);
            var pts = i == 0 || i > _deltas.Count ? p0 : welded.Select((src, w) => p0[w] + _deltas[i - 1][src]).ToArray();
            frames.Add(new VfxMeshFrame(layout.Positions ? VfxGeometry.Quantise(pts) : null, layout.FacingSize ? info.Size : null,
                layout.UpVector ? info.Up : null, layout.Uvs ? (i > 0 && info.Uvs is { } u && u.Length == uv0.Length ? u : uv0) : null,
                layout.Transform ? info.Transform : null, null, null));
        }
        var (bc, br) = VfxGeometry.BoundingSphere([p0]);
        return new Result(p0.Length, [.. faces], [.. records.Select(r => new VfxFaceVertex(r.Sg, r.V, 0xCDCDCDCDu, 0xCDCDCDCDu, [.. r.Adj]))],
            [.. frames], bc, br);
    }
}
