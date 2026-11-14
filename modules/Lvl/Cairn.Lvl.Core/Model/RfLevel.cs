using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>The level header.</summary>
public sealed record RfLevelHeader
{
    public int Version { get; init; }
    /// <summary>Unix time of the last save (from 0x72; 0 or null when not stored).</summary>
    public uint? Timestamp { get; init; }
    /// <summary>Header offsets and totals: informational only, often wrong, never used to read.</summary>
    public int PlayerStartOffset { get; init; }
    public int LevelInfoOffset { get; init; }
    public int? SectionCount { get; init; }
    public int? SectionsTotalSize { get; init; }
    public string LevelName { get; init; } = "";
    /// <summary>Null when the version does not store one (and in RF2 levels).</summary>
    public string? ModName { get; init; }
}

/// <summary>Level info (0x1000000): the leading fields (the editor views after them are not read).</summary>
public sealed record RfLevelInfo(int Unknown, string Name, string Author, string Date, bool HasMovers, bool Multiplayer);

/// <summary>Level properties (0x900).</summary>
public sealed record RfLevelProperties
{
    public string GeomodTexture { get; init; } = "";
    /// <summary>0..100.</summary>
    public int Hardness { get; init; }
    public RflColor AmbientColor { get; init; }
    public byte DirectionalAmbient { get; init; }
    public RflColor FogColor { get; init; }
    public float FogNear { get; init; }
    /// <summary>0 = no fog.</summary>
    public float FogFar { get; init; }
    /// <summary>RF2 only: the fields after the fog (sun, hardlight, lightmap multiplier), as stored.</summary>
    public ImmutableArray<byte> Rf2Data { get; init; } = [];
}

/// <summary>One of the preload lists (0x7000 bitmaps, 0x7001 character meshes, 0x7002 animations, 0x7003 static meshes, 0x7004 effects).</summary>
/// <param name="SectionId">The section the list came from.</param>
/// <param name="Names">File names.</param>
/// <param name="Counts">Reference counts, one per name (empty for the bitmap list).</param>
public sealed record RfFileList(uint SectionId, ImmutableArray<string> Names, ImmutableArray<int> Counts);

/// <summary>A stock lightmap (0x1200). Pixels are 24-bit RGB, read only when asked for.</summary>
public sealed record RfLightmap(int Width, int Height, ReadOnlyMemory<byte> Pixels);

/// <summary>Dash Faction level properties (0xDA58FA00).</summary>
public sealed record RfDashProperties(uint Version, bool? LightmapsFullDepth);

/// <summary>How a section was read.</summary>
public enum RfSectionStatus
{
    /// <summary>Every record read and the body consumed exactly.</summary>
    Read,
    /// <summary>Read exactly, but only with a fallback layout (the detail says which).</summary>
    ReadWithFallback,
    /// <summary>Kept as bytes by design (editor-only data, large Alpine chunks, foreign editors).</summary>
    Opaque,
    /// <summary>A section id this reader does not know; kept as bytes.</summary>
    Unknown,
    /// <summary>The body does not match any known layout; kept as bytes (and its count, where one is plausible).</summary>
    Damaged,
    /// <summary>The section claims more bytes than the file holds; reading stopped there.</summary>
    Truncated,
}

/// <summary>One section of the level's section stream and how it was read.</summary>
public sealed record RfSectionRecord(uint Id, string Name, long Offset, int Length, RfSectionStatus Status, string? Detail);

/// <summary>A section (or Alpine chunk) kept as bytes, with the reason.</summary>
public sealed record RfOpaqueSection(uint Id, string Name, long Offset, ReadOnlyMemory<byte> Body, string Reason)
{
    /// <summary>The leading count of Alpine object chunks (u32), or null when the body has none.</summary>
    public long? LeadingCount => Body.Length >= 4 ? BitConverter.ToUInt32(Body.Span) : null;
}

/// <summary>A Red Faction level (.rfl): compiled geometry, editor brushes, groups and every object.</summary>
public sealed record RfLevel
{
    public required string Name { get; init; }
    public RfLevelHeader Header { get; init; } = new();
    public int Version => Header.Version;
    public bool IsAlpine => Version >= 300;
    public bool IsRf2 => Version == 0x127 && Rf2Layout;
    /// <summary>True when the file was read with the RF2 layouts.</summary>
    public bool Rf2Layout { get; init; }

    public RfLevelInfo? Info { get; init; }
    public RfLevelProperties? Properties { get; init; }
    /// <summary>The compiled geometry the game draws (0x100).</summary>
    public RfGeometry StaticGeometry { get; init; } = RfGeometry.Empty;
    /// <summary>The editor's source brushes (0x2000000), mover brushes included.</summary>
    public ImmutableArray<RfBrush> Brushes { get; init; } = [];
    /// <summary>Mover brushes as the game reads them (0x2000), compiled per mover.</summary>
    public ImmutableArray<RfBrush> Movers { get; init; } = [];
    /// <summary>Editor groups (0x3000000), moving ones included.</summary>
    public ImmutableArray<RfLevelGroup> Groups { get; init; } = [];
    /// <summary>Moving groups as the game reads them (0x3000).</summary>
    public ImmutableArray<RfLevelGroup> MovingGroups { get; init; } = [];
    public RfObjectSet Objects { get; init; } = RfObjectSet.Empty;
    /// <summary>Lights that exist only in the editor (0x4000000).</summary>
    public ImmutableArray<RfLight> EditorLights { get; init; } = [];
    public ImmutableArray<RfCutscene> Cutscenes { get; init; } = [];
    public ImmutableArray<RfNamedUidList> CutscenePaths { get; init; } = [];
    public ImmutableArray<RfWaypointList> WaypointLists { get; init; } = [];
    public RfPlayerStart? PlayerStart { get; init; }
    public ImmutableArray<RfFileList> Preloads { get; init; } = [];
    public ImmutableArray<RfLightmap> Lightmaps { get; init; } = [];
    public RfAlpineLevelProperties? AlpineProperties { get; init; }
    public RfDashProperties? DashProperties { get; init; }
    /// <summary>Red Faction II data with no place in the RF1 model (RF2 levels only).</summary>
    public Rf2LevelData? Rf2 { get; init; }
    /// <summary>Red Faction for N-Gage data (levels read from an <c>.xff</c> scene only).</summary>
    public XffLevelData? Xff { get; init; }
    /// <summary>True for a Red Faction for N-Gage level (read from an <c>.xff</c> scene).</summary>
    public bool IsXff => Xff is not null;

    /// <summary>Sections kept as bytes (editor notes, Alpine lightmaps and terrain, foreign editors, unknown or damaged sections).</summary>
    public ImmutableArray<RfOpaqueSection> OpaqueSections { get; init; } = [];
    /// <summary>Every section in file order with how it was read.</summary>
    public ImmutableArray<RfSectionRecord> Sections { get; init; } = [];
    /// <summary>User-readable notes on anything that did not read cleanly.</summary>
    public ImmutableArray<string> Problems { get; init; } = [];
    /// <summary>The file ends early (no end marker, or a section runs past the end).</summary>
    public bool IsTruncated { get; init; }

    /// <summary>A brush by uid (editor brushes), or null.</summary>
    public RfBrush? FindBrush(int uid)
    {
        foreach (var b in Brushes)
            if (b.Uid == uid) return b;
        return null;
    }
}

/// <summary>One group of a group file: its brushes and objects.</summary>
public sealed record RfGroup
{
    public string Name { get; init; } = "";
    /// <summary>Null for a group that does not move.</summary>
    public RfMovingData? Moving { get; init; }
    /// <summary>The group's brushes (a moving group's mover brushes included).</summary>
    public ImmutableArray<RfBrush> Brushes { get; init; } = [];
    public RfObjectSet Objects { get; init; } = RfObjectSet.Empty;

    public bool IsMoving => Moving is not null;
}

/// <summary>Layout fallbacks a group file needed (see <see cref="RfGroupFile.Fallbacks"/>).</summary>
[Flags]
public enum RfGroupFallbacks
{
    None = 0,
    /// <summary>A file below 300 that stores the Alpine event matrices (Alpine RED saving with a stock version).</summary>
    AlpineEventMatricesBelow300 = 0x1,
    /// <summary>A pre-release Alpine file (300+) without the Alpine event matrices.</summary>
    NoAlpineEventMatrices = 0x2,
    /// <summary>A 0xAE file without the moving groups' no-player-collide byte.</summary>
    NoPlayerCollideAbsent = 0x4,
    /// <summary>REDUX output with event matrices for classes RF1 does not store them for (RED cannot read it).</summary>
    ReduxEventMatrices = 0x8,
}

/// <summary>A Red Faction editor group file (.rfg).</summary>
public sealed record RfGroupFile
{
    public required string Name { get; init; }
    public int Version { get; init; }
    /// <summary>The groups. Alpine objects (meshes, coronas) and Alpine brush data belong to the first one, as Alpine RED imports them.</summary>
    public ImmutableArray<RfGroup> Groups { get; init; } = [];
    /// <summary>The Alpine brush metadata chunk as stored (also applied to the first group's brushes as <see cref="RfBrush.Alpine"/>).</summary>
    public RfBrushMetadata? BrushMetadata { get; init; }
    /// <summary>Alpine chunks kept as bytes (notes, bags, weather, terrain and others), in file order.</summary>
    public ImmutableArray<RfOpaqueSection> OpaqueChunks { get; init; } = [];
    /// <summary>Offset of data after the groups that is not a known Alpine chunk (reading stopped there), or null.</summary>
    public long? UnknownDataOffset { get; init; }
    public RfGroupFallbacks Fallbacks { get; init; }
    public ImmutableArray<string> Problems { get; init; } = [];

    /// <summary>Every brush of every group.</summary>
    public IEnumerable<RfBrush> AllBrushes => Groups.SelectMany(g => g.Brushes);
}
