using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>A texture override of an Alpine mesh.</summary>
public sealed record RfTextureOverride(byte Slot, string Texture);

/// <summary>The clutter block of an Alpine mesh (meshes that behave like clutter).</summary>
public sealed record RfAlpineMeshClutter
{
    public float Life { get; init; }
    public string Debris { get; init; } = "";
    public string ExplosionVclip { get; init; } = "";
    public float ExplosionRadius { get; init; }
    public float DebrisVelocity { get; init; }
    /// <summary>The 11 damage-type factors.</summary>
    public ImmutableArray<float> DamageFactors { get; init; } = [];
    /// <summary>Null in the builds that did not store the corpse fields.</summary>
    public string? Corpse { get; init; }
    public string? CorpseAnim { get; init; }
    public byte? CorpseCollision { get; init; }
    /// <summary>-1 = automatic.</summary>
    public sbyte? CorpseMaterial { get; init; }
}

/// <summary>Which record tail an Alpine mesh chunk was stored with (it changed between builds without a version).</summary>
public enum RfAlpineMeshLayout
{
    /// <summary>The current layout (material i32, clutter block with corpse fields).</summary>
    Full,
    /// <summary>Clutter block without the corpse fields.</summary>
    NoCorpse,
    /// <summary>Early 304: nothing after the texture overrides.</summary>
    NoTail,
    /// <summary>A dev-era build with a one-byte material.</summary>
    ByteMaterial,
}

/// <summary>An Alpine static mesh (chunk 0x0AFBAE01).</summary>
public sealed record RfAlpineMesh : RfPlacedObject
{
    /// <summary>.v3m, .v3c or .vfx.</summary>
    public string MeshFile { get; init; } = "";
    /// <summary>.rfa state animation.</summary>
    public string StateAnim { get; init; } = "";
    /// <summary>0 none, 1 weapons only, 2 all.</summary>
    public byte Collision { get; init; }
    public ImmutableArray<RfTextureOverride> TextureOverrides { get; init; } = [];
    /// <summary>0..9; null when the layout has no tail.</summary>
    public int? Material { get; init; }
    public RfAlpineMeshClutter? Clutter { get; init; }
    /// <summary>Version 306 trailing blocks (null when the chunk has none).</summary>
    public byte? TrailingFlags { get; init; }
    /// <summary>0..2.</summary>
    public byte? BrushGeometrySource { get; init; }
    public string? CollisionMesh { get; init; }
    public float? DrawScale { get; init; }
}

/// <summary>A corona (Alpine chunk 0x0AFBAE03; RF2's 0x7678 coronas fit the same record).</summary>
public sealed record RfCorona : RfPlacedObject
{
    public RflColor Color { get; init; } = RfColors.White;
    public string CoronaBitmap { get; init; } = "";
    public float ConeAngle { get; init; }
    public float Intensity { get; init; }
    public float RadiusDistance { get; init; }
    public float RadiusScale { get; init; }
    public float DiminishDistance { get; init; }
    public string VolumetricBitmap { get; init; } = "";
    public float VolumetricHeight { get; init; }
    public float VolumetricLength { get; init; }
    /// <summary>RF2 only: the flag bytes and other fields Alpine coronas do not have, as stored.</summary>
    public ImmutableArray<byte> Rf2Data { get; init; } = [];
}

/// <summary>A geoable brush of the Alpine level properties.</summary>
public readonly record struct RfAlpineGeoableBrush(int BrushUid, int RoomUid);

/// <summary>A breakable brush of the Alpine level properties.</summary>
public readonly record struct RfAlpineBreakableBrush(int BrushUid, int RoomUid, byte Material);

/// <summary>
/// Alpine level properties (0x0AFBA5ED). Alpine appended fields without raising the chunk version, so every field
/// is null when the chunk ended before it.
/// </summary>
public sealed record RfAlpineLevelProperties
{
    public uint ChunkVersion { get; init; }
    public bool? LegacyCyclicTimers { get; init; }
    public bool? LegacyMovers { get; init; }
    public bool? StartsWithHeadlamp { get; init; }
    public bool? OverrideStaticMeshAmbient { get; init; }
    public float? StaticMeshAmbientModifier { get; init; }
    public bool? Rf2StyleGeomod { get; init; }
    public ImmutableArray<RfAlpineGeoableBrush> GeoableBrushes { get; init; } = [];
    public ImmutableArray<RfAlpineBreakableBrush> BreakableBrushes { get; init; } = [];
    /// <summary>First keyframe uids of movers with "hold open".</summary>
    public ImmutableArray<int> HoldOpenKeyframes { get; init; } = [];
    public bool? SunEnabled { get; init; }
    public float? SunYaw { get; init; }
    public float? SunPitch { get; init; }
    public RflColor? SunColor { get; init; }
    public float? SunIntensity { get; init; }
    public float? SunSpread { get; init; }
    /// <summary>Nine bytes: cast baked shadows, affects meshes, mesh mode, drives shadow map, legacy lighting,
    /// high-res lightmaps, liquid occludes, invisible faces occlude, alpha faces occlude.</summary>
    public ImmutableArray<byte> SunOptions { get; init; } = [];
    public ImmutableArray<int> NoShadowBrushes { get; init; } = [];
    public bool? MeshesOcclude { get; init; }
    public byte? LightmapDensity { get; init; }
    /// <summary>Bit 0: stock lightmaps omitted (a D3D11-only level).</summary>
    public byte? LightmapFlags { get; init; }
    public byte? LightmapCompression { get; init; }
    public bool? FlightCeilingEnabled { get; init; }
    public float? FlightCeilingY { get; init; }
    public bool? MinimapEnabled { get; init; }
    public string? MinimapBitmap { get; init; }
    public Vector3? MinimapMin { get; init; }
    public Vector3? MinimapMax { get; init; }
    public float? MinimapCutHeight { get; init; }
    /// <summary>True when the chunk ended before the fields its version promises.</summary>
    public bool EndedEarly { get; init; }
    /// <summary>Bytes after the last field this reader knows (newer chunk versions).</summary>
    public int UnreadBytes { get; init; }
}

/// <summary>One entry of a group file's Alpine brush metadata chunk (0x0AFBAE05).</summary>
public sealed record RfBrushMetadataEntry(int BrushIndex, RfAlpineBrushFlags Flags, byte Material, ImmutableArray<byte> ExtraBytes);

/// <summary>An Alpine group file's brush metadata chunk; entries index the first group's brush list.</summary>
public sealed record RfBrushMetadata(uint Version, int EntrySize, ImmutableArray<RfBrushMetadataEntry> Entries);
