using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>
/// An orientation in RF space (left-handed, Y up) as three unit rows. A point in the object's own space maps to
/// the world as <c>position + x * Right + y * Up + z * Forward</c>. Stock sections store the rows forward, right,
/// up and Alpine chunks right, up, forward; the readers put them in their places, so this type never depends on
/// where it came from.
/// </summary>
public readonly record struct RfMatrix(Vector3 Right, Vector3 Up, Vector3 Forward)
{
    public static RfMatrix Identity { get; } = new(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);

    /// <summary>A direction in object space rotated into world space.</summary>
    public Vector3 Rotate(Vector3 local) => Right * local.X + Up * local.Y + Forward * local.Z;

    /// <summary>A point in object space placed in the world at <paramref name="position"/>.</summary>
    public Vector3 Transform(Vector3 local, Vector3 position) => position + Rotate(local);

    /// <summary>The same rotation as a row-vector <see cref="Matrix4x4"/> (rows right, up, forward) with a translation.</summary>
    public Matrix4x4 ToMatrix4x4(Vector3 position) => new(
        Right.X, Right.Y, Right.Z, 0,
        Up.X, Up.Y, Up.Z, 0,
        Forward.X, Forward.Y, Forward.Z, 0,
        position.X, position.Y, position.Z, 1);
}

/// <summary>A plane as stored with a face: unit normal and distance (points p on the plane have normal·p + d = 0).</summary>
public readonly record struct RfPlane(Vector3 Normal, float Distance);

/// <summary>A sound file and its volume (moving groups' start, looping, stop and close sounds).</summary>
public sealed record RfSound(string File, float Volume)
{
    public static RfSound None { get; } = new("", 1f);
}

/// <summary>
/// Face flags as RF1 and RED use them (the low 16 bits of the stored value). RF2's 32-bit flags are kept raw in
/// <see cref="RfFace.RawFlags"/>; <see cref="RfFace.Flags"/> holds their RF1 meaning.
/// </summary>
[Flags]
public enum RfFaceFlags : ushort
{
    None = 0,
    /// <summary>Drawn with the sky (a face of a sky room).</summary>
    ShowSky = 0x0001,
    Mirrored = 0x0002,
    /// <summary>The surface of a liquid room (compiled geometry only).</summary>
    LiquidSurface = 0x0004,
    /// <summary>Came from a detail brush (compiled geometry); an editor bit on brush faces.</summary>
    Detail = 0x0008,
    /// <summary>The texture scrolls (speed in <see cref="RfFace.Scroll"/>).</summary>
    ScrollTexture = 0x0010,
    /// <summary>Drawn without lighting. Not double-sided.</summary>
    FullBright = 0x0020,
    HasAlpha = 0x0040,
    /// <summary>Detail face whose texture has holes that shots pass through.</summary>
    HasHoles = 0x0080,
    /// <summary>Two bits of lightmap resolution (0..3).</summary>
    LightmapResolutionMask = 0x0300,
    /// <summary>Set on many faces; meaning unknown, kept as stored.</summary>
    Unknown0800 = 0x0800,
    Unknown1000 = 0x1000,
    /// <summary>Not drawn, still collides.</summary>
    Invisible = 0x2000,
}

/// <summary>Brush solid flags. RED keeps only portal, air, detail and emits steam; the rest are RF2's.</summary>
[Flags]
public enum RfSolidFlags : uint
{
    None = 0,
    Portal = 0x01,
    /// <summary>Subtracts (air) instead of adding (solid).</summary>
    Air = 0x02,
    /// <summary>Not cut by the level build; becomes detail geometry.</summary>
    Detail = 0x04,
    EmitsSteam = 0x10,
    /// <summary>RF2 only.</summary>
    Rf2Geoable = 0x20,
}

/// <summary>Light flags; bits 4-5 are the light type and bits 8-11 the initial state.</summary>
[Flags]
public enum RfLightFlags : uint
{
    None = 0,
    Dynamic = 0x01,
    Fade = 0x02,
    ShadowCasting = 0x04,
    Enabled = 0x08,
    TypeMask = 0x30,
    InitialStateMask = 0xF00,
    RuntimeShadow = 0x2000,
}

/// <summary>Light type from bits 4-5 of the light flags.</summary>
public enum RfLightType
{
    Unknown = 0,
    Omni = 1,
    Spot = 2,
    Tube = 3,
}

/// <summary>Helpers on the shared colour type of the level readers.</summary>
public static class RfColors
{
    public static RflColor White { get; } = new(255, 255, 255, 255);

    /// <summary>RED's default event editor colour.</summary>
    public static RflColor DefaultEventColor { get; } = new(235, 215, 0, 255);

    /// <summary>RED's default room ambient colour.</summary>
    public static RflColor DefaultAmbient { get; } = new(100, 100, 100, 255);
}
