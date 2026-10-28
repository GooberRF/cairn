using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Lvl.Conversion;
using Cairn.Lvl.Model;
using Cairn.Viewport;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>How a batch of faces is drawn.</summary>
internal enum LvlBatchKind
{
    /// <summary>Lit like any face (full bright, the level's ambient, or shaded, as the view chooses).</summary>
    Opaque,
    /// <summary>A full-bright face: always drawn at the texture's own colours.</summary>
    FullBright,
    /// <summary>An alpha face: drawn after the opaque ones.</summary>
    Alpha,
    /// <summary>A liquid surface: drawn see-through, after the opaque ones.</summary>
    Liquid,
}

/// <summary>What the preview shows: which geometry and which face classes.</summary>
/// <param name="Compiled">The compiled geometry (rooms and movers) when true, else the editor brushes.</param>
/// <param name="HiddenClasses">Faces in any of these classes are not drawn.</param>
/// <param name="ShowNormalFaces">Draw faces in no class.</param>
internal sealed record LvlSceneSettings(bool Compiled, RfFaceClass HiddenClasses, bool ShowNormalFaces)
{
    /// <summary>The game draws neither portal nor invisible faces, so the preview hides them at first.</summary>
    public static LvlSceneSettings Default { get; } = new(true, RfFaceClass.Portal | RfFaceClass.Invisible, true);

    /// <summary>True when faces of class <paramref name="faceClass"/> (or ordinary faces for <see cref="RfFaceClass.Normal"/>) are drawn.</summary>
    public bool Shows(RfFaceClass faceClass) => faceClass == RfFaceClass.Normal ? ShowNormalFaces : (HiddenClasses & faceClass) == 0;

    /// <summary>The same settings with one class shown or hidden.</summary>
    public LvlSceneSettings With(RfFaceClass faceClass, bool show) => faceClass == RfFaceClass.Normal
        ? this with { ShowNormalFaces = show }
        : this with { HiddenClasses = show ? HiddenClasses & ~faceClass : HiddenClasses | faceClass };
}

/// <summary>One mesh of the preview: the faces of one texture drawn one way (a frozen mesh; large batches are split).</summary>
internal sealed record LvlPreviewBatch(string Texture, LvlBatchKind Kind, MeshGeometry3D Mesh, int Triangles);

/// <summary>A face the preview can pick, with the brush, mover or room it belongs to.</summary>
internal sealed record LvlPickTarget(LvlPolygon Polygon, LvlPart Part);

/// <summary>
/// A built preview: frozen meshes per texture and kind (made off the UI thread), the bounds, counts, and the triangles
/// again as plain numbers for picking.
/// </summary>
internal sealed class LvlPreviewScene
{
    public required IReadOnlyList<LvlPreviewBatch> Batches { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public required int Triangles { get; init; }
    public required int Faces { get; init; }
    /// <summary>Brushes, rooms and movers drawn.</summary>
    public required int Parts { get; init; }
    /// <summary>The distinct texture names drawn.</summary>
    public required IReadOnlyList<string> Textures { get; init; }
    /// <summary>Three corners (nine floats) per triangle, in batch order.</summary>
    internal required float[] TriangleCorners { get; init; }
    /// <summary>The pick target of each triangle (an index into <see cref="Targets"/>).</summary>
    internal required int[] TriangleTargets { get; init; }
    internal required IReadOnlyList<LvlPickTarget> Targets { get; init; }

    public bool IsEmpty => Triangles == 0;

    /// <summary>
    /// The face nearest along <paramref name="ray"/> that faces the ray (the side the game draws), or null.
    /// <paramref name="bothSides"/> also takes faces seen from behind.
    /// </summary>
    public (LvlPickTarget Target, float Distance)? Pick(WorldRay ray, bool bothSides = false)
    {
        var c = TriangleCorners;
        var o = ray.Origin;
        var d = ray.Direction;
        float best = float.MaxValue;
        int hit = -1;
        for (int t = 0, i = 0; i + 8 < c.Length; t++, i += 9)
        {
            var a = new Vector3(c[i], c[i + 1], c[i + 2]);
            var e1 = new Vector3(c[i + 3], c[i + 4], c[i + 5]) - a;
            var e2 = new Vector3(c[i + 6], c[i + 7], c[i + 8]) - a;
            // Front faces have cross(e1, e2) towards the viewer, so the ray runs against it.
            if (!bothSides && Vector3.Dot(Vector3.Cross(e1, e2), d) >= 0) continue;
            var p = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            float inv = 1 / det;
            var s = o - a;
            float u = Vector3.Dot(s, p) * inv;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(d, q) * inv;
            if (v < 0 || u + v > 1) continue;
            float dist = Vector3.Dot(e2, q) * inv;
            if (dist <= 1e-5f || dist >= best) continue;
            best = dist;
            hit = t;
        }
        return hit < 0 ? null : (Targets[TriangleTargets[hit]], best);
    }
}

/// <summary>
/// Builds the preview of a level or group file with the converters' shared rules (<see cref="LvlScene"/>): faces
/// chosen by their classes, brush transforms applied, per-corner UVs, smoothing-group normals. Meshes are made and
/// frozen on the calling (worker) thread.
/// </summary>
internal static class LvlSceneBuilder
{
    /// <summary>WPF meshes stay below this many vertices (larger batches are split).</summary>
    private const int MaxVerticesPerMesh = 60_000;

    public static LvlPreviewScene Build(LvlSource source, LvlSceneSettings settings, CancellationToken ct)
    {
        var options = new LvlConvertOptions
        {
            Geometry = settings.Compiled && !source.IsGroupFile ? LvlGeometryChoice.Compiled : LvlGeometryChoice.Brushes,
            ExcludedFaceClasses = settings.HiddenClasses,
            IncludeNormalFaces = settings.ShowNormalFaces,
            IncludeMovers = true,
            Objects = LvlObjectKinds.None,
            ExportTextures = false,
        };
        var scene = LvlScene.Build(source.Input, options, applyTransforms: true, new LvlReportBuilder(), ct);

        var batches = new Dictionary<(string Texture, LvlBatchKind Kind), BatchBuilder>();
        var order = new List<BatchBuilder>();
        var targets = new List<LvlPickTarget>();
        var corners = new List<float>();
        var triangleTargets = new List<int>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        // The sky room sits far outside the playable space: frame without it unless it is all there is.
        var skyMin = new Vector3(float.MaxValue);
        var skyMax = new Vector3(float.MinValue);
        int faces = 0, skyFaces = 0;
        var skyArea = source.Level is { } level ? LvlSource.SkyArea(level.StaticGeometry) : [];
        foreach (var part in scene.Parts)
        {
            ct.ThrowIfCancellationRequested();
            bool sky = part.Kind == LvlPartKind.Room && part.RoomIndex >= 0 && part.RoomIndex < skyArea.Length && skyArea[part.RoomIndex];
            foreach (var polygon in part.Polygons)
            {
                var pc = polygon.Corners;
                if (pc.Length < 3) continue;
                var kind = KindOf(polygon.Classes);
                var key = (polygon.Texture.ToLowerInvariant(), kind);
                if (!batches.TryGetValue(key, out var batch))
                {
                    batch = new BatchBuilder(polygon.Texture, kind);
                    batches[key] = batch;
                    order.Add(batch);
                }
                int target = targets.Count;
                targets.Add(new LvlPickTarget(polygon, part));
                batch.Add(pc);
                faces++;
                for (int k = 1; k + 1 < pc.Length; k++)
                {
                    AddCorner(corners, pc[0].Position);
                    AddCorner(corners, pc[k].Position);
                    AddCorner(corners, pc[k + 1].Position);
                    triangleTargets.Add(target);
                }
                if (sky) skyFaces++;
                foreach (var c in pc)
                {
                    if (sky)
                    {
                        skyMin = Vector3.Min(skyMin, c.Position);
                        skyMax = Vector3.Max(skyMax, c.Position);
                    }
                    else
                    {
                        min = Vector3.Min(min, c.Position);
                        max = Vector3.Max(max, c.Position);
                    }
                }
            }
        }
        if (faces == 0) min = max = Vector3.Zero;
        else if (faces == skyFaces) (min, max) = (skyMin, skyMax);

        var meshes = new List<LvlPreviewBatch>();
        // Opaque first, then the see-through kinds (WPF draws in order and does not sort).
        foreach (var batch in order.OrderBy(b => b.Kind is LvlBatchKind.Alpha or LvlBatchKind.Liquid ? 1 : 0))
        {
            ct.ThrowIfCancellationRequested();
            meshes.AddRange(batch.Finish());
        }
        return new LvlPreviewScene
        {
            Batches = meshes,
            Min = min,
            Max = max,
            Triangles = triangleTargets.Count,
            Faces = faces,
            Parts = scene.Parts.Count,
            Textures = [.. order.Select(b => b.Texture).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)],
            TriangleCorners = [.. corners],
            TriangleTargets = [.. triangleTargets],
            Targets = targets,
        };
    }

    /// <summary>A liquid surface or alpha face is see-through; a full-bright face ignores the lighting.</summary>
    internal static LvlBatchKind KindOf(RfFaceClass classes) =>
        (classes & RfFaceClass.LiquidSurface) != 0 ? LvlBatchKind.Liquid
        : (classes & RfFaceClass.Alpha) != 0 ? LvlBatchKind.Alpha
        : (classes & RfFaceClass.FullBright) != 0 ? LvlBatchKind.FullBright
        : LvlBatchKind.Opaque;

    private static void AddCorner(List<float> list, Vector3 p)
    {
        list.Add(p.X);
        list.Add(p.Y);
        list.Add(p.Z);
    }

    /// <summary>Collects one batch's polygons as a triangle fan each, in RF corner order (the order the game treats as front).</summary>
    private sealed class BatchBuilder(string texture, LvlBatchKind kind)
    {
        private readonly List<LvlPreviewBatch> _done = [];
        private List<Point3D> _positions = [];
        private List<Vector3D> _normals = [];
        private List<Point> _uvs = [];
        private List<int> _indices = [];

        public string Texture { get; } = texture;
        public LvlBatchKind Kind { get; } = kind;

        public void Add(LvlCorner[] corners)
        {
            if (_positions.Count + corners.Length > MaxVerticesPerMesh) Flush();
            int start = _positions.Count;
            foreach (var c in corners)
            {
                _positions.Add(new Point3D(c.Position.X, c.Position.Y, c.Position.Z));
                _normals.Add(new Vector3D(c.Normal.X, c.Normal.Y, c.Normal.Z));
                _uvs.Add(new Point(Finite(c.Uv.X), Finite(c.Uv.Y)));
            }
            for (int k = 1; k + 1 < corners.Length; k++)
            {
                _indices.Add(start);
                _indices.Add(start + k);
                _indices.Add(start + k + 1);
            }
        }

        public IReadOnlyList<LvlPreviewBatch> Finish()
        {
            Flush();
            return _done;
        }

        private void Flush()
        {
            if (_indices.Count == 0) return;
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(_positions),
                Normals = new Vector3DCollection(_normals),
                TextureCoordinates = new PointCollection(_uvs),
                TriangleIndices = new Int32Collection(_indices),
            };
            mesh.Freeze();
            _done.Add(new LvlPreviewBatch(Texture, Kind, mesh, _indices.Count / 3));
            _positions = [];
            _normals = [];
            _uvs = [];
            _indices = [];
        }

        private static double Finite(float v) => float.IsFinite(v) ? v : 0;
    }

    /// <summary>"12,345" in the current culture.</summary>
    internal static string N(long n) => n.ToString("N0", CultureInfo.CurrentCulture);
}
