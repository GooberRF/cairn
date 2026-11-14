using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Cairn.Viewport;

/// <summary>
/// Edges drawn in a WPF 3D scene, which has no line primitive: each edge is a thin quad turned to face the camera, a
/// constant width on screen, remade by <see cref="Update"/> whenever the camera (or the points) move. The quads sit a
/// hair towards the camera, so a face's own edges show on it while nearer faces still hide the edges behind them.
/// </summary>
public sealed class WireframeLines
{
    /// <summary>WPF meshes stay below 60,000 vertices (four per edge).</summary>
    private const int EdgesPerMesh = 15_000;

    /// <summary>How far each quad moves towards the camera, as a share of its distance.</summary>
    private const float Lift = 0.002f;

    /// <summary>The colour of edges drawn over faces.</summary>
    public static Color OverFacesColor { get; } = Color.FromRgb(0x14, 0x14, 0x16);

    private readonly List<Chunk> _chunks = [];
    private Material? _material;
    private Color _color = OverFacesColor;

    // First: the chunk's first edge among its points' edges; Start: among all the edges
    private sealed record Chunk(Vector3[] Points, int[]? Edges, int First, int Start, int Count, MeshGeometry3D Mesh, Point3DCollection Positions, GeometryModel3D Model);

    /// <summary>The quads, to add to the scene after the faces.</summary>
    public Model3DGroup Root { get; } = new();

    /// <summary>The edges drawn.</summary>
    public int EdgeCount { get; private set; }

    /// <summary>The width on screen, in DIPs.</summary>
    public double Thickness { get; set; } = 1;

    /// <summary>The colour of the edges (unlit).</summary>
    public Color Color
    {
        get => _color;
        set
        {
            if (_color == value && _material is not null) return;
            _color = value;
            _material = null;
            foreach (var chunk in _chunks) chunk.Model.Material = chunk.Model.BackMaterial = Material;
        }
    }

    private Material Material
    {
        get
        {
            if (_material is not null) return _material;
            // black diffuse plus the colour as emission: the same colour under any light
            var brush = new SolidColorBrush(_color);
            brush.Freeze();
            var material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
            material.Freeze();
            return _material = material;
        }
    }

    /// <summary>A line colour that stands out on <paramref name="background"/>: light on a dark background, dark on a light one.</summary>
    public static Color ContrastWith(Color background) =>
        0.299 * background.R + 0.587 * background.G + 0.114 * background.B < 140 ? Color.FromRgb(0xD6, 0xDA, 0xE2) : Color.FromRgb(0x2A, 0x2D, 0x34);

    /// <summary>
    /// The unique edges of triangles given as index triples: each pair of indices once, whichever triangle and
    /// direction it came from.
    /// </summary>
    public static int[] TriangleEdges(IEnumerable<int> triangleIndices)
    {
        ArgumentNullException.ThrowIfNull(triangleIndices);
        var seen = new HashSet<long>();
        var edges = new List<int>();
        var corner = new int[3];
        int n = 0;
        foreach (int index in triangleIndices)
        {
            corner[n++] = index;
            if (n < 3) continue;
            n = 0;
            for (int k = 0; k < 3; k++)
            {
                int a = corner[k], b = corner[(k + 1) % 3];
                if (a == b) continue;
                if (seen.Add(a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a))
                {
                    edges.Add(a);
                    edges.Add(b);
                }
            }
        }
        return [.. edges];
    }

    /// <summary>Removes every edge.</summary>
    public void Clear()
    {
        _chunks.Clear();
        Root.Children.Clear();
        EdgeCount = 0;
    }

    /// <summary>
    /// Adds edges between <paramref name="points"/>: <paramref name="edges"/> holds index pairs, or with none the points
    /// themselves are the pairs. The array is kept, so the caller can move the points and call <see cref="Update"/>.
    /// </summary>
    public void Add(Vector3[] points, int[]? edges = null)
    {
        ArgumentNullException.ThrowIfNull(points);
        int count = (edges?.Length ?? points.Length) / 2;
        for (int first = 0; first < count; first += EdgesPerMesh)
        {
            int n = Math.Min(EdgesPerMesh, count - first);
            var positions = new Point3DCollection(n * 4);
            var indices = new Int32Collection(n * 6);
            for (int k = 0; k < n; k++)
            {
                for (int c = 0; c < 4; c++) positions.Add(default);
                int b = k * 4;
                indices.Add(b);
                indices.Add(b + 1);
                indices.Add(b + 2);
                indices.Add(b + 2);
                indices.Add(b + 1);
                indices.Add(b + 3);
            }
            indices.Freeze();
            var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            var model = new GeometryModel3D(mesh, Material) { BackMaterial = Material };
            _chunks.Add(new Chunk(points, edges, first, EdgeCount + first, n, mesh, positions, model));
            Root.Children.Add(model);
        }
        EdgeCount += count;
    }

    /// <summary>Turns every quad to face <paramref name="camera"/> in a view of <paramref name="size"/> (DIPs).</summary>
    public void Update(OrbitCamera camera, Size size)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_chunks.Count == 0) return;
        var eye = camera.Eye;
        var forward = camera.Forward;
        var right = camera.Right;
        bool perspective = camera.IsPerspective;
        float half = (float)Thickness * 0.5f;
        // world units per DIP: at a depth of one in perspective, anywhere in orthographic
        float perDepth = (float)(2 * Math.Tan(OrbitCamera.FieldOfViewDegrees * Math.PI / 360) / Math.Max(1, size.Height));
        float flat = (float)camera.UnitsPerPixel(size);
        float near = (float)camera.Near;
        var orthoLift = -forward * (float)(camera.Distance * Lift);

        (Vector3 Point, float Half) Place(Vector3 p)
        {
            if (!perspective) return (p + orthoLift, half * flat);
            float depth = Math.Max(Vector3.Dot(p - eye, forward), near);
            var toEye = eye - p;
            float length = toEye.Length();
            return (length > 1e-6f ? p + toEye * (depth * Lift / length) : p, half * perDepth * depth);
        }

        foreach (var chunk in _chunks)
        {
            // detached while written, so the writes raise no change notifications
            chunk.Mesh.Positions = null;
            var positions = chunk.Positions;
            var points = chunk.Points;
            for (int k = 0; k < chunk.Count; k++)
            {
                int e = (chunk.First + k) * 2;
                int ia = chunk.Edges?[e] ?? e, ib = chunk.Edges?[e + 1] ?? e + 1;
                int b = k * 4;
                if ((uint)ia >= (uint)points.Length || (uint)ib >= (uint)points.Length || !Finite(points[ia]) || !Finite(points[ib]))
                {
                    for (int c = 0; c < 4; c++) positions[b + c] = default;
                    continue;
                }
                Vector3 a = points[ia], z = points[ib];
                var side = Vector3.Cross(z - a, perspective ? (a + z) * 0.5f - eye : forward);
                float length = side.Length();
                side = length > 1e-12f ? side / length : right;
                var (pa, wa) = Place(a);
                var (pz, wz) = Place(z);
                positions[b] = P(pa + side * wa);
                positions[b + 1] = P(pa - side * wa);
                positions[b + 2] = P(pz + side * wz);
                positions[b + 3] = P(pz - side * wz);
            }
            chunk.Mesh.Positions = positions;
        }
    }

    /// <summary>The four corners of edge <paramref name="edge"/>'s quad as last placed (self-tests), or null.</summary>
    public Vector3[]? Quad(int edge)
    {
        foreach (var chunk in _chunks)
        {
            if (edge < chunk.Start || edge >= chunk.Start + chunk.Count) continue;
            int b = (edge - chunk.Start) * 4;
            return [.. Enumerable.Range(b, 4).Select(i => chunk.Positions[i]).Select(p => new Vector3((float)p.X, (float)p.Y, (float)p.Z))];
        }
        return null;
    }

    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);

    private static Point3D P(Vector3 p) => new(p.X, p.Y, p.Z);
}
