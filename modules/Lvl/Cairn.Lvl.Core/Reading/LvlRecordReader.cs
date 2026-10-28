using System.Runtime.InteropServices;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>
/// The layout choices one read is made with: the file version every field is gated by, plus the fallbacks the
/// corpus needs (files whose stated version does not match how they were written).
/// </summary>
internal sealed record LvlRules
{
    public required int Version { get; init; }

    /// <summary>Read the matrix of the Alpine teleport/clone/anchor events (Alpine RED reads it from 300).</summary>
    public bool AlpineEventMatrices { get; init; }

    /// <summary>Read a matrix for <c>Play_Explosion</c> events too (REDUX output that RED cannot read).</summary>
    public bool ReduxEventMatrices { get; init; }

    /// <summary>A 0xAE group file without the moving groups' no-player-collide byte.</summary>
    public bool NoPlayerCollideAbsent { get; init; }

    /// <summary>Accept the version 306 trailing blocks after the Alpine mesh records.</summary>
    public bool AlpineMeshTrailingBlocks { get; init; }

    public static LvlRules For(int version, bool groupFile) => new()
    {
        Version = version,
        AlpineEventMatrices = version >= 300,
        AlpineMeshTrailingBlocks = groupFile || version >= 306,
    };
}

/// <summary>
/// Reads the records RF1 and Alpine levels and group files share (solids, brushes, every object type, groups and
/// moving data, Alpine chunks), each field gated by the file version exactly as the editor reads it. Every method
/// throws only <see cref="LvlBodyException"/>; callers decide what a failure costs.
/// </summary>
internal sealed class LvlRecordReader(LvlRules rules)
{
    private readonly int _v = rules.Version;

    public LvlRules Rules { get; } = rules;

    /// <summary>Face corners that name a vertex the solid does not have (kept as stored; converters skip such faces).</summary>
    public int BadCorners { get; private set; }

    // Smallest possible sizes, used to prove a count fits before anything is allocated for it.
    private const int HeadBytes = 4 + 2 + 12 + 36 + 2 + 1;
    private const int BrushBytes = 4 + 12 + 36 + 30 + 12;

    // ---------- common pieces ----------

    internal readonly record struct Head(int Uid, string ClassName, Vector3 Position, RfMatrix Orientation, string ScriptName, bool Hidden);

    /// <summary>The common object head: uid, class, position, orientation, script name, hidden.</summary>
    public Head ReadHead(LvlCursor c) => new(c.I32(), c.Str(), c.Vec3(), c.StockMatrix(), c.Str(), c.Bool());

    private static T Place<T>(T o, Head h) where T : RfPlacedObject =>
        (T)((RfPlacedObject)o with
        {
            Uid = h.Uid,
            ClassName = h.ClassName,
            Position = h.Position,
            Orientation = h.Orientation,
            ScriptName = h.ScriptName,
            HiddenInEditor = h.Hidden,
        });

    public static ImmutableArray<T> List<T>(LvlCursor c, int minimumBytes, Func<LvlCursor, T> read)
    {
        int n = c.Count(minimumBytes);
        if (n == 0) return [];
        var a = new T[n];
        for (int i = 0; i < n; i++) a[i] = read(c);
        return ImmutableCollectionsMarshal.AsImmutableArray(a);
    }

    private ImmutableArray<RfClassListEntry> ClassList(LvlCursor c)
    {
        if (_v < 0x34) return [];
        return List(c, 6, x => new RfClassListEntry(x.Str(), x.I32()));
    }

    // ---------- solids ----------

    /// <summary>A solid: the compiled geometry (0x100) or the geometry of a brush or mover.</summary>
    public RfGeometry Geometry(LvlCursor c)
    {
        uint unknown = 0, modifiability;
        string name;
        if (_v >= 0xC8)
        {
            unknown = c.U32();
            modifiability = c.U32();
            name = c.Str();
        }
        else
        {
            name = c.Str();
            modifiability = c.U32();
        }

        int textureCount = c.I32();
        if (textureCount < 0)
            throw new LvlBodyException("the solid embeds its textures (a negative texture count), which no released editor writes");
        if ((long)textureCount * 2 > c.Left) throw new LvlBodyException($"texture count {textureCount} does not fit");
        var textures = new string[textureCount];
        for (int i = 0; i < textureCount; i++) textures[i] = c.Str();

        // RED reads the scroll list here only after 0xB4; up to 0xB4 an older 41-byte list sits here and the
        // scroll list comes after the surfaces.
        var scrolls = new List<RfScrollEntry>();
        ImmutableArray<byte> legacy = [];
        if (_v > 0xB4) ReadScrolls(c, scrolls);
        else legacy = c.Bytes(41 * c.Count(41));

        var rooms = List(c, 30, Room);
        var subrooms = _v >= 0x71 ? List(c, 8, x => new RfSubroomList(x.I32(), x.Uids())) : [];
        var portals = List(c, 32, x => new RfPortal(x.I32(), x.I32(), x.Vec3(), x.Vec3()));

        int vertexCount = c.Count(12);
        var vertices = new Vector3[vertexCount];
        for (int i = 0; i < vertexCount; i++) vertices[i] = c.Vec3();

        var faces = Faces(c, vertexCount);
        var surfaces = List(c, 40, Surface);
        if (_v <= 0xB4 && _v >= 0x5E) ReadScrolls(c, scrolls);

        var orphans = AttachScrolls(faces, scrolls);
        return new RfGeometry
        {
            Unknown = unknown,
            Modifiability = modifiability,
            Name = name,
            Textures = ImmutableCollectionsMarshal.AsImmutableArray(textures),
            Rooms = rooms,
            SubroomLists = subrooms,
            Portals = portals,
            Vertices = ImmutableCollectionsMarshal.AsImmutableArray(vertices),
            Faces = ImmutableCollectionsMarshal.AsImmutableArray(faces),
            Surfaces = surfaces,
            OrphanScrolls = orphans,
            LegacyRecords = legacy,
        };
    }

    private static void ReadScrolls(LvlCursor c, List<RfScrollEntry> into)
    {
        int n = c.Count(12);
        for (int i = 0; i < n; i++) into.Add(new RfScrollEntry(c.I32(), c.Vec2()));
    }

    /// <summary>Gives each scroll entry to the first face with its id; entries no face claims are returned.</summary>
    private static ImmutableArray<RfScrollEntry> AttachScrolls(RfFace[] faces, List<RfScrollEntry> scrolls)
    {
        if (scrolls.Count == 0) return [];
        var byId = new Dictionary<int, int>();
        for (int i = 0; i < faces.Length; i++)
            if (faces[i].FaceId != -1) byId.TryAdd(faces[i].FaceId, i);
        var orphans = new List<RfScrollEntry>();
        foreach (var s in scrolls)
        {
            if (byId.TryGetValue(s.FaceId, out int i) && faces[i].Scroll is null)
                faces[i] = faces[i] with { Scroll = s.Speed };
            else
                orphans.Add(s);
        }
        return [.. orphans];
    }

    private RfRoom Room(LvlCursor c)
    {
        int id = _v >= 0x56 ? c.I32() : -1;
        var min = c.Vec3();
        var max = c.Vec3();
        bool sky = _v >= 0x4B && c.Bool();
        bool cold = _v >= 0x65 && c.Bool();
        bool outside = _v >= 0x68 && c.Bool();
        bool airlock = _v >= 0x68 && c.Bool();
        bool liquid = _v >= 0x53 && c.Bool();
        bool ambient = _v >= 0x78 && c.Bool();
        bool subroom = _v >= 0x71 && c.Bool();
        bool alpha = _v >= 0x75 && c.Bool();
        float life = _v >= 0x7A ? c.F32() : -1f;
        string eax = _v >= 0xB4 ? c.Str() : "";
        RfLiquidProperties? lq = null;
        if (liquid)
        {
            var p = new RfLiquidProperties
            {
                Depth = c.F32(),
                Color = c.Color(),
                SurfaceTexture = c.Str(),
                Visibility = c.F32(),
                LiquidType = _v >= 0x59 ? c.I32() : 1,
                Alpha = _v >= 0x6D ? c.I32() : 60,
            };
            if (_v >= 0x74)
                p = p with { Plankton = c.Bool(), PixelsPerMeterU = c.I32(), PixelsPerMeterV = c.I32(), Angle = c.F32(), Waveform = c.I32() };
            if (_v >= 0x81) p = p with { Scroll = c.Vec2() };
            lq = p;
        }
        RflColor? ambientColor = ambient ? c.Color() : null;
        return new RfRoom
        {
            Id = id,
            BoundsMin = min,
            BoundsMax = max,
            IsSky = sky,
            IsCold = cold,
            IsOutside = outside,
            IsAirlock = airlock,
            IsSubroom = subroom,
            HasAlpha = alpha,
            Life = life,
            EaxEffect = eax,
            Liquid = lq,
            AmbientColor = ambientColor,
        };
    }

    private RfFace[] Faces(LvlCursor c, int vertexCount)
    {
        bool plane = _v >= 0xA7;
        int n = c.Count(plane ? 56 : 40);
        var faces = new RfFace[n];
        for (int i = 0; i < n; i++)
        {
            RfPlane? p = plane ? new RfPlane(c.Vec3(), c.F32()) : null;
            int texture = c.I32();
            int surface = c.I32();
            int faceId = c.I32();
            int reservedA = c.I32();
            int reservedB = c.I32();
            int portal = c.I32();
            uint flags = c.U32();
            uint smoothing = c.U32();
            int room = c.I32();
            int k = c.Count(12);
            // RF keeps the surface index as 16 bits and stores lightmap coordinates only when that is >= 0.
            bool lightmap = RfFace.SurfaceHasLightmapUvs(surface);
            c.Need((long)k * (lightmap ? 20 : 12));
            var corners = new RfCorner[k];
            for (int j = 0; j < k; j++)
            {
                int vertex = c.I32();
                if ((uint)vertex >= (uint)vertexCount) BadCorners++;
                var uv = c.Vec2();
                corners[j] = lightmap ? new RfCorner(vertex, uv, c.Vec2()) : new RfCorner(vertex, uv);
            }
            faces[i] = new RfFace
            {
                Plane = p,
                Texture = texture,
                Surface = surface,
                FaceId = faceId,
                ReservedA = reservedA,
                ReservedB = reservedB,
                PortalPlus2 = portal,
                Flags = (RfFaceFlags)(flags & 0xFFFF),
                RawFlags = flags,
                Smoothing = smoothing,
                Room = room,
                Corners = ImmutableCollectionsMarshal.AsImmutableArray(corners),
            };
        }
        return faces;
    }

    private RfSurface Surface(LvlCursor c)
    {
        int lightmap = c.I32();
        byte x = c.U8(), y = c.U8(), w = c.U8(), h = c.U8();
        var legacy = new List<float>();
        if (_v < 0x73)
            for (int i = 0; i < 12; i++) legacy.Add(c.F32());
        var ppm = c.Vec2();
        var min = c.Vec3();
        var max = c.Vec3();
        RfPlane plane = default;
        int smooth = 0, unknown = 0, dropped = 0, uAxis = 0, vAxis = 0;
        Vector2 add = default, scale = default;
        if (_v >= 0x6E)
        {
            plane = new RfPlane(c.Vec3(), c.F32());
            smooth = c.I32();
        }
        if (_v >= 0x7B) unknown = c.I32();
        if (_v >= 0x6E)
        {
            dropped = c.I32();
            uAxis = c.I32();
            vAxis = c.I32();
            add = c.Vec2();
            scale = c.Vec2();
        }
        if (_v >= 0x6E && _v < 0x73)
        {
            int n = c.Count(32);
            for (int i = 0; i < n * 8; i++) legacy.Add(c.F32());
        }
        int room = _v >= 0x73 ? c.I32() : -1;
        return new RfSurface
        {
            Lightmap = lightmap,
            X = x,
            Y = y,
            Width = w,
            Height = h,
            PixelsPerMeter = ppm,
            BoundsMin = min,
            BoundsMax = max,
            Plane = plane,
            ShouldSmooth = smooth,
            Unknown = unknown,
            DroppedAxis = dropped,
            UAxis = uAxis,
            VAxis = vAxis,
            UvAdd = add,
            UvScale = scale,
            Room = room,
            LegacyData = [.. legacy],
        };
    }

    // ---------- brushes ----------

    public ImmutableArray<RfBrush> Brushes(LvlCursor c) => List(c, BrushBytes, Brush);

    public RfBrush Brush(LvlCursor c)
    {
        int uid = _v >= 0x57 ? c.I32() : -1;
        var pos = c.Vec3();
        var orient = c.StockMatrix();
        var geometry = Geometry(c);
        uint flags = 0;
        (byte, byte)? legacyFlags = null;
        int? extra = null;
        if (_v >= 0x51)
        {
            flags = c.U32();
            // Every group file below 0x80 has one more i32 here (found in the corpus, not in RED's reader).
            if (_v < 0x80) extra = c.I32();
        }
        else
        {
            legacyFlags = (c.U8(), c.U8());
        }
        int life = _v >= 0x7A ? c.I32() : -1;
        int state = c.I32();
        return new RfBrush
        {
            Uid = uid,
            Position = pos,
            Orientation = orient,
            Geometry = geometry,
            SolidFlags = (RfSolidFlags)flags,
            LegacyFlags = legacyFlags,
            LegacyExtra = extra,
            Life = life,
            State = state,
        };
    }

    // ---------- objects ----------

    public RfGeoRegion GeoRegion(LvlCursor c)
    {
        int uid = c.I32();
        if (_v < 0x63) throw new LvlBodyException($"geo regions of version {_v} are not readable");
        uint packed = c.U32();
        int shape = (int)(packed & 7);
        bool shallow = (packed & 0x20) != 0;
        string? legacyTexture = _v < 0xA3 && (packed & 0x10) != 0 ? c.Str() : null;
        float? shallowDepth = shallow ? c.F32() : null;
        Vector3 pos = default, size = default;
        RfMatrix? orient = null;
        float radius = 0;
        float? legacyFloat = null;
        if (shape == 2)
        {
            pos = c.Vec3();
            if (shallow) orient = c.StockMatrix();
            if (_v < 0xA3) legacyFloat = c.F32();
            radius = c.F32();
        }
        else if (shape == 4)
        {
            pos = c.Vec3();
            orient = c.StockMatrix();
            size = c.Vec3();
            if (_v < 0xA3) legacyFloat = c.F32();
        }
        int? legacyInt = null;
        RflColor? legacyColor = null;
        if (_v < 0xA3)
        {
            legacyInt = c.I32();
            legacyColor = c.Color();
        }
        return new RfGeoRegion
        {
            Uid = uid,
            Position = pos,
            HiddenInEditor = (packed & 0x08) != 0,
            Packed = packed,
            ShallowDepth = shallowDepth,
            Orientation = orient,
            Radius = radius,
            Size = size,
            LegacyTexture = legacyTexture,
            LegacyFloat = legacyFloat,
            LegacyInt = legacyInt,
            LegacyColor = legacyColor,
        };
    }

    public RfLight Light(LvlCursor c)
    {
        var h = ReadHead(c);
        uint flags = 0;
        var color = RfColors.White;
        (int, int)? legacyInts = null;
        if (_v >= 0x62)
        {
            flags = c.U32();
            color = c.Color();
        }
        else
        {
            legacyInts = (c.I32(), c.I32());
        }
        float range = c.F32();
        float? legacyRange = _v < 0x78 ? c.F32() : null;
        float fov = c.F32();
        float dropoff = c.F32();
        float atMax = _v >= 0x78 ? c.F32() : 0f;
        int dropoffType = _v >= 0x79 ? c.I32() : 0;
        float tube = c.F32();
        var l = new RfLight
        {
            Flags = (RfLightFlags)flags,
            Color = color,
            Range = range,
            Fov = fov,
            FovDropoff = dropoff,
            IntensityAtMaxRange = atMax,
            DropoffType = dropoffType,
            TubeWidth = tube,
            LegacyInts = legacyInts,
            LegacyRangeFloat = legacyRange,
        };
        if (_v >= 0x62)
        {
            l = l with
            {
                OnIntensity = c.F32(),
                OnTime = c.F32(),
                OnTimeVariation = c.F32(),
                OffIntensity = c.F32(),
                OffTime = c.F32(),
                OffTimeVariation = c.F32(),
            };
        }
        else
        {
            l = l with { LegacyTailFloat = c.F32(), Color = c.Color(), LegacyTailBytes = (c.U8(), c.U8()) };
        }
        return Place(l, h);
    }

    public RfCutsceneCamera CutsceneCamera(LvlCursor c) => Place(new RfCutsceneCamera(), ReadHead(c));

    public RfCutscenePathNode CutscenePathNode(LvlCursor c) => Place(new RfCutscenePathNode(), ReadHead(c));

    public RfTarget Target(LvlCursor c) => Place(new RfTarget(), ReadHead(c));

    public RfAmbientSound AmbientSound(LvlCursor c) => new()
    {
        Uid = c.I32(),
        Position = c.Vec3(),
        HiddenInEditor = c.Bool(),
        SoundFile = c.Str(),
        MinDistance = c.F32(),
        Volume = c.F32(),
        Rolloff = _v >= 0x67 ? c.F32() : 0.1f,
        StartDelayMs = _v >= 0x67 ? c.I32() : 0,
    };

    /// <summary>The event classes that store an orientation, under the current rules.</summary>
    public bool EventHasMatrix(string cls) => cls switch
    {
        "Teleport" or "Play_Vclip" or "Teleport_Player" => _v >= 0x91,
        "Alarm" => _v >= 0x98,
        "AF_Teleport_Player" or "Clone_Entity" or "Anchor_Marker_Orient" => Rules.AlpineEventMatrices,
        "Play_Explosion" => Rules.ReduxEventMatrices,
        _ => false,
    };

    public RfEvent Event(LvlCursor c)
    {
        var e = new RfEvent
        {
            Uid = c.I32(),
            ClassName = c.Str(),
            Position = c.Vec3(),
            ScriptName = c.Str(),
            HiddenInEditor = c.Bool(),
            Delay = c.F32(),
            Bool1 = c.Bool(),
            Bool2 = _v >= 0x64 && c.Bool(),
            Int1 = c.I32(),
            Int2 = c.I32(),
            Float1 = c.F32(),
            Float2 = c.F32(),
            Str1 = c.Str(),
            Str2 = c.Str(),
            Links = c.Uids(),
        };
        if (EventHasMatrix(e.ClassName)) e = e with { Orientation = c.StockMatrix() };
        if (_v >= 0xB0) e = e with { Color = c.Color() };
        return e;
    }

    public RfRespawnPoint RespawnPoint(LvlCursor c) => new()
    {
        Uid = c.I32(),
        Position = c.Vec3(),
        Orientation = c.StockMatrix(),
        ScriptName = c.Str(),
        HiddenInEditor = c.Bool(),
        Team = c.I32(),
        RedTeam = _v < 0xAC || c.Bool(),
        BlueTeam = _v < 0xAC || c.Bool(),
        Bot = _v < 0xAC || c.Bool(),
    };

    public RfParticleEmitter ParticleEmitter(LvlCursor c)
    {
        var h = ReadHead(c);
        var p = new RfParticleEmitter
        {
            Shape = c.I32(),
            SphereRadius = c.F32(),
            PlaneWidth = c.F32(),
            PlaneDepth = c.F32(),
            Texture = c.Str(),
            SpawnDelay = c.F32(),
            SpawnDelayRandom = c.F32(),
            Velocity = c.F32(),
            VelocityRandom = c.F32(),
            Acceleration = c.F32(),
            Decay = c.F32(),
            DecayRandom = c.F32(),
            Radius = c.F32(),
            RadiusRandom = c.F32(),
            GrowthRate = c.F32(),
            GravityScale = c.F32(),
            RandomDirection = c.F32(),
            Color = c.Color(),
            FadeColor = _v >= 0x9E ? c.Color() : RfColors.White,
            EmitterFlags = c.U32(),
            ParticleFlags = c.U32(),
            InitiallyOn = c.Bool(),
        };
        if (_v >= 0x77) p = p with { TimeOn = c.F32(), TimeOnRandom = c.F32(), TimeOff = c.F32(), TimeOffRandom = c.F32() };
        if (_v >= 0x88) p = p with { ActiveDistance = c.F32() };
        return Place(p, h);
    }

    public RfGasRegion GasRegion(LvlCursor c)
    {
        var h = ReadHead(c);
        int shape = c.I32();
        float radius = shape == 1 ? c.F32() : 0f;
        var size = shape == 2 ? c.Vec3() : default;
        return Place(new RfGasRegion { Shape = shape, Radius = radius, Size = size, Color = c.Color(), Density = c.F32() }, h);
    }

    public RfRoomEffect RoomEffect(LvlCursor c)
    {
        int type = _v >= 0x4B ? c.I32() : 0;
        RfLiquidProperties? liquid = null;
        int? legacyInt = null;
        if (type == 2)
        {
            var p = new RfLiquidProperties
            {
                Waveform = c.I32(),
                Depth = c.F32(),
                SurfaceTexture = c.Str(),
                Color = c.Color(),
                Visibility = c.F32(),
                LiquidType = _v >= 0x59 ? c.I32() : 1,
            };
            if (_v >= 0x6D && _v < 0x81) legacyInt = c.I32();
            if (_v >= 0x74) p = p with { Plankton = c.Bool(), PixelsPerMeterU = c.I32(), PixelsPerMeterV = c.I32(), Angle = c.F32() };
            if (_v >= 0x81) p = p with { Scroll = c.Vec2() };
            liquid = p;
        }
        RflColor? ambient = type == 3 && _v >= 0x78 ? c.Color() : null;
        var e = new RfRoomEffect
        {
            EffectType = type,
            Liquid = liquid,
            AmbientColor = ambient,
            LegacyInt = legacyInt,
            IsCold = _v >= 0x65 && c.Bool(),
            IsOutside = _v >= 0x68 && c.Bool(),
            IsAirlock = _v >= 0x68 && c.Bool(),
        };
        // The object head comes last in this record.
        return Place(e, ReadHead(c));
    }

    public RfClimbingRegion ClimbingRegion(LvlCursor c)
    {
        var h = ReadHead(c);
        return Place(new RfClimbingRegion { RegionType = _v >= 0x60 ? c.I32() : 1, Extents = c.Vec3() }, h);
    }

    public RfBoltEmitter BoltEmitter(LvlCursor c)
    {
        var h = ReadHead(c);
        return Place(new RfBoltEmitter
        {
            TargetUid = c.I32(),
            SourceControlDistance = c.F32(),
            TargetControlDistance = c.F32(),
            Thickness = c.F32(),
            Jitter = c.F32(),
            Segments = c.I32(),
            SpawnDelay = c.F32(),
            SpawnDelayRandom = c.F32(),
            Decay = c.F32(),
            DecayRandom = c.F32(),
            Color = c.Color(),
            Texture = c.Str(),
            Flags = c.U32(),
            InitiallyOn = c.Bool(),
        }, h);
    }

    public RfDecal Decal(LvlCursor c)
    {
        var h = ReadHead(c);
        var d = new RfDecal
        {
            Extents = c.Vec3(),
            Texture = c.Str(),
            Alpha = _v >= 0x5F ? c.I32() : 255,
            SelfIlluminated = _v >= 0x89 && c.Bool(),
        };
        if (_v >= 0x97) d = d with { TilingRaw = c.I32(), Scale = c.F32() };
        return Place(d, h);
    }

    public RfPushRegion PushRegion(LvlCursor c)
    {
        var h = ReadHead(c);
        int shape = c.I32();
        float radius = shape == 1 ? c.F32() : 0f;
        var extents = shape is 2 or 3 ? c.Vec3() : default;
        return Place(new RfPushRegion { Shape = shape, Radius = radius, Extents = extents, Strength = c.F32(), Packed = c.U32() }, h);
    }

    public RfCutscene Cutscene(LvlCursor c) => new()
    {
        Uid = c.I32(),
        HidePlayer = _v >= 0x8F && c.Bool(),
        Fov = _v >= 0x92 ? c.F32() : 0f,
        Shots = List(c, 26, x => new RfCutsceneShot(x.I32(), x.F32(), x.F32(), x.F32(), x.I32(), x.I32(), x.Str())),
    };

    public static RfNamedUidList NamedUidList(LvlCursor c) => new(c.Str(), c.Uids());

    public RfEaxEffect EaxEffect(LvlCursor c)
    {
        string effect = _v >= 0xB4 ? c.Str() : "";
        return Place(new RfEaxEffect { EffectName = effect }, ReadHead(c));
    }

    /// <summary>A waypoint list: nav point indices from 0x8C, legacy waypoints before.</summary>
    public RfWaypointList WaypointList(LvlCursor c)
    {
        string name = c.Str();
        if (_v >= 0x8C) return new RfWaypointList { Name = name, NavPoints = c.Uids() };
        return new RfWaypointList { Name = name, LegacyWaypoints = List(c, 17, LegacyWaypoint) };
    }

    public RfLegacyWaypoint LegacyWaypoint(LvlCursor c) => new()
    {
        Uid = c.I32(),
        Position = c.Vec3(),
        HiddenInEditor = c.Bool(),
        Value = c.F32(),
    };

    /// <summary>A nav point record (without the connection block that follows all of them in a level).</summary>
    public RfNavPoint NavPoint(LvlCursor c)
    {
        var n = new RfNavPoint
        {
            Uid = c.I32(),
            HiddenInEditor = c.Bool(),
            Height = c.F32(),
            Position = c.Vec3(),
            Radius = c.F32(),
            NavType = c.I32(),
            LegacyInt = _v >= 0x52 && _v < 0x8B ? c.I32() : null,
            Directional = _v >= 0x52 && c.Bool(),
        };
        if (n.Directional && _v >= 0x54) n = n with { Orientation = c.StockMatrix() };
        if (_v >= 0x8A) n = n with { Cover = c.Bool(), Hide = c.Bool(), Crunch = c.Bool() };
        if (_v >= 0x8C) n = n with { PauseTime = c.F32() };
        if (_v >= 0x93) n = n with { Links = c.Uids() };
        return n;
    }

    public RfEntity Entity(LvlCursor c)
    {
        var h = ReadHead(c);
        var e = new RfEntity
        {
            Cooperation = c.I32(),
            Friendliness = c.I32(),
            Team = c.I32(),
            WaypointList = c.Str(),
            WaypointMethod = c.Str(),
            Unknown1 = c.U8(),
            Boarded = c.Bool(),
            ReadyToFire = _v >= 0x36 && c.Bool(),
            OnlyAttackPlayer = _v >= 0x40 && c.Bool(),
            WeaponHolstered = _v >= 0x46 && c.Bool(),
            Deaf = _v >= 0x48 && c.Bool(),
            SweepMin = _v >= 0x48 ? c.I32() : 0,
            SweepMax = _v >= 0x48 ? c.I32() : 0,
            IgnoreTerrainWhenFiring = _v >= 0x4C && c.Bool(),
            LegacyInt = _v >= 0x4C && _v < 0x90 ? c.I32() : null,
            Unknown2 = _v >= 0x72 ? c.U8() : (byte)0,
            StartCrouched = _v >= 0x74 && c.Bool(),
            Life = _v >= 0x66 ? c.F32() : -1f,
            Armor = _v >= 0x9E ? c.F32() : -1f,
            Fov = _v >= 0x8B ? c.I32() : 180,
            PrimaryWeapon = c.Str(),
            SecondaryWeapon = c.Str(),
            ItemDrop = c.Str(),
            StateAnim = c.Str(),
            CorpsePose = _v >= 0x50 ? c.Str() : "",
            Skin = _v >= 0x50 ? c.Str() : "",
            DeathAnim = _v >= 0x88 ? c.Str() : "",
        };
        // Before 0x40 the AI mode was a name; no readable file is that old.
        e = e with
        {
            AiMode = c.U8(),
            AttackStyle = c.U8(),
            ClassList = ClassList(c),
            TurretUid = _v >= 0x62 ? c.I32() : -1,
            AlertCameraUid = _v >= 0x74 ? c.I32() : -1,
            AlarmEventUid = _v >= 0x96 ? c.I32() : -1,
            Run = _v >= 0x7F && c.Bool(),
            StartHidden = _v >= 0x82 && c.Bool(),
            WearHelmet = _v >= 0x82 && c.Bool(),
            EndGameIfKilled = _v >= 0x82 && c.Bool(),
            CowerFromWeapon = _v >= 0x82 && c.Bool(),
            QuestionUnarmedPlayer = _v >= 0x82 && c.Bool(),
            DontHum = _v >= 0x9E && c.Bool(),
            NoShadow = _v >= 0x9E && c.Bool(),
            AlwaysSimulate = _v >= 0x9F && c.Bool(),
            PerfectAim = _v >= 0xA1 && c.Bool(),
            PermanentCorpse = _v >= 0xA2 && c.Bool(),
            NeverFly = _v >= 0xA2 && c.Bool(),
            NeverLeave = _v >= 0xA4 && c.Bool(),
            NoPersonaMessages = _v >= 0xA8 && c.Bool(),
            LegacyByte = _v >= 0x83 && _v < 0xA9 ? c.U8() : null,
            FadeCorpseImmediately = _v >= 0xA9 && c.Bool(),
            NeverCollideWithPlayer = _v >= 0xA9 && c.Bool(),
            UseCustomAttackRange = _v >= 0x94 ? c.U8() : (byte)0,
        };
        // The range is stored only when the byte is exactly 1.
        if (e.UseCustomAttackRange == 1) e = e with { CustomAttackRange = c.F32() };
        if (_v >= 0x9A) e = e with { LeftHandHolding = c.Str(), RightHandHolding = c.Str() };
        if (_v >= 0x9D && _v < 0xA9) e = e with { LegacyString = c.Str() };
        return Place(e, h);
    }

    public RfItem Item(LvlCursor c)
    {
        var h = ReadHead(c);
        return Place(new RfItem { Count = c.I32(), RespawnTime = c.I32(), Team = c.I32() }, h);
    }

    public RfClutter Clutter(LvlCursor c)
    {
        var h = ReadHead(c);
        return Place(new RfClutter
        {
            ClassList = ClassList(c),
            LegacySkinIndex = _v >= 0x67 && _v < 0x7C ? c.I32() : null,
            Skin = _v >= 0x76 ? c.Str() : "",
            Links = _v >= 0x7D ? c.Uids() : [],
        }, h);
    }

    public RfTrigger Trigger(LvlCursor c)
    {
        var t = new RfTrigger
        {
            Uid = c.I32(),
            ScriptName = c.Str(),
            HiddenInEditor = c.Bool(),
            Shape = c.I32(),
            ResetsAfter = _v >= 0x86 ? c.F32() : c.I32(),
            ResetsTimes = c.I32(),
            UseKey = c.Bool(),
            KeyName = _v >= 0x67 ? c.Str() : "",
            WeaponActivates = c.Bool(),
            ActivatedBy = c.U8(),
            IsNpc = c.Bool(),
            IsAuto = c.Bool(),
            InVehicle = _v >= 0xA2 && c.Bool(),
        };
        if (t.Shape == 0)
            t = t with { Position = c.Vec3(), Radius = c.F32() };
        else
            t = t with { Position = c.Vec3(), Orientation = c.StockMatrix(), Size = c.Vec3(), OneWay = _v >= 0x6D && c.Bool() };
        return t with
        {
            AirlockRoomUid = _v >= 0x68 ? c.I32() : -1,
            AttachedToUid = _v >= 0xA5 ? c.I32() : -1,
            UseClutterUid = _v >= 0xA6 ? c.I32() : -1,
            Disabled = _v >= 0x74 && c.Bool(),
            ButtonActiveTime = _v >= 0x77 ? c.F32() : 0f,
            InsideTime = _v >= 0x7E ? c.F32() : 0f,
            Team = _v >= 0xB1 ? c.I32() : -1,
            Links = c.Uids(),
        };
    }

    public RfPlayerStart PlayerStart(LvlCursor c) => new(c.Vec3(), c.StockMatrix());

    // ---------- groups ----------

    public RfKeyframe Keyframe(LvlCursor c)
    {
        var k = new RfKeyframe
        {
            Uid = c.I32(),
            Position = c.Vec3(),
            Orientation = _v >= 0x6B ? c.StockMatrix() : RfMatrix.Identity,
            ScriptName = _v >= 0x9E ? c.Str() : "",
            HiddenInEditor = c.Bool(),
            PauseTime = c.F32(),
            DepartTravelTime = c.F32(),
            ReturnTravelTime = _v >= 0x5D ? c.F32() : 0f,
            AccelTime = c.F32(),
            DecelTime = c.F32(),
            EventUid = _v >= 0x5C ? c.I32() : -1,
            ItemUid1 = _v >= 0x95 ? c.I32() : -1,
            ItemUid2 = _v >= 0x95 ? c.I32() : -1,
        };
        if (_v >= 0x69 && _v < 0x8E) k = k with { LegacyAngles = (c.Vec3(), c.Vec3()) };
        if (_v >= 0x8E) k = k with { DegreesAboutAxis = c.F32() };
        return k;
    }

    public RfMovingData MovingData(LvlCursor c)
    {
        var m = new RfMovingData
        {
            Keyframes = List(c, 30, Keyframe),
            MemberTransforms = List(c, 52, x => new RfMemberTransform(x.I32(), x.Vec3(), x.StockMatrix())),
            IsDoor = c.Bool(),
            LegacyByte = _v < 0x68 ? c.U8() : null,
            RotateInPlace = _v >= 0x69 && c.Bool(),
            StartsBackwards = _v >= 0x5C && c.Bool(),
            UseTravelTimeAsSpeed = _v >= 0x9B && c.Bool(),
            ForceOrient = _v >= 0x9C && c.Bool(),
            NoPlayerCollide = _v >= 0xAE && !(Rules.NoPlayerCollideAbsent && _v == 0xAE) && c.Bool(),
            MovementType = c.I32(),
            StartingKeyframe = _v >= 0x5C ? c.I32() : 0,
        };
        if (_v >= 0x5C)
            m = m with { StartSound = Sound(c), LoopingSound = Sound(c), StopSound = Sound(c) };
        if (_v >= 0x67) m = m with { CloseSound = Sound(c) };
        return m;
    }

    private static RfSound Sound(LvlCursor c) => new(c.Str(), c.F32());

    /// <summary>A group record of a level (0x3000000 and 0x3000).</summary>
    public RfLevelGroup LevelGroup(LvlCursor c)
    {
        string name = c.Str();
        byte unknown = c.U8();
        bool moving = _v >= 0x58 && c.Bool();
        return new RfLevelGroup
        {
            Name = name,
            Unknown = unknown,
            Moving = moving ? MovingData(c) : null,
            ObjectUids = c.Uids(),
            BrushUids = c.Uids(),
        };
    }

    // ---------- Alpine chunks ----------

    /// <summary>
    /// The Alpine mesh chunk. Its record tail changed between builds without a version, so each layout is tried and
    /// the first that ends exactly at the chunk's end wins; null when none does.
    /// </summary>
    public (ImmutableArray<RfAlpineMesh> Meshes, RfAlpineMeshLayout Layout)? AlpineMeshes(LvlCursor c)
    {
        int n = c.CountU32(56);
        if (n > 10_000) throw new LvlBodyException($"mesh count {n} is more than Alpine allows");
        int start = c.Position;
        foreach (var layout in (ReadOnlySpan<RfAlpineMeshLayout>)[RfAlpineMeshLayout.Full, RfAlpineMeshLayout.NoCorpse, RfAlpineMeshLayout.NoTail, RfAlpineMeshLayout.ByteMaterial])
        {
            c.Position = start;
            RfAlpineMesh[] meshes;
            try
            {
                meshes = new RfAlpineMesh[n];
                for (int i = 0; i < n; i++) meshes[i] = AlpineMesh(c, layout);
            }
            catch (LvlBodyException)
            {
                continue;
            }
            if (c.Left == 0 || TryMeshTrailingBlocks(c, meshes))
                return (ImmutableCollectionsMarshal.AsImmutableArray(meshes), layout);
        }
        return null;
    }

    private RfAlpineMesh AlpineMesh(LvlCursor c, RfAlpineMeshLayout layout)
    {
        var m = new RfAlpineMesh
        {
            Uid = c.I32(),
            Position = c.Vec3(),
            Orientation = c.AlpineMatrix(),
            ScriptName = c.Str(),
            MeshFile = c.Str(),
            StateAnim = c.Str(),
            Collision = c.U8(),
        };
        int overrides = c.U8();
        var list = new RfTextureOverride[overrides];
        for (int i = 0; i < overrides; i++) list[i] = new RfTextureOverride(c.U8(), c.Str());
        m = m with { TextureOverrides = ImmutableCollectionsMarshal.AsImmutableArray(list) };
        if (layout == RfAlpineMeshLayout.NoTail) return m;
        m = m with { Material = layout == RfAlpineMeshLayout.ByteMaterial ? c.U8() : c.I32() };
        if (!c.Bool()) return m;
        var k = new RfAlpineMeshClutter
        {
            Life = c.F32(),
            Debris = c.Str(),
            ExplosionVclip = c.Str(),
            ExplosionRadius = c.F32(),
            DebrisVelocity = c.F32(),
        };
        var factors = new float[11];
        for (int i = 0; i < factors.Length; i++) factors[i] = c.F32();
        k = k with { DamageFactors = ImmutableCollectionsMarshal.AsImmutableArray(factors) };
        if (layout == RfAlpineMeshLayout.Full)
            k = k with { Corpse = c.Str(), CorpseAnim = c.Str(), CorpseCollision = c.U8(), CorpseMaterial = c.I8() };
        return m with { Clutter = k };
    }

    /// <summary>The version 306 blocks after the records: per-mesh flags, then per-mesh geometry source, collision mesh and draw scale.</summary>
    private bool TryMeshTrailingBlocks(LvlCursor c, RfAlpineMesh[] meshes)
    {
        if (!Rules.AlpineMeshTrailingBlocks || c.Left < meshes.Length) return false;
        int start = c.Position;
        var flags = new byte[meshes.Length];
        for (int i = 0; i < flags.Length; i++) flags[i] = c.U8();
        if (c.Left == 0)
        {
            for (int i = 0; i < meshes.Length; i++) meshes[i] = meshes[i] with { TrailingFlags = flags[i] };
            return true;
        }
        int afterFlags = c.Position;
        foreach (bool withScale in (ReadOnlySpan<bool>)[true, false])
        {
            c.Position = afterFlags;
            var copy = (RfAlpineMesh[])meshes.Clone();
            try
            {
                for (int i = 0; i < copy.Length; i++)
                {
                    copy[i] = copy[i] with { TrailingFlags = flags[i], BrushGeometrySource = c.U8(), CollisionMesh = c.Str() };
                    if (withScale) copy[i] = copy[i] with { DrawScale = c.F32() };
                }
            }
            catch (LvlBodyException)
            {
                continue;
            }
            if (c.Left != 0) continue;
            copy.CopyTo(meshes, 0);
            return true;
        }
        c.Position = start;
        return false;
    }

    public RfCorona Corona(LvlCursor c)
    {
        var k = new RfCorona
        {
            Uid = c.I32(),
            Position = c.Vec3(),
            Orientation = c.AlpineMatrix(),
            ScriptName = c.Str(),
            Color = c.Color(),
            CoronaBitmap = c.Str(),
            ConeAngle = c.F32(),
            Intensity = c.F32(),
            RadiusDistance = c.F32(),
            RadiusScale = c.F32(),
            DiminishDistance = c.F32(),
            VolumetricBitmap = c.Str(),
        };
        if (k.VolumetricBitmap.Length > 0) k = k with { VolumetricHeight = c.F32(), VolumetricLength = c.F32() };
        return k;
    }

    public ImmutableArray<RfCorona> Coronas(LvlCursor c)
    {
        int n = c.CountU32(56);
        var a = new RfCorona[n];
        for (int i = 0; i < n; i++) a[i] = Corona(c);
        return ImmutableCollectionsMarshal.AsImmutableArray(a);
    }

    /// <summary>A group file's brush metadata chunk (0x0AFBAE05).</summary>
    public static RfBrushMetadata BrushMetadata(LvlCursor c)
    {
        uint version = c.U32();
        int entrySize = c.U16();
        if (entrySize < 6) throw new LvlBodyException($"brush metadata entries of {entrySize} bytes are too small");
        int n = c.CountU32(entrySize);
        if (n > 10_000) throw new LvlBodyException($"{n} brush metadata entries is more than Alpine allows");
        var a = new RfBrushMetadataEntry[n];
        for (int i = 0; i < n; i++)
        {
            uint index = c.U32();
            if (index > int.MaxValue) throw new LvlBodyException($"brush index {index} is not plausible");
            a[i] = new RfBrushMetadataEntry((int)index, (RfAlpineBrushFlags)c.U8(), c.U8(), entrySize > 6 ? c.Bytes(entrySize - 6) : []);
        }
        return new RfBrushMetadata(version, entrySize, ImmutableCollectionsMarshal.AsImmutableArray(a));
    }
}
