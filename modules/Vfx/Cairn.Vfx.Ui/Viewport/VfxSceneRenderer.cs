using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;
using Cairn.Vfx.Ui.Documents;

namespace Cairn.Vfx.Ui.Viewport;

/// <summary>What <see cref="VfxSceneRenderer"/> draws: one effect's sampler, playback state and particle simulators (a document or a preview).</summary>
public interface IVfxScene
{
    /// <summary>The sampler of the effect shown.</summary>
    VfxSampler Sampler { get; }
    /// <summary>Playback state at the current time (effect frame, visibility, emission).</summary>
    VfxPlaybackState State { get; }
    /// <summary>One simulator per particle system.</summary>
    VfxParticleSimulator[] Simulators { get; }
    /// <summary>Unwrapped playback frame for the particle simulators.</summary>
    float TimelineFrame { get; }
    /// <summary>Sections not drawn.</summary>
    IReadOnlySet<int> HiddenSections { get; }
}

/// <summary>
/// Builds and refreshes the WPF scene for a <see cref="VfxDocument"/>: one GeometryModel3D per mesh and
/// material slot (unshared corners, preallocated), camera-facing quads/rods, particle quads grouped by
/// alpha bucket and texture frame, and back-to-front ordering of blended models every frame.
/// </summary>
public sealed class VfxSceneRenderer
{
    private sealed class Part
    {
        public required int Mesh, Section, Material;
        public required int[] Faces;
        public required GeometryModel3D Model;
        public required MeshGeometry3D Geometry;
        public required Point3D[] World;
        public bool Visible, Blended;
    }

    private sealed class ParticlePart
    {
        public required GeometryModel3D Model;
        public required MeshGeometry3D Geometry;
        public int Count;
    }

    private readonly IVfxScene _doc;
    private readonly TextureService _textures;
    private readonly Model3DGroup _root = new();
    private readonly List<Part> _parts = [];
    private readonly List<Dictionary<(int, int), ParticlePart>> _particleParts = [];
    private readonly VfxMeshSample _sample = new();
    private readonly Dictionary<string, TextureImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(int, int, int, int), Material> _materials = [];
    private readonly Dictionary<(string, int), ImageBrush> _brushes = [];
    private readonly List<(Model3D Model, bool Blended, double Depth)> _order = [];

    public VfxSceneRenderer(IVfxScene doc, TextureService textures)
    {
        _doc = doc;
        _textures = textures;
        Rebuild();
    }

    public Model3D Root => _root;
    /// <summary>Raised when a texture finished loading (caller re-renders).</summary>
    public event EventHandler? TexturesChanged;

    /// <summary>Recreates all models after the snapshot changed.</summary>
    public void Rebuild()
    {
        _parts.Clear(); _particleParts.Clear(); _materials.Clear(); _root.Children.Clear();
        var s = _doc.Sampler;
        for (int m = 0; m < s.Meshes.Count; m++)
        {
            var view = s.Meshes[m];
            int section = VfxSections.SectionIndex<VfxMesh>(s.File, m);
            var faces = view.Mesh.Faces;
            foreach (var group in Enumerable.Range(0, faces.Length).GroupBy(f => SlotMaterial(view, faces[f].MaterialIndex)))
            {
                int[] list = view.IsFacing ? [] : [.. group];
                int corners = view.IsFacing ? 4 : list.Length * 3;
                var g = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(new Point3D[corners]),
                    TextureCoordinates = new PointCollection(new Point[corners]),
                    TriangleIndices = view.IsFacing ? new Int32Collection(new[] { 0, 2, 1, 0, 3, 2 }) : new Int32Collection(Enumerable.Range(0, corners)),
                };
                _parts.Add(new Part { Mesh = m, Section = section, Material = group.Key, Faces = list, Model = new GeometryModel3D(g, null), Geometry = g, World = new Point3D[corners] });
                if (view.IsFacing) break;
            }
        }
        for (int p = 0; p < s.ParticleSystems.Count; p++) _particleParts.Add([]);
        foreach (var mat in s.Materials)
            if (mat.Texture0 is { IsPlaceholder: false } t && !_images.ContainsKey(t.Name)) Load(t.Name);
    }

    private static int SlotMaterial(VfxMeshView v, int slot) =>
        slot >= 0 && slot < v.MaterialSlots.Length ? v.MaterialSlots[slot] : (v.MaterialSlots.Length > 0 ? v.MaterialSlots[0] : -1);

    private async void Load(string name)
    {
        _images[name] = null;
        PendingTextures++;
        try { _images[name] = await _textures.GetAsync(name); }
        catch (Exception) { return; }
        finally { PendingTextures--; }
        _materials.Clear();
        TexturesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Number of texture loads still running.</summary>
    public int PendingTextures { get; private set; }
    /// <summary>Number of requested textures that loaded.</summary>
    public int LoadedTextures => _images.Values.Count(v => v is not null);

    /// <summary>Samples every object at the document's current state and orders blended models for <paramref name="camera"/>.</summary>
    public void Update(OrbitCamera camera)
    {
        var s = _doc.Sampler;
        var state = _doc.State;
        Vector3 right = camera.Right, up = camera.Up, eye = camera.Eye, fwd = camera.Forward;
        _order.Clear();
        foreach (var part in _parts)
        {
            part.Visible = state.MeshesVisible && !_doc.HiddenSections.Contains(part.Section) && s.SampleMesh(part.Mesh, state.Frame, _sample);
            if (!part.Visible) continue;
            var view = s.Meshes[part.Mesh];
            var pos = part.Geometry.Positions; var uv = part.Geometry.TextureCoordinates;
            part.Geometry.Positions = null; part.Geometry.TextureCoordinates = null;
            if (view.IsFacing) FillFacing(part, view, right, up, eye, pos, uv);
            else
            {
                var faces = view.Mesh.Faces; int c = 0;
                foreach (int f in part.Faces)
                {
                    var face = faces[f];
                    for (int k = 0; k < 3; k++, c++)
                    {
                        int vi = k == 0 ? face.V0 : k == 1 ? face.V1 : face.V2;
                        var p = (uint)vi < (uint)_sample.Positions.Length ? _sample.Positions[vi] : Vector3.Zero;
                        pos[c] = part.World[c] = new Point3D(p.X, p.Y, p.Z);
                        int ci = f * 3 + k;
                        var t = ci < _sample.Uvs.Length ? _sample.Uvs[ci] : Vector2.Zero;
                        uv[c] = new Point(t.X, t.Y);
                    }
                }
            }
            part.Geometry.Positions = pos; part.Geometry.TextureCoordinates = uv;
            part.Model.Material = MaterialFor(part.Material, state.Frame, _sample.Opacity, view.Flags, out part.Blended);
            _order.Add((part.Model, part.Blended, Vector3.Dot(Centre(part.World) - eye, fwd)));
        }
        ParticleQuadCount = 0;
        for (int i = 0; i < _doc.Simulators.Length; i++)
        {
            var sim = _doc.Simulators[i];
            sim.Advance(_doc.TimelineFrame);
            UpdateParticles(i, sim, !_doc.HiddenSections.Contains(VfxSections.SectionIndex<VfxParticleSystem>(s.File, i)), right, up, eye, fwd);
        }
        // opaque first, then blended far-to-near (WPF does not sort blended geometry)
        _order.Sort((a, b) => a.Blended != b.Blended ? (a.Blended ? 1 : -1) : a.Blended ? b.Depth.CompareTo(a.Depth) : 0);
        bool same = _root.Children.Count == _order.Count;
        for (int i = 0; same && i < _order.Count; i++) same = ReferenceEquals(_root.Children[i], _order[i].Model);
        if (!same)
        {
            _root.Children.Clear();
            foreach (var o in _order) _root.Children.Add(o.Model);
        }
    }

    private void FillFacing(Part part, VfxMeshView view, Vector3 right, Vector3 up, Vector3 eye, Point3DCollection pos, PointCollection uv)
    {
        Vector3 c = _sample.Center;
        float hw = MathF.Abs(_sample.Width) * 0.5f, hh = MathF.Abs(_sample.Height) * 0.5f;
        Vector3 ax = right, ay = up;
        if (view.IsRod && _sample.Up.LengthSquared() > 1e-8f)
        {
            ay = Vector3.Normalize(_sample.Up);
            var side = Vector3.Cross(ay, c - eye);
            ax = side.LengthSquared() > 1e-8f ? Vector3.Normalize(side) : right;
            // keep (ax, ay) turning the same way as the camera's (right, up) so the quad faces the camera and U runs left to right
            if (Vector3.Dot(Vector3.Cross(ax, ay), Vector3.Cross(right, up)) < 0) ax = -ax;
        }
        Span<Vector3> corners = [c - ax * hw - ay * hh, c + ax * hw - ay * hh, c + ax * hw + ay * hh, c - ax * hw + ay * hh];
        Span<Point> uvs = [new(0, 1), new(1, 1), new(1, 0), new(0, 0)];
        for (int i = 0; i < 4; i++) { pos[i] = part.World[i] = new Point3D(corners[i].X, corners[i].Y, corners[i].Z); uv[i] = uvs[i]; }
    }

    /// <summary>Particle sprites emitted by the last <see cref="Update"/> (diagnostics).</summary>
    public int ParticleQuadCount { get; private set; }

    private static (int, int) ParticleKey(in VfxParticle q) => (Math.Clamp((int)(q.Alpha * 8 + 0.5f), 0, 8), Math.Max(0, q.TextureFrame));

    private void UpdateParticles(int system, VfxParticleSimulator sim, bool show, Vector3 right, Vector3 up, Vector3 eye, Vector3 fwd)
    {
        var parts = _particleParts[system];
        foreach (var p in parts.Values) p.Count = 0;
        if (!show) return;
        var particles = sim.Particles;
        foreach (ref readonly var q in particles)
        {
            var key = ParticleKey(q);
            if (key.Item1 == 0) continue;
            if (!parts.TryGetValue(key, out var part))
            {
                var g = new MeshGeometry3D { Positions = [], TextureCoordinates = [], TriangleIndices = [] };
                parts[key] = part = new ParticlePart { Model = new GeometryModel3D(g, null), Geometry = g };
            }
            part.Count++;
        }
        int matIndex = _doc.Sampler.ParticleMaterial(system);
        foreach (var (key, part) in parts)
        {
            if (part.Count == 0) continue;
            var g = part.Geometry;
            var pos = g.Positions; var uv = g.TextureCoordinates; var tri = g.TriangleIndices;
            g.Positions = null; g.TextureCoordinates = null; g.TriangleIndices = null;
            pos.Clear(); uv.Clear(); tri.Clear();
            Vector3 sum = Vector3.Zero;
            foreach (ref readonly var q in particles)
            {
                if (ParticleKey(q) != key) continue;
                int b = pos.Count;
                // screen-aligned sprite of half-extent Size, rolled by the particle's angle (flag 0x10)
                float cos = MathF.Cos(q.Angle) * q.Size, sin = MathF.Sin(q.Angle) * q.Size;
                Vector3 c = q.Position, ax = right * cos + up * sin, ay = up * cos - right * sin;
                Vector3 v0 = c - ax - ay, v1 = c + ax - ay, v2 = c + ax + ay, v3 = c - ax + ay;
                pos.Add(new Point3D(v0.X, v0.Y, v0.Z)); pos.Add(new Point3D(v1.X, v1.Y, v1.Z));
                pos.Add(new Point3D(v2.X, v2.Y, v2.Z)); pos.Add(new Point3D(v3.X, v3.Y, v3.Z));
                uv.Add(new Point(0, 1)); uv.Add(new Point(1, 1)); uv.Add(new Point(1, 0)); uv.Add(new Point(0, 0));
                // clockwise on screen, like RF's front faces (the left-handed view keeps D3D's winding)
                tri.Add(b); tri.Add(b + 2); tri.Add(b + 1); tri.Add(b); tri.Add(b + 3); tri.Add(b + 2);
                sum += c;
                ParticleQuadCount++;
            }
            g.Positions = pos; g.TextureCoordinates = uv; g.TriangleIndices = tri;
            part.Model.Material = MaterialFor(matIndex, -1, key.Item1 / 8f, VfxMeshFlags.Fullbright, out _, key.Item2);
            _order.Add((part.Model, true, Vector3.Dot(sum / part.Count - eye, fwd)));
        }
    }

    /// <summary>
    /// Material for a material view at an effect frame. Additive: emissive only (adds to what is behind);
    /// alpha blend: diffuse with brush opacity (texture alpha x opacity); fullbright or self-illumination
    /// adds an emissive layer (approximates the engine's lighting floor). Faces are back-face culled.
    /// </summary>
    private Material MaterialFor(int index, float frame, float? meshOpacity, uint flags, out bool blended, int forcedTexFrame = -1)
    {
        var mats = _doc.Sampler.Materials;
        if (index < 0 || index >= mats.Count)
        {
            blended = false;
            if (!_materials.TryGetValue((-1, 0, 0, 0), out var d)) _materials[(-1, 0, 0, 0)] = d = Freeze(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(180, 180, 180))));
            return d;
        }
        var view = mats[index];
        var sample = frame >= 0 ? _doc.Sampler.SampleMaterial(index, frame) : new VfxMaterialSample(1, 1, 0, 0, -1);
        float opacity = Math.Clamp(sample.Opacity * (meshOpacity ?? 1), 0, 1);
        bool fullbright = (flags & VfxMeshFlags.Fullbright) != 0;
        float selfIllum = fullbright ? 1 : Math.Clamp(sample.SelfIllumination, 0, 1);
        var tv = view.Texture0 is { IsPlaceholder: false } t0 && view.Type != 2 ? t0 : null;
        var tex = tv is not null && _images.TryGetValue(tv.Name, out var img) ? img : null;
        int texFrame = 0;
        if (tex is { IsAnimated: true } && tv is not null)
            texFrame = forcedTexFrame >= 0 ? forcedTexFrame : Math.Max(0, VfxSampler.TextureFrame(tv with { Bitmap = new VfxBitmapInfo(tex.FrameCount, tex.FramesPerSecond) }, frame));
        blended = view.Additive || opacity < 0.999f || tex is { HasAlpha: true };
        var key = (index, texFrame, (int)(opacity * 64), (int)(selfIllum * 16));
        if (_materials.TryGetValue(key, out var cached)) return cached;
        Brush brush;
        if (tex is not null && tv is not null)
        {
            if (!_brushes.TryGetValue((tv.Name, texFrame), out var ib))
            {
                ib = new ImageBrush(tex.Frame(texFrame)) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1) };
                ib.Freeze();
                _brushes[(tv.Name, texFrame)] = ib;
            }
            brush = ib.Clone();
        }
        else
        {
            var c = view.SolidColor is { } sc ? Color.FromRgb((byte)sc.R, (byte)sc.G, (byte)sc.B) : Color.FromRgb(200, 200, 200);
            brush = new SolidColorBrush(c);
        }
        brush.Opacity = opacity;
        Material result;
        if (view.Additive) result = new EmissiveMaterial(brush);
        else if (selfIllum > 0.01f)
        {
            var emissive = brush.Clone(); emissive.Opacity = opacity * selfIllum;
            result = new MaterialGroup { Children = { new DiffuseMaterial(brush), new EmissiveMaterial(emissive) } };
        }
        else result = new DiffuseMaterial(brush);
        return _materials[key] = Freeze(result);
    }

    private static Material Freeze(Material m) { m.Freeze(); return m; }

    private static Vector3 Centre(Point3D[] pts)
    {
        if (pts.Length == 0) return Vector3.Zero;
        double x = 0, y = 0, z = 0;
        foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
        return new Vector3((float)(x / pts.Length), (float)(y / pts.Length), (float)(z / pts.Length));
    }

    /// <summary>World-space bounds of a section's visible geometry (all sections with -1); false when nothing is visible.</summary>
    public bool TryGetBounds(int section, out Vector3 min, out Vector3 max)
    {
        min = new(float.MaxValue); max = new(float.MinValue);
        foreach (var p in _parts)
            if (p.Visible && (section < 0 || p.Section == section))
                foreach (var w in p.World) { var v = V(w); min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        return min.X <= max.X;
    }

    /// <summary>Nearest mesh section hit by a ray over the last drawn corners, or -1.</summary>
    public int Pick(WorldRay ray, out float distance)
    {
        distance = float.MaxValue; int best = -1;
        foreach (var p in _parts)
        {
            if (!p.Visible) continue;
            var w = p.World;
            bool facing = _doc.Sampler.Meshes[p.Mesh].IsFacing;
            int tris = facing ? 2 : w.Length / 3;
            for (int i = 0; i < tris; i++)
            {
                var (a, b, c) = facing ? (w[0], w[i + 1], w[i + 2]) : (w[i * 3], w[i * 3 + 1], w[i * 3 + 2]);
                if (Intersect(ray, V(a), V(b), V(c), out float t) && t < distance) { distance = t; best = p.Section; }
            }
        }
        return best;
    }

    private static Vector3 V(Point3D p) => new((float)p.X, (float)p.Y, (float)p.Z);

    private static bool Intersect(WorldRay r, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(r.Direction, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f) return false;
        float inv = 1 / det; Vector3 s = r.Origin - a;
        float u = Vector3.Dot(s, p) * inv; if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(r.Direction, q) * inv; if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0;
    }
}
