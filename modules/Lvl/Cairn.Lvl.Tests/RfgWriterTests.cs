using System.Buffers.Binary;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;
using Cairn.Lvl.Reading;
using Cairn.Lvl.Writing;

namespace Cairn.Lvl.Tests;

/// <summary>Synthetic group files through the writer: every record, every gate the tests can reach, every rule the writer applies.</summary>
public class RfgWriterTests
{
    private static readonly RfMatrix Turned = new(new Vector3(0, 0, -1), Vector3.UnitY, new Vector3(1, 0, 0));

    private static readonly RflColor Teal = new(10, 200, 180, 255);

    /// <summary>A brush with a lit face (surface 0, lightmap UVs), a scrolling face and planes, as a level brush can have.</summary>
    internal static RfBrush Brush(int uid, Vector3 position, RfSolidFlags flags = RfSolidFlags.None, RfAlpineBrushData? alpine = null)
    {
        var box = LvlConvertTestData.Box(new Vector3(1, 2, 3), ["rock.tga", "metal.tga"], (k, f) => k switch
        {
            0 => f with { Surface = 0, Corners = [.. f.Corners.Select((c, j) => c with { LightmapUv = new Vector2(j * 0.1f, 0.5f) })] },
            1 => f with { Flags = RfFaceFlags.ScrollTexture, RawFlags = 0x10, Scroll = new Vector2(0.25f, -1f) },
            2 => f with { Flags = RfFaceFlags.FullBright | RfFaceFlags.Unknown0800, RawFlags = 0x820, Smoothing = 3 },
            _ => f,
        });
        var surface = new RfSurface
        {
            Lightmap = 2, X = 1, Y = 2, Width = 16, Height = 8, PixelsPerMeter = new Vector2(4, 4),
            BoundsMin = -Vector3.One, BoundsMax = Vector3.One, Plane = new RfPlane(Vector3.UnitX, -1), ShouldSmooth = 1,
            Unknown = 5, DroppedAxis = 0, UAxis = 1, VAxis = 2, UvAdd = new Vector2(0.5f, 0.25f), UvScale = new Vector2(2, 3), Room = -1,
        };
        return new RfBrush
        {
            Uid = uid,
            Position = position,
            Orientation = Turned,
            Geometry = box with { Modifiability = 4, Surfaces = [surface] },
            SolidFlags = flags,
            Life = 50,
            State = 2,
            Alpine = alpine,
        };
    }

    /// <summary>One object of every type, each field away from its default.</summary>
    internal static RfObjectSet EveryObject(int uidBase) => new()
    {
        GeoRegions =
        [
            new RfGeoRegion { Uid = uidBase + 1, Position = new Vector3(1, 2, 3), Packed = 0x00320022 | 0x08, HiddenInEditor = true, ShallowDepth = 0.5f, Orientation = Turned, Radius = 4 },
            new RfGeoRegion { Uid = uidBase + 2, Position = new Vector3(-1, 0, 2), Packed = 0x00640044, Orientation = Turned, Size = new Vector3(1, 2, 3) },
        ],
        Lights = [new RfLight { Uid = uidBase + 3, ClassName = "Light", Position = Vector3.UnitY, Orientation = Turned, ScriptName = "lamp", Flags = (RfLightFlags)0x2128, Color = Teal, Range = 12, Fov = 45, FovDropoff = 10, IntensityAtMaxRange = 0.1f, DropoffType = 2, TubeWidth = 0.5f, OnIntensity = 2, OnTime = 1, OnTimeVariation = 0.5f, OffIntensity = 0.25f, OffTime = 2, OffTimeVariation = 0.75f }],
        CutsceneCameras = [new RfCutsceneCamera { Uid = uidBase + 4, ClassName = "Cutscene Camera", Position = Vector3.One, Orientation = Turned, ScriptName = "cam" }],
        CutscenePathNodes = [new RfCutscenePathNode { Uid = uidBase + 5, ClassName = "Cutscene Path Node", Position = Vector3.UnitZ, HiddenInEditor = true }],
        AmbientSounds = [new RfAmbientSound { Uid = uidBase + 6, Position = Vector3.UnitX, HiddenInEditor = true, SoundFile = "hum.wav", MinDistance = 3, Volume = 0.5f, Rolloff = 0.75f, StartDelayMs = 250 }],
        Events =
        [
            new RfEvent { Uid = uidBase + 7, ClassName = "Teleport_Player", Position = Vector3.One, ScriptName = "tp", Delay = 1.5f, Bool1 = true, Bool2 = true, Int1 = 3, Int2 = -4, Float1 = 0.5f, Float2 = 2, Str1 = "a", Str2 = "b", Links = [uidBase + 3], Orientation = Turned, Color = Teal },
            new RfEvent { Uid = uidBase + 8, ClassName = "Anchor_Marker_Orient", Orientation = Turned },
            new RfEvent { Uid = uidBase + 9, ClassName = "Play_Explosion", Float1 = 3 },
        ],
        RespawnPoints = [new RfRespawnPoint { Uid = uidBase + 10, Position = Vector3.UnitY, Orientation = Turned, ScriptName = "spawn", Team = 1, RedTeam = false, BlueTeam = true, Bot = false }],
        NavPoints = [new RfNavPoint { Uid = uidBase + 11, HiddenInEditor = true, Height = 1.5f, Position = Vector3.One, Radius = 0.5f, NavType = 1, Directional = true, Orientation = Turned, Cover = true, Hide = true, Crunch = true, PauseTime = 2, Links = [uidBase + 12] }],
        Entities =
        [
            new RfEntity
            {
                Uid = uidBase + 12, ClassName = "guard", Position = Vector3.UnitX, Orientation = Turned, ScriptName = "g1",
                Cooperation = 2, Friendliness = 3, Team = 1, WaypointList = "path", WaypointMethod = "loop", Unknown1 = 1, Boarded = true,
                ReadyToFire = true, OnlyAttackPlayer = true, WeaponHolstered = true, Deaf = true, SweepMin = -30, SweepMax = 30,
                IgnoreTerrainWhenFiring = true, Unknown2 = 2, StartCrouched = true, Life = 100, Armor = 50, Fov = 90,
                PrimaryWeapon = "12mm handgun", SecondaryWeapon = "riot stick", ItemDrop = "medkit", StateAnim = "stand", CorpsePose = "dead",
                Skin = "blue", DeathAnim = "die", AiMode = 2, AttackStyle = 1, ClassList = [new RfClassListEntry("x", 1)], TurretUid = 5,
                AlertCameraUid = 6, AlarmEventUid = 7, Run = true, StartHidden = true, WearHelmet = true, EndGameIfKilled = true,
                CowerFromWeapon = true, QuestionUnarmedPlayer = true, DontHum = true, NoShadow = true, AlwaysSimulate = true, PerfectAim = true,
                PermanentCorpse = true, NeverFly = true, NeverLeave = true, NoPersonaMessages = true, FadeCorpseImmediately = true,
                NeverCollideWithPlayer = true, UseCustomAttackRange = 1, CustomAttackRange = 22, LeftHandHolding = "flashlight", RightHandHolding = "none",
            },
            new RfEntity { Uid = uidBase + 13, ClassName = "miner", UseCustomAttackRange = 2 },
        ],
        Items = [new RfItem { Uid = uidBase + 14, ClassName = "Medical Kit", Position = Vector3.UnitZ, Count = 2, RespawnTime = 30, Team = 1 }],
        Clutter = [new RfClutter { Uid = uidBase + 15, ClassName = "barrel", ClassList = [new RfClassListEntry("y", 2)], Skin = "rusty", Links = [1, 2] }],
        Triggers =
        [
            new RfTrigger { Uid = uidBase + 16, ScriptName = "t", Shape = 0, ResetsAfter = 1.5f, ResetsTimes = 3, UseKey = true, KeyName = "key", WeaponActivates = true, ActivatedBy = 3, IsNpc = true, IsAuto = true, InVehicle = true, Position = Vector3.One, Radius = 2, AirlockRoomUid = 4, AttachedToUid = 5, UseClutterUid = 6, Disabled = true, ButtonActiveTime = 1, InsideTime = 2, Team = 1, Links = [uidBase + 7] },
            new RfTrigger { Uid = uidBase + 17, Shape = 1, Position = Vector3.UnitY, Orientation = Turned, Size = new Vector3(1, 2, 3), OneWay = true },
        ],
        ParticleEmitters = [new RfParticleEmitter { Uid = uidBase + 18, ClassName = "Particle Emitter", Shape = 2, SphereRadius = 1, PlaneWidth = 2, PlaneDepth = 3, Texture = "smoke.tga", SpawnDelay = 0.1f, SpawnDelayRandom = 0.2f, Velocity = 3, VelocityRandom = 0.5f, Acceleration = -1, Decay = 2, DecayRandom = 0.25f, Radius = 0.5f, RadiusRandom = 0.1f, GrowthRate = 0.3f, GravityScale = 0.9f, RandomDirection = 0.4f, Color = Teal, FadeColor = new RflColor(1, 2, 3, 4), EmitterFlags = 0x18, ParticleFlags = 0x12340006, InitiallyOn = true, TimeOn = 1, TimeOnRandom = 2, TimeOff = 3, TimeOffRandom = 4, ActiveDistance = 50 }],
        GasRegions =
        [
            new RfGasRegion { Uid = uidBase + 19, ClassName = "Gas Region", Shape = 1, Radius = 3, Color = Teal, Density = 0.5f },
            new RfGasRegion { Uid = uidBase + 20, ClassName = "Gas Region", Shape = 2, Size = new Vector3(1, 2, 3), Color = Teal, Density = 0.25f },
        ],
        Decals = [new RfDecal { Uid = uidBase + 21, ClassName = "Decal", Extents = new Vector3(1, 1, 0.1f), Texture = "scorch.tga", Alpha = 128, SelfIlluminated = true, TilingRaw = 0x1010001, Scale = 2 }],
        ClimbingRegions = [new RfClimbingRegion { Uid = uidBase + 22, ClassName = "Climbing Region", RegionType = 2, Extents = new Vector3(1, 4, 1) }],
        RoomEffects =
        [
            new RfRoomEffect { Uid = uidBase + 23, ClassName = "Room Effect", EffectType = 2, Liquid = new RfLiquidProperties { Waveform = 2, Depth = 3, SurfaceTexture = "water.tga", Color = Teal, Visibility = 10, LiquidType = 2, Plankton = true, PixelsPerMeterU = 32, PixelsPerMeterV = 16, Angle = 90, Scroll = new Vector2(0.1f, 0.2f) }, IsCold = true, IsOutside = true, IsAirlock = true },
            new RfRoomEffect { Uid = uidBase + 24, ClassName = "Room Effect", EffectType = 3, AmbientColor = Teal },
        ],
        EaxEffects = [new RfEaxEffect { Uid = uidBase + 25, ClassName = "EAX Effect", EffectName = "cave" }],
        BoltEmitters = [new RfBoltEmitter { Uid = uidBase + 26, ClassName = "Bolt Emitter", TargetUid = uidBase + 27, SourceControlDistance = 1, TargetControlDistance = 2, Thickness = 0.1f, Jitter = 0.2f, Segments = 8, SpawnDelay = 0.5f, SpawnDelayRandom = 0.1f, Decay = 1, DecayRandom = 0.2f, Color = Teal, Texture = "bolt.tga", Flags = 0x1E, InitiallyOn = true }],
        Targets = [new RfTarget { Uid = uidBase + 27, ClassName = "Target", Position = Vector3.One }],
        PushRegions =
        [
            new RfPushRegion { Uid = uidBase + 28, ClassName = "Push Region", Shape = 1, Radius = 2, Strength = 5, Packed = 0x00030041 },
            new RfPushRegion { Uid = uidBase + 29, ClassName = "Push Region", Shape = 3, Extents = new Vector3(1, 2, 3), Strength = -2, Packed = 0x10 },
        ],
    };

    private static RfMovingData Moving(int keyUid) => new()
    {
        Keyframes =
        [
            new RfKeyframe { Uid = keyUid, Position = Vector3.Zero, Orientation = Turned, ScriptName = "k0", PauseTime = 1, DepartTravelTime = 2, ReturnTravelTime = 3, AccelTime = 0.5f, DecelTime = 0.25f, EventUid = 9, ItemUid1 = 1, ItemUid2 = 2, DegreesAboutAxis = 90 },
            new RfKeyframe { Uid = keyUid + 1, Position = new Vector3(0, 4, 0), HiddenInEditor = true },
        ],
        MemberTransforms = [new RfMemberTransform(500, new Vector3(1, 0, 0), Turned)],
        IsDoor = true,
        RotateInPlace = true,
        StartsBackwards = true,
        UseTravelTimeAsSpeed = true,
        ForceOrient = true,
        NoPlayerCollide = true,
        MovementType = 3,
        StartingKeyframe = 1,
        StartSound = new RfSound("start.wav", 0.5f),
        LoopingSound = new RfSound("loop.wav", 0.75f),
        StopSound = new RfSound("stop.wav", 1),
        CloseSound = new RfSound("close.wav", 0.25f),
    };

    private static RfAlpineMesh Mesh(int uid) => new()
    {
        Uid = uid, Position = new Vector3(3, 2, 1), Orientation = Turned, ScriptName = "m", MeshFile = "crate.v3m", StateAnim = "",
        Collision = 2, TextureOverrides = [new RfTextureOverride(1, "wood.tga")], Material = 3,
        Clutter = new RfAlpineMeshClutter { Life = 10, Debris = "debris.v3m", ExplosionVclip = "boom", ExplosionRadius = 2, DebrisVelocity = 5, DamageFactors = [.. Enumerable.Range(0, 11).Select(i => i * 0.5f)], Corpse = "crate_dead.v3m", CorpseAnim = "", CorpseCollision = 1, CorpseMaterial = -1 },
        TrailingFlags = 1, BrushGeometrySource = 2, CollisionMesh = "crate_col.v3m", DrawScale = 1.5f,
    };

    /// <summary>A two-group Alpine file holding every record type, Alpine objects, flagged brushes and a chunk kept as stored.</summary>
    internal static RfGroupFile Rich()
    {
        var corona = new RfCorona { Uid = 700, Position = Vector3.UnitY, Orientation = Turned, ScriptName = "c", Color = Teal, CoronaBitmap = "corona.tga", ConeAngle = 80, Intensity = 0.5f, RadiusDistance = 0.6f, RadiusScale = 0.8f, DiminishDistance = -0.05f, VolumetricBitmap = "beam.tga", VolumetricHeight = 1.6f, VolumetricLength = 3.2f };
        var plainCorona = corona with { Uid = 701, VolumetricBitmap = "", VolumetricHeight = 0, VolumetricLength = 0 };
        var door = new RfGroup
        {
            Name = "door",
            Moving = Moving(900),
            Brushes = [Brush(500, new Vector3(1, 0, 0), RfSolidFlags.Detail, new RfAlpineBrushData(RfAlpineBrushFlags.Breakable | RfAlpineBrushFlags.NoShadowCast, 0x83, [])), Brush(501, Vector3.Zero)],
            Objects = EveryObject(1000) with { AlpineMeshes = [Mesh(600)], Coronas = [corona, plainCorona] },
        };
        var plain = new RfGroup { Name = "static", Brushes = [Brush(502, new Vector3(0, 0, 5), RfSolidFlags.Air)], Objects = EveryObject(2000) };
        uint notesId = 0x0AFBAE02;
        string notesName = RflLevelReader.SectionName(notesId);
        return new RfGroupFile
        {
            Name = "rich.rfg",
            Version = 300,
            Groups = [door, plain],
            BrushMetadata = new RfBrushMetadata(1, 6, [new RfBrushMetadataEntry(0, RfAlpineBrushFlags.Breakable | RfAlpineBrushFlags.NoShadowCast, 0x83, [])]),
            OpaqueChunks = [new RfOpaqueSection(notesId, notesName, 0, new byte[] { 0, 0, 0, 0 }, $"{notesName} are kept as stored")],
        };
    }

    [Fact]
    public void Every_record_survives_write_and_read()
    {
        var file = Rich();
        var result = RfgWriter.Write(file);
        var back = RfgReader.Read(result.Bytes, file.Name);

        Assert.Empty(result.Notes.Where(n => !n.Contains("events", StringComparison.Ordinal)));
        Assert.Equal(RfGroupFallbacks.None, back.Fallbacks);
        Assert.Empty(back.Problems);
        // Play_Explosion stores no matrix and had none, so nothing was changed.
        Assert.Empty(result.Notes);
        Assert.Empty(RfModelDiff.Compare(file, back, 20, RfgWriterSampleTests.PlacementProperties));

        // A second write of what was read gives the same bytes.
        Assert.Equal(result.Bytes, RfgWriter.Write(back).Bytes);
    }

    [Theory]
    [InlineData(0x60)]
    [InlineData(0x80)]
    [InlineData(0xAE)]
    [InlineData(0xB4)]
    [InlineData(0xC8)]
    [InlineData(300)]
    public void Files_of_every_layout_are_rewritten_byte_for_byte(int version)
    {
        // Bytes from the tests' own independent writer, so a mistake made the same way in reader and writer shows.
        // That writer lays out moving data, lights and events the modern way only, so older files get brushes alone
        // (the real pre-release files of the sample tests cover those records' old layouts).
        bool modern = version >= 0xAE;
        var w = new LvlBytes(version);
        w.U32(RfgReader.Magic).I32(version);
        if (version >= 0x6C) w.I32(2);
        RfgReaderTests.Group(w, "door", moving: modern, brushes: 2,
            lights: modern ? l => l.I32(1).Light(7, Vector3.One, 0x18, 5) : null,
            events: modern ? e => e.I32(2).Event(8, "Teleport_Player", Vector3.Zero, Turned).Event(9, "Alarm", Vector3.Zero, Turned) : null);
        if (version >= 0x6C) RfgReaderTests.Group(w, "static", moving: false, brushes: 1);
        var data = w.ToArray();

        var file = RfgReader.Read(data, "layout.rfg");
        Assert.Equal(RfGroupFallbacks.None, file.Fallbacks);
        var written = RfgWriter.Write(file, RfgWriteOptions.AsStoredFor(file));
        Assert.Equal(data, written.Bytes);
        Assert.Empty(written.Notes);
    }

    [Fact]
    public void Reader_fallback_layouts_are_reproduced_only_when_asked()
    {
        // A stock-version file that stores an Alpine event matrix (Alpine RED saving with version 200).
        var w = new LvlBytes(0xC8);
        w.U32(RfgReader.Magic).I32(0xC8).I32(1);
        RfgReaderTests.Group(w, "g", moving: false, brushes: 0, events: e => e.I32(1).Event(1, "Anchor_Marker_Orient", Vector3.Zero, Turned));
        var data = w.ToArray();
        var file = RfgReader.Read(data, "12.rfg");
        Assert.Equal(RfGroupFallbacks.AlpineEventMatricesBelow300, file.Fallbacks);

        Assert.Equal(data, RfgWriter.Write(file, RfgWriteOptions.AsStoredFor(file)).Bytes);

        // The default writes version 300, where the matrix belongs, so the file needs no fallback any more.
        var upgraded = RfgWriter.Write(file);
        var back = RfgReader.Read(upgraded.Bytes, "12.rfg");
        Assert.Equal(300, back.Version);
        Assert.Equal(RfGroupFallbacks.None, back.Fallbacks);
        Assert.Equal(Turned, back.Groups[0].Objects.Events[0].Orientation);
    }

    [Fact]
    public void Event_orientations_follow_the_class_rules_of_the_version()
    {
        var file = new RfGroupFile
        {
            Name = "events.rfg",
            Groups =
            [
                new RfGroup
                {
                    Name = "g",
                    Objects = new RfObjectSet
                    {
                        Events =
                        [
                            new RfEvent { Uid = 1, ClassName = "Play_Explosion", Orientation = Turned },    // REDUX's mistake: never stored
                            new RfEvent { Uid = 2, ClassName = "Teleport_Player" },                         // stored, but missing
                            new RfEvent { Uid = 3, ClassName = "AF_Teleport_Player", Orientation = Turned }, // Alpine, from 300
                        ],
                    },
                },
            ],
        };

        var result = RfgWriter.Write(file);
        var events = RfgReader.Read(result.Bytes, file.Name).Groups[0].Objects.Events;
        Assert.Null(events[0].Orientation);
        Assert.Equal(RfMatrix.Identity, events[1].Orientation);
        Assert.Equal(Turned, events[2].Orientation);
        Assert.Contains(result.Notes, n => n.StartsWith("1 events had an orientation", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("1 events of a class that stores an orientation", StringComparison.Ordinal));

        // Stock RED's version has no Alpine matrices: the Alpine event's is left out.
        var stock = RfgWriter.Write(file, new RfgWriteOptions { Version = 0xC8 });
        var stockEvents = RfgReader.Read(stock.Bytes, file.Name).Groups[0].Objects.Events;
        Assert.Null(stockEvents[2].Orientation);
        Assert.Contains(stock.Notes, n => n.StartsWith("2 events had an orientation", StringComparison.Ordinal));
    }

    [Fact]
    public void Scrolling_faces_without_an_id_of_their_own_get_one()
    {
        var brush = Brush(1, Vector3.Zero);
        var faces = brush.Geometry.Faces.ToBuilder();
        // Face 1 scrolls with no id; face 3 scrolls with the id face 0 already has.
        faces[1] = faces[1] with { FaceId = -1 };
        faces[3] = faces[3] with { FaceId = faces[0].FaceId, Scroll = new Vector2(2, 0), Flags = RfFaceFlags.ScrollTexture, RawFlags = 0x10 };
        brush = brush with { Geometry = brush.Geometry with { Faces = faces.ToImmutable() } };
        var file = new RfGroupFile { Name = "scroll.rfg", Groups = [new RfGroup { Name = "g", Brushes = [brush] }] };

        var result = RfgWriter.Write(file);
        var back = RfgReader.Read(result.Bytes, file.Name).Groups[0].Brushes[0].Geometry;
        Assert.Equal(new Vector2(0.25f, -1f), back.Faces[1].Scroll);
        Assert.Equal(new Vector2(2, 0), back.Faces[3].Scroll);
        Assert.Null(back.Faces[0].Scroll);
        Assert.Empty(back.OrphanScrolls);
        Assert.NotEqual(-1, back.Faces[1].FaceId);
        Assert.Equal(back.Faces.Length, back.Faces.Select(f => f.FaceId).Distinct().Count());
        Assert.Contains(result.Notes, n => n.StartsWith("2 scrolling faces", StringComparison.Ordinal));
    }

    [Fact]
    public void Alpine_data_travels_with_the_first_group()
    {
        var file = Rich();
        var second = file.Groups[1];
        var flagged = second.Brushes[0] with { Alpine = new RfAlpineBrushData(RfAlpineBrushFlags.Geoable, 0, []) };
        file = file with { Groups = [file.Groups[0], second with { Brushes = [flagged], Objects = second.Objects with { AlpineMeshes = [Mesh(601)] } }] };

        var result = RfgWriter.Write(file);
        var back = RfgReader.Read(result.Bytes, file.Name);
        Assert.Contains(result.Notes, n => n.StartsWith("1 brushes outside the first group", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.StartsWith("1 Alpine meshes and coronas of later groups", StringComparison.Ordinal));
        Assert.Null(back.Groups[1].Brushes[0].Alpine);
        Assert.Equal([600, 601], back.Groups[0].Objects.AlpineMeshes.Select(m => m.Uid));
    }

    [Fact]
    public void Alpine_chunks_are_written_in_alpine_order()
    {
        var file = Rich();
        uint bags = 0x0AFBAE04, terrain = 0x0AFBAE0B;
        file = file with
        {
            OpaqueChunks =
            [
                new RfOpaqueSection(terrain, RflLevelReader.SectionName(terrain), 0, new byte[] { 9, 9 }, "kept"),
                new RfOpaqueSection(bags, RflLevelReader.SectionName(bags), 0, new byte[] { 0, 0, 0, 0 }, "kept"),
                .. file.OpaqueChunks,
            ],
        };
        var bytes = RfgWriter.Write(file).Bytes;
        var ids = ChunkIds(bytes);
        Assert.Equal([0x0AFBAE01u, 0x0AFBAE02u, 0x0AFBAE03u, bags, terrain, 0x0AFBAE05u], ids);
        var back = RfgReader.Read(bytes, file.Name);
        Assert.Equal([0x0AFBAE02u, bags, terrain], back.OpaqueChunks.Select(c => c.Id));
    }

    /// <summary>The chunk ids after the groups, found by walking back from the end over the chunk lengths.</summary>
    private static List<uint> ChunkIds(byte[] bytes)
    {
        // Find the first mesh chunk header, then walk forward.
        int at = -1;
        for (int i = 8; i + 8 <= bytes.Length && at < 0; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i)) != 0x0AFBAE01) continue;
            int pos = i;
            bool walks = true;
            while (pos < bytes.Length && walks)
            {
                if (pos + 8 > bytes.Length) { walks = false; break; }
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
                if ((BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos)) & 0xFFFFFF00) != 0x0AFBAE00 || length < 0) walks = false;
                pos += 8 + Math.Max(0, length);
            }
            if (walks && pos == bytes.Length) at = i;
        }
        var ids = new List<uint>();
        while (at >= 0 && at < bytes.Length)
        {
            ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
            at += 8 + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
        }
        return ids;
    }

    [Fact]
    public void Versions_are_checked()
    {
        var file = Rich();
        Assert.Throws<ArgumentOutOfRangeException>(() => RfgWriter.Write(file, new RfgWriteOptions { Version = 0x40 }));
        var e = Assert.Throws<ArgumentException>(() => RfgWriter.Write(file, new RfgWriteOptions { Version = 0x60 }));
        Assert.Contains("exactly one group", e.Message);
    }

    [Fact]
    public void Older_versions_drop_what_they_cannot_hold_and_say_so()
    {
        var file = Rich();
        var one = file with { Groups = [file.Groups[0]] };
        var result = RfgWriter.Write(one, new RfgWriteOptions { Version = 0x80 });
        var back = RfgReader.Read(result.Bytes, file.Name);
        Assert.Equal(0x80, back.Version);
        Assert.Contains(result.Notes, n => n.Contains("cutscene path nodes", StringComparison.Ordinal));
        Assert.Contains(result.Notes, n => n.Contains("EAX effects", StringComparison.Ordinal));
        Assert.Empty(back.Groups[0].Objects.EaxEffects);
        Assert.Equal(one.Groups[0].Objects.Lights[0].Range, back.Groups[0].Objects.Lights[0].Range);
        Assert.Equal(one.Groups[0].Brushes.Length, back.Groups[0].Brushes.Length);
    }

    [Fact]
    public void Text_a_group_file_cannot_hold_is_replaced_and_reported()
    {
        var file = new RfGroupFile { Name = "text.rfg", Groups = [new RfGroup { Name = "Gruppe é中" }] };
        var result = RfgWriter.Write(file);
        Assert.Equal("Gruppe é?", RfgReader.Read(result.Bytes, file.Name).Groups[0].Name);
        Assert.Contains(result.Notes, n => n.StartsWith("1 names held characters", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_planes_are_computed_from_the_corners()
    {
        var brush = Brush(1, Vector3.Zero);
        var expected = brush.Geometry.Faces.Select(f => f.Plane!.Value).ToList();
        brush = brush with { Geometry = brush.Geometry with { Faces = [.. brush.Geometry.Faces.Select(f => f with { Plane = null })] } };
        var file = new RfGroupFile { Name = "planes.rfg", Groups = [new RfGroup { Name = "g", Brushes = [brush] }] };
        var back = RfgReader.Read(RfgWriter.Write(file).Bytes, file.Name).Groups[0].Brushes[0].Geometry.Faces;
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.True(Vector3.Distance(expected[i].Normal, back[i].Plane!.Value.Normal) < 1e-5f, $"face {i} normal");
            Assert.Equal(expected[i].Distance, back[i].Plane!.Value.Distance, 4);
        }
    }
}
