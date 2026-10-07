namespace Cairn.Vfx.Formats;

/// <summary>
/// Version constants and the named version gates of the VSFX format. Stock files use 0x30008, 0x3000D,
/// 0x3000E, 0x3000F, 0x30012 and 0x40006; gates between those values are unexercised by stock data.
/// </summary>
public static class VfxVersion
{
    public const int Minimum = 0x30000;
    public const int Current = 0x40006;

    /// <summary>0x40000-0x40004 parse but the engine refuses them; files are accepted and flagged.</summary>
    public static bool IsEngineFatal(int v) => v >= 0x40000 && v <= 0x40004;

    public static bool HasHeaderFlags(int v) => v >= 0x30008;
    public static bool HasSelsets(int v) => v >= 0x3000F;
    public static bool HasHeaderUnk1(int v) => v < 0x3000A;
    public static bool HasMaterialSections(int v) => v >= 0x40000;
    public static bool HasMixFrameTotal(int v) => v >= 0x40002;
    public static bool HasMaterialFps(int v) => v >= 0x40003;
    public static bool HasOpacityFrames(int v) => v >= 0x40005;
    public static bool HasUvFrames(int v) => v >= 0x3000D;
    public static bool HasKeyframes(int v) => v >= 0x30009;

    public static bool HasLegacyPositions(int v) => v < 0x3000A;
    public static bool HasLegacyFaceUvs(int v) => v < 0x3000D;
    public static bool HasMeshFps(int v) => v >= 0x30009;
    public static bool HasTimeRange(int v) => v >= 0x40004;
    public static bool HasInclusiveFrameRange(int v) => v >= 0x3000C;
    public static bool HasLegacyMeshFlags(int v) => v < 0x30002;
    public static bool HasLegacyFacingSize(int v) => v == 0x3000A;
    public static bool HasFacingSize(int v) => v >= 0x3000B;
    public static bool HasUpVector(int v) => v >= 0x40001;
    public static bool HasFirstFrameOnlyTransformForKeyframed(int v) => v < 0x3000E;
    public static bool HasFramePad(int v) => v < 0x30009;
    public static bool HasFrameOpacity(int v) => v < 0x40005;
    public static bool HasPivot(int v) => v >= 0x3000A;

    public static bool HasAdditive(int v) => v >= 0x30003;
    public static bool HasAdditiveOnAllMaterials(int v) => v >= 0x40006;
    public static bool HasTextureAnimation(int v) => v >= 0x30012;
    public static bool HasSpecular(int v) => v >= 0x30007;
    public static bool HasInlineSelfIllumination(int v) => v >= 0x30011;

    public static bool HasParticleFlags(int v) => v >= 0x30010;
    public static bool HasFloatShrink(int v) => v >= 0x30005;
    public static bool HasFade(int v) => v >= 0x30006;
    public static bool HasParticleBlob(int v) => v < 0x3000D;
    public static bool HasParticleFrameOpacity(int v) => v < 0x40005;
}

/// <summary>Section tags (little-endian u32 of the four ASCII bytes).</summary>
public static class VfxSectionTag
{
    public const uint Mesh = 0x4F584653;
    public const uint Material = 0x4C54414D;
    public const uint ParticleSystem = 0x54524150;
    public const uint Selset = 0x534C4553;
    public const uint Light = 0x54474C41;
    public const uint Spacewarp = 0x50524157;
    public const uint Chain = 0x454E4843;
    public const uint MaterialModifier = 0x444F4D4D;
    public const uint Camera = 0x41524D43;
    public const uint Dummy = 0x594D4D44;
}

/// <summary>Mesh flag bits.</summary>
public static class VfxMeshFlags
{
    public const uint Facing = 0x1, NoInterp = 0x2, Morph = 0x4, Fire = 0x8, Fullbright = 0x10, SeeThrough = 0x20,
        Corona = 0x40, Sky = 0x80, DumpUvs = 0x100, FacingRod = 0x800;
}

/// <summary>
/// A parsed VSFX file. Only the independent header fields are stored; every derivable count is
/// recomputed on write (<see cref="VfxHeaderCounts.Compute"/>). Optional fields follow the version gates.
/// </summary>
public sealed record VfxFile(int Version, int? HeaderFlags, int EndFrame, int? LegacyUnk1, int? SelsetObjectCount,
    ImmutableArray<VfxSection> Sections)
{
    /// <summary>Camera sections are opaque, so their frame total cannot be derived; it is stored raw (0 in the corpus).</summary>
    public int CameraFrameCount { get; init; }

    /// <summary>The counts as stored in the header (null for files built in memory).</summary>
    public VfxHeaderCounts? StoredCounts { get; init; }

    /// <summary>Diagnostics from reading: stored header counts that disagree with recomputed ones.</summary>
    public ImmutableArray<string> Notes { get; init; } = [];

    /// <summary>True for 0x40000-0x40004, which the engine refuses to load.</summary>
    public bool IsEngineFatalVersion => VfxVersion.IsEngineFatal(Version);
}

/// <summary>The derivable header counts. Gated counts are null when the version lacks them.</summary>
public sealed record VfxHeaderCounts(
    int Meshes, int Lights, int Dummies, int ParticleSystems, int Spacewarps, int Cameras, int? Selsets,
    int? Materials, int? MixFrames, int? SelfIlluminationFrames, int? OpacityFrames,
    int Faces, int MeshMaterialIndices, int VertexNormals, int AdjacentFaces, int MeshFrames, int? UvFrames,
    int? MeshTransformFrames, int? KeyframeLists, int? TranslationKeys, int? RotationKeys, int? ScaleKeys,
    int LightFrames, int DummyFrames, int ParticleFrames, int SpacewarpFrames, int CameraFrames)
{
    public static VfxHeaderCounts Compute(VfxFile file)
    {
        int v = file.Version;
        var meshes = file.Sections.OfType<VfxMesh>().ToList();
        var mats = file.Sections.OfType<VfxMaterial>().ToList();
        int Opaque(uint tag) => file.Sections.Count(s => s is VfxOpaqueSection o && o.Tag == tag);
        bool kfv = VfxVersion.HasKeyframes(v);
        var kf = meshes.Where(m => m.Keys is not null).ToList();
        return new VfxHeaderCounts(
            meshes.Count + Opaque(VfxSectionTag.Chain),
            file.Sections.Count(s => s is VfxLight),
            file.Sections.Count(s => s is VfxDummy),
            file.Sections.Count(s => s is VfxParticleSystem),
            file.Sections.Count(s => s is VfxSpacewarp),
            Opaque(VfxSectionTag.Camera),
            VfxVersion.HasSelsets(v) ? Opaque(VfxSectionTag.Selset) : null,
            VfxVersion.HasMaterialSections(v) ? mats.Count : null,
            VfxVersion.HasMixFrameTotal(v) ? mats.Sum(m => m.Mix?.Length ?? 0) : null,
            VfxVersion.HasMaterialFps(v) ? mats.Sum(m => m.SelfIllumination.Length) : null,
            VfxVersion.HasOpacityFrames(v) ? mats.Sum(m => m.Opacity?.Length ?? 0) : null,
            meshes.Sum(m => m.Faces.Length),
            meshes.Sum(m => m.MaterialCount),
            meshes.Sum(m => m.FaceVertices.Length),
            meshes.Sum(m => m.FaceVertices.Sum(f => f.AdjacentFaces.Length)),
            meshes.Sum(m => m.Frames.Length),
            VfxVersion.HasUvFrames(v) ? meshes.Sum(m => m.Frames.Count(f => f.Uvs is not null)) : null,
            kfv ? meshes.Sum(m => m.Frames.Count(f => f.Transform is not null)) : null,
            kfv ? kf.Count : null,
            kfv ? kf.Sum(m => m.Keys!.Translation.Length) : null,
            kfv ? kf.Sum(m => m.Keys!.Rotation.Length) : null,
            kfv ? kf.Sum(m => m.Keys!.Scale.Length) : null,
            file.Sections.OfType<VfxLight>().Sum(l => l.Frames.Length),
            file.Sections.OfType<VfxDummy>().Sum(d => d.Frames.Length),
            file.Sections.OfType<VfxParticleSystem>().Sum(p => p.Frames.Length),
            file.Sections.OfType<VfxSpacewarp>().Sum(w => w.Frames.Length),
            file.CameraFrameCount);
    }
}

/// <summary>A section in file order. Concrete types are the modelled sections or <see cref="VfxOpaqueSection"/>.</summary>
public abstract record VfxSection
{
    public abstract uint Tag { get; }
}

/// <summary>A section kept as tag + raw body: selsets, chains, cameras, material modifiers and unknown tags.</summary>
public sealed record VfxOpaqueSection(uint SectionTag, ImmutableArray<byte> Body) : VfxSection
{
    public override uint Tag => SectionTag;
}

public readonly record struct VfxColorI(int R, int G, int B);

public sealed record VfxTransform(Vector3 Translation, Quaternion Rotation, Vector3 Scale);

public sealed record VfxVectorKey(int Time, Vector3 Value, Vector3 InTangent, Vector3 OutTangent);

public sealed record VfxRotationKey(int Time, Quaternion Value, float Tension, float Continuity, float Bias, float EaseIn, float EaseOut);

public sealed record VfxKeyLists(ImmutableArray<VfxVectorKey> Translation, ImmutableArray<VfxRotationKey> Rotation, ImmutableArray<VfxVectorKey> Scale);

/// <summary>Quantised positions: decoded = Center + Raw * Multiplier (per axis). Raw holds 3 shorts per vertex.</summary>
public sealed record VfxCompressedPositions(Vector3 Center, Vector3 Multiplier, ImmutableArray<short> Raw);

public sealed record VfxFace(int V0, int V1, int V2, ImmutableArray<Vector2>? LegacyUvs,
    Vector3 Color0, Vector3 Color1, Vector3 Color2, Vector3 Normal, Vector3 Center, float Radius,
    int MaterialIndex, int SmoothingGroup, int FaceVertex0, int FaceVertex1, int FaceVertex2);

/// <summary>A face-vertex ("vertex normal") record; U/V are opaque raw u32 (usually 0xCDCDCDCD at 0x40006).</summary>
public sealed record VfxFaceVertex(int SmoothingGroup, int VertexIndex, uint RawU, uint RawV, ImmutableArray<int> AdjacentFaces);

/// <summary>One mesh frame; which fields are present is fixed by <see cref="VfxMesh.FrameLayout"/>.</summary>
public sealed record VfxMeshFrame(VfxCompressedPositions? Positions, Vector2? FacingSize, Vector3? UpVector,
    ImmutableArray<Vector2>? Uvs, VfxTransform? Transform, byte? LegacyPad, float? Opacity)
{
    internal static readonly VfxMeshFrame Empty = new(null, null, null, null, null, null, null);
}

public sealed record VfxTexture(string Name, int? StartFrame, float? PlaybackRate, int? AnimType);

/// <summary>Material (0 image, 1 vmix, 2 colour only) inlined in meshes before 0x40000.</summary>
public sealed record VfxInlineMaterial(int Type, byte? Additive, VfxTexture? Texture0, VfxTexture? Texture1,
    int? LegacyStartFrame, int? LegacyAnimType, Vector3? SpecularGlossReflection, string? ReflectionTexture,
    ImmutableArray<float>? Mix, VfxColorI? SolidColor, float? SelfIllumination);

/// <summary>
/// A mesh. Frame count is Frames.Length; below 0x40004 it must agree with StartFrame/EndFrame
/// (end-start+1 at >=0x3000C, end-start before).
/// </summary>
public sealed record VfxMesh(string Name, string Parent, byte SaveParent, int NumVertices,
    ImmutableArray<Vector3>? LegacyPositions, ImmutableArray<VfxFace> Faces, int? Fps,
    float? StartTime, float? EndTime, int? StartFrame, int? EndFrame,
    ImmutableArray<int>? MaterialIndices, ImmutableArray<VfxInlineMaterial>? InlineMaterials,
    Vector3 BoundingCenter, float BoundingRadius, int? LegacyFlags, uint Flags, Vector2? LegacyFacingSize,
    ImmutableArray<VfxFaceVertex> FaceVertices, byte? IsKeyframed, ImmutableArray<VfxMeshFrame> Frames,
    VfxTransform? Pivot, VfxKeyLists? Keys) : VfxSection
{
    public override uint Tag => VfxSectionTag.Mesh;

    public int MaterialCount => MaterialIndices?.Length ?? InlineMaterials?.Length ?? 0;

    public bool IsMorph => (Flags & VfxMeshFlags.Morph) != 0;

    /// <summary>Which optional blocks frame <paramref name="index"/> carries at <paramref name="version"/>.</summary>
    public static VfxFrameLayout FrameLayout(int version, uint flags, bool keyframed, int index)
    {
        bool facing = (flags & VfxMeshFlags.Facing) != 0, rod = (flags & VfxMeshFlags.FacingRod) != 0;
        bool morph = (flags & VfxMeshFlags.Morph) != 0, dump = (flags & VfxMeshFlags.DumpUvs) != 0;
        bool geo = morph || index == 0;
        return new VfxFrameLayout(geo,
            geo && (facing || rod) && VfxVersion.HasFacingSize(version),
            geo && rod && index == 0 && VfxVersion.HasUpVector(version),
            (dump || index == 0) && VfxVersion.HasUvFrames(version),
            !morph && (!keyframed || (VfxVersion.HasFirstFrameOnlyTransformForKeyframed(version) && index == 0)),
            VfxVersion.HasFramePad(version),
            VfxVersion.HasFrameOpacity(version));
    }

    /// <summary>Decoded frame positions (Center + Raw * Multiplier), or null when the frame carries none.</summary>
    public Vector3[]? DecodePositions(int frame) =>
        Frames[frame].Positions is { } p ? Animation.VfxPositionCodec.Decode(p) : null;
}

public readonly record struct VfxFrameLayout(bool Positions, bool FacingSize, bool UpVector, bool Uvs, bool Transform, bool Pad, bool Opacity);

/// <summary>A material section (>=0x40000).</summary>
public sealed record VfxMaterial(int Type, int? Fps, byte? Additive, VfxTexture? Texture0, VfxTexture? Texture1,
    int? LegacyFps, ImmutableArray<float>? Mix, Vector3? SpecularGlossReflection, string? ReflectionTexture,
    VfxColorI? SolidColor, ImmutableArray<float> SelfIllumination, ImmutableArray<float>? Opacity) : VfxSection
{
    public override uint Tag => VfxSectionTag.Material;
}

/// <summary>Particle material inlined before 0x40000. Type is absent for drops systems.</summary>
public sealed record VfxParticleMaterial(int? Type, byte? Additive, string? Texture0, int? Texture0Rate,
    string? Texture1, int? Texture1Rate, ImmutableArray<float>? Mix, VfxColorI? DropsColor, float? SelfIllumination);

public sealed record VfxParticleFrame(Vector3 Position, Quaternion Orientation, float Width, float Height,
    float DropSize, float Speed, float SpeedVariation, float BirthRate, float? Opacity);

public sealed record VfxParticleSystem(string Name, string Parent, byte SaveParent, uint? Flags,
    ImmutableArray<string> Warps, int StartTime, int? MaterialIndex, VfxParticleMaterial? InlineMaterial,
    int ParticleCount, int Start, int Lifetime, float LifetimeVariation, int EmitterType, int? LegacyFlags,
    Vector2? Shrink, int? LegacyShrinkBirth, int? LegacyShrinkDeath, Vector2? Fade, float? TailDistance,
    ImmutableArray<byte>? LegacyBlob, ImmutableArray<VfxParticleFrame> Frames) : VfxSection
{
    public override uint Tag => VfxSectionTag.ParticleSystem;

    public bool IsDrops => ((Flags ?? 0) & 0x100) != 0;
}

public sealed record VfxDummyFrame(Vector3 Position, Quaternion Orientation);

public sealed record VfxDummy(string Name, string Parent, byte SaveParent, Vector3 Position, Quaternion Orientation,
    ImmutableArray<VfxDummyFrame> Frames) : VfxSection
{
    public override uint Tag => VfxSectionTag.Dummy;
}

public sealed record VfxLightParams(Vector3 Position, float Radius, float Multiplier, Vector3 Color, byte IsOn);

public sealed record VfxLight(string Name, string Parent, byte SaveParent, VfxLightParams Initial,
    ImmutableArray<VfxLightParams> Frames) : VfxSection
{
    public override uint Tag => VfxSectionTag.Light;
}

public sealed record VfxSpacewarpFrame(Vector3 Position, Quaternion Orientation, float Strength, float Decay,
    float Turbulence, float Frequency, float Scale);

public sealed record VfxSpacewarp(string Name, string Parent, int Type, ImmutableArray<VfxSpacewarpFrame> Frames) : VfxSection
{
    public override uint Tag => VfxSectionTag.Spacewarp;
}
