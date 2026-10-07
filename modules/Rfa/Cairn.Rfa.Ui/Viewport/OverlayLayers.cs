using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Formats.Maths;
using Quaternion = System.Numerics.Quaternion;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>What a viewport pick found.</summary>
internal enum PickKind
{
    Bone,
    Sphere,
    Prop,
}

/// <summary>One thing under the cursor: lower <paramref name="Score"/> is a better hit (screen pixels, roughly), then nearer the camera.</summary>
internal readonly record struct PickHit(PickKind Kind, int Index, double Score, double Depth);

/// <summary>
/// Everything drawn over the mesh: the skeleton (bones and joints; selection and hover highlighted),
/// bone names, collision spheres, prop points and the root motion path. It projects with the same
/// matrices as the 3D scene (<see cref="Projector"/>), so it sits exactly on the skinned mesh.
/// Also does the picking: <see cref="Pick"/> finds the joint or bone under a point, and
/// <see cref="PickAll"/> everything under it in a mesh tab (joints, collision spheres, prop points), best first.
/// </summary>
internal sealed class OverlayLayer : FrameworkElement
{
    private const double JointRadius = 4.0;
    private const double PickJointRadius = 9.0;
    private const double PickBoneDistance = 5.0;
    private const double PickPropRadius = 8.0;
    private const double PickOutlineBand = 6.0;

    private readonly Dictionary<string, FormattedText> _labels = new(StringComparer.Ordinal);
    private Point[] _screen = [];
    private bool[] _visible = [];
    private readonly List<PickHit> _hits = [];
    private Pen? _boneOutline, _bone, _boneSelected, _boneHover, _sphere, _sphereHighlight, _sphereHover, _prop, _rootPath, _propX, _propY, _propZ;
    private Brush? _joint, _jointSelected, _jointHover, _labelBackground, _labelText, _propFill;
    private double _dpi = 1;

    public OverlayLayer()
    {
        IsHitTestVisible = false;
    }

    public OrbitCamera? Camera { get; set; }

    public SceneViewModel? Scene { get; set; }

    /// <summary>The bone under the mouse, or -1.</summary>
    public int HoverBone { get; set; } = -1;

    /// <summary>The collision sphere under the mouse (mesh tabs), or -1.</summary>
    public int HoverSphere { get; set; } = -1;

    /// <summary>The prop point under the mouse (mesh tabs), or -1.</summary>
    public int HoverProp { get; set; } = -1;

    /// <summary>The screen circle a pose gizmo covers now (null: none); bone labels are kept outside it.</summary>
    public Func<(Point Centre, double Radius)?>? GizmoFootprint { get; set; }

    /// <summary>Milliseconds the last render took (frame-time report).</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>Forgets cached pens and labels (theme or DPI changed).</summary>
    public void ResetResources()
    {
        _bone = null;
        _labels.Clear();
        _ghostResources.Clear();
    }

    private readonly Dictionary<string, (Pen Pen, Brush Joint)> _ghostResources = new(StringComparer.Ordinal);

    private (Pen Pen, Brush Joint) GhostResources(string key)
    {
        if (_ghostResources.TryGetValue(key, out var cached)) return cached;
        var brush = Brush(key);
        var pen = Frozen(new Pen(brush, 2.0) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var result = (pen, brush);
        _ghostResources[key] = result;
        return result;
    }

    /// <summary>The bone whose joint (or segment) is under <paramref name="point"/>, or -1.</summary>
    public int Pick(Point point)
    {
        if (Scene?.Pose is not { } pose || Camera is null) return -1;
        Project(pose.World.Length);
        int best = -1;
        double bestDistance = PickJointRadius;
        for (int i = 0; i < pose.World.Length; i++)
        {
            if (!_visible[i]) continue;
            double d = (_screen[i] - point).Length;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        if (best >= 0) return best;
        bestDistance = PickBoneDistance;
        var parents = Scene.Skeleton.EffectiveParents;
        for (int i = 0; i < pose.World.Length; i++)
        {
            int p = parents[i];
            if (p < 0 || !_visible[i] || !_visible[p]) continue;
            double d = DistanceToSegment(point, _screen[p], _screen[i]);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>
    /// Everything pickable under <paramref name="point"/>, best first: joints (within 9 px; bone segments within
    /// 5 px only when no joint is), and with <paramref name="items"/> the prop points (within 8 px of the
    /// diamond) and collision spheres (on the outline, ±6 px, or inside it, last) that are shown or selected.
    /// Hits about as good are ordered nearer the camera first. A new list (a click keeps it to cycle through).
    /// </summary>
    public List<PickHit> PickAll(Point point, bool items) => [.. Collect(point, items)];

    /// <summary>The best hit under <paramref name="point"/> (hover), without allocating, or null.</summary>
    public PickHit? PickBest(Point point, bool items)
    {
        var hits = Collect(point, items);
        return hits.Count > 0 ? hits[0] : null;
    }

    private List<PickHit> Collect(Point point, bool items)
    {
        _hits.Clear();
        if (Scene is not { } scene || Camera is not { } camera || ActualWidth < 2 || ActualHeight < 2) return _hits;
        var projector = new Projector(camera, new Size(ActualWidth, ActualHeight));
        var pose = scene.Pose;
        double Depth(Vector3 p) => Vector3.Dot(p - camera.Eye, camera.Forward);

        if (pose is not null)
        {
            Project(pose.World.Length);
            bool joint = false;
            for (int i = 0; i < pose.World.Length; i++)
            {
                if (!_visible[i]) continue;
                double d = (_screen[i] - point).Length;
                if (d >= PickJointRadius) continue;
                joint = true;
                _hits.Add(new PickHit(PickKind.Bone, i, d, Depth(pose.World[i].Position)));
            }
            if (!joint)
            {
                var parents = scene.Skeleton.EffectiveParents;
                for (int i = 0; i < pose.World.Length; i++)
                {
                    int p = parents[i];
                    if (p < 0 || !_visible[i] || !_visible[p]) continue;
                    double d = DistanceToSegment(point, _screen[p], _screen[i]);
                    if (d < PickBoneDistance) _hits.Add(new PickHit(PickKind.Bone, i, PickJointRadius + d, Depth(pose.World[i].Position)));
                }
            }
        }

        if (items)
        {
            var display = scene.Display;
            var props = scene.PropPoints;
            for (int i = 0; i < props.Count; i++)
            {
                if (!display.ShowProps && i != scene.HighlightProp) continue;
                PropFrame(pose, props[i], out var origin, out _);
                if (!projector.Point(origin, out var o)) continue;
                double d = (o - point).Length;
                if (d < PickPropRadius) _hits.Add(new PickHit(PickKind.Prop, i, d, Depth(origin)));
            }
            if (scene.Mesh is { } mesh)
            {
                int index = 0;
                foreach (var sphere in mesh.CollisionSpheres)
                {
                    int s = index++;
                    if (!display.ShowSpheres && s != scene.HighlightSphere) continue;
                    var centre = Place(pose, sphere.BoneIndex, sphere.Position);
                    if (!projector.Point(centre, out var c) || !projector.Point(centre + camera.Up * Math.Max(0.001f, sphere.Radius), out var edge)) continue;
                    double r = (edge - c).Length, d = (point - c).Length;
                    double band = Math.Abs(d - r);
                    // The outline is the handle; inside counts too, after anything sharper under the cursor.
                    if (band <= PickOutlineBand) _hits.Add(new PickHit(PickKind.Sphere, s, 2 + band, Depth(centre)));
                    else if (d < r) _hits.Add(new PickHit(PickKind.Sphere, s, PickJointRadius + PickBoneDistance + 6 * d / Math.Max(1, r), Depth(centre)));
                }
            }
        }
        // Better hits first (in half-pixel steps); equally good ones nearer the camera first.
        _hits.Sort(static (a, b) =>
        {
            int byScore = Math.Round(a.Score * 2).CompareTo(Math.Round(b.Score * 2));
            return byScore != 0 ? byScore : a.Depth.CompareTo(b.Depth);
        });
        return _hits;
    }

    /// <summary>A prop point's model-space origin and orientation in <paramref name="pose"/> (its bone's frame, or the model's).</summary>
    private static void PropFrame(Cairn.Rfa.Animation.Pose? pose, Cairn.Rfa.Formats.V3d.V3dPropPoint prop, out Vector3 origin, out Quaternion world)
    {
        var rotation = prop.Rotation.LengthSquared() < 1e-8f ? Quaternion.Identity : Quat.Conj(Quaternion.Normalize(prop.Rotation));
        if (pose is not null && prop.ParentIndex >= 0 && prop.ParentIndex < pose.World.Length)
        {
            var bone = pose.World[prop.ParentIndex];
            origin = bone.TransformPoint(prop.Position);
            world = Quat.Mul(bone.Rotation, rotation);
        }
        else
        {
            origin = prop.Position;
            world = rotation;
        }
    }

    /// <summary>The screen point of a collision sphere's centre or a prop point's origin as drawn now, when visible (self-tests).</summary>
    internal bool TryGetItem(PickKind kind, int index, out Point point, out double screenRadius)
    {
        point = default;
        screenRadius = 0;
        if (Scene is not { } scene || Camera is not { } camera || ActualWidth < 2 || ActualHeight < 2) return false;
        var projector = new Projector(camera, new Size(ActualWidth, ActualHeight));
        if (kind == PickKind.Prop)
        {
            if (index < 0 || index >= scene.PropPoints.Count) return false;
            PropFrame(scene.Pose, scene.PropPoints[index], out var origin, out _);
            return projector.Point(origin, out point);
        }
        if (kind != PickKind.Sphere || scene.Mesh?.CollisionSpheres.ElementAtOrDefault(index) is not { } sphere) return false;
        var centre = Place(scene.Pose, sphere.BoneIndex, sphere.Position);
        if (!projector.Point(centre, out point) || !projector.Point(centre + camera.Up * Math.Max(0.001f, sphere.Radius), out var edge)) return false;
        screenRadius = (edge - point).Length;
        return true;
    }

    /// <summary>Screen position of a bone's joint (for framing the selection), when visible.</summary>
    public bool TryGetJoint(int bone, out Point point)
    {
        point = default;
        if (bone < 0 || bone >= _screen.Length || !_visible[bone]) return false;
        point = _screen[bone];
        return true;
    }

    private void Project(int count)
    {
        if (_screen.Length != count)
        {
            _screen = new Point[count];
            _visible = new bool[count];
        }
        if (Scene?.Pose is not { } pose || Camera is null) return;
        var projector = new Projector(Camera, new Size(ActualWidth, ActualHeight));
        for (int i = 0; i < count; i++) _visible[i] = projector.Point(pose.World[i].Position, out _screen[i]);
    }

    protected override void OnRender(DrawingContext dc)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (Scene is not { } scene || Camera is not { } camera || ActualWidth < 2 || ActualHeight < 2) return;
        EnsureResources();
        var display = scene.Display;
        var projector = new Projector(camera, new Size(ActualWidth, ActualHeight));
        var pose = scene.Pose;
        // Labels (bones, spheres, prop points) stay clear of a gizmo: its rings, arrows and grip would otherwise run through them.
        var gizmo = GizmoFootprint?.Invoke();

        // Root motion path.
        if (display.ShowRootPath && scene.RootPath.Count > 1)
        {
            var path = scene.RootPath;
            for (int i = 1; i < path.Count; i++)
            {
                if (projector.Segment(path[i - 1], path[i], out var a, out var b)) dc.DrawLine(_rootPath!, a, b);
            }
        }

        // Collision spheres: an outline circle of the projected radius at the bone's posed position.
        if ((display.ShowSpheres || scene.HighlightSphere >= 0) && scene.Mesh is { } mesh)
        {
            int index = 0;
            foreach (var sphere in mesh.CollisionSpheres)
            {
                bool highlighted = index == scene.HighlightSphere;
                if (display.ShowSpheres || highlighted)
                {
                    var centre = Place(pose, sphere.BoneIndex, sphere.Position);
                    if (projector.Point(centre, out var c) && projector.Point(centre + camera.Up * Math.Max(0.001f, sphere.Radius), out var edge))
                    {
                        double r = (edge - c).Length;
                        dc.DrawEllipse(null, highlighted ? _sphereHighlight : index == HoverSphere ? _sphereHover : _sphere, c, r, r);
                        if (display.ShowBoneNames || highlighted) Label(dc, sphere.Name.Text, new Point(c.X + r * 0.7, c.Y - r * 0.7), gizmo);
                    }
                }
                index++;
            }
        }

        // Prop points: a small tripod in the prop's orientation and a diamond.
        if (display.ShowProps || scene.HighlightProp >= 0)
        {
            var props = scene.PropPoints;
            float axis = (float)(camera.Distance * 0.03);
            for (int i = 0; i < props.Count; i++)
            {
                bool highlighted = i == scene.HighlightProp;
                if (!display.ShowProps && !highlighted) continue;
                var prop = props[i];
                PropFrame(pose, prop, out var origin, out var world);
                if (!projector.Point(origin, out var o)) continue;
                if (projector.Segment(origin, origin + Quat.Rotate(world, Vector3.UnitX) * axis, out var a1, out var b1)) dc.DrawLine(_propX!, a1, b1);
                if (projector.Segment(origin, origin + Quat.Rotate(world, Vector3.UnitY) * axis, out var a2, out var b2)) dc.DrawLine(_propY!, a2, b2);
                if (projector.Segment(origin, origin + Quat.Rotate(world, Vector3.UnitZ) * axis, out var a3, out var b3)) dc.DrawLine(_propZ!, a3, b3);
                bool hovered = i == HoverProp;
                Diamond(dc, o, highlighted || hovered ? 6 : 4, highlighted ? _jointSelected! : hovered ? _jointHover! : _propFill!);
                if (display.ShowBoneNames || highlighted) Label(dc, prop.Name.Text, new Point(o.X + 6, o.Y - 16), gizmo);
            }
        }

        // Ghost skeletons (the unedited clip, a compared clip): thin and translucent, under the live one.
        foreach (var ghost in scene.Ghosts)
        {
            if (ghost.Pose is not { } ghostPose) continue;
            var (ghostPen, ghostJoint) = GhostResources(ghost.BrushKey);
            var gparents = (ghost.Skeleton ?? scene.Skeleton).EffectiveParents;
            int gn = Math.Min(ghostPose.World.Length, gparents.Length);
            var shift = ghost.Offset;
            for (int i = 0; i < gn; i++)
            {
                int p = gparents[i];
                if (p < 0) continue;
                if (projector.Segment(ghostPose.World[p].Position + shift, ghostPose.World[i].Position + shift, out var a, out var b)) dc.DrawLine(ghostPen, a, b);
            }
            for (int i = 0; i < gn; i++)
            {
                if (projector.Point(ghostPose.World[i].Position + shift, out var c)) dc.DrawEllipse(ghostJoint, null, c, 2.5, 2.5);
            }
        }

        // Skeleton.
        if (pose is not null && (display.ShowSkeleton || scene.Selection.Count > 0))
        {
            int n = pose.World.Length;
            Project(n);
            var parents = scene.Skeleton.EffectiveParents;
            var selection = scene.Selection;
            if (display.ShowSkeleton)
            {
                for (int i = 0; i < n; i++)
                {
                    int p = parents[i];
                    if (p < 0) continue;
                    if (!projector.Segment(pose.World[p].Position, pose.World[i].Position, out var a, out var b)) continue;
                    dc.DrawLine(_boneOutline!, a, b);
                    var pen = selection.Contains(i) ? _boneSelected! : i == HoverBone ? _boneHover! : _bone!;
                    dc.DrawLine(pen, a, b);
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (!_visible[i]) continue;
                bool selected = selection.Contains(i);
                bool hover = i == HoverBone;
                if (!display.ShowSkeleton && !selected) continue;
                var fill = selected ? _jointSelected! : hover ? _jointHover! : _joint!;
                double r = selected || hover ? JointRadius + 1.5 : JointRadius;
                dc.DrawEllipse(fill, _boneOutline, _screen[i], r, r);
            }
            for (int i = 0; i < n; i++)
            {
                if (!_visible[i]) continue;
                bool selected = selection.Contains(i);
                if (display.ShowBoneNames || selected || i == HoverBone)
                    Label(dc, scene.Skeleton.Names[i], new Point(_screen[i].X + 7, _screen[i].Y - 8), gizmo);
            }
        }
        LastRenderMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    }

    private static Vector3 Place(Cairn.Rfa.Animation.Pose? pose, int bone, Vector3 local) =>
        pose is not null && bone >= 0 && bone < pose.World.Length ? pose.World[bone].TransformPoint(local) : local;

    private void Label(DrawingContext dc, string text, Point at, (Point Centre, double Radius)? avoid = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!_labels.TryGetValue(text, out var formatted))
        {
            formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, _labelText!, _dpi);
            _labels[text] = formatted;
        }
        if (avoid is { } circle) at = ClearOf(at, formatted.Width + 6, formatted.Height + 2, circle.Centre, circle.Radius + 3);
        var box = new Rect(at.X - 3, at.Y - 1, formatted.Width + 6, formatted.Height + 2);
        dc.DrawRoundedRectangle(_labelBackground, null, box, 3, 3);
        dc.DrawText(formatted, at);
    }

    /// <summary>
    /// A label's text origin moved straight away from a circle (centre <paramref name="c"/>, radius
    /// <paramref name="r"/>) until its box (<paramref name="w"/> × <paramref name="h"/>, top-left at
    /// <paramref name="at"/> − (3, 1)) no longer overlaps it; unchanged when it does not. A label pushed
    /// past the right edge goes to the circle's left instead.
    /// </summary>
    private Point ClearOf(Point at, double w, double h, Point c, double r)
    {
        var box = new Rect(at.X - 3, at.Y - 1, w, h);
        if (!Overlaps(box, c, r)) return at;
        var dir = new System.Windows.Vector(box.X + w / 2 - c.X, box.Y + h / 2 - c.Y);
        if (dir.Length < 1) dir = new System.Windows.Vector(1, -0.25);
        dir.Normalize();
        var moved = Push(box, dir);
        if (moved.Right > ActualWidth - 2 && ActualWidth > 0) moved = Push(box, new System.Windows.Vector(-dir.X, dir.Y));
        return new Point(moved.X + 3, moved.Y + 1);

        Rect Push(Rect start, System.Windows.Vector d)
        {
            // Bisection on the distance moved: overlap only shrinks as the box moves away along d.
            double lo = 0, hi = r + Math.Sqrt(w * w + h * h) + 2;
            for (int i = 0; i < 24; i++)
            {
                double mid = (lo + hi) / 2;
                var test = start;
                test.Offset(d * mid);
                if (Overlaps(test, c, r)) lo = mid;
                else hi = mid;
            }
            var result = start;
            result.Offset(d * hi);
            return result;
        }
    }

    private static bool Overlaps(Rect box, Point c, double r)
    {
        double dx = c.X - Math.Clamp(c.X, box.Left, box.Right), dy = c.Y - Math.Clamp(c.Y, box.Top, box.Bottom);
        return dx * dx + dy * dy < r * r;
    }

    private static void Diamond(DrawingContext dc, Point c, double r, Brush fill)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
            ctx.LineTo(new Point(c.X + r, c.Y), false, false);
            ctx.LineTo(new Point(c.X, c.Y + r), false, false);
            ctx.LineTo(new Point(c.X - r, c.Y), false, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, null, geometry);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        double t = len2 > 0 ? Math.Clamp(((p - a).X * ab.X + (p - a).Y * ab.Y) / len2, 0, 1) : 0;
        return (p - (a + ab * t)).Length;
    }

    private void EnsureResources()
    {
        if (_bone is not null) return;
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _boneOutline = Frozen(new Pen(Brush("Viewport.BoneOutline"), 4.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _bone = Frozen(new Pen(Brush("Viewport.Bone"), 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _boneSelected = Frozen(new Pen(Brush("Viewport.BoneSelected"), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _boneHover = Frozen(new Pen(Brush("Viewport.BoneHover"), 2.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _sphere = Frozen(new Pen(Brush("Viewport.Sphere"), 1.5));
        _sphereHighlight = Frozen(new Pen(Brush("Viewport.Highlight"), 2.5));
        _sphereHover = Frozen(new Pen(Brush("Viewport.BoneHover"), 2.5));
        _prop = Frozen(new Pen(Brush("Viewport.Prop"), 1.5));
        _propX = Frozen(new Pen(Brush("Viewport.AxisX"), 1.5));
        _propY = Frozen(new Pen(Brush("Viewport.AxisY"), 1.5));
        _propZ = Frozen(new Pen(Brush("Viewport.AxisZ"), 1.5));
        _rootPath = Frozen(new Pen(Brush("Viewport.RootPath"), 1.5) { DashStyle = DashStyles.Dash });
        _joint = Brush("Viewport.Joint");
        _jointSelected = Brush("Viewport.BoneSelected");
        _jointHover = Brush("Viewport.BoneHover");
        _labelBackground = Brush("Viewport.LabelBackground");
        _labelText = Brush("Viewport.Label");
        _propFill = Brush("Viewport.Prop");
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
