using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>
/// One corner of a face. Texture and lightmap coordinates belong to the corner, not to the vertex: the same vertex
/// has other coordinates on its other faces.
/// </summary>
/// <param name="Vertex">Index into <see cref="RfGeometry.Vertices"/>.</param>
/// <param name="Uv">Texture coordinates.</param>
/// <param name="LightmapUv">Lightmap coordinates; present exactly when the face has a lightmap surface.</param>
/// <param name="Color">RF2 vertex colour (0x80 is neutral); null for RF1 levels.</param>
public readonly record struct RfCorner(int Vertex, Vector2 Uv, Vector2? LightmapUv = null, RflColor? Color = null);

/// <summary>RF2-only face data, kept raw (RF2 levels are read and converted, never written).</summary>
/// <param name="Unknown">The i32 after the face id.</param>
/// <param name="ScrollPeriod">The two scroll periods (seconds per cycle) stored when flag 0x8000 is set.</param>
/// <param name="EmissiveColor">The three bytes after the smoothing groups (looks like an RGB colour).</param>
/// <param name="EmissiveIntensity">The float after them.</param>
public sealed record Rf2FaceData(int Unknown, Vector2? ScrollPeriod, (byte R, byte G, byte B) EmissiveColor, float EmissiveIntensity)
{
    /// <summary>The surface index as stored. RF2 faces have no lightmap surfaces (corners carry vertex colours), so <see cref="RfFace.Surface"/> is -1.</summary>
    public int StoredSurface { get; init; } = -1;
}

/// <summary>A face of a solid (compiled geometry, brush or mover).</summary>
public sealed record RfFace
{
    /// <summary>Plane as stored (files from version 0xA7); null in older files.</summary>
    public RfPlane? Plane { get; init; }

    /// <summary>Index into <see cref="RfGeometry.Textures"/>; -1 = no texture.</summary>
    public int Texture { get; init; } = -1;

    /// <summary>
    /// Index into <see cref="RfGeometry.Surfaces"/> as stored. Only the low 16 bits count: RF keeps it as a 16-bit
    /// value, and Alpine writes 0x0000FFFF for "none". See <see cref="HasSurface"/>.
    /// </summary>
    public int Surface { get; init; } = -1;

    /// <summary>Face id (ties scroll data and Alpine data to faces); RED leaves -1 on many brush faces.</summary>
    public int FaceId { get; init; } = -1;

    public int ReservedA { get; init; } = -1;
    public int ReservedB { get; init; } = -1;

    /// <summary>Compiled geometry: portal index + 2 for portal faces, else 0. Brush faces: 0 (REDUX writes -1).</summary>
    public int PortalPlus2 { get; init; }

    /// <summary>The face flags with their RF1 meaning.</summary>
    public RfFaceFlags Flags { get; init; }

    /// <summary>The flags exactly as stored (RF1: a u32 whose high half is 0; RF2: 32 bits of its own).</summary>
    public uint RawFlags { get; init; }

    /// <summary>One bit per smoothing group.</summary>
    public uint Smoothing { get; init; }

    /// <summary>Room index in compiled geometry, -1 in brushes and movers.</summary>
    public int Room { get; init; } = -1;

    public ImmutableArray<RfCorner> Corners { get; init; } = [];

    /// <summary>Scroll speed in texture repeats per second, from the solid's scroll list (matched by face id).</summary>
    public Vector2? Scroll { get; init; }

    /// <summary>RF2 face data; null for RF1 levels.</summary>
    public Rf2FaceData? Rf2 { get; init; }

    /// <summary>True when the face has a lightmap surface (and so lightmap coordinates on every corner).</summary>
    public bool HasSurface => SurfaceHasLightmapUvs(Surface);

    /// <summary>The 16-bit rule RF itself applies to the stored surface index.</summary>
    public static bool SurfaceHasLightmapUvs(int surface) => (short)(surface & 0xFFFF) >= 0;
}

/// <summary>Liquid properties of a liquid room or a liquid room effect.</summary>
public sealed record RfLiquidProperties
{
    public float Depth { get; init; }
    public RflColor Color { get; init; }
    public string SurfaceTexture { get; init; } = "";
    public float Visibility { get; init; }
    /// <summary>1 water, 2 lava, 3 acid.</summary>
    public int LiquidType { get; init; } = 1;
    /// <summary>Rooms only (room effects do not store it).</summary>
    public int Alpha { get; init; } = 60;
    public bool Plankton { get; init; }
    public int PixelsPerMeterU { get; init; } = 64;
    public int PixelsPerMeterV { get; init; } = 64;
    public float Angle { get; init; } = 180f;
    public int Waveform { get; init; }
    public Vector2 Scroll { get; init; }
}

/// <summary>A room of the compiled geometry.</summary>
public sealed record RfRoom
{
    /// <summary>Room-effect uid or a large editor id; -1 when not stored.</summary>
    public int Id { get; init; } = -1;
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public bool IsSky { get; init; }
    public bool IsCold { get; init; }
    public bool IsOutside { get; init; }
    public bool IsAirlock { get; init; }
    public bool IsLiquid => Liquid is not null;
    /// <summary>A detail room (inside another room).</summary>
    public bool IsSubroom { get; init; }
    public bool HasAlpha { get; init; }
    /// <summary>-1 = indestructible.</summary>
    public float Life { get; init; } = -1f;
    public string EaxEffect { get; init; } = "";
    public RfLiquidProperties? Liquid { get; init; }
    /// <summary>Set when the room has its own ambient light.</summary>
    public RflColor? AmbientColor { get; init; }
    /// <summary>RF2 room data kept raw (the four flag-like bytes after the bounding box, then the 40 bytes after the EAX effect); empty for RF1.</summary>
    public ImmutableArray<byte> Rf2Data { get; init; } = [];
}

/// <summary>A portal between two rooms.</summary>
public readonly record struct RfPortal(int RoomA, int RoomB, Vector3 Point1, Vector3 Point2);

/// <summary>A room and the rooms it contains.</summary>
public sealed record RfSubroomList(int Room, ImmutableArray<int> Subrooms);

/// <summary>A lightmap placement (one per lit face, rarely shared).</summary>
public sealed record RfSurface
{
    /// <summary>Index into the level's lightmaps.</summary>
    public int Lightmap { get; init; }
    public byte X { get; init; }
    public byte Y { get; init; }
    public byte Width { get; init; }
    public byte Height { get; init; }
    public Vector2 PixelsPerMeter { get; init; }
    public Vector3 BoundsMin { get; init; }
    public Vector3 BoundsMax { get; init; }
    public RfPlane Plane { get; init; }
    public int ShouldSmooth { get; init; }
    public int Unknown { get; init; }
    /// <summary>0 x, 1 y, 2 z.</summary>
    public int DroppedAxis { get; init; }
    public int UAxis { get; init; }
    public int VAxis { get; init; }
    public Vector2 UvAdd { get; init; }
    public Vector2 UvScale { get; init; }
    public int Room { get; init; } = -1;
    /// <summary>Old files (before 0x73): the projection basis (12 floats) and the sample list, kept as stored.</summary>
    public ImmutableArray<float> LegacyData { get; init; } = [];
}

/// <summary>A scroll-list entry whose face id matches no face of the solid (kept so nothing is lost).</summary>
public readonly record struct RfScrollEntry(int FaceId, Vector2 Speed);

/// <summary>
/// A solid: the compiled level geometry, or the geometry of one brush or mover. Brushes and movers have no rooms,
/// portals or sub-room lists; movers (and some level brushes) have lightmap surfaces.
/// </summary>
public sealed record RfGeometry
{
    public static RfGeometry Empty { get; } = new();

    /// <summary>Stored from version 0xC8 before the name; 0 everywhere.</summary>
    public uint Unknown { get; init; }
    public uint Modifiability { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Texture names; face texture indices point into this list.</summary>
    public ImmutableArray<string> Textures { get; init; } = [];
    public ImmutableArray<RfRoom> Rooms { get; init; } = [];
    public ImmutableArray<RfSubroomList> SubroomLists { get; init; } = [];
    public ImmutableArray<RfPortal> Portals { get; init; } = [];
    public ImmutableArray<Vector3> Vertices { get; init; } = [];
    public ImmutableArray<RfFace> Faces { get; init; } = [];
    public ImmutableArray<RfSurface> Surfaces { get; init; } = [];
    /// <summary>Scroll entries that no face claimed (by face id).</summary>
    public ImmutableArray<RfScrollEntry> OrphanScrolls { get; init; } = [];
    /// <summary>The old 41-byte records of files up to version 0xB4 (meaning unknown), as stored.</summary>
    public ImmutableArray<byte> LegacyRecords { get; init; } = [];
    /// <summary>RF2 only: the extra 8-byte room link records, as stored.</summary>
    public ImmutableArray<byte> Rf2RoomLinks { get; init; } = [];

    /// <summary>The name of a face's texture, or null when it has none.</summary>
    public string? TextureOf(RfFace face) =>
        face.Texture >= 0 && face.Texture < Textures.Length ? Textures[face.Texture] : null;

    /// <summary>Total corners over all faces.</summary>
    public long CornerCount
    {
        get
        {
            long n = 0;
            foreach (var f in Faces) n += f.Corners.Length;
            return n;
        }
    }
}
