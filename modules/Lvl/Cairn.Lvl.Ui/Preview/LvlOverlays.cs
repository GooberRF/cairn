using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Viewport;

namespace Cairn.Lvl.Ui.Preview;

/// <summary>
/// Object markers in the 3D scene: a small diamond per object that always faces the camera and keeps the same size on
/// screen, coloured by kind from the theme (lights, spawns, items, entities, clutter, triggers and events, the rest).
/// They are part of the 3D scene, so walls hide the markers behind them. Rebuilt each time the camera moves (one mesh
/// per kind; a few thousand markers cost well under a millisecond).
/// </summary>
internal sealed class LvlMarkerMeshes
{
    /// <summary>Half the diamond's size on screen, in pixels.</summary>
    private const double PixelRadius = 4;

    private readonly Dictionary<LvlMarkerKinds, (GeometryModel3D Model, Brush Brush)> _models = [];
    private readonly FrameworkElement _resources;

    public LvlMarkerMeshes(FrameworkElement resources) => _resources = resources;

    /// <summary>The models to add to the scene.</summary>
    public Model3DGroup Root { get; } = new();

    public IReadOnlyList<LvlMarker> Markers { get; set; } = [];

    public LvlMarkerKinds Shown { get; set; } = LvlMarkerKinds.Default;

    /// <summary>The number of markers drawn by the last <see cref="Update"/>.</summary>
    public int Drawn { get; private set; }

    /// <summary>Rebuilds the diamonds for <paramref name="camera"/> and a view of <paramref name="size"/>.</summary>
    public void Update(OrbitCamera camera, Size size)
    {
        var byKind = new Dictionary<LvlMarkerKinds, (Point3DCollection Positions, Int32Collection Indices)>();
        if (Markers.Count > 0 && Shown != LvlMarkerKinds.None && size.Height >= 1)
        {
            var eye = camera.Eye;
            var forward = camera.Forward;
            var right = camera.Right;
            var up = camera.Up;
            double tan = Math.Tan(OrbitCamera.FieldOfViewDegrees * Math.PI / 360);
            double perPixelOrtho = 2 * camera.OrthoHalfHeight / size.Height;
            foreach (var m in Markers)
            {
                if ((m.Kind & Shown) == 0) continue;
                var toMarker = m.Position - eye;
                float depth = Vector3.Dot(toMarker, forward);
                if (camera.IsPerspective && depth <= camera.Near) continue;
                double perPixel = camera.IsPerspective ? 2 * depth * tan / size.Height : perPixelOrtho;
                float s = (float)(PixelRadius * perPixel);
                // pulled towards the eye by its own size, so a marker on a floor is not cut by the floor
                var c = m.Position - Vector3.Normalize(toMarker) * s;
                if (!byKind.TryGetValue(m.Kind, out var lists)) byKind[m.Kind] = lists = (new Point3DCollection(), new Int32Collection());
                int start = lists.Positions.Count;
                Add(lists.Positions, c + right * s);
                Add(lists.Positions, c + up * s);
                Add(lists.Positions, c - right * s);
                Add(lists.Positions, c - up * s);
                lists.Indices.Add(start); lists.Indices.Add(start + 1); lists.Indices.Add(start + 2);
                lists.Indices.Add(start); lists.Indices.Add(start + 2); lists.Indices.Add(start + 3);
            }
        }
        foreach (var (kind, entry) in _models)
            if (!byKind.ContainsKey(kind)) entry.Model.Geometry = null;
        int drawn = 0;
        foreach (var (kind, (positions, indices)) in byKind)
        {
            drawn += positions.Count / 4;
            positions.Freeze();
            indices.Freeze();
            var mesh = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
            mesh.Freeze();
            // the theme's colour, looked up each time so a theme change shows at once
            var brush = _resources.TryFindResource(LvlMarkerLayer.BrushKey(kind)) as Brush ?? Brushes.Gray;
            if (!_models.TryGetValue(kind, out var entry))
            {
                entry = (new GeometryModel3D(), brush);
                Root.Children.Add(entry.Model);
                SetMaterial(entry.Model, brush);
            }
            else if (!ReferenceEquals(entry.Brush, brush)) SetMaterial(entry.Model, brush);
            _models[kind] = (entry.Model, brush);
            entry.Model.Geometry = mesh;
        }
        Drawn = drawn;
    }

    private static void Add(Point3DCollection list, Vector3 p) => list.Add(new Point3D(p.X, p.Y, p.Z));

    private static void SetMaterial(GeometryModel3D model, Brush brush)
    {
        // its own colour whatever the lighting: black diffuse plus the colour as emission
        var material = new MaterialGroup { Children = { new DiffuseMaterial(Brushes.Black), new EmissiveMaterial(brush) } };
        material.Freeze();
        model.Material = material;
        model.BackMaterial = material;
    }
}

/// <summary>
/// The picked object marker's ring over the 3D view, and hit testing of the markers on screen.
/// </summary>
internal sealed class LvlMarkerLayer : FrameworkElement
{
    private readonly ViewportSurface _surface;

    public LvlMarkerLayer(ViewportSurface surface)
    {
        _surface = surface;
        IsHitTestVisible = false;
    }

    public IReadOnlyList<LvlMarker> Markers { get; set; } = [];

    public LvlMarkerKinds Shown { get; set; } = LvlMarkerKinds.Default;

    /// <summary>The picked marker (drawn larger and outlined), or null.</summary>
    public LvlMarker? Selected { get; set; }

    /// <summary>The theme brush each kind uses (also the menu's swatches).</summary>
    public static string BrushKey(LvlMarkerKinds kind) => kind switch
    {
        LvlMarkerKinds.Lights => "Viewport.Joint",
        LvlMarkerKinds.Spawns => "Viewport.AxisY",
        LvlMarkerKinds.Items => "Viewport.AxisZ",
        LvlMarkerKinds.Entities => "Viewport.AxisX",
        LvlMarkerKinds.Clutter => "Viewport.Prop",
        LvlMarkerKinds.Logic => "Viewport.Sphere",
        _ => "Viewport.Bone",
    };

    /// <summary>
    /// The shown marker nearest to <paramref name="point"/> within <paramref name="radius"/> pixels (the nearer one to
    /// the eye when several are), with its distance from the eye; null when there is none.
    /// </summary>
    public (LvlMarker Marker, float Distance)? HitTest(Point point, double radius = 7)
    {
        var projector = _surface.CreateProjector();
        var eye = _surface.Camera.Eye;
        (LvlMarker, float)? best = null;
        double bestScore = double.MaxValue;
        foreach (var m in Markers)
        {
            if ((m.Kind & Shown) == 0 || !projector.Point(m.Position, out var s)) continue;
            double d = (s - point).Length;
            if (d > radius) continue;
            float distance = Vector3.Distance(eye, m.Position);
            // within the radius, the nearest to the eye wins (a marker in front of another)
            double score = distance + d * 1e-3;
            if (score < bestScore)
            {
                bestScore = score;
                best = (m, distance);
            }
        }
        return best;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Selected is not { } selected || ActualWidth < 2) return;
        var projector = _surface.CreateProjector();
        if (!projector.Point(selected.Position, out var at)) return;
        var pen = new Pen(TryFindResource("Viewport.Highlight") as Brush ?? Brushes.Orange, 2);
        pen.Freeze();
        dc.DrawEllipse(null, pen, at, 8, 8);
    }
}

/// <summary>The picked face's outline (and, for a brush or mover, the outline of its other faces), over the 3D view.</summary>
internal sealed class LvlSelectionLayer : FrameworkElement
{
    private readonly ViewportSurface _surface;

    public LvlSelectionLayer(ViewportSurface surface)
    {
        _surface = surface;
        IsHitTestVisible = false;
    }

    /// <summary>The picked face's corners (world space), or null.</summary>
    public Vector3[]? Face { get; set; }

    /// <summary>The other faces of the picked brush or mover (world space), drawn faintly.</summary>
    public IReadOnlyList<Vector3[]> Context { get; set; } = [];

    protected override void OnRender(DrawingContext dc)
    {
        if (Face is null && Context.Count == 0) return;
        var projector = _surface.CreateProjector();
        var highlight = TryFindResource("Viewport.Highlight") as Brush ?? Brushes.Orange;
        var faint = new Pen(highlight, 1) { DashStyle = DashStyles.Dash };
        faint.Freeze();
        foreach (var polygon in Context) Outline(dc, projector, polygon, faint);
        if (Face is { } face)
        {
            var strong = new Pen(highlight, 2.5);
            strong.Freeze();
            Outline(dc, projector, face, strong);
        }
    }

    private static void Outline(DrawingContext dc, Projector projector, Vector3[] corners, Pen pen)
    {
        for (int i = 0; i < corners.Length; i++)
            if (projector.Segment(corners[i], corners[(i + 1) % corners.Length], out var a, out var b)) dc.DrawLine(pen, a, b);
    }
}
