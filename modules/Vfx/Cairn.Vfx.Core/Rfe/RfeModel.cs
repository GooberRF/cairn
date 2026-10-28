using Cairn.Rfa.Formats.Legacy;

namespace Cairn.Vfx.Rfe;

/// <summary>One key of a time-keyed track: <paramref name="Time"/> in seconds of effect time, lerped between keys.</summary>
public readonly record struct RfeKey(float Time, float Value);

/// <summary>
/// A material of a Red Faction II effect: a texture, blend bits and two time-keyed tracks. The renderer tests blend bit 2
/// (additive, probable) and bit 4 (another additive-style mode); bit 1 is not understood.
/// </summary>
/// <param name="Texture">Index into <see cref="RfeFile.Textures"/>.</param>
/// <param name="Blend">The blend bits as stored (0 to 4 in the stock files).</param>
/// <param name="SelfIllumination">Track 1: for meshes the RGB level (probably self-illumination, as RF1's list).</param>
/// <param name="Opacity">Track 2: opacity (particles use key 0's value only).</param>
public sealed record RfeMaterial(int Texture, uint Blend, ImmutableArray<RfeKey> SelfIllumination, ImmutableArray<RfeKey> Opacity)
{
    /// <summary>Blend bit 2 or 4: the colour is multiplied by the opacity and added (bit 4 is taken as additive too, approximated).</summary>
    public bool IsAdditive => (Blend & 6) != 0;
}

/// <summary>
/// How a mesh object faces the camera (<c>rf2.exe</c> 0x566082: both modes turn the mesh's local +Z towards the camera
/// position, so its local XY plane faces it; see <see cref="RfeSampler.FacingRotation"/>).
/// </summary>
public enum RfeFacing
{
    /// <summary>0: drawn with its own transform.</summary>
    None = 0,
    /// <summary>1: a full billboard: local +Z at the camera, local Y as near the keyed rotation's Y as it can be.</summary>
    Billboard = 1,
    /// <summary>2: turns about the keyed rotation's Y axis until local +Z is as near the camera as it can be (a rod).</summary>
    Axial = 2,
}

/// <summary>How a facing mesh object's body lies against the local XY plane the game turns to the camera (<see cref="RfeSampler.Lie"/>).</summary>
public enum RfeFacingLie
{
    /// <summary>It spans local X and Y: it shows (and every non-facing object counts as this).</summary>
    Upright,
    /// <summary>It is flat in local X or Y, so the game turns it edge-on to the camera and it never shows.</summary>
    EdgeOn,
    /// <summary>Facing 2 and flat in local Y, across its axis: the game only spins it in its own plane, which stays as keyed.</summary>
    AcrossAxis,
}

/// <summary>
/// A mesh object: transform keys spread evenly over the effect's duration (no times), its material and one static
/// Red Faction II mesh body.
/// </summary>
/// <param name="Facing">The facing value as stored (0, 1 or 2 in the stock files).</param>
/// <param name="Material">Index into <see cref="RfeFile.Materials"/>.</param>
/// <param name="Positions">Position keys (lerped).</param>
/// <param name="Rotations">Rotation keys, (x, y, z, w) as stored (slerped).</param>
/// <param name="Scales">Scale keys (lerped).</param>
/// <param name="Body">The mesh.</param>
public sealed record RfeMeshObject(uint Facing, int Material, ImmutableArray<Vector3> Positions, ImmutableArray<Quaternion> Rotations,
    ImmutableArray<Vector3> Scales, Rf2StaticBody Body)
{
    /// <summary>The facing mode (values above 2 are drawn as <see cref="RfeFacing.None"/>).</summary>
    public RfeFacing FacingMode => Facing is 1 or 2 ? (RfeFacing)Facing : RfeFacing.None;
}

/// <summary>
/// A particle emitter (the 0x74-byte record of the loader, then four tracks). Field meanings are from the game's code
/// unless said otherwise (see the format notes).
/// </summary>
public sealed record RfeEmitter
{
    /// <summary>1: rectangle (local X by Z); 0: sphere or cone; anything else spawns nothing.</summary>
    public uint Shape { get; init; }
    /// <summary>Bit 1: gravity; bit 4: <see cref="Acceleration"/>; bit 2: unknown.</summary>
    public uint Flags { get; init; }
    /// <summary>Index into <see cref="RfeFile.Materials"/>.</summary>
    public int Material { get; init; }
    /// <summary>The particle slots.</summary>
    public int MaxParticles { get; init; }
    /// <summary>The particle quad is twice this.</summary>
    public float Radius { get; init; }
    /// <summary>Above 0: drawn along the velocity (a streak); the length factor is not known.</summary>
    public float Streak { get; init; }
    /// <summary>Launch speed times 1 + U(-v, v).</summary>
    public float SpeedVariance { get; init; }
    /// <summary>The cosine of the launch cone's half angle around the launch axis; 1 or more: no cone.</summary>
    public float Spread { get; init; }
    /// <summary>Seconds.</summary>
    public float Lifetime { get; init; }
    /// <summary>Life = lifetime times 1 + U(-v, v).</summary>
    public float LifetimeVariance { get; init; }
    /// <summary>Fade in, as a fraction of life.</summary>
    public float FadeIn { get; init; }
    /// <summary>Fade out starts at this fraction of life.</summary>
    public float FadeOut { get; init; }
    /// <summary>Size grows until this fraction of life.</summary>
    public float Grow { get; init; }
    /// <summary>Size shrinks from this fraction of life.</summary>
    public float Shrink { get; init; }
    /// <summary>Rectangle width (local X); a sphere's diameter.</summary>
    public float Width { get; init; }
    /// <summary>Rectangle height (local Z).</summary>
    public float Height { get; init; }
    /// <summary>Shape 0: the cosine of the cone the spawn direction is drawn from (around the launch axis); -1 or less: the whole sphere.</summary>
    public float SpawnCone { get; init; }
    /// <summary>The float at 0x44 (0 in every stock emitter).</summary>
    public float Unknown44 { get; init; }
    /// <summary>A constant acceleration in effect space, used with flag bit 4.</summary>
    public Vector3 Acceleration { get; init; }
    /// <summary>Emitter position keys, spread evenly over the duration.</summary>
    public ImmutableArray<Vector3> Positions { get; init; } = [];
    /// <summary>Emitter rotation keys, spread evenly over the duration.</summary>
    public ImmutableArray<Quaternion> Rotations { get; init; } = [];
    /// <summary>Particles per second at seconds of effect time.</summary>
    public ImmutableArray<RfeKey> BirthRate { get; init; } = [];
    /// <summary>Launch speed (m/s) at seconds of effect time.</summary>
    public ImmutableArray<RfeKey> Speed { get; init; } = [];

    /// <summary>Flag bit 1.</summary>
    public bool HasGravity => (Flags & 1) != 0;
    /// <summary>Flag bit 4.</summary>
    public bool HasAcceleration => (Flags & 4) != 0;
    /// <summary>True for the shapes that spawn particles (0 and 1).</summary>
    public bool Spawns => Shape <= 1;
}

/// <summary>
/// A Red Faction II effect (<c>.rfe</c>, "RFEF" version 3), as the PC game's loader reads it: texture names, materials,
/// mesh objects and particle emitters. Read only: Cairn never writes this format.
/// </summary>
/// <param name="Duration">Seconds (whole 1/30 s plus about 0.0002 in the stock files).</param>
/// <param name="Radius">The bounding radius of the mesh part (0 without meshes).</param>
/// <param name="Textures">Texture names (plain file names, .tga or .vbm; an empty name is possible).</param>
/// <param name="Materials">Materials.</param>
/// <param name="Meshes">Mesh objects.</param>
/// <param name="Emitters">Particle emitters.</param>
public sealed record RfeFile(float Duration, float Radius, ImmutableArray<string> Textures, ImmutableArray<RfeMaterial> Materials,
    ImmutableArray<RfeMeshObject> Meshes, ImmutableArray<RfeEmitter> Emitters)
{
    /// <summary>The file's length in bytes (the reader checks it ends there).</summary>
    public int ByteLength { get; init; }

    /// <summary>The texture name of material <paramref name="material"/>, or empty.</summary>
    public string TextureOf(int material) =>
        (uint)material < (uint)Materials.Length && (uint)Materials[material].Texture < (uint)Textures.Length ? Textures[Materials[material].Texture] : "";

    /// <summary>The effect's total particle budget (the sum of the emitters' slots).</summary>
    public int ParticleBudget => Emitters.Sum(e => Math.Max(0, e.MaxParticles));
}
