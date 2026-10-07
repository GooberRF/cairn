namespace Cairn.Formats.Rfl;

/// <summary>Which editor generation saved a level, judged from its format version.</summary>
public enum RflEra
{
    /// <summary>A version no released game loads (hand-edited, RF2, or not a real level).</summary>
    Unknown,
    /// <summary>Volition's internal and pre-release PC levels.</summary>
    PreRelease,
    /// <summary>PlayStation 2 levels (versions 174 and 175).</summary>
    PlayStation2,
    /// <summary>Official Volition levels as shipped with RF 1.0 (version 180).</summary>
    Stock10,
    /// <summary>Community levels saved by the RED 1.2 editor (version 200).</summary>
    Stock12,
    /// <summary>Levels saved by Alpine Faction's editor (versions 300 and up); only Alpine Faction loads them.</summary>
    Alpine,
}

/// <summary>What a level's string names: the kind decides where the asset would be looked up.</summary>
public enum RflReferenceKind
{
    Texture,
    Mesh,
    Clip,
    Effect,
    Sound,
    Music,
    Video,
    Level,
}

/// <summary>Where a referenced file was found, as answered by the caller's presence check.</summary>
public enum AssetPresence
{
    /// <summary>Not checked (no presence function, or it could not tell).</summary>
    Unknown,
    /// <summary>In the same packfile as the level.</summary>
    SamePackfile,
    /// <summary>Elsewhere in the game data (stock packfiles or game folders).</summary>
    GameData,
    /// <summary>Nowhere the game would look.</summary>
    Missing,
}

/// <summary>How far a section's body was understood.</summary>
public enum RflSectionStatus
{
    /// <summary>Read as far as the summary needs.</summary>
    Read,
    /// <summary>A known section the summary does not need to look inside (lightmaps, editor notes).</summary>
    Skipped,
    /// <summary>An id this reader does not know (newer editors, other editors' data); skipped by its length.</summary>
    Unknown,
    /// <summary>The body did not match the expected layout; at most its record count was kept.</summary>
    Damaged,
    /// <summary>The section claims more bytes than the file has; reading stopped here.</summary>
    Truncated,
}

/// <summary>An RGBA colour as stored in a level (four bytes).</summary>
public readonly record struct RflColor(byte R, byte G, byte B, byte A)
{
    /// <summary>"R, G, B" (alpha is always 255 in practice and left out).</summary>
    public override string ToString() => A == 255 ? $"{R}, {G}, {B}" : $"{R}, {G}, {B}, alpha {A}";
}

/// <summary>The stock level properties section.</summary>
public sealed record RflLevelProperties(
    string GeomodTexture,
    int Hardness,
    RflColor AmbientColor,
    bool DirectionalAmbient,
    RflColor FogColor,
    float FogNear,
    float FogFar)
{
    /// <summary>A far distance of zero means the level has no fog.</summary>
    public bool HasFog => FogFar != 0;
}

/// <summary>
/// Alpine Faction's level properties. The chunk grew fields without always raising its version, so
/// every field is null when the file stops before it; the game then uses the default named in each
/// property's documentation.
/// </summary>
public sealed record AlpineLevelProperties
{
    public uint ChunkVersion { get; init; }
    /// <summary>Default true.</summary>
    public bool? LegacyCyclicTimers { get; init; }
    /// <summary>Default true.</summary>
    public bool? LegacyMovers { get; init; }
    /// <summary>Default true.</summary>
    public bool? StartsWithHeadlamp { get; init; }
    /// <summary>Default false.</summary>
    public bool? OverrideStaticMeshAmbient { get; init; }
    /// <summary>Default 2.0.</summary>
    public float? StaticMeshAmbientModifier { get; init; }
    /// <summary>Default false.</summary>
    public bool? Rf2StyleGeomod { get; init; }
    public int? GeoableBrushes { get; init; }
    public int? BreakableBrushes { get; init; }
    public int? HoldOpenKeyframes { get; init; }
    public bool? SunEnabled { get; init; }
    public float? SunYaw { get; init; }
    public float? SunPitch { get; init; }
    public RflColor? SunColor { get; init; }
    public float? SunIntensity { get; init; }
    public float? SunSpread { get; init; }
    public bool? SunCastsBakedShadows { get; init; }
    public bool? SunAffectsMeshes { get; init; }
    public int? SunMeshMode { get; init; }
    public bool? SunDrivesShadowMap { get; init; }
    public bool? LegacyLighting { get; init; }
    public bool? HighResolutionLightmaps { get; init; }
    public bool? LiquidOccludes { get; init; }
    public bool? InvisibleFacesOcclude { get; init; }
    public bool? AlphaFacesOcclude { get; init; }
    public int? NoShadowBrushes { get; init; }
    public bool? MeshesOcclude { get; init; }
    /// <summary>0 = default, 1..128 texels per unit, 255 = off.</summary>
    public int? LightmapDensity { get; init; }
    /// <summary>Raw flag byte; bit 0 = the file carries no stock lightmaps (Direct3D 11 renderer only).</summary>
    public int? LightmapFlags { get; init; }
    public int? LightmapCompression { get; init; }
    public bool? FlightCeilingEnabled { get; init; }
    public float? FlightCeilingY { get; init; }
    public bool? MinimapEnabled { get; init; }
    public string? MinimapBitmap { get; init; }
    public float? MinimapCutHeight { get; init; }

    /// <summary>True when the level ships no stock lightmaps and needs Alpine's Direct3D 11 renderer.</summary>
    public bool D3D11OnlyLightmaps => LightmapFlags is { } f && (f & 1) != 0;

    /// <summary>True when the chunk ended before every field its version promises.</summary>
    public bool EndedEarly { get; init; }
}

/// <summary>A count of one kind of thing in the level ("Lights", "Triggers").</summary>
public sealed record RflCount(string Label, int Count);

/// <summary>A file the level names, with every place it is named from ("level geometry", "event Play_Sound").</summary>
public sealed record RflReference(string Name, RflReferenceKind Kind, IReadOnlyList<string> Places);

/// <summary>RED's preload lists, kept as they are (they include stock assets the level's objects imply).</summary>
public sealed record RflPreloads(
    IReadOnlyList<string> Bitmaps,
    IReadOnlyList<string> CharacterMeshes,
    IReadOnlyList<string> Animations,
    IReadOnlyList<string> StaticMeshes,
    IReadOnlyList<string> Effects)
{
    public static RflPreloads Empty { get; } = new([], [], [], [], []);

    public int Total => Bitmaps.Count + CharacterMeshes.Count + Animations.Count + StaticMeshes.Count + Effects.Count;
}

/// <summary>One section of the level's section stream.</summary>
public sealed record RflSectionInfo(uint Id, string Name, long Offset, int Length, RflSectionStatus Status, string? Detail);

/// <summary>
/// What a details pane shows for a level: identity, properties, statistics and the files it names.
/// Built by <see cref="RflReader"/>; everything the file did not contain is null, zero or empty.
/// </summary>
public sealed record RflSummary
{
    /// <summary>The file name used in messages.</summary>
    public required string Name { get; init; }
    public long FileSize { get; init; }
    public int Version { get; init; }
    public RflEra Era { get; init; }
    /// <summary>"RF 1.0 (180)", "RED 1.2 (200)", "Alpine 1.5.0 (306)", "PS2 (174)", "unsupported (45)".</summary>
    public string VersionLabel { get; init; } = "";
    /// <summary>The header's save time (UTC); null when the version has none or it is zero.</summary>
    public DateTimeOffset? SavedUtc { get; init; }
    /// <summary>Level name from the level info section, else the header's.</summary>
    public string? LevelName { get; init; }
    /// <summary>The header's level name, only when it differs from <see cref="LevelName"/>.</summary>
    public string? HeaderLevelName { get; init; }
    public string? Author { get; init; }
    /// <summary>The editor's own date text (local wall-clock time of the save, long US form).</summary>
    public string? DateText { get; init; }
    /// <summary>The mod the level requires; null when none.</summary>
    public string? ModName { get; init; }
    public bool? IsMultiplayer { get; init; }
    public bool? HasMovers { get; init; }
    public RflLevelProperties? Properties { get; init; }
    public AlpineLevelProperties? Alpine { get; init; }
    /// <summary>Dash Faction's "full-depth lightmaps" flag, when the level carries Dash Faction properties.</summary>
    public int? DashLightmapsFullDepth { get; init; }
    public bool HasAlpineLightmaps { get; init; }
    /// <summary>True when the RED+ editor left its own data in the file.</summary>
    public bool HasRedPlusData { get; init; }
    /// <summary>True when the Glacier editor left its own data in the file.</summary>
    public bool HasGlacierData { get; init; }

    /// <summary>Static geometry statistics; null when the geometry section is missing or unreadable.</summary>
    public int? Rooms { get; init; }
    public int? Faces { get; init; }
    public int? Vertices { get; init; }
    public int? Portals { get; init; }
    public int? SkyRooms { get; init; }
    public int? LiquidRooms { get; init; }
    public int? GeometryTextures { get; init; }

    /// <summary>Counts of the sections present, in file order.</summary>
    public IReadOnlyList<RflCount> Counts { get; init; } = [];
    public int OmniLights { get; init; }
    public int SpotLights { get; init; }
    public int TubeLights { get; init; }
    public int RespawnRed { get; init; }
    public int RespawnBlue { get; init; }
    public int RespawnBot { get; init; }
    public bool HasPlayerStart { get; init; }
    public IReadOnlyDictionary<string, int> EntityClasses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ItemClasses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> ClutterClasses { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> EventClasses { get; init; } = new Dictionary<string, int>();

    /// <summary>Files the level names, distinct ignoring case (first spelling kept), in first-seen order.</summary>
    public IReadOnlyList<RflReference> References { get; init; } = [];
    public RflPreloads Preloads { get; init; } = RflPreloads.Empty;
    public IReadOnlyList<RflSectionInfo> Sections { get; init; } = [];
    /// <summary>Oddities that did not stop the read (truncation, unknown versions, layout fallbacks).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>True when the file ends before its end marker.</summary>
    public bool IsTruncated { get; init; }

    /// <summary>The count recorded under <paramref name="label"/>, or null when that section is absent.</summary>
    public int? Count(string label)
    {
        foreach (var c in Counts)
        {
            if (c.Label == label) return c.Count;
        }
        return null;
    }

    /// <summary>The references of one kind.</summary>
    public IEnumerable<RflReference> ReferencesOf(RflReferenceKind kind) => References.Where(r => r.Kind == kind);
}
