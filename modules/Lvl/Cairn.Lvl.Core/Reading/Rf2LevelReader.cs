using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>
/// The Red Faction II side of the level reader: version 0x127 levels whose first section is the RF2 bitmap list.
/// The section stream is walked as for RF1, every section inside its own bounds and skipped by its stated length.
/// RF2 records that match RF1's (lights, respawn points, ambient sounds, waypoint lists, keyframes, the player start)
/// use the RF1 readers; the rest have their own: the geometry body and face record (32-bit flags, inline scroll
/// periods, per-corner vertex colours), brushes, coronas, level properties, events, triggers, items, clutter, decals,
/// push regions, moving groups, resource lists and the vertex lighting data. Sections whose RF2 layout is not known
/// are kept as bytes with that reason.
/// </summary>
public static partial class RflLevelReader
{
    static partial void ReadRf2(Rf2ReadContext context, ref RfLevel? level) => level = new Rf2Walker(context).Run();

    /// <summary>RF1 face flags an RF2 face keeps (bits 0-9 and invisible); RF1 shows a scroll only with 0x10.</summary>
    internal const uint Rf2ToRf1FaceFlags = 0x23FF;

    /// <summary>The RF2 face flag that says two scroll periods follow in the face record.</summary>
    internal const uint Rf2InlineScrollFlag = 0x8000;

    /// <summary>A display name for an RF2 section (RF2 reuses some RF1 ids for other data, so names come from here).</summary>
    public static string Rf2SectionName(uint id) => id switch
    {
        0x100 => "Static geometry",
        0x200 => "RF2 section 0x200",
        0x300 => "Lights",
        0x400 => "RF2 section 0x400",
        0x500 => "Ambient sounds",
        0x600 => "Events",
        0x700 => "Multiplayer respawn points",
        0x900 => "Level properties",
        0xC00 => "Room effects (RF2)",
        0xD00 => "RF2 section 0xD00",
        0xE00 => "RF2 section 0xE00",
        0x1000 => "Decals",
        0x1100 => "Push regions",
        0x2000 => "Movers",
        0x3000 => "Moving groups",
        0x4000 => "RF2 section 0x4000",
        0x7000 => "Bitmap list",
        0x7001 => "Character mesh list",
        0x7002 => "Animation list",
        0x7003 => "Static mesh list",
        0x7004 => "Effect list",
        0x7005 => "Texture pack list",
        0x7677 => "Mirrors (RF2)",
        0x7678 => "Coronas",
        0x7900 => "Vertex lighting",
        0x7901 => "RF2 section 0x7901",
        0x10000 => "Waypoint lists",
        0x20000 => "Nav points (RF2)",
        0x30000 => "Entities (RF2)",
        0x40000 => "Items",
        0x50000 => "Clutter",
        0x60000 => "Triggers",
        0x70000 => "Player start",
        0x01000000 => "Level info",
        0x01000001 => "RF2 section 0x1000001",
        0x02000000 => "Brushes",
        0x03000000 => "Groups (RF2)",
        _ => $"RF2 section 0x{id:X}",
    };

    /// <summary>RF2 sections whose layout is not known: kept as bytes, with the reason shown for them.</summary>
    private static string? Rf2OpaqueReason(uint id) => id switch
    {
        0x200 or 0x400 or 0xD00 or 0xE00 or 0x4000 or 0x7677 or 0x7680 or 0x7681 or 0x7682 or 0x7777 or 0x7778
            or 0x7779 or 0x01000001 => "Red Faction II data whose layout is not known; kept as stored",
        0xC00 => "Red Faction II room effects use a layout that is not known; kept as stored",
        0x20000 => "Red Faction II nav points use a layout that is not known; kept as stored",
        0x30000 => "Red Faction II entities use a layout that is not known; kept as stored",
        0x03000000 => "Red Faction II editor groups use a layout that is not known; kept as stored",
        _ => null,
    };

    /// <summary>Walks an RF2 level's section stream.</summary>
    private sealed class Rf2Walker(Rf2ReadContext context)
    {
        private readonly byte[] _data = context.Data;
        private readonly LvlRecordReader _rr = new(LvlRules.For(Rf2Version, groupFile: false));
        private readonly List<string> _problems = [];
        private readonly List<RfSectionRecord> _sections = [];
        private readonly List<RfOpaqueSection> _opaque = [];
        private bool _truncated;
        private int _badCorners, _unsetCoronaColors;

        private RfGeometry _static = RfGeometry.Empty;
        private RfLevelInfo? _info;
        private RfLevelProperties? _props;
        private Rf2LevelProperties? _rf2Props;
        private Rf2VertexLighting? _vertexLighting;
        private byte? _section7901;
        private RfPlayerStart? _playerStart;
        private readonly List<RfBrush> _brushes = [], _movers = [];
        private readonly List<RfLevelGroup> _movingGroups = [];
        private readonly List<RfWaypointList> _waypoints = [];
        private readonly List<RfFileList> _preloads = [];
        private readonly List<RfLight> _lights = [];
        private readonly List<RfAmbientSound> _sounds = [];
        private readonly List<RfEvent> _events = [];
        private readonly List<RfRespawnPoint> _respawns = [];
        private readonly List<RfItem> _items = [];
        private readonly List<RfClutter> _clutter = [];
        private readonly List<RfTrigger> _triggers = [];
        private readonly List<RfDecal> _decals = [];
        private readonly List<RfPushRegion> _push = [];
        private readonly List<RfCorona> _coronas = [];

        public RfLevel Run()
        {
            long pos = context.SectionsStart;
            while (true)
            {
                long left = _data.Length - pos;
                if (left <= 0)
                {
                    Truncated("The file ends without an end marker; everything before that point was read.");
                    break;
                }
                if (left < 8)
                {
                    Truncated($"The file ends inside a section header at offset {pos}; everything before that point was read.");
                    break;
                }
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)pos));
                int length = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan((int)pos + 4));
                if (id == 0) break; // end marker
                if (length < 0 || length > left - 8)
                {
                    _sections.Add(new RfSectionRecord(id, Rf2SectionName(id), pos, length, RfSectionStatus.Truncated,
                        $"claims {length} bytes, {left - 8} remain"));
                    Truncated($"{Rf2SectionName(id)} at offset {pos} claims {length} bytes but only {left - 8} remain; reading stopped there.");
                    break;
                }
                Section(id, pos, new LvlCursor(_data, (int)pos + 8, (int)pos + 8 + length));
                pos += 8L + length;
            }

            _badCorners += _rr.BadCorners;
            if (_badCorners > 0)
                _problems.Add($"{_badCorners} face corners name a vertex that does not exist; those faces cannot be drawn.");
            if (_unsetCoronaColors > 0)
                _problems.Add($"{_unsetCoronaColors} coronas have no colour set (the editor's CD CD CD filler is stored); conversions treat them as white.");
            return Build();
        }

        private void Truncated(string message)
        {
            _truncated = true;
            _problems.Add(message);
        }

        private void Section(uint id, long pos, LvlCursor c)
        {
            string name = Rf2SectionName(id);
            int length = c.Left;
            if (Rf2OpaqueReason(id) is { } reason)
            {
                KeepOpaque(id, name, pos, c, reason);
                _sections.Add(new RfSectionRecord(id, name, pos, length, RfSectionStatus.Opaque, reason));
                return;
            }
            try
            {
                var (status, detail) = ReadSection(id, c);
                if (status == RfSectionStatus.Unknown)
                {
                    KeepOpaque(id, name, pos, c, "unknown section");
                    _problems.Add($"Unknown section 0x{id:X8} ({length} bytes) at offset {pos} was kept as stored.");
                }
                _sections.Add(new RfSectionRecord(id, name, pos, length, status, detail));
            }
            catch (LvlBodyException e)
            {
                KeepOpaque(id, name, pos, c, e.Message);
                _problems.Add($"{name} at offset {pos} does not match the Red Faction II layout ({e.Message}); it was kept as stored.");
                _sections.Add(new RfSectionRecord(id, name, pos, length, RfSectionStatus.Damaged, e.Message));
            }
        }

        private void KeepOpaque(uint id, string name, long pos, LvlCursor c, string reason)
        {
            c.Position = c.Start;
            _opaque.Add(new RfOpaqueSection(id, name, pos, c.Memory(c.Left), reason));
        }

        private (RfSectionStatus, string?) ReadSection(uint id, LvlCursor c)
        {
            const RfSectionStatus ok = RfSectionStatus.Read;
            switch (id)
            {
                case 0x100:
                    var geometry = Geometry(c);
                    c.ExpectEnd("static geometry");
                    _static = geometry;
                    return (ok, null);
                case 0x300: return AddList(c, _lights, 57, _rr.Light, "lights");
                case 0x500: return AddList(c, _sounds, 17, _rr.AmbientSound, "ambient sounds");
                case 0x600: return AddList(c, _events, 60, Event, "events");
                case 0x700: return AddList(c, _respawns, 54, _rr.RespawnPoint, "respawn points");
                case 0x900: return LevelProperties(c);
                case 0x1000: return AddList(c, _decals, 90, Decal, "decals");
                case 0x1100: return AddList(c, _push, 69, PushRegion, "push regions");
                case 0x2000: return AddList(c, _movers, 60, Brush, "movers");
                case 0x3000: return AddList(c, _movingGroups, 40, MovingGroup, "moving groups");
                case >= 0x7000 and <= 0x7005: return FileList(id, c);
                case 0x7678: return AddList(c, _coronas, 72, Corona, "coronas");
                case 0x7900: return VertexLighting(c);
                case 0x7901:
                    _section7901 = c.U8();
                    c.ExpectEnd("section 0x7901");
                    return (ok, null);
                case 0x10000: return AddList(c, _waypoints, 6, _rr.WaypointList, "waypoint lists");
                case 0x40000: return AddList(c, _items, 63, Item, "items");
                case 0x50000: return AddList(c, _clutter, 63, Clutter, "clutter");
                case 0x60000: return AddList(c, _triggers, 40, Trigger, "triggers");
                case 0x70000:
                    var start = _rr.PlayerStart(c);
                    c.ExpectEnd("player start");
                    _playerStart = start;
                    return (ok, null);
                case 0x01000000: return LevelInfo(c);
                case 0x02000000: return AddList(c, _brushes, 60, Brush, "brushes");
                default:
                    return (RfSectionStatus.Unknown, "unknown section");
            }
        }

        private static (RfSectionStatus, string?) AddList<T>(LvlCursor c, List<T> into, int minimumBytes, Func<LvlCursor, T> read, string what)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            var list = LvlRecordReader.List(c, minimumBytes, read);
            c.ExpectEnd(what);
            into.AddRange(list);
            return (RfSectionStatus.Read, null);
        }

        /// <summary>A u16-length string; REDUX's reader also takes 0xFFFF as empty, so it is accepted here.</summary>
        private static string Str(LvlCursor c)
        {
            if (c.Left >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(c.Data.AsSpan(c.Position)) == 0xFFFF)
            {
                c.Skip(2);
                return "";
            }
            return c.Str();
        }

        /// <summary>A one-byte-length string (the trigger key name).</summary>
        private static string ShortStr(LvlCursor c)
        {
            int n = c.U8();
            c.Need(n);
            string s = n == 0 ? "" : Encoding.Latin1.GetString(c.Data, c.Position, n);
            c.Skip(n);
            return s;
        }

        private LvlRecordReader.Head Head(LvlCursor c) => new(c.I32(), Str(c), c.Vec3(), c.StockMatrix(), Str(c), c.Bool());

        private static T Place<T>(T o, LvlRecordReader.Head h) where T : RfPlacedObject =>
            (T)((RfPlacedObject)o with
            {
                Uid = h.Uid,
                ClassName = h.ClassName,
                Position = h.Position,
                Orientation = h.Orientation,
                ScriptName = h.ScriptName,
                HiddenInEditor = h.Hidden,
            });

        // ---------- geometry ----------

        /// <summary>The RF2 geometry body: name, a u32, textures, rooms, sub-room lists, room links, portals, vertices, faces.</summary>
        private RfGeometry Geometry(LvlCursor c)
        {
            string name = Str(c);
            uint unknown = c.U32();
            int textureCount = c.Count(2);
            var textures = new string[textureCount];
            for (int i = 0; i < textureCount; i++) textures[i] = Str(c);
            var rooms = LvlRecordReader.List(c, 78, Room);
            var subrooms = LvlRecordReader.List(c, 8, x => new RfSubroomList(x.I32(), x.Uids()));
            int links = c.Count(8);
            var roomLinks = c.Bytes(8 * links);
            var portals = LvlRecordReader.List(c, 32, x => new RfPortal(x.I32(), x.I32(), x.Vec3(), x.Vec3()));
            int vertexCount = c.Count(12);
            var vertices = new Vector3[vertexCount];
            for (int i = 0; i < vertexCount; i++) vertices[i] = c.Vec3();
            var faces = Faces(c, vertexCount);
            return new RfGeometry
            {
                Unknown = unknown,
                Name = name,
                Textures = ImmutableCollectionsMarshal.AsImmutableArray(textures),
                Rooms = rooms,
                SubroomLists = subrooms,
                Rf2RoomLinks = roomLinks,
                Portals = portals,
                Vertices = ImmutableCollectionsMarshal.AsImmutableArray(vertices),
                Faces = ImmutableCollectionsMarshal.AsImmutableArray(faces),
            };
        }

        /// <summary>An RF2 room: id, bounds, four flag-like bytes, life, EAX effect and 40 bytes whose meaning is not known.</summary>
        private static RfRoom Room(LvlCursor c)
        {
            int id = c.I32();
            var min = c.Vec3();
            var max = c.Vec3();
            var flags = c.Bytes(4);
            float life = c.F32();
            string eax = Str(c);
            var tail = c.Bytes(40);
            return new RfRoom
            {
                Id = id,
                BoundsMin = min,
                BoundsMax = max,
                Life = life,
                EaxEffect = eax,
                Rf2Data = [.. flags, .. tail],
            };
        }

        private RfFace[] Faces(LvlCursor c, int vertexCount)
        {
            int n = c.Count(55);
            var faces = new RfFace[n];
            for (int i = 0; i < n; i++)
            {
                var plane = new RfPlane(c.Vec3(), c.F32());
                int texture = c.I32();
                int surface = c.I32();
                int faceId = c.I32();
                int unknown = c.I32();
                uint flags = c.U32();
                uint smoothing = c.U32();
                Vector2? period = (flags & Rf2InlineScrollFlag) != 0 ? c.Vec2() : null;
                var emissive = (c.U8(), c.U8(), c.U8());
                float emissiveIntensity = c.F32();
                int room = c.I32();
                int k = c.Count(16);
                var corners = new RfCorner[k];
                for (int j = 0; j < k; j++)
                {
                    int vertex = c.I32();
                    if ((uint)vertex >= (uint)vertexCount) _badCorners++;
                    var uv = c.Vec2();
                    corners[j] = new RfCorner(vertex, uv, null, c.Color());
                }
                // RF2 stores scroll as periods (seconds per cycle); RF1 as speeds. Faces that show the sky draw the
                // sky, not their texture, so their scroll is dropped as REDUX does.
                Vector2? scroll = null;
                if (period is { } p && (flags & (uint)RfFaceFlags.ShowSky) == 0)
                    scroll = new Vector2(Speed(p.X), Speed(p.Y));
                uint rf1 = flags & Rf2ToRf1FaceFlags;
                if (scroll is { } s && (s.X != 0 || s.Y != 0)) rf1 |= (uint)RfFaceFlags.ScrollTexture;
                faces[i] = new RfFace
                {
                    Plane = plane,
                    Texture = texture,
                    // RF2 lights with vertex colours: no face has a lightmap surface (the stored index is kept in Rf2).
                    Surface = -1,
                    FaceId = faceId,
                    Flags = (RfFaceFlags)rf1,
                    RawFlags = flags,
                    Smoothing = smoothing,
                    Room = room,
                    Corners = ImmutableCollectionsMarshal.AsImmutableArray(corners),
                    Scroll = scroll,
                    Rf2 = new Rf2FaceData(unknown, period, emissive, emissiveIntensity) { StoredSurface = surface },
                };
            }
            return faces;

            static float Speed(float period) => MathF.Abs(period) > 0.0001f && float.IsFinite(period) ? 1f / period : 0f;
        }

        // ---------- brushes ----------

        private RfBrush Brush(LvlCursor c)
        {
            int uid = c.I32();
            var position = c.Vec3();
            var orientation = c.StockMatrix();
            var geometry = Geometry(c);
            uint flags = c.U32();
            int life = c.I32();
            int state = c.I32();
            // Brushes with solid flags 0x04 and 0x08 together (liquid volumes, per REDUX) store 29 more bytes.
            ImmutableArray<byte> extra = (flags & 0xC) == 0xC ? c.Bytes(29) : [];
            return new RfBrush
            {
                Uid = uid,
                Position = position,
                Orientation = orientation,
                Geometry = geometry,
                SolidFlags = (RfSolidFlags)flags,
                Life = life,
                State = state,
                Rf2Extra = extra,
            };
        }

        // ---------- objects ----------

        private RfEvent Event(LvlCursor c)
        {
            var e = new RfEvent
            {
                Uid = c.I32(),
                ClassName = Str(c),
                Position = c.Vec3(),
                ScriptName = Str(c),
                HiddenInEditor = c.Bool(),
                Delay = c.F32(),
            };
            int lead1 = c.I32(), lead2 = c.I32();
            e = e with { Bool1 = c.Bool(), Bool2 = c.Bool(), Int1 = c.I32(), Int2 = c.I32() };
            int int3 = c.I32(), int4 = c.I32();
            e = e with { Float1 = c.F32(), Float2 = c.F32() };
            int tail = c.I32();
            e = e with { Str1 = Str(c), Str2 = Str(c) };
            string str3 = Str(c);
            e = e with { Links = c.Uids() };
            if (e.ClassName is "Play_Explosion" or "Teleport") e = e with { Orientation = c.StockMatrix() };
            return e with { Color = c.Color(), Rf2Data = new Rf2EventData(lead1, lead2, int3, int4, tail, str3) };
        }

        private RfDecal Decal(LvlCursor c)
        {
            var h = Head(c);
            var d = new RfDecal
            {
                Extents = c.Vec3(),
                Texture = Str(c),
                Alpha = c.I32(),
                SelfIlluminated = c.Bool(),
                TilingRaw = c.I32(),
                Scale = c.F32(),
                Rf2Data = new Rf2DecalData(c.F32(), c.I32(), c.I32()),
            };
            return Place(d, h);
        }

        /// <summary>A push region: shape 0 or 1 stores one float (a radius), the box shapes three.</summary>
        private RfPushRegion PushRegion(LvlCursor c)
        {
            var h = Head(c);
            int shape = c.I32();
            float radius = shape is 0 or 1 ? c.F32() : 0f;
            var extents = shape is 0 or 1 ? default : c.Vec3();
            return Place(new RfPushRegion { Shape = shape, Radius = radius, Extents = extents, Strength = c.F32(), Packed = c.U32() }, h);
        }

        private RfItem Item(LvlCursor c)
        {
            var h = Head(c);
            return Place(new RfItem { Count = c.I32(), RespawnTime = c.I32(), Team = c.I32(), Rf2Extra = c.Bytes(6) }, h);
        }

        private RfClutter Clutter(LvlCursor c)
        {
            var h = Head(c);
            return Place(new RfClutter { Skin = Str(c), Links = c.Uids() }, h);
        }

        private RfTrigger Trigger(LvlCursor c)
        {
            var t = new RfTrigger
            {
                Uid = c.I32(),
                ScriptName = Str(c),
                HiddenInEditor = c.Bool(),
                Shape = c.I32(),
                ResetsAfter = c.F32(),
                ResetsTimes = c.I32(),
                UseKey = c.Bool(),
                KeyName = ShortStr(c),
                ActivatedBy = c.U8(),
                Position = c.Vec3(),
            };
            t = t.Shape == 0 ? t with { Radius = c.F32() } : t with { Orientation = c.StockMatrix(), Size = c.Vec3() };
            t = t with
            {
                AirlockRoomUid = c.I32(),
                AttachedToUid = c.I32(),
                UseClutterUid = c.I32(),
                Disabled = c.Bool(),
                ButtonActiveTime = c.F32(),
            };
            float unknown = c.F32();
            byte b = c.U8();
            ushort flags = c.U16();
            uint flags2 = c.U32();
            // REDUX reads flags2 bit 0x8 as "auto".
            return t with { IsAuto = (flags2 & 0x8) != 0, Links = c.Uids(), Rf2Data = new Rf2TriggerData(unknown, b, flags, flags2) };
        }

        private RfCorona Corona(LvlCursor c)
        {
            int uid = c.I32();
            string cls = Str(c);
            var position = c.Vec3();
            // RF2 stores the rows forward, up, right (checked by REDUX on ceiling coronas pointing down).
            var f = c.Vec3();
            var u = c.Vec3();
            var r = c.Vec3();
            string script = Str(c);
            bool hidden = c.Bool();
            var color = c.Color();
            if (color.R == 0xCD && color.G == 0xCD && color.B == 0xCD) _unsetCoronaColors++;
            var flags = c.Bytes(5);
            var k = new RfCorona
            {
                Uid = uid,
                ClassName = cls,
                Position = position,
                Orientation = new RfMatrix(r, u, f),
                ScriptName = script,
                HiddenInEditor = hidden,
                Color = color,
                CoronaBitmap = Str(c),
            };
            // A corona without a bitmap stores none of its five glare values (REDUX always reads them).
            if (k.CoronaBitmap.Length > 0)
                k = k with { ConeAngle = c.F32(), Intensity = c.F32(), RadiusDistance = c.F32(), RadiusScale = c.F32(), DiminishDistance = c.F32() };
            k = k with { VolumetricBitmap = Str(c) };
            ImmutableArray<byte> volumetricExtra = [];
            if (k.VolumetricBitmap.Length > 0)
            {
                k = k with { VolumetricHeight = c.F32(), VolumetricLength = c.F32() };
                volumetricExtra = c.Bytes(4);
            }
            var tail = c.Bytes(5);
            // Rf2Data: the five flag bytes after the colour, the five bytes at the end, then the third volumetric float (when stored).
            return k with { Rf2Data = [.. flags, .. tail, .. volumetricExtra] };
        }

        private RfLevelGroup MovingGroup(LvlCursor c)
        {
            string name = Str(c);
            byte unknown = c.U8();
            var flags = c.Bytes(4);
            bool moving = c.Bool();
            // The moving block is stored even for a group that does not move.
            var keyframes = LvlRecordReader.List(c, 30, _rr.Keyframe);
            var members = LvlRecordReader.List(c, 52, x => new RfMemberTransform(x.I32(), x.Vec3(), x.StockMatrix()));
            var m = new RfMovingData
            {
                Keyframes = keyframes,
                MemberTransforms = members,
                IsDoor = c.Bool(),
                RotateInPlace = c.Bool(),
                StartsBackwards = c.Bool(),
                UseTravelTimeAsSpeed = c.Bool(),
                ForceOrient = c.Bool(),
                NoPlayerCollide = c.Bool(),
            };
            byte movingByte = c.U8();
            m = m with { MovementType = c.I32(), StartingKeyframe = c.I32() };
            m = m with { StartSound = Sound(c), LoopingSound = Sound(c), StopSound = Sound(c), CloseSound = Sound(c) };
            int beforeLists = c.I32();
            var objects = c.Uids();
            var brushes = c.Uids();
            int parent = c.I32();
            Vector3? parentPosition = null;
            RfMatrix? parentOrientation = null;
            if (parent != -1)
            {
                parentPosition = c.Vec3();
                parentOrientation = c.StockMatrix();
            }
            c.Need(36);
            var tail = new int[9];
            for (int i = 0; i < tail.Length; i++) tail[i] = c.I32();
            return new RfLevelGroup
            {
                Name = name,
                Unknown = unknown,
                Moving = moving ? m : null,
                ObjectUids = objects,
                BrushUids = brushes,
                Rf2Data = new Rf2GroupData(flags, movingByte, beforeLists, parent, parentPosition, parentOrientation,
                    ImmutableCollectionsMarshal.AsImmutableArray(tail)),
            };

            static RfSound Sound(LvlCursor c) => new(Str(c), c.F32());
        }

        // ---------- level-wide sections ----------

        private (RfSectionStatus, string?) LevelProperties(LvlCursor c)
        {
            var p = new RfLevelProperties
            {
                GeomodTexture = Str(c),
                Hardness = c.I32(),
                AmbientColor = c.Color(),
                DirectionalAmbient = c.U8(),
                FogColor = c.Color(),
                FogNear = c.F32(),
                FogFar = c.F32(),
            };
            int tailStart = c.Position;
            var rf2 = new Rf2LevelProperties
            {
                SunColor = c.Color(),
                SunYaw = c.F32(),
                SunPitch = c.F32(),
                SunIntensity = c.F32(),
                SunSpread = c.F32(),
                HardlightColor = c.Color(),
                HardlightIntensity = c.I32(),
                LightmapMultiplier = c.F32(),
            };
            c.ExpectEnd("level properties");
            _props = p with { Rf2Data = ImmutableArray.Create(_data, tailStart, c.Position - tailStart) };
            _rf2Props = rf2;
            return (RfSectionStatus.Read, null);
        }

        /// <summary>The RF2 resource lists: names, then (except for bitmaps) one reference count per name.</summary>
        private (RfSectionStatus, string?) FileList(uint id, LvlCursor c)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            int n = c.Count(2);
            var names = new string[n];
            int extraNames = 0;
            for (int i = 0; i < n; i++)
            {
                names[i] = Str(c);
                // Character meshes are stored as pairs of names; the second has always been empty.
                if (id == 0x7001 && Str(c).Length > 0) extraNames++;
            }
            ImmutableArray<int> counts = [];
            if (id != 0x7000)
            {
                c.Need(4L * n);
                var a = new int[n];
                for (int i = 0; i < n; i++) a[i] = c.I32();
                counts = ImmutableCollectionsMarshal.AsImmutableArray(a);
            }
            c.ExpectEnd("resource list");
            _preloads.Add(new RfFileList(id, ImmutableCollectionsMarshal.AsImmutableArray(names), counts));
            return (RfSectionStatus.Read, extraNames > 0 ? $"{extraNames} second names were not kept" : null);
        }

        private const int TrailingLevelInfoBytes = 2080;

        private (RfSectionStatus, string?) LevelInfo(LvlCursor c)
        {
            _info = new RfLevelInfo(c.I32(), Str(c), Str(c), Str(c), c.Bool(), c.Bool());
            for (int i = 0; i < 4; i++)
            {
                if (c.I32() != 0) c.F32();
                c.Vec3();
                c.StockMatrix();
            }
            // Every known RF2 level ends this section with 2,080 more bytes, mostly 0x00 and 0xCD filler; they are not read.
            int rest = c.Left;
            c.Skip(rest);
            return (RfSectionStatus.Read, rest != TrailingLevelInfoBytes ? $"{rest} bytes after the editor views were not read" : null);
        }

        /// <summary>The vertex lighting data (0x7900), version 5.</summary>
        private (RfSectionStatus, string?) VertexLighting(LvlCursor c)
        {
            int version = c.I32();
            if (version != 5) throw new LvlBodyException($"vertex lighting version {version} is not known (5 is)");
            int nameCount = c.Count(1);
            int groupCount = c.Count(4);
            var names = new string[nameCount];
            for (int i = 0; i < nameCount; i++)
            {
                int end = Array.IndexOf(c.Data, (byte)0, c.Position, c.Left);
                if (end < 0) throw new LvlBodyException($"texture name {i} has no terminator");
                names[i] = Encoding.Latin1.GetString(c.Data, c.Position, end - c.Position);
                c.Skip(end - c.Position + 1);
            }
            int probes = c.Count(8);
            c.Skip(8L * probes);
            c.Need(4L * groupCount);
            c.Skip(4L * groupCount); // the groups' lightmap ids
            var entries = new List<Rf2LitEntry>();
            for (int g = 0; g < groupCount; g++)
            {
                int n = c.Count(44);
                for (int e = 0; e < n; e++)
                {
                    int vertexCount = c.Count(12);
                    int triangleCount = c.I32();
                    int texture = c.I32();
                    c.Skip(8);
                    var vertices = new Vector3[vertexCount];
                    for (int v = 0; v < vertexCount; v++) vertices[v] = c.Vec3();
                    c.Skip(24); // bounding box
                    if (triangleCount < 0 || (long)triangleCount * 88 > c.Left)
                        throw new LvlBodyException($"triangle count {triangleCount} does not fit in the {c.Left} bytes left");
                    var triangles = new Rf2LitTriangle[triangleCount];
                    for (int t = 0; t < triangleCount; t++)
                    {
                        int a = c.I32(), b = c.I32(), cc = c.I32();
                        var rgb = new byte[9];
                        for (int k = 0; k < 9; k++) rgb[k] = (byte)Math.Clamp(c.I32(), 0, 255);
                        c.Skip(24); // per-corner texture coordinates
                        byte alpha = (byte)Math.Clamp(c.I32(), 0, 255);
                        c.Skip(12);
                        triangles[t] = new Rf2LitTriangle(a, b, cc,
                            new RflColor(rgb[0], rgb[1], rgb[2], alpha), new RflColor(rgb[3], rgb[4], rgb[5], alpha), new RflColor(rgb[6], rgb[7], rgb[8], alpha));
                    }
                    entries.Add(new Rf2LitEntry
                    {
                        TextureIndex = texture,
                        Vertices = ImmutableCollectionsMarshal.AsImmutableArray(vertices),
                        Triangles = ImmutableCollectionsMarshal.AsImmutableArray(triangles),
                    });
                }
            }
            c.ExpectEnd("vertex lighting");
            _vertexLighting = new Rf2VertexLighting
            {
                Version = version,
                TextureNames = ImmutableCollectionsMarshal.AsImmutableArray(names),
                ProbeCount = probes,
                Entries = [.. entries],
            };
            return (RfSectionStatus.Read, null);
        }

        private RfLevel Build() => new()
        {
            Name = context.Name,
            Header = context.Header,
            Rf2Layout = true,
            Info = _info,
            Properties = _props,
            StaticGeometry = _static,
            Brushes = [.. _brushes],
            Movers = [.. _movers],
            MovingGroups = [.. _movingGroups],
            Objects = new RfObjectSet
            {
                Lights = [.. _lights],
                AmbientSounds = [.. _sounds],
                Events = [.. _events],
                RespawnPoints = [.. _respawns],
                Items = [.. _items],
                Clutter = [.. _clutter],
                Triggers = [.. _triggers],
                Decals = [.. _decals],
                PushRegions = [.. _push],
                Coronas = [.. _coronas],
            },
            WaypointLists = [.. _waypoints],
            PlayerStart = _playerStart,
            Preloads = [.. _preloads],
            Rf2 = new Rf2LevelData { Properties = _rf2Props, VertexLighting = _vertexLighting, Section7901 = _section7901 },
            OpaqueSections = [.. _opaque],
            Sections = [.. _sections],
            Problems = [.. _problems],
            IsTruncated = _truncated,
        };
    }
}
