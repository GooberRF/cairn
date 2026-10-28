using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Rfa.Formats.Legacy;
using Cairn.Viewport;
using Cairn.Vfx.Animation;
using Cairn.Vfx.Rfe;

namespace Cairn.Vfx.Ui.Rfe;

/// <summary>
/// A Red Faction II effect at a playback time: meshes sampled exactly as the game does (<see cref="RfeSampler"/>), and the
/// emitters played by <see cref="VfxParticleSimulator"/> on their converted particle systems with the Red Faction II
/// behaviour on top (<see cref="RfeToVfx.Extras"/>), stepped every 1/30 s.
/// </summary>
public sealed class RfeScene
{
    private readonly VfxSampler _sampler;
    private bool _loop = true;

    public RfeScene(RfeFile effect, string name)
    {
        Effect = effect;
        Name = name;
        // the Red Faction II names are kept: the preview looks them up in the .peg packs
        var particles = RfeToVfx.Convert(effect with { Meshes = [] }, name, new RfeConvertOptions { Textures = Rf2TextureMode.KeepOriginals });
        _sampler = new VfxSampler(particles.File);
        Simulators = new VfxParticleSimulator[effect.Emitters.Length];
        Build();
    }

    public RfeFile Effect { get; }
    public string Name { get; }
    public VfxParticleSimulator[] Simulators { get; private set; }

    /// <summary>Elapsed playback time in seconds (unwrapped).</summary>
    public double Elapsed { get; private set; }

    /// <summary>The effect time in seconds (wrapped when looping, held at the end otherwise).</summary>
    public float Seconds { get; private set; }

    /// <summary>The fraction of the effect at <see cref="Seconds"/>.</summary>
    public float Fraction => RfeSampler.Fraction(Seconds, Effect.Duration);

    /// <summary>True to loop (the game's loop mode 3); false holds the last frame and stops the emitters.</summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            if (_loop == value) return;
            _loop = value;
            Build();
            Seek(Elapsed);
        }
    }

    public void Seek(double seconds)
    {
        Elapsed = double.IsFinite(seconds) ? Math.Max(0, seconds) : 0;
        Seconds = RfeSampler.EffectTime(Elapsed, Effect.Duration, _loop);
    }

    /// <summary>Moves every particle system to the current time.</summary>
    public void AdvanceParticles()
    {
        float frame = (float)(Elapsed * VfxTime.FramesPerSecond);
        foreach (var sim in Simulators) sim.Advance(frame);
    }

    private void Build()
    {
        var mode = _loop ? VfxPlaybackMode.Loop : VfxPlaybackMode.HoldLastFrame;
        Simulators = [.. Enumerable.Range(0, Effect.Emitters.Length).Select(i => new VfxParticleSimulator(_sampler, i, 1, mode, 2, RfeToVfx.Extras(Effect, i)))];
    }
}

/// <summary>
/// Draws an <see cref="RfeScene"/>: one model per mesh object (built once, placed by a matrix every frame; facing objects
/// turned to the camera), materials from the time-keyed opacity and self-illumination tracks, and particle quads (streaks
/// along the velocity), blended models ordered back to front.
/// </summary>
public sealed class RfeSceneRenderer
{
    private sealed class MeshPart
    {
        public required RfeMeshObject Mesh;
        public required GeometryModel3D Model;
        public required MatrixTransform3D Transform;
        public required Vector3 Centre;
        public required float Radius;
        public Vector3 World;
        public bool Blended;
    }

    private readonly RfeScene _scene;
    private readonly RfeTextureSource _textures;
    private readonly Model3DGroup _root = new();
    private readonly List<MeshPart> _meshes = [];
    private readonly List<(GeometryModel3D Model, MeshGeometry3D Geometry)> _particles = [];
    private readonly Dictionary<string, TextureImage?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, int, int, int, bool), Material> _materials = [];
    private readonly List<(Model3D Model, bool Blended, double Depth)> _order = [];

    public RfeSceneRenderer(RfeScene scene, RfeTextureSource textures)
    {
        _scene = scene;
        _textures = textures;
        foreach (var mesh in scene.Effect.Meshes) _meshes.Add(Build(mesh));
        for (int i = 0; i < scene.Effect.Emitters.Length * AlphaLevels; i++)
        {
            var g = new MeshGeometry3D();
            _particles.Add((new GeometryModel3D(g, null), g));
        }
        foreach (string name in scene.Effect.Textures.Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)) Load(name);
    }

    public Model3D Root => _root;

    /// <summary>Raised when a texture finished loading (the caller renders again).</summary>
    public event EventHandler? TexturesChanged;

    /// <summary>Texture loads still running.</summary>
    public int PendingTextures { get; private set; }

    /// <summary>Textures that loaded.</summary>
    public int LoadedTextures => _images.Values.Count(v => v is not null);

    /// <summary>Particle quads drawn by the last <see cref="Update"/>.</summary>
    public int ParticleQuadCount { get; private set; }

    /// <summary>Mesh objects drawn by the last <see cref="Update"/> (not fully transparent).</summary>
    public int VisibleMeshes { get; private set; }

    private static MeshPart Build(RfeMeshObject mesh)
    {
        var positions = new Point3DCollection();
        var uvs = new PointCollection();
        var indices = new Int32Collection();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var g in mesh.Body.Geometries)
        {
            foreach (var t in g.Triangles)
            {
                foreach (var (v, uv) in new[] { (t.A, t.UvA), (t.B, t.UvB), (t.C, t.UvC) })
                {
                    var p = (uint)v < (uint)g.Positions.Length ? g.Positions[v] : Vector3.Zero;
                    if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) p = Vector3.Zero;
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p);
                    indices.Add(positions.Count);
                    positions.Add(new Point3D(p.X, p.Y, p.Z));
                    uvs.Add(float.IsFinite(uv.X) && float.IsFinite(uv.Y) ? new Point(uv.X, uv.Y) : new Point());
                }
            }
        }
        var geometry = new MeshGeometry3D { Positions = positions, TextureCoordinates = uvs, TriangleIndices = indices };
        geometry.Freeze();
        var transform = new MatrixTransform3D();
        var model = new GeometryModel3D(geometry, null) { Transform = transform };
        bool empty = min.X > max.X;
        return new MeshPart
        {
            Mesh = mesh, Model = model, Transform = transform,
            Centre = empty ? Vector3.Zero : (min + max) / 2, Radius = empty ? 0 : (max - min).Length() / 2,
        };
    }

    private async void Load(string name)
    {
        _images[name] = null;
        PendingTextures++;
        try { _images[name] = await _textures.GetAsync(name); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _images[name] = null; }
        finally { PendingTextures--; }
        _materials.Clear();
        TexturesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Places everything at the scene's time for <paramref name="camera"/>.</summary>
    public void Update(OrbitCamera camera)
    {
        var effect = _scene.Effect;
        float seconds = _scene.Seconds, u = _scene.Fraction;
        Vector3 right = camera.Right, up = camera.Up, eye = camera.Eye, fwd = camera.Forward;
        _order.Clear();
        VisibleMeshes = 0;
        foreach (var part in _meshes)
        {
            var (p, q, s) = RfeSampler.Transform(part.Mesh, u);
            if (!Finite(p)) p = Vector3.Zero;
            if (!Finite(s)) s = Vector3.One;
            // facing objects turn their local +Z to the camera as the game does
            var rotation = RfeSampler.FacingRotation(part.Mesh.FacingMode, q, p, eye);
            var m = Matrix4x4.CreateScale(s) * rotation * Matrix4x4.CreateTranslation(p);
            part.Transform.Matrix = new Matrix3D(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
            part.World = Vector3.Transform(part.Centre, m);
            var material = effect.Materials[part.Mesh.Material];
            float opacity = RfeSampler.Opacity(material, seconds);
            if (opacity <= 0.004f) { part.Model.Material = part.Model.BackMaterial = null; continue; }
            var mat = MaterialFor(effect.TextureOf(part.Mesh.Material), material.IsAdditive, opacity, RfeSampler.SelfIllumination(material, seconds), seconds, false, out part.Blended);
            part.Model.Material = mat;
            // the game draws effect meshes from both sides (culling off, rf2.exe 0x565d25)
            part.Model.BackMaterial = mat;
            VisibleMeshes++;
            _order.Add((part.Model, part.Blended, Vector3.Dot(part.World - eye, fwd)));
        }
        _scene.AdvanceParticles();
        ParticleQuadCount = 0;
        for (int i = 0; i < _scene.Simulators.Length; i++) UpdateParticles(i, right, up, eye, fwd, seconds);
        _order.Sort((a, b) => a.Blended != b.Blended ? (a.Blended ? 1 : -1) : a.Blended ? b.Depth.CompareTo(a.Depth) : 0);
        bool same = _root.Children.Count == _order.Count;
        for (int i = 0; same && i < _order.Count; i++) same = ReferenceEquals(_root.Children[i], _order[i].Model);
        if (!same)
        {
            _root.Children.Clear();
            foreach (var o in _order) _root.Children.Add(o.Model);
        }
    }

    /// <summary>Alpha levels particles are grouped into (one model and material each), as the effect renderer does.</summary>
    private const int AlphaLevels = 8;

    private void UpdateParticles(int index, Vector3 right, Vector3 up, Vector3 eye, Vector3 fwd, float seconds)
    {
        var particles = _scene.Simulators[index].Particles;
        if (particles.IsEmpty) return;
        for (int level = 1; level <= AlphaLevels; level++) UpdateParticles(index, level, particles, right, up, eye, fwd, seconds);
    }

    private void UpdateParticles(int index, int level, ReadOnlySpan<VfxParticle> particles, Vector3 right, Vector3 up, Vector3 eye, Vector3 fwd, float seconds)
    {
        var (model, g) = _particles[index * AlphaLevels + level - 1];
        var emitter = _scene.Effect.Emitters[index];
        var material = _scene.Effect.Materials[emitter.Material];
        float streak = emitter.Streak > 0 && float.IsFinite(emitter.Streak) ? emitter.Streak : 0;
        var pos = new Point3DCollection();
        var uv = new PointCollection();
        var tri = new Int32Collection();
        Vector3 sum = Vector3.Zero;
        foreach (ref readonly var q in particles)
        {
            if (q.Size <= 0 || !Finite(q.Position) || Math.Clamp((int)(q.Alpha * AlphaLevels + 0.5f), 0, AlphaLevels) != level) continue;
            Vector3 c = q.Position, ax = right * q.Size, ay = up * q.Size;
            if (streak > 0 && q.Velocity.LengthSquared() > 1e-8f && Finite(q.Velocity))
            {
                // a streak: from the particle back along its velocity, as wide as the particle
                var along = q.Velocity * (streak * RfeSampler.ParticleStep);
                var side = Vector3.Cross(Vector3.Normalize(q.Velocity), c - eye);
                if (side.LengthSquared() > 1e-10f)
                {
                    ax = Vector3.Normalize(side) * q.Size;
                    ay = along / 2 + Vector3.Normalize(along) * q.Size;
                    c -= along / 2;
                }
            }
            int b = pos.Count;
            Vector3 v0 = c - ax - ay, v1 = c + ax - ay, v2 = c + ax + ay, v3 = c - ax + ay;
            pos.Add(new Point3D(v0.X, v0.Y, v0.Z)); pos.Add(new Point3D(v1.X, v1.Y, v1.Z));
            pos.Add(new Point3D(v2.X, v2.Y, v2.Z)); pos.Add(new Point3D(v3.X, v3.Y, v3.Z));
            uv.Add(new Point(0, 1)); uv.Add(new Point(1, 1)); uv.Add(new Point(1, 0)); uv.Add(new Point(0, 0));
            tri.Add(b); tri.Add(b + 2); tri.Add(b + 1); tri.Add(b); tri.Add(b + 3); tri.Add(b + 2);
            sum += c;
            ParticleQuadCount++;
        }
        int n = pos.Count / 4;
        if (n == 0) return;
        g.Positions = pos; g.TextureCoordinates = uv; g.TriangleIndices = tri;
        // unlit (the particles' colour and lighting were not traced): fully self-illuminated
        model.Material = MaterialFor(_scene.Effect.TextureOf(emitter.Material), material.IsAdditive, level / (float)AlphaLevels, 1, seconds, true, out _);
        model.BackMaterial = model.Material;
        _order.Add((model, true, Vector3.Dot(sum / n - eye, fwd)));
    }

    private Material MaterialFor(string texture, bool additive, float opacity, float selfIllumination, float seconds, bool particle, out bool blended)
    {
        var image = texture.Length > 0 && _images.TryGetValue(texture, out var img) ? img : null;
        int frame = image is { IsAnimated: true } ? (int)MathF.Floor(seconds * RfeTextureSource.AnimatedFps) % image.FrameCount : 0;
        blended = additive || opacity < 0.999f || image is { HasAlpha: true } || particle;
        var key = (texture, frame, (int)(opacity * 64), (int)(Math.Clamp(selfIllumination, 0, 1) * 16), additive);
        if (_materials.TryGetValue(key, out var cached)) return cached;
        Brush brush = image is not null
            ? new ImageBrush(image.Frame(frame)) { ViewportUnits = BrushMappingMode.Absolute, TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 1, 1) }
            : new SolidColorBrush(Color.FromRgb(200, 200, 200));
        brush.Opacity = opacity;
        Material result;
        if (additive) result = new EmissiveMaterial(brush);
        else if (selfIllumination > 0.01f)
        {
            var emissive = brush.Clone();
            emissive.Opacity = opacity * selfIllumination;
            result = new MaterialGroup { Children = { new DiffuseMaterial(brush), new EmissiveMaterial(emissive) } };
        }
        else result = new DiffuseMaterial(brush);
        result.Freeze();
        return _materials[key] = result;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>Adds the visible meshes' bounding corners and the live particles' positions as they are now (for framing).</summary>
    public void CollectPoints(List<Vector3> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        foreach (var part in _meshes)
        {
            if (part.Model.Material is null) continue;
            float r = part.Radius * MaxScale(part.Mesh, _scene.Fraction);
            points.Add(part.World - new Vector3(r));
            points.Add(part.World + new Vector3(r));
        }
        for (int i = 0; i < _scene.Simulators.Length; i++)
        {
            foreach (ref readonly var q in _scene.Simulators[i].Particles)
            {
                if (!Finite(q.Position) || !float.IsFinite(q.Size)) continue;
                points.Add(q.Position - new Vector3(q.Size));
                points.Add(q.Position + new Vector3(q.Size));
            }
        }
    }

    private static float MaxScale(RfeMeshObject mesh, float u)
    {
        var s = Vector3.Abs(RfeSampler.Sample(mesh.Scales, u, Vector3.One));
        float m = MathF.Max(s.X, MathF.Max(s.Y, s.Z));
        return float.IsFinite(m) ? m : 1;
    }
}
