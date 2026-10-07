using Cairn.Vfx.Formats;

namespace Cairn.Vfx.Animation;

/// <summary>Frame count and playback rate of a texture, supplied by the caller (from the VBM header, or 1 frame for still images).</summary>
public readonly record struct VfxBitmapInfo(int FrameCount, float Fps)
{
    public static readonly VfxBitmapInfo Still = new(1, 15);
}

/// <summary>A mesh decoded once into a version-independent view; built by <see cref="VfxSampler"/>.</summary>
public sealed class VfxMeshView
{
    internal VfxMeshView(VfxMesh mesh, int version, int[] materialSlots)
    {
        Mesh = mesh;
        Flags = VfxVersion.HasLegacyMeshFlags(version) ? LegacyFlags(mesh) : mesh.Flags;
        Fps = mesh.Fps is > 0 ? mesh.Fps.Value : VfxTime.FramesPerSecond;
        StartSeconds = mesh.StartTime ?? (mesh.StartFrame ?? 0) / (float)Fps;
        FrameCount = mesh.Frames.Length;
        IsKeyframed = mesh.Keys is not null;
        MaterialSlots = materialSlots;
        var frames = mesh.Frames;
        Positions = new Vector3[frames.Length][];
        Uvs = new Vector2[frames.Length][];
        Vector3[] basePos = mesh.LegacyPositions is { } lp ? [.. lp] : new Vector3[mesh.NumVertices];
        Vector2[] baseUv = new Vector2[mesh.Faces.Length * 3];
        for (int f = 0; f < mesh.Faces.Length; f++)
            if (mesh.Faces[f].LegacyUvs is { } fu)
                for (int c = 0; c < 3 && c < fu.Length; c++) baseUv[3 * f + c] = fu[c];
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i].Positions is { } p) basePos = VfxPositionCodec.Decode(p);
            Positions[i] = basePos;
            if (frames[i].Uvs is { } uv)
            {
                baseUv = new Vector2[mesh.Faces.Length * 3];
                for (int c = 0; c < baseUv.Length && c < uv.Length; c++) baseUv[c] = uv[c];
            }
            Uvs[i] = baseUv;
        }
        var f0 = frames.IsEmpty ? null : frames[0];
        FacingHeightNegative = (f0?.FacingSize ?? mesh.LegacyFacingSize)?.Y < 0;
        UpVector = f0?.UpVector ?? Vector3.UnitY;
        // Before 0x3000E a keyframed mesh also carries a frame-0 transform that the game applies on top of
        // the pivot; fold it in exactly as conversion to the current format does, so both preview the same.
        Pivot = mesh.Pivot;
        if (IsKeyframed && mesh.IsKeyframed is 1 && VfxVersion.HasFirstFrameOnlyTransformForKeyframed(version) && f0?.Transform is { } t0)
        {
            var pivot = mesh.Pivot ?? new VfxTransform(Vector3.Zero, Quaternion.Identity, Vector3.One);
            var r0 = Quaternion.Normalize(t0.Rotation);
            Pivot = new VfxTransform(t0.Translation + Vector3.Transform(t0.Scale * pivot.Translation, r0),
                Quaternion.Normalize(r0 * Quaternion.Normalize(pivot.Rotation)), t0.Scale * pivot.Scale);
        }
    }

    /// <summary>The keyframed mesh's pivot as the game applies it (an old file's frame-0 transform folded in).</summary>
    public VfxTransform? Pivot { get; }

    public VfxMesh Mesh { get; }
    /// <summary>Mesh flags in the current bit layout (legacy files remapped).</summary>
    public uint Flags { get; }
    public int Fps { get; }
    public float StartSeconds { get; }
    public int FrameCount { get; }
    public bool IsKeyframed { get; }
    public bool IsFacing => (Flags & (VfxMeshFlags.Facing | VfxMeshFlags.FacingRod)) != 0;
    public bool IsRod => (Flags & VfxMeshFlags.FacingRod) != 0;
    public bool NoInterp => (Flags & VfxMeshFlags.NoInterp) != 0;
    /// <summary>Face material index -> index into <see cref="VfxSampler.Materials"/>.</summary>
    public int[] MaterialSlots { get; }
    /// <summary>Decoded object-space positions per frame (frames without geometry share the previous array).</summary>
    public Vector3[][] Positions { get; }
    /// <summary>Per-corner UVs per frame (three per face, frames without UVs share the previous array).</summary>
    public Vector2[][] Uvs { get; }
    public bool FacingHeightNegative { get; }
    public Vector3 UpVector { get; }

    private static uint LegacyFlags(VfxMesh m)
    {
        uint raw = (uint)(m.LegacyFlags ?? (int)m.Flags);
        return (raw & ~3u) | ((raw & 1) != 0 ? VfxMeshFlags.Fullbright : 0) | ((raw & 2) != 0 ? VfxMeshFlags.SeeThrough : 0);
    }
}

/// <summary>A reusable per-mesh result; buffers grow once and are then reused.</summary>
public sealed class VfxMeshSample
{
    public bool Active;
    public float LocalFrame;
    public int Frame0, Frame1;
    public float Fraction;
    public Vector3[] Positions = [];
    public Vector2[] Uvs = [];
    /// <summary>Smooth normals per face-vertex record (only filled on request).</summary>
    public Vector3[] Normals = [];
    public Vector3 Center;
    public float Width, Height;
    public Vector3 Up;
    /// <summary>Per-frame mesh opacity for versions that store it, otherwise null.</summary>
    public float? Opacity;

    internal void Ensure(int vertices, int corners, int normals)
    {
        if (Positions.Length != vertices) Positions = new Vector3[vertices];
        if (Uvs.Length != corners) Uvs = new Vector2[corners];
        if (Normals.Length != normals) Normals = new Vector3[normals];
    }
}

/// <summary>A material decoded into a version-independent view (section material or inline mesh material).</summary>
public sealed class VfxMaterialView
{
    public int Type { get; init; }
    public bool Additive { get; init; }
    public float Fps { get; init; } = VfxTime.FramesPerSecond;
    public float[] Opacity { get; init; } = [];
    public float[] SelfIllumination { get; init; } = [];
    public float[] Mix { get; init; } = [];
    public VfxTextureView? Texture0 { get; init; }
    public VfxTextureView? Texture1 { get; init; }
    public VfxColorI? SolidColor { get; init; }
}

/// <summary>A texture slot: name, animation start (frames), playback rate and type (0/2 loop, 1 once).</summary>
public sealed record VfxTextureView(string Name, int StartFrame, float PlaybackRate, int AnimType, VfxBitmapInfo Bitmap)
{
    /// <summary>True for the host-supplied placeholders, which preview untextured.</summary>
    public bool IsPlaceholder => Name.StartsWith("$original_map", StringComparison.OrdinalIgnoreCase);
}

public readonly record struct VfxMaterialSample(float Opacity, float SelfIllumination, float Mix, int TextureFrame0, int TextureFrame1);

public readonly record struct VfxDummySample(Vector3 Position, Quaternion Orientation);

public readonly record struct VfxLightSample(Vector3 Position, float Radius, float Multiplier, Vector3 Color, bool IsOn);

public readonly record struct VfxWarpSample(Vector3 Position, Quaternion Orientation, float Strength, float Decay,
    float Turbulence, float Frequency, float Scale);

public readonly record struct VfxEmitterSample(Vector3 Position, Quaternion Orientation, float Width, float Height,
    float DropSize, float Speed, float SpeedVariation, float BirthRate, float Opacity);

/// <summary>
/// Samples a <see cref="VfxFile"/> of any stock version at an effect time in 15 fps frames. Construction decodes
/// the file once; the Sample* calls do not allocate when the caller reuses its <see cref="VfxMeshSample"/>s.
/// </summary>
public sealed class VfxSampler
{
    private readonly VfxMeshView[] meshes;
    private readonly VfxMaterialView[] materials;
    private readonly VfxDummy[] dummies;
    private readonly VfxLight[] lights;
    private readonly VfxSpacewarp[] warps;
    private readonly VfxParticleSystem[] particles;
    private readonly int[] particleMaterials;

    public VfxSampler(VfxFile file, Func<string, VfxBitmapInfo>? bitmapInfo = null)
    {
        File = file;
        bitmapInfo ??= static _ => VfxBitmapInfo.Still;
        var mats = new List<VfxMaterialView>();
        foreach (var m in file.Sections.OfType<VfxMaterial>()) mats.Add(FromSection(m, bitmapInfo));
        int sectionMaterials = mats.Count;
        var meshList = new List<VfxMeshView>();
        foreach (var mesh in file.Sections.OfType<VfxMesh>())
        {
            int[] slots;
            if (mesh.InlineMaterials is { } inl)
            {
                slots = new int[inl.Length];
                for (int i = 0; i < inl.Length; i++) { slots[i] = mats.Count; mats.Add(FromInline(inl[i], bitmapInfo)); }
            }
            else slots = [.. (mesh.MaterialIndices ?? []).Select(i => i >= 0 && i < sectionMaterials ? i : -1)];
            meshList.Add(new VfxMeshView(mesh, file.Version, slots));
        }
        particles = [.. file.Sections.OfType<VfxParticleSystem>()];
        particleMaterials = new int[particles.Length];
        for (int i = 0; i < particles.Length; i++)
        {
            var p = particles[i];
            if (p.InlineMaterial is { } pm) { particleMaterials[i] = mats.Count; mats.Add(FromParticle(pm, bitmapInfo)); }
            else particleMaterials[i] = p.MaterialIndex is int mi && mi >= 0 && mi < sectionMaterials ? mi : -1;
        }
        meshes = [.. meshList];
        materials = [.. mats];
        dummies = [.. file.Sections.OfType<VfxDummy>()];
        lights = [.. file.Sections.OfType<VfxLight>()];
        warps = [.. file.Sections.OfType<VfxSpacewarp>()];
    }

    public VfxFile File { get; }
    public int EndFrame => File.EndFrame;
    public IReadOnlyList<VfxMeshView> Meshes => meshes;
    public IReadOnlyList<VfxMaterialView> Materials => materials;
    public IReadOnlyList<VfxDummy> Dummies => dummies;
    public IReadOnlyList<VfxLight> Lights => lights;
    public IReadOnlyList<VfxSpacewarp> Spacewarps => warps;
    public IReadOnlyList<VfxParticleSystem> ParticleSystems => particles;

    /// <summary>The material view index of particle system <paramref name="index"/>, or -1.</summary>
    public int ParticleMaterial(int index) => particleMaterials[index];

    /// <summary>
    /// Evaluates mesh <paramref name="index"/> at effect <paramref name="frame"/> into <paramref name="s"/>.
    /// Returns false (and sets Active = false) outside the mesh's local time window.
    /// </summary>
    public bool SampleMesh(int index, float frame, VfxMeshSample s, bool normals = false, bool quantiseRotations = false)
    {
        var v = meshes[index];
        var mesh = v.Mesh;
        float local = (frame / VfxTime.FramesPerSecond - v.StartSeconds) * v.Fps;
        s.LocalFrame = local;
        s.Active = v.FrameCount > 0 && local >= 0 && local <= v.FrameCount;
        if (!s.Active) return false;
        int n = v.FrameCount;
        int i0 = Math.Min((int)MathF.Floor(local), n - 1), i1 = i0 + 1;
        float frac = v.NoInterp ? 0 : local - i0;
        if (i1 >= n) { i1 = i0; frac = 0; }
        s.Frame0 = i0; s.Frame1 = i1; s.Fraction = frac;
        var p0 = v.Positions[i0]; var p1 = v.Positions[i1];
        if (p1.Length != p0.Length) p1 = p0;
        s.Ensure(p0.Length, v.Uvs[i0].Length, normals ? mesh.FaceVertices.Length : s.Normals.Length);
        v.Uvs[i0].CopyTo(s.Uvs, 0);
        var f0 = mesh.Frames[i0]; var f1 = mesh.Frames[i1];
        var fr0 = mesh.Frames[0];
        Vector3 c0 = (f0.Positions ?? fr0.Positions)?.Center ?? Vector3.Zero, c1 = (f1.Positions ?? fr0.Positions)?.Center ?? Vector3.Zero;
        Vector2 size0 = f0.FacingSize ?? fr0.FacingSize ?? mesh.LegacyFacingSize ?? Vector2.Zero;
        Vector2 size1 = f1.FacingSize ?? fr0.FacingSize ?? mesh.LegacyFacingSize ?? Vector2.Zero;
        Vector3 center = Vector3.Lerp(c0, c1, frac);
        Vector2 size = Vector2.Lerp(size0, size1, frac);
        s.Opacity = f0.Opacity is float o0 ? Math.Clamp(o0 + ((f1.Opacity ?? o0) - o0) * frac, 0, 1) : null;
        var pos = s.Positions;
        if (frac == 0) p0.CopyTo(pos, 0);
        else for (int i = 0; i < pos.Length; i++) pos[i] = Vector3.Lerp(p0[i], p1[i], frac);
        if (v.IsKeyframed)
        {
            var keys = mesh.Keys!;
            float tick = frame * VfxTime.TicksPerFrame;
            var pv = v.Pivot;
            Vector3 pt = pv?.Translation ?? Vector3.Zero, ps = pv?.Scale ?? Vector3.One;
            Quaternion pq = pv?.Rotation ?? Quaternion.Identity;
            Vector3 kt = VfxKeyframeMath.EvaluateVector(keys.Translation, tick, Vector3.Zero);
            Vector3 ks = VfxKeyframeMath.EvaluateVector(keys.Scale, tick, Vector3.One);
            Quaternion kq = VfxKeyframeMath.EvaluateRotation(keys.Rotation, tick, quantiseRotations);
            for (int i = 0; i < pos.Length; i++) pos[i] = VfxKeyframeMath.ApplyKeyed(kt, kq, ks, pt, pq, ps, pos[i]);
            center = VfxKeyframeMath.ApplyKeyed(kt, kq, ks, pt, pq, ps, center);
        }
        else if (f0.Transform is { } t0)
        {
            var t1 = f1.Transform ?? t0;
            for (int i = 0; i < pos.Length; i++) pos[i] = Vector3.Lerp(Apply(t0, pos[i]), Apply(t1, pos[i]), frac);
            center = Vector3.Lerp(Apply(t0, center), Apply(t1, center), frac);
            if (size0.X > 0)
            {
                var sc = Vector3.Lerp(t0.Scale, t1.Scale, frac);
                size = new Vector2(size.X * MathF.Abs(sc.X), size.Y * MathF.Abs(sc.Z));
            }
        }
        s.Center = center; s.Width = size.X; s.Height = size.Y; s.Up = v.UpVector;
        if (normals) ComputeNormals(mesh, pos, s.Normals);
        return true;
    }

    /// <summary>Smooth normals by the engine's rule: per face-vertex record, the normalised sum of its adjacent face normals.</summary>
    public static void ComputeNormals(VfxMesh mesh, ReadOnlySpan<Vector3> positions, Span<Vector3> normals)
    {
        var fv = mesh.FaceVertices;
        for (int r = 0; r < fv.Length && r < normals.Length; r++)
        {
            Vector3 sum = Vector3.Zero;
            foreach (int fi in fv[r].AdjacentFaces)
            {
                if ((uint)fi >= (uint)mesh.Faces.Length) continue;
                var f = mesh.Faces[fi];
                if ((uint)f.V0 >= (uint)positions.Length || (uint)f.V1 >= (uint)positions.Length || (uint)f.V2 >= (uint)positions.Length) continue;
                var n = Vector3.Cross(positions[f.V1] - positions[f.V0], positions[f.V2] - positions[f.V0]);
                float len = n.Length();
                if (len > 0) sum += n / len;
            }
            float l = sum.Length();
            normals[r] = l > 0 ? sum / l : Vector3.UnitY;
        }
    }

    /// <summary>Material opacity, self-illumination, mix and texture frames at effect <paramref name="frame"/>.</summary>
    public VfxMaterialSample SampleMaterial(int index, float frame)
    {
        var m = materials[index];
        return new VfxMaterialSample(Track(m.Opacity, m.Fps, frame, 1), Track(m.SelfIllumination, m.Fps, frame, 0),
            Track(m.Mix, m.Fps, frame, 0), TextureFrame(m.Texture0, frame), TextureFrame(m.Texture1, frame));
    }

    /// <summary>A per-material key track: linear between samples at the material rate, last sample held, clamped to 0..1.</summary>
    public static float Track(ReadOnlySpan<float> keys, float fps, float frame, float fallback)
    {
        if (keys.IsEmpty) return fallback;
        float f = MathF.Max(0, frame / VfxTime.FramesPerSecond * fps);
        // Compare in float before casting: a huge (or NaN) f must not saturate the int and overflow i + 1 (review-findings-2 #7).
        if (!(f < keys.Length - 1)) return Math.Clamp(keys[^1], 0, 1);
        int i = (int)MathF.Floor(f);
        float value = keys[i] + (keys[i + 1] - keys[i]) * (f - i);
        return Math.Clamp(value, 0, 1);
    }

    /// <summary>Texture frame index at effect <paramref name="frame"/>, or -1 when the slot has no (or a placeholder) texture.</summary>
    public static int TextureFrame(VfxTextureView? t, float frame)
    {
        if (t is null || t.IsPlaceholder || t.Name.Length == 0) return -1;
        int n = t.Bitmap.FrameCount;
        if (n <= 1) return 0;
        int idx = Math.Max(0, (int)MathF.Floor((frame - t.StartFrame) * t.Bitmap.Fps * t.PlaybackRate / VfxTime.FramesPerSecond));
        return t.AnimType == 1 ? Math.Min(idx, n - 1) : idx % n;
    }

    /// <summary>Texture frame from a 0..1 fraction of particle life (particle flag 0x4).</summary>
    public static int TextureFrameByLife(VfxTextureView? t, float lifeFraction)
    {
        if (t is null || t.IsPlaceholder || t.Name.Length == 0) return -1;
        int n = Math.Max(1, t.Bitmap.FrameCount);
        return Math.Clamp((int)MathF.Floor(lifeFraction * n), 0, n - 1);
    }

    public VfxDummySample SampleDummy(int index, float frame)
    {
        var d = dummies[index];
        if (d.Frames.IsEmpty) return new VfxDummySample(d.Position, d.Orientation);
        Step(d.Frames.Length, frame, out int i0, out int i1, out float u);
        var a = d.Frames[i0]; var b = d.Frames[i1];
        return new VfxDummySample(Vector3.Lerp(a.Position, b.Position, u), VfxKeyframeMath.Slerp(a.Orientation, b.Orientation, u));
    }

    public VfxLightSample SampleLight(int index, float frame)
    {
        var l = lights[index];
        if (l.Frames.IsEmpty) return new VfxLightSample(l.Initial.Position, l.Initial.Radius, l.Initial.Multiplier, l.Initial.Color, l.Initial.IsOn != 0);
        Step(l.Frames.Length, frame, out int i0, out int i1, out float u);
        var a = l.Frames[i0]; var b = l.Frames[i1];
        return new VfxLightSample(Vector3.Lerp(a.Position, b.Position, u), a.Radius + (b.Radius - a.Radius) * u,
            a.Multiplier + (b.Multiplier - a.Multiplier) * u, Vector3.Lerp(a.Color, b.Color, u), a.IsOn != 0);
    }

    public VfxWarpSample? SampleSpacewarp(int index, float frame)
    {
        var w = warps[index];
        if (w.Frames.IsEmpty) return null;
        Step(w.Frames.Length, frame, out int i0, out int i1, out float u);
        var a = w.Frames[i0]; var b = w.Frames[i1];
        return new VfxWarpSample(Vector3.Lerp(a.Position, b.Position, u), VfxKeyframeMath.Slerp(a.Orientation, b.Orientation, u),
            Lerp(a.Strength, b.Strength, u), Lerp(a.Decay, b.Decay, u), Lerp(a.Turbulence, b.Turbulence, u),
            Lerp(a.Frequency, b.Frequency, u), Lerp(a.Scale, b.Scale, u));
    }

    /// <summary>
    /// Emitter values of particle system <paramref name="index"/> at its local frame (effect frame minus StartTime):
    /// position/size/speed lerped, orientation slerped, birth rate stepped, opacity from the material (or the frame on old files).
    /// </summary>
    public VfxEmitterSample SampleEmitter(int index, float localFrame)
    {
        var p = particles[index];
        if (p.Frames.IsEmpty) return new VfxEmitterSample(Vector3.Zero, Quaternion.Identity, 0, 0, 0, 0, 0, 0, 0);
        Step(p.Frames.Length, localFrame, out int i0, out int i1, out float u);
        var a = p.Frames[i0]; var b = p.Frames[i1];
        int mat = particleMaterials[index];
        float opacity = a.Opacity is float oa ? Math.Clamp(Lerp(oa, b.Opacity ?? oa, u), 0, 1)
            : mat >= 0 ? Track(materials[mat].Opacity, materials[mat].Fps, localFrame + p.StartTime, 1) : 1;
        int stepped = Math.Max(0, (int)MathF.Floor(localFrame)) % p.Frames.Length;
        return new VfxEmitterSample(Vector3.Lerp(a.Position, b.Position, u), VfxKeyframeMath.Slerp(a.Orientation, b.Orientation, u),
            Lerp(a.Width, b.Width, u), Lerp(a.Height, b.Height, u), Lerp(a.DropSize, b.DropSize, u), Lerp(a.Speed, b.Speed, u),
            Lerp(a.SpeedVariation, b.SpeedVariation, u), p.Frames[stepped].BirthRate, opacity);
    }

    private static float Lerp(float a, float b, float u) => a + (b - a) * u;

    /// <summary>A per-frame (or pivot) transform applied to a point: t + R(s * p).</summary>
    internal static Vector3 Apply(VfxTransform t, Vector3 p) => t.Translation + Vector3.Transform(t.Scale * p, t.Rotation);

    /// <summary>Frame records at 15 fps from 0: floor and next, clamped to the last record.</summary>
    private static void Step(int count, float frame, out int i0, out int i1, out float u)
    {
        float f = Math.Clamp(frame, 0, count - 1);
        i0 = (int)MathF.Floor(f); i1 = Math.Min(i0 + 1, count - 1); u = f - i0;
    }

    private static VfxTextureView? Tex(VfxTexture? t, int? legacyStart, int? legacyType, Func<string, VfxBitmapInfo> info) =>
        t is null || string.IsNullOrEmpty(t.Name) ? null
            : new VfxTextureView(t.Name, t.StartFrame ?? legacyStart ?? 0, t.PlaybackRate ?? 1, t.AnimType ?? legacyType ?? 0, info(t.Name));

    private static VfxMaterialView FromSection(VfxMaterial m, Func<string, VfxBitmapInfo> info) => new()
    {
        Type = m.Type, Additive = m.Additive is > 0, Fps = m.Fps ?? m.LegacyFps ?? VfxTime.FramesPerSecond,
        Opacity = m.Opacity is { } o ? [.. o] : [], SelfIllumination = [.. m.SelfIllumination], Mix = m.Mix is { } x ? [.. x] : [],
        Texture0 = Tex(m.Texture0, null, null, info), Texture1 = Tex(m.Texture1, null, null, info), SolidColor = m.SolidColor,
    };

    private static VfxMaterialView FromInline(VfxInlineMaterial m, Func<string, VfxBitmapInfo> info) => new()
    {
        Type = m.Type, Additive = m.Additive is > 0, Mix = m.Mix is { } x ? [.. x] : [],
        SelfIllumination = m.SelfIllumination is float si ? [si] : [],
        Texture0 = Tex(m.Texture0, m.LegacyStartFrame, m.LegacyAnimType, info),
        Texture1 = Tex(m.Texture1, m.LegacyStartFrame, m.LegacyAnimType, info), SolidColor = m.SolidColor,
    };

    private static VfxMaterialView FromParticle(VfxParticleMaterial m, Func<string, VfxBitmapInfo> info) => new()
    {
        Type = m.Type ?? 2, Additive = m.Additive is > 0, Mix = m.Mix is { } x ? [.. x] : [],
        SelfIllumination = m.SelfIllumination is float si ? [si] : [], SolidColor = m.DropsColor,
        Texture0 = string.IsNullOrEmpty(m.Texture0) ? null : new VfxTextureView(m.Texture0, 0, m.Texture0Rate ?? 1, 0, info(m.Texture0)),
        Texture1 = string.IsNullOrEmpty(m.Texture1) ? null : new VfxTextureView(m.Texture1, 0, m.Texture1Rate ?? 1, 0, info(m.Texture1)),
    };
}
