namespace Cairn.Lvl.Model;

/// <summary>Alpine brush flags (from the level properties' brush lists or a group file's brush metadata chunk).</summary>
[Flags]
public enum RfAlpineBrushFlags : byte
{
    None = 0,
    Geoable = 0x01,
    /// <summary>Breaks with <see cref="RfAlpineBrushData.Material"/>.</summary>
    Breakable = 0x02,
    NoShadowCast = 0x04,
}

/// <summary>Alpine data attached to one brush.</summary>
/// <param name="Flags">Geoable, breakable, no shadow cast.</param>
/// <param name="Material">Breakable material: bits 0-6 the material index, bit 7 "no debris".</param>
/// <param name="ExtraBytes">Group files only: entry bytes beyond the six this version knows, as stored.</param>
public sealed record RfAlpineBrushData(RfAlpineBrushFlags Flags, byte Material, ImmutableArray<byte> ExtraBytes);

/// <summary>An editor brush (level brushes, mover brushes and group brushes share this record).</summary>
public sealed record RfBrush
{
    public int Uid { get; init; } = -1;
    public Vector3 Position { get; init; }
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    public RfGeometry Geometry { get; init; } = RfGeometry.Empty;

    /// <summary>Solid flags as stored (RF1: portal, air, detail, emits steam; RF2 has more).</summary>
    public RfSolidFlags SolidFlags { get; init; }

    /// <summary>Files before 0x51: the two bytes stored instead of the flags.</summary>
    public (byte A, byte B)? LegacyFlags { get; init; }

    /// <summary>Files before 0x80: the extra i32 after the flags (values 0, 2, 3, 4, 8 in the corpus).</summary>
    public int? LegacyExtra { get; init; }

    /// <summary>-1 = indestructible.</summary>
    public int Life { get; init; } = -1;

    /// <summary>Editor state: 0 normal, 2 locked, 3 selected.</summary>
    public int State { get; init; }

    /// <summary>Alpine geoable / breakable / no-shadow data, when the level or group gives the brush any.</summary>
    public RfAlpineBrushData? Alpine { get; init; }

    /// <summary>RF2 only: the 29 bytes stored after the brush when solid flags 0x04 and 0x08 are both set.</summary>
    public ImmutableArray<byte> Rf2Extra { get; init; } = [];

    /// <summary>The flags RED keeps (portal, air, detail, emits steam).</summary>
    public RfSolidFlags Rf1SolidFlags => SolidFlags & (RfSolidFlags.Portal | RfSolidFlags.Air | RfSolidFlags.Detail | RfSolidFlags.EmitsSteam);

    public bool IsPortal => (SolidFlags & RfSolidFlags.Portal) != 0;
    public bool IsAir => (SolidFlags & RfSolidFlags.Air) != 0;
    public bool IsDetail => (SolidFlags & RfSolidFlags.Detail) != 0;

    /// <summary>A vertex of the brush in world space (orientation applied, then position).</summary>
    public Vector3 ToWorld(Vector3 local) => Orientation.Transform(local, Position);
}
