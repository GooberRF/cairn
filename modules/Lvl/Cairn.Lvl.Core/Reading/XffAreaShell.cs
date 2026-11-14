using Cairn.Formats.Obj;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>One triangle of an N-Gage level mesh in Red Faction space (corners wound the Red Faction way), with its face data.</summary>
internal sealed record XffMeshTriangle((Vector3 P, Vector2 Uv, Vector3 N)[] Corners, string Texture, RfFaceFlags Flags, uint Smoothing);

/// <summary>
/// Turns the walls, floors and ceilings of one N-Gage area into an air brush, as Red Faction's rooms are made: corners
/// within <see cref="Weld"/> are joined, a corner lying on another face's edge is added to that face (a T-junction), and
/// faces join across their edges; where more than two faces meet at an edge (a floor against a wall that goes on below
/// it, a fin), each joins the one facing it across the air. The largest piece is the room. Each hole is closed
/// <see cref="HoleDepth"/> out: a flat hole by a short tunnel, one that bends round a corner by a low pyramid, a hole
/// that passes a corner twice as two holes. A doorway's (as the caller decides) reaches into the next area, so
/// neighbouring rooms overlap; any other hole's is a recess, so what fills the opening (glass, a grate, a placed door)
/// stays in front. Each side carries on the wall beside it, so a doorway's sides that lie on the next room's walls (in a
/// hallway) look like them; only a doorway's far end, inside the next room, is invisible. The result must be closed and
/// face inward.
/// </summary>
internal static class XffAreaShell
{
    /// <summary>Corners closer than this (Red Faction metres) are one corner.</summary>
    public const float Weld = 0.01f;

    /// <summary>How deep the tunnel that closes a hole is (into the next area for a doorway), in Red Faction metres.</summary>
    public const float HoleDepth = 0.25f;

    /// <summary>The problem of an area with no walls of its own (only props, or see-through walls): it needs no air brush.</summary>
    public const string NoWalls = "no walls of its own";

    /// <summary>A hole whose corners are within this of its plane is flat, closed by a tunnel.</summary>
    private const float Flat = 0.01f;

    /// <summary>A face closing a hole whose normal is this close (cosine) to the wall's beside it lies in that wall's plane.</summary>
    private const float InPlane = 0.99f;

    /// <summary>The smallest squared Newell length (twice the area) a face that closes a hole may have: RED drops faces with less.</summary>
    private const float MinArea2 = 1e-10f;

    /// <summary>The room as an air brush and the triangles (indices into the input) it is made of, or why the area is not one.</summary>
    public sealed record Result(RfgSourceBrush? Brush, IReadOnlySet<int> Used, int Doorways, int Sealed, string? Problem);

    private sealed record Polygon(int[] Vertices, Vector2[] Uvs, int Triangle);

    /// <summary>The area's room from its triangles; <paramref name="isDoorway"/> tells a hole (its corners, in order) that leads into another area.</summary>
    public static Result Build(string label, IReadOnlyList<XffMeshTriangle> triangles, Func<IReadOnlyList<Vector3>, bool> isDoorway)
    {
        var none = new HashSet<int>();
        var positions = new List<Vector3>();
        var cells = new Dictionary<(int, int, int), List<int>>();
        int Vertex(Vector3 p)
        {
            var cell = ((int)MathF.Floor(p.X / Weld), (int)MathF.Floor(p.Y / Weld), (int)MathF.Floor(p.Z / Weld));
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    for (int dz = -1; dz <= 1; dz++)
                        if (cells.TryGetValue((cell.Item1 + dx, cell.Item2 + dy, cell.Item3 + dz), out var near))
                            foreach (int i in near)
                                if (Vector3.DistanceSquared(positions[i], p) <= Weld * Weld) return i;
            positions.Add(p);
            if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = [];
            list.Add(positions.Count - 1);
            return positions.Count - 1;
        }

        // the corner p lies on the edge from a to b (strictly between them, within the weld), and how far along
        bool OnEdge(Vector3 p, Vector3 a, Vector3 b, out float along)
        {
            var edge = b - a;
            along = Vector3.Dot(p - a, edge) / edge.LengthSquared();
            return along > 1e-4f && along < 1 - 1e-4f && Vector3.DistanceSquared(a + edge * along, p) < Weld * Weld;
        }

        // welded triangles; one with no area (a corner on its own opposite edge) is left out, its neighbours' edges take its corners in
        var welded = new List<(int[] V, int Triangle)>();
        for (int t = 0; t < triangles.Count; t++)
        {
            var c = triangles[t].Corners;
            if (c.Any(x => !RfgBrushBuilder.Finite(x.P))) continue;
            int[] v = [Vertex(c[0].P), Vertex(c[1].P), Vertex(c[2].P)];
            if (v[0] == v[1] || v[1] == v[2] || v[0] == v[2]) continue;
            if (Enumerable.Range(0, 3).Any(k => OnEdge(positions[v[(k + 2) % 3]], positions[v[k]], positions[v[(k + 1) % 3]], out _))) continue;
            welded.Add((v, t));
        }
        // a triangle given twice (or back to back, a two-sided panel) is not a wall of the room
        welded = [.. welded.GroupBy(w => (w.V.Min(), w.V.Sum() - w.V.Min() - w.V.Max(), w.V.Max())).Where(g => g.Count() == 1).SelectMany(g => g)];
        // each edge with the corners that lie on it (a T-junction)
        var corners = welded.SelectMany(w => w.V).Distinct().ToList();
        var polygons = new List<Polygon>();
        foreach (var (v, t) in welded)
        {
            var source = triangles[t].Corners;
            // each corner lying on an edge goes on that edge (on the nearest, if it lies on two by a sharp tip)
            var on = new List<(int Edge, float T, int V)>();
            foreach (int p in corners)
            {
                if (v.Contains(p)) continue;
                int best = -1;
                float bestAlong = 0, bestDistance = float.MaxValue;
                for (int k = 0; k < 3; k++)
                {
                    var a = positions[v[k]];
                    var b = positions[v[(k + 1) % 3]];
                    if (!OnEdge(positions[p], a, b, out float along)) continue;
                    float distance = Vector3.DistanceSquared(a + (b - a) * along, positions[p]);
                    if (distance < bestDistance) (best, bestAlong, bestDistance) = (k, along, distance);
                }
                if (best >= 0) on.Add((best, bestAlong, p));
            }
            var ring = new List<int>();
            var uvs = new List<Vector2>();
            for (int k = 0; k < 3; k++)
            {
                ring.Add(v[k]);
                uvs.Add(source[k].Uv);
                foreach (var (_, along, p) in on.Where(x => x.Edge == k).OrderBy(x => x.T))
                {
                    ring.Add(p);
                    uvs.Add(Vector2.Lerp(source[k].Uv, source[(k + 1) % 3].Uv, along));
                }
            }
            polygons.Add(new Polygon([.. ring], [.. uvs], t));
        }
        if (polygons.Count == 0) return new Result(null, none, 0, 0, NoWalls);

        // the faces around each edge: two that meet once each way join; where more meet (a floor against a wall that goes
        // on below it, a fin on a wall), each face joins the one facing it across the air in front of it
        var around = new Dictionary<(int, int), List<(int Polygon, int Edge, bool Forward)>>();
        for (int i = 0; i < polygons.Count; i++)
        {
            var ring = polygons[i].Vertices;
            for (int k = 0; k < ring.Length; k++)
            {
                int a = ring[k], b = ring[(k + 1) % ring.Length];
                var key = a < b ? (a, b) : (b, a);
                if (!around.TryGetValue(key, out var list)) around[key] = list = [];
                list.Add((i, k, a < b));
            }
        }
        var parent = Enumerable.Range(0, polygons.Count).ToArray();
        int Root(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }
        var partner = new Dictionary<(int, int), int>();
        void Join((int Polygon, int Edge, bool Forward) x, (int Polygon, int Edge, bool Forward) y)
        {
            partner[(x.Polygon, x.Edge)] = y.Polygon;
            partner[(y.Polygon, y.Edge)] = x.Polygon;
            parent[Root(x.Polygon)] = Root(y.Polygon);
        }
        foreach (var (key, list) in around)
        {
            if (list.Count == 2)
            {
                if (list[0].Forward != list[1].Forward) Join(list[0], list[1]);
                continue;
            }
            if (list.Count < 2) continue;
            var origin = positions[key.Item1];
            var axis = Vector3.Normalize(positions[key.Item2] - origin);
            var across = Vector3.Normalize(Vector3.Cross(axis, MathF.Abs(axis.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var up = Vector3.Cross(axis, across);
            var sorted = list.Select(o =>
            {
                var ring = polygons[o.Polygon].Vertices;
                var wing = ring.Aggregate(Vector3.Zero, (sum, v) => sum + positions[v]) / ring.Length - origin;
                wing -= axis * Vector3.Dot(wing, axis);
                bool airAhead = Vector3.Dot(RfgBrushBuilder.Newell([.. ring.Select(v => positions[v])]), Vector3.Cross(axis, wing)) > 0;
                return (Occurrence: o, Angle: MathF.Atan2(Vector3.Dot(wing, up), Vector3.Dot(wing, across)), AirAhead: airAhead);
            }).OrderBy(x => x.Angle).ToList();
            for (int i = 0; i < sorted.Count; i++)
            {
                var f = sorted[i];
                var g = sorted[(i + 1) % sorted.Count];
                if (f.AirAhead && !g.AirAhead && f.Occurrence.Forward != g.Occurrence.Forward) Join(f.Occurrence, g.Occurrence);
            }
        }

        // the room: the largest piece by area. An edge of one of its faces is joined to another of its faces, or is an edge
        // of a hole (two such edges that run opposite ways close each other); where the room touches itself along an
        // edge, two joined pairs share it, which still closes
        double Area(int i) => RfgBrushBuilder.Newell([.. polygons[i].Vertices.Select(v => positions[v])]).Length() / 2.0;
        var room = Enumerable.Range(0, polygons.Count).GroupBy(Root).MaxBy(g => g.Sum(Area))!.ToList();
        var open = new Dictionary<(int, int), List<int>>();
        int directedEdges = 0;
        foreach (int i in room)
        {
            var ring = polygons[i].Vertices;
            for (int k = 0; k < ring.Length; k++)
            {
                directedEdges++;
                if (partner.ContainsKey((i, k))) continue;
                var e = (ring[k], ring[(k + 1) % ring.Length]);
                if (open.TryGetValue((e.Item2, e.Item1), out var back) && back.Count > 0)
                {
                    back.RemoveAt(back.Count - 1);
                    continue;
                }
                if (!open.TryGetValue(e, out var list)) open[e] = list = [];
                list.Add(i);
            }
        }
        var owner = new Dictionary<(int, int), int>();
        var next = new Dictionary<int, List<int>>();
        foreach (var ((a, b), list) in open)
            foreach (int i in list)
            {
                owner[(a, b)] = i;
                if (!next.TryGetValue(a, out var outs)) next[a] = outs = [];
                outs.Add(b);
            }

        // the holes, each as its corners in the order of the room's own edges around it
        var holes = new List<List<int>>();
        foreach (int start in next.Keys.ToList())
        {
            while (next[start].Count > 0)
            {
                var hole = new List<int>();
                int v = start;
                do
                {
                    if (!next.TryGetValue(v, out var outs) || outs.Count == 0) return new Result(null, none, 0, 0, "an open edge that does not close into a hole");
                    int w = outs[^1];
                    outs.RemoveAt(outs.Count - 1);
                    hole.Add(v);
                    v = w;
                }
                while (v != start && hole.Count <= directedEdges);
                if (v != start) return new Result(null, none, 0, 0, "an open edge that does not close into a hole");
                // a hole that passes a corner twice (a figure of eight) is two holes meeting there
                var stack = new Stack<List<int>>([hole]);
                while (stack.Count > 0)
                {
                    var loop = stack.Pop();
                    int first = -1, second = -1;
                    for (int i = 0; i < loop.Count && second < 0; i++)
                        for (int j = i + 1; j < loop.Count; j++)
                            if (loop[i] == loop[j])
                            {
                                (first, second) = (i, j);
                                break;
                            }
                    if (second < 0)
                    {
                        holes.Add(loop);
                        continue;
                    }
                    stack.Push(loop.GetRange(first, second - first));
                    stack.Push([.. loop.Take(first), .. loop.Skip(second)]);
                }
            }
        }

        // a room is mostly wall: holes as large as its faces (a lone floor, a short open frame) do not make one
        double wall = room.Sum(Area);
        double holeArea = holes.Sum(h => RfgBrushBuilder.Newell([.. h.Select(v => positions[v])]).Length() / 2.0);
        if (holeArea >= wall) return new Result(null, none, 0, 0, "more hole than wall");

        // the faces of the brush: the room's own (one with more corners than RED keeps as a fan around its middle), then
        // what closes each hole
        var faces = new List<(int[] V, Vector2[] Uv, string Texture, RfFaceFlags Flags, uint Smoothing)>();
        foreach (int i in room)
        {
            var p = polygons[i];
            var t = triangles[p.Triangle];
            if (p.Vertices.Length <= RfgBrushBuilder.MaxFaceCorners)
            {
                faces.Add((p.Vertices, p.Uvs, t.Texture, t.Flags, t.Smoothing));
                continue;
            }
            positions.Add(p.Vertices.Aggregate(Vector3.Zero, (sum, v) => sum + positions[v]) / p.Vertices.Length);
            int middle = positions.Count - 1;
            var uv = p.Uvs.Aggregate(Vector2.Zero, (sum, x) => sum + x) / p.Uvs.Length;
            for (int k = 0; k < p.Vertices.Length; k++)
                faces.Add(([middle, p.Vertices[k], p.Vertices[(k + 1) % p.Vertices.Length]], [uv, p.Uvs[k], p.Uvs[(k + 1) % p.Vertices.Length]], t.Texture, t.Flags, t.Smoothing));
        }
        // a face that closes a hole beside a room face: in that face's plane (a tunnel's side running on along a hallway's
        // wall) it carries on its texture coordinates and smoothing, so it matches the next room's wall it lies on; any
        // other (a recess's side, a pyramid's) is mapped flat
        (Vector2[] Uv, uint Smoothing) Beside(int[] ring, int polygon)
        {
            var points = ring.Select(v => positions[v]).ToList();
            var t = triangles[polygons[polygon].Triangle];
            var (p0, p1, p2) = (t.Corners[0].P, t.Corners[1].P, t.Corners[2].P);
            var (e1, e2) = (p1 - p0, p2 - p0);
            float d00 = Vector3.Dot(e1, e1), d01 = Vector3.Dot(e1, e2), d11 = Vector3.Dot(e2, e2), det = d00 * d11 - d01 * d01;
            if (!(det > 1e-6f * d00 * d11) || !(Vector3.Dot(Vector3.Normalize(Vector3.Cross(e1, e2)), Vector3.Normalize(RfgBrushBuilder.Newell(points))) > InPlane))
                return (Planar(points), 0);
            var (uv0, uv1, uv2) = (t.Corners[0].Uv, t.Corners[1].Uv, t.Corners[2].Uv);
            return ([.. points.Select(p =>
            {
                float d20 = Vector3.Dot(p - p0, e1), d21 = Vector3.Dot(p - p0, e2);
                return uv0 + (uv1 - uv0) * ((d11 * d20 - d01 * d21) / det) + (uv2 - uv0) * ((d00 * d21 - d01 * d20) / det);
            })], t.Smoothing);
        }

        int doorways = 0, sealedHoles = 0;
        foreach (var hole in holes)
        {
            var points = hole.Select(v => positions[v]).ToList();
            var outward = RfgBrushBuilder.Newell(points);
            if (!(outward.LengthSquared() > 1e-12f)) return new Result(null, none, 0, 0, "a hole with no area");
            outward = Vector3.Normalize(outward);
            // into the next area or a recess: each side textured like the wall beside its edge; the far end of a doorway's
            // (inside the next room, which the build opens) invisible, of a recess's like the wall beside the hole
            bool doorway = isDoorway(points);
            if (doorway) doorways++;
            else sealedHoles++;
            int Wall(int k) => owner[(hole[k], hole[(k + 1) % hole.Count])];
            int far = doorway ? -1 : Wall(0);
            bool flat = points.All(p => MathF.Abs(Vector3.Dot(p - points[0], outward)) < Flat);
            if (!flat)
            {
                // a hole that bends (round a corner of the room): a low pyramid HoleDepth out from its middle
                positions.Add(points.Aggregate(Vector3.Zero, (sum, x) => sum + x) / points.Count + outward * HoleDepth);
                int apex = positions.Count - 1;
                for (int k = 0; k < hole.Count; k++)
                    if (!Add([hole[(k + 1) % hole.Count], hole[k], apex], Wall(k))) return new Result(null, none, 0, 0, "a hole that bends too sharply to close");
                continue;
            }
            // a flat hole: a tunnel HoleDepth deep, a side for each edge of the hole, then its far end
            var cap = new int[hole.Count];
            for (int k = 0; k < hole.Count; k++)
            {
                positions.Add(positions[hole[k]] + outward * HoleDepth);
                cap[k] = positions.Count - 1;
            }
            for (int k = 0; k < hole.Count; k++)
                if (!Add([hole[(k + 1) % hole.Count], hole[k], cap[k], cap[(k + 1) % hole.Count]], Wall(k))) return new Result(null, none, 0, 0, "a hole edge with no length");
            int[] end = [.. cap.Reverse()];
            if (end.Length <= RfgBrushBuilder.MaxFaceCorners)
            {
                Add(end, far);
                continue;
            }
            // more corners than RED keeps: triangles that keep every edge (ear clipping, else a fan around the middle)
            var endPoints = end.Select(v => positions[v]).ToArray();
            var endEdges = Edges(end).ToHashSet();
            List<int[]> pieces = [.. ObjTriangulator.Triangulate(endPoints).Select(x => new[] { end[x.A], end[x.B], end[x.C] })];
            bool Keeps(List<int[]> tris) => endEdges.IsSubsetOf(tris.SelectMany(Edges)) && tris.All(x => HasArea(x));
            if (!Keeps(pieces)) pieces = [.. pieces.Select(x => new[] { x[0], x[2], x[1] })];
            if (!Keeps(pieces))
            {
                positions.Add(endPoints.Aggregate(Vector3.Zero, (sum, x) => sum + x) / endPoints.Length);
                int middle = positions.Count - 1;
                pieces = [.. Enumerable.Range(0, end.Length).Select(k => new[] { middle, end[k], end[(k + 1) % end.Length] })];
                if (!Keeps(pieces)) return new Result(null, none, 0, 0, "a hole too ragged to close");
            }
            foreach (var piece in pieces) Add(piece, far);

            bool HasArea(int[] ring) => RfgBrushBuilder.Newell([.. ring.Select(v => positions[v])]).LengthSquared() > MinArea2;
            // a face beside the room face wall, or (wall -1) a doorway's invisible far end
            bool Add(int[] ring, int wall)
            {
                if (!HasArea(ring)) return false;
                if (wall < 0)
                {
                    faces.Add((ring, Planar(ring.Select(v => positions[v])), XffLevelReader.AirTexture, RfFaceFlags.Invisible, 0));
                    return true;
                }
                var (uv, smoothing) = Beside(ring, wall);
                faces.Add((ring, uv, triangles[polygons[wall].Triangle].Texture, RfFaceFlags.None, smoothing));
                return true;
            }
        }

        // closed, and the faces point in (as RED stores an air brush)
        var geometry = new RfGeometry { Vertices = [.. positions], Faces = [.. faces.Select(f => new RfFace { Corners = [.. f.V.Select(v => new RfCorner(v, Vector2.Zero))] })] };
        var (closed, volume) = Sealed(geometry);
        if (!closed) return new Result(null, none, 0, 0, "not closed once its holes were closed");
        // the largest piece faces out: a prop, so the area's walls (if any) are not among these faces
        if (volume >= 0) return new Result(null, none, 0, 0, NoWalls);

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var f in faces)
            foreach (int v in f.V)
            {
                min = Vector3.Min(min, positions[v]);
                max = Vector3.Max(max, positions[v]);
            }
        var centre = (min + max) / 2;
        var brush = new RfgSourceBrush { Name = label, Position = centre, SolidFlags = RfSolidFlags.Air };
        foreach (var f in faces)
            brush.Faces.Add(new RfgSourceFace([.. f.V.Select((v, k) => new RfgSourceCorner(positions[v] - centre, f.Uv[k]))], f.Texture) { Flags = f.Flags, Smoothing = f.Smoothing });
        return new Result(brush, room.Select(i => polygons[i].Triangle).ToHashSet(), doorways, sealedHoles, null);
    }

    /// <summary>
    /// Whether every edge is used as often one way as the other (a closed surface; unlike <see cref="RfgBrushBuilder.Closure"/>
    /// it allows a room that touches itself along an edge), and the enclosed volume (negative when the faces point in).
    /// </summary>
    public static (bool Closed, double Volume) Sealed(RfGeometry g)
    {
        var edges = new Dictionary<(int, int), int>();
        double volume = 0;
        foreach (var f in g.Faces)
        {
            var c = f.Corners;
            for (int k = 0; k < c.Length; k++)
            {
                var key = (c[k].Vertex, c[(k + 1) % c.Length].Vertex);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
            var p0 = g.Vertices[c[0].Vertex];
            for (int k = 1; k + 1 < c.Length; k++)
                volume += Vector3.Dot(p0, Vector3.Cross(g.Vertices[c[k].Vertex], g.Vertices[c[k + 1].Vertex])) / 6.0;
        }
        return (edges.Count > 0 && edges.All(e => edges.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == e.Value), volume);
    }

    private static IEnumerable<(int, int)> Edges(int[] ring)
    {
        for (int k = 0; k < ring.Length; k++) yield return (ring[k], ring[(k + 1) % ring.Length]);
    }

    /// <summary>Texture coordinates for a face that closes a hole: its corners on the plane of the axes it faces least (one repeat per 4 m).</summary>
    private static Vector2[] Planar(IEnumerable<Vector3> points)
    {
        var list = points.ToList();
        var n = RfgBrushBuilder.Newell(list);
        var a = Vector3.Abs(n);
        return [.. list.Select(p => (a.X >= a.Y && a.X >= a.Z ? new Vector2(p.Z, p.Y) : a.Y >= a.Z ? new Vector2(p.X, p.Z) : new Vector2(p.X, p.Y)) / 4f)];
    }
}
