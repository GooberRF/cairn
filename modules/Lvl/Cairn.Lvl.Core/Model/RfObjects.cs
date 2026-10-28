using Cairn.Formats.Rfl;

namespace Cairn.Lvl.Model;

/// <summary>Anything placed in a level that has a uid and a position.</summary>
public abstract record RfObject
{
    public int Uid { get; init; } = -1;
    public Vector3 Position { get; init; }
    public bool HiddenInEditor { get; init; }
}

/// <summary>Objects stored with the common head: uid, class name, position, orientation, script name, hidden.</summary>
public abstract record RfPlacedObject : RfObject
{
    /// <summary>The class (entity.tbl / items.tbl / clutter.tbl name, or the object type for others).</summary>
    public string ClassName { get; init; } = "";
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    public string ScriptName { get; init; } = "";
}

/// <summary>A geomod region (0x200). The packed value carries shape, hidden, ice, shallow and hardness.</summary>
public sealed record RfGeoRegion : RfObject
{
    /// <summary>Bits 0-2 shape (2 sphere, 4 box), 0x08 hidden, 0x10 old ice texture, 0x20 shallow, 0x40 ice, bits 16-23 hardness.</summary>
    public uint Packed { get; init; }
    public int Shape => (int)(Packed & 7);
    public bool IsSphere => Shape == 2;
    public bool IsBox => Shape == 4;
    public bool IsShallow => (Packed & 0x20) != 0;
    public bool IsIce => (Packed & 0x40) != 0;
    public int Hardness => (int)((Packed >> 16) & 0xFF);
    public float? ShallowDepth { get; init; }
    /// <summary>Boxes, and shallow spheres.</summary>
    public RfMatrix? Orientation { get; init; }
    public float Radius { get; init; }
    /// <summary>Box width, height, depth.</summary>
    public Vector3 Size { get; init; }
    /// <summary>Files before 0xA3: the old texture name (when packed bit 0x10), float, int and colour.</summary>
    public string? LegacyTexture { get; init; }
    public float? LegacyFloat { get; init; }
    public int? LegacyInt { get; init; }
    public RflColor? LegacyColor { get; init; }
}

/// <summary>A light (0x300, and the editor-only lights of 0x4000000).</summary>
public sealed record RfLight : RfPlacedObject
{
    public RfLightFlags Flags { get; init; }
    public RflColor Color { get; init; } = RfColors.White;
    public float Range { get; init; }
    public float Fov { get; init; }
    public float FovDropoff { get; init; }
    public float IntensityAtMaxRange { get; init; }
    /// <summary>0 linear, 1 squared, 2 cosine, 3 square root.</summary>
    public int DropoffType { get; init; }
    public float TubeWidth { get; init; }
    public float OnIntensity { get; init; } = 1f;
    public float OnTime { get; init; }
    public float OnTimeVariation { get; init; }
    public float OffIntensity { get; init; }
    public float OffTime { get; init; }
    public float OffTimeVariation { get; init; }
    /// <summary>Files before 0x62: the two ints stored instead of flags and colour, and the old tail (float, two bytes).</summary>
    public (int A, int B)? LegacyInts { get; init; }
    public float? LegacyTailFloat { get; init; }
    public (byte A, byte B)? LegacyTailBytes { get; init; }
    /// <summary>Files before 0x78: the float after the range.</summary>
    public float? LegacyRangeFloat { get; init; }

    public RfLightType Type => (RfLightType)(((uint)Flags >> 4) & 3);
}

/// <summary>A cutscene camera (0x400).</summary>
public sealed record RfCutsceneCamera : RfPlacedObject;

/// <summary>A cutscene path node (0x5000).</summary>
public sealed record RfCutscenePathNode : RfPlacedObject;

/// <summary>A target (0xF00).</summary>
public sealed record RfTarget : RfPlacedObject;

/// <summary>An ambient sound (0x500).</summary>
public sealed record RfAmbientSound : RfObject
{
    public string SoundFile { get; init; } = "";
    public float MinDistance { get; init; }
    public float Volume { get; init; } = 1f;
    public float Rolloff { get; init; } = 0.1f;
    public int StartDelayMs { get; init; }
}

/// <summary>An event (0x600).</summary>
public sealed record RfEvent : RfObject
{
    public string ClassName { get; init; } = "";
    public string ScriptName { get; init; } = "";
    public float Delay { get; init; }
    public bool Bool1 { get; init; }
    public bool Bool2 { get; init; }
    public int Int1 { get; init; }
    public int Int2 { get; init; }
    public float Float1 { get; init; }
    public float Float2 { get; init; }
    public string Str1 { get; init; } = "";
    public string Str2 { get; init; } = "";
    public ImmutableArray<int> Links { get; init; } = [];
    /// <summary>Stored only for the classes that have one (teleports, Play_Vclip, Alarm and the Alpine classes).</summary>
    public RfMatrix? Orientation { get; init; }
    /// <summary>Editor colour (from 0xB0).</summary>
    public RflColor Color { get; init; } = RfColors.DefaultEventColor;
    /// <summary>RF2 only: the fields RF1 events do not have.</summary>
    public Rf2EventData? Rf2Data { get; init; }
}

/// <summary>A multiplayer respawn point (0x700).</summary>
public sealed record RfRespawnPoint : RfObject
{
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    public string ScriptName { get; init; } = "";
    public int Team { get; init; }
    public bool RedTeam { get; init; } = true;
    public bool BlueTeam { get; init; } = true;
    public bool Bot { get; init; } = true;
}

/// <summary>A particle emitter (0xA00).</summary>
public sealed record RfParticleEmitter : RfPlacedObject
{
    /// <summary>1 plane, 2 sphere.</summary>
    public int Shape { get; init; }
    public float SphereRadius { get; init; }
    public float PlaneWidth { get; init; }
    public float PlaneDepth { get; init; }
    public string Texture { get; init; } = "";
    public float SpawnDelay { get; init; }
    public float SpawnDelayRandom { get; init; }
    public float Velocity { get; init; }
    public float VelocityRandom { get; init; }
    public float Acceleration { get; init; }
    public float Decay { get; init; }
    public float DecayRandom { get; init; }
    public float Radius { get; init; }
    public float RadiusRandom { get; init; }
    public float GrowthRate { get; init; }
    public float GravityScale { get; init; }
    public float RandomDirection { get; init; }
    public RflColor Color { get; init; } = RfColors.White;
    public RflColor FadeColor { get; init; } = RfColors.White;
    /// <summary>0x04 force spawn every frame, 0x08 direction-dependent velocity, 0x10 initially on.</summary>
    public uint EmitterFlags { get; init; }
    /// <summary>Low 16 bits flags; high 16 bits stickiness, bounciness, push and swirliness (4 bits each).</summary>
    public uint ParticleFlags { get; init; }
    public bool InitiallyOn { get; init; }
    public float TimeOn { get; init; }
    public float TimeOnRandom { get; init; }
    public float TimeOff { get; init; }
    public float TimeOffRandom { get; init; }
    public float ActiveDistance { get; init; }
}

/// <summary>A gas region (0xB00).</summary>
public sealed record RfGasRegion : RfPlacedObject
{
    /// <summary>1 sphere, 2 box.</summary>
    public int Shape { get; init; }
    public float Radius { get; init; }
    /// <summary>Box height, width, depth (in that stored order).</summary>
    public Vector3 Size { get; init; }
    public RflColor Color { get; init; }
    public float Density { get; init; }
}

/// <summary>A room effect (0xC00). Its object head is stored last.</summary>
public sealed record RfRoomEffect : RfPlacedObject
{
    /// <summary>1 sky room, 2 liquid room, 3 ambient light, 4 none.</summary>
    public int EffectType { get; init; }
    public RfLiquidProperties? Liquid { get; init; }
    public RflColor? AmbientColor { get; init; }
    public bool IsCold { get; init; }
    public bool IsOutside { get; init; }
    public bool IsAirlock { get; init; }
    /// <summary>Files 0x6D..0x80: the int stored after the liquid type.</summary>
    public int? LegacyInt { get; init; }
}

/// <summary>A climbing region (0xD00).</summary>
public sealed record RfClimbingRegion : RfPlacedObject
{
    /// <summary>1 ladder, 2 chain fence.</summary>
    public int RegionType { get; init; } = 1;
    public Vector3 Extents { get; init; }
}

/// <summary>A bolt emitter (0xE00).</summary>
public sealed record RfBoltEmitter : RfPlacedObject
{
    public int TargetUid { get; init; } = -1;
    public float SourceControlDistance { get; init; }
    public float TargetControlDistance { get; init; }
    public float Thickness { get; init; }
    public float Jitter { get; init; }
    public int Segments { get; init; }
    public float SpawnDelay { get; init; }
    public float SpawnDelayRandom { get; init; }
    public float Decay { get; init; }
    public float DecayRandom { get; init; }
    public RflColor Color { get; init; } = RfColors.White;
    public string Texture { get; init; } = "";
    /// <summary>0x02 fade, 0x04 glow, 0x08 source direction lock, 0x10 target direction lock.</summary>
    public uint Flags { get; init; }
    public bool InitiallyOn { get; init; }
}

/// <summary>A decal (0x1000).</summary>
public sealed record RfDecal : RfPlacedObject
{
    public Vector3 Extents { get; init; }
    public string Texture { get; init; } = "";
    public int Alpha { get; init; } = 255;
    public bool SelfIlluminated { get; init; }
    /// <summary>As stored: only the low byte is the tiling (0 none, 1 U, 2 V); RED writes stray bytes above it.</summary>
    public int TilingRaw { get; init; }
    public float Scale { get; init; } = 1f;
    /// <summary>RF2 only: the fields after the RF1 ones.</summary>
    public Rf2DecalData? Rf2Data { get; init; }
    public int Tiling => TilingRaw & 0xFF;
}

/// <summary>A push region (0x1100).</summary>
public sealed record RfPushRegion : RfPlacedObject
{
    /// <summary>1 sphere, 2 axis-aligned box, 3 oriented box.</summary>
    public int Shape { get; init; }
    public float Radius { get; init; }
    public Vector3 Extents { get; init; }
    public float Strength { get; init; }
    /// <summary>Low 16 bits flags (0x01 mass independent ... 0x40 jump pad), high 16 bits turbulence.</summary>
    public uint Packed { get; init; }
    public int Flags => (int)(Packed & 0xFFFF);
    public int Turbulence => (int)(Packed >> 16);
}

/// <summary>One shot of a cutscene.</summary>
public sealed record RfCutsceneShot(int CameraUid, float PreWait, float PathTime, float PostWait, int LookAtUid, int TriggerUid, string PathName);

/// <summary>A cutscene (0x4000).</summary>
public sealed record RfCutscene
{
    public int Uid { get; init; } = -1;
    public bool HidePlayer { get; init; }
    public float Fov { get; init; }
    public ImmutableArray<RfCutsceneShot> Shots { get; init; } = [];
}

/// <summary>A named list of uids (cutscene paths, 0x6000).</summary>
public sealed record RfNamedUidList(string Name, ImmutableArray<int> Uids);

/// <summary>An EAX sound effect region (0x8000).</summary>
public sealed record RfEaxEffect : RfPlacedObject
{
    public string EffectName { get; init; } = "";
}

/// <summary>A waypoint of the old (before 0x8C) waypoint lists.</summary>
public sealed record RfLegacyWaypoint : RfObject
{
    public float Value { get; init; }
}

/// <summary>A waypoint list (0x10000): nav point indices, or legacy waypoints in old files.</summary>
public sealed record RfWaypointList
{
    public string Name { get; init; } = "";
    public ImmutableArray<int> NavPoints { get; init; } = [];
    public ImmutableArray<RfLegacyWaypoint> LegacyWaypoints { get; init; } = [];
}

/// <summary>A nav point (0x20000).</summary>
public sealed record RfNavPoint : RfObject
{
    /// <summary>As stored (files before 0x8D store half the height the game uses).</summary>
    public float Height { get; init; }
    public float Radius { get; init; }
    /// <summary>0 walk, 1 fly.</summary>
    public int NavType { get; init; }
    public bool Directional { get; init; }
    public RfMatrix? Orientation { get; init; }
    public bool Cover { get; init; }
    public bool Hide { get; init; }
    public bool Crunch { get; init; }
    public float PauseTime { get; init; }
    public ImmutableArray<int> Links { get; init; } = [];
    /// <summary>Indices of connected nav points (levels only; group files do not store them).</summary>
    public ImmutableArray<int> Connections { get; init; } = [];
    /// <summary>Files 0x52..0x8A: the int after the type.</summary>
    public int? LegacyInt { get; init; }
}

/// <summary>A (class name, value) pair of the entity / clutter class lists (empty in every known file).</summary>
public sealed record RfClassListEntry(string Name, int Value);

/// <summary>An entity (0x30000).</summary>
public sealed record RfEntity : RfPlacedObject
{
    /// <summary>0 uncooperative, 1 species, 2 cooperative.</summary>
    public int Cooperation { get; init; }
    /// <summary>0 unfriendly, 1 neutral, 2 friendly, 3 outcast.</summary>
    public int Friendliness { get; init; }
    public int Team { get; init; }
    public string WaypointList { get; init; } = "";
    public string WaypointMethod { get; init; } = "";
    public byte Unknown1 { get; init; }
    public bool Boarded { get; init; }
    public bool ReadyToFire { get; init; }
    public bool OnlyAttackPlayer { get; init; }
    public bool WeaponHolstered { get; init; }
    public bool Deaf { get; init; }
    public int SweepMin { get; init; }
    public int SweepMax { get; init; }
    public bool IgnoreTerrainWhenFiring { get; init; }
    public byte Unknown2 { get; init; }
    public bool StartCrouched { get; init; }
    public float Life { get; init; } = -1f;
    public float Armor { get; init; } = -1f;
    public int Fov { get; init; } = 180;
    public string PrimaryWeapon { get; init; } = "";
    public string SecondaryWeapon { get; init; } = "";
    public string ItemDrop { get; init; } = "";
    public string StateAnim { get; init; } = "";
    public string CorpsePose { get; init; } = "";
    public string Skin { get; init; } = "";
    public string DeathAnim { get; init; } = "";
    /// <summary>0 catatonic, 1 waiting, 2 waypoints, 3 collecting, 4 motion detection.</summary>
    public byte AiMode { get; init; }
    /// <summary>0 default, 1 evasive, 2 direct, 3 stand ground.</summary>
    public byte AttackStyle { get; init; }
    public ImmutableArray<RfClassListEntry> ClassList { get; init; } = [];
    public int TurretUid { get; init; } = -1;
    public int AlertCameraUid { get; init; } = -1;
    public int AlarmEventUid { get; init; } = -1;
    public bool Run { get; init; }
    public bool StartHidden { get; init; }
    public bool WearHelmet { get; init; }
    public bool EndGameIfKilled { get; init; }
    public bool CowerFromWeapon { get; init; }
    public bool QuestionUnarmedPlayer { get; init; }
    public bool DontHum { get; init; }
    public bool NoShadow { get; init; }
    public bool AlwaysSimulate { get; init; }
    public bool PerfectAim { get; init; }
    public bool PermanentCorpse { get; init; }
    public bool NeverFly { get; init; }
    public bool NeverLeave { get; init; }
    public bool NoPersonaMessages { get; init; }
    public bool FadeCorpseImmediately { get; init; }
    public bool NeverCollideWithPlayer { get; init; }
    /// <summary>As stored; the range follows only when this is exactly 1.</summary>
    public byte UseCustomAttackRange { get; init; }
    public float CustomAttackRange { get; init; } = 15f;
    public string LeftHandHolding { get; init; } = "none";
    public string RightHandHolding { get; init; } = "none";
    /// <summary>Fields of pre-release files that later versions dropped, as stored (null when absent).</summary>
    public int? LegacyInt { get; init; }
    public byte? LegacyByte { get; init; }
    public string? LegacyString { get; init; }
}

/// <summary>An item (0x40000).</summary>
public sealed record RfItem : RfPlacedObject
{
    public int Count { get; init; }
    public int RespawnTime { get; init; }
    public int Team { get; init; }
    /// <summary>RF2 only: the six bytes after the team.</summary>
    public ImmutableArray<byte> Rf2Extra { get; init; } = [];
}

/// <summary>A clutter object (0x50000).</summary>
public sealed record RfClutter : RfPlacedObject
{
    public ImmutableArray<RfClassListEntry> ClassList { get; init; } = [];
    public string Skin { get; init; } = "";
    public ImmutableArray<int> Links { get; init; } = [];
    /// <summary>Files 0x67..0x7B: the old skin index.</summary>
    public int? LegacySkinIndex { get; init; }
}

/// <summary>A trigger (0x60000).</summary>
public sealed record RfTrigger : RfObject
{
    public string ScriptName { get; init; } = "";
    /// <summary>0 sphere, 1 box.</summary>
    public int Shape { get; init; }
    public float ResetsAfter { get; init; }
    /// <summary>-1 = infinite.</summary>
    public int ResetsTimes { get; init; } = -1;
    public bool UseKey { get; init; }
    public string KeyName { get; init; } = "";
    public bool WeaponActivates { get; init; }
    /// <summary>0 players, 1 all objects, 2 linked objects, 3 AI, 4 player vehicle, 5 geomods.</summary>
    public byte ActivatedBy { get; init; }
    public bool IsNpc { get; init; }
    public bool IsAuto { get; init; }
    public bool InVehicle { get; init; }
    public float Radius { get; init; }
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    /// <summary>Box height, width, depth (in that stored order).</summary>
    public Vector3 Size { get; init; }
    public bool OneWay { get; init; }
    public int AirlockRoomUid { get; init; } = -1;
    public int AttachedToUid { get; init; } = -1;
    public int UseClutterUid { get; init; } = -1;
    public bool Disabled { get; init; }
    public float ButtonActiveTime { get; init; }
    public float InsideTime { get; init; }
    public int Team { get; init; } = -1;
    public ImmutableArray<int> Links { get; init; } = [];
    /// <summary>RF2 only: the fields RF1 triggers do not have.</summary>
    public Rf2TriggerData? Rf2Data { get; init; }

    /// <summary>Script names starting with byte 0xAB carry Pure Faction flags, not text.</summary>
    public bool HasPureFactionFlags => ScriptName.Length >= 2 && ScriptName[0] == '«';
}

/// <summary>The single-player start (0x70000).</summary>
public sealed record RfPlayerStart(Vector3 Position, RfMatrix Orientation);
