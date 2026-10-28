using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>
/// Red Faction II level properties that follow the RF1 fields of the level properties section: the sun and
/// hardlight of RF2's light bake and the lightmap multiplier.
/// </summary>
public sealed record Rf2LevelProperties
{
    public RflColor SunColor { get; init; }
    public float SunYaw { get; init; }
    public float SunPitch { get; init; }
    public float SunIntensity { get; init; }
    public float SunSpread { get; init; }
    /// <summary>A full-screen subtractive tint (the only field here the game itself uses).</summary>
    public RflColor HardlightColor { get; init; }
    public int HardlightIntensity { get; init; }
    /// <summary>Scales the level's baked light (1 in about half the levels).</summary>
    public float LightmapMultiplier { get; init; } = 1f;
}

/// <summary>One triangle of the RF2 vertex lighting data: three vertex indices and the baked colour at each corner.</summary>
public readonly record struct Rf2LitTriangle(int A, int B, int C, RflColor ColorA, RflColor ColorB, RflColor ColorC);

/// <summary>One entry of the RF2 vertex lighting data: lit geometry with its baked corner colours.</summary>
public sealed record Rf2LitEntry
{
    public int TextureIndex { get; init; }
    public ImmutableArray<Vector3> Vertices { get; init; } = [];
    public ImmutableArray<Rf2LitTriangle> Triangles { get; init; } = [];
}

/// <summary>
/// The RF2 vertex lighting data (section 0x7900): a second copy of the lit geometry with the colours RF2's light
/// bake gave every corner. Converters use it to fit corona-derived lights and fill lights; the preview uses the
/// faces' own corner colours instead.
/// </summary>
public sealed record Rf2VertexLighting
{
    public int Version { get; init; }
    public ImmutableArray<string> TextureNames { get; init; } = [];
    /// <summary>Number of stored light probes (pairs of floats, not used).</summary>
    public int ProbeCount { get; init; }
    public ImmutableArray<Rf2LitEntry> Entries { get; init; } = [];

    /// <summary>Total triangles over all entries.</summary>
    public int TriangleCount => Entries.Sum(e => e.Triangles.Length);
}

/// <summary>The fields of an RF2 event that RF1 events do not have, as stored.</summary>
public sealed record Rf2EventData(int Lead1, int Lead2, int Int3, int Int4, int Tail, string Str3);

/// <summary>The fields of an RF2 trigger that RF1 triggers do not have, as stored.</summary>
public sealed record Rf2TriggerData(float Unknown, byte Byte, ushort Flags, uint Flags2);

/// <summary>The three fields RF2 decals store after the RF1 decal fields.</summary>
public sealed record Rf2DecalData(float Unknown1, int Unknown2, int Unknown3);

/// <summary>The fields of an RF2 group or moving group that RF1 groups do not have, as stored.</summary>
/// <param name="Flags">The four bytes after the leading byte.</param>
/// <param name="MovingByte">The byte inside the moving block before the movement type.</param>
/// <param name="Unknown">The i32 before the member lists.</param>
/// <param name="ParentUid">-1 when the group has no parent.</param>
/// <param name="ParentPosition">Stored only when there is a parent.</param>
/// <param name="ParentOrientation">Stored only when there is a parent.</param>
/// <param name="Tail">The nine i32 at the end.</param>
public sealed record Rf2GroupData(ImmutableArray<byte> Flags, byte MovingByte, int Unknown, int ParentUid,
    Vector3? ParentPosition, RfMatrix? ParentOrientation, ImmutableArray<int> Tail);

/// <summary>Red Faction II data of a level that has no place in the RF1 model.</summary>
public sealed record Rf2LevelData
{
    /// <summary>The RF2 fields of the level properties.</summary>
    public Rf2LevelProperties? Properties { get; init; }
    /// <summary>The baked vertex lighting (section 0x7900).</summary>
    public Rf2VertexLighting? VertexLighting { get; init; }
    /// <summary>The single byte of section 0x7901.</summary>
    public byte? Section7901 { get; init; }
}
