using Cairn.Vfx.Animation;
using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Editing;

/// <summary>
/// Plain-data description of a version-0x40006 mesh; <see cref="Build"/> derives every computed field.
/// One entry in <see cref="Frames"/> = static mesh; several = morph mesh (the Morph flag is set).
/// Frame count: morph = Frames.Count; otherwise FrameCount ?? TransformFrames.Count ?? UvFrames.Count ?? 1.
/// </summary>
public sealed class VfxMeshBuilder
{
    public string Name { get; set; } = "Mesh";
    public string Parent { get; set; } = "Scene Root";
    /// <summary>Positions per geometry frame (each the same length).</summary>
    public List<Vector3[]> Frames { get; set; } = [];
    public List<(int A, int B, int C)> Triangles { get; set; } = [];
    /// <summary>Per-corner UVs (3 per face) for frame 0.</summary>
    public Vector2[]? Uvs { get; set; }
    /// <summary>Per-corner UVs for every frame; sets DumpUvs (overrides <see cref="Uvs"/>).</summary>
    public List<Vector2[]>? UvFrames { get; set; }
    /// <summary>Per-face slot into <see cref="MaterialIndices"/> (-1 none); default 0, or -1 without materials.</summary>
    public int[]? FaceMaterials { get; set; }
    /// <summary>Per-face smoothing group bitmask; default 1.</summary>
    public int[]? SmoothingGroups { get; set; }
    /// <summary>Per-corner colours (3 per face); default (1,1,1).</summary>
    public Vector3[]? CornerColors { get; set; }
    /// <summary>Indices into the file's material sections.</summary>
    public List<int> MaterialIndices { get; set; } = [];
    public uint Flags { get; set; }
    public int Fps { get; set; } = VfxTime.FramesPerSecond;
    public float StartTime { get; set; }
    public int? FrameCount { get; set; }
    /// <summary>Facing / facing-rod size for all geometry frames, or per geometry frame.</summary>
    public Vector2 FacingSize { get; set; } = Vector2.One;
    public Vector2[]? FacingSizes { get; set; }
    public Vector3 UpVector { get; set; } = Vector3.UnitY;
    /// <summary>Static transform repeated on every frame (non-keyframed, non-morph).</summary>
    public VfxTransform Transform { get; set; } = VfxBuilder.Identity;
    /// <summary>Per-frame transforms (non-keyframed, non-morph); overrides <see cref="Transform"/>.</summary>
    public List<VfxTransform>? TransformFrames { get; set; }
    /// <summary>When set the mesh is keyframed (only meaningful for non-morph meshes).</summary>
    public VfxKeyLists? Keys { get; set; }
    public VfxTransform? Pivot { get; set; }

    public VfxMesh Build()
    {
        if (Frames.Count == 0) throw new ArgumentException("At least one frame of positions is required.");
        int nv = Frames[0].Length, nf = Triangles.Count;
        if (Frames.Any(f => f.Length != nv)) throw new ArgumentException("All frames need the same vertex count.");
        foreach (var (a, b, c) in Triangles)
            if ((uint)a >= nv || (uint)b >= nv || (uint)c >= nv) throw new ArgumentException("Triangle index out of range.");
        void Len<T>(T[]? arr, int n, string what) { if (arr is not null && arr.Length != n) throw new ArgumentException($"{what} needs {n} entries."); }
        Len(Uvs, 3 * nf, nameof(Uvs)); Len(FaceMaterials, nf, nameof(FaceMaterials));
        Len(SmoothingGroups, nf, nameof(SmoothingGroups)); Len(CornerColors, 3 * nf, nameof(CornerColors));
        if (Fps <= 0) throw new ArgumentException("Fps must be positive.");

        uint flags = Flags & ~VfxMeshFlags.Morph;
        bool morph = Frames.Count > 1;
        if (morph) flags |= VfxMeshFlags.Morph;
        if (UvFrames is not null) flags |= VfxMeshFlags.DumpUvs;
        bool kf = Keys is not null && !morph;
        int count = morph ? Frames.Count : FrameCount ?? TransformFrames?.Count ?? UvFrames?.Count ?? 1;
        if (count < 1) throw new ArgumentException("Frame count must be at least 1.");
        if (UvFrames is not null && UvFrames.Count != count) throw new ArgumentException($"UvFrames needs {count} frames.");
        if (TransformFrames is not null && TransformFrames.Count != count) throw new ArgumentException($"TransformFrames needs {count} frames.");
        if (FacingSizes is not null && FacingSizes.Length != (morph ? count : 1)) throw new ArgumentException("FacingSizes needs one entry per geometry frame.");

        var white = Vector3.One;
        var faces = Triangles.Select((t, i) => new VfxFace(t.A, t.B, t.C, null,
            CornerColors?[3 * i] ?? white, CornerColors?[3 * i + 1] ?? white, CornerColors?[3 * i + 2] ?? white,
            Vector3.UnitY, Vector3.Zero, 0f, FaceMaterials?[i] ?? (MaterialIndices.Count > 0 ? 0 : -1),
            SmoothingGroups?[i] ?? 1, 0, 0, 0)).ToImmutableArray();

        var frames = ImmutableArray.CreateBuilder<VfxMeshFrame>(count);
        for (int i = 0; i < count; i++)
        {
            var l = VfxMesh.FrameLayout(VfxVersion.Current, flags, kf, i);
            var uv = UvFrames?[i] ?? (i == 0 ? Uvs ?? new Vector2[3 * nf] : null);
            if (uv is not null && uv.Length != 3 * nf) throw new ArgumentException($"UVs of frame {i} need {3 * nf} entries.");
            frames.Add(new VfxMeshFrame(
                l.Positions ? VfxGeometry.Quantise(Frames[i]) : null,
                l.FacingSize ? FacingSizes?[i] ?? FacingSize : null,
                l.UpVector ? UpVector : null,
                l.Uvs ? uv!.ToImmutableArray() : null,
                l.Transform ? TransformFrames?[i] ?? Transform : null, null, null));
        }
        var mesh = new VfxMesh(Name, Parent, 0, nv, null, faces, Fps, StartTime, VfxGeometry.EndTime(StartTime, count, Fps),
            null, null, [.. MaterialIndices], null, Vector3.Zero, 0f, null, flags, null, [], (byte)(kf ? 1 : 0),
            frames.MoveToImmutable(), kf ? Pivot ?? VfxBuilder.Identity : null, kf ? Keys : null);
        return VfxGeometry.Rederive(mesh);
    }
}

/// <summary>Constructors for new 0x40006 files and sections with stock-like defaults.</summary>
public static class VfxBuilder
{
    public static readonly VfxTransform Identity = new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>An empty 0x40006 effect (header flags 0, end frame 0).</summary>
    public static VfxFile NewFile() => new(VfxVersion.Current, 0, 0, null, 0, []);

    public static VfxTexture Texture(string name) => new(name, 0, 1f, 2);

    /// <summary>Image material (type 0): fps 15, one self-illumination and one opacity sample.</summary>
    public static VfxMaterial ImageMaterial(string texture, bool additive = true, float selfIllumination = 1f, float opacity = 1f) =>
        new(0, VfxTime.FramesPerSecond, (byte)(additive ? 1 : 0), Texture(texture), null, null, null, Vector3.Zero, "",
            null, [selfIllumination], [opacity]);

    /// <summary>Two-texture mix material (type 1) with the given mix samples.</summary>
    public static VfxMaterial MixMaterial(string texture0, string texture1, IEnumerable<float> mix, bool additive = true) =>
        new(1, VfxTime.FramesPerSecond, (byte)(additive ? 1 : 0), Texture(texture0), Texture(texture1), null, [.. mix],
            Vector3.Zero, "", null, [1f], [1f]);

    /// <summary>Colour-only material (type 2), stock colour (128,128,128).</summary>
    public static VfxMaterial ColorMaterial(VfxColorI? color = null, bool additive = false) =>
        new(2, VfxTime.FramesPerSecond, (byte)(additive ? 1 : 0), null, null, null, null, null, null,
            color ?? new VfxColorI(128, 128, 128), [1f], [1f]);

    /// <summary>Default birth rate: 30 particles per second, stored per tick of 1/4800 s as the engine reads it.</summary>
    public const float DefaultBirthRate = 30f / VfxTime.TicksPerSecond;

    /// <summary>
    /// A visible emitter frame: 0.5 m square, 0.1 m drops (sprites 0.4 m across), 2 m/s with 0.5 m/s box jitter,
    /// 30 particles per second.
    /// </summary>
    public static VfxParticleFrame ParticleFrame(Vector3? position = null) =>
        new(position ?? Vector3.Zero, Quaternion.Identity, 0.5f, 0.5f, 0.1f, 2f, 0.5f, DefaultBirthRate, null);

    /// <summary>
    /// A particle system with <paramref name="frames"/> identical frames (not drops: no tail distance). Births happen only
    /// while 0 &lt; frame &lt; frame count, so give it at least the effect's length. Lifetime 1 s (4800 ticks) with 20%
    /// variation, cap 60, random roll, shrinking and fading over the last half of each particle's life.
    /// </summary>
    public static VfxParticleSystem ParticleSystem(string name, int materialIndex, int frames = 1, string parent = "Scene Root") =>
        new(name, parent, 0, 0x10, [], 0, materialIndex, null, 60, 0, 4800, 0.2f, 0, null,
            new Vector2(0f, 0.5f), null, null, new Vector2(0f, 0.5f), null, null,
            [.. Enumerable.Repeat(ParticleFrame(), Math.Max(frames, 1))]);

    public static VfxDummy Dummy(string name, int frames = 1, string parent = "Scene Root") =>
        new(name, parent, 0, Vector3.Zero, Quaternion.Identity,
            [.. Enumerable.Repeat(new VfxDummyFrame(Vector3.Zero, Quaternion.Identity), Math.Max(frames, 0))]);

    public static VfxLightParams LightParams(float radius = 10f) => new(Vector3.Zero, radius, 1f, Vector3.One, 1);

    public static VfxLight Light(string name, int frames = 1, string parent = "Scene Root") =>
        new(name, parent, 0, LightParams(), [.. Enumerable.Repeat(LightParams(), Math.Max(frames, 0))]);

    public static VfxSpacewarp Spacewarp(string name, int type = 0, int frames = 1, string parent = "Scene Root") =>
        new(name, parent, type, [.. Enumerable.Repeat(new VfxSpacewarpFrame(Vector3.Zero, Quaternion.Identity, 1f, 0f, 0f, 1f, 1f), Math.Max(frames, 0))]);
}
