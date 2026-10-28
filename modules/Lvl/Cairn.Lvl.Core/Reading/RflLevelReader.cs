using System.Buffers.Binary;
using Cairn.Formats;
using Cairn.Formats.Rfl;
using Cairn.Lvl.Model;

namespace Cairn.Lvl.Reading;

/// <summary>
/// Reads a whole Red Faction level (.rfl) into an <see cref="RfLevel"/>: compiled geometry, brushes, movers, groups
/// and every object, for RF1 (0x4F up to 0xC8) and Alpine Faction (300 and newer; versions past 306 are read with
/// 306's rules). The section stream is walked (the header's offsets are not trusted), every section is read inside
/// its own bounds and skipped by its stated length, and a section that does not match its layout is kept as bytes
/// and reported, so it costs only itself. Only a file that is not a level, has an unsupported version or a cut
/// header throws, and it throws <see cref="AssetFormatException"/>.
/// </summary>
/// <remarks>
/// Red Faction II levels (version 0x127) are handed to <c>ReadRf2</c>, a partial method implemented beside this
/// file by the RF2 reader; without it they are refused with a clear message.
/// </remarks>
public static partial class RflLevelReader
{
    /// <summary>The oldest version the reader accepts; no known level or group is older.</summary>
    public const int OldestVersion = 0x4F;

    /// <summary>The Red Faction II level version.</summary>
    public const int Rf2Version = 0x127;

    /// <summary>The first section id of every RF2 level (its bitmap list).</summary>
    private const uint Rf2FirstSection = 0x7000;

    /// <summary>Reads a level file.</summary>
    /// <exception cref="AssetFormatException">Not a level, unsupported version, or a cut header.</exception>
    public static RfLevel ReadFile(string path, RfReadOptions? options = null) =>
        Read(LvlFiles.ReadAll(path), Path.GetFileName(path), options);

    /// <summary>Reads a level held in memory. The bytes must not change while the result is in use (lightmaps and opaque sections refer to them).</summary>
    /// <exception cref="AssetFormatException">Not a level, unsupported version, or a cut header.</exception>
    public static RfLevel Read(byte[] data, string name, RfReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= RfReadOptions.Default;
        var (header, start, rf2) = ReadHeader(data, name);
        if (rf2)
        {
            RfLevel? level = null;
            ReadRf2(new Rf2ReadContext(data, name, options, header, start), ref level);
            return level ?? throw new AssetFormatException($"'{name}' is a Red Faction II level, which this version of Cairn cannot read.");
        }
        return new Walker(data, name, options, header).Run(start);
    }

    /// <summary>What the RF2 reader receives: the file, its header (read without a mod name) and where the sections start.</summary>
    internal sealed record Rf2ReadContext(byte[] Data, string Name, RfReadOptions Options, RfLevelHeader Header, int SectionsStart);

    /// <summary>Implemented by the RF2 reader; leaves <paramref name="level"/> null when it is not present.</summary>
    static partial void ReadRf2(Rf2ReadContext context, ref RfLevel? level);

    private static (RfLevelHeader Header, int Start, bool Rf2) ReadHeader(byte[] data, string name)
    {
        if (data.Length == 0) throw new AssetFormatException($"'{name}' is empty.");
        if (data.Length < 8) throw new AssetFormatException($"'{name}' is too short to be a level ({data.Length} bytes).");
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != RflReader.Magic) throw new AssetFormatException($"'{name}' is not a Red Faction level (signature 0x{magic:X8}).");
        int v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (v < OldestVersion)
            throw new AssetFormatException($"'{name}' has level version {v}, older than any level Cairn reads (the oldest is {OldestVersion}).");
        if (v > 0xC8 && v < 300 && v != Rf2Version)
            throw new AssetFormatException($"'{name}' has level version {v}, which neither Red Faction nor Alpine Faction uses.");

        var c = new LvlCursor(data, 8, data.Length);
        try
        {
            uint? timestamp = v >= 0x72 ? c.U32() : null;
            int playerStart = c.I32();
            int levelInfo = c.I32();
            int? sections = v >= 0xA0 ? c.I32() : null;
            int? total = v >= 0xA0 ? c.I32() : null;
            string levelName = v >= 0xAA ? c.Str() : "";
            bool rf2 = false;
            string? mod = null;
            if (v == Rf2Version && c.Left >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Position)) == Rf2FirstSection)
                rf2 = true; // real RF2 levels have no mod name
            else if (v >= 0xB2)
                mod = c.Str();
            var header = new RfLevelHeader
            {
                Version = v,
                Timestamp = timestamp,
                PlayerStartOffset = playerStart,
                LevelInfoOffset = levelInfo,
                SectionCount = sections,
                SectionsTotalSize = total,
                LevelName = levelName,
                ModName = mod,
            };
            return (header, c.Position, rf2);
        }
        catch (LvlBodyException e)
        {
            throw new AssetFormatException($"'{name}' is damaged: the header ends early ({e.Message}).");
        }
    }

    /// <summary>A section's display name.</summary>
    public static string SectionName(uint id) => RflReader.SectionName(id);

    /// <summary>Sections kept as bytes by design, with the reason shown for them.</summary>
    private static string? OpaqueReason(uint id) => id switch
    {
        0x800 => "PS2 data is kept as stored",
        0xAFBA5ED1 => "pre-release Alpine level properties are kept as stored",
        0x0AFBAE02 => "Alpine editor notes are kept as stored",
        0x0AFBAE04 => "Alpine bags are kept as stored",
        0x0AFBAE05 => "Alpine group metadata is kept as stored",
        0x0AFBAE06 => "Alpine weather regions are kept as stored",
        0x0AFBAE07 => "Alpine vehicle factories are kept as stored",
        0x0AFBAE08 => "Alpine projection cameras are kept as stored",
        0x0AFBAE09 => "Alpine lightmaps are kept as stored",
        0x0AFBAE0A => "Alpine rope emitters are kept as stored",
        0x0AFBAE0B => "Alpine terrain is kept as stored",
        0x0AFBAE0C => "Alpine directional lights are kept as stored",
        _ when (id & 0xFFFF0000) == 0x5ED00000 => "RED+ editor data is kept as stored",
        _ when (id & 0xFFFF0000) == 0x6ED00000 => "Glacier editor data is kept as stored",
        _ => null,
    };

    /// <summary>Walks the section stream and collects the level.</summary>
    private sealed class Walker(byte[] data, string name, RfReadOptions options, RfLevelHeader header)
    {
        private readonly int _v = header.Version;
        private readonly LvlRules _rules = LvlRules.For(header.Version, groupFile: false);
        private readonly List<string> _problems = [];
        private readonly List<RfSectionRecord> _sections = [];
        private readonly List<RfOpaqueSection> _opaque = [];
        private bool _truncated;
        private int _badCorners;

        // Collected sections (lists concatenate should an id ever repeat).
        private RfGeometry _static = RfGeometry.Empty;
        private RfLevelInfo? _info;
        private RfLevelProperties? _props;
        private RfPlayerStart? _playerStart;
        private RfAlpineLevelProperties? _alpine;
        private RfDashProperties? _dash;
        private readonly List<RfBrush> _brushes = [], _movers = [];
        private readonly List<RfLevelGroup> _groups = [], _movingGroups = [];
        private readonly List<RfLight> _editorLights = [];
        private readonly List<RfCutscene> _cutscenes = [];
        private readonly List<RfNamedUidList> _cutscenePaths = [];
        private readonly List<RfWaypointList> _waypoints = [];
        private readonly List<RfFileList> _preloads = [];
        private readonly List<RfLightmap> _lightmaps = [];
        private readonly List<RfGeoRegion> _geo = [];
        private readonly List<RfLight> _lights = [];
        private readonly List<RfCutsceneCamera> _cameras = [];
        private readonly List<RfCutscenePathNode> _pathNodes = [];
        private readonly List<RfAmbientSound> _sounds = [];
        private readonly List<RfEvent> _events = [];
        private readonly List<RfRespawnPoint> _respawns = [];
        private readonly List<RfNavPoint> _nav = [];
        private readonly List<RfEntity> _entities = [];
        private readonly List<RfItem> _items = [];
        private readonly List<RfClutter> _clutter = [];
        private readonly List<RfTrigger> _triggers = [];
        private readonly List<RfParticleEmitter> _particles = [];
        private readonly List<RfGasRegion> _gas = [];
        private readonly List<RfDecal> _decals = [];
        private readonly List<RfClimbingRegion> _climbing = [];
        private readonly List<RfRoomEffect> _roomEffects = [];
        private readonly List<RfEaxEffect> _eax = [];
        private readonly List<RfBoltEmitter> _bolts = [];
        private readonly List<RfTarget> _targets = [];
        private readonly List<RfPushRegion> _push = [];
        private readonly List<RfAlpineMesh> _meshes = [];
        private readonly List<RfCorona> _coronas = [];

        public RfLevel Run(int start)
        {
            if (_v > RflReader.NewestKnownVersion)
                _problems.Add($"Level version {_v} is newer than Cairn knows; it was read with the rules of version {RflReader.NewestKnownVersion}.");
            if (_v == Rf2Version)
                _problems.Add("The header says Red Faction II (295), but the file is laid out as a Red Faction level; it was read as one.");

            long pos = start;
            while (true)
            {
                long left = data.Length - pos;
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
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)pos));
                int length = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan((int)pos + 4));
                if (id == 0) break; // end marker; its length and anything after it are ignored
                if (length < 0 || length > left - 8)
                {
                    _sections.Add(new RfSectionRecord(id, SectionName(id), pos, length, RfSectionStatus.Truncated,
                        $"claims {length} bytes, {left - 8} remain"));
                    Truncated($"{SectionName(id)} at offset {pos} claims {length} bytes but only {left - 8} remain; reading stopped there.");
                    break;
                }
                Section(id, pos, new LvlCursor(data, (int)pos + 8, (int)pos + 8 + length));
                pos += 8L + length;
            }

            if (_badCorners > 0)
                _problems.Add($"{_badCorners} face corners name a vertex that does not exist; those faces cannot be drawn.");
            return Build();
        }

        private void Truncated(string message)
        {
            _truncated = true;
            _problems.Add(message);
        }

        private void Section(uint id, long pos, LvlCursor c)
        {
            string secName = SectionName(id);
            int length = c.Left;
            if (OpaqueReason(id) is { } reason)
            {
                KeepOpaque(id, secName, pos, c, reason);
                _sections.Add(new RfSectionRecord(id, secName, pos, length, RfSectionStatus.Opaque, reason));
                return;
            }
            var rr = new LvlRecordReader(_rules);
            try
            {
                var (status, detail) = ReadSection(id, c, rr);
                _badCorners += rr.BadCorners;
                if (status == RfSectionStatus.Unknown)
                {
                    c.Position = c.Start;
                    KeepOpaque(id, secName, pos, c, "unknown section");
                    _problems.Add($"Unknown section 0x{id:X8} ({length} bytes) at offset {pos} was kept as stored.");
                }
                _sections.Add(new RfSectionRecord(id, secName, pos, length, status, detail));
            }
            catch (LvlBodyException e)
            {
                c.Position = c.Start;
                string count = "";
                if (length >= 4 && id is not 0x100 and not 0x900 and not 0x70000 and not 0x01000000 and not 0x0AFBA5ED and not 0xDA58FA00)
                {
                    uint n = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(c.Start));
                    if (n <= (uint)length) count = $" It holds {n} records.";
                }
                KeepOpaque(id, secName, pos, c, e.Message);
                _problems.Add($"{secName} at offset {pos} does not match the expected layout ({e.Message}); it was kept as stored.{count}");
                _sections.Add(new RfSectionRecord(id, secName, pos, length, RfSectionStatus.Damaged, e.Message));
            }
        }

        private void KeepOpaque(uint id, string secName, long pos, LvlCursor c, string reason)
        {
            c.Position = c.Start;
            _opaque.Add(new RfOpaqueSection(id, secName, pos, c.Memory(c.Left), reason));
        }

        /// <summary>Reads one section; every list must end exactly at the section's end, or the layout was wrong.</summary>
        private (RfSectionStatus, string?) ReadSection(uint id, LvlCursor c, LvlRecordReader rr)
        {
            const RfSectionStatus ok = RfSectionStatus.Read;
            switch (id)
            {
                case 0x100:
                    var geometry = rr.Geometry(c);
                    c.ExpectEnd("static geometry");
                    _static = geometry;
                    return (ok, null);
                case 0x200: return AddList(c, _geo, 8, rr.GeoRegion, "geo regions");
                case 0x300: return AddList(c, _lights, 57, rr.Light, "lights");
                case 0x400: return AddList(c, _cameras, 57, rr.CutsceneCamera, "cutscene cameras");
                case 0x500: return AddList(c, _sounds, 17, rr.AmbientSound, "ambient sounds");
                case 0x600: return Events(c, rr);
                case 0x700: return AddList(c, _respawns, 54, rr.RespawnPoint, "respawn points");
                case 0x900: return LevelProperties(c);
                case 0xA00: return AddList(c, _particles, 57, rr.ParticleEmitter, "particle emitters");
                case 0xB00: return AddList(c, _gas, 57, rr.GasRegion, "gas regions");
                case 0xC00: return AddList(c, _roomEffects, 57, rr.RoomEffect, "room effects");
                case 0xD00: return AddList(c, _climbing, 57, rr.ClimbingRegion, "climbing regions");
                case 0xE00: return AddList(c, _bolts, 57, rr.BoltEmitter, "bolt emitters");
                case 0xF00: return AddList(c, _targets, 57, rr.Target, "targets");
                case 0x1000: return AddList(c, _decals, 57, rr.Decal, "decals");
                case 0x1100: return AddList(c, _push, 57, rr.PushRegion, "push regions");
                case 0x1200: return Lightmaps(c);
                case 0x2000: return AddList(c, _movers, 60, rr.Brush, "movers");
                case 0x3000: return AddList(c, _movingGroups, 11, rr.LevelGroup, "moving groups");
                case 0x4000: return AddList(c, _cutscenes, 13, rr.Cutscene, "cutscenes");
                case 0x5000: return AddList(c, _pathNodes, 57, rr.CutscenePathNode, "cutscene path nodes");
                case 0x6000: return AddList(c, _cutscenePaths, 6, LvlRecordReader.NamedUidList, "cutscene paths");
                case 0x7000 or 0x7001 or 0x7002 or 0x7003 or 0x7004: return FileList(id, c);
                case 0x8000: return AddList(c, _eax, 59, rr.EaxEffect, "EAX effects");
                case 0x10000: return AddList(c, _waypoints, 6, rr.WaypointList, "waypoint lists");
                case 0x20000: return NavPoints(c, rr);
                case 0x30000: return AddList(c, _entities, 57, rr.Entity, "entities");
                case 0x40000: return AddList(c, _items, 69, rr.Item, "items");
                case 0x50000: return AddList(c, _clutter, 57, rr.Clutter, "clutter");
                case 0x60000: return AddList(c, _triggers, 30, rr.Trigger, "triggers");
                case 0x70000:
                    var start = rr.PlayerStart(c);
                    c.ExpectEnd("player start");
                    _playerStart = start;
                    return (ok, null);
                case 0x01000000: return LevelInfo(c);
                case 0x02000000: return AddList(c, _brushes, 60, rr.Brush, "brushes");
                case 0x03000000: return AddList(c, _groups, 11, rr.LevelGroup, "groups");
                case 0x04000000: return AddList(c, _editorLights, 57, rr.Light, "editor-only lights");
                case 0x0AFBA5ED: return AlpineProperties(c);
                case 0x0AFBAE01: return AlpineMeshes(c, rr);
                case 0x0AFBAE03:
                    var coronas = rr.Coronas(c);
                    c.ExpectEnd("coronas");
                    _coronas.AddRange(coronas);
                    return (ok, null);
                case 0xDA58FA00:
                    uint dashVersion = c.U32();
                    bool? fullDepth = dashVersion >= 1 && c.Left > 0 ? c.Bool() : null;
                    _dash = new RfDashProperties(dashVersion, fullDepth);
                    return (ok, c.Left > 0 ? $"{c.Left} bytes after the known fields" : null);
                default:
                    return (RfSectionStatus.Unknown, "unknown section");
            }
        }

        private static (RfSectionStatus, string?) AddList<T>(LvlCursor c, List<T> into, int minimumBytes, Func<LvlCursor, T> read, string what)
        {
            // An empty body where a count was expected is seen in the wild: treat it as an empty list.
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            var list = LvlRecordReader.List(c, minimumBytes, read);
            c.ExpectEnd(what);
            into.AddRange(list);
            return (RfSectionStatus.Read, null);
        }

        private (RfSectionStatus, string?) Events(LvlCursor c, LvlRecordReader rr)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            int start = c.Position;
            try
            {
                var list = LvlRecordReader.List(c, 30, rr.Event);
                c.ExpectEnd("events");
                _events.AddRange(list);
                return (RfSectionStatus.Read, null);
            }
            catch (LvlBodyException)
            {
                // Pre-release Alpine files store the Alpine teleport events without their matrix; Alpine RED saving
                // a stock version stores it anyway. Retry with the other rule.
                c.Position = start;
                var other = new LvlRecordReader(_rules with { AlpineEventMatrices = !_rules.AlpineEventMatrices });
                var list = LvlRecordReader.List(c, 30, other.Event);
                c.ExpectEnd("events");
                _events.AddRange(list);
                return (RfSectionStatus.ReadWithFallback, _rules.AlpineEventMatrices
                    ? "Alpine events stored without their orientation (pre-release Alpine)"
                    : "Alpine events stored with their orientation under a stock version");
            }
        }

        private (RfSectionStatus, string?) LevelProperties(LvlCursor c)
        {
            var p = new RfLevelProperties
            {
                GeomodTexture = c.Str(),
                Hardness = c.I32(),
                AmbientColor = c.Color(),
                DirectionalAmbient = c.U8(),
            };
            if (_v >= 0x87) p = p with { FogColor = c.Color(), FogNear = c.F32(), FogFar = c.F32() };
            c.ExpectEnd("level properties");
            _props = p;
            return (RfSectionStatus.Read, null);
        }

        private (RfSectionStatus, string?) Lightmaps(LvlCursor c)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            int n = c.Count(8);
            var list = new List<RfLightmap>(n);
            for (int i = 0; i < n; i++)
            {
                int w = c.I32(), h = c.I32();
                if (w < 0 || h < 0) throw new LvlBodyException($"lightmap {i} is {w} x {h}");
                long bytes = (long)w * h * 3;
                c.Need(bytes);
                ReadOnlyMemory<byte> pixels = options.IncludeLightmapPixels ? c.Memory((int)bytes) : default;
                if (!options.IncludeLightmapPixels) c.Skip(bytes);
                list.Add(new RfLightmap(w, h, pixels));
            }
            c.ExpectEnd("lightmaps");
            _lightmaps.AddRange(list);
            return (RfSectionStatus.Read, null);
        }

        private (RfSectionStatus, string?) FileList(uint id, LvlCursor c)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            int n = c.Count(2);
            var names = new string[n];
            for (int i = 0; i < n; i++) names[i] = c.Str();
            ImmutableArray<int> counts = [];
            if (id != 0x7000)
            {
                c.Need(4L * n);
                var a = new int[n];
                for (int i = 0; i < n; i++) a[i] = c.I32();
                counts = [.. a];
            }
            c.ExpectEnd("file list");
            _preloads.Add(new RfFileList(id, [.. names], counts));
            return (RfSectionStatus.Read, null);
        }

        private (RfSectionStatus, string?) NavPoints(LvlCursor c, LvlRecordReader rr)
        {
            if (c.Left == 0) return (RfSectionStatus.Read, "empty");
            var points = LvlRecordReader.List(c, 30, rr.NavPoint).ToBuilder();
            // The connections of every nav point follow all the nav points.
            for (int i = 0; i < points.Count; i++)
            {
                int k = c.U8();
                c.Need(4L * k);
                var a = new int[k];
                for (int j = 0; j < k; j++) a[j] = c.I32();
                points[i] = points[i] with { Connections = [.. a] };
            }
            c.ExpectEnd("nav points");
            _nav.AddRange(points);
            return (RfSectionStatus.Read, null);
        }

        private (RfSectionStatus, string?) LevelInfo(LvlCursor c)
        {
            _info = new RfLevelInfo(c.I32(), c.Str(), c.Str(), c.Str(), c.Bool(), c.Bool());
            // The four editor views follow. They are not needed and one tampered file has garbage there, so a
            // mismatch is only noted.
            try
            {
                for (int i = 0; i < 4; i++)
                {
                    if (c.I32() != 0) c.F32();
                    c.Vec3();
                    c.StockMatrix();
                }
                if (c.Left == 0) return (RfSectionStatus.Read, null);
            }
            catch (LvlBodyException)
            {
            }
            return (RfSectionStatus.Read, "the editor views after the level info were not read");
        }

        private (RfSectionStatus, string?) AlpineProperties(LvlCursor c)
        {
            // Alpine appended fields without raising the chunk version, so read field by field and keep whatever
            // was there when the body runs out.
            if (c.Left < 4)
            {
                _alpine = new RfAlpineLevelProperties { EndedEarly = true, UnreadBytes = c.Left };
                return (RfSectionStatus.Read, "ends before the chunk version (an early Alpine build)");
            }
            var a = new RfAlpineLevelProperties { ChunkVersion = c.U32() };
            uint ver = a.ChunkVersion;
            bool early = true;
            try
            {
                if (ver >= 1) a = a with { LegacyCyclicTimers = c.Bool() };
                if (ver >= 2)
                {
                    a = a with { LegacyMovers = c.Bool() };
                    a = a with { StartsWithHeadlamp = c.Bool() };
                }
                if (ver >= 3)
                {
                    a = a with { OverrideStaticMeshAmbient = c.Bool() };
                    a = a with { StaticMeshAmbientModifier = c.F32() };
                }
                if (ver >= 4)
                {
                    a = a with { Rf2StyleGeomod = c.Bool() };
                    int n = c.CountU32(8);
                    var geo = new RfAlpineGeoableBrush[n];
                    for (int i = 0; i < n; i++) geo[i] = new RfAlpineGeoableBrush(c.I32(), c.I32());
                    a = a with { GeoableBrushes = [.. geo] };
                    n = c.CountU32(9);
                    var breakable = new RfAlpineBreakableBrush[n];
                    for (int i = 0; i < n; i++) breakable[i] = new RfAlpineBreakableBrush(c.I32(), c.I32(), c.U8());
                    a = a with { BreakableBrushes = [.. breakable] };
                    a = a with { HoldOpenKeyframes = U32Uids(c) };
                }
                if (ver >= 5)
                {
                    a = a with { SunEnabled = c.Bool() };
                    a = a with { SunYaw = c.F32() };
                    a = a with { SunPitch = c.F32() };
                    a = a with { SunColor = c.Color() };
                    a = a with { SunIntensity = c.F32() };
                    a = a with { SunSpread = c.F32() };
                    a = a with { SunOptions = c.Bytes(9) };
                    a = a with { NoShadowBrushes = U32Uids(c) };
                    a = a with { MeshesOcclude = c.Bool() };
                    a = a with { LightmapDensity = c.U8() };
                    a = a with { LightmapFlags = c.U8() };
                    a = a with { LightmapCompression = c.U8() };
                }
                if (ver >= 6)
                {
                    a = a with { FlightCeilingEnabled = c.Bool() };
                    a = a with { FlightCeilingY = c.F32() };
                    a = a with { MinimapEnabled = c.Bool() };
                    a = a with { MinimapBitmap = c.Str() };
                    a = a with { MinimapMin = c.Vec3() };
                    a = a with { MinimapMax = c.Vec3() };
                    a = a with { MinimapCutHeight = c.F32() };
                }
                early = false;
            }
            catch (LvlBodyException)
            {
            }
            _alpine = a with { EndedEarly = early, UnreadBytes = c.Left };
            if (early) return (RfSectionStatus.Read, $"ends before the fields of chunk version {ver} (older Alpine builds)");
            return (RfSectionStatus.Read, c.Left > 0 ? $"{c.Left} bytes after the fields of chunk version {Math.Min(ver, 6)}" : null);
        }

        private static ImmutableArray<int> U32Uids(LvlCursor c)
        {
            int n = c.CountU32(4);
            var a = new int[n];
            for (int i = 0; i < n; i++) a[i] = c.I32();
            return [.. a];
        }

        private (RfSectionStatus, string?) AlpineMeshes(LvlCursor c, LvlRecordReader rr)
        {
            var result = rr.AlpineMeshes(c);
            if (result is null) throw new LvlBodyException("no known Alpine mesh layout fits");
            _meshes.AddRange(result.Value.Meshes);
            return result.Value.Layout == RfAlpineMeshLayout.Full
                ? (RfSectionStatus.Read, null)
                : (RfSectionStatus.ReadWithFallback, $"older Alpine mesh layout ({result.Value.Layout})");
        }

        private RfLevel Build()
        {
            var brushes = ApplyAlpineBrushData(_brushes);
            return new RfLevel
            {
                Name = name,
                Header = header,
                Info = _info,
                Properties = _props,
                StaticGeometry = _static,
                Brushes = brushes,
                Movers = [.. _movers],
                Groups = [.. _groups],
                MovingGroups = [.. _movingGroups],
                Objects = new RfObjectSet
                {
                    GeoRegions = [.. _geo],
                    Lights = [.. _lights],
                    CutsceneCameras = [.. _cameras],
                    CutscenePathNodes = [.. _pathNodes],
                    AmbientSounds = [.. _sounds],
                    Events = [.. _events],
                    RespawnPoints = [.. _respawns],
                    NavPoints = [.. _nav],
                    Entities = [.. _entities],
                    Items = [.. _items],
                    Clutter = [.. _clutter],
                    Triggers = [.. _triggers],
                    ParticleEmitters = [.. _particles],
                    GasRegions = [.. _gas],
                    Decals = [.. _decals],
                    ClimbingRegions = [.. _climbing],
                    RoomEffects = [.. _roomEffects],
                    EaxEffects = [.. _eax],
                    BoltEmitters = [.. _bolts],
                    Targets = [.. _targets],
                    PushRegions = [.. _push],
                    AlpineMeshes = [.. _meshes],
                    Coronas = [.. _coronas],
                },
                EditorLights = [.. _editorLights],
                Cutscenes = [.. _cutscenes],
                CutscenePaths = [.. _cutscenePaths],
                WaypointLists = [.. _waypoints],
                PlayerStart = _playerStart,
                Preloads = [.. _preloads],
                Lightmaps = [.. _lightmaps],
                AlpineProperties = _alpine,
                DashProperties = _dash,
                OpaqueSections = [.. _opaque],
                Sections = [.. _sections],
                Problems = [.. _problems],
                IsTruncated = _truncated,
            };
        }

        /// <summary>Puts the Alpine level properties' geoable, breakable and no-shadow lists on the brushes they name.</summary>
        private ImmutableArray<RfBrush> ApplyAlpineBrushData(List<RfBrush> brushes)
        {
            if (_alpine is not { } a || (a.GeoableBrushes.IsEmpty && a.BreakableBrushes.IsEmpty && a.NoShadowBrushes.IsEmpty))
                return [.. brushes];
            var data = new Dictionary<int, (RfAlpineBrushFlags Flags, byte Material)>();
            foreach (var g in a.GeoableBrushes) Merge(g.BrushUid, RfAlpineBrushFlags.Geoable, null);
            foreach (var b in a.BreakableBrushes) Merge(b.BrushUid, RfAlpineBrushFlags.Breakable, b.Material);
            foreach (int uid in a.NoShadowBrushes) Merge(uid, RfAlpineBrushFlags.NoShadowCast, null);
            var result = new RfBrush[brushes.Count];
            for (int i = 0; i < result.Length; i++)
            {
                var b = brushes[i];
                result[i] = data.TryGetValue(b.Uid, out var d) ? b with { Alpine = new RfAlpineBrushData(d.Flags, d.Material, []) } : b;
            }
            return [.. result];

            void Merge(int uid, RfAlpineBrushFlags flag, byte? material)
            {
                data.TryGetValue(uid, out var d);
                data[uid] = (d.Flags | flag, material ?? d.Material);
            }
        }
    }
}
