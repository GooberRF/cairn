using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Cairn.Rfa.Ui.Services;
using Cairn.Rfa.Ui.ViewModels;
using Cairn.Rfa.Animation;
using Cairn.Rfa.Formats.V3d;

namespace Cairn.Rfa.Ui.Viewport;

/// <summary>
/// Turns a <see cref="SceneViewModel"/>'s mesh into WPF 3D models (one <see cref="GeometryModel3D"/>
/// per batch, plus a second one sharing its vertices for the batch's double-sided triangles, which gets
/// a back material) and keeps their vertices skinned to the current pose.
/// </summary>
/// <remarks>
/// <para>Vertices stay in RF space; the view matrix carries the handedness (see <see cref="OrbitCamera"/>).
/// Seen through that left-handed view, RF's front faces wind counter-clockwise on screen, which is what
/// WPF treats as front, so triangles keep their stored order (see <see cref="FlipWinding"/>).</para>
/// <para>Skinning runs on the CPU through Core's <see cref="Skinning"/> into buffers allocated once per
/// mesh build. The result is written into the existing <see cref="Point3DCollection"/> while it is
/// detached from its mesh (so the per-point writes raise no change notifications), then reattached:
/// a frame allocates nothing on this side.</para>
/// <para>Morph (vertex) animation goes through <see cref="MorphSampler"/> into LOD 0's batches before
/// skinning, when the clip has morph data (the engine morphs LOD 0 only).</para>
/// </remarks>
internal sealed class MeshRenderer
{
    /// <summary>
    /// Whether to emit triangles as (A, C, B). Measured, not assumed: through the left-handed view RF's
    /// stored front faces wind counter-clockwise on screen (the engine culls clockwise), which is WPF's
    /// own front-face rule, so the stored order is kept. With the swap, faces render inside out (a
    /// head shows the inside of its far side). REDUX's glTF export agrees: it mirrors X and reverses
    /// the winding, which together preserve the facing.
    /// </summary>
    internal const bool FlipWinding = false;

    private readonly List<RenderBatch> _batches = [];
    private readonly Dictionary<string, Material?> _textureMaterials = new(StringComparer.OrdinalIgnoreCase);
    private Matrix4x4[] _skin = [];
    private Vector3[] _morphed = [];
    private int _textureGeneration;
    private SceneViewModel? _scene;

    public MeshRenderer()
    {
        Root.Children.Add(Ambient);
        Root.Children.Add(Headlight);
        Root.Children.Add(Opaque);
        Root.Children.Add(Transparent);
    }

    /// <summary>The model group the viewport shows.</summary>
    public Model3DGroup Root { get; } = new();

    private Model3DGroup Opaque { get; } = new();

    private Model3DGroup Transparent { get; } = new();

    private AmbientLight Ambient { get; } = new(Color.FromRgb(110, 110, 110));

    private DirectionalLight Headlight { get; } = new(Color.FromRgb(170, 170, 170), new Vector3D(0, 0, -1));

    /// <summary>Number of vertices currently drawn (for the frame-time report).</summary>
    public int VertexCount { get; private set; }

    /// <summary>Texture loads in flight (the screenshot waits for these through <see cref="BusyTracker"/>).</summary>
    public int PendingTextures { get; private set; }

    private sealed class RenderBatch
    {
        public required V3dBatch Batch { get; init; }
        public required BatchRef Ref { get; init; }
        public required Vector3 Offset { get; init; }
        public required Vector3[] Base { get; init; }
        public required Vector3[] Skinned { get; init; }
        public required Vector3[] SkinnedNormals { get; init; }
        public required Vector3[] MorphBuffer { get; init; }
        public required Point3DCollection Positions { get; init; }
        public required Vector3DCollection Normals { get; init; }
        public required MeshGeometry3D? Single { get; init; }
        public required MeshGeometry3D? Double { get; init; }
        public required GeometryModel3D? SingleModel { get; init; }
        public required GeometryModel3D? DoubleModel { get; init; }
        public required string TextureName { get; init; }
        public required bool IsLod0OfFirstSubmesh { get; init; }
        public required bool UseMorphMap { get; init; }
        public bool HasAlpha { get; set; }
    }

    /// <summary>Rebuilds every model from the scene (mesh, LOD, display mode or highlight changed).</summary>
    public void Build(SceneViewModel scene, FrameworkElement resources)
    {
        _scene = scene;
        _batches.Clear();
        Opaque.Children.Clear();
        Transparent.Children.Clear();
        VertexCount = 0;
        var mesh = scene.Mesh;
        var mode = scene.Display.MeshMode;
        if (mesh is null || mode == MeshDisplayMode.Off)
        {
            _skin = [];
            return;
        }

        _skin = new Matrix4x4[scene.Skeleton.Count];
        var flat = Frozen(new DiffuseMaterial(new SolidColorBrush(ThemeColor(resources, "Viewport.FlatMeshColor", Color.FromRgb(185, 190, 200)))));
        var missing = Frozen(new DiffuseMaterial(new SolidColorBrush(ThemeColor(resources, "Viewport.MissingTextureColor", Color.FromRgb(200, 168, 184)))));
        var tint = Frozen(new EmissiveMaterial(new SolidColorBrush(ThemeColor(resources, "Viewport.HighlightTintColor", Color.FromArgb(102, 255, 138, 31)))));

        int s = 0;
        int morphLength = 0;
        foreach (var sub in mesh.Submeshes)
        {
            if (sub.Lods.Length == 0)
            {
                s++;
                continue;
            }
            int l = Math.Clamp(scene.Lod, 0, sub.Lods.Length - 1);
            var lod = sub.Lods[l];
            bool useMap = (lod.Flags & V3dLod.FlagMorphVerticesMap) != 0;
            for (int b = 0; b < lod.Batches.Length; b++)
            {
                var batch = lod.Batches[b];
                int n = batch.VertexCount;
                if (n == 0 || batch.TriangleCount == 0) continue;
                var offset = sub.Offset;
                var basePositions = new Vector3[n];
                for (int i = 0; i < n; i++) basePositions[i] = batch.Positions[i] + offset;
                var normals = new Vector3[n];
                for (int i = 0; i < n && i < batch.Normals.Length; i++) normals[i] = SafeNormal(batch.Normals[i]);

                var positions = new Point3DCollection(n);
                var normalCollection = new Vector3DCollection(n);
                var uvs = new PointCollection(n);
                for (int i = 0; i < n; i++)
                {
                    var p = basePositions[i];
                    positions.Add(new Point3D(p.X, p.Y, p.Z));
                    normalCollection.Add(new Vector3D(normals[i].X, normals[i].Y, normals[i].Z));
                    var uv = i < batch.TexCoords.Length ? batch.TexCoords[i] : Vector2.Zero;
                    uvs.Add(new Point(Finite(uv.X), Finite(uv.Y)));
                }
                uvs.Freeze();

                var single = new Int32Collection();
                var dbl = new Int32Collection();
                foreach (var t in batch.Triangles)
                {
                    if (t.A >= n || t.B >= n || t.C >= n) continue;
                    var target = (t.Flags & V3dTriangle.DoubleSided) != 0 ? dbl : single;
                    target.Add(t.A);
                    target.Add(FlipWinding ? t.C : t.B);
                    target.Add(FlipWinding ? t.B : t.C);
                }
                single.Freeze();
                dbl.Freeze();

                string textureName = batch.TextureIndex >= 0 && batch.TextureIndex < lod.Textures.Length ? lod.Textures[batch.TextureIndex].FileName : string.Empty;
                var bref = new BatchRef(s, l, b);
                bool highlighted = scene.HighlightBatches.Contains(bref);
                Material material = mode == MeshDisplayMode.Flat ? flat : missing;
                if (highlighted) material = Frozen(new MaterialGroup { Children = { material, tint } });

                MeshGeometry3D? singleMesh = null, doubleMesh = null;
                GeometryModel3D? singleModel = null, doubleModel = null;
                if (single.Count > 0)
                {
                    singleMesh = new MeshGeometry3D { Positions = positions, Normals = normalCollection, TextureCoordinates = uvs, TriangleIndices = single };
                    singleModel = new GeometryModel3D(singleMesh, material);
                }
                if (dbl.Count > 0)
                {
                    doubleMesh = new MeshGeometry3D { Positions = positions, Normals = normalCollection, TextureCoordinates = uvs, TriangleIndices = dbl };
                    doubleModel = new GeometryModel3D(doubleMesh, material) { BackMaterial = material };
                }

                var rb = new RenderBatch
                {
                    Batch = batch,
                    Ref = bref,
                    Offset = offset,
                    Base = basePositions,
                    Skinned = new Vector3[n],
                    SkinnedNormals = new Vector3[n],
                    MorphBuffer = s == 0 && l == 0 ? new Vector3[n] : [],
                    Positions = positions,
                    Normals = normalCollection,
                    Single = singleMesh,
                    Double = doubleMesh,
                    SingleModel = singleModel,
                    DoubleModel = doubleModel,
                    TextureName = textureName,
                    IsLod0OfFirstSubmesh = s == 0 && l == 0,
                    UseMorphMap = useMap,
                };
                _batches.Add(rb);
                if (singleModel is not null) Opaque.Children.Add(singleModel);
                if (doubleModel is not null) Opaque.Children.Add(doubleModel);
                VertexCount += n;
                if (rb.IsLod0OfFirstSubmesh) morphLength = Math.Max(morphLength, 1);
            }
            s++;
        }
        _morphed = scene.Clip is { } clip && MorphSampler.HasMorph(clip) ? new Vector3[clip.Morph.VertexCount] : [];

        if (mode == MeshDisplayMode.Textured) LoadTextures(scene, tint);
        UpdatePose(scene);
    }

    /// <summary>Lighting: full-bright (ambient white) or a headlight from the camera.</summary>
    public void UpdateLighting(OrbitCamera camera, bool fullBright)
    {
        if (fullBright)
        {
            Ambient.Color = Colors.White;
            Headlight.Color = Colors.Black;
        }
        else
        {
            Ambient.Color = Color.FromRgb(125, 125, 125);
            Headlight.Color = Color.FromRgb(165, 165, 165);
            var f = camera.Forward - camera.Up * 0.35f;
            Headlight.Direction = new Vector3D(f.X, f.Y, f.Z);
        }
    }

    /// <summary>
    /// Re-skins every batch to the scene's current pose (and morph state) and writes the vertices into
    /// the WPF collections. Allocation-free.
    /// </summary>
    public void UpdatePose(SceneViewModel scene)
    {
        if (_batches.Count == 0) return;
        var pose = scene.Pose;
        bool skin = pose is not null && _skin.Length == scene.Skeleton.Count && _skin.Length > 0;
        if (skin) Skinning.ComputeSkinMatrices(scene.Skeleton, pose!.World, _skin);

        bool morph = false;
        var clip = scene.Clip;
        if (scene.HasMorph && clip is not null)
        {
            if (_morphed.Length != clip.Morph.VertexCount) _morphed = new Vector3[clip.Morph.VertexCount];
            morph = MorphSampler.Sample(clip, scene.Time, _morphed);
        }

        foreach (var rb in _batches)
        {
            ReadOnlySpan<Vector3> source = rb.Base;
            if (morph && rb.IsLod0OfFirstSubmesh && rb.MorphBuffer.Length == rb.Batch.VertexCount)
            {
                MorphSampler.ApplyToBatch(rb.Batch, clip!.Morph.VertexIndices.AsSpan(), _morphed, rb.UseMorphMap && rb.Batch.MorphMap.Length > 0, rb.MorphBuffer);
                var buffer = rb.MorphBuffer;
                for (int i = 0; i < buffer.Length; i++) buffer[i] += rb.Offset;
                source = buffer;
            }

            if (skin && rb.Batch.BoneLinks.Length > 0)
            {
                Skinning.Skin(source, rb.Batch.Normals.Length >= source.Length ? rb.Batch.Normals.AsSpan() : default,
                    rb.Batch.BoneLinks.AsSpan(), _skin, rb.Skinned, rb.Batch.Normals.Length >= source.Length ? rb.SkinnedNormals : default);
            }
            else
            {
                source.CopyTo(rb.Skinned);
                for (int i = 0; i < rb.SkinnedNormals.Length && i < rb.Batch.Normals.Length; i++) rb.SkinnedNormals[i] = rb.Batch.Normals[i];
            }

            // Detach, write, reattach: the writes then raise no per-point notifications.
            if (rb.Single is not null) { rb.Single.Positions = null; rb.Single.Normals = null; }
            if (rb.Double is not null) { rb.Double.Positions = null; rb.Double.Normals = null; }
            var positions = rb.Positions;
            var normals = rb.Normals;
            var skinned = rb.Skinned;
            var skinnedNormals = rb.SkinnedNormals;
            for (int i = 0; i < skinned.Length; i++)
            {
                var p = skinned[i];
                positions[i] = new Point3D(p.X, p.Y, p.Z);
                var nrm = SafeNormal(skinnedNormals[i]);
                normals[i] = new Vector3D(nrm.X, nrm.Y, nrm.Z);
            }
            if (rb.Single is not null) { rb.Single.Positions = positions; rb.Single.Normals = normals; }
            if (rb.Double is not null) { rb.Double.Positions = positions; rb.Double.Normals = normals; }
        }
    }

    /// <summary>Bounds of what is drawn (skinned vertices), or null with nothing drawn.</summary>
    public (Vector3 Min, Vector3 Max)? Bounds() => BoundsOf(rb => rb.Skinned);

    /// <summary>
    /// Bounds of the drawn batches in their bind pose (stored positions plus submesh offset), or null. A mesh
    /// built before its pose arrives is skinned with identity bones for a moment, which can shrink it.
    /// </summary>
    public (Vector3 Min, Vector3 Max)? BindBounds() => BoundsOf(rb => rb.Base);

    private (Vector3 Min, Vector3 Max)? BoundsOf(Func<RenderBatch, Vector3[]> points)
    {
        if (_batches.Count == 0) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (var rb in _batches)
        {
            foreach (var p in points(rb))
            {
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }
        return any ? (min, max) : null;
    }

    private void LoadTextures(SceneViewModel scene, Material tint)
    {
        int generation = ++_textureGeneration;
        var resolver = scene.TextureResolver;
        if (resolver is null) return;
        foreach (var group in _batches.GroupBy(b => b.TextureName, StringComparer.OrdinalIgnoreCase))
        {
            string name = group.Key;
            var batches = group.ToList();
            if (name.Length == 0) continue;
            if (_textureMaterials.TryGetValue(name, out var cached))
            {
                if (cached is not null) Assign(batches, cached, tint, scene);
                continue;
            }
            PendingTextures++;
            var busy = BusyTracker.Begin("texture " + name);
            var task = scene.Textures.GetAsync(resolver, name);
            task.ContinueWith(t =>
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        PendingTextures--;
                        if (generation != _textureGeneration || !ReferenceEquals(scene, _scene)) return;
                        var image = t.IsCompletedSuccessfully ? t.Result : null;
                        Material? material = null;
                        if (image is not null)
                        {
                            var brush = new ImageBrush(image.Image)
                            {
                                ViewportUnits = BrushMappingMode.Absolute,
                                Viewport = new Rect(0, 0, 1, 1),
                                TileMode = TileMode.Tile,
                                Stretch = Stretch.Fill,
                            };
                            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.Linear);
                            brush.Freeze();
                            material = Frozen(new DiffuseMaterial(brush));
                            foreach (var b in batches) b.HasAlpha = image.HasAlpha;
                        }
                        _textureMaterials[name] = material;
                        if (material is not null) Assign(batches, material, tint, scene);
                    }
                    finally { busy.Dispose(); }
                }));
            }, TaskScheduler.Default);
        }
    }

    private void Assign(List<RenderBatch> batches, Material material, Material tint, SceneViewModel scene)
    {
        foreach (var rb in batches)
        {
            var m = scene.HighlightBatches.Contains(rb.Ref) ? Frozen(new MaterialGroup { Children = { material, tint } }) : material;
            if (rb.SingleModel is not null) rb.SingleModel.Material = m;
            if (rb.DoubleModel is not null)
            {
                rb.DoubleModel.Material = m;
                rb.DoubleModel.BackMaterial = m;
            }
            // Blended batches go after the opaque ones (WPF draws in order and does not sort).
            if (rb.HasAlpha)
            {
                if (rb.SingleModel is not null && Opaque.Children.Remove(rb.SingleModel)) Transparent.Children.Add(rb.SingleModel);
                if (rb.DoubleModel is not null && Opaque.Children.Remove(rb.DoubleModel)) Transparent.Children.Add(rb.DoubleModel);
            }
        }
    }

    /// <summary>Forgets cached materials (resolver or theme changed).</summary>
    public void ClearTextureCache() => _textureMaterials.Clear();

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Color ThemeColor(FrameworkElement resources, string key, Color fallback) =>
        resources.TryFindResource(key) is Color c ? c : fallback;

    private static double Finite(float v) => float.IsFinite(v) ? v : 0;

    private static Vector3 SafeNormal(Vector3 n) =>
        float.IsFinite(n.X) && float.IsFinite(n.Y) && float.IsFinite(n.Z) && n.LengthSquared() > 1e-12f ? n : Vector3.UnitY;
}
