namespace Cairn.Lvl.Model;

/// <summary>A keyframe of a moving group (no class name, unlike other objects).</summary>
public sealed record RfKeyframe : RfObject
{
    public RfMatrix Orientation { get; init; } = RfMatrix.Identity;
    public string ScriptName { get; init; } = "";
    public float PauseTime { get; init; }
    public float DepartTravelTime { get; init; }
    public float ReturnTravelTime { get; init; }
    public float AccelTime { get; init; }
    public float DecelTime { get; init; }
    public int EventUid { get; init; } = -1;
    public int ItemUid1 { get; init; } = -1;
    public int ItemUid2 { get; init; } = -1;
    public float DegreesAboutAxis { get; init; }
    /// <summary>Files 0x69..0x8D: two vectors (rotation angles) stored before the degrees.</summary>
    public (Vector3 A, Vector3 B)? LegacyAngles { get; init; }
}

/// <summary>A member's pose relative to the first keyframe.</summary>
public readonly record struct RfMemberTransform(int Uid, Vector3 Position, RfMatrix Orientation);

/// <summary>The moving part of a moving group (levels and group files share it).</summary>
public sealed record RfMovingData
{
    public ImmutableArray<RfKeyframe> Keyframes { get; init; } = [];
    public ImmutableArray<RfMemberTransform> MemberTransforms { get; init; } = [];
    public bool IsDoor { get; init; }
    /// <summary>Files before 0x68: the old byte after "is door".</summary>
    public byte? LegacyByte { get; init; }
    public bool RotateInPlace { get; init; }
    public bool StartsBackwards { get; init; }
    public bool UseTravelTimeAsSpeed { get; init; }
    public bool ForceOrient { get; init; }
    public bool NoPlayerCollide { get; init; }
    /// <summary>1 one way, 2 ping-pong once, 3 ping-pong infinite, 4 loop once, 5 loop infinite, 6 lift.</summary>
    public int MovementType { get; init; } = 1;
    public int StartingKeyframe { get; init; }
    public RfSound StartSound { get; init; } = RfSound.None;
    public RfSound LoopingSound { get; init; } = RfSound.None;
    public RfSound StopSound { get; init; } = RfSound.None;
    public RfSound CloseSound { get; init; } = RfSound.None;
}

/// <summary>An editor group of a level (0x3000000), or a moving group as the game reads it (0x3000).</summary>
public sealed record RfLevelGroup
{
    public string Name { get; init; } = "";
    /// <summary>The leading byte (0 in practice).</summary>
    public byte Unknown { get; init; }
    /// <summary>Null for a group that does not move.</summary>
    public RfMovingData? Moving { get; init; }
    /// <summary>Non-brush members (keyframes live in <see cref="RfMovingData"/>).</summary>
    public ImmutableArray<int> ObjectUids { get; init; } = [];
    /// <summary>Brush uids (files before 0x57: brush indices).</summary>
    public ImmutableArray<int> BrushUids { get; init; } = [];
    /// <summary>RF2 only: the fields RF1 groups do not have.</summary>
    public Rf2GroupData? Rf2Data { get; init; }

    public bool IsMoving => Moving is not null;
}

/// <summary>
/// Every object list a level or a group stores. A level fills all lists but <see cref="LegacyWaypointLists"/>;
/// a group file has no cutscenes or editor-only lights (those live on <see cref="RfLevel"/>).
/// </summary>
public sealed record RfObjectSet
{
    public static RfObjectSet Empty { get; } = new();

    public ImmutableArray<RfGeoRegion> GeoRegions { get; init; } = [];
    public ImmutableArray<RfLight> Lights { get; init; } = [];
    public ImmutableArray<RfCutsceneCamera> CutsceneCameras { get; init; } = [];
    public ImmutableArray<RfCutscenePathNode> CutscenePathNodes { get; init; } = [];
    public ImmutableArray<RfAmbientSound> AmbientSounds { get; init; } = [];
    public ImmutableArray<RfEvent> Events { get; init; } = [];
    public ImmutableArray<RfRespawnPoint> RespawnPoints { get; init; } = [];
    /// <summary>Group files before 0x8C only (later files and levels use <see cref="RfLevel.WaypointLists"/>).</summary>
    public ImmutableArray<RfWaypointList> LegacyWaypointLists { get; init; } = [];
    public ImmutableArray<RfNavPoint> NavPoints { get; init; } = [];
    public ImmutableArray<RfEntity> Entities { get; init; } = [];
    public ImmutableArray<RfItem> Items { get; init; } = [];
    public ImmutableArray<RfClutter> Clutter { get; init; } = [];
    public ImmutableArray<RfTrigger> Triggers { get; init; } = [];
    public ImmutableArray<RfParticleEmitter> ParticleEmitters { get; init; } = [];
    public ImmutableArray<RfGasRegion> GasRegions { get; init; } = [];
    public ImmutableArray<RfDecal> Decals { get; init; } = [];
    public ImmutableArray<RfClimbingRegion> ClimbingRegions { get; init; } = [];
    public ImmutableArray<RfRoomEffect> RoomEffects { get; init; } = [];
    public ImmutableArray<RfEaxEffect> EaxEffects { get; init; } = [];
    public ImmutableArray<RfBoltEmitter> BoltEmitters { get; init; } = [];
    public ImmutableArray<RfTarget> Targets { get; init; } = [];
    public ImmutableArray<RfPushRegion> PushRegions { get; init; } = [];
    public ImmutableArray<RfAlpineMesh> AlpineMeshes { get; init; } = [];
    public ImmutableArray<RfCorona> Coronas { get; init; } = [];

    /// <summary>Every object of every list, for markers, lookups by uid and counts.</summary>
    public IEnumerable<RfObject> All()
    {
        foreach (var o in GeoRegions) yield return o;
        foreach (var o in Lights) yield return o;
        foreach (var o in CutsceneCameras) yield return o;
        foreach (var o in CutscenePathNodes) yield return o;
        foreach (var o in AmbientSounds) yield return o;
        foreach (var o in Events) yield return o;
        foreach (var o in RespawnPoints) yield return o;
        foreach (var l in LegacyWaypointLists)
            foreach (var o in l.LegacyWaypoints) yield return o;
        foreach (var o in NavPoints) yield return o;
        foreach (var o in Entities) yield return o;
        foreach (var o in Items) yield return o;
        foreach (var o in Clutter) yield return o;
        foreach (var o in Triggers) yield return o;
        foreach (var o in ParticleEmitters) yield return o;
        foreach (var o in GasRegions) yield return o;
        foreach (var o in Decals) yield return o;
        foreach (var o in ClimbingRegions) yield return o;
        foreach (var o in RoomEffects) yield return o;
        foreach (var o in EaxEffects) yield return o;
        foreach (var o in BoltEmitters) yield return o;
        foreach (var o in Targets) yield return o;
        foreach (var o in PushRegions) yield return o;
        foreach (var o in AlpineMeshes) yield return o;
        foreach (var o in Coronas) yield return o;
    }

    /// <summary>Total number of objects in every list.</summary>
    public int Count =>
        GeoRegions.Length + Lights.Length + CutsceneCameras.Length + CutscenePathNodes.Length + AmbientSounds.Length
        + Events.Length + RespawnPoints.Length + LegacyWaypointLists.Sum(l => l.LegacyWaypoints.Length)
        + NavPoints.Length + Entities.Length + Items.Length + Clutter.Length + Triggers.Length
        + ParticleEmitters.Length + GasRegions.Length + Decals.Length + ClimbingRegions.Length + RoomEffects.Length
        + EaxEffects.Length + BoltEmitters.Length + Targets.Length + PushRegions.Length + AlpineMeshes.Length
        + Coronas.Length;
}
